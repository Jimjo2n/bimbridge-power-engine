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
        private const string Excel = """[{"id":"F-1","origen":"Panel","destino":"Meter"},{"id":"F-2","origen":"Meter","abierto":true}]""";
        private const string Pdf = """[{"id":"F-1","origen":"Panel","destino":"Meter"},{"id":"F-3","origen":"Meter","destino":"Branch"}]""";

        [Fact]
        public void IndependentAuditFindsOneOmittedBranchAcrossBothSources()
        {
            var args = new Dictionary<string, object> { ["observado"] = Rows(Excel) };
            var audit = RiserSourceWorkflow.Build(args, Rows(Excel), Rows(Pdf), audit: true);
            Assert.Equal("con_diferencias", audit["estado"]);
            var difference = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)audit["diferencias"]));
            Assert.Equal("F-3", difference["id"]);
            Assert.Equal("faltante", difference["tipo"]);
        }

        [Fact]
        public void PlansSymbolsOpenTerminalAndBranchWithTypedOperations()
        {
            var args = Json.ParseObject("""
                {"vista":712,"estilo":"Magenta",
                 "nodos":[{"id":"Panel","tipo":"Equipos: Panel","punto":[0,0]},
                          {"id":"Meter","tipo":"Equipos: Meter","punto":[5,0]},
                          {"id":"Branch","tipo":"Equipos: Branch","punto":[5,5]}],
                 "trazado":[{"id":"F-1","rotulo":[2,1]},{"id":"F-2","fin_abierto":[10,0],"rotulo":[8,1]},
                            {"id":"F-3","rotulo":[6,3]}]}
                """);
            var result = RiserSourceWorkflow.Build(args, Rows(Excel), Rows(Pdf), audit: false);
            var plan = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["planes"]));
            var ops = Rows((string)plan["ops"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "detail_component")));
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "detail_line")));
            Assert.Equal(3, ops.Count(o => Equals(o["op"], "text_note")));
            var edges = ((List<object>)result["esperado_dibujo"]).Cast<Dictionary<string, object>>()
                .ToDictionary(edge => (string)edge["id"]);
            Assert.Equal("Equipos: Meter", edges["F-1"]["tipo_destino"]);
            Assert.Equal("Equipos: Meter", edges["F-2"]["tipo_origen"]);
            Assert.Null(edges["F-2"]["tipo_destino"]);
        }

        [Fact]
        public void SourceDisagreementDoesNotInventPrecedence()
        {
            var args = new Dictionary<string, object>();
            var changed = Rows("""[{"id":"F-1","origen":"Panel","destino":"Branch"}]""");
            var result = RiserSourceWorkflow.Build(args, Rows(Excel), changed, audit: false);
            Assert.Equal("fuentes_en_conflicto", result["estado"]);
            Assert.False(result.ContainsKey("planes"));
        }
    }
}
