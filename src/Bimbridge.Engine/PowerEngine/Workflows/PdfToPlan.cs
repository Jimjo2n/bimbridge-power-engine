// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;
using Home = Bimbridge.Engine.Home;
using QueryException = Bimbridge.Engine.QueryException;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>
    /// Full workflow up to the plan (A→C): PDF → grids/anchors + symbols (Python) → registration (C#) → placement plan (C#, with
    /// Rust for bulk work). Returns the plan with "place" batches ready for the plan tool (plan→commit with journal,
    /// queue and idempotency). Does not touch Revit: the caller supplies the model grids (a prior query of the model).
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
            public List<object> SymbolDecisions;
        }

        public static Dictionary<string, object> Run(PowerEngineDispatcher pe, Request q, IEnumerable<string> authorized)
        {
            var auth = authorized.ToList();
            var args = new Dictionary<string, object> { ["ruta"] = q.PdfPath, ["paginas"] = new List<object> { (long)q.Page } };
            var grids = pe.Document("pdf_grids", args, auth);
            if (!grids.Ok) return Fail("extraccion", grids.Code + ": " + grids.Error);
            if (q.SymbolDecisions != null) args["decisiones"] = q.SymbolDecisions;
            var syms = pe.Document("pdf_symbols", args, auth);
            if (!syms.Ok) return Fail("extraccion", syms.Code + ": " + syms.Error);
            var gp = (Dictionary<string, object>)((List<object>)grids.Result["paginas"])[0];
            var sp = (Dictionary<string, object>)((List<object>)syms.Result["paginas"])[0];
            if (q.SymbolDecisions != null && (!sp.ContainsKey("confirmados") || !sp.ContainsKey("inventario")))
                return Fail("extraccion", "the PDF worker does not support traceable decisions; update the worker");
            if (!Equals(grids.Result["sha256"], syms.Result["sha256"]))
                return Fail("extraccion", "the grids and the symbols belong to different files; repeat the read");
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
            var plan = PlacementPlanner.Plan(PlacementPlanner.FromPdfSymbols(syms.Result, confirmedOnly: q.SymbolDecisions != null), reg, q.Rules,
                new PlacementOptions { Level = q.Level, MinConfidence = q.MinConfidence, SourceId = (num ?? System.IO.Path.GetFileName(q.PdfPath)) + "@" + (rev ?? "?") + "#p" + q.Page, SourceSha256 = shaPdf, SourceRevision = rev },
                q.Existing, pe);
            var r = plan.ToJson(items: false);
            if (q.SymbolDecisions != null && sp["inventario"] is List<object> inventory)
            {
                foreach (var symbol in inventory.Cast<Dictionary<string, object>>())
                {
                    if (!reg.Ok) break;
                    var center = (List<object>)symbol["centro_pt"];
                    var px = D(center[0]);
                    var py = D(center[1]);
                    var point = reg.Map(px, py);
                    symbol["xy_ft"] = new List<object> { point.X, point.Y };
                    if (Arg.GetString(symbol, "codigo_confirmado") is string code)
                        symbol["id_colocacion"] = PlacementPlanner.StableId(shaPdf, q.Page, px, py, code);
                }
                r["inventario"] = inventory;
            }
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
        /// 0.9.1 (Block I): executable PDF→Revit bridge. Turns the placement plan into arguments ready for the plan tool
        /// (plan→commit: diff, queue, a single Undo, audit) ONLY if it is safe to run:
        ///  - there is a valid registration and level, and something unambiguous to place;
        ///  - every operation passes offline typed validation (OpSpec) before reaching Revit;
        ///  - every plan carries an idempotency key derived from the plan fingerprint and its content: repeating it does not place twice;
        ///  - anything ambiguous is NOT run: it is returned separately for a person to decide.
        /// Does not touch Revit or apply anything: the agent commits with the usual permissions and policy.
        /// </summary>
        public static Dictionary<string, object> Execution(PlacementPlan plan)
        {
            var reasons = new List<object>();
            if (plan == null) return new Dictionary<string, object> { ["ejecutable"] = false, ["motivos"] = new List<object> { "no plan" } };
            if (plan.Estado != "listo" && plan.Estado != "listo_con_ambiguedades") reasons.Add("plan status: " + plan.Estado);
            if (plan.Registration == null || !plan.Registration.Ok) reasons.Add("no valid plan–model registration");
            if (plan.Batches.Count == 0) reasons.Add("nothing unambiguous to place");
            if (string.IsNullOrEmpty(plan.Fingerprint) && plan.Batches.Count > 0) reasons.Add("plan without fingerprint: the idempotency key cannot be derived");
            var ops = new List<Dictionary<string, object>>();
            for (var i = 0; i < plan.Batches.Count && reasons.Count == 0; i++)
            {
                var op = plan.Batches[i]["operacion"] as Dictionary<string, object>;
                try
                {
                    if (op == null || Arg.GetString(op, "op") != "place") throw new QueryException("batch " + (i + 1) + " is not a place operation");
                    Bimbridge.Engine.OpSpec.Parse(new Dictionary<string, object>(op), i + 1);
                    ops.Add(op);
                }
                catch (QueryException ex) { reasons.Add("batch " + (i + 1) + " invalid: " + ex.Message); }
            }
            var d = new Dictionary<string, object>
            {
                ["ejecutable"] = reasons.Count == 0,
                ["pendiente_de_persona"] = (long)plan.Ambiguous.Count,
                ["ya_existen"] = (long)plan.AlreadyThere.Count,
            };
            if (reasons.Count > 0) { d["motivos"] = reasons; return d; }
            // A Revit plan accepts a maximum number of operations: it is split into consecutive plans, each with its own key.
            var plans = new List<object>();
            var max = Bimbridge.Engine.OpSpec.MaxOps;
            var source = Arg.GetString(plan.Source, "id") ?? "PDF";
            var sha = Arg.GetString(plan.Source, "sha256");
            var revision = Arg.GetString(plan.Source, "revision");
            var regPrint = Bimbridge.Engine.SourceBinding.RegistrationFingerprint(plan.Registration.M);
            if (string.IsNullOrEmpty(sha)) { d["ejecutable"] = false; d["motivos"] = new List<object> { "the source has no SHA-256: the batch cannot be bound to its origin" }; return d; }
            for (int k = 0, n = 1; k < ops.Count; k += max, n++)
            {
                // AUD-19: every operation carries the origin token in its content; the engine derives the key from it when planning and
                // applies it at commit even if the agent does not pass clave=. The key reported here is the same one the engine will derive.
                var token = Bimbridge.Engine.SourceBinding.Token("pdf", source, sha, revision, regPrint, plan.Fingerprint, n);
                var chunk = ops.Skip(k).Take(max).Select(o => new Dictionary<string, object>(o) { [Bimbridge.Engine.SourceBinding.OpField] = token }).ToList();
                var points = chunk.Sum(o => ((List<object>)o["puntos"]).Count);
                var bound = Bimbridge.Engine.SourceBinding.Resolve(chunk.Cast<object>().ToList());
                plans.Add(new Dictionary<string, object>
                {
                    ["ops"] = chunk.Cast<object>().ToList(),
                    ["summary"] = "Placement from " + source + " (" + n + "): " + points + " elements in " + chunk.Count + " batches",
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
            d["siguiente"] = "for each entry in planes: plan with its ops AS IS (they carry origen=) and summary; review the diff; commit plan_id=… (the engine applies the idempotency key: repeating the same batch on the same model does not place twice); then verify with the placement verifier against an independent source";
            if (plan.Ambiguous.Count > 0) d["aviso"] = plan.Ambiguous.Count + " ambiguous symbols are NOT placed: they need a person's decision (see 'ambiguos')";
            return d;
        }

        private static Dictionary<string, object> Fail(string stage, string why) => new Dictionary<string, object> { ["estado"] = "error_" + stage, ["motivo"] = why };
        private static double D(object o) => Convert.ToDouble(o, CultureInfo.InvariantCulture);

        /// <summary>Reads type rules from JSON: [{"codigo":"D","tipo":"Fam: Tipo","montaje":"losa"|"elev":9.25,"ws":"…","direccional":true}].</summary>
        public static List<TypeRule> RulesFromJson(List<object> rules) => rules.Cast<Dictionary<string, object>>().Select(r => new TypeRule
        {
            Code = Arg.GetString(r, "codigo"), FamilyType = Arg.GetString(r, "tipo"), Montaje = Arg.GetString(r, "montaje"), Workset = Arg.GetString(r, "ws"),
            Host = Arg.GetString(r, "anfitrion"), ElevFt = r.TryGetValue("elev", out var e) && e != null ? D(e) : (double?)null,
            Directional = r.TryGetValue("direccional", out var d) && d is bool b && b,
        }).ToList();

        /// <summary>Model grids from JSON: [{"nombre":"A","p":[x0,y0,x1,y1]}] in feet.</summary>
        public static List<(string, double, double, double, double)> GridsFromJson(List<object> grids) => grids.Cast<Dictionary<string, object>>().Select(g =>
        {
            var p = (List<object>)g["p"];
            return (Arg.GetString(g, "nombre"), D(p[0]), D(p[1]), D(p[2]), D(p[3]));
        }).ToList();
    }
}
