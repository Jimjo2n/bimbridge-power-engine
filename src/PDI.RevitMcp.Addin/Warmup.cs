// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PDI.RevitMcp.Addin
{
    /// <summary>
    /// Revit 2022–2024 (.NET Framework 4.8) compila cada método la primera vez que se usa: la primera consulta o la primera
    /// revisión geométrica pagan ese costo dentro del turno de Revit. Aquí se precompila en segundo plano, con prioridad mínima
    /// y 20 s después de abrir Revit, todo el motor (mallas, consultas, formato, protocolo), que no toca la API de Revit.
    /// En Revit 2025–2026 (.NET 8) no hace falta: esas DLL ya vienen precompiladas (ReadyToRun) desde el instalador.
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
                    var n = Prepare(typeof(PDI.RevitMcp.Engine.EngineConfig).Assembly) + Prepare(typeof(PDI.RevitMcp.Protocol.Wire).Assembly);
                    log?.Invoke("Precompilación en segundo plano (.NET Framework): " + n + " métodos en " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex) { log?.Invoke("Precompilación omitida: " + ex.Message); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = "BIMBRIDGE precompilación",
            };
            t.Start();
#else
            log?.Invoke("Motor precompilado (ReadyToRun, .NET 8): sin precompilación en segundo plano.");
#endif
        }

        /// <summary>Compila (sin ejecutar) los métodos no genéricos del ensamblado. Devuelve cuántos.</summary>
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
