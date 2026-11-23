// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Per-document coordination (0.6.0, §4). A long task or a barrier (save, sync, export, load family) takes the document;
    /// while it holds it, writes from other clients return documento_ocupado instead of interleaving. Reads are not blocked.
    /// - All or nothing and in order (sorted keys): a multi-document script never ends up holding half, and two owners never block
    ///   each other (no deadlock).
    /// - Reentrant for the same owner (the task can run its barriers).
    /// </summary>
    public sealed class DocLocks
    {
        public sealed class Holder
        {
            public string Owner;
            public string Kind;
            public string Detail;
            public DateTime SinceUtc;
            internal int Depth;

            public string Describe() => Kind + " " + Owner + (string.IsNullOrEmpty(Detail) ? "" : " (" + Detail + ")") +
                                        " since " + SinceUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Holder> _held = new Dictionary<string, Holder>(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;

        public DocLocks(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        /// <summary>Tries to take all documents at once. If another owner holds any of them, takes none and returns who holds it.</summary>
        public bool TryAcquire(IEnumerable<string> docs, string owner, string kind, string detail, out Holder blocking)
        {
            blocking = null;
            var keys = Normalize(docs);
            lock (_lock)
            {
                foreach (var k in keys)
                    if (_held.TryGetValue(k, out var h) && h.Owner != owner) { blocking = h; return false; }
                foreach (var k in keys)
                {
                    if (_held.TryGetValue(k, out var h)) h.Depth++;
                    else _held[k] = new Holder { Owner = owner, Kind = kind, Detail = detail, SinceUtc = _now(), Depth = 1 };
                }
                return true;
            }
        }

        /// <summary>Waits (bounded) until the documents can be taken. Checks every 100 ms; never leaves a partial acquisition.</summary>
        public async Task<Holder> AcquireAsync(IEnumerable<string> docs, string owner, string kind, string detail, TimeSpan wait, CancellationToken ct)
        {
            var keys = Normalize(docs);
            var until = DateTime.UtcNow + wait;
            while (true)
            {
                if (TryAcquire(keys, owner, kind, detail, out var blocking)) return null;
                if (DateTime.UtcNow >= until) return blocking;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        public void Release(IEnumerable<string> docs, string owner)
        {
            lock (_lock)
            {
                foreach (var k in Normalize(docs))
                    if (_held.TryGetValue(k, out var h) && h.Owner == owner && --h.Depth <= 0) _held.Remove(k);
            }
        }

        /// <summary>Who holds the document, or null. If <paramref name="exceptOwner"/> holds it, also null.</summary>
        public Holder HeldByOther(string doc, string exceptOwner)
        {
            if (doc == null) return null;
            lock (_lock) return _held.TryGetValue(doc, out var h) && h.Owner != exceptOwner ? h : null;
        }

        public List<KeyValuePair<string, Holder>> All()
        {
            lock (_lock) return _held.ToList();
        }

        private static List<string> Normalize(IEnumerable<string> docs) =>
            (docs ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
    }
}
