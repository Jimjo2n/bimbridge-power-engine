// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Addin.Revit.Writes;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Fachada de escritura (fase 3) para el commit directo y para el botón Aplicar cola. Todo corre en el hilo de Revit.
    /// Planificar: <see cref="PlanBuilder"/> (27 operaciones, validadas contra el modelo sin tocarlo).
    /// Aplicar: <see cref="PlanApplier"/> (conflictos, un TransactionGroup = un Deshacer, @n, verificación).
    /// </summary>
    public static class PlanOps
    {
        public const int MaxEntries = PlanOpsLimits.MaxEntries;

        public sealed class ApplyResult
        {
            public string Status;      // PlanStatus.Aplicado / Conflicto / Revertido
            public string Detail;
            public int Changed;
            public int Deleted;
            public int Created;
            public List<string> Skipped = new List<string>();
            public List<string> Warnings = new List<string>();
            /// <summary>rc3: aplicado, pero su resultado no quedó en la auditoría (requiere reconciliar).</summary>
            public bool AuditIncomplete;
            /// <summary>alpha3, commit modo=ensayo: verificación temporal, estado literal del grupo y restauración comprobada.</summary>
            public Dictionary<string, object> Trial;
        }

        /// <summary>Valor canónico actual de (elemento, campo); guardas para las creaciones.</summary>
        public static string Current(Document doc, PlanEntry entry) => new PlanApplier(doc, null, 1, null).Current(entry);

        public static ApplyResult Apply(Document doc, Plan plan, int subBatch, string label) => new PlanApplier(doc, plan, subBatch, label).Run();

        /// <summary>alpha3: aplica el plan con el MISMO motor (conflictos, lotes, @n, fallos), relee, y revierte el grupo entero.</summary>
        public static ApplyResult Trial(Document doc, Plan plan, int subBatch, string label) => new PlanApplier(doc, plan, subBatch, label) { Trial = true }.Run();
    }
}
