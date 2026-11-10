// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace PDI.RevitMcp.Scripting
{
    /// <summary>
    /// Compila C# en memoria con Roslyn. API sólo con tipos de la BCL para que el add-in lo llame por reflexión desde otro contexto de carga.
    /// No ejecuta nada: devuelve el ensamblado (y su PDB) para que el add-in lo cargue y lo corra en el hilo de Revit.
    /// </summary>
    public static class Compiler
    {
        private static readonly ConcurrentDictionary<string, MetadataReference> RefCache = new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Versión de Roslyn cargada (para ctx y la matriz).</summary>
        public static string RoslynVersion => typeof(CSharpCompilation).Assembly.GetName().Version.ToString();

        /// <summary>
        /// Compila <paramref name="source"/>. Devuelve el ensamblado o null si hay errores.
        /// diagnostics: "error CS0103 l3 c5: mensaje" (líneas del código del usuario gracias a #line). pdb: símbolos portables para trazas con línea.
        /// </summary>
        public static byte[] Compile(string source, string[] references, string assemblyName, out string[] diagnostics, out byte[] pdb, out long ms)
            => CompileWithCapabilities(source, references, assemblyName, out diagnostics, out pdb, out ms, out _);

        /// <summary>Como Compile, y además devuelve las capacidades sensibles que el código usa ("tipo:detalle"), por análisis semántico.
        /// Es una política estática, NO un sandbox: el código compilado corre con los permisos del proceso de Revit.</summary>
        public static byte[] CompileWithCapabilities(string source, string[] references, string assemblyName, out string[] diagnostics, out byte[] pdb, out long ms, out string[] capabilities)
        {
            capabilities = new string[0];
            var sw = Stopwatch.StartNew();
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "script.cs", encoding: System.Text.Encoding.UTF8);
            var refs = new List<MetadataReference>();
            var skipped = new List<string>();
            foreach (var path in references.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var r = Reference(path);
                if (r != null) refs.Add(r); else skipped.Add(Path.GetFileName(path));
            }
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                platform: Platform.AnyCpu,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Disable,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
                {
                    // referencias a versiones distintas del mismo ensamblado: Roslyn unifica, no es un error del usuario
                    ["CS1701"] = ReportDiagnostic.Suppress,
                    ["CS1702"] = ReportDiagnostic.Suppress,
                    ["CS1705"] = ReportDiagnostic.Warn,
                });
            var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, refs, options);
            using (var pe = new MemoryStream())
            using (var symbols = new MemoryStream())
            {
                var result = compilation.Emit(pe, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                diagnostics = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && d.WarningLevel <= 2))
                    .OrderByDescending(d => d.Severity)
                    .Take(40)
                    .Select(Format)
                    .Concat(skipped.Count > 0 ? new[] { "aviso: referencias no legibles omitidas: " + string.Join(", ", skipped.Take(10)) } : new string[0])
                    .ToArray();
                ms = sw.ElapsedMilliseconds;
                if (!result.Success) { pdb = null; return null; }
                capabilities = Capabilities(compilation, tree);
                pdb = symbols.ToArray();
                return pe.ToArray();
            }
        }

        // tipo (prefijo de nombre completo) → capacidad. Miembro "*" = cualquier miembro.
        private static readonly (string type, string member, string cap)[] Rules =
        {
            ("System.Diagnostics.Process", "*", "proceso"),
            ("System.Net.", "*", "red"),
            ("System.Reflection.Assembly", "*", "reflexion"),
            ("System.Reflection.Module", "*", "reflexion"),
            ("System.AppDomain", "*", "reflexion"),
            ("System.Runtime.Loader.", "*", "reflexion"),
            ("System.Delegate", "DynamicInvoke", "reflexion"),
            ("System.Delegate", "CreateDelegate", "reflexion"),
            ("System.Linq.Expressions.LambdaExpression", "Compile", "reflexion"),
            ("System.Linq.Expressions.Expression<TDelegate>", "Compile", "reflexion"),
            ("System.Type", "GetMethod*", "reflexion"), ("System.Type", "GetProperty*", "reflexion"), ("System.Type", "GetField*", "reflexion"),
            ("System.Type", "GetMember*", "reflexion"), ("System.Type", "GetConstructor*", "reflexion"), ("System.Type", "GetTypeFromProgID", "reflexion"),
            ("System.Type", "GetTypeFromCLSID", "reflexion"),
            ("System.IO.Pipes.", "*", "red"),
            ("System.Threading.Tasks.Task", "Run", "hilos"), ("System.Threading.ThreadPool", "*", "hilos"),
            ("System.Reflection.MethodBase", "Invoke", "reflexion"),
            ("System.Reflection.MethodInfo", "Invoke", "reflexion"),
            ("System.Reflection.PropertyInfo", "SetValue", "reflexion"),
            ("System.Reflection.FieldInfo", "SetValue", "reflexion"),
            ("System.Reflection.Emit.", "*", "reflexion"),
            ("System.Activator", "*", "reflexion"),
            ("System.Type", "GetType", "reflexion"),
            ("System.Type", "InvokeMember", "reflexion"),
            ("System.Runtime.InteropServices.Marshal", "*", "nativo"),
            ("System.Runtime.InteropServices.NativeLibrary", "*", "nativo"),
            ("Microsoft.Win32.Registry", "*", "registro"),
            ("System.Environment", "Exit", "salida_proceso"),
            ("System.Environment", "FailFast", "salida_proceso"),
            ("System.Environment", "SetEnvironmentVariable", "entorno"),
            ("System.IO.File", "Write*", "archivo_escritura"), ("System.IO.File", "Append*", "archivo_escritura"),
            ("System.IO.File", "Delete", "archivo_escritura"), ("System.IO.File", "Move", "archivo_escritura"),
            ("System.IO.File", "Copy", "archivo_escritura"), ("System.IO.File", "Replace", "archivo_escritura"),
            ("System.IO.File", "Create*", "archivo_escritura"), ("System.IO.File", "Open*", "archivo_escritura"),
            ("System.IO.Directory", "Delete", "archivo_escritura"), ("System.IO.Directory", "Move", "archivo_escritura"),
            ("System.IO.FileInfo", "Delete", "archivo_escritura"), ("System.IO.FileInfo", "MoveTo", "archivo_escritura"),
            ("System.IO.FileStream", ".ctor", "archivo_escritura"), ("System.IO.StreamWriter", ".ctor", "archivo_escritura"),
            ("System.Threading.Thread", ".ctor", "hilos"),
            ("Autodesk.Revit.DB.Document", "Save", "revit_guardar"), ("Autodesk.Revit.DB.Document", "SaveAs", "revit_guardar"),
            ("Autodesk.Revit.DB.Document", "SynchronizeWithCentral", "revit_sync"),
            ("Autodesk.Revit.DB.Document", "Export", "revit_exportar"),
            ("Autodesk.Revit.DB.Document", "Close", "revit_cerrar"),
            ("Autodesk.Revit.UI.UIApplication", "OpenAndActivateDocument", "revit_abrir"),
            ("Autodesk.Revit.ApplicationServices.Application", "OpenDocumentFile", "revit_abrir"),
            ("Autodesk.Revit.UI.UIDocument", "SaveAndClose", "revit_cerrar"),
            ("Autodesk.Revit.DB.WorksharingUtils", "*", "revit_sync"),
            // rc3: las transacciones son del motor. Código autónomo que abre las suyas (o encola comandos de la UI) podría escribir
            // fuera de Ejecutar o fuera de la auditoría: siempre denegado en el perfil restringido.
            ("Autodesk.Revit.DB.Transaction", "*", "revit_transaccion"), ("Autodesk.Revit.DB.SubTransaction", "*", "revit_transaccion"),
            ("Autodesk.Revit.DB.TransactionGroup", "*", "revit_transaccion"), ("Autodesk.Revit.UI.UIApplication", "PostCommand", "revit_transaccion"),
            ("System.Runtime.CompilerServices.ModuleInitializerAttribute", "*", "reflexion"),
        };

        private static bool Match(string pattern, string value) =>
            pattern == "*" || (pattern.EndsWith("*") ? value.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal) : value == pattern);

        public static string[] Capabilities(CSharpCompilation compilation, SyntaxTree tree)
        {
            var model = compilation.GetSemanticModel(tree);
            var found = new SortedSet<string>(StringComparer.Ordinal);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                if (node is ExternAliasDirectiveSyntax) found.Add("nativo:extern alias");
                if (node is MethodDeclarationSyntax md && md.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword))) found.Add("nativo:extern " + md.Identifier.Text);
                if (node is UnsafeStatementSyntax) found.Add("nativo:unsafe");
                if (node is IdentifierNameSyntax idn && idn.Identifier.Text == "dynamic" && model.GetTypeInfo(idn).Type?.TypeKind == TypeKind.Dynamic) found.Add("reflexion:dynamic");
                ISymbol sym = null;
                if (node is InvocationExpressionSyntax || node is ObjectCreationExpressionSyntax || node is ImplicitObjectCreationExpressionSyntax || node is MemberAccessExpressionSyntax)
                    sym = model.GetSymbolInfo(node).Symbol ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault();
                if (sym == null) continue;
                var type = sym.ContainingType?.OriginalDefinition.ToDisplayString() ?? "";
                var member = sym is IMethodSymbol ms && ms.MethodKind == MethodKind.Constructor ? ".ctor" : sym.Name;
                foreach (var r in Rules)
                {
                    var typeOk = r.type.EndsWith(".") ? type.StartsWith(r.type, StringComparison.Ordinal) : type == r.type;
                    if (typeOk && Match(r.member, member)) found.Add(r.cap + ":" + type + "." + member);
                }
            }
            return found.ToArray();
        }

        private static string Format(Diagnostic d)
        {
            var span = d.Location.GetMappedLineSpan();
            var where = span.IsValid ? " l" + (span.StartLinePosition.Line + 1) + " c" + (span.StartLinePosition.Character + 1) : "";
            var sev = d.Severity == DiagnosticSeverity.Error ? "error" : "aviso";
            return sev + " " + d.Id + where + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Referencia de metadatos en caché (leer RevitAPI.dll cuesta cientos de ms la primera vez). null si no es un ensamblado .NET.</summary>
        private static MetadataReference Reference(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var stamp = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (RefCache.TryGetValue(stamp, out var cached)) return cached;
            try
            {
                var md = AssemblyMetadata.CreateFromFile(path);
                var mods = md.GetModules(); // fuerza la lectura: un DLL nativo falla aquí y se omite
                // Un módulo sin manifiesto de ensamblado (p. ej. System.EnterpriseServices.Wrapper.dll) rompe la compilación con CS1509: se omite.
                if (mods.Length == 0 || !mods[0].GetMetadataReader().IsAssembly) return null;
                var r = md.GetReference(filePath: path);
                RefCache[stamp] = r;
                return r;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Compila algo trivial para cargar Roslyn y dejarlo en caliente (el primer exec real tarda menos).</summary>
        public static long Warm(string[] references)
        {
            Compile("public static class W { public static object Run() { return 1; } }", references, "PdiWarm", out _, out _, out var ms);
            return ms;
        }
    }
}
