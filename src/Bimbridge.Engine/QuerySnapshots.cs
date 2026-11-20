// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Bimbridge.Engine
{
    /// <summary>Result of looking up a query cursor.</summary>
    public enum SnapshotLookup
    {
        Ok,
        /// <summary>Does not exist, expired or belongs to another session (not distinguished, so other sessions' cursors are not leaked).</summary>
        Unknown,
        /// <summary>The document revision changed since the snapshot was taken: repeat the query.</summary>
        Stale,
        /// <summary>The request targets a document other than the snapshot's.</summary>
        OtherDocument,
        /// <summary>Belonged to this session but was discarded (memory budget, expiry or document closed): repeat the query.</summary>
        Evicted,
    }

    /// <summary>Already-filtered rows of a query, bound to session, document and revision (issue #2).</summary>
    public sealed class QuerySnapshot
    {
        public string Id { get; internal set; }
        public string SessionId { get; internal set; }
        public string DocId { get; internal set; }
        public long Rev { get; internal set; }
        public QuerySpec Spec { get; internal set; }
        public IReadOnlyList<Dictionary<string, object>> Rows { get; internal set; }
        public long Cells { get; internal set; }
        /// <summary>ESTIMATED size of the stored rows (see <see cref="QuerySnapshotStore.EstimateBytes"/>). Not a heap measurement.</summary>
        public long EstimatedBytes { get; internal set; }
        internal Func<long> LiveRev;
        internal DateTime LastUseUtc;
    }

    /// <summary>
    /// Snapshots of paged queries (issue #2). The first page reads the model ONCE on the Revit thread; subsequent pages
    /// are served with "query cursor=… page=N" from these rows, without going back to Revit.
    /// Guarantees: a cursor is valid only for the session that created it, for the same document and while its revision does not change
    /// (if it changed, it is rejected with Stale and discarded: rows from two revisions are never mixed). They expire on inactivity (TTL).
    ///
    /// Memory budget (PR #22 review): measured in estimated BYTES, not cells. A cell with a 50 000-character text
    /// no longer counts the same as a numeric cell. Limits: global, per document, per session (count) and per snapshot. A compact
    /// projection is stored (only the requested columns, simple values; never Revit API objects). The figure is a conservative ESTIMATE
    /// of the managed size (object headers, UTF-16 strings, boxed values, dictionaries); it is not a heap measurement
    /// nor an exact guarantee. A cursor discarded by budget, expiry or document close is reported as such (Evicted).
    /// </summary>
    public sealed class QuerySnapshotStore
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        public const long DefaultMaxBytes = 64L * 1024 * 1024;
        public const long DefaultMaxBytesPerDocument = 32L * 1024 * 1024;
        public const int DefaultMaxPerSession = 4;
        private const int EvictedMemory = 512;

        private readonly object _lock = new object();
        private readonly Dictionary<string, QuerySnapshot> _items = new Dictionary<string, QuerySnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, string>> _evicted = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        private readonly Queue<string> _evictedOrder = new Queue<string>();
        private readonly TimeSpan _ttl;
        private readonly long _maxBytes, _maxDocBytes;
        private readonly int _maxPerSession;
        private readonly Func<DateTime> _clock;
        private long _cells, _bytes;

        public QuerySnapshotStore(TimeSpan? ttl = null, long maxBytes = DefaultMaxBytes, long maxBytesPerDocument = DefaultMaxBytesPerDocument,
            int maxPerSession = DefaultMaxPerSession, Func<DateTime> clock = null)
        {
            _ttl = ttl ?? DefaultTtl;
            _maxBytes = maxBytes;
            _maxDocBytes = Math.Min(maxBytesPerDocument, maxBytes);
            _maxPerSession = Math.Max(1, maxPerSession);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Count { get { lock (_lock) return _items.Count; } }
        public long Cells { get { lock (_lock) return _cells; } }
        /// <summary>Sum of the estimated sizes of the stored snapshots.</summary>
        public long EstimatedBytes { get { lock (_lock) return _bytes; } }
        public long MaxBytes => _maxBytes;
        public TimeSpan Ttl => _ttl;

        /// <summary>
        /// Conservative estimate of the managed size of simple rows (x64): dictionary (header, tables) + per entry (entry and bucket)
        /// + per value (string = header + 2 bytes per character; boxed number/boolean; others as their text). Shared keys
        /// (field names) are not counted per row.
        /// </summary>
        public static long EstimateBytes(IReadOnlyList<Dictionary<string, object>> rows)
        {
            long total = 24 + 8L * rows.Count;                     // the list and its references
            foreach (var r in rows) total += RowBytes(r);
            return total;
        }

        /// <summary>Estimated size of a row: Dictionary&lt;string,object&gt; (object + arrays + entry/bucket per key) + its values.</summary>
        private static long RowBytes(Dictionary<string, object> r)
        {
            long b = 80 + 32L * r.Count;
            foreach (var v in r.Values) b += ValueBytes(v);
            return b;
        }

        /// <summary>
        /// AUD-03: projection with an INCREMENTAL budget. Estimates row by row while projecting and stops as soon as it exceeds
        /// <paramref name="limit"/>: it never builds in full a projection that will not fit (previously everything was projected and then
        /// rejected). Returns null if it does not fit; <paramref name="bytes"/> holds the estimate reached. Does not trim rows: all or nothing.
        /// </summary>
        public static List<Dictionary<string, object>> ProjectWithinBudget(QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows, long limit, out long bytes)
        {
            bytes = 24 + 8L * rows.Count;
            if (bytes > limit) return null;                         // early rejection: not even the references fit
            var list = new List<Dictionary<string, object>>(rows.Count);
            foreach (var r in rows)
            {
                var d = ProjectRow(spec, r);
                bytes += RowBytes(d);
                if (bytes > limit) { list.Clear(); return null; }   // release what was built as soon as we know it will not fit
                list.Add(d);
            }
            return list;
        }

        private static Dictionary<string, object> ProjectRow(QuerySpec spec, Dictionary<string, object> r)
        {
            var d = new Dictionary<string, object>(spec.Fields.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in spec.Fields)
            {
                if (r.TryGetValue(f, out var v)) { d[f] = v; continue; }
                foreach (var kv in r) if (string.Equals(kv.Key, f, StringComparison.OrdinalIgnoreCase)) { d[f] = kv.Value; break; }
            }
            return d;
        }

        private static long ValueBytes(object v)
        {
            switch (v)
            {
                case null: return 0;
                case string s: return 26 + 2L * s.Length;
                case long _: case int _: case double _: case bool _: case float _: case decimal _: return 24;
                default: return 32 + 2L * (Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Length ?? 0);
            }
        }

        /// <summary>Compact projection: only the columns the page will return. Filters and units were already applied.</summary>
        public static List<Dictionary<string, object>> Project(QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows)
        {
            var list = new List<Dictionary<string, object>>(rows.Count);
            foreach (var r in rows) list.Add(ProjectRow(spec, r));
            return list;
        }

        /// <summary>
        /// Stores the snapshot and returns its cursor. null if it does not fit in the budget, with the reason in <paramref name="refusal"/>
        /// (the response passes it on to the agent; classic paging is still available). <paramref name="liveRev"/> returns the CURRENT
        /// revision of the document (thread-safe).
        /// </summary>
        public string Put(string sessionId, string docId, long rev, QuerySpec spec, IReadOnlyList<Dictionary<string, object>> rows, Func<long> liveRev, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId");
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (liveRev == null) throw new ArgumentNullException(nameof(liveRev));
            // AUD-03: incremental projection with early rejection; the limit is the per-document budget.
            var compact = ProjectWithinBudget(spec, rows, _maxDocBytes, out var bytes);
            if (compact == null)
            {
                refusal = "the result takes ~" + Mb(bytes) + " MB (estimated) and exceeds the snapshot budget (" + Mb(_maxDocBytes) +
                          " MB per document): no cursor; narrow the query (f=, where, group_by) or page without a cursor";
                return null;
            }
            var snap = new QuerySnapshot
            {
                Id = NewId(), SessionId = sessionId, DocId = docId, Rev = rev, Spec = spec, Rows = compact,
                Cells = (long)compact.Count * Math.Max(1, spec.Fields.Count), EstimatedBytes = bytes, LiveRev = liveRev, LastUseUtc = _clock(),
            };
            lock (_lock)
            {
                PruneExpired();
                // Per-session limit: its least recently used snapshots are discarded.
                var mine = _items.Values.Where(s => s.SessionId == sessionId).OrderBy(s => s.LastUseUtc).ToList();
                for (var i = 0; i <= mine.Count - _maxPerSession; i++) Remove(mine[i].Id, "per-session snapshot limit");
                // Per-document and global budget: LRU within the scope that is exceeded.
                while (_items.Values.Where(s => SameDoc(s.DocId, docId)).Sum(s => s.EstimatedBytes) + bytes > _maxDocBytes)
                    Remove(_items.Values.Where(s => SameDoc(s.DocId, docId)).OrderBy(s => s.LastUseUtc).First().Id, "document memory budget");
                while (_bytes + bytes > _maxBytes && _items.Count > 0)
                    Remove(_items.Values.OrderBy(s => s.LastUseUtc).First().Id, "global memory budget");
                _items[snap.Id] = snap;
                _cells += snap.Cells;
                _bytes += bytes;
            }
            return snap.Id;
        }

        private static bool SameDoc(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Mb(long b) => (b / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Reason a cursor of this session was discarded, if remembered (null otherwise).</summary>
        public string EvictionReason(string id, string sessionId)
        {
            lock (_lock) return id != null && _evicted.TryGetValue(id, out var e) && e.Key == sessionId ? e.Value : null;
        }

        /// <summary>Looks up a cursor for <paramref name="sessionId"/>. <paramref name="targetDocId"/> null = no explicit target.</summary>
        public SnapshotLookup Get(string id, string sessionId, string targetDocId, out QuerySnapshot snapshot)
        {
            snapshot = null;
            QuerySnapshot s;
            lock (_lock)
            {
                PruneExpired();
                if (string.IsNullOrEmpty(id) || !_items.TryGetValue(id, out s) || s.SessionId != sessionId)
                    return id != null && _evicted.TryGetValue(id, out var ev) && ev.Key == sessionId ? SnapshotLookup.Evicted : SnapshotLookup.Unknown;
                if (targetDocId != null && s.DocId != null && !string.Equals(targetDocId, s.DocId, StringComparison.OrdinalIgnoreCase))
                    return SnapshotLookup.OtherDocument;
            }
            long live;
            try { live = s.LiveRev(); }
            catch (Exception) { live = long.MinValue; }   // cannot be checked: fail-closed
            lock (_lock)
            {
                // AUD-02: between the first lock and this one (LiveRev runs outside the lock) another thread may have discarded the snapshot
                // (DropSession, DropDocument, budget, expiry). It is valid only if it is still EXACTLY the same authorized object.
                if (!_items.TryGetValue(s.Id, out var current) || !ReferenceEquals(current, s))
                    return _evicted.TryGetValue(s.Id, out var ev) && ev.Key == sessionId ? SnapshotLookup.Evicted : SnapshotLookup.Unknown;
                if (live != s.Rev)
                {
                    Remove(s.Id, null);
                    return SnapshotLookup.Stale;
                }
                s.LastUseUtc = _clock();
            }
            snapshot = s;
            return SnapshotLookup.Ok;
        }

        public void DropSession(string sessionId)
        {
            lock (_lock) foreach (var s in _items.Values.Where(x => x.SessionId == sessionId).ToList()) Remove(s.Id, null);
        }

        public void DropDocument(string docId)
        {
            lock (_lock)
                foreach (var s in _items.Values.Where(x => string.Equals(x.DocId, docId, StringComparison.OrdinalIgnoreCase)).ToList()) Remove(s.Id, "the document was closed");
        }

        private void PruneExpired()
        {
            var now = _clock();
            foreach (var s in _items.Values.Where(x => now - x.LastUseUtc > _ttl).ToList()) Remove(s.Id, "expired (" + (int)_ttl.TotalMinutes + " min unused)");
        }

        /// <param name="reason">Reason remembered to inform the agent; null = do not remember (revision changed or session closed).</param>
        private void Remove(string id, string reason)
        {
            if (!_items.TryGetValue(id, out var s)) return;
            _items.Remove(id); _cells -= s.Cells; _bytes -= s.EstimatedBytes;
            if (reason == null) return;
            _evicted[id] = new KeyValuePair<string, string>(s.SessionId, reason);
            _evictedOrder.Enqueue(id);
            while (_evictedOrder.Count > EvictedMemory) _evicted.Remove(_evictedOrder.Dequeue());
        }

        private static string NewId()
        {
            var b = new byte[9];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return "q" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}
