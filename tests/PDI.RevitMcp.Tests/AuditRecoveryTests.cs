// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// AUD-01 / AUD-01B: recuperación del diario de auditoría tras caídas en cada punto del group commit, con lotes de 1, 2, 16 y 64.
    /// Cada "proceso" es una instancia de AuditLog sobre el mismo archivo; una caída simulada (<see cref="AuditCrashException"/>) abandona
    /// la instancia sin confirmar ni limpiar nada, y el bloqueo de archivo se libera como cuando muere un proceso.
    /// Comprobaciones: cadena íntegra, sin cuarentena de datos íntegros, sin duplicados, ninguna confirmación de algo no persistido.
    /// </summary>
    public class AuditRecoveryTests
    {
        private static string NewAudit()
        {
            var root = Path.Combine(Path.GetTempPath(), "pdi-rec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Path.Combine(root, "a.jsonl");
        }

        private static FileStream HoldLock(string audit) => new FileStream(audit + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        private static int Pending(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.json").Length : 0;
        private static int Claims(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.reclamada").Length : 0;
        private static List<Dictionary<string, object>> Lines(string audit) =>
            File.ReadAllLines(audit).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();

        private static void WaitFor(Func<bool> cond, string what, int ms = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("no ocurrió: " + what); Thread.Sleep(5); }
        }

        /// <summary>Hilo dedicado (no del pool): decenas de escritores bloqueantes no deben esperar a que el pool crezca.</summary>
        private static Task<T> OnThread<T>(Func<T> body)
        {
            var tcs = new TaskCompletionSource<T>();
            new Thread(() => { try { tcs.SetResult(body()); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true }.Start();
            return tcs.Task;
        }

        private sealed class Outcome { public string Author; public long Seq; public Exception Error; }

        /// <summary>
        /// n escritores encolados con el bloqueo retenido; al soltarlo, el primero que lo toma es líder del lote completo y "cae" en
        /// <paramref name="point"/> (una sola vez). Los demás siguen solos: esperan, toman el bloqueo y reconcilian.
        /// </summary>
        private static List<Outcome> CrashDuringBatch(string p, int n, string point)
        {
            var fired = 0;
            Func<string, bool> crashOnce = pt => pt == point && Interlocked.CompareExchange(ref fired, 1, 0) == 0;
            var writers = Enumerable.Range(0, n).Select(_ => new AuditLog(p)
            {
                CrashPoint = crashOnce, LockWait = TimeSpan.FromSeconds(8), ClaimWait = TimeSpan.FromMilliseconds(400),
            }).ToList();
            var tasks = new List<Task<Outcome>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < n; i++)
                {
                    var w = writers[i]; var name = "w" + i;
                    tasks.Add(OnThread(() =>
                    {
                        try { return new Outcome { Author = name, Seq = w.Append(name, "op", "ok") }; }
                        catch (Exception ex) { return new Outcome { Author = name, Error = ex }; }
                    }));
                    var expected = i + 1;
                    WaitFor(() => Pending(p) == expected, "cola de " + expected);
                }
            }
            Task.WaitAll(tasks.Cast<Task>().ToArray(), 60000);
            Assert.Equal(1, fired);   // hubo exactamente una caída en el punto pedido
            return tasks.Select(t => t.Result).ToList();
        }

        private static void AssertIntegrity(string p, List<Outcome> outcomes)
        {
            // Una nueva instancia (reinicio) abre sin cuarentena y la cadena es válida.
            var reopened = new AuditLog(p);
            Assert.Empty(reopened.Quarantined);
            reopened.Append("r", "tras_recuperar", "ok");
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(0, v.HeadLag);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p), "*.cuarentena-*"));
            var lines = Lines(p);
            foreach (var o in outcomes)
            {
                var mine = lines.Where(l => Arg.GetString(l, "autor") == o.Author).ToList();
                Assert.True(mine.Count <= 1, "duplicado de " + o.Author);                    // nunca dos veces
                if (o.Error == null)
                {
                    Assert.Single(mine);                                                       // confirmado ⇒ persistido
                    Assert.Equal(o.Seq, Convert.ToInt64(mine[0]["seq"]));                     // con SU seq
                }
            }
            var seqs = lines.Select(l => Convert.ToInt64(l["seq"])).ToList();
            Assert.Equal(Enumerable.Range(1, seqs.Count).Select(x => (long)x), seqs);       // secuencia sin huecos
            Assert.Equal(0, Claims(p));                                                       // ningún reclamo sin resolver
        }

        [Fact]
        public void SingleEntry_CrashAfterFsyncBeforeHead_IsToleratedWithoutQuarantine()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var a = new AuditLog(p) { CrashPoint = pt => pt == "tras_fsync_antes_cabeza" };
            Assert.Throws<AuditCrashException>(() => a.Append("a", "op", "ok"));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(1, v.HeadLag);                                                      // persistido, no confirmado
            AssertIntegrity(p, new List<Outcome>());
        }

        [Theory]
        [InlineData(2)]
        [InlineData(16)]
        [InlineData(64)]
        public void Batch_CrashAfterFsyncBeforeHead_IsSealedAndConfirmedToSurvivors(int n)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, n, "tras_fsync_antes_cabeza");
            Assert.Single(outcomes, o => o.Error is AuditCrashException);                    // el líder "murió"
            // Los supervivientes reciben su seq: su línea es íntegra y durable (sellada por el registro de recuperación).
            Assert.All(outcomes.Where(o => !(o.Error is AuditCrashException)), o => Assert.Null(o.Error));
            Assert.Contains(Lines(p), l => Arg.GetString(l, "op") == "recuperacion_reclamos");
            AssertIntegrity(p, outcomes);
            Assert.Equal(n, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));   // las n entradas, una vez cada una
        }

        [Fact]
        public void Batch_HeadLagOfCompleteBatch_VerifiesOk_AndIsSealedOnReopen()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            // Lote completo persistido sin cabeza y sin que nadie quede vivo para reconciliar: se verifica íntegro y se sella al reabrir.
            var fired = 0;
            var writers = Enumerable.Range(0, 16).Select(_ => new AuditLog(p)
            {
                // Tras la caída del líder, cualquier superviviente que tome el bloqueo muere antes de reconciliar (determinista).
                CrashPoint = pt => pt == "tras_fsync_antes_cabeza" ? Interlocked.Exchange(ref fired, 1) == 0 : (pt == "antes_bloqueo" || pt == "inicio_lider") && Volatile.Read(ref fired) == 1,
                LockWait = TimeSpan.FromSeconds(8), ClaimWait = TimeSpan.FromMilliseconds(200),
            }).ToList();
            var tasks = new List<Task<object>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < 16; i++)
                {
                    var w = writers[i]; var name = "w" + i;
                    tasks.Add(OnThread<object>(() => { try { w.Append(name, "op", "ok"); } catch (Exception) { } return null; }));
                    var expected = i + 1;
                    WaitFor(() => Pending(p) == expected, "cola de " + expected);
                }
            }
            Task.WaitAll(tasks.ToArray(), 60000);   // todos "mueren" antes de volver a tomar el bloqueo
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(16, v.HeadLag);
            var reopened = new AuditLog(p);
            Assert.Empty(reopened.Quarantined);
            Assert.Contains(Lines(p), l => Arg.GetString(l, "op") == "recuperacion_cabeza");
            Assert.Equal(0, AuditLog.Verify(p).HeadLag);
        }

        [Theory]
        [InlineData("tras_reclamo")]
        [InlineData("antes_escribir")]
        public void Batch_CrashBeforeWriting_NothingConfirmed_NothingRewritten(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            Assert.All(outcomes, o => Assert.NotNull(o.Error));                              // nadie confirmado: nada se escribió
            Assert.All(outcomes.Where(o => o.Error is JournalUnavailableException), o => Assert.Contains("no", o.Error.Message));
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor")?.StartsWith("w") == true);
            AssertIntegrity(p, outcomes);
        }

        [Fact]
        public void Batch_CrashDuringWrite_TornTailSetAside_NoFalseConfirmation()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, "durante_escritura");
            Assert.All(outcomes, o => Assert.NotNull(o.Error));                              // el lote quedó incompleto: nadie confirmado
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor")?.StartsWith("w") == true);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(p), "*.fragmento-*")); // lo apartado se conserva como evidencia
            AssertIntegrity(p, outcomes);
        }

        [Theory]
        [InlineData("tras_escribir_antes_fsync")]
        [InlineData("tras_cabeza")]
        public void Batch_CrashAfterWrite_EntriesConfirmedOnceBySurvivors(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            Assert.All(outcomes.Where(o => !(o.Error is AuditCrashException)), o => Assert.Null(o.Error));
            AssertIntegrity(p, outcomes);
            Assert.Equal(16, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));
        }

        [Theory]
        [InlineData("antes_confirmar")]
        [InlineData("tras_confirmar_parcial")]
        public void Batch_CrashWhileConfirming_PersistedOnce_FailuresAreConservativeNotFalse(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            // Todo quedó persistido una vez. Quien no recibió confirmación falla de forma conservadora (reconciliable por su cid en el
            // diario); nadie recibe una confirmación de algo que no esté escrito.
            Assert.Equal(16, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));
            AssertIntegrity(p, outcomes);
        }

        [Fact]
        public void CrashBeforeLock_NothingWritten_NothingLeftBehind()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var a = new AuditLog(p) { CrashPoint = pt => pt == "antes_bloqueo" };
            Assert.Throws<AuditCrashException>(() => a.Append("a", "op", "ok"));
            Assert.Equal(0, Pending(p));
            AssertIntegrity(p, new List<Outcome>());
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor") == "a");
        }

        [Fact]
        public void HeadLag_ThatIsNotACompleteBatch_IsStillDetectedAsTampering()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 4; i++) a.Append("a", "op" + i, "ok");
            // La cabeza vuelve atrás dos registros individuales (no hay lote que lo explique): manipulación, no caída.
            var lines = File.ReadAllLines(p).Where(l => l.Length > 0).ToList();
            Home.WriteAtomic(AuditLog.HeadPath(p), Json.Write(new Dictionary<string, object>
            {
                ["seq"] = 2L, ["ultimo"] = Home.Sha256Hex(lines[1]), ["longitud"] = 0L,
            }));
            var v = AuditLog.Verify(p);
            Assert.False(v.Ok);
            Assert.Contains(v.Errors, e => e.Contains("por detrás"));
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);                                                    // la detección de manipulación se conserva
        }

        [Fact]
        public void EditedConfirmedRecord_IsStillQuarantined()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 3; i++) a.Append("s", "op" + i, "ok");
            File.WriteAllText(p, File.ReadAllText(p).Replace("\"op1\"", "\"opX\""));
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);
            Assert.Empty(b.Repaired);
        }
    }
}
