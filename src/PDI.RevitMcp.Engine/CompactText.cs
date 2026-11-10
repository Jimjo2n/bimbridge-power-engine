// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Formato de texto compacto para la IA (perfil compacto). Mismo contenido que el JSON, menos tokens:
    /// - sin comillas, llaves ni nombres de campo repetidos;
    /// - tablas como "col|col" + una fila por línea;
    /// - columnas con un solo valor salen una vez ("fijo:");
    /// - columnas de texto muy repetido se codifican con un diccionario ("col#: 1=…, 2=…");
    /// - listas de ids consecutivos se acortan a rangos (17858356-17858370).
    /// Es reversible para una persona o una IA; no se pierde información.
    /// </summary>
    public static class CompactText
    {
        private static readonly string[] First = { "estado", "aviso" };

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            if (value is IDictionary<string, object> d) WriteDict(sb, d, 0);
            else if (value is IList l) WriteList(sb, null, l, 0);
            else sb.Append(Scalar(value));
            return sb.ToString().TrimEnd('\n');
        }

        private static void WriteDict(StringBuilder sb, IDictionary<string, object> d, int indent)
        {
            var keys = d.Keys.ToList();
            var ordered = First.Where(d.ContainsKey).Concat(keys.Where(k => Array.IndexOf(First, k) < 0)).ToList();
            var isTable = d.ContainsKey("cols") && (d.ContainsKey("rows") || d.ContainsKey("groups"));
            foreach (var k in ordered)
            {
                if (isTable && (k == "cols" || k == "rows" || k == "groups")) continue;
                WriteKey(sb, k, d[k], indent);
            }
            if (isTable)
            {
                var cols = (d["cols"] as IList)?.Cast<object>().Select(c => Convert.ToString(c, CultureInfo.InvariantCulture)).ToList() ?? new List<string>();
                var rows = (d.TryGetValue("rows", out var r) ? r : d["groups"]) as IList;
                WriteTable(sb, cols, rows?.Cast<object>().Select(AsRow).ToList() ?? new List<object[]>(), indent);
            }
        }

        private static object[] AsRow(object row)
        {
            if (row is object[] a) return a;
            if (row is IList l) return l.Cast<object>().ToArray();
            return new[] { row };
        }

        private static void WriteKey(StringBuilder sb, string k, object v, int indent)
        {
            var pad = new string(' ', indent);
            if (v is IDictionary<string, object> nd)
            {
                if (nd.Count == 0) { sb.Append(pad).Append(k).Append(": -\n"); return; }
                if (nd.Values.All(IsScalar) && !nd.ContainsKey("cols"))
                {
                    var inline = string.Join(", ", nd.Select(kv => kv.Key + "=" + Scalar(kv.Value)));
                    if (inline.Length <= 160) { sb.Append(pad).Append(k).Append(": ").Append(inline).Append('\n'); return; }
                }
                sb.Append(pad).Append(k).Append(":\n");
                WriteDict(sb, nd, indent + 2);
                return;
            }
            if (v is IList list && !(v is string))
            {
                WriteList(sb, k, list, indent);
                return;
            }
            sb.Append(pad).Append(k).Append(": ").Append(Scalar(v)).Append('\n');
        }

        private static void WriteList(StringBuilder sb, string k, IList list, int indent)
        {
            var pad = new string(' ', indent);
            var items = list.Cast<object>().ToList();
            var label = k == null ? "" : k + ": ";
            if (items.Count == 0) { sb.Append(pad).Append(label).Append("-\n"); return; }
            if (items.All(IsScalar))
            {
                sb.Append(pad).Append(label).Append(Ranges(items)).Append('\n');
                return;
            }
            // Lista de objetos con las mismas claves → tabla
            if (items.All(i => i is IDictionary<string, object> x && x.Values.All(IsScalar)))
            {
                var cols = new List<string>();
                foreach (IDictionary<string, object> x in items) foreach (var key in x.Keys) if (!cols.Contains(key)) cols.Add(key);
                if (k != null) sb.Append(pad).Append(k).Append(":\n");
                var rows = items.Select(i => { var x = (IDictionary<string, object>)i; return cols.Select(c => x.TryGetValue(c, out var v) ? v : null).ToArray(); }).ToList();
                WriteTable(sb, cols, rows, indent + (k != null ? 2 : 0));
                return;
            }
            if (k != null) sb.Append(pad).Append(k).Append(":\n");
            var n = 0;
            foreach (var item in items)
            {
                n++;
                if (item is IDictionary<string, object> nd) { sb.Append(pad).Append("  - [").Append(n).Append("]\n"); WriteDict(sb, nd, indent + 4); }
                else if (item is IList il) WriteList(sb, "- [" + n + "]", il, indent + 2);
                else sb.Append(pad).Append("  - ").Append(Scalar(item)).Append('\n');
            }
        }

        private static void WriteTable(StringBuilder sb, List<string> cols, List<object[]> rows, int indent)
        {
            var pad = new string(' ', indent);
            if (rows.Count == 0) { sb.Append(pad).Append("filas: 0 (").Append(string.Join("|", cols)).Append(")\n"); return; }
            var keep = new List<int>();
            var fixedParts = new List<string>();
            for (var c = 0; c < cols.Count; c++)
            {
                var first = Cell(rows[0], c);
                if (rows.Count > 2 && rows.All(r => Cell(r, c) == first))
                    fixedParts.Add(cols[c] + "=" + (first.Length == 0 ? "(vacío)" : first));
                else keep.Add(c);
            }
            if (fixedParts.Count > 0) sb.Append(pad).Append("fijo: ").Append(string.Join("; ", fixedParts)).Append('\n');
            if (keep.Count == 0) { sb.Append(pad).Append("filas: ").Append(rows.Count).Append('\n'); return; }

            // Diccionario por columna cuando ahorra al menos un 30 % de esa columna
            var dict = new Dictionary<int, Dictionary<string, int>>();
            foreach (var c in keep)
            {
                var vals = rows.Select(r => Cell(r, c)).ToList();
                if (vals.Any(v => IsNumberText(v))) continue;
                var distinct = vals.Where(v => v.Length > 0).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                if (distinct.Count == 0 || distinct.Count > 40) continue;
                var plain = vals.Sum(v => v.Length);
                var coded = distinct.Sum(v => v.Length + 4) + vals.Sum(v => v.Length == 0 ? 0 : (distinct.IndexOf(v) + 1).ToString(CultureInfo.InvariantCulture).Length);
                if (coded <= plain * 0.7)
                {
                    var map = new Dictionary<string, int>();
                    for (var i = 0; i < distinct.Count; i++) map[distinct[i]] = i + 1;
                    dict[c] = map;
                    sb.Append(pad).Append(cols[c]).Append("#: ").Append(string.Join(", ", distinct.Select((v, i) => (i + 1) + "=" + v))).Append('\n');
                }
            }
            sb.Append(pad).Append(string.Join("|", keep.Select(c => dict.ContainsKey(c) ? cols[c] + "#" : cols[c]))).Append('\n');
            foreach (var r in rows)
            {
                sb.Append(pad);
                for (var i = 0; i < keep.Count; i++)
                {
                    if (i > 0) sb.Append('|');
                    var c = keep[i];
                    var v = Cell(r, c);
                    if (dict.TryGetValue(c, out var map) && v.Length > 0) sb.Append(map[v]);
                    else sb.Append(v);
                }
                sb.Append('\n');
            }
        }

        private static string Cell(object[] r, int c) => c < r.Length ? Scalar(r[c]).Replace("|", "¦").Replace("\n", " ") : "";

        private static bool IsScalar(object v) => v == null || v is string || v is bool || v is long || v is int || v is double || v is float || v is decimal;

        private static bool IsNumberText(string v) =>
            v.Length > 0 && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        public static string Scalar(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s.Replace("\r", "").Replace("\n", "⏎");
                case bool b: return b ? "sí" : "no";
                case double d: return double.IsNaN(d) ? "" : d.ToString("0.######", CultureInfo.InvariantCulture);
                case float f: return ((double)f).ToString("0.######", CultureInfo.InvariantCulture);
                case IDictionary<string, object> nd: return "{" + string.Join(", ", nd.Select(kv => kv.Key + "=" + Scalar(kv.Value))) + "}";
                case IList l: return "[" + string.Join(", ", l.Cast<object>().Select(Scalar)) + "]";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Enteros consecutivos → a-b (sólo con 3 o más seguidos). El resto, separado por comas.</summary>
        public static string Ranges(IList<object> items)
        {
            if (items.Count < 3 || !items.All(i => i is long || i is int))
                return string.Join(", ", items.Select(Scalar));
            var nums = items.Select(i => Convert.ToInt64(i, CultureInfo.InvariantCulture)).ToList();
            var parts = new List<string>();
            var start = 0;
            for (var i = 1; i <= nums.Count; i++)
            {
                if (i < nums.Count && nums[i] == nums[i - 1] + 1) continue;
                var len = i - start;
                if (len >= 3) parts.Add(nums[start].ToString(CultureInfo.InvariantCulture) + "-" + nums[i - 1].ToString(CultureInfo.InvariantCulture));
                else for (var j = start; j < i; j++) parts.Add(nums[j].ToString(CultureInfo.InvariantCulture));
                start = i;
            }
            return string.Join(",", parts);
        }
    }
}
