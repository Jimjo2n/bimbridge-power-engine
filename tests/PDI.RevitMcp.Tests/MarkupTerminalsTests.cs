// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using BimBridge.PowerEngine.Workflows;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class MarkupTerminalsTests
    {
        private const string Input = """
            {
              "nivel":"L1","parametro_sentido":"Dirección",
              "anclas":[{"nombre":"A","pdf":[0,0],"modelo":[10,20]},
                        {"nombre":"B","pdf":[10,0],"modelo":[10,30]},
                        {"nombre":"C","pdf":[0,10],"modelo":[0,20]}],
              "items":[{"ref":"caja","pt":[2,4],"tipo":"Caja: Piso","sentido":"DN","elev":0,"existente_id":800},
                       {"ref":"stub","pt":[3,5],"tipo":"Stub: EMT","sentido":"UP","elev":1,"giro_pt":0}]
            }
            """;

        [Fact]
        public void RotatedMarkupProducesOneIdempotentPlacementAndPreservesTheExistingBox()
        {
            var args = Json.ParseObject(Input);
            var result = MarkupPlacement.Plan(args, new string('d', 64), "Archi Units.pdf#p1", 1);
            Assert.Equal("listo", result["estado"]);
            var rows = (List<object>)result["terminales"];
            Assert.Equal(800L, ((Dictionary<string, object>)rows[0])["id"]);
            var batch = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["planes"]));
            var ops = Assert.IsType<List<object>>(Json.Parse((string)batch["ops"]));
            var op = Assert.IsType<Dictionary<string, object>>(Assert.Single(ops));
            var point = Assert.IsType<List<object>>(Assert.Single((List<object>)op["puntos"]));
            Assert.Equal(5, System.Convert.ToDouble(point[0]), 8);
            Assert.Equal(23, System.Convert.ToDouble(point[1]), 8);
            Assert.Equal(90, System.Convert.ToDouble(op["rot"]), 8);
            Assert.Equal("UP", ((Dictionary<string, object>)op["params"])["Dirección"]);
            Assert.Equal("stub", Assert.Single((List<object>)op["items_fuente"]));
            Assert.NotNull(SourceBinding.Resolve(ops));
        }

        [Fact]
        public void EquipmentRulesKeepBothUnitPanelsAndPlaceOnlyTheDocumentedSuiteDisconnect()
        {
            var args = Json.ParseObject(Input);
            var equipment = Json.ParseObject("""
                {
                  "reglas":[{"ambito":"unidad","codigo":"P","clase":"tablero","tipo":"Tablero: LP"},
                            {"ambito":"suite","codigo":"P","clase":"desconectivo_hvac","tipo":"HVAC: MS"}],
                  "items":[{"ref":"1A-P1","pt":[2,4],"ambito":"unidad","zona":"1A","codigo":"P","elev":2,"evidencia":"planta de unidad"},
                           {"ref":"1A-P2","pt":[5,6],"ambito":"unidad","zona":"1A","codigo":"P","elev":2,"evidencia":"segundo tablero en planta"},
                           {"ref":"2B-D1","pt":[6,7],"ambito":"suite","zona":"2B","codigo":"P","elev":3,"evidencia":"E-1.5.16 HVAC desde L1"}]
                }
                """);
            args["reglas"] = equipment["reglas"];
            args["items"] = equipment["items"];
            var result = MarkupPlacement.Plan(args, new string('d', 64), "cuadros.pdf#p1", 1, equipment: true);
            var batch = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["planes"]));
            var ops = Assert.IsType<List<object>>(Json.Parse((string)batch["ops"]));
            Assert.Collection(ops,
                op => Assert.Equal("Tablero: LP", ((Dictionary<string, object>)op)["type"]),
                op => Assert.Equal("Tablero: LP", ((Dictionary<string, object>)op)["type"]),
                op => Assert.Equal("HVAC: MS", ((Dictionary<string, object>)op)["type"]));
            Assert.NotNull(SourceBinding.Resolve(ops));
        }

        [Fact]
        public void InvalidDirectionIsRejectedAndAmbiguousRegistrationDoesNotProducePlans()
        {
            var args = Json.ParseObject(Input.Replace("\"UP\"", "\"UP/DN\""));
            Assert.Throws<QueryException>(() => MarkupPlacement.Plan(args, new string('d', 64), "source", 1));
            ((List<object>)args["anclas"]).RemoveRange(1, 2);
            var result = MarkupPlacement.Plan(args, new string('d', 64), "source", 1);
            Assert.Equal("sin_registro", result["estado"]);
            Assert.False(result.ContainsKey("planes"));
        }
    }
}
