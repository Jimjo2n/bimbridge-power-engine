// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine.Qc;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Aceptación D (§7) sin Revit: fixture de práctica con grupo de unidad normal y espejado, variante con dos paneles, link
    /// transformado (giro + traslación), luminaria casi duplicada, desviaciones conocidas y ocultación por filtro/rango. El QC debe
    /// encontrar CADA desviación (no sólo totales) y aprobar el modelo corregido dentro de las tolerancias declaradas.
    /// Lo que depende de la API (juntar posiciones, grupos, etiquetas, filtros) se prueba en Revit con el arnés nativo.
    /// </summary>
    public class QcFixtureTests
    {
        private sealed class Member { public string Name, Fam, Type; public double X, Y, Z, Rot; public bool? Tag; }

        // Tipo de grupo "UNIT-A": coordenadas relativas al grupo (mm).
        private static readonly Member[] UnitA =
        {
            new Member { Name = "L1", Fam = "LTG", Type = "D", X = 1000, Y = 1000, Z = 2700, Rot = 0, Tag = true },
            new Member { Name = "L2", Fam = "LTG", Type = "D", X = 3000, Y = 1000, Z = 2700, Rot = 90, Tag = true },
            new Member { Name = "P1", Fam = "PANEL", Type = "P", X = 500, Y = 4000, Z = 1500, Rot = 0 },
            new Member { Name = "P2", Fam = "PANEL", Type = "P", X = 900, Y = 4000, Z = 1500, Rot = 0 }, // dos paneles: no se asume uno por unidad
        };

        // ARCH vinculado: girado 30° y desplazado; las unidades están en coordenadas del vínculo.
        private static readonly Xform Link = Xform.RotZ(30, 10000, 5000, 0);
        private static readonly Dictionary<string, Xform> Units = new Dictionary<string, Xform>
        {
            ["U101"] = Xform.Identity,
            ["U102"] = Xform.MirrorX(6000), // unidad espejada
        };

        private static (List<ManifestQc.Expected> exp, List<ManifestQc.Actual> model) Build()
        {
            var exp = new List<ManifestQc.Expected>();
            var model = new List<ManifestQc.Actual>();
            long id = 1000;
            foreach (var u in Units)
            {
                var toHost = u.Value.Then(Link);
                foreach (var m in UnitA)
                {
                    var p = toHost.Apply(m.X, m.Y, m.Z);
                    var rot = (Xform.RotZ(m.Rot).Then(toHost)).RotationDeg;
                    exp.Add(new ManifestQc.Expected
                    {
                        Ref = u.Key + "/" + m.Name, X = p[0], Y = p[1], Z = p[2], RotDeg = rot, Level = "L05",
                        Types = new List<string> { m.Fam + ": " + m.Type }, Tag = m.Tag, Group = u.Key, Source = "E-1.3.05 p.1",
                    });
                    model.Add(new ManifestQc.Actual
                    {
                        Id = id++, X = p[0], Y = p[1], Z = p[2], RotDeg = rot, Level = "L05", Family = m.Fam, Type = m.Type,
                        Tags = m.Tag == true ? 1 : 0, Group = u.Key,
                    });
                }
            }
            // Pasillo: fuera de grupos.
            exp.Add(new ManifestQc.Expected { Ref = "C1", X = 20000, Y = 0, Z = 2900, Level = "L05", Types = new List<string> { "LTG: G" }, Tag = true });
            model.Add(new ManifestQc.Actual { Id = id++, X = 20000, Y = 0, Z = 2900, Level = "L05", Family = "LTG", Type = "G", Tags = 1 });
            return (exp, model);
        }

        private static ManifestQc.Options Opt => new ManifestQc.Options { TolXY = 150, TolZ = 25, TolRot = 2, AmbiguityMargin = 50, NearDuplicate = 100 };

        private static ManifestQc.Actual At(List<ManifestQc.Expected> exp, List<ManifestQc.Actual> model, string r)
        {
            var e = exp.First(x => x.Ref == r);
            return model.First(a => Math.Abs(a.X - e.X) < 1e-6 && Math.Abs(a.Y - e.Y) < 1e-6 && a.Level == e.Level && (a.Group ?? "") == (e.Group ?? ""));
        }

        [Fact]
        public void LinkAndMirroredGroup_Transforms_AreComposedCorrectly()
        {
            var u102 = Units["U102"].Then(Link);
            Assert.True(u102.Mirrored);
            Assert.False(Units["U101"].Then(Link).Mirrored);
            // Ida y vuelta: anfitrión → vínculo → anfitrión
            var p = u102.Apply(1000, 1000, 2700);
            var back = u102.Inverse().Apply(p[0], p[1], p[2]);
            Assert.Equal(1000, back[0], 6);
            Assert.Equal(1000, back[1], 6);
            Assert.Equal(2700, back[2], 6);
            // En el grupo espejado, L1 queda simétrica respecto de x=6000 antes del giro del vínculo
            var inLink = Units["U102"].Apply(1000, 1000, 0);
            Assert.Equal(11000, inLink[0], 6);
        }

        [Fact]
        public void CorrectModel_IsApproved_OneToOne()
        {
            var (exp, model) = Build();
            var r = ManifestQc.Run(exp, model, Opt);
            Assert.True(r.Passed, string.Join("\n", r.Findings.Select(f => f.Code + " " + f.Ref + " " + f.Detail)));
            Assert.Equal(exp.Count, r.Approved);
            Assert.Equal(exp.Count, r.Pairs.Values.Distinct().Count());
        }

        [Fact]
        public void KnownDeviations_AreEachIdentified()
        {
            var (exp, model) = Build();
            // 1) variante sin segundo panel en la unidad espejada
            model.Remove(At(exp, model, "U102/P2"));
            // 2) luminaria sobrante lejos
            model.Add(new ManifestQc.Actual { Id = 9001, X = 40000, Y = 40000, Z = 2700, Level = "L05", Family = "LTG", Type = "D", Tags = 1 });
            // 3) casi duplicado de U101/L1 a 20 mm
            var l1 = At(exp, model, "U101/L1");
            model.Add(new ManifestQc.Actual { Id = 9002, X = l1.X + 20, Y = l1.Y, Z = l1.Z, RotDeg = l1.RotDeg, Level = "L05", Family = "LTG", Type = "D", Tags = 1, Group = "U101" });
            // 4) altura fuera de tolerancia
            At(exp, model, "U101/L2").Z += 100;
            // 5) tipo equivocado
            At(exp, model, "U102/L1").Type = "G";
            // 6) etiquetas: una sin etiqueta y otra con dos
            At(exp, model, "C1").Tags = 0;
            At(exp, model, "U102/L2").Tags = 2;
            // 7) miembro en el grupo equivocado
            At(exp, model, "U101/P1").Group = "U102";
            // 8) orientación (giro de 90° de más) en la unidad espejada
            var p1 = At(exp, model, "U102/P1");
            p1.RotDeg = (p1.RotDeg + 90) % 360;

            var r = ManifestQc.Run(exp, model, Opt);
            Assert.False(r.Passed);
            bool Has(string code, string reference = null, long? id = null) =>
                r.Findings.Any(f => f.Code == code && (reference == null || f.Ref == reference) && (id == null || f.Id == id || (f.Detail ?? "").Contains("#" + id)));
            Assert.True(Has("faltante", "U102/P2"));
            Assert.True(Has("sobrante", id: 9001));
            Assert.True(Has("casi_duplicado", id: 9002));
            Assert.True(Has("ambiguo", "U101/L1")); // dos candidatos a 0 y 20 mm: la cercanía no prueba cuál es
            Assert.True(Has("altura", "U101/L2"));
            Assert.True(Has("tipo", "U102/L1"));
            Assert.True(Has("sin_etiqueta", "C1"));
            Assert.True(Has("etiqueta_duplicada", "U102/L2"));
            Assert.True(Has("grupo", "U101/P1"));
            Assert.True(Has("orientacion", "U102/P1"));
            // Nada de lo desviado cuenta como correcto
            Assert.Equal(exp.Count - 1 - 1 - 6, r.Approved); // −faltante −ambiguo −6 con desviación (sólo U101/P2 queda bien)
            Assert.Equal(6, r.WithDeviation);
            Assert.Equal("U101/P2", r.Pairs.Keys.Single(k => !r.Findings.Any(f => f.Ref == k)));
        }

        [Fact]
        public void SameTotals_DoNotHideAMissingAndAnExtra()
        {
            var (exp, model) = Build();
            model.Remove(At(exp, model, "C1"));
            model.Add(new ManifestQc.Actual { Id = 9100, X = -30000, Y = 0, Z = 2900, Level = "L05", Family = "LTG", Type = "G", Tags = 1 });
            Assert.Equal(exp.Count, model.Count); // mismos totales
            var r = ManifestQc.Run(exp, model, Opt);
            Assert.False(r.Passed);
            Assert.Equal(1, r.Missing);
            Assert.Equal(1, r.Extra);
        }

        [Fact]
        public void EquidistantCandidates_AreAmbiguous_NotApproved()
        {
            var exp = new List<ManifestQc.Expected> { new ManifestQc.Expected { Ref = "X", X = 0, Y = 0, Level = "L1" } };
            var model = new List<ManifestQc.Actual>
            {
                new ManifestQc.Actual { Id = 1, X = 30, Y = 0, Level = "L1", Family = "F", Type = "T" },
                new ManifestQc.Actual { Id = 2, X = -30, Y = 0, Level = "L1", Family = "F", Type = "U" },
            };
            var r = ManifestQc.Run(exp, model, Opt);
            Assert.Equal(1, r.Ambiguous);
            Assert.Equal(0, r.Approved);
            Assert.False(r.Passed);
        }

        [Fact]
        public void NeighbourWithItsOwnBetterMatch_IsNotAmbiguous()
        {
            var exp = new List<ManifestQc.Expected>
            {
                new ManifestQc.Expected { Ref = "A", X = 0, Y = 0, Level = "L1" },
                new ManifestQc.Expected { Ref = "B", X = 110, Y = 0, Level = "L1" },
            };
            var model = new List<ManifestQc.Actual>
            {
                new ManifestQc.Actual { Id = 1, X = 40, Y = 0, Level = "L1", Family = "F", Type = "T" },
                new ManifestQc.Actual { Id = 2, X = 115, Y = 0, Level = "L1", Family = "F", Type = "T" },
            };
            // #1 está a 40 mm de A y a 70 mm de B (diferencia < margen), pero B tiene a #2 a 5 mm: no hay duda.
            var o = Opt;
            o.NearDuplicate = 50;
            var r = ManifestQc.Run(exp, model, o);
            Assert.True(r.Passed, string.Join("; ", r.Findings.Select(f => f.Code + " " + f.Detail)));
        }

        [Fact]
        public void UnconfirmedSourceFields_AreReportedSeparately()
        {
            var json = Json.ParseObject("{\"tolerancias\":{\"xy\":100,\"z\":20},\"elementos\":[{\"ref\":\"R1\",\"x\":0,\"y\":0,\"nivel\":\"L1\",\"tipo\":\"F: T\",\"no_confirmado\":[\"altura\"],\"fuente\":\"E-1 p2 r3 sha:ab12\"}]}");
            var exp = ManifestQc.Parse(json, out var o);
            Assert.Equal(100, o.TolXY);
            var r = ManifestQc.Run(exp, new List<ManifestQc.Actual> { new ManifestQc.Actual { Id = 5, X = 0, Y = 0, Level = "L1", Family = "F", Type = "T" } }, o);
            Assert.Equal(1, r.Approved);
            Assert.Equal(1, r.Unconfirmed);
            Assert.Contains(r.Findings, f => f.Code == "no_confirmado" && f.Detail.Contains("E-1 p2 r3"));
            Assert.Throws<ArgumentException>(() => ManifestQc.Parse(Json.ParseObject("{\"elementos\":[{\"ref\":\"a\",\"x\":0,\"y\":0},{\"ref\":\"a\",\"x\":1,\"y\":1}]}"), out _));
        }

        [Theory]
        [InlineData(10, 190, 180, 0)]
        [InlineData(0, 90, 180, 90)]
        [InlineData(359, 1, 0, 2)]
        [InlineData(0, 270, 90, 0)]
        public void RotationDifference_RespectsSymbolSymmetry(double a, double b, double sym, double expected) =>
            Assert.Equal(expected, ManifestQc.RotDiff(a, b, sym), 6);

        // ---------- visibilidad ----------

        [Fact]
        public void Visibility_FilterAndRcpRange_AreExplained_WithTemplateSource()
        {
            var f = new VisibilityQc.Facts
            {
                HidingFilters = new List<string> { "COLOR - LIGHTING-EMERGENCY - DIST - FIXTURES ONLY" }, Template = "10-PRECONSTRUCTION",
                PlanKind = "rcp", Cut = 5000, Top = 9000, ZMin = 2600, ZMax = 2700, RangeSource = "región de plano 17861903",
            };
            var reasons = VisibilityQc.Explain(f, out var notCheckable);
            Assert.Contains(reasons, x => x.Code == "filtro" && x.Detail.Contains("10-PRECONSTRUCTION"));
            Assert.Contains(reasons, x => x.Code == "rango_de_vista" && x.Detail.Contains("región de plano") && x.Certain);
            Assert.Contains(notCheckable, s => s.Contains("visibilidad propia de la familia"));
        }

        [Fact]
        public void Visibility_VisibleFixture_HasNoReasons()
        {
            var f = new VisibilityQc.Facts { PlanKind = "rcp", Cut = 1500, Top = 4000, ZMin = 2600, ZMax = 2700 };
            Assert.Empty(VisibilityQc.Explain(f, out _));
        }
    }
}
