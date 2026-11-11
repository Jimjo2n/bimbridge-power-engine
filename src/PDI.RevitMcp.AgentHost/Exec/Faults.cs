// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Inyección de caídas para pruebas de recuperación. Formato (variable PDI_AH_FALLO o <see cref="Configure"/>):
    /// <c>punto[@op][#n][:k]</c> — cae en el punto <c>punto</c>, sólo para la operación <c>op</c> (si se da), la n-ésima vez que se pasa
    /// (por defecto la 1.ª); <c>k</c> es un argumento del punto (p. ej. tras cuántos elementos dentro de una transacción).
    /// Modo (PDI_AH_FALLO_MODO): "matar" (por defecto: el proceso se mata a sí mismo con SIGKILL, sin finally ni vaciados) o "excepcion".
    /// Antes de matar escribe una línea en PDI_AH_FALLO_MARCA (si existe) para que la prueba sepa dónde cayó.
    /// Puntos: backend.mitad_tx, backend.tras_commit, runner.tras_ejecutar, runner.en_verificacion, runner.tras_verificar,
    /// runner.en_checkpoint, runner.entre_operaciones.
    /// </summary>
    public sealed class Faults
    {
        public static Faults Current = FromEnvironment();

        private string _point, _op, _mode, _mark;
        private int _nth = 1, _hits;
        public int Arg { get; private set; } = -1;

        public static Faults FromEnvironment()
        {
            var f = new Faults();
            f.Configure(Environment.GetEnvironmentVariable("PDI_AH_FALLO"), Environment.GetEnvironmentVariable("PDI_AH_FALLO_MODO"), Environment.GetEnvironmentVariable("PDI_AH_FALLO_MARCA"));
            f.ConfigureDelay(Environment.GetEnvironmentVariable("PDI_AH_RETRASO"));
            return f;
        }

        private string _delayPoint;
        private int _delayMs;

        /// <summary>
        /// AUD-04: retraso controlado para pruebas (variable PDI_AH_RETRASO = "punto:ms"). Reproduce sin depender de la carga de la máquina
        /// un periodo largo sin renovar el lease. Puntos: runner.antes_de_ejecutar.
        /// </summary>
        public Faults ConfigureDelay(string spec)
        {
            _delayPoint = null; _delayMs = 0;
            if (string.IsNullOrWhiteSpace(spec)) return this;
            var colon = spec.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(spec.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) return this;
            _delayPoint = spec.Substring(0, colon).Trim();
            _delayMs = ms;
            return this;
        }

        /// <summary>Espera el retraso configurado si este es su punto (una sola vez).</summary>
        public void Delay(string point)
        {
            if (_delayPoint != point || _delayMs <= 0) return;
            var ms = _delayMs;
            _delayPoint = null;
            System.Threading.Thread.Sleep(ms);
        }

        public Faults Configure(string spec, string mode = null, string mark = null)
        {
            _point = null; _op = null; _nth = 1; _hits = 0; Arg = -1; _mode = mode ?? "matar"; _mark = mark;
            if (string.IsNullOrWhiteSpace(spec)) return this;
            var s = spec.Trim();
            var colon = s.LastIndexOf(':');
            if (colon > 0) { Arg = int.Parse(s.Substring(colon + 1), CultureInfo.InvariantCulture); s = s.Substring(0, colon); }
            var hash = s.LastIndexOf('#');
            if (hash > 0) { _nth = int.Parse(s.Substring(hash + 1), CultureInfo.InvariantCulture); s = s.Substring(0, hash); }
            var at = s.IndexOf('@');
            if (at > 0) { _op = s.Substring(at + 1); s = s.Substring(0, at); }
            _point = s;
            return this;
        }

        public bool Armed(string point, string op = null) => _point == point && (_op == null || _op == op);

        /// <summary>Punto de caída. No hace nada si no está armado para este punto/operación o aún no toca.</summary>
        public void Hit(string point, string op = null)
        {
            if (!Armed(point, op)) return;
            if (++_hits != _nth) return;
            Crash(point + (op == null ? "" : "@" + op));
        }

        /// <summary>Fallo SIN caída (0.8.0): devuelve true una sola vez si el punto está armado; el llamador simula el efecto
        /// (p. ej. backend.respuesta_perdida: el add-in aplicó pero la respuesta no llega).</summary>
        public bool Fire(string point, string op = null)
        {
            if (!Armed(point, op)) return false;
            if (++_hits != _nth) return false;
            _point = null;
            return true;
        }

        public void Crash(string where)
        {
            _point = null; // una sola vez
            if (_mark != null)
            {
                try
                {
                    var b = Encoding.UTF8.GetBytes(where + "\n");
                    using (var fs = new FileStream(_mark, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough)) { fs.Write(b, 0, b.Length); fs.Flush(true); }
                }
                catch (IOException) { }
            }
            if (_mode == "excepcion") throw new CrashException("caída simulada en " + where);
            Process.GetCurrentProcess().Kill();
            // Kill es asíncrono en algunos sistemas: no seguir ejecutando nada.
            System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        }
    }

    public sealed class CrashException : Exception { public CrashException(string m) : base(m) { } }
}
