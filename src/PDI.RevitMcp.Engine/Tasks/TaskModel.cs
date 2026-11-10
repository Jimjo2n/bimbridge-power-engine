// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>Estados de una tarea (§3.2). El estado de la conexión es otra cosa: una tarea sigue aunque el cliente se desconecte.</summary>
    public static class TaskState
    {
        public const string Preparada = "preparada";
        public const string EnCola = "en_cola";
        public const string Ejecutando = "ejecutando";
        public const string EsperandoContexto = "esperando_contexto";
        public const string Aplicada = "aplicada";
        public const string AplicadaConDiferencias = "aplicada_con_diferencias";
        /// <summary>Se detuvo con unidades confirmadas y otras fallidas o sin hacer: lo confirmado sigue aplicado y se lista.</summary>
        public const string Parcial = "parcial";
        /// <summary>El proceso que la corría ya no está (Revit cerrado, add-in descargado): reanudar reconcilia y continúa.</summary>
        public const string Interrumpida = "interrumpida";
        public const string CancelacionSolicitada = "cancelacion_solicitada";
        public const string Cancelada = "cancelada";

        public static bool IsFinal(string s) => s == Aplicada || s == AplicadaConDiferencias || s == Cancelada;
        public static bool IsActive(string s) => s == EnCola || s == Ejecutando || s == EsperandoContexto || s == CancelacionSolicitada;
    }

    /// <summary>Estados de una unidad de trabajo.</summary>
    public static class UnitState
    {
        public const string Pendiente = "pendiente";
        /// <summary>Se registró su inicio y no consta su final (ventana commit→diario): se verifica al reanudar.</summary>
        public const string EnCurso = "en_curso";
        public const string Confirmada = "confirmada";
        /// <summary>Falló y se revirtió (confirmado): no quedó nada; se reintenta al reanudar.</summary>
        public const string Revertida = "revertida";
        /// <summary>Sus precondiciones no se cumplen (alguien cambió los elementos): no se aplicó; se replanifica al reanudar.</summary>
        public const string Conflicto = "conflicto";
        /// <summary>Error con cambios confirmados (transacciones propias): reconciliar antes de seguir.</summary>
        public const string Parcial = "parcial";
        public const string Desconocida = "desconocida";
        /// <summary>La receta decidió no hacer nada (ya estaba bien, excepción declarada).</summary>
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

    /// <summary>Evidencia de que el documento quedó persistido (guardado o sincronizado) tras la unidad número <see cref="Seq"/>.</summary>
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
    /// Memoria persistente de una tarea (§3.3): objetivo, alcance, receta y versión, parámetros, documentos, progreso, decisiones,
    /// excepciones, artefactos, evidencia de guardado y resumen de continuación. Se guarda en tareas\&lt;id&gt;\tarea.json (escritura atómica);
    /// las unidades van aparte (unidades.jsonl) para no reescribir miles de filas en cada checkpoint.
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
        /// Último guardado/sincronización registrado en CADA generación del documento (generación → hora UTC ISO). Permite inferir con
        /// seguridad, tras reabrir, que una unidad empezada después del último guardado de su generación se perdió con el cierre.
        /// </summary>
        public Dictionary<string, object> PersistedAt = new Dictionary<string, object>(StringComparer.Ordinal);

        public TaskDoc MainDoc => Docs.FirstOrDefault();

        /// <summary>Sólo en memoria: false si el último checkpoint no llegó a disco (R07). Se informa en estado/resumen.</summary>
        public bool Durable = true;

        /// <summary>Sólo en memoria (no se serializa): el backend la llama en el hilo de Revit justo antes de la transacción de cada unidad.
        /// null = puede ejecutar; texto = no ejecutar esa unidad ni las siguientes del lote (R06, R03).</summary>
        public Func<string> Preflight;

        /// <summary>Registra una evidencia de persistencia (barrera propia o guardado del usuario).</summary>
        public void NotePersist(PersistEvidence p)
        {
            if (p == null) return;
            LastPersist = p;
            if (p.Generation != null && p.When != null) PersistedAt[p.Generation] = p.When;
        }

        /// <summary>¿Hubo un guardado en <paramref name="generation"/> en o después de <paramref name="sinceIso"/>? null = no se sabe (sin hora).</summary>
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

    /// <summary>Una unidad de trabajo tal como la ve el motor: clave estable + tipo (unidad o barrera).</summary>
    public sealed class UnitRef
    {
        public string Key;
        /// <summary>null = unidad normal; si no, la barrera (guardar, sync, pdf, cargar…).</summary>
        public string Barrier;
        public Dictionary<string, object> BarrierArgs;
        public int Ordinal;

        public bool IsBarrier => Barrier != null;
        public override string ToString() => Key;
    }

    /// <summary>Resultado de ejecutar o verificar una unidad.</summary>
    public sealed class UnitOutcome
    {
        public string Key;
        public string State;
        public long Ms;
        /// <summary>Elementos creados: [id, UniqueId] (para verificar tras reabrir y evitar duplicados).</summary>
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

    /// <summary>Estado conocido de una unidad (reconstruido desde unidades.jsonl).</summary>
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
        /// <summary>Hora del último "inicio" (no se sobrescribe con el final): para saber si un guardado posterior pudo incluirla.</summary>
        public string Started;

        public object[] Row() => new object[] { Key, State, (long)Attempts, Ms, Error ?? "", (long)Created.Count };
    }

    /// <summary>Respuesta de un paso del backend: un valor, una espera recuperable (contexto) o un fallo que detiene la tarea.</summary>
    public sealed class BackendResult<T>
    {
        public T Value;
        /// <summary>Esperar y reintentar (Revit ocupado, pausa del usuario, documento cerrado…). La tarea conserva su avance.</summary>
        public string Wait;
        /// <summary>Acción concreta que necesita una persona (cerrar un diálogo, abrir el documento…).</summary>
        public string Action;
        /// <summary>Detiene la tarea (receta inválida, documento sustituido…).</summary>
        public string Fatal;

        public static BackendResult<T> Ok(T v) => new BackendResult<T> { Value = v };
        public static BackendResult<T> Waiting(string why, string action = null) => new BackendResult<T> { Wait = why, Action = action };
        public static BackendResult<T> Fail(string why) => new BackendResult<T> { Fatal = why };
    }

    /// <summary>
    /// Lo que el motor necesita del lado de Revit. El add-in lo implementa con el hilo de Revit (ExternalEvent); las pruebas, con un
    /// modelo falso con inyección de fallos. El motor nunca toca la API de Revit: sólo decide orden, lotes, checkpoints y estados.
    /// </summary>
    public interface ITaskBackend
    {
        /// <summary>Contexto válido para seguir (sin pausa, documento abierto, misma generación). null = sí; si no, el motivo.</summary>
        BackendResult<bool> CheckContext(TaskRecord task);

        /// <summary>Unidades de la receta, en orden y con claves estables (determinista sobre el mismo modelo).</summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, System.Threading.CancellationToken ct);

        /// <summary>
        /// Ejecuta como máximo <paramref name="units"/>.Count unidades en UNA visita al hilo de Revit, cada una en su propia transacción,
        /// parando antes de pasarse de <paramref name="budgetMs"/>. Devuelve el resultado de las que ejecutó (un prefijo).
        /// </summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, System.Threading.CancellationToken ct);

        System.Threading.Tasks.Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, System.Threading.CancellationToken ct);

        /// <summary>Verifica en el modelo unidades dudosas (en curso, parciales, o confirmadas tras el último guardado si se reabrió).</summary>
        System.Threading.Tasks.Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, System.Threading.CancellationToken ct);

        /// <summary>QC final de la receta (sólo lectura). Una lista "desviaciones" no vacía deja la tarea aplicada_con_diferencias.</summary>
        System.Threading.Tasks.Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, System.Threading.CancellationToken ct);

        /// <summary>Generación actual del documento principal (instancia + apertura), o null si no está abierto.</summary>
        string CurrentGeneration(TaskRecord task);
    }

    internal static class TaskTime
    {
        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
