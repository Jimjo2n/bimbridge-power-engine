// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Estilo visual común de las ventanas: azul marino y rojo (aproximados a BIMBRIDGE, sin manual de marca),
    /// verde/ámbar para estados, Segoe UI. Todo en código (sin XAML) para compilar igual en todas las versiones.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Navy = Color.FromRgb(0x0E, 0x24, 0x47);
        public static readonly Color NavySoft = Color.FromRgb(0x24, 0x3B, 0x63);
        public static readonly Color Red = Color.FromRgb(0xC8, 0x1E, 0x2B);
        public static readonly Color Green = Color.FromRgb(0x1E, 0x8E, 0x5A);
        public static readonly Color Amber = Color.FromRgb(0xD9, 0x8A, 0x0B);
        public static readonly Color Slate = Color.FromRgb(0x6B, 0x75, 0x86);
        public static readonly Color Ink = Color.FromRgb(0x1A, 0x21, 0x2E);
        public static readonly Color Muted = Color.FromRgb(0x5C, 0x66, 0x78);
        public static readonly Color Line = Color.FromRgb(0xE1, 0xE5, 0xEC);
        public static readonly Color Page = Color.FromRgb(0xF4, 0xF6, 0xF9);
        public static readonly Color White = Colors.White;

        public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static SolidColorBrush Tint(Color c, byte alpha = 0x22) => B(Color.FromArgb(alpha, c.R, c.G, c.B));

        public static readonly FontFamily Font = new FontFamily("Segoe UI");

        public static Window NewWindow(string title, double width, double height)
        {
            return new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = 520,
                MinHeight = 360,
                FontFamily = Font,
                FontSize = 13,
                Background = B(Page),
                Foreground = B(Ink),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
            };
        }

        /// <summary>Banda superior azul con título, subtítulo y una píldora de estado a la derecha.</summary>
        public static Border Header(string title, string subtitle, out TextBlock subtitleBlock, out Border pill, out TextBlock pillText)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = "BIMBRIDGE - Power Engine", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Tint(White, 0xB0), Margin = new Thickness(0, 0, 0, 2) });
            left.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B(White) });
            subtitleBlock = new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = Tint(White, 0xD0), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            left.Children.Add(subtitleBlock);
            grid.Children.Add(left);

            pillText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(White) };
            pill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 4, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Tint(White, 0x30),
                Child = pillText,
            };
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);

            var band = new Border { Background = B(Navy), Padding = new Thickness(24, 18, 24, 18), Child = grid };
            band.BorderBrush = B(Red);
            band.BorderThickness = new Thickness(0, 0, 0, 3);
            return band;
        }

        public static Border Card(UIElement child, Thickness? padding = null) => new Border
        {
            Background = B(White),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = child,
        };

        public static Border Chip(string text, Color color)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 3),
                Background = Tint(color, 0x1F),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B(Darken(color)) },
            };
        }

        public static Color Darken(Color c, double f = 0.8) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        public enum Kind { Primary, Secondary, Danger, Quiet }

        /// <summary>Botón plano con esquinas redondeadas y estados hover/deshabilitado.</summary>
        public static Button Button(string text, Kind kind = Kind.Secondary)
        {
            Color bg, fg, border, hover;
            switch (kind)
            {
                case Kind.Primary: bg = Navy; fg = White; border = Navy; hover = NavySoft; break;
                case Kind.Danger: bg = White; fg = Red; border = Color.FromRgb(0xF0, 0xC4, 0xC7); hover = Color.FromRgb(0xFD, 0xF0, 0xF1); break;
                case Kind.Quiet: bg = Colors.Transparent; fg = NavySoft; border = Colors.Transparent; hover = Color.FromRgb(0xE9, 0xED, 0xF3); break;
                default: bg = White; fg = Ink; border = Line; hover = Color.FromRgb(0xF0, 0xF2, 0xF6); break;
            }

            var template = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, B(bg));
            bd.SetValue(Border.BorderBrushProperty, B(border));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 8));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            template.VisualTree = bd;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, B(hover), "bd"));
            template.Triggers.Add(over);
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(off);

            return new Button
            {
                Content = text,
                Template = template,
                Foreground = B(fg),
                FontWeight = kind == Kind.Primary ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 90,
            };
        }

        public static TextBlock Label(string text, double size = 13, bool bold = false, Color? color = null) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = B(color ?? Ink),
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>"claude-code" → "Claude Code", "codex-mcp-client" → "Codex".</summary>
        public static string ClientName(string raw)
        {
            var s = (raw ?? "").ToLowerInvariant();
            if (s.Contains("claude")) return "Claude";
            if (s.Contains("codex")) return "Codex";
            if (s.Contains("probe") || s.Contains("prueba")) return "Prueba";
            if (s.Length == 0) return "IA";
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        public static Color ClientColor(string raw)
        {
            var n = ClientName(raw);
            if (n == "Claude") return Color.FromRgb(0xC1, 0x5F, 0x3C);
            if (n == "Codex") return Color.FromRgb(0x10, 0x7A, 0x6E);
            return Slate;
        }

        public static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 60) return "hace un momento";
            if (d.TotalMinutes < 60) return "hace " + (int)d.TotalMinutes + " min";
            if (d.TotalHours < 24) return "hace " + (int)d.TotalHours + " h";
            return utc.ToLocalTime().ToString("dd/MM HH:mm");
        }

        public static ScrollViewer Scroll(UIElement content) => new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(24, 16, 24, 8),
        };
    }
}
