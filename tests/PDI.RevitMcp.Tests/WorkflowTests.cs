// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine.Workflow;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0 paquete A: requisitos con procedencia, verificación independiente y estados de entrega (sin Revit).</summary>
    public class WorkflowTests
    {
        private static Requirement R(string id, string kind, string src, string key, string value, string scope = "*", string rev = null, params string[] supersedes)
            => new Requirement { Id = id, Source = new SourceRef { Id = src, Kind = kind, Revision = rev }, Key = key, Value = value, Scope = scope, Supersedes = supersedes.ToList() };

        [Fact]
        public void Requirements_RejectWithoutProvenance_AndInferenceCannotBeObserved()
        {
            var s = new RequirementSet();
            Assert.NotNull(s.Add(new Requirement { Id = "r1", Key = "altura" }));
            Assert.NotNull(s.Add(R("r2", SourceKinds.Inferencia, "agente", "altura", "10'")));   // inferencia + OBSERVED
            var inf = R("r3", SourceKinds.Inferencia, "agente", "altura", "10'"); inf.Confidence = Confidence.Inferred;
            Assert.Null(s.Add(inf));
        }

        [Fact]
        public void Requirements_LaterInstructionSupersedesOnlyItsTarget()
        {
            var s = new RequirementSet();
            Assert.Null(s.Add(R("a", SourceKinds.Plano, "E-1.3.02", "workset", "DIST", "nivel=L02")));
            Assert.Null(s.Add(R("b", SourceKinds.Plano, "E-1.3.02", "codigo", "D", "nivel=L02")));
            Assert.Null(s.Add(R("c", SourceKinds.Usuario, "operator-2026-10-13", "workset", "EMERGENCY", "nivel=L02", null, "a")));
            Assert.Equal("EMERGENCY", s.Resolve("nivel=L02", "workset").Winner.Value);
            Assert.Equal("D", s.Resolve("nivel=L02", "codigo").Winner.Value);   // intacto
        }

        [Fact]
        public void Requirements_AuthorityAndRevision_ThenConflictForHuman()
        {
            var s = new RequirementSet();
            Assert.Null(s.Add(R("t", SourceKinds.Transcripcion, "reunion-0930", "diametro", "3/4\"")));
            Assert.Null(s.Add(R("p", SourceKinds.Plano, "E-6.1", "diametro", "1\"")));
            Assert.Equal("1\"", s.Resolve("*", "diametro").Winner.Value);         // plano > transcripción
            Assert.Null(s.Add(R("p2", SourceKinds.Plano, "E-6.1", "diametro", "1-1/4\"", "*", "CCD8")));
            Assert.Null(s.Add(R("p1", SourceKinds.Plano, "E-6.1", "dummy", "x", "*", "CCD7")));
            // misma fuente: p (sin revisión) y p2 (CCD8) → gana la revisión posterior
            Assert.Equal("1-1/4\"", s.Resolve("*", "diametro").Winner.Value);
            Assert.Null(s.Add(R("x", SourceKinds.Pdf, "ARQ-A1.7", "diametro", "2\"")));
            var res = s.Resolve("*", "diametro");
            Assert.Null(res.Winner);
            Assert.NotNull(res.Conflict);
            Assert.Single(s.Audit().conflicts);
        }

        [Fact]
        public void Requirements_MissingValueIsReported_NeverInvented()
        {
            var s = new RequirementSet();
            Assert.Null(s.Add(R("h", SourceKinds.Plano, "E-1.3.01L", "altura_montaje", null, "luminaria=LT-03")));
            var res = s.Resolve("luminaria=LT-03", "altura_montaje");
            Assert.True(res.MissingValue);
            Assert.Single(s.Audit().missing);
        }

        internal static string Sha(string s) => PDI.RevitMcp.Engine.Home.Sha256Hex(s);
        private static IndependentVerifier.Item E(string key, string code = null) { var i = new IndependentVerifier.Item { Key = key }; if (code != null) i.Attrs["codigo"] = code; return i; }
        private static IndependentVerifier.Item O(string key, long id, string code = null) { var i = E(key, code); i.ElementId = id; return i; }

        [Fact]
        public void Verifier_RefusesSelfVerification_AgainstBuildInventory()
        {
            var r = IndependentVerifier.Verify(new IndependentVerifier.Input
            {
                ExpectedSource = new SourceRef { Id = "inventario-riser-v1", Kind = SourceKinds.Tabla, Sha256 = Sha("inv") }, BuildSourceId = "inventario-riser-v1",
                Expected = { E("F1") }, Observed = { O("F1", 1) },
            });
            Assert.Equal("no_independiente", r.Estado);
        }

        [Fact]
        public void Verifier_SeededOmissions_ExtrasDuplicatesMismatches_AndJustifiedExceptions()
        {
            var input = new IndependentVerifier.Input
            {
                ExpectedSource = new SourceRef { Id = "feeder-schedule", Kind = SourceKinds.Tabla, Revision = "R3", Sha256 = Sha("fs") }, BuildSourceId = "markup-riser",
                RequiredRevision = "R3", CompareAttrs = new[] { "codigo" },
                Expected = { E("F1", "4#500"), E("F2", "4#4/0"), E("F3", "4#2"), E("F9", "x") },
                Observed = { O("F1", 10, "4#500"), O("F2", 11, "4#1/0"), O("F2", 12, "4#4/0"), O("F7", 13) },
                Exceptions = { ["F9"] = "extremo abierto respaldado por la fuente (hoja 2, fila 14)" },
            };
            var r = IndependentVerifier.Verify(input);
            Assert.Equal("diferencias", r.Estado);
            Assert.Contains("F3", r.Missing);
            Assert.Contains(13L, r.Extra);
            Assert.Single(r.Duplicates);
            Assert.Single(r.Mismatches);                 // F2 #11 con código distinto
            Assert.Single(r.JustifiedExceptions);
            Assert.DoesNotContain("F9", r.Missing);
        }

        [Fact]
        public void Verifier_AllowedDuplicate_IsKept_AndPositionMatchingUsesTolerance()
        {
            var input = new IndependentVerifier.Input
            {
                ExpectedSource = new SourceRef { Id = "E-1.3.03", Kind = SourceKinds.Plano, Sha256 = Sha("e103") }, BuildSourceId = "plan-colocacion",
                Expected = { new IndependentVerifier.Item { X = 10, Y = 10 }, new IndependentVerifier.Item { X = 20, Y = 10 } },
                Observed = { new IndependentVerifier.Item { ElementId = 1, X = 10.02, Y = 10 }, new IndependentVerifier.Item { ElementId = 2, X = 21, Y = 10 } },
            };
            var r = IndependentVerifier.Verify(input);
            Assert.Equal(1, r.Matched);
            Assert.Single(r.Missing);
            Assert.Contains(2L, r.Extra);
            var dup = new IndependentVerifier.Input
            {
                ExpectedSource = new SourceRef { Id = "s", Kind = SourceKinds.Tabla, Sha256 = Sha("s") }, BuildSourceId = "markup", Expected = { E("FD-1") },
                Observed = { O("FD-1", 1), O("FD-1", 2) }, AllowedDuplicates = { "FD-1" },
            };
            Assert.True(IndependentVerifier.Verify(dup).Ok);
        }

        [Fact]
        public void Verifier_WrongRevision_IsRejected()
        {
            var r = IndependentVerifier.Verify(new IndependentVerifier.Input { ExpectedSource = new SourceRef { Id = "s", Kind = SourceKinds.Pdf, Revision = "CCD5", Sha256 = Sha("s") }, BuildSourceId = "plan", RequiredRevision = "CCD7" });
            Assert.Equal("revision_incorrecta", r.Estado);
        }

        private static DeliveryReceipt Rc(string stage, string doc, string rev, params (string, object)[] data)
        {
            var r = new DeliveryReceipt { Stage = stage, DocKey = doc, Revision = rev };
            // 0.9.0: procedencia por defecto de un recibo observado por BIMBRIDGE
            r.Data["origen"] = "bimbridge:prueba"; r.Data["evidencia"] = Confidence.Comprobado;
            foreach (var (k, v) in data) r.Data[k] = v;
            return r;
        }

        [Fact]
        public void Delivery_CannotSkip_CannotUseOtherDocument_NorStalePdf()
        {
            var t = new DeliveryTrack("set-riser", "doc-A", new[] { DeliveryStages.CommitConfirmado, DeliveryStages.Sincronizado, DeliveryStages.SetPersistido, DeliveryStages.Exportado, DeliveryStages.ArtefactoVerificado });
            Assert.NotNull(t.Record(Rc(DeliveryStages.Exportado, "doc-A", "7", ("archivo", "x.pdf"), ("sha256", "aa"))));          // salto
            Assert.Null(t.Record(Rc(DeliveryStages.CommitConfirmado, "doc-A", "7", ("plan_id", "P1"), ("rev", 7L))));
            Assert.NotNull(t.Record(Rc(DeliveryStages.Sincronizado, "doc-B", "7", ("central", "c"), ("huella_central", "h"))));   // otro documento
            Assert.Null(t.Record(Rc(DeliveryStages.Sincronizado, "doc-A", "7", ("central", "c"), ("huella_central", "h"))));
            Assert.Null(t.Record(Rc(DeliveryStages.SetPersistido, "doc-A", "7", ("set", "NON-UNIT LIGHTING"), ("laminas", 12L))));
            Assert.NotNull(t.Record(Rc(DeliveryStages.Exportado, "doc-A", "5", ("archivo", "viejo.pdf"), ("sha256", "bb"))));     // revisión anterior
            Assert.Null(t.Record(Rc(DeliveryStages.Exportado, "doc-A", "7", ("archivo", "x.pdf"), ("sha256", "aa"))));
            Assert.NotNull(t.Record(Rc(DeliveryStages.ArtefactoVerificado, "doc-A", "7", ("archivo", "x.pdf"), ("sha256", "cc"), ("paginas", 12L))));  // otro archivo
            Assert.NotNull(t.Record(Rc(DeliveryStages.ArtefactoVerificado, "doc-A", "7", ("archivo", "x.pdf"), ("sha256", "aa"), ("paginas", 11L))));  // páginas
            Assert.Null(t.Record(Rc(DeliveryStages.ArtefactoVerificado, "doc-A", "7", ("archivo", "x.pdf"), ("sha256", "aa"), ("paginas", 12L))));
            Assert.True(t.Complete);
        }
    }
}
