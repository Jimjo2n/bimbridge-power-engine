// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Bimbridge.AgentHost;
using Bimbridge.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Bimbridge.Tests
{
    /// <summary>Persistent run service (P1 §3): bounded queue, control, recovery on startup and graceful shutdown.</summary>
    public class AgentHostServiceTests
    {
        private readonly ITestOutputHelper _out;
        public AgentHostServiceTests(ITestOutputHelper o) { _out = o; }

        private static string PipeName() => "bimbridge-ah-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        private static Dictionary<string, object> Call(string pipe, string command, Dictionary<string, object> extra = null)
        {
            var req = new Dictionary<string, object> { ["orden"] = command };
            if (extra != null) foreach (var kv in extra) req[kv.Key] = kv.Value;
            return ServiceClient.Call(pipe, req);
        }

        private static Dictionary<string, object> Submit(string pipe, PlanDraft plan, int fixtures = 5, Dictionary<string, object> config = null)
        {
            var model = new Dictionary<string, object> { ["elementos"] = new List<object> { AH.Cat + "=" + fixtures + "@L03" } };
            if (config != null) model["config"] = config;
            return Call(pipe, "enviar", new Dictionary<string, object> { ["ir"] = plan.ToJson(), ["modelo"] = model });
        }

        private static string Text(Dictionary<string, object> r) => Arg.GetString(r, "texto") ?? Json.Write(r);

        private static string WaitFor(string pipe, string plan, Func<string, bool> cond, int ms = 60000)
        {
            var sw = Stopwatch.StartNew();
            string last = null;
            while (sw.ElapsedMilliseconds < ms)
            {
                last = Text(Call(pipe, "resumen", new Dictionary<string, object> { ["plan"] = plan }));
                if (cond(last)) return last;
                Thread.Sleep(50);
            }
            throw new TimeoutException("condition not reached; last summary: " + last);
        }

        private static IHost StartHost(string root, string pipe, int cap = 16, int workers = 2, int leaseMs = 30000)
        {
            var h = ServiceMain.Build(new ServiceOptions { Root = root, Pipe = pipe, QueueCapacity = cap, Workers = workers, LeaseTtl = TimeSpan.FromMilliseconds(leaseMs) });
            h.StartAsync().GetAwaiter().GetResult();
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try { if (Call(pipe, "salud")["ok"] is bool b && b) return h; }
                catch (Exception) when (sw.ElapsedMilliseconds < 10000) { Thread.Sleep(50); }
            }
        }

        [Fact]
        public void Service_SubmitRunInspectReport_Idempotent_StructuredErrors()
        {
            var root = AH.NewDir("svc"); var pipe = PipeName();
            var host = StartHost(root, pipe);
            try
            {
                var plan = AH.BasePlan();
                var s1 = Submit(pipe, plan);
                Assert.True((bool)s1["ok"], Json.Write(s1));
                var id = Arg.GetString(s1, "plan");
                var final = WaitFor(pipe, id, t => t.StartsWith("3/3 verified"));
                _out.WriteLine(final);
                var s2 = Submit(pipe, plan);                                   // same plan: not duplicated
                Assert.True((bool)s2["ok"]); Assert.True((bool)s2["ya_existia"]);
                Thread.Sleep(300);
                var model = DurableSimModel.Open(Path.Combine(root, "planes", id, "modelo"));
                Assert.Equal(1, model.TxFor(plan.Ops[1].Id, id).Count);
                Assert.Equal("no exceptions", Text(Call(pipe, "excepciones", new Dictionary<string, object> { ["plan"] = id })));
                Assert.Equal("none", Text(Call(pipe, "siguiente", new Dictionary<string, object> { ["plan"] = id })));
                Assert.StartsWith("seq ", Text(Call(pipe, "cambios", new Dictionary<string, object> { ["plan"] = id, ["desde"] = 0L })));
                var rep = Call(pipe, "informe", new Dictionary<string, object> { ["plan"] = id });
                Assert.Equal(0L, Arg.GetLong(rep, "sin_resolver"));
                Assert.True(File.Exists(Arg.GetString(rep, "detalle")));
                var bad = Call(pipe, "enviar", new Dictionary<string, object> { ["ir"] = new Dictionary<string, object> { ["ir"] = 2L, ["autorizacion"] = "todo" } });
                Assert.False((bool)bad["ok"]);
                Assert.Equal("plan_invalido", Arg.GetString((Dictionary<string, object>)bad["error"], "clase"));
                var unknown = Call(pipe, "resumen", new Dictionary<string, object> { ["plan"] = "no-existe" });
                Assert.Equal("plan_desconocido", Arg.GetString((Dictionary<string, object>)unknown["error"], "clase"));
                var health = Call(pipe, "salud");
                Assert.Equal(AgentHostService.Version, health["version"]);
                Assert.Equal(1L, health["planes"]);
            }
            finally { host.StopAsync().GetAwaiter().GetResult(); host.Dispose(); }
        }

        [Fact]
        public void Service_BoundedQueue_RejectsWhenFull()
        {
            var root = AH.NewDir("svccola"); var pipe = PipeName();
            var host = StartHost(root, pipe, cap: 1, workers: 1);
            try
            {
                var cfg = new Dictionary<string, object> { ["retardo_ms"] = 150L };
                Dictionary<string, object> Sub(int i) => Submit(pipe, AH.Plan(Enumerable.Range(0, 40).Select(k => AH.Set("Cola" + i + "_" + k, (long)k, level: "L03")).ToArray()), config: cfg);
                var r = new List<Dictionary<string, object>> { Sub(0) };
                // wait for the only worker to take the first one off the queue (may be slow under load)
                var until = DateTime.UtcNow.AddSeconds(20);
                while ((long)Call(pipe, "salud")["cola"] != 0 && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
                r.Add(Sub(1)); r.Add(Sub(2));
                _out.WriteLine(string.Join("\n", r.Select(Json.Write)));
                Assert.True((bool)r[0]["ok"]);
                Assert.True((bool)r[1]["ok"]);
                Assert.False((bool)r[2]["ok"]);
                Assert.Equal("cola_llena", Arg.GetString((Dictionary<string, object>)r[2]["error"], "clase"));
            }
            finally { host.StopAsync().GetAwaiter().GetResult(); host.Dispose(); }
        }

        [Fact]
        public void Service_PauseResumeCancel_ArePersistedAndRespected()
        {
            var root = AH.NewDir("svcctl"); var pipe = PipeName();
            var host = StartHost(root, pipe, workers: 1);
            try
            {
                var plan = AH.Plan(Enumerable.Range(0, 200).Select(k => AH.Set("Ctl" + k, (long)k, level: "L03")).ToArray());
                var id = Arg.GetString(Submit(pipe, plan, config: new Dictionary<string, object> { ["retardo_ms"] = 30L }), "plan");
                WaitFor(pipe, id, t => !t.StartsWith("0/"));
                Assert.True((bool)Call(pipe, "pausar", new Dictionary<string, object> { ["plan"] = id })["ok"]);
                var paused = WaitFor(pipe, id, t => t.Contains("plan pausado") && !t.Contains("running"));
                Thread.Sleep(300);
                Assert.Equal(paused.Split('·')[0], Text(Call(pipe, "resumen", new Dictionary<string, object> { ["plan"] = id })).Split('·')[0]);   // does not advance while paused
                Assert.True((bool)Call(pipe, "reanudar", new Dictionary<string, object> { ["plan"] = id })["ok"]);
                WaitFor(pipe, id, t => !t.Contains("pausado") && t.Contains("running"));
                Assert.True((bool)Call(pipe, "cancelar", new Dictionary<string, object> { ["plan"] = id })["ok"]);
                var done = WaitFor(pipe, id, t => t.Contains("plan cancelado") && !t.Contains("running") && !t.Contains("pending"));
                _out.WriteLine(done);
                Assert.Contains("cancelled", done);
                var model = DurableSimModel.Open(Path.Combine(root, "planes", id, "modelo"));
                Assert.All(plan.Ops, op => Assert.InRange(model.TxFor(op.Id, id).Count, 0, 1));
            }
            finally { host.StopAsync().GetAwaiter().GetResult(); host.Dispose(); }
        }

        [Fact]
        public void Service_GracefulShutdown_ThenNewHostRecoversOnStartup()
        {
            var root = AH.NewDir("svcrec"); var pipe = PipeName();
            var plan = AH.Plan(Enumerable.Range(0, 120).Select(k => AH.Set("Rec" + k, (long)k, level: "L03")).ToArray());
            var host = StartHost(root, pipe, workers: 1);
            string id;
            try
            {
                id = Arg.GetString(Submit(pipe, plan, config: new Dictionary<string, object> { ["retardo_ms"] = 40L }), "plan");
                WaitFor(pipe, id, t => int.Parse(t.Split('/')[0]) >= 5);
            }
            finally { host.StopAsync().GetAwaiter().GetResult(); host.Dispose(); }
            var mid = AH.Read(Path.Combine(root, "planes", id), out _, out _);
            var verified = mid.Ops.Count(o => o.Value.State == OpState.Verified);
            Assert.InRange(verified, 5, 119);
            Assert.DoesNotContain(mid.Ops.Values, o => o.State == OpState.Running || o.State == OpState.Executed);   // stopped BETWEEN operations
            Assert.Null(PlanLease.Peek(Path.Combine(root, "planes", id)) is var l && l != null && !l.Released ? "lease not released" : null);

            var pipe2 = PipeName();
            var host2 = StartHost(root, pipe2, workers: 1);
            try
            {
                var final = WaitFor(pipe2, id, t => t.StartsWith("120/120 verified"));
                _out.WriteLine(final);
            }
            finally { host2.StopAsync().GetAwaiter().GetResult(); host2.Dispose(); }
            var log = File.ReadAllLines(Path.Combine(root, "servicio.jsonl"));
            Assert.Contains(log, x => x.Contains("\"ev\":\"recuperacion\""));
            var model = DurableSimModel.Open(Path.Combine(root, "planes", id, "modelo"));
            Assert.All(plan.Ops, op => Assert.Equal(1, model.TxFor(op.Id, id).Count));
        }

        /// <summary>The service as an independent PROCESS: it is killed with SIGKILL in the middle of a plan; another service process recovers it.</summary>
        [Fact]
        public void Service_ProcessKilled_NewServiceProcessRecoversWithoutDuplicates()
        {
            var root = AH.NewDir("svckill"); var pipe = PipeName();
            var plan = AH.Plan(Enumerable.Range(0, 40).Select(k => AH.Set("Kill" + k, (long)k, level: "L03")).ToArray());
            Process Start(string p)
            {
                var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add(AH.Dll); psi.ArgumentList.Add("servicio"); psi.ArgumentList.Add(root); psi.ArgumentList.Add("--tuberia"); psi.ArgumentList.Add(p); psi.ArgumentList.Add("--trabajadores"); psi.ArgumentList.Add("1");
                psi.Environment["BIMBRIDGE_AH_LEASE_MS"] = "800";
                psi.Environment.Remove("BIMBRIDGE_AH_FAULT");
                var proc = Process.Start(psi);
                proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    try { Call(p, "salud"); return proc; }
                    catch (Exception) when (sw.ElapsedMilliseconds < 20000) { Thread.Sleep(100); }
                }
            }
            var s1 = Start(pipe);
            var id = Arg.GetString(Submit(pipe, plan, config: new Dictionary<string, object> { ["retardo_ms"] = 25L }), "plan");
            WaitFor(pipe, id, t => int.Parse(t.Split('/')[0]) >= 8);
            s1.Kill();                      // SIGKILL: no graceful shutdown
            s1.WaitForExit();
            var pipe2 = PipeName();
            var s2 = Start(pipe2);
            try
            {
                var final = WaitFor(pipe2, id, t => t.StartsWith("40/40 verified"), 90000);
                _out.WriteLine(final);
                Assert.Equal(0L, Arg.GetLong(Call(pipe2, "informe", new Dictionary<string, object> { ["plan"] = id }), "sin_resolver"));
                Call(pipe2, "apagar");
                Assert.True(s2.WaitForExit(30000));
            }
            finally { if (!s2.HasExited) s2.Kill(); }
            var model = DurableSimModel.Open(Path.Combine(root, "planes", id, "modelo"));
            Assert.All(plan.Ops, op => Assert.Equal(1, model.TxFor(op.Id, id).Count));
            var log = File.ReadAllText(Path.Combine(root, "servicio.jsonl"));
            _out.WriteLine(log);
            Assert.Contains("\"ev\":\"recuperacion\"", log);
        }
    }
}
