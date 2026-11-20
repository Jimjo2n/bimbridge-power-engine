// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Runs a QuerySpec on the Revit thread, in slices (section 8):
    ///   slice 1: collector with quick filters (category, view, ids) → list of ids;
    ///   following slices: read only the requested fields, until each visit's budget runs out.
    /// Read-only: opens no transactions and subscribes to nothing (P2).
    /// The resulting rows are simple values; filtering, grouping and paging happen outside the Revit thread.
    /// </summary>
    public sealed class QueryRunner
    {
        private readonly QuerySpec _q;
        private readonly IReadOnlyCollection<long> _explicitIds;
        private readonly HashSet<long> _sinceIds;
        private readonly List<string> _fields;
        private List<ElementId> _candidates;
        private readonly ElementFields.NameCache _names = new ElementFields.NameCache();
        /// <summary>0.9.1: name cache of this query (observed hits/misses) and elements opened.</summary>
        public ElementFields.NameCache Names => _names;
        public long ElementsRead;
        private int _next;

        public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();
        public readonly ResultTable.GroupAccumulator Groups;
        public long Rev;
        /// <summary>Revision registry and handle (dN) of the document read: they bind the snapshot cursor (issue #2).</summary>
        public VersionRegistry Registry;
        public string DocId;
        public string Error;
        public bool RevisionChanged;
        public string DocumentTitle;
        /// <summary>Total obtained with GetElementCount (quick count); null if the elements were read.</summary>
        public long? FastCount;

        public QueryRunner(QuerySpec q, IReadOnlyCollection<long> explicitIds, HashSet<long> sinceIds)
        {
            _q = q;
            _explicitIds = explicitIds;
            _sinceIds = sinceIds;
            _fields = q.FieldsToRead();
            if (q.CanAggregateDuringRead && !q.GroupBy.Contains("id", StringComparer.OrdinalIgnoreCase) &&
                !q.Where.Any(c => string.Equals(c.Field, "id", StringComparison.OrdinalIgnoreCase)))
                _fields.Remove("id");
            if (q.CanAggregateDuringRead) Groups = new ResultTable.GroupAccumulator(q);
            // Level, type and workset filters are applied on the fields read.
            if (q.Levels.Count > 0 && !_fields.Contains("lvl")) _fields.Add("lvl");
            if (q.Types.Count > 0) { if (!_fields.Contains("fam")) _fields.Add("fam"); if (!_fields.Contains("type")) _fields.Add("type"); }
            if (q.Worksets.Count > 0 && !_fields.Contains("ws")) _fields.Add("ws");
        }

        /// <summary>One slice of work. Returns true when it finished.</summary>
        public bool Step(UIApplication app, Stopwatch sw, int budgetMs, DocumentTracker tracker)
        {
            var doc = DocTarget.Resolve(app, out var docError);
            if (doc == null) { Error = docError; return true; }
            DocumentTitle = doc.Title;

            if (_candidates == null)
            {
                Registry = tracker.For(doc);
                DocId = DocRegistry.Instance.IdOf(doc);
                Rev = Registry?.Rev ?? 0;
                _candidates = Collect(doc);
                if (Error != null) return true;
            }
            else if (!ReferenceEquals(tracker.For(doc), Registry) || Registry?.Rev != Rev ||
                     DocRegistry.Instance.IdOf(doc) != DocId)
            {
                RevisionChanged = true;
                Error = "The document or its revision changed during the read. Repeat the query.";
                return true;
            }

            var deadline = sw.ElapsedMilliseconds + budgetMs;
            while (_next < _candidates.Count)
            {
                var e = doc.GetElement(_candidates[_next++]);
                if (e != null)
                {
                    ElementsRead++;
                    var row = ElementFields.Read(doc, e, _fields, _names);
                    if (PassesPostFilters(row))
                    {
                        if (Groups != null) Groups.Add(row);
                        else Rows.Add(row);
                    }
                }
                // check the clock every 32 elements: cheap and sufficient
                if ((_next & 31) == 0 && sw.ElapsedMilliseconds >= deadline) return false;
            }
            return true;
        }

        private List<ElementId> Collect(Document doc)
        {
            IEnumerable<ElementId> ids;

            if (_explicitIds != null)
            {
                ids = _explicitIds.Select(Ids.ToId);
            }
            else
            {
                FilteredElementCollector col;
                if (_q.ViewId.HasValue)
                {
                    var view = doc.GetElement(Ids.ToId(_q.ViewId.Value)) as View;
                    if (view == null) { Error = "view " + _q.ViewId.Value + " is not a view of this document"; return null; }
                    col = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    col = new FilteredElementCollector(doc);
                }

                if (_q.Categories.Count > 0)
                {
                    var cats = new List<BuiltInCategory>();
                    foreach (var name in _q.Categories)
                    {
                        if (!TryCategory(name, out var bic)) { Error = "unknown category: " + name + " (use names like LightingFixtures, ElectricalFixtures)"; return null; }
                        cats.Add(bic);
                    }
                    col = col.WherePasses(new ElementMulticategoryFilter(cats));
                }
                col = col.WhereElementIsNotElementType();
                // Pure count: the Revit index gives the total without materializing ids or opening elements.
                if (_q.CanFastCount && _sinceIds == null)
                {
                    FastCount = col.GetElementCount();
                    return new List<ElementId>();
                }
                ids = col.ToElementIds();
            }

            if (_sinceIds != null) ids = ids.Where(id => _sinceIds.Contains(Ids.ToLong(id)));
            return ids.ToList();
        }

        private bool PassesPostFilters(Dictionary<string, object> row)
        {
            if (_q.Levels.Count > 0 && !AnyEquals(row, "lvl", _q.Levels)) return false;
            if (_q.Worksets.Count > 0 && !AnyEquals(row, "ws", _q.Worksets)) return false;
            if (_q.Types.Count > 0)
            {
                var fam = row.TryGetValue("fam", out var f) ? f as string : null;
                var type = row.TryGetValue("type", out var t) ? t as string : null;
                var full = (fam ?? "") + ": " + (type ?? "");
                if (!_q.Types.Any(x => full.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }
            return true;
        }

        private static bool AnyEquals(Dictionary<string, object> row, string key, List<string> values)
        {
            var v = row.TryGetValue(key, out var o) ? o as string : null;
            return v != null && values.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"LightingFixtures" or "OST_LightingFixtures" → BuiltInCategory.</summary>
        public static bool TryCategory(string name, out BuiltInCategory bic)
        {
            var n = (name ?? "").Trim().Replace(" ", "");
            if (!n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) n = "OST_" + n;
            return Enum.TryParse(n, true, out bic) && bic != BuiltInCategory.INVALID;
        }
    }
}
