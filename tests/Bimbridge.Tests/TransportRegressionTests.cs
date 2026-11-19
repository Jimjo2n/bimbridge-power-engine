// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Bridge;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    // Regression coverage for transport errors, identity and reconciliation.
    public class TransportRegressionTests
    {
        private static OperationJournal Journal() => new OperationJournal(Path.Combine(
            Path.GetTempPath(), "bimbridge-regression-" + Guid.NewGuid().ToString("N"), "operations.jsonl"));

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task SentWrite_RemainsUnknownAfterDisconnectOrTimeout(bool supportsReceipt, bool timeout)
        {
            var name = "bimbridge-receipt-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var bridge = new RevitConnection { FixedTarget = (name, null) };
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<Request> Peer()
            {
                await server.WaitForConnectionAsync(deadline.Token);
                var hello = Request.FromJson(await Frame.ReadAsync(server, deadline.Token));
                await Frame.WriteAsync(server, Json.Write(new Response
                {
                    Id = hello.Id, Datos = new Dictionary<string, object> { [Wire.WriteReceiptFeature] = supportsReceipt },
                }), deadline.Token);
                var request = Request.FromJson(await Frame.ReadAsync(server, deadline.Token));
                if (timeout)
                {
                    await release.Task.WaitAsync(deadline.Token);
                    await Frame.WriteAsync(server, Json.Write(new Response { Id = request.Id }), deadline.Token);
                }
                else server.Disconnect();
                return request;
            }
            var peer = Peer();
            var call = await bridge.CallAsync(Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = "P-1" },
                TimeSpan.FromMilliseconds(timeout ? 250 : 3000), deadline.Token);
            release.TrySetResult(true);
            var sent = await peer;
            Assert.Equal(Status.Desconocido, call.Estado);
            if (supportsReceipt)
            {
                var data = Assert.IsType<Dictionary<string, object>>(call.Datos);
                var receipt = Assert.IsType<Dictionary<string, object>>(data["recibo"]);
                Assert.Equal(sent.AttemptId, receipt["intento_id"]);
                Assert.True(Guid.TryParseExact(sent.AttemptId, "N", out _));
                Assert.Equal(true, receipt["envio_confirmado"]);
            }
            else Assert.Null(sent.AttemptId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SourceBoundWrite_ReconnectsToOriginalReceiptAndRetryUsesTheSameOutcome(bool queued)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            const string doc = "documento-prueba";
            const string model = "huella-modelo-prueba";
            const string agent = "agente-de-prueba";
            const string plan = "P-1";
            var source = SourceBinding.Resolve(new List<object>
            {
                new Dictionary<string, object>
                {
                    ["op"] = "place", [SourceBinding.OpField] = SourceBinding.Token("pdf", "fixture", "sha", "1", "registro", "lote", 1),
                },
            });
            var journal = Journal();
            var pipe = "bimbridge-resume-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var bridge = new RevitConnection { FixedTarget = (pipe, null) };
            var mutations = 0;
            async Task<Request> Read() => Request.FromJson(await Frame.ReadAsync(server, deadline.Token));
            Task Reply(Request request, object data) => Frame.WriteAsync(server, Json.Write(new Response { Id = request.Id, Datos = data }), deadline.Token);
            async Task Accept()
            {
                await server.WaitForConnectionAsync(deadline.Token);
                var hello = await Read();
                Assert.Equal(Wire.OpHello, hello.Op);
                await Reply(hello, new Dictionary<string, object> { [Wire.WriteReceiptFeature] = true });
            }
            async Task Peer()
            {
                await Accept();
                var sent = await Read();
                Assert.Equal(Wire.OpCommit, sent.Op);
                Assert.Equal(source.Key, sent.Args[Wire.KeyArg]);
                Assert.Equal(OperationJournal.BeginKind.New, journal.Begin(source.Key, doc, Wire.OpCommit, source.Hash, null,
                    "primera", OperationJournal.ReconcileMode.None, model, "generation", sent.AttemptId, agent, plan).Kind);
                if (queued) journal.LinkJob(source.Key, doc, "cola-1");
                mutations++;
                journal.Complete(source.Key, doc, Status.Ok, new Dictionary<string, object> { ["id"] = 771L });
                server.Disconnect();

                await Accept();
                var query = await Read();
                Assert.Equal(Wire.OpJob, query.Op);
                var reopened = new OperationJournal(journal.Path_);
                var receipt = reopened.FindReceipt(Arg.GetString(query.Args, "intento_id"), "segunda", agent, doc, model);
                await Reply(query, receipt);
                var retry = await Read();
                Assert.Equal(Wire.OpCommit, retry.Op);
                Assert.Equal(source.Key, retry.Args[Wire.KeyArg]);
                var replay = reopened.Begin(source.Key, doc, Wire.OpCommit, source.Hash, null, "segunda");
                Assert.Equal(OperationJournal.BeginKind.Completed, replay.Kind);
                await Reply(retry, new Dictionary<string, object> { ["repeticion"] = true });
            }
            var peer = Peer();
            var commit = new Dictionary<string, object> { ["plan_id"] = plan, [Wire.KeyArg] = source.Key };
            var lost = await bridge.CallAsync(Wire.OpCommit, commit, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(Status.Desconocido, lost.Estado);
            var data = Assert.IsType<Dictionary<string, object>>(lost.Datos);
            var intent = Assert.IsType<Dictionary<string, object>>(data["recibo"])["intento_id"];
            var recovered = await bridge.CallAsync(Wire.OpJob, new Dictionary<string, object> { ["intento_id"] = intent },
                TimeSpan.FromSeconds(5), deadline.Token);
            var saved = Assert.IsType<Dictionary<string, object>>(recovered.Datos);
            Assert.Equal(Status.Ok, saved["estado"]);
            Assert.Equal(intent, saved["intento_id"]);
            Assert.Equal(queued ? "cola-1" : null, saved["job_id"]);
            var replayed = await bridge.CallAsync(Wire.OpCommit, commit, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(true, Assert.IsType<Dictionary<string, object>>(replayed.Datos)["repeticion"]);
            await peer;
            Assert.Equal(1, mutations);
        }

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

        [Fact]
        public void LargePowerResultRetainsItsStatusAndCanBeReassembledInSmallReads()
        {
            var data = new Dictionary<string, object>
            {
                ["estado"] = "con_diferencias",
                ["detalle"] = string.Concat(System.Linq.Enumerable.Repeat("Plano 🚧\n", 2500)),
            };
            var expected = Json.Write(data);
            var summary = Json.ParseObject(ResultStore.RenderPowerResult(data, 1200));
            Assert.Equal(data["estado"], summary["estado"]);
            var id = (string)summary["res"];
            try
            {
                var detail = new System.Text.StringBuilder();
                for (var from = 0; ; from += 2)
                {
                    var page = ResultStore.Read(id, from, 2, out var total);
                    Assert.True(page.Length <= 8001);
                    detail.Append(page.Replace("\n", ""));
                    if (from + 2 >= total) break;
                }
                Assert.Equal(expected, detail.ToString());
            }
            finally { File.Delete(Path.Combine(ResultStore.Folder, id + ".txt")); }
        }
    }
}
