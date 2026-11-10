// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Issue #27: group commit de AuditLog entre escritores. Pruebas deterministas en un proceso: el bloqueo real (auditoria.lock, FileShare.None)
    /// lo retiene la prueba para fijar el orden; cada AuditLog es un escritor independiente sobre el mismo archivo, como un proceso de Revit.
    /// Garantías comprobadas: ninguna confirmación antes de persistir, ninguna pérdida, ningún duplicado, orden de encolado, fallo cerrado.
    /// </summary>
    public class AuditGroupCommitTests
    {
        private static string NewAudit()
        {
            var root = Path.Combine(Path.GetTempPath(), "pdi-gc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Path.Combine(root, "a.jsonl");
        }

        private static FileStream HoldLock(string audit) => new FileStream(audit + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        private static string[] Pending(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.json") : new string[0];

        private static void WaitFor(Func<bool> cond, string what, int ms = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("no ocurrió: " + what); Thread.Sleep(5); }
        }

        private static List<Dictionary<string, object>> Lines(string audit) =>
            File.ReadAllLines(audit).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();

        [Fact]
        public void Contended_EntryWaits_IsNotConfirmedBeforePersisted_ThenWrittenOnce()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p);
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entrada en cola");
                Thread.Sleep(150);
                Assert.False(t.IsCompleted);                 // sin bloqueo no hay escritura: tampoco confirmación
                Assert.Single(Lines(p));
            }
            Assert.Equal(2, t.Result);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(2, v.Lines);
            var last = Lines(p).Last();
            Assert.Equal("b", Arg.GetString(last, "autor"));
            Assert.False(string.IsNullOrEmpty(Arg.GetString(last, "cid")));   // entrada que pasó por la cola: identificable
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void Leader_DrainsAllWaiting_InEnqueueOrder_NoLossNoDuplicates()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var writers = Enumerable.Range(1, 4).Select(_ => new AuditLog(p)).ToList();
            var tasks = new List<Task<long>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < writers.Count; i++)
                {
                    var w = writers[i]; var name = "w" + (i + 1);
                    tasks.Add(Task.Run(() => w.Append(name, "op", "ok")));
                    var expected = i + 1;
                    WaitFor(() => Pending(p).Length == expected, "cola de " + expected);   // orden de encolado fijado por la prueba
                }
                Assert.All(tasks, x => Assert.False(x.IsCompleted));
            }
            Task.WaitAll(tasks.Cast<Task>().ToArray(), 20000);
            var seqs = tasks.Select(x => x.Result).ToList();
            Assert.Equal(new long[] { 2, 3, 4, 5 }, seqs.OrderBy(x => x).ToArray());
            Assert.Equal(seqs.Count, seqs.Distinct().Count());
            var lines = Lines(p);
            Assert.Equal(new[] { "s", "w1", "w2", "w3", "w4" }, lines.Select(l => Arg.GetString(l, "autor")).ToArray());
            for (var i = 0; i < 4; i++) Assert.Equal(seqs[i], Convert.ToInt64(lines[i + 1]["seq"]));   // cada escritor recibe SU seq
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void Timeout_UnclaimedEntry_IsWithdrawn_NothingWritten_FailsClosed()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { LockWait = TimeSpan.FromMilliseconds(300) };
            using (HoldLock(p))
            {
                var ex = Assert.Throws<JournalUnavailableException>(() => b.Append("b", "op", "ok"));
                Assert.Contains("no se escribió nada", ex.Message);
            }
            Assert.Equal(1, b.Failures);
            Assert.Empty(Pending(p));                          // la entrada retirada no puede escribirla otro líder después
            Assert.Single(Lines(p));
            Assert.Equal(2, b.Append("b", "op2", "ok"));       // el escritor sigue utilizable
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void ClaimedByCrashedLeader_FailsClosed_AndIsNeverRewritten()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { LockWait = TimeSpan.FromMilliseconds(400), ClaimWait = TimeSpan.FromMilliseconds(300) };
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "intencion_x", "intencion"));
                WaitFor(() => Pending(p).Length == 1, "entrada en cola");
                // Un líder la reclama (misma primitiva: apertura exclusiva que borra al cerrar) y "cae" antes de escribir y confirmar.
                using (new FileStream(Pending(p)[0], FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose)) { }
                var ex = Assert.Throws<AggregateException>(() => t.Wait(10000));
                Assert.IsType<JournalUnavailableException>(ex.InnerException);
                Assert.Contains("reclamada", ex.InnerException.Message);
            }
            Assert.Single(Lines(p));                           // el seguidor no la reescribe: sin duplicados
            b.Append("b", "despues", "ok");
            Assert.Equal(2, AuditLog.Verify(p).Lines);
        }

        [Fact]
        public void LeaderWriteFailure_GivesNoConfirmation_ChainStaysValid()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { FaultBeforeWrite = _ => throw new IOException("disco lleno") };
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entrada en cola");
            }
            var ex = Assert.Throws<AggregateException>(() => t.Wait(10000));
            Assert.IsType<JournalUnavailableException>(ex.InnerException);
            Assert.Equal(1, b.Failures);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(1, v.Lines);
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void LeaderWriteFailure_PropagatesErrorToFollowers_NoFalseConfirmation()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var follower = new AuditLog(p);
            var leader = new AuditLog(p) { FaultBeforeWrite = _ => throw new IOException("disco lleno") };
            Task<long> tf, tl;
            using (HoldLock(p))
            {
                tl = Task.Run(() => leader.Append("lider", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "líder en cola");
                tf = Task.Run(() => follower.Append("seguidor", "op", "ok"));
                WaitFor(() => Pending(p).Length == 2, "seguidor en cola");
            }
            // Quien tome el bloqueo primero escribe el lote. Si es el líder con fallo, ambos reciben error; si es el seguidor, éste escribe ambas
            // entradas y el líder con fallo sólo falla si le toca escribir. En ningún caso hay confirmación sin línea persistida.
            var results = new[] { tl, tf }.Select(x => { try { return (ok: true, seq: x.Result); } catch (AggregateException) { return (ok: false, seq: 0L); } }).ToList();
            var lines = Lines(p);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            foreach (var r in results.Where(r => r.ok)) Assert.Contains(lines, l => Convert.ToInt64(l["seq"]) == r.seq);
            Assert.Equal(1 + results.Count(r => r.ok), lines.Count);   // cada confirmación tiene exactamente una línea, y no hay líneas sin dueño confirmado o fallido
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void OrphanEntry_FromDeadWriter_IsPersistedByNextLeader_AndStaysReconcilable()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            // Un escritor dejó su entrada en la cola y murió antes de recibir confirmación (reinicio de Revit, proceso terminado).
            Directory.CreateDirectory(p + ".pend");
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(p + ".pend", DateTime.UtcNow.Ticks.ToString("D19") + "-" + id + ".json"), Json.Write(new Dictionary<string, object>
            {
                ["id"] = id, ["autor"] = "muerto", ["op"] = "exec", ["estado"] = "intencion", ["pid"] = 1L, ["t"] = "2026-11-10T00:00:00Z",
                ["extra"] = new Dictionary<string, object> { ["intencion"] = "abc123", ["objetivo"] = "doc" },
            }), new UTF8Encoding(false));
            var c = new AuditLog(p);
            Assert.Equal(3, c.Append("c", "op", "ok"));       // el líder siguiente escribe primero la entrada huérfana, luego la suya
            var lines = Lines(p);
            Assert.Equal(new[] { "s", "muerto", "c" }, lines.Select(l => Arg.GetString(l, "autor")).ToArray());
            Assert.Equal(id, Arg.GetString(lines[1], "cid"));
            Assert.True(AuditLog.Verify(p).Ok);
            // La intención huérfana no tiene resultado: queda pendiente de reconciliar, nunca como completada.
            Assert.Contains(AuditLog.PendingIntents(p), x => Arg.GetString(x, "intencion") == "abc123");
        }

        [Fact]
        public void Restart_AfterQueuedWork_NewInstancesContinueTheSameChain()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 5; i++) a.Append("a", "op" + i, "ok");
            Task<long> t;
            var b = new AuditLog(p);                           // la apertura también toma el bloqueo: se crea antes de retenerlo
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entrada en cola");
                Assert.False(t.IsCompleted);
            }
            Assert.Equal(6, t.Result);
            var reopened = new AuditLog(p);                    // "reinicio": instancia nueva relee toda la cadena
            Assert.Equal(7, reopened.Append("r", "op", "ok"));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(7, v.Lines);
        }
    }
}
