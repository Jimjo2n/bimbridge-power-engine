// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;
using Home = PDI.RevitMcp.Engine.Home;
using QueryException = PDI.RevitMcp.Engine.QueryException;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>
    /// Flujo completo hasta el plan (A→C): PDF → ejes/anclas + símbolos (Python) → registro (C#) → plan de colocación (C#, con
    /// Rust en lo masivo). Devuelve el plan con lotes de "place" listos para la herramienta plan (plan→commit con diario,
    /// cola e idempotencia). No toca Revit: los ejes del modelo los aporta quien llama (consulta previa al modelo).
    /// </summary>
    public static class PdfToPlan
    {
        public sealed class Request
        {
            public string PdfPath;
            public int Page = 1;
            public string Level;
            public List<TypeRule> Rules = new List<TypeRule>();
            public List<(string Name, double X0, double Y0, double X1, double Y1)> ModelGrids = new List<(string, double, double, double, double)>();
            public List<ExistingElement> Existing = new List<ExistingElement>();
            public double TolFt = 0.25;
            public double MinConfidence = 0.9;
            public double[] LinkTransform;
            public string Mode = "semejanza";
        }

        public static Dictionary<string, object> Run(PowerEngineDispatcher pe, Request q, IEnumerable<string> authorized)
        {
            var auth = authorized.ToList();
            var args = new Dictionary<string, object> { ["ruta"] = q.PdfPath, ["paginas"] = new List<object> { (long)q.Page } };
            var grids = pe.Document("pdf_grids", args, auth);
            if (!grids.Ok) return Fail("extraccion", grids.Code + ": " + grids.Error);
            var syms = pe.Document("pdf_symbols", args, auth);
            if (!syms.Ok) return Fail("extraccion", syms.Code + ": " + syms.Error);
            var gp = (Dictionary<string, object>)((List<object>)grids.Result["paginas"])[0];
            var sp = (Dictionary<string, object>)((List<object>)syms.Result["paginas"])[0];
            var page = ((List<object>)gp["intersecciones"]).Cast<Dictionary<string, object>>()
                .ToDictionary(x => (string)x["nombre"], x => { var p = (List<object>)x["punto_pt"]; return (D(p[0]), D(p[1])); }, StringComparer.Ordinal);
            var model = Registration.GridIntersections(q.ModelGrids);
            var anchors = Registration.AnchorsByName(page, model);
            var sheet = sp.TryGetValue("lamina", out var lm) ? lm as Dictionary<string, object> : null;
            double? scale = null;
            if (sheet != null && sheet.TryGetValue("escala", out var es) && es is Dictionary<string, object> esd && esd.TryGetValue("factor_revit", out var f) && f != null) scale = D(f);
            var marco = (Dictionary<string, object>)sp["marco"];
            var reg = Registration.Solve(anchors, new RegistrationOptions
            {
                Mode = q.Mode, TolFt = q.TolFt, ExpectedRevitScale = scale, LinkTransform = q.LinkTransform,
                UserUnit = marco.TryGetValue("unidad_usuario", out var uu) ? D(uu) : 1.0,
            });
            var shaPdf = (string)syms.Result["sha256"];
            var rev = sheet != null && sheet.TryGetValue("revision", out var rv) && rv is Dictionary<string, object> rvd ? Arg.GetString(rvd, "valor") : null;
            var num = sheet != null && sheet.TryGetValue("numero", out var nm) && nm is Dictionary<string, object> nmd ? Arg.GetString(nmd, "valor") : null;
            var plan = PlacementPlanner.Plan(PlacementPlanner.FromPdfSymbols(syms.Result), reg, q.Rules,
                new PlacementOptions { Level = q.Level, MinConfidence = q.MinConfidence, SourceId = (num ?? System.IO.Path.GetFileName(q.PdfPath)) + "@" + (rev ?? "?") + "#p" + q.Page, SourceSha256 = shaPdf, SourceRevision = rev },
                q.Existing, pe);
            var r = plan.ToJson(items: false);
            r["anclas"] = new Dictionary<string, object>
            {
                ["pagina"] = (long)page.Count, ["modelo"] = (long)model.Count, ["comunes"] = (long)anchors.Count,
                ["solo_pagina"] = page.Keys.Where(k => !model.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).Take(20).Cast<object>().ToList(),
            };
            r["lamina"] = sheet;
            r["telemetria"] = new Dictionary<string, object> { ["pdf_grids_ms"] = Math.Round(grids.Telemetry.TotalMs, 1), ["pdf_symbols_ms"] = Math.Round(syms.Telemetry.TotalMs, 1), ["motor_geometria"] = pe.Last?.Engine };
            r["ops"] = plan.Batches.Select(b => (object)Json.Write(b["operacion"])).ToList();
            r["ejecucion"] = Execution(plan);
            return r;
        }

        /// <summary>
        /// 0.9.1 (Bloque I): puente ejecutable PDF→Revit. Convierte el plan de colocación en argumentos listos para la herramienta
        /// plan (plan→commit: diff, cola, un Deshacer, auditoría) SÓLO si es seguro ejecutarlo:
        ///  - hay registro válido y nivel, y algo inequívoco que colocar;
        ///  - cada operación pasa la validación tipada offline (OpSpec) antes de llegar a Revit;
        ///  - cada plan lleva una clave de idempotencia derivada de la huella del plan y de su contenido: repetirlo no coloca dos veces;
        ///  - lo ambiguo NO se ejecuta: se devuelve aparte para que lo decida una persona.
        /// No toca Revit ni aplica nada: el commit lo hace el agente con los permisos y la política de siempre.
        /// </summary>
        public static Dictionary<string, object> Execution(PlacementPlan plan)
        {
            var reasons = new List<object>();
            if (plan == null) return new Dictionary<string, object> { ["ejecutable"] = false, ["motivos"] = new List<object> { "sin plan" } };
            if (plan.Estado != "listo" && plan.Estado != "listo_con_ambiguedades") reasons.Add("estado del plan: " + plan.Estado);
            if (plan.Registration == null || !plan.Registration.Ok) reasons.Add("sin registro plano–modelo válido");
            if (plan.Batches.Count == 0) reasons.Add("no hay nada inequívoco que colocar");
            if (string.IsNullOrEmpty(plan.Fingerprint) && plan.Batches.Count > 0) reasons.Add("plan sin huella: no se puede derivar la clave de idempotencia");
            var ops = new List<Dictionary<string, object>>();
            for (var i = 0; i < plan.Batches.Count && reasons.Count == 0; i++)
            {
                var op = plan.Batches[i]["operacion"] as Dictionary<string, object>;
                try
                {
                    if (op == null || Arg.GetString(op, "op") != "place") throw new QueryException("el lote " + (i + 1) + " no es una operación place");
                    PDI.RevitMcp.Engine.OpSpec.Parse(new Dictionary<string, object>(op), i + 1);
                    ops.Add(op);
                }
                catch (QueryException ex) { reasons.Add("lote " + (i + 1) + " no válido: " + ex.Message); }
            }
            var d = new Dictionary<string, object>
            {
                ["ejecutable"] = reasons.Count == 0,
                ["pendiente_de_persona"] = (long)plan.Ambiguous.Count,
                ["ya_existen"] = (long)plan.AlreadyThere.Count,
            };
            if (reasons.Count > 0) { d["motivos"] = reasons; return d; }
            // Un plan de Revit admite un máximo de operaciones: se parte en planes consecutivos, cada uno con su clave.
            var plans = new List<object>();
            var max = PDI.RevitMcp.Engine.OpSpec.MaxOps;
            var source = Arg.GetString(plan.Source, "id") ?? "PDF";
            var sha = Arg.GetString(plan.Source, "sha256");
            var revision = Arg.GetString(plan.Source, "revision");
            var regPrint = PDI.RevitMcp.Engine.SourceBinding.RegistrationFingerprint(plan.Registration.M);
            if (string.IsNullOrEmpty(sha)) { d["ejecutable"] = false; d["motivos"] = new List<object> { "la fuente no tiene SHA-256: no se puede vincular el lote a su origen" }; return d; }
            for (int k = 0, n = 1; k < ops.Count; k += max, n++)
            {
                // AUD-19: cada operación lleva el testigo de origen en su contenido; el motor deriva de él la clave al planificar y la
                // aplica en el commit aunque el agente no pase clave=. La clave que se informa aquí es la misma que derivará el motor.
                var token = PDI.RevitMcp.Engine.SourceBinding.Token("pdf", source, sha, revision, regPrint, plan.Fingerprint, n);
                var chunk = ops.Skip(k).Take(max).Select(o => new Dictionary<string, object>(o) { [PDI.RevitMcp.Engine.SourceBinding.OpField] = token }).ToList();
                var points = chunk.Sum(o => ((List<object>)o["puntos"]).Count);
                var bound = PDI.RevitMcp.Engine.SourceBinding.Resolve(chunk.Cast<object>().ToList());
                plans.Add(new Dictionary<string, object>
                {
                    ["ops"] = chunk.Cast<object>().ToList(),
                    ["summary"] = "Colocación desde " + source + " (" + n + "): " + points + " elementos en " + chunk.Count + " lotes",
                    [Wire.KeyArg] = bound.Key,
                    ["elementos"] = (long)points,
                    ["origen"] = new Dictionary<string, object>
                    {
                        ["tipo"] = "pdf", ["fuente"] = source, ["sha256"] = sha, ["revision"] = revision, ["registro"] = regPrint,
                        ["huella_plan"] = plan.Fingerprint, ["lote"] = (long)n, ["testigo"] = token, ["huella_operaciones"] = bound.Hash,
                    },
                });
            }
            d["planes"] = plans;
            d["elementos"] = (long)plan.Place.Count;
            d["siguiente"] = "por cada entrada de planes: plan con sus ops TAL CUAL (llevan origen=) y summary; revise el diff; commit plan_id=… (la clave de idempotencia la aplica el motor: repetir el mismo lote en el mismo modelo no coloca dos veces); después verifique con el verificador de colocación contra una fuente independiente";
            if (plan.Ambiguous.Count > 0) d["aviso"] = plan.Ambiguous.Count + " símbolos ambiguos NO se colocan: necesitan decisión de una persona (ver 'ambiguos')";
            return d;
        }

        private static Dictionary<string, object> Fail(string stage, string why) => new Dictionary<string, object> { ["estado"] = "error_" + stage, ["motivo"] = why };
        private static double D(object o) => Convert.ToDouble(o, CultureInfo.InvariantCulture);

        /// <summary>Lee reglas de tipo de JSON: [{"codigo":"D","tipo":"Fam: Tipo","montaje":"losa"|"elev":9.25,"ws":"…","direccional":true}].</summary>
        public static List<TypeRule> RulesFromJson(List<object> rules) => rules.Cast<Dictionary<string, object>>().Select(r => new TypeRule
        {
            Code = Arg.GetString(r, "codigo"), FamilyType = Arg.GetString(r, "tipo"), Montaje = Arg.GetString(r, "montaje"), Workset = Arg.GetString(r, "ws"),
            Host = Arg.GetString(r, "anfitrion"), ElevFt = r.TryGetValue("elev", out var e) && e != null ? D(e) : (double?)null,
            Directional = r.TryGetValue("direccional", out var d) && d is bool b && b,
        }).ToList();

        /// <summary>Ejes del modelo de JSON: [{"nombre":"A","p":[x0,y0,x1,y1]}] en pies.</summary>
        public static List<(string, double, double, double, double)> GridsFromJson(List<object> grids) => grids.Cast<Dictionary<string, object>>().Select(g =>
        {
            var p = (List<object>)g["p"];
            return (Arg.GetString(g, "nombre"), D(p[0]), D(p[1]), D(p[2]), D(p[3]));
        }).ToList();
    }
}
