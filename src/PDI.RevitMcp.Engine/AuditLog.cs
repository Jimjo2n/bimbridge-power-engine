// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Auditoría durable (0.6.1), separada de la telemetría (<see cref="Journal"/>). Compartida por varios procesos de Revit en la misma PC.
    ///
    /// Garantías:
    /// - Escritura síncrona con WriteThrough + Flush(true): cuando Append vuelve, la línea está en disco.
    /// - Serialización entre procesos: cada Append toma <c>auditoria.lock</c> (FileShare.None) y relee lo que otros procesos añadieron
    ///   desde la última vez, así seq y prev nunca se calculan sobre un estado viejo.
    /// - Cadena: cada línea lleva prev = sha256 de la anterior. <see cref="Verify"/> detecta borrado, edición, desorden y cola cortada.
    /// - Fallo seguro: un archivo inválido (al abrir o al relerlo) se mueve a cuarentena y se abre una cadena nueva cuyo primer registro
    ///   cita el archivo apartado y su sha256. Nunca se sigue escribiendo encima de una cadena rota.
    /// - Nunca se descarta en silencio: un fallo de escritura lanza <see cref="JournalUnavailableException"/> y suma a <see cref="Failures"/>.
    ///
    /// - Cabeza (rc3): <c>auditoria.jsonl.cabeza</c> guarda (seq, hash y longitud del último registro) tras cada escritura. Verify la
    ///   compara con la cola: detecta la edición o el borrado del ÚLTIMO registro, que la cadena sola no puede detectar.
    /// - Relectura barata (rc3): en cada Append se comprueba (a) que el último registro conocido sigue igual byte a byte y (b) que el
    ///   archivo no cambió de fecha sin cambiar de tamaño; si no cuadra, verificación completa (y cuarentena si procede).
    ///
    /// Modelo de amenazas (local, sin secreto): detecta corrupción accidental, cortes de escritura y ediciones a mano del archivo.
    /// NO resiste a un proceso del mismo usuario que reescriba a la vez la cadena entera Y la cabeza de forma coherente: para eso hace
    /// falta un ancla fuera del alcance de ese usuario (servidor, DPAPI de máquina o firma con clave protegida), previsto para 0.7.0.
    /// Dos fases (<see cref="Intent"/> / <see cref="Outcome"/>): la intención se persiste ANTES de tocar el modelo; si no se puede, no se toca.
    /// </summary>
    public sealed class AuditLog
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private string _prev = "";
        private long _seq;
        private long _knownLength;
        private long _lastOffset = -1;      // inicio del último registro conocido
        private DateTime _knownWrite;        // fecha de modificación tras nuestra última lectura/escritura
        private long _failures;

        public long Failures => Interlocked.Read(ref _failures);
        public string Path_ => _path;
        /// <summary>Archivos apartados por esta instancia (ruta completa).</summary>
        public readonly List<string> Quarantined = new List<string>();
        public static TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Límite de espera del bloqueo de ESTA instancia (por defecto <see cref="LockTimeout"/>, 10 s). Las pruebas lo acortan sin tocar el valor global.</summary>
        public TimeSpan LockWait = LockTimeout;
        /// <summary>Margen de esta instancia para recibir la confirmación de una entrada ya reclamada por otro líder.</summary>
        public TimeSpan ClaimWait = ClaimGrace;
        /// <summary>Sólo para pruebas: se llama justo antes de escribir; puede lanzar IOException para simular disco lleno.</summary>
        public Action<string> FaultBeforeWrite;
        /// <summary>
        /// Sólo para pruebas (AUD-01): devuelve true en el punto donde debe simularse la caída del proceso. Puntos: antes_bloqueo, inicio_lider, tras_reclamo,
        /// antes_escribir, durante_escritura, tras_escribir_antes_fsync, tras_fsync_antes_cabeza, tras_cabeza, antes_confirmar,
        /// tras_confirmar_parcial. La caída es una <see cref="AuditCrashException"/>: ni se confirma ni se limpia nada, como si el proceso muriera.
        /// </summary>
        public Func<string, bool> CrashPoint;
        private void Crash(string point) { if (CrashPoint != null && CrashPoint(point)) throw new AuditCrashException(point); }

        public AuditLog(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            lock (_lock) WithFileLock(() => { Resync(fullCheck: true); return true; });
        }

        public static string DefaultPath => Path.Combine(Home.Root, "auditoria", "auditoria.jsonl");

        private string LockPath => _path + ".lock";
        public static string HeadPath(string path) => path + ".cabeza";

        /// <summary>Registra un evento de autoridad. Devuelve su seq.</summary>
        public long Append(string author, string op, string estado, Dictionary<string, object> extra = null)
        {
            var me = new AuditItem { Id = Guid.NewGuid().ToString("N"), Author = author, Op = op, Estado = estado, Extra = extra, Pid = PidCache, T = Home.Now() };
            try { return AppendBatched(me); }
            catch (JournalUnavailableException) { Interlocked.Increment(ref _failures); throw; }
        }

        // ---------------- group commit entre procesos (issue #27) ----------------
        //
        // Con el bloqueo libre, Append escribe directo (camino rápido, formato idéntico). Con el bloqueo ocupado, el escritor deja su entrada
        // en <auditoria>.pend\ y espera; quien tiene el bloqueo (el líder) reclama TODAS las entradas pendientes y las escribe en una sola
        // pasada: un append, un fsync y una actualización de la cabeza para todo el lote. Cada seguidor sólo recibe su confirmación
        // (<id>.done con su seq) DESPUÉS de que el líder terminó el fsync y la cabeza. Un error del líder se propaga a cada seguidor
        // (<id>.err): nunca se confirma antes de persistir. Una entrada reclamada por un líder que cayó sin confirmar NO se reescribe
        // (evita duplicados): el seguidor falla de forma controlada y la intención queda para reconciliar, como cualquier escritura incierta.
        // El orden global es el del nombre del archivo pendiente (ticks de encolado); seq y prev los asigna siempre el líder bajo el bloqueo.

        private static readonly long PidCache = System.Diagnostics.Process.GetCurrentProcess().Id;
        public const int MaxBatch = 64;
        private DateTime _lastClean = DateTime.MinValue;
        private const long AwaitClaimed = -1;
        /// <summary>Margen para que un líder que ya reclamó mi entrada envíe su confirmación; pasado, falla sin reescribir.</summary>
        public static TimeSpan ClaimGrace = TimeSpan.FromSeconds(5);
        private string PendDir => _path + ".pend";

        private sealed class AuditItem
        {
            public string Id, Author, Op, Estado, Cid;
            public object T;
            public long Pid;
            public Dictionary<string, object> Extra;
        }

        private long AppendBatched(AuditItem me)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random();
            string pendName = null;
            while (true)
            {
                if (pendName != null)
                {
                    var done = Collect(me);
                    if (done.HasValue) return done.Value;
                }
                var waited = sw.ElapsedTicks;
                Crash("antes_bloqueo");
                var fs = TryOpenLock();
                if (fs != null)
                {
                    var held = System.Diagnostics.Stopwatch.StartNew();
                    // Las respuestas a los seguidores se envían DESPUÉS de soltar el bloqueo: la durabilidad ya está completa (fsync y cabeza)
                    // y así el bloqueo no espera a escribir un archivo por seguidor.
                    var after = new List<Action>();
                    long led = 0;
                    var crashed = false;
                    try
                    {
                        using (fs)
                        {
                            try { lock (_lock) led = Lead(me, pendName, after); }
                            catch (AuditCrashException) { crashed = true; throw; }
                            finally { AuditProfile.Lock(waited, held.ElapsedTicks); }
                        }
                    }
                    finally
                    {
                        // Una caída simulada no envía nada: el proceso "murió". La reconciliación la hará el siguiente líder.
                        if (!crashed)
                        {
                            Crash("antes_confirmar");
                            for (var k = 0; k < after.Count; k++)
                            {
                                after[k]();
                                if (k == 0 && after.Count > 1) Crash("tras_confirmar_parcial");
                            }
                        }
                    }
                    if (led != AwaitClaimed) return led;
                    var grace = System.Diagnostics.Stopwatch.StartNew();
                    while (grace.Elapsed < ClaimWait) { var d = Collect(me); if (d.HasValue) return d.Value; Thread.Sleep(2); }
                    throw new JournalUnavailableException("entrada de auditoría reclamada por otro escritor sin confirmación (posible caída); intención pendiente de reconciliar", null);
                }
                if (pendName == null) pendName = Enqueue(me);
                if (sw.Elapsed > LockWait)
                {
                    // Recuperar la propia entrada con la MISMA primitiva de reclamo que usa el líder (apertura exclusiva + borrar al cerrar):
                    // o la recupero yo (nadie la escribió) o ya la tiene un líder; nunca las dos cosas.
                    if (TryReclaim(Path.Combine(PendDir, pendName + ".json")))
                        throw new JournalUnavailableException("bloqueo de auditoría no obtenido en " + LockWait.TotalSeconds + " s (no se escribió nada)", null);
                    // Ya la reclamó un líder: se espera su confirmación un margen acotado; si no llega, falla sin reescribir.
                    var grace = System.Diagnostics.Stopwatch.StartNew();
                    while (grace.Elapsed < ClaimWait) { var d = Collect(me); if (d.HasValue) return d.Value; Thread.Sleep(5); }
                    throw new JournalUnavailableException("entrada de auditoría reclamada por otro escritor sin confirmación (posible caída); intención pendiente de reconciliar", null);
                }
                Thread.Sleep(1 + rnd.Next(3));
            }
        }

        /// <summary>
        /// Retira una entrada pendiente propia con la MISMA primitiva atómica que usa el líder para reclamarla (renombrar): o la retira el
        /// escritor (nadie la escribió) o ya la reclamó un líder; nunca las dos cosas.
        /// </summary>
        private static bool TryReclaim(string file)
        {
            try
            {
                var withdrawn = file + ".retirada";
                File.Move(file, withdrawn);
                try { File.Delete(withdrawn); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Intenta tomar el bloqueo sin esperar. null = ocupado.</summary>
        private FileStream TryOpenLock()
        {
            try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (DirectoryNotFoundException ex) { throw new JournalUnavailableException("carpeta de auditoría no disponible: " + ex.Message, ex); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException ex) { throw new JournalUnavailableException("bloqueo de auditoría sin permiso: " + ex.Message, ex); }
        }

        private string Enqueue(AuditItem me)
        {
            me.Cid = me.Id;
            var name = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + me.Id;
            var json = Json.Write(new Dictionary<string, object>
            {
                ["id"] = me.Id, ["autor"] = me.Author, ["op"] = me.Op, ["estado"] = me.Estado, ["pid"] = me.Pid, ["t"] = me.T, ["extra"] = me.Extra,
            });
            try
            {
                Directory.CreateDirectory(PendDir);
                var tmp = Path.Combine(PendDir, name + ".t");
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, Path.Combine(PendDir, name + ".json"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("auditoría no escribible (cola de escritores): " + ex.Message, ex);
            }
            return name;
        }

        /// <summary>Confirmación o error que un líder dejó para esta entrada. null = todavía no hay.</summary>
        private long? Collect(AuditItem me)
        {
            var done = Path.Combine(PendDir, me.Id + ".done");
            var err = Path.Combine(PendDir, me.Id + ".err");
            try
            {
                if (File.Exists(done))
                {
                    var seq = long.Parse(File.ReadAllText(done).Trim(), CultureInfo.InvariantCulture);
                    try { File.Delete(done); } catch (Exception) { }
                    return seq;
                }
                if (File.Exists(err))
                {
                    var msg = File.ReadAllText(err);
                    try { File.Delete(err); } catch (Exception) { }
                    throw new JournalUnavailableException(msg, null);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException) { return null; }
            return null;
        }

        /// <summary>Con el bloqueo tomado: reclama los pendientes, los escribe con un solo fsync y confirma a cada seguidor.</summary>
        private long Lead(AuditItem me, string pendName, List<Action> after)
        {
            Crash("inicio_lider");
            if (pendName != null) { var early = Collect(me); if (early.HasValue) return early.Value; }
            var tp = System.Diagnostics.Stopwatch.StartNew();
            Resync(fullCheck: false);
            AuditProfile.Phase(0, tp.ElapsedTicks); tp.Restart();
            // AUD-01B: reclamos que dejó un líder anterior que soltó el bloqueo sin terminar (sólo puede ser una caída: reclamar, persistir y
            // borrar el reclamo ocurren con el bloqueo tomado). Se decide con la evidencia del diario, no con la memoria de nadie.
            long? recoveredMine = RecoverAbandonedClaims(me, after);
            if (recoveredMine.HasValue) return recoveredMine.Value;
            var batch = new List<AuditItem>();
            var claimFiles = new List<string>();
            var mineClaimed = false;
            if (Directory.Exists(PendDir))
            {
                string[] files;
                try { files = Directory.GetFiles(PendDir, "*.json"); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { files = new string[0]; }
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    if (batch.Count >= MaxBatch - (pendName == null ? 1 : 0)) break;   // el lote nunca pasa de MaxBatch líneas
                    Dictionary<string, object> o;
                    // AUD-01B: reclamo DURABLE. La entrada se renombra a ".reclamada" (atómico: sólo un líder lo consigue) y sigue en disco hasta
                    // que su línea está persistida y sellada por la cabeza. Si el líder muere en medio, el reclamo queda como evidencia.
                    var claimed = Path.Combine(PendDir, Path.GetFileNameWithoutExtension(f) + ".reclamada");
                    try { File.Move(f, claimed); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }   // la retiró su escritor
                    try { o = Json.ParseObject(File.ReadAllText(claimed)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                    {
                        // Ilegible: no se escribe. Queda el reclamo para la reconciliación y el escritor recibe un error definitivo.
                        var bad = Path.GetFileNameWithoutExtension(f); var badId = bad.Length > 20 ? bad.Substring(20) : bad; var reason = ex.GetType().Name;
                        after.Add(() => Reply(badId, ".err", "entrada de auditoría ilegible en la cola (" + reason + "): no se escribió"));
                        continue;
                    }
                    var it = new AuditItem
                    {
                        Id = Arg.GetString(o, "id"), Author = Arg.GetString(o, "autor"), Op = Arg.GetString(o, "op"), Estado = Arg.GetString(o, "estado"),
                        Pid = Arg.GetLong(o, "pid") ?? 0, T = o.TryGetValue("t", out var tv) ? tv : Home.Now(),
                        Extra = o.TryGetValue("extra", out var ev) ? ev as Dictionary<string, object> : null,
                    };
                    it.Cid = it.Id;
                    if (it.Id == me.Id) { mineClaimed = true; me.Cid = me.Id; }
                    batch.Add(it);
                    claimFiles.Add(claimed);
                }
                if ((DateTime.UtcNow - _lastClean).TotalSeconds > 30) { _lastClean = DateTime.UtcNow; CleanStaleReplies(); }
            }
            Crash("tras_reclamo");
            AuditProfile.Phase(1, tp.ElapsedTicks); tp.Restart();
            // Mi entrada ya la reclamó otro líder: su respuesta se envía justo después de soltar el bloqueo, así que se espera (acotado) en vez de fallar.
            // Lo que YO reclamé de otros se escribe siempre: una entrada reclamada nunca se pierde.
            var meInBatch = pendName == null || mineClaimed;
            if (pendName == null) batch.Add(me);
            if (batch.Count == 0) return AwaitClaimed;
            List<long> seqs;
            try { seqs = WriteBatch(batch); }
            catch (JournalUnavailableException ex)
            {
                // Estado INCIERTO (pudo quedar algo escrito): los reclamos se conservan para que el siguiente líder decida con el diario.
                var msg = ex.Message + " (estado incierto: se reconcilia con el diario)";
                foreach (var it in batch) if (it.Id != me.Id) { var id = it.Id; after.Add(() => Reply(id, ".err", msg)); }
                if (meInBatch) throw;
                return AwaitClaimed;   // mi propia entrada la tiene otro líder, que me responderá; lo que yo reclamé ya recibió su error
            }
            // Persistido y sellado por la cabeza: los reclamos ya no hacen falta (con el bloqueo todavía tomado).
            foreach (var cf in claimFiles) { try { File.Delete(cf); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } }
            AuditProfile.Phase(2, tp.ElapsedTicks); tp.Restart();
            AuditProfile.Batch(batch.Count);
            long mySeq = 0;
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Id == me.Id) mySeq = seqs[i];
                else { var id = batch[i].Id; var text = seqs[i].ToString(CultureInfo.InvariantCulture); after.Add(() => Reply(id, ".done", text)); }
            }
            AuditProfile.Phase(3, tp.ElapsedTicks);
            return meInBatch ? mySeq : AwaitClaimed;
        }

        /// <summary>
        /// AUD-01B: resuelve los reclamos de un líder que cayó. Para cada entrada reclamada se busca su cid en la cadena (ya reparada o
        /// verificada por Resync): si está, se SELLA con un registro de recuperación (append + fsync + cabeza, que también hace durable la línea
        /// que el líder caído pudo dejar sin fsync) y sólo entonces se confirma a su escritor; si no está, no se reescribe nunca (el escritor
        /// pudo haber informado ya de un fallo y actuar en consecuencia) y se le comunica un error DEFINITIVO: no consta escrita.
        /// Devuelve el seq de MI entrada si estaba entre las reclamadas y consta escrita.
        /// </summary>
        private long? RecoverAbandonedClaims(AuditItem me, List<Action> after)
        {
            if (!Directory.Exists(PendDir)) return null;
            string[] claims;
            try { claims = Directory.GetFiles(PendDir, "*.reclamada"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            if (claims.Length == 0) return null;
            var byId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in claims)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                byId[name.Length > 20 ? name.Substring(20) : name] = f;
            }
            var found = FindCids(_path, byId.Keys);
            var inQuarantine = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in Quarantined) foreach (var kv in FindCids(q, byId.Keys)) inQuarantine.Add(kv.Key);
            // Sello y evidencia ANTES de confirmar nada.
            WriteLine("sistema", "recuperacion_reclamos", "reclamos_de_lider_caido", new Dictionary<string, object>
            {
                ["confirmados"] = byId.Keys.Where(found.ContainsKey).Cast<object>().ToList(),
                ["no_escritos"] = byId.Keys.Where(k => !found.ContainsKey(k) && !inQuarantine.Contains(k)).Cast<object>().ToList(),
                ["en_cadena_apartada"] = byId.Keys.Where(inQuarantine.Contains).Cast<object>().ToList(),
                ["motivo"] = "un líder soltó el bloqueo sin terminar (caída) dejando entradas reclamadas",
            });
            long? mine = null;
            string mineError = null;
            foreach (var kv in byId)
            {
                var id = kv.Key;
                if (found.TryGetValue(id, out var seq))
                {
                    if (id == me.Id) mine = seq;
                    else { var text = seq.ToString(CultureInfo.InvariantCulture); after.Add(() => Reply(id, ".done", text)); }
                }
                else
                {
                    var msg = inQuarantine.Contains(id)
                        ? "entrada de auditoría escrita en una cadena que después se apartó a cuarentena: estado incierto, reconcilie"
                        : "entrada de auditoría no escrita: el líder que la reclamó cayó antes de persistirla (no se escribió nada)";
                    if (id == me.Id) mineError = msg; else after.Add(() => Reply(id, ".err", msg));
                }
                try { File.Delete(kv.Value); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            if (mineError != null) throw new JournalUnavailableException(mineError, null);
            return mine;
        }

        /// <summary>cid → seq de las líneas de <paramref name="path"/> cuyo cid está en <paramref name="ids"/> (camino de recuperación, poco frecuente).</summary>
        private static Dictionary<string, long> FindCids(string path, IEnumerable<string> ids)
        {
            var want = new HashSet<string>(ids, StringComparer.Ordinal);
            var res = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!File.Exists(path) || want.Count == 0) return res;
            string text;
            try { text = ReadFrom(path, 0); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return res; }
            foreach (var raw in text.Split('\n'))
            {
                if (raw.IndexOf("\"cid\":", StringComparison.Ordinal) < 0) continue;
                Dictionary<string, object> o;
                try { o = Json.ParseObject(raw); } catch (FormatException) { continue; }
                var cid = Arg.GetString(o, "cid");
                if (cid != null && want.Contains(cid)) res[cid] = Arg.GetLong(o, "seq") ?? 0;
            }
            return res;
        }

        private void Reply(string id, string ext, string content)
        {
            try { File.WriteAllText(Path.Combine(PendDir, id + ext), content, new UTF8Encoding(false)); AuditProfile.ReplySent(id + ext); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                /* el seguidor agota su espera y falla sin reescribir; el error queda contado para diagnóstico */
                AuditProfile.ReplyError(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void CleanStaleReplies()
        {
            try
            {
                foreach (var pattern in new[] { "*.done", "*.err", "*.t" })
                    foreach (var f in Directory.GetFiles(PendDir, pattern))
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 120) File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Fase 1: intención durable antes de tocar el modelo. Si lanza, el llamador NO debe modificar nada.</summary>
        public string Intent(string author, string op, string target, Dictionary<string, object> extra = null)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            var e = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            e["intencion"] = id;
            e["objetivo"] = target;
            Append(author, op, "intencion", e);
            return id;
        }

        /// <summary>Fase 2: resultado. Si falla, la intención queda sin resultado y <see cref="PendingIntents"/> la devuelve para reconciliar.</summary>
        public void Outcome(string author, string op, string intentId, string estado, Dictionary<string, object> extra = null)
        {
            var e = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            e["intencion"] = intentId;
            e["fase"] = "resultado";
            Append(author, op, estado, e);
        }

        /// <summary>Intenciones sin resultado (la escritura pudo ocurrir o no): entrada de la reconciliación.</summary>
        public static List<Dictionary<string, object>> PendingIntents(string path)
        {
            var open = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (!File.Exists(path)) return new List<Dictionary<string, object>>();
            foreach (var raw in ReadLinesShared(path))
            {
                Dictionary<string, object> o;
                try { o = Json.ParseObject(raw); } catch (FormatException) { continue; }
                if (Arg.GetString(o, "op") == "cuarentena" && o.TryGetValue("intenciones_pendientes", out var ip) && ip is List<object> carried)
                {
                    // rc3: lo que quedó pendiente en una cadena apartada sigue pendiente (no se oculta al poner la cadena en cuarentena).
                    foreach (var c in carried.OfType<Dictionary<string, object>>())
                    {
                        var cid = Arg.GetString(c, "intencion");
                        if (cid != null) { c["desde_cuarentena"] = Arg.GetString(o, "apartado"); open[cid] = c; }
                    }
                    continue;
                }
                var id = Arg.GetString(o, "intencion");
                if (id == null) continue;
                if (Arg.GetString(o, "estado") == "intencion") open[id] = o;
                else open.Remove(id);
            }
            return open.Values.ToList();
        }

        // ---------------- interno ----------------

        private long WriteLine(string author, string op, string estado, Dictionary<string, object> extra) =>
            WriteBatch(new List<AuditItem> { new AuditItem { Author = author, Op = op, Estado = estado, Extra = extra, Pid = PidCache, T = Home.Now() } })[0];

        /// <summary>
        /// Escribe N líneas con UN append, UN fsync y UNA actualización de la cabeza. seq y prev se asignan en orden bajo el bloqueo.
        /// Sólo cuando el fsync y la cabeza terminaron se actualiza el estado en memoria y el llamador puede confirmar a nadie.
        /// </summary>
        private List<long> WriteBatch(List<AuditItem> items)
        {
            var prev = _prev; var seq = _seq;
            var seqs = new List<long>(items.Count);
            var buf = new MemoryStream();
            long lastLineStart = 0;
            var index = 0;
            foreach (var it in items)
            {
                seq++;
                index++;
                var e = new Dictionary<string, object>
                {
                    ["seq"] = seq,
                    ["t"] = it.T ?? Home.Now(),
                    ["pid"] = it.Pid,
                    ["autor"] = it.Author,
                    ["op"] = it.Op,
                    ["estado"] = it.Estado,
                    ["prev"] = prev,
                };
                if (it.Extra != null) foreach (var kv in it.Extra) if (!e.ContainsKey(kv.Key)) e[kv.Key] = kv.Value;
                if (it.Cid != null) e["cid"] = it.Cid;
                // AUD-01: marca de lote para que la verificación distinga un lote completo no confirmado de una cola truncada.
                if (items.Count > 1) e["lote"] = index.ToString(CultureInfo.InvariantCulture) + "/" + items.Count.ToString(CultureInfo.InvariantCulture);
                var line = Json.Write(e);
                lastLineStart = buf.Length;
                var b = Encoding.UTF8.GetBytes(line + "\n");
                buf.Write(b, 0, b.Length);
                prev = Home.Sha256Hex(line);
                seqs.Add(seq);
            }
            try
            {
                foreach (var it in items) FaultBeforeWrite?.Invoke(it.Op);
                Crash("antes_escribir");
                var bytes = buf.ToArray();
                long offset, length;
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    offset = fs.Position;
                    var tw = System.Diagnostics.Stopwatch.StartNew();
                    if (CrashPoint != null && CrashPoint("durante_escritura"))
                    {
                        // Sólo pruebas: escritura cortada a la mitad (deja líneas completas y un fragmento, como una caída real).
                        fs.Write(bytes, 0, bytes.Length / 2);
                        fs.Flush(true);
                        throw new AuditCrashException("durante_escritura");
                    }
                    fs.Write(bytes, 0, bytes.Length);
                    Crash("tras_escribir_antes_fsync");
                    var tf = System.Diagnostics.Stopwatch.StartNew();
                    fs.Flush(true);
                    AuditProfile.Write(tw.ElapsedTicks - tf.ElapsedTicks, tf.ElapsedTicks);
                    length = fs.Length;
                }
                Crash("tras_fsync_antes_cabeza");
                var th = System.Diagnostics.Stopwatch.StartNew();
                Home.WriteAtomic(HeadPath(_path), Json.Write(new Dictionary<string, object> { ["seq"] = seq, ["ultimo"] = prev, ["longitud"] = length }));
                AuditProfile.Head(th.ElapsedTicks);
                Crash("tras_cabeza");
                _knownLength = length;
                _lastOffset = offset + lastLineStart;
                _knownWrite = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Lo que haya quedado a medias lo detecta el siguiente Resync (cola sin salto de línea) y va a cuarentena.
                _knownLength = -1;
                throw new JournalUnavailableException("auditoría no escribible: " + ex.Message, ex);
            }
            _prev = prev;
            _seq = seq;
            return seqs;
        }

        /// <summary>Pone al día seq/prev con lo que escribieron otros procesos. Con fullCheck verifica la cadena entera.
        /// Si algo no cuadra (cadena rota, archivo acortado, línea final sin terminar) → cuarentena + cadena nueva.</summary>
        private void Resync(bool fullCheck)
        {
            long len;
            try { len = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new JournalUnavailableException("auditoría ilegible: " + ex.Message, ex); }
            if (!fullCheck && len == _knownLength && SameLastRecord() && File.GetLastWriteTimeUtc(_path) == _knownWrite) return;
            if (!fullCheck && _knownLength >= 0 && len > _knownLength && SameLastRecord())
            {
                // Sólo lo nuevo: barato aunque el archivo sea grande.
                string tail;
                try { tail = ReadFrom(_path, _knownLength); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new JournalUnavailableException("auditoría ilegible: " + ex.Message, ex); }
                var errors = new List<string>();
                var prev = _prev; var seq = _seq;
                if (!tail.EndsWith("\n", StringComparison.Ordinal)) errors.Add("línea final sin terminar");
                var off = _knownLength; var lastOff = _lastOffset;
                foreach (var raw in tail.Split('\n'))
                {
                    if (raw.Length == 0) continue;
                    lastOff = off;
                    off += Encoding.UTF8.GetByteCount(raw) + 1;
                    CheckLine(raw, ref prev, ref seq, errors);
                }
                if (errors.Count == 0) { _prev = prev; _seq = seq; _knownLength = len; _lastOffset = lastOff; _knownWrite = File.GetLastWriteTimeUtc(_path); return; }
                // AUD-01: una cola cortada por la caída de otro escritor se aparta (con evidencia) si no toca registros confirmados.
                if (TryRepairTail(out _)) return;
                Quarantine(errors);
                return;
            }
            var v = Verify(_path);
            if (v.Ok)
            {
                _prev = v.LastHash ?? ""; _seq = v.Lines; _knownLength = len; _lastOffset = v.LastOffset;
                _knownWrite = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default(DateTime);
                // AUD-01: lote persistido pero no confirmado (caída entre el fsync y la cabeza). Es una cadena íntegra: se sella con un registro
                // que lo documenta, en vez de declararla corrupta.
                if (v.HeadLag > 1)
                    WriteLine("sistema", "recuperacion_cabeza", "lote_no_confirmado_sellado", new Dictionary<string, object>
                    {
                        ["registros"] = v.HeadLag, ["desde_seq"] = v.Lines - v.HeadLag + 1, ["hasta_seq"] = v.Lines,
                        ["motivo"] = "caída entre el fsync del lote y la actualización de la cabeza; ningún escritor recibió confirmación",
                    });
                return;
            }
            if (TryRepairTail(out _)) return;
            Quarantine(v.Errors);
        }

        /// <summary>¿El último registro que conocemos sigue intacto en su sitio? (lectura de una sola línea).</summary>
        private bool SameLastRecord()
        {
            if (_seq == 0 || _lastOffset < 0) return _seq == 0;
            try
            {
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var n = (int)(_knownLength - _lastOffset);
                    if (n <= 1 || fs.Length < _knownLength) return false;
                    var buf = new byte[n];
                    fs.Seek(_lastOffset, SeekOrigin.Begin);
                    var read = 0;
                    while (read < n) { var k = fs.Read(buf, read, n - read); if (k <= 0) return false; read += k; }
                    if (buf[n - 1] != (byte)'\n') return false;
                    return Home.Sha256Hex(Encoding.UTF8.GetString(buf, 0, n - 1)) == _prev;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private void Quarantine(List<string> errors)
        {
            List<object> pending;
            try { pending = PendingIntents(_path).Select(d => (object)new Dictionary<string, object> { ["intencion"] = Arg.GetString(d, "intencion"), ["op"] = Arg.GetString(d, "op"), ["objetivo"] = Arg.GetString(d, "objetivo"), ["autor"] = Arg.GetString(d, "autor"), ["t"] = Arg.GetString(d, "t") }).ToList(); }
            catch (Exception) { pending = new List<object>(); }
            var dest = _path + ".cuarentena-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string sha;
            try
            {
                sha = Home.FileSha256(_path);
                File.Move(_path, dest);
                if (File.Exists(HeadPath(_path))) File.Move(HeadPath(_path), dest + ".cabeza");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("auditoría inválida (" + string.Join("; ", errors.Take(3)) + ") y no se pudo apartar: " + ex.Message, ex);
            }
            Quarantined.Add(dest);
            _prev = ""; _seq = 0; _knownLength = 0; _lastOffset = -1;
            WriteLine("sistema", "cuarentena", "cadena_nueva", new Dictionary<string, object>
            {
                ["apartado"] = Path.GetFileName(dest), ["sha256_apartado"] = sha, ["errores"] = errors.Take(10).Cast<object>().ToList(),
                ["intenciones_pendientes"] = pending,
            });
        }

        private T WithFileLock<T>(Func<T> body)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random();
            while (true)
            {
                FileStream fs;
                try { fs = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (DirectoryNotFoundException ex) { throw new JournalUnavailableException("carpeta de auditoría no disponible: " + ex.Message, ex); }
                catch (IOException ex)
                {
                    if (sw.Elapsed > LockTimeout) throw new JournalUnavailableException("bloqueo de auditoría no obtenido en " + LockTimeout.TotalSeconds + " s", ex);
                    Thread.Sleep(2 + rnd.Next(10));
                    continue;
                }
                catch (UnauthorizedAccessException ex) { throw new JournalUnavailableException("bloqueo de auditoría sin permiso: " + ex.Message, ex); }
                var waited = sw.ElapsedTicks;
                var held = System.Diagnostics.Stopwatch.StartNew();
                using (fs)
                {
                    try { return body(); }
                    finally { AuditProfile.Lock(waited, held.ElapsedTicks); }
                }
            }
        }

        private static string ReadFrom(string path, long offset)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            var text = ReadFrom(path, 0);
            return text.Split('\n').Where(l => l.Length > 0);
        }

        private static void CheckLine(string raw, ref string prev, ref long n, List<string> errors) => CheckLine(raw, ref prev, ref n, errors, out _);

        private static void CheckLine(string raw, ref string prev, ref long n, List<string> errors, out string lote)
        {
            n++;
            lote = null;
            Dictionary<string, object> o;
            try { o = Json.ParseObject(raw); }
            catch (FormatException) { errors.Add("línea " + n + " ilegible"); prev = Home.Sha256Hex(raw); return; }
            var p = Arg.GetString(o, "prev") ?? "";
            if (p != prev) errors.Add("línea " + n + ": cadena rota (prev no coincide)");
            long seq;
            try { seq = Convert.ToInt64(o.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); }
            catch (Exception) { seq = -1; }
            if (seq != n) errors.Add("línea " + n + ": seq " + seq + " fuera de orden");
            lote = Arg.GetString(o, "lote");
            prev = Home.Sha256Hex(raw);
        }

        /// <summary>"i/n" → (i, n). (0, 0) si no hay marca de lote (registro individual).</summary>
        private static void ParseLote(string lote, out int i, out int n)
        {
            i = 0; n = 0;
            if (string.IsNullOrEmpty(lote)) return;
            var k = lote.IndexOf('/');
            if (k <= 0 || !int.TryParse(lote.Substring(0, k), NumberStyles.None, CultureInfo.InvariantCulture, out i) ||
                !int.TryParse(lote.Substring(k + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n)) { i = -1; n = -1; }
        }

        /// <summary>
        /// AUD-01: compara la cola con la cabeza. La cabeza nunca puede ir por delante ni apuntar a otro hash. Puede ir por detrás sólo en el
        /// estado que deja una caída entre el fsync de un lote y la actualización de la cabeza: o bien UN registro individual, o bien UN lote
        /// COMPLETO (todas sus líneas con marca "lote" i/n, i = 1..n en orden, n = desfase). Esas líneas están persistidas pero NO confirmadas
        /// (nadie recibió su seq), y la siguiente escritura las sella. Devuelve el desfase tolerado (0 si la cabeza está al día).
        /// </summary>
        private static long CheckHead(string path, List<string> hashes, List<string> lotes, List<string> errors)
        {
            var hp = HeadPath(path);
            if (!File.Exists(hp)) return 0; // cadenas anteriores a rc3
            Dictionary<string, object> h;
            try { h = Json.ParseObject(File.ReadAllText(hp)); }
            catch (Exception) { errors.Add("cabeza ilegible"); return 0; }
            long seq;
            try { seq = Convert.ToInt64(h.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); } catch (Exception) { seq = -1; }
            var last = Arg.GetString(h, "ultimo") ?? "";
            if (seq < 0) { errors.Add("cabeza ilegible"); return 0; }
            if (seq > hashes.Count) { errors.Add("faltan registros al final (la cabeza indica " + seq + ", hay " + hashes.Count + ")"); return 0; }
            if (seq >= 1 && hashes[(int)seq - 1] != last) { errors.Add("el registro " + seq + " no coincide con la cabeza (editado)"); return 0; }
            var lag = hashes.Count - seq;
            if (lag <= 1) return lag;
            if (lag > MaxBatch) { errors.Add("la cabeza va " + lag + " registros por detrás (más que un lote)"); return 0; }
            for (var k = 0; k < lag; k++)
            {
                ParseLote(lotes[(int)seq + k], out var i, out var n);
                if (i != k + 1 || n != lag) { errors.Add("la cabeza va " + lag + " registros por detrás y la cola no es un lote completo"); return 0; }
            }
            return lag;
        }

        public sealed class VerifyResult
        {
            public long Lines;
            public string LastHash;
            public long LastOffset = -1;
            /// <summary>Registros persistidos más allá de la cabeza (lote no confirmado tras una caída). 0 = cabeza al día.</summary>
            public long HeadLag;
            public List<string> Errors = new List<string>();
            public bool Ok => Errors.Count == 0;
        }

        /// <summary>Comprueba la cadena completa. Una línea final cortada (caída a mitad) es error, no se oculta: la reparación es aparte.</summary>
        public static VerifyResult Verify(string path)
        {
            var r = new VerifyResult();
            if (!File.Exists(path)) return r;
            var text = ReadFrom(path, 0);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) r.Errors.Add("línea final sin terminar");
            string prev = "";
            long n = 0, off = 0;
            var hashes = new List<string>();
            var lotes = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { off += 1; continue; }
                r.LastOffset = off;
                off += Encoding.UTF8.GetByteCount(raw) + 1;
                CheckLine(raw, ref prev, ref n, r.Errors, out var lote);
                hashes.Add(prev);
                lotes.Add(lote);
            }
            r.Lines = n;
            r.LastHash = n == 0 ? null : prev;
            r.HeadLag = CheckHead(path, hashes, lotes, r.Errors);
            return r;
        }

        /// <summary>
        /// AUD-01: reparación conservadora de una cola dejada por una caída. Sólo actúa si TODO lo anterior es una cadena válida y lo que
        /// sobra está más allá de la cabeza (persistido pero nunca confirmado): un fragmento final sin salto de línea y/o las líneas de un lote
        /// incompleto. Lo apartado se guarda íntegro en "&lt;auditoria&gt;.fragmento-…" (evidencia), el archivo se corta en el último límite
        /// válido, se reescribe la cabeza y se añade un registro "recuperacion_cola" que lo documenta. Nunca corta registros confirmados
        /// (≤ cabeza); una edición, un borrado o una cadena rota siguen yendo a cuarentena.
        /// </summary>
        private bool TryRepairTail(out string note)
        {
            note = null;
            byte[] all;
            try { all = File.ReadAllBytes(_path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            var lastNl = Array.LastIndexOf(all, (byte)'\n');
            var completeLen = lastNl + 1;   // bytes de líneas completas
            var text = Encoding.UTF8.GetString(all, 0, completeLen);
            var errors = new List<string>();
            string prev = "";
            long n = 0, off = 0;
            var hashes = new List<string>(); var lotes = new List<string>(); var ends = new List<long>();
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { off += 1; continue; }
                off += Encoding.UTF8.GetByteCount(raw) + 1;
                CheckLine(raw, ref prev, ref n, errors, out var lote);
                hashes.Add(prev); lotes.Add(lote); ends.Add(off);
            }
            if (errors.Count > 0) return false;                        // la parte completa no es una cadena válida: corrupción real
            // Lote incompleto al final: se quitan sus líneas (nunca se confirmó).
            var keep = hashes.Count;
            if (keep > 0)
            {
                ParseLote(lotes[keep - 1], out var li, out var ln);
                if (li > 0 && ln > 0 && li < ln) keep -= li;
            }
            var keepBytes = keep == 0 ? 0 : ends[keep - 1];
            if (keepBytes == all.Length) return false;                 // no hay nada que apartar: el problema es otro
            // La cabeza debe quedar dentro de lo conservado y coincidir con su hash.
            long headSeq = 0;
            var hp = HeadPath(_path);
            if (File.Exists(hp))
            {
                Dictionary<string, object> h;
                try { h = Json.ParseObject(File.ReadAllText(hp)); } catch (Exception) { return false; }
                try { headSeq = Convert.ToInt64(h.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); } catch (Exception) { return false; }
                var last = Arg.GetString(h, "ultimo") ?? "";
                if (headSeq < 0 || headSeq > keep) return false;       // se perderían registros confirmados: cuarentena
                if (headSeq >= 1 && hashes[(int)headSeq - 1] != last) return false;
            }
            var dest = _path + ".fragmento-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dropped = new byte[all.Length - keepBytes];
            Array.Copy(all, keepBytes, dropped, 0, dropped.Length);
            try
            {
                // AUD-10: el fragmento debe ser durable y legible ANTES de truncar el diario. Se escribe con WriteThrough + fsync en un
                // temporal, se relee y compara byte a byte, y sólo entonces se renombra. Si algo falla, no se trunca nada.
                var tmp = dest + ".tmp";
                using (var fw = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fw.Write(dropped, 0, dropped.Length);
                    fw.Flush(true);
                }
                Crash("reparacion_tras_fragmento_tmp");
                var back = File.ReadAllBytes(tmp);
                if (back.Length != dropped.Length || !back.SequenceEqual(dropped)) { File.Delete(tmp); return false; }
                File.Move(tmp, dest);
                Crash("reparacion_antes_truncar");
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    fs.SetLength(keepBytes);
                    fs.Flush(true);
                }
                Crash("reparacion_tras_truncar");
                var lastHash = keep == 0 ? "" : hashes[keep - 1];
                Home.WriteAtomic(hp, Json.Write(new Dictionary<string, object> { ["seq"] = (long)keep, ["ultimo"] = lastHash, ["longitud"] = keepBytes }));
                _prev = lastHash; _seq = keep; _knownLength = keepBytes;
                _lastOffset = keep == 0 ? -1 : (keep == 1 ? 0 : ends[keep - 2]);
                _knownWrite = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            Repaired.Add(dest);
            note = "cola incompleta apartada: " + (hashes.Count - keep) + " líneas de un lote sin terminar y " + (all.Length - completeLen) + " bytes sin salto de línea";
            WriteLine("sistema", "recuperacion_cola", "cola_incompleta_apartada", new Dictionary<string, object>
            {
                ["apartado"] = Path.GetFileName(dest), ["sha256_apartado"] = Home.FileSha256(dest), ["bytes"] = (long)dropped.Length,
                ["lineas_descartadas"] = (long)(hashes.Count - keep), ["motivo"] = "escritura interrumpida más allá de la cabeza (nunca confirmada)",
            });
            return true;
        }

        /// <summary>Archivos de cola incompleta apartados por esta instancia (evidencia de una reparación, no de corrupción).</summary>
        public readonly List<string> Repaired = new List<string>();
    }

    /// <summary>Caída simulada en un punto del protocolo de auditoría (sólo pruebas). No la captura ningún manejador de E/S.</summary>
    public sealed class AuditCrashException : Exception
    {
        public AuditCrashException(string point) : base("caída simulada en " + point) { Point = point; }
        public string Point { get; }
    }

    /// <summary>
    /// Contadores de coste de AuditLog por proceso (issue #27): espera del bloqueo, tiempo con el bloqueo tomado, escritura, fsync y cabeza.
    /// Sólo suman ticks con Interlocked; no cambian el comportamiento. Los lee la sonda de pruebas y el diagnóstico.
    /// </summary>
    public static class AuditProfile
    {
        private static long _n, _wait, _held, _write, _fsync, _head, _maxWait, _maxHeld;
        private static readonly object _samplesLock = new object();
        private static readonly List<long> _waitSamples = new List<long>(), _heldSamples = new List<long>();
        internal static void Lock(long waitTicks, long heldTicks)
        {
            Interlocked.Increment(ref _n); Interlocked.Add(ref _wait, waitTicks); Interlocked.Add(ref _held, heldTicks);
            lock (_samplesLock) { if (_waitSamples.Count < 100000) { _waitSamples.Add(waitTicks); _heldSamples.Add(heldTicks); } }
        }
        internal static void Write(long writeTicks, long fsyncTicks) { Interlocked.Add(ref _write, writeTicks); Interlocked.Add(ref _fsync, fsyncTicks); }
        internal static void Head(long ticks) => Interlocked.Add(ref _head, ticks);
        private static long _replyErrors; private static string _lastReplyError;
        private static readonly List<string> _sent = new List<string>();
        internal static void ReplySent(string name) { lock (_sent) if (_sent.Count < 20) _sent.Add(name); }
        public static string RepliesSent { get { lock (_sent) return string.Join(",", _sent); } }
        internal static void ReplyError(string msg) { Interlocked.Increment(ref _replyErrors); _lastReplyError = msg; }
        public static string ReplyErrors => Interlocked.Read(ref _replyErrors) + (_lastReplyError == null ? "" : " último: " + _lastReplyError);
        private static readonly long[] _phase = new long[4];
        internal static void Phase(int i, long ticks) => Interlocked.Add(ref _phase[i], ticks);
        public static string PhaseReport() => string.Format(CultureInfo.InvariantCulture, "resync={0:n1}ms claim={1:n1}ms batch_write={2:n1}ms replies={3:n1}ms (totales del proceso)",
            Ms(Interlocked.Read(ref _phase[0])), Ms(Interlocked.Read(ref _phase[1])), Ms(Interlocked.Read(ref _phase[2])), Ms(Interlocked.Read(ref _phase[3])));
        private static long _batches, _batched, _maxBatch;
        internal static void Batch(int n)
        {
            Interlocked.Increment(ref _batches); Interlocked.Add(ref _batched, n);
            long cur; while (n > (cur = Interlocked.Read(ref _maxBatch)) && Interlocked.CompareExchange(ref _maxBatch, n, cur) != cur) { }
        }
        public static string BatchReport() => string.Format(CultureInfo.InvariantCulture, "batches={0} entries={1} max_batch={2} mean_batch={3:n2}",
            Interlocked.Read(ref _batches), Interlocked.Read(ref _batched), Interlocked.Read(ref _maxBatch), Interlocked.Read(ref _batched) / (double)Math.Max(1, Interlocked.Read(ref _batches)));
        private static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static double Pct(List<long> v, double p) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return Ms(s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)]); }
        public static string Report()
        {
            lock (_samplesLock)
            {
                var n = Math.Max(1, Interlocked.Read(ref _n));
                return string.Format(CultureInfo.InvariantCulture,
                    "n={0} wait_mean={1:n2}ms wait_p50={2:n2} wait_p95={3:n2} wait_max={4:n2} held_mean={5:n2}ms held_p95={6:n2} write_mean={7:n3}ms fsync_mean={8:n2}ms head_mean={9:n2}ms",
                    n, Ms(_wait) / n, Pct(_waitSamples, 0.5), Pct(_waitSamples, 0.95), _waitSamples.Count == 0 ? 0 : Ms(_waitSamples.Max()),
                    Ms(_held) / n, Pct(_heldSamples, 0.95), Ms(_write) / n, Ms(_fsync) / n, Ms(_head) / n);
            }
        }
    }

    /// <summary>
    /// Protocolo común de dos momentos (R01) para cualquier escritura fuera del motor de tareas (cola, exec):
    /// intención durable → modificar → resultado. Si la intención no se puede persistir, NO se modifica.
    /// rc3: si el resultado no se puede persistir, la modificación YA ocurrió (no se finge reversión): se devuelve el resultado BIM marcado
    /// con <paramref name="incomplete"/> (auditoría incompleta + id de la intención, que queda en PendingIntents para reconciliar), de modo
    /// que el cliente no lo pueda leer como completamente verificado.
    /// </summary>
    public static class AuditGate
    {
        public const string Incompleta = "auditoria_incompleta";

        public static T Apply<T>(AuditLog audit, string author, string op, string target, Dictionary<string, object> extra,
            Func<T> apply, Func<T, string> status, Func<string, T> refused, Func<T, string, T> incomplete)
        {
            string intent;
            try
            {
                if (audit == null) throw new JournalUnavailableException("auditoría no inicializada", null);
                intent = audit.Intent(author, op, target, extra);
            }
            catch (JournalUnavailableException ex) { return refused("auditoria_no_disponible: no se modificó nada (" + ex.Message + ")"); }
            var result = apply();
            try { audit.Outcome(author, op, intent, status(result), extra); }
            catch (JournalUnavailableException ex)
            {
                return incomplete(result, Incompleta + ": la operación terminó (su resultado es real y no se revirtió), pero el resultado no quedó en la auditoría (" +
                                          ex.Message + "). Intención " + intent + " pendiente de reconciliar.");
            }
            return result;
        }
    }
}
