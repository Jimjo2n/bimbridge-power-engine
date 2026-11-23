// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Bimbridge.Engine;
using Bimbridge.Engine.Tasks;
using Bimbridge.Protocol;
using Bimbridge.Scripting;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// R09: reproducible measurements (without Revit). They always check a generous ceiling; with BIMBRIDGE_MEASURE_OUT=path they write the evidence JSON.
    /// These are measurements from the Linux CI container, not from Windows/Revit.
    /// </summary>
    public class MeasurementTests
    {
        private static Dictionary<string, object> Stats(List<double> ms)
        {
            ms.Sort();
            double P(double q) => ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(q * ms.Count) - 1)];
            return new Dictionary<string, object> { ["n"] = (long)ms.Count, ["mean_ms_value"] = Math.Round(ms.Average(), 4), ["p50_ms"] = Math.Round(P(0.5), 4), ["p95_ms"] = Math.Round(P(0.95), 4), ["max_ms"] = Math.Round(ms.Max(), 4) };
        }

        private static List<double> Time(int n, Action a)
        {
            var list = new List<double>(n);
            for (var i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); a(); list.Add(sw.Elapsed.TotalMilliseconds); }
            return list;
        }

        [Fact]
        public void Measure_SafetyOverheads()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimbridge-med-" + Guid.NewGuid().ToString("N"));
            var res = new Dictionary<string, object>();

            var audit = new AuditLog(Path.Combine(root, "aud", "a.jsonl"));
            for (var i = 0; i < 20; i++) audit.Append("w", "warmup", "ok");
            res["audit_append"] = Stats(Time(500, () => audit.Append("s", "exec_code", "ok", new Dictionary<string, object> { ["sha256"] = new string('a', 64) })));
            res["audit_intent_plus_result"] = Stats(Time(300, () => { var id = audit.Intent("s", "task_batch", "t"); audit.Outcome("s", "task_batch", id, "result"); }));
            var big = Path.Combine(root, "aud", "a.jsonl");
            res["audit_bytes_per_record"] = new FileInfo(big).Length / AuditLog.Verify(big).Lines;
            res["audit_verify_full"] = Stats(Time(5, () => AuditLog.Verify(big)));

            var store = new TaskStore(Path.Combine(root, "tasks"));
            Assert.True(store.TryLease("t", "A", "i", TimeSpan.FromHours(1), DateTime.UtcNow, out var l));
            res["lease_validate"] = Stats(Time(1000, () => store.ValidateOwnership("t", "A", l.Fencing, DateTime.UtcNow)));
            res["lease_renew"] = Stats(Time(300, () => store.RenewLease("t", "A", l.Fencing, TimeSpan.FromHours(1), DateTime.UtcNow)));

            var g = new GrantStore(Path.Combine(root, "aut"));
            for (var i = 0; i < 10; i++) g.Approve("t" + i, "doc", null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "p", DateTime.UtcNow);
            res["lease_check_10"] = Stats(Time(300, () => g.Check("t5", "doc", null, Capability.Modify, DateTime.UtcNow)));

            var outp = Environment.GetEnvironmentVariable("BIMBRIDGE_MEASURE_OUT");
            if (!string.IsNullOrEmpty(outp)) File.WriteAllText(Path.Combine(outp, "medicion_sobrecostes.json"), Json.Write(res));
            Assert.True((double)((Dictionary<string, object>)res["lease_validate"])["p95_ms"] < 50);
        }

        /// <summary>
        /// Memory retained by script assemblies: 0.6.x loads each unique script into a NON-collectible context (as today in 2025–26
        /// and always in 2022–24): it is never released. It is compared with a collectible AssemblyLoadContext to support (or not) the proposal.
        /// </summary>
        [Fact]
        public void Measure_RoslynAssemblyRetention()
        {
            var refs = ScriptingTests.References(2026);
            const int n = 200;
            var images = new List<byte[]>();
            var compileMs = new List<double>();
            for (var i = 0; i < n; i++)
            {
                var w = ScriptSource.Wrap("return " + i + " + doc.Title.Length;", "M" + i);
                var sw = Stopwatch.StartNew();
                var img = Compiler.Compile(w.Source, refs, "med_" + i + "_" + Guid.NewGuid().ToString("N").Substring(0, 6), out var d, out _, out _);
                compileMs.Add(sw.Elapsed.TotalMilliseconds);
                Assert.True(img != null, string.Join("\n", d));
                images.Add(img);
            }
            long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

            Mem();
            var ws0 = Process.GetCurrentProcess().PrivateMemorySize64;
            var baseMem = Mem();
            var asmBefore = AppDomain.CurrentDomain.GetAssemblies().Length;
            var ctx = new AssemblyLoadContext("nonCollectible_" + Guid.NewGuid().ToString("N"), isCollectible: false);
            foreach (var img in images) using (var ms = new MemoryStream(img)) ctx.LoadFromStream(ms);
            var retainedNonCollectible = Mem() - baseMem;
            var asmAfter = AppDomain.CurrentDomain.GetAssemblies().Length;
            var ws1 = Process.GetCurrentProcess().PrivateMemorySize64;

            var base2 = Mem();
            WeakReference wr = LoadCollectible(images);
            for (var i = 0; i < 10 && wr.IsAlive; i++) Mem();
            var retainedCollectible = Mem() - base2;
            var ws2 = Process.GetCurrentProcess().PrivateMemorySize64;

            var res = new Dictionary<string, object>
            {
                ["unique_scripts"] = (long)n,
                ["mean_image_bytes"] = (long)images.Average(x => x.Length),
                ["compile_2026"] = Stats(compileMs.Skip(5).ToList()),
                ["noncollectible_bytes_retained"] = retainedNonCollectible,
                ["noncollectible_new_assemblies"] = (long)(asmAfter - asmBefore),
                ["collectible_unloaded"] = !wr.IsAlive,
                ["collectible_bytes_retained_after_unload"] = retainedCollectible,
                ["private_memory_delta_noncollectible_bytes"] = ws1 - ws0,
                ["memoria_privada_delta_coleccionable_tras_descargar_bytes"] = ws2 - ws1,
                ["note"] = "GC.GetTotalMemory does not count assembly metadata (native memory); the process private memory does, but it is noisy",
            };
            var outp = Environment.GetEnvironmentVariable("BIMBRIDGE_MEASURE_OUT");
            if (!string.IsNullOrEmpty(outp)) File.WriteAllText(Path.Combine(outp, "medicion_roslyn_memoria.json"), Json.Write(res));
            Assert.Equal(n, asmAfter - asmBefore);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference LoadCollectible(List<byte[]> images)
        {
            var c = new AssemblyLoadContext("collectible_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            foreach (var img in images) using (var ms = new MemoryStream(img)) c.LoadFromStream(ms);
            var wr = new WeakReference(c);
            c.Unload();
            return wr;
        }

        private sealed class AllowAll : ITaskAuthority { public string Check(TaskRecord t, string c, DateTime n) => null; }

        /// <summary>Engine performance regression: 1,000 units with all the 0.6.1 protections (two-phase audit,
        /// signed grants, document lease, last safe point per unit) versus the same without audit or grants.</summary>
        [Fact]
        public async System.Threading.Tasks.Task Measure_TaskEngineOverhead_SafetyOnVsOff()
        {
            async System.Threading.Tasks.Task<double> RunOnce(bool safety)
            {
                var root = Path.Combine(Path.GetTempPath(), "bimbridge-ov-" + Guid.NewGuid().ToString("N"));
                var m = new TaskEngineTests.FakeModel();
                ITaskAuthority auth = new AllowAll();
                AuditLog audit = null;
                if (safety)
                {
                    var g = new GrantStore(Path.Combine(root, "aut"));
                    g.Approve("ov", "archivo:c:\\practica.rvt", null, new[] { Capability.Modify }, TimeSpan.FromHours(1), "p", DateTime.UtcNow);
                    auth = new GrantAuthority(g);
                    audit = new AuditLog(Path.Combine(root, "aud", "a.jsonl"));
                }
                var mgr = new TaskManager(new TaskStore(root), "A", t => new TaskEngineTests.FakeBackend(m))
                { Delay = (d, ct) => System.Threading.Tasks.Task.Delay(1, ct), Authority = auth, Audit = audit, BudgetMs = 200 };
                var draft = new TaskRecord
                {
                    Id = "ov", Objective = "x", RecipeName = "f", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c",
                    Args = new Dictionary<string, object> { ["n"] = 1000L },
                    Docs = new List<TaskDoc> { new TaskDoc { Key = "archivo:c:\\practica.rvt", Title = "p", Generation = "inst#1" } },
                };
                var sw = Stopwatch.StartNew();
                mgr.Start(draft, "// r", "p");
                await mgr.Completion("ov");
                var elapsed = sw.Elapsed.TotalMilliseconds;
                Assert.Equal(TaskState.Applied, mgr.Get("ov", out _).State);
                return elapsed;
            }
            await RunOnce(false); await RunOnce(true); // warm up
            var off = new List<double>(); var on = new List<double>();
            for (var i = 0; i < 7; i++) { off.Add(await RunOnce(false)); on.Add(await RunOnce(true)); }
            var res = new Dictionary<string, object> { ["units"] = 1000L, ["protections_off"] = Stats(off), ["protections_0_6_1"] = Stats(on) };
            var outp = Environment.GetEnvironmentVariable("BIMBRIDGE_MEASURE_OUT");
            if (!string.IsNullOrEmpty(outp)) File.WriteAllText(Path.Combine(outp, "medicion_motor_1000.json"), Json.Write(res));
        }
    }
}
