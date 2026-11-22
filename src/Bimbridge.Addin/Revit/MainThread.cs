// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// The only gateway to the Revit main thread (architecture section 4).
    /// - A single ExternalEvent for everything (pyRevit also used one, but one request per visit: D3).
    /// - Each visit serves work until the budget runs out (250 ms by default) and raises the event again if
    ///   anything is left. Between visits Revit processes the UI: the user never sees Revit "frozen" by the MCP (C8).
    /// - Work is resumable: a function that advances one slice and says whether it finished.
    /// - Fair sharing: new work (a query, the context) starts on the next visit even if a long check is in
    ///   progress; long ones take turns. Previously, a 30 s check kept any other request (and the other AI) waiting.
    /// - No exception escapes into Revit.
    /// - Measures each visit: the efficiency audit uses these numbers (visits, longest slice, busy time).
    /// </summary>
    public sealed class MainThread : IExternalEventHandler
    {
        private readonly ConcurrentQueue<WorkItem> _high = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<WorkItem> _normal = new ConcurrentQueue<WorkItem>();
        /// <summary>Started jobs that did not finish in their slice. Only touched on the Revit thread.</summary>
        private readonly List<WorkItem> _active = new List<WorkItem>();
        private readonly Action<string> _log;
        private ExternalEvent _event;
        private readonly int _budgetMs;
        private int _rr;

        // Global statistics (read from any thread; only the Revit thread writes)
        private long _visits, _busyMs, _maxVisitMs, _overBudget, _done, _cancelled, _failed;

        public MainThread(int budgetMs, Action<string> log)
        {
            _budgetMs = budgetMs;
            _log = log ?? (_ => { });
        }

        public int BudgetMs => _budgetMs;

        /// <summary>Must be called from OnStartup (valid API context).</summary>
        public void Create() { _event = ExternalEvent.Create(this); }

        public string GetName() => "BIMBRIDGE";

        /// <summary>
        /// Runs a resumable job on the Revit thread. <paramref name="step"/> receives the UIApplication, the visit clock
        /// and the time it has left; returns true when it finished. If Revit does not start the job within <paramref name="busyAfter"/>
        /// (dialog open, command in progress), it is discarded without running and "busy" is reported.
        /// </summary>
        public async Task<WorkResult> RunAsync(Func<UIApplication, Stopwatch, int, bool> step, bool highPriority, TimeSpan busyAfter, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return WorkResult.Make(WorkResult.KindCancelled, null, null);
            var item = new WorkItem(step, highPriority) { VisitsAtEnqueue = Interlocked.Read(ref _visits) };
            (highPriority ? _high : _normal).Enqueue(item);
            item.RaiseResult = Raise();

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(busyAfter, timer.Token);
                var first = await Task.WhenAny(item.Started.Task, delay).ConfigureAwait(false);
                timer.Cancel(); // do not leave the timer alive
                if (first != item.Started.Task)
                {
                    // If it just started, let it finish; otherwise, mark it so it never runs.
                    if (Interlocked.CompareExchange(ref item.State, WorkItem.Cancelled, WorkItem.Waiting) == WorkItem.Waiting)
                    {
                        // Not started: how many visits happened while waiting distinguishes "Revit did not visit" from "visited but it was not its turn".
                        item.VisitsBeforeStart = Interlocked.Read(ref _visits) - item.VisitsAtEnqueue;
                        item.WaitMs = item.ElapsedMs();
                        return WorkResult.Make(ct.IsCancellationRequested ? WorkResult.KindCancelled : WorkResult.KindBusy, null, item);
                    }
                }
            }
            using (ct.Register(() => item.CancelRequested = true))
            {
                var result = await item.Done.Task.ConfigureAwait(false);
                // Revit may have finished it inside an ongoing visit before Raise() returned its response.
                result.RaiseResult = result.RaiseResult ?? item.RaiseResult;
                return result;
            }
        }

        private string Raise()
        {
            var ev = _event;
            if (ev == null) return "no_event";
            var r = ev.Raise();
            if (r == ExternalEventRequest.Denied || r == ExternalEventRequest.TimedOut)
                _log("ExternalEvent.Raise: " + r);
            return r.ToString();
        }

        public void Execute(UIApplication app)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // 1) new urgent work (context, queries), 2) urgent work already started,
                // 3) new normal work (writes, checks), 4) long work already started, in turns.
                while (sw.ElapsedMilliseconds < _budgetMs && _high.TryDequeue(out var h)) Start(app, h, sw);
                RunActive(app, sw, highOnly: true);
                while (sw.ElapsedMilliseconds < _budgetMs && _normal.TryDequeue(out var n)) Start(app, n, sw);
                RunActive(app, sw, highOnly: false);
            }
            catch (Exception ex)
            {
                _log("MainThread.Execute: " + ex);
            }
            finally
            {
                var ms = sw.ElapsedMilliseconds;
                _visits++;
                _busyMs += ms;
                if (ms > _maxVisitMs) _maxVisitMs = ms;
                if (ms > _budgetMs * 2) _overBudget++;
                if (_active.Count > 0 || !_high.IsEmpty || !_normal.IsEmpty) Raise();
            }
        }

        private void Start(UIApplication app, WorkItem item, Stopwatch sw)
        {
            if (item.State == WorkItem.Cancelled || item.CancelRequested) { Finish(item, WorkResult.KindCancelled, null); return; }
            if (Interlocked.CompareExchange(ref item.State, WorkItem.Running, WorkItem.Waiting) != WorkItem.Waiting)
            {
                // The request thread cancelled it just now (the "busy" deadline passed).
                if (item.State == WorkItem.Cancelled) Interlocked.Increment(ref _cancelled);
                return;
            }
            item.WaitMs = item.ElapsedMs();
            item.VisitsBeforeStart = _visits - item.VisitsAtEnqueue;   // the current visit has not been counted yet
            item.Started.TrySetResult(true);
            if (!Slice(app, item, sw)) _active.Add(item);
        }

        private void RunActive(UIApplication app, Stopwatch sw, bool highOnly)
        {
            if (_active.Count == 0) return;
            var n = _active.Count;
            for (var k = 0; k < n && sw.ElapsedMilliseconds < _budgetMs && _active.Count > 0; k++)
            {
                if (_rr >= _active.Count) _rr = 0;
                var item = _active[_rr];
                if (highOnly && !item.High) { _rr++; continue; }
                if (item.CancelRequested)
                {
                    _active.RemoveAt(_rr);
                    Finish(item, WorkResult.KindCancelled, null);
                    continue;
                }
                if (Slice(app, item, sw)) _active.RemoveAt(_rr);
                else _rr++;
            }
        }

        /// <summary>One slice of a job. true = finished (successfully or with an error).</summary>
        private bool Slice(UIApplication app, WorkItem item, Stopwatch sw)
        {
            var t0 = sw.ElapsedMilliseconds;
            bool finished;
            try
            {
                var remaining = (int)Math.Max(10, _budgetMs - t0);
                finished = item.Step(app, sw, remaining);
            }
            catch (Exception ex)
            {
                Account(item, sw.ElapsedMilliseconds - t0);
                Finish(item, WorkResult.KindFailed, ex);
                return true;
            }
            Account(item, sw.ElapsedMilliseconds - t0);
            if (finished) Finish(item, WorkResult.KindCompleted, null);
            return finished;
        }

        private static void Account(WorkItem item, long ms)
        {
            item.Slices++;
            item.BusyMs += ms;
            if (ms > item.MaxSliceMs) item.MaxSliceMs = ms;
        }

        private void Finish(WorkItem item, string kind, Exception ex)
        {
            if (kind == WorkResult.KindCompleted) Interlocked.Increment(ref _done);
            else if (kind == WorkResult.KindCancelled) Interlocked.Increment(ref _cancelled);
            else Interlocked.Increment(ref _failed);
            item.Done.TrySetResult(WorkResult.Make(kind, ex, item));
        }

        /// <summary>Revit thread numbers since the add-in started (for the audit and the status).</summary>
        public Dictionary<string, object> Stats() => new Dictionary<string, object>
        {
            ["visits"] = Interlocked.Read(ref _visits),
            ["busy_ms"] = Interlocked.Read(ref _busyMs),
            ["max_visit_ms"] = Interlocked.Read(ref _maxVisitMs),
            ["long_visits"] = Interlocked.Read(ref _overBudget),
            ["budget_ms"] = (long)_budgetMs,
            ["jobs"] = Interlocked.Read(ref _done),
            ["cancelled_items"] = Interlocked.Read(ref _cancelled),
            ["failed_items"] = Interlocked.Read(ref _failed),
        };

        public void Dispose()
        {
            foreach (var i in _active) i.Done.TrySetResult(WorkResult.Make(WorkResult.KindCancelled, null, i));
            _active.Clear();
            while (_high.TryDequeue(out var i)) i.Done.TrySetResult(WorkResult.Make(WorkResult.KindCancelled, null, i));
            while (_normal.TryDequeue(out var i)) i.Done.TrySetResult(WorkResult.Make(WorkResult.KindCancelled, null, i));
            _event?.Dispose();
            _event = null;
        }

        internal sealed class WorkItem
        {
            public const int Waiting = 0, Running = 1, Cancelled = 2;
            public readonly Func<UIApplication, Stopwatch, int, bool> Step;
            public readonly bool High;
            public readonly long CreatedTs = Stopwatch.GetTimestamp();
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<WorkResult> Done = new TaskCompletionSource<WorkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int State;
            public volatile bool CancelRequested;
            public long BusyMs, MaxSliceMs, WaitMs, VisitsAtEnqueue, VisitsBeforeStart;
            public int Slices;
            public string RaiseResult;
            public WorkItem(Func<UIApplication, Stopwatch, int, bool> step, bool high) { Step = step; High = high; }
            public long ElapsedMs() => (Stopwatch.GetTimestamp() - CreatedTs) * 1000 / Stopwatch.Frequency;
        }
    }

    /// <summary>How a job ended on the Revit thread and how long it occupied it.</summary>
    public sealed class WorkResult
    {
        public const string KindCompleted = "completed", KindBusy = "busy", KindCancelled = "cancelled", KindFailed = "failed";

        internal static WorkResult Make(string kind, Exception error, MainThread.WorkItem item) => new WorkResult
        {
            Kind = kind,
            Error = error,
            BusyMs = item?.BusyMs ?? 0,
            MaxSliceMs = item?.MaxSliceMs ?? 0,
            Slices = item?.Slices ?? 0,
            WaitMs = item?.WaitMs ?? 0,
            RaiseResult = item?.RaiseResult,
            VisitsBeforeStart = item?.VisitsBeforeStart ?? 0,
        };

        public string Kind { get; private set; }
        public Exception Error { get; private set; }
        /// <summary>Milliseconds the job occupied the Revit thread (sum of slices).</summary>
        public long BusyMs { get; private set; }
        public long MaxSliceMs { get; private set; }
        public int Slices { get; private set; }
        /// <summary>Milliseconds from the request until Revit started it.</summary>
        public long WaitMs { get; private set; }
        /// <summary>ExternalEvent.Raise response when queuing (Accepted, Pending, Denied, TimedOut).</summary>
        public string RaiseResult { get; internal set; }
        /// <summary>Revit visits that ended without starting this job. 0 with a long wait = Revit did not serve the event.</summary>
        public long VisitsBeforeStart { get; private set; }
        public bool Ok => Kind == KindCompleted;

        /// <summary>Dispatch breakdown for timing=true and for revit_ocupado responses.</summary>
        public Dictionary<string, object> DispatchJson() => new Dictionary<string, object>
        {
            ["queue_wait_ms"] = WaitMs, ["revit_thread_ms"] = BusyMs, ["segments"] = (long)Slices, ["max_segment_ms"] = MaxSliceMs,
            ["raise"] = RaiseResult, ["visits_before_start"] = VisitsBeforeStart,
        };
    }
}
