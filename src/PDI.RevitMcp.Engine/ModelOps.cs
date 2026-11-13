// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// 0.9.1 (Bloques C/D/E): validación offline de las operaciones tipadas de modelado (niveles, rejillas, muros, suelos, techos,
    /// habitaciones, conductos, tuberías, circuitos y tablas). Todo lo que se puede comprobar sin Revit se rechaza aquí, antes de abrir
    /// una transacción: geometría degenerada, unidades, combinaciones de argumentos incompatibles y nombres que Revit no admite.
    /// La resolución (tipos, niveles, sistemas, cuadros) y la verificación de lo creado se hacen en el add-in.
    /// </summary>
    public static class ModelOps
    {
        /// <summary>Caracteres que Revit no admite en nombres de niveles, rejillas, vistas y tablas.</summary>
        public static readonly char[] InvalidNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
        public const int MaxWallSegments = 500;
        public const int MaxLoopVertices = 500;
        public const int MaxScheduleFields = 60;
        /// <summary>Altura/elevación razonable para un edificio (pies): fuera de esto es casi seguro un error de unidades.</summary>
        public const double MaxElevation = 10000;
        public const double DefaultWallHeight = 10.0;
        public const double DefaultCeilingHeight = 8.0;

        public static string NameError(string name, string what)
        {
            if (name == null) return null;
            if (name.Trim().Length == 0) return what + ": el nombre no puede estar vacío";
            if (name.Length > 250) return what + ": nombre demasiado largo";
            var bad = name.IndexOfAny(InvalidNameChars);
            return bad >= 0 ? what + ": Revit no admite el carácter '" + name[bad] + "' en nombres" : null;
        }

        private static void Name(IDictionary<string, object> o, string key, string kind)
        {
            var e = NameError(Arg.GetString(o, key), kind + " " + key);
            if (e != null) throw new QueryException(e);
        }

        private static double? Positive(IDictionary<string, object> o, string key, string kind, double max)
        {
            var v = OpCatalog.Length(o, key, kind);
            if (v.HasValue && (!(v.Value > Draw2D.ShortCurve) || v.Value > max))
                throw new QueryException(kind + " " + key + ": debe ser mayor que 0 y menor que " + max + " ft");
            return v;
        }

        private static double? Ranged(IDictionary<string, object> o, string key, string kind)
        {
            var v = OpCatalog.Length(o, key, kind);
            if (v.HasValue && Math.Abs(v.Value) > MaxElevation) throw new QueryException(kind + " " + key + ": fuera de rango (±" + MaxElevation + " ft)");
            return v;
        }

        private static double[] Point3(object v, string what)
        {
            var p = OpCatalog.Point(v, what, true);
            if (p.Length != 3) throw new QueryException(what + ": use [x,y,z] en pies");
            if (p.Any(c => double.IsNaN(c) || double.IsInfinity(c) || Math.Abs(c) > Draw2D.MaxCoord)) throw new QueryException(what + ": coordenada no finita o fuera de rango");
            return p;
        }

        public static void Validate(string kind, IDictionary<string, object> o, OpSpec s)
        {
            if (kind != "circuit_create" && (s.Ids.Count > 0 || s.Set != null || s.RefOps.Count > 0))
                throw new QueryException(kind + ": crea elementos nuevos; no use ids");
            switch (kind)
            {
                case "level_create":
                {
                    if (!o.TryGetValue("elev", out var ev) || ev == null) throw new QueryException("level_create: falta elev");
                    Ranged(o, "elev", kind);
                    Name(o, "nombre", kind);
                    break;
                }
                case "grid_create":
                {
                    var gp = OpCatalog.Points(o["puntos"], "grid_create puntos", 2, 2, false);
                    Draw2D.Segments(gp, false, out var ge);
                    if (ge != null) throw new QueryException("grid_create: " + ge);
                    Name(o, "nombre", kind);
                    break;
                }
                case "wall_create":
                {
                    var wp = OpCatalog.Points(o["puntos"], "wall_create puntos", 2, MaxWallSegments + 1, false);
                    Draw2D.Segments(wp, Arg.GetBool(o, "cerrado") == true, out var we);
                    if (we != null) throw new QueryException("wall_create: " + we);
                    if (o.ContainsKey("altura") && o.ContainsKey("nivel_superior")) throw new QueryException("wall_create: use altura o nivel_superior, no los dos");
                    Positive(o, "altura", kind, MaxElevation);
                    Ranged(o, "desfase", kind);
                    break;
                }
                case "floor_create":
                case "ceiling_create":
                {
                    var fp = OpCatalog.Points(o["puntos"], kind + " puntos", 3, MaxLoopVertices, false);
                    var le = Draw2D.ValidateLoop(fp);
                    if (le != null) throw new QueryException(kind + ": " + le);
                    if (kind == "floor_create") Ranged(o, "desfase", kind);
                    else Positive(o, "altura", kind, MaxElevation);
                    break;
                }
                case "room_create":
                {
                    var rp = OpCatalog.Point(o["punto"], "room_create punto", false);
                    var pe = Draw2D.ValidatePoint(rp, "room_create punto");
                    if (pe != null) throw new QueryException(pe);
                    foreach (var k in new[] { "nombre", "numero" })
                    {
                        var v = Arg.GetString(o, k);
                        if (v != null && v.Trim().Length == 0) throw new QueryException("room_create " + k + ": no puede estar vacío");
                    }
                    break;
                }
                case "duct_create":
                case "pipe_create":
                {
                    var a = Point3(o["inicio"], kind + " inicio");
                    var b = Point3(o["fin"], kind + " fin");
                    var len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
                    if (len < Draw2D.ShortCurve) throw new QueryException(kind + ": inicio y fin coinciden (longitud " + len + " ft)");
                    if (string.IsNullOrWhiteSpace(Arg.GetString(o, "sistema"))) throw new QueryException(kind + ": falta sistema (nombre del tipo de sistema)");
                    Positive(o, "diametro", kind, 20);
                    if (kind == "duct_create")
                    {
                        var hasW = o.ContainsKey("ancho"); var hasH = o.ContainsKey("alto");
                        if (hasW != hasH) throw new QueryException("duct_create: indique ancho y alto juntos");
                        if (hasW && o.ContainsKey("diametro")) throw new QueryException("duct_create: use ancho/alto (rectangular) o diametro (redondo), no los dos");
                        Positive(o, "ancho", kind, 20);
                        Positive(o, "alto", kind, 20);
                    }
                    else if (o.ContainsKey("ancho") || o.ContainsKey("alto")) throw new QueryException("pipe_create: las tuberías sólo admiten diametro");
                    break;
                }
                case "circuit_create":
                {
                    if (s.RefOps.Count > 0) throw new QueryException("circuit_create: use ids de dispositivos que ya existen o un $conjunto (no @n)");
                    if (s.Ids.Count + (s.Set != null ? 1 : 0) == 0) throw new QueryException("circuit_create: indique los ids de los dispositivos");
                    if (s.Ids.Count > PlanOpsLimits.MaxEntries) throw new QueryException("circuit_create: máximo " + PlanOpsLimits.MaxEntries + " dispositivos");
                    if (s.Ids.Distinct().Count() != s.Ids.Count) throw new QueryException("circuit_create: ids repetidos");
                    var panel = Arg.GetString(o, "panel");
                    if (panel != null && panel.Trim().Length == 0) throw new QueryException("circuit_create panel: nombre o id del cuadro");
                    break;
                }
                case "schedule_create":
                {
                    var cat = NormalizeCategory(Arg.GetString(o, "categoria"));
                    if (cat == null) throw new QueryException("schedule_create categoria: nombre de BuiltInCategory, p. ej. LightingFixtures u OST_LightingFixtures");
                    var fields = ScheduleFields(o);
                    if (fields.Count == 0) throw new QueryException("schedule_create campos: al menos un campo");
                    if (fields.Count > MaxScheduleFields) throw new QueryException("schedule_create campos: máximo " + MaxScheduleFields);
                    var dup = fields.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                    if (dup != null) throw new QueryException("schedule_create campos: '" + dup.Key + "' repetido");
                    if (fields.Any(f => f.Trim().Length == 0)) throw new QueryException("schedule_create campos: hay un campo vacío");
                    Name(o, "nombre", kind);
                    break;
                }
            }
        }

        /// <summary>"LightingFixtures" u "OST_LightingFixtures" → "OST_LightingFixtures"; null si no tiene forma de BuiltInCategory.</summary>
        public static string NormalizeCategory(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return null;
            c = c.Trim();
            if (!c.StartsWith("OST_", StringComparison.Ordinal)) c = "OST_" + c;
            for (var i = 4; i < c.Length; i++) if (!char.IsLetterOrDigit(c[i]) && c[i] != '_') return null;
            return c.Length > 4 ? c : null;
        }

        public static List<string> ScheduleFields(IDictionary<string, object> o)
        {
            if (!o.TryGetValue("campos", out var v) || v == null) return new List<string>();
            if (v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();
            if (v is string s) return s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            throw new QueryException("schedule_create campos: lista [\"Level\",\"Type\",…]");
        }

        /// <summary>
        /// AUD-06: ¿difiere lo leído de lo pedido? Un valor no finito (parámetro ausente leído como NaN) cuenta SIEMPRE como diferencia:
        /// Math.Abs(NaN − x) &gt; tol es false y daba por buena una verificación que no se pudo hacer.
        /// </summary>
        public static bool Differs(double actual, double wanted, double tol)
        {
            if (double.IsNaN(actual) || double.IsInfinity(actual) || double.IsNaN(wanted) || double.IsInfinity(wanted)) return true;
            return Math.Abs(actual - wanted) > tol;
        }

        /// <summary>Tramos de muro (pares de puntos) de la polilínea, ya validada.</summary>
        public static List<(double[] A, double[] B)> WallSegments(IList<double[]> pts, bool closed)
        {
            var r = new List<(double[], double[])>();
            var n = closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < n; i++) r.Add((pts[i], pts[(i + 1) % pts.Count]));
            return r;
        }
    }
}
