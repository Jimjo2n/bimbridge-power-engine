// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Revit.Writes;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> AnnotationAuditCheck(Request request, CancellationToken ct)
        {
            List<long> ids;
            try { ids = RouteIds(request.Args, "ids"); }
            catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
            if (ids.Count == 0 || ids.Count > 100) return Response.Fail(request.Id, Status.Invalid, "give 1 to 100 dimension or tag IDs");
            var rows = new List<object>();
            var differences = false;
            string error = null;
            long rev = 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                foreach (var id in ids)
                {
                    try
                    {
                        var original = doc.GetElement(Ids.ToId(id));
                        var data = AnnotationRepair.Inspect(original);
                        data["estado"] = "referencias_disponibles";
                        rows.Add(data);
                    }
                    catch (QueryException ex)
                    {
                        differences = true;
                        rows.Add(new Dictionary<string, object> { ["id"] = id, ["estado"] = "no_evaluable", ["motivo"] = ex.Message });
                    }
                }
                rev = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: System.TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            return new Response { Id = request.Id, Rev = rev, State = differences ? Status.CompletedWithDifferences : Status.Ok,
                Data = new Dictionary<string, object> { ["anotaciones"] = rows } };
        }
    }
}
