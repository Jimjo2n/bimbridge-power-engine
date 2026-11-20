// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bimbridge.Addin
{
    /// <summary>
    /// Revit 2022–2024 (.NET Framework 4.8) compiles each method the first time it is used: the first query or the first
    /// geometry check pays that cost inside Revit's turn. Here the whole engine (meshes, queries, formatting, protocol), which
    /// does not touch the Revit API, is precompiled in the background, at lowest priority and 20 s after Revit opens.
    /// Revit 2025–2026 (.NET 8) does not need it: those DLLs already ship precompiled (ReadyToRun) from the installer.
    /// </summary>
    internal static class Warmup
    {
        public static void Start(Action<string> log)
        {
#if !REVIT2025_OR_GREATER
            var t = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(20000);
                    var sw = Stopwatch.StartNew();
                    var n = Prepare(typeof(Bimbridge.Engine.EngineConfig).Assembly) + Prepare(typeof(Bimbridge.Protocol.Wire).Assembly);
                    log?.Invoke("Background precompilation (.NET Framework): " + n + " methods in " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex) { log?.Invoke("Precompilation skipped: " + ex.Message); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = "BIMBRIDGE precompilation",
            };
            t.Start();
#else
            log?.Invoke("Engine precompiled (ReadyToRun, .NET 8): no background precompilation.");
#endif
        }

        /// <summary>Compiles (without running) the assembly's non-generic methods. Returns how many.</summary>
        internal static int Prepare(Assembly a)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            var n = 0;
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in types)
            {
                if (type.IsInterface || type.ContainsGenericParameters) continue;
                MethodBase[] methods;
                try { methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).ToArray(); }
                catch (Exception) { continue; }
                foreach (var m in methods)
                {
                    try
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        RuntimeHelpers.PrepareMethod(m.MethodHandle);
                        n++;
                    }
                    catch (Exception) { }
                }
            }
            return n;
        }
    }
}
