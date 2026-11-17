// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Órdenes de una línea para el perfil compacto: "q LF lvl=LEVEL05 g=lvl,type elev>9".
    /// - primera palabra = verbo; palabras sueltas = posicionales;
    /// - clave=valor = argumento (con alias cortos); comillas para valores con espacios;
    /// - campo&gt;n, campo~txt, campo!=x, campo? y !campo = condiciones where (en query).
    /// </summary>
    public sealed class Command
    {
        public string Verb;
        public List<string> Positional = new List<string>();
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        public List<object> Where = new List<object>();

        private static readonly Dictionary<string, string> Cat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LF"] = "LightingFixtures", ["LD"] = "LightingDevices", ["EF"] = "ElectricalFixtures", ["EE"] = "ElectricalEquipment",
            ["FA"] = "FireAlarmDevices", ["DD"] = "DataDevices", ["CD"] = "CommunicationDevices", ["SD"] = "SecurityDevices",
            ["NC"] = "NurseCallDevices", ["TD"] = "TelephoneDevices", ["CO"] = "Conduit", ["CT"] = "CableTray",
            ["LT"] = "LightingFixtureTags", ["GM"] = "GenericModel",
        };

        private static readonly Dictionary<string, string> Alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g"] = "group_by", ["n"] = "count", ["p"] = "page", ["ps"] = "page_size", ["s"] = "save_as", ["as"] = "save_as",
            ["t"] = "type", ["v"] = "view", ["u"] = "units", ["c"] = "cat",
        };

        public static string CatAlias(string s) => s != null && Cat.TryGetValue(s, out var full) ? full : s;

        public static Command Parse(string line)
        {
            var c = new Command();
            var tokens = Tokenize(line ?? "");
            if (tokens.Count == 0) return null;
            c.Verb = tokens[0].ToLowerInvariant();
            foreach (var tok in tokens.Skip(1))
            {
                var eq = tok.IndexOf('=');
                if (IsCondition(tok, eq)) { c.Where.Add(tok); continue; }
                if (eq > 0)
                {
                    var k = tok.Substring(0, eq).Trim();
                    var v = tok.Substring(eq + 1);
                    if (Alias.TryGetValue(k, out var full)) k = full;
                    c.Args[k] = Value(k, v);
                    continue;
                }
                c.Positional.Add(tok);
            }
            if (c.Args.TryGetValue("cat", out var cat) && cat is string cs)
                c.Args["cat"] = string.Join(",", cs.Split(',').Select(x => CatAlias(x.Trim())));
            return c;
        }

        /// <summary>Condición tipo where si el token tiene &gt;, &lt;, ~, != o termina en ? o empieza con !.</summary>
        private static bool IsCondition(string tok, int equals)
        {
            if (tok.Length < 2) return false;
            if (equals < 0 && (tok.EndsWith("?", StringComparison.Ordinal) || tok[0] == '!')) return true;
            foreach (var op in new[] { ">=", "<=", "!=", ">", "<", "~" })
            {
                var index = tok.IndexOf(op, StringComparison.Ordinal);
                if (index > 0 && (equals < 0 || index < equals)) return true;
            }
            return false;
        }

        private static object Value(string key, string v)
        {
            if (v.Length > 0 && (v[0] == '[' || v[0] == '{'))
            {
                try { return PDI.RevitMcp.Protocol.Json.Parse(v); } catch (Exception) { /* texto tal cual */ }
            }
            if (v == "true" || v == "sí" || v == "si") return true;
            if (v == "false" || v == "no") return false;
            if (key != "ids" && key != "lvl" && key != "type" && key != "name" && key != "value"
                && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (key != "ids" && key != "name" && key != "value" && v.IndexOf('.') > 0
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return v;
        }

        public static List<string> Tokenize(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            var context = new TokenContext();
            var any = false;
            foreach (var ch in line)
            {
                if (context.Advance(ch))
                {
                    any = true;
                    continue;
                }
                if (context.AtTopLevel && char.IsWhiteSpace(ch))
                {
                    if (sb.Length > 0 || any) { list.Add(sb.ToString()); sb.Clear(); any = false; }
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0 || any) list.Add(sb.ToString());
            return list;
        }

        /// <summary>Varias órdenes: una por línea o separadas por ";;".</summary>
        public static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            var start = 0;
            var comment = false;
            var hasContent = false;
            var context = new TokenContext();
            void Add(int end)
            {
                var line = text.Substring(start, end - start).Trim();
                if (line.Length > 0 && !comment) lines.Add(line);
                comment = hasContent = false;
            }
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                var separator = ch == ';' && i + 1 < text.Length && text[i + 1] == ';';
                if (context.AtTopLevel && (ch == '\n' || separator))
                {
                    Add(i);
                    if (separator) i++;
                    start = i + 1;
                    continue;
                }
                if (!hasContent && !char.IsWhiteSpace(ch)) { hasContent = true; comment = ch == '#'; }
                if (!comment) context.Advance(ch);
            }
            Add(text.Length);
            return lines;
        }

        private struct TokenContext
        {
            private int _depth;
            private bool _quoted, _jsonString, _escaped;
            public bool AtTopLevel => !_quoted && _depth == 0;

            // Returns true for the outer quotes that are removed from a token.
            public bool Advance(char ch)
            {
                if (_jsonString)
                {
                    if (_escaped) _escaped = false;
                    else if (ch == '\\') _escaped = true;
                    else if (ch == '"') _jsonString = false;
                    return false;
                }
                if (ch == '"')
                {
                    if (_depth > 0) { _jsonString = true; return false; }
                    _quoted = !_quoted;
                    return true;
                }
                if (_quoted) return false;
                if (ch == '[' || ch == '{') _depth++;
                if (ch == ']' || ch == '}') _depth = Math.Max(0, _depth - 1);
                return false;
            }
        }

        /// <summary>Plan en líneas: "op ids clave=valor" → [{op, ids, …}]. Si el texto es JSON, se usa tal cual.</summary>
        public static List<object> ParseOps(string text)
        {
            var t = (text ?? "").Trim();
            if (t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("{", StringComparison.Ordinal))
            {
                var parsed = PDI.RevitMcp.Protocol.Json.Parse(t);
                return parsed is List<object> l ? l : new List<object> { parsed };
            }
            var ops = new List<object>();
            foreach (var line in SplitLines(t))
            {
                var tokens = Tokenize(line);
                var d = new Dictionary<string, object>(StringComparer.Ordinal) { ["op"] = OpAlias(tokens[0]) };
                foreach (var tok in tokens.Skip(1))
                {
                    var eq = tok.IndexOf('=');
                    if (eq > 0) { var k = tok.Substring(0, eq); d[k] = Value(k, tok.Substring(eq + 1)); }
                    else if (!d.ContainsKey("ids")) d["ids"] = tok;
                }
                ops.Add(d);
            }
            return ops;
        }

        private static string OpAlias(string op)
        {
            switch (op.ToLowerInvariant())
            {
                case "set": return "set_param";
                case "snap": return "snap_to_surface";
                case "type": return "change_type";
                case "elev": return "set_elevation";
                case "ws": return "set_workset";
                default: return op;
            }
        }
    }
}
