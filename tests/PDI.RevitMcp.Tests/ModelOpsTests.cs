// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1 (Bloques C/D/E): validación offline de las operaciones tipadas de modelado. Todo lo que se rechaza aquí no llega a abrir
    /// una transacción en Revit. La resolución contra el modelo y la verificación de lo creado se prueban en la campaña nativa.
    /// </summary>
    public class ModelOpsTests
    {
        private static List<OpSpec> Plan(string opsJson) => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + opsJson + "]}"));

        private static void Rejected(string opJson, string fragment)
        {
            var ex = Assert.ThrowsAny<QueryException>(() => Plan(opJson));
            Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CatalogExamples_OfEveryNewOperation_ValidateOffline()
        {
            var kinds = new[] { "level_create", "grid_create", "wall_create", "floor_create", "ceiling_create", "room_create", "duct_create", "pipe_create", "circuit_create", "schedule_create" };
            foreach (var k in kinds)
            {
                var info = OpCatalog.ByKind[k];
                Assert.True(info.Creates, k + " debe declarar que crea");
                var ops = Plan(info.Example);
                Assert.Equal(k, ops.Single().Kind);
            }
        }

        [Fact]
        public void LevelAndGrid_RejectBadInput()
        {
            Rejected("{\"op\":\"level_create\"}", "falta elev");
            Rejected("{\"op\":\"level_create\",\"elev\":\"abc\"}", "elev");
            Rejected("{\"op\":\"level_create\",\"elev\":20000}", "fuera de rango");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"nombre\":\"L:2\"}", "no admite el carácter ':'");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"nombre\":\"  \"}", "vacío");
            Rejected("{\"op\":\"level_create\",\"elev\":12,\"ids\":\"123\"}", "no use ids");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,0]]}", "Revit no admite curvas tan cortas");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,10],[5,5]]}", "máximo 2");
            Rejected("{\"op\":\"grid_create\",\"puntos\":[[0,0],[0,10]],\"nombre\":\"A{1}\"}", "no admite");
        }

        [Fact]
        public void Walls_ValidateSegmentsHeightAndConstraints()
        {
            Plan("{\"op\":\"wall_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"cerrado\":true,\"nivel\":\"L1\",\"altura\":\"3000 mm\"}");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0],[5,0]],\"nivel\":\"L1\"}", "se superponen");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":0}", "altura");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":-3}", "altura");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"altura\":10,\"nivel_superior\":\"L2\"}", "no los dos");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]]}", "falta nivel");
            Rejected("{\"op\":\"wall_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\",\"color\":\"rojo\"}", "no conozco color");
        }

        [Fact]
        public void FloorsAndCeilings_RequireAValidClosedLoop()
        {
            Plan("{\"op\":\"floor_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"desfase\":\"-2 in\"}");
            Plan("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"altura\":\"2700 mm\"}");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,0]],\"nivel\":\"L1\"}", "al menos 3");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,0],[20,0]],\"nivel\":\"L1\"}", "superponen");
            Rejected("{\"op\":\"floor_create\",\"puntos\":[[0,0],[10,10],[10,0],[0,20]],\"nivel\":\"L1\"}", "se cruza");
            Rejected("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"altura\":0}", "altura");
            Rejected("{\"op\":\"ceiling_create\",\"puntos\":[[0,0],[30,0],[30,20],[0,20]],\"nivel\":\"L1\",\"desfase\":1}", "no conozco desfase");
        }

        [Fact]
        public void Rooms_RequireA2DPointAndNonEmptyTexts()
        {
            Plan("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10],\"numero\":101}");
            Rejected("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10,3]}", "[x,y]");
            Rejected("{\"op\":\"room_create\",\"nivel\":\"L1\",\"punto\":[15,10],\"nombre\":\" \"}", "vacío");
        }

        [Fact]
        public void DuctsAndPipes_ValidateEndpointsSystemAndSize()
        {
            Plan("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"Supply Air\",\"diametro\":\"10 in\"}");
            Plan("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[0,0,1],\"nivel\":\"L1\",\"sistema\":\"Sanitary\",\"diametro\":\"4 in\"}");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\"}", "[x,y,z]");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[1,1,9],\"fin\":[1,1,9],\"nivel\":\"L1\",\"sistema\":\"S\"}", "coinciden");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":\"12 in\"}", "juntos");
            Rejected("{\"op\":\"duct_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":1,\"alto\":1,\"diametro\":1}", "no los dos");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"diametro\":0}", "diametro");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\",\"sistema\":\"S\",\"ancho\":1}", "no conozco ancho");
            Rejected("{\"op\":\"pipe_create\",\"inicio\":[0,0,9],\"fin\":[20,0,9],\"nivel\":\"L1\"}", "falta sistema");
        }

        [Fact]
        public void Circuits_NeedExistingDistinctDevices()
        {
            Plan("{\"op\":\"circuit_create\",\"ids\":\"101,102,103\",\"panel\":\"LP-1\"}");
            Plan("{\"op\":\"circuit_create\",\"ids\":\"$receptaculos\"}");
            Rejected("{\"op\":\"circuit_create\",\"panel\":\"LP-1\"}", "falta ids");
            Rejected("{\"op\":\"circuit_create\",\"ids\":\"101,101\"}", "repetidos");
            Rejected("{\"op\":\"place\",\"type\":\"F: T\",\"level\":\"L1\",\"puntos\":[[0,0]]},{\"op\":\"circuit_create\",\"ids\":\"@1\"}", "no @n");
        }

        [Fact]
        public void Schedules_ValidateCategoryAndFields()
        {
            Plan("{\"op\":\"schedule_create\",\"categoria\":\"OST_Rooms\",\"campos\":\"Number,Name,Area\"}");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Lighting Fixtures\",\"campos\":[\"Type\"]}", "BuiltInCategory");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[]}", "al menos un campo");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[\"Name\",\"name\"]}", "repetido");
            Rejected("{\"op\":\"schedule_create\",\"categoria\":\"Rooms\",\"campos\":[\"Name\"],\"nombre\":\"A|B\"}", "no admite");
            Assert.Equal("OST_LightingFixtures", ModelOps.NormalizeCategory("LightingFixtures"));
            Assert.Equal("OST_Rooms", ModelOps.NormalizeCategory(" OST_Rooms "));
            Assert.Null(ModelOps.NormalizeCategory("OST_"));
        }

        [Fact]
        public void Differs_TreatsNonFiniteValuesAsAMismatch()
        {
            // AUD-06: un parámetro ausente leído como NaN no puede pasar la verificación.
            Assert.True(ModelOps.Differs(double.NaN, 10, 1e-5));
            Assert.True(ModelOps.Differs(10, double.NaN, 1e-5));
            Assert.True(ModelOps.Differs(double.PositiveInfinity, 10, 1e-5));
            Assert.True(ModelOps.Differs(10.001, 10, 1e-5));
            Assert.False(ModelOps.Differs(10.000001, 10, 1e-5));
            Assert.DoesNotContain("tensión y polos.", OpCatalog.ByKind["circuit_create"].Help);
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
