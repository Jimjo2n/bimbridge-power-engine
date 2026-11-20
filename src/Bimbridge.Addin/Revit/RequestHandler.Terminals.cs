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
            var scope = equipment ? "equipos" : "terminales";
            if (!request.Args.TryGetValue(scope, out var raw) || !(raw is List<object> input) ||
                input.Count == 0 || input.Count > 200 || input.Any(x => x is not Dictionary<string, object>))
                return Response.Fail(request.Id, Status.Invalido, scope + " requires 1 to 200 objects with the markup expectations");
            var specs = input.Cast<Dictionary<string, object>>().ToList();
            var unexpected = new List<long>();
            if (equipment && request.Args.ContainsKey("ids_observados"))
            {
                try { unexpected = RouteIds(request.Args, "ids_observados").Except(specs.Select(x => Arg.GetLong(x, "id") ?? 0)).ToList(); }
                catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }
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
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);
            return new Response
            {
                Id = request.Id, Rev = revision, Estado = differs ? Status.ConDiferencias : Status.Ok,
                Datos = new Dictionary<string, object> { [scope] = rows, ["ids_no_previstos"] = unexpected },
            };
        }

        private static Dictionary<string, object> InspectTerminal(Document doc, Dictionary<string, object> spec, bool equipment, out bool ok)
        {
            const double tolerance = 1.0 / 304.8;
            if (!(Arg.GetLong(spec, "id") is long id) || id <= 0 || !spec.TryGetValue("xy_ft", out var xy))
                throw new QueryException("terminales: each inspection requires the markup id and xy_ft");
            var expected = OpCatalog.Point(xy, "terminales xy_ft", false);
            var offset = OpCatalog.Number(spec, "elev_ft", "terminales") ?? throw new QueryException("missing elev_ft");
            var expectedType = Arg.GetString(spec, "tipo");
            var expectedLevel = Arg.GetString(spec, "nivel");
            var direction = Arg.GetString(spec, "sentido");
            var parameter = Arg.GetString(spec, "parametro_sentido");
            if (string.IsNullOrWhiteSpace(expectedType) || string.IsNullOrWhiteSpace(expectedLevel))
                throw new QueryException("missing the expected tipo or nivel");
            if (!equipment && ((direction != "UP" && direction != "DN") || string.IsNullOrWhiteSpace(parameter)))
                throw new QueryException("missing sentido UP/DN or parametro_sentido");
            var diagnostics = new List<object>();
            var row = new Dictionary<string, object> { ["id"] = id, ["ref"] = Arg.GetString(spec, "ref"), ["diferencias"] = diagnostics };
            if (!(doc.GetElement(Ids.ToId(id)) is FamilyInstance terminal))
            {
                diagnostics.Add("terminal not found");
                ok = false;
                return row;
            }
            var level = WriteKit.LevelOf(doc, terminal);
            if (!string.Equals(level?.Name, expectedLevel, StringComparison.OrdinalIgnoreCase)) diagnostics.Add("nivel");
            var type = WriteKit.TypeName(doc, terminal);
            if (!string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(terminal.Symbol.Name, expectedType, StringComparison.OrdinalIgnoreCase)) diagnostics.Add("tipo");
            var loc = terminal.Location as LocationPoint;
            var point = loc?.Point;
            if (point == null || level == null || point.DistanceTo(new XYZ(expected[0], expected[1], level.ProjectElevation + offset)) > tolerance)
                diagnostics.Add("position");
            if (OpCatalog.Number(spec, "giro_deg", "terminales") is double rotation &&
                (loc == null || ManifestQc.RotDiff(loc.Rotation * 180 / Math.PI, rotation, 0) > 0.5))
                diagnostics.Add("orientation");
            if (!equipment)
            {
                var shown = WriteKit.ParamValue(terminal.LookupParameter(parameter));
                row["sentido"] = shown;
                if (shown != direction) diagnostics.Add("sentido");
            }
            else
            {
                row["tipo_observado"] = type;
                row["clase_esperada"] = Arg.GetString(spec, "clase");
                row["zona"] = Arg.GetString(spec, "zona");
            }
            if (Arg.GetString(spec, "ws") is string workset && WriteKit.WorksetName(doc, terminal) != workset)
                diagnostics.Add("workset");
            var ports = terminal.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End).ToList() ?? new List<Connector>();
            row["puertos_fisicos"] = ports.Select(p => new Dictionary<string, object>
            {
                ["conector"] = (long)p.Id, ["xyz_ft"] = new[] { p.Origin.X, p.Origin.Y, p.Origin.Z },
                ["ocupado"] = p.IsConnected,
            }).ToList();
            if (Arg.GetLong(spec, "tramo_id") is long segment)
            {
                var connected = ports.Any(p => ConduitRouteScope.PhysicalReferences(p).Any(c => Ids.ToLong(c.Owner.Id) == segment));
                var curve = (doc.GetElement(Ids.ToId(segment)) as Conduit)?.Location as LocationCurve;
                var arrives = curve != null && point != null && new[] { curve.Curve.GetEndPoint(0), curve.Curve.GetEndPoint(1) }
                    .Any(end => end.DistanceTo(point) <= tolerance || ports.Any(p => end.DistanceTo(p.Origin) <= tolerance));
                row["llegada"] = connected ? "conexion_fisica" : arrives ? "llegada_geometrica" : "sin_llegada";
                if (!connected && !arrives) diagnostics.Add("the segment does not reach the terminal");
                if (Arg.GetBool(spec, "exigir_conexion") == true && !connected) diagnostics.Add("required physical connection is missing");
            }
            ok = diagnostics.Count == 0;
            return row;
        }
    }
}
