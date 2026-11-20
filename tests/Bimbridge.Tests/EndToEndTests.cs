// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// End-to-end test without Revit: two real MCP clients (like Claude Code and Codex) each launch
    /// their own Bimbridge.Bridge over stdio; both bridges connect to the same PipeHost,
    /// which uses a simulated add-in with lighting fixture data. Checks that:
    ///   - the AI sees the tools without any mention of ports or sessions;
    ///   - each client gets its own session (identified by the client name);
    ///   - queries and named sets work, and sets do not mix between clients.
    /// </summary>
    public class EndToEndTests : IAsyncLifetime
    {
        private readonly string _pipe = "Bimbridge.test." + Guid.NewGuid().ToString("N").Substring(0, 8);
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
            var bridgeDll = typeof(Bimbridge.Bridge.Program).Assembly.Location;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = clientName,
                Command = "dotnet",
                Arguments = new[] { bridgeDll },
                // Isolated from the real profile: its own config.json (missing = defaults) instead of the user's %APPDATA%.
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["BIMBRIDGE_PIPE"] = _pipe, ["BIMBRIDGE_PROFILE"] = perfil,
                    ["BIMBRIDGE_CONFIG"] = Path.Combine(Path.GetTempPath(), "bimbridge-e2e-config-" + Guid.NewGuid().ToString("N") + ".json"),
                    // Issue #3: operating requires a named agent; each test connection declares a unique one through configuration.
                    ["BIMBRIDGE_AGENT"] = "E2E " + clientName + " " + Guid.NewGuid().ToString("N").Substring(0, 6),
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
        public async Task FullQueryRecoversAnOversizeCellThroughResThenContinuesWithCursor()
        {
            await using var client = await Connect("paged-query");
            var first = await Call(client, "query", new Dictionary<string, object>
            {
                ["ids"] = "70001,70002", ["f"] = "id,comments", ["full"] = true, ["page_size"] = 1,
            });
            var id = Assert.IsType<string>(first["res"]);
            try
            {
                Assert.Equal(2L, first["pages"]);
                var reconstructed = new StringBuilder();
                for (var from = 0; ; from += 7)
                {
                    var part = await Call(client, "res", new Dictionary<string, object> { ["id"] = id, ["desde"] = from, ["lineas"] = 7 });
                    Assert.Equal((long)from, part["desde"]);
                    reconstructed.Append(((string)part["texto"]).Replace("\n", ""));
                    if (from + 7 >= (long)part["total_lineas"]) break;
                }
                var original = Assert.IsType<Dictionary<string, object>>(Response.FromJson(reconstructed.ToString()).Datos);
                Assert.Equal(first["cursor"], original["cursor"]);
                var largeRow = Assert.IsType<List<object>>(Assert.Single(Assert.IsType<List<object>>(original["rows"])));
                Assert.Equal(new object[] { 70001L, FakeRevit.WideCell }, largeRow);

                var next = await Call(client, "query", new Dictionary<string, object> { ["cursor"] = first["cursor"], ["page"] = 2 });
                Assert.Equal(first["rev_instantanea"], next["rev_instantanea"]);
                Assert.Equal(2L, next["page"]);
                var nextRow = Assert.IsType<List<object>>(Assert.Single(Assert.IsType<List<object>>(next["rows"])));
                Assert.Equal(new object[] { 70002L, "second row" }, nextRow);
            }
            finally { File.Delete(Path.Combine(ResultStore.Folder, id + ".txt")); }
        }

        [Fact]
        public async Task TwoAgents_WorkInParallel_WithSeparateSessions()
        {
            await using var claude = await Connect("claude-code");
            await using var codex = await Connect("codex-mcp-client");

            // Full profile: 20 tools, no trace of ports, sessions or the internal queue.
            var tools = await claude.ListToolsAsync();
            Assert.Equal(new[] { "capture", "catalog", "check", "commit", "doc_op", "docs", "exec", "fam", "geo", "get", "guide", "job", "lote", "plan", "query", "res", "revit_context", "sets", "show", "tarea" },
                tools.Select(t => t.Name).OrderBy(n => n).ToArray());
            foreach (var t in tools)
            {
                var schema = t.JsonSchema.ToString() + t.Description;
                Assert.DoesNotContain("puerto", schema, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sesi", schema, StringComparison.OrdinalIgnoreCase);
            }

            // Both query at the same time.
            var t1 = Call(claude, "query", new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["lvl"] = "LEVEL05", ["save_as"] = "$l5" });
            var t2 = Call(codex, "query", new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["group_by"] = "lvl,type" });
            var r1 = await t1;
            var r2 = await t2;

            Assert.Equal(5L, r1["total"]);
            Assert.Equal("$l5", r1["guardado"]);
            Assert.Equal(9L, r2["total"]);
            Assert.Equal(3, ((List<object>)r2["groups"]).Count);

            // Each client has its own session, identified by the client name.
            var sessions = _fake.SeenSessions.Keys.OrderBy(x => x).ToArray();
            Assert.Equal(2, sessions.Length);
            Assert.StartsWith("S-claude-code-", sessions[0]);
            Assert.StartsWith("S-codex-mcp-client-", sessions[1]);

            // The set belongs to Claude; Codex does not see it (names do not collide between agents).
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
            _fake.Hold.Set(true); // the operator turns on "Hold" in the ribbon

            // Claude plans to raise two lighting fixtures and commits: it is held, the model does not change.
            var plan = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855000,17855001\",\"ft\":12.5}]"),
                ["summary"] = "Raise 2 L5 fixtures",
            });
            Assert.Equal(2L, plan["cambios"]);
            var pid = (string)plan["plan_id"];
            var commit = await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = pid });
            Assert.Equal("retenido", commit["estado"]);
            Assert.Equal(1L, commit["posicion_en_cola"]);
            Assert.Equal(10.0, _fake.Elev(17855000));

            // Codex plans on the same fixture: it chains onto what is held (before = 12.5).
            var plan2 = await Call(codex, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855000\",\"ft\":13}]"),
            });
            var row = (List<object>)((List<object>)plan2["filas"])[0];
            Assert.Equal("12'-6\" (12.5 ft)", row[3]);
            Assert.Equal("retenido", (await Call(codex, "commit", new Dictionary<string, object> { ["plan_id"] = plan2["plan_id"] }))["estado"]);

            // A third plan that the user invalidates by hand while it waits.
            var plan3 = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("[{\"op\":\"set_elevation\",\"ids\":\"17855005\",\"ft\":9}]"),
            });
            await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = plan3["plan_id"] });
            _fake.SetElev(17855005, 8.0); // the operator moves it in Revit

            // the operator presses "Apply queue".
            var result = _fake.ApplyQueue();
            Assert.Equal(new[] { "aplicado", "aplicado", "conflicto" }, result);
            Assert.Equal(13.0, _fake.Elev(17855000));
            Assert.Equal(12.5, _fake.Elev(17855001));
            Assert.Equal(8.0, _fake.Elev(17855005));

            // The AI queries afterwards: it knows what happened without repeating anything.
            var job = await Call(claude, "job", new Dictionary<string, object> { ["plan_id"] = pid });
            Assert.False(job.ContainsKey("estado"));
            Assert.Equal("aplicado", job["estado_plan"]);
            var job3 = await Call(claude, "job", new Dictionary<string, object> { ["plan_id"] = plan3["plan_id"] });
            Assert.Equal("conflicto", job3["estado"]);

            // With the queue off, commit applies directly.
            _fake.Hold.Set(false);
            var plan4 = await Call(claude, "plan", new Dictionary<string, object>
            {
                ["ops"] = Json.Parse("{\"op\":\"set_elevation\",\"ids\":\"17855002\",\"ft\":11}"),
            });
            var c4 = await Call(claude, "commit", new Dictionary<string, object> { ["plan_id"] = plan4["plan_id"] });
            Assert.False(c4.ContainsKey("estado"));
            Assert.Equal(11.0, _fake.Elev(17855002));

            // Free-form code does not exist as an option.
            var bad = await Call(claude, "plan", new Dictionary<string, object> { ["ops"] = Json.Parse("[{\"op\":\"run_python\",\"ids\":\"1\"}]") });
            Assert.Equal("invalido", bad["estado"]);
        }

        [Fact]
        public async Task NewTools_Guide_Batch_Capture_AndParamsRouting()
        {
            await using var c = await Connect("claude-code");

            // guide does not touch Revit and filters by operation (less text)
            var g = await c.CallToolAsync("guide", new Dictionary<string, object> { ["tema"] = "escritura", ["op"] = "tag" });
            var guideText = g.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("- tag", guideText);
            Assert.DoesNotContain("- place", guideText);
            Assert.True(guideText.Length < OpCatalog.Guide().Length / 5);

            // batch: several reads in one call, in order; the write is rejected without affecting the others
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
            Assert.Equal("params", res[2]["op_vista"]);              // get f=* goes to the read of all parameters
            Assert.Equal(Status.Invalido, res[3]["estado"]);

            // a direct get f=* is also routed to params
            var all = await Call(c, "get", new Dictionary<string, object> { ["ids"] = "17855000", ["f"] = "*" });
            Assert.Equal("params", all["op_vista"]);

            // capture returns text + image
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
            // 0.9.0: Power Engine through the same real bridge; on-demand discovery, and without authorized folders it opens no documents
            var pw = (await c.CallToolAsync("power", new Dictionary<string, object> { ["c"] = "cap ejes\npdf info ruta=/etc/passwd" })).Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("pdf.grids", pw);
            Assert.Contains("sin carpetas de documentos autorizadas", pw);

            // Several commands in one call (in parallel), text without JSON
            var r = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "q LF n=true\nq LF g=lvl\nguide escritura op=tag" });
            var text = r.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("## q LF n=true", text);
            Assert.Contains("total: 9", text);
            Assert.Contains("- tag", text);
            Assert.DoesNotContain("\"cols\"", text);

            // inline where and saved set
            var w = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "q LF lvl=LEVEL05 s=$l5" });
            Assert.False(w.IsError ?? false);

            // plan as lines → diff; commit applies
            var plan = await c.CallToolAsync("plan", new Dictionary<string, object> { ["ops"] = "elev 17855000 ft=10.5" });
            var planText = plan.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("plan_id:", planText);
            var id = planText.Split('\n').First(l => l.StartsWith("plan_id:")).Substring(8).Trim();
            var commit = await c.CallToolAsync("commit", new Dictionary<string, object> { ["plan_id"] = id });
            Assert.DoesNotContain("invalido", commit.Content.OfType<TextContentBlock>().First().Text);

            // unknown command → actionable error
            var bad = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "zzz" });
            Assert.True(bad.IsError ?? false);

            // capture: image + text, no file path
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
                ["args"] = new Dictionary<string, object> { ["n"] = 6.5 }, ["clave"] = "lav-1", ["tarea"] = "sinks",
            });
            Assert.False(ex.IsError ?? false);
            string seen;
            lock (_fake.Seen) seen = _fake.Seen.Last();
            Assert.StartsWith("exec ", seen);
            foreach (var part in new[] { "\"modo\":\"ejecutar\"", "\"tx\":\"ninguna\"", "\"doc\":\"d3\"", "\"clave\":\"lav-1\"", "\"tarea\":\"sinks\"", "\"n\":6.5" })
                Assert.Contains(part, seen);

            // act: in order; the 2nd fails (no radius) → the 3rd (load) is not attempted.
            _fake.Seen.Clear();
            var act = await c.CallToolAsync("act", new Dictionary<string, object>
            {
                ["c"] = "conector nombre=cold sistema=cold cara=812:3 radio=6.5mm doc=d3\nconector nombre=hot sistema=hot cara=812:5 doc=d3\ncargar en=d1 doc=d3",
            });
            var text = act.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("parcial: fallaron 1 de 3", text);
            Assert.Contains("## ERROR conector nombre=hot", text);
            Assert.Contains("no se ejecutaron", text);
            lock (_fake.Seen)
            {
                Assert.Equal(2, _fake.Seen.Count);
                Assert.Contains("\"radio\":\"6.5mm\"", _fake.Seen[0]);
                Assert.DoesNotContain(_fake.Seen, x => x.Contains("cargar"));
            }

            // revit fam only reads
            var famw = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "fam abrir X" });
            Assert.True(famw.IsError ?? false);
        }

        [Fact]
        public async Task TaskAndRecoveryArguments_ReachTheHandler_InBothProfiles()
        {
            await using var c = await Connect("codex-mcp-client", "compacto");
            string Last() { lock (_fake.Seen) return _fake.Seen.Last(); }

            // exec: archivo, refs, docs and reconciliar arrive as is (H04)
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

            // act: a single command starts the whole task, with policy and args
            var act = await c.CallToolAsync("act", new Dictionary<string, object>
            {
                ["c"] = "tarea iniciar receta=agrupar_unidades id=g1 objetivo=\"L5 groups\" args.nivel=LEVEL05 args.max=3 guardar_cada=200 al_final=guardar,pdf en_error=continuar",
            });
            Assert.False(act.IsError ?? false);
            seen = Last();
            Assert.StartsWith("tarea ", seen);
            foreach (var part in new[] { "\"accion\":\"iniciar\"", "\"id\":\"g1\"", "\"receta\":\"agrupar_unidades\"", "\"objetivo\":\"L5 groups\"",
                                         "\"nivel\":\"LEVEL05\"", "\"guardar_cada\":200", "\"al_final\":\"guardar,pdf\"", "\"en_error\":\"continuar\"" })
                Assert.Contains(part, seen);
            Assert.DoesNotContain("args.nivel", seen);

            // act: args as JSON and reconcile with keys
            await c.CallToolAsync("act", new Dictionary<string, object> { ["c"] = "tarea reconciliar g1 decision=no_aplicada claves=u1,u2" });
            seen = Last();
            Assert.Contains("\"accion\":\"reconciliar\"", seen);
            Assert.Contains("\"decision\":\"no_aplicada\"", seen);

            // revit: task reads; writes are rejected in the bridge
            await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "tarea esperar g1 desde=7 max=5" });
            seen = Last();
            foreach (var part in new[] { "\"accion\":\"esperar\"", "\"id\":\"g1\"", "\"desde\":7", "\"max\":5" }) Assert.Contains(part, seen);
            var w = await c.CallToolAsync("revit", new Dictionary<string, object> { ["c"] = "tarea iniciar receta=x" });
            Assert.True(w.IsError ?? false);

            // full profile: tarea tool with options
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
            _host.Dispose(); // Revit "closed"
            await using var c = await Connect("claude-code");
            var r = await Call(c, "revit_context");
            Assert.Equal("revit_no_disponible", r["estado"]);
        }

        [Fact]
        public async Task Deadline_CancelsWorkInsideRevit()
        {
            // Before: the bridge gave up at 60 s but Revit kept working for ~10 min (check views=rcp).
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", _pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await Frame.WriteAsync(pipe, Json.Write(new Request { Id = 1, Op = Wire.OpHello, Args = new Dictionary<string, object>
            {
                ["protocol"] = (long)Wire.ProtocolVersion, ["client"] = "test", ["agente"] = "E2E deadline " + Guid.NewGuid().ToString("N").Substring(0, 6), ["agente_fuente"] = AgentIdentity.SourceConfig,
            } }), CancellationToken.None);
            Assert.Equal(Status.Ok, Response.FromJson(await Frame.ReadAsync(pipe, CancellationToken.None)).Estado);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Frame.WriteAsync(pipe, Json.Write(new Request { Id = 2, Op = Wire.OpCheck, DeadlineMs = 300, Args = new Dictionary<string, object> { ["name"] = "slow" } }), CancellationToken.None);
            var resp = Response.FromJson(await Frame.ReadAsync(pipe, CancellationToken.None));
            Assert.Equal(Status.TiempoAgotado, resp.Estado);
            Assert.True(_fake.SlowCancelled);
            Assert.InRange(sw.ElapsedMilliseconds, 0, 5000);
        }

        /// <summary>Simulated add-in: same responses as the real one, with fixed data.</summary>
        private sealed class FakeRevit : IRequestHandler
        {
            private readonly QuerySnapshotStore _snapshots = new QuerySnapshotStore();
            internal static readonly string WideCell = new string('é', 600000) + "🚧\nend";
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

            public readonly HoldSwitch Hold = new HoldSwitch(Path.Combine(Path.GetTempPath(), "bimbridge-hold-" + Guid.NewGuid().ToString("N")));
            private readonly PlanStore _plans = new PlanStore(Path.Combine(Path.GetTempPath(), "bimbridge-cola-" + Guid.NewGuid().ToString("N")));
            private const string Doc = "C:\\modelos\\PRUEBA.rvt";

            public double Elev(long id) => Convert.ToDouble(_rows.First(x => (long)x["id"] == id)["elev"]);
            public void SetElev(long id, double v) { lock (_rows) _rows.First(x => (long)x["id"] == id)["elev"] = v; }
            private string Current(PlanEntry e) => PlanEntry.Canon(Elev(e.Id));

            private string Apply(Plan p)
            {
                if (p.FindConflicts(Current).Count > 0) { _plans.Finish(p, PlanStatus.Conflicto, "changed"); return p.Status; }
                foreach (var e in p.Entries) SetElev(e.Id, Convert.ToDouble(e.Target));
                _plans.Finish(p, PlanStatus.Aplicado, "ok");
                return p.Status;
            }

            /// <summary>What the "Apply queue" button does.</summary>
            public string[] ApplyQueue() => _plans.Held(Doc).Select(Apply).ToArray();

            public Dictionary<string, object> HelloInfo() => new Dictionary<string, object> { ["revit"] = "2024" };

            private Response WideQuery(Session session, Request request, string cursor)
            {
                if (cursor == null)
                {
                    var spec = QuerySpec.Parse(request.Args);
                    var rows = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["id"] = 70001L, ["comments"] = WideCell },
                        new Dictionary<string, object> { ["id"] = 70002L, ["comments"] = "second row" },
                    };
                    cursor = _snapshots.Put(session.Id, Doc, _versions.Rev, spec, rows, () => _versions.Rev, out var refusal);
                    Assert.True(cursor != null, refusal);
                }
                Assert.Equal(SnapshotLookup.Ok, _snapshots.Get(cursor, session.Id, Doc, out var snapshot));
                var page = (int)(Arg.GetLong(request.Args, "page") ?? 1L);
                var data = (Dictionary<string, object>)ResultTable.Render(snapshot.Spec, snapshot.Rows, page, snapshot.Spec.PageSize);
                data["cursor"] = cursor;
                data["rev_instantanea"] = snapshot.Rev;
                return new Response { Id = request.Id, Rev = snapshot.Rev, Datos = data };
            }

            public async Task<Response> HandleAsync(Session s, Request r, CancellationToken ct)
            {
                SeenSessions[s.Id] = s;
                await Task.Delay(50, ct); // simulates the trip to the Revit thread
                switch (r.Op)
                {
                    case Wire.OpContext:
                        return new Response { Id = r.Id, Rev = _versions.Rev, Datos = new Dictionary<string, object> { ["documento"] = "PRUEBA" } };
                    case Wire.OpCheck:
                        try { await Task.Delay(30000, ct); }
                        catch (OperationCanceledException) { SlowCancelled = true; return Response.Fail(r.Id, Status.TiempoAgotado, "cancelled"); }
                        return new Response { Id = r.Id };
                    case Wire.OpQuery:
                        var cursor = Arg.GetString(r.Args, "cursor");
                        if (cursor != null || Arg.GetString(r.Args, "ids") == "70001,70002")
                            return WideQuery(s, r, cursor);
                        QuerySpec q;
                        try { q = QuerySpec.Parse(r.Args); }
                        catch (Exception ex) when (ex is QueryException || ex is ArgumentException) { return Response.Fail(r.Id, Status.Invalido, ex.Message); }
                        IEnumerable<Dictionary<string, object>> rows = _rows;
                        if (q.FromSet != null)
                        {
                            if (!s.Sets.TryGet(q.FromSet, out var set)) return Response.Fail(r.Id, Status.Invalido, "set does not exist: " + q.FromSet);
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
                        if (p == null || p.SessionId != s.Id) return Response.Fail(r.Id, Status.Invalido, "does not exist");
                        if (Hold.On || _plans.Held(Doc).Count > 0)
                        {
                            _plans.Hold(p);
                            var d = p.Describe(0);
                            d["posicion_en_cola"] = (long)(_plans.Held(Doc).FindIndex(x => x.Id == p.Id) + 1);
                            return new Response { Id = r.Id, Estado = Status.Retenido, Datos = d, Aviso = "queued" };
                        }
                        var st = Apply(p);
                        return new Response { Id = r.Id, Estado = st == PlanStatus.Aplicado ? Status.Ok : Status.Conflicto, Datos = p.Describe(0) };
                    }
                    case Wire.OpJob:
                    {
                        var p = _plans.Get(Arg.GetString(r.Args, "plan_id"));
                        if (p == null || p.SessionId != s.Id) return Response.Fail(r.Id, Status.Invalido, "does not exist");
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
                        var file = Path.Combine(Path.GetTempPath(), "bimbridge-cap-" + Guid.NewGuid().ToString("N") + ".png");
                        File.WriteAllBytes(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object>
                        {
                            ["archivo"] = file, ["mime"] = "image/png",
                            ["leyenda"] = new Dictionary<string, object> { ["LightingFixtures"] = "orange (9)" },
                        } };
                    }
                    case Wire.OpExec:
                    case Wire.OpDoc:
                    case Wire.OpTask:
                        // Echo: the bridge must deliver the arguments as is (code, mode, args as an object, key, doc).
                        lock (Seen) Seen.Add(r.Op + " " + Json.Write(r.Args));
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["eco"] = r.Args } };
                    case Wire.OpFam:
                        lock (Seen) Seen.Add(r.Op + " " + Json.Write(r.Args));
                        if (Arg.GetString(r.Args, "accion") == "conector" && Arg.GetString(r.Args, "radio") == null && Arg.GetString(r.Args, "diametro") == null)
                            return Response.Fail(r.Id, Status.Invalido, "to create a pipe connector give radio= or diametro= with a unit");
                        return new Response { Id = r.Id, Datos = new Dictionary<string, object> { ["eco"] = r.Args } };
                    default:
                        return Response.Fail(r.Id, Status.Invalido, "op " + r.Op);
                }
            }

            public void OnSessionClosed(Session s) { }
        }
    }
}
