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
    /// Ventana única del instalador: elegir versiones de Revit, qué puede hacer la IA y con qué IAs conectarse;
    /// luego instalar y ver el resultado. También sirve para modificar y desinstalar.
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

            Window = Theme.NewWindow(_uninstallMode ? "Desinstalar BIMBRIDGE - Power Engine" : "Instalar BIMBRIDGE - Power Engine", 640, 720);
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
                _uninstallMode ? "Desinstalar" : _installed != null ? "Modificar instalación" : "Instalar",
                "Claude y Codex trabajando con Revit, con su permiso.",
                out _subtitle, out var pill, out var pillText);
            pillText.Text = "v" + Platform.ProductVersion;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Pie: enlace discreto a la izquierda (p. ej. Desinstalar), nota, y acciones a la derecha.
            var left = new DockPanel();
            DockPanel.SetDock(_footerLink, Dock.Left);
            left.Children.Add(_footerLink);
            left.Children.Add(_footerNote);
            _footer = Theme.Footer(left, _footerButtons);
            DockPanel.SetDock(_footer, Dock.Bottom);
            root.Children.Add(_footer);

            _banner = Theme.Notice("", Theme.NavySoft, out _bannerText);
            _banner.Visibility = Visibility.Collapsed;

            _scroll = Theme.Scroll(null);
            root.Children.Add(_scroll);
            Window.Content = root;

            if (_uninstallMode) ShowUninstallForm();
            else ShowInstallForm();
        }

        // ================= Instalar / modificar =================

        /// <summary>El aviso se reutiliza entre pantallas: hay que soltarlo de la anterior antes de ponerlo en la nueva.</summary>
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
                ShowBanner("Ya está instalada la versión " + _installed.ProductVersion + ". Puede cambiar las opciones y pulsar Actualizar.", Theme.NavySoft);

            // 1. Revit
            var revit = new StackPanel();
            revit.Children.Add(Theme.Section("¿En qué versiones de Revit?", "Se marcaron las que hay en esta computadora.", "1"));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 4, -8, 0) };
            foreach (var r in _core.DetectRevit())
            {
                var chosen = _installed != null ? _installed.Versions.Contains(r.Version) : r.Installed;
                var withBimbridge = _installed != null && _installed.Versions.Contains(r.Version);
                var hint = withBimbridge ? (r.Installed ? "Con BIMBRIDGE" : "Con BIMBRIDGE · Revit no encontrado")
                                         : r.Installed ? "Revit encontrado" : "No encontrado";
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
                var none = Theme.Notice("No se encontró Revit 2022–2027 en C:\\Program Files\\Autodesk. Instale Revit primero.", Theme.Amber, out _);
                none.Margin = new Thickness(0, 12, 0, 0);
                revit.Children.Add(none);
            }
            body.Children.Add(Theme.Card(revit));

            // 2. Permisos
            var perms = new StackPanel();
            perms.Children.Add(Theme.Section("¿Qué puede hacer la IA?", "Se puede cambiar después volviendo a abrir este instalador.", "2"));
            var current = _installed?.Levels ?? "RPW";
            _r = Option(perms, "Sólo leer el modelo", "Consultar, contar y auditar. No puede proponer ni hacer cambios.", current == "R");
            _rp = Option(perms, "Leer y proponer cambios (todo espera en la cola)", "Los cambios nunca se aplican solos: usted los revisa y aplica desde el botón Cola del ribbon.", current == "RP");
            _rpw = Option(perms, "Leer y cambiar con su aprobación (recomendado)", "La IA le muestra el antes y el después y usted aprueba. Con el botón Modo elige si se aplica al momento o espera en la cola.", current == "RPW");
            body.Children.Add(Theme.Card(perms));

            // 3. IAs
            var ais = new StackPanel();
            ais.Children.Add(Theme.Section("¿Con qué IA la va a usar?", "Marque las que usa. Puede añadir otra después volviendo a abrir este instalador.", "3"));
            var claudePath = Platform.FindClaude();
            _claude = AiOption(ais, "Claude Code", claudePath != null ? "Encontrado. Se conectará solo." : "No se encontró en esta computadora. Puede conectarlo después volviendo a abrir este instalador.", claudePath != null, claudePath != null);
            _codex = AiOption(ais, "Codex", _core.CodexPresent ? "Encontrado. Se añadirá a su configuración." : "No se encontró. Si lo instala más tarde, vuelva a abrir este instalador.", _core.CodexPresent || (_installed?.Codex ?? false), true);
            body.Children.Add(Theme.Card(ais));

            // Nota de seguridad
            var note = Theme.Notice(
                "La primera vez que abra Revit aparecerá el aviso \"Complemento sin firmar\" (Unsigned Add-In). Elija \"Cargar siempre\" (Always Load). " +
                "Si IT firma el complemento, ese aviso desaparece.", Theme.Slate, out var noteText);
            noteText.FontSize = 12.5;
            body.Children.Add(note);

            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter(
                "Se instala sólo para su usuario. No pide permisos de administrador.",
                _installed != null ? Link("Desinstalar", ShowUninstallForm) : null,
                Btn("Cancelar", Theme.Kind.Secondary, () => Window.Close()),
                Btn(_installed != null ? "Actualizar" : "Instalar", Theme.Kind.Primary, () => _ = InstallAsync()));
        }

        private async Task InstallAsync()
        {
            var versions = _versionChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (versions.Count == 0) { ShowBanner("Elija al menos una versión de Revit.", Theme.Amber); return; }
            var revitOpen = Platform.RevitRunning();

            var levels = _r.IsChecked == true ? "R" : _rp.IsChecked == true ? "RP" : "RPW";
            var options = new InstallOptions { Versions = versions, Levels = levels, Codex = _codex.IsChecked == true, AllowInUse = true, RequireManifest = true };
            var before = _core.ReadInstalled();
            var wantClaude = _claude.IsChecked == true;
            using var waiting = new CancellationTokenSource();
            _installationWait = waiting;
            if (revitOpen) ShowWaitingForRevit();
            else ShowBusy("Instalando…");
            SetFooter("", null, Btn("Cancelar", Theme.Kind.Secondary, () => Window.Close()));

            string error = null;
            string claudeResult = null;
            await Task.Run(() =>
            {
                try
                {
                    using var installation = Platform.LockInstallation(_paths);
                    if (!Platform.WaitForRevitExit(waiting.Token)) throw new InvalidOperationException("La espera terminó con Revit abierto. Puede volver a intentar la instalación.");
                    Window.Dispatcher.Invoke(() =>
                    {
                        waiting.Token.ThrowIfCancellationRequested();
                        _applyingInstallation = true;
                        ShowBusy("Instalando…");
                    });
                    Platform.MigrateLegacyData(_paths, _log.Add);
                    using (var zip = new ZipArchive(Platform.OpenPayload()))
                        _core.Install(zip, options, Platform.ProductVersion);
                    Platform.RemoveLegacyInstall(_paths, _log.Add, true);
                    if (wantClaude)
                    {
                        var claude = Platform.FindClaude();
                        if (claude != null && Platform.RegisterClaude(claude, _paths.BridgeExe, out var detail)) _log.Add("Conectado con Claude Code");
                        else claudeResult = "No se pudo conectar con Claude Code automáticamente. En una consola escriba:\nclaude mcp add --scope user bimbridge -- \"" + _paths.BridgeExe + "\"";
                    }
                    var recycled = Platform.AfterInstall(_core, _paths, before);
                    if (Platform.LastWarning != null) _log.Add("Aviso: " + Platform.LastWarning);
                    if (recycled.Count > 0) _log.Add("Instaladores viejos enviados a la papelera: " + recycled.Count);
                    if (_core.ReplacedInUse > 0) _log.Add("Reinicie las conversaciones que usaban BIMBRIDGE para cargar el puente nuevo.");
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
            body.Children.Add(Hero(Theme.Green, "Listo", "BIMBRIDGE - Power Engine quedó instalado."));
            var steps = new StackPanel();
            steps.Children.Add(Theme.Section("Qué sigue"));
            steps.Children.Add(Step("1", OpenRevitText(versions) + ". Si pregunta por el complemento sin firmar, elija \"Cargar siempre\"."));
            steps.Children.Add(Step("2", "Verá la pestaña BIMBRIDGE. El botón Power Engine | Estado se pone verde cuando una IA se conecta."));
            steps.Children.Add(Step("3", "En Claude o Codex pida, por ejemplo: \"¿Cuántas luminarias hay por nivel en el modelo abierto?\""));
            body.Children.Add(Theme.Card(steps));

            var done = new StackPanel();
            done.Children.Add(Theme.Section("Lo que se hizo"));
            foreach (var l in _log) done.Children.Add(Did(l));
            done.Children.Add(Did("Permisos de la IA: " + SetupCore.LevelsText(levels)));
            done.Children.Add(Did("Aparece en Configuración > Aplicaciones como \"BIMBRIDGE - Power Engine\" para quitarlo cuando quiera"));
            body.Children.Add(Theme.Card(done));
            if (warning != null) { PutBanner(body, 0); ShowBanner(warning, Theme.Amber); }

            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter("", null, Btn("Cerrar", Theme.Kind.Primary, () => Window.Close()));
        }

        /// <summary>"Abra Revit 2024", "Abra Revit 2024 o 2025", "Abra Revit 2022, 2024 o 2026", "Abra cualquier Revit entre 2022 y 2026".</summary>
        private static string OpenRevitText(List<string> versions)
        {
            var v = versions.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(); // años de 4 cifras: orden de texto = orden numérico
            if (v.Count == 1) return "Abra Revit " + v[0];
            if (v.Count >= 3 && int.TryParse(v[0], out var first) && int.TryParse(v[v.Count - 1], out var last) && v.All(s => int.TryParse(s, out _)) && last - first == v.Count - 1)
                return "Abra cualquier Revit entre " + first + " y " + last;
            return "Abra Revit " + string.Join(", ", v.Take(v.Count - 1)) + " o " + v[v.Count - 1];
        }

        // ================= Desinstalar =================

        private void ShowUninstallForm()
        {
            var body = new StackPanel();
            PutBanner(body);
            var s = new StackPanel();
            s.Children.Add(Theme.Section("¿Quitar BIMBRIDGE - Power Engine de esta computadora?", "Se quita el complemento de Revit, la conexión con Claude y Codex y el puente. Sus modelos no se tocan."));
            _removeData = Theme.Choice(new CheckBox(), "Borrar también mis datos", "Registros, la cola de cambios pendientes y la configuración.");
            _removeData.Margin = new Thickness(0, 12, 0, 0);
            s.Children.Add(_removeData);
            body.Children.Add(Theme.Card(s));
            _scroll.Content = body;
            Theme.Enter(body);
            // Intro y Esc vuelven atrás: la acción destructiva sólo se confirma con un clic o Tab + Espacio sobre su botón.
            var cancel = Btn("Cancelar", Theme.Kind.Secondary, () => { if (_uninstallMode) Window.Close(); else ShowInstallForm(); });
            cancel.IsDefault = true;
            cancel.IsCancel = true;
            SetFooter(
                _installed != null ? "Versión instalada: " + _installed.ProductVersion : "",
                null,
                cancel,
                Btn("Desinstalar", Theme.Kind.DangerSolid, () => _ = UninstallAsync()));
        }

        private async Task UninstallAsync()
        {
            if (Platform.RevitRunning()) { ShowBanner("Revit está abierto. Ciérrelo y pulse Desinstalar otra vez.", Theme.Amber); return; }
            var removeData = _removeData.IsChecked == true;
            ShowBusy("Desinstalando…");
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
            body.Children.Add(Hero(Theme.Slate, "Desinstalado", "BIMBRIDGE - Power Engine se quitó de esta computadora."));
            _scroll.Content = body;
            Theme.Enter(body);
            SetFooter("", null, Btn("Cerrar", Theme.Kind.Primary, () => { Platform.DeleteSetupCopyLater(_paths); Window.Close(); }));
        }

        // ================= piezas =================

        /// <summary>Trabajo en curso: frase de estado y barra de progreso. El pie queda oculto hasta que alguien ponga botones.</summary>
        private void ShowBusy(string text)
        {
            var s = BusyPanel(text);
            var bar = Theme.ProgressLine(!Theme.ReducedMotion); // movimiento reducido: sin animación indeterminada; la frase y el cursor de espera siguen
            bar.Width = 240;
            bar.Margin = new Thickness(0, 16, 0, 0);
            s.Children.Add(bar);
            ShowBusyPanel(s);
        }

        /// <summary>Esperando a que el usuario cierre Revit: no hay progreso que medir, así que aviso ámbar en vez de barra.</summary>
        private void ShowWaitingForRevit()
        {
            var s = BusyPanel("Esperando a que se cierre Revit");
            var notice = Theme.Notice("Instalación pendiente. Guarde su trabajo y cierre todas las ventanas de Revit. Deje abierto este instalador.", Theme.Amber, out _);
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

        /// <summary>Fila de "Lo que se hizo": marca a la izquierda y texto con sangría francesa (las líneas largas no pasan bajo la marca).</summary>
        private static UIElement Did(string text)
        {
            var warning = text.StartsWith("Aviso: ", StringComparison.Ordinal);
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
            var r = Theme.Choice(new RadioButton { GroupName = "niveles", IsChecked = selected }, title, text);
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

        /// <summary>Acción secundaria con aspecto de enlace, alineada con el margen izquierdo del pie.</summary>
        private static Button Link(string text, Action click)
        {
            var b = Btn(text, Theme.Kind.Quiet, click);
            b.MinWidth = 0;
            b.Margin = new Thickness(-16, 0, 8, 0);
            return b;
        }

        /// <summary>Pie de cada pantalla. Sin nota, enlace ni botones se oculta (p. ej. mientras se desinstala).</summary>
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
