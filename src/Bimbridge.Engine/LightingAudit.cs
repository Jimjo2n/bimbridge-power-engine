// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine.Qc;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Server-side "lighting_audit" recipe (section 7, check tool).
    /// What used to take 5–10 round trips with scripts (inventory, tags, orphans, duplicates,
    /// worksets, visibility per RCP) is answered in one call.
    /// This only summarizes; the Revit adapter collects the raw data.
    /// The tag rule is a task profile (F12, R044, A15), not something hard-coded:
    ///   tags=one      exactly one tag per fixture and view, no tag without a fixture (SAMPLE PROJECT Non-Unit Lighting);
    ///   tags=none     fixtures carry no tags (e.g. Unit Grouping): any fixture tag is a difference;
    ///   tags=ignore   only counts, does not judge.
    /// </summary>
    public static class LightingAudit
    {
        public sealed class Fixture
        {
            public long Id;
            public string Level;
            public string Workset;
            public string Type;
        }

        public sealed class Tag
        {
            public long Id;
            public long ViewId;
            public string ViewName;
            /// <summary>Level associated with the tag's view (to filter orphans by level).</summary>
            public string ViewLevel;
            /// <summary>Tagged elements that exist in the document (empty = orphan).</summary>
            public long[] Tagged = new long[0];
        }

        /// <summary>
        /// A view to check. Without <see cref="Visible"/> it is estimated by associated level (without regenerating anything in Revit);
        /// with <see cref="Visible"/> they are the fixtures actually visible (exact mode, regenerates the view).
        /// </summary>
        public sealed class ViewCount
        {
            public long ViewId;
            public string Name;
            public string Level;
            public long[] Visible;
        }

        /// <summary>Maximum views in exact mode: each one forces Revit to regenerate it (~2 s in SAMPLE PROJECT).</summary>
        public const int MaxExactViews = 200;
        /// <summary>Maximum view rows in the response.</summary>
        public const int MaxViewRows = 60;

        public const int SampleSize = 20;

        public static Dictionary<string, object> CompareInventory(List<ManifestQc.Expected> expected, ManifestQc.Options options,
            List<ManifestQc.Actual> physical, IList<Fixture> fixtures, IList<Tag> tags, IList<ViewCount> views)
        {
            var viewIds = new HashSet<long>(views.Select(v => v.ViewId));
            var tagsPerFixture = tags.Where(t => (viewIds.Count == 0 || viewIds.Contains(t.ViewId)) && t.Tagged != null)
                .SelectMany(t => t.Tagged.Distinct().Select(id => (Fixture: id, t.ViewId, Tag: t.Id)))
                .GroupBy(t => t.Fixture).ToDictionary(g => g.Key, g => g.GroupBy(t => t.ViewId).Max(view => view.Select(t => t.Tag).Distinct().Count()));
            foreach (var fixture in physical)
                fixture.Tags = tagsPerFixture.TryGetValue(fixture.Id, out var count) ? count : 0;
            var result = ManifestQc.Run(expected, physical, options).ToJson();
            var positionedIds = new HashSet<long>(physical.Select(e => e.Id));
            var withoutLocation = fixtures.Where(e => !positionedIds.Contains(e.Id)).Select(e => e.Id).ToArray();
            result["no_evaluable_position"] = Block(withoutLocation);
            if (withoutLocation.Length > 0) result["approved"] = false;
            return result;
        }

        /// <summary>Returns the summary and, separately, the sets worth saving for the next call.</summary>
        /// <param name="allFixtureIds">All fixtures in the model, even when filtering by level. Without this, with lvl= the tags
        /// of fixtures on other levels looked orphaned (877 false positives on LEVEL 02, 0.2.2 test of 2026-10-15).</param>
        /// <param name="levels">Requested levels (empty = all): tags on other levels do not count.</param>
        public static Dictionary<string, object> Summarize(
            IList<Fixture> fixtures, IList<Tag> tags, IList<ViewCount> views,
            out Dictionary<string, long[]> setsToSave,
            ICollection<long> allFixtureIds = null, IList<string> levels = null, string tagRule = "one")
        {
            tagRule = (tagRule ?? "one").Trim().ToLowerInvariant();
            if (tagRule != "one" && tagRule != "none" && tagRule != "ignore")
                throw new ArgumentException("tags must be one, none or ignore");
            setsToSave = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var fixtureIds = new HashSet<long>(fixtures.Select(f => f.Id));
            var all = allFixtureIds != null ? new HashSet<long>(allFixtureIds) : fixtureIds;
            all.UnionWith(fixtureIds);
            var filtered = levels != null && levels.Count > 0;
            bool IsOrphan(Tag t) => t.Tagged == null || !t.Tagged.Any(all.Contains);
            if (filtered)
            {
                var lv = new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase);
                // Only the tags in this scope: those tagging fixtures on those levels, or orphans in views of those levels.
                tags = tags.Where(t => (t.Tagged != null && t.Tagged.Any(fixtureIds.Contains)) || (IsOrphan(t) && t.ViewLevel != null && lv.Contains(t.ViewLevel))).ToList();
            }

            // Orphan tags: they tag no existing fixture in the model.
            var orphan = tags.Where(IsOrphan).Select(t => t.Id).ToArray();

            // Tags per fixture and view.
            var perFixtureView = new Dictionary<(long fixture, long view), List<long>>();
            var tagged = new HashSet<long>();
            foreach (var t in tags)
            {
                if (t.Tagged == null) continue;
                foreach (var fid in t.Tagged.Where(fixtureIds.Contains).Distinct())
                {
                    tagged.Add(fid);
                    var key = (fid, t.ViewId);
                    if (!perFixtureView.TryGetValue(key, out var list)) perFixtureView[key] = list = new List<long>();
                    list.Add(t.Id);
                }
            }
            var untagged = fixtures.Where(f => !tagged.Contains(f.Id)).Select(f => f.Id).ToArray();
            // Duplicates: more than one tag of the same fixture in the same view. The extra ones are proposed.
            var dupGroups = perFixtureView.Where(kv => kv.Value.Count > 1).ToList();
            var extraTags = dupGroups.SelectMany(kv => kv.Value.OrderBy(x => x).Skip(1)).ToArray();

            var result = new Dictionary<string, object>
            {
                ["fixtures"] = (long)fixtures.Count,
                ["tags"] = (long)tags.Count,
                ["by_level"] = CountBy(fixtures, f => f.Level),
                ["by_workset"] = CountBy(fixtures, f => f.Workset),
                ["by_type"] = CountBy(fixtures, f => f.Type),
                ["untagged"] = Block(untagged),
                ["orphan_tags"] = Block(orphan),
                ["duplicate_tags"] = new Dictionary<string, object>
                {
                    ["n"] = (long)dupGroups.Count,
                    ["leftovers"] = (long)extraTags.Length,
                    ["sample"] = dupGroups.Take(SampleSize)
                        .Select(kv => (object)new object[] { kv.Key.fixture, kv.Key.view, kv.Value.Cast<object>().ToList() })
                        .ToList(),
                    ["sample_cols"] = new List<object> { "fixture", "view", "tags" },
                },
            };

            if (views != null && views.Count > 0)
            {
                var exact = views.All(v => v.Visible != null);
                var tagsByView = new Dictionary<long, HashSet<long>>();
                var tagCountByView = new Dictionary<long, long>();
                foreach (var t in tags)
                {
                    tagCountByView[t.ViewId] = (tagCountByView.TryGetValue(t.ViewId, out var c) ? c : 0) + 1;
                    if (!tagsByView.TryGetValue(t.ViewId, out var hs)) tagsByView[t.ViewId] = hs = new HashSet<long>();
                    if (t.Tagged != null) foreach (var f in t.Tagged) hs.Add(f);
                }
                var byLevel = fixtures.GroupBy(f => f.Level ?? "", StringComparer.OrdinalIgnoreCase)
                                      .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
                var rows = new List<object>();
                foreach (var v in views)
                {
                    var inView = v.Visible ?? (byLevel.TryGetValue(v.Level ?? "", out var ids) ? ids : new long[0]);
                    tagsByView.TryGetValue(v.ViewId, out var taggedHere);
                    var missing = inView.Count(id => taggedHere == null || !taggedHere.Contains(id));
                    rows.Add(new object[] { v.ViewId, v.Name, v.Level ?? "", (long)inView.Length, tagCountByView.TryGetValue(v.ViewId, out var n) ? n : 0L, (long)missing });
                }
                result["views_cols"] = new List<object> { "id", "name", "level", "fixtures", "tags", "untagged_in_view" };
                result["views"] = rows.Take(MaxViewRows).ToList();
                if (rows.Count > MaxViewRows) result["skipped_views"] = (long)(rows.Count - MaxViewRows);
                result["views_method"] = exact
                    ? "visible: fixtures that Revit shows in each view"
                    : "level: fixtures on the level associated with the view (ignores crop, range and filters; use exact=true with few views to confirm)";
            }

            result["tags_rule"] = tagRule;
            if (tagRule == "one")
            {
                var ok = untagged.Length == 0 && orphan.Length == 0 && extraTags.Length == 0;
                result["meets_rule"] = ok;
                if (untagged.Length > 0) setsToSave["$untagged"] = untagged;
                if (extraTags.Length > 0) setsToSave["$tags_sobrantes"] = extraTags;
            }
            else if (tagRule == "none")
            {
                // Fixtures must not carry tags: fixture tags are the difference; "untagged" is expected.
                var fixtureTags = tags.Where(t => t.Tagged != null && t.Tagged.Any(fixtureIds.Contains)).Select(t => t.Id).ToArray();
                result.Remove("untagged");
                result["fixture_tags"] = Block(fixtureTags);
                result["meets_rule"] = fixtureTags.Length == 0 && orphan.Length == 0;
                if (fixtureTags.Length > 0) setsToSave["$fixture_tags"] = fixtureTags;
            }
            if (orphan.Length > 0) setsToSave["$orphan_tags"] = orphan;
            return result;
        }

        private static Dictionary<string, object> Block(long[] ids) => new Dictionary<string, object>
        {
            ["n"] = (long)ids.Length,
            ["sample"] = ids.Take(SampleSize).Cast<object>().ToList(),
        };

        private static Dictionary<string, object> CountBy(IEnumerable<Fixture> items, Func<Fixture, string> key)
        {
            var d = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
            {
                var k = key(i) ?? "(no data)";
                d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            return d.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
    }
}
