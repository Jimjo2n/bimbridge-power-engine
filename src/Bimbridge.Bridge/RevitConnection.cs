// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Protocol;

namespace Bimbridge.Bridge
{
    /// <summary>
    /// Bridge connection to the Revit add-in over a named pipe.
    /// - Discovers open Revit instances by their descriptors and cleans up dead ones.
    /// - Says hello with the client name (Claude Code, Codex…) so the add-in opens a session of its own.
    /// - Allows several requests in flight, sends heartbeats and reconnects on its own on the next call.
    /// The AI sees none of this: it only gets the result or a clear status.
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

        public string ClientName { get; set; } = "client";
        /// <summary>Version the MCP client declared in clientInfo.</summary>
        public string ClientVersion { get; set; }
        /// <summary>
        /// Agent alias configured by the user for THIS bridge (BIMBRIDGE_AGENT variable in the client's MCP configuration).
        /// The MCP protocol does not carry the chat title: without an alias, the agent registers with ctx agente=… or stays "unnamed".
        /// </summary>
        public string AgentName { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_AGENT");
        /// <summary>configuracion (environment variable) or registro (ctx agente=…, repeated on reconnect).</summary>
        public string AgentSource { get; set; } = "configuration";
        public string Conversation { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_CONVERSATION");
        public string Model { get; set; } = Environment.GetEnvironmentVariable("BIMBRIDGE_MODEL");
        public Dictionary<string, object> Hello { get; private set; }
        public InstanceDescriptor Instance { get; private set; }

        /// <summary>Heartbeat interval. The add-in drops the session if it receives nothing for 30 s.</summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

        public bool IsConnected => _pipe != null && _pipe.IsConnected;

        /// <summary>alpha3 (AgentHost): FIXED target chosen by the operator (descriptor pid + pipe, or a test pipe with pid 0).
        /// When set, no other Revit is ever used: not the most recent, not the environment variable, not read forwarding.</summary>
        public (string Pipe, InstanceDescriptor Descriptor)? FixedTarget { get; set; }

        /// <summary>Preferred Revit when several are open (pid). 0 = the most recent.</summary>
        public int PreferredPid { get; private set; }

        /// <summary>Version of the pinned Revit (to warn if it closed).</summary>
        public string PinnedVersion { get; private set; }

        /// <summary>Open Revit instances with the add-in (dead ones excluded).</summary>
        public static List<InstanceDescriptor> Instances() => InstanceDescriptor.ReadAll();

        /// <summary>
        /// Switches to another open Revit: by pid, or by version ("2024"). Returns null if it was selected, or the reason.
        /// The current connection is closed; the next call connects to the selected one.
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
                return "no open Revit matches '" + which + "'. Open: " +
                       (all.Count == 0 ? "none" : string.Join(", ", all.Select(d => "Revit " + d.RevitVersion + " (pid " + d.Pid + ")")));
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
        /// Writes: never redirected to another Revit if the selected one closed (F06, R007, A08). 0.6.0 (H01): uses the SAME normalizer as
        /// the add-in (OpSemantics); an unknown mode or action counts as a write here (not redirected) and is also rejected before sending.
        /// </summary>
        public static bool IsWriteOp(string op, IDictionary<string, object> args) =>
            OpSemantics.Classify(op, args, out _) != OpSemantics.Kind.Read;

        /// <summary>Warning when a read connected to another Revit because the selected one closed (shown once).</summary>
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
                return Response.Fail(0, Status.Invalid, invalid);
            var write = IsWriteOp(op, args);
            var receiptQuery = op == Wire.OpJob && !string.IsNullOrEmpty(Arg.GetString(args, "attempt_id"));
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
            if (pipe == null) return Response.Fail(0, Status.RevitUnavailable, "The request was never sent to Revit.");
            if (receiptQuery && !supportsReceipt)
                return Response.Fail(0, Status.NotSupported, "This add-in does not support per-attempt receipts. Query job with plan_id or check the model before repeating.");

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
                var response = Response.Fail(id, Status.Unknown,
                    "The write may have started or finished in Revit. Query the attempt or the plan and re-read the model before repeating.");
                if (req.AttemptId != null)
                    response.Data = new Dictionary<string, object>
                    {
                        ["receipt"] = new Dictionary<string, object>
                        {
                            ["attempt_id"] = req.AttemptId, ["status"] = Status.Unknown, ["pid"] = pid,
                            ["doc"] = Arg.GetString(req.Args, "doc"), ["plan_id"] = Arg.GetString(req.Args, "plan_id"),
                            ["send_confirmed"] = sent,
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
                        Response.Fail(id, Status.RevitUnavailable, sending ? "The connection to Revit was lost." : "The request was never sent to Revit."));
                    if (sending) Drop(pipe);
                }
                using var registration = wait.Token.Register(() => tcs.TrySetResult(write ? Unknown() :
                    Response.Fail(id, Status.RevitBusy, "Revit did not respond in time. Check for an open dialog or narrow the query.")));
                var response = await tcs.Task.ConfigureAwait(false);
                return write && sending && response.State == Status.RevitUnavailable ? Unknown() : response;
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
                var forced = Environment.GetEnvironmentVariable("BIMBRIDGE_PIPE");
                if (FixedTarget != null) candidates.Add((FixedTarget.Value.Pipe, FixedTarget.Value.Descriptor));
                else if (!string.IsNullOrEmpty(forced)) candidates.Add((forced, null));
                else
                {
                    var all = InstanceDescriptor.ReadAll();
                    // Once a Revit is selected (on first connect or with ctx instancia=…), it stays FIXED.
                    // If it closed: writes fail with destino_no_disponible; reads only move to another Revit of the same version
                    // if there is exactly one, and they say so. "The most recent" is never silently picked for writing.
                    var pinned = all.Where(x => x.Pid == PreferredPid).ToList();
                    if (PreferredPid != 0 && pinned.Count == 0)
                    {
                        var openList = all.Count == 0 ? "none" : string.Join(", ", all.Select(d => "Revit " + d.RevitVersion + " (pid " + d.Pid + ")"));
                        var same = all.Where(x => x.RevitVersion == PinnedVersion).ToList();
                        if (!allowRetarget || same.Count != 1)
                            return Response.Fail(0, Status.TargetUnavailable,
                                "The selected Revit (" + PinnedVersion + ", pid " + PreferredPid + ") is no longer open. Not redirecting to another one" +
                                (allowRetarget ? "" : " for writing") + ". Open: " + openList +". Pick one with ctx instance=<pid or version> and re-read before changing anything.");
                        RetargetNotice = "Revit " + PinnedVersion + " (pid " + PreferredPid + ") closed; this read ran on the other open Revit " + PinnedVersion + " (pid " + same[0].Pid + "). " +
                                         "Previous dN handles, sets and plans are not valid here.";
                        PreferredPid = same[0].Pid;
                        pinned = same;
                    }
                    if (PreferredPid != 0) foreach (var d in pinned) candidates.Add((d.PipeName, d));
                    else foreach (var d in all) candidates.Add((d.PipeName, d)); // first connection: the most recent, and it stays fixed
                }

                if (candidates.Count == 0)
                    return Response.Fail(0, Status.RevitUnavailable, "No open Revit has the BIMBRIDGE add-in loaded.");

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

                    // 0.6.1: the pipe server must be the announced Revit process (prevents another process from taking the name first).
                    var who = PipeServerCheck.Verify(pipe, d?.Pid ?? 0);
                    if (who != null) { pipe.Dispose(); lastError = who; continue; }

                    // Hello
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
                            ["agent"] = AgentName,
                            ["agent_source"] = string.IsNullOrWhiteSpace(AgentName) ? null : AgentSource,
                            ["conversation"] = Conversation,
                            ["model"] = Model,
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
                        // Revit closed during the hello: that is "unavailable", not a tool error.
                        pipe.Dispose();
                        lastError = ex.Message;
                        continue;
                    }
                    if (text == null) { pipe.Dispose(); lastError = "Revit did not answer the hello"; continue; }
                    var resp = Response.FromJson(text);
                    if (resp.State != Status.Ok) { pipe.Dispose(); return resp; }

                    lock (_connectionState)
                    {
                        _connCts = new CancellationTokenSource();
                        var token = _connCts.Token;
                        Hello = resp.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                        Instance = d;
                        if (d != null) { PreferredPid = d.Pid; PinnedVersion = d.RevitVersion; }
                        _pipe = pipe;
                        _ = Task.Run(() => ReadLoop(pipe, token));
                        _heartbeat = new Timer(_ => _ = PingAsync(pipe), null, HeartbeatInterval, HeartbeatInterval);
                    }
                    return null;
                }
                return Response.Fail(0, Status.RevitUnavailable, "Could not connect to Revit (" + lastError + ").");
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
            if (pipe == null) throw new IOException("not connected");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(pipe, _pipe)) throw new IOException("the connection changed before sending");
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
                    kv.Value.Waiter.TrySetResult(Response.Fail(kv.Key, Status.RevitUnavailable, "The connection to Revit was lost."));
        }

        public void Dispose() => Drop();
    }
}
