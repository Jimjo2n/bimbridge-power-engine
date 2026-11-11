// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Utilidades de las pruebas del AgentHost 0.7.0-alpha1: planes, modelos y procesos hijos REALES.</summary>
    internal static class AH
    {
        public const string Doc = "archivo:c:\\practica.rvt";
        public const string Print = "huella-practica-0001";
        public const string Cat = "OST_LightingFixtures";
        public static readonly string SrcSha = new string('a', 64);

        public static string NewDir(string tag = "ah") { var d = Path.Combine(Path.GetTempPath(), "pdi-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(d); return d; }

        public static OpDraft Set(string param, object value, string unit = "mm", string cat = Cat, string level = null, long? expectElems = null, long? expectChanged = null, bool allowEmpty = false, params string[] deps)
        {
            var op = new OpDraft { Operation = "fijar_parametro", AllowEmpty = allowEmpty };
            op.Params["parametro"] = param; op.Params["valor"] = value; if (unit != null) op.Params["unidades"] = unit;
            if (cat != null) op.Scope["categoria"] = cat;
            if (level != null) op.Scope["nivel"] = level;
            if (expectElems != null) op.Expected["elementos"] = expectElems.Value;
            if (expectChanged != null) op.Expected["cambiados"] = expectChanged.Value;
            op.DependsOn.AddRange(deps);
            return op;
        }

        public static OpDraft Query(string cat = Cat) { var op = new OpDraft { Operation = "consultar" }; op.Params["categoria"] = cat; return op; }
        public static OpDraft Qc(params string[] deps) { var op = new OpDraft { Operation = "verificar_qc" }; op.Params["receta"] = "qc_manifiesto"; op.DependsOn.AddRange(deps); return op; }

        public static PlanDraft Plan(params OpDraft[] ops)
        {
            var p = new PlanDraft { Objective = "prueba", DocKey = Doc, DocPrint = Print, LengthUnit = "mm", Coordinates = "interno_revit" };
            p.Sources.Add(new SourceDoc { Id = "src-1", Nombre = "especificacion.txt", Sha256 = SrcSha });
            p.Ops.AddRange(ops);
            return p.AssignIds();
        }

        /// <summary>Plan base de AH-01: consultar → fijar Mounting Height (espera 5 elementos y 5 cambios) → QC.</summary>
        public static PlanDraft BasePlan(long expect = 5) => Plan(Query(), Set("Mounting Height", 2700L, expectElems: expect, expectChanged: expect, level: "L03", deps: new[] { "#0" }), Qc("#1"));

        public static string WritePlan(string dir, PlanDraft p) { var f = Path.Combine(dir, "plan_entrada.json"); File.WriteAllText(f, Json.Write(p.ToJson())); return f; }

        public static string Model(string dir, int fixturesL03 = 5, Dictionary<string, object> config = null, params string[] extra)
        {
            var m = Path.Combine(dir, "modelo");
            DurableSimModel.Create(m, Doc, Print, new[] { Cat + "=" + fixturesL03 + "@L03" }.Concat(extra), config);
            return m;
        }

        public static string Dll => Path.Combine(AppContext.BaseDirectory, "PDI.RevitMcp.AgentHost.dll");

        public sealed class ChildResult { public int Code; public string Out; public string Err; public bool Killed => Code == 137 || Code == -1 || Code == 134; public override string ToString() => "code=" + Code + "\n" + Out + Err; }

        /// <summary>Lanza el AgentHost como PROCESO INDEPENDIENTE (dotnet …AgentHost.dll). <paramref name="fault"/>: PDI_AH_FALLO.</summary>
        public static ChildResult Run(string fault, string markFile, params string[] args) => Start(fault, markFile, args).Wait();

        /// <summary>Como <see cref="Run"/> con variables de entorno adicionales (p. ej. PDI_AH_RETRASO para un retraso controlado).</summary>
        public static ChildResult RunWithEnv(IDictionary<string, string> env, string fault, string markFile, params string[] args)
        {
            ExtraEnv = env;
            try { return Start(fault, markFile, args).Wait(); }
            finally { ExtraEnv = null; }
        }

        [ThreadStatic] private static IDictionary<string, string> ExtraEnv;

        /// <summary>
        /// AUD-04: separa la inyección de caídas de la expiración. Tras una caída, el proceso muerto no libera su lease; en vez de esperar a que
        /// venza (lo que obliga a usar TTL muy cortos que la carga de la máquina puede agotar antes del punto de caída), se adelanta el
        /// vencimiento en el fixture de disco. Sólo es válido con el dueño ya terminado: se comprueba que el lease no esté liberado.
        /// </summary>
        public static void ExpireDeadLease(string stateDir)
        {
            var path = Path.Combine(stateDir, "control", "plan", "lease.json");
            if (!File.Exists(path)) return;
            var lease = Json.ParseObject(File.ReadAllText(path));
            if (Arg.GetBool(lease, "liberada") == true) return;
            lease["vence"] = "2000-01-01T00:00:00Z";
            File.WriteAllText(path, Json.Write(lease));
        }

        public sealed class Child
        {
            public Process P; public StringBuilder Out = new StringBuilder(), Err = new StringBuilder();
            public ChildResult Wait(int ms = 120000)
            {
                if (!P.WaitForExit(ms)) { try { P.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("el proceso hijo no terminó: " + Out); }
                P.WaitForExit();
                return new ChildResult { Code = P.ExitCode, Out = Out.ToString(), Err = Err.ToString() };
            }
        }

        public static Child Start(string fault, string markFile, params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Dll);
            foreach (var x in args) psi.ArgumentList.Add(x);
            psi.Environment.Remove("PDI_AH_FALLO");
            if (fault != null) psi.Environment["PDI_AH_FALLO"] = fault;
            if (markFile != null) psi.Environment["PDI_AH_FALLO_MARCA"] = markFile;
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment.Remove("PDI_AH_RETRASO");
            if (ExtraEnv != null) foreach (var kv in ExtraEnv) psi.Environment[kv.Key] = kv.Value;
            var c = new Child { P = new Process { StartInfo = psi } };
            c.P.OutputDataReceived += (s, e) => { if (e.Data != null) lock (c.Out) c.Out.AppendLine(e.Data); };
            c.P.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (c.Err) c.Err.AppendLine(e.Data); };
            c.P.Start();
            c.P.BeginOutputReadLine();
            c.P.BeginErrorReadLine();
            return c;
        }

        public static List<Dictionary<string, object>> Journal(string stateDir) =>
            File.ReadAllLines(Path.Combine(stateDir, "diario.jsonl")).Where(l => l.Length > 0).Select(Json.ParseObject).ToList();

        public static int Transitions(string stateDir, string op, string to) =>
            Journal(stateDir).Count(r => Arg.GetString(r, "ev") == "transicion" && Arg.GetString(r, "op") == op && Arg.GetString(r, "a") == to);

        public static List<Dictionary<string, object>> Notes(string stateDir, string op) =>
            Journal(stateDir).Where(r => Arg.GetString(r, "ev") == "nota" && Arg.GetString(r, "op") == op).ToList();

        /// <summary>Recupera el estado desde disco en un objeto nuevo (sólo lectura).</summary>
        public static ExecJournal Read(string stateDir, out ValidatedPlan plan, out List<Diagnostic> diags)
        {
            diags = new List<Diagnostic>();
            plan = PlanFile.Read(stateDir, diags);
            return plan == null ? null : ExecJournal.Recover(stateDir, plan, false, null, diags);
        }

        public static string StateOf(string stateDir, string op) => Read(stateDir, out _, out _).Ops[op].State;

        /// <summary>Transacciones confirmadas de la operación en el modelo, leídas por un modelo recién abierto desde disco.</summary>
        public static long TxCount(string modelDir, string planHash, string op) => DurableSimModel.Open(modelDir).TxFor(op, planHash).Count;

        /// <summary>Un proceso muerto no libera su lease: el siguiente sólo puede tomar el plan cuando vence (AH-04). Espera a ese momento.</summary>
        public static void WaitLeaseExpiry(string stateDir)
        {
            if (!Directory.Exists(stateDir)) return;
            var l = PlanLease.Peek(stateDir);
            if (l == null || l.Released) return;
            if (DateTime.TryParse(l.Expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var e))
            {
                var wait = e - DateTime.UtcNow + TimeSpan.FromMilliseconds(60);
                if (wait > TimeSpan.Zero) System.Threading.Thread.Sleep(wait);
            }
        }

        public static string Hash(PlanDraft p) { IrValidator.Validate(p, out var vp); return vp.Hash; }
    }
}
