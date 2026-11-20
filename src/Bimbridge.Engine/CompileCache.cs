// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Compilation cache for dynamic C# with "single-flight" (#9) and memory limits (AUD-05).
    ///
    /// Single-flight: if two agents request the same code at once (same fingerprint), only one compiles with Roslyn and the others wait for its
    /// result. AUD-05: the per-key gate has a user counter and is removed only when nobody uses it, so a key never has
    /// two live gates (previously, removing it while someone was waiting allowed two simultaneous compilations of the same key when the
    /// first one failed). Compilation failures are not stored: the next attempt compiles again.
    ///
    /// Memory: each distinct compilation leaves a loaded assembly that CANNOT be unloaded individually (.NET Framework 4.8; on
    /// .NET 8 the add-in loads them into its own context, which is not collectible either). That is why an LRU policy frees no memory: evicting
    /// an entry would only cause recompiling and loading ANOTHER assembly. Instead there is a limit on distinct entries: once reached,
    /// NEW compilations are rejected with a clear reason (reuse scripts by passing data through args, or restart Revit to free memory), while
    /// what is already compiled stays available. Accounting is per agent (or session) plus the estimated size of the loaded images.
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

        /// <param name="maxEntries">Distinct compilations allowed in this process (each one leaves a loaded assembly).</param>
        /// <param name="sizeOf">Estimated size of a result (e.g. bytes of the assembly image). Optional.</param>
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
        /// <summary>Requests that waited for an identical compilation in progress (and did not compile).</summary>
        public long CoalescedWaits => Interlocked.Read(ref _waits);
        public long TotalCompileMs => Interlocked.Read(ref _compileMs);
        /// <summary>Estimated bytes of loaded assemblies (sum of images). An estimate, not a heap measurement.</summary>
        public long LoadedBytes => Interlocked.Read(ref _bytes);
        /// <summary>New compilations rejected by the entry limit.</summary>
        public long Refused => Interlocked.Read(ref _refused);
        /// <summary>Live gates (for tests: returns to 0 when no compilations are in progress).</summary>
        public int LiveGates { get { lock (_gatesLock) return _gates.Count; } }

        public bool TryGet(string key, out T value)
        {
            if (_done.TryGetValue(key, out value)) { Interlocked.Increment(ref _hits); return true; }
            return false;
        }

        /// <summary>Compatibility: no owner and no refusal reason.</summary>
        public T GetOrCompile(string key, Func<Tuple<T, long>> compile, out bool fromCache) =>
            GetOrCompile(key, null, compile, out fromCache, out _);

        /// <summary>
        /// Returns the cached value or compiles it with <paramref name="compile"/> (only once per key at a time).
        /// <paramref name="fromCache"/> = true if it was not compiled in this call. compile returns null if it fails (with its error).
        /// If the key is new and <see cref="MaxEntries"/> was reached, returns null without compiling and explains the reason in <paramref name="refusal"/>.
        /// <paramref name="owner"/>: agent or session that requested it (per-agent accounting).
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
                // Atomic slot reservation: the limit is exact even when different keys compile at the same time.
                if (Interlocked.Increment(ref _slots) > MaxEntries)
                {
                    Interlocked.Decrement(ref _slots);
                    Interlocked.Increment(ref _refused);
                    refusal = "limit of distinct dynamic compilations reached (" + MaxEntries + "): each distinct script stays loaded in Revit " +
                              "until it restarts. Reuse the same script passing the data through args, or restart Revit to free memory.";
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
                    _byOwner.AddOrUpdate(string.IsNullOrEmpty(owner) ? "(no owner)" : owner, 1, (_, n) => n + 1);
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
                // Removed only when nobody else uses it: a key never has two live gates at once.
                if (--g.Users == 0 && _gates.TryGetValue(key, out var cur) && ReferenceEquals(cur, g)) _gates.Remove(key);
            }
        }

        /// <summary>Distinct compilations per agent or session (largest first).</summary>
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
