// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Append-only, hash-chained JSONL file: each line carries <c>seq</c> (1, 2, 3…), <c>prev</c> (hash of the previous one) and
    /// <c>h</c> = sha256(canonical form of the line without h). Each append is written in one go and flushed to disk (WriteThrough + Flush(true)):
    /// a complete line on disk is a committed fact; a torn final line (crash mid-write) was never committed and is
    /// trimmed on open. Any other defect (unreadable line in the middle, seq gap, hash or chain mismatch) is corruption:
    /// <see cref="Open"/> reports it as an error and nothing is recovered automatically.
    /// One writer per file: before appending, it checks that the file length is the one this writer left; if another process
    /// wrote, <see cref="ConcurrentWriterException"/> is thrown and nothing is written.
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

        /// <summary>Maximum bytes per line; a longer line is corruption (avoids unbounded memory allocation).</summary>
        public const int MaxLineBytes = 16 * 1024 * 1024;

        /// <summary>Compatibility: accumulates the records in a list (small files only).</summary>
        public static ChainLog Open(string path, bool repairTornTail, List<Dictionary<string, object>> records, List<Diagnostic> diags, string label) =>
            Open(path, repairTornTail, r => { records.Add(r); return true; }, diags, label);

        /// <summary>
        /// Reads and validates the file BY STREAMING (alpha3, P1-C): memory bounded by the longest line, not by the journal size.
        /// Each validated record (seq, prev, h) is handed to <paramref name="onRecord"/>; if it returns false, reading stops
        /// (the caller has already added its diagnostic). <paramref name="repairTornTail"/>: physically trims an incomplete last line
        /// (only the writer with authority). On any error the caller must discard whatever it has reconstructed.
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
                    if (line.Length > MaxLineBytes) { diags.Add(Diagnostic.Error(label + "_linea_demasiado_larga", "after seq " + seq + ": more than " + MaxLineBytes + " bytes without a newline")); stopped = true; }
                    pos += n;
                }
            }
            if (!stopped && line.Length > 0)
                diags.Add(Diagnostic.Warn(label + "_linea_final_truncada", line.Length + " uncommitted bytes after seq " + seq + (repairTornTail ? "; trimmed" : "")));
            if (goodEnd < fileLen && repairTornTail && !Diagnostic.AnyError(diags))
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { fs.SetLength(goodEnd); fs.Flush(true); }
            log.LastSeq = seq; log.LastHash = prev; log._length = goodEnd;
            return log;

            bool Accept(MemoryStream ms, bool isLast)
            {
                Dictionary<string, object> rec = null;
                try { rec = Json.ParseObject(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); } catch (FormatException) { }
                if (rec == null) { diags.Add(Diagnostic.Error(label + "_linea_ilegible", "after seq " + seq + (isLast ? " (last line, complete but unreadable)" : ""))); return false; }
                var s = Arg.GetLong(rec, "seq");
                var h = Arg.GetString(rec, "h");
                var p = Arg.GetString(rec, "prev");
                if (s != seq + 1) { diags.Add(Diagnostic.Error(label + "_seq_no_consecutiva", "expected " + (seq + 1) + ", read " + (s?.ToString() ?? "nothing"))); return false; }
                if (p != prev) { diags.Add(Diagnostic.Error(label + "_cadena_rota", "seq " + s + ": prev does not match")); return false; }
                if (h == null || h != HashOf(rec)) { diags.Add(Diagnostic.Error(label + "_hash_invalido", "seq " + s + ": content altered")); return false; }
                seq = s.Value; prev = h;
                return onRecord(rec);
            }
        }

        /// <summary>Appends a record: adds seq/prev/h, writes it in one go and flushes it to disk. Returns the final record.</summary>
        public Dictionary<string, object> Append(Dictionary<string, object> body)
        {
            var rec = new Dictionary<string, object>(body, StringComparer.Ordinal) { ["seq"] = LastSeq + 1, ["prev"] = LastHash };
            rec["h"] = HashOf(rec);
            var bytes = Encoding.UTF8.GetBytes(Json.Write(rec) + "\n");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            using (var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                if (fs.Length != _length) throw new ConcurrentWriterException("another process wrote to " + System.IO.Path.GetFileName(_path) + " (length " + fs.Length + ", expected " + _length + ")");
                fs.Seek(_length, SeekOrigin.Begin);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            _length += bytes.Length;
            LastSeq = (long)rec["seq"];
            LastHash = (string)rec["h"];
            return rec;
        }

        /// <summary>Writes an incomplete line (only to simulate a crash mid-write in tests).</summary>
        internal void AppendTornForTest(Dictionary<string, object> body)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) { fs.Write(bytes, 0, bytes.Length / 2); fs.Flush(true); }
        }
    }
}
