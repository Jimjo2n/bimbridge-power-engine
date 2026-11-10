// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Filas ya leídas de Revit (sólo valores simples, nunca objetos de la API).
    /// Aquí se filtran, agrupan, paginan y se convierten al formato compacto que ve la IA:
    /// encabezado una sola vez, filas como arreglos. Ahorra 40–70 % frente a JSON por objeto.
    /// </summary>
    public sealed class ResultTable
    {

        public static object Build(QuerySpec q, IEnumerable<Dictionary<string, object>> rows, out List<long> matchedIds, int maxGroups = 500)
            => Render(q, Filter(q, rows, out matchedIds), q.Page, q.PageSize, maxGroups);

        /// <summary>Convierte unidades (en el sitio) y aplica where. Lo que devuelve ya no debe volver a pasar por aquí.</summary>
        public static List<Dictionary<string, object>> Filter(QuerySpec q, IEnumerable<Dictionary<string, object>> rows, out List<long> matchedIds)
        {
            matchedIds = new List<long>();
            var filtered = new List<Dictionary<string, object>>();
            foreach (var r in rows)
            {
                if (q.Millimeters) ToMillimeters(r);
                var ok = true;
                foreach (var c in q.Where)
                    if (!c.Matches(Get(r, c.Field))) { ok = false; break; }
                if (!ok) continue;
                filtered.Add(r);
                if (Get(r, "id") is long id) matchedIds.Add(id);
            }
            return filtered;
        }

        /// <summary>True si la consulta devuelve filas paginables (ni digest, ni conteo, ni agrupación).</summary>
        public static bool IsPagedListing(QuerySpec q) => !q.Digest && !q.CountOnly && q.GroupBy.Count == 0;

        /// <summary>Formatea filas ya filtradas. Puro: no modifica las filas (sirve para paginar una instantánea).</summary>
        public static object Render(QuerySpec q, IReadOnlyList<Dictionary<string, object>> filtered, int page, int pageSize, int maxGroups = 500)
        {
            var result = new Dictionary<string, object> { ["total"] = (long)filtered.Count };

            if (q.Digest)
            {
                // Orden canónico de todas las filas filtradas. Se conservan duplicados y textos completos.
                var canonicalRows = filtered.Select(row => OpSemantics.Canonical(q.Fields.Select(f => Get(row, f)).ToList()))
                    .OrderBy(row => row, StringComparer.Ordinal);
                var payload = OpSemantics.Canonical(new object[]
                {
                    "query-rows-v1", q.Millimeters ? "mm" : "ft", q.Fields, canonicalRows.ToList(),
                });
                using (var sha = SHA256.Create())
                    result["sha256"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
                result["formato_digest"] = "query-rows-v1";
                return result;
            }

            if (q.CountOnly && q.GroupBy.Count == 0) return result;

            if (q.GroupBy.Count > 0)
            {
                var groups = new Dictionary<string, object[]>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (var r in filtered)
                {
                    var keyParts = new object[q.GroupBy.Count];
                    for (var i = 0; i < keyParts.Length; i++) keyParts[i] = Get(r, q.GroupBy[i]);
                    var key = string.Join("\u001f", Array.ConvertAll(keyParts, k => k == null ? "\u0000" : Convert.ToString(k, CultureInfo.InvariantCulture)));
                    if (!groups.TryGetValue(key, out var g))
                    {
                        g = new object[keyParts.Length + 1];
                        Array.Copy(keyParts, g, keyParts.Length);
                        g[keyParts.Length] = 0L;
                        groups[key] = g;
                        order.Add(key);
                    }
                    g[keyParts.Length] = (long)g[keyParts.Length] + 1;
                }
                order.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                var cols = new List<object>(q.GroupBy) { "n" };
                var list = new List<object>();
                foreach (var k in order)
                {
                    if (list.Count >= maxGroups) { result["aviso"] = "más de " + maxGroups + " grupos; se muestran los primeros"; break; }
                    list.Add(groups[k]);
                }
                result["cols"] = cols;
                result["groups"] = list;
                return result;
            }

            var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
            var start = (page - 1) * pageSize;
            var outRows = new List<object>();
            var trimmed = 0;
            for (var i = start; i < filtered.Count && i < start + pageSize; i++)
            {
                var r = filtered[i];
                var arr = new object[q.Fields.Count];
                for (var c = 0; c < arr.Length; c++)
                {
                    var v = Get(r, q.Fields[c]);
                    if (!q.FullText && v is string sv && sv.Length > QuerySpec.MaxTextLength)
                    {
                        v = Trim(sv);
                        trimmed++;
                    }
                    arr[c] = v;
                }
                outRows.Add(arr);
            }
            result["cols"] = new List<object>(q.Fields);
            result["rows"] = outRows;
            if (q.Millimeters) result["unidades"] = "mm";
            if (trimmed > 0) result["aviso_texto"] = trimmed + " textos recortados a " + QuerySpec.MaxTextLength + " caracteres; use full=true para verlos completos";
            if (pages > 1)
            {
                result["page"] = (long)page;
                result["pages"] = (long)pages;
            }
            return result;
        }

        public static string Trim(string s, int max = QuerySpec.MaxTextLength)
            => s == null || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static void ToMillimeters(Dictionary<string, object> r)
        {
            foreach (var k in new List<string>(r.Keys))
            {
                if (!QuerySpec.LengthFields.Contains(k)) continue;
                switch (r[k])
                {
                    case double d: r[k] = Math.Round(d * 304.8, 1); break;
                    case long l: r[k] = Math.Round(l * 304.8, 1); break;
                }
            }
        }

        private static object Get(Dictionary<string, object> r, string field)
        {
            if (r.TryGetValue(field, out var v)) return v;
            foreach (var kv in r)
                if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }
}
