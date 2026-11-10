// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Consultas compactas para Claude/Codex (P1 §6). Nunca devuelven el grafo completo: ids estables, recuentos, diferencias desde un
    /// seq del diario y, para el detalle, un archivo local cuyo camino se devuelve como referencia. El agente decide sobre excepciones;
    /// la contabilidad la hace este código.
    /// </summary>
    public static class Compact
    {
        public static string Label(ValidatedOp op)
        {
            var sb = new StringBuilder(op.Id).Append(' ').Append(op.Operation);
            switch (op.Operation)
            {
                case "fijar_parametro": sb.Append(' ').Append(op.Param("parametro")).Append('=').Append(op.Param("valor")); if (op.Param("unidades") != null) sb.Append(' ').Append(op.Param("unidades")); break;
                case "consultar": sb.Append(' ').Append(op.Param("categoria")); break;
                case "verificar_qc": sb.Append(' ').Append(op.Param("receta")); break;
            }
            var scope = string.Join("/", op.Scope.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Values.Show(kv.Value)));
            if (scope.Length > 0) sb.Append(" [").Append(scope).Append(']');
            if (op.Source?.Requisito != null) sb.Append(" ← ").Append(op.Source.Requisito).Append(op.Source.Pagina != null ? " p." + op.Source.Pagina : "");
            return sb.ToString();
        }

        public static Dictionary<string, int> Counts(ValidatedPlan plan, ExecJournal st) =>
            OpState.All.ToDictionary(s => s, s => st.Ops.Values.Count(r => r.State == s), StringComparer.Ordinal);

        /// <summary>Una línea: recuentos, seq del diario y la siguiente operación accionable.</summary>
        public static string Summary(ValidatedPlan plan, ExecJournal st)
        {
            var c = Counts(plan, st);
            var sb = new StringBuilder();
            sb.Append(c[OpState.Verificada]).Append('/').Append(plan.Ops.Count).Append(" verificadas");
            void Add(string s, string label) { if (c[s] > 0) sb.Append(" · ").Append(c[s]).Append(' ').Append(label); }
            Add(OpState.Ensayada, "ensayadas (revertidas, no aplicadas)");
            Add(OpState.EnEjecucion, "en curso");
            Add(OpState.Fallida, "fallidas");
            Add(OpState.RequiereVerificacion, "requieren verificación");
            Add(OpState.RequiereReconciliar, "requieren reconciliar");
            Add(OpState.Bloqueada, "bloqueadas");
            Add(OpState.Cancelada, "canceladas");
            var pend = c[OpState.Validada] + c[OpState.Ejecutada];
            if (pend > 0) sb.Append(" · ").Append(pend).Append(" pendientes");
            if (st.PlanState != PlanRunState.Activo) sb.Append(" · plan ").Append(st.PlanState);
            sb.Append(" · seq ").Append(st.Seq);
            var next = Next(plan, st);
            if (next != null) sb.Append(" · siguiente ").Append(next.Id);
            return sb.ToString();
        }

        /// <summary>Siguiente operación accionable: no final y con todas sus dependencias verificadas.</summary>
        public static ValidatedOp Next(ValidatedPlan plan, ExecJournal st) =>
            plan.Order.Select(id => plan.ById[id]).FirstOrDefault(o => !OpState.IsFinal(st.Ops[o.Id].State) && o.DependsOn.All(d => st.Ops[d].State == OpState.Verificada));

        public static string NextText(ValidatedPlan plan, ExecJournal st)
        {
            var n = Next(plan, st);
            if (n == null) return "ninguna";
            var r = st.Ops[n.Id];
            return Label(n) + " · " + r.State + (r.Attempt > 0 ? " intento " + r.Attempt : "");
        }

        /// <summary>Excepciones que requieren razonamiento (fallida, requiere_*, bloqueada), una por línea, con tope.</summary>
        public static string Exceptions(ValidatedPlan plan, ExecJournal st, int limit = 20, bool includeBlocked = false)
        {
            var list = plan.Order.Select(id => (Op: plan.ById[id], Rec: st.Ops[id]))
                .Where(x => OpState.Exceptions.Contains(x.Rec.State) && (includeBlocked || x.Rec.State != OpState.Bloqueada)).ToList();
            if (list.Count == 0) return "sin excepciones";
            var sb = new StringBuilder();
            foreach (var x in list.Take(limit))
                sb.Append(x.Op.Id).Append(' ').Append(x.Rec.State).Append(x.Rec.Error != null ? "(" + x.Rec.Error + ")" : "").Append(": ").Append(Trim(x.Rec.Note, 140)).Append('\n');
            if (list.Count > limit) sb.Append("+").Append(list.Count - limit).Append(" más\n");
            var blocked = st.Ops.Values.Count(r => r.State == OpState.Bloqueada);
            if (!includeBlocked && blocked > 0) sb.Append(blocked).Append(" bloqueadas por dependencias (consecuencia de las anteriores)\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Cambios desde un seq del diario: el estado más reciente de cada operación que cambió, y el seq actual para la próxima consulta.</summary>
        public static string ChangesSince(ValidatedPlan plan, ExecJournal st, long since, int limit = 50)
        {
            var latest = new Dictionary<string, (long Seq, string To, string Err)>(StringComparer.Ordinal);
            foreach (var c in st.Changes) if (c.Seq > since) latest[c.Op] = (c.Seq, c.To, c.Error);
            var sb = new StringBuilder("seq ").Append(st.Seq).Append(" · ").Append(latest.Count).Append(" operaciones cambiaron desde ").Append(since);
            foreach (var kv in latest.OrderBy(k => k.Value.Seq).Take(limit))
                sb.Append('\n').Append(kv.Key).Append(' ').Append(kv.Value.To).Append(kv.Value.Err != null && kv.Value.To != OpState.Verificada ? "(" + kv.Value.Err + ")" : "");
            if (latest.Count > limit) sb.Append("\n+").Append(latest.Count - limit).Append(" más");
            return sb.ToString();
        }

        public static string VerificationFailures(ValidatedPlan plan, ExecJournal st, int limit = 20)
        {
            var list = plan.Order.Where(id => st.Ops[id].Error == ErrorClass.Verificacion || st.Ops[id].Error == ErrorClass.EvidenciaInsuficiente)
                                 .Where(id => st.Ops[id].State != OpState.Verificada).ToList();
            if (list.Count == 0) return "sin fallos de verificación";
            var sb = new StringBuilder();
            foreach (var id in list.Take(limit)) sb.Append(id).Append(' ').Append(st.Ops[id].State).Append(": ").Append(Trim(st.Ops[id].Note, 140)).Append('\n');
            if (list.Count > limit) sb.Append('+').Append(list.Count - limit).Append(" más\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Informe final: escribe el detalle completo en informe.json (en la carpeta del plan) y devuelve un resumen + la referencia.
        /// Toda operación no verificada aparece en "sin_resolver" con su estado, clase de error, nota, dependencias y requisito de origen.
        /// </summary>
        public static Dictionary<string, object> Report(string dir, ValidatedPlan plan, ExecJournal st)
        {
            var ops = plan.Order.Select(id =>
            {
                var o = plan.ById[id]; var r = st.Ops[id];
                var d = r.ToJson();
                d["id"] = id; d["operacion"] = o.Operation; d["etiqueta"] = Label(o);
                d["depende_de"] = o.DependsOn.Cast<object>().ToList();
                if (o.Source != null) d["fuente"] = o.Source.ToJson();
                d["autorizacion_derivada"] = o.Authorization.Cast<object>().ToList();
                return d;
            }).ToList();
            var unresolved = ops.Where(d => (string)d["estado"] != OpState.Verificada && (string)d["estado"] != OpState.Ensayada).ToList();
            var full = new Dictionary<string, object>
            {
                ["plan"] = plan.Hash, ["objetivo"] = plan.Objective, ["documento"] = new Dictionary<string, object> { ["clave"] = plan.DocKey, ["huella"] = plan.DocPrint },
                ["seq"] = st.Seq, ["plan_estado"] = st.PlanState, ["t"] = Home.Now(),
                ["por_estado"] = Counts(plan, st).Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => (object)(long)kv.Value),
                ["por_error"] = st.Ops.Values.Where(r => r.Error != null && r.State != OpState.Verificada).GroupBy(r => r.Error).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["sin_resolver"] = unresolved.Cast<object>().ToList(),
                ["operaciones"] = ops.Cast<object>().ToList(),
            };
            var path = Path.Combine(dir, "informe.json");
            Home.WriteAtomic(path, Json.Write(full));
            return new Dictionary<string, object>
            {
                ["resumen"] = Summary(plan, st), ["sin_resolver"] = (long)unresolved.Count,
                ["por_estado"] = full["por_estado"], ["por_error"] = full["por_error"], ["detalle"] = path,
                ["sha256_detalle"] = Home.FileSha256(path),
            };
        }

        private static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
