using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Issues #3 y #4: identidad nominal obligatoria para operar y correlación de cada petición.</summary>
    public class AgentIdentityGateTests
    {
        private sealed class Recorder : IRequestHandler
        {
            public readonly List<string> Ops = new List<string>();
            public Correlation LastCorrelation;
            public Dictionary<string, object> LastArgs;
            public Dictionary<string, object> HelloInfo() => new Dictionary<string, object>();
            public Task<Response> HandleAsync(Session session, Request request, CancellationToken ct)
            {
                lock (Ops) { Ops.Add(request.Op); LastCorrelation = Correlation.Current; LastArgs = request.Args; }
                var d = new Dictionary<string, object> { ["ok"] = true };
                if (request.Op == Wire.OpCommit) d["plan_id"] = "P-0001";
                return Task.FromResult(new Response { Id = request.Id, Estado = Status.Ok, Datos = d });
            }
            public void OnSessionClosed(Session session) { }
        }

        private static async Task<NamedPipeClientStream> Connect(string pipe, string agent = null, string client = "prueba", long pid = 4242)
        {
            var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(5000);
            var a = new Dictionary<string, object> { ["protocol"] = (long)Wire.ProtocolVersion, ["client"] = client, ["pid"] = pid };
            if (agent != null) { a["agente"] = agent; a["agente_fuente"] = AgentIdentity.SourceConfig; }
            await Frame.WriteAsync(c, Json.Write(new Request { Id = 1, Op = Wire.OpHello, Args = a }), CancellationToken.None);
            Assert.Equal(Status.Ok, Response.FromJson(await Frame.ReadAsync(c, CancellationToken.None)).Estado);
            return c;
        }

        private static async Task<Response> Call(NamedPipeClientStream c, long id, string op, Dictionary<string, object> args = null)
        {
            await Frame.WriteAsync(c, Json.Write(new Request { Id = id, Op = op, Args = args ?? new Dictionary<string, object>() }), CancellationToken.None);
            return Response.FromJson(await Frame.ReadAsync(c, CancellationToken.None));
        }

        private static string NewPipe() => "PDI.RevitMcp.test.id." + Guid.NewGuid().ToString("N").Substring(0, 8);

        [Fact]
        public async Task UnnamedSession_CanRead_ButCannotOperate()
        {
            var pipe = NewPipe();
            var h = new Recorder();
            using (var host = new PipeHost(pipe, null, new SessionRegistry(), h, new EngineConfig()))
            {
                host.Start();
                using (var c = await Connect(pipe))
                {
                    Assert.Equal(Status.Ok, (await Call(c, 2, Wire.OpQuery, new Dictionary<string, object> { ["cat"] = "x" })).Estado);
                    Assert.Equal(Status.Ok, (await Call(c, 3, Wire.OpContext)).Estado);
                    foreach (var op in new[] { Wire.OpPlan, Wire.OpCommit, Wire.OpExec, Wire.OpFam, Wire.OpDoc, Wire.OpShow, Wire.OpCapture, Wire.OpTask, "desconocida" })
                    {
                        var r = await Call(c, 10, op);
                        Assert.Equal(Status.Rechazado, r.Estado);
                        Assert.Contains("ctx agente=", r.Aviso);
                    }
                }
            }
            Assert.DoesNotContain(Wire.OpCommit, h.Ops);
            Assert.DoesNotContain(Wire.OpExec, h.Ops);
        }

        [Fact]
        public async Task NamedSession_Operates_AndCarriesCorrelation()
        {
            var pipe = NewPipe();
            var h = new Recorder();
            var sessions = new SessionRegistry();
            using (var host = new PipeHost(pipe, null, sessions, h, new EngineConfig()))
            {
                host.Start();
                using (var c = await Connect(pipe, agent: "Integration client"))
                {
                    var r = await Call(c, 7, Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = "P-0001", [Correlation.TraceArg] = "trace-abc-123" });
                    Assert.Equal(Status.Ok, r.Estado);
                }
            }
            var corr = h.LastCorrelation;
            Assert.NotNull(corr);
            Assert.StartsWith("A-", corr.AgentId);
            Assert.Equal("trace-abc-123", corr.TraceId);                // la traza del cliente une procesos
            Assert.EndsWith("#7", corr.RequestId);
            Assert.StartsWith(corr.SessionId + "#", corr.RequestId);
            Assert.False(h.LastArgs.ContainsKey(Correlation.TraceArg)); // _traza es transporte: el manejador no lo ve
        }

        [Fact]
        public void Correlation_CompletesJobAndTransaction_AndRejectsBadTrace()
        {
            var sessions = new SessionRegistry();
            var s = sessions.Open("prueba", 1);
            sessions.Describe(s, "1", "Codex", null, null, AgentIdentity.SourceConfig, "usuario");
            var req = new Request { Id = 3, Op = Wire.OpCommit, Args = new Dictionary<string, object> { ["plan_id"] = "P-9", [Correlation.TraceArg] = "x y" } };
            var corr = Correlation.For(s, req);
            Assert.NotEqual("x y", corr.TraceId);                       // traza inválida → nueva
            Assert.Equal(16, corr.TraceId.Length);
            corr.Complete(req, new Response { Estado = Status.Ok, Datos = new Dictionary<string, object> { ["job_id"] = "G-1" } });
            var j = new Dictionary<string, object>();
            corr.WriteTo(j);
            Assert.Equal("G-1", j["job_id"]);
            Assert.Equal("P-9", j["transaction_id"]);
            Assert.Equal(s.AgentId, j["agent_id"]);
        }

        [Fact]
        public void AgentId_IsStableAcrossReconnects_AndDiffersByAliasOrUser()
        {
            Assert.Equal(Correlation.AgentIdOf("ana", "Codex"), Correlation.AgentIdOf("ANA", "codex"));
            Assert.NotEqual(Correlation.AgentIdOf("ana", "Codex"), Correlation.AgentIdOf("ana", "Claude"));
            Assert.NotEqual(Correlation.AgentIdOf("ana", "Codex"), Correlation.AgentIdOf("luis", "Codex"));
            Assert.Null(Correlation.AgentIdOf("ana", null));
        }

        [Fact]
        public void DuplicateAlias_FromAnotherClient_IsRejected_SameInstanceReconnectIsAllowed()
        {
            var reg = new SessionRegistry();
            var a = reg.Open("claude-code", 100); reg.Describe(a, "1", null, null, null, null, "ana");
            var b = reg.Open("codex", 200); reg.Describe(b, "1", null, null, null, null, "ana");
            Assert.Equal(RegisterResult.Ok, reg.TryRegister(a, "Revisor", null, null, out _));
            Assert.Equal(RegisterResult.Duplicate, reg.TryRegister(b, "revisor", null, null, out var holder));   // suplantación / reasignación
            Assert.Same(a, holder);
            Assert.Null(b.AgentName);
            Assert.Equal(RegisterResult.Invalid, reg.TryRegister(b, "  \n ", null, null, out _));                // alias ausente

            // La misma instancia del puente reconecta antes de que se cierre su sesión anterior: puede conservar el nombre.
            var a2 = reg.Open("claude-code", 100); reg.Describe(a2, "1", "Revisor", null, null, AgentIdentity.SourceRegister, "ana");
            Assert.Equal("Revisor", a2.AgentName);
            Assert.Equal(a.AgentId, a2.AgentId);

            // Otro cliente que intenta entrar con ese alias en el saludo queda sin nombre y con el motivo.
            var c = reg.Open("otro", 300); reg.Describe(c, "1", "Revisor", null, null, AgentIdentity.SourceConfig, "ana");
            Assert.Null(c.AgentName);
            Assert.Contains("ya lo usa", c.IdentityNote);

            // Al cerrarse el titular, el alias queda libre.
            reg.Close(a); reg.Close(a2);
            Assert.Equal(RegisterResult.Ok, reg.TryRegister(b, "Revisor", null, null, out _));
        }

        [Fact]
        public void Unregister_ReturnsToReadOnly()
        {
            var reg = new SessionRegistry();
            var s = reg.Open("x", 1);
            Assert.True(reg.Register(s, "Agente", null, null));
            reg.Unregister(s);
            Assert.Null(s.AgentName);
            Assert.True(IdentityPolicy.RequiresIdentity(Wire.OpCommit));
            Assert.False(IdentityPolicy.RequiresIdentity(Wire.OpQuery));
            Assert.Contains("desregistrada", IdentityPolicy.RefusalMessage(s));
        }

        [Fact]
        public async Task IdentityNotRequired_ByExplicitConfig_AllowsLegacyClients()
        {
            var pipe = NewPipe();
            var h = new Recorder();
            using (var host = new PipeHost(pipe, null, new SessionRegistry(), h, new EngineConfig { IdentityRequired = false }))
            {
                host.Start();
                using (var c = await Connect(pipe))
                    Assert.Equal(Status.Ok, (await Call(c, 2, Wire.OpCommit, new Dictionary<string, object> { ["plan_id"] = "P-1" })).Estado);
            }
            Assert.Contains(Wire.OpCommit, h.Ops);
        }
    }
}
