// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;
using Visibility = System.Windows.Visibility;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// "Change queue" window: shows what the AIs left waiting, lets you choose what to apply or discard,
    /// and shows each plan's result on the same card. It opens modally from a Revit command,
    /// so applying happens in a valid API context, without interrupting anything.
    /// </summary>
    internal sealed class QueueWindow
    {
        private readonly UIApplication _app;
        private readonly Document _doc;
        private readonly List<Plan> _plans;
        private readonly List<PlanCard> _cards = new List<PlanCard>();
        private Window _win;
        private Button _apply, _discard, _close;
        private CheckBox _all;
        private Border _banner;
        private TextBlock _bannerText;
        private TextBlock _pillText;
        private Border _pill;

        public QueueWindow(UIApplication app, Document doc, List<Plan> plans)
        {
            _app = app;
            _doc = doc;
            _plans = plans;
        }

        public void ShowDialog()
        {
            _win = Theme.NewWindow("BIMBRIDGE · Change queue", 860, 640);
            try { new WindowInteropHelper(_win) { Owner = _app.MainWindowHandle }; } catch (Exception) { }

            var root = new DockPanel();
            var header = Theme.Header("AI change queue", _doc.Title, out _, out _pill, out _pillText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel();
            _banner = Theme.Notice("", Theme.Slate, out _bannerText);
            _banner.Visibility = Visibility.Collapsed;
            body.Children.Add(_banner);

            if (_plans.Count == 0)
            {
                body.Children.Add(EmptyState());
            }
            else
            {
                var pos = 1;
                foreach (var p in _plans)
                {
                    var card = new PlanCard(p, pos++, _doc.Title, UpdateButtons);
                    _cards.Add(card);
                    body.Children.Add(card.Root);
                }
            }
            root.Children.Add(Theme.Scroll(body));
            _win.Content = root;
            UpdatePill();
            UpdateButtons();
            _win.ShowDialog();
        }

        private UIElement EmptyState()
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 64, 0, 0), MaxWidth = 400 };
            s.Children.Add(new Image { Source = Icons.Queue(0, 32), Width = 40, Height = 40, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 16) });
            s.Children.Add(new TextBlock { Text = "No changes waiting", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Ink), HorizontalAlignment = HorizontalAlignment.Center });
            s.Children.Add(new TextBlock
            {
                Text = "With Hold mode on, each AI change waits here before touching the model. You decide when to apply it.",
                FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 8, 0, 0),
            });
            return s;
        }

        private UIElement BuildFooter()
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            _all = new CheckBox { Content = "Select all", IsChecked = true, Margin = new Thickness(0, 0, 0, 4), Visibility = _plans.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
            _all.Click += (s, e) => { foreach (var c in _cards.Where(c => c.Pending)) c.Selected = _all.IsChecked == true; UpdateButtons(); };
            left.Children.Add(_all);
            left.Children.Add(new TextBlock
            {
                Text = "Each plan is undone with one Ctrl+Z. Applying does not save or sync the model.",
                FontSize = 12, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _discard = Theme.Button("Discard", Theme.Kind.Danger);
            _close = Theme.Button("Close");
            _apply = Theme.Button("Apply", Theme.Kind.Primary);
            _apply.IsDefault = true;
            _close.IsCancel = true;
            _discard.Click += (s, e) => Discard();
            _apply.Click += (s, e) => Apply();
            _close.Click += (s, e) => _win.Close();
            buttons.Children.Add(_discard);
            buttons.Children.Add(_close);
            buttons.Children.Add(_apply);

            return Theme.Footer(left, buttons);
        }

        private void UpdateButtons()
        {
            var n = _cards.Count(c => c.Pending && c.Selected);
            _apply.Content = n == 0 ? "Apply" : n == 1 ? "Apply 1 plan" : "Apply " + n + " plans";
            _apply.IsEnabled = n > 0;
            _discard.IsEnabled = n > 0;
            _apply.Visibility = _discard.Visibility = _cards.Any(c => c.Pending) ? Visibility.Visible : Visibility.Collapsed;
            if (_all != null) _all.Visibility = _cards.Any(c => c.Pending) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePill()
        {
            var pending = _cards.Count(c => c.Pending);
            Theme.SetPill(_pill, Theme.Tabular(_pillText), pending == 0 ? Theme.Green : Theme.Amber,
                pending == 0 ? "Nothing pending" : pending == 1 ? "1 plan waiting" : pending + " plans waiting", dot: true);
        }

        private void Apply()
        {
            var selected = _cards.Where(c => c.Pending && c.Selected).ToList();
            if (selected.Count == 0) return;
            _apply.IsEnabled = _discard.IsEnabled = _close.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            int ok = 0, conflict = 0, failed = 0;
            try
            {
                foreach (var card in selected)
                {
                    card.ShowRunning();
                    _win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render); // paint before applying
                    var r = Runtime.ApplyHeld(_doc, card.Plan);
                    card.ShowResult(r.Status, r.Detail);
                    if (r.Status == PlanStatus.Applied) ok++;
                    else if (r.Status == PlanStatus.Conflict) conflict++;
                    else failed++;
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _close.IsEnabled = true;
                Runtime.MarkDirty();
            }

            var msg = ok + (ok == 1 ? " plan applied" : " plans applied");
            if (conflict > 0) msg += " · " + conflict + " skipped because the model changed while waiting";
            if (failed > 0) msg += " · " + failed + " rolled back because of a Revit error";
            ShowBanner(msg, failed > 0 ? Theme.Red : conflict > 0 ? Theme.Amber : Theme.Green);
            _close.Content = "Done";
            UpdatePill();
            UpdateButtons();
        }

        private void Discard()
        {
            var selected = _cards.Where(c => c.Pending && c.Selected).ToList();
            if (selected.Count == 0) return;
            var r = TaskDialog.Show("BIMBRIDGE - Power Engine", "Discard " + (selected.Count == 1 ? "1 plan" : selected.Count + " plans") + " without applying?\nThe AI will see that you discarded them.",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No);
            if (r != TaskDialogResult.Yes) return;
            foreach (var c in selected)
            {
                Runtime.Plans.Finish(c.Plan, PlanStatus.Discarded, "discarded by the user");
                Runtime.Journal?.Log(VersionRegistry.HumanAuthor, "descartar", PlanStatus.Discarded, 0, new Dictionary<string, object> { ["plan"] = c.Plan.Id });
                c.ShowResult(PlanStatus.Discarded, "Discarded. The model was not touched.");
            }
            Runtime.MarkDirty();
            ShowBanner(selected.Count == 1 ? "1 plan discarded" : selected.Count + " plans discarded", Theme.Slate);
            UpdatePill();
            UpdateButtons();
        }

        private void ShowBanner(string text, Color color)
        {
            _bannerText.Text = text;
            Theme.SetNotice(_banner, _bannerText, color);
            _banner.Visibility = Visibility.Visible;
            Theme.Enter(_banner, 4, 200); // result of a user action: occasional
        }

        // ------------------------------------------------------------------

        /// <summary>A plan's card: who asked for it, what changes, the expandable detail and its result.</summary>
        private sealed class PlanCard
        {
            public readonly Plan Plan;
            public readonly Border Root;
            private readonly CheckBox _check;
            private readonly Border _statusChip;
            private readonly TextBlock _statusText;
            private readonly TextBlock _detail;
            public bool Pending = true;

            public bool Selected { get => _check.IsChecked == true; set => _check.IsChecked = value; }

            public PlanCard(Plan p, int position, string docTitle, Action changed)
            {
                Plan = p;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                _check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0) };
                _check.Click += (s, e) => changed();
                grid.Children.Add(_check);

                var main = new StackPanel();
                var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; // the last line fills and trims long titles
                top.Children.Add(new TextBlock { Text = "#" + position, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                top.Children.Add(Theme.Chip(Theme.ClientName(p.Client ?? p.SessionId), Theme.ClientColor(p.Client ?? p.SessionId)));
                top.Children.Add(new TextBlock
                {
                    Text = Counts(p) + " · " + (string.IsNullOrWhiteSpace(p.DocTitle) ? docTitle : p.DocTitle) + " · " + Theme.Ago(p.HeldUtc ?? p.CreatedUtc),
                    Foreground = Theme.B(Theme.Muted), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                main.Children.Add(top);
                main.Children.Add(new TextBlock { Text = Friendly(p), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Ink), TextWrapping = TextWrapping.Wrap });

                // risk warnings: the first ones in view (they decide whether to apply); the rest inside "View changes"
                foreach (var w in p.Warnings.Take(4))
                    main.Children.Add(WarningLine(w));

                _detail = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
                main.Children.Add(_detail);

                // technical detail (table, ids, plan) one click away; built the first time it opens
                var details = new Border();
                var disclosure = Theme.Disclosure("View changes (" + p.Entries.Count + ")", details);
                disclosure.Margin = new Thickness(0, 10, 0, 0);
                ((System.Windows.Controls.Primitives.ToggleButton)disclosure.Children[0]).Checked += (s, e) => { if (details.Child == null) details.Child = Details(p); };
                main.Children.Add(disclosure);
                Grid.SetColumn(main, 1);
                grid.Children.Add(main);

                _statusText = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
                _statusChip = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = _statusText };
                SetChip("Waiting", Theme.Amber);
                Grid.SetColumn(_statusChip, 2);
                grid.Children.Add(_statusChip);

                Root = Theme.Card(grid);
            }

            public void ShowRunning() => SetChip("Applying…", Theme.NavySoft);

            public void ShowResult(string status, string detail)
            {
                Pending = false;
                _check.IsChecked = false;
                _check.IsEnabled = false;
                switch (status)
                {
                    case PlanStatus.Applied: SetChip("Applied", Theme.Green); break;
                    case PlanStatus.Conflict: SetChip("Skipped", Theme.Amber); break;
                    case PlanStatus.Discarded: SetChip("Discarded", Theme.Slate); break;
                    default: SetChip("Rolled back", Theme.Red); break;
                }
                _detail.Text = Humanize(status, detail);
                _detail.Foreground = Theme.B(status == PlanStatus.Applied ? Theme.Darken(Theme.Green, 0.8) : status == PlanStatus.Discarded ? Theme.Muted : Theme.Darken(status == PlanStatus.Conflict ? Theme.Amber : Theme.Red, 0.8));
                _detail.Visibility = Visibility.Visible;
                Theme.Enter(_detail, 4, 200); // occasional state change (apply/discard), not a refresh
                Root.Opacity = status == PlanStatus.Discarded ? 0.7 : 1;
            }

            private void SetChip(string text, Color c) => Theme.SetPill(_statusChip, _statusText, c, text);

            private static TextBlock WarningLine(string w) =>
                new TextBlock { Text = "• " + w, FontSize = 12.5, Foreground = Theme.B(Theme.Darken(Theme.Amber, 0.72)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

            /// <summary>What sits behind "View changes": remaining warnings, the change table and the plan identifier.</summary>
            private static UIElement Details(Plan p)
            {
                var s = new StackPanel();
                foreach (var w in p.Warnings.Skip(4))
                    s.Children.Add(WarningLine(w));
                var table = DiffTable(p);
                ((FrameworkElement)table).Margin = new Thickness(0, s.Children.Count > 0 ? 8 : 0, 0, 0);
                s.Children.Add(table);
                var id = new TextBlock { FontSize = 11.5, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
                id.Inlines.Add(new System.Windows.Documents.Run("Plan "));
                id.Inlines.Add(new System.Windows.Documents.Run(p.Id ?? "") { FontFamily = Theme.Mono });
                s.Children.Add(id);
                return s;
            }

            private static string Humanize(string status, string detail)
            {
                switch (status)
                {
                    case PlanStatus.Applied: return "Done: " + detail + ". Ctrl+Z undoes it.";
                    case PlanStatus.Conflict: return "Not applied: someone changed these elements while the plan was waiting. The AI will know and can plan it again. Detail: " + detail;
                    case PlanStatus.Discarded: return detail;
                    default: return "Revit did not accept the change and it was fully undone. Detail: " + detail;
                }
            }

            private static string Counts(Plan p)
            {
                var n = p.ElementCount;
                return n == 1 ? "1 element" : n + " elements";
            }

            /// <summary>Summary in words: the one the AI gave or a generated one ("Height on 12 elements").</summary>
            private static string Friendly(Plan p)
            {
                if (!string.IsNullOrWhiteSpace(p.Summary) && !p.Summary.Contains("×")) return p.Summary;
                var parts = p.Entries.GroupBy(e => e.Op + "|" + (e.Op == "set_param" ? e.Key : "")).Select(g =>
                {
                    var op = g.First().Op;
                    var created = g.Count(e => e.IsCreation);
                    var n = g.Where(e => e.Id != 0).Select(e => e.Id).Distinct().Count();
                    var what = n == 1 ? "1 element" : n + " elements";
                    switch (op)
                    {
                        case "set_param": return "Change «" + g.First().Key + "» on " + what;
                        case "change_type": return "Change type of " + what;
                        case "set_elevation": return "Change height of " + what;
                        case "set_workset": return "Move " + what + " to another workset";
                        case "delete": return "Delete " + what;
                        default:
                            var name = OpCatalog.NameOf(op);
                            if (created > 0) return name + ": " + created + " new";
                            if (n > 0) return name + " on " + what;
                            return name + " (on what the plan creates)";
                    }
                });
                return string.Join(" · ", parts);
            }

            private static UIElement DiffTable(Plan p)
            {
                var rows = p.Entries.Take(300).Select(e => new DiffRow
                {
                    Element = e.Ref > 0 ? "created by @" + e.Ref : e.Id == 0 ? "new" : e.Id.ToString(CultureInfo.InvariantCulture),
                    Change = OpName(e.Op),
                    Field = Field(e.Key),
                    Before = e.Id == 0 ? "—" : Show(e, e.Before),
                    After = e.Show ?? Show(e, e.After),
                }).ToList();
                var grid = new DataGrid
                {
                    ItemsSource = rows,
                    IsReadOnly = true,
                    AutoGenerateColumns = true,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                    HorizontalGridLinesBrush = Theme.B(Theme.Line),
                    BorderThickness = new Thickness(0),
                    Background = Theme.B(Theme.Sunken),
                    RowBackground = Theme.B(Theme.Sunken),
                    AlternatingRowBackground = Theme.B(Theme.Page),
                    Foreground = Theme.B(Theme.Ink),
                    FontFamily = Theme.Font,
                    CanUserAddRows = false,
                    MaxHeight = 260,
                    FontSize = 12,
                    ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star),
                };
                // headers and labels in the normal font; monospace only for ids and values
                grid.AutoGeneratingColumn += (s, e) =>
                {
                    // column headers come from DiffRow property names; show them in English
                    switch (e.PropertyName)
                    {
                        case nameof(DiffRow.Element): e.Column.Header = "Element"; break;
                        case nameof(DiffRow.Change): e.Column.Header = "Change"; break;
                        case nameof(DiffRow.Field): e.Column.Header = "Field"; break;
                        case nameof(DiffRow.Before): e.Column.Header = "Before"; break;
                        case nameof(DiffRow.After): e.Column.Header = "After"; break;
                    }
                    if (e.Column is DataGridTextColumn tc && (e.PropertyName == nameof(DiffRow.Element) || e.PropertyName == nameof(DiffRow.Before) || e.PropertyName == nameof(DiffRow.After)))
                        tc.FontFamily = Theme.Mono;
                };
                // sunken reading area: the diff stands apart from the rest of the card
                var panel = new StackPanel();
                panel.Children.Add(grid);
                if (p.Entries.Count > 300)
                    panel.Children.Add(new TextBlock { Text = "… and " + (p.Entries.Count - 300) + " more changes", FontSize = 12, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 8, 0, 0) });
                return Theme.Inset(panel);
            }

            private static string OpName(string op) => OpCatalog.NameOf(op);

            private static string Field(string key)
            {
                if (key == null) return "";
                switch (key)
                {
                    case "elev": return "Height";
                    case "ws": return "Workset";
                    case "type": return "Type";
                    case "lvl": return "Level";
                    case "loc": return "Position";
                    case "rot": return "Rotation";
                    case "head": return "Tag head";
                    case "exists": case "nuevo": return "";
                    case "tag": return "Tag";
                    case "rango": return "View range";
                    case "plantilla": return "Template";
                    case "escala": return "Scale";
                    case "detalle": return "Detail level";
                    case "recorte": return "Crop";
                    case "caja_seccion": return "Section box";
                }
                if (key.StartsWith("oculto@", StringComparison.Ordinal)) return "Hidden in view";
                if (key.StartsWith("graficos@", StringComparison.Ordinal)) return "Graphics in view";
                if (key.StartsWith("cat_oculta:", StringComparison.Ordinal)) return "Hidden category: " + key.Substring(11);
                if (key.StartsWith("cat_graficos:", StringComparison.Ordinal)) return "Graphics of " + key.Substring(13);
                return key;
            }

            private static string Show(PlanEntry e, string v)
            {
                if (e.Key == "elev" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ft)) return FeetInches(ft);
                var shown = Plan.ShowValue(e, v, false);
                return string.IsNullOrEmpty(shown) ? "(empty)" : shown;
            }

            /// <summary>10.5 → 10'-6"; rounded to 1/8".</summary>
            internal static string FeetInches(double ft)
            {
                var sign = ft < 0 ? "-" : "";
                var eighths = (long)Math.Round(Math.Abs(ft) * 96);
                var feet = eighths / 96;
                var rem = eighths % 96;
                var inches = rem / 8;
                var frac = rem % 8;
                var fracText = "";
                if (frac != 0)
                {
                    var den = 8L; var num = frac;
                    while (num % 2 == 0) { num /= 2; den /= 2; }
                    fracText = " " + num + "/" + den;
                }
                return sign + feet + "'-" + inches + fracText + "\"";
            }
        }

        public sealed class DiffRow
        {
            public string Element { get; set; }
            public string Change { get; set; }
            public string Field { get; set; }
            public string Before { get; set; }
            public string After { get; set; }
        }
    }
}
