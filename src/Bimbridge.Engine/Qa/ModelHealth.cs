// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.Engine.Qa
{
    /// <summary>
    /// 0.9.1 (Block H): model health rules over already-read data (without Revit). The add-in reads warnings, elements, connectors,
    /// sheets and views; this class groups and decides. Each finding carries ids so it can be acted on.
    /// </summary>
    public static class ModelHealth
    {
        public sealed class Warning { public string Description; public string Severity; public List<long> Ids = new List<long>(); }
        public sealed class Element
        {
            public long Id; public string Category, Family, Type, Level;
            public double X, Y, Z, RotationDeg;
            public bool HasLocation = true;
            public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        public sealed class Connector { public long ElementId; public string Domain; public bool Connected; public string Category; }
        public sealed class Sheet { public long Id; public string Number, Name; public int Viewports; public int Schedules; }
        public sealed class View { public long Id; public string Name, Kind; public bool OnSheet, IsTemplate, IsDependent; }

        public const double DuplicateTolerance = 0.003;   // feet (~1 mm)
        public const int MaxSample = 20;

        private static Dictionary<string, object> Bucket(IEnumerable<long> ids, string extra = null)
        {
            var l = ids.Distinct().OrderBy(x => x).ToList();
            var d = new Dictionary<string, object> { ["n"] = (long)l.Count, ["ids"] = l.Take(MaxSample).Cast<object>().ToList() };
            if (l.Count > MaxSample) d["more"] = (long)(l.Count - MaxSample);
            if (extra != null) d["detail"] = extra;
            return d;
        }

        /// <summary>Groups of coincident elements: same category, family, type and level, at the same position and rotation (within tolerance).</summary>
        public static List<List<long>> Duplicates(IEnumerable<Element> elements, double tol = DuplicateTolerance)
        {
            var groups = new List<List<long>>();
            // 0.9.1: non-finite coordinates or ones outside the quantization range are excluded (Math.Floor(x/tol) would overflow long).
            const double MaxQuant = 1e9;
            bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < MaxQuant;
            foreach (var g in elements.Where(e => e.HasLocation && Finite(e.X) && Finite(e.Y) && Finite(e.Z) && Finite(e.RotationDeg)).GroupBy(e => (e.Category ?? "") + "|" + e.Family + "|" + e.Type + "|" + e.Level, StringComparer.Ordinal))
            {
                // Spatial grid: each cell is tol in size; neighbors in adjacent cells are compared (O(n) expected, not O(n²)).
                var cell = new Dictionary<(long, long, long), List<Element>>();
                (long, long, long) Key(Element e) => ((long)Math.Floor(e.X / tol), (long)Math.Floor(e.Y / tol), (long)Math.Floor(e.Z / tol));
                foreach (var e in g) { var k = Key(e); if (!cell.TryGetValue(k, out var l)) cell[k] = l = new List<Element>(); l.Add(e); }
                var seen = new HashSet<long>();
                foreach (var e in g.OrderBy(x => x.Id))
                {
                    if (seen.Contains(e.Id)) continue;
                    var (cx, cy, cz) = Key(e);
                    var grp = new List<long> { e.Id };
                    var inGrp = new HashSet<long> { e.Id };   // O(1) membership: in very dense groups List.Contains was quadratic
                    for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!cell.TryGetValue((cx + dx, cy + dy, cz + dz), out var near)) continue;
                        foreach (var o in near)
                        {
                            if (o.Id == e.Id || seen.Contains(o.Id) || inGrp.Contains(o.Id)) continue;
                            var dist = Math.Sqrt((o.X - e.X) * (o.X - e.X) + (o.Y - e.Y) * (o.Y - e.Y) + (o.Z - e.Z) * (o.Z - e.Z));
                            var dr = Math.Abs(((o.RotationDeg - e.RotationDeg) % 360 + 540) % 360 - 180);
                            if (dist <= tol && dr <= 0.5) { grp.Add(o.Id); inGrp.Add(o.Id); }
                        }
                    }
                    if (grp.Count > 1) { foreach (var id in grp) seen.Add(id); groups.Add(grp.OrderBy(x => x).ToList()); }
                }
            }
            return groups.OrderBy(x => x[0]).ToList();
        }

        public static Dictionary<string, object> Report(IList<Warning> warnings, IList<Element> elements, IList<Connector> connectors,
                                                        IList<Sheet> sheets, IList<View> views, IList<string> requiredParams)
        {
            warnings = warnings ?? new List<Warning>(); elements = elements ?? new List<Element>(); connectors = connectors ?? new List<Connector>();
            sheets = sheets ?? new List<Sheet>(); views = views ?? new List<View>(); requiredParams = requiredParams ?? new List<string>();

            var byWarning = warnings.GroupBy(w => w.Description ?? "").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object)new Dictionary<string, object>
                {
                    ["warning"] = g.Key, ["times"] = (long)g.Count(), ["severity"] = g.First().Severity ?? "",
                    ["ids"] = g.SelectMany(w => w.Ids).Distinct().OrderBy(x => x).Take(MaxSample).Cast<object>().ToList(),
                }).ToList();

            var dups = Duplicates(elements);
            var missing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in requiredParams.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var absent = elements.Where(e => !e.Params.ContainsKey(p)).Select(e => e.Id);
                var empty = elements.Where(e => e.Params.TryGetValue(p, out var v) && string.IsNullOrWhiteSpace(v)).Select(e => e.Id);
                missing[p] = new Dictionary<string, object> { ["no_parameter"] = Bucket(absent), ["empty_value"] = Bucket(empty) };
            }
            var disconnected = connectors.Where(c => !c.Connected).GroupBy(c => c.Domain ?? "unknown").OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object)Bucket(g.Select(c => c.ElementId), g.Count() + " free connectors"));

            var emptySheets = sheets.Where(s => s.Viewports == 0 && s.Schedules == 0).Select(s => s.Id);
            var unplaced = views.Where(v => !v.IsTemplate && !v.OnSheet && (v.Kind == "FloorPlan" || v.Kind == "CeilingPlan" || v.Kind == "Section" || v.Kind == "Elevation" || v.Kind == "Detail" || v.Kind == "DraftingView" || v.Kind == "EngineeringPlan" || v.Kind == "AreaPlan"))
                                .Select(v => v.Id);
            var dupViewNames = views.Where(v => !v.IsTemplate).GroupBy(v => (v.Kind ?? "") + "|" + v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g.Select(v => v.Id));

            var problems = warnings.Count + dups.Sum(g => g.Count - 1) + connectors.Count(c => !c.Connected);
            return new Dictionary<string, object>
            {
                ["summary"] = new Dictionary<string, object>
                {
                    ["warnings"] = (long)warnings.Count, ["warning_types"] = (long)byWarning.Count,
                    ["duplicate_groups"] = (long)dups.Count, ["leftover_elements"] = (long)dups.Sum(g => g.Count - 1),
                    ["free_connectors"] = (long)connectors.Count(c => !c.Connected), ["empty_sheets"] = (long)emptySheets.Count(),
                    ["views_without_sheet"] = (long)unplaced.Count(), ["elements_reviewed"] = (long)elements.Count, ["reviewed_connectors"] = (long)connectors.Count,
                    ["problems"] = (long)problems,
                },
                ["warnings"] = byWarning,
                ["duplicates"] = dups.Take(MaxSample).Select(g => (object)g.Cast<object>().ToList()).ToList(),
                ["required_parameters"] = missing,
                ["disconnected_connectors"] = disconnected,
                ["documentation"] = new Dictionary<string, object>
                {
                    ["empty_sheets"] = Bucket(emptySheets), ["views_without_sheet"] = Bucket(unplaced), ["repeated_view_names"] = Bucket(dupViewNames),
                },
                ["note"] = "Read only. Duplicate = same category, family, type and level at the same position and rotation (±" + (DuplicateTolerance * 304.8).ToString("0.#", CultureInfo.InvariantCulture) + " mm, ±0.5°).",
            };
        }
    }
}
