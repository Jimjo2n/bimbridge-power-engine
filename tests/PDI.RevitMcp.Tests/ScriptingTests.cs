// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Scripting;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Runner general (exec): el código del usuario se envuelve y se COMPILA con Roslyn contra la API real de cada año
    /// (ensamblados de referencia de RevitAPI/RevitAPIUI) y contra el add-in compilado de ese año. No ejecuta nada en Revit:
    /// prueba que el script es válido para esa API, no que funcione en un modelo.
    /// </summary>
    public class ScriptingTests
    {
        private static readonly string Root = FindRoot();
        public static string RepoRoot => Root;
        private static readonly string Nuget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        private static readonly Dictionary<int, string> RefVersion = new Dictionary<int, string>
        {
            [2022] = "2022.1.80", [2023] = "2023.1.90", [2024] = "2024.3.60", [2025] = "2025.4.60", [2026] = "2026.4.10",
        };

        private static string FindRoot()
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "PDI.RevitMcp.sln"))) d = Path.GetDirectoryName(d);
            return d;
        }

        public static string[] References(int year)
        {
            var refs = new List<string>();
            if (year < 2025)
            {
                var fx = Directory.GetDirectories(Path.Combine(Nuget, "microsoft.netframework.referenceassemblies.net48")).OrderBy(x => x).Last();
                var v48 = Path.Combine(fx, "build", ".NETFramework", "v4.8");
                refs.AddRange(Directory.GetFiles(v48, "*.dll"));
                refs.AddRange(Directory.GetFiles(Path.Combine(v48, "Facades"), "*.dll"));
            }
            else
            {
                var dotnetRoot = Path.GetDirectoryName(typeof(object).Assembly.Location); // …/shared/Microsoft.NETCore.App/8.x
                var packs = Path.GetFullPath(Path.Combine(dotnetRoot, "..", "..", "..", "packs", "Microsoft.NETCore.App.Ref"));
                var pack = Directory.GetDirectories(packs).Where(x => Path.GetFileName(x).StartsWith("8.")).OrderBy(x => x).Last();
                refs.AddRange(Directory.GetFiles(Path.Combine(pack, "ref", "net8.0"), "*.dll"));
                var desktop = Path.Combine(Path.GetDirectoryName(packs), "Microsoft.WindowsDesktop.App.Ref");
                if (!Directory.Exists(desktop))
                    desktop = Path.Combine(Nuget, "microsoft.windowsdesktop.app.ref");
                var wd = Directory.GetDirectories(desktop).Where(x => Path.GetFileName(x).StartsWith("8.")).OrderBy(x => x).Last();
                refs.AddRange(Directory.GetFiles(Path.Combine(wd, "ref", "net8.0"), "*.dll"));
            }
            foreach (var pkg in new[] { "nice3point.revit.api.revitapi", "nice3point.revit.api.revitapiui" })
                refs.AddRange(Directory.GetFiles(Path.Combine(Nuget, pkg, RefVersion[year], "ref"), "*.dll", SearchOption.AllDirectories));
            var addin = Path.Combine(Root, "src", "PDI.RevitMcp.Addin", "bin", "Release", "R" + year);
            Assert.True(File.Exists(Path.Combine(addin, "PDI.RevitMcp.Addin.dll")),
                "falta el add-in compilado de " + year + " (" + addin + "): build.sh compila los add-ins antes de las pruebas");
            refs.AddRange(Directory.GetFiles(addin, "PDI.RevitMcp.*.dll"));
            return refs.ToArray();
        }

        private static (byte[] dll, string[] diags) Compile(string code, int year)
        {
            var w = ScriptSource.Wrap(code, "S" + Guid.NewGuid().ToString("N"));
            var dll = Compiler.Compile(w.Source, References(year), "prueba_" + year, out var diags, out _, out _);
            return (dll, diags);
        }

        public static string[] CapsOf(string code, int year) => Caps(code, year);

        private static string[] Caps(string code, int year)
        {
            var w = ScriptSource.Wrap(code, "S" + Guid.NewGuid().ToString("N"));
            var dll = Compiler.CompileWithCapabilities(w.Source, References(year), "cap_" + year, out var diags, out _, out _, out var caps);
            Assert.True(dll != null, string.Join("\n", diags));
            return caps;
        }

        [Theory, InlineData(2024), InlineData(2026)]
        public void BuiltInRecipes_PassRestrictedAutonomousPolicy(int year)
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "src", "PDI.RevitMcp.Addin", "recetas"), "*.cs"))
            {
                var caps = Caps(File.ReadAllText(f), year);
                var denied = PDI.RevitMcp.Engine.CapabilityPolicy.Denied(PDI.RevitMcp.Engine.ExecutionProfile.RestrictedAutonomous, caps);
                Assert.True(denied.Count == 0, Path.GetFileName(f) + ": " + string.Join(", ", denied));
            }
        }

        [Theory, InlineData(2026)]
        public void HostileCode_IsDetected_AndDeniedInAutonomous(int year)
        {
            var cases = new Dictionary<string, string>
            {
                ["proceso"] = "System.Diagnostics.Process.Start(\"cmd.exe\"); return 1;",
                ["red"] = "var c = new System.Net.Http.HttpClient(); return c;",
                ["reflexion"] = "var t = System.Type.GetType(\"X\"); return System.Activator.CreateInstance(t);",
                ["archivo_escritura"] = "System.IO.File.WriteAllText(\"c:/x.txt\", \"a\"); return 1;",
                ["revit_sync"] = "doc.SynchronizeWithCentral(new TransactWithCentralOptions(), new SynchronizeWithCentralOptions()); return 1;",
                ["revit_cerrar"] = "doc.Close(false); return 1;",
                ["salida_proceso"] = "System.Environment.Exit(0); return 1;",
            };
            foreach (var kv in cases)
            {
                var caps = Caps(kv.Value, year);
                Assert.Contains(caps, c => c.StartsWith(kv.Key + ":"));
                Assert.NotEmpty(PDI.RevitMcp.Engine.CapabilityPolicy.Denied(PDI.RevitMcp.Engine.ExecutionProfile.RestrictedAutonomous, caps));
                Assert.Empty(PDI.RevitMcp.Engine.CapabilityPolicy.Denied(PDI.RevitMcp.Engine.ExecutionProfile.TrustedInteractive, caps));
            }
            var w = Caps("System.IO.File.WriteAllText(\"c:/x.txt\", \"a\"); return 1;", year);
            Assert.Empty(PDI.RevitMcp.Engine.CapabilityPolicy.Denied(PDI.RevitMcp.Engine.ExecutionProfile.RestrictedAutonomous, w, new[] { "archivo_escritura" }));
            Assert.NotEmpty(PDI.RevitMcp.Engine.CapabilityPolicy.Denied(PDI.RevitMcp.Engine.ExecutionProfile.RestrictedAutonomous, Caps("System.Diagnostics.Process.Start(\"x\"); return 1;", year), new[] { "proceso" }));
            Assert.Empty(Caps("return new FilteredElementCollector(doc).GetElementCount();", year));
        }

        public static IEnumerable<object[]> Years() => new[] { 2022, 2023, 2024, 2025, 2026 }.Select(y => new object[] { y });

        [Theory, MemberData(nameof(Years))]
        public void Lavamanos_CompilesAgainstEachYearsApi(int year)
        {
            var code = File.ReadAllText(Path.Combine(Root, "tools", "aceptacion", "lavamanos_exec.cs"));
            var (dll, diags) = Compile(code, year);
            Assert.True(dll != null, year + ": " + string.Join("\n", diags));
        }

        /// <summary>Las recetas que trae el instalador (src/PDI.RevitMcp.Addin/recetas) y un script que usa Bim.* compilan en los 5 años.</summary>
        [Theory, MemberData(nameof(Years))]
        public void BuiltInRecipesAndBimHelpers_Compile(int year)
        {
            var dir = Path.Combine(Root, "src", "PDI.RevitMcp.Addin", "recetas");
            var files = Directory.GetFiles(dir, "*.cs");
            Assert.True(files.Length >= 4);
            foreach (var f in files)
            {
                var code = File.ReadAllText(f);
                Assert.True(ScriptSource.IsRecipe(code), Path.GetFileName(f) + " no se reconoce como receta");
                var (dll, diags) = Compile(code, year);
                Assert.True(dll != null, year + " " + Path.GetFileName(f) + ": " + string.Join("\n", diags));
            }
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "tools", "bench_060"), "mcp_*.cs").Concat(Directory.GetFiles(Path.Combine(Root, "tools", "aceptacion"), "*_060.cs")).Concat(Directory.GetFiles(Path.Combine(Root, "tools", "aceptacion", "0.9.0"), "*_090.cs")))
            {
                var (db, gb) = Compile(File.ReadAllText(f), year);
                Assert.True(db != null, year + " " + Path.GetFileName(f) + ": " + string.Join("\n", gb));
            }
            var body = "var v = Bim.Montaje.Vista3D(doc);\nvar g = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().FirstOrDefault();\n" +
                       "var vp = doc.ActiveView as ViewPlan;\nreturn new { g = g == null ? null : Bim.Grupos.Inspeccionar(g), r = vp == null ? null : Bim.Vistas.Rango(vp), x = Bim.Montaje.Transformacion(Transform.Identity).Mirrored };";
            var (d2, g2) = Compile(body, year);
            Assert.True(d2 != null, string.Join("\n", g2));
        }

        [Theory, MemberData(nameof(Years))]
        public void ExpressionAndBody_Compile(int year)
        {
            Assert.NotNull(Compile("doc.Title", year).dll);
            var body = "var n = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType().GetElementCount();\n" +
                       "ctx.Log(n);\nreturn new { n, mm = ctx.ToMm(1.0) };";
            var (dll, diags) = Compile(body, year);
            Assert.True(dll != null, string.Join("\n", diags));
        }

        /// <summary>Medición (no prueba): PDI_BENCH_OUT=archivo dotnet test --filter Bench_. Compilación en frío y en caliente por año.</summary>
        [Fact]
        public void Bench_CompileTimes()
        {
            var outPath = Environment.GetEnvironmentVariable("PDI_BENCH_OUT");
            if (string.IsNullOrEmpty(outPath)) return;
            var code = File.ReadAllText(Path.Combine(Root, "tools", "aceptacion", "lavamanos_exec.cs"));
            var lines = new List<string> { "año;primera_ms;siguientes_mediana_ms;referencias" };
            foreach (var y in new[] { 2022, 2023, 2024, 2025, 2026 })
            {
                var refs = References(y);
                var w = ScriptSource.Wrap(code, "B" + y);
                Compiler.Compile(w.Source, refs, "b" + y, out _, out _, out var first);
                var times = new List<long>();
                for (var i = 0; i < 7; i++) { Compiler.Compile(w.Source, refs, "b" + y + "_" + i, out _, out _, out var ms); times.Add(ms); }
                times.Sort();
                lines.Add(y + ";" + first + ";" + times[3] + ";" + refs.Length);
            }
            File.WriteAllLines(outPath, lines);
        }

        [Fact]
        public void Errors_PointToTheUsersLine()
        {
            var (dll, diags) = Compile("// comentario\nvar x = 1;\nreturn noExiste + x;", 2024);
            Assert.Null(dll);
            Assert.Contains(diags, d => d.StartsWith("error CS0103 l3", StringComparison.Ordinal));
        }

        [Fact]
        public void Wrap_HoistsUsingsAfterComments_AndDetectsForms()
        {
            var w = ScriptSource.Wrap("// nota\nusing System.Text.RegularExpressions;\nreturn Regex.IsMatch(\"a\", \"a\");", "X");
            Assert.Equal("cuerpo", w.Kind);
            Assert.Contains("using System.Text.RegularExpressions;", w.HoistedUsings);
            Assert.Equal("expresión", ScriptSource.Wrap("doc.Title", "X").Kind);
            Assert.Equal("clase", ScriptSource.Wrap("public static class A { public static object Run(PDI.RevitMcp.Addin.Scripting.ScriptContext ctx) => 1; }", "X").Kind);
        }
    }
}
