// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Settings: only the switches that already exist and are already saved (Pause and Hold as state files, C# block in
    /// config.json). Each change takes the same path as its ribbon button and shows immediately on the ribbon and in Status.
    /// Plus a UI preference of the add-in itself (Motion), saved separately in ui.json.
    /// Levels are shown, but only the installer changes them (they are read when Revit starts).
    /// </summary>
    internal sealed class SettingsWindow
    {
        private const string Live = "Live", Hold = "Hold", Active = "Active", Paused = "Paused", Allowed = "Allowed", Blocked = "Blocked";
        private const string MotionAuto = "Automatic (follows Windows)", MotionReduced = "Reduced";

        private readonly Autodesk.Revit.UI.UIApplication _app;
        private readonly Window _win;
        private readonly Border _pill, _warn;
        private readonly TextBlock _pillText, _warnText;
        private readonly TextBlock _pauseLine = Line(), _modeLine = Line(), _codeLine = Line(), _motionLine = Line();
        private StackPanel _codeSeg;
        private bool _syncing;

        public static void Show(Autodesk.Revit.UI.UIApplication app) => new SettingsWindow(app)._win.ShowDialog();

        private SettingsWindow(Autodesk.Revit.UI.UIApplication app)
        {
            _app = app;
            _win = Theme.NewWindow("BIMBRIDGE · Settings", 620, 700);
            try { new WindowInteropHelper(_win) { Owner = app.MainWindowHandle }; } catch (Exception) { }
            var cfg = Runtime.Config;
            var root = new DockPanel();
            var header = Theme.Header(Theme.ProductEyebrow, "Settings", "Changes apply immediately and are saved.", out _, out _pill, out _pillText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var close = Theme.Button("Done", Theme.Kind.Primary);
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (s, e) => _win.Close();
            var footer = Theme.Footer(Theme.FooterNote("The same switches are on the BIMBRIDGE ribbon."), close);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel();
            _warn = Theme.Notice("", Theme.Amber, out _warnText);
            _warn.Visibility = Visibility.Collapsed;
            body.Children.Add(_warn);

            // ---- 1. AI control ----
            var control = new StackPanel();
            control.Children.Add(Theme.Section("AI control", "What happens to requests arriving now."));
            control.Children.Add(Row("AI", Theme.Segmented(new[] { Active, Paused }, Runtime.Pause.On ? Paused : Active, v => SetPause(v == Paused)), _pauseLine));
            control.Children.Add(Theme.Divider());
            var writes = cfg.Allows('P') && cfg.Allows('W');
            var modeSeg = Theme.Segmented(new[] { Live, Hold }, Runtime.Hold.On || !cfg.Allows('W') ? Hold : Live, v => SetHold(v == Hold));
            modeSeg.IsEnabled = writes;
            control.Children.Add(Row("Approved changes", modeSeg, _modeLine));
            if (!writes) control.Children.Add(Muted("Your permissions do not include applying changes (W): everything waits in Queue. Change it with the installer."));
            body.Children.Add(Theme.Card(control));

            // ---- 2. What the AI can do ----
            var perms = new StackPanel();
            perms.Children.Add(Theme.Section("What the AI can do", "Permissions chosen at install time."));
            var levels = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var items = new[] { ("Read the model", 'R'), ("Prepare changes (plan)", 'P'), ("Apply changes", 'W'), ("Save, sync, PDF", 'D') };
            for (var i = 0; i < items.Length; i++)
            {
                var on = items[i].Item2 == 'D' ? cfg.AllowsDocOps : cfg.Allows(items[i].Item2);
                if (i % 2 == 0) levels.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 6) };
                var mark = Theme.ResultMark(on ? Theme.Green : Theme.Slate, on);
                mark.Margin = new Thickness(0, 2, 8, 0);
                cell.Children.Add(mark);
                cell.Children.Add(Theme.Label(items[i].Item1 + (on ? "" : " · no"), 13, false, on ? Theme.Ink : Theme.Muted));
                Grid.SetRow(cell, i / 2);
                Grid.SetColumn(cell, i % 2);
                levels.Children.Add(cell);
            }
            perms.Children.Add(levels);
            perms.Children.Add(Muted("To change them, open the installer and choose Modify installation. They take effect when Revit restarts."));
            perms.Children.Add(Theme.Divider());
            _codeSeg = Theme.Segmented(new[] { Allowed, Blocked }, Runtime.BlockDynamicCode ? Blocked : Allowed, v => SetBlocked(v == Blocked));
            perms.Children.Add(Row("Dynamic C# code (exec)", _codeSeg, _codeLine));
            if (cfg.Runner == "off") perms.Children.Add(Muted("config.json has runner = off (off): exec never runs."));
            var why = new StackPanel();
            why.Children.Add(Muted("By default the authorized local AI can run dynamic C#, audited. Blocked: every new exec request is rejected; typed operations (plan/commit) remain available."));
            why.Children.Add(Muted("It is not a sandbox: the code runs in the Revit process with your user's permissions and its effects outside the document are not rolled back."));
            why.Children.Add(Muted("The change is saved in config.json and applies to new requests; it does not stop what is already running inside Revit."));
            var disc = Theme.Disclosure("What it means", why);
            disc.Margin = new Thickness(0, 8, 0, 0);
            perms.Children.Add(disc);
            body.Children.Add(Theme.Card(perms));

            // ---- 3. Interface ----
            var ui = new StackPanel();
            ui.Children.Add(Theme.Section("Interface", "For this Windows user only. Changes nothing about the AI."));
            ui.Children.Add(Row("Motion", Theme.Segmented(new[] { MotionAuto, MotionReduced }, Theme.ReduceMotionOverride ? MotionReduced : MotionAuto, v => SetMotion(v == MotionReduced)), _motionLine));
            body.Children.Add(Theme.Card(ui));

            root.Children.Add(Theme.Scroll(body));
            _win.Content = root;
            Describe(false);
        }

        // ---- changes: the same path as the ribbon buttons ----

        private void SetPause(bool on)
        {
            if (_syncing || Runtime.Pause.On == on) return;
            Runtime.TogglePause();
            Saved(Runtime.Pause.Unsaved, "the pause");
        }

        private void SetHold(bool on)
        {
            if (_syncing || Runtime.Hold.On == on) return;
            Runtime.ToggleHold();
            Saved(Runtime.Hold.Unsaved, "the mode");
        }

        private void SetBlocked(bool on)
        {
            if (_syncing || Runtime.BlockDynamicCode == on) return;
            // Allowing widens what the AI can do: the same confirmation as the ribbon, "No" by default.
            if (!on && !Runtime.ConfirmAllowDynamicCode()) { Select(_codeSeg, Blocked); return; }
            if (!Runtime.ToggleBlockDynamicCode(out var error))
            {
                Select(_codeSeg, Runtime.BlockDynamicCode ? Blocked : Allowed);
                ShowWarn("Could not save to config.json, so it was not applied. " + error);
                return;
            }
            Changed();
        }

        /// <summary>The add-in's own preference (not config.json). Applies immediately, without restarting.</summary>
        private void SetMotion(bool reduced)
        {
            if (_syncing || Theme.ReduceMotionOverride == reduced) return;
            if (!Runtime.SetReducedMotion(reduced, out var error)) ShowWarn("Could not save the motion preference: it holds until Revit closes. " + error);
            Describe(true);
        }

        private void Saved(bool unsaved, string what)
        {
            if (unsaved) ShowWarn("Could not save " + what + " to disk: it holds until Revit closes.");
            Changed();
        }

        private void Changed()
        {
            Runtime.MarkDirty();
            try { Runtime.OnIdling(_app); } catch (Exception ex) { Runtime.Log("Settings: " + ex.Message); }
            Describe(true);
        }

        private void ShowWarn(string text)
        {
            _warnText.Text = text;
            _warn.Visibility = Visibility.Visible;
            Theme.Enter(_warn, 4, 200);
        }

        /// <summary>Consequence of each setting in one line, and a pill with the effective state. On change, only the line that changed animates in.</summary>
        private void Describe(bool animate)
        {
            var cfg = Runtime.Config;
            var paused = Runtime.Pause.On;
            var hold = Runtime.Hold.On || !cfg.Allows('W');
            Set(_pauseLine, paused ? "Revit rejects every AI request, reads included." : "The AI can read the model and request changes.", animate);
            Set(_modeLine, hold ? "Changes wait in Queue; the model is not touched until you apply them." : "What you approve in the conversation is applied immediately.", animate);
            Set(_codeLine, Runtime.BlockDynamicCode ? "The AI will not be able to run C#; plan/commit remain available." : "The AI will be able to run C# on the Revit API, audited.", animate);
            Set(_motionLine, Theme.ReduceMotionOverride ? "No animations in the windows or on the ribbon; everything responds just as fast."
                : Theme.WindowsReducedMotion ? "Windows has animations turned off: no animations." : "Brief animations only on occasional state changes.", animate);
            if (paused) Theme.SetPill(_pill, _pillText, Theme.Red, "AI paused", true);
            else if (hold) Theme.SetPill(_pill, _pillText, Theme.Amber, "Hold", true);
            else Theme.SetPill(_pill, _pillText, Theme.Green, "Live", true);
        }

        private static void Set(TextBlock t, string text, bool animate)
        {
            if (t.Text == text) return;
            t.Text = text;
            if (animate && !Theme.FromKeyboard) Theme.Enter(t, 4, 180); // arrow keys on the segments: instant
        }

        private void Select(StackPanel seg, string value)
        {
            _syncing = true;
            try { foreach (var r in seg.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Content == value; }
            finally { _syncing = false; }
        }

        // ---- parts ----

        private static TextBlock Line() => new TextBlock { FontSize = 12.5, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        private static TextBlock Muted(string text)
        {
            var t = Theme.Label(text, 12.5, false, Theme.Muted);
            t.Margin = new Thickness(0, 6, 0, 0);
            return t;
        }

        /// <summary>Label on the left, segments on the right and the consequence below.</summary>
        private static UIElement Row(string label, StackPanel seg, TextBlock line)
        {
            var s = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = Theme.Label(label, 13.5, true);
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);
            seg.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(seg, label);
            Grid.SetColumn(seg, 1);
            g.Children.Add(seg);
            s.Children.Add(g);
            s.Children.Add(line);
            return s;
        }

        /// <summary>Ribbon icon: navy gear with a white center, on the 32×32 canvas of <see cref="Icons"/>.</summary>
        public static ImageSource Icon(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                var navy = Theme.B(Theme.Navy);
                for (var a = 0; a < 360; a += 45)
                {
                    dc.PushTransform(new RotateTransform(a, 16, 16));
                    dc.DrawRoundedRectangle(navy, null, new Rect(13, 2, 6, 8), 1.5, 1.5);
                    dc.Pop();
                }
                dc.DrawEllipse(navy, null, new Point(16, 16), 10.5, 10.5);
                dc.DrawEllipse(Theme.B(Theme.White), null, new Point(16, 16), 4.5, 4.5);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
