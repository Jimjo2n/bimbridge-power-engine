// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// 0.9.1 (Block I): history of finished long operations, persistent and queryable (filters, pagination) for the panel (#5)
    /// and for agents. Each completion goes to a JSONL (one line, append) and to an in-memory ring; on startup the last
    /// <see cref="MaxInMemory"/> are reloaded. Includes the retry advice: repeating something that may have been applied is never advised.
    /// </summary>
    public sealed class OperationHistory
    {
        public const int MaxInMemory = 500;
        public const long MaxFileBytes = 4 * 1024 * 1024;

        public sealed class Record
        {
            public string JobId, Kind, Session, Agent, Task, State, Error;
            public DateTime CreatedUtc, FinishedUtc;
            public long Ms;
            public string Retry;

            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["job_id"] = JobId, ["tipo"] = Kind, ["sesion"] = Session, ["agente"] = Agent, ["tarea"] = Task, ["estado"] = State, ["error"] = Error,
                ["inicio"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["fin"] = FinishedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["ms"] = Ms, ["reintento"] = Retry,
            };

            public static Record FromJson(Dictionary<string, object> d) => new Record
            {
                JobId = Arg.GetString(d, "job_id"), Kind = Arg.GetString(d, "tipo"), Session = Arg.GetString(d, "sesion"), Agent = Arg.GetString(d, "agente"), Task = Arg.GetString(d, "tarea"),
                State = Arg.GetString(d, "estado"), Error = Arg.GetString(d, "error"), Retry = Arg.GetString(d, "reintento"), Ms = Arg.GetLong(d, "ms") ?? 0,
                CreatedUtc = DateTime.Parse(Arg.GetString(d, "inicio"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                FinishedUtc = DateTime.Parse(Arg.GetString(d, "fin"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            };
        }

        /// <summary>
        /// Retry advice per final status. "reintentable": nothing was applied (rejection, conflict, validation, busy, expired).
        /// "reconciliar_antes": the result is uncertain or may have been partly applied. "no_necesario": it finished fine.
        /// "revisar_diferencias" (AUD-14): it was applied, but the verification does not match what was requested; the differences
        /// must be reviewed or reconciled before accepting it, and the operation must NOT be repeated (it is already applied).
        /// </summary>
        public static string RetryAdvice(string status)
        {
            switch (status)
            {
                case Status.Ok: return "no_necesario";
                case Status.CompletedWithDifferences: return "revisar_diferencias";
                case Status.Rejected: case Status.Conflict: case Status.Invalid: case Status.Expired: case Status.RolledBack: return "reintentable";
                case Status.Cancelled: return "reconciliar_antes";   // it may have been cancelled midway
                default: return "reconciliar_antes";
            }
        }

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly LinkedList<Record> _ring = new LinkedList<Record>();
        public readonly List<string> LoadErrors = new List<string>();

        public OperationHistory(string path)
        {
            _path = path;
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) return;
            var n = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                n++;
                if (line.Trim().Length == 0) continue;
                try { Push(Record.FromJson(Json.ParseObject(line))); }
                catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)
                { if (LoadErrors.Count < 20) LoadErrors.Add("line " + n + ": " + ex.GetType().Name); }
            }
        }

        private void Push(Record r)
        {
            _ring.AddFirst(r);
            while (_ring.Count > MaxInMemory) _ring.RemoveLast();
        }

        public Record Add(Job j)
        {
            var r = new Record
            {
                JobId = j.Id, Kind = j.Kind, Session = j.SessionId, Agent = j.AgentId, Task = j.Task, State = j.State, Error = j.Error,
                CreatedUtc = j.CreatedUtc, FinishedUtc = j.FinishedUtc ?? DateTime.UtcNow,
            };
            r.Ms = (long)(r.FinishedUtc - r.CreatedUtc).TotalMilliseconds;
            r.Retry = RetryAdvice(r.State);
            lock (_lock)
            {
                Push(r);
                if (_path != null)
                {
                    // Simple rotation: once over the size limit, the in-memory ring is kept and rewritten (the oldest entries are dropped on purpose).
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxFileBytes)
                        Home.WriteAtomic(_path, string.Join("\n", _ring.Reverse().Select(x => Json.Write(x.ToJson()))) + "\n");
                    else
                        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        {
                            var b = Encoding.UTF8.GetBytes(Json.Write(r.ToJson()) + "\n");
                            fs.Write(b, 0, b.Length);
                        }
                }
            }
            return r;
        }

        /// <summary>
        /// AUD-13: who is asking. A record is visible if it was started by the SAME session or the SAME named agent (the session changes on
        /// reconnect; the agent —Windows principal + alias— does not). The task name (tarea=) does NOT grant visibility: the client
        /// declares it and another one could guess it. Seeing others' records requires <see cref="SeeAll"/>, which is only enabled with the
        /// user's explicit authorization (config historial_compartido). Without any identity nothing is visible.
        /// </summary>
        public sealed class Scope
        {
            public string SessionId, AgentId;
            public bool SeeAll;

            public bool CanSee(Record r)
            {
                if (SeeAll) return true;
                if (!string.IsNullOrEmpty(SessionId) && string.Equals(r.Session, SessionId, StringComparison.Ordinal)) return true;
                if (!string.IsNullOrEmpty(AgentId) && string.Equals(r.Agent, AgentId, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        /// <summary>
        /// Scope of a query. Asking for "all" without the user's authorization (<paramref name="sharedAuthorized"/>) is REJECTED
        /// (returns null and the reason): it is not silently downgraded to "own records" nor granted just for asking.
        /// </summary>
        public static Scope ScopeFor(string sessionId, string agentId, bool wantAll, bool sharedAuthorized, out string refusal)
        {
            refusal = null;
            if (wantAll && !sharedAuthorized)
            {
                refusal = "seeing operations from other sessions requires the user to authorize it (config historial_compartido=true). Without that, only your own are visible.";
                return null;
            }
            return new Scope { SessionId = sessionId, AgentId = agentId, SeeAll = wantAll };
        }

        /// <summary>Query with a mandatory scope, filters (any may be null) and pagination; most recent first.
        /// Filters only narrow what is visible: asking for someone else's sesion= does not show it.</summary>
        public (List<Record> Page, int Total) Query(Scope scope, string status, string kind, string session, DateTime? since, string text, int page = 1, int pageSize = 20)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            List<Record> all;
            lock (_lock) all = _ring.ToList();
            IEnumerable<Record> q = all.Where(scope.CanSee);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => string.Equals(r.State, status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(r => (r.Kind ?? "").IndexOf(kind.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(session)) q = q.Where(r => string.Equals(r.Session, session.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.Task, session.Trim(), StringComparison.OrdinalIgnoreCase));
            if (since.HasValue) q = q.Where(r => r.FinishedUtc >= since.Value);
            if (!string.IsNullOrWhiteSpace(text)) q = q.Where(r => ((r.Kind ?? "") + " " + (r.Error ?? "") + " " + r.JobId).IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            var list = q.ToList();
            var size = Math.Max(1, Math.Min(100, pageSize));
            return (list.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), list.Count);
        }

        public int Count { get { lock (_lock) return _ring.Count; } }
    }
}
