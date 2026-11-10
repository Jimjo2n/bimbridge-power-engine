// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Issue #9: caché de compilación de C# dinámico con "single-flight". Si dos agentes piden a la vez el mismo código (misma huella),
    /// sólo uno compila con Roslyn y el otro espera ese resultado, en vez de compilar y cargar dos ensamblados idénticos (en .NET Framework
    /// no se descargan nunca). Los fallos de compilación no se guardan: el siguiente intento vuelve a compilar.
    /// Contadores para el panel Estado (#5) y para diagnóstico.
    /// </summary>
    public sealed class CompileCache<T> where T : class
    {
        private readonly ConcurrentDictionary<string, T> _done = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _gates = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private long _hits, _misses, _failures, _waits, _compileMs;

        public int Count => _done.Count;
        public long Hits => Interlocked.Read(ref _hits);
        public long Misses => Interlocked.Read(ref _misses);
        public long Failures => Interlocked.Read(ref _failures);
        /// <summary>Peticiones que esperaron a una compilación idéntica en curso (y no compilaron).</summary>
        public long CoalescedWaits => Interlocked.Read(ref _waits);
        public long TotalCompileMs => Interlocked.Read(ref _compileMs);

        public bool TryGet(string key, out T value)
        {
            if (_done.TryGetValue(key, out value)) { Interlocked.Increment(ref _hits); return true; }
            return false;
        }

        /// <summary>
        /// Devuelve el valor de la caché o lo compila con <paramref name="compile"/> (una sola vez por clave a la vez).
        /// <paramref name="fromCache"/> = true si no se compiló en esta llamada. compile devuelve null si falla (con su error).
        /// </summary>
        public T GetOrCompile(string key, Func<Tuple<T, long>> compile, out bool fromCache)
        {
            fromCache = true;
            if (TryGet(key, out var hit)) return hit;
            var gate = _gates.GetOrAdd(key, _ => new object());
            var entered = Monitor.TryEnter(gate);
            if (!entered) { Interlocked.Increment(ref _waits); Monitor.Enter(gate); }
            try
            {
                if (_done.TryGetValue(key, out hit)) { Interlocked.Increment(ref _hits); return hit; }
                fromCache = false;
                Interlocked.Increment(ref _misses);
                var r = compile();
                if (r == null || r.Item1 == null) { Interlocked.Increment(ref _failures); return null; }
                Interlocked.Add(ref _compileMs, Math.Max(0, r.Item2));
                _done[key] = r.Item1;
                return r.Item1;
            }
            finally
            {
                Monitor.Exit(gate);
                _gates.TryRemove(key, out _);
            }
        }

        public Dictionary<string, object> Stats() => new Dictionary<string, object>
        {
            ["guardados"] = (long)Count, ["aciertos"] = Hits, ["fallos_cache"] = Misses, ["errores_compilacion"] = Failures,
            ["esperas_compartidas"] = CoalescedWaits, ["compilacion_ms_total"] = TotalCompileMs,
        };
    }
}
