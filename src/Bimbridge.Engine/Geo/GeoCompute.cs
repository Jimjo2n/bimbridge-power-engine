// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Bimbridge.Engine.Geo
{
    /// <summary>An element being checked (fixture, equipment…). The add-in fills it on the Revit thread; here it is only read.</summary>
    public sealed class GeoTarget
    {
        public long Id;
        public string Level;
        public double LevelElev = double.NaN;
        public string TypeKey;
        public string Category;
        public Box3 Box;
        public double X, Y, Z;
        /// <summary>Real body (mesh). Empty or null = the box is used.</summary>
        public TriMesh Body;
        /// <summary>Indices of index bodies that may touch it (decided by the add-in while collecting).</summary>
        public int[] Candidates = new int[0];
        public string Room;
        public string Space;
        public string Note;
    }

    /// <summary>Host level for nivel_real and rango_vista.</summary>
    public sealed class GeoLevel
    {
        public string Name;
        public double Elevation;
    }

    /// <summary>Plan view (floor or ceiling) with its range already in absolute host heights.</summary>
    public sealed class GeoView
    {
        public long Id;
        public string Name;
        public string Level;
        public bool Ceiling;
        public double Cut, Top, Bottom, Depth;
        /// <summary>Plan crop (if active). Empty = no crop.</summary>
        public Box3 Crop = Box3.Empty;
        public List<Box3> Regions = new List<Box3>();
    }

    /// <summary>
    /// Computes the "geo" recipes over already collected data: runs off the Revit thread and in parallel.
    /// Each recipe returns rows (simple values) that GeoSummary turns into the compact response.
    /// </summary>
    public static class GeoCompute
    {
        public static int Threads => Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));

        /// <summary>Search height below the body (feet) to find what wraps it.</summary>
        public const double BelowMargin = 1.0;

        public sealed class Stats
        {
            public int Rays;
            public int Inconsistent;
            public int BoxFallback;
            public long Ms;
        }

        // ------------------------------------------------------------------ mounting / clearance

        /// <summary>
        /// Mounting: at each footprint point, the body's real top face (mesh) is measured against the bottom face of what is above
        /// (or the base against what is below). The point with the smallest clearance controls (corner on a slope). Without a body the box is used and reported.
        /// </summary>
        public static List<Dictionary<string, object>> Mount(IList<GeoTarget> targets, IList<GeoBody> bodies, GeoSpec spec, Stats stats)
        {
            var rows = new Dictionary<string, object>[targets.Count];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rays = 0, bad = 0, boxes = 0;
            Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads },
                () => new Local(),
                (i, _, loc) =>
                {
                    rows[i] = MountOne(targets[i], bodies, spec, loc);
                    return loc;
                },
                loc =>
                {
                    System.Threading.Interlocked.Add(ref rays, loc.Rays);
                    System.Threading.Interlocked.Add(ref bad, loc.Bad);
                    System.Threading.Interlocked.Add(ref boxes, loc.Boxes);
                });
            if (stats != null) { stats.Rays += rays; stats.Inconsistent += bad; stats.BoxFallback += boxes; stats.Ms += sw.ElapsedMilliseconds; }
            return rows.ToList();
        }

        private sealed class Local
        {
            public readonly RayScratch Sc = new RayScratch();
            public readonly List<Segment> Segs = new List<Segment>(16);
            public readonly List<ProbeHit> Hits = new List<ProbeHit>(9);
            public int Rays, Bad, Boxes;
        }

        /// <summary>Mounting result of one element (also used by snap_to_surface when planning).</summary>
        public static MountResult MeasureMount(GeoTarget t, IList<GeoBody> bodies, GeoSpec spec, RayScratch sc, out bool usedBox, out int inconsistent)
        {
            var loc = new Local();
            var r = MeasureMountCore(t, bodies, spec, loc, out usedBox);
            inconsistent = loc.Bad;
            return r;
        }

        private static Dictionary<string, object> MountOne(GeoTarget t, IList<GeoBody> bodies, GeoSpec spec, Local loc)
        {
            var r = MeasureMountCore(t, bodies, spec, loc, out var usedBox);
            if (usedBox) loc.Boxes++;
            var row = new Dictionary<string, object>
            {
                ["id"] = t.Id,
                ["lvl"] = t.Level,
                ["class"] = r.Class,
                ["gap"] = r.GapMin,
                ["pts"] = r.PointsWithSurface + "/" + r.PointsTotal,
                ["against"] = r.Item >= 0 && r.Item < bodies.Count ? bodies[r.Item].Label : null,
            };
            if (spec.Points > 1) row["gap_max"] = r.GapMax;
            if (r.Sloped) row["pend"] = true;
            if (usedBox) row["body"] = "box";
            if (!double.IsNaN(r.GapMin)) row["move"] = spec.Up ? r.GapMin : -r.GapMin;
            if (spec.SurfaceReference && r.Item >= 0 && r.Item < bodies.Count)
            {
                var surface = bodies[r.Item];
                row["element_id"] = surface.Id;
                row["link_id"] = surface.LinkId;
                row["face_x"] = r.SurfaceX;
                row["face_y"] = r.SurfaceY;
                row["face_z"] = r.SurfaceZ;
                row["face_side"] = spec.Up ? "bottom" : "top";
            }
            return row;
        }

        private static MountResult MeasureMountCore(GeoTarget t, IList<GeoBody> bodies, GeoSpec spec, Local loc, out bool usedBox)
        {
            var pts = Probes.Points(t.Box, spec.Points);
            var hits = loc.Hits;
            hits.Clear();
            var body = t.Body;
            usedBox = body == null || body.IsEmpty;
            var onBody = 0;
            for (var pass = 0; pass < 2 && onBody == 0; pass++)
            {
                // Second pass only if no point landed on the body (odd mesh): measure with the box.
                if (pass == 1) { usedBox = true; hits.Clear(); }
                foreach (var p in pts)
                {
                    double zBot, zTop;
                    if (!usedBox)
                    {
                        var er = body.Extent(p[0], p[1], loc.Sc, out zBot, out zTop);
                        if (er == RayResult.Inconsistent) { loc.Bad++; continue; }
                        if (er != RayResult.Ok) continue; // the point falls outside the body (e.g. corner of a round puck)
                    }
                    else { zBot = t.Box.MinZ; zTop = t.Box.MaxZ; }
                    onBody++;
                    loc.Segs.Clear();
                    double z0 = spec.Up ? zBot - BelowMargin : zBot - spec.MaxRange;
                    double z1 = spec.Up ? zTop + spec.MaxRange : zTop + BelowMargin;
                    foreach (var c in t.Candidates)
                    {
                        var m = bodies[c].Mesh;
                        if (m == null || m.IsEmpty) continue;
                        loc.Rays++;
                        if (m.Segments(p[0], p[1], z0, z1, c, loc.Segs, loc.Sc) == RayResult.Inconsistent) loc.Bad++;
                    }
                    hits.Add(spec.Up
                        ? Mounting.Up(p[0], p[1], zBot, zTop, loc.Segs, spec.MaxRange, spec.Tolerance)
                        : Mounting.Down(p[0], p[1], zBot, zTop, loc.Segs, spec.MaxRange, spec.Tolerance));
                }
            }
            return Mounting.Classify(hits, spec.Tolerance);
        }

        // ------------------------------------------------------------------ near

        public static List<Dictionary<string, object>> Near(IList<GeoTarget> targets, IList<GeoBody> bodies, GeoSpec spec)
        {
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                GeoBody best = null;
                double bestD = double.MaxValue, bestDz = 0;
                string cls = "free";
                var count = 0;
                foreach (var c in t.Candidates)
                {
                    var b = bodies[c];
                    var dxy = t.Box.DistanceXY(b.Box);
                    if (dxy > spec.Radius) continue;
                    count++;
                    var overlapZ = b.Box.MinZ < t.Box.MaxZ - spec.Tolerance && b.Box.MaxZ > t.Box.MinZ + spec.Tolerance;
                    double dz = b.Box.MinZ >= t.Box.MaxZ - spec.Tolerance ? b.Box.MinZ - t.Box.MaxZ
                              : b.Box.MaxZ <= t.Box.MinZ + spec.Tolerance ? b.Box.MaxZ - t.Box.MinZ : 0;
                    string c2 = dxy <= 0 && overlapZ ? "clash" : dxy <= 0 ? (dz >= 0 ? "above" : "below") : "near";
                    var better = best == null || Rank(c2) < Rank(cls) || (Rank(c2) == Rank(cls) && (dxy < bestD || (dxy == bestD && Math.Abs(dz) < Math.Abs(bestDz))));
                    if (better) { best = b; bestD = dxy; bestDz = dz; cls = c2; }
                }
                var row = new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = cls };
                if (best != null)
                {
                    row["other"] = best.Label;
                    row["dist_xy"] = bestD;
                    row["dz"] = bestDz;
                    if (count > 1) row["n"] = (long)count;
                }
                rows.Add(row);
            }
            return rows;
        }

        private static int Rank(string c)
        {
            switch (c)
            {
                case "clash": return 0;
                case "above": return 1;
                case "below": return 2;
                case "near": return 3;
                default: return 4;
            }
        }

        // ------------------------------------------------------------------ wall

        public static List<Dictionary<string, object>> Wall(IList<GeoTarget> targets, IList<GeoBody> walls, GeoSpec spec)
        {
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                double best = double.MaxValue; GeoBody bw = null; var side = 0;
                foreach (var c in t.Candidates)
                {
                    var w = walls[c];
                    if (w.Line == null || w.Line.Length < 4) continue;
                    // Only walls that exist at the element's height.
                    if (w.Box.MaxZ < t.Box.MinZ - spec.Tolerance || w.Box.MinZ > t.Box.MaxZ + spec.Tolerance) continue;
                    for (var k = 0; k + 3 < w.Line.Length; k += 2)
                    {
                        double ax = w.Line[k], ay = w.Line[k + 1], bx = w.Line[k + 2], by = w.Line[k + 3];
                        var d = Plan2D.BoxSegment(t.Box, ax, ay, bx, by) - w.Width / 2;
                        if (d < best)
                        {
                            best = d; bw = w;
                            Plan2D.PointSegment(t.Box.CenterX, t.Box.CenterY, ax, ay, bx, by, out side, out _);
                        }
                    }
                }
                var row = new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level };
                if (bw == null || best > spec.Radius) { row["class"] = "no_wall"; rows.Add(row); continue; }
                row["class"] = best < -spec.Tolerance ? "penetrates" : best <= spec.Tolerance ? "touches" : "separated";
                row["dist"] = best;
                row["wall"] = bw.Label;
                row["side"] = side > 0 ? "left_side" : "right_side";
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ nivel_real

        public static List<Dictionary<string, object>> RealLevel(IList<GeoTarget> targets, IList<GeoLevel> levels)
        {
            var infos = levels.Select(l => new LevelMath.LevelInfo { Name = l.Name, Elevation = l.Elevation }).ToList();
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                var z = t.Box.IsEmpty ? t.Z : t.Box.MinZ;
                var real = LevelMath.Resolve(infos, z);
                var same = real != null && string.Equals(real.Name, t.Level, StringComparison.OrdinalIgnoreCase);
                var row = new Dictionary<string, object>
                {
                    ["id"] = t.Id, ["lvl"] = t.Level, ["actual_level"] = real?.Name, ["class"] = same ? "equal" : "different", ["z"] = z,
                };
                if (!double.IsNaN(t.LevelElev)) row["dz"] = z - t.LevelElev;
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ duplicates

        public static List<Dictionary<string, object>> Duplicates(IList<GeoTarget> targets, GeoSpec spec)
        {
            var pts = targets.Select(t => new Clusters.Point { Id = t.Id, Key = spec.SameType ? t.TypeKey ?? "" : "", X = t.X, Y = t.Y, Z = t.Z }).ToList();
            var groups = Clusters.Duplicates(pts, spec.Tolerance, spec.SameType);
            var level = targets.ToDictionary(t => t.Id, t => t.Level);
            var rows = new List<Dictionary<string, object>>();
            foreach (var g in groups)
            {
                var keep = g[0];
                foreach (var p in g.Skip(1))
                {
                    var dx = p.X - keep.X; var dy = p.Y - keep.Y; var dz = p.Z - keep.Z;
                    rows.Add(new Dictionary<string, object>
                    {
                        ["id"] = p.Id, ["lvl"] = level.TryGetValue(p.Id, out var l) ? l : null, ["class"] = "leftover",
                        ["equal_to"] = keep.Id, ["dist"] = Math.Sqrt(dx * dx + dy * dy + dz * dz),
                    });
                }
            }
            return rows;
        }

        // ------------------------------------------------------------------ room

        public static List<Dictionary<string, object>> Rooms(IList<GeoTarget> targets, string source)
        {
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                var has = source == "spaces" ? t.Space != null : source == "rooms" ? t.Room != null : (t.Room != null || t.Space != null);
                var row = new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = has ? "with_room" : "no_room" };
                if (source != "spaces") row["room"] = t.Room;
                if (source != "rooms") row["space"] = t.Space;
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ rango_vista

        /// <summary>
        /// For each plan view: fixtures on its level fall inside, above or below the range, or outside the crop;
        /// those on other levels that fall inside the range are marked "foreign" (they will show in that view).
        /// Those inside a plan region are marked en_region: the API does not expose its range.
        /// </summary>
        public static List<Dictionary<string, object>> ViewRange(IList<GeoTarget> targets, IList<GeoView> views)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var v in views)
            {
                foreach (var t in targets)
                {
                    var own = string.Equals(t.Level, v.Level, StringComparison.OrdinalIgnoreCase);
                    var cls = ViewBand.Classify(v.Ceiling, v.Cut, v.Top, v.Bottom, v.Depth, t.Box.MinZ, t.Box.MaxZ, out var low, out var high);
                    if (!own && cls != ViewBand.Inside) continue;
                    if (!v.Crop.IsEmpty && !v.Crop.ContainsXY(t.Box.CenterX, t.Box.CenterY)) { if (!own) continue; cls = "outside"; }
                    else if (v.Regions.Any(r => r.ContainsXY(t.Box.CenterX, t.Box.CenterY))) cls = "in_region";
                    else if (!own) cls = "foreign";
                    rows.Add(new Dictionary<string, object>
                    {
                        ["view"] = v.Name, ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = cls,
                        ["zbot"] = t.Box.MinZ, ["ztop"] = t.Box.MaxZ, ["low"] = low, ["high"] = high,
                    });
                }
            }
            return rows;
        }

        // ------------------------------------------------------------------ clashes (screened outside Revit)

        /// <summary>An element–body pair the screen could not decide: resolved with Revit (exact boolean).</summary>
        public struct PairToCheck
        {
            public int Target;
            public int Body;
        }

        /// <summary>
        /// Clash screen: discards pairs that only touch (vertical overlap ≤ tolerance) and confirms with rays those that
        /// penetrate vertically (fixture sunk into the slab or beam). What remains goes to Revit's exact boolean.
        /// </summary>
        public static List<Dictionary<string, object>> ClashScreen(IList<GeoTarget> targets, IList<GeoBody> bodies, GeoSpec spec,
            List<PairToCheck> exact, Stats stats)
        {
            var rows = new List<Dictionary<string, object>>();
            var sc = new RayScratch();
            var segs = new List<Segment>();
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var found = false;
                var pending = false;
                foreach (var c in t.Candidates)
                {
                    var b = bodies[c];
                    if (!t.Box.Overlaps(b.Box, -spec.Tolerance)) continue; // they only touch
                    double depth = 0;
                    var decided = false;
                    if (t.Body != null && !t.Body.IsEmpty && b.Mesh != null && !b.Mesh.IsEmpty)
                    {
                        foreach (var p in Probes.Points(t.Box, 9))
                        {
                            if (t.Body.Extent(p[0], p[1], sc, out var zb, out var zt) != RayResult.Ok) continue;
                            segs.Clear();
                            if (stats != null) stats.Rays++;
                            b.Mesh.Segments(p[0], p[1], zb, zt, c, segs, sc);
                            foreach (var s in segs)
                            {
                                var ov = Math.Min(zt, s.ZOut) - Math.Max(zb, s.ZIn);
                                if (ov > depth) depth = ov;
                            }
                        }
                        if (depth > spec.Tolerance) decided = true;
                    }
                    if (decided)
                    {
                        rows.Add(new Dictionary<string, object>
                        {
                            ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = "clash", ["against"] = b.Label, ["prof"] = depth, ["method"] = "rays",
                        });
                        found = true;
                    }
                    else { exact.Add(new PairToCheck { Target = i, Body = c }); pending = true; }
                }
                if (!found && !pending)
                    rows.Add(new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = "free" });
            }
            return rows;
        }

        [Flags]
        public enum ExactIssue { None = 0, NoSolid = 1, Extraction = 2, Transform = 4, Boolean = 8 }

        /// <summary>Observed volume and the conditions that prevent treating it as complete.</summary>
        public struct ExactResult
        {
            public int Target;
            public int Body;
            public double Volume;
            public ExactIssue Issues;
        }

        /// <summary>
        /// Merges the ray screen and the exact boolean into one row per element: the deepest (or largest-volume) clash,
        /// how many bodies it touches (n) and the method. No clashes: libre; if some boolean failed and there was no clash: no_evaluable.
        /// </summary>
        public static List<Dictionary<string, object>> MergeClashes(IList<GeoTarget> targets, IList<GeoBody> bodies,
            IEnumerable<Dictionary<string, object>> screened, IEnumerable<ExactResult> exact, double minVolume)
        {
            var byId = new Dictionary<long, List<Dictionary<string, object>>>();
            void Add(long id, Dictionary<string, object> r)
            {
                if (!byId.TryGetValue(id, out var l)) byId[id] = l = new List<Dictionary<string, object>>(2);
                l.Add(r);
            }
            foreach (var r in screened) Add((long)r["id"], r);
            var exactByTarget = (exact ?? Enumerable.Empty<ExactResult>()).ToLookup(e => e.Target);
            foreach (var group in exactByTarget)
                foreach (var e in group)
                {
                    var t = targets[e.Target];
                    var label = e.Body >= 0 && e.Body < bodies.Count ? bodies[e.Body].Label : null;
                    if (e.Volume > minVolume)
                        Add(t.Id, new Dictionary<string, object>
                        {
                            ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = "clash", ["against"] = label, ["vol"] = e.Volume, ["method"] = "boolean_op",
                            ["partial_vol"] = e.Issues != ExactIssue.None,
                        });
                    else if (e.Issues != ExactIssue.None)
                        Add(t.Id, new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = "not_evaluable", ["against"] = label });
                }
            var rows = new List<Dictionary<string, object>>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                byId.TryGetValue(t.Id, out var list);
                var clashes = list?.Where(r => (string)r["class"] == "clash").ToList() ?? new List<Dictionary<string, object>>();
                Dictionary<string, object> row;
                if (clashes.Count > 0)
                {
                    var best = clashes.OrderByDescending(r => r.TryGetValue("prof", out var p) ? (double)p : 0)
                                      .ThenByDescending(r => r.TryGetValue("vol", out var v) ? (double)v : 0).First();
                    row = new Dictionary<string, object>(best);
                    if (clashes.Count > 1) row["n"] = (long)clashes.Count;
                }
                else
                {
                    var bad = list?.FirstOrDefault(r => (string)r["class"] == "not_evaluable");
                    row = bad ?? new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["class"] = "free" };
                }
                var observed = exactByTarget[i];
                if (observed.Any()) row["coverage"] = ClashCoverage(observed, bodies, minVolume);
                rows.Add(row);
            }
            return rows;
        }

        private static Dictionary<string, object> ClashCoverage(IEnumerable<ExactResult> observed, IList<GeoBody> bodies, double minVolume)
        {
            var samples = new List<object>();
            long total = 0, unresolved = 0, partial = 0;
            foreach (var e in observed)
            {
                total++;
                if (e.Issues != ExactIssue.None)
                {
                    if (e.Volume > minVolume) partial++;
                    else unresolved++;
                }
                if (samples.Count >= 8) continue;
                var body = bodies[e.Body];
                var states = new List<object>();
                if (e.Issues.HasFlag(ExactIssue.NoSolid)) states.Add("no_solid");
                if (e.Issues.HasFlag(ExactIssue.Extraction)) states.Add("extraction_failed");
                if (e.Issues.HasFlag(ExactIssue.Transform)) states.Add("transformation_failed");
                if (e.Issues.HasFlag(ExactIssue.Boolean)) states.Add("boolean_failed");
                if (states.Count == 0) states.Add("boolean_complete");
                samples.Add(new Dictionary<string, object> { ["element_id"] = body.Id, ["link_id"] = body.LinkId, ["statuses"] = states });
            }
            return new Dictionary<string, object>
            {
                ["exact_pairs"] = total, ["unresolved"] = unresolved, ["partial_volumes"] = partial,
                ["full"] = unresolved + partial == 0, ["sample"] = samples,
            };
        }

        public static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
