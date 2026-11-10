// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Addin.Revit;
using PDI.RevitMcp.Addin.Revit.Geo;
using PDI.RevitMcp.Engine.Qc;

namespace PDI.RevitMcp.Addin.Scripting
{
    /// <summary>
    /// Ayudas BIM para scripts y recetas (0.6.0, hito D). Son funciones de la API pública de Revit ya resueltas: no limitan lo que el
    /// script puede hacer (sigue teniendo doc, la API completa y ctx), sólo ahorran código repetido y devuelven resultados compactos en mm.
    /// Nada aquí abre transacciones: las que escriben (Grupos.Componer/CambiarTipo, Vistas.AplicarRango/FiltroPorCategoria) se llaman
    /// dentro de la transacción del motor (exec con escritura o receta PorUnidad).
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
        // GRUPOS
        // =====================================================================================================
        public static class Grupos
        {
            /// <summary>
            /// Lo que hay dentro de un grupo, con posiciones relativas a su origen (mm, sin rotación del grupo): permite comparar
            /// instancias del mismo tipo aunque estén giradas. "miembros_espejados" cuenta los FamilyInstance con Mirrored=true
            /// (la API no expone si el grupo entero está en espejo).
            /// </summary>
            public static Dictionary<string, object> Inspeccionar(Group g)
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
                    if (m is Group sub) { nested.Add(new Dictionary<string, object> { ["id"] = L(id), ["tipo"] = sub.GroupType?.Name }); continue; }
                    if (m is FamilyInstance fi && fi.Mirrored) mirrored++;
                    var p = PointOf(m);
                    var rel = p == null ? null : inv.OfPoint(p) - o;
                    members.Add(new Dictionary<string, object>
                    {
                        ["id"] = L(id), ["uid"] = m.UniqueId, ["cat"] = m.Category?.Name, ["tipo"] = TypeName(doc, m),
                        ["rel_mm"] = rel == null ? null : new[] { R(rel.X), R(rel.Y), R(rel.Z) },
                    });
                }
                return new Dictionary<string, object>
                {
                    ["id"] = L(g.Id), ["tipo"] = g.GroupType?.Name, ["origen_mm"] = new[] { R(o.X), R(o.Y), R(o.Z) },
                    ["giro"] = Math.Round(rot * 180 / Math.PI, 3), ["miembros"] = members.Count, ["anidados"] = nested,
                    ["miembros_espejados"] = mirrored, ["detalle"] = members,
                };
            }

            /// <summary>Firma estable de un miembro: categoría|tipo|posición relativa redondeada a 10 mm. Sirve para emparejar miembros entre tipos.</summary>
            public static string Firma(Group g, Element m)
            {
                var o = (g.Location as LocationPoint)?.Point ?? XYZ.Zero;
                var rot = (g.Location as LocationPoint)?.Rotation ?? 0;
                var p = PointOf(m);
                var rel = p == null ? XYZ.Zero : Transform.CreateRotationAtPoint(XYZ.BasisZ, -rot, o).OfPoint(p) - o;
                string r10(double ft) => (Math.Round(ft * Ft / 10) * 10).ToString("0", CultureInfo.InvariantCulture);
                return (m.Category?.Name ?? "?") + "|" + TypeName(m.Document, m) + "|" + r10(rel.X) + "," + r10(rel.Y) + "," + r10(rel.Z);
            }

            /// <summary>Instancias de un tipo de grupo (por nombre o id).</summary>
            public static IList<Group> Instancias(Document doc, string nombreTipo) =>
                new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().Where(x => x.GroupType?.Name == nombreTipo).ToList();

            /// <summary>Crea un grupo con esos elementos y, si se da nombre, renombra su tipo (falla si ya existe un tipo con ese nombre).</summary>
            public static Group Componer(Document doc, IEnumerable<ElementId> ids, string nombreTipo = null)
            {
                var list = ids.ToList();
                if (list.Count == 0) throw new ArgumentException("no hay elementos para agrupar");
                var g = doc.Create.NewGroup(list);
                if (!string.IsNullOrEmpty(nombreTipo))
                {
                    if (new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Any(t => t.Name == nombreTipo && t.Id != g.GroupType.Id))
                        throw new ArgumentException("ya existe un tipo de grupo '" + nombreTipo + "'");
                    g.GroupType.Name = nombreTipo;
                }
                return g;
            }

            /// <summary>
            /// Cambia el tipo de un grupo y comprueba que no se movió ni giró. Devuelve el emparejamiento de miembros por firma
            /// (antes→después) y los que no tienen pareja, para que la receta decida (no se inventa correspondencia).
            /// </summary>
            public static Dictionary<string, object> CambiarTipo(Group g, GroupType nuevo)
            {
                if (nuevo == null) throw new ArgumentNullException(nameof(nuevo));
                var doc = g.Document;
                var lp0 = g.Location as LocationPoint;
                var o0 = lp0?.Point; var r0 = lp0?.Rotation ?? 0;
                var before = g.GetMemberIds().Select(doc.GetElement).Where(m => m != null && !(m is Group))
                    .GroupBy(m => Firma(g, m)).ToDictionary(x => x.Key, x => x.Select(m => L(m.Id)).ToList());
                var anterior = g.GroupType?.Name;
                g.GroupType = nuevo;
                doc.Regenerate();
                var lp1 = g.Location as LocationPoint;
                var moved = o0 != null && lp1 != null ? lp1.Point.DistanceTo(o0) * Ft : 0;
                var turned = lp1 != null ? Math.Abs(lp1.Rotation - r0) * 180 / Math.PI : 0;
                var after = g.GetMemberIds().Select(doc.GetElement).Where(m => m != null && !(m is Group))
                    .GroupBy(m => Firma(g, m)).ToDictionary(x => x.Key, x => x.Select(m => L(m.Id)).ToList());
                var pares = before.Keys.Intersect(after.Keys).Select(k => new object[] { k, before[k], after[k] }).ToList();
                return new Dictionary<string, object>
                {
                    ["grupo"] = L(g.Id), ["anterior"] = anterior, ["nuevo"] = g.GroupType?.Name, ["ok"] = g.GroupType?.Id == nuevo.Id,
                    ["desplazado_mm"] = Math.Round(moved, 1), ["girado_grados"] = Math.Round(turned, 3),
                    ["emparejados"] = pares.Count, ["solo_antes"] = before.Keys.Except(after.Keys).ToList(), ["solo_despues"] = after.Keys.Except(before.Keys).ToList(),
                };
            }

            /// <summary>¿Todas las instancias del tipo tienen la misma composición (firmas)? Lista las que difieren (miembros excluidos, etc.).</summary>
            public static Dictionary<string, object> VerificarComposicion(Document doc, string nombreTipo)
            {
                var inst = Instancias(doc, nombreTipo);
                var sigs = inst.ToDictionary(x => L(x.Id), x => string.Join(";", x.GetMemberIds().Select(doc.GetElement).Where(m => m != null).Select(m => Firma(x, m)).OrderBy(s => s, StringComparer.Ordinal)));
                var mode = sigs.Values.GroupBy(s => s).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
                var diff = sigs.Where(kv => kv.Value != mode).Select(kv => kv.Key).ToList();
                return new Dictionary<string, object> { ["tipo"] = nombreTipo, ["instancias"] = inst.Count, ["distintas"] = diff, ["coherente"] = diff.Count == 0 };
            }
        }

        // =====================================================================================================
        // MONTAJE
        // =====================================================================================================
        public static class Montaje
        {
            /// <summary>Cotas del cuerpo real (mm, sin "Light Source"): [zmin, zmax, fuente]. Si no hay sólidos usa la caja y lo dice.</summary>
            public static Dictionary<string, object> Cuerpo(Element e)
            {
                var solids = new List<Solid>();
                Bodies.CollectSolids(e.get_Geometry(Bodies.NewOptions()), solids, e.Document, new[] { "Light Source" });
                if (Bodies.TryBox(solids, out var box))
                    return new Dictionary<string, object> { ["zmin"] = R(box.Min.Z), ["zmax"] = R(box.Max.Z), ["min"] = new[] { R(box.Min.X), R(box.Min.Y) }, ["max"] = new[] { R(box.Max.X), R(box.Max.Y) }, ["fuente"] = "solidos" };
                var bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                return new Dictionary<string, object> { ["zmin"] = R(bb.Min.Z), ["zmax"] = R(bb.Max.Z), ["min"] = new[] { R(bb.Min.X), R(bb.Min.Y) }, ["max"] = new[] { R(bb.Max.X), R(bb.Max.Y) }, ["fuente"] = "caja (sin sólidos: dato aproximado)" };
            }

            /// <summary>Primera vista 3D no plantilla y sin caja de sección (ReferenceIntersector la necesita).</summary>
            public static View3D Vista3D(Document doc) =>
                new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate)
                    .OrderBy(v => v.IsSectionBoxActive ? 1 : 0).FirstOrDefault();

            /// <summary>
            /// Busca el soporte del cuerpo con un rayo desde la base del cuerpo en <paramref name="direccion"/> ("arriba" | "abajo"),
            /// también dentro de vínculos. Clasifica: al_ras (|holgura| ≤ tol), empotrado (el soporte corta el cuerpo), separado (holgura
            /// positiva: puede ser correcto si es colgante; lo decide quien revisa), sin_referencia (nada en maxMm). No mueve nada.
            /// </summary>
            public static Dictionary<string, object> Soporte(Element e, View3D vista, string direccion = "arriba", double maxMm = 3000, double tolMm = 3,
                IEnumerable<BuiltInCategory> categorias = null)
            {
                var doc = e.Document;
                vista = vista ?? Vista3D(doc) ?? throw new InvalidOperationException("no hay una vista 3D no plantilla para lanzar rayos");
                var body = Cuerpo(e);
                if (body == null) return new Dictionary<string, object> { ["id"] = L(e.Id), ["clase"] = "sin_cuerpo" };
                var up = direccion != "abajo";
                var cats = (categorias ?? new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_GenericModel }).ToList();
                // El filtro debe aceptar también la instancia de vínculo; la categoría de lo encontrado dentro del vínculo se comprueba después.
                ElementFilter f = new LogicalOrFilter(new ElementMulticategoryFilter(cats), new ElementClassFilter(typeof(RevitLinkInstance)));
                f = new LogicalAndFilter(f, new ExclusionFilter(new List<ElementId> { e.Id }));
                var ri = new ReferenceIntersector(f, FindReferenceTarget.Face, vista) { FindReferencesInRevitLinks = true };
                var min = (double[])body["min"]; var max = (double[])body["max"];
                var cx = (min[0] + max[0]) / 2 / Ft; var cy = (min[1] + max[1]) / 2 / Ft;
                var zmin = (double)body["zmin"] / Ft; var zmax = (double)body["zmax"] / Ft;
                // Origen 1 mm por fuera del cuerpo por el lado opuesto: así un soporte que corta el cuerpo aparece a menos de su altura.
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
                    var clase = Math.Abs(gap) <= tolMm ? "al_ras" : gap < 0 ? "empotrado" : "separado";
                    return new Dictionary<string, object>
                    {
                        ["id"] = L(e.Id), ["clase"] = clase, ["holgura_mm"] = Math.Round(gap, 1), ["soporte"] = L(hit.Id), ["soporte_uid"] = hit.UniqueId,
                        ["soporte_cat"] = hit.Category?.Name, ["vinculo"] = link, ["cuerpo"] = body["fuente"], ["direccion"] = up ? "arriba" : "abajo",
                        ["regla"] = "rayo vertical desde el centro de la huella del cuerpo; tolerancia " + tolMm + " mm",
                    };
                }
                return new Dictionary<string, object> { ["id"] = L(e.Id), ["clase"] = "sin_referencia", ["direccion"] = up ? "arriba" : "abajo", ["max_mm"] = maxMm };
            }

            /// <summary>Transform de Revit (pies) → Xform del motor (mm) para usar ManifestQc/transformaciones sin Revit.</summary>
            public static Xform Transformacion(Transform t) => new Xform
            {
                X = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z }, Y = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z },
                Z = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z }, O = new[] { t.Origin.X * Ft, t.Origin.Y * Ft, t.Origin.Z * Ft },
            };
        }

        // =====================================================================================================
        // VISTAS
        // =====================================================================================================
        public static class Vistas
        {
            private static readonly PlanViewPlane[] Planes = { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane };
            private static string PlaneName(PlanViewPlane p) => p == PlanViewPlane.TopClipPlane ? "superior" : p == PlanViewPlane.CutPlane ? "corte" : p == PlanViewPlane.BottomClipPlane ? "inferior" : "profundidad";

            /// <summary>Rango de vista: por plano, nivel + desfase y cota absoluta (mm, elevación de proyecto). "ilimitado" cuando aplica.</summary>
            public static Dictionary<string, object> Rango(ViewPlan v)
            {
                var doc = v.Document;
                var r = v.GetViewRange();
                var o = new Dictionary<string, object> { ["vista"] = L(v.Id), ["nombre"] = v.Name, ["tipo"] = v.ViewType.ToString() };
                foreach (var p in Planes)
                {
                    var lid = r.GetLevelId(p);
                    var off = r.GetOffset(p);
                    if (lid == PlanViewRange.Unlimited) { o[PlaneName(p)] = new Dictionary<string, object> { ["nivel"] = "ilimitado" }; continue; }
                    var lvl = lid == PlanViewRange.LevelAbove ? null : lid == PlanViewRange.Current ? v.GenLevel : doc.GetElement(lid) as Level;
                    o[PlaneName(p)] = new Dictionary<string, object>
                    {
                        ["nivel"] = lid == PlanViewRange.LevelAbove ? "nivel superior" : lvl?.Name, ["desfase_mm"] = R(off),
                        ["cota_mm"] = lvl == null ? (object)null : R(lvl.Elevation + off),
                    };
                }
                var tid = v.ViewTemplateId;
                if (tid != ElementId.InvalidElementId) o["plantilla"] = doc.GetElement(tid)?.Name;
                return o;
            }

            /// <summary>
            /// Cambia un plano del rango ("superior"|"corte"|"inferior"|"profundidad") a nivel + desfase (mm). Falla con mensaje claro si una
            /// plantilla controla el rango. Requiere transacción.
            /// </summary>
            public static void AplicarRango(ViewPlan v, string plano, Level nivel, double desfaseMm)
            {
                var p = Planes.FirstOrDefault(x => PlaneName(x) == plano);
                if (PlaneName(p) != plano) throw new ArgumentException("plano debe ser superior, corte, inferior o profundidad");
                var tid = v.ViewTemplateId;
                if (tid != ElementId.InvalidElementId && v.Document.GetElement(tid) is View t
                    && !t.GetNonControlledTemplateParameterIds().Contains(new ElementId(BuiltInParameter.PLAN_VIEW_RANGE)))
                    throw new InvalidOperationException("la plantilla '" + t.Name + "' controla el rango de vista de '" + v.Name + "'");
                var r = v.GetViewRange();
                r.SetLevelId(p, nivel.Id);
                r.SetOffset(p, desfaseMm / Ft);
                v.SetViewRange(r);
            }

            /// <summary>Qué parámetros controla la plantilla de la vista y cuáles no (nombres), para saber qué se puede cambiar en la vista.</summary>
            public static Dictionary<string, object> CompararPlantilla(View v)
            {
                var doc = v.Document;
                if (!(doc.GetElement(v.ViewTemplateId) is View t)) return new Dictionary<string, object> { ["vista"] = v.Name, ["plantilla"] = null };
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
                    ["vista"] = v.Name, ["plantilla"] = t.Name,
                    ["controla"] = all.Where(i => !free.Contains(i)).Select(name).OrderBy(s => s).ToList(),
                    ["libres"] = all.Where(free.Contains).Select(name).OrderBy(s => s).ToList(),
                };
            }

            /// <summary>
            /// ¿Por qué no se ve <paramref name="e"/> en <paramref name="v"/>? Junta los hechos con la API y deja que VisibilityQc decida.
            /// Las regiones de plano de la vista se listan, pero su rango no es legible por la API pública: si el elemento cae dentro de una,
            /// el veredicto de rango se marca como no comprobable.
            /// </summary>
            public static Dictionary<string, object> PorQueNoSeVe(View v, Element e)
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
                        if (v.GetFilterVisibility(fid)) continue;
                        var fe = doc.GetElement(fid);
                        bool passes = fe is ParameterFilterElement pf ? (pf.GetCategories().Contains(e.Category?.Id ?? ElementId.InvalidElementId) && (pf.GetElementFilter()?.PassesFilter(doc, e.Id) ?? true))
                            : fe is SelectionFilterElement sf && sf.GetElementIds().Contains(e.Id);
                        if (passes) f.HidingFilters.Add(fe.Name);
                    }
                    catch (Exception ex) { notes.Add("filtro " + L(fid) + ": " + ex.Message); }
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
                if (e.DesignOption != null) notes.Add("pertenece a la opción de diseño '" + e.DesignOption.Name + "': la API no dice qué opción muestra la vista");
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
                var regiones = new List<long>();
                if (v is ViewPlan vp)
                {
                    f.PlanKind = vp.ViewType == ViewType.CeilingPlan ? "rcp" : vp.ViewType == ViewType.FloorPlan || vp.ViewType == ViewType.EngineeringPlan ? "planta" : null;
                    var rg = Rango(vp);
                    double? cota(string k) => rg.TryGetValue(k, out var x) && x is Dictionary<string, object> d && d.TryGetValue("cota_mm", out var c) && c is double cd ? cd : (double?)null;
                    f.Top = cota("superior"); f.Cut = cota("corte"); f.Bottom = cota("inferior"); f.Depth = cota("profundidad");
                    f.RangeSource = "rango de la vista";
                    var body = Montaje.Cuerpo(e);
                    if (body != null) { f.ZMin = (double)body["zmin"]; f.ZMax = (double)body["zmax"]; }
                    try
                    {
                        var ept = PointOf(e);
                        foreach (var pr in new FilteredElementCollector(doc, v.Id).OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType())
                        {
                            var bb = pr.get_BoundingBox(v);
                            if (ept != null && bb != null && ept.X >= bb.Min.X && ept.X <= bb.Max.X && ept.Y >= bb.Min.Y && ept.Y <= bb.Max.Y) regiones.Add(L(pr.Id));
                        }
                    }
                    catch (Exception) { }
                    if (regiones.Count > 0)
                    {
                        notes.Add("cae en la caja de región(es) de plano " + string.Join(", ", regiones) + ": su rango no es legible por la API pública; el veredicto de rango de la vista puede no aplicar");
                        f.RangeSource = "rango de la vista (hay región de plano encima: comprobar a mano)";
                    }
                }
                var reasons = VisibilityQc.Explain(f, out var notCheckable);
                if (regiones.Count > 0) foreach (var r in reasons.Where(x => x.Code == "rango_de_vista")) r.Certain = false;
                return new Dictionary<string, object>
                {
                    ["vista"] = v.Name, ["id"] = L(e.Id), ["visible_segun_api"] = reasons.Count == 0,
                    ["causas"] = reasons.Select(x => x.ToString()).ToList(), ["no_comprobable"] = notCheckable.Concat(notes).ToList(),
                    ["cuerpo_mm"] = f.ZMin.HasValue ? new[] { f.ZMin.Value, f.ZMax.Value } : null, ["regiones_de_plano"] = regiones,
                };
            }

            /// <summary>
            /// Filtro de vista "parámetro de texto = valor" para unas categorías. Si ya existe uno con ese nombre se devuelve sin tocarlo.
            /// Requiere transacción.
            /// </summary>
            public static ParameterFilterElement FiltroPorCategoria(Document doc, string nombre, IEnumerable<BuiltInCategory> categorias, BuiltInParameter parametro, string valor)
            {
                var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(x => x.Name == nombre);
                if (existing != null) return existing;
                var cats = categorias.Select(c => new ElementId(c)).ToList();
                var pid = new ElementId(parametro);
#if REVIT2023_OR_GREATER
                var rule = ParameterFilterRuleFactory.CreateEqualsRule(pid, valor);
#else
                var rule = ParameterFilterRuleFactory.CreateEqualsRule(pid, valor, false);
#endif
                return ParameterFilterElement.Create(doc, nombre, cats, new ElementParameterFilter(rule));
            }
        }

        // =====================================================================================================
        // MANIFIESTO (QC uno a uno contra una lista esperada)
        // =====================================================================================================
        public static class Manifiesto
        {
            /// <summary>Convierte elementos del modelo a lo que compara ManifestQc (mm, giro por BasisX, nivel, etiquetas, grupo).</summary>
            public static List<ManifestQc.Actual> Juntar(Document doc, IEnumerable<Element> elems, View vistaEtiquetas = null)
            {
                var list = elems.Where(x => x != null).ToList();
                var tagCount = new Dictionary<ElementId, int>();
                var tags = vistaEtiquetas != null ? new FilteredElementCollector(doc, vistaEtiquetas.Id).OfClass(typeof(IndependentTag)) : new FilteredElementCollector(doc).OfClass(typeof(IndependentTag));
                foreach (IndependentTag t in tags)
                {
                    ICollection<ElementId> ids;
                    try { ids = t.GetTaggedLocalElementIds(); } catch (Exception) { continue; }
                    foreach (var id in ids) tagCount[id] = tagCount.TryGetValue(id, out var n) ? n + 1 : 1;
                }
                var res = new List<ManifestQc.Actual>();
                foreach (var e in list)
                {
                    var p = PointOf(e);
                    if (p == null) continue;
                    double? rot = null;
                    if (e is FamilyInstance fi) { var bx = fi.GetTransform().BasisX; rot = ((Math.Atan2(bx.Y, bx.X) * 180 / Math.PI) % 360 + 360) % 360; }
                    else if (e.Location is LocationPoint lp) rot = lp.Rotation * 180 / Math.PI;
                    var t = doc.GetElement(e.GetTypeId()) as ElementType;
                    var lvl = doc.GetElement(e.LevelId) as Level;
                    if (lvl == null && e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId() is ElementId li) lvl = doc.GetElement(li) as Level;
                    res.Add(new ManifestQc.Actual
                    {
                        Id = L(e.Id), Uid = e.UniqueId, X = p.X * Ft, Y = p.Y * Ft, Z = p.Z * Ft, RotDeg = rot, Level = lvl?.Name,
                        Family = t?.FamilyName, Type = t?.Name ?? e.Name, Tags = tagCount.TryGetValue(e.Id, out var c) ? c : 0,
                        Group = e.GroupId != ElementId.InvalidElementId ? (doc.GetElement(e.GroupId) as Group)?.GroupType?.Name : null,
                    });
                }
                return res;
            }

            /// <summary>QC completo: lee el manifiesto (JSON de ManifestQc.Parse), compara y devuelve el resultado.</summary>
            public static ManifestQc.Result Qc(Document doc, string manifestJson, IEnumerable<Element> elems, View vistaEtiquetas = null)
            {
                var json = PDI.RevitMcp.Protocol.Json.ParseObject(manifestJson);
                var expected = ManifestQc.Parse(json, out var opt);
                return ManifestQc.Run(expected, Juntar(doc, elems, vistaEtiquetas), opt);
            }
        }
    }
}
