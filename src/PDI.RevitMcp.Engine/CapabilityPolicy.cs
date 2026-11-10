// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine
{
    /// <summary>Perfil de ejecución de código C# (0.6.1).</summary>
    public enum ExecutionProfile
    {
        /// <summary>exec pedido en una sesión con una persona presente: todo permitido pero auditado de forma durable antes de correr.</summary>
        TrustedInteractive,
        /// <summary>Recetas/tareas que corren solas: sólo API de Revit dentro de la transacción del motor y archivos vía ctx.Artefacto.
        /// Guardar/sync/exportar sólo por barreras del motor. Nada de procesos, red, reflexión, código nativo, registro ni salida del proceso.</summary>
        RestrictedAutonomous,
    }

    /// <summary>
    /// Política de capacidades aplicada en el punto de ejecución (antes de Run/Ejecutar), sobre lo que el análisis semántico de Roslyn encontró.
    /// Límite honesto: es una puerta de política, no un aislamiento; .NET no ofrece sandbox en proceso. Un código hostil puede ocultar
    /// llamadas (p. ej. vía tipos no listados); por eso el perfil restringido también rechaza toda reflexión y lo dinámico.
    /// </summary>
    public static class CapabilityPolicy
    {
        public static readonly string[] AlwaysDeniedAutonomous = { "proceso", "red", "reflexion", "nativo", "registro", "salida_proceso", "entorno", "hilos", "revit_cerrar", "revit_abrir", "revit_transaccion" };
        /// <summary>En autónomo se pueden conceder explícitamente (args de la tarea, quedan en su huella).</summary>
        public static readonly string[] GrantableAutonomous = { "archivo_escritura", "revit_guardar", "revit_sync", "revit_exportar" };

        public static string Kind(string capability) { var i = capability.IndexOf(':'); return i < 0 ? capability : capability.Substring(0, i); }

        /// <summary>Lista de capacidades denegadas (vacía = permitido).</summary>
        public static List<string> Denied(ExecutionProfile profile, IEnumerable<string> used, IEnumerable<string> granted = null)
        {
            var res = new List<string>();
            if (profile == ExecutionProfile.TrustedInteractive || used == null) return res;
            var g = new HashSet<string>((granted ?? Enumerable.Empty<string>()).Where(x => GrantableAutonomous.Contains(x)), StringComparer.Ordinal);
            foreach (var u in used)
            {
                var k = Kind(u);
                if (AlwaysDeniedAutonomous.Contains(k)) res.Add(u);
                else if (GrantableAutonomous.Contains(k) && !g.Contains(k)) res.Add(u);
            }
            return res;
        }

        /// <summary>
        /// Frontera de exec (R04). Sin sesión de código confiable activada por una persona en la cinta de Revit:
        /// - ejecutar (escribir) con código arbitrario → rechazado: para escribir sin una persona se usan tareas con recetas y concesiones;
        /// - consultar → perfil restringido (sin procesos, red, reflexión, nativo, archivos, guardar…).
        /// Con sesión confiable: perfil TrustedInteractive (todo, auditado antes de correr).
        /// </summary>
        public static string ExecDecision(bool run, bool trustedSessionActive, IEnumerable<string> usedCapabilities, out ExecutionProfile profile)
        {
            profile = trustedSessionActive ? ExecutionProfile.TrustedInteractive : ExecutionProfile.RestrictedAutonomous;
            if (trustedSessionActive) return null;
            if (run)
                return "codigo_confiable_requerido: ejecutar código C# arbitrario que escribe exige que una persona active «Código confiable» en la cinta PDI de Revit. " +
                       "Sin eso: modo=consulta (sólo lectura), plan/commit, o una tarea con receta y concesión.";
            var denied = Denied(ExecutionProfile.RestrictedAutonomous, usedCapabilities);
            return denied.Count == 0 ? null : Explain(denied);
        }

        public static string Explain(List<string> denied) =>
            "perfil autónomo restringido: el código usa capacidades no permitidas (" + string.Join(", ", denied.Take(8)) + (denied.Count > 8 ? ", …" : "") +
            "). Use ctx.Artefacto para archivos y las barreras Unidad.Guardar/Sync/Pdf; procesos, red, reflexión y código nativo nunca corren en una tarea.";
    }

    /// <summary>
    /// Referencias DLL adicionales (refs=) — R04. Se decide ANTES de cargar nada en el proceso de Revit.
    /// Perfil restringido: sólo DLL de la carpeta del add-in o cuyo SHA-256 esté en referencias_confiables.txt (lo mantiene una persona).
    /// Perfil confiable: cualquiera, pero queda en la auditoría con su SHA-256.
    /// </summary>
    public static class ReferencePolicy
    {
        public static string TrustedListPath => System.IO.Path.Combine(Home.Root, "referencias_confiables.txt");

        public static HashSet<string> LoadTrusted(string path = null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var p = path ?? TrustedListPath;
                if (System.IO.File.Exists(p))
                    foreach (var line in System.IO.File.ReadAllLines(p))
                    {
                        var t = line.Split('#')[0].Trim();
                        if (t.Length == 64) set.Add(t);
                    }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
            return set;
        }

        /// <summary>Lista de (ruta → motivo) rechazadas. sha: función que da el SHA-256 de un archivo (o null si no se puede leer).</summary>
        public static List<string> Denied(ExecutionProfile profile, IEnumerable<string> paths, Func<string, string> sha, ISet<string> trusted, string addinDir)
        {
            var res = new List<string>();
            if (paths == null) return res;
            foreach (var raw in paths)
            {
                var p = raw?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                string full;
                try { full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)); }
                catch (Exception) { res.Add(p + ": ruta inválida"); continue; }
                if (profile == ExecutionProfile.TrustedInteractive) continue;
                var dir = addinDir == null ? null : System.IO.Path.GetFullPath(addinDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (dir != null && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                var h = sha(full);
                if (h == null) { res.Add(p + ": no se puede leer"); continue; }
                if (!trusted.Contains(h)) res.Add(p + ": SHA-256 " + h.Substring(0, 12) + "… no está en referencias_confiables.txt");
            }
            return res;
        }
    }
}
