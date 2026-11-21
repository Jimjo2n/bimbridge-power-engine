// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Revit.Geo;
using Bimbridge.Addin.Revit.Writes;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    public sealed partial class RequestHandler
    {
        private async Task<Response> RouteAuditCheck(Request request, CancellationToken ct)
        {
            List<long> route, stubs;
            List<(long Id, long Link)> decks, obstacles;
            XYZ start, end;
            try
            {
                route = RouteIds(request.Args, "ids");
                stubs = RouteIds(request.Args, "stubs_externos");
                decks = RouteGeometryIds(request.Args, "losas");
                obstacles = RouteGeometryIds(request.Args, "obstaculos");
                if (route.Count == 0 || route.Count > 200 || decks.Count == 0 || decks.Count > 32 || obstacles.Count > 100 ||
                    stubs.Except(route).Any() || obstacles.Any(x => x.Link == 0 && route.Contains(x.Id)))
                    throw new QueryException("specify the segments and slabs in scope, without overlapping them with obstacles. Stubs must belong to the route");
                start = ReadRoutePoint(request.Args, "inicio");
                end = ReadRoutePoint(request.Args, "fin");
            }
            catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }

            var findings = new List<object>();
            var sample = new List<object>();
            var bodies = new Dictionary<(long Id, long Link), List<Solid>>();
            string error = null;
            long revision = 0;
            var cursor = 0;
            var fails = false;
            var expectedContacts = new HashSet<long>();
            var work = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out error);
                if (doc == null) return true;
                var rev = _tracker.For(doc)?.Rev ?? 0;
                if (cursor == 0)
                {
                    revision = rev;
                    try
                    {
                        var topology = ConduitRouteScope.Capture(doc, route, start, end);
                        foreach (var port in ((List<object>)topology["extremos"]).OfType<Dictionary<string, object>>())
                            expectedContacts.Add(Arg.GetLong(port, "id").Value);
                    }
                    catch (QueryException ex)
                    {
                        findings.Add(new Dictionary<string, object> { ["tipo"] = "continuidad_fisica", ["motivo"] = ex.Message });
                        fails = true;
                    }
                }
                else if (revision != rev)
                {
                    error = "the model changed during the route audit; repeat";
                    return true;
                }

                using var options = new Options { DetailLevel = ViewDetailLevel.Fine };
                List<Solid> Solids((long Id, long Link) key)
                {
                    if (bodies.TryGetValue(key, out var found)) return found;
                    try
                    {
                        var link = key.Link == 0 ? null : doc.GetElement(Ids.ToId(key.Link)) as RevitLinkInstance;
                        var owner = key.Link == 0 ? doc : link?.GetLinkDocument();
                        var element = owner?.GetElement(Ids.ToId(key.Id));
                        if (element == null) return bodies[key] = null;
                        var solids = new List<Solid>();
                        var exact = true;
                        Bodies.CollectSolids(element.get_Geometry(options), solids, owner, null, onFailure: () => exact = false);
                        if (!exact || solids.Count == 0) return bodies[key] = null;
                        if (link != null) solids = solids.Select(solid => SolidUtils.CreateTransformed(solid, link.GetTotalTransform())).ToList();
                        return bodies[key] = solids;
                    }
                    catch (Exception) { return bodies[key] = null; }
                }

                var id = route[cursor++];
                void Add(string kind, string detail, bool failure = true, long? other = null, long? link = null, double? volume = null)
                {
                    var item = new Dictionary<string, object> { ["tipo"] = kind, ["elemento"] = id, ["motivo"] = detail };
                    if (other.HasValue) { item["otro_id"] = other.Value; item["vinculo_id"] = link; }
                    if (volume.HasValue) item["volumen_ft3"] = volume.Value;
                    if (failure) { fails = true; findings.Add(item); }
                    else if (sample.Count < 50) sample.Add(item);
                }
                var target = Solids((id, 0));
                if (target == null) Add("no_evaluable", "the element has no intact solid geometry");
                else
                {
                    var deckSolids = decks.Select(Solids).ToList();
                    if (stubs.Contains(id)) Add("stub_externo", "exempt from containment by route scope", failure: false);
                    else if (deckSolids.Any(s => s == null)) Add("contencion_no_evaluable", "solids missing for one or more slabs");
                    else
                    {
                        try
                        {
                            var outside = target.Sum(body => OutsideVolume(body, deckSolids.SelectMany(s => s)));
                            if (outside > RouteVolumeTolerance) Add("fuera_de_losa", "body volume outside the given slabs", volume: outside);
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) { Add("contencion_no_evaluable", ex.Message); }
                    }
                    foreach (var obstacle in obstacles)
                    {
                        var other = Solids(obstacle);
                        if (other == null) { Add("choque_no_evaluable", "obstacle solids missing", other: obstacle.Id, link: obstacle.Link); continue; }
                        try
                        {
                            var interference = target.Sum(a => other.Sum(b => IntersectionVolume(a, b)));
                            if (interference <= RouteVolumeTolerance)
                                Add("candidato_descartado", "solid intersection without significant volume", false, obstacle.Id, obstacle.Link);
                            else
                                Add("choque_real", obstacle.Link == 0 && expectedContacts.Contains(obstacle.Id)
                                    ? "overlap with a connected terminal; check that the volume is limited to the intended joint" : "solid intersection",
                                    true, obstacle.Id, obstacle.Link, interference);
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) { Add("choque_no_evaluable", ex.Message, other: obstacle.Id, link: obstacle.Link); }
                    }
                }
                return cursor == route.Count;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!work.Ok) return FromWork(request.Id, work);
            if (error != null) return Response.Fail(request.Id, cursor > 0 ? Status.Conflict : Status.Invalid, error);
            return new Response
            {
                Id = request.Id, Rev = revision, State = fails ? Status.CompletedWithDifferences : Status.Ok,
                Data = new Dictionary<string, object>
                {
                    ["ruta"] = Arg.GetString(request.Args, "ruta"), ["nivel"] = Arg.GetString(request.Args, "nivel"),
                    ["elementos_revisados"] = (long)cursor, ["hallazgos"] = findings, ["muestra_sin_incidencia"] = sample,
                },
            };
        }

        private const double RouteVolumeTolerance = 1.0 / (304.8 * 304.8 * 304.8);

        private static double IntersectionVolume(Solid a, Solid b)
        {
            using var overlap = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
            return overlap.Volume;
        }

        private static double OutsideVolume(Solid body, IEnumerable<Solid> decks)
        {
            var remainder = body;
            try
            {
                foreach (var deck in decks)
                {
                    if (remainder.Faces.Size == 0 || remainder.Volume <= RouteVolumeTolerance) return 0;
                    var next = BooleanOperationsUtils.ExecuteBooleanOperation(remainder, deck, BooleanOperationsType.Difference);
                    if (!ReferenceEquals(body, remainder)) remainder.Dispose();
                    remainder = next;
                }
                return remainder.Faces.Size == 0 ? 0 : remainder.Volume;
            }
            finally { if (!ReferenceEquals(body, remainder)) remainder.Dispose(); }
        }

        private static XYZ ReadRoutePoint(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var value)) throw new QueryException("specify the route " + key);
            var p = OpCatalog.Point(value, "ruta " + key);
            if (p.Length != 3) throw new QueryException(key + " requires XYZ in internal feet");
            return new XYZ(p[0], p[1], p[2]);
        }

        private static List<long> RouteIds(Dictionary<string, object> args, string key) => Arg.GetStringList(args, key)
            .Select(value => long.TryParse(value, out var id) && id > 0 ? id : throw new QueryException("invalid ID in " + key))
            .Distinct().ToList();

        private static List<(long Id, long Link)> RouteGeometryIds(Dictionary<string, object> args, string key)
        {
            if (!args.TryGetValue(key, out var value)) return new List<(long, long)>();
            if (!(value is List<object> list)) throw new QueryException(key + " requires a list of {id,vinculo?}");
            return list.Select(x => x is Dictionary<string, object> item && Arg.GetLong(item, "id") is long id && id > 0 &&
                (!item.ContainsKey("vinculo") || (Arg.GetLong(item, "vinculo") is long link && link > 0))
                ? (id, Arg.GetLong(item, "vinculo") ?? 0L) : throw new QueryException("invalid geometric reference in " + key)).Distinct().ToList();
        }
    }
}
