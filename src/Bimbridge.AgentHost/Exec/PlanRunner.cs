// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.Engine;
using Bimbridge.Engine.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    public sealed class RunnerOptions
    {
        public Faults Faults;
        public TimeSpan? LeaseTtl;
        public int CheckpointEvery = 64;
        /// <summary>Open for inspection only (no lease, writes nothing).</summary>
        public bool InspectOnly;
        /// <summary>alpha3: writes against a real backend. null = blocked (alpha1). "ensayo" or "persistente": chosen by the OPERATOR
        /// when launching the AgentHost (never by the plan) and must match the backend mode.</summary>
        public string RealWrites;
    }

    /// <summary>plan.json: the validated plan and its hash. A state folder belongs to a single plan.</summary>
    public static class PlanFile
    {
        public static string PathIn(string dir) => Path.Combine(dir, "plan.json");

        public static void Write(string dir, ValidatedPlan plan) =>
            Home.WriteAtomic(PathIn(dir), Json.Write(new Dictionary<string, object> { ["hash"] = plan.Hash, ["plan"] = Json.Parse(plan.CanonicalJson) }));

        /// <summary>Reads and revalidates the saved plan. null + diagnostic if it is unreliable.</summary>
        public static ValidatedPlan Read(string dir, List<Diagnostic> diags)
        {
            var path = PathIn(dir);
            if (!File.Exists(path)) { diags.Add(Diagnostic.Error("plan_ausente", path)); return null; }
            Dictionary<string, object> o;
            try { o = Json.ParseObject(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is FormatException || ex is IOException) { diags.Add(Diagnostic.Error("plan_ilegible", ex.Message)); return null; }
            if (!(o.TryGetValue("plan", out var p) && p is Dictionary<string, object> pd)) { diags.Add(Diagnostic.Error("plan_ilegible", "no plan field")); return null; }
            var errs = IrValidator.Validate(PlanDraft.FromJson(pd), out var vp);
            if (errs.Count > 0) { diags.Add(Diagnostic.Error("plan_guardado_invalido", string.Join("; ", errs.Take(5)))); return null; }
            if (Arg.GetString(o, "hash") != vp.Hash) { diags.Add(Diagnostic.Error("plan_alterado", "saved hash " + Arg.GetString(o, "hash") + " ≠ content " + vp.Hash)); return null; }
            return vp;
        }
    }

    /// <summary>
    /// Plan runner with durable state and verified recovery (AH-01…AH-05). Deterministic: follows the plan's topological order;
    /// it does not reason or choose: anything that needs judgment is left in an exception state (fallida, requiere_verificacion, requiere_reconciliar,
    /// bloqueada) for a person or an agent.
    ///
    /// Per-operation protocol: [preconditions, empty set] → lease.Check → journal: en_ejecucion (attempt n) → backend.Execute →
    /// journal: ejecutada (result) → postconditions against the model + expected result → journal: verificada (evidence).
    /// Recovery of en_ejecucion: the model is OBSERVED (effect observed / not observed / partial / uncertain / no targets) and it is only
    /// run again if the effect does not exist; an observed effect always goes through verification; without enough evidence it stays in
    /// requiere_verificacion, never in verificada.
    /// </summary>
    public sealed class PlanRunner : IDisposable
    {
        private readonly string _dir;
        private readonly IExecutionBackend _b;
        private readonly Faults _faults;
        private PlanLease _lease;
        public ValidatedPlan Plan { get; private set; }
        public ExecJournal State { get; private set; }
        public bool IsOwner => _lease != null && !_lease.Released;
        public TaskStore.Lease Holder { get; private set; }
        public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();
        public string Dir => _dir;

        private PlanRunner(string dir, IExecutionBackend b, Faults f) { _dir = dir; _b = b; _faults = f ?? new Faults(); }

        /// <summary>
        /// Opens (or resumes) the run of a plan in <paramref name="dir"/>. Returns null if it is not safe to continue (reasons in
        /// <paramref name="diags"/>). If another process is the owner, returns an inspection-only runner (IsOwner=false) with a
        /// plan_en_uso diagnostic.
        /// </summary>
        public static PlanRunner Open(string dir, PlanDraft draft, IExecutionBackend backend, RunnerOptions opt, out List<Diagnostic> diags)
        {
            opt = opt ?? new RunnerOptions();
            var r = new PlanRunner(dir, backend, opt.Faults);
            diags = r.Diagnostics;
            Directory.CreateDirectory(dir);

            // 1. plan
            ValidatedPlan given = null;
            if (draft != null)
            {
                var errs = IrValidator.Validate(draft, out given);
                if (errs.Count > 0) { foreach (var e in errs) diags.Add(Diagnostic.Error("plan_invalido", e)); return null; }
            }
            if (File.Exists(PlanFile.PathIn(dir)))
            {
                var saved = PlanFile.Read(dir, diags);
                if (saved == null) return null;
                if (given != null && given.Hash != saved.Hash) { diags.Add(Diagnostic.Error("plan_distinto", "the folder belongs to plan " + saved.Hash + "; the one received is " + given.Hash)); return null; }
                r.Plan = saved;
            }
            else
            {
                if (given == null) { diags.Add(Diagnostic.Error("plan_ausente", "no plan.json and no plan received")); return null; }
                if (!opt.InspectOnly) PlanFile.Write(dir, given);
                r.Plan = given;
            }

            // 2. document identity and write policy
            if (backend != null)
            {
                if (backend.DocKey != r.Plan.DocKey || backend.DocPrint != r.Plan.DocPrint)
                { diags.Add(Diagnostic.Error(ErrorClass.DifferentDocument, "the plan is for " + r.Plan.DocKey + " [" + r.Plan.DocPrint + "]; the backend has " + backend.DocKey + " [" + backend.DocPrint + "]")); return null; }
                if (backend.TouchesRealModel && r.Plan.Ops.Any(o => o.Writes) && (opt.RealWrites == null || opt.RealWrites != backend.WriteMode))
                { diags.Add(Diagnostic.Error("escrituras_no_permitidas", "the plan writes to a real model and the operator did not authorize that mode (authorized: " + (opt.RealWrites ?? "none") + ", backend: " + backend.WriteMode + ")")); return null; }
            }

            // 3. ownership
            if (!opt.InspectOnly)
            {
                try { r._lease = PlanLease.TryAcquire(dir, opt.LeaseTtl, out var h); r.Holder = h; }
                catch (TaskStore.LeaseStoreException ex) { diags.Add(Diagnostic.Error("lease_no_fiable", ex.Message)); return null; }
                if (r._lease == null) diags.Add(Diagnostic.Warn(ErrorClass.PlanInUse, "owner " + r.Holder?.Owner + " (fencing " + r.Holder?.Fencing + ", expires " + r.Holder?.Expires + "); inspection only"));
            }

            // 4. state
            r.State = ExecJournal.Recover(dir, r.Plan, r.IsOwner, r._faults, diags);
            if (r.State == null) { r._lease?.Release(); return null; }
            r.State.CheckpointEvery = Math.Max(1, opt.CheckpointEvery);
            if (r.IsOwner)
            {
                if (r._lease.Fencing <= r.State.Fencing)
                { diags.Add(Diagnostic.Error("lease_reiniciado", "lease fencing " + r._lease.Fencing + " ≤ journal fencing " + r.State.Fencing + "; the lease store was lost or altered")); r._lease.Release(); return null; }
                r.State.RecordOwner(r._lease.Fencing, r._lease.Owner);
            }
            return r;
        }

        private ExecContext Ctx(int attempt) => new ExecContext { PlanHash = Plan.Hash, Fencing = _lease?.Fencing ?? 0, Attempt = attempt };

        private void RequireOwner()
        {
            if (!IsOwner) throw new InvalidOperationException(ErrorClass.PlanInUse + ": this process does not own the plan");
        }

        public void Pause() { RequireOwner(); if (State.PlanState == PlanRunState.Active) { State.PlanAction("pausar"); State.Checkpoint(); } }
        public void Resume() { RequireOwner(); if (State.PlanState == PlanRunState.Paused) { State.PlanAction("reanudar"); State.Checkpoint(); } }

        /// <summary>Cancels: operations not yet started become cancelada; those left half done are resolved (recovery) on the next run.</summary>
        public void Cancel()
        {
            RequireOwner();
            if (State.PlanState != PlanRunState.Cancelled) State.PlanAction("cancelar");
            foreach (var id in Plan.Order)
                if (State.Ops[id].State == OpState.Validated) State.Transition(id, OpState.Cancelled, error: ErrorClass.Cancellation, note: "plan cancelled");
            State.Checkpoint();
        }

        /// <summary>
        /// Runs or resumes until the end, token cancellation (stops BETWEEN operations; the operation in progress finishes its
        /// protocol) or loss of the lease. Returns the one-line summary.
        /// </summary>
        public string Run(CancellationToken ct = default)
        {
            RequireOwner();
            if (State.PlanState == PlanRunState.Paused) return Compact.Summary(Plan, State) + " · pausado";
            var cancelled = State.PlanState == PlanRunState.Cancelled;
            try
            {
                // AUD-04 (#28): explicit renewal after initialization (acquisition → journal recovery → model open).
                // If that phase took longer than the TTL, the lease has already expired: Check detects it and the process fails closed (lease_perdido, code 4)
                // BEFORE any transition or write; it never silently re-acquires. If it has not expired, it renews it.
                _faults.Delay("runner.antes_de_ejecutar");
                _lease.Check();
                foreach (var id in Plan.Order)
                {
                    if (ct.IsCancellationRequested) break;
                    var rec = State.Ops[id];
                    if (OpState.IsFinal(rec.State)) continue;
                    if (cancelled && rec.State == OpState.Validated) { State.Transition(id, OpState.Cancelled, error: ErrorClass.Cancellation, note: "plan cancelled"); continue; }
                    _faults.Hit("runner.entre_operaciones");
                    Step(Plan.ById[id]);
                }
            }
            catch (DurableSimModel.StaleFencingException ex) { _lease?.Release(); throw new PlanLease.LeaseLostException(ex.Message); }
            catch (ChainLog.ConcurrentWriterException ex) { _lease?.Release(); throw new PlanLease.LeaseLostException(ex.Message); }
            State.Checkpoint();
            return Compact.Summary(Plan, State);
        }

        private void Step(ValidatedOp op)
        {
            var id = op.Id;
            for (var guard = 0; guard < 16; guard++)
            {
                var rec = State.Ops[id];
                switch (rec.State)
                {
                    case OpState.Validated:
                        if (!ExecuteStep(op)) return;
                        break;
                    case OpState.Running:
                        Recover(op);
                        break;
                    case OpState.Executed:
                        VerifyStep(op);
                        break;
                    default:
                        return; // final
                }
            }
            throw new InvalidOperationException("operation " + id + " does not converge (state " + State.Ops[id].State + ")");
        }

        /// <summary>validada → … Returns false if the operation cannot advance now (dependency not yet resolved).</summary>
        private bool ExecuteStep(ValidatedOp op)
        {
            var id = op.Id;
            foreach (var d in op.DependsOn)
            {
                var ds = State.Ops[d].State;
                if (ds == OpState.Verified) continue;
                if (OpState.IsFinal(ds)) { State.Transition(id, OpState.Blocked, error: ErrorClass.Dependency, note: "dependency " + d + " in " + ds); return true; }
                return false;
            }
            var pre = _b.Prepare(op);
            if (pre.Error != null) { State.Transition(id, OpState.Failed, error: ErrorClass.Precondition, note: pre.Error); return true; }
            foreach (var pc in op.Preconditions)
                if ((pc.Min != null && pre.Targets < pc.Min) || (pc.Max != null && pre.Targets > pc.Max))
                { State.Transition(id, OpState.Failed, error: ErrorClass.Precondition, note: "targets = " + pre.Targets + " outside " + pc + "; nothing was written"); return true; }
            if (op.Writes && pre.Targets == 0 && !op.AllowEmpty)
            { State.Transition(id, OpState.Failed, error: ErrorClass.NoTargets, note: "the scope selects no elements; nothing was written (permitir_vacio=false)"); return true; }

            _lease.Check();
            var attempt = State.Ops[id].Attempt + 1;
            State.Transition(id, OpState.Running, attempt: attempt);
            ExecResult r;
            try { r = _b.Execute(op, Ctx(attempt)); }
            catch (UncertainEffectException ex)
            {
                // unknown whether it was committed: never retry blindly; observe the model right now
                State.Note(id, "efecto_incierto", new Dictionary<string, object> { ["detalle"] = ex.Message });
                Recover(op);
                return true;
            }
            _faults.Hit("runner.tras_ejecutar", id);
            if (!r.Ok)
            {
                if (r.ErrorClass == ErrorClass.Transient && attempt <= op.RetryMax)
                { State.Transition(id, OpState.Validated, error: ErrorClass.Transient, note: "retry " + attempt + "/" + op.RetryMax + ": " + r.Error); return true; }
                State.Transition(id, OpState.Failed, error: r.ErrorClass ?? ErrorClass.BackendRejection, note: r.Error);
                return true;
            }
            State.Transition(id, OpState.Executed, result: r.Data);
            return true;
        }

        /// <summary>en_ejecucion after a crash: decide by what is OBSERVED in the model, never by assumption.</summary>
        private void Recover(ValidatedOp op)
        {
            var id = op.Id;
            var rec = State.Ops[id];
            var obs = _b.Observe(op, Ctx(rec.Attempt));
            State.Note(id, "recuperacion", new Dictionary<string, object> { ["efecto"] = JournalTokens.Of(obs.Effect), ["transacciones"] = obs.Transactions, ["detalle"] = obs.Detail });
            switch (obs.Effect)
            {
                case Effect.NotObserved:
                    State.Transition(id, OpState.Validated, note: "recovery: effect not observed (" + obs.Detail + "); will run without duplicating");
                    return;
                case Effect.NoTargets:
                    if (op.AllowEmpty) State.Transition(id, OpState.Executed, result: new Dictionary<string, object> { ["elementos"] = 0L, ["cambiados"] = 0L, ["vacio"] = true, ["recuperada"] = true });
                    else State.Transition(id, OpState.Failed, error: ErrorClass.NoTargets, note: "recovery: no effect and the scope selects no elements");
                    return;
                case Effect.Partial:
                    State.Transition(id, OpState.NeedsReconciliation, error: ErrorClass.PartialEffect, note: "recovery: " + obs.Detail + "; not repeated");
                    return;
                case Effect.Uncertain:
                    State.Transition(id, OpState.NeedsReconciliation, error: ErrorClass.UncertainEffect, note: "recovery: " + obs.Detail + "; not repeated");
                    return;
                case Effect.Observed:
                    if (obs.Reconstructed != null) { State.Transition(id, OpState.Executed, result: obs.Reconstructed, note: "recovery: " + obs.Detail); return; }
                    // the effect is proven but the result cannot be reconstructed: not repeated and no result is invented
                    var post = _b.CheckPostconditions(op, Ctx(rec.Attempt));
                    var evid = Evidence(post);
                    if (post.State == PostState.NotSatisfied)
                    { State.Transition(id, OpState.Failed, error: ErrorClass.Verification, evidence: evid, note: "recovery: effect observed without evidence and postcondition NOT satisfied: " + post.Detail); return; }
                    if (post.State == PostState.NotEvaluable || op.Expected.Count > 0)
                    {
                        State.Transition(id, OpState.NeedsVerification, error: ErrorClass.InsufficientEvidence, evidence: evid,
                            note: "recovery: " + obs.Detail + "; postconditions " + JournalTokens.Of(post.State) + (op.Expected.Count > 0 ? "; resultado_esperado (" + string.Join(",", op.Expected.Keys) + ") not checkable" : "") + "; not repeated");
                        return;
                    }
                    // no expected result: verification is state-only; the result makes explicit that there is no run data
                    State.Transition(id, OpState.Executed, result: new Dictionary<string, object> { ["recuperada"] = true, ["sin_datos_de_ejecucion"] = true }, note: "recovery: " + obs.Detail);
                    return;
            }
        }

        private static Dictionary<string, object> Evidence(PostcheckResult post)
        {
            var e = new Dictionary<string, object>(post.Evidence, StringComparer.Ordinal) { ["postcondiciones"] = JournalTokens.Of(post.State) };
            if (post.Detail != null) e["detalle"] = post.Detail;
            return e;
        }

        private void VerifyStep(ValidatedOp op)
        {
            var id = op.Id;
            var rec = State.Ops[id];
            _faults.Hit("runner.en_verificacion", id);
            var post = _b.CheckPostconditions(op, Ctx(rec.Attempt));
            var evid = Evidence(post);
            var trial = rec.Result != null && rec.Result.TryGetValue("ensayo", out var tv) && tv is bool tb && tb;
            if (trial)
            {
                // trial: the postcondition is "applied and verified temporarily, and the model returned to its initial state".
                if (post.State == PostState.Satisfied) { _faults.Hit("runner.tras_verificar", id); State.Transition(id, OpState.Trialed, evidence: evid); }
                else State.Transition(id, OpState.NeedsReconciliation, error: ErrorClass.UncertainEffect, evidence: evid, note: "trial without proven restoration: " + post.Detail);
                return;
            }
            if (post.State == PostState.NotSatisfied) { State.Transition(id, OpState.Failed, error: ErrorClass.Verification, evidence: evid, note: "postcondition not satisfied: " + post.Detail); return; }
            if (post.State == PostState.NotEvaluable) { State.Transition(id, OpState.NeedsVerification, error: ErrorClass.InsufficientEvidence, evidence: evid, note: "postconditions not evaluable: " + post.Detail); return; }
            var checkedKeys = new List<object>();
            foreach (var kv in op.Expected)
            {
                object got = null;
                if (rec.Result == null || !rec.Result.TryGetValue(kv.Key, out got))
                { State.Transition(id, OpState.NeedsVerification, error: ErrorClass.InsufficientEvidence, evidence: evid, note: "the result does not contain '" + kv.Key + "'; not certified"); return; }
                if (!Values.Same(kv.Value, got))
                { evid["esperado"] = kv.Key; State.Transition(id, OpState.Failed, error: ErrorClass.Verification, evidence: evid, note: kv.Key + ": expected " + Values.Show(kv.Value) + ", got " + Values.Show(got)); return; }
                checkedKeys.Add(kv.Key);
            }
            evid["esperado_comprobado"] = checkedKeys;
            _faults.Hit("runner.tras_verificar", id);
            State.Transition(id, OpState.Verified, evidence: evid);
        }

        public void Dispose()
        {
            // only the CURRENT owner writes the checkpoint on close (a zombie does not)
            try { if (IsOwner && State != null) { _lease.Check(); State.Checkpoint(); } }
            catch (IOException) { }
            catch (PlanLease.LeaseLostException) { }
            _lease?.Release();
        }
    }
}
