// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Backend simulado sobre <see cref="DurableSimModel"/>. Configuración de pruebas (persistida en el propio modelo, campo config):
    /// <c>rechazar</c>: [op] que el backend rechaza sin aplicar; <c>transitorio</c>: {op: n} rechazos transitorios en los n primeros intentos;
    /// <c>qc_desviaciones</c>: {op: n}; <c>retardo_ms</c>: espera por operación (contención de leases).
    /// </summary>
    public sealed class SimulatedBackend : IExecutionBackend
    {
        private readonly DurableSimModel _m;
        private readonly Faults _faults;

        public SimulatedBackend(DurableSimModel model, Faults faults = null) { _m = model; _faults = faults ?? new Faults(); }

        public string Name => "simulado";
        public bool TouchesRealModel => false;
        public string DocKey => _m.DocKey;
        public string DocPrint => _m.DocPrint;
        public DurableSimModel Model => _m;

        private bool ConfigList(string key, string op) => _m.Config.TryGetValue(key, out var v) && v is List<object> l && l.Any(x => Convert.ToString(x, CultureInfo.InvariantCulture) == op);
        private long ConfigMap(string key, string op) => _m.Config.TryGetValue(key, out var v) && v is Dictionary<string, object> d && d.TryGetValue(op, out var n) ? Convert.ToInt64(n, CultureInfo.InvariantCulture) : 0;

        public Preflight Prepare(ValidatedOp op)
        {
            switch (op.Operation)
            {
                case "consultar": return new Preflight { Targets = _m.CountCategory(op.Param("categoria")) };
                default: return new Preflight { Targets = _m.Targets(op.Scope).Count };
            }
        }

        public ExecResult Execute(ValidatedOp op, ExecContext ctx)
        {
            var delay = _m.Config.TryGetValue("retardo_ms", out var dv) ? Convert.ToInt32(dv, CultureInfo.InvariantCulture) : 0;
            if (delay > 0) Thread.Sleep(delay);
            if (ConfigList("rechazar", op.Id)) return ExecResult.Fail(ErrorClass.Rechazo, "el backend rechazó la operación (simulado); nada se aplicó");
            if (ctx.Attempt <= ConfigMap("transitorio", op.Id)) return ExecResult.Fail(ErrorClass.Transitorio, "rechazo transitorio (simulado, intento " + ctx.Attempt + "); nada se aplicó");
            switch (op.Operation)
            {
                case "consultar":
                    return new ExecResult { Ok = true, Data = { ["elementos"] = (long)_m.CountCategory(op.Param("categoria")) } };
                case "verificar_qc":
                    return new ExecResult { Ok = true, Data = { ["desviaciones"] = ConfigMap("qc_desviaciones", op.Id) } };
                case "fijar_parametro":
                case "fijar_parametro_lote":
                    return SetParameter(op, ctx);
                default:
                    return ExecResult.Fail(ErrorClass.Rechazo, "operación no soportada por el backend simulado: " + op.Operation);
            }
        }

        /// <summary>rc1: valor esperado por objetivo (mismo valor, o el del mapa de fijar_parametro_lote).</summary>
        private static Func<long, object> ValueFor(ValidatedOp op)
        {
            if (op.Operation == "fijar_parametro_lote")
            {
                var map = (IDictionary<string, object>)op.Params["valores"];
                return id => map.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var x) ? x : null;
            }
            var v0 = op.Params["valor"];
            return _ => v0;
        }

        private ExecResult SetParameter(ValidatedOp op, ExecContext ctx)
        {
            var p = op.Param("parametro");
            var vf = ValueFor(op);
            var targets = _m.Targets(op.Scope);
            if (targets.Count == 0)
            {
                if (!op.AllowEmpty) return ExecResult.Fail(ErrorClass.SinObjetivos, "el alcance no selecciona ningún elemento; nada se aplicó");
                return new ExecResult { Ok = true, Data = { ["elementos"] = 0L, ["cambiados"] = 0L, ["vacio"] = true } };
            }
            // "transacción": se preparan los cambios en memoria; sólo el Commit (una línea en el WAL con fsync) los confirma.
            var staged = new List<object[]>();
            foreach (var id in targets)
            {
                var v = vf(id);
                if (v == null) return ExecResult.Fail(ErrorClass.Precondicion, "fijar_parametro_lote: el objetivo " + id + " no tiene valor en el lote; nada se aplicó");
                var old = _m.Get(id, p);
                if (old != null && Values.Same(old, v)) continue;
                staged.Add(new object[] { id, p, old, v });
                if (_faults.Armed("backend.mitad_tx", op.Id) && staged.Count == _faults.Arg) _faults.Crash("backend.mitad_tx@" + op.Id + ":" + staged.Count);
            }
            var tx = _m.Commit(op.Id, ctx.PlanHash, ctx.Fencing, targets.Count, DurableSimModel.IdsHash(targets), staged);
            _faults.Hit("backend.tras_commit", op.Id);
            return new ExecResult { Ok = true, Data = { ["elementos"] = (long)targets.Count, ["cambiados"] = (long)staged.Count, ["tx"] = tx } };
        }

        public Observation Observe(ValidatedOp op, ExecContext ctx)
        {
            if (!op.Writes) return new Observation { Effect = Effect.NoObservado, Detail = "lectura: no deja efecto; repetirla es inocuo" };
            var (count, detailed) = _m.TxFor(op.Id, ctx.PlanHash);
            var targets = _m.Targets(op.Scope);
            if (count == 0)
                return targets.Count == 0
                    ? new Observation { Effect = Effect.SinObjetivos, Detail = "sin transacción y sin objetivos" }
                    : new Observation { Effect = Effect.NoObservado, Detail = "ninguna transacción confirmada de esta operación" };
            if (count > 1) return new Observation { Effect = Effect.Incierto, Transactions = count, Detail = "la operación tiene " + count + " transacciones confirmadas" };
            if (detailed.Count == 0)
                return new Observation { Effect = Effect.Observado, Transactions = 1, Detail = "transacción confirmada pero compactada: no hay detalle para reconstruir el resultado" };
            var r = detailed[0];
            var nowHash = DurableSimModel.IdsHash(targets);
            if (Arg.GetString(r, "objs_h") != nowHash)
                return new Observation { Effect = Effect.Parcial, Transactions = 1, Detail = "la transacción " + Arg.GetString(r, "tx") + " cubrió " + Arg.GetLong(r, "obj") + " objetivos; el alcance ahora selecciona " + targets.Count + " (otros elementos)" };
            var changes = r.TryGetValue("cambios", out var ch) && ch is List<object> cl ? cl.Count : 0;
            return new Observation
            {
                Effect = Effect.Observado, Transactions = 1, Detail = "transacción " + Arg.GetString(r, "tx"),
                Reconstructed = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["elementos"] = Arg.GetLong(r, "obj") ?? 0, ["cambiados"] = (long)changes, ["tx"] = Arg.GetString(r, "tx"), ["recuperada"] = true,
                },
            };
        }

        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx)
        {
            switch (op.Operation)
            {
                case "fijar_parametro":
                case "fijar_parametro_lote":
                {
                    var p = op.Param("parametro");
                    var vf = ValueFor(op);
                    var targets = _m.Targets(op.Scope);
                    var res = new PostcheckResult { Evidence = { ["objetivos"] = (long)targets.Count, ["objs_h"] = DurableSimModel.IdsHash(targets) } };
                    if (targets.Count == 0)
                    {
                        res.State = op.AllowEmpty ? PostState.Satisfechas : PostState.NoSatisfechas;
                        res.Detail = op.AllowEmpty ? "conjunto vacío permitido por el plan" : "el alcance no selecciona ningún elemento";
                        return res;
                    }
                    var ok = targets.Count(id => { var x = _m.Get(id, p); var v = vf(id); return x != null && v != null && Values.Same(x, v); });
                    res.Evidence["conformes"] = (long)ok;
                    res.State = ok == targets.Count ? PostState.Satisfechas : PostState.NoSatisfechas;
                    res.Detail = ok + "/" + targets.Count + " objetivos con " + p + " = valor esperado";
                    return res;
                }
                case "verificar_qc":
                {
                    var dev = ConfigMap("qc_desviaciones", op.Id);
                    return new PostcheckResult { State = dev == 0 ? PostState.Satisfechas : PostState.NoSatisfechas, Detail = "desviaciones = " + dev, Evidence = { ["desviaciones"] = dev } };
                }
                default:
                    return new PostcheckResult { State = PostState.Satisfechas, Detail = "sin postcondiciones aplicables", Evidence = { ["aplicables"] = 0L } };
            }
        }
    }
}
