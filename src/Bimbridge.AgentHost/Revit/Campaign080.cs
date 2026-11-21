// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// 0.8.0: generic native campaign harness. Each item (N8-01…N8-13) is a JSON steps file:
    /// { "item", "titulo", "estado_inicial", "pasos": [ { "nombre", "op", "args", "escritura", "esperar", "contiene", "no_contiene", "guardar" } ] }.
    /// - ${var} is replaced by variables from --var name=value or by what earlier steps saved ("guardar": {"plan": "plan_id"}).
    /// - "esperar": expected status (ok, rechazado, invalido…); "contiene"/"no_contiene": texts in the JSON response.
    /// - The document is pinned in the preflight (path = --ruta-esperada, local, non-workshared) and injected into every step: no step picks the document.
    /// Without BIMBRIDGE_REVIT_NATIVE=AUTORIZADO and --revit-disponible nothing runs (NativeGate).
    /// </summary>
    public static class Campaign080
    {
        public const string Schema = "bimbridge.campana080/1";

        public sealed class StepResult
        {
            public string Name, Op, State, Expected, Reason;
            public bool Ok;
            public long Ms;
        }

        /// <summary>Runs the steps with <paramref name="call"/> (op, args, write) → response. Pure: the tests use it without Revit.</summary>
        public static Dictionary<string, object> RunSteps(Dictionary<string, object> spec, Func<string, Dictionary<string, object>, bool, Response> call,
                                                          string docHandle, Dictionary<string, string> vars)
        {
            if (Arg.GetString(spec, "esquema") != Schema) throw new FormatException("schema differs from " + Schema);
            var item = Arg.GetString(spec, "item") ?? throw new FormatException("missing item");
            var steps = spec.TryGetValue("pasos", out var ps) && ps is List<object> pl ? pl.OfType<Dictionary<string, object>>().ToList() : throw new FormatException("missing pasos");
            vars = new Dictionary<string, string>(vars ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            var results = new List<StepResult>();
            var stopped = false;
            foreach (var s in steps)
            {
                var r = new StepResult { Name = Arg.GetString(s, "nombre") ?? ("step " + (results.Count + 1)), Op = Arg.GetString(s, "op"), Expected = Arg.GetString(s, "esperar") ?? Status.Ok };
                results.Add(r);
                if (stopped) { r.Reason = "not run: a previous step failed"; continue; }
                Dictionary<string, object> args;
                try { args = (Dictionary<string, object>)Subst(s.TryGetValue("args", out var a) ? a : new Dictionary<string, object>(), vars); }
                catch (KeyNotFoundException ex) { r.Reason = ex.Message; stopped = true; continue; }
                // the preflight pins the document; only a NEGATIVE step marked doc_explicito keeps its own doc (to check the rejection)
                if (r.Op != Wire.OpContext && r.Op != Wire.OpDocs && !(Arg.GetBool(s, "doc_explicito") ?? false)) args[Wire.DocArg] = docHandle;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = call(r.Op, args, Arg.GetBool(s, "escritura") ?? false);
                r.Ms = sw.ElapsedMilliseconds;
                r.State = resp.State;
                var json = Json.Write(resp.ToJson());
                var problems = new List<string>();
                if (!string.Equals(resp.State, r.Expected, StringComparison.Ordinal)) problems.Add("status " + resp.State + " (expected " + r.Expected + ")" + (resp.Warning != null ? ": " + resp.Warning : ""));
                foreach (var t in Arg.GetStringList(s, "contiene") ?? new List<string>()) { var tt = SubstText(t, vars); if (json.IndexOf(tt, StringComparison.Ordinal) < 0) problems.Add("does not contain '" + tt + "'"); }
                foreach (var t in Arg.GetStringList(s, "no_contiene") ?? new List<string>()) { var tt = SubstText(t, vars); if (json.IndexOf(tt, StringComparison.Ordinal) >= 0) problems.Add("contains '" + tt + "'"); }
                if (s.TryGetValue("guardar", out var g) && g is Dictionary<string, object> gd && resp.Data is Dictionary<string, object> dd)
                    foreach (var kv in gd)
                    {
                        var v = Pick(dd, Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                        if (v == null) problems.Add("could not save " + kv.Key + " from " + kv.Value);
                        else vars[kv.Key] = Convert.ToString(v, CultureInfo.InvariantCulture);
                    }
                r.Ok = problems.Count == 0;
                r.Reason = r.Ok ? null : string.Join("; ", problems);
                if (!r.Ok) stopped = true;
            }
            var pass = results.Count > 0 && results.All(x => x.Ok);
            return new Dictionary<string, object>
            {
                ["item"] = item, ["titulo"] = Arg.GetString(spec, "titulo"), ["resultado"] = pass ? "APROBADA" : "FALLIDA",
                ["pasos"] = results.Select(x => (object)new Dictionary<string, object> { ["nombre"] = x.Name, ["op"] = x.Op, ["estado"] = x.State, ["esperado"] = x.Expected, ["ok"] = x.Ok, ["ms"] = x.Ms, ["motivo"] = x.Reason }).ToList(),
            };
        }

        /// <summary>Simple path a.b.0.c inside a response's data.</summary>
        public static object Pick(object root, string path)
        {
            object cur = root;
            foreach (var part in (path ?? "").Split('.'))
            {
                if (cur is Dictionary<string, object> d && d.TryGetValue(part, out var v)) cur = v;
                else if (cur is List<object> l && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < l.Count) cur = l[i];
                else return null;
            }
            return cur;
        }

        private static readonly Regex Var = new Regex(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant);

        private static string SubstText(string s, Dictionary<string, string> vars) =>
            Var.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new KeyNotFoundException("variable without value: ${" + m.Groups[1].Value + "}"));

        /// <summary>Recursive substitution. A text that is exactly "${x}" and whose value is an integer becomes a number (ids, views).</summary>
        public static object Subst(object v, Dictionary<string, string> vars)
        {
            switch (v)
            {
                case string s:
                    var m = Var.Match(s);
                    if (m.Success && m.Value == s)
                    {
                        var val = SubstText(s, vars);
                        // 0.9.0: a JSON value (list or object, e.g. the ops of power flujo) goes in as a structure, not as text
                        var tv = val.TrimStart();
                        if (tv.StartsWith("[", StringComparison.Ordinal) || tv.StartsWith("{", StringComparison.Ordinal))
                            try { return Json.Parse(val); } catch (FormatException) { /* text as is */ }
                        return long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)n : val;
                    }
                    return SubstText(s, vars);
                case Dictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => Subst(kv.Value, vars));
                case List<object> l: return l.Select(x => Subst(x, vars)).ToList();
                default: return v;
            }
        }

        public static Dictionary<string, string> ParseVars(IEnumerable<string> raw)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in raw)
            {
                var i = r.IndexOf('=');
                if (i <= 0) throw new ArgumentException("--var name=value");
                var v = r.Substring(i + 1);
                // 0.9.0: --var ops=@file.json reads the value from a file (avoids PowerShell quoting with long JSON)
                d[r.Substring(0, i)] = v.StartsWith("@", StringComparison.Ordinal) && v.Length > 1 ? System.IO.File.ReadAllText(v.Substring(1)) : v;
            }
            return d;
        }
    }

    public static partial class RevitCli
    {
        /// <summary>
        /// revit campana080 --revit-pid N --ruta-esperada C:\...\copia.rvt --autorizo-persistente --pasos N8-04.json --salida DIR [--var x=y ...]
        /// Common identity and preflight (CampaignPreflight): actual path = authorized, local, non-workshared; otherwise BLOQUEADA and no step is sent.
        /// </summary>
        private static int RunCampaign080(string[] a)
        {
            var o = new Nrt05Options
            {
                Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
                OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("missing --salida"),
                ExpectedPath = Program.Opt(a, "--ruta-esperada"),
                Authorized = a.Contains("--autorizo-persistente"),
                Ids = new List<long> { 1, 2, 3 },   // the 0.8.0 campaign does not use --ids here: ids go in --var
            };
            var spec = Json.ParseObject(File.ReadAllText(Program.Opt(a, "--pasos") ?? throw new ArgumentException("missing --pasos")));
            var vars = Campaign080.ParseVars(Program.Multi(a, "--var"));
            var link = RevitLink.ForPid(o.Pid, out var err);
            if (link == null) { Console.WriteLine("error " + err); return 1; }
            using (link)
            {
                Directory.CreateDirectory(o.OutDir);
                link.OnCall = c => File.AppendAllText(Path.Combine(o.OutDir, "llamadas.jsonl"), Json.Write(c) + "\n");
                var report = new Dictionary<string, object> { ["campana"] = "0.8.0", ["version"] = BuildInfo.Informational, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
                var b = CampaignPreflight(link, o, report, out var why);
                var name = (Arg.GetString(spec, "item") ?? "item").ToLowerInvariant();
                if (b == null) { report["resultado"] = "BLOQUEADA"; report["motivo"] = why; File.WriteAllText(Path.Combine(o.OutDir, name + "_informe.json"), Json.Write(report)); Console.WriteLine("BLOQUEADA: " + why); return 3; }
                using (b)
                {
                    if (b.Context.GetValueOrDefault("worksharing") is bool ws && ws) { Console.WriteLine("BLOQUEADA: workshared document"); return 3; }
                    var res = Campaign080.RunSteps(spec, (op, args, write) => link.Call(op, args, write), Convert.ToString(b.Context.GetValueOrDefault("doc"), CultureInfo.InvariantCulture), vars);
                    foreach (var kv in res) report[kv.Key] = kv.Value;
                    report["metricas"] = Bench.Benchmarks.Wire(link.Calls);
                    report["fin"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    File.WriteAllText(Path.Combine(o.OutDir, name + "_informe.json"), Json.Write(report));
                    Console.WriteLine(res["item"] + " " + res["resultado"]);
                    return (string)res["resultado"] == "APROBADA" ? 0 : 1;
                }
            }
        }
    }
}
