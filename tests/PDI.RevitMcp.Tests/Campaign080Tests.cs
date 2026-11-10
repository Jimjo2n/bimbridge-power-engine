// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0: arnés de la campaña nativa (sin Revit). Sustitución, documento fijado, expectativas y validez de los archivos de pasos.</summary>
    public class Campaign080Tests
    {
        private static string Dir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return Path.Combine(d.FullName, "tools", "aceptacion", "0.8.0");
        }

        [Fact]
        public void Runner_PinsDocument_SubstitutesSavedValues_StopsOnFirstFailure()
        {
            var spec = Json.ParseObject("{\"esquema\":\"bimbridge.campana080/1\",\"item\":\"T\",\"pasos\":[" +
                "{\"op\":\"plan\",\"args\":{\"doc\":\"d9\",\"ops\":[]},\"guardar\":{\"plan\":\"plan_id\"}}," +
                "{\"op\":\"commit\",\"args\":{\"plan_id\":\"${plan}\"},\"escritura\":true,\"contiene\":[\"P-7\"]}," +
                "{\"op\":\"query\",\"args\":{\"doc\":\"d999\"},\"doc_explicito\":true,\"esperar\":\"destino_no_disponible\"}," +
                "{\"op\":\"commit\",\"args\":{\"plan_id\":\"${plan}\"},\"esperar\":\"rechazado\"}," +
                "{\"op\":\"query\",\"args\":{}}]}");
            var seen = new List<(string Op, string Doc, bool Write)>();
            Response Call(string op, Dictionary<string, object> a, bool w)
            {
                seen.Add((op, Arg.GetString(a, "doc"), w));
                if (op == "plan") return new Response { Datos = new Dictionary<string, object> { ["plan_id"] = "P-7" } };
                if (op == "commit") return new Response { Datos = new Dictionary<string, object> { ["plan_id"] = a["plan_id"] } };
                return Response.Fail(0, Status.DestinoNoDisponible, "no existe d999");
            }
            var r = Campaign080.RunSteps(spec, Call, "d1", null);
            Assert.Equal("FALLIDA", r["resultado"]);                 // el 4.º paso esperaba rechazado y fue ok
            Assert.Equal(4, seen.Count);                              // el 5.º no se ejecuta
            Assert.Equal("d1", seen[0].Doc);                          // el doc del archivo se sustituye por el fijado
            Assert.Equal("d999", seen[2].Doc);                        // salvo en el paso negativo explícito
            Assert.True(seen[1].Write);
            var pasos = (List<object>)r["pasos"];
            Assert.True((bool)((Dictionary<string, object>)pasos[1])["ok"]);
            Assert.Contains("no ejecutado", (string)((Dictionary<string, object>)pasos[4])["motivo"]);
        }

        [Fact]
        public void Runner_MissingVariable_FailsWithoutCalling()
        {
            var spec = Json.ParseObject("{\"esquema\":\"bimbridge.campana080/1\",\"item\":\"T\",\"pasos\":[{\"op\":\"query\",\"args\":{\"ids\":\"${ids}\"}}]}");
            var calls = 0;
            var r = Campaign080.RunSteps(spec, (o, a, w) => { calls++; return new Response(); }, "d1", null);
            Assert.Equal("FALLIDA", r["resultado"]);
            Assert.Equal(0, calls);
            Assert.Equal(17L, Campaign080.Subst("${n}", new Dictionary<string, string> { ["n"] = "17" }));
        }

        [Fact]
        public void StepFiles_AreWellFormed_AndTheirPlansValidate()
        {
            var files = Directory.GetFiles(Dir(), "N8-*.json").Where(f => !f.EndsWith("_plan.json", StringComparison.Ordinal)).ToArray();
            Assert.True(files.Length >= 11);
            foreach (var f in files)
            {
                var spec = Json.ParseObject(File.ReadAllText(f));
                Assert.Equal(Campaign080.Schema, spec["esquema"]);
                var vars = ((Dictionary<string, object>)spec["variables"]).Keys.ToDictionary(k => k, k => k == "nivel" ? "Level 1" : k.StartsWith("ids") || k.StartsWith("lineas") ? "101,102,103" : "123");
                vars["plan"] = "P-1";
                foreach (Dictionary<string, object> s in (List<object>)spec["pasos"])
                {
                    var args = (Dictionary<string, object>)Campaign080.Subst(s["args"], vars);
                    if ((string)s["op"] != "plan") continue;
                    var negative = (Arg.GetString(s, "esperar") ?? "ok") != "ok";
                    if (negative && Arg.GetBool(s, "depende_del_modelo") == true) continue;
                    if (negative) Assert.ThrowsAny<Exception>(() => PDI.RevitMcp.Engine.OpSpec.ParseAll(args));   // los negativos fallan ya sin Revit, salvo los que dependen del modelo
                    else PDI.RevitMcp.Engine.OpSpec.ParseAll(args);
                }
            }
        }
    }
}

namespace PDI.RevitMcp.Tests
{
    public class Campaign080PlanTemplateTests
    {
        /// <summary>N8-10: plantilla IR de contraste independiente (sólo lectura). Se regenera con PDI_GEN_MATRIZ=1 y debe validar.</summary>
        [Fact]
        public void N810_Template_ValidatesAsIr()
        {
            var d = new PlanDraft { Objective = "N8-10 contraste independiente (sólo lectura)", DocKey = "CLAVE_DEL_DOCUMENTO", DocPrint = "HUELLA_DEL_DOCUMENTO", LengthUnit = "mm", Coordinates = "interno_revit" };
            var v = new OpDraft { Operation = "verificar_independiente" };
            v.Scope["categoria"] = "Doors";
            v.Params["fuente_id"] = "TABLA-PUERTAS-PRUEBA"; v.Params["fuente_tipo"] = "tabla"; v.Params["fuente_construccion"] = "N8-03";
            // 0.9.0: SHA-256 de la tabla de prueba (archivo TABLA-PUERTAS-PRUEBA.csv de la campaña); sustituir por el real al correr N8-10.
            v.Params["fuente_sha256"] = "SHA256_DE_LA_TABLA_DE_PRUEBA_0000000000000000000000000000000000";
            v.Params["parametro_clave"] = "Mark";
            v.Params["esperado"] = new System.Collections.Generic.Dictionary<string, object> { ["D-101"] = "-", ["D-102"] = "-", ["D-999"] = "-" };
            v.Params["excepciones"] = new System.Collections.Generic.Dictionary<string, object> { ["D-103"] = "justificada en la fuente, hoja 2" };
            d.Ops.Add(v); d.AssignIds();
            var text = Json.Write(d.ToJson());
            var path = Path.Combine(Path.GetDirectoryName(typeof(Campaign080Tests).Assembly.Location)!, "..", "..", "..", "..", "..", "tools", "aceptacion", "0.8.0", "N8-10_plan.json");
            if (Environment.GetEnvironmentVariable("PDI_GEN_MATRIZ") == "1") File.WriteAllText(path, text);
            var errs = IrValidator.Validate(PlanDraft.Parse(File.ReadAllText(path)), out _);
            Assert.Empty(errs);
        }
    }
}
