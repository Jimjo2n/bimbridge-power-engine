// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Diario de operaciones (idempotencia y recuperación), resultados largos por handle, conjuntos por documento, matriz y regla de tags.</summary>
    public class DurabilityTests
    {
        static DurabilityTests()
        {
            // Nada de las pruebas escribe en %LOCALAPPDATA% real.
            Environment.SetEnvironmentVariable("PDI_REVITMCP_HOME", Path.Combine(Path.GetTempPath(), "pdi-home-" + Guid.NewGuid().ToString("N")));
        }

        private static string TempJournal() => Path.Combine(Path.GetTempPath(), "pdi-j-" + Guid.NewGuid().ToString("N"), "operaciones.jsonl");

        [Fact]
        public void Journal_SameKey_IsNotAppliedTwice_EvenAfterRestart()
        {
            var path = TempJournal();
            var j = new OperationJournal(path);
            var args = new Dictionary<string, object> { ["plan_id"] = "p1", [Wire.KeyArg] = "k1" };
            var h = OperationJournal.HashArgs(Wire.OpCommit, args);
            Assert.Equal(OperationJournal.BeginKind.New, j.Begin("k1", "d1", Wire.OpCommit, h, "tareaA", "s1").Kind);
            Assert.Equal(OperationJournal.BeginKind.Running, j.Begin("k1", "d1", Wire.OpCommit, h, "tareaA", "s2").Kind);
            j.Complete("k1", "d1", Status.Ok, new Dictionary<string, object> { ["cambiados"] = 3L });

            // "Reinicio" de Revit: el diario se relee del disco.
            var j2 = new OperationJournal(path);
            var again = j2.Begin("k1", "d1", Wire.OpCommit, h, "tareaA", "s3");
            Assert.Equal(OperationJournal.BeginKind.Completed, again.Kind);
            Assert.Equal(Status.Ok, again.Previous.Estado);
            // La misma clave en otro documento es otra operación.
            Assert.Equal(OperationJournal.BeginKind.New, j2.Begin("k1", "d2", Wire.OpCommit, h, "tareaA", "s3").Kind);
        }

        [Fact]
        public void Journal_InterruptedOperation_NeedsReconcile()
        {
            var path = TempJournal();
            var j = new OperationJournal(path);
            j.Begin("k2", "d1", Wire.OpExec, "h", "t", "s1");
            // Se corta (Revit se cierra): sin "fin". Al releer, queda interrumpida y NO se repite sola.
            var j2 = new OperationJournal(path);
            var r = j2.Begin("k2", "d1", Wire.OpExec, "h", "t", "s2");
            Assert.Equal(OperationJournal.BeginKind.Interrupted, r.Kind);
            Assert.Equal(OperationJournal.BeginKind.New, j2.Begin("k2", "d1", Wire.OpExec, "h", "t", "s2", reconcile: true).Kind);
            Assert.Single(j2.ForTask("t"));
        }

        [Fact]
        public void Journal_SameKeyDifferentContent_IsRejected()
        {
            var j = new OperationJournal(TempJournal());
            var h1 = OperationJournal.HashArgs("exec", new Dictionary<string, object> { ["codigo"] = "a", [Wire.KeyArg] = "k", [Wire.TaskArg] = "t" });
            var h2 = OperationJournal.HashArgs("exec", new Dictionary<string, object> { ["codigo"] = "b", [Wire.KeyArg] = "k" });
            var h1b = OperationJournal.HashArgs("exec", new Dictionary<string, object> { ["codigo"] = "a", [Wire.KeyArg] = "otra" });
            Assert.Equal(h1, h1b); // la clave y la tarea no cuentan en la huella
            j.Begin("k", "d", "exec", h1, null, "s");
            j.Complete("k", "d", Status.Revertido, null);
            Assert.Equal(OperationJournal.BeginKind.Mismatch, j.Begin("k", "d", "exec", h2, null, "s").Kind);
            // 0.6.0 (H03): sólo una reversión CONFIRMADA permite reintentar con el mismo contenido; un "error" a secas exige reconciliar
            // (ver TransportRegressionTests.PartialCommitErrorMustRequireReconciliationBeforeRetry).
            Assert.Equal(OperationJournal.BeginKind.New, j.Begin("k", "d", "exec", h1, null, "s").Kind);
        }

        [Fact]
        public void ResultStore_LongTextIsShortened_AndReadableByLines()
        {
            var text = string.Join("\n", Enumerable.Range(0, 2000).Select(i => "fila " + i + " " + new string('x', 20)));
            var shown = ResultStore.Shorten(text, 1000, "t", out var id);
            Assert.NotNull(id);
            Assert.True(shown.Length < 1200);
            Assert.Contains("res " + id, shown);
            var part = ResultStore.Read(id, 1500, 3, out var total);
            Assert.Equal(2000, total);
            Assert.StartsWith("fila 1500 ", part);
            Assert.Null(ResultStore.Read("../../etc/passwd", 0, 1, out _));
            Assert.Equal("corto", ResultStore.Shorten("corto", 1000, "t", out var none));
            Assert.Null(none);
        }

        [Fact]
        public void NamedSets_AreBoundToTheirDocument()
        {
            var s = new NamedSets();
            s.Save("$luces", new long[] { 1, 2 }, "d1", "SAMPLE PROJECT");
            Assert.True(s.TryGet("$luces", "d1", out var ids, out _));
            Assert.Equal(new long[] { 1, 2 }, ids);
            Assert.False(s.TryGet("$luces", "d2", out _, out var err));
            Assert.Contains("SAMPLE PROJECT", err);
            Assert.Equal(1, s.RemoveDoc("d1"));
            Assert.False(s.TryGet("$luces", out _));
        }

        [Fact]
        public void Capabilities_DocMatchesCode_AndFamilyRowsExcludeProjectOnly()
        {
            var props = File.ReadAllText(Path.Combine(FindRoot(), "Directory.Build.props"));
            var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
            var doc = Path.Combine(FindRoot(), "docs", "MATRIZ_CAPACIDADES.md");
            // Regenerar: PDI_GEN_MATRIZ=1 dotnet test --filter Capabilities_DocMatchesCode
            if (Environment.GetEnvironmentVariable("PDI_GEN_MATRIZ") == "1") File.WriteAllText(doc, Capabilities.Markdown(version));
            Assert.True(File.Exists(doc), "falta docs/MATRIZ_CAPACIDADES.md");
            Assert.Equal(Capabilities.Markdown(version).Replace("\r\n", "\n"), File.ReadAllText(doc).Replace("\r\n", "\n"));
            var fam = Capabilities.ForDoc("familia", 2024).Select(r => (string)((object[])r)[0]).ToList();
            Assert.Contains("fam conector", fam);
            Assert.DoesNotContain("geo", fam);
            Assert.Contains("geo", Capabilities.NotFor("familia"));
        }

        [Fact]
        public void LightingAudit_TagRuleIsPerTask()
        {
            var fx = new List<LightingAudit.Fixture> { new LightingAudit.Fixture { Id = 1, Level = "L1" }, new LightingAudit.Fixture { Id = 2, Level = "L1" } };
            var tags = new List<LightingAudit.Tag> { new LightingAudit.Tag { Id = 10, ViewId = 5, Tagged = new long[] { 1 } } };
            var uno = LightingAudit.Summarize(fx, tags, null, out _, tagRule: "uno");
            Assert.Equal(false, uno["cumple_regla"]);
            var ninguno = LightingAudit.Summarize(fx, tags, null, out var sets, tagRule: "ninguno");
            Assert.Equal(false, ninguno["cumple_regla"]);
            Assert.Equal(new long[] { 10 }, sets["$tags_de_luminarias"]);
            var ninguno0 = LightingAudit.Summarize(fx, new List<LightingAudit.Tag>(), null, out _, tagRule: "ninguno");
            Assert.Equal(true, ninguno0["cumple_regla"]);
            Assert.Throws<ArgumentException>(() => LightingAudit.Summarize(fx, tags, null, out _, tagRule: "dos"));
        }

        private static string FindRoot()
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "PDI.RevitMcp.sln"))) d = Path.GetDirectoryName(d);
            return d;
        }
    }
}
