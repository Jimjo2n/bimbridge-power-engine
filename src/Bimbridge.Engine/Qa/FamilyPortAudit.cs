// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine.Geo;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qa
{
    public static class FamilyPortAudit
    {
        public static IEnumerable<string> Compare(List<Dictionary<string, object>> expected, List<object> observed)
        {
            if (observed == null)
            {
                yield return "no placed instance with readable connectors";
                yield break;
            }
            if (expected.Count != observed.Count)
                yield return "the project shows " + observed.Count + " connectors and the RFA has " + expected.Count;
            var byKey = observed.OfType<Dictionary<string, object>>().ToLookup(p => Arg.GetString(p, "key"), StringComparer.OrdinalIgnoreCase);
            foreach (var group in expected.GroupBy(p => Arg.GetString(p, "key"), StringComparer.OrdinalIgnoreCase))
            {
                var key = group.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (group.Count() != 1)
                {
                    yield return key + " is repeated in the RFA";
                    continue;
                }
                if (byKey[key].Count() != 1)
                {
                    yield return key + " does not appear exactly once in the instance";
                    continue;
                }
                var source = group.Single();
                var port = byKey[key].Single();
                foreach (var field in new[] { "domain", "system", "workflow", "shape" })
                {
                    var wanted = Arg.GetString(source, field);
                    if (wanted != null && wanted != "UndefinedSystemClassification" &&
                        !string.Equals(wanted, Arg.GetString(port, field), StringComparison.OrdinalIgnoreCase))
                        yield return key + ": " + field + " differs from the RFA";
                }
                var radius = ReadRadius(source);
                var observedRadius = ReadRadius(port);
                if (radius.HasValue && (!observedRadius.HasValue || Math.Abs(radius.Value - observedRadius.Value) > 0.1))
                    yield return key + ": radius differs from the RFA";
            }
        }

        private static double? ReadRadius(Dictionary<string, object> port)
        {
            var radius = GeoSummary.Num(port, "radius_mm");
            return radius > 0 ? radius : GeoSummary.Num(port, "diameter_mm") / 2;
        }
    }
}
