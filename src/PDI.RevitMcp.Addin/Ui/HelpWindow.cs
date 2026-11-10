// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>Guía corta de los botones, con los mismos iconos del ribbon.</summary>
    internal static class HelpWindow
    {
        public static void Show(IntPtr owner)
        {
            var win = Theme.NewWindow("BIMBRIDGE · Cómo funciona", 600, 640);
            try { new WindowInteropHelper(win) { Owner = owner }; } catch (Exception) { }
            var root = new DockPanel();
            var header = Theme.Header("Cómo funciona", "Claude y Codex trabajan con el modelo abierto; usted decide qué se aplica.", out _, out var pill, out var pillText);
            pill.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var body = new StackPanel();
            body.Children.Add(Item(Icons.Status(Theme.Green, 32), "Estado",
                "Muestra qué IAs están conectadas, qué pueden hacer y lo que hicieron, en palabras. Verde: hay IA conectada. Gris: esperando. Rojo: en pausa."));
            body.Children.Add(Item(Icons.Pause(false, 32), "Pausar IA",
                "Corta todo al instante: mientras está en pausa, Revit rechaza cualquier petición de la IA. Púlselo de nuevo para reanudar."));
            body.Children.Add(Item(Icons.Mode(false, 32), "Modo En vivo",
                "Los cambios que usted apruebe en la conversación se aplican al momento."));
            body.Children.Add(Item(Icons.Mode(true, 32), "Modo Retener",
                "Los cambios esperan en una cola guardada en disco y el modelo no se toca. Siga trabajando tranquilo; la cola sobrevive a un cierre o un fallo de Revit."));
            body.Children.Add(Item(Icons.Queue(3, 32), "Cola",
                "Revise cada cambio (antes y después), elija cuáles aplicar o descartar. Si algo cambió mientras esperaba, ese cambio se omite y la IA se entera. Cada plan se deshace con un Ctrl+Z."));
            body.Children.Add(Item(Icons.Update(1, 32), "Versión / Actualizar",
                "Cuando le pasen un instalador nuevo, déjelo en Descargas o en el Escritorio (sin abrirlo). El botón se pone verde: púlselo y se instala sin cerrar Revit, " +
                "con sus mismas opciones; entra al reiniciar Revit. Los instaladores viejos van a la papelera y puede volver a la versión anterior."));
            body.Children.Add(Theme.Card(Theme.Label(
                "Qué puede hacer la IA, según los permisos que usted eligió al instalar: leer el modelo; proponer cambios tipados con su antes y después (plan); " +
                "ejecutar código C# sobre la API de Revit (exec, permiso W), que queda guardado y registrado en %LOCALAPPDATA%\\PDI.RevitMcp\\scripts; " +
                "editar familias y conectores; y guardar, sincronizar o exportar PDF (permiso D o W). Nada de esto queda oculto: cada operación se registra. " +
                "Pausa detiene todo lo que llegue después.", 12.5, false, Theme.Muted)));
            root.Children.Add(Theme.Scroll(body));

            var close = Theme.Button("Entendido", Theme.Kind.Primary);
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (s, e) => win.Close();
            var footer = new Border
            {
                Background = Theme.B(Theme.White), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(24, 12, 24, 14),
                Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { close } },
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Insert(1, footer);
            win.Content = root;
            win.ShowDialog();
        }

        private static UIElement Item(System.Windows.Media.ImageSource icon, string title, string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Image { Source = icon, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
            var s = new StackPanel();
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
