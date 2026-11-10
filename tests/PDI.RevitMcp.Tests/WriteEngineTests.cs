// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Geo;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Piezas del motor de escritura 0.3.0 que no necesitan Revit: comparación tolerante, @n, catálogo y cuerpos movidos.</summary>
    public class WriteEngineTests
    {
        [Fact]
        public void Same_IgnoresFloatNoise_ButNotRealChanges()
        {
            Assert.True(PlanEntry.Same("10.5", "10.500000001"));
            Assert.True(PlanEntry.Same("1,2,3", "1.000001,2,3"));
            Assert.True(PlanEntry.Same("1,2,3|359.999999", "1,2,3|0"));
            Assert.True(PlanEntry.Same("top=LEVEL 10+5|corte=LEVEL 09+0", "top=LEVEL 10+5.000000001|corte=LEVEL 09+0"));
            Assert.False(PlanEntry.Same("10.5", "10.51"));
            Assert.False(PlanEntry.Same("1,2,3", "1,2"));
            Assert.False(PlanEntry.Same("LIGHTING - DIST", "LIGHTING - EMERGENCY - DIST"));
            Assert.False(PlanEntry.Same("top=LEVEL 10+5", "top=LEVEL 11+5"));
            Assert.True(PlanEntry.Same(null, null));
            Assert.False(PlanEntry.Same(null, ""));
        }

        [Fact]
        public void Conflicts_ToleratePredictedValues()
        {
            var p = new Plan();
            p.Entries.Add(new PlanEntry { Id = 1, Op = "move", Key = "loc", Before = "11,5,0" });
            p.Entries.Add(new PlanEntry { Id = 2, Op = "set_elevation", Key = "elev", Before = "10" });
            var now = new Dictionary<long, string> { [1] = "11.0000000001,5,0", [2] = "10.25" };
            var c = p.FindConflicts(e => now[e.Id]);
            Assert.Single(c);
            Assert.Equal(2, c[0].Id);
        }

        [Fact]
        public void RefOps_MustPointBackToCreatingOps()
        {
            var ok = OpSpec.ParseAll(Json.ParseObject(
                "{\"ops\":[{\"op\":\"place\",\"type\":\"F: G\",\"level\":\"LEVEL 09\",\"puntos\":[[1,2],[3,4]],\"montaje\":\"losa\"}," +
                "{\"op\":\"tag\",\"ids\":\"@1\",\"view\":17732282}," +
                "{\"op\":\"set_workset\",\"ids\":\"@1\",\"ws\":\"LIGHTING - DIST\"}]}"));
            Assert.Equal(new List<int> { 1 }, ok[1].RefOps);
            Assert.Equal(new List<int> { 1 }, ok[2].RefOps);

            // @2 apunta a una op que no crea nada; @3 apunta hacia adelante.
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject(
                "{\"ops\":[{\"op\":\"set_elevation\",\"ids\":\"1\",\"ft\":9},{\"op\":\"move\",\"ids\":\"5\",\"dx\":1},{\"op\":\"tag\",\"ids\":\"@2\",\"view\":1}]}")));
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject(
                "{\"ops\":[{\"op\":\"tag\",\"ids\":\"@2\",\"view\":1},{\"op\":\"copy\",\"ids\":\"5\",\"dx\":1}]}")));
            // snap_to_surface mide lo que existe: no admite @n.
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject(
                "{\"ops\":[{\"op\":\"copy\",\"ids\":\"5\",\"dx\":1},{\"op\":\"snap_to_surface\",\"ids\":\"@1\"}]}")));
            // view=@n cuenta como referencia.
            var v = OpSpec.ParseAll(Json.ParseObject(
                "{\"ops\":[{\"op\":\"view_create\",\"tipo\":\"rcp\",\"nivel\":\"LEVEL 09\"},{\"op\":\"view_hide\",\"view\":\"@1\",\"cats\":\"LightingDevices\"}]}"));
            Assert.Equal(new List<int> { 1 }, v[1].RefOps);
        }

        [Fact]
        public void Parse_ValidatesShapes_BeforeTouchingRevit()
        {
            void Bad(string op) => Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + op + "]}")));
            Bad("{\"op\":\"place\",\"type\":\"F: G\",\"level\":\"L9\",\"puntos\":[[1,2]],\"montaje\":\"techo\"}");
            Bad("{\"op\":\"place\",\"type\":\"F: G\",\"level\":\"L9\",\"puntos\":[[1,2]],\"montaje\":\"losa\",\"elev\":9}");
            Bad("{\"op\":\"mirror\",\"ids\":\"1\",\"eje\":[[0,0],[0,0]]}");
            Bad("{\"op\":\"move\",\"ids\":\"1,2\",\"to\":[0,0,0]}");
            Bad("{\"op\":\"view_set\",\"ids\":\"1\",\"rango\":{\"arriba\":[\"L9\",0]}}");
            Bad("{\"op\":\"view_set\",\"ids\":\"1\",\"detalle\":\"altisimo\"}");
            Bad("{\"op\":\"view_create\",\"tipo\":\"seccion\"}");
            Bad("{\"op\":\"snap_to_surface\",\"ids\":\"1\",\"puntos\":4}");
            Bad("{\"op\":\"tag_move\",\"ids\":\"1\"}");
            Bad("{\"op\":\"view_hide\",\"view\":1}");
            var ok = OpSpec.ParseAll(Json.ParseObject("{\"ops\":[{\"op\":\"set_elevation\",\"ids\":\"1\",\"valor\":\"10' - 9\\\"\"}]}"));
            Assert.Equal(10.75, ok[0].Ft.Value, 9);
        }

        [Fact]
        public void Catalog_Has34Ops_AndGuideMentionsEach()
        {
            Assert.Equal(34, OpCatalog.All.Count);   // 0.8.0: +6 de dibujo 2D, +sheet_set
            var guide = OpCatalog.Guide();
            foreach (var i in OpCatalog.All)
            {
                Assert.Contains("- " + i.Kind + " (", guide);
                Assert.NotNull(Json.Parse(i.Example)); // cada ejemplo es JSON válido
                OpSpec.Parse(Json.ParseObject(i.Example.Replace("\"@2\"", "\"10\"").Replace("\"@1\"", "\"11\""))); // y pasa la validación
            }
            Assert.Equal("Colocar", OpCatalog.NameOf("place"));
        }

        [Fact]
        public void BuildMoved_TranslatesAndRotatesBodies()
        {
            var b = new TriMeshBuilder();
            MeshTests.Box(b, 0, 0, 0, 2, 1, 0.5); // 2' x 1' x 6"
            var sc = new RayScratch();
            // 90° alrededor de (0,0) y luego a (10, 20, 9): la caja queda en x∈[9,10], y∈[20,22], z∈[9,9.5].
            var m = b.BuildMoved(0, 0, 0, 1, 10, 20, 9);
            Assert.Equal(9, m.Bounds.MinX, 9);
            Assert.Equal(10, m.Bounds.MaxX, 9);
            Assert.Equal(20, m.Bounds.MinY, 9);
            Assert.Equal(22, m.Bounds.MaxY, 9);
            Assert.Equal(RayResult.Ok, m.Extent(9.5, 21.5, sc, out var bot, out var top));
            Assert.Equal(9, bot, 9);
            Assert.Equal(9.5, top, 9);
            Assert.NotEqual(RayResult.Ok, m.Extent(5, 21.5, sc, out _, out _));
            // La original no cambia.
            var o = b.Build();
            Assert.Equal(0, o.Bounds.MinX, 9);
            Assert.Equal(2, o.Bounds.MaxX, 9);
        }
    }
}
