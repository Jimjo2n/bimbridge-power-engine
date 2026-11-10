// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Prueba extremo a extremo sin Revit: dos clientes MCP reales (como Claude Code y Codex) lanzan
    /// cada uno su propio PDI.RevitMcp.Bridge por stdio; ambos puentes se conectan al mismo PipeHost,
    /// que usa un add-in simulado con datos de luminarias. Verifica que:
    ///   - la IA ve las herramientas sin ninguna mención de puertos o sesiones;
    ///   - cada cliente obtiene su propia sesión (identificada por el nombre del cliente);
    ///   - las consultas y los conjuntos con nombre funcionan, y los conjuntos no se mezclan entre clientes.
    /// </summary>
    public class EndToEndTests : IAsyncLifetime
    {
        private readonly string _pipe = "PDI.RevitMcp.test." + Guid.NewGuid().ToString("N").Substring(0, 8);
        private FakeRevit _fake;
        private PipeHost _host;

        public Task InitializeAsync()
        {
            _fake = new FakeRevit();
            _host = new PipeHost(_pipe, null, new SessionRegistry(), _fake, new EngineConfig());
            _host.Start();
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            _host.Dispose();
            return Task.CompletedTask;
        }

        private Task<McpClient> Connect(string clientName, string perfil = "completo")
        {
            var bridgeDll = typeof(PDI.RevitMcp.Bridge.Program).Assembly.Location;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = clientName,
                Command = "dotnet",
                Arguments = new[] { bridgeDll },
                // Aislado del perfil real: config.json propio (inexistente = valores por defecto) en vez del %APPDATA% del usuario.
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["PDI_REVITMCP_PIPE"] = _pipe, ["PDI_REVITMCP_PERFIL"] = perfil,
                    ["PDI_REVITMCP_CONFIG"] = Path.Combine(Path.GetTempPath(), "pdi-e2e-config-" + Guid.NewGuid().ToString("N") + ".json"),
                    ["BIMBRIDGE_AGENTE"] = "",
                },
            });
            return McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = clientName, Version = "test" },
            });
        }

        private static async Task<Dictionary<string, object>> Call(McpClient c, string tool, Dictionary<string, object> args = null)
        {
            var r = await c.CallToolAsync(tool, args ?? new Dictionary<string, object>());
            var text = r.Content.OfType<TextContentBlock>().First().Text;
            return Json.ParseObject(text);
        }

        [Fact]
        public async Task TwoAgents_WorkInParallel_WithSeparateSessions()
        {
            await using var claude = await Connect("claude-code");
            await using var codex = await Connect("codex-mcp-client");

            // Perfil completo: 20 herramientas, sin rastro de puertos, sesiones ni de la cola interna.
            var tools = await claude.ListToolsAsync();
            Assert.Equal(new[] { "capture", "catalog", "check", "commit", "doc_op", "docs", "exec", "fam", "geo", "get", "guide", "job", "lote", "plan", "query", "res", "revit_context", "sets", "show", "tarea" },
                tools.Select(t => t.Name).OrderBy(n => n).ToArray());
            foreach (var t in tools)
            {
                var schema = t.JsonSchema.ToString() + t.Description;
                Assert.DoesNotContain("puerto", schema, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sesi", schema, StringComparison.OrdinalIgnoreCase);
            }

            // Las dos consultan a la vez.
            var t1 = Call(claude, "query", new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["lvl"] = "LEVEL05", ["save_as"] = "$l5" });
            var t2 = Call(codex, "query", new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl,type" });
            var r1 = await t1;
            var r2 = await t2;

            Assert.Equal(5L, r1["total"]);
            Assert.Equal("$l5", r1["guardado"]);
            Assert.Equal(9L, r2["total"]);
            Assert.Equal(3, ((List<object>)r2["groups"]).Count);

            // Cada cliente tiene su propia sesión, identificada por el nombre del cliente.
            var sessions = _fake.SeenSessions.Keys.OrderBy(x => x).ToArray();
            Assert.Equal(2, sessions.Length);
            Assert.StartsWith("S-claude-code-", sessions[0]);
            Assert.StartsWith("S-codex-mcp-client-", sessions[1]);

            // El conjunto es de Claude; Codex no lo ve (no se pisan nombres entre agentes).
            var reuse = await Call(claude, "query", new Dictionary<string, object> { ["ids"] = "$l5", ["count"] = true });
            Assert.Equal(5L, reuse["total"]);
            var other = await Call(codex, "query", new Dictionary<string, object> { ["ids"] = "$l5" });
            Assert.Equal("invalido", other["estado"]);
        }

        [Fact]
        public async Task HeldCommit_WaitsForUser_ThenApplies_AndDetectsConflicts()
        {
            await using var claude = await Connect("claude-code");
            await using var codex = await Connect("codex-mcp-client");
            _fake.Hold.Set(true); // operator enciende "Retener" en la cinta

            // Claude planifica subir dos luminarias y hace commit: queda retenido, el modelo no cambia.
            var plan = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855000,17855001\",\"ft\":12.5}]"),
                ["summary"] = "Subir 2 luminarias L5",
            });
            Assert.Equal(2L, plan["cambios"]);
            var pid = (string)plan["plan_id"];
            var commit = await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = pid });
            Assert.Equal("retenido", commit["estado"]);
            Assert.Equal(1L, commit["posicion_en_cola"]);
            Assert.Equal(10.0, _fake.Elev(17855000));

            // Codex planifica sobre la misma luminaria: se encadena sobre lo retenido (antes = 12.5).
            var plan2 = await Call(codex, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855000\",\"ft\":13}]"),
            });
            var row = (List<object>)((List<object>)plan2["filas"])[0];
            Assert.Equal("12'-6\" (12.5 ft)", row[3]);
            Assert.Equal("retenido", (await Call(codex, "commit", new Dictionary<string, object> { ["plan_id"] = plan2["plan_id"] }))["estado"]);

            // Un tercer plan que el usuario invalida a mano mientras espera.
            var plan3 = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855005\",\"ft\":9}]"),
            });
            await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = plan3["plan_id"] });
            _fake.SetElev(17855005, 8.0); // operator la mueve en Revit

            // operator pulsa "Aplicar cola".
            var result = _fake.ApplyQueue();
            Assert.Equal(new[] { "aplicado", "aplicado", "conflicto" }, result);
            Assert.Equal(13.0, _fake.Elev(17855000));
            Assert.Equal(12.5, _fake.Elev(17855001));
            Assert.Equal(8.0, _fake.Elev(17855005));

            // La IA consulta después: sabe qué pasó sin repetir nada.
            var job = await Call(claude, "job", new Dictionary<string, object> { ["plan_id"] = pid });
            Assert.False(job.ContainsKey("estado"));
            Assert.Equal("aplicado", job["estado_plan"]);
            var job3 = await Call(claude, "job", new Dictionary<string, object> { ["plan_id"] = plan3["plan_id"] });
            Assert.Equal("conflicto", job3["estado"]);

            // Con la cola apagada, commit aplica directo.
            _fake.Hold.Set(false);
            var plan4 = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("{\"op\":\"set_elevation\",\"ids\":\"17855002\",\"ft\":11}"),
            });
            var c4 = await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = plan4["plan_id"] });
            Assert.False(c4.ContainsKey("estado"));
            Assert.Equal(11.0, _fake.Elev(17855002));

            // Código libre no existe como opción.
            var bad = await Call(claude, "plan", new Dictionary<string, object> { ["ops"] = Json.Parse("[{\"op\":\"run_python\",\"ids\":\"1\"}]") });
            Assert.Equal("invalido", bad["estado"]);
        }

        [Fact]
        public async Task NewTools_Guide_Batch_Capture_AndParamsRouting()
        {
            await using var c = await Connect("claude-code");

            // guide no toca Revit y filtra por operación (menos texto)
            var g = await c.CallToolAsync("guide", new Dictionary<string, object> { ["tema"] = "escritura", ["op"] = "tag" });
            var guideText = g.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("- tag", guideText);
            Assert.DoesNotContain("- place", guideText);
            Assert.True(guideText.Length < OpCatalog.Guide().Length / 5);

            // lote: varias lecturas en una llamada, en orden; la escritura se rechaza sin afectar a las demás
            var batch = await Call(c, "lote", new Dictionary<string, object>
            {
                ["llamadas"] = new object[]
                {
                    new Dictionary<string, object> { ["tool"] = "query", ["cat"] = "LightingFixtures", ["count"] = true },
                    new Dictionary<string, object> { ["tool"] = "catalog", ["que"] = "niveles" },
                    new Dictionary<string, object> { ["tool"] = "get", ["ids"] = "17855000", ["f"] = "*" },
                    new Dictionary<string, object> { ["tool"] = "plan", ["ops"] = "[]" },
                },
            });
            var res = ((List<object>)batch["resultados"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "query", "catalog", "get", "plan" }, res.Select(r => (string)r["tool"]).ToArray());
            Assert.Equal(9L, res[0]["total"]);
            Assert.Equal("niveles", res[1]["que"]);
            Assert.Equal("params", res[2]["op_vista"]);              // get f=* va a la lectura de todos los parámetros
            Assert.Equal(Status.Invalido, res[3]["estado"]);

            // get f=* directo también se enruta a params
            var all = await Call(c, "get", new Dictionary<string, object> { ["ids"] = "17855000", ["f"] = "*" });
            Assert.Equal("params", all["op_vista"]);

            // capture devuelve texto + imagen
            var cap = await c.CallToolAsync("capture", new Dictionary<string, object> { ["colorear"] = "categorias" });
            Assert.False(cap.IsError ?? false);
            var img = cap.Content.OfType<ImageContentBlock>().Single();
            Assert.Equal("image/png", img.MimeType);
            Assert.Equal(0x89, img.DecodedData.Span[0]);
            var capText = Json.ParseObject(cap.Content.OfType<TextContentBlock>().First().Text);
            Assert.False(capText.ContainsKey("mime"));
            Assert.True(capText.ContainsKey("leyenda"));
        }

        [Fact]
        public async Task CompactProfile_ThreeTools_OneLineCommands_TextOutput()
        {
            await using var c = await Connect("claude-code", "compacto");
            var tools = await c.ListToolsAsync();
            Assert.Equal(new[] { "act", "commit", "exec", "plan", "power", "revit" }, tools.Select(t => t.Name).OrderBy(n => n).ToArray());
            // 0.9.0: Power Engine por el mismo puente real; descubrimiento bajo demanda y sin carpetas autorizadas no abre documentos
            var pw = (await c.CallToolAsync("power", new Dictionary<string, object> { ["c"] = "cap ejes\npdf info ruta=/etc/passwd" })).Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("pdf.grids", pw);
            Assert.Contains("sin carpetas de documentos autorizadas", pw);

            // Varias órdenes en una llamada (en paralelo), texto sin JSON
            var r = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "q LF n=true\nq LF g=lvl\nguide escritura op=tag" });
            var text = r.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("## q LF n=true", text);
            Assert.Contains("total: 9", text);
            Assert.Contains("- tag", text);
            Assert.DoesNotContain("\"cols\"", text);

            // where en línea y conjunto guardado
            var w = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "q LF lvl=LEVEL05 s=$l5" });
            Assert.False(w.IsError ?? false);

            // plan en líneas → diff; commit aplica
            var plan = await c.CallToolAsync("plan", new Dictionary<string, object> { ["ops"] = "elev 17855000 ft=10.5" });
            var planText = plan.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("plan_id:", planText);
            var id = planText.Split('\n').First(l => l.StartsWith("plan_id:")).Substring(8).Trim();
            var commit = await c.CallToolAsync("commit", new Dictionary<string, object> { ["plan_id"] = id });
            Assert.DoesNotContain("invalido", commit.Content.OfType<TextContentBlock>().First().Text);

            // orden desconocida → error accionable
            var bad = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "zzz" });
            Assert.True(bad.IsError ?? false);

            // captura: imagen + texto, sin ruta de archivo
            var cap = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "cap colorear=categorias" });
            Assert.Single(cap.Content.OfType<ImageContentBlock>());
            Assert.DoesNotContain("archivo", cap.Content.OfType<TextContentBlock>().First().Text);
        }

        [Fact]
        public async Task CompactProfile_ExecAndAct_PassArgumentsAndStopAfterFailure()
        {
            await using var c = await Connect("claude-code", "compacto");
            var ex = await c.CallToolAsync("exec", new Dictionary<string, object>
            {
                ["codigo"] = "return args[\"n\"];", ["modo"] = "ejecutar", ["tx"] = "ninguna", ["doc"] = "d3",
                ["args"] = new Dictionary<string, object> { ["n"] = 6.5 }, ["clave"] = "lav-1", ["tarea"] = "lavamanos",
            });
            Assert.False(ex.IsError ?? false);
            string seen;
            lock (_fake.Seen) seen = _fake.Seen.Last();
            Assert.StartsWith("exec ", seen);
            foreach (var part in new[] { "\"modo\":\"ejecutar\"", "\"tx\":\"ninguna\"", "\"doc\":\"d3\"", "\"clave\":\"lav-1\"", "\"tarea\":\"lavamanos\"", "\"n\":6.5" })
                Assert.Contains(part, seen);

            // act: en orden; la 2.ª falla (sin radio) → la 3.ª (cargar) no se intenta.
            _fake.Seen.Clear();
            var act = await c.CallToolAsync("act", new Dictionary<string, object>
            {
                ["c"] = "conector nombre=fria sistema=fria cara=812:3 radio=6.5mm doc=d3\nconector nombre=caliente sistema=caliente cara=812:5 doc=d3\ncargar en=d1 doc=d3",
            });
            var text = act.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("parcial: fallaron 1 de 3", text);
            Assert.Contains("## ERROR conector nombre=caliente", text);
            Assert.Contains("no se ejecutaron", text);
            lock (_fake.Seen)
            {
                Assert.Equal(2, _fake.Seen.Count);
                Assert.Contains("\"radio\":\"6.5mm\"", _fake.Seen[0]);
                Assert.DoesNotContain(_fake.Seen, x => x.Contains("cargar"));
            }

            // revit fam sólo lee
            var famw = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "fam abrir X" });
            Assert.True(famw.IsError ?? false);
        }

        [Fact]
        public async Task TaskAndRecoveryArguments_ReachTheHandler_InBothProfiles()
        {
            await using var c = await Connect("codex-mcp-client", "compacto");
            string Last() { lock (_fake.Seen) return _fake.Seen.Last(); }

            // exec: archivo, refs, docs y reconciliar llegan tal cual (H04)
            await c.CallToolAsync("exec", new Dictionary<string, object>
            {
                ["archivo"] = @"C:\recetas\lav.cs", ["modo"] = "ejecutar", ["refs"] = @"C:\libs\A.dll, C:\libs\B.dll", ["docs"] = "d2,d3",
                ["clave"] = "k9", ["reconciliar"] = "no_aplicado",
            });
            var seen = Last();
            foreach (var part in new[] { "\"archivo\":\"C:\\\\recetas\\\\lav.cs\"", "\"refs\":[\"C:\\\\libs\\\\A.dll\",\"C:\\\\libs\\\\B.dll\"]", "\"docs\":[\"d2\",\"d3\"]", "\"reconciliar\":\"no_aplicado\"", "\"clave\":\"k9\"" })
                Assert.Contains(part, seen);

            // commit: reconciliar
            await c.CallToolAsync("commit", new Dictionary<string, object> { ["plan_id"] = "P-x", ["clave"] = "k1", ["reconciliar"] = "aplicado" });
            Assert.Contains("\"reconciliar\":\"aplicado\"", Last());

            // act: una sola orden inicia la tarea completa, con política y args
            var act = await c.CallToolAsync("act", new Dictionary<string, object>
            {
                ["c"] = "tarea iniciar receta=agrupar_unidades id=g1 objetivo=\"grupos L5\" args.nivel=LEVEL05 args.max=3 guardar_cada=200 al_final=guardar,pdf en_error=continuar",
            });
            Assert.False(act.IsError ?? false);
            seen = Last();
            Assert.StartsWith("tarea ", seen);
            foreach (var part in new[] { "\"accion\":\"iniciar\"", "\"id\":\"g1\"", "\"receta\":\"agrupar_unidades\"", "\"objetivo\":\"grupos L5\"",
                                         "\"nivel\":\"LEVEL05\"", "\"guardar_cada\":200", "\"al_final\":\"guardar,pdf\"", "\"en_error\":\"continuar\"" })
                Assert.Contains(part, seen);
            Assert.DoesNotContain("args.nivel", seen);

            // act: args como JSON y reconciliar con claves
            await c.CallToolAsync("act", new Dictionary<string, object> { ["c"] = "tarea reconciliar g1 decision=no_aplicada claves=u1,u2" });
            seen = Last();
            Assert.Contains("\"accion\":\"reconciliar\"", seen);
            Assert.Contains("\"decision\":\"no_aplicada\"", seen);

            // revit: lecturas de tarea; las escrituras se rechazan en el puente
            await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "tarea esperar g1 desde=7 max=5" });
            seen = Last();
            foreach (var part in new[] { "\"accion\":\"esperar\"", "\"id\":\"g1\"", "\"desde\":7", "\"max\":5" }) Assert.Contains(part, seen);
            var w = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "tarea iniciar receta=x" });
            Assert.True(w.IsError ?? false);

            // perfil completo: herramienta tarea con opciones
            await using var f = await Connect("claude-code");
            await f.CallToolAsync("tarea", new Dictionary<string, object>
            {
                ["accion"] = "iniciar", ["archivo"] = @"C:\r\x.cs", ["id"] = "t2",
                ["args"] = new Dictionary<string, object> { ["nivel"] = "LEVEL06" }, ["opciones"] = "guardar_cada=50 al_final=guardar espera_s=0",
            });
            seen = Last();
            foreach (var part in new[] { "\"accion\":\"iniciar\"", "\"id\":\"t2\"", "\"nivel\":\"LEVEL06\"", "\"guardar_cada\":50", "\"al_final\":\"guardar\"", "\"espera_s\":0" })
                Assert.Contains(part, seen);
        }

        [Fact]
        public async Task InvalidQuery_ReturnsActionableMessage()
        {
            await using var c = await Connect("claude-code");
            var r = await Call(c, "query", new Dictionary<string, object> { ["f"] = "id" });
            Assert.Equal("invalido", r["estado"]);
            Assert.Contains("cat, ids, set o view", (string)r["aviso"]);
        }

        [Fact]
        public async Task NoRevit_ReturnsClearStatus()
        {
            _host.Dispose(); // Revit "cerrado"
            await using var c = await Connect("claude-code");
            var r = await Call(c, "revit_context");
            Assert.Equal("revit_no_disponible", r["estado"]);
        }

        [Fact]
        public async Task Deadline_CancelsWorkInsideRevit()
        {
            // Antes: el puente se rendía a los 60 s pero Revit seguía trabajando ~10 min (check views=rcp).
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", _pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await Frame.WriteAsync(pipe, Json.Write(new Request { Id = 1, Op = Wire.OpHello, Args = new Dictionary<string, object> { ["protocol"] = (long)Wire.ProtocolVersion, ["client"] = "prueba" } }), CancellationToken.None);
            Assert.Equal(Status.Ok, Response.FromJson(await Frame.ReadAsync(pipe, CancellationToken.None)).Estado);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Frame.WriteAsync(pipe, Json.Write(new Request { Id = 2, Op = Wire.OpCheck, DeadlineMs = 300, Args = new Dictionary<string, object> { ["name"] = "lento" } }), CancellationToken.None);
            var resp = Response.FromJson(await Frame.ReadAsync(pipe, CancellationToken.None));
            Assert.Equal(Status.TiempoAgotado, resp.Estado);
            Assert.True(_fake.SlowCancelled);
            Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);
        }

        /// <summary>Add-in simulado: mismas respuestas que el real, con datos fijos.</summary>
        private sealed class FakeRevit : IRequestHandler
        {
            public volatile bool SlowCancelled;
            public readonly List<string> Seen = new List<string>();
            public readonly ConcurrentDictionary<string, Session> SeenSessions = new ConcurrentDictionary<string, Session>();
            private readonly VersionRegistry _versions = new VersionRegistry();
            private readonly List<Dictionary<string, object>> _rows = new List<Dictionary<string, object>>();

            public FakeRevit()
            {
                long id = 17855000;
                foreach (var (lvl, type, n) in new[] { ("LEVEL05", "G", 3), ("LEVEL05", "L5", 2), ("LEVEL06", "G", 4) })
                    for (var i = 0; i < n; i++)
                        _rows.Add(new Dictionary<string, object> { ["id"] = id++, ["lvl"] = lvl, ["fam"] = "LTG", ["type"] = type, ["elev"] = 10.0 + i });
                _versions.Record(_rows.Select(r => (long)r["id"]), null, null, null);
            }

            public readonly HoldSwitch Hold = new HoldSwitch(Path.Combine(Path.GetTempPath(), "pdi-hold-" + Guid.NewGuid().ToString("N")));
            private readonly PlanStore _plans = new PlanStore(Path.Combine(Path.GetTempPath(), "pdi-cola-" + Guid.NewGuid().ToString("N")));
            private const string Doc = "C:\\modelos\\PRUEBA.rvt";

            public double Elev(long id) => Convert.ToDouble(_rows.First(x => (long)x["id"] == id)["elev"]);
            public void SetElev(long id, double v) { lock (_rows) _rows.First(x => (long)x["id"] == id)["elev"] = v; }
            private string Current(PlanEntry e) => PlanEntry.Canon(Elev(e.Id));

            private string Apply(Plan p)
            {
                if (p.FindConflicts(Current).Count > 0) { _plans.Finish(p, PlanStatus.Conflicto, "cambió"); return p.Status; }
                foreach (var e in p.Entries) SetElev(e.Id, Convert.ToDouble(e.Target));
                _plans.Finish(p, PlanStatus.Aplicado, "ok");
                return p.Status;
            }

            /// <summary>Lo que hace el botón "Aplicar cola".</summary>
            public string[] ApplyQueue() => _plans.Held(Doc).Select(Apply).ToArray();

            public Dictionary<string, object> HelloInfo() => new Dictionary<string, object> { ["revit"] = "2024" };

            public async Task<Response> HandleAsync(Session s, Request r, CancellationToken ct)
            {
                SeenSessions[s.Id] = s;
                await Task.Delay(50, ct); // simula el viaje al hilo de Revit
                switch (r.Op)
                {
                    case Wire.OpContext:
                        return new Response { Id = r.Id, Rev = _versions.Rev, Datos = new Dictionary<string, object> { ["documento"] = "PRUEBA" } };
                    case Wire.OpCheck:
                        try { await Task.Delay(30000, ct); }
                        catch (OperationCanceledException) { SlowCancelled = true; return Response.Fail(r.Id, Status.TiempoAgotado, "cancelado"); }
                        return new Response { Id = r.Id };
                    case Wire.OpQuery:
                        QuerySpec q;
                        try { q = QuerySpec.Parse(r.Args); }
                        catch (Exception ex) when (ex is QueryException || ex is ArgumentException) { return Response.Fail(r.Id, Status.Invalido, ex.Message); }
                        IEnumerable<Dictionary<string, object>> rows = _rows;
                        if (q.FromSet != null)
                        {
                            if (!s.Sets.TryGet(q.FromSet, out var set)) return Response.Fail(r.Id, Status.Invalido, "no existe el conjunto " + q.FromSet);
                            rows = rows.Where(x => set.Contains((long)x["id"]));
                        }
                        if (q.Levels.Count > 0) rows = rows.Where(x => q.Levels.Contains((string)x["lvl"]));
                        var datos = (Dictionary<string, object>)ResultTable.Build(q, rows, out var matched);
                        if (q.SaveAs != null) { s.Sets.Save(q.SaveAs, matched); datos["guardado"] = q.SaveAs; }
                        return new Response { Id = r.Id, Rev = _versions.Rev, Datos = datos };
                    case Wire.OpPlan:
                    {
                        List<OpSpec> ops;
                        try { ops = OpSpec.ParseAll(r.Args); }
                        catch (QueryException ex) { return Response.Fail(r.Id, Status.Invalido, ex.Message); }
                        var p = new Plan { Id = _plans.NewId(), SessionId = s.Id, Client = s.ClientName, DocKey = Doc, CreatedUtc = DateTime.UtcNow, Summary = Arg.GetString(r.Args, "summary") };
                        foreach (var op in ops)
                            foreach (var id in op.Ids)
                            {
                                var before = _plans.TryHeldAfter(Doc, id, "elev", out var after, out _) ? after : PlanEntry.Canon(Elev(id));
                                p.Entries.Add(new PlanEntry { Id = id, Op = op.Kind, Key = "elev", Before = before, After = PlanEntry.Canon(op.Ft.Value), Target = op.Ft.Value });
                            }
                        _plans.Add(p);
                        return new Response { Id = r.Id, Datos = p.Describe() };
                    }
                    case Wire.OpCommit:
                    {
                        lock (Seen) Seen.Add(r.Op + " " + Json.Write(r.Args));
                        var p = _plans.Get(Arg.GetString(r.Args, "plan_id"));
                        if (p == null || p.SessionId != s.Id) return Response.Fail(r.Id, Status.Invalido, "no existe");
                        if (Hold.On || _plans.Held(Doc).Count > 0)
                        {
                            _plans.Hold(p);
                            var d = p.Describe(0);
                            d["posicion_en_cola"] = (long)(_plans.Held(Doc).FindIndex(x => x.Id == p.Id) + 1);
                            return new Response { Id = r.Id, Estado = Status.Retenido, Datos = d, Aviso = "en cola" };
                        }
                        var st = Apply(p);
                        return new Response { Id = r.Id, Estado = st == PlanStatus.Aplicado ? Status.Ok : Status.Conflicto, Datos = p.Describe(0) };
                    }
                    case Wire.OpJob:
                    {
                        var p = _plans.Get(Arg.GetString(r.Args, "plan_id"));
                        if (p == null || p.SessionId != s.Id) return Response.Fail(r.Id, Status.Invalido, "no existe");
                        var est = p.Status == PlanStatus.Aplicado ? Status.Ok : p.Status;
                        return new Response { Id = r.Id, Estado = est, Datos = p.Describe(0) };
                    }
                    case Wire.OpCatalog:
                        return new Response { Id = r.Id, Rev = _versions.Rev, Datos = new Dictionary<string, object>
                        {
                            ["que"] = Arg.GetString(r.Args, "que") ?? "resumen", ["total"] = 2L,
                            ["cols"] = new List<object> { "id", "nombre" }, ["rows"] = new List<object> { new List<object> { 1L, "LEVEL05" }, new List<object> { 2L, "LEVEL06" } },
                        } };
                    case Wire.OpParams:
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["op_vista"] = "params", ["ids"] = Arg.GetString(r.Args, "ids") } };
                    case Wire.OpCapture:
                    {
                        var file = Path.Combine(Path.GetTempPath(), "pdi-cap-" + Guid.NewGuid().ToString("N") + ".png");
                        File.WriteAllBytes(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object>
                        {
                            ["archivo"] = file, ["mime"] = "image/png",
                            ["leyenda"] = new Dictionary<string, object> { ["LightingFixtures"] = "naranja (9)" },
                        } };
                    }
                    case Wire.OpExec:
                    case Wire.OpDoc:
                    case Wire.OpTask:
                        // Eco: el puente debe entregar los argumentos tal cual (código, modo, args como objeto, clave, doc).
                        lock (Seen) Seen.Add(r.Op + " " + Json.Write(r.Args));
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["eco"] = r.Args } };
                    case Wire.OpFam:
                        lock (Seen) Seen.Add(r.Op + " " + Json.Write(r.Args));
                        if (Arg.GetString(r.Args, "accion") == "conector" && Arg.GetString(r.Args, "radio") == null && Arg.GetString(r.Args, "diametro") == null)
                            return Response.Fail(r.Id, Status.Invalido, "para crear un conector de tubería indique radio= o diametro= con unidad");
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["eco"] = r.Args } };
                    default:
                        return Response.Fail(r.Id, Status.Invalido, "op " + r.Op);
                }
            }

            public void OnSessionClosed(Session s) { }
        }
    }
}
