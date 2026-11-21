// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> ScheduleCheck(Request request, CancellationToken ct)
        {
            var selector = Arg.GetString(request.Args, "schedule")?.Trim();
            if (string.IsNullOrEmpty(selector)) return Response.Fail(request.Id, Status.Invalid, "give schedule= with the ID or exact name");
            ScheduleAudit audit;
            try { audit = new ScheduleAudit(request.Args); }
            catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }

            Dictionary<string, object> data = null;
            string error = null;
            long revision = 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                ViewSchedule schedule;
                if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    schedule = doc.GetElement(Ids.ToId(id)) as ViewSchedule;
                else
                {
                    using var collector = new FilteredElementCollector(doc);
                    var matches = collector.OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                        .Where(v => !v.IsTemplate && string.Equals(v.Name, selector, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                    if (matches.Count > 1) { error = "there are several schedules with that name; give its ID"; return true; }
                    schedule = matches.SingleOrDefault();
                }
                if (schedule == null || schedule.IsTemplate) { error = "that schedule does not exist in the target document"; return true; }
                var definition = schedule.Definition;
                var filters = new List<object>();
                foreach (var filter in definition.GetFilters())
                {
                    var field = definition.GetField(filter.FieldId);
                    object value = null;
                    if (filter.IsStringValue) value = filter.GetStringValue();
                    else if (filter.IsIntegerValue) value = (long)filter.GetIntegerValue();
                    else if (filter.IsDoubleValue) value = filter.GetDoubleValue();
                    else if (filter.IsElementIdValue) value = Ids.ToLong(filter.GetElementIdValue());
                    filters.Add(new Dictionary<string, object>
                    {
                        ["field"] = field.GetName(), ["parameter_id"] = Ids.ToLong(field.ParameterId),
                        ["operator"] = filter.FilterType.ToString(), ["value"] = value,
                    });
                }

                List<long> population = null;
                string limitation = null;
                if (audit.NeedsPopulation)
                {
                    if (definition.IncludeLinkedFiles || definition.HasEmbeddedSchedule)
                        limitation = "The population includes links or an embedded schedule. The host element count does not represent it.";
                    else if (!FilteredElementCollector.IsViewValidForElementIteration(doc, schedule.Id))
                        limitation = "Revit does not allow enumerating the population of this schedule.";
                    else
                    {
                        using var collector = new FilteredElementCollector(doc, schedule.Id);
                        population = collector.WhereElementIsNotElementType().ToElementIds().Select(Ids.ToLong).ToList();
                    }
                }
                using var table = schedule.GetTableData();
                using var body = table.GetSectionData(SectionType.Body);
                data = audit.Compare(population, filters, cell =>
                    cell.Row >= body.NumberOfRows || cell.Column >= body.NumberOfColumns ? null :
                        schedule.GetCellText(SectionType.Body, body.FirstRowNumber + cell.Row, body.FirstColumnNumber + cell.Column));
                data["schedule"] = schedule.Name;
                data["schedule_id"] = Ids.ToLong(schedule.Id);
                data["doc"] = DocRegistry.Instance.IdOf(doc);
                data["body_rows"] = (long)body.NumberOfRows;
                data["body_columns"] = (long)body.NumberOfColumns;
                if (limitation != null) data["population_reason"] = limitation;
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            var outcome = Arg.GetString(data, "result");
            return new Response
            {
                Id = request.Id, Rev = revision, Data = data,
                State = outcome == "with_differences" || outcome == "not_evaluable" ? Status.CompletedWithDifferences : Status.Ok,
            };
        }
    }
}
