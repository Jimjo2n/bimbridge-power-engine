// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine.Workflows
{
    public static class MarkupPlacement
    {
        private static Dictionary<string, object> Map(object value) => value as Dictionary<string, object> ??
            throw new QueryException("each anchor or terminal must be a JSON object");
        private static List<object> List(Dictionary<string, object> args, string key) =>
            args.TryGetValue(key, out var value) && value is List<object> list && list.Count > 0 ? list :
                throw new QueryException("missing list " + key);
        private static string Text(Dictionary<string, object> args, string key) =>
            Arg.GetString(args, key)?.Trim() is string value && value.Length > 0 ? value :
                throw new QueryException("missing " + key);
        private static double Number(object value)
        {
            if (!(value is long || value is double)) throw new QueryException("a numeric coordinate is required");
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new QueryException("a finite coordinate is required");
            return number;
        }
        private static (double X, double Y) Point(Dictionary<string, object> row, string key)
        {
            var list = List(row, key);
            if (list.Count != 2) throw new QueryException(key + " requires two coordinates");
            return (Number(list[0]), Number(list[1]));
        }

        public static Dictionary<string, object> Plan(Dictionary<string, object> args, string sourceSha, string sourceId, double userUnit, bool equipment = false)
        {
            var level = Text(args, "nivel");
            var parameter = equipment ? null : Text(args, "parametro_sentido");
            var equipmentRules = equipment ? EquipmentRules(List(args, "reglas")) : null;
            var anchors = List(args, "anclas").Select(Map).Select(a =>
            {
                var (px, py) = Point(a, "pdf");
                var (x, y) = Point(a, "modelo");
                return new Anchor(Text(a, "nombre"), px, py, x, y);
            }).ToList();
            var registration = Registration.Solve(anchors, new RegistrationOptions
            {
                UserUnit = userUnit, TolFt = args.TryGetValue("tol", out var tol) ? Number(tol) : 0.25,
            });
            var result = new Dictionary<string, object> { ["registro"] = registration.ToJson(), ["sha256"] = sourceSha };
            if (!registration.Ok) { result["estado"] = "sin_registro"; return result; }

            var items = List(args, "items");
            if (items.Count > PlanOpsLimits.MaxEntries) throw new QueryException("too many terminals in a single call");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var existing = new HashSet<long>();
            var rows = new List<object>();
            var operations = new List<Dictionary<string, object>>();
            foreach (var raw in items)
            {
                var item = Map(raw);
                var id = Text(item, "ref");
                if (!names.Add(id)) throw new QueryException("duplicate reference: " + id);
                var (px, py) = Point(item, "pt");
                var (x, y) = registration.Map(px, py);
                if (!item.TryGetValue("elev", out var elevation)) throw new QueryException("missing elev for " + id);
                var offset = Number(elevation);
                var expected = new Dictionary<string, object>
                {
                    ["ref"] = id, ["xy_ft"] = new List<object> { x, y }, ["elev_ft"] = offset,
                    ["nivel"] = level,
                    ["punto_pt"] = new List<object> { px, py },
                };
                if (equipmentRules == null)
                {
                    var direction = Text(item, "sentido").ToUpperInvariant();
                    if (direction != "UP" && direction != "DN") throw new QueryException("sentido of " + id + ": use UP or DN");
                    expected["tipo"] = Text(item, "tipo");
                    expected["sentido"] = direction;
                    expected["parametro_sentido"] = parameter;
                }
                else
                {
                    var scope = Text(item, "ambito");
                    var code = Text(item, "codigo");
                    if (!equipmentRules.TryGetValue((scope.ToUpperInvariant(), code.ToUpperInvariant()), out var rule))
                        throw new QueryException("missing equipment rule for " + scope + " / " + code + " (" + id + ")");
                    expected["ambito"] = scope;
                    expected["zona"] = Text(item, "zona");
                    expected["codigo"] = code;
                    expected["clase"] = rule.Role;
                    expected["tipo"] = rule.Type;
                    expected["evidencia"] = Text(item, "evidencia");
                }
                if (Arg.GetString(item, "ws") is string workset) expected["ws"] = workset;
                if (item.TryGetValue("giro_pt", out var rotation))
                    expected["giro_deg"] = registration.RotationDeg + Number(rotation);
                rows.Add(expected);
                if (item.ContainsKey("existente_id"))
                {
                    if (!(Arg.GetLong(item, "existente_id") is long reused) || reused <= 0 || !existing.Add(reused))
                        throw new QueryException("existing ID invalid or used more than once: " + id);
                    expected["id"] = reused;
                    expected["estado"] = "reutilizacion_por_verificar";
                    continue;
                }
                var operation = new Dictionary<string, object>
                {
                    ["op"] = "place", ["type"] = expected["tipo"], ["level"] = level, ["elev"] = offset,
                    ["puntos"] = new List<object> { new List<object> { x, y } },
                    ["items_fuente"] = new List<object> { id },
                };
                if (parameter != null) operation["params"] = new Dictionary<string, object> { [parameter] = expected["sentido"] };
                if (expected.TryGetValue("ws", out var configuredWorkset)) operation["ws"] = configuredWorkset;
                if (expected.TryGetValue("giro_deg", out var modelRotation)) operation["rot"] = modelRotation;
                expected["estado"] = "para_colocar";
                operations.Add(operation);
            }
            var plans = new List<object>();
            var fingerprint = Home.Sha256Hex(OpSemantics.Canonical(new object[] { rows, operations }));
            for (var start = 0; start < operations.Count; start += OpSpec.MaxOps)
            {
                var token = SourceBinding.Token(equipment ? "equipos" : "terminales", sourceId, sourceSha, null,
                    SourceBinding.RegistrationFingerprint(registration.M), fingerprint, plans.Count + 1);
                var batch = operations.Skip(start).Take(OpSpec.MaxOps).ToList();
                foreach (var operation in batch)
                {
                    operation[SourceBinding.OpField] = token;
                    OpSpec.Parse(operation);
                }
                plans.Add(new Dictionary<string, object> { ["ops"] = Json.Write(batch) });
            }
            result["estado"] = "listo";
            result[equipment ? "equipos" : "terminales"] = rows;
            result["planes"] = plans;
            return result;
        }

        private static Dictionary<(string Scope, string Code), (string Role, string Type)> EquipmentRules(List<object> values)
        {
            var rules = new Dictionary<(string, string), (string, string)>();
            foreach (var entry in values.Select(Map))
            {
                var scope = Text(entry, "ambito");
                var code = Text(entry, "codigo");
                var role = Text(entry, "clase").ToLowerInvariant();
                if (role != "tablero" && role != "msde" && role != "desconectivo_hvac")
                    throw new QueryException("invalid clase for " + code + "; use tablero, msde or desconectivo_hvac");
                var key = (scope.ToUpperInvariant(), code.ToUpperInvariant());
                if (rules.ContainsKey(key)) throw new QueryException("contradictory equipment rules for " + scope + " / " + code);
                rules.Add(key, (role, Text(entry, "tipo")));
            }
            return rules;
        }
    }
}
