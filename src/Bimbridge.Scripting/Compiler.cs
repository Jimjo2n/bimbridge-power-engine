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

namespace Bimbridge.Scripting
{
    /// <summary>
    /// Compiles C# in memory with Roslyn. The API uses only BCL types so the add-in can call it by reflection from another load context.
    /// It runs nothing: it returns the assembly (and its PDB) for the add-in to load and run on the Revit thread.
    /// </summary>
    public static class Compiler
    {
        private static readonly ConcurrentDictionary<string, MetadataReference> RefCache = new ConcurrentDictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loaded Roslyn version (for ctx and the matrix).</summary>
        public static string RoslynVersion => typeof(CSharpCompilation).Assembly.GetName().Version.ToString();

        /// <summary>
        /// Compiles <paramref name="source"/>. Returns the assembly, or null if there are errors.
        /// diagnostics: "error CS0103 l3 c5: message" (user code lines thanks to #line). pdb: portable symbols for stack traces with line numbers.
        /// </summary>
        public static byte[] Compile(string source, string[] references, string assemblyName, out string[] diagnostics, out byte[] pdb, out long ms)
            => CompileWithCapabilities(source, references, assemblyName, out diagnostics, out pdb, out ms, out _);

        /// <summary>Like Compile, and also returns the sensitive capabilities the code uses ("type:detail"), by semantic analysis.
        /// It is a static policy, NOT a sandbox: the compiled code runs with the permissions of the Revit process.</summary>
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
                    // references to different versions of the same assembly: Roslyn unifies them, not a user error
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
                    .Concat(skipped.Count > 0 ? new[] { "warning: unreadable references skipped: " + string.Join(", ", skipped.Take(10)) } : new string[0])
                    .ToArray();
                ms = sw.ElapsedMilliseconds;
                if (!result.Success) { pdb = null; return null; }
                capabilities = Capabilities(compilation, tree);
                pdb = symbols.ToArray();
                return pe.ToArray();
            }
        }

        // type (full-name prefix) → capability. Member "*" = any member.
        private static readonly (string type, string member, string cap)[] Rules =
        {
            ("System.Diagnostics.Process", "*", "process"),
            ("System.Net.", "*", "network"),
            ("System.Reflection.Assembly", "*", "reflection"),
            ("System.Reflection.Module", "*", "reflection"),
            ("System.AppDomain", "*", "reflection"),
            ("System.Runtime.Loader.", "*", "reflection"),
            ("System.Delegate", "DynamicInvoke", "reflection"),
            ("System.Delegate", "CreateDelegate", "reflection"),
            ("System.Linq.Expressions.LambdaExpression", "Compile", "reflection"),
            ("System.Linq.Expressions.Expression<TDelegate>", "Compile", "reflection"),
            ("System.Type", "GetMethod*", "reflection"), ("System.Type", "GetProperty*", "reflection"), ("System.Type", "GetField*", "reflection"),
            ("System.Type", "GetMember*", "reflection"), ("System.Type", "GetConstructor*", "reflection"), ("System.Type", "GetTypeFromProgID", "reflection"),
            ("System.Type", "GetTypeFromCLSID", "reflection"),
            ("System.IO.Pipes.", "*", "network"),
            ("System.Threading.Tasks.Task", "Run", "threads"), ("System.Threading.ThreadPool", "*", "threads"),
            ("System.Reflection.MethodBase", "Invoke", "reflection"),
            ("System.Reflection.MethodInfo", "Invoke", "reflection"),
            ("System.Reflection.PropertyInfo", "SetValue", "reflection"),
            ("System.Reflection.FieldInfo", "SetValue", "reflection"),
            ("System.Reflection.Emit.", "*", "reflection"),
            ("System.Activator", "*", "reflection"),
            ("System.Type", "GetType", "reflection"),
            ("System.Type", "InvokeMember", "reflection"),
            ("System.Runtime.InteropServices.Marshal", "*", "native"),
            ("System.Runtime.InteropServices.NativeLibrary", "*", "native"),
            ("Microsoft.Win32.Registry", "*", "registry"),
            ("System.Environment", "Exit", "process_output"),
            ("System.Environment", "FailFast", "process_output"),
            ("System.Environment", "SetEnvironmentVariable", "environment"),
            ("System.IO.File", "Write*", "write_file"), ("System.IO.File", "Append*", "write_file"),
            ("System.IO.File", "Delete", "write_file"), ("System.IO.File", "Move", "write_file"),
            ("System.IO.File", "Copy", "write_file"), ("System.IO.File", "Replace", "write_file"),
            ("System.IO.File", "Create*", "write_file"), ("System.IO.File", "Open*", "write_file"),
            ("System.IO.Directory", "Delete", "write_file"), ("System.IO.Directory", "Move", "write_file"),
            ("System.IO.FileInfo", "Delete", "write_file"), ("System.IO.FileInfo", "MoveTo", "write_file"),
            ("System.IO.FileStream", ".ctor", "write_file"), ("System.IO.StreamWriter", ".ctor", "write_file"),
            ("System.Threading.Thread", ".ctor", "threads"),
            ("Autodesk.Revit.DB.Document", "Save", "revit_save"), ("Autodesk.Revit.DB.Document", "SaveAs", "revit_save"),
            ("Autodesk.Revit.DB.Document", "SynchronizeWithCentral", "revit_sync"),
            ("Autodesk.Revit.DB.Document", "Export", "revit_export"),
            ("Autodesk.Revit.DB.Document", "Close", "revit_close"),
            ("Autodesk.Revit.UI.UIApplication", "OpenAndActivateDocument", "revit_open"),
            ("Autodesk.Revit.ApplicationServices.Application", "OpenDocumentFile", "revit_open"),
            ("Autodesk.Revit.UI.UIDocument", "SaveAndClose", "revit_close"),
            ("Autodesk.Revit.DB.WorksharingUtils", "*", "revit_sync"),
            // rc3: transactions belong to the engine. Standalone code that opens its own (or queues UI commands) could write
            // outside Run or outside the audit: always denied in the restricted profile.
            ("Autodesk.Revit.DB.Transaction", "*", "revit_transaction"), ("Autodesk.Revit.DB.SubTransaction", "*", "revit_transaction"),
            ("Autodesk.Revit.DB.TransactionGroup", "*", "revit_transaction"), ("Autodesk.Revit.UI.UIApplication", "PostCommand", "revit_transaction"),
            ("System.Runtime.CompilerServices.ModuleInitializerAttribute", "*", "reflection"),
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
                if (node is ExternAliasDirectiveSyntax) found.Add("native:extern alias");
                if (node is MethodDeclarationSyntax md && md.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword))) found.Add("native:extern " + md.Identifier.Text);
                if (node is UnsafeStatementSyntax) found.Add("native:unsafe");
                if (node is IdentifierNameSyntax idn && idn.Identifier.Text == "dynamic" && model.GetTypeInfo(idn).Type?.TypeKind == TypeKind.Dynamic) found.Add("reflection:dynamic");
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
            var sev = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            return sev + " " + d.Id + where + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Cached metadata reference (reading RevitAPI.dll costs hundreds of ms the first time). null if it is not a .NET assembly.</summary>
        private static MetadataReference Reference(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var stamp = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (RefCache.TryGetValue(stamp, out var cached)) return cached;
            try
            {
                var md = AssemblyMetadata.CreateFromFile(path);
                var mods = md.GetModules(); // forces the read: a native DLL fails here and is skipped
                // A module without an assembly manifest (e.g. System.EnterpriseServices.Wrapper.dll) breaks compilation with CS1509: skipped.
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

        /// <summary>Compiles something trivial to load Roslyn and keep it warm (the first real exec is faster).</summary>
        public static long Warm(string[] references)
        {
            Compile("public static class W { public static object Run() { return 1; } }", references, "BimbridgeWarm", out _, out _, out var ms);
            return ms;
        }
    }
}
