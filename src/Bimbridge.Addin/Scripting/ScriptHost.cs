// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Scripting
{
    /// <summary>
    /// Loads the compiler (roslyn\ folder next to the add-in) only when needed, compiles with a cache and returns the ready Run method.
    /// - Revit 2025–2026 (.NET 8): Roslyn lives in its own AssemblyLoadContext; if Revit or another add-in already loaded another Roslyn version, they do not clash.
    ///   The compiled script is loaded into the SAME context as this add-in, so it sees ScriptContext and the Revit API exactly as the add-in does.
    /// - Revit 2022–2024 (.NET Framework 4.8): AssemblyResolve over roslyn\ (only for names that exist there). If another add-in already loaded a
    ///   different version of System.Collections.Immutable or System.Memory, two versions may coexist: documented risk, not tested in Revit.
    /// Compilation runs outside the Revit thread; only Run runs inside it.
    /// </summary>
    public static class ScriptHost
    {
        public sealed class Compiled
        {
            public MethodInfo Run;
            public string Key;
            public long CompileMs;
            /// <summary>Bytes of the loaded assembly image (+ PDB): basis of the AUD-05 memory estimate.</summary>
            public long ImageBytes;
            public bool FromCache;
            public string[] Diagnostics;
            public string Kind;
            /// <summary>Recipe type (when the code defines a class that inherits from Recipe).</summary>
            public Type RecipeType;
            /// <summary>Explicit references used (path → version or error).</summary>
            public Dictionary<string, object> ExtraRefs;
            /// <summary>Sensitive capabilities detected by semantic analysis ("type:detail"). Enforced with CapabilityPolicy at the point of execution.</summary>
            public string[] Capabilities = new string[0];
        }

        private static readonly object Lock = new object();
        /// <summary>AUD-05: limit on distinct compilations (each one leaves an assembly loaded until Revit restarts) and estimated image size.</summary>
        private static readonly CompileCache<Compiled> Cache = new CompileCache<Compiled>(CompileCache<Compiled>.DefaultMaxEntries, c => c.ImageBytes);
        private static MethodInfo _compile;
        private static Func<string> _roslynVersion;
        private static string[] _references;
        private static string _refFingerprint;
        private static int _refAssemblyCount;
        private static string _loadError;
        private static int _seq;

        public static string AddinDir => Path.GetDirectoryName(typeof(ScriptHost).Assembly.Location);
        public static string RoslynDir => Path.Combine(AddinDir, "roslyn");
        public static bool Present => File.Exists(Path.Combine(RoslynDir, "Bimbridge.Scripting.dll"));
        public static string LoadError => _loadError;
        public static int CachedCount => Cache.Count;

        public static string RoslynVersion
        {
            get { try { return EnsureLoaded() ? _roslynVersion() : null; } catch (Exception) { return null; } }
        }

#if NET
        private sealed class RoslynContext : System.Runtime.Loader.AssemblyLoadContext
        {
            private readonly string _dir;
            public RoslynContext(string dir) : base("Bimbridge.Roslyn") { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                // Only Roslyn and our compiler come from roslyn\; the rest (System.*) is provided by the Revit runtime, which is newer.
                if (name.Name == null) return null;
                if (!name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) && name.Name != "Bimbridge.Scripting") return null;
                var path = Path.Combine(_dir, name.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }

        private static RoslynContext _alc;
#else
        private static bool _resolveHooked;

        private static Assembly ResolveFromRoslynDir(object sender, ResolveEventArgs e)
        {
            try
            {
                var name = new AssemblyName(e.Name);
                // 1) Already loaded with a sufficient version → reuse it. Essential for scripts: they are loaded with Assembly.Load(byte[])
                //    (no context) and their reference to Bimbridge.Addin is not found on its own, because Revit loads the add-in with LoadFrom.
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName an;
                    try { an = a.GetName(); } catch (Exception) { continue; }
                    if (string.Equals(an.Name, name.Name, StringComparison.OrdinalIgnoreCase) && (name.Version == null || an.Version >= name.Version)) return a;
                }
                // 2) Roslyn and its dependencies, from roslyn\.
                var path = Path.Combine(RoslynDir, name.Name + ".dll");
                if (!File.Exists(path)) return null;
                var have = AssemblyName.GetAssemblyName(path);
                if (name.Version != null && have.Version < name.Version) return null;
                return Assembly.LoadFrom(path);
            }
            catch (Exception) { return null; }
        }
#endif

        private static MethodInfo _recipeConstant;

        /// <summary>rc3: constant value of a recipe property read from the source code (Roslyn, without instantiating). null = not constant.</summary>
        public static string RecipeConstant(string source, string name)
        {
            if (!EnsureLoaded() || _recipeConstant == null) return null;
            return (string)_recipeConstant.Invoke(null, new object[] { source, name });
        }

        private static bool EnsureLoaded()
        {
            if (_compile != null) return true;
            lock (Lock)
            {
                if (_compile != null) return true;
                if (!Present) { _loadError = "the roslyn\\ folder with the compiler is missing next to the add-in (" + RoslynDir + "); reinstall"; return false; }
                try
                {
                    var path = Path.Combine(RoslynDir, "Bimbridge.Scripting.dll");
                    Assembly asm;
#if NET
                    _alc = new RoslynContext(RoslynDir);
                    asm = _alc.LoadFromAssemblyPath(path);
#else
                    if (!_resolveHooked) { AppDomain.CurrentDomain.AssemblyResolve += ResolveFromRoslynDir; _resolveHooked = true; }
                    asm = Assembly.LoadFrom(path);
#endif
                    var t = asm.GetType("Bimbridge.Scripting.Compiler", true);
                    _compile = t.GetMethod("CompileWithCapabilities", BindingFlags.Public | BindingFlags.Static);
                    _recipeConstant = asm.GetType("Bimbridge.Scripting.RecipeMetadata", true).GetMethod("Constant", BindingFlags.Public | BindingFlags.Static);
                    var v = t.GetProperty("RoslynVersion", BindingFlags.Public | BindingFlags.Static);
                    _roslynVersion = () => (string)v.GetValue(null);
                    _loadError = null;
                    return _compile != null;
                }
                catch (Exception ex)
                {
                    _loadError = "could not load the compiler: " + ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Compilation references: the .NET assemblies of the Revit runtime + RevitAPI/RevitAPIUI + the add-in ones + whatever is loaded in Revit.
        /// 0.6.0: recalculated when Revit has loaded new assemblies since the last time (it used to freeze on the first exec, H-C).
        /// If two versions share a name, the newest one is kept.
        /// </summary>
        public static string[] References()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (_references != null && loaded == _refAssemblyCount) return _references;
            _refAssemblyCount = loaded;
            var byName = new Dictionary<string, Tuple<Version, string>>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (path.StartsWith(RoslynDir, StringComparison.OrdinalIgnoreCase)) return;
                AssemblyName an;
                try { an = AssemblyName.GetAssemblyName(path); } catch (Exception) { return; }
                if (an.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)) return;
                if (!byName.TryGetValue(an.Name, out var cur) || (an.Version != null && an.Version > cur.Item1))
                    byName[an.Name] = Tuple.Create(an.Version, path);
            }
            // Runtime: every System.* in the runtime folder (on .NET 8 these are the implementations; on 4.8, the framework GAC).
            var rt = Path.GetDirectoryName(typeof(object).Assembly.Location);
            foreach (var f in Directory.GetFiles(rt, "*.dll"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.StartsWith("System", StringComparison.OrdinalIgnoreCase) || n == "mscorlib" || n == "netstandard" || n == "Microsoft.CSharp" || n == "Microsoft.Win32.Primitives")
                    Add(f);
            }
#if !NET
            var facades = Path.Combine(rt, "Facades");
            if (Directory.Exists(facades)) foreach (var f in Directory.GetFiles(facades, "*.dll")) Add(f);
#endif
            // What is already loaded in Revit (RevitAPI, RevitAPIUI, netstandard, the add-in…).
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) Add(a.Location); } catch (Exception) { }
            }
            Add(typeof(Autodesk.Revit.DB.Document).Assembly.Location);
            Add(typeof(Autodesk.Revit.UI.UIApplication).Assembly.Location);
            Add(typeof(ScriptHost).Assembly.Location);
            Add(typeof(Bimbridge.Engine.ScriptSource).Assembly.Location);
            Add(typeof(Bimbridge.Protocol.Json).Assembly.Location);
            _references = byName.Values.Select(v => v.Item2).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            _refFingerprint = Home.Sha256Hex(string.Join("|", byName.OrderBy(k => k.Key).Select(k => k.Key + "/" + k.Value.Item1)), 16);
            return _references;
        }

        /// <summary>
        /// Explicit references (refs=path.dll[,…]): they are resolved, loaded into the add-in context so the script finds them
        /// at run time, and included in the compilation fingerprint (size + date). Returns null and the reason if any fails.
        /// </summary>
        public static List<string> ResolveExtraRefs(IList<string> paths, Dictionary<string, object> report, out string fingerprint, out string error)
        {
            error = null;
            fingerprint = "";
            var list = new List<string>();
            if (paths == null) return list;
            var fp = new List<string>();
            foreach (var raw in paths)
            {
                var p = raw?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { error = "refs: '" + p + "' is not a .dll"; return null; }
                if (!File.Exists(p)) { error = "refs: does not exist '" + p + "'"; return null; }
                AssemblyName an;
                try { an = AssemblyName.GetAssemblyName(p); }
                catch (Exception ex) { error = "refs: '" + p + "' is not a .NET assembly (" + ex.Message + ")"; return null; }
                try
                {
                    var already = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                    {
                        try { return string.Equals(a.GetName().Name, an.Name, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
                    });
                    if (already != null)
                    {
                        var have = already.GetName().Version;
                        report[p] = "already loaded " + an.Name + " " + have + (have < an.Version ? " (OLDER than the requested " + an.Version + ": the loaded one will be used; restart Revit to use the new one)" : "");
                    }
                    else
                    {
#if NET
                        var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly) ?? System.Runtime.Loader.AssemblyLoadContext.Default;
                        ctx.LoadFromAssemblyPath(Path.GetFullPath(p));
#else
                        Assembly.LoadFrom(p);
#endif
                        report[p] = "loaded " + an.Name + " " + an.Version;
                    }
                }
                catch (Exception ex) { error = "refs: could not load '" + p + "': " + ex.Message; return null; }
                var fi = new FileInfo(p);
                fp.Add(an.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                list.Add(Path.GetFullPath(p));
            }
            fingerprint = Home.Sha256Hex(string.Join(";", fp), 16);
            return list;
        }

        /// <summary>Compiles (or takes from the cache) and returns the Run method. Outside the Revit thread.</summary>
        public static Compiled Compile(string code, string revitVersion, string addinVersion, out string error) =>
            Compile(code, revitVersion, addinVersion, null, out error);

        /// <summary>Compiles with optional explicit references (refs=). Outside the Revit thread.</summary>
        public static Compiled Compile(string code, string revitVersion, string addinVersion, IList<string> extraRefs, out string error)
        {
            error = null;
            if (!EnsureLoaded()) { error = _loadError; return null; }
            var extraReport = new Dictionary<string, object>();
            var extra = ResolveExtraRefs(extraRefs, extraReport, out var extraFp, out error);
            if (extra == null) return null;
            var refs = References().Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var className = "S" + System.Threading.Interlocked.Increment(ref _seq);
            // The cache key does not depend on the class name: it is wrapped with a fixed name for the fingerprint.
            var probe = ScriptSource.Wrap(code, "S");
            var key = ScriptSource.CacheKey(probe.Source, revitVersion, addinVersion, _refFingerprint + extraFp);
            // Issue #9: single-flight. Two simultaneous requests with the same code compile only once.
            string compileError = null;
            // AUD-05: accounting per agent (or session) using the correlation of the current request.
            var owner = Correlation.Current?.AgentId ?? Correlation.Current?.SessionId;
            var got = Cache.GetOrCompile(key, owner, () =>
            {
                var c = CompileNew(code, className, refs, key, out compileError);
                return c == null ? null : Tuple.Create(c, c.CompileMs);
            }, out var fromCache, out var refusal);
            if (got == null) { error = refusal ?? compileError ?? "does not compile"; return null; }
            if (!fromCache) { got.ExtraRefs = extraReport.Count > 0 ? extraReport : null; return got; }
            return new Compiled
            {
                Run = got.Run, Key = key, CompileMs = 0, FromCache = true, Diagnostics = got.Diagnostics, Kind = got.Kind, RecipeType = got.RecipeType, Capabilities = got.Capabilities,
                ExtraRefs = extraReport.Count > 0 ? extraReport : null,
            };
        }

        /// <summary>Compilation cache counters (Status panel, diagnostics).</summary>
        public static Dictionary<string, object> CacheStats() => Cache.Stats();

        private static Compiled CompileNew(string code, string className, string[] refs, string key, out string error)
        {
            error = null;
            var wrapped = ScriptSource.Wrap(code, className);
            var args = new object[] { wrapped.Source, refs, "BimbridgeScript_" + key.Substring(0, 12) + "_" + className, null, null, 0L, null };
            var image = (byte[])_compile.Invoke(null, args);
            var diags = (string[])args[3];
            var pdb = (byte[])args[4];
            var ms = (long)args[5];
            var caps = (string[])args[6] ?? new string[0];
            if (image == null)
            {
                error = "does not compile:\n" + string.Join("\n", diags ?? new string[0]);
                return null;
            }
            Assembly asm;
#if NET
            var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly) ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            using (var pe = new MemoryStream(image))
            using (var sym = pdb != null ? new MemoryStream(pdb) : null)
                asm = ctx.LoadFromStream(pe, sym);
#else
            asm = pdb != null ? Assembly.Load(image, pdb) : Assembly.Load(image);
#endif
            MethodInfo run = null;
            Type recipe = null;
            foreach (var t in asm.GetTypes())
            {
                if (recipe == null && !t.IsAbstract && typeof(Recipe).IsAssignableFrom(t)) recipe = t;
                var m = t.GetMethod(ScriptSource.EntryMethod, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ScriptContext) }, null);
                if (run == null && m != null && m.ReturnType == typeof(object)) run = m;
            }
            if (run == null && recipe == null)
            {
                error = wrapped.Kind == "receta"
                    ? "compiled, but there is no public non-abstract class inheriting from Recipe"
                    : "compiled, but no class has 'public static object Run(ScriptContext ctx)'";
                return null;
            }
            return new Compiled
            {
                Run = run, Key = key, CompileMs = ms, ImageBytes = image.Length + (pdb?.Length ?? 0),
                FromCache = false, Diagnostics = diags, Kind = wrapped.Kind, RecipeType = recipe, Capabilities = caps,
            };
        }

        /// <summary>Loads Roslyn and compiles something trivial in the background (so the first real exec is faster). Never throws.</summary>
        public static void WarmInBackground(Action<string> log)
        {
            if (!Present || _compile != null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var c = Compile("1", "warm", "warm", out var err);
                    log?.Invoke(c != null ? "Runner compiler ready in " + sw.ElapsedMilliseconds + " ms" : "Runner compiler: " + err);
                }
                catch (Exception ex) { log?.Invoke("Runner compiler: " + ex.Message); }
            });
        }
    }
}
