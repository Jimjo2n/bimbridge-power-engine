// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace PDI.RevitMcp.Engine.Geo
{
    /// <summary>
    /// Acumula los triángulos de un elemento (coordenadas del anfitrión, pies). El orden a→b→c debe dar la normal
    /// HACIA AFUERA del sólido (regla de la mano derecha); el add-in la corrige con la normal de la cara de Revit.
    /// Las caras verticales se descartan: una vertical nunca las cruza, y así la malla pesa la mitad.
    /// </summary>
    public sealed class TriMeshBuilder
    {
        /// <summary>|nz| / |n| por debajo de esto = cara vertical (se descarta).</summary>
        public const double VerticalTolerance = 1e-6;

        private double[] _v = new double[9 * 64];
        private sbyte[] _s = new sbyte[64];
        private int _n;

        public int Count => _n;
        public int SkippedVertical { get; private set; }
        public int SkippedDegenerate { get; private set; }

        public void Add(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(len > 1e-12)) { SkippedDegenerate++; return; }
            if (Math.Abs(nz) < VerticalTolerance * len) { SkippedVertical++; return; }
            if (_n == _s.Length)
            {
                Array.Resize(ref _s, _n * 2);
                Array.Resize(ref _v, _n * 2 * 9);
            }
            var o = _n * 9;
            _v[o] = ax; _v[o + 1] = ay; _v[o + 2] = az;
            _v[o + 3] = bx; _v[o + 4] = by; _v[o + 5] = bz;
            _v[o + 6] = cx; _v[o + 7] = cy; _v[o + 8] = cz;
            // Normal hacia afuera con z > 0 = cara que mira hacia arriba: por ahí SALE una vertical que sube.
            _s[_n] = nz > 0 ? (sbyte)1 : (sbyte)-1;
            _n++;
        }

        public TriMesh Build() => TriMesh.Create(_v, _s, _n);

        /// <summary>
        /// La misma geometría girada en planta <paramref name="cos"/>/<paramref name="sin"/> alrededor de (ox, oy) y desplazada (tx, ty, tz).
        /// Sirve para medir dónde quedaría una familia que todavía no existe (place con montaje) sin crearla en Revit.
        /// Un giro en planta no cambia qué caras miran hacia arriba: los signos se conservan.
        /// </summary>
        public TriMesh BuildMoved(double ox, double oy, double cos, double sin, double tx, double ty, double tz)
        {
            var v = new double[_n * 9];
            for (var k = 0; k < _n * 3; k++)
            {
                var o = k * 3;
                double x = _v[o] - ox, y = _v[o + 1] - oy;
                v[o] = ox + x * cos - y * sin + tx;
                v[o + 1] = oy + x * sin + y * cos + ty;
                v[o + 2] = _v[o + 2] + tz;
            }
            return TriMesh.Create(v, _s, _n);
        }
    }

    /// <summary>Resultado de lanzar una vertical contra una malla.</summary>
    public enum RayResult
    {
        /// <summary>La vertical no toca el elemento.</summary>
        Miss = 0,
        Ok = 1,
        /// <summary>Entradas y salidas no cuadran ni moviendo el punto una millonésima de pie (arista o sólido abierto).</summary>
        Inconsistent = 2,
    }

    /// <summary>Memoria de trabajo por hilo (sin asignaciones por rayo).</summary>
    public sealed class RayScratch
    {
        internal double[] Z = new double[32];
        internal sbyte[] S = new sbyte[32];
        internal int N;
        internal readonly int[] Stack = new int[128];
        internal readonly List<double> Lo = new List<double>(8);
        internal readonly List<double> Hi = new List<double>(8);

        internal void Add(double z, sbyte s)
        {
            if (N == Z.Length)
            {
                Array.Resize(ref Z, N * 2);
                Array.Resize(ref S, N * 2);
            }
            Z[N] = z; S[N] = s; N++;
        }
    }

    /// <summary>
    /// Malla inmutable de un elemento para rayos verticales, pensada para correr FUERA del hilo de Revit y en paralelo.
    ///
    /// Por qué: pyRevit (y la versión 0.2) llamaban Solid.IntersectWithCurve en el hilo de Revit por cada punto y cada sólido
    /// candidato: 1442 luminarias × 9 puntos × ~5 sólidos ≈ 65 000 llamadas a la API (0,05–0,5 ms cada una) con Revit congelado.
    /// Aquí cada elemento se triangula UNA vez (en el hilo de Revit, por tramos) y luego cada rayo cuesta ~1 µs en cualquier hilo.
    ///
    /// Estructura: triángulos no verticales en arreglos paralelos (SoA) agrupados en hojas de 4 dentro de un BVH 2D (XY).
    /// Cada hoja de 4 triángulos se prueba con una sola instrucción AVX2 en .NET 8 (Revit 2025–2026) y con un bucle escalar
    /// en .NET Framework 4.8 (Revit 2022–2024). Ambos caminos hacen exactamente las mismas operaciones: mismos resultados.
    ///
    /// Robustez: prueba de "punto dentro del triángulo" inclusiva (tolerancia 1e-9 pies) y orientación por la normal hacia afuera:
    /// cara que mira abajo = entrada, cara que mira arriba = salida. Golpes del mismo signo a la misma altura (arista compartida)
    /// cuentan una vez; entrada y salida a la misma altura (rozar una silueta) se anulan. Si aun así no cuadra, se mueve el punto
    /// 1e-6 pies y se repite; si tampoco, se informa Inconsistent y ese punto no se usa (nunca se inventa una altura).
    /// </summary>
    public sealed class TriMesh
    {
        public const int Lanes = 4;

        /// <summary>
        /// .NET 8 (Revit 2025–2026) compila primero sin optimizar y optimiza después de ~30 llamadas: el núcleo de rayos con AVX2 sería
        /// lento justo en la primera revisión. AggressiveOptimization lo compila optimizado desde la primera llamada.
        /// En .NET Framework (2022–2024) no hay compilación por niveles: siempre se optimiza y la marca no hace falta.
        /// </summary>
#if NET8_0_OR_GREATER
        internal const System.Runtime.CompilerServices.MethodImplOptions Hot = System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization;
#else
        internal const System.Runtime.CompilerServices.MethodImplOptions Hot = 0;
#endif
        /// <summary>Tolerancia de borde (pies) para "dentro del triángulo".</summary>
        public const double EdgeTolerance = 1e-9;
        /// <summary>Golpes a menos de esto (pies) se consideran a la misma altura.</summary>
        public const double SameZ = 1e-7;
        private const double JitterX = 1.3e-6, JitterY = 0.7e-6;

        /// <summary>Usar AVX2 cuando el procesador lo tiene (sólo .NET 8). Las pruebas lo apagan para comparar con el camino escalar.</summary>
        public static bool SimdEnabled = true;

        // Hojas: 4 carriles por hoja. Arista i: d = Ux*py - Uy*px + K (distancia con signo; triángulo en sentido antihorario).
        internal double[] U0x, U0y, K0, U1x, U1y, K1, U2x, U2y, K2;
        // Plano: z = A x + B y + C. Sg: +1 salida (cara hacia arriba), -1 entrada, 0 carril vacío.
        internal double[] PA, PB, PC, Sg;
        // Nodos del BVH (XY).
        internal double[] NMinX, NMinY, NMaxX, NMaxY;
        internal int[] NLeft, NRight, NLeaf;

        public int Triangles { get; private set; }
        public int Leaves { get; private set; }
        public Box3 Bounds { get; private set; }
        public bool IsEmpty => Triangles == 0;

        /// <summary>Memoria aproximada (bytes) para el informe del índice.</summary>
        public long Bytes => (long)Leaves * Lanes * 13 * 8 + (NMinX?.Length ?? 0) * (4 * 8 + 3 * 4);

        public static readonly TriMesh Empty = Create(new double[0], new sbyte[0], 0);

        public static bool SimdAvailable
        {
            get
            {
#if NET8_0_OR_GREATER
                return Vector256.IsHardwareAccelerated;
#else
                return false;
#endif
            }
        }

        internal static TriMesh Create(double[] v, sbyte[] s, int n)
        {
            var m = new TriMesh { Triangles = n };
            if (n == 0)
            {
                m.Bounds = Box3.Empty;
                m.NMinX = new double[0]; m.NMinY = new double[0]; m.NMaxX = new double[0]; m.NMaxY = new double[0];
                m.NLeft = new int[0]; m.NRight = new int[0]; m.NLeaf = new int[0];
                m.Alloc(0);
                return m;
            }
            var minX = new double[n]; var minY = new double[n]; var maxX = new double[n]; var maxY = new double[n];
            var cx = new double[n]; var cy = new double[n];
            var bounds = Box3.Empty;
            for (var t = 0; t < n; t++)
            {
                var o = t * 9;
                minX[t] = Math.Min(v[o], Math.Min(v[o + 3], v[o + 6]));
                maxX[t] = Math.Max(v[o], Math.Max(v[o + 3], v[o + 6]));
                minY[t] = Math.Min(v[o + 1], Math.Min(v[o + 4], v[o + 7]));
                maxY[t] = Math.Max(v[o + 1], Math.Max(v[o + 4], v[o + 7]));
                cx[t] = (v[o] + v[o + 3] + v[o + 6]) / 3;
                cy[t] = (v[o + 1] + v[o + 4] + v[o + 7]) / 3;
                bounds = bounds.Include(v[o], v[o + 1], v[o + 2]).Include(v[o + 3], v[o + 4], v[o + 5]).Include(v[o + 6], v[o + 7], v[o + 8]);
            }
            m.Bounds = bounds;

            // BVH por mediana del eje más largo; hojas de hasta 4 triángulos.
            var order = new int[n];
            for (var i = 0; i < n; i++) order[i] = i;
            var maxNodes = 2 * ((n + Lanes - 1) / Lanes) + 1;
            var nodes = new NodeBuilder(maxNodes * 2);
            var leafTris = new List<int[]>((n + Lanes - 1) / Lanes);
            BuildNode(order, 0, n, minX, minY, maxX, maxY, cx, cy, nodes, leafTris);

            m.NMinX = nodes.MinX.AsSpanCopy(nodes.Count); m.NMinY = nodes.MinY.AsSpanCopy(nodes.Count);
            m.NMaxX = nodes.MaxX.AsSpanCopy(nodes.Count); m.NMaxY = nodes.MaxY.AsSpanCopy(nodes.Count);
            m.NLeft = nodes.Left.AsSpanCopy(nodes.Count); m.NRight = nodes.Right.AsSpanCopy(nodes.Count); m.NLeaf = nodes.Leaf.AsSpanCopy(nodes.Count);

            m.Alloc(leafTris.Count);
            for (var leaf = 0; leaf < leafTris.Count; leaf++)
            {
                var tris = leafTris[leaf];
                for (var lane = 0; lane < Lanes; lane++)
                {
                    var k = leaf * Lanes + lane;
                    if (lane >= tris.Length) { m.SetDummy(k); continue; }
                    m.SetTriangle(k, v, tris[lane] * 9, s[tris[lane]]);
                }
            }
            return m;
        }

        private void Alloc(int leaves)
        {
            Leaves = leaves;
            var len = leaves * Lanes;
            U0x = new double[len]; U0y = new double[len]; K0 = new double[len];
            U1x = new double[len]; U1y = new double[len]; K1 = new double[len];
            U2x = new double[len]; U2y = new double[len]; K2 = new double[len];
            PA = new double[len]; PB = new double[len]; PC = new double[len]; Sg = new double[len];
        }

        private void SetDummy(int k)
        {
            // Aristas nulas con K = -1: la distancia sale -1 para cualquier punto → nunca "dentro".
            U0x[k] = U0y[k] = U1x[k] = U1y[k] = U2x[k] = U2y[k] = 0;
            K0[k] = K1[k] = K2[k] = -1;
            PA[k] = PB[k] = PC[k] = 0;
            Sg[k] = 0;
        }

        private void SetTriangle(int k, double[] v, int o, sbyte sign)
        {
            double ax = v[o], ay = v[o + 1], az = v[o + 2];
            double bx = v[o + 3], by = v[o + 4], bz = v[o + 5];
            double cx = v[o + 6], cy = v[o + 7], cz = v[o + 8];
            // Plano con la normal de la terna tal cual (el signo de entrada/salida ya está en "sign").
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            PA[k] = -nx / nz;
            PB[k] = -ny / nz;
            PC[k] = az - PA[k] * ax - PB[k] * ay;
            Sg[k] = sign;
            // Prueba en planta siempre en sentido antihorario.
            if (nz < 0) { var tx = bx; bx = cx; cx = tx; var ty = by; by = cy; cy = ty; }
            Edge(ax, ay, bx, by, out U0x[k], out U0y[k], out K0[k]);
            Edge(bx, by, cx, cy, out U1x[k], out U1y[k], out K1[k]);
            Edge(cx, cy, ax, ay, out U2x[k], out U2y[k], out K2[k]);
        }

        private static void Edge(double x0, double y0, double x1, double y1, out double ux, out double uy, out double k)
        {
            var dx = x1 - x0; var dy = y1 - y0;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-300) { ux = 0; uy = 0; k = 0; return; }
            ux = dx / len; uy = dy / len;
            // d(p) = ux*(py - y0) - uy*(px - x0) = ux*py - uy*px + (uy*x0 - ux*y0)
            k = uy * x0 - ux * y0;
        }

        private sealed class NodeBuilder
        {
            public double[] MinX, MinY, MaxX, MaxY;
            public int[] Left, Right, Leaf;
            public int Count;
            public NodeBuilder(int cap)
            {
                MinX = new double[cap]; MinY = new double[cap]; MaxX = new double[cap]; MaxY = new double[cap];
                Left = new int[cap]; Right = new int[cap]; Leaf = new int[cap];
            }
            public int New()
            {
                if (Count == MinX.Length)
                {
                    var c = Count * 2;
                    Array.Resize(ref MinX, c); Array.Resize(ref MinY, c); Array.Resize(ref MaxX, c); Array.Resize(ref MaxY, c);
                    Array.Resize(ref Left, c); Array.Resize(ref Right, c); Array.Resize(ref Leaf, c);
                }
                return Count++;
            }
        }

        private static int BuildNode(int[] order, int lo, int hi, double[] minX, double[] minY, double[] maxX, double[] maxY,
            double[] cx, double[] cy, NodeBuilder nb, List<int[]> leafTris)
        {
            var node = nb.New();
            double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
            double cx0 = double.MaxValue, cy0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue;
            for (var i = lo; i < hi; i++)
            {
                var t = order[i];
                if (minX[t] < bx0) bx0 = minX[t]; if (maxX[t] > bx1) bx1 = maxX[t];
                if (minY[t] < by0) by0 = minY[t]; if (maxY[t] > by1) by1 = maxY[t];
                if (cx[t] < cx0) cx0 = cx[t]; if (cx[t] > cx1) cx1 = cx[t];
                if (cy[t] < cy0) cy0 = cy[t]; if (cy[t] > cy1) cy1 = cy[t];
            }
            nb.MinX[node] = bx0; nb.MinY[node] = by0; nb.MaxX[node] = bx1; nb.MaxY[node] = by1;
            if (hi - lo <= Lanes)
            {
                var tris = new int[hi - lo];
                Array.Copy(order, lo, tris, 0, hi - lo);
                nb.Leaf[node] = leafTris.Count;
                nb.Left[node] = nb.Right[node] = -1;
                leafTris.Add(tris);
                return node;
            }
            // División por la mediana de los centroides en el eje más largo (selección parcial, O(n) por nivel).
            var byX = (cx1 - cx0) >= (cy1 - cy0);
            var key = byX ? cx : cy;
            var mid = (lo + hi) / 2;
            NthElement(order, lo, hi - 1, mid, key);
            nb.Leaf[node] = -1;
            var l = BuildNode(order, lo, mid, minX, minY, maxX, maxY, cx, cy, nb, leafTris);
            var r = BuildNode(order, mid, hi, minX, minY, maxX, maxY, cx, cy, nb, leafTris);
            nb.Left[node] = l;
            nb.Right[node] = r;
            return node;
        }

        /// <summary>Quickselect: deja en order[k] el elemento k-ésimo según key, menores a la izquierda.</summary>
        private static void NthElement(int[] order, int lo, int hi, int k, double[] key)
        {
            while (hi > lo)
            {
                var pivot = key[order[(lo + hi) >> 1]];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (key[order[i]] < pivot) i++;
                    while (key[order[j]] > pivot) j--;
                    if (i <= j)
                    {
                        var t = order[i]; order[i] = order[j]; order[j] = t;
                        i++; j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;
            }
        }

        // ------------------------------------------------------------------ consultas

        /// <summary>Junta en <paramref name="sc"/> los golpes (z, signo) de la vertical que pasa por (px, py).</summary>
        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        internal void Probe(double px, double py, RayScratch sc)
        {
            sc.N = 0;
            if (Triangles == 0) return;
            var b = Bounds;
            if (px < b.MinX - EdgeTolerance || px > b.MaxX + EdgeTolerance || py < b.MinY - EdgeTolerance || py > b.MaxY + EdgeTolerance) return;
            var stack = sc.Stack;
            var sp = 0;
            stack[sp++] = 0;
#if NET8_0_OR_GREATER
            var simd = SimdEnabled && Vector256.IsHardwareAccelerated;
#endif
            while (sp > 0)
            {
                var n = stack[--sp];
                if (px < NMinX[n] - EdgeTolerance || px > NMaxX[n] + EdgeTolerance || py < NMinY[n] - EdgeTolerance || py > NMaxY[n] + EdgeTolerance)
                    continue;
                var leaf = NLeaf[n];
                if (leaf >= 0)
                {
#if NET8_0_OR_GREATER
                    if (simd) { LeafSimd(leaf * Lanes, px, py, sc); continue; }
#endif
                    LeafScalar(leaf * Lanes, px, py, sc);
                    continue;
                }
                if (sp + 2 > stack.Length) { LeafFallback(n, px, py, sc); continue; }
                stack[sp++] = NLeft[n];
                stack[sp++] = NRight[n];
            }
        }

        /// <summary>Árbol más profundo que la pila (no pasa con mallas reales): recorre recursivamente.</summary>
        private void LeafFallback(int n, double px, double py, RayScratch sc)
        {
            if (px < NMinX[n] - EdgeTolerance || px > NMaxX[n] + EdgeTolerance || py < NMinY[n] - EdgeTolerance || py > NMaxY[n] + EdgeTolerance)
                return;
            if (NLeaf[n] >= 0) { LeafScalar(NLeaf[n] * Lanes, px, py, sc); return; }
            LeafFallback(NLeft[n], px, py, sc);
            LeafFallback(NRight[n], px, py, sc);
        }

        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        private void LeafScalar(int k0, double px, double py, RayScratch sc)
        {
            for (var k = k0; k < k0 + Lanes; k++)
            {
                if ((U0x[k] * py - U0y[k] * px) + K0[k] < -EdgeTolerance) continue;
                if ((U1x[k] * py - U1y[k] * px) + K1[k] < -EdgeTolerance) continue;
                if ((U2x[k] * py - U2y[k] * px) + K2[k] < -EdgeTolerance) continue;
                sc.Add((PA[k] * px + PB[k] * py) + PC[k], (sbyte)Sg[k]);
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>Las 4 pruebas de una hoja en una sola pasada AVX2 (Revit 2025–2026, .NET 8).</summary>
        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        private void LeafSimd(int k0, double px, double py, RayScratch sc)
        {
            var vx = Vector256.Create(px);
            var vy = Vector256.Create(py);
            var lim = Vector256.Create(-EdgeTolerance);
            var o = (nuint)k0;
            var d0 = (Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U0x), o) * vy
                      - Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U0y), o) * vx)
                     + Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(K0), o);
            var d1 = (Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U1x), o) * vy
                      - Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U1y), o) * vx)
                     + Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(K1), o);
            var d2 = (Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U2x), o) * vy
                      - Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U2y), o) * vx)
                     + Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(K2), o);
            var inside = Vector256.GreaterThanOrEqual(d0, lim) & Vector256.GreaterThanOrEqual(d1, lim) & Vector256.GreaterThanOrEqual(d2, lim);
            var bits = inside.ExtractMostSignificantBits();
            while (bits != 0)
            {
                var lane = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var k = k0 + lane;
                sc.Add((PA[k] * px + PB[k] * py) + PC[k], (sbyte)Sg[k]);
            }
        }
#endif

        /// <summary>
        /// Tramos de la vertical (x, y) que quedan dentro del elemento y tocan [z0, z1]. Se agregan a <paramref name="into"/>
        /// sin recortar (ZIn/ZOut son las caras reales). Mismo significado que Solid.IntersectWithCurve(CurveSegmentsInside).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        public RayResult Segments(double x, double y, double z0, double z1, int item, List<Segment> into, RayScratch sc)
        {
            if (Triangles == 0) return RayResult.Miss;
            Probe(x, y, sc);
            if (sc.N == 0) return RayResult.Miss;
            if (!Resolve(sc))
            {
                Probe(x + JitterX, y + JitterY, sc);
                if (sc.N == 0) return RayResult.Miss;
                if (!Resolve(sc)) return RayResult.Inconsistent;
            }
            var any = false;
            for (var i = 0; i < sc.Lo.Count; i++)
            {
                var lo = sc.Lo[i]; var hi = sc.Hi[i];
                if (hi < z0 || lo > z1) continue;
                into.Add(new Segment(lo, hi, item));
                any = true;
            }
            return any ? RayResult.Ok : RayResult.Miss;
        }

        /// <summary>Altura inferior y superior del propio cuerpo en (x, y): para medir la cara superior real de una luminaria.</summary>
        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        public RayResult Extent(double x, double y, RayScratch sc, out double zBot, out double zTop)
        {
            zBot = double.NaN; zTop = double.NaN;
            if (Triangles == 0) return RayResult.Miss;
            Probe(x, y, sc);
            if (sc.N == 0) return RayResult.Miss;
            if (!Resolve(sc))
            {
                Probe(x + JitterX, y + JitterY, sc);
                if (sc.N == 0) return RayResult.Miss;
                if (!Resolve(sc)) return RayResult.Inconsistent;
            }
            if (sc.Lo.Count == 0) return RayResult.Miss;
            zBot = double.MaxValue; zTop = double.MinValue;
            for (var i = 0; i < sc.Lo.Count; i++)
            {
                if (sc.Lo[i] < zBot) zBot = sc.Lo[i];
                if (sc.Hi[i] > zTop) zTop = sc.Hi[i];
            }
            return RayResult.Ok;
        }

        /// <summary>
        /// Ordena los golpes y los convierte en tramos [entrada, salida] con la profundidad (número de sólidos que contienen el punto).
        /// Devuelve false si no cuadran (profundidad negativa o que no vuelve a 0).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(Hot)]
        internal static bool Resolve(RayScratch sc)
        {
            sc.Lo.Clear(); sc.Hi.Clear();
            var n = sc.N;
            var z = sc.Z; var s = sc.S;
            // Inserción: casi siempre hay 2 a 6 golpes.
            for (var i = 1; i < n; i++)
            {
                var zi = z[i]; var si = s[i];
                var j = i - 1;
                while (j >= 0 && z[j] > zi) { z[j + 1] = z[j]; s[j + 1] = s[j]; j--; }
                z[j + 1] = zi; s[j + 1] = si;
            }
            var depth = 0;
            var start = 0.0;
            var r = 0;
            while (r < n)
            {
                // Racha de golpes a la misma altura.
                var hasIn = false; var hasOut = false;
                var zr = z[r];
                var e = r;
                while (e < n && z[e] - zr <= SameZ)
                {
                    if (s[e] < 0) hasIn = true; else if (s[e] > 0) hasOut = true;
                    e++;
                }
                var zMid = z[r];
                r = e;
                if (hasIn && hasOut) continue; // rozar una silueta o tocar entre dos sólidos: no cambia nada
                if (hasIn)
                {
                    if (depth == 0) start = zMid;
                    depth++;
                }
                else if (hasOut)
                {
                    depth--;
                    if (depth < 0) return false;
                    if (depth == 0 && zMid - start > SameZ) { sc.Lo.Add(start); sc.Hi.Add(zMid); }
                }
            }
            return depth == 0;
        }
    }

    internal static class ArrayExt
    {
        public static T[] AsSpanCopy<T>(this T[] a, int count)
        {
            var r = new T[count];
            Array.Copy(a, r, count);
            return r;
        }
    }

    /// <summary>
    /// Un cuerpo del índice de geometría, sin nada de la API de Revit: id, de qué modelo viene, caja y malla (coordenadas del anfitrión).
    /// La malla se llena sólo cuando algo lo necesita. Una vez llena no cambia: se lee desde varios hilos a la vez.
    /// </summary>
    public sealed class GeoBody
    {
        public long Id;
        /// <summary>0 = anfitrión; si no, id de la instancia del vínculo.</summary>
        public long LinkId;
        public string Source;
        public string Category;
        public string Label;
        public Box3 Box;
        public volatile TriMesh Mesh;
        public bool MeshFailed;
        /// <summary>Revisión del anfitrión cuando se extrajo la malla (los vínculos no cambian mientras siguen cargados).</summary>
        public long MeshRev;
        /// <summary>Muros: eje en planta como polilínea [x0,y0,x1,y1,…] (coordenadas del anfitrión) y espesor.</summary>
        public double[] Line;
        public double Width;
        /// <summary>Caja del elemento tal como la dio Revit al extraer la malla (si cambia, la malla se rehace).</summary>
        public Box3 RawBox = Box3.Empty;
        /// <summary>Subcategorías excluidas con que se extrajo la malla ("" = ninguna).</summary>
        public string MeshKey = "";
        /// <summary>Subcategorías de los sólidos del cuerpo (p. ej. "Lighting Fixtures", "JBox").</summary>
        public string[] Parts;
    }
}
