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
    /// Tasks of this Revit: creates, launches, resumes, cancels, waits for changes and reconciles. A single engine per task in the process: if Codex and
    /// Claude ask to "resume" at the same time, the second gets the state of the one already running. Across processes (two Revits) the lease on disk decides.
    /// Any client retrieves the task by its id, without the session that created it.
    /// </summary>
    public sealed class TaskManager
    {
        private readonly TaskStore _store;
        private readonly string _instance;
        private readonly Func<TaskRecord, ITaskBackend> _backend;
        private readonly object _lock = new object();
        private readonly Dictionary<string, TaskRunner> _running = new Dictionary<string, TaskRunner>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task> _work = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _signals = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>For tests: fast waits.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay;
        public TimeSpan? MaxWait;
        public int BudgetMs = 200;
        /// <summary>R03: grant authority; denies writes by default.</summary>
        public ITaskAuthority Authority = DenyWritesAuthority.Instance;
        /// <summary>R01: two-phase audit of batches and barriers.</summary>
        public AuditLog Audit;
        /// <summary>R06: per-document leases (sibling folder of the tasks).</summary>
        public TaskStore DocLeases;

        public TaskManager(TaskStore store, string instance, Func<TaskRecord, ITaskBackend> backend)
        {
            _store = store;
            _instance = instance;
            _backend = backend;
        }

        public TaskStore Store => _store;

        public sealed class StartResult
        {
            public TaskRecord Task;
            public bool Created;
            public bool AlreadyRunning;
            public string Error;
            public string ErrorStatus;
        }

        /// <summary>
        /// Creates and launches a task. Same id with the same fingerprint → the existing one is returned (no duplicate); same id with other content →
        /// error (an id identifies one task). The recipe is stored with the task: resume uses that copy.
        /// </summary>
        public StartResult Start(TaskRecord draft, string recipeCode, string client)
        {
            lock (_lock)
            {
                var existing = _store.Load(draft.Id);
                if (existing != null)
                {
                    if (existing.ContentHash != draft.ContentHash)
                        return new StartResult
                        {
                            Error = "task " + draft.Id + " already exists with other content (different recipe, arguments or document: " + existing.ContentHash + " ≠ " + draft.ContentHash +
                                    "). Use another id, or task resume " + draft.Id + " to continue the existing one as is.",
                            ErrorStatus = Status.Invalid,
                        };
                    var running = _running.ContainsKey(draft.Id);
                    if (!running && !TaskState.IsFinal(existing.State)) return Resume(draft.Id, client, null);
                    return new StartResult { Task = running ? _running[draft.Id].Task_ : existing, AlreadyRunning = running };
                }
                draft.Created = TaskTime.Now();
                draft.State = TaskState.Queued;
                draft.Client = client;
                if (client != null && !draft.Clients.Contains(client)) draft.Clients.Add(client);
                _store.Create(draft, recipeCode);
                Launch(draft);
                return new StartResult { Task = draft, Created = true };
            }
        }

        /// <summary>Resumes an unfinished task (or one finished with differences, to retry conflicts and rolled-back units).</summary>
        public StartResult Resume(string id, string client, Dictionary<string, object> policy)
        {
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r)) return new StartResult { Task = r.Task_, AlreadyRunning = true };
                var t = _store.Load(id);
                if (t == null) return new StartResult { Error = "task " + id + " does not exist (task list)", ErrorStatus = Status.Invalid };
                if (t.State == TaskState.Applied)
                    return new StartResult { Task = t };
                var elsewhere = HeldElsewhere(id);
                if (elsewhere != null) return new StartResult { Task = t, Error = elsewhere, ErrorStatus = Status.DocumentBusy };
                if (policy != null) foreach (var kv in policy) t.Policy[kv.Key] = kv.Value;
                t.Client = client ?? t.Client;
                if (client != null && !t.Clients.Contains(client)) t.Clients.Add(client);
                t.State = TaskState.Queued;
                t.Reason = null;
                _store.Save(t);
                Launch(t);
                return new StartResult { Task = t };
            }
        }

        private void Launch(TaskRecord t)
        {
            var backend = _backend(t);
            var runner = new TaskRunner(_store, backend, t, "inst:" + _instance, _instance, Changed)
            {
                BudgetMs = BudgetMs, Authority = Authority, Audit = Audit,
                DocLeases = DocLeases ?? new TaskStore(_store.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + "_documents"),
            };
            if (Delay != null) runner.Delay = Delay;
            if (MaxWait.HasValue) runner.MaxWait = MaxWait.Value;
            _running[t.Id] = runner;
            _work[t.Id] = Task.Run(async () =>
            {
                try { await runner.RunAsync(CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    lock (_lock) { _running.Remove(t.Id); }
                    // The backend releases what it took (documents, compilation) when the engine finishes, no matter what.
                    try { (backend as IDisposable)?.Dispose(); } catch (Exception) { }
                    Changed(t);
                }
            });
        }

        /// <summary>Waits for the task engine to finish (for tests and for an orderly Revit shutdown).</summary>
        public Task Completion(string id)
        {
            lock (_lock) return _work.TryGetValue(id, out var w) ? w : Task.CompletedTask;
        }

        public bool IsRunning(string id)
        {
            lock (_lock) return _running.ContainsKey(id);
        }

        /// <summary>
        /// Another Revit holds the task (valid lease from another instance): its engine does not read tarea.json, so changing it from here
        /// would be lost at its next checkpoint. null if nobody else holds it.
        /// </summary>
        public string HeldElsewhere(string id)
        {
            var lease = _store.ReadLease(id);
            return lease != null && lease.Valid(DateTime.UtcNow) && lease.Instance != _instance
                ? "the task is running in another Revit (" + lease.Instance + "); do it from that Revit"
                : null;
        }

        /// <summary>
        /// Cooperative cancellation: stays "cancellation_requested" until the engine confirms it between batches.
        /// <paramref name="busy"/> says why it was not cancelled if another Revit holds it (the task is returned unchanged).
        /// </summary>
        public TaskRecord Cancel(string id, out string busy)
        {
            busy = null;
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r))
                {
                    r.CancelRequested = true;
                    var t = r.Task_;
                    if (!TaskState.IsFinal(t.State)) { t.State = TaskState.CancelRequested; t.Reason = "cancellation requested; confirmed when the current unit finishes"; }
                    Changed(t);
                    return t;
                }
                var s = _store.Load(id);
                if (s == null) return null;
                if ((busy = HeldElsewhere(id)) != null) return s;
                if (!TaskState.IsFinal(s.State))
                {
                    s.State = TaskState.Cancelled;
                    s.Reason = "cancelled while not running; what was committed before stays applied";
                    s.Rev++;
                    _store.Save(s);
                    Changed(s);
                }
                return s;
            }
        }

        /// <summary>Current state: the engine's if it runs here; otherwise the one on disk (with "interrupted" if its engine died).</summary>
        public TaskRecord Get(string id, out UnitLog units)
        {
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r)) { units = r.Units; return r.Task_; }
            }
            var t = _store.Load(id);
            units = t == null ? new UnitLog() : _store.LoadUnits(id);
            if (t != null && (t.State == TaskState.Running || t.State == TaskState.Queued || t.State == TaskState.CancelRequested))
            {
                var lease = _store.ReadLease(id);
                if (lease == null || !lease.Valid(DateTime.UtcNow) || lease.Instance == _instance)
                {
                    // Nobody is running it: the process that held it died or Revit was closed.
                    t.State = TaskState.Interrupted;
                    t.Reason = "the engine that was running it is gone (Revit was closed or restarted); task resume verifies what is doubtful and continues";
                }
            }
            return t;
        }

        /// <summary>Waits until the task revision exceeds <paramref name="since"/> or the time runs out (no polling by the agent).</summary>
        public async Task<TaskRecord> WaitAsync(string id, long since, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (true)
            {
                Task signal;
                lock (_lock) signal = Signal(id).Task;
                var t = Get(id, out _);
                if (t == null || t.Rev > since || !IsRunning(id)) return t;
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return t;
                await Task.WhenAny(signal, Task.Delay(left, ct)).ConfigureAwait(false);
            }
        }

        private TaskCompletionSource<bool> Signal(string id)
        {
            if (!_signals.TryGetValue(id, out var s) || s.Task.IsCompleted)
            {
                s = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _signals[id] = s;
            }
            return s;
        }

        private void Changed(TaskRecord t)
        {
            TaskCompletionSource<bool> s;
            lock (_lock) { _signals.TryGetValue(t.Id, out s); _signals.Remove(t.Id); }
            s?.TrySetResult(true);
        }

        /// <summary>
        /// Explicit reconciliation (§3.2): the agent or the user read the model and decide. aplicada = stays committed (not repeated);
        /// no_aplicada = goes back to pending (will run on resume). Only with the task stopped.
        /// </summary>
        /// <summary>
        /// Reconciliation with explicit transitions (R05):
        /// - only DOUBTFUL units (en_curso, parcial, desconocida). A committed/skipped unit never goes back to pending this way
        ///   (extraordinary procedure: a new task the user decides on);
        /// - "applied" requires evidence (nota); "not applied" causes it to run again, so it also requires the
        ///   reconciliar_no_aplicada grant from a person for THIS task and document;
        /// - a finished task (aplicada, aplicada_con_diferencias, cancelada) is not reopened;
        /// - nothing is deleted: each decision is a new event with the previous state.
        /// </summary>
        public string Reconcile(string id, IList<string> keys, bool applied, string note, string who)
        {
            lock (_lock)
            {
                if (_running.ContainsKey(id)) return "the task is running; cancel it or wait for it to stop before reconciling";
                var elsewhere = HeldElsewhere(id);
                if (elsewhere != null) return elsewhere;
                var t = _store.Load(id);
                if (t == null) return "task " + id + " does not exist";
                if (TaskState.IsFinal(t.State)) return "illegal_transition: the task is " + t.State + " and is not reopened; start a new task if needed";
                if (string.IsNullOrWhiteSpace(note)) return "evidence_required: state in note= what was checked in the model (ids, values) to decide";
                var units = _store.LoadUnits(id);
                var targets = keys != null && keys.Count > 0
                    ? keys.ToList()
                    : units.Units.Values.Where(u => UnitState.NeedsReconcile(u.State)).Select(u => u.Key).ToList();
                var unknown = targets.Where(k => units.Get(k) == null).ToList();
                if (unknown.Count > 0) return "units that do not exist in the task: " + string.Join(", ", unknown.Take(10));
                var illegal = targets.Where(k => !UnitState.NeedsReconcile(units.Get(k).State)).ToList();
                if (illegal.Count > 0)
                    return "illegal_transition: only doubtful units are reconciled (running, partial, unknown); " +
                           string.Join(", ", illegal.Take(10).Select(k => k + "=" + units.Get(k).State)) +
                           (illegal.Any(k => UnitState.IsDone(units.Get(k).State)) ? ". A committed unit is not run again." : "");
                if (!applied)
                {
                    var why = (Authority == null ? "no authority configured" : Authority.Check(t, Capability.ReconcileNotApplied, DateTime.UtcNow));
                    if (why != null) return "authorization_required: marking as not applied will make it run again; " + why;
                }
                foreach (var k in targets)
                {
                    long? seq = null;
                    if (applied) seq = ++t.ConfirmSeq;
                    _store.AppendUnitEvent(id, new Dictionary<string, object>
                    {
                        ["ev"] = "reconciled", ["key"] = k, ["status"] = applied ? UnitState.Committed : UnitState.Pending, ["seq"] = seq,
                        ["before"] = units.Get(k).State, ["who"] = who, ["evidence"] = note,
                        ["error"] = (applied ? "reconciled as applied" : "reconciled as not applied") + ": " + note + " (" + who + ")",
                    });
                }
                t.Decisions.Add(new Dictionary<string, object>
                {
                    ["t"] = TaskTime.Now(), ["text"] = "reconciled " + targets.Count + " units as " + (applied ? "applied" : "not applied") + ": " + note, ["who"] = who,
                });
                if (t.State == TaskState.Interrupted || t.State == TaskState.Partial) t.Reason = "reconciled; use task resume";
                t.Rev++;
                t.Summary = TaskSummary.Continuation(t, _store.LoadUnits(id));
                _store.Save(t);
                Changed(t);
                return null;
            }
        }

        /// <summary>Notes a decision of the agent or the user in the task memory (it appears in the continuation summary).</summary>
        public string Note(string id, string text, string who)
        {
            lock (_lock)
            {
                TaskRecord t;
                if (_running.TryGetValue(id, out var r)) t = r.Task_;
                else
                {
                    var elsewhere = HeldElsewhere(id);
                    if (elsewhere != null) return elsewhere;
                    t = _store.Load(id);
                }
                if (t == null) return "task " + id + " does not exist";
                lock (t) t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["text"] = text, ["who"] = who });
                if (!_running.ContainsKey(id)) { t.Rev++; _store.Save(t); }
                Changed(t);
                return null;
            }
        }

        /// <summary>
        /// A document was saved or synced (Revit reports it, whether the task or the user requested it). Unfinished tasks of that
        /// document IN THAT SAME opening record the evidence: what was committed so far is already on disk. A save in another opening does not
        /// count (what was committed in the previous opening may have been lost when closing without saving).
        /// </summary>
        public int RecordPersist(string docKey, string generation, PersistEvidence ev)
        {
            if (docKey == null || generation == null || ev == null) return 0;
            var n = 0;
            List<TaskRecord> stored;
            lock (_lock)
            {
                foreach (var r in _running.Values)
                {
                    var t = r.Task_;
                    if (t.MainDoc?.Key != docKey) continue;
                    lock (t)
                    {
                        if (t.MainDoc.Generation != null && t.MainDoc.Generation != generation) continue;
                        t.NotePersist(new PersistEvidence { Kind = ev.Kind, When = ev.When, Seq = t.ConfirmSeq, Generation = generation, File = ev.File, Bytes = ev.Bytes, Mtime = ev.Mtime, Sha = ev.Sha });
                    }
                    n++;
                }
                stored = _store.List().Where(t => !_running.ContainsKey(t.Id) && !TaskState.IsFinal(t.State) && t.MainDoc?.Key == docKey && t.MainDoc.Generation == generation && HeldElsewhere(t.Id) == null).ToList();
                foreach (var t in stored)
                {
                    t.NotePersist(new PersistEvidence { Kind = ev.Kind, When = ev.When, Seq = t.ConfirmSeq, Generation = generation, File = ev.File, Bytes = ev.Bytes, Mtime = ev.Mtime, Sha = ev.Sha });
                    t.Rev++;
                    _store.Save(t);
                    n++;
                }
            }
            foreach (var t in stored) Changed(t);
            return n;
        }

        /// <summary>Save as: the document's unfinished tasks continue in the new file (and note it).</summary>
        public int Rekey(string oldKey, string newKey)
        {
            var n = 0;
            var changed = new List<TaskRecord>();
            lock (_lock)
            {
                foreach (var r in _running.Values)
                {
                    var t = r.Task_;
                    var d = t.Docs.FirstOrDefault(x => x.Key == oldKey);
                    if (d == null) continue;
                    lock (t)
                    {
                        d.Key = newKey;
                        t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["text"] = "the document was saved as " + newKey + " (formerly " + oldKey + "); the task continues in the new one", ["who"] = "revit" });
                    }
                    changed.Add(t);
                    n++;
                }
                foreach (var t in _store.List().Where(x => !_running.ContainsKey(x.Id) && !TaskState.IsFinal(x.State) && HeldElsewhere(x.Id) == null))
                {
                    var d = t.Docs.FirstOrDefault(x => x.Key == oldKey);
                    if (d == null) continue;
                    d.Key = newKey;
                    t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["text"] = "the document was saved as " + newKey + " (formerly " + oldKey + ")", ["who"] = "revit" });
                    t.Rev++;
                    _store.Save(t);
                    changed.Add(t);
                    n++;
                }
            }
            foreach (var t in changed) Changed(t);
            return n;
        }

        /// <summary>Archives a stopped task (moves its folder to tareas\_archivo). Deletes nothing.</summary>
        public string Archive(string id)
        {
            lock (_lock)
            {
                if (_running.ContainsKey(id)) return "the task is running; cancel it before archiving it";
                if (!_store.Exists(id)) return "task " + id + " does not exist";
                var elsewhere = HeldElsewhere(id);
                if (elsewhere != null) return elsewhere;
                return _store.Archive(id);
            }
        }

        public List<TaskRecord> List() => _store.List().Select(t => Get(t.Id, out _)).Where(t => t != null).ToList();

        /// <summary>Unit rows (paged) so as not to send thousands to the agent.</summary>
        public List<object[]> UnitRows(string id, string state, int from, int count, out int total)
        {
            Get(id, out var units);
            var rows = units.Order.Select(k => units.Units[k]).Where(u => state == null || u.State == state).ToList();
            total = rows.Count;
            return rows.Skip(Math.Max(0, from)).Take(Math.Max(1, Math.Min(500, count))).Select(u => u.Row()).ToList();
        }
    }
}
