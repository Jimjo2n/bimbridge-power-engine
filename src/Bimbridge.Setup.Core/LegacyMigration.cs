// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bimbridge.Setup
{
    /// <summary>
    /// Moves an installation made under the product's former name to the BIMBRIDGE layout, in two phases:
    /// MigrateData (before SetupCore.Install: data, configuration, Codex, grant key, user environment, saved recipes) and
    /// RemoveOldInstall (only after SetupCore.Install succeeded: old add-in, old bridge). If the install fails in between,
    /// the old add-in still works. Idempotent: every step checks what is still there, so a run interrupted halfway (or blocked
    /// by a file in use) finishes on the next install. Nothing running is stopped or killed.
    /// </summary>
    public static class LegacyMigration
    {
        // The only place the former names may appear: they are needed to find, move and unregister old installations.
        public const string LegacyName = "PDI";
        public const string LegacyFolder = "PDI.RevitMcp";
        public const string LegacyMcpName = "pdi-revit";
        public const string LegacyCodexSection = "[mcp_servers.pdi-revit]";
        public const string LegacyEnvPrefix = "PDI_REVITMCP_";
        public const string LegacyNativeGate = "PDI_REVIT_NATIVO";
        public const string LegacyUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDI.RevitMcp";
        public const string LegacyKeyHeader = "PDI-GRANT-KEY v1 base64";
        public const string LegacySetupPrefix = "PDI.RevitMcp.Setup";
        public const string LegacyBridgeExe = "PDI.RevitMcp.Bridge.exe";
        /// <summary>Namespace prefix in saved scripts and recipes; it also covers the old fully qualified recipe base type.</summary>
        public const string LegacyNamespacePrefix = LegacyFolder + ".";
        public const string NewNamespacePrefix = "Bimbridge.";

        public const string NewEnvPrefix = "BIMBRIDGE_";
        public const string NewNativeGate = "BIMBRIDGE_REVIT_NATIVE";
        /// <summary>Must equal Bimbridge.Engine.GrantStore.KeyHeader (Setup.Core does not reference the engine; a test checks it).</summary>
        public const string NewKeyHeader = "BIMBRIDGE-GRANT-KEY v1 base64";

        /// <summary>Old variable suffix to new suffix (the new names are English).</summary>
        public static readonly Dictionary<string, string> EnvSuffixMap = new Dictionary<string, string>
        {
            ["HOME"] = "HOME", ["CONFIG"] = "CONFIG", ["PIPE"] = "PIPE", ["PERFIL"] = "PROFILE", ["AGENTE"] = "AGENT",
            ["CONVERSACION"] = "CONVERSATION", ["MODELO"] = "MODEL", ["CLAVE_ACTUALIZACION"] = "UPDATE_KEY",
        };

        /// <summary>Matches the former name in text (used by the test that keeps it out of the rest of the repo).</summary>
        public static readonly Regex NamePattern = new Regex(LegacyName + "|Pdi[A-Z_]|(^|[^A-Za-z])pdi", RegexOptions.Multiline);

        public static string LegacyHome(SetupPaths p) => Path.Combine(p.LocalAppData, LegacyFolder);
        public static string LegacyConfigDir(SetupPaths p) => Path.Combine(p.AppData, LegacyFolder);
        public static string LegacyAddinManifest(SetupPaths p, string version) => Path.Combine(p.AddinsDir(version), LegacyFolder + ".addin");
        public static string LegacyAddinFolder(SetupPaths p, string version) => Path.Combine(p.AddinsDir(version), LegacyFolder);
        public static string LegacyBinDir(SetupPaths p) => Path.Combine(LegacyHome(p), "bin");
        public static string LegacyBridge(SetupPaths p) => Path.Combine(LegacyBinDir(p), LegacyBridgeExe);

        /// <summary>Install record of an old installation that has not been migrated yet (null if there is none).</summary>
        public static InstalledInfo ReadInstalled(SetupPaths p) => SetupCore.ReadRecord(Path.Combine(LegacyHome(p), "install.json"));

        /// <summary>A backup made before the rename: its unit keys point at the former add-in folder, so applying it would restore nothing.</summary>
        public static bool IsLegacyBackup(Dictionary<string, object> manifest) =>
            manifest.TryGetValue("archivos", out var f) && f is Dictionary<string, object> files &&
            files.Keys.Any(k => k.Contains("/" + LegacyFolder));

        /// <summary>
        /// Phase 1, before SetupCore.Install: moves data and configuration, renames the Codex section, the grant key header and
        /// and updates saved scripts and recipes. Leaves the old add-in and bridge working. User environment variables are
        /// MigrateUserEnvironment, called next to it by the installer (kept apart so tests never touch the real HKCU).
        /// Returns true if anything was moved or rewritten.
        /// </summary>
        public static bool MigrateData(SetupPaths p, Action<string> log)
        {
            log = log ?? (_ => { });
            var found = false;
            if (Directory.Exists(LegacyHome(p)))
            {
                var moved = 0;
                // bin\ stays until RemoveOldInstall: the old add-in and bridge keep working if the install fails.
                var clean = Merge(LegacyHome(p), p.Home, DropFromHome(LegacyHome(p)), d => d == LegacyBinDir(p), log, ref moved);
                if (moved > 0)
                {
                    found = true;
                    log(clean ? "Data moved from " + LegacyHome(p) + " to " + p.Home
                              : "Data moved to " + p.Home + "; some files stayed in " + LegacyHome(p) + " (in use or already present); the next install retries");
                }
            }
            if (Directory.Exists(LegacyConfigDir(p)))
            {
                var moved = 0;
                Merge(LegacyConfigDir(p), Path.GetDirectoryName(p.ConfigFile), _ => false, _ => false, log, ref moved);
                if (moved > 0) { found = true; log("Configuration moved to " + Path.GetDirectoryName(p.ConfigFile)); }
            }
            found |= RewritePaths(p, p.ConfigFile);
            found |= RewritePaths(p, p.InstallRecord);
            found |= RewriteGrantKey(Path.Combine(p.Home, "autorizaciones", "clave.bin"), log);
            found |= MigrateCodex(p, log);
            found |= RewriteScripts(p, log);
            return found;
        }

        /// <summary>
        /// Phase 2, only after SetupCore.Install succeeded: removes the old add-in manifests and folders and the old bin\.
        /// Returns true if anything was removed (the caller then removes the old uninstall entry and renames the Claude Code registration).
        /// </summary>
        public static bool RemoveOldInstall(SetupPaths p, Action<string> log)
        {
            log = log ?? (_ => { });
            var found = RemoveAddins(p, log);
            if (Directory.Exists(LegacyBinDir(p)))
            {
                if (TryDelete(LegacyBinDir(p))) { found = true; log("Old bridge removed"); }
                else log("The old bridge is still in use. Restart open Claude/Codex conversations to use the new bridge.");
            }
            if (Directory.Exists(LegacyHome(p)) && !Directory.EnumerateFileSystemEntries(LegacyHome(p)).Any())
                found |= TryDelete(LegacyHome(p));
            return found;
        }

        // ---------------- Revit add-in ----------------

        private static bool RemoveAddins(SetupPaths p, Action<string> log)
        {
            var found = false;
            foreach (var v in SetupCore.SupportedVersions)
            {
                var manifest = LegacyAddinManifest(p, v);
                var folder = LegacyAddinFolder(p, v);
                // Both manifests carry the same AddInId: Revit would reject one of them.
                if (File.Exists(manifest))
                {
                    found = true;
                    try { File.SetAttributes(manifest, FileAttributes.Normal); File.Delete(manifest); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { throw new IOException("Could not remove the old add-in for Revit " + v + " (" + manifest + "). Close Revit and try again.", ex); }
                    log("Old add-in removed from Revit " + v);
                }
                if (Directory.Exists(p.AddinsDir(v)))
                    foreach (var d in Directory.GetDirectories(p.AddinsDir(v), LegacyFolder + "*")
                                 .Where(d => Path.GetFileName(d) == LegacyFolder || Path.GetFileName(d).StartsWith(LegacyFolder + Updates.LeftoverMark, StringComparison.Ordinal)))
                    {
                        if (TryDelete(d)) { found = true; continue; }
                        if (d != folder) continue;
                        try { Directory.Move(d, d + Updates.LeftoverMark + DateTime.UtcNow.Ticks); found = true; }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                if (!SetupPaths.IsSandbox)
                {
                    var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", v, LegacyFolder + ".addin");
                    if (File.Exists(machine)) log("Warning: " + machine + " was deployed for all users; IT must remove it (it needs administrator rights)");
                }
            }
            return found;
        }

        // ---------------- folders ----------------

        private static Func<string, bool> DropFromHome(string root) => path =>
        {
            var rel = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(path);
            // instances\ holds stale pid descriptors, old installers cannot be run against the new layout.
            return rel == "instances" || rel == "instalador.lock" || name.Contains(Updates.LeftoverMark)
                || (name.StartsWith(LegacySetupPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        };

        /// <summary>
        /// Moves src into dst without overwriting: whole folders when dst lacks them, otherwise file by file. Entries matching drop are
        /// deleted, entries matching keep stay in src. Deletes src only when it ends up empty. Counts moved entries in moved.
        /// </summary>
        private static bool Merge(string src, string dst, Func<string, bool> drop, Func<string, bool> keep, Action<string> log, ref int moved)
        {
            Directory.CreateDirectory(dst);
            var clean = true;
            var kept = false;
            foreach (var d in Directory.GetDirectories(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(d));
                if (keep(d)) { kept = true; continue; }
                if (drop(d)) { clean &= TryDelete(d); continue; }
                if (!Directory.Exists(target) && !File.Exists(target) && !Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Any(drop))
                {
                    try { Directory.Move(d, target); moved++; continue; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                clean &= Merge(d, target, drop, keep, log, ref moved);
            }
            foreach (var f in Directory.GetFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(f));
                if (keep(f)) { kept = true; continue; }
                if (drop(f)) { clean &= TryDelete(f); continue; }
                if (File.Exists(target) || Directory.Exists(target)) { log("Kept the old copy in place; " + target + " already exists and was not overwritten: " + f); clean = false; continue; }
                try { File.Move(f, target); moved++; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { clean = false; }
            }
            if (clean && !kept) clean = TryDelete(src);
            return clean;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        /// <summary>config.json and install.json may hold absolute paths under the old data folder.</summary>
        private static bool RewritePaths(SetupPaths p, string file)
        {
            if (!File.Exists(file)) return false;
            var text = File.ReadAllText(file);
            var updated = text.Replace(LegacyHome(p).Replace("\\", "\\\\"), p.Home.Replace("\\", "\\\\")).Replace(LegacyHome(p), p.Home);
            if (updated == text) return false;
            File.WriteAllText(file, updated);
            return true;
        }

        /// <summary>
        /// Scripting API renamed to English (regex on the old name, replacement). Whole words only; enum members, Bim helpers and
        /// members whose bare name is a common word are matched only in their qualified, override or base-call form.
        /// </summary>
        public static readonly (string Pattern, string Replacement)[] ScriptApiRenames = BuildScriptApiRenames();

        private static (string, string)[] BuildScriptApiRenames()
        {
            var list = new List<(string, string)>
            {
                (@"\bOmitirUnidad\b", "SkipUnit"),
                (@"\bPoliticaTx\.PorUnidad\b", "TxPolicy.PerUnit"), (@"\bPoliticaTx\.Ninguna\b", "TxPolicy.None"), (@"\bPoliticaTx\.Lectura\b", "TxPolicy.ReadOnly"),
                (@"\bPoliticaTx\b", "TxPolicy"),
                (@"\bVerificacion\.Aplicada\b", "Verification.Applied"), (@"\bVerificacion\.NoAplicada\b", "Verification.NotApplied"),
                (@"\bVerificacion\.Desconocida\b", "Verification.Unknown"), (@"\bVerificacion\b", "Verification"),
                (@"\bUnidad\.Guardar\b", "Unit.Save"), (@"\bUnidad\b", "Unit"),
                (@"\bReceta\b", "Recipe"),
                (@"\.Clave\b", ".Key"), (@"\.Datos\b", ".Data"), (@"\.BarreraArgs\b", ".BarrierArgs"), (@"\.Barrera\b", ".BarrierAction"),
                (@"\.Excepcion\(", ".Exception("), (@"\.Omitir\(", ".Skip("), (@"\.Artefacto\(", ".Artifact("), (@"\.TareaId\b", ".TaskId"),
            };
            var members = new Dictionary<string, string>
            {
                ["Descripcion"] = "Description", ["Transaccion"] = "Transaction", ["Entradas"] = "Inputs", ["Preparar"] = "Prepare",
                ["Unidades"] = "Units", ["Ejecutar"] = "Run", ["Verificar"] = "Verify", ["Postcondicion"] = "Postcondition",
            };
            foreach (var m in members)
            {
                list.Add((@"(\boverride\s+[\w<>\[\]?,. ]+?\s)" + m.Key + @"\b", "${1}" + m.Value));
                list.Add((@"\bbase\." + m.Key + @"\b", "base." + m.Value));
            }
            var helpers = new Dictionary<string, (string NewClass, Dictionary<string, string> Members)>
            {
                ["Grupos"] = ("Groups", new Dictionary<string, string> { ["Inspeccionar"] = "Inspect", ["Firma"] = "Signature", ["Instancias"] = "Instances", ["Componer"] = "Compose", ["CambiarTipo"] = "ChangeType", ["VerificarComposicion"] = "VerifyComposition" }),
                ["Montaje"] = ("Mounting", new Dictionary<string, string> { ["Cuerpo"] = "Body", ["Vista3D"] = "View3D", ["Soporte"] = "Support", ["Transformacion"] = "Transformation" }),
                ["Vistas"] = ("Views", new Dictionary<string, string> { ["Rango"] = "Range", ["AplicarRango"] = "ApplyRange", ["CompararPlantilla"] = "CompareTemplate", ["PorQueNoSeVe"] = "WhyNotVisible", ["FiltroPorCategoria"] = "FilterByCategory" }),
                ["Manifiesto"] = ("Manifest", new Dictionary<string, string> { ["Juntar"] = "Collect", ["Qc"] = "Qc" }),
            };
            foreach (var h in helpers)
                foreach (var m in h.Value.Members)
                    list.Add((@"\b" + h.Key + @"\." + m.Key + @"\b", h.Value.NewClass + "." + m.Value));
            return list.ToArray();
        }

        /// <summary>Old namespaces and old scripting API names replaced by the current ones (see <see cref="ScriptApiRenames"/>).</summary>
        public static string RewriteScriptText(string text)
        {
            var updated = text.Replace(LegacyNamespacePrefix, NewNamespacePrefix);
            foreach (var r in ScriptApiRenames) updated = Regex.Replace(updated, r.Pattern, r.Replacement);
            return updated;
        }

        /// <summary>
        /// Saved scripts and recipes (recetas\, scripts\) that name the old namespaces or the old (Spanish) scripting API stop compiling.
        /// Paused tasks keep their own copy (tareas\*\receta.cs) bound to a hash and are not touched: finish or cancel them before updating.
        /// </summary>
        public static bool RewriteScripts(SetupPaths p, Action<string> log)
        {
            var found = false;
            foreach (var dir in new[] { "recetas", "scripts" }.Select(d => Path.Combine(p.Home, d)).Where(Directory.Exists))
                foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    try
                    {
                        var text = File.ReadAllText(f);
                        var updated = RewriteScriptText(text);
                        if (updated == text) continue;
                        File.WriteAllText(f, updated, new UTF8Encoding(false));
                        found = true;
                        log("Updated names in " + f);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log("Could not update " + f + ": " + ex.Message); }
                }
            return found;
        }

        /// <summary>Existing grants stop parsing unless the key file's header carries the new name. Atomic: temp file plus replace.</summary>
        public static bool RewriteGrantKey(string path, Action<string> log)
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (!text.StartsWith(LegacyKeyHeader + "\n", StringComparison.Ordinal) && !text.StartsWith(LegacyKeyHeader + "\r\n", StringComparison.Ordinal)) return false;
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, NewKeyHeader + text.Substring(LegacyKeyHeader.Length), new UTF8Encoding(false));
            File.Replace(tmp, path, null);
            log("Grant key header updated");
            return true;
        }

        // ---------------- Codex ----------------

        /// <summary>Renames the old [mcp_servers.*] section (and its .env subtable) to the new one, keeping the user's keys. Backup plus atomic replace.</summary>
        public static bool MigrateCodex(SetupPaths p, Action<string> log)
        {
            var path = p.CodexConfig;
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (!lines.Any(l => l.Trim() == LegacyCodexSection)) return false;
            string updated;
            if (lines.Any(l => l.Trim() == SetupCore.CodexSection)) updated = SetupCore.RemoveTomlSection(text, LegacyCodexSection);
            else
            {
                var oldName = LegacyCodexSection.Trim('[', ']');
                var newName = SetupCore.CodexSection.Trim('[', ']');
                var inside = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal))
                    {
                        var h = t.Trim('[', ']').Trim();
                        inside = h == oldName || h.StartsWith(oldName + ".", StringComparison.Ordinal);
                        if (inside) lines[i] = lines[i].Replace(oldName, newName);
                    }
                    else if (inside)
                        lines[i] = RenameEnv(lines[i]).Replace(LegacyBridge(p).Replace("\\", "\\\\"), p.BridgeExe.Replace("\\", "\\\\")).Replace(LegacyBridge(p), p.BridgeExe);
                }
                updated = string.Join("\n", lines);
            }
            File.Copy(path, path + ".bimbridge-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak", true);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, updated);
            File.Replace(tmp, path, null);
            log("Codex: MCP server renamed to " + SetupCore.CodexSection);
            return true;
        }

        private static readonly Regex LegacyEnvName = new Regex(Regex.Escape(LegacyEnvPrefix) + "([A-Z_]+)");

        /// <summary>Replaces every old variable name in s with its new (English) name.</summary>
        public static string RenameEnv(string s) =>
            LegacyEnvName.Replace(s.Replace(LegacyNativeGate, NewNativeGate),
                m => NewEnvPrefix + (EnvSuffixMap.TryGetValue(m.Groups[1].Value, out var n) ? n : m.Groups[1].Value));

        // ---------------- Claude Code ----------------

        /// <summary>
        /// The env block of a user-scope Claude Code MCP entry in ~/.claude.json (read-only), with old variable names renamed.
        /// Empty when the file, the entry or its env is missing or unreadable.
        /// </summary>
        public static Dictionary<string, string> ReadClaudeEnv(string claudeJson, string server)
        {
            var env = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(claudeJson)) return env;
                var root = Bimbridge.Protocol.Json.ParseObject(File.ReadAllText(claudeJson));
                if (root.TryGetValue("mcpServers", out var s) && s is Dictionary<string, object> servers &&
                    servers.TryGetValue(server, out var e) && e is Dictionary<string, object> entry &&
                    entry.TryGetValue("env", out var v) && v is Dictionary<string, object> vars)
                    foreach (var kv in vars) env[RenameEnv(kv.Key)] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception) { } // a malformed ~/.claude.json only means no env is carried over
            return env;
        }

        // ---------------- user environment ----------------

        /// <summary>HKCU\Environment: copies each old variable to its new name when that one is unset, then removes the old one (.NET broadcasts WM_SETTINGCHANGE).</summary>
        public static bool MigrateUserEnvironment(Action<string> log)
        {
            if (SetupPaths.IsSandbox) return false;
            log = log ?? (_ => { });
            var found = false;
            foreach (var old in EnvSuffixMap.Keys.Select(s => LegacyEnvPrefix + s).Concat(new[] { LegacyNativeGate }))
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(old, EnvironmentVariableTarget.User);
                    if (value == null) continue;
                    found = true;
                    var name = RenameEnv(old);
                    if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) == null)
                        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable(old, null, EnvironmentVariableTarget.User);
                    log("Environment variable " + old + " renamed to " + name);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is PlatformNotSupportedException) { }
            }
            return found;
        }
    }
}
