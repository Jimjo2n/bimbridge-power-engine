// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Engine.Coverage;
using PDI.RevitMcp.Protocol;

// Benchmarks offline del índice de capacidades (0.9.1). Catálogos sintéticos de 500, 1.000, 5.000 y 10.000 entradas más el real.
// Mide en este equipo: construcción del índice, búsquedas, memoria retenida y asignada, y bytes de la respuesta serializada como la
// enviaría el add-in (Response → JSON). Los tokens son una ESTIMACIÓN (bytes / 4). Nada de esto mide Revit.
// uso: CapabilityBench [salida.md]
internal static class Program
{
    private static readonly string[] Words = { "muro", "suelo", "techo", "conducto", "tuberia", "circuito", "cuadro", "vista", "tabla", "lamina", "familia", "vinculo", "nivel", "rejilla", "habitacion", "cota", "etiqueta", "exportar", "fase", "grupo" };
    private static readonly string[] Strategies = { "typed:place", "query:query", "doc:pdf", "exec", "engine", "postcommand", "none" };

    private static List<CapabilityEntry> Synthetic(int n)
    {
        var r = new Random(42);
        var l = new List<CapabilityEntry>(n);
        for (var i = 0; i < n; i++)
        {
            var dom = "F" + (1 + i % 37).ToString("00", CultureInfo.InvariantCulture);
            var w1 = Words[r.Next(Words.Length)]; var w2 = Words[r.Next(Words.Length)];
            l.Add(new CapabilityEntry
            {
                Id = dom + "-SYN-" + i.ToString("000000", CultureInfo.InvariantCulture).Substring(0, 3) + i.ToString(CultureInfo.InvariantCulture) + "-" + (i % 1000).ToString("000", CultureInfo.InvariantCulture),
                Domain = dom, Subdomain = w1, Name = "Operación " + w1 + " " + w2 + " " + i, NameEn = "Synthetic op " + i, Operation = "CREATE",
                Expected = "resultado esperado de la operación sintética " + i, Strategy = Strategies[i % Strategies.Length],
                Status = (CoverageStatus)(i % 11), Categories = new[] { "Cat" + (i % 50) }, MinVersion = 2022 + i % 3, MaxVersion = 2026,
                Implementation = new[] { "src/x.cs" }, Tests = new[] { "XTests" }, Verifier = "verificador", Limitations = "limitación de ejemplo", Priority = "P" + (i % 4),
                PostableCommands = new[] { "Cmd" + (i % 600) },
            });
        }
        return l;
    }

    /// <summary>Memoria gestionada que retiene un índice recién construido (montón tras GC completo, con y sin él).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long Retained(List<CapabilityEntry> entries)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        var before = GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes;
        var x = new CapabilityIndex(entries);
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        var after = GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes;
        GC.KeepAlive(x);
        return after - before;
    }

    private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

    private static double TimeUs(Action a, int reps)
    {
        for (var i = 0; i < Math.Min(50, reps); i++) a();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < reps; i++) a();
        return sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
    }

    private static int Bytes(object datos) => Encoding.UTF8.GetByteCount(Json.Write(new Response { Id = 1, Datos = (Dictionary<string, object>)datos }.ToJson()));

    private static Dictionary<string, object> Page(CapabilityIndex idx, CapabilityQuery q)
    {
        var (page, total) = idx.Search(q);
        return new Dictionary<string, object> { ["total"] = (long)total, ["cols"] = CapabilityIndex.RowColumns, ["rows"] = page.Select(c => (object)CapabilityIndex.Row(c)).ToList() };
    }

    private static int Main(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmarks offline del índice de capacidades (0.9.1)");
        sb.AppendLine();
        sb.AppendLine("Generado por `tools/bench/CapabilityBench` en " + Environment.ProcessorCount + " CPU lógicas, " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
                      ", " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ". Catálogos sintéticos (semilla fija) y el catálogo real.");
        sb.AppendLine("No mide Revit. Tokens = bytes/4 (estimación). Tiempos: mediana de 5 construcciones; búsquedas: media por llamada.");
        sb.AppendLine();
        sb.AppendLine("| Catálogo | Construcción ms | Retenido KB | Asignado KB | Por id µs | Por dominio µs | Texto µs | Versión+contexto µs | Página 20 filas bytes | Tokens est. | Catálogo completo bytes | Tokens est. completo |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        var extra = new StringBuilder();
        var sets = new List<(string Name, List<CapabilityEntry> Entries)> { ("real (" + CapabilityCatalog.All.Count + ")", CapabilityCatalog.All.ToList()) };
        foreach (var n in new[] { 500, 1000, 5000, 10000 }) sets.Add((n.ToString(CultureInfo.InvariantCulture), Synthetic(n)));
        foreach (var (name, entries) in sets)
        {
            var builds = new List<double>();
            CapabilityIndex idx = null;
            long retained = 0, allocated = 0;
            for (var k = 0; k < 5; k++)
            {
                idx = null;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalMemory(true);
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                idx = new CapabilityIndex(entries);
                builds.Add(sw.Elapsed.TotalMilliseconds);
                allocated = GC.GetAllocatedBytesForCurrentThread() - alloc0;
                retained = GC.GetTotalMemory(true) - before;
                GC.KeepAlive(idx);
            }
            retained = Retained(entries);
            var ids = entries.Select(e => e.Id).ToArray();
            var rnd = new Random(7);
            var byId = TimeUs(() => idx.Get(ids[rnd.Next(ids.Length)]), 100000);
            var byDomain = TimeUs(() => idx.Search(new CapabilityQuery { Domain = "F20" }), 2000);
            var byText = TimeUs(() => idx.Search(new CapabilityQuery { Text = "tuber" }), 2000);
            var byCtx = TimeUs(() => idx.Search(new CapabilityQuery { Version = 2023, DocumentKind = "project", Domain = "F17" }), 2000);
            var pageBytes = Bytes(Page(idx, new CapabilityQuery { Domain = "F20" }));
            var full = new Dictionary<string, object> { ["capacidades"] = entries.Select(e => (object)CapabilityIndex.Contract(e)).ToList() };
            var fullBytes = Bytes(full);
            sb.AppendLine("| " + name + " | " + Median(builds).ToString("0.00", CultureInfo.InvariantCulture) + " | " + (retained / 1024) + " | " + (allocated / 1024) + " | " +
                          byId.ToString("0.000", CultureInfo.InvariantCulture) + " | " + byDomain.ToString("0.0", CultureInfo.InvariantCulture) + " | " + byText.ToString("0.0", CultureInfo.InvariantCulture) + " | " +
                          byCtx.ToString("0.0", CultureInfo.InvariantCulture) + " | " + pageBytes + " | " + (pageBytes / 4) + " | " + fullBytes + " | " + (fullBytes / 4) + " |");
            // AUD-15/16: huella canónica (perezosa, una vez), serialización de la página y del contrato, y primera consulta en frío.
            var swv = Stopwatch.StartNew(); var ver = new CapabilityIndex(entries).Version; var verMs = swv.Elapsed.TotalMilliseconds;
            var pg = Page(idx, new CapabilityQuery { Domain = "F20" });
            var serPage = TimeUs(() => Bytes(pg), 2000);
            var one = CapabilityIndex.Contract(entries[entries.Count / 2]);
            var serOne = TimeUs(() => Bytes(one), 2000);
            var oneBytes = Bytes(one);
            extra.AppendLine("| " + name + " | " + verMs.ToString("0.00", CultureInfo.InvariantCulture) + " | " + serPage.ToString("0.0", CultureInfo.InvariantCulture) + " | " +
                             serOne.ToString("0.0", CultureInfo.InvariantCulture) + " | " + oneBytes + " | " + (oneBytes / 4) + " | " + GC.GetGCMemoryInfo().HeapSizeBytes / 1024 + " |");
            GC.KeepAlive(ver);
            Console.WriteLine(name + " ok");
        }
        sb.AppendLine();
        sb.AppendLine("## Huella, serialización y memoria del proceso");
        sb.AppendLine();
        sb.AppendLine("| Catálogo | Huella canónica (construir + SHA-256) ms | Serializar página µs | Serializar un contrato µs | Contrato bytes | Tokens est. | Montón del proceso KB (pico observado tras la serie) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        sb.Append(extra);
        sb.AppendLine();
        sb.AppendLine("La fila «real» es el catálogo del producto en este commit. La huella es perezosa: no entra en la construcción ni en la carga del add-in; se calcula la primera vez que se pide `catalogo`.");
        sb.AppendLine();
        sb.AppendLine("Lectura: el descubrimiento devuelve una página compacta de tamaño casi constante; serializar el catálogo completo crece linealmente y por eso nunca se envía entero (contrato completo sólo con `check name=cobertura id=`).");
        var outPath = args.Length > 0 ? args[0] : "inventory-bench.md";
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        return 0;
    }
}
