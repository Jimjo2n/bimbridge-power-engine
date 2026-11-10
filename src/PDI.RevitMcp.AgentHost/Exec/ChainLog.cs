// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Archivo JSONL de sólo anexar con cadena de hash: cada línea lleva <c>seq</c> (1, 2, 3…), <c>prev</c> (hash de la anterior) y
    /// <c>h</c> = sha256(canónico de la línea sin h). Cada anexo se escribe de una vez y se vacía al disco (WriteThrough + Flush(true)):
    /// una línea completa en disco es un hecho confirmado; una línea final cortada (caída a mitad de escritura) nunca se confirmó y se
    /// recorta al abrir. Cualquier otro defecto (línea ilegible en medio, hueco de seq, hash o cadena que no cuadran) es corrupción:
    /// <see cref="Open"/> lo informa como error y no se recupera nada automáticamente.
    /// Un solo escritor por archivo: antes de anexar se comprueba que la longitud del archivo es la que este escritor dejó; si otro proceso
    /// escribió, se lanza <see cref="ConcurrentWriterException"/> y no se escribe.
    /// </summary>
    public sealed class ChainLog
    {
        public const string Genesis = "0";
        private readonly string _path;
        private long _length;
        public long LastSeq { get; private set; }
        public string LastHash { get; private set; } = Genesis;
        public string Path => _path;

        private ChainLog(string path) { _path = path; }

        public sealed class ConcurrentWriterException : IOException { public ConcurrentWriterException(string m) : base(m) { } }

        public static string HashOf(Dictionary<string, object> rec)
        {
            var copy = new Dictionary<string, object>(rec, StringComparer.Ordinal);
            copy.Remove("h");
            return Home.Sha256Hex(OpSemantics.Canonical(copy), 32);
        }

        /// <summary>Máximo de bytes por línea; una línea mayor es corrupción (evita reservar memoria sin límite).</summary>
        public const int MaxLineBytes = 16 * 1024 * 1024;

        /// <summary>Compatibilidad: acumula los registros en una lista (sólo para archivos pequeños).</summary>
        public static ChainLog Open(string path, bool repairTornTail, List<Dictionary<string, object>> records, List<Diagnostic> diags, string label) =>
            Open(path, repairTornTail, r => { records.Add(r); return true; }, diags, label);

        /// <summary>
        /// Lee y valida el archivo EN STREAMING (alpha3, P1-C): memoria acotada por la línea más larga, no por el tamaño del diario.
        /// Cada registro validado (seq, prev, h) se entrega a <paramref name="onRecord"/>; si devuelve false, se deja de leer
        /// (el llamador ya añadió su diagnóstico). <paramref name="repairTornTail"/>: recorta físicamente una última línea incompleta
        /// (sólo el escritor con autoridad). Ante cualquier error el llamador debe descartar lo que haya reconstruido.
        /// </summary>
        public static ChainLog Open(string path, bool repairTornTail, Func<Dictionary<string, object>, bool> onRecord, List<Diagnostic> diags, string label)
        {
            var log = new ChainLog(path);
            if (!File.Exists(path)) return log;
            long pos = 0, goodEnd = 0, fileLen;
            var prev = Genesis;
            long seq = 0;
            var line = new MemoryStream();
            var stopped = false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                fileLen = fs.Length;
                var buf = new byte[1 << 16];
                int n;
                while (!stopped && pos < fileLen && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, fileLen - pos))) > 0)
                {
                    var start = 0;
                    for (var i = 0; i < n && !stopped; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        line.Write(buf, start, i - start);
                        var lineEnd = pos + i + 1;
                        if (!Accept(line, lineEnd == fileLen)) { stopped = true; break; }
                        goodEnd = lineEnd;
                        line.SetLength(0);
                        start = i + 1;
                    }
                    if (stopped) break;
                    line.Write(buf, start, n - start);
                    if (line.Length > MaxLineBytes) { diags.Add(Diagnostic.Error(label + "_linea_demasiado_larga", "tras seq " + seq + ": más de " + MaxLineBytes + " bytes sin salto")); stopped = true; }
                    pos += n;
                }
            }
            if (!stopped && line.Length > 0)
                diags.Add(Diagnostic.Warn(label + "_linea_final_truncada", line.Length + " bytes sin confirmar tras seq " + seq + (repairTornTail ? "; recortados" : "")));
            if (goodEnd < fileLen && repairTornTail && !Diagnostic.AnyError(diags))
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { fs.SetLength(goodEnd); fs.Flush(true); }
            log.LastSeq = seq; log.LastHash = prev; log._length = goodEnd;
            return log;

            bool Accept(MemoryStream ms, bool isLast)
            {
                Dictionary<string, object> rec = null;
                try { rec = Json.ParseObject(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); } catch (FormatException) { }
                if (rec == null) { diags.Add(Diagnostic.Error(label + "_linea_ilegible", "tras seq " + seq + (isLast ? " (última línea, completa pero ilegible)" : ""))); return false; }
                var s = Arg.GetLong(rec, "seq");
                var h = Arg.GetString(rec, "h");
                var p = Arg.GetString(rec, "prev");
                if (s != seq + 1) { diags.Add(Diagnostic.Error(label + "_seq_no_consecutiva", "esperada " + (seq + 1) + ", leída " + (s?.ToString() ?? "nada"))); return false; }
                if (p != prev) { diags.Add(Diagnostic.Error(label + "_cadena_rota", "seq " + s + ": prev no coincide")); return false; }
                if (h == null || h != HashOf(rec)) { diags.Add(Diagnostic.Error(label + "_hash_invalido", "seq " + s + ": contenido alterado")); return false; }
                seq = s.Value; prev = h;
                return onRecord(rec);
            }
        }

        /// <summary>Anexa un registro: le añade seq/prev/h, lo escribe de una vez y lo vacía al disco. Devuelve el registro final.</summary>
        public Dictionary<string, object> Append(Dictionary<string, object> body)
        {
            var rec = new Dictionary<string, object>(body, StringComparer.Ordinal) { ["seq"] = LastSeq + 1, ["prev"] = LastHash };
            rec["h"] = HashOf(rec);
            var bytes = Encoding.UTF8.GetBytes(Json.Write(rec) + "\n");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            using (var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                if (fs.Length != _length) throw new ConcurrentWriterException("otro proceso escribió en " + System.IO.Path.GetFileName(_path) + " (longitud " + fs.Length + ", esperada " + _length + ")");
                fs.Seek(_length, SeekOrigin.Begin);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            _length += bytes.Length;
            LastSeq = (long)rec["seq"];
            LastHash = (string)rec["h"];
            return rec;
        }

        /// <summary>Escribe una línea incompleta (sólo para simular una caída a mitad de escritura en pruebas).</summary>
        internal void AppendTornForTest(Dictionary<string, object> body)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) { fs.Write(bytes, 0, bytes.Length / 2); fs.Flush(true); }
        }
    }
}
