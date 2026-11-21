// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// 0.9.1 (Blocks C/D/E): offline validation of the typed modeling operations (levels, grids, walls, floors, ceilings,
    /// rooms, ducts, pipes, circuits and schedules). Everything that can be checked without Revit is rejected here, before opening
    /// a transaction: degenerate geometry, units, incompatible argument combinations and names Revit does not accept.
    /// Resolution (types, levels, systems, panels) and verification of what was created happen in the add-in.
    /// </summary>
    public static class ModelOps
    {
        /// <summary>Characters Revit does not accept in level, grid, view and schedule names.</summary>
        public static readonly char[] InvalidNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
        public const int MaxWallSegments = 500;
        public const int MaxLoopVertices = 500;
        public const int MaxScheduleFields = 60;
        /// <summary>Reasonable height/elevation for a building (feet): beyond this it is almost certainly a units error.</summary>
        public const double MaxElevation = 10000;
        public const double DefaultWallHeight = 10.0;
        public const double DefaultCeilingHeight = 8.0;

        public static string NameError(string name, string what)
        {
            if (name == null) return null;
            if (name.Trim().Length == 0) return what + ": the name cannot be empty";
            if (name.Length > 250) return what + ": name too long";
            var bad = name.IndexOfAny(InvalidNameChars);
            return bad >= 0 ? what + ": Revit does not accept the character '" + name[bad] + "' in names" : null;
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
                throw new QueryException(kind + " " + key + ": must be greater than 0 and less than " + max + " ft");
            return v;
        }

        private static double? Ranged(IDictionary<string, object> o, string key, string kind)
        {
            var v = OpCatalog.Length(o, key, kind);
            if (v.HasValue && Math.Abs(v.Value) > MaxElevation) throw new QueryException(kind + " " + key + ": out of range (±" + MaxElevation + " ft)");
            return v;
        }

        private static double[] Point3(object v, string what)
        {
            var p = OpCatalog.Point(v, what, true);
            if (p.Length != 3) throw new QueryException(what + ": use [x,y,z] in feet");
            if (p.Any(c => double.IsNaN(c) || double.IsInfinity(c) || Math.Abs(c) > Draw2D.MaxCoord)) throw new QueryException(what + ": non-finite or out-of-range coordinate");
            return p;
        }

        public static void Validate(string kind, IDictionary<string, object> o, OpSpec s)
        {
            if (kind != "circuit_create" && (s.Ids.Count > 0 || s.Set != null || s.RefOps.Count > 0))
                throw new QueryException(kind + ": creates new elements; do not use ids");
            switch (kind)
            {
                case "level_create":
                {
                    if (!o.TryGetValue("elev", out var ev) || ev == null) throw new QueryException("level_create: elev is missing");
                    Ranged(o, "elev", kind);
                    Name(o, "name", kind);
                    break;
                }
                case "grid_create":
                {
                    var gp = OpCatalog.Points(o["points"], "grid_create points", 2, 2, false);
                    Draw2D.Segments(gp, false, out var ge);
                    if (ge != null) throw new QueryException("grid_create: " + ge);
                    Name(o, "name", kind);
                    break;
                }
                case "wall_create":
                {
                    var wp = OpCatalog.Points(o["points"], "wall_create points", 2, MaxWallSegments + 1, false);
                    Draw2D.Segments(wp, Arg.GetBool(o, "closed") == true, out var we);
                    if (we != null) throw new QueryException("wall_create: " + we);
                    if (o.ContainsKey("height") && o.ContainsKey("top_level")) throw new QueryException("wall_create: use height or top_level, not both");
                    Positive(o, "height", kind, MaxElevation);
                    Ranged(o, "offset", kind);
                    break;
                }
                case "floor_create":
                case "ceiling_create":
                {
                    var fp = OpCatalog.Points(o["points"], kind + " points", 3, MaxLoopVertices, false);
                    var le = Draw2D.ValidateLoop(fp);
                    if (le != null) throw new QueryException(kind + ": " + le);
                    if (kind == "floor_create") Ranged(o, "offset", kind);
                    else Positive(o, "height", kind, MaxElevation);
                    break;
                }
                case "room_create":
                {
                    var rp = OpCatalog.Point(o["point"], "room_create point", false);
                    var pe = Draw2D.ValidatePoint(rp, "room_create point");
                    if (pe != null) throw new QueryException(pe);
                    foreach (var k in new[] { "name", "number" })
                    {
                        var v = Arg.GetString(o, k);
                        if (v != null && v.Trim().Length == 0) throw new QueryException("room_create " + k + ": cannot be empty");
                    }
                    break;
                }
                case "duct_create":
                case "pipe_create":
                case "conduit_create":
                {
                    var a = Point3(o["start"], kind + " start");
                    var b = Point3(o["end"], kind + " end");
                    var len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
                    if (len < Draw2D.ShortCurve) throw new QueryException(kind + ": start and end coincide (length " + len + " ft)");
                    if (kind != "conduit_create" && string.IsNullOrWhiteSpace(Arg.GetString(o, "system")))
                        throw new QueryException(kind + ": system is missing (system type name)");
                    if (!Positive(o, "diameter", kind, 20).HasValue && kind == "conduit_create")
                        throw new QueryException("conduit_create: specify diameter");
                    if (kind == "duct_create")
                    {
                        var hasW = o.ContainsKey("width"); var hasH = o.ContainsKey("height");
                        if (hasW != hasH) throw new QueryException("duct_create: specify width and height together");
                        if (hasW && o.ContainsKey("diameter")) throw new QueryException("duct_create: use width/height (rectangular) or diameter (round), not both");
                        Positive(o, "width", kind, 20);
                        Positive(o, "height", kind, 20);
                    }
                    else if (o.ContainsKey("width") || o.ContainsKey("height")) throw new QueryException("pipe_create: pipes only accept diameter");
                    break;
                }
                case "conduit_route":
                {
                    var points = OpCatalog.Points(o["points"], "conduit_route points", 3, 100);
                    foreach (var p in points) Point3(p.Cast<object>().ToList(), "conduit_route vertex");
                    for (var i = 1; i < points.Count; i++)
                        if (points[i].Zip(points[i - 1], (a, b) => (a - b) * (a - b)).Sum() < Draw2D.ShortCurve * Draw2D.ShortCurve)
                            throw new QueryException("conduit_route: consecutive points coincide or form a segment that is too short");
                    if (!Positive(o, "diameter", kind, 20).HasValue) throw new QueryException("conduit_route: specify diameter");
                    if (o.TryGetValue("replace_ids", out var replace))
                    {
                        if (!(replace is List<object> ids) || ids.Count < 1 || ids.Count > 200 ||
                            ids.Any(id => !(id is long n) || n <= 0) || ids.Distinct().Count() != ids.Count)
                            throw new QueryException("conduit_route: replace_ids requires 1 to 200 positive, unique IDs of segments and elbows");
                        if (o.ContainsKey("start_terminal") || o.ContainsKey("end_terminal"))
                            throw new QueryException("conduit_route: when replacing, the terminals are kept from the existing connections");
                    }
                    foreach (var key in new[] { "start_terminal", "end_terminal" })
                    {
                        if (!o.TryGetValue(key, out var value)) continue;
                        if (!(value is Dictionary<string, object> terminal) || terminal.Count != 2 ||
                            !(Arg.GetLong(terminal, "id") is long id) || id <= 0 ||
                            !(Arg.GetLong(terminal, "connector") is long port) || port < 0 || port > int.MaxValue)
                            throw new QueryException("conduit_route: " + key + " requires {id,connector}");
                    }
                    break;
                }
                case "circuit_create":
                {
                    if (s.RefOps.Count > 0) throw new QueryException("circuit_create: use ids of devices that already exist or a $set (not @n)");
                    if (s.Ids.Count + (s.Set != null ? 1 : 0) == 0) throw new QueryException("circuit_create: specify the device ids");
                    if (s.Ids.Count > PlanOpsLimits.MaxEntries) throw new QueryException("circuit_create: maximum " + PlanOpsLimits.MaxEntries + " devices");
                    if (s.Ids.Distinct().Count() != s.Ids.Count) throw new QueryException("circuit_create: repeated ids");
                    var panel = Arg.GetString(o, "panel");
                    if (panel != null && panel.Trim().Length == 0) throw new QueryException("circuit_create panel: panel name or id");
                    break;
                }
                case "schedule_create":
                {
                    var cat = NormalizeCategory(Arg.GetString(o, "category"));
                    if (cat == null) throw new QueryException("schedule_create category: BuiltInCategory name, e.g. LightingFixtures or OST_LightingFixtures");
                    var fields = ScheduleFields(o);
                    if (fields.Count == 0) throw new QueryException("schedule_create fields: at least one field");
                    if (fields.Count > MaxScheduleFields) throw new QueryException("schedule_create fields: maximum " + MaxScheduleFields);
                    var dup = fields.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                    if (dup != null) throw new QueryException("schedule_create fields: '" + dup.Key + "' repeated");
                    if (fields.Any(f => f.Trim().Length == 0)) throw new QueryException("schedule_create fields: there is an empty field");
                    Name(o, "name", kind);
                    break;
                }
            }
        }

        /// <summary>"LightingFixtures" or "OST_LightingFixtures" → "OST_LightingFixtures"; null if it does not have the shape of a BuiltInCategory.</summary>
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
            if (!o.TryGetValue("fields", out var v) || v == null) return new List<string>();
            if (v is List<object> l) return l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();
            if (v is string s) return s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            throw new QueryException("schedule_create fields: list [\"Level\",\"Type\",…]");
        }

        /// <summary>
        /// AUD-06: does what was read differ from what was requested? A non-finite value (missing parameter read as NaN) ALWAYS counts as a difference:
        /// Math.Abs(NaN − x) &gt; tol is false and used to pass a verification that could not be done.
        /// </summary>
        public static bool Differs(double actual, double wanted, double tol)
        {
            if (double.IsNaN(actual) || double.IsInfinity(actual) || double.IsNaN(wanted) || double.IsInfinity(wanted)) return true;
            return Math.Abs(actual - wanted) > tol;
        }

        /// <summary>Wall segments (point pairs) of the already validated polyline.</summary>
        public static List<(double[] A, double[] B)> WallSegments(IList<double[]> pts, bool closed)
        {
            var r = new List<(double[], double[])>();
            var n = closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < n; i++) r.Add((pts[i], pts[(i + 1) % pts.Count]));
            return r;
        }
    }
}
