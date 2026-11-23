// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    public class VisibilityAuditTests
    {
        [Fact]
        public void ChecksPowerAndMsdeIndependentlyInSystemAndCompositeViews()
        {
            var args = Json.ParseObject("""
                {"expectations":[
                  {"view":11,"id":51,"in_view_collector":true,"template_id":201,"filter_id":101,"active_filter":true,"visible_filter":true,"projection_line":[0,128,0]},
                  {"view":11,"id":52,"in_view_collector":false},
                  {"view":12,"id":51,"in_view_collector":true},
                  {"view":12,"id":52,"in_view_collector":true,"template_id":202,"filter_id":102,"active_filter":true,"projection_line":[255,0,255]}
                ]}
                """);
            var rows = (List<object>)Json.Parse("""
                [ {"id":12,"template_id":202,"elements":[
                    {"id":52,"in_view_collector":true,"matching_filters":[{"id":102,"active":true,"graphics":{"projection_line":[255,0,255]}}]},
                    {"id":51,"in_view_collector":true}]},
                  {"id":11,"template_id":201,"elements":[
                    {"id":51,"in_view_collector":true,"matching_filters":[{"id":101,"active":true,"visible":true,"graphics":{"projection_line":[0,128,0]}}]},
                    {"id":52,"in_view_collector":false}] } ]
                """);
            var check = new VisibilityAudit(args, new long[] { 11, 12 }, new long[] { 51, 52 });
            Assert.Empty(check.Compare(rows));
            var composite = (Dictionary<string, object>)rows[0];
            var msde = (Dictionary<string, object>)((List<object>)composite["elements"])[0];
            msde["in_view_collector"] = false;
            msde["matching_filters"] = new List<object>();
            Assert.Collection(check.Compare(rows),
                row => Assert.Equal("in_view_collector", ((Dictionary<string, object>)row)["field"]),
                row => Assert.Equal("filter_id", ((Dictionary<string, object>)row)["field"]));
        }

        [Theory]
        [InlineData("{\"view\":11,\"id\":999,\"in_view_collector\":true}")]
        [InlineData("{\"view\":11,\"id\":51,\"active_filter\":true}")]
        [InlineData("{\"view\":11,\"id\":51,\"projection_line\":[0,0,256]}")]
        public void RejectsUnverifiableExpectations(string row) => Assert.Throws<QueryException>(() =>
            new VisibilityAudit(Json.ParseObject("{\"expectations\":[" + row + "]}"), new long[] { 11 }, new long[] { 51 }));
    }
}
