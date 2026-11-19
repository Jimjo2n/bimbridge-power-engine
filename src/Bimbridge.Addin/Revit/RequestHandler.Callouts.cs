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
            var viewId = Arg.GetLong(request.Args, "vista");
            if (!viewId.HasValue || viewId.Value <= 0)
                return Response.Fail(request.Id, Status.Invalido, "indique el ID positivo de la vista");
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
                    error = "no existe esa vista en el documento destino";
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
                        var row = new Dictionary<string, object> { ["id"] = id, ["texto"] = note.Text, ["uid_luminaria"] = group.Key };
                        rows.Add(row);
                        if (duplicates)
                        {
                            row["estado"] = "duplicada";
                            differences = true;
                            continue;
                        }
                        try
                        {
                            var state = LightingCallout.Observe(doc, config);
                            row["luminaria_id"] = state.Id;
                            row["texto_esperado"] = state.Text;
                            var current = LightingCallout.IsCurrent(note, config, state.Text, state.Point, state.TypeUid);
                            row["estado"] = current ? "vigente" : "desactualizada";
                            if (!current) { differences = true; refresh.Add(id); }
                        }
                        catch (QueryException ex)
                        {
                            row["estado"] = "origen_no_evaluable";
                            row["motivo"] = ex.Message;
                            differences = true;
                        }
                    }
                }
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            var result = new Dictionary<string, object> { ["vista"] = viewId.Value, ["total"] = (long)rows.Count, ["llamadas"] = rows };
            const string set = "$llamadas_desactualizadas";
            if (refresh.Count > 0)
            {
                session.Sets.Save(set, refresh, TargetId, TargetTitle);
                result["conjunto_para_regenerar"] = set;
            }
            else session.Sets.Remove(set);
            return new Response { Id = request.Id, Rev = revision, Datos = result, Estado = differences ? Status.ConDiferencias : Status.Ok };
        }
    }
}
