// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Addin.Scripting;
using Bimbridge.Engine;
using Bimbridge.Engine.Qc;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    internal static class GroupPlacement
    {
        private const double Tolerance = 1.0 / 304.8;

        internal static XYZ Point(Dictionary<string, object> target) =>
            new XYZ(WriteKit.D(target, "x"), WriteKit.D(target, "y"), WriteKit.D(target, "z"));

        internal static List<Group> AtPoint(Document doc, ElementId type, XYZ point)
        {
            using var collector = new FilteredElementCollector(doc);
            return collector.OfClass(typeof(Group)).Cast<Group>().Where(g => g.GetTypeId() == type &&
                g.Location is LocationPoint location && location.Point.DistanceTo(point) <= Tolerance).ToList();
        }

        internal static string Signature(Group group)
        {
            var members = group.GetMemberIds().Select(group.Document.GetElement).ToArray();
            if (members.Any(e => e == null)) throw new QueryException("members are missing while checking the group");
            return Home.Sha256Hex(OpSemantics.Canonical(members.Select(e => Bim.Grupos.Firma(group, e))
                .OrderBy(x => x, StringComparer.Ordinal).ToList()));
        }

        internal static string Difference(Group group, Dictionary<string, object> target)
        {
            if (group == null || group.Location is not LocationPoint location || location.Point.DistanceTo(Point(target)) > Tolerance)
                return "origin";
            if (target.ContainsKey("gtype") && Ids.ToLong(group.GetTypeId()) != WriteKit.L(target, "gtype")) return "type";
            if (target.ContainsKey("nivel") && Ids.ToLong(group.get_Parameter(BuiltInParameter.GROUP_LEVEL)?.AsElementId()) != WriteKit.L(target, "nivel"))
                return "level";
            if (target.ContainsKey("rot") && ManifestQc.RotDiff(location.Rotation * 180 / Math.PI, WriteKit.D(target, "rot"), 0) > 0.1)
                return "rotation";
            if (target.ContainsKey("ws") && group.WorksetId.IntegerValue != WriteKit.L(target, "ws")) return "workset";
            if (target.ContainsKey("firma_miembros") && WriteKit.S(target, "firma_miembros") != Signature(group)) return "members relative to the prototype";
            return null;
        }
    }
}
