// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using PDI.RevitMcp.Setup;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Bloqueo fail-closed, persistencia y reconciliación del documento. Sin Revit.</summary>
    public class WriteSafetyTests : IDisposable
    {
        private const string Doc = "c:\\lab\\x.rvt";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-safe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static Plan NewPlan(string id = "P-1")
        {
            var p = new Plan { Id = id, DocKey = Doc, DocTitle = "x", SessionId = "s", Client = "c", CreatedUtc = DateTime.UtcNow, TotalBatches = 1 };
            p.Entries.Add(new PlanEntry { Op = "set_param", Id = 5, Key = "Comments", Before = "a", After = "b", OpIndex = 1 });
            return p;
        }

        private PlanStore Store() => new PlanStore(_root);
        private string FileOf(PlanStore s, string id) => Path.Combine(s.DocDir(Doc), id + ".json");

        [Fact]
        public void BlockJson_HasCodeOnlyWhenBlocked()
        {
            var b = new DocWriteBlock { DocKey = Doc };
            Assert.Null(b.ToJson()["codigo"]);
            b.Plans.Add("P-1");
            Assert.Equal(DocWriteBlock.Code, b.ToJson()["codigo"]);
        }

        [Fact]
        public void BridgeWiring_LostConnectionOnWriteIsUnknown()
        {
            var src = Source("src/PDI.RevitMcp.Bridge/RevitConnection.cs");
            var i = src.IndexOf("catch (IOException)", src.IndexOf("public async Task<Response> CallAsync", StringComparison.Ordinal), StringComparison.Ordinal);
            var block = src.Substring(i, 600);
            Assert.True(block.IndexOf("Status.Desconocido", StringComparison.Ordinal) < block.IndexOf("Status.RevitNoDisponible", StringComparison.Ordinal));
        }

        [Fact]
        public void CleanDocument_IsWritable_AndCleanFinishLeavesNoLock()
        {
            var s = Store();
            var p = NewPlan();
            s.Add(p);
            Assert.False(s.CheckWritable(Doc).Blocked);
            s.BeginWrite(p);
            Assert.True(File.Exists(FileOf(s, p.Id)));
            Assert.True(s.Finish(p, PlanStatus.Aplicado, "ok"));
            Assert.False(File.Exists(FileOf(s, p.Id)));
            Assert.False(s.CheckWritable(Doc).Blocked);
        }

        [Fact]
        public void IntentWithoutResult_SurvivesRestart_AsUnknown_AndBlocks()
        {
            var s = Store();
            var p = NewPlan();
            s.Add(p);
            s.BeginWrite(p); // aquí muere Revit

            var again = Store();
            Assert.True(again.CheckWritable(Doc).Blocked);
            again.LoadPersisted(Doc);
            var q = again.Get(p.Id);
            Assert.Equal(Status.Desconocido, q.Status);
            Assert.NotEqual(PlanStatus.Listo, q.Status);
            var other = NewPlan("P-2");
            Assert.Throws<DocWriteBlockedException>(() => again.BeginWrite(other));
            Assert.False(File.Exists(FileOf(again, "P-2")));
        }

        [Fact]
        public void BlockIsPerDocument()
        {
            var s = Store();
            var p = NewPlan();
            s.BeginWrite(p);
            Assert.True(s.CheckWritable(Doc).Blocked);
            Assert.False(s.CheckWritable("c:\\lab\\otro.rvt").Blocked);
        }

        [Fact]
        public void HeldPlan_DoesNotBlock_ButApplyingItDoesUntilFinished()
        {
            var s = Store();
            var p = NewPlan();
            s.Hold(p);
            Assert.False(s.CheckWritable(Doc).Blocked);
            s.BeginWrite(p);
            Assert.True(Store().CheckWritable(Doc).Blocked);
        }

        [Fact]
        public void IntentWriteFailure_RefusesBeforeTouchingAnything()
        {
            var s = Store();
            s.WriteFile = (_, __) => throw new IOException("disco lleno");
            var p = NewPlan();
            var ex = Assert.Throws<PlanStoreException>(() => s.BeginWrite(p));
            Assert.Contains("disco lleno", ex.Message);
            Assert.Equal(PlanStatus.Listo, p.Status);
            Assert.Null(p.Reconciliation);
        }

        [Fact]
        public void EvidenceWriteFailure_IsReported_AndStillBlocksInMemory()
        {
            var s = Store();
            var p = NewPlan();
            s.Add(p);
            GroupAbort.PreserveInterrupted(p, "x");
            s.WriteFile = (_, __) => throw new UnauthorizedAccessException("denegado");
            Assert.False(s.Finish(p, Status.Desconocido, "incierto"));
            Assert.Contains("no es durable", p.StorageWarning);
            Assert.True(p.Describe(0).ContainsKey("almacenamiento"));
            Assert.True(s.CheckWritable(Doc).Blocked);
        }

        [Fact]
        public void IntentRemovalFailure_KeepsBlocking_NeverReportsDurableSuccess()
        {
            var s = Store();
            var p = NewPlan();
            s.Add(p);
            s.BeginWrite(p);
            s.RemoveFile = _ => throw new IOException("en uso");
            Assert.False(s.Finish(p, PlanStatus.Aplicado, "ok"));
            Assert.NotNull(p.StorageWarning);
            Assert.True(Store().CheckWritable(Doc).Blocked);
        }

        [Fact]
        public void UnknownWithoutEvidence_GetsEvidence()
        {
            var s = Store();
            var p = NewPlan();
            s.Add(p);
            s.Finish(p, Status.Desconocido, "sin datos");
            Assert.NotNull(p.Reconciliation);
            Assert.True(Store().CheckWritable(Doc).Blocked);
        }

        [Theory]
        [InlineData("{no es json")]
        [InlineData("")]
        [InlineData("{}")]
        public void CorruptEvidence_Blocks_AndIsKept(string content)
        {
            var s = Store();
            Directory.CreateDirectory(s.DocDir(Doc));
            var f = FileOf(s, "P-9");
            File.WriteAllText(f, content);
            var b = s.CheckWritable(Doc);
            Assert.True(b.Blocked);
            Assert.Contains("P-9.json", b.CorruptFiles);
            Assert.True(File.Exists(f));
        }

        [Fact]
        public void UnreadableEvidence_Blocks()
        {
            var s = Store();
            var p = NewPlan();
            s.BeginWrite(p);
            using (new FileStream(FileOf(s, p.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(s.CheckWritable(Doc).Errors.Count > 0);
        }

        [Fact]
        public void LockHeldByAnotherProcess_FailsClosed()
        {
            var s = Store();
            s.LockWait = TimeSpan.FromMilliseconds(100);
            Directory.CreateDirectory(s.DocDir(Doc));
            using (new FileStream(Path.Combine(s.DocDir(Doc), ".bloqueo"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<PlanStoreException>(() => s.BeginWrite(NewPlan()));
        }

        [Fact]
        public async Task ConcurrentStores_ExactlyOneBeginsWrite()
        {
            var wins = 0; var refused = 0;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                var s = Store();
                s.LockWait = TimeSpan.FromSeconds(10);
                try { s.BeginWrite(NewPlan("P-" + i)); System.Threading.Interlocked.Increment(ref wins); }
                catch (DocWriteBlockedException) { System.Threading.Interlocked.Increment(ref refused); }
            })));
            Assert.Equal(1, wins);
            Assert.Equal(7, refused);
        }

        [Fact]
        public void Checkpoint_KeepsCreatedIds_ForAfterACrash_WithoutMarkingThePlan()
        {
            var s = Store();
            var p = NewPlan();
            s.BeginWrite(p);
            p.Status = PlanStatus.EnProceso;
            p.Created.AddRange(new long[] { 100, 101 });
            p.AppliedBatches = 1;
            s.Checkpoint(p);
            Assert.Null(p.Reconciliation);
            Assert.Equal(PlanStatus.EnProceso, p.Status);
            var again = Store();
            again.LoadPersisted(Doc);
            Assert.Equal(new long[] { 100, 101 }, again.Get(p.Id).Created.ToArray());
            Assert.Equal(2, ((List<object>)again.Get(p.Id).Reconciliation["ids_creados"]).Count);
        }

        // ---- Veredicto ----

        private static ReconcileCheck Eval(Plan p, string value, params long[] existing) =>
            ReconcileEvaluator.Evaluate(p, _ => value, id => existing.Contains(id));

        [Fact]
        public void Evaluate_AllAsBefore_NoCreatedLeft_IsReverted() => Assert.Equal(ReconcileCheck.Reverted, Eval(NewPlan(), "a").Verdict);

        [Fact]
        public void Evaluate_AllAsAfter_IsApplied() => Assert.Equal(ReconcileCheck.Applied, Eval(NewPlan(), "b").Verdict);

        [Fact]
        public void Evaluate_OtherValue_IsNotVerifiable() => Assert.Equal(ReconcileCheck.NotVerifiable, Eval(NewPlan(), "z").Verdict);

        [Fact]
        public void Evaluate_ReadError_IsNotVerifiable()
        {
            var c = ReconcileEvaluator.Evaluate(NewPlan(), _ => throw new InvalidOperationException("x"), _ => true);
            Assert.False(c.Verified);
        }

        [Fact]
        public void Evaluate_ValuesRevertedButCreatedSurvives_IsNotVerifiable()
        {
            var p = NewPlan();
            p.Created.Add(100);
            Assert.False(Eval(p, "a", 100).Verified);
        }

        [Fact]
        public void Evaluate_CreationsWithLostIds_IsNotVerifiable()
        {
            var p = NewPlan();
            p.Entries.Add(new PlanEntry { Op = "place", Key = "nuevo", OpIndex = 1 });
            p.AppliedBatches = 1;
            Assert.False(Eval(p, "a").Verified);
        }

        [Fact]
        public void Evaluate_TemplatesCannotBeDeclaredApplied()
        {
            var p = NewPlan();
            p.Created.Add(100);
            p.Entries.Add(new PlanEntry { Op = "set_param", Ref = 1, Key = "Comments", After = "x", OpIndex = 2 });
            Assert.False(Eval(p, "b", 100).Verified);
        }

        [Fact]
        public void Evaluate_DeletedOriginalMissing_IsNotReverted()
        {
            var p = NewPlan();
            GroupAbort.Preserve(p, "Error", null, new GroupAbort.Evidence { Deleted = new long[] { 77 } });
            Assert.False(Eval(p, "a").Verified);
            Assert.Equal(ReconcileCheck.Reverted, Eval(p, "a", 77).Verdict);
        }

        // ---- Liberar ----

        private Plan Unknown(PlanStore s)
        {
            var p = NewPlan();
            s.Add(p);
            s.BeginWrite(p);
            s.Finish(p, Status.Desconocido, "incierto");
            return p;
        }

        [Fact]
        public void Reconcile_NotVerifiable_KeepsBlock_AndRecordsAttempt()
        {
            var s = Store();
            var p = Unknown(s);
            Assert.False(s.Reconcile(p, new ReconcileCheck(), "s1"));
            Assert.True(s.CheckWritable(Doc).Blocked);
            var again = Store();
            again.LoadPersisted(Doc);
            Assert.Single((List<object>)again.Get(p.Id).Reconciliation["intentos"]);
        }

        [Fact]
        public void Reconcile_Verified_Releases_ArchivesEvidence_AndPlanNeverReturnsToReady()
        {
            var s = Store();
            var p = Unknown(s);
            Assert.True(s.Reconcile(p, new ReconcileCheck { Verdict = ReconcileCheck.Reverted }, "s1"));
            Assert.False(s.CheckWritable(Doc).Blocked);
            Assert.Equal(PlanStatus.Reconciliado, p.Status);
            Assert.True(p.IsFinal);
            var archived = Directory.GetFiles(Path.Combine(s.DocDir(Doc), "reconciliados"));
            Assert.Single(archived);
            var o = Json.ParseObject(File.ReadAllText(archived[0]));
            Assert.Equal("s1", ((Dictionary<string, object>)o["registro"])["autor"]);
            Assert.Equal("P-1", ((Dictionary<string, object>)o["plan"])["id"]);
            Assert.False(Store().CheckWritable(Doc).Blocked);
        }

        [Fact]
        public void Reconcile_ArchiveFailure_KeepsBlock()
        {
            var s = Store();
            var p = Unknown(s);
            s.WriteFile = (_, __) => throw new IOException("disco lleno");
            Assert.Throws<PlanStoreException>(() => s.Reconcile(p, new ReconcileCheck { Verdict = ReconcileCheck.Applied }, "s1"));
            Assert.True(Store().CheckWritable(Doc).Blocked);
            Assert.NotEqual(PlanStatus.Reconciliado, p.Status);
        }

        [Fact]
        public void Quarantine_KeepsFile_Registers_AndReleases_ButNeedsJustification()
        {
            var s = Store();
            Directory.CreateDirectory(s.DocDir(Doc));
            File.WriteAllText(FileOf(s, "P-9"), "{roto");
            Assert.Throws<ArgumentException>(() => s.QuarantineCorrupt(s.DocDir(Doc), "P-9.json", "operator", " "));
            Assert.Throws<ArgumentException>(() => s.QuarantineCorrupt(s.DocDir(Doc), "..\\P-9.json", "operator", "m"));
            s.QuarantineCorrupt(s.DocDir(Doc), "P-9.json", "operator", "revisé el modelo");
            Assert.False(s.CheckWritable(Doc).Blocked);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(s.DocDir(Doc), "cuarentena")).Length);
        }

        // ---- Ensayo ----

        [Theory]
        [InlineData(true, true, true, TrialGuard.CorrectReverted)]
        [InlineData(false, true, true, TrialGuard.FunctionalFailureReverted)]
        [InlineData(true, false, false, TrialGuard.RollbackFailed)]
        [InlineData(false, false, false, TrialGuard.RollbackFailed)]
        [InlineData(true, true, false, TrialGuard.UnknownNeedsReconciliation)]
        public void Trial_FourOutcomes(bool functional, bool rollback, bool restored, string expected) =>
            Assert.Equal(expected, TrialGuard.Classify(functional, rollback, restored));

        // ---- Código del add-in: no compila aquí sin Revit, así que se fija el cableado ----

        private static string Source(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PDI.RevitMcp.sln"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir, relative));
        }

        [Fact]
        public void AddinWiring_WritesPassTheBarrier()
        {
            var handler = Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.cs");
            Assert.True(handler.IndexOf("_plans.BeginWrite(plan)", StringComparison.Ordinal) < handler.IndexOf("applyEntered = true", StringComparison.Ordinal));
            Assert.Contains("WriteBarrier(request.Id, docs)", Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.Exec.cs"));
            Assert.Contains("Plans.BeginWrite(p)", Source("src/PDI.RevitMcp.Addin/Ui/Runtime.cs"));
            Assert.Contains("Checkpoint(_plan)", Source("src/PDI.RevitMcp.Addin/Revit/Writes/PlanApplier.cs"));
        }

        // ---- Descriptor con pid reutilizado ----

        [Fact]
        public void IsAlive_ProtectedProcess_DoesNotThrow()
        {
            if (!OperatingSystem.IsWindows()) return;
            Assert.False(InstanceDescriptor.IsAlive(4)); // pid 4 es System: Windows niega abrirlo
        }

        // ---- Instalador ----

        [Fact]
        public void CodexRestore_OnlyTouchesOurSection()
        {
            var saved = "model = \"viejo\"\n\n[mcp_servers.pdi-revit]\ncommand = \"viejo.exe\"\nstartup_timeout_sec = 9\n";
            var current = "model = \"nuevo\"\n\n[otra]\nx = 1\n\n[mcp_servers.pdi-revit]\ncommand = \"nuevo.exe\"\n";
            var r = SetupCore.ReplaceTomlSection(current, saved, SetupCore.CodexSection);
            Assert.Contains("model = \"nuevo\"", r);
            Assert.Contains("[otra]", r);
            Assert.Contains("viejo.exe", r);
            Assert.Contains("startup_timeout_sec = 9", r);
            Assert.DoesNotContain("nuevo.exe", r);
        }

        [Fact]
        public void CodexRestore_RemovesOurSection_WhenItDidNotExist()
        {
            var r = SetupCore.ReplaceTomlSection("a = 1\n\n[mcp_servers.pdi-revit]\ncommand = \"x\"\n", "a = 0\n", SetupCore.CodexSection);
            Assert.Contains("a = 1", r);
            Assert.DoesNotContain("pdi-revit", r);
        }
    }
}
