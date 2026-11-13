// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    public sealed class RunnerOptions
    {
        public Faults Faults;
        public TimeSpan? LeaseTtl;
        public int CheckpointEvery = 64;
        /// <summary>Abrir sólo para inspeccionar (sin lease, sin escribir nada).</summary>
        public bool InspectOnly;
        /// <summary>alpha3: escrituras contra un backend real. null = bloqueadas (alpha1). "ensayo" o "persistente": lo decide el OPERADOR
        /// al lanzar el AgentHost (nunca el plan) y debe coincidir con el modo del backend.</summary>
        public string RealWrites;
    }

    /// <summary>plan.json: el plan validado y su hash. Una carpeta de estado pertenece a un único plan.</summary>
    public static class PlanFile
    {
        public static string PathIn(string dir) => Path.Combine(dir, "plan.json");

        public static void Write(string dir, ValidatedPlan plan) =>
            Home.WriteAtomic(PathIn(dir), Json.Write(new Dictionary<string, object> { ["hash"] = plan.Hash, ["plan"] = Json.Parse(plan.CanonicalJson) }));

        /// <summary>Lee y vuelve a validar el plan guardado. null + diagnóstico si no es fiable.</summary>
        public static ValidatedPlan Read(string dir, List<Diagnostic> diags)
        {
            var path = PathIn(dir);
            if (!File.Exists(path)) { diags.Add(Diagnostic.Error("plan_ausente", path)); return null; }
            Dictionary<string, object> o;
            try { o = Json.ParseObject(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is FormatException || ex is IOException) { diags.Add(Diagnostic.Error("plan_ilegible", ex.Message)); return null; }
            if (!(o.TryGetValue("plan", out var p) && p is Dictionary<string, object> pd)) { diags.Add(Diagnostic.Error("plan_ilegible", "sin campo plan")); return null; }
            var errs = IrValidator.Validate(PlanDraft.FromJson(pd), out var vp);
            if (errs.Count > 0) { diags.Add(Diagnostic.Error("plan_guardado_invalido", string.Join("; ", errs.Take(5)))); return null; }
            if (Arg.GetString(o, "hash") != vp.Hash) { diags.Add(Diagnostic.Error("plan_alterado", "hash guardado " + Arg.GetString(o, "hash") + " ≠ contenido " + vp.Hash)); return null; }
            return vp;
        }
    }

    /// <summary>
    /// Ejecutor de un plan con estado durable y recuperación verificada (AH-01…AH-05). Determinista: sigue el orden topológico del plan;
    /// no razona ni elige: lo que necesita juicio queda en un estado de excepción (fallida, requiere_verificacion, requiere_reconciliar,
    /// bloqueada) para una persona o un agente.
    ///
    /// Protocolo por operación: [precondiciones, conjunto vacío] → lease.Check → diario: en_ejecucion (intento n) → backend.Execute →
    /// diario: ejecutada (resultado) → postcondiciones contra el modelo + resultado esperado → diario: verificada (evidencia).
    /// Recuperación de en_ejecucion: se OBSERVA el modelo (efecto observado / no observado / parcial / incierto / sin objetivos) y sólo se
    /// vuelve a ejecutar si el efecto no existe; un efecto observado pasa siempre por la verificación; sin evidencia suficiente queda en
    /// requiere_verificacion, nunca en verificada.
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
        /// Abre (o reanuda) la ejecución de un plan en <paramref name="dir"/>. Devuelve null si no es seguro continuar (motivos en
        /// <paramref name="diags"/>). Si otro proceso es dueño, devuelve un ejecutor de sólo inspección (IsOwner=false) con un diagnóstico
        /// plan_en_uso.
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
                if (given != null && given.Hash != saved.Hash) { diags.Add(Diagnostic.Error("plan_distinto", "la carpeta pertenece al plan " + saved.Hash + "; el recibido es " + given.Hash)); return null; }
                r.Plan = saved;
            }
            else
            {
                if (given == null) { diags.Add(Diagnostic.Error("plan_ausente", "no hay plan.json ni plan recibido")); return null; }
                if (!opt.InspectOnly) PlanFile.Write(dir, given);
                r.Plan = given;
            }

            // 2. identidad del documento y política de escritura
            if (backend != null)
            {
                if (backend.DocKey != r.Plan.DocKey || backend.DocPrint != r.Plan.DocPrint)
                { diags.Add(Diagnostic.Error(ErrorClass.DocumentoDistinto, "el plan es para " + r.Plan.DocKey + " [" + r.Plan.DocPrint + "]; el backend tiene " + backend.DocKey + " [" + backend.DocPrint + "]")); return null; }
                if (backend.TouchesRealModel && r.Plan.Ops.Any(o => o.Writes) && (opt.RealWrites == null || opt.RealWrites != backend.WriteMode))
                { diags.Add(Diagnostic.Error("escrituras_no_permitidas", "el plan escribe en un modelo real y el operador no autorizó ese modo (autorizado: " + (opt.RealWrites ?? "ninguno") + ", backend: " + backend.WriteMode + ")")); return null; }
            }

            // 3. propiedad
            if (!opt.InspectOnly)
            {
                try { r._lease = PlanLease.TryAcquire(dir, opt.LeaseTtl, out var h); r.Holder = h; }
                catch (TaskStore.LeaseStoreException ex) { diags.Add(Diagnostic.Error("lease_no_fiable", ex.Message)); return null; }
                if (r._lease == null) diags.Add(Diagnostic.Warn(ErrorClass.PlanEnUso, "dueño " + r.Holder?.Owner + " (fencing " + r.Holder?.Fencing + ", vence " + r.Holder?.Expires + "); sólo inspección"));
            }

            // 4. estado
            r.State = ExecJournal.Recover(dir, r.Plan, r.IsOwner, r._faults, diags);
            if (r.State == null) { r._lease?.Release(); return null; }
            r.State.CheckpointEvery = Math.Max(1, opt.CheckpointEvery);
            if (r.IsOwner)
            {
                if (r._lease.Fencing <= r.State.Fencing)
                { diags.Add(Diagnostic.Error("lease_reiniciado", "fencing del lease " + r._lease.Fencing + " ≤ fencing del diario " + r.State.Fencing + "; el almacén de leases se perdió o se alteró")); r._lease.Release(); return null; }
                r.State.RecordOwner(r._lease.Fencing, r._lease.Owner);
            }
            return r;
        }

        private ExecContext Ctx(int attempt) => new ExecContext { PlanHash = Plan.Hash, Fencing = _lease?.Fencing ?? 0, Attempt = attempt };

        private void RequireOwner()
        {
            if (!IsOwner) throw new InvalidOperationException(ErrorClass.PlanEnUso + ": este proceso no es dueño del plan");
        }

        public void Pause() { RequireOwner(); if (State.PlanState == PlanRunState.Activo) { State.PlanAction("pausar"); State.Checkpoint(); } }
        public void Resume() { RequireOwner(); if (State.PlanState == PlanRunState.Pausado) { State.PlanAction("reanudar"); State.Checkpoint(); } }

        /// <summary>Cancela: las operaciones aún no empezadas pasan a cancelada; las que quedaron a medias se resuelven (recuperación) al ejecutar.</summary>
        public void Cancel()
        {
            RequireOwner();
            if (State.PlanState != PlanRunState.Cancelado) State.PlanAction("cancelar");
            foreach (var id in Plan.Order)
                if (State.Ops[id].State == OpState.Validada) State.Transition(id, OpState.Cancelada, error: ErrorClass.Cancelacion, note: "plan cancelado");
            State.Checkpoint();
        }

        /// <summary>
        /// Ejecuta o reanuda hasta el final, la cancelación del token (se detiene ENTRE operaciones; la operación en curso termina su
        /// protocolo) o la pérdida del lease. Devuelve el resumen de una línea.
        /// </summary>
        public string Run(CancellationToken ct = default)
        {
            RequireOwner();
            if (State.PlanState == PlanRunState.Pausado) return Compact.Summary(Plan, State) + " · pausado";
            var cancelled = State.PlanState == PlanRunState.Cancelado;
            try
            {
                // AUD-04 (#28): renovación explícita tras la inicialización (adquisición → recuperación del diario → apertura del modelo).
                // Si esa fase tardó más que el TTL, el lease ya venció: Check lo detecta y el proceso falla cerrado (lease_perdido, código 4)
                // ANTES de cualquier transición o escritura; nunca re-adquiere en silencio. Si no venció, lo renueva.
                _faults.Delay("runner.antes_de_ejecutar");
                _lease.Check();
                foreach (var id in Plan.Order)
                {
                    if (ct.IsCancellationRequested) break;
                    var rec = State.Ops[id];
                    if (OpState.IsFinal(rec.State)) continue;
                    if (cancelled && rec.State == OpState.Validada) { State.Transition(id, OpState.Cancelada, error: ErrorClass.Cancelacion, note: "plan cancelado"); continue; }
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
                    case OpState.Validada:
                        if (!ExecuteStep(op)) return;
                        break;
                    case OpState.EnEjecucion:
                        Recover(op);
                        break;
                    case OpState.Ejecutada:
                        VerifyStep(op);
                        break;
                    default:
                        return; // final
                }
            }
            throw new InvalidOperationException("la operación " + id + " no converge (estado " + State.Ops[id].State + ")");
        }

        /// <summary>validada → … Devuelve false si la operación no puede avanzar ahora (dependencia aún no resuelta).</summary>
        private bool ExecuteStep(ValidatedOp op)
        {
            var id = op.Id;
            foreach (var d in op.DependsOn)
            {
                var ds = State.Ops[d].State;
                if (ds == OpState.Verificada) continue;
                if (OpState.IsFinal(ds)) { State.Transition(id, OpState.Bloqueada, error: ErrorClass.Dependencia, note: "dependencia " + d + " en " + ds); return true; }
                return false;
            }
            var pre = _b.Prepare(op);
            if (pre.Error != null) { State.Transition(id, OpState.Fallida, error: ErrorClass.Precondicion, note: pre.Error); return true; }
            foreach (var pc in op.Preconditions)
                if ((pc.Min != null && pre.Targets < pc.Min) || (pc.Max != null && pre.Targets > pc.Max))
                { State.Transition(id, OpState.Fallida, error: ErrorClass.Precondicion, note: "objetivos = " + pre.Targets + " fuera de " + pc + "; no se escribió nada"); return true; }
            if (op.Writes && pre.Targets == 0 && !op.AllowEmpty)
            { State.Transition(id, OpState.Fallida, error: ErrorClass.SinObjetivos, note: "el alcance no selecciona ningún elemento; no se escribió nada (permitir_vacio=false)"); return true; }

            _lease.Check();
            var attempt = State.Ops[id].Attempt + 1;
            State.Transition(id, OpState.EnEjecucion, attempt: attempt);
            ExecResult r;
            try { r = _b.Execute(op, Ctx(attempt)); }
            catch (UncertainEffectException ex)
            {
                // no se sabe si se confirmó: nunca se reintenta a ciegas; se observa el modelo ahora mismo
                State.Note(id, "efecto_incierto", new Dictionary<string, object> { ["detalle"] = ex.Message });
                Recover(op);
                return true;
            }
            _faults.Hit("runner.tras_ejecutar", id);
            if (!r.Ok)
            {
                if (r.ErrorClass == ErrorClass.Transitorio && attempt <= op.RetryMax)
                { State.Transition(id, OpState.Validada, error: ErrorClass.Transitorio, note: "reintento " + attempt + "/" + op.RetryMax + ": " + r.Error); return true; }
                State.Transition(id, OpState.Fallida, error: r.ErrorClass ?? ErrorClass.Rechazo, note: r.Error);
                return true;
            }
            State.Transition(id, OpState.Ejecutada, result: r.Data);
            return true;
        }

        /// <summary>en_ejecucion tras una caída: decidir por lo que se OBSERVA en el modelo, nunca por suposición.</summary>
        private void Recover(ValidatedOp op)
        {
            var id = op.Id;
            var rec = State.Ops[id];
            var obs = _b.Observe(op, Ctx(rec.Attempt));
            State.Note(id, "recuperacion", new Dictionary<string, object> { ["efecto"] = obs.Effect.ToString(), ["transacciones"] = obs.Transactions, ["detalle"] = obs.Detail });
            switch (obs.Effect)
            {
                case Effect.NoObservado:
                    State.Transition(id, OpState.Validada, note: "recuperación: efecto no observado (" + obs.Detail + "); se ejecutará sin duplicar");
                    return;
                case Effect.SinObjetivos:
                    if (op.AllowEmpty) State.Transition(id, OpState.Ejecutada, result: new Dictionary<string, object> { ["elementos"] = 0L, ["cambiados"] = 0L, ["vacio"] = true, ["recuperada"] = true });
                    else State.Transition(id, OpState.Fallida, error: ErrorClass.SinObjetivos, note: "recuperación: sin efecto y el alcance no selecciona elementos");
                    return;
                case Effect.Parcial:
                    State.Transition(id, OpState.RequiereReconciliar, error: ErrorClass.EfectoParcial, note: "recuperación: " + obs.Detail + "; no se repite");
                    return;
                case Effect.Incierto:
                    State.Transition(id, OpState.RequiereReconciliar, error: ErrorClass.EfectoIncierto, note: "recuperación: " + obs.Detail + "; no se repite");
                    return;
                case Effect.Observado:
                    if (obs.Reconstructed != null) { State.Transition(id, OpState.Ejecutada, result: obs.Reconstructed, note: "recuperación: " + obs.Detail); return; }
                    // el efecto está probado pero el resultado no se puede reconstruir: no se repite y no se inventa un resultado
                    var post = _b.CheckPostconditions(op, Ctx(rec.Attempt));
                    var evid = Evidence(post);
                    if (post.State == PostState.NoSatisfechas)
                    { State.Transition(id, OpState.Fallida, error: ErrorClass.Verificacion, evidence: evid, note: "recuperación: efecto observado sin evidencia y postcondición NO satisfecha: " + post.Detail); return; }
                    if (post.State == PostState.NoEvaluables || op.Expected.Count > 0)
                    {
                        State.Transition(id, OpState.RequiereVerificacion, error: ErrorClass.EvidenciaInsuficiente, evidence: evid,
                            note: "recuperación: " + obs.Detail + "; postcondiciones " + post.State + (op.Expected.Count > 0 ? "; resultado_esperado (" + string.Join(",", op.Expected.Keys) + ") no comprobable" : "") + "; no se repite");
                        return;
                    }
                    // sin resultado esperado: la verificación es sólo de estado; el resultado deja explícito que no hay datos de ejecución
                    State.Transition(id, OpState.Ejecutada, result: new Dictionary<string, object> { ["recuperada"] = true, ["sin_datos_de_ejecucion"] = true }, note: "recuperación: " + obs.Detail);
                    return;
            }
        }

        private static Dictionary<string, object> Evidence(PostcheckResult post)
        {
            var e = new Dictionary<string, object>(post.Evidence, StringComparer.Ordinal) { ["postcondiciones"] = post.State.ToString() };
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
                // ensayo: la postcondición es "aplicado y verificado temporalmente, y el modelo volvió a su estado inicial".
                if (post.State == PostState.Satisfechas) { _faults.Hit("runner.tras_verificar", id); State.Transition(id, OpState.Ensayada, evidence: evid); }
                else State.Transition(id, OpState.RequiereReconciliar, error: ErrorClass.EfectoIncierto, evidence: evid, note: "ensayo sin restauración demostrada: " + post.Detail);
                return;
            }
            if (post.State == PostState.NoSatisfechas) { State.Transition(id, OpState.Fallida, error: ErrorClass.Verificacion, evidence: evid, note: "postcondición no satisfecha: " + post.Detail); return; }
            if (post.State == PostState.NoEvaluables) { State.Transition(id, OpState.RequiereVerificacion, error: ErrorClass.EvidenciaInsuficiente, evidence: evid, note: "postcondiciones no evaluables: " + post.Detail); return; }
            var checkedKeys = new List<object>();
            foreach (var kv in op.Expected)
            {
                object got = null;
                if (rec.Result == null || !rec.Result.TryGetValue(kv.Key, out got))
                { State.Transition(id, OpState.RequiereVerificacion, error: ErrorClass.EvidenciaInsuficiente, evidence: evid, note: "el resultado no contiene '" + kv.Key + "'; no se certifica"); return; }
                if (!Values.Same(kv.Value, got))
                { evid["esperado"] = kv.Key; State.Transition(id, OpState.Fallida, error: ErrorClass.Verificacion, evidence: evid, note: kv.Key + ": esperado " + Values.Show(kv.Value) + ", obtenido " + Values.Show(got)); return; }
                checkedKeys.Add(kv.Key);
            }
            evid["esperado_comprobado"] = checkedKeys;
            _faults.Hit("runner.tras_verificar", id);
            State.Transition(id, OpState.Verificada, evidence: evid);
        }

        public void Dispose()
        {
            // sólo el dueño VIGENTE escribe el checkpoint al cerrar (un zombi no)
            try { if (IsOwner && State != null) { _lease.Check(); State.Checkpoint(); } }
            catch (IOException) { }
            catch (PlanLease.LeaseLostException) { }
            _lease?.Release();
        }
    }
}
