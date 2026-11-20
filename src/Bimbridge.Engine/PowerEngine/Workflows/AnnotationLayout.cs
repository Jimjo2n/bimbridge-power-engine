// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    public sealed class TagRequest { public string Id; public double X, Y; public double W = 0.6, H = 0.15; }   // anchor and size, in paper INCHES
    public sealed class Box { public string Id; public double X0, Y0, X1, Y1; public bool Overlaps(Box b, double gap = 0) => X0 < b.X1 + gap && b.X0 < X1 + gap && Y0 < b.Y1 + gap && b.Y0 < Y1 + gap; }

    /// <summary>
    /// Tag layout at the view scale (everything in paper inches): for each anchor it tries 8 positions around it
    /// and then farther positions (with leader), and picks the lowest-cost one: no collision with obstacles (geometry, other
    /// tags), close to the anchor, aligned with its neighbors. Checks legibility (text height on paper). It is a PROPOSAL:
    /// native tag placement follows the "tag"/"tag_move" operation with its verification.
    /// </summary>
    public static class AnnotationLayout
    {
        public const double MinTextHeightIn = 3.0 / 32.0;   // 3/32" on paper

        public static Dictionary<string, object> Layout(IList<TagRequest> tags, IList<Box> obstacles, double textHeightIn = 3.0 / 32.0, double offsetIn = 0.12, double leaderFromIn = 0.35, double gapIn = 0.02, int repairPasses = 4)
        {
            var obs = (obstacles ?? new List<Box>()).ToList();
            // stable order: top to bottom and left to right (alignments propagate)
            var order = tags.OrderByDescending(t => Math.Round(t.Y, 2)).ThenBy(t => t.X).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
            var pos = new Box[order.Count];
            var lead = new bool[order.Count];
            double Cost(int i, Box b, out bool leader)
            {
                var t = order[i];
                double cx = (b.X0 + b.X1) / 2, cy = (b.Y0 + b.Y1) / 2;
                var hits = obs.Count(o => o.Overlaps(b, gapIn));
                var align = false;
                for (var j = 0; j < pos.Length; j++)
                {
                    if (j == i || pos[j] == null) continue;
                    if (pos[j].Overlaps(b, gapIn)) hits++;
                    if (Math.Abs((pos[j].Y0 + pos[j].Y1) / 2 - cy) < 0.01 || Math.Abs(pos[j].X0 - b.X0) < 0.01) align = true;
                }
                var dist = Math.Sqrt((cx - t.X) * (cx - t.X) + (cy - t.Y) * (cy - t.Y));
                leader = dist - Math.Max(t.W, t.H) / 2 > leaderFromIn;
                return hits * 100 + dist + (align ? -0.05 : 0) + (leader ? 0.2 : 0);
            }
            IEnumerable<Box> Candidates(TagRequest t)
            {
                foreach (var ring in new[] { 1.0, 2.0, 3.5, 5.0 })
                    for (var k = 0; k < 16; k++)
                    {
                        var ang = k * Math.PI / 8;
                        double cx = t.X + Math.Cos(ang) * (offsetIn * ring + t.W / 2), cy = t.Y + Math.Sin(ang) * (offsetIn * ring + t.H / 2);
                        yield return new Box { Id = t.Id, X0 = cx - t.W / 2, X1 = cx + t.W / 2, Y0 = cy - t.H / 2, Y1 = cy + t.H / 2 };
                    }
            }
            void Place(int i)
            {
                Box best = null; var bc = double.MaxValue; var bl = false;
                foreach (var b in Candidates(order[i])) { var c = Cost(i, b, out var l); if (c < bc - 1e-12) { bc = c; best = b; bl = l; } }
                pos[i] = best; lead[i] = bl;
            }
            bool Collides(int i) => obs.Any(o => o.Overlaps(pos[i], gapIn)) || Enumerable.Range(0, pos.Length).Any(j => j != i && pos[j] != null && pos[j].Overlaps(pos[i], gapIn));
            for (var i = 0; i < order.Count; i++) Place(i);
            // repair: colliding tags are re-placed considering all the others (deterministic local search)
            for (var pass = 0; pass < repairPasses; pass++)
            {
                var bad = Enumerable.Range(0, order.Count).Where(Collides).ToList();
                if (bad.Count == 0) break;
                foreach (var i in bad) { pos[i] = null; Place(i); }
            }
            var outTags = new List<object>();
            var collisions = 0; var leaders = 0;
            for (var i = 0; i < order.Count; i++)
            {
                var t = order[i]; var b = pos[i];
                var collide = Collides(i);
                if (collide) collisions++;
                if (lead[i]) leaders++;
                outTags.Add(new Dictionary<string, object>
                {
                    ["id"] = t.Id, ["centro_in"] = new List<object> { R((b.X0 + b.X1) / 2), R((b.Y0 + b.Y1) / 2) },
                    ["offset_in"] = new List<object> { R((b.X0 + b.X1) / 2 - t.X), R((b.Y0 + b.Y1) / 2 - t.Y) },
                    ["leader"] = lead[i], ["colision"] = collide,
                });
            }
            return new Dictionary<string, object>
            {
                ["etiquetas"] = outTags, ["colisiones"] = (long)collisions, ["con_leader"] = (long)leaders,
                ["legible"] = textHeightIn + 1e-9 >= MinTextHeightIn,
                ["altura_texto_in"] = R(textHeightIn),
                ["nota"] = "Proposal in paper inches (offset = what tag/tag_move uses). Placing and verifying in Revit remains NATIVE_TEST_PENDING.",
            };
        }

        private static object R(double v) => Math.Round(v, 4);
    }
}
