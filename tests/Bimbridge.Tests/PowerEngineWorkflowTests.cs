// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BimBridge.PowerEngine;
using BimBridge.PowerEngine.Workflows;
using Bimbridge.Engine;
using Bimbridge.Engine.Workflow;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Drawing-model registration (no PDF): geometry cases.</summary>
    public class RegistrationTests
    {
        private static List<Anchor> Grid(double rotDeg, double ftPerPt, double tx, double ty)
        {
            var c = Math.Cos(rotDeg * Math.PI / 180); var s = Math.Sin(rotDeg * Math.PI / 180);
            var l = new List<Anchor>();
            foreach (var (n, mx, my) in new[] { ("A/1", 0.0, 0.0), ("F/1", 150.0, 0.0), ("A/5", 0.0, 100.0), ("F/5", 150.0, 100.0), ("C/3", 60.0, 50.0) })
            {
                // page = inverse of (model = R*s*page + t)
                double dx = mx - tx, dy = my - ty;
                l.Add(new Anchor(n, (c * dx + s * dy) / ftPerPt, (-s * dx + c * dy) / ftPerPt, mx, my));
            }
            return l;
        }

        [Fact]
        public void Similarity_RecoversScaleRotationTranslation()
        {
            var f = Registration.FtPerPtAtScale(96);
            var r = Registration.Solve(Grid(30, f, -12.5, 40), new RegistrationOptions { ExpectedRevitScale = 96 });
            Assert.True(r.Ok, r.Reason);
            Assert.Equal(96, r.RevitScale, 6);
            Assert.Equal(30, r.RotationDeg, 6);
            Assert.True(r.MaxFt < 1e-9);
            var (x, y) = r.Map(0, 0);
            Assert.Equal(-12.5, x, 6); Assert.Equal(40, y, 6);
        }

        [Fact]
        public void BadAnchor_IsDetectedAndDiscarded()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0);
            a[4].Mx += 30;   // C/3 paired with the wrong grid
            var r = Registration.Solve(a);
            Assert.True(r.Ok, r.Reason);
            Assert.Contains(r.Anchors, x => x.Name == "C/3" && x.Outlier && !x.Used);
            Assert.True(r.MaxFt < 1e-6);
            Assert.Contains(r.Warnings, w => w.Contains("C/3"));
        }

        [Fact]
        public void TooFew_Degenerate_ScaleMismatch_Residual()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0);
            Assert.Equal("insufficient_anchors", Registration.Solve(a.Take(1).ToList()).State);
            Assert.Equal("degenerate", Registration.Solve(new List<Anchor> { new Anchor("x", 1, 1, 0, 0), new Anchor("y", 1, 1, 5, 5) }).State);
            Assert.Equal("degenerate", Registration.Solve(new List<Anchor> { a[0], a[1], new Anchor("m", (a[0].Px + a[1].Px) / 2, a[0].Py, 75, 0) }, new RegistrationOptions { Mode = "affine" }).State);
            Assert.Equal("inconsistent_scale", Registration.Solve(a, new RegistrationOptions { ExpectedRevitScale = 48 }).State);
            Assert.Equal("invalid_anchor", Registration.Solve(new List<Anchor> { a[0], new Anchor("A/1", 0, 0, 1, 1) }).State);
            // with only 3 anchors, a bad one cannot be isolated: the residual stays visible
            var b = a.Take(3).ToList(); b[2] = new Anchor(b[2].Name, b[2].Px, b[2].Py, b[2].Mx + 10, b[2].My);
            Assert.Equal("excessive_residual", Registration.Solve(b).State);
        }

        [Fact]
        public void Affine_HandlesNonUniformScale_AndWarns()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0).Select(x => new Anchor(x.Name, x.Px, x.Py * 1.05, x.Mx, x.My)).ToList();
            var r = Registration.Solve(a, new RegistrationOptions { Mode = "affine" });
            Assert.True(r.Ok, r.Reason);
            Assert.Contains(r.Warnings, w => w.Contains("different scale"));
            Assert.Equal("excessive_residual", Registration.Solve(a).State);   // a similarity cannot explain a distorted drawing
        }

        [Fact]
        public void LinkTransform_IsComposed()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0);
            var link = new double[] { 0, -1, 0, 100, 1, 0, 0, 5, 0, 0, 1, 0 };   // link rotated 90 degrees and moved
            var r = Registration.Solve(a, new RegistrationOptions { LinkTransform = link });
            var (x, y) = r.Map(a[1].Px, a[1].Py);   // link F/1 = (150, 0) -> host (100, 155)
            Assert.Equal(100, x, 6); Assert.Equal(155, y, 6);
        }

        [Fact]
        public void ModelGridIntersections_ByName()
        {
            var g = Registration.GridIntersections(new[] { ("A", 0.0, -5.0, 0.0, 105.0), ("1", -5.0, 0.0, 155.0, 0.0), ("B", 30.0, -5.0, 30.0, 105.0) });
            Assert.Equal(2, g.Count);
            Assert.Equal((30.0, 0.0), g["B/1"]);
        }
    }

    /// <summary>
    /// Full SIMULATED workflow (no Revit): synthetic PDF -> extraction (Python) -> registration -> placement plan -> simulated
    /// "run" -> independent verification. None of this is native evidence.
    /// </summary>
    public class PdfToModelWorkflowTests
    {
        private static readonly string Fx = PowerEngineEnv.Fixtures;
        private static string F(string n) => Path.Combine(Fx, n);

        private static Dictionary<string, object> Truth(string name) => (Dictionary<string, object>)Json.ParseObject(File.ReadAllText(F("truth.json")))[name];

        private static Dictionary<string, (double X, double Y)> ModelGrid()
        {
            var axes = new List<(string, double, double, double, double)>();
            var gx = new[] { 0.0, 30, 60, 90, 120, 150 }; var gy = new[] { 0.0, 25, 50, 75, 100 };
            for (var i = 0; i < gx.Length; i++) axes.Add(("ABCDEF"[i].ToString(), gx[i], -5, gx[i], 105));
            for (var j = 0; j < gy.Length; j++) axes.Add(((j + 1).ToString(CultureInfo.InvariantCulture), -5, gy[j], 155, gy[j]));
            return Registration.GridIntersections(axes);
        }

        private static readonly List<TypeRule> Rules = new List<TypeRule>
        {
            new TypeRule { Code = "D", FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: D", Mounting = "slab", Workset = "LIGHTING - DIST" },
            new TypeRule { Code = "G", FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: G", Mounting = "slab", Workset = "LIGHTING-EMERGENCY - DIST" },
            new TypeRule { Code = "S", FamilyType = "LTG_STRIP LIGHT: S", ElevFt = 9.25, Workset = "LIGHTING - DIST", Directional = true },
        };

        public static IEnumerable<object[]> Plans() => new[] { "plan_vectorial.pdf", "plan_rotado.pdf", "plan_girado30.pdf" }.Select(n => new object[] { n });

        [Theory, MemberData(nameof(Plans))]
        public void Pdf_Register_Plan_Simulate_Verify(string name)
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                // A. interpretation
                var grids = pe.Document("pdf_grids", new Dictionary<string, object> { ["path"] = F(name) }, new[] { Fx });
                var syms = pe.Document("pdf_symbols", new Dictionary<string, object> { ["path"] = F(name) }, new[] { Fx });
                Assert.True(grids.Ok && syms.Ok, grids.Error + syms.Error);
                var pg = (Dictionary<string, object>)((List<object>)grids.Result["pages"])[0];
                var page = ((List<object>)pg["intersections"]).Cast<Dictionary<string, object>>()
                    .ToDictionary(x => (string)x["name"], x => { var p = (List<object>)x["point_pt"]; return (Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture)); });
                var sheet = (Dictionary<string, object>)((Dictionary<string, object>)((List<object>)syms.Result["pages"])[0])["sheet"];
                var scale = Convert.ToDouble(((Dictionary<string, object>)sheet["scale"])["revit_factor"], CultureInfo.InvariantCulture);

                // B. registration (anchors are intersections of grids with the same name in the drawing and in the model)
                var anchors = Registration.AnchorsByName(page, ModelGrid());
                Assert.Equal(30, anchors.Count);
                var reg = Registration.Solve(anchors, new RegistrationOptions { ExpectedRevitScale = scale, TolFt = 0.05 });
                Assert.True(reg.Ok, reg.Reason);
                var truth = Truth(name);
                // page->model undoes the model rotation on the sheet and applies the page /Rotate (visible = content rotated -90 degrees)
                var expRot = -Convert.ToDouble(truth["model_rotation_deg"], CultureInfo.InvariantCulture) + (truth.ContainsKey("page_rotation_deg") ? Convert.ToDouble(truth["page_rotation_deg"], CultureInfo.InvariantCulture) : 0);
                Assert.True(PlacementVerifier.AngleDiff(reg.RotationDeg, expRot, false) < 0.01, "giro " + reg.RotationDeg + " frente a " + expRot);
                Assert.True(reg.RmsFt < 0.01);

                // C. plan
                var pdfSha = (string)syms.Result["sha256"];
                var plan = PlacementPlanner.Plan(PlacementPlanner.FromPdfSymbols(syms.Result), reg, Rules,
                    new PlacementOptions { Level = "LEVEL 09", SourceId = name, SourceSha256 = pdfSha, SourceRevision = "CCD7" }, null, pe);
                Assert.Equal("ready_with_ambiguities", plan.State);
                Assert.Equal(72, plan.Place.Count);
                Assert.Equal(2, plan.Ambiguous.Count(a => a.Reason.StartsWith("near_duplicate", StringComparison.Ordinal)));
                Assert.All(plan.Batches, b => Assert.True(((List<object>)((Dictionary<string, object>)b["operation"])["points"]).Count <= 500));
                Assert.Equal(72, plan.Batches.Sum(b => ((List<object>)b["items"]).Count));
                var fixtures = ((List<object>)truth["fixtures"]).Cast<Dictionary<string, object>>().ToList();
                foreach (var l in fixtures)
                {
                    var m = (List<object>)l["model_ft"];
                    double mx = Convert.ToDouble(m[0], CultureInfo.InvariantCulture), my = Convert.ToDouble(m[1], CultureInfo.InvariantCulture);
                    var best = plan.Place.OrderBy(p => (p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my)).First();
                    Assert.True(Math.Sqrt((best.X - mx) * (best.X - mx) + (best.Y - my) * (best.Y - my)) < 0.02, name + " " + l["code"] + " " + mx + "," + my);
                    Assert.Equal((string)l["code"], best.Code);
                }
                // the strips follow the model rotation (symmetric at 180 degrees)
                Assert.All(plan.Place.Where(p => p.Code == "S"), p => Assert.True(PlacementVerifier.AngleDiff(p.RotDeg, -Convert.ToDouble(truth["model_rotation_deg"], CultureInfo.InvariantCulture), true) < 0.5, "tira " + p.RotDeg));

                // D. SIMULATED run: what place "would do", with seeded defects
                long id = 1000;
                var observed = plan.Place.Select(p => new ObservedFixture { Id = id++, FamilyType = p.FamilyType, X = p.X, Y = p.Y, RotDeg = p.RotDeg, ElevFt = p.ElevFt ?? 10.0 }).ToList();
                var lost = observed[0]; observed.RemoveAt(0);                                           // omitted
                observed[0].X += 1.0;                                                                      // moved 1 ft
                observed[1].FamilyType = "LTG_STRIP LIGHT: L5";                                            // wrong type
                observed.Add(new ObservedFixture { Id = 9001, FamilyType = observed[2].FamilyType, X = observed[2].X + 0.01, Y = observed[2].Y, RotDeg = observed[2].RotDeg }); // duplicate
                observed.Add(new ObservedFixture { Id = 9002, FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: D", X = 500, Y = 500 }); // extra

                // E. independent verification: the expected set comes from the test TRUTH (another source, another sha), not from the plan
                var expected = fixtures.Select((l, i) =>
                {
                    var m = (List<object>)l["model_ft"];
                    var code = (string)l["code"];
                    return new ExpectedFixture { Key = code + "-" + i, Code = code, FamilyType = Rules.First(r => r.Code == code).FamilyType, X = Convert.ToDouble(m[0], CultureInfo.InvariantCulture), Y = Convert.ToDouble(m[1], CultureInfo.InvariantCulture) };
                }).ToList();
                var truthSrc = new SourceRef { Id = "truth.json", Kind = SourceKinds.Table, Sha256 = Home.Sha256Hex(File.ReadAllText(F("truth.json"))) };
                var built = new List<SourceRef> { new SourceRef { Id = name, Kind = SourceKinds.Pdf, Sha256 = pdfSha } };
                var rep = PlacementVerifier.Verify(truthSrc, built, plan.Fingerprint, expected, observed, null, pe);
                Assert.Equal("differences", rep["status"]);
                var counts = (Dictionary<string, object>)((Dictionary<string, object>)rep["summary"])["differences"];
                Assert.Equal(1L, counts["skipped"]); Assert.Equal(1L, counts["displaced"]); Assert.Equal(1L, counts["wrong_type"]);
                Assert.Equal(1L, counts["duplicate"]); Assert.Equal(1L, counts["additional"]);
                Assert.False(counts.ContainsKey("wrong_rotation"));

                // verification against the plan itself or against the same PDF is NOT independent
                var planAsExpected = plan.Place.Select(p => new ExpectedFixture { Key = p.Id, Code = p.Code, FamilyType = p.FamilyType, X = p.X, Y = p.Y }).ToList();
                Assert.Equal("not_independent", PlacementVerifier.Verify(new SourceRef { Id = "plan", Kind = SourceKinds.Table, Sha256 = Home.Sha256Hex("other") }, built, plan.Fingerprint, planAsExpected, observed)["status"]);
                Assert.Equal("not_independent", PlacementVerifier.Verify(new SourceRef { Id = "copy", Kind = SourceKinds.Pdf, Sha256 = pdfSha }, built, null, expected, observed)["status"]);
            }
        }

        [Fact]
        public void Planner_EscalatesInsteadOfGuessing()
        {
            var reg = Registration.Solve(new List<Anchor> { new Anchor("a", 0, 0, 0, 0), new Anchor("b", 100, 0, 10, 0), new Anchor("c", 0, 100, 0, 10) });
            var syms = new List<SymbolCandidate>
            {
                new SymbolCandidate { Px = 10, Py = 10, Code = "D", Confidence = 0.95 },
                new SymbolCandidate { Px = 20, Py = 10, Code = "X", Confidence = 0.95 },     // no rule
                new SymbolCandidate { Px = 30, Py = 10, Code = "D", Confidence = 0.5 },      // low confidence
                new SymbolCandidate { Px = 40, Py = 10, Code = "N", Confidence = 0.95 },     // rule without height
                new SymbolCandidate { Px = 50, Py = 10, Code = "D", Confidence = 0.95 },     // already exists (same type)
                new SymbolCandidate { Px = 60, Py = 10, Code = "D", Confidence = 0.95 },     // another existing type next to it
            };
            var rules = new List<TypeRule> { new TypeRule { Code = "D", FamilyType = "F: D", ElevFt = 9 }, new TypeRule { Code = "N", FamilyType = "F: N" } };
            var ex = new List<ExistingElement> { new ExistingElement { Id = 7, X = 5, Y = 1, FamilyType = "F: D" }, new ExistingElement { Id = 8, X = 6, Y = 1, FamilyType = "F: Z" } };
            var plan = PlacementPlanner.Plan(syms, reg, rules, new PlacementOptions { Level = "L1", SourceSha256 = new string('a', 64) }, ex);
            Assert.Single(plan.Place);
            Assert.Single(plan.AlreadyThere);
            var why = plan.Ambiguous.Select(a => a.Reason.Split(':')[0]).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "existing_conflict", "low_confidence", "no_height", "no_type" }, why);
            Assert.Equal("unregistered", PlacementPlanner.Plan(syms, new RegistrationResult { State = "excessive_residual" }, rules, new PlacementOptions { Level = "L1" }).State);
            // ids stable across runs
            Assert.Equal(plan.Place[0].Id, PlacementPlanner.Plan(syms, reg, rules, new PlacementOptions { Level = "L1", SourceSha256 = new string('a', 64) }, ex).Place[0].Id);
        }

        [Fact]
        public void PlannerReusesEachExistingFixtureOnceAndOnlyOnTheRequestedLevel()
        {
            var registration = Registration.Solve(new List<Anchor>
            {
                new Anchor("a", 0, 0, 0, 0), new Anchor("b", 100, 0, 100, 0), new Anchor("c", 0, 100, 0, 100),
            });
            var symbols = new List<SymbolCandidate>
            {
                new SymbolCandidate { Px = 1, Py = 1, Code = "D", Confidence = 1 },
                new SymbolCandidate { Px = 1.8, Py = 1, Code = "D", Confidence = 1 },
            };
            var rules = new List<TypeRule> { new TypeRule { Code = "D", FamilyType = "F: D", ElevFt = 9 } };
            var existing = new List<ExistingElement>
            {
                new ExistingElement { Id = 40, X = 1.4, Y = 1, FamilyType = "F: D", Level = "L1" },
                new ExistingElement { Id = 41, X = 1, Y = 1, FamilyType = "F: D", Level = "L2" },
            };
            var settings = new PlacementOptions { Level = "L1" };
            var ambiguous = PlacementPlanner.Plan(symbols, registration, rules, settings, existing);
            Assert.Empty(ambiguous.AlreadyThere);
            Assert.Empty(ambiguous.Batches);
            Assert.Equal(2, ambiguous.Ambiguous.Count);
            Assert.All(ambiguous.Ambiguous, item => Assert.StartsWith("shared_existing", item.Reason));
            existing.RemoveAt(0);
            var first = PlacementPlanner.Plan(symbols, registration, rules, settings, existing);
            Assert.Equal(2, first.Place.Count);
            existing.AddRange(first.Place.Select((item, i) =>
                new ExistingElement { Id = 50 + i, X = item.X, Y = item.Y, FamilyType = item.FamilyType, Level = "L1" }));
            var retry = PlacementPlanner.Plan(symbols, registration, rules, settings, existing);
            Assert.Equal(2, retry.AlreadyThere.Count);
            Assert.Empty(retry.Batches);
        }

        [Fact]
        public void Planner_SplitsBatchesAt500()
        {
            var reg = Registration.Solve(new List<Anchor> { new Anchor("a", 0, 0, 0, 0), new Anchor("b", 100, 0, 100, 0), new Anchor("c", 0, 100, 0, 100) });
            var syms = Enumerable.Range(0, 1203).Select(i => new SymbolCandidate { Px = (i % 40) * 3, Py = (i / 40) * 3, Code = "D", Confidence = 0.95 }).ToList();
            var plan = PlacementPlanner.Plan(syms, reg, new List<TypeRule> { new TypeRule { Code = "D", FamilyType = "F: D", ElevFt = 9 } }, new PlacementOptions { Level = "L1", DuplicateTolFt = 0.5 });
            Assert.Equal(new[] { 500, 500, 203 }, plan.Batches.Select(b => ((List<object>)b["items"]).Count));
        }
    }
}
