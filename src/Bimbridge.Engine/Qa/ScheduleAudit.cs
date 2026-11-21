// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qa
{
    /// <summary>Checks a schedule's population, configuration and cells against external expectations.</summary>
    public sealed class ScheduleAudit
    {
        public sealed class Cell
        {
            public int Row, Column;
            public string Text;
        }

        private readonly HashSet<long> _ids;
        private readonly long? _count;
        private readonly List<object> _filters;
        public readonly List<Cell> Cells = new List<Cell>();
        public bool NeedsPopulation => _ids != null || _count.HasValue;

        public ScheduleAudit(Dictionary<string, object> args)
        {
            if (args.TryGetValue("expected_ids", out var ids))
            {
                if (!(ids is List<object> list) || list.Any(x => !(x is long id) || id <= 0))
                    throw new QueryException("expected_ids requires a JSON list of positive IDs");
                _ids = new HashSet<long>(list.Cast<long>());
                if (_ids.Count != list.Count) throw new QueryException("expected_ids contains duplicates");
            }
            if (args.TryGetValue("expected_count", out var count))
            {
                if (!(count is long n) || n < 0) throw new QueryException("expected_count requires a non-negative integer");
                _count = n;
            }
            if (args.TryGetValue("expected_filters", out var filters))
            {
                _filters = filters as List<object>;
                if (_filters == null || _filters.Any(f => !(f is Dictionary<string, object>)))
                    throw new QueryException("expected_filters requires a JSON list of filter contracts");
            }
            if (args.TryGetValue("expected_cells", out var cells))
            {
                if (!(cells is List<object> list) || list.Count > 200)
                    throw new QueryException("expected_cells requires a JSON list of up to 200 cells");
                var seen = new HashSet<(int, int)>();
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> cell) ||
                        !cell.TryGetValue("row", out var rawRow) || !(rawRow is long row) || row < 0 || row > int.MaxValue ||
                        !cell.TryGetValue("column", out var rawColumn) || !(rawColumn is long col) || col < 0 || col > int.MaxValue ||
                        !cell.TryGetValue("text", out var rawText) || !(rawText is string value))
                        throw new QueryException("each cell requires row, column (zero-based) and text");
                    if (!seen.Add(((int)row, (int)col))) throw new QueryException("expected_cells contains duplicate coordinates");
                    Cells.Add(new Cell { Row = (int)row, Column = (int)col, Text = value });
                }
            }
        }

        public Dictionary<string, object> Compare(IEnumerable<long> population, List<object> filters, Func<Cell, string> read)
        {
            var differences = new List<object>();
            var ids = population == null ? null : new HashSet<long>(population);
            var checkedCount = 0;
            if (NeedsPopulation && ids == null)
                differences.Add(new Dictionary<string, object> { ["type"] = "population_not_evaluable" });
            if (_ids != null && ids != null)
            {
                checkedCount++;
                var missing = _ids.Except(ids).OrderBy(i => i).ToList();
                var extra = ids.Except(_ids).OrderBy(i => i).ToList();
                if (missing.Count + extra.Count > 0)
                    differences.Add(new Dictionary<string, object>
                    {
                        ["type"] = "population", ["missing_items"] = (long)missing.Count, ["extra_items"] = (long)extra.Count,
                        ["missing_sample"] = missing.Take(20).Cast<object>().ToList(), ["extra_sample"] = extra.Take(20).Cast<object>().ToList(),
                    });
            }
            if (_count.HasValue && ids != null)
            {
                checkedCount++;
                if (ids.Count != _count.Value) differences.Add(new Dictionary<string, object> { ["type"] = "quantity", ["expected"] = _count.Value, ["observed"] = (long)ids.Count });
            }
            if (_filters != null)
            {
                checkedCount++;
                IEnumerable<string> Canonical(List<object> list) => list.Select(OpSemantics.Canonical).OrderBy(x => x, StringComparer.Ordinal);
                if (!Canonical(_filters).SequenceEqual(Canonical(filters)))
                    differences.Add(new Dictionary<string, object> { ["type"] = "filters", ["expected"] = _filters, ["observed"] = filters });
            }
            foreach (var cell in Cells)
            {
                checkedCount++;
                var actual = read(cell);
                if (!string.Equals(actual, cell.Text, StringComparison.Ordinal))
                    differences.Add(new Dictionary<string, object>
                    {
                        ["type"] = actual == null ? "cell_out_of_range" : "cell", ["row"] = (long)cell.Row,
                        ["column"] = (long)cell.Column, ["expected"] = cell.Text, ["observed"] = actual,
                    });
            }
            return new Dictionary<string, object>
            {
                ["result"] = differences.Count > 0 ? (NeedsPopulation && ids == null ? "not_evaluable" : "with_differences") :
                    checkedCount == 0 ? "inventory" : "verified",
                ["checks"] = (long)checkedCount, ["population"] = ids == null ? null : (object)(long)ids.Count,
                ["filters"] = filters, ["differences"] = differences,
            };
        }
    }
}
