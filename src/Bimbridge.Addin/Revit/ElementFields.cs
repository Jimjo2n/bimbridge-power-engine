// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Reads only the requested fields of an element (pyRevit problem D10: it read all parameters).
    /// Lengths in decimal feet (Revit internal unit), rounded to 1/10000 ft.
    /// Non-length parameters are returned with their display text.
    /// </summary>
    public static class ElementFields
    {
        /// <summary>
        /// Type, family and level names already resolved in THIS query (by id). Lives only as long as a query, on the Revit thread:
        /// never survives a model change nor carries over to another document. Thousands of fixtures share a few dozen types.
        /// </summary>
        public sealed class NameCache
        {
            internal readonly Dictionary<long, string> Type = new Dictionary<long, string>(), Family = new Dictionary<long, string>(), Level = new Dictionary<long, string>();
            /// <summary>0.9.1: type element by id (looking up a type parameter no longer does doc.GetElement per instance).</summary>
            internal readonly Dictionary<long, Element> TypeElem = new Dictionary<long, Element>();
            /// <summary>0.9.1: name of the element an ElementId parameter points to (materials, levels, phases…), by id.</summary>
            internal readonly Dictionary<long, object> Target = new Dictionary<long, object>();
            /// <summary>0.9.1: data class (length / yes-no) per parameter definition: GetDataType() once per parameter, not per value.</summary>
            internal readonly Dictionary<Definition, (bool Length, bool YesNo)> Spec = new Dictionary<Definition, (bool, bool)>();
            /// <summary>Cache hits and misses (observed): they go into the query metrics.</summary>
            public long Hits, Misses;
        }

        public static Dictionary<string, object> Read(Document doc, Element e, List<string> fields, NameCache cache = null)
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {
                try { row[f] = ReadOne(doc, e, f, cache); }
                catch (Exception) { row[f] = null; }
            }
            return row;
        }

        private static string Cached(Dictionary<long, string> map, ElementId id, Func<string> resolve, NameCache cache = null)
        {
            if (id == null || id == ElementId.InvalidElementId) return resolve();
            var k = Ids.ToLong(id);
            if (map.TryGetValue(k, out var v)) { if (cache != null) cache.Hits++; return v; }
            if (cache != null) cache.Misses++;
            v = resolve();
            map[k] = v;
            return v;
        }

        /// <summary>Type element of <paramref name="e"/>, cached by type id if a cache is given.</summary>
        public static Element TypeOf(Document doc, Element e, NameCache cache)
        {
            var tid = e.GetTypeId();
            if (tid == null || tid == ElementId.InvalidElementId) return null;
            if (cache == null) return doc.GetElement(tid);
            var k = Ids.ToLong(tid);
            if (cache.TypeElem.TryGetValue(k, out var t)) { cache.Hits++; return t; }
            cache.Misses++;
            t = doc.GetElement(tid);
            cache.TypeElem[k] = t;
            return t;
        }

        private static object ReadOne(Document doc, Element e, string field, NameCache cache)
        {
            var fi = e as FamilyInstance;
            switch (field.ToLowerInvariant())
            {
                case "id": return Ids.ToLong(e.Id);
                case "uid": return e.UniqueId;
                case "cat": return e.Category?.Name;
                case "name": return e.Name;
                case "fam":
                {
                    string R() => fi != null ? fi.Symbol?.FamilyName : (doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName;
                    return cache == null ? R() : Cached(cache.Family, e.GetTypeId(), R, cache);
                }
                case "type":
                {
                    string R() => fi != null ? fi.Symbol?.Name : doc.GetElement(e.GetTypeId())?.Name;
                    return cache == null ? R() : Cached(cache.Type, e.GetTypeId(), R, cache);
                }
                case "lvl":
                {
                    if (cache == null) return LevelName(doc, e);
                    var lid = e.LevelId;
                    // Only the direct path (LevelId) is cached; the alternative parameters are resolved as usual.
                    return lid != null && lid != ElementId.InvalidElementId ? Cached(cache.Level, lid, () => doc.GetElement(lid)?.Name, cache) : LevelName(doc, e);
                }
                case "ws":
                    if (!doc.IsWorkshared) return null;
                    return doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name;
                case "elev": return Length(e.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM));
                case "mark": return e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                case "comments": return e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                case "host": return fi?.Host != null ? (object)Ids.ToLong(fi.Host.Id) : null;
                case "view": return e.OwnerViewId != ElementId.InvalidElementId ? (object)Ids.ToLong(e.OwnerViewId) : null;
                case "x": return Coord(e, 0);
                case "y": return Coord(e, 1);
                case "z": return Coord(e, 2);
            }
            // Parameter by name: instance first, then type.
            var p = e.LookupParameter(field);
            if (p == null) p = TypeOf(doc, e, cache)?.LookupParameter(field);
            return ParamValue(doc, p, false, cache);
        }

        private static string LevelName(Document doc, Element e)
        {
            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                        ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                        ?? e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                id = p?.AsElementId();
            }
            if (id == null || id == ElementId.InvalidElementId) return null;
            return doc.GetElement(id)?.Name;
        }

        private static object Coord(Element e, int axis)
        {
            XYZ pt = null;
            if (e.Location is LocationPoint lp) pt = lp.Point;
            else if (e.Location is LocationCurve lc) pt = lc.Curve.Evaluate(0.5, true);
            if (pt == null) return null;
            return Round(axis == 0 ? pt.X : axis == 1 ? pt.Y : pt.Z);
        }

        private static object Length(Parameter p)
        {
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
            return Round(p.AsDouble());
        }

        private static double Round(double ft) => Math.Round(ft, 4);

        public static object ParamValue(Document doc, Parameter p) => ParamValue(doc, p, false, null);

        public static object ParamValue(Document doc, Parameter p, bool mm) => ParamValue(doc, p, mm, null);

        private static (bool Length, bool YesNo) SpecOf(Parameter p, NameCache cache)
        {
            var def = p.Definition;
            if (cache != null && def != null && cache.Spec.TryGetValue(def, out var s)) { cache.Hits++; return s; }
            if (cache != null) cache.Misses++;
            var dataType = DataType(def);
            s = (dataType == SpecTypeId.Length, dataType == SpecTypeId.Boolean.YesNo);
            if (cache != null && def != null) cache.Spec[def] = s;
            return s;
        }

        /// <summary>Simple value of a parameter: lengths in feet (or mm), yes/no as bool, ids as the element name.</summary>
        /// <param name="cache">0.9.1: query cache (data class per definition and target names by id); null = no cache.</param>
        public static object ParamValue(Document doc, Parameter p, bool mm, NameCache cache)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer:
                    if (SpecOf(p, cache).YesNo) return p.AsInteger() != 0;
                    return (long)p.AsInteger();
                case StorageType.Double:
                {
                    var len = SpecOf(p, cache).Length;
                    if (mm && len) return Math.Round(p.AsDouble() * 304.8, 1);
                    if (len) return Round(p.AsDouble());
                    return p.AsValueString() ?? (object)Math.Round(p.AsDouble(), 6);
                }
                case StorageType.ElementId:
                {
                    var id = p.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    var k = Ids.ToLong(id);
                    if (cache != null && cache.Target.TryGetValue(k, out var cached)) { cache.Hits++; return cached; }
                    if (cache != null) cache.Misses++;
                    var target = doc.GetElement(id);
                    var v = target != null ? (object)target.Name : k;
                    if (cache != null) cache.Target[k] = v;
                    return v;
                }
            }
            return null;
        }

        public static bool IsLength(Parameter p) => DataType(p?.Definition) == SpecTypeId.Length;

        private static ForgeTypeId DataType(Definition definition)
        {
            try { return definition?.GetDataType(); }
            catch (Exception) { return null; }
        }
    }
}
