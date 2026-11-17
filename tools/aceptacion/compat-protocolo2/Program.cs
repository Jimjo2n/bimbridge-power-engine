// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;

if (args is ["host", var pipeName, var generation])
{
    using var host = new PipeHost(pipeName, null, new SessionRegistry(), new Model(generation), new EngineConfig());
    host.Start();
    Console.WriteLine("listo");
    Console.ReadLine();
    return;
}
if (args is not [var oldHost, var oldBridge, var newHost, var newBridge])
    throw new ArgumentException("indique DLL del host anterior, puente anterior, host actual y puente actual");

await Exercise(oldBridge, newHost, bridge: "anterior", host: "actual");
await Exercise(newBridge, oldHost, bridge: "actual", host: "anterior");
await Exercise(newBridge, newHost, bridge: "actual", host: "actual");

static async Task Exercise(string bridgeDll, string hostDll, string bridge, string host)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var pipe = "Bimbridge.ProtocolMatrix." + Guid.NewGuid().ToString("N");
    var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    using var server = Process.Start(new ProcessStartInfo(dotnet)
    {
        ArgumentList = { hostDll, "host", pipe, host },
        RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("no arrancó el host del protocolo");
    try
    {
        Require(await server.StandardOutput.ReadLineAsync(limit.Token) == "listo", "el host no arrancó");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "matriz-de-protocolo",
            Command = dotnet,
            Arguments = new[] { bridgeDll },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["PDI_REVITMCP_PIPE"] = pipe, ["PDI_REVITMCP_PERFIL"] = "completo",
                ["PDI_REVITMCP_CONFIG"] = Path.Combine(Environment.GetEnvironmentVariable("PDI_REVITMCP_HOME") ?? Path.GetTempPath(), pipe + ".json"),
                ["BIMBRIDGE_AGENTE"] = "matriz de protocolo " + pipe,
            },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: limit.Token);
        async Task<Dictionary<string, object>> Call(string tool, Dictionary<string, object> input)
        {
            var result = await client.CallToolAsync(tool, input, cancellationToken: limit.Token);
            return Json.ParseObject(result.Content.OfType<TextContentBlock>().Single().Text);
        }
        var query = await Call("query", new Dictionary<string, object> { ["cat"] = "LightingFixtures" });
        Require(Equals(query["host"], host), "no se recibió la consulta del host esperado");
        if (bridge == "actual")
        {
            var marker = Guid.NewGuid().ToString("N");
            var receipt = await Call("job", new Dictionary<string, object> { ["intento_id"] = marker });
            if (host == "anterior") Require(Equals(receipt["estado"], "no_soportado"), "el puente debió reconocer el host sin recibos");
            else Require(Equals(receipt["intento_leido"], marker), "no se negoció la consulta de recibos");
        }
        var detail = await Call("query", new Dictionary<string, object> { ["ids"] = "70001", ["full"] = true });
        if (bridge == "actual" && host == "actual")
        {
            Require(detail.ContainsKey("res") && Equals(detail["cursor"], "cursor-matriz"), "faltan res o cursor en la respuesta amplia");
            var page = await Call("res", new Dictionary<string, object> { ["id"] = detail["res"], ["lineas"] = 2 });
            Require(((string)page["texto"]).Contains("cursor-matriz") && (long)page["total_lineas"] > 2,
                "el detalle negociado no es paginable");
        }
        else Require(detail.ContainsKey("rows") && !detail.ContainsKey("res"), "se alteró la respuesta para un par antiguo");
        Console.WriteLine($"puente {bridge} / host {host}: OK");
    }
    finally
    {
        if (!server.HasExited)
        {
            await server.StandardInput.WriteLineAsync();
            try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { server.Kill(); }
        }
    }
}

static void Require(bool result, string reason)
{
    if (!result) throw new InvalidOperationException(reason);
}

sealed class Model(string generation) : IRequestHandler
{
    public Dictionary<string, object> HelloInfo() => new()
    {
        ["revit"] = "2024", ["recibo_escritura"] = generation == "actual",
    };

    public Task<Response> HandleAsync(Session session, Request request, CancellationToken ct)
    {
        var data = new Dictionary<string, object> { ["host"] = generation };
        if (request.Op == Wire.OpJob) data["intento_leido"] = Arg.GetString(request.Args, "intento_id");
        if (request.Op == Wire.OpQuery && Arg.GetString(request.Args, "ids") == "70001")
        {
            data["cursor"] = "cursor-matriz";
            data["rows"] = new List<object> { new string('é', 600000) };
        }
        return Task.FromResult(new Response { Id = request.Id, Estado = Status.Ok, Rev = 10, Datos = data });
    }

    public void OnSessionClosed(Session session) { }
}
