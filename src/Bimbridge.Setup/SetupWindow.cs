// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bimbridge.Addin.Ui;

namespace Bimbridge.Setup
{
    /// <summary>
    /// The installer's single window: choose Revit versions, what the AI can do and which AIs to connect;
    /// then install and see the result. It is also used to modify and uninstall.
    /// </summary>
    internal sealed class SetupWindow
    {
        public readonly Window Window;
        private readonly SetupPaths _paths = SetupPaths.Current();
        private readonly SetupCore _core;
        private readonly InstalledInfo _installed;
        private readonly bool _uninstallMode;

        private readonly ScrollViewer _scroll;
        private readonly Border _footer;
        private readonly Border _footerLink = new Border { VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _footerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _footerNote = Theme.FooterNote();
        private readonly Border _banner;
        private readonly TextBlock _bannerText;
        private TextBlock _subtitle;

        private readonly List<CheckBox> _versionChecks = new List<CheckBox>();
        private RadioButton _r, _rp, _rpw;
        private CheckBox _claude, _codex, _removeData;
        private readonly List<string> _log = new List<string>();
        private CancellationTokenSource _installationWait;
        private bool _applyingInstallation;

        public SetupWindow(bool uninstallMode)
        {
            _core = new SetupCore(_paths, s => _log.Add(s));
            _installed = _core.ReadInstalled();
            _uninstallMode = uninstallMode;

            Window = Theme.NewWindow(_uninstallMode ? "Uninstall BIMBRIDGE" : "Install BIMBRIDGE", 640, 720);
            Window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Window.ShowInTaskbar = true;
            Window.MinHeight = 560;
            Window.Closing += (_, e) =>
            {
                if (_applyingInstallation) e.Cancel = true;
                else _installationWait?.Cancel();
            };
            try { Window.Icon = Icons.Status(Theme.Red, 32); } catch (Exception) { }

            var root = new DockPanel();
            var header = Theme.Header(
                Theme.ProductEyebrow,
                _uninstallMode ? "Uninstall" : _installed != null ? "Modify installation" : "Install",
                "Claude and Codex working with Revit, with your permission.",
                out _subtitle, out var pill, out var pillText);
            pillText.Text = "v" + Platform.ProductVersion;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Footer: discreet link on the left (e.g. Uninstall), note, and actions on the right.
            var left = new DockPanel();
            DockPanel.SetDock(_footerLink, Dock.Left);
            left.Children.Add(_footerLink);
            left.Children.Add(_footerNote);
            _footer = Theme.Footer(left, _footerButtons);
            DockPanel.SetDock(_footer, Dock.Bottom);
            root.Children.Add(_footer);

            _banner = Theme.Notice("", Theme.ActionHover, out _bannerText);
            _banner.Visibility = Visibility.Collapsed;

            _scroll = Theme.Scroll(null);
            root.Children.Add(_scroll);
            Window.Content = root;

            if (_uninstallMode) ShowUninstallForm();
            else ShowInstallForm();
        }

        // ================= Install / modify =================

        /// <summary>The banner is reused across screens: it must be detached from the previous one before adding it to the new one.</summary>
        private void PutBanner(Panel body, int index = -1)
        {
            (_banner.Parent as Panel)?.Children.Remove(_banner);
            _banner.Visibility = Visibility.Collapsed;
            if (index < 0) body.Children.Add(_banner); else body.Children.Insert(index, _banner);
        }

        private void ShowInstallForm()
        {
            _versionChecks.Clear();
            var body = new StackPanel();
            PutBanner(body);
            if (_installed != null)
                ShowBanner("Version " + _installed.ProductVersion + " is already installed. You can change the options and click Update.", Theme.ActionHover);

            // 1. Revit
            var revit = new StackPanel();
            revit.Children.Add(Theme.Section("Which Revit versions?", "The ones on this computer are checked.", "1"));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 4, -8, 0) };
            foreach (var r in _core.DetectRevit())
            {
                var chosen = _installed != null ? _installed.Versions.Contains(r.Version) : r.Installed;
                var withBimbridge = _installed != null && _installed.Versions.Contains(r.Version);
                var hint = withBimbridge ? (r.Installed ? "With BIMBRIDGE" : "With BIMBRIDGE · Revit not found")
                                         : r.Installed ? "Revit found" : "Not found";
                var cb = Theme.Choice(new CheckBox
                {
                    IsChecked = chosen && r.Installed || (_installed != null && _installed.Versions.Contains(r.Version)),
                    IsEnabled = r.Installed || (_installed != null && _installed.Versions.Contains(r.Version)),
                    Tag = r.Version,
                }, "Revit " + r.Version, hint);
                cb.Margin = new Thickness(0, 8, 8, 0);
                _versionChecks.Add(cb);
                grid.Children.Add(cb);
            }
            revit.Children.Add(grid);
            if (!_core.DetectRevit().Any(r => r.Installed))
            {
                var none = Theme.Notice("Revit 2022–2027 was not found in C:\\Program Files\\Autodesk. Install Revit first.", Theme.Amber, out _);
                none.Margin = new Thickness(0, 12, 0, 0);
                revit.Children.Add(none);
            }
            body.Children.Add(Theme.Card(revit));

            // 2. Permissions
            var perms = new StackPanel();
            perms.Children.Add(Theme.Section("What can the AI do?", "You can change this later by opening this installer again.", "2"));
            var current = _installed?.Levels ?? "RPW";
            _r = Option(perms, "Read the model only", "Query, count and audit. It cannot propose or make changes.", current == "R");
            _rp = Option(perms, "Read and propose changes (everything waits in the queue)", "Changes are never applied on their own: you review and apply them from the Queue button on the ribbon.", current == "RP");
            _rpw = Option(perms, "Read and change with your approval (recommended)", "The AI shows you the before and after and you approve. With the Mode button you choose whether it applies right away or waits in the queue.", current == "RPW");
            body.Children.Add(Theme.Card(perms));

            // 3. AIs
            var ais = new StackPanel();
            ais.Children.Add(Theme.Section("Which AI will you use it with?", "Check the ones you use. You can add another later by opening this installer again.", "3"));
            var claudePath = Platform.FindClaude();
            _claude = AiOption(ais, "Claude Code", claudePath != null ? "Found. It will connect automatically." : "Not found on this computer. You can connect it later by opening this installer again.", claudePath != null, claudePath != null);
            _codex = AiOption(ais, "Codex", _core.CodexPresent ? "Found. It will be added to its configuration." : "Not found. If you install it later, open this installer again.", _core.CodexPresent || (_installed?.Codex ?? false), true);
            body.Children.Add(Theme.Card(ais));

            // Security note
            var note = Theme.Notice(
                "The first time you open Revit, the \"Unsigned Add-In\" warning will appear. Choose \"Always Load\". " +
                "If IT signs the add-in, that warning goes away.", Theme.Slate, out var noteText);
            noteText.FontSize = 12.5;
            body.Children.Add(note);

            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter(
                "Installs for your user only. Does not ask for administrator permissions.",
                _installed != null ? Link("Uninstall", ShowUninstallForm) : null,
                Btn("Cancel", Theme.Kind.Secondary, () => Window.Close()),
                Btn(_installed != null ? "Update" : "Install", Theme.Kind.Primary, () => _ = InstallAsync()));
        }

        private async Task InstallAsync()
        {
            var versions = _versionChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (versions.Count == 0) { ShowBanner("Choose at least one Revit version.", Theme.Amber); return; }
            var revitOpen = Platform.RevitRunning();

            var levels = _r.IsChecked == true ? "R" : _rp.IsChecked == true ? "RP" : "RPW";
            var options = new InstallOptions { Versions = versions, Levels = levels, Codex = _codex.IsChecked == true, AllowInUse = true, RequireManifest = true };
            var before = _core.ReadInstalled();
            var wantClaude = _claude.IsChecked == true;
            using var waiting = new CancellationTokenSource();
            _installationWait = waiting;
            if (revitOpen) ShowWaitingForRevit();
            else ShowBusy("Installing…");
            SetFooter("", null, Btn("Cancel", Theme.Kind.Secondary, () => Window.Close()));

            string error = null;
            string claudeResult = null;
            await Task.Run(() =>
            {
                try
                {
                    using var installation = Platform.LockInstallation(_paths);
                    if (!Platform.WaitForRevitExit(waiting.Token)) throw new InvalidOperationException("The wait ended with Revit still open. You can try the install again.");
                    Window.Dispatcher.Invoke(() =>
                    {
                        waiting.Token.ThrowIfCancellationRequested();
                        _applyingInstallation = true;
                        ShowBusy("Installing…");
                    });
                    Platform.MigrateLegacyData(_paths, _log.Add);
                    using (var zip = new ZipArchive(Platform.OpenPayload()))
                        _core.Install(zip, options, Platform.ProductVersion);
                    Platform.RemoveLegacyInstall(_paths, _log.Add, true);
                    if (wantClaude)
                    {
                        var claude = Platform.FindClaude();
                        if (claude != null && Platform.RegisterClaude(claude, _paths.BridgeExe, out var detail)) _log.Add("Connected to Claude Code");
                        else claudeResult = "Could not connect to Claude Code automatically. In a terminal type:\nclaude mcp add --scope user bimbridge -- \"" + _paths.BridgeExe + "\"";
                    }
                    var recycled = Platform.AfterInstall(_core, _paths, before);
                    if (Platform.LastWarning != null) _log.Add("Warning: " + Platform.LastWarning);
                    if (recycled.Count > 0) _log.Add("Old installers sent to the Recycle Bin: " + recycled.Count);
                    if (_core.ReplacedInUse > 0) _log.Add("Restart the conversations that were using BIMBRIDGE to load the new bridge.");
                }
                catch (Exception ex) { error = ex.Message; }
            });

            _applyingInstallation = false;
            _installationWait = null;
            if (waiting.IsCancellationRequested) return;
            if (error != null)
            {
                ShowInstallForm();
                ShowBanner(error, Theme.Red);
                return;
            }
            ShowDone(versions, levels, claudeResult);
        }

        private void ShowDone(List<string> versions, string levels, string warning)
        {
            var body = new StackPanel();
            body.Children.Add(Hero(Theme.Green, "Done", "BIMBRIDGE is installed."));
            var steps = new StackPanel();
            steps.Children.Add(Theme.Section("What's next"));
            steps.Children.Add(Step("1", OpenRevitText(versions) + ". If it asks about the unsigned add-in, choose \"Always Load\"."));
            steps.Children.Add(Step("2", "You will see the BIMBRIDGE tab. The Status button turns green when an AI connects."));
            steps.Children.Add(Step("3", "In Claude or Codex ask, for example: \"How many lighting fixtures are there per level in the open model?\""));
            body.Children.Add(Theme.Card(steps));

            var done = new StackPanel();
            done.Children.Add(Theme.Section("What was done"));
            foreach (var l in _log) done.Children.Add(Did(l));
            done.Children.Add(Did("AI permissions: " + SetupCore.LevelsText(levels)));
            done.Children.Add(Did("Listed in Settings > Apps as \"BIMBRIDGE\" so you can remove it whenever you want"));
            body.Children.Add(Theme.Card(done));
            if (warning != null) { PutBanner(body, 0); ShowBanner(warning, Theme.Amber); }

            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter("", null, Btn("Close", Theme.Kind.Primary, () => Window.Close()));
        }

        /// <summary>"Open Revit 2024", "Open Revit 2024 or 2025", "Open Revit 2022, 2024 or 2026", "Open any Revit from 2022 to 2026".</summary>
        private static string OpenRevitText(List<string> versions)
        {
            var v = versions.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(); // 4-digit years: text order = numeric order
            if (v.Count == 1) return "Open Revit " + v[0];
            if (v.Count >= 3 && int.TryParse(v[0], out var first) && int.TryParse(v[v.Count - 1], out var last) && v.All(s => int.TryParse(s, out _)) && last - first == v.Count - 1)
                return "Open any Revit from " + first + " to " + last;
            return "Open Revit " + string.Join(", ", v.Take(v.Count - 1)) + " or " + v[v.Count - 1];
        }

        // ================= Uninstall =================

        private void ShowUninstallForm()
        {
            var body = new StackPanel();
            PutBanner(body);
            var s = new StackPanel();
            s.Children.Add(Theme.Section("Remove BIMBRIDGE from this computer?", "Removes the Revit add-in, the connection to Claude and Codex, and the bridge. Your models are not touched."));
            _removeData = Theme.Choice(new CheckBox(), "Also delete my data", "Logs, the queue of pending changes and the configuration.");
            _removeData.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(_removeData);
            body.Children.Add(Theme.Card(s));
            _scroll.Content = body;
            Theme.Enter(body);
            // Enter and Esc go back: the destructive action is only confirmed with a click or Tab + Space on its button.
            var cancel = Btn("Cancel", Theme.Kind.Secondary, () => { if (_uninstallMode) Window.Close(); else ShowInstallForm(); });
            cancel.IsDefault = true;
            cancel.IsCancel = true;
            SetFooter(
                _installed != null ? "Installed version: " + _installed.ProductVersion : "",
                null,
                cancel,
                Btn("Uninstall", Theme.Kind.DangerSolid, () => _ = UninstallAsync()));
        }

        private async Task UninstallAsync()
        {
            if (Platform.RevitRunning()) { ShowBanner("Revit is open. Close it and click Uninstall again.", Theme.Amber); return; }
            var removeData = _removeData.IsChecked == true;
            ShowBusy("Uninstalling…");
            string error = null;
            await Task.Run(() =>
            {
                try
                {
                    Platform.MigrateLegacyData(_paths, _log.Add);
                    _core.Uninstall(removeData);
                    Platform.RemoveLegacyInstall(_paths, _log.Add, false);
                    Platform.UnregisterClaude();
                    Platform.UnregisterUninstall();
                }
                catch (Exception ex) { error = ex.Message; }
            });
            if (error != null) { ShowUninstallForm(); ShowBanner(error, Theme.Red); return; }

            var body = new StackPanel();
            body.Children.Add(Hero(Theme.Slate, "Uninstalled", "BIMBRIDGE was removed from this computer."));
            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter("", null, Btn("Close", Theme.Kind.Primary, () => { Platform.DeleteSetupCopyLater(_paths); Window.Close(); }));
        }

        // ================= pieces =================

        /// <summary>Work in progress: status sentence and progress bar. The footer stays hidden until someone adds buttons.</summary>
        private void ShowBusy(string text)
        {
            var s = BusyPanel(text);
            var bar = Theme.ProgressLine(!Theme.ReducedMotion); // reduced motion: no indeterminate animation; the sentence and the wait cursor remain
            bar.Width = 240;
            bar.Margin = new Thickness(0, 16, 0, 0);
            s.Children.Add(bar);
            ShowBusyPanel(s);
        }

        /// <summary>Waiting for the user to close Revit: there is no progress to measure, so an amber notice instead of a bar.</summary>
        private void ShowWaitingForRevit()
        {
            var s = BusyPanel("Waiting for Revit to close");
            var notice = Theme.Notice("Install pending. Save your work and close all Revit windows. Leave this installer open.", Theme.Amber, out _);
            notice.Margin = new Thickness(0, 16, 0, 0);
            s.Children.Add(notice);
            ShowBusyPanel(s);
        }

        private static StackPanel BusyPanel(string text)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 96, 0, 0) };
            s.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Ink), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            return s;
        }

        private void ShowBusyPanel(StackPanel s)
        {
            _scroll.Content = s;
            Theme.Enter(s);
            SetFooter("", null);
            Mouse.OverrideCursor = Cursors.Wait;
            Window.Dispatcher.BeginInvoke(new Action(() => Mouse.OverrideCursor = null), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private static UIElement Hero(Color c, string title, string text)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 24) };
            var circle = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = Theme.B(c), HorizontalAlignment = HorizontalAlignment.Center };
            circle.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(c == Theme.Green ? "M 0,8 L 7,15 L 20,1" : "M 0,0 L 18,0"),
                Stroke = Theme.B(Theme.White), StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            s.Children.Add(circle);
            s.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Ink), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
            s.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            return s;
        }

        private static UIElement Step(string n, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Theme.StepBadge(n));
            var t = Theme.Label(text, 13.5);
            t.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        /// <summary>"What was done" row: mark on the left and text with a hanging indent (long lines do not wrap under the mark).</summary>
        private static UIElement Did(string text)
        {
            var warning = text.StartsWith("Warning: ", StringComparison.Ordinal);
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var mark = Theme.ResultMark(warning ? Theme.Amber : Theme.Green, !warning);
            mark.Margin = new Thickness(0, 2, 0, 0);
            g.Children.Add(mark);
            var t = Theme.Label(text, 12.5, false, Theme.Muted);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        private static RadioButton Option(Panel parent, string title, string text, bool selected)
        {
            var r = Theme.Choice(new RadioButton { GroupName = "levels", IsChecked = selected }, title, text);
            parent.Children.Add(r);
            return r;
        }

        private static CheckBox AiOption(Panel parent, string title, string text, bool isChecked, bool enabled)
        {
            var c = Theme.Choice(new CheckBox { IsChecked = isChecked, IsEnabled = enabled }, title, text);
            parent.Children.Add(c);
            return c;
        }

        private static Button Btn(string text, Theme.Kind kind, Action click)
        {
            var b = Theme.Button(text, kind);
            b.Click += (s, e) => click();
            if (kind == Theme.Kind.Primary) b.IsDefault = true;
            return b;
        }

        /// <summary>Secondary action styled as a link, aligned with the footer's left margin.</summary>
        private static Button Link(string text, Action click)
        {
            var b = Btn(text, Theme.Kind.Quiet, click);
            b.MinWidth = 0;
            b.Margin = new Thickness(-16, 0, 8, 0);
            return b;
        }

        /// <summary>Footer of each screen. With no note, link or buttons it is hidden (e.g. while uninstalling).</summary>
        private void SetFooter(string note, Button link, params Button[] buttons)
        {
            _footerNote.Text = note ?? "";
            _footerLink.Child = link;
            _footerButtons.Children.Clear();
            foreach (var b in buttons.Where(x => x != null)) _footerButtons.Children.Add(b);
            _footer.Visibility = link == null && _footerButtons.Children.Count == 0 && _footerNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowBanner(string text, Color color)
        {
            _bannerText.Text = text;
            Theme.SetNotice(_banner, _bannerText, color);
            _banner.Visibility = Visibility.Visible;
            _scroll.ScrollToTop();
        }
    }
}
