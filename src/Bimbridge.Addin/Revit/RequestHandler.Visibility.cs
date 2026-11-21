// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Scripting;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> VisibilityCheck(Request request, CancellationToken ct)
        {
            static long[] IdList(Dictionary<string, object> args, string key) => Arg.GetStringList(args, key)
                .Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
                .Distinct().ToArray();
            var views = IdList(request.Args, "views");
            var ids = IdList(request.Args, "ids");
            if (views.Length == 0 || ids.Length == 0 || views.Length > 8 || (long)views.Length * ids.Length > 100 ||
                views.Contains(0) || ids.Contains(0))
                return Response.Fail(request.Id, Status.Invalid, "give positive IDs in views and ids. Maximum 8 views and 100 view-element pairs.");
            VisibilityAudit audit;
            try { audit = new VisibilityAudit(request.Args, views, ids); }
            catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }

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
                        error = "view " + id + " does not support visibility diagnosis";
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
                            elements.Add(new Dictionary<string, object> { ["id"] = target.Id, ["status"] = "not_found" });
                            continue;
                        }
                        var diagnosis = Bim.Views.WhyNotVisible(view, element);
                        diagnosis["in_view_collector"] = candidates.Contains(element.Id);
                        diagnosis["element_graphics"] = Colors(view.GetElementOverrides(element.Id));
                        diagnosis["matching_filters"] = orderedFilters
                            .Where(filter => Bim.Views.MatchesFilter(filter.Filter, element))
                            .Select(filter => new Dictionary<string, object>
                            {
                                ["id"] = Ids.ToLong(filter.Id), ["name"] = filter.Filter.Name, ["active"] = filter.Enabled,
                                ["visible"] = filter.Visible, ["graphics"] = filter.Graphics,
                            }).ToList();
                        if (element is RevitLinkInstance link)
                        {
                            diagnosis["link_loaded"] = link.GetLinkDocument() != null;
#if REVIT2024_OR_GREATER
                            using var settings = view.GetLinkOverrides(link.Id) ?? view.GetLinkOverrides(link.GetTypeId());
                            diagnosis["link_mode"] = settings?.LinkVisibilityType.ToString();
                            diagnosis["linked_view_id"] = settings == null ? null : (object)Ids.ToLong(settings.LinkedViewId);
#endif
                        }
                        elements.Add(diagnosis);
                    }
                    rows.Add(new Dictionary<string, object>
                    {
                        ["id"] = id, ["name"] = view.Name, ["template"] = doc.GetElement(view.ViewTemplateId)?.Name,
                        ["template_id"] = Ids.ToLong(view.ViewTemplateId), ["elements"] = elements,
                    });
                }
                rev = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            var differences = audit.Compare(rows);
            return new Response
            {
                Id = request.Id, Rev = rev, State = differences.Count == 0 ? Status.Ok : Status.CompletedWithDifferences,
                Data = new Dictionary<string, object>
                {
                    ["views"] = rows,
                    ["differences"] = differences,
                    ["scope"] = "The collector identifies the view's candidates. The causes and overrides are configuration evidence. Check the final render for crops and colors.",
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
                Add("projection_line", graphics.ProjectionLineColor);
                Add("cut_line", graphics.CutLineColor);
                Add("surface_fill", graphics.SurfaceForegroundPatternColor);
                Add("cut_fill", graphics.CutForegroundPatternColor);
                return colors;
            }
        }
    }
}
