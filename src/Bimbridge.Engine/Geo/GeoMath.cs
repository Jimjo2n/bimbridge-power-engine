// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine.Geo
{
    /// <summary>
    /// Geometry math without the Revit API (tested on Linux). The add-in only extracts boxes,
    /// segments and intersections; this decides what they mean: clearance, embedded, floating, real level, duplicates.
    /// Everything in feet (Revit's internal unit) and in host model coordinates.
    /// </summary>
    public struct Box3
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public Box3(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = Math.Min(minX, maxX); MaxX = Math.Max(minX, maxX);
            MinY = Math.Min(minY, maxY); MaxY = Math.Max(minY, maxY);
            MinZ = Math.Min(minZ, maxZ); MaxZ = Math.Max(minZ, maxZ);
        }

        public static Box3 Empty => new Box3 { MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue };

        public bool IsEmpty => MinX > MaxX || MinY > MaxY || MinZ > MaxZ;
        public double SizeX => MaxX - MinX;
        public double SizeY => MaxY - MinY;
        public double SizeZ => MaxZ - MinZ;
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
        public double CenterZ => (MinZ + MaxZ) / 2;

        public Box3 Include(double x, double y, double z)
        {
            var b = this;
            if (x < b.MinX) b.MinX = x; if (x > b.MaxX) b.MaxX = x;
            if (y < b.MinY) b.MinY = y; if (y > b.MaxY) b.MaxY = y;
            if (z < b.MinZ) b.MinZ = z; if (z > b.MaxZ) b.MaxZ = z;
            return b;
        }

        public Box3 Union(Box3 o)
        {
            if (IsEmpty) return o;
            if (o.IsEmpty) return this;
            return new Box3(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ),
                            Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));
        }

        public Box3 Grow(double xy, double z) => new Box3(MinX - xy, MinY - xy, MinZ - z, MaxX + xy, MaxY + xy, MaxZ + z);

        public bool ContainsXY(double x, double y, double tol = 0) =>
            x >= MinX - tol && x <= MaxX + tol && y >= MinY - tol && y <= MaxY + tol;

        public bool OverlapsZ(double z0, double z1) => MaxZ >= Math.Min(z0, z1) && MinZ <= Math.Max(z0, z1);

        public bool Overlaps(Box3 o, double tol = 0) =>
            MinX <= o.MaxX + tol && MaxX >= o.MinX - tol &&
            MinY <= o.MaxY + tol && MaxY >= o.MinY - tol &&
            MinZ <= o.MaxZ + tol && MaxZ >= o.MinZ - tol;

        /// <summary>Horizontal distance between two boxes (0 if they overlap in plan).</summary>
        public double DistanceXY(Box3 o)
        {
            var dx = Math.Max(0, Math.Max(o.MinX - MaxX, MinX - o.MaxX));
            var dy = Math.Max(0, Math.Max(o.MinY - MaxY, MinY - o.MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Vertical gap: positive if o is entirely above or below; 0 if they overlap in Z.</summary>
        public double GapZ(Box3 o) => Math.Max(0, Math.Max(o.MinZ - MaxZ, MinZ - o.MaxZ));
    }

    /// <summary>
    /// Spatial index in plan: uniform grid. A large slab falls into many cells; a fixture into one or two.
    /// Querying a box returns only the elements of the touched cells (no duplicates).
    /// Avoids the pyRevit pattern that costs O(fixtures × elements): every script walked all the solids of the link.
    /// </summary>
    public sealed class GridIndex
    {
        private readonly double _cell;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly List<int> _big = new List<int>();
        private readonly List<Box3> _boxes = new List<Box3>();
        private int[] _stamp = new int[0];
        private int _stampValue;

        /// <summary>An element that would occupy more cells than this goes to a separate list that is always checked.</summary>
        public const int MaxCellsPerItem = 4096;

        public GridIndex(double cellSize = 8.0)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _cell = cellSize;
        }

        public int Count => _boxes.Count;
        public double CellSize => _cell;
        public Box3 BoxOf(int item) => _boxes[item];

        /// <summary>Adds an element and returns its index (0, 1, 2…).</summary>
        public int Add(Box3 b)
        {
            var item = _boxes.Count;
            _boxes.Add(b);
            long x0 = Cell(b.MinX), x1 = Cell(b.MaxX), y0 = Cell(b.MinY), y1 = Cell(b.MaxY);
            var n = (x1 - x0 + 1) * (y1 - y0 + 1);
            if (n > MaxCellsPerItem || n <= 0) { _big.Add(item); return item; }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    var key = Key(x, y);
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(4);
                    list.Add(item);
                }
            return item;
        }

        /// <summary>Elements whose box touches the plan rectangle (and, if given, the Z range).</summary>
        public List<int> Query(double minX, double minY, double maxX, double maxY, double minZ = double.MinValue, double maxZ = double.MaxValue)
        {
            var result = new List<int>();
            if (_boxes.Count == 0) return result;
            if (_stamp.Length < _boxes.Count) _stamp = new int[Math.Max(_boxes.Count, _stamp.Length * 2)];
            if (++_stampValue == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _stampValue = 1; }
            var probe = new Box3(minX, minY, minZ, maxX, maxY, maxZ);
            void Consider(int i)
            {
                if (_stamp[i] == _stampValue) return;
                _stamp[i] = _stampValue;
                var b = _boxes[i];
                if (b.MinX <= probe.MaxX && b.MaxX >= probe.MinX && b.MinY <= probe.MaxY && b.MaxY >= probe.MinY &&
                    b.MinZ <= probe.MaxZ && b.MaxZ >= probe.MinZ)
                    result.Add(i);
            }
            long x0 = Cell(minX), x1 = Cell(maxX), y0 = Cell(minY), y1 = Cell(maxY);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 100000)
            {
                for (var i = 0; i < _boxes.Count; i++) Consider(i);
                return result;
            }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    if (_cells.TryGetValue(Key(x, y), out var list))
                        foreach (var i in list) Consider(i);
            foreach (var i in _big) Consider(i);
            return result;
        }

        public List<int> QueryPoint(double x, double y, double minZ = double.MinValue, double maxZ = double.MaxValue) =>
            Query(x, y, x, y, minZ, maxZ);

        private long Cell(double v) => (long)Math.Floor(v / _cell);
        private static long Key(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFFL);
    }

    /// <summary>Segment of a vertical ray that lies inside a solid: enters at ZIn and exits at ZOut.</summary>
    public struct Segment
    {
        public double ZIn, ZOut;
        /// <summary>Index of the element (in the geometry index) it belongs to.</summary>
        public int Item;
        public Segment(double a, double b, int item) { ZIn = Math.Min(a, b); ZOut = Math.Max(a, b); Item = item; }
        public override string ToString() => "[" + ZIn + ", " + ZOut + "] #" + Item;
    }

    /// <summary>Result at one point of the footprint.</summary>
    public struct ProbeHit
    {
        /// <summary>Signed clearance: positive = free space; negative = penetration. NaN = no surface in range.</summary>
        public double Gap;
        public int Item;
        public double X, Y, SurfaceZ;
        public bool Found => !double.IsNaN(Gap);
    }

    public static class MountClass
    {
        public const string Ok = "ok";
        public const string Floating = "flotando";
        public const string Embedded = "empotrada";
        public const string NoSurface = "sin_superficie";
        public const string NoBody = "sin_cuerpo";
    }

    /// <summary>Summary of all the points of a fixture.</summary>
    public sealed class MountResult
    {
        public string Class;
        /// <summary>Clearance at the controlling point (the closest or the most embedded).</summary>
        public double GapMin = double.NaN;
        public double GapMax = double.NaN;
        public int Item = -1;
        public double SurfaceX, SurfaceY, SurfaceZ;
        public int PointsTotal;
        public int PointsWithSurface;
        /// <summary>The surface is not parallel to the body (ramp, stair): the clearance varies more than the tolerance.</summary>
        public bool Sloped;
        public bool Partial => PointsWithSurface > 0 && PointsWithSurface < PointsTotal;
        /// <summary>How much to raise (positive) or lower (negative) so the controlling point ends up at the desired clearance.</summary>
        public double DeltaFor(double targetGap) => double.IsNaN(GapMin) ? double.NaN : GapMin - targetGap;
    }

    /// <summary>
    /// Operator mounting rule (2026-10-13): without an explicit height, the fixture's top face sits exactly
    /// on the bottom face of the slab above, never embedded. On a slope the closest corner controls.
    /// </summary>
    public static class Mounting
    {
        /// <summary>1/16" in feet: historical QC tolerance (±1/16").</summary>
        public const double DefaultTolerance = 1.0 / 16.0 / 12.0;

        /// <summary>
        /// Upward: the body occupies [zBot, zTop] at this point. Finds the first solid that is not
        /// entirely below the body. Clearance = bottom face of that solid − top face of the body.
        /// If the solid wraps the body (fixture inside the slab) the clearance is negative: embedded.
        /// </summary>
        public static ProbeHit Up(double x, double y, double zBot, double zTop, IEnumerable<Segment> surfaces, double maxUp, double tol)
        {
            var best = new ProbeHit { Gap = double.NaN, Item = -1, X = x, Y = y };
            var bestIn = double.MaxValue;
            foreach (var s in surfaces)
            {
                if (s.ZOut <= zBot + tol) continue;            // entirely below the body: it is a floor, not what is above
                if (s.ZOut - s.ZIn <= 1e-9) continue;
                if (s.ZIn < bestIn) { bestIn = s.ZIn; best.Item = s.Item; }
            }
            if (best.Item < 0) return best;
            var gap = bestIn - zTop;
            if (gap > maxUp) { best.Item = -1; return best; }
            best.Gap = gap;
            best.SurfaceZ = bestIn;
            return best;
        }

        /// <summary>Downward (fixtures or equipment resting on a floor): clearance = body base − top face of the solid below.</summary>
        public static ProbeHit Down(double x, double y, double zBot, double zTop, IEnumerable<Segment> surfaces, double maxDown, double tol)
        {
            var best = new ProbeHit { Gap = double.NaN, Item = -1, X = x, Y = y };
            var bestOut = double.MinValue;
            foreach (var s in surfaces)
            {
                if (s.ZIn >= zTop - tol) continue;             // entirely above the body
                if (s.ZOut - s.ZIn <= 1e-9) continue;
                if (s.ZOut > bestOut) { bestOut = s.ZOut; best.Item = s.Item; }
            }
            if (best.Item < 0) return best;
            var gap = zBot - bestOut;
            if (gap > maxDown) { best.Item = -1; return best; }
            best.Gap = gap;
            best.SurfaceZ = bestOut;
            return best;
        }

        /// <summary>Combines the points: the one with the smallest clearance controls (the first to touch or the most embedded).</summary>
        public static MountResult Classify(IList<ProbeHit> hits, double tol)
        {
            var r = new MountResult { PointsTotal = hits.Count };
            foreach (var h in hits)
            {
                if (!h.Found) continue;
                r.PointsWithSurface++;
                if (double.IsNaN(r.GapMin) || h.Gap < r.GapMin)
                {
                    r.GapMin = h.Gap;
                    r.Item = h.Item;
                    r.SurfaceX = h.X;
                    r.SurfaceY = h.Y;
                    r.SurfaceZ = h.SurfaceZ;
                }
                if (double.IsNaN(r.GapMax) || h.Gap > r.GapMax) r.GapMax = h.Gap;
            }
            if (r.PointsWithSurface == 0) { r.Class = MountClass.NoSurface; return r; }
            r.Sloped = r.GapMax - r.GapMin > Math.Max(tol, 1e-6);
            r.Class = r.GapMin < -tol ? MountClass.Embedded : r.GapMin <= tol ? MountClass.Ok : MountClass.Floating;
            return r;
        }
    }

    /// <summary>Footprint points where measurements are taken: 1 (center), 5 (corners + center) or 9 (3×3 grid).</summary>
    public static class Probes
    {
        public static readonly int[] Allowed = { 1, 5, 9 };

        public static List<double[]> Points(Box3 footprint, int n, double inset = 1.0 / 24.0)
        {
            if (Array.IndexOf(Allowed, n) < 0) throw new ArgumentException("puntos must be 1, 5 or 9");
            // Measure slightly inside the border: right on the edge the intersection is unstable.
            var ix = Math.Min(inset, footprint.SizeX * 0.1);
            var iy = Math.Min(inset, footprint.SizeY * 0.1);
            double x0 = footprint.MinX + ix, x1 = footprint.MaxX - ix, y0 = footprint.MinY + iy, y1 = footprint.MaxY - iy;
            double cx = footprint.CenterX, cy = footprint.CenterY;
            var pts = new List<double[]> { new[] { cx, cy } };
            if (n == 1) return pts;
            pts.Add(new[] { x0, y0 }); pts.Add(new[] { x1, y0 }); pts.Add(new[] { x1, y1 }); pts.Add(new[] { x0, y1 });
            if (n == 5) return pts;
            pts.Add(new[] { cx, y0 }); pts.Add(new[] { x1, cy }); pts.Add(new[] { cx, y1 }); pts.Add(new[] { x0, cy });
            return pts;
        }
    }

    /// <summary>Real level by height: the highest level whose elevation is below (or within tolerance of) the point.</summary>
    public static class LevelMath
    {
        public sealed class LevelInfo
        {
            public string Name;
            public double Elevation;
            public long Id;
        }

        public static LevelInfo Resolve(IList<LevelInfo> levels, double z, double tol = 0.01)
        {
            LevelInfo best = null;
            foreach (var l in levels.OrderBy(x => x.Elevation))
                if (l.Elevation <= z + tol) best = l;
            return best;
        }

        /// <summary>The level immediately above a given one (by elevation), or null.</summary>
        public static LevelInfo Above(IList<LevelInfo> levels, double elevation, double tol = 0.01) =>
            levels.Where(l => l.Elevation > elevation + tol).OrderBy(l => l.Elevation).FirstOrDefault();

        public static LevelInfo Below(IList<LevelInfo> levels, double elevation, double tol = 0.01) =>
            levels.Where(l => l.Elevation < elevation - tol).OrderByDescending(l => l.Elevation).FirstOrDefault();
    }

    /// <summary>Duplicates: elements with the same key (type) within tol in X, Y and Z.</summary>
    public static class Clusters
    {
        public struct Point
        {
            public long Id;
            public string Key;
            public double X, Y, Z;
        }

        /// <summary>Groups of 2 or more. In each group the first is the lowest id (the oldest): the others are the extras.</summary>
        public static List<List<Point>> Duplicates(IList<Point> pts, double tol, bool sameKey = true)
        {
            var grid = new GridIndex(Math.Max(tol * 4, 0.5));
            foreach (var p in pts) grid.Add(new Box3(p.X, p.Y, p.Z, p.X, p.Y, p.Z));
            var parent = Enumerable.Range(0, pts.Count).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                foreach (var j in grid.Query(p.X - tol, p.Y - tol, p.X + tol, p.Y + tol, p.Z - tol, p.Z + tol))
                {
                    if (j <= i) continue;
                    var q = pts[j];
                    if (sameKey && !string.Equals(p.Key, q.Key, StringComparison.Ordinal)) continue;
                    var dx = p.X - q.X; var dy = p.Y - q.Y; var dz = p.Z - q.Z;
                    if (dx * dx + dy * dy + dz * dz > tol * tol) continue;
                    var a = Find(i); var b = Find(j);
                    if (a != b) parent[b] = a;
                }
            }
            return Enumerable.Range(0, pts.Count).GroupBy(Find).Where(g => g.Count() > 1)
                .Select(g => g.Select(i => pts[i]).OrderBy(p => p.Id).ToList())
                .OrderBy(g => g[0].Id).ToList();
        }
    }

    /// <summary>Plan distances for wall contact.</summary>
    public static class Plan2D
    {
        /// <summary>Plan distance between the footprint (box rectangle) and segment AB; 0 if they cross or touch.</summary>
        public static double BoxSegment(Box3 b, double ax, double ay, double bx, double by)
        {
            if (b.ContainsXY(ax, ay) || b.ContainsXY(bx, by)) return 0;
            double[] xs = { b.MinX, b.MaxX, b.MaxX, b.MinX };
            double[] ys = { b.MinY, b.MinY, b.MaxY, b.MaxY };
            var d = double.MaxValue;
            for (var i = 0; i < 4; i++)
            {
                var j = (i + 1) & 3;
                d = Math.Min(d, SegmentSegment(ax, ay, bx, by, xs[i], ys[i], xs[j], ys[j]));
                if (d <= 0) return 0;
            }
            return d;
        }

        /// <summary>Minimum distance between two segments in plan (0 if they cross).</summary>
        public static double SegmentSegment(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double o1 = Orient(ax, ay, bx, by, cx, cy), o2 = Orient(ax, ay, bx, by, dx, dy);
            double o3 = Orient(cx, cy, dx, dy, ax, ay), o4 = Orient(cx, cy, dx, dy, bx, by);
            if (((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0))) return 0;
            var m = PointSegment(ax, ay, cx, cy, dx, dy, out _, out _);
            m = Math.Min(m, PointSegment(bx, by, cx, cy, dx, dy, out _, out _));
            m = Math.Min(m, PointSegment(cx, cy, ax, ay, bx, by, out _, out _));
            m = Math.Min(m, PointSegment(dx, dy, ax, ay, bx, by, out _, out _));
            return m;
        }

        private static double Orient(double ax, double ay, double bx, double by, double px, double py) =>
            (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        /// <summary>Distance from P to segment AB and side (+1 left of A→B, −1 right).</summary>
        public static double PointSegment(double px, double py, double ax, double ay, double bx, double by, out int side, out double t)
        {
            var vx = bx - ax; var vy = by - ay;
            var len2 = vx * vx + vy * vy;
            t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * vx + (py - ay) * vy) / len2));
            var cx = ax + t * vx; var cy = ay + t * vy;
            var cross = vx * (py - ay) - vy * (px - ax);
            side = cross >= 0 ? 1 : -1;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }
    }

    /// <summary>
    /// View range. A ceiling plan (RCP) looks up from the cut plane to the depth;
    /// a floor plan looks down from the cut to the depth. Estimate: does not consider plan regions
    /// (Plan Regions, whose range the API does not expose) or per-family adjustments.
    /// </summary>
    public static class ViewBand
    {
        public const string Inside = "dentro";
        public const string Above = "encima";
        public const string Below = "debajo";

        public static string Classify(bool ceilingPlan, double cut, double top, double bottom, double depth, double zBot, double zTop, out double low, out double high)
        {
            if (ceilingPlan) { low = cut; high = Math.Max(top, depth); }
            else { low = Math.Min(bottom, depth); high = cut; }
            if (zTop < low) return Below;
            if (zBot > high) return Above;
            return Inside;
        }
    }
}
