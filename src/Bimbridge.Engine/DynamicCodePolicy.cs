// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Dynamic C# execution (exec), since the consolidated review: ENABLED by default for local agents that can already use
    /// BIMBRIDGE (pipe with the Windows user's ACL + config.json levels). A single user control blocks it:
    /// "Block C# code injection" = config.json "block_csharp_injection": true. Persists across restarts.
    /// Replaces the 4-hour "Trusted code" and the SHA-256 authorization (authorized_code).
    /// It is not a sandbox: the code runs in the Revit process with the user's permissions; its effects outside the document are not rolled back.
    /// </summary>
    public static class DynamicCodePolicy
    {
        public const string ConfigKey = "block_csharp_injection";
        public const string BlockedCode = "csharp_injection_blocked";

        /// <summary>null = may run; text = refusal (nothing was compiled or run).</summary>
        public static string Decide(bool blocked) =>
            blocked
                ? BlockedCode + ": the user enabled \"Block C# code injection\" on the BIMBRIDGE ribbon. Nothing was run. " +
                  "Use typed operations (plan/commit) or ask the user to turn it off."
                : null;

        /// <summary>Reads the persisted value. If the file cannot be read, blocks (fail-closed).</summary>
        public static bool LoadBlocked(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var o = LegacyAliases.NormalizeConfig(Json.ParseObject(File.ReadAllText(path)));
                return Arg.GetBool(o, ConfigKey) ?? false;
            }
            catch (Exception) { return true; }
        }

        /// <summary>Saves the value keeping the rest of config.json. Throws if it cannot: the change would not be persisted.</summary>
        public static void SaveBlocked(string path, bool blocked)
        {
            var o = File.Exists(path) ? Json.ParseObject(File.ReadAllText(path)) : new Dictionary<string, object>();
            o[ConfigKey] = blocked;
            Home.WriteAtomic(path, Json.Write(o));
        }
    }
}
