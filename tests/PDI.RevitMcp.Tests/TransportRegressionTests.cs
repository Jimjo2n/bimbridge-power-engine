// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using PDI.RevitMcp.Bridge;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    // Regression coverage for transport errors, identity and reconciliation.
    public class TransportRegressionTests
    {
        private static OperationJournal Journal() => new OperationJournal(Path.Combine(
            Path.GetTempPath(), "bimbridge-regression-" + Guid.NewGuid().ToString("N"), "operations.jsonl"));

        [Theory]
        [InlineData("escribir")]
        [InlineData(" ejecutar ")]
        public void AcceptedWriteModeMustNeverBeClassifiedAsRead(string mode)
        {
            Assert.True(RevitConnection.IsWriteOp(Wire.OpExec,
                new Dictionary<string, object> { ["modo"] = mode }));
        }

        [Fact]
        public void InProgressResponseMustKeepOperationRunning()
        {
            var journal = Journal();
            journal.Begin("key", "document", Wire.OpExec, "hash", "task", "session");
            // WithJournal currently does this when AwaitJob returns before job completion.
            journal.Complete("key", "document", Status.EnProceso, null);
            Assert.Equal(OperationJournal.BeginKind.Running,
                journal.Begin("key", "document", Wire.OpExec, "hash", "task", "session2").Kind);
        }

        [Fact]
        public void ReconcileMustNotAcceptDifferentContentAsAlreadyApplied()
        {
            var journal = Journal();
            journal.Begin("key", "document", Wire.OpExec, "hash-A", null, "session");
            journal.Complete("key", "document", Status.Ok, null);
            Assert.Equal(OperationJournal.BeginKind.Mismatch,
                journal.Begin("key", "document", Wire.OpExec, "hash-B", null, "session", reconcile: true).Kind);
        }

        [Fact]
        public void PartialCommitErrorMustRequireReconciliationBeforeRetry()
        {
            var journal = Journal();
            journal.Begin("key", "document", Wire.OpExec, "hash", null, "session");
            // tx=ninguna can commit transaction 1 and fail transaction 2; handler records Error.
            journal.Complete("key", "document", Status.Error, new Dictionary<string, object> { ["tx"] = "ninguna" });
            Assert.Equal(OperationJournal.BeginKind.Interrupted,
                journal.Begin("key", "document", Wire.OpExec, "hash", null, "session2").Kind);
        }

        [Fact]
        public void WriteAttemptMetadataRoundTripsWithoutChangingOperationHash()
        {
            var request = new Request { Op = Wire.OpCommit, AttemptId = Guid.NewGuid().ToString("N") };
            request.Args["plan_id"] = "P-1";
            var read = Request.FromJson(Json.Write(request));
            Assert.Equal(request.AttemptId, read.AttemptId);
            Assert.False(read.Args.ContainsKey("intento_id"));
            Assert.Equal(OperationJournal.HashArgs(request.Op, request.Args),
                OperationJournal.HashArgs(read.Op, read.Args));
            Assert.Null(Request.FromJson("{\"id\":1,\"op\":\"commit\",\"args\":{}}").AttemptId);
        }

        [Fact]
        public void KeyedReceipt_SurvivesRestart_AndChecksDocumentAndOwner()
        {
            var journal = Journal();
            var attempt = Guid.NewGuid().ToString("N");
            journal.Begin("key", "doc", Wire.OpCommit, "hash", "tarea", "S1", OperationJournal.ReconcileMode.None,
                "model", "opening", attempt, "agent1", "P-1");
            Assert.Equal(Status.EnProceso, journal.FindReceipt(attempt, "S1", null, "doc", "model")["estado"]);
            var restarted = new OperationJournal(journal.Path_);
            Assert.Equal(Status.Desconocido, restarted.FindReceipt(attempt, "S1", null, "doc", "model")["estado"]);
            journal.LinkJob("key", "doc", "J-1");
            journal.Complete("key", "doc", Status.ConDiferencias, null);
            var receipt = restarted.FindReceipt(attempt, "S2", "agent1", "doc", "model");
            Assert.Equal(Status.ConDiferencias, receipt["estado"]);
            Assert.Equal("P-1", receipt["plan_id"]);
            Assert.Equal("J-1", receipt["job_id"]);
            Assert.Null(restarted.FindReceipt(attempt, "S2", "agent2", "doc", "model"));
            Assert.Null(restarted.FindReceipt(attempt, "S1", null, "doc", "another-model"));
            Assert.Null(restarted.FindReceipt(attempt, "S1", null, "another-doc", "model"));
        }

        [Fact]
        public void CommitReceipt_UsesAuditOutcomeAndPreservesReportedDifferences()
        {
            var path = Path.Combine(Path.GetTempPath(), "bimbridge-audit-" + Guid.NewGuid().ToString("N"), "audit.jsonl");
            var audit = new AuditLog(path);
            var attempt = Guid.NewGuid().ToString("N");
            var intent = audit.Intent("session1", "commit", "document", new Dictionary<string, object>
            {
                ["intento_id"] = attempt, ["plan_id"] = "P-1", ["huella_doc"] = "model", ["agente"] = "agent",
            });
            Assert.Equal(Status.Desconocido, AuditLog.FindCommitReceipt(path, attempt, "session1", null, "document", "model")["estado"]);
            audit.Outcome("session1", "commit", intent, PlanStatus.Aplicado,
                new Dictionary<string, object> { ["estado_recibo"] = Status.ConDiferencias });
            var receipt = AuditLog.FindCommitReceipt(path, attempt, "session2", "agent", "document", "model");
            Assert.Equal("P-1", receipt["plan_id"]);
            Assert.Equal(Status.ConDiferencias, receipt["estado"]);
            Assert.Null(AuditLog.FindCommitReceipt(path, attempt, "session2", null, "document", "model"));
            Assert.Null(AuditLog.FindCommitReceipt(path, attempt, "session1", null, "document", "replaced"));
        }

        [Fact]
        public void FrameBudget_PreservesReceiptAndCursor_AndStoresCompleteMultibyteRow()
        {
            var response = new Response
            {
                Id = 7, Rev = 12, Estado = Status.ConDiferencias, Aviso = "Se aplicó con diferencias",
                Datos = new Dictionary<string, object>
                {
                    ["rows"] = new List<object> { new string('é', 600000) + "🚧" },
                    ["cursor"] = "cursor-1", ["recibo"] = new Dictionary<string, object> { ["plan_id"] = "P-1" },
                },
            };
            var expected = Json.Write(response);
            string detail = null;
            var summary = Response.FromJson(PipeHost.FitResponse(response, true, text => { detail = text; return "r123"; }));
            Assert.Equal(response.Estado, summary.Estado);
            Assert.Equal(response.Rev, summary.Rev);
            Assert.Equal(response.Aviso, summary.Aviso);
            var data = Assert.IsType<Dictionary<string, object>>(summary.Datos);
            Assert.Equal("cursor-1", data["cursor"]);
            Assert.True(data.ContainsKey("recibo"));
            Assert.Equal("r123", data["res"]);
            Assert.Equal(expected, detail.Replace("\n", ""));
            var failed = Response.FromJson(PipeHost.FitResponse(response, true, _ => throw new IOException("sin espacio")));
            Assert.Equal(response.Estado, failed.Estado);
            Assert.Contains("sin espacio", (string)Assert.IsType<Dictionary<string, object>>(failed.Datos)["aviso_resultado"]);
            Assert.Equal(expected, PipeHost.FitResponse(response, false, _ => throw new InvalidOperationException()));
        }
    }
}
