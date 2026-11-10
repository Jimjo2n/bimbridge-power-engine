// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

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
            return r;
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
