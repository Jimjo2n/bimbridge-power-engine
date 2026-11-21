// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Tasks
{
    /// <summary>
    /// Compact, deterministic summaries (§6): what the next agent (Codex or Claude) needs to continue without resending code or
    /// references. Calls no model and includes no credentials; computed from the engine's data.
    /// </summary>
    public static class TaskSummary
    {
        public static string Continuation(TaskRecord t, UnitLog units)
        {
            var c = units.Counts();
            long N(string s) => c.TryGetValue(s, out var n) ? n : 0;
            var done = N(UnitState.Committed) + N(UnitState.Skipped);
            var parts = new List<string>
            {
                "Task " + t.Id + " (" + (t.RecipeName ?? "?") + " v" + (t.RecipeVersion ?? "?") + ", doc " + (t.MainDoc?.Title ?? t.MainDoc?.Key ?? "?") + "): " + t.State + ".",
            };
            if (!string.IsNullOrEmpty(t.Objective)) parts.Add("Target: " + Clip(t.Objective, 140) + ".");
            parts.Add("Units: " + done + "/" + t.UnitsTotal + " done" +
                      Opt(N(UnitState.Conflict), " conflicts") + Opt(N(UnitState.RolledBack), " rolled back") +
                      Opt(N(UnitState.InProgress) + N(UnitState.Partial) + N(UnitState.Unknown), " doubtful") + ".");
            if (t.LastPersist != null)
                parts.Add("Last save: " + t.LastPersist.Kind + " " + t.LastPersist.When + " after " + t.LastPersist.Seq + " committed" +
                          (t.ConfirmSeq > t.LastPersist.Seq ? " (" + (t.ConfirmSeq - t.LastPersist.Seq) + " committed unsaved)" : "") + ".");
            else if (t.ConfirmSeq > 0) parts.Add("Not saved yet (" + t.ConfirmSeq + " committed only in Revit memory).");
            if (t.Decisions.Count > 0) parts.Add("Decisions: " + string.Join("; ", t.Decisions.Skip(Math.Max(0, t.Decisions.Count - 3)).Select(d => Clip(Convert.ToString(d.TryGetValue("texto", out var x) ? x : "", CultureInfo.InvariantCulture), 80))) + ".");
            if (t.Exceptions.Count > 0) parts.Add("Exceptions: " + t.Exceptions.Count + ".");
            if (!string.IsNullOrEmpty(t.Reason)) parts.Add("Reason: " + Clip(t.Reason, 200) + ".");
            parts.Add("Next: " + Next(t, units) + ".");
            return string.Join(" ", parts);
        }

        public static string Next(TaskRecord t, UnitLog units)
        {
            switch (t.State)
            {
                case TaskState.Applied: return "nothing; review artifacts if needed";
                case TaskState.AppliedWithDifferences:
                {
                    // Points to the state that actually has units pending review (it is not always conflict).
                    var st = new[] { UnitState.RolledBack, UnitState.Conflict, UnitState.Partial, UnitState.Skipped }
                        .FirstOrDefault(s => units.Units.Values.Any(u => u.State == s));
                    var which = units.Units.Values.Any(u => u.State == st) ? "tarea unidades estado=" + st.ToString().ToLowerInvariant() + ", " : "";
                    return "review deviations (" + which + "qc_final) and decide; tarea reanudar retries the rolled-back ones";
                }
                case TaskState.Cancelled: return "nothing unless you want to resume (tarea reanudar)";
                case TaskState.Interrupted: return units.Units.Values.Any(u => u.State == UnitState.Unknown)
                    ? "tarea reconciliar with decision=aplicada|no_aplicada for the doubtful ones; then tarea reanudar"
                    : "tarea reanudar (verifies what is doubtful and continues)";
                case TaskState.Partial: return "read the reason; fix and tarea reanudar";
                case TaskState.WaitingContext: return "resolve what the reason says; the task continues on its own (or tarea reanudar if it stopped waiting)";
                default: return "wait (tarea esperar desde=" + t.Rev + ")";
            }
        }

        /// <summary>Compact status for the agent response: counts, progress, deviations (max. 5), next action and artifacts.</summary>
        public static Dictionary<string, object> Status(TaskRecord t, UnitLog units, bool includeContinuation)
        {
            var c = units.Counts();
            long N(string s) => c.TryGetValue(s, out var n) ? n : 0;
            var d = new Dictionary<string, object>
            {
                ["tarea"] = t.Id,
                ["estado"] = t.State,
                ["rev"] = t.Rev,
                ["progreso"] = (N(UnitState.Committed) + N(UnitState.Skipped)) + "/" + t.UnitsTotal,
            };
            var counts = new Dictionary<string, object>();
            foreach (var kv in c.OrderBy(k => k.Key, StringComparer.Ordinal)) counts[kv.Key] = kv.Value;
            d["unidades"] = counts;
            if (!string.IsNullOrEmpty(t.Reason)) d["motivo"] = t.Reason;
            var dev = units.Units.Values.Where(u => u.State == UnitState.Conflict || u.State == UnitState.RolledBack || UnitState.NeedsReconcile(u.State))
                .Take(5).Select(u => (object)(u.Key + ": " + u.State + (string.IsNullOrEmpty(u.Error) ? "" : " — " + Clip(u.Error, 120)))).ToList();
            if (dev.Count > 0) d["desviaciones"] = dev;
            if (t.Postcondition != null) d["qc_final"] = Compact(t.Postcondition);
            if (t.Artifacts.Count > 0)
                d["artefactos"] = t.Artifacts.Skip(Math.Max(0, t.Artifacts.Count - 5)).Select(a => (object)new Dictionary<string, object>
                {
                    ["ruta"] = a.TryGetValue("ruta", out var r) ? r : null, ["sha256"] = a.TryGetValue("sha256", out var h) ? h : null,
                }).ToList();
            if (t.LastPersist != null) d["ultimo_guardado"] = t.LastPersist.Kind + " " + t.LastPersist.When + " (seq " + t.LastPersist.Seq + "/" + t.ConfirmSeq + ")";
            d["siguiente"] = Next(t, units);
            if (includeContinuation) d["resumen"] = t.Summary ?? Continuation(t, units);
            return d;
        }

        private static object Compact(Dictionary<string, object> pc)
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in pc)
            {
                if (kv.Value is List<object> l && l.Count > 5)
                {
                    o[kv.Key] = l.Take(5).ToList();
                    o[kv.Key + "_total"] = (long)l.Count;
                }
                else o[kv.Key] = kv.Value;
            }
            return o;
        }

        private static string Opt(long n, string label) => n > 0 ? ", " + n + label : "";

        private static string Clip(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
