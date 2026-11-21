// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>Write state of a document. Any doubt (evidence, damaged file, disk error) leaves it blocked.</summary>
    public sealed class DocWriteBlock
    {
        public const string Code = "document_locked_reconciliation";
        public string DocKey;
        public List<string> Plans = new List<string>();
        public List<string> CorruptFiles = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Blocked => Plans.Count > 0 || CorruptFiles.Count > 0 || Errors.Count > 0;

        public string Message =>
            "Nothing was applied: the document has unreconciled operations" +
            (Plans.Count > 0 ? " (" + string.Join(", ", Plans) + ")" : "") +
            (CorruptFiles.Count > 0 ? "; damaged evidence files: " + string.Join(", ", CorruptFiles) : "") +
            (Errors.Count > 0 ? "; could not read the evidence: " + string.Join("; ", Errors) : "") +
            ". Use job op=reconcile plan_id=… to check it against the model; do not repeat the operation.";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            // The code only appears when blocked: a "document_locked" with bloqueado=no confuses the reader.
            ["code"] = Blocked ? Code : null,
            ["blocked"] = Blocked,
            ["plans"] = Plans.Cast<object>().ToList(),
            ["damaged_files"] = CorruptFiles.Cast<object>().ToList(),
            ["errors"] = Errors.Cast<object>().ToList(),
        };
    }

    public sealed class DocWriteBlockedException : InvalidOperationException
    {
        public DocWriteBlock Block { get; }
        public DocWriteBlockedException(DocWriteBlock block) : base(block.Message) { Block = block; }
    }

    /// <summary>The evidence did not reach disk: the caller must not write to the model.</summary>
    public sealed class PlanStoreException : System.IO.IOException
    {
        public PlanStoreException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Result of comparing a plan's evidence with the current model. Never guesses: what cannot be checked does not release.</summary>
    public sealed class ReconcileCheck
    {
        public const string Reverted = "reverted_verified";
        public const string Applied = "applied_verified";
        public const string NotVerifiable = "not_verifiable";
        public const string ManualHuman = "human_manual";

        public string Verdict = NotVerifiable;
        public int Checked;
        public List<string> Reasons = new List<string>();

        public bool Verified => Verdict == Reverted || Verdict == Applied;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["verdict"] = Verdict,
            ["checked"] = (long)Checked,
            ["reasons"] = Reasons.Take(20).Cast<object>().ToList(),
        };
    }

    public static class ReconcileEvaluator
    {
        /// <param name="current">Current canonical value of the entry, like PlanApplier.Current.</param>
        /// <param name="exists">Does that id exist in the document?</param>
        public static ReconcileCheck Evaluate(Plan plan, Func<PlanEntry, string> current, Func<long, bool> exists)
        {
            var result = new ReconcileCheck();
            var entries = plan.Entries.Where(Comparable).ToList();
            var created = plan.Created.Distinct().ToList();
            var deleted = EvidenceIds(plan, "deleted_ids");
            var plannedCreations = plan.Entries.Count(e => e.IsCreation);
            var templates = plan.Entries.Count(e => e.Ref > 0);

            int asBefore = 0, asAfter = 0;
            try
            {
                foreach (var e in entries)
                {
                    var now = current(e);
                    result.Checked++;
                    if (PlanEntry.Same(now, e.Before)) asBefore++;
                    else if (PlanEntry.Same(now, e.After)) asAfter++;
                    else result.Reasons.Add(e.Id + " " + e.Key + ": matches neither the before nor the after");
                }
                var createdPresent = created.Where(exists).ToList();
                var survivors = created.Where(id => !deleted.Contains(id)).ToList();
                var deletedOriginals = deleted.Where(id => !created.Contains(id)).ToList();
                result.Checked += created.Count + deletedOriginals.Count;

                var revertedOk = asBefore == entries.Count && createdPresent.Count == 0 && deletedOriginals.All(exists);
                var appliedOk = asAfter == entries.Count && survivors.All(exists) && deletedOriginals.All(id => !exists(id));

                // Without ids it cannot be shown what happened to what was created.
                if (plannedCreations + templates > 0 && created.Count == 0 && plan.AppliedBatches > 0)
                {
                    result.Reasons.Add("the plan creates elements and the evidence does not keep their ids");
                    return result;
                }
                if (revertedOk && (entries.Count > 0 || created.Count > 0 || plan.AppliedBatches == 0))
                {
                    result.Verdict = ReconcileCheck.Reverted;
                    return result;
                }
                // "Applied" requires seeing the whole plan: complete creations and no @n templates, which have no comparable after.
                if (appliedOk && templates == 0 && created.Count >= plannedCreations && (entries.Count > 0 || created.Count > 0))
                {
                    result.Verdict = ReconcileCheck.Applied;
                    return result;
                }
                result.Reasons.Add("mixed state: " + asBefore + " as before, " + asAfter + " as after, of " + entries.Count +
                                   "; created present " + createdPresent.Count + " of " + created.Count +
                                   (templates > 0 ? "; includes " + templates + " @n templates" : ""));
            }
            catch (Exception ex)
            {
                result.Reasons.Add("could not read the model: " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        private static bool Comparable(PlanEntry e) =>
            e.Ref == 0 && e.Id != 0 && e.Key != "new" && !(e.Key != null && e.Key.Contains("@@"));

        private static HashSet<long> EvidenceIds(Plan plan, string key)
        {
            var set = new HashSet<long>();
            if (plan.Reconciliation != null && plan.Reconciliation.TryGetValue(key, out var v) && v is List<object> list)
                foreach (var x in list)
                    set.Add(Convert.ToInt64(x, CultureInfo.InvariantCulture));
            return set;
        }
    }
}
