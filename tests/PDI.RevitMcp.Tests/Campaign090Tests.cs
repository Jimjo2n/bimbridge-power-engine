// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Campaña nativa 0.9.0: los archivos están bien formados y sus planes validan sin Revit. Ejecutarla sigue BLOCKED.</summary>
    public class Campaign090Tests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return d!.FullName;
        }

        [Fact]
        public void StepFiles_AreBlocked_WellFormed_AndPlansValidate_WithJsonOps()
        {
            var dir = Path.Combine(Root(), "tools", "aceptacion", "0.9.0");
            var files = Directory.GetFiles(dir, "N9-*.json");
            Assert.Equal(6, files.Length);
            var ops = "[{\"op\":\"place\",\"type\":\"F: T\",\"level\":\"Level 1\",\"puntos\":[[1,2],[3,4]],\"montaje\":\"losa\"}]";
            foreach (var f in files)
            {
                var spec = Json.ParseObject(File.ReadAllText(f));
                Assert.Equal(Campaign080.Schema, spec["esquema"]);
                Assert.Equal("BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION", spec["estado"]);
                var vars = ((Dictionary<string, object>)spec["variables"]).Keys.ToDictionary(k => k, k => k == "ops" ? ops : k == "nivel" ? "Level 1" : k.StartsWith("ids") ? "101,102" : k.StartsWith("codigo") ? "return 1;" : "123");
                vars["plan"] = "P-1";
                foreach (Dictionary<string, object> s in (List<object>)spec["pasos"])
                {
                    var args = (Dictionary<string, object>)Campaign080.Subst(s["args"], vars);
                    if ((string)s["op"] == "plan") PDI.RevitMcp.Engine.OpSpec.ParseAll(args);
                }
            }
        }

        [Fact]
        public void Vars_FromFile_AndJsonValues_BecomeStructures()
        {
            var p = Path.Combine(Path.GetTempPath(), "bb-ops-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(p, "[{\"op\":\"delete\",\"ids\":\"1\"}]");
            var vars = Campaign080.ParseVars(new[] { "ops=@" + p, "n=12" });
            var r = (Dictionary<string, object>)Campaign080.Subst(new Dictionary<string, object> { ["ops"] = "${ops}", ["x"] = "${n}" }, vars);
            Assert.IsType<List<object>>(r["ops"]);
            Assert.Equal(12L, r["x"]);
        }
    }
}
