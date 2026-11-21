// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Issue #7: jobs started without await are supervised; none stays en_proceso or loses its exception.</summary>
    public class JobSupervisionTests
    {
        private static Job Finish(JobStore store, Func<Job, Func<Task>> body)
        {
            var j = store.Create("s1", "test");
            store.Run(j, body(j));
            Assert.True(j.Work.Wait(TimeSpan.FromSeconds(10)), "the supervisor did not finish");
            return j;
        }

        [Fact]
        public void ThrowingBody_EndsUnknown_WithReason()
        {
            var store = new JobStore();
            var j = Finish(store, _ => async () => { await Task.Yield(); throw new InvalidOperationException("failure halfway"); });
            Assert.True(j.IsFinal);
            Assert.Equal(Status.Unknown, j.State);
            Assert.Contains("InvalidOperationException", j.Error);
            Assert.Contains("reconcile", j.Error);
        }

        [Fact]
        public void BodyWithoutResult_EndsUnknown()
        {
            var store = new JobStore();
            var j = Finish(store, _ => () => Task.CompletedTask);
            Assert.Equal(Status.Unknown, j.State);
        }

        [Fact]
        public void CanceledBody_EndsCanceled()
        {
            var store = new JobStore();
            var j = Finish(store, job => async () => { job.Cts.Cancel(); await Task.Delay(10, job.Cts.Token); });
            Assert.Equal(JobStatus.Cancelled, j.Status);
        }

        [Fact]
        public void CompletedBody_KeepsResult_AndFinalHandlerRunsOnce()
        {
            var store = new JobStore();
            var calls = 0;
            var j = store.Create("s1", "test");
            store.WhenFinal(j, _ => Interlocked.Increment(ref calls));
            store.Run(j, async () => { await Task.Yield(); store.Complete(j, new System.Collections.Generic.Dictionary<string, object> { ["n"] = 1L }); });
            Assert.True(j.Work.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(Status.Ok, j.State);
            Assert.Equal(1, calls);
        }
    }
}
