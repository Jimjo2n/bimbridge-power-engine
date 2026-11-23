// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimbridge.AgentHost;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.8.0: native campaign harness (without Revit). Substitution, pinned document, expectations and validity of the step files.</summary>
    public class Campaign080Tests
    {
        private static string Dir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return Path.Combine(d.FullName, "tools", "acceptance", "0.8.0");
        }

        [Fact]
        public void Runner_PinsDocument_SubstitutesSavedValues_StopsOnFirstFailure()
        {
            var spec = Json.ParseObject("{\"schema\":\"bimbridge.campaign080/1\",\"item\":\"T\",\"steps\":[" +
                "{\"op\":\"plan\",\"args\":{\"doc\":\"d9\",\"ops\":[]},\"save\":{\"plan\":\"plan_id\"}}," +
                "{\"op\":\"commit\",\"args\":{\"plan_id\":\"${plan}\"},\"write\":true,\"contains\":[\"P-7\"]}," +
                "{\"op\":\"query\",\"args\":{\"doc\":\"d999\"},\"explicit_doc\":true,\"wait\":\"target_unavailable\"}," +
                "{\"op\":\"commit\",\"args\":{\"plan_id\":\"${plan}\"},\"wait\":\"rejected\"}," +
                "{\"op\":\"query\",\"args\":{}}]}");
            var seen = new List<(string Op, string Doc, bool Write)>();
            Response Call(string op, Dictionary<string, object> a, bool w)
            {
                seen.Add((op, Arg.GetString(a, "doc"), w));
                if (op == "plan") return new Response { Data = new Dictionary<string, object> { ["plan_id"] = "P-7" } };
                if (op == "commit") return new Response { Data = new Dictionary<string, object> { ["plan_id"] = a["plan_id"] } };
                return Response.Fail(0, Status.TargetUnavailable, "d999 does not exist");
            }
            var r = Campaign080.RunSteps(spec, Call, "d1", null);
            Assert.Equal("FAILED", r["result"]);                 // the 4th step expected rechazado and was ok
            Assert.Equal(4, seen.Count);                              // the 5th does not run
            Assert.Equal("d1", seen[0].Doc);                          // the file's doc is replaced by the pinned one
            Assert.Equal("d999", seen[2].Doc);                        // except in the explicit negative step
            Assert.True(seen[1].Write);
            var steps = (List<object>)r["steps"];
            Assert.True((bool)((Dictionary<string, object>)steps[1])["ok"]);
            Assert.Contains("not run", (string)((Dictionary<string, object>)steps[4])["reason"]);
        }

        [Fact]
        public void Runner_MissingVariable_FailsWithoutCalling()
        {
            var spec = Json.ParseObject("{\"schema\":\"bimbridge.campaign080/1\",\"item\":\"T\",\"steps\":[{\"op\":\"query\",\"args\":{\"ids\":\"${ids}\"}}]}");
            var calls = 0;
            var r = Campaign080.RunSteps(spec, (o, a, w) => { calls++; return new Response(); }, "d1", null);
            Assert.Equal("FAILED", r["result"]);
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
                Assert.Equal(Campaign080.Schema, spec["schema"]);
                var vars = ((Dictionary<string, object>)spec["variables"]).Keys.ToDictionary(k => k, k => k == "level" ? "Level 1" : k.StartsWith("ids") || k.Contains("lines") ? "101,102,103" : "123");
                vars["plan"] = "P-1";
                foreach (Dictionary<string, object> s in (List<object>)spec["steps"])
                {
                    var args = (Dictionary<string, object>)Campaign080.Subst(s["args"], vars);
                    if ((string)s["op"] != "plan") continue;
                    var negative = (Arg.GetString(s, "wait") ?? "ok") != "ok";
                    if (negative && Arg.GetBool(s, "depends_on_model") == true) continue;
                    if (negative) Assert.ThrowsAny<Exception>(() => Bimbridge.Engine.OpSpec.ParseAll(args));   // negative steps already fail without Revit, except those that depend on the model
                    else Bimbridge.Engine.OpSpec.ParseAll(args);
                }
            }
        }
    }
}

namespace Bimbridge.Tests
{
    public class Campaign080PlanTemplateTests
    {
        /// <summary>N8-10: IR template for independent cross-check (read only). Regenerated with BIMBRIDGE_GEN_MATRIZ=1 and must validate.</summary>
        [Fact]
        public void N810_Template_ValidatesAsIr()
        {
            var d = new PlanDraft { Objective = "N8-10 contraste independiente (sólo lectura)", DocKey = "CLAVE_DEL_DOCUMENTO", DocPrint = "HUELLA_DEL_DOCUMENTO", LengthUnit = "mm", Coordinates = "revit_internal" };
            var v = new OpDraft { Operation = "verify_independent" };
            v.Scope["category"] = "Doors";
            v.Params["source_id"] = "TABLA-PUERTAS-PRUEBA"; v.Params["source_type_kind"] = "schedule"; v.Params["source_construction"] = "N8-03";
            // 0.9.0: SHA-256 of the test table (campaign file TABLA-PUERTAS-PRUEBA.csv); replace with the real one when running N8-10.
            v.Params["source_sha256"] = "SHA256_DE_LA_TABLA_DE_PRUEBA_0000000000000000000000000000000000";
            v.Params["key_parameter"] = "Mark";
            v.Params["expected"] = new System.Collections.Generic.Dictionary<string, object> { ["D-101"] = "-", ["D-102"] = "-", ["D-999"] = "-" };
            v.Params["exceptions"] = new System.Collections.Generic.Dictionary<string, object> { ["D-103"] = "justificada en la fuente, hoja 2" };
            d.Ops.Add(v); d.AssignIds();
            var text = Json.Write(d.ToJson());
            var path = Path.Combine(Path.GetDirectoryName(typeof(Campaign080Tests).Assembly.Location)!, "..", "..", "..", "..", "..", "tools", "acceptance", "0.8.0", "N8-10_plan.json");
            if (Environment.GetEnvironmentVariable("BIMBRIDGE_GEN_MATRIZ") == "1") File.WriteAllText(path, text);
            var errs = IrValidator.Validate(PlanDraft.Parse(File.ReadAllText(path)), out _);
            Assert.Empty(errs);
        }
    }
}
