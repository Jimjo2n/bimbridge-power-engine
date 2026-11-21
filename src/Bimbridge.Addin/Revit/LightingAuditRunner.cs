// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Scripting;
using Bimbridge.Engine;
using Bimbridge.Engine.Qc;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Gathers the raw data for "lighting_audit" in slices: fixtures, tags and, if requested, per-view counts.
    /// Read-only. The summary is done by LightingAudit outside the Revit thread.
    /// </summary>
    public sealed class LightingAuditRunner
    {
        private readonly List<string> _levels;
        private readonly string _viewsArg;
        private readonly bool _exact;
        private readonly bool _readInventory;
        private int _phase;
        private List<ElementId> _viewIds;
        private int _nextView;

        public readonly List<LightingAudit.Fixture> Fixtures = new List<LightingAudit.Fixture>();
        public readonly List<LightingAudit.Tag> Tags = new List<LightingAudit.Tag>();
        public readonly List<LightingAudit.ViewCount> Views = new List<LightingAudit.ViewCount>();
        public readonly List<ManifestQc.Actual> Inventory = new List<ManifestQc.Actual>();
        /// <summary>All fixtures in the model (also outside the level filter), so tags on other levels are not mistaken for orphans.</summary>
        public readonly HashSet<long> AllFixtureIds = new HashSet<long>();
        public List<string> Levels => _levels;
        public long Rev;
        public string Error;
        public bool Stale;
        /// <summary>0.9.1: audit name cache (observed hits and misses for the metrics).</summary>
        public readonly ElementFields.NameCache Names = new ElementFields.NameCache();
        /// <summary>Fixtures and tags opened (observed).</summary>
        public long ElementsRead;

        /// <param name="levels">Filter fixtures by level (empty = all).</param>
        /// <param name="views">null = no per-view count; "rcp" = ceiling plans with fixture tags; "rcp_all" = all
        /// ceiling plans; or comma-separated ids. With <paramref name="levels"/> only the views of those levels.</param>
        /// <param name="exact">true = fixtures actually visible (regenerates each view; at most LightingAudit.MaxExactViews).
        /// false = estimate by associated level, without regenerating anything.</param>
        public LightingAuditRunner(List<string> levels, string views, bool exact, bool readInventory = false)
        {
            _levels = levels ?? new List<string>();
            _viewsArg = string.IsNullOrWhiteSpace(views) ? null : views.Trim();
            _exact = exact;
            _readInventory = readInventory;
        }

        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            if (doc.IsFamilyDocument) { Error = Status.NotSupported + ": lighting_audit checks projects; the target " + DocTarget.Current?.Describe() + " is a family. Use fam info or exec"; return true; }
            var deadline = sw.ElapsedMilliseconds + budgetMs;
            if (_readInventory && _phase != 0 && (tracker.For(doc)?.Rev ?? 0) != Rev)
            {
                Stale = true;
                Error = "the document changed while reading fixtures, tags and views; repeat the audit";
                return true;
            }

            if (_phase == 0)
            {
                Rev = tracker.For(doc)?.Rev ?? 0;
                var ws = doc.IsWorkshared ? doc.GetWorksetTable() : null;
                // 0.9.1: a single field list and caches per audit (level by level id, workset by id): thousands of fixtures
                // share a few dozen levels and worksets.
                var lvlField = new List<string> { "lvl" };
                var wsNames = new Dictionary<int, string>();
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType())
                {
                    AllFixtureIds.Add(Ids.ToLong(e.Id));
                    ElementsRead++;
                    var lvl = ElementFields.Read(doc, e, lvlField, Names)["lvl"] as string;
                    if (_levels.Count > 0 && !_levels.Any(l => string.Equals(l, lvl, StringComparison.OrdinalIgnoreCase))) continue;
                    var fi = e as FamilyInstance;
                    Fixtures.Add(new LightingAudit.Fixture
                    {
                        Id = Ids.ToLong(e.Id),
                        Level = lvl,
                        Workset = ws == null ? null : (wsNames.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : wsNames[e.WorksetId.IntegerValue] = ws.GetWorkset(e.WorksetId)?.Name),
                        Type = fi?.Symbol != null ? fi.Symbol.FamilyName + ": " + fi.Symbol.Name : e.Name,
                    });
                    if (_readInventory && Bim.Manifest.Observe(doc, e) is ManifestQc.Actual actual)
                        Inventory.Add(actual);
                }
                _phase = 1;
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }

            if (_phase == 1)
            {
                // 0.9.1: view name and level once per view, not per tag (hundreds of tags per view).
                var viewInfo = new Dictionary<long, (string Name, string Level)>();
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtureTags).WhereElementIsNotElementType())
                {
                    if (!(e is IndependentTag tag)) continue;
                    ElementsRead++;
                    var vid = Ids.ToLong(tag.OwnerViewId);
                    if (!viewInfo.TryGetValue(vid, out var vi))
                    {
                        Names.Misses++;
                        var view = doc.GetElement(tag.OwnerViewId) as View;
                        viewInfo[vid] = vi = (view?.Name, (view as ViewPlan)?.GenLevel?.Name);
                    }
                    else Names.Hits++;
                    Tags.Add(new LightingAudit.Tag
                    {
                        Id = Ids.ToLong(tag.Id),
                        ViewId = vid,
                        ViewName = vi.Name,
                        ViewLevel = vi.Level,
                        Tagged = TaggedIds(tag),
                    });
                }
                _phase = 2;
                if (_viewsArg == null) return true;
                _viewIds = ResolveViews(doc);
                if (Error != null) return true;
                if (!_exact)
                {
                    // Estimate by level: only reads each view's name and level. Regenerates nothing.
                    foreach (var id in _viewIds)
                        if (doc.GetElement(id) is View v) Views.Add(Describe(v, null));
                    return true;
                }
                if (_viewIds.Count > LightingAudit.MaxExactViews)
                {
                    Error = "exact=true regenerates each view in Revit (about 2 s per view) and there are " + _viewIds.Count +
                            " here; maximum " + LightingAudit.MaxExactViews + ". Narrow with lvl= or views=<ids>, or use the by-level mode (without exact).";
                    return true;
                }
                if (sw.ElapsedMilliseconds >= deadline) return false;
            }

            // Phase 2 (exact only): visible fixtures per view, at most one view per visit
            // so Revit stays responsive and deadline cancellation acts between views.
            if (_nextView < _viewIds.Count)
            {
                var v = doc.GetElement(_viewIds[_nextView++]) as View;
                if (v != null)
                {
                    var visible = new FilteredElementCollector(doc, v.Id).OfCategory(BuiltInCategory.OST_LightingFixtures)
                        .WhereElementIsNotElementType().ToElementIds().Select(Ids.ToLong).ToArray();
                    Views.Add(Describe(v, visible));
                }
            }
            return _nextView >= _viewIds.Count;
        }

        private static long[] TaggedIds(IndependentTag tag)
        {
            try
            {
                // Only elements of the document itself (not from links). Exists since Revit 2022.
                return tag.GetTaggedLocalElementIds()
                          .Where(id => id != null && id != ElementId.InvalidElementId)
                          .Select(Ids.ToLong).ToArray();
            }
            catch (Exception) { return new long[0]; }
        }

        private LightingAudit.ViewCount Describe(View v, long[] visible) => new LightingAudit.ViewCount
        {
            ViewId = Ids.ToLong(v.Id),
            Name = v.Name,
            Level = (v as ViewPlan)?.GenLevel?.Name,
            Visible = visible,
        };

        private List<ElementId> ResolveViews(Document doc)
        {
            var all = string.Equals(_viewsArg, "rcp_all", StringComparison.OrdinalIgnoreCase);
            if (all || string.Equals(_viewsArg, "rcp", StringComparison.OrdinalIgnoreCase))
            {
                // Previously "rcp" meant ALL ceiling plans (261 in SAMPLE PROJECT: layout, alarm, dimensions…) counted
                // with one collector per view, which forces regenerating them: Revit stayed busy ~10 min. Now "rcp" means
                // only the ceiling plans that already have fixture tags (the lighting views).
                var withTags = new HashSet<long>(Tags.Select(t => t.ViewId));
                return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.CeilingPlan)
                    .Where(v => all || withTags.Contains(Ids.ToLong(v.Id)))
                    .Where(v => _levels.Count == 0 || _levels.Any(l => string.Equals(l, v.GenLevel?.Name, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(v => v.Name)
                    .Select(v => v.Id).ToList();
            }
            var list = new List<ElementId>();
            foreach (var part in _viewsArg.Split(','))
            {
                if (!long.TryParse(part.Trim(), out var id)) { Error = "views: use rcp, rcp_all or comma-separated view ids"; return null; }
                if (!(doc.GetElement(Ids.ToId(id)) is View)) { Error = "views: " + id + " is not a view of the active model"; return null; }
                list.Add(Ids.ToId(id));
            }
            return list;
        }
    }
}
