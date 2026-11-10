// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Trabajos largos (geometría sobre cientos de elementos). La petición espera unos segundos; si no terminó,
    /// devuelve job_id y el trabajo sigue dentro de Revit por tramos de 250 ms. Así nunca se agota el plazo del
    /// puente (pyRevit: 4 timeouts que costaron 31,9 min) y Revit sigue respondiendo entre tramos.
    /// </summary>
    public sealed class Job
    {
        public string Id;
        public string SessionId;
        /// <summary>Tarea autorizada (tarea=…). Un trabajo con tarea sigue aunque se corte la conexión y otra conexión de la misma tarea puede consultarlo.</summary>
        public string Task;
        public string Kind;
        public string Status = JobStatus.EnProceso;
        public DateTime CreatedUtc;
        public DateTime? FinishedUtc;
        public Dictionary<string, object> Result;
        public string Error;
        public string ErrorStatus;
        public CancellationTokenSource Cts;
        /// <summary>Avance que actualiza el trabajo desde el hilo de Revit.</summary>
        public volatile int Done;
        public volatile int Total;
        public volatile string Phase;
        /// <summary>Conjuntos que el trabajo guardó al terminar (se vuelven a guardar en la sesión al entregarlos).</summary>
        public Dictionary<string, long[]> Sets = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        public bool Delivered;
        /// <summary>Error al registrar el final (p. ej. diario no escribible): se avisa en la respuesta.</summary>
        public volatile string FinalizeError;
        /// <summary>Revisión del modelo con que se calculó el resultado.</summary>
        public long Rev;
        /// <summary>La tarea que hace el trabajo (para esperar su fin sin sondear).</summary>
        public System.Threading.Tasks.Task Work;

        public bool IsFinal => Status != JobStatus.EnProceso;

        /// <summary>Estado de respuesta al terminar (ok, completado_con_diferencias, aplicado_error_resultado, parcial…).</summary>
        public string FinalEstado;

        /// <summary>Estado que ve la IA: en_proceso mientras corre; al terminar, el estado real (nunca "ok" para un fallo).</summary>
        public string Estado => Status == JobStatus.EnProceso ? PDI.RevitMcp.Protocol.Status.EnProceso
            : Status == JobStatus.Listo ? (FinalEstado ?? PDI.RevitMcp.Protocol.Status.Ok)
            : (ErrorStatus ?? (Status == JobStatus.Cancelado ? PDI.RevitMcp.Protocol.Status.Cancelado : PDI.RevitMcp.Protocol.Status.Error));

        internal List<Action<Job>> FinalHandlers = new List<Action<Job>>();

        public Dictionary<string, object> Progress()
        {
            var d = new Dictionary<string, object> { ["job_id"] = Id, ["tipo"] = Kind, ["estado_job"] = Status };
            if (!IsFinal)
            {
                if (Total > 0) d["progreso"] = Done + "/" + Total;
                if (!string.IsNullOrEmpty(Phase)) d["fase"] = Phase;
                d["segundos"] = (long)(DateTime.UtcNow - CreatedUtc).TotalSeconds;
            }
            return d;
        }
    }

    public static class JobStatus
    {
        public const string EnProceso = "en_proceso";
        public const string Listo = "listo";
        public const string Error = "error";
        public const string Cancelado = "cancelado";
    }

    public sealed class JobStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _now;
        private int _counter;

        public const int MaxKept = 64;

        public JobStore(Func<DateTime> now = null) { _now = now ?? (() => DateTime.UtcNow); }

        public Job Create(string sessionId, string kind)
        {
            lock (_lock)
            {
                _counter++;
                var j = new Job
                {
                    Id = "G-" + _now().ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + _counter.ToString("D3", CultureInfo.InvariantCulture),
                    SessionId = sessionId,
                    Kind = kind,
                    CreatedUtc = _now(),
                    Cts = new CancellationTokenSource(),
                };
                _jobs[j.Id] = j;
                Prune();
                return j;
            }
        }

        public Job Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock) return _jobs.TryGetValue(id.Trim(), out var j) ? j : null;
        }

        public void Complete(Job j, Dictionary<string, object> result, string estado = null)
        {
            lock (_lock)
            {
                if (j.IsFinal) return;
                j.Result = result;
                j.FinalEstado = estado;
                j.Status = JobStatus.Listo;
                j.FinishedUtc = _now();
            }
            FireFinal(j);
        }

        public void Fail(Job j, string status, string error)
        {
            lock (_lock)
            {
                if (j.IsFinal) return;
                j.Status = status == JobStatus.Cancelado ? JobStatus.Cancelado : JobStatus.Error;
                j.ErrorStatus = status == JobStatus.Cancelado ? PDI.RevitMcp.Protocol.Status.Cancelado : status;
                j.Error = error;
                j.FinishedUtc = _now();
            }
            FireFinal(j);
        }

        /// <summary>
        /// Único punto de finalización (H02): <paramref name="handler"/> corre una vez cuando el trabajo termina de verdad
        /// (bien, con error, cancelado o por una excepción no observada del worker). Si ya terminó, corre ahora.
        /// </summary>
        public void WhenFinal(Job j, Action<Job> handler)
        {
            var now = false;
            lock (_lock)
            {
                if (j.IsFinal) now = true;
                else j.FinalHandlers.Add(handler);
            }
            if (now) SafeInvoke(handler, j);
        }

        private void FireFinal(Job j)
        {
            List<Action<Job>> hs;
            lock (_lock) { hs = j.FinalHandlers; j.FinalHandlers = new List<Action<Job>>(); }
            foreach (var h in hs) SafeInvoke(h, j);
        }

        /// <summary>Errores de los manejadores finales (p. ej. el diario no se pudo escribir); se muestran en diagnóstico.</summary>
        public readonly List<string> FinalHandlerErrors = new List<string>();

        private void SafeInvoke(Action<Job> h, Job j)
        {
            try { h(j); }
            catch (Exception ex)
            {
                lock (_lock) { if (FinalHandlerErrors.Count < 50) FinalHandlerErrors.Add(j.Id + ": " + ex.Message); }
                j.FinalizeError = ex.Message;
            }
        }

        /// <summary>
        /// Lanza el cuerpo del trabajo y garantiza un estado final: si el cuerpo termina sin completar ni fallar el trabajo, o lanza una
        /// excepción, el trabajo queda en error con el motivo. Nunca queda "en_proceso" para siempre.
        /// Issue #7: devuelve void a propósito. El trabajo es supervisado (la continuación observa cualquier excepción) y quien
        /// quiera esperarlo usa <see cref="Job.Work"/> o <see cref="WhenFinal"/>; así no hay tareas sin observar (CS4014) en los llamadores.
        /// </summary>
        public void Run(Job j, Func<Task> body)
        {
            var work = Task.Run(body);
            j.Work = work.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var ex = t.Exception?.GetBaseException();
                    Fail(j, ex is OperationCanceledException ? JobStatus.Cancelado : PDI.RevitMcp.Protocol.Status.Desconocido,
                        "el trabajo terminó por una excepción no controlada (" + (ex?.GetType().Name ?? "?") + ": " + ex?.Message +
                        "); no se sabe qué quedó aplicado: reconcilie antes de repetir");
                }
                else if (t.IsCanceled) Fail(j, JobStatus.Cancelado, "cancelado");
                else if (!j.IsFinal) Fail(j, PDI.RevitMcp.Protocol.Status.Desconocido, "el trabajo terminó sin registrar su resultado; reconcilie antes de repetir");
            }, TaskScheduler.Default);
        }

        public bool Cancel(Job j)
        {
            if (j == null || j.IsFinal) return false;
            try { j.Cts.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        /// <summary>Cancela los trabajos en curso de una sesión que se desconectó (nadie leerá el resultado).</summary>
        public int CancelSession(string sessionId)
        {
            List<Job> list;
            // Los trabajos de una tarea autorizada no se abandonan al desconectar (R023): siguen y se reclaman con job id tarea=.
            lock (_lock) list = _jobs.Values.Where(j => j.SessionId == sessionId && !j.IsFinal && string.IsNullOrEmpty(j.Task)).ToList();
            foreach (var j in list) Cancel(j);
            return list.Count;
        }

        public List<Job> ForSession(string sessionId, string task = null)
        {
            lock (_lock) return _jobs.Values.Where(j => j.SessionId == sessionId || (task != null && j.Task == task)).OrderByDescending(j => j.CreatedUtc).ToList();
        }

        /// <summary>¿Puede esta conexión ver el trabajo? La misma sesión, o la misma tarea autorizada.</summary>
        public static bool Visible(Job j, string sessionId, string task) =>
            j != null && (j.SessionId == sessionId || (!string.IsNullOrEmpty(task) && j.Task == task));

        public int Running
        {
            get { lock (_lock) return _jobs.Values.Count(j => !j.IsFinal); }
        }

        private void Prune()
        {
            if (_jobs.Count <= MaxKept) return;
            foreach (var old in _jobs.Values.Where(j => j.IsFinal).OrderBy(j => j.CreatedUtc).Take(_jobs.Count - MaxKept).ToList())
                _jobs.Remove(old.Id);
        }
    }

    /// <summary>
    /// Resultados ya calculados, por documento: si el modelo no cambió (misma rev) y los vínculos son los mismos,
    /// repetir una revisión cuesta 0 ms en Revit. En pyRevit se repetían "pass 2 / verificación final" (29,5 min en reintentos).
    /// </summary>
    public sealed class ResultCache
    {
        private readonly object _lock = new object();
        private readonly LinkedList<KeyValuePair<string, Entry>> _lru = new LinkedList<KeyValuePair<string, Entry>>();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>>(StringComparer.Ordinal);
        private readonly int _max;

        public sealed class Entry
        {
            public Dictionary<string, object> Result;
            /// <summary>Datos crudos (filas) para volver a resumir con otra página, unidades o filtro sin recalcular.</summary>
            public object Payload;
            public Dictionary<string, long[]> Sets;
            public long RevitMs;
            public DateTime CreatedUtc;
        }

        public ResultCache(int max = 48) { _max = max; }

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        public static string Key(string docKey, long rev, string signature, string recipe, IDictionary<string, object> args)
        {
            var parts = args == null ? new List<string>() :
                args.Where(kv => !kv.Key.StartsWith("_", StringComparison.Ordinal) && kv.Key != "save_as" && kv.Key != "page" && kv.Key != "nuevo")
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + "=" + Protocol.Json.Write(kv.Value)).ToList();
            return (docKey ?? "") + "|" + rev.ToString(CultureInfo.InvariantCulture) + "|" + (signature ?? "") + "|" + recipe + "|" + string.Join("&", parts);
        }

        public bool TryGet(string key, out Entry e)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    e = node.Value.Value;
                    Hits++;
                    return true;
                }
                Misses++;
                e = null;
                return false;
            }
        }

        public void Put(string key, Entry e)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var old)) { _lru.Remove(old); _map.Remove(key); }
                var node = _lru.AddFirst(new KeyValuePair<string, Entry>(key, e));
                _map[key] = node;
                while (_lru.Count > _max)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Clear()
        {
            lock (_lock) { _lru.Clear(); _map.Clear(); }
        }

        public int Count { get { lock (_lock) return _map.Count; } }
    }
}
