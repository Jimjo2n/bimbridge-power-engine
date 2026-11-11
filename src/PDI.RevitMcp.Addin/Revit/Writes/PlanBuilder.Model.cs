// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Writes
{
    /// <summary>
    /// 0.9.1 (Bloques C/D/E): planificación de las operaciones tipadas de modelado. La validación sin Revit está en Engine.ModelOps;
    /// aquí se resuelven tipos, niveles, sistemas y cuadros contra el modelo y se rechaza la operación entera si falta algo.
    /// Nada se crea al planificar: cada entrada "nuevo" guarda en Target lo necesario para crear y para verificar después.
    /// </summary>
    internal static partial class PlanBuilder
    {
        private static string Show(BuildContext ctx, ElementType t) => t == null ? "por defecto" : t.FamilyName + ": " + t.Name;

        private static ElementId DefaultType(BuildContext ctx, ElementTypeGroup g, string kind)
        {
            var id = ctx.Doc.GetDefaultElementTypeId(g);
            if (id == null || id == ElementId.InvalidElementId) throw new QueryException(kind + ": el modelo no tiene tipo por defecto; indique tipo");
            return id;
        }

        private static double Feet(OpSpec op, string key, double fallback) => OpCatalog.Length(op.Args, key, op.Kind) ?? fallback;

        private static List<object> Pts(IEnumerable<double[]> pts) => pts.Select(p => (object)WriteKit.List(p.Length > 2 ? new[] { p[0], p[1], p[2] } : new[] { p[0], p[1] })).ToList();

        private static void LevelCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var elev = OpCatalog.Length(op.Args, "elev", op.Kind).Value;
            var name = Arg.GetString(op.Args, "nombre")?.Trim();
            if (ctx.Model.Levels.Any(l => Math.Abs(l.Elevation - elev) < 1e-6))
                c.Note("level_create: ya hay un nivel a esa elevación (" + string.Join(", ", ctx.Model.Levels.Where(l => Math.Abs(l.Elevation - elev) < 1e-6).Select(l => l.Name)) + ")");
            if (name != null && (ctx.Model.Levels.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) || !ctx.NewNames.Add("nivel:" + name)))
                throw new QueryException("level_create: ya existe el nivel '" + name + "'");
            var t = new Dictionary<string, object> { ["elev"] = elev };
            if (name != null) t["nombre"] = name;
            foreach (var (key, fam, tk) in new[] { ("vista_planta", ViewFamily.FloorPlan, "vft_planta"), ("vista_rcp", ViewFamily.CeilingPlan, "vft_rcp") })
            {
                if (Arg.GetBool(op.Args, key) != true) continue;
                var vft = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == fam)
                          ?? throw new QueryException("level_create " + key + ": el modelo no tiene tipo de vista " + fam);
                t[tk] = Ids.ToLong(vft.Id);
            }
            var show = "nivel " + (name ?? "(nombre automático)") + " a " + Ft(elev) + (t.ContainsKey("vft_planta") ? " + planta" : "") + (t.ContainsKey("vft_rcp") ? " + RCP" : "");
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }

        private static void GridCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var p = OpCatalog.Points(op.Args["puntos"], "grid_create puntos", 2, 2, false);
            var name = Arg.GetString(op.Args, "nombre")?.Trim();
            if (name != null)
            {
                var taken = new FilteredElementCollector(ctx.Doc).OfClass(typeof(Grid)).Cast<Grid>().Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (taken || !ctx.NewNames.Add("rejilla:" + name)) throw new QueryException("grid_create: ya existe la rejilla '" + name + "'");
            }
            var t = new Dictionary<string, object> { ["pts"] = Pts(p) };
            if (name != null) t["nombre"] = name;
            var show = "rejilla " + (name ?? "(nombre automático)") + " de " + Draw2D.From(p[0]) + " a " + Draw2D.From(p[1]);
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }

        private static void WallCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var pts = OpCatalog.Points(op.Args["puntos"], "wall_create puntos", 2, ModelOps.MaxWallSegments + 1, false);
            var closed = Arg.GetBool(op.Args, "cerrado") == true;
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "nivel"), op.Kind);
            var tn = Arg.GetString(op.Args, "tipo");
            var wt = tn != null ? ctx.Model.FindType(tn, op.Kind, x => x is WallType, "tipos de muro") : null;
            var typeId = wt?.Id ?? DefaultType(ctx, ElementTypeGroup.WallType, op.Kind);
            Level top = null;
            if (op.Args.ContainsKey("nivel_superior"))
            {
                top = ctx.Model.FindLevel(Arg.GetString(op.Args, "nivel_superior"), op.Kind + " nivel_superior");
                if (top.Elevation <= lvl.Elevation) throw new QueryException("wall_create: nivel_superior debe estar por encima de " + lvl.Name);
            }
            var height = Feet(op, "altura", ModelOps.DefaultWallHeight);
            var offset = Feet(op, "desfase", 0);
            var structural = Arg.GetBool(op.Args, "estructural") == true;
            foreach (var (a, b) in ModelOps.WallSegments(pts, closed))
            {
                var t = new Dictionary<string, object>
                {
                    ["pts"] = Pts(new[] { a, b }), ["wtype"] = Ids.ToLong(typeId), ["nivel"] = Ids.ToLong(lvl.Id),
                    ["altura"] = height, ["desfase"] = offset, ["estructural"] = structural,
                };
                if (top != null) t["nivel_superior"] = Ids.ToLong(top.Id);
                var show = "muro " + Show(ctx, wt) + " " + Draw2D.From(a) + "→" + Draw2D.From(b) + " en " + lvl.Name + (top != null ? " hasta " + top.Name : ", h=" + Ft(height));
                c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
            }
        }

        private static void SlabCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var floor = op.Kind == "floor_create";
            var pts = OpCatalog.Points(op.Args["puntos"], op.Kind + " puntos", 3, ModelOps.MaxLoopVertices, false);
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "nivel"), op.Kind);
            var tn = Arg.GetString(op.Args, "tipo");
            ElementType st = null;
            if (tn != null) st = floor ? ctx.Model.FindType(tn, op.Kind, x => x is FloorType, "tipos de suelo") : ctx.Model.FindType(tn, op.Kind, x => x is CeilingType, "tipos de techo");
            var typeId = st?.Id ?? DefaultType(ctx, floor ? ElementTypeGroup.FloorType : ElementTypeGroup.CeilingType, op.Kind);
            var off = floor ? Feet(op, "desfase", 0) : Feet(op, "altura", ModelOps.DefaultCeilingHeight);
            var t = new Dictionary<string, object> { ["pts"] = Pts(pts), ["stype"] = Ids.ToLong(typeId), ["nivel"] = Ids.ToLong(lvl.Id), ["desfase"] = off };
            if (floor) t["estructural"] = Arg.GetBool(op.Args, "estructural") == true;
            var area = Math.Abs(Draw2D.SignedArea(pts));
            var show = (floor ? "suelo " : "techo ") + Show(ctx, st) + " de " + pts.Count + " vértices (" + Math.Round(area, 1).ToString(CultureInfo.InvariantCulture) + " ft²) en " + lvl.Name + (off != 0 ? (floor ? ", desfase " : ", a ") + Ft(off) : "");
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }

        private static void RoomCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "nivel"), op.Kind);
            var p = OpCatalog.Point(op.Args["punto"], "room_create punto", false);
            var t = new Dictionary<string, object> { ["nivel"] = Ids.ToLong(lvl.Id), ["x"] = p[0], ["y"] = p[1] };
            var name = Arg.GetString(op.Args, "nombre"); if (name != null) t["nombre"] = name.Trim();
            var number = Arg.GetString(op.Args, "numero");
            if (number != null)
            {
                number = number.Trim();
                var dup = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .Cast<SpatialElement>().Any(r => r.LevelId == lvl.Id && string.Equals(r.Number, number, StringComparison.OrdinalIgnoreCase));
                if (dup || !ctx.NewNames.Add("habitacion:" + Ids.ToLong(lvl.Id) + ":" + number)) c.Note("room_create: ya hay una habitación " + number + " en " + lvl.Name + " (Revit avisará de número duplicado)");
                t["numero"] = number;
            }
            var show = "habitación " + (number ?? "") + " " + (name ?? "") + " en " + lvl.Name + " " + Draw2D.From(p);
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show.Replace("  ", " "), Show = show.Replace("  ", " "), Target = t });
        }

        private static void MepCurveCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var duct = op.Kind == "duct_create";
            var a = OpCatalog.Point(op.Args["inicio"], op.Kind + " inicio", true);
            var b = OpCatalog.Point(op.Args["fin"], op.Kind + " fin", true);
            var lvl = ctx.Model.FindLevel(Arg.GetString(op.Args, "nivel"), op.Kind);
            var sysName = Arg.GetString(op.Args, "sistema");
            var sys = duct
                ? ctx.Model.FindType(sysName, op.Kind + " sistema", x => x is MechanicalSystemType, "tipos de sistema mecánico")
                : ctx.Model.FindType(sysName, op.Kind + " sistema", x => x is PipingSystemType, "tipos de sistema de tuberías");
            var tn = Arg.GetString(op.Args, "tipo");
            ElementType ct;
            if (tn != null) ct = duct ? ctx.Model.FindType(tn, op.Kind, x => x is DuctType, "tipos de conducto") : ctx.Model.FindType(tn, op.Kind, x => x is PipeType, "tipos de tubería");
            else
            {
                var pool = ctx.Model.Types.Where(x => duct ? x is DuctType : x is PipeType).ToList();
                if (pool.Count != 1) throw new QueryException(op.Kind + ": hay " + pool.Count + " tipos " + (duct ? "de conducto" : "de tubería") + "; indique tipo (" + string.Join(" | ", pool.Take(6).Select(x => x.FamilyName + ": " + x.Name)) + ")");
                ct = pool[0];
            }
            if (duct && ct is DuctType dt)
            {
                var round = dt.Shape == ConnectorProfileType.Round;
                if (op.Args.ContainsKey("diametro") && !round) throw new QueryException("duct_create: el tipo " + ct.Name + " no es redondo; use ancho/alto");
                if (op.Args.ContainsKey("ancho") && round) throw new QueryException("duct_create: el tipo " + ct.Name + " es redondo; use diametro");
            }
            var t = new Dictionary<string, object>
            {
                ["a"] = WriteKit.List(a[0], a[1], a[2]), ["b"] = WriteKit.List(b[0], b[1], b[2]),
                ["nivel"] = Ids.ToLong(lvl.Id), ["sys"] = Ids.ToLong(sys.Id), ["ctype"] = Ids.ToLong(ct.Id),
            };
            foreach (var k in new[] { "diametro", "ancho", "alto" })
            {
                var v = OpCatalog.Length(op.Args, k, op.Kind);
                if (v.HasValue) t[k] = v.Value;
            }
            var size = t.ContainsKey("diametro") ? " Ø" + Ft((double)t["diametro"]) : t.ContainsKey("ancho") ? " " + Ft((double)t["ancho"]) + "×" + Ft((double)t["alto"]) : "";
            var show = (duct ? "conducto " : "tubería ") + ct.Name + size + " (" + sys.Name + ") de [" + string.Join(", ", a.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "] a [" + string.Join(", ", b.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }

        /// <summary>¿Tiene el elemento algún conector eléctrico de fuerza libre? (los ocupados harían fallar ElectricalSystem.Create).</summary>
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
            if (hits.Count > 1) throw new QueryException(kind + ": hay " + hits.Count + " cuadros llamados '" + s + "'; use el id");
            throw new QueryException(kind + ": no existe el cuadro '" + s + "'. Cuadros: " + string.Join(", ", panels.Select(PanelName).Distinct().OrderBy(x => x).Take(30)));
        }

        private static void CircuitCreateOp(BuildContext ctx, OpSpec op, IList<long> ids, Collector c)
        {
            if (op.RefOps.Count > 0) throw new QueryException("circuit_create: use ids de dispositivos que ya existen (no @n)");
            var ok = new List<long>();
            foreach (var id in ids)
            {
                var e = ctx.Doc.GetElement(Ids.ToId(id));
                if (e == null) { c.Skip("no existe", id); continue; }
                if (!(e is FamilyInstance)) { c.Skip("no es un ejemplar de familia", id); continue; }
                if (!HasFreePowerConnector(e)) { c.Skip("sin conector eléctrico de fuerza libre (¿ya está en un circuito?)", id); continue; }
                ok.Add(id);
            }
            if (ok.Count == 0) throw new QueryException("circuit_create: ningún dispositivo admite un circuito nuevo");
            var t = new Dictionary<string, object> { ["ids"] = ok.Cast<object>().ToList() };
            var pn = Arg.GetString(op.Args, "panel");
            FamilyInstance panel = null;
            if (pn != null)
            {
                panel = FindPanel(ctx.Doc, pn, op.Kind);
                t["panel"] = Ids.ToLong(panel.Id);
            }
            var show = "circuito de fuerza con " + ok.Count + " dispositivo(s)" + (panel != null ? " en " + (panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? panel.Name) : " (sin cuadro)");
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }

        private static void ScheduleCreateOp(BuildContext ctx, OpSpec op, Collector c)
        {
            var catName = ModelOps.NormalizeCategory(Arg.GetString(op.Args, "categoria"));
            if (!QueryRunner.TryCategory(catName, out var bic)) throw new QueryException("schedule_create: no existe la categoría " + catName);
            var catId = new ElementId(bic);
            if (!ViewSchedule.IsValidCategoryForSchedule(catId)) throw new QueryException("schedule_create: Revit no admite tablas de " + catName);
            var name = Arg.GetString(op.Args, "nombre")?.Trim();
            if (name != null)
            {
                var taken = new FilteredElementCollector(ctx.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (taken || !ctx.NewNames.Add("tabla:" + name)) throw new QueryException("schedule_create: ya existe la tabla '" + name + "'");
            }
            var fields = ModelOps.ScheduleFields(op.Args).Select(f => f.Trim()).ToList();
            var t = new Dictionary<string, object> { ["cat"] = Ids.ToLong(catId), ["campos"] = fields.Cast<object>().ToList() };
            if (name != null) t["nombre"] = name;
            var show = "tabla " + (name ?? "(nombre automático)") + " de " + catName.Substring(4) + ": " + string.Join(", ", fields);
            c.Add(new PlanEntry { Id = 0, Key = "nuevo", Before = "", After = show, Show = show, Target = t });
        }
    }
}
