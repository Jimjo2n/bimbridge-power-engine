// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PDI.RevitMcp.Setup;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Ventana del botón Actualizar: versión en uso, versión nueva encontrada (y dónde), instalar sin cerrar Revit,
    /// volver a la versión anterior. Todo el trabajo pesado lo hace el instalador en modo /actualizar.
    /// </summary>
    internal sealed class UpdateWindow
    {
        private readonly Window _win;
        private readonly StackPanel _body = new StackPanel();
        private readonly Border _banner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        private readonly TextBlock _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        private readonly Button _recheck;
        private TextBlock _subtitle;
        private bool _busy;

        public static void Show(IntPtr owner) => new UpdateWindow(owner)._win.ShowDialog();

        private UpdateWindow(IntPtr owner)
        {
            _win = Theme.NewWindow("BIMBRIDGE · Actualizaciones", 620, 560);
            try { new WindowInteropHelper(_win) { Owner = owner }; } catch (Exception) { }
            _banner.Child = _bannerText;

            var root = new DockPanel();
            var header = Theme.Header("Actualizaciones", "", out _subtitle, out var pill, out _);
            pill.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _recheck = Theme.Button("Buscar otra vez");
            _recheck.Click += async (s, e) => await Recheck();
            var close = Theme.Button("Cerrar", Theme.Kind.Primary);
            close.IsCancel = true;
            close.Click += (s, e) => { if (!_busy) _win.Close(); };
            var footer = new Border
            {
                Background = Theme.B(Theme.White), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(24, 12, 24, 14),
                Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _recheck, close } },
            };
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
            _subtitle.Text = "Versión en uso: " + Updates.Format(UpdateService.Running);
            _body.Children.Clear();
            var a = UpdateService.Available;

            if (UpdateService.RestartPending)
            {
                _body.Children.Add(StatusCard(Theme.Amber, "Falta reiniciar Revit",
                    "La versión " + UpdateService.InstalledVersion + " ya está instalada. Guarde su trabajo, cierre Revit y vuelva a abrirlo para usarla. " +
                    "Las conversaciones de Claude o Codex usan la versión nueva cuando empiece una nueva."));
            }
            else if (a != null)
            {
                var install = Theme.Button("Instalar " + a.VersionText, Theme.Kind.Primary);
                install.Margin = new Thickness(0, 12, 0, 0);
                install.HorizontalAlignment = HorizontalAlignment.Left;
                install.Click += async (s, e) => await Install(a, false);
                var card = StatusCard(Theme.Green, "Hay una versión nueva: " + a.VersionText,
                    "Encontrada en " + a.Source + "\n\nSe instala sin cerrar Revit y con sus mismas opciones (versiones de Revit, permisos de la IA). " +
                    "La cola de cambios y los registros se conservan. Entra en uso la próxima vez que abra Revit.");
                ((StackPanel)((Grid)card.Child).Children[1]).Children.Add(install);
                _body.Children.Add(card);
            }
            else
            {
                _body.Children.Add(StatusCard(Theme.Green, "Está al día",
                    "No hay instaladores más nuevos que la versión " + Updates.Format(UpdateService.Running) + "."));
            }

            _body.Children.Add(OnlineCard());

            // Cómo llega una versión nueva
            var how = new StackPanel();
            how.Children.Add(Theme.Label("Cómo recibir una versión nueva", 14, true));
            var t = Theme.Label("Cuando le pasen un instalador nuevo (PDI.RevitMcp.Setup….zip o .exe), sólo déjelo en una de estas carpetas; no hace falta abrirlo ni descomprimirlo. " +
                                "Este botón se pone verde solo, en unos minutos, o al pulsar Buscar otra vez. Después de instalar, los instaladores viejos se mandan a la papelera.", 12.5, false, Theme.Muted);
            t.Margin = new Thickness(0, 4, 0, 6);
            how.Children.Add(t);
            foreach (var f in UpdateService.Folders)
                how.Children.Add(new TextBlock { Text = "•  " + f, FontSize = 12, Foreground = Theme.B(Theme.Ink), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 1, 0, 1) });
            _body.Children.Add(Theme.Card(how));

            // Volver atrás
            var prevExe = UpdateService.PreviousSetup;
            if (prevExe != null && !UpdateService.RestartPending)
            {
                var back = new StackPanel();
                back.Children.Add(Theme.Label("Volver a la versión " + UpdateService.PreviousVersion, 14, true));
                var bt = Theme.Label("Si la versión actual le da problemas, puede volver a la anterior. Se aplica al reiniciar Revit.", 12.5, false, Theme.Muted);
                bt.Margin = new Thickness(0, 4, 0, 8);
                back.Children.Add(bt);
                var btn = Theme.Button("Volver a " + UpdateService.PreviousVersion, Theme.Kind.Danger);
                btn.Margin = new Thickness(0);
                btn.HorizontalAlignment = HorizontalAlignment.Left;
                var prevVersion = Updates.Parse(UpdateService.PreviousVersion);
                btn.Click += async (s, e) => await Install(new UpdateCandidate { Version = prevVersion, Exe = prevExe, Source = prevExe }, true);
                back.Children.Add(btn);
                _body.Children.Add(Theme.Card(back));
            }
        }

        /// <summary>
        /// 0.9.1: actualización en línea. Sólo se conecta a GitHub cuando la persona pulsa «Buscar en GitHub»; descargar e instalar
        /// es otro clic. El instalador se verifica (SHA-256 publicado, tamaño y versión) antes de ofrecer la instalación.
        /// </summary>
        private UIElement OnlineCard()
        {
            var p = new StackPanel();
            p.Children.Add(Theme.Label("Actualización en línea (GitHub)", 14, true));
            var state = UpdateService.OnlineState;
            var r = UpdateService.Online;
            string text;
            if (!UpdateService.OnlineEnabled) text = "Deshabilitada en la configuración de este equipo (actualizacion_online=no).";
            else if (state == null) text = "Consulta la última versión publicada en github.com/" + UpdateService.Repo + ". Sólo se conecta cuando usted pulsa el botón; no hace falta cuenta de GitHub.";
            else if (state == "error") text = "No se pudo consultar: " + UpdateService.OnlineError;
            else if (state == "sin_releases") text = "El repositorio todavía no tiene ninguna versión publicada que se pueda instalar." + (UpdateService.OnlineError != null ? "\n" + UpdateService.OnlineError : "");
            else if (state == "nueva") text = "Versión publicada: " + r.Tag + (r.Commit != null ? " (commit " + r.Commit.Substring(0, 7) + ")" : "") + (r.Prerelease ? " · prerelease" : "") + ". Es más nueva que la que está usando.";
            else if (state == "mismo_version_otro_commit") text = "La versión publicada " + r.Tag + " tiene el mismo número que la suya pero otro commit (" + r.Commit.Substring(0, 7) + "; usted usa " + (UpdateService.RunningCommit ?? "?") + "). Puede instalarla si quiere exactamente la publicada.";
            else if (state == "anterior") text = "La última versión publicada (" + r.Tag + ") es anterior a la que está usando. No hay nada que actualizar.";
            else text = "Está usando la última versión publicada (" + r.Tag + (r.Commit != null ? ", commit " + r.Commit.Substring(0, 7) : "") + ").";
            var t = Theme.Label(text, 12.5, false, state == "error" ? Theme.Darken(Theme.Red, 0.8) : Theme.Muted);
            t.Margin = new Thickness(0, 4, 0, 8);
            p.Children.Add(t);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (UpdateService.OnlineEnabled)
            {
                var check = Theme.Button(state == null ? "Buscar en GitHub" : "Buscar de nuevo en GitHub");
                check.Margin = new Thickness(0, 0, 8, 0);
                check.Click += async (s, e) => await CheckOnline();
                row.Children.Add(check);
                if (r != null && (state == "nueva" || state == "mismo_version_otro_commit") && !UpdateService.RestartPending)
                {
                    var get = Theme.Button("Descargar e instalar " + r.Tag, Theme.Kind.Primary);
                    get.Margin = new Thickness(0);
                    var sameVersion = state == "mismo_version_otro_commit";
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
            SetBusy(true, "Consultando GitHub…");
            await Task.Run(() => UpdateService.CheckOnline());
            SetBusy(false, null);
            Render();
        }

        private async Task DownloadAndInstall(OnlineRelease r, bool sameVersion)
        {
            if (_busy) return;
            SetBusy(true, "Descargando " + r.Tag + " (" + (r.SetupSize / (1024 * 1024)) + " MB) y verificando su SHA-256…");
            var d = await Task.Run(() => UpdateService.DownloadOnline(r));
            if (d.c == null)
            {
                SetBusy(false, null);
                Render();
                Banner("No se instaló nada: " + d.error, Theme.Red);
                return;
            }
            SetBusy(true, "Verificado. Instalando la versión " + d.c.VersionText + "… (unos segundos)");
            // Misma versión con otro commit: el instalador sólo lo acepta con /permitir-anterior.
            var res = await Task.Run(() => UpdateService.Install(d.c, sameVersion));
            SetBusy(false, null);
            Render();
            if (res.ok) Banner(res.text + "\nDescargada de " + d.c.Source + " y verificada. Cierre y abra Revit para usarla.", Theme.Green);
            else Banner("Se descargó y verificó, pero no se pudo instalar: " + res.text, Theme.Red);
        }

        private static Border StatusCard(Color color, string title, string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var state = color == Theme.Amber ? 2 : title.StartsWith("Hay", StringComparison.Ordinal) ? 1 : 0;
            g.Children.Add(new Image { Source = Icons.Update(state, 32), Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            var s = new StackPanel();
            s.Children.Add(Theme.Label(title, 15, true));
            var body = Theme.Label(text, 13, false, Theme.Muted);
            body.Margin = new Thickness(0, 4, 0, 0);
            s.Children.Add(body);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return Theme.Card(g);
        }

        private async Task Recheck()
        {
            if (_busy) return;
            SetBusy(true, "Buscando instaladores…");
            await Task.Run(() => UpdateService.Check());
            SetBusy(false, null);
            Render();
        }

        private async Task Install(UpdateCandidate c, bool allowOlder)
        {
            if (_busy) return;
            SetBusy(true, (allowOlder ? "Volviendo a la versión " : "Instalando la versión ") + c.VersionText + "… (unos segundos)");
            var r = await Task.Run(() => UpdateService.Install(c, allowOlder));
            SetBusy(false, null);
            Render();
            if (r.ok) Banner(r.text + "\nCierre y abra Revit para usarla.", Theme.Green);
            else Banner("No se pudo instalar: " + r.text, Theme.Red);
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
            _bannerText.Foreground = Theme.B(Theme.Darken(c, 0.85));
            _banner.Background = Theme.Tint(c, 0x1C);
            _banner.Visibility = Visibility.Visible;
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
            // Antes de mostrarse no hay árbol visual: se recorre el lógico.
            if (n == 0)
                foreach (var o in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                {
                    if (o is T t) yield return t;
                    foreach (var d in Descendants<T>(o)) yield return d;
                }
        }
    }
}
