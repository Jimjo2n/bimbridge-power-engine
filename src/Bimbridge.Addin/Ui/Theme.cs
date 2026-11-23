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

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Shared visual system for the installer and the add-in windows (all in code, no XAML, so it builds the same on
    /// net48, net8 and net10). Light, calm surfaces, navy as the action color and red only as a brand accent;
    /// green, amber and red for states. Minimal, purposeful motion: press feedback, entry of screens whose content
    /// changes (occasional) and nothing on what repeats many times a day. Honors Windows "reduce animations".
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
        /// <summary>Sunken surface (reading areas, code, row backgrounds on mouse hover).</summary>
        public static readonly Color Sunken = Color.FromRgb(0xEE, 0xF1, 0xF5);

        public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static SolidColorBrush Tint(Color c, byte alpha = 0x22) => B(Color.FromArgb(alpha, c.R, c.G, c.B));
        public static Color Darken(Color c, double f = 0.8) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        // ---------------- Typography and sizes (4/8 grid) ----------------
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        public const double Gutter = 24;
        public const double Radius = 10;
        /// <summary>Radius of buttons, notices, sunken areas and segments.</summary>
        public const double RadiusSmall = 8;
        /// <summary>Single product-name eyebrow used in headers.</summary>
        public const string ProductEyebrow = "BIMBRIDGE · Power Engine";

        // ---------------- Motion ----------------
        /// <summary>Windows with animations off or high contrast: no movement (feedback stays immediate).</summary>
        public static bool ReducedMotion => ReduceMotionOverride || WindowsReducedMotion;

        public static bool WindowsReducedMotion
        {
            get { try { return !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast; } catch (Exception) { return false; } }
        }

        /// <summary>The add-in's "Motion: Reduced" preference (Settings). Adds to the Windows one; never overrides it.</summary>
        public static bool ReduceMotionOverride;

        /// <summary>The last change came from the keyboard: respond instantly, without animating.</summary>
        public static bool FromKeyboard => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

        // strong ease-out (cubic-bezier(0.23, 1, 0.32, 1)): starts fast, the UI responds on the first frame.
        private static readonly KeySpline EaseOutSpline = Frozen(new KeySpline(0.23, 1, 0.32, 1));
        private static KeySpline Frozen(KeySpline k) { k.Freeze(); return k; }

        private static AnimationTimeline To(double to, int ms) => new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EaseOutSpline) },
        };

        /// <summary>
        /// Entry of a new screen (step change, result): opacity and 8 px upward, 220 ms ease-out.
        /// Occasional, which is why it animates; with reduced motion it just appears.
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
        /// Press feedback: scale (0.97 buttons, 0.99 large surfaces) in 100 ms on press, back in 160 ms on release or leave.
        /// Never blocks the click.
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

        // ---------------- Keyboard focus ----------------
        private static readonly System.Collections.Generic.Dictionary<double, Style> _focus = new System.Collections.Generic.Dictionary<double, Style>();
        /// <summary>Focus ring for controls with an 8 px radius (buttons, segments). See <see cref="FocusRingFor"/>.</summary>
        public static Style FocusRing => FocusRingFor(RadiusSmall);

        /// <summary>
        /// Visible focus ring (2 px navy, 3 px outside the control) concentric with a control of radius <paramref name="radius"/>:
        /// the ring uses radius + 3 so the gap is the same at the corners.
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

        // ---------------- Window and structure ----------------
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

        /// <summary>Brand mark: rounded navy square with the bridge (two piers and the red deck).</summary>
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
            Rect(1, 6, 18, 3.2, Red);           // deck
            Rect(3.5, 9.2, 2.6, 9, White);      // pier
            Rect(13.9, 9.2, 2.6, 9, White);     // pier
            Rect(1, 2, 18, 1.6, Tint(White, 0x88).Color);
            g.Children.Add(c);
            return g;
        }

        /// <summary>
        /// Header with the product as eyebrow (omitted if the title already names BIMBRIDGE, to avoid repeating it).
        /// </summary>
        public static Border Header(string title, string subtitle, out TextBlock subtitleBlock, out Border pill, out TextBlock pillText)
            => Header((title ?? "").IndexOf("BIMBRIDGE", StringComparison.OrdinalIgnoreCase) >= 0 ? null : ProductEyebrow, title, subtitle, out subtitleBlock, out pill, out pillText);

        /// <summary>
        /// Header: white surface with the brand mark, optional small eyebrow (null = no eyebrow), title and subtitle;
        /// status pill on the right. A thin line separates it from the content (no color band competing with the content).
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

        /// <summary>Card: white surface, nearly invisible border and a very soft shadow. The content goes in a child so the shadow does not blur it.</summary>
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

        /// <summary>Section title inside a card, with an optional step number (1, 2, 3…).</summary>
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

        /// <summary>Step number (1, 2, 3…): 22 px navy-tinted circle. The single style for sections and step lists.</summary>
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

        /// <summary>Status dot (live/paused/error) for rows and headers.</summary>
        public static Ellipse Dot(Color color, double size = 8) => new Ellipse { Width = size, Height = size, Fill = B(color), VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Inline notice with a color bar on the left (info, warning or error).</summary>
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

        /// <summary>Recolors a notice created with <see cref="Notice"/> (progress, success, error) without rebuilding it.</summary>
        public static void SetNotice(Border border, TextBlock textBlock, Color color)
        {
            textBlock.Foreground = B(Darken(color, 0.72));
            border.Background = Tint(color, 0x16);
            border.BorderBrush = Tint(color, 0x80);
        }

        /// <summary>
        /// Status pill (the one in <see cref="Header"/>): color + words, with an optional leading dot. Can be called many times;
        /// it does not animate (it changes often).
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

        /// <summary>Vector result mark: check (done) or dash (no changes / skipped), no background.</summary>
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

        /// <summary>Thin divider between groups in a card.</summary>
        public static Border Divider() => new Border { Height = 1, Background = B(Line), Margin = new Thickness(0, 12, 0, 12), SnapsToDevicePixels = true };

        /// <summary>Aligned (tabular) figures so changing numbers do not jitter.</summary>
        public static TextBlock Tabular(TextBlock t)
        {
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        /// <summary>Highlighted stat: large number with tabular figures, label and optional sublabel.</summary>
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

        /// <summary>Sunken reading area (paths, commands, technical details).</summary>
        public static Border Inset(UIElement child) => new Border
        {
            Background = B(Sunken),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusSmall),
            Padding = new Thickness(12, 8, 12, 8),
            Child = child,
        };

        /// <summary>Thin progress bar (4 px, navy on sunken). Indeterminate only while real work is happening.</summary>
        public static ProgressBar ProgressLine(bool indeterminate = true) => new ProgressBar
        {
            IsIndeterminate = indeterminate,
            Height = 4,
            Foreground = B(Navy),
            Background = B(Sunken),
            BorderThickness = new Thickness(0),
        };

        /// <summary>Footer note (left of <see cref="Footer"/>): small, muted, wraps.</summary>
        public static TextBlock FooterNote(string text = "") => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = B(Muted), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };

        /// <summary>
        /// Segmented filter over real RadioButtons (Tab enters once, arrows move, Space selects). Selected: sunken background and
        /// semibold Ink text; the rest, subdued. No animation (used many times). Build it once and do not rebuild it on
        /// refresh, so focus is not lost. <paramref name="onSelect"/> receives the selected text (not called on creation).
        /// </summary>
        public static StackPanel Segmented(string[] options, string selected, Action<string> onSelect)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            var group = "sec" + Guid.NewGuid().ToString("N");

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
        /// Lightweight collapsible detail (progressive disclosure): header with a chevron over a real ToggleButton and content below.
        /// On open, the content enters with <see cref="Enter"/> (opacity + 4 px, height not animated); on close it disappears
        /// instantly (exit faster than enter). The chevron rotates 90° in 160 ms. Returns the panel; Children[0] is the ToggleButton.
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
        /// Button: 8 px corners, press feedback, focus ring and hover/disabled states without animation (they are used a lot).
        /// Danger = subdued destructive (red text); DangerSolid = only for the final confirmation of something destructive.
        /// Disabled: explicit colors (sunken, line, muted text), never a grey block by opacity.
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
        /// Selectable option as a card (over a real RadioButton or CheckBox: keyboard, group and IsChecked keep working).
        /// Checked: navy border and a faintly tinted background; circular (radio) or square (checkbox) indicator on the left.
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
            Pressable(toggle, 0.99); // large surface: 0.97 is too noticeable
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
            if (s.Contains("probe") || s.Contains("prueba")) return "Probe";
            if (s.Length == 0) return "AI";
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
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
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

        /// <summary>Fixed footer: note on the left and buttons on the right, on a white surface with a thin top line.</summary>
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
