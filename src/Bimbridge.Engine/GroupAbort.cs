// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Rollback decision for a transaction group, independent of the Revit API so it can be tested by running it.
    /// Rule: only the literal RolledBack status authorizes forgetting what was created; any other result keeps the evidence.
    /// </summary>
    public static class GroupAbort
    {
        public const string RolledBack = "RolledBack";
        public const string NotObserved = "NoObservado";

        /// <summary>Attempts the rollback and returns the literal status. An exception is never interpreted as a rollback.</summary>
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

        /// <summary>Evidence the applier had when aborting.</summary>
        public sealed class Evidence
        {
            public IEnumerable<long> Deleted = Enumerable.Empty<long>();
            public int Changed, DeletedCount;
            public IDictionary<string, List<long>> Skipped = new Dictionary<string, List<long>>();
            public IEnumerable<string> Warnings = Enumerable.Empty<string>();
        }

        /// <summary>Keeps the evidence intact for reconciliation. Does not truncate IDs.</summary>
        public static void Preserve(Plan plan, string state, string error, Evidence e)
        {
            plan.Reconciliation = new Dictionary<string, object>
            {
                ["group_status"] = state,
                ["rollback_error"] = error,
                ["created_ids"] = plan.Created.Cast<object>().ToList(),
                ["created_by_op"] = plan.CreatedByOp.ToDictionary(k => "@" + k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["deleted_ids"] = e.Deleted.OrderBy(id => id).Cast<object>().ToList(),
                ["attempted_changes"] = (long)e.Changed,
                ["attempted_deletes"] = (long)e.DeletedCount,
                ["committed_batches"] = (long)plan.AppliedBatches,
                ["total_batches_count"] = (long)plan.TotalBatches,
                ["skipped"] = e.Skipped.ToDictionary(k => k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["warnings"] = e.Warnings.Cast<object>().ToList(),
                ["inputs"] = plan.Entries.Select(x => x.ToJson()).ToList(),
            };
        }

        /// <summary>Interrupted plan without applier data: all that is known is that it may have started.</summary>
        public static void PreserveInterrupted(Plan plan, string reason)
        {
            if (plan.Reconciliation != null) return;
            Preserve(plan, NotObserved, reason, new Evidence());
        }

        /// <summary>
        /// Dispose exception after the result is known. A status confirmed by Revit is kept and a warning is raised;
        /// without a result, or with a result that was not confirmed, it stays unknown.
        /// </summary>
        public static string StatusAfterDisposeError(string status) =>
            status == PlanStatus.Applied || status == PlanStatus.RolledBack || status == PlanStatus.Trialed || status == PlanStatus.Conflict
                ? status : Status.Unknown;

        /// <summary>
        /// Functional trial failure: elements created and later deleted by the same plan must not be counted as missing.
        /// Returns how many creations must still exist during the temporary verification.
        /// </summary>
        public static int ExpectedSurvivingCreations(int plannedCreations, IEnumerable<long> created, IEnumerable<long> deleted)
        {
            var gone = new HashSet<long>(deleted);
            return plannedCreations - created.Count(gone.Contains);
        }
    }
}
