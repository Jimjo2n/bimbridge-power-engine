// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Bimbridge.AgentHost 0.7.0-alpha3 — local orchestrator. By default it writes to the durable simulated model; with `revit` and --revit-pid it talks to a specific Revit through the typed bridge (read, trial or, with the operator's explicit authorization, persistent).
    ///   validar   &lt;plan.json&gt;
    ///   modelo    crear &lt;dir&gt; --clave K --huella H [--elementos Cat=N[@Nivel] ...] [--config JSON]
    ///   modelo    compactar &lt;dir&gt;
    ///   ejecutar  &lt;plan.json|-&gt; &lt;estado&gt; --modelo &lt;dir&gt; [--checkpoint N] [--lease-ms N]
    ///   estado    &lt;estado&gt; [resumen|siguiente|excepciones|cambios N|fallos|informe]   (read-only, no lease)
    ///   pausar | reanudar | cancelar &lt;estado&gt; --modelo &lt;dir&gt;
    ///   servicio  &lt;raiz&gt; [--tuberia nombre] [--cola N] [--trabajadores N]
    ///   cliente   &lt;tuberia&gt; '&lt;json&gt;'
    ///   documento &lt;archivo.txt&gt; --clave K --huella H [--salida plan.json]
    ///   escenario &lt;dir&gt; [--n 1000]
    ///   banco-kernel [--puntos N] [--triangulos M]
    /// Exit codes: 0 all verified · 2 finished with exceptions · 3 plan in use by another process · 4 lease lost ·
    /// 5 paused/cancelled · 1 error (invalid plan, corrupt state, different document…) · 64 usage.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] a)
        {
            try { return Dispatch(a); }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException || ex is ArgumentException)
            {
                Console.Error.WriteLine("error: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        internal static string Opt(string[] a, string name, string def = null)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        internal static List<string> Multi(string[] a, string name)
        {
            var res = new List<string>();
            var i = Array.IndexOf(a, name);
            if (i < 0) return res;
            for (var k = i + 1; k < a.Length && !a[k].StartsWith("--", StringComparison.Ordinal); k++) res.Add(a[k]);
            return res;
        }

        private static void PrintDiags(IEnumerable<Diagnostic> ds) { foreach (var d in ds) Console.WriteLine("diag " + d); }

        private static int Dispatch(string[] a)
        {
            var cmd = a.Length > 0 ? a[0] : "";
            switch (cmd)
            {
                case "validar":
                {
                    var errs = IrValidator.Validate(PlanDraft.Parse(File.ReadAllText(a[1])), out var vp);
                    Console.WriteLine(errs.Count == 0 ? "valid " + vp.Hash + " · " + vp.Ops.Count + " operations" : string.Join(Environment.NewLine, errs));
                    return errs.Count == 0 ? 0 : 1;
                }
                case "modelo":
                {
                    if (a.Length >= 3 && a[1] == "crear")
                    {
                        var cfg = Opt(a, "--config");
                        DurableSimModel.Create(a[2], Opt(a, "--clave"), Opt(a, "--huella"), Multi(a, "--elementos"), cfg == null ? null : Json.ParseObject(cfg));
                        Console.WriteLine("model created " + a[2]);
                        return 0;
                    }
                    if (a.Length >= 3 && a[1] == "compactar") { DurableSimModel.Open(a[2]).Compact(); Console.WriteLine("compacted"); return 0; }
                    break;
                }
                case "ejecutar":
                    if (a.Length >= 3) return Execute(a);
                    break;
                case "estado":
                    if (a.Length >= 2) return Inspect(a);
                    break;
                case "pausar":
                case "reanudar":
                case "cancelar":
                    if (a.Length >= 2) return Control(a);
                    break;
                case "servicio":
                    if (a.Length >= 2) return ServiceMain.Run(a[1], Opt(a, "--tuberia", "Bimbridge.AgentHost." + Environment.UserName), int.Parse(Opt(a, "--cola", "16")), int.Parse(Opt(a, "--trabajadores", "2")));
                    break;
                case "cliente":
                    if (a.Length >= 3) { Console.WriteLine(ServiceClient.Send(a[1], a[2], TimeSpan.FromSeconds(30))); return 0; }
                    break;
                case "documento":
                    if (a.Length >= 2) return DocumentCli.Run(a);
                    break;
                case "plan-store":
                    if (a.Length >= 2) return PlanStoreCli.Run(a);
                    break;
                case "escenario":
                    if (a.Length >= 2) { Console.WriteLine(Scenario.Generate(a[1], int.Parse(Opt(a, "--n", "1000")))); return 0; }
                    break;
                case "revit":
                    if (a.Length >= 2) return RevitCli.Run(a);
                    break;
                case "paquete-actualizacion":
                {
                    // 0.8.0: ZIP for "Update from ZIP". Without --clave-privada it is UNSIGNED (the user will have to confirm it explicitly).
                    var files = new Dictionary<string, string> { ["Bimbridge.Setup.exe"] = Opt(a, "--setup") };
                    foreach (var x in Multi(a, "--extra")) files[Path.GetFileName(x)] = x;
                    System.Security.Cryptography.RSA signer = null;
                    var keyFile = Opt(a, "--clave-privada");
                    if (keyFile != null) { signer = System.Security.Cryptography.RSA.Create(); signer.FromXmlString(File.ReadAllText(keyFile)); }
                    var m = Bimbridge.Setup.ZipUpdate.Build(Opt(a, "--salida"), files, Opt(a, "--version"), Opt(a, "--commit"),
                        (Opt(a, "--revit", "2022,2023,2024,2025,2026,2027")).Split(','), Opt(a, "--desde-minima"), signer);
                    Console.WriteLine(Json.Write(m));
                    return 0;
                }
                case "generar-clave":
                {
                    // Generates an RSA-3072 key pair. The private key is saved where --salida says and NEVER goes into the repository; the public key goes into config.json (claves_actualizacion).
                    using (var rsa = System.Security.Cryptography.RSA.Create(3072))
                    {
                        File.WriteAllText(Opt(a, "--salida"), rsa.ToXmlString(true));
                        Console.WriteLine(Bimbridge.Setup.TrustedKey.Format(rsa.ExportParameters(false)));
                    }
                    return 0;
                }
                case "power-engine":
                    return PowerEngineCli(a);
                case "banco-kernel":
                    Console.WriteLine(Json.Write(MountKernelBench.Run(int.Parse(Opt(a, "--puntos", "20000")), int.Parse(Opt(a, "--triangulos", "20000")), int.Parse(Opt(a, "--rondas", "5")))));
                    return 0;
            }
            Console.Error.WriteLine("usage: validar | modelo crear|compactar | ejecutar [--revit-pid N --escritura lectura|ensayo|persistente] | revit instancias|identidad|nrt04 | estado | pausar | reanudar | cancelar | servicio | cliente | documento | escenario | banco-kernel | power-engine estado|bench|calibrar | paquete-actualizacion | generar-clave");
            return 64;
        }

        /// <summary>power-engine estado | bench [--rondas 5 --docx f --pdf f --salida f.json] | calibrar --salida calibracion.json</summary>
        private static int PowerEngineCli(string[] a)
        {
            var baseDir = AppContext.BaseDirectory;
            var cfg = Bimbridge.Engine.EngineConfig.Load();
            var opt = BimBridge.PowerEngine.PowerCommands.OptionsFrom(cfg, baseDir);
            opt.PythonExe = Opt(a, "--python", opt.PythonExe); opt.PythonWorkerScript = Opt(a, "--worker-python", opt.PythonWorkerScript); opt.RustWorkerExe = Opt(a, "--rust-worker", opt.RustWorkerExe);
            var sub = a.Length > 1 ? a[1] : "estado";
            if (sub == "estado")
            {
                using (var pe = new BimBridge.PowerEngine.PowerEngineDispatcher(opt)) Console.WriteLine(Json.Write(pe.Status()));
                return 0;
            }
            if (sub == "bench" || sub == "calibrar")
            {
                var s = new BimBridge.PowerEngine.PowerEngineBench.Settings
                {
                    Rounds = int.Parse(Opt(a, "--rondas", "5")), RustWorkerExe = opt.RustWorkerExe, PythonExe = opt.PythonExe, PythonWorkerScript = opt.PythonWorkerScript,
                    DocxPath = Opt(a, "--docx"), PdfPath = Opt(a, "--pdf"), IncludeIpc = sub == "bench",
                };
                var r = BimBridge.PowerEngine.PowerEngineBench.Run(s);
                var outText = Json.Write(sub == "calibrar" ? r["calibracion"] : r);
                var path = Opt(a, "--salida");
                if (path != null) File.WriteAllText(path, outText);
                Console.WriteLine(outText);
                return 0;
            }
            if (sub == "flujo-pdf")
            {
                // power-engine flujo-pdf --pdf f.pdf --pagina 1 --nivel "LEVEL 09" --reglas reglas.json --ejes ejes_modelo_090.json --salida DIR
                // Writes DIR/plan.json (summary) and DIR/ops.json (list of place ops for plan→commit; campaign: --var ops=@DIR/ops.json).
                var pdf = Opt(a, "--pdf");
                var folder = Path.GetDirectoryName(Path.GetFullPath(pdf));
                var q = new BimBridge.PowerEngine.Workflows.PdfToPlan.Request
                {
                    PdfPath = pdf, Page = int.Parse(Opt(a, "--pagina", "1")), Level = Opt(a, "--nivel"),
                    Rules = BimBridge.PowerEngine.Workflows.PdfToPlan.RulesFromJson((List<object>)Json.Parse(File.ReadAllText(Opt(a, "--reglas")))),
                    ModelGrids = BimBridge.PowerEngine.Workflows.PdfToPlan.GridsFromJson((List<object>)Json.Parse(File.ReadAllText(Opt(a, "--ejes")))),
                };
                using (var pe = new BimBridge.PowerEngine.PowerEngineDispatcher(opt))
                {
                    var r = BimBridge.PowerEngine.Workflows.PdfToPlan.Run(pe, q, new[] { folder });
                    var dir = Opt(a, "--salida", ".");
                    Directory.CreateDirectory(dir);
                    var ops = r.TryGetValue("ops", out var o) && o is List<object> ol ? ol.Select(x => Json.Parse((string)x)).ToList() : new List<object>();
                    r.Remove("ops");
                    File.WriteAllText(Path.Combine(dir, "plan.json"), Json.Write(r));
                    File.WriteAllText(Path.Combine(dir, "ops.json"), Json.Write(ops));
                    Console.WriteLine(Json.Write(new Dictionary<string, object> { ["estado"] = r["estado"], ["ops"] = (long)ops.Count, ["salida"] = Path.GetFullPath(dir) }));
                    return Arg.GetString(r, "estado")?.StartsWith("listo", StringComparison.Ordinal) == true ? 0 : 3;
                }
            }
            Console.Error.WriteLine("usage: power-engine estado | flujo-pdf --pdf f --nivel N --reglas r.json --ejes e.json --salida DIR | bench [--rondas N --docx f --pdf f --salida f.json] | calibrar [--salida f.json]");
            return 64;
        }

        private static RunnerOptions Options(string[] a) => new RunnerOptions
        {
            Faults = Faults.Current,
            CheckpointEvery = int.Parse(Opt(a, "--checkpoint", "64")),
            LeaseTtl = Opt(a, "--lease-ms") == null ? (TimeSpan?)null : TimeSpan.FromMilliseconds(double.Parse(Opt(a, "--lease-ms"))),
        };

        private static int Execute(string[] a)
        {
            var draft = a[1] == "-" ? null : PlanDraft.Parse(File.ReadAllText(a[1]));
            IExecutionBackend backend;
            var opt = Options(a);
            RevitBackend revit = null;
            if (Opt(a, "--revit-pid") != null)
            {
                if (NativeGate.Check(a) is int blocked) return blocked;
                // alpha3: real Revit. The operator chooses pid and mode; the plan cannot widen them.
                var mode = Opt(a, "--escritura", "lectura");
                var link = RevitLink.ForPid(int.Parse(Opt(a, "--revit-pid")), out var lerr);
                if (link == null) { Console.WriteLine("error " + lerr); return 1; }
                revit = RevitBackend.Open(link, mode, out var berr);
                if (revit == null) { link.Dispose(); Console.WriteLine("error " + berr); return 1; }
                if (mode != "lectura") opt.RealWrites = mode;
                var calls = Opt(a, "--llamadas");
                if (calls != null) link.OnCall = c => File.AppendAllText(calls, Json.Write(c) + "\n");
                backend = revit;
            }
            else
            {
                var model = DurableSimModel.Open(Opt(a, "--modelo") ?? throw new ArgumentException("missing --modelo or --revit-pid"));
                PrintDiags(model.OpenDiagnostics);
                backend = new SimulatedBackend(model, Faults.Current);
            }
            using (revit)
            using (var r = PlanRunner.Open(a[2], draft, backend, opt, out var diags))
            {
                PrintDiags(diags);
                if (r == null) return 1;
                if (!r.IsOwner) { Console.WriteLine("plan_en_uso " + Compact.Summary(r.Plan, r.State)); return 3; }
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                string summary;
                try { summary = r.Run(cts.Token); }
                catch (PlanLease.LeaseLostException ex) { Console.WriteLine("lease_perdido " + ex.Message); return 4; }
                Console.WriteLine(summary);
                if (r.State.PlanState != PlanRunState.Activo) return 5;
                return r.State.Ops.Values.All(o => o.State == OpState.Verificada || o.State == OpState.Ensayada) ? 0 : 2;
            }
        }

        private static int Inspect(string[] a)
        {
            var diags = new List<Diagnostic>();
            var plan = PlanFile.Read(a[1], diags);
            if (plan == null) { PrintDiags(diags); return 1; }
            var st = ExecJournal.Recover(a[1], plan, false, null, diags);
            PrintDiags(diags.Where(d => d.Severity != "info"));
            if (st == null) return 1;
            var what = a.Length > 2 ? a[2] : "resumen";
            switch (what)
            {
                case "siguiente": Console.WriteLine(Compact.NextText(plan, st)); break;
                case "excepciones": Console.WriteLine(Compact.Exceptions(plan, st)); break;
                case "cambios": Console.WriteLine(Compact.ChangesSince(plan, st, long.Parse(a.Length > 3 ? a[3] : "0"))); break;
                case "fallos": Console.WriteLine(Compact.VerificationFailures(plan, st)); break;
                case "informe": Console.WriteLine(Json.Write(Compact.Report(a[1], plan, st))); break;
                default:
                    var holder = PlanLease.Peek(a[1]);
                    Console.WriteLine(Compact.Summary(plan, st) + (holder != null && holder.Valid(DateTime.UtcNow) ? " · owner " + holder.Owner + " f" + holder.Fencing : ""));
                    break;
            }
            return 0;
        }

        private static int Control(string[] a)
        {
            var model = DurableSimModel.Open(Opt(a, "--modelo") ?? throw new ArgumentException("missing --modelo"));
            using (var r = PlanRunner.Open(a[1], null, new SimulatedBackend(model), Options(a), out var diags))
            {
                PrintDiags(diags);
                if (r == null) return 1;
                if (!r.IsOwner) return 3;
                if (a[0] == "pausar") r.Pause(); else if (a[0] == "reanudar") r.Resume(); else r.Cancel();
                Console.WriteLine(Compact.Summary(r.Plan, r.State));
                return 0;
            }
        }
    }
}
