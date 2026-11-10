// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.Engine.Geo
{
    /// <summary>
    /// Convierte las filas que junta el add-in (valores simples) en la respuesta compacta de "geo":
    /// conteos por clase y por nivel, una tabla paginada con los problemas primero y conjuntos guardados por clase.
    /// Corre fuera del hilo de Revit.
    /// </summary>
    public static class GeoSummary
    {
        /// <summary>Columnas de longitud (pies) que se convierten con units=mm.</summary>
        public static readonly HashSet<string> LengthCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "z", "ztop", "zbot", "x", "y", "dist", "dist_xy", "dz", "offset", "bajo", "alto", "elev" };

        /// <summary>Orden de gravedad: lo que hay que corregir primero.</summary>
        private static readonly string[] Severity =
        {
            MountClass.Embedded, "penetra", "choque", MountClass.Floating, "separada", "distinto", "sobrante", "fuera", "ajena", "sin_cuarto",
            ViewBand.Above, ViewBand.Below, "cerca", "en_region", MountClass.NoSurface, MountClass.NoBody, "sin_muro", "no_evaluable",
            MountClass.Ok, "toca", "igual", "dentro", "unico", "con_cuarto", "libre",
        };

        public const double CubicCmPerCubicFoot = 28316.846592;

        public static readonly HashSet<string> OkClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { MountClass.Ok, "toca", "igual", "dentro", "unico", "con_cuarto", "libre" };

        public static Dictionary<string, object> Build(GeoSpec spec, IList<Dictionary<string, object>> rows, IList<string> cols,
            string classField, IDictionary<string, string> setNames, out Dictionary<string, long[]> sets)
        {
            sets = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            var d = new Dictionary<string, object> { ["receta"] = spec.Recipe, ["total"] = (long)rows.Count };

            if (classField != null)
            {
                var byClass = rows.GroupBy(r => Str(r, classField) ?? "?")
                    .OrderBy(g => Rank(g.Key)).ToDictionary(g => g.Key, g => (object)(long)g.Count());
                d["por_clase"] = byClass;
                if (rows.Any(r => r.ContainsKey("lvl")))
                {
                    var byLevel = new Dictionary<string, object>();
                    foreach (var g in rows.GroupBy(r => Str(r, "lvl") ?? "(sin nivel)").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var counts = g.GroupBy(r => Str(r, classField) ?? "?").OrderBy(x => Rank(x.Key))
                            .ToDictionary(x => x.Key, x => (object)(long)x.Count());
                        if (counts.Count > 1 || !OkClasses.Contains(counts.Keys.First())) byLevel[g.Key] = counts;
                    }
                    if (byLevel.Count > 0 && byLevel.Count <= 40) d["por_nivel"] = byLevel;
                }
                if (setNames != null)
                {
                    foreach (var kv in setNames)
                    {
                        var ids = rows.Where(r => string.Equals(Str(r, classField), kv.Key, StringComparison.OrdinalIgnoreCase))
                                      .Select(r => r.TryGetValue("id", out var v) ? v : null).OfType<long>().Distinct().ToArray();
                        if (ids.Length > 0) sets[kv.Value] = ids;
                    }
                }
            }

            // Tabla: por defecto sólo lo que no está bien; solo=todos para verlo todo, solo=<clase> para una clase.
            IEnumerable<Dictionary<string, object>> shown = rows;
            var only = (spec.Only ?? "").Trim();
            if (classField != null)
            {
                if (only.Length == 0)
                {
                    var problems = rows.Where(r => !OkClasses.Contains(Str(r, classField) ?? "")).ToList();
                    if (problems.Count > 0) { shown = problems; d["tabla"] = "sólo problemas (" + problems.Count + "); use solo=todos para ver todo"; }
                }
                else if (!string.Equals(only, "todos", StringComparison.OrdinalIgnoreCase))
                {
                    shown = rows.Where(r => string.Equals(Str(r, classField), only, StringComparison.OrdinalIgnoreCase));
                }
                shown = shown.OrderBy(r => Rank(Str(r, classField))).ThenBy(r => Math.Abs(Num(r, "gap") ?? Num(r, "dist") ?? 0));
            }
            var list = shown.ToList();
            var pages = Math.Max(1, (list.Count + spec.PageSize - 1) / spec.PageSize);
            var outRows = new List<object>();
            foreach (var r in list.Skip((spec.Page - 1) * spec.PageSize).Take(spec.PageSize))
            {
                var arr = new object[cols.Count];
                for (var i = 0; i < arr.Length; i++) arr[i] = Convert(r, cols[i], spec.Millimeters);
                outRows.Add(arr);
            }
            d["cols"] = cols.Select(c => (object)Header(c, spec.Millimeters)).ToList();
            d["rows"] = outRows;
            if (pages > 1) { d["page"] = (long)spec.Page; d["pages"] = (long)pages; }
            d["unidades"] = spec.Millimeters ? "mm" : "pies (holguras en pulgadas)";
            if (sets.Count > 0) d["conjuntos_guardados"] = sets.Keys.Cast<object>().ToList();
            return d;
        }

        private static string Header(string c, bool mm)
        {
            if (c == "gap") return mm ? "holgura_mm" : "holgura_in";
            if (c == "gap_max") return mm ? "holgura_max_mm" : "holgura_max_in";
            if (c == "prof") return mm ? "prof_mm" : "prof_in";
            if (c == "vol") return mm ? "vol_cm3" : "vol_ft3";
            return c;
        }

        private static object Convert(Dictionary<string, object> r, string col, bool mm)
        {
            if (!r.TryGetValue(col, out var v) || v == null) return null;
            if (col == "vol")
            {
                if (!(v is double vol) || double.IsNaN(vol)) return null;
                return mm ? Math.Round(vol * CubicCmPerCubicFoot, 1) : Math.Round(vol, 5);
            }
            if (col == "gap" || col == "gap_max" || col == "prof")
            {
                if (!(v is double g) || double.IsNaN(g)) return null;
                return mm ? Math.Round(g * Lengths.MmPerFoot, 1) : Math.Round(g * 12.0, 3);
            }
            if (v is double ft && LengthCols.Contains(col))
            {
                if (double.IsNaN(ft)) return null;
                return mm ? Math.Round(ft * Lengths.MmPerFoot, 1) : Math.Round(ft, 4);
            }
            if (v is double other) return double.IsNaN(other) ? null : (object)Math.Round(other, 6);
            if (v is string s && s.Length > QuerySpec.MaxTextLength) return ResultTable.Trim(s);
            return v;
        }

        private static int Rank(string cls)
        {
            var i = Array.IndexOf(Severity, cls ?? "");
            return i < 0 ? Severity.Length : i;
        }

        public static string Str(Dictionary<string, object> r, string k) =>
            r.TryGetValue(k, out var v) && v != null ? System.Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        public static double? Num(Dictionary<string, object> r, string k)
        {
            if (!r.TryGetValue(k, out var v) || v == null) return null;
            if (v is double d) return double.IsNaN(d) ? (double?)null : d;
            if (v is long l) return l;
            return null;
        }
    }
}
