// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine;

namespace Bimbridge.AgentHost.Bench
{
    /// <summary>
    /// 0.8.0: performance and token measurement. Every figure carries its provenance:
    /// MEDIDO_SIMULADO (fake add-in over the real pipe, no Revit), MEDIDO_NATIVO (real Revit, with evidence) or NO_MEDIDO.
    /// Comparisons against pyRevit are prepared but stay NO_MEDIDO until there is an authorized native run.
    /// </summary>
    public static class Benchmarks
    {
        public const string Simulated = "MEDIDO_SIMULADO", Native = "MEDIDO_NATIVO", NotMeasured = "NO_MEDIDO";

        /// <summary>Summary of the wire calls of a <see cref="Revit.RevitLink"/> (Calls).</summary>
        public static Dictionary<string, object> Wire(IEnumerable<Dictionary<string, object>> calls)
        {
            var list = calls.ToList();
            long L(Dictionary<string, object> c, string k) => c.TryGetValue(k, out var v) ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
            var req = list.Sum(c => L(c, "bytes_peticion"));
            var resp = list.Sum(c => L(c, "bytes_respuesta"));
            return new Dictionary<string, object>
            {
                ["llamadas_cable"] = (long)list.Count,
                ["por_operacion"] = list.GroupBy(c => Convert.ToString(c["op"], CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["bytes_peticion"] = req, ["bytes_respuesta"] = resp,
                ["ms_total"] = list.Sum(c => L(c, "ms")),
                ["tokens_estimados_cable"] = TokenEstimate.FromChars(req + resp),
            };
        }

        public sealed class Def
        {
            public string Id, Name, Scenario, Metrics, Harness, State = NotMeasured, Blocker;
            /// <summary>0.9.0: native campaign items that cover it (checked against the procedure by a test).</summary>
            public string[] Items = new string[0];
            public Dictionary<string, object> ToJson() => new Dictionary<string, object>
            {
                ["id"] = Id, ["nombre"] = Name, ["escenario"] = Scenario, ["metricas"] = Metrics, ["arnes"] = Harness, ["estado"] = State, ["bloqueo"] = Blocker, ["items"] = Items.Cast<object>().ToList(),
            };
        }

        private const string Blocked = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";

        /// <summary>B1–B8 from the real workflows report (§12). None measured natively yet.</summary>
        public static readonly IReadOnlyList<Def> Catalog = new List<Def>
        {
            new Def { Id = "B0", Name = "586 fixtures: one by one vs. plan", Scenario = "586 synthetic elements; read and change one parameter on each",
                Metrics = "agent calls, wire calls, bytes, ESTIMATED tokens, time; commits",
                Harness = "tests: Bench_B0_586_OneByOne_vs_Plan (fake add-in over the real pipe). JSON output with BIMBRIDGE_BENCH_OUT=<folder>",
                State = Simulated, Blocker = "native: " + Blocked },
            new Def { Id = "B1", Name = "Template and visibility", Scenario = "several views with an inherited rule that hides a system and an overly broad filter",
                Metrics = "time, calls, per-view match, zero unrelated categories, original template preserved",
                Harness = "campaign items N8-02 (views and templates) and N8-11 (visibility) on a disposable copy; pyRevit counterpart: equivalent read+change script", Items = new[] { "N8-02", "N8-11" }, Blocker = Blocked },
            new Def { Id = "B2", Name = "Lighting and support", Scenario = "hundreds of lights, similar symbols, near duplicates, ramps, offset ≠ physical height",
                Metrics = "precision/recall of matching and mounting, false positives/negatives against test ground truth",
                Harness = "verificar_independiente + geo montaje on a copy; seeded test ground truth (omissions and extras); items N8-07 and N8-10", Items = new[] { "N8-07", "N8-10" }, Blocker = Blocked },
            new Def { Id = "B3", Name = "Groups and annotation", Scenario = "11 unit sheets, variants, more than one panel, non-uniform level jump",
                Metrics = "type/floor plan coverage, correct members, zero invalid references, legibility at scale",
                Harness = "PARTIALLY PREPARED: group reading exists; the per type/floor plan coverage recipe is 0.9.0", Blocker = Blocked },
            new Def { Id = "B4", Name = "Two-system feeds", Scenario = "26 floor plans with stubs, crossing routes, links and alerts near the tolerance",
                Metrics = "time, memory, calls; continuity and separation; zero known incompatible interferences",
                Harness = "NOT PREPARED: physical routing is 0.9.0 (CAP-011)", Blocker = Blocked },
            new Def { Id = "B5", Name = "Riser 2D", Scenario = "graph with an omitted branch, a crossed label, a valid repeated ID, a backed open end",
                Metrics = "node/edge accuracy, error rates, legibility, one page",
                Harness = "PARTIALLY PREPARED: 2D drawing (detail_*) and independent verification exist; the riser graph is 0.9.0 (CAP-012)", Blocker = Blocked },
            new Def { Id = "B6", Name = "Publishing", Scenario = "ordered set of 26 sheets and a riser, with an obsolete PDF present",
                Metrics = "persistence after reopening, order, paper, color, seal; detect the previous PDF and verify the new one",
                Harness = "DeliveryTrack (12 stages) + campaign N8-05 (sheets and sheet set) and N8-12 (PDF and receipt); export only with export permission", Items = new[] { "N8-05", "N8-12" }, Blocker = Blocked },
            new Def { Id = "B7", Name = "Recovery", Scenario = "Revit busy, second agent, PID change, crash after commit, truncated response",
                Metrics = "zero concurrent execution, zero duplication, zero writes to the wrong model; repeated phases",
                Harness = "NRT-03/05/06/07 (simulated in tests; NRT-03/05/06 native in rc1 14ec21d; NRT-07 without a native run); items N8-09 and N8-13", Items = new[] { "N8-09", "N8-13" }, Blocker = Blocked },
            new Def { Id = "B8", Name = "Family compatibility", Scenario = "installation/manifest, open family, connectors, reload in 2022–2026",
                Metrics = "result per version×operation combination, verifiable rollback and reload",
                Harness = "fam info/caras/conector/cargar_archivo + campaign N8-06 (external families) and N8-13c (ZIP installation) per version", Items = new[] { "N8-06", "N8-13" }, Blocker = Blocked },
        };
    }
}
