// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BimBridge.PowerEngine;
using BimBridge.PowerEngine.Workflows;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Workflow;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Registro plano–modelo (sin PDF): casos de geometría.</summary>
    public class RegistrationTests
    {
        private static List<Anchor> Grid(double rotDeg, double ftPerPt, double tx, double ty)
        {
            var c = Math.Cos(rotDeg * Math.PI / 180); var s = Math.Sin(rotDeg * Math.PI / 180);
            var l = new List<Anchor>();
            foreach (var (n, mx, my) in new[] { ("A/1", 0.0, 0.0), ("F/1", 150.0, 0.0), ("A/5", 0.0, 100.0), ("F/5", 150.0, 100.0), ("C/3", 60.0, 50.0) })
            {
                // página = inversa de (modelo = R·s·página + t)
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
            Assert.True(r.Ok, r.Motivo);
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
            a[4].Mx += 30;   // C/3 emparejada con el eje equivocado
            var r = Registration.Solve(a);
            Assert.True(r.Ok, r.Motivo);
            Assert.Contains(r.Anchors, x => x.Name == "C/3" && x.Outlier && !x.Used);
            Assert.True(r.MaxFt < 1e-6);
            Assert.Contains(r.Warnings, w => w.Contains("C/3"));
        }

        [Fact]
        public void TooFew_Degenerate_ScaleMismatch_Residual()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0);
            Assert.Equal("anclas_insuficientes", Registration.Solve(a.Take(1).ToList()).Estado);
            Assert.Equal("degenerado", Registration.Solve(new List<Anchor> { new Anchor("x", 1, 1, 0, 0), new Anchor("y", 1, 1, 5, 5) }).Estado);
            Assert.Equal("degenerado", Registration.Solve(new List<Anchor> { a[0], a[1], new Anchor("m", (a[0].Px + a[1].Px) / 2, a[0].Py, 75, 0) }, new RegistrationOptions { Mode = "afin" }).Estado);
            Assert.Equal("escala_incoherente", Registration.Solve(a, new RegistrationOptions { ExpectedRevitScale = 48 }).Estado);
            Assert.Equal("ancla_invalida", Registration.Solve(new List<Anchor> { a[0], new Anchor("A/1", 0, 0, 1, 1) }).Estado);
            // con sólo 3 anclas, una mala no se puede aislar: el residuo queda a la vista
            var b = a.Take(3).ToList(); b[2] = new Anchor(b[2].Name, b[2].Px, b[2].Py, b[2].Mx + 10, b[2].My);
            Assert.Equal("residuo_excesivo", Registration.Solve(b).Estado);
        }

        [Fact]
        public void Affine_HandlesNonUniformScale_AndWarns()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0).Select(x => new Anchor(x.Name, x.Px, x.Py * 1.05, x.Mx, x.My)).ToList();
            var r = Registration.Solve(a, new RegistrationOptions { Mode = "afin" });
            Assert.True(r.Ok, r.Motivo);
            Assert.Contains(r.Warnings, w => w.Contains("escala distinta"));
            Assert.Equal("residuo_excesivo", Registration.Solve(a).Estado);   // la semejanza no puede explicar un plano deformado
        }

        [Fact]
        public void LinkTransform_IsComposed()
        {
            var a = Grid(0, Registration.FtPerPtAtScale(96), 0, 0);
            var link = new double[] { 0, -1, 0, 100, 1, 0, 0, 5, 0, 0, 1, 0 };   // vínculo girado 90° y desplazado
            var r = Registration.Solve(a, new RegistrationOptions { LinkTransform = link });
            var (x, y) = r.Map(a[1].Px, a[1].Py);   // F/1 del vínculo = (150, 0) → anfitrión (100, 155)
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
    /// Flujo completo SIMULADO (sin Revit): PDF sintético → extracción (Python) → registro → plan de colocación → "ejecución"
    /// simulada → verificación independiente. Nada de esto es evidencia nativa.
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
            new TypeRule { Code = "D", FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: D", Montaje = "losa", Workset = "LIGHTING - DIST" },
            new TypeRule { Code = "G", FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: G", Montaje = "losa", Workset = "LIGHTING-EMERGENCY - DIST" },
            new TypeRule { Code = "S", FamilyType = "LTG_STRIP LIGHT: S", ElevFt = 9.25, Workset = "LIGHTING - DIST", Directional = true },
        };

        public static IEnumerable<object[]> Plans() => new[] { "plan_vectorial.pdf", "plan_rotado.pdf", "plan_girado30.pdf" }.Select(n => new object[] { n });

        [Theory, MemberData(nameof(Plans))]
        public void Pdf_Register_Plan_Simulate_Verify(string name)
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                // A. interpretación
                var grids = pe.Document("pdf_grids", new Dictionary<string, object> { ["ruta"] = F(name) }, new[] { Fx });
                var syms = pe.Document("pdf_symbols", new Dictionary<string, object> { ["ruta"] = F(name) }, new[] { Fx });
                Assert.True(grids.Ok && syms.Ok, grids.Error + syms.Error);
                var pg = (Dictionary<string, object>)((List<object>)grids.Result["paginas"])[0];
                var page = ((List<object>)pg["intersecciones"]).Cast<Dictionary<string, object>>()
                    .ToDictionary(x => (string)x["nombre"], x => { var p = (List<object>)x["punto_pt"]; return (Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture)); });
                var sheet = (Dictionary<string, object>)((Dictionary<string, object>)((List<object>)syms.Result["paginas"])[0])["lamina"];
                var scale = Convert.ToDouble(((Dictionary<string, object>)sheet["escala"])["factor_revit"], CultureInfo.InvariantCulture);

                // B. registro (las anclas son intersecciones de ejes con el mismo nombre en el plano y en el modelo)
                var anchors = Registration.AnchorsByName(page, ModelGrid());
                Assert.Equal(30, anchors.Count);
                var reg = Registration.Solve(anchors, new RegistrationOptions { ExpectedRevitScale = scale, TolFt = 0.05 });
                Assert.True(reg.Ok, reg.Motivo);
                var truth = Truth(name);
                // página→modelo deshace el giro del modelo sobre la lámina y aplica el /Rotate de la página (visible = contenido girado −90°)
                var expRot = -Convert.ToDouble(truth["rotacion_modelo_deg"], CultureInfo.InvariantCulture) + (truth.ContainsKey("rotacion_pagina_deg") ? Convert.ToDouble(truth["rotacion_pagina_deg"], CultureInfo.InvariantCulture) : 0);
                Assert.True(PlacementVerifier.AngleDiff(reg.RotationDeg, expRot, false) < 0.01, "giro " + reg.RotationDeg + " frente a " + expRot);
                Assert.True(reg.RmsFt < 0.01);

                // C. plan
                var pdfSha = (string)syms.Result["sha256"];
                var plan = PlacementPlanner.Plan(PlacementPlanner.FromPdfSymbols(syms.Result), reg, Rules,
                    new PlacementOptions { Level = "LEVEL 09", SourceId = name, SourceSha256 = pdfSha, SourceRevision = "CCD7" }, null, pe);
                Assert.Equal("listo_con_ambiguedades", plan.Estado);
                Assert.Equal(72, plan.Place.Count);
                Assert.Equal(2, plan.Ambiguous.Count(a => a.Motivo.StartsWith("casi_duplicado", StringComparison.Ordinal)));
                Assert.All(plan.Batches, b => Assert.True(((List<object>)((Dictionary<string, object>)b["operacion"])["puntos"]).Count <= 500));
                Assert.Equal(72, plan.Batches.Sum(b => ((List<object>)b["items"]).Count));
                var lums = ((List<object>)truth["luminarias"]).Cast<Dictionary<string, object>>().ToList();
                foreach (var l in lums)
                {
                    var m = (List<object>)l["modelo_ft"];
                    double mx = Convert.ToDouble(m[0], CultureInfo.InvariantCulture), my = Convert.ToDouble(m[1], CultureInfo.InvariantCulture);
                    var best = plan.Place.OrderBy(p => (p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my)).First();
                    Assert.True(Math.Sqrt((best.X - mx) * (best.X - mx) + (best.Y - my) * (best.Y - my)) < 0.02, name + " " + l["codigo"] + " " + mx + "," + my);
                    Assert.Equal((string)l["codigo"], best.Code);
                }
                // las tiras siguen el giro del modelo (simétricas a 180°)
                Assert.All(plan.Place.Where(p => p.Code == "S"), p => Assert.True(PlacementVerifier.AngleDiff(p.RotDeg, -Convert.ToDouble(truth["rotacion_modelo_deg"], CultureInfo.InvariantCulture), true) < 0.5, "tira " + p.RotDeg));

                // D. ejecución SIMULADA: lo que "haría" place, con defectos sembrados
                long id = 1000;
                var observed = plan.Place.Select(p => new ObservedFixture { Id = id++, FamilyType = p.FamilyType, X = p.X, Y = p.Y, RotDeg = p.RotDeg, ElevFt = p.ElevFt ?? 10.0 }).ToList();
                var lost = observed[0]; observed.RemoveAt(0);                                           // omitido
                observed[0].X += 1.0;                                                                      // desplazado 1 ft
                observed[1].FamilyType = "LTG_STRIP LIGHT: L5";                                            // tipo incorrecto
                observed.Add(new ObservedFixture { Id = 9001, FamilyType = observed[2].FamilyType, X = observed[2].X + 0.01, Y = observed[2].Y, RotDeg = observed[2].RotDeg }); // duplicado
                observed.Add(new ObservedFixture { Id = 9002, FamilyType = "LTG_DOWNLIGHT-SURFACE - ROUND: D", X = 500, Y = 500 }); // adicional

                // E. verificación independiente: lo esperado sale de la VERDAD de prueba (otra fuente, otro sha), no del plan
                var expected = lums.Select((l, i) =>
                {
                    var m = (List<object>)l["modelo_ft"];
                    var code = (string)l["codigo"];
                    return new ExpectedFixture { Key = code + "-" + i, Code = code, FamilyType = Rules.First(r => r.Code == code).FamilyType, X = Convert.ToDouble(m[0], CultureInfo.InvariantCulture), Y = Convert.ToDouble(m[1], CultureInfo.InvariantCulture) };
                }).ToList();
                var truthSrc = new SourceRef { Id = "truth.json", Kind = SourceKinds.Tabla, Sha256 = Home.Sha256Hex(File.ReadAllText(F("truth.json"))) };
                var built = new List<SourceRef> { new SourceRef { Id = name, Kind = SourceKinds.Pdf, Sha256 = pdfSha } };
                var rep = PlacementVerifier.Verify(truthSrc, built, plan.Fingerprint, expected, observed, null, pe);
                Assert.Equal("diferencias", rep["estado"]);
                var counts = (Dictionary<string, object>)((Dictionary<string, object>)rep["resumen"])["diferencias"];
                Assert.Equal(1L, counts["omitido"]); Assert.Equal(1L, counts["desplazado"]); Assert.Equal(1L, counts["tipo_incorrecto"]);
                Assert.Equal(1L, counts["duplicado"]); Assert.Equal(1L, counts["adicional"]);
                Assert.False(counts.ContainsKey("giro_incorrecto"));

                // la verificación contra el propio plan o contra el mismo PDF NO es independiente
                var planAsExpected = plan.Place.Select(p => new ExpectedFixture { Key = p.Id, Code = p.Code, FamilyType = p.FamilyType, X = p.X, Y = p.Y }).ToList();
                Assert.Equal("no_independiente", PlacementVerifier.Verify(new SourceRef { Id = "plan", Kind = SourceKinds.Tabla, Sha256 = Home.Sha256Hex("otra") }, built, plan.Fingerprint, planAsExpected, observed)["estado"]);
                Assert.Equal("no_independiente", PlacementVerifier.Verify(new SourceRef { Id = "copia", Kind = SourceKinds.Pdf, Sha256 = pdfSha }, built, null, expected, observed)["estado"]);
            }
        }

        [Fact]
        public void Planner_EscalatesInsteadOfGuessing()
        {
            var reg = Registration.Solve(new List<Anchor> { new Anchor("a", 0, 0, 0, 0), new Anchor("b", 100, 0, 10, 0), new Anchor("c", 0, 100, 0, 10) });
            var syms = new List<SymbolCandidate>
            {
                new SymbolCandidate { Px = 10, Py = 10, Code = "D", Confidence = 0.95 },
                new SymbolCandidate { Px = 20, Py = 10, Code = "X", Confidence = 0.95 },     // sin regla
                new SymbolCandidate { Px = 30, Py = 10, Code = "D", Confidence = 0.5 },      // confianza baja
                new SymbolCandidate { Px = 40, Py = 10, Code = "N", Confidence = 0.95 },     // regla sin altura
                new SymbolCandidate { Px = 50, Py = 10, Code = "D", Confidence = 0.95 },     // ya existe (mismo tipo)
                new SymbolCandidate { Px = 60, Py = 10, Code = "D", Confidence = 0.95 },     // otro tipo existente al lado
            };
            var rules = new List<TypeRule> { new TypeRule { Code = "D", FamilyType = "F: D", ElevFt = 9 }, new TypeRule { Code = "N", FamilyType = "F: N" } };
            var ex = new List<ExistingElement> { new ExistingElement { Id = 7, X = 5, Y = 1, FamilyType = "F: D" }, new ExistingElement { Id = 8, X = 6, Y = 1, FamilyType = "F: Z" } };
            var plan = PlacementPlanner.Plan(syms, reg, rules, new PlacementOptions { Level = "L1", SourceSha256 = new string('a', 64) }, ex);
            Assert.Single(plan.Place);
            Assert.Single(plan.AlreadyThere);
            var why = plan.Ambiguous.Select(a => a.Motivo.Split(':')[0]).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "confianza_baja", "conflicto_existente", "sin_altura", "sin_tipo" }, why);
            Assert.Equal("sin_registro", PlacementPlanner.Plan(syms, new RegistrationResult { Estado = "residuo_excesivo" }, rules, new PlacementOptions { Level = "L1" }).Estado);
            // ids estables entre corridas
            Assert.Equal(plan.Place[0].Id, PlacementPlanner.Plan(syms, reg, rules, new PlacementOptions { Level = "L1", SourceSha256 = new string('a', 64) }, ex).Place[0].Id);
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
