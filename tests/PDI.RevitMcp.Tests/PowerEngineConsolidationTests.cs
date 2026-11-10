// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Revisión consolidada: C# dinámico por defecto con bloqueo, identidad de agentes y rebranding visible. Sin Revit.</summary>
    public class PowerEngineConsolidationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-pe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PowerEngineConsolidationTests() { Directory.CreateDirectory(_root); }
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static string Source(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PDI.RevitMcp.sln"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir, relative));
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PDI.RevitMcp.sln"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // ---- C# dinámico ----

        [Fact]
        public void DynamicCode_AllowedByDefault()
        {
            Assert.Null(DynamicCodePolicy.Decide(false));
            Assert.False(new EngineConfig().BlockDynamicCode);
            Assert.False(DynamicCodePolicy.LoadBlocked(Path.Combine(_root, "no-existe.json")));
            var cfg = Path.Combine(_root, "c.json");
            File.WriteAllText(cfg, "{\"levels\":\"RPW\"}");
            Assert.False(EngineConfig.Load(cfg).BlockDynamicCode);
        }

        [Fact]
        public void DynamicCode_BlockRejectsWithCode()
        {
            var why = DynamicCodePolicy.Decide(true);
            Assert.StartsWith(DynamicCodePolicy.BlockedCode, why);
        }

        [Fact]
        public void DynamicCode_BlockPersists_AndKeepsOtherKeys()
        {
            var cfg = Path.Combine(_root, "config.json");
            File.WriteAllText(cfg, "{\"levels\":\"RPW\",\"carpetas_documentos\":[\"C:\\\\x\"]}");
            DynamicCodePolicy.SaveBlocked(cfg, true);
            Assert.True(DynamicCodePolicy.LoadBlocked(cfg));
            var c = EngineConfig.Load(cfg);
            Assert.True(c.BlockDynamicCode);
            Assert.Equal("RPW", c.Levels);
            Assert.Single(c.DocumentFolders);
            DynamicCodePolicy.SaveBlocked(cfg, false);
            Assert.False(EngineConfig.Load(cfg).BlockDynamicCode);
        }

        [Fact]
        public void DynamicCode_UnreadableConfig_FailsClosed()
        {
            var cfg = Path.Combine(_root, "roto.json");
            File.WriteAllText(cfg, "{roto");
            Assert.True(DynamicCodePolicy.LoadBlocked(cfg));
        }

        [Fact]
        public void DynamicCode_Wiring_NoPerScriptSha_AndBlockBeforeCompile()
        {
            var exec = Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.Exec.cs");
            Assert.DoesNotContain("DenyDynamicCode(", exec);
            Assert.True(exec.IndexOf("DynamicCodePolicy.Decide(", StringComparison.Ordinal) < exec.IndexOf("ScriptHost.Compile(", StringComparison.Ordinal));
            var runtime = Source("src/PDI.RevitMcp.Addin/Ui/Runtime.cs");
            Assert.DoesNotContain("AddHours(4)", runtime);
            Assert.Contains("DynamicCodePolicy.SaveBlocked", runtime);
            Assert.Contains("Runtime.BlockDynamicCode = config.BlockDynamicCode", Source("src/PDI.RevitMcp.Addin/App.cs"));
        }

        [Fact]
        public void PipeStaysUserOnly()
        {
            // Sin conexiones remotas anónimas: la tubería sólo admite al usuario actual (no se toca con el nuevo valor por defecto).
            var app = Source("src/PDI.RevitMcp.Addin/App.cs");
            Assert.Contains("PipeOptions.CurrentUserOnly", app);
            Assert.Contains("new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow)", app);
        }

        // ---- Identidad ----

        [Fact]
        public void Agent_NameIsNeverInheritedBetweenSessions()
        {
            var reg = new SessionRegistry();
            var a = reg.Open("claude-code", 1);
            var b = reg.Open("codex", 2);
            reg.Describe(a, "2.1", "Integration client", null, null, AgentIdentity.SourceConfig, "operator");
            reg.Describe(b, "1.0", null, null, null, AgentIdentity.SourceHello, "operator");
            Assert.Equal("Integration client", a.AgentName);
            Assert.Equal(AgentIdentity.SourceConfig, a.IdentitySource);
            Assert.Null(b.AgentName);
            Assert.Equal(AgentIdentity.SourceNone, b.IdentitySource);
            Assert.StartsWith("(sin nombre)", b.DisplayName);
        }

        [Fact]
        public void Agent_RegisterValidatesAndOnlyTouchesItsSession()
        {
            var reg = new SessionRegistry();
            var a = reg.Open("claude-code", 1);
            var b = reg.Open("claude-code", 1);
            Assert.False(reg.Register(a, "   ", null, null));
            Assert.True(reg.Register(a, "ENTORNO DE PRUEBAS\n", "chat 7", "modelo-x"));
            Assert.Equal("ENTORNO DE PRUEBAS", a.AgentName);
            Assert.Equal(AgentIdentity.SourceRegister, a.IdentitySource);
            Assert.Equal("chat 7", a.Conversation);
            Assert.Null(b.AgentName);
            Assert.Equal(AgentIdentity.MaxName, AgentIdentity.Clean(new string('x', 200)).Length);
        }

        [Fact]
        public async Task Agent_CountersAndPresence()
        {
            var t = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
            var reg = new SessionRegistry(() => t);
            var s = reg.Open("codex", 3);
            reg.RequestStarted(s, "query", "d1");
            Assert.Equal("ejecutando", AgentIdentity.Presence(s, t));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => reg.RequestStarted(s, "q", null))));
            await Task.WhenAll(Enumerable.Range(0, 21).Select(i => Task.Run(() => reg.RequestFinished(s, 10, i == 0))));
            Assert.Equal(0, s.Active);
            Assert.Equal(21, s.Requests);
            Assert.Equal(1, s.Errors);
            Assert.Equal(210, s.TotalMs);
            Assert.Equal("activo", AgentIdentity.Presence(s, t.AddSeconds(30)));
            Assert.Equal("inactivo", AgentIdentity.Presence(s, t.AddMinutes(5)));
        }

        [Fact]
        public void Agent_ClosedHistoryIsBounded_AndNotCountedAsConnected()
        {
            var reg = new SessionRegistry();
            for (var i = 0; i < SessionRegistry.ClosedMax + 10; i++) reg.Close(reg.Open("c", i), "conexion_cerrada");
            Assert.Equal(0, reg.Count);
            Assert.Equal(SessionRegistry.ClosedMax, reg.Closed().Count);
        }

        [Fact]
        public void Agent_Wiring_JournalCarriesAttribution()
        {
            var host = Source("src/PDI.RevitMcp.Engine/PipeHost.cs");
            foreach (var k in new[] { "extra[\"agente\"]", "extra[\"cliente\"]", "extra[\"traza\"]", "RequestStarted(", "RequestFinished(" })
                Assert.Contains(k, host);
            Assert.Contains("[\"agente\"] = AgentName", Source("src/PDI.RevitMcp.Bridge/RevitConnection.cs"));
            Assert.Contains("Sessions.Register(session", Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.cs"));
        }

        // ---- Consultas ----

        [Theory]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true}", true)]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true,\"lvl\":[\"L1\"]}", false)]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true,\"where\":[\"elev>1\"]}", false)]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true,\"save_as\":\"$x\"}", false)]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true,\"group_by\":[\"lvl\"]}", false)]
        [InlineData("{\"cat\":[\"LightingFixtures\"],\"count\":true,\"ids\":[\"5\"]}", false)]
        [InlineData("{\"cat\":[\"LightingFixtures\"]}", false)]
        public void FastCount_OnlyWhenNothingMustBeReadPerElement(string json, bool expected) =>
            Assert.Equal(expected, QuerySpec.Parse(Json.ParseObject(json)).CanFastCount);

        [Fact]
        public void CommitWiring_OpsWithoutPlanIdPlansAndCommitsInOneTrip()
        {
            var h = Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.cs");
            Assert.Contains("request.Args.ContainsKey(\"ops\")", h);
            Assert.True(h.IndexOf("await PlanOp(session, new Request", StringComparison.Ordinal) < h.IndexOf("await CommitPlan(session, request, ct)", StringComparison.Ordinal));
            Assert.Contains("plan_en_el_mismo_viaje", h);
        }

        [Fact]
        public void ReviewFixes_Wiring()
        {
            var h = Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.cs");
            Assert.Contains("commit ops= no admite clave=", h);
            Assert.Contains("commit NO ejecutado en este viaje", h);
            var host = Source("src/PDI.RevitMcp.Engine/PipeHost.cs");
            Assert.True(host.IndexOf("_sessions.Touch(session)", StringComparison.Ordinal) > host.IndexOf("req.Op == Wire.OpPing", StringComparison.Ordinal),
                "el latido no debe contar como actividad");
            var compact = Source("src/PDI.RevitMcp.Bridge/CompactTools.cs");
            Assert.Contains("Name = \"commit\", ReadOnly = false, Destructive = true, Idempotent = false", compact);
            Assert.Contains("_revit.AgentSource = \"registro\"", compact);
            Assert.Contains("DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode)", Source("src/PDI.RevitMcp.Addin/Revit/RequestHandler.Tasks.cs"));
        }

        // ---- Rebranding ----

        [Fact]
        public void Brand_LegacyTransactionsStillAttributed()
        {
            Assert.True(Brand.IsOwnTransaction("BIMBRIDGE · ensayo · P-1"));
            Assert.True(Brand.IsOwnTransaction("PDI MCP · P-1 1/2"));
            Assert.False(Brand.IsOwnTransaction("Mover"));
            Assert.True(Brand.IsTemporaryTransaction("PDI MCP temporal captura"));
            Assert.True(Brand.IsTemporaryTransaction("BIMBRIDGE temporal exec"));
        }

        /// <summary>Ninguna superficie visible vuelve a decir "PDI MCP". Excepciones: comentarios, el prefijo heredado y este test.</summary>
        [Fact]
        public void Brand_NoVisiblePdiMcpStrings()
        {
            var bad = new List<string>();
            var rx = new Regex("\"[^\"\\n]*(PDI MCP|PDI Revit MCP|PDI-MCP)[^\"\\n]*\"");
            foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.*", SearchOption.AllDirectories)
                         .Where(p => (p.EndsWith(".cs") || p.EndsWith(".addin")) && !Regex.IsMatch(p, @"[\\/](bin|obj)[\\/]")))
            {
                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length; i++)
                {
                    var l = lines[i].Trim();
                    if (l.StartsWith("//") || l.StartsWith("///") || l.StartsWith("*")) continue;
                    if (l.Contains("LegacyTransactionPrefix")) continue;
                    if (rx.IsMatch(l) || l.Contains(">PDI Revit MCP<")) bad.Add(Path.GetFileName(f) + ":" + (i + 1) + ": " + l);
                }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void Brand_ServerAndAddinAnnounceProduct()
        {
            Assert.Contains("Name = \"" + Brand.Product + "\"", Source("src/PDI.RevitMcp.Bridge/Program.cs"));
            Assert.Contains("<Name>" + Brand.Product + "</Name>", Source("src/PDI.RevitMcp.Addin/PDI.RevitMcp.addin"));
            Assert.Contains("\"DisplayName\", \"" + Brand.Product + "\"", Source("src/PDI.RevitMcp.Setup/Platform.cs"));
        }
    }
}
