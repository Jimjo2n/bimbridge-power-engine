using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Issue #9: la caché de compilación compila una sola vez por huella aunque lleguen peticiones simultáneas.</summary>
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
                    start.Wait();                                  // compilación lenta, retenida de forma determinista
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
            Assert.Same(u, cache.GetOrCompile("k", () => throw new InvalidOperationException("no debe compilar"), out var fc3));
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
    }
}
