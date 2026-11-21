// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Bimbridge.Setup;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Consolidated review: dynamic C# by default with a block switch, agent identity and visible rebranding. No Revit.</summary>
    public class PowerEngineConsolidationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bimbridge-pe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public PowerEngineConsolidationTests() { Directory.CreateDirectory(_root); }
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static string Source(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Bimbridge.sln"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir, relative));
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Bimbridge.sln"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // ---- Dynamic C# ----

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
            File.WriteAllText(cfg, "{\"levels\":\"RPW\",\"document_folders\":[\"C:\\\\x\"]}");
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
            var exec = Source("src/Bimbridge.Addin/Revit/RequestHandler.Exec.cs");
            Assert.DoesNotContain("DenyDynamicCode(", exec);
            Assert.True(exec.IndexOf("DynamicCodePolicy.Decide(", StringComparison.Ordinal) < exec.IndexOf("ScriptHost.Compile(", StringComparison.Ordinal));
            var runtime = Source("src/Bimbridge.Addin/Ui/Runtime.cs");
            Assert.DoesNotContain("AddHours(4)", runtime);
            Assert.Contains("DynamicCodePolicy.SaveBlocked", runtime);
            Assert.Contains("Runtime.BlockDynamicCode = config.BlockDynamicCode", Source("src/Bimbridge.Addin/App.cs"));
        }

        [Fact]
        public void PipeStaysUserOnly()
        {
            // No anonymous remote connections: the pipe only admits the current user (unchanged by the new default).
            var app = Source("src/Bimbridge.Addin/App.cs");
            Assert.Contains("PipeOptions.CurrentUserOnly", app);
            Assert.Contains("new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow)", app);
        }

        // ---- Identity ----

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
            Assert.StartsWith("(unnamed)", b.DisplayName);
        }

        [Fact]
        public void Agent_RegisterValidatesAndOnlyTouchesItsSession()
        {
            var reg = new SessionRegistry();
            var a = reg.Open("claude-code", 1);
            var b = reg.Open("claude-code", 1);
            Assert.False(reg.Register(a, "   ", null, null));
            Assert.True(reg.Register(a, "TEST ENVIRONMENT\n", "chat 7", "modelo-x"));
            Assert.Equal("TEST ENVIRONMENT", a.AgentName);
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
            Assert.Equal("running_now", AgentIdentity.Presence(s, t));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => reg.RequestStarted(s, "q", null))));
            await Task.WhenAll(Enumerable.Range(0, 21).Select(i => Task.Run(() => reg.RequestFinished(s, 10, i == 0))));
            Assert.Equal(0, s.Active);
            Assert.Equal(21, s.Requests);
            Assert.Equal(1, s.Errors);
            Assert.Equal(210, s.TotalMs);
            Assert.Equal("active", AgentIdentity.Presence(s, t.AddSeconds(30)));
            Assert.Equal("inactive", AgentIdentity.Presence(s, t.AddMinutes(5)));
        }

        [Fact]
        public void Agent_ClosedHistoryIsBounded_AndNotCountedAsConnected()
        {
            var reg = new SessionRegistry();
            for (var i = 0; i < SessionRegistry.ClosedMax + 10; i++) reg.Close(reg.Open("c", i), "connection_closed");
            Assert.Equal(0, reg.Count);
            Assert.Equal(SessionRegistry.ClosedMax, reg.Closed().Count);
        }

        [Fact]
        public void Agent_Wiring_JournalCarriesAttribution()
        {
            var host = Source("src/Bimbridge.Engine/PipeHost.cs");
            foreach (var k in new[] { "extra[\"agent\"]", "extra[\"client\"]", "extra[\"trace\"]", "RequestStarted(", "RequestFinished(" })
                Assert.Contains(k, host);
            Assert.Contains("[\"agent\"] = AgentName", Source("src/Bimbridge.Bridge/RevitConnection.cs"));
            Assert.Contains("Sessions.TryRegister(session", Source("src/Bimbridge.Addin/Revit/RequestHandler.cs"));
            Assert.Contains("corr.WriteTo(extra)", host);   // issue #4: correlation ids on every journal line
        }

        // ---- Queries ----

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
            var h = Source("src/Bimbridge.Addin/Revit/RequestHandler.cs");
            Assert.Contains("request.Args.ContainsKey(\"ops\")", h);
            Assert.True(h.IndexOf("await PlanOp(session, new Request", StringComparison.Ordinal) < h.IndexOf("await CommitPlan(session, request, ct)", StringComparison.Ordinal));
            Assert.Contains("plan_in_same_round_trip", h);
        }

        [Fact]
        public void ReviewFixes_Wiring()
        {
            var h = Source("src/Bimbridge.Addin/Revit/RequestHandler.cs");
            Assert.Contains("commit ops= does not accept key=", h);
            Assert.Contains("commit NOT run in this round trip", h);
            var host = Source("src/Bimbridge.Engine/PipeHost.cs");
            Assert.True(host.IndexOf("_sessions.Touch(session)", StringComparison.Ordinal) > host.IndexOf("req.Op == Wire.OpPing", StringComparison.Ordinal),
                "the heartbeat must not count as activity");
            var compact = Source("src/Bimbridge.Bridge/CompactTools.cs");
            Assert.Contains("Name = \"commit\", ReadOnly = false, Destructive = true, Idempotent = false", compact);
            Assert.Contains("_revit.AgentSource = AgentIdentity.SourceRegister", compact);
            Assert.Contains("DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode)", Source("src/Bimbridge.Addin/Revit/RequestHandler.Tasks.cs"));
        }

        // ---- Rebranding ----

        [Fact]
        public void Brand_TransactionsAttributed()
        {
            Assert.True(Brand.IsOwnTransaction("BIMBRIDGE · ensayo · P-1"));
            Assert.False(Brand.IsOwnTransaction("Mover"));
            Assert.True(Brand.IsTemporaryTransaction("BIMBRIDGE temporary exec"));
            Assert.False(Brand.IsTemporaryTransaction("BIMBRIDGE · ensayo"));
        }

        /// <summary>The previous name only appears in LegacyMigration.cs (it uses it to find and move old installations).</summary>
        [Fact]
        public void Brand_NoLegacyNameOutsideMigration()
        {
            var root = RepoRoot();
            var rx = LegacyMigration.NamePattern;
            var skip = new[] { ".png", ".ico", ".zip", ".dll", ".exe", ".rvt", ".rfa", ".pdf", ".whl", ".docx", ".xlsx" };
            var bad = new List<string>();
            foreach (var f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(root.Length + 1).Replace('\\', '/');
                if (Regex.IsMatch(rel, @"(^|/)(\.git|bin|obj|dist|artifacts|target|node_modules|\.venv|__pycache__)(/|$)")) continue;
                if (rel == "src/Bimbridge.Setup.Core/LegacyMigration.cs" || skip.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                var text = File.ReadAllText(f);
                if (text.IndexOf('\0') >= 0) continue;
                var m = rx.Match(text);
                if (m.Success) bad.Add(rel + ": " + m.Value);
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void Brand_ServerAndAddinAnnounceProduct()
        {
            Assert.Contains("Name = \"" + Brand.Product + "\"", Source("src/Bimbridge.Bridge/Program.cs"));
            Assert.Contains("<Name>" + Brand.Product + "</Name>", Source("src/Bimbridge.Addin/Bimbridge.addin"));
            Assert.Contains("\"DisplayName\", \"" + Brand.Product + "\"", Source("src/Bimbridge.Setup/Platform.cs"));
        }
    }
}
