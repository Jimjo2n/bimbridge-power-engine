// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Engine.Geo;

namespace Bimbridge.Addin.Revit.Geo
{
    /// <summary>A model searched for geometry: the host or a loaded link, with its transform.</summary>
    internal sealed class GeoSource
    {
        public long LinkId;              // 0 = host
        public string Name;              // "host" or the link type name ("ARCH.rvt")
        public string Short;             // short label for tables ("ARCH")
        public Document Doc;
        public Transform ToHost;
        public Transform ToLocal;
        public bool Identity;
        public string Key;

        public bool IsHost => LinkId == 0;
    }

    /// <summary>An indexed element: box (host coordinates) and its body for the mesh engine.</summary>
    internal sealed class GeoItem
    {
        public GeoSource Source;
        public Element Element;
        public long Id;
        public string Category;
        public Box3 Box;
        public GeoBody Body;
        /// <summary>Revit solids (only for the exact clash boolean). Loaded on demand.</summary>
        public List<Solid> Solids;
        public bool SolidsFailed;
    }

    /// <summary>Elements of one category in one model, with their plan grid.</summary>
    internal sealed class GeoLayer
    {
        public GeoSource Source;
        public BuiltInCategory Category;
        public string CategoryName;
        public readonly List<GeoItem> Items = new List<GeoItem>();
        public GridIndex Grid = new GridIndex(8.0);
        public long HostRev;
        public bool Complete;
        public List<ElementId> Pending;
        public int Next;
        public long BuildMs;
    }

    /// <summary>
    /// Geometry index per document (host + links). Built once per category and model, in slices
    /// (never more than one 250 ms slice in a row on the Revit thread) and reused while the model and links do not change.
    /// Meshes are extracted only from elements near what is being measured, and cached:
    ///  - for a link, while it stays loaded and in the same place;
    ///  - for the host, per element: if the AI moves 50 fixtures, only those 50 are re-triangulated.
    ///
    /// Compared with pyRevit: its height scripts (soffit, nine points, columns, clashes) re-extracted every link solid on each run
    /// and cast rays against each one in nested O(fixtures × elements) loops, with no cache or cancellation.
    /// </summary>
    internal sealed class GeoIndex
    {
        public readonly string DocKey;
        private readonly Dictionary<string, GeoLayer> _layers = new Dictionary<string, GeoLayer>(StringComparer.Ordinal);
        private readonly Dictionary<long, GeoBody> _hostBodies = new Dictionary<long, GeoBody>();
        private readonly Options _options = Bodies.NewOptions();

        /// <summary>Cap on cached host triangles (~13 doubles each): past this the cache is cleared.</summary>
        public const long MaxHostTriangles = 3000000;

        public int MeshesBuilt;
        public int MeshFailures;
        public long MeshMs;
        public long HostTriangles;
        public int HostReused;

        public GeoIndex(string docKey) { DocKey = docKey; }

        /// <summary>Extra categories that go with a requested one (stairs keep their geometry in runs and landings).</summary>
        public static IEnumerable<BuiltInCategory> Expand(BuiltInCategory bic)
        {
            yield return bic;
            switch (bic)
            {
                case BuiltInCategory.OST_Stairs:
                    yield return BuiltInCategory.OST_StairsRuns;
                    yield return BuiltInCategory.OST_StairsLandings;
                    break;
                case BuiltInCategory.OST_Roofs:
                    yield return BuiltInCategory.OST_RoofSoffit;
                    break;
            }
        }

        // ---------------- sources ----------------

        private static readonly string[] Disciplines = { "ARCH", "STRUCT", "STR", "MEP", "MECH", "ELEC", "PLMB", "PLUMB", "FP", "CIVIL", "SITE", "INT", "LAND", "FACADE" };

        /// <summary>Host + loaded links that pass the filter (name contains). Cheap: called on every request.</summary>
        public static List<GeoSource> Sources(Document doc, bool host, bool links, IList<string> linkFilter, List<string> warnings)
        {
            var list = new List<GeoSource>();
            if (host)
                list.Add(new GeoSource { LinkId = 0, Name = "host", Short = "", Doc = doc, ToHost = Transform.Identity, ToLocal = Transform.Identity, Identity = true, Key = "host" });
            if (!links) return list;
            var shorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                string name;
                try { name = doc.GetElement(li.GetTypeId())?.Name ?? li.Name; } catch (Exception) { name = li.Name; }
                if (linkFilter != null && linkFilter.Count > 0 && !linkFilter.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                Document ld;
                try { ld = li.GetLinkDocument(); } catch (Exception) { ld = null; }
                if (ld == null)
                {
                    warnings?.Add("link " + name + " is not loaded: not used");
                    continue;
                }
                // F16/R041: if the transform cannot be read, the link is excluded and reported; identity is never assumed.
                Transform t;
                try { t = li.GetTotalTransform(); }
                catch (Exception ex)
                {
                    warnings?.Add("link " + name + ": its transform could not be read (" + ex.GetType().Name + "); excluded from the check");
                    continue;
                }
                if (t == null)
                {
                    warnings?.Add("link " + name + ": no transform; excluded from the check");
                    continue;
                }
                var id = Ids.ToLong(li.Id);
                var sh = ShortName(name);
                var baseSh = sh;
                for (var k = 2; !shorts.Add(sh); k++) sh = baseSh + k;
                list.Add(new GeoSource
                {
                    LinkId = id,
                    Name = name,
                    Short = sh,
                    Doc = ld,
                    ToHost = t,
                    ToLocal = t.Inverse,
                    Identity = t.IsIdentity,
                    // If the link is reloaded, Revit gives a different Document object; if it moves, the transform changes.
                    Key = id + "|" + RuntimeHelpers.GetHashCode(ld) + "|" + TransformKey(t),
                });
            }
            return list;
        }

        /// <summary>"BIMBRIDGE_VDC-ARCH_SAMPLE PROJECT_R21.rvt" → "ARCH". No recognizable discipline: the first letters of the name.</summary>
        public static string ShortName(string name)
        {
            var n = (name ?? "").ToUpperInvariant();
            foreach (var d in Disciplines)
            {
                var i = n.IndexOf(d, StringComparison.Ordinal);
                if (i < 0) continue;
                var before = i == 0 || !char.IsLetter(n[i - 1]);
                var after = i + d.Length >= n.Length || !char.IsLetter(n[i + d.Length]);
                if (before && after) return d == "STR" ? "STRUCT" : d == "PLUMB" ? "PLMB" : d;
            }
            var clean = new string(n.Where(char.IsLetterOrDigit).Take(8).ToArray());
            return clean.Length == 0 ? "LINK" : clean;
        }

        private static string TransformKey(Transform t) =>
            string.Join(",", new[] { t.Origin.X, t.Origin.Y, t.Origin.Z, t.BasisX.X, t.BasisX.Y, t.BasisZ.Z }
                .Select(v => Math.Round(v, 5).ToString(CultureInfo.InvariantCulture)));

        /// <summary>Link signature (for the result cache): changes if one is reloaded, moved or removed.</summary>
        public static string Signature(IEnumerable<GeoSource> sources) => string.Join(";", sources.Where(s => !s.IsHost).Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal));

        // ---------------- layers ----------------

        private static string LayerKey(GeoSource s, BuiltInCategory c) => s.Key + "#" + (int)c;

        /// <summary>
        /// Prepares (in slices) the layers for those categories in those sources. Returns true when all are complete.
        /// Host layers are rebuilt if the model changed (rev), reusing meshes for what did not change;
        /// link layers only if they are reloaded or moved.
        /// </summary>
        public bool Ensure(IList<GeoSource> sources, IList<BuiltInCategory> cats, long hostRev, Stopwatch sw, long deadlineMs, Action<string> phase)
        {
            foreach (var s in sources)
                foreach (var c in cats.SelectMany(Expand).Distinct())
                {
                    var key = LayerKey(s, c);
                    if (!_layers.TryGetValue(key, out var layer) || (s.IsHost && layer.HostRev != hostRev))
                    {
                        layer = new GeoLayer { Source = s, Category = c, CategoryName = CatName(s.Doc, c), HostRev = hostRev };
                        _layers[key] = layer;
                    }
                    else
                    {
                        layer.Source = s; // same key: same document and transform
                    }
                    if (layer.Complete) continue;
                    phase?.Invoke("indexing " + layer.CategoryName + (s.IsHost ? "" : " in " + s.Short));
                    if (!Build(layer, sw, deadlineMs)) return false;
                }
            return true;
        }

        private bool Build(GeoLayer layer, Stopwatch sw, long deadlineMs)
        {
            var t0 = sw.ElapsedMilliseconds;
            var doc = layer.Source.Doc;
            if (layer.Pending == null)
            {
                try
                {
                    layer.Pending = new FilteredElementCollector(doc).OfCategory(layer.Category).WhereElementIsNotElementType().ToElementIds().ToList();
                }
                catch (Exception) { layer.Pending = new List<ElementId>(); }
                if (sw.ElapsedMilliseconds >= deadlineMs) { layer.BuildMs += sw.ElapsedMilliseconds - t0; return false; }
            }
            while (layer.Next < layer.Pending.Count)
            {
                var e = doc.GetElement(layer.Pending[layer.Next++]);
                if (e == null) continue;
                BoundingBoxXYZ bb;
                try { bb = e.get_BoundingBox(null); } catch (Exception) { bb = null; }
                if (bb == null || bb.Min == null || bb.Max == null) continue;
                var box = ToHostBox(bb, layer.Source);
                if (box.IsEmpty || box.SizeX > 5000 || box.SizeY > 5000) continue; // toposurfaces and the like
                var id = Ids.ToLong(e.Id);
                GeoBody body = null;
                if (layer.Source.IsHost && _hostBodies.TryGetValue(id, out var cached) && SameBox(cached.RawBox, box))
                    body = cached; // same box: the mesh is still valid unless the registry marks it as changed (checked on use)
                if (body == null) body = new GeoBody { Id = id, LinkId = layer.Source.LinkId, Source = layer.Source.Short, Category = layer.CategoryName, Box = box, RawBox = box };
                var item = new GeoItem { Source = layer.Source, Element = e, Id = id, Category = layer.CategoryName, Box = box, Body = body };
                layer.Items.Add(item);
                layer.Grid.Add(box);
                if ((layer.Next & 63) == 0 && sw.ElapsedMilliseconds >= deadlineMs) { layer.BuildMs += sw.ElapsedMilliseconds - t0; return false; }
            }
            layer.Complete = true;
            layer.Pending = null;
            layer.BuildMs += sw.ElapsedMilliseconds - t0;
            return true;
        }

        /// <summary>Box of an element (in its model's coordinates, with the box transform) mapped to the host.</summary>
        public static Box3 ToHostBox(BoundingBoxXYZ bb, GeoSource s)
        {
            var b = Box3.Empty;
            var bt = bb.Transform ?? Transform.Identity;
            for (var i = 0; i < 8; i++)
            {
                var p = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                p = bt.OfPoint(p);
                if (s != null && !s.Identity) p = s.ToHost.OfPoint(p);
                b = b.Include(p.X, p.Y, p.Z);
            }
            return b;
        }

        public static string CatName(Document doc, BuiltInCategory c)
        {
            try { return Category.GetCategory(doc, c)?.Name ?? c.ToString().Replace("OST_", ""); }
            catch (Exception) { return c.ToString().Replace("OST_", ""); }
        }

        // ---------------- queries ----------------

        /// <summary>Elements of the requested layers whose box touches the given box (in host coordinates).</summary>
        public List<GeoItem> Query(IList<GeoSource> sources, IList<BuiltInCategory> cats, Box3 probe)
        {
            var result = new List<GeoItem>();
            foreach (var s in sources)
                foreach (var c in cats.SelectMany(Expand).Distinct())
                {
                    if (!_layers.TryGetValue(LayerKey(s, c), out var layer) || !layer.Complete) continue;
                    foreach (var i in layer.Grid.Query(probe.MinX, probe.MinY, probe.MaxX, probe.MaxY, probe.MinZ, probe.MaxZ))
                        result.Add(layer.Items[i]);
                }
            return result;
        }

        // ---------------- meshes ----------------

        private static bool Valid(GeoBody b, VersionRegistry reg, string key)
        {
            if (b?.Mesh == null || !string.Equals(b.MeshKey ?? "", key ?? "", StringComparison.Ordinal)) return false;
            if (b.LinkId != 0 || reg == null) return true;
            return reg.ElementRev(b.Id) <= b.MeshRev && reg.AllBumpedAt <= b.MeshRev;
        }

        private static bool SameBox(Box3 a, Box3 b) =>
            !a.IsEmpty && !b.IsEmpty &&
            Math.Abs(a.MinX - b.MinX) < 1e-9 && Math.Abs(a.MinY - b.MinY) < 1e-9 && Math.Abs(a.MinZ - b.MinZ) < 1e-9 &&
            Math.Abs(a.MaxX - b.MaxX) < 1e-9 && Math.Abs(a.MaxY - b.MaxY) < 1e-9 && Math.Abs(a.MaxZ - b.MaxZ) < 1e-9;

        /// <summary>Mesh of the indexed element (triangulated once). Returns false if it has no solids.</summary>
        public bool EnsureMesh(GeoItem item, VersionRegistry reg, long hostRev, ICollection<string> skip)
        {
            var body = item.Body;
            var key = Bodies.SkipKey(skip);
            if (Valid(body, reg, key)) return !body.Mesh.IsEmpty;
            var sw = Stopwatch.StartNew();
            TriMesh mesh;
            try
            {
                var e = Live(item);
                mesh = Bodies.MeshOf(e, _options, item.Source.Identity ? null : item.Source.ToHost, skip, out _);
                if (e is Wall) EnsureWallLine(item);
            }
            catch (Exception)
            {
                mesh = TriMesh.Empty;
                body.MeshFailed = true;
                MeshFailures++;
            }
            body.MeshRev = hostRev;
            body.MeshKey = key;
            body.RawBox = item.Box;
            body.Mesh = mesh;
            MeshesBuilt++;
            MeshMs += sw.ElapsedMilliseconds;
            if (item.Source.IsHost) Remember(body);
            return !mesh.IsEmpty;
        }

        /// <summary>Wall location line and thickness (cheap: no triangulation). false if it is not a wall with a location curve.</summary>
        public bool EnsureWallLine(GeoItem item)
        {
            var body = item.Body;
            if (body.Line != null) return true;
            try
            {
                var e = Live(item);
                if (!Bodies.WallLine(e, item.Source.Identity ? null : item.Source.ToHost, out var line, out var width)) return false;
                body.Line = line;
                body.Width = width;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Element Live(GeoItem item)
        {
            var e = item.Element;
            if (e == null || !e.IsValidObject) e = item.Element = item.Source.Doc.GetElement(Ids.ToId(item.Id));
            return e;
        }

        /// <summary>
        /// Body of a host element under check (fixture…): its solids' box and mesh, cached per element.
        /// The cache is valid while the registry does not mark the element as changed and its box stays the same (double safeguard).
        /// </summary>
        public GeoBody TargetBody(Element e, VersionRegistry reg, long hostRev, ICollection<string> skip, bool needMesh)
        {
            var id = Ids.ToLong(e.Id);
            var key = Bodies.SkipKey(skip);
            BoundingBoxXYZ bb;
            try { bb = e.get_BoundingBox(null); } catch (Exception) { bb = null; }
            var raw = bb != null && bb.Min != null && bb.Max != null ? ToHostBox(bb, null) : Box3.Empty;
            if (_hostBodies.TryGetValue(id, out var b) && SameBox(b.RawBox, raw) &&
                (Valid(b, reg, key) || (!needMesh && b.MeshRev == hostRev)))
            {
                HostReused++;
                return b;
            }
            b = new GeoBody { Id = id, Box = raw, RawBox = raw, MeshRev = hostRev, MeshKey = key };
            if (needMesh)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ge = e.get_Geometry(_options);
                    var solids = new List<Solid>();
                    var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Bodies.CollectSolids(ge, solids, e.Document, skip, parts);
                    b.Parts = parts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (solids.Count > 0)
                    {
                        // The solids' box is the physical body's (the element box includes symbolic lines and connectors).
                        if (Bodies.TryBox(solids, out var sb)) b.Box = ToHostBox(sb, null);
                        var builder = new TriMeshBuilder();
                        foreach (var s in solids) Bodies.AddSolid(s, builder, null);
                        b.Mesh = builder.Build();
                    }
                    else b.Mesh = TriMesh.Empty;
                }
                catch (Exception)
                {
                    b.Mesh = TriMesh.Empty;
                    b.MeshFailed = true;
                    MeshFailures++;
                }
                MeshesBuilt++;
                MeshMs += sw.ElapsedMilliseconds;
            }
            Remember(b);
            return b;
        }

        private void Remember(GeoBody b)
        {
            if (_hostBodies.TryGetValue(b.Id, out var old) && old.Mesh != null) HostTriangles -= old.Mesh.Triangles;
            _hostBodies[b.Id] = b;
            if (b.Mesh != null) HostTriangles += b.Mesh.Triangles;
            if (HostTriangles > MaxHostTriangles)
            {
                _hostBodies.Clear();
                HostTriangles = 0;
            }
        }

        /// <summary>Revit solids of an indexed element (for the exact boolean), in its own model's coordinates.</summary>
        public List<Solid> SolidsOf(GeoItem item, ICollection<string> skip, out bool failed)
        {
            if (item.Solids == null)
                item.Solids = ReadSolids(() => Live(item), skip, out item.SolidsFailed);
            failed = item.SolidsFailed;
            return item.Solids;
        }

        public List<Solid> ReadSolids(Func<Element> resolve, ICollection<string> skip, out bool failed)
        {
            var list = new List<Solid>();
            var incomplete = false;
            try
            {
                var e = resolve();
                if (e == null) incomplete = true;
                else Bodies.CollectSolids(e.get_Geometry(_options), list, e.Document, skip, onFailure: () => incomplete = true);
            }
            catch (Exception) { incomplete = true; }
            if (incomplete) MeshFailures++;
            failed = incomplete;
            return list;
        }

        public static string LabelOf(GeoItem item)
        {
            string type = null;
            try
            {
                var t = item.Source.Doc.GetElement(item.Element.GetTypeId()) as ElementType;
                type = t != null ? t.Name : item.Element.Name;
            }
            catch (Exception) { }
            return (item.Source.IsHost ? "" : item.Source.Short + ":") + item.Id.ToString(CultureInfo.InvariantCulture) + (type != null ? " " + type : "");
        }

        public Dictionary<string, object> Stats()
        {
            var layers = _layers.Values.ToList();
            var meshes = layers.SelectMany(l => l.Items).Where(i => i.Body?.Mesh != null).Select(i => i.Body.Mesh).ToList();
            return new Dictionary<string, object>
            {
                ["capas"] = (long)layers.Count,
                ["elementos"] = (long)layers.Sum(l => l.Items.Count),
                ["mallas"] = (long)(meshes.Count + _hostBodies.Count),
                ["triangulos"] = meshes.Sum(m => (long)m.Triangles) + HostTriangles,
                ["mb"] = Math.Round((meshes.Sum(m => m.Bytes) + HostTriangles * 110) / 1048576.0, 1),
                ["ms_mallas"] = MeshMs,
                ["ms_cajas"] = layers.Sum(l => l.BuildMs),
                ["fallos_geometria"] = (long)MeshFailures,
                ["reutilizadas"] = (long)HostReused,
                ["simd"] = TriMesh.SimdAvailable && TriMesh.SimdEnabled,
                ["detalle"] = layers.OrderBy(l => l.Source.Name).ThenBy(l => l.CategoryName)
                    .Select(l => (object)((l.Source.IsHost ? "host" : l.Source.Short) + " · " + l.CategoryName + ": " + l.Items.Count + (l.Complete ? "" : " (incomplete)"))).ToList(),
            };
        }

        /// <summary>Removes layers of sources that no longer exist (link reloaded, moved or removed).</summary>
        public int Prune(IList<GeoSource> current)
        {
            var keys = new HashSet<string>(current.Select(s => s.Key));
            var dead = _layers.Where(kv => !keys.Contains(kv.Value.Source.Key) && kv.Value.Source.LinkId != 0).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _layers.Remove(k);
            return dead.Count;
        }
    }

    /// <summary>Indexes per document. Used only on the Revit thread.</summary>
    internal static class GeoService
    {
        private static readonly Dictionary<string, GeoIndex> Indexes = new Dictionary<string, GeoIndex>(StringComparer.OrdinalIgnoreCase);

        public static GeoIndex For(Document doc)
        {
            var key = DocumentTracker.KeyOf(doc);
            if (!Indexes.TryGetValue(key, out var idx)) Indexes[key] = idx = new GeoIndex(key);
            return idx;
        }

        public static bool Has(Document doc) => Indexes.ContainsKey(DocumentTracker.KeyOf(doc) ?? "");

        public static void Drop(string docKey)
        {
            if (docKey != null) Indexes.Remove(docKey);
        }

        public static void Reset(Document doc) => Indexes.Remove(DocumentTracker.KeyOf(doc));
    }
}
