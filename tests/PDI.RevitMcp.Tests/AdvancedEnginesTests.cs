// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using BimBridge.PowerEngine;
using BimBridge.PowerEngine.Workflows;
using Xunit;

namespace PDI.RevitMcp.Tests
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
                S("c2", 10, 0, 20, 0, "c1", null),             // conectado y continuo
                S("c3", 20, 0, 30, 0),                         // toca c2 pero sin conector: toca_sin_conectar
                S("c4", 30.2, 0, 40, 0),                       // a 0,2 ft de c3: proximidad, no conexión
                S("c5", 50, 0, 60, 0, null, "c6", 1.0 / 12),   // conectado a c6 con un hueco de 1 ft y otro diámetro
                S("c6", 61, 0, 70, 0, "c5", null),
                S("c7", 80, 0, 90, 0, null, "EQ-1"),           // conectado a un equipo fuera del conjunto
            };
            foreach (var eng in new[] { null, new PowerEngineDispatcher(PowerEngineEnv.Options("rust_ffi")) })
            {
                var r = MepNetwork.Analyze(segs, engine: eng);
                Assert.Equal("discrepancias", r["veredicto"]);
                Assert.Single((List<object>)r["toca_sin_conectar"]);
                Assert.Single((List<object>)r["conectado_con_hueco"]);
                Assert.Contains((List<object>)r["incompatibles"], x => ((string)((Dictionary<string, object>)x)["motivo"]).StartsWith("diámetro"));
                var near = (List<object>)r["cerca_sin_conectar"];
                Assert.Single(near);
                Assert.Equal(new List<object> { "c3", "c4" }, ((Dictionary<string, object>)near[0])["tramos"]);
                Assert.Single((List<object>)r["conectado_fuera_del_conjunto"]);
                var capas = (Dictionary<string, object>)r["capas"];
                // continuidad: {c1,c2,c3}, {c4}, {c5}, {c6}, {c7}; física: {c1,c2}, {c3}, {c4}, {c5,c6}, {c7}
                Assert.Equal(5L, ((Dictionary<string, object>)capas["continuidad"])["componentes"]);
                Assert.Equal(5L, ((Dictionary<string, object>)capas["conexion_fisica"])["componentes"]);
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
                new RiserNode { Id = "J3", Kind = "union", Level = "L03", LevelElevFt = 24 },   // extremo abierto (unión sin salida)
                new RiserNode { Id = "D2", Kind = "dispositivo", Level = "L02", LevelElevFt = 12 },
                new RiserNode { Id = "F-7", Kind = "terminal", Level = "L02", LevelElevFt = 12 },
                new RiserNode { Id = "F-7", Kind = "terminal", Level = "L03", LevelElevFt = 24 },  // ID repetido válido (feeder)
                new RiserNode { Id = "X9", Kind = "union", Level = "L03", LevelElevFt = 24 },   // repetido inválido
                new RiserNode { Id = "X9", Kind = "union", Level = "L01", LevelElevFt = 0 },
            };
            var edges = new List<RiserEdge>
            {
                new RiserEdge { From = "P1", To = "J2", Kind = "fisico", PhysicalId = "c-101" },
                new RiserEdge { From = "J2", To = "J3", Kind = "diagrama" },
                new RiserEdge { From = "J2", To = "D2", Kind = "fisico", PhysicalId = "c-102" },
                new RiserEdge { From = "P1", To = "F-7", Kind = "fisico", PhysicalId = "c-103" },
                new RiserEdge { From = "X9", To = "D2", Kind = "fisico", PhysicalId = "c-104" },
                new RiserEdge { From = "J3", To = "NOEXISTE" },
            };
            var r = RiserGraph.Analyze(nodes, edges, new[] { "F-7" }, new[] { ("J3", "D2") });
            Assert.Equal("revisar", r["veredicto"]);
            Assert.Single((List<object>)r["solo_diagrama"]);
            Assert.Contains("J3", (List<object>)r["extremos_abiertos"]);
            Assert.Equal(new List<object> { "X9" }, r["ids_repetidos_invalidos"]);
            Assert.Equal(new List<object> { "F-7" }, r["ids_repetidos_validos"]);
            Assert.Equal(new List<object> { "J3→D2" }, r["ramas_faltantes"]);
            Assert.Single((List<object>)r["aristas_sin_nodo"]);
            Assert.True((long)r["componentes_fisicos"] > (long)r["componentes_diagrama"]);
            var dib = (Dictionary<string, object>)r["dibujo"];
            Assert.True(dib.ContainsKey("cruces"));
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
                G("U3", "UNIT A", 0, 100, 0, true, unit),                        // espejada: misma firma
                G("U4", "UNIT A", 200, 0, 0, false, unit.Take(2).ToArray()),     // le falta la G
                G("U5", "UNIT B", 0, 300, 0, false, unit),
            };
            var r = GroupVariants.Analyze(groups);
            var a = (Dictionary<string, object>)((List<object>)r["tipos"])[0];
            Assert.Equal("UNIT A", a["tipo_grupo"]);
            Assert.Equal(2L, a["variantes"]);
            Assert.Equal(3L, ((Dictionary<string, object>)a["referencia"])["instancias"]);
            var dev = (Dictionary<string, object>)((List<object>)a["desviaciones"])[0];
            Assert.Equal(new List<object> { "U4" }, dev["instancias"]);
            Assert.Single((List<object>)dev["faltan"]);
            Assert.StartsWith("G|", (string)((List<object>)dev["faltan"])[0]);
            Assert.Equal(new List<object> { "U3" }, a["espejadas"]);
        }

        [Fact]
        public void Annotation_NoCollisions_WhenSpaceExists_LeaderWhenFar_LegibilityChecked()
        {
            var tags = Enumerable.Range(0, 12).Select(i => new TagRequest { Id = "t" + i, X = 1 + (i % 4) * 0.5, Y = 1 + (i / 4) * 0.4 }).ToList();
            var obstacles = tags.Select(t => new Box { Id = "sym-" + t.Id, X0 = t.X - 0.04, X1 = t.X + 0.04, Y0 = t.Y - 0.04, Y1 = t.Y + 0.04 }).ToList();
            var r = AnnotationLayout.Layout(tags, obstacles);
            Assert.Equal(0L, r["colisiones"]);
            Assert.True((bool)r["legible"]);
            Assert.False((bool)AnnotationLayout.Layout(tags, obstacles, textHeightIn: 1.0 / 16)["legible"]);
            // ancla rodeada: la etiqueta se aleja y lleva leader
            var crowd = new List<Box> { new Box { X0 = 4.0, X1 = 6.0, Y0 = 4.0, Y1 = 6.0 } };
            var one = AnnotationLayout.Layout(new List<TagRequest> { new TagRequest { Id = "x", X = 5, Y = 5 } }, crowd);
            var tag = (Dictionary<string, object>)((List<object>)one["etiquetas"])[0];
            Assert.True((bool)tag["colision"] || (bool)tag["leader"]);
        }
    }
}
