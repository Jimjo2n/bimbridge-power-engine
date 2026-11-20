// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Crash injection for recovery tests. Format (variable BIMBRIDGE_AH_FAULT or <see cref="Configure"/>):
    /// <c>point[@op][#n][:k]</c> — crashes at point <c>point</c>, only for operation <c>op</c> (if given), the n-th time it is passed
    /// (the 1st by default); <c>k</c> is an argument of the point (e.g. after how many elements inside a transaction).
    /// Mode (BIMBRIDGE_AH_FAULT_MODE): "matar" (default: the process kills itself with SIGKILL, with no finally blocks or flushes) or "excepcion".
    /// Before killing it writes a line to BIMBRIDGE_AH_FAULT_MARK (if set) so the test knows where it crashed.
    /// Points: backend.mitad_tx, backend.tras_commit, runner.tras_ejecutar, runner.en_verificacion, runner.tras_verificar,
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
            f.Configure(Environment.GetEnvironmentVariable("BIMBRIDGE_AH_FAULT"), Environment.GetEnvironmentVariable("BIMBRIDGE_AH_FAULT_MODE"), Environment.GetEnvironmentVariable("BIMBRIDGE_AH_FAULT_MARK"));
            f.ConfigureDelay(Environment.GetEnvironmentVariable("BIMBRIDGE_AH_DELAY"));
            return f;
        }

        private string _delayPoint;
        private int _delayMs;

        /// <summary>
        /// AUD-04: controlled delay for tests (variable BIMBRIDGE_AH_DELAY = "point:ms"). Reproduces a long period without renewing the lease
        /// without depending on machine load. Points: runner.antes_de_ejecutar.
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

        /// <summary>Waits the configured delay if this is its point (only once).</summary>
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

        /// <summary>Crash point. Does nothing if not armed for this point/operation or if its turn has not come yet.</summary>
        public void Hit(string point, string op = null)
        {
            if (!Armed(point, op)) return;
            if (++_hits != _nth) return;
            Crash(point + (op == null ? "" : "@" + op));
        }

        /// <summary>Failure WITHOUT a crash (0.8.0): returns true only once if the point is armed; the caller simulates the effect
        /// (e.g. backend.respuesta_perdida: the add-in applied but the response never arrives).</summary>
        public bool Fire(string point, string op = null)
        {
            if (!Armed(point, op)) return false;
            if (++_hits != _nth) return false;
            _point = null;
            return true;
        }

        public void Crash(string where)
        {
            _point = null; // only once
            if (_mark != null)
            {
                try
                {
                    var b = Encoding.UTF8.GetBytes(where + "\n");
                    using (var fs = new FileStream(_mark, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough)) { fs.Write(b, 0, b.Length); fs.Flush(true); }
                }
                catch (IOException) { }
            }
            if (_mode == "excepcion") throw new CrashException("simulated crash at " + where);
            Process.GetCurrentProcess().Kill();
            // Kill is asynchronous on some systems: do not keep running anything.
            System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        }
    }

    public sealed class CrashException : Exception { public CrashException(string m) : base(m) { } }
}
