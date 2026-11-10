// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using PDI.RevitMcp.Setup;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Native Validation 0.9.0: reversión, reconciliación durable y retorno de instalación. Sin Revit.</summary>
    public class ReconciliationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-recon-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static Plan Sample()
        {
            var p = new Plan { Id = "P-1", DocKey = "c:\\lab\\x.rvt", DocTitle = "x", SessionId = "s", Client = "c", CreatedUtc = DateTime.UtcNow, TotalBatches = 2, AppliedBatches = 1 };
            p.Entries.Add(new PlanEntry { Op = "set_param", Id = 5, Key = "Comments", Before = "a", After = "b" });
            p.Created.AddRange(new long[] { 100, 101, 102 });
            p.CreatedByOp[0] = new List<long> { 100, 101 };
            p.CreatedByOp[1] = new List<long> { 102 };
            return p;
        }

        private static GroupAbort.Evidence Ev() => new GroupAbort.Evidence
        {
            Deleted = new long[] { 9, 7 }, Changed = 3, DeletedCount = 2,
            Skipped = new Dictionary<string, List<long>> { ["motivo"] = new List<long> { 11 } }, Warnings = new[] { "aviso" },
        };

        [Fact]
        public void RollbackThatThrows_IsNotObserved_AndKeepsError()
        {
            var st = GroupAbort.Attempt(() => true, () => throw new InvalidOperationException("boom"), () => "x", out var err);
            Assert.Equal(GroupAbort.NotObserved, st);
            Assert.Contains("boom", err);
            Assert.NotEqual(GroupAbort.RolledBack, st);
        }

        [Theory]
        [InlineData("Pending")]
        [InlineData("Error")]
        [InlineData("Committed")]
        public void RollbackWithOtherLiteralState_IsReturnedVerbatim(string state)
        {
            Assert.Equal(state, GroupAbort.Attempt(() => true, () => state, () => "z", out var err));
            Assert.Null(err);
        }

        [Fact]
        public void NotStartedGroup_UsesItsStatus_AndNeverCallsRollback()
        {
            var called = false;
            var st = GroupAbort.Attempt(() => false, () => { called = true; return "RolledBack"; }, () => "Uninitialized", out _);
            Assert.False(called);
            Assert.Equal("Uninitialized", st);
        }

        [Fact]
        public void Preserve_KeepsAllIds_WithoutTruncating()
        {
            var p = Sample();
            for (long i = 200; i < 400; i++) p.Created.Add(i);
            GroupAbort.Preserve(p, "Pending", null, Ev());
            Assert.Equal(203, ((List<object>)p.Reconciliation["ids_creados"]).Count);
            Assert.Equal(new object[] { 7L, 9L }, ((List<object>)p.Reconciliation["ids_borrados"]).ToArray());
            Assert.Equal(2, ((List<object>)((Dictionary<string, object>)p.Reconciliation["creados_por_op"])["@0"]).Count);
            Assert.Single((List<object>)p.Reconciliation["entradas"]);
        }

        [Fact]
        public void Reconciliation_SurvivesRestart_AndIsNeverHeldOrReapplied()
        {
            var store = new PlanStore(_root);
            var p = Sample();
            store.Add(p);
            GroupAbort.Preserve(p, "Error", "fallo", Ev());
            store.Finish(p, Status.Desconocido, "incierto");

            var restarted = new PlanStore(_root);
            Assert.Equal(1, restarted.LoadPersisted(p.DocKey));
            var q = restarted.Get("P-1");
            Assert.Equal(Status.Desconocido, q.Status);
            Assert.NotNull(q.FinishedUtc);
            Assert.Empty(restarted.Held(p.DocKey));
            Assert.Single(restarted.Unreconciled(p.DocKey));
            Assert.Equal(new long[] { 100, 101, 102 }, q.Created.ToArray());
            Assert.Equal(new long[] { 102 }, q.CreatedByOp[1].ToArray());
            Assert.Equal("Error", q.Reconciliation["grupo_estado"]);
            Assert.Equal(3, ((List<object>)q.Reconciliation["ids_creados"]).Count);
            Assert.Equal(1L, q.Reconciliation["lotes_confirmados"]);
            Assert.True(q.Describe(0).ContainsKey("reconciliacion"));
        }

        [Fact]
        public void Finish_WithoutEvidence_StillDeletesTheFile()
        {
            var store = new PlanStore(_root);
            var p = Sample();
            store.Hold(p);
            var file = Path.Combine(store.DocDir(p.DocKey), p.Id + ".json");
            Assert.True(File.Exists(file));
            store.Finish(p, PlanStatus.Aplicado, "ok");
            Assert.False(File.Exists(file));
        }

        [Fact]
        public void Interrupted_KeepsApplierEvidenceWhenPresent()
        {
            var p = Sample();
            GroupAbort.Preserve(p, "Pending", null, Ev());
            GroupAbort.PreserveInterrupted(p, "otro");
            Assert.Equal("Pending", p.Reconciliation["grupo_estado"]);
            var q = Sample();
            GroupAbort.PreserveInterrupted(q, "corte");
            Assert.Equal(GroupAbort.NotObserved, q.Reconciliation["grupo_estado"]);
            Assert.Equal("corte", q.Reconciliation["rollback_error"]);
        }

        /// <summary>Recorrido completo del aborto con el mismo orden que PlanApplier.AbortGroup, por cada resultado de RollBack.</summary>
        private static string Abort(Plan plan, Func<string> rollBack, GroupAbort.Evidence ev, out string error)
        {
            var state = GroupAbort.Attempt(() => true, rollBack, () => "x", out error);
            if (state == GroupAbort.RolledBack) { plan.Created.Clear(); plan.CreatedByOp.Clear(); }
            else GroupAbort.Preserve(plan, state, error, ev);
            return state;
        }

        public static IEnumerable<object[]> BadRollbacks()
        {
            yield return new object[] { (Func<string>)(() => throw new InvalidOperationException("RollBack lanzó")), GroupAbort.NotObserved };
            yield return new object[] { (Func<string>)(() => "Pending"), "Pending" };
            yield return new object[] { (Func<string>)(() => "Error"), "Error" };
        }

        [Theory]
        [MemberData(nameof(BadRollbacks))]
        public void Abort_PureCreations_KeepsEverythingAfterRestart(Func<string> rollBack, string expected)
        {
            var store = new PlanStore(_root);
            var p = new Plan { Id = "P-C", DocKey = "c:\\lab\\c.rvt", DocTitle = "c", SessionId = "s", Client = "c", CreatedUtc = DateTime.UtcNow, TotalBatches = 1, AppliedBatches = 1 };
            p.Entries.Add(new PlanEntry { Op = "place", Id = 0, Key = "nuevo", After = "1", OpIndex = 0 });
            p.Entries.Add(new PlanEntry { Op = "place", Id = 0, Key = "nuevo", After = "1", OpIndex = 0 });
            p.Created.AddRange(new long[] { 500, 501 });
            p.CreatedByOp[0] = new List<long> { 500, 501 };
            store.Add(p);
            Assert.Equal(expected, Abort(p, rollBack, new GroupAbort.Evidence { Changed = 2 }, out _));
            store.Finish(p, Status.Desconocido, "incierto");

            var again = new PlanStore(_root);
            again.LoadPersisted(p.DocKey);
            var q = again.Get("P-C");
            Assert.Equal(expected, q.Reconciliation["grupo_estado"]);
            Assert.Equal(new object[] { 500L, 501L }, ((List<object>)q.Reconciliation["ids_creados"]).ToArray());
            Assert.Equal(2, ((List<object>)q.Reconciliation["entradas"]).Count);
            Assert.Equal(2L, q.Reconciliation["cambios_intentados"]);
        }

        [Theory]
        [MemberData(nameof(BadRollbacks))]
        public void Abort_MixedCreationsRefsAndDeletes_KeepsAllEvidence(Func<string> rollBack, string expected)
        {
            var store = new PlanStore(_root);
            var p = new Plan { Id = "P-M", DocKey = "c:\\lab\\m.rvt", DocTitle = "m", SessionId = "s", Client = "c", CreatedUtc = DateTime.UtcNow, TotalBatches = 3, AppliedBatches = 2 };
            p.Entries.Add(new PlanEntry { Op = "place", Id = 0, Key = "nuevo", After = "1", OpIndex = 0 });
            p.Entries.Add(new PlanEntry { Op = "set_param", Ref = 1, Key = "Comments", After = "x", OpIndex = 1 });
            p.Entries.Add(new PlanEntry { Op = "delete", Id = 77, Key = "exists", Before = "1", After = "0", OpIndex = 2 });
            p.Created.AddRange(new long[] { 600, 601 });
            p.CreatedByOp[0] = new List<long> { 600, 601 };
            store.Add(p);
            var ev = new GroupAbort.Evidence
            {
                Deleted = new long[] { 77, 601 }, Changed = 1, DeletedCount = 2,
                Skipped = new Dictionary<string, List<long>> { ["no editable"] = new List<long> { 88 } }, Warnings = new[] { "w" },
            };
            Assert.Equal(expected, Abort(p, rollBack, ev, out _));
            store.Finish(p, Status.Desconocido, "incierto");

            var again = new PlanStore(_root);
            again.LoadPersisted(p.DocKey);
            var r = again.Get("P-M").Reconciliation;
            Assert.Equal(new object[] { 600L, 601L }, ((List<object>)r["ids_creados"]).ToArray());
            Assert.Equal(new object[] { 77L, 601L }, ((List<object>)r["ids_borrados"]).ToArray());
            Assert.Equal(2, ((List<object>)((Dictionary<string, object>)r["creados_por_op"])["@0"]).Count);
            Assert.Equal(new object[] { 88L }, ((List<object>)((Dictionary<string, object>)r["omitidos"])["no editable"]).ToArray());
            Assert.Equal(3, ((List<object>)r["entradas"]).Count);
            Assert.Equal(2L, r["lotes_confirmados"]);
            Assert.Equal(3L, r["lotes_totales"]);
            Assert.Equal(new object[] { "w" }, ((List<object>)r["avisos"]).ToArray());
        }

        [Fact]
        public void Abort_ConfirmedRollback_LeavesNoEvidence()
        {
            var p = Sample();
            Assert.Equal(GroupAbort.RolledBack, Abort(p, () => GroupAbort.RolledBack, Ev(), out _));
            Assert.Null(p.Reconciliation);
            Assert.Empty(p.Created);
        }

        [Theory]
        [InlineData(PlanStatus.Aplicado, PlanStatus.Aplicado)]
        [InlineData(PlanStatus.Revertido, PlanStatus.Revertido)]
        [InlineData(PlanStatus.Ensayado, PlanStatus.Ensayado)]
        [InlineData(PlanStatus.EnProceso, Status.Desconocido)]
        [InlineData(null, Status.Desconocido)]
        public void DisposeError_NeverUpgradesAResult(string before, string after) =>
            Assert.Equal(after, GroupAbort.StatusAfterDisposeError(before));

        [Fact]
        public void MixedPlan_CreationDeletedLater_IsNotReportedMissing()
        {
            Assert.Equal(2, GroupAbort.ExpectedSurvivingCreations(3, new long[] { 1, 2, 3 }, new long[] { 2, 50 }));
            Assert.True(TrialGuard.FunctionalSuccess(0, false, false, 2, 2, false));
            Assert.False(TrialGuard.FunctionalSuccess(0, false, false, 2, 1, false));
        }

        [Fact]
        public void Adapter_RoutesEveryOutcomeThroughTheTestedDecision()
        {
            // La parte con TransactionGroup real sólo se puede ejercer en Revit (NATIVE_ACCEPTANCE_PENDING); aquí se fija que el adaptador delega.
            var s = File.ReadAllText(TestPaths.Addin("Revit/Writes/PlanApplier.cs"));
            Assert.Contains("GroupAbort.Attempt(group.HasStarted", s);
            Assert.Contains("try { group.Dispose(); }", s);
            Assert.DoesNotContain("using (var group = new TransactionGroup", s);
            var h = File.ReadAllText(TestPaths.Addin("Revit/RequestHandler.cs"));
            Assert.Contains("applyEntered = true;", h);
            Assert.Contains("GroupAbort.PreserveInterrupted(plan, detail);", h);
        }
    }

    internal static class TestPaths
    {
        public static string Addin(string rel)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PDI.RevitMcp.sln"))) d = d.Parent;
            return Path.Combine(d.FullName, "src", "PDI.RevitMcp.Addin", rel.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    public class InstallRollbackTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-rollback-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly SetupPaths _p;

        public InstallRollbackTests()
        {
            _p = new SetupPaths
            {
                AppData = Path.Combine(_root, "Roaming"), LocalAppData = Path.Combine(_root, "Local"),
                UserProfile = Path.Combine(_root, "User"), ProgramFiles = Path.Combine(_root, "PF"), Desktop = Path.Combine(_root, "Desk"),
            };
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private void OldInstall()
        {
            Put(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp.addin"), "viejo-addin");
            Put(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll"), "viejo-dll");
            Put(Path.Combine(_p.AddinsDir("2024"), "pyRevit.addin"), "ajeno");
            Put(_p.BridgeExe, "viejo-puente");
            Put(_p.ConfigFile, "{\"levels\":\"R\"}");
            Put(_p.InstallRecord, "{\"version\":\"viejo\"}");
            Put(_p.CodexConfig, "[otro]\nx=1\n");
        }

        private void NewInstall()
        {
            Put(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp.addin"), "nuevo-addin");
            Put(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll"), "nuevo-dll");
            Put(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "extra-nuevo.dll"), "x");
            Put(_p.BridgeExe, "nuevo-puente");
            Put(Path.Combine(_p.BinDir, "power_engine", "w.exe"), "worker-nuevo");
            Put(_p.ConfigFile, "{\"levels\":\"RPW\"}");
            Put(_p.InstallRecord, "{\"version\":\"nuevo\"}");
            Put(_p.SetupCopy, "setup-nuevo");
            Put(_p.CodexConfig, "[otro]\nx=1\n[mcp]\n");
        }

        [Fact]
        public void Restore_ReturnsFilesConfigAndRecord_RemovesNewOnes_KeepsForeignAddins()
        {
            OldInstall();
            var dir = InstallBackup.Create(_p, new[] { "2024" }, "prueba", DateTime.UtcNow);
            NewInstall();
            var undo = InstallBackup.Restore(_p, dir, DateTime.UtcNow.AddSeconds(1));

            Assert.Equal("viejo-addin", File.ReadAllText(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp.addin")));
            Assert.Equal("viejo-dll", File.ReadAllText(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll")));
            Assert.False(File.Exists(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "extra-nuevo.dll")));
            Assert.False(Directory.Exists(Path.Combine(_p.BinDir, "power_engine")));
            Assert.Equal("viejo-puente", File.ReadAllText(_p.BridgeExe));
            Assert.Equal("{\"levels\":\"R\"}", File.ReadAllText(_p.ConfigFile));
            Assert.Equal("{\"version\":\"viejo\"}", File.ReadAllText(_p.InstallRecord));
            Assert.False(File.Exists(_p.SetupCopy));
            // Decisión de esta revisión: config.toml es del usuario. El retorno sólo devuelve la sección de BIMBRIDGE,
            // así que [mcp], que NewInstall escribe fuera de ella, se conserva. CodexRestore_* fija el caso de la sección propia.
            Assert.Equal("[otro]\nx=1\n[mcp]\n", File.ReadAllText(_p.CodexConfig));
            Assert.Equal("ajeno", File.ReadAllText(Path.Combine(_p.AddinsDir("2024"), "pyRevit.addin")));

            // El retorno también se puede deshacer.
            InstallBackup.Restore(_p, undo, DateTime.UtcNow.AddSeconds(2));
            Assert.Equal("nuevo-puente", File.ReadAllText(_p.BridgeExe));
            Assert.True(File.Exists(Path.Combine(_p.BinDir, "power_engine", "w.exe")));
        }

        [Fact]
        public void Restore_FromNoPreviousInstall_RemovesEverythingOurs()
        {
            var dir = InstallBackup.Create(_p, new[] { "2024" }, "limpio", DateTime.UtcNow);
            NewInstall();
            InstallBackup.Restore(_p, dir, DateTime.UtcNow.AddSeconds(1));
            Assert.False(File.Exists(_p.InstallRecord));
            Assert.False(Directory.Exists(_p.BinDir));
            Assert.False(Directory.Exists(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp")));
        }

        [Fact]
        public void TamperedBackup_IsRejected_BeforeTouchingAnything()
        {
            OldInstall();
            var dir = InstallBackup.Create(_p, new[] { "2024" }, "prueba", DateTime.UtcNow);
            NewInstall();
            File.AppendAllText(Path.Combine(dir, "archivos", "bin", "PDI.RevitMcp.Bridge.exe"), "alterado");
            Assert.Throws<InvalidDataException>(() => InstallBackup.Restore(_p, dir, DateTime.UtcNow));
            Assert.Equal("nuevo-puente", File.ReadAllText(_p.BridgeExe));
        }

        [Fact]
        public void FailedRestore_UndoesToPriorState()
        {
            OldInstall();
            var dir = InstallBackup.Create(_p, new[] { "2024" }, "prueba", DateTime.UtcNow);
            NewInstall();
            var locked = Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll");
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => InstallBackup.Restore(_p, dir, DateTime.UtcNow));
            Assert.Equal("nuevo-dll", File.ReadAllText(locked));
        }

        [Fact]
        public void Prune_KeepsOnlyTheLatest()
        {
            for (var i = 0; i < 5; i++) InstallBackup.Create(_p, new[] { "2024" }, "n", DateTime.UtcNow.AddMinutes(i));
            InstallBackup.Prune(_p);
            Assert.Equal(InstallBackup.Keep, Directory.GetDirectories(InstallBackup.Root(_p)).Length);
            Assert.NotNull(InstallBackup.Latest(_p));
        }
    }
}
