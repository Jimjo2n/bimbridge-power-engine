// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDI.RevitMcp.Addin.Ui;

namespace PDI.RevitMcp.Setup
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
        private readonly StackPanel _footerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _footerNote;
        private readonly Border _banner;
        private readonly TextBlock _bannerText;
        private TextBlock _subtitle;

        private readonly List<CheckBox> _versionChecks = new List<CheckBox>();
        private RadioButton _r, _rp, _rpw;
        private CheckBox _claude, _codex, _removeData;
        private readonly List<string> _log = new List<string>();

        public SetupWindow(bool uninstallMode)
        {
            _core = new SetupCore(_paths, s => _log.Add(s));
            _installed = _core.ReadInstalled();
            _uninstallMode = uninstallMode;

            Window = Theme.NewWindow(_uninstallMode ? "Desinstalar BIMBRIDGE - Power Engine" : "Instalar BIMBRIDGE - Power Engine", 640, 720);
            Window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Window.ShowInTaskbar = true;
            Window.MinHeight = 560;
            try { Window.Icon = Icons.Status(Theme.Red, 32); } catch (Exception) { }

            var root = new DockPanel();
            var header = Theme.Header(
                _uninstallMode ? "Desinstalar BIMBRIDGE - Power Engine" : _installed != null ? "Modificar BIMBRIDGE - Power Engine" : "Instalar BIMBRIDGE - Power Engine",
                "Claude y Codex trabajando con Revit, con su permiso.",
                out _subtitle, out var pill, out var pillText);
            pillText.Text = "v" + Platform.ProductVersion;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footerGrid = new Grid { Margin = new Thickness(24, 12, 24, 14) };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _footerNote = new TextBlock { FontSize = 11.5, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            footerGrid.Children.Add(_footerNote);
            Grid.SetColumn(_footerButtons, 1);
            footerGrid.Children.Add(_footerButtons);
            var footer = new Border { Background = Theme.B(Theme.White), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = footerGrid };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            _banner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 11), Margin = new Thickness(0, 0, 0, 12), Child = _bannerText, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1) };

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
            revit.Children.Add(Theme.Label("¿En qué versiones de Revit?", 15, true));
            revit.Children.Add(Hint("Se marcaron las que hay en esta computadora."));
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var r in _core.DetectRevit())
            {
                var chosen = _installed != null ? _installed.Versions.Contains(r.Version) : r.Installed;
                var cb = new CheckBox
                {
                    Content = new TextBlock { Inlines = { new System.Windows.Documents.Run("Revit " + r.Version) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run(r.Installed ? "" : "  · no encontrado") { Foreground = Theme.B(Theme.Muted) } } },
                    IsChecked = chosen && r.Installed || (_installed != null && _installed.Versions.Contains(r.Version)),
                    IsEnabled = r.Installed || (_installed != null && _installed.Versions.Contains(r.Version)),
                    Tag = r.Version,
                    Margin = new Thickness(0, 4, 22, 4),
                };
                _versionChecks.Add(cb);
                wrap.Children.Add(cb);
            }
            revit.Children.Add(wrap);
            if (!_core.DetectRevit().Any(r => r.Installed))
                revit.Children.Add(Hint("No se encontró Revit 2022–2026 en C:\\Program Files\\Autodesk. Instale Revit primero."));
            body.Children.Add(Theme.Card(revit));

            // 2. Permisos
            var perms = new StackPanel();
            perms.Children.Add(Theme.Label("¿Qué puede hacer la IA?", 15, true));
            perms.Children.Add(Hint("Se puede cambiar después volviendo a abrir este instalador."));
            var current = _installed?.Levels ?? "RPW";
            _r = Option(perms, "Sólo leer el modelo", "Consultar, contar y auditar. No puede proponer ni hacer cambios.", current == "R");
            _rp = Option(perms, "Leer y proponer cambios (todo espera en la cola)", "Los cambios nunca se aplican solos: usted los revisa y aplica desde el botón Cola del ribbon.", current == "RP");
            _rpw = Option(perms, "Leer y cambiar con su aprobación (recomendado)", "La IA le muestra el antes y el después y usted aprueba. Con el botón Modo elige si se aplica al momento o espera en la cola.", current == "RPW");
            body.Children.Add(Theme.Card(perms));

            // 3. IAs
            var ais = new StackPanel();
            ais.Children.Add(Theme.Label("¿Con qué IA la va a usar?", 15, true));
            var claudePath = Platform.FindClaude();
            _claude = AiOption(ais, "Claude Code", claudePath != null ? "Encontrado. Se conectará solo." : "No se encontró en esta computadora. Puede conectarlo después volviendo a abrir este instalador.", claudePath != null, claudePath != null);
            _codex = AiOption(ais, "Codex", _core.CodexPresent ? "Encontrado. Se añadirá a su configuración." : "No se encontró. Si lo instala más tarde, vuelva a abrir este instalador.", _core.CodexPresent || (_installed?.Codex ?? false), true);
            body.Children.Add(Theme.Card(ais));

            // Nota de seguridad
            body.Children.Add(Theme.Card(Theme.Label(
                "La primera vez que abra Revit aparecerá el aviso \"Complemento sin firmar\" (Unsigned Add-In). Elija \"Cargar siempre\" (Always Load). " +
                "Si IT firma el complemento, ese aviso desaparece.", 12.5, false, Theme.Muted)));

            _scroll.Content = body;
            _footerNote.Text = "Se instala sólo para su usuario. No pide permisos de administrador.";
            SetButtons(
                _installed != null ? Btn("Desinstalar", Theme.Kind.Danger, ShowUninstallForm) : null,
                Btn("Cancelar", Theme.Kind.Secondary, () => Window.Close()),
                Btn(_installed != null ? "Actualizar" : "Instalar", Theme.Kind.Primary, () => _ = InstallAsync()));
        }

        private async Task InstallAsync()
        {
            var versions = _versionChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (versions.Count == 0) { ShowBanner("Elija al menos una versión de Revit.", Theme.Amber); return; }
            var revitOpen = Platform.RevitRunning();
            if (revitOpen) { ShowBanner("Revit está abierto. Ciérrelo y pulse otra vez: los binarios sólo cambian con Revit cerrado.", Theme.Amber); return; }

            var levels = _r.IsChecked == true ? "R" : _rp.IsChecked == true ? "RP" : "RPW";
            var options = new InstallOptions { Versions = versions, Levels = levels, Codex = _codex.IsChecked == true, AllowInUse = false, RequireManifest = true };
            var before = _core.ReadInstalled();
            var wantClaude = _claude.IsChecked == true;
            ShowBusy("Instalando…");

            string error = null;
            string claudeResult = null;
            await Task.Run(() =>
            {
                try
                {
                    using (var zip = new ZipArchive(Platform.OpenPayload()))
                        _core.Install(zip, options, Platform.ProductVersion);
                    if (wantClaude)
                    {
                        var claude = Platform.FindClaude();
                        if (claude != null && Platform.RegisterClaude(claude, _paths.BridgeExe, out var detail)) _log.Add("Conectado con Claude Code");
                        else claudeResult = "No se pudo conectar con Claude Code automáticamente. En una consola escriba:\nclaude mcp add --scope user pdi-revit -- \"" + _paths.BridgeExe + "\"";
                    }
                    var recycled = Platform.AfterInstall(_core, _paths, before);
                    if (Platform.LastWarning != null) _log.Add("Aviso: " + Platform.LastWarning);
                    if (recycled.Count > 0) _log.Add("Instaladores viejos enviados a la papelera: " + recycled.Count);
                    if (_core.ReplacedInUse > 0 || revitOpen) _log.Add("Revit estaba abierto: cierre y abra Revit para usar esta versión");
                }
                catch (Exception ex) { error = ex.Message; }
            });

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
            steps.Children.Add(Theme.Label("Qué sigue", 15, true));
            steps.Children.Add(Step("1", "Abra Revit " + string.Join(" o ", versions) + ". Si pregunta por el complemento sin firmar, elija \"Cargar siempre\"."));
            steps.Children.Add(Step("2", "Verá la pestaña BIMBRIDGE. El botón Power Engine | Estado se pone verde cuando una IA se conecta."));
            steps.Children.Add(Step("3", "En Claude o Codex pida, por ejemplo: \"¿Cuántas luminarias hay por nivel en el modelo abierto?\""));
            body.Children.Add(Theme.Card(steps));

            var done = new StackPanel();
            done.Children.Add(Theme.Label("Lo que se hizo", 13, true));
            foreach (var l in _log) done.Children.Add(Hint("✓  " + l));
            done.Children.Add(Hint("✓  Permisos de la IA: " + SetupCore.LevelsText(levels)));
            done.Children.Add(Hint("✓  Aparece en Configuración > Aplicaciones como \"BIMBRIDGE - Power Engine\" para quitarlo cuando quiera"));
            body.Children.Add(Theme.Card(done));
            if (warning != null) { PutBanner(body, 0); ShowBanner(warning, Theme.Amber); }

            _scroll.Content = body;
            _footerNote.Text = "";
            SetButtons(Btn("Cerrar", Theme.Kind.Primary, () => Window.Close()));
        }

        // ================= Desinstalar =================

        private void ShowUninstallForm()
        {
            var body = new StackPanel();
            PutBanner(body);
            var s = new StackPanel();
            s.Children.Add(Theme.Label("¿Quitar BIMBRIDGE - Power Engine de esta computadora?", 15, true));
            s.Children.Add(Hint("Se quita el complemento de Revit, la conexión con Claude y Codex y el puente. Sus modelos no se tocan."));
            _removeData = new CheckBox { Content = "Borrar también registros, la cola de cambios pendientes y la configuración", Margin = new Thickness(0, 12, 0, 0) };
            s.Children.Add(_removeData);
            body.Children.Add(Theme.Card(s));
            _scroll.Content = body;
            _footerNote.Text = _installed != null ? "Versión instalada: " + _installed.ProductVersion : "";
            SetButtons(
                Btn("Cancelar", Theme.Kind.Secondary, () => { if (_uninstallMode) Window.Close(); else ShowInstallForm(); }),
                Btn("Desinstalar", Theme.Kind.Primary, () => _ = UninstallAsync()));
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
                    _core.Uninstall(removeData);
                    Platform.UnregisterClaude();
                    Platform.UnregisterUninstall();
                }
                catch (Exception ex) { error = ex.Message; }
            });
            if (error != null) { ShowUninstallForm(); ShowBanner(error, Theme.Red); return; }

            var body = new StackPanel();
            body.Children.Add(Hero(Theme.Slate, "Desinstalado", "BIMBRIDGE - Power Engine se quitó de esta computadora."));
            _scroll.Content = body;
            _footerNote.Text = "";
            SetButtons(Btn("Cerrar", Theme.Kind.Primary, () => { Platform.DeleteSetupCopyLater(_paths); Window.Close(); }));
        }

        // ================= piezas =================

        private void ShowBusy(string text)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 90, 0, 0) };
            s.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 260, Height = 6, Foreground = Theme.B(Theme.Navy), Background = Theme.B(Theme.Line), BorderThickness = new Thickness(0) });
            s.Children.Add(new TextBlock { Text = text, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
            _scroll.Content = s;
            _footerNote.Text = "";
            SetButtons();
            Mouse.OverrideCursor = Cursors.Wait;
            Window.Dispatcher.BeginInvoke(new Action(() => Mouse.OverrideCursor = null), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private static UIElement Hero(Color c, string title, string text)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 18) };
            var circle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Theme.B(c), HorizontalAlignment = HorizontalAlignment.Center };
            circle.Child = new TextBlock { Text = c == Theme.Green ? "✓" : "–", FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Theme.B(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) };
            s.Children.Add(circle);
            s.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
            s.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Theme.B(Theme.Muted), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            return s;
        }

        private static UIElement Step(string n, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Tint(Theme.Navy, 0x18), VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = n, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Navy), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var t = Theme.Label(text, 13.5);
            t.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        private static TextBlock Hint(string text)
        {
            var t = Theme.Label(text, 12.5, false, Theme.Muted);
            t.Margin = new Thickness(0, 3, 0, 0);
            return t;
        }

        private static RadioButton Option(Panel parent, string title, string text, bool selected)
        {
            var content = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            content.Children.Add(Theme.Label(title, 13.5, true));
            content.Children.Add(Hint(text));
            var r = new RadioButton { Content = content, GroupName = "niveles", IsChecked = selected, Margin = new Thickness(0, 10, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
            parent.Children.Add(r);
            return r;
        }

        private static CheckBox AiOption(Panel parent, string title, string text, bool isChecked, bool enabled)
        {
            var content = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            content.Children.Add(Theme.Label(title, 13.5, true));
            content.Children.Add(Hint(text));
            var c = new CheckBox { Content = content, IsChecked = isChecked, IsEnabled = enabled, Margin = new Thickness(0, 10, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
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

        private void SetButtons(params Button[] buttons)
        {
            _footerButtons.Children.Clear();
            foreach (var b in buttons.Where(x => x != null)) _footerButtons.Children.Add(b);
        }

        private void ShowBanner(string text, Color color)
        {
            _bannerText.Text = text;
            _bannerText.Foreground = Theme.B(Theme.Darken(color, 0.75));
            _banner.Background = Theme.Tint(color, 0x1E);
            _banner.BorderBrush = Theme.Tint(color, 0x55);
            _banner.Visibility = Visibility.Visible;
            _scroll.ScrollToTop();
        }
    }
}
