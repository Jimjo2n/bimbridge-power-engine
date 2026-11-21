// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Engine
{
    /// <summary>C# code execution profile (0.6.1).</summary>
    public enum ExecutionProfile
    {
        /// <summary>exec requested in a session with a person present: everything allowed but durably audited before it runs.</summary>
        TrustedInteractive,
        /// <summary>Recipes/tasks that run unattended: only the Revit API inside the engine transaction, and files via ctx.Artifact.
        /// Save/sync/export only through engine barriers. No processes, network, reflection, native code, registry or process exit.</summary>
        RestrictedAutonomous,
    }

    /// <summary>
    /// Capability policy applied at the point of execution (before Run), over what Roslyn's semantic analysis found.
    /// Honest limit: this is a policy gate, not isolation; .NET offers no in-process sandbox. Hostile code can hide
    /// calls (e.g. via unlisted types); that is why the restricted profile also rejects all reflection and dynamic code.
    /// </summary>
    public static class CapabilityPolicy
    {
        public static readonly string[] AlwaysDeniedAutonomous = { "proceso", "red", "reflexion", "nativo", "registro", "salida_proceso", "entorno", "hilos", "revit_cerrar", "revit_abrir", "revit_transaccion" };
        /// <summary>In autonomous mode these can be granted explicitly (task args; they are part of its fingerprint).</summary>
        public static readonly string[] GrantableAutonomous = { "archivo_escritura", "revit_guardar", "revit_sync", "revit_exportar" };

        public static string Kind(string capability) { var i = capability.IndexOf(':'); return i < 0 ? capability : capability.Substring(0, i); }

        /// <summary>List of denied capabilities (empty = allowed).</summary>
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
        /// exec boundary (R04). Without a trusted-code session enabled by a person on the Revit ribbon:
        /// - run (write) with arbitrary code → rejected: writing without a person uses tasks with recipes and grants;
        /// - query → restricted profile (no processes, network, reflection, native, files, save…).
        /// With a trusted session: TrustedInteractive profile (everything, audited before it runs).
        /// </summary>
        public static string ExecDecision(bool run, bool trustedSessionActive, IEnumerable<string> usedCapabilities, out ExecutionProfile profile)
        {
            profile = trustedSessionActive ? ExecutionProfile.TrustedInteractive : ExecutionProfile.RestrictedAutonomous;
            if (trustedSessionActive) return null;
            if (run)
                return "codigo_confiable_requerido: running arbitrary C# code that writes requires a person to enable \"Trusted code\" on the BIMBRIDGE ribbon in Revit. " +
                       "Without that: modo=consulta (read-only), plan/commit, or a task with a recipe and a grant.";
            var denied = Denied(ExecutionProfile.RestrictedAutonomous, usedCapabilities);
            return denied.Count == 0 ? null : Explain(denied);
        }

        public static string Explain(List<string> denied) =>
            "restricted autonomous profile: the code uses capabilities that are not allowed (" + string.Join(", ", denied.Take(8)) + (denied.Count > 8 ? ", …" : "") +
            "). Use ctx.Artifact for files and the Unit.Save/Sync/Pdf barriers; processes, network, reflection and native code never run in a task.";
    }

    /// <summary>
    /// Additional DLL references (refs=) — R04. Decided BEFORE anything is loaded into the Revit process.
    /// Restricted profile: only DLLs from the add-in folder or whose SHA-256 is in referencias_confiables.txt (maintained by a person).
    /// Trusted profile: any, but it is recorded in the audit with its SHA-256.
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

        /// <summary>List of rejected (path → reason). sha: function that returns a file's SHA-256 (or null if it cannot be read).</summary>
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
                catch (Exception) { res.Add(p + ": invalid path"); continue; }
                if (profile == ExecutionProfile.TrustedInteractive) continue;
                var dir = addinDir == null ? null : System.IO.Path.GetFullPath(addinDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (dir != null && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                var h = sha(full);
                if (h == null) { res.Add(p + ": cannot be read"); continue; }
                if (!trusted.Contains(h)) res.Add(p + ": SHA-256 " + h.Substring(0, 12) + "… is not in referencias_confiables.txt");
            }
            return res;
        }
    }
}
