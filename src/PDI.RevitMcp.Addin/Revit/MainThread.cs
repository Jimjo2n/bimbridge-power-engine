// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Única puerta al hilo principal de Revit (sección 4 de la arquitectura).
    /// - Un solo ExternalEvent para todo (pyRevit también usaba uno, pero una petición por visita: D3).
    /// - En cada visita se atiende trabajo hasta agotar el presupuesto (250 ms por defecto) y se vuelve a levantar el evento si
    ///   queda algo. Entre visitas Revit procesa la interfaz: el usuario nunca ve Revit "congelado" por el MCP (C8).
    /// - El trabajo es reanudable: una función que avanza un tramo y dice si terminó.
    /// - Reparto justo: lo nuevo (una consulta, el contexto) arranca en la siguiente visita aunque haya una revisión larga en
    ///   curso; las largas se turnan entre sí. Antes, una revisión de 30 s dejaba esperando a cualquier otra petición (y a la otra IA).
    /// - Ninguna excepción escapa hacia Revit.
    /// - Mide cada visita: la auditoría de eficiencia usa estos números (visitas, tramo más largo, tiempo ocupado).
    /// </summary>
    public sealed class MainThread : IExternalEventHandler
    {
        private readonly ConcurrentQueue<WorkItem> _high = new ConcurrentQueue<WorkItem>();
        private readonly ConcurrentQueue<WorkItem> _normal = new ConcurrentQueue<WorkItem>();
        /// <summary>Trabajos empezados que no terminaron en su tramo. Sólo se toca en el hilo de Revit.</summary>
        private readonly List<WorkItem> _active = new List<WorkItem>();
        private readonly Action<string> _log;
        private ExternalEvent _event;
        private readonly int _budgetMs;
        private int _rr;

        // Estadísticas globales (se leen desde cualquier hilo; escribe sólo el hilo de Revit)
        private long _visits, _busyMs, _maxVisitMs, _overBudget, _done, _cancelled, _failed;

        public MainThread(int budgetMs, Action<string> log)
        {
            _budgetMs = budgetMs;
            _log = log ?? (_ => { });
        }

        public int BudgetMs => _budgetMs;

        /// <summary>Debe llamarse desde OnStartup (contexto válido de la API).</summary>
        public void Create() { _event = ExternalEvent.Create(this); }

        public string GetName() => "BIMBRIDGE - Power Engine";

        /// <summary>
        /// Ejecuta un trabajo reanudable en el hilo de Revit. <paramref name="step"/> recibe la UIApplication, el reloj de la visita
        /// y el tiempo que le queda; devuelve true cuando terminó. Si Revit no empieza el trabajo en <paramref name="busyAfter"/>
        /// (diálogo abierto, comando en curso), se descarta sin correr y se informa "ocupado".
        /// </summary>
        public async Task<WorkResult> RunAsync(Func<UIApplication, Stopwatch, int, bool> step, bool highPriority, TimeSpan busyAfter, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return WorkResult.Make(WorkResult.KindCancelled, null, null);
            var item = new WorkItem(step, highPriority);
            (highPriority ? _high : _normal).Enqueue(item);
            Raise();

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(busyAfter, timer.Token);
                var first = await Task.WhenAny(item.Started.Task, delay).ConfigureAwait(false);
                timer.Cancel(); // no dejar el temporizador vivo
                if (first != item.Started.Task)
                {
                    // Si justo empezó, se deja terminar; si no, se marca para que no corra nunca.
                    if (Interlocked.CompareExchange(ref item.State, WorkItem.Cancelled, WorkItem.Waiting) == WorkItem.Waiting)
                        return WorkResult.Make(ct.IsCancellationRequested ? WorkResult.KindCancelled : WorkResult.KindBusy, null, item);
                }
            }
            using (ct.Register(() => item.CancelRequested = true))
                return await item.Done.Task.ConfigureAwait(false);
        }

        private void Raise()
        {
            var ev = _event;
            if (ev == null) return;
            var r = ev.Raise();
            if (r == ExternalEventRequest.Denied || r == ExternalEventRequest.TimedOut)
                _log("ExternalEvent.Raise: " + r);
        }

        public void Execute(UIApplication app)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // 1) lo nuevo urgente (contexto, consultas), 2) lo urgente ya empezado,
                // 3) lo nuevo normal (escrituras, revisiones), 4) lo largo ya empezado, por turnos.
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
                // Lo canceló el hilo de la petición justo ahora (pasó el plazo de "ocupado").
                if (item.State == WorkItem.Cancelled) Interlocked.Increment(ref _cancelled);
                return;
            }
            item.WaitMs = (long)(DateTime.UtcNow - item.CreatedUtc).TotalMilliseconds;
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

        /// <summary>Un tramo de un trabajo. true = terminó (bien o con error).</summary>
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

        /// <summary>Números del hilo de Revit desde que arrancó el complemento (para la auditoría y el estado).</summary>
        public Dictionary<string, object> Stats() => new Dictionary<string, object>
        {
            ["visitas"] = Interlocked.Read(ref _visits),
            ["ms_ocupado"] = Interlocked.Read(ref _busyMs),
            ["visita_max_ms"] = Interlocked.Read(ref _maxVisitMs),
            ["visitas_largas"] = Interlocked.Read(ref _overBudget),
            ["presupuesto_ms"] = (long)_budgetMs,
            ["trabajos"] = Interlocked.Read(ref _done),
            ["cancelados"] = Interlocked.Read(ref _cancelled),
            ["fallidos"] = Interlocked.Read(ref _failed),
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
            public readonly DateTime CreatedUtc = DateTime.UtcNow;
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<WorkResult> Done = new TaskCompletionSource<WorkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int State;
            public volatile bool CancelRequested;
            public long BusyMs, MaxSliceMs, WaitMs;
            public int Slices;
            public WorkItem(Func<UIApplication, Stopwatch, int, bool> step, bool high) { Step = step; High = high; }
        }
    }

    /// <summary>Cómo terminó un trabajo en el hilo de Revit y cuánto lo ocupó.</summary>
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
        };

        public string Kind { get; private set; }
        public Exception Error { get; private set; }
        /// <summary>Milisegundos que el trabajo ocupó el hilo de Revit (suma de tramos).</summary>
        public long BusyMs { get; private set; }
        public long MaxSliceMs { get; private set; }
        public int Slices { get; private set; }
        /// <summary>Milisegundos desde que se pidió hasta que Revit lo empezó.</summary>
        public long WaitMs { get; private set; }
        public bool Ok => Kind == KindCompleted;
    }
}
