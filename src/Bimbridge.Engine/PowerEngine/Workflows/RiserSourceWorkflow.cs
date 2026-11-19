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
                throw new QueryException("riser: falta " + key);
        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ??
            throw new QueryException("riser: se esperaba un objeto JSON");
        private static List<object> List(Dictionary<string, object> args, string name) =>
            args.TryGetValue(name, out var value) && value is List<object> rows ? rows :
                throw new QueryException("riser: faltan los objetos de " + name);

        private static Dictionary<string, Edge> Read(IEnumerable<object> rows, string source)
        {
            var result = new Dictionary<string, Edge>(StringComparer.Ordinal);
            foreach (var row in rows.Select(Object))
            {
                var id = Text(row, "id");
                var start = Text(row, "origen");
                var end = Arg.GetString(row, "destino")?.Trim();
                var open = Arg.GetBool(row, "abierto") == true;
                if (open != string.IsNullOrEmpty(end) || (!open && Key(start) == Key(end)))
                    throw new QueryException(source + ": extremo inválido en " + id);
                var key = Key(id);
                if (result.ContainsKey(key)) throw new QueryException(source + ": ID de alimentador repetido " + id);
                result.Add(key, new Edge { Id = id, Start = start, End = open ? null : end, Evidence = row });
            }
            return result;
        }

        public static Dictionary<string, object> Build(Dictionary<string, object> args, List<object> excel, List<object> pdf, bool audit)
        {
            var a = Read(excel, "Excel");
            var b = Read(pdf, "PDF");
            var precedence = (Arg.GetString(args, "precedencia") ?? "acuerdo").ToLowerInvariant();
            if (precedence != "acuerdo" && precedence != "excel" && precedence != "pdf")
                throw new QueryException("precedencia: use acuerdo, excel o pdf");
            var resolved = new List<Edge>();
            var unresolved = false;
            var output = new List<object>();
            foreach (var key in a.Keys.Concat(b.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                a.TryGetValue(key, out var ex);
                b.TryGetValue(key, out var sheet);
                var conflict = ex != null && sheet != null && !Same(ex, sheet);
                var chosen = conflict ? (precedence == "acuerdo" ? null : precedence == "excel" ? ex : sheet) : ex ?? sheet;
                if (chosen == null) unresolved = true;
                else resolved.Add(chosen);
                output.Add(new Dictionary<string, object>
                {
                    ["id"] = chosen?.Id ?? key, ["origen"] = chosen?.Start, ["destino"] = chosen?.End,
                    ["fuente_aplicada"] = chosen == null ? null : chosen == ex ? "excel" : "pdf",
                    ["discrepancia"] = conflict, ["excel"] = ex?.Evidence, ["pdf"] = sheet?.Evidence,
                });
            }
            var result = new Dictionary<string, object> { ["aristas"] = output };
            if (unresolved) { result["estado"] = "fuentes_en_conflicto"; return result; }
            result["estado"] = "listo";
            if (audit)
            {
                var model = Read(List(args, "observado"), "modelo");
                var keys = new HashSet<string>(resolved.Select(e => Key(e.Id)), StringComparer.Ordinal);
                var findings = new List<object>();
                foreach (var expected in resolved)
                {
                    if (!model.TryGetValue(Key(expected.Id), out var actual))
                        findings.Add(new Dictionary<string, object> { ["id"] = expected.Id, ["tipo"] = "faltante" });
                    else if (!Same(expected, actual))
                        findings.Add(new Dictionary<string, object> { ["id"] = expected.Id, ["tipo"] = "extremos_distintos",
                            ["esperado"] = new[] { expected.Start, expected.End }, ["observado"] = new[] { actual.Start, actual.End } });
                }
                foreach (var extra in model.Keys.Where(id => !keys.Contains(id)))
                    findings.Add(new Dictionary<string, object> { ["id"] = model[extra].Id, ["tipo"] = "sobrante" });
                result["diferencias"] = findings;
                result["estado"] = findings.Count == 0 ? "verificado" : "con_diferencias";
                return result;
            }
            result["planes"] = Draw(args, resolved, out var drawing);
            result["esperado_dibujo"] = drawing;
            return result;
        }

        private static List<object> Draw(Dictionary<string, object> args, List<Edge> edges, out List<object> drawing)
        {
            var view = Arg.GetLong(args, "vista") ?? throw new QueryException("indique el ID positivo de la vista de dibujo");
            if (view <= 0) throw new QueryException("indique el ID positivo de la vista de dibujo");
            var style = Text(args, "estilo");
            drawing = new List<object>();
            var operations = new List<Dictionary<string, object>>();
            var nodes = new Dictionary<string, (List<object> Point, string Type)>(StringComparer.Ordinal);
            var layoutEdges = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (args.ContainsKey("bandas"))
                foreach (var band in List(args, "bandas").Select(Object))
                    operations.Add(new Dictionary<string, object>
                    {
                        ["op"] = "detail_rect", ["view"] = view, ["esquinas"] = List(band, "esquinas"), ["estilo"] = Text(band, "estilo"),
                    });
            foreach (var row in List(args, "trazado").Select(Object)) layoutEdges.Add(Key(Text(row, "id")), row);
            foreach (var row in List(args, "nodos").Select(Object))
            {
                var id = Text(row, "id");
                var point = List(row, "punto");
                OpCatalog.Point(point, "riser punto", false);
                var type = Text(row, "tipo");
                nodes.Add(Key(id), (point, type));
                operations.Add(new Dictionary<string, object>
                {
                    ["op"] = "detail_component", ["view"] = view, ["punto"] = point, ["tipo"] = type,
                });
                if (row.TryGetValue("rot", out var rotation)) operations[operations.Count - 1]["rot"] = rotation;
            }
            foreach (var edge in edges)
            {
                if (!nodes.TryGetValue(Key(edge.Start), out var start))
                    throw new QueryException("falta el símbolo del origen " + edge.Start);
                if (!layoutEdges.TryGetValue(Key(edge.Id), out var layout))
                    throw new QueryException("falta el trazado y posición del rótulo de " + edge.Id);
                List<object> end;
                string endType = null;
                if (edge.End == null) end = List(layout, "fin_abierto");
                else
                {
                    if (!nodes.TryGetValue(Key(edge.End), out var destination))
                        throw new QueryException("falta el símbolo del destino " + edge.End);
                    end = destination.Point;
                    endType = destination.Type;
                }
                var line = new List<object> { start.Point };
                if (layout.ContainsKey("codos")) line.AddRange(List(layout, "codos"));
                line.Add(end);
                var label = List(layout, "rotulo");
                drawing.Add(new Dictionary<string, object>
                {
                    ["id"] = edge.Id, ["origen"] = edge.Start, ["destino"] = edge.End, ["abierto"] = edge.End == null,
                    ["tipo_origen"] = start.Type, ["tipo_destino"] = endType,
                    ["puntos"] = line, ["rotulo"] = label,
                });
                operations.Add(new Dictionary<string, object> { ["op"] = "detail_line", ["view"] = view, ["puntos"] = line, ["estilo"] = style });
                operations.Add(new Dictionary<string, object> { ["op"] = "text_note", ["view"] = view,
                    ["punto"] = label, ["texto"] = edge.Id });
            }
            foreach (var op in operations) OpSpec.Parse(op);
            var plans = new List<object>();
            for (var start = 0; start < operations.Count; start += OpSpec.MaxOps)
                plans.Add(new Dictionary<string, object> { ["ops"] = Json.Write(operations.Skip(start).Take(OpSpec.MaxOps).ToList()) });
            return plans;
        }
    }
}
