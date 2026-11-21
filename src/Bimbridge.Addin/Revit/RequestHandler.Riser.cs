// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private async Task<Response> RiserDrawingCheck(Request request, CancellationToken ct)
        {
            var viewId = Arg.GetLong(request.Args, "view");
            var style = Arg.GetString(request.Args, "style");
            if (!viewId.HasValue || viewId <= 0 || string.IsNullOrWhiteSpace(style) ||
                !request.Args.TryGetValue("expected_drawing", out var value) || value is not List<object> rows ||
                rows.Count == 0 || rows.Count > 2000 || rows.Any(r => r is not Dictionary<string, object>))
                return Response.Fail(request.Id, Status.Invalid, "give the drafting view, line style and expected_drawing of the riser");
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
                    error = "the riser must be in a drafting view of the target document";
                    return true;
                }
                using var collector = new FilteredElementCollector(doc);
                var lines = collector.OfClass(typeof(CurveElement)).OwnedByView(view.Id).OfType<DetailCurve>()
                    .Where(c => string.Equals(c.LineStyle?.Name, style, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.GeometryCurve).OfType<Line>()
                    .Select(c => RiserLegKey(c.GetEndPoint(0), c.GetEndPoint(1))).ToHashSet();
                var notes = LightingCallout.InView(doc, view.Id).ToLookup(n => n.Text, StringComparer.OrdinalIgnoreCase);
                using var symbolCollector = new FilteredElementCollector(doc);
                var symbols = symbolCollector.OfClass(typeof(FamilyInstance)).OwnedByView(view.Id).Cast<FamilyInstance>()
                    .Select(f => (Point: (f.Location as LocationPoint)?.Point, Type: f.Symbol.Name, Family: f.Symbol.FamilyName))
                    .Where(s => s.Point != null).ToLookup(s => RiserPointKey(s.Point));
                bool Present(double[] point, string type) => string.IsNullOrWhiteSpace(type) ||
                    symbols[RiserPointKey(RiserPoint(point))].Count(s =>
                        string.Equals(type, s.Type, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, s.Family + ": " + s.Type, StringComparison.OrdinalIgnoreCase)) == 1;
                try
                {
                    foreach (var edge in rows.Cast<Dictionary<string, object>>())
                    {
                        var id = Arg.GetString(edge, "id");
                        if (string.IsNullOrWhiteSpace(id) || !edge.TryGetValue("points", out var points) || !edge.TryGetValue("label", out var label))
                            throw new QueryException("each edge requires ID, points and label position");
                        var path = OpCatalog.Points(points, "riser points", 2, 2000, false);
                        var absentLegs = new List<object>();
                        for (var i = 1; i < path.Count; i++)
                            if (!lines.Contains(RiserLegKey(RiserPoint(path[i - 1]), RiserPoint(path[i]))))
                                absentLegs.Add((long)i);
                        var expected = RiserPoint(OpCatalog.Point(label, "riser label", false));
                        var labelTotal = notes[id].Count();
                        var labelCount = notes[id].Count(n => n.Coord.DistanceTo(expected) <= (1.0 / 304.8) * view.Scale);
                        var startExists = Present(path[0], Arg.GetString(edge, "source_type"));
                        var endExists = Present(path[path.Count - 1], Arg.GetString(edge, "target_type"));
                        if (absentLegs.Count == 0 && labelCount == 1 && labelTotal == 1 && startExists && endExists)
                        {
                            found.Add(new Dictionary<string, object>
                            {
                                ["id"] = id, ["origin"] = Arg.GetString(edge, "origin"), ["target"] = Arg.GetString(edge, "target"),
                                ["open"] = Arg.GetBool(edge, "open") ?? false,
                            });
                        }
                        else missing.Add(new Dictionary<string, object>
                        {
                            ["id"] = id, ["missing_segments"] = absentLegs, ["labels_in_position"] = (long)labelCount,
                            ["labels_total"] = (long)labelTotal, ["source_symbol_ok"] = startExists, ["target_symbol_ok"] = endExists,
                        });
                    }
                }
                catch (QueryException ex) { error = ex.Message; }
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            return new Response
            {
                Id = request.Id, Rev = revision, State = missing.Count > 0 ? Status.CompletedWithDifferences : Status.Ok,
                Data = new Dictionary<string, object> { ["observed"] = found, ["differences"] = missing,
                    ["scope"] = "Checks segments, labels and the given symbol types in the drafting view. MEP connectivity is checked in the MEP model." },
            };
        }

        private static XYZ RiserPoint(double[] point) => new XYZ(point[0], point[1], 0);

        private static string RiserPointKey(XYZ p) => Math.Round(p.X, 4).ToString("0.0000", CultureInfo.InvariantCulture) + "," +
            Math.Round(p.Y, 4).ToString("0.0000", CultureInfo.InvariantCulture);

        private static string RiserLegKey(XYZ a, XYZ b)
        {
            var first = RiserPointKey(a);
            var last = RiserPointKey(b);
            return string.CompareOrdinal(first, last) < 0 ? first + "|" + last : last + "|" + first;
        }
    }
}
