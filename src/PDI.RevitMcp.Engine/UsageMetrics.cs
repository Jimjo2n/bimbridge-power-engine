// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Observabilidad del consumo (§6): por llamada del agente se registran herramienta, operación, caracteres recibidos y devueltos,
    /// duración y estado. Son CARACTERES, no tokens: los tokens reales sólo los conoce el cliente (Codex/Claude). Un archivo JSONL por día
    /// en metricas\ (se borran los de más de 14 días); sin argumentos ni resultados, sólo tamaños.
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

        public static string Folder => _folder ?? (_folder = Home.Sub("metricas"));

        public static void Record(string client, string tool, string op, long charsIn, long charsOut, long ms, string estado)
        {
            var error = estado != null && estado != "ok" && estado != "completado_con_diferencias";
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
                    foreach (var f in Directory.GetFiles(Folder, "puente-*.jsonl"))
                        if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
                }
                var line = Json.Write(new Dictionary<string, object>
                {
                    ["t"] = Home.Now(), ["cliente"] = client, ["herramienta"] = tool, ["op"] = op,
                    ["chars_entrada"] = charsIn, ["chars_salida"] = charsOut, ["ms"] = ms, ["estado"] = estado,
                }) + "\n";
                File.AppendAllText(Path.Combine(Folder, "puente-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl"), line);
            }
            catch (Exception) { /* la métrica nunca rompe una llamada */ }
        }

        /// <summary>Resumen de este proceso del puente (desde que arrancó).</summary>
        public static Dictionary<string, object> Summary()
        {
            lock (Lock)
            {
                var rows = ByTool.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(kv => (object)new List<object> { kv.Key, kv.Value.Calls, kv.Value.CharsIn, kv.Value.CharsOut, kv.Value.Calls == 0 ? 0L : kv.Value.Ms / kv.Value.Calls, kv.Value.Errors })
                    .ToList();
                return new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "herramienta", "llamadas", "chars_entrada", "chars_salida", "ms_medio", "errores" },
                    ["rows"] = rows,
                    ["llamadas"] = ByTool.Values.Sum(r => r.Calls),
                    ["chars_total"] = ByTool.Values.Sum(r => r.CharsIn + r.CharsOut),
                    ["tokens_estimados"] = TokenEstimate.FromChars(ByTool.Values.Sum(r => r.CharsIn + r.CharsOut)),
                    ["tokens_metodo"] = TokenEstimate.Method,
                    ["nota"] = "caracteres intercambiados con el agente; los tokens reales los informa el cliente",
                };
            }
        }

        public static void Reset() { lock (Lock) ByTool.Clear(); }
    }
}
