// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Engine.Geo;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Geo recipes on synthetic scenes (the same situations as SAMPLE PROJECT: slab, ramp, beam, wall).</summary>
    public class GeoComputeTests
    {
        private static GeoBody Slab(double z0, double z1, string label = "slab", double x0 = -50, double y0 = -50, double x1 = 50, double y1 = 50)
        {
            var b = new TriMeshBuilder();
            MeshTests.Box(b, x0, y0, z0, x1, y1, z1);
            return new GeoBody { Id = 1, Label = label, Box = new Box3(x0, y0, z0, x1, y1, z1), Mesh = b.Build() };
        }

        private static GeoTarget Fixture(long id, double cx, double cy, double zBot, double zTop, double w = 2, double d = 4, string lvl = "LEVEL 02")
        {
            var b = new TriMeshBuilder();
            MeshTests.Box(b, cx - w / 2, cy - d / 2, zBot, cx + w / 2, cy + d / 2, zTop);
            return new GeoTarget
            {
                Id = id, Level = lvl, TypeKey = "LTG_STRIP LIGHT: D", Box = new Box3(cx - w / 2, cy - d / 2, zBot, cx + w / 2, cy + d / 2, zTop),
                X = cx, Y = cy, Z = zBot, Body = b.Build(), Candidates = new[] { 0 },
            };
        }

        private static GeoSpec Spec(string recipe, params (string, object)[] extra)
        {
            var a = new Dictionary<string, object> { ["name"] = recipe };
            foreach (var (k, v) in extra) a[k] = v;
            return GeoSpec.Parse(a);
        }

        [Fact]
        public void Mount_Classifies_Ok_Floating_Embedded_ByTopFace()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var targets = new List<GeoTarget>
            {
                Fixture(1, 0, 0, 19.5, 20),                    // top face = slab underside
                Fixture(2, 10, 0, 19.0, 19.5),                 // 6" of air
                Fixture(3, 20, 0, 19.75, 20.125),              // 1 1/2" into the slab
                Fixture(4, 30, 0, 19.5, 20.0 + 1.0 / 16 / 12), // 1/16": within tolerance
            };
            var rows = GeoCompute.Mount(targets, bodies, Spec("mounting"), new GeoCompute.Stats());
            Assert.Equal(new[] { "ok", "floating", "embedded", "ok" }, rows.Select(r => (string)r["class"]).ToArray());
            Assert.Equal(0.5, (double)rows[1]["gap"], 9);
            Assert.Equal(-0.125, (double)rows[2]["gap"], 9);
            Assert.Equal("9/9", rows[0]["pts"]);
            Assert.Equal("slab", rows[0]["against"]);
        }

        [Fact]
        public void MountReportIdentifiesTheControllingLinkedCopyAndHostFacePoint()
        {
            var original = Slab(23, 24, "ARCH: losa");
            original.LinkId = 10;
            var translated = Slab(20, 21, "ARCH: losa");
            translated.LinkId = 20;
            var target = Fixture(4, 5, 6, 18, 19);
            target.Candidates = new[] { 0, 1 };
            var bodies = new[] { original, translated };
            var result = Assert.Single(GeoCompute.Mount(new[] { target }, bodies, Spec("clearance", ("references", true)), null));
            Assert.Equal(1L, result["element_id"]);
            Assert.Equal(20L, result["link_id"]);
            Assert.Equal(5d, result["face_x"]);
            Assert.Equal(6d, result["face_y"]);
            Assert.Equal(20d, result["face_z"]);
            Assert.Equal("bottom", result["face_side"]);
            Assert.False(Assert.Single(GeoCompute.Mount(new[] { target }, bodies, Spec("clearance"), null)).ContainsKey("link_id"));
        }

        [Fact]
        public void Mount_UnderRamp_ControllingCornerIsTheLowestUnderside()
        {
            var rb = new TriMeshBuilder();
            MeshTests.Ramp(rb, -20, -20, 20, 20, 15, 4, 1); // underside: z = 15 + 4 (x + 20)/40
            var bodies = new List<GeoBody> { new GeoBody { Label = "ramp", Box = new Box3(-20, -20, 15, 20, 20, 20), Mesh = rb.Build() } };
            // 2×4 fixture centered at x=0: the left edge controls. It is measured 1/2" in from the edge (x = -1 + 1/24),
            // where the underside is at 15 + 0.1 (x + 20) = 16.9 + 0.1/24.
            var t = Fixture(9, 0, 0, 16.4, 16.9 + 0.1 / 24);
            var rows = GeoCompute.Mount(new[] { t }, bodies, Spec("mounting"), null);
            Assert.Equal("ok", rows[0]["class"]);
            Assert.True(rows[0].ContainsKey("pend"));
            // Raising it 1/8" leaves it embedded at the low corner even though the center has air.
            var t2 = Fixture(10, 0, 0, 16.5, 16.9 + 1.0 / 96);
            var r2 = GeoCompute.Mount(new[] { t2 }, bodies, Spec("mounting"), null);
            Assert.Equal("embedded", r2[0]["class"]);
        }

        [Fact]
        public void Mount_WithoutSurface_IsReported_NotInvented()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75, x0: 100, x1: 120) };
            var rows = GeoCompute.Mount(new[] { Fixture(5, 0, 0, 19, 19.5) }, bodies, Spec("mounting"), null);
            Assert.Equal("no_surface", rows[0]["class"]);
            Assert.True(double.IsNaN((double)rows[0]["gap"]));
        }

        [Fact]
        public void Mount_FallsBackToBox_WhenBodyHasNoMesh()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var t = Fixture(6, 0, 0, 19.5, 20);
            t.Body = null;
            var rows = GeoCompute.Mount(new[] { t }, bodies, Spec("clearance"), null);
            Assert.Equal("ok", rows[0]["class"]);
            Assert.Equal("box", rows[0]["body"]);
            Assert.Equal("1/1", rows[0]["pts"]);
        }

        [Fact]
        public void Mount_Down_MeasuresBaseAgainstFloor()
        {
            var bodies = new List<GeoBody> { Slab(9.25, 10) };
            var rows = GeoCompute.Mount(new[] { Fixture(7, 0, 0, 10.25, 11) }, bodies, Spec("mounting", ("side", "down"), ("against", "slabs")), null);
            Assert.Equal("floating", rows[0]["class"]);
            Assert.Equal(0.25, (double)rows[0]["gap"], 9);
        }

        [Fact]
        public void Near_FindsColumnBesideAndBeamAbove()
        {
            var col = new GeoBody { Label = "column", Box = new Box3(3, -1, 0, 4, 1, 30) };
            var beam = new GeoBody { Label = "beam", Box = new Box3(-10, -0.5, 21, 10, 0.5, 22) };
            var t = Fixture(8, 0, 0, 19, 19.5);
            t.Candidates = new[] { 0, 1 };
            var rows = GeoCompute.Near(new[] { t }, new List<GeoBody> { col, beam }, Spec("near"));
            Assert.Equal("above", rows[0]["class"]);
            Assert.Equal("beam", rows[0]["other"]);
            Assert.Equal(2L, rows[0]["n"]);
        }

        [Fact]
        public void Wall_TouchPenetrateSeparate()
        {
            var wall = new GeoBody { Label = "wall", Box = new Box3(-50, 4.75, 0, 50, 5.25, 30), Line = new[] { -50.0, 5, 50, 5 }, Width = 0.5 };
            var touch = Fixture(1, 0, 3.75, 10, 11, w: 1, d: 1);    // edge at y=4.25 … does not touch
            var flush = Fixture(2, 0, 4.25, 10, 11, w: 1, d: 1);    // edge at y=4.75 = wall face
            var inside = Fixture(3, 0, 4.5, 10, 11, w: 1, d: 1);    // 3" inside
            foreach (var t in new[] { touch, flush, inside }) t.Candidates = new[] { 0 };
            var rows = GeoCompute.Wall(new[] { touch, flush, inside }, new List<GeoBody> { wall }, Spec("wall"));
            Assert.Equal(new[] { "separated", "touches", "penetrates" }, rows.Select(r => (string)r["class"]).ToArray());
            Assert.Equal(0.5, (double)rows[0]["dist"], 9);
        }

        [Fact]
        public void RealLevel_UsesBottomOfBody()
        {
            var levels = new List<GeoLevel> { new GeoLevel { Name = "LEVEL 01", Elevation = 0 }, new GeoLevel { Name = "LEVEL 02", Elevation = 20 }, new GeoLevel { Name = "LEVEL 03", Elevation = 30.75 } };
            var a = Fixture(1, 0, 0, 29.5, 30, lvl: "LEVEL 02");
            var b = Fixture(2, 0, 0, 29.5, 30, lvl: "LEVEL 03");
            a.LevelElev = 20; b.LevelElev = 30.75;
            var rows = GeoCompute.RealLevel(new[] { a, b }, levels);
            Assert.Equal("equal", rows[0]["class"]);
            Assert.Equal("different", rows[1]["class"]);
            Assert.Equal("LEVEL 02", rows[1]["actual_level"]);
        }

        [Fact]
        public void Duplicates_KeepOldest_ReportOthers()
        {
            var t = new[] { Fixture(30, 0, 0, 10, 11), Fixture(10, 0.001, 0, 10, 11), Fixture(20, 50, 0, 10, 11) };
            var rows = GeoCompute.Duplicates(t, Spec("duplicates"));
            Assert.Single(rows);
            Assert.Equal(30L, rows[0]["id"]);
            Assert.Equal(10L, rows[0]["equal_to"]);
        }

        [Fact]
        public void ViewRange_CeilingPlan_InsideAboveForeignAndCrop()
        {
            var v = new GeoView { Id = 1, Name = "LEVEL 02 - RCP", Level = "LEVEL 02", Ceiling = true, Cut = 24, Top = 30.75, Bottom = 20, Depth = 30.75, Crop = new Box3(-100, -100, -1e6, 100, 100, 1e6) };
            var inside = Fixture(1, 0, 0, 28, 29);
            var above = Fixture(2, 0, 0, 31, 32);
            var foreign = Fixture(3, 0, 0, 25, 26, lvl: "LEVEL 01");
            var outside = Fixture(4, 500, 0, 28, 29);
            var rows = GeoCompute.ViewRange(new[] { inside, above, foreign, outside }, new[] { v });
            var map = rows.ToDictionary(r => (long)r["id"], r => (string)r["class"]);
            Assert.Equal("inside", map[1]);
            Assert.Equal("above", map[2]);
            Assert.Equal("foreign", map[3]);
            Assert.Equal("outside", map[4]);
        }

        [Fact]
        public void ClashScreen_ConfirmsVerticalPenetration_AndSkipsFlushContact()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var flush = Fixture(1, 0, 0, 19.5, 20);
            var embedded = Fixture(2, 10, 0, 19.75, 20.25);
            var exact = new List<GeoCompute.PairToCheck>();
            var rows = GeoCompute.ClashScreen(new[] { flush, embedded }, bodies, Spec("clashes"), exact, null);
            Assert.Empty(exact);
            Assert.Equal("free", rows.Single(r => (long)r["id"] == 1)["class"]);
            var c = rows.Single(r => (long)r["id"] == 2);
            Assert.Equal("clash", c["class"]);
            Assert.Equal(0.25, (double)c["prof"], 9);
        }

        [Fact]
        public void MergeClashes_OneRowPerElement_DeepestFirst_BooleanFills_FailedIsReported()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75, "slab"), new GeoBody { Label = "beam", Box = new Box3(-1, -1, 19, 1, 1, 21) }, new GeoBody { Label = "wall" } };
            var a = Fixture(1, 0, 0, 19.75, 20.25);
            var b = Fixture(2, 10, 0, 19.0, 19.5);
            var c = Fixture(3, 20, 0, 19.0, 19.5);
            var d = Fixture(4, 30, 0, 19.0, 19.5);
            var screened = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["id"] = 1L, ["lvl"] = "LEVEL 02", ["class"] = "clash", ["against"] = "slab", ["prof"] = 0.25, ["method"] = "rays" },
                new Dictionary<string, object> { ["id"] = 4L, ["lvl"] = "LEVEL 02", ["class"] = "free" },
            };
            var exact = new[]
            {
                new GeoCompute.ExactResult { Target = 0, Body = 1, Volume = 0.002 },   // 1 also touches the beam
                new GeoCompute.ExactResult { Target = 1, Body = 1, Volume = 0.01 },    // 2 only by boolean
                new GeoCompute.ExactResult { Target = 2, Body = 2, Issues = GeoCompute.ExactIssue.Boolean },
            };
            var rows = GeoCompute.MergeClashes(new[] { a, b, c, d }, bodies, screened, exact, 0.0001);
            Assert.Equal(4, rows.Count);
            Assert.Equal("clash", rows[0]["class"]);
            Assert.Equal("slab", rows[0]["against"]);
            Assert.Equal(2L, rows[0]["n"]);
            Assert.Equal("boolean_op", rows[1]["method"]);
            Assert.Equal("beam", rows[1]["against"]);
            Assert.Equal("not_evaluable", rows[2]["class"]);
            Assert.Equal("free", rows[3]["class"]);

            var spec = Spec("clashes");
            var dsum = GeoSummary.Build(spec, rows, new List<string> { "id", "class", "prof", "vol" }, "class", new Dictionary<string, string> { ["clash"] = "$choques" }, out var sets);
            Assert.Equal(new[] { 1L, 2L }, sets["$choques"].OrderBy(x => x).ToArray());
            var cols = (List<object>)dsum["cols"];
            Assert.Equal(new object[] { "id", "class", "depth_in", "vol_ft3" }, cols.ToArray());
            var first = (object[])((List<object>)dsum["rows"])[0];
            Assert.Equal(3.0, (double)first[2], 6); // 0.25 ft = 3"
        }

        [Fact]
        public void ExactCoverage_DistinguishesMissingSolid_PartialVolume_AndLinkInstances()
        {
            var targets = new[] { Fixture(1, 0, 0, 0, 1), Fixture(2, 0, 0, 0, 1), Fixture(3, 0, 0, 0, 1) };
            var bodies = new[] { new GeoBody { Id = 77, LinkId = 10 }, new GeoBody { Id = 77, LinkId = 20 } };
            var exact = new[]
            {
                new GeoCompute.ExactResult { Target = 0, Body = 0, Issues = GeoCompute.ExactIssue.NoSolid },
                new GeoCompute.ExactResult { Target = 1, Body = 0, Volume = 0.5, Issues = GeoCompute.ExactIssue.Boolean },
                new GeoCompute.ExactResult { Target = 1, Body = 1, Issues = GeoCompute.ExactIssue.Transform | GeoCompute.ExactIssue.Extraction },
                new GeoCompute.ExactResult { Target = 2, Body = 0 },
            };
            var rows = GeoCompute.MergeClashes(targets, bodies, Array.Empty<Dictionary<string, object>>(), exact, 0.0001);
            Assert.Equal("not_evaluable", rows[0]["class"]);
            Assert.Equal("clash", rows[1]["class"]);
            Assert.Equal(true, rows[1]["partial_vol"]);
            Assert.Equal("free", rows[2]["class"]);
            var coverage = Assert.IsType<Dictionary<string, object>>(rows[1]["coverage"]);
            Assert.Equal(2L, coverage["exact_pairs"]);
            Assert.Equal(1L, coverage["unresolved"]);
            Assert.Equal(1L, coverage["partial_volumes"]);
            Assert.Equal(false, coverage["full"]);
            var sample = Assert.IsType<List<object>>(coverage["sample"]).Cast<Dictionary<string, object>>().ToArray();
            Assert.All(sample, x => Assert.Equal(77L, x["element_id"]));
            Assert.Equal(new[] { 10L, 20L }, sample.Select(x => (long)x["link_id"]));
            var summary = GeoSummary.Build(Spec("clashes"), rows, new[] { "id", "class", "vol" }, "class", null, out _);
            Assert.Equal(2, Assert.IsType<List<object>>(summary["coverage"]).Count);
        }

        [Fact]
        public void Summary_ProblemsFirst_SavesSets_ConvertsGapToInches()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var targets = new List<GeoTarget> { Fixture(1, 0, 0, 19.5, 20), Fixture(2, 10, 0, 19, 19.5), Fixture(3, 20, 0, 19.75, 20.125) };
            var spec = Spec("mounting");
            var rows = GeoCompute.Mount(targets, bodies, spec, null);
            var cols = new List<string> { "id", "lvl", "class", "gap", "pts" };
            var d = GeoSummary.Build(spec, rows, cols, "class", new Dictionary<string, string> { ["embedded"] = "$empotradas", ["floating"] = "$flotando" }, out var sets);
            Assert.Equal(new[] { 3L }, sets["$empotradas"]);
            Assert.Equal(new[] { 2L }, sets["$flotando"]);
            var r = (List<object>)d["rows"];
            Assert.Equal(2, r.Count); // problems only
            Assert.Equal(3L, ((object[])r[0])[0]); // embedded first
            Assert.Equal(-1.5, (double)((object[])r[0])[3], 6); // inches
        }
    }
}
