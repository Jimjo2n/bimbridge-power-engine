// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.Protocol;
using static BimBridge.PowerEngine.PowerEngineContracts;

using Request = BimBridge.PowerEngine.PowerEngineContracts.Request;
using Response = BimBridge.PowerEngine.PowerEngineContracts.Response;

namespace BimBridge.PowerEngine
{
    /// <summary>Power Engine configuration (paths of the optional engines and cost-model thresholds).</summary>
    public sealed class PowerEngineOptions
    {
        /// <summary>Rust Geometry Engine executable in IPC mode (optional; FFI is always probed).</summary>
        public string RustWorkerExe;
        /// <summary>Python Worker interpreter and script (optional). Without them, PDF document jobs return NO_DISPONIBLE.</summary>
        public string PythonExe, PythonWorkerScript;
        /// <summary>auto (cost model) | csharp | rust_ffi | rust_ipc. Forcing is meant for benchmarks and diagnostics.</summary>
        public string Geometry = "auto";
        public Calibration Calibration = Calibration.Default();
        public bool DisableRustFfi;
    }

    /// <summary>
    /// Measured (not assumed) thresholds: from how many items Rust FFI pays off its call cost compared with C#, per job.
    /// The default values come from the Power Engine benchmark (docs/0.9.0); <c>power-engine calibrar</c> recomputes them on the machine.
    /// int.MaxValue = always C# (Rust did not win at any measured size).
    /// </summary>
    public sealed class Calibration
    {
        public Dictionary<string, int> RustFfiFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        public string Source = "por_defecto";

        public static Calibration Default() => new Calibration
        {
            RustFfiFrom =
            {
                // measured 2026-11-04 (power-engine calibrar, 3 campaigns of 5–15 rounds, Linux x64 4-CPU container): Rust FFI wins from
                // n=100 in nearest/match/duplicates/segment_contacts (×2.5–×11); transform3 and components do not win at every size → C#.
                // On the target PC, recalibrate with "power-engine calibrar --salida power_engine\calibracion.json".
                ["transform3"] = int.MaxValue, ["nearest"] = 100, ["match"] = 100, ["duplicates"] = 100,
                ["segment_contacts"] = 100, ["components"] = int.MaxValue,
            },
            Source = "por_defecto (Power Engine 0.9.0 benchmark, Linux x64 4 CPU; recalibrate on the machine)",
        };

        public int For(string job) => RustFfiFrom.TryGetValue(job, out var v) ? v : int.MaxValue;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["fuente"] = Source, ["rust_ffi_desde"] = RustFfiFrom.ToDictionary(k => k.Key, k => (object)(long)k.Value),
        };

        public static Calibration FromJson(Dictionary<string, object> o)
        {
            var c = Default();
            c.Source = Arg.GetString(o, "fuente") ?? "archivo";
            if (o.TryGetValue("rust_ffi_desde", out var r) && r is Dictionary<string, object> rd)
                foreach (var kv in rd) c.RustFfiFrom[kv.Key] = (int)Math.Min(int.MaxValue, Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture));
            return c;
        }
    }

    /// <summary>
    /// BIMBRIDGE Power Engine — dispatcher. C# decides which engine runs each job based on availability, size and measurements;
    /// an operation never goes through all three languages: geometry = C# or Rust; documents = Python (or .NET when equivalent).
    /// Rust and C# results are identical by contract (same rules and tie-breaks), so choosing an engine does not change the result.
    /// Records power_engine.* metrics per job and engine. Does not touch Revit or grant permissions.
    /// </summary>
    public sealed class PowerEngineDispatcher : IDisposable
    {
        public readonly PowerEngineOptions Options;
        private WorkerClient _rustIpc, _python;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        public Telemetry Last { get; private set; }

        private sealed class Stat { public long Calls; public double Ms; public long Items; }

        public PowerEngineDispatcher(PowerEngineOptions options = null)
        {
            Options = options ?? new PowerEngineOptions();
        }

        public bool RustFfiAvailable => !Options.DisableRustFfi && RustGeometryEngine.Available;
        public bool RustIpcConfigured => !string.IsNullOrEmpty(Options.RustWorkerExe) && File.Exists(Options.RustWorkerExe);
        public bool PythonConfigured => !string.IsNullOrEmpty(Options.PythonExe) && !string.IsNullOrEmpty(Options.PythonWorkerScript) && File.Exists(Options.PythonWorkerScript);

        /// <summary>Engine chosen for a geometry job of <paramref name="size"/> items (and why).</summary>
        public string Choose(string job, long size, out string why)
        {
            var forced = (Options.Geometry ?? "auto").Trim().ToLowerInvariant();
            if (forced == EngineCSharp) { why = "forced"; return EngineCSharp; }
            if (forced == EngineRustFfi) { why = "forced"; return RustFfiAvailable ? EngineRustFfi : Fallback(out why, "rust_ffi unavailable: " + RustGeometryEngine.Unavailable); }
            if (forced == EngineRustIpc) { why = "forced"; return RustIpcConfigured ? EngineRustIpc : Fallback(out why, "rust_ipc not configured"); }
            var from = Options.Calibration.For(job);
            if (RustFfiAvailable && size >= from) { why = "n=" + size + " ≥ " + from + " (" + Options.Calibration.Source + ")"; return EngineRustFfi; }
            why = RustFfiAvailable ? "n=" + size + " < " + (from == int.MaxValue ? "∞" : from.ToString(CultureInfo.InvariantCulture)) + ": C# is faster without transfer cost" : "Rust unavailable: " + (Options.DisableRustFfi ? "disabled" : RustGeometryEngine.Unavailable);
            return EngineCSharp;
        }

        private static string Fallback(out string why, string reason) { why = reason + " → C# (controlled degradation)"; return EngineCSharp; }

        private T Run<T>(string job, long size, Func<T> cs, Func<T> ffi, Func<T> ipc)
        {
            var engine = Choose(job, size, out var why);
            var sw = Stopwatch.StartNew();
            T r;
            switch (engine)
            {
                case EngineRustFfi: r = ffi(); break;
                case EngineRustIpc: r = ipc(); break;
                default: r = cs(); break;
            }
            sw.Stop();
            Record(job, engine, sw.Elapsed.TotalMilliseconds, size);
            Last = new Telemetry { Engine = engine, TotalMs = sw.Elapsed.TotalMilliseconds, EngineMs = sw.Elapsed.TotalMilliseconds };
            LastReason = why;
            return r;
        }

        public string LastReason { get; private set; }

        private void Record(string job, string engine, double ms, long items)
        {
            lock (_lock)
            {
                var k = job + "@" + engine;
                if (!_stats.TryGetValue(k, out var s)) _stats[k] = s = new Stat();
                s.Calls++; s.Ms += ms; s.Items += items;
            }
        }

        // ---------------- geometry (C# or Rust; never Python) ----------------

        public double[] Transform3(double[] xyz, double[] m, CancellationToken ct = default(CancellationToken)) =>
            Run("transform3", xyz.Length / 3, () => GeometryKernels.Transform3(xyz, m, ct), () => RustGeometryEngine.Transform3(xyz, m, ct),
                () => IpcDoubles("transform3", new Dictionary<string, object> { ["xyz"] = Box(xyz), ["m"] = Box(m) }, "xyz", ct));

        public (int[] Index, double[] Dist) Nearest(double[] q, double[] r, int dim, double maxDist, CancellationToken ct = default(CancellationToken)) =>
            Run("nearest", q.Length / dim + r.Length / dim, () => GeometryKernels.Nearest(q, r, dim, maxDist, ct), () => RustGeometryEngine.Nearest(q, r, dim, maxDist, ct), () =>
            {
                var res = Ipc("nearest", new Dictionary<string, object> { ["dim"] = (long)dim, ["max_dist"] = maxDist, ["consulta"] = Box(q), ["referencia"] = Box(r) }, ct);
                return (Ints(res, "indice"), Doubles(res, "distancia"));
            });

        public int[] Match(double[] e, double[] o, int dim, double tol, CancellationToken ct = default(CancellationToken)) =>
            Run("match", e.Length / dim + o.Length / dim, () => GeometryKernels.Match(e, o, dim, tol, ct), () => RustGeometryEngine.Match(e, o, dim, tol, ct),
                () => Ints(Ipc("match", new Dictionary<string, object> { ["dim"] = (long)dim, ["tol"] = tol, ["esperados"] = Box(e), ["observados"] = Box(o) }, ct), "pareja"));

        public int[] Duplicates(double[] p, int dim, double tol, CancellationToken ct = default(CancellationToken)) =>
            Run("duplicates", p.Length / dim, () => GeometryKernels.Duplicates(p, dim, tol, ct), () => RustGeometryEngine.Duplicates(p, dim, tol, ct),
                () => Ints(Ipc("duplicates", new Dictionary<string, object> { ["dim"] = (long)dim, ["tol"] = tol, ["puntos"] = Box(p) }, ct), "grupo"));

        public List<(int I, int J, int Kind)> SegmentContacts(double[] s, double eps = 1e-7, CancellationToken ct = default(CancellationToken)) =>
            Run("segment_contacts", s.Length / 4, () => GeometryKernels.SegmentContacts(s, eps, ct), () => RustGeometryEngine.SegmentContacts(s, eps, ct), () =>
            {
                var f = Ints(Ipc("segment_contacts", new Dictionary<string, object> { ["segmentos"] = Box(s), ["eps"] = eps }, ct), "contactos");
                var l = new List<(int, int, int)>(f.Length / 3);
                for (var k = 0; k + 2 < f.Length; k += 3) l.Add((f[k], f[k + 1], f[k + 2]));
                return l;
            });

        public (int[] Component, int[] Degree) Components(int nodes, int[] edges) =>
            Run("components", nodes + edges.Length / 2, () => GeometryKernels.Components(nodes, edges), () => RustGeometryEngine.Components(nodes, edges), () =>
            {
                var res = Ipc("components", new Dictionary<string, object> { ["nodos"] = (long)nodes, ["aristas"] = edges.Select(x => (object)(long)x).ToList() }, CancellationToken.None);
                return (Ints(res, "componente"), Ints(res, "grado"));
            });

        // ---------------- IPC ----------------

        private WorkerClient RustIpc()
        {
            lock (_lock) return _rustIpc ?? (_rustIpc = new WorkerClient(EngineRustIpc, Options.RustWorkerExe, new string[0]));
        }

        private Dictionary<string, object> Ipc(string job, Dictionary<string, object> args, CancellationToken ct)
        {
            var r = RustIpc().Call(new Request { Job = job, Args = args }, ct);
            if (r.Code == Codes.Cancelled) throw new OperationCanceledException(r.Error, ct);
            if (!r.Ok) throw new InvalidOperationException("rust_ipc " + job + ": " + r.Code + " — " + r.Error);
            return r.Result;
        }

        private double[] IpcDoubles(string job, Dictionary<string, object> args, string key, CancellationToken ct) => Doubles(Ipc(job, args, ct), key);

        internal static List<object> Box(double[] v) { var l = new List<object>(v.Length); foreach (var x in v) l.Add(x); return l; }
        internal static double[] Doubles(Dictionary<string, object> d, string k) =>
            ((List<object>)d[k]).Select(x => x == null ? double.NaN : Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
        internal static int[] Ints(Dictionary<string, object> d, string k) =>
            ((List<object>)d[k]).Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();

        // ---------------- documents (Python; .NET where equivalent) ----------------

        private WorkerClient Python()
        {
            lock (_lock) return _python ?? (_python = new WorkerClient(EnginePython, Options.PythonExe, new[] { "-I", "-B", Options.PythonWorkerScript }));
        }

        /// <summary>
        /// Document job. <paramref name="authorized"/> are the ONLY paths the worker may open in this job.
        /// docx_extract runs in .NET if Python is absent (measured equivalent result); PDFs need the Python Worker.
        /// </summary>
        public Response Document(string job, Dictionary<string, object> args, IEnumerable<string> authorized, Limits limits = null, CancellationToken ct = default(CancellationToken), bool preferDotNet = false)
        {
            var paths = (authorized ?? Enumerable.Empty<string>()).Select(Path.GetFullPath).ToList();
            var file = Arg.GetString(args, "ruta");
            if (file != null && !PathAllowed(Path.GetFullPath(file), paths))
                return Response.Fail(NewId(), Codes.PathDenied, "path not authorized for this job: " + file);
            var sw = Stopwatch.StartNew();
            Response r;
            if (job == "docx_extract" && (preferDotNet || !PythonConfigured))
            {
                r = DocxExtractor.Run(file, limits ?? new Limits(), ct);
                r.Telemetry.Engine = EngineCSharp;
            }
            else if (!PythonConfigured)
                r = Response.Fail(NewId(), Codes.Unavailable, "Python Worker not configured: " + job + " has no .NET equivalent (controlled degradation; the rest of BIMBRIDGE keeps working)");
            else
                r = Python().Call(new Request { Job = job, Args = args, Paths = paths, Limits = limits ?? new Limits() }, ct);
            sw.Stop();
            if (r.Telemetry.TotalMs <= 0) r.Telemetry.TotalMs = sw.Elapsed.TotalMilliseconds;
            Record(job, r.Telemetry.Engine ?? EnginePython, sw.Elapsed.TotalMilliseconds, 1);
            Last = r.Telemetry;
            return r;
        }

        public static bool PathAllowed(string full, IList<string> allowed)
        {
            foreach (var a in allowed)
            {
                if (string.Equals(full, a, StringComparison.OrdinalIgnoreCase)) return true;
                var dir = a.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? a : a + Path.DirectorySeparatorChar;
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Handshake with each configured engine: what is available, version and capabilities (visible degradation, not silent).</summary>
        public Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>
            {
                ["csharp"] = new Dictionary<string, object> { ["disponible"] = true },
                ["rust_ffi"] = new Dictionary<string, object> { ["disponible"] = RustFfiAvailable, ["motivo"] = RustFfiAvailable ? null : (Options.DisableRustFfi ? "disabled" : RustGeometryEngine.Unavailable), ["version"] = RustFfiAvailable ? RustGeometryEngine.Version : null },
            };
            d["rust_ipc"] = Probe(RustIpcConfigured ? RustIpc() : null, "not configured");
            d["python"] = Probe(PythonConfigured ? Python() : null, "not configured");
            d["calibracion"] = Options.Calibration.ToJson();
            return d;
        }

        private static Dictionary<string, object> Probe(WorkerClient w, string missing)
        {
            if (w == null) return new Dictionary<string, object> { ["disponible"] = false, ["motivo"] = missing };
            var ok = w.EnsureStarted();
            return new Dictionary<string, object>
            {
                ["disponible"] = ok, ["motivo"] = ok ? null : w.LastStartError, ["version"] = w.Info?.Version, ["pe"] = (long)(w.Info?.Protocol ?? 0),
                ["capacidades"] = (w.Info?.Capabilities ?? new List<string>()).Cast<object>().ToList(),
            };
        }

        /// <summary>Accumulated power_engine.* metrics per job and engine.</summary>
        public Dictionary<string, object> Metrics()
        {
            lock (_lock)
                return _stats.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => "power_engine." + k.Key, k => (object)new Dictionary<string, object>
                {
                    ["llamadas"] = k.Value.Calls, ["ms"] = Math.Round(k.Value.Ms, 3), ["elementos"] = k.Value.Items,
                });
        }

        public void Dispose()
        {
            _rustIpc?.Dispose();
            _python?.Dispose();
        }
    }
}
