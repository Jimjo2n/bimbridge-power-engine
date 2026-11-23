// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Setup
{
    /// <summary>
    /// Install backup and rollback (Native Validation): add-ins, bridge and Power Engine, configuration, install record,
    /// installer copy and Codex config.toml. What did not exist is noted and deleted when rolling back.
    /// Rollback verifies every hash before touching anything and first saves the current state, so it can be undone.
    /// It neither closes nor detects Revit: the caller requires it to be closed.
    /// </summary>
    public static class InstallBackup
    {
        public const string ManifestName = "backup.json";
        public const int Keep = 3;

        public const string Schema = "bimbridge.backup/1";

        public static string Root(SetupPaths p) => LegacyAliases.ResolveExisting(p.Home, Path.Combine(p.Home, "backups"));

        // A backup made by 0.9.3 or earlier keeps its legacy manifest and folder names (LegacyMigration renames them).
        private static string ManifestPath(string dir) => LegacyAliases.ResolveExisting(dir, Path.Combine(dir, ManifestName));
        private static string FilesDir(string dir) => LegacyAliases.ResolveExisting(dir, Path.Combine(dir, "files"));

        /// <summary>Backup units: stable name → real file or folder.</summary>
        private static IEnumerable<KeyValuePair<string, string>> Units(SetupPaths p, IEnumerable<string> versions)
        {
            foreach (var v in versions)
            {
                yield return new KeyValuePair<string, string>("addin/" + v + "/Bimbridge.addin", Path.Combine(p.AddinsDir(v), "Bimbridge.addin"));
                yield return new KeyValuePair<string, string>("addin/" + v + "/Bimbridge", Path.Combine(p.AddinsDir(v), "Bimbridge"));
            }
            yield return new KeyValuePair<string, string>("bin", p.BinDir);
            yield return new KeyValuePair<string, string>("config.json", p.ConfigFile);
            yield return new KeyValuePair<string, string>("install.json", p.InstallRecord);
            yield return new KeyValuePair<string, string>("setup.exe", p.SetupCopy);
            yield return new KeyValuePair<string, string>("codex.toml", p.CodexConfig);
        }

        public static string Create(SetupPaths p, IEnumerable<string> versions, string reason, DateTime utcNow)
        {
            var id = utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dir = Path.Combine(Root(p), id);
            var filesDir = Path.Combine(dir, "files");
            Directory.CreateDirectory(filesDir);
            var files = new Dictionary<string, object>();
            var absent = new List<object>();
            var units = new List<object>();
            foreach (var u in Units(p, versions.Distinct().OrderBy(x => x, StringComparer.Ordinal)))
            {
                units.Add(u.Key);
                if (File.Exists(u.Value))
                {
                    var dest = Path.Combine(filesDir, u.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(u.Value, dest);
                    files[u.Key] = SetupCore.FileSha(dest);
                }
                else if (Directory.Exists(u.Value))
                {
                    foreach (var f in Directory.GetFiles(u.Value, "*", SearchOption.AllDirectories))
                    {
                        var key = u.Key + "/" + f.Substring(u.Value.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                        var dest = Path.Combine(filesDir, key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest);
                        files[key] = SetupCore.FileSha(dest);
                    }
                }
                else absent.Add(u.Key);
            }
            var manifest = new Dictionary<string, object>
            {
                ["schema"] = Schema, ["id"] = id, ["reason"] = reason,
                ["created_utc"] = utcNow.ToString("o", CultureInfo.InvariantCulture),
                ["revit"] = versions.Distinct().Cast<object>().ToList(), ["units"] = units, ["missing"] = absent, ["files"] = files,
            };
            File.WriteAllText(Path.Combine(dir, ManifestName), Json.Write(manifest));
            Verify(dir);
            return dir;
        }

        public static string Latest(SetupPaths p)
        {
            var root = Root(p);
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root).Where(d => File.Exists(ManifestPath(d)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>Throws InvalidDataException if any backup file is missing or changed.</summary>
        public static Dictionary<string, object> Verify(string dir)
        {
            var path = ManifestPath(dir);
            if (!File.Exists(path)) throw new InvalidDataException("backup without a manifest: " + dir);
            var m = LegacyAliases.NormalizeRecord(Json.ParseObject(File.ReadAllText(path)));
            if (Arg.GetString(m, "schema") != Schema && Arg.GetString(m, "schema") != LegacyAliases.BackupSchema) throw new InvalidDataException("unknown backup schema");
            if (!(m.TryGetValue("files", out var fo) && fo is Dictionary<string, object> files)) throw new InvalidDataException("backup without a file list");
            foreach (var kv in files)
            {
                if (kv.Key.Split('/').Any(x => x == ".." || x.Length == 0)) throw new InvalidDataException("invalid path in the backup: " + kv.Key);
                var f = Path.Combine(FilesDir(dir), kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) throw new InvalidDataException("missing from the backup: " + kv.Key);
                if (!string.Equals(SetupCore.FileSha(f), kv.Value as string, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("hash mismatch in the backup: " + kv.Key);
            }
            return m;
        }

        /// <summary>
        /// Goes back to the backup state. Returns the folder of the backup taken right before the rollback.
        /// If something fails halfway it tries to undo with that backup and rethrows stating the result.
        /// </summary>
        public static string Restore(SetupPaths p, string dir, DateTime utcNow)
        {
            var m = Verify(dir);
            if (LegacyMigration.IsLegacyBackup(m))
                throw new InvalidDataException("Backup " + Path.GetFileName(dir) + " was made before the product was renamed to BIMBRIDGE and cannot be restored over this installation.");
            var versions = ((List<object>)m["revit"]).Select(x => (string)x).ToList();
            var before = Create(p, versions, "before restoring " + Path.GetFileName(dir), utcNow);
            try { Apply(p, dir, m, versions); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                try { Apply(p, before, Verify(before), versions); }
                catch (Exception undo) when (undo is IOException || undo is UnauthorizedAccessException || undo is InvalidDataException)
                {
                    throw new IOException("The rollback failed (" + ex.Message + ") and could not be undone either (" + undo.Message + "). Backup intact at " + before + ".", ex);
                }
                throw new IOException("The rollback failed and the previous state was kept: " + ex.Message, ex);
            }
            return before;
        }

        private static void Apply(SetupPaths p, string dir, Dictionary<string, object> m, List<string> versions)
        {
            var files = (Dictionary<string, object>)m["files"];
            foreach (var u in Units(p, versions))
            {
                // config.toml belongs to the user: only the BIMBRIDGE section is restored; changes outside it are kept.
                if (u.Key == "codex.toml") { RestoreCodexSection(p, dir, files); continue; }
                if (Directory.Exists(u.Value)) Directory.Delete(u.Value, true);
                else if (File.Exists(u.Value)) { File.SetAttributes(u.Value, FileAttributes.Normal); File.Delete(u.Value); }
                foreach (var kv in files.Where(x => x.Key == u.Key || x.Key.StartsWith(u.Key + "/", StringComparison.Ordinal)))
                {
                    var target = kv.Key == u.Key ? u.Value : Path.Combine(u.Value, kv.Key.Substring(u.Key.Length + 1).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(FilesDir(dir), kv.Key.Replace('/', Path.DirectorySeparatorChar)), target);
                    if (!string.Equals(SetupCore.FileSha(target), kv.Value as string, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("hash mismatch after restoring: " + kv.Key);
                }
            }
        }

        private static void RestoreCodexSection(SetupPaths p, string dir, Dictionary<string, object> files)
        {
            var saved = files.ContainsKey("codex.toml") ? File.ReadAllText(Path.Combine(FilesDir(dir), "codex.toml")) : "";
            var current = File.Exists(p.CodexConfig) ? File.ReadAllText(p.CodexConfig) : "";
            if (current == saved) return;
            var result = SetupCore.ReplaceTomlSection(current, saved, SetupCore.CodexSection);
            if (result == current) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p.CodexConfig));
            File.WriteAllText(p.CodexConfig, result);
        }

        /// <summary>Keeps the latest backups. Cleanup never fails the install.</summary>
        public static void Prune(SetupPaths p)
        {
            var root = Root(p);
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Skip(Keep))
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
