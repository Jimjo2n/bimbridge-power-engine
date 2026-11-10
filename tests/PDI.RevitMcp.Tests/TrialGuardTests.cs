// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.RegularExpressions;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>P0 de Codex (2026-10-28): ningún ensayo revierte un grupo sobre un documento con cambios sin guardar.</summary>
    public class TrialGuardTests
    {
        [Theory]
        [InlineData(true, true, "ensayado")]
        [InlineData(false, true, "revertido")]
        [InlineData(true, false, "desconocido")]
        [InlineData(false, false, "desconocido")]
        public void TrialStatusRequiresBothFunctionalSuccessAndRestoration(bool functional, bool restored, string expected)
        {
            Assert.Equal(expected, TrialGuard.TrialStatus(functional, restored));
        }

        [Fact]
        public void CreationOnlyRequiresObservedElementsAndExpectedOperations()
        {
            Assert.True(TrialGuard.FunctionalSuccess(0, false, false, 2, 2, false));
            Assert.False(TrialGuard.FunctionalSuccess(0, false, false, 2, 1, false));
            Assert.False(TrialGuard.FunctionalSuccess(0, false, false, 2, 2, true));
            Assert.False(TrialGuard.FunctionalSuccess(0, false, false, 0, 0, false));
            Assert.False(TrialGuard.FunctionalSuccess(1, true, false, 0, 0, false));
        }

        [Fact]
        public void AbortClearsEvidenceOnlyOnConfirmedRollback()
        {
            var source = Src("Writes/PlanApplier.cs");
            Assert.Contains("if (rolledBack) ResetCreated();", source);
            Assert.Contains("else GroupAbort.Preserve(_plan, state, error, Evidence());", source);
            Assert.Contains("if (restoration) ResetCreated();", source);
            Assert.Contains("else GroupAbort.Preserve(_plan, state, rollbackError ?? readError, Evidence());", source);
        }

        [Fact]
        public void VerificationRunsInsideGroupAndRollbackPrecedesReturn()
        {
            var changed = true;
            var observed = TrialGuard.VerifyAndRollback(() => changed, () => { changed = false; });
            Assert.True(observed);
            Assert.False(changed);
        }

        [Fact]
        public void VerificationFailureStillRollsBack()
        {
            var changed = true;
            Assert.Throws<System.InvalidOperationException>(() => TrialGuard.VerifyAndRollback<bool>(
                () => throw new System.InvalidOperationException(), () => { changed = false; }));
            Assert.False(changed);
        }

        [Fact]
        public void RollbackFailureCannotReturnVerificationAsSuccess()
        {
            Assert.Throws<System.InvalidOperationException>(() => TrialGuard.VerifyAndRollback(
                () => true, () => throw new System.InvalidOperationException()));
        }

        [Fact]
        public void Refuses_WhenModified_AllowsWhenClean()
        {
            Assert.StartsWith(TrialGuard.Code, TrialGuard.Refuse(true, "commit modo=ensayo"));
            Assert.Contains("No se modificó nada", TrialGuard.Refuse(true, "exec modo=ensayo"));
            Assert.Null(TrialGuard.Refuse(false, "commit modo=ensayo"));
            Assert.False(TrialGuard.MayOpenTemporaryGroup(true));
            Assert.True(TrialGuard.MayOpenTemporaryGroup(false));
        }

        private static string Src(string rel)
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d.FullName, "src", "PDI.RevitMcp.Addin", "Revit", rel));
        }

        // Las rutas del add-in no se pueden ejecutar sin Revit: se comprueba en el código que la guardia va ANTES del grupo,
        // dentro del hilo de Revit (RunMain/OnRevit), y que no se toca IsModified. La prueba nativa NRT-04N lo confirma en Revit.
        [Fact]
        public void CommitTrial_GuardRunsBeforePlanOpsTrial()
        {
            var s = Src("RequestHandler.cs");
            var g = s.IndexOf("TrialGuard.Refuse(doc.IsModified, \"commit modo=ensayo\")", System.StringComparison.Ordinal);
            var t = s.IndexOf("PlanOps.Trial(doc", System.StringComparison.Ordinal);
            var main = s.LastIndexOf("RunMain(", g, System.StringComparison.Ordinal);
            Assert.True(g > 0 && t > g && main > 0 && main < g);
        }

        [Fact]
        public void ExecTrial_GuardRunsBeforeTransactionGroup()
        {
            var s = Src("RequestHandler.Exec.cs");
            var g = s.IndexOf("TrialGuard.Refuse(doc.IsModified, \"exec modo=ensayo\")", System.StringComparison.Ordinal);
            var grp = s.IndexOf("group = new TransactionGroup(doc, txName)", System.StringComparison.Ordinal);
            Assert.True(g > 0 && grp > g);
        }

        [Fact]
        public void TemporaryGroups_CaptureAndTaskQc_AreSkippedWhenModified()
        {
            var c = Src("Capture.cs");
            Assert.True(c.IndexOf("MayOpenTemporaryGroup(doc.IsModified)", System.StringComparison.Ordinal) < c.IndexOf("new TransactionGroup(doc, DocumentTracker.TemporaryPrefix + \" captura\")", System.StringComparison.Ordinal));
            var t = Src("RequestHandler.Tasks.cs");
            var gi = t.IndexOf("MayOpenTemporaryGroup(doc.IsModified)", System.StringComparison.Ordinal);
            Assert.True(gi > 0 && gi < t.IndexOf("\" qc tarea\")", System.StringComparison.Ordinal));
        }

        [Fact]
        public void NoCodeWritesIsModified()
        {
            var root = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
            foreach (var f in Directory.GetFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
                Assert.DoesNotMatch(new Regex(@"^(?!\s*//).*\.IsModified\s*=[^=>]", RegexOptions.Multiline), File.ReadAllText(f));
        }
    }
}
