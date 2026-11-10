// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>Símbolo candidato leído del PDF (salida de pdf_symbols).</summary>
    public sealed class SymbolCandidate
    {
        public double Px, Py;            // pt, espacio visible
        public string Code, Signature, Label;
        public double Confidence;
        public double RotDeg;            // giro del símbolo en la página
        public int Page = 1;
    }

    /// <summary>Cómo se materializa un código del plano en el modelo (lo decide la persona o la tabla de luminarias, no el agente).</summary>
    public sealed class TypeRule
    {
        public string Code;
        public string FamilyType;        // "Familia: Tipo" tal como lo usa la operación place
        public double? ElevFt;           // offset sobre el nivel; null = usar Montaje
        public string Montaje;           // "losa" (cara inferior de lo de arriba) u otro modo de place
        public string Workset;
        public string Host;              // anfitrión requerido (techo, muro, losa…) — restricción declarada
        public bool Directional;         // si true, la orientación del símbolo importa (tiras, apliques)
        public Dictionary<string, object> Params = new Dictionary<string, object>();
    }

    /// <summary>Elemento ya existente en el modelo (para no duplicar).</summary>
    public sealed class ExistingElement
    {
        public long Id; public double X, Y; public string FamilyType;
    }

    public sealed class PlacementOptions
    {
        public string Level;
        public double MinConfidence = 0.9;
        public double DuplicateTolFt = 0.5;      // dos símbolos a menos de esto en el modelo = casi duplicado (6")
        public double ExistingTolFt = 0.5;
        public int BatchMax = 500;               // máximo de la operación place
        public string SourceId, SourceSha256, SourceRevision;
        public double MinRotationDeg = 0.5;      // por debajo, sin giro
    }

    public sealed class PlacementItem
    {
        public string Id, Code, FamilyType, Level, Workset, Host, Montaje, Estado, Motivo;
        public double X, Y, RotDeg, Confidence;
        public double? ElevFt;
        public double Px, Py; public int Page;
        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["codigo"] = Code, ["tipo"] = FamilyType, ["nivel"] = Level, ["xy_ft"] = new List<object> { R(X), R(Y) },
                ["giro_deg"] = Math.Round(RotDeg, 3), ["confianza"] = Confidence, ["estado"] = Estado,
                ["fuente"] = new Dictionary<string, object> { ["pagina"] = (long)Page, ["punto_pt"] = new List<object> { R(Px), R(Py) } },
            };
            if (ElevFt.HasValue) d["elev_ft"] = R(ElevFt.Value);
            if (Montaje != null) d["montaje"] = Montaje;
            if (Workset != null) d["ws"] = Workset;
            if (Host != null) d["anfitrion"] = Host;
            if (Motivo != null) d["motivo"] = Motivo;
            return d;
        }
        private static object R(double v) => Math.Round(v, 5);
    }

    public sealed class PlacementPlan
    {
        public string Estado = "listo";
        public RegistrationResult Registration;
        public List<PlacementItem> Place = new List<PlacementItem>();
        public List<PlacementItem> Ambiguous = new List<PlacementItem>();
        public List<PlacementItem> AlreadyThere = new List<PlacementItem>();
        public List<Dictionary<string, object>> Batches = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Source = new Dictionary<string, object>();
        public string Fingerprint;

        public Dictionary<string, object> ToJson(bool items = true) => new Dictionary<string, object>
        {
            ["estado"] = Estado, ["fuente"] = Source, ["huella_plan"] = Fingerprint,
            ["resumen"] = new Dictionary<string, object> { ["colocar"] = (long)Place.Count, ["ambiguos"] = (long)Ambiguous.Count, ["ya_existen"] = (long)AlreadyThere.Count, ["lotes"] = (long)Batches.Count },
            ["registro"] = Registration?.ToJson(),
            ["colocar"] = items ? Place.Select(p => (object)p.ToJson()).ToList() : null,
            ["ambiguos"] = Ambiguous.Select(p => (object)p.ToJson()).ToList(),
            ["ya_existen"] = items ? AlreadyThere.Select(p => (object)p.ToJson()).ToList() : null,
            ["lotes"] = Batches.Cast<object>().ToList(),
        };
    }

    /// <summary>
    /// Planificador de colocación: símbolos del PDF + registro + reglas de tipo → plan determinista. Resuelve lo que tiene una sola
    /// respuesta; lo demás (confianza baja, sin tipo, casi duplicados, conflicto con lo existente) se ESCALA como ambiguo, nunca se
    /// adivina. Produce lotes de la operación tipada "place" (≤500 puntos) agrupados por tipo, nivel, montaje, giro y workset: la
    /// ejecución reutiliza el plan/commit existente (cola, diario, idempotencia). No llama a Revit.
    /// </summary>
    public static class PlacementPlanner
    {
        public static PlacementPlan Plan(IList<SymbolCandidate> symbols, RegistrationResult reg, IList<TypeRule> rules, PlacementOptions o,
                                         IList<ExistingElement> existing = null, PowerEngineDispatcher engine = null)
        {
            var plan = new PlacementPlan { Registration = reg };
            plan.Source["id"] = o.SourceId; plan.Source["sha256"] = o.SourceSha256; plan.Source["revision"] = o.SourceRevision;
            if (reg == null || !reg.Ok) { plan.Estado = "sin_registro"; return plan; }
            if (string.IsNullOrWhiteSpace(o.Level)) { plan.Estado = "sin_nivel"; return plan; }
            var byCode = (rules ?? new List<TypeRule>()).GroupBy(r => r.Code ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var regRot = reg.RotationDeg;

            // 1) transformar todos los símbolos (una sola llamada: C# o Rust según tamaño)
            var syms = symbols ?? new List<SymbolCandidate>();
            var xyz = new double[syms.Count * 3];
            for (var i = 0; i < syms.Count; i++) { xyz[3 * i] = syms[i].Px; xyz[3 * i + 1] = syms[i].Py; }
            var model = engine != null ? engine.Transform3(xyz, reg.M) : GeometryKernels.Transform3(xyz, reg.M);

            var items = new List<PlacementItem>(syms.Count);
            for (var i = 0; i < syms.Count; i++)
            {
                var s = syms[i];
                var it = new PlacementItem
                {
                    Code = s.Code, Level = o.Level, X = model[3 * i], Y = model[3 * i + 1], Confidence = s.Confidence, Px = s.Px, Py = s.Py, Page = s.Page,
                    Id = StableId(o.SourceSha256, s.Page, s.Px, s.Py, s.Code),
                };
                var rule = s.Code != null && byCode.TryGetValue(s.Code, out var rl) ? rl : null;
                if (rule == null) Amb(it, "sin_tipo", "el código «" + (s.Code ?? "?") + "» no tiene regla de tipo: lo decide una persona o la tabla de luminarias");
                else if (rule.Count > 1) Amb(it, "tipo_ambiguo", "el código «" + s.Code + "» tiene " + rule.Count + " reglas de tipo");
                else
                {
                    var r = rule[0];
                    it.FamilyType = r.FamilyType; it.ElevFt = r.ElevFt; it.Montaje = r.Montaje; it.Workset = r.Workset; it.Host = r.Host;
                    var rot = r.Directional ? Norm180(s.RotDeg + regRot) : 0.0;
                    it.RotDeg = Math.Abs(rot) < o.MinRotationDeg ? 0 : rot;
                    if (s.Confidence < o.MinConfidence) Amb(it, "confianza_baja", "confianza " + s.Confidence.ToString("0.##", CultureInfo.InvariantCulture) + " < " + o.MinConfidence.ToString("0.##", CultureInfo.InvariantCulture));
                    else if (!it.ElevFt.HasValue && string.IsNullOrEmpty(it.Montaje)) Amb(it, "sin_altura", "la regla no da altura ni modo de montaje: no se inventa");
                    else it.Estado = "colocar";
                }
                items.Add(it);
            }

            // 2) casi duplicados entre símbolos (en pies de modelo): se coloca el de mayor confianza (desempate: índice menor); el resto, ambiguo
            if (items.Count > 1)
            {
                var pts = new double[items.Count * 2];
                for (var i = 0; i < items.Count; i++) { pts[2 * i] = items[i].X; pts[2 * i + 1] = items[i].Y; }
                var grp = engine != null ? engine.Duplicates(pts, 2, o.DuplicateTolFt) : GeometryKernels.Duplicates(pts, 2, o.DuplicateTolFt);
                foreach (var g in Enumerable.Range(0, items.Count).GroupBy(i => grp[i]).Where(g => g.Count() > 1))
                {
                    var keep = g.OrderByDescending(i => items[i].Confidence).ThenBy(i => i).First();
                    foreach (var i in g.Where(i => i != keep))
                        Amb(items[i], "casi_duplicado", "a menos de " + o.DuplicateTolFt.ToString("0.##", CultureInfo.InvariantCulture) + " ft de " + items[keep].Id + " (se coloca sólo aquel; confirme si son dos luminarias)");
                }
            }

            // 3) lo que ya existe en el modelo
            var ex = existing ?? new List<ExistingElement>();
            if (ex.Count > 0)
            {
                var cand = items.Where(i => i.Estado == "colocar").ToList();
                var q = new double[cand.Count * 2];
                for (var i = 0; i < cand.Count; i++) { q[2 * i] = cand[i].X; q[2 * i + 1] = cand[i].Y; }
                var r = new double[ex.Count * 2];
                for (var j = 0; j < ex.Count; j++) { r[2 * j] = ex[j].X; r[2 * j + 1] = ex[j].Y; }
                var nn = engine != null ? engine.Nearest(q, r, 2, o.ExistingTolFt) : GeometryKernels.Nearest(q, r, 2, o.ExistingTolFt);
                for (var i = 0; i < cand.Count; i++)
                {
                    if (nn.Index[i] < 0) continue;
                    var e = ex[nn.Index[i]];
                    if (string.Equals(e.FamilyType, cand[i].FamilyType, StringComparison.Ordinal)) { cand[i].Estado = "ya_existe"; cand[i].Motivo = "elemento " + e.Id + " del mismo tipo a " + nn.Dist[i].ToString("0.###", CultureInfo.InvariantCulture) + " ft"; }
                    else Amb(cand[i], "conflicto_existente", "elemento " + e.Id + " de otro tipo (" + e.FamilyType + ") a " + nn.Dist[i].ToString("0.###", CultureInfo.InvariantCulture) + " ft");
                }
            }

            plan.Place = items.Where(i => i.Estado == "colocar").ToList();
            plan.Ambiguous = items.Where(i => i.Estado == "ambiguo").ToList();
            plan.AlreadyThere = items.Where(i => i.Estado == "ya_existe").ToList();

            // 4) lotes "place": mismos tipo, nivel, montaje/elev, giro y workset; ≤ BatchMax puntos; orden estable
            foreach (var g in plan.Place.GroupBy(i => (i.FamilyType, i.Level, i.Montaje, Elev: i.ElevFt.HasValue ? Math.Round(i.ElevFt.Value, 6) : (double?)null, Rot: Math.Round(i.RotDeg, 3), i.Workset))
                                        .OrderBy(g => g.Key.FamilyType, StringComparer.Ordinal).ThenBy(g => g.Key.Rot))
            {
                var all = g.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
                var rule = byCode[all[0].Code][0];
                for (var k = 0; k < all.Count; k += o.BatchMax)
                {
                    var chunk = all.Skip(k).Take(o.BatchMax).ToList();
                    var op = new Dictionary<string, object>
                    {
                        ["op"] = "place", ["type"] = g.Key.FamilyType, ["level"] = g.Key.Level,
                        ["puntos"] = chunk.Select(i => (object)new List<object> { Math.Round(i.X, 6), Math.Round(i.Y, 6) }).ToList(),
                    };
                    if (g.Key.Elev.HasValue) op["elev"] = g.Key.Elev.Value; else op["montaje"] = g.Key.Montaje;
                    if (Math.Abs(g.Key.Rot) > 0) op["rot"] = g.Key.Rot;
                    if (g.Key.Workset != null) op["ws"] = g.Key.Workset;
                    if (rule.Params.Count > 0) op["params"] = rule.Params;
                    plan.Batches.Add(new Dictionary<string, object> { ["operacion"] = op, ["items"] = chunk.Select(i => (object)i.Id).ToList() });
                }
            }
            plan.Fingerprint = Fingerprint(plan.Place);
            if (plan.Ambiguous.Count > 0) plan.Estado = plan.Place.Count > 0 ? "listo_con_ambiguedades" : "solo_ambiguedades";
            return plan;
        }

        /// <summary>Huella del plan (igual criterio que IndependentVerifier.Fingerprint, con los ids estables del plan como clave): permite
        /// rechazar una verificación cuyo "esperado" es el propio plan copiado. Una fuente independiente que coincide en posiciones
        /// no comparte los ids del plan, así que un acierto preciso no se confunde con una copia.</summary>
        public static string Fingerprint(IEnumerable<PlacementItem> items) =>
            PDI.RevitMcp.Engine.Workflow.IndependentVerifier.Fingerprint(items.Select(i => new PDI.RevitMcp.Engine.Workflow.IndependentVerifier.Item
            { Key = i.Id, X = i.X, Y = i.Y, Attrs = { ["tipo"] = i.FamilyType ?? "" } }));

        public static string StableId(string sourceSha, int page, double px, double py, string code) =>
            "pl-" + Home.Sha256Hex((sourceSha ?? "") + "|" + page.ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(px * 8).ToString(CultureInfo.InvariantCulture) + "|" +
                                   Math.Round(py * 8).ToString(CultureInfo.InvariantCulture) + "|" + (code ?? "")).Substring(0, 16);

        /// <summary>Lee los candidatos (y ambiguos, marcados con su confianza) de la salida de pdf_symbols.</summary>
        public static List<SymbolCandidate> FromPdfSymbols(Dictionary<string, object> result, bool includeAmbiguous = true)
        {
            var l = new List<SymbolCandidate>();
            foreach (Dictionary<string, object> pg in (List<object>)result["paginas"])
            {
                var page = Convert.ToInt32(pg["pagina"], CultureInfo.InvariantCulture);
                foreach (var key in includeAmbiguous ? new[] { "candidatos", "ambiguos" } : new[] { "candidatos" })
                    foreach (Dictionary<string, object> c in (List<object>)pg[key])
                    {
                        var p = (List<object>)c["centro_pt"];
                        l.Add(new SymbolCandidate
                        {
                            Page = page, Px = Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Py = Convert.ToDouble(p[1], CultureInfo.InvariantCulture),
                            Code = Arg.GetString(c, "codigo"), Signature = Arg.GetString(c, "firma"), Label = Arg.GetString(c, "rotulo"),
                            Confidence = c.TryGetValue("confianza", out var cf) && cf != null ? Convert.ToDouble(cf, CultureInfo.InvariantCulture) : 0,
                            RotDeg = c.TryGetValue("rot_deg", out var rd) && rd != null ? Convert.ToDouble(rd, CultureInfo.InvariantCulture) : 0,
                        });
                    }
            }
            return l;
        }

        private static void Amb(PlacementItem it, string estado, string motivo) { it.Estado = "ambiguo"; it.Motivo = estado + ": " + motivo; }

        private static double Norm180(double a) { a %= 360; if (a > 180) a -= 360; if (a <= -180) a += 360; return a; }
    }
}
