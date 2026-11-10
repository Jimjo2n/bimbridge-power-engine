// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>
    /// Persistencia de tareas en disco (no depende de la conversación ni del objeto Session): tareas\&lt;id&gt;\
    ///   tarea.json      memoria de la tarea (escritura atómica en cada checkpoint);
    ///   receta.cs       copia exacta del código con que empezó (reanudar usa ESTA copia, nunca el archivo modificado después);
    ///   unidades.jsonl  inicio/fin/verificación de cada unidad (durable, sólo-anexar);
    ///   lease.json      quién la está corriendo (instancia + vencimiento) para que dos Revit o dos clientes no la corran a la vez;
    ///   artefactos\     informes y salidas con su hash.
    /// </summary>
    public sealed class TaskStore
    {
        private readonly string _root;
        private readonly Dictionary<string, JsonlLog> _logs = new Dictionary<string, JsonlLog>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public TaskStore(string root = null) { _root = root ?? Home.Sub("tareas"); }

        public string Root => _root;
        public string Dir(string id) => Path.Combine(_root, SafeId(id));
        public string ArtifactsDir(string id) => Path.Combine(Dir(id), "artefactos");

        public static string SafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("tarea: falta el identificador");
            var t = id.Trim();
            if (t.Length > 64) throw new ArgumentException("tarea: identificador de hasta 64 caracteres");
            foreach (var c in t)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) throw new ArgumentException("tarea: el identificador admite letras, números, _ - .");
            if (t.Contains("..")) throw new ArgumentException("tarea: identificador inválido");
            return t;
        }

        public static string NewId(DateTime now) =>
            "T" + now.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);

        public bool Exists(string id) => File.Exists(Path.Combine(Dir(id), "tarea.json"));

        public void Create(TaskRecord r, string recipeCode)
        {
            var dir = Dir(r.Id);
            if (Exists(r.Id)) throw new InvalidOperationException("la tarea " + r.Id + " ya existe");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(ArtifactsDir(r.Id));
            if (recipeCode != null) Home.WriteAtomic(Path.Combine(dir, "receta.cs"), recipeCode);
            Save(r);
        }

        /// <summary>Sólo pruebas: inyección de fallos antes de escribir tarea.json (R07).</summary>
        public Action<string> FaultBeforeSave;

        public void Save(TaskRecord r)
        {
            FaultBeforeSave?.Invoke(r.Id);
            r.Updated = TaskTime.Now();
            Home.WriteAtomic(Path.Combine(Dir(r.Id), "tarea.json"), Json.Write(r.ToJson()));
        }

        public TaskRecord Load(string id)
        {
            var p = Path.Combine(Dir(id), "tarea.json");
            if (!File.Exists(p)) return null;
            return TaskRecord.FromJson(Json.ParseObject(File.ReadAllText(p)));
        }

        public string RecipeCode(string id)
        {
            var p = Path.Combine(Dir(id), "receta.cs");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        public List<TaskRecord> List()
        {
            var list = new List<TaskRecord>();
            if (!Directory.Exists(_root)) return list;
            foreach (var d in Directory.GetDirectories(_root))
            {
                try
                {
                    var r = Load(Path.GetFileName(d));
                    if (r != null) list.Add(r);
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException) { }
            }
            return list.OrderByDescending(r => r.Updated ?? r.Created ?? "").ToList();
        }

        private JsonlLog Log(string id)
        {
            lock (_lock)
            {
                if (!_logs.TryGetValue(id, out var l))
                {
                    l = new JsonlLog(Path.Combine(Dir(id), "unidades.jsonl"));
                    _logs[id] = l;
                }
                return l;
            }
        }

        /// <summary>Anexa un evento de unidad (durable). Lanza JournalUnavailableException si no puede escribir.</summary>
        public void AppendUnitEvent(string id, Dictionary<string, object> ev)
        {
            var others = new List<Dictionary<string, object>>();
            Log(id).Append(ev, others);
        }

        /// <summary>Estado de todas las unidades conocidas, en el orden en que aparecieron.</summary>
        public UnitLog LoadUnits(string id)
        {
            var log = new UnitLog();
            var path = Path.Combine(Dir(id), "unidades.jsonl");
            if (!File.Exists(path)) return log;
            var reader = new JsonlLog(path);
            foreach (var ev in reader.ReadNew()) log.Apply(ev);
            log.CorruptLines = reader.CorruptLines;
            return log;
        }

        /// <summary>Mueve la carpeta de la tarea a _archivo\ (no borra). null = bien; si no, el motivo.</summary>
        public string Archive(string id)
        {
            try
            {
                var dest = Path.Combine(_root, "_archivo", SafeId(id) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Directory.Move(Dir(id), dest);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return "no se pudo archivar: " + ex.Message; }
        }

        public string ArtifactPath(string id, string name) => Path.Combine(ArtifactsDir(id), name);

        // ---------------- lease ----------------

        public sealed class Lease
        {
            public string Owner;
            public string Instance;
            public string Expires;
            /// <summary>Token de cercado monotónico: sube en cada adquisición nueva (otro dueño, vencida o liberada). 0 = lease.json antiguo sin campo.</summary>
            public long Fencing;
            public bool Released;

            public bool Valid(DateTime now) =>
                !Released && DateTime.TryParse(Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) && e > now;
        }

        /// <summary>Error explícito del almacén de leases (bloqueo no obtenido, archivo ilegible o no escribible). Nunca se interpreta como "libre".</summary>
        public sealed class LeaseStoreException : Exception
        {
            public LeaseStoreException(string m, Exception inner = null) : base(m, inner) { }
        }

        public static TimeSpan LeaseLockTimeout = TimeSpan.FromSeconds(10);

        public Lease ReadLease(string id)
        {
            try { return ReadLeaseStrict(id); } catch (LeaseStoreException) { return null; }
        }

        /// <summary>Lee lease.json; null si no existe; excepción si existe pero está corrupto o es ilegible (fail-safe).</summary>
        private Lease ReadLeaseStrict(string id)
        {
            var p = Path.Combine(Dir(id), "lease.json");
            try
            {
                if (!File.Exists(p)) return null;
                var o = Json.ParseObject(File.ReadAllText(p));
                long f = 0;
                if (o.TryGetValue("fencing", out var fv) && fv != null) f = Convert.ToInt64(fv, CultureInfo.InvariantCulture);
                bool rel = o.TryGetValue("liberada", out var rv) && rv is bool rb && rb;
                return new Lease { Owner = Arg.GetString(o, "dueno"), Instance = Arg.GetString(o, "instancia"), Expires = Arg.GetString(o, "vence"), Fencing = f, Released = rel };
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is UnauthorizedAccessException || ex is InvalidCastException || ex is OverflowException)
            { throw new LeaseStoreException("lease.json ilegible: " + ex.Message, ex); }
        }

        private void WriteLease(string id, Lease l)
        {
            try
            {
                Home.WriteAtomic(Path.Combine(Dir(id), "lease.json"), Json.Write(new Dictionary<string, object>
                { ["dueno"] = l.Owner, ["instancia"] = l.Instance, ["vence"] = l.Expires, ["fencing"] = l.Fencing, ["liberada"] = l.Released }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new LeaseStoreException("lease.json no escribible: " + ex.Message, ex); }
        }

        /// <summary>Exclusión entre procesos de la misma máquina: archivo lease.lock abierto con FileShare.None durante leer-comprobar-escribir.</summary>
        private T WithLeaseLock<T>(string id, Func<T> body)
        {
            lock (_lock)
            {
                var lockPath = Path.Combine(Dir(id), "lease.lock");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                FileStream fs = null;
                var rnd = new Random();
                while (fs == null)
                {
                    try
                    {
                        Directory.CreateDirectory(Dir(id));
                        fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException ex)
                    {
                        if (sw.Elapsed > LeaseLockTimeout) throw new LeaseStoreException("bloqueo de lease no obtenido en " + LeaseLockTimeout.TotalSeconds + " s", ex);
                        System.Threading.Thread.Sleep(5 + rnd.Next(20));
                    }
                    catch (UnauthorizedAccessException ex) { throw new LeaseStoreException("bloqueo de lease sin permiso: " + ex.Message, ex); }
                }
                using (fs) return body();
            }
        }

        /// <summary>Toma o renueva. Renovación del mismo dueño vigente conserva el fencing; cualquier otra adquisición lo sube.
        /// false + titular si otro dueño la tiene vigente. LeaseStoreException si el almacén no es fiable.</summary>
        public bool TryLease(string id, string owner, string instance, TimeSpan ttl, DateTime now, out Lease holder)
        {
            Lease h = null;
            var ok = WithLeaseLock(id, () =>
            {
                var cur = ReadLeaseStrict(id);
                if (cur != null && cur.Owner != owner && cur.Valid(now)) { h = cur; return false; }
                bool renewal = cur != null && cur.Owner == owner && cur.Valid(now);
                var l = new Lease
                {
                    Owner = owner, Instance = instance,
                    Expires = (now + ttl).ToString("o", CultureInfo.InvariantCulture),
                    Fencing = renewal ? cur.Fencing : (cur?.Fencing ?? 0) + 1,
                };
                WriteLease(id, l);
                h = l;
                return true;
            });
            holder = h;
            return ok;
        }

        /// <summary>Renueva sólo si sigue siendo dueño con el mismo fencing y vigente. Nunca re-adquiere.</summary>
        public bool RenewLease(string id, string owner, long fencing, TimeSpan ttl, DateTime now)
        {
            return WithLeaseLock(id, () =>
            {
                var cur = ReadLeaseStrict(id);
                if (cur == null || cur.Owner != owner || cur.Fencing != fencing || !cur.Valid(now)) return false;
                cur.Expires = (now + ttl).ToString("o", CultureInfo.InvariantCulture);
                WriteLease(id, cur);
                return true;
            });
        }

        /// <summary>Punto de autoridad: ¿puede este dueño con este fencing escribir ahora? Ante error del almacén, false.</summary>
        public bool ValidateOwnership(string id, string owner, long fencing, DateTime now)
        {
            try
            {
                return WithLeaseLock(id, () =>
                {
                    var cur = ReadLeaseStrict(id);
                    return cur != null && cur.Owner == owner && cur.Fencing == fencing && cur.Valid(now);
                });
            }
            catch (LeaseStoreException) { return false; }
        }

        public Lease GetCurrentLease(string id) => WithLeaseLock(id, () => ReadLeaseStrict(id));

        /// <summary>Libera sin borrar el archivo, para que el fencing siga siendo monotónico.</summary>
        public void ReleaseLease(string id, string owner)
        {
            try
            {
                WithLeaseLock(id, () =>
                {
                    var l = ReadLeaseStrict(id);
                    if (l != null && l.Owner == owner && !l.Released) { l.Released = true; WriteLease(id, l); }
                    return true;
                });
            }
            catch (LeaseStoreException) { }
        }
    }

    /// <summary>Unidades reconstruidas desde unidades.jsonl.</summary>
    public sealed class UnitLog
    {
        public readonly Dictionary<string, UnitRecord> Units = new Dictionary<string, UnitRecord>(StringComparer.Ordinal);
        public readonly List<string> Order = new List<string>();
        public int CorruptLines;
        /// <summary>Eventos de transición ilegal ignorados al reconstruir (R05).</summary>
        public int IllegalEvents;

        public UnitRecord Get(string key) => Units.TryGetValue(key, out var u) ? u : null;

        private UnitRecord Ensure(string key)
        {
            if (!Units.TryGetValue(key, out var u))
            {
                u = new UnitRecord { Key = key };
                Units[key] = u;
                Order.Add(key);
            }
            return u;
        }

        public void Apply(Dictionary<string, object> ev)
        {
            var kind = Arg.GetString(ev, "ev");
            switch (kind)
            {
                case "inicio":
                    foreach (var k in Arg.GetStringList(ev, "claves"))
                    {
                        var u = Ensure(k);
                        u.State = UnitState.EnCurso;
                        u.Attempts++;
                        u.Generation = Arg.GetString(ev, "generacion");
                        u.When = Arg.GetString(ev, "t");
                        u.Started = u.When;
                    }
                    break;
                case "sin_ejecutar":
                    foreach (var k in Arg.GetStringList(ev, "claves"))
                    {
                        var u = Ensure(k);
                        if (u.State == UnitState.EnCurso) { u.State = UnitState.Pendiente; u.Attempts = Math.Max(0, u.Attempts - 1); }
                    }
                    break;
                case "fin":
                case "verificada":
                case "reconciliada":
                {
                    var key = Arg.GetString(ev, "clave");
                    if (key == null) break;
                    var u = Ensure(key);
                    // R05, defensa en profundidad: aunque alguien escriba el evento a mano, una reconciliación no devuelve a pendiente lo confirmado.
                    if (kind == "reconciliada" && UnitState.IsDone(u.State) && Arg.GetString(ev, "estado") == UnitState.Pendiente) { IllegalEvents++; break; }
                    u.State = Arg.GetString(ev, "estado") ?? u.State;
                    u.Error = Arg.GetString(ev, "error");
                    u.Ms = Arg.GetLong(ev, "ms") ?? u.Ms;
                    u.When = Arg.GetString(ev, "t");
                    var seq = Arg.GetLong(ev, "seq");
                    if (seq.HasValue) u.Seq = seq.Value;
                    if (ev.TryGetValue("creados", out var cv) && cv is System.Collections.IEnumerable ce && !(cv is string))
                        u.Created = ce.Cast<object>().Select(x => x is object[] arr ? arr : x is List<object> l ? l.ToArray() : null).Where(x => x != null).ToList();
                    if (ev.TryGetValue("resultado", out var rv)) u.Result = rv;
                    if (kind == "fin" && Arg.GetString(ev, "generacion") is string g) u.Generation = g;
                    break;
                }
                case "pendiente":
                    foreach (var k in Arg.GetStringList(ev, "claves"))
                    {
                        var u = Ensure(k);
                        u.State = UnitState.Pendiente;
                        u.Error = Arg.GetString(ev, "motivo");
                    }
                    break;
            }
        }

        public Dictionary<string, long> Counts()
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var u in Units.Values) d[u.State] = (d.TryGetValue(u.State, out var n) ? n : 0) + 1;
            return d;
        }
    }
}
