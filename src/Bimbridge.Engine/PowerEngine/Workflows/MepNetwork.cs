// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>Conduit (or pipe/tray) run with its ends and, per end, the id of the PHYSICALLY connected element (Revit connector).</summary>
    public sealed class RunSegment
    {
        public string Id;
        public double Ax, Ay, Az, Bx, By, Bz;
        public double DiameterFt;
        public string System;
        /// <summary>Id of the element connected at A and at B according to the connectors; null = free connector.</summary>
        public string ConnA, ConnB;
    }

    /// <summary>
    /// MEP network in three layers that are NEVER mixed:
    /// 1) proximity: ends close together (possible gap or drafting error) — only a hint;
    /// 2) topological continuity: coincident ends within a strict tolerance — continuous geometry;
    /// 3) physical connection: linked Revit connectors — the only thing that proves the network is connected.
    /// Discrepancies between layers are the useful result: "touches without connecting", "connected with a gap", open ends,
    /// different diameters or systems in the same physical connection.
    /// </summary>
    public static class MepNetwork
    {
        public static Dictionary<string, object> Analyze(IList<RunSegment> segs, double contactTolFt = 1.0 / 192.0, double proximityTolFt = 0.5, PowerEngineDispatcher engine = null, int maxListed = 50)
        {
            var n = segs.Count;
            var ids = segs.Select(s => s.Id).ToList();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < n; i++)
            {
                if (string.IsNullOrEmpty(ids[i]) || index.ContainsKey(ids[i])) throw new ArgumentException("empty or duplicate run ids: " + ids[i]);
                index[ids[i]] = i;
            }
            // ends: node 2i = A, 2i+1 = B
            var pts = new double[n * 2 * 3];
            for (var i = 0; i < n; i++)
            {
                var s = segs[i];
                pts[6 * i] = s.Ax; pts[6 * i + 1] = s.Ay; pts[6 * i + 2] = s.Az; pts[6 * i + 3] = s.Bx; pts[6 * i + 4] = s.By; pts[6 * i + 5] = s.Bz;
            }
            Func<double[], int, double, int[]> dup = (p, d, t) => engine != null ? engine.Duplicates(p, d, t) : GeometryKernels.Duplicates(p, d, t);
            Func<int, int[], (int[] Component, int[] Degree)> comps = (nn, e) => engine != null ? engine.Components(nn, e) : GeometryKernels.Components(nn, e);

            // layer 2: continuity (coincident ends within contactTol)
            var cont = n > 0 ? dup(pts, 3, contactTolFt) : new int[0];
            var contEdges = new List<int>();
            for (var i = 0; i < n; i++) { contEdges.Add(2 * i); contEdges.Add(2 * i + 1); }   // each run joins its two ends
            for (var k = 0; k < 2 * n; k++) if (cont[k] != k) { contEdges.Add(k); contEdges.Add(cont[k]); }
            var contComp = comps(2 * n, contEdges.ToArray());

            // layer 3: physical connection (connectors)
            var physEdges = new List<int>();
            var physPairs = new List<(int A, int B)>();
            var unknownConn = new List<object>();
            var openEnds = new List<object>();
            for (var i = 0; i < n; i++) { physEdges.Add(2 * i); physEdges.Add(2 * i + 1); }
            for (var i = 0; i < n; i++)
                foreach (var (end, conn) in new[] { (0, segs[i].ConnA), (1, segs[i].ConnB) })
                {
                    if (conn == null) { openEnds.Add(new Dictionary<string, object> { ["segment"] = ids[i], ["end"] = end == 0 ? "A" : "B" }); continue; }
                    if (!index.TryGetValue(conn, out var j)) { unknownConn.Add(new Dictionary<string, object> { ["segment"] = ids[i], ["connected_to"] = conn }); continue; }  // fitting/equipment outside the set
                    // which end of j? the one closest to this end
                    var ex = Pt(segs[i], end);
                    var ja = Dist(ex, Pt(segs[j], 0)); var jb = Dist(ex, Pt(segs[j], 1));
                    var nodeJ = 2 * j + (ja <= jb ? 0 : 1);
                    physEdges.Add(2 * i + end); physEdges.Add(nodeJ);
                    if (i < j) physPairs.Add((2 * i + end, nodeJ));
                }
            var physComp = comps(2 * n, physEdges.ToArray());

            var touchNoConn = new List<object>();
            var connGap = new List<object>();
            var mismatch = new List<object>();
            var physSet = new HashSet<(int, int)>(physPairs.Select(p => (Math.Min(p.A, p.B), Math.Max(p.A, p.B))));
            for (var k = 0; k < 2 * n; k++)
            {
                if (cont[k] == k) continue;
                var a = Math.Min(k, cont[k]); var b = Math.Max(k, cont[k]);
                if (a / 2 == b / 2) continue;
                if (physComp.Component[a] != physComp.Component[b])
                    touchNoConn.Add(new Dictionary<string, object> { ["segments"] = new List<object> { ids[a / 2], ids[b / 2] }, ["note"] = "coincident ends without a linked connector" });
            }
            foreach (var (a, b) in physPairs)
            {
                var d = Dist(Pt(segs[a / 2], a % 2), Pt(segs[b / 2], b % 2));
                if (d > contactTolFt) connGap.Add(new Dictionary<string, object> { ["segments"] = new List<object> { ids[a / 2], ids[b / 2] }, ["gap_ft"] = Math.Round(d, 5) });
                var sa = segs[a / 2]; var sb = segs[b / 2];
                if (Math.Abs(sa.DiameterFt - sb.DiameterFt) > 1e-6) mismatch.Add(new Dictionary<string, object> { ["segments"] = new List<object> { sa.Id, sb.Id }, ["reason"] = "diameter " + In(sa.DiameterFt) + " ≠ " + In(sb.DiameterFt) });
                if (!string.Equals(sa.System, sb.System, StringComparison.Ordinal)) mismatch.Add(new Dictionary<string, object> { ["segments"] = new List<object> { sa.Id, sb.Id }, ["reason"] = "system " + sa.System + " ≠ " + sb.System });
            }

            // layer 1: proximity (free ends within proximityTol of another end, with no continuity or connection)
            var near = new List<object>();
            var groupSize = new int[2 * n];
            for (var k = 0; k < 2 * n; k++) groupSize[cont[k]]++;
            var free = Enumerable.Range(0, 2 * n).Where(k => (k % 2 == 0 ? segs[k / 2].ConnA : segs[k / 2].ConnB) == null && groupSize[cont[k]] == 1).ToList();
            if (free.Count > 1)
            {
                var fp = new double[free.Count * 3];
                for (var q = 0; q < free.Count; q++) { var p = Pt(segs[free[q] / 2], free[q] % 2); fp[3 * q] = p.X; fp[3 * q + 1] = p.Y; fp[3 * q + 2] = p.Z; }
                var g = dup(fp, 3, proximityTolFt);
                foreach (var grp in Enumerable.Range(0, free.Count).GroupBy(q => g[q]).Where(x => x.Count() > 1))
                {
                    var members = grp.Select(q => free[q]).ToList();
                    if (members.Select(k => k / 2).Distinct().Count() < 2) continue;
                    var d = Dist(Pt(segs[members[0] / 2], members[0] % 2), Pt(segs[members[1] / 2], members[1] % 2));
                    near.Add(new Dictionary<string, object> { ["segments"] = members.Select(k => (object)ids[k / 2]).Distinct().ToList(), ["distance_ft"] = Math.Round(d, 4), ["note"] = "proximity is not connection: check whether a fitting is missing" });
                }
            }

            int Count(int[] c) => c.Distinct().Count();
            return new Dictionary<string, object>
            {
                ["segments"] = (long)n,
                ["layers"] = new Dictionary<string, object>
                {
                    ["proximity"] = new Dictionary<string, object> { ["tolerance_ft"] = proximityTolFt, ["pairs"] = (long)near.Count },
                    ["continuity"] = new Dictionary<string, object> { ["tolerance_ft"] = contactTolFt, ["components"] = (long)Count(contComp.Component) },
                    ["physical_connection"] = new Dictionary<string, object> { ["components"] = (long)Count(physComp.Component), ["open_ends"] = (long)openEnds.Count },
                },
                ["touches_unconnected"] = touchNoConn.Take(maxListed).ToList(),
                ["connected_with_gap"] = connGap.Take(maxListed).ToList(),
                ["incompatible_items"] = mismatch.Take(maxListed).ToList(),
                ["near_unconnected"] = near.Take(maxListed).ToList(),
                ["open_ends"] = openEnds.Take(maxListed).ToList(),
                ["connected_outside_set"] = unknownConn.Take(maxListed).ToList(),
                ["verdict"] = touchNoConn.Count + connGap.Count + mismatch.Count == 0 ? "no_layer_discrepancies" : "discrepancies",
                ["note"] = "Only the physical connection layer proves electrical/mechanical continuity; the others are geometry.",
            };
        }

        private static (double X, double Y, double Z) Pt(RunSegment s, int end) => end == 0 ? (s.Ax, s.Ay, s.Az) : (s.Bx, s.By, s.Bz);
        private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        private static string In(double ft) => (ft * 12).ToString("0.###", CultureInfo.InvariantCulture) + "\"";
    }
}
