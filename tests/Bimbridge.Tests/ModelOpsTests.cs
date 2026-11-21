// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.9.1 (Blocks C/D/E): offline validation of the typed modeling operations. Anything rejected here never opens
    /// a transaction in Revit. Resolution against the model and verification of what was created are tested in the native campaign.
    /// </summary>
    public class ModelOpsTests
    {
        [Fact]
        public void ConduitReplacementAndNewTerminalAttachmentsAreDistinctContracts()
        {
            Dictionary<string, object> Route() => Json.ParseObject(OpCatalog.ByKind["conduit_route"].Example);
            var replace = Route();
            replace["reemplazar_ids"] = new List<object> { 1L, 2L, 3L };
            Assert.Equal("conduit_route", OpSpec.Parse(replace).Kind);
            var port = new Dictionary<string, object> { ["id"] = 900L, ["conector"] = 2L };
            replace["terminal_inicio"] = port;
            Assert.Throws<QueryException>(() => OpSpec.Parse(replace));
            replace.Remove("terminal_inicio");
            replace["reemplazar_ids"] = new List<object> { 1L, 1L };
            Assert.Throws<QueryException>(() => OpSpec.Parse(replace));
            var create = Route();
            create["terminal_inicio"] = port;
            Assert.Equal("conduit_route", OpSpec.Parse(create).Kind);
        }

        private static List<OpSpec> Plan(string opsJson) => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + opsJson + "]}"));

        private static void Rejected(string opJson, string fragment)
        {
            var ex = Assert.ThrowsAny<QueryException>(() => Plan(opJson));
            Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CatalogExamples_OfEveryNewOperation_ValidateOffline()
        {
            var kinds = new[] { "level_create", "grid_create", "wall_create", "floor_create", "ceiling_create", "room_create", "duct_create", "pipe_create", "conduit_create", "conduit_route", "circuit_create", "schedule_create" };
            foreach (var k in kinds)
            {
                var info = OpCatalog.ByKind[k];
                Assert.True(info.Creates, k + " must declare that it creates");
                var ops = Plan(info.Example);
                Assert.Equal(k, ops.Single().Kind);
            }
        }

        [Fact]
        public void ConduitRoute_RejectsCollapsedAndTwoDimensionalSegments()
        {
            var good = OpCatalog.ByKind["conduit_route"].Example;
            Rejected(good.Replace("[20,10,9]", "[20,0,9]"), "coincide");
            Rejected(good.Replace("[20,10,9]", "[20,10]"), "[x,y,z]");
        }

        [Fact]
        public void LevelAndGrid_RejectBadInput()
        {
            Rejected("{\"op\":\"level_create\"}", "elev is missing");
            Rejected("{\"op\":\"level_create\",\"elev\":\"abc\"}", "elev");
            Rejected("{\"op\":\"level_create\",\"elev\":20000}", "out of range");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"nombre\":\"L:2\"}", "does not accept the character ':'");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"nombre\":\"  \"}", "cannot be empty");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"ids\":\"123\"}", "do not use ids");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,0]]}", "Revit does not accept curves that short");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,10],[5,5]]}", "maximum 2");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,10]],\"nombre\":\"A{1}\"}", "does not accept");
        }

        [Fact]
        public void Walls_ValidateSegmentsHeightAndConstraints()
        {
            Plan("{\"op\":\"wall_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"cerrado\":true,\"nivel\":\"L1\",\"altura\":\"3000 mm\"}");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0],[5,0]],\"nivel\":\"L1\"}", "overlap");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":0}", "altura");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":-3}", "altura");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":10,\"nivel_superior\":\"L2\"}", "not both");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]]}", "nivel is missing");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"color\":\"rojo\"}", "unknown color");
        }

        [Fact]
        public void FloorsAndCeilings_RequireAValidClosedLoop()
        {
            Plan("{\"op\":\"floor_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"desfase\":\"-2 in\"}");
            Plan("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"altura\":\"2700 mm\"}");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\"}", "at least 3");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,0],[20,0]],\"nivel\":\"L1\"}", "overlap");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,10],[10,0],[0,20]],\"nivel\":\"L1\"}", "crosses");
            Rejected("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"altura\":0}", "altura");
            Rejected("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"desfase\":1}", "unknown desfase");
        }

        [Fact]
        public void Rooms_RequireA2DPointAndNonEmptyTexts()
        {
            Plan("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10],\"numero\":101}");
            Rejected("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10,3]}", "[x,y]");
            Rejected("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10],\"nombre\":\" \"}", "cannot be empty");
        }

        [Fact]
        public void DuctsAndPipes_ValidateEndpointsSystemAndSize()
        {
            Plan("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"Supply Air\",\"diametro\":\"10 in\"}");
            Plan("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[0,0,1],\"nivel\":\"L1\",\"sistema\":\"Sanitary\",\"diametro\":\"4 in\"}");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\"}", "[x,y,z]");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[1,1,9],\"fin\":[1,1,9],\"nivel\":\"L1\",\"sistema\":\"S\"}", "coincide");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":\"12 in\"}", "together");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":1,\"alto\":1,\"diametro\":1}", "not both");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"diametro\":0}", "diametro");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":1}", "unknown ancho");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\"}", "sistema is missing");
        }

        [Fact]
        public void Circuits_NeedExistingDistinctDevices()
        {
            Plan("{\"op\":\"circuit_create\",\"ids\":\"101,102,103\",\"panel\":\"LP-1\"}");
            Plan("{\"op\":\"circuit_create\",\"ids\":\"$receptaculos\"}");
            Rejected("{\"op\":\"circuit_create\",\"panel\":\"LP-1\"}", "ids is missing");
            Rejected("{\"op\":\"circuit_create\",\"ids\":\"101,101\"}", "repeated");
            Rejected("{\"op\":\"place\",\"type\":\"F: T\",\"level\":\"L1\",\"puntos\":[[0,0]]},{\"op\":\"circuit_create\",\"ids\":\"@1\"}", "not @n");
        }

        [Fact]
        public void Schedules_ValidateCategoryAndFields()
        {
            Plan("{\"op\":\"schedule_create\",\"categoria\":\"OST_Rooms\",\"campos\":\"Number,Name,Area\"}");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Lighting Fixtures\",\"campos\":[\"Type\"]}", "BuiltInCategory");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[]}", "at least one field");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[\"Name\",\"name\"]}", "repeated");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[\"Name\"],\"nombre\":\"A|B\"}", "does not accept");
            Assert.Equal("OST_LightingFixtures", ModelOps.NormalizeCategory("LightingFixtures"));
            Assert.Equal("OST_Rooms", ModelOps.NormalizeCategory(" OST_Rooms "));
            Assert.Null(ModelOps.NormalizeCategory("OST_"));
        }

        [Fact]
        public void Differs_TreatsNonFiniteValuesAsAMismatch()
        {
            // AUD-06: a missing parameter read as NaN cannot pass verification.
            Assert.True(ModelOps.Differs(double.NaN, 10, 1e-5));
            Assert.True(ModelOps.Differs(10, double.NaN, 1e-5));
            Assert.True(ModelOps.Differs(double.PositiveInfinity, 10, 1e-5));
            Assert.True(ModelOps.Differs(10.001, 10, 1e-5));
            Assert.False(ModelOps.Differs(10.000001, 10, 1e-5));
            Assert.DoesNotContain("voltage and poles.", OpCatalog.ByKind["circuit_create"].Help);
        }

        [Fact]
        public void WallSegments_OpenAndClosed()
        {
            var pts = new List<double[]> { new[] { 0.0, 0 }, new[] { 10.0, 0 }, new[] { 10.0, 5 } };
            Assert.Equal(2, ModelOps.WallSegments(pts, false).Count);
            var closed = ModelOps.WallSegments(pts, true);
            Assert.Equal(3, closed.Count);
            Assert.Same(pts[0], closed[2].B);
        }
    }
}
