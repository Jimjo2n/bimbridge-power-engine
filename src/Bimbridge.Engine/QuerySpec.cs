// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Declarative query (section 7, "query" tool). The AI sends a few short keys
    /// instead of a script; they are validated and normalized here before touching Revit.
    /// </summary>
    public sealed class QuerySpec
    {
        public List<string> Categories = new List<string>();
        public List<string> Levels = new List<string>();
        public List<string> Types = new List<string>();      // "Family: Type", "Type" or fragment
        public List<string> Worksets = new List<string>();
        public List<long> Ids = new List<long>();
        public string FromSet;                                // "$name"
        public long? ViewId;
        public List<Condition> Where = new List<Condition>();
        public List<string> Fields = new List<string>();
        public List<string> GroupBy = new List<string>();
        public bool CountOnly;
        public bool Digest;
        public int Page = 1;
        public int PageSize = DefaultPageSize;
        public string SaveAs;
        public long? Since;
        /// <summary>true = lengths (elev, x, y, z) in mm instead of feet; also in where.</summary>
        public bool Millimeters;
        /// <summary>true = do not truncate long texts.</summary>
        public bool FullText;

        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 200;
        /// <summary>Longer texts are truncated with "…" unless full=true (a 600-character Comments over 290 rows was ~45 000 characters).</summary>
        public const int MaxTextLength = 120;

        /// <summary>Length fields converted with units=mm.</summary>
        public static readonly HashSet<string> LengthFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "elev", "x", "y", "z" };

        public static readonly string[] DefaultFields = { "id", "fam", "type", "lvl" };

        /// <summary>Fields the Revit adapter knows without looking up a parameter by name.</summary>
        public static readonly HashSet<string> BuiltInFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "cat", "fam", "type", "lvl", "ws", "elev", "mark", "comments", "host", "x", "y", "z", "view", "name", "uid",
        };

        public static QuerySpec Parse(IDictionary<string, object> a)
        {
            var q = new QuerySpec
            {
                Categories = Arg.GetStringList(a, "cat"),
                Levels = Arg.GetStringList(a, "lvl"),
                Types = Arg.GetStringList(a, "type"),
                Worksets = Arg.GetStringList(a, "ws"),
                Fields = Arg.GetStringList(a, "f"),
                GroupBy = Arg.GetStringList(a, "group_by"),
                CountOnly = Arg.GetBool(a, "count") ?? false,
                Digest = Arg.GetBool(a, "digest") ?? false,
                Page = (int)(Arg.GetLong(a, "page") ?? 1),
                PageSize = (int)(Arg.GetLong(a, "page_size") ?? DefaultPageSize),
                SaveAs = Arg.GetString(a, "save_as"),
                Since = Arg.GetLong(a, "since"),
                ViewId = Arg.GetLong(a, "view"),
                FullText = Arg.GetBool(a, "full") ?? false,
            };
            var units = (Arg.GetString(a, "units") ?? "ft").Trim().ToLowerInvariant();
            if (units == "mm") q.Millimeters = true;
            else if (units != "ft" && units != "") throw new QueryException("units: use ft (default) or mm");

            foreach (var s in Arg.GetStringList(a, "ids"))
            {
                if (s.StartsWith("$")) { q.FromSet = NamedSets.Normalize(s); continue; }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + s + "' is not an id or a set ($name)");
                q.Ids.Add(id);
            }
            var set = Arg.GetString(a, "set");
            if (!string.IsNullOrEmpty(set)) q.FromSet = NamedSets.Normalize(set);

            foreach (var w in Arg.GetStringList(a, "where")) q.Where.Add(Condition.Parse(w));

            if (q.Categories.Count == 0 && q.Ids.Count == 0 && q.FromSet == null && q.ViewId == null)
                throw new QueryException("specify at least cat, ids, set or view");
            if (q.Page < 1) throw new QueryException("page starts at 1");
            if (q.PageSize < 1 || q.PageSize > MaxPageSize) throw new QueryException("page_size between 1 and " + MaxPageSize + "; for more rows use page, group_by or count");
            if (q.SaveAs != null) q.SaveAs = NamedSets.Normalize(q.SaveAs);
            if (q.Fields.Count == 0) q.Fields.AddRange(DefaultFields);
            if (q.GroupBy.Count > 3) throw new QueryException("group_by accepts up to 3 fields");
            if (q.Digest && (q.CountOnly || q.GroupBy.Count > 0))
                throw new QueryException("digest cannot be combined with count or group_by; use f= to select fields");
            return q;
        }

        /// <summary>
        /// Count that Revit resolves with its index (FilteredElementCollector.GetElementCount) without opening any element:
        /// only quick filters (category, view) and nothing that must be read per element.
        /// </summary>
        public bool CanFastCount => CountOnly && GroupBy.Count == 0 && Levels.Count == 0 && Types.Count == 0 && Worksets.Count == 0 &&
                                    Where.Count == 0 && Ids.Count == 0 && FromSet == null && Since == null && SaveAs == null && !Digest;
        public bool CanAggregateDuringRead => GroupBy.Count > 0 && SaveAs == null && !Digest;

        /// <summary>All the fields to read from each element (output + filters + grouping).</summary>
        public List<string> FieldsToRead()
        {
            var set = new List<string>();
            void Add(string f) { if (!set.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) set.Add(f); }
            Add("id");
            if (!CountOnly && GroupBy.Count == 0)
                foreach (var f in Fields) Add(f);
            foreach (var g in GroupBy) Add(g);
            foreach (var c in Where) Add(c.Field);
            return set;
        }
    }

    public sealed class QueryException : Exception
    {
        public QueryException(string message) : base(message) { }
    }

    /// <summary>Compact condition: "Mark=LT-01", "elev>10.5", "type~STRIP", "comments?" (not empty), "!comments" (empty).</summary>
    public sealed class Condition
    {
        public string Field;
        public string Op;
        public string Value;

        private static readonly string[] Ops = { ">=", "<=", "!=", "=", ">", "<", "~" };
        private static readonly char[] OpChars = { '=', '<', '>', '~', '!' };

        public static Condition Parse(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) throw new QueryException("empty where");
            if (t.EndsWith("?")) return new Condition { Field = t.Substring(0, t.Length - 1).Trim(), Op = "notempty" };
            if (t.StartsWith("!") && t.IndexOfAny(new[] { '=', '<', '>', '~' }) < 0)
                return new Condition { Field = t.Substring(1).Trim(), Op = "empty" };
            foreach (var op in Ops)
            {
                var i = t.IndexOf(op, StringComparison.Ordinal);
                if (i <= 0) continue;
                var c = new Condition { Field = t.Substring(0, i).Trim(), Op = op, Value = t.Substring(i + op.Length).Trim() };
                if (c.Field.Length == 0) break;
                // No silent empty results from a mistyped where ("elev>>3", "mark==A"): the AI would draw false conclusions.
                if (c.Field.IndexOfAny(OpChars) >= 0 || (c.Value.Length > 0 && c.Value.IndexOfAny(OpChars) == 0))
                    throw new QueryException("where '" + text + "' is malformed: use a single operator (field=value, !=, >, <, >=, <=, ~)");
                if ((op == ">" || op == "<" || op == ">=" || op == "<=") &&
                    !double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new QueryException("where '" + text + "': '" + c.Value + "' is not a number (use a decimal point; lengths in feet or with units=mm)");
                return c;
            }
            throw new QueryException("where '" + text + "': use field=value, !=, >, <, >=, <=, ~ (contains), field? or !field");
        }

        /// <summary>Evaluates against an already-read value (string, double, long, bool or null).</summary>
        public bool Matches(object value)
        {
            var s = value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
            switch (Op)
            {
                case "empty": return s.Length == 0;
                case "notempty": return s.Length > 0;
                case "~": return s.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "=": return Equal(value, s);
                case "!=": return !Equal(value, s);
            }
            if (!TryNumber(value, out var a) || !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                return false;
            switch (Op)
            {
                case ">": return a > b;
                case "<": return a < b;
                case ">=": return a >= b - 1e-9;
                case "<=": return a <= b + 1e-9;
            }
            return false;
        }

        private bool Equal(object value, string s)
        {
            if (TryNumber(value, out var a) && double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                return Math.Abs(a - b) < 1e-6;
            return string.Equals(s, Value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNumber(object v, out double d)
        {
            switch (v)
            {
                case double x: d = x; return true;
                case long l: d = l; return true;
                case int i: d = i; return true;
                case bool _: d = 0; return false;
                case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                default: d = 0; return false;
            }
        }
    }
}
