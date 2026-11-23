// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BimBridge.PowerEngine;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Compact "power" tool (Bridge) end to end, no Revit: discovery, folders, PDF->ops workflow.</summary>
    public class PowerCommandsTests
    {
        private static readonly string Fx = PowerEngineEnv.Fixtures;

        private static string Grids()
        {
            var l = new List<string>();
            var gx = new[] { 0, 30, 60, 90, 120, 150 }; var gy = new[] { 0, 25, 50, 75, 100 };
            for (var i = 0; i < gx.Length; i++) l.Add("{\"name\":\"" + "ABCDEF"[i] + "\",\"p\":[" + gx[i] + ",-5," + gx[i] + ",105]}");
            for (var j = 0; j < gy.Length; j++) l.Add("{\"name\":\"" + (j + 1) + "\",\"p\":[-5," + gy[j] + ",155," + gy[j] + "]}");
            return "[" + string.Join(",", l) + "]";
        }

        private const string Rules = "[{\"code\":\"D\",\"type\":\"LTG_DOWNLIGHT-SURFACE - ROUND: D\",\"mounting\":\"slab\",\"ws\":\"LIGHTING - DIST\"}," +
                                     "{\"code\":\"G\",\"type\":\"LTG_DOWNLIGHT-SURFACE - ROUND: G\",\"mounting\":\"slab\",\"ws\":\"LIGHTING-EMERGENCY - DIST\"}," +
                                     "{\"code\":\"S\",\"type\":\"LTG_STRIP LIGHT: S\",\"elev\":9.25,\"ws\":\"LIGHTING - DIST\",\"directional\":true}]";

        [Fact]
        public void Flow_FromOneLine_ProducesPlaceOpsForPlanCommit()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var line = "flujo path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\" page=1 level=\"LEVEL 09\" tol=0.05 rules=" + Rules + " grids=" + Grids();
                var r = PowerCommands.Run(pe, new List<string> { Fx }, line);
                Assert.Equal("ready_with_ambiguities", r["status"]);
                var ops = ((List<object>)r["ops"]).Cast<string>().Select(Json.ParseObject).ToList();
                Assert.All(ops, o => Assert.Equal("place", o["op"]));
                Assert.Equal(72, ops.Sum(o => ((List<object>)o["points"]).Count));
                Assert.Equal(30L, ((Dictionary<string, object>)r["anchors"])["common"]);
                // the compact response does not carry the items one by one
                Assert.True(Json.Write(r).Length < 20000, "response " +Json.Write(r).Length);
            }
        }

        [Fact]
        public void Flow_UsesOnlyReviewedSymbolsAndCarriesTheirEvidence()
        {
            using var pe = new PowerEngineDispatcher(PowerEngineEnv.Options());
            var folders = new List<string> { Fx };
            var path = "path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"";
            var result = PowerCommands.Run(pe, folders, "pdf symbols " + path + " traceability=true");
            Assert.Equal("OK", result["code"]);
            var symbols = (Dictionary<string, object>)result["result"];
            var page = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)symbols["pages"]));
            var candidates = ((List<object>)page["candidates"]).Cast<Dictionary<string, object>>().Take(2).ToList();
            var included = candidates[0]["symbol_id"];
            var excluded = candidates[1]["symbol_id"];
            var review = new[]
            {
                new Dictionary<string, object> { ["symbol_id"] = included, ["decision"] = "include", ["evidence"] = "legend and note 13" },
                new Dictionary<string, object> { ["symbol_id"] = excluded, ["decision"] = "exclude", ["evidence"] = "another device in the reference" },
            };
            var command = "flujo " + path + " level=\"LEVEL 09\" tol=0.05 rules=" + Rules + " grids=" + Grids();
            var audited = PowerCommands.Run(pe, folders, command + " decisions=" + Json.Write(review));
            var operation = Json.ParseObject(Assert.IsType<string>(Assert.Single((List<object>)audited["ops"])));
            Assert.Single((List<object>)operation["points"]);
            var inventory = ((List<object>)audited["inventory"]).Cast<Dictionary<string, object>>().ToDictionary(x => x["symbol_id"]);
            Assert.Equal("included", inventory[included]["status"]);
            Assert.True(inventory[included].ContainsKey("xy_ft"));
            Assert.True(inventory[included].ContainsKey("placement_id"));
            Assert.Equal("excluded", inventory[excluded]["status"]);
            Assert.Equal("another device in the reference", inventory[excluded]["evidence_revision"]);
            Assert.Contains(inventory.Values, x => Equals(x["status"], "unreviewed"));
            review[0]["symbol_id"] = "revision-anterior";
            var stale = PowerCommands.Run(pe, folders, command + " decisions=" + Json.Write(review));
            Assert.False(stale.ContainsKey("ops"));
        }

        [Fact]
        public void Documents_WithoutAuthorizedFolders_AreRejected_AndOutsidePathsDenied()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var none = PowerCommands.Run(pe, new List<string>(), "pdf info path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal("INVALID", none["code"]);
                var other = Path.Combine(Path.GetTempPath(), "bb-otra-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(other);
                var denied = PowerCommands.Run(pe, new List<string> { other }, "pdf info path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal(PowerEngineContracts.Codes.PathDenied, denied["code"]);
                var ok = PowerCommands.Run(pe, new List<string> { Fx }, "pdf info path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal("OK", ok["code"]);
            }
        }

        [Fact]
        public void Flow_WithoutModelGrids_RefusesInsteadOfGuessing()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var r = PowerCommands.Run(pe, new List<string> { Fx }, "flujo path=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\" rules=" + Rules);
                Assert.Equal("INVALID", r["code"]);
            }
        }

        [Fact]
        public void Cap_SearchIsBrief_AndExactIdIsFull()
        {
            using (var pe = new PowerEngineDispatcher(new PowerEngineOptions()))
            {
                var s = PowerCommands.Run(pe, new List<string>(), "cap grids registration");
                var ids = ((List<object>)s["capabilities"]).Cast<Dictionary<string, object>>().Select(d => (string)d["id"]).ToList();
                Assert.Contains("pdf.grids", ids);
                var f = PowerCommands.Run(pe, new List<string>(), "cap pdf.grids");
                Assert.Equal("AVAILABLE", f["status"]);
                Assert.True(f.ContainsKey("restrictions"));
            }
        }

        [Fact]
        public void LabFolders_OnlyCountInLabProfile()
        {
            var cfg = new EngineConfig { DocumentFolders = new List<string> { "/docs" } };
            cfg.Profile = ProfilePolicy.Parse("lab", new[] { "/lab" }, false, null);
            Assert.Equal(2, PowerCommands.AuthorizedFolders(cfg).Count);
            cfg.Profile = ProfilePolicy.Parse("production", new[] { "/lab" }, false, null);
            Assert.Single(PowerCommands.AuthorizedFolders(cfg));
        }

        [Fact]
        public void Config_ParsesPowerEngineSection()
        {
            var p = Path.Combine(Path.GetTempPath(), "bb-cfg-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(p, "{\"document_folders\":[\"/d\"],\"power_engine\":{\"python\":\"/usr/bin/python3\",\"geometry\":\"RUST_FFI\",\"rust_worker\":\"/x\"}}");
            var c = EngineConfig.Load(p);
            Assert.Equal(new[] { "/d" }, c.DocumentFolders);
            Assert.Equal("/usr/bin/python3", c.PythonExe);
            Assert.Equal("rust_ffi", c.Geometry);
            File.WriteAllText(p, "{\"power_engine\":{\"geometry\":\"cualquiera\"}}");
            Assert.Equal("auto", EngineConfig.Load(p).Geometry);
        }
    
        [Fact]
        public void Bench_SmallRun_AgreesAcrossEngines_AndReportsEstimatedTokens()
        {
            var r = PowerEngineBench.Run(new PowerEngineBench.Settings { Sizes = new[] { 50, 500 }, SegmentSizes = new[] { 50, 300 }, Rounds = 1, RustWorkerExe = PowerEngineEnv.RustWorker });
            foreach (Dictionary<string, object> row in (List<object>)r["geometry_ms_median"])
                Assert.DoesNotContain(row.Keys, k => k.StartsWith("difference_", StringComparison.Ordinal));
            var t = (Dictionary<string, object>)r["text"];
            Assert.StartsWith("ESTIMATED tokens", (string)t["method"]);
            Assert.True(Convert.ToDouble(((Dictionary<string, object>)t["discovery"])["reduction_pct"], CultureInfo.InvariantCulture) > 50);
            Assert.True(Convert.ToDouble(((Dictionary<string, object>)t["placement_72"])["chars_reduction_pct"], CultureInfo.InvariantCulture) > 50);
        }
    
        [Fact]
        public void Red_FromJsonFile_SeparatesProximityContinuityAndConnection()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bb-red-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // 1-2 physically connected; 2-3 touch without connecting; 3-4 with a 0.2 ft gap (proximity only)
            File.WriteAllText(Path.Combine(dir, "t.json"), "[" +
                "{\"id\":\"1\",\"a\":[0,0,10],\"b\":[10,0,10],\"diam\":0.0625,\"with_b\":\"2\"}," +
                "{\"id\":\"2\",\"a\":[10,0,10],\"b\":[20,0,10],\"diam\":0.0625,\"with_a\":\"1\"}," +
                "{\"id\":\"3\",\"a\":[20,0,10],\"b\":[30,0,10],\"diam\":0.0625}," +
                "{\"id\":\"4\",\"a\":[30.2,0,10],\"b\":[40,0,10],\"diam\":0.0625}]");
            using (var pe = new PowerEngineDispatcher(new PowerEngineOptions()))
            {
                var r = PowerCommands.Run(pe, new List<string> { dir }, "red path=\"" + Path.Combine(dir, "t.json") + "\"");
                var j = Json.Write(r);
                Assert.Contains("touches_unconnected", j);
                Assert.Contains("near_unconnected", j);
                var denied = PowerCommands.Run(pe, new List<string> { Fx }, "red path=\"" + Path.Combine(dir, "t.json") + "\"");
                Assert.Equal(PowerEngineContracts.Codes.PathDenied, denied["code"]);
            }
        }
    }
}
