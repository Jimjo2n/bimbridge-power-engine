// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.AgentHost;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>alpha3 P1-B: la cola del servicio no pierde reprogramaciones ni ejecuta un plan dos veces a la vez.</summary>
    public class PlanQueueTests
    {
        [Fact]
        public async Task EnqueueDuringRun_IsRepeatedOnce_NeverConcurrent()
        {
            var q = new PlanQueue(8);
            var runs = 0; var concurrent = 0; var maxConcurrent = 0;
            using (var inRun = new SemaphoreSlim(0))
            using (var proceed = new SemaphoreSlim(0))
            using (var cts = new CancellationTokenSource())
            {
                void Run(string id)
                {
                    var c = Interlocked.Increment(ref concurrent);
                    lock (q) maxConcurrent = System.Math.Max(maxConcurrent, c);
                    var n = Interlocked.Increment(ref runs);
                    if (n == 1) { inRun.Release(); proceed.Wait(); }
                    Interlocked.Decrement(ref concurrent);
                }
                var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => q.WorkerLoop(Run, cts.Token))).ToList();
                Assert.True(q.Enqueue("p"));
                Assert.True(await inRun.WaitAsync(5000));
                for (var i = 0; i < 20; i++) Assert.True(q.Enqueue("p"));   // llegan durante la ejecución
                Assert.True(q.IsRunning("p"));
                Assert.Equal(0, q.Queued);                                    // no ocupan capacidad
                proceed.Release();
                for (var t = 0; t < 200 && (runs < 2 || q.IsRunning("p") || q.Queued > 0); t++) await Task.Delay(10);
                Assert.Equal(2, runs);           // exactamente una repetición, no 0 (perdida) ni 21 (duplicados)
                Assert.Equal(1, maxConcurrent);
                cts.Cancel();
                await Task.WhenAll(workers);
            }
        }

        [Fact]
        public async Task ManyPlans_ManyWorkers_EachRunsAtLeastOnce_NoOverlapPerPlan()
        {
            var q = new PlanQueue(64);
            var active = new ConcurrentDictionary<string, int>();
            var count = new ConcurrentDictionary<string, int>();
            var overlap = 0;
            using (var cts = new CancellationTokenSource())
            {
                void Run(string id)
                {
                    if (active.AddOrUpdate(id, 1, (_, v) => v + 1) > 1) Interlocked.Increment(ref overlap);
                    count.AddOrUpdate(id, 1, (_, v) => v + 1);
                    Thread.Sleep(2);
                    active.AddOrUpdate(id, 0, (_, v) => v - 1);
                }
                var workers = Enumerable.Range(0, 6).Select(_ => Task.Run(() => q.WorkerLoop(Run, cts.Token))).ToList();
                Parallel.For(0, 600, i => q.Enqueue("p" + (i % 10)));
                for (var t = 0; t < 500 && (q.Queued > 0 || Enumerable.Range(0, 10).Any(i => q.IsRunning("p" + i))); t++) await Task.Delay(10);
                Assert.Equal(0, overlap);
                Assert.All(Enumerable.Range(0, 10), i => Assert.True(count.ContainsKey("p" + i)));
                cts.Cancel();
                await Task.WhenAll(workers);
            }
        }

        [Fact]
        public void FullQueue_RejectsNewPlans_ButDuplicatesAreIdempotent()
        {
            var q = new PlanQueue(1);
            Assert.True(q.Enqueue("a"));
            Assert.True(q.Enqueue("a"));
            Assert.False(q.Enqueue("b"));
            Assert.Equal(1, q.Queued);
        }
    }
}
