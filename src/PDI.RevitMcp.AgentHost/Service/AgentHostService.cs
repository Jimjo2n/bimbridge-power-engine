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
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
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
    /// Servicio de ejecución persistente (P1 §3), alojado con Microsoft.Extensions.Hosting y ejecutable desde la consola (no instala un
    /// servicio de Windows ni toca el sistema). Cola acotada, N trabajadores, cada plan con su lease (dos servicios no ejecutan el mismo
    /// plan), pausa/reanudación/cancelación persistidas en el diario del plan, recuperación al arrancar, apagado ordenado (para ENTRE
    /// operaciones y deja checkpoint), errores estructurados, salud y consultas compactas. No depende de ninguna conversación: todo lo que
    /// necesita está en la carpeta raíz.
    ///
    /// Raíz: planes/&lt;hash&gt;/ (plan.json, diario.jsonl, estado.json, control/, modelo/ simulado) y servicio.jsonl (registro del servicio).
    /// Transporte: named pipe local (PipeOptions.CurrentUserOnly), una línea JSON por petición y por respuesta.
    /// </summary>
    public sealed class AgentHostService : BackgroundService
    {
        /// <summary>rc1: versión y commit salen del ensamblado (Directory.Build.props + SourceRevisionId); nunca un literal.</summary>
        public static readonly string Version = BuildInfo.Version;
        private readonly ServiceOptions _o;
        private readonly IHostApplicationLifetime _life;
        private readonly ConcurrentDictionary<string, Slot> _slots = new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);
        private readonly PlanQueue _pq;   // alpha3 (P1-B), ver PlanQueue
        private readonly object _logLock = new object();
        private readonly DateTime _started = DateTime.UtcNow;
        private CancellationToken _stopping;

        private sealed class Slot
        {
            public CancellationTokenSource Cts;
            public string After;   // "pausar" | "cancelar" | null: lo que se hace al terminar la operación en curso
        }

        public AgentHostService(ServiceOptions o, IHostApplicationLifetime life)
        {
            _o = o; _life = life;
            Directory.CreateDirectory(PlansDir);
            _pq = new PlanQueue(o.QueueCapacity) { OnLost = (id, why) => Log("reprogramacion_no_encolada", new Dictionary<string, object> { ["plan"] = id, ["motivo"] = why }) };
        }

        private string PlansDir => Path.Combine(_o.Root, "planes");
        private string PlanDir(string id) => Path.Combine(PlansDir, id);

        private void Log(string ev, Dictionary<string, object> data = null)
        {
            var d = new Dictionary<string, object> { ["t"] = Home.Now(), ["ev"] = ev };
            if (data != null) foreach (var kv in data) d[kv.Key] = kv.Value;
            lock (_logLock) File.AppendAllText(Path.Combine(_o.Root, "servicio.jsonl"), Json.Write(d) + "\n");
        }

        // ---------------- ciclo de vida ----------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stopping = stoppingToken;
            Log("inicio", new Dictionary<string, object> { ["version"] = Version, ["pid"] = (long)Process.GetCurrentProcess().Id, ["tuberia"] = _o.Pipe, ["cola"] = (long)_o.QueueCapacity, ["trabajadores"] = (long)_o.Workers });
            // recuperación al arrancar: todo plan activo con trabajo pendiente vuelve a la cola
            foreach (var d in Directory.GetDirectories(PlansDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(d);
                var view = Inspect(id, out var diags);
                if (view == null) { Log("recuperacion_rechazada", new Dictionary<string, object> { ["plan"] = id, ["diag"] = string.Join("; ", diags) }); continue; }
                if (view.Value.State.PlanState == PlanRunState.Activo && view.Value.State.Ops.Values.Any(o => !OpState.IsFinal(o.State)))
                {
                    Log("recuperacion", new Dictionary<string, object> { ["plan"] = id, ["resumen"] = Compact.Summary(view.Value.Plan, view.Value.State) });
                    Enqueue(id);
                }
            }
            var workers = Enumerable.Range(0, Math.Max(1, _o.Workers)).Select(i => Task.Run(() => _pq.WorkerLoop(id => RunPlan(id, stoppingToken), stoppingToken))).ToList();
            var pipe = Task.Run(() => PipeLoop(stoppingToken));
            await Task.WhenAll(workers.Append(pipe)).ConfigureAwait(false);
            Log("apagado", new Dictionary<string, object> { ["ejecutando_al_parar"] = (long)_slots.Count });
        }

        private bool Enqueue(string id) => _pq.Enqueue(id);

        private void RunPlan(string id, CancellationToken stop)
        {
            var slot = new Slot { Cts = CancellationTokenSource.CreateLinkedTokenSource(stop) };
            if (!_slots.TryAdd(id, slot)) return;    // ya se está ejecutando en este servicio
            try
            {
                var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "modelo"));
                using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model, Faults.Current), new RunnerOptions { Faults = Faults.Current, LeaseTtl = _o.LeaseTtl, CheckpointEvery = _o.CheckpointEvery }, out var diags))
                {
                    if (r == null) { Log("plan_rechazado", new Dictionary<string, object> { ["plan"] = id, ["diag"] = string.Join("; ", diags) }); return; }
                    if (!r.IsOwner)
                    {
                        // otro proceso lo tiene: reintentar cuando venza su lease (si sigue vivo, lo renovará y volveremos a ceder)
                        var wait = Expiry(r.Holder) - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
                        Log("plan_en_uso", new Dictionary<string, object> { ["plan"] = id, ["dueno"] = r.Holder?.Owner, ["reintento_ms"] = (long)Math.Max(0, wait.TotalMilliseconds) });
                        _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100), stop).ContinueWith(t => { if (!t.IsCanceled) Enqueue(id); }, TaskScheduler.Default);
                        return;
                    }
                    Log("ejecucion_inicio", new Dictionary<string, object> { ["plan"] = id, ["fencing"] = r.State.Fencing, ["resumen"] = Compact.Summary(r.Plan, r.State) });
                    string summary;
                    try { summary = r.Run(slot.Cts.Token); }
                    catch (PlanLease.LeaseLostException ex) { Log("lease_perdido", new Dictionary<string, object> { ["plan"] = id, ["detalle"] = ex.Message }); return; }
                    if (slot.After == "pausar") r.Pause();
                    else if (slot.After == "cancelar") { r.Cancel(); summary = r.Run(CancellationToken.None); }
                    Log("ejecucion_fin", new Dictionary<string, object> { ["plan"] = id, ["resumen"] = Compact.Summary(r.Plan, r.State), ["por_parada"] = stop.IsCancellationRequested });
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Log("error", new Dictionary<string, object> { ["plan"] = id, ["clase"] = ex.GetType().Name, ["detalle"] = ex.Message });
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

        // ---------------- peticiones ----------------

        public static Dictionary<string, object> Error(string cls, string msg) => new Dictionary<string, object> { ["ok"] = false, ["error"] = new Dictionary<string, object> { ["clase"] = cls, ["mensaje"] = msg } };

        public Dictionary<string, object> Handle(Dictionary<string, object> req)
        {
            var cmd = Arg.GetString(req, "orden");
            var id = Arg.GetString(req, "plan");
            try
            {
                switch (cmd)
                {
                    case "salud":
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true, ["version"] = Version, ["pid"] = (long)Process.GetCurrentProcess().Id,
                            ["activo_s"] = (long)(DateTime.UtcNow - _started).TotalSeconds,
                            ["cola"] = (long)_pq.Queued, ["capacidad"] = (long)_o.QueueCapacity, ["trabajadores"] = (long)_o.Workers,
                            ["ejecutando"] = _slots.Keys.OrderBy(x => x).Cast<object>().ToList(),
                            ["planes"] = (long)Directory.GetDirectories(PlansDir).Length, ["parando"] = _stopping.IsCancellationRequested,
                        };
                    case "enviar": return Submit(req);
                    case "lista":
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["planes"] = Directory.GetDirectories(PlansDir).OrderBy(x => x).Select(d =>
                            {
                                var v = Inspect(Path.GetFileName(d), out var dg);
                                return (object)(Path.GetFileName(d) + " " + (v == null ? "no_recuperable" : Compact.Summary(v.Value.Plan, v.Value.State)));
                            }).ToList(),
                        };
                    case "apagar":
                        Log("apagado_pedido");
                        _life.StopApplication();
                        return new Dictionary<string, object> { ["ok"] = true, ["detalle"] = "parando entre operaciones" };
                    case "pausar": case "cancelar": return Control(id, cmd);
                    case "reanudar": return Resume(id);
                }
                if (id == null) return Error("peticion_invalida", "falta plan (o orden desconocida: " + cmd + ")");
                if (!Directory.Exists(PlanDir(id))) return Error("plan_desconocido", id);
                var view = Inspect(id, out var diags);
                if (view == null) return Error(ErrorClass.EstadoCorrupto, string.Join("; ", diags.Where(d => d.Severity == "error")));
                var (plan, st) = view.Value;
                switch (cmd)
                {
                    case "resumen": return Ok(Compact.Summary(plan, st) + (_slots.ContainsKey(id) ? " · ejecutando" : ""));
                    case "siguiente": return Ok(Compact.NextText(plan, st));
                    case "excepciones": return Ok(Compact.Exceptions(plan, st, (int)(Arg.GetLong(req, "max") ?? 20)));
                    case "cambios": return Ok(Compact.ChangesSince(plan, st, Arg.GetLong(req, "desde") ?? 0, (int)(Arg.GetLong(req, "max") ?? 50)));
                    case "fallos": return Ok(Compact.VerificationFailures(plan, st));
                    case "informe":
                        var rep = Compact.Report(PlanDir(id), plan, st);
                        rep["ok"] = true;
                        return rep;
                    default: return Error("orden_desconocida", cmd);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException || ex is ArgumentException || ex is KeyNotFoundException)
            {
                return Error(ErrorClass.Interno, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Dictionary<string, object> Ok(string text) => new Dictionary<string, object> { ["ok"] = true, ["texto"] = text };

        /// <summary>Recibe un plan IR (objeto JSON) y el modelo simulado sobre el que ejecutarlo. Idempotente por hash del plan.</summary>
        private Dictionary<string, object> Submit(Dictionary<string, object> req)
        {
            if (!(req.TryGetValue("ir", out var p) && p is Dictionary<string, object> pd)) return Error("peticion_invalida", "falta ir (el plan)");
            var errs = IrValidator.Validate(PlanDraft.FromJson(pd), out var vp);
            if (errs.Count > 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = new Dictionary<string, object> { ["clase"] = "plan_invalido", ["mensaje"] = errs.Count + " errores" }, ["errores"] = errs.Take(20).Cast<object>().ToList() };
            var dir = PlanDir(vp.Hash);
            var existed = File.Exists(PlanFile.PathIn(dir));
            if (!existed)
            {
                Directory.CreateDirectory(dir);
                var mdir = Path.Combine(dir, "modelo");
                if (!File.Exists(Path.Combine(mdir, "modelo.json")))
                {
                    if (!(req.TryGetValue("modelo", out var m) && m is Dictionary<string, object> md)) return Error("peticion_invalida", "falta modelo (simulado: elementos y config)");
                    DurableSimModel.Create(mdir, vp.DocKey, Arg.GetString(md, "huella") ?? vp.DocPrint, Arg.GetStringList(md, "elementos"), md.TryGetValue("config", out var c) ? c as Dictionary<string, object> : null);
                }
                PlanFile.Write(dir, vp);
                Log("plan_recibido", new Dictionary<string, object> { ["plan"] = vp.Hash, ["operaciones"] = (long)vp.Ops.Count });
            }
            if (!Enqueue(vp.Hash)) return Error("cola_llena", "la cola admite " + _o.QueueCapacity + " planes; reintente más tarde");
            return new Dictionary<string, object> { ["ok"] = true, ["plan"] = vp.Hash, ["ya_existia"] = existed, ["operaciones"] = (long)vp.Ops.Count };
        }

        private Dictionary<string, object> Control(string id, string cmd)
        {
            if (id == null || !Directory.Exists(PlanDir(id))) return Error("plan_desconocido", id);
            if (_slots.TryGetValue(id, out var slot))
            {
                slot.After = cmd;
                slot.Cts.Cancel();     // se detiene ENTRE operaciones; el trabajador aplica la orden con el lease que ya tiene
                return new Dictionary<string, object> { ["ok"] = true, ["detalle"] = cmd + " al terminar la operación en curso" };
            }
            var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "modelo"));
            using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model), new RunnerOptions { LeaseTtl = _o.LeaseTtl }, out var diags))
            {
                if (r == null) return Error(ErrorClass.EstadoCorrupto, string.Join("; ", diags));
                if (!r.IsOwner) return Error(ErrorClass.PlanEnUso, "otro proceso es dueño del plan");
                if (cmd == "pausar") r.Pause(); else r.Cancel();
                return Ok(Compact.Summary(r.Plan, r.State));
            }
        }

        private Dictionary<string, object> Resume(string id)
        {
            if (id == null || !Directory.Exists(PlanDir(id))) return Error("plan_desconocido", id);
            if (_slots.TryGetValue(id, out var running))
            {
                if (running.After == "cancelar") return Error("cancelacion_en_curso", "el plan se está cancelando; no se reanuda");
                running.After = null;          // anula un "pausar" pendiente
                Enqueue(id);                   // se repite al terminar la operación en curso
                return Ok("ya se está ejecutando; continuará");
            }
            var model = DurableSimModel.Open(Path.Combine(PlanDir(id), "modelo"));
            using (var r = PlanRunner.Open(PlanDir(id), null, new SimulatedBackend(model), new RunnerOptions { LeaseTtl = _o.LeaseTtl }, out var diags))
            {
                if (r == null) return Error(ErrorClass.EstadoCorrupto, string.Join("; ", diags));
                if (!r.IsOwner) return Error(ErrorClass.PlanEnUso, "otro proceso es dueño del plan");
                r.Resume();
            }
            return Enqueue(id) ? Ok("reanudado") : Error("cola_llena", "reanudado pero la cola está llena; reintente");
        }

        // ---------------- transporte ----------------

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
                catch (IOException ex) { Log("tuberia_error", new Dictionary<string, object> { ["detalle"] = ex.Message }); await Task.Delay(200, stop).ContinueWith(_ => { }).ConfigureAwait(false); }
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
                        catch (FormatException ex) { resp = Error("json_invalido", ex.Message); }
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
        /// <summary>Construye el host (para la consola y para las pruebas).</summary>
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
            var ttl = Environment.GetEnvironmentVariable("PDI_AH_LEASE_MS");
            var o = new ServiceOptions
            {
                Root = Path.GetFullPath(root), Pipe = pipe, QueueCapacity = capacity, Workers = workers,
                LeaseTtl = ttl == null ? (TimeSpan?)null : TimeSpan.FromMilliseconds(double.Parse(ttl, CultureInfo.InvariantCulture)),
            };
            Console.WriteLine("agenthost " + AgentHostService.Version + " · raíz " + o.Root + " · tubería " + pipe + " · Ctrl+C para parar entre operaciones");
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
                return line ?? Json.Write(AgentHostService.Error("sin_respuesta", "el servicio cerró la conexión"));
            }
        }

        public static Dictionary<string, object> Call(string pipe, Dictionary<string, object> req, TimeSpan? timeout = null) =>
            Json.ParseObject(Send(pipe, Json.Write(req), timeout ?? TimeSpan.FromSeconds(30)));
    }
}
