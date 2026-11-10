// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Catálogo del modelo en una llamada (lo que en pyRevit son varios scripts o recorrer el navegador a mano):
    /// resumen, categorias, tipos, familias, niveles, vistas, plantillas, laminas, worksets, vinculos, filtros.
    /// Los conteos usan filtros rápidos de Revit (GetElementCount no abre los elementos) y se reparten en tramos,
    /// así un modelo grande no congela Revit. Sólo lee.
    /// </summary>
    public sealed class CatalogRunner
    {
        public static readonly string[] Kinds =
            { "resumen", "categorias", "tipos", "familias", "niveles", "vistas", "plantillas", "laminas", "worksets", "vinculos", "filtros" };

        private const int DefaultPageSize = 100;
        private const int MaxPageSize = 200;

        private readonly string _que;
        private readonly List<string> _cats;
        private readonly List<string> _lvls;
        private readonly List<string> _extra;
        private readonly string _buscar;
        private readonly string _tipoVista;
        private readonly int _page;
        private readonly int _pageSize;
        private readonly bool _mm;

        private Document _doc;
        private Queue<Action> _units;
        private string[] _cols;
        private readonly List<object[]> _rows = new List<object[]>();
        private readonly Dictionary<string, object> _summary = new Dictionary<string, object>();
        private readonly Dictionary<string, long> _catCounts = new Dictionary<string, long>();
        private Comparison<object[]> _sort;

        public long Rev;
        public string Error;

        public CatalogRunner(IDictionary<string, object> a)
        {
            _que = (Arg.GetString(a, "que") ?? "resumen").Trim().ToLowerInvariant();
            if (_que == "categorías") _que = "categorias";
            if (_que == "láminas" || _que == "hojas") _que = "laminas";
            if (_que == "vínculos" || _que == "links") _que = "vinculos";
            if (Array.IndexOf(Kinds, _que) < 0) { Error = "que: " + string.Join(", ", Kinds); return; }
            _cats = Arg.GetStringList(a, "cat");
            _lvls = Arg.GetStringList(a, "lvl");
            _extra = Arg.GetStringList(a, "f");
            _buscar = Arg.GetString(a, "buscar");
            _tipoVista = Arg.GetString(a, "tipo");
            _page = (int)(Arg.GetLong(a, "page") ?? 1);
            _pageSize = (int)(Arg.GetLong(a, "page_size") ?? DefaultPageSize);
            _mm = string.Equals(Arg.GetString(a, "units"), "mm", StringComparison.OrdinalIgnoreCase);
            if (_page < 1) Error = "page empieza en 1";
            else if (_pageSize < 1 || _pageSize > MaxPageSize) Error = "page_size entre 1 y " + MaxPageSize;
            else if (_extra.Count > 6) Error = "f admite hasta 6 parámetros extra";
        }

        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (_units == null)
            {
                _doc = doc;
                Rev = tracker.For(doc)?.Rev ?? 0;
                _units = new Queue<Action>();
                try { Plan(); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { Error = ex.Message; }
                if (Error != null) return true;
            }
            else if (!ReferenceEquals(doc, _doc) && doc.Title != _doc.Title)
            {
                Error = "El documento activo cambió durante la consulta; repítala.";
                return true;
            }
            var deadline = sw.ElapsedMilliseconds + budgetMs;
            while (_units.Count > 0)
            {
                _units.Dequeue()();
                if (Error != null) return true;
                if (_units.Count > 0 && sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        /// <summary>Filtrar por texto, ordenar y paginar: fuera del hilo de Revit.</summary>
        public Dictionary<string, object> Finish()
        {
            var d = new Dictionary<string, object> { ["que"] = _que };
            if (_que == "resumen")
            {
                foreach (var kv in _summary) d[kv.Key] = kv.Value;
                d["categorias_top"] = _catCounts.OrderByDescending(k => k.Value).Take(25)
                    .ToDictionary(k => k.Key, k => (object)k.Value);
                d["sugerencia"] = "catalog que=categorias|tipos|vistas|laminas… para el detalle; query cat=… para elementos";
                return d;
            }
            IEnumerable<object[]> rows = _rows;
            if (!string.IsNullOrWhiteSpace(_buscar))
            {
                var b = _buscar.Trim();
                rows = rows.Where(r => r.Any(c => c is string s && s.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            var list = rows.ToList();
            if (_sort != null) list.Sort(_sort);
            var total = list.Count;
            var pages = Math.Max(1, (total + _pageSize - 1) / _pageSize);
            d["total"] = (long)total;
            d["cols"] = _cols.Cast<object>().ToList();
            d["rows"] = list.Skip((_page - 1) * _pageSize).Take(_pageSize).Select(r => (object)r.ToList()).ToList();
            if (pages > 1) { d["page"] = (long)_page; d["pages"] = (long)pages; }
            foreach (var kv in _summary) d[kv.Key] = kv.Value;
            return d;
        }

        // ---------------- planificación por tipo ----------------

        private void Plan()
        {
            switch (_que)
            {
                case "resumen": PlanSummary(); break;
                case "categorias": PlanCategories(); break;
                case "tipos": PlanTypes(); break;
                case "familias": PlanFamilies(); break;
                case "niveles": PlanLevels(); break;
                case "vistas": PlanViews(); break;
                case "plantillas": PlanTemplates(); break;
                case "laminas": PlanSheets(); break;
                case "worksets": PlanWorksets(); break;
                case "vinculos": PlanLinks(); break;
                case "filtros": PlanFilters(); break;
            }
        }

        private void PlanSummary()
        {
            var doc = _doc;
            _summary["documento"] = doc.Title;
            _units.Enqueue(() =>
            {
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
                _summary["niveles"] = (long)new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
                _summary["vistas"] = (long)views.Count(v => !v.IsTemplate && !(v is ViewSheet) && IsListed(v.ViewType));
                _summary["plantillas"] = (long)views.Count(v => v.IsTemplate);
                _summary["laminas"] = (long)views.Count(v => v is ViewSheet);
                _summary["familias"] = (long)new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
                _summary["tipos_de_familia"] = (long)new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).GetElementCount();
                var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
                _summary["vinculos_revit"] = (long)links.Count;
                _summary["vinculos_cargados"] = (long)links.Count(l => l.GetLinkDocument() != null);
                if (doc.IsWorkshared)
                    _summary["worksets"] = (long)new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count;
            });
            EnqueueCategoryCounts(c => c.CategoryType == CategoryType.Model, null);
        }

        private void PlanCategories()
        {
            _cols = new[] { "cat", "nombre", "clase", "elementos" };
            EnqueueCategoryCounts(c => c.CategoryType == CategoryType.Model || c.CategoryType == CategoryType.Annotation, (c, n) =>
                _rows.Add(new object[] { BicName(c), c.Name, c.CategoryType == CategoryType.Model ? "modelo" : "anotacion", n }));
            _sort = (a, b) => ((long)b[3]).CompareTo((long)a[3]);
        }

        /// <summary>Un tramo por cada ~40 categorías: GetElementCount usa el índice de categorías y no abre los elementos.</summary>
        private void EnqueueCategoryCounts(Func<Category, bool> include, Action<Category, long> row)
        {
            var doc = _doc;
            var cats = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
            {
                try { if (c != null && include(c)) cats.Add(c); } catch (Exception) { }
            }
            for (var i = 0; i < cats.Count; i += 40)
            {
                var chunk = cats.Skip(i).Take(40).ToList();
                _units.Enqueue(() =>
                {
                    foreach (var c in chunk)
                    {
                        long n;
                        try { n = new FilteredElementCollector(doc).OfCategoryId(c.Id).WhereElementIsNotElementType().GetElementCount(); }
                        catch (Exception) { continue; }
                        if (n == 0 && string.IsNullOrEmpty(_buscar)) continue;
                        _catCounts[BicName(c)] = n;
                        row?.Invoke(c, n);
                    }
                });
            }
        }

        private void PlanTypes()
        {
            var doc = _doc;
            _cols = new[] { "id", "cat", "fam", "type", "instancias" };
            var bics = Categories();
            if (Error != null) return;
            FilteredElementCollector types = new FilteredElementCollector(doc).WhereElementIsElementType();
            types = bics.Count > 0 ? types.WherePasses(new ElementMulticategoryFilter(bics)) : types.OfClass(typeof(FamilySymbol));
            var typeIds = types.ToElementIds().ToList();
            var counts = new Dictionary<long, long>();
            // Conteo de instancias por tipo: GetTypeId es barato; con categoría se recorre sólo esa categoría.
            FilteredElementCollector inst = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            inst = bics.Count > 0 ? inst.WherePasses(new ElementMulticategoryFilter(bics)) : inst.OfClass(typeof(FamilyInstance));
            var instIds = inst.ToElementIds().ToList();
            for (var i = 0; i < instIds.Count; i += 2000)
            {
                var chunk = instIds.Skip(i).Take(2000).ToList();
                _units.Enqueue(() =>
                {
                    foreach (var id in chunk)
                    {
                        var e = doc.GetElement(id);
                        if (e == null) continue;
                        var t = Ids.ToLong(e.GetTypeId());
                        counts[t] = counts.TryGetValue(t, out var n) ? n + 1 : 1;
                    }
                });
            }
            for (var i = 0; i < typeIds.Count; i += 500)
            {
                var chunk = typeIds.Skip(i).Take(500).ToList();
                _units.Enqueue(() =>
                {
                    foreach (var id in chunk)
                    {
                        if (!(doc.GetElement(id) is ElementType t)) continue;
                        var key = Ids.ToLong(id);
                        _rows.Add(new object[] { key, t.Category != null ? BicName(t.Category) : null, t.FamilyName, t.Name, counts.TryGetValue(key, out var n) ? n : 0L });
                    }
                });
            }
            _sort = (a, b) =>
            {
                var c = string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.Compare(a[2] as string, b[2] as string, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a[3] as string, b[3] as string, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void PlanFamilies()
        {
            var doc = _doc;
            _cols = new[] { "id", "nombre", "cat", "tipos", "en_sitio" };
            var bics = Categories();
            if (Error != null) return;
            var wanted = new HashSet<long>(bics.Select(b => Ids.ToLong(new ElementId(b))));
            _units.Enqueue(() =>
            {
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
                {
                    Category cat = null;
                    try { cat = f.FamilyCategory; } catch (Exception) { }
                    if (wanted.Count > 0 && (cat == null || !wanted.Contains(Ids.ToLong(cat.Id)))) continue;
                    long n = 0;
                    try { n = f.GetFamilySymbolIds().Count; } catch (Exception) { }
                    _rows.Add(new object[] { Ids.ToLong(f.Id), f.Name, cat != null ? BicName(cat) : null, n, f.IsInPlace });
                }
            });
            _sort = (a, b) =>
            {
                var c = string.Compare(a[2] as string, b[2] as string, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void PlanLevels()
        {
            var doc = _doc;
            _cols = new[] { "id", "nombre", "elev", "elev_txt", "z" };
            _units.Enqueue(() =>
            {
                foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    _rows.Add(new object[] { Ids.ToLong(l.Id), l.Name, Len(l.Elevation), Lengths.FeetInches16(l.Elevation), Len(l.ProjectElevation) });
            });
            _sort = (a, b) => Convert.ToDouble(a[4], CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b[4], CultureInfo.InvariantCulture));
            _summary["nota"] = "elev = cota mostrada en Revit; z = cota interna (la que usan x,y,z de query y geo)";
        }

        private void PlanViews()
        {
            var doc = _doc;
            var cols = new List<string> { "id", "nombre", "tipo", "nivel", "plantilla", "escala", "hoja" };
            cols.AddRange(_extra);
            _cols = cols.ToArray();
            ViewType? only = null;
            if (!string.IsNullOrWhiteSpace(_tipoVista))
            {
                only = ParseViewType(_tipoVista);
                if (only == null) { Error = "tipo: plano, rcp, seccion, alzado, 3d, detalle, dibujo, leyenda, tabla, area o estructural"; return; }
            }
            Dictionary<long, string> sheetOf = null;
            Dictionary<long, string> names = null;
            var views = new List<View>();
            _units.Enqueue(() =>
            {
                sheetOf = SheetNumbers(doc);
                names = new Dictionary<long, string>();
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v.IsTemplate || v is ViewSheet || !IsListed(v.ViewType)) continue;
                    if (only.HasValue && v.ViewType != only.Value) continue;
                    views.Add(v);
                }
            });
            _units.Enqueue(() =>
            {
                foreach (var v in views)
                {
                    var lvl = (v as ViewPlan)?.GenLevel?.Name;
                    if (_lvls.Count > 0 && (lvl == null || !_lvls.Any(x => string.Equals(x, lvl, StringComparison.OrdinalIgnoreCase)))) continue;
                    var row = new List<object>
                    {
                        Ids.ToLong(v.Id), v.Name, ViewTypeName(v.ViewType), lvl, TemplateName(doc, v.ViewTemplateId, names), SafeScale(v),
                        sheetOf.TryGetValue(Ids.ToLong(v.Id), out var sh) ? sh : null,
                    };
                    foreach (var p in _extra) row.Add(Param(doc, v, p));
                    _rows.Add(row.ToArray());
                }
            });
            _sort = (a, b) =>
            {
                var c = string.Compare(a[2] as string, b[2] as string, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void PlanTemplates()
        {
            var doc = _doc;
            _cols = new[] { "id", "nombre", "tipo", "usada_por" };
            _units.Enqueue(() =>
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
                var uses = new Dictionary<long, long>();
                foreach (var v in all)
                {
                    if (v.IsTemplate) continue;
                    var t = Ids.ToLong(v.ViewTemplateId);
                    if (t > 0) uses[t] = uses.TryGetValue(t, out var n) ? n + 1 : 1;
                }
                foreach (var v in all.Where(x => x.IsTemplate))
                {
                    var id = Ids.ToLong(v.Id);
                    _rows.Add(new object[] { id, v.Name, ViewTypeName(v.ViewType), uses.TryGetValue(id, out var n) ? n : 0L });
                }
            });
            _sort = (a, b) => string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
        }

        private void PlanSheets()
        {
            var doc = _doc;
            var cols = new List<string> { "id", "numero", "nombre", "vistas", "cajetin" };
            cols.AddRange(_extra);
            _cols = cols.ToArray();
            _units.Enqueue(() =>
            {
                var titleblocks = new Dictionary<long, string>();
                foreach (var tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
                {
                    var owner = Ids.ToLong(tb.OwnerViewId);
                    if (owner > 0 && !titleblocks.ContainsKey(owner) && doc.GetElement(tb.GetTypeId()) is ElementType t)
                        titleblocks[owner] = t.FamilyName + ": " + t.Name;
                }
                foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                {
                    if (s.IsTemplate) continue;
                    var id = Ids.ToLong(s.Id);
                    long n = 0;
                    try { n = s.GetAllViewports().Count; } catch (Exception) { }
                    var row = new List<object> { id, s.SheetNumber, s.Name, n, titleblocks.TryGetValue(id, out var tb) ? tb : null };
                    foreach (var p in _extra) row.Add(Param(doc, s, p));
                    _rows.Add(row.ToArray());
                }
            });
            _sort = (a, b) => string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
        }

        private void PlanWorksets()
        {
            var doc = _doc;
            if (!doc.IsWorkshared) { Error = "el modelo no usa worksets"; return; }
            _cols = new[] { "id", "nombre", "abierto", "visible", "editable", "propietario", "elementos" };
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
            for (var i = 0; i < worksets.Count; i += 8)
            {
                var chunk = worksets.Skip(i).Take(8).ToList();
                _units.Enqueue(() =>
                {
                    foreach (var w in chunk)
                    {
                        long n = 0;
                        try { n = new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount(); }
                        catch (Exception) { }
                        _rows.Add(new object[] { (long)w.Id.IntegerValue, w.Name, w.IsOpen, w.IsVisibleByDefault, w.IsEditable, string.IsNullOrEmpty(w.Owner) ? null : w.Owner, n });
                    }
                });
            }
            _sort = (a, b) => string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
        }

        private void PlanLinks()
        {
            var doc = _doc;
            _cols = new[] { "id", "nombre", "clase", "cargado", "ruta", "vista" };
            _units.Enqueue(() =>
            {
                foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var typeId = li.GetTypeId();
                    bool loaded = false;
                    try { loaded = RevitLinkType.IsLoaded(doc, typeId); } catch (Exception) { }
                    _rows.Add(new object[] { Ids.ToLong(li.Id), li.Name, "revit", loaded, LinkPath(doc, typeId), null });
                }
                foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                {
                    bool linked;
                    try { linked = ii.IsLinked; } catch (Exception) { continue; }
                    var typeName = doc.GetElement(ii.GetTypeId())?.Name ?? ii.Name;
                    var view = ii.OwnerViewId != ElementId.InvalidElementId ? doc.GetElement(ii.OwnerViewId)?.Name : null;
                    _rows.Add(new object[] { Ids.ToLong(ii.Id), typeName, linked ? "cad_vinculado" : "cad_importado", linked ? (object)true : null,
                        linked ? LinkPath(doc, ii.GetTypeId()) : null, view });
                }
            });
            _sort = (a, b) =>
            {
                var c = string.Compare(a[2] as string, b[2] as string, StringComparison.Ordinal);
                return c != 0 ? -c : string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void PlanFilters()
        {
            var doc = _doc;
            _cols = new[] { "id", "nombre", "categorias", "vistas", "plantillas" };
            _units.Enqueue(() =>
            {
                var inViews = new Dictionary<long, long>();
                var inTemplates = new Dictionary<long, long>();
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    ICollection<ElementId> fs;
                    try { if (!v.AreGraphicsOverridesAllowed()) continue; fs = v.GetFilters(); } catch (Exception) { continue; }
                    var target = v.IsTemplate ? inTemplates : inViews;
                    foreach (var f in fs)
                    {
                        var k = Ids.ToLong(f);
                        target[k] = target.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
                }
                foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                {
                    var id = Ids.ToLong(f.Id);
                    string cats = null;
                    try
                    {
                        cats = string.Join(",", f.GetCategories().Select(c => { var cat = Category.GetCategory(doc, c); return cat != null ? BicName(cat) : Ids.ToLong(c).ToString(CultureInfo.InvariantCulture); }));
                    }
                    catch (Exception) { }
                    _rows.Add(new object[] { id, f.Name, cats, inViews.TryGetValue(id, out var a) ? a : 0L, inTemplates.TryGetValue(id, out var b) ? b : 0L });
                }
            });
            _sort = (a, b) => string.Compare(a[1] as string, b[1] as string, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- utilidades ----------------

        private List<BuiltInCategory> Categories()
        {
            var list = new List<BuiltInCategory>();
            foreach (var name in _cats)
            {
                if (!QueryRunner.TryCategory(name, out var bic)) { Error = "categoría desconocida: " + name + " (use nombres como LightingFixtures, ElectricalFixtures)"; return list; }
                list.Add(bic);
            }
            return list;
        }

        private object Len(double ft) => _mm ? Math.Round(ft * 304.8, 1) : Math.Round(ft, 4);

        /// <summary>Nombre que acepta cat= en las otras herramientas (LightingFixtures), no el nombre traducido de la interfaz.</summary>
        public static string BicName(Category c)
        {
            BuiltInCategory bic;
#if REVIT2023_OR_GREATER
            bic = c.BuiltInCategory;
#else
            bic = (BuiltInCategory)c.Id.IntegerValue;
#endif
            if (bic == BuiltInCategory.INVALID || !Enum.IsDefined(typeof(BuiltInCategory), bic)) return c.Name;
            var s = bic.ToString();
            return s.StartsWith("OST_", StringComparison.Ordinal) ? s.Substring(4) : s;
        }

        private static bool IsListed(ViewType t)
        {
            switch (t)
            {
                case ViewType.Undefined:
                case ViewType.Internal:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.DrawingSheet:
                    return false;
                default:
                    return true;
            }
        }

        public static ViewType? ParseViewType(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "plano": case "planta": case "floorplan": return ViewType.FloorPlan;
                case "rcp": case "techo": case "ceilingplan": return ViewType.CeilingPlan;
                case "seccion": case "sección": case "section": return ViewType.Section;
                case "alzado": case "elevation": return ViewType.Elevation;
                case "3d": case "threed": return ViewType.ThreeD;
                case "detalle": case "detail": return ViewType.Detail;
                case "dibujo": case "drafting": case "draftingview": return ViewType.DraftingView;
                case "leyenda": case "legend": return ViewType.Legend;
                case "tabla": case "schedule": return ViewType.Schedule;
                case "area": case "área": case "areaplan": return ViewType.AreaPlan;
                case "estructural": case "engineeringplan": return ViewType.EngineeringPlan;
                case "hoja": case "lamina": case "sheet": return ViewType.DrawingSheet;
                default: return null;
            }
        }

        public static string ViewTypeName(ViewType t)
        {
            switch (t)
            {
                case ViewType.FloorPlan: return "plano";
                case ViewType.CeilingPlan: return "rcp";
                case ViewType.Section: return "seccion";
                case ViewType.Elevation: return "alzado";
                case ViewType.ThreeD: return "3d";
                case ViewType.Detail: return "detalle";
                case ViewType.DraftingView: return "dibujo";
                case ViewType.Legend: return "leyenda";
                case ViewType.Schedule: return "tabla";
                case ViewType.AreaPlan: return "area";
                case ViewType.EngineeringPlan: return "estructural";
                case ViewType.DrawingSheet: return "hoja";
                default: return t.ToString();
            }
        }

        private static object SafeScale(View v)
        {
            try { return v.ViewType == ViewType.Schedule || v.Scale <= 0 ? null : (object)(long)v.Scale; }
            catch (Exception) { return null; }
        }

        private static string TemplateName(Document doc, ElementId id, Dictionary<long, string> cache)
        {
            var k = Ids.ToLong(id);
            if (k <= 0) return null;
            if (!cache.TryGetValue(k, out var n)) cache[k] = n = doc.GetElement(id)?.Name;
            return n;
        }

        /// <summary>Vista → número de la hoja donde está (viewports y tablas colocadas).</summary>
        public static Dictionary<long, string> SheetNumbers(Document doc)
        {
            var map = new Dictionary<long, string>();
            var numbers = new Dictionary<long, string>();
            string Number(ElementId sheetId)
            {
                var k = Ids.ToLong(sheetId);
                if (!numbers.TryGetValue(k, out var n)) numbers[k] = n = (doc.GetElement(sheetId) as ViewSheet)?.SheetNumber;
                return n;
            }
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                var k = Ids.ToLong(vp.ViewId);
                if (!map.ContainsKey(k)) map[k] = Number(vp.SheetId);
            }
            foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                if (si.IsTitleblockRevisionSchedule) continue;
                var k = Ids.ToLong(si.ScheduleId);
                if (!map.ContainsKey(k)) map[k] = Number(si.OwnerViewId);
            }
            return map;
        }

        private object Param(Document doc, Element e, string name)
        {
            try
            {
                var p = e.LookupParameter(name);
                return p == null ? null : ElementFields.ParamValue(doc, p, _mm);
            }
            catch (Exception) { return null; }
        }

        private static string LinkPath(Document doc, ElementId typeId)
        {
            try
            {
                if (!ExternalFileUtils.IsExternalFileReference(doc, typeId)) return null;
                var r = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                var mp = r?.GetAbsolutePath() ?? r?.GetPath();
                return mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
            }
            catch (Exception) { return null; }
        }
    }
}
