// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Ribbon icons drawn in code (vector, crisp at 16 and 32 px), with no image files.
    /// Each icon is drawn on a 32×32 canvas and scaled. Straight edges fall on even coordinates so that
    /// they line up with whole pixels both at 32 px and at 16 px (scale 0.5). Brand palette: black, white, brand red and states.
    /// </summary>
    internal static class Icons
    {
        public static ImageSource Status(Color state, int size) => Render(size, dc =>
        {
            Monitor(dc);
            dc.DrawEllipse(Theme.B(Theme.White), null, new Point(24, 22), 7, 7);
            dc.DrawEllipse(Theme.B(state), null, new Point(24, 22), 5, 5);
        });

        /// <summary>Status transition frame (t in [0,1]): the dot already has the new color from the first frame (two state colors
        /// are never blended) and a ring of that color grows out of it and fades. t = 1 draws no ring. <paramref name="from"/> is not drawn.</summary>
        public static ImageSource Status(Color from, Color to, double t, int size) => Render(size, dc =>
        {
            Monitor(dc);
            dc.DrawEllipse(Theme.B(Theme.White), null, new Point(24, 22), 7, 7);
            dc.DrawEllipse(Theme.B(to), null, new Point(24, 22), 5, 5);
            var a = (byte)(0x99 * (1 - t));
            if (a > 0) dc.DrawEllipse(null, new Pen(Theme.B(Color.FromArgb(a, to.R, to.G, to.B)), 1.5), new Point(24, 22), 7 + 3 * t, 7 + 3 * t);
        });

        // Monitor: "the connection with the AI".
        private static void Monitor(DrawingContext dc)
        {
            Fill(dc, Theme.Action, Rounded(2, 4, 24, 18, 3));
            Fill(dc, Theme.Sunken, Rounded(4, 6, 20, 14, 1.5));
            // "AI" strokes inside the monitor
            Fill(dc, Theme.ActionHover, Rounded(8, 10, 8, 2, 1));
            Fill(dc, Theme.ActionHover, Rounded(8, 14, 12, 2, 1));
            Fill(dc, Theme.Action, Rounded(12, 22, 4, 4, 0));
            Fill(dc, Theme.Action, Rounded(8, 26, 12, 2, 1));
        }

        // Pause <-> resume: each bar morphs into one half of the triangle (vertex to vertex).
        private static readonly Point[] BarL = { new Point(10, 10), new Point(14, 10), new Point(14, 22), new Point(10, 22) };
        private static readonly Point[] BarR = { new Point(18, 10), new Point(22, 10), new Point(22, 22), new Point(18, 22) };
        private static readonly Point[] TriL = { new Point(13, 10), new Point(18.5, 13.3), new Point(18.5, 18.7), new Point(13, 22) };
        private static readonly Point[] TriR = { new Point(18, 13), new Point(23, 16), new Point(23, 16), new Point(18, 19) };

        /// <summary>Pause transition frame (t in [0,1] towards <paramref name="paused"/>): the circle already has the final color; only the shape
        /// changes (bars turning into a triangle, or the other way round).</summary>
        public static ImageSource Pause(bool paused, double t, int size) => Render(size, dc =>
        {
            var k = paused ? t : 1 - t; // 0 = pause bars, 1 = resume triangle
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
                // "Resume" triangle (shifted 1 to the right so it looks centered)
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

        /// <summary>Mode: live (green, arrow straight to the model) or hold (amber, stacked changes).</summary>
        public static ImageSource Mode(bool hold, int size) => Render(size, dc =>
        {
            var c = hold ? Theme.Amber : Theme.Green;
            Fill(dc, c, Rounded(2, 2, 28, 28, 7));
            ModeGlyph(dc, hold);
        });

        /// <summary>Mode transition frame (t in [0,1] towards <paramref name="hold"/>): the background already has the final color; the old glyph
        /// fades out and the new one fades in, growing from 0.92 to 1.</summary>
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

        /// <summary>Queue: three stacked cards with a red badge showing the number of waiting changes.</summary>
        public static ImageSource Queue(int count, int size) => Queue(count, 1, size);

        /// <summary>Queue transition frame (t in [0,1]): the badge with the new number grows from 0.85 to 1 (never from 0).</summary>
        public static ImageSource Queue(int count, double t, int size) => Render(size, dc =>
        {
            Fill(dc, Color.FromArgb(0x66, Theme.Slate.R, Theme.Slate.G, Theme.Slate.B), Rounded(8, 4, 18, 6, 2));
            Fill(dc, Theme.Slate, Rounded(6, 8, 20, 8, 2));
            Fill(dc, Theme.Action, Rounded(4, 14, 22, 14, 3));
            Fill(dc, Theme.White, Rounded(8, 18, 12, 2, 1));
            Fill(dc, Theme.White, Rounded(8, 22, 8, 2, 1));
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                // White border of radius R tangent to the right and top edges: the whole badge stays within [0,32] (and within 16 px at scale 0.5).
                // Integer R => center on an even/integer coordinate, crisp at 32 and 16 px.
                var R = text.Length > 1 ? 10.0 : 9.0;
                var r = R - 1.2;
                var c = new Point(32 - R, R);
                var s = 0.85 + 0.15 * t;
                if (s < 1) dc.PushTransform(new ScaleTransform(s, s, c.X, c.Y)); // s <= 1 around the center: never leaves the canvas
                dc.DrawEllipse(Theme.B(Theme.White), null, c, R, R);
                dc.DrawEllipse(Theme.B(Theme.Red), null, c, r, r);
                // Only the badge grows: text scaled below 1 loses pixel snapping (blurry "+"), so it is always drawn at full size.
                if (s < 1) dc.Pop();
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), text.Length > 2 ? 7.5 : 10, Theme.B(Theme.White), 1.0);
                // "99+" can be wider than the red circle: it is narrowed horizontally (WPF guarantees no condensed face).
                var maxW = 2 * r - 3;
                var sx = ft.Width > maxW ? maxW / ft.Width : 1;
                System.Diagnostics.Debug.Assert(c.X + R <= 32 && c.Y - R >= 0 && ft.Width * sx <= maxW + 1e-9, "queue badge outside the canvas");
                if (sx < 1) dc.PushTransform(new ScaleTransform(sx, 1, c.X, c.Y));
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                if (sx < 1) dc.Pop();
            }
        });

        public static ImageSource Journal(int size) => Render(size, dc =>
        {
            Fill(dc, Theme.Action, Rounded(6, 2, 20, 28, 3));
            Fill(dc, Theme.White, Rounded(8, 4, 16, 24, 1.5));
            for (var y = 10; y <= 22; y += 4) Fill(dc, Theme.ActionHover, Rounded(12, y, y > 20 ? 6 : 8, 2, 1));
        });

        public static ImageSource Folder(int size) => Render(size, dc =>
        {
            Fill(dc, Theme.Slate, Rounded(2, 6, 12, 6, 2));
            Fill(dc, Theme.Action, Rounded(2, 10, 28, 18, 3));
        });

        public static ImageSource Help(int size) => Render(size, dc =>
        {
            dc.DrawEllipse(Theme.B(Theme.Action), null, new Point(16, 16), 14, 14);
            var ft = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 19, Theme.B(Theme.White), 1.0);
            dc.DrawText(ft, new Point(Math.Round(16 - ft.Width / 2), Math.Round(16 - ft.Height / 2)));
        });

        /// <summary>Update: circular arrow. state 0 up to date (blue), 1 new version available (green badge), 2 restart pending (amber badge).</summary>
        public static ImageSource Update(int state, int size) => Update(state, 1, size);

        /// <summary>Update transition frame (t in [0,1]): the badge appears growing from 0.85 to 1 and fading in (never from 0). t = 1 is the still icon.</summary>
        public static ImageSource Update(int state, double t, int size) => Render(size, dc =>
        {
            var pen = new Pen(Theme.B(Theme.Action), 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            // ~300° arc around (15,17), radius 10
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(24.2, 12.0), false, false);
                ctx.ArcTo(new Point(19.0, 26.2), new Size(10, 10), 0, true, SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            // Arrowhead at the start of the arc
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new Point(26.5, 5.5), true, true);
                ctx.LineTo(new Point(27.2, 14.5), true, false);
                ctx.LineTo(new Point(18.6, 12.2), true, false);
            }
            head.Freeze();
            dc.DrawGeometry(Theme.B(Theme.Action), null, head);
            if (state > 0)
            {
                var c = state == 1 ? Theme.Green : Theme.Amber;
                var s = 0.85 + 0.15 * t;
                if (t < 1) { dc.PushOpacity(t); dc.PushTransform(new ScaleTransform(s, s, 24, 24)); }
                // Badge centered on (24,24): lands on pixel (12,12) at 16 px.
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

        // ---------------- utilities ----------------

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
