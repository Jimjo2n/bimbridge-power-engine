// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>MCP data folder (%LOCALAPPDATA%\Bimbridge or BIMBRIDGE_HOME).</summary>
    public static class Home
    {
        public static string Root
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("BIMBRIDGE_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
                return root;
            }
        }

        /// <summary>A folder or file under <see cref="Root"/> (English name). While data written by 0.9.3 or earlier is still under its legacy
        /// name (the installer migrates it; tests and sandbox homes skip the installer), the legacy one is used.</summary>
        public static string Sub(string name) => Resolve(Path.Combine(Root, name));

        public static string Resolve(string path) => LegacyAliases.ResolveExisting(Root, path);

        public static string Sha256Hex(byte[] data, int chars = 64)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString().Substring(0, Math.Min(64, chars));
            }
        }

        public static string Sha256Hex(string text, int chars = 64) => Sha256Hex(Encoding.UTF8.GetBytes(text ?? ""), chars);

        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var h = sha.ComputeHash(f);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Writes a file atomically (temp file + replace) and flushes it to disk.</summary>
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                f.Write(bytes, 0, bytes.Length);
                f.Flush(true);
            }
            // 0.8.0 (leftover estado.json.tmp-* from the rc1 campaign): on Windows File.Replace can fail briefly if an
            // antivirus or the indexer has the target open. It retries briefly and, if it still fails, the temp file is deleted.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    System.Threading.Thread.Sleep(20 * attempt);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
            }
        }

        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>The journal could not be read or written: idempotency that does not exist is not advertised (H03).</summary>
    public sealed class JournalUnavailableException : Exception
    {
        public JournalUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Append-only, durable and tolerant JSONL file (0.6.0):
    /// - each line is written with WriteThrough and Flush(true);
    /// - an incomplete last line (power cut, dead Revit) is ignored on read and the next write starts on a new line;
    /// - several processes (two Revits) share the file: before writing, the new lines from the others are read;
    /// - if it cannot write, <see cref="JournalUnavailableException"/> is thrown; nothing is ever lost silently.
    /// </summary>
    public sealed class JsonlLog
    {
        private readonly string _path;
        private long _offset;

        public JsonlLog(string path)
        {
            _path = path;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("cannot create the journal folder " + Path.GetDirectoryName(path) + ": " + ex.Message, ex);
            }
        }

        public string Path_ => _path;
        public int CorruptLines { get; private set; }

        /// <summary>Reads the new complete lines since the last read (or all of them the first time).</summary>
        public List<Dictionary<string, object>> ReadNew()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                if (!File.Exists(_path)) return list;
                using (var f = Open(FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    ReadFrom(f, list);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("cannot read the journal " + _path + ": " + ex.Message, ex);
            }
            return list;
        }

        private void ReadFrom(FileStream f, List<Dictionary<string, object>> into)
        {
            if (f.Length <= _offset) return;
            f.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[f.Length - _offset];
            var read = 0;
            while (read < buf.Length)
            {
                var n = f.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (lastNl < 0) return; // only an incomplete line: ignored until it is complete (or forever)
            var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
            _offset += lastNl + 1;
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { into.Add(LegacyAliases.NormalizeRecord(Json.ParseObject(line))); }
                catch (FormatException) { CorruptLines++; }
            }
        }

        /// <summary>Appends a line; first returns (in <paramref name="others"/>) what other processes wrote since the last read.</summary>
        public void Append(Dictionary<string, object> o, List<Dictionary<string, object>> others)
        {
            if (!o.ContainsKey("t")) o["t"] = Home.Now();
            var line = Encoding.UTF8.GetBytes(Json.Write(o) + "\n");
            Exception last = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    using (var f = Open(FileAccess.ReadWrite, FileShare.Read))
                    {
                        ReadFrom(f, others);
                        // A line cut off by another process or by a power cut: start on a new line.
                        var prefix = false;
                        if (f.Length > 0)
                        {
                            f.Seek(f.Length - 1, SeekOrigin.Begin);
                            prefix = f.ReadByte() != '\n';
                        }
                        f.Seek(0, SeekOrigin.End);
                        if (prefix) f.WriteByte((byte)'\n');
                        f.Write(line, 0, line.Length);
                        f.Flush(true);
                        _offset = f.Length;
                    }
                    return;
                }
                catch (IOException ex) when (IsSharing(ex)) { last = ex; Thread.Sleep(50); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new JournalUnavailableException("could not write to the journal " + _path + ": " + ex.Message, ex);
                }
            }
            throw new JournalUnavailableException("the journal " + _path + " is locked by another process", last);
        }

        private FileStream Open(FileAccess access, FileShare share) =>
            new FileStream(_path, access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate, access, share, 4096,
                access == FileAccess.Read ? FileOptions.None : FileOptions.WriteThrough);

        private static bool IsSharing(IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        }
    }

    /// <summary>
    /// Operation journal with idempotency key (R017, R022, R024; rewritten in 0.6.0 for H02/H03).
    /// State machine per (durable document, key):
    /// - running in this process → "Running" with the same job_id (not launched again);
    /// - same id with a different fingerprint → "Mismatch" ALWAYS, also with reconciliar (a key identifies a single operation);
    /// - finished and applied (ok, with differences, held, applied with result error) → "Completed": the saved result is returned;
    /// - finished with confirmed rollback (reverted, rejected, invalid…) → can be repeated with the same key (new attempt);
    /// - error, partial, unknown, cancelled or no end → "Interrupted": must reconcile (read the model) before repeating;
    /// - en_proceso is NOT final: the job writes the end when it truly finishes (a single completion point).
    /// Attempts are not overwritten: each one stays in the file and in <see cref="Record.Attempts"/>.
    /// It does not promise "exactly once" if Revit dies between applying and writing the end: that case stays "Interrupted".
    /// </summary>
    public sealed class OperationJournal
    {
        public sealed class Attempt
        {
            public int N;
            public string Session;
            public string Started;
            public string Finished;
            public string State;
            public string JobId;
            public string Reconciled;
            public string Correlation;
            public string Agent;
            public string PlanId;
            public string DocPrint;
        }

        public sealed class Record
        {
            public string Key;
            public string Doc;
            public string Op;
            public string Hash;
            public string Task;
            public string Session;
            public string Started;
            public string Finished;
            public string State;
            public object Result;
            public string JobId;
            /// <summary>Model fingerprint (ProjectInformation.UniqueId, cloud GUID…) when it was recorded: detects replaced files and copies.</summary>
            public string DocPrint;
            /// <summary>Open generation (instance + sequence): tells reopenings of the same file apart.</summary>
            public string Generation;
            public List<Attempt> Attempts = new List<Attempt>();

            internal Record Clone(string doc)
            {
                var r = (Record)MemberwiseClone();
                r.Doc = doc;
                r.Attempts = Attempts.Select(a => a).ToList();
                return r;
            }
        }

        public enum BeginKind { New, Completed, Interrupted, Running, Mismatch, DocumentReplaced, PossibleCopy }

        /// <summary>Reconciliation decision requested by the agent after reading the model.</summary>
        public enum ReconcileMode
        {
            None,
            /// <summary>"reconciliar=no_aplicado" (or =si): it was confirmed NOT applied; it is repeated with the same fingerprint.</summary>
            NotApplied,
            /// <summary>"reconciliar=aplicado": it was confirmed applied; it is marked done without repeating.</summary>
            Applied,
        }

        public static ReconcileMode ParseReconcile(object v)
        {
            if (v == null) return ReconcileMode.None;
            if (v is bool b) return b ? ReconcileMode.NotApplied : ReconcileMode.None;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            switch (s)
            {
                case "": case "no": case "false": return ReconcileMode.None;
                case "applied": case "done":  return ReconcileMode.Applied;
                case "yes":  case "true": case "not_applied": case "repeat": return ReconcileMode.NotApplied;
                default: throw new ArgumentException("reconcile= must be not_applied (repeat) or applied (mark done)");
            }
        }

        public sealed class BeginResult
        {
            public BeginKind Kind;
            public Record Previous;
            /// <summary>Document key where the operation was found when it is a possible copy.</summary>
            public string OtherDoc;
        }

        private readonly object _lock = new object();
        private readonly JsonlLog _log;
        private readonly Dictionary<string, Record> _index = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _running = new Dictionary<string, string>(StringComparer.Ordinal); // id → job id (or "")

        public static string DefaultPath => Home.Resolve(Path.Combine(Home.Root, "operations", "operations.jsonl"));

        public OperationJournal(string path = null)
        {
            _log = new JsonlLog(path ?? DefaultPath);
            Apply(_log.ReadNew());
        }

        public string Path_ => _log.Path_;
        public int CorruptLines => _log.CorruptLines;

        private static string Id(string doc, string key) => (doc ?? "") + "\u0001" + key;

        private void Apply(List<Dictionary<string, object>> events)
        {
            foreach (var o in events)
            {
                var ev = Arg.GetString(o, "ev");
                if (ev == "alias")
                {
                    ApplyAlias(Arg.GetString(o, "de"), Arg.GetString(o, "a"));
                    continue;
                }
                var key = Arg.GetString(o, "key");
                var doc = Arg.GetString(o, "doc");
                if (key == null) continue;
                var id = Id(doc, key);
                _index.TryGetValue(id, out var r);
                switch (ev)
                {
                    case "start":
                        if (r == null)
                        {
                            r = new Record { Key = key, Doc = doc };
                            _index[id] = r;
                        }
                        r.Op = Arg.GetString(o, "op");
                        r.Hash = Arg.GetString(o, "hash");
                        r.Task = Arg.GetString(o, "task") ?? r.Task;
                        r.Session = Arg.GetString(o, "session");
                        r.Started = Arg.GetString(o, "t");
                        r.DocPrint = Arg.GetString(o, "doc_fingerprint") ?? r.DocPrint;
                        r.Generation = Arg.GetString(o, "generation");
                        r.Finished = null;
                        r.State = null;
                        r.Result = null;
                        r.JobId = null;
                        r.Attempts.Add(new Attempt
                        {
                            N = r.Attempts.Count + 1, Session = r.Session, Started = r.Started,
                            Correlation = Arg.GetString(o, "attempt_id"), Agent = Arg.GetString(o, "agent"),
                            PlanId = Arg.GetString(o, "plan_id"), DocPrint = r.DocPrint,
                        });
                        break;
                    case "job" when r != null:
                        r.JobId = Arg.GetString(o, "job_id");
                        if (r.Attempts.Count > 0) r.Attempts[r.Attempts.Count - 1].JobId = r.JobId;
                        break;
                    case "end" when r != null:
                        r.Finished = Arg.GetString(o, "t");
                        r.State = Arg.GetString(o, "status");
                        o.TryGetValue("result", out r.Result);
                        r.Result = LegacyAliases.NormalizePayloadStatus(r.Result);   // replayed as stored; only a legacy status is mapped
                        if (r.Attempts.Count > 0) { var a = r.Attempts[r.Attempts.Count - 1]; a.Finished = r.Finished; a.State = r.State; }
                        break;
                    case "reconciled" when r != null:
                        var dec = Arg.GetString(o, "decision");
                        if (r.Attempts.Count > 0) r.Attempts[r.Attempts.Count - 1].Reconciled = dec;
                        if (dec == "applied")
                        {
                            r.Finished = Arg.GetString(o, "t");
                            r.State = Status.Ok;
                            o.TryGetValue("evidence", out r.Result);
                        }
                        break;
                    case "forget":
                        _index.Remove(id);
                        break;
                }
            }
        }

        private void ApplyAlias(string from, string to)
        {
            if (from == null || to == null || from == to) return;
            foreach (var r in _index.Values.Where(x => x.Doc == from).ToList())
            {
                var id = Id(to, r.Key);
                if (!_index.ContainsKey(id)) _index[id] = r.Clone(to);
            }
        }

        private void Write(Dictionary<string, object> o)
        {
            var others = new List<Dictionary<string, object>>();
            _log.Append(o, others);
            others.Add(o);
            Apply(others);
        }

        /// <summary>Reads what other processes wrote (cheap if there is nothing new).</summary>
        private void Refresh() => Apply(_log.ReadNew());

        /// <summary>Final statuses that count as "already done" (not repeated).</summary>
        public static bool IsDone(string status) => Status.CountsAsApplied(status);

        public BeginResult Begin(string key, string doc, string op, string hash, string task, string session, bool reconcile = false) =>
            Begin(key, doc, op, hash, task, session, reconcile ? ReconcileMode.NotApplied : ReconcileMode.None);

        /// <summary>
        /// Records the intent ("start" event, durable) or explains why not. Throws <see cref="JournalUnavailableException"/> if it cannot
        /// read or write the journal: the caller must NOT apply the operation in that case.
        /// </summary>
        /// <param name="legacyHash">The fingerprint the same operation had in 0.9.3 and earlier (legacy argument names, see
        /// <see cref="LegacyHashArgs"/>): a retry of a key recorded before the upgrade is the same operation, not a conflict.</param>
        public BeginResult Begin(string key, string doc, string op, string hash, string task, string session, ReconcileMode mode,
            string docPrint = null, string generation = null, string correlation = null, string agent = null, string planId = null, string legacyHash = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("empty key");
            bool Differs(string stored) => stored != null && hash != null && stored != hash && !(legacyHash != null && stored == legacyHash);
            lock (_lock)
            {
                Refresh();
                var id = Id(doc, key);
                if (_running.TryGetValue(id, out var runningJob))
                {
                    _index.TryGetValue(id, out var cur);
                    if (cur != null && Differs(cur.Hash)) return new BeginResult { Kind = BeginKind.Mismatch, Previous = cur };
                    return new BeginResult { Kind = BeginKind.Running, Previous = cur };
                }
                if (_index.TryGetValue(id, out var prev))
                {
                    if (Differs(prev.Hash))
                        return new BeginResult { Kind = BeginKind.Mismatch, Previous = prev };
                    if (prev.DocPrint != null && docPrint != null && prev.DocPrint != docPrint)
                        return new BeginResult { Kind = BeginKind.DocumentReplaced, Previous = prev };
                    if (prev.Finished != null && IsDone(prev.State))
                        return new BeginResult { Kind = BeginKind.Completed, Previous = prev };
                    if (!(prev.Finished != null && Status.ConfirmsNothingApplied(prev.State)))
                    {
                        // No end, error, partial, unknown or cancelled: only with an explicit reconciliation decision.
                        if (mode == ReconcileMode.None) return new BeginResult { Kind = BeginKind.Interrupted, Previous = prev };
                        if (mode == ReconcileMode.Applied)
                        {
                            MarkReconciled(prev, "applied", null);
                            return new BeginResult { Kind = BeginKind.Completed, Previous = prev };
                        }
                        MarkReconciled(prev, "not_applied", null);
                    }
                }
                else if (docPrint != null)
                {
                    // Was the same operation recorded in another file with the same model (copy, Save As outside the MCP)?
                    var copy = _index.Values.FirstOrDefault(r => r.Key == key && r.DocPrint == docPrint && r.Doc != doc &&
                                                                 !(r.Finished != null && Status.ConfirmsNothingApplied(r.State)));
                    if (copy != null)
                    {
                        if (Differs(copy.Hash)) return new BeginResult { Kind = BeginKind.Mismatch, Previous = copy, OtherDoc = copy.Doc };
                        if (mode == ReconcileMode.None) return new BeginResult { Kind = BeginKind.PossibleCopy, Previous = copy, OtherDoc = copy.Doc };
                        if (mode == ReconcileMode.Applied)
                        {
                            Write(new Dictionary<string, object> { ["ev"] = "alias", ["de"] = copy.Doc, ["a"] = doc });
                            var aliased = _index[Id(doc, key)];
                            MarkReconciled(aliased, "applied", null);
                            return new BeginResult { Kind = BeginKind.Completed, Previous = aliased };
                        }
                    }
                }
                Write(new Dictionary<string, object>
                {
                    ["ev"] = "start", ["key"] = key, ["doc"] = doc, ["op"] = op, ["hash"] = hash, ["task"] = task, ["session"] = session,
                    ["doc_fingerprint"] = docPrint, ["generation"] = generation,
                    ["attempt_id"] = correlation, ["agent"] = agent, ["plan_id"] = planId,
                });
                _running[id] = "";
                return new BeginResult { Kind = BeginKind.New, Previous = _index.TryGetValue(id, out var now) ? now : null };
            }
        }

        public Dictionary<string, object> FindReceipt(string correlation, string session, string agent, string doc, string docPrint)
        {
            if (string.IsNullOrEmpty(correlation) || string.IsNullOrEmpty(doc) || string.IsNullOrEmpty(docPrint)) return null;
            lock (_lock)
            {
                Refresh();
                var matches = _index.Values.Where(r => r.Doc == doc)
                    .SelectMany(r => r.Attempts.Where(a => a.Correlation == correlation && a.DocPrint == docPrint &&
                        (a.Session == session || (!string.IsNullOrEmpty(agent) && a.Agent == agent))).Select(a => (Record: r, Attempt: a)))
                    .Take(2).ToList();
                if (matches.Count != 1) return null;
                var (record, attempt) = matches[0];
                var active = ReferenceEquals(attempt, record.Attempts.Last()) && _running.ContainsKey(Id(doc, record.Key));
                return new Dictionary<string, object>
                {
                    ["attempt_id"] = correlation, ["status"] = attempt.State ?? (active ? Status.InProgress : Status.Unknown),
                    ["key"] = record.Key, ["plan_id"] = attempt.PlanId, ["doc"] = doc, ["doc_fingerprint"] = docPrint,
                    ["job_id"] = attempt.JobId, ["start"] = attempt.Started, ["end"] = attempt.Finished,
                };
            }
        }

        private void MarkReconciled(Record r, string decision, object evidence)
        {
            Write(new Dictionary<string, object> { ["ev"] = "reconciled", ["key"] = r.Key, ["doc"] = r.Doc, ["decision"] = decision, ["evidence"] = evidence });
        }

        /// <summary>Links the job (job_id) carrying the operation: repeating the key while it runs returns that same job.</summary>
        public void LinkJob(string key, string doc, string jobId)
        {
            lock (_lock)
            {
                var id = Id(doc, key);
                Write(new Dictionary<string, object> { ["ev"] = "job", ["key"] = key, ["doc"] = doc, ["job_id"] = jobId });
                if (_running.ContainsKey(id)) _running[id] = jobId ?? "";
            }
        }

        public string RunningJob(string key, string doc)
        {
            lock (_lock) return _running.TryGetValue(Id(doc, key), out var j) ? j : null;
        }

        /// <summary>
        /// Records the end. en_proceso is NOT final and does nothing (the job continues and will record it when done).
        /// Throws <see cref="JournalUnavailableException"/> if it cannot write: the caller must warn that the end is not recorded.
        /// </summary>
        public void Complete(string key, string doc, string status, object result)
        {
            if (status == Status.InProgress || status == Status.Queued || status == Status.CancelRequested) return;
            lock (_lock)
            {
                var id = Id(doc, key);
                try
                {
                    Write(new Dictionary<string, object> { ["ev"] = "end", ["key"] = key, ["doc"] = doc, ["status"] = status, ["result"] = Trim(result) });
                }
                finally
                {
                    _running.Remove(id);
                }
            }
        }

        /// <summary>Save As / new document saved: the operations of the previous document also count for the new one.</summary>
        public void Alias(string fromDoc, string toDoc)
        {
            if (fromDoc == null || toDoc == null || fromDoc == toDoc) return;
            lock (_lock) Write(new Dictionary<string, object> { ["ev"] = "alias", ["de"] = fromDoc, ["a"] = toDoc });
        }

        /// <summary>Explicit reconciliation (task/command reconciliar): records the decision and its evidence.</summary>
        public bool Reconcile(string key, string doc, bool applied, object evidence)
        {
            lock (_lock)
            {
                Refresh();
                if (!_index.TryGetValue(Id(doc, key), out var r)) return false;
                if (_running.ContainsKey(Id(doc, key))) throw new InvalidOperationException("the operation is still running; wait for it to finish");
                MarkReconciled(r, applied ? "applied" : "not_applied", evidence);
                if (!applied)
                {
                    // Explicitly marked as not applied: a new attempt with the same fingerprint may run.
                    Write(new Dictionary<string, object> { ["ev"] = "end", ["key"] = key, ["doc"] = doc, ["status"] = Status.RolledBack, ["result"] = Trim(evidence) });
                }
                return true;
            }
        }

        private static object Trim(object result)
        {
            if (result == null) return null;
            var txt = Json.Write(result);
            return txt.Length <= 4000 ? result : new Dictionary<string, object> { ["summary"] = txt.Substring(0, 4000) + "…" };
        }

        public Record Get(string key, string doc)
        {
            lock (_lock)
            {
                try { Refresh(); } catch (JournalUnavailableException) { }
                return _index.TryGetValue(Id(doc, key), out var r) ? r : null;
            }
        }

        /// <summary>Records of a task (to resume after reconnecting).</summary>
        public List<Record> ForTask(string task)
        {
            lock (_lock)
            {
                try { Refresh(); } catch (JournalUnavailableException) { }
                var seen = new HashSet<Record>();
                var list = new List<Record>();
                foreach (var r in _index.Values) if (task != null && r.Task == task && seen.Add(r)) list.Add(r);
                return list;
            }
        }

        /// <summary>
        /// Content fingerprint of an operation (H03): recursive canonical arguments without control fields, plus the content
        /// actually resolved (the code of nombre=/archivo=, the recipe version…). Changing the file changes the fingerprint.
        /// </summary>
        public static string HashArgs(string op, IDictionary<string, object> args, string resolvedContent = null)
        {
            var canon = OpSemantics.Canonical(args ?? new Dictionary<string, object>());
            // "|contenido:" is a fixed domain separator of stored fingerprints: changing it would turn every retry after an upgrade into a conflict.
            var content = resolvedContent == null ? "" : "|contenido:" + Home.Sha256Hex(resolvedContent);
            return Home.Sha256Hex(op + "|" + canon + content, 16);
        }

        /// <summary>The fingerprint 0.9.3 and earlier computed for the same operation (legacy argument names and values). Remove after 0.9.5.</summary>
        public static string LegacyHashArgs(string op, IDictionary<string, object> args, string resolvedContent = null) =>
            HashArgs(LegacyAliases.LegacyOp(op), (IDictionary<string, object>)LegacyAliases.ToLegacy(args ?? new Dictionary<string, object>()), resolvedContent);
    }
}
