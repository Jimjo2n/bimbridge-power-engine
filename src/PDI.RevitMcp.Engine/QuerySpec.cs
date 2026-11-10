// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Consulta declarativa (sección 7, herramienta "query"). La IA manda unas pocas claves
    /// cortas en lugar de un script; aquí se validan y se normalizan antes de tocar Revit.
    /// </summary>
    public sealed class QuerySpec
    {
        public List<string> Categories = new List<string>();
        public List<string> Levels = new List<string>();
        public List<string> Types = new List<string>();      // "Familia: Tipo", "Tipo" o fragmento
        public List<string> Worksets = new List<string>();
        public List<long> Ids = new List<long>();
        public string FromSet;                                // "$nombre"
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
        /// <summary>true = longitudes (elev, x, y, z) en mm en vez de pies; también en where.</summary>
        public bool Millimeters;
        /// <summary>true = no recortar textos largos.</summary>
        public bool FullText;

        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 200;
        /// <summary>Textos más largos se recortan con "…" salvo full=true (un Comments de 600 caracteres en 290 filas eran ~45 000 caracteres).</summary>
        public const int MaxTextLength = 120;

        /// <summary>Campos de longitud que se convierten con units=mm.</summary>
        public static readonly HashSet<string> LengthFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "elev", "x", "y", "z" };

        public static readonly string[] DefaultFields = { "id", "fam", "type", "lvl" };

        /// <summary>Campos que el adaptador de Revit conoce sin buscar un parámetro por nombre.</summary>
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
            else if (units != "ft" && units != "") throw new QueryException("units: use ft (por defecto) o mm");

            foreach (var s in Arg.GetStringList(a, "ids"))
            {
                if (s.StartsWith("$")) { q.FromSet = NamedSets.Normalize(s); continue; }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    throw new QueryException("ids: '" + s + "' no es un id ni un conjunto ($nombre)");
                q.Ids.Add(id);
            }
            var set = Arg.GetString(a, "set");
            if (!string.IsNullOrEmpty(set)) q.FromSet = NamedSets.Normalize(set);

            foreach (var w in Arg.GetStringList(a, "where")) q.Where.Add(Condition.Parse(w));

            if (q.Categories.Count == 0 && q.Ids.Count == 0 && q.FromSet == null && q.ViewId == null)
                throw new QueryException("indique al menos cat, ids, set o view");
            if (q.Page < 1) throw new QueryException("page empieza en 1");
            if (q.PageSize < 1 || q.PageSize > MaxPageSize) throw new QueryException("page_size entre 1 y " + MaxPageSize + "; para más filas use page, group_by o count");
            if (q.SaveAs != null) q.SaveAs = NamedSets.Normalize(q.SaveAs);
            if (q.Fields.Count == 0) q.Fields.AddRange(DefaultFields);
            if (q.GroupBy.Count > 3) throw new QueryException("group_by admite hasta 3 campos");
            if (q.Digest && (q.CountOnly || q.GroupBy.Count > 0))
                throw new QueryException("digest no se combina con count ni group_by; indique f= para seleccionar campos");
            return q;
        }

        /// <summary>
        /// Conteo que Revit resuelve con su índice (FilteredElementCollector.GetElementCount) sin abrir ningún elemento:
        /// sólo filtros rápidos (categoría, vista) y nada que haya que leer por elemento.
        /// </summary>
        public bool CanFastCount => CountOnly && GroupBy.Count == 0 && Levels.Count == 0 && Types.Count == 0 && Worksets.Count == 0 &&
                                    Where.Count == 0 && Ids.Count == 0 && FromSet == null && Since == null && SaveAs == null && !Digest;

        /// <summary>Todos los campos que hay que leer de cada elemento (salida + filtros + agrupación).</summary>
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

    /// <summary>Condición compacta: "Mark=LT-01", "elev>10.5", "type~STRIP", "comments?" (no vacío), "!comments" (vacío).</summary>
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
            if (t.Length == 0) throw new QueryException("where vacío");
            if (t.EndsWith("?")) return new Condition { Field = t.Substring(0, t.Length - 1).Trim(), Op = "notempty" };
            if (t.StartsWith("!") && t.IndexOfAny(new[] { '=', '<', '>', '~' }) < 0)
                return new Condition { Field = t.Substring(1).Trim(), Op = "empty" };
            foreach (var op in Ops)
            {
                var i = t.IndexOf(op, StringComparison.Ordinal);
                if (i <= 0) continue;
                var c = new Condition { Field = t.Substring(0, i).Trim(), Op = op, Value = t.Substring(i + op.Length).Trim() };
                if (c.Field.Length == 0) break;
                // Nada de resultados vacíos en silencio por un where mal escrito ("elev>>3", "mark==A"): la IA sacaría conclusiones falsas.
                if (c.Field.IndexOfAny(OpChars) >= 0 || (c.Value.Length > 0 && c.Value.IndexOfAny(OpChars) == 0))
                    throw new QueryException("where '" + text + "' está mal formado: use un solo operador (campo=valor, !=, >, <, >=, <=, ~)");
                if ((op == ">" || op == "<" || op == ">=" || op == "<=") &&
                    !double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new QueryException("where '" + text + "': '" + c.Value + "' no es un número (use punto decimal; longitudes en pies o con units=mm)");
                return c;
            }
            throw new QueryException("where '" + text + "': use campo=valor, !=, >, <, >=, <=, ~ (contiene), campo? o !campo");
        }

        /// <summary>Evalúa contra un valor ya leído (string, double, long, bool o null).</summary>
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
