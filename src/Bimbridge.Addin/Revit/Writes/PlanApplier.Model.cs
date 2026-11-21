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
using Bimbridge.Engine;

namespace Bimbridge.Addin.Revit.Writes
{
    /// <summary>
    /// 0.9.1 (Blocks C/D/E): creation and verification of the typed modeling operations.
    /// Guards at apply time (what was resolved while planning still exists and the name is still free), creation with Revit's public API,
    /// and verification of what was created: each element is read back and compared with the request (elevation, ends, level, size, circuit
    /// members and panel, schedule fields, bounded room). A difference is reported in "creation_verification".
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
            "level_create", "grid_create", "wall_create", "floor_create", "ceiling_create", "room_create", "duct_create", "pipe_create", "conduit_create", "conduit_route", "circuit_create", "schedule_create",
        };

        private string ModelGuard(string op, Dictionary<string, object> t)
        {
            if (!ModelKinds.Contains(op)) return null;
            bool Exists(string k) => !t.ContainsKey(k) || _doc.GetElement(Ids.ToId(WriteKit.L(t, k))) != null;
            foreach (var k in new[] { "level", "top_level", "wtype", "stype", "sys", "ctype", "panel", "vft_plan", "vft_rcp" })
                if (!Exists(k)) return "\u0000" + k + " no longer exists";
            var name = WriteKit.S(t, "name");
            switch (op)
            {
                case "conduit_route":
                    return VerifyRouteScope(t) is string routeError ? "\u0000" + routeError : "";
                case "level_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(Level)).Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000level already exists: '" + name + "'";
                    return "";
                case "grid_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000grid already exists: '" + name + "'";
                    return "";
                case "schedule_create":
                    if (name != null && new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000schedule already exists: '" + name + "'";
                    return "";
                case "circuit_create":
                    foreach (var id in WriteKit.Longs(t, "ids"))
                    {
                        var e = _doc.GetElement(Ids.ToId(id));
                        if (e == null) return "\u0000" + id + " no longer exists";
                        if (!PlanBuilder.HasFreePowerConnector(e)) return "\u0000" + id + " no longer has a free power connector (did someone circuit it after planning?)";
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
                    var name = WriteKit.S(t, "name");
                    if (name != null) lvl.Name = name;
                    var made = new List<ElementId> { Track(opIndex, op, lvl.Id, t) };
                    foreach (var k in new[] { "vft_plan", "vft_rcp" })
                        if (t.ContainsKey(k)) made.Add(ViewPlan.Create(_doc, Ids.ToId(WriteKit.L(t, k)), lvl.Id).Id);
                    return made;
                }
                case "grid_create":
                {
                    var p = PtList(t, "pts", 0);
                    var g = Grid.Create(_doc, Line.CreateBound(p[0], p[1]));
                    var name = WriteKit.S(t, "name");
                    if (name != null) g.Name = name;
                    return new[] { Track(opIndex, op, g.Id, t) };
                }
                case "wall_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "level")));
                    var p = PtList(t, "pts", lvl.Elevation);
                    var w = Wall.Create(_doc, Line.CreateBound(p[0], p[1]), Ids.ToId(WriteKit.L(t, "wtype")), lvl.Id,
                                        WriteKit.D(t, "height"), WriteKit.D(t, "offset", 0), false, WriteKit.B(t, "structural"));
                    if (t.ContainsKey("top_level"))
                    {
                        var hp = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                        if (hp == null || hp.IsReadOnly || !hp.Set(Ids.ToId(WriteKit.L(t, "top_level"))))
                            throw new InvalidOperationException("could not constrain the wall to the top level");
                    }
                    return new[] { Track(opIndex, op, w.Id, t) };
                }
                case "floor_create":
                case "ceiling_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "level")));
                    var loops = new List<CurveLoop> { Loop(PtList(t, "pts", lvl.Elevation)) };
                    Element e;
                    BuiltInParameter offP;
                    if (op == "floor_create")
                    {
                        e = Floor.Create(_doc, loops, Ids.ToId(WriteKit.L(t, "stype")), lvl.Id, WriteKit.B(t, "structural"), null, 0.0);
                        offP = BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
                    }
                    else
                    {
                        e = Ceiling.Create(_doc, loops, Ids.ToId(WriteKit.L(t, "stype")), lvl.Id);
                        offP = BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM;
                    }
                    var off = WriteKit.D(t, "offset", 0);
                    var pp = e.get_Parameter(offP);
                    if (pp != null && !pp.IsReadOnly && Math.Abs(pp.AsDouble() - off) > Tol && !pp.Set(off))
                        throw new InvalidOperationException("could not set the height above the level");
                    return new[] { Track(opIndex, op, e.Id, t) };
                }
                case "room_create":
                {
                    var lvl = (Level)_doc.GetElement(Ids.ToId(WriteKit.L(t, "level")));
                    var room = _doc.Create.NewRoom(lvl, new UV(WriteKit.D(t, "x"), WriteKit.D(t, "y")))
                               ?? throw new InvalidOperationException("Revit did not create the room");
                    var name = WriteKit.S(t, "name"); if (name != null) room.Name = name;
                    var number = WriteKit.S(t, "number"); if (number != null) room.Number = number;
                    return new[] { Track(opIndex, op, room.Id, t) };
                }
                case "conduit_create":
                {
                    var m = CreateConduit(t, Xyz(t, "a"), Xyz(t, "b"));
                    return new[] { Track(opIndex, op, m.Id, t) };
                }
                case "conduit_route": return CreateConduitRoute(t);
                case "duct_create":
                case "pipe_create":
                {
                    var a = Xyz(t, "a"); var b = Xyz(t, "b");
                    MEPCurve m = op == "duct_create"
                        ? (MEPCurve)Duct.Create(_doc, Ids.ToId(WriteKit.L(t, "sys")), Ids.ToId(WriteKit.L(t, "ctype")), Ids.ToId(WriteKit.L(t, "level")), a, b)
                        : Pipe.Create(_doc, Ids.ToId(WriteKit.L(t, "sys")), Ids.ToId(WriteKit.L(t, "ctype")), Ids.ToId(WriteKit.L(t, "level")), a, b);
                    foreach (var (k, bip) in SizeParams(op))
                    {
                        if (!t.ContainsKey(k)) continue;
                        var sp = m.get_Parameter(bip);
                        if (sp == null || sp.IsReadOnly || !sp.Set(WriteKit.D(t, k))) throw new InvalidOperationException("could not set " + k);
                    }
                    return new[] { Track(opIndex, op, m.Id, t) };
                }
                case "circuit_create":
                {
                    var ids = WriteKit.Longs(t, "ids").Select(Ids.ToId).ToList();
                    var es = ElectricalSystem.Create(_doc, ids, ElectricalSystemType.PowerCircuit)
                             ?? throw new InvalidOperationException("Revit did not create the circuit (devices with a different system type or voltage?)");
                    if (t.ContainsKey("panel"))
                    {
                        var panel = _doc.GetElement(Ids.ToId(WriteKit.L(t, "panel"))) as FamilyInstance ?? throw new InvalidOperationException("the panel no longer exists");
                        es.SelectPanel(panel);
                    }
                    return new[] { Track(opIndex, op, es.Id, t) };
                }
                case "schedule_create":
                {
                    var vs = ViewSchedule.CreateSchedule(_doc, Ids.ToId(WriteKit.L(t, "cat")));
                    var name = WriteKit.S(t, "name"); if (name != null) vs.Name = name;
                    var def = vs.Definition;
                    var available = def.GetSchedulableFields().ToList();
                    var missing = new List<string>();
                    foreach (var f in ((List<object>)t["fields"]).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))
                    {
                        var sf = available.FirstOrDefault(x => string.Equals(x.GetName(_doc), f, StringComparison.OrdinalIgnoreCase));
                        if (sf == null) { missing.Add(f); continue; }
                        def.AddField(sf);
                    }
                    if (missing.Count > 0)
                        throw new InvalidOperationException("the category does not have the fields " + string.Join(", ", missing) + ". Available fields: " +
                                                            string.Join(", ", available.Select(x => x.GetName(_doc)).Distinct().OrderBy(x => x).Take(40)));
                    return new[] { Track(opIndex, op, vs.Id, t) };
                }
            }
            return null;
        }

        private Conduit CreateConduit(Dictionary<string, object> t, XYZ a, XYZ b)
        {
            var m = Conduit.Create(_doc, Ids.ToId(WriteKit.L(t, "ctype")), a, b, Ids.ToId(WriteKit.L(t, "level")));
            var diameter = m.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (diameter == null || diameter.IsReadOnly || !diameter.Set(WriteKit.D(t, "diameter")))
                throw new InvalidOperationException("could not set the conduit diameter");
            SetConduitWorkset(m, t);
            return m;
        }

        private static void SetConduitWorkset(Element e, Dictionary<string, object> t)
        {
            if (!t.ContainsKey("ws") || e.WorksetId.IntegerValue == WriteKit.L(t, "ws")) return;
            var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p == null || p.IsReadOnly || !p.Set((int)WriteKit.L(t, "ws")))
                throw new InvalidOperationException("could not set the run's workset");
        }

        private readonly List<(Dictionary<string, object> Target, List<ElementId> Segments, List<ElementId> Elbows)> _conduitRoutes =
            new List<(Dictionary<string, object>, List<ElementId>, List<ElementId>)>();

        private IEnumerable<ElementId> CreateConduitRoute(Dictionary<string, object> target)
        {
            var points = RoutePoints(target);
            var scopeError = VerifyRouteScope(target);
            if (scopeError != null) throw new InvalidOperationException(scopeError);
            if (target.ContainsKey("replace_ids"))
            {
                var replacement = WriteKit.Longs(target, "replace_ids").Select(Ids.ToId).ToList();
                var removed = _doc.Delete(replacement);
                if (!new HashSet<ElementId>(removed).SetEquals(replacement))
                    throw new InvalidOperationException("Revit tried to delete elements outside the scope; the replacement is rolled back");
                foreach (var id in removed) _deleted.Add(Ids.ToLong(id));
                _r.Deleted += removed.Count;
                _doc.Regenerate();
            }
            var segments = new List<Conduit>();
            for (var i = 1; i < points.Count; i++) segments.Add(CreateConduit(target, points[i - 1], points[i]));
            _doc.Regenerate();
            var elbows = new List<ElementId>();
            for (var i = 1; i < segments.Count; i++)
            {
                var before = AtEnd(segments[i - 1], points[i]);
                var after = AtEnd(segments[i], points[i]);
                var elbow = _doc.Create.NewElbowFitting(before, after) ?? throw new InvalidOperationException("Revit did not create elbow " + i);
                SetConduitWorkset(elbow, target);
                elbows.Add(elbow.Id);
            }
            _doc.Regenerate();
            var terminals = RouteTerminals(target);
            if (terminals[0] is Dictionary<string, object> first)
                AtEnd(segments[0], points[0]).ConnectTo(ConduitRouteScope.Resolve(_doc, first, points[0], mustBeFree: true));
            if (terminals[1] is Dictionary<string, object> last)
                AtEnd(segments.Last(), points.Last()).ConnectTo(ConduitRouteScope.Resolve(_doc, last, points.Last(), mustBeFree: true));
            var ids = segments.Select(s => s.Id).ToList();
            _conduitRoutes.Add((target, ids, elbows));
            return ids.Concat(elbows).ToArray();
        }

        private static List<XYZ> RoutePoints(Dictionary<string, object> t) =>
            OpCatalog.Points(t["points"], "conduit_route points", 3, 100).Select(p => new XYZ(p[0], p[1], p[2])).ToList();

        private static List<object> RouteTerminals(Dictionary<string, object> target) =>
            target.TryGetValue("ends", out var ends) ? (List<object>)ends : new List<object> { null, null };

        private string VerifyRouteScope(Dictionary<string, object> target)
        {
            if (!target.ContainsKey("ends")) return null;
            try
            {
                var points = RoutePoints(target);
                var replacing = target.ContainsKey("replace_ids");
                if (replacing)
                {
                    var current = ConduitRouteScope.Capture(_doc, WriteKit.Longs(target, "replace_ids").ToList(), points[0], points.Last());
                    if (!Equals(current["fingerprint"], target["original_fingerprint"])) return "the original run changed after planning";
                }
                var terminals = RouteTerminals(target);
                if (terminals[0] is Dictionary<string, object> first)
                    ConduitRouteScope.Resolve(_doc, first, points[0], mustBeFree: !replacing);
                if (terminals[1] is Dictionary<string, object> last)
                    ConduitRouteScope.Resolve(_doc, last, points.Last(), mustBeFree: !replacing);
                return null;
            }
            catch (QueryException ex) { return ex.Message; }
        }

        private static Connector AtEnd(Conduit segment, XYZ point)
        {
            var ends = segment.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End && Near(c.Origin, point)).ToList();
            if (ends.Count != 1 || ends[0].IsConnected)
                throw new InvalidOperationException("the conduit end is not free or could not be identified");
            return ends[0];
        }

        private string VerifyConduitRoutes()
        {
            foreach (var (target, segments, elbows) in _conduitRoutes)
            {
                var points = RoutePoints(target);
                var live = segments.Select(id => _doc.GetElement(id) as Conduit).ToList();
                if (live.Any(s => s == null)) return "Run: a created segment is missing";
                bool HasEnd(Conduit s, XYZ point, object terminal)
                {
                    var connector = s.ConnectorManager.Connectors.Cast<Connector>()
                        .SingleOrDefault(c => c.ConnectorType == ConnectorType.End &&
                            c.Origin.DistanceTo(point) <= (terminal == null ? Tol : 1.0 / 304.8));
                    if (connector == null) return false;
                    if (!(terminal is Dictionary<string, object> saved)) return !connector.IsConnected;
                    try
                    {
                        var external = ConduitRouteScope.Resolve(_doc, saved, point, mustBeFree: false);
                        var refs = ConduitRouteScope.PhysicalReferences(connector);
                        return refs.Count == 1 && connector.IsConnectedTo(external);
                    }
                    catch (QueryException) { return false; }
                }
                bool Connects(Conduit s, ElementId elbow) => s.ConnectorManager.Connectors.Cast<Connector>()
                    .Any(c => c.ConnectorType == ConnectorType.End && c.IsConnected &&
                        c.AllRefs.Cast<Connector>().Any(r => r.ConnectorType == ConnectorType.End && r.Owner.Id == elbow));
                var terminals = RouteTerminals(target);
                if (!HasEnd(live[0], points[0], terminals[0]) || !HasEnd(live.Last(), points.Last(), terminals[1]))
                    return "Run: the ends changed";
                for (var i = 0; i < elbows.Count; i++)
                    if (_doc.GetElement(elbows[i]) == null || !Connects(live[i], elbows[i]) || !Connects(live[i + 1], elbows[i]))
                        return "Run: elbow " + (i + 1) + " does not connect its two segments";
                for (var i = 0; i < live.Count; i++)
                {
                    var s = live[i];
                    var size = s.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    if (size == null || ModelOps.Differs(size.AsDouble(), WriteKit.D(target, "diameter"), Tol) ||
                        Ids.ToLong(s.GetTypeId()) != WriteKit.L(target, "ctype") ||
                        Ids.ToLong(s.ReferenceLevel?.Id) != WriteKit.L(target, "level") ||
                        (target.ContainsKey("ws") && s.WorksetId.IntegerValue != WriteKit.L(target, "ws")))
                        return "Run: the type, level, diameter or workset does not match";
                    if (!((s.Location as LocationCurve)?.Curve is Line line) ||
                        !WithinLeg(line.GetEndPoint(0), points[i], points[i + 1]) ||
                        !WithinLeg(line.GetEndPoint(1), points[i], points[i + 1]))
                        return "Run: the axis of segment " + (i + 1) + " does not match the planned route";
                }
            }
            return null;
        }

        private static bool WithinLeg(XYZ point, XYZ start, XYZ end)
        {
            const double tolerance = 1.0 / 304.8;
            var axis = end - start;
            var offset = point - start;
            var direction = axis.Normalize();
            var projection = offset.DotProduct(direction);
            return projection >= -tolerance && projection <= axis.GetLength() + tolerance &&
                (offset - projection * direction).GetLength() <= tolerance;
        }

        private static IEnumerable<(string, BuiltInParameter)> SizeParams(string op) => op == "duct_create"
            ? new[] { ("diameter", BuiltInParameter.RBS_CURVE_DIAMETER_PARAM), ("width", BuiltInParameter.RBS_CURVE_WIDTH_PARAM), ("height", BuiltInParameter.RBS_CURVE_HEIGHT_PARAM) }
            : new[] { ("diameter", op == "conduit_create" ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM : BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) };

        private static bool Near(XYZ a, XYZ b) => a != null && b != null && a.DistanceTo(b) < Tol;

        private static bool SameLine(Curve c, XYZ a, XYZ b) =>
            c != null && ((Near(c.GetEndPoint(0), a) && Near(c.GetEndPoint(1), b)) || (Near(c.GetEndPoint(0), b) && Near(c.GetEndPoint(1), a)));

        /// <summary>What does not match the request in a created element; null if everything matches.</summary>
        private string CheckMade(Made m)
        {
            var e = _doc.GetElement(m.Id);
            if (e == null) return "no longer exists";
            var t = m.T;
            switch (m.Kind)
            {
                case "level_create":
                {
                    var l = (Level)e;
                    if (ModelOps.Differs(l.Elevation, WriteKit.D(t, "elev"), Tol)) return "elevation " + l.Elevation + " ft, requested " + WriteKit.D(t, "elev");
                    var n = WriteKit.S(t, "name");
                    return n != null && l.Name != n ? "name '" + l.Name + "'" : null;
                }
                case "grid_create":
                {
                    var p = PtList(t, "pts", 0);
                    var gc = ((Grid)e).Curve;
                    // The grid lives in the XY plane: the ends are compared in plan.
                    return gc != null && SameLine(Line.CreateBound(new XYZ(gc.GetEndPoint(0).X, gc.GetEndPoint(0).Y, 0), new XYZ(gc.GetEndPoint(1).X, gc.GetEndPoint(1).Y, 0)), p[0], p[1]) ? null : "the ends do not match";
                }
                case "wall_create":
                {
                    var w = (Wall)e;
                    if (w.LevelId != Ids.ToId(WriteKit.L(t, "level"))) return "different level";
                    var p = PtList(t, "pts", 0);
                    var lc = (w.Location as LocationCurve)?.Curve;
                    if (lc == null) return "no location line";
                    var flat = Line.CreateBound(new XYZ(lc.GetEndPoint(0).X, lc.GetEndPoint(0).Y, 0), new XYZ(lc.GetEndPoint(1).X, lc.GetEndPoint(1).Y, 0));
                    if (!SameLine(flat, p[0], p[1])) return "the ends do not match";
                    if (t.ContainsKey("top_level"))
                        return w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() == Ids.ToId(WriteKit.L(t, "top_level")) ? null : "it was not constrained to the top level";
                    var hp = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (hp == null || !hp.HasValue) return "could not read the height (parameter missing)";
                    var h = hp.AsDouble();
                    return ModelOps.Differs(h, WriteKit.D(t, "height"), Tol) ? "height " + h + " ft" : null;
                }
                case "floor_create":
                case "ceiling_create":
                {
                    if (e.LevelId != Ids.ToId(WriteKit.L(t, "level"))) return "different level";
                    var bip = m.Kind == "floor_create" ? BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM : BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM;
                    var op = e.get_Parameter(bip);
                    if (op == null || !op.HasValue) return "could not read the height above the level (parameter missing)";
                    if (ModelOps.Differs(op.AsDouble(), WriteKit.D(t, "offset", 0), Tol)) return "height above the level " + op.AsDouble() + " ft";
                    var ap = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    if (ap == null || !ap.HasValue) return "could not read the area (parameter missing)";
                    var area = ap.AsDouble();
                    return !double.IsNaN(area) && !double.IsInfinity(area) && area > 0 ? null : "no area";
                }
                case "room_create":
                {
                    var r = (Room)e;
                    if (r.Location == null) return "not placed";
                    return r.Area > 0 ? null : "not bounded (area 0): the point is not inside a closed enclosure";
                }
                case "duct_create":
                case "pipe_create":
                case "conduit_create":
                {
                    var lc = ((e.Location as LocationCurve)?.Curve);
                    if (!SameLine(lc, Xyz(t, "a"), Xyz(t, "b"))) return "the ends do not match";
                    if (m.Kind == "conduit_create" && (Ids.ToLong(e.GetTypeId()) != WriteKit.L(t, "ctype") ||
                        Ids.ToLong(((Conduit)e).ReferenceLevel?.Id) != WriteKit.L(t, "level") ||
                        (t.ContainsKey("ws") && e.WorksetId.IntegerValue != WriteKit.L(t, "ws"))))
                        return "the conduit type, level or workset does not match";
                    foreach (var (k, bip) in SizeParams(m.Kind))
                    {
                        if (!t.ContainsKey(k)) continue;
                        var sp = e.get_Parameter(bip);
                        if (sp == null || !sp.HasValue) return "could not read " + k + " (parameter missing)";
                        var v = sp.AsDouble();
                        if (ModelOps.Differs(v, WriteKit.D(t, k), Tol)) return k + " " + v + " ft (Revit adjusted it to a type size)";
                    }
                    return null;
                }
                case "circuit_create":
                {
                    var es = (ElectricalSystem)e;
                    var want = new HashSet<long>(WriteKit.Longs(t, "ids"));
                    var got = new HashSet<long>(es.Elements.Cast<Element>().Select(x => Ids.ToLong(x.Id)));
                    if (!want.SetEquals(got)) return "members " + got.Count + ", requested " + want.Count;
                    if (t.ContainsKey("panel") && (es.BaseEquipment == null || Ids.ToLong(es.BaseEquipment.Id) != WriteKit.L(t, "panel"))) return "it did not end up on the requested panel";
                    return null;
                }
                case "schedule_create":
                {
                    var vs = (ViewSchedule)e;
                    var want = ((List<object>)t["fields"]).Count;
                    var got = vs.Definition.GetFieldCount();
                    return got == want ? null : "fields " + got + ", requested " + want;
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
                try { why = CheckMade(m); } catch (Exception ex) { why = "could not read back: " + ex.Message; }
                if (why == null) { ok++; continue; }
                if (problems.Count < 10) problems.Add("@" + m.Op.ToString(CultureInfo.InvariantCulture) + " " + m.Kind + " " + Ids.ToLong(m.Id) + ": " + why);
            }
            var d = new Dictionary<string, object> { ["reviewed"] = (long)_made.Count, ["as_requested"] = (long)ok };
            if (problems.Count > 0) d["problems"] = problems;
            return d;
        }
    }
}
