// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Setup;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// "Update" button: looks for new installers (Downloads, Desktop, the updates folder and any folders IT configures)
    /// every 10 minutes on a background thread, without touching Revit. Pressing Install runs the installer in /actualizar mode,
    /// which waits for Revit to close before replacing the files.
    /// 0.9.1: also, when a person presses "Check GitHub", it queries the repository's latest public Release and downloads its
    /// installer, verifying SHA-256, size and version. It never connects on its own or installs without the user pressing the button.
    /// </summary>
    internal static class UpdateService
    {
        public static readonly SetupPaths Paths = SetupPaths.Current();
        public static Version Running = new Version(0, 0, 0);
        public static volatile UpdateCandidate Available;
        public static volatile string InstalledVersion;
        public static volatile string PreviousVersion;
        public static PushButton Button;

        private static Timer _timer;
        private static List<string> _extraFolders = new List<string>();
        private static readonly object CheckLock = new object();
        private static string _lastVisual;
        private static int? _lastState;

        public static void Start(EngineConfig config)
        {
            Running = Updates.Normalize(Assembly.GetExecutingAssembly().GetName().Version);
            _extraFolders = config?.UpdateFolders ?? new List<string>();
            OnlineEnabled = (config?.OnlineUpdate ?? "manual") != "no";
            AllowPrerelease = config?.UpdatePrerelease ?? false;
            try { Repo = OnlineUpdate.ValidRepo(config?.UpdateRepo); }
            catch (OnlineUpdateException ex) { Repo = OnlineUpdate.DefaultRepo; Runtime.Log("Online update: " + ex.Message + "; using " + Repo); }
            // What was set aside when updating with Revit open is no longer in use: delete it.
            try
            {
                var n = Updates.DeleteLeftovers(Paths.LeftoverDirs(SetupCore.SupportedVersions));
                if (n > 0) Runtime.Log("Update: " + n + " files from the previous version deleted.");
            }
            catch (Exception) { }
            _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
        }

        // ------------------------------------------------------------------ 0.9.1: online update (public GitHub Releases)

        public static volatile OnlineRelease Online;
        /// <summary>null (not queried), nueva, mismo_version_otro_commit, al_dia, anterior, sin_releases or error.</summary>
        public static volatile string OnlineState;
        public static volatile string OnlineError;
        public static string Repo = OnlineUpdate.DefaultRepo;
        public static bool OnlineEnabled = true, AllowPrerelease;

        /// <summary>Commit this add-in was built from (from its informational version), or null.</summary>
        public static string RunningCommit
        {
            get
            {
                try
                {
                    var info = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
                    return OnlineUpdate.CommitOf(info?.InformationalVersion);
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>Queries the latest published release. Only called when a person presses the button; on a background thread.</summary>
        public static void CheckOnline()
        {
            Online = null; OnlineError = null;
            try
            {
                if (!OnlineEnabled) { OnlineState = "error"; OnlineError = "online update is disabled in the configuration (online_update=no)"; return; }
                var json = OnlineHttp.GetText(OnlineUpdate.ApiUrl(Repo), OnlineUpdate.MaxTextBytes);
                var r = OnlineUpdate.Pick(json, Repo, AllowPrerelease, out var skipped);
                if (r == null) { OnlineState = "no_releases"; OnlineError = skipped.Count > 0 ? "discarded: " + string.Join("; ", skipped.Take(5)) : null; return; }
                Online = r;
                OnlineState = OnlineUpdate.Compare(Running, RunningCommit, r);
                Runtime.Journal?.Log("user", "update_check_online", OnlineState, 0, new Dictionary<string, object> { ["release"] = r.Tag, ["repo"] = Repo });
            }
            catch (Exception ex) when (ex is OnlineUpdateException || ex is IOException)
            {
                OnlineState = "error";
                OnlineError = ex.Message;
                Runtime.Log("Online update: " + OnlineError);
            }
        }

        /// <summary>Downloads and verifies the release installer (published SHA-256, size and version). Returns (candidate, error).</summary>
        public static (UpdateCandidate c, string error) DownloadOnline(OnlineRelease r)
        {
            try
            {
                var c = OnlineUpdate.Download(r, Paths.CachedSetup(r.VersionText), OnlineHttp.GetText, OnlineHttp.GetFile);
                Runtime.Journal?.Log("user", "update_download_online", "ok", 0, new Dictionary<string, object> { ["release"] = r.Tag, ["repo"] = Repo, ["sha256"] = OnlineUpdate.Sha256File(c.Exe) });
                return (c, null);
            }
            catch (Exception ex) when (ex is OnlineUpdateException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Runtime.Journal?.Log("user", "update_download_online", "error", 0, new Dictionary<string, object> { ["release"] = r.Tag, ["detail"] = ex.Message });
                return (null, ex.Message);
            }
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        public static bool RestartPending
        {
            get
            {
                var inst = Updates.Parse(InstalledVersion);
                return inst != null && inst > Running;
            }
        }

        public static List<string> Folders => Updates.Folders(Paths, _extraFolders);

        /// <summary>Copy of the previous version saved by the installer, to roll back.</summary>
        public static string PreviousSetup
        {
            get
            {
                var prev = PreviousVersion;
                if (string.IsNullOrEmpty(prev)) return null;
                var f = Paths.CachedSetup(prev);
                return File.Exists(f) ? f : null;
            }
        }

        /// <summary>Searches again. Fast except the first time it sees a new .zip (it opens it to read the version).</summary>
        public static void Check()
        {
            if (!Monitor.TryEnter(CheckLock)) return;
            try
            {
                var info = new SetupCore(Paths).ReadInstalled();
                InstalledVersion = info?.ProductVersion;
                PreviousVersion = info?.Previous;
                var baseline = Updates.Parse(InstalledVersion);
                if (baseline == null || baseline < Running) baseline = Running;
                var found = Updates.Scan(Folders, Paths.UpdatesTemp);
                Available = Updates.Newest(found, baseline);
            }
            catch (Exception ex)
            {
                Runtime.Log("Update: could not search: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(CheckLock);
                Runtime.MarkDirty();
            }
        }

        /// <summary>
        /// Runs the installer in /actualizar mode and waits. It is first copied to the updates folder
        /// (so it can be used to roll back and does not depend on the file in Downloads). Returns (success, message).
        /// </summary>
        public static (bool ok, string text) Install(UpdateCandidate c, bool allowOlder)
        {
            try
            {
                var pending = PendingInstallation();
                if (pending != null) return (true, pending);
                Directory.CreateDirectory(Paths.UpdatesDir);
                var exe = Paths.CachedSetup(c.VersionText);
                if (!string.Equals(Path.GetFullPath(c.Exe), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                    File.Copy(c.Exe, exe, true);
                var report = Bimbridge.Protocol.LegacyAliases.ResolveExisting(Paths.UpdatesDir, Path.Combine(Paths.UpdatesDir, "last.txt"));
                try { File.Delete(report); } catch (Exception) { }

                var psi = new ProcessStartInfo(exe, "/update" + (allowOlder ? " /permitir-anterior" : ""))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Paths.UpdatesDir,
                };
                using (var p = Process.Start(psi))
                {
                    var deadline = Stopwatch.StartNew();
                    while (!p.WaitForExit(250))
                    {
                        var waiting = PendingInstallation();
                        if (waiting != null) return (true, waiting);
                        if (deadline.Elapsed >= TimeSpan.FromMinutes(3))
                            return (false, "The installer did not finish within 3 minutes. Check " + report + ".");
                    }
                    var text = File.Exists(report) ? File.ReadAllLines(report).Skip(2).FirstOrDefault() : null;
                    Runtime.Journal?.Log("user", "update", p.ExitCode == 0 ? "ok" : "error", 0,
                        new Dictionary<string, object> { ["version"] = c.VersionText, ["code"] = (long)p.ExitCode, ["detail"] = text });
                    Runtime.Log("Update to " + c.VersionText + ": exit code " + p.ExitCode + " " + text);
                    return (p.ExitCode == 0, text ?? "The installer finished with exit code " + p.ExitCode + ".");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                Check();
            }
        }

        public static string PendingInstallation()
        {
            try
            {
                using (File.Open(Path.Combine(Paths.Home, "installer.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                return null;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException)
            {
                try
                {
                    var lines = File.ReadAllLines(Bimbridge.Protocol.LegacyAliases.ResolveExisting(Paths.UpdatesDir, Path.Combine(Paths.UpdatesDir, "last.txt")));
                    return lines.Length >= 3 && lines[0] == "6" ? lines[2] : null;
                }
                catch (IOException) { return null; }
            }
        }

        /// <summary>Called on Idling: changes the button icon and text only if the state changed.</summary>
        public static void RefreshButton()
        {
            if (Button == null) return;
            var a = Available;
            var state = RestartPending ? 2 : a != null ? 1 : 0;
            var visual = state + "|" + (a?.VersionText ?? InstalledVersion ?? "");
            if (visual == _lastVisual) return;
            _lastVisual = visual;
            if (state != _lastState)
            {
                // Only animates when the badge appears or changes (not on first paint nor when removed); with reduced motion, Play sets the final frame.
                RibbonMotion.Play(Button, "update|" + state, _lastState != null && state > 0, (t, s) => Icons.Update(state, t, s), s => Icons.Update(state, s));
                _lastState = state;
            }
            Button.ItemText = state == 2 ? "Restart\nRevit" : state == 1 ? "Update\nto " + a.VersionText : "Version\n" + Updates.Format(Running);
            Button.ToolTip = state == 2
                ? "Version " + InstalledVersion + " is already installed: close and reopen Revit to use it."
                : state == 1
                    ? "A new version is available (" + a.VersionText + "). Press to prepare the installation for when Revit closes."
                    : "Up to date. Press to search again or go back to the previous version.";
        }
    }
}
