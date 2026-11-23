// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimbridge.Protocol;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Power Engine benchmark (no Revit): C# vs Rust FFI and Rust IPC per job and size, Python vs .NET on DOCX,
    /// fixed cost of each path and text efficiency (on-demand discovery and batch vs individual calls).
    /// It measures; it does not assume. The calibration (from which n Rust FFI wins) comes from these numbers.
    /// "Tokens" are ESTIMATED (characters/4): without the client's counter there are no real tokens, and it says so.
    /// </summary>
    public static class PowerEngineBench
    {
        public sealed class Settings
        {
            public int[] Sizes = { 100, 1000, 10000, 100000 };
            public int[] SegmentSizes = { 100, 1000, 5000, 20000 };
            public int Rounds = 5;
            public string RustWorkerExe, PythonExe, PythonWorkerScript, DocxPath, PdfPath;
            public bool IncludeIpc = true;
        }

        private static readonly string[] Jobs = { "transform3", "nearest", "match", "duplicates", "segment_contacts", "components" };

        public static Dictionary<string, object> Run(Settings s)
        {
            var engines = new List<string> { "csharp" };
            if (RustGeometryEngine.Available) engines.Add("rust_ffi");
            if (s.IncludeIpc && s.RustWorkerExe != null && File.Exists(s.RustWorkerExe)) engines.Add("rust_ipc");
            var pes = engines.ToDictionary(e => e, e => new PowerEngineDispatcher(new PowerEngineOptions { Geometry = e, RustWorkerExe = s.RustWorkerExe }));
            try
            {
                var geo = new List<object>();
                var med = new Dictionary<(string job, string eng, int n), double>();
                foreach (var job in Jobs)
                    foreach (var n in job == "segment_contacts" ? s.SegmentSizes : s.Sizes)
                    {
                        var input = Input(job, n);
                        var row = new Dictionary<string, object> { ["job"] = job, ["n"] = (long)n };
                        string reference = null;
                        foreach (var e in engines)
                        {
                            // IPC with 100,000 points in JSON mostly measures serialization: capped so the campaign does not drag on
                            if (e == "rust_ipc" && n > 20000) { row[e] = null; continue; }
                            var pe = pes[e];
                            string sig = null;
                            Exec(pe, job, input, out sig);                         // warm-up (JIT, IPC process startup)
                            var times = new List<double>();
                            for (var r = 0; r < s.Rounds; r++)
                            {
                                var sw = Stopwatch.StartNew();
                                Exec(pe, job, input, out sig);
                                times.Add(sw.Elapsed.TotalMilliseconds);
                            }
                            times.Sort();
                            var m = times[times.Count / 2];
                            med[(job, e, n)] = m;
                            row[e] = Math.Round(m, 3);
                            if (reference == null) reference = sig; else if (sig != reference) row["difference_" + e] = "result differs from C#";
                        }
                        if (row.TryGetValue("rust_ffi", out var f) && f != null) row["rust_ffi_vs_csharp"] = Math.Round(med[(job, "csharp", n)] / med[(job, "rust_ffi", n)], 2);
                        geo.Add(row);
                    }
                var cal = Calibrate(med, engines.Contains("rust_ffi"), s);
                var d = new Dictionary<string, object>
                {
                    ["date_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["machine"] = new Dictionary<string, object> { ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ["cpus"] = (long)Environment.ProcessorCount, ["dotnet"] = Environment.Version.ToString() },
                    ["rounds"] = (long)s.Rounds, ["engines"] = engines.Cast<object>().ToList(),
                    ["geometry_ms_median"] = geo,
                    ["fixed_cost_ms"] = FixedCost(pes),
                    ["calibration"] = cal.ToJson(),
                    ["documents"] = Docs(s),
                    ["text"] = TextEfficiency(),
                };
                return d;
            }
            finally { foreach (var p in pes.Values) p.Dispose(); }
        }

        /// <summary>Smallest measured n from which Rust FFI beats C# at that size and at all larger sizes; otherwise, always C#.</summary>
        public static Calibration Calibrate(Dictionary<(string job, string eng, int n), double> med, bool haveFfi, Settings s)
        {
            var c = Calibration.Default();
            if (!haveFfi) { c.Source = "no Rust FFI: always C#"; foreach (var j in Jobs) c.RustFfiFrom[j] = int.MaxValue; return c; }
            foreach (var job in Jobs)
            {
                var sizes = (job == "segment_contacts" ? s.SegmentSizes : s.Sizes).OrderBy(x => x).ToList();
                var from = int.MaxValue;
                for (var i = sizes.Count - 1; i >= 0; i--)
                {
                    // 10 % margin: a statistical tie does not justify crossing the FFI boundary
                    if (med.TryGetValue((job, "rust_ffi", sizes[i]), out var r) && med.TryGetValue((job, "csharp", sizes[i]), out var cs) && r * 1.1 < cs) from = sizes[i];
                    else break;
                }
                c.RustFfiFrom[job] = from;
            }
            c.Source = "measured " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ", " + Environment.ProcessorCount + " CPU";
            return c;
        }

        private static Dictionary<string, object> FixedCost(Dictionary<string, PowerEngineDispatcher> pes)
        {
            var d = new Dictionary<string, object>();
            var one = new[] { 1.0, 2, 3 };
            var m = new[] { 1.0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
            foreach (var kv in pes)
            {
                kv.Value.Transform3(one, m);
                var t = new List<double>();
                for (var i = 0; i < 200; i++) { var sw = Stopwatch.StartNew(); kv.Value.Transform3(one, m); t.Add(sw.Elapsed.TotalMilliseconds); }
                t.Sort();
                d[kv.Key + "_1_punto"] = Math.Round(t[t.Count / 2], 4);
            }
            return d;
        }

        private static Dictionary<string, object> Docs(Settings s)
        {
            var d = new Dictionary<string, object>();
            var py = new PowerEngineDispatcher(new PowerEngineOptions { PythonExe = s.PythonExe, PythonWorkerScript = s.PythonWorkerScript });
            try
            {
                if (s.DocxPath != null && File.Exists(s.DocxPath))
                {
                    var dir = new[] { Path.GetDirectoryName(Path.GetFullPath(s.DocxPath)) };
                    var args = new Dictionary<string, object> { ["path"] = s.DocxPath };
                    d["docx_dotnet_ms"] = Median(() => py.Document("docx_extract", args, dir, preferDotNet: true), s.Rounds);
                    if (py.PythonConfigured)
                    {
                        var sw = Stopwatch.StartNew();
                        var first = py.Document("docx_extract", args, dir);
                        d["python_startup_and_first_job_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                        d["docx_python_ms"] = first.Ok ? Median(() => py.Document("docx_extract", args, dir), s.Rounds) : (object)("not available: " + first.Error);
                    }
                    else d["docx_python_ms"] = "Python not configured";
                }
                if (s.PdfPath != null && File.Exists(s.PdfPath) && py.PythonConfigured)
                {
                    var dir = new[] { Path.GetDirectoryName(Path.GetFullPath(s.PdfPath)) };
                    foreach (var job in new[] { "pdf_info", "pdf_extract", "pdf_tables", "pdf_symbols", "pdf_grids" })
                        d[job + "_ms"] = Median(() => py.Document(job, new Dictionary<string, object> { ["path"] = s.PdfPath }, dir), s.Rounds);
                }
            }
            finally { py.Dispose(); }
            return d;
        }

        private static object Median(Func<PowerEngineContracts.Response> f, int rounds)
        {
            var t = new List<double>();
            for (var i = 0; i < Math.Max(1, rounds); i++)
            {
                var sw = Stopwatch.StartNew();
                var r = f();
                if (!r.Ok) return r.Code + ": " + r.Error;
                t.Add(sw.Elapsed.TotalMilliseconds);
            }
            t.Sort();
            return Math.Round(t[t.Count / 2], 2);
        }

        /// <summary>
        /// Text the AI reads and writes (estimate characters/4 = ESTIMATED tokens):
        ///  - discovery: brief search + one card vs the full catalog;
        ///  - placement of N elements: one "place" batch vs N individual commands (same content, different number of calls).
        /// </summary>
        public static Dictionary<string, object> TextEfficiency(int n = 72)
        {
            var full = Json.Write(PowerEngineCapabilityRegistry.All.Select(e => (object)e.Full()).ToList());
            var search = Json.Write(PowerEngineCapabilityRegistry.Search("pdf symbol", 8).Select(e => (object)e.Brief()).ToList());
            var one = Json.Write(PowerEngineCapabilityRegistry.Describe("pdf.symbols").Full());
            var pts = Enumerable.Range(0, n).Select(i => (object)new List<object> { Math.Round(10 + i * 3.25, 4), Math.Round(20 + (i % 9) * 4.5, 4) }).ToList();
            var batch = "plan " + Json.Write(new Dictionary<string, object> { ["op"] = "place", ["type"] = "LTG_DOWNLIGHT-SURFACE - ROUND: D", ["level"] = "LEVEL 09", ["points"] = pts, ["mounting"] = "slab", ["ws"] = "LIGHTING - DIST" });
            var single = pts.Select(p => "plan " + Json.Write(new Dictionary<string, object> { ["op"] = "place", ["type"] = "LTG_DOWNLIGHT-SURFACE - ROUND: D", ["level"] = "LEVEL 09", ["points"] = new List<object> { p }, ["mounting"] = "slab", ["ws"] = "LIGHTING - DIST" })).ToList();
            long Tok(long chars) => (chars + 3) / 4;
            return new Dictionary<string, object>
            {
                ["method"] = "ESTIMATED tokens = characters/4; not the client's real tokens",
                ["discovery"] = new Dictionary<string, object>
                {
                    ["full_catalog_chars"] = (long)full.Length, ["search_plus_card_chars"] = (long)(search.Length + one.Length),
                    ["catalog_estimated_tokens"] = Tok(full.Length), ["on_demand_estimated_tokens"] = Tok(search.Length + one.Length),
                    ["reduction_pct"] = Math.Round(100.0 * (1 - (double)(search.Length + one.Length) / full.Length), 1),
                },
                ["placement_" + n] = new Dictionary<string, object>
                {
                    ["batch_chars"] = (long)batch.Length, ["batch_calls"] = 1L,
                    ["single_chars"] = (long)single.Sum(x => x.Length), ["single_calls"] = (long)n,
                    ["chars_reduction_pct"] = Math.Round(100.0 * (1 - (double)batch.Length / single.Sum(x => x.Length)), 1),
                    ["note"] = "each individual call also adds the response and reasoning of one AI turn; only the command is counted here",
                },
            };
        }

        // ---------------- deterministic inputs ----------------

        private sealed class In { public double[] A, B; public int[] E; public int Nodes; }

        private static In Input(string job, int n)
        {
            var rnd = new Random(12345 + n);
            double[] Pts(int k, double span) { var p = new double[k * 2]; for (var i = 0; i < p.Length; i++) p[i] = rnd.NextDouble() * span; return p; }
            var span = Math.Sqrt(n) * 3;
            switch (job)
            {
                case "transform3": { var p = new double[n * 3]; for (var i = 0; i < p.Length; i++) p[i] = rnd.NextDouble() * 100; return new In { A = p }; }
                case "nearest": return new In { A = Pts(n / 2, span), B = Pts(n / 2, span) };
                case "match":
                {
                    var e = Pts(n / 2, span); var o = (double[])e.Clone();
                    for (var i = 0; i < o.Length; i++) o[i] += (rnd.NextDouble() - 0.5) * 0.2;
                    return new In { A = e, B = o };
                }
                case "duplicates": { var p = Pts(n, span); for (var i = 0; i + 3 < p.Length; i += 20) { p[i + 2] = p[i] + 0.01; p[i + 3] = p[i + 1]; } return new In { A = p }; }
                case "segment_contacts":
                {
                    var s = new double[n * 4];
                    for (var i = 0; i < n; i++)
                    {
                        double x = rnd.NextDouble() * span * 2, y = rnd.NextDouble() * span * 2, a = rnd.NextDouble() * Math.PI, l = 1 + rnd.NextDouble() * 4;
                        s[i * 4] = x; s[i * 4 + 1] = y; s[i * 4 + 2] = x + Math.Cos(a) * l; s[i * 4 + 3] = y + Math.Sin(a) * l;
                    }
                    return new In { A = s };
                }
                default:
                {
                    var e = new int[n * 2];
                    for (var i = 0; i < n; i++) { e[i * 2] = rnd.Next(n); e[i * 2 + 1] = rnd.Next(n); }
                    return new In { E = e, Nodes = n };
                }
            }
        }

        private static readonly double[] Mtx = { 0.8660254037844387, -0.5, 0, 12.5, 0.5, 0.8660254037844387, 0, -3.25, 0, 0, 1, 1.5 };

        private static void Exec(PowerEngineDispatcher pe, string job, In x, out string sig)
        {
            switch (job)
            {
                case "transform3": { var r = pe.Transform3(x.A, Mtx); sig = r.Length + ":" + Math.Round(r.Sum(), 6).ToString(CultureInfo.InvariantCulture); return; }
                case "nearest": { var r = pe.Nearest(x.A, x.B, 2, 5.0); sig = r.Index.Sum().ToString(CultureInfo.InvariantCulture); return; }
                case "match": { var r = pe.Match(x.A, x.B, 2, 0.5); sig = r.Sum().ToString(CultureInfo.InvariantCulture); return; }
                case "duplicates": { var r = pe.Duplicates(x.A, 2, 0.05); sig = r.Distinct().Count().ToString(CultureInfo.InvariantCulture); return; }
                case "segment_contacts": { var r = pe.SegmentContacts(x.A); sig = r.Count.ToString(CultureInfo.InvariantCulture); return; }
                default: { var r = pe.Components(x.Nodes, x.E); sig = r.Component.Distinct().Count().ToString(CultureInfo.InvariantCulture); return; }
            }
        }
    }
}
