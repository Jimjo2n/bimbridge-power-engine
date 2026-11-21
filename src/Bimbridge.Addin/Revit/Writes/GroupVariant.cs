// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    internal static class GroupVariant
    {
        internal static Dictionary<string, object> Plan(BuildContext ctx, long id, Dictionary<string, object> args)
        {
            var group = CurrentGroup(ctx.Doc, id);
            var name = Arg.GetString(args, "name").Trim();
            if (!ctx.NewNames.Add("group:" + name) || NameExists(ctx.Doc, name))
                throw new QueryException("a variant already exists with the name " + name);
            var rules = new Dictionary<string, object>((Dictionary<string, object>)args["rules"], StringComparer.OrdinalIgnoreCase);
            var snapshot = Snapshot(group);
            var changes = new Dictionary<string, object>();
            foreach (var element in group.GetMemberIds().Select(ctx.Doc.GetElement))
            {
                if (!rules.TryGetValue(WriteKit.TypeName(ctx.Doc, element), out var wanted)) continue;
                var symbol = ctx.Model.FindType((string)wanted, "group_variant", t => t is FamilySymbol, "lighting fixture types") as FamilySymbol;
                if (!(element is FamilyInstance fixture) ||
                    Ids.ToLong(fixture.Category?.Id ?? ElementId.InvalidElementId) != (long)BuiltInCategory.OST_LightingFixtures ||
                    fixture.Category.Id != symbol.Category?.Id || fixture.Symbol.Family.FamilyPlacementType != symbol.Family.FamilyPlacementType)
                    throw new QueryException("the category or mounting type changed for " + Ids.ToLong(element.Id));
                if (symbol.Id != fixture.GetTypeId())
                    changes[Ids.ToLong(element.Id).ToString(CultureInfo.InvariantCulture)] = Ids.ToLong(symbol.Id);
            }
            if (changes.Count == 0) throw new QueryException("the group has no lighting fixtures matching the replacement rules");
            return new Dictionary<string, object>
            {
                ["group"] = id, ["name"] = name, ["changes"] = changes, ["members"] = snapshot,
                ["fingerprint"] = Fingerprint(group, snapshot), ["ws"] = (long)group.WorksetId.IntegerValue,
            };
        }

        internal static Group Validate(Document doc, Dictionary<string, object> target)
        {
            var group = CurrentGroup(doc, WriteKit.L(target, "group"));
            if (Fingerprint(group, Snapshot(group)) != WriteKit.S(target, "fingerprint"))
                throw new QueryException("the source group changed after planning");
            if (NameExists(doc, WriteKit.S(target, "name"))) throw new QueryException("the variant name is already taken");
            return group;
        }

        internal static Group Rebuild(Document doc, Dictionary<string, object> target)
        {
            var original = Validate(doc, target);
            var sourceWorkset = original.WorksetId.IntegerValue;
            var members = ((Dictionary<string, object>)target["members"]);
            var changed = (Dictionary<string, object>)target["changes"];
            var ids = original.UngroupMembers().ToList();
            var byId = ids.ToDictionary(id => Ids.ToLong(id).ToString(CultureInfo.InvariantCulture));
            if (!new HashSet<string>(byId.Keys).SetEquals(members.Keys))
                throw new InvalidOperationException("Revit changed the members when ungrouping");
            foreach (var item in changed)
            {
                var element = doc.GetElement(byId[item.Key]);
                var type = Ids.ToId(Convert.ToInt64(item.Value, CultureInfo.InvariantCulture));
                if (!element.IsValidType(type)) throw new InvalidOperationException("Revit does not accept the new type for " + item.Key);
                var replacement = element.ChangeTypeId(type);
                if (replacement != ElementId.InvalidElementId)
                    throw new InvalidOperationException("the change would recreate " + item.Key + "; review its references before replacing it");
            }
            var variant = doc.Create.NewGroup(ids) ?? throw new InvalidOperationException("Revit did not rebuild the group");
            variant.GroupType.Name = WriteKit.S(target, "name");
            if (doc.IsWorkshared && variant.WorksetId.IntegerValue != sourceWorkset)
            {
                var parameter = variant.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly || !parameter.Set(sourceWorkset))
                    throw new InvalidOperationException("Revit did not keep the group's workset");
            }
            doc.Regenerate();
            if (!Matches(variant, target)) throw new InvalidOperationException("the new group did not keep the members, geometry or mounting");
            return variant;
        }

        internal static bool Matches(Group group, Dictionary<string, object> target)
        {
            if (group == null || group.GroupType.Name != WriteKit.S(target, "name") ||
                (group.Document.IsWorkshared && group.WorksetId.IntegerValue != WriteKit.L(target, "ws"))) return false;
            var current = Snapshot(group);
            var old = (Dictionary<string, object>)target["members"];
            var changed = (Dictionary<string, object>)target["changes"];
            return GroupMemberMap.MatchesVariant(old, current, changed);
        }

        private static Group CurrentGroup(Document doc, long id)
        {
            var group = doc.GetElement(Ids.ToId(id)) as Group;
            if (group == null || group.Pinned || group.GroupId != ElementId.InvalidElementId || group.ViewSpecific)
                throw new QueryException("the group must be a model group, not nested, and unpinned");
            var members = new HashSet<ElementId>(group.GetMemberIds());
            if (group.GetDependentElements(null).Any(dependent => dependent != group.Id && !members.Contains(dependent)))
                throw new QueryException("the group has dependents outside its members; they must be resolved before rebuilding it");
            return group;
        }

        private static bool NameExists(Document doc, string name)
        {
            using var collector = new FilteredElementCollector(doc);
            return collector.OfClass(typeof(GroupType)).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> Snapshot(Group group)
        {
            return group.GetMemberIds().Select(group.Document.GetElement).ToDictionary(
                element => Ids.ToLong(element.Id).ToString(CultureInfo.InvariantCulture), element =>
                {
                    var state = new Dictionary<string, object>
                    {
                        ["uid"] = element.UniqueId, ["type"] = Ids.ToLong(element.GetTypeId()), ["workset"] = (long)element.WorksetId.IntegerValue,
                        ["pos"] = WriteKit.Loc(element), ["rot"] = WriteKit.Rot(element), ["level"] = Ids.ToLong(element.LevelId),
                    };
                    if (element is FamilyInstance family)
                    {
                        state["host"] = Ids.ToLong(family.Host?.Id);
                        state["hand"] = family.HandFlipped;
                        state["facing"] = family.FacingFlipped;
                        state["mirrored"] = family.Mirrored;
                    }
                    if (element.Location is LocationCurve curve)
                        state["ends"] = new object[] { WriteKit.Xyz(curve.Curve.GetEndPoint(0)), WriteKit.Xyz(curve.Curve.GetEndPoint(1)) };
                    return (object)state;
                });
        }

        private static string Fingerprint(Group group, Dictionary<string, object> members) =>
            Home.Sha256Hex(OpSemantics.Canonical(new object[] { group.UniqueId, Ids.ToLong(group.GetTypeId()),
                (long)group.WorksetId.IntegerValue, WriteKit.Loc(group), WriteKit.Rot(group), members }));
    }
}
