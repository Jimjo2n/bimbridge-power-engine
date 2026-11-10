// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Issue #1: una solicitud más larga que el umbral de inactividad no debe cerrar la sesión. Una sesión ociosa sí expira.
    /// Tubería real con un PipeHost y un manejador que tarda lo que se le pide. El cliente NO envía latidos.
    /// </summary>
    public class SessionLifecycleTests
    {
        private sealed class SlowHandler : IRequestHandler
        {
            public int DelayMs;
            public int Closed;
            public Dictionary<string, object> HelloInfo() => new Dictionary<string, object>();
            public async Task<Response> HandleAsync(Session session, Request request, CancellationToken ct)
            {
                await Task.Delay(DelayMs).ConfigureAwait(false);   // fase no interrumpible: no observa ct
                return new Response { Id = request.Id, Estado = Status.Ok, Datos = new Dictionary<string, object> { ["hecho"] = true } };
            }
            public void OnSessionClosed(Session session) => Interlocked.Increment(ref Closed);
        }

        private static async Task<NamedPipeClientStream> Connect(string pipe)
        {
            var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(5000);
            await Frame.WriteAsync(c, Json.Write(new Request { Id = 1, Op = Wire.OpHello, Args = new Dictionary<string, object>
            {
                // Identidad obligatoria: cada conexión de prueba declara un agente único por configuración.
                ["protocol"] = (long)Wire.ProtocolVersion, ["client"] = "prueba", ["agente"] = "Prueba " + Guid.NewGuid().ToString("N").Substring(0, 6), ["agente_fuente"] = AgentIdentity.SourceConfig,
            } }), CancellationToken.None);
            Assert.Equal(Status.Ok, Response.FromJson(await Frame.ReadAsync(c, CancellationToken.None)).Estado);
            return c;
        }

        [Fact]
        public void Policy_OnlyIdleSessionsExpire()
        {
            Assert.True(SessionLifecycle.ShouldExpireOnIdle(0));
            Assert.False(SessionLifecycle.ShouldExpireOnIdle(1));
        }

        [Fact]
        public async Task LongRequest_WithoutHeartbeat_KeepsSession_AndDeliversResult()
        {
            var pipe = "PDI.RevitMcp.test.life." + Guid.NewGuid().ToString("N").Substring(0, 8);
            var handler = new SlowHandler { DelayMs = 2500 };
            var sessions = new SessionRegistry();
            using (var host = new PipeHost(pipe, null, sessions, handler, new EngineConfig { HeartbeatTimeoutMs = 600 }))
            {
                host.Start();
                using (var c = await Connect(pipe))
                {
                    await Frame.WriteAsync(c, Json.Write(new Request { Id = 2, Op = Wire.OpQuery, Args = new Dictionary<string, object>(), DeadlineMs = 30000 }), CancellationToken.None);
                    var read = Frame.ReadAsync(c, CancellationToken.None);
                    var done = await Task.WhenAny(read, Task.Delay(15000));
                    Assert.Same(read, done);
                    var r = Response.FromJson(await read);
                    Assert.Equal(2, r.Id);
                    Assert.Equal(Status.Ok, r.Estado);   // la sesión no se cerró durante los 2,5 s (4× el umbral)
                    Assert.Equal(0, handler.Closed);
                }
            }
        }

        [Fact]
        public async Task IdleSession_StillExpires()
        {
            var pipe = "PDI.RevitMcp.test.idle." + Guid.NewGuid().ToString("N").Substring(0, 8);
            var handler = new SlowHandler { DelayMs = 0 };
            var sessions = new SessionRegistry();
            using (var host = new PipeHost(pipe, null, sessions, handler, new EngineConfig { HeartbeatTimeoutMs = 400 }))
            {
                host.Start();
                using (var c = await Connect(pipe))
                {
                    var limit = DateTime.UtcNow.AddSeconds(10);
                    while (handler.Closed == 0 && DateTime.UtcNow < limit) await Task.Delay(50);
                    Assert.Equal(1, handler.Closed);
                    Assert.Equal(0, sessions.Count);
                    Assert.Contains(sessions.Closed(), x => x.Reason == "inactividad");
                }
            }
        }

        [Fact]
        public async Task ClientGoneDuringRequest_ClosesSession_AndLateResponseDoesNotCrash()
        {
            var pipe = "PDI.RevitMcp.test.gone." + Guid.NewGuid().ToString("N").Substring(0, 8);
            var handler = new SlowHandler { DelayMs = 1000 };
            var sessions = new SessionRegistry();
            using (var host = new PipeHost(pipe, null, sessions, handler, new EngineConfig { HeartbeatTimeoutMs = 400 }))
            {
                host.Start();
                var c = await Connect(pipe);
                await Frame.WriteAsync(c, Json.Write(new Request { Id = 2, Op = Wire.OpQuery, Args = new Dictionary<string, object>(), DeadlineMs = 30000 }), CancellationToken.None);
                c.Dispose();   // el cliente desaparece con la solicitud en curso
                var limit = DateTime.UtcNow.AddSeconds(10);
                while (handler.Closed == 0 && DateTime.UtcNow < limit) await Task.Delay(50);
                Assert.Equal(1, handler.Closed);
                await Task.Delay(1500);   // la respuesta tardía se descarta sin excepción no controlada
                using (var again = await Connect(pipe)) { }   // el servidor sigue aceptando conexiones
            }
        }
    }
}
