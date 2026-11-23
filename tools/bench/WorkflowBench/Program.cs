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
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Engine.Workflows;
using Bimbridge.Protocol;

// Reproducible offline harness (0.9.1). Each BM-xx scenario measures, with median, p95, throughput and allocated bytes, the part of BIMBRIDGE
// that runs OUTSIDE the Revit thread. What needs Revit (real reads/writes, MEP, families, dynamic compilation) is NOT measured
// here: it is declared in `workloads` with its specification for the native campaign. None of this is Revit or pyRevit performance.
// usage: WorkflowBench [output.md] [output.json] [repetitions]
internal static class Program
{
    private sealed class Row
    {
        public string Id, Name, Variant; public int N; public double MedianMs, P95Ms, ThroughputPerS; public long AllocBytes; public string Note;
    }

    private static readonly List<Row> Rows = new List<Row>();

    private static Row Measure(string id, string name, string variant, int n, int reps, Func<object> body, string note = null)
    {
        body();                                   // warm-up
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
        Console.WriteLine(id + " " + variant + " n=" + n + " median " + med.ToString("0.00", CultureInfo.InvariantCulture) + " ms");
        return row;
    }

    // ---------------- synthetic data (fixed seed) ----------------
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

    // Baseline: the previous detection implementation (group with List.Contains), copied to measure the "before".
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

        foreach (var n in new[] { 1000, 10000, 50000 })
        {
            var source = Elements(n, false);
            var spec = QuerySpec.Parse(new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl,type" });
            Dictionary<string, object> Project(ModelHealth.Element e) => new Dictionary<string, object>
            {
                ["lvl"] = e.Level, ["type"] = e.Type,
            };
            object Legacy() => ResultTable.Build(spec, source.Select(Project), out _);
            object Streaming()
            {
                var groups = new ResultTable.GroupAccumulator(spec);
                foreach (var e in source) groups.Add(Project(e));
                return groups.Render();
            }
            var check = Json.Write(Legacy());
            if (check != Json.Write(Streaming())) throw new InvalidOperationException("BM-01: the groupings differ");
            var note = "same count, order and groups; SHA-256 " + Home.Sha256Hex(check) + "; offline projection and aggregation only";
            Measure("BM-01", "Grouped inventory", "materialized", n, Math.Max(30, reps), Legacy, note);
            Measure("BM-01", "Grouped inventory", "incremental", n, Math.Max(30, reps), Streaming, note);
        }

        // BM-03 Model Health (analysis outside Revit) at different sizes, and dense duplicates before/after.
        foreach (var n in new[] { 1000, 10000, 50000 })
        {
            var els = Elements(n, false);
            Measure("BM-03", "Model Health: report (duplicates, parameters, connectors)", "report", n, reps, () => ModelHealth.Report(null, els, new List<ModelHealth.Connector>(), null, null, new[] { "Circuit Number" }));
        }
        foreach (var n in new[] { 10000, 50000 })
        {
            var els = Elements(n, true);
            Measure("BM-03", "Duplicates in a dense model", "before (List.Contains)", n, reps, () => DuplicatesBefore(els));
            Measure("BM-03", "Duplicates in a dense model", "after (HashSet)", n, reps, () => ModelHealth.Duplicates(els));
        }
        var cluster = Enumerable.Range(0, 20000).Select(i => new ModelHealth.Element { Id = i + 1, Category = "C", Family = "F", Type = "T", Level = "L", X = 5, Y = 5, Z = 9 }).ToList();
        Measure("BM-03", "Duplicates: a single group of 20,000 coincident elements", "before (List.Contains)", cluster.Count, Math.Min(reps, 3), () => DuplicatesBefore(cluster));
        Measure("BM-03", "Duplicates: a single group of 20,000 coincident elements", "after (HashSet)", cluster.Count, reps, () => ModelHealth.Duplicates(cluster));

        // BM-06 Batch writes: validate N separate operations (N plan calls) versus one compound operation (1 call with N ops).
        foreach (var n in new[] { 10, 50, 200 })
        {
            var ops = Enumerable.Range(0, n).Select(PlaceOp).ToList();
            Measure("BM-06", "Offline validation of N placements", "N plans of 1 op", n, reps, () => { var c = 0; foreach (var o in ops) c += OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = new List<object> { o } }).Count; return c; },
                "measures offline validation only; each extra plan in Revit adds one trip to the thread and one transaction (not measured here)");
            Measure("BM-06", "Offline validation of N placements", "1 plan of N ops", n, reps, () => OpSpec.ParseAll(new Dictionary<string, object> { ["ops"] = ops.Cast<object>().ToList() }).Count);
        }

        // BM-07 Copy/Monitor: verification of the synthetic 88-fixture test set (and 10,000), without the manual step.
        foreach (var n in new[] { 88, 10000 })
        {
            var src = Enumerable.Range(0, n).Select(i => new SourceFixture { UniqueId = "S" + i, Category = "PlumbingFixtures", Family = "F" + (i % 5), Type = "T", Level = "L1", X = (i % 100) * 6.0, Y = (i / 100) * 8.0 }).ToList();
            var tf = new LinkTransform { Dx = 100, Dy = -20, RotationDeg = 30 };
            long id = 5000;
            var host = src.Select(s => { var p = tf.Apply(s.X, s.Y, s.Z); return new HostFixture { Id = id++, Family = s.Family, Type = s.Type, X = p.X, Y = p.Y, Z = p.Z, RotationDeg = tf.RotateAngle(0), Monitoring = true, MonitoredLinkInstances = new List<long> { 77 } }; }).ToList();
            Measure("BM-07", "Copy/Monitor: verification of the copy (without the manual part)", "verify", n, reps, () => CopyMonitorVerifier.Verify(src, new List<HostFixture>(), host, tf, null, 77));
            var e = new CopyMonitorExpectations { HostIdentity = "h", LinkIdentity = "l", Category = "PlumbingFixtures", Transform = tf, Source = src };
            Measure("BM-07", "Copy/Monitor: freeze and compare expectations", "fingerprint + diff", n, reps, () => { var f = e.Fingerprint; return e.Diff(e).Count + f.Length; });
        }

        // BM-09 Concurrent agents: scheduler distribution with 1, 2, 4 and 8 agents (synthetic 5 ms operations).
        foreach (var agents in new[] { 1, 2, 4, 8 })
        {
            Measure("BM-09", "Scheduler: 64 operations distributed among agents", agents + " agent(s)", 64, Math.Min(reps, 5), () =>
            {
                var s = new OperationScheduler { MaxRunning = 4, MaxPerOwner = 2, MaxQueued = 1000 };
                var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() => s.Run("agent" + (i % agents), "doc" + (i % 3), async () => { await Task.Delay(5); return i; }, CancellationToken.None))).ToArray();
                Task.WaitAll(tasks);
                return s.Stats();
            }, "synthetic 5 ms wait per operation: measures coordination and distribution cost, not work in Revit");
        }

        // Instrumentation: cost of building and serializing a workflow's metrics (must be negligible next to the workflow).
        Measure("INS", "Build and serialize WorkflowMetrics", "1 workflow", 1, reps * 20, () => Json.Write(WorkflowMetrics.FromMainThread(100, 5, 80, 4, 30).Observe("elements_read", 1000).Observe("cache_hits", 900).Observe("cache_misses", 100).ToJson()));

        var md = new StringBuilder();
        md.AppendLine("# Offline workflow benchmarks (0.9.1)");
        md.AppendLine();
        md.AppendLine("Generated by `tools/bench/WorkflowBench` on " + Environment.ProcessorCount + " logical CPUs, " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ". Median and p95 of " + reps + " repetitions after warm-up; fixed seed.");
        md.AppendLine("**Measures only what BIMBRIDGE does outside the Revit thread.** It is not Revit or pyRevit performance; the \"before/after\" figures compare two implementations of the same computation on this machine.");
        md.AppendLine();
        md.AppendLine("| Scenario | Variant | N | Median ms | p95 ms | Throughput /s | Allocated KB | Note |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in Rows)
            md.AppendLine("| " + r.Id + " " + r.Name + " | " + r.Variant + " | " + r.N + " | " + r.MedianMs.ToString("0.00", CultureInfo.InvariantCulture) + " | " + r.P95Ms.ToString("0.00", CultureInfo.InvariantCulture) + " | " +
                          r.ThroughputPerS.ToString("0", CultureInfo.InvariantCulture) + " | " + r.AllocBytes / 1024 + " | " + (r.Note ?? "") + " |");
        md.AppendLine();
        md.AppendLine("## Before → after (same machine, same data)");
        foreach (var g in Rows.Where(r => r.Variant.StartsWith("before")).Select(r => r))
        {
            var after = Rows.First(r => r.Name == g.Name && r.N == g.N && r.Variant.StartsWith("after"));
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

    /// <summary>Specification of the workloads that can only be measured inside Revit (native campaign). Same parameters for BIMBRIDGE and pyRevit.</summary>
    private static readonly List<Dictionary<string, object>> Workloads = new List<Dictionary<string, object>>
    {
        W("BM-01", "Bulk read", "1000, 10000, 50000 elements; full and paged read", "same set of fields (id, fam, type, lvl, 3 parameters)", "identical rows (count and hash of the set)"),
        W("BM-02", "Parameters", "read and modify 1 parameter on 100, 1000, 5000 instances", "same parameter and values", "re-read: 100 % of values as requested"),
        W("BM-03n", "Model Health", "small/medium/large synthetic models", "same rules (warnings, duplicates, empty parameters, free connectors)", "same ids per finding"),
        W("BM-04", "Families", "place and modify repeated instances (100, 1000)", "same type, level and points", "position and type verified"),
        W("BM-05", "MEP", "query systems, connectors, equipment, circuits and relationships", "same categories", "same counts and relationships"),
        W("BM-06n", "Batch writes", "N individual operations versus one equivalent compound operation", "same result in the model", "identical re-read diff"),
        W("BM-07n", "Copy/Monitor", "preflight and verification of the 88-fixture test set; the manual step is timed SEPARATELY", "redistributable synthetic test set", "verified/failed with the same rule; not compared as finished native automation"),
        W("BM-08", "PDF→Revit", "extraction, registration, planning and apply, timed separately per stage", "same PDF, same type rules", "independent placement verification"),
        W("BM-09n", "Concurrent agents", "1, 2, 4 and 8 agents on compatible documents and operations", "same total load", "no duplicates or losses; write order intact"),
        W("BM-10", "Dynamic compilation", "cold compilation, warm compilation, cache and execution separately", "same script", "same result"),
    };

    private static Dictionary<string, object> W(string id, string name, string size, string sameInput, string correctness) =>
        new Dictionary<string, object> { ["id"] = id, ["nombre"] = name, ["tamanos"] = size, ["misma_entrada"] = sameInput, ["correccion"] = correctness };
}
