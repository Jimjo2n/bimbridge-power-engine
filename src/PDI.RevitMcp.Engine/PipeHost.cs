// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>Lo que el add-in de Revit (o una prueba) implementa para atender peticiones.</summary>
    public interface IRequestHandler
    {
        /// <summary>Datos que se devuelven en el saludo (versión de Revit, documento, rev…).</summary>
        Dictionary<string, object> HelloInfo();

        /// <summary>Atiende una petición. Nunca debe lanzar: los errores van como Response.</summary>
        Task<Response> HandleAsync(Session session, Request request, CancellationToken ct);

        /// <summary>La conexión se cerró: soltar turnos, cancelar trabajos en cola de esa sesión.</summary>
        void OnSessionClosed(Session session);
    }

    /// <summary>
    /// Servidor de named pipe con varias instancias (sección 6). Cada puente que se conecta
    /// abre una sesión propia; varias peticiones pueden estar en vuelo en la misma conexión.
    /// Corre en hilos de fondo: aquí nunca se toca la API de Revit.
    /// </summary>
    public sealed class PipeHost : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<string, NamedPipeServerStream> _factory;
        private readonly SessionRegistry _sessions;
        private readonly IRequestHandler _handler;
        private readonly EngineConfig _config;
        private readonly Journal _journal;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _pendingLock = new object();
        private NamedPipeServerStream _pending;
        private Task _acceptLoop;

        public PipeHost(string pipeName, Func<string, NamedPipeServerStream> factory, SessionRegistry sessions,
            IRequestHandler handler, EngineConfig config, Journal journal = null, Action<string> log = null)
        {
            _pipeName = pipeName;
            _factory = factory ?? DefaultFactory;
            _sessions = sessions;
            _handler = handler;
            _config = config;
            _journal = journal;
            _log = log ?? (_ => { });
        }

        public string PipeName => _pipeName;

        /// <summary>Fábrica sin ACL, sólo para pruebas. El add-in pasa la suya con ACL de usuario actual.</summary>
        public static NamedPipeServerStream DefaultFactory(string name) =>
            new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        public void Start()
        {
            _acceptLoop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = _factory(_pipeName);
                    lock (_pendingLock) _pending = server;
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    lock (_pendingLock) _pending = null;
                    var s = server;
                    server = null;
                    _ = Task.Run(() => ServeConnection(s));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { if (_stop.IsCancellationRequested) break; }
                catch (Exception ex)
                {
                    _log("pipe: error al aceptar conexión: " + ex.Message);
                    try { await Task.Delay(500, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private async Task ServeConnection(NamedPipeServerStream stream)
        {
            Session session = null;
            Task<string> pendingRead = null;
            string closeReason = null;
            var writeLock = new SemaphoreSlim(1, 1);
            using (var connCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            using (stream)
            {
                try
                {
                    // 1) Saludo obligatorio en 10 s.
                    var helloText = await ReadWithTimeout(stream, 10000, connCts).ConfigureAwait(false);
                    if (helloText == null) return;
                    var hello = Request.FromJson(helloText);
                    if (hello.Op != Wire.OpHello)
                    {
                        await Send(stream, writeLock, Response.Fail(hello.Id, Status.Invalido, "primero hay que saludar (hello)"), connCts.Token).ConfigureAwait(false);
                        return;
                    }
                    var proto = Arg.GetLong(hello.Args, "protocol") ?? 0;
                    if (proto != Wire.ProtocolVersion)
                    {
                        await Send(stream, writeLock, Response.Fail(hello.Id, Status.Incompatible,
                            "versión de protocolo del puente " + proto + ", del add-in " + Wire.ProtocolVersion + ": actualice ambos a la misma versión"), connCts.Token).ConfigureAwait(false);
                        return;
                    }
                    session = _sessions.Open(Arg.GetString(hello.Args, "client"), (int)(Arg.GetLong(hello.Args, "pid") ?? 0));
                    // La ACL de la tubería sólo admite al usuario de Windows de este Revit: ése es el principal verificado.
                    _sessions.Describe(session, Arg.GetString(hello.Args, "client_version"), Arg.GetString(hello.Args, "agente"),
                        Arg.GetString(hello.Args, "conversacion"), Arg.GetString(hello.Args, "modelo"),
                        Arg.GetString(hello.Args, "agente_fuente") == AgentIdentity.SourceConfig ? AgentIdentity.SourceConfig
                            : Arg.GetString(hello.Args, "agente_fuente") == AgentIdentity.SourceRegister ? AgentIdentity.SourceRegister : AgentIdentity.SourceHello,
                        Environment.UserName);
                    var info = SafeHelloInfo();
                    info["protocol"] = (long)Wire.ProtocolVersion;
                    info["sesion"] = session.Id;
                    info["agente"] = session.AgentName;
                    info["identidad"] = session.AgentName == null ? "insuficiente" : "declarada";
                    await Send(stream, writeLock, new Response { Id = hello.Id, Estado = Status.Ok, Datos = info }, connCts.Token).ConfigureAwait(false);
                    _journal?.Log(session.Id, "conectar", Status.Ok, 0, new Dictionary<string, object>
                    {
                        ["cliente"] = session.ClientName, ["pid"] = (long)session.ClientPid, ["agente"] = session.AgentName, ["identidad_fuente"] = session.IdentitySource,
                    });
                    _log("sesión abierta: " + session.Id);

                    // 2) Peticiones. Sin latido ni petición en HeartbeatTimeoutMs, la sesión se da por muerta.
                    while (!connCts.IsCancellationRequested)
                    {
                        // Issue #1: el umbral de inactividad sólo cierra sesiones OCIOSAS. Con solicitudes en curso se sigue esperando la
                        // misma lectura: la duración de una escritura no es abandono. Una conexión realmente rota termina la lectura con
                        // EOF o IOException, y eso sí cierra la sesión (las escrituras en curso quedan con su propia evidencia y su estado).
                        pendingRead = pendingRead ?? Frame.ReadAsync(stream, connCts.Token);
                        var waited = await Task.WhenAny(pendingRead, Task.Delay(_config.HeartbeatTimeoutMs, connCts.Token)).ConfigureAwait(false);
                        if (waited != pendingRead)
                        {
                            if (!SessionLifecycle.ShouldExpireOnIdle(session.Active)) continue;
                            closeReason = "inactividad";
                            connCts.Cancel();
                            stream.Dispose();
                            try { await pendingRead.ConfigureAwait(false); } catch (Exception) { }
                            break;
                        }
                        var text = await pendingRead.ConfigureAwait(false);
                        pendingRead = null;
                        if (text == null) break;
                        Request req;
                        try { req = Request.FromJson(text); }
                        catch (FormatException ex)
                        {
                            await Send(stream, writeLock, Response.Fail(0, Status.Invalido, ex.Message), connCts.Token).ConfigureAwait(false);
                            continue;
                        }
                        if (req.Op == Wire.OpPing)
                        {
                            await Send(stream, writeLock, new Response { Id = req.Id, Estado = Status.Ok }, connCts.Token).ConfigureAwait(false);
                            continue;
                        }
                        // El latido mantiene viva la conexión (timeout de lectura) pero NO cuenta como actividad del agente:
                        // si contara, un agente ocioso aparecería "activo" para siempre en el panel.
                        _sessions.Touch(session);
                        var s = session;
                        var traceId = s.Id + ":" + req.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var reqDoc = Arg.GetString(req.Args, Wire.DocArg);
                        _sessions.RequestStarted(s, ToolName(req), reqDoc);
                        _ = Task.Run(async () =>
                        {
                            var sw = Stopwatch.StartNew();
                            Response resp;
                            // Plazo real: si el puente deja de esperar, el trabajo pendiente en Revit también se cancela
                            // (antes seguía corriendo y bloqueaba Revit; ver prueba de check views=rcp, 2026-10-15).
                            using (var reqCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                            {
                                if (req.DeadlineMs > 0) reqCts.CancelAfter(req.DeadlineMs + DeadlineGraceMs);
                                try
                                {
                                    resp = await _handler.HandleAsync(s, req, reqCts.Token).ConfigureAwait(false)
                                           ?? Response.Fail(req.Id, Status.Error, "respuesta vacía");
                                }
                                catch (Exception ex)
                                {
                                    resp = Response.Fail(req.Id, Status.Error, ex.GetType().Name + ": " + ex.Message);
                                }
                            }
                            resp.Id = req.Id;
                            var ms = sw.ElapsedMilliseconds;
                            var failed = resp.Estado == Status.Error || resp.Estado == Status.Desconocido || resp.Estado == Status.Invalido;
                            _sessions.RequestFinished(s, ms, failed);
                            var extra = JournalExtra(resp) ?? new Dictionary<string, object>();
                            // Atribución: cada línea del diario lleva el agente, el cliente, la traza y el documento pedido.
                            extra["agente"] = s.AgentName;
                            extra["cliente"] = s.ClientName;
                            extra["traza"] = traceId;
                            if (reqDoc != null) extra["doc"] = reqDoc;
                            _journal?.Log(s.Id, ToolName(req), resp.Estado, ms, extra);
                            try { await Send(stream, writeLock, resp, connCts.Token).ConfigureAwait(false); }
                            catch (Exception) { /* conexión cerrada mientras se trabajaba */ }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { _log("pipe: conexión terminada por error: " + ex.Message); }
                finally
                {
                    var lost = !_stop.IsCancellationRequested;
                    connCts.Cancel();
                    if (session != null)
                    {
                        _sessions.Close(session, closeReason ?? (lost ? "conexion_cerrada" : "revit_cerrando"));
                        try { _handler.OnSessionClosed(session); } catch (Exception ex) { _log("OnSessionClosed: " + ex.Message); }
                        _journal?.Log(session.Id, "desconectar", Status.Ok, 0);
                        _log("sesión cerrada: " + session.Id);
                    }
                }
            }
        }

        /// <summary>Margen sobre el plazo del puente antes de cancelar dentro de Revit.</summary>
        public const int DeadlineGraceMs = 1000;

        /// <summary>Nombre de la herramienta que vio la IA ("get" viaja como query con _tool=get).</summary>
        internal static string ToolName(Request req)
        {
            var t = Arg.GetString(req.Args, Wire.ToolArg);
            return string.IsNullOrEmpty(t) ? req.Op : t;
        }

        private static Dictionary<string, object> JournalExtra(Response resp)
        {
            if (resp.Estado == Status.Ok || string.IsNullOrEmpty(resp.Aviso)) return null;
            var a = resp.Aviso.Length > 200 ? resp.Aviso.Substring(0, 200) + "…" : resp.Aviso;
            return new Dictionary<string, object> { ["aviso"] = a };
        }

        private Dictionary<string, object> SafeHelloInfo()
        {
            try { return _handler.HelloInfo() ?? new Dictionary<string, object>(); }
            catch (Exception ex) { return new Dictionary<string, object> { ["aviso"] = ex.Message }; }
        }

        /// <summary>
        /// Lectura con límite de tiempo que funciona igual en .NET Framework 4.8 (donde los pipes
        /// pueden ignorar el CancellationToken): si se vence, se cierra el stream.
        /// </summary>
        private static async Task<string> ReadWithTimeout(Stream stream, int timeoutMs, CancellationTokenSource cts)
        {
            var read = Frame.ReadAsync(stream, cts.Token);
            var done = await Task.WhenAny(read, Task.Delay(timeoutMs, cts.Token)).ConfigureAwait(false);
            if (done != read)
            {
                cts.Cancel();
                stream.Dispose();
                try { await read.ConfigureAwait(false); } catch (Exception) { }
                return null;
            }
            return await read.ConfigureAwait(false);
        }

        private static async Task Send(Stream stream, SemaphoreSlim writeLock, Response resp, CancellationToken ct)
        {
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await Frame.WriteAsync(stream, Json.Write(resp), ct).ConfigureAwait(false); }
            finally { writeLock.Release(); }
        }

        public void Dispose()
        {
            _stop.Cancel();
            lock (_pendingLock) { _pending?.Dispose(); _pending = null; }
            try { _acceptLoop?.Wait(2000); } catch (AggregateException) { }
        }
    }
}
