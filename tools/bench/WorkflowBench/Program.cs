// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Qa;
using PDI.RevitMcp.Engine.Workflows;
using PDI.RevitMcp.Protocol;

// Harness offline reproducible (0.9.1). Cada escenario BM-xx mide, con mediana, p95, throughput y bytes asignados, la parte de BIMBRIDGE
// que corre FUERA del hilo de Revit. Lo que necesita Revit (lectura/escritura reales, MEP, familias, compilación dinámica) NO se mide
// aquí: se declara en `workloads` con su especificación para la campaña nativa. Nada de esto es rendimiento de Revit ni de pyRevit.
// uso: WorkflowBench [salida.md] [salida.json] [repeticiones]
internal static class Program
{
    private sealed class Row
    {
        public string Id, Name, Variant; public int N; public double MedianMs, P95Ms, ThroughputPerS; public long AllocBytes; public string Note;
    }

    private static readonly List<Row> Rows = new List<Row>();

    private static Row Measure(string id, string name, string variant, int n, int reps, Func<object> body, string note = null)
    {
        body();                                   // calentamiento
        var times = new List<double>(); long alloc = 0;
        for (var i = 0; i < reps; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var r = body();
            times.Add(sw.Elapsed.TotalMilliseconds);
            alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            GC.KeepAlive(r);
        }
        times.Sort();
        var med = times[times.Count / 2]; var p95 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.95) - 1)];
        var row = new Row { Id = id, Name = name, Variant = variant, N = n, MedianMs = med, P95Ms = p95, ThroughputPerS = med > 0 ? n / (med / 1000.0) : 0, AllocBytes = alloc, Note = note };
        Rows.Add(row);
        Console.WriteLine(id + " " + variant + " n=" + n + " mediana " + med.ToString("0.00", CultureInfo.InvariantCulture) + " ms");
        return row;
    }

    // ---------------- datos sintéticos (semilla fija) ----------------
    private static List<ModelHealth.Element> Elements(int n, bool dense)
    {
        var r = new Random(11); var l = new List<ModelHealth.Element>(n);
        for (var i = 0; i < n; i++)
        {
            var dup = dense ? i % 50 == 0 : i % 200 == 0;
            var e = new ModelHealth.Element { Id = i + 1, Category = "Lighting Fixtures", Family = "F" + (i % 12), Type = "T" + (i % 40), Level = "L" + (i % 6),
                X = dup && i > 0 ? l[i - 1].X : r.NextDouble() * 500, Y = dup && i > 0 ? l[i - 1].Y : r.NextDouble() * 300, Z = 9 };
            e.Params["Circuit Number"] = i % 7 == 0 ? "" : "C" + i;
            l.Add(e);
        }
        return l;
    }

    // Línea base: la implementación anterior de la detección (grupo con List.Contains), copiada para medir el «antes».
    private static List<List<long>> DuplicatesBefore(IEnumerable<ModelHealth.Element> elements, double tol = ModelHealth.DuplicateTolerance)
    {
        var groups = new List<List<long>>();
        foreach (var g in elements.Where(e => e.HasLocation && !double.IsNaN(e.X)).GroupBy(e => (e.Category ?? "") + "|" + e.Family + "|" + e.Type + "|" + e.Level, StringComparer.Ordinal))
        {
            var cell = new Dictionary<(long, long, long), List<ModelHealth.Element>>();
            (long, long, long) Key(ModelHealth.Element e) => ((long)Math.Floor(e.X / tol), (long)Math.Floor(e.Y / tol), (long)Math.Floor(e.Z / tol));
            foreach (var e in g) { var k = Key(e); if (!cell.TryGetValue(k, out var l)) cell[k] = l = new List<ModelHealth.Element>(); l.Add(e); }
            var seen = new HashSet<long>();
            foreach (var e in g.OrderBy(x => x.Id))
            {
                if (seen.Contains(e.Id)) continue;
                var (cx, cy, cz) = Key(e);
                var grp = new List<long> { e.Id };
                for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                {
                    if (!cell.TryGetValue((cx + dx, cy + dy, cz + dz), out var near)) continue;
                    foreach (var o in near)
                    {
                        if (o.Id == e.Id || seen.Contains(o.Id) || grp.Contains(o.Id)) continue;
                        var dist = Math.Sqrt((o.X - e.X) * (o.X - e.X) + (o.Y - e.Y) * (o.Y - e.Y) + (o.Z - e.Z) * (o.Z - e.Z));
                        if (dist <= tol) grp.Add(o.Id);
                    }
                }
                if (grp.Count > 1) { foreach (var id in grp) seen.Add(id); groups.Add(grp); }
            }
        }
        return groups;
    }

    private static Dictionary<string, object> PlaceOp(int i) => new Dictionary<string, object>
    {
        ["op"] = "place", ["type"] = "F: T" + (i % 5), ["level"] = "LEVEL 02", ["elev"] = 9.25,
        ["puntos"] = new List<object> { new List<object> { (double)i, 1.0 }, new List<object> { (double)i, 2.0 } },
    };

    private static int Main(string[] args)
    {
        var reps = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 7;

        // BM-03 Model Health (análisis fuera de Revit) en tamaños distintos, y duplicados denso antes/después.
        foreach (var n in new[] { 1000, 10000, 50000 })
        {
            var els = Elements(n, false);
            Measure("BM-03", "Model Health: informe (duplicados, parámetros, conectores)", "informe", n, reps, () => ModelHealth.Report(null, els, new List<ModelHealth.Connector>(), null, null, new[] { "Circuit Number" }));
        }
        foreach (var n in new[] { 10000, 50000 })
        {
            var els = Elements(n, true);
            Measure("BM-03", "Duplicados en modelo denso", "antes (List.Contains)", n, reps, () => DuplicatesBefore(els));
            Measure("BM-03", "Duplicados en modelo denso", "después (HashSet)", n, reps, () => ModelHealth.Duplicates(els));
        }
        var cluster = Enumerable.Range(0, 20000).Select(i => new ModelHealth.Element { Id = i + 1, Category = "C", Family = "F", Type = "T", Level = "L", X = 5, Y = 5, Z = 9 }).ToList();
        Measure("BM-03", "Duplicados: un único grupo de 20.000 coincidentes", "antes (List.Contains)", cluster.Count, Math.Min(reps, 3), () => DuplicatesBefore(cluster));
        Measure("BM-03", "Duplicados: un único grupo de 20.000 coincidentes", "después (HashSet)", cluster.Count, reps, () => ModelHealth.Duplicates(cluster));

        // BM-06 Escrituras por lote: validar N operaciones sueltas (N llamadas a plan) frente a una operación compuesta (1 llamada con N ops).
        foreach (var n in new[] { 10, 50, 200 })
        {
            var ops = Enumerable.Range(0, n).Select(PlaceOp).ToList();
            Measure("BM-06", "Validación offline de N colocaciones", "N planes de 1 op", n, reps, () => { var c = 0; foreach (var o in ops) c += OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = new List<object> { o } }).Count; return c; },
                "mide sólo la validación offline; cada plan adicional en Revit añade una visita al hilo y una transacción (no medido aquí)");
            Measure("BM-06", "Validación offline de N colocaciones", "1 plan de N ops", n, reps, () => OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = ops.Cast<object>().ToList() }).Count);
        }

        // BM-07 Copy/Monitor: verificación del fixture sintético de 88 aparatos (y 10.000), sin la intervención manual.
        foreach (var n in new[] { 88, 10000 })
        {
            var src = Enumerable.Range(0, n).Select(i => new SourceFixture { UniqueId = "S" + i, Category = "PlumbingFixtures", Family = "F" + (i % 5), Type = "T", Level = "L1", X = (i % 100) * 6.0, Y = (i / 100) * 8.0 }).ToList();
            var tf = new LinkTransform { Dx = 100, Dy = -20, RotationDeg = 30 };
            long id = 5000;
            var host = src.Select(s => { var p = tf.Apply(s.X, s.Y, s.Z); return new HostFixture { Id = id++, Family = s.Family, Type = s.Type, X = p.X, Y = p.Y, Z = p.Z, RotationDeg = tf.RotateAngle(0), Monitoring = true, MonitoredLinkInstances = new List<long> { 77 } }; }).ToList();
            Measure("BM-07", "Copy/Monitor: verificación de la copia (sin la parte manual)", "verificar", n, reps, () => CopyMonitorVerifier.Verify(src, new List<HostFixture>(), host, tf, null, 77));
            var e = new CopyMonitorExpectations { HostIdentity = "h", LinkIdentity = "l", Category = "PlumbingFixtures", Transform = tf, Source = src };
            Measure("BM-07", "Copy/Monitor: congelar y comparar expectativas", "huella + diff", n, reps, () => { var f = e.Fingerprint; return e.Diff(e).Count + f.Length; });
        }

        // BM-09 Agentes concurrentes: reparto del planificador con 1, 2, 4 y 8 agentes (operaciones sintéticas de 5 ms).
        foreach (var agents in new[] { 1, 2, 4, 8 })
        {
            Measure("BM-09", "Planificador: 64 operaciones repartidas entre agentes", agents + " agente(s)", 64, Math.Min(reps, 5), () =>
            {
                var s = new OperationScheduler { MaxRunning = 4, MaxPerOwner = 2, MaxQueued = 1000 };
                var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() => s.Run("agente" + (i % agents), "doc" + (i % 3), async () => { await Task.Delay(5); return i; }, CancellationToken.None))).ToArray();
                Task.WaitAll(tasks);
                return s.Stats();
            }, "espera sintética de 5 ms por operación: mide el coste de coordinación y el reparto, no trabajo en Revit");
        }

        // Instrumentación: coste de armar y serializar las métricas de un workflow (no debe pesar frente al workflow).
        Measure("INS", "Armar y serializar WorkflowMetrics", "1 workflow", 1, reps * 20, () => Json.Write(WorkflowMetrics.FromMainThread(100, 5, 80, 4, 30).Observe("elements_read", 1000).Observe("cache_hits", 900).Observe("cache_misses", 100).ToJson()));

        var md = new StringBuilder();
        md.AppendLine("# Benchmarks offline de workflows (0.9.1)");
        md.AppendLine();
        md.AppendLine("Generado por `tools/bench/WorkflowBench` en " + Environment.ProcessorCount + " CPU lógicas, " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ". Mediana y p95 de " + reps + " repeticiones tras calentamiento; semilla fija.");
        md.AppendLine("**Mide sólo lo que BIMBRIDGE hace fuera del hilo de Revit.** No es rendimiento de Revit ni de pyRevit; las cifras «antes/después» comparan dos implementaciones del mismo cálculo en este equipo.");
        md.AppendLine();
        md.AppendLine("| Escenario | Variante | N | Mediana ms | p95 ms | Throughput /s | Asignado KB | Nota |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in Rows)
            md.AppendLine("| " + r.Id + " " + r.Name + " | " + r.Variant + " | " + r.N + " | " + r.MedianMs.ToString("0.00", CultureInfo.InvariantCulture) + " | " + r.P95Ms.ToString("0.00", CultureInfo.InvariantCulture) + " | " +
                          r.ThroughputPerS.ToString("0", CultureInfo.InvariantCulture) + " | " + r.AllocBytes / 1024 + " | " + (r.Note ?? "") + " |");
        md.AppendLine();
        md.AppendLine("## Antes → después (misma máquina, mismo dato)");
        foreach (var g in Rows.Where(r => r.Variant.StartsWith("antes")).Select(r => r))
        {
            var after = Rows.First(r => r.Name == g.Name && r.N == g.N && r.Variant.StartsWith("después"));
            md.AppendLine("- " + g.Name + " (N=" + g.N + "): " + g.MedianMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms → " + after.MedianMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms (" + (g.MedianMs / Math.Max(0.001, after.MedianMs)).ToString("0.0", CultureInfo.InvariantCulture) + "×)");
        }
        var outMd = args.Length > 0 ? args[0] : "workflow-bench.md";
        File.WriteAllText(outMd, md.ToString(), new UTF8Encoding(false));
        var json = new Dictionary<string, object>
        {
            ["esquema"] = "bimbridge.workflow_bench/1", ["alcance"] = "solo_fuera_del_hilo_de_revit",
            ["resultados"] = Rows.Select(r => (object)new Dictionary<string, object> { ["id"] = r.Id, ["nombre"] = r.Name, ["variante"] = r.Variant, ["n"] = (long)r.N, ["mediana_ms"] = r.MedianMs, ["p95_ms"] = r.P95Ms, ["throughput_s"] = r.ThroughputPerS, ["asignado_bytes"] = r.AllocBytes }).ToList(),
            ["workloads_nativos"] = Workloads.Select(w => (object)w).ToList(),
        };
        File.WriteAllText(args.Length > 1 ? args[1] : "workflow-bench.json", Json.Write(json), new UTF8Encoding(false));
        Console.WriteLine(md.ToString());
        return 0;
    }

    /// <summary>Especificación de los workloads que sólo se pueden medir dentro de Revit (campaña nativa). Mismos parámetros para BIMBRIDGE y pyRevit.</summary>
    private static readonly List<Dictionary<string, object>> Workloads = new List<Dictionary<string, object>>
    {
        W("BM-01", "Lectura masiva", "1000, 10000, 50000 elementos; lectura completa y paginada", "mismo conjunto de campos (id, fam, type, lvl, 3 parámetros)", "filas iguales (conteo y hash del conjunto)"),
        W("BM-02", "Parámetros", "leer y modificar 1 parámetro en 100, 1000, 5000 instancias", "mismo parámetro y valores", "relectura: 100 % de valores como se pidieron"),
        W("BM-03n", "Model Health", "modelos sintéticos pequeño/mediano/grande", "mismas reglas (avisos, duplicados, parámetros vacíos, conectores libres)", "mismos ids por hallazgo"),
        W("BM-04", "Familias", "colocar y modificar instancias repetidas (100, 1000)", "mismo tipo, nivel y puntos", "posición y tipo verificados"),
        W("BM-05", "MEP", "consultar sistemas, conectores, equipos, circuitos y relaciones", "mismas categorías", "mismos conteos y relaciones"),
        W("BM-06n", "Escrituras por lotes", "N operaciones individuales frente a una compuesta equivalente", "mismo resultado en el modelo", "diff de relectura idéntico"),
        W("BM-07n", "Copy/Monitor", "preflight y verificación del fixture de 88 aparatos; la intervención manual se cronometra APARTE", "fixture sintético redistribuible", "verificado/fallido con la misma regla; no se compara como automatización nativa terminada"),
        W("BM-08", "PDF→Revit", "extracción, registro, planificación y aplicación con tiempo separado por etapa", "mismo PDF, mismas reglas de tipo", "verificación independiente de colocación"),
        W("BM-09n", "Agentes concurrentes", "1, 2, 4 y 8 agentes sobre documentos y operaciones compatibles", "misma carga total", "sin duplicados ni pérdidas; orden de escritura intacto"),
        W("BM-10", "Compilación dinámica", "compilación en frío, en caliente, caché y ejecución por separado", "mismo script", "mismo resultado"),
    };

    private static Dictionary<string, object> W(string id, string name, string size, string sameInput, string correctness) =>
        new Dictionary<string, object> { ["id"] = id, ["nombre"] = name, ["tamanos"] = size, ["misma_entrada"] = sameInput, ["correccion"] = correctness };
}