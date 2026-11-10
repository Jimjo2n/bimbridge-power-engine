// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (CAP-008): requisitos con procedencia. Cada requisito dice de dónde sale (fuente, revisión, ubicación), qué regla impone,
    /// sobre qué alcance, con qué autoridad y con qué confianza. Una instrucción posterior reemplaza SOLO el requisito al que se refiere.
    /// Las contradicciones se detectan; no se resuelven inventando: o gana una precedencia explícita o queda para una persona.
    /// </summary>
    public static class SourceKinds
    {
        public const string Usuario = "instruccion_usuario", Transcripcion = "transcripcion", Demostracion = "demostracion", Plano = "plano",
            Pdf = "pdf", Tabla = "tabla", Markup = "markup", Revision = "revision", DecisionProfesional = "decision_profesional",
            Inferencia = "inferencia_agente", Modelo = "modelo", PlanEjecucion = "plan_ejecucion";

        public static readonly string[] All = { Usuario, Transcripcion, Demostracion, Plano, Pdf, Tabla, Markup, Revision, DecisionProfesional, Inferencia, Modelo, PlanEjecucion };

        /// <summary>0.9.0: tipos que NUNCA son una fuente independiente para QA: el propio modelo, el plan que lo construyó o una inferencia.</summary>
        public static bool NeverIndependent(string kind) => kind == Inferencia || kind == Modelo || kind == PlanEjecucion;

        /// <summary>Autoridad por defecto (mayor gana). Una inferencia del agente nunca vence a una fuente documentada.</summary>
        public static int DefaultAuthority(string kind)
        {
            switch (kind)
            {
                case DecisionProfesional: return 90;
                case Revision: return 80;
                case Usuario: return 70;
                case Plano: case Pdf: case Tabla: return 60;
                case Markup: return 55;
                case Demostracion: return 50;
                case Transcripcion: return 40;
                case Inferencia: return 10;
                default: return 0;
            }
        }
    }

    /// <summary>Confianza (lo que se sabe) y evidencia (lo que se comprobó), con el vocabulario del informe de workflows.</summary>
    public static class Confidence
    {
        public const string Observed = "OBSERVED", Inferred = "INFERRED", Unknown = "UNKNOWN";
        public const string Comprobado = "COMPROBADO", Reportado = "REPORTADO", Pendiente = "PENDIENTE", Bloqueado = "BLOQUEADO";
        public static bool IsConfidence(string s) => s == Observed || s == Inferred || s == Unknown;
        public static bool IsEvidence(string s) => s == Comprobado || s == Reportado || s == Pendiente || s == Bloqueado;
    }

    public sealed class SourceRef
    {
        public string Id;          // identificador estable de la fuente (p. ej. "E-1.3.02@CCD7")
        public string Kind;        // SourceKinds
        public string Revision;    // revisión o fecha de emisión; null = sin revisión conocida
        public string Locator;     // página, minuto, celda…
        /// <summary>0.9.0: SHA-256 del contenido exacto de la fuente (archivo, extracción). Sin él la independencia no se puede comprobar.</summary>
        public string Sha256;
        /// <summary>0.9.0: linaje: ids o "sha256:…" de las fuentes de las que se derivó (una tabla sacada de un plano, un plan sacado de un markup…).</summary>
        public List<string> DerivedFrom = new List<string>();

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["id"] = Id, ["tipo"] = Kind, ["revision"] = Revision, ["ubicacion"] = Locator };
            if (Sha256 != null) d["sha256"] = Sha256;
            if (DerivedFrom.Count > 0) d["derivada_de"] = DerivedFrom.Cast<object>().ToList();
            return d;
        }

        public static SourceRef FromJson(Dictionary<string, object> d) => d == null ? null : new SourceRef
        {
            Id = Arg.GetString(d, "id"), Kind = Arg.GetString(d, "tipo"), Revision = Arg.GetString(d, "revision"), Locator = Arg.GetString(d, "ubicacion"),
            Sha256 = Arg.GetString(d, "sha256"), DerivedFrom = Arg.GetStringList(d, "derivada_de") ?? new List<string>(),
        };

        /// <summary>Identidades de esta fuente para comparar linajes: su id y su contenido.</summary>
        public IEnumerable<string> Identities()
        {
            if (!string.IsNullOrWhiteSpace(Id)) yield return "id:" + Id.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(Sha256)) yield return "sha256:" + Sha256.Trim().ToLowerInvariant();
        }

        /// <summary>Identidades de su linaje declarado, normalizadas igual que <see cref="Identities"/>.</summary>
        public IEnumerable<string> Lineage() => DerivedFrom.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x =>
        {
            var t = x.Trim().ToLowerInvariant();
            return t.StartsWith("sha256:", StringComparison.Ordinal) ? t : "id:" + t;
        });
    }

    public sealed class Requirement
    {
        public string Id;
        public SourceRef Source;
        public string Scope;          // alcance: "nivel=L03", "vista=…", "*"
        public string Key;            // qué se regula: "altura_montaje", "workset", "codigo"…
        public string Value;          // valor exigido (texto); null = la fuente no lo fija (no se inventa)
        public int Authority = -1;    // -1 = la de su tipo de fuente
        public string Confidence = Workflow.Confidence.Observed;
        public string Acceptance;     // criterio de aceptación legible
        public List<long> Elements = new List<long>();
        public List<string> Supersedes = new List<string>();
        public string Note;

        public int EffectiveAuthority => Authority >= 0 ? Authority : SourceKinds.DefaultAuthority(Source?.Kind);

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["fuente"] = Source?.ToJson(), ["alcance"] = Scope, ["clave"] = Key, ["valor"] = Value,
                ["autoridad"] = (long)EffectiveAuthority, ["confianza"] = Confidence,
            };
            if (Acceptance != null) d["aceptacion"] = Acceptance;
            if (Elements.Count > 0) d["elementos"] = Elements.Cast<object>().ToList();
            if (Supersedes.Count > 0) d["reemplaza"] = Supersedes.Cast<object>().ToList();
            if (Note != null) d["nota"] = Note;
            return d;
        }

        public static Requirement FromJson(Dictionary<string, object> d)
        {
            var r = new Requirement
            {
                Id = Arg.GetString(d, "id"), Source = SourceRef.FromJson(d.TryGetValue("fuente", out var f) ? f as Dictionary<string, object> : null),
                Scope = Arg.GetString(d, "alcance") ?? "*", Key = Arg.GetString(d, "clave"), Value = Arg.GetString(d, "valor"),
                Confidence = Arg.GetString(d, "confianza") ?? Workflow.Confidence.Observed, Acceptance = Arg.GetString(d, "aceptacion"), Note = Arg.GetString(d, "nota"),
            };
            var a = Arg.GetLong(d, "autoridad"); if (a.HasValue) r.Authority = (int)a.Value;
            if (d.TryGetValue("elementos", out var e) && e is List<object> el) r.Elements = el.Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList();
            if (d.TryGetValue("reemplaza", out var s) && s is List<object> sl) r.Supersedes = sl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
            return r;
        }
    }

    public sealed class RequirementConflict
    {
        public string Scope, Key;
        public List<Requirement> Candidates = new List<Requirement>();
        public string Reason;
        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["alcance"] = Scope, ["clave"] = Key, ["motivo"] = Reason,
            ["candidatos"] = Candidates.Select(c => (object)new Dictionary<string, object> { ["id"] = c.Id, ["valor"] = c.Value, ["fuente"] = c.Source?.Id, ["autoridad"] = (long)c.EffectiveAuthority }).ToList(),
        };
    }

    /// <summary>
    /// Conjunto versionado de requisitos. <see cref="Resolve"/> devuelve el requisito vigente por (alcance, clave) o un conflicto.
    /// Reglas, en orden: (1) un requisito reemplazado explícitamente deja de regir; (2) gana la mayor autoridad;
    /// (3) a igual autoridad y misma fuente, gana la revisión posterior; (4) si aun así hay valores distintos → conflicto (persona).
    /// Un requisito sin valor (la fuente no fija el dato) nunca se completa: queda UNKNOWN y se informa como dato faltante.
    /// </summary>
    public sealed class RequirementSet
    {
        private readonly List<Requirement> _all = new List<Requirement>();
        public IReadOnlyList<Requirement> All => _all;

        public string Add(Requirement r)
        {
            if (r == null) return "requisito nulo";
            if (string.IsNullOrWhiteSpace(r.Id)) return "requisito sin id";
            if (_all.Any(x => x.Id == r.Id)) return "id repetido: " + r.Id;
            if (r.Source == null || string.IsNullOrWhiteSpace(r.Source.Id)) return r.Id + ": falta la fuente (sin procedencia no se acepta)";
            if (!SourceKinds.All.Contains(r.Source.Kind)) return r.Id + ": tipo de fuente desconocido " + r.Source.Kind;
            if (string.IsNullOrWhiteSpace(r.Key)) return r.Id + ": falta la clave regulada";
            if (!Confidence.IsConfidence(r.Confidence)) return r.Id + ": confianza debe ser OBSERVED, INFERRED o UNKNOWN";
            if (r.Source.Kind == SourceKinds.Inferencia && r.Confidence == Confidence.Observed) return r.Id + ": una inferencia del agente no puede declararse OBSERVED";
            foreach (var s in r.Supersedes) if (!_all.Any(x => x.Id == s)) return r.Id + ": reemplaza a " + s + ", que no existe";
            if (string.IsNullOrWhiteSpace(r.Scope)) r.Scope = "*";
            _all.Add(r);
            return null;
        }

        public bool IsSuperseded(string id) => _all.Any(x => x.Supersedes.Contains(id));

        public sealed class Resolution
        {
            public Requirement Winner;
            public RequirementConflict Conflict;
            public bool MissingValue => Winner != null && Winner.Value == null;
        }

        public Resolution Resolve(string scope, string key)
        {
            var live = _all.Where(r => r.Key == key && (r.Scope == scope || r.Scope == "*") && !IsSuperseded(r.Id)).ToList();
            if (live.Count == 0) return new Resolution();
            // lo específico gana a lo general (alcance exacto sobre "*") solo cuando la autoridad no es menor
            var maxAuth = live.Max(r => r.EffectiveAuthority);
            var top = live.Where(r => r.EffectiveAuthority == maxAuth).ToList();
            if (top.Count > 1 && top.Any(r => r.Scope == scope) && top.Any(r => r.Scope == "*")) top = top.Where(r => r.Scope == scope).ToList();
            if (top.Count > 1)
            {
                // misma fuente, distinta revisión → la posterior
                var bySource = top.GroupBy(r => r.Source.Id).ToList();
                if (bySource.Count == 1)
                {
                    var ordered = top.OrderBy(r => r.Source.Revision ?? "", StringComparer.Ordinal).ToList();
                    if (ordered.Select(r => r.Source.Revision ?? "").Distinct().Count() == ordered.Count) top = new List<Requirement> { ordered.Last() };
                }
            }
            if (top.Count > 1 && top.Select(r => r.Value ?? "\u0000").Distinct().Count() > 1)
                return new Resolution { Conflict = new RequirementConflict { Scope = scope, Key = key, Candidates = top, Reason = "valores distintos con la misma autoridad y sin reemplazo explícito: decide una persona" } };
            return new Resolution { Winner = top[0] };
        }

        /// <summary>Todos los conflictos vigentes y los datos que ninguna fuente fija.</summary>
        public (List<RequirementConflict> conflicts, List<Requirement> missing) Audit()
        {
            var conflicts = new List<RequirementConflict>();
            var missing = new List<Requirement>();
            foreach (var g in _all.Where(r => r.Scope != "*").Select(r => (r.Scope, r.Key)).Concat(_all.Where(r => r.Scope == "*").Select(r => ("*", r.Key))).Distinct())
            {
                var res = Resolve(g.Item1, g.Item2);
                if (res.Conflict != null) conflicts.Add(res.Conflict);
                else if (res.MissingValue) missing.Add(res.Winner);
            }
            return (conflicts, missing);
        }

        public List<object> ToJson() => _all.Select(r => (object)r.ToJson()).ToList();

        public static RequirementSet FromJson(List<object> items, List<string> errors)
        {
            var set = new RequirementSet();
            foreach (var it in items ?? new List<object>())
            {
                var d = it as Dictionary<string, object>;
                if (d == null) { errors.Add("requisito no es un objeto"); continue; }
                var e = set.Add(Requirement.FromJson(d));
                if (e != null) errors.Add(e);
            }
            return set;
        }
    }
}
