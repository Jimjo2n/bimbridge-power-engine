// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>
    /// Resúmenes compactos y deterministas (§6): lo que necesita el siguiente agente (Codex o Claude) para continuar sin reenviar código ni
    /// referencias. No llama a ningún modelo ni incluye credenciales; se calcula con los datos del motor.
    /// </summary>
    public static class TaskSummary
    {
        public static string Continuation(TaskRecord t, UnitLog units)
        {
            var c = units.Counts();
            long N(string s) => c.TryGetValue(s, out var n) ? n : 0;
            var done = N(UnitState.Confirmada) + N(UnitState.Omitida);
            var parts = new List<string>
            {
                "Tarea " + t.Id + " (" + (t.RecipeName ?? "?") + " v" + (t.RecipeVersion ?? "?") + ", doc " + (t.MainDoc?.Title ?? t.MainDoc?.Key ?? "?") + "): " + t.State + ".",
            };
            if (!string.IsNullOrEmpty(t.Objective)) parts.Add("Objetivo: " + Clip(t.Objective, 140) + ".");
            parts.Add("Unidades: " + done + "/" + t.UnitsTotal + " hechas" +
                      Opt(N(UnitState.Conflicto), " conflictos") + Opt(N(UnitState.Revertida), " revertidas") +
                      Opt(N(UnitState.EnCurso) + N(UnitState.Parcial) + N(UnitState.Desconocida), " dudosas") + ".");
            if (t.LastPersist != null)
                parts.Add("Último guardado: " + t.LastPersist.Kind + " " + t.LastPersist.When + " tras " + t.LastPersist.Seq + " confirmadas" +
                          (t.ConfirmSeq > t.LastPersist.Seq ? " (" + (t.ConfirmSeq - t.LastPersist.Seq) + " confirmadas sin guardar)" : "") + ".");
            else if (t.ConfirmSeq > 0) parts.Add("Sin guardar todavía (" + t.ConfirmSeq + " confirmadas sólo en memoria de Revit).");
            if (t.Decisions.Count > 0) parts.Add("Decisiones: " + string.Join("; ", t.Decisions.Skip(Math.Max(0, t.Decisions.Count - 3)).Select(d => Clip(Convert.ToString(d.TryGetValue("texto", out var x) ? x : "", CultureInfo.InvariantCulture), 80))) + ".");
            if (t.Exceptions.Count > 0) parts.Add("Excepciones: " + t.Exceptions.Count + ".");
            if (!string.IsNullOrEmpty(t.Reason)) parts.Add("Motivo: " + Clip(t.Reason, 200) + ".");
            parts.Add("Siguiente: " + Next(t, units) + ".");
            return string.Join(" ", parts);
        }

        public static string Next(TaskRecord t, UnitLog units)
        {
            switch (t.State)
            {
                case TaskState.Aplicada: return "nada; revisar artefactos si hace falta";
                case TaskState.AplicadaConDiferencias:
                {
                    // Señala el estado que de verdad tiene unidades pendientes de revisar (no siempre es conflicto).
                    var st = new[] { UnitState.Revertida, UnitState.Conflicto, UnitState.Parcial, UnitState.Omitida }
                        .FirstOrDefault(s => units.Units.Values.Any(u => u.State == s));
                    var which = units.Units.Values.Any(u => u.State == st) ? "tarea unidades estado=" + st.ToString().ToLowerInvariant() + ", " : "";
                    return "revisar desviaciones (" + which + "qc_final) y decidir; tarea reanudar reintenta las revertidas";
                }
                case TaskState.Cancelada: return "nada salvo que se quiera reanudar (tarea reanudar)";
                case TaskState.Interrumpida: return units.Units.Values.Any(u => u.State == UnitState.Desconocida)
                    ? "tarea reconciliar con decision=aplicada|no_aplicada para las dudosas; luego tarea reanudar"
                    : "tarea reanudar (verifica lo dudoso y continúa)";
                case TaskState.Parcial: return "leer el motivo; corregir y tarea reanudar";
                case TaskState.EsperandoContexto: return "resolver lo indicado en el motivo; la tarea sigue sola (o tarea reanudar si dejó de esperar)";
                default: return "esperar (tarea esperar desde=" + t.Rev + ")";
            }
        }

        /// <summary>Estado compacto para la respuesta del agente: conteos, progreso, desviaciones (máx. 5), siguiente acción y artefactos.</summary>
        public static Dictionary<string, object> Status(TaskRecord t, UnitLog units, bool includeContinuation)
        {
            var c = units.Counts();
            long N(string s) => c.TryGetValue(s, out var n) ? n : 0;
            var d = new Dictionary<string, object>
            {
                ["tarea"] = t.Id,
                ["estado"] = t.State,
                ["rev"] = t.Rev,
                ["progreso"] = (N(UnitState.Confirmada) + N(UnitState.Omitida)) + "/" + t.UnitsTotal,
            };
            var counts = new Dictionary<string, object>();
            foreach (var kv in c.OrderBy(k => k.Key, StringComparer.Ordinal)) counts[kv.Key] = kv.Value;
            d["unidades"] = counts;
            if (!string.IsNullOrEmpty(t.Reason)) d["motivo"] = t.Reason;
            var dev = units.Units.Values.Where(u => u.State == UnitState.Conflicto || u.State == UnitState.Revertida || UnitState.NeedsReconcile(u.State))
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
