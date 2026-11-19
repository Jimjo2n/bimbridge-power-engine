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
                yield return "sin instancia colocada con conectores legibles";
                yield break;
            }
            if (expected.Count != observed.Count)
                yield return "el proyecto muestra " + observed.Count + " conectores y el RFA tiene " + expected.Count;
            var byKey = observed.OfType<Dictionary<string, object>>().ToLookup(p => Arg.GetString(p, "clave"), StringComparer.OrdinalIgnoreCase);
            foreach (var group in expected.GroupBy(p => Arg.GetString(p, "clave"), StringComparer.OrdinalIgnoreCase))
            {
                var key = group.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (group.Count() != 1)
                {
                    yield return key + " se repite en el RFA";
                    continue;
                }
                if (byKey[key].Count() != 1)
                {
                    yield return key + " no aparece una sola vez en la instancia";
                    continue;
                }
                var source = group.Single();
                var port = byKey[key].Single();
                foreach (var field in new[] { "dominio", "sistema", "flujo", "forma" })
                {
                    var wanted = Arg.GetString(source, field);
                    if (wanted != null && wanted != "UndefinedSystemClassification" &&
                        !string.Equals(wanted, Arg.GetString(port, field), StringComparison.OrdinalIgnoreCase))
                        yield return key + ": " + field + " difiere del RFA";
                }
                var radius = ReadRadius(source);
                var observedRadius = ReadRadius(port);
                if (radius.HasValue && (!observedRadius.HasValue || Math.Abs(radius.Value - observedRadius.Value) > 0.1))
                    yield return key + ": radio difiere del RFA";
            }
        }

        private static double? ReadRadius(Dictionary<string, object> port)
        {
            var radius = GeoSummary.Num(port, "radio_mm");
            return radius > 0 ? radius : GeoSummary.Num(port, "diametro_mm") / 2;
        }
    }
}
