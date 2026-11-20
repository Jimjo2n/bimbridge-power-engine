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
    /// Ajustes: sólo los interruptores que ya existen y ya se guardan (Pausa y Retener como archivos de estado, bloqueo de C# en
    /// config.json). Cada cambio usa el mismo camino que su botón de la cinta y se ve al momento en la cinta y en Estado.
    /// Más una preferencia de interfaz propia del complemento (Movimiento), guardada aparte en interfaz.json.
    /// Los niveles se muestran, pero sólo los cambia el instalador (se leen al arrancar Revit).
    /// </summary>
    internal sealed class SettingsWindow
    {
        private const string Live = "En vivo", Hold = "Retener", Active = "Activa", Paused = "En pausa", Allowed = "Permitido", Blocked = "Bloqueado";
        private const string MotionAuto = "Automático (sigue a Windows)", MotionReduced = "Reducido";

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
            _win = Theme.NewWindow("BIMBRIDGE · Ajustes", 620, 700);
            try { new WindowInteropHelper(_win) { Owner = app.MainWindowHandle }; } catch (Exception) { }
            var cfg = Runtime.Config;
            var root = new DockPanel();
            var header = Theme.Header(Theme.ProductEyebrow, "Ajustes", "Los cambios se aplican al momento y se guardan.", out _, out _pill, out _pillText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var close = Theme.Button("Listo", Theme.Kind.Primary);
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (s, e) => _win.Close();
            var footer = Theme.Footer(Theme.FooterNote("Los mismos interruptores están en la cinta BIMBRIDGE."), close);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new StackPanel();
            _warn = Theme.Notice("", Theme.Amber, out _warnText);
            _warn.Visibility = Visibility.Collapsed;
            body.Children.Add(_warn);

            // ---- 1. Control de la IA ----
            var control = new StackPanel();
            control.Children.Add(Theme.Section("Control de la IA", "Qué ocurre con las peticiones que llegan ahora."));
            control.Children.Add(Row("IA", Theme.Segmented(new[] { Active, Paused }, Runtime.Pause.On ? Paused : Active, v => SetPause(v == Paused)), _pauseLine));
            control.Children.Add(Theme.Divider());
            var writes = cfg.Allows('P') && cfg.Allows('W');
            var modeSeg = Theme.Segmented(new[] { Live, Hold }, Runtime.Hold.On || !cfg.Allows('W') ? Hold : Live, v => SetHold(v == Hold));
            modeSeg.IsEnabled = writes;
            control.Children.Add(Row("Cambios aprobados", modeSeg, _modeLine));
            if (!writes) control.Children.Add(Muted("Sus permisos no incluyen aplicar cambios (W): todo espera en Cola. Se cambia con el instalador."));
            body.Children.Add(Theme.Card(control));

            // ---- 2. Qué puede hacer la IA ----
            var perms = new StackPanel();
            perms.Children.Add(Theme.Section("Qué puede hacer la IA", "Permisos elegidos al instalar."));
            var levels = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            levels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var items = new[] { ("Leer el modelo", 'R'), ("Preparar cambios (plan)", 'P'), ("Aplicar cambios", 'W'), ("Guardar, sincronizar, PDF", 'D') };
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
            perms.Children.Add(Muted("Para cambiarlos, abra el instalador y elija Modificar instalación. Se aplican al reiniciar Revit."));
            perms.Children.Add(Theme.Divider());
            _codeSeg = Theme.Segmented(new[] { Allowed, Blocked }, Runtime.BlockDynamicCode ? Blocked : Allowed, v => SetBlocked(v == Blocked));
            perms.Children.Add(Row("Código C# dinámico (exec)", _codeSeg, _codeLine));
            if (cfg.Runner == "apagado") perms.Children.Add(Muted("config.json tiene runner = apagado: exec no se ejecuta en ningún caso."));
            var why = new StackPanel();
            why.Children.Add(Muted("Por defecto la IA local autorizada puede ejecutar C# dinámico, auditado. Bloqueado: toda petición nueva de exec se rechaza; las operaciones tipadas (plan/commit) siguen disponibles."));
            why.Children.Add(Muted("No es un sandbox: el código corre en el proceso de Revit con los permisos de su usuario y sus efectos fuera del documento no se revierten."));
            why.Children.Add(Muted("El cambio queda guardado en config.json y vale para las peticiones nuevas; no detiene lo que ya está dentro de Revit."));
            var disc = Theme.Disclosure("Qué implica", why);
            disc.Margin = new Thickness(0, 8, 0, 0);
            perms.Children.Add(disc);
            body.Children.Add(Theme.Card(perms));

            // ---- 3. Interfaz ----
            var ui = new StackPanel();
            ui.Children.Add(Theme.Section("Interfaz", "Sólo para este usuario de Windows. No cambia nada de la IA."));
            ui.Children.Add(Row("Movimiento", Theme.Segmented(new[] { MotionAuto, MotionReduced }, Theme.ReduceMotionOverride ? MotionReduced : MotionAuto, v => SetMotion(v == MotionReduced)), _motionLine));
            body.Children.Add(Theme.Card(ui));

            root.Children.Add(Theme.Scroll(body));
            _win.Content = root;
            Describe(false);
        }

        // ---- cambios: el mismo camino que los botones de la cinta ----

        private void SetPause(bool on)
        {
            if (_syncing || Runtime.Pause.On == on) return;
            Runtime.TogglePause();
            Saved(Runtime.Pause.Unsaved, "la pausa");
        }

        private void SetHold(bool on)
        {
            if (_syncing || Runtime.Hold.On == on) return;
            Runtime.ToggleHold();
            Saved(Runtime.Hold.Unsaved, "el modo");
        }

        private void SetBlocked(bool on)
        {
            if (_syncing || Runtime.BlockDynamicCode == on) return;
            // Permitir amplía lo que la IA puede hacer: la misma confirmación que la cinta, "No" por defecto.
            if (!on && !Runtime.ConfirmAllowDynamicCode()) { Select(_codeSeg, Blocked); return; }
            if (!Runtime.ToggleBlockDynamicCode(out var error))
            {
                Select(_codeSeg, Runtime.BlockDynamicCode ? Blocked : Allowed);
                ShowWarn("No se pudo guardar en config.json, así que no se aplicó. " + error);
                return;
            }
            Changed();
        }

        /// <summary>Preferencia propia del complemento (no config.json). Se aplica al momento, sin reiniciar.</summary>
        private void SetMotion(bool reduced)
        {
            if (_syncing || Theme.ReduceMotionOverride == reduced) return;
            if (!Runtime.SetReducedMotion(reduced, out var error)) ShowWarn("No se pudo guardar la preferencia de movimiento: vale hasta cerrar Revit. " + error);
            Describe(true);
        }

        private void Saved(bool unsaved, string what)
        {
            if (unsaved) ShowWarn("No se pudo guardar " + what + " en disco: vale hasta cerrar Revit.");
            Changed();
        }

        private void Changed()
        {
            Runtime.MarkDirty();
            try { Runtime.OnIdling(_app); } catch (Exception ex) { Runtime.Log("Ajustes: " + ex.Message); }
            Describe(true);
        }

        private void ShowWarn(string text)
        {
            _warnText.Text = text;
            _warn.Visibility = Visibility.Visible;
            Theme.Enter(_warn, 4, 200);
        }

        /// <summary>Consecuencia de cada ajuste en una línea y píldora con el estado efectivo. Al cambiar, sólo entra la línea que cambió.</summary>
        private void Describe(bool animate)
        {
            var cfg = Runtime.Config;
            var paused = Runtime.Pause.On;
            var hold = Runtime.Hold.On || !cfg.Allows('W');
            Set(_pauseLine, paused ? "Revit rechaza toda petición de la IA, también las lecturas." : "La IA puede leer el modelo y pedir cambios.", animate);
            Set(_modeLine, hold ? "Los cambios esperan en Cola; el modelo no se toca hasta que usted los aplique." : "Lo que usted apruebe en la conversación se aplica al momento.", animate);
            Set(_codeLine, Runtime.BlockDynamicCode ? "La IA no podrá ejecutar C#; plan/commit siguen disponibles." : "La IA podrá ejecutar C# sobre la API de Revit, auditado.", animate);
            Set(_motionLine, Theme.ReduceMotionOverride ? "Sin animaciones en las ventanas ni en la cinta; todo responde igual de rápido."
                : Theme.WindowsReducedMotion ? "Windows tiene las animaciones desactivadas: sin animaciones." : "Animaciones breves sólo en cambios de estado ocasionales.", animate);
            if (paused) Theme.SetPill(_pill, _pillText, Theme.Red, "IA en pausa", true);
            else if (hold) Theme.SetPill(_pill, _pillText, Theme.Amber, "Retener", true);
            else Theme.SetPill(_pill, _pillText, Theme.Green, "En vivo", true);
        }

        private static void Set(TextBlock t, string text, bool animate)
        {
            if (t.Text == text) return;
            t.Text = text;
            if (animate && !Theme.FromKeyboard) Theme.Enter(t, 4, 180); // flechas en los segmentos: al instante
        }

        private void Select(StackPanel seg, string value)
        {
            _syncing = true;
            try { foreach (var r in seg.Children.OfType<RadioButton>()) r.IsChecked = (string)r.Content == value; }
            finally { _syncing = false; }
        }

        // ---- piezas ----

        private static TextBlock Line() => new TextBlock { FontSize = 12.5, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        private static TextBlock Muted(string text)
        {
            var t = Theme.Label(text, 12.5, false, Theme.Muted);
            t.Margin = new Thickness(0, 6, 0, 0);
            return t;
        }

        /// <summary>Etiqueta a la izquierda, segmentos a la derecha y la consecuencia debajo.</summary>
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

        /// <summary>Icono de la cinta: engranaje marino con centro blanco, en el lienzo de 32×32 de <see cref="Icons"/>.</summary>
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
