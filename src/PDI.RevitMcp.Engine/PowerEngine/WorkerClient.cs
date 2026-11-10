// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Protocol;
using static BimBridge.PowerEngine.PowerEngineContracts;

using Request = BimBridge.PowerEngine.PowerEngineContracts.Request;
using Response = BimBridge.PowerEngine.PowerEngineContracts.Response;

namespace BimBridge.PowerEngine
{
    /// <summary>
    /// Cliente de un motor externo del Power Engine (Python Worker o Rust Geometry Engine en modo IPC). Un proceso hijo aislado
    /// de Revit, sin consola, con stdin/stdout en JSON por líneas. El cliente impone por su cuenta:
    /// saludo y versión del contrato, correlación, tiempo máximo (mata el proceso y lo reinicia en el siguiente trabajo),
    /// cancelación, tamaño máximo de mensaje y vigilancia de memoria. Un trabajo a la vez por cliente (el dispatcher serializa).
    /// </summary>
    public sealed class WorkerClient : IDisposable
    {
        public readonly string Name, Exe;
        public readonly string[] Args;
        private readonly Dictionary<string, string> _env;
        private Process _proc;
        private LineReader _reader;
        private Stream _in;
        private readonly object _gate = new object();
        private bool _everStarted;
        private Task _stderrDrain;
        private StringBuilder _stderr;
        private int? _exitCode;
        private const int MaxStderrChars = 2048;
        public HelloInfo Info { get; private set; }
        public int Restarts { get; private set; }
        public string LastStartError { get; private set; }

        public WorkerClient(string name, string exe, string[] args, Dictionary<string, string> env = null)
        {
            Name = name; Exe = exe; Args = args ?? new string[0]; _env = env ?? new Dictionary<string, string>();
        }

        public bool Running => _proc != null && !_proc.HasExited;

        /// <summary>Arranca (si hace falta) y saluda. false si el motor no está disponible o es incompatible (degradación controlada).</summary>
        public bool EnsureStarted(int helloTimeoutMs = 15000)
        {
            lock (_gate)
            {
                if (Running && Info != null) return true;
                Kill();
                _exitCode = null;
                _stderr = new StringBuilder();
                LastStartError = null;
                try
                {
                    var psi = new ProcessStartInfo(Name == EnginePython ? ResolvePython(Exe) : Exe)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                        RedirectStandardError = true, WorkingDirectory = Path.GetTempPath(),
                    };
                    foreach (var a in Args) psi.Arguments += (psi.Arguments.Length > 0 ? " " : "") + Quote(a);
                    foreach (var kv in _env) psi.Environment[kv.Key] = kv.Value;
                    _proc = Process.Start(psi);
                    var stderr = _proc.StandardError;
                    var captured = _stderr;
                    _stderrDrain = DrainStderr(stderr, captured);
                    _in = _proc.StandardInput.BaseStream;
                    _reader = new LineReader(_proc.StandardOutput.BaseStream);   // uno por proceso: una lectura huérfana no toca al siguiente
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException || ex is InvalidOperationException)
                {
                    LastStartError = "arranque (" + Path.GetFileName(Exe) + "): " + ex.Message;
                    Kill();
                    return false;
                }
                var hello = new Request { Job = Hello, Args = { ["pe_min"] = (long)MinSupported, ["pe_max"] = (long)ProtocolVersion }, Limits = { TimeoutMs = helloTimeoutMs } };
                var r = CallLocked(hello, CancellationToken.None);
                if (!r.Ok)
                {
                    Kill();
                    string detail;
                    lock (_stderr) detail = _stderr.ToString().Trim();
                    LastStartError = "saludo (" + Path.GetFileName(Exe) + "): " + r.Code + ": " + r.Error
                        + (_exitCode.HasValue ? "; salida=" + _exitCode.Value : "")
                        + (detail.Length == 0 ? "" : "; stderr: " + detail);
                    return false;
                }
                var info = HelloInfo.From(r);
                if (!info.Compatible) { LastStartError = "contrato " + info.Protocol + " incompatible"; Kill(); return false; }
                Info = info;
                _everStarted = true;
                return true;
            }
        }

        /// <summary>Ejecuta un trabajo. Nunca lanza por fallos del motor: devuelve un código.</summary>
        public Response Call(Request req, CancellationToken ct = default(CancellationToken))
        {
            lock (_gate)
            {
                if (!Running || Info == null)
                {
                    if (_everStarted) Restarts++;
                    if (!EnsureStarted()) return Response.Fail(req.Id, Codes.Unavailable, Name + " no disponible: " + LastStartError);
                }
                return CallLocked(req, ct);
            }
        }

        private Response CallLocked(Request req, CancellationToken ct)
        {
            var total = Stopwatch.StartNew();
            var ser = Stopwatch.StartNew();
            var line = Json.Write(req.ToJson());
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            ser.Stop();
            if (bytes.Length > req.Limits.MaxMessageBytes) return Response.Fail(req.Id, Codes.TooLarge, "la petición ocupa " + bytes.Length + " bytes (máximo " + req.Limits.MaxMessageBytes + ")");
            if (_proc == null || _proc.HasExited) return Response.Fail(req.Id, Codes.Unavailable, Name + " no está en marcha");
            string respLine = null;
            string readErr = null;
            var ipc = Stopwatch.StartNew();
            var reader = _reader;
            var input = _in;
            var t = Task.Run(() =>
            {
                try
                {
                    input.Write(bytes, 0, bytes.Length); input.Flush();
                    respLine = reader.ReadLine(req.Limits.MaxMessageBytes, out readErr);
                }
                catch (IOException ex) { readErr = "tubería rota: " + ex.Message; }
                catch (ObjectDisposedException) { readErr = "proceso terminado"; }
            });
            int idx;
            try { idx = Task.WaitAny(new[] { t }, req.Limits.TimeoutMs, ct); }
            catch (OperationCanceledException) { Kill(); return Response.Fail(req.Id, Codes.Cancelled, "cancelado por el núcleo; el proceso de " + Name + " se detuvo y se reiniciará en el próximo trabajo"); }
            ipc.Stop();
            if (idx < 0) { Kill(); return Response.Fail(req.Id, Codes.Timeout, Name + " superó " + req.Limits.TimeoutMs + " ms; se detuvo y se reiniciará en el próximo trabajo"); }
            if (respLine == null)
            {
                var code = readErr != null && readErr.StartsWith("mensaje", StringComparison.Ordinal) ? Codes.TooLarge : Codes.EngineError;
                if (req.Job == Hello && readErr == "el proceso cerró la salida")
                    _proc.WaitForExit(150);
                Kill();
                return Response.Fail(req.Id, code, readErr ?? "sin respuesta");
            }
            var deser = Stopwatch.StartNew();
            Dictionary<string, object> obj;
            try { obj = Json.ParseObject(respLine); }
            catch (FormatException ex) { Kill(); return Response.Fail(req.Id, Codes.EngineError, "respuesta ilegible: " + ex.Message); }
            var r = ParseResponse(req.Id, obj);
            deser.Stop();
            long peak = 0;
            try { _proc.Refresh(); peak = _proc.PeakWorkingSet64 / 1024; } catch (Exception) { }
            if (peak > (long)req.Limits.MaxMemoryMb * 1024 && r.Ok)
            {
                Kill();
                return Response.Fail(req.Id, Codes.MemoryLimit, Name + " usó " + peak / 1024 + " MB (máximo " + req.Limits.MaxMemoryMb + "); resultado descartado");
            }
            total.Stop();
            r.Telemetry.Engine = Name;
            r.Telemetry.TotalMs = total.Elapsed.TotalMilliseconds;
            r.Telemetry.SerializeMs = ser.Elapsed.TotalMilliseconds + deser.Elapsed.TotalMilliseconds;
            r.Telemetry.IpcMs = Math.Max(0, ipc.Elapsed.TotalMilliseconds - r.Telemetry.EngineMs);
            r.Telemetry.BytesIn = bytes.Length;
            r.Telemetry.BytesOut = Encoding.UTF8.GetByteCount(respLine);
            r.Telemetry.PeakMemoryKb = Math.Max(r.Telemetry.PeakMemoryKb, peak);
            return r;
        }

        internal static string ResolvePython(string exe)
        {
            if (Path.DirectorySeparatorChar != '\\') return exe;

            bool IsAlias(string path) => Path.GetFullPath(path).Split('\\')
                .Any(part => string.Equals(part, "WindowsApps", StringComparison.OrdinalIgnoreCase));

            const string hint = "Use power_engine.python con la ruta absoluta de un CPython instalado fuera de WindowsApps.";
            if (Path.GetFileName(exe) != exe)
            {
                var full = Path.GetFullPath(exe);
                if (IsAlias(full)) throw new FileNotFoundException("Alias WindowsApps rechazado. " + hint);
                return full;
            }

            var file = Path.HasExtension(exe) ? exe : exe + ".exe";
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var directory = folder.Trim().Trim('"');
                if (!Path.IsPathRooted(directory)) continue;
                var candidate = Path.Combine(directory, file);
                if (!IsAlias(candidate) && File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("CPython no encontrado en PATH fuera de WindowsApps. " + hint);
        }

        private static async Task DrainStderr(StreamReader reader, StringBuilder captured)
        {
            var buffer = new char[512];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                {
                    lock (captured)
                    {
                        for (var i = 0; i < count && captured.Length < MaxStderrChars; i++)
                            captured.Append(char.IsControl(buffer[i]) ? ' ' : buffer[i]);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private void Kill()
        {
            Info = null;
            try
            {
                if (_proc != null)
                {
                    if (_proc.HasExited)
                        _exitCode = _proc.ExitCode;
                    else _proc.Kill();
                    _stderrDrain?.Wait(150);
                }
            }
            catch (Exception) { }
            _stderrDrain = null;
            try { _proc?.Dispose(); } catch (Exception) { }
            _proc = null;
        }

        /// <summary>Lee hasta '\n' con límite de tamaño (un motor que no termina la línea no agota la memoria del núcleo).</summary>
        private sealed class LineReader
        {
            private readonly Stream _s;
            private readonly byte[] _buf = new byte[1 << 16];
            private int _len, _pos;
            public LineReader(Stream s) { _s = s; }

            public string ReadLine(long max, out string error)
            {
                error = null;
                var ms = new MemoryStream();
                while (true)
                {
                    if (_pos >= _len)
                    {
                        _len = _s.Read(_buf, 0, _buf.Length);
                        _pos = 0;
                        if (_len <= 0) { error = "el proceso cerró la salida"; return null; }
                    }
                    var nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
                    var end = nl < 0 ? _len : nl;
                    ms.Write(_buf, _pos, end - _pos);
                    _pos = nl < 0 ? _len : nl + 1;
                    if (ms.Length > max) { error = "mensaje de respuesta mayor que " + max + " bytes"; return null; }
                    if (nl >= 0) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).TrimEnd('\r');
                }
            }
        }

        private static string Quote(string a) => a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        public void Dispose()
        {
            lock (_gate)
            {
                try
                {
                    if (Running)
                    {
                        var bye = Encoding.UTF8.GetBytes(Json.Write(new Request { Job = Shutdown }.ToJson()) + "\n");
                        _in.Write(bye, 0, bye.Length); _in.Flush();
                        if (!_proc.WaitForExit(1000)) Kill();
                    }
                }
                catch (Exception) { }
                Kill();
            }
        }
    }
}
