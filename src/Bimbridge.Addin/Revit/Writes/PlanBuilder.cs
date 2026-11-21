// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    /// <summary>What planning needs: model, plan, held queue and geometry measurements already taken.</summary>
    internal sealed class BuildContext
    {
        public Document Doc;
        public Plan Plan;
        public ModelIndex Model;
        public Func<long, string, Tuple<string, string>> HeldAfter;
        /// <summary>Mounting measurements per operation (snap_to_surface: by id; place: by point index).</summary>
        public Dictionary<int, Dictionary<long, Dictionary<string, object>>> Geo = new Dictionary<int, Dictionary<long, Dictionary<string, object>>>();
        /// <summary>place with mounting: offset at which the virtual body was measured (feet above the level).</summary>
        public Dictionary<int, double> GeoNominal = new Dictionary<int, double>();
        /// <summary>Why the geometry of an operation could not be measured (reported when it is planned).</summary>
        public Dictionary<int, string> GeoErrors = new Dictionary<int, string>();
        public Dictionary<int, OpSpec> Ops = new Dictionary<int, OpSpec>();
        /// <summary>Names of views, sheets and groups created by this same plan (so they are not repeated).</summary>
        public HashSet<string> NewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Planning: validates each operation against the model and turns it into entries (before → after) without touching anything.
    /// Problems with one element skip it with a warning; problems with the whole operation (missing type, view or level) reject it.
    /// </summary>
    internal static partial class PlanBuilder
    {
        /// <summary>Operations that accept ids=@n (elements created by an earlier operation of the same plan).</summary>
        private static readonly HashSet<string> RefIdsOk = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_param", "change_type", "set_elevation", "set_workset", "move", "rotate", "mirror", "copy", "tag",
            "group_create", "view_set", "view_hide", "view_override",
        };

        public static void Build(BuildContext ctx, OpSpec op, IList<long> ids)
        {
            ctx.Ops[op.Index] = op;
            foreach (var r in op.RefOps)
                if (IdsRef(op, r) && !RefIdsOk.Contains(op.Kind))
                    throw new QueryException(op.Kind + ": ids=@" + r + " is not allowed in this operation (only in " + string.Join(", ", RefIdsOk.OrderBy(x => x)) + "). Split the work into two plans.");
            var c = new Collector(ctx, op);
            switch (op.Kind)
            {
                case "set_param": SetParam(ctx, op, ids, c); break;
                case "set_values": SetValues(ctx, op, c); break;
                case "change_type": ChangeType(ctx, op, ids, c); break;
                case "set_elevation": SetElevation(ctx, op, ids, c); break;
                case "set_workset": SetWorkset(ctx, op, ids, c); break;
                case "delete": Delete(ctx, op, ids, c); break;
                case "set_type_param": SetTypeParam(ctx, op, ids, c); break;
                case "set_level": SetLevel(ctx, op, ids, c); break;
                case "snap_to_surface": Snap(ctx, op, ids, c); break;
                case "move": Move(ctx, op, ids, c); break;
                case "rotate": Rotate(ctx, op, ids, c); break;
                case "mirror": Mirror(ctx, op, ids, c); break;
                case "copy": Copy(ctx, op, ids, c); break;
                case "place": Place(ctx, op, c); break;
                case "tag": Tag(ctx, op, ids, c); break;
                case "tag_move": TagMove(ctx, op, ids, c); break;
                case "group_create": GroupCreate(ctx, op, ids, c); break;
                case "ungroup": Ungroup(ctx, op, ids, c); break;
                case "group_place": GroupPlace(ctx, op, c); break;
                case "group_variant":
                {
                    var target = GroupVariant.Plan(ctx, ids.Single(), op.Args);
                    var show = "create group variant " + Arg.GetString(target, "name") + " from " + ids[0] +
                        " with " + ((Dictionary<string, object>)target["changes"]).Count + " substitutions";
                    c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = target });
                    break;
                }
                case "view_create": ViewCreate(ctx, op, c); break;
                case "view_set": ViewSet(ctx, op, ids, c); break;
                case "view_hide": ViewHide(ctx, op, ids, c); break;
                case "view_override": ViewOverride(ctx, op, ids, c); break;
                case "sheet_create": SheetCreate(ctx, op, c); break;
                case "sheet_place": SheetPlace(ctx, op, c); break;
                case "sheet_set": SheetSetOp(ctx, op, c); break;
                case "text_note": TextNoteOp(ctx, op, c); break;
                case "text_note_refresh": RefreshTextNotes(ctx, ids, c); break;
                case "annotation_rebind": RebindAnnotation(ctx, op, ids.Single(), c); break;
                case "filled_region": FilledRegionOp(ctx, op, c); break;
                case "detail_line": DetailCurvesOp(ctx, op, c, "detail_line"); break;
                case "detail_rect": DetailCurvesOp(ctx, op, c, "detail_rect"); break;
                case "detail_polygon": DetailCurvesOp(ctx, op, c, "detail_polygon"); break;
                case "detail_arc": DetailArcOp(ctx, op, c); break;
                case "detail_component": DetailComponentOp(ctx, op, c); break;
                case "dimension": DimensionOp(ctx, op, c); break;
                case "level_create": LevelCreateOp(ctx, op, c); break;
                case "grid_create": GridCreateOp(ctx, op, c); break;
                case "wall_create": WallCreateOp(ctx, op, c); break;
                case "floor_create": case "ceiling_create": SlabCreateOp(ctx, op, c); break;
                case "room_create": RoomCreateOp(ctx, op, c); break;
                case "duct_create": case "pipe_create": MepCurveCreateOp(ctx, op, c); break;
                case "conduit_create": case "conduit_route": ConduitCreateOp(ctx, op, c); break;
                case "circuit_create": CircuitCreateOp(ctx, op, ids, c); break;
                case "schedule_create": ScheduleCreateOp(ctx, op, c); break;
                default: throw new QueryException("operation not implemented: " + op.Kind);
            }
            c.Finish();
        }

        // =====================================================================================================
        // Collector: skipped, unchanged, chained on the queue, entry limit
        // =====================================================================================================

        private sealed class Collector
        {
            private readonly BuildContext _ctx;
            private readonly OpSpec _op;
            private readonly Dictionary<string, List<long>> _skipped = new Dictionary<string, List<long>>();
            private int _unchanged;
            public int Added;

            public Collector(BuildContext ctx, OpSpec op) { _ctx = ctx; _op = op; }

            public void Skip(string why, long id)
            {
                if (!_skipped.TryGetValue(why, out var l)) _skipped[why] = l = new List<long>();
                l.Add(id);
            }

            public void Add(PlanEntry e)
            {
                e.Op = e.Op ?? _op.Kind;
                e.OpIndex = _op.Index;
                if (e.Id != 0 && e.Key != null)
                {
                    var held = _ctx.HeldAfter?.Invoke(e.Id, e.Key);
                    if (held != null)
                    {
                        e.Before = held.Item1;
                        Note("chained on " + held.Item2 + " (held): it will be applied afterwards");
                    }
                    if (!e.IsCreation && e.Key != "new" && string.Equals(e.Before, e.After, StringComparison.Ordinal)) { _unchanged++; return; }
                }
                _ctx.Plan.Entries.Add(e);
                Added++;
                if (_ctx.Plan.Entries.Count > PlanOpsLimits.MaxEntries)
                    throw new QueryException("the plan exceeds " + PlanOpsLimits.MaxEntries + " changes; split it");
            }

            public void Note(string text)
            {
                if (!_ctx.Plan.Warnings.Contains(text)) _ctx.Plan.Warnings.Add(text);
            }

            public void Finish()
            {
                foreach (var kv in _skipped)
                    Note(_op.Kind + " (op " + _op.Index + "): " + kv.Value.Count + " skipped because " + kv.Key + " (" +
                         string.Join(",", kv.Value.Take(8)) + (kv.Value.Count > 8 ? ",…" : "") + ")");
                if (_unchanged > 0) Note(_op.Kind + " (op " + _op.Index + "): " + _unchanged + " were already like that");
            }
        }

        /// <summary>Existing, editable elements from the id list (the rest are skipped with their reason).</summary>
        private static IEnumerable<Element> Editable(BuildContext ctx, IList<long> ids, Collector c, bool allowPinned = true, bool allowGroup = false, bool allowTypes = false)
        {
            foreach (var id in ids.Distinct())
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                if (!allowTypes && e is ElementType) { c.Skip("it is a type (instances only; for types use set_type_param)", id); continue; }
                var why = WriteKit.WhyNotEditable(ctx.Doc, e, allowGroup, allowPinned);
                if (why != null) { c.Skip(why, id); continue; }
                yield return e;
            }
        }

        /// <summary>Template entries for elements created by an earlier operation of the same plan (ids=@n).</summary>
        private static void Templates(OpSpec op, Collector c, string key, object target, string show)
        {
            foreach (var r in op.RefOps.Distinct())
            {
                if (!IdsRef(op, r)) continue;
                c.Add(new PlanEntry { Id = 0, Ref = r, Key = key, Before = "", After = show ?? "", Show = show, Target = target });
            }
        }

        /// <summary>Does the reference @r come from ids (and not from a key such as view/hoja/vista/tipo/origen)?</summary>
        private static bool IdsRef(OpSpec op, int r)
        {
            foreach (var x in Arg.GetStringList(op.Args, "ids"))
                if (x == "@" + r.ToString(CultureInfo.InvariantCulture)) return true;
            return false;
        }

        private static int KeyRef(OpSpec op, string key)
        {
            var v = Arg.GetString(op.Args, key);
            if (v != null && v.StartsWith("@") && int.TryParse(v.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            return 0;
        }

        private static string Ft(double v) => Lengths.FeetInches16(v);

        // =====================================================================================================
        // Parameters, type, elevation, workset, delete
        // =====================================================================================================

        private static void SetParam(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            Templates(op, c, op.Name, new Dictionary<string, object> { ["raw"] = op.Value }, Convert.ToString(op.Value, CultureInfo.InvariantCulture));
            foreach (var e in Editable(ctx, ids, c))
                AddParamEntry(ctx, e, op.Name, op.Value, c);
        }

        private static void AddParamEntry(BuildContext ctx, Element e, string name, object value, Collector c)
        {
            var id = Ids.ToLong(e.Id);
            var p = e.LookupParameter(name);
            if (p == null)
            {
                var tp = ctx.Doc.GetElement(e.GetTypeId())?.LookupParameter(name);
                c.Skip(tp != null ? "'" + name + "' is a type parameter (use set_type_param)" : "does not have the parameter '" + name + "'", id);
                return;
            }
            if (p.IsReadOnly) { c.Skip("'" + name + "' is read-only (calculated or controlled by something else)", id); return; }
            if (!WriteKit.TryTarget(ctx.Doc, p, value, out var target, out var show, out var why))
                throw new QueryException("'" + name + "': " + why);
            c.Add(new PlanEntry { Id = id, Key = name, Before = WriteKit.ParamValue(p), Target = target, After = PlanEntry.Canon(target), Show = show });
        }

        private static void SetValues(BuildContext ctx, OpSpec op, Collector c)
        {
            var values = (Dictionary<string, object>)op.Args["values"];
            var key = (op.Name ?? "").Trim();
            var lower = key.ToLowerInvariant();
            foreach (var kv in values)
            {
                var id = long.Parse(kv.Key, CultureInfo.InvariantCulture);
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                var why = WriteKit.WhyNotEditable(ctx.Doc, e);
                if (why != null) { c.Skip(why, id); continue; }
                switch (lower)
                {
                    case "elev":
                        if (!Lengths.TryFeet(kv.Value, out var ft, out var err)) throw new QueryException("set_values " + id + ": " + err);
                        AddElev(ctx, e, ft, c, null);
                        break;
                    case "ws":
                        AddWorkset(ctx, e, ctx.Model.FindWorkset(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), "set_values"), c);
                        break;
                    case "type":
                        AddType(ctx, e, ctx.Model.FindType(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), "set_values"), c);
                        break;
                    default:
                        AddParamEntry(ctx, e, key, kv.Value, c);
                        break;
                }
            }
        }

        private static void ChangeType(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var t = ctx.Model.FindType(op.Type, "change_type");
            Templates(op, c, "type", Ids.ToLong(t.Id), t.FamilyName + ": " + t.Name);
            foreach (var e in Editable(ctx, ids, c)) AddType(ctx, e, t, c);
        }

        private static void AddType(BuildContext ctx, Element e, ElementType t, Collector c)
        {
            var id = Ids.ToLong(e.Id);
            bool valid;
            try { valid = e.IsValidType(t.Id); } catch (Exception) { valid = false; }
            if (!valid) { c.Skip("the type " + t.FamilyName + ": " + t.Name + " is not valid for that element (different category or different host family)", id); return; }
            c.Add(new PlanEntry { Id = id, Key = "type", Before = WriteKit.TypeName(ctx.Doc, e), Target = Ids.ToLong(t.Id), After = t.FamilyName + ": " + t.Name });
        }

        private static void SetElevation(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            Templates(op, c, "elev", op.Ft.Value, Ft(op.Ft.Value));
            foreach (var e in Editable(ctx, ids, c)) AddElev(ctx, e, op.Ft.Value, c, null);
        }

        private static void AddElev(BuildContext ctx, Element e, double ft, Collector c, string show)
        {
            var id = Ids.ToLong(e.Id);
            var ep = WriteKit.ElevParam(e);
            if (ep == null || ep.IsReadOnly) { c.Skip("has no editable elevation", id); return; }
            c.Add(new PlanEntry { Id = id, Key = "elev", Before = PlanEntry.Canon(ep.AsDouble()), Target = ft, After = PlanEntry.Canon(ft), Show = show });
        }

        private static void SetWorkset(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var ws = ctx.Model.FindWorkset(op.Workset, "set_workset");
            Templates(op, c, "ws", (long)ws.Id.IntegerValue, ws.Name);
            foreach (var e in Editable(ctx, ids, c)) AddWorkset(ctx, e, ws, c);
        }

        private static void AddWorkset(BuildContext ctx, Element e, Workset ws, Collector c)
        {
            var id = Ids.ToLong(e.Id);
            var wp = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (wp == null || wp.IsReadOnly) { c.Skip("its workset cannot be changed (it follows the host or the view)", id); return; }
            c.Add(new PlanEntry { Id = id, Key = "ws", Before = WriteKit.WorksetName(ctx.Doc, e), Target = (long)ws.Id.IntegerValue, After = ws.Name });
        }

        private static void Delete(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            if (op.RefOps.Count > 0) throw new QueryException("delete: do not delete in the same plan what the plan creates (@n)");
            foreach (var e in Editable(ctx, ids, c, allowPinned: false, allowTypes: true))
                c.Add(new PlanEntry { Id = Ids.ToLong(e.Id), Key = "exists", Before = "1", After = "0" });
            if (c.Added > 0) c.Note("delete: Revit also deletes whatever depends on the element (its tags, for example)");
        }

        private static void SetTypeParam(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var types = new List<ElementType>();
            if (op.Type != null) types.Add(ctx.Model.FindType(op.Type, "set_type_param"));
            foreach (var id in ids.Distinct())
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                var t = e as ElementType ?? ctx.Doc.GetElement(e.GetTypeId()) as ElementType;
                if (t == null) { c.Skip("has no type", id); continue; }
                if (!types.Any(x => x.Id == t.Id)) types.Add(t);
            }
            foreach (var t in types)
            {
                var tid = Ids.ToLong(t.Id);
                var p = t.LookupParameter(op.Name);
                if (p == null) { c.Skip("the type does not have the parameter '" + op.Name + "' (is it an instance parameter? use set_param)", tid); continue; }
                if (p.IsReadOnly) { c.Skip("'" + op.Name + "' is read-only", tid); continue; }
                if (!WriteKit.TryTarget(ctx.Doc, p, op.Value, out var target, out var show, out var why))
                    throw new QueryException("set_type_param '" + op.Name + "': " + why);
                c.Add(new PlanEntry { Id = tid, Key = op.Name, Before = WriteKit.ParamValue(p), Target = target, After = PlanEntry.Canon(target), Show = show });
            }
            if (c.Added > 0) c.Note("set_type_param: changes the TYPE and therefore all of its instances in the model");
        }

        private static void SetLevel(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), "set_level");
            var keep = (Arg.GetString(op.Args, "keep") ?? "position").Trim().ToLowerInvariant();
            if (keep != "position" && keep != "position" && keep != "offset") throw new QueryException("set_level keep: position (default) or offset");
            var keepPosition = keep != "offset";
            foreach (var e in Editable(ctx, ids, c))
            {
                var id = Ids.ToLong(e.Id);
                var lp = WriteKit.LevelParam(e);
                if (lp == null || lp.IsReadOnly) { c.Skip("its level cannot be changed through the API (hosted or work-plane-based family)", id); continue; }
                var old = WriteKit.LevelOf(ctx.Doc, e);
                if (old != null && old.Id == lvl.Id) { c.Add(new PlanEntry { Id = id, Key = "lvl", Before = lvl.Name, After = lvl.Name }); continue; }
                c.Add(new PlanEntry { Id = id, Key = "lvl", Before = old?.Name ?? "", Target = Ids.ToLong(lvl.Id), After = lvl.Name });
                if (keepPosition && old != null)
                {
                    var ep = WriteKit.ElevParam(e);
                    if (ep != null && !ep.IsReadOnly)
                    {
                        var now = ep.AsDouble();
                        var target = now + (old.ProjectElevation - lvl.ProjectElevation);
                        c.Add(new PlanEntry { Id = id, Key = "elev", Before = PlanEntry.Canon(now), Target = target, After = PlanEntry.Canon(target), Show = Ft(target) + " (same actual elevation)" });
                    }
                }
            }
        }

        // =====================================================================================================
        // Snap to slab (with the geometry measured before planning)
        // =====================================================================================================

        private static void Snap(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var up = !string.Equals(Arg.GetString(op.Args, "side"), "down", StringComparison.OrdinalIgnoreCase);
            var clearance = OpCatalog.Length(op.Args, "clearance", "snap_to_surface") ?? 0.0;
            if (ctx.GeoErrors.TryGetValue(op.Index, out var geoError)) throw new QueryException("snap_to_surface: the geometry could not be measured: " + geoError);
            ctx.Geo.TryGetValue(op.Index, out var rows);
            var boxNote = 0;
            foreach (var e in Editable(ctx, ids, c))
            {
                var id = Ids.ToLong(e.Id);
                if (rows == null || !rows.TryGetValue(id, out var row)) { c.Skip("its geometry could not be measured", id); continue; }
                var cls = row.TryGetValue("class", out var k) ? k as string : null;
                if (cls == Engine.Geo.MountClass.NoSurface) { c.Skip("there is nothing " + (up ? "above" : "below") + " within the maximum distance (the elevation is not made up)", id); continue; }
                if (!(row.TryGetValue("move", out var mv) && mv is double mover) || double.IsNaN(mover)) { c.Skip("could not be measured (open mesh or no body)", id); continue; }
                if (row.ContainsKey("body")) boxNote++;
                var delta = mover - (up ? clearance : -clearance);
                var ep = WriteKit.ElevParam(e);
                if (ep == null || ep.IsReadOnly) { c.Skip("has no editable elevation", id); continue; }
                var now = ep.AsDouble();
                if (Math.Abs(delta) < 1e-6) { c.Add(new PlanEntry { Id = id, Key = "elev", Before = PlanEntry.Canon(now), After = PlanEntry.Canon(now) }); continue; }
                var target = now + delta;
                var show = Ft(target) + " (" + (delta > 0 ? "up " : "down ") + Lengths.Inches16(Math.Abs(delta)) + ", against " + (row.TryGetValue("against", out var ct) ? ct : "?") + ")";
                c.Add(new PlanEntry { Id = id, Key = "elev", Before = PlanEntry.Canon(now), Target = target, After = PlanEntry.Canon(target), Show = show });
            }
            if (boxNote > 0) c.Note("snap_to_surface: " + boxNote + " measured with their bounding box (no body mesh); review them");
        }

        // =====================================================================================================
        // Move, rotate, mirror, copy
        // =====================================================================================================

        private static XYZ Vector(OpSpec op, string kind)
        {
            var dx = OpCatalog.Length(op.Args, "dx", kind) ?? 0;
            var dy = OpCatalog.Length(op.Args, "dy", kind) ?? 0;
            var dz = OpCatalog.Length(op.Args, "dz", kind) ?? 0;
            return new XYZ(dx, dy, dz);
        }

        private static void Move(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            XYZ to = null;
            if (op.Args.TryGetValue("to", out var t) && t != null)
            {
                var p = OpCatalog.Point(t, "move to");
                to = new XYZ(p[0], p[1], p.Length > 2 ? p[2] : double.NaN);
            }
            var v = to == null ? Vector(op, "move") : null;
            if (v != null) Templates(op, c, "loc", WriteKit.List(v), "+(" + WriteKit.Xyz(v) + ")");
            foreach (var e in Editable(ctx, ids, c, allowPinned: false))
            {
                var id = Ids.ToLong(e.Id);
                var loc = WriteKit.LocationOf(e);
                if (loc == null) { c.Skip("has no location", id); continue; }
                var d = v ?? new XYZ(to.X - loc.X, to.Y - loc.Y, double.IsNaN(to.Z) ? 0 : to.Z - loc.Z);
                c.Add(new PlanEntry { Id = id, Key = "loc", Before = WriteKit.Xyz(loc), Target = WriteKit.List(d), After = WriteKit.Xyz(loc + d) });
            }
        }

        private static void Rotate(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var deg = OpCatalog.Number(op.Args, "deg", "rotate") ?? 0;
            double[] center = null;
            if (op.Args.TryGetValue("center", out var ce) && ce is List<object>) center = OpCatalog.Point(ce, "rotate center", false);
            var target = new Dictionary<string, object> { ["deg"] = deg };
            if (center != null) { target["cx"] = center[0]; target["cy"] = center[1]; }
            Templates(op, c, "rot", target, deg.ToString(CultureInfo.InvariantCulture) + "°");
            var rad = deg * Math.PI / 180.0;
            foreach (var e in Editable(ctx, ids, c, allowPinned: false))
            {
                var id = Ids.ToLong(e.Id);
                var loc = WriteKit.LocationOf(e);
                if (loc == null) { c.Skip("has no location", id); continue; }
                var rot = WriteKit.RotationDeg(e);
                var newLoc = loc;
                if (center != null)
                {
                    double x = loc.X - center[0], y = loc.Y - center[1];
                    newLoc = new XYZ(center[0] + x * Math.Cos(rad) - y * Math.Sin(rad), center[1] + x * Math.Sin(rad) + y * Math.Cos(rad), loc.Z);
                }
                var before = WriteKit.Xyz(loc) + "|" + (rot.HasValue ? WriteKit.Num(rot.Value) : "");
                var after = WriteKit.Xyz(newLoc) + "|" + (rot.HasValue ? WriteKit.Num(WriteKit.Norm360(rot.Value + deg)) : "");
                c.Add(new PlanEntry { Id = id, Key = "rot", Before = before, Target = target, After = after, Show = (rot.HasValue ? WriteKit.Norm360(rot.Value + deg) + "°" : "+" + deg + "°") });
            }
        }

        private static void Mirror(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var axisPoints = OpCatalog.Points(op.Args["axis"], "mirror axis", 2, 2, false);
            var copy = Arg.GetBool(op.Args, "copy") ?? false;
            var axis = WriteKit.List(axisPoints[0][0], axisPoints[0][1], axisPoints[1][0], axisPoints[1][1]);
            if (!copy) Templates(op, c, "loc", new Dictionary<string, object> { ["axis"] = axis }, "mirror");
            else Templates(op, c, "new", new Dictionary<string, object> { ["axis"] = axis }, "mirrored copy of each created element");
            foreach (var e in Editable(ctx, ids, c, allowPinned: copy))
            {
                var id = Ids.ToLong(e.Id);
                var loc = WriteKit.LocationOf(e);
                if (loc == null) { c.Skip("has no location", id); continue; }
                var m = Reflect(loc, axisPoints[0][0], axisPoints[0][1], axisPoints[1][0], axisPoints[1][1]);
                if (copy)
                    c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = "mirrored copy of " + id, Show = "mirrored copy of " + id + " at " + WriteKit.Xyz(m), Target = new Dictionary<string, object> { ["src"] = id, ["axis"] = axis } });
                else
                    c.Add(new PlanEntry { Id = id, Key = "loc", Before = WriteKit.Xyz(loc), Target = new Dictionary<string, object> { ["axis"] = axis }, After = WriteKit.Xyz(m) });
            }
        }

        public static XYZ Reflect(XYZ p, double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            var len2 = dx * dx + dy * dy;
            var t = ((p.X - x1) * dx + (p.Y - y1) * dy) / len2;
            double fx = x1 + t * dx, fy = y1 + t * dy;
            return new XYZ(2 * fx - p.X, 2 * fy - p.Y, p.Z);
        }

        private static void Copy(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            Level lvl = null;
            if (op.Args.ContainsKey("level")) lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), "copy");
            var v = lvl == null ? Vector(op, "copy") : null;
            var hostedNote = 0;
            var tplTarget = new Dictionary<string, object>();
            if (lvl != null) tplTarget["level"] = Ids.ToLong(lvl.Id); else tplTarget["v"] = WriteKit.List(v);
            Templates(op, c, "new", tplTarget, lvl != null ? "copy of each created element on " + lvl.Name : "copy of each created element offset by " + WriteKit.Xyz(v));
            foreach (var e in Editable(ctx, ids, c, allowPinned: true))
            {
                var id = Ids.ToLong(e.Id);
                var t = new Dictionary<string, object> { ["src"] = id };
                string show;
                if (lvl != null)
                {
                    var old = WriteKit.LevelOf(ctx.Doc, e);
                    if (old == null) { c.Skip("has no level", id); continue; }
                    if (old.Id == lvl.Id) { c.Skip("already on " + lvl.Name, id); continue; }
                    t["level"] = Ids.ToLong(lvl.Id);
                    t["dz"] = lvl.ProjectElevation - old.ProjectElevation;
                    var lp = WriteKit.LevelParam(e);
                    if (lp == null || lp.IsReadOnly) hostedNote++;
                    show = "copy of " + id + " on " + lvl.Name + " (same plan position and height above its level)";
                }
                else
                {
                    t["v"] = WriteKit.List(v);
                    show = "copy of " + id + " offset by " + WriteKit.Xyz(v);
                }
                c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
            }
            if (hostedNote > 0) c.Note("copy: " + hostedNote + " copies cannot change level (hosted family): they are raised by the level difference but stay associated with the original level");
        }

        // =====================================================================================================
        // Place families
        // =====================================================================================================

        private static void Place(BuildContext ctx, OpSpec op, Collector c)
        {
            var host = Arg.GetString(op.Args, "host");
            if (!string.IsNullOrWhiteSpace(host))
                throw new QueryException("place: a physical host '" + host + "' is required. This operation only places by level; specify a compatible family and rule.");
            var sourceBound = !string.IsNullOrEmpty(Arg.GetString(op.Args, SourceBinding.OpField));
            var sym = ctx.Model.FindType(Arg.GetString(op.Args, "type"), "place", t => t is FamilySymbol, "loaded families") as FamilySymbol;
            var pt = sym.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid;
            if (sourceBound && pt != FamilyPlacementType.OneLevelBased)
                throw new QueryException("registered-source place: the family " + sym.FamilyName + " is " + pt + "; only OneLevelBased is allowed.");
            if (pt != FamilyPlacementType.OneLevelBased && pt != FamilyPlacementType.WorkPlaneBased)
                throw new QueryException("place: " + sym.FamilyName + " is a " + pt + " family (it needs a host: wall, face or ceiling). Copy an existing instance with copy");
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), "place");
            var points = OpCatalog.Points(op.Args["points"], "place points", 1, 5000);
            var sourceItems = op.Args.TryGetValue("source_items", out var rawItems) ? rawItems as List<object> : null;
            var elev = OpCatalog.Length(op.Args, "elev", "place");
            var mounting = Arg.GetString(op.Args, "mounting");
            var clearance = OpCatalog.Length(op.Args, "clearance", "place") ?? 0.0;
            var rot = OpCatalog.Number(op.Args, "rot", "place");
            Workset ws = null;
            if (op.Args.ContainsKey("ws")) ws = ctx.Model.FindWorkset(Arg.GetString(op.Args, "ws"), "place");
            var prms = op.Args.TryGetValue("params", out var pr) ? pr as Dictionary<string, object> : null;

            // Validate params against another instance of the same type (if any): name, read-only and value.
            var converted = new Dictionary<string, object>();
            if (prms != null && prms.Count > 0)
            {
                var sample = new FilteredElementCollector(ctx.Doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                    .Cast<FamilyInstance>().FirstOrDefault(f => f.Symbol?.Id == sym.Id);
                foreach (var kv in prms)
                {
                    if (sample == null) { converted[kv.Key] = new Dictionary<string, object> { ["raw"] = kv.Value }; continue; }
                    var p = sample.LookupParameter(kv.Key);
                    if (p == null) throw new QueryException("place params: the type does not have the instance parameter '" + kv.Key + "'");
                    if (p.IsReadOnly) throw new QueryException("place params: '" + kv.Key + "' is read-only");
                    if (!WriteKit.TryTarget(ctx.Doc, p, kv.Value, out var tv, out _, out var why)) throw new QueryException("place params '" + kv.Key + "': " + why);
                    converted[kv.Key] = tv;
                }
                if (sample == null) c.Note("place: there is no other instance of that type to validate params against; they are validated on apply");
            }

            Dictionary<long, Dictionary<string, object>> geo = null;
            var up = !string.Equals(mounting, "floor", StringComparison.OrdinalIgnoreCase);
            if (mounting != null && ctx.GeoErrors.TryGetValue(op.Index, out var geoError)) throw new QueryException(geoError.StartsWith("place", StringComparison.Ordinal) ? geoError : "place mounting: " + geoError);
            if (mounting != null && !ctx.Geo.TryGetValue(op.Index, out geo))
                throw new QueryException("place mounting: the type's body could not be measured (there are no instances of " + sym.FamilyName + ": " + sym.Name + " in the model). Give elev");
            ctx.GeoNominal.TryGetValue(op.Index, out var nominal);

            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                double? offset = elev;
                string how = elev.HasValue ? "elev " + Ft(elev.Value) : "default elev";
                if (geo != null)
                {
                    if (!geo.TryGetValue(i, out var row) || !(row.TryGetValue("move", out var mv) && mv is double mover) || double.IsNaN(mover))
                    {
                        if (sourceBound) throw new QueryException("registered-source place: there is no measurable support at point " + (i + 1));
                        c.Skip("there is no measurable " + (up ? "slab above" : "floor below") + " at that point (the elevation is not made up)", i + 1);
                        continue;
                    }
                    offset = nominal + mover - (up ? clearance : -clearance);
                    how = "flush with " + (row.TryGetValue("against", out var ct) ? ct : "the surface") + " (elev " + Ft(offset.Value) + ")";
                }
                var t = new Dictionary<string, object>
                {
                    ["sym"] = Ids.ToLong(sym.Id), ["lvl"] = Ids.ToLong(lvl.Id), ["x"] = p[0], ["y"] = p[1],
                };
                if (offset.HasValue) t["elev"] = offset.Value;
                if (sourceBound) t["z_check"] = lvl.ProjectElevation + (offset ?? 0);
                if (sourceItems != null) t["source"] = sourceItems[i];
                if (rot.HasValue && Math.Abs(rot.Value) > 1e-9) t["rot"] = rot.Value;
                if (ws != null) t["ws"] = (long)ws.Id.IntegerValue;
                if (converted.Count > 0) t["params"] = converted;
                var show = sym.FamilyName + ": " + sym.Name + " at (" + Math.Round(p[0], 3) + ", " + Math.Round(p[1], 3) + ") " + lvl.Name + ", " + how;
                c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
            }
        }

        // =====================================================================================================
        // Tags
        // =====================================================================================================

        /// <summary>Tag category matching an element's category (LightingFixtures → LightingFixtureTags).</summary>
        public static BuiltInCategory TagCategoryFor(Category cat)
        {
            if (cat == null) return BuiltInCategory.INVALID;
            var bic = CatBic(cat);
            var name = bic.ToString();
            var stem = name.EndsWith("s", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name;
            if (Enum.TryParse(stem + "Tags", out BuiltInCategory tagBic)) return tagBic;
            if (Enum.TryParse(name + "Tags", out tagBic)) return tagBic;
            return BuiltInCategory.INVALID;
        }

        public static BuiltInCategory CatBic(Category cat)
        {
#if REVIT2023_OR_GREATER
            return cat.BuiltInCategory;
#else
            return (BuiltInCategory)cat.Id.IntegerValue;
#endif
        }

        private static void Tag(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var viewRef = KeyRef(op, "view");
            View view = viewRef > 0 ? null : ctx.Model.FindView(op.Args["view"], "tag", allowSheet: false);
            if (view is ViewSheet) throw new QueryException("tag: the view is a sheet; tag in the model view");
            if (view != null && view.IsTemplate) throw new QueryException("tag: '" + view.Name + "' is a view template");

            ElementType forcedType = null;
            var tt = Arg.GetString(op.Args, "tag_type");
            if (tt != null) forcedType = ctx.Model.FindType(tt, "tag", x => x.Category != null && CatBic(x.Category).ToString().EndsWith("Tags", StringComparison.Ordinal), "tag types");
            var leaderArg = Arg.GetBool(op.Args, "leader");
            var orientArg = Arg.GetString(op.Args, "orientation");
            double[] offsetPaper = op.Args.TryGetValue("offset", out var off) && off != null ? OpCatalog.Point(off, "tag offset", false) : null;
            var scale = view != null ? Math.Max(1, view.Scale) : 96;

            // Tags on what this same plan creates (place/copy @n): the style is decided on apply, using the tags present then.
            var tpl = new Dictionary<string, object> { ["ttype"] = forcedType != null ? Ids.ToLong(forcedType.Id) : -1L };
            if (view != null) tpl["view"] = Ids.ToLong(view.Id); else tpl["view_ref"] = (long)viewRef;
            if (leaderArg.HasValue) tpl["leader"] = leaderArg.Value;
            if (orientArg != null) tpl["orientation"] = orientArg;
            if (offsetPaper != null) tpl["offset"] = WriteKit.List(offsetPaper[0], offsetPaper[1]);
            foreach (var r in op.RefOps.Distinct())
            {
                if (!IdsRef(op, r)) continue;
                c.Add(new PlanEntry { Id = 0, Ref = r, Key = "tag", Before = "", After = "tag", Show = "one tag for each element created by @" + r + " in " + (view?.Name ?? "@" + viewRef), Target = tpl });
            }

            var kit = TagKit.For(ctx.Doc, view);
            var notes = new HashSet<string>();
            foreach (var e in Editable(ctx, ids, c, allowGroup: true))
            {
                var id = Ids.ToLong(e.Id);
                if (kit.Tagged.Contains(id)) { c.Skip("already tagged in that view (one tag per element)", id); continue; }
                if (!kit.TryChoose(e, forcedType, offsetPaper, leaderArg, orientArg, scale, out var ch, out var why)) { c.Skip(why, id); continue; }
                if (ch.Note != null) notes.Add(ch.Note);
                kit.Remember(id, TagKit.TagCategoryId(e), null); // the same id twice in the list = a single tag
                var t = new Dictionary<string, object>
                {
                    ["elem"] = id, ["ttype"] = ch.TypeId, ["hx"] = ch.Head.X, ["hy"] = ch.Head.Y, ["hz"] = ch.Head.Z, ["leader"] = ch.Leader, ["vertical"] = ch.Vertical,
                };
                if (view != null) t["view"] = Ids.ToLong(view.Id); else t["view_ref"] = (long)viewRef;
                var show = "tag " + TagKit.TypeLabel(ctx.Doc, ch.TypeId) + " for " + id + " in " + (view?.Name ?? "@" + viewRef);
                c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
            }
            foreach (var n in notes) c.Note("tag: " + n);
        }

        public static string HeadCanon(IndependentTag t)
        {
            XYZ head = null;
            bool leader = false, vertical = false;
            try { head = t.TagHeadPosition; leader = t.HasLeader; vertical = t.TagOrientation == TagOrientation.Vertical; } catch (Exception) { }
            return WriteKit.Xyz(head) + "|" + (leader ? "L" : "-") + "|" + (vertical ? "V" : "H");
        }

        private static void TagMove(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            double[] head = op.Args.TryGetValue("head", out var h) && h != null ? OpCatalog.Point(h, "tag_move head", false) : null;
            var dx = OpCatalog.Length(op.Args, "dx", "tag_move") ?? 0;
            var dy = OpCatalog.Length(op.Args, "dy", "tag_move") ?? 0;
            var dxp = OpCatalog.Number(op.Args, "dxp", "tag_move") ?? 0;
            var dyp = OpCatalog.Number(op.Args, "dyp", "tag_move") ?? 0;
            var leaderArg = Arg.GetBool(op.Args, "leader");
            var orientArg = Arg.GetString(op.Args, "orientation");
            double[] end = op.Args.TryGetValue("end", out var ex) && ex != null ? OpCatalog.Point(ex, "tag_move end", false) : null;
            double[] elbow = op.Args.TryGetValue("elbow", out var co) && co != null ? OpCatalog.Point(co, "tag_move elbow", false) : null;
            foreach (var e in Editable(ctx, ids, c, allowGroup: true))
            {
                var id = Ids.ToLong(e.Id);
                if (!(e is IndependentTag t)) { c.Skip("is not a tag", id); continue; }
                XYZ now;
                bool leader, vertical;
                try { now = t.TagHeadPosition; leader = t.HasLeader; vertical = t.TagOrientation == TagOrientation.Vertical; }
                catch (Exception) { c.Skip("Revit does not report its head position", id); continue; }
                var scale = (ctx.Doc.GetElement(t.OwnerViewId) as View)?.Scale ?? 96;
                var nx = head != null ? head[0] : now.X + dx + dxp / 12.0 * scale;
                var ny = head != null ? head[1] : now.Y + dy + dyp / 12.0 * scale;
                var newLeader = leaderArg ?? leader;
                var newVertical = orientArg != null ? orientArg == "vertical" : vertical;
                var target = new Dictionary<string, object> { ["hx"] = nx, ["hy"] = ny, ["hz"] = now.Z, ["leader"] = newLeader, ["vertical"] = newVertical };
                if (end != null) target["ex"] = WriteKit.List(end[0], end[1]);
                if (elbow != null) target["co"] = WriteKit.List(elbow[0], elbow[1]);
                if ((end != null || elbow != null) && !newLeader) c.Note("tag_move: end/elbow only apply with leader=true");
                var after = WriteKit.Xyz(new XYZ(nx, ny, now.Z)) + "|" + (newLeader ? "L" : "-") + "|" + (newVertical ? "V" : "H");
                var entry = new PlanEntry { Id = id, Key = "head", Before = HeadCanon(t), Target = target, After = after };
                if (end != null || elbow != null) entry.Show = after + " (+end/elbow)";
                if (entry.Before == entry.After && (end != null || elbow != null)) entry.After += "*"; // forces extremo/codo to be applied
                c.Add(entry);
            }
        }

        // =====================================================================================================
        // Groups
        // =====================================================================================================

        private static void GroupCreate(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var name = Arg.GetString(op.Args, "name");
            if (name != null)
            {
                if (ctx.Model.Types.Any(t => t is GroupType && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) || !ctx.NewNames.Add("group:" + name))
                    throw new QueryException("group_create: a group type named '" + name + "' already exists");
            }
            var members = Editable(ctx, ids, c, allowPinned: true).Select(e => Ids.ToLong(e.Id)).ToList();
            var refs = op.RefOps.Distinct().Where(r => IdsRef(op, r)).ToList();
            if (members.Count == 0 && refs.Count == 0) throw new QueryException("group_create: no element can be grouped");
            var t = new Dictionary<string, object> { ["ids"] = members.Cast<object>().ToList() };
            if (refs.Count > 0) t["refs"] = refs.Select(r => (object)(long)r).ToList();
            if (name != null) t["name"] = name;
            var show = "group " + (name ?? "(automatic name)") + " with " + members.Count + " elements" + (refs.Count > 0 ? " + what was created by " + string.Join(", ", refs.Select(r => "@" + r)) : "");
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void Ungroup(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            foreach (var e in Editable(ctx, ids, c, allowPinned: false))
            {
                var id = Ids.ToLong(e.Id);
                if (!(e is Group)) { c.Skip("is not a group", id); continue; }
                c.Add(new PlanEntry { Id = id, Key = "exists", Before = "1", After = "0", Show = "ungrouped (the members are left loose)" });
            }
        }

        private static void GroupPlace(BuildContext ctx, OpSpec op, Collector c)
        {
            var gref = KeyRef(op, "type");
            GroupType gt = null;
            if (gref == 0) gt = ctx.Model.FindType(Arg.GetString(op.Args, "type"), "group_place", t => t is GroupType, "group types") as GroupType;
            else if (!ctx.Ops.TryGetValue(gref, out var src) || src.Kind != "group_create")
                throw new QueryException("group_place type: @" + gref + " must be a group_create of this plan");
            var level = Arg.GetString(op.Args, "level") is string levelName ? ctx.Model.FindLevel(levelName, op.Kind) : null;
            var workset = Arg.GetString(op.Args, "ws") is string worksetName ? ctx.Model.FindWorkset(worksetName, op.Kind) : null;
            var rotation = OpCatalog.Number(op.Args, "rot", op.Kind);
            var reuse = Arg.GetBool(op.Args, "reuse") == true;
            Group prototype = null;
            string signature = null;
            if (Arg.GetLong(op.Args, "prototype") is long sourceId)
            {
                prototype = ctx.Doc.GetElement(Ids.ToId(sourceId)) as Group;
                if (prototype == null || gt == null || prototype.GetTypeId() != gt.Id)
                    throw new QueryException("group_place: the prototype must exist and match the type of this variant");
                signature = GroupPlacement.Signature(prototype);
            }
            var points = OpCatalog.Points(op.Args["points"], "group_place points", 1, 200);
            foreach (var p in points)
            {
                var t = new Dictionary<string, object>
                {
                    ["x"] = p[0], ["y"] = p[1], ["z"] = (p.Length > 2 ? p[2] : 0.0) + (level?.ProjectElevation ?? 0),
                };
                if (gt != null) t["gtype"] = Ids.ToLong(gt.Id); else t["gref"] = (long)gref;
                if (level != null) t["level"] = Ids.ToLong(level.Id);
                if (rotation.HasValue) t["rot"] = rotation.Value;
                if (workset != null) t["ws"] = (long)workset.Id.IntegerValue;
                if (signature != null) { t["member_signature"] = signature; t["prototype"] = Ids.ToLong(prototype.Id); }
                if (reuse)
                {
                    t["reuse"] = true;
                    if (gt != null)
                    {
                        var matches = GroupPlacement.AtPoint(ctx.Doc, gt.Id, GroupPlacement.Point(t));
                        if (matches.Count > 1) throw new QueryException("there is more than one group of this type at the point");
                        if (matches.Count == 1)
                        {
                            var found = GroupPlacement.Difference(matches[0], t);
                            if (found != null) throw new QueryException("the existing group differs in " + found);
                            c.Note("keeping the verified group " + Ids.ToLong(matches[0].Id));
                            continue;
                        }
                    }
                }
                var show = "group " + (gt?.Name ?? "@" + gref) + " at (" + Math.Round(p[0], 3) + ", " + Math.Round(p[1], 3) + (p.Length > 2 ? ", " + Math.Round(p[2], 3) : "") + ")";
                if (level != null) show += " relative to " + level.Name;
                c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
            }
        }

        // =====================================================================================================
        // Views
        // =====================================================================================================

        private static ViewFamilyType Vft(BuildContext ctx, ViewFamily fam, string name, string op)
        {
            var all = ctx.Model.Types.OfType<ViewFamilyType>().Where(v => v.ViewFamily == fam).ToList();
            if (all.Count == 0) throw new QueryException(op + ": the model has no view types " + fam);
            if (name == null) return all[0];
            var hit = all.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new QueryException(op + ": the view type '" + name + "' (" + fam + ") does not exist: " + string.Join(", ", all.Select(v => v.Name)));
            return hit;
        }

        private static void ViewCreate(BuildContext ctx, OpSpec op, Collector c)
        {
            var kind = Arg.GetString(op.Args, "type").Trim().ToLowerInvariant();
            var name = Arg.GetString(op.Args, "name");
            var vtName = Arg.GetString(op.Args, "view_type");
            var vtype = ModelIndex.ViewTypeFor(kind);
            if (kind == "duplicate" && KeyRef(op, "source") == 0) vtype = ctx.Model.FindView(op.Args["source"], "view_create", allowSheet: false).ViewType;
            if (name != null && (ctx.Model.ViewNameTaken(name, vtype) || !ctx.NewNames.Add("view:" + kind + ":" + name)))
                throw new QueryException("view_create: a view named '" + name + "' already exists");
            var t = new Dictionary<string, object> { ["type"] = kind };
            if (name != null) t["name"] = name;
            var tpl = Arg.GetString(op.Args, "template");
            if (tpl != null) t["template"] = Ids.ToLong(ctx.Model.FindTemplate(tpl, "view_create").Id);
            string show;
            switch (kind)
            {
                case "plan":
                case "rcp":
                {
                    var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), "view_create");
                    var vft = Vft(ctx, kind == "rcp" ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan, vtName, "view_create");
                    t["level"] = Ids.ToLong(lvl.Id); t["vft"] = Ids.ToLong(vft.Id);
                    show = (kind == "rcp" ? "ceiling plan" : "floor plan") + " of " + lvl.Name;
                    break;
                }
                case "section":
                {
                    var box = OpCatalog.Points(op.Args["box"], "view_create box", 2, 2);
                    var depth = OpCatalog.Length(op.Args, "depth", "view_create") ?? 6.0;
                    if (Math.Abs(box[0][0] - box[1][0]) + Math.Abs(box[0][1] - box[1][1]) < 0.01) throw new QueryException("view_create section: the section line (x0,y0)→(x1,y1) cannot be a point");
                    var vft = Vft(ctx, ViewFamily.Section, vtName, "view_create");
                    t["vft"] = Ids.ToLong(vft.Id);
                    t["box"] = new List<object> { WriteKit.List(box[0][0], box[0][1], box[0].Length > 2 ? box[0][2] : 0), WriteKit.List(box[1][0], box[1][1], box[1].Length > 2 ? box[1][2] : 10) };
                    t["prof"] = depth;
                    show = "section from (" + Math.Round(box[0][0], 2) + ", " + Math.Round(box[0][1], 2) + ") to (" + Math.Round(box[1][0], 2) + ", " + Math.Round(box[1][1], 2) + "), depth " + Ft(depth);
                    break;
                }
                case "callout":
                {
                    var parentRef = KeyRef(op, "source");
                    if (parentRef > 0) throw new QueryException("view_create callout: the parent must already exist (not @n)");
                    var parent = ctx.Model.FindView(op.Args["source"], "view_create", allowSheet: false);
                    var box = OpCatalog.Points(op.Args["box"], "view_create box", 2, 2);
                    ViewFamily fam;
                    var pv = ctx.Model.Types.OfType<ViewFamilyType>().FirstOrDefault(v => v.Id == parent.GetTypeId());
                    fam = pv?.ViewFamily ?? ViewFamily.FloorPlan;
                    var vft = Vft(ctx, fam, vtName, "view_create");
                    t["origin"] = Ids.ToLong(parent.Id); t["vft"] = Ids.ToLong(vft.Id);
                    t["box"] = new List<object> { WriteKit.List(box[0][0], box[0][1], 0), WriteKit.List(box[1][0], box[1][1], 0) };
                    show = "callout in " + parent.Name;
                    break;
                }
                case "drafting":
                {
                    var vft = Vft(ctx, ViewFamily.Drafting, vtName, "view_create");
                    t["vft"] = Ids.ToLong(vft.Id);
                    var esc = OpCatalog.Number(op.Args, "scale", "view_create");
                    if (esc.HasValue) { if (esc < 1 || esc > 24000) throw new QueryException("view_create scale: 1–24000"); t["scale"] = (long)esc.Value; }
                    show = "drafting view" + (esc.HasValue ? " 1:" + esc.Value : "");
                    break;
                }
                case "3d":
                {
                    var vft = Vft(ctx, ViewFamily.ThreeDimensional, vtName, "view_create");
                    t["vft"] = Ids.ToLong(vft.Id);
                    if (op.Args.ContainsKey("box"))
                    {
                        var box = OpCatalog.Points(op.Args["box"], "view_create box", 2, 2);
                        t["box"] = new List<object> { WriteKit.List(box[0][0], box[0][1], box[0].Length > 2 ? box[0][2] : -1e3), WriteKit.List(box[1][0], box[1][1], box[1].Length > 2 ? box[1][2] : 1e3) };
                    }
                    show = "3D view" + (op.Args.ContainsKey("box") ? " with section box" : "");
                    break;
                }
                case "duplicate":
                {
                    var srcRef = KeyRef(op, "source");
                    if (srcRef > 0) throw new QueryException("view_create duplicate: the source must already exist (not @n)");
                    var src = ctx.Model.FindView(op.Args["source"], "view_create", allowSheet: false);
                    var opt = (Arg.GetString(op.Args, "option") ?? "duplicate").ToLowerInvariant();
                    var dupOpt = opt == "with_detailing" ? ViewDuplicateOption.WithDetailing : opt == "dependent" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.Duplicate;
                    if (!src.CanViewBeDuplicated(dupOpt)) throw new QueryException("view_create duplicate: Revit does not allow duplicating '" + src.Name + "' as " + opt);
                    t["origin"] = Ids.ToLong(src.Id); t["option"] = opt;
                    show = "copy (" + opt + ") of " + src.Name;
                    break;
                }
                default: throw new QueryException("view_create: unknown type");
            }
            if (name != null) show += " named '" + name + "'";
            if (tpl != null) show += " with template " + tpl;
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        /// <summary>Does the view template control that parameter? (then it cannot be changed in the view).</summary>
        public static bool ControlledByTemplate(Document doc, View v, BuiltInParameter bip, out string template)
        {
            template = null;
            try
            {
                var tid = v.ViewTemplateId;
                if (tid == null || tid == ElementId.InvalidElementId) return false;
                var t = doc.GetElement(tid) as View;
                if (t == null) return false;
                template = t.Name;
                var pid = new ElementId(bip);
                return t.GetTemplateParameterIds().Contains(pid) && !t.GetNonControlledTemplateParameterIds().Contains(pid);
            }
            catch (Exception) { return false; }
        }

        public static string RangeCanon(Document doc, ViewPlan v)
        {
            try
            {
                var r = v.GetViewRange();
                var parts = new List<string>();
                foreach (var kv in new[] { ("top", PlanViewPlane.TopClipPlane), ("cut", PlanViewPlane.CutPlane), ("bottom", PlanViewPlane.BottomClipPlane), ("depth", PlanViewPlane.ViewDepthPlane) })
                {
                    var lid = r.GetLevelId(kv.Item2);
                    string lname;
                    if (lid == PlanViewRange.Unlimited) lname = "unlimited";
                    else if (lid == PlanViewRange.Current) lname = "current";
                    else if (lid == PlanViewRange.LevelAbove) lname = "up";
                    else if (lid == PlanViewRange.LevelBelow) lname = "down";
                    else lname = doc.GetElement(lid)?.Name ?? "?";
                    parts.Add(kv.Item1 + "=" + lname + (lname == "unlimited" ? "" : "+" + WriteKit.Num(r.GetOffset(kv.Item2))));
                }
                return string.Join("|", parts);
            }
            catch (Exception) { return ""; }
        }

        private static void ViewSet(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var a = op.Args;
            // Values already resolved (common to all views).
            View tpl = null; var noTpl = false;
            if (a.ContainsKey("template"))
            {
                var n = Arg.GetString(a, "template");
                if (n == null || n.Equals("none", StringComparison.OrdinalIgnoreCase) || LegacyAliases.IsLegacyNone(n)) noTpl = true;
                else tpl = ctx.Model.FindTemplate(n, "view_set");
            }
            Dictionary<string, object> range = null;
            if (a.TryGetValue("range", out var rg) && rg is Dictionary<string, object> rd)
            {
                range = new Dictionary<string, object>();
                foreach (var kv in rd)
                {
                    var pair = (List<object>)kv.Value;
                    var lname = Convert.ToString(pair[0], CultureInfo.InvariantCulture).Trim();
                    Lengths.TryFeet(pair[1], out var off, out _);
                    long lid;
                    switch (LegacyAliases.Token(lname.ToLowerInvariant()))   // 0.9.4 actual/arriba/abajo/ilimitado; a level name is used as given
                    {
                        case "current": lid = -101; break;
                        case "up": lid = -102; break;
                        case "down": lid = -103; break;
                        case "unlimited": lid = -104; break;
                        default: lid = Ids.ToLong(ctx.Model.FindLevel(lname, "view_set range").Id); break;
                    }
                    range[kv.Key] = WriteKit.List(lid, off);
                }
            }
            long? scale = null;
            if (a.ContainsKey("scale"))
            {
                var sc = OpCatalog.Number(a, "scale", "view_set") ?? 0;
                if (sc < 1 || sc > 24000 || Math.Abs(sc - Math.Round(sc)) > 1e-9) throw new QueryException("view_set scale: integer (96 = 1/8\" = 1'-0\")");
                scale = (long)Math.Round(sc);
            }
            string detail = Arg.GetString(a, "detail");
            string newName = Arg.GetString(a, "name");
            bool? crop = Arg.GetBool(a, "crop");
            Element scope = null; var noScope = false;
            if (a.ContainsKey("scope_box"))
            {
                var n = Arg.GetString(a, "scope_box");
                if (n == null || n.Equals("none", StringComparison.OrdinalIgnoreCase) || LegacyAliases.IsLegacyNone(n)) noScope = true;
                else
                {
                    var hits = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                        .Where(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count != 1) throw new QueryException("view_set scope_box: " + (hits.Count == 0 ? "'" + n + "' does not exist" : "'" + n + "' is ambiguous"));
                    scope = hits[0];
                }
            }
            List<double[]> secBox = a.ContainsKey("section_box") ? OpCatalog.Points(a["section_box"], "view_set section_box", 2, 2) : null;

            // Templates on views this plan creates (@n).
            if (tpl != null || noTpl) Templates(op, c, "template", tpl != null ? Ids.ToLong(tpl.Id) : -1L, tpl?.Name ?? "none");
            if (range != null) Templates(op, c, "range", range, "view range");
            if (scale.HasValue) Templates(op, c, "scale", scale.Value, scale.Value.ToString(CultureInfo.InvariantCulture));
            if (detail != null) Templates(op, c, "detail", detail, detail);
            if (newName != null && op.RefOps.Count > 0) throw new QueryException("view_set name: set it in view_create (name) for new views");
            if (crop.HasValue) Templates(op, c, "crop", crop.Value, crop.Value ? "yes" : "no");
            if (scope != null || noScope) Templates(op, c, "scope_box", scope != null ? Ids.ToLong(scope.Id) : -1L, scope?.Name ?? "none");
            if (secBox != null) Templates(op, c, "section_box", new List<object> { WriteKit.List(secBox[0]), WriteKit.List(secBox[1]) }, "section box");

            foreach (var id in ids.Distinct())
            {
                var v = ctx.Doc.GetElement(Ids.ToId(id)) as View;
                if (v == null) { c.Skip("is not a view", id); continue; }
                if (v.IsTemplate && (tpl != null || noTpl)) { c.Skip("it is a template", id); continue; }
                var why = WriteKit.WhyNotEditable(ctx.Doc, v);
                if (why != null) { c.Skip(why, id); continue; }
                string tname;
                if (tpl != null || noTpl)
                {
                    var cur = ctx.Doc.GetElement(v.ViewTemplateId) as View;
                    c.Add(new PlanEntry { Id = id, Key = "template", Before = cur?.Name ?? "none", Target = tpl != null ? Ids.ToLong(tpl.Id) : -1L, After = tpl?.Name ?? "none" });
                }
                if (range != null)
                {
                    if (!(v is ViewPlan vp)) c.Skip("view range only exists in floor plans and ceiling plans", id);
                    else if (tpl == null && ControlledByTemplate(ctx.Doc, v, BuiltInParameter.PLAN_VIEW_RANGE, out tname)) c.Skip("its template (" + tname + ") controls the view range", id);
                    else c.Add(new PlanEntry { Id = id, Key = "range", Before = RangeCanon(ctx.Doc, vp), Target = range, After = PredictRange(ctx.Doc, vp, range) });
                }
                if (scale.HasValue)
                {
                    if (tpl == null && ControlledByTemplate(ctx.Doc, v, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL, out tname)) c.Skip("its template (" + tname + ") controls the scale", id);
                    else c.Add(new PlanEntry { Id = id, Key = "scale", Before = PlanEntry.Canon((long)v.Scale), Target = scale.Value, After = PlanEntry.Canon(scale.Value) });
                }
                if (detail != null)
                {
                    if (tpl == null && ControlledByTemplate(ctx.Doc, v, BuiltInParameter.VIEW_DETAIL_LEVEL, out tname)) c.Skip("its template (" + tname + ") controls the detail level", id);
                    else c.Add(new PlanEntry { Id = id, Key = "detail", Before = DetailName(v.DetailLevel), Target = detail, After = detail });
                }
                if (newName != null)
                {
                    if (!string.Equals(v.Name, newName, StringComparison.Ordinal) && (ctx.Model.ViewNameTaken(newName, v.ViewType) || !ctx.NewNames.Add("view:" + v.ViewType + ":" + newName)))
                        throw new QueryException("view_set name: a view named '" + newName + "' already exists");
                    c.Add(new PlanEntry { Id = id, Key = "name", Before = v.Name, Target = newName, After = newName });
                }
                if (crop.HasValue)
                    c.Add(new PlanEntry { Id = id, Key = "crop", Before = v.CropBoxActive ? "1" : "0", Target = crop.Value, After = crop.Value ? "1" : "0" });
                if (scope != null || noScope)
                {
                    var sp = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    if (sp == null || sp.IsReadOnly) c.Skip("that view does not support a scope box", id);
                    else
                    {
                        var cur = ctx.Doc.GetElement(sp.AsElementId());
                        c.Add(new PlanEntry { Id = id, Key = "scope_box", Before = cur?.Name ?? "none", Target = scope != null ? Ids.ToLong(scope.Id) : -1L, After = scope?.Name ?? "none" });
                    }
                }
                if (secBox != null)
                {
                    if (!(v is View3D v3)) c.Skip("section_box only in 3D views", id);
                    else c.Add(new PlanEntry { Id = id, Key = "section_box", Before = SectionBoxCanon(v3), Target = new List<object> { WriteKit.List(secBox[0]), WriteKit.List(secBox[1]) }, After = BoxCanon(secBox[0], secBox[1]) });
                }
            }
        }

        public static string DetailName(ViewDetailLevel d) => d == ViewDetailLevel.Fine ? "fine" : d == ViewDetailLevel.Medium ? "medium" : d == ViewDetailLevel.Coarse ? "coarse" : "";

        public static string SectionBoxCanon(View3D v)
        {
            try
            {
                if (!v.IsSectionBoxActive) return "no box";
                var b = v.GetSectionBox();
                var t = b.Transform ?? Transform.Identity;
                return WriteKit.Xyz(t.OfPoint(b.Min)) + ";" + WriteKit.Xyz(t.OfPoint(b.Max));
            }
            catch (Exception) { return ""; }
        }

        public static string BoxCanon(double[] a, double[] b)
        {
            var min = new XYZ(Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Min(a.Length > 2 ? a[2] : 0, b.Length > 2 ? b[2] : 0));
            var max = new XYZ(Math.Max(a[0], b[0]), Math.Max(a[1], b[1]), Math.Max(a.Length > 2 ? a[2] : 0, b.Length > 2 ? b[2] : 0));
            return WriteKit.Xyz(min) + ";" + WriteKit.Xyz(max);
        }

        private static string PredictRange(Document doc, ViewPlan v, Dictionary<string, object> range)
        {
            var now = RangeCanon(doc, v).Split('|').Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            foreach (var kv in range)
            {
                var l = (List<object>)kv.Value;
                var lid = Convert.ToInt64(l[0], CultureInfo.InvariantCulture);
                var off = Convert.ToDouble(l[1], CultureInfo.InvariantCulture);
                var lname = lid == -101 ? "current" : lid == -102 ? "up" : lid == -103 ? "down" : lid == -104 ? "unlimited" : doc.GetElement(Ids.ToId(lid))?.Name ?? "?";
                now[kv.Key] = lname + (lname == "unlimited" ? "" : "+" + WriteKit.Num(off));
            }
            return string.Join("|", new[] { "top", "cut", "bottom", "depth" }.Where(now.ContainsKey).Select(k => k + "=" + now[k]));
        }

        private static (View view, int viewRef) ViewArg(BuildContext ctx, OpSpec op, string op2)
        {
            var r = KeyRef(op, "view");
            if (r > 0) return (null, r);
            var v = ctx.Model.FindView(op.Args["view"], op2);
            return (v, 0);
        }

        private static void ViewHide(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "view_hide");
            var hide = Arg.GetBool(op.Args, "hide") ?? true;
            var vkey = view != null ? Ids.ToLong(view.Id).ToString(CultureInfo.InvariantCulture) : "@" + viewRef;
            var ht = new Dictionary<string, object> { ["hide"] = hide };
            if (view != null) ht["view"] = Ids.ToLong(view.Id); else ht["view_ref"] = (long)viewRef;
            Templates(op, c, "oculto@" + vkey, ht, hide ? "hidden" : "visible");
            foreach (var id in ids.Distinct())
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                var t = new Dictionary<string, object> { ["hide"] = hide };
                if (view != null)
                {
                    t["view"] = Ids.ToLong(view.Id);
                    bool can;
                    try { can = e.CanBeHidden(view); } catch (Exception) { can = false; }
                    if (!can) { c.Skip("cannot be hidden in that view", id); continue; }
                    c.Add(new PlanEntry { Id = id, Key = "oculto@" + vkey, Before = e.IsHidden(view) ? "1" : "0", Target = t, After = hide ? "1" : "0" });
                }
                else
                {
                    t["view_ref"] = (long)viewRef;
                    c.Add(new PlanEntry { Id = id, Key = "oculto@" + vkey, Before = "", Target = t, After = hide ? "1" : "0" });
                }
            }
            foreach (var cn in Arg.GetStringList(op.Args, "cats"))
            {
                var cat = WriteKit.CategoryByName(ctx.Doc, cn);
                if (cat == null) throw new QueryException("view_hide: unknown category '" + cn + "'");
                var t = new Dictionary<string, object> { ["hide"] = hide, ["cat"] = Ids.ToLong(cat.Id) };
                if (view != null)
                {
                    if (ControlledByTemplate(ctx.Doc, view, BuiltInParameter.VIS_GRAPHICS_MODEL, out var tn) && !cat.IsTagCategory && cat.CategoryType == CategoryType.Model)
                    { c.Skip("its template (" + tn + ") controls model category visibility", Ids.ToLong(view.Id)); continue; }
                    if (ControlledByTemplate(ctx.Doc, view, BuiltInParameter.VIS_GRAPHICS_ANNOTATION, out tn) && cat.CategoryType == CategoryType.Annotation)
                    { c.Skip("its template (" + tn + ") controls annotation visibility", Ids.ToLong(view.Id)); continue; }
                    if (!view.CanCategoryBeHidden(cat.Id)) { c.Skip("the category " + cat.Name + " cannot be hidden in that view", Ids.ToLong(view.Id)); continue; }
                    t["view"] = Ids.ToLong(view.Id);
                    c.Add(new PlanEntry { Id = Ids.ToLong(view.Id), Key = "cat_oculta:" + cat.Name, Before = view.GetCategoryHidden(cat.Id) ? "1" : "0", Target = t, After = hide ? "1" : "0" });
                }
                else
                {
                    t["view_ref"] = (long)viewRef;
                    c.Add(new PlanEntry { Id = 0, Ref = viewRef, Key = "cat_oculta:" + cat.Name, Before = "", Target = t, After = hide ? "1" : "0" });
                }
            }
        }

        private static void ViewOverride(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "view_override");
            var a = op.Args;
            var t0 = new Dictionary<string, object>();
            var reset = Arg.GetBool(a, "reset") ?? false;
            if (reset) t0["reset"] = true;
            var color = Arg.GetString(a, "color");
            if (color != null)
            {
                if (!WriteKit.TryColor(color, out _)) throw new QueryException("view_override color: #RRGGBB, e.g. #D27300");
                t0["color"] = color.Trim().StartsWith("#") ? color.Trim().ToUpperInvariant() : "#" + color.Trim().ToUpperInvariant();
            }
            if (a.ContainsKey("halftone")) t0["halftone"] = Arg.GetBool(a, "halftone") ?? true;
            var tr = OpCatalog.Number(a, "transparency", "view_override");
            if (tr.HasValue) { if (tr < 0 || tr > 100) throw new QueryException("view_override transparency: 0 to 100"); t0["transp"] = (long)Math.Round(tr.Value); }
            var w = OpCatalog.Number(a, "line_weight", "view_override");
            if (w.HasValue) { if (w < 1 || w > 16) throw new QueryException("view_override line_weight: 1 to 16"); t0["weight"] = (long)Math.Round(w.Value); }
            if (t0.Count == 0) throw new QueryException("view_override: specify color, halftone, transparency, line_weight or reset");
            var vkey = view != null ? Ids.ToLong(view.Id).ToString(CultureInfo.InvariantCulture) : "@" + viewRef;
            var ot = new Dictionary<string, object>(t0);
            if (view != null) ot["view"] = Ids.ToLong(view.Id); else ot["view_ref"] = (long)viewRef;
            Templates(op, c, "graphics@" + vkey, ot, WriteKit.Overrides(Merge(ctx.Doc, new OverrideGraphicSettings(), t0)));

            foreach (var id in ids.Distinct())
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                var t = new Dictionary<string, object>(t0);
                if (view != null)
                {
                    t["view"] = Ids.ToLong(view.Id);
                    var cur = view.GetElementOverrides(e.Id);
                    c.Add(new PlanEntry { Id = id, Key = "graphics@" + vkey, Before = WriteKit.Overrides(cur), Target = t, After = WriteKit.Overrides(Merge(ctx.Doc, cur, t)) });
                }
                else
                {
                    t["view_ref"] = (long)viewRef;
                    c.Add(new PlanEntry { Id = id, Key = "graphics@" + vkey, Before = "", Target = t, After = WriteKit.Overrides(Merge(ctx.Doc, new OverrideGraphicSettings(), t)) });
                }
            }
            foreach (var cn in Arg.GetStringList(a, "cats"))
            {
                var cat = WriteKit.CategoryByName(ctx.Doc, cn);
                if (cat == null) throw new QueryException("view_override: unknown category '" + cn + "'");
                var t = new Dictionary<string, object>(t0) { ["cat"] = Ids.ToLong(cat.Id) };
                if (view != null)
                {
                    if (ControlledByTemplate(ctx.Doc, view, cat.CategoryType == CategoryType.Annotation ? BuiltInParameter.VIS_GRAPHICS_ANNOTATION : BuiltInParameter.VIS_GRAPHICS_MODEL, out var tn))
                    { c.Skip("its template (" + tn + ") controls the graphics of that category", Ids.ToLong(view.Id)); continue; }
                    t["view"] = Ids.ToLong(view.Id);
                    var cur = view.GetCategoryOverrides(cat.Id);
                    c.Add(new PlanEntry { Id = Ids.ToLong(view.Id), Key = "cat_graficos:" + cat.Name, Before = WriteKit.Overrides(cur), Target = t, After = WriteKit.Overrides(Merge(ctx.Doc, cur, t)) });
                }
                else
                {
                    t["view_ref"] = (long)viewRef;
                    c.Add(new PlanEntry { Id = 0, Ref = viewRef, Key = "cat_graficos:" + cat.Name, Before = "", Target = t, After = WriteKit.Overrides(Merge(ctx.Doc, new OverrideGraphicSettings(), t)) });
                }
            }
        }

        /// <summary>Resulting overrides: the current ones with the requested ones on top (or cleared with reset).</summary>
        public static OverrideGraphicSettings Merge(Document doc, OverrideGraphicSettings cur, Dictionary<string, object> t)
        {
            var o = WriteKit.B(t, "reset") || cur == null ? new OverrideGraphicSettings() : new OverrideGraphicSettings(cur);
            var hex = WriteKit.S(t, "color");
            if (hex != null && WriteKit.TryColor(hex, out var col))
            {
                o.SetProjectionLineColor(col);
                o.SetSurfaceForegroundPatternColor(col);
                o.SetCutLineColor(col);
                var solid = SolidFill(doc);
                if (solid != null) o.SetSurfaceForegroundPatternId(solid.Id);
            }
            if (t.ContainsKey("halftone")) o.SetHalftone(WriteKit.B(t, "halftone"));
            if (t.ContainsKey("transp")) o.SetSurfaceTransparency((int)WriteKit.L(t, "transp"));
            if (t.ContainsKey("weight")) o.SetProjectionLineWeight((int)WriteKit.L(t, "weight"));
            return o;
        }

        private static FillPatternElement _solid;
        private static Document _solidDoc;

        /// <summary>Solid fill (its name changes with the Revit language: it is found by property, not by name).</summary>
        public static FillPatternElement SolidFill(Document doc)
        {
            if (_solid != null && _solidDoc == doc && _solid.IsValidObject) return _solid;
            _solidDoc = doc;
            _solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => { try { return f.GetFillPattern().IsSolidFill; } catch (Exception) { return false; } });
            return _solid;
        }

        // =====================================================================================================
        // Sheets, notes, regions
        // =====================================================================================================

        private static void SheetCreate(BuildContext ctx, OpSpec op, Collector c)
        {
            var number = Arg.GetString(op.Args, "number").Trim();
            var name = Arg.GetString(op.Args, "name").Trim();
            if (ctx.Model.Sheets.Any(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase)) || !ctx.NewNames.Add("sheet:" + number))
                throw new QueryException("sheet_create: sheet " + number + " already exists");
            var tbName = Arg.GetString(op.Args, "titleblock");
            ElementType tb;
            if (tbName != null) tb = ctx.Model.FindType(tbName, "sheet_create", t => t.Category != null && CatBic(t.Category) == BuiltInCategory.OST_TitleBlocks, "titleblocks");
            else
            {
                tb = ctx.Model.Types.FirstOrDefault(t => t is FamilySymbol && t.Category != null && CatBic(t.Category) == BuiltInCategory.OST_TitleBlocks);
                if (tb == null) throw new QueryException("sheet_create: the model has no loaded titleblocks");
                c.Note("sheet_create: default titleblock " + tb.FamilyName + ": " + tb.Name);
            }
            var t2 = new Dictionary<string, object> { ["tb"] = Ids.ToLong(tb.Id), ["number"] = number, ["name"] = name };
            var show = "sheet " + number + " - " + name + " (" + tb.FamilyName + ": " + tb.Name + ")";
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t2 });
        }

        /// <summary>0.8.0: persistent sheet set (ViewSheetSet). Replacing requires reemplazar=true; otherwise an existing set is a clear error.</summary>
        private static void SheetSetOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var name = Arg.GetString(op.Args, "name").Trim();
            var replace = Arg.GetBool(op.Args, "replace") ?? false;
            var existing = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !replace) throw new QueryException("sheet_set: the set '" + name + "' already exists; use replace=true to replace it");
            if (!ctx.NewNames.Add("sheet_set:" + name)) throw new QueryException("sheet_set: the plan already saves a set '" + name + "'");
            var sheets = new List<ViewSheet>();
            foreach (var h in (List<object>)op.Args["sheets"])
            {
                var s = ctx.Model.FindSheet(h, "sheet_set");
                if (s.IsPlaceholder) throw new QueryException("sheet_set: " + s.SheetNumber + " is a placeholder; it is not printed");
                if (!sheets.Any(x => x.Id == s.Id)) sheets.Add(s);
            }
            var t = new Dictionary<string, object> { ["name"] = name, ["sheets"] = sheets.Select(s => (object)Ids.ToLong(s.Id)).ToList() };
            if (op.Args.TryGetValue("pdf_preset", out var preset)) t["pdf_preset"] = SheetSetPdf.ValidatePreset(preset);
            if (existing != null) t["replaces"] = Ids.ToLong(existing.Id);
            var before = existing == null ? "" : existing.Views.Size + " views";
            var show = "set '" + name + "': " + sheets.Count + " sheets (" + string.Join(", ", sheets.Take(6).Select(s => s.SheetNumber)) + (sheets.Count > 6 ? "…" : "") + ")";
            if (t.ContainsKey("pdf_preset")) show += ", preset PDF " + Json.Write(t["pdf_preset"]);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = before, After = show, Show = show, Target = t });
        }

        private static void SheetPlace(BuildContext ctx, OpSpec op, Collector c)
        {
            var sheetRef = KeyRef(op, "sheet");
            var viewRef = KeyRef(op, "view");
            ViewSheet sheet = sheetRef > 0 ? null : ctx.Model.FindSheet(op.Args["sheet"], "sheet_place");
            View view = viewRef > 0 ? null : ctx.Model.FindView(op.Args["view"], "sheet_place", allowSheet: false);
            if (sheet != null && view != null && !Viewport.CanAddViewToSheet(ctx.Doc, sheet.Id, view.Id))
                throw new QueryException("sheet_place: Revit does not allow placing '" + view.Name + "' on " + sheet.SheetNumber + " (is it already on another sheet, or is it a template?)");
            var t = new Dictionary<string, object>();
            if (sheet != null) t["sheet"] = Ids.ToLong(sheet.Id); else t["sheet_ref"] = (long)sheetRef;
            if (view != null) t["view"] = Ids.ToLong(view.Id); else t["view_ref_of"] = (long)viewRef;
            if (op.Args.TryGetValue("point", out var pt) && pt != null)
            {
                var p = OpCatalog.Point(pt, "sheet_place point", false);
                t["x"] = p[0]; t["y"] = p[1];
            }
            var vpt = Arg.GetString(op.Args, "viewport_type");
            if (vpt != null) t["vptype"] = Ids.ToLong(ctx.Model.FindType(vpt, "sheet_place", x => string.Equals(x.FamilyName, "Viewport", StringComparison.OrdinalIgnoreCase) || x.GetType().Name == "ElementType" && x.Category == null, "viewport types").Id);
            var show = (view?.Name ?? "@" + viewRef) + " on " + (sheet != null ? sheet.SheetNumber : "@" + sheetRef) + (t.ContainsKey("x") ? "" : " (titleblock center)") + (vpt != null ? ", viewport " + vpt : "");
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void TextNoteOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "text_note");
            var p = OpCatalog.Point(op.Args["point"], "text_note point", false);
            var text = Arg.GetString(op.Args, "text");
            var tn = Arg.GetString(op.Args, "type");
            ElementId typeId = tn != null ? ctx.Model.FindType(tn, "text_note", x => x is TextNoteType, "text types").Id : ctx.Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (typeId == null || typeId == ElementId.InvalidElementId) throw new QueryException("text_note: there is no default text type; give type");
            var t = new Dictionary<string, object> { ["x"] = p[0], ["y"] = p[1], ["text"] = text, ["ttype"] = Ids.ToLong(typeId) };
            if (Arg.GetLong(op.Args, "fixture") is long light)
            {
                var config = LightingCallout.Bind(ctx.Doc, light, op.Args);
                var uid = Arg.GetString(config, "uid");
                var key = "callout:" + (view?.UniqueId ?? "@" + viewRef) + ":" + uid;
                if (!ctx.NewNames.Add(key) || (view != null && LightingCallout.InView(ctx.Doc, view.Id)
                    .Any(note => Arg.GetString(LightingCallout.Read(note), "uid") == uid)))
                    throw new QueryException("a managed callout for this fixture already exists in the view. Use text_note_refresh");
                var state = LightingCallout.Observe(ctx.Doc, config);
                text = state.Text;
                t["text"] = text;
                t["callout"] = config;
                t["origin_fingerprint"] = LightingCallout.Fingerprint(config, state.Text, state.Point, state.TypeUid);
            }
            foreach (var key in new[] { "bold", "underline" })
                if (Arg.GetBool(op.Args, key) is bool value) t[key] = value;
            if (view != null) t["view"] = Ids.ToLong(view.Id); else t["view_ref"] = (long)viewRef;
            var show = "note '" + (text.Length > 40 ? text.Substring(0, 40) + "…" : text) + "' in " + (view?.Name ?? "@" + viewRef);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void RefreshTextNotes(BuildContext ctx, IList<long> ids, Collector c)
        {
            foreach (var id in ids.Distinct())
            {
                if (!(ctx.Doc.GetElement(Ids.ToId(id)) is TextNote note) || LightingCallout.Read(note) is not Dictionary<string, object> config)
                    throw new QueryException("note " + id + " has no managed link");
                var state = LightingCallout.Observe(ctx.Doc, config);
                if (LightingCallout.IsCurrent(note, config, state.Text, state.Point, state.TypeUid)) continue;
                var source = LightingCallout.Fingerprint(config, state.Text, state.Point, state.TypeUid);
                var view = ctx.Doc.GetElement(note.OwnerViewId) as View ?? throw new QueryException("the note's view no longer exists");
                var future = LightingCallout.WithSource(config, state.Point, state.TypeUid);
                var futureSource = LightingCallout.Fingerprint(future, state.Text, state.Point, state.TypeUid);
                var position = LightingCallout.Reposition(note, view, config, state.Point);
                var show = "Regenerate callout " + id + ": " + note.Text + " → " + state.Text;
                c.Add(new PlanEntry { Id = id, Key = "fixture_callout", Before = LightingCallout.NoteFingerprint(note, config, source),
                    After = LightingCallout.NoteFingerprint(state.Text, position, future, futureSource), Show = show, Target = new Dictionary<string, object>() });
            }
        }

        private static void RebindAnnotation(BuildContext ctx, OpSpec op, long id, Collector c)
        {
            if (ctx.Doc.IsFamilyDocument) throw new QueryException("annotation_rebind only supports annotations in a project");
            var original = ctx.Doc.GetElement(Ids.ToId(id));
            if (original == null || original.Pinned) throw new QueryException("the annotation must exist and be unpinned");
            if (!ctx.NewNames.Add("annotation_rebind:" + id)) throw new QueryException("the annotation is already in another replacement in the plan");
            var data = new Dictionary<string, object> { ["original"] = id };
            foreach (var key in new[] { "target", "stable_refs" })
                if (op.Args.TryGetValue(key, out var value)) data[key] = value;
            var references = AnnotationRepair.ResolveTargets(ctx.Doc, original, data);
            data["original_signature"] = AnnotationRepair.Fingerprint(original);
            data["target_refs"] = references.Select(r => (object)r.ConvertToStableRepresentation(ctx.Doc)).ToList();
            var show = "repair annotation " + id + " in view " + Ids.ToLong(original.OwnerViewId) +
                " with references to " + string.Join(", ", references.Select(r => Ids.ToLong(r.ElementId)));
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = data });
        }

        private static void FilledRegionOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "filled_region");
            var pts = OpCatalog.Points(op.Args["points"], "filled_region points", 3, 200, false);
            double area2 = 0;
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                area2 += a[0] * b[1] - b[0] * a[1];
            }
            if (Math.Abs(area2) < 1e-6) throw new QueryException("filled_region: the polygon has no area");
            var tn = Arg.GetString(op.Args, "type");
            ElementId typeId = tn != null ? ctx.Model.FindType(tn, "filled_region", x => x is FilledRegionType, "filled region types").Id : ctx.Doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType);
            if (typeId == null || typeId == ElementId.InvalidElementId) throw new QueryException("filled_region: there is no default type; give type");
            var t = new Dictionary<string, object> { ["pts"] = pts.Select(p => (object)WriteKit.List(p[0], p[1])).ToList(), ["frtype"] = Ids.ToLong(typeId) };
            if (view != null) t["view"] = Ids.ToLong(view.Id); else t["view_ref"] = (long)viewRef;
            var show = "region " + (tn ?? "default") + " with " + pts.Count + " vertices (" + Math.Round(Math.Abs(area2) / 2, 1) + " ft²) in " + (view?.Name ?? "@" + viewRef);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }
    
        // ---------------- 0.8.0: 2D drafting (geometry and validation shared in Engine.Draw2D) ----------------

        /// <summary>Line style (subcategory of Lines) → id of its projection GraphicsStyle; null = the view's.</summary>
        private static long? LineStyle(BuildContext ctx, OpSpec op, string kind)
        {
            var name = Arg.GetString(op.Args, "style");
            if (name == null) return null;
            var lines = ctx.Doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var sub = lines?.SubCategories.Cast<Category>().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var gs = sub?.GetGraphicsStyle(GraphicsStyleType.Projection);
            if (gs == null)
            {
                var known = lines == null ? "" : string.Join(", ", lines.SubCategories.Cast<Category>().Select(x => x.Name).OrderBy(x => x).Take(25));
                throw new QueryException(kind + ": the line style '" + name + "' does not exist. Styles: " + known);
            }
            return Ids.ToLong(gs.Id);
        }

        private static void ViewTarget(Dictionary<string, object> t, View view, int viewRef)
        {
            if (view != null)
            {
                if (view is View3D || view.IsTemplate) throw new QueryException("2D drafting needs a 2D view (plan, section, elevation, drafting or sheet), not " + view.ViewType);
                t["view"] = Ids.ToLong(view.Id);
            }
            else t["view_ref"] = (long)viewRef;
        }

        private static void DetailCurvesOp(BuildContext ctx, OpSpec op, Collector c, string kind)
        {
            var (view, viewRef) = ViewArg(ctx, op, kind);
            List<double[]> pts; bool closed; string err;
            switch (kind)
            {
                case "detail_rect":
                    var ec = OpCatalog.Points(op.Args["corners"], "detail_rect corners", 2, 2, false);
                    pts = Draw2D.Rect(ec[0], ec[1], out err); closed = true; break;
                case "detail_polygon":
                    var pc = OpCatalog.Point(op.Args["center"], "detail_polygon center", false);
                    Lengths.TryFeet(op.Args["radius"], out var rad, out _);
                    pts = Draw2D.RegularPolygon(pc, rad, (int)(OpCatalog.Number(op.Args, "sides", kind) ?? 0), OpCatalog.Number(op.Args, "rot", kind) ?? 0, out err); closed = true; break;
                default:
                    pts = OpCatalog.Points(op.Args["points"], "detail_line points", 2, Draw2D.MaxVertices, false);
                    closed = Arg.GetBool(op.Args, "closed") == true; err = null; break;
            }
            if (err != null) throw new QueryException(kind + ": " + err);
            var segs = Draw2D.Segments(pts, closed, out err);
            if (segs == null) throw new QueryException(kind + ": " + err);
            var t = new Dictionary<string, object> { ["pts"] = pts.Select(p => (object)WriteKit.List(p[0], p[1])).ToList(), ["closed"] = closed };
            var gs = LineStyle(ctx, op, kind); if (gs.HasValue) t["gs"] = gs.Value;
            ViewTarget(t, view, viewRef);
            var show = segs.Count + " detail line(s)" + (Arg.GetString(op.Args, "style") != null ? " (" + Arg.GetString(op.Args, "style") + ")" : "") + " in " + (view?.Name ?? "@" + viewRef);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void DetailArcOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "detail_arc");
            var cen = OpCatalog.Point(op.Args["center"], "detail_arc center", false);
            Lengths.TryFeet(op.Args["radius"], out var rad, out _);
            var a0 = OpCatalog.Number(op.Args, "start", "detail_arc") ?? 0; var a1 = OpCatalog.Number(op.Args, "end", "detail_arc") ?? 0;
            var e = Draw2D.ValidateArc(rad, a0, a1); if (e != null) throw new QueryException("detail_arc: " + e);
            var t = new Dictionary<string, object> { ["c"] = WriteKit.List(cen[0], cen[1]), ["r"] = rad, ["a0"] = a0, ["a1"] = a1 };
            var gs = LineStyle(ctx, op, "detail_arc"); if (gs.HasValue) t["gs"] = gs.Value;
            ViewTarget(t, view, viewRef);
            var show = "arc r=" + Ft(rad) + " from " + a0 + "° to " + a1 + "° in " + (view?.Name ?? "@" + viewRef);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void DetailComponentOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "detail_component");
            var spec = Arg.GetString(op.Args, "type");
            var sym = ctx.Model.FindType(spec, "detail_component", x => x is FamilySymbol fs && (fs.Family?.FamilyPlacementType == FamilyPlacementType.ViewBased), "loaded detail components / symbols") as FamilySymbol;
            var p = OpCatalog.Point(op.Args["point"], "detail_component point", false);
            var t = new Dictionary<string, object> { ["sym"] = Ids.ToLong(sym.Id), ["x"] = p[0], ["y"] = p[1], ["rot"] = OpCatalog.Number(op.Args, "rot", "detail_component") ?? 0.0 };
            ViewTarget(t, view, viewRef);
            var show = "component " + sym.FamilyName + ": " + sym.Name + " in " + (view?.Name ?? "@" + viewRef);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void DimensionOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var (view, viewRef) = ViewArg(ctx, op, "dimension");
            if (view == null) throw new QueryException("dimension: the view must already exist (not @n)");
            var refs = Arg.GetString(op.Args, "refs").Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList();
            foreach (var r in refs)
            {
                var e = ctx.Doc.GetElement(Ids.ToId(r)) as DetailCurve;
                if (e == null || !(e.GeometryCurve is Line)) throw new QueryException("dimension refs: " + r + " is not a straight detail line");
                if (e.OwnerViewId != view.Id) throw new QueryException("dimension refs: line " + r + " does not belong to view " + view.Name);
            }
            var ln = OpCatalog.Points(op.Args["line"], "dimension line", 2, 2, false);
            var t = new Dictionary<string, object> { ["refs"] = refs.Cast<object>().ToList(), ["l"] = new List<object> { WriteKit.List(ln[0][0], ln[0][1]), WriteKit.List(ln[1][0], ln[1][1]) } };
            var tn = Arg.GetString(op.Args, "type");
            if (tn != null) t["dtype"] = Ids.ToLong(ctx.Model.FindType(tn, "dimension", x => x is DimensionType, "dimension types").Id);
            ViewTarget(t, view, viewRef);
            var show = "dimension between " + refs.Count + " lines in " + view.Name;
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }
    }
}
