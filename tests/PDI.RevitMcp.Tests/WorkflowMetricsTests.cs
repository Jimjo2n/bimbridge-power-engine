// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Las métricas declaran su procedencia: observadas, estimadas o no instrumentadas; nunca se rellena lo que no se midió.</summary>
    public class WorkflowMetricsTests
    {
        [Fact]
        public void Metrics_SeparateObservedEstimatedAndNotInstrumented()
        {
            var m = WorkflowMetrics.FromMainThread(120, 5, 80, 4, 30).Observe("elements_read", 1000).Observe("cache_hits", 900).Observe("payload_bytes", 4000).Estimate("allocations_bytes", 12345);
            var d = m.ToJson();
            var obs = (Dictionary<string, object>)d["observadas"];
            Assert.Equal(120L, obs["total_ms"]); Assert.Equal(4L, obs["revit_slices"]); Assert.Equal(1000L, obs["elements_read"]);
            Assert.Equal(12345L, ((Dictionary<string, object>)d["estimadas"])["allocations_bytes"]);
            var missing = ((List<object>)d["no_instrumentadas"]).Cast<string>().ToList();
            Assert.Contains("api_calls", missing);          // sin contador completo: se declara, no se rellena
            Assert.Contains("regenerations", missing);
            Assert.DoesNotContain("total_ms", missing);
            Assert.Equal(1000L, d["tokens_estimados"]);     // bytes/4, estimación
            Assert.Equal(WorkflowMetrics.Names.Length, obs.Count + 1 + missing.Count);
        }

        [Fact]
        public void Metrics_RejectUnknownNames_AndObservedWinsOverEstimate()
        {
            Assert.Throws<ArgumentException>(() => new WorkflowMetrics().Observe("inventada", 1));
            var m = new WorkflowMetrics().Estimate("payload_bytes", 10).Observe("payload_bytes", 20).Estimate("payload_bytes", 30);
            Assert.True(m.IsObserved("payload_bytes")); Assert.Equal(20, m.Get("payload_bytes"));
        }
    }
}