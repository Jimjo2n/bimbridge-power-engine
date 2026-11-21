// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Bimbridge.Protocol
{
    /// <summary>Constants of the internal bridge ↔ add-in protocol. The AI never sees them.</summary>
    public static class Wire
    {
        /// <summary>Bumped when the format changes. Bridge and add-in must match.</summary>
        public const int ProtocolVersion = 3;

        public const string PipePrefix = "Bimbridge.";

        // Operations
        public const string OpHello = "hello";
        public const string WriteReceiptFeature = "write_receipt";
        public const string LargeResultFeature = "out_of_frame_result";
        public const string OpPing = "ping";
        public const string OpContext = "context";
        public const string OpQuery = "query";
        public const string OpSets = "sets";
        public const string OpCheck = "check";
        public const string OpPlan = "plan";
        public const string OpCommit = "commit";
        public const string OpJob = "job";
        public const string OpGeo = "geo";
        public const string OpCatalog = "catalog";
        public const string OpShow = "show";
        public const string OpCapture = "capture";
        public const string OpParams = "params";
        /// <summary>General runner: compiles and runs C# against the public Revit API (rolled-back query, run with or without a transaction).</summary>
        public const string OpExec = "exec";
        /// <summary>Open documents (projects and families) with a stable dN handle.</summary>
        public const string OpDocs = "docs";
        /// <summary>Families: info, faces, connectors, parameters, edit, load (typed reads and writes).</summary>
        public const string OpFam = "fam";
        /// <summary>Document operations: save, save as, sync, PDF, close.</summary>
        public const string OpDoc = "doc";
        /// <summary>0.6.0: long resumable tasks (start, status, wait, cancel, resume, reconcile, summary…).</summary>
        public const string OpTask = "task";
        /// <summary>Args key with the target document (dN handle, path or title). Without it: the document active when the request arrives, fixed for the whole request.</summary>
        public const string DocArg = "doc";
        /// <summary>Idempotency key: the same key on the same document is not applied again.</summary>
        public const string KeyArg = "key";
        public const string TaskArg = "task";
        /// <summary>Optional args key with the name of the tool the AI saw (e.g. "get"), journal only.</summary>
        public const string ToolArg = "_tool";
    }

    /// <summary>Response statuses (architecture section 6). These are what the AI sees.</summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string Queued = "queued";
        public const string InProgress = "in_progress";
        /// <summary>The change is in the Revit queue; the user will apply it with the "Apply queue" button.</summary>
        public const string Held = "held";
        public const string Conflict = "conflict";
        public const string RevitBusy = "revit_busy";
        public const string RolledBack = "reverted";
        public const string Expired = "expired";
        public const string Rejected = "rejected";
        public const string Invalid = "invalid";
        public const string RevitUnavailable = "revit_unavailable";
        public const string Incompatible = "incompatible";
        public const string Error = "error";
        /// <summary>The request deadline passed and the work was cancelled inside Revit.</summary>
        public const string TimedOut = "timed_out";
        /// <summary>The operation does not apply to this document type or Revit version (says which, and what alternative exists).</summary>
        public const string NotSupported = "not_supported";
        /// <summary>The pinned document or Revit is no longer open. Not redirected to another one.</summary>
        public const string TargetUnavailable = "target_unavailable";
        /// <summary>A user decision is needed (e.g. Hold is on, ambiguous data left unresolved).</summary>
        public const string NeedsUser = "needs_user";
        /// <summary>An operation with that key started and its end is not recorded (Revit closed or was cut off): reconcile before repeating.</summary>
        public const string Interrupted = "interrupted";
        /// <summary>Applied, but the follow-up verification found differences from what was requested (they are listed).</summary>
        public const string CompletedWithDifferences = "completed_with_differences";

        // 0.6.0: semantic statuses that tell apart what was applied, what was rolled back and what is unknown.
        /// <summary>One part was committed and another was not (e.g. tx=ninguna with a failure halfway, or one document yes and another no). Reconcile before repeating.</summary>
        public const string Partial = "partial";
        /// <summary>It cannot be known whether it was applied (the evidence was lost). Reconcile before repeating.</summary>
        public const string Unknown = "unknown";
        /// <summary>The changes were committed, but producing the response failed. Nothing was rolled back.</summary>
        public const string AppliedResultError = "applied_result_error";
        public const string CancelRequested = "cancellation_requested";
        /// <summary>Cancellation confirmed: what was committed before cancelling stays applied and is listed.</summary>
        public const string Cancelled = "cancelled";
        /// <summary>The task is waiting on context (user pause, open dialog, closed document). It keeps its progress.</summary>
        public const string WaitingContext = "waiting_for_context";
        /// <summary>The operation journal is unavailable: a write with clave= is not applied when its idempotency cannot be guaranteed.</summary>
        public const string JournalUnavailable = "journal_unavailable";
        /// <summary>A name matches several documents or elements: candidates are returned, the first one is not picked.</summary>
        public const string Ambiguous = "ambiguous";
        /// <summary>Another task or barrier holds the document; the write was not applied.</summary>
        public const string DocumentBusy = "document_busy";

        /// <summary>Terminal statuses that confirm NOTHING was applied (can be retried with the same key).</summary>
        public static bool ConfirmsNothingApplied(string s) =>
            s == RolledBack || s == Rejected || s == Invalid || s == TargetUnavailable || s == NotSupported || s == NeedsUser ||
            s == Conflict || s == RevitBusy || s == Expired || s == JournalUnavailable || s == Ambiguous || s == DocumentBusy ||
            s == Incompatible || s == RevitUnavailable || s == TimedOut;

        /// <summary>Terminal statuses that count as applied (repeating the key returns the stored result).</summary>
        public static bool CountsAsApplied(string s) => s == Ok || s == CompletedWithDifferences || s == Held || s == AppliedResultError;
    }

    public sealed class Request : IJsonWritable
    {
        public long Id;
        public string Op;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>Maximum time the bridge will wait; the add-in uses it to avoid wasted work.</summary>
        public int DeadlineMs;
        public string AttemptId;

        public object ToJson()
        {
            var data = new Dictionary<string, object> { ["id"] = Id, ["op"] = Op, ["args"] = Args, ["deadline_ms"] = DeadlineMs };
            if (AttemptId != null) data["attempt_id"] = AttemptId;
            return data;
        }

        public static Request FromJson(string json)
        {
            var o = Json.ParseObject(json);
            return new Request
            {
                Id = Arg.GetLong(o, "id") ?? 0,
                Op = Arg.GetString(o, "op"),
                AttemptId = LegacyAliases.GetString(o, "attempt_id"),
                Args = o.TryGetValue("args", out var a) && a is Dictionary<string, object> d
                    ? d
                    : new Dictionary<string, object>(StringComparer.Ordinal),
                DeadlineMs = (int)(Arg.GetLong(o, "deadline_ms") ?? 0),
            };
        }
    }

    public sealed class Response : IJsonWritable
    {
        public long Id;
        public string State = Status.Ok;
        public long? Rev;
        public object Data;
        public string Warning;

        public object ToJson()
        {
            var d = new Dictionary<string, object> { ["id"] = Id, ["status"] = State };
            if (Rev.HasValue) d["rev"] = Rev.Value;
            if (Data != null) d["data"] = Data;
            if (!string.IsNullOrEmpty(Warning)) d["warning"] = Warning;
            return d;
        }

        public static Response FromJson(string json)
        {
            var o = Json.ParseObject(json);
            LegacyAliases.TryGet(o, "data", out var data);
            return new Response
            {
                Id = Arg.GetLong(o, "id") ?? 0,
                State = LegacyAliases.GetString(o, "status") ?? Status.Error,
                Rev = Arg.GetLong(o, "rev"),
                Data = data,
                Warning = LegacyAliases.GetString(o, "warning"),
            };
        }

        public static Response Fail(long id, string status, string warning) =>
            new Response { Id = id, State = status, Warning = warning };
    }

    /// <summary>Lenient reading of already-parsed JSON arguments.</summary>
    public static class Arg
    {
        public static string GetString(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            return v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static long? GetLong(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            switch (v)
            {
                case long l: return l;
                case int i: return i;
                case double d when Math.Abs(d - Math.Round(d)) < 1e-9: return (long)Math.Round(d);
                case string s when long.TryParse(s, out var p): return p;
                default: return null;
            }
        }

        public static bool? GetBool(IDictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return null;
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out var p)) return p;
            return null;
        }

        /// <summary>Accepts "a" or ["a","b"] and always returns a list.</summary>
        public static List<string> GetStringList(IDictionary<string, object> o, string key)
        {
            var result = new List<string>();
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return result;
            if (v is string s)
            {
                foreach (var part in s.Split(','))
                    if (part.Trim().Length > 0) result.Add(part.Trim());
                return result;
            }
            if (v is List<object> l)
            {
                foreach (var item in l)
                    if (item != null) result.Add(Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture));
                return result;
            }
            // A bare number (ids=12345) is also a list of one.
            if (v is long || v is int || v is double) result.Add(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            return result;
        }
    }
}
