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
              "expected_ids":[11,22],
              "expected_count":2,
              "expected_filters":[{"field":"Nivel","value":"Nivel 01"},{"field":"Área","value":8}],
              "expected_cells":[{"row":0,"column":1,"text":"2 m²"}]
            }
            """;

        [Fact]
        public void AcceptsSamePopulationAndFiltersInDifferentOrder()
        {
            var args = Json.ParseObject(Expected);
            var filters = ((List<object>)args["expected_filters"]).AsEnumerable().Reverse().ToList();
            var result = new ScheduleAudit(args).Compare(new long[] { 22, 11 }, filters, cell =>
            {
                Assert.Equal(0, cell.Row);
                Assert.Equal(1, cell.Column);
                return "2 m²";
            });
            Assert.Equal("verified", result["result"]);
            Assert.Equal(4L, result["checks"]);
        }

        [Fact]
        public void ReportsMissingExtraQuantityFilterAndInvalidCell()
        {
            var result = new ScheduleAudit(Json.ParseObject(Expected))
                .Compare(new long[] { 11, 33, 44 }, new List<object>(), _ => null);
            Assert.Equal("with_differences", result["result"]);
            var differences = ((List<object>)result["differences"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "population", "quantity", "filters", "cell_out_of_range" }, differences.Select(d => (string)d["type"]));
            Assert.Equal(new object[] { 22L }, (List<object>)differences[0]["missing_sample"]);
            Assert.Equal(new object[] { 33L, 44L }, (List<object>)differences[0]["extra_sample"]);
        }

        [Fact]
        public void UnsupportedPopulationCannotPassAnExpectedZeroCount()
        {
            var audit = new ScheduleAudit(Json.ParseObject("{\"expected_count\":0}"));
            Assert.Equal("not_evaluable", audit.Compare(null, new List<object>(), _ => null)["result"]);
            Assert.Equal("verified", audit.Compare(new long[0], new List<object>(), _ => null)["result"]);
        }

        [Theory]
        [InlineData("{\"expected_count\":-1}")]
        [InlineData("{\"expected_filters\":[1]}")]
        [InlineData("{\"expected_ids\":[1,1]}")]
        [InlineData("{\"expected_cells\":[{\"row\":0,\"column\":0,\"text\":\"\"},{\"row\":0,\"column\":0,\"text\":\"b\"}]}")]
        public void RejectsInvalidExpectations(string json)
        {
            Assert.Throws<QueryException>(() => new ScheduleAudit(Json.ParseObject(json)));
        }

        [Fact]
        public void CompactCommandPreservesJsonWithQuotesBracketsAndConditionOperators()
        {
            var command = Command.Parse("""
                chk schedule_audit schedule="Tabla 1" expected_cells=[{"row":0,"column":0,"text":"[A] > 1 \"B\""}] expected_count=2
                """);
            Assert.Equal("Tabla 1", command.Args["schedule"]);
            Assert.Empty(command.Where);
            var audit = new ScheduleAudit(command.Args);
            Assert.Equal("[A] > 1 \"B\"", Assert.Single(audit.Cells).Text);
            Assert.True(audit.NeedsPopulation);
        }
    }
}
