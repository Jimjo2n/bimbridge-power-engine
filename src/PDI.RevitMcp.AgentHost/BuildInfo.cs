// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Metadatos de versión centralizados (rc1): InformationalVersion = "versión+commit" embebido por el build reproducible.</summary>
    public static class BuildInfo
    {
        public static readonly string Informational =
            typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "desconocida";
        public static string Version => Informational.Split('+')[0];
        public static string Commit => Informational.Contains("+") ? Informational.Substring(Informational.IndexOf('+') + 1) : "sin_commit";
    }
}
