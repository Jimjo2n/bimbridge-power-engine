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
using PDI.RevitMcp.Engine;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;
using Visibility = System.Windows.Visibility;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Ventana "Cola de cambios": muestra lo que las IAs dejaron esperando, deja elegir qué aplicar o descartar,
    /// y muestra el resultado de cada plan en la misma tarjeta. Se abre modal desde un comando de Revit,
    /// así que aplicar ocurre en un contexto válido de la API, sin interrumpir nada.
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
            _win = Theme.NewWindow("BIMBRIDGE · Cola de cambios", 860, 640);
            try { new WindowInteropHelper(_win) { Owner = _app.MainWindowHandle }; } catch (Exception) { }

            var root = new DockPanel();
            var header = Theme.Header("Cola de cambios de la IA", _doc.Title, out _, out _pill, out _pillText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel();
            _bannerText = Theme.Label("", 13, true);
            _banner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 11), Margin = new Thickness(0, 0, 0, 12), Child = _bannerText, Visibility = Visibility.Collapsed };
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
                    var card = new PlanCard(p, pos++, UpdateButtons);
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
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0), MaxWidth = 440 };
            s.Children.Add(new Image { Source = Icons.Queue(0, 32), Width = 48, Height = 48, Margin = new Thickness(0, 0, 0, 14) });
            s.Children.Add(new TextBlock { Text = "No hay cambios esperando", FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            s.Children.Add(new TextBlock
            {
                Text = "Con el modo Retener activado, cada cambio que pida la IA aparece aquí antes de tocar el modelo. Usted decide cuándo aplicarlo.",
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 8, 0, 0),
            });
            return s;
        }

        private UIElement BuildFooter()
        {
            var grid = new Grid { Margin = new Thickness(24, 12, 24, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _all = new CheckBox { Content = "Seleccionar todo", IsChecked = true, Margin = new Thickness(0, 0, 0, 4), Visibility = _plans.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
            _all.Click += (s, e) => { foreach (var c in _cards.Where(c => c.Pending)) c.Selected = _all.IsChecked == true; UpdateButtons(); };
            left.Children.Add(_all);
            left.Children.Add(new TextBlock
            {
                Text = "Cada plan se deshace con un Ctrl+Z. Aplicar no guarda ni sincroniza el modelo.",
                FontSize = 11.5, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap,
            });
            grid.Children.Add(left);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _discard = Theme.Button("Descartar", Theme.Kind.Danger);
            _close = Theme.Button("Cerrar");
            _apply = Theme.Button("Aplicar", Theme.Kind.Primary);
            _apply.IsDefault = true;
            _close.IsCancel = true;
            _discard.Click += (s, e) => Discard();
            _apply.Click += (s, e) => Apply();
            _close.Click += (s, e) => _win.Close();
            buttons.Children.Add(_discard);
            buttons.Children.Add(_close);
            buttons.Children.Add(_apply);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            return new Border { Background = Theme.B(Theme.White), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
        }

        private void UpdateButtons()
        {
            var n = _cards.Count(c => c.Pending && c.Selected);
            _apply.Content = n == 0 ? "Aplicar" : n == 1 ? "Aplicar 1 plan" : "Aplicar " + n + " planes";
            _apply.IsEnabled = n > 0;
            _discard.IsEnabled = n > 0;
            _apply.Visibility = _discard.Visibility = _cards.Any(c => c.Pending) ? Visibility.Visible : Visibility.Collapsed;
            if (_all != null) _all.Visibility = _cards.Any(c => c.Pending) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePill()
        {
            var pending = _cards.Count(c => c.Pending);
            _pillText.Text = pending == 0 ? "Sin pendientes" : pending == 1 ? "1 plan esperando" : pending + " planes esperando";
            _pill.Background = pending == 0 ? Theme.Tint(Theme.Green, 0x90) : Theme.Tint(Theme.Amber, 0xB0);
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
                    _win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render); // pintar antes de aplicar
                    var r = Runtime.ApplyHeld(_doc, card.Plan);
                    card.ShowResult(r.Status, r.Detail);
                    if (r.Status == PlanStatus.Aplicado) ok++;
                    else if (r.Status == PlanStatus.Conflicto) conflict++;
                    else failed++;
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _close.IsEnabled = true;
                Runtime.MarkDirty();
            }

            var msg = ok + (ok == 1 ? " plan aplicado" : " planes aplicados");
            if (conflict > 0) msg += " · " + conflict + " omitido" + (conflict > 1 ? "s" : "") + " porque el modelo cambió mientras esperaba";
            if (failed > 0) msg += " · " + failed + " revertido" + (failed > 1 ? "s" : "") + " por un error de Revit";
            ShowBanner(msg, failed > 0 ? Theme.Red : conflict > 0 ? Theme.Amber : Theme.Green);
            _close.Content = "Listo";
            UpdatePill();
            UpdateButtons();
        }

        private void Discard()
        {
            var selected = _cards.Where(c => c.Pending && c.Selected).ToList();
            if (selected.Count == 0) return;
            var r = TaskDialog.Show("BIMBRIDGE - Power Engine", "¿Descartar " + (selected.Count == 1 ? "1 plan" : selected.Count + " planes") + " sin aplicarlos?\nLa IA verá que usted los descartó.",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No);
            if (r != TaskDialogResult.Yes) return;
            foreach (var c in selected)
            {
                Runtime.Plans.Finish(c.Plan, PlanStatus.Descartado, "descartado por el usuario");
                Runtime.Journal?.Log(VersionRegistry.HumanAuthor, "descartar", PlanStatus.Descartado, 0, new Dictionary<string, object> { ["plan"] = c.Plan.Id });
                c.ShowResult(PlanStatus.Descartado, "Descartado. No se tocó el modelo.");
            }
            Runtime.MarkDirty();
            ShowBanner(selected.Count == 1 ? "1 plan descartado" : selected.Count + " planes descartados", Theme.Slate);
            UpdatePill();
            UpdateButtons();
        }

        private void ShowBanner(string text, Color color)
        {
            _bannerText.Text = text;
            _bannerText.Foreground = Theme.B(Theme.Darken(color, 0.75));
            _banner.Background = Theme.Tint(color, 0x22);
            _banner.BorderBrush = Theme.Tint(color, 0x55);
            _banner.BorderThickness = new Thickness(1);
            _banner.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------

        /// <summary>Tarjeta de un plan: quién lo pidió, qué cambia, el detalle desplegable y su resultado.</summary>
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

            public PlanCard(Plan p, int position, Action changed)
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
                var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                top.Children.Add(new TextBlock { Text = "#" + position, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                top.Children.Add(Theme.Chip(Theme.ClientName(p.Client ?? p.SessionId), Theme.ClientColor(p.Client ?? p.SessionId)));
                top.Children.Add(new TextBlock
                {
                    Text = Counts(p) + " · " + Theme.Ago(p.HeldUtc ?? p.CreatedUtc),
                    Foreground = Theme.B(Theme.Muted), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                });
                main.Children.Add(top);
                main.Children.Add(new TextBlock { Text = Friendly(p), FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

                foreach (var w in p.Warnings.Take(4))
                    main.Children.Add(new TextBlock { Text = "• " + w, FontSize = 12, Foreground = Theme.B(Theme.Darken(Theme.Amber, 0.75)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

                _detail = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
                main.Children.Add(_detail);

                var exp = new Expander { Header = "Ver cambios (" + p.Entries.Count + ")", Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.B(Theme.NavySoft) };
                exp.Expanded += (s, e) => { if (exp.Content == null) exp.Content = DiffTable(p); };
                main.Children.Add(exp);
                Grid.SetColumn(main, 1);
                grid.Children.Add(main);

                _statusText = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
                _statusChip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Top, Child = _statusText };
                SetChip("En espera", Theme.Amber);
                Grid.SetColumn(_statusChip, 2);
                grid.Children.Add(_statusChip);

                Root = Theme.Card(grid);
            }

            public void ShowRunning() => SetChip("Aplicando…", Theme.NavySoft);

            public void ShowResult(string status, string detail)
            {
                Pending = false;
                _check.IsChecked = false;
                _check.IsEnabled = false;
                switch (status)
                {
                    case PlanStatus.Aplicado: SetChip("Aplicado", Theme.Green); break;
                    case PlanStatus.Conflicto: SetChip("Omitido", Theme.Amber); break;
                    case PlanStatus.Descartado: SetChip("Descartado", Theme.Slate); break;
                    default: SetChip("Revertido", Theme.Red); break;
                }
                _detail.Text = Humanize(status, detail);
                _detail.Foreground = Theme.B(status == PlanStatus.Aplicado ? Theme.Darken(Theme.Green, 0.8) : status == PlanStatus.Descartado ? Theme.Muted : Theme.Darken(status == PlanStatus.Conflicto ? Theme.Amber : Theme.Red, 0.8));
                _detail.Visibility = Visibility.Visible;
                Root.Opacity = status == PlanStatus.Descartado ? 0.7 : 1;
            }

            private void SetChip(string text, Color c)
            {
                _statusText.Text = text;
                _statusText.Foreground = Theme.B(Theme.Darken(c, 0.78));
                _statusChip.Background = Theme.Tint(c, 0x24);
            }

            private static string Humanize(string status, string detail)
            {
                switch (status)
                {
                    case PlanStatus.Aplicado: return "Listo: " + detail + ". Ctrl+Z lo deshace.";
                    case PlanStatus.Conflicto: return "No se aplicó: alguien cambió estos elementos mientras el plan esperaba. La IA lo sabrá y puede volver a planificarlo. Detalle: " + detail;
                    case PlanStatus.Descartado: return detail;
                    default: return "Revit no aceptó el cambio y se deshizo completo. Detalle: " + detail;
                }
            }

            private static string Counts(Plan p)
            {
                var n = p.ElementCount;
                return n == 1 ? "1 elemento" : n + " elementos";
            }

            /// <summary>Resumen en palabras: el que dio la IA o uno generado ("Altura en 12 elementos").</summary>
            private static string Friendly(Plan p)
            {
                if (!string.IsNullOrWhiteSpace(p.Summary) && !p.Summary.Contains("×")) return p.Summary;
                var parts = p.Entries.GroupBy(e => e.Op + "|" + (e.Op == "set_param" ? e.Key : "")).Select(g =>
                {
                    var op = g.First().Op;
                    var created = g.Count(e => e.IsCreation);
                    var n = g.Where(e => e.Id != 0).Select(e => e.Id).Distinct().Count();
                    var what = n == 1 ? "1 elemento" : n + " elementos";
                    switch (op)
                    {
                        case "set_param": return "Cambiar «" + g.First().Key + "» en " + what;
                        case "change_type": return "Cambiar tipo de " + what;
                        case "set_elevation": return "Cambiar altura de " + what;
                        case "set_workset": return "Mover " + what + " de workset";
                        case "delete": return "Borrar " + what;
                        default:
                            var name = OpCatalog.NameOf(op);
                            if (created > 0) return name + ": " + created + (created == 1 ? " nuevo" : " nuevos");
                            if (n > 0) return name + " en " + what;
                            return name + " (sobre lo que crea el plan)";
                    }
                });
                return string.Join(" · ", parts);
            }

            private static UIElement DiffTable(Plan p)
            {
                var rows = p.Entries.Take(300).Select(e => new DiffRow
                {
                    Elemento = e.Ref > 0 ? "lo creado por @" + e.Ref : e.Id == 0 ? "nuevo" : e.Id.ToString(CultureInfo.InvariantCulture),
                    Cambio = OpName(e.Op),
                    Campo = Field(e.Key),
                    Antes = e.Id == 0 ? "—" : Show(e, e.Before),
                    Después = e.Show ?? Show(e, e.After),
                }).ToList();
                var grid = new DataGrid
                {
                    ItemsSource = rows,
                    IsReadOnly = true,
                    AutoGenerateColumns = true,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                    HorizontalGridLinesBrush = Theme.B(Theme.Line),
                    BorderBrush = Theme.B(Theme.Line),
                    Background = Theme.B(Theme.White),
                    RowBackground = Theme.B(Theme.White),
                    AlternatingRowBackground = Theme.B(Color.FromRgb(0xF8, 0xF9, 0xFB)),
                    CanUserAddRows = false,
                    MaxHeight = 260,
                    FontSize = 12,
                    Margin = new Thickness(0, 6, 0, 0),
                    ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star),
                };
                if (p.Entries.Count > 300)
                {
                    var s = new StackPanel();
                    s.Children.Add(grid);
                    s.Children.Add(new TextBlock { Text = "… y " + (p.Entries.Count - 300) + " cambios más", Foreground = Theme.B(Theme.Muted), Margin = new Thickness(0, 4, 0, 0) });
                    return s;
                }
                return grid;
            }

            private static string OpName(string op) => OpCatalog.NameOf(op);

            private static string Field(string key)
            {
                if (key == null) return "";
                switch (key)
                {
                    case "elev": return "Altura";
                    case "ws": return "Workset";
                    case "type": return "Tipo";
                    case "lvl": return "Nivel";
                    case "loc": return "Posición";
                    case "rot": return "Giro";
                    case "head": return "Cabeza del tag";
                    case "exists": case "nuevo": return "";
                    case "tag": return "Tag";
                    case "rango": return "Rango de vista";
                    case "plantilla": return "Plantilla";
                    case "escala": return "Escala";
                    case "detalle": return "Nivel de detalle";
                    case "recorte": return "Recorte";
                    case "caja_seccion": return "Caja de sección";
                }
                if (key.StartsWith("oculto@", StringComparison.Ordinal)) return "Oculto en vista";
                if (key.StartsWith("graficos@", StringComparison.Ordinal)) return "Gráficos en vista";
                if (key.StartsWith("cat_oculta:", StringComparison.Ordinal)) return "Categoría oculta: " + key.Substring(11);
                if (key.StartsWith("cat_graficos:", StringComparison.Ordinal)) return "Gráficos de " + key.Substring(13);
                return key;
            }

            private static string Show(PlanEntry e, string v)
            {
                if (e.Key == "elev" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ft)) return FeetInches(ft);
                var shown = Plan.ShowValue(e, v, false);
                return string.IsNullOrEmpty(shown) ? "(vacío)" : shown;
            }

            /// <summary>10.5 → 10'-6"; redondeado a 1/8".</summary>
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
            public string Elemento { get; set; }
            public string Cambio { get; set; }
            public string Campo { get; set; }
            public string Antes { get; set; }
            public string Después { get; set; }
        }
    }
}
