// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    public sealed class GroupMember { public string Type; public double X, Y, RotDeg; public long Id; }

    public sealed class GroupInstance
    {
        public string Id, GroupType, Level;
        public double Ox, Oy, RotDeg;
        public bool Mirrored;
        public List<GroupMember> Members = new List<GroupMember>();
    }

    /// <summary>
    /// Groups and variants: brings each instance to local coordinates (undoes origin, rotation and mirror), computes the signature of its
    /// members and groups identical instances. The majority variant of each group type is the reference; the rest are reported
    /// with what is missing and what is extra. Also gives coverage per group type and level.
    /// </summary>
    public static class GroupVariants
    {
        public static Dictionary<string, object> Analyze(IList<GroupInstance> groups, double tolFt = 1.0 / 48.0)
        {
            var sig = groups.ToDictionary(g => g.Id, g => Signature(g, tolFt), StringComparer.Ordinal);
            var types = new List<object>();
            foreach (var t in groups.GroupBy(g => g.GroupType).OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var variants = t.GroupBy(g => string.Join(";", sig[g.Id])).OrderByDescending(v => v.Count()).ThenBy(v => v.Key, StringComparer.Ordinal).ToList();
                var refSig = variants[0].Key.Length == 0 ? new string[0] : variants[0].Key.Split(';');
                var deviations = new List<object>();
                for (var k = 1; k < variants.Count; k++)
                {
                    var vs = variants[k].Key.Length == 0 ? new string[0] : variants[k].Key.Split(';');
                    deviations.Add(new Dictionary<string, object>
                    {
                        ["instances"] = variants[k].Select(g => (object)g.Id).ToList(),
                        ["missing_items"] = MultisetMinus(refSig, vs).Cast<object>().ToList(),
                        ["extra_items"] = MultisetMinus(vs, refSig).Cast<object>().ToList(),
                    });
                }
                types.Add(new Dictionary<string, object>
                {
                    ["group_type"] = t.Key, ["instances"] = (long)t.Count(), ["variants"] = (long)variants.Count,
                    ["reference"] = new Dictionary<string, object> { ["instances"] = (long)variants[0].Count(), ["members"] = (long)refSig.Length },
                    ["deviations"] = deviations,
                    ["mirrored"] = t.Where(g => g.Mirrored).Select(g => (object)g.Id).ToList(),
                    ["by_level"] = t.GroupBy(g => g.Level ?? "?").OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => (object)(long)l.Count()),
                });
            }
            return new Dictionary<string, object>
            {
                ["groups"] = (long)groups.Count, ["types"] = types,
                ["note"] = "The majority variant is the reference; which one is correct is not decided: deviations are escalated.",
            };
        }

        /// <summary>Sorted local signature: type|x|y|rotation of each member, in the instance frame (quantized to tolFt and 1°).</summary>
        public static string[] Signature(GroupInstance g, double tolFt)
        {
            var c = Math.Cos(-g.RotDeg * Math.PI / 180); var s = Math.Sin(-g.RotDeg * Math.PI / 180);
            return g.Members.Select(m =>
            {
                double dx = m.X - g.Ox, dy = m.Y - g.Oy;
                double lx = c * dx - s * dy, ly = s * dx + c * dy;
                var lr = m.RotDeg - g.RotDeg;
                if (g.Mirrored) { lx = -lx; lr = 180 - lr; }
                lr = ((Math.Round(lr) % 360) + 360) % 360;
                return m.Type + "|" + Q(lx, tolFt) + "|" + Q(ly, tolFt) + "|" + lr.ToString(CultureInfo.InvariantCulture);
            }).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        private static string Q(double v, double tol) => (Math.Round(v / tol) * tol).ToString("0.####", CultureInfo.InvariantCulture);

        private static List<string> MultisetMinus(IEnumerable<string> a, IEnumerable<string> b)
        {
            var bag = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var r = new List<string>();
            foreach (var x in a) { if (bag.TryGetValue(x, out var n) && n > 0) bag[x] = n - 1; else r.Add(x); }
            return r;
        }
    }
}
