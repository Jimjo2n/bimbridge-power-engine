// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Rows already read from Revit (simple values only, never API objects).
    /// Here they are filtered, grouped, paged and converted to the compact format the AI sees:
    /// header once, rows as arrays. Saves 40–70 % compared to JSON per object.
    /// </summary>
    public sealed class ResultTable
    {

        public static object Build(QuerySpec q, IEnumerable<Dictionary<string, object>> rows, out List<long> matchedIds, int maxGroups = 500)
            => Render(q, Filter(q, rows, out matchedIds), q.Page, q.PageSize, maxGroups);

        /// <summary>Converts units (in place) and applies where. What it returns must not pass through here again.</summary>
        public static List<Dictionary<string, object>> Filter(QuerySpec q, IEnumerable<Dictionary<string, object>> rows, out List<long> matchedIds)
        {
            matchedIds = new List<long>();
            var filtered = new List<Dictionary<string, object>>();
            foreach (var r in rows)
            {
                if (!NormalizeAndMatch(q, r)) continue;
                filtered.Add(r);
                if (Get(r, "id") is long id) matchedIds.Add(id);
            }
            return filtered;
        }

        private static bool NormalizeAndMatch(QuerySpec q, Dictionary<string, object> row)
        {
            if (q.Millimeters) ToMillimeters(row);
            return q.Where.All(c => c.Matches(Get(row, c.Field)));
        }

        public sealed class GroupAccumulator
        {
            private readonly QuerySpec _spec;
            private readonly Dictionary<string, object[]> _groups = new Dictionary<string, object[]>(StringComparer.Ordinal);
            private readonly List<string> _order = new List<string>();
            private long _total;

            public GroupAccumulator(QuerySpec spec) { _spec = spec; }

            public void Add(Dictionary<string, object> row)
            {
                if (NormalizeAndMatch(_spec, row)) AddFiltered(row);
            }

            internal void AddFiltered(Dictionary<string, object> row)
            {
                var parts = new object[_spec.GroupBy.Count];
                for (var i = 0; i < parts.Length; i++) parts[i] = Get(row, _spec.GroupBy[i]);
                var key = string.Join("\u001f", Array.ConvertAll(parts, v => v == null ? "\u0000" : Convert.ToString(v, CultureInfo.InvariantCulture)));
                if (!_groups.TryGetValue(key, out var group))
                {
                    group = new object[parts.Length + 1];
                    Array.Copy(parts, group, parts.Length);
                    group[parts.Length] = 0L;
                    _groups.Add(key, group);
                    _order.Add(key);
                }
                group[parts.Length] = (long)group[parts.Length] + 1;
                _total++;
            }

            public Dictionary<string, object> Render(int maxGroups = 500)
            {
                var order = new List<string>(_order);
                order.Sort(StringComparer.OrdinalIgnoreCase);
                var result = new Dictionary<string, object>
                {
                    ["total"] = _total, ["cols"] = new List<object>(_spec.GroupBy) { "n" },
                    ["groups"] = order.Take(maxGroups).Select(key => (object)_groups[key]).ToList(),
                };
                if (order.Count > maxGroups) result["warning"] = "more than " + maxGroups + " groups; showing the first ones";
                return result;
            }
        }

        /// <summary>True if the query returns pageable rows (no digest, count or grouping).</summary>
        public static bool IsPagedListing(QuerySpec q) => !q.Digest && !q.CountOnly && q.GroupBy.Count == 0;

        /// <summary>Formats already-filtered rows. Pure: does not modify the rows (used to page a snapshot).</summary>
        public static object Render(QuerySpec q, IReadOnlyList<Dictionary<string, object>> filtered, int page, int pageSize, int maxGroups = 500)
        {
            var result = new Dictionary<string, object> { ["total"] = (long)filtered.Count };

            if (q.Digest)
            {
                // Canonical order of all filtered rows. Duplicates and full texts are kept.
                var canonicalRows = filtered.Select(row => OpSemantics.Canonical(q.Fields.Select(f => Get(row, f)).ToList()))
                    .OrderBy(row => row, StringComparer.Ordinal);
                var payload = OpSemantics.Canonical(new object[]
                {
                    "query-rows-v1", q.Millimeters ? "mm" : "ft", q.Fields, canonicalRows.ToList(),
                });
                using (var sha = SHA256.Create())
                    result["sha256"] = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
                result["digest_format"] = "query-rows-v1";
                return result;
            }

            if (q.CountOnly && q.GroupBy.Count == 0) return result;

            if (q.GroupBy.Count > 0)
            {
                var groups = new GroupAccumulator(q);
                foreach (var r in filtered) groups.AddFiltered(r);
                return groups.Render(maxGroups);
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
            if (q.Millimeters) result["units"] = "mm";
            if (trimmed > 0) result["text_warning"] = trimmed + " texts truncated to " + QuerySpec.MaxTextLength + " characters; use full=true to see them in full";
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
