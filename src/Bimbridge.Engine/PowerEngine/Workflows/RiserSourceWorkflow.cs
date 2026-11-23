// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine.Workflows
{
    public static class RiserSourceWorkflow
    {
        private sealed class Edge
        {
            public string Id, Start, End;
            public Dictionary<string, object> Evidence;
        }

        private static string Key(string value) => value?.Trim().ToUpperInvariant();
        private static bool Same(Edge a, Edge b) => Key(a.Start) == Key(b.Start) && Key(a.End) == Key(b.End);
        private static string Text(Dictionary<string, object> item, string key) =>
            Arg.GetString(item, key)?.Trim() is string value && value.Length > 0 ? value :
                throw new QueryException("riser: missing " + key);
        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ??
            throw new QueryException("riser: a JSON object was expected");
        private static List<object> List(Dictionary<string, object> args, string name) =>
            args.TryGetValue(name, out var value) && value is List<object> rows ? rows :
                throw new QueryException("riser: missing the objects of " + name);

        private static Dictionary<string, Edge> Read(IEnumerable<object> rows, string source)
        {
            var result = new Dictionary<string, Edge>(StringComparer.Ordinal);
            foreach (var row in rows.Select(Object))
            {
                var id = Text(row, "id");
                var start = Text(row, "origin");
                var end = Arg.GetString(row, "target")?.Trim();
                var open = Arg.GetBool(row, "open") == true;
                if (open != string.IsNullOrEmpty(end) || (!open && Key(start) == Key(end)))
                    throw new QueryException(source + ": invalid end in " + id);
                var key = Key(id);
                if (result.ContainsKey(key)) throw new QueryException(source + ": repeated feeder ID " + id);
                result.Add(key, new Edge { Id = id, Start = start, End = open ? null : end, Evidence = row });
            }
            return result;
        }

        public static Dictionary<string, object> Build(Dictionary<string, object> args, List<object> excel, List<object> pdf, bool audit)
        {
            var a = Read(excel, "Excel");
            var b = Read(pdf, "PDF");
            var precedence = (Arg.GetString(args, "precedence") ?? "agreement").ToLowerInvariant();
            if (precedence != "agreement" && precedence != "excel" && precedence != "pdf")
                throw new QueryException("precedence: use agreement, excel or pdf");
            var resolved = new List<Edge>();
            var unresolved = false;
            var output = new List<object>();
            foreach (var key in a.Keys.Concat(b.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                a.TryGetValue(key, out var ex);
                b.TryGetValue(key, out var sheet);
                var conflict = ex != null && sheet != null && !Same(ex, sheet);
                var chosen = conflict ? (precedence == "agreement" ? null : precedence == "excel" ? ex : sheet) : ex ?? sheet;
                if (chosen == null) unresolved = true;
                else resolved.Add(chosen);
                output.Add(new Dictionary<string, object>
                {
                    ["id"] = chosen?.Id ?? key, ["origin"] = chosen?.Start, ["target"] = chosen?.End,
                    ["applied_source"] = chosen == null ? null : chosen == ex ? "excel" : "pdf",
                    ["discrepancy"] = conflict, ["excel"] = ex?.Evidence, ["pdf"] = sheet?.Evidence,
                });
            }
            var result = new Dictionary<string, object> { ["edges"] = output };
            if (unresolved) { result["status"] = "conflicting_sources"; return result; }
            result["status"] = "ready";
            if (audit)
            {
                var model = Read(List(args, "observed"), "model");
                var keys = new HashSet<string>(resolved.Select(e => Key(e.Id)), StringComparer.Ordinal);
                var findings = new List<object>();
                foreach (var expected in resolved)
                {
                    if (!model.TryGetValue(Key(expected.Id), out var actual))
                        findings.Add(new Dictionary<string, object> { ["id"] = expected.Id, ["type"] = "missing" });
                    else if (!Same(expected, actual))
                        findings.Add(new Dictionary<string, object> { ["id"] = expected.Id, ["type"] = "different_ends",
                            ["expected"] = new[] { expected.Start, expected.End }, ["observed"] = new[] { actual.Start, actual.End } });
                }
                foreach (var extra in model.Keys.Where(id => !keys.Contains(id)))
                    findings.Add(new Dictionary<string, object> { ["id"] = model[extra].Id, ["type"] = "leftover" });
                result["differences"] = findings;
                result["status"] = findings.Count == 0 ? "verified" : "with_differences";
                return result;
            }
            result["plans"] = Draw(args, resolved, out var drawing);
            result["expected_drawing"] = drawing;
            return result;
        }

        private static List<object> Draw(Dictionary<string, object> args, List<Edge> edges, out List<object> drawing)
        {
            var view = Arg.GetLong(args, "view") ?? throw new QueryException("give the positive ID of the drafting view");
            if (view <= 0) throw new QueryException("give the positive ID of the drafting view");
            var style = Text(args, "style");
            drawing = new List<object>();
            var operations = new List<Dictionary<string, object>>();
            var nodes = new Dictionary<string, (List<object> Point, string Type)>(StringComparer.Ordinal);
            var layoutEdges = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (args.ContainsKey("bands"))
                foreach (var band in List(args, "bands").Select(Object))
                    operations.Add(new Dictionary<string, object>
                    {
                        ["op"] = "detail_rect", ["view"] = view, ["corners"] = List(band, "corners"), ["style"] = Text(band, "style"),
                    });
            foreach (var row in List(args, "trace_path").Select(Object)) layoutEdges.Add(Key(Text(row, "id")), row);
            foreach (var row in List(args, "nodes").Select(Object))
            {
                var id = Text(row, "id");
                var point = List(row, "point");
                OpCatalog.Point(point, "riser point", false);
                var type = Text(row, "type");
                nodes.Add(Key(id), (point, type));
                operations.Add(new Dictionary<string, object>
                {
                    ["op"] = "detail_component", ["view"] = view, ["point"] = point, ["type"] = type,
                });
                if (row.TryGetValue("rot", out var rotation)) operations[operations.Count - 1]["rot"] = rotation;
            }
            foreach (var edge in edges)
            {
                if (!nodes.TryGetValue(Key(edge.Start), out var start))
                    throw new QueryException("missing the symbol for source " + edge.Start);
                if (!layoutEdges.TryGetValue(Key(edge.Id), out var layout))
                    throw new QueryException("missing the route and label position for " + edge.Id);
                List<object> end;
                string endType = null;
                if (edge.End == null) end = List(layout, "open_end");
                else
                {
                    if (!nodes.TryGetValue(Key(edge.End), out var destination))
                        throw new QueryException("missing the symbol for target " + edge.End);
                    end = destination.Point;
                    endType = destination.Type;
                }
                var line = new List<object> { start.Point };
                if (layout.ContainsKey("elbows")) line.AddRange(List(layout, "elbows"));
                line.Add(end);
                var label = List(layout, "label");
                drawing.Add(new Dictionary<string, object>
                {
                    ["id"] = edge.Id, ["origin"] = edge.Start, ["target"] = edge.End, ["open"] = edge.End == null,
                    ["source_type"] = start.Type, ["target_type"] = endType,
                    ["points"] = line, ["label"] = label,
                });
                operations.Add(new Dictionary<string, object> { ["op"] = "detail_line", ["view"] = view, ["points"] = line, ["style"] = style });
                operations.Add(new Dictionary<string, object> { ["op"] = "text_note", ["view"] = view,
                    ["point"] = label, ["text"] = edge.Id });
            }
            foreach (var op in operations) OpSpec.Parse(op);
            var plans = new List<object>();
            for (var start = 0; start < operations.Count; start += OpSpec.MaxOps)
                plans.Add(new Dictionary<string, object> { ["ops"] = Json.Write(operations.Skip(start).Take(OpSpec.MaxOps).ToList()) });
            return plans;
        }
    }
}
