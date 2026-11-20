// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    public class ScheduleAuditTests
    {
        private const string Expected = """
            {
              "ids_esperados":[11,22],
              "cantidad_esperada":2,
              "filtros_esperados":[{"campo":"Nivel","valor":"Nivel 01"},{"campo":"Área","valor":8}],
              "celdas_esperadas":[{"fila":0,"columna":1,"texto":"2 m²"}]
            }
            """;

        [Fact]
        public void AcceptsSamePopulationAndFiltersInDifferentOrder()
        {
            var args = Json.ParseObject(Expected);
            var filters = ((List<object>)args["filtros_esperados"]).AsEnumerable().Reverse().ToList();
            var result = new ScheduleAudit(args).Compare(new long[] { 22, 11 }, filters, cell =>
            {
                Assert.Equal(0, cell.Row);
                Assert.Equal(1, cell.Column);
                return "2 m²";
            });
            Assert.Equal("verificado", result["resultado"]);
            Assert.Equal(4L, result["comprobaciones"]);
        }

        [Fact]
        public void ReportsMissingExtraQuantityFilterAndInvalidCell()
        {
            var result = new ScheduleAudit(Json.ParseObject(Expected))
                .Compare(new long[] { 11, 33, 44 }, new List<object>(), _ => null);
            Assert.Equal("con_diferencias", result["resultado"]);
            var differences = ((List<object>)result["diferencias"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "poblacion", "cantidad", "filtros", "celda_fuera_de_rango" }, differences.Select(d => (string)d["tipo"]));
            Assert.Equal(new object[] { 22L }, (List<object>)differences[0]["muestra_faltan"]);
            Assert.Equal(new object[] { 33L, 44L }, (List<object>)differences[0]["muestra_sobran"]);
        }

        [Fact]
        public void UnsupportedPopulationCannotPassAnExpectedZeroCount()
        {
            var audit = new ScheduleAudit(Json.ParseObject("{\"cantidad_esperada\":0}"));
            Assert.Equal("no_evaluable", audit.Compare(null, new List<object>(), _ => null)["resultado"]);
            Assert.Equal("verificado", audit.Compare(new long[0], new List<object>(), _ => null)["resultado"]);
        }

        [Theory]
        [InlineData("{\"cantidad_esperada\":-1}")]
        [InlineData("{\"filtros_esperados\":[1]}")]
        [InlineData("{\"ids_esperados\":[1,1]}")]
        [InlineData("{\"celdas_esperadas\":[{\"fila\":0,\"columna\":0,\"texto\":\"\"},{\"fila\":0,\"columna\":0,\"texto\":\"b\"}]}")]
        public void RejectsInvalidExpectations(string json)
        {
            Assert.Throws<QueryException>(() => new ScheduleAudit(Json.ParseObject(json)));
        }

        [Fact]
        public void CompactCommandPreservesJsonWithQuotesBracketsAndConditionOperators()
        {
            var command = Command.Parse("""
                chk tabla_audit tabla="Tabla 1" celdas_esperadas=[{"fila":0,"columna":0,"texto":"[A] > 1 \"B\""}] cantidad_esperada=2
                """);
            Assert.Equal("Tabla 1", command.Args["tabla"]);
            Assert.Empty(command.Where);
            var audit = new ScheduleAudit(command.Args);
            Assert.Equal("[A] > 1 \"B\"", Assert.Single(audit.Cells).Text);
            Assert.True(audit.NeedsPopulation);
        }
    }
}
