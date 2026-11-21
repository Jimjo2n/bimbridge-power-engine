// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (CAP-008): requirements with provenance. Each requirement states where it comes from (source, revision, location), which rule it imposes,
    /// on which scope, with which authority and with which confidence. A later instruction replaces ONLY the requirement it refers to.
    /// Contradictions are detected, not resolved by invention: either an explicit precedence wins or a person decides.
    /// </summary>
    public static class SourceKinds
    {
        public const string Usuario = "instruccion_usuario", Transcripcion = "transcripcion", Demostracion = "demostracion", Plano = "plano",
            Pdf = "pdf", Tabla = "tabla", Markup = "markup", Revision = "revision", DecisionProfesional = "decision_profesional",
            Inferencia = "inferencia_agente", Modelo = "modelo", PlanEjecucion = "plan_ejecucion";

        public static readonly string[] All = { Usuario, Transcripcion, Demostracion, Plano, Pdf, Tabla, Markup, Revision, DecisionProfesional, Inferencia, Modelo, PlanEjecucion };

        /// <summary>0.9.0: kinds that are NEVER an independent source for QA: the model itself, the plan that built it, or an inference.</summary>
        public static bool NeverIndependent(string kind) => kind == Inferencia || kind == Modelo || kind == PlanEjecucion;

        /// <summary>Default authority (higher wins). An agent inference never beats a documented source.</summary>
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

    /// <summary>Confidence (what is known) and evidence (what was checked), using the vocabulary of the workflow report.</summary>
    public static class Confidence
    {
        public const string Observed = "OBSERVED", Inferred = "INFERRED", Unknown = "UNKNOWN";
        public const string Comprobado = "COMPROBADO", Reportado = "REPORTADO", Pendiente = "PENDIENTE", Bloqueado = "BLOQUEADO";
        public static bool IsConfidence(string s) => s == Observed || s == Inferred || s == Unknown;
        public static bool IsEvidence(string s) => s == Comprobado || s == Reportado || s == Pendiente || s == Bloqueado;
    }

    public sealed class SourceRef
    {
        public string Id;          // stable source identifier (e.g. "E-1.3.02@CCD7")
        public string Kind;        // SourceKinds
        public string Revision;    // revision or issue date; null = no known revision
        public string Locator;     // page, minute, cell…
        /// <summary>0.9.0: SHA-256 of the exact source content (file, extraction). Without it independence cannot be verified.</summary>
        public string Sha256;
        /// <summary>0.9.0: lineage: ids or "sha256:…" of the sources it was derived from (a table taken from a drawing, a plan taken from a markup…).</summary>
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

        /// <summary>Identities of this source for comparing lineages: its id and its content.</summary>
        public IEnumerable<string> Identities()
        {
            if (!string.IsNullOrWhiteSpace(Id)) yield return "id:" + Id.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(Sha256)) yield return "sha256:" + Sha256.Trim().ToLowerInvariant();
        }

        /// <summary>Identities of its declared lineage, normalized the same way as <see cref="Identities"/>.</summary>
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
        public string Scope;          // scope: "nivel=L03", "vista=…", "*"
        public string Key;            // what is regulated: "altura_montaje", "workset", "codigo"…
        public string Value;          // required value (text); null = the source does not set it (not invented)
        public int Authority = -1;    // -1 = that of its source kind
        public string Confidence = Workflow.Confidence.Observed;
        public string Acceptance;     // human-readable acceptance criterion
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
    /// Versioned set of requirements. <see cref="Resolve"/> returns the requirement in force per (scope, key) or a conflict.
    /// Rules, in order: (1) an explicitly replaced requirement stops applying; (2) the highest authority wins;
    /// (3) with equal authority and the same source, the later revision wins; (4) if values still differ → conflict (person).
    /// A requirement without a value (the source does not set it) is never filled in: it stays UNKNOWN and is reported as missing data.
    /// </summary>
    public sealed class RequirementSet
    {
        private readonly List<Requirement> _all = new List<Requirement>();
        public IReadOnlyList<Requirement> All => _all;

        public string Add(Requirement r)
        {
            if (r == null) return "null requirement";
            if (string.IsNullOrWhiteSpace(r.Id)) return "requirement without id";
            if (_all.Any(x => x.Id == r.Id)) return "duplicate id: " + r.Id;
            if (r.Source == null || string.IsNullOrWhiteSpace(r.Source.Id)) return r.Id + ": source is missing (not accepted without provenance)";
            if (!SourceKinds.All.Contains(r.Source.Kind)) return r.Id + ": unknown source kind " + r.Source.Kind;
            if (string.IsNullOrWhiteSpace(r.Key)) return r.Id + ": the regulated key is missing";
            if (!Confidence.IsConfidence(r.Confidence)) return r.Id + ": confidence must be OBSERVED, INFERRED or UNKNOWN";
            if (r.Source.Kind == SourceKinds.Inferencia && r.Confidence == Confidence.Observed) return r.Id + ": an agent inference cannot be declared OBSERVED";
            foreach (var s in r.Supersedes) if (!_all.Any(x => x.Id == s)) return r.Id + ": replaces " + s + ", which does not exist";
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
            // specific beats general (exact scope over "*") only when the authority is not lower
            var maxAuth = live.Max(r => r.EffectiveAuthority);
            var top = live.Where(r => r.EffectiveAuthority == maxAuth).ToList();
            if (top.Count > 1 && top.Any(r => r.Scope == scope) && top.Any(r => r.Scope == "*")) top = top.Where(r => r.Scope == scope).ToList();
            if (top.Count > 1)
            {
                // same source, different revision → the later one
                var bySource = top.GroupBy(r => r.Source.Id).ToList();
                if (bySource.Count == 1)
                {
                    var ordered = top.OrderBy(r => r.Source.Revision ?? "", StringComparer.Ordinal).ToList();
                    if (ordered.Select(r => r.Source.Revision ?? "").Distinct().Count() == ordered.Count) top = new List<Requirement> { ordered.Last() };
                }
            }
            if (top.Count > 1 && top.Select(r => r.Value ?? "\u0000").Distinct().Count() > 1)
                return new Resolution { Conflict = new RequirementConflict { Scope = scope, Key = key, Candidates = top, Reason = "different values with the same authority and no explicit replacement: a person decides" } };
            return new Resolution { Winner = top[0] };
        }

        /// <summary>All current conflicts and the data no source sets.</summary>
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
                if (d == null) { errors.Add("requirement is not an object"); continue; }
                var e = set.Add(Requirement.FromJson(d));
                if (e != null) errors.Add(e);
            }
            return set;
        }
    }
}
