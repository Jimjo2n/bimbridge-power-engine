// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    public sealed class ServiceOptions
    {
        public string Root;
        public string Pipe;
        public int QueueCapacity = 16;
        public int Workers = 2;
        public TimeSpan? LeaseTtl;
        public int CheckpointEvery = 64;
    }

    /// <summary>
    /// Persistent execution service (P1 §3), hosted with Microsoft.Extensions.Hosting and runnable from the console (it does not install a
    /// Windows service or touch the system). Bounded queue, N workers, each plan with its lease (two services never run the same
    /// plan), pause/resume/cancel persisted in the plan journal, recovery at startup, orderly shutdown (stops BETWEEN
    /// operations and leaves a checkpoint), structured errors, health and compact queries. It depends on no conversation: everything it
    /// needs is in the root folder.
    ///
    /// Root: plans/&lt;hash&gt;/ (plan.json, journal.jsonl, state.json, control/, simulated model/) and service.jsonl (service log).
    /// Transport: local named pipe (PipeOptions.CurrentUserOnly), one JSON line per request and per response.
    /// </summary>
    public sealed class AgentHostService : BackgroundService
    {
        /// <summary>rc1: version and commit come from the assembly (Directory.Build.props + SourceRevisionId); never a literal.</summary>
        public static readonly string Version = BuildInfo.Version;
        private readonly ServiceOptions _o;
        private readonly IHostApplicationLifetime _life;
        private readonly ConcurrentDictionary<string, Slot> _slots = new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);
        private readonly PlanQueue _pq;   // alpha3 (P1-B), see PlanQueue
        private readonly object _logLock = new object();
        private readonly DateTime _started = DateTime.UtcNow;
        private CancellationToken _stopping;

        private sealed class Slot
        {
            public CancellationTokenSource Cts;
            public string After;   // "pause" | "cancel" | null: what to do when the current operation finishes
        }

        public AgentHostService(ServiceOptions o, IHostApplicationLifetime life)
        {
            _o = o; _life = life;
            Directory.CreateDirectory(PlansDir);
            _pq = new PlanQueue(o.QueueCapacity) { OnLost = (id, why) => Log("reschedule_not_queued", new Dictionary<string, object> { ["plan"] = id, ["reason"] = why }) };
        }

        private string PlansDir => LegacyAliases.ResolveExisting(_o.Root, Path.Combine(_o.Root, "plans"));
        private string PlanDir(string id) => Path.Combine(PlansDir, id);

        private void Log(string ev, Dictionary<string, object> data = null)
        {
            var d = new Dictionary<string, object> { ["t"] = Home.Now(), ["ev"] = ev };
            if (data != null) foreach (var kv in data) d[kv.Key] = kv.Value;
            lock (_logLock) File.AppendAllText(Path.Combine(_o.Root, "service.jsonl"), Json.Write(d) + "\n");
        }

        // ---------------- lifecycle ----------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stopping = stoppingToken;
            Log("start", new Dictionary<string, object> { ["version"] = Version, ["pid"] = (long)Process.GetCurrentProcess().Id, ["pipe"] = _o.Pipe, ["queue"] = (long)_o.QueueCapacity, ["workers"] = (long)_o.Workers });
            // recovery at startup: every active plan with pending work goes back to the queue
            foreach (var d in Directory.GetDirectories(PlansDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(d);
                var view = Inspect(id, out var diags);
                if (view == null) { Log("recovery_rejected", new Dictionary<string, object> { ["plan"] = id, ["diag"] = string.Join("; ", diags) }); continue; }
                if (view.Value.State.PlanState == PlanRunState.Active && view.Value.State.Ops.Values.Any(o => !OpState.IsFinal(o.State)))
                {
                    Log("recovery", new Dictionary<string, object> { ["plan"] = id, ["summary"] = Compact.Summary(view.Value.Plan, view.Value.State) });
                    Enqueue(id);
                }
            }
            var workers = Enumerable.Range(0, Math.Max(1, _o.Workers)).Select(i => Task.Run(() => _pq.WorkerLoop(id => RunPlan(id, stoppingToken), stoppingToken))).ToList();
            var pipe = Task.Run(() => PipeLoop(stoppingToken));
            await Task.WhenAll(workers.Append(pipe)).ConfigureAwait(false);
            Log("off", new Dictionary<string, object> { ["running_at_stop"] = (long)_slots.Count });
        }

        private bool Enqueue(string id) => _pq.Enqueue(id);

        private void RunPlan(string id, CancellationToken stop)
        {
            var slot = new Slot { Cts = CancellationTokenSource.CreateLinkedTokenSource(stop) };
            if (!_slots.TryAdd(id, slot)) return;    // already running in this service
            try
            {
                var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "model"));
                using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model, Faults.Current), new RunnerOptions { Faults = Faults.Current, LeaseTtl = _o.LeaseTtl, CheckpointEvery = _o.CheckpointEvery }, out var diags))
                {
                    if (r == null) { Log("plan_rejected", new Dictionary<string, object> { ["plan"] = id, ["diag"] = string.Join("; ", diags) }); return; }
                    if (!r.IsOwner)
                    {
                        // another process holds it: retry when its lease expires (if it is still alive, it will renew it and we will yield again)
                        var wait = Expiry(r.Holder) - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
                        Log("plan_in_use", new Dictionary<string, object> { ["plan"] = id, ["owner"] = r.Holder?.Owner, ["retry_ms"] = (long)Math.Max(0, wait.TotalMilliseconds) });
                        _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), stop).ContinueWith(t => { if (!t.IsCanceled) Enqueue(id); }, TaskScheduler.Default);
                        return;
                    }
                    Log("execution_start", new Dictionary<string, object> { ["plan"] = id, ["fencing"] = r.State.Fencing, ["summary"] = Compact.Summary(r.Plan, r.State) });
                    string summary;
                    try { summary = r.Run(slot.Cts.Token); }
                    catch (PlanLease.LeaseLostException ex) { Log("lease_lost", new Dictionary<string, object> { ["plan"] = id, ["detail"] = ex.Message }); return; }
                    if (slot.After == "pause") r.Pause();
                    else if (slot.After == "cancel") { r.Cancel(); summary = r.Run(CancellationToken.None); }
                    Log("execution_end", new Dictionary<string, object> { ["plan"] = id, ["summary"] = Compact.Summary(r.Plan, r.State), ["by_stop"] = stop.IsCancellationRequested });
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Log("error", new Dictionary<string, object> { ["plan"] = id, ["class"] = ex.GetType().Name, ["detail"] = ex.Message });
            }
            finally { _slots.TryRemove(id, out _); slot.Cts.Dispose(); }
        }

        private static DateTime Expiry(Engine.Tasks.TaskStore.Lease l) =>
            l != null && DateTime.TryParse(l.Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) ? e : DateTime.UtcNow;

        private (ValidatedPlan Plan, ExecJournal State)? Inspect(string id, out List<Diagnostic> diags)
        {
            diags = new List<Diagnostic>();
            var plan = PlanFile.Read(PlanDir(id), diags);
            if (plan == null) return null;
            var st = ExecJournal.Recover(PlanDir(id), plan, false, null, diags);
            if (st == null) return null;
            return (plan, st);
        }

        // ---------------- requests ----------------

        public static Dictionary<string, object> Error(string cls, string msg) => new Dictionary<string, object> { ["ok"] = false, ["error"] = new Dictionary<string, object> { ["class"] = cls, ["message"] = msg } };

        public Dictionary<string, object> Handle(Dictionary<string, object> req)
        {
            var cmd = Arg.GetString(req, "order");
            var id = Arg.GetString(req, "plan");
            try
            {
                switch (cmd)
                {
                    case "health":
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true, ["version"] = Version, ["pid"] = (long)Process.GetCurrentProcess().Id,
                            ["active_s"] = (long)(DateTime.UtcNow - _started).TotalSeconds,
                            ["queue"] = (long)_pq.Queued, ["capability"] = (long)_o.QueueCapacity, ["workers"] = (long)_o.Workers,
                            ["running_now"] = _slots.Keys.OrderBy(x => x).Cast<object>().ToList(),
                            ["plans"] = (long)Directory.GetDirectories(PlansDir).Length, ["stopping"] = _stopping.IsCancellationRequested,
                        };
                    case "send": return Submit(req);
                    case "list":
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["plans"] = Directory.GetDirectories(PlansDir).OrderBy(x => x).Select(d =>
                            {
                                var v = Inspect(Path.GetFileName(d), out var dg);
                                return (object)(Path.GetFileName(d) + " " + (v == null ? "not_recoverable" : Compact.Summary(v.Value.Plan, v.Value.State)));
                            }).ToList(),
                        };
                    case "shutdown":
                        Log("shutdown_requested");
                        _life.StopApplication();
                        return new Dictionary<string, object> { ["ok"] = true, ["detail"] = "stopping between operations" };
                    case "pause": case "cancel": return Control(id, cmd);
                    case "resume": return Resume(id);
                }
                if (id == null) return Error("invalid_request", "missing plan (or unknown command: " + cmd + ")");
                if (!Directory.Exists(PlanDir(id))) return Error("unknown_plan", id);
                var view = Inspect(id, out var diags);
                if (view == null) return Error(ErrorClass.CorruptState, string.Join("; ", diags.Where(d => d.Severity == "error")));
                var (plan, st) = view.Value;
                switch (cmd)
                {
                    case "summary": return Ok(Compact.Summary(plan, st) + (_slots.ContainsKey(id) ? " · running" : ""));
                    case "next": return Ok(Compact.NextText(plan, st));
                    case "exceptions": return Ok(Compact.Exceptions(plan, st, (int)(Arg.GetLong(req, "max") ?? 20)));
                    case "changes": return Ok(Compact.ChangesSince(plan, st, Arg.GetLong(req, "from") ?? 0, (int)(Arg.GetLong(req, "max") ?? 50)));
                    case "misses": return Ok(Compact.VerificationFailures(plan, st));
                    case "report":
                        var rep = Compact.Report(PlanDir(id), plan, st);
                        rep["ok"] = true;
                        return rep;
                    default: return Error("unknown_command", cmd);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException || ex is ArgumentException || ex is KeyNotFoundException)
            {
                return Error(ErrorClass.Internal, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Dictionary<string, object> Ok(string text) => new Dictionary<string, object> { ["ok"] = true, ["text"] = text };

        /// <summary>Receives an IR plan (JSON object) and the simulated model to run it on. Idempotent by plan hash.</summary>
        private Dictionary<string, object> Submit(Dictionary<string, object> req)
        {
            if (!(req.TryGetValue("ir", out var p) && p is Dictionary<string, object> pd)) return Error("invalid_request", "missing ir (the plan)");
            var errs = IrValidator.Validate(PlanDraft.FromJson(pd), out var vp);
            if (errs.Count > 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = new Dictionary<string, object> { ["class"] = "plan_invalid", ["message"] = errs.Count + " errors" }, ["errors"] = errs.Take(20).Cast<object>().ToList() };
            var dir = PlanDir(vp.Hash);
            var existed = File.Exists(PlanFile.PathIn(dir));
            if (!existed)
            {
                Directory.CreateDirectory(dir);
                var mdir = Path.Combine(dir, "model");
                if (!File.Exists(LegacyAliases.ResolveExisting(mdir, Path.Combine(mdir, "model.json"))))
                {
                    if (!(req.TryGetValue("model", out var m) && m is Dictionary<string, object> md)) return Error("invalid_request", "missing model (simulated: elements and config)");
                    DurableSimModel.Create(mdir, vp.DocKey, Arg.GetString(md, "fingerprint") ?? vp.DocPrint, Arg.GetStringList(md, "elements"), md.TryGetValue("config", out var c) ? c as Dictionary<string, object> : null);
                }
                PlanFile.Write(dir, vp);
                Log("plan_received", new Dictionary<string, object> { ["plan"] = vp.Hash, ["operations"] = (long)vp.Ops.Count });
            }
            if (!Enqueue(vp.Hash)) return Error("queue_full", "the queue holds " + _o.QueueCapacity + " plans; retry later");
            return new Dictionary<string, object> { ["ok"] = true, ["plan"] = vp.Hash, ["already_existed"] = existed, ["operations"] = (long)vp.Ops.Count };
        }

        private Dictionary<string, object> Control(string id, string cmd)
        {
            if (id == null || !Directory.Exists(PlanDir(id))) return Error("unknown_plan", id);
            if (_slots.TryGetValue(id, out var slot))
            {
                slot.After = cmd;
                slot.Cts.Cancel();     // stops BETWEEN operations; the worker applies the command with the lease it already holds
                return new Dictionary<string, object> { ["ok"] = true, ["detail"] = cmd + " when the current operation finishes" };
            }
            var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "model"));
            using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model), new RunnerOptions { LeaseTtl = _o.LeaseTtl }, out var diags))
            {
                if (r == null) return Error(ErrorClass.CorruptState, string.Join("; ", diags));
                if (!r.IsOwner) return Error(ErrorClass.PlanInUse, "another process owns the plan");
                if (cmd == "pause") r.Pause(); else r.Cancel();
                return Ok(Compact.Summary(r.Plan, r.State));
            }
        }

        private Dictionary<string, object> Resume(string id)
        {
            if (id == null || !Directory.Exists(PlanDir(id))) return Error("unknown_plan", id);
            if (_slots.TryGetValue(id, out var running))
            {
                if (running.After == "cancel") return Error("cancellation_running", "the plan is being cancelled; not resuming");
                running.After = null;          // cancels a pending "pause"
                Enqueue(id);                   // repeats when the current operation finishes
                return Ok("already running; it will continue");
            }
            var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "model"));
            using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model), new RunnerOptions { LeaseTtl = _o.LeaseTtl }, out var diags))
            {
                if (r == null) return Error(ErrorClass.CorruptState, string.Join("; ", diags));
                if (!r.IsOwner) return Error(ErrorClass.PlanInUse, "another process owns the plan");
                r.Resume();
            }
            return Enqueue(id) ? Ok("resumed") : Error("queue_full", "resumed but the queue is full; retry");
        }

        // ---------------- transport ----------------

        private async Task PipeLoop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(_o.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                    var s = server; server = null;
                    _ = Task.Run(() => Serve(s, stop));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex) { Log("pipe_error", new Dictionary<string, object> { ["detail"] = ex.Message }); await Task.Delay(200, stop).ContinueWith(_ => { }).ConfigureAwait(false); }
                finally { server?.Dispose(); }
            }
        }

        private async Task Serve(NamedPipeServerStream s, CancellationToken stop)
        {
            using (s)
            using (var reader = new StreamReader(s, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(s, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        Dictionary<string, object> resp;
                        try { resp = Handle(Json.ParseObject(line)); }
                        catch (FormatException ex) { resp = Error("invalid_json", ex.Message); }
                        await writer.WriteLineAsync(Json.Write(resp)).ConfigureAwait(false);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public static class ServiceMain
    {
        /// <summary>Builds the host (for the console and for the tests).</summary>
        public static IHost Build(ServiceOptions o) =>
            new HostBuilder()
                .ConfigureServices(s =>
                {
                    s.AddSingleton(o);
                    s.AddSingleton<AgentHostService>();
                    s.AddHostedService(sp => sp.GetRequiredService<AgentHostService>());
                    s.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(60));
                })
                .UseConsoleLifetime(c => c.SuppressStatusMessages = true)
                .Build();

        public static int Run(string root, string pipe, int capacity, int workers)
        {
            Directory.CreateDirectory(root);
            var ttl = Environment.GetEnvironmentVariable("BIMBRIDGE_AH_LEASE_MS");
            var o = new ServiceOptions
            {
                Root = Path.GetFullPath(root), Pipe = pipe, QueueCapacity = capacity, Workers = workers,
                LeaseTtl = ttl == null ? (TimeSpan?)null : TimeSpan.FromMilliseconds(double.Parse(ttl, CultureInfo.InvariantCulture)),
            };
            Console.WriteLine("agenthost " + AgentHostService.Version + " · root " + o.Root + " · pipe " + pipe + " · Ctrl+C to stop between operations");
            Build(o).Run();
            return 0;
        }
    }

    public static class ServiceClient
    {
        public static string Send(string pipe, string json, TimeSpan timeout)
        {
            using (var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly))
            {
                c.Connect((int)timeout.TotalMilliseconds);
                var w = new StreamWriter(c, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
                var r = new StreamReader(c, new UTF8Encoding(false), false, 4096, true);
                w.WriteLine(json.Replace("\r", "").Replace("\n", " "));
                var line = r.ReadLine();
                return line ?? Json.Write(AgentHostService.Error("no_response", "the service closed the connection"));
            }
        }

        public static Dictionary<string, object> Call(string pipe, Dictionary<string, object> req, TimeSpan? timeout = null) =>
            Json.ParseObject(Send(pipe, Json.Write(req), timeout ?? TimeSpan.FromSeconds(30)));
    }
}
