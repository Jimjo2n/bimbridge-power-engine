// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.8.0 package C: shared 2D geometry and validation of drawing operations before touching Revit.</summary>
    public class Draw2DTests
    {
        [Fact]
        public void ManagedCalloutDetectsAChangedTypeHeightOrAnchor()
        {
            var text = LightingCalloutState.Text(" LT ", 8.5);
            Assert.Equal("LT 8'-6\"", text);
            Assert.True(LightingCalloutState.IsCurrent(text, "typ-a", text, "typ-a", 0.002));
            Assert.False(LightingCalloutState.IsCurrent(text, "typ-a", text, "typ-b", 0));
            Assert.False(LightingCalloutState.IsCurrent(text, "typ-a", LightingCalloutState.Text("LT", 8.75), "typ-a", 0));
            Assert.False(LightingCalloutState.IsCurrent(text, "typ-a", text, "typ-a", 0.004));
        }

        private static List<OpSpec> Parse(string json) => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + json + "]}"));

        [Fact]
        public void CoordinationSheetKeepsEachViewportBoundToItsPlannedView()
        {
            var ops = Parse("""
                {"op":"view_create","type":"section","name":"L15 corte","box":[[0,0,9],[12,0,16]],"depth":6},
                {"op":"view_create","type":"3d","name":"L15 sistemas","box":[[0,0,9],[12,8,16]]},
                {"op":"view_create","type":"callout","name":"L15 equipo","source":101,"box":[[0,0,9],[4,4,9]]},
                {"op":"sheet_create","number":"QC-15","name":"Coordinación"},
                {"op":"sheet_place","sheet":"@4","view":"@1","point":[1,1]},
                {"op":"sheet_place","sheet":"@4","view":"@2","point":[2,1]},
                {"op":"sheet_place","sheet":"@4","view":"@3","point":[1,2]}
                """);
            Assert.Equal(7, ops.Count);
            for (var index = 0; index < 3; index++)
            {
                Assert.Equal("sheet_place", ops[index + 4].Kind);
                Assert.Equal("@4", ops[index + 4].Args["sheet"]);
                Assert.Equal("@" + (index + 1), ops[index + 4].Args["view"]);
            }
        }

        [Fact]
        public void DrawingOps_ValidInputs_Parse()
        {
            var ops = Parse(
                "{\"op\":\"view_create\",\"type\":\"drafting\",\"name\":\"RISER\",\"scale\":96}," +
                "{\"op\":\"detail_line\",\"view\":\"@1\",\"points\":[[0,0],[10,0],[10,5]],\"style\":\"Lines - Magenta\"}," +
                "{\"op\":\"detail_rect\",\"view\":\"@1\",\"corners\":[[0,0],[4,2]]}," +
                "{\"op\":\"detail_arc\",\"view\":\"@1\",\"center\":[5,5],\"radius\":\"6 in\",\"start\":0,\"end\":180}," +
                "{\"op\":\"detail_polygon\",\"view\":\"@1\",\"center\":[2,2],\"radius\":0.5,\"sides\":6}," +
                "{\"op\":\"detail_component\",\"view\":\"@1\",\"type\":\"PANEL: 42\",\"point\":[3,12]}," +
                "{\"op\":\"text_note\",\"view\":\"@1\",\"point\":[1,1],\"text\":\"MDP\"}," +
                "{\"op\":\"dimension\",\"view\":123,\"refs\":\"171001,171002\",\"line\":[[0,-1],[10,-1]]}");
            Assert.Equal(8, ops.Count);
        }

        [Theory]
        [InlineData("{\"op\":\"detail_line\",\"view\":1,\"points\":[[0,0],[0.001,0]]}")]                          // segment shorter than Revit allows
        [InlineData("{\"op\":\"detail_line\",\"view\":1,\"points\":[[0,0],[1,0]],\"closed\":true}")]           // closed with 2 points
        [InlineData("{\"op\":\"detail_rect\",\"view\":1,\"corners\":[[0,0],[4,0]]}")]                           // no height
        [InlineData("{\"op\":\"detail_arc\",\"view\":1,\"center\":[0,0],\"radius\":1,\"start\":0,\"end\":360}")] // full circle
        [InlineData("{\"op\":\"detail_arc\",\"view\":1,\"center\":[0,0],\"radius\":0,\"start\":0,\"end\":90}")]
        [InlineData("{\"op\":\"detail_polygon\",\"view\":1,\"center\":[0,0],\"radius\":1,\"sides\":2}")]
        [InlineData("{\"op\":\"filled_region\",\"view\":1,\"points\":[[0,0],[2,2],[2,0],[0,2]]}")]              // bow tie (self-intersecting)
        [InlineData("{\"op\":\"dimension\",\"view\":1,\"refs\":\"5\",\"line\":[[0,0],[1,0]]}")]                  // only 1 reference
        [InlineData("{\"op\":\"dimension\",\"view\":1,\"refs\":\"@2,@3\",\"line\":[[0,0],[1,0]]}")]              // @n not supported yet
        [InlineData("{\"op\":\"view_create\",\"type\":\"legend\"}")]
        public void DrawingOps_InvalidInputs_RejectedBeforeRevit(string json)
        {
            Assert.Throws<QueryException>(() => Parse(json));
        }

        [Fact]
        public void ManagedLightingNotesUseARealSourceAndRefreshBySelectedIds()
        {
            var ops = Parse("{\"op\":\"text_note\",\"view\":123,\"point\":[8,9],\"fixture\":456,\"subtract_type_param\":\"Height\"}," +
                "{\"op\":\"text_note_refresh\",\"ids\":\"$llamadas_desactualizadas\"}");
            Assert.Equal(456L, Arg.GetLong(ops[0].Args, "fixture"));
            Assert.Equal("$llamadas_desactualizadas", ops[1].Set);
            Assert.Throws<QueryException>(() => Parse("{\"op\":\"text_note\",\"view\":1,\"point\":[8,9],\"fixture\":2,\"text\":\"manual\"}"));
            Assert.Throws<QueryException>(() => Parse("{\"op\":\"text_note\",\"view\":1,\"point\":[8,9]}"));
            Assert.Throws<QueryException>(() => Parse("{\"op\":\"text_note_refresh\"}"));
        }

        [Fact]
        public void AnnotationRepairRequiresOneAnnotationAndExplicitDestinationReferences()
        {
            Assert.Single(Parse("{\"op\":\"annotation_rebind\",\"ids\":[101],\"target\":202}"));
            Assert.Single(Parse("{\"op\":\"annotation_rebind\",\"ids\":[101],\"stable_refs\":[\"uidA:LINEAR\",\"uidB:LINEAR\"]}"));
            Assert.Throws<QueryException>(() => Parse("{\"op\":\"annotation_rebind\",\"ids\":[101,102],\"target\":202}"));
            Assert.Throws<QueryException>(() => Parse("{\"op\":\"annotation_rebind\",\"ids\":[101],\"stable_refs\":[\"same\",\"same\"]}"));
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
            Assert.Equal(new Draw2D.P(-1, 0).X, Draw2D.AlignDelta(new Draw2D.P(1, 0), new Draw2D.P(3, 1), 0, "left").X, 9);
            var d = Draw2D.Distribute(new List<(double, double)> { (0, 2), (0, 3) }, 10, 1);
            Assert.Equal(new List<double> { 10, 13 }, d);
        }
    }
}
