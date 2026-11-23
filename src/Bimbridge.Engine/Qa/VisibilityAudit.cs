// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qa
{
    public sealed class VisibilityAudit
    {
        private readonly List<Dictionary<string, object>> _expected = new List<Dictionary<string, object>>();

        public VisibilityAudit(Dictionary<string, object> args, long[] views, long[] elements)
        {
            if (!args.TryGetValue("expectations", out var value)) return;
            if (!(value is List<object> rows) || rows.Count == 0 || rows.Count > 100)
                throw new QueryException("expectations requires 1 to 100 view-and-element objects");
            var pairs = new HashSet<(long, long)>();
            foreach (var item in rows)
            {
                if (!(item is Dictionary<string, object> row) ||
                    !row.TryGetValue("view", out var viewValue) || !(viewValue is long view) || !views.Contains(view) ||
                    !row.TryGetValue("id", out var idValue) || !(idValue is long id) || !elements.Contains(id) ||
                    row.Count < 3 || !pairs.Add((view, id)))
                    throw new QueryException("expectations: give a view and a unique in-scope id pair and its checks");
                var hasFilter = row.ContainsKey("filter_id");
                foreach (var entry in row)
                {
                    var valid = entry.Key switch
                    {
                        "view" or "id" => true,
                        "template_id" => entry.Value is long template && (template == -1 || template > 0),
                        "in_view_collector" => entry.Value is bool,
                        "filter_id" => entry.Value is long filter && filter > 0,
                        "active_filter" or "visible_filter" => hasFilter && entry.Value is bool,
                        "projection_line" => entry.Value is List<object> rgb && rgb.Count == 3 && rgb.All(c => c is long n && n >= 0 && n <= 255),
                        _ => false,
                    };
                    if (!valid) throw new QueryException("expectations: invalid field or value for " + entry.Key);
                }
                _expected.Add(row);
            }
        }

        public List<object> Compare(List<object> observed)
        {
            var differences = new List<object>();
            foreach (var expected in _expected)
            {
                var viewId = (long)expected["view"];
                var id = (long)expected["id"];
                var view = Find(observed, viewId);
                var element = Find(Field(view, "elements"), id);
                if (element == null || Arg.GetString(element, "status") == "not_found")
                {
                    Add("element", true, false);
                    continue;
                }
                Check("template_id", Field(view, "template_id"));
                Check("in_view_collector", Field(element, "in_view_collector"));
                var graphics = Field(element, "element_graphics") as Dictionary<string, object>;
                if (expected.TryGetValue("filter_id", out var filterId))
                {
                    var filter = Find(Field(element, "matching_filters"), (long)filterId);
                    if (filter == null) { Add("filter_id", filterId, null); continue; }
                    Check("active_filter", Field(filter, "active"));
                    Check("visible_filter", Field(filter, "visible"));
                    graphics = Field(filter, "graphics") as Dictionary<string, object>;
                }
                Check("projection_line", Field(graphics, "projection_line"));

                void Check(string field, object actual)
                {
                    if (expected.TryGetValue(field, out var wanted) && Json.Write(wanted) != Json.Write(actual)) Add(field, wanted, actual);
                }

                void Add(string field, object wanted, object actual) => differences.Add(new Dictionary<string, object>
                {
                    ["view"] = viewId, ["id"] = id, ["field"] = field, ["expected"] = wanted, ["observed"] = actual,
                });
            }
            return differences;
        }

        private static Dictionary<string, object> Find(object rows, long id) =>
            (rows as IEnumerable<object>)?.OfType<Dictionary<string, object>>()
                .FirstOrDefault(row => Arg.GetLong(row, "id") == id);

        private static object Field(Dictionary<string, object> row, string key) =>
            row != null && row.TryGetValue(key, out var value) ? value : null;
    }
}
