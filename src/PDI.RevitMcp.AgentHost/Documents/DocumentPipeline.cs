// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Documento → requisitos → IR (P1 §5). Tres etapas separadas, cada una con su salida inspeccionable:
    /// 1. <see cref="RequirementExtractor"/>: EXTRAE requisitos literales con su página, sección e identificador (sin interpretar).
    /// 2. <see cref="RequirementInterpreter"/>: INTERPRETA cada requisito con reglas deterministas y cerradas; lo ambiguo, lo sospechoso y lo
    ///    que no encaja queda marcado, nunca se "adivina".
    /// 3. <see cref="PlanBuilder"/>: CONSTRUYE un plan IR sólo con requisitos de confianza alta y sin ambigüedad; el resto va a la cola de
    ///    revisión con sus motivos. La ejecución es otra etapa (PlanRunner) que vuelve a validar el plan.
    /// El contenido del documento es DATO NO CONFIABLE: no hay ningún campo del IR que conceda permisos o cambie políticas, el validador
    /// rechaza campos de sistema, y las frases que intentan dar órdenes al sistema se marcan como contenido_sospechoso.
    /// Formato de entrada de alpha1 (texto estructurado, p. ej. extraído de un PDF por una persona o una herramienta aparte):
    ///   # DOCUMENTO: título
    ///   === PAGINA n ===
    ///   ## 2.1 Sección
    ///   [REQ-001] texto del requisito
    /// La extracción desde PDF no forma parte de alpha1.
    /// </summary>
    public sealed class SourceDocument
    {
        public string Name, Title, Sha256;
        public List<RawRequirement> Requirements = new List<RawRequirement>();
        public List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public int Pages;
    }

    public sealed class RawRequirement
    {
        public string DocSha, ReqId, Section, Original;
        public int Page, Line;
    }

    public static class RequirementExtractor
    {
        private static readonly Regex Page = new Regex(@"^===\s*(?:PAGINA|PÁGINA|PAGE)\s+(\d+)\s*===\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Section = new Regex(@"^##\s+(.+?)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex Title = new Regex(@"^#\s*(?:DOCUMENTO|DOCUMENT)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Req = new Regex(@"^\[(?<id>[A-Z][A-Z0-9]*-[0-9A-Z]+(?:[.\-][0-9A-Z]+)*)\]\s*(?<t>.*)$", RegexOptions.CultureInvariant);

        public static string Sha256Hex(byte[] b) { using (var s = SHA256.Create()) return string.Concat(s.ComputeHash(b).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))); }

        public static SourceDocument Extract(string name, byte[] bytes)
        {
            var doc = new SourceDocument { Name = name, Sha256 = Sha256Hex(bytes) };
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int page = 0; string section = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                Match m;
                if ((m = Title.Match(l)).Success) { doc.Title = m.Groups[1].Value; continue; }
                if ((m = Page.Match(l)).Success) { page = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); doc.Pages = Math.Max(doc.Pages, page); section = null; continue; }
                if ((m = Section.Match(l)).Success) { section = m.Groups[1].Value; continue; }
                if ((m = Req.Match(l)).Success)
                {
                    var id = m.Groups["id"].Value;
                    var t = m.Groups["t"].Value.Trim();
                    // continuación: líneas siguientes que no son marcador ni requisito pertenecen a este requisito
                    while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !Req.IsMatch(lines[i + 1].Trim()) && !Page.IsMatch(lines[i + 1].Trim()) && !Section.IsMatch(lines[i + 1].Trim()) && !lines[i + 1].TrimStart().StartsWith("#", StringComparison.Ordinal))
                        t += " " + lines[++i].Trim();
                    if (!seen.Add(id)) { doc.Diagnostics.Add(Diagnostic.Error("requisito_duplicado", id + " (línea " + (i + 1) + ")")); continue; }
                    if (page == 0) doc.Diagnostics.Add(Diagnostic.Warn("requisito_sin_pagina", id));
                    if (t.Length == 0) { doc.Diagnostics.Add(Diagnostic.Warn("requisito_vacio", id)); continue; }
                    doc.Requirements.Add(new RawRequirement { DocSha = doc.Sha256, ReqId = id, Section = section, Original = t, Page = page, Line = i + 1 });
                }
            }
            return doc;
        }
    }

    public sealed class NormalizedRequirement
    {
        public RawRequirement Raw;
        public string Kind = "no_interpretable";     // fijar_parametro | verificacion | no_interpretable
        public string Category, Level, Parameter, Unit, Recipe;
        public object Value;
        public double? Tolerance;
        public string Confidence = "baja";           // alta | media | baja
        public List<string> Ambiguities = new List<string>();
        public List<string> Suspicious = new List<string>();
        public List<string> DependsOnReqs = new List<string>();
        public List<string> Unresolved = new List<string>();
        public string Verification;
        public bool Executable => Confidence == "alta" && Ambiguities.Count == 0 && Suspicious.Count == 0 && Unresolved.Count == 0 && Kind != "no_interpretable";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["requisito"] = Raw.ReqId, ["pagina"] = (long)Raw.Page, ["seccion"] = Raw.Section, ["original"] = Raw.Original, ["tipo"] = Kind,
            ["categoria"] = Category, ["nivel"] = Level, ["parametro"] = Parameter, ["valor"] = Value, ["unidades"] = Unit,
            ["tolerancia"] = Tolerance, ["receta"] = Recipe, ["confianza"] = Confidence,
            ["ambiguedades"] = Ambiguities.Cast<object>().ToList(), ["sospechoso"] = Suspicious.Cast<object>().ToList(),
            ["depende_de"] = DependsOnReqs.Cast<object>().ToList(), ["sin_resolver"] = Unresolved.Cast<object>().ToList(),
            ["verificacion"] = Verification, ["ejecutable"] = Executable,
        };
    }

    public static class RequirementInterpreter
    {
        private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        public static readonly Dictionary<string, string> Categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lighting fixtures"] = "OST_LightingFixtures", ["light fixtures"] = "OST_LightingFixtures", ["luminarias"] = "OST_LightingFixtures",
            ["lighting devices"] = "OST_LightingDevices", ["dispositivos de iluminación"] = "OST_LightingDevices",
            ["electrical fixtures"] = "OST_ElectricalFixtures", ["aparatos eléctricos"] = "OST_ElectricalFixtures",
        };

        private static readonly Regex After = new Regex(@"\b(?:after|despu[eé]s de|tras)\s+(?<r>[A-Z][A-Z0-9]*-[0-9A-Z]+(?:[.\-][0-9A-Z]+)*)\.?", O);
        private static readonly Regex Set = new Regex(
            @"^(?:set|fijar|establecer)\s+(?:the\s+|el\s+)?(?:parameter\s+|par[aá]metro\s+)?""(?<p>[^""]+)""\s+(?:to|a|en|=)\s+(?<v>""[^""]*""|-?\d+(?:\.\d+)?)\s*(?<u>mm|m|ft|in)?\s+" +
            @"(?:for|para|en)\s+(?:all|every|todas?\s+las|todos?\s+los)\s+(?<c>[\p{L}\s]+?)" +
            @"(?:\s+(?:on|in|en|del?)\s+(?:the\s+|el\s+)?(?:level|nivel)?\s*(?<l>[A-Za-z]*\s*\d+))?" +
            @"(?:\s*\(\s*(?:tolerance|tolerancia)\s*(?:±|\+/-)\s*(?<t>\d+(?:\.\d+)?)\s*(?<tu>mm|m|ft|in)\s*\))?\s*\.?\s*$", O);
        private static readonly Regex Verify = new Regex(
            @"^(?:verify|check|verificar|comprobar)\s+(?:that\s+|que\s+)?(?:the\s+|el\s+)?(?<what>qc manifest|manifiesto qc|manifiesto de qc|mounting supports|montaje de soportes|soportes de montaje)" +
            @"(?:\s+(?:on|in|en|del?)\s+(?:the\s+|el\s+)?(?:level|nivel)?\s*(?<l>[A-Za-z]*\s*\d+))?\s*\.?\s*$", O);
        private static readonly Regex Hedge = new Regex(@"\b(approx(?:imately)?|about|aprox(?:imadamente)?|as (?:needed|required)|where (?:appropriate|possible)|seg[uú]n (?:se requiera|convenga|criterio)|tbd|tbc|por definir|verify in field|verificar en (?:sitio|obra)|or similar|o similar|typical|t[ií]pico|unless noted|salvo indicaci[oó]n)\b", O);
        private static readonly Regex Injection = new Regex(@"\b(ignore|ignora|disregard|olvida)\b.{0,40}\b(instruc|instrucciones|previous|anteriores|rules|reglas)|\b(grant|conceder|conced[ae]|otorgar|otorga)\b|\bpermis(?:sion|o)s?\b|\bautoriza(?:ci[oó]n|r)?\b|\bauthori[sz](?:e|ation)\b|system prompt|\bbypass\b|desactiva(?:r)? (?:la )?seguridad|\b(?:run|execute|ejecuta(?:r)?)\b.{0,20}\b(?:code|c[oó]digo|script|comando|command)\b|\b(?:api key|token|password|contrase[nñ]a)\b|\b(?:install|instalar|restart|reiniciar)\b|\b(?:sync(?:hronize)?|sincroniza(?:r)?)\b|\b(?:delete|borra(?:r)?|elimina(?:r)?)\b", O);

        public static string NormalizeLevel(string l)
        {
            if (string.IsNullOrWhiteSpace(l)) return null;
            var d = new string(l.Where(char.IsDigit).ToArray());
            if (d.Length == 0) return null;
            return "L" + int.Parse(d, CultureInfo.InvariantCulture).ToString("00", CultureInfo.InvariantCulture);
        }

        public static List<NormalizedRequirement> Interpret(SourceDocument doc)
        {
            var res = new List<NormalizedRequirement>();
            var ids = new HashSet<string>(doc.Requirements.Select(r => r.ReqId), StringComparer.Ordinal);
            foreach (var raw in doc.Requirements)
            {
                var n = new NormalizedRequirement { Raw = raw };
                var t = raw.Original;
                foreach (Match m in Injection.Matches(t)) n.Suspicious.Add("contenido_sospechoso: «" + m.Value + "» (el documento no puede dar órdenes al sistema)");
                foreach (Match m in After.Matches(t))
                {
                    var r = m.Groups["r"].Value;
                    n.DependsOnReqs.Add(r);
                    if (!ids.Contains(r)) n.Unresolved.Add("dependencia_no_resuelta: " + r + " no existe en el documento");
                }
                t = After.Replace(t, "").Trim().TrimEnd('.', ' ').Trim();
                foreach (Match m in Hedge.Matches(t)) n.Ambiguities.Add("lenguaje_ambiguo: «" + m.Value + "»");
                Match s;
                if ((s = Set.Match(t)).Success)
                {
                    n.Kind = "fijar_parametro";
                    n.Parameter = s.Groups["p"].Value;
                    var v = s.Groups["v"].Value;
                    if (v.StartsWith("\"", StringComparison.Ordinal)) { n.Value = v.Trim('"'); n.Unit = s.Groups["u"].Success ? s.Groups["u"].Value : "texto"; }
                    else
                    {
                        n.Value = v.Contains(".") ? (object)double.Parse(v, CultureInfo.InvariantCulture) : long.Parse(v, CultureInfo.InvariantCulture);
                        if (s.Groups["u"].Success) n.Unit = s.Groups["u"].Value.ToLowerInvariant();
                        else n.Ambiguities.Add("unidades_no_explicitas: valor numérico " + v + " sin unidad");
                    }
                    var cat = Regex.Replace(s.Groups["c"].Value.Trim(), @"\s+", " ");
                    if (Categories.TryGetValue(cat, out var ost)) n.Category = ost; else n.Ambiguities.Add("categoria_desconocida: «" + cat + "»");
                    n.Level = NormalizeLevel(s.Groups["l"].Value);
                    if (s.Groups["t"].Success)
                    {
                        if (n.Unit != null && string.Equals(s.Groups["tu"].Value, n.Unit, StringComparison.OrdinalIgnoreCase)) n.Tolerance = double.Parse(s.Groups["t"].Value, CultureInfo.InvariantCulture);
                        else n.Ambiguities.Add("tolerancia_en_otra_unidad");
                    }
                    n.Verification = "postcondición: todos los objetivos con " + n.Parameter + " = " + Values.Show(n.Value) + (n.Unit != null && n.Unit != "texto" ? " " + n.Unit : "");
                    n.Confidence = n.Level == null ? "media" : "alta";
                    if (n.Level == null) n.Ambiguities.Add("alcance_sin_nivel: afectaría a la categoría completa en todos los niveles");
                }
                else if ((s = Verify.Match(t)).Success)
                {
                    n.Kind = "verificacion";
                    var what = s.Groups["what"].Value.ToLowerInvariant();
                    n.Recipe = what.Contains("manifest") || what.Contains("manifiesto") ? "qc_manifiesto" : "montaje_soportes";
                    n.Level = NormalizeLevel(s.Groups["l"].Value);
                    n.Verification = "postcondición: desviaciones = 0 (" + n.Recipe + ")";
                    n.Confidence = "alta";
                }
                else n.Ambiguities.Add("no_interpretable: no coincide con ninguna forma admitida; se conserva para revisión");
                res.Add(n);
            }
            // dependencias hacia requisitos no ejecutables también quedan sin resolver (no se ejecuta algo cuyo requisito previo no se ejecutará)
            bool changed;
            do
            {
                changed = false;
                var execSet = new HashSet<string>(res.Where(r => r.Executable).Select(r => r.Raw.ReqId), StringComparer.Ordinal);
                foreach (var r in res.Where(x => x.Executable))
                    foreach (var d in r.DependsOnReqs)
                        if (!execSet.Contains(d)) { r.Unresolved.Add("dependencia_no_ejecutable: " + d + " queda en revisión"); changed = true; }
            } while (changed);
            return res;
        }
    }

    public sealed class BuildResult
    {
        public PlanDraft Plan;
        public List<NormalizedRequirement> Review = new List<NormalizedRequirement>();
        public Dictionary<string, string> OpByRequirement = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Errors = new List<string>();
    }

    public static class PlanBuilder
    {
        public static BuildResult Build(SourceDocument doc, List<NormalizedRequirement> reqs, string docKey, string docPrint, string lengthUnit = "mm", string coordinates = "interno_revit")
        {
            var res = new BuildResult();
            var plan = new PlanDraft { Objective = "Requisitos de " + (doc.Title ?? doc.Name), DocKey = docKey, DocPrint = docPrint, LengthUnit = lengthUnit, Coordinates = coordinates };
            plan.Sources.Add(new SourceDoc { Id = "doc-1", Nombre = doc.Name, Sha256 = doc.Sha256 });
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var semantic = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in reqs)
            {
                if (!r.Executable) { res.Review.Add(r); continue; }
                var op = new OpDraft { Source = new SourceRef { Fuente = "doc-1", Pagina = r.Raw.Page > 0 ? r.Raw.Page : (long?)null, Seccion = r.Raw.Section, Requisito = r.Raw.ReqId, Cita = r.Raw.Original.Length > 200 ? r.Raw.Original.Substring(0, 199) + "…" : r.Raw.Original } };
                if (r.Kind == "fijar_parametro")
                {
                    op.Operation = "fijar_parametro";
                    op.Scope["categoria"] = r.Category; op.Scope["nivel"] = r.Level;
                    op.Params["parametro"] = r.Parameter; op.Params["valor"] = r.Value; op.Params["unidades"] = r.Unit;
                    if (r.Tolerance != null) op.Params["tolerancia"] = r.Tolerance.Value;
                }
                else
                {
                    op.Operation = "verificar_qc";
                    op.Params["receta"] = r.Recipe;
                    if (r.Level != null) op.Scope["nivel"] = r.Level;
                }
                var sk = IrIds.SemanticKey(op.Scope, op.Operation, op.Params);
                if (semantic.TryGetValue(sk, out var first)) { r.Ambiguities.Add("duplicado_de: " + first); res.Review.Add(r); continue; }
                semantic[sk] = r.Raw.ReqId;
                index[r.Raw.ReqId] = plan.Ops.Count;
                op.DependsOn.AddRange(r.DependsOnReqs.Select(d => "@" + d));
                plan.Ops.Add(op);
            }
            // dependencias por requisito → por índice; un requisito que dependa de uno enviado a revisión también va a revisión
            foreach (var op in plan.Ops.ToList())
                op.DependsOn = op.DependsOn.Select(d => index.TryGetValue(d.Substring(1), out var i) ? "#" + i.ToString(CultureInfo.InvariantCulture) : "!" + d.Substring(1)).ToList();
            plan.AssignIdsSafe(res.Errors);
            foreach (var op in plan.Ops) res.OpByRequirement[op.Source.Requisito] = op.Id;
            res.Plan = plan;
            return res;
        }

        /// <summary>Comprueba que los documentos de origen siguen siendo los mismos (sha256). Un documento alterado invalida el plan.</summary>
        public static List<string> VerifySources(ValidatedPlan plan, IDictionary<string, byte[]> documents)
        {
            var errs = new List<string>();
            foreach (var s in plan.Sources)
            {
                if (!documents.TryGetValue(s.Nombre, out var b)) { errs.Add("fuente_no_disponible: " + s.Nombre); continue; }
                var sha = RequirementExtractor.Sha256Hex(b);
                if (sha != s.Sha256) errs.Add("fuente_alterada: " + s.Nombre + " sha256 " + sha.Substring(0, 12) + "… ≠ " + s.Sha256.Substring(0, 12) + "…");
            }
            return errs;
        }
    }

    internal static class PlanDraftExtensions
    {
        /// <summary>Como AssignIds, pero una dependencia marcada "!" (hacia un requisito no convertido) se informa como error.</summary>
        public static void AssignIdsSafe(this PlanDraft p, List<string> errors)
        {
            foreach (var op in p.Ops)
                foreach (var d in op.DependsOn.Where(x => x.StartsWith("!", StringComparison.Ordinal)))
                    errors.Add(op.Source?.Requisito + ": dependencia hacia " + d.Substring(1) + " no convertida");
            foreach (var op in p.Ops) op.DependsOn = op.DependsOn.Where(x => !x.StartsWith("!", StringComparison.Ordinal)).ToList();
            p.AssignIds();
        }
    }

    public static class DocumentCli
    {
        public static int Run(string[] a)
        {
            string Opt(string n) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            var path = a[1];
            var doc = RequirementExtractor.Extract(Path.GetFileName(path), File.ReadAllBytes(path));
            var reqs = RequirementInterpreter.Interpret(doc);
            var b = PlanBuilder.Build(doc, reqs, Opt("--clave") ?? "?", Opt("--huella") ?? "?");
            var errs = IrValidator.Validate(b.Plan, out var vp);
            Console.WriteLine("documento " + doc.Name + " sha256 " + doc.Sha256.Substring(0, 16) + "… · " + doc.Pages + " páginas · " + doc.Requirements.Count + " requisitos");
            foreach (var d in doc.Diagnostics) Console.WriteLine("diag " + d);
            Console.WriteLine("convertidos " + b.Plan.Ops.Count + " · a revisión " + b.Review.Count + (vp != null ? " · plan " + vp.Hash : ""));
            foreach (var r in b.Review) Console.WriteLine("revision " + r.Raw.ReqId + " p." + r.Raw.Page + ": " + string.Join("; ", r.Ambiguities.Concat(r.Suspicious).Concat(r.Unresolved)));
            foreach (var e in errs.Concat(b.Errors)) Console.WriteLine("error " + e);
            var outp = Opt("--salida");
            if (outp != null)
            {
                File.WriteAllText(outp, Json.Write(b.Plan.ToJson()));
                File.WriteAllText(Path.ChangeExtension(outp, ".revision.json"), Json.Write(new Dictionary<string, object>
                {
                    ["documento"] = doc.Name, ["sha256"] = doc.Sha256, ["requisitos"] = reqs.Select(r => (object)r.ToJson()).ToList(),
                }));
            }
            return errs.Count == 0 && b.Errors.Count == 0 ? 0 : 1;
        }
    }
}
