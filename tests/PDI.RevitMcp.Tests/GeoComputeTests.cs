// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Geo;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Recetas de geo sobre escenas sintéticas (las mismas situaciones de SAMPLE PROJECT: losa, rampa, viga, muro).</summary>
    public class GeoComputeTests
    {
        private static GeoBody Slab(double z0, double z1, string label = "losa", double x0 = -50, double y0 = -50, double x1 = 50, double y1 = 50)
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
                Fixture(1, 0, 0, 19.5, 20),                    // cara superior = cara inferior de la losa
                Fixture(2, 10, 0, 19.0, 19.5),                 // 6" de aire
                Fixture(3, 20, 0, 19.75, 20.125),              // 1 1/2" dentro de la losa
                Fixture(4, 30, 0, 19.5, 20.0 + 1.0 / 16 / 12), // 1/16": dentro de la tolerancia
            };
            var rows = GeoCompute.Mount(targets, bodies, Spec("montaje"), new GeoCompute.Stats());
            Assert.Equal(new[] { "ok", "flotando", "empotrada", "ok" }, rows.Select(r => (string)r["clase"]).ToArray());
            Assert.Equal(0.5, (double)rows[1]["gap"], 9);
            Assert.Equal(-0.125, (double)rows[2]["gap"], 9);
            Assert.Equal("9/9", rows[0]["pts"]);
            Assert.Equal("losa", rows[0]["contra"]);
        }

        [Fact]
        public void Mount_UnderRamp_ControllingCornerIsTheLowestUnderside()
        {
            var rb = new TriMeshBuilder();
            MeshTests.Ramp(rb, -20, -20, 20, 20, 15, 4, 1); // cara inferior: z = 15 + 4 (x + 20)/40
            var bodies = new List<GeoBody> { new GeoBody { Label = "rampa", Box = new Box3(-20, -20, 15, 20, 20, 20), Mesh = rb.Build() } };
            // Luminaria de 2×4 centrada en x=0: el borde izquierdo controla. Se mide 1/2" hacia adentro del borde (x = -1 + 1/24),
            // donde la cara inferior está en 15 + 0.1 (x + 20) = 16.9 + 0.1/24.
            var t = Fixture(9, 0, 0, 16.4, 16.9 + 0.1 / 24);
            var rows = GeoCompute.Mount(new[] { t }, bodies, Spec("montaje"), null);
            Assert.Equal("ok", rows[0]["clase"]);
            Assert.True(rows[0].ContainsKey("pend"));
            // Subir 1/8" la deja empotrada en la esquina baja aunque el centro tenga aire.
            var t2 = Fixture(10, 0, 0, 16.5, 16.9 + 1.0 / 96);
            var r2 = GeoCompute.Mount(new[] { t2 }, bodies, Spec("montaje"), null);
            Assert.Equal("empotrada", r2[0]["clase"]);
        }

        [Fact]
        public void Mount_WithoutSurface_IsReported_NotInvented()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75, x0: 100, x1: 120) };
            var rows = GeoCompute.Mount(new[] { Fixture(5, 0, 0, 19, 19.5) }, bodies, Spec("montaje"), null);
            Assert.Equal("sin_superficie", rows[0]["clase"]);
            Assert.True(double.IsNaN((double)rows[0]["gap"]));
        }

        [Fact]
        public void Mount_FallsBackToBox_WhenBodyHasNoMesh()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var t = Fixture(6, 0, 0, 19.5, 20);
            t.Body = null;
            var rows = GeoCompute.Mount(new[] { t }, bodies, Spec("holgura"), null);
            Assert.Equal("ok", rows[0]["clase"]);
            Assert.Equal("caja", rows[0]["cuerpo"]);
            Assert.Equal("1/1", rows[0]["pts"]);
        }

        [Fact]
        public void Mount_Down_MeasuresBaseAgainstFloor()
        {
            var bodies = new List<GeoBody> { Slab(9.25, 10) };
            var rows = GeoCompute.Mount(new[] { Fixture(7, 0, 0, 10.25, 11) }, bodies, Spec("montaje", ("lado", "abajo"), ("contra", "losas")), null);
            Assert.Equal("flotando", rows[0]["clase"]);
            Assert.Equal(0.25, (double)rows[0]["gap"], 9);
        }

        [Fact]
        public void Near_FindsColumnBesideAndBeamAbove()
        {
            var col = new GeoBody { Label = "columna", Box = new Box3(3, -1, 0, 4, 1, 30) };
            var beam = new GeoBody { Label = "viga", Box = new Box3(-10, -0.5, 21, 10, 0.5, 22) };
            var t = Fixture(8, 0, 0, 19, 19.5);
            t.Candidates = new[] { 0, 1 };
            var rows = GeoCompute.Near(new[] { t }, new List<GeoBody> { col, beam }, Spec("cerca"));
            Assert.Equal("encima", rows[0]["clase"]);
            Assert.Equal("viga", rows[0]["otro"]);
            Assert.Equal(2L, rows[0]["n"]);
        }

        [Fact]
        public void Wall_TouchPenetrateSeparate()
        {
            var wall = new GeoBody { Label = "muro", Box = new Box3(-50, 4.75, 0, 50, 5.25, 30), Line = new[] { -50.0, 5, 50, 5 }, Width = 0.5 };
            var touch = Fixture(1, 0, 3.75, 10, 11, w: 1, d: 1);    // borde en y=4.25 … no toca
            var flush = Fixture(2, 0, 4.25, 10, 11, w: 1, d: 1);    // borde en y=4.75 = cara del muro
            var inside = Fixture(3, 0, 4.5, 10, 11, w: 1, d: 1);    // 3" dentro
            foreach (var t in new[] { touch, flush, inside }) t.Candidates = new[] { 0 };
            var rows = GeoCompute.Wall(new[] { touch, flush, inside }, new List<GeoBody> { wall }, Spec("muro"));
            Assert.Equal(new[] { "separada", "toca", "penetra" }, rows.Select(r => (string)r["clase"]).ToArray());
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
            Assert.Equal("igual", rows[0]["clase"]);
            Assert.Equal("distinto", rows[1]["clase"]);
            Assert.Equal("LEVEL 02", rows[1]["nivel_real"]);
        }

        [Fact]
        public void Duplicates_KeepOldest_ReportOthers()
        {
            var t = new[] { Fixture(30, 0, 0, 10, 11), Fixture(10, 0.001, 0, 10, 11), Fixture(20, 50, 0, 10, 11) };
            var rows = GeoCompute.Duplicates(t, Spec("duplicados"));
            Assert.Single(rows);
            Assert.Equal(30L, rows[0]["id"]);
            Assert.Equal(10L, rows[0]["igual_a"]);
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
            var map = rows.ToDictionary(r => (long)r["id"], r => (string)r["clase"]);
            Assert.Equal("dentro", map[1]);
            Assert.Equal("encima", map[2]);
            Assert.Equal("ajena", map[3]);
            Assert.Equal("fuera", map[4]);
        }

        [Fact]
        public void ClashScreen_ConfirmsVerticalPenetration_AndSkipsFlushContact()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var flush = Fixture(1, 0, 0, 19.5, 20);
            var embedded = Fixture(2, 10, 0, 19.75, 20.25);
            var exact = new List<GeoCompute.PairToCheck>();
            var rows = GeoCompute.ClashScreen(new[] { flush, embedded }, bodies, Spec("choques"), exact, null);
            Assert.Empty(exact);
            Assert.Equal("libre", rows.Single(r => (long)r["id"] == 1)["clase"]);
            var c = rows.Single(r => (long)r["id"] == 2);
            Assert.Equal("choque", c["clase"]);
            Assert.Equal(0.25, (double)c["prof"], 9);
        }

        [Fact]
        public void MergeClashes_OneRowPerElement_DeepestFirst_BooleanFills_FailedIsReported()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75, "losa"), new GeoBody { Label = "viga", Box = new Box3(-1, -1, 19, 1, 1, 21) }, new GeoBody { Label = "muro" } };
            var a = Fixture(1, 0, 0, 19.75, 20.25);
            var b = Fixture(2, 10, 0, 19.0, 19.5);
            var c = Fixture(3, 20, 0, 19.0, 19.5);
            var d = Fixture(4, 30, 0, 19.0, 19.5);
            var screened = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["id"] = 1L, ["lvl"] = "LEVEL 02", ["clase"] = "choque", ["contra"] = "losa", ["prof"] = 0.25, ["metodo"] = "rayos" },
                new Dictionary<string, object> { ["id"] = 4L, ["lvl"] = "LEVEL 02", ["clase"] = "libre" },
            };
            var exact = new[]
            {
                new GeoCompute.ExactResult { Target = 0, Body = 1, Volume = 0.002 },   // 1 también toca la viga
                new GeoCompute.ExactResult { Target = 1, Body = 1, Volume = 0.01 },    // 2 sólo por booleana
                new GeoCompute.ExactResult { Target = 2, Body = 2, Failed = true },    // 3: Revit no pudo
            };
            var rows = GeoCompute.MergeClashes(new[] { a, b, c, d }, bodies, screened, exact, 0.0001);
            Assert.Equal(4, rows.Count);
            Assert.Equal("choque", rows[0]["clase"]);
            Assert.Equal("losa", rows[0]["contra"]);
            Assert.Equal(2L, rows[0]["n"]);
            Assert.Equal("booleana", rows[1]["metodo"]);
            Assert.Equal("viga", rows[1]["contra"]);
            Assert.Equal("no_evaluable", rows[2]["clase"]);
            Assert.Equal("libre", rows[3]["clase"]);

            var spec = Spec("choques");
            var dsum = GeoSummary.Build(spec, rows, new List<string> { "id", "clase", "prof", "vol" }, "clase", new Dictionary<string, string> { ["choque"] = "$choques" }, out var sets);
            Assert.Equal(new[] { 1L, 2L }, sets["$choques"].OrderBy(x => x).ToArray());
            var cols = (List<object>)dsum["cols"];
            Assert.Equal(new object[] { "id", "clase", "prof_in", "vol_ft3" }, cols.ToArray());
            var first = (object[])((List<object>)dsum["rows"])[0];
            Assert.Equal(3.0, (double)first[2], 6); // 0.25 ft = 3"
        }

        [Fact]
        public void Summary_ProblemsFirst_SavesSets_ConvertsGapToInches()
        {
            var bodies = new List<GeoBody> { Slab(20, 20.75) };
            var targets = new List<GeoTarget> { Fixture(1, 0, 0, 19.5, 20), Fixture(2, 10, 0, 19, 19.5), Fixture(3, 20, 0, 19.75, 20.125) };
            var spec = Spec("montaje");
            var rows = GeoCompute.Mount(targets, bodies, spec, null);
            var cols = new List<string> { "id", "lvl", "clase", "gap", "pts" };
            var d = GeoSummary.Build(spec, rows, cols, "clase", new Dictionary<string, string> { ["empotrada"] = "$empotradas", ["flotando"] = "$flotando" }, out var sets);
            Assert.Equal(new[] { 3L }, sets["$empotradas"]);
            Assert.Equal(new[] { 2L }, sets["$flotando"]);
            var r = (List<object>)d["rows"];
            Assert.Equal(2, r.Count); // sólo problemas
            Assert.Equal(3L, ((object[])r[0])[0]); // empotrada primero
            Assert.Equal(-1.5, (double)((object[])r[0])[3], 6); // pulgadas
        }
    }
}
