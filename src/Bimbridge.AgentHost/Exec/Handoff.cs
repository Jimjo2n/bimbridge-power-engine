// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine.Workflow;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// 0.8.0 (CAP-004 / WF-13 / WF-14): structured checkpoint and handoff package. It is built from the existing journal
    /// (not a second persistence system) and says just enough for another agent to resume without the conversation history:
    /// identity, plan, what is committed, what is uncertain, what was verified, which artifacts exist and the next SAFE step.
    /// An uncertain step is never proposed as "repeat": it is proposed as "reconcile by observing".
    /// </summary>
    public static class Handoff
    {
        public const string Schema = "bimbridge.handoff/1";

        public static Dictionary<string, object> Build(PlanRunner runner, RequirementSet requirements = null, DeliveryTrack delivery = null,
                                                       string instance = null, string docRevision = null)
        {
            var plan = runner.Plan;
            var st = runner.State;
            var confirmed = new List<object>();
            var uncertain = new List<object>();
            var failed = new List<object>();
            var pending = new List<object>();
            foreach (var id in plan.Order)
            {
                var rec = st.Ops[id];
                var op = plan.ById[id];
                var row = new Dictionary<string, object> { ["op"] = id, ["operacion"] = op.Operation, ["estado"] = rec.State };
                if (rec.Note != null) row["nota"] = Trim(rec.Note, 160);
                switch (rec.State)
                {
                    case OpState.Verified: case OpState.Trialed: confirmed.Add(row); break;
                    case OpState.NeedsVerification: case OpState.NeedsReconciliation: case OpState.Running: case OpState.Executed:
                        uncertain.Add(row); break;
                    case OpState.Failed: case OpState.Blocked: case OpState.Cancelled: failed.Add(row); break;
                    default: pending.Add(row); break;
                }
            }

            string next;
            if (uncertain.Count > 0)
                next = "reconcile " + ((Dictionary<string, object>)uncertain[0])["op"] + " by reading the model; do NOT repeat the write";
            else if (failed.Count > 0 && pending.Count == 0)
                next = "review failures (" + failed.Count + ") with a person before continuing";
            else if (pending.Count > 0)
                next = "continue with " + ((Dictionary<string, object>)pending[0])["op"] + " (same journal, same plan)";
            else if (delivery != null && !delivery.Complete)
                next = "delivery: milestone missing " + delivery.Next;
            else next = "nothing: plan finished";

            var d = new Dictionary<string, object>
            {
                ["esquema"] = Schema,
                ["creado"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["modelo"] = new Dictionary<string, object> { ["clave"] = plan.DocKey, ["huella"] = plan.DocPrint, ["revision"] = docRevision, ["instancia"] = instance },
                ["plan"] = new Dictionary<string, object> { ["hash"] = plan.Hash, ["objetivo"] = plan.Objective, ["operaciones"] = (long)plan.Ops.Count, ["estado"] = st.PlanState, ["fencing"] = st.Fencing, ["seq"] = st.Seq },
                ["confirmadas"] = confirmed,
                ["inciertas"] = uncertain,
                ["fallidas"] = failed,
                ["pendientes"] = pending,
                ["siguiente_paso_seguro"] = next,
                ["diario"] = st.JournalPath,
            };
            if (plan.Sources.Count > 0) d["fuentes"] = plan.Sources.Select(s => (object)s.ToJson()).ToList();
            if (requirements != null)
            {
                var (conflicts, missing) = requirements.Audit();
                d["requisitos"] = new Dictionary<string, object>
                {
                    ["total"] = (long)requirements.All.Count,
                    ["conflictos"] = conflicts.Select(c => (object)c.ToJson()).ToList(),
                    ["datos_sin_fuente"] = missing.Select(m => (object)(m.Id + " " + m.Key + "@" + m.Scope)).ToList(),
                };
            }
            if (delivery != null) d["entrega"] = delivery.ToJson();
            return d;
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
