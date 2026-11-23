// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine.Workflow;

namespace BimBridge.PowerEngine.Workflows
{
    public sealed class ExpectedFixture
    {
        public string Key, Code, FamilyType, Host;
        public double X, Y;
        public double? RotDeg, ElevFt;
    }

    public sealed class ObservedFixture
    {
        public long Id;
        public string FamilyType, Host;
        public double X, Y, RotDeg;
        public double? ElevFt;
        public bool? Visible;              // visible in the delivery view (null = not checked)
        public bool? SupportOk;            // contact with the host checked by geo (null = not checked)
    }

    public sealed class PlacementVerifyOptions
    {
        public double PositionTolFt = 0.5 / 12.0;   // 1/2"
        public double SearchRadiusFt = 3.0;         // beyond: missing/extra; inside but out of tolerance: displaced
        public double RotationTolDeg = 1.0;
        public double ElevTolFt = 1.0 / 96.0;       // 1/8"
        public double DuplicateTolFt = 0.25;
        public bool RotationSymmetric180 = true;    // a strip rotated 180° is the same strip
        public int MaxListed = 50;                  // the compact report lists at most this many per difference kind
    }

    /// <summary>
    /// Independent verification of a placement: compares the expected items (from a source INDEPENDENT of the one that guided the build,
    /// checked by SHA-256 and lineage via IndependentVerifier.CheckIndependence) with what is observed in the model, and classifies:
    /// missing, extra, duplicate, wrong type, displaced, wrong rotation, wrong elevation, invalid host, not visible.
    /// Compact report: counts + at most MaxListed cases per class.
    /// </summary>
    public static class PlacementVerifier
    {
        public static Dictionary<string, object> Verify(SourceRef expectedSource, IList<SourceRef> buildSources, string buildPlanFingerprint,
                                                        IList<ExpectedFixture> expected, IList<ObservedFixture> observed, PlacementVerifyOptions o = null, PowerEngineDispatcher engine = null)
        {
            o = o ?? new PlacementVerifyOptions();
            var ind = new IndependentVerifier.Input
            {
                ExpectedSource = expectedSource, BuildSources = (buildSources ?? new List<SourceRef>()).ToList(), BuildPlanFingerprint = buildPlanFingerprint,
                Expected = expected.Select(e => new IndependentVerifier.Item { Key = e.Key, X = e.X, Y = e.Y, Attrs = { [PlacementPlanner.FingerprintTypeAttr] = e.FamilyType ?? "" } }).ToList(),
            };
            var gate = IndependentVerifier.CheckIndependence(ind, out var proof);
            if (gate.HasValue)
                return new Dictionary<string, object> { ["status"] = gate.Value.State, ["reason"] = gate.Value.Reason, ["note"] = "no QA/QC verdict is issued without an independent source" };

            var diffs = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
            void Add(string kind, Dictionary<string, object> d) { if (!diffs.TryGetValue(kind, out var l)) diffs[kind] = l = new List<Dictionary<string, object>>(); l.Add(d); }

            // duplicates among the observed items (same type, very close)
            var opts = new double[observed.Count * 2];
            for (var i = 0; i < observed.Count; i++) { opts[2 * i] = observed[i].X; opts[2 * i + 1] = observed[i].Y; }
            var dg = observed.Count > 1 ? (engine != null ? engine.Duplicates(opts, 2, o.DuplicateTolFt) : GeometryKernels.Duplicates(opts, 2, o.DuplicateTolFt)) : new int[observed.Count];
            var dupOf = new HashSet<int>();
            foreach (var g in Enumerable.Range(0, observed.Count).GroupBy(i => dg[i]).Where(g => g.Count() > 1))
                foreach (var sub in g.GroupBy(i => observed[i].FamilyType ?? "").Where(s => s.Count() > 1))
                {
                    var keep = sub.OrderBy(i => observed[i].Id).First();
                    foreach (var i in sub.Where(i => i != keep)) { dupOf.Add(i); Add("duplicate", new Dictionary<string, object> { ["id"] = observed[i].Id, ["de"] = observed[keep].Id, ["type"] = observed[i].FamilyType }); }
                }
            var obsIdx = Enumerable.Range(0, observed.Count).Where(i => !dupOf.Contains(i)).ToList();

            // 1-to-1 matching within the search radius (deterministic global greedy; C# or Rust)
            var e = new double[expected.Count * 2];
            for (var i = 0; i < expected.Count; i++) { e[2 * i] = expected[i].X; e[2 * i + 1] = expected[i].Y; }
            var ob = new double[obsIdx.Count * 2];
            for (var k = 0; k < obsIdx.Count; k++) { ob[2 * k] = observed[obsIdx[k]].X; ob[2 * k + 1] = observed[obsIdx[k]].Y; }
            var pair = engine != null ? engine.Match(e, ob, 2, o.SearchRadiusFt) : GeometryKernels.Match(e, ob, 2, o.SearchRadiusFt);
            var matched = new HashSet<int>();
            var okCount = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                var x = expected[i];
                if (pair[i] < 0) { Add("skipped", new Dictionary<string, object> { ["key"] = x.Key, ["code"] = x.Code, ["xy_ft"] = XY(x.X, x.Y) }); continue; }
                var oi = obsIdx[pair[i]]; matched.Add(oi);
                var y = observed[oi];
                var bad = false;
                var d = Math.Sqrt((x.X - y.X) * (x.X - y.X) + (x.Y - y.Y) * (x.Y - y.Y));
                if (d > o.PositionTolFt) { bad = true; Add("displaced", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["distance_ft"] = Math.Round(d, 4), ["xy_ft"] = XY(x.X, x.Y) }); }
                if (x.FamilyType != null && !string.Equals(x.FamilyType, y.FamilyType, StringComparison.Ordinal))
                { bad = true; Add("wrong_type", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["expected"] = x.FamilyType, ["observed"] = y.FamilyType }); }
                if (x.RotDeg.HasValue)
                {
                    var dr = AngleDiff(x.RotDeg.Value, y.RotDeg, o.RotationSymmetric180);
                    if (dr > o.RotationTolDeg) { bad = true; Add("wrong_rotation", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["expected"] = x.RotDeg.Value, ["observed"] = Math.Round(y.RotDeg, 3) }); }
                }
                if (x.ElevFt.HasValue && (!y.ElevFt.HasValue || Math.Abs(x.ElevFt.Value - y.ElevFt.Value) > o.ElevTolFt))
                { bad = true; Add("wrong_height", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["expected_ft"] = x.ElevFt.Value, ["observed_ft"] = y.ElevFt }); }
                if (x.Host != null && !string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase))
                { bad = true; Add("invalid_support", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["expected"] = x.Host, ["observed"] = y.Host }); }
                else if (y.SupportOk == false) { bad = true; Add("invalid_support", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id, ["reason"] = "no contact with the host (geo mounting)" }); }
                if (y.Visible == false) { bad = true; Add("not_visible", new Dictionary<string, object> { ["key"] = x.Key, ["id"] = y.Id }); }
                if (!bad) okCount++;
            }
            foreach (var oi in obsIdx.Where(i => !matched.Contains(i)))
                Add("additional", new Dictionary<string, object> { ["id"] = observed[oi].Id, ["type"] = observed[oi].FamilyType, ["xy_ft"] = XY(observed[oi].X, observed[oi].Y) });

            var notChecked = new List<object>();
            if (observed.Any(y => y.Visible == null)) notChecked.Add("visibility (no view data)");
            if (observed.Any(y => y.SupportOk == null) && expected.All(x => x.Host == null)) notChecked.Add("physical host (no geo mounting)");
            if (expected.All(x => x.ElevFt == null)) notChecked.Add("elevation (not given by the source)");
            var total = diffs.Values.Sum(l => l.Count);
            return new Dictionary<string, object>
            {
                ["status"] = total == 0 ? "conforming" : "differences",
                ["independence"] = proof,
                ["summary"] = new Dictionary<string, object>
                {
                    ["expected_items"] = (long)expected.Count, ["observed_items"] = (long)observed.Count, ["correct"] = (long)okCount,
                    ["differences"] = diffs.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)(long)k.Value.Count),
                },
                ["differences"] = diffs.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => (object)k.Value.Take(o.MaxListed).Cast<object>().ToList()),
                ["not_checked"] = notChecked,
            };
        }

        public static double AngleDiff(double a, double b, bool sym180)
        {
            var d = Math.Abs(a - b) % 360; if (d > 180) d = 360 - d;
            if (sym180 && d > 90) d = 180 - d;
            return d;
        }

        private static List<object> XY(double x, double y) => new List<object> { Math.Round(x, 4), Math.Round(y, 4) };
    }
}
