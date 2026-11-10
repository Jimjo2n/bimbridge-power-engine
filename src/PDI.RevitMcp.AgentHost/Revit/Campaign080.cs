// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// 0.8.0: arnés genérico de la campaña nativa. Cada ítem (N8-01…N8-13) es un archivo de pasos JSON:
    /// { "item", "titulo", "estado_inicial", "pasos": [ { "nombre", "op", "args", "escritura", "esperar", "contiene", "no_contiene", "guardar" } ] }.
    /// - ${var} se sustituye por variables de --var nombre=valor o por lo guardado en pasos anteriores ("guardar": {"plan": "plan_id"}).
    /// - "esperar": estado esperado (ok, rechazado, invalido…); "contiene"/"no_contiene": textos en la respuesta JSON.
    /// - El documento se fija en el preflight (ruta = --ruta-esperada, local, no workshared) y se inyecta en cada paso: ningún paso elige documento.
    /// Sin PDI_REVIT_NATIVO=AUTORIZADO y --revit-disponible no se ejecuta nada (NativeGate).
    /// </summary>
    public static class Campaign080
    {
        public const string Schema = "bimbridge.campana080/1";

        public sealed class StepResult
        {
            public string Nombre, Op, Estado, Esperado, Motivo;
            public bool Ok;
            public long Ms;
        }

        /// <summary>Ejecuta los pasos con <paramref name="call"/> (op, args, escritura) → respuesta. Puro: las pruebas lo usan sin Revit.</summary>
        public static Dictionary<string, object> RunSteps(Dictionary<string, object> spec, Func<string, Dictionary<string, object>, bool, Response> call,
                                                          string docHandle, Dictionary<string, string> vars)
        {
            if (Arg.GetString(spec, "esquema") != Schema) throw new FormatException("esquema distinto de " + Schema);
            var item = Arg.GetString(spec, "item") ?? throw new FormatException("falta item");
            var steps = spec.TryGetValue("pasos", out var ps) && ps is List<object> pl ? pl.OfType<Dictionary<string, object>>().ToList() : throw new FormatException("faltan pasos");
            vars = new Dictionary<string, string>(vars ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            var results = new List<StepResult>();
            var stopped = false;
            foreach (var s in steps)
            {
                var r = new StepResult { Nombre = Arg.GetString(s, "nombre") ?? ("paso " + (results.Count + 1)), Op = Arg.GetString(s, "op"), Esperado = Arg.GetString(s, "esperar") ?? Status.Ok };
                results.Add(r);
                if (stopped) { r.Motivo = "no ejecutado: falló un paso anterior"; continue; }
                Dictionary<string, object> args;
                try { args = (Dictionary<string, object>)Subst(s.TryGetValue("args", out var a) ? a : new Dictionary<string, object>(), vars); }
                catch (KeyNotFoundException ex) { r.Motivo = ex.Message; stopped = true; continue; }
                // el documento lo fija el preflight; sólo un paso NEGATIVO marcado doc_explicito conserva su propio doc (para comprobar el rechazo)
                if (r.Op != Wire.OpContext && r.Op != Wire.OpDocs && !(Arg.GetBool(s, "doc_explicito") ?? false)) args[Wire.DocArg] = docHandle;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = call(r.Op, args, Arg.GetBool(s, "escritura") ?? false);
                r.Ms = sw.ElapsedMilliseconds;
                r.Estado = resp.Estado;
                var json = Json.Write(resp.ToJson());
                var problems = new List<string>();
                if (!string.Equals(resp.Estado, r.Esperado, StringComparison.Ordinal)) problems.Add("estado " + resp.Estado + " (esperado " + r.Esperado + ")" + (resp.Aviso != null ? ": " + resp.Aviso : ""));
                foreach (var t in Arg.GetStringList(s, "contiene") ?? new List<string>()) { var tt = SubstText(t, vars); if (json.IndexOf(tt, StringComparison.Ordinal) < 0) problems.Add("no contiene «" + tt + "»"); }
                foreach (var t in Arg.GetStringList(s, "no_contiene") ?? new List<string>()) { var tt = SubstText(t, vars); if (json.IndexOf(tt, StringComparison.Ordinal) >= 0) problems.Add("contiene «" + tt + "»"); }
                if (s.TryGetValue("guardar", out var g) && g is Dictionary<string, object> gd && resp.Datos is Dictionary<string, object> dd)
                    foreach (var kv in gd)
                    {
                        var v = Pick(dd, Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                        if (v == null) problems.Add("no se pudo guardar " + kv.Key + " desde " + kv.Value);
                        else vars[kv.Key] = Convert.ToString(v, CultureInfo.InvariantCulture);
                    }
                r.Ok = problems.Count == 0;
                r.Motivo = r.Ok ? null : string.Join("; ", problems);
                if (!r.Ok) stopped = true;
            }
            var pass = results.Count > 0 && results.All(x => x.Ok);
            return new Dictionary<string, object>
            {
                ["item"] = item, ["titulo"] = Arg.GetString(spec, "titulo"), ["resultado"] = pass ? "APROBADA" : "FALLIDA",
                ["pasos"] = results.Select(x => (object)new Dictionary<string, object> { ["nombre"] = x.Nombre, ["op"] = x.Op, ["estado"] = x.Estado, ["esperado"] = x.Esperado, ["ok"] = x.Ok, ["ms"] = x.Ms, ["motivo"] = x.Motivo }).ToList(),
            };
        }

        /// <summary>Ruta simple a.b.0.c dentro de los datos de una respuesta.</summary>
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
            Var.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new KeyNotFoundException("variable sin valor: ${" + m.Groups[1].Value + "}"));

        /// <summary>Sustitución recursiva. Un texto que es exactamente "${x}" y cuyo valor es entero se convierte en número (ids, vistas).</summary>
        public static object Subst(object v, Dictionary<string, string> vars)
        {
            switch (v)
            {
                case string s:
                    var m = Var.Match(s);
                    if (m.Success && m.Value == s)
                    {
                        var val = SubstText(s, vars);
                        // 0.9.0: un valor JSON (lista u objeto, p. ej. las ops de power flujo) entra como estructura, no como texto
                        var tv = val.TrimStart();
                        if (tv.StartsWith("[", StringComparison.Ordinal) || tv.StartsWith("{", StringComparison.Ordinal))
                            try { return Json.Parse(val); } catch (FormatException) { /* texto tal cual */ }
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
                if (i <= 0) throw new ArgumentException("--var nombre=valor");
                var v = r.Substring(i + 1);
                // 0.9.0: --var ops=@archivo.json lee el valor de un archivo (evita comillas de PowerShell con JSON largo)
                d[r.Substring(0, i)] = v.StartsWith("@", StringComparison.Ordinal) && v.Length > 1 ? System.IO.File.ReadAllText(v.Substring(1)) : v;
            }
            return d;
        }
    }

    public static partial class RevitCli
    {
        /// <summary>
        /// revit campana080 --revit-pid N --ruta-esperada C:\...\copia.rvt --autorizo-persistente --pasos N8-04.json --salida DIR [--var x=y ...]
        /// Identidad y preflight comunes (CampaignPreflight): ruta real = autorizada, local, no workshared; si no, BLOQUEADA y no se envía ningún paso.
        /// </summary>
        private static int Campana080(string[] a)
        {
            var o = new Nrt05Options
            {
                Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
                OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("falta --salida"),
                ExpectedPath = Program.Opt(a, "--ruta-esperada"),
                Authorized = a.Contains("--autorizo-persistente"),
                Ids = new List<long> { 1, 2, 3 },   // la campaña 0.8.0 no usa --ids aquí: los ids van en --var
            };
            var spec = Json.ParseObject(File.ReadAllText(Program.Opt(a, "--pasos") ?? throw new ArgumentException("falta --pasos")));
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
                    if (b.Context.GetValueOrDefault("worksharing") is bool ws && ws) { Console.WriteLine("BLOQUEADA: documento workshared"); return 3; }
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
