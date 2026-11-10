// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Issue #7: los trabajos lanzados sin await están supervisados; ninguno queda en_proceso ni pierde su excepción.</summary>
    public class JobSupervisionTests
    {
        private static Job Finish(JobStore store, Func<Job, Func<Task>> body)
        {
            var j = store.Create("s1", "prueba");
            store.Run(j, body(j));
            Assert.True(j.Work.Wait(TimeSpan.FromSeconds(10)), "el supervisor no terminó");
            return j;
        }

        [Fact]
        public void ThrowingBody_EndsUnknown_WithReason()
        {
            var store = new JobStore();
            var j = Finish(store, _ => async () => { await Task.Yield(); throw new InvalidOperationException("fallo a mitad"); });
            Assert.True(j.IsFinal);
            Assert.Equal(Status.Desconocido, j.Estado);
            Assert.Contains("InvalidOperationException", j.Error);
            Assert.Contains("reconcilie", j.Error);
        }

        [Fact]
        public void BodyWithoutResult_EndsUnknown()
        {
            var store = new JobStore();
            var j = Finish(store, _ => () => Task.CompletedTask);
            Assert.Equal(Status.Desconocido, j.Estado);
        }

        [Fact]
        public void CanceledBody_EndsCanceled()
        {
            var store = new JobStore();
            var j = Finish(store, job => async () => { job.Cts.Cancel(); await Task.Delay(10, job.Cts.Token); });
            Assert.Equal(JobStatus.Cancelado, j.Status);
        }

        [Fact]
        public void CompletedBody_KeepsResult_AndFinalHandlerRunsOnce()
        {
            var store = new JobStore();
            var calls = 0;
            var j = store.Create("s1", "prueba");
            store.WhenFinal(j, _ => Interlocked.Increment(ref calls));
            store.Run(j, async () => { await Task.Yield(); store.Complete(j, new System.Collections.Generic.Dictionary<string, object> { ["n"] = 1L }); });
            Assert.True(j.Work.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(Status.Ok, j.Estado);
            Assert.Equal(1, calls);
        }
    }
}
