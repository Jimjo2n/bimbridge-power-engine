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
    }
}
