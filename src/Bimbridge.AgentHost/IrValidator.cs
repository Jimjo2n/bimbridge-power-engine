// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>Definition of an operation in the closed catalog. Everything that is not a parameter is set by the system, not the plan.</summary>
    public sealed class OpSpec
    {
        public string Name;
        public bool Writes;
        public Dictionary<string, string> Required = new Dictionary<string, string>(StringComparer.Ordinal);   // name → "text" | "number" | "value" | "list"
        public Dictionary<string, string> Optional = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        /// <summary>Allowed scope keys.</summary>
        public string[] ScopeKeys = new string[0];
        /// <summary>Result keys the plan may declare as expected (and that the backend returns).</summary>
        public string[] ExpectedKeys = new string[0];
        /// <summary>"read" (repeating is harmless) | "convergent" (applying twice leaves the same state) | "not_idempotent".</summary>
        public string Idempotency = "read";
        public IReadOnlyList<string> Authorization = new ReadOnlyCollection<string>(new string[0]);
        /// <summary>Postconditions the system checks against the model state (not declared by the agent).</summary>
        public IReadOnlyList<string> Postconditions = new ReadOnlyCollection<string>(new string[0]);
    }

    /// <summary>
    /// IR schema 2 validator. Rejects: unknown version (no "best effort"), fields only the system sets, operations outside the
    /// catalog, parameters with the wrong type or value, implicit units or coordinates, ids that do not match the content, repeated
    /// ids, semantic duplicates, unknown dependencies, cycles, undeclared sources and preconditions outside the catalog.
    /// Only when there are no errors does it return an immutable <see cref="ValidatedPlan"/>.
    /// </summary>
    public static class IrValidator
    {
        public const int MaxRetries = 3;
        /// <summary>Maximum entries in a batch (same as the add-in page).</summary>
        public const int MaxBatch = 200;

        public static readonly Dictionary<string, OpSpec> Catalog = new[]
        {
            new OpSpec { Name = "query_check", Required = { ["category"] = "text" }, Optional = { ["filter"] = "text" },
                         ExpectedKeys = new[] { "elements" } },
            new OpSpec { Name = "set_parameter", Writes = true, Idempotency = "convergent",
                         Required = { ["parameter"] = "text", ["value"] = "value" }, Optional = { ["units"] = "text", ["tolerance"] = "number" },
                         Allowed = { ["units"] = IrSchema.LengthUnits.Concat(new[] { "text", "dimensionless" }).ToArray() },
                         ScopeKeys = new[] { "category", "level", "ids" }, ExpectedKeys = new[] { "elements", "changed" },
                         Authorization = new ReadOnlyCollection<string>(new[] { "model_write" }),
                         Postconditions = new ReadOnlyCollection<string>(new[] { "all targets have parameter = value" }) },
            // rc1: batch write. One text value per id; a single plan/commit in the add-in. alcance.ids = keys of valores.
            new OpSpec { Name = "set_parameter_batch", Writes = true, Idempotency = "convergent",
                         Required = { ["parameter"] = "text", ["values"] = "text_map" },
                         ScopeKeys = new[] { "category", "ids" }, ExpectedKeys = new[] { "elements", "changed" },
                         Authorization = new ReadOnlyCollection<string>(new[] { "model_write" }),
                         Postconditions = new ReadOnlyCollection<string>(new[] { "each target has parameter = its batch value" }) },
            new OpSpec { Name = "verify_qc", Required = { ["recipe"] = "text" }, Allowed = { ["recipe"] = new[] { "qc_manifest", "mount_supports", "qc_fixture_tags" } },
                         ScopeKeys = new[] { "category", "level" }, ExpectedKeys = new[] { "deviations" },
                         Postconditions = new ReadOnlyCollection<string>(new[] { "deviations = 0" }) },
            // 0.8.0 (CAP-008): cross-check against an independent source. esperado: key → expected attribute value (if parametro is given).
            new OpSpec { Name = "verify_independent",
                         Required = { ["source_id"] = "text", ["source_type_kind"] = "text", ["source_sha256"] = "text", ["source_construction"] = "text", ["key_parameter"] = "text", ["expected"] = "text_map" },
                         // 0.9.0 (P0): verifiable independence: content (sha256) and lineage of both sides; plan fingerprint to detect copies.
                         Optional = { ["source_revision"] = "text", ["required_revision"] = "text", ["parameter"] = "text", ["exceptions"] = "text_map",
                                      ["source_derived_from"] = "text", ["source_construction_sha256"] = "text", ["construction_derived_from"] = "text", ["plan_fingerprint"] = "text" },
                         Allowed = { ["source_type_kind"] = Bimbridge.Engine.Workflow.SourceKinds.All.Where(k => !Bimbridge.Engine.Workflow.SourceKinds.NeverIndependent(k)).ToArray() },
                         ScopeKeys = new[] { "category", "level" }, ExpectedKeys = new[] { "deviations" },
                         Postconditions = new ReadOnlyCollection<string>(new[] { "missing + extra + duplicates + mismatches = 0 against the independent source" }) },
        }.ToDictionary(s => s.Name, StringComparer.Ordinal);

        public static List<string> Validate(PlanDraft plan) => Validate(plan, out _);

        public static List<string> Validate(PlanDraft plan, out ValidatedPlan validated)
        {
            validated = null;
            var errors = new List<string>();
            if (plan.Ir != IrSchema.Version) errors.Add("unsupported_version: ir=" + plan.Ir + " (supported: " + IrSchema.Version + ")");
            if (string.IsNullOrWhiteSpace(plan.Objective)) errors.Add("missing target");
            if (string.IsNullOrEmpty(plan.DocKey)) errors.Add("missing document.key");
            if (string.IsNullOrEmpty(plan.DocPrint)) errors.Add("missing document.fingerprint (strong model identity)");
            if (plan.LengthUnit == null || !IrSchema.LengthUnits.Contains(plan.LengthUnit)) errors.Add("explicit units.length required (" + string.Join("|", IrSchema.LengthUnits) + ")");
            if (plan.Coordinates == null || !IrSchema.CoordinateSystems.Contains(plan.Coordinates)) errors.Add("explicit coordinates required (" + string.Join("|", IrSchema.CoordinateSystems) + ")");
            if (plan.Ops.Count == 0) errors.Add("the plan has no operations");
            foreach (var f in plan.Forbidden) errors.Add("non_declarable_field: plan." + f + " (computed by the system)");
            var srcIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in plan.Sources)
            {
                if (string.IsNullOrEmpty(s.Id) || !srcIds.Add(s.Id)) errors.Add("source without id or repeated: " + s.Id);
                if (s.Sha256 == null || s.Sha256.Length != 64 || !s.Sha256.All(Uri.IsHexDigit)) errors.Add("source " + s.Id + ": invalid sha256");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var semantic = new Dictionary<string, string>(StringComparer.Ordinal);
            var pres = new Dictionary<OpDraft, List<Precondition>>();
            foreach (var op in plan.Ops)
            {
                var who = op.Id ?? "(no id)";
                foreach (var f in op.Forbidden) errors.Add(who + ": non_declarable_field " + f + " (computed by the system)");
                OpSpec spec = null;
                if (op.Operation == null || !Catalog.TryGetValue(op.Operation, out spec)) errors.Add(who + ": operacion_fuera_de_catalogo '" + op.Operation + "'");
                if (spec != null)
                {
                    CheckParams(who, op, spec, errors);
                    foreach (var k in op.Scope.Keys) if (!spec.ScopeKeys.Contains(k)) errors.Add(who + ": scope." + k + " not allowed");
                    if (op.Scope.TryGetValue("ids", out var sids))
                    {
                        // alpha3: explicit ids narrow the scope (never widen it): list of positive integers, 1..500, no repeats
                        var l = sids as List<object>;
                        if (l == null || l.Count == 0 || l.Count > 500 || l.Any(x => !(x is long n) || n <= 0) || l.Distinct().Count() != l.Count)
                            errors.Add(who + ": scope.ids must be a list of 1 to 500 positive integer ids without repeats");
                    }
                    foreach (var k in op.Expected.Keys) if (!spec.ExpectedKeys.Contains(k)) errors.Add(who + ": expected_result." + k + " not allowed");
                    if (op.AllowEmpty && !spec.Writes) errors.Add(who + ": allow_empty only applies to writes");
                }
                if (op.Retries != null && (op.Retries < 0 || op.Retries > MaxRetries)) errors.Add(who + ": retries outside 0.." + MaxRetries);
                if (op.Source != null)
                {
                    if (string.IsNullOrEmpty(op.Source.SourceId) || !srcIds.Contains(op.Source.SourceId)) errors.Add(who + ": source '" + op.Source.SourceId + "' not declared in the plan");
                    if (op.Source.Page != null && op.Source.Page < 1) errors.Add(who + ": invalid page");
                }
                pres[op] = ParsePre(who, op, errors);
                var expected = op.ExpectedId(plan);
                if (op.Id == null) errors.Add("operation without id (expected " + expected + ")");
                else if (op.Id != expected) errors.Add(who + ": nondeterministic_id (expected " + expected + ")");
                if (op.Id != null && !ids.Add(op.Id)) errors.Add(who + ": repeated id");
                var sk = IrIds.SemanticKey(op.Scope, op.Operation, op.Params);
                if (semantic.TryGetValue(sk, out var first)) errors.Add(who + ": semantic_duplicate of " + first + " (same scope, operation and parameters)");
                else semantic[sk] = who;
            }
            foreach (var op in plan.Ops)
                foreach (var d in op.DependsOn)
                    if (d == op.Id) errors.Add(op.Id + ": depends on itself");
                    else if (!ids.Contains(d)) errors.Add((op.Id ?? "?") + ": unknown dependency " + d);
            var cycle = PlanGraph.FindCycle(plan.Ops);
            if (cycle != null) errors.Add("cycle: " + string.Join(" → ", cycle));
            if (errors.Count > 0) return errors;

            var vops = plan.Ops.Select(o => new ValidatedOp(o, Catalog[o.Operation], new ReadOnlyCollection<Precondition>(pres[o]))).ToList();
            var order = PlanGraph.Order(vops).ToList();
            validated = new ValidatedPlan(plan, vops, order);
            return errors;
        }

        private static List<Precondition> ParsePre(string who, OpDraft op, List<string> errors)
        {
            var res = new List<Precondition>();
            foreach (var p in op.Preconditions)
            {
                var kind = Arg.GetString(p, "type");
                if (kind != "targets") { errors.Add(who + ": precondition outside the catalog '" + kind + "'"); continue; }
                foreach (var k in p.Keys) if (k != "type" && k != "min" && k != "max") errors.Add(who + ": precondition targets: unknown field " + k);
                var pc = new Precondition { Kind = kind, Min = Arg.GetLong(p, "min"), Max = Arg.GetLong(p, "max") };
                if (pc.Min == null && pc.Max == null) errors.Add(who + ": precondition targets without min or max");
                if (pc.Min != null && pc.Max != null && pc.Min > pc.Max) errors.Add(who + ": precondition targets with min > max");
                res.Add(pc);
            }
            return res;
        }

        private static void CheckParams(string who, OpDraft op, OpSpec spec, List<string> errors)
        {
            foreach (var r in spec.Required)
                if (!op.Params.TryGetValue(r.Key, out var v) || v == null) errors.Add(who + ": missing parameters." + r.Key);
                else if (!TypeOk(v, r.Value)) errors.Add(who + ": parameters." + r.Key + " must be " + r.Value);
            foreach (var k in op.Params.Keys)
            {
                if (!spec.Required.ContainsKey(k) && !spec.Optional.ContainsKey(k)) { errors.Add(who + ": unknown parameter " + k); continue; }
                if (spec.Optional.TryGetValue(k, out var t) && op.Params[k] != null && !TypeOk(op.Params[k], t)) errors.Add(who + ": parameters." + k + " must be " + t);
                if (spec.Allowed.TryGetValue(k, out var allowed) && !allowed.Contains(Convert.ToString(op.Params[k], CultureInfo.InvariantCulture)))
                    errors.Add(who + ": parameters." + k + " outside " + string.Join("|", allowed));
            }
            if (spec.Name == "set_parameter_batch" && op.Params.TryGetValue("values", out var mv) && mv is IDictionary<string, object> map)
            {
                if (map.Count < 1 || map.Count > MaxBatch) errors.Add(who + ": parameters.values must have between 1 and " + MaxBatch + " entries");
                if (map.Keys.Any(k => !long.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                    errors.Add(who + ": parameters.values: keys must be numeric ids");
                var sids = op.Scope.TryGetValue("ids", out var sv) && sv is List<object> sl ? sl.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToList() : null;
                if (sids == null) errors.Add(who + ": set_parameter_batch requires scope.ids");
                else if (sids.Count != map.Count || sids.Except(map.Keys).Any()) errors.Add(who + ": scope.ids must match the keys of parameters.values exactly");
            }
            // explicit units: a numeric value without units is ambiguous
            if (spec.Name == "set_parameter" && op.Params.TryGetValue("value", out var val) && (val is long || val is double || val is int) && !op.Params.ContainsKey("units"))
                errors.Add(who + ": implicit_units (numeric value without parameters.units)");
        }

        private static bool TypeOk(object v, string type)
        {
            switch (type)
            {
                case "text": return v is string s && s.Length > 0;
                case "number": return v is long || v is double || v is int;
                case "value": return v is string || v is long || v is double || v is int || v is bool;
                case "list": return v is List<object>;
                case "text_map": return v is IDictionary<string, object> m && m.Values.All(x => x is string t && t.Length > 0);
                default: return false;
            }
        }
    }
}
