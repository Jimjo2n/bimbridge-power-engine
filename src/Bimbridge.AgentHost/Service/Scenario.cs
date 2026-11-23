// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Full simulated scenario (§9): N operations (1,000 by default) on a 10-level model with chained dependencies,
    /// deliberate failures and interruption points. Writes to &lt;dir&gt;: plan_input.json, model/ and scenario.json (what is expected of
    /// each special operation, so the test can check it without depending on this code).
    /// Structure: 2 queries · 10 levels × K parameters (chains of 5: each depends on the previous one; the first, on the query) ·
    /// 10 QC (one per level, depending on the end of 5 chains) · 8 writes on a nonexistent level (4 with permitir_vacio).
    /// Deliberate failures: backend rejections (block their chain), recoverable and unrecoverable transient errors, wrong expected
    /// results, a QC with deviations and disallowed empty sets.
    /// </summary>
    public static class Scenario
    {
        public const int Levels = 10, PerLevel = 20, Chain = 5;

        public static string Generate(string dir, int n = 1000)
        {
            Directory.CreateDirectory(dir);
            var k = Math.Max(Chain, (n - 2 - Levels - 8) / Levels);
            var plan = new PlanDraft { Objective = "Scenario 1000: per-level parameters with chains, QC and deliberate failures", DocKey = "archivo:c:\\escenario.rvt", DocPrint = "huella-escenario-0001", LengthUnit = "mm", Coordinates = "revit_internal" };
            plan.Sources.Add(new SourceDoc { Id = "doc-1", Name = "escenario_1000.txt", Sha256 = new string('e', 64) });
            OpDraft Q(string cat) { var o = new OpDraft { Operation = "query_check" }; o.Params["category"] = cat; return o; }
            plan.Ops.Add(Q("OST_LightingFixtures"));
            plan.Ops.Add(Q("OST_LightingDevices"));
            var byLevel = new Dictionary<int, List<int>>();
            var chainEnds = new Dictionary<int, List<int>>();
            for (var l = 1; l <= Levels; l++)
            {
                byLevel[l] = new List<int>(); chainEnds[l] = new List<int>();
                for (var p = 0; p < k; p++)
                {
                    var o = new OpDraft { Operation = "set_parameter", Source = new SourceRef { SourceId = "doc-1", Page = l, Requirement = "REQ-L" + l.ToString("00", CultureInfo.InvariantCulture) + "-" + p.ToString("000", CultureInfo.InvariantCulture) } };
                    o.Scope["category"] = "OST_LightingFixtures"; o.Scope["level"] = "L" + l.ToString("00", CultureInfo.InvariantCulture);
                    o.Params["parameter"] = "P" + p.ToString("000", CultureInfo.InvariantCulture); o.Params["value"] = (long)(l * 1000 + p); o.Params["units"] = "mm";
                    o.Expected["elements"] = (long)PerLevel;
                    o.DependsOn.Add(p % Chain == 0 ? "#0" : "#" + (plan.Ops.Count - 1).ToString(CultureInfo.InvariantCulture));
                    byLevel[l].Add(plan.Ops.Count);
                    if (p % Chain == Chain - 1) chainEnds[l].Add(plan.Ops.Count);
                    plan.Ops.Add(o);
                }
            }
            var qc = new List<int>();
            for (var l = 1; l <= Levels; l++)
            {
                var o = new OpDraft { Operation = "verify_qc" };
                o.Params["recipe"] = "qc_manifest"; o.Scope["level"] = "L" + l.ToString("00", CultureInfo.InvariantCulture);
                o.DependsOn.AddRange(chainEnds[l].Take(5).Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)));
                qc.Add(plan.Ops.Count);
                plan.Ops.Add(o);
            }
            var empty = new List<int>();
            for (var e = 0; e < 8; e++)
            {
                var o = new OpDraft { Operation = "set_parameter", AllowEmpty = e < 4 };
                o.Scope["category"] = "OST_LightingFixtures"; o.Scope["level"] = "L99";
                o.Params["parameter"] = "VACIO" + e; o.Params["value"] = (long)e; o.Params["units"] = "mm";
                empty.Add(plan.Ops.Count);
                plan.Ops.Add(o);
            }
            // deliberate failures (indices within specific chains)
            var rejected = new[] { byLevel[2][1], byLevel[5][7], byLevel[9][12] };                                // block the rest of their chain
            var transientOk = new[] { byLevel[1][3], byLevel[3][4], byLevel[4][10], byLevel[6][2], byLevel[8][9] }; // 1 rejection, retries=1 → ok
            var transientKo = new[] { byLevel[7][5], byLevel[10][15] };                                           // 3 rejections, retries=1 → fallida
            var wrongExpect = new[] { byLevel[1][20], byLevel[3][30], byLevel[6][40], byLevel[8][50], byLevel[10][60] };
            foreach (var i in transientOk.Concat(transientKo)) plan.Ops[i].Retries = 1;
            foreach (var i in wrongExpect) plan.Ops[i].Expected["elements"] = (long)(PerLevel + 1);
            plan.AssignIds();
            string Id(int i) => plan.Ops[i].Id;

            var errs = IrValidator.Validate(plan, out var vp);
            if (errs.Count > 0) throw new InvalidOperationException("invalid scenario: " + string.Join("; ", errs.Take(5)));
            File.WriteAllText(Path.Combine(dir, "plan_input.json"), Json.Write(plan.ToJson()));
            var qcBad = qc[3];
            var elements = Enumerable.Range(1, Levels).Select(l => "OST_LightingFixtures=" + PerLevel + "@L" + l.ToString("00", CultureInfo.InvariantCulture))
                                     .Concat(new[] { "OST_LightingDevices=40@L01" }).ToList();
            DurableSimModel.Create(Path.Combine(dir, "model"), plan.DocKey, plan.DocPrint, elements, new Dictionary<string, object>
            {
                ["reject"] = rejected.Select(i => (object)Id(i)).ToList(),
                ["transient"] = transientOk.ToDictionary(i => Id(i), i => (object)1L).Concat(transientKo.ToDictionary(i => Id(i), i => (object)3L)).ToDictionary(kv => kv.Key, kv => kv.Value),
                ["qc_deviations"] = new Dictionary<string, object> { [Id(qcBad)] = 2L },
            });
            var spec = new Dictionary<string, object>
            {
                ["plan"] = vp.Hash, ["operations"] = (long)plan.Ops.Count,
                ["rejected_items"] = rejected.Select(i => (object)Id(i)).ToList(),
                ["transient_ok"] = transientOk.Select(i => (object)Id(i)).ToList(),
                ["transient_ko"] = transientKo.Select(i => (object)Id(i)).ToList(),
                ["wrong_expected"] = wrongExpect.Select(i => (object)Id(i)).ToList(),
                ["qc_deviated"] = Id(qcBad),
                ["empty_allowed"] = empty.Take(4).Select(i => (object)Id(i)).ToList(),
                ["empty_not_allowed"] = empty.Skip(4).Select(i => (object)Id(i)).ToList(),
                ["by_level"] = byLevel.ToDictionary(kv => "L" + kv.Key.ToString("00", CultureInfo.InvariantCulture), kv => (object)kv.Value.Select(i => (object)Id(i)).ToList()),
                ["qc"] = qc.Select(i => (object)Id(i)).ToList(),
            };
            File.WriteAllText(Path.Combine(dir, "scenario.json"), Json.Write(spec));
            return "scenario " + vp.Hash + " · " + plan.Ops.Count + " operations · " + vp.Ops.Count(o => o.Writes) + " writes · " + vp.Ops.Sum(o => o.DependsOn.Count) + " dependencies";
        }
    }
}
