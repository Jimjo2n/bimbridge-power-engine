// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Motor de tareas con un modelo FALSO (no es Revit): prueba orden, lotes, checkpoints, reanudación, barreras, cancelación y la
    /// ventana commit→diario con inyección de fallos. La aceptación dentro de Revit está en tools/aceptacion (pendiente de ejecutar).
    /// </summary>
    public class TaskEngineTests
    {
        /// <summary>Modelo falso: lo "aplicado" en memoria de Revit, lo guardado en el archivo, generación de apertura y fallos inyectados.</summary>
        public sealed class FakeModel
        {
            public readonly object Lock = new object();
            public Dictionary<string, string> Live = new Dictionary<string, string>();
            public Dictionary<string, string> Saved = new Dictionary<string, string>();
            public int Generation = 1;
            public int Duplicates;
            public int Visits;
            public int MsPerUnit = 2;
            public volatile bool Paused;
            public volatile bool DocClosed;
            public string CrashAfterCommitAt;
            public HashSet<string> FailUnits = new HashSet<string>();
            public HashSet<string> ConflictUnits = new HashSet<string>();
            public HashSet<string> Unverifiable = new HashSet<string>();
            public int Barriers;
            public Action<int> OnVisit;
            public int Count;
            public List<string> PreflightRefusals = new List<string>();

            /// <summary>Revit se cae y se vuelve a abrir el archivo: se pierde lo no guardado y cambia la generación.</summary>
            public void RestartRevit()
            {
                lock (Lock) { Live = new Dictionary<string, string>(Saved); Generation++; }
            }
        }

        public sealed class CrashException : Exception { public CrashException() : base("Revit murió (simulado)") { } }

        public sealed class FakeBackend : ITaskBackend
        {
            private readonly FakeModel _m;
            public FakeBackend(FakeModel m) { _m = m; }

            public BackendResult<bool> CheckContext(TaskRecord task)
            {
                if (_m.Paused) return BackendResult<bool>.Waiting("el usuario pausó la IA", "pulsar Reanudar IA en Revit");
                if (_m.DocClosed) return BackendResult<bool>.Waiting("el documento está cerrado", "abrir el documento");
                return BackendResult<bool>.Ok(true);
            }

            public Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, CancellationToken ct)
            {
                var n = (int)(PDI.RevitMcp.Protocol.Arg.GetLong(task.Args, "n") ?? 10);
                var list = Enumerable.Range(1, n).Select(i => new UnitRef { Key = "u" + i.ToString("D4") }).ToList();
                var barrierAt = PDI.RevitMcp.Protocol.Arg.GetLong(task.Args, "barrera_en");
                if (barrierAt.HasValue) list.Insert((int)barrierAt.Value, new UnitRef { Key = "pdf-intermedio", Barrier = "pdf", BarrierArgs = new Dictionary<string, object>() });
                return Task.FromResult(BackendResult<List<UnitRef>>.Ok(list));
            }

            public Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, CancellationToken ct)
            {
                var outs = new List<UnitOutcome>();
                lock (_m.Lock)
                {
                    _m.Visits++;
                    _m.OnVisit?.Invoke(_m.Visits);
                    if (_m.Paused) return Task.FromResult(BackendResult<List<UnitOutcome>>.Waiting("el usuario pausó la IA"));
                    long spent = 0;
                    foreach (var u in units)
                    {
                        if (spent + _m.MsPerUnit > budgetMs && outs.Count > 0) break; // presupuesto de la visita
                        if (task.Preflight?.Invoke() is string pf) { _m.PreflightRefusals.Add(pf); break; } // último punto seguro (R06)
                        spent += _m.MsPerUnit;
                        if (_m.ConflictUnits.Contains(u.Key)) { outs.Add(new UnitOutcome { Key = u.Key, State = UnitState.Conflicto, Error = "otro usuario cambió el elemento", Ms = _m.MsPerUnit }); continue; }
                        if (_m.FailUnits.Contains(u.Key)) { outs.Add(new UnitOutcome { Key = u.Key, State = UnitState.Revertida, Error = "Revit rechazó el cambio", Ms = _m.MsPerUnit }); continue; }
                        if (_m.Live.ContainsKey(u.Key)) _m.Duplicates++;
                        var uid = "uid-" + u.Key + "-" + (++_m.Count);
                        _m.Live[u.Key] = uid;
                        if (_m.CrashAfterCommitAt == u.Key) { _m.CrashAfterCommitAt = null; throw new CrashException(); }
                        var o = new UnitOutcome { Key = u.Key, State = UnitState.Confirmada, Ms = _m.MsPerUnit, Changed = 1 };
                        o.Created.Add(new object[] { (long)_m.Count, uid });
                        outs.Add(o);
                    }
                }
                return Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(outs));
            }

            public Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    _m.Barriers++;
                    var o = new UnitOutcome { Key = barrier.Key, State = UnitState.Confirmada, Ms = 50 };
                    if (barrier.Barrier == "guardar")
                    {
                        _m.Saved = new Dictionary<string, string>(_m.Live);
                        o.Persist = new PersistEvidence { Kind = "guardar", When = DateTime.UtcNow.ToString("o"), File = "practica.rvt", Bytes = _m.Saved.Count };
                    }
                    if (barrier.Barrier == "pdf")
                        o.Artifacts.Add(new Dictionary<string, object> { ["ruta"] = "salida.pdf", ["sha256"] = "abc", ["unidades_en_modelo"] = (long)_m.Live.Count });
                    return Task.FromResult(BackendResult<UnitOutcome>.Ok(o));
                }
            }

            public Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    var outs = units.Select(u => _m.Unverifiable.Contains(u.Key) ? UnitOutcome.Of(u.Key, UnitState.Desconocida, "no se puede comprobar")
                        : _m.Live.ContainsKey(u.Key) ? UnitOutcome.Of(u.Key, UnitState.Confirmada) : UnitOutcome.Of(u.Key, UnitState.Pendiente, "no está en el modelo")).ToList();
                    return Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(outs));
                }
            }

            public Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    var n = (int)(PDI.RevitMcp.Protocol.Arg.GetLong(task.Args, "n") ?? 10);
                    var missing = Enumerable.Range(1, n).Select(i => "u" + i.ToString("D4")).Where(k => !_m.Live.ContainsKey(k)).Cast<object>().ToList();
                    return Task.FromResult(BackendResult<Dictionary<string, object>>.Ok(new Dictionary<string, object>
                    {
                        ["en_modelo"] = (long)_m.Live.Count, ["desviaciones"] = missing,
                    }));
                }
            }

            public string CurrentGeneration(TaskRecord task) { lock (_m.Lock) return _m.DocClosed ? null : "inst#" + _m.Generation; }
        }

        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "pdi-tareas-" + Guid.NewGuid().ToString("N"));

        public sealed class AllowAll : ITaskAuthority
        {
            public string Check(TaskRecord task, string capability, DateTime now) => null;
        }

        private static TaskManager Manager(string root, FakeModel m, string instance = "A") => new TaskManager(new TaskStore(root), instance, t => new FakeBackend(m))
        {
            Authority = new AllowAll(),
            Delay = (d, ct) => Task.Delay(5, ct),
            MaxWait = TimeSpan.FromSeconds(20),
            BudgetMs = 200,
        };

        private static TaskRecord Draft(string id, int n, Dictionary<string, object> policy = null, Dictionary<string, object> extra = null)
        {
            var args = new Dictionary<string, object> { ["n"] = (long)n };
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value;
            return new TaskRecord
            {
                Id = id, Objective = "prueba", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c-" + n,
                Args = args, Docs = new List<TaskDoc> { new TaskDoc { Key = "archivo:c:\\practica.rvt", Title = "practica", Generation = "inst#1" } },
                Policy = policy ?? new Dictionary<string, object>(),
            };
        }

        private static async Task<TaskRecord> RunToEnd(TaskManager mgr, string id)
        {
            await mgr.Completion(id);
            return mgr.Get(id, out _);
        }

        /// <summary>INV-001/002 (0.6.1): si otra instancia toma la tarea (fencing nuevo), el ejecutor anterior no ejecuta ni un lote más.</summary>
        [Fact]
        public async Task LeaseStolen_MidRun_OldRunnerStopsBeforeNextBatch()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 50 };
            var thief = new TaskStore(root);
            int visitsAtSteal = -1;
            m.OnVisit = v =>
            {
                if (v == 3)
                {
                    Assert.True(thief.TryLease("robo", "OTRA-INSTANCIA", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out var l));
                    Assert.True(l.Fencing >= 2);
                    visitsAtSteal = v;
                }
            };
            var mgr = Manager(root, m);
            mgr.Start(Draft("robo", 200), "// fake", "prueba");
            var t = await RunToEnd(mgr, "robo");
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("lease perdido", t.Reason);
            Assert.Equal(3, m.Visits); // ningún lote después del robo
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task ThousandUnits_RunInManyVisits_WithAdaptiveBatches_NoDuplicates()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("t1000", 1000), "receta", "claude");
            var t = await RunToEnd(mgr, "t1000");
            mgr.Get("t1000", out var units);
            Assert.True(t.State == TaskState.Aplicada, t.Reason);
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(m.Visits > 5, "debe ceder el hilo entre visitas: " + m.Visits);
            Assert.True(m.Visits < 200, "el lote debe crecer con la medición: " + m.Visits);
            Assert.True(t.Metric("checkpoints") >= m.Visits);
            Assert.Equal(1000, units.Units.Values.Count(u => u.State == UnitState.Confirmada));
            Assert.Contains("1000/1000", t.Summary);
        }

        /// <summary>
        /// Medición (además de prueba): cuánto texto ve la IA para seguir y recuperar una tarea de 1000 unidades, con fallos y excepciones.
        /// Es la salida real de TaskSummary sobre el motor real con el modelo FALSO; no mide Revit. PDI_BENCH_OUT=archivo la guarda.
        /// </summary>
        [Fact]
        public async Task Bench_CompactSummary_StaysSmall_ForThousandUnits()
        {
            var m = new FakeModel();
            for (var i = 0; i < 1000; i += 97) m.FailUnits.Add("u" + i.ToString("D4"));
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("tb", 1000, new Dictionary<string, object> { ["en_error"] = "continuar" }), "receta", "claude");
            var t = await RunToEnd(mgr, "tb");
            mgr.Get("tb", out var units);
            var status = PDI.RevitMcp.Protocol.Json.Write(TaskSummary.Status(t, units, true));
            var compact = CompactText.Write(TaskSummary.Status(t, units, true));
            // Otro cliente: abre el almacén desde cero (como tras perder el chat) y pide el resumen.
            var other = Manager(root, m);
            var t2 = other.Get("tb", out var units2);
            var recovered = CompactText.Write(TaskSummary.Status(t2, units2, true));
            Assert.True(units.Units.Count == units2.Units.Count, "otro cliente ve " + units2.Units.Count + " unidades y el primero " + units.Units.Count + " estado " + t.State + " " + t.Summary);
            Assert.True(compact.Length < 3000, "resumen compacto demasiado grande: " + compact.Length);
            Assert.Equal(compact, recovered);
            var outFile = Environment.GetEnvironmentVariable("PDI_BENCH_OUT");
            if (!string.IsNullOrEmpty(outFile))
                File.WriteAllText(outFile, PDI.RevitMcp.Protocol.Json.Write(new Dictionary<string, object>
                {
                    ["unidades"] = 1000L, ["fallidas"] = (long)m.FailUnits.Count, ["estado"] = t.State.ToString(),
                    ["resumen_json_chars"] = (long)status.Length, ["resumen_compacto_chars"] = (long)compact.Length,
                    ["recuperado_por_otro_cliente_chars"] = (long)recovered.Length, ["resumen_compacto"] = compact,
                }));
        }

        [Fact]
        public async Task CrashBetweenCommitAndJournal_Resume_VerifiesAndDoesNotDuplicate()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0500" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("crash", 1000), "receta", "claude");
            var t = await RunToEnd(mgr, "crash");
            mgr.Get("crash", out var units);
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Equal(UnitState.EnCurso, units.Get("u0500").State); // el commit ocurrió, el "fin" no se escribió

            // Otro proceso / otro cliente (Codex) retoma por id, sin la sesión anterior.
            var mgr2 = Manager(root, m, "B");
            var r = mgr2.Resume("crash", "codex", null);
            Assert.Null(r.Error);
            t = await RunToEnd(mgr2, "crash");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(t.Metric("verificadas") >= 1);
            Assert.Contains("codex", t.Clients);
        }

        [Fact]
        public async Task RevitRestartBeforeSave_LostUnitsAreReexecuted_SavedOnesAreNot()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0450" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("reinicio", 1000, new Dictionary<string, object> { ["guardar_cada"] = 300L }), "receta", "claude");
            var t = await RunToEnd(mgr, "reinicio");
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.NotNull(t.LastPersist);
            var savedSeq = t.LastPersist.Seq;
            Assert.True(savedSeq >= 300 && savedSeq < 450);
            // Revit se cierra sin guardar: el archivo sólo tiene lo guardado por la barrera.
            m.RestartRevit();
            Assert.Equal((int)savedSeq, m.Live.Count);
            var mgr2 = Manager(root, m, "B");
            mgr2.Resume("reinicio", "claude", null);
            t = await RunToEnd(mgr2, "reinicio");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.Equal(1L, t.Metric("reaperturas"));
            Assert.True(t.Metric("verificadas") >= 450 - savedSeq);
        }

        [Fact]
        public async Task RevitRestartRightAfterSave_NothingIsLost_NothingRepeated()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0301" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("trasguardar", 600, new Dictionary<string, object> { ["guardar_cada"] = 300L }), "receta", "claude");
            await RunToEnd(mgr, "trasguardar");
            m.RestartRevit();
            Assert.Equal(300, m.Live.Count);
            var mgr2 = Manager(root, m, "B");
            mgr2.Resume("trasguardar", "claude", null);
            var t = await RunToEnd(mgr2, "trasguardar");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(600, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task Cancel_IsCooperative_AndConfirmedWorkIsReported()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            m.OnVisit = v => { if (v == 4) mgr.Cancel("cancel"); };
            mgr.MaxWait = TimeSpan.FromSeconds(5);
            var draft = Draft("cancel", 1000, new Dictionary<string, object> { ["lote_max"] = 20L });
            mgr.Start(draft, "receta", "claude");
            var t = await RunToEnd(mgr, "cancel");
            mgr.Get("cancel", out var units);
            Assert.Equal(TaskState.Cancelada, t.State);
            var confirmed = units.Units.Values.Count(u => u.State == UnitState.Confirmada);
            Assert.True(confirmed > 0 && confirmed < 1000);
            Assert.Equal(confirmed, m.Live.Count); // lo confirmado es exactamente lo que está en el modelo
            Assert.Contains("sigue aplicado", t.Reason);
        }

        [Fact]
        public async Task ExportBarrier_DoesNotRunWhileEarlierUnitsAreNotConfirmed()
        {
            var m = new FakeModel { FailUnits = new HashSet<string> { "u0003" } };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("barrera", 10, new Dictionary<string, object> { ["en_error"] = "continuar" }, new Dictionary<string, object> { ["barrera_en"] = 6L }), "receta", "claude");
            var t = await RunToEnd(mgr, "barrera");
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.Contains("barrera pdf no se ejecuta", t.Reason);
            Assert.Equal(0, m.Barriers);
            // Corregido el fallo, reanudar reintenta la revertida y ahora sí exporta, con artefacto registrado.
            m.FailUnits.Clear();
            mgr.Resume("barrera", "claude", null);
            t = await RunToEnd(mgr, "barrera");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(1, m.Barriers);
            Assert.Single(t.Artifacts);
            Assert.Equal(6L, t.Artifacts[0]["unidades_en_modelo"]);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task PauseAndClosedDocument_WaitWithoutLosingProgress()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            m.OnVisit = v =>
            {
                if (v == 3) { m.Paused = true; Task.Run(async () => { await Task.Delay(150); m.Paused = false; m.DocClosed = true; await Task.Delay(150); m.DocClosed = false; }); }
            };
            mgr.Start(Draft("pausa", 300, new Dictionary<string, object> { ["lote_max"] = 10L }), "receta", "claude");
            var t = await RunToEnd(mgr, "pausa");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(300, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(t.Metric("esperas") >= 1);
        }

        [Fact]
        public async Task ConflictsAreReported_NotOverwritten()
        {
            var m = new FakeModel { ConflictUnits = new HashSet<string> { "u0002", "u0007" } };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("conflictos", 10), "receta", "claude");
            var t = await RunToEnd(mgr, "conflictos");
            Assert.Equal(TaskState.AplicadaConDiferencias, t.State);
            Assert.Equal(8, m.Live.Count);
            var status = TaskSummary.Status(t, mgr.Store.LoadUnits("conflictos"), true);
            Assert.Equal(2, ((List<object>)status["desviaciones"]).Count);
        }

        [Fact]
        public async Task UnverifiableUnits_RequireExplicitReconcile()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0005", Unverifiable = new HashSet<string> { "u0005" } };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("reconc", 10), "receta", "claude");
            await RunToEnd(mgr, "reconc");
            mgr.Resume("reconc", "claude", null);
            var t = await RunToEnd(mgr, "reconc");
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("reconciliar", t.Reason);
            Assert.Null(mgr.Reconcile("reconc", new List<string> { "u0005" }, applied: true, note: "comprobado en el modelo", who: "claude"));
            mgr.Resume("reconc", "claude", null);
            t = await RunToEnd(mgr, "reconc");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(0, m.Duplicates);
            Assert.Contains(t.Decisions, d => ((string)d["texto"]).Contains("reconciliadas 1"));
        }

        [Fact]
        public async Task SameTaskIdTwice_DoesNotStartASecondRunner_AndDifferentContentIsRejected()
        {
            var m = new FakeModel();
            var gate = new ManualResetEventSlim(false);
            m.OnVisit = v => { if (v == 1) gate.Wait(2000); };
            var mgr = Manager(TempRoot(), m);
            var a = mgr.Start(Draft("dos", 50), "receta", "claude");
            var b = mgr.Start(Draft("dos", 50), "receta", "codex");
            Assert.True(a.Created);
            Assert.True(b.AlreadyRunning);
            var c = mgr.Start(Draft("dos", 51), "receta", "codex");
            Assert.NotNull(c.Error);
            gate.Set();
            var t = await RunToEnd(mgr, "dos");
            Assert.Equal(50, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task UnitJournalUnwritable_StopsVisibly()
        {
            var m = new FakeModel();
            var root = TempRoot();
            var mgr = Manager(root, m);
            m.OnVisit = v =>
            {
                if (v != 2) return;
                // El registro de unidades se vuelve imposible de escribir (disco, permisos…): se sustituye por una carpeta.
                var p = Path.Combine(root, "roto", "unidades.jsonl");
                File.Delete(p);
                Directory.CreateDirectory(p);
            };
            mgr.Start(Draft("roto", 100, new Dictionary<string, object> { ["lote_max"] = 5L }), "receta", "claude");
            var t = await RunToEnd(mgr, "roto");
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.Contains("registro de unidades no se pudo escribir", t.Reason);
        }

        [Fact]
        public async Task WaitReturnsOnRevisionChange_WithoutPolling()
        {
            var m = new FakeModel();
            var gate = new ManualResetEventSlim(false);
            m.OnVisit = v => { if (v == 1) gate.Wait(3000); };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("espera", 20), "receta", "claude");
            var rev0 = mgr.Get("espera", out _).Rev;
            var waiting = mgr.WaitAsync("espera", rev0, TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            gate.Set();
            var t = await waiting;
            Assert.True(t.Rev > rev0);
            await mgr.Completion("espera");
        }

        [Fact]
        public void DocLocks_AllOrNothing_Reentrant_NoDeadlock()
        {
            var l = new DocLocks();
            Assert.True(l.TryAcquire(new[] { "b", "a" }, "tarea:1", "tarea", "1", out _));
            Assert.True(l.TryAcquire(new[] { "a" }, "tarea:1", "tarea", "1", out _)); // reentrante
            Assert.False(l.TryAcquire(new[] { "c", "a" }, "otro", "barrera", null, out var h));
            Assert.Equal("tarea:1", h.Owner);
            Assert.Null(l.HeldByOther("c", "x")); // no tomó "c" a medias
            l.Release(new[] { "a" }, "tarea:1");
            Assert.NotNull(l.HeldByOther("a", "otro")); // sigue tomado (profundidad 2 → 1)
            l.Release(new[] { "a", "b" }, "tarea:1");
            Assert.True(l.TryAcquire(new[] { "a", "b", "c" }, "otro", "barrera", null, out _));
        }
    }
}
