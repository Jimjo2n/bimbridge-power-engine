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
        /// <summary>Sólo para pruebas: se llama justo antes de escribir; puede lanzar IOException para simular disco lleno.</summary>
        public Action<string> FaultBeforeWrite;

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
            lock (_lock)
            {
                try
                {
                    return WithFileLock(() =>
                    {
                        Resync(fullCheck: false);
                        return WriteLine(author, op, estado, extra);
                    });
                }
                catch (JournalUnavailableException) { Interlocked.Increment(ref _failures); throw; }
            }
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

        private long WriteLine(string author, string op, string estado, Dictionary<string, object> extra)
        {
            var e = new Dictionary<string, object>
            {
                ["seq"] = _seq + 1,
                ["t"] = Home.Now(),
                ["pid"] = (long)System.Diagnostics.Process.GetCurrentProcess().Id,
                ["autor"] = author,
                ["op"] = op,
                ["estado"] = estado,
                ["prev"] = _prev,
            };
            if (extra != null) foreach (var kv in extra) if (!e.ContainsKey(kv.Key)) e[kv.Key] = kv.Value;
            var line = Json.Write(e);
            try
            {
                FaultBeforeWrite?.Invoke(op);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                long offset;
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    offset = fs.Position;
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                    _knownLength = fs.Length;
                }
                _lastOffset = offset;
                var hash = Home.Sha256Hex(line);
                Home.WriteAtomic(HeadPath(_path), Json.Write(new Dictionary<string, object> { ["seq"] = _seq + 1, ["ultimo"] = hash, ["longitud"] = _knownLength }));
                _knownWrite = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Lo que haya quedado a medias lo detecta el siguiente Resync (cola sin salto de línea) y va a cuarentena.
                _knownLength = -1;
                throw new JournalUnavailableException("auditoría no escribible: " + ex.Message, ex);
            }
            _prev = Home.Sha256Hex(line);
            _seq++;
            return _seq;
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
                Quarantine(errors);
                return;
            }
            var v = Verify(_path);
            if (v.Ok) { _prev = v.LastHash ?? ""; _seq = v.Lines; _knownLength = len; _lastOffset = v.LastOffset; _knownWrite = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default(DateTime); return; }
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
                using (fs) return body();
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

        private static void CheckLine(string raw, ref string prev, ref long n, List<string> errors)
        {
            n++;
            Dictionary<string, object> o;
            try { o = Json.ParseObject(raw); }
            catch (FormatException) { errors.Add("línea " + n + " ilegible"); prev = Home.Sha256Hex(raw); return; }
            var p = Arg.GetString(o, "prev") ?? "";
            if (p != prev) errors.Add("línea " + n + ": cadena rota (prev no coincide)");
            long seq;
            try { seq = Convert.ToInt64(o.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); }
            catch (Exception) { seq = -1; }
            if (seq != n) errors.Add("línea " + n + ": seq " + seq + " fuera de orden");
            prev = Home.Sha256Hex(raw);
        }

        /// <summary>Compara la cola con la cabeza. Se tolera que la cabeza vaya UN registro por detrás (caída entre escribir la línea y la
        /// cabeza); nunca por delante ni con otro hash.</summary>
        private static void CheckHead(string path, List<string> hashes, List<string> errors)
        {
            var hp = HeadPath(path);
            if (!File.Exists(hp)) return; // cadenas anteriores a rc3
            Dictionary<string, object> h;
            try { h = Json.ParseObject(File.ReadAllText(hp)); }
            catch (Exception) { errors.Add("cabeza ilegible"); return; }
            long seq;
            try { seq = Convert.ToInt64(h.TryGetValue("seq", out var s) ? s : 0L, CultureInfo.InvariantCulture); } catch (Exception) { seq = -1; }
            var last = Arg.GetString(h, "ultimo") ?? "";
            if (seq > hashes.Count) { errors.Add("faltan registros al final (la cabeza indica " + seq + ", hay " + hashes.Count + ")"); return; }
            if (seq < hashes.Count - 1 || seq < 0) { errors.Add("la cabeza va " + (hashes.Count - seq) + " registros por detrás"); return; }
            if (seq >= 1 && hashes[(int)seq - 1] != last) errors.Add("el registro " + seq + " no coincide con la cabeza (editado)");
        }

        public sealed class VerifyResult
        {
            public long Lines;
            public string LastHash;
            public long LastOffset = -1;
            public List<string> Errors = new List<string>();
            public bool Ok => Errors.Count == 0;
        }

        /// <summary>Comprueba la cadena completa. Una línea final cortada (caída a mitad) es error, no se oculta.</summary>
        public static VerifyResult Verify(string path)
        {
            var r = new VerifyResult();
            if (!File.Exists(path)) return r;
            var text = ReadFrom(path, 0);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) r.Errors.Add("línea final sin terminar");
            string prev = "";
            long n = 0, off = 0;
            var hashes = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { off += 1; continue; }
                r.LastOffset = off;
                off += Encoding.UTF8.GetByteCount(raw) + 1;
                CheckLine(raw, ref prev, ref n, r.Errors);
                hashes.Add(prev);
            }
            r.Lines = n;
            r.LastHash = n == 0 ? null : prev;
            CheckHead(path, hashes, r.Errors);
            return r;
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
