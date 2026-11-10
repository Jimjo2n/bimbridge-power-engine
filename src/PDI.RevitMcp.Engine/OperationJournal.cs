// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>Carpeta de datos del MCP (%LOCALAPPDATA%\PDI.RevitMcp o PDI_REVITMCP_HOME).</summary>
    public static class Home
    {
        public static string Root
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("PDI_REVITMCP_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp");
                return root;
            }
        }

        public static string Sub(string name) => Path.Combine(Root, name);

        public static string Sha256Hex(byte[] data, int chars = 64)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString().Substring(0, Math.Min(64, chars));
            }
        }

        public static string Sha256Hex(string text, int chars = 64) => Sha256Hex(Encoding.UTF8.GetBytes(text ?? ""), chars);

        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var h = sha.ComputeHash(f);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Escribe un archivo de forma atómica (temporal + reemplazo) y lo vacía al disco.</summary>
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                f.Write(bytes, 0, bytes.Length);
                f.Flush(true);
            }
            // 0.8.0 (residuo estado.json.tmp-* de la campaña rc1): en Windows File.Replace puede fallar un instante si un
            // antivirus o el indexador tiene abierto el destino. Se reintenta brevemente y, si aun así falla, el temporal se borra.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    System.Threading.Thread.Sleep(20 * attempt);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
            }
        }

        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>El diario no se pudo leer o escribir: no se anuncia idempotencia que no existe (H03).</summary>
    public sealed class JournalUnavailableException : Exception
    {
        public JournalUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Archivo JSONL de sólo-anexar, durable y tolerante (0.6.0):
    /// - cada línea se escribe con WriteThrough y Flush(true);
    /// - una última línea incompleta (corte de luz, Revit muerto) se ignora al leer y la siguiente escritura empieza en línea nueva;
    /// - varios procesos (dos Revit) comparten el archivo: antes de escribir se leen las líneas nuevas de los demás;
    /// - si no se puede escribir se lanza <see cref="JournalUnavailableException"/>; nunca se pierde en silencio.
    /// </summary>
    public sealed class JsonlLog
    {
        private readonly string _path;
        private long _offset;

        public JsonlLog(string path)
        {
            _path = path;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("no se puede crear la carpeta del diario " + Path.GetDirectoryName(path) + ": " + ex.Message, ex);
            }
        }

        public string Path_ => _path;
        public int CorruptLines { get; private set; }

        /// <summary>Lee las líneas completas nuevas desde la última lectura (o todas la primera vez).</summary>
        public List<Dictionary<string, object>> ReadNew()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                if (!File.Exists(_path)) return list;
                using (var f = Open(FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    ReadFrom(f, list);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new JournalUnavailableException("no se puede leer el diario " + _path + ": " + ex.Message, ex);
            }
            return list;
        }

        private void ReadFrom(FileStream f, List<Dictionary<string, object>> into)
        {
            if (f.Length <= _offset) return;
            f.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[f.Length - _offset];
            var read = 0;
            while (read < buf.Length)
            {
                var n = f.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (lastNl < 0) return; // sólo una línea incompleta: se ignora hasta que esté completa (o para siempre)
            var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
            _offset += lastNl + 1;
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { into.Add(Json.ParseObject(line)); }
                catch (FormatException) { CorruptLines++; }
            }
        }

        /// <summary>Anexa una línea; antes devuelve (en <paramref name="others"/>) lo que otros procesos escribieron desde la última lectura.</summary>
        public void Append(Dictionary<string, object> o, List<Dictionary<string, object>> others)
        {
            if (!o.ContainsKey("t")) o["t"] = Home.Now();
            var line = Encoding.UTF8.GetBytes(Json.Write(o) + "\n");
            Exception last = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    using (var f = Open(FileAccess.ReadWrite, FileShare.Read))
                    {
                        ReadFrom(f, others);
                        // Una línea cortada de otro proceso o de un corte de luz: empezar en línea nueva.
                        var prefix = false;
                        if (f.Length > 0)
                        {
                            f.Seek(f.Length - 1, SeekOrigin.Begin);
                            prefix = f.ReadByte() != '\n';
                        }
                        f.Seek(0, SeekOrigin.End);
                        if (prefix) f.WriteByte((byte)'\n');
                        f.Write(line, 0, line.Length);
                        f.Flush(true);
                        _offset = f.Length;
                    }
                    return;
                }
                catch (IOException ex) when (IsSharing(ex)) { last = ex; Thread.Sleep(50); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new JournalUnavailableException("no se pudo escribir en el diario " + _path + ": " + ex.Message, ex);
                }
            }
            throw new JournalUnavailableException("el diario " + _path + " está bloqueado por otro proceso", last);
        }

        private FileStream Open(FileAccess access, FileShare share) =>
            new FileStream(_path, access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate, access, share, 4096,
                access == FileAccess.Read ? FileOptions.None : FileOptions.WriteThrough);

        private static bool IsSharing(IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        }
    }

    /// <summary>
    /// Diario de operaciones con clave de idempotencia (R017, R022, R024; reescrito en 0.6.0 por H02/H03).
    /// Máquina de estados por (documento duradero, clave):
    /// - en curso en este proceso → "Running" con el mismo job_id (no se lanza otra vez);
    /// - mismo id con otra huella → "Mismatch" SIEMPRE, también con reconciliar (una clave identifica una sola operación);
    /// - terminado y aplicado (ok, con diferencias, retenido, aplicado con error de resultado) → "Completed": se devuelve lo guardado;
    /// - terminado con reversión confirmada (revertido, rechazado, inválido…) → se puede repetir con la misma clave (nuevo intento);
    /// - error, parcial, desconocido, cancelado o sin final → "Interrupted": hay que reconciliar (leer el modelo) antes de repetir;
    /// - en_proceso NO es final: el final lo escribe el trabajo cuando termina de verdad (un único punto de finalización).
    /// Los intentos no se sobrescriben: cada uno queda en el archivo y en <see cref="Record.Attempts"/>.
    /// No promete "exactamente una vez" si Revit muere entre aplicar y escribir el final: ese caso queda "Interrupted".
    /// </summary>
    public sealed class OperationJournal
    {
        public sealed class Attempt
        {
            public int N;
            public string Session;
            public string Started;
            public string Finished;
            public string Estado;
            public string JobId;
            public string Reconciled;
        }

        public sealed class Record
        {
            public string Key;
            public string Doc;
            public string Op;
            public string Hash;
            public string Task;
            public string Session;
            public string Started;
            public string Finished;
            public string Estado;
            public object Result;
            public string JobId;
            /// <summary>Huella del modelo (ProjectInformation.UniqueId, GUID cloud…) cuando se registró: detecta archivos sustituidos y copias.</summary>
            public string DocPrint;
            /// <summary>Generación de apertura (instancia + secuencia): distingue reaperturas del mismo archivo.</summary>
            public string Generation;
            public List<Attempt> Attempts = new List<Attempt>();

            internal Record Clone(string doc)
            {
                var r = (Record)MemberwiseClone();
                r.Doc = doc;
                r.Attempts = Attempts.Select(a => a).ToList();
                return r;
            }
        }

        public enum BeginKind { New, Completed, Interrupted, Running, Mismatch, DocumentReplaced, PossibleCopy }

        /// <summary>Decisión de reconciliación pedida por el agente tras leer el modelo.</summary>
        public enum ReconcileMode
        {
            None,
            /// <summary>"reconciliar=no_aplicado" (o =si): se comprobó que NO quedó aplicado; se repite con la misma huella.</summary>
            NotApplied,
            /// <summary>"reconciliar=aplicado": se comprobó que SÍ quedó aplicado; se marca hecho sin repetir.</summary>
            Applied,
        }

        public static ReconcileMode ParseReconcile(object v)
        {
            if (v == null) return ReconcileMode.None;
            if (v is bool b) return b ? ReconcileMode.NotApplied : ReconcileMode.None;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            switch (s)
            {
                case "": case "no": case "false": return ReconcileMode.None;
                case "aplicado": case "hecho": case "applied": return ReconcileMode.Applied;
                case "si": case "sí": case "true": case "no_aplicado": case "repetir": return ReconcileMode.NotApplied;
                default: throw new ArgumentException("reconciliar= debe ser no_aplicado (repetir) o aplicado (marcar hecho)");
            }
        }

        public sealed class BeginResult
        {
            public BeginKind Kind;
            public Record Previous;
            /// <summary>Clave de documento donde se encontró la operación cuando es una posible copia.</summary>
            public string OtherDoc;
        }

        private readonly object _lock = new object();
        private readonly JsonlLog _log;
        private readonly Dictionary<string, Record> _index = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _running = new Dictionary<string, string>(StringComparer.Ordinal); // id → job id (o "")

        public static string DefaultPath => Path.Combine(Home.Sub("operaciones"), "operaciones.jsonl");

        public OperationJournal(string path = null)
        {
            _log = new JsonlLog(path ?? DefaultPath);
            Apply(_log.ReadNew());
        }

        public string Path_ => _log.Path_;
        public int CorruptLines => _log.CorruptLines;

        private static string Id(string doc, string key) => (doc ?? "") + "\u0001" + key;

        private void Apply(List<Dictionary<string, object>> events)
        {
            foreach (var o in events)
            {
                var ev = Arg.GetString(o, "ev");
                if (ev == "alias")
                {
                    ApplyAlias(Arg.GetString(o, "de"), Arg.GetString(o, "a"));
                    continue;
                }
                var key = Arg.GetString(o, "clave");
                var doc = Arg.GetString(o, "doc");
                if (key == null) continue;
                var id = Id(doc, key);
                _index.TryGetValue(id, out var r);
                switch (ev)
                {
                    case "inicio":
                        if (r == null)
                        {
                            r = new Record { Key = key, Doc = doc };
                            _index[id] = r;
                        }
                        r.Op = Arg.GetString(o, "op");
                        r.Hash = Arg.GetString(o, "hash");
                        r.Task = Arg.GetString(o, "tarea") ?? r.Task;
                        r.Session = Arg.GetString(o, "sesion");
                        r.Started = Arg.GetString(o, "t");
                        r.DocPrint = Arg.GetString(o, "huella_doc") ?? r.DocPrint;
                        r.Generation = Arg.GetString(o, "generacion");
                        r.Finished = null;
                        r.Estado = null;
                        r.Result = null;
                        r.JobId = null;
                        r.Attempts.Add(new Attempt { N = r.Attempts.Count + 1, Session = r.Session, Started = r.Started });
                        break;
                    case "job" when r != null:
                        r.JobId = Arg.GetString(o, "job_id");
                        if (r.Attempts.Count > 0) r.Attempts[r.Attempts.Count - 1].JobId = r.JobId;
                        break;
                    case "fin" when r != null:
                        r.Finished = Arg.GetString(o, "t");
                        r.Estado = Arg.GetString(o, "estado");
                        o.TryGetValue("resultado", out r.Result);
                        if (r.Attempts.Count > 0) { var a = r.Attempts[r.Attempts.Count - 1]; a.Finished = r.Finished; a.Estado = r.Estado; }
                        break;
                    case "reconciliado" when r != null:
                        var dec = Arg.GetString(o, "decision");
                        if (r.Attempts.Count > 0) r.Attempts[r.Attempts.Count - 1].Reconciled = dec;
                        if (dec == "aplicado")
                        {
                            r.Finished = Arg.GetString(o, "t");
                            r.Estado = Status.Ok;
                            o.TryGetValue("evidencia", out r.Result);
                        }
                        break;
                    case "olvidar":
                        _index.Remove(id);
                        break;
                }
            }
        }

        private void ApplyAlias(string from, string to)
        {
            if (from == null || to == null || from == to) return;
            foreach (var r in _index.Values.Where(x => x.Doc == from).ToList())
            {
                var id = Id(to, r.Key);
                if (!_index.ContainsKey(id)) _index[id] = r.Clone(to);
            }
        }

        private void Write(Dictionary<string, object> o)
        {
            var others = new List<Dictionary<string, object>>();
            _log.Append(o, others);
            others.Add(o);
            Apply(others);
        }

        /// <summary>Lee lo que otros procesos escribieron (barato si no hay nada nuevo).</summary>
        private void Refresh() => Apply(_log.ReadNew());

        /// <summary>Estados finales que cuentan como "ya hecho" (no se repiten).</summary>
        public static bool IsDone(string estado) => Status.CountsAsApplied(estado);

        public BeginResult Begin(string key, string doc, string op, string hash, string task, string session, bool reconcile = false) =>
            Begin(key, doc, op, hash, task, session, reconcile ? ReconcileMode.NotApplied : ReconcileMode.None);

        /// <summary>
        /// Registra la intención (evento "inicio", durable) o explica por qué no. Lanza <see cref="JournalUnavailableException"/> si no puede
        /// leer o escribir el diario: quien llama NO debe aplicar la operación en ese caso.
        /// </summary>
        public BeginResult Begin(string key, string doc, string op, string hash, string task, string session, ReconcileMode mode,
            string docPrint = null, string generation = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("clave vacía");
            lock (_lock)
            {
                Refresh();
                var id = Id(doc, key);
                if (_running.TryGetValue(id, out var runningJob))
                {
                    _index.TryGetValue(id, out var cur);
                    if (cur != null && hash != null && cur.Hash != null && cur.Hash != hash) return new BeginResult { Kind = BeginKind.Mismatch, Previous = cur };
                    return new BeginResult { Kind = BeginKind.Running, Previous = cur };
                }
                if (_index.TryGetValue(id, out var prev))
                {
                    if (prev.Hash != null && hash != null && prev.Hash != hash)
                        return new BeginResult { Kind = BeginKind.Mismatch, Previous = prev };
                    if (prev.DocPrint != null && docPrint != null && prev.DocPrint != docPrint)
                        return new BeginResult { Kind = BeginKind.DocumentReplaced, Previous = prev };
                    if (prev.Finished != null && IsDone(prev.Estado))
                        return new BeginResult { Kind = BeginKind.Completed, Previous = prev };
                    if (!(prev.Finished != null && Status.ConfirmsNothingApplied(prev.Estado)))
                    {
                        // Sin final, error, parcial, desconocido o cancelado: sólo con una decisión explícita de reconciliación.
                        if (mode == ReconcileMode.None) return new BeginResult { Kind = BeginKind.Interrupted, Previous = prev };
                        if (mode == ReconcileMode.Applied)
                        {
                            MarkReconciled(prev, "aplicado", null);
                            return new BeginResult { Kind = BeginKind.Completed, Previous = prev };
                        }
                        MarkReconciled(prev, "no_aplicado", null);
                    }
                }
                else if (docPrint != null)
                {
                    // ¿La misma operación se registró en otro archivo con el mismo modelo (copia, Guardar como fuera del MCP)?
                    var copy = _index.Values.FirstOrDefault(r => r.Key == key && r.DocPrint == docPrint && r.Doc != doc &&
                                                                 !(r.Finished != null && Status.ConfirmsNothingApplied(r.Estado)));
                    if (copy != null)
                    {
                        if (copy.Hash != null && hash != null && copy.Hash != hash) return new BeginResult { Kind = BeginKind.Mismatch, Previous = copy, OtherDoc = copy.Doc };
                        if (mode == ReconcileMode.None) return new BeginResult { Kind = BeginKind.PossibleCopy, Previous = copy, OtherDoc = copy.Doc };
                        if (mode == ReconcileMode.Applied)
                        {
                            Write(new Dictionary<string, object> { ["ev"] = "alias", ["de"] = copy.Doc, ["a"] = doc });
                            var aliased = _index[Id(doc, key)];
                            MarkReconciled(aliased, "aplicado", null);
                            return new BeginResult { Kind = BeginKind.Completed, Previous = aliased };
                        }
                    }
                }
                Write(new Dictionary<string, object>
                {
                    ["ev"] = "inicio", ["clave"] = key, ["doc"] = doc, ["op"] = op, ["hash"] = hash, ["tarea"] = task, ["sesion"] = session,
                    ["huella_doc"] = docPrint, ["generacion"] = generation,
                });
                _running[id] = "";
                return new BeginResult { Kind = BeginKind.New, Previous = _index.TryGetValue(id, out var now) ? now : null };
            }
        }

        private void MarkReconciled(Record r, string decision, object evidence)
        {
            Write(new Dictionary<string, object> { ["ev"] = "reconciliado", ["clave"] = r.Key, ["doc"] = r.Doc, ["decision"] = decision, ["evidencia"] = evidence });
        }

        /// <summary>Asocia el trabajo (job_id) que lleva la operación: repetir la clave mientras corre devuelve ese mismo job.</summary>
        public void LinkJob(string key, string doc, string jobId)
        {
            lock (_lock)
            {
                var id = Id(doc, key);
                Write(new Dictionary<string, object> { ["ev"] = "job", ["clave"] = key, ["doc"] = doc, ["job_id"] = jobId });
                if (_running.ContainsKey(id)) _running[id] = jobId ?? "";
            }
        }

        public string RunningJob(string key, string doc)
        {
            lock (_lock) return _running.TryGetValue(Id(doc, key), out var j) ? j : null;
        }

        /// <summary>
        /// Registra el final. en_proceso NO es final y no hace nada (el trabajo sigue y lo registrará al terminar).
        /// Lanza <see cref="JournalUnavailableException"/> si no puede escribir: quien llama debe avisar de que el final no consta.
        /// </summary>
        public void Complete(string key, string doc, string estado, object result)
        {
            if (estado == Status.EnProceso || estado == Status.EnCola || estado == Status.CancelacionSolicitada) return;
            lock (_lock)
            {
                var id = Id(doc, key);
                try
                {
                    Write(new Dictionary<string, object> { ["ev"] = "fin", ["clave"] = key, ["doc"] = doc, ["estado"] = estado, ["resultado"] = Trim(result) });
                }
                finally
                {
                    _running.Remove(id);
                }
            }
        }

        /// <summary>Guardar como / documento nuevo guardado: las operaciones del documento anterior valen también para el nuevo.</summary>
        public void Alias(string fromDoc, string toDoc)
        {
            if (fromDoc == null || toDoc == null || fromDoc == toDoc) return;
            lock (_lock) Write(new Dictionary<string, object> { ["ev"] = "alias", ["de"] = fromDoc, ["a"] = toDoc });
        }

        /// <summary>Reconciliación explícita (tarea/orden reconciliar): deja constancia de la decisión y su evidencia.</summary>
        public bool Reconcile(string key, string doc, bool applied, object evidence)
        {
            lock (_lock)
            {
                Refresh();
                if (!_index.TryGetValue(Id(doc, key), out var r)) return false;
                if (_running.ContainsKey(Id(doc, key))) throw new InvalidOperationException("la operación sigue en curso; espere a que termine");
                MarkReconciled(r, applied ? "aplicado" : "no_aplicado", evidence);
                if (!applied)
                {
                    // Queda explícitamente como no aplicada: un intento nuevo con la misma huella podrá correr.
                    Write(new Dictionary<string, object> { ["ev"] = "fin", ["clave"] = key, ["doc"] = doc, ["estado"] = Status.Revertido, ["resultado"] = Trim(evidence) });
                }
                return true;
            }
        }

        private static object Trim(object result)
        {
            if (result == null) return null;
            var txt = Json.Write(result);
            return txt.Length <= 4000 ? result : new Dictionary<string, object> { ["resumen"] = txt.Substring(0, 4000) + "…" };
        }

        public Record Get(string key, string doc)
        {
            lock (_lock)
            {
                try { Refresh(); } catch (JournalUnavailableException) { }
                return _index.TryGetValue(Id(doc, key), out var r) ? r : null;
            }
        }

        /// <summary>Registros de una tarea (para retomar tras reconectar).</summary>
        public List<Record> ForTask(string task)
        {
            lock (_lock)
            {
                try { Refresh(); } catch (JournalUnavailableException) { }
                var seen = new HashSet<Record>();
                var list = new List<Record>();
                foreach (var r in _index.Values) if (task != null && r.Task == task && seen.Add(r)) list.Add(r);
                return list;
            }
        }

        /// <summary>
        /// Huella del contenido de una operación (H03): argumentos canónicos recursivos sin campos de control, más el contenido
        /// efectivamente resuelto (el código de nombre=/archivo=, la versión de la receta…). Cambiar el archivo cambia la huella.
        /// </summary>
        public static string HashArgs(string op, IDictionary<string, object> args, string resolvedContent = null)
        {
            var canon = OpSemantics.Canonical(args ?? new Dictionary<string, object>());
            var content = resolvedContent == null ? "" : "|contenido:" + Home.Sha256Hex(resolvedContent);
            return Home.Sha256Hex(op + "|" + canon + content, 16);
        }
    }
}
