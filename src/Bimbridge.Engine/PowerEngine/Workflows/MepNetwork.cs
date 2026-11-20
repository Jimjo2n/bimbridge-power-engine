// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BimBridge.PowerEngine.Workflows
{
    /// <summary>Tramo de conduit (o tubería/bandeja) con sus extremos y, por extremo, el id del elemento conectado FÍSICAMENTE (conector de Revit).</summary>
    public sealed class RunSegment
    {
        public string Id;
        public double Ax, Ay, Az, Bx, By, Bz;
        public double DiameterFt;
        public string System;
        /// <summary>Id del elemento conectado en A y en B según los conectores; null = conector libre.</summary>
        public string ConnA, ConnB;
    }

    /// <summary>
    /// Red MEP en tres capas que NUNCA se mezclan:
    /// 1) proximidad: extremos cerca (posible hueco o error de dibujo) — sólo una pista;
    /// 2) continuidad topológica: extremos coincidentes dentro de una tolerancia estricta — geometría continua;
    /// 3) conexión física: conectores de Revit enlazados — lo único que prueba que la red está conectada.
    /// Las discrepancias entre capas son el resultado útil: "toca sin conectar", "conectado con hueco", extremos abiertos,
    /// diámetros o sistemas distintos en una misma conexión física.
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
                if (string.IsNullOrEmpty(ids[i]) || index.ContainsKey(ids[i])) throw new ArgumentException("ids de tramo vacíos o repetidos: " + ids[i]);
                index[ids[i]] = i;
            }
            // extremos: nodo 2i = A, 2i+1 = B
            var pts = new double[n * 2 * 3];
            for (var i = 0; i < n; i++)
            {
                var s = segs[i];
                pts[6 * i] = s.Ax; pts[6 * i + 1] = s.Ay; pts[6 * i + 2] = s.Az; pts[6 * i + 3] = s.Bx; pts[6 * i + 4] = s.By; pts[6 * i + 5] = s.Bz;
            }
            Func<double[], int, double, int[]> dup = (p, d, t) => engine != null ? engine.Duplicates(p, d, t) : GeometryKernels.Duplicates(p, d, t);
            Func<int, int[], (int[] Component, int[] Degree)> comps = (nn, e) => engine != null ? engine.Components(nn, e) : GeometryKernels.Components(nn, e);

            // capa 2: continuidad (extremos coincidentes dentro de contactTol)
            var cont = n > 0 ? dup(pts, 3, contactTolFt) : new int[0];
            var contEdges = new List<int>();
            for (var i = 0; i < n; i++) { contEdges.Add(2 * i); contEdges.Add(2 * i + 1); }   // cada tramo une sus dos extremos
            for (var k = 0; k < 2 * n; k++) if (cont[k] != k) { contEdges.Add(k); contEdges.Add(cont[k]); }
            var contComp = comps(2 * n, contEdges.ToArray());

            // capa 3: conexión física (conectores)
            var physEdges = new List<int>();
            var physPairs = new List<(int A, int B)>();
            var unknownConn = new List<object>();
            var openEnds = new List<object>();
            for (var i = 0; i < n; i++) { physEdges.Add(2 * i); physEdges.Add(2 * i + 1); }
            for (var i = 0; i < n; i++)
                foreach (var (end, conn) in new[] { (0, segs[i].ConnA), (1, segs[i].ConnB) })
                {
                    if (conn == null) { openEnds.Add(new Dictionary<string, object> { ["tramo"] = ids[i], ["extremo"] = end == 0 ? "A" : "B" }); continue; }
                    if (!index.TryGetValue(conn, out var j)) { unknownConn.Add(new Dictionary<string, object> { ["tramo"] = ids[i], ["conectado_a"] = conn }); continue; }  // accesorio/equipo fuera del conjunto
                    // ¿qué extremo de j? el más cercano a este extremo
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
                    touchNoConn.Add(new Dictionary<string, object> { ["tramos"] = new List<object> { ids[a / 2], ids[b / 2] }, ["nota"] = "extremos coincidentes sin conector enlazado" });
            }
            foreach (var (a, b) in physPairs)
            {
                var d = Dist(Pt(segs[a / 2], a % 2), Pt(segs[b / 2], b % 2));
                if (d > contactTolFt) connGap.Add(new Dictionary<string, object> { ["tramos"] = new List<object> { ids[a / 2], ids[b / 2] }, ["hueco_ft"] = Math.Round(d, 5) });
                var sa = segs[a / 2]; var sb = segs[b / 2];
                if (Math.Abs(sa.DiameterFt - sb.DiameterFt) > 1e-6) mismatch.Add(new Dictionary<string, object> { ["tramos"] = new List<object> { sa.Id, sb.Id }, ["motivo"] = "diámetro " + In(sa.DiameterFt) + " ≠ " + In(sb.DiameterFt) });
                if (!string.Equals(sa.System, sb.System, StringComparison.Ordinal)) mismatch.Add(new Dictionary<string, object> { ["tramos"] = new List<object> { sa.Id, sb.Id }, ["motivo"] = "sistema " + sa.System + " ≠ " + sb.System });
            }

            // capa 1: proximidad (extremos libres a menos de proximityTol de otro extremo, sin continuidad ni conexión)
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
                    near.Add(new Dictionary<string, object> { ["tramos"] = members.Select(k => (object)ids[k / 2]).Distinct().ToList(), ["distancia_ft"] = Math.Round(d, 4), ["nota"] = "proximidad no es conexión: revisar si falta un accesorio" });
                }
            }

            int Count(int[] c) => c.Distinct().Count();
            return new Dictionary<string, object>
            {
                ["tramos"] = (long)n,
                ["capas"] = new Dictionary<string, object>
                {
                    ["proximidad"] = new Dictionary<string, object> { ["tolerancia_ft"] = proximityTolFt, ["pares"] = (long)near.Count },
                    ["continuidad"] = new Dictionary<string, object> { ["tolerancia_ft"] = contactTolFt, ["componentes"] = (long)Count(contComp.Component) },
                    ["conexion_fisica"] = new Dictionary<string, object> { ["componentes"] = (long)Count(physComp.Component), ["extremos_abiertos"] = (long)openEnds.Count },
                },
                ["toca_sin_conectar"] = touchNoConn.Take(maxListed).ToList(),
                ["conectado_con_hueco"] = connGap.Take(maxListed).ToList(),
                ["incompatibles"] = mismatch.Take(maxListed).ToList(),
                ["cerca_sin_conectar"] = near.Take(maxListed).ToList(),
                ["extremos_abiertos"] = openEnds.Take(maxListed).ToList(),
                ["conectado_fuera_del_conjunto"] = unknownConn.Take(maxListed).ToList(),
                ["veredicto"] = touchNoConn.Count + connGap.Count + mismatch.Count == 0 ? "sin_discrepancias_entre_capas" : "discrepancias",
                ["nota"] = "Sólo la capa de conexión física prueba continuidad eléctrica/mecánica; las otras son geometría.",
            };
        }

        private static (double X, double Y, double Z) Pt(RunSegment s, int end) => end == 0 ? (s.Ax, s.Ay, s.Az) : (s.Bx, s.By, s.Bz);
        private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        private static string In(double ft) => (ft * 12).ToString("0.###", CultureInfo.InvariantCulture) + "\"";
    }
}
