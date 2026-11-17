// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine.Qa
{
    public static class PrintSheetSet
    {
        public static bool MatchesMembership(IList<long> order, IEnumerable<long> members)
        {
            var actual = new HashSet<long>(members);
            return order.Count == actual.Count && actual.SetEquals(order);
        }

        public static Dictionary<string, object> ApplyPreset(IDictionary<string, object> args, IDictionary<string, object> preset)
        {
            var settings = new Dictionary<string, object>(args, System.StringComparer.Ordinal);
            foreach (var saved in preset)
                if (!settings.ContainsKey(saved.Key)) settings.Add(saved.Key, saved.Value);
            return settings;
        }

        public static bool OutsidePaper(double left, double bottom, double right, double top,
            double paperLeft, double paperBottom, double paperRight, double paperTop)
        {
            const double millimeter = 1.0 / 304.8;
            return left < paperLeft - millimeter || bottom < paperBottom - millimeter ||
                right > paperRight + millimeter || top > paperTop + millimeter;
        }
    }
}
