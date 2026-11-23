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
        [InlineData("attempt_id", "status", "data", "warning")]
        [InlineData("intento_id", "estado", "datos", "aviso")]   // legacy aliases, accepted for one version
        public void Frames_ReadEnglishKeysAndLegacyAliases(string attemptKey, string statusKey, string dataKey, string warningKey)
        {
            var req = Request.FromJson("{\"id\":7,\"op\":\"x\",\"args\":{},\"" + attemptKey + "\":\"A1\"}");
            Assert.Equal("A1", req.AttemptId);

            var resp = Response.FromJson("{\"id\":7,\"" + statusKey + "\":\"" + Status.Held + "\",\"" + dataKey + "\":{\"n\":1},\"" + warningKey + "\":\"w\"}");
            Assert.Equal(Status.Held, resp.State);
            Assert.Equal(1L, Assert.IsType<Dictionary<string, object>>(resp.Data)["n"]);
            Assert.Equal("w", resp.Warning);
        }

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
                    Id = hello.Id, Data = new Dictionary<string, object> { [Wire.WriteReceiptFeature] = supportsReceipt },
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
            Assert.Equal(Status.Unknown, call.State);
            if (supportsReceipt)
            {
                var data = Assert.IsType<Dictionary<string, object>>(call.Data);
                var receipt = Assert.IsType<Dictionary<string, object>>(data["receipt"]);
                Assert.Equal(sent.AttemptId, receipt["attempt_id"]);
                Assert.True(Guid.TryParseExact(sent.AttemptId, "N", out _));
                Assert.Equal(true, receipt["send_confirmed"]);
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
                    ["op"] = "place", [SourceBinding.OpField] = SourceBinding.Token("pdf", "fixture", "sha", "1", "record", "batch", 1),
                },
            });
            var journal = Journal();
            var pipe = "bimbridge-resume-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var bridge = new RevitConnection { FixedTarget = (pipe, null) };
            var mutations = 0;
            async Task<Request> Read() => Request.FromJson(await Frame.ReadAsync(server, deadline.Token));
            Task Reply(Request request, object data) => Frame.WriteAsync(server, Json.Write(new Response { Id = request.Id, Data = data }), deadline.Token);
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
                    "first", OperationJournal.ReconcileMode.None, model, "generation", sent.AttemptId, agent, plan).Kind);
                if (queued) journal.LinkJob(source.Key, doc, "cola-1");
                mutations++;
                journal.Complete(source.Key, doc, Status.Ok, new Dictionary<string, object> { ["id"] = 771L });
                server.Disconnect();

                await Accept();
                var query = await Read();
                Assert.Equal(Wire.OpJob, query.Op);
                var reopened = new OperationJournal(journal.Path_);
                var receipt = reopened.FindReceipt(Arg.GetString(query.Args, "attempt_id"), "second", agent, doc, model);
                await Reply(query, receipt);
                var retry = await Read();
                Assert.Equal(Wire.OpCommit, retry.Op);
                Assert.Equal(source.Key, retry.Args[Wire.KeyArg]);
                var replay = reopened.Begin(source.Key, doc, Wire.OpCommit, source.Hash, null, "second");
                Assert.Equal(OperationJournal.BeginKind.Completed, replay.Kind);
                await Reply(retry, new Dictionary<string, object> { ["repetition"] = true });
            }
            var peer = Peer();
            var commit = new Dictionary<string, object> { ["plan_id"] = plan, [Wire.KeyArg] = source.Key };
            var lost = await bridge.CallAsync(Wire.OpCommit, commit, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(Status.Unknown, lost.State);
            var data = Assert.IsType<Dictionary<string, object>>(lost.Data);
            var intent = Assert.IsType<Dictionary<string, object>>(data["receipt"])["attempt_id"];
            var recovered = await bridge.CallAsync(Wire.OpJob, new Dictionary<string, object> { ["attempt_id"] = intent },
                TimeSpan.FromSeconds(5), deadline.Token);
            var saved = Assert.IsType<Dictionary<string, object>>(recovered.Data);
            Assert.Equal(Status.Ok, saved["status"]);
            Assert.Equal(intent, saved["attempt_id"]);
            Assert.Equal(queued ? "cola-1" : null, saved["job_id"]);
            var replayed = await bridge.CallAsync(Wire.OpCommit, commit, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(true, Assert.IsType<Dictionary<string, object>>(replayed.Data)["repetition"]);
            await peer;
            Assert.Equal(1, mutations);
        }

        [Theory]
        [InlineData("write")]
        [InlineData(" ejecutar ")]
        public void AcceptedWriteModeMustNeverBeClassifiedAsRead(string mode)
        {
            Assert.True(RevitConnection.IsWriteOp(Wire.OpExec,
                new Dictionary<string, object> { ["mode"] = mode }));
        }

        [Fact]
        public void InProgressResponseMustKeepOperationRunning()
        {
            var journal = Journal();
            journal.Begin("key", "document", Wire.OpExec, "hash", "task", "session");
            // WithJournal currently does this when AwaitJob returns before job completion.
            journal.Complete("key", "document", Status.InProgress, null);
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
            journal.Complete("key", "document", Status.Error, new Dictionary<string, object> { ["tx"] = "none" });
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
            Assert.False(read.Args.ContainsKey("attempt_id"));
            Assert.Equal(OperationJournal.HashArgs(request.Op, request.Args),
                OperationJournal.HashArgs(read.Op, read.Args));
            Assert.Null(Request.FromJson("{\"id\":1,\"op\":\"commit\",\"args\":{}}").AttemptId);
        }

        [Fact]
        public void KeyedReceipt_SurvivesRestart_AndChecksDocumentAndOwner()
        {
            var journal = Journal();
            var attempt = Guid.NewGuid().ToString("N");
            journal.Begin("key", "doc", Wire.OpCommit, "hash", "task", "S1", OperationJournal.ReconcileMode.None,
                "model", "opening", attempt, "agent1", "P-1");
            Assert.Equal(Status.InProgress, journal.FindReceipt(attempt, "S1", null, "doc", "model")["status"]);
            var restarted = new OperationJournal(journal.Path_);
            Assert.Equal(Status.Unknown, restarted.FindReceipt(attempt, "S1", null, "doc", "model")["status"]);
            journal.LinkJob("key", "doc", "J-1");
            journal.Complete("key", "doc", Status.CompletedWithDifferences, null);
            var receipt = restarted.FindReceipt(attempt, "S2", "agent1", "doc", "model");
            Assert.Equal(Status.CompletedWithDifferences, receipt["status"]);
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
                ["attempt_id"] = attempt, ["plan_id"] = "P-1", ["doc_fingerprint"] = "model", ["agent"] = "agent",
            });
            Assert.Equal(Status.Unknown, AuditLog.FindCommitReceipt(path, attempt, "session1", null, "document", "model")["status"]);
            audit.Outcome("session1", "commit", intent, PlanStatus.Applied,
                new Dictionary<string, object> { ["receipt_status"] = Status.CompletedWithDifferences });
            var receipt = AuditLog.FindCommitReceipt(path, attempt, "session2", "agent", "document", "model");
            Assert.Equal("P-1", receipt["plan_id"]);
            Assert.Equal(Status.CompletedWithDifferences, receipt["status"]);
            Assert.Null(AuditLog.FindCommitReceipt(path, attempt, "session2", null, "document", "model"));
            Assert.Null(AuditLog.FindCommitReceipt(path, attempt, "session1", null, "document", "replaced"));
        }

        [Fact]
        public void FrameBudget_PreservesReceiptAndCursor_AndStoresCompleteMultibyteRow()
        {
            var response = new Response
            {
                Id = 7, Rev = 12, State = Status.CompletedWithDifferences, Warning = "Se aplicó con diferencias",
                Data = new Dictionary<string, object>
                {
                    ["rows"] = new List<object> { new string('é', 600000) + "🚧" },
                    ["cursor"] = "cursor-1", ["receipt"] = new Dictionary<string, object> { ["plan_id"] = "P-1" },
                },
            };
            var expected = Json.Write(response);
            string detail = null;
            var summary = Response.FromJson(PipeHost.FitResponse(response, true, text => { detail = text; return "r123"; }));
            Assert.Equal(response.State, summary.State);
            Assert.Equal(response.Rev, summary.Rev);
            Assert.Equal(response.Warning, summary.Warning);
            var data = Assert.IsType<Dictionary<string, object>>(summary.Data);
            Assert.Equal("cursor-1", data["cursor"]);
            Assert.True(data.ContainsKey("receipt"));
            Assert.Equal("r123", data["res"]);
            Assert.Equal(expected, detail.Replace("\n", ""));
            var failed = Response.FromJson(PipeHost.FitResponse(response, true, _ => throw new IOException("out of space")));
            Assert.Equal(response.State, failed.State);
            Assert.Contains("out of space", (string)Assert.IsType<Dictionary<string, object>>(failed.Data)["result_warning"]);
            Assert.Equal(expected, PipeHost.FitResponse(response, false, _ => throw new InvalidOperationException()));
        }

        [Fact]
        public void LargePowerResultRetainsItsStatusAndCanBeReassembledInSmallReads()
        {
            var data = new Dictionary<string, object>
            {
                ["status"] = "with_differences",
                ["detail"] = string.Concat(System.Linq.Enumerable.Repeat("Plano 🚧\n", 2500)),
            };
            var expected = Json.Write(data);
            var summary = Json.ParseObject(ResultStore.RenderPowerResult(data, 1200));
            Assert.Equal(data["status"], summary["status"]);
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
