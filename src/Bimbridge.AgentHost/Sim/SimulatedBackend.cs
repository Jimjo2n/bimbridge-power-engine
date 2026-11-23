// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Simulated backend over <see cref="DurableSimModel"/>. Test configuration (persisted in the model itself, config field):
    /// <c>rechazar</c>: [op] that the backend rejects without applying; <c>transitorio</c>: {op: n} transient rejections on the first n attempts;
    /// <c>qc_desviaciones</c>: {op: n}; <c>retardo_ms</c>: wait per operation (lease contention).
    /// </summary>
    public sealed class SimulatedBackend : IExecutionBackend
    {
        private readonly DurableSimModel _m;
        private readonly Faults _faults;

        public SimulatedBackend(DurableSimModel model, Faults faults = null) { _m = model; _faults = faults ?? new Faults(); }

        public string Name => "simulated";
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
                case "query_check": return new Preflight { Targets = _m.CountCategory(op.Param("category")) };
                default: return new Preflight { Targets = _m.Targets(op.Scope).Count };
            }
        }

        public ExecResult Execute(ValidatedOp op, ExecContext ctx)
        {
            var delay = _m.Config.TryGetValue("delay_ms", out var dv) ? Convert.ToInt32(dv, CultureInfo.InvariantCulture) : 0;
            if (delay > 0) Thread.Sleep(delay);
            if (ConfigList("reject", op.Id)) return ExecResult.Fail(ErrorClass.BackendRejection, "the backend rejected the operation (simulated); nothing was applied");
            if (ctx.Attempt <= ConfigMap("transient", op.Id)) return ExecResult.Fail(ErrorClass.Transient, "transient rejection (simulated, attempt " + ctx.Attempt + "); nothing was applied");
            switch (op.Operation)
            {
                case "query_check":
                    return new ExecResult { Ok = true, Data = { ["elements"] = (long)_m.CountCategory(op.Param("category")) } };
                case "verify_qc":
                    return new ExecResult { Ok = true, Data = { ["deviations"] = ConfigMap("qc_deviations", op.Id) } };
                case "set_parameter":
                case "set_parameter_batch":
                    return SetParameter(op, ctx);
                default:
                    return ExecResult.Fail(ErrorClass.BackendRejection, "operation not supported by the simulated backend: " + op.Operation);
            }
        }

        /// <summary>rc1: expected value per target (same value, or the one from the fijar_parametro_lote map).</summary>
        private static Func<long, object> ValueFor(ValidatedOp op)
        {
            if (op.Operation == "set_parameter_batch")
            {
                var map = (IDictionary<string, object>)op.Params["values"];
                return id => map.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var x) ? x : null;
            }
            var v0 = op.Params["value"];
            return _ => v0;
        }

        private ExecResult SetParameter(ValidatedOp op, ExecContext ctx)
        {
            var p = op.Param("parameter");
            var vf = ValueFor(op);
            var targets = _m.Targets(op.Scope);
            if (targets.Count == 0)
            {
                if (!op.AllowEmpty) return ExecResult.Fail(ErrorClass.NoTargets, "the scope selects no element; nothing was applied");
                return new ExecResult { Ok = true, Data = { ["elements"] = 0L, ["changed"] = 0L, ["empty_value"] = true } };
            }
            // "transaction": changes are staged in memory; only the Commit (one WAL line with fsync) commits them.
            var staged = new List<object[]>();
            foreach (var id in targets)
            {
                var v = vf(id);
                if (v == null) return ExecResult.Fail(ErrorClass.Precondition, "set_parameter_batch: target " + id + " has no value in the batch; nothing was applied");
                var old = _m.Get(id, p);
                if (old != null && Values.Same(old, v)) continue;
                staged.Add(new object[] { id, p, old, v });
                if (_faults.Armed("backend.mitad_tx", op.Id) && staged.Count == _faults.Arg) _faults.Crash("backend.mitad_tx@" + op.Id + ":" + staged.Count);
            }
            var tx = _m.Commit(op.Id, ctx.PlanHash, ctx.Fencing, targets.Count, DurableSimModel.IdsHash(targets), staged);
            _faults.Hit("backend.after_commit", op.Id);
            return new ExecResult { Ok = true, Data = { ["elements"] = (long)targets.Count, ["changed"] = (long)staged.Count, ["tx"] = tx } };
        }

        public Observation Observe(ValidatedOp op, ExecContext ctx)
        {
            if (!op.Writes) return new Observation { Effect = Effect.NotObserved, Detail = "read: leaves no effect; repeating it is harmless" };
            var (count, detailed) = _m.TxFor(op.Id, ctx.PlanHash);
            var targets = _m.Targets(op.Scope);
            if (count == 0)
                return targets.Count == 0
                    ? new Observation { Effect = Effect.NoTargets, Detail = "no transaction and no targets" }
                    : new Observation { Effect = Effect.NotObserved, Detail = "no committed transaction for this operation" };
            if (count > 1) return new Observation { Effect = Effect.Uncertain, Transactions = count, Detail = "the operation has " + count + " committed transactions" };
            if (detailed.Count == 0)
                return new Observation { Effect = Effect.Observed, Transactions = 1, Detail = "transaction committed but compacted: no detail to reconstruct the result" };
            var r = detailed[0];
            var nowHash = DurableSimModel.IdsHash(targets);
            if (Arg.GetString(r, "objs_h") != nowHash)
                return new Observation { Effect = Effect.Partial, Transactions = 1, Detail = "transaction " + Arg.GetString(r, "tx") + " covered " + Arg.GetLong(r, "obj") + " targets; the scope now selects " + targets.Count + " (other elements)" };
            var changes = r.TryGetValue("changes", out var ch) && ch is List<object> cl ? cl.Count : 0;
            return new Observation
            {
                Effect = Effect.Observed, Transactions = 1, Detail = "transaction " + Arg.GetString(r, "tx"),
                Reconstructed = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["elements"] = Arg.GetLong(r, "obj") ?? 0, ["changed"] = (long)changes, ["tx"] = Arg.GetString(r, "tx"), ["recovered"] = true,
                },
            };
        }

        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx)
        {
            switch (op.Operation)
            {
                case "set_parameter":
                case "set_parameter_batch":
                {
                    var p = op.Param("parameter");
                    var vf = ValueFor(op);
                    var targets = _m.Targets(op.Scope);
                    var res = new PostcheckResult { Evidence = { ["targets"] = (long)targets.Count, ["objs_h"] = DurableSimModel.IdsHash(targets) } };
                    if (targets.Count == 0)
                    {
                        res.State = op.AllowEmpty ? PostState.Satisfied : PostState.NotSatisfied;
                        res.Detail = op.AllowEmpty ? "empty set allowed by the plan" : "the scope selects no element";
                        return res;
                    }
                    var ok = targets.Count(id => { var x = _m.Get(id, p); var v = vf(id); return x != null && v != null && Values.Same(x, v); });
                    res.Evidence["conforming"] = (long)ok;
                    res.State = ok == targets.Count ? PostState.Satisfied : PostState.NotSatisfied;
                    res.Detail = ok + "/" + targets.Count + " targets with " + p + " = expected value";
                    return res;
                }
                case "verify_qc":
                {
                    var dev = ConfigMap("qc_deviations", op.Id);
                    return new PostcheckResult { State = dev == 0 ? PostState.Satisfied : PostState.NotSatisfied, Detail = "deviations = " + dev, Evidence = { ["deviations"] = dev } };
                }
                default:
                    return new PostcheckResult { State = PostState.Satisfied, Detail = "no applicable postconditions", Evidence = { ["applicable"] = 0L } };
            }
        }
    }
}
