// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine.Geo;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Mallas para rayos verticales: deben dar lo mismo que Solid.IntersectWithCurve en los casos de obra.</summary>
    public class MeshTests : IDisposable
    {
        private readonly bool _simd = TriMesh.SimdEnabled;
        public void Dispose() => TriMesh.SimdEnabled = _simd;

        /// <summary>Caja con las 12 caras orientadas hacia afuera (las verticales se descartan solas).</summary>
        internal static void Box(TriMeshBuilder b, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            // abajo (normal -z) y arriba (+z)
            b.Add(x0, y0, z0, x0, y1, z0, x1, y1, z0);
            b.Add(x0, y0, z0, x1, y1, z0, x1, y0, z0);
            b.Add(x0, y0, z1, x1, y0, z1, x1, y1, z1);
            b.Add(x0, y0, z1, x1, y1, z1, x0, y1, z1);
            // lados (verticales)
            b.Add(x0, y0, z0, x1, y0, z0, x1, y0, z1);
            b.Add(x0, y0, z0, x1, y0, z1, x0, y0, z1);
            b.Add(x1, y1, z0, x0, y1, z0, x0, y1, z1);
            b.Add(x1, y1, z0, x0, y1, z1, x1, y1, z1);
        }

        /// <summary>Rampa: losa de espesor t cuya cara inferior sube de z0 (en x0) a z0+rise (en x1).</summary>
        internal static void Ramp(TriMeshBuilder b, double x0, double y0, double x1, double y1, double z0, double rise, double t)
        {
            double za = z0, zb = z0 + rise;
            b.Add(x0, y0, za, x0, y1, za, x1, y1, zb);
            b.Add(x0, y0, za, x1, y1, zb, x1, y0, zb);
            b.Add(x0, y0, za + t, x1, y0, zb + t, x1, y1, zb + t);
            b.Add(x0, y0, za + t, x1, y1, zb + t, x0, y1, za + t);
        }

        /// <summary>Losa 0..10 × 0..10 con un hueco 4..6 × 4..6, triangulada como marco (8 triángulos por cara).</summary>
        internal static void SlabWithHole(TriMeshBuilder b, double z0, double z1)
        {
            var outer = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0) };
            var inner = new[] { (4.0, 4.0), (6.0, 4.0), (6.0, 6.0), (4.0, 6.0) };
            for (var i = 0; i < 4; i++)
            {
                var o0 = outer[i]; var o1 = outer[(i + 1) % 4];
                var i0 = inner[i]; var i1 = inner[(i + 1) % 4];
                // arriba: antihorario visto desde arriba
                b.Add(o0.Item1, o0.Item2, z1, o1.Item1, o1.Item2, z1, i1.Item1, i1.Item2, z1);
                b.Add(o0.Item1, o0.Item2, z1, i1.Item1, i1.Item2, z1, i0.Item1, i0.Item2, z1);
                // abajo: horario
                b.Add(o0.Item1, o0.Item2, z0, i1.Item1, i1.Item2, z0, o1.Item1, o1.Item2, z0);
                b.Add(o0.Item1, o0.Item2, z0, i0.Item1, i0.Item2, z0, i1.Item1, i1.Item2, z0);
            }
        }

        private static List<Segment> Cast(TriMesh m, double x, double y, out RayResult r)
        {
            var list = new List<Segment>();
            r = m.Segments(x, y, -1000, 1000, 7, list, new RayScratch());
            return list;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Box_GivesBottomAndTop_AndMissesOutside(bool simd)
        {
            TriMesh.SimdEnabled = simd;
            var b = new TriMeshBuilder();
            Box(b, 0, 0, 10, 20, 30, 10.75);
            var m = b.Build();
            Assert.Equal(4, m.Triangles);
            Assert.Equal(4, b.SkippedVertical);

            var s = Cast(m, 5, 5, out var r);
            Assert.Equal(RayResult.Ok, r);
            Assert.Single(s);
            Assert.Equal(10, s[0].ZIn, 9);
            Assert.Equal(10.75, s[0].ZOut, 9);
            Assert.Equal(7, s[0].Item);

            Cast(m, 25, 5, out r);
            Assert.Equal(RayResult.Miss, r);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SharedEdgesAndCorners_CountOnce(bool simd)
        {
            TriMesh.SimdEnabled = simd;
            var b = new TriMeshBuilder();
            Box(b, 0, 0, 0, 10, 10, 1);
            var m = b.Build();
            // Diagonal compartida por los dos triángulos de cada cara, una esquina y una arista exterior.
            foreach (var (x, y) in new[] { (5.0, 5.0), (0.0, 0.0), (10.0, 10.0), (0.0, 5.0), (3.3, 3.3) })
            {
                var s = Cast(m, x, y, out var r);
                Assert.Equal(RayResult.Ok, r);
                Assert.Single(s);
                Assert.Equal(0, s[0].ZIn, 9);
                Assert.Equal(1, s[0].ZOut, 9);
            }
        }

        [Fact]
        public void SlabWithHole_MissesTheHole()
        {
            var b = new TriMeshBuilder();
            SlabWithHole(b, 20, 20.75);
            var m = b.Build();
            Cast(m, 5, 5, out var r);
            Assert.Equal(RayResult.Miss, r);
            var s = Cast(m, 2, 7, out r);
            Assert.Equal(RayResult.Ok, r);
            Assert.Equal(20, s[0].ZIn, 9);
            // Justo en el borde del hueco: toca la losa (inclusivo, como Revit con tolerancia).
            s = Cast(m, 4, 5, out r);
            Assert.Equal(RayResult.Ok, r);
        }

        [Fact]
        public void Ramp_Underside_FollowsSlope()
        {
            var b = new TriMeshBuilder();
            Ramp(b, 0, 0, 40, 10, 12, 2, 0.75);
            var m = b.Build();
            for (var x = 1.0; x < 40; x += 3.7)
            {
                var s = Cast(m, x, 5, out var r);
                Assert.Equal(RayResult.Ok, r);
                Assert.Equal(12 + 2 * x / 40, s[0].ZIn, 9);
                Assert.Equal(12 + 2 * x / 40 + 0.75, s[0].ZOut, 9);
            }
        }

        [Fact]
        public void OverlappingSolids_InOneElement_Union()
        {
            var b = new TriMeshBuilder();
            Box(b, 0, 0, 0, 10, 10, 2);
            Box(b, 2, 2, 1, 8, 8, 3);
            Box(b, 0, 0, 5, 10, 10, 6);
            var m = b.Build();
            var s = Cast(m, 5, 5, out var r);
            Assert.Equal(RayResult.Ok, r);
            Assert.Equal(2, s.Count);
            Assert.Equal(0, s[0].ZIn, 9); Assert.Equal(3, s[0].ZOut, 9);
            Assert.Equal(5, s[1].ZIn, 9); Assert.Equal(6, s[1].ZOut, 9);
        }

        [Fact]
        public void Wedge_SilhouetteGraze_IsNotAHit_RidgeIs()
        {
            // Prisma triangular a lo largo de Y: base z=0 de x=0 a 2, cumbrera en x=1, z=1.
            var b = new TriMeshBuilder();
            for (var k = 0; k < 1; k++)
            {
                double y0 = 0, y1 = 10;
                b.Add(0, y0, 0, 0, y1, 0, 2, y1, 0);   // base (abajo)
                b.Add(0, y0, 0, 2, y1, 0, 2, y0, 0);
                b.Add(0, y0, 0, 1, y0, 1, 1, y1, 1);   // faldón izquierdo (normal arriba-izquierda)
                b.Add(0, y0, 0, 1, y1, 1, 0, y1, 0);
                b.Add(2, y0, 0, 2, y1, 0, 1, y1, 1);   // faldón derecho (normal arriba-derecha)
                b.Add(2, y0, 0, 1, y1, 1, 1, y0, 1);
            }
            var m = b.Build();
            var s = Cast(m, 1, 5, out var r);
            Assert.Equal(RayResult.Ok, r);
            Assert.Single(s);
            Assert.Equal(1, s[0].ZOut, 9);
            Cast(m, 0, 5, out r);
            Assert.Equal(RayResult.Miss, r);
            s = Cast(m, 0.5, 5, out r);
            Assert.Equal(0.5, s[0].ZOut, 9);
        }

        [Fact]
        public void OpenShell_IsReportedInconsistent_NeverInvented()
        {
            var b = new TriMeshBuilder();
            b.Add(0, 0, 0, 0, 10, 0, 10, 10, 0);
            b.Add(0, 0, 0, 10, 10, 0, 10, 0, 0);
            var m = b.Build();
            Cast(m, 5, 2, out var r);
            Assert.Equal(RayResult.Inconsistent, r);
        }

        [Fact]
        public void Extent_GivesBodyTopAndBottom_AndMissesOutsideRoundBody()
        {
            // "Puck" octogonal de 1 pie de diámetro: las esquinas de su caja quedan fuera del cuerpo.
            var b = new TriMeshBuilder();
            const int n = 8;
            double r0 = 0.5, zb = 9.5, zt = 9.75;
            for (var i = 0; i < n; i++)
            {
                double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                double x0 = r0 * Math.Cos(a0), y0 = r0 * Math.Sin(a0), x1 = r0 * Math.Cos(a1), y1 = r0 * Math.Sin(a1);
                b.Add(0, 0, zt, x0, y0, zt, x1, y1, zt);
                b.Add(0, 0, zb, x1, y1, zb, x0, y0, zb);
            }
            var m = b.Build();
            var sc = new RayScratch();
            Assert.Equal(RayResult.Ok, m.Extent(0, 0.2, sc, out var bot, out var top));
            Assert.Equal(zb, bot, 9);
            Assert.Equal(zt, top, 9);
            Assert.Equal(RayResult.Miss, m.Extent(0.48, 0.48, sc, out _, out _));
        }

        [Fact]
        public void Simd_And_Scalar_GiveIdenticalResults_OnRandomScene()
        {
            var rnd = new Random(1234);
            var b = new TriMeshBuilder();
            for (var i = 0; i < 300; i++)
            {
                double x = rnd.NextDouble() * 200, y = rnd.NextDouble() * 200, z = rnd.NextDouble() * 50;
                double w = 0.5 + rnd.NextDouble() * 20, d = 0.5 + rnd.NextDouble() * 20, h = 0.2 + rnd.NextDouble() * 3;
                if (i % 3 == 0) Ramp(b, x, y, x + w, y + d, z, rnd.NextDouble() * 4 - 2, h);
                else Box(b, x, y, z, x + w, y + d, z + h);
            }
            var m = b.Build();
            var sc = new RayScratch();
            var mismatches = 0;
            for (var i = 0; i < 4000; i++)
            {
                double x = rnd.NextDouble() * 220, y = rnd.NextDouble() * 220;
                TriMesh.SimdEnabled = false;
                var a = new List<Segment>();
                var ra = m.Segments(x, y, -1e4, 1e4, 0, a, sc);
                TriMesh.SimdEnabled = true;
                var c = new List<Segment>();
                var rc = m.Segments(x, y, -1e4, 1e4, 0, c, sc);
                if (ra != rc || a.Count != c.Count) { mismatches++; continue; }
                for (var k = 0; k < a.Count; k++)
                    if (a[k].ZIn != c[k].ZIn || a[k].ZOut != c[k].ZOut) mismatches++;
            }
            Assert.Equal(0, mismatches);
        }

        [Fact]
        public void BruteForce_AgreesWithBvh()
        {
            // Referencia lenta: probar todos los triángulos sin BVH (lo que hacían los scripts de pyRevit con cada sólido).
            var rnd = new Random(99);
            var boxes = new List<double[]>();
            var b = new TriMeshBuilder();
            for (var i = 0; i < 120; i++)
            {
                double x = rnd.NextDouble() * 100, y = rnd.NextDouble() * 100, z = rnd.NextDouble() * 30;
                double w = 1 + rnd.NextDouble() * 15, d = 1 + rnd.NextDouble() * 15, h = 0.3 + rnd.NextDouble() * 2;
                Box(b, x, y, z, x + w, y + d, z + h);
                boxes.Add(new[] { x, y, z, x + w, y + d, z + h });
            }
            var m = b.Build();
            var sc = new RayScratch();
            for (var i = 0; i < 2000; i++)
            {
                double px = rnd.NextDouble() * 110, py = rnd.NextDouble() * 110;
                var got = new List<Segment>();
                m.Segments(px, py, -1e4, 1e4, 0, got, sc);
                // Unión de intervalos de las cajas que contienen el punto.
                var iv = boxes.Where(q => px >= q[0] && px <= q[3] && py >= q[1] && py <= q[4]).Select(q => (q[2], q[5])).OrderBy(t => t.Item1).ToList();
                var merged = new List<(double, double)>();
                foreach (var t in iv)
                {
                    if (merged.Count > 0 && t.Item1 <= merged[merged.Count - 1].Item2 + 1e-9)
                        merged[merged.Count - 1] = (merged[merged.Count - 1].Item1, Math.Max(merged[merged.Count - 1].Item2, t.Item2));
                    else merged.Add(t);
                }
                Assert.Equal(merged.Count, got.Count);
                for (var k = 0; k < merged.Count; k++)
                {
                    Assert.Equal(merged[k].Item1, got[k].ZIn, 9);
                    Assert.Equal(merged[k].Item2, got[k].ZOut, 9);
                }
            }
        }
    }
}
