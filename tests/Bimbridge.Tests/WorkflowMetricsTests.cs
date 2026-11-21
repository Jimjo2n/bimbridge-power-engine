// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Metrics declare their provenance: observed, estimated or not instrumented; what was not measured is never filled in.</summary>
    public class WorkflowMetricsTests
    {
        [Fact]
        public void Metrics_SeparateObservedEstimatedAndNotInstrumented()
        {
            var m = WorkflowMetrics.FromMainThread(120, 5, 80, 4, 30).Observe("elements_read", 1000).Observe("cache_hits", 900).Observe("payload_bytes", 4000).Estimate("allocations_bytes", 12345);
            var d = m.ToJson();
            var obs = (Dictionary<string, object>)d["observed"];
            Assert.Equal(120L, obs["total_ms"]); Assert.Equal(4L, obs["revit_slices"]); Assert.Equal(1000L, obs["elements_read"]);
            Assert.Equal(12345L, ((Dictionary<string, object>)d["estimated"])["allocations_bytes"]);
            var missing = ((List<object>)d["not_instrumented"]).Cast<string>().ToList();
            Assert.Contains("api_calls", missing);          // no complete counter: declared, not filled in
            Assert.Contains("regenerations", missing);
            Assert.DoesNotContain("total_ms", missing);
            Assert.Equal(1000L, d["estimated_tokens"]);     // bytes/4, estimate
            Assert.Equal(WorkflowMetrics.Names.Length, obs.Count + 1 + missing.Count);
        }

        [Fact]
        public void Metrics_RejectUnknownNames_AndObservedWinsOverEstimate()
        {
            Assert.Throws<ArgumentException>(() => new WorkflowMetrics().Observe("invented", 1));
            var m = new WorkflowMetrics().Estimate("payload_bytes", 10).Observe("payload_bytes", 20).Estimate("payload_bytes", 30);
            Assert.True(m.IsObserved("payload_bytes")); Assert.Equal(20, m.Get("payload_bytes"));
        }
    }
}