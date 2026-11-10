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
    /// Tareas de este Revit: crea, lanza, reanuda, cancela, espera cambios y reconcilia. Un solo motor por tarea en el proceso: si Codex y
    /// Claude piden "reanudar" a la vez, el segundo recibe el estado del que ya corre. Entre procesos (dos Revit) decide el lease en disco.
    /// Cualquier cliente recupera la tarea por su id, sin la sesión que la creó.
    /// </summary>
    public sealed class TaskManager
    {
        private readonly TaskStore _store;
        private readonly string _instance;
        private readonly Func<TaskRecord, ITaskBackend> _backend;
        private readonly object _lock = new object();
        private readonly Dictionary<string, TaskRunner> _running = new Dictionary<string, TaskRunner>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task> _work = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _signals = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Para las pruebas: esperas rápidas.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay;
        public TimeSpan? MaxWait;
        public int BudgetMs = 200;
        /// <summary>R03: autoridad de concesiones; por defecto niega escrituras.</summary>
        public ITaskAuthority Authority = DenyWritesAuthority.Instance;
        /// <summary>R01: auditoría en dos fases de lotes y barreras.</summary>
        public AuditLog Audit;
        /// <summary>R06: leases por documento (carpeta hermana de las tareas).</summary>
        public TaskStore DocLeases;

        public TaskManager(TaskStore store, string instance, Func<TaskRecord, ITaskBackend> backend)
        {
            _store = store;
            _instance = instance;
            _backend = backend;
        }

        public TaskStore Store => _store;

        public sealed class StartResult
        {
            public TaskRecord Task;
            public bool Created;
            public bool AlreadyRunning;
            public string Error;
            public string ErrorStatus;
        }

        /// <summary>
        /// Crea y lanza una tarea. Mismo id con la misma huella → se devuelve la existente (no se duplica); mismo id con otro contenido →
        /// error (un id identifica una tarea). La receta se guarda junto a la tarea: reanudar usa esa copia.
        /// </summary>
        public StartResult Start(TaskRecord draft, string recipeCode, string client)
        {
            lock (_lock)
            {
                var existing = _store.Load(draft.Id);
                if (existing != null)
                {
                    if (existing.ContentHash != draft.ContentHash)
                        return new StartResult
                        {
                            Error = "la tarea " + draft.Id + " ya existe con otro contenido (receta, argumentos o documento distintos: " + existing.ContentHash + " ≠ " + draft.ContentHash +
                                    "). Use otro id, o tarea reanudar " + draft.Id + " para continuar la existente tal cual.",
                            ErrorStatus = Status.Invalido,
                        };
                    var running = _running.ContainsKey(draft.Id);
                    if (!running && !TaskState.IsFinal(existing.State)) return Resume(draft.Id, client, null);
                    return new StartResult { Task = running ? _running[draft.Id].Task_ : existing, AlreadyRunning = running };
                }
                draft.Created = TaskTime.Now();
                draft.State = TaskState.EnCola;
                draft.Client = client;
                if (client != null && !draft.Clients.Contains(client)) draft.Clients.Add(client);
                _store.Create(draft, recipeCode);
                Launch(draft);
                return new StartResult { Task = draft, Created = true };
            }
        }

        /// <summary>Reanuda una tarea no terminada (o terminada con diferencias, para reintentar conflictos y revertidas).</summary>
        public StartResult Resume(string id, string client, Dictionary<string, object> policy)
        {
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r)) return new StartResult { Task = r.Task_, AlreadyRunning = true };
                var t = _store.Load(id);
                if (t == null) return new StartResult { Error = "no existe la tarea " + id + " (tarea lista)", ErrorStatus = Status.Invalido };
                if (t.State == TaskState.Aplicada)
                    return new StartResult { Task = t };
                var lease = _store.ReadLease(id);
                if (lease != null && lease.Valid(DateTime.UtcNow) && lease.Instance != _instance)
                    return new StartResult { Task = t, Error = "la tarea se está ejecutando en otro Revit (" + lease.Instance + ")", ErrorStatus = Status.DocumentoOcupado };
                if (policy != null) foreach (var kv in policy) t.Policy[kv.Key] = kv.Value;
                t.Client = client ?? t.Client;
                if (client != null && !t.Clients.Contains(client)) t.Clients.Add(client);
                t.State = TaskState.EnCola;
                t.Reason = null;
                _store.Save(t);
                Launch(t);
                return new StartResult { Task = t };
            }
        }

        private void Launch(TaskRecord t)
        {
            var backend = _backend(t);
            var runner = new TaskRunner(_store, backend, t, "inst:" + _instance, _instance, Changed)
            {
                BudgetMs = BudgetMs, Authority = Authority, Audit = Audit,
                DocLeases = DocLeases ?? new TaskStore(_store.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + "_documentos"),
            };
            if (Delay != null) runner.Delay = Delay;
            if (MaxWait.HasValue) runner.MaxWait = MaxWait.Value;
            _running[t.Id] = runner;
            _work[t.Id] = Task.Run(async () =>
            {
                try { await runner.RunAsync(CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    lock (_lock) { _running.Remove(t.Id); }
                    // El backend suelta lo que tomó (documentos, compilación) al terminar el motor, pase lo que pase.
                    try { (backend as IDisposable)?.Dispose(); } catch (Exception) { }
                    Changed(t);
                }
            });
        }

        /// <summary>Espera a que termine el motor de la tarea (para pruebas y para el cierre ordenado de Revit).</summary>
        public Task Completion(string id)
        {
            lock (_lock) return _work.TryGetValue(id, out var w) ? w : Task.CompletedTask;
        }

        public bool IsRunning(string id)
        {
            lock (_lock) return _running.ContainsKey(id);
        }

        /// <summary>Cancelación cooperativa: queda "cancelacion_solicitada" hasta que el motor la confirma entre lotes.</summary>
        public TaskRecord Cancel(string id)
        {
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r))
                {
                    r.CancelRequested = true;
                    var t = r.Task_;
                    if (!TaskState.IsFinal(t.State)) { t.State = TaskState.CancelacionSolicitada; t.Reason = "cancelación pedida; se confirma al terminar la unidad en curso"; }
                    Changed(t);
                    return t;
                }
                var s = _store.Load(id);
                if (s == null) return null;
                if (!TaskState.IsFinal(s.State))
                {
                    s.State = TaskState.Cancelada;
                    s.Reason = "cancelada sin estar en ejecución; lo confirmado antes sigue aplicado";
                    s.Rev++;
                    _store.Save(s);
                    Changed(s);
                }
                return s;
            }
        }

        /// <summary>Estado actual: el del motor si corre aquí; si no, el de disco (con "interrumpida" si su motor murió).</summary>
        public TaskRecord Get(string id, out UnitLog units)
        {
            lock (_lock)
            {
                if (_running.TryGetValue(id, out var r)) { units = r.Units; return r.Task_; }
            }
            var t = _store.Load(id);
            units = t == null ? new UnitLog() : _store.LoadUnits(id);
            if (t != null && (t.State == TaskState.Ejecutando || t.State == TaskState.EnCola || t.State == TaskState.CancelacionSolicitada))
            {
                var lease = _store.ReadLease(id);
                if (lease == null || !lease.Valid(DateTime.UtcNow) || lease.Instance == _instance)
                {
                    // Nadie la corre: el proceso que la llevaba murió o se cerró Revit.
                    t.State = TaskState.Interrumpida;
                    t.Reason = "el motor que la ejecutaba ya no está (Revit se cerró o se reinició); tarea reanudar verifica lo dudoso y continúa";
                }
            }
            return t;
        }

        /// <summary>Espera hasta que la revisión de la tarea supere <paramref name="since"/> o pase el tiempo (sin sondeo del agente).</summary>
        public async Task<TaskRecord> WaitAsync(string id, long since, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (true)
            {
                Task signal;
                lock (_lock) signal = Signal(id).Task;
                var t = Get(id, out _);
                if (t == null || t.Rev > since || !IsRunning(id)) return t;
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return t;
                await Task.WhenAny(signal, Task.Delay(left, ct)).ConfigureAwait(false);
            }
        }

        private TaskCompletionSource<bool> Signal(string id)
        {
            if (!_signals.TryGetValue(id, out var s) || s.Task.IsCompleted)
            {
                s = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _signals[id] = s;
            }
            return s;
        }

        private void Changed(TaskRecord t)
        {
            TaskCompletionSource<bool> s;
            lock (_lock) { _signals.TryGetValue(t.Id, out s); _signals.Remove(t.Id); }
            s?.TrySetResult(true);
        }

        /// <summary>
        /// Reconciliación explícita (§3.2): el agente o el usuario leyeron el modelo y deciden. aplicada = queda confirmada (no se repite);
        /// no_aplicada = vuelve a pendiente (se ejecutará al reanudar). Sólo con la tarea detenida.
        /// </summary>
        /// <summary>
        /// Reconciliación con transiciones explícitas (R05):
        /// - sólo unidades DUDOSAS (en_curso, parcial, desconocida). Una confirmada/omitida nunca vuelve a pendiente por esta vía
        ///   (procedimiento extraordinario: una tarea nueva que el usuario decide);
        /// - "aplicada" exige evidencia (nota); "no aplicada" provoca que se vuelva a ejecutar, así que además exige la concesión
        ///   reconciliar_no_aplicada de una persona para ESTA tarea y documento;
        /// - una tarea finalizada (aplicada, aplicada_con_diferencias, cancelada) no se reabre;
        /// - nada se borra: cada decisión es un evento nuevo con el estado anterior.
        /// </summary>
        public string Reconcile(string id, IList<string> keys, bool applied, string note, string who)
        {
            lock (_lock)
            {
                if (_running.ContainsKey(id)) return "la tarea está corriendo; cancélela o espere a que se detenga antes de reconciliar";
                var t = _store.Load(id);
                if (t == null) return "no existe la tarea " + id;
                if (TaskState.IsFinal(t.State)) return "transicion_ilegal: la tarea está " + t.State + " y no se reabre; inicie una tarea nueva si hace falta";
                if (string.IsNullOrWhiteSpace(note)) return "evidencia_requerida: diga en nota= qué se comprobó en el modelo (ids, valores) para decidir";
                var units = _store.LoadUnits(id);
                var targets = keys != null && keys.Count > 0
                    ? keys.ToList()
                    : units.Units.Values.Where(u => UnitState.NeedsReconcile(u.State)).Select(u => u.Key).ToList();
                var unknown = targets.Where(k => units.Get(k) == null).ToList();
                if (unknown.Count > 0) return "unidades que no existen en la tarea: " + string.Join(", ", unknown.Take(10));
                var illegal = targets.Where(k => !UnitState.NeedsReconcile(units.Get(k).State)).ToList();
                if (illegal.Count > 0)
                    return "transicion_ilegal: sólo se reconcilian unidades dudosas (en_curso, parcial, desconocida); " +
                           string.Join(", ", illegal.Take(10).Select(k => k + "=" + units.Get(k).State)) +
                           (illegal.Any(k => UnitState.IsDone(units.Get(k).State)) ? ". Una unidad confirmada no se vuelve a ejecutar." : "");
                if (!applied)
                {
                    var why = (Authority == null ? "sin autoridad configurada" : Authority.Check(t, Capability.ReconciliarNoAplicada, DateTime.UtcNow));
                    if (why != null) return "autorizacion_requerida: marcar como no aplicada hará que se ejecute otra vez; " + why;
                }
                foreach (var k in targets)
                {
                    long? seq = null;
                    if (applied) seq = ++t.ConfirmSeq;
                    _store.AppendUnitEvent(id, new Dictionary<string, object>
                    {
                        ["ev"] = "reconciliada", ["clave"] = k, ["estado"] = applied ? UnitState.Confirmada : UnitState.Pendiente, ["seq"] = seq,
                        ["antes"] = units.Get(k).State, ["quien"] = who, ["evidencia"] = note,
                        ["error"] = (applied ? "reconciliada como aplicada" : "reconciliada como no aplicada") + ": " + note + " (" + who + ")",
                    });
                }
                t.Decisions.Add(new Dictionary<string, object>
                {
                    ["t"] = TaskTime.Now(), ["texto"] = "reconciliadas " + targets.Count + " unidades como " + (applied ? "aplicadas" : "no aplicadas") + ": " + note, ["quien"] = who,
                });
                if (t.State == TaskState.Interrumpida || t.State == TaskState.Parcial) t.Reason = "reconciliada; use tarea reanudar";
                t.Rev++;
                t.Summary = TaskSummary.Continuation(t, _store.LoadUnits(id));
                _store.Save(t);
                Changed(t);
                return null;
            }
        }

        /// <summary>Anota una decisión del agente o del usuario en la memoria de la tarea (aparece en el resumen de continuación).</summary>
        public string Note(string id, string text, string who)
        {
            lock (_lock)
            {
                TaskRecord t;
                if (_running.TryGetValue(id, out var r)) t = r.Task_;
                else t = _store.Load(id);
                if (t == null) return "no existe la tarea " + id;
                lock (t) t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["texto"] = text, ["quien"] = who });
                if (!_running.ContainsKey(id)) { t.Rev++; _store.Save(t); }
                Changed(t);
                return null;
            }
        }

        /// <summary>
        /// Un documento se guardó o sincronizó (lo informa Revit, lo haya pedido la tarea o el usuario). Las tareas no terminadas de ese
        /// documento EN ESA MISMA apertura registran la evidencia: lo confirmado hasta ahora ya está en disco. Un guardado en otra apertura no
        /// cuenta (lo confirmado en la apertura anterior pudo perderse al cerrar sin guardar).
        /// </summary>
        public int RecordPersist(string docKey, string generation, PersistEvidence ev)
        {
            if (docKey == null || generation == null || ev == null) return 0;
            var n = 0;
            List<TaskRecord> stored;
            lock (_lock)
            {
                foreach (var r in _running.Values)
                {
                    var t = r.Task_;
                    if (t.MainDoc?.Key != docKey) continue;
                    lock (t)
                    {
                        if (t.MainDoc.Generation != null && t.MainDoc.Generation != generation) continue;
                        t.NotePersist(new PersistEvidence { Kind = ev.Kind, When = ev.When, Seq = t.ConfirmSeq, Generation = generation, File = ev.File, Bytes = ev.Bytes, Mtime = ev.Mtime, Sha = ev.Sha });
                    }
                    n++;
                }
                stored = _store.List().Where(t => !_running.ContainsKey(t.Id) && !TaskState.IsFinal(t.State) && t.MainDoc?.Key == docKey && t.MainDoc.Generation == generation).ToList();
                foreach (var t in stored)
                {
                    t.NotePersist(new PersistEvidence { Kind = ev.Kind, When = ev.When, Seq = t.ConfirmSeq, Generation = generation, File = ev.File, Bytes = ev.Bytes, Mtime = ev.Mtime, Sha = ev.Sha });
                    t.Rev++;
                    _store.Save(t);
                    n++;
                }
            }
            foreach (var t in stored) Changed(t);
            return n;
        }

        /// <summary>Guardar como: las tareas no terminadas del documento siguen en el archivo nuevo (y lo anotan).</summary>
        public int Rekey(string oldKey, string newKey)
        {
            var n = 0;
            var changed = new List<TaskRecord>();
            lock (_lock)
            {
                foreach (var r in _running.Values)
                {
                    var t = r.Task_;
                    var d = t.Docs.FirstOrDefault(x => x.Key == oldKey);
                    if (d == null) continue;
                    lock (t)
                    {
                        d.Key = newKey;
                        t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["texto"] = "el documento se guardó como " + newKey + " (antes " + oldKey + "); la tarea sigue en el nuevo", ["quien"] = "revit" });
                    }
                    changed.Add(t);
                    n++;
                }
                foreach (var t in _store.List().Where(x => !_running.ContainsKey(x.Id) && !TaskState.IsFinal(x.State)))
                {
                    var d = t.Docs.FirstOrDefault(x => x.Key == oldKey);
                    if (d == null) continue;
                    d.Key = newKey;
                    t.Decisions.Add(new Dictionary<string, object> { ["t"] = TaskTime.Now(), ["texto"] = "el documento se guardó como " + newKey + " (antes " + oldKey + ")", ["quien"] = "revit" });
                    t.Rev++;
                    _store.Save(t);
                    changed.Add(t);
                    n++;
                }
            }
            foreach (var t in changed) Changed(t);
            return n;
        }

        /// <summary>Archiva una tarea detenida (mueve su carpeta a tareas\_archivo). No borra nada.</summary>
        public string Archive(string id)
        {
            lock (_lock)
            {
                if (_running.ContainsKey(id)) return "la tarea está corriendo; cancélela antes de archivarla";
                if (!_store.Exists(id)) return "no existe la tarea " + id;
                return _store.Archive(id);
            }
        }

        public List<TaskRecord> List() => _store.List().Select(t => Get(t.Id, out _)).Where(t => t != null).ToList();

        /// <summary>Filas de unidades (paginadas) para no mandar miles al agente.</summary>
        public List<object[]> UnitRows(string id, string state, int from, int count, out int total)
        {
            Get(id, out var units);
            var rows = units.Order.Select(k => units.Units[k]).Where(u => state == null || u.State == state).ToList();
            total = rows.Count;
            return rows.Skip(Math.Max(0, from)).Take(Math.Max(1, Math.Min(500, count))).Select(u => u.Row()).ToList();
        }
    }
}
