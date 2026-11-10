// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Geo;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bench
{
    /// <summary>
    /// Auditoría de eficiencia sin Revit. Mide lo que es del MCP (motor, formato, lo que ve la IA) sobre un edificio sintético
    /// del tamaño de SAMPLE PROJECT. Lo que depende de Revit (extraer sólidos, exportar imágenes) NO se mide aquí: se mide en la PC
    /// con el diario del add-in.
    /// </summary>
    public static class Program
    {
        private const double CharsPerToken = 3.2; // estimación única para todo (JSON/código en español); sólo importan las proporciones

        public static async Task<int> Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "all";
            if (mode == "cold") return Cold();
            if (mode == "warmup") return WarmupMode();
            var outFile = args.Length > 1 ? args[1] : "bench_result.json";
            var report = new Dictionary<string, object>
            {
                ["fecha"] = DateTime.UtcNow.ToString("o"),
                ["maquina"] = new Dictionary<string, object>
                {
                    ["nucleos"] = (long)Environment.ProcessorCount,
                    ["hilos_geo"] = (long)GeoCompute.Threads,
                    ["avx2"] = TriMesh.SimdAvailable,
                    ["runtime"] = RuntimeInformation.FrameworkDescription,
                    ["so"] = RuntimeInformation.OSDescription,
                },
            };
            if (mode == "all" || mode == "geo") report["geo"] = GeoBench();
            if (mode == "all" || mode == "tokens") report["tokens"] = await TokenBench();
            var json = Json.Write(report);
            File.WriteAllText(outFile, json);
            Console.WriteLine(json);
            return 0;
        }

        // =====================================================================================================
        // Geometría: edificio sintético + 1442 luminarias, montaje en 9 puntos
        // =====================================================================================================

        private sealed class Scene
        {
            public readonly List<GeoBody> Bodies = new List<GeoBody>();
            public readonly List<double[]> Raw = new List<double[]>();   // triángulos de cada cuerpo (para la fuerza bruta)
            public readonly List<GeoTarget> Targets = new List<GeoTarget>();
            public readonly List<TriMeshBuilder> Builders = new List<TriMeshBuilder>();
            public readonly List<TriMeshBuilder> TargetBuilders = new List<TriMeshBuilder>();
            public int Triangles;
            public readonly Dictionary<string, int> Planted = new Dictionary<string, int>();
            public readonly List<string> PlantedOf = new List<string>();
        }

        private static Dictionary<string, object> GeoBench() => new Dictionary<string, object>
        {
            ["normal"] = GeoScene(12, 8, brute: true),
            ["losas_detalladas_x16"] = GeoScene(48, 32, brute: true),
        };

        private static Dictionary<string, object> GeoScene(int nx, int ny, bool brute)
        {
            var d = new Dictionary<string, object>();
            var sw = Stopwatch.StartNew();
            var scene = BuildScene(levels: 14, fixtures: 1442, seed: 7, nx: nx, ny: ny);
            d["escena"] = new Dictionary<string, object>
            {
                ["niveles"] = 14L, ["cuerpos"] = (long)scene.Bodies.Count, ["triangulos"] = (long)scene.Triangles,
                ["luminarias"] = (long)scene.Targets.Count, ["sembradas"] = scene.Planted.ToDictionary(k => k.Key, k => (object)(long)k.Value),
                ["generar_ms"] = sw.ElapsedMilliseconds,
            };

            // 1) Mallas → BVH (en Revit esto se hace una vez por cuerpo y queda en caché)
            sw.Restart();
            for (var i = 0; i < scene.Bodies.Count; i++) scene.Bodies[i].Mesh = scene.Builders[i].Build();
            for (var i = 0; i < scene.Targets.Count; i++) scene.Targets[i].Body = scene.TargetBuilders[i].Build();
            d["mallas_bvh_ms"] = sw.ElapsedMilliseconds;

            // 2) Candidatos por rejilla 2D (lo que hace el índice del add-in)
            sw.Restart();
            Candidates(scene);
            d["candidatos_ms"] = sw.ElapsedMilliseconds;
            d["candidatos_promedio"] = Math.Round(scene.Targets.Average(t => t.Candidates.Length), 1);

            var spec = GeoSpec.Parse(new Dictionary<string, object> { ["name"] = "montaje" });

            // 3) Montaje 9 puntos: SIMD y escalar, en paralelo y en un hilo
            var runs = new Dictionary<string, object>();
            List<Dictionary<string, object>> rows = null;
            foreach (var (label, simd, parallel) in new[] { ("simd_paralelo", true, true), ("escalar_paralelo", false, true), ("simd_1hilo", true, false), ("escalar_1hilo", false, false) })
            {
                if (simd && !TriMesh.SimdAvailable) continue;
                TriMesh.SimdEnabled = simd;
                var best = long.MaxValue; var rays = 0;
                for (var rep = 0; rep < 8; rep++)
                {
                    var st = new GeoCompute.Stats();
                    var t0 = Stopwatch.StartNew();
                    if (parallel) rows = GeoCompute.Mount(scene.Targets, scene.Bodies, spec, st);
                    else
                    {
                        var sc = new RayScratch();
                        foreach (var t in scene.Targets) GeoCompute.MeasureMount(t, scene.Bodies, spec, sc, out _, out _);
                    }
                    best = Math.Min(best, t0.ElapsedTicks);
                    rays = st.Rays;
                }
                runs[label] = Math.Round(best * 1000.0 / Stopwatch.Frequency, 2);
                if (rays > 0) d["rayos"] = (long)rays;
            }
            TriMesh.SimdEnabled = true;
            d["montaje_ms"] = runs;

            // Exactitud contra lo sembrado (sabemos la holgura real de cada luminaria)
            var byClass = rows.GroupBy(r => (string)r["clase"]).ToDictionary(g => g.Key, g => (object)(long)g.Count());
            d["clases"] = byClass;
            string Expected(string planted) => planted == "flotando" ? MountClass.Floating : planted == "empotrada" ? MountClass.Embedded : MountClass.Ok;
            var wrong = new List<object>();
            for (var i = 0; i < rows.Count; i++)
            {
                if ((string)rows[i]["clase"] == Expected(scene.PlantedOf[i])) continue;
                var t = scene.Targets[i];
                wrong.Add(new Dictionary<string, object> { ["id"] = t.Id, ["sembrada"] = scene.PlantedOf[i], ["clase"] = rows[i]["clase"], ["gap"] = rows[i]["gap"], ["x"] = Math.Round(t.X, 2), ["y"] = Math.Round(t.Y, 2), ["contra"] = rows[i]["contra"] });
            }
            d["aciertos"] = Math.Round(1 - (double)wrong.Count / rows.Count, 4);
            if (wrong.Count > 0) d["distintas"] = wrong.Take(6).ToList();

            // 4) Lo que haría un script tipo pyRevit: cada luminaria × cada punto × TODOS los cuerpos × todos sus triángulos (sin índice)
            var sample = scene.Targets.Where((t, i) => i % 10 == 0).ToList();
            sw.Restart();
            long tests = 0;
            foreach (var t in sample)
                foreach (var p in Probes.Points(t.Box, 9))
                    foreach (var raw in scene.Raw)
                        tests += BruteRay(raw, p[0], p[1]);
            var bruteMs = sw.Elapsed.TotalMilliseconds;
            var bruteFull = bruteMs * scene.Targets.Count / sample.Count;
            d["fuerza_bruta"] = new Dictionary<string, object>
            {
                ["muestra"] = (long)sample.Count, ["ms_muestra"] = Math.Round(bruteMs, 1), ["ms_extrapolado_1442"] = Math.Round(bruteFull, 0),
                ["pruebas_triangulo_muestra"] = tests,
            };
            var simdPar = runs.TryGetValue("simd_paralelo", out var sp) ? Convert.ToDouble(sp) : Convert.ToDouble(runs["escalar_paralelo"]);
            d["aceleracion_vs_fuerza_bruta"] = Math.Round(bruteFull / Math.Max(0.01, simdPar), 0);

            // 5) Resumen para la IA (conteos + problemas primero, 100 filas) y su tamaño
            var cols = new List<string> { "id", "lvl", "clase", "gap", "gap_max", "pts", "contra", "pend", "cuerpo" };
            var setNames = new Dictionary<string, string> { [MountClass.Embedded] = "$empotradas", [MountClass.Floating] = "$flotando", [MountClass.NoSurface] = "$sin_superficie" };
            sw.Restart();
            var summary = GeoSummary.Build(spec, rows, cols, "clase", setNames, out var sets);
            var text = Json.Write(summary);
            d["resumen_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            d["respuesta_chars"] = (long)text.Length;
            d["respuesta_texto_compacto_chars"] = (long)CompactText.Write(summary).Length;
            var ps = spec.PageSize; spec.PageSize = 25;
            d["respuesta_compacta_25_filas_chars"] = (long)CompactText.Write(GeoSummary.Build(spec, rows, cols, "clase", setNames, out _)).Length;
            spec.PageSize = ps;
            d["respuesta_tokens_est"] = (long)Math.Round(text.Length / CharsPerToken);
            // La misma información como la imprimía un script (una línea por luminaria con id, nivel, clase y holgura)
            var naive = new StringBuilder();
            foreach (var r in rows) naive.Append(r["id"]).Append(" ").Append(r["lvl"]).Append(" ").Append(r["clase"]).Append(" gap=").Append(Convert.ToString(r["gap"], CultureInfo.InvariantCulture)).Append('\n');
            d["salida_linea_por_luminaria_chars"] = (long)naive.Length;
            d["conjuntos"] = sets.ToDictionary(k => k.Key, k => (object)(long)k.Value.Length);
            return d;
        }

        private static Scene BuildScene(int levels, int fixtures, int seed, int nx = 12, int ny = 8)
        {
            var rnd = new Random(seed);
            var s = new Scene();
            const double W = 300, D = 180, F2F = 11.0, T = 0.67;
            void AddBody(string cat, string label, TriMeshBuilder b, List<double> raw, Box3 box)
            {
                s.Bodies.Add(new GeoBody { Id = 1000 + s.Bodies.Count, LinkId = 17024537, Source = "ARCH", Category = cat, Label = label, Box = box });
                s.Builders.Add(b);
                s.Raw.Add(raw.ToArray());
                s.Triangles += raw.Count / 9;
            }
            var levelZ = new double[levels + 1];
            for (var l = 0; l <= levels; l++) levelZ[l] = l * F2F + (l > 0 ? 11.3 : 0); // L1 doble altura
            var slabBottom = new Dictionary<int, Func<double, double, double>>();
            var beams = new List<(int lvl, double x0, double y0, double x1, double y1, double zb)>();
            for (var l = 1; l <= levels; l++)
            {
                var z = levelZ[l];
                var ramp = l >= 2 && l <= 4; // rampas de estacionamiento: la losa baja 1:20 en una franja
                Func<double, double, double> bottom = (x, y) => ramp && x > 200 ? z - T - (x - 200) * 0.05 : z - T;
                slabBottom[l] = bottom;
                var b = new TriMeshBuilder(); var raw = new List<double>();
                Grid(b, raw, 0, 0, W, D, nx, ny, (x, y) => bottom(x, y) + T, up: true);
                Grid(b, raw, 0, 0, W, D, nx, ny, bottom, up: false);
                AddBody(ramp ? "Ramps" : "Floors", "Losa L" + l, b, raw, new Box3(0, 0, z - T - 5.5, W, D, z));
                // vigas bajo la losa (separadas de los ejes de luminarias para que haya de todo)
                for (var i = 0; i < 24; i++)
                {
                    var alongX = i < 14;
                    double x0, y0, x1, y1;
                    if (alongX) { var y = 6 + i * 12.5; x0 = 0; x1 = W; y0 = y; y1 = y + 1.0; }
                    else { var x = 10 + (i - 14) * 30; y0 = 0; y1 = D; x0 = x; x1 = x + 1.0; }
                    var zb = bottom((x0 + x1) / 2, (y0 + y1) / 2) - 2.0;
                    var bb = new TriMeshBuilder(); var rb = new List<double>();
                    BoxFaces(bb, rb, x0, y0, zb, x1, y1, bottom((x0 + x1) / 2, (y0 + y1) / 2));
                    AddBody("StructuralFraming", "Viga L" + l + "-" + i, bb, rb, new Box3(x0, y0, zb, x1, y1, z - T));
                    beams.Add((l, x0, y0, x1, y1, zb));
                }
                // dos escaleras (caja escalonada de 16 peldaños)
                for (var k = 0; k < 2; k++)
                {
                    var sb = new TriMeshBuilder(); var rs = new List<double>();
                    double sx = 20 + k * 240, sy = 150;
                    for (var st = 0; st < 16; st++)
                        BoxFaces(sb, rs, sx + st * 0.9, sy, levelZ[l - 1] + st * F2F / 16 - 0.6, sx + st * 0.9 + 0.9, sy + 4, levelZ[l - 1] + (st + 1) * F2F / 16);
                    AddBody("Stairs", "Escalera L" + l + "-" + k, sb, rs, new Box3(sx, sy, levelZ[l - 1] - 0.6, sx + 14.4, sy + 4, levelZ[l]));
                }
            }

            // Luminarias: bajo la losa del nivel de arriba. Holgura sembrada: 70 % a ras, 15 % flotando, 10 % empotradas, 5 % bajo viga.
            for (var i = 0; i < fixtures; i++)
            {
                var l = 1 + i % (levels - 1);           // luminaria en el nivel l, la losa de arriba es l+1
                double x, y;
                var kind = rnd.NextDouble();
                string planted;
                do { x = 5 + rnd.NextDouble() * (W - 10); y = 5 + rnd.NextDouble() * 135; }
                while (kind < 0.95 && beams.Any(b => b.lvl == l + 1 && x > b.x0 - 3 && x < b.x1 + 3 && y > b.y0 - 3 && y < b.y1 + 3));
                var strip = rnd.NextDouble() < 0.6;
                double hx = strip ? 2.0 : 0.25, hy = strip ? 0.33 : 0.25, h = strip ? 0.33 : 0.25;
                var top = slabBottom[l + 1](x, y);
                // en rampa manda la esquina más baja
                top = Math.Min(top, Math.Min(slabBottom[l + 1](x - hx, y), slabBottom[l + 1](x + hx, y)));
                if (kind < 0.70) planted = "a_ras";
                else if (kind < 0.85) { planted = "flotando"; top -= 0.1 + rnd.NextDouble() * 0.9; }
                else if (kind < 0.95) { planted = "empotrada"; top += 0.05 + rnd.NextDouble() * 0.25; }
                else
                {
                    // bajo una viga en X, lejos de las vigas en Y y fuera de la rampa (la viga es recta; la losa en rampa baja)
                    planted = "bajo_viga";
                    var beam = beams.Where(b => b.lvl == l + 1 && b.x1 - b.x0 > 100).OrderBy(_ => rnd.Next()).First();
                    do { x = 5 + rnd.NextDouble() * 190; } while (beams.Any(b => b.lvl == l + 1 && b.x1 - b.x0 < 100 && x + hx > b.x0 - 0.5 && x - hx < b.x1 + 0.5));
                    y = (beam.y0 + beam.y1) / 2; top = beam.zb;
                }
                s.Planted[planted] = s.Planted.TryGetValue(planted, out var n) ? n + 1 : 1;
                s.PlantedOf.Add(planted);
                var b2 = new TriMeshBuilder();
                var tri = new List<double>();
                BoxFaces(b2, tri, x - hx, y - hy, top - h, x + hx, y + hy, top);
                s.TargetBuilders.Add(b2);
                s.Targets.Add(new GeoTarget
                {
                    Id = 17855000 + i, Level = "LEVEL" + (l + 1).ToString("00", CultureInfo.InvariantCulture), LevelElev = levelZ[l],
                    Category = "LightingFixtures", TypeKey = strip ? "LTG_STRIP: D" : "PUCK: LT-51",
                    Box = new Box3(x - hx, y - hy, top - h, x + hx, y + hy, top), X = x, Y = y, Z = top - h,
                });
            }
            return s;
        }

        private static void Grid(TriMeshBuilder b, List<double> raw, double x0, double y0, double x1, double y1, int nx, int ny, Func<double, double, double> z, bool up)
        {
            for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++)
                {
                    double xa = x0 + (x1 - x0) * i / nx, xb = x0 + (x1 - x0) * (i + 1) / nx;
                    double ya = y0 + (y1 - y0) * j / ny, yb = y0 + (y1 - y0) * (j + 1) / ny;
                    if (up)
                    {
                        Tri(b, raw, xa, ya, z(xa, ya), xb, ya, z(xb, ya), xb, yb, z(xb, yb));
                        Tri(b, raw, xa, ya, z(xa, ya), xb, yb, z(xb, yb), xa, yb, z(xa, yb));
                    }
                    else
                    {
                        Tri(b, raw, xa, ya, z(xa, ya), xb, yb, z(xb, yb), xb, ya, z(xb, ya));
                        Tri(b, raw, xa, ya, z(xa, ya), xa, yb, z(xa, yb), xb, yb, z(xb, yb));
                    }
                }
        }

        private static void BoxFaces(TriMeshBuilder b, List<double> raw, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            Tri(b, raw, x0, y0, z0, x0, y1, z0, x1, y1, z0);
            Tri(b, raw, x0, y0, z0, x1, y1, z0, x1, y0, z0);
            Tri(b, raw, x0, y0, z1, x1, y0, z1, x1, y1, z1);
            Tri(b, raw, x0, y0, z1, x1, y1, z1, x0, y1, z1);
            Tri(b, raw, x0, y0, z0, x1, y0, z0, x1, y0, z1);
            Tri(b, raw, x0, y0, z0, x1, y0, z1, x0, y0, z1);
            Tri(b, raw, x1, y1, z0, x0, y1, z0, x0, y1, z1);
            Tri(b, raw, x1, y1, z0, x0, y1, z1, x1, y1, z1);
            Tri(b, raw, x0, y1, z0, x0, y0, z0, x0, y0, z1);
            Tri(b, raw, x0, y1, z0, x0, y0, z1, x0, y1, z1);
            Tri(b, raw, x1, y0, z0, x1, y1, z0, x1, y1, z1);
            Tri(b, raw, x1, y0, z0, x1, y1, z1, x1, y0, z1);
        }

        private static void Tri(TriMeshBuilder b, List<double> raw, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            b.Add(ax, ay, az, bx, by, bz, cx, cy, cz);
            raw.Add(ax); raw.Add(ay); raw.Add(az); raw.Add(bx); raw.Add(by); raw.Add(bz); raw.Add(cx); raw.Add(cy); raw.Add(cz);
        }

        private static void Candidates(Scene s)
        {
            const double cell = 10;
            var grid = new Dictionary<(int, int), List<int>>();
            for (var i = 0; i < s.Bodies.Count; i++)
            {
                var bx = s.Bodies[i].Box;
                for (var gx = (int)Math.Floor(bx.MinX / cell); gx <= (int)Math.Floor(bx.MaxX / cell); gx++)
                    for (var gy = (int)Math.Floor(bx.MinY / cell); gy <= (int)Math.Floor(bx.MaxY / cell); gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = new List<int>();
                        l.Add(i);
                    }
            }
            foreach (var t in s.Targets)
            {
                var found = new HashSet<int>();
                var b = t.Box;
                double z0 = b.MinZ - 1, z1 = b.MaxZ + 30;
                for (var gx = (int)Math.Floor(b.MinX / cell); gx <= (int)Math.Floor(b.MaxX / cell); gx++)
                    for (var gy = (int)Math.Floor(b.MinY / cell); gy <= (int)Math.Floor(b.MaxY / cell); gy++)
                        if (grid.TryGetValue((gx, gy), out var l))
                            foreach (var i in l)
                            {
                                var o = s.Bodies[i].Box;
                                if (o.MaxX < b.MinX || o.MinX > b.MaxX || o.MaxY < b.MinY || o.MinY > b.MaxY || o.MaxZ < z0 || o.MinZ > z1) continue;
                                found.Add(i);
                            }
                t.Candidates = found.ToArray();
            }
        }

        /// <summary>Vertical por (x,y) contra todos los triángulos de un cuerpo, sin índice. Devuelve cuántos triángulos probó.</summary>
        private static int BruteRay(double[] raw, double x, double y)
        {
            var n = raw.Length / 9;
            double sink = 0;
            for (var k = 0; k < n; k++)
            {
                var o = k * 9;
                double ax = raw[o], ay = raw[o + 1], bx = raw[o + 3], by = raw[o + 4], cx = raw[o + 6], cy = raw[o + 7];
                var d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(d) < 1e-12) continue;
                var l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
                var l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
                var l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                sink += l1 * raw[o + 2] + l2 * raw[o + 5] + l3 * raw[o + 8];
            }
            return sink > double.MaxValue ? 0 : n;
        }

        // =====================================================================================================
        // Arranque en frío: primera llamada (con JIT) vs. segunda
        // =====================================================================================================

        private static int Cold()
        {
            var total = Stopwatch.StartNew();
            var t1 = Stopwatch.StartNew();
            Workload();
            var first = t1.Elapsed.TotalMilliseconds;
            var t2 = Stopwatch.StartNew();
            Workload();
            var second = t2.Elapsed.TotalMilliseconds;
            Console.WriteLine(Json.Write(new Dictionary<string, object>
            {
                ["primera_ms"] = Math.Round(first, 1), ["segunda_ms"] = Math.Round(second, 1), ["jit_y_primer_uso_ms"] = Math.Round(first - second, 1),
                ["proceso_ms"] = total.ElapsedMilliseconds,
            }));
            return 0;
        }

        /// <summary>Lo que hace el add-in en Revit 2022–2024 a los 20 s de arrancar: precompilar motor y protocolo. Luego la carga de trabajo.</summary>
        private static int WarmupMode()
        {
            var sw = Stopwatch.StartNew();
            var n = Prepare(typeof(EngineConfig).Assembly) + Prepare(typeof(Wire).Assembly);
            var prep = sw.Elapsed.TotalMilliseconds;
            var t1 = Stopwatch.StartNew();
            Workload();
            var first = t1.Elapsed.TotalMilliseconds;
            Console.WriteLine(Json.Write(new Dictionary<string, object> { ["metodos"] = (long)n, ["precompilar_ms"] = Math.Round(prep, 1), ["primera_tras_precompilar_ms"] = Math.Round(first, 1) }));
            return 0;
        }

        private static int Prepare(System.Reflection.Assembly a)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var n = 0;
            foreach (var type in a.GetTypes())
            {
                if (type.IsInterface || type.ContainsGenericParameters) continue;
                foreach (var m in type.GetMethods(all).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(all)))
                {
                    try
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
                        n++;
                    }
                    catch (Exception) { }
                }
            }
            return n;
        }

        /// <summary>Lo que corre una consulta + una revisión geo pequeña: mallas, montaje, tabla, JSON.</summary>
        private static void Workload()
        {
            var scene = BuildScene(levels: 4, fixtures: 120, seed: 3);
            for (var i = 0; i < scene.Bodies.Count; i++) scene.Bodies[i].Mesh = scene.Builders[i].Build();
            for (var i = 0; i < scene.Targets.Count; i++) scene.Targets[i].Body = scene.TargetBuilders[i].Build();
            Candidates(scene);
            var spec = GeoSpec.Parse(new Dictionary<string, object> { ["name"] = "montaje" });
            var rows = GeoCompute.Mount(scene.Targets, scene.Bodies, spec, new GeoCompute.Stats());
            var summary = GeoSummary.Build(spec, rows, new List<string> { "id", "lvl", "clase", "gap" }, "clase", new Dictionary<string, string>(), out _);
            var q = QuerySpec.Parse(new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl,type" });
            var table = ResultTable.Build(q, SyntheticRows(500), out _);
            var text = Json.Write(new Dictionary<string, object> { ["a"] = summary, ["b"] = table });
            var back = Json.Parse(text);
            if (back == null) throw new InvalidOperationException();
        }

        // =====================================================================================================
        // Tokens: lo que ve la IA
        // =====================================================================================================

        private static List<Dictionary<string, object>> SyntheticRows(int n)
        {
            var rnd = new Random(11);
            var types = new[] { "D", "D1", "F", "G", "S", "L5", "L2", "LT-02", "LT-08E", "LT-51", "LT-06E" };
            var ws = new[] { "LIGHTING - DIST", "LIGHTING-EMERGENCY - DIST" };
            var rows = new List<Dictionary<string, object>>();
            for (var i = 0; i < n; i++)
                rows.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = 17855000L + i, ["fam"] = i % 3 == 0 ? "LTG_DOWNLIGHT-SURFACE - ROUND" : "LTG_STRIP LIGHT",
                    ["type"] = types[rnd.Next(types.Length)], ["lvl"] = "LEVEL" + (2 + i % 13).ToString("00", CultureInfo.InvariantCulture),
                    ["ws"] = ws[i % 9 == 0 ? 1 : 0], ["elev"] = Math.Round(9 + rnd.NextDouble() * 4, 4), ["cat"] = "Lighting Fixtures",
                    ["mark"] = null, ["host"] = null, ["x"] = Math.Round(rnd.NextDouble() * 300, 4), ["y"] = Math.Round(rnd.NextDouble() * 180, 4), ["z"] = Math.Round(100 + rnd.NextDouble() * 50, 4),
                });
            return rows;
        }

        private static async Task<Dictionary<string, object>> TokenBench()
        {
            var d = new Dictionary<string, object>();

            // 1) Costo fijo por sesión: lista de herramientas + instrucciones del servidor (medido del puente real)
            var bridgeDll = typeof(PDI.RevitMcp.Bridge.Program).Assembly.Location;
            var perfil = Environment.GetEnvironmentVariable("PDI_REVITMCP_PERFIL") ?? "compacto";
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "bench", Command = "dotnet", Arguments = new[] { bridgeDll },
                EnvironmentVariables = new Dictionary<string, string> { ["PDI_REVITMCP_PIPE"] = "PDI.RevitMcp.bench.nadie", ["PDI_REVITMCP_PERFIL"] = perfil },
            });
            await using (var c = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "bench", Version = "1" } }))
            {
                var tools = await c.ListToolsAsync();
                var perTool = new Dictionary<string, object>();
                long total = 0;
                foreach (var t in tools.OrderBy(t => t.Name))
                {
                    var size = t.Name.Length + (t.Description?.Length ?? 0) + t.JsonSchema.GetRawText().Length;
                    perTool[t.Name] = (long)size;
                    total += size;
                }
                var instr = (perfil == "completo" ? PDI.RevitMcp.Bridge.Program.Instructions : PDI.RevitMcp.Bridge.CompactTools.Instructions).Length;
                d["perfil"] = perfil;
                d["herramientas"] = (long)tools.Count;
                d["esquemas_chars"] = total;
                d["instrucciones_chars"] = (long)instr;
                d["costo_fijo_tokens_est"] = (long)Math.Round((total + instr) / CharsPerToken);
                d["por_herramienta_chars"] = perTool;
                var guides = Guides.Topics.ToDictionary(t => t, t => (object)(long)Guides.Get(t).Length);
                d["guias_chars"] = guides;
                d["guia_escritura_una_op_chars"] = (long)Guides.Get("escritura", "tag").Length;
            }

            // 2) Consultas típicas: respuesta del MCP vs. lo que imprime un script equivalente (lista de diccionarios JSON)
            var rows = SyntheticRows(1442);
            var cases = new Dictionary<string, object>();
            void Case(string name, Dictionary<string, object> args, Func<List<Dictionary<string, object>>, string> naive, string aiRequest)
            {
                var q = QuerySpec.Parse(args);
                var datos = (Dictionary<string, object>)ResultTable.Build(q, rows, out _);
                datos["rev"] = 4821L; datos["ms"] = 212L;
                var mcp = Json.Write(datos);
                var script = naive(rows);
                cases[name] = new Dictionary<string, object>
                {
                    ["pide_ia_chars"] = (long)aiRequest.Length,
                    ["mcp_chars"] = (long)mcp.Length,
                    ["compacto_chars"] = (long)CompactText.Write(datos).Length,
                    ["script_chars"] = (long)script.Length,
                    ["ahorro_respuesta"] = Math.Round(1 - (double)mcp.Length / script.Length, 3),
                };
            }
            Case("conteo_por_nivel",
                new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl" },
                r => Json.Write(r.GroupBy(x => (string)x["lvl"]).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => (object)(long)g.Count())),
                "{\"cat\":\"LightingFixtures\",\"group_by\":\"lvl\"}");
            Case("conteo_por_nivel_y_tipo",
                new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl,type" },
                r => Json.Write(r.GroupBy(x => (string)x["lvl"] + "|" + (string)x["type"]).OrderBy(g => g.Key)
                    .Select(g => (object)new Dictionary<string, object> { ["lvl"] = g.Key.Split('|')[0], ["type"] = g.Key.Split('|')[1], ["n"] = (long)g.Count() }).ToList()),
                "{\"cat\":\"LightingFixtures\",\"group_by\":\"lvl,type\"}");
            Case("100_filas_4_campos",
                new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["f"] = "id,type,lvl,elev", ["page_size"] = 100L },
                r => Json.Write(r.Take(100).Select(x => (object)new Dictionary<string, object> { ["id"] = x["id"], ["type"] = x["type"], ["level"] = x["lvl"], ["elevation"] = x["elev"] }).ToList()),
                "{\"cat\":\"LightingFixtures\",\"f\":\"id,type,lvl,elev\",\"page_size\":100}");
            Case("detalle_12_campos_20",
                new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["f"] = "id,cat,fam,type,lvl,ws,elev,mark,host,x,y,z", ["page_size"] = 20L },
                r => Json.Write(r.Take(20).Select(x => (object)new Dictionary<string, object>(x)).ToList()),
                "{\"ids\":\"$sin_tag\"}");
            d["consultas"] = cases;

            // 3) get f=* de 10 luminarias del mismo tipo: con "comunes" vs. un diccionario completo por elemento
            var paramsNaive = new List<object>();
            var common = new Dictionary<string, object>();
            var diffs = new List<object>();
            for (var i = 0; i < 10; i++)
            {
                var p = new Dictionary<string, object>();
                for (var k = 0; k < 58; k++) p["Param " + k.ToString("00", CultureInfo.InvariantCulture) + (k % 5 == 0 ? " Mounting Height" : "")] = k % 7 == 0 ? (object)(9.25 + k) : "valor común " + k;
                p["Comments"] = i % 2 == 0 ? "REVISAR" : "OK"; p["Mark"] = "F-" + i; p["Elevation from Level"] = 9.0 + i * 0.01;
                paramsNaive.Add(new Dictionary<string, object> { ["id"] = 17855000L + i, ["params"] = p });
                if (i == 0) foreach (var kv in p) if (!kv.Key.StartsWith("Comments") && kv.Key != "Mark" && kv.Key != "Elevation from Level") common[kv.Key] = kv.Value;
                diffs.Add(new Dictionary<string, object> { ["id"] = 17855000L + i, ["params"] = new Dictionary<string, object> { ["Comments"] = p["Comments"], ["Mark"] = p["Mark"], ["Elevation from Level"] = p["Elevation from Level"] } });
            }
            var naiveText = Json.Write(paramsNaive);
            var mcpText = Json.Write(new Dictionary<string, object> { ["comunes"] = common, ["elementos"] = diffs });
            d["parametros_10_elementos"] = new Dictionary<string, object>
            {
                ["sin_comunes_chars"] = (long)naiveText.Length, ["con_comunes_chars"] = (long)mcpText.Length,
                ["ahorro"] = Math.Round(1 - (double)mcpText.Length / naiveText.Length, 3),
            };

            // 4) Peticiones de escritura: un plan típico vs. el script medido de pyRevit (mediana F3 1583, p90 7157)
            var plan = "[{\"op\":\"snap_to_surface\",\"ids\":\"$flotando\"},{\"op\":\"tag\",\"ids\":\"$sin_tag\",\"view\":17732282}]";
            d["plan_tipico_chars"] = (long)plan.Length;
            return d;
        }
    }
}
