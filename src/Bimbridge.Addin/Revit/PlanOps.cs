// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Revit.Writes;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Write facade (phase 3) for direct commit and for the Apply queue button. Everything runs on the Revit thread.
    /// Plan: <see cref="PlanBuilder"/> (27 operations, validated against the model without touching it).
    /// Apply: <see cref="PlanApplier"/> (conflicts, one TransactionGroup = one Undo, @n, verification).
    /// </summary>
    public static class PlanOps
    {
        public const int MaxEntries = PlanOpsLimits.MaxEntries;

        public sealed class ApplyResult
        {
            public string Status;      // PlanStatus.Applied / Conflict / RolledBack
            public string Detail;
            public int Changed;
            public int Deleted;
            public int Created;
            public List<string> Skipped = new List<string>();
            public List<string> Warnings = new List<string>();
            /// <summary>rc3: applied, but its result was not recorded in the audit (requires reconciliation).</summary>
            public bool AuditIncomplete;
            /// <summary>alpha3, commit modo=ensayo: temporary verification, literal group status and verified restoration.</summary>
            public Dictionary<string, object> Trial;
        }

        /// <summary>Current canonical value of (element, field); guards for creations.</summary>
        public static string Current(Document doc, PlanEntry entry) => new PlanApplier(doc, null, 1, null).Current(entry);

        public static ApplyResult Apply(Document doc, Plan plan, int subBatch, string label) => new PlanApplier(doc, plan, subBatch, label).Run();

        /// <summary>alpha3: applies the plan with the SAME engine (conflicts, batches, @n, failures), re-reads, and rolls back the whole group.</summary>
        public static ApplyResult Trial(Document doc, Plan plan, int subBatch, string label) => new PlanApplier(doc, plan, subBatch, label) { Trial = true }.Run();
    }
}
