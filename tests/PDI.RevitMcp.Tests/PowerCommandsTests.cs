// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BimBridge.PowerEngine;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Herramienta compacta "power" (Bridge) de punta a punta, sin Revit: descubrimiento, carpetas, flujo PDF→ops.</summary>
    public class PowerCommandsTests
    {
        private static readonly string Fx = PowerEngineEnv.Fixtures;

        private static string Grids()
        {
            var l = new List<string>();
            var gx = new[] { 0, 30, 60, 90, 120, 150 }; var gy = new[] { 0, 25, 50, 75, 100 };
            for (var i = 0; i < gx.Length; i++) l.Add("{\"nombre\":\"" + "ABCDEF"[i] + "\",\"p\":[" + gx[i] + ",-5," + gx[i] + ",105]}");
            for (var j = 0; j < gy.Length; j++) l.Add("{\"nombre\":\"" + (j + 1) + "\",\"p\":[-5," + gy[j] + ",155," + gy[j] + "]}");
            return "[" + string.Join(",", l) + "]";
        }

        private const string Rules = "[{\"codigo\":\"D\",\"tipo\":\"LTG_DOWNLIGHT-SURFACE - ROUND: D\",\"montaje\":\"losa\",\"ws\":\"LIGHTING - DIST\"}," +
                                     "{\"codigo\":\"G\",\"tipo\":\"LTG_DOWNLIGHT-SURFACE - ROUND: G\",\"montaje\":\"losa\",\"ws\":\"LIGHTING-EMERGENCY - DIST\"}," +
                                     "{\"codigo\":\"S\",\"tipo\":\"LTG_STRIP LIGHT: S\",\"elev\":9.25,\"ws\":\"LIGHTING - DIST\",\"direccional\":true}]";

        [Fact]
        public void Flujo_FromOneLine_ProducesPlaceOpsForPlanCommit()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var line = "flujo ruta=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\" pagina=1 nivel=\"LEVEL 09\" tol=0.05 reglas=" + Rules + " ejes=" + Grids();
                var r = PowerCommands.Run(pe, new List<string> { Fx }, line);
                Assert.Equal("listo_con_ambiguedades", r["estado"]);
                var ops = ((List<object>)r["ops"]).Cast<string>().Select(Json.ParseObject).ToList();
                Assert.All(ops, o => Assert.Equal("place", o["op"]));
                Assert.Equal(72, ops.Sum(o => ((List<object>)o["puntos"]).Count));
                Assert.Equal(30L, ((Dictionary<string, object>)r["anclas"])["comunes"]);
                // la respuesta compacta no lleva los ítems uno a uno
                Assert.True(Json.Write(r).Length < 20000, "respuesta " + Json.Write(r).Length);
            }
        }

        [Fact]
        public void Documents_WithoutAuthorizedFolders_AreRejected_AndOutsidePathsDenied()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var none = PowerCommands.Run(pe, new List<string>(), "pdf info ruta=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal("INVALIDO", none["codigo"]);
                var other = Path.Combine(Path.GetTempPath(), "bb-otra-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(other);
                var denied = PowerCommands.Run(pe, new List<string> { other }, "pdf info ruta=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal(PowerEngineContracts.Codes.PathDenied, denied["codigo"]);
                var ok = PowerCommands.Run(pe, new List<string> { Fx }, "pdf info ruta=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\"");
                Assert.Equal("OK", ok["codigo"]);
            }
        }

        [Fact]
        public void Flujo_WithoutModelGrids_RefusesInsteadOfGuessing()
        {
            using (var pe = new PowerEngineDispatcher(PowerEngineEnv.Options()))
            {
                var r = PowerCommands.Run(pe, new List<string> { Fx }, "flujo ruta=\"" + Path.Combine(Fx, "plan_vectorial.pdf") + "\" reglas=" + Rules);
                Assert.Equal("INVALIDO", r["codigo"]);
            }
        }

        [Fact]
        public void Cap_SearchIsBrief_AndExactIdIsFull()
        {
            using (var pe = new PowerEngineDispatcher(new PowerEngineOptions()))
            {
                var s = PowerCommands.Run(pe, new List<string>(), "cap ejes registro");
                var ids = ((List<object>)s["capacidades"]).Cast<Dictionary<string, object>>().Select(d => (string)d["id"]).ToList();
                Assert.Contains("pdf.grids", ids);
                var f = PowerCommands.Run(pe, new List<string>(), "cap pdf.grids");
                Assert.Equal("AVAILABLE", f["estado"]);
                Assert.True(f.ContainsKey("restricciones"));
            }
        }

        [Fact]
        public void LabFolders_OnlyCountInLabProfile()
        {
            var cfg = new EngineConfig { DocumentFolders = new List<string> { "/docs" } };
            cfg.Profile = ProfilePolicy.Parse("laboratorio", new[] { "/lab" }, false, null);
            Assert.Equal(2, PowerCommands.AuthorizedFolders(cfg).Count);
            cfg.Profile = ProfilePolicy.Parse("produccion", new[] { "/lab" }, false, null);
            Assert.Single(PowerCommands.AuthorizedFolders(cfg));
        }

        [Fact]
        public void Config_ParsesPowerEngineSection()
        {
            var p = Path.Combine(Path.GetTempPath(), "bb-cfg-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(p, "{\"carpetas_documentos\":[\"/d\"],\"power_engine\":{\"python\":\"/usr/bin/python3\",\"geometria\":\"RUST_FFI\",\"rust_worker\":\"/x\"}}");
            var c = EngineConfig.Load(p);
            Assert.Equal(new[] { "/d" }, c.DocumentFolders);
            Assert.Equal("/usr/bin/python3", c.PythonExe);
            Assert.Equal("rust_ffi", c.Geometry);
            File.WriteAllText(p, "{\"power_engine\":{\"geometria\":\"cualquiera\"}}");
            Assert.Equal("auto", EngineConfig.Load(p).Geometry);
        }
    
        [Fact]
        public void Bench_SmallRun_AgreesAcrossEngines_AndReportsEstimatedTokens()
        {
            var r = PowerEngineBench.Run(new PowerEngineBench.Settings { Sizes = new[] { 50, 500 }, SegmentSizes = new[] { 50, 300 }, Rounds = 1, RustWorkerExe = PowerEngineEnv.RustWorker });
            foreach (Dictionary<string, object> row in (List<object>)r["geometria_ms_mediana"])
                Assert.DoesNotContain(row.Keys, k => k.StartsWith("diferencia_", StringComparison.Ordinal));
            var t = (Dictionary<string, object>)r["texto"];
            Assert.StartsWith("tokens ESTIMADOS", (string)t["metodo"]);
            Assert.True(Convert.ToDouble(((Dictionary<string, object>)t["descubrimiento"])["reduccion_pct"], CultureInfo.InvariantCulture) > 50);
            Assert.True(Convert.ToDouble(((Dictionary<string, object>)t["colocacion_72"])["reduccion_chars_pct"], CultureInfo.InvariantCulture) > 50);
        }
    
        [Fact]
        public void Red_FromJsonFile_SeparatesProximityContinuityAndConnection()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bb-red-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // 1–2 conectados físicamente; 2–3 se tocan sin conectar; 3–4 con hueco de 0,2 ft (sólo proximidad)
            File.WriteAllText(Path.Combine(dir, "t.json"), "[" +
                "{\"id\":\"1\",\"a\":[0,0,10],\"b\":[10,0,10],\"diam\":0.0625,\"con_b\":\"2\"}," +
                "{\"id\":\"2\",\"a\":[10,0,10],\"b\":[20,0,10],\"diam\":0.0625,\"con_a\":\"1\"}," +
                "{\"id\":\"3\",\"a\":[20,0,10],\"b\":[30,0,10],\"diam\":0.0625}," +
                "{\"id\":\"4\",\"a\":[30.2,0,10],\"b\":[40,0,10],\"diam\":0.0625}]");
            using (var pe = new PowerEngineDispatcher(new PowerEngineOptions()))
            {
                var r = PowerCommands.Run(pe, new List<string> { dir }, "red ruta=\"" + Path.Combine(dir, "t.json") + "\"");
                var j = Json.Write(r);
                Assert.Contains("toca_sin_conectar", j);
                Assert.Contains("cerca_sin_conectar", j);
                var denied = PowerCommands.Run(pe, new List<string> { Fx }, "red ruta=\"" + Path.Combine(dir, "t.json") + "\"");
                Assert.Equal(PowerEngineContracts.Codes.PathDenied, denied["codigo"]);
            }
        }
    }
}
