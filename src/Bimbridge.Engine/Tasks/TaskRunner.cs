// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Tasks
{
    /// <summary>
    /// Engine of a resumable task (§4). A single request starts it and the engine:
    /// 1. reconciles what is doubtful (units with no end, or committed after the last save if the document was reopened);
    /// 2. enumerates the recipe units (stable keys) and skips those already committed: resume never duplicates;
    /// 3. runs batches of units, one per visit to the Revit thread, sized to what was measured (yields the UI between visits);
    /// 4. writes "inicio" BEFORE and "fin" AFTER each batch (durable): the commit→journal window stays "en_curso" and is verified on resume;
    /// 5. respects barriers (save, sync, PDF…): they only run when everything before is committed; they record the persistence evidence;
    /// 6. cooperative cancellation between batches; user pause, Revit busy or document closed = wait with bounded retry;
    /// 7. final QC (postcondition) and deterministic continuation summary.
    /// It does not promise "exactly once" by decree: what cannot be proven stays "desconocida" and asks for explicit reconciliation.
    /// </summary>
    public sealed class TaskRunner
    {
        private readonly TaskStore _store;
        private readonly ITaskBackend _b;
        private readonly TaskRecord _t;
        private readonly UnitLog _units;
        private readonly string _owner;
        private readonly string _instance;
        private readonly Action<TaskRecord> _changed;

        public volatile bool CancelRequested;
        /// <summary>Maximum time waiting for context (pause, dialog…) before leaving the task parked in esperando_contexto.</summary>
        public TimeSpan MaxWait = TimeSpan.FromHours(2);
        public Func<TimeSpan, CancellationToken, Task> Delay = (d, ct) => Task.Delay(d, ct);
        public Func<DateTime> Now = () => DateTime.UtcNow;
        public int BudgetMs = 200;
        public TimeSpan LeaseTtl = TimeSpan.FromSeconds(60);
        /// <summary>Capability authority (R03). By default denies everything that writes: the add-in sets the real one (GrantAuthority).</summary>
        public ITaskAuthority Authority = DenyWritesAuthority.Instance;
        /// <summary>Two-phase audit (R01). null = no audit (engine tests only); the add-in always sets it.</summary>
        public AuditLog Audit;
        /// <summary>Per-document exclusion across processes (R06): leases in a sibling folder of the tasks.</summary>
        public TaskStore DocLeases;

        public TaskRunner(TaskStore store, ITaskBackend backend, TaskRecord task, string owner, string instance, Action<TaskRecord> changed)
        {
            _store = store;
            _b = backend;
            _t = task;
            _owner = owner;
            _instance = instance;
            _changed = changed ?? (_ => { });
            _units = store.LoadUnits(task.Id);
        }

        public TaskRecord Task_ => _t;
        public UnitLog Units => _units;

        private bool StopOnError => !string.Equals(_t.PolicyString("en_error"), "continuar", StringComparison.OrdinalIgnoreCase);
        private int MaxBatch => (int)Math.Max(1, Math.Min(500, _t.PolicyLong("lote_max", 200)));
        private long SaveEvery => _t.PolicyLong("guardar_cada", 0);

        private DateTime _lastLease;

        public async Task RunAsync(CancellationToken ct)
        {
            TaskStore.Lease holder;
            bool got;
            try { got = _store.TryLease(_t.Id, _owner, _instance, LeaseTtl, Now(), out holder); }
            catch (TaskStore.LeaseStoreException ex)
            {
                _t.Reason = "could not take over the task execution (unreliable lease store: " + ex.Message + "); nothing was run";
                _changed(_t);
                return;
            }
            if (!got)
            {
                _t.Reason = "the task is running in another instance (" + holder.Instance + ") until " + holder.Expires;
                _changed(_t);
                return;
            }
            _lastLease = Now();
            _fencing = holder.Fencing;
            // Per-document exclusion: two tasks (even from different processes) do not write to the same document at the same time.
            if (DocLeases != null && _t.MainDoc?.Key != null)
            {
                _docLeaseId = "doc-" + Home.Sha256Hex(_t.MainDoc.Key, 24);
                TaskStore.Lease dh = null;
                bool dok;
                try { dok = DocLeases.TryLease(_docLeaseId, _owner + "|" + _t.Id, _t.Id, LeaseTtl, Now(), out dh); }
                catch (TaskStore.LeaseStoreException ex) { dok = false; dh = new TaskStore.Lease { Instance = "?", Expires = "unreliable store: " + ex.Message }; }
                if (!dok)
                {
                    // Persisted while the TASK lease is still held (nobody else writes tarea.json), then released.
                    _docLeaseId = null;
                    SetState(TaskState.EsperandoContexto, "the document is being modified by task " + dh.Instance + " (until " + dh.Expires + "); nothing was run, resume when it finishes");
                    _store.ReleaseLease(_t.Id, _owner);
                    return;
                }
                _docFencing = dh.Fencing;
            }
            _t.Preflight = Preflight;
            _t.Owner = _instance;
            SetState(TaskState.Ejecutando, null);
            try
            {
                // rc3: authorize BEFORE any recipe code (constructor, Preparar, enumeration, Verificar during reconciliation).
                // The backend repeats the check on the Revit thread through Preflight/RecipeGate in each phase.
                Authorize(null);
                if (!await ReconcileAsync(ct).ConfigureAwait(false)) return;
                var list = await EnumerateAsync(ct).ConfigureAwait(false);
                if (list == null) return;
                if (!await MainLoopAsync(list, ct).ConfigureAwait(false)) return;
                await FinishAsync(ct).ConfigureAwait(false);
            }
            catch (JournalUnavailableException ex)
            {
                SetState(TaskState.Parcial, "the unit log could not be written (" + ex.Message + "): the task stopped so as not to lose count. " +
                                             "What was committed up to the last checkpoint is listed; fix the data folder and resume (what is doubtful will be verified).");
            }
            catch (CheckpointException ex)
            {
                SetState(TaskState.Parcial, "checkpoint not durable (" + ex.Message + "): the task stopped; the state on disk may lag behind the real one. " +
                                             "Fix the data folder and resume: what is doubtful will be verified from unidades.jsonl.");
            }
            catch (NotAuthorizedException ex)
            {
                SetState(TaskState.Interrumpida, "autorizacion_requerida: " + ex.Message + ". Nothing more was run; a person must authorize it in Revit and then resume.");
            }
            catch (AuditUnavailableException ex)
            {
                SetState(TaskState.Parcial, "auditoria_no_disponible: " + ex.Message);
            }
            catch (LeaseLostException ex)
            {
                // In memory only: if another Revit already took the task, its tarea.json is the valid one (the checkpoint in finally decides).
                _t.State = TaskState.Interrumpida;
                _t.Reason = "lease lost (" + ex.Message + "): this instance stopped running so as not to write without authority; what was committed stays in the log";
                _changed(_t);
            }
            catch (OperationCanceledException)
            {
                SetState(TaskState.Cancelada, "cancelled; what was committed before cancelling stays applied (" + Count(UnitState.Confirmada) + " units)");
            }
            catch (Exception ex)
            {
                SetState(TaskState.Interrumpida, "internal engine error (" + ex.GetType().Name + ": " + ex.Message + "); resume: what is doubtful will be verified before continuing");
            }
            finally
            {
                _t.Preflight = null;
                // Save BEFORE releasing the lease and only if nobody else took it: an engine that lost the task does not overwrite the new owner's tarea.json.
                // shortcut: read and save without the lease lock (another Revit could take it in between if the lease already expired); move to a SaveIfOwner under WithLeaseLock if observed.
                var cur = _store.ReadLease(_t.Id);
                if (cur == null || (cur.Owner == _owner && cur.Fencing == _fencing))
                    try { Checkpoint(); } catch (CheckpointException) { }
                if (_docLeaseId != null) DocLeases.ReleaseLease(_docLeaseId, _owner + "|" + _t.Id);
                _store.ReleaseLease(_t.Id, _owner);
            }
        }

        // ---------------- 1) reconciliation ----------------

        private async Task<bool> ReconcileAsync(CancellationToken ct)
        {
            var gen = _b.CurrentGeneration(_t);
            var main = _t.MainDoc;
            var reopened = gen != null && main?.Generation != null && gen != main.Generation;
            var persistSeq = _t.LastPersist?.Seq ?? 0;
            var doubtful = _units.Units.Values.Where(u => UnitState.NeedsReconcile(u.State) ||
                                                          (reopened && u.State == UnitState.Confirmada && u.Seq > persistSeq)).ToList();
            if (doubtful.Count > 0)
            {
                _t.Reason = "verifying " + doubtful.Count + " doubtful units" + (reopened ? " (the document was reopened: what was committed after the last save may have been lost)" : "");
                _changed(_t);
                var r = await WithWaitAsync(() => _b.VerifyAsync(_t, doubtful, reopened, ct), ct).ConfigureAwait(false);
                if (r == null) return false;
                var unknown = new List<string>();
                foreach (var o in r.Value)
                {
                    var state = o.State == UnitState.Confirmada ? UnitState.Confirmada : o.State == UnitState.Pendiente || o.State == UnitState.Revertida ? UnitState.Pendiente : UnitState.Desconocida;
                    _store.AppendUnitEvent(_t.Id, new Dictionary<string, object>
                    {
                        ["ev"] = "verificada", ["clave"] = o.Key, ["estado"] = state, ["error"] = o.Error, ["generacion"] = gen,
                        ["creados"] = o.Created.Count > 0 ? (object)o.Created : null,
                    });
                    _units.Apply(new Dictionary<string, object> { ["ev"] = "verificada", ["clave"] = o.Key, ["estado"] = state, ["error"] = o.Error });
                    if (state == UnitState.Desconocida) unknown.Add(o.Key);
                }
                _t.AddMetric("verificadas", r.Value.Count);
                if (unknown.Count > 0)
                {
                    SetState(TaskState.Interrumpida, unknown.Count + " units could not be verified in the model (" + string.Join(", ", unknown.Take(5)) +
                                                     (unknown.Count > 5 ? "…" : "") + "). Review them and use tarea reconciliar with decision=aplicada or no_aplicada; then resume.");
                    return false;
                }
            }
            if (reopened && main != null)
            {
                main.Generation = gen;
                _t.AddMetric("reaperturas", 1);
            }
            else if (main != null && main.Generation == null) main.Generation = gen;
            return true;
        }

        // ---------------- 2) enumeration ----------------

        private async Task<List<UnitRef>> EnumerateAsync(CancellationToken ct)
        {
            _t.Reason = "enumerating units";
            _changed(_t);
            var r = await WithWaitAsync(() => _b.EnumerateAsync(_t, ct), ct).ConfigureAwait(false);
            if (r == null) return null;
            var list = r.Value ?? new List<UnitRef>();
            var dup = list.GroupBy(u => u.Key).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                SetState(TaskState.Parcial, "the recipe returned two units with the same key '" + dup.Key + "': keys must be unique and stable");
                return null;
            }
            // Final barriers requested in the task (al_final=guardar,pdf …)
            var i = 0;
            foreach (var spec in FinalBarriers())
            {
                var parts = spec.Split(new[] { ' ' }, 2);
                var bargs = new Dictionary<string, object>(StringComparer.Ordinal);
                if (parts.Length > 1)
                    foreach (var kv in parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq > 0) bargs[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                    }
                list.Add(new UnitRef { Key = "final:" + (++i) + ":" + parts[0], Barrier = parts[0], BarrierArgs = bargs });
            }
            for (var k = 0; k < list.Count; k++) list[k].Ordinal = k;
            _t.UnitsTotal = list.Count(u => !u.IsBarrier);
            _t.SetMetric("barreras", list.Count(u => u.IsBarrier));
            return list;
        }

        private IEnumerable<string> FinalBarriers()
        {
            if (!_t.Policy.TryGetValue("al_final", out var v) || v == null) return Enumerable.Empty<string>();
            if (v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).Where(x => !string.IsNullOrWhiteSpace(x));
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Split(';', ',').Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        // ---------------- 3) main loop ----------------

        private bool Done(UnitRef u)
        {
            var r = _units.Get(u.Key);
            return r != null && UnitState.IsDone(r.State);
        }

        private async Task<bool> MainLoopAsync(List<UnitRef> list, CancellationToken ct)
        {
            // On resume, rolled-back and conflicting units are retried (replanned on the current model).
            var pending = list.Where(u => !Done(u)).ToList();
            _t.Reason = null;
            SetState(TaskState.Ejecutando, null);
            var k = 1;
            double avg = -1;
            var emptyVisits = 0;
            long sinceSave = _t.ConfirmSeq - (_t.LastPersist?.Seq ?? 0);
            var i = 0;
            while (i < pending.Count)
            {
                if (CancelRequested) { SetState(TaskState.Cancelada, "cancelled on request; what was committed before (" + Count(UnitState.Confirmada) + " units) stays applied"); return false; }
                if (!await EnsureContextAsync(ct).ConfigureAwait(false)) return false;
                RenewLease();
                var u = pending[i];
                Authorize(u.IsBarrier ? Capability.ForBarrier(u.Barrier) : null);
                if (u.IsBarrier)
                {
                    if (!await RunBarrierAsync(u, list, ct).ConfigureAwait(false)) return false;
                    sinceSave = _t.ConfirmSeq - (_t.LastPersist?.Seq ?? 0);
                    i++;
                    Checkpoint();
                    continue;
                }
                var batch = new List<UnitRef>();
                // With guardar_cada=N the batch does not go past the save point: it saves exactly every N committed units.
                var cap = SaveEvery > 0 ? (int)Math.Max(1, Math.Min(k, SaveEvery - sinceSave)) : k;
                for (var j = i; j < pending.Count && batch.Count < cap && !pending[j].IsBarrier; j++) batch.Add(pending[j]);
                var gen = _b.CurrentGeneration(_t);
                var startEv = new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = batch.Select(x => (object)x.Key).ToList(), ["generacion"] = gen };
                _store.AppendUnitEvent(_t.Id, startEv);
                _units.Apply(startEv);
                var intent = AuditIntent("tarea_lote", new Dictionary<string, object> { ["claves"] = startEv["claves"], ["generacion"] = gen });
                var r = await _b.RunBatchAsync(_t, batch, BudgetMs, ct).ConfigureAwait(false);
                var outcomes = r.Value ?? new List<UnitOutcome>();
                // What the backend did run is ALWAYS recorded, also when it additionally asks to wait or fails.
                long batchMs = 0;
                var stop = (string)null;
                foreach (var o in outcomes)
                {
                    batchMs += o.Ms;
                    Record(o, gen);
                    if (o.State == UnitState.Confirmada) sinceSave++;
                    if (o.State == UnitState.Parcial || o.State == UnitState.Desconocida)
                        stop = "unit " + o.Key + " ended up " + o.State + " (" + o.Error + "): reconcile it before continuing";
                    else if (o.State == UnitState.Revertida && StopOnError)
                        stop = "unit " + o.Key + " failed and was rolled back: " + o.Error + ". Fix the cause and resume (it is retried), or resume with en_error=continuar";
                }
                var notRun = batch.Skip(outcomes.Count).Select(x => (object)x.Key).ToList();
                if (notRun.Count > 0)
                {
                    _store.AppendUnitEvent(_t.Id, new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = notRun });
                    _units.Apply(new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = notRun });
                }
                AuditOutcome("tarea_lote", intent, outcomes);
                _t.AddMetric("lotes", 1);
                _t.AddMetric("ms_revit", batchMs);
                if (outcomes.Count > (int)_t.Metric("lote_max_real")) _t.SetMetric("lote_max_real", outcomes.Count);
                i += outcomes.Count;
                Checkpoint();
                if (stop != null) { SetState(TaskState.Parcial, stop); return false; }
                if (r.Fatal != null) { SetState(TaskState.Parcial, r.Fatal); return false; }
                if (r.Wait != null)
                {
                    if (!await WaitContextAsync(r.Wait, r.Action, ct).ConfigureAwait(false)) return false;
                    continue;
                }
                if (outcomes.Count == 0)
                {
                    if (++emptyVisits >= 3) { SetState(TaskState.Parcial, "Revit did not run any unit in 3 consecutive visits; resume later"); return false; }
                    k = 1;
                    continue;
                }
                emptyVisits = 0;
                // Adaptive batch size: what fits in the budget according to the measured time per unit.
                var per = (double)batchMs / outcomes.Count;
                avg = avg < 0 ? per : avg * 0.7 + per * 0.3;
                k = outcomes.Count < batch.Count ? Math.Max(1, outcomes.Count) : (int)Math.Max(1, Math.Min(MaxBatch, Math.Floor(BudgetMs * 0.8 / Math.Max(0.5, avg))));
                if (SaveEvery > 0 && sinceSave >= SaveEvery && i < pending.Count)
                {
                    var auto = new UnitRef { Key = "auto-guardar:" + _t.ConfirmSeq, Barrier = "guardar", BarrierArgs = new Dictionary<string, object>() };
                    if (!await RunBarrierAsync(auto, list, ct).ConfigureAwait(false)) return false;
                    sinceSave = 0;
                    Checkpoint();
                }
            }
            return true;
        }

        private void Record(UnitOutcome o, string gen)
        {
            long? seq = null;
            if (o.State == UnitState.Confirmada) seq = ++_t.ConfirmSeq;
            var ev = new Dictionary<string, object>
            {
                ["ev"] = "fin", ["clave"] = o.Key, ["estado"] = o.State, ["ms"] = o.Ms, ["seq"] = seq, ["generacion"] = gen,
                ["cambiados"] = o.Changed, ["borrados"] = o.Deleted, ["error"] = o.Error,
                ["creados"] = o.Created.Count > 0 ? (object)o.Created : null,
                ["resultado"] = TrimResult(o.Result),
            };
            _store.AppendUnitEvent(_t.Id, ev);
            _units.Apply(ev);
            lock (_t) foreach (var d in o.Decisions) _t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["unidad"] = o.Key, ["texto"] = d });
            foreach (var x in o.Exceptions) { x["unidad"] = o.Key; _t.Exceptions.Add(x); }
            foreach (var a in o.Artifacts) { a["unidad"] = o.Key; _t.Artifacts.Add(a); }
            if (_t.Decisions.Count > 200) _t.Decisions.RemoveRange(0, _t.Decisions.Count - 200);
        }

        private static object TrimResult(object r)
        {
            if (r == null) return null;
            var txt = Json.Write(r);
            return txt.Length <= 1500 ? r : txt.Substring(0, 1500) + "…";
        }

        private async Task<bool> RunBarrierAsync(UnitRef u, List<UnitRef> list, CancellationToken ct)
        {
            var before = list.Where(x => x.Ordinal < u.Ordinal && !x.IsBarrier && !Done(x)).Select(x => x.Key).ToList();
            if (u.Ordinal == 0 && u.Key.StartsWith("auto-", StringComparison.Ordinal)) before.Clear();
            if (before.Count > 0)
            {
                SetState(TaskState.Parcial, "barrier " + u.Barrier + " does not run because " + before.Count + " previous units are not committed (" +
                                            string.Join(", ", before.Take(5)) + (before.Count > 5 ? "…" : "") + "): saving/exporting now would leave an inconsistent result");
                return false;
            }
            var gen = _b.CurrentGeneration(_t);
            var startEv = new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = new List<object> { u.Key }, ["generacion"] = gen };
            _store.AppendUnitEvent(_t.Id, startEv);
            _units.Apply(startEv);
            _t.Reason = "barrier " + u.Barrier;
            _changed(_t);
            var bIntent = AuditIntent("tarea_barrera_" + u.Barrier, new Dictionary<string, object> { ["clave"] = u.Key, ["generacion"] = gen });
            var r = await WithWaitAsync(() => _b.RunBarrierAsync(_t, u, ct), ct).ConfigureAwait(false);
            if (r == null)
            {
                _store.AppendUnitEvent(_t.Id, new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = new List<object> { u.Key } });
                _units.Apply(new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = new List<object> { u.Key } });
                return false;
            }
            var o = r.Value;
            AuditOutcome("tarea_barrera_" + u.Barrier, bIntent, new List<UnitOutcome> { o });
            // A barrier does not commit units: it does not consume a commit sequence number.
            var ev = new Dictionary<string, object>
            {
                ["ev"] = "fin", ["clave"] = u.Key, ["estado"] = o.State, ["ms"] = o.Ms, ["error"] = o.Error, ["generacion"] = gen, ["resultado"] = TrimResult(o.Result),
            };
            _store.AppendUnitEvent(_t.Id, ev);
            _units.Apply(ev);
            foreach (var a in o.Artifacts) { a["unidad"] = u.Key; _t.Artifacts.Add(a); }
            foreach (var x in o.Exceptions) { x["unidad"] = u.Key; _t.Exceptions.Add(x); }
            _t.AddMetric("barreras_ok", o.State == UnitState.Confirmada ? 1 : 0);
            if (o.State != UnitState.Confirmada)
            {
                SetState(TaskState.Parcial, "barrier " + u.Barrier + " failed: " + o.Error);
                return false;
            }
            if (o.Persist != null)
            {
                o.Persist.Seq = _t.ConfirmSeq;
                o.Persist.Generation = gen;
                _t.NotePersist(o.Persist);
            }
            return true;
        }

        // ---------------- 4) final ----------------

        private async Task FinishAsync(CancellationToken ct)
        {
            _t.Reason = "final QC";
            _changed(_t);
            var r = await WithWaitAsync(() => _b.PostconditionAsync(_t, ct), ct).ConfigureAwait(false);
            if (r == null) return;
            _t.Postcondition = r.Value;
            var deviations = r.Value != null && r.Value.TryGetValue("desviaciones", out var dv) && dv is List<object> dl ? dl.Count : 0;
            var conflicts = Count(UnitState.Conflicto);
            var reverted = Count(UnitState.Revertida);
            var diff = deviations > 0 || conflicts > 0 || reverted > 0 || _t.Exceptions.Count > 0;
            SetState(diff ? TaskState.AplicadaConDiferencias : TaskState.Aplicada,
                diff ? "finished with " + conflicts + " conflicts, " + reverted + " rolled back, " + _t.Exceptions.Count + " exceptions and " + deviations + " QC deviations" : null);
        }

        // ---------------- waits, context, state ----------------

        private async Task<bool> EnsureContextAsync(CancellationToken ct)
        {
            var c = _b.CheckContext(_t);
            if (c.Fatal != null) { SetState(TaskState.Parcial, c.Fatal); return false; }
            if (c.Wait == null) return true;
            return await WaitContextAsync(c.Wait, c.Action, ct).ConfigureAwait(false);
        }

        /// <summary>Waits with spaced retries (1, 2, 4… up to 15 s): does not flood the ExternalEvent or spend agent calls.</summary>
        private async Task<bool> WaitContextAsync(string why, string action, CancellationToken ct)
        {
            var start = Now();
            var delay = TimeSpan.FromSeconds(1);
            SetState(TaskState.EsperandoContexto, why + (action != null ? ". Action: " + action : ""));
            _t.AddMetric("esperas", 1);
            while (true)
            {
                if (CancelRequested) { SetState(TaskState.Cancelada, "cancelled while waiting; what was committed before stays applied (" + Count(UnitState.Confirmada) + " units)"); return false; }
                if (Now() - start > MaxWait)
                {
                    SetState(TaskState.EsperandoContexto, why + ". Stopped waiting after " + (int)MaxWait.TotalMinutes + " min; once resolved, use tarea reanudar");
                    return false;
                }
                await Delay(delay, ct).ConfigureAwait(false);
                RenewLease();
                var c = _b.CheckContext(_t);
                if (c.Fatal != null) { SetState(TaskState.Parcial, c.Fatal); return false; }
                if (c.Wait == null) { SetState(TaskState.Ejecutando, null); return true; }
                if (c.Wait != why) { why = c.Wait; SetState(TaskState.EsperandoContexto, why + (c.Action != null ? ". Action: " + c.Action : "")); }
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
            }
        }

        private async Task<BackendResult<T>> WithWaitAsync<T>(Func<Task<BackendResult<T>>> step, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var r = await step().ConfigureAwait(false);
                if (r.Fatal != null) { SetState(TaskState.Parcial, r.Fatal); return null; }
                if (r.Wait == null) return r;
                if (!await WaitContextAsync(r.Wait, r.Action, ct).ConfigureAwait(false)) return null;
            }
        }

        private long _fencing;
        public long Fencing => _fencing;
        private string _docLeaseId;
        private long _docFencing;
        private string _checkpointError;
        private DateTime _authCacheAt = DateTime.MinValue;
        private string _authCacheCap;
        private string _authCacheResult;

        public sealed class CheckpointException : Exception { public CheckpointException(string m) : base(m) { } }
        public sealed class NotAuthorizedException : Exception { public NotAuthorizedException(string m) : base(m) { } }
        public sealed class AuditUnavailableException : Exception { public AuditUnavailableException(string m) : base(m) { } }

        /// <summary>Capabilities the task needs for its units (not barriers). Without the list (0.6.0 tasks): it is assumed to write.</summary>
        private IEnumerable<string> UnitCapabilities()
        {
            if (_t.Policy.TryGetValue("requiere", out var v) && v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture));
            return new[] { Capability.Modificar };
        }

        /// <summary>R03: checks grants at the point of execution. barrierCap = null → the units' capabilities.</summary>
        private void Authorize(string barrierCap)
        {
            var now = Now();
            foreach (var cap in barrierCap != null ? new[] { barrierCap } : UnitCapabilities())
            {
                var why = (Authority == null ? "no authority configured" : Authority.Check(_t, cap, now));
                if (why != null) throw new NotAuthorizedException(cap + ": " + why);
            }
        }

        /// <summary>
        /// Last safe point (R06): the backend calls it on the Revit thread RIGHT before opening each unit's transaction.
        /// null = go ahead; otherwise the unit is not run (stays sin_ejecutar) and the runner stops at the next point.
        /// Cheap: validating the lease ~0.07 ms; the authority is cached for 2 s (a revocation takes at most 2 s to take effect).
        /// </summary>
        private string Preflight()
        {
            var now = Now();
            if (!_store.ValidateOwnership(_t.Id, _owner, _fencing, now)) return "lease_perdido";
            if (_docLeaseId != null && !DocLeases.ValidateOwnership(_docLeaseId, _owner + "|" + _t.Id, _docFencing, now)) return "documento_sin_exclusion";
            foreach (var cap in UnitCapabilities())
            {
                string why;
                if (_authCacheCap == cap && now - _authCacheAt < TimeSpan.FromSeconds(2)) why = _authCacheResult;
                else { why = (Authority == null ? "no authority configured" : Authority.Check(_t, cap, now)); _authCacheCap = cap; _authCacheAt = now; _authCacheResult = why; }
                if (why != null) return "autorizacion_requerida: " + cap + ": " + why;
            }
            return null;
        }

        private string AuditIntent(string op, Dictionary<string, object> extra)
        {
            if (Audit == null) return null;
            extra["tarea"] = _t.Id;
            extra["fencing"] = _fencing;
            extra["doc"] = _t.MainDoc?.Key;
            try { return Audit.Intent(_owner, op, _t.Id, extra); }
            catch (JournalUnavailableException ex) { throw new AuditUnavailableException("could not record the intent before " + op + " (" + ex.Message + "): nothing was modified"); }
        }

        private void AuditOutcome(string op, string intent, List<UnitOutcome> outs)
        {
            if (Audit == null || intent == null) return;
            var extra = new Dictionary<string, object>
            {
                ["tarea"] = _t.Id,
                ["resultado"] = outs.Where(o => o != null).GroupBy(o => o.State).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
            };
            try { Audit.Outcome(_owner, op, intent, "resultado", extra); }
            catch (JournalUnavailableException ex)
            {
                // What ran is already in unidades.jsonl; the intent without an outcome appears in AuditLog.PendingIntents for reconciliation.
                throw new AuditUnavailableException("the operation finished and is in the unit log, but its outcome did not reach the audit (" + ex.Message +
                                                    "); intent " + intent + " pending reconciliation. The task stopped.");
            }
        }

        public sealed class LeaseLostException : Exception { public LeaseLostException(string m) : base(m) { } }

        /// <summary>Authority point before each batch/barrier: validates owner+fencing and renews when due. Failure → LeaseLostException (nothing is written).</summary>
        private void RenewLease()
        {
            if (_checkpointError != null) throw new CheckpointException(_checkpointError);
            var now = Now();
            if (now - _lastLease >= TimeSpan.FromSeconds(LeaseTtl.TotalSeconds / 3))
            {
                bool ok;
                try { ok = _store.RenewLease(_t.Id, _owner, _fencing, LeaseTtl, now); }
                catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("renewal impossible: " + ex.Message); }
                if (!ok) throw new LeaseLostException("another instance took the task or the lease expired (fencing " + _fencing + ")");
                if (_docLeaseId != null)
                {
                    bool dok;
                    try { dok = DocLeases.RenewLease(_docLeaseId, _owner + "|" + _t.Id, _docFencing, LeaseTtl, now); }
                    catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("document exclusion: " + ex.Message); }
                    if (!dok) throw new LeaseLostException("document exclusion was lost (fencing " + _docFencing + ")");
                }
                _lastLease = now;
                return;
            }
            if (!_store.ValidateOwnership(_t.Id, _owner, _fencing, now))
                throw new LeaseLostException("ownership validation failed (fencing " + _fencing + ")");
            if (_docLeaseId != null && !DocLeases.ValidateOwnership(_docLeaseId, _owner + "|" + _t.Id, _docFencing, now))
                throw new LeaseLostException("document exclusion was lost (fencing " + _docFencing + ")");
        }

        private long Count(string state) => _units.Units.Values.LongCount(u => u.State == state);

        private void SetState(string state, string reason)
        {
            _t.State = state;
            _t.Reason = reason;
            // A failure here is already flagged (Durable=false, _checkpointError); the next authority point stops the task.
            try { Checkpoint(); } catch (CheckpointException) { }
        }

        private void Checkpoint()
        {
            _t.Rev++;
            _t.AddMetric("checkpoints", 1);
            try
            {
                lock (_t) { _t.Summary = TaskSummary.Continuation(_t, _units); _store.Save(_t); }
                _checkpointError = null;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                // R07: what is only in memory is never declared durable. It is flagged, reported, and the next authority point stops the task.
                _checkpointError = ex.Message;
                _t.Durable = false;
                _t.Reason = (_t.Reason == null ? "" : _t.Reason + " | ") + "checkpoint NOT saved: " + ex.Message;
                _changed(_t);
                throw new CheckpointException(ex.Message);
            }
            _t.Durable = true;
            _changed(_t);
        }
    }
}
