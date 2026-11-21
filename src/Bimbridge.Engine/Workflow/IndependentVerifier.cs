// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (CAP-007/CAP-008, report rule WF-11): independent verification
    /// <c>independent source → expected result → actual model → differences</c>.
    /// It REFUSES to accept a verification whose "expected" comes from the same inventory or plan that guided the build:
    /// that only proves internal consistency, and it was exactly what hid omissions in the riser audit.
    /// </summary>
    public static class IndependentVerifier
    {
        public sealed class Item
        {
            public string Key;                 // logical identifier (code + location, equipment id, sheet name…); null = match by position
            public long ElementId;             // only in the observed items
            public double? X, Y;               // internal feet, only for matching by position
            public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public sealed class Input
        {
            public SourceRef ExpectedSource;   // where the expected items come from
            public string BuildSourceId;       // source/plan used to BUILD (0.8.0 compatibility; equivalent to BuildSources with id only)
            /// <summary>0.9.0: ALL sources and plans that guided the build, with their SHA-256 and lineage.</summary>
            public List<SourceRef> BuildSources = new List<SourceRef>();
            /// <summary>0.9.0: fingerprint (<see cref="Fingerprint"/>) of the execution plan; if the expected items are identical, they are a copy of the plan.</summary>
            public string BuildPlanFingerprint;
            public string RequiredRevision;    // revision the expected source must have (null = not required)
            public List<Item> Expected = new List<Item>();
            public List<Item> Observed = new List<Item>();
            public double PositionTolerance = 0.5 / 12.0;   // 1/2" by default, in feet
            public string[] CompareAttrs = new string[0];
            /// <summary>Justified exceptions: key → reason with source. Not counted as a difference, but reported.</summary>
            public Dictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Keys that may legitimately repeat (e.g. a feeder ID valid on two runs).</summary>
            public HashSet<string> AllowedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        }

        public sealed class Result
        {
            public string Estado;              // ok | diferencias | no_independiente | independencia_no_comprobable | revision_incorrecta | invalido
            public string Motivo;
            public List<string> Missing = new List<string>();
            public List<long> Extra = new List<long>();
            public List<string> Duplicates = new List<string>();
            public List<string> Mismatches = new List<string>();
            public List<string> JustifiedExceptions = new List<string>();
            public int Matched;
            /// <summary>0.9.0: recorded independence proof (expected source, build sources, method).</summary>
            public Dictionary<string, object> Independence;

            public bool Ok => Estado == "ok";

            public Dictionary<string, object> ToJson(int sample = 20)
            {
                var d = new Dictionary<string, object> { ["estado"] = Estado, ["emparejados"] = (long)Matched };
                if (Motivo != null) d["motivo"] = Motivo;
                if (Independence != null) d["independencia"] = Independence;
                void L<T>(string k, List<T> l) { if (l.Count > 0) { d[k + "_n"] = (long)l.Count; d[k] = l.Take(sample).Select(x => (object)x).ToList(); } }
                L("omitidos", Missing); L("adicionales", Extra); L("duplicados", Duplicates); L("discrepancias", Mismatches); L("excepciones_justificadas", JustifiedExceptions);
                return d;
            }
        }

        /// <summary>
        /// 0.9.0 (P0): VERIFIABLE independence, not declared. Requires: (1) an expected source identified by content (SHA-256) and of a
        /// kind that can be independent; (2) at least one declared build source; (3) no id or content match
        /// with what built it; (4) lineages disjoint in both directions; (5) the expected items are not an exact copy of the plan.
        /// null = independent; otherwise (status, reason).
        /// </summary>
        public static (string Estado, string Motivo)? CheckIndependence(Input input, out Dictionary<string, object> proof)
        {
            proof = null;
            var exp = input.ExpectedSource;
            if (SourceKinds.NeverIndependent(exp.Kind))
                return ("no_independiente", "the expected items are of kind " + exp.Kind + " (model, execution plan or agent inference): never an independent source");
            if (string.IsNullOrWhiteSpace(exp.Sha256) || exp.Sha256.Trim().Length < 32)
                return ("independencia_no_comprobable", "the expected source " + exp.Id + " does not carry the SHA-256 of its content: it cannot be shown that it is not the same one that guided the build");
            var builds = new List<SourceRef>(input.BuildSources ?? new List<SourceRef>());
            if (!string.IsNullOrWhiteSpace(input.BuildSourceId) && !builds.Any(b => string.Equals(b.Id, input.BuildSourceId, StringComparison.OrdinalIgnoreCase)))
                builds.Add(new SourceRef { Id = input.BuildSourceId });
            if (builds.Count == 0)
                return ("independencia_no_comprobable", "the source or plan the build came from was not declared; without it independence cannot be verified");
            var expIds = new HashSet<string>(exp.Identities(), StringComparer.Ordinal);
            var expLineage = new HashSet<string>(exp.Lineage(), StringComparer.Ordinal);
            foreach (var b in builds)
            {
                var bIds = b.Identities().ToList();
                var same = bIds.FirstOrDefault(expIds.Contains);
                if (same != null)
                    return ("no_independiente", "the expected items and the build share " + (same.StartsWith("sha256:", StringComparison.Ordinal) ? "the same content (" + same.Substring(0, Math.Min(23, same.Length)) + "…)" : "the same source (" + b.Id + ")") + ": it would only prove internal consistency");
                var up = bIds.FirstOrDefault(expLineage.Contains);
                if (up != null) return ("no_independiente", "the expected source " + exp.Id + " was derived from " + up + ", which also guided the build");
                var down = b.Lineage().FirstOrDefault(expIds.Contains);
                if (down != null) return ("no_independiente", "the build (" + b.Id + ") was derived from the expected source itself " + exp.Id);
                var common = b.Lineage().FirstOrDefault(expLineage.Contains);
                if (common != null) return ("no_independiente", "the expected source and the build (" + b.Id + ") derive from the same origin (" + common + ")");
            }
            if (!string.IsNullOrEmpty(input.BuildPlanFingerprint) && input.Expected.Count > 0 &&
                string.Equals(Fingerprint(input.Expected), input.BuildPlanFingerprint, StringComparison.OrdinalIgnoreCase))
                return ("no_independiente", "the expected items are identical, element by element, to the execution plan: a copy of the plan, not an independent source");
            proof = new Dictionary<string, object>
            {
                ["metodo"] = "sha256+linaje", ["fuente"] = exp.Id, ["fuente_sha256"] = exp.Sha256,
                ["construccion"] = builds.Select(b => (object)(b.Id + (b.Sha256 != null ? "@" + b.Sha256.Substring(0, Math.Min(12, b.Sha256.Length)) : ""))).ToList(),
                ["copia_del_plan"] = input.BuildPlanFingerprint == null ? "no_comprobado" : "no",
            };
            return null;
        }

        /// <summary>Stable fingerprint of a set of elements (key, position to 1/256 ft and attributes), independent of order.</summary>
        public static string Fingerprint(IEnumerable<Item> items)
        {
            string R(double? v) => v.HasValue ? Math.Round(v.Value * 256).ToString(CultureInfo.InvariantCulture) : "-";
            var lines = items.Select(i => (i.Key ?? "") + "|" + R(i.X) + "|" + R(i.Y) + "|" +
                                          string.Join(";", i.Attrs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)))
                             .OrderBy(x => x, StringComparer.Ordinal);
            return Home.Sha256Hex(string.Join("\n", lines));
        }

        public static Result Verify(Input input)
        {
            if (input?.ExpectedSource == null || string.IsNullOrWhiteSpace(input.ExpectedSource.Id))
                return new Result { Estado = "invalido", Motivo = "the independent source of the expected items is missing" };
            var ind = CheckIndependence(input, out var proof);
            if (ind != null) return new Result { Estado = ind.Value.Estado, Motivo = ind.Value.Motivo };
            if (input.RequiredRevision != null && !string.Equals(input.RequiredRevision, input.ExpectedSource.Revision, StringComparison.Ordinal))
                return new Result { Estado = "revision_incorrecta", Motivo = "the expected source is revision " + (input.ExpectedSource.Revision ?? "(no revision)") + " and revision " + input.RequiredRevision + " is required" };

            var res = new Result { Independence = proof };
            var usedObserved = new HashSet<int>();

            // 1. duplicates in the observed items (same key more than once) except the allowed ones
            foreach (var g in input.Observed.Where(o => o.Key != null).GroupBy(o => o.Key, StringComparer.Ordinal))
            {
                var expectedCount = input.Expected.Count(e => e.Key == g.Key);
                if (g.Count() > Math.Max(1, expectedCount) && !input.AllowedDuplicates.Contains(g.Key))
                    res.Duplicates.Add(g.Key + " ×" + g.Count().ToString(CultureInfo.InvariantCulture) + " (" + string.Join(",", g.Select(o => o.ElementId.ToString(CultureInfo.InvariantCulture))) + ")");
            }

            // 2. match: by key; without a key, by position (the closest within tolerance, without reuse)
            for (var i = 0; i < input.Expected.Count; i++)
            {
                var e = input.Expected[i];
                var label = e.Key ?? ("(" + Fmt(e.X) + "," + Fmt(e.Y) + ")");
                var idx = -1;
                if (e.Key != null)
                {
                    for (var j = 0; j < input.Observed.Count; j++)
                        if (!usedObserved.Contains(j) && input.Observed[j].Key == e.Key) { idx = j; break; }
                }
                else if (e.X.HasValue && e.Y.HasValue)
                {
                    var best = double.MaxValue;
                    for (var j = 0; j < input.Observed.Count; j++)
                    {
                        var o = input.Observed[j];
                        if (usedObserved.Contains(j) || !o.X.HasValue || !o.Y.HasValue) continue;
                        var dx = o.X.Value - e.X.Value; var dy = o.Y.Value - e.Y.Value;
                        var dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist <= input.PositionTolerance && dist < best) { best = dist; idx = j; }
                    }
                }
                if (idx < 0)
                {
                    if (e.Key != null && input.Exceptions.TryGetValue(e.Key, out var why)) res.JustifiedExceptions.Add(e.Key + ": " + why);
                    else res.Missing.Add(label);
                    continue;
                }
                usedObserved.Add(idx);
                res.Matched++;
                var obs = input.Observed[idx];
                foreach (var a in input.CompareAttrs)
                {
                    e.Attrs.TryGetValue(a, out var ev); obs.Attrs.TryGetValue(a, out var ov);
                    if (ev == null) continue;   // the source does not set that value: not required (and not invented)
                    if (!string.Equals(ev, ov, StringComparison.Ordinal)) res.Mismatches.Add(label + " #" + obs.ElementId.ToString(CultureInfo.InvariantCulture) + " " + a + ": expected «" + ev + "», model «" + (ov ?? "(empty)") + "»");
                }
            }

            // 3. extras: observed items without a match (allowed duplicates and exceptions do not count)
            for (var j = 0; j < input.Observed.Count; j++)
            {
                if (usedObserved.Contains(j)) continue;
                var o = input.Observed[j];
                if (o.Key != null && input.AllowedDuplicates.Contains(o.Key)) continue;
                if (o.Key != null && input.Exceptions.ContainsKey(o.Key)) { res.JustifiedExceptions.Add(o.Key + ": " + input.Exceptions[o.Key]); continue; }
                res.Extra.Add(o.ElementId);
            }

            res.Estado = res.Missing.Count + res.Extra.Count + res.Duplicates.Count + res.Mismatches.Count == 0 ? "ok" : "diferencias";
            return res;
        }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";
    }
}
