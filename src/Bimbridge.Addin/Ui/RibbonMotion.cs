// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// Short transition of a ribbon button when its visible state changes: 5 frames in 180 ms with Theme's ease-out
    /// curve, on the Revit thread. Never looped; a new change replaces the one in progress (it is not queued); with
    /// reduced motion the final image is set immediately. With no change it does nothing.
    /// </summary>
    internal static class RibbonMotion
    {
        private const int Frames = 5, Ms = 180;
        /// <summary>Progress of each frame: cubic-bezier(0.23, 1, 0.32, 1) at times (i+1)/Frames. The last one is 1.</summary>
        internal static readonly double[] T = Ease(0.23, 1, 0.32, 1, Frames);

        private sealed class Track { public ImageSource[] Large, Small; public int Next; }
        private static readonly Dictionary<PushButton, Track> _tracks = new Dictionary<PushButton, Track>();
        private static readonly Dictionary<string, ImageSource[]> _cache = new Dictionary<string, ImageSource[]>();
        private static DispatcherTimer _timer;

        /// <summary>
        /// Takes <paramref name="button"/> to its final image. <paramref name="key"/> identifies the transition (from→to) so
        /// frames are reused; <paramref name="frame"/>(t, size) draws an intermediate one; <paramref name="final"/>(size) the still state.
        /// <paramref name="animate"/> = false (first paint) sets the final image with no transition.
        /// </summary>
        public static void Play(PushButton button, string key, bool animate, Func<double, int, ImageSource> frame, Func<int, ImageSource> final)
        {
            if (button == null) return;
            if (!animate || Theme.ReducedMotion)
            {
                _tracks.Remove(button);
                button.LargeImage = final(32);
                button.Image = final(16);
                return;
            }
            var track = new Track { Large = Get(key, 32, frame, final), Small = Get(key, 16, frame, final) };
            _tracks[button] = track; // replaces this button's transition in progress: goes straight to the newest state
            Step(button, track); // first frame right away, without waiting for the timer
            if (_tracks.Count == 0) return;
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(Ms / Frames) };
                _timer.Tick += (_, __) => Tick();
            }
            if (!_timer.IsEnabled) _timer.Start();
        }

        private static void Tick()
        {
            foreach (var kv in new List<KeyValuePair<PushButton, Track>>(_tracks)) Step(kv.Key, kv.Value);
            if (_tracks.Count == 0) _timer.Stop();
        }

        private static void Step(PushButton button, Track track)
        {
            try
            {
                button.LargeImage = track.Large[track.Next];
                button.Image = track.Small[track.Next];
                if (++track.Next >= Frames) _tracks.Remove(button);
            }
            catch (Exception)
            {
                // Button or ribbon already released (Revit closing): the transition is simply abandoned.
                _tracks.Remove(button);
            }
        }

        private static ImageSource[] Get(string key, int size, Func<double, int, ImageSource> frame, Func<int, ImageSource> final)
        {
            var k = key + "@" + size;
            if (_cache.TryGetValue(k, out var f)) return f;
            // shortcut: the cache is cleared entirely past 64 transitions (the queue can produce many distinct counts); enough for frames of a few KB.
            if (_cache.Count >= 64) _cache.Clear();
            f = new ImageSource[Frames];
            for (var i = 0; i < Frames - 1; i++) f[i] = frame(T[i], size);
            f[Frames - 1] = final(size); // the last frame is exactly the still icon
            _cache[k] = f;
            return f;
        }

        /// <summary>y(x) of a cubic-bezier(x1,y1,x2,y2) curve at x = (i+1)/n, solved by bisection (x is monotonic).</summary>
        private static double[] Ease(double x1, double y1, double x2, double y2, int n)
        {
            double B(double a, double b, double s) => 3 * (1 - s) * (1 - s) * s * a + 3 * (1 - s) * s * s * b + s * s * s;
            var r = new double[n];
            for (var i = 0; i < n; i++)
            {
                var x = (i + 1) / (double)n;
                double lo = 0, hi = 1;
                for (var k = 0; k < 30; k++) { var m = (lo + hi) / 2; if (B(x1, x2, m) < x) lo = m; else hi = m; }
                r[i] = Math.Min(1, Math.Max(0, B(y1, y2, (lo + hi) / 2)));
            }
            r[n - 1] = 1;
            System.Diagnostics.Debug.Assert(r[0] > 0 && r[0] < 1 && r[n - 2] <= 1, "ease-out curve out of range");
            return r;
        }
    }
}
