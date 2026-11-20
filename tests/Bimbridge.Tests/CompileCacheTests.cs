// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Issue #9: the compile cache compiles only once per fingerprint even when simultaneous requests arrive.</summary>
    public class CompileCacheTests
    {
        private sealed class Unit { public int N; }

        [Fact]
        public void ConcurrentSameKey_CompilesOnce_OthersWaitAndHit()
        {
            var cache = new CompileCache<Unit>();
            var compiles = 0;
            using (var start = new ManualResetEventSlim(false))
            using (var inside = new ManualResetEventSlim(false))
            {
                var first = Task.Run(() => cache.GetOrCompile("k", () =>
                {
                    Interlocked.Increment(ref compiles);
                    inside.Set();
                    start.Wait();                                  // slow compile, held deterministically
                    return Tuple.Create(new Unit { N = 1 }, 40L);
                }, out _));
                Assert.True(inside.Wait(5000));
                var others = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    var u = cache.GetOrCompile("k", () => { Interlocked.Increment(ref compiles); return Tuple.Create(new Unit { N = 2 }, 1L); }, out var fc);
                    return (u, fc);
                })).ToArray();
                SpinWait.SpinUntil(() => cache.CoalescedWaits >= 4, 5000);
                start.Set();
                Task.WaitAll(others.Cast<Task>().Append(first).ToArray(), 10000);
                Assert.Equal(1, compiles);
                Assert.All(others, t => { Assert.Same(first.Result, t.Result.u); Assert.True(t.Result.fc); });
                Assert.Equal(1, cache.Misses);
                Assert.Equal(4, cache.Hits);
                Assert.Equal(4, cache.CoalescedWaits);
                Assert.Equal(40, cache.TotalCompileMs);
            }
        }

        [Fact]
        public void Failures_AreNotCached()
        {
            var cache = new CompileCache<Unit>();
            Assert.Null(cache.GetOrCompile("k", () => null, out var fc1));
            Assert.False(fc1);
            var u = cache.GetOrCompile("k", () => Tuple.Create(new Unit(), 5L), out var fc2);
            Assert.NotNull(u);
            Assert.False(fc2);
            Assert.Equal(1, cache.Failures);
            Assert.Same(u, cache.GetOrCompile("k", () => throw new InvalidOperationException("must not compile"), out var fc3));
            Assert.True(fc3);
            Assert.Equal(1L, (long)cache.Stats()["guardados"]);
        }

        [Fact]
        public void DifferentKeys_CompileIndependently()
        {
            var cache = new CompileCache<Unit>();
            var a = cache.GetOrCompile("a", () => Tuple.Create(new Unit { N = 1 }, 1L), out _);
            var b = cache.GetOrCompile("b", () => Tuple.Create(new Unit { N = 2 }, 1L), out _);
            Assert.NotSame(a, b);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void FailedCompile_WhileOthersWait_NeverCompilesTheSameKeyTwiceAtOnce()
        {
            // AUD-05: before, when the gate was removed while another thread was waiting, a third one created a new gate and compiled in
            // parallel with the waiting one (the first compile had failed). Now the gate lives as long as someone uses it.
            var cache = new CompileCache<Unit>();
            var concurrent = 0; var maxConcurrent = 0; var calls = 0;
            Tuple<Unit, long> SlowFail()
            {
                var c = Interlocked.Increment(ref concurrent);
                int m; while ((m = maxConcurrent) < c && Interlocked.CompareExchange(ref maxConcurrent, c, m) != m) { }
                Interlocked.Increment(ref calls);
                Thread.Sleep(30);
                Interlocked.Decrement(ref concurrent);
                return null;   // compile failure: not cached
            }
            var threads = Enumerable.Range(0, 16).Select(i => new Thread(() => cache.GetOrCompile("k", SlowFail, out bool _))).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(20000));
            Assert.Equal(1, maxConcurrent);                 // never two compiles of the same key at once
            Assert.Equal(16, calls);                        // failures are not cached: each attempt compiles again, serially
            Assert.Equal(0, cache.LiveGates);               // no orphan gates
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void EntryLimit_RefusesNewKeysWithReason_KeepsServingCompiledOnes()
        {
            // AUD-05: loaded assemblies are not unloaded; evicting entries (LRU) would only force recompiling and loading another one. When the
            // limit is reached, NEW keys are refused with a clear reason and what is already compiled keeps being served.
            var cache = new CompileCache<Unit>(maxEntries: 3, sizeOf: u => 1000);
            for (var i = 0; i < 3; i++) Assert.NotNull(cache.GetOrCompile("k" + i, "agente-A", () => Tuple.Create(new Unit { N = i }, 1L), out _, out var r0));
            Assert.Null(cache.GetOrCompile("k3", "agente-B", () => throw new InvalidOperationException("must not compile"), out _, out var refusal));
            Assert.Contains("límite", refusal);
            Assert.Contains("reinicie Revit", refusal);
            Assert.NotNull(cache.GetOrCompile("k1", "agente-B", () => throw new InvalidOperationException("must not compile"), out var fc, out _));
            Assert.True(fc);
            Assert.Equal(1, cache.Refused);
            Assert.Equal(3000, cache.LoadedBytes);
            Assert.Equal(3L, cache.ByOwner().Single(kv => kv.Key == "agente-A").Value);   // per-agent accounting
        }

        [Fact]
        public void EntryLimit_IsExactUnderConcurrentDistinctKeys_AndFailuresFreeTheirSlot()
        {
            var cache = new CompileCache<Unit>(maxEntries: 10);
            Assert.Null(cache.GetOrCompile("falla", null, () => null, out _, out _));   // a failure does not use up a slot
            var ok = 0;
            var threads = Enumerable.Range(0, 64).Select(i => new Thread(() =>
            {
                if (cache.GetOrCompile("k" + i, "a", () => { Thread.Sleep(5); return Tuple.Create(new Unit { N = i }, 1L); }, out _, out _) != null) Interlocked.Increment(ref ok);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(20000));
            Assert.Equal(10, ok);
            Assert.Equal(10, cache.Count);
            Assert.Equal(54, cache.Refused);
        }
    }
}