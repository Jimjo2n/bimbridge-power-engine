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
            var level = Text(args, "level");
            var parameter = equipment ? null : Text(args, "direction_parameter");
            var equipmentRules = equipment ? EquipmentRules(List(args, "rules")) : null;
            var anchors = List(args, "anchors").Select(Map).Select(a =>
            {
                var (px, py) = Point(a, "pdf");
                var (x, y) = Point(a, "model");
                return new Anchor(Text(a, "name"), px, py, x, y);
            }).ToList();
            var registration = Registration.Solve(anchors, new RegistrationOptions
            {
                UserUnit = userUnit, TolFt = args.TryGetValue("tol", out var tol) ? Number(tol) : 0.25,
            });
            var result = new Dictionary<string, object> { ["registration"] = registration.ToJson(), ["sha256"] = sourceSha };
            if (!registration.Ok) { result["status"] = "unregistered"; return result; }

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
                    ["level"] = level,
                    ["point_pt"] = new List<object> { px, py },
                };
                if (equipmentRules == null)
                {
                    var direction = Text(item, "direction").ToUpperInvariant();
                    if (direction != "UP" && direction != "DN") throw new QueryException("direction of " + id + ": use UP or DN");
                    expected["type"] = Text(item, "type");
                    expected["direction"] = direction;
                    expected["direction_parameter"] = parameter;
                }
                else
                {
                    var scope = Text(item, "scope");
                    var code = Text(item, "code");
                    if (!equipmentRules.TryGetValue((scope.ToUpperInvariant(), code.ToUpperInvariant()), out var rule))
                        throw new QueryException("missing equipment rule for " + scope + " / " + code + " (" + id + ")");
                    expected["scope"] = scope;
                    expected["zone"] = Text(item, "zone");
                    expected["code"] = code;
                    expected["class"] = rule.Role;
                    expected["type"] = rule.Type;
                    expected["evidence"] = Text(item, "evidence");
                }
                if (Arg.GetString(item, "ws") is string workset) expected["ws"] = workset;
                if (item.TryGetValue("rotation_pt", out var rotation))
                    expected["rotation_deg"] = registration.RotationDeg + Number(rotation);
                rows.Add(expected);
                if (item.ContainsKey("existing_id"))
                {
                    if (!(Arg.GetLong(item, "existing_id") is long reused) || reused <= 0 || !existing.Add(reused))
                        throw new QueryException("existing ID invalid or used more than once: " + id);
                    expected["id"] = reused;
                    expected["status"] = "reuse_to_verify";
                    continue;
                }
                var operation = new Dictionary<string, object>
                {
                    ["op"] = "place", ["type"] = expected["type"], ["level"] = level, ["elev"] = offset,
                    ["points"] = new List<object> { new List<object> { x, y } },
                    ["source_items"] = new List<object> { id },
                };
                if (parameter != null) operation["params"] = new Dictionary<string, object> { [parameter] = expected["direction"] };
                if (expected.TryGetValue("ws", out var configuredWorkset)) operation["ws"] = configuredWorkset;
                if (expected.TryGetValue("rotation_deg", out var modelRotation)) operation["rot"] = modelRotation;
                expected["status"] = "to_place";
                operations.Add(operation);
            }
            var plans = new List<object>();
            var fingerprint = Home.Sha256Hex(OpSemantics.Canonical(new object[] { rows, operations }));
            for (var start = 0; start < operations.Count; start += OpSpec.MaxOps)
            {
                var token = SourceBinding.Token(equipment ? "equipment" : "terminals", sourceId, sourceSha, null,
                    SourceBinding.RegistrationFingerprint(registration.M), fingerprint, plans.Count + 1);
                var batch = operations.Skip(start).Take(OpSpec.MaxOps).ToList();
                foreach (var operation in batch)
                {
                    operation[SourceBinding.OpField] = token;
                    OpSpec.Parse(operation);
                }
                plans.Add(new Dictionary<string, object> { ["ops"] = Json.Write(batch) });
            }
            result["status"] = "ready";
            result[equipment ? "equipment" : "terminals"] = rows;
            result["plans"] = plans;
            return result;
        }

        private static Dictionary<(string Scope, string Code), (string Role, string Type)> EquipmentRules(List<object> values)
        {
            var rules = new Dictionary<(string, string), (string, string)>();
            foreach (var entry in values.Select(Map))
            {
                var scope = Text(entry, "scope");
                var code = Text(entry, "code");
                var role = Text(entry, "class").ToLowerInvariant();
                if (role != "panel" && role != "msde" && role != "hvac_disconnect")
                    throw new QueryException("invalid class for " + code + "; use panel, msde or hvac_disconnect");
                var key = (scope.ToUpperInvariant(), code.ToUpperInvariant());
                if (rules.ContainsKey(key)) throw new QueryException("contradictory equipment rules for " + scope + " / " + code);
                rules.Add(key, (role, Text(entry, "type")));
            }
            return rules;
        }
    }
}
