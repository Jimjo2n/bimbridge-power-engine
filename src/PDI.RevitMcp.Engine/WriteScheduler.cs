// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Cola única de escritura con turnos justos entre sesiones (sección 9.2).
    /// Cada elemento de la cola es un sub-lote. El planificador toma uno de la sesión A,
    /// luego uno de la B, etc., para que un plan grande de un agente no bloquee a otro.
    /// Una barrera cerrada (guardar, sync, exportar) detiene la entrega de sub-lotes nuevos.
    /// </summary>
    public sealed class WriteScheduler<T>
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Queue<T>> _queues = new Dictionary<string, Queue<T>>(StringComparer.Ordinal);
        private readonly List<string> _ring = new List<string>();
        private int _cursor; // índice del próximo candidato en _ring
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

        /// <summary>Entrega el siguiente sub-lote según el turno. False si no hay nada o la barrera está cerrada.</summary>
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
                        // la sesión sale del anillo; el cursor queda en el mismo índice (que ahora es el siguiente)
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

        /// <summary>Cuántos sub-lotes se entregarán antes de que le toque al primero pendiente de esta sesión.</summary>
        public int PositionOf(string sessionId)
        {
            lock (_lock)
            {
                if (!_queues.ContainsKey(sessionId)) return -1;
                // simulación del turno sin modificar la cola
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

        /// <summary>Quita todo lo pendiente de una sesión (desconexión). Devuelve lo que se quitó.</summary>
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

        /// <summary>Cierra la barrera de documento. Lo pendiente queda en cola, sin entregarse.</summary>
        public void CloseBarrier(string reason)
        {
            lock (_lock) _barrierReason = string.IsNullOrEmpty(reason) ? "operación de documento" : reason;
        }

        public void OpenBarrier()
        {
            lock (_lock) _barrierReason = null;
        }

        /// <summary>Motivo de la barrera cerrada, o null si está abierta.</summary>
        public string BarrierReason { get { lock (_lock) return _barrierReason; } }
    }
}
