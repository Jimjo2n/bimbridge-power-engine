// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PDI.RevitMcp.Engine
{
    /// <summary>La cola de operaciones largas está llena: se rechaza en vez de acumular sin límite.</summary>
    public sealed class SchedulerBusyException : Exception
    {
        public SchedulerBusyException(string message) : base(message) { }
    }

    /// <summary>
    /// 0.9.1 (Bloque I): planificador de operaciones largas entre agentes. No ejecuta nada por su cuenta: decide CUÁNDO puede empezar
    /// cada operación para que un agente no acapare el motor.
    ///  - límite global y por dueño de operaciones en curso;
    ///  - clave exclusiva opcional (p. ej. el documento): dos operaciones con la misma clave nunca corren a la vez;
    ///  - reparto justo: entre las que pueden empezar, primero la del dueño con menos en curso y, a igualdad, la más antigua;
    ///  - cola acotada: si está llena se rechaza (falla cerrado), no se espera indefinidamente;
    ///  - cancelar una operación en cola la retira sin haber empezado (nada que reconciliar).
    /// El hilo de Revit sigue teniendo su propia cola (MainThread); esto ordena el trabajo antes de llegar a ella.
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
                    throw new SchedulerBusyException("hay " + _queue.Count + " operaciones largas en cola (máximo " + MaxQueued + "): espere a que terminen o cancele alguna");
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
                    // Sólo se cancela si sigue en cola; si ya empezó, la cancelación es asunto del cuerpo (su propio token).
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

        /// <summary>Arranca todas las que puedan empezar ahora (llamar con el bloqueo tomado).</summary>
        private void Dispatch()
        {
            while (_running < MaxRunning)
            {
                Waiter next = null;
                var best = int.MaxValue;
                foreach (var w in _queue)   // _queue está en orden de llegada: a igualdad gana la más antigua
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
