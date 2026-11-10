// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Decisión de reversión de un grupo de transacciones, sin depender de la API de Revit para poder probarla ejecutándola.
    /// Regla: sólo el estado literal RolledBack autoriza a olvidar lo creado; cualquier otro resultado conserva la evidencia.
    /// </summary>
    public static class GroupAbort
    {
        public const string RolledBack = "RolledBack";
        public const string NotObserved = "NoObservado";

        /// <summary>Intenta revertir y devuelve el estado literal. Una excepción nunca se interpreta como reversión.</summary>
        public static string Attempt(Func<bool> hasStarted, Func<string> rollBack, Func<string> status, out string error)
        {
            error = null;
            try { return hasStarted() ? rollBack() : status(); }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return NotObserved;
            }
        }

        /// <summary>Evidencia que el aplicador tenía al abortar.</summary>
        public sealed class Evidence
        {
            public IEnumerable<long> Deleted = Enumerable.Empty<long>();
            public int Changed, DeletedCount;
            public IDictionary<string, List<long>> Skipped = new Dictionary<string, List<long>>();
            public IEnumerable<string> Warnings = Enumerable.Empty<string>();
        }

        /// <summary>Conserva íntegra la evidencia para reconciliar. No recorta IDs.</summary>
        public static void Preserve(Plan plan, string state, string error, Evidence e)
        {
            plan.Reconciliation = new Dictionary<string, object>
            {
                ["grupo_estado"] = state,
                ["rollback_error"] = error,
                ["ids_creados"] = plan.Created.Cast<object>().ToList(),
                ["creados_por_op"] = plan.CreatedByOp.ToDictionary(k => "@" + k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["ids_borrados"] = e.Deleted.OrderBy(id => id).Cast<object>().ToList(),
                ["cambios_intentados"] = (long)e.Changed,
                ["borrados_intentados"] = (long)e.DeletedCount,
                ["lotes_confirmados"] = (long)plan.AppliedBatches,
                ["lotes_totales"] = (long)plan.TotalBatches,
                ["omitidos"] = e.Skipped.ToDictionary(k => k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["avisos"] = e.Warnings.Cast<object>().ToList(),
                ["entradas"] = plan.Entries.Select(x => x.ToJson()).ToList(),
            };
        }

        /// <summary>Plan interrumpido sin datos del aplicador: sólo se sabe que pudo haber empezado.</summary>
        public static void PreserveInterrupted(Plan plan, string reason)
        {
            if (plan.Reconciliation != null) return;
            Preserve(plan, NotObserved, reason, new Evidence());
        }

        /// <summary>
        /// Excepción de Dispose después de conocer el resultado. Un estado confirmado por Revit se mantiene y se avisa;
        /// sin resultado, o con un resultado que no estaba confirmado, queda desconocido.
        /// </summary>
        public static string StatusAfterDisposeError(string status) =>
            status == PlanStatus.Aplicado || status == PlanStatus.Revertido || status == PlanStatus.Ensayado || status == PlanStatus.Conflicto
                ? status : Status.Desconocido;

        /// <summary>
        /// Fallo funcional del ensayo: elementos creados que luego borró el mismo plan no deben contarse como ausentes.
        /// Devuelve cuántas creaciones deben seguir existiendo durante la verificación temporal.
        /// </summary>
        public static int ExpectedSurvivingCreations(int plannedCreations, IEnumerable<long> created, IEnumerable<long> deleted)
        {
            var gone = new HashSet<long>(deleted);
            return plannedCreations - created.Count(gone.Contains);
        }
    }
}
