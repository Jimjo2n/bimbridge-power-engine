// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Bimbridge.Engine.Coverage
{
    /// <summary>Capability search filter. Everything is optional; filters are combined with AND.</summary>
    public sealed class CapabilityQuery
    {
        public string Text;          // words (es/en, id, subdomain, command)
        public string Domain;        // F01..F37
        public string Status;        // CoverageStatus
        public string Strategy;      // prefix: typed, query, doc, exec, engine, postcommand, none…
        public int? Version;         // 2022..2027, depending on the capability
        public string Category;      // Revit category
        public string DocumentKind;  // project | family | any
        public string Priority;      // P0..P3
        public int Page = 1;
        public int PageSize;
        public bool Relevance;
    }

    /// <summary>
    /// 0.9.1: immutable index of the capability catalog. Built ONCE per process (lazily) from already compiled
    /// data: no reflection, no reading of Revit assemblies, no model traversal, and it does not enter the Revit thread.
    /// Indexes by id, domain, status, strategy, version, category, document kind and words. Searches return compact
    /// paginated rows; the full contract is only serialized when requested by id.
    /// </summary>
    public sealed class CapabilityIndex
    {
        public const int MaxPageSize = 100;
        private static readonly Lazy<CapabilityIndex> _default = new Lazy<CapabilityIndex>(() => new CapabilityIndex(CapabilityCatalog.All));
        public static CapabilityIndex Default => _default.Value;

        private readonly CapabilityEntry[] _all;
        private readonly Dictionary<string, int> _byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int[]> _byDomain, _byStatus, _byStrategy, _byCategory, _byDocKind, _byPriority;
        private readonly Dictionary<string, int[]> _byToken;
        private readonly string[] _tokenKeys;
        private readonly Lazy<string> _version;
        public string Version => _version.Value;

        public int Count => _all.Length;

        public CapabilityIndex(IEnumerable<CapabilityEntry> entries)
        {
            _all = entries.ToArray();
            for (var i = 0; i < _all.Length; i++)
            {
                if (_byId.ContainsKey(_all[i].Id)) throw new ArgumentException("repeated capability id: " + _all[i].Id);
                _byId[_all[i].Id] = i;
            }
            _byDomain = Group(c => new[] { c.Domain });
            _byStatus = Group(c => new[] { c.Status.ToString() });
            _byStrategy = Group(c => new[] { StrategyKind(c.Strategy) });
            _byCategory = Group(c => c.Categories ?? new string[0]);
            _byDocKind = Group(c => new[] { c.DocumentKind ?? "project" });
            _byPriority = Group(c => new[] { c.Priority ?? "P2" });
            _byToken = Group(c => Tokens(c.Id + " " + c.Name + " " + c.NameEn + " " + c.Subdomain + " " + c.Operation + " " + string.Join(" ", c.PostableCommands ?? new string[0])));
            _tokenKeys = _byToken.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            _version = new Lazy<string>(ComputeVersion);
        }

        /// <summary>
        /// AUD-16: fingerprint of the FULL canonical content (SHA-256 of each capability's whole contract, in id order): any
        /// change of name, limits, verifier, tests, commands, versions… changes the fingerprint. Lazy: costs nothing at load.
        /// </summary>
        private string ComputeVersion()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                foreach (var c in _all.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    var contract = Contract(c);
                    var canon = string.Join("\u001f", contract.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Key + "=" + (kv.Value is List<object> l ? string.Join("\u001e", l) : Convert.ToString(kv.Value, CultureInfo.InvariantCulture))));
                    canon += "\u001fmin=" + c.MinVersion + "\u001fmax=" + c.MaxVersion + "\u001d";
                    var b = Encoding.UTF8.GetBytes(canon);
                    sha.TransformBlock(b, 0, b.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            }
        }

        public static string StrategyKind(string s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var i = s.IndexOf(':');
            return (i > 0 ? s.Substring(0, i) : s).ToLowerInvariant();
        }

        private Dictionary<string, int[]> Group(Func<CapabilityEntry, IEnumerable<string>> keys)
        {
            var tmp = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _all.Length; i++)
                foreach (var k in keys(_all[i]).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!tmp.TryGetValue(k, out var l)) tmp[k] = l = new List<int>();
                    l.Add(i);
                }
            return tmp.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Normalized words (lowercase, no accents, split on non-alphanumerics and CamelCase), 2+ characters long.</summary>
        public static IEnumerable<string> Tokens(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var norm = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length + 8);
            char prev = ' ';
            foreach (var ch in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsUpper(ch) && char.IsLower(prev)) sb.Append(' ');
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
                prev = ch;
            }
            foreach (var t in sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) yield return t;
        }

        public CapabilityEntry Get(string id) => id != null && _byId.TryGetValue(id.Trim(), out var i) ? _all[i] : null;

        /// <summary>Searches and paginates. Intersection of sorted lists (without scanning the catalog if any indexed filter is present).</summary>
        public (IReadOnlyList<CapabilityEntry> Page, int Total) Search(CapabilityQuery q)
        {
            q = q ?? new CapabilityQuery();
            IEnumerable<int> set = null;
            void And(int[] ids) { set = set == null ? ids : set.Intersect(ids); }
            int[] Lookup(Dictionary<string, int[]> d, string k) => d.TryGetValue(k.Trim(), out var v) ? v : new int[0];
            if (!string.IsNullOrWhiteSpace(q.Domain)) And(Lookup(_byDomain, q.Domain));
            if (!string.IsNullOrWhiteSpace(q.Status)) And(Lookup(_byStatus, q.Status));
            if (!string.IsNullOrWhiteSpace(q.Strategy)) And(Lookup(_byStrategy, StrategyKind(q.Strategy)));
            if (!string.IsNullOrWhiteSpace(q.Category)) And(Lookup(_byCategory, q.Category));
            if (!string.IsNullOrWhiteSpace(q.DocumentKind)) And(Lookup(_byDocKind, q.DocumentKind).Concat(Lookup(_byDocKind, "any")).OrderBy(x => x).ToArray());
            if (!string.IsNullOrWhiteSpace(q.Priority)) And(Lookup(_byPriority, q.Priority));
            if (!q.Relevance && !string.IsNullOrWhiteSpace(q.Text))
                foreach (var t in Tokens(q.Text).Distinct())
                {
                    // prefix: "circ" finds "circuit"; bounded to the keys of the word index (not the catalog).
                    // Binary search for the first token >= t in the sorted keys, scanning only while they share the prefix.
                    var lo = Array.BinarySearch(_tokenKeys, t, StringComparer.Ordinal);
                    if (lo < 0) lo = ~lo;
                    var hits = new HashSet<int>();
                    for (var j = lo; j < _tokenKeys.Length && _tokenKeys[j].StartsWith(t, StringComparison.Ordinal); j++)
                        foreach (var x in _byToken[_tokenKeys[j]]) hits.Add(x);
                    And(hits.OrderBy(x => x).ToArray());
                }
            var ids = (set ?? Enumerable.Range(0, _all.Length));
            if (q.Version.HasValue) ids = ids.Where(i => _all[i].MinVersion <= q.Version && _all[i].MaxVersion >= q.Version);
            var list = q.Relevance && !string.IsNullOrWhiteSpace(q.Text)
                ? Rank(ids.Distinct(), q.Text).ToList()
                : ids.Distinct().OrderBy(i => _all[i].Id, StringComparer.Ordinal).ToList();
            var size = Math.Max(1, Math.Min(MaxPageSize, q.PageSize == 0 ? (q.Relevance ? 3 : 20) : q.PageSize));
            var page = Math.Max(1, q.Page);
            var offset = Math.Min(list.Count, ((long)page - 1) * size);
            return (list.Skip((int)offset).Take(size).Select(i => _all[i]).ToList(), list.Count);
        }

        private static readonly string[][] VdcAliases =
        {
            new[] { "fixture", "fixtures", "fixture", "fixtures", "lighting" },
            new[] { "link", "links", "link", "links", "linked" },
            new[] { "clash", "clashes", "clash", "clashes", "interference", "interference" },
            new[] { "pipe", "pipes", "pipe", "pipes", "piping" },
            new[] { "sheet", "sheets", "sheet", "sheets" },
            new[] { "tag", "tags_list", "tag", "tags" },
            new[] { "dimension", "dimensions", "dimension", "dimensions" },
        };

        private IEnumerable<int> Rank(IEnumerable<int> ids, string text)
        {
            var terms = Tokens(text).Distinct().ToArray();
            var phrase = string.Join(" ", Tokens(text));
            var scores = new Dictionary<int, int>();
            foreach (var term in terms)
            {
                if (_byToken.TryGetValue(term, out var hits))
                    foreach (var id in hits) scores[id] = (scores.TryGetValue(id, out var old) ? old : 0) + 10;
                var aliases = VdcAliases.FirstOrDefault(group => group.Contains(term));
                if (aliases == null) continue;
                var related = new HashSet<int>();
                foreach (var alias in aliases.Where(a => a != term))
                    if (_byToken.TryGetValue(alias, out var other)) related.UnionWith(other);
                foreach (var id in related)
                    if (hits == null || Array.BinarySearch(hits, id) < 0)
                        scores[id] = (scores.TryGetValue(id, out var old) ? old : 0) + 3;
            }
            if (_byId.TryGetValue(text.Trim(), out var exact)) scores[exact] = 10000;
            return ids.Where(scores.ContainsKey)
                .Select(i => (Index: i, Score: scores[i] + (phrase.Length > 0 &&
                    (string.Join(" ", Tokens(_all[i].Name)) == phrase || string.Join(" ", Tokens(_all[i].NameEn)) == phrase) ? 1000 : 0)))
                .OrderByDescending(x => x.Score).ThenBy(x => _all[x.Index].Id, StringComparer.Ordinal).Select(x => x.Index);
        }

        /// <summary>Compact row for discovery (the minimum to decide; the full contract is separate).</summary>
        public static List<object> Row(CapabilityEntry c) => new List<object> { c.Id, c.Name, c.Status.ToString(), c.Strategy ?? "none" };
        public static readonly List<object> RowColumns = new List<object> { "id", "capability", "status", "strategy" };

        /// <summary>Full contract (on demand only).</summary>
        public static Dictionary<string, object> Contract(CapabilityEntry c) => new Dictionary<string, object>
        {
            ["id"] = c.Id, ["domain"] = c.Domain, ["subdomain"] = c.Subdomain, ["name"] = c.Name, ["name_en"] = c.NameEn, ["operation"] = c.Operation,
            ["expected_result"] = c.Expected, ["categories"] = (c.Categories ?? new string[0]).Cast<object>().ToList(), ["document"] = c.DocumentKind,
            ["strategy"] = c.Strategy, ["executor"] = ExecutorOf(c), ["api"] = (c.ApiSurfaces ?? new string[0]).Cast<object>().ToList(),
            ["versions"] = c.MinVersion + "–" + c.MaxVersion, ["status"] = c.Status.ToString(), ["implementation"] = (c.Implementation ?? new string[0]).Cast<object>().ToList(),
            ["tests_list"] = (c.Tests ?? new string[0]).Cast<object>().ToList(), ["verifier_name"] = c.Verifier, ["idempotency"] = c.Idempotency, ["recovery"] = c.Recovery,
            ["limitations"] = c.Limitations, ["ribbon_commands"] = (c.PostableCommands ?? new string[0]).Cast<object>().ToList(), ["priority"] = c.Priority,
            ["native_verification"] = c.Status == CoverageStatus.NATIVE_VERIFIED ? "yes" : "pending",
        };

        /// <summary>Official executor of the directive (Capability → Executor) derived from the strategy.</summary>
        public static string ExecutorOf(CapabilityEntry c)
        {
            switch (StrategyKind(c.Strategy))
            {
                case "typed": return c.Strategy != null && c.Strategy.Contains("+") ? "TYPED_COMPOSITE" : "DIRECT_REVIT_API";
                case "query": case "doc": case "fam": return "DIRECT_REVIT_API";
                case "exec": return "DYNAMIC_CSHARP";
                case "engine": return c.Implementation != null && c.Implementation.Any(f => f.Contains("PowerEngine/Workflows") || f.Contains("Pdf")) ? "PYTHON_DOCUMENT" : "ENGINE_INTERNAL";   // engine guarantee: runs nothing in Revit
                case "rust": return "RUST_GEOMETRY";
                case "postcommand": return c.Status == CoverageStatus.UNSUPPORTED_BY_PUBLIC_API ? "MANUAL_HANDOFF" : "NATIVE_UI_ASSISTED";
                case "manual": return "MANUAL_HANDOFF";
                default: return "UNAVAILABLE";
            }
        }
    }
}
