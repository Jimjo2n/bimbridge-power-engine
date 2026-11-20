// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    internal static class ConduitRouteScope
    {
        private const double Tolerance = 1.0 / 304.8;

        internal static Dictionary<string, object> Capture(Document doc, List<long> ids, XYZ start, XYZ end)
        {
            var scope = new HashSet<long>(ids);
            var adjacency = ids.ToDictionary(x => x, _ => new List<long>());
            var boundaries = new List<(XYZ Point, Dictionary<string, object> External)>();
            var snapshot = new List<object>();
            foreach (var id in ids.OrderBy(x => x))
            {
                var element = doc.GetElement(Ids.ToId(id));
                if (!(element is Conduit) && !(element is FamilyInstance fitting &&
                    Ids.ToLong(fitting.Category?.Id ?? ElementId.InvalidElementId) == (long)BuiltInCategory.OST_ConduitFitting))
                    throw new QueryException("el ID " + id + " no es tramo ni accesorio de conduit");
                var ports = Manager(element)?.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End)
                    .OrderBy(c => c.Id).ToList();
                if (ports == null || ports.Count != 2) throw new QueryException("el ID " + id + " debe tener exactamente dos extremos físicos");
                var portDetails = new List<object>();
                foreach (var port in ports)
                {
                    var refs = PhysicalReferences(port);
                    if (refs.Count > 1 || (port.IsConnected && refs.Count != 1))
                        throw new QueryException("no se pudo identificar una conexión física única en " + id);
                    var reference = refs.SingleOrDefault();
                    if (reference != null && scope.Contains(Ids.ToLong(reference.Owner.Id)))
                        adjacency[id].Add(Ids.ToLong(reference.Owner.Id));
                    else
                        boundaries.Add((port.Origin, reference == null ? null : FingerprintPort(reference)));
                    portDetails.Add(new object[] { FingerprintPort(port), reference == null ? null : FingerprintPort(reference) });
                }
                snapshot.Add(new object[]
                {
                    element.UniqueId, Ids.ToLong(element.GetTypeId()), element.WorksetId.IntegerValue,
                    (element as Conduit)?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble(), portDetails,
                });
            }
            var seen = new HashSet<long>();
            var pending = new Stack<long>();
            pending.Push(ids[0]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current)) continue;
                foreach (var next in adjacency[current]) pending.Push(next);
            }
            if (seen.Count != ids.Count || boundaries.Count != 2)
                throw new QueryException("los IDs deben formar un recorrido conectado sin ramales y con exactamente dos extremos");
            var first = boundaries.FindIndex(b => Close(b.Point, start));
            if (first < 0 || !Close(boundaries[1 - first].Point, end) || Close(start, end))
                throw new QueryException("los extremos del trazado deben coincidir con los dos extremos originales dentro de 1 mm");
            return new Dictionary<string, object>
            {
                ["huella"] = Home.Sha256Hex(OpSemantics.Canonical(snapshot)),
                ["extremos"] = new List<object> { boundaries[first].External, boundaries[1 - first].External },
            };
        }

        internal static Dictionary<string, object> Terminal(Document doc, Dictionary<string, object> input, XYZ target, bool mustBeFree)
        {
            var owner = doc.GetElement(Ids.ToId(Arg.GetLong(input, "id").Value));
            var port = Manager(owner)?.Lookup((int)Arg.GetLong(input, "conector").Value);
            if (port == null || port.ConnectorType != ConnectorType.End || !Close(port.Origin, target))
                throw new QueryException("el conector especificado no es un extremo en la posición esperada");
            if (mustBeFree && port.IsConnected) throw new QueryException("el conector terminal ya está ocupado");
            return FingerprintPort(port);
        }

        internal static Connector Resolve(Document doc, Dictionary<string, object> saved, XYZ target, bool mustBeFree)
        {
            var current = Terminal(doc, saved, target, mustBeFree);
            if (OpSemantics.Canonical(current) != OpSemantics.Canonical(saved))
                throw new QueryException("cambió el conector terminal");
            var owner = doc.GetElement(Ids.ToId(Arg.GetLong(saved, "id").Value));
            return Manager(owner).Lookup((int)Arg.GetLong(saved, "conector").Value);
        }

        internal static List<Connector> PhysicalReferences(Connector port) => port.AllRefs.Cast<Connector>()
            .Where(c => c.Owner.Id != port.Owner.Id && c.ConnectorType == ConnectorType.End && port.IsConnectedTo(c)).ToList();

        private static ConnectorManager Manager(Element e) => e is MEPCurve curve ? curve.ConnectorManager :
            (e as FamilyInstance)?.MEPModel?.ConnectorManager;

        private static Dictionary<string, object> FingerprintPort(Connector c)
        {
            var point = c.Origin;
            var direction = c.CoordinateSystem.BasisZ;
            return new Dictionary<string, object>
            {
                ["id"] = Ids.ToLong(c.Owner.Id), ["uid"] = c.Owner.UniqueId, ["conector"] = (long)c.Id,
                ["pos"] = WriteKit.List(point.X, point.Y, point.Z), ["dir"] = WriteKit.List(direction.X, direction.Y, direction.Z),
                ["dominio"] = c.Domain.ToString(), ["forma"] = c.Shape.ToString(), ["radio"] = c.Shape == ConnectorProfileType.Round ? (object)c.Radius : null,
            };
        }

        private static bool Close(XYZ a, XYZ b) => a.DistanceTo(b) <= Tolerance;
    }
}
