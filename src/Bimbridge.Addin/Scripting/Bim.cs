// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Revit;
using Bimbridge.Addin.Revit.Geo;
using Bimbridge.Engine.Qc;

namespace Bimbridge.Addin.Scripting
{
    /// <summary>
    /// BIM helpers for scripts and recipes (0.6.0, milestone D). They are ready-made functions over the public Revit API: they do not limit what the
    /// script can do (it still has doc, the full API and ctx), they only save repeated code and return compact results in mm.
    /// Nothing here opens transactions: the ones that write (Groups.Compose/ChangeType, Views.ApplyRange/FilterByCategory) are called
    /// inside the engine's transaction (exec with writes or a PerUnit recipe).
    /// </summary>
    public static class Bim
    {
        private const double Ft = 304.8;
        private static double R(double ft) => Math.Round(ft * Ft, 1);
        private static long L(ElementId id) => Ids.ToLong(id);

        private static string TypeName(Document doc, Element e)
        {
            var t = doc.GetElement(e.GetTypeId()) as ElementType;
            if (t == null) return e.Name;
            return string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + ": " + t.Name;
        }

        private static XYZ PointOf(Element e)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            var bb = e.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        // =====================================================================================================
        // GROUPS
        // =====================================================================================================
        public static class Groups
        {
            /// <summary>
            /// What is inside a group, with positions relative to its origin (mm, without the group rotation): lets you compare
            /// instances of the same type even when rotated. "mirrored_members" counts the FamilyInstances with Mirrored=true
            /// (the API does not expose whether the whole group is mirrored).
            /// </summary>
            public static Dictionary<string, object> Inspect(Group g)
            {
                var doc = g.Document;
                var o = (g.Location as LocationPoint)?.Point ?? XYZ.Zero;
                var rot = (g.Location as LocationPoint)?.Rotation ?? 0;
                var inv = Transform.CreateRotationAtPoint(XYZ.BasisZ, -rot, o);
                var members = new List<object>();
                var nested = new List<object>();
                int mirrored = 0;
                foreach (var id in g.GetMemberIds())
                {
                    var m = doc.GetElement(id);
                    if (m == null) continue;
                    if (m is Group sub) { nested.Add(new Dictionary<string, object> { ["id"] = L(id), ["type"] = sub.GroupType?.Name }); continue; }
                    if (m is FamilyInstance fi && fi.Mirrored) mirrored++;
                    var p = PointOf(m);
                    var rel = p == null ? null : inv.OfPoint(p) - o;
                    members.Add(new Dictionary<string, object>
                    {
                        ["id"] = L(id), ["uid"] = m.UniqueId, ["cat"] = m.Category?.Name, ["type"] = TypeName(doc, m),
                        ["rel_mm"] = rel == null ? null : new[] { R(rel.X), R(rel.Y), R(rel.Z) },
                    });
                }
                return new Dictionary<string, object>
                {
                    ["id"] = L(g.Id), ["type"] = g.GroupType?.Name, ["origin_mm"] = new[] { R(o.X), R(o.Y), R(o.Z) },
                    ["rotation"] = Math.Round(rot * 180 / Math.PI, 3), ["members"] = members.Count, ["nested"] = nested,
                    ["mirrored_members"] = mirrored, ["detail"] = members,
                };
            }

            /// <summary>Stable signature of a member: category|type|relative position rounded to 10 mm. Used to match members across types.</summary>
            public static string Signature(Group g, Element m)
            {
                var o = (g.Location as LocationPoint)?.Point ?? XYZ.Zero;
                var rot = (g.Location as LocationPoint)?.Rotation ?? 0;
                var p = PointOf(m);
                var rel = p == null ? XYZ.Zero : Transform.CreateRotationAtPoint(XYZ.BasisZ, -rot, o).OfPoint(p) - o;
                string r10(double ft) => (Math.Round(ft * Ft / 10) * 10).ToString("0", CultureInfo.InvariantCulture);
                return (m.Category?.Name ?? "?") + "|" + TypeName(m.Document, m) + "|" + r10(rel.X) + "," + r10(rel.Y) + "," + r10(rel.Z);
            }

            /// <summary>Instances of a group type (by name or id).</summary>
            public static IList<Group> Instances(Document doc, string typeName) =>
                new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().Where(x => x.GroupType?.Name == typeName).ToList();

            /// <summary>Creates a group with those elements and, if a name is given, renames its type (fails if a type with that name already exists).</summary>
            public static Group Compose(Document doc, IEnumerable<ElementId> ids, string typeName = null)
            {
                var list = ids.ToList();
                if (list.Count == 0) throw new ArgumentException("there are no elements to group");
                var g = doc.Create.NewGroup(list);
                if (!string.IsNullOrEmpty(typeName))
                {
                    if (new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Any(t => t.Name == typeName && t.Id != g.GroupType.Id))
                        throw new ArgumentException("a group type already exists: '" + typeName + "'");
                    g.GroupType.Name = typeName;
                }
                return g;
            }

            /// <summary>
            /// Changes the type of a group and checks that it did not move or rotate. Returns the member matching by signature
            /// (before→after) and the unmatched ones, so the recipe decides (no correspondence is invented).
            /// </summary>
            public static Dictionary<string, object> ChangeType(Group g, GroupType newType)
            {
                if (newType == null) throw new ArgumentNullException(nameof(newType));
                var doc = g.Document;
                var lp0 = g.Location as LocationPoint;
                var o0 = lp0?.Point; var r0 = lp0?.Rotation ?? 0;
                var before = g.GetMemberIds().Select(doc.GetElement).Where(m => m != null && !(m is Group))
                    .GroupBy(m => Signature(g, m)).ToDictionary(x => x.Key, x => x.Select(m => L(m.Id)).ToList());
                var previous = g.GroupType?.Name;
                g.GroupType = newType;
                doc.Regenerate();
                var lp1 = g.Location as LocationPoint;
                var moved = o0 != null && lp1 != null ? lp1.Point.DistanceTo(o0) * Ft : 0;
                var turned = lp1 != null ? Math.Abs(lp1.Rotation - r0) * 180 / Math.PI : 0;
                var after = g.GetMemberIds().Select(doc.GetElement).Where(m => m != null && !(m is Group))
                    .GroupBy(m => Signature(g, m)).ToDictionary(x => x.Key, x => x.Select(m => L(m.Id)).ToList());
                var pairs = before.Keys.Intersect(after.Keys).Select(k => new object[] { k, before[k], after[k] }).ToList();
                return new Dictionary<string, object>
                {
                    ["group"] = L(g.Id), ["previous"] = previous, ["new"] = g.GroupType?.Name, ["ok"] = g.GroupType?.Id == newType.Id,
                    ["displaced_mm"] = Math.Round(moved, 1), ["rotated_degrees"] = Math.Round(turned, 3),
                    ["paired"] = pairs.Count, ["only_before"] = before.Keys.Except(after.Keys).ToList(), ["only_after"] = after.Keys.Except(before.Keys).ToList(),
                };
            }

            /// <summary>Do all instances of the type have the same composition (signatures)? Lists the ones that differ (excluded members, etc.).</summary>
            public static Dictionary<string, object> VerifyComposition(Document doc, string typeName)
            {
                var inst = Instances(doc, typeName);
                var sigs = inst.ToDictionary(x => L(x.Id), x => string.Join(";", x.GetMemberIds().Select(doc.GetElement).Where(m => m != null).Select(m => Signature(x, m)).OrderBy(s => s, StringComparer.Ordinal)));
                var mode = sigs.Values.GroupBy(s => s).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
                var diff = sigs.Where(kv => kv.Value != mode).Select(kv => kv.Key).ToList();
                return new Dictionary<string, object> { ["type"] = typeName, ["instances"] = inst.Count, ["different"] = diff, ["coherent"] = diff.Count == 0 };
            }
        }

        // =====================================================================================================
        // MOUNTING
        // =====================================================================================================
        public static class Mounting
        {
            /// <summary>Extents of the real body (mm, without "Light Source"): [zmin, zmax, fuente]. If there are no solids it uses the bounding box and says so.</summary>
            public static Dictionary<string, object> Body(Element e)
            {
                var solids = new List<Solid>();
                Bodies.CollectSolids(e.get_Geometry(Bodies.NewOptions()), solids, e.Document, new[] { "Light Source" });
                if (Bodies.TryBox(solids, out var box))
                    return new Dictionary<string, object> { ["zmin"] = R(box.Min.Z), ["zmax"] = R(box.Max.Z), ["min"] = new[] { R(box.Min.X), R(box.Min.Y) }, ["max"] = new[] { R(box.Max.X), R(box.Max.Y) }, ["source"] = "solids" };
                var bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                return new Dictionary<string, object> { ["zmin"] = R(bb.Min.Z), ["zmax"] = R(bb.Max.Z), ["min"] = new[] { R(bb.Min.X), R(bb.Min.Y) }, ["max"] = new[] { R(bb.Max.X), R(bb.Max.Y) }, ["source"] = "box (no solids: approximate value)" };
            }

            /// <summary>First 3D view that is not a template and has no section box (ReferenceIntersector needs it).</summary>
            public static View3D View3D(Document doc) =>
                new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate)
                    .OrderBy(v => v.IsSectionBoxActive ? 1 : 0).FirstOrDefault();

            /// <summary>
            /// Finds the body's support with a ray from the body base in <paramref name="direction"/> ("up" | "down"),
            /// also inside links. Classifies: al_ras (|clearance| ≤ tol), empotrado (the support cuts the body), separado (positive
            /// clearance: may be correct if pendant-mounted; the reviewer decides), sin_referencia (nothing within maxMm). Moves nothing.
            /// </summary>
            public static Dictionary<string, object> Support(Element e, View3D view, string direction = "up", double maxMm = 3000, double tolMm = 3,
                IEnumerable<BuiltInCategory> categories = null)
            {
                var doc = e.Document;
                view = view ?? View3D(doc) ?? throw new InvalidOperationException("there is no non-template 3D view to cast rays");
                var body = Body(e);
                if (body == null) return new Dictionary<string, object> { ["id"] = L(e.Id), ["class"] = "no_body" };
                var up = direction != "down";
                var cats = (categories ?? new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_GenericModel }).ToList();
                // The filter must also accept the link instance; the category of what is found inside the link is checked afterwards.
                ElementFilter f = new LogicalOrFilter(new ElementMulticategoryFilter(cats), new ElementClassFilter(typeof(RevitLinkInstance)));
                f = new LogicalAndFilter(f, new ExclusionFilter(new List<ElementId> { e.Id }));
                var ri = new ReferenceIntersector(f, FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                var min = (double[])body["min"]; var max = (double[])body["max"];
                var cx = (min[0] + max[0]) / 2 / Ft; var cy = (min[1] + max[1]) / 2 / Ft;
                var zmin = (double)body["zmin"] / Ft; var zmax = (double)body["zmax"] / Ft;
                // Origin 1 mm outside the body on the opposite side: this way a support that cuts the body shows up closer than its height.
                var start = up ? zmin - 1 / Ft : zmax + 1 / Ft;
                var height = (zmax - zmin) + 1 / Ft;
                var hits = ri.Find(new XYZ(cx, cy, start), up ? XYZ.BasisZ : -XYZ.BasisZ) ?? new List<ReferenceWithContext>();
                foreach (var h in hits.OrderBy(x => x.Proximity))
                {
                    if (h.Proximity * Ft > maxMm + height * Ft) break;
                    var r = h.GetReference();
                    Element hit; string link = null;
                    var host = doc.GetElement(r.ElementId);
                    if (host is RevitLinkInstance li)
                    {
                        var ld = li.GetLinkDocument();
                        hit = ld?.GetElement(r.LinkedElementId);
                        if (hit == null || hit.Category == null || !cats.Contains((BuiltInCategory)Ids.ToLong(hit.Category.Id))) continue;
                        link = li.Name;
                    }
                    else hit = host;
                    if (hit == null) continue;
                    var gap = (h.Proximity - height) * Ft;
                    var cls = Math.Abs(gap) <= tolMm ? "flush" : gap < 0 ? "embedded" : "separated";
                    return new Dictionary<string, object>
                    {
                        ["id"] = L(e.Id), ["class"] = cls, ["clearance_mm"] = Math.Round(gap, 1), ["support"] = L(hit.Id), ["support_uid"] = hit.UniqueId,
                        ["support_cat"] = hit.Category?.Name, ["link"] = link, ["body"] = body["source"], ["direction"] = up ? "up" : "down",
                        ["rule"] = "vertical ray from the center of the body footprint; tolerance " + tolMm + " mm",
                    };
                }
                return new Dictionary<string, object> { ["id"] = L(e.Id), ["class"] = "no_reference", ["direction"] = up ? "up" : "down", ["max_mm"] = maxMm };
            }

            /// <summary>Revit Transform (feet) → engine Xform (mm) to use ManifestQc/transformations without Revit.</summary>
            public static Xform Transformation(Transform t) => new Xform
            {
                X = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z }, Y = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z },
                Z = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z }, O = new[] { t.Origin.X * Ft, t.Origin.Y * Ft, t.Origin.Z * Ft },
            };
        }

        // =====================================================================================================
        // VIEWS
        // =====================================================================================================
        public static class Views
        {
            private static readonly PlanViewPlane[] Planes = { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane };
            private static string PlaneName(PlanViewPlane p) => p == PlanViewPlane.TopClipPlane ? "top" : p == PlanViewPlane.CutPlane ? "cut" : p == PlanViewPlane.BottomClipPlane ? "bottom" : "depth";

            /// <summary>View range: per plane, level + offset and absolute elevation (mm, project elevation). "unlimited" when applicable.</summary>
            public static Dictionary<string, object> Range(ViewPlan v)
            {
                var doc = v.Document;
                var r = v.GetViewRange();
                var o = new Dictionary<string, object> { ["view"] = L(v.Id), ["name"] = v.Name, ["type"] = v.ViewType.ToString() };
                foreach (var p in Planes)
                {
                    var lid = r.GetLevelId(p);
                    var off = r.GetOffset(p);
                    if (lid == PlanViewRange.Unlimited) { o[PlaneName(p)] = new Dictionary<string, object> { ["level"] = "unlimited" }; continue; }
                    var lvl = lid == PlanViewRange.LevelAbove ? null : lid == PlanViewRange.Current ? v.GenLevel : doc.GetElement(lid) as Level;
                    o[PlaneName(p)] = new Dictionary<string, object>
                    {
                        ["level"] = lid == PlanViewRange.LevelAbove ? "level above" : lvl?.Name, ["offset_mm"] = R(off),
                        ["dimension_mm"] = lvl == null ? (object)null : R(lvl.Elevation + off),
                    };
                }
                var tid = v.ViewTemplateId;
                if (tid != ElementId.InvalidElementId) o["template"] = doc.GetElement(tid)?.Name;
                return o;
            }

            /// <summary>
            /// Changes a range plane ("top"|"cut"|"bottom"|"depth") to level + offset (mm). Fails with a clear message if a
            /// template controls the range. Requires a transaction.
            /// </summary>
            public static void ApplyRange(ViewPlan v, string plane, Level level, double offsetMm)
            {
                var p = Planes.FirstOrDefault(x => PlaneName(x) == plane);
                if (PlaneName(p) != plane) throw new ArgumentException("plane must be top, cut, bottom or depth");
                var tid = v.ViewTemplateId;
                if (tid != ElementId.InvalidElementId && v.Document.GetElement(tid) is View t
                    && !t.GetNonControlledTemplateParameterIds().Contains(new ElementId(BuiltInParameter.PLAN_VIEW_RANGE)))
                    throw new InvalidOperationException("the template '" + t.Name + "' controls the view range of '" + v.Name + "'");
                var r = v.GetViewRange();
                r.SetLevelId(p, level.Id);
                r.SetOffset(p, offsetMm / Ft);
                v.SetViewRange(r);
            }

            /// <summary>Which parameters the view template controls and which it does not (names), to know what can be changed in the view.</summary>
            public static Dictionary<string, object> CompareTemplate(View v)
            {
                var doc = v.Document;
                if (!(doc.GetElement(v.ViewTemplateId) is View t)) return new Dictionary<string, object> { ["view"] = v.Name, ["template"] = null };
                var all = t.GetTemplateParameterIds();
                var free = new HashSet<ElementId>(t.GetNonControlledTemplateParameterIds());
                string name(ElementId id)
                {
                    var bip = Ids.ToLong(id);
                    if (bip < 0) { try { return LabelUtils.GetLabelFor((BuiltInParameter)bip); } catch (Exception) { return bip.ToString(CultureInfo.InvariantCulture); } }
                    return doc.GetElement(id)?.Name ?? bip.ToString(CultureInfo.InvariantCulture);
                }
                return new Dictionary<string, object>
                {
                    ["view"] = v.Name, ["template"] = t.Name,
                    ["controls"] = all.Where(i => !free.Contains(i)).Select(name).OrderBy(s => s).ToList(),
                    ["free_items"] = all.Where(free.Contains).Select(name).OrderBy(s => s).ToList(),
                };
            }

            /// <summary>
            /// Why is <paramref name="e"/> not visible in <paramref name="v"/>? Gathers the facts with the API and lets VisibilityQc decide.
            /// The view's plan regions are listed, but their range is not readable through the public API: if the element falls inside one,
            /// the range verdict is marked as not checkable.
            /// </summary>
            public static Dictionary<string, object> WhyNotVisible(View v, Element e)
            {
                var doc = v.Document;
                var f = new VisibilityQc.Facts();
                var notes = new List<string>();
                try { f.ElementHidden = e.IsHidden(v); } catch (Exception) { }
                if (e.Category != null) try { f.CategoryHidden = v.GetCategoryHidden(e.Category.Id); } catch (Exception) { }
                foreach (var fid in v.GetFilters())
                {
                    try
                    {
                        if (!v.GetIsFilterEnabled(fid) || v.GetFilterVisibility(fid)) continue;
                        var fe = doc.GetElement(fid);
                        if (MatchesFilter(fe, e)) f.HidingFilters.Add(fe.Name);
                    }
                    catch (Exception ex) { notes.Add("filter " + L(fid) + ": " + ex.Message); }
                }
                if (doc.IsWorkshared && e.WorksetId != WorksetId.InvalidWorksetId)
                {
                    var wv = v.GetWorksetVisibility(e.WorksetId);
                    f.WorksetHidden = wv == WorksetVisibility.Hidden
                        || (wv == WorksetVisibility.UseGlobalSetting && !WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc).IsWorksetVisible(e.WorksetId));
                }
                try
                {
                    var phaseId = v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                    var pfId = v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
                    if (phaseId != null && phaseId != ElementId.InvalidElementId && e.HasPhases())
                    {
                        var st = e.GetPhaseStatus(phaseId);
                        f.PhaseHidden = st == ElementOnPhaseStatus.Future || st == ElementOnPhaseStatus.None
                            || (doc.GetElement(pfId) is PhaseFilter pfe && pfe.GetPhaseStatusPresentation(st) == PhaseStatusPresentation.DontShow);
                    }
                    else f.PhaseHidden = false;
                }
                catch (Exception) { f.PhaseHidden = null; }
                f.DesignOptionHidden = e.DesignOption == null ? false : (bool?)null;
                if (e.DesignOption != null) notes.Add("belongs to design option '" + e.DesignOption.Name + "': the API does not say which option the view shows");
                f.Template = doc.GetElement(v.ViewTemplateId)?.Name;
                f.CropActive = v.CropBoxActive;
                var ebb = e.get_BoundingBox(null);
                if (f.CropActive && ebb != null)
                {
                    try
                    {
                        var cb = v.CropBox; var inv = cb.Transform.Inverse;
                        var pts = new[] { ebb.Min, ebb.Max, new XYZ(ebb.Min.X, ebb.Max.Y, ebb.Min.Z), new XYZ(ebb.Max.X, ebb.Min.Y, ebb.Max.Z) }.Select(inv.OfPoint).ToList();
                        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
                        f.InsideCrop = x1 >= cb.Min.X && x0 <= cb.Max.X && y1 >= cb.Min.Y && y0 <= cb.Max.Y;
                    }
                    catch (Exception) { f.InsideCrop = null; }
                }
                var regions = new List<long>();
                if (v is ViewPlan vp)
                {
                    f.PlanKind = vp.ViewType == ViewType.CeilingPlan ? "rcp" : vp.ViewType == ViewType.FloorPlan || vp.ViewType == ViewType.EngineeringPlan ? "floorplan" : null;
                    var rg = Range(vp);
                    double? elevation(string k) => rg.TryGetValue(k, out var x) && x is Dictionary<string, object> d && d.TryGetValue("dimension_mm", out var c) && c is double cd ? cd : (double?)null;
                    f.Top = elevation("top"); f.Cut = elevation("cut"); f.Bottom = elevation("bottom"); f.Depth = elevation("depth");
                    f.RangeSource = "view range";
                    var body = Mounting.Body(e);
                    if (body != null) { f.ZMin = (double)body["zmin"]; f.ZMax = (double)body["zmax"]; }
                    try
                    {
                        var ept = PointOf(e);
                        foreach (var pr in new FilteredElementCollector(doc, v.Id).OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType())
                        {
                            var bb = pr.get_BoundingBox(v);
                            if (ept != null && bb != null && ept.X >= bb.Min.X && ept.X <= bb.Max.X && ept.Y >= bb.Min.Y && ept.Y <= bb.Max.Y) regions.Add(L(pr.Id));
                        }
                    }
                    catch (Exception) { }
                    if (regions.Count > 0)
                    {
                        notes.Add("falls inside the box of plan region(s) " + string.Join(", ", regions) + ": their range is not readable through the public API; the view range verdict may not apply");
                        f.RangeSource = "view range (a plan region overlaps: check by hand)";
                    }
                }
                var reasons = VisibilityQc.Explain(f, out var notCheckable);
                if (regions.Count > 0) foreach (var r in reasons.Where(x => x.Code == "view_range_of")) r.Certain = false;
                return new Dictionary<string, object>
                {
                    ["view"] = v.Name, ["id"] = L(e.Id), ["visible_per_api"] = reasons.Count == 0,
                    ["causes"] = reasons.Select(x => x.ToString()).ToList(), ["not_checkable"] = notCheckable.Concat(notes).ToList(),
                    ["body_mm"] = f.ZMin.HasValue ? new[] { f.ZMin.Value, f.ZMax.Value } : null, ["plan_regions"] = regions,
                };
            }

            internal static bool MatchesFilter(Element filter, Element element)
            {
                if (filter is ParameterFilterElement parameter)
                    return parameter.GetCategories().Contains(element.Category?.Id ?? ElementId.InvalidElementId) &&
                        (parameter.GetElementFilter()?.PassesFilter(element.Document, element.Id) ?? true);
                return filter is SelectionFilterElement selection && selection.GetElementIds().Contains(element.Id);
            }

            /// <summary>
            /// View filter "text parameter = value" for some categories. If one with that name already exists it is returned untouched.
            /// Requires a transaction.
            /// </summary>
            public static ParameterFilterElement FilterByCategory(Document doc, string name, IEnumerable<BuiltInCategory> categories, BuiltInParameter parameter, string value)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(x => x.Name == name);
                if (existing != null) return existing;
                var cats = categories.Select(c => new ElementId(c)).ToList();
                var pid = new ElementId(parameter);
#if REVIT2023_OR_GREATER
                var rule = ParameterFilterRuleFactory.CreateEqualsRule(pid, value);
#else
                var rule = ParameterFilterRuleFactory.CreateEqualsRule(pid, value, false);
#endif
                return ParameterFilterElement.Create(doc, name, cats, new ElementParameterFilter(rule));
            }
        }

        // =====================================================================================================
        // MANIFEST (one-to-one QC against an expected list)
        // =====================================================================================================
        public static class Manifest
        {
            /// <summary>Converts model elements to what ManifestQc compares (mm, rotation from BasisX, level, tags, group).</summary>
            public static List<ManifestQc.Actual> Collect(Document doc, IEnumerable<Element> elems, View tagView = null)
            {
                var list = elems.Where(x => x != null).ToList();
                var tagCount = new Dictionary<ElementId, int>();
                var tags = tagView != null ? new FilteredElementCollector(doc, tagView.Id).OfClass(typeof(IndependentTag)) : new FilteredElementCollector(doc).OfClass(typeof(IndependentTag));
                foreach (IndependentTag t in tags)
                {
                    ICollection<ElementId> ids;
                    try { ids = t.GetTaggedLocalElementIds(); } catch (Exception) { continue; }
                    foreach (var id in ids) tagCount[id] = tagCount.TryGetValue(id, out var n) ? n + 1 : 1;
                }
                var res = new List<ManifestQc.Actual>();
                foreach (var e in list)
                {
                    var actual = Observe(doc, e);
                    if (actual == null) continue;
                    actual.Tags = tagCount.TryGetValue(e.Id, out var count) ? count : 0;
                    res.Add(actual);
                }
                return res;
            }

            internal static ManifestQc.Actual Observe(Document doc, Element e)
            {
                var p = PointOf(e);
                if (p == null) return null;
                double? rot = null;
                if (e is FamilyInstance fi) { var bx = fi.GetTransform().BasisX; rot = ((Math.Atan2(bx.Y, bx.X) * 180 / Math.PI) % 360 + 360) % 360; }
                else if (e.Location is LocationPoint lp) rot = lp.Rotation * 180 / Math.PI;
                var t = doc.GetElement(e.GetTypeId()) as ElementType;
                var lvl = doc.GetElement(e.LevelId) as Level;
                if (lvl == null && e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId() is ElementId li) lvl = doc.GetElement(li) as Level;
                return new ManifestQc.Actual
                {
                    Id = L(e.Id), Uid = e.UniqueId, X = p.X * Ft, Y = p.Y * Ft, Z = p.Z * Ft, RotDeg = rot, Level = lvl?.Name,
                    Family = t?.FamilyName, Type = t?.Name ?? e.Name,
                    Group = e.GroupId != ElementId.InvalidElementId ? (doc.GetElement(e.GroupId) as Group)?.GroupType?.Name : null,
                };
            }

            /// <summary>Full QC: reads the manifest (ManifestQc.Parse JSON), compares and returns the result.</summary>
            public static ManifestQc.Result Qc(Document doc, string manifestJson, IEnumerable<Element> elems, View tagView = null)
            {
                var json = Bimbridge.Protocol.Json.ParseObject(manifestJson);
                var expected = ManifestQc.Parse(json, out var opt);
                return ManifestQc.Run(expected, Collect(doc, elems, tagView), opt);
            }
        }
    }
}
