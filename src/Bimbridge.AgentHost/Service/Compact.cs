// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Compact queries for Claude/Codex (P1 §6). They never return the full graph: stable ids, counts, differences since a
    /// journal seq and, for the detail, a local file whose path is returned as a reference. The agent decides on exceptions;
    /// this code does the bookkeeping.
    /// </summary>
    public static class Compact
    {
        public static string Label(ValidatedOp op)
        {
            var sb = new StringBuilder(op.Id).Append(' ').Append(op.Operation);
            switch (op.Operation)
            {
                case "set_parameter": sb.Append(' ').Append(op.Param("parameter")).Append('=').Append(op.Param("value")); if (op.Param("units") != null) sb.Append(' ').Append(op.Param("units")); break;
                case "query_check": sb.Append(' ').Append(op.Param("category")); break;
                case "verify_qc": sb.Append(' ').Append(op.Param("recipe")); break;
            }
            var scope = string.Join("/", op.Scope.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Values.Show(kv.Value)));
            if (scope.Length > 0) sb.Append(" [").Append(scope).Append(']');
            if (op.Source?.Requirement != null) sb.Append(" ← ").Append(op.Source.Requirement).Append(op.Source.Page != null ? " p." + op.Source.Page : "");
            return sb.ToString();
        }

        public static Dictionary<string, int> Counts(ValidatedPlan plan, ExecJournal st) =>
            OpState.All.ToDictionary(s => s, s => st.Ops.Values.Count(r => r.State == s), StringComparer.Ordinal);

        /// <summary>One line: counts, journal seq and the next actionable operation.</summary>
        public static string Summary(ValidatedPlan plan, ExecJournal st)
        {
            var c = Counts(plan, st);
            var sb = new StringBuilder();
            sb.Append(c[OpState.Verified]).Append('/').Append(plan.Ops.Count).Append(" verified");
            void Add(string s, string label) { if (c[s] > 0) sb.Append(" · ").Append(c[s]).Append(' ').Append(label); }
            Add(OpState.Trialed, "trialed (reverted, not applied)");
            Add(OpState.Running, "in progress");
            Add(OpState.Failed, "failed");
            Add(OpState.NeedsVerification, "need verification");
            Add(OpState.NeedsReconciliation, "need reconciliation");
            Add(OpState.Blocked, "blocked");
            Add(OpState.Cancelled, "cancelled");
            var pend = c[OpState.Validated] + c[OpState.Executed];
            if (pend > 0) sb.Append(" · ").Append(pend).Append(" pending");
            if (st.PlanState != PlanRunState.Active) sb.Append(" · plan ").Append(st.PlanState);
            sb.Append(" · seq ").Append(st.Seq);
            var next = Next(plan, st);
            if (next != null) sb.Append(" · next ").Append(next.Id);
            return sb.ToString();
        }

        /// <summary>Next actionable operation: not final and with all its dependencies verified.</summary>
        public static ValidatedOp Next(ValidatedPlan plan, ExecJournal st) =>
            plan.Order.Select(id => plan.ById[id]).FirstOrDefault(o => !OpState.IsFinal(st.Ops[o.Id].State) && o.DependsOn.All(d => st.Ops[d].State == OpState.Verified));

        public static string NextText(ValidatedPlan plan, ExecJournal st)
        {
            var n = Next(plan, st);
            if (n == null) return "none";
            var r = st.Ops[n.Id];
            return Label(n) + " · " + r.State + (r.Attempt > 0 ? " attempt " + r.Attempt : "");
        }

        /// <summary>Exceptions that need reasoning (fallida, requiere_*, bloqueada), one per line, capped.</summary>
        public static string Exceptions(ValidatedPlan plan, ExecJournal st, int limit = 20, bool includeBlocked = false)
        {
            var list = plan.Order.Select(id => (Op: plan.ById[id], Rec: st.Ops[id]))
                .Where(x => OpState.Exceptions.Contains(x.Rec.State) && (includeBlocked || x.Rec.State != OpState.Blocked)).ToList();
            if (list.Count == 0) return "no exceptions";
            var sb = new StringBuilder();
            foreach (var x in list.Take(limit))
                sb.Append(x.Op.Id).Append(' ').Append(x.Rec.State).Append(x.Rec.Error != null ? "(" + x.Rec.Error + ")" : "").Append(": ").Append(Trim(x.Rec.Note, 140)).Append('\n');
            if (list.Count > limit) sb.Append("+").Append(list.Count - limit).Append(" more\n");
            var blocked = st.Ops.Values.Count(r => r.State == OpState.Blocked);
            if (!includeBlocked && blocked > 0) sb.Append(blocked).Append(" blocked by dependencies (a consequence of the above)\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Changes since a journal seq: the latest state of each operation that changed, and the current seq for the next query.</summary>
        public static string ChangesSince(ValidatedPlan plan, ExecJournal st, long since, int limit = 50)
        {
            var latest = new Dictionary<string, (long Seq, string To, string Err)>(StringComparer.Ordinal);
            foreach (var c in st.Changes) if (c.Seq > since) latest[c.Op] = (c.Seq, c.To, c.Error);
            var sb = new StringBuilder("seq ").Append(st.Seq).Append(" · ").Append(latest.Count).Append(" operations changed since ").Append(since);
            foreach (var kv in latest.OrderBy(k => k.Value.Seq).Take(limit))
                sb.Append('\n').Append(kv.Key).Append(' ').Append(kv.Value.To).Append(kv.Value.Err != null && kv.Value.To != OpState.Verified ? "(" + kv.Value.Err + ")" : "");
            if (latest.Count > limit) sb.Append("\n+").Append(latest.Count - limit).Append(" more");
            return sb.ToString();
        }

        public static string VerificationFailures(ValidatedPlan plan, ExecJournal st, int limit = 20)
        {
            var list = plan.Order.Where(id => st.Ops[id].Error == ErrorClass.Verification || st.Ops[id].Error == ErrorClass.InsufficientEvidence)
                                 .Where(id => st.Ops[id].State != OpState.Verified).ToList();
            if (list.Count == 0) return "no verification failures";
            var sb = new StringBuilder();
            foreach (var id in list.Take(limit)) sb.Append(id).Append(' ').Append(st.Ops[id].State).Append(": ").Append(Trim(st.Ops[id].Note, 140)).Append('\n');
            if (list.Count > limit) sb.Append('+').Append(list.Count - limit).Append(" more\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Final report: writes the full detail to report.json (in the plan folder) and returns a summary + the reference.
        /// Every unverified operation appears in "unresolved" with its state, error class, note, dependencies and source requirement.
        /// </summary>
        public static Dictionary<string, object> Report(string dir, ValidatedPlan plan, ExecJournal st)
        {
            var ops = plan.Order.Select(id =>
            {
                var o = plan.ById[id]; var r = st.Ops[id];
                var d = r.ToJson();
                d["id"] = id; d["operation"] = o.Operation; d["tag"] = Label(o);
                d["depends_on"] = o.DependsOn.Cast<object>().ToList();
                if (o.Source != null) d["source"] = o.Source.ToJson();
                d["derived_authorization"] = o.Authorization.Cast<object>().ToList();
                return d;
            }).ToList();
            var unresolved = ops.Where(d => (string)d["status"] != OpState.Verified && (string)d["status"] != OpState.Trialed).ToList();
            var full = new Dictionary<string, object>
            {
                ["plan"] = plan.Hash, ["target"] = plan.Objective, ["document"] = new Dictionary<string, object> { ["key"] = plan.DocKey, ["fingerprint"] = plan.DocPrint },
                ["seq"] = st.Seq, ["plan_status"] = st.PlanState, ["t"] = Home.Now(),
                ["by_status"] = Counts(plan, st).Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => (object)(long)kv.Value),
                ["by_error"] = st.Ops.Values.Where(r => r.Error != null && r.State != OpState.Verified).GroupBy(r => r.Error).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["unresolved"] = unresolved.Cast<object>().ToList(),
                ["operations"] = ops.Cast<object>().ToList(),
            };
            var path = Path.Combine(dir, "report.json");
            Home.WriteAtomic(path, Json.Write(full));
            return new Dictionary<string, object>
            {
                ["summary"] = Summary(plan, st), ["unresolved"] = (long)unresolved.Count,
                ["by_status"] = full["by_status"], ["by_error"] = full["by_error"], ["detail"] = path,
                ["detail_sha256"] = Home.FileSha256(path),
            };
        }

        private static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
