// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Bridge
{
    /// <summary>
    /// Conexión del puente con el add-in de Revit por named pipe.
    /// - Descubre los Revit abiertos por sus descriptores y limpia los muertos.
    /// - Saluda con el nombre del cliente (Claude Code, Codex…) para que el add-in abra una sesión propia.
    /// - Permite varias peticiones en vuelo, envía latidos y se reconecta sola en la siguiente llamada.
    /// La IA no ve nada de esto: sólo recibe el resultado o un estado claro.
    /// </summary>
    public sealed class RevitConnection : IDisposable
    {
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _connectionState = new object();
        private readonly ConcurrentDictionary<long, (Stream Pipe, TaskCompletionSource<Response> Waiter)> _pending =
            new ConcurrentDictionary<long, (Stream, TaskCompletionSource<Response>)>();
        private NamedPipeClientStream _pipe;
        private CancellationTokenSource _connCts;
        private long _nextId;
        private Timer _heartbeat;

        public string ClientName { get; set; } = "cliente";
        /// <summary>Versión que el cliente MCP declaró en clientInfo.</summary>
        public string ClientVersion { get; set; }
        /// <summary>
        /// Alias del agente configurado por el usuario para ESTE puente (variable BIMBRIDGE_AGENTE en la configuración MCP del cliente).
        /// El protocolo MCP no trae el título del chat: si no hay alias, el agente se registra con ctx agente=… o queda "sin nombre".
        /// </summary>
        public string AgentName { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_AGENTE");
        /// <summary>configuracion (variable de entorno) o registro (ctx agente=…, se repite al reconectar).</summary>
        public string AgentSource { get; set; } = "configuracion";
        public string Conversation { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_CONVERSACION");
        public string Model { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_MODELO");
        public Dictionary<string, object> Hello { get; private set; }
        public InstanceDescriptor Instance { get; private set; }

        /// <summary>Intervalo de latido. El add-in corta la sesión si no recibe nada en 30 s.</summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

        public bool IsConnected => _pipe != null && _pipe.IsConnected;

        /// <summary>alpha3 (AgentHost): destino FIJO elegido por el operador (pid + pipe del descriptor, o un pipe de prueba con pid 0).
        /// Si está puesto, nunca se usa otro Revit: ni el más reciente, ni la variable de entorno, ni el reenvío de lecturas.</summary>
        public (string Pipe, InstanceDescriptor Descriptor)? FixedTarget { get; set; }

        /// <summary>Revit preferido cuando hay varios abiertos (pid). 0 = el más reciente.</summary>
        public int PreferredPid { get; private set; }

        /// <summary>Versión del Revit fijado (para avisar si se cerró).</summary>
        public string PinnedVersion { get; private set; }

        /// <summary>Revit abiertos con el complemento (sin los muertos).</summary>
        public static List<InstanceDescriptor> Instances() => InstanceDescriptor.ReadAll();

        /// <summary>
        /// Cambia a otro Revit abierto: por pid, o por versión ("2024"). Devuelve null si quedó elegido, o el motivo.
        /// La conexión actual se cierra; la siguiente llamada se conecta al elegido.
        /// </summary>
        public async Task<string> SwitchToAsync(string which, CancellationToken ct)
        {
            var all = Instances();
            InstanceDescriptor pick = null;
            if (int.TryParse(which, out var n))
                pick = all.FirstOrDefault(d => d.Pid == n) ?? all.FirstOrDefault(d => d.RevitVersion != null && d.RevitVersion.StartsWith(which, StringComparison.Ordinal));
            else
                pick = all.FirstOrDefault(d => d.RevitVersion != null && d.RevitVersion.IndexOf(which ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            if (pick == null)
                return "no hay un Revit abierto que coincida con '" + which + "'. Abiertos: " +
                       (all.Count == 0 ? "ninguno" : string.Join(", ", all.Select(d => "Revit " + d.RevitVersion + " (pid " + d.Pid + ")")));
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                PreferredPid = pick.Pid;
                PinnedVersion = pick.RevitVersion;
                if (Instance == null || Instance.Pid != pick.Pid) Drop();
            }
            finally { _connectLock.Release(); }
            return null;
        }

        /// <summary>
        /// Escrituras: nunca se redirigen a otro Revit si el elegido se cerró (F06, R007, A08). 0.6.0 (H01): usa el MISMO normalizador que
        /// el add-in (OpSemantics); un modo o acción desconocidos cuentan como escritura aquí (no se redirigen) y además se rechazan antes de enviar.
        /// </summary>
        public static bool IsWriteOp(string op, IDictionary<string, object> args) =>
            OpSemantics.Classify(op, args, out _) != OpSemantics.Kind.Read;

        /// <summary>Aviso cuando una lectura se conectó a otro Revit porque el elegido se cerró (se muestra una vez).</summary>
        public string RetargetNotice { get; private set; }

        public string TakeRetargetNotice()
        {
            var n = RetargetNotice;
            RetargetNotice = null;
            return n;
        }

        public async Task<Response> CallAsync(string op, Dictionary<string, object> args, TimeSpan timeout, CancellationToken ct)
        {
            if (OpSemantics.Classify(op, args, out var invalid) == OpSemantics.Kind.Invalid)
                return Response.Fail(0, Status.Invalido, invalid);
            var write = IsWriteOp(op, args);
            var receiptQuery = op == Wire.OpJob && !string.IsNullOrEmpty(Arg.GetString(args, "intento_id"));
            var connectError = await EnsureConnectedAsync(ct, allowRetarget: !write && !receiptQuery).ConfigureAwait(false);
            if (connectError != null) return connectError;
            bool supportsReceipt;
            NamedPipeClientStream pipe;
            int? pid;
            lock (_connectionState)
            {
                supportsReceipt = Arg.GetBool(Hello, Wire.WriteReceiptFeature) == true;
                pipe = _pipe;
                pid = Instance?.Pid;
            }
            if (pipe == null) return Response.Fail(0, Status.RevitNoDisponible, "La petición no llegó a enviarse a Revit.");
            if (receiptQuery && !supportsReceipt)
                return Response.Fail(0, Status.NoSoportado, "Este add-in no admite recibos por intento. Consulte job con plan_id o revise el modelo antes de repetir.");

            var id = Interlocked.Increment(ref _nextId);
            var req = new Request
            {
                Id = id, Op = op, Args = args ?? new Dictionary<string, object>(), DeadlineMs = (int)timeout.TotalMilliseconds,
                AttemptId = write && supportsReceipt ? Guid.NewGuid().ToString("N") : null,
            };
            var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = (pipe, tcs);
            var sending = false;
            var sent = false;
            Response Unknown()
            {
                var response = Response.Fail(id, Status.Desconocido,
                    "La escritura pudo iniciarse o terminar en Revit. Consulte el intento o el plan y vuelva a leer el modelo antes de repetir.");
                if (req.AttemptId != null)
                    response.Datos = new Dictionary<string, object>
                    {
                        ["recibo"] = new Dictionary<string, object>
                        {
                            ["intento_id"] = req.AttemptId, ["estado"] = Status.Desconocido, ["pid"] = pid,
                            ["doc"] = Arg.GetString(req.Args, "doc"), ["plan_id"] = Arg.GetString(req.Args, "plan_id"),
                            ["envio_confirmado"] = sent,
                        },
                    };
                return response;
            }
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    await SendAsync(req, ct, pipe, () => sending = true).ConfigureAwait(false);
                    sent = true;
                    wait.CancelAfter(timeout);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || (sending && ex is OperationCanceledException))
                {
                    tcs.TrySetResult(write && sending ? Unknown() :
                        Response.Fail(id, Status.RevitNoDisponible, sending ? "Se perdió la conexión con Revit." : "La petición no llegó a enviarse a Revit."));
                    if (sending) Drop(pipe);
                }
                using var registration = wait.Token.Register(() => tcs.TrySetResult(write ? Unknown() :
                    Response.Fail(id, Status.RevitOcupado, "Revit no respondió dentro del plazo. Revise si hay un diálogo abierto o acote la consulta.")));
                var response = await tcs.Task.ConfigureAwait(false);
                return write && sending && response.Estado == Status.RevitNoDisponible ? Unknown() : response;
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task<Response> EnsureConnectedAsync(CancellationToken ct, bool allowRetarget = true)
        {
            if (IsConnected) return null;
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsConnected) return null;
                Drop();

                var candidates = new List<(string pipe, InstanceDescriptor d)>();
                var forced = Environment.GetEnvironmentVariable("PDI_REVITMCP_PIPE");
                if (FixedTarget != null) candidates.Add((FixedTarget.Value.Pipe, FixedTarget.Value.Descriptor));
                else if (!string.IsNullOrEmpty(forced)) candidates.Add((forced, null));
                else
                {
                    var all = InstanceDescriptor.ReadAll();
                    // Una vez elegido un Revit (al conectar por primera vez o con ctx instancia=…), queda FIJO.
                    // Si se cerró: las escrituras fallan con destino_no_disponible; las lecturas sólo pasan a otro Revit de la misma versión
                    // si hay exactamente uno, y lo avisan. Nunca se elige "el más reciente" en silencio para escribir.
                    var pinned = all.Where(x => x.Pid == PreferredPid).ToList();
                    if (PreferredPid != 0 && pinned.Count == 0)
                    {
                        var abiertos = all.Count == 0 ? "ninguno" : string.Join(", ", all.Select(d => "Revit " + d.RevitVersion + " (pid " + d.Pid + ")"));
                        var same = all.Where(x => x.RevitVersion == PinnedVersion).ToList();
                        if (!allowRetarget || same.Count != 1)
                            return Response.Fail(0, Status.DestinoNoDisponible,
                                "El Revit elegido (" + PinnedVersion + ", pid " + PreferredPid + ") ya no está abierto. No se redirige a otro" +
                                (allowRetarget ? "" : " para escribir") + ". Abiertos: " + abiertos + ". Elija uno con ctx instancia=<pid o versión> y vuelva a leer antes de cambiar nada.");
                        RetargetNotice = "el Revit " + PinnedVersion + " (pid " + PreferredPid + ") se cerró; esta lectura se hizo en el otro Revit " + PinnedVersion + " abierto (pid " + same[0].Pid + "). " +
                                         "Los handles dN, conjuntos y planes anteriores no valen aquí.";
                        PreferredPid = same[0].Pid;
                        pinned = same;
                    }
                    if (PreferredPid != 0) foreach (var d in pinned) candidates.Add((d.PipeName, d));
                    else foreach (var d in all) candidates.Add((d.PipeName, d)); // primera conexión: el más reciente, y queda fijo
                }

                if (candidates.Count == 0)
                    return Response.Fail(0, Status.RevitNoDisponible, "No hay ningún Revit abierto con el complemento BIMBRIDGE - Power Engine cargado.");

                string lastError = null;
                foreach (var (pipeName, d) in candidates)
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException || ex is IOException)
                    {
                        pipe.Dispose();
                        lastError = ex.Message;
                        continue;
                    }

                    // 0.6.1: el servidor del pipe debe ser el proceso de Revit anunciado (evita que otro proceso ocupe el nombre antes).
                    var who = PipeServerCheck.Verify(pipe, d?.Pid ?? 0);
                    if (who != null) { pipe.Dispose(); lastError = who; continue; }

                    // Saludo
                    var hello = new Request
                    {
                        Id = 0,
                        Op = Wire.OpHello,
                        Args = new Dictionary<string, object>
                        {
                            ["protocol"] = (long)Wire.ProtocolVersion,
                            [Wire.LargeResultFeature] = true,
                            ["client"] = ClientName,
                            ["client_version"] = ClientVersion,
                            ["agente"] = AgentName,
                            ["agente_fuente"] = string.IsNullOrWhiteSpace(AgentName) ? null : AgentSource,
                            ["conversacion"] = Conversation,
                            ["modelo"] = Model,
                            ["pid"] = (long)Environment.ProcessId,
                        },
                    };
                    string text;
                    try
                    {
                        await Frame.WriteAsync(pipe, Json.Write(hello), ct).ConfigureAwait(false);
                        text = await ReadWithTimeout(pipe, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                        // Revit se cerró durante el saludo: es "no disponible", no un error de la herramienta.
                        pipe.Dispose();
                        lastError = ex.Message;
                        continue;
                    }
                    if (text == null) { pipe.Dispose(); lastError = "Revit no respondió al saludo"; continue; }
                    var resp = Response.FromJson(text);
                    if (resp.Estado != Status.Ok) { pipe.Dispose(); return resp; }

                    lock (_connectionState)
                    {
                        _connCts = new CancellationTokenSource();
                        var token = _connCts.Token;
                        Hello = resp.Datos as Dictionary<string, object> ?? new Dictionary<string, object>();
                        Instance = d;
                        if (d != null) { PreferredPid = d.Pid; PinnedVersion = d.RevitVersion; }
                        _pipe = pipe;
                        _ = Task.Run(() => ReadLoop(pipe, token));
                        _heartbeat = new Timer(_ => _ = PingAsync(pipe), null, HeartbeatInterval, HeartbeatInterval);
                    }
                    return null;
                }
                return Response.Fail(0, Status.RevitNoDisponible, "No se pudo conectar con Revit (" + lastError + ").");
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task PingAsync(NamedPipeClientStream pipe)
        {
            try
            {
                var id = Interlocked.Increment(ref _nextId);
                await SendAsync(new Request { Id = id, Op = Wire.OpPing }, CancellationToken.None, pipe, null).ConfigureAwait(false);
            }
            catch (Exception) { Drop(pipe); }
        }

        private async Task SendAsync(Request req, CancellationToken ct, NamedPipeClientStream pipe, Action starting)
        {
            if (pipe == null) throw new IOException("sin conexión");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(pipe, _pipe)) throw new IOException("la conexión cambió antes del envío");
                var json = Json.Write(req);
                starting?.Invoke();
                await Frame.WriteAsync(pipe, json, ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private async Task ReadLoop(Stream pipe, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var text = await Frame.ReadAsync(pipe, ct).ConfigureAwait(false);
                    if (text == null) break;
                    Response resp;
                    try { resp = Response.FromJson(text); } catch (FormatException) { continue; }
                    if (_pending.TryGetValue(resp.Id, out var pending) && ReferenceEquals(pending.Pipe, pipe))
                        pending.Waiter.TrySetResult(resp);
                }
            }
            catch (Exception) { }
            Drop(pipe);
        }

        private static async Task<string> ReadWithTimeout(Stream s, TimeSpan timeout, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var read = Frame.ReadAsync(s, cts.Token);
                var done = await Task.WhenAny(read, Task.Delay(timeout, ct)).ConfigureAwait(false);
                if (done != read) { cts.Cancel(); return null; }
                return await read.ConfigureAwait(false);
            }
        }

        private void Drop(Stream expected = null)
        {
            Stream closed;
            lock (_connectionState)
            {
                if (expected != null && !ReferenceEquals(expected, _pipe)) return;
                closed = _pipe;
                _pipe = null;
                _heartbeat?.Dispose();
                _heartbeat = null;
                _connCts?.Cancel();
                _connCts?.Dispose();
                _connCts = null;
                closed?.Dispose();
            }
            foreach (var kv in _pending)
                if (ReferenceEquals(kv.Value.Pipe, closed))
                    kv.Value.Waiter.TrySetResult(Response.Fail(kv.Key, Status.RevitNoDisponible, "Se perdió la conexión con Revit."));
        }

        public void Dispose() => Drop();
    }
}
