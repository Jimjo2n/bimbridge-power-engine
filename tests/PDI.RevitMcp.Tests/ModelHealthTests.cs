// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine.Qa;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.9.1 (Bloque H): reglas de salud del modelo sobre datos sintéticos.</summary>
    public class ModelHealthTests
    {
        private static ModelHealth.Element E(long id, double x, double y, string type = "A", double rot = 0, string level = "L1") =>
            new ModelHealth.Element { Id = id, Category = "Lighting Fixtures", Family = "F", Type = type, Level = level, X = x, Y = y, Z = 9, RotationDeg = rot };

        [Fact]
        public void Duplicates_SameTypeLevelPositionAndRotation_Only()
        {
            var els = new List<ModelHealth.Element>
            {
                E(1, 0, 0), E(2, 0.001, 0), E(3, 0, 0, "B"), E(4, 0, 0, rot: 90), E(5, 0, 0, level: "L2"), E(6, 10, 10), E(7, 10, 10.002), E(8, 10, 10.0039, rot: 360),
            };
            var g = ModelHealth.Duplicates(els);
            Assert.Equal(2, g.Count);
            Assert.Equal(new long[] { 1, 2 }, g[0]);
            Assert.Equal(new long[] { 6, 7 }, g[1].Take(2).ToArray());
            // 8 está a 3,9 mm de 6: fuera de la tolerancia de 3 mm respecto a 6 aunque cerca de 7.
            Assert.DoesNotContain(new[] { 3L, 4L, 5L }, id => g.Any(x => x.Contains(id)));
        }

        [Fact]
        public void Duplicates_DenseClusters_AreLinearish_AndNonFiniteOrHugeCoordsAreIgnored()
        {
            // Un grupo muy denso (20.000 elementos en el mismo punto) y 20.000 dispersos: debe terminar rápido y dar un solo grupo.
            var els = new List<ModelHealth.Element>();
            for (var i = 0; i < 20000; i++) els.Add(E(i + 1, 5, 5));
            for (var i = 0; i < 20000; i++) els.Add(E(100000 + i, 1000 + i * 3.0, 2000));
            els.Add(E(900001, double.NaN, 0)); els.Add(E(900002, double.PositiveInfinity, 0)); els.Add(E(900003, 1e15, 0));
            els.Add(new ModelHealth.Element { Id = 900004, Category = "Lighting Fixtures", Family = "F", Type = "A", Level = "L1", X = 0, Y = 0, Z = double.NaN });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = ModelHealth.Duplicates(els);
            sw.Stop();
            Assert.Single(g);
            Assert.Equal(20000, g[0].Count);
            Assert.DoesNotContain(g[0], id => id >= 900001);
            Assert.True(sw.ElapsedMilliseconds < 5000, "tardó " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void Report_GroupsWarnings_FlagsMissingParams_DisconnectedConnectors_AndDocumentation()
        {
            var warnings = new List<ModelHealth.Warning>
            {
                new ModelHealth.Warning { Description = "Highlighted elements are joined but do not intersect.", Severity = "Warning", Ids = new List<long> { 1, 2 } },
                new ModelHealth.Warning { Description = "Highlighted elements are joined but do not intersect.", Severity = "Warning", Ids = new List<long> { 3 } },
                new ModelHealth.Warning { Description = "Room is not in a properly enclosed region", Severity = "Warning", Ids = new List<long> { 9 } },
            };
            var a = E(1, 0, 0); a.Params["Circuit Number"] = "";
            var b = E(2, 5, 0); b.Params["Circuit Number"] = "12";
            var c = E(3, 9, 0);
            var conns = new List<ModelHealth.Connector>
            {
                new ModelHealth.Connector { ElementId = 20, Domain = "DomainHvac", Connected = false },
                new ModelHealth.Connector { ElementId = 20, Domain = "DomainHvac", Connected = true },
                new ModelHealth.Connector { ElementId = 21, Domain = "DomainPiping", Connected = false },
            };
            var sheets = new List<ModelHealth.Sheet> { new ModelHealth.Sheet { Id = 100, Number = "A1", Viewports = 0 }, new ModelHealth.Sheet { Id = 101, Number = "A2", Viewports = 2 } };
            var views = new List<ModelHealth.View>
            {
                new ModelHealth.View { Id = 200, Name = "L1", Kind = "FloorPlan", OnSheet = true }, new ModelHealth.View { Id = 201, Name = "L2", Kind = "FloorPlan" },
                new ModelHealth.View { Id = 202, Name = "T", Kind = "FloorPlan", IsTemplate = true }, new ModelHealth.View { Id = 203, Name = "3D", Kind = "ThreeD" },
            };
            var r = ModelHealth.Report(warnings, new[] { a, b, c }, conns, sheets, views, new[] { "Circuit Number" });
            var s = (Dictionary<string, object>)r["resumen"];
            Assert.Equal(3L, s["avisos"]); Assert.Equal(2L, s["tipos_de_aviso"]);
            Assert.Equal(2L, s["conectores_libres"]); Assert.Equal(1L, s["laminas_vacias"]); Assert.Equal(1L, s["vistas_sin_lamina"]);
            var first = (Dictionary<string, object>)((List<object>)r["avisos"])[0];
            Assert.Equal(2L, first["veces"]);
            Assert.Equal(new object[] { 1L, 2L, 3L }, ((List<object>)first["ids"]).ToArray());
            var p = (Dictionary<string, object>)((Dictionary<string, object>)r["parametros_requeridos"])["Circuit Number"];
            Assert.Equal(1L, ((Dictionary<string, object>)p["vacio"])["n"]);
            Assert.Equal(1L, ((Dictionary<string, object>)p["sin_parametro"])["n"]);
            var dc = (Dictionary<string, object>)r["conectores_desconectados"];
            Assert.Equal(2, dc.Count);
        }
    }
}
