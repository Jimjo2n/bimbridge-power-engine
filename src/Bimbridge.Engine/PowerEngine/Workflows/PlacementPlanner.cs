// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>Candidate symbol read from the PDF (pdf_symbols output).</summary>
    public sealed class SymbolCandidate
    {
        public double Px, Py;            // pt, visible space
        public string Code, Signature, Label;
        public double Confidence;
        public double RotDeg;            // rotation of the symbol on the page
        public int Page = 1;
    }

    /// <summary>How a drawing code materializes in the model (decided by the person or the fixture schedule, not the agent).</summary>
    public sealed class TypeRule
    {
        public string Code;
        public string FamilyType;        // "Family: Type" as used by the place operation
        public double? ElevFt;           // offset above the level; null = use Mounting
        public string Mounting;           // "losa" (underside of what is above) or another place mode
        public string Workset;
        public string Host;              // required host (ceiling, wall, slab…) — declared constraint
        public bool Directional;         // if true, the symbol orientation matters (strips, sconces)
        public Dictionary<string, object> Params = new Dictionary<string, object>();
    }

    /// <summary>Element that already exists in the model (to avoid duplicates).</summary>
    public sealed class ExistingElement
    {
        public long Id; public double X, Y; public string FamilyType, Level;
    }

    public sealed class PlacementOptions
    {
        public string Level;
        public double MinConfidence = 0.9;
        public double DuplicateTolFt = 0.5;      // two symbols closer than this in the model = near-duplicate (6")
        public double ExistingTolFt = 0.5;
        public int BatchMax = 500;               // maximum of the place operation
        public string SourceId, SourceSha256, SourceRevision;
        public double MinRotationDeg = 0.5;      // below this, no rotation
    }

    public sealed class PlacementItem
    {
        public string Id, Code, FamilyType, Level, Workset, Host, Mounting, State, Reason;
        public double X, Y, RotDeg, Confidence;
        public double? ElevFt;
        public double Px, Py; public int Page;
        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["codigo"] = Code, ["tipo"] = FamilyType, ["nivel"] = Level, ["xy_ft"] = new List<object> { R(X), R(Y) },
                ["giro_deg"] = Math.Round(RotDeg, 3), ["confianza"] = Confidence, ["estado"] = State,
                ["fuente"] = new Dictionary<string, object> { ["pagina"] = (long)Page, ["punto_pt"] = new List<object> { R(Px), R(Py) } },
            };
            if (ElevFt.HasValue) d["elev_ft"] = R(ElevFt.Value);
            if (Mounting != null) d["montaje"] = Mounting;
            if (Workset != null) d["ws"] = Workset;
            if (Host != null) d["anfitrion"] = Host;
            if (Reason != null) d["motivo"] = Reason;
            return d;
        }
        private static object R(double v) => Math.Round(v, 5);
    }

    public sealed class PlacementPlan
    {
        public string State = "listo";
        public RegistrationResult Registration;
        public List<PlacementItem> Place = new List<PlacementItem>();
        public List<PlacementItem> Ambiguous = new List<PlacementItem>();
        public List<PlacementItem> AlreadyThere = new List<PlacementItem>();
        public List<Dictionary<string, object>> Batches = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Source = new Dictionary<string, object>();
        public string Fingerprint;

        public Dictionary<string, object> ToJson(bool items = true) => new Dictionary<string, object>
        {
            ["estado"] = State, ["fuente"] = Source, ["huella_plan"] = Fingerprint,
            ["resumen"] = new Dictionary<string, object> { ["colocar"] = (long)Place.Count, ["ambiguos"] = (long)Ambiguous.Count, ["ya_existen"] = (long)AlreadyThere.Count, ["lotes"] = (long)Batches.Count },
            ["registro"] = Registration?.ToJson(),
            ["colocar"] = items ? Place.Select(p => (object)p.ToJson()).ToList() : null,
            ["ambiguos"] = Ambiguous.Select(p => (object)p.ToJson()).ToList(),
            ["ya_existen"] = items ? AlreadyThere.Select(p => (object)p.ToJson()).ToList() : null,
            ["lotes"] = Batches.Cast<object>().ToList(),
        };
    }

    /// <summary>
    /// Placement planner: PDF symbols + registration + type rules → deterministic plan. Resolves what has a single
    /// answer; everything else (low confidence, no type, near-duplicates, conflict with existing elements) is ESCALATED as ambiguous, never
    /// guessed. Produces batches of the typed "place" operation (≤500 points) grouped by type, level, mounting, rotation and workset:
    /// execution reuses the existing plan/commit (queue, journal, idempotency). Does not call Revit.
    /// </summary>
    public static class PlacementPlanner
    {
        public static PlacementPlan Plan(IList<SymbolCandidate> symbols, RegistrationResult reg, IList<TypeRule> rules, PlacementOptions o,
                                         IList<ExistingElement> existing = null, PowerEngineDispatcher engine = null)
        {
            var plan = new PlacementPlan { Registration = reg };
            plan.Source["id"] = o.SourceId; plan.Source["sha256"] = o.SourceSha256; plan.Source["revision"] = o.SourceRevision;
            if (reg == null || !reg.Ok) { plan.State = "sin_registro"; return plan; }
            if (string.IsNullOrWhiteSpace(o.Level)) { plan.State = "sin_nivel"; return plan; }
            var byCode = (rules ?? new List<TypeRule>()).GroupBy(r => r.Code ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var regRot = reg.RotationDeg;

            // 1) transform all symbols (a single call: C# or Rust depending on size)
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
                if (rule == null) Amb(it, "sin_tipo", "code «" + (s.Code ?? "?") + "» has no type rule: a person or the fixture schedule decides");
                else if (rule.Count > 1) Amb(it, "tipo_ambiguo", "code «" + s.Code + "» has " + rule.Count + " type rules");
                else
                {
                    var r = rule[0];
                    it.FamilyType = r.FamilyType; it.ElevFt = r.ElevFt; it.Mounting = r.Mounting; it.Workset = r.Workset; it.Host = r.Host;
                    var rot = r.Directional ? Norm180(s.RotDeg + regRot) : 0.0;
                    it.RotDeg = Math.Abs(rot) < o.MinRotationDeg ? 0 : rot;
                    if (s.Confidence < o.MinConfidence) Amb(it, "confianza_baja", "confidence " + s.Confidence.ToString("0.##", CultureInfo.InvariantCulture) + " < " + o.MinConfidence.ToString("0.##", CultureInfo.InvariantCulture));
                    else if (!it.ElevFt.HasValue && string.IsNullOrEmpty(it.Mounting)) Amb(it, "sin_altura", "the rule gives neither height nor mounting mode: nothing is invented");
                    else it.State = "colocar";
                }
                items.Add(it);
            }

            // 2) near-duplicates among symbols (in model feet): the highest-confidence one is placed (tie-break: lowest index); the rest, ambiguous
            if (items.Count > 1)
            {
                var pts = new double[items.Count * 2];
                for (var i = 0; i < items.Count; i++) { pts[2 * i] = items[i].X; pts[2 * i + 1] = items[i].Y; }
                var grp = engine != null ? engine.Duplicates(pts, 2, o.DuplicateTolFt) : GeometryKernels.Duplicates(pts, 2, o.DuplicateTolFt);
                foreach (var g in Enumerable.Range(0, items.Count).GroupBy(i => grp[i]).Where(g => g.Count() > 1))
                {
                    var keep = g.OrderByDescending(i => items[i].Confidence).ThenBy(i => i).First();
                    foreach (var i in g.Where(i => i != keep))
                        Amb(items[i], "casi_duplicado", "within " + o.DuplicateTolFt.ToString("0.##", CultureInfo.InvariantCulture) + " ft of " + items[keep].Id + " (only that one is placed; confirm whether they are two fixtures)");
                }
            }

            // 3) what already exists in the model
            var ex = (existing ?? new List<ExistingElement>())
                .Where(e => string.IsNullOrWhiteSpace(e.Level) || string.Equals(e.Level, o.Level, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ex.Count > 0)
            {
                var cand = items.Where(i => i.State == "colocar").ToList();
                var q = new double[cand.Count * 2];
                for (var i = 0; i < cand.Count; i++) { q[2 * i] = cand[i].X; q[2 * i + 1] = cand[i].Y; }
                var r = new double[ex.Count * 2];
                for (var j = 0; j < ex.Count; j++) { r[2 * j] = ex[j].X; r[2 * j + 1] = ex[j].Y; }
                var nn = engine != null ? engine.Nearest(q, r, 2, o.ExistingTolFt) : GeometryKernels.Nearest(q, r, 2, o.ExistingTolFt);
                var claims = Enumerable.Range(0, cand.Count)
                    .Where(i => nn.Index[i] >= 0 && string.Equals(ex[nn.Index[i]].FamilyType, cand[i].FamilyType, StringComparison.Ordinal))
                    .ToLookup(i => nn.Index[i]);
                for (var i = 0; i < cand.Count; i++)
                {
                    if (nn.Index[i] < 0) continue;
                    var e = ex[nn.Index[i]];
                    if (!string.Equals(e.FamilyType, cand[i].FamilyType, StringComparison.Ordinal))
                        Amb(cand[i], "conflicto_existente", "element " + e.Id + " of another type (" + e.FamilyType + ") at " + nn.Dist[i].ToString("0.###", CultureInfo.InvariantCulture) + " ft");
                    else if (claims[nn.Index[i]].Count() > 1)
                        Amb(cand[i], "existente_compartido", "element " + e.Id + " matches more than one symbol");
                    else
                    {
                        cand[i].State = "ya_existe";
                        cand[i].Reason = "element " + e.Id + " of the same type at " + nn.Dist[i].ToString("0.###", CultureInfo.InvariantCulture) + " ft";
                    }
                }
            }

            plan.Place = items.Where(i => i.State == "colocar").ToList();
            plan.Ambiguous = items.Where(i => i.State == "ambiguo").ToList();
            plan.AlreadyThere = items.Where(i => i.State == "ya_existe").ToList();

            // 4) "place" batches: same type, level, mounting/elev, rotation and workset; ≤ BatchMax points; stable order
            foreach (var g in plan.Place.GroupBy(i => (i.FamilyType, i.Level, i.Mounting, Elev: i.ElevFt.HasValue ? Math.Round(i.ElevFt.Value, 6) : (double?)null, Rot: Math.Round(i.RotDeg, 3), i.Workset, i.Host,
                                            Parameters: OpSemantics.Canonical(byCode[i.Code][0].Params)))
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
                        ["items_fuente"] = chunk.Select(i => (object)i.Id).ToList(),
                    };
                    if (g.Key.Elev.HasValue) op["elev"] = g.Key.Elev.Value; else op["montaje"] = g.Key.Mounting;
                    if (Math.Abs(g.Key.Rot) > 0) op["rot"] = g.Key.Rot;
                    if (g.Key.Workset != null) op["ws"] = g.Key.Workset;
                    if (!string.IsNullOrWhiteSpace(g.Key.Host)) op["anfitrion"] = g.Key.Host;
                    if (rule.Params.Count > 0) op["params"] = rule.Params;
                    plan.Batches.Add(new Dictionary<string, object> { ["operacion"] = op, ["items"] = chunk.Select(i => (object)i.Id).ToList() });
                }
            }
            plan.Fingerprint = Fingerprint(plan.Place);
            if (plan.Ambiguous.Count > 0) plan.State = plan.Place.Count > 0 ? "listo_con_ambiguedades" : "solo_ambiguedades";
            return plan;
        }

        /// <summary>Plan fingerprint (same criterion as IndependentVerifier.Fingerprint, with the plan's stable ids as key): allows
        /// rejecting a verification whose "expected" is the plan itself copied. An independent source that matches in positions
        /// does not share the plan ids, so an accurate match is not mistaken for a copy.</summary>
        public static string Fingerprint(IEnumerable<PlacementItem> items) =>
            Bimbridge.Engine.Workflow.IndependentVerifier.Fingerprint(items.Select(i => new Bimbridge.Engine.Workflow.IndependentVerifier.Item
            { Key = i.Id, X = i.X, Y = i.Y, Attrs = { ["tipo"] = i.FamilyType ?? "" } }));

        public static string StableId(string sourceSha, int page, double px, double py, string code) =>
            "pl-" + Home.Sha256Hex((sourceSha ?? "") + "|" + page.ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(px * 8).ToString(CultureInfo.InvariantCulture) + "|" +
                                   Math.Round(py * 8).ToString(CultureInfo.InvariantCulture) + "|" + (code ?? "")).Substring(0, 16);

        /// <summary>Reads the candidates (and ambiguous ones, marked with their confidence) from the pdf_symbols output.</summary>
        public static List<SymbolCandidate> FromPdfSymbols(Dictionary<string, object> result, bool includeAmbiguous = true, bool confirmedOnly = false)
        {
            var l = new List<SymbolCandidate>();
            foreach (Dictionary<string, object> pg in (List<object>)result["paginas"])
            {
                var page = Convert.ToInt32(pg["pagina"], CultureInfo.InvariantCulture);
                foreach (var key in confirmedOnly ? new[] { "confirmados" } : includeAmbiguous ? new[] { "candidatos", "ambiguos" } : new[] { "candidatos" })
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

        private static void Amb(PlacementItem it, string status, string reason) { it.State = "ambiguo"; it.Reason = status + ": " + reason; }

        private static double Norm180(double a) { a %= 360; if (a > 180) a -= 360; if (a <= -180) a += 360; return a; }
    }
}
