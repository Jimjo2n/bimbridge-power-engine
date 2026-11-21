// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// JSONL audit journal (section 14). Written on a background thread, so
    /// recording a call costs the Revit thread nothing (pyRevit problem D6).
    /// One file per day; files older than N days are deleted at startup.
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
            _thread = new Thread(Run) { IsBackground = true, Name = "Bimbridge.Journal" };
            _thread.Start();
        }

        public static string DefaultDirectory
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("BIMBRIDGE_HOME");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
                return Path.Combine(root, "journal");
            }
        }

        public void Log(string author, string op, string status, long elapsedMs, Dictionary<string, object> extra = null)
        {
            var e = new Dictionary<string, object>
            {
                ["t"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["author"] = author,
                ["op"] = op,
                ["status"] = status,
                ["ms"] = elapsedMs,
            };
            if (extra != null) foreach (var kv in extra) e[kv.Key] = kv.Value;
            lock (_recentLock)
            {
                _recent.AddLast(e);
                if (_recent.Count > RecentMax) _recent.RemoveFirst();
            }
            // TELEMETRY: if the queue fills up the line is dropped rather than blocking Revit, but it is COUNTED.
            // Anything that must be kept no matter what goes through AuditLog (synchronous, chained, visible failure).
            if (!_queue.TryAdd(Json.Write(e))) Interlocked.Increment(ref _dropped);
        }

        private long _dropped, _writeErrors;
        /// <summary>Dropped telemetry lines (queue full) and write errors. Shown in diagnostics.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);
        public long WriteErrors => Interlocked.Read(ref _writeErrors);

        /// <summary>Latest in-memory entries (for the "journal" tool).</summary>
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
