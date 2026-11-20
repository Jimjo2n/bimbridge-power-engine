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
            var selector = Arg.GetString(request.Args, "tabla")?.Trim();
            if (string.IsNullOrEmpty(selector)) return Response.Fail(request.Id, Status.Invalido, "indique tabla= con el ID o nombre exacto");
            ScheduleAudit audit;
            try { audit = new ScheduleAudit(request.Args); }
            catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }

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
                    if (matches.Count > 1) { error = "hay varias tablas con ese nombre; indique su ID"; return true; }
                    schedule = matches.SingleOrDefault();
                }
                if (schedule == null || schedule.IsTemplate) { error = "no existe esa tabla en el documento destino"; return true; }
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
                        ["campo"] = field.GetName(), ["parametro_id"] = Ids.ToLong(field.ParameterId),
                        ["operador"] = filter.FilterType.ToString(), ["valor"] = value,
                    });
                }

                List<long> population = null;
                string limitation = null;
                if (audit.NeedsPopulation)
                {
                    if (definition.IncludeLinkedFiles || definition.HasEmbeddedSchedule)
                        limitation = "La población incluye vínculos o una tabla incrustada. El recuento de elementos del anfitrión no la representa.";
                    else if (!FilteredElementCollector.IsViewValidForElementIteration(doc, schedule.Id))
                        limitation = "Revit no permite enumerar la población de esta tabla.";
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
                data["tabla"] = schedule.Name;
                data["tabla_id"] = Ids.ToLong(schedule.Id);
                data["doc"] = DocRegistry.Instance.IdOf(doc);
                data["filas_cuerpo"] = (long)body.NumberOfRows;
                data["columnas_cuerpo"] = (long)body.NumberOfColumns;
                if (limitation != null) data["motivo_poblacion"] = limitation;
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            var outcome = Arg.GetString(data, "resultado");
            return new Response
            {
                Id = request.Id, Rev = revision, Datos = data,
                Estado = outcome == "con_diferencias" || outcome == "no_evaluable" ? Status.ConDiferencias : Status.Ok,
            };
        }
    }
}
