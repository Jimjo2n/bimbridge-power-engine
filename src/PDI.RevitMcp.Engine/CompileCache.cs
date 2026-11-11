// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Caché de compilación de C# dinámico con "single-flight" (#9) y límites de memoria (AUD-05).
    ///
    /// Single-flight: si dos agentes piden a la vez el mismo código (misma huella), sólo uno compila con Roslyn y los demás esperan su
    /// resultado. AUD-05: la compuerta por clave tiene contador de usuarios y sólo se retira cuando nadie la usa, así una clave nunca tiene
    /// dos compuertas vivas (antes, retirarla mientras alguien esperaba permitía dos compilaciones simultáneas de la misma clave cuando la
    /// primera fallaba). Los fallos de compilación no se guardan: el siguiente intento vuelve a compilar.
    ///
    /// Memoria: cada compilación distinta deja un ensamblado cargado que NO se puede descargar individualmente (.NET Framework 4.8; en
    /// .NET 8 el add-in los carga en su propio contexto, que tampoco es recolectable). Por eso una política LRU no libera memoria: descartar
    /// una entrada sólo provocaría recompilar y cargar OTRO ensamblado. En su lugar hay un límite de entradas distintas: al alcanzarlo se
    /// rechazan compilaciones NUEVAS con un motivo claro (reutilizar scripts pasando datos por args, o reiniciar Revit para liberar), mientras
    /// lo ya compilado sigue disponible. Se contabiliza por agente (o sesión) y el tamaño estimado de las imágenes cargadas.
    /// </summary>
    public sealed class CompileCache<T> where T : class
    {
        public const int DefaultMaxEntries = 1024;

        private sealed class Gate { public int Users; }

        private readonly ConcurrentDictionary<string, T> _done = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
        private readonly Dictionary<string, Gate> _gates = new Dictionary<string, Gate>(StringComparer.Ordinal);
        private readonly object _gatesLock = new object();
        private readonly ConcurrentDictionary<string, long> _byOwner = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<T, long> _sizeOf;
        private long _hits, _misses, _failures, _waits, _compileMs, _bytes, _refused, _slots;

        /// <param name="maxEntries">Compilaciones distintas permitidas en este proceso (cada una deja un ensamblado cargado).</param>
        /// <param name="sizeOf">Tamaño estimado de un resultado (p. ej. bytes de la imagen del ensamblado). Opcional.</param>
        public CompileCache(int maxEntries = DefaultMaxEntries, Func<T, long> sizeOf = null)
        {
            MaxEntries = Math.Max(1, maxEntries);
            _sizeOf = sizeOf;
        }

        public int MaxEntries { get; }
        public int Count => _done.Count;
        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public long Failures => Interlocked.Read(ref _failures);
        /// <summary>Peticiones que esperaron a una compilación idéntica en curso (y no compilaron).</summary>
        public long CoalescedWaits => Interlocked.Read(ref _waits);
        public long TotalCompileMs => Interlocked.Read(ref _compileMs);
        /// <summary>Bytes estimados de los ensamblados cargados (suma de imágenes). Estimación, no medición del heap.</summary>
        public long LoadedBytes => Interlocked.Read(ref _bytes);
        /// <summary>Compilaciones nuevas rechazadas por el límite de entradas.</summary>
        public long Refused => Interlocked.Read(ref _refused);
        /// <summary>Compuertas vivas (para pruebas: vuelve a 0 cuando no hay compilaciones en curso).</summary>
        public int LiveGates { get { lock (_gatesLock) return _gates.Count; } }

        public bool TryGet(string key, out T value)
        {
            if (_done.TryGetValue(key, out value)) { Interlocked.Increment(ref _hits); return true; }
            return false;
        }

        /// <summary>Compatibilidad: sin dueño ni motivo de rechazo.</summary>
        public T GetOrCompile(string key, Func<Tuple<T, long>> compile, out bool fromCache) =>
            GetOrCompile(key, null, compile, out fromCache, out _);

        /// <summary>
        /// Devuelve el valor de la caché o lo compila con <paramref name="compile"/> (una sola vez por clave a la vez).
        /// <paramref name="fromCache"/> = true si no se compiló en esta llamada. compile devuelve null si falla (con su error).
        /// Si la clave es nueva y se alcanzó <see cref="MaxEntries"/>, devuelve null sin compilar y explica el motivo en <paramref name="refusal"/>.
        /// <paramref name="owner"/>: agente o sesión que la pidió (contabilidad por agente).
        /// </summary>
        public T GetOrCompile(string key, string owner, Func<Tuple<T, long>> compile, out bool fromCache, out string refusal)
        {
            fromCache = true;
            refusal = null;
            if (TryGet(key, out var hit)) return hit;
            var gate = EnterGate(key);
            try
            {
                if (_done.TryGetValue(key, out hit)) { Interlocked.Increment(ref _hits); return hit; }
                // Reserva atómica de una plaza: el límite es exacto aunque compilen a la vez claves distintas.
                if (Interlocked.Increment(ref _slots) > MaxEntries)
                {
                    Interlocked.Decrement(ref _slots);
                    Interlocked.Increment(ref _refused);
                    refusal = "límite de compilaciones dinámicas distintas alcanzado (" + MaxEntries + "): cada script distinto queda cargado en Revit " +
                              "hasta reiniciarlo. Reutilice un mismo script pasando los datos por args, o reinicie Revit para liberar memoria.";
                    return null;
                }
                fromCache = false;
                Interlocked.Increment(ref _misses);
                Tuple<T, long> r;
                try { r = compile(); }
                catch { Interlocked.Decrement(ref _slots); throw; }
                if (r == null || r.Item1 == null) { Interlocked.Decrement(ref _slots); Interlocked.Increment(ref _failures); return null; }
                Interlocked.Add(ref _compileMs, Math.Max(0, r.Item2));
                if (_done.TryAdd(key, r.Item1))
                {
                    if (_sizeOf != null) Interlocked.Add(ref _bytes, Math.Max(0, _sizeOf(r.Item1)));
                    _byOwner.AddOrUpdate(string.IsNullOrEmpty(owner) ? "(sin dueño)" : owner, 1, (_, n) => n + 1);
                }
                else Interlocked.Decrement(ref _slots);
                return r.Item1;
            }
            finally { ExitGate(key, gate); }
        }

        private Gate EnterGate(string key)
        {
            Gate g;
            lock (_gatesLock)
            {
                if (!_gates.TryGetValue(key, out g)) { g = new Gate(); _gates[key] = g; }
                g.Users++;
            }
            if (!Monitor.TryEnter(g)) { Interlocked.Increment(ref _waits); Monitor.Enter(g); }
            return g;
        }

        private void ExitGate(string key, Gate g)
        {
            Monitor.Exit(g);
            lock (_gatesLock)
            {
                // Sólo se retira cuando nadie más la usa: una clave nunca tiene dos compuertas vivas a la vez.
                if (--g.Users == 0 && _gates.TryGetValue(key, out var cur) && ReferenceEquals(cur, g)) _gates.Remove(key);
            }
        }

        /// <summary>Compilaciones distintas por agente o sesión (las más numerosas primero).</summary>
        public List<KeyValuePair<string, long>> ByOwner(int top = 10) =>
            _byOwner.OrderByDescending(kv => kv.Value).Take(top).ToList();

        public Dictionary<string, object> Stats() => new Dictionary<string, object>
        {
            ["guardados"] = (long)Count, ["limite"] = (long)MaxEntries, ["aciertos"] = Hits, ["fallos_cache"] = Misses, ["errores_compilacion"] = Failures,
            ["esperas_compartidas"] = CoalescedWaits, ["compilacion_ms_total"] = TotalCompileMs, ["rechazadas_por_limite"] = Refused,
            ["bytes_ensamblados_estimados"] = LoadedBytes,
            ["por_agente"] = ByOwner().ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        };
    }
}
