// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// BIM Task IR, schema 2 (0.7.0-alpha1). Two layers:
    /// - <see cref="PlanDraft"/>/<see cref="OpDraft"/>: what arrives from an agent, a document or a test. Mutable and NOT trusted.
    /// - <see cref="ValidatedPlan"/>/<see cref="ValidatedOp"/>: the result of <see cref="IrValidator.Validate"/>. Immutable (read-only
    ///   collections, deep copy); includes what the system DERIVES (authorization, postconditions, idempotency) and never accepts from the agent.
    /// Execution state lives separately (Exec/ExecState); the validated plan does not change while it runs.
    /// </summary>
    public static class IrSchema
    {
        public const int Version = 2;
        public static readonly string[] CoordinateSystems = { "revit_internal", "project", "shared_items" };
        public static readonly string[] LengthUnits = { "mm", "m", "ft", "in" };
        /// <summary>Fields only the system may set. If they appear in the JSON, the plan is rejected.</summary>
        public static readonly string[] SystemOnlyOpFields = { "authorization", "status", "postconditions", "idempotency", "attempt", "result" };
        public static readonly string[] SystemOnlyPlanFields = { "authorization", "status", "hash" };
    }

    /// <summary>Technical source document declared by the plan (e.g. a specifications PDF), identified by its SHA-256.</summary>
    public sealed class SourceDoc
    {
        public string Id, Name, Sha256;
        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["id"] = Id, ["name"] = Name, ["sha256"] = Sha256 };
    }

    /// <summary>Traceability of an operation back to the requirement that produced it.</summary>
    public sealed class SourceRef
    {
        public string SourceId;      // id of a SourceDoc of the plan
        public long? Page;
        public string Section, Requirement, Quote;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["source"] = SourceId };
            if (Page != null) d["page"] = Page.Value;
            if (Section != null) d["section"] = Section;
            if (Requirement != null) d["requirement"] = Requirement;
            if (Quote != null) d["quote"] = Quote;
            return d;
        }

        public static SourceRef FromJson(Dictionary<string, object> o) => o == null ? null : new SourceRef
        {
            SourceId = Arg.GetString(o, "source"), Page = Arg.GetLong(o, "page"), Section = Arg.GetString(o, "section"),
            Requirement = Arg.GetString(o, "requirement"), Quote = Arg.GetString(o, "quote"),
        };
    }

    /// <summary>Operation as received (not trusted).</summary>
    public sealed class OpDraft
    {
        public string Id;
        public string Operation;
        public Dictionary<string, object> Scope = new Dictionary<string, object>(StringComparer.Ordinal);
        public Dictionary<string, object> Params = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<string> DependsOn = new List<string>();
        public List<Dictionary<string, object>> Preconditions = new List<Dictionary<string, object>>();
        public Dictionary<string, object> Expected = new Dictionary<string, object>(StringComparer.Ordinal);
        public long? Retries;
        public bool AllowEmpty;
        public SourceRef Source;
        /// <summary>System fields found while reading the JSON (reported as errors).</summary>
        public List<string> Forbidden = new List<string>();

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["operation"] = Operation, ["scope"] = Scope, ["parameters"] = Params,
                ["depends_on"] = DependsOn.Cast<object>().ToList(), ["preconditions"] = Preconditions.Cast<object>().ToList(),
                ["expected_result"] = Expected, ["allow_empty"] = AllowEmpty,
            };
            if (Retries != null) d["retries"] = Retries.Value;
            if (Source != null) d["source"] = Source.ToJson();
            return d;
        }

        public static OpDraft FromJson(Dictionary<string, object> o)
        {
            var op = new OpDraft { Id = Arg.GetString(o, "id"), Operation = Arg.GetString(o, "operation"), Retries = Arg.GetLong(o, "retries") };
            if (o.TryGetValue("scope", out var s) && s is Dictionary<string, object> sd) op.Scope = sd;
            if (o.TryGetValue("parameters", out var p) && p is Dictionary<string, object> pd) op.Params = pd;
            if (o.TryGetValue("expected_result", out var e) && e is Dictionary<string, object> ed) op.Expected = ed;
            if (o.TryGetValue("depends_on", out var dep) && dep is List<object> dl) op.DependsOn = dl.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
            if (o.TryGetValue("preconditions", out var pre) && pre is List<object> pl) op.Preconditions = pl.OfType<Dictionary<string, object>>().ToList();
            if (o.TryGetValue("source", out var f) && f is Dictionary<string, object> fd) op.Source = SourceRef.FromJson(fd);
            op.AllowEmpty = Arg.GetBool(o, "allow_empty") ?? false;
            foreach (var k in IrSchema.SystemOnlyOpFields) if (o.ContainsKey(k)) op.Forbidden.Add(k);
            return op;
        }

        /// <summary>Id the system expects for this content within <paramref name="plan"/>.</summary>
        public string ExpectedId(PlanDraft plan) =>
            IrIds.OpId(plan.DocKey, plan.DocPrint, Scope, Operation, Params, plan.SourceIdentity(Source));
    }

    /// <summary>Plan as received (not trusted).</summary>
    public sealed class PlanDraft
    {
        public int Ir = IrSchema.Version;
        public string Objective;
        public string DocKey, DocPrint;
        public string LengthUnit;
        public string Coordinates;
        public List<SourceDoc> Sources = new List<SourceDoc>();
        public List<OpDraft> Ops = new List<OpDraft>();
        public List<string> Forbidden = new List<string>();

        /// <summary>Source identity of an operation for its id: document sha256 + requirement (+ page). No source: "".</summary>
        public string SourceIdentity(SourceRef r)
        {
            if (r == null) return "";
            var doc = Sources.FirstOrDefault(s => s.Id == r.SourceId);
            return (doc?.Sha256 ?? ("?" + r.SourceId)) + "|" + (r.Requirement ?? "") + "|" + (r.Page?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        /// <summary>Assigns each operation its deterministic id. Dependencies are expressed by index as "#n" before calling.</summary>
        public PlanDraft AssignIds()
        {
            var ids = Ops.Select(o => o.ExpectedId(this)).ToList();
            for (var i = 0; i < Ops.Count; i++)
            {
                Ops[i].Id = ids[i];
                Ops[i].DependsOn = Ops[i].DependsOn.Select(d => d.StartsWith("#", StringComparison.Ordinal) ? ids[int.Parse(d.Substring(1), CultureInfo.InvariantCulture)] : d).ToList();
            }
            return this;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["ir"] = (long)Ir, ["target"] = Objective,
            ["document"] = new Dictionary<string, object> { ["key"] = DocKey, ["fingerprint"] = DocPrint },
            ["units"] = new Dictionary<string, object> { ["length"] = LengthUnit },
            ["coordinates"] = Coordinates,
            ["sources"] = Sources.Select(s => (object)s.ToJson()).ToList(),
            ["operations"] = Ops.Select(x => (object)x.ToJson()).ToList(),
        };

        public static PlanDraft Parse(string json) => FromJson(Json.ParseObject(json));

        public static PlanDraft FromJson(Dictionary<string, object> o)
        {
            var plan = new PlanDraft
            {
                Ir = (int)(Arg.GetLong(o, "ir") ?? 0), Objective = Arg.GetString(o, "target"), Coordinates = Arg.GetString(o, "coordinates"),
            };
            if (o.TryGetValue("document", out var d) && d is Dictionary<string, object> doc) { plan.DocKey = Arg.GetString(doc, "key"); plan.DocPrint = Arg.GetString(doc, "fingerprint"); }
            if (o.TryGetValue("units", out var u) && u is Dictionary<string, object> ud) plan.LengthUnit = Arg.GetString(ud, "length");
            if (o.TryGetValue("sources", out var fs) && fs is List<object> fl)
                foreach (var x in fl.OfType<Dictionary<string, object>>())
                    plan.Sources.Add(new SourceDoc { Id = Arg.GetString(x, "id"), Name = Arg.GetString(x, "name"), Sha256 = Arg.GetString(x, "sha256") });
            if (o.TryGetValue("operations", out var ops) && ops is List<object> list)
                foreach (var x in list.OfType<Dictionary<string, object>>()) plan.Ops.Add(OpDraft.FromJson(x));
            foreach (var k in IrSchema.SystemOnlyPlanFields) if (o.ContainsKey(k)) plan.Forbidden.Add(k);
            return plan;
        }
    }

    public static class IrIds
    {
        /// <summary>
        /// Deterministic id of an operation: "op-" + 20 hex of sha256 over (document: key + fingerprint, scope, operation, canonical
        /// parameters, source identity). Including the fingerprint prevents reusing an id against a replaced model; including the source
        /// prevents confusing two different requirements that ask for the same thing (those are detected separately as semantic duplicates).
        /// </summary>
        public static string OpId(string docKey, string docPrint, IDictionary<string, object> scope, string operation, IDictionary<string, object> parameters, string sourceIdentity) =>
            "op-" + Home.Sha256Hex(OpSemantics.Canonical(new Dictionary<string, object>
            {
                ["doc"] = docKey ?? "", ["fingerprint"] = docPrint ?? "", ["scope"] = scope ?? new Dictionary<string, object>(), ["operation"] = operation ?? "",
                ["parameters"] = parameters ?? new Dictionary<string, object>(), ["source"] = sourceIdentity ?? "",
            }), 20);

        /// <summary>Semantic key: the same without the source. Two operations in the same plan with the same key are a duplicate.</summary>
        public static string SemanticKey(IDictionary<string, object> scope, string operation, IDictionary<string, object> parameters) =>
            Home.Sha256Hex(OpSemantics.Canonical(new Dictionary<string, object> { ["scope"] = scope, ["operation"] = operation, ["parameters"] = parameters }), 20);

        /// <summary>Read-only deep copy (dictionaries and lists).</summary>
        public static object Freeze(object v)
        {
            switch (v)
            {
                case IDictionary<string, object> d:
                    return new ReadOnlyDictionary<string, object>(d.ToDictionary(kv => kv.Key, kv => Freeze(kv.Value), StringComparer.Ordinal));
                case string _: return v;
                case System.Collections.IEnumerable e:
                    return new ReadOnlyCollection<object>(e.Cast<object>().Select(Freeze).ToList());
                default: return v;
            }
        }

        public static IReadOnlyDictionary<string, object> FreezeDict(IDictionary<string, object> d) =>
            (IReadOnlyDictionary<string, object>)Freeze(d ?? new Dictionary<string, object>());

        /// <summary>Converts back to a mutable serializable form (for writing JSON).</summary>
        public static object Thaw(object v)
        {
            switch (v)
            {
                case IReadOnlyDictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => Thaw(kv.Value), StringComparer.Ordinal);
                case IDictionary<string, object> d2: return d2.ToDictionary(kv => kv.Key, kv => Thaw(kv.Value), StringComparer.Ordinal);
                case string _: return v;
                case System.Collections.IEnumerable e: return e.Cast<object>().Select(Thaw).ToList();
                default: return v;
            }
        }
    }

    /// <summary>Precondition from the closed catalog. Today: number of targets within [Min, Max].</summary>
    public sealed class Precondition
    {
        public string Kind;
        public long? Min, Max;
        public override string ToString() => Kind + "[" + (Min?.ToString(CultureInfo.InvariantCulture) ?? "") + "," + (Max?.ToString(CultureInfo.InvariantCulture) ?? "") + "]";
    }

    /// <summary>Validated, immutable operation. What the system derives is marked.</summary>
    public sealed class ValidatedOp
    {
        public string Id { get; }
        public string Operation { get; }
        public OpSpec Spec { get; }
        public IReadOnlyDictionary<string, object> Scope { get; }
        public IReadOnlyDictionary<string, object> Params { get; }
        public IReadOnlyList<string> DependsOn { get; }
        public IReadOnlyList<Precondition> Preconditions { get; }
        public IReadOnlyDictionary<string, object> Expected { get; }
        public bool AllowEmpty { get; }
        public int RetryMax { get; }
        public SourceRef Source { get; }
        // derived by the system:
        public bool Writes => Spec.Writes;
        public string Idempotency => Spec.Idempotency;
        public IReadOnlyList<string> Authorization => Spec.Authorization;
        public IReadOnlyList<string> Postconditions => Spec.Postconditions;

        internal ValidatedOp(OpDraft d, OpSpec spec, IReadOnlyList<Precondition> pre)
        {
            Id = d.Id; Operation = d.Operation; Spec = spec;
            Scope = IrIds.FreezeDict(d.Scope); Params = IrIds.FreezeDict(d.Params); Expected = IrIds.FreezeDict(d.Expected);
            DependsOn = new ReadOnlyCollection<string>(d.DependsOn.Distinct(StringComparer.Ordinal).ToList());
            Preconditions = pre; AllowEmpty = d.AllowEmpty; RetryMax = (int)(d.Retries ?? 0);
            Source = d.Source == null ? null : new SourceRef { SourceId = d.Source.SourceId, Page = d.Source.Page, Section = d.Source.Section, Requirement = d.Source.Requirement, Quote = d.Source.Quote };
        }

        public string Param(string k) => Params.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        public string ScopeValue(string k) => Scope.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Validated, immutable plan. <see cref="Hash"/> identifies its canonical content.</summary>
    public sealed class ValidatedPlan
    {
        public string Hash { get; }
        public string Objective { get; }
        public string DocKey { get; }
        public string DocPrint { get; }
        public string LengthUnit { get; }
        public string Coordinates { get; }
        public IReadOnlyList<SourceDoc> Sources { get; }
        public IReadOnlyList<ValidatedOp> Ops { get; }
        /// <summary>Deterministic topological order (ties broken by id).</summary>
        public IReadOnlyList<string> Order { get; }
        public IReadOnlyDictionary<string, ValidatedOp> ById { get; }
        /// <summary>Canonical JSON of the validated draft (what is saved in plan.json).</summary>
        public string CanonicalJson { get; }

        internal ValidatedPlan(PlanDraft d, List<ValidatedOp> ops, List<string> order)
        {
            var draftJson = d.ToJson();
            CanonicalJson = OpSemantics.Canonical(draftJson);
            Hash = Home.Sha256Hex(CanonicalJson, 20);
            Objective = d.Objective; DocKey = d.DocKey; DocPrint = d.DocPrint; LengthUnit = d.LengthUnit; Coordinates = d.Coordinates;
            Sources = new ReadOnlyCollection<SourceDoc>(d.Sources.Select(s => new SourceDoc { Id = s.Id, Name = s.Name, Sha256 = s.Sha256 }).ToList());
            Ops = new ReadOnlyCollection<ValidatedOp>(ops);
            Order = new ReadOnlyCollection<string>(order);
            ById = new ReadOnlyDictionary<string, ValidatedOp>(ops.ToDictionary(o => o.Id, StringComparer.Ordinal));
        }

        public static string HashOf(PlanDraft d) => Home.Sha256Hex(OpSemantics.Canonical(d.ToJson()), 20);
    }
}
