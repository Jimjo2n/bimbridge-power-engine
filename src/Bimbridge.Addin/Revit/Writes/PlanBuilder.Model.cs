// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    /// <summary>
    /// 0.9.1 (Blocks C/D/E): planning of the typed modeling operations. Validation without Revit lives in Engine.ModelOps;
    /// here types, levels, systems and panels are resolved against the model and the whole operation is rejected if anything is missing.
    /// Nothing is created while planning: each "new" entry stores in Target what is needed to create and later verify.
    /// </summary>
    internal static partial class PlanBuilder
    {
        private static string Show(BuildContext ctx, ElementType t) => t == null ? "default" : t.FamilyName + ": " + t.Name;

        private static ElementId DefaultType(BuildContext ctx, ElementTypeGroup g, string kind)
        {
            var id = ctx.Doc.GetDefaultElementTypeId(g);
            if (id == null || id == ElementId.InvalidElementId) throw new QueryException(kind + ": the model has no default type; specify type");
            return id;
        }

        private static double Feet(OpSpec op, string key, double fallback) => OpCatalog.Length(op.Args, key, op.Kind) ?? fallback;

        private static List<object> Pts(IEnumerable<double[]> pts) => pts.Select(p => (object)WriteKit.List(p.Length > 2 ? new[] { p[0], p[1], p[2] } : new[] { p[0], p[1] })).ToList();

        private static void LevelCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var elev = OpCatalog.Length(op.Args, "elev", op.Kind).Value;
            var name = Arg.GetString(op.Args, "name")?.Trim();
            if (ctx.Model.Levels.Any(l => Math.Abs(l.Elevation - elev) < 1e-6))
                c.Note("level_create: there is already a level at that elevation (" + string.Join(", ", ctx.Model.Levels.Where(l => Math.Abs(l.Elevation - elev) < 1e-6).Select(l => l.Name)) + ")");
            if (name != null && (ctx.Model.Levels.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) || !ctx.NewNames.Add("level:" + name)))
                throw new QueryException("level_create: level already exists: '" + name + "'");
            var t = new Dictionary<string, object> { ["elev"] = elev };
            if (name != null) t["name"] = name;
            foreach (var (key, fam, tk) in new[] { ("plan_view", ViewFamily.FloorPlan, "vft_plan"), ("rcp_view", ViewFamily.CeilingPlan, "vft_rcp") })
            {
                if (Arg.GetBool(op.Args, key) != true) continue;
                var vft = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == fam)
                          ?? throw new QueryException("level_create " + key + ": the model has no view type " + fam);
                t[tk] = Ids.ToLong(vft.Id);
            }
            var show = "level " + (name ?? "(automatic name)") + " at " + Ft(elev) + (t.ContainsKey("vft_plan") ? " + floor plan" : "") + (t.ContainsKey("vft_rcp") ? " + RCP" : "");
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void GridCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var p = OpCatalog.Points(op.Args["points"], "grid_create points", 2, 2, false);
            var name = Arg.GetString(op.Args, "name")?.Trim();
            if (name != null)
            {
                var taken = new FilteredElementCollector(ctx.Doc).OfClass(typeof(Grid)).Cast<Grid>().Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (taken || !ctx.NewNames.Add("rejilla:" + name)) throw new QueryException("grid_create: grid already exists: '" + name + "'");
            }
            var t = new Dictionary<string, object> { ["pts"] = Pts(p) };
            if (name != null) t["name"] = name;
            var show = "grid " + (name ?? "(automatic name)") + " from " + Draw2D.From(p[0]) + " to " + Draw2D.From(p[1]);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void WallCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var pts = OpCatalog.Points(op.Args["points"], "wall_create points", 2, ModelOps.MaxWallSegments + 1, false);
            var closed = Arg.GetBool(op.Args, "closed") == true;
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), op.Kind);
            var tn = Arg.GetString(op.Args, "type");
            var wt = tn != null ? ctx.Model.FindType(tn, op.Kind, x => x is WallType, "wall types") : null;
            var typeId = wt?.Id ?? DefaultType(ctx, ElementTypeGroup.WallType, op.Kind);
            Level top = null;
            if (op.Args.ContainsKey("top_level"))
            {
                top = ctx.Model.FindLevel(Arg.GetString(op.Args, "top_level"), op.Kind + " top_level");
                if (top.Elevation <= lvl.Elevation) throw new QueryException("wall_create: top_level must be above " + lvl.Name);
            }
            var height = Feet(op, "height", ModelOps.DefaultWallHeight);
            var offset = Feet(op, "offset", 0);
            var structural = Arg.GetBool(op.Args, "structural") == true;
            foreach (var (a, b) in ModelOps.WallSegments(pts, closed))
            {
                var t = new Dictionary<string, object>
                {
                    ["pts"] = Pts(new[] { a, b }), ["wtype"] = Ids.ToLong(typeId), ["level"] = Ids.ToLong(lvl.Id),
                    ["height"] = height, ["offset"] = offset, ["structural"] = structural,
                };
                if (top != null) t["top_level"] = Ids.ToLong(top.Id);
                var show = "wall " + Show(ctx, wt) + " " + Draw2D.From(a) + "→" + Draw2D.From(b) + " on " + lvl.Name + (top != null ? " up to " + top.Name : ", h=" + Ft(height));
                c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
            }
        }

        private static void SlabCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var floor = op.Kind == "floor_create";
            var pts = OpCatalog.Points(op.Args["points"], op.Kind + " points", 3, ModelOps.MaxLoopVertices, false);
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), op.Kind);
            var tn = Arg.GetString(op.Args, "type");
            ElementType st = null;
            if (tn != null) st = floor ? ctx.Model.FindType(tn, op.Kind, x => x is FloorType, "floor types") : ctx.Model.FindType(tn, op.Kind, x => x is CeilingType, "ceiling types");
            var typeId = st?.Id ?? DefaultType(ctx, floor ? ElementTypeGroup.FloorType : ElementTypeGroup.CeilingType, op.Kind);
            var off = floor ? Feet(op, "offset", 0) : Feet(op, "height", ModelOps.DefaultCeilingHeight);
            var t = new Dictionary<string, object> { ["pts"] = Pts(pts), ["stype"] = Ids.ToLong(typeId), ["level"] = Ids.ToLong(lvl.Id), ["offset"] = off };
            if (floor) t["structural"] = Arg.GetBool(op.Args, "structural") == true;
            var area = Math.Abs(Draw2D.SignedArea(pts));
            var show = (floor ? "floor " : "ceiling ") + Show(ctx, st) + " with " + pts.Count + " vertices (" + Math.Round(area, 1).ToString(CultureInfo.InvariantCulture) + " ft²) on " + lvl.Name + (off != 0 ? (floor ? ", offset " : ", at ") + Ft(off) : "");
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void RoomCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), op.Kind);
            var p = OpCatalog.Point(op.Args["point"], "room_create point", false);
            var t = new Dictionary<string, object> { ["level"] = Ids.ToLong(lvl.Id), ["x"] = p[0], ["y"] = p[1] };
            var name = Arg.GetString(op.Args, "name"); if (name != null) t["name"] = name.Trim();
            var number = Arg.GetString(op.Args, "number");
            if (number != null)
            {
                number = number.Trim();
                var dup = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<SpatialElement>().Any(r => r.LevelId == lvl.Id && string.Equals(r.Number, number, StringComparison.OrdinalIgnoreCase));
                if (dup || !ctx.NewNames.Add("habitacion:" + Ids.ToLong(lvl.Id) + ":" + number)) c.Note("room_create: there is already a room " + number + " on " + lvl.Name + " (Revit will warn about a duplicate number)");
                t["number"] = number;
            }
            var show = "room " + (number ?? "") + " " + (name ?? "") + " on " + lvl.Name + " " + Draw2D.From(p);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show.Replace("  ", " "), Show = show.Replace("  ", " "), Target = t });
        }

        private static void ConduitCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var level = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), op.Kind);
            var type = ctx.Model.FindType(Arg.GetString(op.Args, "type"), op.Kind, x => x is ConduitType, "conduit types");
            var settings = new Dictionary<string, object>
            {
                ["level"] = Ids.ToLong(level.Id), ["ctype"] = Ids.ToLong(type.Id),
                ["diameter"] = OpCatalog.Length(op.Args, "diameter", op.Kind).Value,
            };
            if (op.Kind == "conduit_route")
            {
                var points = OpCatalog.Points(op.Args["points"], op.Kind + " points", 3, 100);
                settings["points"] = points.Select(p => (object)WriteKit.List(p[0], p[1], p[2])).ToList();
                var a = new XYZ(points[0][0], points[0][1], points[0][2]);
                var b = new XYZ(points.Last()[0], points.Last()[1], points.Last()[2]);
                if (op.Args.TryGetValue("replace_ids", out var replace))
                {
                    var replacing = ((List<object>)replace).Cast<long>().ToList();
                    foreach (var id in replacing)
                        if (!ctx.NewNames.Add("conduit_route:" + id))
                            throw new QueryException("ID " + id + " is already in the scope of another replacement in the plan");
                    var original = ConduitRouteScope.Capture(ctx.Doc, replacing, a, b);
                    settings["replace_ids"] = replace;
                    settings["original_fingerprint"] = original["fingerprint"];
                    settings["ends"] = original["ends"];
                }
                else if (op.Args.ContainsKey("start_terminal") || op.Args.ContainsKey("end_terminal"))
                {
                    object Terminal(string name, XYZ point) => op.Args.TryGetValue(name, out var port)
                        ? ConduitRouteScope.Terminal(ctx.Doc, (Dictionary<string, object>)port, point, mustBeFree: true) : null;
                    settings["ends"] = new List<object> { Terminal("start_terminal", a), Terminal("end_terminal", b) };
                }
            }
            else
            {
                var a = OpCatalog.Point(op.Args["start"], op.Kind + " start", true);
                var b = OpCatalog.Point(op.Args["end"], op.Kind + " end", true);
                settings["a"] = WriteKit.List(a[0], a[1], a[2]); settings["b"] = WriteKit.List(b[0], b[1], b[2]);
            }
            var ws = Arg.GetString(op.Args, "ws");
            if (ws != null) settings["ws"] = (long)ctx.Model.FindWorkset(ws, op.Kind).Id.IntegerValue;
            var show = (op.Kind == "conduit_route" ? "conduit run " : "conduit ") +
                type.Name + " Ø" + Ft((double)settings["diameter"]) + " on " + level.Name;
            if (settings.TryGetValue("replace_ids", out var old))
                show += " replacing " + ((List<object>)old).Count + " elements, keeping the ends";
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = settings });
        }

        private static void MepCurveCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var duct = op.Kind == "duct_create";
            var a = OpCatalog.Point(op.Args["start"], op.Kind + " start", true);
            var b = OpCatalog.Point(op.Args["end"], op.Kind + " end", true);
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "level"), op.Kind);
            var sysName = Arg.GetString(op.Args, "system");
            var sys = duct
                ? ctx.Model.FindType(sysName, op.Kind + " system", x => x is MechanicalSystemType, "mechanical system types")
                : ctx.Model.FindType(sysName, op.Kind + " system", x => x is PipingSystemType, "piping system types");
            var tn = Arg.GetString(op.Args, "type");
            ElementType ct;
            if (tn != null) ct = duct ? ctx.Model.FindType(tn, op.Kind, x => x is DuctType, "duct types") : ctx.Model.FindType(tn, op.Kind, x => x is PipeType, "pipe types");
            else
            {
                var pool = ctx.Model.Types.Where(x => duct ? x is DuctType : x is PipeType).ToList();
                if (pool.Count != 1) throw new QueryException(op.Kind + ": there are " + pool.Count + " " + (duct ? "duct" : "pipe") + " types; specify type (" + string.Join(" | ", pool.Take(6).Select(x => x.FamilyName + ": " + x.Name)) + ")");
                ct = pool[0];
            }
            if (duct && ct is DuctType dt)
            {
                var round = dt.Shape == ConnectorProfileType.Round;
                if (op.Args.ContainsKey("diameter") && !round) throw new QueryException("duct_create: type " + ct.Name + " is not round; use width/height");
                if (op.Args.ContainsKey("width") && round) throw new QueryException("duct_create: type " + ct.Name + " is round; use diameter");
            }
            var t = new Dictionary<string, object>
            {
                ["a"] = WriteKit.List(a[0], a[1], a[2]), ["b"] = WriteKit.List(b[0], b[1], b[2]),
                ["level"] = Ids.ToLong(lvl.Id), ["sys"] = Ids.ToLong(sys.Id), ["ctype"] = Ids.ToLong(ct.Id),
            };
            foreach (var k in new[] { "diameter", "width", "height" })
            {
                var v = OpCatalog.Length(op.Args, k, op.Kind);
                if (v.HasValue) t[k] = v.Value;
            }
            var size = t.ContainsKey("diameter") ? " Ø" + Ft((double)t["diameter"]) : t.ContainsKey("width") ? " " + Ft((double)t["width"]) + "×" + Ft((double)t["height"]) : "";
            var show = (duct ? "duct " : "pipe ") + ct.Name + size + " (" + sys.Name + ") from [" + string.Join(", ", a.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "] to [" + string.Join(", ", b.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        /// <summary>Does the element have a free power electrical connector? (taken ones would make ElectricalSystem.Create fail).</summary>
        internal static bool HasFreePowerConnector(Element e)
        {
            var cm = (e as FamilyInstance)?.MEPModel?.ConnectorManager;
            if (cm == null) return false;
            foreach (Connector k in cm.Connectors)
            {
                if (k.Domain != Domain.DomainElectrical) continue;
                var st = k.ElectricalSystemType;
                if (st != ElectricalSystemType.PowerCircuit && st != ElectricalSystemType.PowerBalanced && st != ElectricalSystemType.PowerUnBalanced) continue;
                if (!k.IsConnected) return true;
            }
            return false;
        }

        internal static FamilyInstance FindPanel(Document doc, string spec, string kind)
        {
            var s = (spec ?? "").Trim();
            var panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = panels.FirstOrDefault(p => Ids.ToLong(p.Id) == id);
                if (byId != null) return byId;
            }
            string PanelName(FamilyInstance p) => p.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? p.Name;
            var hits = panels.Where(p => string.Equals(PanelName(p), s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) throw new QueryException(kind + ": there are " + hits.Count + " panels named '" + s + "'; use the id");
            throw new QueryException(kind + ": panel '" + s + "' does not exist. Panels: " + string.Join(", ", panels.Select(PanelName).Distinct().OrderBy(x => x).Take(30)));
        }

        private static void CircuitCreateOp(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            if (op.RefOps.Count > 0) throw new QueryException("circuit_create: use ids of devices that already exist (not @n)");
            var ok = new List<long>();
            foreach (var id in ids)
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("does not exist", id); continue; }
                if (!(e is FamilyInstance)) { c.Skip("is not a family instance", id); continue; }
                if (!HasFreePowerConnector(e)) { c.Skip("no free power electrical connector (already on a circuit?)", id); continue; }
                ok.Add(id);
            }
            if (ok.Count == 0) throw new QueryException("circuit_create: no device accepts a new circuit");
            var t = new Dictionary<string, object> { ["ids"] = ok.Cast<object>().ToList() };
            var pn = Arg.GetString(op.Args, "panel");
            FamilyInstance panel = null;
            if (pn != null)
            {
                panel = FindPanel(ctx.Doc, pn, op.Kind);
                t["panel"] = Ids.ToLong(panel.Id);
            }
            var show = "power circuit with " + ok.Count + " device(s)" + (panel != null ? " on " + (panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name) : " (no panel)");
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }

        private static void ScheduleCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var catName = ModelOps.NormalizeCategory(Arg.GetString(op.Args, "category"));
            if (!QueryRunner.TryCategory(catName, out var bic)) throw new QueryException("schedule_create: category does not exist: " + catName);
            var catId = new ElementId(bic);
            if (!ViewSchedule.IsValidCategoryForSchedule(catId)) throw new QueryException("schedule_create: Revit does not support schedules of " + catName);
            var name = Arg.GetString(op.Args, "name")?.Trim();
            if (name != null)
            {
                var taken = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (taken || !ctx.NewNames.Add("schedule:" + name)) throw new QueryException("schedule_create: schedule already exists: '" + name + "'");
            }
            var fields = ModelOps.ScheduleFields(op.Args).Select(f => f.Trim()).ToList();
            var t = new Dictionary<string, object> { ["cat"] = Ids.ToLong(catId), ["fields"] = fields.Cast<object>().ToList() };
            if (name != null) t["name"] = name;
            var show = "schedule " + (name ?? "(automatic name)") + " of " + catName.Substring(4) + ": " + string.Join(", ", fields);
            c.Add(new PlanEntry { Id = 0, Key = "new", Before = "", After = show, Show = show, Target = t });
        }
    }
}
