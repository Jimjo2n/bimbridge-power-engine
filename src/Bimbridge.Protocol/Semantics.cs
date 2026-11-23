// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// The SINGLE normalizer of modes and actions (0.6.0, H01). Used by the bridge (so writes are not redirected to another Revit) and the add-in
    /// (for the journal, Hold, barriers). Previously each side had its own list: "write" or " run " were writes for the add-in and
    /// reads for the bridge. An unknown mode is invalid; it never becomes a read.
    /// </summary>
    public static class OpSemantics
    {
        public enum Kind { Read, Write, Invalid }

        public const string ModeQuery = "query";
        public const string ModeRun = "run";
        /// <summary>alpha3: TEMPORARY modification inside a group that is always rolled back. It changes the model while running, so it is
        /// a write for authorization (level W) and routing; query no longer opens a transaction (Revit blocks any change).</summary>
        public const string ModeTrial = "trial";

        private static readonly string[] QueryModes = { "query", "read" };
        private static readonly string[] TrialModes = { "trial", "test", "dry_run", "simulate" };
        private static readonly string[] RunModes = { "run", "execute", "write", "apply" };

        /// <summary>Normalized exec mode: "query", "run", "trial" or null if not recognized. No mode = query.</summary>
        public static string ExecMode(IDictionary<string, object> args, out string error)
        {
            error = null;
            var raw = Arg.GetString(args, "mode");
            if (raw == null) return ModeQuery;
            var m = Norm(raw);
            if (m.Length == 0) return ModeQuery;
            if (QueryModes.Contains(m)) return ModeQuery;
            if (RunModes.Contains(m)) return ModeRun;
            if (TrialModes.Contains(m)) return ModeTrial;
            error = "mode '" + raw.Trim() + "' does not exist: use query (read-only), trial (changes and rolls back) or run (applies changes)";
            return null;
        }

        public static readonly string[] FamReads = { "info", "faces", "search" };
        public static readonly string[] FamWrites = { "open", "connector", "param", "load", "load_file" };

        public static string FamVerb(IDictionary<string, object> args) => Norm(Arg.GetString(args, "action") ?? "info");

        public static readonly string[] DocVerbs = { "save", "save_as", "sync", "pdf", "close" };

        /// <summary>Task verbs: read (status, wait…) or write (start, resume…).</summary>
        public static readonly string[] TaskReads = { "status", "wait", "summary", "list", "units", "recipes" };
        public static readonly string[] TaskWrites = { "start", "resume", "cancel", "reconcile", "note", "forget" };

        public static string TaskVerb(IDictionary<string, object> args) => Norm(Arg.GetString(args, "action") ?? "status");

        /// <summary>Classifies a request. error carries the reason when it is Invalid.</summary>
        public static Kind Classify(string op, IDictionary<string, object> args, out string error)
        {
            error = null;
            switch (op)
            {
                case Wire.OpCommit:
                case Wire.OpDoc:
                    return Kind.Write;
                case Wire.OpExec:
                    if (string.Equals(Norm(Arg.GetString(args, "action")), "list", StringComparison.Ordinal)) return Kind.Read;
                    var m = ExecMode(args, out error);
                    return m == null ? Kind.Invalid : m == ModeQuery ? Kind.Read : Kind.Write;
                case Wire.OpFam:
                    var f = FamVerb(args);
                    if (FamReads.Contains(f)) return Kind.Read;
                    if (FamWrites.Contains(f)) return Kind.Write;
                    error = "fam: action '" + f + "' does not exist (" + string.Join(", ", FamReads.Concat(FamWrites)) + ")";
                    return Kind.Invalid;
                case Wire.OpTask:
                    var t = TaskVerb(args);
                    if (TaskReads.Contains(t)) return Kind.Read;
                    if (TaskWrites.Contains(t)) return Kind.Write;
                    error = "task: action '" + t + "' does not exist (" + string.Join(", ", TaskReads.Concat(TaskWrites)) + ")";
                    return Kind.Invalid;
                default:
                    return Kind.Read;
            }
        }

        public static bool IsWrite(string op, IDictionary<string, object> args) => Classify(op, args, out _) == Kind.Write;

        // Legacy (Spanish) modes and verbs are accepted here too: callers may pass arguments that did not go through NormalizeArgs.
        private static string Norm(string s) => LegacyAliases.Token((s ?? "").Trim().ToLowerInvariant());

        /// <summary>
        /// Canonical, recursive arguments for fingerprints: keys sorted (ordinal), numbers normalized, no transport control
        /// fields. Same logical content = same text, whether it comes from Codex or Claude.
        /// </summary>
        public static string Canonical(object value)
        {
            var sb = new StringBuilder();
            WriteCanonical(sb, value, 0);
            return sb.ToString();
        }

        /// <summary>Fields that are not part of an operation's logical content (control/transport).</summary>
        public static readonly HashSet<string> ControlArgs = new HashSet<string>(StringComparer.Ordinal)
        {
            Wire.KeyArg, Wire.TaskArg, Wire.ToolArg, "reconcile", "deadline_ms", "wait", "wait_s", "page", "_client", "_bytes", "_trace",
        };

        private static void WriteCanonical(StringBuilder sb, object v, int depth)
        {
            if (depth > 64) throw new FormatException("arguments nested too deeply");
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: sb.Append(Json.Write(s)); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(((long)i).ToString(CultureInfo.InvariantCulture)); return;
                case double d:
                    if (Math.Abs(d - Math.Round(d)) < 1e-12 && Math.Abs(d) < 9e15) sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> o:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in o.Where(kv => depth > 0 || !ControlArgs.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(Json.Write(kv.Key)).Append(':');
                        WriteCanonical(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                case System.Collections.IEnumerable e:
                    sb.Append('[');
                    var f2 = true;
                    foreach (var item in e)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        WriteCanonical(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                default:
                    sb.Append(Json.Write(Convert.ToString(v, CultureInfo.InvariantCulture)));
                    return;
            }
        }
    }
}
