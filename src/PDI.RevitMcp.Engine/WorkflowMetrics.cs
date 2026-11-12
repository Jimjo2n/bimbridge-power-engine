// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// 0.9.1 (instrumentación para el benchmark frente a pyRevit): métricas de un workflow con su procedencia declarada.
    /// Cada métrica es OBSERVADA (medida en este proceso: cronómetro, contador incrementado en el punto exacto), ESTIMADA
    /// (calculada, p. ej. tokens = bytes/4) o NO INSTRUMENTADA (no se mide en esta ruta). Nunca se informa como observado algo que
    /// sólo está parcialmente contado: los contadores de llamadas a la API de Revit no existen todavía y se declaran así.
    /// </summary>
    public sealed class WorkflowMetrics
    {
        /// <summary>Nombres oficiales de la directiva, en orden.</summary>
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
            if (System.Array.IndexOf(Names, name) < 0) throw new System.ArgumentException("métrica desconocida: " + name);
        }

        /// <summary>Métricas del hilo de Revit tal como las mide MainThread (observadas).</summary>
        public static WorkflowMetrics FromMainThread(long totalMs, long waitMs, long busyMs, int slices, long maxSliceMs) =>
            new WorkflowMetrics().Observe("total_ms", totalMs).Observe("queue_ms", waitMs).Observe("revit_main_thread_ms", busyMs)
                                 .Observe("revit_slices", slices).Observe("revit_max_slice_ms", maxSliceMs);

        /// <summary>Tamaño de la respuesta serializada (observado) y su coste en tokens (estimado: bytes/4, no lo informa el cliente MCP).</summary>
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
