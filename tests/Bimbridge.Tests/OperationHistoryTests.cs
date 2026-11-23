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
    /// 0.9.1 (Block I): persistent operation history, filters, pagination and safe-retry advice.
    /// AUD-13: isolation between sessions and agents (others' entries only with explicit authorization). AUD-14: con_diferencias requires review.
    /// </summary>
    public class OperationHistoryTests
    {
        private static string NewPath() => Path.Combine(Path.GetTempPath(), "bb_hist_" + Guid.NewGuid().ToString("N"), "operations.jsonl");
        private static readonly OperationHistory.Scope All = new OperationHistory.Scope { SeeAll = true };
        private static OperationHistory.Scope Me(string session, string agent = null) => new OperationHistory.Scope { SessionId = session, AgentId = agent };

        private static Job Done(JobStore jobs, string session, string agent, string kind, string failStatus = null, string error = null)
        {
            var j = jobs.Create(session, kind);
            j.AgentId = agent;
            if (failStatus == null) jobs.Complete(j, new Dictionary<string, object>()); else jobs.Fail(j, failStatus, error);
            return j;
        }

        [Fact]
        public void EveryFinishedJob_IsRecorded_Persisted_AndFilterable()
        {
            var p = NewPath();
            var jobs = new JobStore();
            var h = new OperationHistory(p);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S1", null, "doc pdf");
            Done(jobs, "S1", null, "commit", Status.Conflict, "the model changed");
            var c = Done(jobs, "S2", null, "commit", Status.Unknown, "connection dropped");
            Done(jobs, "S2", null, "check", JobStatus.Cancelled, "cancelled");
            Assert.Equal(4, h.Count);
            var (commits, n) = h.Query(All, null, "commit", null, null, null);
            Assert.Equal(2, n);
            Assert.Equal(c.Id, commits[0].JobId);                           // most recent first
            Assert.Equal("reconcile_first", commits[0].Retry);           // uncertain: never retry blindly
            Assert.Equal("retryable", commits[1].Retry);                // conflict: nothing was applied
            Assert.Equal("reconcile_first", h.Query(All, Status.Cancelled, null, null, null, null).Page[0].Retry);
            Assert.Equal(2, h.Query(All, null, null, "S2", null, null).Total);
            Assert.Equal(1, h.Query(All, null, null, null, null, "dropped").Total);

            // Restart: reloaded from the file; a broken line is reported, not silently dropped.
            File.AppendAllText(p, "{roto\n");
            var again = new OperationHistory(p);
            Assert.Equal(4, again.Count);
            Assert.Single(again.LoadErrors);
            var (p1, total) = again.Query(All, null, null, null, null, null, page: 2, pageSize: 3);
            Assert.Equal(4, total);
            Assert.Single(p1);
        }

        [Fact]
        public void WithDifferences_RequiresReview_NotSuccessWithoutPendingActions()
        {
            // AUD-14
            Assert.Equal("review_differences", OperationHistory.RetryAdvice(Status.CompletedWithDifferences));
            Assert.Equal("not_needed", OperationHistory.RetryAdvice(Status.Ok));
            Assert.Equal("reconcile_first", OperationHistory.RetryAdvice("an_unknown_new_status"));
        }

        [Fact]
        public void Sessions_OnlySeeTheirOwnOperations_UnlessExplicitlyAuthorized()
        {
            // AUD-13: two agents with concurrent sessions.
            var jobs = new JobStore();
            var h = new OperationHistory(null);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S-ana-1", "A-ana", "commit");
            Done(jobs, "S-ana-1", "A-ana", "doc pdf");
            Done(jobs, "S-bob-1", "A-bob", "commit", Status.Unknown, "secreto de bob");
            Done(jobs, "S-anon-1", null, "check");

            var ana = h.Query(Me("S-ana-1", "A-ana"), null, null, null, null, null);
            Assert.Equal(2, ana.Total);
            Assert.All(ana.Page, r => Assert.Equal("A-ana", r.Agent));
            // Filters do not widen the scope: asking for someone else's session or text does not show it.
            Assert.Equal(0, h.Query(Me("S-ana-1", "A-ana"), null, null, "S-bob-1", null, null).Total);
            Assert.Equal(0, h.Query(Me("S-ana-1", "A-ana"), null, null, null, null, "secret").Total);
            Assert.Equal(1, h.Query(Me("S-bob-1", "A-bob"), null, null, null, null, null).Total);
            // An anonymous session only sees its own session; another anonymous session does not see the first (null is not a shared identity).
            Assert.Equal(1, h.Query(Me("S-anon-1"), null, null, null, null, null).Total);
            Assert.Equal(0, h.Query(Me("S-anon-2"), null, null, null, null, null).Total);
            // With no identity at all nothing is visible; the scope is mandatory.
            Assert.Equal(0, h.Query(new OperationHistory.Scope(), null, null, null, null, null).Total);
            Assert.Throws<ArgumentNullException>(() => h.Query(null, null, null, null, null, null));
            // Asking for "all" without user authorization is rejected (not granted just for asking, nor silently downgraded).
            Assert.Null(OperationHistory.ScopeFor("S-ana-1", "A-ana", wantAll: true, sharedAuthorized: false, out var refusal));
            Assert.Contains("shared_history", refusal);
            Assert.False(new EngineConfig().SharedHistory);                 // not authorized by default
            Assert.Equal(2, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", false, false, out _), null, null, null, null, null).Total);
            Assert.Equal(2, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", false, true, out _), null, null, null, null, null).Total);   // authorized but not requested: own entries
            // With explicit authorization (historial_compartido) and asking for it, everything is visible.
            Assert.Equal(4, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", true, true, out _), null, null, null, null, null).Total);
            // In progress: same rule.
            var running = jobs.Create("S-bob-1", "geo"); running.AgentId = "A-bob";
            Assert.Empty(jobs.ForOwner("S-ana-1", "A-ana").Where(j => !j.IsFinal));
            Assert.Single(jobs.ForOwner("S-bob-2", "A-bob").Where(j => !j.IsFinal));
            Assert.Empty(jobs.ForOwner("S-anon-2", null).Where(j => !j.IsFinal));
        }

        [Fact]
        public void Reconnection_SameNamedAgentKeepsItsHistory_AcrossSessionsAndRestart()
        {
            // AUD-13: on reconnect the session is a different one (unique ids per instance); a named agent keeps its own entries, also after a restart.
            var p = NewPath();
            var jobs = new JobStore();
            var h = new OperationHistory(p);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S-ana-1", "A-ana", "commit");
            Done(jobs, "S-bob-1", "A-bob", "commit");
            Assert.Equal(1, h.Query(Me("S-ana-2", "A-ana"), null, null, null, null, null).Total);
            var restarted = new OperationHistory(p);
            Assert.Equal(1, restarted.Query(Me("S-ana-3", "A-ana"), null, null, null, null, null).Total);
            Assert.Equal("S-ana-1", restarted.Query(Me("S-ana-3", "A-ana"), null, null, null, null, null).Page[0].Session);
            Assert.Equal(0, restarted.Query(Me("S-eve-1", "A-eve"), null, null, null, null, null).Total);
        }

        [Fact]
        public void ConcurrentSessions_WritingAndReading_StayIsolatedAndComplete()
        {
            var jobs = new JobStore();
            var h = new OperationHistory(NewPath());
            jobs.OnAnyFinal = j => h.Add(j);
            const int perAgent = 60;
            var leaks = 0;
            using (var start = new ManualResetEventSlim())
            {
                var writers = new[] { "ana", "bob", "eve" }.Select(name => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < perAgent; i++)
                    {
                        Done(jobs, "S-" + name, "A-" + name, "commit " + name);
                        var mine = h.Query(Me("S-" + name, "A-" + name), null, null, null, null, null, pageSize: 100);
                        if (mine.Page.Any(r => r.Agent != "A-" + name)) Interlocked.Increment(ref leaks);
                    }
                })).ToArray();
                start.Set();
                Task.WaitAll(writers);
            }
            Assert.Equal(0, leaks);
            foreach (var name in new[] { "ana", "bob", "eve" })
                Assert.Equal(perAgent, h.Query(Me("S-" + name, "A-" + name), null, null, null, null, null).Total);
            Assert.Equal(3 * perAgent, h.Query(All, null, null, null, null, null).Total);
        }

        [Fact]
        public async Task JobFinishingWithDifferences_AfterClientSawInProgress_IsNeverRecordedAsOk()
        {
            // AUD-18: the client received en_proceso; the job finishes later with verification differences.
            var jobs = new JobStore();
            var h = new OperationHistory(NewPath());
            jobs.OnAnyFinal = j => h.Add(j);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = jobs.Create("S1", "doc pdf");
            jobs.Run(job, async () =>
            {
                await release.Task;
                // Like DocOp: result with the list of differences, without passing a status.
                jobs.Complete(job, new Dictionary<string, object> { ["file"] = "x.pdf", ["differences"] = new List<object> { "the PDF has 3 pages and 4 sheets were requested" } });
            });
            Assert.Equal(Status.InProgress, job.State);                    // what the client saw at first
            Assert.Equal(0, h.Count);
            release.SetResult(true);
            await job.Work;
            Assert.Equal(Status.CompletedWithDifferences, job.State);               // deferred response
            Assert.Equal(Status.CompletedWithDifferences, jobs.Get(job.Id).State);  // later query of the job_id
            var rec = h.Query(All, null, null, null, null, null).Page.Single();
            Assert.Equal(Status.CompletedWithDifferences, rec.State);               // history
            Assert.Equal("review_differences", rec.Retry);                // requires review, not "not_needed"
            Assert.Equal(0, h.Query(All, Status.Ok, null, null, null, null).Total);

            // An explicit "ok" does not mask differences; other explicit statuses are respected; without differences it is still ok.
            var a = jobs.Create("S1", "doc ifc"); jobs.Complete(a, new Dictionary<string, object> { ["differences"] = new List<object> { "x" } }, Status.Ok);
            Assert.Equal(Status.CompletedWithDifferences, a.State);
            var b = jobs.Create("S1", "exec"); jobs.Complete(b, new Dictionary<string, object> { ["differences"] = new List<object> { "x" } }, "partial");
            Assert.Equal("partial", b.State);
            var c = jobs.Create("S1", "doc pdf"); jobs.Complete(c, new Dictionary<string, object> { ["differences"] = new List<object>() });
            Assert.Equal(Status.Ok, c.State);
        }

        [Fact]
        public async Task CancelledRunningJob_EndsCancelled_AndIsRecorded()
        {
            var jobs = new JobStore();
            var h = new OperationHistory(null);
            jobs.OnAnyFinal = j => h.Add(j);
            var j1 = jobs.Create("S1", "geo");
            jobs.Run(j1, async () => { await Task.Delay(5000, j1.Cts.Token); jobs.Complete(j1, null); });
            Assert.True(jobs.Cancel(j1));
            await j1.Work;
            Assert.Equal(JobStatus.Cancelled, j1.Status);
            Assert.Equal(1, h.Count);
            Assert.False(jobs.Cancel(j1));                                  // already finished: cancel does nothing
        }
    }
}
