// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Bimbridge.Engine.Geo;

namespace Bimbridge.Addin.Revit.Geo
{
    /// <summary>
    /// One "geo" check from start to finish. On the Revit thread it only gathers, in resumable slices:
    ///   0 start: document, rev, links and result cache (if nothing changed, it ends here with 0 ms of computation)
    ///   1 checked elements: box, mesh (if the recipe measures bodies), level and type
    ///   2 index of what is around, per category and model (reused across checks)
    ///   3 candidates for each element (plan grid, no API calls)
    ///   4 candidate meshes (once per element; link meshes are cached while the links stay loaded)
    ///   5 extras: levels, views, rooms and spaces
    /// Computation (rays, classes) runs off the Revit thread and in parallel. Only "choques" goes back to Revit for the
    /// exact boolean of the pairs the rays could not decide.
    /// </summary>
    internal sealed class GeoRunner
    {
        private enum Stage { Init, Targets, Index, Candidates, Meshes, Levels, Views, Rooms, Done }

        public readonly GeoSpec Spec;
        private readonly IReadOnlyCollection<long> _explicit;
        private readonly DocumentTracker _tracker;
        private readonly ResultCache _cache;
        private readonly Job _job;

        // ---- gather output
        public string Error;
        public long Rev;
        public string DocKey;
        public string DocTitle;
        public string Signature;
        public string CacheKey;
        public ResultCache.Entry CacheHit;
        public readonly List<GeoTarget> Targets = new List<GeoTarget>();
        public readonly List<GeoBody> Bodies = new List<GeoBody>();
        public readonly List<GeoLevel> Levels = new List<GeoLevel>();
        public readonly List<GeoView> Views = new List<GeoView>();
        public readonly List<string> Warnings = new List<string>();
        public readonly GeoCompute.Stats Stats = new GeoCompute.Stats();
        public readonly Dictionary<string, long> StageMs = new Dictionary<string, long>();
        public Dictionary<string, object> IndexInfo;
        public int MeshesNew, MeshesReused;

        // ---- compute output
        public List<Dictionary<string, object>> Rows;
        public List<string> Cols;
        public string ClassField = "clase";
        public Dictionary<string, string> SetNames = new Dictionary<string, string>();
        public long CalcMs;

        // ---- internal state
        private Stage _stage = Stage.Init;
        private Document _doc;
        private GeoIndex _index;
        private VersionRegistry _reg;
        private List<GeoSource> _sources = new List<GeoSource>();
        private List<BuiltInCategory> _against = new List<BuiltInCategory>();
        private List<BuiltInCategory> _targetCats = new List<BuiltInCategory>();
        private List<ElementId> _targetIds;
        private readonly List<ElementId> _targetElemIds = new List<ElementId>();
        private int _next;
        private int _missing;
        private readonly Dictionary<GeoItem, int> _bodyIndex = new Dictionary<GeoItem, int>();
        private readonly List<GeoItem> _items = new List<GeoItem>();
        private int _meshNext;
        private readonly Dictionary<long, Tuple<string, double>> _levelCache = new Dictionary<long, Tuple<string, double>>();
        private List<GeoSource> _roomSources;
        private bool _hostSpaces;
        private int _meshesBuiltAtStart, _reusedAtStart;

        // ---- clashes
        private List<Dictionary<string, object>> _screened;
        private readonly List<GeoCompute.PairToCheck> _pairs = new List<GeoCompute.PairToCheck>();
        private readonly List<GeoCompute.ExactResult> _exact = new List<GeoCompute.ExactResult>();
        private int _exactNext;
        private readonly Dictionary<(int Target, long Link), (List<Solid> Solids, GeoCompute.ExactIssue Issues)> _targetSolids =
            new Dictionary<(int, long), (List<Solid>, GeoCompute.ExactIssue)>();

        /// <summary>
        /// place with montaje: measures families that do not exist yet. Takes the body of an existing instance of the same type and moves
        /// (and rotates) it to each requested point, at <see cref="VirtualPlacement.Nominal"/> above the level. Targets get id -(i+1).
        /// </summary>
        public sealed class VirtualPlacement
        {
            public long TemplateId;
            public List<double[]> Points = new List<double[]>();
            public string Level;
            public double LevelElev;
            public double Nominal;
            public double RotDeg;
        }

        public VirtualPlacement Virtual;

        public GeoRunner(GeoSpec spec, IReadOnlyCollection<long> explicitIds, DocumentTracker tracker, ResultCache cache, Job job)
        {
            Spec = spec;
            _explicit = explicitIds;
            _tracker = tracker;
            _cache = cache;
            _job = job;
        }

        private bool MeasuresBodies => Spec.Recipe == "montaje" || Spec.Recipe == "holgura" || Spec.Recipe == "choques";
        private bool UsesIndex => MeasuresBodies || Spec.Recipe == "cerca" || Spec.Recipe == "muro";
        public bool NeedsExact => Spec.Recipe == "choques" && _pairs.Count > 0;
        public bool ModelChanged => _reg != null && _reg.Rev != Rev;

        private void Phase(string p)
        {
            if (_job != null) _job.Phase = p;
        }

        // =====================================================================================================
        // Gather (Revit thread)
        // =====================================================================================================

        /// <summary>One slice of work on the Revit thread. true = finished (or failed, or the cache already had the answer).</summary>
        public bool Gather(UIApplication app, Stopwatch sw, int budgetMs)
        {
            var deadline = sw.ElapsedMilliseconds + budgetMs;
            if (_stage != Stage.Init && (_doc == null || !_doc.IsValidObject))
            {
                Error = "the model was closed during the check";
                return true;
            }
            while (_stage != Stage.Done)
            {
                var t0 = sw.ElapsedMilliseconds;
                var stage = _stage;
                bool finished;
                switch (_stage)
                {
                    case Stage.Init: finished = Init(app); break;
                    case Stage.Targets: finished = ReadTargets(sw, deadline); break;
                    case Stage.Index:
                        finished = _index.Ensure(_sources, _against, Rev, sw, deadline, Phase);
                        break;
                    case Stage.Candidates: finished = FindCandidates(sw, deadline); break;
                    case Stage.Meshes: finished = BuildMeshes(sw, deadline); break;
                    case Stage.Levels: finished = ReadLevels(); break;
                    case Stage.Views: finished = ReadViews(); break;
                    case Stage.Rooms: finished = ReadRooms(sw, deadline); break;
                    default: finished = true; break;
                }
                var key = stage.ToString();
                StageMs[key] = (StageMs.TryGetValue(key, out var ms) ? ms : 0) + (sw.ElapsedMilliseconds - t0);
                if (Error != null || CacheHit != null || IndexInfo != null) return true;
                if (!finished) return false;
                _stage = NextStage(stage);
                if (_stage != Stage.Done && sw.ElapsedMilliseconds >= deadline) return false;
            }
            MeshesNew = _index != null ? _index.MeshesBuilt - _meshesBuiltAtStart : 0;
            MeshesReused = _index != null ? _index.HostReused - _reusedAtStart : 0;
            return true;
        }

        private Stage NextStage(Stage s)
        {
            switch (s)
            {
                case Stage.Init: return Stage.Targets;
                case Stage.Targets: return UsesIndex ? Stage.Index : Stage.Levels;
                case Stage.Index: return Stage.Candidates;
                case Stage.Candidates: return Stage.Meshes;
                case Stage.Meshes: return Stage.Levels;
                case Stage.Levels: return Spec.Recipe == "rango_vista" ? Stage.Views : Spec.Recipe == "cuarto" ? Stage.Rooms : Stage.Done;
                default: return Stage.Done;
            }
        }

        private bool Init(UIApplication app)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (doc.IsFamilyDocument) { Error = Status.NotSupported + ": geo checks project models (montaje, choques, ranges); the target is a family. For faces and connectors use fam caras / fam info, or exec"; return true; }
            _doc = doc;
            DocKey = DocumentTracker.KeyOf(doc);
            DocTitle = doc.Title;
            _reg = _tracker.For(doc);
            Rev = _reg?.Rev ?? 0;
            if (Spec.Rebuild) GeoService.Reset(doc);
            _index = GeoService.For(doc);
            _meshesBuiltAtStart = _index.MeshesBuilt;
            _reusedAtStart = _index.HostReused;

            if (Spec.Recipe == "indice")
            {
                IndexInfo = _index.Stats();
                return true;
            }

            foreach (var name in Spec.Categories)
            {
                if (!QueryRunner.TryCategory(name, out var bic)) { Error = UnknownCategory(name); return true; }
                _targetCats.Add(bic);
            }
            if (UsesIndex)
            {
                foreach (var name in Spec.Against)
                {
                    if (!QueryRunner.TryCategory(name, out var bic)) { Error = UnknownCategory(name); return true; }
                    _against.Add(bic);
                }
            }
            if (UsesIndex || Spec.Recipe == "cuarto")
            {
                _sources = GeoIndex.Sources(doc, Spec.IncludeHost || Spec.Recipe == "cuarto", Spec.IncludeLinks, Spec.Links, Warnings);
                _index.Prune(_sources);
                if (UsesIndex && _sources.Count == 0) { Error = "Nothing to measure against: no host and no loaded links matching vinculos=."; return true; }
            }
            Signature = GeoIndex.Signature(_sources);

            // Cache: same rev, same links and same arguments = same answer (0 ms of computation).
            var args = Spec.CacheArgs();
            if (_explicit != null && _explicit.Count > 0) args["idsx"] = IdsDigest(_explicit);
            CacheKey = ResultCache.Key(DocKey, Rev, Signature, Spec.Recipe, args);
            if (_cache != null && !Spec.Rebuild && Virtual == null && _cache.TryGet(CacheKey, out var hit))
            {
                CacheHit = hit;
                return true;
            }

            // Elements to check
            if (Virtual != null)
                _targetIds = new List<ElementId>();
            else if (_explicit != null && _explicit.Count > 0)
                _targetIds = _explicit.Distinct().Select(Ids.ToId).ToList();
            else
            {
                var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                _targetIds = (_targetCats.Count == 1 ? col.OfCategory(_targetCats[0]) : col.WherePasses(new ElementMulticategoryFilter(_targetCats)))
                    .ToElementIds().ToList();
            }
            if (_job != null) _job.Total = _targetIds.Count;
            Phase("reading " + _targetIds.Count + " elements");
            return true;
        }

        private static string UnknownCategory(string name) =>
            "unknown category: " + name + " (use API names such as LightingFixtures, Floors, StructuralFraming, or aliases: losas, vigas, columnas, rampas, escaleras, muros)";

        private static string IdsDigest(IEnumerable<long> ids)
        {
            unchecked
            {
                long h = 1469598103934665603L, n = 0;
                foreach (var id in ids.OrderBy(x => x)) { h = (h ^ id) * 1099511628211L; n++; }
                return n.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString("x", CultureInfo.InvariantCulture);
            }
        }

        private bool ReadTargets(Stopwatch sw, long deadline)
        {
            if (Virtual != null) return ReadVirtual();
            var needMesh = MeasuresBodies;
            var skip = Spec.ExcludeSubcategories;
            var ws = _doc.IsWorkshared && Spec.Worksets.Count > 0 ? _doc.GetWorksetTable() : null;
            while (_next < _targetIds.Count)
            {
                var id = _targetIds[_next++];
                if (_job != null) _job.Done = _next;
                Element e;
                try { e = _doc.GetElement(id); } catch (Exception) { e = null; }
                if (e == null || e is ElementType) { _missing++; continue; }
                var lvl = LevelOf(e);
                if (Spec.Levels.Count > 0 && !Spec.Levels.Any(l => string.Equals(l, lvl?.Item1, StringComparison.OrdinalIgnoreCase))) continue;
                var typeKey = TypeKey(e);
                if (Spec.Types.Count > 0 && !Spec.Types.Any(t => typeKey.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (ws != null)
                {
                    string wsName = null;
                    try { wsName = ws.GetWorkset(e.WorksetId)?.Name; } catch (Exception) { }
                    if (!Spec.Worksets.Any(w => string.Equals(w, wsName, StringComparison.OrdinalIgnoreCase))) continue;
                }

                var body = _index.TargetBody(e, _reg, Rev, skip, needMesh);
                var box = body.Box;
                if (box.IsEmpty) { _missing++; continue; }
                var t = new GeoTarget
                {
                    Id = Ids.ToLong(e.Id),
                    Level = lvl?.Item1,
                    LevelElev = lvl?.Item2 ?? double.NaN,
                    TypeKey = typeKey,
                    Category = e.Category?.Name,
                    Box = box,
                    Body = body.Mesh != null && !body.Mesh.IsEmpty ? body.Mesh : null,
                };
                var p = PointOf(e, box);
                t.X = p.X; t.Y = p.Y; t.Z = p.Z;
                Targets.Add(t);
                _targetElemIds.Add(e.Id);
                if (body.Parts != null) foreach (var part in body.Parts) _parts[part] = (_parts.TryGetValue(part, out var c) ? c : 0) + 1;
                if ((_next & 15) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
            }
            if (_missing > 0) Warnings.Add(_missing + " ids do not exist in the model or have no geometry");
            if (Targets.Count == 0 && Error == null)
                Error = "No elements to check with those filters (cat, lvl, ids, ws, type).";
            return true;
        }

        private readonly Dictionary<string, int> _parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Virtual bodies: the reference instance's mesh, rotated and moved to each point (without creating anything in Revit).</summary>
        private bool ReadVirtual()
        {
            var v = Virtual;
            Element e;
            try { e = _doc.GetElement(Ids.ToId(v.TemplateId)); } catch (Exception) { e = null; }
            if (e == null) { Error = "place montaje: reference instance " + v.TemplateId + " no longer exists"; return true; }
            if (!(e.Location is LocationPoint lp) || lp.Point == null) { Error = "place montaje: the reference instance has no insertion point"; return true; }
            var builder = new TriMeshBuilder();
            var solids = new List<Solid>();
            try
            {
                var ge = e.get_Geometry(global::Bimbridge.Addin.Revit.Geo.Bodies.NewOptions());
                if (ge != null) global::Bimbridge.Addin.Revit.Geo.Bodies.CollectSolids(ge, solids, _doc, Spec.ExcludeSubcategories);
                foreach (var so in solids) global::Bimbridge.Addin.Revit.Geo.Bodies.AddSolid(so, builder, null);
            }
            catch (Exception ex) { Error = "place montaje: could not read the type's body (" + ex.Message + "); give elev"; return true; }
            if (builder.Count == 0) { Error = "place montaje: the type has no measurable solid body; give elev"; return true; }
            var origin = lp.Point;
            double rot0;
            try { rot0 = lp.Rotation; } catch (Exception) { rot0 = 0; }
            var d = v.RotDeg * Math.PI / 180.0 - rot0;
            double cos = Math.Cos(d), sin = Math.Sin(d);
            var typeKey = TypeKey(e);
            for (var i = 0; i < v.Points.Count; i++)
            {
                var p = v.Points[i];
                var z = v.LevelElev + v.Nominal;
                var mesh = builder.BuildMoved(origin.X, origin.Y, cos, sin, p[0] - origin.X, p[1] - origin.Y, z - origin.Z);
                Targets.Add(new GeoTarget
                {
                    Id = -(i + 1),
                    Level = v.Level,
                    LevelElev = v.LevelElev,
                    TypeKey = typeKey,
                    Category = e.Category?.Name,
                    Box = mesh.Bounds,
                    Body = mesh,
                    X = p[0], Y = p[1], Z = z,
                });
            }
            Warnings.Add("measured with the body of instance " + v.TemplateId + " of the same type (instance parameters such as length can change it)");
            return true;
        }

        private Tuple<string, double> LevelOf(Element e)
        {
            ElementId id = null;
            try
            {
                id = e.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                            ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                            ?? e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    id = p?.AsElementId();
                }
            }
            catch (Exception) { id = null; }
            if (id == null || id == ElementId.InvalidElementId) return null;
            var key = Ids.ToLong(id);
            if (_levelCache.TryGetValue(key, out var hit)) return hit;
            var lv = _doc.GetElement(id) as Level;
            var r = lv == null ? null : Tuple.Create(lv.Name, lv.ProjectElevation);
            _levelCache[key] = r;
            return r;
        }

        private string TypeKey(Element e)
        {
            try
            {
                if (e is FamilyInstance fi && fi.Symbol != null) return fi.Symbol.FamilyName + ": " + fi.Symbol.Name;
                var t = _doc.GetElement(e.GetTypeId()) as ElementType;
                return t != null ? t.FamilyName + ": " + t.Name : e.Name ?? "";
            }
            catch (Exception) { return ""; }
        }

        private static XYZ PointOf(Element e, Box3 box)
        {
            try
            {
                if (e.Location is LocationPoint lp && lp.Point != null) return lp.Point;
                if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            }
            catch (Exception) { }
            return new XYZ(box.CenterX, box.CenterY, box.CenterZ);
        }

        /// <summary>Box searched for candidates for an element, depending on the recipe.</summary>
        private Box3 ProbeBox(GeoTarget t)
        {
            var b = t.Box;
            switch (Spec.Recipe)
            {
                case "montaje":
                case "holgura":
                    return Spec.Up
                        ? new Box3(b.MinX, b.MinY, b.MinZ - GeoCompute.BelowMargin, b.MaxX, b.MaxY, b.MaxZ + Spec.MaxRange)
                        : new Box3(b.MinX, b.MinY, b.MinZ - Spec.MaxRange, b.MaxX, b.MaxY, b.MaxZ + GeoCompute.BelowMargin);
                case "choques":
                    return b.Grow(Spec.Tolerance, Spec.Tolerance);
                case "cerca":
                    return new Box3(b.MinX - Spec.Radius, b.MinY - Spec.Radius, b.MinZ - Spec.Radius, b.MaxX + Spec.Radius, b.MaxY + Spec.Radius, b.MaxZ + Spec.MaxRange);
                case "muro":
                    return b.Grow(Spec.Radius, Spec.Tolerance);
                default:
                    return b;
            }
        }

        private bool FindCandidates(Stopwatch sw, long deadline)
        {
            Phase("searching around " + Targets.Count + " elements");
            for (; _candNext < Targets.Count; _candNext++)
            {
                var t = Targets[_candNext];
                var found = _index.Query(_sources, _against, ProbeBox(t));
                var list = new List<int>(found.Count);
                foreach (var item in found)
                {
                    if (item.Source.IsHost && item.Id == t.Id) continue; // itself
                    if (!_bodyIndex.TryGetValue(item, out var idx))
                    {
                        idx = Bodies.Count;
                        _bodyIndex[item] = idx;
                        Bodies.Add(item.Body);
                        _items.Add(item);
                    }
                    if (!list.Contains(idx)) list.Add(idx);
                }
                t.Candidates = list.ToArray();
                if ((_candNext & 63) == 63 && sw.ElapsedMilliseconds >= deadline) { _candNext++; return false; }
            }
            if (_job != null) { _job.Done = 0; _job.Total = _items.Count; }
            return true;
        }

        private int _candNext;

        private bool BuildMeshes(Stopwatch sw, long deadline)
        {
            var needMesh = MeasuresBodies;
            Phase(needMesh ? "triangulating " + _items.Count + " surrounding elements (once; reused afterwards)" : "reading " + _items.Count + " surrounding elements");
            while (_meshNext < _items.Count)
            {
                var item = _items[_meshNext++];
                if (_job != null) _job.Done = _meshNext;
                if (needMesh) _index.EnsureMesh(item, _reg, Rev, null);
                if (Spec.Recipe == "muro") _index.EnsureWallLine(item);
                if (item.Body.Label == null) item.Body.Label = GeoIndex.LabelOf(item);
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        private List<Level> _levelElems;

        private bool ReadLevels()
        {
            if (Spec.Recipe != "nivel_real" && Spec.Recipe != "rango_vista") return true;
            _levelElems = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            foreach (var lv in _levelElems) Levels.Add(new GeoLevel { Name = lv.Name, Elevation = lv.ProjectElevation });
            return true;
        }

        // ---------------- rango_vista ----------------

        private bool ReadViews()
        {
            var plans = ResolveViews();
            if (Error != null) return true;
            // Plan regions: a single collector for the whole model, grouped by view (without regenerating views).
            var regions = new Dictionary<long, List<Box3>>();
            foreach (var r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType())
            {
                var owner = Ids.ToLong(r.OwnerViewId);
                BoundingBoxXYZ bb;
                try { bb = r.get_BoundingBox(_doc.GetElement(r.OwnerViewId) as View); } catch (Exception) { bb = null; }
                if (bb == null) continue;
                var box = GeoIndex.ToHostBox(bb, null);
                if (!regions.TryGetValue(owner, out var l)) regions[owner] = l = new List<Box3>();
                l.Add(box);
            }
            foreach (var v in plans)
            {
                var gv = DescribeView(v);
                if (gv == null) continue;
                if (regions.TryGetValue(gv.Id, out var rl)) gv.Regions.AddRange(rl);
                Views.Add(gv);
            }
            if (Views.Count == 0) Error = "No plans to check with views=" + Spec.Views + (Spec.Levels.Count > 0 ? " and those levels" : "");
            return true;
        }

        private List<ViewPlan> ResolveViews()
        {
            var arg = (Spec.Views ?? "rcp").Trim();
            var all = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();
            IEnumerable<ViewPlan> chosen;
            if (arg.Equals("rcp", StringComparison.OrdinalIgnoreCase) || arg.Equals("rcp_todos", StringComparison.OrdinalIgnoreCase))
            {
                chosen = all.Where(v => v.ViewType == ViewType.CeilingPlan);
                if (arg.Equals("rcp", StringComparison.OrdinalIgnoreCase))
                {
                    // Lighting plans: those that already have lighting fixture tags (not the 261 power, alarm… plans).
                    var withTags = new HashSet<long>(new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_LightingFixtureTags)
                        .WhereElementIsNotElementType().Select(t => Ids.ToLong(t.OwnerViewId)));
                    chosen = chosen.Where(v => withTags.Contains(Ids.ToLong(v.Id)));
                }
            }
            else if (arg.Equals("plantas", StringComparison.OrdinalIgnoreCase))
                chosen = all.Where(v => v.ViewType == ViewType.FloorPlan);
            else
            {
                var list = new List<ViewPlan>();
                foreach (var part in arg.Split(','))
                {
                    if (!long.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    { Error = "views: rcp, rcp_todos, plantas or comma-separated view ids"; return list; }
                    if (!(_doc.GetElement(Ids.ToId(id)) is ViewPlan vp))
                    { Error = "views: " + id + " is neither a floor plan nor a ceiling plan"; return list; }
                    list.Add(vp);
                }
                chosen = list;
            }
            if (Spec.Levels.Count > 0)
                chosen = chosen.Where(v => Spec.Levels.Any(l => string.Equals(l, v.GenLevel?.Name, StringComparison.OrdinalIgnoreCase)));
            return chosen.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private GeoView DescribeView(ViewPlan v)
        {
            PlanViewRange r;
            try { r = v.GetViewRange(); } catch (Exception) { return null; }
            if (r == null) return null;
            var gen = v.GenLevel;
            double Abs(PlanViewPlane plane, double unlimited)
            {
                ElementId lid;
                double off;
                try { lid = r.GetLevelId(plane); off = r.GetOffset(plane); } catch (Exception) { return unlimited; }
                if (lid == null || lid == ElementId.InvalidElementId || lid == PlanViewRange.Unlimited) return unlimited;
                Level lv;
                if (lid == PlanViewRange.Current) lv = gen;
                else if (lid == PlanViewRange.LevelAbove) lv = Neighbor(gen, +1);
                else if (lid == PlanViewRange.LevelBelow) lv = Neighbor(gen, -1);
                else lv = _doc.GetElement(lid) as Level;
                if (lv == null) return unlimited;
                return lv.ProjectElevation + off;
            }
            var gv = new GeoView
            {
                Id = Ids.ToLong(v.Id),
                Name = v.Name,
                Level = gen?.Name,
                Ceiling = v.ViewType == ViewType.CeilingPlan,
                Cut = Abs(PlanViewPlane.CutPlane, double.NaN),
                Top = Abs(PlanViewPlane.TopClipPlane, 1e6),
                Bottom = Abs(PlanViewPlane.BottomClipPlane, -1e6),
            };
            // In ceiling plans the "depth" looks up (to the top); in floor plans, down.
            gv.Depth = Abs(PlanViewPlane.ViewDepthPlane, gv.Ceiling ? 1e6 : -1e6);
            try
            {
                if (v.CropBoxActive && v.CropBox != null)
                {
                    var c = GeoIndex.ToHostBox(v.CropBox, null);
                    gv.Crop = new Box3(c.MinX, c.MinY, -1e6, c.MaxX, c.MaxY, 1e6);
                }
            }
            catch (Exception) { }
            return gv;
        }

        /// <summary>Level immediately above (dir=+1) or below (dir=-1) by elevation.</summary>
        private Level Neighbor(Level gen, int dir)
        {
            if (gen == null) return null;
            if (_levelElems == null) ReadLevels();
            var z = gen.ProjectElevation;
            return dir > 0 ? _levelElems.FirstOrDefault(l => l.ProjectElevation > z + 1e-6)
                           : _levelElems.LastOrDefault(l => l.ProjectElevation < z - 1e-6);
        }

        // ---------------- room ----------------

        private bool ReadRooms(Stopwatch sw, long deadline)
        {
            if (_roomSources == null)
            {
                _roomSources = new List<GeoSource>();
                var wantRooms = Spec.Source != "espacios";
                _hostSpaces = Spec.Source != "cuartos" && Count(_doc, BuiltInCategory.OST_MEPSpaces) > 0;
                if (wantRooms)
                    foreach (var s in _sources)
                        if (Count(s.Doc, BuiltInCategory.OST_Rooms) > 0) _roomSources.Add(s);
                if (_roomSources.Count == 0 && !_hostSpaces)
                {
                    Warnings.Add("there are no rooms (neither in the model nor in loaded links) and no MEP spaces: everything will come out sin_cuarto");
                    return true;
                }
                _next = 0;
                Phase("finding the room of " + Targets.Count + " elements");
            }
            while (_next < Targets.Count)
            {
                var t = Targets[_next++];
                if (_job != null) _job.Done = _next;
                var pts = new List<XYZ> { new XYZ(t.Box.CenterX, t.Box.CenterY, t.Box.CenterZ) };
                // A fixture against the slab can sit above the room's upper limit: also try 1 ft above the floor.
                if (!double.IsNaN(t.LevelElev)) pts.Add(new XYZ(t.Box.CenterX, t.Box.CenterY, t.LevelElev + 1.0));
                foreach (var p in pts)
                {
                    if (t.Space == null && _hostSpaces)
                    {
                        try { t.Space = Label(_doc.GetSpaceAtPoint(p)); } catch (Exception) { }
                    }
                    if (t.Room == null)
                        foreach (var s in _roomSources)
                        {
                            try { t.Room = Label(s.Doc.GetRoomAtPoint(s.Identity ? p : s.ToLocal.OfPoint(p))); } catch (Exception) { }
                            if (t.Room != null) { if (!s.IsHost) t.Room = s.Short + ":" + t.Room; break; }
                        }
                    if ((t.Room != null || _roomSources.Count == 0) && (t.Space != null || !_hostSpaces)) break;
                }
                if ((_next & 15) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        private static int Count(Document d, BuiltInCategory c)
        {
            try { return new FilteredElementCollector(d).OfCategory(c).WhereElementIsNotElementType().GetElementCount(); }
            catch (Exception) { return 0; }
        }

        private static string Label(SpatialElement s)
        {
            if (s == null) return null;
            string number = null, name = null;
            try { number = s.Number; } catch (Exception) { }
            try { name = s.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(); } catch (Exception) { }
            var l = ((number ?? "") + " " + (name ?? "")).Trim();
            return l.Length == 0 ? Ids.ToLong(s.Id).ToString(CultureInfo.InvariantCulture) : l;
        }

        // =====================================================================================================
        // Compute (off the Revit thread)
        // =====================================================================================================

        public void Compute()
        {
            var sw = Stopwatch.StartNew();
            switch (Spec.Recipe)
            {
                case "montaje":
                case "holgura":
                    Rows = GeoCompute.Mount(Targets, Bodies, Spec, Stats);
                    Cols = Spec.Points > 1
                        ? new List<string> { "id", "lvl", "clase", "gap", "gap_max", "pts", "contra", "pend", "cuerpo" }
                        : new List<string> { "id", "lvl", "clase", "gap", "contra", "cuerpo" };
                    if (Spec.SurfaceReference)
                        Cols.AddRange(new[] { "elemento_id", "vinculo_id", "cara_x", "cara_y", "cara_z", "cara_lado" });
                    SetNames = new Dictionary<string, string> { [MountClass.Embedded] = "$empotradas", [MountClass.Floating] = "$flotando", [MountClass.NoSurface] = "$sin_superficie" };
                    break;
                case "choques":
                    _screened = GeoCompute.ClashScreen(Targets, Bodies, Spec, _pairs, Stats);
                    Cols = new List<string> { "id", "lvl", "clase", "contra", "prof", "vol", "n", "metodo" };
                    SetNames = new Dictionary<string, string> { ["choque"] = "$choques", ["no_evaluable"] = "$no_evaluables" };
                    if (_pairs.Count == 0) Rows = GeoCompute.MergeClashes(Targets, Bodies, _screened, null, Spec.MinVolume);
                    break;
                case "cerca":
                    Rows = GeoCompute.Near(Targets, Bodies, Spec);
                    Cols = new List<string> { "id", "lvl", "clase", "otro", "dist_xy", "dz", "n" };
                    SetNames = new Dictionary<string, string> { ["choque"] = "$cerca_choque", ["encima"] = "$cerca_encima", ["debajo"] = "$cerca_debajo", ["cerca"] = "$cerca" };
                    break;
                case "muro":
                    Rows = GeoCompute.Wall(Targets, Bodies, Spec);
                    Cols = new List<string> { "id", "lvl", "clase", "dist", "muro", "lado" };
                    SetNames = new Dictionary<string, string> { ["penetra"] = "$penetra_muro", ["separada"] = "$separadas_muro", ["sin_muro"] = "$sin_muro" };
                    break;
                case "nivel_real":
                    Rows = GeoCompute.RealLevel(Targets, Levels);
                    Cols = new List<string> { "id", "lvl", "nivel_real", "clase", "z", "dz" };
                    SetNames = new Dictionary<string, string> { ["distinto"] = "$nivel_distinto" };
                    break;
                case "duplicados":
                    Rows = GeoCompute.Duplicates(Targets, Spec);
                    Cols = new List<string> { "id", "lvl", "clase", "igual_a", "dist" };
                    SetNames = new Dictionary<string, string> { ["sobrante"] = "$duplicados" };
                    break;
                case "cuarto":
                    Rows = GeoCompute.Rooms(Targets, Spec.Source);
                    Cols = Spec.Source == "espacios" ? new List<string> { "id", "lvl", "clase", "espacio" }
                         : Spec.Source == "cuartos" ? new List<string> { "id", "lvl", "clase", "cuarto" }
                         : new List<string> { "id", "lvl", "clase", "cuarto", "espacio" };
                    SetNames = new Dictionary<string, string> { ["sin_cuarto"] = "$sin_cuarto" };
                    break;
                case "rango_vista":
                    Rows = GeoCompute.ViewRange(Targets, Views);
                    Cols = new List<string> { "vista", "id", "lvl", "clase", "zbot", "ztop", "bajo", "alto" };
                    SetNames = new Dictionary<string, string>
                    {
                        [ViewBand.Above] = "$rango_encima", [ViewBand.Below] = "$rango_debajo", ["ajena"] = "$ajenas_en_vista", ["fuera"] = "$fuera_de_recorte",
                    };
                    break;
            }
            CalcMs += sw.ElapsedMilliseconds;
        }

        // =====================================================================================================
        // Clashes: exact boolean of the doubtful pairs (Revit thread)
        // =====================================================================================================

        public bool ExactStep(UIApplication app, Stopwatch sw, int budgetMs)
        {
            var deadline = sw.ElapsedMilliseconds + budgetMs;
            if (_doc == null || !_doc.IsValidObject) { Error = "the model was closed during the check"; return true; }
            Phase("exact boolean of " + _pairs.Count + " doubtful pairs");
            if (_job != null) _job.Total = _pairs.Count;
            while (_exactNext < _pairs.Count)
            {
                var p = _pairs[_exactNext++];
                if (_job != null) _job.Done = _exactNext;
                _exact.Add(Intersect(p));
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        private GeoCompute.ExactResult Intersect(GeoCompute.PairToCheck p)
        {
            var r = new GeoCompute.ExactResult { Target = p.Target, Body = p.Body };
            var item = _items[p.Body];
            var mine = TargetSolids(p.Target, item.Source);
            var other = _index.SolidsOf(item, null, out var extractionFailed);
            r.Issues = mine.Issues;
            if (extractionFailed) r.Issues |= GeoCompute.ExactIssue.Extraction;
            if (mine.Solids.Count == 0 || other.Count == 0)
            {
                if (r.Issues == GeoCompute.ExactIssue.None) r.Issues = GeoCompute.ExactIssue.NoSolid;
                return r;
            }
            foreach (var a in mine.Solids)
                foreach (var b in other)
                {
                    try
                    {
                        var x = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
                        if (x == null) r.Issues |= GeoCompute.ExactIssue.Boolean;
                        else r.Volume += x.Volume;
                    }
                    catch (Exception) { r.Issues |= GeoCompute.ExactIssue.Boolean; }
                }
            return r;
        }

        /// <summary>Solids of the checked element, mapped to the other model's coordinates (links have their own origin).</summary>
        private (List<Solid> Solids, GeoCompute.ExactIssue Issues) TargetSolids(int target, GeoSource src)
        {
            var key = (target, src.LinkId);
            if (_targetSolids.TryGetValue(key, out var cached)) return cached;
            var hostKey = (target, 0L);
            if (!_targetSolids.TryGetValue(hostKey, out var host))
            {
                var solids = _index.ReadSolids(() => _doc.GetElement(_targetElemIds[target]), Spec.ExcludeSubcategories, out var failed);
                host = (solids, failed ? GeoCompute.ExactIssue.Extraction : GeoCompute.ExactIssue.None);
                _targetSolids[hostKey] = host;
            }
            if (src.Identity) return _targetSolids[key] = host;
            var transformed = new List<Solid>(host.Solids.Count);
            var issues = host.Issues;
            foreach (var s in host.Solids)
            {
                try { transformed.Add(SolidUtils.CreateTransformed(s, src.ToLocal)); }
                catch (Exception) { issues |= GeoCompute.ExactIssue.Transform; }
            }
            return _targetSolids[key] = (transformed, issues);
        }

        public void FinishClashes()
        {
            if (Spec.Recipe != "choques" || Rows != null) return;
            var sw = Stopwatch.StartNew();
            Rows = GeoCompute.MergeClashes(Targets, Bodies, _screened ?? new List<Dictionary<string, object>>(), _exact, Spec.MinVolume);
            CalcMs += sw.ElapsedMilliseconds;
        }

        // =====================================================================================================
        // Summary
        // =====================================================================================================

        /// <summary>Raw data cached to re-summarize (another page, units or filter) without recomputing.</summary>
        public sealed class Outcome
        {
            public List<Dictionary<string, object>> Rows;
            public List<string> Cols;
            public string ClassField;
            public Dictionary<string, string> SetNames;
            public Dictionary<string, object> Extra;
        }

        public Outcome ToOutcome()
        {
            var extra = new Dictionary<string, object>();
            if (_sources.Any(s => !s.IsHost)) extra["fuentes"] = string.Join("+", _sources.Select(s => s.IsHost ? "host" : s.Short));
            if (Stats.Rays > 0) extra["rayos"] = (long)Stats.Rays;
            if (Stats.Inconsistent > 0)
                extra["rayos_dudosos"] = (long)Stats.Inconsistent + " (open or odd mesh at that point: not used for the height)";
            if (Stats.BoxFallback > 0) extra["sin_malla"] = (long)Stats.BoxFallback + " elements measured with their box (cuerpo=caja)";
            if (_pairs.Count > 0) extra["booleanas"] = (long)_pairs.Count;
            if (_parts.Count > 1)
                extra["partes_cuerpo"] = _parts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)(long)kv.Value);
            var warn = Warnings.Distinct().Take(6).Cast<object>().ToList();
            if (warn.Count > 0) extra["avisos"] = warn;
            return new Outcome { Rows = Rows ?? new List<Dictionary<string, object>>(), Cols = Cols ?? new List<string> { "id", "clase" }, ClassField = ClassField, SetNames = SetNames, Extra = extra };
        }

        public static Dictionary<string, object> Summarize(GeoSpec spec, Outcome o, out Dictionary<string, long[]> sets)
        {
            var d = GeoSummary.Build(spec, o.Rows, o.Cols, o.ClassField, o.SetNames, out sets);
            if (o.Extra != null) foreach (var kv in o.Extra) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
