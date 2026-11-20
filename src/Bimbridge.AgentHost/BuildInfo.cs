// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;

namespace Bimbridge.AgentHost
{
    /// <summary>Centralized version metadata (rc1): InformationalVersion = "version+commit" embedded by the reproducible build.</summary>
    public static class BuildInfo
    {
        public static readonly string Informational =
            typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "desconocida";
        public static string Version => Informational.Split('+')[0];
        public static string Commit => Informational.Contains("+") ? Informational.Substring(Informational.IndexOf('+') + 1) : "sin_commit";
    }
}
