// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>
    /// Motor de una tarea reanudable (§4). Una sola petición la inicia y el motor:
    /// 1. reconcilia lo dudoso (unidades sin final, o confirmadas después del último guardado si el documento se reabrió);
    /// 2. enumera las unidades de la receta (claves estables) y salta las ya confirmadas: reanudar nunca duplica;
    /// 3. ejecuta lotes de unidades, uno por visita al hilo de Revit, con tamaño adaptado a lo medido (cede la interfaz entre visitas);
    /// 4. escribe "inicio" ANTES y "fin" DESPUÉS de cada lote (durable): la ventana commit→diario queda "en_curso" y se verifica al reanudar;
    /// 5. respeta barreras (guardar, sync, PDF…): sólo corren cuando todo lo anterior está confirmado; registran la evidencia de persistencia;
    /// 6. cancelación cooperativa entre lotes; pausa del usuario, Revit ocupado o documento cerrado = espera con reintento acotado;
    /// 7. QC final (postcondición) y resumen de continuación determinista.
    /// No promete "exactamente una vez" por decreto: lo que no se puede probar queda "desconocida" y pide reconciliación explícita.
    /// </summary>
    public sealed class TaskRunner
    {
        private readonly TaskStore _store;
        private readonly ITaskBackend _b;
        private readonly TaskRecord _t;
        private readonly UnitLog _units;
        private readonly string _owner;
        private readonly string _instance;
        private readonly Action<TaskRecord> _changed;

        public volatile bool CancelRequested;
        /// <summary>Tiempo máximo esperando contexto (pausa, diálogo…) antes de dejar la tarea aparcada en esperando_contexto.</summary>
        public TimeSpan MaxWait = TimeSpan.FromHours(2);
        public Func<TimeSpan, CancellationToken, Task> Delay = (d, ct) => Task.Delay(d, ct);
        public Func<DateTime> Now = () => DateTime.UtcNow;
        public int BudgetMs = 200;
        public TimeSpan LeaseTtl = TimeSpan.FromSeconds(60);
        /// <summary>Autoridad de capacidades (R03). Por defecto niega todo lo que escribe: el add-in pone la real (GrantAuthority).</summary>
        public ITaskAuthority Authority = DenyWritesAuthority.Instance;
        /// <summary>Auditoría en dos fases (R01). null = sin auditoría (sólo pruebas del motor); el add-in siempre la pone.</summary>
        public AuditLog Audit;
        /// <summary>Exclusión por documento entre procesos (R06): leases en una carpeta hermana de las tareas.</summary>
        public TaskStore DocLeases;

        public TaskRunner(TaskStore store, ITaskBackend backend, TaskRecord task, string owner, string instance, Action<TaskRecord> changed)
        {
            _store = store;
            _b = backend;
            _t = task;
            _owner = owner;
            _instance = instance;
            _changed = changed ?? (_ => { });
            _units = store.LoadUnits(task.Id);
        }

        public TaskRecord Task_ => _t;
        public UnitLog Units => _units;

        private bool StopOnError => !string.Equals(_t.PolicyString("en_error"), "continuar", StringComparison.OrdinalIgnoreCase);
        private int MaxBatch => (int)Math.Max(1, Math.Min(500, _t.PolicyLong("lote_max", 200)));
        private long SaveEvery => _t.PolicyLong("guardar_cada", 0);

        private DateTime _lastLease;

        public async Task RunAsync(CancellationToken ct)
        {
            TaskStore.Lease holder;
            bool got;
            try { got = _store.TryLease(_t.Id, _owner, _instance, LeaseTtl, Now(), out holder); }
            catch (TaskStore.LeaseStoreException ex)
            {
                _t.Reason = "no se pudo tomar la ejecución de la tarea (almacén de leases no fiable: " + ex.Message + "); no se ejecutó nada";
                _changed(_t);
                return;
            }
            if (!got)
            {
                _t.Reason = "la tarea se está ejecutando en otra instancia (" + holder.Instance + ") hasta " + holder.Expires;
                _changed(_t);
                return;
            }
            _lastLease = Now();
            _fencing = holder.Fencing;
            // Exclusión por documento: dos tareas (aunque sean de procesos distintos) no escriben a la vez en el mismo documento.
            if (DocLeases != null && _t.MainDoc?.Key != null)
            {
                _docLeaseId = "doc-" + Home.Sha256Hex(_t.MainDoc.Key, 24);
                TaskStore.Lease dh = null;
                bool dok;
                try { dok = DocLeases.TryLease(_docLeaseId, _owner + "|" + _t.Id, _t.Id, LeaseTtl, Now(), out dh); }
                catch (TaskStore.LeaseStoreException ex) { dok = false; dh = new TaskStore.Lease { Instance = "?", Expires = "almacén no fiable: " + ex.Message }; }
                if (!dok)
                {
                    // Se persiste mientras aún se tiene el lease de la TAREA (nadie más escribe tarea.json), y después se suelta.
                    _docLeaseId = null;
                    SetState(TaskState.EsperandoContexto, "el documento lo está modificando la tarea " + dh.Instance + " (hasta " + dh.Expires + "); no se ejecutó nada, reanude cuando termine");
                    _store.ReleaseLease(_t.Id, _owner);
                    return;
                }
                _docFencing = dh.Fencing;
            }
            _t.Preflight = Preflight;
            _t.Owner = _instance;
            SetState(TaskState.Ejecutando, null);
            try
            {
                // rc3: autorizar ANTES de cualquier código de la receta (constructor, Preparar, enumeración, Verificar de la reconciliación).
                // El backend repite la comprobación en el hilo de Revit a través de Preflight/RecipeGate en cada fase.
                Authorize(null);
                if (!await ReconcileAsync(ct).ConfigureAwait(false)) return;
                var list = await EnumerateAsync(ct).ConfigureAwait(false);
                if (list == null) return;
                if (!await MainLoopAsync(list, ct).ConfigureAwait(false)) return;
                await FinishAsync(ct).ConfigureAwait(false);
            }
            catch (JournalUnavailableException ex)
            {
                SetState(TaskState.Parcial, "el registro de unidades no se pudo escribir (" + ex.Message + "): la tarea se detuvo para no perder la cuenta. " +
                                             "Lo confirmado hasta el último checkpoint está listado; corrija la carpeta de datos y reanude (se verificará lo dudoso).");
            }
            catch (CheckpointException ex)
            {
                SetState(TaskState.Parcial, "checkpoint no durable (" + ex.Message + "): la tarea se detuvo; el estado en disco puede ir por detrás del real. " +
                                             "Corrija la carpeta de datos y reanude: se verificará lo dudoso desde unidades.jsonl.");
            }
            catch (NotAuthorizedException ex)
            {
                SetState(TaskState.Interrumpida, "autorizacion_requerida: " + ex.Message + ". Nada más se ejecutó; una persona debe autorizarlo en Revit y luego reanudar.");
            }
            catch (AuditUnavailableException ex)
            {
                SetState(TaskState.Parcial, "auditoria_no_disponible: " + ex.Message);
            }
            catch (LeaseLostException ex)
            {
                SetState(TaskState.Interrumpida, "lease perdido (" + ex.Message + "): esta instancia dejó de ejecutar para no escribir sin autoridad; lo confirmado sigue en el registro");
            }
            catch (OperationCanceledException)
            {
                SetState(TaskState.Cancelada, "cancelada; lo confirmado antes de cancelar sigue aplicado (" + Count(UnitState.Confirmada) + " unidades)");
            }
            catch (Exception ex)
            {
                SetState(TaskState.Interrumpida, "error interno del motor (" + ex.GetType().Name + ": " + ex.Message + "); reanude: se verificará lo dudoso antes de seguir");
            }
            finally
            {
                _t.Preflight = null;
                if (_docLeaseId != null) DocLeases.ReleaseLease(_docLeaseId, _owner + "|" + _t.Id);
                _store.ReleaseLease(_t.Id, _owner);
                try { Checkpoint(); } catch (CheckpointException) { }
            }
        }

        // ---------------- 1) reconciliación ----------------

        private async Task<bool> ReconcileAsync(CancellationToken ct)
        {
            var gen = _b.CurrentGeneration(_t);
            var main = _t.MainDoc;
            var reopened = gen != null && main?.Generation != null && gen != main.Generation;
            var persistSeq = _t.LastPersist?.Seq ?? 0;
            var doubtful = _units.Units.Values.Where(u => UnitState.NeedsReconcile(u.State) ||
                                                          (reopened && u.State == UnitState.Confirmada && u.Seq > persistSeq)).ToList();
            if (doubtful.Count > 0)
            {
                _t.Reason = "verificando " + doubtful.Count + " unidades dudosas" + (reopened ? " (el documento se reabrió: lo confirmado después del último guardado pudo perderse)" : "");
                _changed(_t);
                var r = await WithWaitAsync(() => _b.VerifyAsync(_t, doubtful, reopened, ct), ct).ConfigureAwait(false);
                if (r == null) return false;
                var unknown = new List<string>();
                foreach (var o in r.Value)
                {
                    var state = o.State == UnitState.Confirmada ? UnitState.Confirmada : o.State == UnitState.Pendiente || o.State == UnitState.Revertida ? UnitState.Pendiente : UnitState.Desconocida;
                    _store.AppendUnitEvent(_t.Id, new Dictionary<string, object>
                    {
                        ["ev"] = "verificada", ["clave"] = o.Key, ["estado"] = state, ["error"] = o.Error, ["generacion"] = gen,
                        ["creados"] = o.Created.Count > 0 ? (object)o.Created : null,
                    });
                    _units.Apply(new Dictionary<string, object> { ["ev"] = "verificada", ["clave"] = o.Key, ["estado"] = state, ["error"] = o.Error });
                    if (state == UnitState.Desconocida) unknown.Add(o.Key);
                }
                _t.AddMetric("verificadas", r.Value.Count);
                if (unknown.Count > 0)
                {
                    SetState(TaskState.Interrumpida, unknown.Count + " unidades no se pudieron verificar en el modelo (" + string.Join(", ", unknown.Take(5)) +
                                                     (unknown.Count > 5 ? "…" : "") + "). Revíselas y use tarea reconciliar con decision=aplicada o no_aplicada; después, reanudar.");
                    return false;
                }
            }
            if (reopened && main != null)
            {
                main.Generation = gen;
                _t.AddMetric("reaperturas", 1);
            }
            else if (main != null && main.Generation == null) main.Generation = gen;
            return true;
        }

        // ---------------- 2) enumeración ----------------

        private async Task<List<UnitRef>> EnumerateAsync(CancellationToken ct)
        {
            _t.Reason = "enumerando unidades";
            _changed(_t);
            var r = await WithWaitAsync(() => _b.EnumerateAsync(_t, ct), ct).ConfigureAwait(false);
            if (r == null) return null;
            var list = r.Value ?? new List<UnitRef>();
            var dup = list.GroupBy(u => u.Key).FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                SetState(TaskState.Parcial, "la receta devolvió dos unidades con la misma clave '" + dup.Key + "': las claves deben ser únicas y estables");
                return null;
            }
            // Barreras finales pedidas en la tarea (al_final=guardar,pdf …)
            var i = 0;
            foreach (var spec in FinalBarriers())
            {
                var parts = spec.Split(new[] { ' ' }, 2);
                var bargs = new Dictionary<string, object>(StringComparer.Ordinal);
                if (parts.Length > 1)
                    foreach (var kv in parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq > 0) bargs[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                    }
                list.Add(new UnitRef { Key = "final:" + (++i) + ":" + parts[0], Barrier = parts[0], BarrierArgs = bargs });
            }
            for (var k = 0; k < list.Count; k++) list[k].Ordinal = k;
            _t.UnitsTotal = list.Count(u => !u.IsBarrier);
            _t.SetMetric("barreras", list.Count(u => u.IsBarrier));
            return list;
        }

        private IEnumerable<string> FinalBarriers()
        {
            if (!_t.Policy.TryGetValue("al_final", out var v) || v == null) return Enumerable.Empty<string>();
            if (v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).Where(x => !string.IsNullOrWhiteSpace(x));
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Split(';', ',').Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        // ---------------- 3) bucle principal ----------------

        private bool Done(UnitRef u)
        {
            var r = _units.Get(u.Key);
            return r != null && UnitState.IsDone(r.State);
        }

        private async Task<bool> MainLoopAsync(List<UnitRef> list, CancellationToken ct)
        {
            // Al reanudar se reintentan las revertidas y las de conflicto (se replanifican sobre el modelo actual).
            var pending = list.Where(u => !Done(u)).ToList();
            _t.Reason = null;
            SetState(TaskState.Ejecutando, null);
            var k = 1;
            double avg = -1;
            var emptyVisits = 0;
            long sinceSave = _t.ConfirmSeq - (_t.LastPersist?.Seq ?? 0);
            var i = 0;
            while (i < pending.Count)
            {
                if (CancelRequested) { SetState(TaskState.Cancelada, "cancelada a petición; lo confirmado antes (" + Count(UnitState.Confirmada) + " unidades) sigue aplicado"); return false; }
                if (!await EnsureContextAsync(ct).ConfigureAwait(false)) return false;
                RenewLease();
                var u = pending[i];
                Authorize(u.IsBarrier ? Capability.ForBarrier(u.Barrier) : null);
                if (u.IsBarrier)
                {
                    if (!await RunBarrierAsync(u, list, ct).ConfigureAwait(false)) return false;
                    sinceSave = _t.ConfirmSeq - (_t.LastPersist?.Seq ?? 0);
                    i++;
                    Checkpoint();
                    continue;
                }
                var batch = new List<UnitRef>();
                // Con guardar_cada=N el lote no pasa del punto de guardado: se guarda exactamente cada N confirmadas.
                var cap = SaveEvery > 0 ? (int)Math.Max(1, Math.Min(k, SaveEvery - sinceSave)) : k;
                for (var j = i; j < pending.Count && batch.Count < cap && !pending[j].IsBarrier; j++) batch.Add(pending[j]);
                var gen = _b.CurrentGeneration(_t);
                var startEv = new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = batch.Select(x => (object)x.Key).ToList(), ["generacion"] = gen };
                _store.AppendUnitEvent(_t.Id, startEv);
                _units.Apply(startEv);
                var intent = AuditIntent("tarea_lote", new Dictionary<string, object> { ["claves"] = startEv["claves"], ["generacion"] = gen });
                var r = await _b.RunBatchAsync(_t, batch, BudgetMs, ct).ConfigureAwait(false);
                var outcomes = r.Value ?? new List<UnitOutcome>();
                // Lo que el backend sí ejecutó se registra SIEMPRE, también cuando además pide esperar o falla.
                long batchMs = 0;
                var stop = (string)null;
                foreach (var o in outcomes)
                {
                    batchMs += o.Ms;
                    Record(o, gen);
                    if (o.State == UnitState.Confirmada) sinceSave++;
                    if (o.State == UnitState.Parcial || o.State == UnitState.Desconocida)
                        stop = "la unidad " + o.Key + " quedó " + o.State + " (" + o.Error + "): reconcíliela antes de seguir";
                    else if (o.State == UnitState.Revertida && StopOnError)
                        stop = "la unidad " + o.Key + " falló y se revirtió: " + o.Error + ". Corrija la causa y reanude (se reintenta), o reanude con en_error=continuar";
                }
                var notRun = batch.Skip(outcomes.Count).Select(x => (object)x.Key).ToList();
                if (notRun.Count > 0)
                {
                    _store.AppendUnitEvent(_t.Id, new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = notRun });
                    _units.Apply(new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = notRun });
                }
                AuditOutcome("tarea_lote", intent, outcomes);
                _t.AddMetric("lotes", 1);
                _t.AddMetric("ms_revit", batchMs);
                if (outcomes.Count > (int)_t.Metric("lote_max_real")) _t.SetMetric("lote_max_real", outcomes.Count);
                i += outcomes.Count;
                Checkpoint();
                if (stop != null) { SetState(TaskState.Parcial, stop); return false; }
                if (r.Fatal != null) { SetState(TaskState.Parcial, r.Fatal); return false; }
                if (r.Wait != null)
                {
                    if (!await WaitContextAsync(r.Wait, r.Action, ct).ConfigureAwait(false)) return false;
                    continue;
                }
                if (outcomes.Count == 0)
                {
                    if (++emptyVisits >= 3) { SetState(TaskState.Parcial, "Revit no ejecutó ninguna unidad en 3 visitas seguidas; reanude más tarde"); return false; }
                    k = 1;
                    continue;
                }
                emptyVisits = 0;
                // Tamaño de lote adaptativo: lo que cabe en el presupuesto según el tiempo medido por unidad.
                var per = (double)batchMs / outcomes.Count;
                avg = avg < 0 ? per : avg * 0.7 + per * 0.3;
                k = outcomes.Count < batch.Count ? Math.Max(1, outcomes.Count) : (int)Math.Max(1, Math.Min(MaxBatch, Math.Floor(BudgetMs * 0.8 / Math.Max(0.5, avg))));
                if (SaveEvery > 0 && sinceSave >= SaveEvery && i < pending.Count)
                {
                    var auto = new UnitRef { Key = "auto-guardar:" + _t.ConfirmSeq, Barrier = "guardar", BarrierArgs = new Dictionary<string, object>() };
                    if (!await RunBarrierAsync(auto, list, ct).ConfigureAwait(false)) return false;
                    sinceSave = 0;
                    Checkpoint();
                }
            }
            return true;
        }

        private void Record(UnitOutcome o, string gen)
        {
            long? seq = null;
            if (o.State == UnitState.Confirmada) seq = ++_t.ConfirmSeq;
            var ev = new Dictionary<string, object>
            {
                ["ev"] = "fin", ["clave"] = o.Key, ["estado"] = o.State, ["ms"] = o.Ms, ["seq"] = seq, ["generacion"] = gen,
                ["cambiados"] = o.Changed, ["borrados"] = o.Deleted, ["error"] = o.Error,
                ["creados"] = o.Created.Count > 0 ? (object)o.Created : null,
                ["resultado"] = TrimResult(o.Result),
            };
            _store.AppendUnitEvent(_t.Id, ev);
            _units.Apply(ev);
            lock (_t) foreach (var d in o.Decisions) _t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["unidad"] = o.Key, ["texto"] = d });
            foreach (var x in o.Exceptions) { x["unidad"] = o.Key; _t.Exceptions.Add(x); }
            foreach (var a in o.Artifacts) { a["unidad"] = o.Key; _t.Artifacts.Add(a); }
            if (_t.Decisions.Count > 200) _t.Decisions.RemoveRange(0, _t.Decisions.Count - 200);
        }

        private static object TrimResult(object r)
        {
            if (r == null) return null;
            var txt = Json.Write(r);
            return txt.Length <= 1500 ? r : txt.Substring(0, 1500) + "…";
        }

        private async Task<bool> RunBarrierAsync(UnitRef u, List<UnitRef> list, CancellationToken ct)
        {
            var before = list.Where(x => x.Ordinal < u.Ordinal && !x.IsBarrier && !Done(x)).Select(x => x.Key).ToList();
            if (u.Ordinal == 0 && u.Key.StartsWith("auto-", StringComparison.Ordinal)) before.Clear();
            if (before.Count > 0)
            {
                SetState(TaskState.Parcial, "la barrera " + u.Barrier + " no se ejecuta porque " + before.Count + " unidades anteriores no están confirmadas (" +
                                            string.Join(", ", before.Take(5)) + (before.Count > 5 ? "…" : "") + "): guardar/exportar ahora dejaría un resultado inconsistente");
                return false;
            }
            var gen = _b.CurrentGeneration(_t);
            var startEv = new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = new List<object> { u.Key }, ["generacion"] = gen };
            _store.AppendUnitEvent(_t.Id, startEv);
            _units.Apply(startEv);
            _t.Reason = "barrera " + u.Barrier;
            _changed(_t);
            var bIntent = AuditIntent("tarea_barrera_" + u.Barrier, new Dictionary<string, object> { ["clave"] = u.Key, ["generacion"] = gen });
            var r = await WithWaitAsync(() => _b.RunBarrierAsync(_t, u, ct), ct).ConfigureAwait(false);
            if (r == null)
            {
                _store.AppendUnitEvent(_t.Id, new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = new List<object> { u.Key } });
                _units.Apply(new Dictionary<string, object> { ["ev"] = "sin_ejecutar", ["claves"] = new List<object> { u.Key } });
                return false;
            }
            var o = r.Value;
            AuditOutcome("tarea_barrera_" + u.Barrier, bIntent, new List<UnitOutcome> { o });
            // Una barrera no confirma unidades: no consume secuencia de confirmación.
            var ev = new Dictionary<string, object>
            {
                ["ev"] = "fin", ["clave"] = u.Key, ["estado"] = o.State, ["ms"] = o.Ms, ["error"] = o.Error, ["generacion"] = gen, ["resultado"] = TrimResult(o.Result),
            };
            _store.AppendUnitEvent(_t.Id, ev);
            _units.Apply(ev);
            foreach (var a in o.Artifacts) { a["unidad"] = u.Key; _t.Artifacts.Add(a); }
            foreach (var x in o.Exceptions) { x["unidad"] = u.Key; _t.Exceptions.Add(x); }
            _t.AddMetric("barreras_ok", o.State == UnitState.Confirmada ? 1 : 0);
            if (o.State != UnitState.Confirmada)
            {
                SetState(TaskState.Parcial, "la barrera " + u.Barrier + " falló: " + o.Error);
                return false;
            }
            if (o.Persist != null)
            {
                o.Persist.Seq = _t.ConfirmSeq;
                o.Persist.Generation = gen;
                _t.NotePersist(o.Persist);
            }
            return true;
        }

        // ---------------- 4) final ----------------

        private async Task FinishAsync(CancellationToken ct)
        {
            _t.Reason = "QC final";
            _changed(_t);
            var r = await WithWaitAsync(() => _b.PostconditionAsync(_t, ct), ct).ConfigureAwait(false);
            if (r == null) return;
            _t.Postcondition = r.Value;
            var deviations = r.Value != null && r.Value.TryGetValue("desviaciones", out var dv) && dv is List<object> dl ? dl.Count : 0;
            var conflicts = Count(UnitState.Conflicto);
            var reverted = Count(UnitState.Revertida);
            var diff = deviations > 0 || conflicts > 0 || reverted > 0 || _t.Exceptions.Count > 0;
            SetState(diff ? TaskState.AplicadaConDiferencias : TaskState.Aplicada,
                diff ? "terminó con " + conflicts + " conflictos, " + reverted + " revertidas, " + _t.Exceptions.Count + " excepciones y " + deviations + " desviaciones del QC" : null);
        }

        // ---------------- esperas, contexto, estado ----------------

        private async Task<bool> EnsureContextAsync(CancellationToken ct)
        {
            var c = _b.CheckContext(_t);
            if (c.Fatal != null) { SetState(TaskState.Parcial, c.Fatal); return false; }
            if (c.Wait == null) return true;
            return await WaitContextAsync(c.Wait, c.Action, ct).ConfigureAwait(false);
        }

        /// <summary>Espera con reintentos espaciados (1, 2, 4… hasta 15 s): no bombardea el ExternalEvent ni gasta llamadas del agente.</summary>
        private async Task<bool> WaitContextAsync(string why, string action, CancellationToken ct)
        {
            var start = Now();
            var delay = TimeSpan.FromSeconds(1);
            SetState(TaskState.EsperandoContexto, why + (action != null ? ". Acción: " + action : ""));
            _t.AddMetric("esperas", 1);
            while (true)
            {
                if (CancelRequested) { SetState(TaskState.Cancelada, "cancelada mientras esperaba; lo confirmado antes sigue aplicado (" + Count(UnitState.Confirmada) + " unidades)"); return false; }
                if (Now() - start > MaxWait)
                {
                    SetState(TaskState.EsperandoContexto, why + ". Se dejó de esperar tras " + (int)MaxWait.TotalMinutes + " min; cuando se resuelva, use tarea reanudar");
                    return false;
                }
                await Delay(delay, ct).ConfigureAwait(false);
                RenewLease();
                var c = _b.CheckContext(_t);
                if (c.Fatal != null) { SetState(TaskState.Parcial, c.Fatal); return false; }
                if (c.Wait == null) { SetState(TaskState.Ejecutando, null); return true; }
                if (c.Wait != why) { why = c.Wait; SetState(TaskState.EsperandoContexto, why + (c.Action != null ? ". Acción: " + c.Action : "")); }
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
            }
        }

        private async Task<BackendResult<T>> WithWaitAsync<T>(Func<Task<BackendResult<T>>> step, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var r = await step().ConfigureAwait(false);
                if (r.Fatal != null) { SetState(TaskState.Parcial, r.Fatal); return null; }
                if (r.Wait == null) return r;
                if (!await WaitContextAsync(r.Wait, r.Action, ct).ConfigureAwait(false)) return null;
            }
        }

        private long _fencing;
        public long Fencing => _fencing;
        private string _docLeaseId;
        private long _docFencing;
        private string _checkpointError;
        private DateTime _authCacheAt = DateTime.MinValue;
        private string _authCacheCap;
        private string _authCacheResult;

        public sealed class CheckpointException : Exception { public CheckpointException(string m) : base(m) { } }
        public sealed class NotAuthorizedException : Exception { public NotAuthorizedException(string m) : base(m) { } }
        public sealed class AuditUnavailableException : Exception { public AuditUnavailableException(string m) : base(m) { } }

        /// <summary>Capacidades que la tarea necesita para sus unidades (no barreras). Sin la lista (tareas de 0.6.0): se asume que escribe.</summary>
        private IEnumerable<string> UnitCapabilities()
        {
            if (_t.Policy.TryGetValue("requiere", out var v) && v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture));
            return new[] { Capability.Modificar };
        }

        /// <summary>R03: comprueba concesiones en el punto de ejecución. barrierCap = null → capacidades de las unidades.</summary>
        private void Authorize(string barrierCap)
        {
            var now = Now();
            foreach (var cap in barrierCap != null ? new[] { barrierCap } : UnitCapabilities())
            {
                var why = (Authority == null ? "sin autoridad configurada" : Authority.Check(_t, cap, now));
                if (why != null) throw new NotAuthorizedException(cap + ": " + why);
            }
        }

        /// <summary>
        /// Último punto seguro (R06): el backend lo llama en el hilo de Revit JUSTO antes de abrir la transacción de cada unidad.
        /// null = adelante; si no, la unidad no se ejecuta (queda sin_ejecutar) y el ejecutor se detiene en el siguiente punto.
        /// Barato: validar lease ~0,07 ms; la autoridad se cachea 2 s (una revocación tarda como mucho 2 s en cortar).
        /// </summary>
        private string Preflight()
        {
            var now = Now();
            if (!_store.ValidateOwnership(_t.Id, _owner, _fencing, now)) return "lease_perdido";
            if (_docLeaseId != null && !DocLeases.ValidateOwnership(_docLeaseId, _owner + "|" + _t.Id, _docFencing, now)) return "documento_sin_exclusion";
            foreach (var cap in UnitCapabilities())
            {
                string why;
                if (_authCacheCap == cap && now - _authCacheAt < TimeSpan.FromSeconds(2)) why = _authCacheResult;
                else { why = (Authority == null ? "sin autoridad configurada" : Authority.Check(_t, cap, now)); _authCacheCap = cap; _authCacheAt = now; _authCacheResult = why; }
                if (why != null) return "autorizacion_requerida: " + cap + ": " + why;
            }
            return null;
        }

        private string AuditIntent(string op, Dictionary<string, object> extra)
        {
            if (Audit == null) return null;
            extra["tarea"] = _t.Id;
            extra["fencing"] = _fencing;
            extra["doc"] = _t.MainDoc?.Key;
            try { return Audit.Intent(_owner, op, _t.Id, extra); }
            catch (JournalUnavailableException ex) { throw new AuditUnavailableException("no se pudo registrar la intención antes de " + op + " (" + ex.Message + "): no se modificó nada"); }
        }

        private void AuditOutcome(string op, string intent, List<UnitOutcome> outs)
        {
            if (Audit == null || intent == null) return;
            var extra = new Dictionary<string, object>
            {
                ["tarea"] = _t.Id,
                ["resultado"] = outs.Where(o => o != null).GroupBy(o => o.State).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
            };
            try { Audit.Outcome(_owner, op, intent, "resultado", extra); }
            catch (JournalUnavailableException ex)
            {
                // Lo ejecutado ya está en unidades.jsonl; la intención sin resultado aparece en AuditLog.PendingIntents para reconciliar.
                throw new AuditUnavailableException("la operación terminó y está en el registro de unidades, pero su resultado no quedó en la auditoría (" + ex.Message +
                                                    "); intención " + intent + " pendiente de reconciliar. La tarea se detuvo.");
            }
        }

        public sealed class LeaseLostException : Exception { public LeaseLostException(string m) : base(m) { } }

        /// <summary>Punto de autoridad antes de cada lote/barrera: valida dueño+fencing y renueva si toca. Falla → LeaseLostException (no se escribe).</summary>
        private void RenewLease()
        {
            if (_checkpointError != null) throw new CheckpointException(_checkpointError);
            var now = Now();
            if (now - _lastLease >= TimeSpan.FromSeconds(LeaseTtl.TotalSeconds / 3))
            {
                bool ok;
                try { ok = _store.RenewLease(_t.Id, _owner, _fencing, LeaseTtl, now); }
                catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("renovación imposible: " + ex.Message); }
                if (!ok) throw new LeaseLostException("otra instancia tomó la tarea o el lease venció (fencing " + _fencing + ")");
                if (_docLeaseId != null)
                {
                    bool dok;
                    try { dok = DocLeases.RenewLease(_docLeaseId, _owner + "|" + _t.Id, _docFencing, LeaseTtl, now); }
                    catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("exclusión del documento: " + ex.Message); }
                    if (!dok) throw new LeaseLostException("se perdió la exclusión del documento (fencing " + _docFencing + ")");
                }
                _lastLease = now;
                return;
            }
            if (!_store.ValidateOwnership(_t.Id, _owner, _fencing, now))
                throw new LeaseLostException("validación de propiedad fallida (fencing " + _fencing + ")");
            if (_docLeaseId != null && !DocLeases.ValidateOwnership(_docLeaseId, _owner + "|" + _t.Id, _docFencing, now))
                throw new LeaseLostException("se perdió la exclusión del documento (fencing " + _docFencing + ")");
        }

        private long Count(string state) => _units.Units.Values.LongCount(u => u.State == state);

        private void SetState(string state, string reason)
        {
            _t.State = state;
            _t.Reason = reason;
            // Un fallo aquí ya quedó marcado (Durable=false, _checkpointError); el siguiente punto de autoridad detiene la tarea.
            try { Checkpoint(); } catch (CheckpointException) { }
        }

        private void Checkpoint()
        {
            _t.Rev++;
            _t.AddMetric("checkpoints", 1);
            try
            {
                lock (_t) { _t.Summary = TaskSummary.Continuation(_t, _units); _store.Save(_t); }
                _checkpointError = null;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                // R07: nunca se declara durable lo que sólo está en memoria. Se marca, se avisa y el siguiente punto de autoridad detiene la tarea.
                _checkpointError = ex.Message;
                _t.Durable = false;
                _t.Reason = (_t.Reason == null ? "" : _t.Reason + " | ") + "checkpoint NO guardado: " + ex.Message;
                _changed(_t);
                throw new CheckpointException(ex.Message);
            }
            _t.Durable = true;
            _changed(_t);
        }
    }
}
