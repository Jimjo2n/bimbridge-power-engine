// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    internal static class AnnotationRepair
    {
        internal static Dictionary<string, object> Inspect(Element element)
        {
            var refs = References(element);
            object presentation;
            if (element is IndependentTag tag)
            {
                var reference = refs.Count == 1 ? refs[0] : null;
                var elbow = tag.HasLeader && reference != null && tag.HasLeaderElbow(reference) ? tag.GetLeaderElbow(reference) : null;
                var end = tag.HasLeader && reference != null && tag.LeaderEndCondition == LeaderEndCondition.Free ? tag.GetLeaderEnd(reference) : null;
                presentation = new object[] { Point(tag.TagHeadPosition), tag.TagOrientation.ToString(), tag.RotationAngle,
                    tag.HasLeader, tag.HasLeader ? tag.LeaderEndCondition.ToString() : null, Point(elbow), Point(end) };
            }
            else
            {
                var dim = (Dimension)element;
                if (!(dim.Curve is Line axis)) throw new QueryException("dimension repair supports linear dimensions");
                var segments = dim.Segments.Cast<DimensionSegment>().ToList();
                var formatting = segments.Count == 0
                    ? new List<object> { Format(dim.Prefix, dim.Suffix, dim.Above, dim.Below, dim.ValueOverride,
                        dim.IsTextPositionAdjustable() ? dim.TextPosition : null) }
                    : segments.Select(s => Format(s.Prefix, s.Suffix, s.Above, s.Below, s.ValueOverride,
                        s.IsTextPositionAdjustable() ? s.TextPosition : null)).ToList();
                presentation = new object[] { Point(axis.Origin), Point(axis.Direction), formatting, dim.IsLocked,
                    segments.Select(s => s.IsLocked).ToList() };
            }
            return new Dictionary<string, object>
            {
                ["id"] = Ids.ToLong(element.Id), ["uid"] = element.UniqueId, ["view_id"] = Ids.ToLong(element.OwnerViewId),
                ["type_id"] = Ids.ToLong(element.GetTypeId()),
                ["references"] = refs.Select(r => new Dictionary<string, object>
                {
                    ["id"] = Ids.ToLong(r.ElementId), ["linked_id"] = Ids.ToLong(r.LinkedElementId),
                    ["stable"] = r.ConvertToStableRepresentation(element.Document),
                }).ToList(),
                ["presentation"] = presentation,
            };
        }

        internal static string Fingerprint(Element element) => Home.Sha256Hex(OpSemantics.Canonical(Inspect(element)));

        internal static List<Reference> ResolveTargets(Document doc, Element original, Dictionary<string, object> data)
        {
            var existing = References(original);
            if (original is IndependentTag && data.ContainsKey("target"))
            {
                if (existing.Count != 1 || existing[0].LinkedElementId != ElementId.InvalidElementId)
                    throw new QueryException("only a tag with a single local reference can be repaired");
                var target = doc.GetElement(Ids.ToId(WriteKit.L(data, "target")));
                if (target == null || target.Id == existing[0].ElementId)
                    throw new QueryException("the target element must exist and differ from the tagged one");
                return new List<Reference> { new Reference(target) };
            }
            if (!(original is Dimension dim) || !data.ContainsKey("stable_refs"))
                throw new QueryException("a tag requires target and a dimension requires stable_refs");
            if (dim.IsLocked || dim.Segments.Cast<DimensionSegment>().Any(s => s.IsLocked))
                throw new QueryException("release the dimension's constraints before repairing its references");
            var supplied = Arg.GetStringList(data, "stable_refs");
            if (supplied.Count != existing.Count)
                throw new QueryException("the dimension requires one target reference per previous reference, in the same order");
            var references = new List<Reference>();
            foreach (var value in supplied)
            {
                try
                {
                    var reference = Reference.ParseFromStableRepresentation(doc, value);
                    if (reference == null || doc.GetElement(reference.ElementId) == null)
                        throw new QueryException("the target of a dimension reference does not exist");
                    references.Add(reference);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    throw new QueryException("a stable dimension reference did not resolve in the document");
                }
            }
            return references;
        }

        internal static Element Recreate(Element original, View view, List<Reference> references)
        {
            var doc = original.Document;
            if (original is IndependentTag tag)
            {
                var old = References(original).Single();
                var reference = references.Single();
                var replacement = IndependentTag.Create(doc, tag.GetTypeId(), view.Id, reference, tag.HasLeader, tag.TagOrientation, tag.TagHeadPosition)
                    ?? throw new InvalidOperationException("Revit did not create the new tag");
                replacement.TagHeadPosition = tag.TagHeadPosition;
                if (Math.Abs(replacement.RotationAngle - tag.RotationAngle) > 1e-7) replacement.RotationAngle = tag.RotationAngle;
                if (tag.HasLeader)
                {
                    if (replacement.LeaderEndCondition != tag.LeaderEndCondition)
                        replacement.LeaderEndCondition = tag.LeaderEndCondition;
                    if (tag.LeaderEndCondition == LeaderEndCondition.Free)
                        replacement.SetLeaderEnd(reference, tag.GetLeaderEnd(old));
                    if (tag.HasLeaderElbow(old)) replacement.SetLeaderElbow(reference, tag.GetLeaderElbow(old));
                }
                return replacement;
            }
            var originalDimension = (Dimension)original;
            var axis = (Line)originalDimension.Curve;
            var array = new ReferenceArray();
            foreach (var reference in references) array.Append(reference);
            var linear = axis.IsBound ? axis : Line.CreateBound(axis.Origin - axis.Direction, axis.Origin + axis.Direction);
            var result = doc.Create.NewDimension(view, linear, array, (DimensionType)doc.GetElement(originalDimension.GetTypeId()))
                ?? throw new InvalidOperationException("Revit did not create a dimension with the requested references");
            var originalSegments = originalDimension.Segments.Cast<DimensionSegment>().ToList();
            var resultSegments = result.Segments.Cast<DimensionSegment>().ToList();
            if (originalSegments.Count != resultSegments.Count) throw new InvalidOperationException("the number of segments changed");
            if (originalSegments.Count == 0)
            {
                result.Prefix = originalDimension.Prefix; result.Suffix = originalDimension.Suffix;
                result.Above = originalDimension.Above; result.Below = originalDimension.Below; result.ValueOverride = originalDimension.ValueOverride;
                if (originalDimension.IsTextPositionAdjustable() && result.IsTextPositionAdjustable()) result.TextPosition = originalDimension.TextPosition;
            }
            for (var i = 0; i < originalSegments.Count; i++)
            {
                var from = originalSegments[i]; var to = resultSegments[i];
                to.Prefix = from.Prefix; to.Suffix = from.Suffix;
                to.Above = from.Above; to.Below = from.Below; to.ValueOverride = from.ValueOverride;
                if (from.IsTextPositionAdjustable() && to.IsTextPositionAdjustable()) to.TextPosition = from.TextPosition;
            }
            return result;
        }

        internal static List<Reference> References(Element element)
        {
            if (element is IndependentTag tag && !tag.IsOrphaned) return tag.GetTaggedReferences().ToList();
            if (element is Dimension dim && dim.AreReferencesAvailable && dim.References != null) return dim.References.Cast<Reference>().ToList();
            throw new QueryException("the tag or dimension must exist and have readable references in its view");
        }

        private static object Format(string prefix, string suffix, string above, string below, string value, XYZ text) =>
            new object[] { prefix, suffix, above, below, value, Point(text) };

        private static object Point(XYZ point) => point == null ? null : WriteKit.List(point.X, point.Y, point.Z);
    }
}
