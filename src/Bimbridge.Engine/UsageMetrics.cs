// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Usage observability (§6): for each agent call, tool, operation, characters received and returned,
    /// duration and status are recorded. These are CHARACTERS, not tokens: only the client (Codex/Claude) knows the real tokens. One JSONL file per day
    /// in metricas\ (files older than 14 days are deleted); no arguments or results, only sizes.
    /// </summary>
    public static class UsageMetrics
    {
        public sealed class Row
        {
            public long Calls;
            public long CharsIn;
            public long CharsOut;
            public long Ms;
            public long Errors;
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Row> ByTool = new Dictionary<string, Row>(StringComparer.Ordinal);
        private static string _folder;
        private static bool _cleaned;
        public static bool WriteFiles = true;

        public static string Folder => _folder ?? (_folder = Home.Sub("metrics"));

        public static void Record(string client, string tool, string op, long charsIn, long charsOut, long ms, string status)
        {
            var error = status != null && status != "ok" && status != "completed_with_differences";
            lock (Lock)
            {
                if (!ByTool.TryGetValue(tool, out var r)) ByTool[tool] = r = new Row();
                r.Calls++;
                r.CharsIn += charsIn;
                r.CharsOut += charsOut;
                r.Ms += ms;
                if (error) r.Errors++;
            }
            if (!WriteFiles) return;
            try
            {
                Directory.CreateDirectory(Folder);
                if (!_cleaned)
                {
                    _cleaned = true;
                    foreach (var f in Directory.GetFiles(Folder, "bridge-*.jsonl"))
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
                }
                var line = Json.Write(new Dictionary<string, object>
                {
                    ["t"] = Home.Now(), ["client"] = client, ["tool_name"] = tool, ["op"] = op,
                    ["chars_in"] = charsIn, ["chars_out"] = charsOut, ["ms"] = ms, ["status"] = status,
                }) + "\n";
                File.AppendAllText(Path.Combine(Folder, "bridge-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl"), line);
            }
            catch (Exception) { /* metrics never break a call */ }
        }

        /// <summary>Summary of this bridge process (since it started).</summary>
        public static Dictionary<string, object> Summary()
        {
            lock (Lock)
            {
                var rows = ByTool.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(kv => (object)new List<object> { kv.Key, kv.Value.Calls, kv.Value.CharsIn, kv.Value.CharsOut, kv.Value.Calls == 0 ? 0L : kv.Value.Ms / kv.Value.Calls, kv.Value.Errors })
                    .ToList();
                return new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "tool_name", "calls", "chars_in", "chars_out", "mean_ms", "errors" },
                    ["rows"] = rows,
                    ["calls"] = ByTool.Values.Sum(r => r.Calls),
                    ["chars_total"] = ByTool.Values.Sum(r => r.CharsIn + r.CharsOut),
                    ["estimated_tokens"] = TokenEstimate.FromChars(ByTool.Values.Sum(r => r.CharsIn + r.CharsOut)),
                    ["tokens_method"] = TokenEstimate.Method,
                    ["note"] = "characters exchanged with the agent; the client reports the real tokens",
                };
            }
        }

        public static void Reset() { lock (Lock) ByTool.Clear(); }
    }
}
