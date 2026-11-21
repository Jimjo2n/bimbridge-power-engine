// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Long jobs (geometry over hundreds of elements). The request waits a few seconds; if it has not finished,
    /// it returns job_id and the job continues inside Revit in 250 ms slices. That way the bridge timeout is never
    /// hit (pyRevit: 4 timeouts that cost 31.9 min) and Revit keeps responding between slices.
    /// </summary>
    public sealed class Job
    {
        public string Id;
        public string SessionId;
        /// <summary>Named agent that started the job (fingerprint of principal + alias); null if the session was anonymous.</summary>
        public string AgentId;
        /// <summary>Authorized task (tarea=…). A job with a task continues even if the connection drops, and another connection with the same task can query it.</summary>
        public string Task;
        public string Kind;
        public string Status = JobStatus.InProgress;
        public DateTime CreatedUtc;
        public DateTime? FinishedUtc;
        public Dictionary<string, object> Result;
        public string Error;
        public string ErrorStatus;
        public CancellationTokenSource Cts;
        /// <summary>Progress the job updates from the Revit thread.</summary>
        public volatile int Done;
        public volatile int Total;
        public volatile string Phase;
        /// <summary>Sets the job saved when it finished (saved again into the session when delivered).</summary>
        public Dictionary<string, long[]> Sets = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        public bool Delivered;
        /// <summary>Error while recording the end (e.g. journal not writable): reported as a warning in the response.</summary>
        public volatile string FinalizeError;
        /// <summary>Model revision the result was computed with.</summary>
        public long Rev;
        /// <summary>The Task doing the work (to wait for its end without polling).</summary>
        public System.Threading.Tasks.Task Work;

        public bool IsFinal => Status != JobStatus.InProgress;

        /// <summary>Response status at the end (ok, completado_con_diferencias, aplicado_error_resultado, parcial…).</summary>
        public string FinalState;

        /// <summary>Status the AI sees: en_proceso while it runs; at the end, the real status (never "ok" for a failure).</summary>
        public string State => Status == JobStatus.InProgress ? Bimbridge.Protocol.Status.InProgress
            : Status == JobStatus.Done ? (FinalState ?? Bimbridge.Protocol.Status.Ok)
            : (ErrorStatus ?? (Status == JobStatus.Cancelled ? Bimbridge.Protocol.Status.Cancelled : Bimbridge.Protocol.Status.Error));

        internal List<Action<Job>> FinalHandlers = new List<Action<Job>>();

        public Dictionary<string, object> Progress()
        {
            var d = new Dictionary<string, object> { ["job_id"] = Id, ["type"] = Kind, ["job_status"] = Status };
            if (!IsFinal)
            {
                if (Total > 0) d["progress"] = Done + "/" + Total;
                if (!string.IsNullOrEmpty(Phase)) d["phase"] = Phase;
                d["seconds"] = (long)(DateTime.UtcNow - CreatedUtc).TotalSeconds;
            }
            return d;
        }
    }

    public static class JobStatus
    {
        public const string InProgress = "in_progress";
        public const string Done = "ready";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
    }

    public sealed class JobStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _now;
        private int _counter;

        public const int MaxKept = 64;

        public JobStore(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        public Job Create(string sessionId, string kind)
        {
            lock (_lock)
            {
                _counter++;
                var j = new Job
                {
                    Id = "G-" + _now().ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + _counter.ToString("D3", CultureInfo.InvariantCulture),
                    SessionId = sessionId,
                    AgentId = Correlation.Current?.AgentId,   // AUD-13: nominal owner (survives reconnection; the session does not)
                    Kind = kind,
                    CreatedUtc = _now(),
                    Cts = new CancellationTokenSource(),
                };
                _jobs[j.Id] = j;
                Prune();
                return j;
            }
        }

        public Job Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock) return _jobs.TryGetValue(id.Trim(), out var j) ? j : null;
        }

        public void Complete(Job j, Dictionary<string, object> result, string status = null)
        {
            lock (_lock)
            {
                if (j.IsFinal) return;
                j.Result = result;
                // AUD-18: the final status is decided HERE, before notifying anyone (the response, a later job query and the history see
                // the same thing). A result with verification differences never ends up as "ok", even if the caller passes no status
                // or passes "ok" by mistake.
                j.FinalState = HasDifferences(result) && (status == null || status == Bimbridge.Protocol.Status.Ok)
                    ? Bimbridge.Protocol.Status.CompletedWithDifferences : status;
                j.Status = JobStatus.Done;
                j.FinishedUtc = _now();
            }
            FireFinal(j);
        }

        /// <summary>Does the result declare differences between what was requested and what was verified? ("differences" key with at least one element).</summary>
        public static bool HasDifferences(Dictionary<string, object> result) =>
            result != null && result.TryGetValue("differences", out var d) && d is System.Collections.ICollection c && c.Count > 0;

        public void Fail(Job j, string status, string error)
        {
            lock (_lock)
            {
                if (j.IsFinal) return;
                j.Status = status == JobStatus.Cancelled ? JobStatus.Cancelled : JobStatus.Error;
                j.ErrorStatus = status == JobStatus.Cancelled ? Bimbridge.Protocol.Status.Cancelled : status;
                j.Error = error;
                j.FinishedUtc = _now();
            }
            FireFinal(j);
        }

        /// <summary>
        /// Single finalization point (H02): <paramref name="handler"/> runs once when the job really ends
        /// (successfully, with an error, cancelled or from an unobserved worker exception). If it already ended, it runs now.
        /// </summary>
        public void WhenFinal(Job j, Action<Job> handler)
        {
            var now = false;
            lock (_lock)
            {
                if (j.IsFinal) now = true;
                else j.FinalHandlers.Add(handler);
            }
            if (now) SafeInvoke(handler, j);
        }

        /// <summary>0.9.1 (Block I): observer of every completion (operation history). Runs after the job's handlers.</summary>
        public Action<Job> OnAnyFinal;

        private void FireFinal(Job j)
        {
            List<Action<Job>> hs;
            lock (_lock) { hs = j.FinalHandlers; j.FinalHandlers = new List<Action<Job>>(); }
            foreach (var h in hs) SafeInvoke(h, j);
            var any = OnAnyFinal;
            if (any != null) SafeInvoke(any, j);
        }

        /// <summary>Errors from the final handlers (e.g. the journal could not be written); shown in diagnostics.</summary>
        public readonly List<string> FinalHandlerErrors = new List<string>();

        private void SafeInvoke(Action<Job> h, Job j)
        {
            try { h(j); }
            catch (Exception ex)
            {
                lock (_lock) { if (FinalHandlerErrors.Count < 50) FinalHandlerErrors.Add(j.Id + ": " + ex.Message); }
                j.FinalizeError = ex.Message;
            }
        }

        /// <summary>
        /// Starts the job body and guarantees a final status: if the body ends without completing or failing the job, or throws an
        /// exception, the job ends in error with the reason. It never stays "in_progress" forever.
        /// Issue #7: returns void on purpose. The job is supervised (the continuation observes any exception) and whoever
        /// wants to wait for it uses <see cref="Job.Work"/> or <see cref="WhenFinal"/>; that way callers have no unobserved tasks (CS4014).
        /// </summary>
        public void Run(Job j, Func<Task> body)
        {
            var work = Task.Run(body);
            j.Work = work.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException();
                    Fail(j, ex is OperationCanceledException ? JobStatus.Cancelled : Bimbridge.Protocol.Status.Unknown,
                        "the job ended with an unhandled exception (" + (ex?.GetType().Name ?? "?") + ": " + ex?.Message +
                        "); it is unknown what was applied: reconcile before repeating");
                }
                else if (t.IsCanceled) Fail(j, JobStatus.Cancelled, "cancelled");
                else if (!j.IsFinal) Fail(j, Bimbridge.Protocol.Status.Unknown, "the job ended without recording its result; reconcile before repeating");
            }, TaskScheduler.Default);
        }

        public bool Cancel(Job j)
        {
            if (j == null || j.IsFinal) return false;
            try { j.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        /// <summary>Cancels the running jobs of a session that disconnected (nobody will read the result).</summary>
        public int CancelSession(string sessionId)
        {
            List<Job> list;
            // Jobs of an authorized task are not abandoned on disconnect (R023): they continue and are claimed with job id tarea=.
            lock (_lock) list = _jobs.Values.Where(j => j.SessionId == sessionId && !j.IsFinal && string.IsNullOrEmpty(j.Task)).ToList();
            foreach (var j in list) Cancel(j);
            return list.Count;
        }

        public List<Job> ForSession(string sessionId, string task = null)
        {
            lock (_lock) return _jobs.Values.Where(j => j.SessionId == sessionId || (task != null && j.Task == task)).OrderByDescending(j => j.CreatedUtc).ToList();
        }

        /// <summary>All running jobs. Only for the person in the Revit panel (agents use <see cref="ForOwner"/>).</summary>
        public List<Job> AllRunning()
        {
            lock (_lock) return _jobs.Values.Where(j => !j.IsFinal).OrderBy(j => j.CreatedUtc).ToList();
        }

        /// <summary>AUD-13: jobs of the same session or the same named agent (for the history; the task name does not count).</summary>
        public List<Job> ForOwner(string sessionId, string agentId)
        {
            lock (_lock) return _jobs.Values.Where(j => j.SessionId == sessionId || (!string.IsNullOrEmpty(agentId) && j.AgentId == agentId)).OrderByDescending(j => j.CreatedUtc).ToList();
        }

        /// <summary>Can this connection see the job? The same session, or the same authorized task.</summary>
        public static bool Visible(Job j, string sessionId, string task) =>
            j != null && (j.SessionId == sessionId || (!string.IsNullOrEmpty(task) && j.Task == task));

        public int Running
        {
            get { lock (_lock) return _jobs.Values.Count(j => !j.IsFinal); }
        }

        private void Prune()
        {
            if (_jobs.Count <= MaxKept) return;
            foreach (var old in _jobs.Values.Where(j => j.IsFinal).OrderBy(j => j.CreatedUtc).Take(_jobs.Count - MaxKept).ToList())
                _jobs.Remove(old.Id);
        }
    }

    /// <summary>
    /// Already computed results, per document: if the model did not change (same rev) and the links are the same,
    /// repeating a check costs 0 ms in Revit. In pyRevit "pass 2 / final verification" was repeated (29.5 min in retries).
    /// </summary>
    public sealed class ResultCache
    {
        private readonly object _lock = new object();
        private readonly LinkedList<KeyValuePair<string, Entry>> _lru = new LinkedList<KeyValuePair<string, Entry>>();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>>(StringComparer.Ordinal);
        private readonly int _max;

        public sealed class Entry
        {
            public Dictionary<string, object> Result;
            /// <summary>Raw data (rows) to re-summarize with another page, units or filter without recomputing.</summary>
            public object Payload;
            public Dictionary<string, long[]> Sets;
            public long RevitMs;
            public DateTime CreatedUtc;
        }

        public ResultCache(int max = 48) { _max = max; }

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        public static string Key(string docKey, long rev, string signature, string recipe, IDictionary<string, object> args)
        {
            var parts = args == null ? new List<string>() :
                args.Where(kv => !kv.Key.StartsWith("_", StringComparison.Ordinal) && kv.Key != "save_as" && kv.Key != "page" && kv.Key != "new")
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + "=" + Protocol.Json.Write(kv.Value)).ToList();
            return (docKey ?? "") + "|" + rev.ToString(CultureInfo.InvariantCulture) + "|" + (signature ?? "") + "|" + recipe + "|" + string.Join("&", parts);
        }

        public bool TryGet(string key, out Entry e)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    e = node.Value.Value;
                    Hits++;
                    return true;
                }
                Misses++;
                e = null;
                return false;
            }
        }

        public void Put(string key, Entry e)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var old)) { _lru.Remove(old); _map.Remove(key); }
                var node = _lru.AddFirst(new KeyValuePair<string, Entry>(key, e));
                _map[key] = node;
                while (_lru.Count > _max)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Clear()
        {
            lock (_lock) { _lru.Clear(); _map.Clear(); }
        }

        public int Count { get { lock (_lock) return _map.Count; } }
    }
}
