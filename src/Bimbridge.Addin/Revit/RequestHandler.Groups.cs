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
            var id = Arg.GetLong(request.Args, "group");
            if (!id.HasValue || id <= 0) return Response.Fail(request.Id, Status.Invalid, "give the ID of the current group");
            List<object> previous = null;
            if (request.Args.TryGetValue("previous", out var raw))
            {
                previous = raw as List<object>;
                if (previous == null) return Response.Fail(request.Id, Status.Invalid, "previous requires the members list from the previous check");
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
                    error = "the group does not exist in the target document";
                    return true;
                }
                var snapshot = group.GetMemberIds().Select(doc.GetElement).Where(e => e != null).OrderBy(e => Ids.ToLong(e.Id))
                    .Select(e => (object)new Dictionary<string, object>
                    {
                        ["id"] = Ids.ToLong(e.Id), ["uid"] = e.UniqueId,
                        ["signature"] = e.Location == null && e.get_BoundingBox(null) == null ? null : Bim.Groups.Signature(group, e),
                    }).ToList();
                data = Bim.Groups.Inspect(group);
                data["members_snapshot"] = snapshot;
                data["level_id"] = Ids.ToLong(group.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId());
                data["workset_id"] = (long)group.WorksetId.IntegerValue;
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: System.TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            var status = Status.Ok;
            if (previous != null)
            {
                try
                {
                    var map = GroupMemberMap.Compare(previous, (List<object>)data["members_snapshot"]);
                    data["reconciliation"] = map;
                    if (!(bool)map["complete"]) status = Status.CompletedWithDifferences;
                }
                catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
            }
            return new Response { Id = request.Id, Rev = revision, Data = data, State = status };
        }
    }
}
