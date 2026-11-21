// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Setup
{
    /// <summary>User folders. Tests point them at temporary folders.</summary>
    public sealed class SetupPaths
    {
        public string AppData;        // %APPDATA%
        public string LocalAppData;   // %LOCALAPPDATA%
        public string UserProfile;    // %USERPROFILE%
        public string ProgramFiles;   // C:\Program Files
        public string Desktop;        // Desktop (may be in OneDrive)

        /// <summary>Offline tests: with BIMBRIDGE_SETUP_SANDBOX=folder all paths fall inside it; no Windows registry and no Claude.</summary>
        public static string Sandbox => Environment.GetEnvironmentVariable("BIMBRIDGE_SETUP_SANDBOX");
        public static bool IsSandbox => !string.IsNullOrEmpty(Sandbox);

        public static SetupPaths Current() => IsSandbox ? new SetupPaths
        {
            AppData = Path.Combine(Sandbox, "Roaming"), LocalAppData = Path.Combine(Sandbox, "Local"), UserProfile = Path.Combine(Sandbox, "User"),
            ProgramFiles = Path.Combine(Sandbox, "PF"), Desktop = Path.Combine(Sandbox, "Desktop"),
        } : new SetupPaths
        {
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        public string Home => Path.Combine(LocalAppData, "Bimbridge");
        public string BinDir => Path.Combine(Home, "bin");
        public string BridgeExe => Path.Combine(BinDir, "Bimbridge.Bridge.exe");
        public string SetupCopy => Path.Combine(Home, "Bimbridge.Setup.exe");
        public string InstallRecord => Path.Combine(Home, "install.json");
        public string ConfigFile => Path.Combine(AppData, "Bimbridge", "config.json");
        public string AddinsDir(string version) => Path.Combine(AppData, "Autodesk", "Revit", "Addins", version);
        public string CodexConfig => Path.Combine(UserProfile, ".codex", "config.toml");
        public string Downloads => Path.Combine(UserProfile, "Downloads");
        /// <summary>Copies of the installers (current and previous) for "Go back to the previous version", and the temp folder.</summary>
        public string UpdatesDir => Path.Combine(Home, "updates");
        public string UpdatesTemp => Path.Combine(UpdatesDir, "tmp");
        public string CachedSetup(string version) => Path.Combine(UpdatesDir, "Bimbridge.Setup-" + version + ".exe");
        /// <summary>Folders that may contain files renamed *.viejo-* when something in use was replaced.</summary>
        public IEnumerable<string> LeftoverDirs(IEnumerable<string> versions)
        {
            yield return BinDir;
            foreach (var v in versions) yield return Path.Combine(AddinsDir(v), "Bimbridge");
        }
    }

    public sealed class RevitInstall
    {
        public string Version;
        public bool Installed;   // Revit.exe found
        public string Path;
    }

    public sealed class InstallOptions
    {
        public List<string> Versions = new List<string>();
        /// <summary>R, RP or RPW.</summary>
        public string Levels = "RPW";
        public bool Codex;
        /// <summary>
        /// true (/actualizar mode): if a file is in use by Revit or by an AI conversation, it is renamed to *.viejo-*
        /// and the new one is placed next to it. Windows allows renaming a loaded .dll/.exe but not deleting it. The new version takes effect on restart.
        /// </summary>
        public bool AllowInUse;
        /// <summary>true: without a manifest nothing is installed.</summary>
        public bool RequireManifest;
    }

    public sealed class InstalledInfo
    {
        public string ProductVersion;
        public List<string> Versions = new List<string>();
        public string Levels;
        public DateTime InstalledUtc;
        public bool Codex;
        /// <summary>Version that was installed before the last update (to go back).</summary>
        public string Previous;
    }

    /// <summary>
    /// Installs, updates and uninstalls BIMBRIDGE - Power Engine for the current user, without administrator permissions:
    ///   add-in  → %APPDATA%\Autodesk\Revit\Addins\&lt;version&gt;\Bimbridge.addin + Bimbridge\
    ///   bridge  → %LOCALAPPDATA%\Bimbridge\bin\Bimbridge.Bridge.exe
    ///   config  → %APPDATA%\Bimbridge\config.json (only "levels" changes; the rest is kept)
    ///   Codex   → section [mcp_servers.bimbridge] in ~\.codex\config.toml (optional)
    /// Claude Code and the Windows registry are handled by the .exe (they need processes and the registry).
    /// </summary>
    public sealed class SetupCore
    {
        public static readonly string[] SupportedVersions = { "2022", "2023", "2024", "2025", "2026", "2027" };
        public const string CodexSection = "[mcp_servers.bimbridge]";

        private readonly SetupPaths _p;
        private readonly Action<string> _log;

        public SetupCore(SetupPaths paths, Action<string> log = null)
        {
            _p = paths;
            _log = log ?? (_ => { });
        }

        public SetupPaths Paths => _p;

        // ---------------- detection ----------------

        public List<RevitInstall> DetectRevit()
        {
            var list = new List<RevitInstall>();
            foreach (var v in SupportedVersions)
            {
                var dir = Path.Combine(_p.ProgramFiles ?? "", "Autodesk", "Revit " + v);
                list.Add(new RevitInstall { Version = v, Path = dir, Installed = File.Exists(Path.Combine(dir, "Revit.exe")) });
            }
            return list;
        }

        public bool CodexPresent => Directory.Exists(Path.GetDirectoryName(_p.CodexConfig));

        /// <summary>An installation under the former name still counts as installed until LegacyMigration moves it.</summary>
        public InstalledInfo ReadInstalled() => File.Exists(_p.InstallRecord) ? ReadRecord(_p.InstallRecord) : LegacyMigration.ReadInstalled(_p);

        internal static InstalledInfo ReadRecord(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var o = LegacyAliases.NormalizeRecord(Json.ParseObject(File.ReadAllText(file)));
                var info = new InstalledInfo
                {
                    ProductVersion = Arg.GetString(o, "version"),
                    Levels = Arg.GetString(o, "levels"),
                    Versions = Arg.GetStringList(o, "revit"),
                    Codex = Arg.GetBool(o, "codex") ?? false,
                    Previous = Arg.GetString(o, "previous"),
                };
                DateTime.TryParse(Arg.GetString(o, "installed_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out info.InstalledUtc);
                return info;
            }
            catch (Exception) { return null; }
        }

        // ---------------- install ----------------

        /// <summary>Copies what is needed from the package (zip). Throws IOException with a clear message if a file is in use.</summary>
        /// <summary>Backup created by the last install (null if nothing was installed).</summary>
        public string LastBackup { get; private set; }

        public void Install(ZipArchive payload, InstallOptions o, string productVersion)
        {
            if (o.Versions.Count == 0) throw new ArgumentException("choose at least one Revit version");
            var levels = (o.Levels ?? "R").ToUpperInvariant();
            if (levels != "R" && levels != "RP" && levels != "RPW") throw new ArgumentException("invalid levels: " + levels);

            var previous = ReadInstalled();
            // 0.6.0: the package carries manifest.json with each file's SHA-256; if it does not match, nothing is installed.
            var manifest = ReadPayloadManifest(payload);
            if (manifest == null && o.RequireManifest) throw new InvalidOperationException("the package has no manifest.json: its integrity cannot be verified and nothing was installed");
            if (manifest != null) VerifyPayload(payload, manifest, o.Versions);
            // Native Validation: before touching anything the installed files are backed up, so we can go back (Setup.exe /restaurar).
            LastBackup = previous == null && !Directory.Exists(_p.BinDir) ? null
                : InstallBackup.Create(_p, o.Versions.Concat(previous?.Versions ?? new List<string>()).Distinct(), "before installing " + productVersion, DateTime.UtcNow);
            // Before touching anything: if the bridge is in use and renaming is not allowed, stop here with no changes.
            if (!o.AllowInUse && File.Exists(_p.BridgeExe))
            {
                try { using (new FileStream(_p.BridgeExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException)
                {
                    throw new IOException("Nothing was changed: " + _p.BridgeExe + " is in use by a Claude or Codex conversation. Close it and try again.");
                }
            }
            InstallBackup.Prune(_p);
            try { InstallSteps(payload, o, productVersion, levels, previous, manifest); }
            catch (Exception ex) when (LastBackup != null && (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is InvalidDataException))
            {
                // Atomic from the outside: a failure halfway goes back to the previous backup instead of leaving mixed files.
                try { InstallBackup.Restore(_p, LastBackup, DateTime.UtcNow); }
                catch (Exception undo) { throw new IOException("The install failed (" + ex.Message + ") and could not go back to the backup (" + undo.Message + "). Use /restore=\"" + LastBackup + "\".", ex); }
                throw new IOException("The install failed and the previous state was restored: " + ex.Message, ex);
            }
        }

        private void InstallSteps(ZipArchive payload, InstallOptions o, string productVersion, string levels, InstalledInfo previous, Dictionary<string, string> manifest)
        {
            _installed.Clear();
            Updates.DeleteLeftovers(_p.LeftoverDirs(SupportedVersions));
            _allowInUse = o.AllowInUse;
            _replacedInUse = 0;

            // 1) Add-in per version
            foreach (var v in o.Versions)
            {
                var prefix = "addin/" + v + "/";
                // PowerShell 5 Compress-Archive stores paths with "\": they are normalized.
                var entries = payload.Entries.Where(e => Norm(e).StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
                if (entries.Count == 0) throw new InvalidOperationException("the package does not contain the add-in for Revit " + v);
                var dest = _p.AddinsDir(v);
                foreach (var e in entries)
                {
                    var target = Path.Combine(dest, Norm(e).Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar));
                    Extract(e, target, "Revit " + v);
                    Record(Norm(e), target, manifest);
                }
                _log("Add-in installed for Revit " + v);
            }
            // Versions that were installed and are no longer chosen: removed.
            if (previous != null)
                foreach (var v in previous.Versions.Where(x => !o.Versions.Contains(x)))
                {
                    RemoveAddin(v);
                    _log("Add-in removed from Revit " + v);
                }

            // 2) Bridge
            var bridge = payload.Entries.FirstOrDefault(e => Norm(e) == "bridge/Bimbridge.Bridge.exe") ?? throw new InvalidOperationException("the package does not contain the bridge");
            Extract(bridge, _p.BridgeExe, "the bridge (is a Claude or Codex conversation open?)");
            Record(Norm(bridge), _p.BridgeExe, manifest);
            // 0.9.0: AgentHost and Power Engine next to the bridge.
            var peFiles = 0;
            foreach (var e in payload.Entries)
            {
                var n = Norm(e);
                if (!n.StartsWith("bridge/", StringComparison.Ordinal) || n == "bridge/Bimbridge.Bridge.exe" || n.EndsWith("/", StringComparison.Ordinal)) continue;
                var rel = n.Substring("bridge/".Length);
                if (rel.Split('/').Any(x => x == ".." || x.Length == 0)) throw new InvalidOperationException("invalid path in the package: " + n);
                var target = Path.Combine(_p.BinDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Extract(e, target, "the Power Engine");
                Record(n, target, manifest);
                peFiles++;
            }
            _log("Bridge installed in " + _p.BinDir + (peFiles > 0 ? " (Power Engine: " + peFiles + " files)" : ""));

            // 3) Configuration: only "levels" and "enabled"; the rest is kept.
            WriteConfig(levels);
            _log("AI permissions: " + LevelsText(levels));

            // 4) Codex
            if (o.Codex) { RegisterCodex(); _log("Connected to Codex"); }
            else if (previous != null && previous.Codex) { UnregisterCodex(); }

            // 5) Install record
            var record = new Dictionary<string, object>
            {
                ["version"] = productVersion,
                ["revit"] = o.Versions.Cast<object>().ToList(),
                ["levels"] = levels,
                ["codex"] = o.Codex,
                ["previous"] = previous == null ? null
                    : previous.ProductVersion != productVersion ? previous.ProductVersion : previous.Previous,
                ["installed_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                // 0.6.0: hash of each installed file (relative to its root folder) for later diagnostics.
                ["files"] = _installed.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["package_manifest"] = manifest != null,
                ["replaced_in_use"] = _replacedInUse,
            };
            Directory.CreateDirectory(_p.Home);
            File.WriteAllText(_p.InstallRecord, Json.Write(record));
        }


        // ---------------- hash verification and diagnostics (0.6.0) ----------------

        public const string PayloadManifest = "manifest.json";
        private readonly Dictionary<string, string> _installed = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Sha256(Stream s)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Copies overwriting even if the old target is marked read-only (the cause of the alpha3 "Access denied").</summary>
        public static void CopyOver(string from, string to)
        {
            if (File.Exists(to))
            {
                var at = File.GetAttributes(to);
                if ((at & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, at & ~FileAttributes.ReadOnly);
            }
            File.Copy(from, to, true);
        }

        public static string FileSha(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return Sha256(f);
        }

        public static Dictionary<string, string> ReadPayloadManifest(ZipArchive payload)
        {
            var m = payload.Entries.FirstOrDefault(e => Norm(e) == PayloadManifest);
            if (m == null) return null;
            using (var r = new StreamReader(m.Open(), Encoding.UTF8))
            {
                var o = Json.ParseObject(r.ReadToEnd());
                if (!(o.TryGetValue("files", out var a) && a is Dictionary<string, object> files)) throw new InvalidDataException("manifest.json without 'files'");
                if (o.ContainsKey("required"))
                {
                    var required = Arg.GetStringList(o, "required");
                    var present = new HashSet<string>(payload.Entries.Where(e => e.Name.Length > 0 && e.Length > 0).Select(Norm), StringComparer.Ordinal);
                    if (required.Count == 0 || required.Any(n => !present.Contains(n) || !files.ContainsKey(n)))
                        throw new InvalidDataException("required payload components are missing");
                }
                return files.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            }
        }

        /// <summary>Every file to be installed must be in the manifest with a matching SHA-256; otherwise InvalidDataException (nothing is touched).</summary>
        public static void VerifyPayload(ZipArchive payload, Dictionary<string, string> manifest, IEnumerable<string> versions)
        {
            var bad = new List<string>();
            var wanted = versions.Select(v => "addin/" + v + "/").ToList();
            bool Selected(string n) => n.StartsWith("bridge/", StringComparison.Ordinal) || wanted.Any(w => n.StartsWith(w, StringComparison.Ordinal));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in payload.Entries.Where(x => x.Name.Length > 0))
            {
                var n = Norm(e);
                if (!Selected(n)) continue;
                if (!seen.Add(n) || n.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.Contains(":")))
                {
                    bad.Add(n + " (invalid or duplicate path)");
                    continue;
                }
                string sha;
                using (var s = e.Open()) sha = Sha256(s);
                if (!manifest.TryGetValue(n, out var exp)) bad.Add(n + " (not in the manifest)");
                else if (!string.Equals(exp, sha, StringComparison.OrdinalIgnoreCase)) bad.Add(n + " (hash mismatch)");
            }
            foreach (var expected in manifest.Keys.Where(Selected))
                if (!seen.Contains(expected)) bad.Add(expected + " (missing from the payload)");
            if (bad.Count > 0) throw new InvalidDataException("the package does not match its manifest; nothing was installed: " + string.Join("; ", bad.Take(5)) + (bad.Count > 5 ? " …" : ""));
        }

        private void Record(string entry, string target, Dictionary<string, string> manifest)
        {
            var sha = FileSha(target);
            if (manifest != null && manifest.TryGetValue(entry, out var exp) && !string.Equals(exp, sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("after copying, " + entry + " does not match the manifest (disk or antivirus); install again");
            _installed[entry] = sha;
        }

        /// <summary>
        /// Compact diagnostics: installed version, per year whether the files match what was installed, whether a restart is pending
        /// (files set aside as *.viejo-*), version loaded in each open Revit (from its descriptor) and the bridge. Read-only.
        /// </summary>
        public Dictionary<string, object> Diagnose()
        {
            var info = ReadInstalled();
            Dictionary<string, object> files = null;
            try { if (File.Exists(_p.InstallRecord)) files = LegacyAliases.NormalizeRecord(Json.ParseObject(File.ReadAllText(_p.InstallRecord))).TryGetValue("files", out var a) ? a as Dictionary<string, object> : null; }
            catch (Exception) { }
            var res = new Dictionary<string, object>
            {
                ["installed"] = info?.ProductVersion, ["previous"] = info?.Previous, ["revit_selected"] = info?.Versions?.Cast<object>().ToList(),
            };
            var years = new Dictionary<string, object>();
            foreach (var v in SupportedVersions)
            {
                var dir = _p.AddinsDir(v);
                var manifestFile = Path.Combine(dir, "Bimbridge.addin");
                if (!File.Exists(manifestFile)) { years[v] = "not installed"; continue; }
                var diff = new List<object>();
                var n = 0;
                if (files != null)
                    foreach (var kv in files.Where(k => k.Key.StartsWith("addin/" + v + "/", StringComparison.Ordinal)))
                    {
                        n++;
                        var path = Path.Combine(dir, kv.Key.Substring(("addin/" + v + "/").Length).Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(path)) diff.Add(kv.Key + " missing");
                        else if (!string.Equals(FileSha(path), Convert.ToString(kv.Value), StringComparison.OrdinalIgnoreCase)) diff.Add(kv.Key + " changed");
                    }
                var pending = Directory.Exists(Path.Combine(dir, "Bimbridge"))
                    && Directory.GetFiles(Path.Combine(dir, "Bimbridge"), "*" + Updates.LeftoverMark + "*", SearchOption.AllDirectories).Length > 0;
                years[v] = new Dictionary<string, object>
                {
                    ["files"] = n, ["match"] = files == null ? (object)"no hash record (install older than 0.6.0)" : diff.Count == 0,
                    ["differences"] = diff, ["restart_pending"] = pending,
                };
            }
            res["addin"] = years;
            var bridgeOk = File.Exists(_p.BridgeExe);
            object bridgeMatch = null;
            if (bridgeOk && files != null && files.TryGetValue("bridge/Bimbridge.Bridge.exe", out var bs))
                bridgeMatch = string.Equals(FileSha(_p.BridgeExe), Convert.ToString(bs), StringComparison.OrdinalIgnoreCase);
            res["bridge"] = new Dictionary<string, object>
            {
                ["exists"] = bridgeOk, ["matches"] = bridgeMatch,
                ["restart_pending"] = Directory.Exists(_p.BinDir) && Directory.GetFiles(_p.BinDir, "*" + Updates.LeftoverMark + "*").Length > 0,
            };
            var loaded = new List<object>();
            var inst = Path.Combine(_p.Home, "instances");
            if (Directory.Exists(inst))
                foreach (var f in Directory.GetFiles(inst, "*.json"))
                {
                    try
                    {
                        var d = InstanceDescriptor.FromJson(File.ReadAllText(f));
                        bool alive;
                        try { alive = !System.Diagnostics.Process.GetProcessById(d.Pid).HasExited; } catch (Exception) { alive = false; }
                        if (!alive) continue;
                        loaded.Add(new Dictionary<string, object>
                        {
                            ["revit"] = d.RevitVersion, ["pid"] = d.Pid, ["addin_loaded"] = d.AddinVersion, ["from"] = d.StartedUtc,
                            ["matches_installed"] = info?.ProductVersion != null && d.AddinVersion != null && d.AddinVersion.StartsWith(info.ProductVersion, StringComparison.Ordinal),
                        });
                    }
                    catch (Exception) { }
                }
            res["open_revits"] = loaded;
            try
            {
                if (File.Exists(_p.ConfigFile))
                {
                    var c = LegacyAliases.NormalizeConfig(Json.ParseObject(File.ReadAllText(_p.ConfigFile)));
                    res["config"] = new Dictionary<string, object> { ["enabled"] = Arg.GetBool(c, "enabled"), ["levels"] = Arg.GetString(c, "levels"), ["runner"] = Arg.GetString(c, "runner") ?? "auto" };
                }
            }
            catch (Exception ex) { res["config"] = "unreadable: " + ex.Message; }
            return res;
        }

        private static string Norm(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');

        public static string LevelsText(string levels)
        {
            switch (levels)
            {
                case "R": return "read the model only";
                case "RP": return "read and propose changes; every change waits in the queue";
                default: return "read and change, with your approval (Live or Hold)";
            }
        }

        private bool _allowInUse;
        private int _replacedInUse;

        /// <summary>How many files were in use and were replaced by renaming in the last install (they take effect on restart).</summary>
        public int ReplacedInUse => _replacedInUse;

        private void Extract(ZipArchiveEntry e, string dest, string owner)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var tmp = dest + ".nuevo";
            using (var src = e.Open())
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (_allowInUse)
                {
                    try
                    {
                        // In use: set aside under another name (allowed for loaded files) and the new one takes its place.
                        var aside = dest + Updates.LeftoverMark + DateTime.UtcNow.Ticks;
                        File.Move(dest, aside);
                        File.Move(tmp, dest);
                        _replacedInUse++;
                        return;
                    }
                    catch (Exception) { }
                }
                try { File.Delete(tmp); } catch (Exception) { }
                throw new IOException("Could not replace " + Path.GetFileName(dest) + ": it is in use by " + owner + ". Close it and try again.", ex);
            }
        }

        /// <summary>
        /// Saves a copy of the installer in the updates folder and keeps only the current and the previous one
        /// (the previous one is used for "Go back to the previous version"). Returns the deleted copies.
        /// </summary>
        public List<string> CacheSetup(string selfExe, string productVersion, string previousVersion)
        {
            var removed = new List<string>();
            Directory.CreateDirectory(_p.UpdatesDir);
            var target = _p.CachedSetup(productVersion);
            if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                CopyOver(selfExe, target);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
            if (!string.IsNullOrEmpty(previousVersion)) keep.Add(_p.CachedSetup(previousVersion));
            foreach (var f in Directory.GetFiles(_p.UpdatesDir, "Bimbridge.Setup-*.exe"))
                if (!keep.Contains(f) && Updates.TryDelete(f)) removed.Add(f);
            // Temp files of already checked .zip packages
            if (Directory.Exists(_p.UpdatesTemp))
                foreach (var f in Directory.GetFiles(_p.UpdatesTemp)) Updates.TryDelete(f);
            return removed;
        }

        public void WriteConfig(string levels)
        {
            Dictionary<string, object> o = null;
            if (File.Exists(_p.ConfigFile))
            {
                try { o = Json.ParseObject(File.ReadAllText(_p.ConfigFile)); }
                catch (Exception) { File.Copy(_p.ConfigFile, _p.ConfigFile + ".damaged", true); }
            }
            // F19: an update does not turn back on an MCP the user turned off; only a fresh install leaves it on.
            if (o == null) o = new Dictionary<string, object> { ["enabled"] = true };
            else if (!o.ContainsKey("enabled")) o["enabled"] = true;
            o["levels"] = levels;
            Directory.CreateDirectory(Path.GetDirectoryName(_p.ConfigFile));
            File.WriteAllText(_p.ConfigFile, Json.Write(o));
        }

        // ---------------- Codex ----------------

        /// <summary>Adds or updates [mcp_servers.bimbridge] in config.toml without touching the rest of the file.</summary>
        public void RegisterCodex()
        {
            var path = _p.CodexConfig;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            // F19/A31: the section's other keys (args, env, startup_timeout_sec…) and its subtables are kept; only command changes.
            var kept = TomlSectionBody(text, CodexSection, out var subtables);
            var block = CodexSection + "\ncommand = \"" + _p.BridgeExe.Replace("\\", "\\\\") + "\"\n" + kept + subtables;
            var original = File.Exists(path) ? text : null;
            text = RemoveTomlSection(text, CodexSection);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
            if (text.Length > 0) text += "\n";
            var updated = text + block;
            if (original == updated) return;
            // Copy next to the file before changing it; the replace is atomic so it is never left half-written.
            if (original != null) File.Copy(path, path + ".bimbridge-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak", true);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, updated);
            if (original != null) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Returns current with the header section (and its subtables) as it is in source, or without it if source does not have it.</summary>
        public static string ReplaceTomlSection(string current, string source, string header)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var inSource = lines.Any(l => l.Trim() == header);
            var rest = RemoveTomlSection(current, header);
            if (!inSource) return rest;
            var kept = TomlSectionBody(source, header, out var subtables);
            var command = lines.SkipWhile(l => l.Trim() != header).Skip(1).TakeWhile(l => !l.Trim().StartsWith("[", StringComparison.Ordinal))
                .FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^command\s*="));
            if (rest.Length > 0 && !rest.EndsWith("\n", StringComparison.Ordinal)) rest += "\n";
            if (rest.Length > 0) rest += "\n";
            return rest + header + "\n" + (command != null ? command + "\n" : "") + kept + subtables;
        }

        /// <summary>Conflicts with what is already in this profile. Empty = nothing is installed.</summary>
        public List<string> DetectConflicts(string productVersion)
        {
            var list = new List<string>();
            var installed = ReadInstalled();
            if (installed != null) list.Add("there is already an install " + installed.ProductVersion + " for Revit " + string.Join(", ", installed.Versions));
            else if (File.Exists(_p.InstallRecord)) list.Add("the install record exists and cannot be read: " + _p.InstallRecord);
            else if (Directory.Exists(_p.BinDir)) list.Add("there are files in " + _p.BinDir + " without an install record");
            foreach (var v in SupportedVersions)
                if (installed == null && File.Exists(Path.Combine(_p.AddinsDir(v), "Bimbridge.addin"))) list.Add("there is an add-in without a record in Revit " + v);
            return list;
        }

        public void UnregisterCodex()
        {
            var path = _p.CodexConfig;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var cleaned = RemoveTomlSection(text, CodexSection);
            if (cleaned != text) File.WriteAllText(path, cleaned);
        }

        /// <summary>Lines of section [x] except its header and "command =" (text with a trailing \n), and separately its complete subtables [x.something].</summary>
        public static string TomlSectionBody(string text, string header, out string subtables)
        {
            var name = header.Trim('[', ']');
            var body = new StringBuilder();
            var subs = new StringBuilder();
            var where = 0; // 0 outside, 1 in the section, 2 in a subtable
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    var h = t.Trim('[', ']').Trim();
                    where = h == name ? 1 : h.StartsWith(name + ".", StringComparison.Ordinal) ? 2 : 0;
                    if (where == 2) subs.Append('\n').Append(line).Append('\n');
                    continue;
                }
                if (where == 1)
                {
                    if (t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^command\s*=")) continue;
                    body.Append(line).Append('\n');
                }
                else if (where == 2 && t.Length > 0) subs.Append(line).Append('\n');
            }
            subtables = subs.ToString();
            return body.ToString();
        }

        /// <summary>Removes a section [x] and its lines up to the next section (including subtables [x.something]).</summary>
        public static string RemoveTomlSection(string text, string header)
        {
            var name = header.Trim('[', ']');
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            var skipping = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    var h = t.Trim('[', ']').Trim();
                    skipping = h == name || h.StartsWith(name + ".", StringComparison.Ordinal);
                }
                if (!skipping) sb.Append(line).Append('\n');
            }
            var result = sb.ToString();
            while (result.EndsWith("\n\n", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1);
            return result == "\n" ? "" : result;
        }

        // ---------------- uninstall ----------------

        public void Uninstall(bool removeData)
        {
            var info = ReadInstalled();
            foreach (var v in SupportedVersions) RemoveAddin(v);
            _log("Add-in removed from Revit");
            try { if (Directory.Exists(_p.BinDir)) Directory.Delete(_p.BinDir, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new IOException("Could not remove the bridge: it is in use. Close the Claude or Codex conversations and try again.", ex); }
            UnregisterCodex();
            try { File.Delete(_p.InstallRecord); } catch (Exception) { }
            if (removeData)
            {
                // Data never migrated by an install keeps its 0.9.3 name: both spellings are removed.
                string[] AndLegacy(params string[] names) => names.Concat(names.Where(LegacyAliases.HomeNames.ContainsKey).Select(n => LegacyAliases.HomeNames[n])).ToArray();
                foreach (var d in AndLegacy("journal", "queue", "instances", "operations", "results", "scripts").Select(n => Path.Combine(_p.Home, n)))
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { }
                foreach (var f in AndLegacy("addin.log", "hold.on", "paused.on"))
                    try { File.Delete(Path.Combine(_p.Home, f)); } catch (Exception) { }
                try { var cfg = Path.GetDirectoryName(_p.ConfigFile); if (Directory.Exists(cfg)) Directory.Delete(cfg, true); } catch (Exception) { }
                _log("Logs, queue and configuration deleted");
            }
        }

        private void RemoveAddin(string v)
        {
            var dir = _p.AddinsDir(v);
            var manifest = Path.Combine(dir, "Bimbridge.addin");
            var folder = Path.Combine(dir, "Bimbridge");
            try
            {
                if (File.Exists(manifest)) File.Delete(manifest);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("Could not remove the add-in from Revit " + v + ": Revit is open. Close it and try again.", ex);
            }
        }
    }
}
