// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Iconos del ribbon dibujados en código (vectoriales, nítidos a 16 y 32 px), sin archivos de imagen.
    /// Cada icono se dibuja en un lienzo de 32×32 y se escala.
    /// </summary>
    internal static class Icons
    {
        public static ImageSource Status(Color state, int size) => Render(size, dc =>
        {
            // Monitor con un punto de estado: "la conexión con la IA".
            Fill(dc, Theme.Navy, Rounded(3, 5, 24, 17, 3));
            Fill(dc, Color.FromRgb(0xDC, 0xE4, 0xF0), Rounded(5.5, 7.5, 19, 12, 1.5));
            Fill(dc, Theme.Navy, Rounded(12, 23, 6, 3, 0));
            Fill(dc, Theme.Navy, Rounded(9, 26, 12, 2.4, 1.2));
            dc.DrawEllipse(Theme.B(Colors.White), null, new Point(24.5, 22.5), 6.5, 6.5);
            dc.DrawEllipse(Theme.B(state), null, new Point(24.5, 22.5), 4.8, 4.8);
            // Trazo "IA" dentro del monitor
            var pen = new Pen(Theme.B(Theme.NavySoft), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(9, 11.5), new Point(15, 11.5));
            dc.DrawLine(pen, new Point(9, 15), new Point(18, 15));
        });

        public static ImageSource Pause(bool paused, int size) => Render(size, dc =>
        {
            var c = paused ? Theme.Red : Theme.Slate;
            dc.DrawEllipse(Theme.B(c), null, new Point(16, 16), 13, 13);
            var w = Theme.B(Colors.White);
            if (paused)
            {
                // Triángulo "reanudar"
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(13, 9.5), true, true);
                    ctx.LineTo(new Point(23, 16), true, false);
                    ctx.LineTo(new Point(13, 22.5), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(w, null, g);
            }
            else
            {
                Fill(dc, Colors.White, Rounded(11, 9.5, 3.6, 13, 1.2));
                Fill(dc, Colors.White, Rounded(17.4, 9.5, 3.6, 13, 1.2));
            }
        });

        /// <summary>Modo: en vivo (verde, flecha directa al modelo) o retener (ámbar, cambios apilados).</summary>
        public static ImageSource Mode(bool hold, int size) => Render(size, dc =>
        {
            var c = hold ? Theme.Amber : Theme.Green;
            Fill(dc, c, Rounded(2, 2, 28, 28, 7));
            if (hold)
            {
                Fill(dc, Colors.White, Rounded(8, 9, 16, 3.4, 1.7));
                Fill(dc, Color.FromArgb(0xE0, 255, 255, 255), Rounded(8, 14.3, 16, 3.4, 1.7));
                Fill(dc, Color.FromArgb(0xB0, 255, 255, 255), Rounded(8, 19.6, 16, 3.4, 1.7));
            }
            else
            {
                var pen = new Pen(Theme.B(Colors.White), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawLine(pen, new Point(8, 16), new Point(22, 16));
                dc.DrawLine(pen, new Point(17, 10.5), new Point(22.5, 16));
                dc.DrawLine(pen, new Point(17, 21.5), new Point(22.5, 16));
            }
        });

        /// <summary>Cola: tres tarjetas apiladas con un globo rojo con el número de cambios en espera.</summary>
        public static ImageSource Queue(int count, int size) => Render(size, dc =>
        {
            Fill(dc, Color.FromRgb(0xB9, 0xC4, 0xD6), Rounded(7, 4, 20, 6, 2));
            Fill(dc, Color.FromRgb(0x6F, 0x82, 0xA3), Rounded(5, 10, 22, 7, 2));
            Fill(dc, Theme.Navy, Rounded(3, 17, 24, 11, 2.5));
            var pen = new Pen(Theme.B(Colors.White), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(7, 21.5), new Point(18, 21.5));
            dc.DrawLine(pen, new Point(7, 24.5), new Point(14, 24.5));
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                var r = text.Length > 1 ? 8.5 : 7.5;
                dc.DrawEllipse(Theme.B(Colors.White), null, new Point(32 - r, r), r + 1.2, r + 1.2);
                dc.DrawEllipse(Theme.B(Theme.Red), null, new Point(32 - r, r), r, r);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), text.Length > 2 ? 7.5 : 10, Theme.B(Colors.White), 1.0);
                dc.DrawText(ft, new Point(32 - r - ft.Width / 2, r - ft.Height / 2));
            }
        });

        public static ImageSource Journal(int size) => Render(size, dc =>
        {
            Fill(dc, Theme.Navy, Rounded(6, 3, 20, 26, 3));
            Fill(dc, Colors.White, Rounded(8.5, 5.5, 15, 21, 1.5));
            var pen = new Pen(Theme.B(Theme.NavySoft), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (var y = 10.0; y <= 22; y += 4) dc.DrawLine(pen, new Point(11.5, y), new Point(y > 20 ? 16 : 20.5, y));
        });

        public static ImageSource Folder(int size) => Render(size, dc =>
        {
            Fill(dc, Color.FromRgb(0x6F, 0x82, 0xA3), Rounded(3, 7, 12, 6, 2));
            Fill(dc, Theme.Navy, Rounded(3, 10, 26, 17, 2.5));
        });

        public static ImageSource Help(int size) => Render(size, dc =>
        {
            dc.DrawEllipse(Theme.B(Theme.NavySoft), null, new Point(16, 16), 13, 13);
            var ft = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 19, Theme.B(Colors.White), 1.0);
            dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
        });

        /// <summary>Actualizar: flecha circular. state 0 al día (azul), 1 hay versión nueva (globo verde), 2 falta reiniciar (globo ámbar).</summary>
        public static ImageSource Update(int state, int size) => Render(size, dc =>
        {
            var pen = new Pen(Theme.B(Theme.Navy), 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            // Arco de ~300° alrededor de (15,17), radio 10
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(24.2, 12.0), false, false);
                ctx.ArcTo(new Point(19.0, 26.2), new Size(10, 10), 0, true, SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            // Punta de flecha en el inicio del arco
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new Point(26.5, 5.5), true, true);
                ctx.LineTo(new Point(27.2, 14.5), true, false);
                ctx.LineTo(new Point(18.6, 12.2), true, false);
            }
            head.Freeze();
            dc.DrawGeometry(Theme.B(Theme.Navy), null, head);
            if (state > 0)
            {
                var c = state == 1 ? Theme.Green : Theme.Amber;
                dc.DrawEllipse(Theme.B(Colors.White), null, new Point(24.5, 24.5), 7.6, 7.6);
                dc.DrawEllipse(Theme.B(c), null, new Point(24.5, 24.5), 6.2, 6.2);
                var w = new Pen(Theme.B(Colors.White), 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (state == 1)
                {
                    dc.DrawLine(w, new Point(24.5, 28), new Point(24.5, 21.3));
                    dc.DrawLine(w, new Point(21.6, 24.0), new Point(24.5, 21.1));
                    dc.DrawLine(w, new Point(27.4, 24.0), new Point(24.5, 21.1));
                }
                else
                {
                    dc.DrawLine(w, new Point(24.5, 21.0), new Point(24.5, 25.2));
                    dc.DrawLine(w, new Point(24.5, 25.2), new Point(27.0, 26.6));
                }
            }
        });

        // ---------------- utilidades ----------------

        private static Geometry Rounded(double x, double y, double w, double h, double r)
        {
            var g = new RectangleGeometry(new Rect(x, y, w, h), r, r);
            g.Freeze();
            return g;
        }

        private static void Fill(DrawingContext dc, Color c, Geometry g) => dc.DrawGeometry(Theme.B(c), null, g);

        private static ImageSource Render(int size, Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                draw(dc);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
