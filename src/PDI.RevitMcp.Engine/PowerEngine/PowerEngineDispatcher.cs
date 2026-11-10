// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PDI.RevitMcp.Protocol;
using static BimBridge.PowerEngine.PowerEngineContracts;

using Request = BimBridge.PowerEngine.PowerEngineContracts.Request;
using Response = BimBridge.PowerEngine.PowerEngineContracts.Response;

namespace BimBridge.PowerEngine
{
    /// <summary>Configuración del Power Engine (rutas de los motores opcionales y umbrales del modelo de coste).</summary>
    public sealed class PowerEngineOptions
    {
        /// <summary>Ejecutable del Rust Geometry Engine en modo IPC (opcional; FFI se prueba siempre).</summary>
        public string RustWorkerExe;
        /// <summary>Intérprete y script del Python Worker (opcionales). Sin ellos, los trabajos documentales PDF devuelven NO_DISPONIBLE.</summary>
        public string PythonExe, PythonWorkerScript;
        /// <summary>auto (modelo de coste) | csharp | rust_ffi | rust_ipc. Forzar sirve para benchmarks y diagnóstico.</summary>
        public string Geometry = "auto";
        public Calibration Calibration = Calibration.Default();
        public bool DisableRustFfi;
    }

    /// <summary>
    /// Umbrales medidos (no supuestos): a partir de cuántos elementos Rust FFI compensa su coste de llamada frente a C#, por trabajo.
    /// Los valores por defecto salen del benchmark Power Engine (docs/0.9.0); <c>power-engine calibrar</c> los recalcula en el equipo.
    /// int.MaxValue = C# siempre (Rust no ganó en ningún tamaño medido).
    /// </summary>
    public sealed class Calibration
    {
        public Dictionary<string, int> RustFfiFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        public string Source = "por_defecto";

        public static Calibration Default() => new Calibration
        {
            RustFfiFrom =
            {
                // medido 2026-11-04 (power-engine calibrar, 3 campañas de 5–15 rondas, contenedor Linux x64 de 4 CPU): Rust FFI gana desde
                // n=100 en nearest/match/duplicates/segment_contacts (×2,5–×11); transform3 y components no ganan en todos los tamaños → C#.
                // En el PC de destino se recalibra con "power-engine calibrar --salida power_engine\calibracion.json".
                ["transform3"] = int.MaxValue, ["nearest"] = 100, ["match"] = 100, ["duplicates"] = 100,
                ["segment_contacts"] = 100, ["components"] = int.MaxValue,
            },
            Source = "por_defecto (benchmark Power Engine 0.9.0, Linux x64 4 CPU; recalibrar en el equipo)",
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
    /// BIMBRIDGE Power Engine — despachador. C# decide qué motor ejecuta cada trabajo según disponibilidad, tamaño y mediciones;
    /// una operación nunca pasa por los tres lenguajes: geometría = C# o Rust; documentos = Python (o .NET si es equivalente).
    /// Los resultados de Rust y C# son idénticos por contrato (mismas reglas y desempates), así que elegir motor no cambia el resultado.
    /// Registra métricas power_engine.* por trabajo y motor. No toca Revit ni concede permisos.
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

        /// <summary>Motor elegido para un trabajo geométrico de <paramref name="size"/> elementos (y por qué).</summary>
        public string Choose(string job, long size, out string why)
        {
            var forced = (Options.Geometry ?? "auto").Trim().ToLowerInvariant();
            if (forced == EngineCSharp) { why = "forzado"; return EngineCSharp; }
            if (forced == EngineRustFfi) { why = "forzado"; return RustFfiAvailable ? EngineRustFfi : Fallback(out why, "rust_ffi no disponible: " + RustGeometryEngine.Unavailable); }
            if (forced == EngineRustIpc) { why = "forzado"; return RustIpcConfigured ? EngineRustIpc : Fallback(out why, "rust_ipc no configurado"); }
            var from = Options.Calibration.For(job);
            if (RustFfiAvailable && size >= from) { why = "n=" + size + " ≥ " + from + " (" + Options.Calibration.Source + ")"; return EngineRustFfi; }
            why = RustFfiAvailable ? "n=" + size + " < " + (from == int.MaxValue ? "∞" : from.ToString(CultureInfo.InvariantCulture)) + ": C# es más rápido sin coste de transferencia" : "Rust no disponible: " + (Options.DisableRustFfi ? "desactivado" : RustGeometryEngine.Unavailable);
            return EngineCSharp;
        }

        private static string Fallback(out string why, string reason) { why = reason + " → C# (degradación controlada)"; return EngineCSharp; }

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

        // ---------------- geometría (C# o Rust; nunca Python) ----------------

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

        // ---------------- documentos (Python; .NET donde es equivalente) ----------------

        private WorkerClient Python()
        {
            lock (_lock) return _python ?? (_python = new WorkerClient(EnginePython, Options.PythonExe, new[] { "-I", "-B", Options.PythonWorkerScript }));
        }

        /// <summary>
        /// Trabajo documental. <paramref name="authorized"/> son las ÚNICAS rutas que el worker puede abrir en este trabajo.
        /// docx_extract corre en .NET si Python no está (resultado equivalente medido); los PDF necesitan el Python Worker.
        /// </summary>
        public Response Document(string job, Dictionary<string, object> args, IEnumerable<string> authorized, Limits limits = null, CancellationToken ct = default(CancellationToken), bool preferDotNet = false)
        {
            var paths = (authorized ?? Enumerable.Empty<string>()).Select(Path.GetFullPath).ToList();
            var file = Arg.GetString(args, "ruta");
            if (file != null && !PathAllowed(Path.GetFullPath(file), paths))
                return Response.Fail(NewId(), Codes.PathDenied, "ruta no autorizada para este trabajo: " + file);
            var sw = Stopwatch.StartNew();
            Response r;
            if (job == "docx_extract" && (preferDotNet || !PythonConfigured))
            {
                r = DocxExtractor.Run(file, limits ?? new Limits(), ct);
                r.Telemetry.Engine = EngineCSharp;
            }
            else if (!PythonConfigured)
                r = Response.Fail(NewId(), Codes.Unavailable, "Python Worker no configurado: " + job + " no tiene equivalente .NET (degradación controlada; el resto de BIMBRIDGE sigue funcionando)");
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

        /// <summary>Saludo con cada motor configurado: qué está disponible, versión y capacidades (degradación visible, no silenciosa).</summary>
        public Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>
            {
                ["csharp"] = new Dictionary<string, object> { ["disponible"] = true },
                ["rust_ffi"] = new Dictionary<string, object> { ["disponible"] = RustFfiAvailable, ["motivo"] = RustFfiAvailable ? null : (Options.DisableRustFfi ? "desactivado" : RustGeometryEngine.Unavailable), ["version"] = RustFfiAvailable ? RustGeometryEngine.Version : null },
            };
            d["rust_ipc"] = Probe(RustIpcConfigured ? RustIpc() : null, "no configurado");
            d["python"] = Probe(PythonConfigured ? Python() : null, "no configurado");
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

        /// <summary>Métricas acumuladas power_engine.* por trabajo y motor.</summary>
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
