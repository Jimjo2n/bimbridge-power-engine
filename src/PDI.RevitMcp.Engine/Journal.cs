// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Diario de auditoría JSONL (sección 14). Se escribe en un hilo de fondo, así que
    /// registrar una llamada no cuesta nada al hilo de Revit (problema D6 de pyRevit).
    /// Un archivo por día; se borran los de más de N días al arrancar.
    /// </summary>
    public sealed class Journal : IDisposable
    {
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(10000);
        private readonly string _dir;
        private readonly Thread _thread;
        private readonly object _recentLock = new object();
        private readonly LinkedList<Dictionary<string, object>> _recent = new LinkedList<Dictionary<string, object>>();
        private const int RecentMax = 200;

        public Journal(string directory, int keepDays)
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
            Prune(keepDays);
            _thread = new Thread(Run) { IsBackground = true, Name = "PDI.RevitMcp.Journal" };
            _thread.Start();
        }

        public static string DefaultDirectory
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("PDI_REVITMCP_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp");
                return Path.Combine(root, "journal");
            }
        }

        public void Log(string author, string op, string estado, long elapsedMs, Dictionary<string, object> extra = null)
        {
            var e = new Dictionary<string, object>
            {
                ["t"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["autor"] = author,
                ["op"] = op,
                ["estado"] = estado,
                ["ms"] = elapsedMs,
            };
            if (extra != null) foreach (var kv in extra) e[kv.Key] = kv.Value;
            lock (_recentLock)
            {
                _recent.AddLast(e);
                if (_recent.Count > RecentMax) _recent.RemoveFirst();
            }
            // TELEMETRÍA: si la cola se llena se pierde la línea antes que bloquear a Revit, pero se CUENTA.
            // Lo que debe quedar sí o sí va por AuditLog (síncrono, encadenado, falla visible).
            if (!_queue.TryAdd(Json.Write(e))) Interlocked.Increment(ref _dropped);
        }

        private long _dropped, _writeErrors;
        /// <summary>Líneas de telemetría perdidas (cola llena) y errores de escritura. Se muestran en el diagnóstico.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);
        public long WriteErrors => Interlocked.Read(ref _writeErrors);

        /// <summary>Últimas entradas en memoria (para la herramienta "journal").</summary>
        public List<Dictionary<string, object>> Recent(int max)
        {
            lock (_recentLock)
            {
                var list = new List<Dictionary<string, object>>();
                var node = _recent.Last;
                while (node != null && list.Count < max) { list.Add(node.Value); node = node.Previous; }
                list.Reverse();
                return list;
            }
        }

        private void Run()
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var file = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
                    File.AppendAllText(file, line + "\n");
                }
                catch (IOException) { Interlocked.Increment(ref _writeErrors); }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref _writeErrors); }
            }
        }

        private void Prune(int keepDays)
        {
            try
            {
                var limit = DateTime.UtcNow.Date.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(_dir, "*.jsonl"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) && d < limit)
                        File.Delete(f);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(2000);
        }
    }
}
