// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Bimbridge.Setup;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Window of the Update button: version in use, new version found (and where), schedule the installation for when Revit closes,
    /// go back to the previous version. All the heavy lifting is done by the installer in /actualizar mode.
    /// </summary>
    internal sealed class UpdateWindow
    {
        private readonly Window _win;
        private readonly StackPanel _body = new StackPanel();
        // Same look as Theme.Notice, but with a variable color (progress, success, error).
        private readonly Border _banner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 11), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(3, 0, 0, 0), Visibility = Visibility.Collapsed };
        private readonly TextBlock _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        private readonly Button _recheck;
        private TextBlock _subtitle;
        private bool _busy;
        private string _shown;

        public static void Show(IntPtr owner) => new UpdateWindow(owner)._win.ShowDialog();

        private UpdateWindow(IntPtr owner)
        {
            _win = Theme.NewWindow("BIMBRIDGE · Updates", 620, 560);
            try { new WindowInteropHelper(_win) { Owner = owner }; } catch (Exception) { }
            _banner.Child = _bannerText;

            var root = new DockPanel();
            var header = Theme.Header("Updates", "", out _subtitle, out var pill, out _);
            pill.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _recheck = Theme.Button("Search again");
            _recheck.Click += async (s, e) => await Recheck();
            // The primary action (install/download) lives on its card; closing is secondary.
            var close = Theme.Button("Close");
            close.IsCancel = true;
            close.Click += (s, e) => { if (!_busy) _win.Close(); };
            var footer = Theme.Footer(null, new StackPanel { Orientation = Orientation.Horizontal, Children = { _recheck, close } });
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var content = new StackPanel();
            content.Children.Add(_banner);
            content.Children.Add(_body);
            root.Children.Add(Theme.Scroll(content));
            _win.Content = root;
            _win.Closing += (s, e) => { if (_busy) e.Cancel = true; };
            Render();
        }

        private void Render()
        {
            _subtitle.Text = "Version in use: " + Updates.Format(UpdateService.Running);
            _body.Children.Clear();
            var a = UpdateService.Available;
            var pending = UpdateService.PendingInstallation();
            // Only animates in if what is shown changed (version found, pending, online state), not on every redraw.
            var shown = pending + "|" + UpdateService.RestartPending + "|" + a?.VersionText + "|" + UpdateService.OnlineState + "|" + UpdateService.Online?.Tag + "|" + UpdateService.OnlineError;
            if (shown != _shown) { _shown = shown; Theme.Enter(_body); }
            if (pending != null)
            {
                _body.Children.Add(StatusCard(Theme.Amber, "Installation pending", pending));
                return;
            }

            if (UpdateService.RestartPending)
            {
                _body.Children.Add(StatusCard(Theme.Amber, "Revit needs a restart",
                    "Version " + UpdateService.InstalledVersion + " is already installed. Save your work, close Revit and reopen it to use it. " +
                    "Claude or Codex conversations use the new version once you start a new one."));
            }
            else if (a != null)
            {
                var install = Theme.Button("Install when Revit closes (" + a.VersionText + ")", Theme.Kind.Primary);
                install.Margin = new Thickness(0, 14, 0, 0);
                install.HorizontalAlignment = HorizontalAlignment.Left;
                install.Click += async (s, e) => await Install(a, false);
                _body.Children.Add(StatusCard(Theme.Green, "A new version is available: " + a.VersionText,
                    "Found in " + a.Source + "\n\nIt installs when all Revit windows are closed and keeps your options (Revit versions, AI permissions). " +
                    "The change queue and the logs are kept. It takes effect the next time you open Revit.", install));
            }
            else
            {
                _body.Children.Add(StatusCard(Theme.Green, "Up to date",
                    "There are no installers newer than version " + Updates.Format(UpdateService.Running) + "."));
            }

            _body.Children.Add(OnlineCard());

            // How a new version arrives
            var how = new StackPanel();
            how.Children.Add(Theme.Section("How to get a new version",
                "When you receive a new installer (Bimbridge.Setup….zip or .exe), just drop it in one of these folders; there is no need to open or unzip it. " +
                "This button turns green on its own within a few minutes, or when you press Search again. After installing, old installers are sent to the Recycle Bin."));
            var folders = new StackPanel();
            foreach (var f in UpdateService.Folders)
                folders.Children.Add(new TextBlock { Text = "•  " + f, FontFamily = Theme.Mono, FontSize = 12, Foreground = Theme.B(Theme.Ink), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2) });
            how.Children.Add(new Border { Background = Theme.B(Theme.Sunken), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0), Child = folders });
            _body.Children.Add(Theme.Card(how));

            // Roll back
            var prevExe = UpdateService.PreviousSetup;
            if (prevExe != null && !UpdateService.RestartPending)
            {
                var back = new StackPanel();
                back.Children.Add(Theme.Section("Go back to version " + UpdateService.PreviousVersion,
                    "If the current version gives you trouble, you can go back to the previous one. It applies when Revit restarts."));
                var btn = Theme.Button("Go back to " + UpdateService.PreviousVersion, Theme.Kind.Danger);
                btn.Margin = new Thickness(0, 10, 0, 0);
                btn.HorizontalAlignment = HorizontalAlignment.Left;
                var prevVersion = Updates.Parse(UpdateService.PreviousVersion);
                btn.Click += async (s, e) => await Install(new UpdateCandidate { Version = prevVersion, Exe = prevExe, Source = prevExe }, true);
                back.Children.Add(btn);
                _body.Children.Add(Theme.Card(back));
            }
        }

        /// <summary>
        /// 0.9.1: online update. It only connects to GitHub when the person presses "Check GitHub"; downloading and installing
        /// is another click. The installer is verified (published SHA-256, size and version) before the installation is offered.
        /// </summary>
        private UIElement OnlineCard()
        {
            var p = new StackPanel();
            p.Children.Add(Theme.Section("Online update (GitHub)"));
            var state = UpdateService.OnlineState;
            var r = UpdateService.Online;
            string text;
            if (!UpdateService.OnlineEnabled) text = "Disabled in this computer's configuration (online_update=no).";
            else if (state == null) text = "Checks the latest version published at github.com/" + UpdateService.Repo + ". It only connects when you press the button; no GitHub account needed.";
            else if (state == "error") text = "Could not check: " + UpdateService.OnlineError;
            else if (state == "no_releases") text = "The repository does not have any installable published version yet." + (UpdateService.OnlineError != null ? "\n" + UpdateService.OnlineError : "");
            else if (state == "new") text = "Published version: " + r.Tag + (r.Commit != null ? " (commit " + r.Commit.Substring(0, 7) + ")" : "") + (r.Prerelease ? " · prerelease" : "") + ". It is newer than the one you are using.";
            else if (state == "same_version_other_commit") text = "The published version " + r.Tag + " has the same number as yours but a different commit (" + r.Commit.Substring(0, 7) + "; you are using " + (UpdateService.RunningCommit ?? "?") + "). You can install it if you want exactly the published one.";
            else if (state == "previous") text = "The latest published version (" + r.Tag + ") is older than the one you are using. There is nothing to update.";
            else text = "You are using the latest published version (" + r.Tag + (r.Commit != null ? ", commit " + r.Commit.Substring(0, 7) : "") + ").";
            var t = Theme.Label(text, 12.5, false, state == "error" ? Theme.Darken(Theme.Red, 0.8) : Theme.Muted);
            t.Margin = new Thickness(0, 0, 0, 10);
            p.Children.Add(t);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (UpdateService.OnlineEnabled)
            {
                var check = Theme.Button(state == null ? "Check GitHub" : "Check GitHub again");
                check.Margin = new Thickness(0, 0, 8, 0);
                check.Click += async (s, e) => await CheckOnline();
                row.Children.Add(check);
                if (r != null && (state == "new" || state == "same_version_other_commit") && !UpdateService.RestartPending)
                {
                    var get = Theme.Button("Download and install " + r.Tag, Theme.Kind.Primary);
                    get.Margin = new Thickness(0);
                    var sameVersion = state == "same_version_other_commit";
                    get.Click += async (s, e) => await DownloadAndInstall(r, sameVersion);
                    row.Children.Add(get);
                }
            }
            p.Children.Add(row);
            return Theme.Card(p);
        }

        private async Task CheckOnline()
        {
            if (_busy) return;
            SetBusy(true, "Checking GitHub…");
            await Task.Run(() => UpdateService.CheckOnline());
            SetBusy(false, null);
            Render();
        }

        private async Task DownloadAndInstall(OnlineRelease r, bool sameVersion)
        {
            if (_busy) return;
            SetBusy(true, "Downloading " + r.Tag + " (" + (r.SetupSize / (1024 * 1024)) + " MB) and verifying its SHA-256…");
            var d = await Task.Run(() => UpdateService.DownloadOnline(r));
            if (d.c == null)
            {
                SetBusy(false, null);
                Render();
                Banner("Nothing was installed: " + d.error, Theme.Red);
                return;
            }
            SetBusy(true, "Verified. Installing version " + d.c.VersionText + "… (a few seconds)");
            // Same version with another commit: the installer only accepts it with /permitir-anterior.
            var res = await Task.Run(() => UpdateService.Install(d.c, sameVersion));
            SetBusy(false, null);
            Render();
            if (res.ok) Banner(res.text + "\nDownloaded from " + d.c.Source + " and verified.", Theme.Green);
            else Banner("Downloaded and verified, but could not be installed: " + res.text, Theme.Red);
        }

        private static Border StatusCard(Color color, string title, string text, UIElement action = null)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var state = color == Theme.Amber ? 2 : title.StartsWith("A new version", StringComparison.Ordinal) ? 1 : 0;
            // Exactly 32 px: the icon is a 32 px bitmap and scaling it blurs it.
            g.Children.Add(new Image { Source = Icons.Update(state, 32), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            var s = new StackPanel();
            s.Children.Add(Theme.Label(title, 15, true));
            var body = Theme.Label(text, 13, false, Theme.Muted);
            body.Margin = new Thickness(0, 4, 0, 0);
            s.Children.Add(body);
            if (action != null) s.Children.Add(action);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return Theme.Card(g);
        }

        private async Task Recheck()
        {
            if (_busy) return;
            SetBusy(true, "Searching for installers…");
            await Task.Run(() => UpdateService.Check());
            SetBusy(false, null);
            Render();
        }

        private async Task Install(UpdateCandidate c, bool allowOlder)
        {
            if (_busy) return;
            SetBusy(true, (allowOlder ? "Going back to version " : "Installing version ") + c.VersionText + "… (a few seconds)");
            var r = await Task.Run(() => UpdateService.Install(c, allowOlder));
            SetBusy(false, null);
            Render();
            if (r.ok) Banner(r.text, Theme.Green);
            else Banner("Could not install: " + r.text, Theme.Red);
        }

        private void SetBusy(bool on, string text)
        {
            _busy = on;
            _recheck.IsEnabled = !on;
            foreach (var b in Descendants<Button>(_body)) b.IsEnabled = !on;
            _win.Cursor = on ? System.Windows.Input.Cursors.Wait : null;
            if (on) Banner(text, Theme.NavySoft);
            else _banner.Visibility = Visibility.Collapsed;
        }

        private void Banner(string text, Color c)
        {
            _bannerText.Text = text;
            _bannerText.Foreground = Theme.B(Theme.Darken(c, 0.72));
            _banner.Background = Theme.Tint(c, 0x16);
            _banner.BorderBrush = Theme.Tint(c, 0x80);
            var appearing = _banner.Visibility != Visibility.Visible;
            _banner.Visibility = Visibility.Visible;
            if (appearing) Theme.Enter(_banner); // only when appearing, not on every text change
        }

        private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
            // Before it is shown there is no visual tree: walk the logical one.
            if (n == 0)
                foreach (var o in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                {
                    if (o is T t) yield return t;
                    foreach (var d in Descendants<T>(o)) yield return d;
                }
        }
    }
}
