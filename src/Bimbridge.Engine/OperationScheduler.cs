// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bimbridge.Engine
{
    /// <summary>The long-operation queue is full: the request is rejected instead of piling up without limit.</summary>
    public sealed class SchedulerBusyException : Exception
    {
        public SchedulerBusyException(string message) : base(message) { }
    }

    /// <summary>
    /// 0.9.1 (Block I): scheduler for long operations across agents. It runs nothing on its own: it decides WHEN each operation
    /// may start so that one agent does not hog the engine.
    ///  - global and per-owner limit on running operations;
    ///  - optional exclusive key (e.g. the document): two operations with the same key never run at the same time;
    ///  - fair share: among those that can start, first the one whose owner has the fewest running and, on a tie, the oldest;
    ///  - bounded queue: when full it rejects (fails closed), it does not wait indefinitely;
    ///  - cancelling a queued operation removes it before it started (nothing to reconcile).
    /// The Revit thread still has its own queue (MainThread); this orders the work before it reaches it.
    /// </summary>
    public sealed class OperationScheduler
    {
        public int MaxRunning = 4;
        public int MaxPerOwner = 2;
        public int MaxQueued = 64;

        private sealed class Waiter
        {
            public string Owner, Key;
            public long Seq;
            public TaskCompletionSource<bool> Go = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly object _lock = new object();
        private readonly List<Waiter> _queue = new List<Waiter>();
        private readonly Dictionary<string, int> _runningByOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _heldKeys = new HashSet<string>(StringComparer.Ordinal);
        private int _running;
        private long _seq, _started, _rejected, _cancelledQueued, _maxQueuedSeen;

        public async Task<T> Run<T>(string owner, string exclusiveKey, Func<Task<T>> body, CancellationToken ct)
        {
            owner = owner ?? "";
            var w = new Waiter { Owner = owner, Key = string.IsNullOrEmpty(exclusiveKey) ? null : exclusiveKey };
            lock (_lock)
            {
                ct.ThrowIfCancellationRequested();
                if (_queue.Count >= MaxQueued)
                {
                    _rejected++;
                    throw new SchedulerBusyException("there are " + _queue.Count + " long operations queued (maximum " + MaxQueued + "): wait for them to finish or cancel one");
                }
                w.Seq = ++_seq;
                _queue.Add(w);
                if (_queue.Count > _maxQueuedSeen) _maxQueuedSeen = _queue.Count;
                Dispatch();
            }
            using (ct.Register(() =>
            {
                lock (_lock)
                {
                    // Only cancelled while still queued; once started, cancellation is up to the body (its own token).
                    if (_queue.Remove(w)) { _cancelledQueued++; w.Go.TrySetCanceled(); }
                }
            }))
            {
                await w.Go.Task.ConfigureAwait(false);
            }
            try { return await body().ConfigureAwait(false); }
            finally
            {
                lock (_lock)
                {
                    _running--;
                    _runningByOwner[owner]--;
                    if (_runningByOwner[owner] == 0) _runningByOwner.Remove(owner);
                    if (w.Key != null) _heldKeys.Remove(w.Key);
                    Dispatch();
                }
            }
        }

        /// <summary>Starts every operation that can start now (call with the lock held).</summary>
        private void Dispatch()
        {
            while (_running < MaxRunning)
            {
                Waiter next = null;
                var best = int.MaxValue;
                foreach (var w in _queue)   // _queue is in arrival order: on a tie the oldest wins
                {
                    var mine = _runningByOwner.TryGetValue(w.Owner, out var n) ? n : 0;
                    if (mine >= MaxPerOwner) continue;
                    if (w.Key != null && _heldKeys.Contains(w.Key)) continue;
                    if (mine < best) { best = mine; next = w; }
                }
                if (next == null) return;
                _queue.Remove(next);
                _running++;
                _runningByOwner[next.Owner] = best + 1;
                if (next.Key != null) _heldKeys.Add(next.Key);
                _started++;
                next.Go.TrySetResult(true);
            }
        }

        public Dictionary<string, object> Stats()
        {
            lock (_lock)
                return new Dictionary<string, object>
                {
                    ["en_curso"] = (long)_running, ["en_cola"] = (long)_queue.Count, ["max_en_curso"] = (long)MaxRunning, ["max_por_agente"] = (long)MaxPerOwner,
                    ["max_en_cola"] = (long)MaxQueued, ["iniciadas"] = _started, ["rechazadas_por_cola_llena"] = _rejected, ["canceladas_en_cola"] = _cancelledQueued,
                    ["cola_maxima_vista"] = _maxQueuedSeen, ["agentes_en_curso"] = (long)_runningByOwner.Count,
                };
        }
    }
}
