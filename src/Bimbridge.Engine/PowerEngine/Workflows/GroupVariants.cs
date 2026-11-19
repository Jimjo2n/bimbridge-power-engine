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
    /// Grupos y variantes: lleva cada instancia a coordenadas locales (deshace origen, giro y espejo), calcula la firma de sus
    /// miembros y agrupa instancias idénticas. La variante mayoritaria de cada tipo de grupo es la referencia; el resto se informa
    /// con lo que falta y lo que sobra. Da además cobertura por tipo de grupo y nivel.
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
                        ["instancias"] = variants[k].Select(g => (object)g.Id).ToList(),
                        ["faltan"] = MultisetMinus(refSig, vs).Cast<object>().ToList(),
                        ["sobran"] = MultisetMinus(vs, refSig).Cast<object>().ToList(),
                    });
                }
                types.Add(new Dictionary<string, object>
                {
                    ["tipo_grupo"] = t.Key, ["instancias"] = (long)t.Count(), ["variantes"] = (long)variants.Count,
                    ["referencia"] = new Dictionary<string, object> { ["instancias"] = (long)variants[0].Count(), ["miembros"] = (long)refSig.Length },
                    ["desviaciones"] = deviations,
                    ["espejadas"] = t.Where(g => g.Mirrored).Select(g => (object)g.Id).ToList(),
                    ["por_nivel"] = t.GroupBy(g => g.Level ?? "?").OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => (object)(long)l.Count()),
                });
            }
            return new Dictionary<string, object>
            {
                ["grupos"] = (long)groups.Count, ["tipos"] = types,
                ["nota"] = "La variante mayoritaria es la referencia; no se decide cuál es la correcta: las desviaciones se escalan.",
            };
        }

        /// <summary>Firma local ordenada: tipo|x|y|giro de cada miembro, en el marco de la instancia (cuantizado a tolFt y 1°).</summary>
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
