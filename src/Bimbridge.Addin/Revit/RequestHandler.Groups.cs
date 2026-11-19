// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
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
        private async Task<Response> GroupAuditCheck(Request request, CancellationToken ct)
        {
            var id = Arg.GetLong(request.Args, "grupo");
            if (!id.HasValue || id <= 0) return Response.Fail(request.Id, Status.Invalido, "indique el ID del grupo actual");
            List<object> previous = null;
            if (request.Args.TryGetValue("anterior", out var raw))
            {
                previous = raw as List<object>;
                if (previous == null) return Response.Fail(request.Id, Status.Invalido, "anterior exige la lista miembros del control previo");
            }
            Dictionary<string, object> data = null;
            string error = null;
            long revision = 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                if (!(doc.GetElement(Ids.ToId(id.Value)) is Group group))
                {
                    error = "no existe el grupo en el documento destino";
                    return true;
                }
                var snapshot = group.GetMemberIds().Select(doc.GetElement).Where(e => e != null).OrderBy(e => Ids.ToLong(e.Id))
                    .Select(e => (object)new Dictionary<string, object>
                    {
                        ["id"] = Ids.ToLong(e.Id), ["uid"] = e.UniqueId,
                        ["firma"] = e.Location == null && e.get_BoundingBox(null) == null ? null : Bim.Grupos.Firma(group, e),
                    }).ToList();
                data = Bim.Grupos.Inspeccionar(group);
                data["instantanea_miembros"] = snapshot;
                data["nivel_id"] = Ids.ToLong(group.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId());
                data["workset_id"] = (long)group.WorksetId.IntegerValue;
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: System.TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            var status = Status.Ok;
            if (previous != null)
            {
                try
                {
                    var map = GroupMemberMap.Compare(previous, (List<object>)data["instantanea_miembros"]);
                    data["reconciliacion"] = map;
                    if (!(bool)map["completo"]) status = Status.ConDiferencias;
                }
                catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }
            }
            return new Response { Id = request.Id, Rev = revision, Datos = data, Estado = status };
        }
    }
}
