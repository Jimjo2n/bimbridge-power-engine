// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace PDI.RevitMcp.Engine.Geo
{
    /// <summary>Un elemento que se revisa (luminaria, equipo…). Lo llena el add-in en el hilo de Revit; aquí sólo se lee.</summary>
    public sealed class GeoTarget
    {
        public long Id;
        public string Level;
        public double LevelElev = double.NaN;
        public string TypeKey;
        public string Category;
        public Box3 Box;
        public double X, Y, Z;
        /// <summary>Cuerpo real (malla). Vacío o null = se usa la caja.</summary>
        public TriMesh Body;
        /// <summary>Índices de cuerpos del índice que pueden tocarlo (los decide el add-in al juntar).</summary>
        public int[] Candidates = new int[0];
        public string Room;
        public string Space;
        public string Note;
    }

    /// <summary>Nivel del anfitrión para nivel_real y rango_vista.</summary>
    public sealed class GeoLevel
    {
        public string Name;
        public double Elevation;
    }

    /// <summary>Plano (de planta o de techo) con su rango ya en alturas absolutas del anfitrión.</summary>
    public sealed class GeoView
    {
        public long Id;
        public string Name;
        public string Level;
        public bool Ceiling;
        public double Cut, Top, Bottom, Depth;
        /// <summary>Recorte en planta (si está activo). Vacío = sin recorte.</summary>
        public Box3 Crop = Box3.Empty;
        public List<Box3> Regions = new List<Box3>();
    }

    /// <summary>
    /// Cálculo de las recetas de "geo" sobre datos ya juntados: corre fuera del hilo de Revit y en paralelo.
    /// Cada receta devuelve filas (valores simples) que GeoSummary convierte en la respuesta compacta.
    /// </summary>
    public static class GeoCompute
    {
        public static int Threads => Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));

        /// <summary>Altura de búsqueda por debajo del cuerpo (pies) para encontrar lo que lo envuelve.</summary>
        public const double BelowMargin = 1.0;

        public sealed class Stats
        {
            public int Rays;
            public int Inconsistent;
            public int BoxFallback;
            public long Ms;
        }

        // ------------------------------------------------------------------ montaje / holgura

        /// <summary>
        /// Montaje: en cada punto de la huella se mide la cara superior real del cuerpo (malla) contra la cara inferior de lo de arriba
        /// (o la base contra lo de abajo). Manda el punto de menor holgura (esquina en pendiente). Sin cuerpo se usa la caja y se dice.
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

        /// <summary>Resultado de montaje de un elemento (también lo usa snap_to_surface al planificar).</summary>
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
                ["clase"] = r.Class,
                ["gap"] = r.GapMin,
                ["pts"] = r.PointsWithSurface + "/" + r.PointsTotal,
                ["contra"] = r.Item >= 0 && r.Item < bodies.Count ? bodies[r.Item].Label : null,
            };
            if (spec.Points > 1) row["gap_max"] = r.GapMax;
            if (r.Sloped) row["pend"] = true;
            if (usedBox) row["cuerpo"] = "caja";
            if (!double.IsNaN(r.GapMin)) row["mover"] = spec.Up ? r.GapMin : -r.GapMin;
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
                // Segunda pasada sólo si ningún punto cayó sobre el cuerpo (malla rara): se mide con la caja.
                if (pass == 1) { usedBox = true; hits.Clear(); }
                foreach (var p in pts)
                {
                    double zBot, zTop;
                    if (!usedBox)
                    {
                        var er = body.Extent(p[0], p[1], loc.Sc, out zBot, out zTop);
                        if (er == RayResult.Inconsistent) { loc.Bad++; continue; }
                        if (er != RayResult.Ok) continue; // el punto queda fuera del cuerpo (p. ej. esquina de un puck redondo)
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

        // ------------------------------------------------------------------ cerca

        public static List<Dictionary<string, object>> Near(IList<GeoTarget> targets, IList<GeoBody> bodies, GeoSpec spec)
        {
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                GeoBody best = null;
                double bestD = double.MaxValue, bestDz = 0;
                string cls = "libre";
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
                    string c2 = dxy <= 0 && overlapZ ? "choque" : dxy <= 0 ? (dz >= 0 ? "encima" : "debajo") : "cerca";
                    var better = best == null || Rank(c2) < Rank(cls) || (Rank(c2) == Rank(cls) && (dxy < bestD || (dxy == bestD && Math.Abs(dz) < Math.Abs(bestDz))));
                    if (better) { best = b; bestD = dxy; bestDz = dz; cls = c2; }
                }
                var row = new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = cls };
                if (best != null)
                {
                    row["otro"] = best.Label;
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
                case "choque": return 0;
                case "encima": return 1;
                case "debajo": return 2;
                case "cerca": return 3;
                default: return 4;
            }
        }

        // ------------------------------------------------------------------ muro

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
                    // Sólo muros que existen a la altura del elemento.
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
                if (bw == null || best > spec.Radius) { row["clase"] = "sin_muro"; rows.Add(row); continue; }
                row["clase"] = best < -spec.Tolerance ? "penetra" : best <= spec.Tolerance ? "toca" : "separada";
                row["dist"] = best;
                row["muro"] = bw.Label;
                row["lado"] = side > 0 ? "izq" : "der";
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
                    ["id"] = t.Id, ["lvl"] = t.Level, ["nivel_real"] = real?.Name, ["clase"] = same ? "igual" : "distinto", ["z"] = z,
                };
                if (!double.IsNaN(t.LevelElev)) row["dz"] = z - t.LevelElev;
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ duplicados

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
                        ["id"] = p.Id, ["lvl"] = level.TryGetValue(p.Id, out var l) ? l : null, ["clase"] = "sobrante",
                        ["igual_a"] = keep.Id, ["dist"] = Math.Sqrt(dx * dx + dy * dy + dz * dz),
                    });
                }
            }
            return rows;
        }

        // ------------------------------------------------------------------ cuarto

        public static List<Dictionary<string, object>> Rooms(IList<GeoTarget> targets, string source)
        {
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                var has = source == "espacios" ? t.Space != null : source == "cuartos" ? t.Room != null : (t.Room != null || t.Space != null);
                var row = new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = has ? "con_cuarto" : "sin_cuarto" };
                if (source != "espacios") row["cuarto"] = t.Room;
                if (source != "cuartos") row["espacio"] = t.Space;
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------ rango_vista

        /// <summary>
        /// Por cada plano: las luminarias de su nivel quedan dentro, encima o debajo del rango, o fuera del recorte;
        /// las de otros niveles que caen dentro del rango se marcan "ajena" (se verán en ese plano).
        /// Las que están dentro de una región de plano se marcan en_region: su rango no lo expone la API.
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
                    if (!v.Crop.IsEmpty && !v.Crop.ContainsXY(t.Box.CenterX, t.Box.CenterY)) { if (!own) continue; cls = "fuera"; }
                    else if (v.Regions.Any(r => r.ContainsXY(t.Box.CenterX, t.Box.CenterY))) cls = "en_region";
                    else if (!own) cls = "ajena";
                    rows.Add(new Dictionary<string, object>
                    {
                        ["vista"] = v.Name, ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = cls,
                        ["zbot"] = t.Box.MinZ, ["ztop"] = t.Box.MaxZ, ["bajo"] = low, ["alto"] = high,
                    });
                }
            }
            return rows;
        }

        // ------------------------------------------------------------------ choques (cribado fuera de Revit)

        /// <summary>Un par elemento–cuerpo que la criba no pudo decidir: se resuelve con Revit (booleana exacta).</summary>
        public struct PairToCheck
        {
            public int Target;
            public int Body;
        }

        /// <summary>
        /// Criba de choques: descarta pares que sólo se tocan (solape vertical ≤ tolerancia) y confirma con rayos los que se
        /// penetran verticalmente (luminaria metida en la losa o la viga). Lo que queda va a la booleana exacta de Revit.
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
                    if (!t.Box.Overlaps(b.Box, -spec.Tolerance)) continue; // sólo se tocan
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
                            ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "choque", ["contra"] = b.Label, ["prof"] = depth, ["metodo"] = "rayos",
                        });
                        found = true;
                    }
                    else { exact.Add(new PairToCheck { Target = i, Body = c }); pending = true; }
                }
                if (!found && !pending)
                    rows.Add(new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "libre" });
            }
            return rows;
        }

        /// <summary>Resultado de la booleana exacta de Revit para un par que la criba no decidió.</summary>
        public struct ExactResult
        {
            public int Target;
            public int Body;
            public double Volume;
            public bool Failed;
        }

        /// <summary>
        /// Une la criba por rayos y la booleana exacta en una fila por elemento: el choque más profundo (o de más volumen),
        /// cuántos cuerpos toca (n) y el método. Sin choques: libre; si alguna booleana falló y no hubo choque: no_evaluable.
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
            if (exact != null)
                foreach (var e in exact)
                {
                    var t = targets[e.Target];
                    var label = e.Body >= 0 && e.Body < bodies.Count ? bodies[e.Body].Label : null;
                    if (e.Failed)
                        Add(t.Id, new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "no_evaluable", ["contra"] = label });
                    else if (e.Volume > minVolume)
                        Add(t.Id, new Dictionary<string, object>
                        {
                            ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "choque", ["contra"] = label, ["vol"] = e.Volume, ["metodo"] = "booleana",
                        });
                }
            var rows = new List<Dictionary<string, object>>(targets.Count);
            foreach (var t in targets)
            {
                if (!byId.TryGetValue(t.Id, out var list))
                {
                    rows.Add(new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "libre" });
                    continue;
                }
                var clashes = list.Where(r => (string)r["clase"] == "choque").ToList();
                if (clashes.Count > 0)
                {
                    var best = clashes.OrderByDescending(r => r.TryGetValue("prof", out var p) ? (double)p : 0)
                                      .ThenByDescending(r => r.TryGetValue("vol", out var v) ? (double)v : 0).First();
                    var row = new Dictionary<string, object>(best);
                    if (clashes.Count > 1) row["n"] = (long)clashes.Count;
                    rows.Add(row);
                }
                else
                {
                    var bad = list.FirstOrDefault(r => (string)r["clase"] == "no_evaluable");
                    rows.Add(bad ?? new Dictionary<string, object> { ["id"] = t.Id, ["lvl"] = t.Level, ["clase"] = "libre" });
                }
            }
            return rows;
        }

        public static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
