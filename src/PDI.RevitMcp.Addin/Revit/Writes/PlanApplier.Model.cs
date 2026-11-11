// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Revit.Writes
{
    /// <summary>
    /// 0.9.1 (Bloques C/D/E): creación y verificación de las operaciones tipadas de modelado.
    /// Guardas al aplicar (lo resuelto al planificar sigue existiendo y el nombre sigue libre), creación con la API pública de Revit,
    /// y verificación de lo creado: se relee cada elemento y se compara con lo pedido (elevación, extremos, nivel, tamaño, miembros
    /// del circuito y cuadro, campos de la tabla, habitación delimitada). Una diferencia se informa en "verificacion_creacion".
    /// </summary>
    internal sealed partial class PlanApplier
    {
        private const double Tol = 1e-5;

        private sealed class Made
        {
            public int Op;
            public string Kind;
            public ElementId Id;
            public Dictionary<string, object> T;
        }

        private readonly List<Made> _made = new List<Made>();

        private static readonly HashSet<string> ModelKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "level_create", "grid_create", "wall_create", "floor_create", "ceiling_create", "room_create", "duct_create", "pipe_create", "circuit_create", "schedule_create",
        };

        private string ModelGuard(string op, Dictionary<string, object> t)
        {
            if (!ModelKinds.Contains(op)) return null;
            bool Exists(string k) => !t.ContainsKey(k) || _doc.GetElement(Ids.ToId(WriteKit.L(t, k))) != null;
            foreach (var k in new[] { "nivel", "nivel_superior", "wtype", "stype", "sys", "ctype", "panel", "vft_planta", "vft_rcp" })
                if (!Exists(k)) return "\u0000" + k + " ya no existe";
            var name = WriteKit.S(t, "nombre");
            switch (op)
            {
                case "level_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(Level)).Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000ya existe el nivel '" + name + "'";
                    return "";
                case "grid_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000ya existe la rejilla '" + name + "'";
                    return "";
                case "schedule_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000ya existe la tabla '" + name + "'";
                    return "";
                case "circuit_create":
                    foreach (var id in WriteKit.Longs(t, "ids"))
                    {
                        var e = _doc.GetElement(Ids.ToId(id));
                        if (e == null) return "\u0000" + id + " ya no existe";
                        if (!PlanBuilder.HasFreePowerConnector(e)) return "\u0000" + id + " ya no tiene conector de fuerza libre (¿lo circuitó alguien después de planificar?)";
                    }
                    return "";
                default:
                    return "";
            }
        }

        private static XYZ Xyz(Dictionary<string, object> t, string k) => WriteKit.P(t, k);

        private static List<XYZ> PtList(Dictionary<string, object> t, string k, double z) =>
            ((List<object>)t[k]).Cast<List<object>>().Select(l => new XYZ(N(l, 0), N(l, 1), l.Count > 2 ? N(l, 2) : z)).ToList();

        private static CurveLoop Loop(List<XYZ> pts)
        {
            var loop = new CurveLoop();
            for (var i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            return loop;
        }

        private ElementId Track(int opIndex, string kind, ElementId id, Dictionary<string, object> t)
        {
            _made.Add(new Made { Op = opIndex, Kind = kind, Id = id, T = t });
            return id;
        }

        private IEnumerable<ElementId> ModelCreate(string op, Dictionary<string, object> t, int opIndex)
        {
            if (!ModelKinds.Contains(op)) return null;
            switch (op)
            {
                case "level_create":
                {
                    var lvl = Level.Create(_doc, WriteKit.D(t, "elev"));
                    var name = WriteKit.S(t, "nombre");
                    if (name != null) lvl.Name = name;
                    var made = new List<ElementId> { Track(opIndex, op, lvl.Id, t) };
                    foreach (var k in new[] { "vft_planta", "vft_rcp" })
                        if (t.ContainsKey(k)) made.Add(ViewPlan.Create(_doc, Ids.ToId(WriteKit.L(t, k)), lvl.Id).Id);
                    return made;
                }
                case "grid_create":
                {
                    var p = PtList(t, "pts", 0);
                    var g = Grid.Create(_doc, Line.CreateBound(p[0], p[1]));
                    var name = WriteKit.S(t, "nombre");
                    if (name != null) g.Name = name;
                    return new[] { Track(opIndex, op, g.Id, t) };
                }
                case "wall_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "nivel")));
                    var p = PtList(t, "pts", lvl.Elevation);
                    var w = Wall.Create(_doc, Line.CreateBound(p[0], p[1]), Ids.ToId(WriteKit.L(t, "wtype")), lvl.Id,
                                        WriteKit.D(t, "altura"), WriteKit.D(t, "desfase", 0), false, WriteKit.B(t, "estructural"));
                    if (t.ContainsKey("nivel_superior"))
                    {
                        var hp = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                        if (hp == null || hp.IsReadOnly || !hp.Set(Ids.ToId(WriteKit.L(t, "nivel_superior"))))
                            throw new InvalidOperationException("no se pudo restringir el muro al nivel superior");
                    }
                    return new[] { Track(opIndex, op, w.Id, t) };
                }
                case "floor_create":
                case "ceiling_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "nivel")));
                    var loops = new List<CurveLoop> { Loop(PtList(t, "pts", lvl.Elevation)) };
                    Element e;
                    BuiltInParameter offP;
                    if (op == "floor_create")
                    {
                        e = Floor.Create(_doc, loops, Ids.ToId(WriteKit.L(t, "stype")), lvl.Id, WriteKit.B(t, "estructural"), null, 0.0);
                        offP = BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
                    }
                    else
                    {
                        e = Ceiling.Create(_doc, loops, Ids.ToId(WriteKit.L(t, "stype")), lvl.Id);
                        offP = BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM;
                    }
                    var off = WriteKit.D(t, "desfase", 0);
                    var pp = e.get_Parameter(offP);
                    if (pp != null && !pp.IsReadOnly && Math.Abs(pp.AsDouble() - off) > Tol && !pp.Set(off))
                        throw new InvalidOperationException("no se pudo fijar la altura sobre el nivel");
                    return new[] { Track(opIndex, op, e.Id, t) };
                }
                case "room_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "nivel")));
                    var room = _doc.Create.NewRoom(lvl, new UV(WriteKit.D(t, "x"), WriteKit.D(t, "y")))
                               ?? throw new InvalidOperationException("Revit no creó la habitación");
                    var name = WriteKit.S(t, "nombre"); if (name != null) room.Name = name;
                    var number = WriteKit.S(t, "numero"); if (number != null) room.Number = number;
                    return new[] { Track(opIndex, op, room.Id, t) };
                }
                case "duct_create":
                case "pipe_create":
                {
                    var a = Xyz(t, "a"); var b = Xyz(t, "b");
                    MEPCurve m = op == "duct_create"
                        ? (MEPCurve)Duct.Create(_doc, Ids.ToId(WriteKit.L(t, "sys")), Ids.ToId(WriteKit.L(t, "ctype")), Ids.ToId(WriteKit.L(t, "nivel")), a, b)
                        : Pipe.Create(_doc, Ids.ToId(WriteKit.L(t, "sys")), Ids.ToId(WriteKit.L(t, "ctype")), Ids.ToId(WriteKit.L(t, "nivel")), a, b);
                    foreach (var (k, bip) in SizeParams(op))
                    {
                        if (!t.ContainsKey(k)) continue;
                        var sp = m.get_Parameter(bip);
                        if (sp == null || sp.IsReadOnly || !sp.Set(WriteKit.D(t, k))) throw new InvalidOperationException("no se pudo fijar " + k);
                    }
                    return new[] { Track(opIndex, op, m.Id, t) };
                }
                case "circuit_create":
                {
                    var ids = WriteKit.Longs(t, "ids").Select(Ids.ToId).ToList();
                    var es = ElectricalSystem.Create(_doc, ids, ElectricalSystemType.PowerCircuit)
                             ?? throw new InvalidOperationException("Revit no creó el circuito (¿dispositivos de distinto tipo de sistema o tensión?)");
                    if (t.ContainsKey("panel"))
                    {
                        var panel = _doc.GetElement(Ids.ToId(WriteKit.L(t, "panel"))) as FamilyInstance ?? throw new InvalidOperationException("el cuadro ya no existe");
                        es.SelectPanel(panel);
                    }
                    return new[] { Track(opIndex, op, es.Id, t) };
                }
                case "schedule_create":
                {
                    var vs = ViewSchedule.CreateSchedule(_doc, Ids.ToId(WriteKit.L(t, "cat")));
                    var name = WriteKit.S(t, "nombre"); if (name != null) vs.Name = name;
                    var def = vs.Definition;
                    var available = def.GetSchedulableFields().ToList();
                    var missing = new List<string>();
                    foreach (var f in ((List<object>)t["campos"]).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))
                    {
                        var sf = available.FirstOrDefault(x => string.Equals(x.GetName(_doc), f, StringComparison.OrdinalIgnoreCase));
                        if (sf == null) { missing.Add(f); continue; }
                        def.AddField(sf);
                    }
                    if (missing.Count > 0)
                        throw new InvalidOperationException("la categoría no tiene los campos " + string.Join(", ", missing) + ". Campos posibles: " +
                                                            string.Join(", ", available.Select(x => x.GetName(_doc)).Distinct().OrderBy(x => x).Take(40)));
                    return new[] { Track(opIndex, op, vs.Id, t) };
                }
            }
            return null;
        }

        private static IEnumerable<(string, BuiltInParameter)> SizeParams(string op) => op == "duct_create"
            ? new[] { ("diametro", BuiltInParameter.RBS_CURVE_DIAMETER_PARAM), ("ancho", BuiltInParameter.RBS_CURVE_WIDTH_PARAM), ("alto", BuiltInParameter.RBS_CURVE_HEIGHT_PARAM) }
            : new[] { ("diametro", BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) };

        private static bool Near(XYZ a, XYZ b) => a != null && b != null && a.DistanceTo(b) < Tol;

        private static bool SameLine(Curve c, XYZ a, XYZ b) =>
            c != null && ((Near(c.GetEndPoint(0), a) && Near(c.GetEndPoint(1), b)) || (Near(c.GetEndPoint(0), b) && Near(c.GetEndPoint(1), a)));

        /// <summary>Lo que no coincide con lo pedido en un elemento creado; null si todo coincide.</summary>
        private string CheckMade(Made m)
        {
            var e = _doc.GetElement(m.Id);
            if (e == null) return "ya no existe";
            var t = m.T;
            switch (m.Kind)
            {
                case "level_create":
                {
                    var l = (Level)e;
                    if (Math.Abs(l.Elevation - WriteKit.D(t, "elev")) > Tol) return "elevación " + l.Elevation + " ft, se pidió " + WriteKit.D(t, "elev");
                    var n = WriteKit.S(t, "nombre");
                    return n != null && l.Name != n ? "nombre '" + l.Name + "'" : null;
                }
                case "grid_create":
                {
                    var p = PtList(t, "pts", 0);
                    var gc = ((Grid)e).Curve;
                    // La rejilla vive en el plano XY: se comparan los extremos en planta.
                    return gc != null && SameLine(Line.CreateBound(new XYZ(gc.GetEndPoint(0).X, gc.GetEndPoint(0).Y, 0), new XYZ(gc.GetEndPoint(1).X, gc.GetEndPoint(1).Y, 0)), p[0], p[1]) ? null : "los extremos no coinciden";
                }
                case "wall_create":
                {
                    var w = (Wall)e;
                    if (w.LevelId != Ids.ToId(WriteKit.L(t, "nivel"))) return "nivel distinto";
                    var p = PtList(t, "pts", 0);
                    var lc = (w.Location as LocationCurve)?.Curve;
                    if (lc == null) return "sin línea de ubicación";
                    var flat = Line.CreateBound(new XYZ(lc.GetEndPoint(0).X, lc.GetEndPoint(0).Y, 0), new XYZ(lc.GetEndPoint(1).X, lc.GetEndPoint(1).Y, 0));
                    if (!SameLine(flat, p[0], p[1])) return "los extremos no coinciden";
                    if (t.ContainsKey("nivel_superior"))
                        return w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() == Ids.ToId(WriteKit.L(t, "nivel_superior")) ? null : "no quedó restringido al nivel superior";
                    var h = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? double.NaN;
                    return Math.Abs(h - WriteKit.D(t, "altura")) > Tol ? "altura " + h + " ft" : null;
                }
                case "floor_create":
                case "ceiling_create":
                {
                    if (e.LevelId != Ids.ToId(WriteKit.L(t, "nivel"))) return "nivel distinto";
                    var bip = m.Kind == "floor_create" ? BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM : BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM;
                    var off = e.get_Parameter(bip)?.AsDouble();
                    if (off.HasValue && Math.Abs(off.Value - WriteKit.D(t, "desfase", 0)) > Tol) return "altura sobre el nivel " + off.Value + " ft";
                    var area = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                    return area > 0 ? null : "sin área";
                }
                case "room_create":
                {
                    var r = (Room)e;
                    if (r.Location == null) return "sin colocar";
                    return r.Area > 0 ? null : "no delimitada (área 0): el punto no está dentro de un recinto cerrado";
                }
                case "duct_create":
                case "pipe_create":
                {
                    var lc = ((e.Location as LocationCurve)?.Curve);
                    if (!SameLine(lc, Xyz(t, "a"), Xyz(t, "b"))) return "los extremos no coinciden";
                    foreach (var (k, bip) in SizeParams(m.Kind))
                    {
                        if (!t.ContainsKey(k)) continue;
                        var v = e.get_Parameter(bip)?.AsDouble() ?? double.NaN;
                        if (Math.Abs(v - WriteKit.D(t, k)) > Tol) return k + " " + v + " ft (Revit lo ajustó a un tamaño del tipo)";
                    }
                    return null;
                }
                case "circuit_create":
                {
                    var es = (ElectricalSystem)e;
                    var want = new HashSet<long>(WriteKit.Longs(t, "ids"));
                    var got = new HashSet<long>(es.Elements.Cast<Element>().Select(x => Ids.ToLong(x.Id)));
                    if (!want.SetEquals(got)) return "miembros " + got.Count + ", se pidieron " + want.Count;
                    if (t.ContainsKey("panel") && (es.BaseEquipment == null || Ids.ToLong(es.BaseEquipment.Id) != WriteKit.L(t, "panel"))) return "no quedó en el cuadro pedido";
                    return null;
                }
                case "schedule_create":
                {
                    var vs = (ViewSchedule)e;
                    var want = ((List<object>)t["campos"]).Count;
                    var got = vs.Definition.GetFieldCount();
                    return got == want ? null : "campos " + got + ", se pidieron " + want;
                }
            }
            return null;
        }

        private Dictionary<string, object> VerifyModelCreations()
        {
            if (_made.Count == 0) return null;
            var problems = new List<object>();
            var ok = 0;
            foreach (var m in _made)
            {
                string why;
                try { why = CheckMade(m); } catch (Exception ex) { why = "no se pudo releer: " + ex.Message; }
                if (why == null) { ok++; continue; }
                if (problems.Count < 10) problems.Add("@" + m.Op.ToString(CultureInfo.InvariantCulture) + " " + m.Kind + " " + Ids.ToLong(m.Id) + ": " + why);
            }
            var d = new Dictionary<string, object> { ["revisados"] = (long)_made.Count, ["como_se_pidio"] = (long)ok };
            if (problems.Count > 0) d["problemas"] = problems;
            return d;
        }
    }
}
