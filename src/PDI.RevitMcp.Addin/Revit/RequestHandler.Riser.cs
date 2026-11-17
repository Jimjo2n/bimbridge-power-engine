// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> RiserDrawingCheck(Request request, CancellationToken ct)
        {
            var viewId = Arg.GetLong(request.Args, "vista");
            var style = Arg.GetString(request.Args, "estilo");
            if (!viewId.HasValue || viewId <= 0 || string.IsNullOrWhiteSpace(style) ||
                !request.Args.TryGetValue("esperado_dibujo", out var value) || value is not List<object> rows ||
                rows.Count == 0 || rows.Count > 2000 || rows.Any(r => r is not Dictionary<string, object>))
                return Response.Fail(request.Id, Status.Invalido, "indique vista de dibujo, estilo de línea y esperado_dibujo del riser");
            var found = new List<object>();
            var missing = new List<object>();
            string error = null;
            long revision = 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                if (!(doc.GetElement(Ids.ToId(viewId.Value)) is ViewDrafting view) || view.IsTemplate)
                {
                    error = "el riser debe estar en una vista de dibujo del documento destino";
                    return true;
                }
                using var collector = new FilteredElementCollector(doc);
                var lines = collector.OfClass(typeof(CurveElement)).OwnedByView(view.Id).OfType<DetailCurve>()
                    .Where(c => string.Equals(c.LineStyle?.Name, style, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.GeometryCurve).OfType<Line>()
                    .Select(c => RiserLegKey(c.GetEndPoint(0), c.GetEndPoint(1))).ToHashSet();
                var notes = LightingCallout.InView(doc, view.Id).ToLookup(n => n.Text, StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var edge in rows.Cast<Dictionary<string, object>>())
                    {
                        var id = Arg.GetString(edge, "id");
                        if (string.IsNullOrWhiteSpace(id) || !edge.TryGetValue("puntos", out var points) || !edge.TryGetValue("rotulo", out var label))
                            throw new QueryException("cada arista exige ID, puntos y posición del rótulo");
                        var path = OpCatalog.Points(points, "puntos del riser", 2, 2000, false);
                        var absentLegs = new List<object>();
                        for (var i = 1; i < path.Count; i++)
                            if (!lines.Contains(RiserLegKey(RiserPoint(path[i - 1]), RiserPoint(path[i]))))
                                absentLegs.Add((long)i);
                        var expected = RiserPoint(OpCatalog.Point(label, "rótulo del riser", false));
                        var labelCount = notes[id].Count(n => n.Coord.DistanceTo(expected) <= (1.0 / 304.8) * view.Scale);
                        if (absentLegs.Count == 0 && labelCount == 1)
                        {
                            found.Add(new Dictionary<string, object>
                            {
                                ["id"] = id, ["origen"] = Arg.GetString(edge, "origen"), ["destino"] = Arg.GetString(edge, "destino"),
                                ["abierto"] = Arg.GetBool(edge, "abierto") ?? false,
                            });
                        }
                        else missing.Add(new Dictionary<string, object> { ["id"] = id, ["tramos_faltantes"] = absentLegs, ["rotulos_en_posicion"] = (long)labelCount });
                    }
                }
                catch (QueryException ex) { error = ex.Message; }
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            return new Response
            {
                Id = request.Id, Rev = revision, Estado = missing.Count > 0 ? Status.ConDiferencias : Status.Ok,
                Datos = new Dictionary<string, object> { ["observado"] = found, ["diferencias"] = missing,
                    ["alcance"] = "Tramos por estilo y rótulos en la vista de dibujo. No mide posición visual de símbolos ni conexiones MEP." },
            };
        }

        private static XYZ RiserPoint(double[] point) => new XYZ(point[0], point[1], 0);

        private static string RiserLegKey(XYZ a, XYZ b)
        {
            static string Key(XYZ p) => Math.Round(p.X, 4).ToString("0.0000", CultureInfo.InvariantCulture) + "," +
                Math.Round(p.Y, 4).ToString("0.0000", CultureInfo.InvariantCulture);
            var first = Key(a);
            var last = Key(b);
            return string.CompareOrdinal(first, last) < 0 ? first + "|" + last : last + "|" + first;
        }
    }
}
