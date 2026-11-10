// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Threading;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Power Engine — núcleos geométricos de referencia en C#. Operan sobre instantáneas inmutables en búferes planos
    /// (x,y[,z] consecutivos), igual que el Rust Geometry Engine, para comparar resultados exactos y tiempos con los mismos datos.
    /// Son también la ruta por defecto cuando el volumen es pequeño (el coste de transferir a Rust no compensa) o Rust no está.
    /// Unidades: las del llamador (pies internos de Revit en BIMBRIDGE). Ninguna función toca Revit.
    /// </summary>
    public static class GeometryKernels
    {
        /// <summary>La cancelación se consulta por trabajo hecho (puntos visitados), igual que Rust (WORK_CHECK).</summary>
        public const int WorkCheck = 1 << 16;

        // ---------------- transformaciones ----------------

        /// <summary>
        /// Aplica una transformación afín 3D (matriz 3×4 por filas: [a b c tx; d e f ty; g h i tz]) a n puntos xyz.
        /// Sirve para vínculos (Transform de un RevitLinkInstance) y para registro plano→modelo (z = 0).
        /// </summary>
        public static double[] Transform3(double[] xyz, double[] m, CancellationToken ct = default(CancellationToken))
        {
            if (m == null || m.Length != 12) throw new ArgumentException("matriz 3×4 (12 valores)");
            CheckFinite(m, "matriz");
            var n = xyz.Length / 3;
            var o = new double[n * 3];
            for (var i = 0; i < n; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                double x = xyz[3 * i], y = xyz[3 * i + 1], z = xyz[3 * i + 2];
                o[3 * i] = m[0] * x + m[1] * y + m[2] * z + m[3];
                o[3 * i + 1] = m[4] * x + m[5] * y + m[6] * z + m[7];
                o[3 * i + 2] = m[8] * x + m[9] * y + m[10] * z + m[11];
            }
            return o;
        }

        /// <summary>Composición de dos matrices 3×4 (primero b, luego a): a∘b.</summary>
        public static double[] Compose(double[] a, double[] b)
        {
            var r = new double[12];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++) r[4 * i + j] = a[4 * i] * b[j] + a[4 * i + 1] * b[4 + j] + a[4 * i + 2] * b[8 + j];
                r[4 * i + 3] = a[4 * i] * b[3] + a[4 * i + 1] * b[7] + a[4 * i + 2] * b[11] + a[4 * i + 3];
            }
            return r;
        }

        public static void CheckFinite(double[] v, string what)
        {
            for (var i = 0; i < v.Length; i++) if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) throw new ArgumentException(what + ": valor no finito en la posición " + i);
        }

        // ---------------- índice espacial (rejilla uniforme ordenada) ----------------

        /// <summary>
        /// Rejilla uniforme 2D/3D compacta: celdas ordenadas por clave con índices contiguos (sin diccionarios en la búsqueda caliente).
        /// Construcción O(n log n), consulta por vecindad 3×3(×3).
        /// </summary>
        public sealed class Grid
        {
            public readonly int Dim;
            public readonly double Cell;
            private readonly double[] _p;
            private readonly long[] _keys;     // clave de celda ordenada
            private readonly int[] _idx;       // índice del punto, en el orden de _keys
            private readonly double _ox, _oy, _oz;

            public Grid(double[] pts, int dim, double cell)
            {
                if (dim != 2 && dim != 3) throw new ArgumentException("dim 2 o 3");
                if (!(cell > 0) || double.IsInfinity(cell)) throw new ArgumentException("tamaño de celda > 0");
                Dim = dim; Cell = cell; _p = pts;
                var n = pts.Length / dim;
                double ox = double.MaxValue, oy = double.MaxValue, oz = double.MaxValue;
                for (var i = 0; i < n; i++) { ox = Math.Min(ox, pts[dim * i]); oy = Math.Min(oy, pts[dim * i + 1]); if (dim == 3) oz = Math.Min(oz, pts[dim * i + 2]); }
                _ox = n == 0 ? 0 : ox; _oy = n == 0 ? 0 : oy; _oz = n == 0 || dim == 2 ? 0 : oz;
                _keys = new long[n]; _idx = new int[n];
                for (var i = 0; i < n; i++) { _keys[i] = Key(pts[dim * i], pts[dim * i + 1], dim == 3 ? pts[dim * i + 2] : 0); _idx[i] = i; }
                Array.Sort(_keys, _idx);
            }

            private long CellOf(double v, double o) => (long)Math.Floor((v - o) / Cell);
            private static long Pack(long cx, long cy, long cz) => ((cx + (1L << 20)) << 42) | ((cy + (1L << 20)) << 21) | (cz + (1L << 20));
            private long Key(double x, double y, double z) => Pack(CellOf(x, _ox), CellOf(y, _oy), Dim == 3 ? CellOf(z, _oz) : 0);

            /// <summary>Llama a <paramref name="visit"/> con cada punto de las celdas vecinas (incluida la propia) de (x,y,z).</summary>
            public int Near(double x, double y, double z, Action<int> visit)
            {
                var visited = 0;
                long cx = CellOf(x, _ox), cy = CellOf(y, _oy), cz = Dim == 3 ? CellOf(z, _oz) : 0;
                var zr = Dim == 3 ? 1 : 0;
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -zr; dz <= zr; dz++)
                        {
                            var k = Pack(cx + dx, cy + dy, cz + dz);
                            var lo = LowerBound(k);
                            for (var t = lo; t < _keys.Length && _keys[t] == k; t++) { visit(_idx[t]); visited++; }
                        }
                return visited;
            }

            private int LowerBound(long k)
            {
                int lo = 0, hi = _keys.Length;
                while (lo < hi) { var mid = (lo + hi) >> 1; if (_keys[mid] < k) lo = mid + 1; else hi = mid; }
                return lo;
            }

            public double Dist2(int i, double x, double y, double z)
            {
                var dx = _p[Dim * i] - x; var dy = _p[Dim * i + 1] - y; var dz = Dim == 3 ? _p[Dim * i + 2] - z : 0;
                return dx * dx + dy * dy + dz * dz;
            }
        }

        // ---------------- vecino más cercano ----------------

        /// <summary>
        /// Para cada punto de consulta, el índice del punto de referencia más cercano dentro de <paramref name="maxDist"/> (−1 si
        /// no hay) y su distancia. Rejilla de celda = maxDist: exacto dentro del radio.
        /// </summary>
        public static (int[] Index, double[] Dist) Nearest(double[] query, double[] reference, int dim, double maxDist, CancellationToken ct = default(CancellationToken))
        {
            CheckFinite(query, "consulta"); CheckFinite(reference, "referencia");
            var g = new Grid(reference, dim, maxDist);
            var n = query.Length / dim;
            var idx = new int[n]; var dist = new double[n];
            var max2 = maxDist * maxDist;
            var work = WorkCheck;
            for (var i = 0; i < n; i++)
            {
                if (work >= WorkCheck) { ct.ThrowIfCancellationRequested(); work = 0; }
                double x = query[dim * i], y = query[dim * i + 1], z = dim == 3 ? query[dim * i + 2] : 0;
                var best = -1; var bd = double.MaxValue;
                work += 1 + g.Near(x, y, z, j => { var d = g.Dist2(j, x, y, z); if (d <= max2 && (d < bd || (d == bd && j < best))) { bd = d; best = j; } });
                idx[i] = best; dist[i] = best < 0 ? double.NaN : Math.Sqrt(bd);
            }
            return (idx, dist);
        }

        // ---------------- emparejamiento uno a uno ----------------

        /// <summary>
        /// Emparejamiento uno a uno esperado↔observado por distancia creciente (voraz global, determinista): cada par candidato a
        /// menos de <paramref name="tol"/> se ordena por (distancia, i, j) y se acepta si ninguno de los dos está usado.
        /// Devuelve, por cada esperado, el observado asignado o −1. Es la base de "omitidas / desplazadas / adicionales".
        /// </summary>
        public static int[] Match(double[] expected, double[] observed, int dim, double tol, CancellationToken ct = default(CancellationToken))
        {
            CheckFinite(expected, "esperados"); CheckFinite(observed, "observados");
            var g = new Grid(observed, dim, tol);
            var ne = expected.Length / dim; var no = observed.Length / dim;
            var cand = new List<(double D, int I, int J)>();
            var t2 = tol * tol;
            var work = WorkCheck;
            for (var i = 0; i < ne; i++)
            {
                if (work >= WorkCheck) { ct.ThrowIfCancellationRequested(); work = 0; }
                double x = expected[dim * i], y = expected[dim * i + 1], z = dim == 3 ? expected[dim * i + 2] : 0;
                var ii = i;
                work += 1 + g.Near(x, y, z, j => { var d = g.Dist2(j, x, y, z); if (d <= t2) cand.Add((d, ii, j)); });
            }
            cand.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.I != b.I ? a.I.CompareTo(b.I) : a.J.CompareTo(b.J));
            var res = new int[ne]; for (var i = 0; i < ne; i++) res[i] = -1;
            var usedO = new bool[no];
            foreach (var c in cand) if (res[c.I] < 0 && !usedO[c.J]) { res[c.I] = c.J; usedO[c.J] = true; }
            return res;
        }

        // ---------------- duplicados ----------------

        /// <summary>Grupos de puntos a menos de <paramref name="tol"/> entre sí (cierre transitivo). Devuelve un id de grupo por punto (el menor índice del grupo).</summary>
        public static int[] Duplicates(double[] pts, int dim, double tol, CancellationToken ct = default(CancellationToken))
        {
            CheckFinite(pts, "puntos");
            var n = pts.Length / dim;
            var uf = new UnionFind(n);
            var g = new Grid(pts, dim, tol);
            var t2 = tol * tol;
            var work = WorkCheck;
            for (var i = 0; i < n; i++)
            {
                if (work >= WorkCheck) { ct.ThrowIfCancellationRequested(); work = 0; }
                double x = pts[dim * i], y = pts[dim * i + 1], z = dim == 3 ? pts[dim * i + 2] : 0;
                var ii = i;
                work += 1 + g.Near(x, y, z, j => { if (j > ii && g.Dist2(j, x, y, z) <= t2) uf.Union(ii, j); });
            }
            var r = new int[n];
            for (var i = 0; i < n; i++) r[i] = uf.Find(i);
            return r;
        }

        public sealed class UnionFind
        {
            private readonly int[] _p;
            public UnionFind(int n) { _p = new int[n]; for (var i = 0; i < n; i++) _p[i] = i; }
            public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
            /// <summary>La raíz es siempre el menor índice (resultado determinista, igual en Rust).</summary>
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (a < b) _p[b] = a; else _p[a] = b; }
        }

        // ---------------- intersecciones de segmentos 2D ----------------

        public const int ContactCross = 1, ContactTouch = 2, ContactOverlap = 3;

        /// <summary>
        /// Todas las parejas de segmentos 2D (x1,y1,x2,y2 por segmento) que se cruzan, se tocan o se superponen, con su tipo.
        /// Rejilla por cajas (celda = longitud media). Pares ordenados (i&lt;j), sin repetir. Misma clasificación que Draw2D.
        /// </summary>
        public static List<(int I, int J, int Kind)> SegmentContacts(double[] segs, double eps = 1e-7, CancellationToken ct = default(CancellationToken))
        {
            CheckFinite(segs, "segmentos");
            var n = segs.Length / 4;
            var res = new List<(int, int, int)>();
            if (n < 2) return res;
            double total = 0;
            for (var i = 0; i < n; i++) total += Math.Sqrt(Sq(segs[4 * i + 2] - segs[4 * i]) + Sq(segs[4 * i + 3] - segs[4 * i + 1]));
            var cell = Math.Max(total / n, 1e-6);
            double ox = double.MaxValue, oy = double.MaxValue;
            for (var i = 0; i < n; i++) { ox = Math.Min(ox, Math.Min(segs[4 * i], segs[4 * i + 2])); oy = Math.Min(oy, Math.Min(segs[4 * i + 1], segs[4 * i + 3])); }
            var buckets = new Dictionary<long, List<int>>();
            for (var i = 0; i < n; i++)
            {
                long x0 = (long)Math.Floor((Math.Min(segs[4 * i], segs[4 * i + 2]) - ox) / cell - 1e-9), x1 = (long)Math.Floor((Math.Max(segs[4 * i], segs[4 * i + 2]) - ox) / cell + 1e-9);
                long y0 = (long)Math.Floor((Math.Min(segs[4 * i + 1], segs[4 * i + 3]) - oy) / cell - 1e-9), y1 = (long)Math.Floor((Math.Max(segs[4 * i + 1], segs[4 * i + 3]) - oy) / cell + 1e-9);
                for (var cx = x0; cx <= x1; cx++)
                    for (var cy = y0; cy <= y1; cy++)
                    {
                        var k = (cx << 32) ^ (cy & 0xFFFFFFFFL);
                        if (!buckets.TryGetValue(k, out var l)) buckets[k] = l = new List<int>();
                        l.Add(i);
                    }
            }
            var seen = new HashSet<long>();
            var count = 0;
            foreach (var l in buckets.Values)
            {
                if ((count++ & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                for (var a = 0; a < l.Count; a++)
                    for (var b = a + 1; b < l.Count; b++)
                    {
                        int i = Math.Min(l[a], l[b]), j = Math.Max(l[a], l[b]);
                        if (!seen.Add(((long)i << 32) | (uint)j)) continue;
                        var kind = Classify(segs, i, j, eps);
                        if (kind != 0) res.Add((i, j, kind));
                    }
            }
            res.Sort((p, q) => p.Item1 != q.Item1 ? p.Item1.CompareTo(q.Item1) : p.Item2.CompareTo(q.Item2));
            return res;
        }

        private static double Sq(double v) => v * v;

        /// <summary>0 ninguno, 1 cruce, 2 contacto en un punto, 3 superposición colineal.</summary>
        public static int Classify(double[] s, int i, int j, double eps)
        {
            double p1x = s[4 * i], p1y = s[4 * i + 1], p2x = s[4 * i + 2], p2y = s[4 * i + 3];
            double q1x = s[4 * j], q1y = s[4 * j + 1], q2x = s[4 * j + 2], q2y = s[4 * j + 3];
            var lp = Math.Sqrt(Sq(p2x - p1x) + Sq(p2y - p1y)); var lq = Math.Sqrt(Sq(q2x - q1x) + Sq(q2y - q1y));
            if (lp < eps || lq < eps) return 0;   // degenerados: los descarta el validador, aquí no cuentan
            var scale = Math.Max(1.0, Math.Max(Math.Max(Math.Abs(p1x), Math.Abs(p1y)), Math.Max(Math.Abs(q1x), Math.Abs(q1y))));
            var tol = eps * scale;
            double Cr(double ox, double oy, double ax, double ay, double bx, double by) => (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
            double d1 = Cr(q1x, q1y, q2x, q2y, p1x, p1y) / lq, d2 = Cr(q1x, q1y, q2x, q2y, p2x, p2y) / lq;
            double d3 = Cr(p1x, p1y, p2x, p2y, q1x, q1y) / lp, d4 = Cr(p1x, p1y, p2x, p2y, q2x, q2y) / lp;
            bool z1 = Math.Abs(d1) <= tol, z2 = Math.Abs(d2) <= tol, z3 = Math.Abs(d3) <= tol, z4 = Math.Abs(d4) <= tol;
            if (z1 && z2)
            {
                double ux = (p2x - p1x) / lp, uy = (p2y - p1y) / lp;
                double b0 = (q1x - p1x) * ux + (q1y - p1y) * uy, b1 = (q2x - p1x) * ux + (q2y - p1y) * uy;
                if (b0 > b1) { var t = b0; b0 = b1; b1 = t; }
                var ov = Math.Min(lp, b1) - Math.Max(0, b0);
                return ov > tol ? ContactOverlap : ov >= -tol ? ContactTouch : 0;
            }
            if (((d1 > tol && d2 < -tol) || (d1 < -tol && d2 > tol)) && ((d3 > tol && d4 < -tol) || (d3 < -tol && d4 > tol))) return ContactCross;
            bool On(double ax, double ay, double bx, double by, double px, double py) =>
                px >= Math.Min(ax, bx) - tol && px <= Math.Max(ax, bx) + tol && py >= Math.Min(ay, by) - tol && py <= Math.Max(ay, by) + tol;
            if ((z1 && On(q1x, q1y, q2x, q2y, p1x, p1y)) || (z2 && On(q1x, q1y, q2x, q2y, p2x, p2y)) || (z3 && On(p1x, p1y, p2x, p2y, q1x, q1y)) || (z4 && On(p1x, p1y, p2x, p2y, q2x, q2y))) return ContactTouch;
            return 0;
        }

        // ---------------- grafos ----------------

        /// <summary>Componentes conexas de un grafo no dirigido (id = menor nodo del componente) y grado de cada nodo.</summary>
        public static (int[] Component, int[] Degree) Components(int nodes, int[] edges)
        {
            var uf = new UnionFind(nodes);
            var deg = new int[nodes];
            for (var e = 0; e + 1 < edges.Length; e += 2)
            {
                int a = edges[e], b = edges[e + 1];
                if (a < 0 || b < 0 || a >= nodes || b >= nodes) throw new ArgumentException("arista " + e / 2 + " con nodo fuera de rango");
                uf.Union(a, b); deg[a]++; if (b != a) deg[b]++;
            }
            var c = new int[nodes];
            for (var i = 0; i < nodes; i++) c[i] = uf.Find(i);
            return (c, deg);
        }
    }
}
