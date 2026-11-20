// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Iconos del ribbon dibujados en código (vectoriales, nítidos a 16 y 32 px), sin archivos de imagen.
    /// Cada icono se dibuja en un lienzo de 32×32 y se escala. Los bordes rectos caen en coordenadas pares para que
    /// coincidan con píxeles enteros tanto a 32 px como a 16 px (escala 0,5). Paleta de la marca: marino, blanco y estados.
    /// </summary>
    internal static class Icons
    {
        public static ImageSource Status(Color state, int size) => Render(size, dc =>
        {
            Monitor(dc);
            dc.DrawEllipse(Theme.B(Theme.White), null, new Point(24, 22), 7, 7);
            dc.DrawEllipse(Theme.B(state), null, new Point(24, 22), 5, 5);
        });

        /// <summary>Cuadro de transición de Estado (t en [0,1]): el punto ya tiene el color nuevo desde el primer cuadro (nunca se mezclan
        /// dos colores de estado) y un anillo de ese color sale de él y se desvanece. t = 1 no dibuja anillo. <paramref name="from"/> no se dibuja.</summary>
        public static ImageSource Status(Color from, Color to, double t, int size) => Render(size, dc =>
        {
            Monitor(dc);
            dc.DrawEllipse(Theme.B(Theme.White), null, new Point(24, 22), 7, 7);
            dc.DrawEllipse(Theme.B(to), null, new Point(24, 22), 5, 5);
            var a = (byte)(0x99 * (1 - t));
            if (a > 0) dc.DrawEllipse(null, new Pen(Theme.B(Color.FromArgb(a, to.R, to.G, to.B)), 1.5), new Point(24, 22), 7 + 3 * t, 7 + 3 * t);
        });

        // Monitor: "la conexión con la IA".
        private static void Monitor(DrawingContext dc)
        {
            Fill(dc, Theme.Navy, Rounded(2, 4, 24, 18, 3));
            Fill(dc, Theme.Sunken, Rounded(4, 6, 20, 14, 1.5));
            // Trazo "IA" dentro del monitor
            Fill(dc, Theme.NavySoft, Rounded(8, 10, 8, 2, 1));
            Fill(dc, Theme.NavySoft, Rounded(8, 14, 12, 2, 1));
            Fill(dc, Theme.Navy, Rounded(12, 22, 4, 4, 0));
            Fill(dc, Theme.Navy, Rounded(8, 26, 12, 2, 1));
        }

        // Pausa <-> reanudar: cada barra se transforma en una mitad del triángulo (vértice a vértice).
        private static readonly Point[] BarL = { new Point(10, 10), new Point(14, 10), new Point(14, 22), new Point(10, 22) };
        private static readonly Point[] BarR = { new Point(18, 10), new Point(22, 10), new Point(22, 22), new Point(18, 22) };
        private static readonly Point[] TriL = { new Point(13, 10), new Point(18.5, 13.3), new Point(18.5, 18.7), new Point(13, 22) };
        private static readonly Point[] TriR = { new Point(18, 13), new Point(23, 16), new Point(23, 16), new Point(18, 19) };

        /// <summary>Cuadro de transición de Pausar (t en [0,1] hacia <paramref name="paused"/>): el círculo ya tiene el color final; sólo la forma
        /// cambia (barras que se vuelven triángulo, o al revés).</summary>
        public static ImageSource Pause(bool paused, double t, int size) => Render(size, dc =>
        {
            var k = paused ? t : 1 - t; // 0 = barras de pausa, 1 = triángulo de reanudar
            dc.DrawEllipse(Theme.B(paused ? Theme.Red : Theme.Slate), null, new Point(16, 16), 14, 14);
            dc.DrawGeometry(Theme.B(Theme.White), null, Quad(BarL, TriL, k));
            dc.DrawGeometry(Theme.B(Theme.White), null, Quad(BarR, TriR, k));
        });

        private static Geometry Quad(Point[] a, Point[] b, double k)
        {
            Point P(int i) => new Point(a[i].X + (b[i].X - a[i].X) * k, a[i].Y + (b[i].Y - a[i].Y) * k);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(0), true, true);
                ctx.LineTo(P(1), true, false);
                ctx.LineTo(P(2), true, false);
                ctx.LineTo(P(3), true, false);
            }
            g.Freeze();
            return g;
        }

        public static ImageSource Pause(bool paused, int size) => Render(size, dc =>
        {
            var c = paused ? Theme.Red : Theme.Slate;
            dc.DrawEllipse(Theme.B(c), null, new Point(16, 16), 14, 14);
            if (paused)
            {
                // Triángulo "reanudar" (desplazado 1 a la derecha para que se vea centrado)
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(13, 10), true, true);
                    ctx.LineTo(new Point(23, 16), true, false);
                    ctx.LineTo(new Point(13, 22), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(Theme.B(Theme.White), null, g);
            }
            else
            {
                Fill(dc, Theme.White, Rounded(10, 10, 4, 12, 1));
                Fill(dc, Theme.White, Rounded(18, 10, 4, 12, 1));
            }
        });

        /// <summary>Modo: en vivo (verde, flecha directa al modelo) o retener (ámbar, cambios apilados).</summary>
        public static ImageSource Mode(bool hold, int size) => Render(size, dc =>
        {
            var c = hold ? Theme.Amber : Theme.Green;
            Fill(dc, c, Rounded(2, 2, 28, 28, 7));
            ModeGlyph(dc, hold);
        });

        /// <summary>Cuadro de transición de Modo (t en [0,1] hacia <paramref name="hold"/>): el fondo ya tiene el color final; el dibujo viejo
        /// se desvanece y el nuevo aparece por opacidad, creciendo de 0,92 a 1.</summary>
        public static ImageSource Mode(bool hold, double t, int size) => Render(size, dc =>
        {
            Fill(dc, hold ? Theme.Amber : Theme.Green, Rounded(2, 2, 28, 28, 7));
            dc.PushOpacity(1 - t);
            ModeGlyph(dc, !hold);
            dc.Pop();
            var s = 0.92 + 0.08 * t;
            dc.PushOpacity(t);
            dc.PushTransform(new ScaleTransform(s, s, 16, 16));
            ModeGlyph(dc, hold);
            dc.Pop();
            dc.Pop();
        });

        private static void ModeGlyph(DrawingContext dc, bool hold)
        {
            if (hold)
            {
                Fill(dc, Theme.White, Rounded(8, 8, 16, 4, 2));
                Fill(dc, Color.FromArgb(0xE0, 255, 255, 255), Rounded(8, 14, 16, 4, 2));
                Fill(dc, Color.FromArgb(0xB0, 255, 255, 255), Rounded(8, 20, 16, 4, 2));
            }
            else
            {
                var pen = new Pen(Theme.B(Theme.White), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawLine(pen, new Point(9, 16), new Point(22, 16));
                var head = new StreamGeometry();
                using (var ctx = head.Open())
                {
                    ctx.BeginFigure(new Point(17, 11), false, false);
                    ctx.LineTo(new Point(22, 16), true, true);
                    ctx.LineTo(new Point(17, 21), true, true);
                }
                head.Freeze();
                dc.DrawGeometry(null, pen, head);
            }
        }

        /// <summary>Cola: tres tarjetas apiladas con un globo rojo con el número de cambios en espera.</summary>
        public static ImageSource Queue(int count, int size) => Queue(count, 1, size);

        /// <summary>Cuadro de transición de Cola (t en [0,1]): el globo con el número nuevo crece de 0,85 a 1 (nunca desde 0).</summary>
        public static ImageSource Queue(int count, double t, int size) => Render(size, dc =>
        {
            Fill(dc, Color.FromArgb(0x66, Theme.Slate.R, Theme.Slate.G, Theme.Slate.B), Rounded(8, 4, 18, 6, 2));
            Fill(dc, Theme.Slate, Rounded(6, 8, 20, 8, 2));
            Fill(dc, Theme.Navy, Rounded(4, 14, 22, 14, 3));
            Fill(dc, Theme.White, Rounded(8, 18, 12, 2, 1));
            Fill(dc, Theme.White, Rounded(8, 22, 8, 2, 1));
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                // Borde blanco de radio R tangente a los lados derecho y superior: el globo entero queda en [0,32] (y en 16 px a escala 0,5).
                // R entero => centro en coordenada par/entera, nítido a 32 y 16 px.
                var R = text.Length > 1 ? 10.0 : 9.0;
                var r = R - 1.2;
                var c = new Point(32 - R, R);
                var s = 0.85 + 0.15 * t;
                if (s < 1) dc.PushTransform(new ScaleTransform(s, s, c.X, c.Y)); // s <= 1 alrededor del centro: nunca sale del lienzo
                dc.DrawEllipse(Theme.B(Theme.White), null, c, R, R);
                dc.DrawEllipse(Theme.B(Theme.Red), null, c, r, r);
                // Sólo el globo crece: el texto escalado por debajo de 1 pierde el ajuste de píxel ("+" borroso), así que va siempre a tamaño real.
                if (s < 1) dc.Pop();
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), text.Length > 2 ? 7.5 : 10, Theme.B(Theme.White), 1.0);
                // "99+" puede ser más ancho que el círculo rojo: se estrecha en horizontal (no hay cara condensada garantizada en WPF).
                var maxW = 2 * r - 3;
                var sx = ft.Width > maxW ? maxW / ft.Width : 1;
                System.Diagnostics.Debug.Assert(c.X + R <= 32 && c.Y - R >= 0 && ft.Width * sx <= maxW + 1e-9, "globo de la cola fuera del lienzo");
                if (sx < 1) dc.PushTransform(new ScaleTransform(sx, 1, c.X, c.Y));
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                if (sx < 1) dc.Pop();
            }
        });

        public static ImageSource Journal(int size) => Render(size, dc =>
        {
            Fill(dc, Theme.Navy, Rounded(6, 2, 20, 28, 3));
            Fill(dc, Theme.White, Rounded(8, 4, 16, 24, 1.5));
            for (var y = 10; y <= 22; y += 4) Fill(dc, Theme.NavySoft, Rounded(12, y, y > 20 ? 6 : 8, 2, 1));
        });

        public static ImageSource Folder(int size) => Render(size, dc =>
        {
            Fill(dc, Theme.Slate, Rounded(2, 6, 12, 6, 2));
            Fill(dc, Theme.Navy, Rounded(2, 10, 28, 18, 3));
        });

        public static ImageSource Help(int size) => Render(size, dc =>
        {
            dc.DrawEllipse(Theme.B(Theme.Navy), null, new Point(16, 16), 14, 14);
            var ft = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 19, Theme.B(Theme.White), 1.0);
            dc.DrawText(ft, new Point(Math.Round(16 - ft.Width / 2), Math.Round(16 - ft.Height / 2)));
        });

        /// <summary>Actualizar: flecha circular. state 0 al día (azul), 1 hay versión nueva (globo verde), 2 falta reiniciar (globo ámbar).</summary>
        public static ImageSource Update(int state, int size) => Update(state, 1, size);

        /// <summary>Cuadro de transición de Actualizar (t en [0,1]): el globo aparece creciendo de 0,85 a 1 y entrando por opacidad (nunca desde 0). t = 1 es el icono quieto.</summary>
        public static ImageSource Update(int state, double t, int size) => Render(size, dc =>
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
                var s = 0.85 + 0.15 * t;
                if (t < 1) { dc.PushOpacity(t); dc.PushTransform(new ScaleTransform(s, s, 24, 24)); }
                // Globo centrado en (24,24): cae en el píxel (12,12) a 16 px.
                dc.DrawEllipse(Theme.B(Theme.White), null, new Point(24, 24), 8, 8);
                dc.DrawEllipse(Theme.B(c), null, new Point(24, 24), 6.5, 6.5);
                var w = new Pen(Theme.B(Theme.White), 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (state == 1)
                {
                    dc.DrawLine(w, new Point(24, 27.5), new Point(24, 20.8));
                    dc.DrawLine(w, new Point(21.1, 23.5), new Point(24, 20.6));
                    dc.DrawLine(w, new Point(26.9, 23.5), new Point(24, 20.6));
                }
                else
                {
                    dc.DrawLine(w, new Point(24, 20.5), new Point(24, 24.7));
                    dc.DrawLine(w, new Point(24, 24.7), new Point(26.5, 26.1));
                }
                if (t < 1) { dc.Pop(); dc.Pop(); }
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
