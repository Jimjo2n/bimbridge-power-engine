// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Tasks
{
    /// <summary>Task states (§3.2). The connection state is something else: a task continues even if the client disconnects.</summary>
    public static class TaskState
    {
        public const string Preparada = "preparada";
        public const string EnCola = "en_cola";
        public const string Ejecutando = "ejecutando";
        public const string EsperandoContexto = "esperando_contexto";
        public const string Aplicada = "aplicada";
        public const string AplicadaConDiferencias = "aplicada_con_diferencias";
        /// <summary>Stopped with some units committed and others failed or not done: what was committed stays applied and is listed.</summary>
        public const string Parcial = "parcial";
        /// <summary>The process that was running it is gone (Revit closed, add-in unloaded): resume reconciles and continues.</summary>
        public const string Interrumpida = "interrumpida";
        public const string CancelacionSolicitada = "cancelacion_solicitada";
        public const string Cancelada = "cancelada";

        public static bool IsFinal(string s) => s == Aplicada || s == AplicadaConDiferencias || s == Cancelada;
        public static bool IsActive(string s) => s == EnCola || s == Ejecutando || s == EsperandoContexto || s == CancelacionSolicitada;
    }

    /// <summary>States of a work unit.</summary>
    public static class UnitState
    {
        public const string Pendiente = "pendiente";
        /// <summary>Its start was recorded and its end is not on record (commit→journal window): verified on resume.</summary>
        public const string EnCurso = "en_curso";
        public const string Confirmada = "confirmada";
        /// <summary>Failed and was rolled back (confirmed): nothing was left; retried on resume.</summary>
        public const string Revertida = "revertida";
        /// <summary>Its preconditions are not met (someone changed the elements): not applied; replanned on resume.</summary>
        public const string Conflicto = "conflicto";
        /// <summary>Error with committed changes (own transactions): reconcile before continuing.</summary>
        public const string Parcial = "parcial";
        public const string Desconocida = "desconocida";
        /// <summary>The recipe decided to do nothing (already correct, declared exception).</summary>
        public const string Omitida = "omitida";

        public static bool IsDone(string s) => s == Confirmada || s == Omitida;
        public static bool NeedsReconcile(string s) => s == EnCurso || s == Parcial || s == Desconocida;
    }

    public sealed class TaskDoc
    {
        public string Key;
        public string Print;
        public string Generation;
        public string Handle;
        public string Title;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["clave"] = Key, ["huella"] = Print, ["generacion"] = Generation, ["handle"] = Handle, ["titulo"] = Title,
        };

        public static TaskDoc FromJson(Dictionary<string, object> o) => o == null ? null : new TaskDoc
        {
            Key = Arg.GetString(o, "clave"), Print = Arg.GetString(o, "huella"), Generation = Arg.GetString(o, "generacion"),
            Handle = Arg.GetString(o, "handle"), Title = Arg.GetString(o, "titulo"),
        };
    }

    /// <summary>Evidence that the document was persisted (saved or synced) after unit number <see cref="Seq"/>.</summary>
    public sealed class PersistEvidence
    {
        public string Kind; // guardar | sync | guardado_externo
        public string When;
        public long Seq;
        public string Generation;
        public string File;
        public long Bytes;
        public string Mtime;
        public string Sha;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["tipo"] = Kind, ["t"] = When, ["seq"] = Seq, ["generacion"] = Generation, ["archivo"] = File, ["bytes"] = Bytes, ["mtime"] = Mtime, ["sha256"] = Sha,
        };

        public static PersistEvidence FromJson(Dictionary<string, object> o) => o == null ? null : new PersistEvidence
        {
            Kind = Arg.GetString(o, "tipo"), When = Arg.GetString(o, "t"), Seq = Arg.GetLong(o, "seq") ?? 0, Generation = Arg.GetString(o, "generacion"),
            File = Arg.GetString(o, "archivo"), Bytes = Arg.GetLong(o, "bytes") ?? 0, Mtime = Arg.GetString(o, "mtime"), Sha = Arg.GetString(o, "sha256"),
        };
    }

    /// <summary>
    /// Persistent memory of a task (§3.3): target, scope, recipe and version, parameters, documents, progress, decisions,
    /// exceptions, artifacts, save evidence and continuation summary. Stored in tareas\&lt;id&gt;\tarea.json (atomic write);
    /// units are kept separately (unidades.jsonl) so thousands of rows are not rewritten at each checkpoint.
    /// </summary>
    public sealed class TaskRecord
    {
        public const int SchemaVersion = 1;

        public string Id;
        public string Objective;
        public string Scope;
        public string RecipeName;
        public string RecipeVersion;
        public string RecipeHash;
        public string RecipeSource;
        public string ContentHash;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<TaskDoc> Docs = new List<TaskDoc>();
        public string State = TaskState.Preparada;
        public string Reason;
        public string Created;
        public string Updated;
        public long Rev;
        public string Client;
        public string Owner;
        public long UnitsTotal;
        public long ConfirmSeq;
        public List<Dictionary<string, object>> Decisions = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Exceptions = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Artifacts = new List<Dictionary<string, object>>();
        public PersistEvidence LastPersist;
        public Dictionary<string, object> Policy = new Dictionary<string, object>(StringComparer.Ordinal);
        public Dictionary<string, object> Metrics = new Dictionary<string, object>(StringComparer.Ordinal);
        public Dictionary<string, object> Postcondition;
        public string Summary;
        public List<string> Clients = new List<string>();
        /// <summary>
        /// Last save/sync recorded in EACH generation of the document (generation → ISO UTC time). Allows safely inferring,
        /// after reopening, that a unit started after the last save of its generation was lost on close.
        /// </summary>
        public Dictionary<string, object> PersistedAt = new Dictionary<string, object>(StringComparer.Ordinal);

        public TaskDoc MainDoc => Docs.FirstOrDefault();

        /// <summary>In memory only: false if the last checkpoint did not reach disk (R07). Reported in status/summary.</summary>
        public bool Durable = true;

        /// <summary>In memory only (not serialized): the backend calls it on the Revit thread right before each unit's transaction.
        /// null = may run; text = do not run that unit or the following ones in the batch (R06, R03).</summary>
        public Func<string> Preflight;

        /// <summary>Records persistence evidence (own barrier or user save).</summary>
        public void NotePersist(PersistEvidence p)
        {
            if (p == null) return;
            LastPersist = p;
            if (p.Generation != null && p.When != null) PersistedAt[p.Generation] = p.When;
        }

        /// <summary>Was there a save in <paramref name="generation"/> at or after <paramref name="sinceIso"/>? null = unknown (no time).</summary>
        public bool? PersistedSince(string generation, string sinceIso)
        {
            if (generation == null || sinceIso == null) return null;
            if (!PersistedAt.TryGetValue(generation, out var v) || v == null) return false;
            if (!DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
            if (!DateTime.TryParse(sinceIso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since)) return null;
            return at >= since;
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["esquema"] = (long)SchemaVersion,
            ["id"] = Id, ["objetivo"] = Objective, ["alcance"] = Scope,
            ["receta"] = new Dictionary<string, object> { ["nombre"] = RecipeName, ["version"] = RecipeVersion, ["hash"] = RecipeHash, ["origen"] = RecipeSource },
            ["huella"] = ContentHash,
            ["args"] = Args,
            ["docs"] = Docs.Select(d => (object)d.ToJson()).ToList(),
            ["estado"] = State, ["motivo"] = Reason,
            ["creada"] = Created, ["actualizada"] = Updated, ["rev"] = Rev,
            ["cliente"] = Client, ["clientes"] = Clients.Cast<object>().ToList(), ["instancia"] = Owner,
            ["unidades_total"] = UnitsTotal, ["seq_confirmacion"] = ConfirmSeq,
            ["decisiones"] = Decisions.Cast<object>().ToList(),
            ["excepciones"] = Exceptions.Cast<object>().ToList(),
            ["artefactos"] = Artifacts.Cast<object>().ToList(),
            ["ultimo_guardado"] = LastPersist?.ToJson(),
            ["guardado_por_generacion"] = PersistedAt,
            ["politica"] = Policy, ["metricas"] = Metrics,
            ["postcondicion"] = Postcondition,
            ["resumen"] = Summary,
        };

        public static TaskRecord FromJson(Dictionary<string, object> o)
        {
            var r = new TaskRecord
            {
                Id = Arg.GetString(o, "id"), Objective = Arg.GetString(o, "objetivo"), Scope = Arg.GetString(o, "alcance"),
                ContentHash = Arg.GetString(o, "huella"), State = Arg.GetString(o, "estado") ?? TaskState.Preparada, Reason = Arg.GetString(o, "motivo"),
                Created = Arg.GetString(o, "creada"), Updated = Arg.GetString(o, "actualizada"), Rev = Arg.GetLong(o, "rev") ?? 0,
                Client = Arg.GetString(o, "cliente"), Owner = Arg.GetString(o, "instancia"),
                UnitsTotal = Arg.GetLong(o, "unidades_total") ?? 0, ConfirmSeq = Arg.GetLong(o, "seq_confirmacion") ?? 0,
                Summary = Arg.GetString(o, "resumen"),
            };
            if (o.TryGetValue("receta", out var rv) && rv is Dictionary<string, object> rd)
            {
                r.RecipeName = Arg.GetString(rd, "nombre"); r.RecipeVersion = Arg.GetString(rd, "version");
                r.RecipeHash = Arg.GetString(rd, "hash"); r.RecipeSource = Arg.GetString(rd, "origen");
            }
            if (o.TryGetValue("args", out var av) && av is Dictionary<string, object> ad) r.Args = ad;
            if (o.TryGetValue("docs", out var dv) && dv is List<object> dl) r.Docs = dl.OfType<Dictionary<string, object>>().Select(TaskDoc.FromJson).ToList();
            r.Decisions = Dicts(o, "decisiones");
            r.Exceptions = Dicts(o, "excepciones");
            r.Artifacts = Dicts(o, "artefactos");
            r.Clients = Arg.GetStringList(o, "clientes");
            if (o.TryGetValue("ultimo_guardado", out var lp) && lp is Dictionary<string, object> lpd) r.LastPersist = PersistEvidence.FromJson(lpd);
            if (o.TryGetValue("guardado_por_generacion", out var gv) && gv is Dictionary<string, object> gd) r.PersistedAt = gd;
            if (o.TryGetValue("politica", out var pv) && pv is Dictionary<string, object> pd) r.Policy = pd;
            if (o.TryGetValue("metricas", out var mv) && mv is Dictionary<string, object> md) r.Metrics = md;
            if (o.TryGetValue("postcondicion", out var qv) && qv is Dictionary<string, object> qd) r.Postcondition = qd;
            return r;
        }

        private static List<Dictionary<string, object>> Dicts(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v is List<object> l ? l.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();

        public long Metric(string name) => Metrics.TryGetValue(name, out var v) && v is long l ? l : v is double d ? (long)d : 0;
        public void AddMetric(string name, long delta) => Metrics[name] = Metric(name) + delta;
        public void SetMetric(string name, long value) => Metrics[name] = value;

        public long PolicyLong(string name, long def)
        {
            var v = Arg.GetLong(Policy, name);
            return v ?? def;
        }

        public string PolicyString(string name) => Arg.GetString(Policy, name);
    }

    /// <summary>A work unit as the engine sees it: stable key + type (unit or barrier).</summary>
    public sealed class UnitRef
    {
        public string Key;
        /// <summary>null = normal unit; otherwise the barrier (guardar, sync, pdf, cargar…).</summary>
        public string Barrier;
        public Dictionary<string, object> BarrierArgs;
        public int Ordinal;

        public bool IsBarrier => Barrier != null;
        public override string ToString() => Key;
    }

    /// <summary>Result of running or verifying a unit.</summary>
    public sealed class UnitOutcome
    {
        public string Key;
        public string State;
        public long Ms;
        /// <summary>Created elements: [id, UniqueId] (to verify after reopening and avoid duplicates).</summary>
        public List<object[]> Created = new List<object[]>();
        public long Changed;
        public long Deleted;
        public object Result;
        public string Error;
        public List<string> Decisions = new List<string>();
        public List<Dictionary<string, object>> Exceptions = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Artifacts = new List<Dictionary<string, object>>();
        public PersistEvidence Persist;

        public static UnitOutcome Of(string key, string state, string error = null) => new UnitOutcome { Key = key, State = state, Error = error };
    }

    /// <summary>Known state of a unit (rebuilt from unidades.jsonl).</summary>
    public sealed class UnitRecord
    {
        public string Key;
        public string State = UnitState.Pendiente;
        public long Seq;
        public int Attempts;
        public string Error;
        public long Ms;
        public List<object[]> Created = new List<object[]>();
        public string Generation;
        public object Result;
        public string When;
        /// <summary>Time of the last "start" (not overwritten by the end): to know whether a later save could include it.</summary>
        public string Started;

        public object[] Row() => new object[] { Key, State, (long)Attempts, Ms, Error ?? "", (long)Created.Count };
    }

    /// <summary>Response of a backend step: a value, a recoverable wait (context) or a failure that stops the task.</summary>
    public sealed class BackendResult<T>
    {
        public T Value;
        /// <summary>Wait and retry (Revit busy, user pause, document closed…). The task keeps its progress.</summary>
        public string Wait;
        /// <summary>Concrete action that needs a person (close a dialog, open the document…).</summary>
        public string Action;
        /// <summary>Stops the task (invalid recipe, replaced document…).</summary>
        public string Fatal;

        public static BackendResult<T> Ok(T v) => new BackendResult<T> { Value = v };
        public static BackendResult<T> Waiting(string why, string action = null) => new BackendResult<T> { Wait = why, Action = action };
        public static BackendResult<T> Fail(string why) => new BackendResult<T> { Fatal = why };
    }

    /// <summary>
    /// What the engine needs from the Revit side. The add-in implements it with the Revit thread (ExternalEvent); tests, with a
    /// fake model with fault injection. The engine never touches the Revit API: it only decides order, batches, checkpoints and states.
    /// </summary>
    public interface ITaskBackend
    {
        /// <summary>Valid context to continue (no pause, document open, same generation). null = yes; otherwise the reason.</summary>
        BackendResult<bool> CheckContext(TaskRecord task);

        /// <summary>Recipe units, in order and with stable keys (deterministic over the same model).</summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, System.Threading.CancellationToken ct);

        /// <summary>
        /// Runs at most <paramref name="units"/>.Count units in ONE visit to the Revit thread, each in its own transaction,
        /// stopping before exceeding <paramref name="budgetMs"/>. Returns the result of those it ran (a prefix).
        /// </summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, System.Threading.CancellationToken ct);

        System.Threading.Tasks.Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, System.Threading.CancellationToken ct);

        /// <summary>Verifies doubtful units in the model (in progress, partial, or committed after the last save if it was reopened).</summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, System.Threading.CancellationToken ct);

        /// <summary>Final recipe QC (read-only). A non-empty "desviaciones" list leaves the task aplicada_con_diferencias.</summary>
        System.Threading.Tasks.Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, System.Threading.CancellationToken ct);

        /// <summary>Current generation of the main document (instance + opening), or null if it is not open.</summary>
        string CurrentGeneration(TaskRecord task);
    }

    internal static class TaskTime
    {
        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
