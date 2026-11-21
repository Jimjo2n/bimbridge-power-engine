// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using BimBridge.PowerEngine;
using BimBridge.PowerEngine.Workflows;
using Xunit;

namespace Bimbridge.Tests
{
    public class AdvancedEnginesTests
    {
        private static RunSegment S(string id, double ax, double ay, double bx, double by, string ca = null, string cb = null, double d = 0.75 / 12, string sys = "LTG") =>
            new RunSegment { Id = id, Ax = ax, Ay = ay, Bx = bx, By = by, Az = 10, Bz = 10, ConnA = ca, ConnB = cb, DiameterFt = d, System = sys };

        [Fact]
        public void Mep_ThreeLayersStaySeparate()
        {
            var segs = new List<RunSegment>
            {
                S("c1", 0, 0, 10, 0, null, "c2"),
                S("c2", 10, 0, 20, 0, "c1", null),             // connected and continuous
                S("c3", 20, 0, 30, 0),                         // touches c2 but has no connector: toca_sin_conectar
                S("c4", 30.2, 0, 40, 0),                       // 0.2 ft from c3: proximity, not a connection
                S("c5", 50, 0, 60, 0, null, "c6", 1.0 / 12),   // connected to c6 with a 1 ft gap and a different diameter
                S("c6", 61, 0, 70, 0, "c5", null),
                S("c7", 80, 0, 90, 0, null, "EQ-1"),           // connected to equipment outside the set
            };
            foreach (var eng in new[] { null, new PowerEngineDispatcher(PowerEngineEnv.Options("rust_ffi")) })
            {
                var r = MepNetwork.Analyze(segs, engine: eng);
                Assert.Equal("discrepancies", r["verdict"]);
                Assert.Single((List<object>)r["touches_unconnected"]);
                Assert.Single((List<object>)r["connected_with_gap"]);
                Assert.Contains((List<object>)r["incompatible_items"], x => ((string)((Dictionary<string, object>)x)["reason"]).StartsWith("diameter"));
                var near = (List<object>)r["near_unconnected"];
                Assert.Single(near);
                Assert.Equal(new List<object> { "c3", "c4" }, ((Dictionary<string, object>)near[0])["segments"]);
                Assert.Single((List<object>)r["connected_outside_set"]);
                var layers = (Dictionary<string, object>)r["layers"];
                // continuity: {c1,c2,c3}, {c4}, {c5}, {c6}, {c7}; physical: {c1,c2}, {c3}, {c4}, {c5,c6}, {c7}
                Assert.Equal(5L, ((Dictionary<string, object>)layers["continuity"])["components"]);
                Assert.Equal(5L, ((Dictionary<string, object>)layers["physical_connection"])["components"]);
                eng?.Dispose();
            }
            Assert.Throws<ArgumentException>(() => MepNetwork.Analyze(new List<RunSegment> { S("x", 0, 0, 1, 0), S("x", 1, 0, 2, 0) }));
        }

        [Fact]
        public void Riser_DiagramIsNotPhysical_FindsOpenEndsDuplicatesMissingAndCrossings()
        {
            var nodes = new List<RiserNode>
            {
                new RiserNode { Id = "P1", Kind = "panel", Level = "L01", LevelElevFt = 0 },
                new RiserNode { Id = "J2", Kind = "union", Level = "L02", LevelElevFt = 12 },
                new RiserNode { Id = "J3", Kind = "union", Level = "L03", LevelElevFt = 24 },   // open end (junction with no outlet)
                new RiserNode { Id = "D2", Kind = "device", Level = "L02", LevelElevFt = 12 },
                new RiserNode { Id = "F-7", Kind = "terminal", Level = "L02", LevelElevFt = 12 },
                new RiserNode { Id = "F-7", Kind = "terminal", Level = "L03", LevelElevFt = 24 },  // valid repeated ID (feeder)
                new RiserNode { Id = "X9", Kind = "union", Level = "L03", LevelElevFt = 24 },   // invalid repeat
                new RiserNode { Id = "X9", Kind = "union", Level = "L01", LevelElevFt = 0 },
            };
            var edges = new List<RiserEdge>
            {
                new RiserEdge { From = "P1", To = "J2", Kind = "physical", PhysicalId = "c-101" },
                new RiserEdge { From = "J2", To = "J3", Kind = "diagram" },
                new RiserEdge { From = "J2", To = "D2", Kind = "physical", PhysicalId = "c-102" },
                new RiserEdge { From = "P1", To = "F-7", Kind = "physical", PhysicalId = "c-103" },
                new RiserEdge { From = "X9", To = "D2", Kind = "physical", PhysicalId = "c-104" },
                new RiserEdge { From = "J3", To = "NOEXISTE" },
            };
            var r = RiserGraph.Analyze(nodes, edges, new[] { "F-7" }, new[] { ("J3", "D2") });
            Assert.Equal("review", r["verdict"]);
            Assert.Single((List<object>)r["diagram_only"]);
            Assert.Contains("J3", (List<object>)r["open_ends"]);
            Assert.Equal(new List<object> { "X9" }, r["repeated_invalid_ids"]);
            Assert.Equal(new List<object> { "F-7" }, r["repeated_valid_ids"]);
            Assert.Equal(new List<object> { "J3→D2" }, r["missing_branches"]);
            Assert.Single((List<object>)r["edges_without_node"]);
            Assert.True((long)r["physical_components"] > (long)r["diagram_components"]);
            var drawing = (Dictionary<string, object>)r["drafting"];
            Assert.True(drawing.ContainsKey("crossings"));
        }

        private static GroupInstance G(string id, string type, double ox, double oy, double rot, bool mirror, params (string T, double X, double Y)[] local)
        {
            var g = new GroupInstance { Id = id, GroupType = type, Ox = ox, Oy = oy, RotDeg = rot, Mirrored = mirror, Level = "L" + (id.Length % 3) };
            var c = Math.Cos(rot * Math.PI / 180); var s = Math.Sin(rot * Math.PI / 180);
            foreach (var m in local)
            {
                var lx = mirror ? -m.X : m.X;
                g.Members.Add(new GroupMember { Type = m.T, X = ox + c * lx - s * m.Y, Y = oy + s * lx + c * m.Y, RotDeg = rot + (mirror ? 180 : 0) });
            }
            return g;
        }

        [Fact]
        public void Groups_VariantsDetectedUnderRotationAndMirror()
        {
            var unit = new[] { ("D", 2.0, 3.0), ("D", 8.0, 3.0), ("G", 5.0, 10.0) };
            var groups = new List<GroupInstance>
            {
                G("U1", "UNIT A", 0, 0, 0, false, unit),
                G("U2", "UNIT A", 100, 0, 90, false, unit),
                G("U3", "UNIT A", 0, 100, 0, true, unit),                        // mirrored: same signature
                G("U4", "UNIT A", 200, 0, 0, false, unit.Take(2).ToArray()),     // missing the G
                G("U5", "UNIT B", 0, 300, 0, false, unit),
            };
            var r = GroupVariants.Analyze(groups);
            var a = (Dictionary<string, object>)((List<object>)r["types"])[0];
            Assert.Equal("UNIT A", a["group_type"]);
            Assert.Equal(2L, a["variants"]);
            Assert.Equal(3L, ((Dictionary<string, object>)a["reference"])["instances"]);
            var dev = (Dictionary<string, object>)((List<object>)a["deviations"])[0];
            Assert.Equal(new List<object> { "U4" }, dev["instances"]);
            Assert.Single((List<object>)dev["missing_items"]);
            Assert.StartsWith("G|", (string)((List<object>)dev["missing_items"])[0]);
            Assert.Equal(new List<object> { "U3" }, a["mirrored"]);
        }

        [Fact]
        public void Annotation_NoCollisions_WhenSpaceExists_LeaderWhenFar_LegibilityChecked()
        {
            var tags = Enumerable.Range(0, 12).Select(i => new TagRequest { Id = "t" + i, X = 1 + (i % 4) * 0.5, Y = 1 + (i / 4) * 0.4 }).ToList();
            var obstacles = tags.Select(t => new Box { Id = "sym-" + t.Id, X0 = t.X - 0.04, X1 = t.X + 0.04, Y0 = t.Y - 0.04, Y1 = t.Y + 0.04 }).ToList();
            var r = AnnotationLayout.Layout(tags, obstacles);
            Assert.Equal(0L, r["collisions"]);
            Assert.True((bool)r["readable"]);
            Assert.False((bool)AnnotationLayout.Layout(tags, obstacles, textHeightIn: 1.0 / 16)["readable"]);
            // crowded anchor: the tag moves away and gets a leader
            var crowd = new List<Box> { new Box { X0 = 4.0, X1 = 6.0, Y0 = 4.0, Y1 = 6.0 } };
            var one = AnnotationLayout.Layout(new List<TagRequest> { new TagRequest { Id = "x", X = 5, Y = 5 } }, crowd);
            var tag = (Dictionary<string, object>)((List<object>)one["tags_list"])[0];
            Assert.True((bool)tag["collision"] || (bool)tag["leader"]);
        }
    }
}
