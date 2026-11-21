// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// AUD-01 / AUD-01B: audit journal recovery after crashes at each point of the group commit, with batches of 1, 2, 16 and 64.
    /// Each "process" is an AuditLog instance on the same file; a simulated crash (<see cref="AuditCrashException"/>) abandons
    /// the instance without confirming or cleaning up anything, and the file lock is released as when a process dies.
    /// Checks: intact chain, no quarantine of intact data, no duplicates, no confirmation of anything not persisted.
    /// </summary>
    public class AuditRecoveryTests
    {
        private static string NewAudit()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimbridge-rec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Path.Combine(root, "a.jsonl");
        }

        private static FileStream HoldLock(string audit) => new FileStream(audit + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        private static int Pending(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.json").Length : 0;
        private static int Claims(string audit) => Directory.Exists(audit + ".pend") ? Directory.GetFiles(audit + ".pend", "*.reclamada").Length : 0;
        private static List<Dictionary<string, object>> Lines(string audit) =>
            File.ReadAllLines(audit).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();

        private static void WaitFor(Func<bool> cond, string what, int ms = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("did not happen: " + what); Thread.Sleep(5); }
        }

        /// <summary>Dedicated thread (not from the pool): dozens of blocking writers must not wait for the pool to grow.</summary>
        private static Task<T> OnThread<T>(Func<T> body)
        {
            var tcs = new TaskCompletionSource<T>();
            new Thread(() => { try { tcs.SetResult(body()); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true }.Start();
            return tcs.Task;
        }

        private sealed class Outcome { public string Author; public long Seq; public Exception Error; }

        /// <summary>
        /// n writers queued while the lock is held; when it is released, the first to take it leads the whole batch and "crashes" at
        /// <paramref name="point"/> (only once). The others carry on alone: they wait, take the lock and reconcile.
        /// </summary>
        private static List<Outcome> CrashDuringBatch(string p, int n, string point)
        {
            var fired = 0;
            Func<string, bool> crashOnce = pt => pt == point && Interlocked.CompareExchange(ref fired, 1, 0) == 0;
            var writers = Enumerable.Range(0, n).Select(_ => new AuditLog(p)
            {
                CrashPoint = crashOnce, LockWait = TimeSpan.FromSeconds(8), ClaimWait = TimeSpan.FromMilliseconds(400),
            }).ToList();
            var tasks = new List<Task<Outcome>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < n; i++)
                {
                    var w = writers[i]; var name = "w" + i;
                    tasks.Add(OnThread(() =>
                    {
                        try { return new Outcome { Author = name, Seq = w.Append(name, "op", "ok") }; }
                        catch (Exception ex) { return new Outcome { Author = name, Error = ex }; }
                    }));
                    var expected = i + 1;
                    WaitFor(() => Pending(p) == expected, "queue of " + expected);
                }
            }
            Task.WaitAll(tasks.Cast<Task>().ToArray(), 60000);
            Assert.Equal(1, fired);   // there was exactly one crash at the requested point
            return tasks.Select(t => t.Result).ToList();
        }

        private static void AssertIntegrity(string p, List<Outcome> outcomes)
        {
            // A new instance (restart) opens without quarantine and the chain is valid.
            var reopened = new AuditLog(p);
            Assert.Empty(reopened.Quarantined);
            reopened.Append("r", "tras_recuperar", "ok");
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(0, v.HeadLag);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p), "*.cuarentena-*"));
            var lines = Lines(p);
            foreach (var o in outcomes)
            {
                var mine = lines.Where(l => Arg.GetString(l, "autor") == o.Author).ToList();
                Assert.True(mine.Count <= 1, "duplicate of " + o.Author);                    // never twice
                if (o.Error == null)
                {
                    Assert.Single(mine);                                                       // confirmed ⇒ persisted
                    Assert.Equal(o.Seq, Convert.ToInt64(mine[0]["seq"]));                     // with ITS seq
                }
            }
            var seqs = lines.Select(l => Convert.ToInt64(l["seq"])).ToList();
            Assert.Equal(Enumerable.Range(1, seqs.Count).Select(x => (long)x), seqs);       // sequence without gaps
            Assert.Equal(0, Claims(p));                                                       // no unresolved claim
        }

        [Fact]
        public void SingleEntry_CrashAfterFsyncBeforeHead_IsToleratedWithoutQuarantine()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var a = new AuditLog(p) { CrashPoint = pt => pt == "tras_fsync_antes_cabeza" };
            Assert.Throws<AuditCrashException>(() => a.Append("a", "op", "ok"));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(1, v.HeadLag);                                                      // persisted, not confirmed
            AssertIntegrity(p, new List<Outcome>());
        }

        [Theory]
        [InlineData(2)]
        [InlineData(16)]
        [InlineData(64)]
        public void Batch_CrashAfterFsyncBeforeHead_IsSealedAndConfirmedToSurvivors(int n)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, n, "tras_fsync_antes_cabeza");
            Assert.Single(outcomes, o => o.Error is AuditCrashException);                    // the leader "died"
            // The survivors get their seq: their line is intact and durable (sealed by the recovery record).
            Assert.All(outcomes.Where(o => !(o.Error is AuditCrashException)), o => Assert.Null(o.Error));
            Assert.Contains(Lines(p), l => Arg.GetString(l, "op") == "recuperacion_reclamos");
            AssertIntegrity(p, outcomes);
            Assert.Equal(n, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));   // the n entries, once each
        }

        [Fact]
        public void Batch_HeadLagOfCompleteBatch_VerifiesOk_AndIsSealedOnReopen()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            // Complete batch persisted without a head and with nobody left alive to reconcile: it verifies as intact and is sealed on reopen.
            var fired = 0;
            var writers = Enumerable.Range(0, 16).Select(_ => new AuditLog(p)
            {
                // After the leader crashes, any survivor that takes the lock dies before reconciling (deterministic).
                CrashPoint = pt => pt == "tras_fsync_antes_cabeza" ? Interlocked.Exchange(ref fired, 1) == 0 : (pt == "antes_bloqueo" || pt == "inicio_lider") && Volatile.Read(ref fired) == 1,
                LockWait = TimeSpan.FromSeconds(8), ClaimWait = TimeSpan.FromMilliseconds(200),
            }).ToList();
            var tasks = new List<Task<object>>();
            using (HoldLock(p))
            {
                for (var i = 0; i < 16; i++)
                {
                    var w = writers[i]; var name = "w" + i;
                    tasks.Add(OnThread<object>(() => { try { w.Append(name, "op", "ok"); } catch (Exception) { } return null; }));
                    var expected = i + 1;
                    WaitFor(() => Pending(p) == expected, "queue of " + expected);
                }
            }
            Task.WaitAll(tasks.ToArray(), 60000);   // all of them "die" before taking the lock again
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Equal(16, v.HeadLag);
            var reopened = new AuditLog(p);
            Assert.Empty(reopened.Quarantined);
            Assert.Contains(Lines(p), l => Arg.GetString(l, "op") == "recuperacion_cabeza");
            Assert.Equal(0, AuditLog.Verify(p).HeadLag);
        }

        [Theory]
        [InlineData("tras_reclamo")]
        [InlineData("antes_escribir")]
        public void Batch_CrashBeforeWriting_NothingConfirmed_NothingRewritten(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            Assert.All(outcomes, o => Assert.NotNull(o.Error));                              // nobody confirmed: nothing was written
            Assert.All(outcomes.Where(o => o.Error is JournalUnavailableException), o => Assert.Contains("no", o.Error.Message));
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor")?.StartsWith("w") == true);
            AssertIntegrity(p, outcomes);
        }

        [Fact]
        public void Batch_CrashDuringWrite_TornTailSetAside_NoFalseConfirmation()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, "durante_escritura");
            Assert.All(outcomes, o => Assert.NotNull(o.Error));                              // the batch was left incomplete: nobody confirmed
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor")?.StartsWith("w") == true);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(p), "*.fragmento-*")); // what was set aside is kept as evidence
            AssertIntegrity(p, outcomes);
        }

        [Theory]
        [InlineData("tras_escribir_antes_fsync")]
        [InlineData("tras_cabeza")]
        public void Batch_CrashAfterWrite_EntriesConfirmedOnceBySurvivors(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            Assert.All(outcomes.Where(o => !(o.Error is AuditCrashException)), o => Assert.Null(o.Error));
            AssertIntegrity(p, outcomes);
            Assert.Equal(16, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));
        }

        [Theory]
        [InlineData("antes_confirmar")]
        [InlineData("tras_confirmar_parcial")]
        public void Batch_CrashWhileConfirming_PersistedOnce_FailuresAreConservativeNotFalse(string point)
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var outcomes = CrashDuringBatch(p, 16, point);
            // Everything was persisted once. Whoever got no confirmation fails conservatively (reconcilable by its cid in the
            // journal); nobody gets a confirmation for something that is not written.
            Assert.Equal(16, Lines(p).Count(l => Arg.GetString(l, "autor")?.StartsWith("w") == true));
            AssertIntegrity(p, outcomes);
        }

        [Fact]
        public void CrashBeforeLock_NothingWritten_NothingLeftBehind()
        {
            var p = NewAudit();
            new AuditLog(p).Append("s", "genesis", "ok");
            var a = new AuditLog(p) { CrashPoint = pt => pt == "antes_bloqueo" };
            Assert.Throws<AuditCrashException>(() => a.Append("a", "op", "ok"));
            Assert.Equal(0, Pending(p));
            AssertIntegrity(p, new List<Outcome>());
            Assert.DoesNotContain(Lines(p), l => Arg.GetString(l, "autor") == "a");
        }

        [Fact]
        public void HeadLag_ThatIsNotACompleteBatch_IsStillDetectedAsTampering()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 4; i++) a.Append("a", "op" + i, "ok");
            // The head goes back two individual records (no batch explains it): tampering, not a crash.
            var lines = File.ReadAllLines(p).Where(l => l.Length > 0).ToList();
            Home.WriteAtomic(AuditLog.HeadPath(p), Json.Write(new Dictionary<string, object>
            {
                ["seq"] = 2L, ["ultimo"] = Home.Sha256Hex(lines[1]), ["longitud"] = 0L,
            }));
            var v = AuditLog.Verify(p);
            Assert.False(v.Ok);
            Assert.Contains(v.Errors, e => e.Contains("behind"));
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);                                                    // the tampering detection is kept
        }

        [Theory]
        [InlineData("reparacion_tras_fragmento_tmp")]
        [InlineData("reparacion_antes_truncar")]
        [InlineData("reparacion_tras_truncar")]
        public void TailRepair_CrashAtAnyStep_NeverLosesTheFragmentOrConfirmedRecords(string point)
        {
            // AUD-10: the set-aside fragment is durable before truncating; a crash at any step leaves the journal recoverable.
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 3; i++) a.Append("s", "op" + i, "ok");
            var b = new AuditLog(p) { CrashPoint = x => x == point };
            var torn = System.Text.Encoding.UTF8.GetBytes("{\"seq\":4,\"cortado");
            using (var f = new FileStream(p, FileMode.Append)) f.Write(torn, 0, torn.Length);
            Assert.Throws<AuditCrashException>(() => b.Append("s", "op3", "ok"));
            var frags = Directory.GetFiles(Path.GetDirectoryName(p), Path.GetFileName(p) + ".fragmento-*").Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)).ToList();
            if (point != "reparacion_tras_fragmento_tmp")
                Assert.Contains(frags, f => File.ReadAllBytes(f).SequenceEqual(torn));   // the fragment exists and is intact
            if (point != "reparacion_tras_truncar")
                Assert.EndsWith("cortado", File.ReadAllText(p));                     // without a confirmed fragment nothing is truncated
            var c = new AuditLog(p);                                                   // reopen: repairs and continues
            c.Append("s", "op3", "ok");
            Assert.Empty(c.Quarantined);
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors));
            Assert.Contains("\"op2\"", File.ReadAllText(p));                           // the confirmed ones are still there
        }

        [Fact]
        public void EditedConfirmedRecord_IsStillQuarantined()
        {
            var p = NewAudit();
            var a = new AuditLog(p);
            for (var i = 0; i < 3; i++) a.Append("s", "op" + i, "ok");
            File.WriteAllText(p, File.ReadAllText(p).Replace("\"op1\"", "\"opX\""));
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);
            Assert.Empty(b.Repaired);
        }
    }
}
