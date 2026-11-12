// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.9.1 (Bloque I): planificador de operaciones largas. Deterministas: cada operación espera una señal explícita.</summary>
    public class OperationSchedulerTests
    {
        private sealed class Op
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<string> Task;
        }

        private static Op Start(OperationScheduler s, string owner, string key, string name, List<string> order, CancellationToken ct = default)
        {
            var op = new Op();
            op.Task = s.Run(owner, key, async () =>
            {
                lock (order) order.Add(name);
                op.Started.TrySetResult(true);
                await op.Release.Task;
                return name;
            }, ct);
            return op;
        }

        private static async Task Finish(Op op) { op.Release.TrySetResult(true); await op.Task; }
        private static long Stat(OperationScheduler s, string k) => (long)s.Stats()[k];

        [Fact]
        public async Task OneAgentCannotStarveAnother_FairOrderAndPerOwnerLimit()
        {
            var s = new OperationScheduler { MaxRunning = 2, MaxPerOwner = 1 };
            var order = new List<string>();
            var a1 = Start(s, "ana", null, "a1", order);
            var a2 = Start(s, "ana", null, "a2", order);
            var a3 = Start(s, "ana", null, "a3", order);
            var b1 = Start(s, "bob", null, "b1", order);
            await a1.Started.Task; await b1.Started.Task;                 // bob entra aunque ana llegó antes con tres
            Assert.False(a2.Started.Task.IsCompleted);
            Assert.Equal(2L, Stat(s, "en_curso")); Assert.Equal(2L, Stat(s, "en_cola"));
            await Finish(a1);
            await a2.Started.Task;
            await Finish(b1); await Finish(a2);
            await a3.Started.Task; await Finish(a3);
            Assert.Equal(new[] { "a1", "b1", "a2", "a3" }, order.ToArray());
            Assert.Equal(0L, Stat(s, "en_curso")); Assert.Equal(0L, Stat(s, "en_cola"));
        }

        [Fact]
        public async Task SameExclusiveKey_NeverRunsConcurrently_EvenForDifferentAgents()
        {
            var s = new OperationScheduler { MaxRunning = 4, MaxPerOwner = 4 };
            var order = new List<string>();
            var x = Start(s, "ana", "doc-1", "x", order);
            var y = Start(s, "bob", "doc-1", "y", order);
            var z = Start(s, "bob", "doc-2", "z", order);
            await x.Started.Task; await z.Started.Task;
            Assert.False(y.Started.Task.IsCompleted);                     // mismo documento: espera
            await Finish(x);
            await y.Started.Task;
            await Finish(y); await Finish(z);
        }

        [Fact]
        public async Task CancellingAQueuedOperation_RemovesItWithoutRunning_AndFullQueueIsRejected()
        {
            var s = new OperationScheduler { MaxRunning = 1, MaxPerOwner = 1, MaxQueued = 2 };
            var order = new List<string>();
            var running = Start(s, "ana", null, "r", order);
            await running.Started.Task;
            using (var cts = new CancellationTokenSource())
            {
                var queued = Start(s, "bob", null, "q", order, cts.Token);
                var other = Start(s, "eve", null, "o", order);
                await Assert.ThrowsAsync<SchedulerBusyException>(() => s.Run("zoe", null, () => Task.FromResult(1), CancellationToken.None));
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Task);
                Assert.False(queued.Started.Task.IsCompleted);            // nunca empezó: nada que reconciliar
                await Finish(running);
                await other.Started.Task; await Finish(other);
            }
            Assert.Equal(new[] { "r", "o" }, order.ToArray());
            Assert.Equal(1L, Stat(s, "canceladas_en_cola")); Assert.Equal(1L, Stat(s, "rechazadas_por_cola_llena"));
            using (var pre = new CancellationTokenSource())
            {
                pre.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Run("ana", null, () => Task.FromResult(1), pre.Token));
            }
        }

        [Fact]
        public async Task FailingOperation_ReleasesItsSlotAndKey()
        {
            var s = new OperationScheduler { MaxRunning = 1 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Run<int>("ana", "doc-1", () => throw new InvalidOperationException("x"), CancellationToken.None));
            Assert.Equal(7, await s.Run("bob", "doc-1", () => Task.FromResult(7), CancellationToken.None));
            Assert.Equal(0L, Stat(s, "en_curso"));
        }

        [Fact]
        public async Task ManyConcurrentOperations_RespectLimitsAndAllComplete()
        {
            var s = new OperationScheduler { MaxRunning = 3, MaxPerOwner = 2, MaxQueued = 1000 };
            int running = 0, peak = 0, sameDoc = 0, sameDocPeak = 0;
            var perOwner = new Dictionary<string, int>(); var ownerPeak = 0;
            var gate = new object();
            var tasks = Enumerable.Range(0, 120).Select(i => Task.Run(() => s.Run("agente" + (i % 4), i % 5 == 0 ? "doc" : null, async () =>
            {
                lock (gate)
                {
                    running++; peak = Math.Max(peak, running);
                    var o = "agente" + (i % 4); perOwner[o] = (perOwner.TryGetValue(o, out var n) ? n : 0) + 1; ownerPeak = Math.Max(ownerPeak, perOwner[o]);
                    if (i % 5 == 0) { sameDoc++; sameDocPeak = Math.Max(sameDocPeak, sameDoc); }
                }
                await Task.Yield();
                lock (gate) { running--; perOwner["agente" + (i % 4)]--; if (i % 5 == 0) sameDoc--; }
                return i;
            }, CancellationToken.None))).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(Enumerable.Range(0, 120), results.OrderBy(x => x));
            Assert.True(peak <= 3, "pico global " + peak);
            Assert.True(ownerPeak <= 2, "pico por agente " + ownerPeak);
            Assert.Equal(1, sameDocPeak);
            Assert.Equal(120L, Stat(s, "iniciadas"));
        }
    }
}
