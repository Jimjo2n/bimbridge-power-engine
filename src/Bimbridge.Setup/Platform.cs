// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace Bimbridge.Setup
{
    /// <summary>What only exists on Windows: processes, the "Apps" registry and the Claude Code CLI.</summary>
    internal static class Platform
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bimbridge";

        public static string ProductVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static Stream OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

        public static bool RevitRunning()
        {
            if (SetupPaths.IsSandbox) return false;
            var instances = Process.GetProcessesByName("Revit");
            try { return instances.Length > 0; }
            finally { foreach (var instance in instances) instance.Dispose(); }
        }

        public const int WaitingForRevit = 6;

        public static bool WaitForRevitExit(CancellationToken cancellation = default)
        {
            if (SetupPaths.IsSandbox) return !cancellation.IsCancellationRequested;
            var elapsed = Stopwatch.StartNew();
            return RevitExitWait.Run(RevitRunning,
                () => !cancellation.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromHours(24),
                Thread.Sleep, TimeSpan.FromSeconds(1));
        }

        public static FileStream LockInstallation(SetupPaths paths)
        {
            Directory.CreateDirectory(paths.Home);
            return new FileStream(Path.Combine(paths.Home, "instalador.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        // ---------------- Claude Code ----------------

        /// <summary>Looks for the Claude Code CLI in the usual places.</summary>
        public static string FindClaude()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(appData, "npm", "claude.cmd"),
            }.ToList();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>claude mcp add --scope user bimbridge -- "bridge.exe" (removing an old version first).</summary>
        public static bool RegisterClaude(string claude, string bridgeExe, out string detail)
        {
            if (SetupPaths.IsSandbox) { detail = "sandbox"; return false; }
            // Keep the user's env block across the re-add (from the old-name entry first, renamed to the new variable names).
            var claudeJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            var env = LegacyMigration.ReadClaudeEnv(claudeJson, LegacyMigration.LegacyMcpName);
            if (env.Count == 0) env = LegacyMigration.ReadClaudeEnv(claudeJson, "bimbridge");
            // shortcut: values containing quotes are skipped (cmd.exe quoting), re-add them by hand if ever needed.
            var flags = string.Concat(env.Where(kv => !kv.Value.Contains("\"")).Select(kv => "-e \"" + kv.Key + "=" + kv.Value + "\" "));
            RunClaude(claude, "mcp remove --scope user " + LegacyMigration.LegacyMcpName, out _);
            RunClaude(claude, "mcp remove --scope user bimbridge", out _);
            // -e takes several values: it goes before --scope so it cannot swallow the server name.
            var ok = RunClaude(claude, "mcp add " + flags + "--scope user bimbridge -- \"" + bridgeExe + "\"", out detail);
            return ok;
        }

        public static void UnregisterClaude()
        {
            if (SetupPaths.IsSandbox) return;
            var claude = FindClaude();
            if (claude == null) return;
            RunClaude(claude, "mcp remove --scope user " + LegacyMigration.LegacyMcpName, out _);
            RunClaude(claude, "mcp remove --scope user bimbridge", out _);
        }

        /// <summary>
        /// Phase 1 of moving an installation made under the former product name (see LegacyMigration). Call it with the installation
        /// lock held, after Revit has closed and before SetupCore.Install or Uninstall.
        /// </summary>
        public static void MigrateLegacyData(SetupPaths p, Action<string> log)
        {
            LegacyMigration.MigrateData(p, log);
            LegacyMigration.MigrateUserEnvironment(log);
        }

        /// <summary>
        /// Phase 2: call it only after SetupCore.Install (or Uninstall) succeeded. Removes the old add-in, bridge and "Apps" entry;
        /// with registerClaude the old Claude Code registration is replaced by the new name, otherwise it is only removed.
        /// </summary>
        public static void RemoveLegacyInstall(SetupPaths p, Action<string> log, bool registerClaude)
        {
            if (!LegacyMigration.RemoveOldInstall(p, log) || SetupPaths.IsSandbox) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(LegacyMigration.LegacyUninstallKey, false); } catch (Exception) { }
            var claude = FindClaude();
            // Only users who had the old registration get the new one.
            if (claude == null || !RunClaude(claude, "mcp get " + LegacyMigration.LegacyMcpName, out _)) return;
            if (registerClaude) { if (RegisterClaude(claude, p.BridgeExe, out _)) log("Claude Code: MCP server renamed to bimbridge"); }
            else RunClaude(claude, "mcp remove --scope user " + LegacyMigration.LegacyMcpName, out _);
        }

        private static bool RunClaude(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : exe,
                    Arguments = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "/c \"\"" + exe + "\" " + args + "\"" : args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true, // one-shot configuration command; not a server
                };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } output = "no response in 30 s"; return false; }
                    output = (o.Result + " " + e.Result).Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        // ---------------- Windows "Apps" ----------------

        public static void RegisterUninstall(SetupPaths p)
        {
            if (SetupPaths.IsSandbox) return;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "BIMBRIDGE - Power Engine");
                k.SetValue("DisplayVersion", ProductVersion);
                k.SetValue("Publisher", "BIMBRIDGE");
                k.SetValue("DisplayIcon", p.SetupCopy + ",0");
                k.SetValue("InstallLocation", p.Home);
                k.SetValue("UninstallString", "\"" + p.SetupCopy + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + p.SetupCopy + "\" /quiet /uninstall");
                k.SetValue("NoModify", 0, RegistryValueKind.DWord);
                k.SetValue("ModifyPath", "\"" + p.SetupCopy + "\"");
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 75000, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        public static void UnregisterUninstall()
        {
            if (SetupPaths.IsSandbox) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        }

        /// <summary>Keeps a copy of the installer for "Modify" and "Uninstall" from Apps.</summary>
        public static void KeepSetupCopy(SetupPaths p)
        {
            var self = Assembly.GetExecutingAssembly().Location;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(p.SetupCopy), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(p.Home);
            CopyOver(self, p.SetupCopy);
        }

        /// <summary>Last non-fatal warning from AfterInstall (e.g. the copy for Apps could not be renewed). Null if there was none.</summary>
        public static string LastWarning;

        public static void CopyOver(string from, string to) => SetupCore.CopyOver(from, to);

        /// <summary>
        /// Common steps after installing (window, /quiet and /actualizar): copy for Apps, entry in Apps,
        /// copy in the updates folder (current + previous) and Recycle Bin for old installers left in Downloads
        /// or on the Desktop. Returns what was sent to the Recycle Bin.
        /// </summary>
        public static List<string> AfterInstall(SetupCore core, SetupPaths p, InstalledInfo before, IEnumerable<string> extraFolders = null)
        {
            var self = Assembly.GetExecutingAssembly().Location;
            var prev = before != null && before.ProductVersion != ProductVersion ? before.ProductVersion : before?.Previous;
            // First time with the updater: the Apps copy is still the previous version; it is kept so we can go back.
            try
            {
                if (prev != null && !File.Exists(p.CachedSetup(prev)) && File.Exists(p.SetupCopy) &&
                    Updates.Format(Updates.ReadVersion(p.SetupCopy)) == prev)
                {
                    Directory.CreateDirectory(p.UpdatesDir);
                    CopyOver(p.SetupCopy, p.CachedSetup(prev));
                }
            }
            catch (Exception) { }
            LastWarning = null;
            // The install already finished: if the copy for Apps fails, we warn and do not mark the install as failed.
            try { KeepSetupCopy(p); }
            catch (Exception e) { LastWarning = "Could not renew the installer copy at " + p.SetupCopy + ": " + e.Message; }
            RegisterUninstall(p);
            var recycled = new List<string>();
            try
            {
                core.CacheSetup(self, ProductVersion, prev);
                var found = Updates.Scan(Updates.Folders(p, extraFolders).Where(f => !string.Equals(f, p.UpdatesDir, StringComparison.OrdinalIgnoreCase)), p.UpdatesTemp);
                foreach (var f in Updates.Superseded(found, Updates.Parse(ProductVersion), p.UpdatesDir))
                    if (Recycle(f)) recycled.Add(f);
            }
            catch (Exception) { }
            return recycled;
        }

        /// <summary>To the Recycle Bin (recoverable), never permanently deleted. Folders included.</summary>
        public static bool Recycle(string path)
        {
            try
            {
                if (File.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else return false;
                // If the folder left by "Extract all" (Bimbridge.Setup_0.2.1\) is now empty, it is recycled too.
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Path.GetFileName(dir).StartsWith(Updates.Prefix, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The .exe cannot delete itself while running: it is deleted a few seconds after it closes.</summary>
        public static void DeleteSetupCopyLater(SetupPaths p)
        {
            try
            {
                if (!File.Exists(p.SetupCopy)) return;
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & del /f /q \"" + p.SetupCopy + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception) { }
        }
    }

    /// <summary>/quiet mode for IT (Intune, SCCM, logon script).</summary>
    internal static class Quiet
    {
        public static int Run(CommandLine c)
        {
            var paths = SetupPaths.Current();
            var core = new SetupCore(paths, Console.WriteLine);
            try
            {
                if (c.Diagnose)
                {
                    // 0.6.0: compact diagnostics (installed / loaded / bridge / hashes / restart pending). Read-only.
                    var text = Bimbridge.Protocol.Json.Write(core.Diagnose());
                    Directory.CreateDirectory(paths.Home);
                    File.WriteAllText(Path.Combine(paths.Home, "diagnostico.json"), text);
                    Console.WriteLine(text);
                    return 0;
                }
                if (c.Restore) return Restore(c, paths);
                if (c.Update) return Update(c, paths, core);
                if (c.ApplyPending) return ApplyPending(paths, core);
                if (Platform.RevitRunning() && c.Uninstall) { Console.Error.WriteLine("Revit is open."); return 2; }
                if (c.Uninstall)
                {
                    Platform.MigrateLegacyData(paths, Console.WriteLine);
                    core.Uninstall(c.RemoveData);
                    Platform.RemoveLegacyInstall(paths, Console.WriteLine, false);
                    Platform.UnregisterClaude();
                    Platform.UnregisterUninstall();
                    Platform.DeleteSetupCopyLater(paths);
                    return 0;
                }
                using var installation = Platform.LockInstallation(paths);
                var before = core.ReadInstalled();
                // 0.6.0: without revit=/niveles= the choice and permissions of the previous install are kept (e.g. RPW).
                var versions = c.Versions ?? (before != null && before.Versions.Count > 0 ? before.Versions.ToArray() : core.DetectRevit().Where(r => r.Installed).Select(r => r.Version).ToArray());
                if (versions.Length == 0) { Console.Error.WriteLine("Revit 2022-2027 not found."); return 1; }
                // Never over an existing install unless asked; it could be the production one.
                var conflicts = core.DetectConflicts(Platform.ProductVersion);
                if (conflicts.Count > 0 && !c.Replace)
                {
                    Console.Error.WriteLine("Nothing was installed: " + string.Join("; ", conflicts) + ". If this is a lab profile and you want to replace it, repeat with /reemplazar (a backup is created).");
                    return 5;
                }
                if (Platform.RevitRunning())
                    Report(paths, Platform.WaitingForRevit, "Install pending. Close all Revit windows to apply it.");
                if (!Platform.WaitForRevitExit()) return Report(paths, 2, "The wait ended with Revit still open.");
                Platform.MigrateLegacyData(paths, Console.WriteLine);
                using (var zip = new System.IO.Compression.ZipArchive(Platform.OpenPayload()))
                    core.Install(zip, new InstallOptions { Versions = versions.ToList(), Levels = c.Levels ?? before?.Levels ?? "R", Codex = c.Codex || (before?.Codex ?? false), AllowInUse = true, RequireManifest = true }, Platform.ProductVersion);
                Platform.RemoveLegacyInstall(paths, Console.WriteLine, true);
                if (c.Claude)
                {
                    var claude = Platform.FindClaude();
                    if (claude != null) Platform.RegisterClaude(claude, paths.BridgeExe, out _);
                }
                Platform.AfterInstall(core, paths, before);
                if (Platform.LastWarning != null) Console.Error.WriteLine("Warning: " + Platform.LastWarning);
                if (core.ReplacedInUse > 0) Console.WriteLine("Revit or an AI conversation was open: the new version takes effect when they restart.");
                return Report(paths, 0, "Installed version " + Platform.ProductVersion + ".");
            }
            catch (Exception ex)
            {
                return Report(paths, 1, ex.Message);
            }
        }

        private static int Restore(CommandLine c, SetupPaths paths)
        {
            if (Platform.RevitRunning() && !SetupPaths.IsSandbox) { Console.Error.WriteLine("Revit is open: close it before restoring."); return 2; }
            var dir = c.RestoreFrom ?? InstallBackup.Latest(paths);
            if (dir == null) { Console.Error.WriteLine("There is no install backup."); return 1; }
            var undo = InstallBackup.Restore(paths, dir, DateTime.UtcNow);
            if (!SetupPaths.IsSandbox && !File.Exists(paths.InstallRecord)) { Platform.UnregisterClaude(); Platform.UnregisterUninstall(); }
            Console.WriteLine("Restored from " + dir);
            Console.WriteLine("To undo: /restaurar=\"" + undo + "\"");
            return 0;
        }

        /// <summary>
        /// /actualizar: launched by the ribbon Update button. Uses the same options as the previous install
        /// (Revit versions, permissions, Codex) and waits for Revit to close when it is open.
        /// Codes: 0 ok, 1 error, 3 no previous install, 4 the same or a newer version is already installed.
        /// The result is left in actualizaciones\ultima.txt for the ribbon to show.
        /// </summary>
        private static int Update(CommandLine c, SetupPaths paths, SetupCore core)
        {
            using var installation = Platform.LockInstallation(paths);
            if (Platform.RevitRunning())
                Report(paths, Platform.WaitingForRevit, "Update pending. Close all Revit windows to apply it. Open Revit after the install finishes.");
            if (!Platform.WaitForRevitExit()) return Report(paths, 2, "The wait ended with Revit still open.");
            Platform.MigrateLegacyData(paths, Console.WriteLine);
            var before = core.ReadInstalled();
            if (before == null || before.Versions.Count == 0) return Report(paths, 3, "There is no previous install: use the normal installer.");
            var mine = Updates.Parse(Platform.ProductVersion);
            var installed = Updates.Parse(before.ProductVersion);
            if (installed != null && mine <= installed && !c.AllowOlder)
                return Report(paths, 4, "Version " + before.ProductVersion + " is already installed.");
            using (var zip = new System.IO.Compression.ZipArchive(Platform.OpenPayload()))
                core.Install(zip, new InstallOptions { Versions = before.Versions, Levels = before.Levels ?? "R", Codex = before.Codex, AllowInUse = true, RequireManifest = true }, Platform.ProductVersion);
            Platform.RemoveLegacyInstall(paths, Console.WriteLine, true);
            var recycled = Platform.AfterInstall(core, paths, before);
            var msg = "Installed version " + Platform.ProductVersion + " (previously " + before.ProductVersion + ")." +
                      (core.ReplacedInUse > 0 ? " " + core.ReplacedInUse + " file(s) were in use: the new version loads when Revit and the AI conversation restart; until then the previous one keeps running." : "") +
                      (recycled.Count > 0 ? " Old installers sent to the Recycle Bin: " + recycled.Count + "." : "") +
                      (Platform.LastWarning != null ? " Warning: " + Platform.LastWarning : "");
            return Report(paths, 0, msg);
        }

        /// <summary>
        /// 0.8.0: applies the update prepared by "Update from ZIP". Waits up to 24 h until no Revit is left open;
        /// it never closes or kills Revit. The prepared installer runs as a separate process with /actualizar and is then verified.
        /// </summary>
        private static int ApplyPending(SetupPaths paths, SetupCore core)
        {
            var host = new ApplyHost
            {
                RevitRunning = Platform.RevitRunning,
                Sleep = t => System.Threading.Thread.Sleep(t),
                RunInstaller = (exe, args) =>
                {
                    using (var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.UpdatesDir }))
                    {
                        if (!pr.WaitForExit(600000)) return 124;
                        return pr.ExitCode;
                    }
                },
                ReadInstalled = core.ReadInstalled,
                Diagnose = core.Diagnose,
                CachedSetup = v => { var f = paths.CachedSetup(v); return File.Exists(f) ? f : null; },
                Log = Console.WriteLine,
            };
            // 0.9.0: current keys and profile (not those in pendiente.json) to re-verify provenance right before installing.
            UpdateConfig.Load(UpdateConfig.DefaultPath, out var keys, out var policy);
            host.Keys = () => keys;
            host.Policy = () => policy;
            var r = PendingApplier.Run(paths.UpdatesDir, host, TimeSpan.FromHours(24));
            var status = Convert.ToString(r["estado"]);
            return Report(paths, status == "aplicada_verificada" ? 0 : 1, "ZIP: " + status + " — " + r["mensaje"]);
        }

        private static int Report(SetupPaths p, int code, string text)
        {
            try
            {
                Directory.CreateDirectory(p.UpdatesDir);
                File.WriteAllText(Path.Combine(p.UpdatesDir, "ultima.txt"), code + "\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n" + text);
            }
            catch (Exception) { }
            (code == 0 ? Console.Out : Console.Error).WriteLine(text);
            return code;
        }
    }
}
