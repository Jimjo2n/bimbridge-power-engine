// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text;

namespace Bimbridge.Engine
{
    /// <summary>
    /// A session = one bridge connection (Claude Code, Codex…). It is what the operator called a "port".
    /// The AI never sees this identifier; only the add-in uses it for the queue, conflicts and the journal.
    /// </summary>
    public sealed class Session
    {
        public string Id { get; internal set; }
        public string ClientName { get; internal set; }
        public int ClientPid { get; internal set; }
        public DateTime ConnectedUtc { get; internal set; }
        public DateTime LastSeenUtc { get; internal set; }

        // ---- Nominal identity (consolidated review). See docs/0.9.0/AGENT_IDENTITY_PROTOCOL.md ----
        /// <summary>Agent alias ("Integration client"). Declared: never inferred or inherited from another session. Null = unnamed.</summary>
        public string AgentName { get; internal set; }
        public string ClientVersion { get; internal set; }
        public string Conversation { get; internal set; }
        public string Model { get; internal set; }
        /// <summary>Where the name came from: configuracion (bridge variable), registro (ctx agente=), hello or ninguno.</summary>
        public string IdentitySource { get; internal set; } = AgentIdentity.SourceNone;
        /// <summary>Windows user: verified by the pipe ACL (only that user can connect). The alias is NOT verified.</summary>
        public string Principal { get; internal set; }
        /// <summary>Issue #3: why the session has no name (e.g. alias in use by another session). Null if there is nothing to say.</summary>
        public string IdentityNote { get; internal set; }
        /// <summary>Issue #4: stable agent fingerprint (principal + alias). Null without an alias.</summary>
        public string AgentId => Correlation.AgentIdOf(Principal, AgentName);

        // ---- Counters (written by PipeHost; read from the window without blocking Revit) ----
        internal long _requests, _errors, _active, _totalMs;
        public long Requests => System.Threading.Interlocked.Read(ref _requests);
        public long Errors => System.Threading.Interlocked.Read(ref _errors);
        public long Active => System.Threading.Interlocked.Read(ref _active);
        public long TotalMs => System.Threading.Interlocked.Read(ref _totalMs);
        public string LastOp { get; internal set; }
        public string LastDoc { get; internal set; }

        public string DisplayName => AgentName ?? "(unnamed) · " + ClientName;

        /// <summary>Named sets ($l5_perimeter): ids saved by this session.</summary>
        public NamedSets Sets { get; } = new NamedSets();

        public override string ToString() => Id;
    }

    /// <summary>Agent identity rules: alias validation and verifiable presence state.</summary>
    public static class AgentIdentity
    {
        public const string SourceNone = "none";
        public const string SourceConfig = "configuration";
        public const string SourceRegister = "registration";
        public const string SourceHello = "hello";
        public const int MaxName = 80;

        /// <summary>Acceptable alias: 1..80 printable characters, no line breaks. Returns null if invalid.</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sb = new StringBuilder();
            foreach (var c in name.Trim()) if (!char.IsControl(c)) sb.Append(c);
            var s = sb.ToString().Trim();
            if (s.Length == 0) return null;
            return s.Length > MaxName ? s.Substring(0, MaxName) : s;
        }

        /// <summary>State of an open session: ejecutando (running), activo (request in the last 60 s) or inactivo (idle). Never "connected" by inference.</summary>
        public static string Presence(Session s, DateTime now) =>
            s.Active > 0 ? "running_now" : (now - s.LastSeenUtc).TotalSeconds <= 60 ? "active" : "inactive";
    }

    /// <summary>
    /// Session lifecycle against the idle threshold (issue #1). The logical session does not die because of a request's duration:
    /// it only expires on inactivity when it has no requests in progress. A broken connection (EOF/IOException) is always closed; whatever
    /// was writing keeps its own evidence (durable intent, unknown state, reconciliation) and is never retried automatically.
    /// </summary>
    public static class SessionLifecycle
    {
        public static bool ShouldExpireOnIdle(long activeRequests) => activeRequests <= 0;
    }

    public enum RegisterResult { Ok, Invalid, Duplicate }

    /// <summary>Session already closed, for the panel history (bounded).</summary>
    public sealed class ClosedSession
    {
        public string Id, AgentName, ClientName, IdentitySource, Reason;
        public DateTime ConnectedUtc, ClosedUtc;
        public long Requests, Errors;
    }

    public sealed class SessionRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;

        public SessionRegistry(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        public Session Open(string clientName, int clientPid)
        {
            lock (_lock)
            {
                // PR #22 review: unique identifier per session INSTANCE. Previously "S-cliente-pid" was reused when reconnecting with the same
                // pid and, since request_id = session_id#counter and the counter restarts, two different requests could share a request_id.
                // The readable prefix (client and pid, useful in the journal) is kept and a random 64-bit suffix is added.
                var baseId = "S-" + Slug(clientName) + "-" + (clientPid > 0 ? clientPid.ToString() : "0");
                string id;
                do id = baseId + "-" + NewInstanceSuffix();
                while (_sessions.ContainsKey(id) || _issued.Contains(id));
                _issued.Add(id);
                var t = _now();
                var s = new Session
                {
                    Id = id,
                    ClientName = string.IsNullOrWhiteSpace(clientName) ? "client" : clientName.Trim(),
                    ClientPid = clientPid,
                    ConnectedUtc = t,
                    LastSeenUtc = t,
                };
                _sessions[id] = s;
                return s;
            }
        }

        public void Touch(Session s)
        {
            lock (_lock) s.LastSeenUtc = _now();
        }

        /// <summary>Hello data: client version and, if the bridge has it configured, the agent alias.</summary>
        public void Describe(Session s, string clientVersion, string agent, string conversation, string model, string source, string principal)
        {
            lock (_lock)
            {
                s.ClientVersion = AgentIdentity.Clean(clientVersion);
                s.Principal = principal;
                var a = AgentIdentity.Clean(agent);
                if (a != null)
                {
                    var holder = HolderOf(s, a);
                    if (holder == null) { s.AgentName = a; s.IdentitySource = source ?? AgentIdentity.SourceHello; s.IdentityNote = null; }
                    else s.IdentityNote = "the alias \"" + a + "\" is already used by the active session " + holder.Id;
                }
                s.Conversation = AgentIdentity.Clean(conversation);
                s.Model = AgentIdentity.Clean(model);
            }
        }

        /// <summary>Explicit registration (ctx agente=…). Changes only THIS session. Returns false if the alias is invalid or in use.</summary>
        public bool Register(Session s, string agent, string conversation, string model) =>
            TryRegister(s, agent, conversation, model, out _) == RegisterResult.Ok;

        /// <summary>
        /// Issue #3: registration with duplicate detection. An alias already used by ANOTHER open session of another client process is rejected
        /// (prevents accidental impersonation and improper reassignment). The same bridge instance (same client and pid) reconnecting
        /// while its previous session has not closed yet may reuse it.
        /// </summary>
        public RegisterResult TryRegister(Session s, string agent, string conversation, string model, out Session holder)
        {
            holder = null;
            var a = AgentIdentity.Clean(agent);
            if (a == null) return RegisterResult.Invalid;
            lock (_lock)
            {
                holder = HolderOf(s, a);
                if (holder != null) return RegisterResult.Duplicate;
                s.AgentName = a;
                s.IdentitySource = AgentIdentity.SourceRegister;
                s.IdentityNote = null;
                var c = AgentIdentity.Clean(conversation); if (c != null) s.Conversation = c;
                var m = AgentIdentity.Clean(model); if (m != null) s.Model = m;
            }
            return RegisterResult.Ok;
        }

        /// <summary>Unregistration (ctx desregistrar=true): the session goes back to "unnamed" and loses reads and changes until it registers again.</summary>
        public void Unregister(Session s)
        {
            lock (_lock)
            {
                s.AgentName = null;
                s.IdentitySource = AgentIdentity.SourceNone;
                s.IdentityNote = "unregistered";
            }
        }

        /// <summary>Another open session already using that alias (case-insensitive), except the same client instance reconnecting.</summary>
        private Session HolderOf(Session s, string alias)
        {
            foreach (var o in _sessions.Values)
            {
                if (ReferenceEquals(o, s) || !string.Equals(o.AgentName, alias, StringComparison.OrdinalIgnoreCase)) continue;
                var sameInstance = o.ClientPid > 0 && o.ClientPid == s.ClientPid && string.Equals(o.ClientName, s.ClientName, StringComparison.Ordinal)
                                   && string.Equals(o.Principal, s.Principal, StringComparison.OrdinalIgnoreCase);
                if (!sameInstance) return o;
            }
            return null;
        }

        public void RequestStarted(Session s, string op, string doc)
        {
            System.Threading.Interlocked.Increment(ref s._active);
            lock (_lock) { s.LastOp = op; if (!string.IsNullOrEmpty(doc)) s.LastDoc = doc; }
        }

        public void RequestFinished(Session s, long ms, bool error)
        {
            System.Threading.Interlocked.Decrement(ref s._active);
            System.Threading.Interlocked.Increment(ref s._requests);
            System.Threading.Interlocked.Add(ref s._totalMs, ms);
            if (error) System.Threading.Interlocked.Increment(ref s._errors);
        }

        private readonly LinkedList<ClosedSession> _closed = new LinkedList<ClosedSession>();
        public const int ClosedMax = 50;

        public bool Close(Session s, string reason = "disconnected")
        {
            lock (_lock)
            {
                if (s == null || !_sessions.Remove(s.Id)) return false;
                _closed.AddLast(new ClosedSession
                {
                    Id = s.Id, AgentName = s.AgentName, ClientName = s.ClientName, IdentitySource = s.IdentitySource, Reason = reason,
                    ConnectedUtc = s.ConnectedUtc, ClosedUtc = _now(), Requests = s.Requests, Errors = s.Errors,
                });
                if (_closed.Count > ClosedMax) _closed.RemoveFirst();
                return true;
            }
        }

        /// <summary>Sessions closed in this Revit run (most recent last). A restart clears them: no ghost agents.</summary>
        public List<ClosedSession> Closed()
        {
            lock (_lock) return new List<ClosedSession>(_closed);
        }

        public List<Session> All()
        {
            lock (_lock) return new List<Session>(_sessions.Values);
        }

        public int Count { get { lock (_lock) return _sessions.Count; } }

        /// <summary>Ids issued in this run (open or closed): one is never issued again.</summary>
        private readonly HashSet<string> _issued = new HashSet<string>(StringComparer.Ordinal);

        private static string NewInstanceSuffix()
        {
            var b = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        internal static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "client";
            var sb = new StringBuilder();
            foreach (var c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 24) break;
            }
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? "client" : s;
        }
    }

    /// <summary>
    /// Named id sets, per session. They avoid resending hundreds of ids on every call.
    /// Since 0.5.0 each set is bound to the document where it was computed (handle dN): a set from A never resolves elements of B.
    /// </summary>
    public sealed class NamedSets
    {
        private sealed class Entry
        {
            public long[] Ids;
            public string Doc;
            public string DocTitle;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _sets = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Limit of sets per session (the oldest are not deleted automatically: a warning is given).</summary>
        public const int MaxSets = 256;

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("empty set name");
            var n = name.Trim();
            if (!n.StartsWith("$")) n = "$" + n;
            for (var i = 1; i < n.Length; i++)
            {
                var c = n[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    throw new ArgumentException("invalid set name: use letters, digits, _ or -");
            }
            if (n.Length < 2 || n.Length > 48) throw new ArgumentException("set name of 1 to 47 characters");
            return n;
        }

        /// <param name="doc">Handle of the document (dN) where those ids are valid; null = no document (compatibility).</param>
        public void Save(string name, IEnumerable<long> ids, string doc = null, string docTitle = null)
        {
            var key = Normalize(name);
            var arr = new List<long>(ids).ToArray();
            lock (_lock)
            {
                if (!_sets.ContainsKey(key) && _sets.Count >= MaxSets)
                    throw new InvalidOperationException("too many sets (" + MaxSets + "); delete some with sets drop");
                _sets[key] = new Entry { Ids = arr, Doc = doc, DocTitle = docTitle };
            }
        }

        // Selections the user copies from Revit ("Copy selection ID"): process-wide, so any session (any AI conversation) resolves them.
        private static readonly object PinnedLock = new object();
        private static readonly Dictionary<string, Entry> Pinned = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> PinnedOrder = new Queue<string>();
        public const string SelectionPrefix = "$sel-";

        /// <summary>
        /// Registers the user's Revit selection and returns its reference ($sel-xxxxxx). The same elements in the same document
        /// always get the same reference. Keeps the latest <see cref="MaxSets"/> selections for the life of the Revit process.
        /// </summary>
        public static string Pin(IEnumerable<long> ids, string doc, string docTitle)
        {
            var arr = new List<long>(ids);
            arr.Sort();
            string name;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((doc ?? "") + "|" + string.Join(",", arr)));
                // 6 bytes: a collision would silently repoint an older reference to other elements, so keep it practically impossible.
                name = SelectionPrefix + BitConverter.ToString(h, 0, 6).Replace("-", "").ToLowerInvariant();
            }
            lock (PinnedLock)
            {
                if (!Pinned.ContainsKey(name))
                {
                    PinnedOrder.Enqueue(name);
                    while (PinnedOrder.Count > MaxSets) Pinned.Remove(PinnedOrder.Dequeue());
                }
                Pinned[name] = new Entry { Ids = arr.ToArray(), Doc = doc, DocTitle = docTitle };
            }
            return name;
        }

        /// <summary>Forgets the selections of a document that was closed (its handle is never reused in this Revit run).</summary>
        public static void UnpinDoc(string doc)
        {
            if (doc == null) return;
            lock (PinnedLock)
            {
                var gone = new List<string>();
                foreach (var kv in Pinned) if (string.Equals(kv.Value.Doc, doc, StringComparison.OrdinalIgnoreCase)) gone.Add(kv.Key);
                foreach (var k in gone) Pinned.Remove(k);
            }
        }

        /// <summary>Ids are listed inline only up to this count; beyond it the reference alone keeps the pasted text short.</summary>
        public const int MaxInlineIds = 60;

        /// <summary>
        /// Text the user pastes into an AI conversation. It must stand on its own for an AI that has never seen it: what it is,
        /// where, what kinds, and how to use it. Example: BIMBRIDGE selection $sel-3fa91c: 4 elements in Revit document "Tower A"
        /// (d1). Conduits ×3 [1201, 1202, 1203]; Electrical Fixtures ×1 [1350]. Refer to them as $sel-3fa91c in any BIMBRIDGE
        /// command (for example ids=$sel-3fa91c).
        /// </summary>
        public static string DescribeSelection(string reference, string docTitle, string handle, IList<Tuple<string, List<long>>> groups)
        {
            var total = 0;
            foreach (var g in groups) total += g.Item2.Count;
            var inline = total <= MaxInlineIds;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            foreach (var g in groups)
                parts.Add(g.Item1 + " ×" + g.Item2.Count.ToString("N0", inv) + (inline ? " [" + string.Join(", ", g.Item2) + "]" : ""));
            return "BIMBRIDGE selection " + reference + ": " + total.ToString("N0", inv) + (total == 1 ? " element" : " elements") +
                   " in Revit document \"" + docTitle + "\"" + (handle != null ? " (" + handle + ")" : "") + ". " + string.Join("; ", parts) + "." +
                   (inline ? "" : " Element ids are omitted to keep this short.") +
                   " Refer to them as " + reference + " in any BIMBRIDGE command (for example ids=" + reference + ").";
        }

        /// <summary>Session set first, then a selection pinned by the user. Call inside <c>_lock</c>.</summary>
        private bool Find(string key, out Entry e)
        {
            if (_sets.TryGetValue(key, out e)) return true;
            lock (PinnedLock) return Pinned.TryGetValue(key, out e);
        }

        /// <summary>Without checking the document (only for listings and compatibility).</summary>
        public bool TryGet(string name, out long[] ids)
        {
            lock (_lock)
            {
                if (Find(Normalize(name), out var e)) { ids = e.Ids; return true; }
                ids = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves the set for document <paramref name="doc"/>. If it was saved in another document, returns false with the reason.
        /// </summary>
        public bool TryGet(string name, string doc, out long[] ids, out string error)
        {
            ids = null;
            error = null;
            lock (_lock)
            {
                if (!Find(Normalize(name), out var e))
                {
                    error = Normalize(name).StartsWith(SelectionPrefix, StringComparison.OrdinalIgnoreCase)
                        ? "Revit selection reference " + Normalize(name) + " is no longer available (Revit was restarted or it is old). " +
                          "Ask the user to select the elements again and press Copy selection ID on the BIMBRIDGE tab."
                        : "set " + name + " does not exist; use sets to see the saved ones";
                    return false;
                }
                if (doc != null && e.Doc != null && !string.Equals(doc, e.Doc, StringComparison.OrdinalIgnoreCase))
                {
                    error = "set " + Normalize(name) + " belongs to document " + e.Doc + (e.DocTitle != null ? " (" + e.DocTitle + ")" : "") +
                            ", not " + doc + ": its ids are not valid here. Query it again in this document or pass doc=" + e.Doc;
                    return false;
                }
                ids = e.Ids;
                return true;
            }
        }

        /// <summary>Document the set belongs to (null if unknown).</summary>
        public string DocOf(string name)
        {
            lock (_lock) return Find(Normalize(name), out var e) ? e.Doc : null;
        }

        public bool Remove(string name)
        {
            lock (_lock) return _sets.Remove(Normalize(name));
        }

        /// <summary>Deletes the sets of a document that was closed.</summary>
        public int RemoveDoc(string doc)
        {
            lock (_lock)
            {
                var gone = new List<string>();
                foreach (var kv in _sets) if (doc != null && string.Equals(kv.Value.Doc, doc, StringComparison.OrdinalIgnoreCase)) gone.Add(kv.Key);
                foreach (var k in gone) _sets.Remove(k);
                return gone.Count;
            }
        }

        public Dictionary<string, int> List()
        {
            lock (_lock)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in _sets) d[kv.Key] = kv.Value.Ids.Length;
                return d;
            }
        }

        /// <summary>Name → (count, document).</summary>
        public List<object[]> Describe()
        {
            lock (_lock)
            {
                var rows = new List<object[]>();
                foreach (var kv in _sets) rows.Add(new object[] { kv.Key, (long)kv.Value.Ids.Length, kv.Value.Doc ?? "" });
                return rows;
            }
        }

        public void Clear() { lock (_lock) _sets.Clear(); }
    }
}
