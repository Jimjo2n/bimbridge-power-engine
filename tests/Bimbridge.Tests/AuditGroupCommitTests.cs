// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// Issue #27: AuditLog group commit across writers. Deterministic in-process tests: the test holds the real lock (auditoria.lock, FileShare.None)
    /// to fix the order; each AuditLog is an independent writer on the same file, like a Revit process.
    /// Guarantees checked: no confirmation before persisting, no loss, no duplicate, enqueue order, fail-closed.
    /// </summary>
    public class AuditGroupCommitTests
    {
        private static string NewAudit()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimbridge-gc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Path.Combine(root, "a.jsonl");
        }

        private static FileStream HoldLock(string audit) => new FileStream(audit + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        private static string[] Pending(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.json") : new string[0];

        private static void WaitFor(Func<bool> cond, string what, int ms = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("did not happen: " + what); Thread.Sleep(5); }
        }

        private static List<Dictionary<string, object>> Lines(string audit) =>
            File.ReadAllLines(audit).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();

        [Fact]
        public void Contended_EntryWaits_IsNotConfirmedBeforePersisted_ThenWrittenOnce()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p);
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entry queued");
                Thread.Sleep(150);
                Assert.False(t.IsCompleted);                 // no lock means no write: and no confirmation either
                Assert.Single(Lines(p));
            }
            Assert.Equal(2, t.Result);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(2, v.Lines);
            var last = Lines(p).Last();
            Assert.Equal("b", Arg.GetString(last, "author"));
            Assert.False(string.IsNullOrEmpty(Arg.GetString(last, "cid")));   // entry that went through the queue: identifiable
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void Leader_DrainsAllWaiting_InEnqueueOrder_NoLossNoDuplicates()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var writers = Enumerable.Range(1, 4).Select(_ => new AuditLog(p)).ToList();
            var tasks = new List<Task<long>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < writers.Count; i++)
                {
                    var w = writers[i]; var name = "w" + (i + 1);
                    tasks.Add(Task.Run(() => w.Append(name, "op", "ok")));
                    var expected = i + 1;
                    WaitFor(() => Pending(p).Length == expected, "queue of " + expected);   // enqueue order fixed by the test
                }
                Assert.All(tasks, x => Assert.False(x.IsCompleted));
            }
            Task.WaitAll(tasks.Cast<Task>().ToArray(), 20000);
            var seqs = tasks.Select(x => x.Result).ToList();
            Assert.Equal(new long[] { 2, 3, 4, 5 }, seqs.OrderBy(x => x).ToArray());
            Assert.Equal(seqs.Count, seqs.Distinct().Count());
            var lines = Lines(p);
            Assert.Equal(new[] { "s", "w1", "w2", "w3", "w4" }, lines.Select(l => Arg.GetString(l, "author")).ToArray());
            for (var i = 0; i < 4; i++) Assert.Equal(seqs[i], Convert.ToInt64(lines[i + 1]["seq"]));   // each writer gets ITS seq
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void Timeout_UnclaimedEntry_IsWithdrawn_NothingWritten_FailsClosed()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { LockWait = TimeSpan.FromMilliseconds(300) };
            using (HoldLock(p))
            {
                var ex = Assert.Throws<JournalUnavailableException>(() => b.Append("b", "op", "ok"));
                Assert.Contains("nothing was written", ex.Message);
            }
            Assert.Equal(1, b.Failures);
            Assert.Empty(Pending(p));                          // the withdrawn entry cannot be written by another leader later
            Assert.Single(Lines(p));
            Assert.Equal(2, b.Append("b", "op2", "ok"));       // the writer is still usable
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void ClaimedByCrashedLeader_FailsClosed_AndIsNeverRewritten()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { LockWait = TimeSpan.FromMilliseconds(400), ClaimWait = TimeSpan.FromMilliseconds(300) };
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "intent_x", "intent"));
                WaitFor(() => Pending(p).Length == 1, "entry queued");
                // A leader claims it (same primitive: exclusive open that deletes on close) and "crashes" before writing and confirming.
                using (new FileStream(Pending(p)[0], FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose)) { }
                var ex = Assert.Throws<AggregateException>(() => t.Wait(10000));
                Assert.IsType<JournalUnavailableException>(ex.InnerException);
                Assert.Contains("claimed", ex.InnerException.Message);
            }
            Assert.Single(Lines(p));                           // the follower does not rewrite it: no duplicates
            b.Append("b", "after", "ok");
            Assert.Equal(2, AuditLog.Verify(p).Lines);
        }

        [Fact]
        public void LeaderWriteFailure_GivesNoConfirmation_ChainStaysValid()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var b = new AuditLog(p) { FaultBeforeWrite = _ => throw new IOException("disk full") };
            Task<long> t;
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entry queued");
            }
            var ex = Assert.Throws<AggregateException>(() => t.Wait(10000));
            Assert.IsType<JournalUnavailableException>(ex.InnerException);
            Assert.Equal(1, b.Failures);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(1, v.Lines);
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void LeaderWriteFailure_PropagatesErrorToFollowers_NoFalseConfirmation()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var follower = new AuditLog(p);
            var leader = new AuditLog(p) { FaultBeforeWrite = _ => throw new IOException("disk full") };
            Task<long> tf, tl;
            using (HoldLock(p))
            {
                tl = Task.Run(() => leader.Append("leader", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "leader queued");
                tf = Task.Run(() => follower.Append("follower", "op", "ok"));
                WaitFor(() => Pending(p).Length == 2, "follower queued");
            }
            // Whoever takes the lock first writes the batch. If it is the faulty leader, both get an error; if it is the follower, it writes both
            // entries and the faulty leader only fails if it is the one writing. In no case is there a confirmation without a persisted line.
            var results = new[] { tl, tf }.Select(x => { try { return (ok: true, seq: x.Result); } catch (AggregateException) { return (ok: false, seq: 0L); } }).ToList();
            var lines = Lines(p);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            foreach (var r in results.Where(r => r.ok)) Assert.Contains(lines, l => Convert.ToInt64(l["seq"]) == r.seq);
            Assert.Equal(1 + results.Count(r => r.ok), lines.Count);   // each confirmation has exactly one line, and there are no lines without a confirmed or failed owner
            Assert.Empty(Pending(p));
        }

        [Fact]
        public void OrphanEntry_FromDeadWriter_IsPersistedByNextLeader_AndStaysReconcilable()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            // A writer left its entry in the queue and died before getting confirmation (Revit restart, process terminated).
            Directory.CreateDirectory(p + ".pend");
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(p + ".pend", DateTime.UtcNow.Ticks.ToString("D19") + "-" + id + ".json"), Json.Write(new Dictionary<string, object>
            {
                ["id"] = id, ["author"] = "dead", ["op"] = "exec", ["status"] = "intent", ["pid"] = 1L, ["t"] = "2026-11-10T00:00:00Z",
                ["extra"] = new Dictionary<string, object> { ["intent"] = "abc123", ["target"] = "doc" },
            }), new UTF8Encoding(false));
            var c = new AuditLog(p);
            Assert.Equal(3, c.Append("c", "op", "ok"));       // the next leader writes the orphan entry first, then its own
            var lines = Lines(p);
            Assert.Equal(new[] { "s", "dead", "c" }, lines.Select(l => Arg.GetString(l, "author")).ToArray());
            Assert.Equal(id, Arg.GetString(lines[1], "cid"));
            Assert.True(AuditLog.Verify(p).Ok);
            // The orphan intent has no result: it stays pending reconciliation, never as completed.
            Assert.Contains(AuditLog.PendingIntents(p), x => Arg.GetString(x, "intent") == "abc123");
        }

        [Fact]
        public void Restart_AfterQueuedWork_NewInstancesContinueTheSameChain()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 5; i++) a.Append("a", "op" + i, "ok");
            Task<long> t;
            var b = new AuditLog(p);                           // opening also takes the lock: it is created before holding it
            using (HoldLock(p))
            {
                t = Task.Run(() => b.Append("b", "op", "ok"));
                WaitFor(() => Pending(p).Length == 1, "entry queued");
                Assert.False(t.IsCompleted);
            }
            Assert.Equal(6, t.Result);
            var reopened = new AuditLog(p);                    // "restart": a new instance rereads the whole chain
            Assert.Equal(7, reopened.Append("r", "op", "ok"));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(7, v.Lines);
        }
    }
}
