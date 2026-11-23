// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;

namespace Bimbridge.Addin.Ui
{
    /// <summary>Short guide to the buttons, with the same icons as the ribbon.</summary>
    internal static class HelpWindow
    {
        public static void Show(IntPtr owner)
        {
            var win = Theme.NewWindow("BIMBRIDGE · How it works", 600, 640);
            try { new WindowInteropHelper(win) { Owner = owner }; } catch (Exception) { }
            var root = new DockPanel();
            var header = Theme.Header("How it works", "Claude and Codex work with the open model; you decide what gets applied.", out _, out var pill, out var pillText);
            pill.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var body = new StackPanel();
            body.Children.Add(Item(Icons.Status(Theme.Green, 32), "Status",
                "Shows which AIs are connected, what they can do and what they did, in plain words. Green: an AI is connected. Grey: waiting. Red: paused."));
            body.Children.Add(Item(Icons.Pause(false, 32), "Pause AI",
                "Cuts everything off instantly: while paused, Revit rejects any AI request. Press it again to resume."));
            body.Children.Add(Item(Icons.Mode(false, 32), "Live mode",
                "The changes you approve in the conversation are applied immediately."));
            body.Children.Add(Item(Icons.Mode(true, 32), "Hold mode",
                "Changes wait in a queue saved on disk and the model is not touched. Keep working in peace; the queue survives Revit closing or crashing."));
            body.Children.Add(Item(Icons.Queue(3, 32), "Queue",
                "Review each change (before and after) and choose which ones to apply or discard. If something changed while it was waiting, that change is skipped and the AI is told. Each plan is undone with one Ctrl+Z."));
            body.Children.Add(Item(Icons.Update(1, 32), "Version / Update",
                "When someone gives you a new installer, drop it in Downloads or on the Desktop (without opening it). The button turns green: press it and it gets ready to install when Revit closes, " +
                "with the same options; it takes effect when Revit restarts. Old installers go to the Recycle Bin and you can go back to the previous version."));
            body.Children.Add(Theme.Card(Theme.Section("What the AI can do",
                "Depending on the permissions you chose at install time: read the model; propose typed changes with their before and after (plan); " +
                "run C# code on the Revit API (exec, permission W), which is saved and logged in %LOCALAPPDATA%\\Bimbridge\\scripts; " +
                "edit families and connectors; and save, sync or export PDF (permission D or W). None of this is hidden: every operation is logged. " +
                "Pause stops everything that arrives afterwards.")));
            root.Children.Add(Theme.Scroll(body));

            var close = Theme.Button("Got it", Theme.Kind.Primary);
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (s, e) => win.Close();
            var footer = Theme.Footer(null, close);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Insert(1, footer);
            win.Content = root;
            win.ShowDialog();
        }

        private static UIElement Item(System.Windows.Media.ImageSource icon, string title, string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Exactly 32 px: the icon is a 32 px bitmap and scaling it blurs it.
            g.Children.Add(new Image { Source = icon, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(Theme.Label(title, 14.5, true));
            var t = Theme.Label(text, 13, false, Theme.Muted);
            t.Margin = new Thickness(0, 3, 0, 0);
            s.Children.Add(t);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return Theme.Card(g);
        }
    }
}
