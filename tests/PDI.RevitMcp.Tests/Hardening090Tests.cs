// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PDI.RevitMcp.AgentHost.Bench;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Workflow;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.9.0 — correcciones P0/P1 de 0.8.0 (sin Revit).</summary>
    public class Hardening090Tests
    {
        // ---------- P1.6: Retener no se apaga solo cuando no se pudo guardar ----------
        [Fact]
        public void FlagFile_UnsavedWrite_KeepsInMemoryValue_AfterRefresh()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "bb-ff-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(tmp, "soy un archivo, no una carpeta");
            try
            {
                var f = new FlagFile(Path.Combine(tmp, "home"), "hold.flag");  // la carpeta no se puede crear
                f.Set(true);
                Assert.True(f.Unsaved);
                Thread.Sleep(1100);   // pasa el intervalo de relectura del disco
                Assert.True(f.On, "el valor en memoria no puede caer por releer un disco donde nunca se escribió");
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void FlagFile_MissingHome_IsCreated_AndPersists()
        {
            var home = Path.Combine(Path.GetTempPath(), "bb-ff-" + Guid.NewGuid().ToString("N").Substring(0, 8), "no", "existe");
            try
            {
                var f = new FlagFile(home, "hold.flag");
                f.Set(true);
                Assert.False(f.Unsaved);
                Assert.True(new FlagFile(home, "hold.flag").On);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(home)), true); } catch (IOException) { } }
        }

        // ---------- P0.2: perfiles ----------
        private static readonly string LabDir = Path.Combine(Path.GetTempPath(), "bb-lab");

        [Fact]
        public void Profile_UnknownName_IsProduction()
        {
            Assert.False(ProfilePolicy.Parse("cualquiera", null, true, null).IsLab);
            Assert.False(ProfilePolicy.Parse(null, null, true, null).IsLab);
            Assert.True(ProfilePolicy.Parse("experimental", new[] { LabDir }, false, null).IsLab);
        }

        [Fact]
        public void Profile_Production_RejectsExperimental_AllowsAvailable()
        {
            var p = new ProfilePolicy();
            Assert.NotNull(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.Experimental));
            Assert.Null(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.Available));
            Assert.Null(p.DenyRequest("geo", Permissions.Read, "archivo:c:\\x.rvt", CapabilityStates.NativeTestPending));
        }

        [Fact]
        public void Profile_Lab_OnlyLabFolders_NoCloudNoUncNoTraversal()
        {
            var lab = ProfilePolicy.Parse("laboratorio", new[] { LabDir }, false, null);
            Assert.Null(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "copia.rvt"), CapabilityStates.Experimental));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(Path.GetTempPath(), "otro", "x.rvt"), CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "..", "fuera.rvt"), CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "nube:abc/def", CapabilityStates.Available));
            Assert.NotNull(lab.DenyRequest("query", Permissions.Read, "archivo:\\\\servidor\\share\\x.rvt", CapabilityStates.Available));
            // lo que no toca documento: contexto, catálogo, sin documento
            Assert.Null(lab.DenyRequest(Wire.OpContext, Permissions.Read, "nube:abc/def", CapabilityStates.Available));
            Assert.Null(lab.DenyRequest("query", Permissions.Read, null, CapabilityStates.Available));
            // laboratorio sin carpetas = nada
            Assert.NotNull(ProfilePolicy.Parse("laboratorio", null, false, null).DenyRequest("query", Permissions.Read, "archivo:" + Path.Combine(LabDir, "c.rvt"), CapabilityStates.Available));
        }

        [Fact]
        public void Profile_DynamicCode_ProductionNeedsShaAndScope()
        {
            const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string doc = "archivo:c:\\obra\\a.rvt";
            Assert.NotNull(new ProfilePolicy().DenyDynamicCode(sha, doc));
            Assert.Null(ProfilePolicy.Parse("produccion", null, false, new[] { sha }).DenyDynamicCode(sha, doc));
            Assert.Null(ProfilePolicy.Parse("produccion", null, false, new[] { sha + "@" + doc }).DenyDynamicCode(sha, doc));
            Assert.NotNull(ProfilePolicy.Parse("produccion", null, false, new[] { sha + "@archivo:c:\\obra\\b.rvt" }).DenyDynamicCode(sha, doc));
            Assert.NotNull(ProfilePolicy.Parse("produccion", null, false, new[] { sha }).DenyDynamicCode(sha.Replace('a', 'b'), doc));
            var lab = ProfilePolicy.Parse("laboratorio", new[] { LabDir }, false, null);
            Assert.Null(lab.DenyDynamicCode(sha, "archivo:" + Path.Combine(LabDir, "c.rvt")));
            Assert.NotNull(lab.DenyDynamicCode(sha, doc));
        }

        [Fact]
        public void EngineConfig_ParsesProfile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bb-cfg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var f = Path.Combine(dir, "config.json");
                File.WriteAllText(f, "{\"perfil\":\"laboratorio\",\"laboratorio\":{\"carpetas\":[\"" + LabDir.Replace("\\", "\\\\") + "\"],\"actualizacion_sin_firma\":true}}");
                var c = EngineConfig.Load(f);
                Assert.True(c.Profile.IsLab);
                Assert.True(c.Profile.LabUnsignedUpdates);
                Assert.Null(c.Profile.DenyUnsignedUpdate());
                File.WriteAllText(f, "{}");
                Assert.False(EngineConfig.Load(f).Profile.IsLab);
                Assert.NotNull(EngineConfig.Load(f).Profile.DenyUnsignedUpdate());
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---------- P0.3: independencia demostrable ----------
        private static string Sha(char c) => new string(c, 64);

        private static IndependentVerifier.Input In(SourceRef exp, params SourceRef[] builds) => new IndependentVerifier.Input
        {
            ExpectedSource = exp, BuildSources = builds.ToList(),
            Expected = { new IndependentVerifier.Item { Key = "D-1" } }, Observed = { new IndependentVerifier.Item { Key = "D-1", ElementId = 1 } },
        };

        [Fact]
        public void Independence_Cases()
        {
            var pdf = new SourceRef { Id = "E-1.3.02@CCD7", Kind = SourceKinds.Pdf, Revision = "CCD7", Sha256 = Sha('a') };
            var markup = new SourceRef { Id = "markup-1", Kind = SourceKinds.Pdf, Sha256 = Sha('b') };
            // independiente
            Assert.Null(IndependentVerifier.CheckIndependence(In(pdf, markup), out var proof));
            Assert.Equal("sha256+linaje", proof["metodo"]);
            // sin sha
            Assert.Equal("independencia_no_comprobable", IndependentVerifier.CheckIndependence(In(new SourceRef { Id = "x", Kind = SourceKinds.Pdf }, markup), out _)?.Estado);
            // sin construcción declarada
            Assert.Equal("independencia_no_comprobable", IndependentVerifier.CheckIndependence(In(pdf), out _)?.Estado);
            // mismo contenido con otro nombre
            Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(In(pdf, new SourceRef { Id = "renombrado", Sha256 = Sha('a') }), out _)?.Estado);
            // la construcción se derivó de la fuente esperada
            Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(In(pdf, new SourceRef { Id = "plan", Sha256 = Sha('c'), DerivedFrom = { "sha256:" + Sha('a') } }), out _)?.Estado);
            // la esperada se derivó de la construcción
            var derived = new SourceRef { Id = "tabla", Kind = SourceKinds.Pdf, Sha256 = Sha('d'), DerivedFrom = { "markup-1" } };
            Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(In(derived, markup), out _)?.Estado);
            // origen común
            var a = new SourceRef { Id = "a", Kind = SourceKinds.Pdf, Sha256 = Sha('e'), DerivedFrom = { "raiz" } };
            Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(In(a, new SourceRef { Id = "b", Sha256 = Sha('f'), DerivedFrom = { "raiz" } }), out _)?.Estado);
            // tipos que nunca son independientes
            foreach (var k in new[] { SourceKinds.Inferencia, SourceKinds.Modelo, SourceKinds.PlanEjecucion })
                Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(In(new SourceRef { Id = "z", Kind = k, Sha256 = Sha('9') }, markup), out _)?.Estado);
        }

        [Fact]
        public void Independence_ExpectedEqualsPlan_IsACopy()
        {
            var pdf = new SourceRef { Id = "E", Kind = SourceKinds.Pdf, Sha256 = Sha('a') };
            var inp = In(pdf, new SourceRef { Id = "plan", Sha256 = Sha('b') });
            inp.BuildPlanFingerprint = IndependentVerifier.Fingerprint(inp.Expected);
            Assert.Equal("no_independiente", IndependentVerifier.CheckIndependence(inp, out _)?.Estado);
            var r = IndependentVerifier.Verify(inp);
            Assert.Equal("no_independiente", r.Estado);
            // huella independiente del orden
            var x = new[] { new IndependentVerifier.Item { Key = "a", X = 1 }, new IndependentVerifier.Item { Key = "b", Y = 2 } };
            Assert.Equal(IndependentVerifier.Fingerprint(x), IndependentVerifier.Fingerprint(x.Reverse()));
        }

        // ---------- P0.4: recibos críticos con revisión y procedencia ----------
        private static DeliveryReceipt R(string stage, string rev, params (string, object)[] data)
        {
            var r = new DeliveryReceipt { Stage = stage, DocKey = "archivo:c:\\a.rvt", Revision = rev };
            foreach (var (k, v) in data) r.Data[k] = v;
            return r;
        }

        [Fact]
        public void Delivery_CriticalReceipts_NeedRevisionOriginAndObservedEvidence()
        {
            var t = new DeliveryTrack("e1", "archivo:c:\\a.rvt", new[] { DeliveryStages.Exportado, DeliveryStages.ArtefactoVerificado, DeliveryStages.EntregaConfirmada });
            Assert.Contains("sin revisión", t.Record(R(DeliveryStages.Exportado, null, ("archivo", "a.pdf"), ("sha256", Sha('a')), ("origen", "export"), ("evidencia", Confidence.Comprobado))));
            Assert.Contains("procedencia", t.Record(R(DeliveryStages.Exportado, "r5", ("archivo", "a.pdf"), ("sha256", Sha('a')), ("evidencia", Confidence.Comprobado))));
            Assert.Contains("COMPROBADO", t.Record(R(DeliveryStages.Exportado, "r5", ("archivo", "a.pdf"), ("sha256", Sha('a')), ("origen", "export"), ("evidencia", "REPORTADO"))));
            Assert.Null(t.Record(R(DeliveryStages.Exportado, "r5", ("archivo", "a.pdf"), ("sha256", Sha('a')), ("origen", "export"), ("evidencia", Confidence.Comprobado))));
            // verificar otra revisión que la exportada
            Assert.Contains("se exportó la r5", t.Record(R(DeliveryStages.ArtefactoVerificado, "r6", ("archivo", "a.pdf"), ("sha256", Sha('a')), ("paginas", 3), ("origen", "pdf_info"), ("evidencia", Confidence.Comprobado))));
            Assert.Null(t.Record(R(DeliveryStages.ArtefactoVerificado, "r5", ("archivo", "a.pdf"), ("sha256", Sha('a')), ("paginas", 3), ("origen", "pdf_info"), ("evidencia", Confidence.Comprobado))));
            // la entrega la confirma una persona, no el agente
            Assert.Contains("persona", t.Record(R(DeliveryStages.EntregaConfirmada, "r5", ("confirma", "ok"), ("origen", "agente"))));
            Assert.Null(t.Record(R(DeliveryStages.EntregaConfirmada, "r5", ("confirma", "ok"), ("origen", "usuario:operator"))));
            Assert.True(t.Complete);
        }

        // ---------- P1.5: geometría 2D degenerada ----------
        [Fact]
        public void Draw2D_RejectsCollinearOverlap_NonFinite_Huge()
        {
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 5.0, 0 } }, false, out var e1);
            Assert.Contains("colineales", e1 ?? "");
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { double.NaN, 1 } }, false, out var e2);
            Assert.NotNull(e2);
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 1e9, 1 } }, false, out var e3);
            Assert.NotNull(e3);
            Draw2D.Segments(new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 10.0, 5 } }, false, out var ok);
            Assert.Null(ok);
        }

        [Fact]
        public void Draw2D_Classify_CrossTouchOverlap()
        {
            Draw2D.P P(double x, double y) => new Draw2D.P(x, y);
            Assert.Equal(Draw2D.Contact.Cross, Draw2D.Classify(P(0, 0), P(2, 2), P(0, 2), P(2, 0)));
            Assert.Equal(Draw2D.Contact.Touch, Draw2D.Classify(P(0, 0), P(2, 0), P(1, 0), P(1, 5)));
            Assert.Equal(Draw2D.Contact.Overlap, Draw2D.Classify(P(0, 0), P(4, 0), P(2, 0), P(6, 0)));
            Assert.Equal(Draw2D.Contact.None, Draw2D.Classify(P(0, 0), P(4, 0), P(5, 0), P(6, 0)));
            Assert.Equal(Draw2D.Contact.None, Draw2D.Classify(P(0, 0), P(4, 0), P(0, 1), P(4, 1)));
            // bucle en ocho (no adyacentes se tocan) → inválido
            Assert.NotNull(Draw2D.ValidateLoop(new List<double[]> { new[] { 0.0, 0 }, new[] { 2.0, 2 }, new[] { 2.0, 0 }, new[] { 0.0, 2 } }));
            Assert.Null(Draw2D.ValidateLoop(new List<double[]> { new[] { 0.0, 0 }, new[] { 2.0, 0 }, new[] { 2.0, 2 }, new[] { 0.0, 2 } }));
        }

        // ---------- P1.7: referencias del catálogo de benchmarks ----------
        [Fact]
        public void Benchmarks_ItemsExistInProcedure_AndMatchTheirScenario()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "tools", "aceptacion", "0.8.0"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            var proc = File.ReadAllText(Path.Combine(dir, "tools", "aceptacion", "0.8.0", "PROCEDIMIENTO_0.8.0.md"));
            var known = new HashSet<string>(Regex.Matches(proc, @"^\| (N8-\d\d)", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value));
            Assert.Equal(13, known.Count);
            foreach (var b in Benchmarks.Catalog)
            {
                foreach (var it in b.Items) Assert.Contains(it, known);
                foreach (Match m in Regex.Matches(b.Arnes ?? "", @"N8-\d\d")) Assert.Contains(m.Value, b.Items);
            }
            Assert.Equal(5, Benchmarks.Catalog.Count(b => b.Items.Length > 0));
            Assert.Equal(new[] { "N8-02", "N8-11" }, Benchmarks.Catalog.First(b => b.Id == "B1").Items);
            Assert.Equal(new[] { "N8-06", "N8-13" }, Benchmarks.Catalog.First(b => b.Id == "B8").Items);
        }
    }
}
