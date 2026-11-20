// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine
{
    /// <summary>
    /// 0.9.1 (instrumentation for the benchmark against pyRevit): workflow metrics with their declared provenance.
    /// Each metric is OBSERVED (measured in this process: stopwatch, counter incremented at the exact point), ESTIMATED
    /// (computed, e.g. tokens = bytes/4) or NOT INSTRUMENTED (not measured on this path). Something only partially counted
    /// is never reported as observed: Revit API call counters do not exist yet and are declared as such.
    /// </summary>
    public sealed class WorkflowMetrics
    {
        /// <summary>Official names from the directive, in order.</summary>
        public static readonly string[] Names =
        {
            "total_ms", "queue_ms", "revit_main_thread_ms", "revit_slices", "revit_max_slice_ms", "api_calls", "elements_read", "elements_written",
            "transactions", "regenerations", "serialization_ms", "payload_bytes", "worker_ms", "cache_hits", "cache_misses", "verification_ms", "audit_ms",
            "allocations_bytes",
        };

        private readonly Dictionary<string, long> _observed = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _estimated = new Dictionary<string, long>();

        public WorkflowMetrics Observe(string name, long? value) { if (value.HasValue) { Check(name); _observed[name] = value.Value; _estimated.Remove(name); } return this; }
        public WorkflowMetrics Estimate(string name, long? value) { if (value.HasValue && !_observed.ContainsKey(name)) { Check(name); _estimated[name] = value.Value; } return this; }

        public long? Get(string name) => _observed.TryGetValue(name, out var v) ? v : _estimated.TryGetValue(name, out var e) ? e : (long?)null;
        public bool IsObserved(string name) => _observed.ContainsKey(name);

        private static void Check(string name)
        {
            if (System.Array.IndexOf(Names, name) < 0) throw new System.ArgumentException("unknown metric: " + name);
        }

        /// <summary>Revit thread metrics as measured by MainThread (observed).</summary>
        public static WorkflowMetrics FromMainThread(long totalMs, long waitMs, long busyMs, int slices, long maxSliceMs) =>
            new WorkflowMetrics().Observe("total_ms", totalMs).Observe("queue_ms", waitMs).Observe("revit_main_thread_ms", busyMs)
                                 .Observe("revit_slices", slices).Observe("revit_max_slice_ms", maxSliceMs);

        /// <summary>Size of the serialized response (observed) and its token cost (estimated: bytes/4, not reported by the MCP client).</summary>
        public WorkflowMetrics Payload(long bytes, long serializationMs)
        {
            Observe("payload_bytes", bytes);
            Observe("serialization_ms", serializationMs);
            return this;
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["observadas"] = Names.Where(_observed.ContainsKey).ToDictionary(n => n, n => (object)_observed[n]),
            };
            if (_estimated.Count > 0) d["estimadas"] = Names.Where(_estimated.ContainsKey).ToDictionary(n => n, n => (object)_estimated[n]);
            var missing = Names.Where(n => !_observed.ContainsKey(n) && !_estimated.ContainsKey(n)).ToList();
            if (missing.Count > 0) d["no_instrumentadas"] = missing.Cast<object>().ToList();
            if (_observed.TryGetValue("payload_bytes", out var b)) d["tokens_estimados"] = b / 4;
            return d;
        }
    }
}
