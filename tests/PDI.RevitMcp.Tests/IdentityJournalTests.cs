// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Bridge;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Aceptación A (§3): normalizador único, identidad duradera, diario con estados correctos y escritura durable.</summary>
    public class IdentityJournalTests
    {
        private static string Tmp(string name = "operaciones.jsonl") => Path.Combine(Path.GetTempPath(), "pdi-a-" + Guid.NewGuid().ToString("N"), name);
        private static OperationJournal Journal(string path = null) => new OperationJournal(path ?? Tmp());

        // ---------- normalizador compartido ----------

        [Theory]
        [InlineData(null, OpSemantics.ModeQuery)]
        [InlineData("", OpSemantics.ModeQuery)]
        [InlineData(" Consulta ", OpSemantics.ModeQuery)]
        // alpha3: "prueba" ya no es consulta: es alias de ensayo (exige W). Cambio intencionado y más restrictivo.
        [InlineData("PRUEBA", OpSemantics.ModeTrial)]
        [InlineData("read", OpSemantics.ModeQuery)]
        [InlineData(" Ejecutar ", OpSemantics.ModeRun)]
        [InlineData("escribir", OpSemantics.ModeRun)]
        [InlineData("RUN", OpSemantics.ModeRun)]
        [InlineData("aplicar", OpSemantics.ModeRun)]
        public void ExecMode_Aliases_AndSpaces(string raw, string expected)
        {
            var a = new Dictionary<string, object>();
            if (raw != null) a["modo"] = raw;
            Assert.Equal(expected, OpSemantics.ExecMode(a, out var err));
            Assert.Null(err);
        }

        [Fact]
        public void UnknownMode_IsInvalid_NeverRead_AndNeverRetargeted()
        {
            var a = new Dictionary<string, object> { ["modo"] = "borrar_todo" };
            Assert.Null(OpSemantics.ExecMode(a, out var err));
            Assert.NotNull(err);
            Assert.Equal(OpSemantics.Kind.Invalid, OpSemantics.Classify(Wire.OpExec, a, out _));
            // El puente trata lo no-lectura como escritura: no se redirige a otro Revit.
            Assert.True(RevitConnection.IsWriteOp(Wire.OpExec, a));
        }

        [Theory]
        [InlineData(Wire.OpFam, "info", OpSemantics.Kind.Read)]
        [InlineData(Wire.OpFam, " Conector ", OpSemantics.Kind.Write)]
        [InlineData(Wire.OpFam, "borrar", OpSemantics.Kind.Invalid)]
        [InlineData(Wire.OpTask, "estado", OpSemantics.Kind.Read)]
        [InlineData(Wire.OpTask, "esperar", OpSemantics.Kind.Read)]
        [InlineData(Wire.OpTask, "iniciar", OpSemantics.Kind.Write)]
        [InlineData(Wire.OpTask, "reconciliar", OpSemantics.Kind.Write)]
        [InlineData(Wire.OpTask, "inventar", OpSemantics.Kind.Invalid)]
        [InlineData(Wire.OpDoc, "guardar", OpSemantics.Kind.Write)]
        public void Classify_SameSemanticsEverywhere(string op, string accion, OpSemantics.Kind kind)
        {
            Assert.Equal(kind, OpSemantics.Classify(op, new Dictionary<string, object> { ["accion"] = accion }, out _));
        }

        [Fact]
        public void Canonical_IsOrderIndependent_Recursive_AndStripsOnlyTopLevelControlArgs()
        {
            var a = new Dictionary<string, object>
            {
                ["b"] = 2L, ["a"] = new Dictionary<string, object> { ["z"] = 1.0, ["y"] = new List<object> { 1L, "x" }, ["clave"] = "interna" },
                ["clave"] = "k1", ["_tool"] = "exec", ["deadline_ms"] = 5000L,
            };
            var b = new Dictionary<string, object>
            {
                ["a"] = new Dictionary<string, object> { ["clave"] = "interna", ["y"] = new List<object> { 1L, "x" }, ["z"] = 1L },
                ["b"] = 2.0, ["clave"] = "OTRA", ["page"] = 3L,
            };
            Assert.Equal(OpSemantics.Canonical(a), OpSemantics.Canonical(b));
            var c = new Dictionary<string, object>(b) { ["a"] = new Dictionary<string, object> { ["clave"] = "distinta", ["y"] = new List<object> { 1L, "x" }, ["z"] = 1L } };
            Assert.NotEqual(OpSemantics.Canonical(a), OpSemantics.Canonical(c));
        }

        [Fact]
        public void Hash_IncludesResolvedCode_NotJustFileName()
        {
            var args = new Dictionary<string, object> { ["archivo"] = @"C:\x\script.cs", ["modo"] = "ejecutar" };
            var h1 = OperationJournal.HashArgs(Wire.OpExec, args, "return 1;");
            var h2 = OperationJournal.HashArgs(Wire.OpExec, args, "return 2;");
            Assert.NotEqual(h1, h2);
            Assert.Equal(h1, OperationJournal.HashArgs(Wire.OpExec, new Dictionary<string, object>(args) { ["clave"] = "otra" }, "return 1;"));
        }

        // ---------- identidad ----------

        [Theory]
        [InlineData(@"C:\Proy\Modelo.rvt", @"c:/proy/./sub/../MODELO.rvt")]
        [InlineData(@"\\srv\share\A.rvt", @"\\SRV\share\x\..\a.rvt")]
        public void NormalizePath_SameFile(string a, string b) => Assert.Equal(DocIdentity.NormalizePath(a), DocIdentity.NormalizePath(b));

        [Fact]
        public void UnsavedDocs_FromDifferentInstances_NeverShareKeys()
        {
            var a = DocIdentity.Unsaved("100.120000", 1, DocIdentity.GenerationOf("100.120000", 1));
            var b = DocIdentity.Unsaved("200.130000", 1, DocIdentity.GenerationOf("200.130000", 1));
            Assert.NotEqual(a.Key, b.Key);
            Assert.Null(a.Print);
        }

        [Fact]
        public void Resolver_AmbiguousTitle_ListsCandidates_AndPicksNone()
        {
            var snap = new List<DocResolver.Candidate>
            {
                new DocResolver.Candidate { Id = "d1", Key = "archivo:c:\\a\\familia.rfa", Title = "Familia.rfa", Path = @"C:\a\Familia.rfa" },
                new DocResolver.Candidate { Id = "d2", Key = "archivo:c:\\b\\familia.rfa", Title = "Familia", Path = @"C:\b\Familia.rfa", Active = true },
            };
            Assert.Equal(-1, DocResolver.Find(snap, "familia", out var err, out var amb));
            Assert.True(amb);
            Assert.Contains("d1", err);
            Assert.Contains("d2", err);
            Assert.Equal(1, DocResolver.Find(snap, @"c:/b/./FAMILIA.rfa", out _, out _));
            Assert.Equal(0, DocResolver.Find(snap, "D1", out _, out _));
            Assert.Equal(1, DocResolver.Find(snap, null, out _, out _));
        }

        // ---------- diario ----------

        [Fact]
        public void ReplacedDocument_SamePathOtherModel_IsDetected()
        {
            var j = Journal();
            j.Begin("k", "archivo:c:\\m.rvt", Wire.OpExec, "h", null, "s", OperationJournal.ReconcileMode.None, "rvt:AAA", "g1");
            j.Complete("k", "archivo:c:\\m.rvt", Status.Ok, null);
            var b = j.Begin("k", "archivo:c:\\m.rvt", Wire.OpExec, "h", null, "s", OperationJournal.ReconcileMode.None, "rvt:BBB", "g2");
            Assert.Equal(OperationJournal.BeginKind.DocumentReplaced, b.Kind);
        }

        [Fact]
        public void CopyOfModel_OtherPathSamePrint_AsksToReconcile_ThenAppliedIsNotRepeated()
        {
            var j = Journal();
            j.Begin("k", "archivo:c:\\a.rvt", Wire.OpExec, "h", null, "s", OperationJournal.ReconcileMode.None, "rvt:P", "g1");
            j.Complete("k", "archivo:c:\\a.rvt", Status.Ok, null);
            Assert.Equal(OperationJournal.BeginKind.PossibleCopy,
                j.Begin("k", "archivo:c:\\b.rvt", Wire.OpExec, "h", null, "s", OperationJournal.ReconcileMode.None, "rvt:P", "g2").Kind);
            Assert.Equal(OperationJournal.BeginKind.Completed,
                j.Begin("k", "archivo:c:\\b.rvt", Wire.OpExec, "h", null, "s", OperationJournal.ReconcileMode.Applied, "rvt:P", "g2").Kind);
        }

        [Fact]
        public void SaveAs_Alias_CarriesCompletedOperations()
        {
            var j = Journal();
            j.Begin("k", "nuevo:1#1", Wire.OpExec, "h", null, "s");
            j.Complete("k", "nuevo:1#1", Status.Ok, new Dictionary<string, object> { ["n"] = 3L });
            j.Alias("nuevo:1#1", "archivo:c:\\x.rvt");
            Assert.Equal(OperationJournal.BeginKind.Completed, j.Begin("k", "archivo:c:\\x.rvt", Wire.OpExec, "h", null, "s2").Kind);
            // Y otra instancia con su propio documento sin guardar no lo reclama.
            Assert.Equal(OperationJournal.BeginKind.New, j.Begin("k", "nuevo:2#1", Wire.OpExec, "h", null, "s3").Kind);
        }

        [Fact]
        public void LongJob_RepeatedKey_ReturnsSameJobId()
        {
            var j = Journal();
            j.Begin("k", "d", Wire.OpExec, "h", null, "s");
            j.LinkJob("k", "d", "J-42");
            j.Complete("k", "d", Status.EnProceso, null);
            var b = j.Begin("k", "d", Wire.OpExec, "h", null, "s2");
            Assert.Equal(OperationJournal.BeginKind.Running, b.Kind);
            Assert.Equal("J-42", j.RunningJob("k", "d"));
            j.Complete("k", "d", Status.Ok, null);
            Assert.Equal(OperationJournal.BeginKind.Completed, j.Begin("k", "d", Wire.OpExec, "h", null, "s3").Kind);
        }

        [Theory]
        [InlineData("parcial")]
        [InlineData("desconocido")]
        [InlineData("error")]
        [InlineData("cancelado")]
        public void PartialOrUnknown_RequiresExplicitReconcile_AndKeepsAttempts(string estado)
        {
            var path = Tmp();
            var j = Journal(path);
            j.Begin("k", "d", Wire.OpExec, "h", null, "s");
            j.Complete("k", "d", estado, null);
            Assert.Equal(OperationJournal.BeginKind.Interrupted, j.Begin("k", "d", Wire.OpExec, "h", null, "s2").Kind);
            Assert.Equal(OperationJournal.BeginKind.New, j.Begin("k", "d", Wire.OpExec, "h", null, "s2", OperationJournal.ReconcileMode.NotApplied).Kind);
            j.Complete("k", "d", Status.Ok, null);
            // Otro proceso lee el mismo diario: dos intentos con su historia.
            var r = Journal(path).Get("k", "d");
            Assert.Equal(2, r.Attempts.Count);
            Assert.Equal(estado, r.Attempts[0].Estado);
            Assert.Equal(Status.Ok, r.Attempts[1].Estado);
        }

        [Fact]
        public void RevertedConfirmed_AllowsRetry_WithoutReconcile()
        {
            var j = Journal();
            j.Begin("k", "d", Wire.OpExec, "h", null, "s");
            j.Complete("k", "d", Status.Revertido, null);
            Assert.Equal(OperationJournal.BeginKind.New, j.Begin("k", "d", Wire.OpExec, "h", null, "s").Kind);
        }

        [Fact]
        public void TornLastLine_IsRecovered_AndNextAppendStartsOnNewLine()
        {
            var path = Tmp();
            var j = Journal(path);
            j.Begin("k1", "d", Wire.OpExec, "h", null, "s");
            j.Complete("k1", "d", Status.Ok, null);
            File.AppendAllText(path, "{\"ev\":\"inicio\",\"clave\":\"k2\",\"do"); // corte de luz a mitad de línea
            var j2 = Journal(path);
            Assert.Equal(OperationJournal.BeginKind.Completed, j2.Begin("k1", "d", Wire.OpExec, "h", null, "s").Kind);
            Assert.Equal(OperationJournal.BeginKind.New, j2.Begin("k3", "d", Wire.OpExec, "h", null, "s").Kind);
            j2.Complete("k3", "d", Status.Ok, null);
            var j3 = Journal(path);
            Assert.Equal(1, j3.CorruptLines);
            Assert.Equal(OperationJournal.BeginKind.Completed, j3.Begin("k3", "d", Wire.OpExec, "h", null, "s").Kind);
        }

        [Fact]
        public void UnwritableJournal_IsVisible_NotSilent()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pdi-a-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var blocker = Path.Combine(dir, "soy_un_archivo");
            File.WriteAllText(blocker, "x");
            // La carpeta del diario es un archivo: ni se crea ni se escribe.
            Assert.Throws<JournalUnavailableException>(() => new OperationJournal(Path.Combine(blocker, "operaciones.jsonl")));
        }

        [Fact]
        public void TwoProcesses_SameJournal_SeeEachOther()
        {
            var path = Tmp();
            var a = Journal(path);
            var b = Journal(path);
            a.Begin("k", "d", Wire.OpExec, "h", null, "A");
            a.Complete("k", "d", Status.Ok, null);
            Assert.Equal(OperationJournal.BeginKind.Completed, b.Begin("k", "d", Wire.OpExec, "h", null, "B").Kind);
            Assert.Equal(OperationJournal.BeginKind.Mismatch, b.Begin("k", "d", Wire.OpExec, "otra", null, "B", OperationJournal.ReconcileMode.Applied).Kind);
        }

        // ---------- evidencia de persistencia por apertura (tareas) ----------

        [Fact]
        public void PersistedSince_IsPerGeneration()
        {
            var t = new TaskRecord { Id = "t" };
            t.NotePersist(new PersistEvidence { Kind = "guardado", When = "2026-10-20T10:00:00.000Z", Generation = "g1", Seq = 5 });
            Assert.True(t.PersistedSince("g1", "2026-10-20T09:59:00.000Z"));
            Assert.False(t.PersistedSince("g1", "2026-10-20T10:01:00.000Z"));
            Assert.False(t.PersistedSince("g2", "2026-10-20T09:00:00.000Z"));
            Assert.Null(t.PersistedSince("g1", null));
            var back = TaskRecord.FromJson(Json.ParseObject(Json.Write(t.ToJson())));
            Assert.True(back.PersistedSince("g1", "2026-10-20T09:59:00.000Z"));
        }
    }
}
