// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using TextElement = System.Windows.Documents.TextElement;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Sistema visual común del instalador y de las ventanas del complemento (todo en código, sin XAML, para compilar igual en
    /// net48, net8 y net10). Superficies claras y tranquilas, azul marino como color de acción y rojo sólo como acento de marca;
    /// verde, ámbar y rojo para estados. Movimiento mínimo y con propósito: respuesta al pulsar, entrada de pantallas que cambian
    /// de contenido (ocasional) y nada en lo que se repite muchas veces al día. Respeta "reducir animaciones" de Windows.
    /// </summary>
    internal static class Theme
    {
        // ---------------- Color ----------------
        public static readonly Color Navy = Color.FromRgb(0x0E, 0x24, 0x47);
        public static readonly Color NavySoft = Color.FromRgb(0x24, 0x3B, 0x63);
        public static readonly Color Red = Color.FromRgb(0xC8, 0x1E, 0x2B);
        public static readonly Color Green = Color.FromRgb(0x16, 0x83, 0x52);
        public static readonly Color Amber = Color.FromRgb(0xB9, 0x70, 0x08);
        public static readonly Color Slate = Color.FromRgb(0x64, 0x6E, 0x80);
        public static readonly Color Ink = Color.FromRgb(0x14, 0x1A, 0x26);
        public static readonly Color Muted = Color.FromRgb(0x5B, 0x65, 0x76);
        public static readonly Color Line = Color.FromRgb(0xE3, 0xE7, 0xEE);
        public static readonly Color Page = Color.FromRgb(0xF5, 0xF6, 0xF8);
        public static readonly Color White = Colors.White;
        /// <summary>Superficie hundida (zonas de lectura, código, fondos de fila al pasar el ratón).</summary>
        public static readonly Color Sunken = Color.FromRgb(0xEE, 0xF1, 0xF5);

        public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static SolidColorBrush Tint(Color c, byte alpha = 0x22) => B(Color.FromArgb(alpha, c.R, c.G, c.B));
        public static Color Darken(Color c, double f = 0.8) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        // ---------------- Tipografía y medidas (rejilla de 4/8) ----------------
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        public const double Gutter = 24;
        public const double Radius = 10;
        /// <summary>Radio de botones, avisos, zonas hundidas y segmentos.</summary>
        public const double RadiusSmall = 8;
        /// <summary>Separador único del nombre del producto en cabeceras.</summary>
        public const string ProductEyebrow = "BIMBRIDGE · Power Engine";

        // ---------------- Movimiento ----------------
        /// <summary>Windows con animaciones desactivadas o alto contraste: sin desplazamientos (las respuestas siguen siendo inmediatas).</summary>
        public static bool ReducedMotion => ReduceMotionOverride || WindowsReducedMotion;

        public static bool WindowsReducedMotion
        {
            get { try { return !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast; } catch (Exception) { return false; } }
        }

        /// <summary>Preferencia «Movimiento: Reducido» del complemento (Ajustes). Se suma a la de Windows; nunca la anula.</summary>
        public static bool ReduceMotionOverride;

        /// <summary>El último cambio vino del teclado: se responde al instante, sin animar.</summary>
        public static bool FromKeyboard => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

        // ease-out fuerte (cubic-bezier(0.23, 1, 0.32, 1)): arranca rápido, la interfaz responde en el primer cuadro.
        private static readonly KeySpline EaseOutSpline = Frozen(new KeySpline(0.23, 1, 0.32, 1));
        private static KeySpline Frozen(KeySpline k) { k.Freeze(); return k; }

        private static AnimationTimeline To(double to, int ms) => new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EaseOutSpline) },
        };

        /// <summary>
        /// Entrada de una pantalla nueva (cambio de paso, resultado): opacidad y 8 px hacia arriba, 220 ms ease-out.
        /// Ocasional, por eso se anima; con movimiento reducido sólo aparece.
        /// </summary>
        public static void Enter(UIElement e, double dy = 8, int ms = 220)
        {
            if (e == null || ReducedMotion) return;
            var t = new TranslateTransform(0, dy);
            e.RenderTransform = t;
            e.Opacity = 0;
            e.BeginAnimation(UIElement.OpacityProperty, To(1, ms));
            t.BeginAnimation(TranslateTransform.YProperty, To(0, ms));
        }

        /// <summary>
        /// Respuesta al pulsar: escala (0.97 botones, 0.99 superficies grandes) en 100 ms al bajar, vuelta en 160 ms al soltar o salir.
        /// Nunca bloquea el clic.
        /// </summary>
        public static void Pressable(FrameworkElement e, double scale = 0.97)
        {
            var s = new ScaleTransform(1, 1);
            e.RenderTransform = s;
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            void Go(double v, int ms)
            {
                if (ReducedMotion) { s.ScaleX = s.ScaleY = 1; return; }
                s.BeginAnimation(ScaleTransform.ScaleXProperty, To(v, ms));
                s.BeginAnimation(ScaleTransform.ScaleYProperty, To(v, ms));
            }
            e.PreviewMouseLeftButtonDown += (_, __) => Go(scale, 100);
            e.PreviewMouseLeftButtonUp += (_, __) => Go(1, 160);
            e.MouseLeave += (_, __) => Go(1, 160);
        }

        // ---------------- Foco de teclado ----------------
        private static readonly System.Collections.Generic.Dictionary<double, Style> _focus = new System.Collections.Generic.Dictionary<double, Style>();
        /// <summary>Anillo de foco para controles de 8 px de radio (botones, segmentos). Ver <see cref="FocusRingFor"/>.</summary>
        public static Style FocusRing => FocusRingFor(RadiusSmall);

        /// <summary>
        /// Anillo de foco visible (2 px marino, 3 px fuera del control) concéntrico con un control de radio <paramref name="radius"/>:
        /// el anillo usa radius + 3 para que la separación sea igual en las esquinas.
        /// </summary>
        public static Style FocusRingFor(double radius)
        {
            if (_focus.TryGetValue(radius, out var cached)) return cached;
            var t = new ControlTemplate();
            var r = new FrameworkElementFactory(typeof(Rectangle));
            r.SetValue(Shape.StrokeProperty, B(Navy));
            r.SetValue(Shape.StrokeThicknessProperty, 2.0);
            r.SetValue(Rectangle.RadiusXProperty, radius + 3);
            r.SetValue(Rectangle.RadiusYProperty, radius + 3);
            r.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
            r.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            t.VisualTree = r;
            var s = new Style();
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            s.Seal();
            return _focus[radius] = s;
        }

        // ---------------- Ventana y estructura ----------------
        public static Window NewWindow(string title, double width, double height)
        {
            var w = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = 520,
                MinHeight = 360,
                FontFamily = Font,
                FontSize = 13.5,
                Background = B(Page),
                Foreground = B(Ink),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            return w;
        }

        /// <summary>Marca: cuadro marino redondeado con el puente (dos pilares y el tablero rojo).</summary>
        public static UIElement BrandMark(double size = 36)
        {
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Border { CornerRadius = new CornerRadius(size * 0.26), Background = B(Navy) });
            var c = new Canvas { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new ScaleTransform(size / 36.0, size / 36.0), RenderTransformOrigin = new Point(0.5, 0.5) };
            void Rect(double x, double y, double w, double h, Color col)
            {
                var r = new Rectangle { Width = w, Height = h, Fill = B(col), RadiusX = 1, RadiusY = 1 };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
            }
            Rect(1, 6, 18, 3.2, Red);           // tablero
            Rect(3.5, 9.2, 2.6, 9, White);      // pilar
            Rect(13.9, 9.2, 2.6, 9, White);     // pilar
            Rect(1, 2, 18, 1.6, Tint(White, 0x88).Color);
            g.Children.Add(c);
            return g;
        }

        /// <summary>
        /// Cabecera con el producto como antetítulo (se omite si el título ya nombra BIMBRIDGE, para no repetirlo).
        /// </summary>
        public static Border Header(string title, string subtitle, out TextBlock subtitleBlock, out Border pill, out TextBlock pillText)
            => Header((title ?? "").IndexOf("BIMBRIDGE", StringComparison.OrdinalIgnoreCase) >= 0 ? null : ProductEyebrow, title, subtitle, out subtitleBlock, out pill, out pillText);

        /// <summary>
        /// Cabecera: superficie blanca con la marca, antetítulo opcional en pequeño (null = sin antetítulo), título y subtítulo;
        /// píldora de estado a la derecha. Una línea fina la separa del contenido (sin banda de color que compita con el contenido).
        /// </summary>
        public static Border Header(string eyebrow, string title, string subtitle, out TextBlock subtitleBlock, out Border pill, out TextBlock pillText)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = BrandMark(40);
            ((FrameworkElement)mark).VerticalAlignment = VerticalAlignment.Center;
            ((FrameworkElement)mark).Margin = new Thickness(0, 0, 14, 0);
            grid.Children.Add(mark);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(eyebrow))
                left.Children.Add(new TextBlock { Text = eyebrow, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B(Muted), Margin = new Thickness(0, 0, 0, 1) });
            left.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), TextTrimming = TextTrimming.CharacterEllipsis });
            subtitleBlock = new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = B(Muted), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            left.Children.Add(subtitleBlock);
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            pillText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(NavySoft) };
            pill = new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(11, 4, 11, 5),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = B(Sunken),
                Child = pillText,
            };
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);

            return new Border
            {
                Background = B(White),
                Padding = new Thickness(Gutter, 18, Gutter, 16),
                BorderBrush = B(Line),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid,
            };
        }

        private static readonly DropShadowEffect CardShadow = FrozenShadow();
        private static DropShadowEffect FrozenShadow()
        {
            var s = new DropShadowEffect { Color = Color.FromRgb(0x14, 0x1A, 0x26), Opacity = 0.06, BlurRadius = 14, ShadowDepth = 2, Direction = 270, RenderingBias = RenderingBias.Performance };
            s.Freeze();
            return s;
        }

        /// <summary>Tarjeta: superficie blanca, borde casi invisible y sombra muy suave. El contenido va en un hijo para que la sombra no lo difumine.</summary>
        public static Border Card(UIElement child, Thickness? padding = null)
        {
            var inner = new Border { Padding = padding ?? new Thickness(18, 16, 18, 16), Child = child };
            return new Border
            {
                Background = B(White),
                BorderBrush = B(Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Radius),
                Margin = new Thickness(0, 0, 0, 12),
                Effect = CardShadow,
                Child = inner,
            };
        }

        /// <summary>Título de sección dentro de una tarjeta, con número opcional de paso (1, 2, 3…).</summary>
        public static UIElement Section(string title, string hint = null, string step = null)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (step != null)
            {
                var badge = StepBadge(step);
                badge.Margin = new Thickness(0, 0, 10, 0);
                g.Children.Add(badge);
            }
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(hint)) s.Children.Add(new TextBlock { Text = hint, FontSize = 12.5, Foreground = B(Muted), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return g;
        }

        /// <summary>Número de paso (1, 2, 3…): círculo de 22 px teñido de marino. Único estilo para secciones y listas de pasos.</summary>
        public static Border StepBadge(string n) => new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Tint(Navy, 0x18),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = n, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B(Navy), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        public static Border Chip(string text, Color color)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(9, 2, 9, 3),
                Background = Tint(color, 0x1C),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B(Darken(color, 0.78)) },
            };
        }

        /// <summary>Punto de estado (vivo/pausa/error) para filas y cabeceras.</summary>
        public static Ellipse Dot(Color color, double size = 8) => new Ellipse { Width = size, Height = size, Fill = B(color), VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Aviso en línea con barra de color a la izquierda (informativo, advertencia o error).</summary>
        public static Border Notice(string text, Color color, out TextBlock textBlock)
        {
            textBlock = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            var b = new Border
            {
                CornerRadius = new CornerRadius(RadiusSmall),
                Padding = new Thickness(14, 10, 14, 11),
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Child = textBlock,
            };
            SetNotice(b, textBlock, color);
            return b;
        }

        /// <summary>Recolorea un aviso creado con <see cref="Notice"/> (progreso, éxito, error) sin rehacerlo.</summary>
        public static void SetNotice(Border border, TextBlock textBlock, Color color)
        {
            textBlock.Foreground = B(Darken(color, 0.72));
            border.Background = Tint(color, 0x16);
            border.BorderBrush = Tint(color, 0x80);
        }

        /// <summary>
        /// Píldora de estado (la de <see cref="Header"/>): color + palabras, con punto opcional delante. Se puede llamar muchas veces;
        /// no anima (cambia a menudo).
        /// </summary>
        public static void SetPill(Border pill, TextBlock pillText, Color color, string text, bool dot = false)
        {
            pillText.Text = text;
            pillText.Foreground = B(Darken(color, 0.78));
            pill.Background = Tint(color, 0x1C);
            var row = pill.Child as StackPanel;
            if (dot && row == null)
            {
                pill.Child = null;
                (pillText.Parent as Panel)?.Children.Remove(pillText);
                row = new StackPanel { Orientation = Orientation.Horizontal };
                var d = Dot(color, 7);
                d.Margin = new Thickness(0, 1, 6, 0);
                row.Children.Add(d);
                row.Children.Add(pillText);
                pill.Child = row;
            }
            if (row != null && row.Children.Count > 0 && row.Children[0] is Ellipse e)
            {
                e.Fill = B(color);
                e.Visibility = dot ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Marca de resultado vectorial: visto (hecho) o guion (sin cambios / omitido), sin fondo.</summary>
        public static FrameworkElement ResultMark(Color color, bool check = true, double size = 14)
        {
            var p = new Path
            {
                Data = Geometry.Parse(check ? "M 2.5,7.5 L 5.75,10.75 L 11.5,3.75" : "M 3.5,7 L 10.5,7"),
                Stroke = B(color), StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = 14, Height = 14,
            };
            return new Viewbox { Width = size, Height = size, Child = p, VerticalAlignment = VerticalAlignment.Top };
        }

        /// <summary>Línea divisoria fina entre grupos de una tarjeta.</summary>
        public static Border Divider() => new Border { Height = 1, Background = B(Line), Margin = new Thickness(0, 12, 0, 12), SnapsToDevicePixels = true };

        /// <summary>Cifras alineadas (tabulares) para que los números que cambian no bailen.</summary>
        public static TextBlock Tabular(TextBlock t)
        {
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        /// <summary>Dato destacado: cifra grande con cifras tabulares, etiqueta y subetiqueta opcional.</summary>
        public static StackPanel Stat(string value, string label, string sublabel = null) => Stat(value, label, sublabel, out _);

        public static StackPanel Stat(string value, string label, string sublabel, out TextBlock valueBlock)
        {
            var s = new StackPanel();
            valueBlock = Tabular(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = B(Ink) });
            s.Children.Add(valueBlock);
            s.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(sublabel)) s.Children.Add(new TextBlock { Text = sublabel, FontSize = 11.5, Foreground = B(Muted), Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
            return s;
        }

        /// <summary>Zona de lectura hundida (rutas, comandos, detalles técnicos).</summary>
        public static Border Inset(UIElement child) => new Border
        {
            Background = B(Sunken),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusSmall),
            Padding = new Thickness(12, 8, 12, 8),
            Child = child,
        };

        /// <summary>Barra de progreso fina (4 px, marino sobre hundido). Indeterminada sólo mientras de verdad se trabaja.</summary>
        public static ProgressBar ProgressLine(bool indeterminate = true) => new ProgressBar
        {
            IsIndeterminate = indeterminate,
            Height = 4,
            Foreground = B(Navy),
            Background = B(Sunken),
            BorderThickness = new Thickness(0),
        };

        /// <summary>Nota del pie (izquierda de <see cref="Footer"/>): pequeña, apagada, ajusta líneas.</summary>
        public static TextBlock FooterNote(string text = "") => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };

        /// <summary>
        /// Filtro segmentado sobre RadioButtons reales (Tab entra una vez, flechas mueven, Espacio elige). Elegido: fondo hundido y
        /// texto Ink seminegrita; el resto, discreto. Sin animación (se usa muchas veces). Construirlo una vez y no rehacerlo al
        /// refrescar, para no perder el foco. <paramref name="onSelect"/> recibe el texto elegido (no se llama al crear).
        /// </summary>
        public static StackPanel Segmented(string[] options, string selected, Action<string> onSelect)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            var group = "seg" + Guid.NewGuid().ToString("N");

            var t = new ControlTemplate(typeof(RadioButton));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter), "cp");
            cp.SetValue(TextElement.ForegroundProperty, B(Muted));
            cp.SetValue(TextElement.FontWeightProperty, FontWeights.Normal);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(TextElement.ForegroundProperty, B(Ink), "cp"));
            t.Triggers.Add(over);
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, B(Sunken), "bd"));
            on.Setters.Add(new Setter(TextElement.ForegroundProperty, B(Ink), "cp"));
            on.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold, "cp"));
            t.Triggers.Add(on);
            t.Seal();

            foreach (var o in options ?? new string[0])
            {
                var r = new RadioButton
                {
                    Content = o, GroupName = group, IsChecked = o == selected, Template = t,
                    Cursor = Cursors.Hand, FocusVisualStyle = FocusRing, Margin = new Thickness(0, 0, 4, 0),
                };
                AutomationProperties.SetName(r, o);
                var value = o;
                r.Checked += (_, __) => onSelect?.Invoke(value);
                panel.Children.Add(r);
            }
            return panel;
        }

        /// <summary>
        /// Detalle plegable ligero (divulgación progresiva): cabecera con chevron sobre un ToggleButton real y contenido debajo.
        /// Al abrir, el contenido entra con <see cref="Enter"/> (opacidad + 4 px, sin animar la altura); al cerrar desaparece al
        /// instante (salir más rápido que entrar). El chevron gira 90° en 160 ms. Devuelve el panel; Children[0] es el ToggleButton.
        /// </summary>
        public static StackPanel Disclosure(string title, UIElement content, bool expanded = false)
        {
            var panel = new StackPanel();
            var rotate = new RotateTransform(expanded ? 90 : 0);
            var chevron = new Path
            {
                Data = Geometry.Parse("M 0,0 L 4,4 L 0,8"),
                Stroke = B(Muted), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = 8, Height = 8, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = rotate, RenderTransformOrigin = new Point(0.3, 0.5),
            };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(chevron);
            head.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), VerticalAlignment = VerticalAlignment.Center });

            var tt = new ControlTemplate(typeof(ToggleButton));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
            bd.SetValue(Border.PaddingProperty, new Thickness(0, 4, 6, 4));
            bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            tt.VisualTree = bd;
            var toggle = new ToggleButton
            {
                Content = head, Template = tt, IsChecked = expanded, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left, FocusVisualStyle = FocusRing,
            };
            AutomationProperties.SetName(toggle, title);
            panel.Children.Add(toggle);

            var host = new Border { Child = content, Margin = new Thickness(18, 6, 0, 0), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            panel.Children.Add(host);

            void Turn(double angle)
            {
                if (ReducedMotion || FromKeyboard) { rotate.BeginAnimation(RotateTransform.AngleProperty, null); rotate.Angle = angle; return; }
                rotate.BeginAnimation(RotateTransform.AngleProperty, To(angle, 160));
            }
            toggle.Checked += (_, __) => { host.Visibility = Visibility.Visible; Turn(90); if (!FromKeyboard) Enter(host, 4, 200); };
            toggle.Unchecked += (_, __) => { host.Visibility = Visibility.Collapsed; Turn(0); };
            return panel;
        }

        public enum Kind { Primary, Secondary, Danger, Quiet, DangerSolid }

        /// <summary>
        /// Botón: esquinas de 8 px, respuesta al pulsar, anillo de foco y estados hover/deshabilitado sin animación (se usan mucho).
        /// Danger = destructivo discreto (texto rojo); DangerSolid = sólo para la confirmación final de algo destructivo.
        /// Deshabilitado: colores explícitos (hundido, línea, texto apagado), nunca un bloque gris por opacidad.
        /// </summary>
        public static Button Button(string text, Kind kind = Kind.Secondary)
        {
            Color bg, fg, border, hover;
            switch (kind)
            {
                case Kind.Primary: bg = Navy; fg = White; border = Navy; hover = NavySoft; break;
                case Kind.DangerSolid: bg = Red; fg = White; border = Red; hover = Darken(Red, 0.88); break;
                case Kind.Danger: bg = White; fg = Red; border = Color.FromRgb(0xEE, 0xC2, 0xC6); hover = Color.FromRgb(0xFD, 0xF1, 0xF2); break;
                case Kind.Quiet: bg = Colors.Transparent; fg = NavySoft; border = Colors.Transparent; hover = Sunken; break;
                default: bg = White; fg = Ink; border = Color.FromRgb(0xD5, 0xDA, 0xE3); hover = Color.FromRgb(0xF3, 0xF5, 0xF8); break;
            }

            var template = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
            bd.SetValue(Border.BackgroundProperty, B(bg));
            bd.SetValue(Border.BorderBrushProperty, B(border));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 8));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter), "cp");
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            template.VisualTree = bd;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, B(hover), "bd"));
            if (kind == Kind.Secondary) over.Setters.Add(new Setter(Border.BorderBrushProperty, B(Color.FromRgb(0xC4, 0xCB, 0xD6)), "bd"));
            template.Triggers.Add(over);
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            if (kind != Kind.Quiet)
            {
                off.Setters.Add(new Setter(Border.BackgroundProperty, B(Sunken), "bd"));
                off.Setters.Add(new Setter(Border.BorderBrushProperty, B(Line), "bd"));
            }
            off.Setters.Add(new Setter(TextElement.ForegroundProperty, B(Muted), "cp"));
            template.Triggers.Add(off);

            var b = new Button
            {
                Content = text,
                Template = template,
                Foreground = B(fg),
                FontWeight = kind == Kind.Primary || kind == Kind.DangerSolid ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 92,
                MinHeight = 34,
                FocusVisualStyle = FocusRing,
            };
            Pressable(b);
            return b;
        }

        /// <summary>
        /// Opción seleccionable como tarjeta (sobre un RadioButton o CheckBox real: teclado, grupo e IsChecked siguen funcionando).
        /// Marcada: borde marino y fondo apenas teñido; indicador circular (radio) o cuadrado (casilla) a la izquierda.
        /// </summary>
        public static T Choice<T>(T toggle, string title, string hint) where T : ToggleButton
        {
            var radio = toggle is RadioButton;
            var content = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            content.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = B(Ink), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(hint)) content.Children.Add(new TextBlock { Text = hint, FontSize = 12.5, Foreground = B(Muted), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            toggle.Content = content;

            var t = new ControlTemplate(typeof(T));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius));
            bd.SetValue(Border.BackgroundProperty, B(White));
            bd.SetValue(Border.BorderBrushProperty, B(Line));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            bd.SetValue(Border.PaddingProperty, new Thickness(14, 12, 14, 12));
            var row = new FrameworkElementFactory(typeof(DockPanel));
            var ind = new FrameworkElementFactory(typeof(Border), "ind");
            ind.SetValue(FrameworkElement.WidthProperty, 18.0);
            ind.SetValue(FrameworkElement.HeightProperty, 18.0);
            ind.SetValue(Border.CornerRadiusProperty, new CornerRadius(radio ? 9 : 5));
            ind.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            ind.SetValue(Border.BorderBrushProperty, B(Color.FromRgb(0xB8, 0xC0, 0xCC)));
            ind.SetValue(Border.BackgroundProperty, B(White));
            ind.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            ind.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 0));
            ind.SetValue(DockPanel.DockProperty, Dock.Left);
            var mark = new FrameworkElementFactory(radio ? typeof(Ellipse) : typeof(Path), "mark");
            if (radio)
            {
                mark.SetValue(FrameworkElement.WidthProperty, 8.0);
                mark.SetValue(FrameworkElement.HeightProperty, 8.0);
                mark.SetValue(Shape.FillProperty, B(White));
            }
            else
            {
                mark.SetValue(Path.DataProperty, Geometry.Parse("M 3.5,8.5 L 7,12 L 13.5,5"));
                mark.SetValue(Shape.StrokeProperty, B(White));
                mark.SetValue(Shape.StrokeThicknessProperty, 2.0);
                mark.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
                mark.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
                mark.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            }
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            ind.AppendChild(mark);
            row.AppendChild(ind);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            row.AppendChild(cp);
            bd.AppendChild(row);
            t.VisualTree = bd;

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BorderBrushProperty, B(Color.FromRgb(0xC4, 0xCB, 0xD6)), "bd"));
            t.Triggers.Add(over);
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BorderBrushProperty, B(NavySoft), "bd"));
            on.Setters.Add(new Setter(Border.BackgroundProperty, Tint(Navy, 0x0F), "bd"));
            on.Setters.Add(new Setter(Border.BackgroundProperty, B(Navy), "ind"));
            on.Setters.Add(new Setter(Border.BorderBrushProperty, B(Navy), "ind"));
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "mark"));
            t.Triggers.Add(on);
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            t.Triggers.Add(off);

            toggle.Template = t;
            toggle.Cursor = Cursors.Hand;
            toggle.FocusVisualStyle = FocusRingFor(Radius);
            toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            toggle.Margin = new Thickness(0, 8, 0, 0);
            Pressable(toggle, 0.99); // superficie grande: 0.97 se nota demasiado
            return toggle;
        }

        public static TextBlock Label(string text, double size = 13, bool bold = false, Color? color = null) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = B(color ?? Ink),
            TextWrapping = TextWrapping.Wrap,
        };

        /// <summary>"claude-code" → "Claude", "codex-mcp-client" → "Codex".</summary>
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
            Padding = new Thickness(Gutter, 18, Gutter, 8),
            Focusable = false,
        };

        /// <summary>Pie fijo: nota a la izquierda y botones a la derecha, sobre superficie blanca con línea fina superior.</summary>
        public static Border Footer(UIElement note, UIElement buttons)
        {
            var g = new Grid { Margin = new Thickness(Gutter, 12, Gutter, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (note != null) g.Children.Add(note);
            if (buttons != null) { Grid.SetColumn(buttons, 1); g.Children.Add(buttons); }
            return new Border { Background = B(White), BorderBrush = B(Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
        }
    }
}
