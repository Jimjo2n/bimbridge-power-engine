// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PDI.RevitMcp.Engine.Geo;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Kernel candidato para una futura comparación C# ↔ Rust (P2 §8): "extensión vertical de N puntos contra una malla de triángulos"
    /// (TriMesh.Extent del motor 0.3+, usado por el QC de montaje). Cumple las condiciones:
    /// - no usa objetos de Revit: entra un buffer binario determinista (triángulos + puntos), sale un buffer de resultados;
    /// - resultado verificable: digest sha256 de (resultado, zBot, zTop) redondeados a 1e-9 ft — Rust debe dar el mismo digest;
    /// - medible: construcción del índice, consulta (mediana de rondas), bytes asignados, serialización y tamaño de datos.
    /// Formato de entrada (little endian): int32 nTri, nTri×9 float64, int32 nPts, nPts×2 float64. Semilla fija.
    /// </summary>
    public static class MountKernelBench
    {
        public static (double[] Tri, double[] Pts) Generate(int triangles, int points, int seed = 20261026)
        {
            var rng = new Random(seed);
            var boxes = Math.Max(1, triangles / 4);
            var side = (int)Math.Ceiling(Math.Sqrt(boxes));
            var tri = new List<double>(boxes * 36);
            for (var b = 0; b < boxes; b++)
            {
                double x0 = b % side * 2.0, y0 = b / side * 2.0, x1 = x0 + 1.8, y1 = y0 + 1.8;
                var zb = 8 + rng.NextDouble() * 4;            // cara inferior de losa (ft)
                var zt = zb + 0.5 + rng.NextDouble() * 0.5;   // cara superior, algo inclinada
                var s = (rng.NextDouble() - 0.5) * 0.05;
                double Top(double x, double y) => zt + s * (x - x0);
                // superior (normal +z)
                tri.AddRange(new[] { x0, y0, Top(x0, y0), x1, y0, Top(x1, y0), x1, y1, Top(x1, y1) });
                tri.AddRange(new[] { x0, y0, Top(x0, y0), x1, y1, Top(x1, y1), x0, y1, Top(x0, y1) });
                // inferior (normal −z)
                tri.AddRange(new[] { x0, y0, zb, x1, y1, zb, x1, y0, zb });
                tri.AddRange(new[] { x0, y0, zb, x0, y1, zb, x1, y1, zb });
            }
            var pts = new double[points * 2];
            for (var i = 0; i < points; i++) { pts[2 * i] = rng.NextDouble() * side * 2.0; pts[2 * i + 1] = rng.NextDouble() * side * 2.0; }
            return (tri.ToArray(), pts);
        }

        public static byte[] Serialize(double[] tri, double[] pts)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(tri.Length / 9); foreach (var d in tri) w.Write(d);
                w.Write(pts.Length / 2); foreach (var d in pts) w.Write(d);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static (double[] Tri, double[] Pts) Deserialize(byte[] b)
        {
            using (var r = new BinaryReader(new MemoryStream(b)))
            {
                var t = new double[r.ReadInt32() * 9]; for (var i = 0; i < t.Length; i++) t[i] = r.ReadDouble();
                var p = new double[r.ReadInt32() * 2]; for (var i = 0; i < p.Length; i++) p[i] = r.ReadDouble();
                return (t, p);
            }
        }

        /// <summary>El kernel: índice + consulta. Devuelve (resultado, zBot, zTop) por punto.</summary>
        public static (byte[] Res, double[] Z) Compute(double[] tri, double[] pts, out double buildMs)
        {
            var sw = Stopwatch.StartNew();
            var b = new TriMeshBuilder();
            for (var i = 0; i < tri.Length; i += 9) b.Add(tri[i], tri[i + 1], tri[i + 2], tri[i + 3], tri[i + 4], tri[i + 5], tri[i + 6], tri[i + 7], tri[i + 8]);
            var mesh = b.Build();
            buildMs = sw.Elapsed.TotalMilliseconds;
            var n = pts.Length / 2;
            var res = new byte[n]; var z = new double[n * 2];
            var sc = new RayScratch();
            for (var i = 0; i < n; i++)
            {
                res[i] = (byte)mesh.Extent(pts[2 * i], pts[2 * i + 1], sc, out var lo, out var hi);
                z[2 * i] = lo; z[2 * i + 1] = hi;
            }
            return (res, z);
        }

        public static string Digest(byte[] res, double[] z)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                for (var i = 0; i < res.Length; i++)
                {
                    w.Write(res[i]);
                    w.Write(double.IsNaN(z[2 * i]) ? long.MinValue : (long)Math.Round(z[2 * i] * 1e9));
                    w.Write(double.IsNaN(z[2 * i + 1]) ? long.MinValue : (long)Math.Round(z[2 * i + 1] * 1e9));
                }
                w.Flush();
                using (var s = SHA256.Create()) return string.Concat(s.ComputeHash(ms.ToArray()).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        public static Dictionary<string, object> Run(int points, int triangles, int rounds)
        {
            var (tri0, pts0) = Generate(triangles, points);
            var sw = Stopwatch.StartNew();
            var bytes = Serialize(tri0, pts0);
            var serMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var (tri, pts) = Deserialize(bytes);
            var deserMs = sw.Elapsed.TotalMilliseconds;
            Compute(tri, pts, out _); // calentamiento (JIT)
            var build = new List<double>(); var query = new List<double>(); var alloc = new List<long>();
            string digest = null; var hits = 0;
            for (var r = 0; r < Math.Max(1, rounds); r++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var (res, z) = Compute(tri, pts, out var bms);
                var total = sw.Elapsed.TotalMilliseconds;
                alloc.Add(GC.GetAllocatedBytesForCurrentThread() - a0);
                build.Add(bms); query.Add(total - bms);
                var d = Digest(res, z);
                if (digest != null && d != digest) throw new InvalidOperationException("el kernel no es determinista");
                digest = d; hits = res.Count(x => x == 1);
            }
            double Med(List<double> l) { var s = l.OrderBy(x => x).ToList(); return Math.Round(s[s.Count / 2], 3); }
            return new Dictionary<string, object>
            {
                ["kernel"] = "TriMesh.Extent (montaje vertical)", ["lenguaje"] = "C# " + Environment.Version, ["simd"] = TriMesh.SimdAvailable && TriMesh.SimdEnabled,
                ["triangulos"] = (long)(tri.Length / 9), ["puntos"] = (long)(pts.Length / 2), ["aciertos"] = (long)hits, ["rondas"] = (long)rounds,
                ["entrada_bytes"] = (long)bytes.Length, ["serializar_ms"] = Math.Round(serMs, 3), ["deserializar_ms"] = Math.Round(deserMs, 3),
                ["indice_ms_mediana"] = Med(build), ["consulta_ms_mediana"] = Med(query),
                ["consulta_ns_por_punto"] = Math.Round(Med(query) * 1e6 / Math.Max(1, pts.Length / 2), 1),
                ["asignado_bytes_mediana"] = alloc.OrderBy(x => x).ElementAt(alloc.Count / 2),
                ["memoria_proceso_mb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1),
                ["digest"] = digest, ["sha256_entrada"] = RequirementExtractor.Sha256Hex(bytes),
            };
        }
    }
}
