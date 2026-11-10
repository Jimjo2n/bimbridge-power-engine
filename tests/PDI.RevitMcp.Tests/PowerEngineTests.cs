// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BimBridge.PowerEngine;
using PDI.RevitMcp.Protocol;
using Xunit;
using static BimBridge.PowerEngine.PowerEngineContracts;
using Request = BimBridge.PowerEngine.PowerEngineContracts.Request;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Raíz del repositorio, ejecutables de los workers y fixtures sintéticos (generados una vez por corrida).</summary>
    public static class PowerEngineEnv
    {
        public static readonly string Root = FindRoot();
        public static string RustWorker => Path.Combine(Root, "native", "power_engine", "target", "release", OperatingSystem.IsWindows() ? "bimbridge-power-engine.exe" : "bimbridge-power-engine");
        public static string PythonScript => Path.Combine(Root, "workers", "python", "run_worker.py");
        public static string Python => Environment.GetEnvironmentVariable("BIMBRIDGE_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        private static readonly Lazy<string> _fx = new Lazy<string>(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "bb-pe-fx-" + Process.GetCurrentProcess().Id);
            if (!File.Exists(Path.Combine(dir, "truth.json")))
            {
                var p = Process.Start(new ProcessStartInfo(Python) { ArgumentList = { "-I", "-B", Path.Combine(Root, "workers", "python", "tests", "fixtures.py"), dir }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                p.WaitForExit(120000);
            }
            return dir;
        });
        public static string Fixtures => _fx.Value;

        private static string FindRoot()
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !Directory.Exists(Path.Combine(d, "native", "power_engine"))) d = Path.GetDirectoryName(d);
            return d ?? throw new InvalidOperationException("no se encontró la raíz del repositorio");
        }

        public static PowerEngineOptions Options(string geometry = "auto") => new PowerEngineOptions
        {
            RustWorkerExe = RustWorker, PythonExe = Python, PythonWorkerScript = PythonScript, Geometry = geometry,
        };
    }

    public class PowerEngineGeometryTests
    {
        private static double[] Rand(int n, int seed, double span, double snap = 0)
        {
            var r = new Random(seed);
            var v = new double[n];
            for (var i = 0; i < n; i++) { v[i] = r.NextDouble() * span; if (snap > 0) v[i] = Math.Round(v[i] / snap) * snap; }
            return v;
        }

        [Fact]
        public void RustLibrary_IsAvailable_AndSpeaksTheContract()
        {
            Assert.True(RustGeometryEngine.Available, "compile native/power_engine con cargo build --release: " + RustGeometryEngine.Unavailable);
            Assert.Equal("0.9.0", RustGeometryEngine.Version);
            Assert.True(File.Exists(PowerEngineEnv.RustWorker), "falta el worker IPC de Rust");
        }

        public static IEnumerable<object[]> Sizes() => new[] { 0, 1, 7, 500, 6000 }.Select(n => new object[] { n });

        [Theory, MemberData(nameof(Sizes))]
        public void Equivalence_CSharp_RustFfi_RustIpc(int n)
        {
            using (var cs = new PowerEngineDispatcher(PowerEngineEnv.Options("csharp")))
            using (var ffi = new PowerEngineDispatcher(PowerEngineEnv.Options("rust_ffi")))
            using (var ipc = new PowerEngineDispatcher(PowerEngineEnv.Options("rust_ipc")))
            {
                var all = new[] { cs, ffi, ipc };
                // puntos con empates y duplicados exactos (rejilla de 0,25) para ejercitar los desempates
                var q = Rand(2 * n, 1, 100, 0.25); var r = Rand(2 * n + 2, 2, 100, 0.25);
                var m = new[] { 0.866, -0.5, 0, 10, 0.5, 0.866, 0, -4, 0, 0, 1, 2.5 };
                var xyz = Rand(3 * n, 3, 50);
                var t = all.Select(d => d.Transform3(xyz, m)).ToList();
                Assert.Equal(new[] { "csharp", "rust_ffi", "rust_ipc" }, all.Select(d => d.Last.Engine));
                // misma fórmula; el orden de redondeo puede diferir en 1 ulp (≈7e-15 a 32 ft): tolerancia relativa 1e-13
                for (var k = 1; k < 3; k++) for (var i = 0; i < xyz.Length; i++) Assert.True(Math.Abs(t[0][i] - t[k][i]) <= 1e-13 * Math.Max(1, Math.Abs(t[0][i])), "transform3 " + i);

                var near = all.Select(d => d.Nearest(q, r, 2, 3.0)).ToList();
                for (var k = 1; k < 3; k++)
                {
                    Assert.Equal(near[0].Index, near[k].Index);
                    for (var i = 0; i < near[0].Dist.Length; i++)
                        Assert.True(double.IsNaN(near[0].Dist[i]) && double.IsNaN(near[k].Dist[i]) || Math.Abs(near[0].Dist[i] - near[k].Dist[i]) < 1e-12);
                }
                var match = all.Select(d => d.Match(q, r, 2, 1.5)).ToList();
                Assert.Equal(match[0], match[1]); Assert.Equal(match[0], match[2]);
                var dup = all.Select(d => d.Duplicates(q, 2, 0.3)).ToList();
                Assert.Equal(dup[0], dup[1]); Assert.Equal(dup[0], dup[2]);

                var segs = Rand(4 * Math.Min(n, 1500), 4, 100, 1.0);   // coordenadas enteras: muchos toques y colineales
                var sc = all.Select(d => d.SegmentContacts(segs)).ToList();
                Assert.Equal(sc[0], sc[1]); Assert.Equal(sc[0], sc[2]);

                var rnd = new Random(5);
                var edges = Enumerable.Range(0, 2 * n).Select(_ => rnd.Next(Math.Max(1, n))).ToArray();
                var comp = all.Select(d => d.Components(Math.Max(1, n), edges)).ToList();
                Assert.Equal(comp[0].Component, comp[1].Component); Assert.Equal(comp[0].Component, comp[2].Component);
                Assert.Equal(comp[0].Degree, comp[1].Degree); Assert.Equal(comp[0].Degree, comp[2].Degree);
            }
        }

        [Fact]
        public void SegmentContacts_KnownCases_SameInAllEngines()
        {
            var s = new double[]
            {
                0, 0, 4, 0,     // 0
                2, -1, 2, 1,    // 1 cruza 0
                4, 0, 4, 3,     // 2 toca 0 en el extremo
                3, 0, 6, 0,     // 3 colineal superpuesto a 0
                10, 10, 11, 11, // 4 aislado
            };
            foreach (var g in new[] { "csharp", "rust_ffi", "rust_ipc" })
                using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options(g)))
                {
                    var c = d.SegmentContacts(s).OrderBy(x => x.I).ThenBy(x => x.J).ToList();
                    Assert.Contains((0, 1, 1), c);
                    Assert.Contains((0, 2, 2), c);
                    Assert.Contains((0, 3, 3), c);
                    Assert.DoesNotContain(c, x => x.I == 4 || x.J == 4);
                }
        }

        [Fact]
        public void InvalidInput_RejectedByAllEngines()
        {
            var bad = new[] { 0.0, double.NaN, 1, 2 };
            Assert.Throws<ArgumentException>(() => GeometryKernels.Duplicates(bad, 2, 0.1));
            Assert.Throws<ArgumentException>(() => RustGeometryEngine.Duplicates(bad, 2, 0.1));
            using (var ipc = new PowerEngineDispatcher(PowerEngineEnv.Options("rust_ipc")))
                Assert.Contains("INVALIDO", Assert.Throws<InvalidOperationException>(() => ipc.Duplicates(bad, 2, 0.1)).Message);
            Assert.Throws<ArgumentException>(() => RustGeometryEngine.Transform3(new double[3], new double[11]));
            Assert.Throws<ArgumentException>(() => RustGeometryEngine.Nearest(new double[] { 0, 0 }, new double[] { 1, 1 }, 2, -1));
        }

        [Fact]
        public void Cancellation_PreCancelled_And_DuringLargeJob()
        {
            var cts = new CancellationTokenSource(); cts.Cancel();
            var p = Rand(2 * 1000, 7, 100);
            Assert.ThrowsAny<OperationCanceledException>(() => GeometryKernels.Duplicates(p, 2, 0.5, cts.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => RustGeometryEngine.Duplicates(p, 2, 0.5, cts.Token));
            // durante: 3 M de puntos en un radio enorme (cuadrático en una celda) se cancela a los 100 ms
            var big = Rand(2 * 200000, 8, 1.0);
            var c2 = new CancellationTokenSource(100);
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => RustGeometryEngine.Duplicates(big, 2, 5.0, c2.Token));
            Assert.True(sw.ElapsedMilliseconds < 5000, "la cancelación debe llegar a Rust mientras calcula");
            var c3 = new CancellationTokenSource(100);
            Assert.ThrowsAny<OperationCanceledException>(() => GeometryKernels.Duplicates(big, 2, 5.0, c3.Token));
        }

        [Fact]
        public void Dispatcher_Degrades_AndExplainsWhy()
        {
            using (var d = new PowerEngineDispatcher(new PowerEngineOptions { DisableRustFfi = true }))
            {
                d.Nearest(new double[] { 0, 0 }, new double[] { 1, 1 }, 2, 5);
                Assert.Equal(EngineCSharp, d.Last.Engine);
                Assert.Contains("desactivado", d.LastReason);
            }
            using (var d = new PowerEngineDispatcher(new PowerEngineOptions { Geometry = "rust_ipc", RustWorkerExe = "/no/existe" }))
            {
                d.Duplicates(new double[] { 0, 0, 0, 0 }, 2, 0.1);
                Assert.Equal(EngineCSharp, d.Last.Engine);
                Assert.Contains("degradación", d.LastReason);
            }
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                // auto: tamaños pequeños en C#, grandes en Rust según la calibración
                d.Options.Calibration.RustFfiFrom["duplicates"] = 1000;
                d.Duplicates(new double[20], 2, 0.1);
                Assert.Equal(EngineCSharp, d.Last.Engine);
                d.Duplicates(new double[2 * 1500], 2, 0.1);
                Assert.Equal(EngineRustFfi, d.Last.Engine);
                var m = d.Metrics();
                Assert.True(m.Keys.Any(k => k.StartsWith("power_engine.", StringComparison.Ordinal)));
            }
        }
    }

    public class PowerEngineDocumentTests
    {
        private static readonly string Fx = PowerEngineEnv.Fixtures;

        [Fact]
        public void WindowsAppsAliasIsRejectedBeforeLaunching()
        {
            if (!OperatingSystem.IsWindows()) return;
            var alias = Path.Combine(Path.GetTempPath(), "Microsoft", "WindowsApps", "python.exe");
            using var worker = new WorkerClient(EnginePython, alias, Array.Empty<string>());
            Assert.False(worker.EnsureStarted());
            Assert.Contains("Alias WindowsApps rechazado", worker.LastStartError);
        }

        [Fact]
        public void StartupFailureIncludesExitCodeAndBoundedStderr()
        {
            using var worker = new WorkerClient(EnginePython, PowerEngineEnv.Python, new[]
            {
                "-I", "-c", "import sys; sys.stderr.write('missing dependency ' + 'x' * 5000); sys.exit(9)"
            });
            Assert.False(worker.EnsureStarted());
            Assert.Contains("saludo", worker.LastStartError);
            Assert.Contains("missing dependency", worker.LastStartError);
            Assert.Contains("salida=9", worker.LastStartError);
            Assert.InRange(worker.LastStartError.Length, 1, 2300);
        }
        private static string F(string n) => Path.Combine(Fx, n);
        private static Dictionary<string, object> A(string ruta) => new Dictionary<string, object> { ["ruta"] = ruta };

        [Fact]
        public void Python_Handshake_AndSymbols_ThroughTheCore()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var r = d.Document("pdf_symbols", A(F("plan_vectorial.pdf")), new[] { Fx });
                Assert.True(r.Ok, r.Code + " " + r.Error);
                var page = (Dictionary<string, object>)((List<object>)r.Result["paginas"])[0];
                Assert.Equal(74, ((List<object>)page["candidatos"]).Count);   // 72 + 2 casi duplicados
                Assert.Equal(EnginePython, r.Telemetry.Engine);
                Assert.True(r.Telemetry.EngineMs > 0 && r.Telemetry.BytesOut > 0);
                Assert.Equal(true, d.Status()["python"] is Dictionary<string, object> py && (bool)py["disponible"]);
            }
        }

        [Fact]
        public void PathDenied_BeforeAnyWorkerRuns()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                Assert.Equal(Codes.PathDenied, d.Document("pdf_info", A("/etc/passwd"), new[] { Fx }).Code);
                Assert.Equal(Codes.PathDenied, d.Document("pdf_info", A(F("plan_vectorial.pdf")), new string[0]).Code);
                Assert.Equal(Codes.PathDenied, d.Document("pdf_info", A(Fx + "_hermano/x.pdf"), new[] { Fx }).Code);
            }
        }

        [Fact]
        public void Timeout_KillsWorker_ThenRestarts()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var r = d.Document("pdf_symbols", A(F("grande_40p.pdf")), new[] { Fx }, new Limits { TimeoutMs = 300 });
                Assert.Equal(Codes.Timeout, r.Code);
                var ok = d.Document("pdf_info", A(F("plan_vectorial.pdf")), new[] { Fx });
                Assert.True(ok.Ok, ok.Error);
            }
        }

        [Fact]
        public void Cancellation_KillsWorker_ThenRestarts()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var cts = new CancellationTokenSource(300);
                Assert.Equal(Codes.Cancelled, d.Document("pdf_symbols", A(F("grande_40p.pdf")), new[] { Fx }, null, cts.Token).Code);
                Assert.True(d.Document("pdf_info", A(F("plan_vectorial.pdf")), new[] { Fx }).Ok);
            }
        }

        [Fact]
        public void MessageSizeLimit_IsEnforcedByTheCore()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var r = d.Document("pdf_extract", A(F("grande_40p.pdf")), new[] { Fx }, new Limits { MaxMessageBytes = 100000, TimeoutMs = 60000 });
                Assert.Equal(Codes.TooLarge, r.Code);
                Assert.True(d.Document("pdf_info", A(F("plan_vectorial.pdf")), new[] { Fx }).Ok);
            }
        }

        [Fact]
        public void NoPython_Degrades_DocxInDotNet_PdfUnavailable()
        {
            using (var d = new PowerEngineDispatcher(new PowerEngineOptions { PythonExe = "python3", PythonWorkerScript = "/no/existe.py" }))
            {
                var docx = d.Document("docx_extract", A(F("especificacion.docx")), new[] { Fx });
                Assert.True(docx.Ok, docx.Error);
                Assert.Equal(EngineCSharp, docx.Telemetry.Engine);
                Assert.Equal(Codes.Unavailable, d.Document("pdf_info", A(F("plan_vectorial.pdf")), new[] { Fx }).Code);
            }
        }

        [Fact]
        public void Docx_DotNet_EqualsPython()
        {
            using (var d = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var py = d.Document("docx_extract", A(F("especificacion.docx")), new[] { Fx });
                var net = d.Document("docx_extract", A(F("especificacion.docx")), new[] { Fx }, preferDotNet: true);
                Assert.True(py.Ok && net.Ok);
                Assert.Equal(EnginePython, py.Telemetry.Engine); Assert.Equal(EngineCSharp, net.Telemetry.Engine);
                foreach (var k in new[] { "sha256", "bloques", "parrafos", "tablas", "sospechosos", "truncado" })
                    Assert.Equal(Json.Write(py.Result[k]), Json.Write(net.Result[k]));
            }
        }

        [Fact]
        public void Docx_DotNet_RejectsXxe_AndNonZip()
        {
            var x = Path.Combine(Fx, "xxe-net.docx");
            using (var z = System.IO.Compression.ZipFile.Open(x, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("word/document.xml").Open()))
                w.Write("<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>");
            try
            {
                Assert.Equal(Codes.Invalid, DocxExtractor.Run(x, new Limits(), CancellationToken.None).Code);
                Assert.Equal(Codes.Invalid, DocxExtractor.Run(F("plan_vectorial.pdf"), new Limits(), CancellationToken.None).Code);
            }
            finally { File.Delete(x); }
        }

        [Fact]
        public void IncompatibleWorker_IsRefusedAtHandshake()
        {
            var fake = Path.Combine(Fx, "falso_pe2.py");
            File.WriteAllText(fake, "import sys,json\nfor l in sys.stdin:\n r=json.loads(l)\n print(json.dumps({'pe':2,'id':r.get('id'),'codigo':'OK','resultado':{'pe':2}}),flush=True)\n");
            using (var w = new WorkerClient(EnginePython, PowerEngineEnv.Python, new[] { "-I", fake }))
            {
                var r = w.Call(new Request { Job = "pdf_info" });
                Assert.Equal(Codes.Unavailable, r.Code);
                Assert.Contains("VERSION_INCOMPATIBLE", w.LastStartError ?? "");
            }
        }
    }

    public class PowerEngineRegistryTests
    {
        [Fact]
        public void Search_ReturnsBriefEntries_DescribeReturnsDetail()
        {
            var hits = PowerEngineCapabilityRegistry.Search("luminaria pdf");
            Assert.Equal("pdf.symbols", hits[0].Id);
            var d = PowerEngineCapabilityRegistry.Describe("pdf.symbols").Full();
            Assert.Equal(CapabilityStates.Available, d["estado"]);
            Assert.NotNull(d["motor"]);
            Assert.Null(PowerEngineCapabilityRegistry.Describe("no.existe"));
        }

        [Fact]
        public void States_AreFromTheClosedSet_AndNativeWorkIsNeverAvailable()
        {
            var states = new HashSet<string> { CapabilityStates.Available, CapabilityStates.Experimental, CapabilityStates.RequiresAuthorization, CapabilityStates.NativeTestPending, CapabilityStates.Deferred, CapabilityStates.ApiLimitation };
            foreach (var e in PowerEngineCapabilityRegistry.All)
            {
                Assert.Contains(e.State, states);
                Assert.False(string.IsNullOrEmpty(e.Engine));
                if (e.Compatibility.StartsWith("Revit", StringComparison.Ordinal) && e.Permission != Permissions.Read)
                    Assert.NotEqual(CapabilityStates.Available, e.State);   // nada que escriba en Revit es AVAILABLE sin evidencia nativa
            }
            Assert.Equal(PowerEngineCapabilityRegistry.All.Count, PowerEngineCapabilityRegistry.All.Select(e => e.Id).Distinct().Count());
        }

        [Fact]
        public void Wire_ExistingOpsAvailable_LabExecExperimental()
        {
            Assert.Equal(CapabilityStates.Available, PowerEngineCapabilityRegistry.StateOfWire(Wire.OpQuery, new Dictionary<string, object>()));
            Assert.Equal(CapabilityStates.Experimental, PowerEngineCapabilityRegistry.StateOfWire(Wire.OpExec, new Dictionary<string, object> { ["accion"] = "laboratorio" }));
        }
    }
}
