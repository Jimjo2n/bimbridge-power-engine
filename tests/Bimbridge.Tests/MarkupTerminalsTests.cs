// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using BimBridge.PowerEngine.Workflows;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    public class MarkupTerminalsTests
    {
        private const string Input = """
            {
              "level":"L1","direction_parameter":"Dirección",
              "anchors":[{"name":"A","pdf":[0,0],"model":[10,20]},
                        {"name":"B","pdf":[10,0],"model":[10,30]},
                        {"name":"C","pdf":[0,10],"model":[0,20]}],
              "items":[{"ref":"box","pt":[2,4],"type":"Caja: Piso","direction":"DN","elev":0,"existing_id":800},
                       {"ref":"stub","pt":[3,5],"type":"Stub: EMT","direction":"UP","elev":1,"rotation_pt":0}]
            }
            """;

        [Fact]
        public void RotatedMarkupProducesOneIdempotentPlacementAndPreservesTheExistingBox()
        {
            var args = Json.ParseObject(Input);
            var result = MarkupPlacement.Plan(args, new string('d', 64), "Archi Units.pdf#p1", 1);
            Assert.Equal("ready", result["status"]);
            var rows = (List<object>)result["terminals"];
            Assert.Equal(800L, ((Dictionary<string, object>)rows[0])["id"]);
            var batch = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["plans"]));
            var ops = Assert.IsType<List<object>>(Json.Parse((string)batch["ops"]));
            var op = Assert.IsType<Dictionary<string, object>>(Assert.Single(ops));
            var point = Assert.IsType<List<object>>(Assert.Single((List<object>)op["points"]));
            Assert.Equal(5, System.Convert.ToDouble(point[0]), 8);
            Assert.Equal(23, System.Convert.ToDouble(point[1]), 8);
            Assert.Equal(90, System.Convert.ToDouble(op["rot"]), 8);
            Assert.Equal("UP", ((Dictionary<string, object>)op["params"])["Dirección"]);
            Assert.Equal("stub", Assert.Single((List<object>)op["source_items"]));
            Assert.NotNull(SourceBinding.Resolve(ops));
        }

        [Fact]
        public void EquipmentRulesKeepBothUnitPanelsAndPlaceOnlyTheDocumentedSuiteDisconnect()
        {
            var args = Json.ParseObject(Input);
            var equipment = Json.ParseObject("""
                {
                  "rules":[{"scope":"unit","code":"P","class":"panel","type":"Tablero: LP"},
                            {"scope":"suite","code":"P","class":"hvac_disconnect","type":"HVAC: MS"}],
                  "items":[{"ref":"1A-P1","pt":[2,4],"scope":"unit","zone":"1A","code":"P","elev":2,"evidence":"planta de unidad"},
                           {"ref":"1A-P2","pt":[5,6],"scope":"unit","zone":"1A","code":"P","elev":2,"evidence":"segundo tablero en planta"},
                           {"ref":"2B-D1","pt":[6,7],"scope":"suite","zone":"2B","code":"P","elev":3,"evidence":"E-1.5.16 HVAC desde L1"}]
                }
                """);
            args["rules"] = equipment["rules"];
            args["items"] = equipment["items"];
            var result = MarkupPlacement.Plan(args, new string('d', 64), "cuadros.pdf#p1", 1, equipment: true);
            var batch = Assert.IsType<Dictionary<string, object>>(Assert.Single((List<object>)result["plans"]));
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
            ((List<object>)args["anchors"]).RemoveRange(1, 2);
            var result = MarkupPlacement.Plan(args, new string('d', 64), "source", 1);
            Assert.Equal("unregistered", result["status"]);
            Assert.False(result.ContainsKey("plans"));
        }
    }
}
