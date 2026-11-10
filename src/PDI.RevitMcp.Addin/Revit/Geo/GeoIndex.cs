// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Geo;

namespace PDI.RevitMcp.Addin.Revit.Geo
{
    /// <summary>Un modelo donde se busca geometría: el anfitrión o un vínculo cargado, con su transformación.</summary>
    internal sealed class GeoSource
    {
        public long LinkId;              // 0 = anfitrión
        public string Name;              // "anfitrión" o el nombre del tipo de vínculo ("ARCH.rvt")
        public string Short;             // etiqueta corta para las tablas ("ARCH")
        public Document Doc;
        public Transform ToHost;
        public Transform ToLocal;
        public bool Identity;
        public string Key;

        public bool IsHost => LinkId == 0;
    }

    /// <summary>Un elemento indexado: caja (coordenadas del anfitrión) y su cuerpo para el motor de mallas.</summary>
    internal sealed class GeoItem
    {
        public GeoSource Source;
        public Element Element;
        public long Id;
        public string Category;
        public Box3 Box;
        public GeoBody Body;
        /// <summary>Sólidos de Revit (sólo para la booleana exacta de choques). Se cargan si hacen falta.</summary>
        public List<Solid> Solids;
    }

    /// <summary>Elementos de una categoría en un modelo, con su rejilla en planta.</summary>
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
    /// Índice de geometría por documento (anfitrión + vínculos). Se construye una vez por categoría y modelo, por tramos
    /// (nunca más de un tramo de 250 ms seguido en el hilo de Revit) y se reutiliza mientras el modelo y los vínculos no cambien.
    /// Las mallas se extraen sólo de los elementos que caen cerca de lo que se mide y se guardan:
    ///  - de un vínculo, mientras siga cargado y en el mismo sitio;
    ///  - del anfitrión, por elemento: si la IA mueve 50 luminarias, sólo esas 50 se vuelven a triangular.
    ///
    /// Frente a pyRevit: sus scripts de altura (soffit, nueve puntos, columnas, choques) volvían a extraer en cada run todos los
    /// sólidos del vínculo y lanzaban rayos contra cada uno en bucles anidados O(luminarias × elementos), sin caché ni cancelación.
    /// </summary>
    internal sealed class GeoIndex
    {
        public readonly string DocKey;
        private readonly Dictionary<string, GeoLayer> _layers = new Dictionary<string, GeoLayer>(StringComparer.Ordinal);
        private readonly Dictionary<long, GeoBody> _hostBodies = new Dictionary<long, GeoBody>();
        private readonly Options _options = Bodies.NewOptions();

        /// <summary>Tope de triángulos guardados del anfitrión (~13 doubles cada uno): pasado esto se vacía la caché.</summary>
        public const long MaxHostTriangles = 3000000;

        public int MeshesBuilt;
        public int MeshFailures;
        public long MeshMs;
        public long HostTriangles;
        public int HostReused;

        public GeoIndex(string docKey) { DocKey = docKey; }

        /// <summary>Categorías extra que acompañan a una pedida (las escaleras guardan su geometría en tramos y descansos).</summary>
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

        // ---------------- fuentes ----------------

        private static readonly string[] Disciplines = { "ARCH", "STRUCT", "STR", "MEP", "MECH", "ELEC", "PLMB", "PLUMB", "FP", "CIVIL", "SITE", "INT", "LAND", "FACADE" };

        /// <summary>Anfitrión + vínculos cargados que pasan el filtro (nombre contiene). Barato: se llama en cada petición.</summary>
        public static List<GeoSource> Sources(Document doc, bool host, bool links, IList<string> linkFilter, List<string> warnings)
        {
            var list = new List<GeoSource>();
            if (host)
                list.Add(new GeoSource { LinkId = 0, Name = "anfitrión", Short = "", Doc = doc, ToHost = Transform.Identity, ToLocal = Transform.Identity, Identity = true, Key = "host" });
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
                    warnings?.Add("vínculo " + name + " no está cargado: no se usa");
                    continue;
                }
                // F16/R041: si la transformación no se puede leer, el vínculo se excluye y se informa; nunca se supone la identidad.
                Transform t;
                try { t = li.GetTotalTransform(); }
                catch (Exception ex)
                {
                    warnings?.Add("vínculo " + name + ": no se pudo leer su transformación (" + ex.GetType().Name + "); se excluyó de la revisión");
                    continue;
                }
                if (t == null)
                {
                    warnings?.Add("vínculo " + name + ": sin transformación; se excluyó de la revisión");
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
                    // Si el vínculo se recarga, Revit da otro objeto Document; si se mueve, cambia la transformación.
                    Key = id + "|" + RuntimeHelpers.GetHashCode(ld) + "|" + TransformKey(t),
                });
            }
            return list;
        }

        /// <summary>"PDI_VDC-ARCH_SAMPLE PROJECT_R21.rvt" → "ARCH". Sin disciplina reconocible: las primeras letras del nombre.</summary>
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
            return clean.Length == 0 ? "VINC" : clean;
        }

        private static string TransformKey(Transform t) =>
            string.Join(",", new[] { t.Origin.X, t.Origin.Y, t.Origin.Z, t.BasisX.X, t.BasisX.Y, t.BasisZ.Z }
                .Select(v => Math.Round(v, 5).ToString(CultureInfo.InvariantCulture)));

        /// <summary>Firma de los vínculos (para la caché de resultados): cambia si se recarga, mueve o quita uno.</summary>
        public static string Signature(IEnumerable<GeoSource> sources) => string.Join(";", sources.Where(s => !s.IsHost).Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal));

        // ---------------- capas ----------------

        private static string LayerKey(GeoSource s, BuiltInCategory c) => s.Key + "#" + (int)c;

        /// <summary>
        /// Prepara (por tramos) las capas de esas categorías en esas fuentes. Devuelve true cuando todas están completas.
        /// Las capas del anfitrión se rehacen si el modelo cambió (rev), reutilizando las mallas de lo que no cambió;
        /// las de vínculos sólo si se recargan o se mueven.
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
                        layer.Source = s; // misma clave: mismo documento y transformación
                    }
                    if (layer.Complete) continue;
                    phase?.Invoke("indexando " + layer.CategoryName + (s.IsHost ? "" : " en " + s.Short));
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
                if (box.IsEmpty || box.SizeX > 5000 || box.SizeY > 5000) continue; // topografías y similares
                var id = Ids.ToLong(e.Id);
                GeoBody body = null;
                if (layer.Source.IsHost && _hostBodies.TryGetValue(id, out var cached) && SameBox(cached.RawBox, box))
                    body = cached; // misma caja: la malla sigue valiendo si el registro no lo marca como cambiado (se revisa al usarla)
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

        /// <summary>Caja de un elemento (en coordenadas de su modelo, con la transformación de la caja) llevada al anfitrión.</summary>
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

        // ---------------- consultas ----------------

        /// <summary>Elementos de las capas pedidas cuya caja toca la caja dada (en coordenadas del anfitrión).</summary>
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

        // ---------------- mallas ----------------

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

        /// <summary>Malla del elemento indexado (se triangula una vez). Devuelve false si no tiene sólidos.</summary>
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

        /// <summary>Eje y espesor de un muro (barato: no triangula). false si no es un muro con curva de ubicación.</summary>
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
        /// Cuerpo de un elemento del anfitrión que se revisa (luminaria…): caja de sus sólidos y malla, con caché por elemento.
        /// La caché vale mientras el registro no marque el elemento como cambiado y su caja siga igual (doble seguro).
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
                        // La caja de los sólidos es la del cuerpo físico (la del elemento incluye líneas simbólicas y conectores).
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

        /// <summary>Sólidos de Revit de un elemento indexado (para la booleana exacta), en coordenadas de su propio modelo.</summary>
        public List<Solid> SolidsOf(GeoItem item, ICollection<string> skip)
        {
            if (item.Solids != null) return item.Solids;
            var list = new List<Solid>();
            try
            {
                var e = Live(item);
                Bodies.CollectSolids(e?.get_Geometry(_options), list, e?.Document, skip);
            }
            catch (Exception) { MeshFailures++; }
            item.Solids = list;
            return list;
        }

        public List<Solid> SolidsOf(Element e, ICollection<string> skip)
        {
            var list = new List<Solid>();
            try { Bodies.CollectSolids(e?.get_Geometry(_options), list, e?.Document, skip); }
            catch (Exception) { MeshFailures++; }
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
                    .Select(l => (object)((l.Source.IsHost ? "anfitrión" : l.Source.Short) + " · " + l.CategoryName + ": " + l.Items.Count + (l.Complete ? "" : " (incompleta)"))).ToList(),
            };
        }

        /// <summary>Quita las capas de fuentes que ya no existen (vínculo recargado, movido o quitado).</summary>
        public int Prune(IList<GeoSource> current)
        {
            var keys = new HashSet<string>(current.Select(s => s.Key));
            var dead = _layers.Where(kv => !keys.Contains(kv.Value.Source.Key) && kv.Value.Source.LinkId != 0).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _layers.Remove(k);
            return dead.Count;
        }
    }

    /// <summary>Índices por documento. Se usan sólo en el hilo de Revit.</summary>
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
