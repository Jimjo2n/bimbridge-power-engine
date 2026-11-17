// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Addin.Scripting;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> VisibilityCheck(Request request, CancellationToken ct)
        {
            static long[] IdList(Dictionary<string, object> args, string key) => Arg.GetStringList(args, key)
                .Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
                .Distinct().ToArray();
            var views = IdList(request.Args, "vistas");
            var ids = IdList(request.Args, "ids");
            if (views.Length == 0 || ids.Length == 0 || views.Length > 8 || (long)views.Length * ids.Length > 100 ||
                views.Contains(0) || ids.Contains(0))
                return Response.Fail(request.Id, Status.Invalido, "indique IDs positivos en vistas e ids. Máximo 8 vistas y 100 pares vista-elemento.");

            var rows = new List<object>();
            string error = null;
            long rev = 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                var targets = ids.Select(id => (Id: id, Element: doc.GetElement(Ids.ToId(id)))).ToList();
                foreach (var id in views)
                {
                    if (!(doc.GetElement(Ids.ToId(id)) is View view) || view.IsTemplate || !view.AreGraphicsOverridesAllowed() ||
                        !FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
                    {
                        error = "la vista " + id + " no admite diagnóstico de visibilidad";
                        return true;
                    }
                    var existing = targets.Where(e => e.Element != null).Select(e => e.Element.Id).ToList();
                    var candidates = new HashSet<ElementId>();
                    if (existing.Count > 0)
                    {
                        using var collector = new FilteredElementCollector(doc, view.Id);
                        candidates.UnionWith(collector.WherePasses(new ElementIdSetFilter(existing)).ToElementIds());
                    }
                    var orderedFilters = view.GetOrderedFilters().Select(filterId =>
                        (Id: filterId, Filter: doc.GetElement(filterId), Enabled: view.GetIsFilterEnabled(filterId),
                         Visible: view.GetFilterVisibility(filterId), Graphics: Colors(view.GetFilterOverrides(filterId)))).ToList();
                    var elements = new List<object>();
                    foreach (var target in targets)
                    {
                        var element = target.Element;
                        if (element == null)
                        {
                            elements.Add(new Dictionary<string, object> { ["id"] = target.Id, ["estado"] = "no_encontrado" });
                            continue;
                        }
                        var diagnosis = Bim.Vistas.PorQueNoSeVe(view, element);
                        diagnosis["en_colector_de_vista"] = candidates.Contains(element.Id);
                        diagnosis["graficos_elemento"] = Colors(view.GetElementOverrides(element.Id));
                        diagnosis["filtros_coincidentes"] = orderedFilters
                            .Where(filter => Bim.Vistas.MatchesFilter(filter.Filter, element))
                            .Select(filter => new Dictionary<string, object>
                            {
                                ["id"] = Ids.ToLong(filter.Id), ["nombre"] = filter.Filter.Name, ["activo"] = filter.Enabled,
                                ["visible"] = filter.Visible, ["graficos"] = filter.Graphics,
                            }).ToList();
                        if (element is RevitLinkInstance link)
                        {
                            diagnosis["vinculo_cargado"] = link.GetLinkDocument() != null;
#if REVIT2024_OR_GREATER
                            using var settings = view.GetLinkOverrides(link.Id) ?? view.GetLinkOverrides(link.GetTypeId());
                            diagnosis["modo_vinculo"] = settings?.LinkVisibilityType.ToString();
                            diagnosis["vista_vinculada_id"] = settings == null ? null : (object)Ids.ToLong(settings.LinkedViewId);
#endif
                        }
                        elements.Add(diagnosis);
                    }
                    rows.Add(new Dictionary<string, object>
                    {
                        ["id"] = id, ["nombre"] = view.Name, ["plantilla"] = doc.GetElement(view.ViewTemplateId)?.Name,
                        ["plantilla_id"] = Ids.ToLong(view.ViewTemplateId), ["elementos"] = elements,
                    });
                }
                rev = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            return new Response
            {
                Id = request.Id, Rev = rev, Datos = new Dictionary<string, object>
                {
                    ["vistas"] = rows,
                    ["alcance"] = "El colector identifica candidatos de la vista. Las causas y los overrides son evidencia de configuración. Compruebe el render final para recortes y colores.",
                },
            };
        }

        private static Dictionary<string, object> Colors(OverrideGraphicSettings graphics)
        {
            using (graphics)
            {
                var colors = new Dictionary<string, object>();
                void Add(string key, Color color)
                {
                    if (color != null && color.IsValid) colors[key] = new object[] { (long)color.Red, (long)color.Green, (long)color.Blue };
                }
                Add("linea_proyeccion", graphics.ProjectionLineColor);
                Add("linea_corte", graphics.CutLineColor);
                Add("relleno_superficie", graphics.SurfaceForegroundPatternColor);
                Add("relleno_corte", graphics.CutForegroundPatternColor);
                return colors;
            }
        }
    }
}
