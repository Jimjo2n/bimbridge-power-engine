// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Junta los datos crudos de "lighting_audit" por tramos: luminarias, tags y, si se piden, conteos por vista.
    /// Sólo lectura. El resumen lo hace LightingAudit fuera del hilo de Revit.
    /// </summary>
    public sealed class LightingAuditRunner
    {
        private readonly List<string> _levels;
        private readonly string _viewsArg;
        private readonly bool _exact;
        private int _phase;
        private List<ElementId> _viewIds;
        private int _nextView;

        public readonly List<LightingAudit.Fixture> Fixtures = new List<LightingAudit.Fixture>();
        public readonly List<LightingAudit.Tag> Tags = new List<LightingAudit.Tag>();
        public readonly List<LightingAudit.ViewCount> Views = new List<LightingAudit.ViewCount>();
        /// <summary>Todas las luminarias del modelo (también fuera del filtro de nivel), para no confundir tags de otros niveles con huérfanos.</summary>
        public readonly HashSet<long> AllFixtureIds = new HashSet<long>();
        public List<string> Levels => _levels;
        public long Rev;
        public string Error;
        /// <summary>0.9.1: caché de nombres de la auditoría (aciertos y fallos observados para las métricas).</summary>
        public readonly ElementFields.NameCache Names = new ElementFields.NameCache();
        /// <summary>Luminarias y etiquetas abiertas (observado).</summary>
        public long ElementsRead;

        /// <param name="levels">Filtrar luminarias por nivel (vacío = todas).</param>
        /// <param name="views">null = sin conteo por vista; "rcp" = planos de techo con tags de luminarias; "rcp_todos" = todos los
        /// planos de techo; o ids separados por coma. Con <paramref name="levels"/> sólo las vistas de esos niveles.</param>
        /// <param name="exact">true = luminarias realmente visibles (regenera cada vista; máximo LightingAudit.MaxExactViews).
        /// false = estimación por nivel asociado, sin regenerar nada.</param>
        public LightingAuditRunner(List<string> levels, string views, bool exact)
        {
            _levels = levels ?? new List<string>();
            _viewsArg = string.IsNullOrWhiteSpace(views) ? null : views.Trim();
            _exact = exact;
        }

        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (doc.IsFamilyDocument) { Error = Status.NoSoportado + ": lighting_audit revisa proyectos; el destino " + DocTarget.Current?.Describe() + " es una familia. Use fam info o exec"; return true; }
            var deadline = sw.ElapsedMilliseconds + budgetMs;

            if (_phase == 0)
            {
                Rev = tracker.For(doc)?.Rev ?? 0;
                var ws = doc.IsWorkshared ? doc.GetWorksetTable() : null;
                // 0.9.1: una sola lista de campos y cachés por auditoría (nivel por id de nivel, workset por id): miles de luminarias
                // comparten unas pocas decenas de niveles y worksets.
                var lvlField = new List<string> { "lvl" };
                var wsNames = new Dictionary<int, string>();
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType())
                {
                    AllFixtureIds.Add(Ids.ToLong(e.Id));
                    ElementsRead++;
                    var lvl = ElementFields.Read(doc, e, lvlField, Names)["lvl"] as string;
                    if (_levels.Count > 0 && !_levels.Any(l => string.Equals(l, lvl, StringComparison.OrdinalIgnoreCase))) continue;
                    var fi = e as FamilyInstance;
                    Fixtures.Add(new LightingAudit.Fixture
                    {
                        Id = Ids.ToLong(e.Id),
                        Level = lvl,
                        Workset = ws == null ? null : (wsNames.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : wsNames[e.WorksetId.IntegerValue] = ws.GetWorkset(e.WorksetId)?.Name),
                        Type = fi?.Symbol != null ? fi.Symbol.FamilyName + ": " + fi.Symbol.Name : e.Name,
                    });
                }
                _phase = 1;
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }

            if (_phase == 1)
            {
                // 0.9.1: nombre y nivel de la vista una vez por vista, no por etiqueta (cientos de etiquetas por vista).
                var viewInfo = new Dictionary<long, (string Name, string Level)>();
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtureTags).WhereElementIsNotElementType())
                {
                    if (!(e is IndependentTag tag)) continue;
                    ElementsRead++;
                    var vid = Ids.ToLong(tag.OwnerViewId);
                    if (!viewInfo.TryGetValue(vid, out var vi))
                    {
                        Names.Misses++;
                        var view = doc.GetElement(tag.OwnerViewId) as View;
                        viewInfo[vid] = vi = (view?.Name, (view as ViewPlan)?.GenLevel?.Name);
                    }
                    else Names.Hits++;
                    Tags.Add(new LightingAudit.Tag
                    {
                        Id = Ids.ToLong(tag.Id),
                        ViewId = vid,
                        ViewName = vi.Name,
                        ViewLevel = vi.Level,
                        Tagged = TaggedIds(tag),
                    });
                }
                _phase = 2;
                if (_viewsArg == null) return true;
                _viewIds = ResolveViews(doc);
                if (Error != null) return true;
                if (!_exact)
                {
                    // Estimación por nivel: sólo lee nombres y nivel de cada vista. No regenera nada.
                    foreach (var id in _viewIds)
                        if (doc.GetElement(id) is View v) Views.Add(Describe(v, null));
                    return true;
                }
                if (_viewIds.Count > LightingAudit.MaxExactViews)
                {
                    Error = "exacto=true regenera cada vista en Revit (unos 2 s por vista) y aquí hay " + _viewIds.Count +
                            "; máximo " + LightingAudit.MaxExactViews + ". Acote con lvl= o views=<ids>, o use el modo por nivel (sin exacto).";
                    return true;
                }
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }

            // Fase 2 (sólo exacto): luminarias visibles por vista, una vista por visita como mucho
            // para que Revit siga respondiendo y la cancelación por plazo actúe entre vistas.
            if (_nextView < _viewIds.Count)
            {
                var v = doc.GetElement(_viewIds[_nextView++]) as View;
                if (v != null)
                {
                    var visible = new FilteredElementCollector(doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures)
                        .WhereElementIsNotElementType().ToElementIds().Select(Ids.ToLong).ToArray();
                    Views.Add(Describe(v, visible));
                }
            }
            return _nextView >= _viewIds.Count;
        }

        private static long[] TaggedIds(IndependentTag tag)
        {
            try
            {
                // Sólo elementos del propio documento (no de vínculos). Existe desde Revit 2022.
                return tag.GetTaggedLocalElementIds()
                          .Where(id => id != null && id != ElementId.InvalidElementId)
                          .Select(Ids.ToLong).ToArray();
            }
            catch (Exception) { return new long[0]; }
        }

        private LightingAudit.ViewCount Describe(View v, long[] visible) => new LightingAudit.ViewCount
        {
            ViewId = Ids.ToLong(v.Id),
            Name = v.Name,
            Level = (v as ViewPlan)?.GenLevel?.Name,
            Visible = visible,
        };

        private List<ElementId> ResolveViews(Document doc)
        {
            var all = string.Equals(_viewsArg, "rcp_todos", StringComparison.OrdinalIgnoreCase);
            if (all || string.Equals(_viewsArg, "rcp", StringComparison.OrdinalIgnoreCase))
            {
                // Antes "rcp" eran TODOS los planos de techo (261 en SAMPLE PROJECT: distribución, alarma, cotas…) contados
                // con un colector por vista, que obliga a regenerarlas: Revit quedó ocupado ~10 min. Ahora "rcp" son
                // sólo los planos de techo que ya tienen tags de luminarias (las vistas de iluminación).
                var withTags = new HashSet<long>(Tags.Select(t => t.ViewId));
                return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.CeilingPlan)
                    .Where(v => all || withTags.Contains(Ids.ToLong(v.Id)))
                    .Where(v => _levels.Count == 0 || _levels.Any(l => string.Equals(l, v.GenLevel?.Name, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(v => v.Name)
                    .Select(v => v.Id).ToList();
            }
            var list = new List<ElementId>();
            foreach (var part in _viewsArg.Split(','))
            {
                if (!long.TryParse(part.Trim(), out var id)) { Error = "views: use rcp, rcp_todos o ids de vista separados por coma"; return null; }
                if (!(doc.GetElement(Ids.ToId(id)) is View)) { Error = "views: " + id + " no es una vista del modelo activo"; return null; }
                list.Add(Ids.ToId(id));
            }
            return list;
        }
    }
}
