// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Bimbridge.Addin.Revit.Writes;
using Bimbridge.Engine;
using Bimbridge.Engine.Qc;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> TerminalCheck(Request request, CancellationToken ct, bool equipment = false)
        {
            var scope = equipment ? "equipment" : "terminals";
            if (!request.Args.TryGetValue(scope, out var raw) || !(raw is List<object> input) ||
                input.Count == 0 || input.Count > 200 || input.Any(x => x is not Dictionary<string, object>))
                return Response.Fail(request.Id, Status.Invalid, scope + " requires 1 to 200 objects with the markup expectations");
            var specs = input.Cast<Dictionary<string, object>>().ToList();
            var unexpected = new List<long>();
            if (equipment && request.Args.ContainsKey("observed_ids"))
            {
                try { unexpected = RouteIds(request.Args, "observed_ids").Except(specs.Select(x => Arg.GetLong(x, "id") ?? 0)).ToList(); }
                catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
            }
            var rows = new List<object>();
            string error = null;
            long revision = 0;
            var differs = unexpected.Count > 0;
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                foreach (var spec in specs)
                {
                    try
                    {
                        var row = InspectTerminal(doc, spec, equipment, out var ok);
                        rows.Add(row);
                        differs |= !ok;
                    }
                    catch (Exception ex) when (ex is QueryException || ex is FormatException || ex is OverflowException)
                    {
                        error = ex.Message;
                        return true;
                    }
                }
                revision = _tracker.For(doc)?.Rev ?? 0;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);
            return new Response
            {
                Id = request.Id, Rev = revision, State = differs ? Status.CompletedWithDifferences : Status.Ok,
                Data = new Dictionary<string, object> { [scope] = rows, ["unexpected_ids"] = unexpected },
            };
        }

        private static Dictionary<string, object> InspectTerminal(Document doc, Dictionary<string, object> spec, bool equipment, out bool ok)
        {
            const double tolerance = 1.0 / 304.8;
            if (!(Arg.GetLong(spec, "id") is long id) || id <= 0 || !spec.TryGetValue("xy_ft", out var xy))
                throw new QueryException("terminals: each inspection requires the markup id and xy_ft");
            var expected = OpCatalog.Point(xy, "terminals xy_ft", false);
            var offset = OpCatalog.Number(spec, "elev_ft", "terminals") ?? throw new QueryException("missing elev_ft");
            var expectedType = Arg.GetString(spec, "type");
            var expectedLevel = Arg.GetString(spec, "level");
            var direction = Arg.GetString(spec, "direction");
            var parameter = Arg.GetString(spec, "direction_parameter");
            if (string.IsNullOrWhiteSpace(expectedType) || string.IsNullOrWhiteSpace(expectedLevel))
                throw new QueryException("missing the expected type or level");
            if (!equipment && ((direction != "UP" && direction != "DN") || string.IsNullOrWhiteSpace(parameter)))
                throw new QueryException("missing direction UP/DN or direction_parameter");
            var diagnostics = new List<object>();
            var row = new Dictionary<string, object> { ["id"] = id, ["ref"] = Arg.GetString(spec, "ref"), ["differences"] = diagnostics };
            if (!(doc.GetElement(Ids.ToId(id)) is FamilyInstance terminal))
            {
                diagnostics.Add("terminal not found");
                ok = false;
                return row;
            }
            var level = WriteKit.LevelOf(doc, terminal);
            if (!string.Equals(level?.Name, expectedLevel, StringComparison.OrdinalIgnoreCase)) diagnostics.Add("level");
            var type = WriteKit.TypeName(doc, terminal);
            if (!string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(terminal.Symbol.Name, expectedType, StringComparison.OrdinalIgnoreCase)) diagnostics.Add("type");
            var loc = terminal.Location as LocationPoint;
            var point = loc?.Point;
            if (point == null || level == null || point.DistanceTo(new XYZ(expected[0], expected[1], level.ProjectElevation + offset)) > tolerance)
                diagnostics.Add("position");
            if (OpCatalog.Number(spec, "rotation_deg", "terminals") is double rotation &&
                (loc == null || ManifestQc.RotDiff(loc.Rotation * 180 / Math.PI, rotation, 0) > 0.5))
                diagnostics.Add("orientation");
            if (!equipment)
            {
                var shown = WriteKit.ParamValue(terminal.LookupParameter(parameter));
                row["direction"] = shown;
                if (shown != direction) diagnostics.Add("direction");
            }
            else
            {
                row["observed_type"] = type;
                row["expected_class"] = Arg.GetString(spec, "class");
                row["zone"] = Arg.GetString(spec, "zone");
            }
            if (Arg.GetString(spec, "ws") is string workset && WriteKit.WorksetName(doc, terminal) != workset)
                diagnostics.Add("workset");
            var ports = terminal.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End).ToList() ?? new List<Connector>();
            row["physical_ports"] = ports.Select(p => new Dictionary<string, object>
            {
                ["connector"] = (long)p.Id, ["xyz_ft"] = new[] { p.Origin.X, p.Origin.Y, p.Origin.Z },
                ["busy"] = p.IsConnected,
            }).ToList();
            if (Arg.GetLong(spec, "segment_id") is long segment)
            {
                var connected = ports.Any(p => ConduitRouteScope.PhysicalReferences(p).Any(c => Ids.ToLong(c.Owner.Id) == segment));
                var curve = (doc.GetElement(Ids.ToId(segment)) as Conduit)?.Location as LocationCurve;
                var arrives = curve != null && point != null && new[] { curve.Curve.GetEndPoint(0), curve.Curve.GetEndPoint(1) }
                    .Any(end => end.DistanceTo(point) <= tolerance || ports.Any(p => end.DistanceTo(p.Origin) <= tolerance));
                row["arrival"] = connected ? "physical_connection" : arrives ? "geometric_arrival" : "no_arrival";
                if (!connected && !arrives) diagnostics.Add("the segment does not reach the terminal");
                if (Arg.GetBool(spec, "require_connection") == true && !connected) diagnostics.Add("required physical connection is missing");
            }
            ok = diagnostics.Count == 0;
            return row;
        }
    }
}
