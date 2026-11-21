// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Workflow
{
    /// <summary>
    /// 0.8.0 (delivery contract; incidents from the report: "one branch was synced and another was opened", "the sheet was reviewed but the PDF
    /// was from an earlier revision"). A delivery advances through DISTINCT milestones, each with its receipt. No milestone is skipped, a later one
    /// is not declared by inference, and a receipt from another document or another revision does not count.
    /// </summary>
    public static class DeliveryStages
    {
        public const string PlanGenerated = "plan_generated", PlanValidated = "plan_validated", PlanAuthorized = "plan_authorized",
            OperationRun = "operation_executed", CommitConfirmed = "commit_confirmed", StateObserved = "observed_status",
            Saved = "changes_saved", Synced = "model_synced", SheetSetPersisted = "sheet_set_persisted",
            Exported = "file_exported", ArtifactVerified = "artifact_verified", DeliveryConfirmed = "delivery_confirmed";

        /// <summary>Canonical order. Guardado/Sincronizado/Set are optional depending on the delivery (see <see cref="DeliveryTrack.Required"/>).</summary>
        public static readonly string[] Order =
        {
            PlanGenerated, PlanValidated, PlanAuthorized, OperationRun, CommitConfirmed, StateObserved,
            Saved, Synced, SheetSetPersisted, Exported, ArtifactVerified, DeliveryConfirmed,
        };

        /// <summary>
        /// 0.9.0 (P0): critical milestones. Their receipt requires the model revision and provenance: "origin" (which operation or person
        /// produced it) and "evidence" = VERIFIED (observed by BIMBRIDGE). A REPORTED result never closes a critical milestone,
        /// except the delivery confirmation, which is a statement by a person ("origin" = "usuario:…").
        /// </summary>
        public static readonly HashSet<string> Critical = new HashSet<string>
        {
            CommitConfirmed, StateObserved, Saved, Synced, SheetSetPersisted, Exported, ArtifactVerified, DeliveryConfirmed,
        };

        /// <summary>What each milestone's receipt must carry (besides document and revision).</summary>
        public static readonly Dictionary<string, string[]> ReceiptFields = new Dictionary<string, string[]>
        {
            [PlanGenerated] = new[] { "plan_id" },
            [PlanValidated] = new[] { "plan_id", "validator" },
            [PlanAuthorized] = new[] { "plan_id", "authorizes" },
            [OperationRun] = new[] { "plan_id" },
            [CommitConfirmed] = new[] { "plan_id", "rev" },
            [StateObserved] = new[] { "rev", "read" },
            [Saved] = new[] { "disk_fingerprint" },
            [Synced] = new[] { "central", "central_fingerprint" },
            [SheetSetPersisted] = new[] { "set", "sheets" },
            [Exported] = new[] { "file", "sha256" },
            [ArtifactVerified] = new[] { "file", "sha256", "pages" },
            [DeliveryConfirmed] = new[] { "confirms" },
        };
    }

    public sealed class DeliveryReceipt
    {
        public string Stage;
        public string DocKey;       // document identity (key + fingerprint)
        public string Revision;     // revision/snapshot of the model it refers to
        public string At;
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["milestone"] = Stage, ["document"] = DocKey, ["revision"] = Revision, ["in"] = At, ["data"] = Data,
        };

        public static DeliveryReceipt FromJson(Dictionary<string, object> d) => new DeliveryReceipt
        {
            Stage = Arg.GetString(d, "milestone"), DocKey = Arg.GetString(d, "document"), Revision = Arg.GetString(d, "revision"), At = Arg.GetString(d, "in"),
            Data = d.TryGetValue("data", out var x) && x is Dictionary<string, object> dd ? dd : new Dictionary<string, object>(),
        };
    }

    /// <summary>Tracking of one specific delivery on ONE document.</summary>
    public sealed class DeliveryTrack
    {
        public string Id;
        public string DocKey;
        public List<string> Required;   // milestones this delivery requires, in canonical order
        public readonly List<DeliveryReceipt> Receipts = new List<DeliveryReceipt>();

        public DeliveryTrack(string id, string docKey, IEnumerable<string> required = null)
        {
            Id = id; DocKey = docKey;
            var req = new HashSet<string>(required ?? DeliveryStages.Order);
            Required = DeliveryStages.Order.Where(req.Contains).ToList();
        }

        public string Current => Receipts.Count == 0 ? null : Receipts[Receipts.Count - 1].Stage;
        public string Next => Required.FirstOrDefault(s => !Receipts.Any(r => r.Stage == s));
        public bool Complete => Next == null;

        /// <summary>Records a milestone. Returns null if accepted, or the reason for rejection.</summary>
        public string Record(DeliveryReceipt r)
        {
            if (r == null || string.IsNullOrEmpty(r.Stage)) return "empty receipt";
            if (!Required.Contains(r.Stage)) return "milestone " + r.Stage + " is not part of this delivery";
            if (Receipts.Any(x => x.Stage == r.Stage)) return "milestone " + r.Stage + " already has a receipt";
            if (r.Stage != Next) return "out of order: the next milestone is " + Next + ", not " + r.Stage + " (a later milestone is not declared by inference)";
            if (!string.Equals(r.DocKey, DocKey, StringComparison.OrdinalIgnoreCase))
                return "the receipt is from another document (" + r.DocKey + "); a result in one document proves nothing in " + DocKey;
            foreach (var f in DeliveryStages.ReceiptFields[r.Stage])
                if (!r.Data.TryGetValue(f, out var v) || v == null || (v is string s && s.Length == 0)) return r.Stage + ": the receipt is missing " + f;
            if (DeliveryStages.Critical.Contains(r.Stage))
            {
                if (string.IsNullOrWhiteSpace(r.Revision)) return r.Stage + ": critical milestone without a model revision; it is unknown which state it refers to";
                var origin = Arg.GetString(r.Data, "origin");
                if (string.IsNullOrWhiteSpace(origin)) return r.Stage + ": provenance is missing (origin: operation or person that produced it)";
                var ev = Arg.GetString(r.Data, "evidence");
                if (r.Stage == DeliveryStages.DeliveryConfirmed)
                {
                    if (!origin.StartsWith("usuario:", StringComparison.OrdinalIgnoreCase)) return r.Stage + ": the delivery confirmation is given by a person (origin=user:…), not the agent";
                }
                else if (ev != Confidence.Verified)
                    return r.Stage + ": the evidence is " + (ev ?? "(not stated)") + "; a critical milestone requires VERIFIED (observed by BIMBRIDGE), not a reported result";
            }
            // 0.9.0: the exported artifact and its verification must be from the SAME revision (not merely not earlier)
            if (r.Stage == DeliveryStages.ArtifactVerified)
            {
                var exp0 = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.Exported);
                if (exp0 != null && exp0.Revision != null && !string.Equals(exp0.Revision, r.Revision, StringComparison.Ordinal))
                    return "artifact_verified: revision " + r.Revision + " was verified but revision " + exp0.Revision + " was exported";
            }
            // the revision cannot go backwards: a PDF from a revision earlier than the commit does not count
            var lastRev = Receipts.LastOrDefault(x => x.Revision != null)?.Revision;
            if (lastRev != null && r.Revision != null && CompareRev(r.Revision, lastRev) < 0)
                return r.Stage + ": the receipt is from revision " + r.Revision + ", earlier than " + lastRev + " (stale artifact)";
            if (r.Stage == DeliveryStages.ArtifactVerified)
            {
                var exp = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.Exported);
                if (exp != null && !string.Equals(Arg.GetString(exp.Data, "sha256"), Arg.GetString(r.Data, "sha256"), StringComparison.OrdinalIgnoreCase))
                    return "artifact_verified: the verified file is not the exported one (different sha256)";
                var set = Receipts.FirstOrDefault(x => x.Stage == DeliveryStages.SheetSetPersisted);
                var expected = set == null ? null : Arg.GetLong(set.Data, "sheets");
                var pages = Arg.GetLong(r.Data, "pages");
                if (expected.HasValue && pages.HasValue && expected.Value != pages.Value)
                    return "artifact_verified: the PDF has " + pages + " pages and the set " + expected + " sheets";
            }
            r.At = r.At ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Receipts.Add(r);
            return null;
        }

        private static int CompareRev(string a, string b)
        {
            if (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) return x.CompareTo(y);
            return string.CompareOrdinal(a, b);
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["document"] = DocKey, ["milestones"] = Required.Cast<object>().ToList(), ["current"] = Current, ["next"] = Next,
            ["full"] = Complete, ["receipts"] = Receipts.Select(r => (object)r.ToJson()).ToList(),
        };
    }
}
