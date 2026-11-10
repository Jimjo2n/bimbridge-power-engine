// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Writes
{
    /// <summary>
    /// Herramientas comunes de escritura: formas canónicas para comparar "antes/después", búsquedas por nombre con mensajes
    /// claros y conversión de valores. Todo corre en el hilo de Revit.
    /// </summary>
    internal static class WriteKit
    {
        public const string Missing = "\u0000borrado";
        public const string NoParam = "\u0000sin_parametro";

        // ------------------------------------------------------------------ formas canónicas

        public static string Num(double v) => PlanEntry.Canon(Math.Round(v, 6));

        public static string Xyz(XYZ p) => p == null ? "" : Num(p.X) + "," + Num(p.Y) + "," + Num(p.Z);

        /// <summary>Punto que representa la posición del elemento: punto de inserción, centro de su curva o centro de su caja.</summary>
        public static XYZ LocationOf(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp && lp.Point != null) return lp.Point;
                if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            }
            catch (Exception) { }
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch (Exception) { }
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        /// <summary>Giro en planta en grados [0,360), o null si el elemento no tiene punto de inserción.</summary>
        public static double? RotationDeg(Element e)
        {
            try
            {
                if (e.Location is LocationPoint lp) return Norm360(lp.Rotation * 180.0 / Math.PI);
            }
            catch (Exception) { }
            return null;
        }

        public static double Norm360(double deg)
        {
            var d = deg % 360.0;
            if (d < 0) d += 360.0;
            if (d > 359.9999) d = 0;
            return Math.Round(d, 4);
        }

        public static string Loc(Element e) => Xyz(LocationOf(e));

        public static string Rot(Element e)
        {
            var r = RotationDeg(e);
            return r.HasValue ? Num(r.Value) : "";
        }

        public static string ParamValue(Parameter p)
        {
            if (p == null) return NoParam;
            if (!p.HasValue) return "";
            switch (p.StorageType)
            {
                case StorageType.Double: return PlanEntry.Canon(p.AsDouble());
                case StorageType.Integer: return PlanEntry.Canon((long)p.AsInteger());
                case StorageType.String: return p.AsString() ?? "";
                case StorageType.ElementId: return PlanEntry.Canon(Ids.ToLong(p.AsElementId()));
                default: return "";
            }
        }

        public static string TypeName(Document doc, Element e)
        {
            var t = doc.GetElement(e.GetTypeId()) as ElementType;
            return t == null ? "" : t.FamilyName + ": " + t.Name;
        }

        public static string WorksetName(Document doc, Element e)
        {
            if (!doc.IsWorkshared) return "";
            return doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name ?? "";
        }

        /// <summary>Elevation from Level; si la familia es alojada y no lo tiene, Offset del host.</summary>
        public static Parameter ElevParam(Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (p != null && !p.IsReadOnly) return p;
            var q = e.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            return q ?? p;
        }

        public static Parameter LevelParam(Element e) => e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);

        public static Level LevelOf(Document doc, Element e)
        {
            try
            {
                var id = e.LevelId;
                if (id == null || id == ElementId.InvalidElementId) id = LevelParam(e)?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) id = e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsElementId();
                return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id) as Level;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ comprobaciones por elemento

        /// <summary>Por qué no se puede editar un elemento (null = se puede).</summary>
        public static string WhyNotEditable(Document doc, Element e, bool allowGroupMember = false, bool allowPinned = true)
        {
            if (e == null) return "no existe";
            if (!allowGroupMember && e.GroupId != null && e.GroupId != ElementId.InvalidElementId) return "está dentro de un grupo";
            if (!allowPinned)
            {
                try { if (e.Pinned) return "está fijado (pin)"; } catch (Exception) { }
            }
            if (doc.IsWorkshared)
            {
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) return "lo tiene prestado otro usuario";
                }
                catch (Exception) { }
            }
            return null;
        }

        // ------------------------------------------------------------------ valores de parámetros

        private static bool IsLength(Parameter p)
        {
            try { return p.Definition.GetDataType() == SpecTypeId.Length; }
            catch (Exception) { return false; }
        }

        private static bool IsYesNo(Parameter p)
        {
            try { return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Convierte lo que da la IA al valor que Revit guarda: números en unidades internas, textos con unidades ("9' - 3\"", "2820 mm"),
        /// sí/no, o nombres para parámetros que apuntan a un elemento (nivel, fase, plantilla, scope box, material).
        /// <paramref name="show"/> es cómo se muestra en el diff.
        /// </summary>
        public static bool TryTarget(Document doc, Parameter p, object v, out object value, out string show, out string why)
        {
            value = null; why = null; show = null;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (v is double || v is long) { value = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true; }
                    if (v is string text)
                    {
                        if (IsLength(p))
                        {
                            if (Lengths.TryFeet(text, out var ft, out var err)) { value = ft; show = Lengths.FeetInches16(ft); return true; }
                            why = err;
                            return false;
                        }
                        try
                        {
                            if (UnitFormatUtils.TryParse(doc.GetUnits(), p.Definition.GetDataType(), text, out double parsed))
                            { value = parsed; show = text; return true; }
                        }
                        catch (Exception) { }
                        why = "no entiendo '" + text + "' para este parámetro; dé un número en unidades internas";
                        return false;
                    }
                    why = "es numérico: dé un número en unidades internas (pies para longitudes) o un texto con unidades";
                    return false;
                case StorageType.Integer:
                    if (v is bool b) { value = b ? 1L : 0L; return true; }
                    if (v is long l) { value = l; return true; }
                    if (v is double d && Math.Abs(d - Math.Round(d)) < 1e-9) { value = (long)Math.Round(d); return true; }
                    if (v is string s2)
                    {
                        var t = s2.Trim().ToLowerInvariant();
                        if (IsYesNo(p) && (t == "si" || t == "sí" || t == "yes" || t == "true" || t == "1")) { value = 1L; return true; }
                        if (IsYesNo(p) && (t == "no" || t == "false" || t == "0")) { value = 0L; return true; }
                        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { value = n; return true; }
                    }
                    why = IsYesNo(p) ? "es sí/no: dé true o false" : "es entero: dé un número entero";
                    return false;
                case StorageType.String:
                    value = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
                    return true;
                case StorageType.ElementId:
                    if (v is long lid)
                    {
                        if (lid != -1 && doc.GetElement(Ids.ToId(lid)) == null) { why = "no existe el elemento " + lid; return false; }
                        value = lid;
                        show = lid == -1 ? "ninguno" : doc.GetElement(Ids.ToId(lid))?.Name;
                        return true;
                    }
                    var name = v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
                    if (name.Length == 0 || name.Equals("ninguno", StringComparison.OrdinalIgnoreCase) || name.Equals("ninguna", StringComparison.OrdinalIgnoreCase) || name.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        value = -1L; show = "ninguno"; return true;
                    }
                    var target = ResolveByName(doc, p, name, out why);
                    if (target == null) return false;
                    value = Ids.ToLong(target.Id);
                    show = target.Name;
                    return true;
            }
            why = "tipo de parámetro no soportado";
            return false;
        }

        /// <summary>Elemento por nombre para un parámetro que apunta a otro elemento.</summary>
        private static Element ResolveByName(Document doc, Parameter p, string name, out string why)
        {
            why = null;
            var bip = (p.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
            var tag = bip.ToString();
            IEnumerable<Element> pool;
            string what;
            if (tag.Contains("LEVEL")) { pool = new FilteredElementCollector(doc).OfClass(typeof(Level)); what = "nivel"; }
            else if (tag.Contains("PHASE")) { pool = doc.Phases.Cast<Element>(); what = "fase"; }
            else if (tag.Contains("TEMPLATE")) { pool = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(x => x.IsTemplate); what = "plantilla de vista"; }
            else if (tag.Contains("VOLUME_OF_INTEREST")) { pool = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType(); what = "scope box"; }
            else if (tag.Contains("MATERIAL")) { pool = new FilteredElementCollector(doc).OfClass(typeof(Material)); what = "material"; }
            else
            {
                pool = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Element>()
                    .Concat(doc.Phases.Cast<Element>())
                    .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType())
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(Material)));
                what = "elemento";
            }
            var hits = pool.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            why = hits.Count == 0 ? "no existe " + what + " '" + name + "'" : "'" + name + "' es ambiguo (" + hits.Count + "); dé el id";
            return null;
        }

        public static void SetParam(Parameter p, object target)
        {
            if (p == null) throw new InvalidOperationException("sin parámetro");
            if (p.IsReadOnly) throw new InvalidOperationException("parámetro de sólo lectura");
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.Double: ok = p.Set(Convert.ToDouble(target, CultureInfo.InvariantCulture)); break;
                case StorageType.Integer: ok = p.Set((int)Convert.ToInt64(target, CultureInfo.InvariantCulture)); break;
                case StorageType.String: ok = p.Set(Convert.ToString(target, CultureInfo.InvariantCulture) ?? ""); break;
                case StorageType.ElementId:
                    var id = Convert.ToInt64(target, CultureInfo.InvariantCulture);
                    ok = p.Set(id == -1 ? ElementId.InvalidElementId : Ids.ToId(id));
                    break;
                default: throw new InvalidOperationException("tipo de parámetro no soportado");
            }
            if (!ok) throw new InvalidOperationException("Revit no aceptó el valor");
        }

        // ------------------------------------------------------------------ lectores de Target (los planes retenidos vuelven del disco como JSON)

        public static Dictionary<string, object> Dict(object target) => target as Dictionary<string, object> ?? new Dictionary<string, object>();

        public static long L(Dictionary<string, object> d, string k, long fallback = -1)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return fallback;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static double D(Dictionary<string, object> d, string k, double fallback = double.NaN)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return fallback;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static string S(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        public static bool B(Dictionary<string, object> d, string k, bool fallback = false)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return fallback;
            if (v is bool b) return b;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;
        }

        public static IEnumerable<long> Longs(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || !(v is List<object> l)) yield break;
            foreach (var x in l) if (x != null) yield return Convert.ToInt64(x, CultureInfo.InvariantCulture);
        }

        public static XYZ P(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || !(v is List<object> l) || l.Count < 2) return null;
            var x = Convert.ToDouble(l[0], CultureInfo.InvariantCulture);
            var y = Convert.ToDouble(l[1], CultureInfo.InvariantCulture);
            var z = l.Count > 2 ? Convert.ToDouble(l[2], CultureInfo.InvariantCulture) : 0.0;
            return new XYZ(x, y, z);
        }

        public static List<object> List(XYZ p) => new List<object> { p.X, p.Y, p.Z };

        public static List<object> List(params double[] v) => v.Select(x => (object)x).ToList();

        // ------------------------------------------------------------------ colores

        public static bool TryColor(string hex, out Color c)
        {
            c = null;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var h = hex.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
            c = new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            return true;
        }

        public static string Hex(Color c) => c == null || !c.IsValid ? "" : "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2");

        /// <summary>Forma canónica de unas sobrescrituras: sólo lo que este MCP maneja (color, medio tono, transparencia, grosor).</summary>
        public static string Overrides(OverrideGraphicSettings o)
        {
            if (o == null) return "";
            var parts = new List<string>();
            var col = Hex(o.ProjectionLineColor);
            if (col.Length > 0) parts.Add("color=" + col);
            if (o.Halftone) parts.Add("medio_tono");
            if (o.Transparency > 0) parts.Add("transparencia=" + o.Transparency);
            if (o.ProjectionLineWeight > 0) parts.Add("grosor=" + o.ProjectionLineWeight);
            return parts.Count == 0 ? "sin cambios" : string.Join(" ", parts);
        }

        public static Category CategoryByName(Document doc, string name)
        {
            if (QueryRunner.TryCategory(name, out var bic))
            {
                try { var c = Category.GetCategory(doc, bic); if (c != null) return c; } catch (Exception) { }
            }
            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }

    /// <summary>
    /// Búsquedas por nombre con caché durante una planificación (tipos, niveles, vistas, plantillas, láminas, worksets).
    /// Recorrer todos los tipos del modelo una vez cuesta ~50 ms; hacerlo por operación, segundos.
    /// </summary>
    internal sealed class ModelIndex
    {
        public readonly Document Doc;
        private List<ElementType> _types;
        private List<Level> _levels;
        private List<View> _views;
        private List<ViewSheet> _sheets;

        public ModelIndex(Document doc) { Doc = doc; }

        public List<ElementType> Types => _types ?? (_types = new FilteredElementCollector(Doc).WhereElementIsElementType().Cast<ElementType>().ToList());
        public List<Level> Levels => _levels ?? (_levels = new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList());
        public List<View> Views => _views ?? (_views = new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().ToList());
        public List<ViewSheet> Sheets => _sheets ?? (_sheets = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList());

        /// <summary>"Familia: Tipo", o sólo "Tipo" si es único (con el filtro dado).</summary>
        public ElementType FindType(string spec, string op, Func<ElementType, bool> filter = null, string hint = null)
        {
            var s = (spec ?? "").Trim();
            if (s.Length == 0) throw new QueryException(op + ": falta el tipo (\"Familia: Tipo\")");
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Doc.GetElement(Ids.ToId(id)) is ElementType byId)
                return byId;
            var pool = filter == null ? Types : Types.Where(filter);
            var i = s.IndexOf(':');
            List<ElementType> hits;
            if (i > 0)
            {
                var fam = s.Substring(0, i).Trim();
                var name = s.Substring(i + 1).Trim();
                hits = pool.Where(t => string.Equals(t.FamilyName, fam, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else hits = pool.Where(t => string.Equals(t.Name, s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0)
            {
                // Sugerencias: tipos que contienen el texto buscado.
                var key = (i > 0 ? s.Substring(i + 1) : s).Trim();
                var near = pool.Where(t => (t.FamilyName + ": " + t.Name).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                               .Select(t => t.FamilyName + ": " + t.Name).Distinct().Take(5).ToList();
                throw new QueryException(op + ": no existe el tipo '" + spec + "'" + (hint != null ? " (" + hint + ")" : "") +
                                         (near.Count > 0 ? ". Parecidos: " + string.Join(" | ", near) : ". Use catalog para ver los tipos cargados"));
            }
            throw new QueryException(op + ": '" + spec + "' es ambiguo (" + hits.Count + " tipos); use \"Familia: Tipo\"");
        }

        public Level FindLevel(string name, string op)
        {
            var n = (name ?? "").Trim();
            if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Doc.GetElement(Ids.ToId(id)) is Level byId) return byId;
            var hit = Levels.FirstOrDefault(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))
                      ?? Levels.FirstOrDefault(l => string.Equals(l.Name.Replace(" ", ""), n.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new QueryException(op + ": no existe el nivel '" + name + "'. Niveles: " + string.Join(", ", Levels.Select(l => l.Name).Take(30)));
            return hit;
        }

        /// <summary>Vista por id o por nombre exacto (no plantillas). Las láminas también se aceptan por número.</summary>
        public View FindView(object spec, string op, bool allowSheet = true)
        {
            var s = Convert.ToString(spec, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                if (Doc.GetElement(Ids.ToId(id)) is View v && !v.IsTemplate) return v;
                throw new QueryException(op + ": " + id + " no es una vista del modelo");
            }
            var hits = Views.Where(v => !v.IsTemplate && string.Equals(v.Name, s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (allowSheet)
            {
                var sh = Sheets.FirstOrDefault(x => string.Equals(x.SheetNumber, s, StringComparison.OrdinalIgnoreCase));
                if (sh != null) return sh;
            }
            if (hits.Count > 1) throw new QueryException(op + ": hay " + hits.Count + " vistas llamadas '" + s + "' (distinto tipo); use el id");
            throw new QueryException(op + ": no existe la vista '" + s + "'");
        }

        public View FindTemplate(string name, string op)
        {
            var hit = Views.FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                throw new QueryException(op + ": no existe la plantilla de vista '" + name + "'. Plantillas: " +
                                         string.Join(", ", Views.Where(v => v.IsTemplate).Select(v => v.Name).OrderBy(x => x).Take(25)));
            return hit;
        }

        public ViewSheet FindSheet(object spec, string op)
        {
            var s = Convert.ToString(spec, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Doc.GetElement(Ids.ToId(id)) is ViewSheet byId) return byId;
            var hit = Sheets.FirstOrDefault(x => string.Equals(x.SheetNumber, s, StringComparison.OrdinalIgnoreCase))
                      ?? Sheets.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new QueryException(op + ": no existe la lámina '" + s + "' (use número, nombre o id)");
            return hit;
        }

        public Workset FindWorkset(string name, string op)
        {
            if (!Doc.IsWorkshared) throw new QueryException(op + ": el modelo no usa worksets");
            var ws = new FilteredWorksetCollector(Doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => string.Equals(w.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (ws == null)
                throw new QueryException(op + ": no existe el workset '" + name + "'. Worksets: " +
                                         string.Join(", ", new FilteredWorksetCollector(Doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name).OrderBy(x => x).Take(30)));
            return ws;
        }

        /// <summary>
        /// ¿Ya hay una vista con ese nombre? Revit exige nombres únicos por tipo de vista (una planta y un plano de techo pueden llamarse igual);
        /// sin tipo se comparan todas. Las láminas no cuentan: su nombre puede repetir el de una vista.
        /// </summary>
        public bool ViewNameTaken(string name, ViewType? type = null) =>
            Views.Any(v => !v.IsTemplate && !(v is ViewSheet) && (type == null || v.ViewType == type.Value) && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        public static ViewType? ViewTypeFor(string kind)
        {
            switch (kind)
            {
                case "plano": return ViewType.FloorPlan;
                case "rcp": return ViewType.CeilingPlan;
                case "seccion": return ViewType.Section;
                case "3d": return ViewType.ThreeD;
                case "dibujo": return ViewType.DraftingView;
                default: return null;
            }
        }
    }
}
