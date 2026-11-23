// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Single write queue with fair turns between sessions (section 9.2).
    /// Each queue item is a sub-batch. The scheduler takes one from session A,
    /// then one from B, etc., so one agent's large plan does not block another.
    /// A closed barrier (save, sync, export) stops handing out new sub-batches.
    /// </summary>
    public sealed class WriteScheduler<T>
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Queue<T>> _queues = new Dictionary<string, Queue<T>>(StringComparer.Ordinal);
        private readonly List<string> _ring = new List<string>();
        private int _cursor; // index of the next candidate in _ring
        private string _barrierReason;

        public void Enqueue(string sessionId, T item)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            lock (_lock)
            {
                if (!_queues.TryGetValue(sessionId, out var q))
                {
                    q = new Queue<T>();
                    _queues[sessionId] = q;
                    _ring.Add(sessionId);
                }
                q.Enqueue(item);
            }
        }

        /// <summary>Hands out the next sub-batch by turn. False if there is nothing or the barrier is closed.</summary>
        public bool TryDequeue(out string sessionId, out T item)
        {
            lock (_lock)
            {
                sessionId = null;
                item = default(T);
                if (_barrierReason != null || _ring.Count == 0) return false;
                for (var step = 0; step < _ring.Count; step++)
                {
                    var idx = (_cursor + step) % _ring.Count;
                    var sid = _ring[idx];
                    var q = _queues[sid];
                    if (q.Count == 0) continue;
                    item = q.Dequeue();
                    sessionId = sid;
                    if (q.Count == 0)
                    {
                        // the session leaves the ring; the cursor stays at the same index (which is now the next one)
                        _queues.Remove(sid);
                        _ring.RemoveAt(idx);
                        _cursor = _ring.Count == 0 ? 0 : idx % _ring.Count;
                    }
                    else
                    {
                        _cursor = (idx + 1) % _ring.Count;
                    }
                    return true;
                }
                return false;
            }
        }

        /// <summary>How many sub-batches will be handed out before this session's first pending one gets its turn.</summary>
        public int PositionOf(string sessionId)
        {
            lock (_lock)
            {
                if (!_queues.ContainsKey(sessionId)) return -1;
                // simulate the turn without modifying the queue
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in _queues) counts[kv.Key] = kv.Value.Count;
                var ring = new List<string>(_ring);
                var cursor = _cursor;
                var served = 0;
                while (ring.Count > 0)
                {
                    var idx = cursor % ring.Count;
                    var sid = ring[idx];
                    if (sid == sessionId) return served;
                    served++;
                    counts[sid]--;
                    if (counts[sid] == 0) { ring.RemoveAt(idx); cursor = ring.Count == 0 ? 0 : idx % ring.Count; }
                    else cursor = (idx + 1) % ring.Count;
                }
                return served;
            }
        }

        /// <summary>Removes everything pending for a session (disconnect). Returns what was removed.</summary>
        public List<T> RemoveSession(string sessionId)
        {
            lock (_lock)
            {
                var removed = new List<T>();
                if (!_queues.TryGetValue(sessionId, out var q)) return removed;
                removed.AddRange(q);
                _queues.Remove(sessionId);
                var idx = _ring.IndexOf(sessionId);
                _ring.RemoveAt(idx);
                if (_ring.Count == 0) _cursor = 0;
                else if (idx < _cursor) _cursor--;
                if (_cursor >= _ring.Count) _cursor = 0;
                return removed;
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    var n = 0;
                    foreach (var q in _queues.Values) n += q.Count;
                    return n;
                }
            }
        }

        /// <summary>Closes the document barrier. Pending items stay queued, not handed out.</summary>
        public void CloseBarrier(string reason)
        {
            lock (_lock) _barrierReason = string.IsNullOrEmpty(reason) ? "document operation" : reason;
        }

        public void OpenBarrier()
        {
            lock (_lock) _barrierReason = null;
        }

        /// <summary>Reason for the closed barrier, or null if it is open.</summary>
        public string BarrierReason { get { lock (_lock) return _barrierReason; } }
    }
}
