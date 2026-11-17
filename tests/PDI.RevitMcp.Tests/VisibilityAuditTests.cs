// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Qa;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class VisibilityAuditTests
    {
        [Fact]
        public void ChecksPowerAndMsdeIndependentlyInSystemAndCompositeViews()
        {
            var args = Json.ParseObject("""
                {"expectativas":[
                  {"vista":11,"id":51,"en_colector_de_vista":true,"plantilla_id":201,"filtro_id":101,"filtro_activo":true,"filtro_visible":true,"linea_proyeccion":[0,128,0]},
                  {"vista":11,"id":52,"en_colector_de_vista":false},
                  {"vista":12,"id":51,"en_colector_de_vista":true},
                  {"vista":12,"id":52,"en_colector_de_vista":true,"plantilla_id":202,"filtro_id":102,"filtro_activo":true,"linea_proyeccion":[255,0,255]}
                ]}
                """);
            var rows = (List<object>)Json.Parse("""
                [ {"id":12,"plantilla_id":202,"elementos":[
                    {"id":52,"en_colector_de_vista":true,"filtros_coincidentes":[{"id":102,"activo":true,"graficos":{"linea_proyeccion":[255,0,255]}}]},
                    {"id":51,"en_colector_de_vista":true}]},
                  {"id":11,"plantilla_id":201,"elementos":[
                    {"id":51,"en_colector_de_vista":true,"filtros_coincidentes":[{"id":101,"activo":true,"visible":true,"graficos":{"linea_proyeccion":[0,128,0]}}]},
                    {"id":52,"en_colector_de_vista":false}] } ]
                """);
            var check = new VisibilityAudit(args, new long[] { 11, 12 }, new long[] { 51, 52 });
            Assert.Empty(check.Compare(rows));
            var composite = (Dictionary<string, object>)rows[0];
            var msde = (Dictionary<string, object>)((List<object>)composite["elementos"])[0];
            msde["en_colector_de_vista"] = false;
            msde["filtros_coincidentes"] = new List<object>();
            Assert.Collection(check.Compare(rows),
                row => Assert.Equal("en_colector_de_vista", ((Dictionary<string, object>)row)["campo"]),
                row => Assert.Equal("filtro_id", ((Dictionary<string, object>)row)["campo"]));
        }

        [Theory]
        [InlineData("{\"vista\":11,\"id\":999,\"en_colector_de_vista\":true}")]
        [InlineData("{\"vista\":11,\"id\":51,\"filtro_activo\":true}")]
        [InlineData("{\"vista\":11,\"id\":51,\"linea_proyeccion\":[0,0,256]}")]
        public void RejectsUnverifiableExpectations(string row) => Assert.Throws<QueryException>(() =>
            new VisibilityAudit(Json.ParseObject("{\"expectativas\":[" + row + "]}"), new long[] { 11 }, new long[] { 51 }));
    }
}
