// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Transición corta de un botón de la cinta cuando su estado visible cambia: 5 cuadros en 180 ms con la curva ease-out de
    /// Theme, en el hilo de Revit. Nunca en bucle; un cambio nuevo sustituye al que está en curso (no se encola); con
    /// movimiento reducido se pone la imagen final al momento. Sin cambios no hace nada.
    /// </summary>
    internal static class RibbonMotion
    {
        private const int Frames = 5, Ms = 180;
        /// <summary>Progreso de cada cuadro: cubic-bezier(0.23, 1, 0.32, 1) en los tiempos (i+1)/Frames. El último es 1.</summary>
        internal static readonly double[] T = Ease(0.23, 1, 0.32, 1, Frames);

        private sealed class Track { public ImageSource[] Large, Small; public int Next; }
        private static readonly Dictionary<PushButton, Track> _tracks = new Dictionary<PushButton, Track>();
        private static readonly Dictionary<string, ImageSource[]> _cache = new Dictionary<string, ImageSource[]>();
        private static DispatcherTimer _timer;

        /// <summary>
        /// Lleva <paramref name="button"/> a su imagen final. <paramref name="key"/> identifica la transición (desde→hacia) para
        /// reutilizar los cuadros; <paramref name="frame"/>(t, tamaño) dibuja uno intermedio; <paramref name="final"/>(tamaño) el estado quieto.
        /// <paramref name="animate"/> = false (primera pintura) pone la imagen final sin transición.
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
            _tracks[button] = track; // sustituye la transición en curso de este botón: va directo al estado más nuevo
            Step(button, track); // primer cuadro ya, sin esperar al temporizador
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
                // Botón o cinta ya liberados (Revit cerrándose): se abandona la transición sin más.
                _tracks.Remove(button);
            }
        }

        private static ImageSource[] Get(string key, int size, Func<double, int, ImageSource> frame, Func<int, ImageSource> final)
        {
            var k = key + "@" + size;
            if (_cache.TryGetValue(k, out var f)) return f;
            // shortcut: la caché se vacía entera al pasar de 64 transiciones (la cola puede dar muchas cuentas distintas); suficiente para cuadros de pocos KB.
            if (_cache.Count >= 64) _cache.Clear();
            f = new ImageSource[Frames];
            for (var i = 0; i < Frames - 1; i++) f[i] = frame(T[i], size);
            f[Frames - 1] = final(size); // el último cuadro es exactamente el icono quieto
            _cache[k] = f;
            return f;
        }

        /// <summary>y(x) de una curva cubic-bezier(x1,y1,x2,y2) en x = (i+1)/n, resuelta por bisección (x es monótona).</summary>
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
            System.Diagnostics.Debug.Assert(r[0] > 0 && r[0] < 1 && r[n - 2] <= 1, "curva ease-out fuera de rango");
            return r;
        }
    }
}
