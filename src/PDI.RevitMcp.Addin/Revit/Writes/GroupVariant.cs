// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Qa;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit.Writes
{
    internal static class GroupVariant
    {
        internal static Dictionary<string, object> Plan(BuildContext ctx, long id, Dictionary<string, object> args)
        {
            var group = CurrentGroup(ctx.Doc, id);
            var name = Arg.GetString(args, "nombre").Trim();
            if (!ctx.NewNames.Add("grupo:" + name) || NameExists(ctx.Doc, name))
                throw new QueryException("ya existe una variante con el nombre " + name);
            var rules = new Dictionary<string, object>((Dictionary<string, object>)args["reglas"], StringComparer.OrdinalIgnoreCase);
            var snapshot = Snapshot(group);
            var changes = new Dictionary<string, object>();
            foreach (var element in group.GetMemberIds().Select(ctx.Doc.GetElement))
            {
                if (!rules.TryGetValue(WriteKit.TypeName(ctx.Doc, element), out var wanted)) continue;
                var symbol = ctx.Model.FindType((string)wanted, "group_variant", t => t is FamilySymbol, "tipos de luminaria") as FamilySymbol;
                if (!(element is FamilyInstance fixture) ||
                    Ids.ToLong(fixture.Category?.Id ?? ElementId.InvalidElementId) != (long)BuiltInCategory.OST_LightingFixtures ||
                    fixture.Category.Id != symbol.Category?.Id || fixture.Symbol.Family.FamilyPlacementType != symbol.Family.FamilyPlacementType)
                    throw new QueryException("cambió la categoría o el tipo de montaje para " + Ids.ToLong(element.Id));
                if (symbol.Id != fixture.GetTypeId())
                    changes[Ids.ToLong(element.Id).ToString(CultureInfo.InvariantCulture)] = Ids.ToLong(symbol.Id);
            }
            if (changes.Count == 0) throw new QueryException("el grupo no tiene luminarias que coincidan con las reglas de sustitución");
            return new Dictionary<string, object>
            {
                ["grupo"] = id, ["nombre"] = name, ["cambios"] = changes, ["miembros"] = snapshot,
                ["huella"] = Fingerprint(group, snapshot), ["ws"] = (long)group.WorksetId.IntegerValue,
            };
        }

        internal static Group Validate(Document doc, Dictionary<string, object> target)
        {
            var group = CurrentGroup(doc, WriteKit.L(target, "grupo"));
            if (Fingerprint(group, Snapshot(group)) != WriteKit.S(target, "huella"))
                throw new QueryException("cambió el grupo de origen después de planificar");
            if (NameExists(doc, WriteKit.S(target, "nombre"))) throw new QueryException("el nombre de la variante ya está ocupado");
            return group;
        }

        internal static Group Rebuild(Document doc, Dictionary<string, object> target)
        {
            var original = Validate(doc, target);
            var sourceWorkset = original.WorksetId.IntegerValue;
            var members = ((Dictionary<string, object>)target["miembros"]);
            var changed = (Dictionary<string, object>)target["cambios"];
            var ids = original.UngroupMembers().ToList();
            var byId = ids.ToDictionary(id => Ids.ToLong(id).ToString(CultureInfo.InvariantCulture));
            if (!new HashSet<string>(byId.Keys).SetEquals(members.Keys))
                throw new InvalidOperationException("Revit cambió los miembros al desagrupar");
            foreach (var item in changed)
            {
                var element = doc.GetElement(byId[item.Key]);
                var type = Ids.ToId(Convert.ToInt64(item.Value, CultureInfo.InvariantCulture));
                if (!element.IsValidType(type)) throw new InvalidOperationException("Revit no acepta el tipo nuevo para " + item.Key);
                var replacement = element.ChangeTypeId(type);
                if (replacement != ElementId.InvalidElementId)
                    throw new InvalidOperationException("el cambio recrearía " + item.Key + "; revise sus referencias antes de sustituirlo");
            }
            var variant = doc.Create.NewGroup(ids) ?? throw new InvalidOperationException("Revit no reconstruyó el grupo");
            variant.GroupType.Name = WriteKit.S(target, "nombre");
            if (doc.IsWorkshared && variant.WorksetId.IntegerValue != sourceWorkset)
            {
                var parameter = variant.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly || !parameter.Set(sourceWorkset))
                    throw new InvalidOperationException("Revit no conservó el workset del grupo");
            }
            doc.Regenerate();
            if (!Matches(variant, target)) throw new InvalidOperationException("el grupo nuevo no conservó los miembros, la geometría o el montaje");
            return variant;
        }

        internal static bool Matches(Group group, Dictionary<string, object> target)
        {
            if (group == null || group.GroupType.Name != WriteKit.S(target, "nombre") ||
                (group.Document.IsWorkshared && group.WorksetId.IntegerValue != WriteKit.L(target, "ws"))) return false;
            var current = Snapshot(group);
            var old = (Dictionary<string, object>)target["miembros"];
            var changed = (Dictionary<string, object>)target["cambios"];
            return GroupMemberMap.MatchesVariant(old, current, changed);
        }

        private static Group CurrentGroup(Document doc, long id)
        {
            var group = doc.GetElement(Ids.ToId(id)) as Group;
            if (group == null || group.Pinned || group.GroupId != ElementId.InvalidElementId || group.ViewSpecific)
                throw new QueryException("el grupo debe ser de modelo, no anidado y estar desbloqueado");
            var members = new HashSet<ElementId>(group.GetMemberIds());
            if (group.GetDependentElements(null).Any(dependent => dependent != group.Id && !members.Contains(dependent)))
                throw new QueryException("el grupo tiene dependientes fuera de sus miembros; deben resolverse antes de reconstruirlo");
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
                        ["uid"] = element.UniqueId, ["tipo"] = Ids.ToLong(element.GetTypeId()), ["workset"] = (long)element.WorksetId.IntegerValue,
                        ["pos"] = WriteKit.Loc(element), ["rot"] = WriteKit.Rot(element), ["nivel"] = Ids.ToLong(element.LevelId),
                    };
                    if (element is FamilyInstance family)
                    {
                        state["anfitrion"] = Ids.ToLong(family.Host?.Id);
                        state["mano"] = family.HandFlipped;
                        state["frente"] = family.FacingFlipped;
                        state["espejo"] = family.Mirrored;
                    }
                    if (element.Location is LocationCurve curve)
                        state["extremos"] = new object[] { WriteKit.Xyz(curve.Curve.GetEndPoint(0)), WriteKit.Xyz(curve.Curve.GetEndPoint(1)) };
                    return (object)state;
                });
        }

        private static string Fingerprint(Group group, Dictionary<string, object> members) =>
            Home.Sha256Hex(OpSemantics.Canonical(new object[] { group.UniqueId, Ids.ToLong(group.GetTypeId()),
                (long)group.WorksetId.IntegerValue, WriteKit.Loc(group), WriteKit.Rot(group), members }));
    }
}
