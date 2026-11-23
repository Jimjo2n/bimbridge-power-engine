// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using BimBridge.PowerEngine.Workflows;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.9.1 (Block I): executable PDF->Revit bridge. The placement plan only becomes typed plans when it is safe:
    /// valid registration, operations that pass offline validation, a stable idempotency key and ambiguous items left out.
    /// </summary>
    public class PdfExecutionTests
    {
        private static Dictionary<string, object> Batch(string type, params double[][] pts) => new Dictionary<string, object>
        {
            ["operation"] = new Dictionary<string, object>
            {
                ["op"] = "place", ["type"] = type, ["level"] = "LEVEL 02", ["elev"] = 9.25,
                ["points"] = pts.Select(p => (object)new List<object> { p[0], p[1] }).ToList(),
            },
            ["items"] = new List<object>(),
        };

        private static PlacementPlan Plan(params Dictionary<string, object>[] batches)
        {
            var p = new PlacementPlan { Registration = new RegistrationResult(), Fingerprint = "huella-1" };
            p.Source["id"] = "E-201@B#p1"; p.Source["sha256"] = "sha-de-prueba"; p.Source["revision"] = "B";
            p.Batches.AddRange(batches);
            foreach (var b in batches)
                foreach (var _ in (List<object>)((Dictionary<string, object>)b["operation"])["points"]) p.Place.Add(new PlacementItem());
            return p;
        }

        [Theory]
        [InlineData(10)]
        [InlineData(50)]
        [InlineData(200)]
        public void HostAndItemExpectationsSurvivePdfExecution(int size)
        {
            var reg = new RegistrationResult { M = new[] { 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0 } };
            var symbols = Enumerable.Range(0, size).Select(i => new SymbolCandidate
            {
                Px = i * 2.0, Py = 1, Code = i % 2 == 0 ? "A" : "B", Confidence = 1,
            }).ToList();
            var rules = new List<TypeRule>
            {
                new TypeRule { Code = "A", FamilyType = "F: T", ElevFt = 9, Host = "slab" },
                new TypeRule { Code = "B", FamilyType = "F: T", ElevFt = 9 },
            };
            var plan = PlacementPlanner.Plan(symbols, reg, rules, new PlacementOptions
            {
                Level = "L1", SourceId = "E-101", SourceSha256 = "sha-prueba",
            });
            Assert.Equal(size, plan.Place.Count);
            var execution = PdfToPlan.Execution(plan);
            var ops = ((List<object>)execution["plans"]).Cast<Dictionary<string, object>>()
                .SelectMany(p => (List<object>)p["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(2, ops.Count);
            Assert.Single(ops.Where(o => Arg.GetString(o, "host") == "slab"));
            Assert.Equal(size, ops.SelectMany(o => (List<object>)o["source_items"]).Distinct().Count());
            Assert.All(ops, o => Assert.StartsWith(SourceBinding.TokenPrefix, Arg.GetString(o, SourceBinding.OpField)));
        }

        [Fact]
        public void ReadyPlan_BecomesTypedPlansWithStableIdempotencyKeys()
        {
            var plan = Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 }));
            var e = PdfToPlan.Execution(plan);
            Assert.Equal(true, e["executable"]);
            var one = (Dictionary<string, object>)((List<object>)e["plans"]).Single();
            Assert.Equal(3L, one["elements"]);
            // The output is directly acceptable to the plan tool (offline typed validation).
            var parsed = OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = one["ops"] });
            Assert.Equal(new[] { "place", "place" }, parsed.Select(o => o.Kind).ToArray());
            // Same input -> same key (repeating does not place twice); other content or another fingerprint -> another key.
            var key = (string)one[Wire.KeyArg];
            Assert.StartsWith("src-", key);
            Assert.Equal(key, ((Dictionary<string, object>)((List<object>)PdfToPlan.Execution(Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 })))["plans"])[0])[Wire.KeyArg]);
            var moved = (Dictionary<string, object>)((List<object>)PdfToPlan.Execution(Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4.5 }), Batch("F: B", new[] { 5.0, 6 })))["plans"])[0];
            Assert.NotEqual(key, moved[Wire.KeyArg]);
            var other = Plan(Batch("F: A", new[] { 1.0, 2 }, new[] { 3.0, 4 }), Batch("F: B", new[] { 5.0, 6 })); other.Fingerprint = "huella-2";
            Assert.NotEqual(key, ((Dictionary<string, object>)((List<object>)PdfToPlan.Execution(other)["plans"])[0])[Wire.KeyArg]);
        }

        [Fact]
        public void UnsafePlans_AreNotExecutable_AndSayWhy()
        {
            Assert.Equal(false, PdfToPlan.Execution(null)["executable"]);
            var noReg = Plan(Batch("F: A", new[] { 1.0, 2 })); noReg.Registration = new RegistrationResult { State = "few_anchors" };
            Assert.Contains("registration", string.Join(" ", (List<object>)PdfToPlan.Execution(noReg)["reasons"]));
            var state = Plan(Batch("F: A", new[] { 1.0, 2 })); state.State = "no_level";
            Assert.Equal(false, PdfToPlan.Execution(state)["executable"]);
            var onlyAmb = Plan(); onlyAmb.State = "only_ambiguities"; onlyAmb.Ambiguous.Add(new PlacementItem());
            var ea = PdfToPlan.Execution(onlyAmb);
            Assert.Equal(false, ea["executable"]); Assert.Equal(1L, ea["pending_person"]);
            Assert.False(ea.ContainsKey("plans"));
            // An operation that typed validation would reject (no points, or not a place) blocks the whole plan.
            var bad = Plan(Batch("F: A")); bad.Place.Add(new PlacementItem());
            Assert.Equal(false, PdfToPlan.Execution(bad)["executable"]);
            var notPlace = Plan(Batch("F: A", new[] { 1.0, 2 }));
            ((Dictionary<string, object>)notPlace.Batches[0]["operation"])["op"] = "delete";
            Assert.Contains("is not a place operation", string.Join(" ", (List<object>)PdfToPlan.Execution(notPlace)["reasons"]));
            var noPrint = Plan(Batch("F: A", new[] { 1.0, 2 })); noPrint.Fingerprint = null;
            Assert.Equal(false, PdfToPlan.Execution(noPrint)["executable"]);
        }

        [Fact]
        public void AmbiguousSymbolsAreLeftOut_AndLargePlansAreSplit()
        {
            var plan = Plan(Enumerable.Range(0, OpSpec.MaxOps + 5).Select(i => Batch("F: T" + i, new[] { (double)i, 0 })).ToArray());
            plan.State = "ready_with_ambiguities";
            plan.Ambiguous.Add(new PlacementItem()); plan.Ambiguous.Add(new PlacementItem());
            var e = PdfToPlan.Execution(plan);
            Assert.Equal(true, e["executable"]);
            Assert.Equal(2L, e["pending_person"]);
            Assert.Contains("are NOT placed",(string)e["warning"]);
            var plans = ((List<object>)e["plans"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(2, plans.Count);
            Assert.Equal(OpSpec.MaxOps, ((List<object>)plans[0]["ops"]).Count);
            Assert.Equal(5, ((List<object>)plans[1]["ops"]).Count);
            Assert.NotEqual(plans[0][Wire.KeyArg], plans[1][Wire.KeyArg]);
            foreach (var p in plans) OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = p["ops"] });
        }
    }
}
