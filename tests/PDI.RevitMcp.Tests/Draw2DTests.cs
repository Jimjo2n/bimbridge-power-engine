// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0 paquete C: geometría 2D compartida y validación de las operaciones de dibujo antes de tocar Revit.</summary>
    public class Draw2DTests
    {
        private static List<OpSpec> Parse(string json) => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + json + "]}"));

        [Fact]
        public void DrawingOps_ValidInputs_Parse()
        {
            var ops = Parse(
                "{\"op\":\"view_create\",\"tipo\":\"dibujo\",\"nombre\":\"RISER\",\"escala\":96}," +
                "{\"op\":\"detail_line\",\"view\":\"@1\",\"puntos\":[[0,0],[10,0],[10,5]],\"estilo\":\"PDI - MAGENTA\"}," +
                "{\"op\":\"detail_rect\",\"view\":\"@1\",\"esquinas\":[[0,0],[4,2]]}," +
                "{\"op\":\"detail_arc\",\"view\":\"@1\",\"centro\":[5,5],\"radio\":\"6 in\",\"inicio\":0,\"fin\":180}," +
                "{\"op\":\"detail_polygon\",\"view\":\"@1\",\"centro\":[2,2],\"radio\":0.5,\"lados\":6}," +
                "{\"op\":\"detail_component\",\"view\":\"@1\",\"tipo\":\"PANEL: 42\",\"punto\":[3,12]}," +
                "{\"op\":\"text_note\",\"view\":\"@1\",\"punto\":[1,1],\"texto\":\"MDP\"}," +
                "{\"op\":\"dimension\",\"view\":123,\"refs\":\"171001,171002\",\"linea\":[[0,-1],[10,-1]]}");
            Assert.Equal(8, ops.Count);
        }

        [Theory]
        [InlineData("{\"op\":\"detail_line\",\"view\":1,\"puntos\":[[0,0],[0.001,0]]}")]                          // tramo más corto que Revit
        [InlineData("{\"op\":\"detail_line\",\"view\":1,\"puntos\":[[0,0],[1,0]],\"cerrado\":true}")]           // cerrado con 2 puntos
        [InlineData("{\"op\":\"detail_rect\",\"view\":1,\"esquinas\":[[0,0],[4,0]]}")]                           // sin alto
        [InlineData("{\"op\":\"detail_arc\",\"view\":1,\"centro\":[0,0],\"radio\":1,\"inicio\":0,\"fin\":360}")] // círculo completo
        [InlineData("{\"op\":\"detail_arc\",\"view\":1,\"centro\":[0,0],\"radio\":0,\"inicio\":0,\"fin\":90}")]
        [InlineData("{\"op\":\"detail_polygon\",\"view\":1,\"centro\":[0,0],\"radio\":1,\"lados\":2}")]
        [InlineData("{\"op\":\"filled_region\",\"view\":1,\"puntos\":[[0,0],[2,2],[2,0],[0,2]]}")]              // pajarita (se cruza)
        [InlineData("{\"op\":\"dimension\",\"view\":1,\"refs\":\"5\",\"linea\":[[0,0],[1,0]]}")]                  // 1 sola referencia
        [InlineData("{\"op\":\"dimension\",\"view\":1,\"refs\":\"@2,@3\",\"linea\":[[0,0],[1,0]]}")]              // @n no admitido aún
        [InlineData("{\"op\":\"view_create\",\"tipo\":\"leyenda\"}")]
        public void DrawingOps_InvalidInputs_RejectedBeforeRevit(string json)
        {
            Assert.Throws<QueryException>(() => Parse(json));
        }

        [Fact]
        public void Geometry_RectPolygonArea_AndPaperViewRoundTrip()
        {
            var r = Draw2D.Rect(new double[] { 4, 2 }, new double[] { 0, 0 }, out var e);
            Assert.Null(e);
            Assert.Equal(8, Draw2D.SignedArea(r), 6);
            var hex = Draw2D.RegularPolygon(new double[] { 0, 0 }, 1, 6, 0, out _);
            Assert.Equal(6, hex.Count);
            Assert.Null(Draw2D.ValidateLoop(hex));
            var vc = new Draw2D.P(100, 50); var sc = new Draw2D.P(1.5, 1.0);
            var onSheet = Draw2D.ViewToSheet(new Draw2D.P(196, 50), vc, sc, 96);
            Assert.Equal(2.5, onSheet.X, 9);
            var back = Draw2D.SheetToView(onSheet, vc, sc, 96);
            Assert.Equal(196, back.X, 9);
            Assert.Equal(0.125 * 96, Draw2D.PaperToView(0.125, 96), 9);
            var rot = Draw2D.Rotate(new Draw2D.P(1, 0), new Draw2D.P(0, 0), 90);
            Assert.Equal(1, rot.Y, 9);
            Assert.Equal(new Draw2D.P(-1, 0).X, Draw2D.AlignDelta(new Draw2D.P(1, 0), new Draw2D.P(3, 1), 0, "izquierda").X, 9);
            var d = Draw2D.Distribute(new List<(double, double)> { (0, 2), (0, 3) }, 10, 1);
            Assert.Equal(new List<double> { 10, 13 }, d);
        }
    }
}
