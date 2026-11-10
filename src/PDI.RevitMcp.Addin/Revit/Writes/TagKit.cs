// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Revit.Writes
{
    /// <summary>
    /// Cómo están etiquetados los elementos en una vista: quién ya tiene tag (regla: un tag por elemento) y con qué estilo
    /// (tipo, desplazamiento de la cabeza respecto al elemento, leader, orientación). Lo usan el plan (tag) y el aplicador
    /// (tag sobre elementos que crea el mismo plan, @n), así un tag nuevo se ve igual que sus vecinos.
    /// </summary>
    internal sealed class TagKit
    {
        public sealed class Style
        {
            public long TypeId;
            public double Dx, Dy;
            public bool Leader;
            public bool Vertical;
        }

        public sealed class Choice
        {
            public long TypeId;
            public XYZ Head;
            public bool Leader;
            public bool Vertical;
            public string Note;
        }

        public readonly View View;
        public readonly HashSet<long> Tagged = new HashSet<long>();
        private readonly Dictionary<long, List<Style>> _styles = new Dictionary<long, List<Style>>();
        private readonly Document _doc;

        private TagKit(Document doc, View view) { _doc = doc; View = view; }

        /// <summary>Lee los tags de la vista (una pasada con filtro rápido: ~ms incluso con cientos de tags).</summary>
        public static TagKit For(Document doc, View view)
        {
            var kit = new TagKit(doc, view);
            if (view == null) return kit;
            // Filtro rápido por vista dueña (incluye tags ocultos, que también cuentan para "un tag por elemento").
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).WherePasses(new ElementOwnerViewFilter(view.Id)).Cast<IndependentTag>())
            {
                ICollection<ElementId> local;
                try { local = t.GetTaggedLocalElementIds(); } catch (Exception) { continue; }
                foreach (var lid in local)
                {
                    if (lid == null || lid == ElementId.InvalidElementId) continue;
                    kit.Tagged.Add(Ids.ToLong(lid));
                    var host = doc.GetElement(lid);
                    var loc = host == null ? null : WriteKit.LocationOf(host);
                    if (loc == null || t.Category == null) continue;
                    XYZ head;
                    try { head = t.TagHeadPosition; } catch (Exception) { continue; }
                    var key = Ids.ToLong(t.Category.Id);
                    if (!kit._styles.TryGetValue(key, out var l)) kit._styles[key] = l = new List<Style>();
                    bool leader = false, vertical = false;
                    try { leader = t.HasLeader; vertical = t.TagOrientation == TagOrientation.Vertical; } catch (Exception) { }
                    l.Add(new Style { TypeId = Ids.ToLong(t.GetTypeId()), Dx = head.X - loc.X, Dy = head.Y - loc.Y, Leader = leader, Vertical = vertical });
                }
            }
            return kit;
        }

        /// <summary>Un tag más en esta vista (para no etiquetar dos veces lo mismo dentro del mismo plan).</summary>
        public void Remember(long elemId, long tagCategoryId, Style s)
        {
            Tagged.Add(elemId);
            if (s == null) return;
            if (!_styles.TryGetValue(tagCategoryId, out var l)) _styles[tagCategoryId] = l = new List<Style>();
            l.Add(s);
        }

        /// <summary>
        /// Tipo, cabeza, leader y orientación del tag para <paramref name="e"/>: el tipo pedido, el más usado en la vista para su categoría,
        /// o el predeterminado; desplazamiento = mediana de los tags del mismo tipo en la vista, o <paramref name="offsetPaper"/> (pulgadas de papel).
        /// </summary>
        public bool TryChoose(Element e, ElementType forcedType, double[] offsetPaper, bool? leaderArg, string orientArg, int scale,
            out Choice choice, out string why)
        {
            choice = null; why = null;
            var loc = WriteKit.LocationOf(e);
            if (loc == null) { why = "no tiene posición"; return false; }
            var tagBic = PlanBuilder.TagCategoryFor(e.Category);
            if (tagBic == BuiltInCategory.INVALID && forcedType == null) { why = "su categoría no tiene tags"; return false; }
            var tagCatId = tagBic == BuiltInCategory.INVALID ? -1 : Ids.ToLong(new ElementId(tagBic));
            _styles.TryGetValue(tagCatId, out var sameCat);

            long typeId;
            if (forcedType != null) typeId = Ids.ToLong(forcedType.Id);
            else if (sameCat != null && sameCat.Count > 0) typeId = sameCat.GroupBy(s => s.TypeId).OrderByDescending(g => g.Count()).First().Key;
            else
            {
                ElementId def = null;
                try { def = _doc.GetDefaultFamilyTypeId(new ElementId(tagBic)); } catch (Exception) { }
                if (def == null || def == ElementId.InvalidElementId) { why = "no hay tipo de tag cargado para su categoría (dé tag_type)"; return false; }
                typeId = Ids.ToLong(def);
            }

            var same = sameCat?.Where(s => s.TypeId == typeId).ToList();
            double dx, dy; bool leader, vertical; string note;
            if (offsetPaper == null && same != null && same.Count > 0)
            {
                dx = Median(same.Select(s => s.Dx)); dy = Median(same.Select(s => s.Dy));
                leader = same.Count(s => s.Leader) * 2 > same.Count;
                vertical = same.Count(s => s.Vertical) * 2 > same.Count;
                note = "copiado de " + same.Count + " tags del mismo tipo en la vista";
            }
            else
            {
                dx = offsetPaper != null ? offsetPaper[0] / 12.0 * scale : 0;
                dy = offsetPaper != null ? offsetPaper[1] / 12.0 * scale : 0;
                leader = false; vertical = false;
                note = offsetPaper == null ? "sin tags de referencia en la vista: cabeza sobre el elemento" : null;
            }
            if (leaderArg.HasValue) leader = leaderArg.Value;
            if (orientArg != null) vertical = orientArg == "vertical";
            choice = new Choice { TypeId = typeId, Head = new XYZ(loc.X + dx, loc.Y + dy, loc.Z), Leader = leader, Vertical = vertical, Note = note };
            return true;
        }

        public static long TagCategoryId(Element e)
        {
            var bic = PlanBuilder.TagCategoryFor(e?.Category);
            return bic == BuiltInCategory.INVALID ? -1 : Ids.ToLong(new ElementId(bic));
        }

        private static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(x => x).ToArray();
            if (a.Length == 0) return 0;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        public static string TypeLabel(Document doc, long typeId) =>
            doc.GetElement(Ids.ToId(typeId)) is ElementType et ? et.FamilyName + ": " + et.Name : typeId.ToString(CultureInfo.InvariantCulture);
    }
}
