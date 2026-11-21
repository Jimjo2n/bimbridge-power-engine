// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// alpha3 (P1-B): the service's plan queue with ONE state per plan under a lock.
    /// Guarantees: (1) a plan never runs twice at once in this service; (2) an Enqueue that arrives during the run
    /// is not lost: it is repeated when the run ends; (3) enqueuing something already queued is idempotent and uses no capacity.
    /// Root cause of the earlier defect: the id was removed from "_queued" before taking "_slots"; an Enqueue in that gap or during
    /// the run put it back in the queue, another worker took it, could not take the slot and dropped it without warning.
    /// </summary>
    public sealed class PlanQueue
    {
        private enum St { Queued, Running, RunningThenRerun }
        private readonly Dictionary<string, St> _q = new Dictionary<string, St>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private readonly Channel<string> _ch;
        public Action<string, string> OnLost;   // (plan, reason): a reschedule that did not fit in the queue

        public PlanQueue(int capacity) =>
            _ch = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, capacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false });

        public int Queued { get { lock (_lock) return _q.Values.Count(v => v == St.Queued); } }
        public bool IsRunning(string id) { lock (_lock) return _q.TryGetValue(id, out var s) && s != St.Queued; }

        public bool Enqueue(string id)
        {
            lock (_lock)
            {
                if (_q.TryGetValue(id, out var st))
                {
                    if (st == St.Running) _q[id] = St.RunningThenRerun;
                    return true;
                }
                if (!_ch.Writer.TryWrite(id)) return false;
                _q[id] = St.Queued;
                return true;
            }
        }

        /// <summary>Worker loop: takes a plan, runs it and releases it (re-enqueuing it if an Enqueue arrived while it ran).</summary>
        public async Task WorkerLoop(Action<string> run, CancellationToken stop)
        {
            try
            {
                while (await _ch.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
                {
                    if (!_ch.Reader.TryRead(out var id)) continue;
                    lock (_lock)
                    {
                        if (!_q.TryGetValue(id, out var st) || st != St.Queued) { OnLost?.Invoke(id, "cola_inconsistente"); continue; }
                        _q[id] = St.Running;
                    }
                    try { run(id); }
                    finally { Release(id, stop); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Release(string id, CancellationToken stop)
        {
            bool again;
            lock (_lock)
            {
                again = _q.TryGetValue(id, out var st) && st == St.RunningThenRerun && !stop.IsCancellationRequested;
                _q.Remove(id);
                if (again && _ch.Writer.TryWrite(id)) { _q[id] = St.Queued; return; }
            }
            if (again) OnLost?.Invoke(id, "queue full; recovered on restart or with reanudar");
        }
    }
}
