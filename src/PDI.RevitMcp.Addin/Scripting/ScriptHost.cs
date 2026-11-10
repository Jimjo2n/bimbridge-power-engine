// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Scripting
{
    /// <summary>
    /// Carga el compilador (carpeta roslyn\ junto al add-in) sólo cuando hace falta, compila con caché y devuelve el método Run listo.
    /// - Revit 2025–2026 (.NET 8): Roslyn vive en un AssemblyLoadContext propio; si Revit u otro add-in ya cargó otra versión de Roslyn, no chocan.
    ///   El script compilado se carga en el MISMO contexto que este add-in, para que vea ScriptContext y la API de Revit tal como las ve el add-in.
    /// - Revit 2022–2024 (.NET Framework 4.8): AssemblyResolve sobre roslyn\ (sólo para nombres que existan ahí). Si otro add-in ya cargó una
    ///   versión distinta de System.Collections.Immutable o System.Memory, pueden convivir dos versiones: riesgo documentado, no probado en Revit.
    /// La compilación corre fuera del hilo de Revit; sólo Run corre dentro.
    /// </summary>
    public static class ScriptHost
    {
        public sealed class Compiled
        {
            public MethodInfo Run;
            public string Key;
            public long CompileMs;
            public bool FromCache;
            public string[] Diagnostics;
            public string Kind;
            /// <summary>Tipo de la receta (cuando el código define una clase que hereda de Receta).</summary>
            public Type RecipeType;
            /// <summary>Referencias explícitas usadas (ruta → versión o error).</summary>
            public Dictionary<string, object> ExtraRefs;
            /// <summary>Capacidades sensibles detectadas por análisis semántico ("tipo:detalle"). Se aplican con CapabilityPolicy en el punto de ejecución.</summary>
            public string[] Capabilities = new string[0];
        }

        private static readonly object Lock = new object();
        private static readonly CompileCache<Compiled> Cache = new CompileCache<Compiled>();
        private static MethodInfo _compile;
        private static Func<string> _roslynVersion;
        private static string[] _references;
        private static string _refFingerprint;
        private static int _refAssemblyCount;
        private static string _loadError;
        private static int _seq;

        public static string AddinDir => Path.GetDirectoryName(typeof(ScriptHost).Assembly.Location);
        public static string RoslynDir => Path.Combine(AddinDir, "roslyn");
        public static bool Present => File.Exists(Path.Combine(RoslynDir, "PDI.RevitMcp.Scripting.dll"));
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
            public RoslynContext(string dir) : base("PDI.RevitMcp.Roslyn") { _dir = dir; }

            protected override Assembly Load(AssemblyName name)
            {
                // Sólo Roslyn y nuestro compilador salen de roslyn\; el resto (System.*) lo da el runtime de Revit, que es más nuevo.
                if (name.Name == null) return null;
                if (!name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) && name.Name != "PDI.RevitMcp.Scripting") return null;
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
                // 1) Ya cargado con versión suficiente → se reutiliza. Imprescindible para los scripts: se cargan con Assembly.Load(byte[])
                //    (sin contexto) y su referencia a PDI.RevitMcp.Addin no se encuentra sola, porque Revit carga el add-in con LoadFrom.
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName an;
                    try { an = a.GetName(); } catch (Exception) { continue; }
                    if (string.Equals(an.Name, name.Name, StringComparison.OrdinalIgnoreCase) && (name.Version == null || an.Version >= name.Version)) return a;
                }
                // 2) Roslyn y sus dependencias, desde roslyn\.
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

        /// <summary>rc3: valor constante de una propiedad de la receta leído del código fuente (Roslyn, sin instanciar). null = no constante.</summary>
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
                if (!Present) { _loadError = "no está la carpeta roslyn\\ con el compilador junto al add-in (" + RoslynDir + "); reinstale"; return false; }
                try
                {
                    var path = Path.Combine(RoslynDir, "PDI.RevitMcp.Scripting.dll");
                    Assembly asm;
#if NET
                    _alc = new RoslynContext(RoslynDir);
                    asm = _alc.LoadFromAssemblyPath(path);
#else
                    if (!_resolveHooked) { AppDomain.CurrentDomain.AssemblyResolve += ResolveFromRoslynDir; _resolveHooked = true; }
                    asm = Assembly.LoadFrom(path);
#endif
                    var t = asm.GetType("PDI.RevitMcp.Scripting.Compiler", true);
                    _compile = t.GetMethod("CompileWithCapabilities", BindingFlags.Public | BindingFlags.Static);
                    _recipeConstant = asm.GetType("PDI.RevitMcp.Scripting.RecipeMetadata", true).GetMethod("Constant", BindingFlags.Public | BindingFlags.Static);
                    var v = t.GetProperty("RoslynVersion", BindingFlags.Public | BindingFlags.Static);
                    _roslynVersion = () => (string)v.GetValue(null);
                    _loadError = null;
                    return _compile != null;
                }
                catch (Exception ex)
                {
                    _loadError = "no se pudo cargar el compilador: " + ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Referencias de compilación: los ensamblados .NET del runtime de Revit + RevitAPI/RevitAPIUI + los del add-in + lo cargado en Revit.
        /// 0.6.0: se recalcula cuando Revit cargó ensamblados nuevos desde la última vez (antes se congelaba en el primer exec, H-C).
        /// Si dos versiones tienen el mismo nombre, se queda la más nueva.
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
            // Runtime: todos los System.* de la carpeta del runtime (en .NET 8 son las implementaciones; en 4.8, el GAC de framework).
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
            // Lo que ya está cargado en Revit (RevitAPI, RevitAPIUI, netstandard, el add-in…).
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) Add(a.Location); } catch (Exception) { }
            }
            Add(typeof(Autodesk.Revit.DB.Document).Assembly.Location);
            Add(typeof(Autodesk.Revit.UI.UIApplication).Assembly.Location);
            Add(typeof(ScriptHost).Assembly.Location);
            Add(typeof(PDI.RevitMcp.Engine.ScriptSource).Assembly.Location);
            Add(typeof(PDI.RevitMcp.Protocol.Json).Assembly.Location);
            _references = byName.Values.Select(v => v.Item2).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            _refFingerprint = Home.Sha256Hex(string.Join("|", byName.OrderBy(k => k.Key).Select(k => k.Key + "/" + k.Value.Item1)), 16);
            return _references;
        }

        /// <summary>
        /// Referencias explícitas (refs=ruta.dll[,…]): se resuelven, se cargan en el contexto del add-in para que el script las encuentre
        /// al correr y entran en la huella de compilación (tamaño + fecha). Devuelve null y el motivo si alguna falla.
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
                if (!p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { error = "refs: '" + p + "' no es un .dll"; return null; }
                if (!File.Exists(p)) { error = "refs: no existe '" + p + "'"; return null; }
                AssemblyName an;
                try { an = AssemblyName.GetAssemblyName(p); }
                catch (Exception ex) { error = "refs: '" + p + "' no es un ensamblado .NET (" + ex.Message + ")"; return null; }
                try
                {
                    var already = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                    {
                        try { return string.Equals(a.GetName().Name, an.Name, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
                    });
                    if (already != null)
                    {
                        var have = already.GetName().Version;
                        report[p] = "ya cargado " + an.Name + " " + have + (have < an.Version ? " (MÁS VIEJO que el pedido " + an.Version + ": se usará el cargado; reinicie Revit para usar el nuevo)" : "");
                    }
                    else
                    {
#if NET
                        var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(ScriptHost).Assembly) ?? System.Runtime.Loader.AssemblyLoadContext.Default;
                        ctx.LoadFromAssemblyPath(Path.GetFullPath(p));
#else
                        Assembly.LoadFrom(p);
#endif
                        report[p] = "cargado " + an.Name + " " + an.Version;
                    }
                }
                catch (Exception ex) { error = "refs: no se pudo cargar '" + p + "': " + ex.Message; return null; }
                var fi = new FileInfo(p);
                fp.Add(an.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                list.Add(Path.GetFullPath(p));
            }
            fingerprint = Home.Sha256Hex(string.Join(";", fp), 16);
            return list;
        }

        /// <summary>Compila (o toma de la caché) y devuelve el método Run. Fuera del hilo de Revit.</summary>
        public static Compiled Compile(string code, string revitVersion, string addinVersion, out string error) =>
            Compile(code, revitVersion, addinVersion, null, out error);

        /// <summary>Compila con referencias explícitas opcionales (refs=). Fuera del hilo de Revit.</summary>
        public static Compiled Compile(string code, string revitVersion, string addinVersion, IList<string> extraRefs, out string error)
        {
            error = null;
            if (!EnsureLoaded()) { error = _loadError; return null; }
            var extraReport = new Dictionary<string, object>();
            var extra = ResolveExtraRefs(extraRefs, extraReport, out var extraFp, out error);
            if (extra == null) return null;
            var refs = References().Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var className = "S" + System.Threading.Interlocked.Increment(ref _seq);
            // La clave de caché no depende del nombre de clase: se envuelve con un nombre fijo para la huella.
            var probe = ScriptSource.Wrap(code, "S");
            var key = ScriptSource.CacheKey(probe.Source, revitVersion, addinVersion, _refFingerprint + extraFp);
            // Issue #9: single-flight. Dos peticiones simultáneas del mismo código compilan una sola vez.
            string compileError = null;
            var got = Cache.GetOrCompile(key, () =>
            {
                var c = CompileNew(code, className, refs, key, out compileError);
                return c == null ? null : Tuple.Create(c, c.CompileMs);
            }, out var fromCache);
            if (got == null) { error = compileError ?? "no compila"; return null; }
            if (!fromCache) { got.ExtraRefs = extraReport.Count > 0 ? extraReport : null; return got; }
            return new Compiled
            {
                Run = got.Run, Key = key, CompileMs = 0, FromCache = true, Diagnostics = got.Diagnostics, Kind = got.Kind, RecipeType = got.RecipeType, Capabilities = got.Capabilities,
                ExtraRefs = extraReport.Count > 0 ? extraReport : null,
            };
        }

        /// <summary>Contadores de la caché de compilación (panel Estado, diagnóstico).</summary>
        public static Dictionary<string, object> CacheStats() => Cache.Stats();

        private static Compiled CompileNew(string code, string className, string[] refs, string key, out string error)
        {
            error = null;
            var wrapped = ScriptSource.Wrap(code, className);
            var args = new object[] { wrapped.Source, refs, "PdiScript_" + key.Substring(0, 12) + "_" + className, null, null, 0L, null };
            var image = (byte[])_compile.Invoke(null, args);
            var diags = (string[])args[3];
            var pdb = (byte[])args[4];
            var ms = (long)args[5];
            var caps = (string[])args[6] ?? new string[0];
            if (image == null)
            {
                error = "no compila:\n" + string.Join("\n", diags ?? new string[0]);
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
                if (recipe == null && !t.IsAbstract && typeof(Receta).IsAssignableFrom(t)) recipe = t;
                var m = t.GetMethod(ScriptSource.EntryMethod, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ScriptContext) }, null);
                if (run == null && m != null && m.ReturnType == typeof(object)) run = m;
            }
            if (run == null && recipe == null)
            {
                error = wrapped.Kind == "receta"
                    ? "compiló, pero no hay una clase pública no abstracta que herede de Receta"
                    : "compiló, pero no hay 'public static object Run(ScriptContext ctx)' en ninguna clase";
                return null;
            }
            return new Compiled
            {
                Run = run, Key = key, CompileMs = ms, FromCache = false, Diagnostics = diags, Kind = wrapped.Kind, RecipeType = recipe, Capabilities = caps,
            };
        }

        /// <summary>Carga Roslyn y compila algo trivial en segundo plano (el primer exec real tarda menos). Nunca lanza.</summary>
        public static void WarmInBackground(Action<string> log)
        {
            if (!Present || _compile != null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var c = Compile("1", "warm", "warm", out var err);
                    log?.Invoke(c != null ? "Compilador del runner listo en " + sw.ElapsedMilliseconds + " ms" : "Compilador del runner: " + err);
                }
                catch (Exception ex) { log?.Invoke("Compilador del runner: " + ex.Message); }
            });
        }
    }
}
