// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>What the Revit add-in (or a test) implements to serve requests.</summary>
    public interface IRequestHandler
    {
        /// <summary>Data returned in the hello (Revit version, document, rev…).</summary>
        Dictionary<string, object> HelloInfo();

        /// <summary>Serves a request. Must never throw: errors go back as a Response.</summary>
        Task<Response> HandleAsync(Session session, Request request, CancellationToken ct);

        /// <summary>The connection closed: release turns, cancel that session's queued jobs.</summary>
        void OnSessionClosed(Session session);
    }

    /// <summary>
    /// Named pipe server with several instances (section 6). Each bridge that connects
    /// opens its own session; several requests can be in flight on the same connection.
    /// Runs on background threads: the Revit API is never touched here.
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

        /// <summary>Factory without ACL, for tests only. The add-in passes its own with a current-user ACL.</summary>
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
                    _log("pipe: error accepting connection: " + ex.Message);
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
                    // 1) Mandatory hello within 10 s.
                    var helloText = await ReadWithTimeout(stream, 10000, connCts).ConfigureAwait(false);
                    if (helloText == null) return;
                    var hello = Request.FromJson(helloText);
                    if (hello.Op != Wire.OpHello)
                    {
                        await Send(stream, writeLock, Response.Fail(hello.Id, Status.Invalido, "hello must come first"), connCts.Token).ConfigureAwait(false);
                        return;
                    }
                    var proto = Arg.GetLong(hello.Args, "protocol") ?? 0;
                    if (proto != Wire.ProtocolVersion)
                    {
                        await Send(stream, writeLock, Response.Fail(hello.Id, Status.Incompatible,
                            "bridge protocol version " + proto + ", add-in " + Wire.ProtocolVersion + ": update both to the same version"), connCts.Token).ConfigureAwait(false);
                        return;
                    }
                    session = _sessions.Open(Arg.GetString(hello.Args, "client"), (int)(Arg.GetLong(hello.Args, "pid") ?? 0));
                    // The pipe ACL only admits the Windows user of this Revit: that is the verified principal.
                    _sessions.Describe(session, Arg.GetString(hello.Args, "client_version"), Arg.GetString(hello.Args, "agente"),
                        Arg.GetString(hello.Args, "conversacion"), Arg.GetString(hello.Args, "modelo"),
                        Arg.GetString(hello.Args, "agente_fuente") == AgentIdentity.SourceConfig ? AgentIdentity.SourceConfig
                            : Arg.GetString(hello.Args, "agente_fuente") == AgentIdentity.SourceRegister ? AgentIdentity.SourceRegister : AgentIdentity.SourceHello,
                        Environment.UserName);
                    var info = SafeHelloInfo();
                    var largeResults = Arg.GetBool(hello.Args, Wire.LargeResultFeature) == true;
                    info[Wire.LargeResultFeature] = largeResults;
                    info["protocol"] = (long)Wire.ProtocolVersion;
                    info["sesion"] = session.Id;
                    info["agente"] = session.AgentName;
                    info["identidad"] = session.AgentName == null ? "insuficiente" : "declarada";
                    await Send(stream, writeLock, new Response { Id = hello.Id, Estado = Status.Ok, Datos = info }, connCts.Token).ConfigureAwait(false);
                    _journal?.Log(session.Id, "conectar", Status.Ok, 0, new Dictionary<string, object>
                    {
                        ["cliente"] = session.ClientName, ["pid"] = (long)session.ClientPid, ["agente"] = session.AgentName, ["identidad_fuente"] = session.IdentitySource,
                    });
                    _log("session opened: " + session.Id);

                    // 2) Requests. With no heartbeat or request within HeartbeatTimeoutMs, the session is considered dead.
                    while (!connCts.IsCancellationRequested)
                    {
                        // Issue #1: the inactivity threshold only closes IDLE sessions. With requests in progress it keeps waiting on the
                        // same read: the duration of a write is not abandonment. A truly broken connection ends the read with
                        // EOF or IOException, and that does close the session (writes in progress keep their own evidence and status).
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
                        // The heartbeat keeps the connection alive (read timeout) but does NOT count as agent activity:
                        // if it did, an idle agent would show as "active" forever in the panel.
                        _sessions.Touch(session);
                        var s = session;
                        var corr = Correlation.For(s, req);
                        var reqDoc = Arg.GetString(req.Args, Wire.DocArg);
                        // Issue #3 (PR #22 review): without an agent name only hello, heartbeat and registration. No reads or changes.
                        // The refusal is recorded in the journal with its correlation.
                        var anonymous = s.AgentName == null;
                        if (_config.IdentityRequired && anonymous && IdentityPolicy.RequiresIdentity(req))
                        {
                            var refused = Response.Fail(req.Id, Status.Rechazado, IdentityPolicy.RefusalMessage(s));
                            var rj = new Dictionary<string, object> { ["aviso"] = "identidad_insuficiente", ["cliente"] = s.ClientName };
                            corr.WriteTo(rj);
                            if (reqDoc != null) rj["doc"] = reqDoc;
                            _journal?.Log(s.Id, ToolName(req), refused.Estado, 0, rj);
                            await Send(stream, writeLock, refused, connCts.Token).ConfigureAwait(false);
                            continue;
                        }
                        _sessions.RequestStarted(s, ToolName(req), reqDoc);
                        _ = Task.Run(async () =>
                        {
                            Correlation.Current = corr;
                            var sw = Stopwatch.StartNew();
                            Response resp;
                            // Real deadline: if the bridge stops waiting, the pending work in Revit is cancelled too
                            // (it used to keep running and block Revit; see the check views=rcp test, 2026-10-15).
                            using (var reqCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                            {
                                if (req.DeadlineMs > 0) reqCts.CancelAfter(req.DeadlineMs + DeadlineGraceMs);
                                try
                                {
                                    resp = await _handler.HandleAsync(s, req, reqCts.Token).ConfigureAwait(false)
                                           ?? Response.Fail(req.Id, Status.Error, "empty response");
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
                            // Attribution: every journal line carries the agent, the client, the trace and the requested document.
                            extra["agente"] = s.AgentName;
                            extra["cliente"] = s.ClientName;
                            extra["traza"] = corr.TraceId;
                            corr.Complete(req, resp);
                            corr.WriteTo(extra);
                            // Explicit legacy mode (identidad_obligatoria=false): the anonymous operation is marked as such in the journal.
                            if (anonymous && !IdentityPolicy.IsRegistration(req)) extra["identidad"] = _config.IdentityRequired ? "sin_nombre" : "sin_nombre_modo_heredado";
                            if (reqDoc != null) extra["doc"] = reqDoc;
                            _journal?.Log(s.Id, ToolName(req), resp.Estado, ms, extra);
                            try { await Send(stream, writeLock, resp, connCts.Token, largeResults).ConfigureAwait(false); }
                            catch (Exception) { /* connection closed while working */ }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { _log("pipe: connection ended by error: " + ex.Message); }
                finally
                {
                    var lost = !_stop.IsCancellationRequested;
                    connCts.Cancel();
                    if (session != null)
                    {
                        _sessions.Close(session, closeReason ?? (lost ? "conexion_cerrada" : "revit_cerrando"));
                        try { _handler.OnSessionClosed(session); } catch (Exception ex) { _log("OnSessionClosed: " + ex.Message); }
                        _journal?.Log(session.Id, "desconectar", Status.Ok, 0);
                        _log("session closed: " + session.Id);
                    }
                }
            }
        }

        /// <summary>Margin over the bridge deadline before cancelling inside Revit.</summary>
        public const int DeadlineGraceMs = 1000;

        /// <summary>Name of the tool the AI saw ("get" travels as query with _tool=get).</summary>
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
        /// Time-limited read that works the same on .NET Framework 4.8 (where pipes
        /// may ignore the CancellationToken): if it expires, the stream is closed.
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

        private static async Task Send(Stream stream, SemaphoreSlim writeLock, Response resp, CancellationToken ct, bool largeResults = false)
        {
            var json = FitResponse(resp, largeResults, text => ResultStore.Save(text, "a"));
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try { await Frame.WriteAsync(stream, json, ct).ConfigureAwait(false); }
            finally { writeLock.Release(); }
        }

        public static string FitResponse(Response response, bool largeResults, Func<string, string> save)
        {
            var json = Json.Write(response);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            var budget = largeResults ? 1024 * 1024 : Frame.MaxBytes;
            if (bytes <= budget) return json;

            var summary = new Dictionary<string, object> { ["bytes_respuesta"] = (long)bytes };
            if (response.Datos is Dictionary<string, object> data)
                foreach (var key in new[] { "total", "page", "pages", "cursor", "rev_instantanea", "recibo", "job_id", "plan_id" })
                    if (data.TryGetValue(key, out var field)) summary[key] = field;
            if (largeResults)
            {
                try
                {
                    summary["res"] = save(ResultStore.FragmentJson(json));
                    summary["formato_res"] = ResultStore.JsonFragmentFormat;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    summary["aviso_resultado"] = "Could not save the detail: " + ex.Message;
                }
            }
            else summary["aviso_resultado"] = "The response exceeds the bridge limit. Narrow the read or restart the conversation with the updated bridge.";
            var result = new Response { Id = response.Id, Estado = response.Estado, Rev = response.Rev, Aviso = response.Aviso, Datos = summary };
            var fitted = Json.Write(result);
            if (System.Text.Encoding.UTF8.GetByteCount(fitted) <= budget) return fitted;
            result.Aviso = ResultTable.Trim(result.Aviso, 2000);
            fitted = Json.Write(result);
            if (System.Text.Encoding.UTF8.GetByteCount(fitted) <= budget) return fitted;
            foreach (var key in new[] { "cursor", "recibo", "job_id", "plan_id" })
                if (summary.TryGetValue(key, out var field) && System.Text.Encoding.UTF8.GetByteCount(Json.Write(field)) > 16384)
                    summary.Remove(key);
            return Json.Write(result);
        }

        public void Dispose()
        {
            _stop.Cancel();
            lock (_pendingLock) { _pending?.Dispose(); _pending = null; }
            try { _acceptLoop?.Wait(2000); } catch (AggregateException) { }
        }
    }
}
