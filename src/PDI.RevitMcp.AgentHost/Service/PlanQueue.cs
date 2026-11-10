// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// alpha3 (P1-B): cola de planes del servicio con UN estado por plan bajo un candado.
    /// Garantías: (1) un plan nunca se ejecuta dos veces a la vez en este servicio; (2) un Enqueue que llega durante la ejecución
    /// no se pierde: se repite al terminar; (3) encolar algo ya en cola es idempotente y no consume capacidad.
    /// Causa raíz del defecto anterior: el id se quitaba de "_queued" antes de ocupar "_slots"; un Enqueue en ese hueco o durante
    /// la ejecución lo volvía a meter en la cola, otro trabajador lo sacaba, no podía ocupar el slot y lo descartaba sin aviso.
    /// </summary>
    public sealed class PlanQueue
    {
        private enum St { EnCola, Ejecutando, EjecutandoYRepetir }
        private readonly Dictionary<string, St> _q = new Dictionary<string, St>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private readonly Channel<string> _ch;
        public Action<string, string> OnLost;   // (plan, motivo): reprogramación que no cupo en la cola

        public PlanQueue(int capacity) =>
            _ch = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, capacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false });

        public int Queued { get { lock (_lock) return _q.Values.Count(v => v == St.EnCola); } }
        public bool IsRunning(string id) { lock (_lock) return _q.TryGetValue(id, out var s) && s != St.EnCola; }

        public bool Enqueue(string id)
        {
            lock (_lock)
            {
                if (_q.TryGetValue(id, out var st))
                {
                    if (st == St.Ejecutando) _q[id] = St.EjecutandoYRepetir;
                    return true;
                }
                if (!_ch.Writer.TryWrite(id)) return false;
                _q[id] = St.EnCola;
                return true;
            }
        }

        /// <summary>Bucle de trabajador: toma un plan, lo ejecuta y lo libera (re-encolándolo si hubo un Enqueue mientras corría).</summary>
        public async Task WorkerLoop(Action<string> run, CancellationToken stop)
        {
            try
            {
                while (await _ch.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
                {
                    if (!_ch.Reader.TryRead(out var id)) continue;
                    lock (_lock)
                    {
                        if (!_q.TryGetValue(id, out var st) || st != St.EnCola) { OnLost?.Invoke(id, "cola_inconsistente"); continue; }
                        _q[id] = St.Ejecutando;
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
                again = _q.TryGetValue(id, out var st) && st == St.EjecutandoYRepetir && !stop.IsCancellationRequested;
                _q.Remove(id);
                if (again && _ch.Writer.TryWrite(id)) { _q[id] = St.EnCola; return; }
            }
            if (again) OnLost?.Invoke(id, "cola llena; se recupera al reiniciar o con reanudar");
        }
    }
}
