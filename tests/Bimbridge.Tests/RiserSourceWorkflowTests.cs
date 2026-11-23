// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using BimBridge.PowerEngine.Workflows;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    public class RiserSourceWorkflowTests
    {
        private static List<object> Rows(string json) => (List<object>)Json.Parse(json);
        private const string Excel = """[{"id":"F-1","origin":"Panel","target":"Meter"},{"id":"F-2","origin":"Meter","open":true}]""";
        private const string Pdf = """[{"id":"F-1","origin":"Panel","target":"Meter"},{"id":"F-3","origin":"Meter","target":"Branch"}]""";

        [Fact]
        public void IndependentAuditFindsOneOmittedBranchAcrossBothSources()
        {
            var args = new Dictionary<string, object> { ["observed"] = Rows(Excel) };
            var audit = RiserSourceWorkflow.Build(args, Rows(Excel), Rows(Pdf), audit: true);
            Assert.Equal("with_differences", audit["status"]);
            var difference = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)audit["differences"]));
            Assert.Equal("F-3", difference["id"]);
            Assert.Equal("missing", difference["type"]);
        }

        [Fact]
        public void PlansSymbolsOpenTerminalAndBranchWithTypedOperations()
        {
            var args = Json.ParseObject("""
                {"view":712,"style":"Magenta",
                 "nodes":[{"id":"Panel","type":"Equipos: Panel","point":[0,0]},
                          {"id":"Meter","type":"Equipos: Meter","point":[5,0]},
                          {"id":"Branch","type":"Equipos: Branch","point":[5,5]}],
                 "trace_path":[{"id":"F-1","label":[2,1]},{"id":"F-2","open_end":[10,0],"label":[8,1]},
                            {"id":"F-3","label":[6,3]}]}
                """);
            var result = RiserSourceWorkflow.Build(args, Rows(Excel), Rows(Pdf), audit: false);
            var plan = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["plans"]));
            var ops = Rows((string)plan["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "detail_component")));
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "detail_line")));
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "text_note")));
            var edges = ((List<object>)result["expected_drawing"]).Cast<Dictionary<string, object>>()
                .ToDictionary(edge => (string)edge["id"]);
            Assert.Equal("Equipos: Meter", edges["F-1"]["target_type"]);
            Assert.Equal("Equipos: Meter", edges["F-2"]["source_type"]);
            Assert.Null(edges["F-2"]["target_type"]);
        }

        [Fact]
        public void SourceDisagreementDoesNotInventPrecedence()
        {
            var args = new Dictionary<string, object>();
            var changed = Rows("""[{"id":"F-1","origin":"Panel","target":"Branch"}]""");
            var result = RiserSourceWorkflow.Build(args, Rows(Excel), changed, audit: false);
            Assert.Equal("conflicting_sources", result["status"]);
            Assert.False(result.ContainsKey("plans"));
        }
    }
}
