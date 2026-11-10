// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Engine.Workflow;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// 0.8.0 (CAP-004 / WF-13 / WF-14): checkpoint estructurado y paquete de traspaso. Se construye a partir del diario existente
    /// (no es un segundo sistema de persistencia) y dice lo justo para que otro agente retome sin el historial de la conversación:
    /// identidad, plan, qué está confirmado, qué es incierto, qué se verificó, qué artefactos hay y cuál es el siguiente paso SEGURO.
    /// Un paso incierto nunca se propone como "repetir": se propone "reconciliar observando".
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
                    case OpState.Verificada: case OpState.Ensayada: confirmed.Add(row); break;
                    case OpState.RequiereVerificacion: case OpState.RequiereReconciliar: case OpState.EnEjecucion: case OpState.Ejecutada:
                        uncertain.Add(row); break;
                    case OpState.Fallida: case OpState.Bloqueada: case OpState.Cancelada: failed.Add(row); break;
                    default: pending.Add(row); break;
                }
            }

            string next;
            if (uncertain.Count > 0)
                next = "reconciliar " + ((Dictionary<string, object>)uncertain[0])["op"] + " leyendo el modelo; NO repetir la escritura";
            else if (failed.Count > 0 && pending.Count == 0)
                next = "revisar fallos (" + failed.Count + ") con una persona antes de seguir";
            else if (pending.Count > 0)
                next = "continuar con " + ((Dictionary<string, object>)pending[0])["op"] + " (mismo diario, mismo plan)";
            else if (delivery != null && !delivery.Complete)
                next = "entrega: falta el hito " + delivery.Next;
            else next = "nada: plan terminado";

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
