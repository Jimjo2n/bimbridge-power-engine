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
                var cond = CondOp(tok);
                if (cond != null) { c.Where.Add(tok); continue; }
                var eq = tok.IndexOf('=');
                if (eq > 0 && tok.IndexOf('!') != eq - 1)
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
        private static string CondOp(string tok)
        {
            if (tok.Length < 2) return null;
            if (tok.EndsWith("?", StringComparison.Ordinal) || (tok[0] == '!' && tok.IndexOf('=') < 0)) return "u";
            foreach (var op in new[] { ">=", "<=", "!=", ">", "<", "~" })
                if (tok.IndexOf(op, StringComparison.Ordinal) > 0) return op;
            return null;
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
            var quote = false; var depth = 0; var any = false;
            foreach (var ch in line)
            {
                if (ch == '"' && depth == 0) { quote = !quote; any = true; continue; }
                if (!quote && (ch == '[' || ch == '{')) depth++;
                if (!quote && (ch == ']' || ch == '}')) depth = Math.Max(0, depth - 1);
                if (!quote && depth == 0 && char.IsWhiteSpace(ch))
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
        public static List<string> SplitLines(string text) =>
            (text ?? "").Replace(";;", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToList();

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
