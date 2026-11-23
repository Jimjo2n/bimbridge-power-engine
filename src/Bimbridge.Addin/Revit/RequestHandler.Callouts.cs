// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> CalloutCheck(Session session, Request request, CancellationToken ct)
        {
            var viewId = Arg.GetLong(request.Args, "view");
            if (!viewId.HasValue || viewId.Value <= 0)
                return Response.Fail(request.Id, Status.Invalid, "give the positive ID of the view");
            var rows = new List<object>();
            var refresh = new List<long>();
            string error = null;
            long revision = 0;
            var differences = false;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                if (!(doc.GetElement(Ids.ToId(viewId.Value)) is View view) || view.IsTemplate)
                {
                    error = "that view does not exist in the target document";
                    return true;
                }
                var notes = LightingCallout.InView(doc, view.Id).Select(note => (Note: note, Config: LightingCallout.Read(note)))
                    .Where(x => x.Config != null);
                foreach (var group in notes.GroupBy(x => Arg.GetString(x.Config, "uid")))
                {
                    var duplicates = group.Count() > 1;
                    foreach (var (note, config) in group)
                    {
                        var id = Ids.ToLong(note.Id);
                        var row = new Dictionary<string, object> { ["id"] = id, ["text"] = note.Text, ["fixture_uid"] = group.Key };
                        rows.Add(row);
                        if (duplicates)
                        {
                            row["status"] = "duplicated";
                            differences = true;
                            continue;
                        }
                        try
                        {
                            var state = LightingCallout.Observe(doc, config);
                            row["fixture_id"] = state.Id;
                            row["expected_text"] = state.Text;
                            var current = LightingCallout.IsCurrent(note, config, state.Text, state.Point, state.TypeUid);
                            row["status"] = current ? "current_valid" : "outdated";
                            if (!current) { differences = true; refresh.Add(id); }
                        }
                        catch (QueryException ex)
                        {
                            row["status"] = "origin_not_evaluable";
                            row["reason"] = ex.Message;
                            differences = true;
                        }
                    }
                }
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            var result = new Dictionary<string, object> { ["view"] = viewId.Value, ["total"] = (long)rows.Count, ["calls"] = rows };
            const string set = "$llamadas_desactualizadas";
            if (refresh.Count > 0)
            {
                session.Sets.Save(set, refresh, TargetId, TargetTitle);
                result["set_to_regenerate"] = set;
            }
            else session.Sets.Remove(set);
            return new Response { Id = request.Id, Rev = revision, Data = result, State = differences ? Status.CompletedWithDifferences : Status.Ok };
        }
    }
}
