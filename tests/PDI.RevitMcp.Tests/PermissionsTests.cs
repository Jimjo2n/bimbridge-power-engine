// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0: permisos separados (leer, modificar proyecto/familia, cargar archivo, guardar, sincronizar, exportar, publicar, código dinámico, interfaz).</summary>
    public class PermissionsTests
    {
        private static Dictionary<string, object> A(params (string k, object v)[] kv) { var d = new Dictionary<string, object>(); foreach (var (k, v) in kv) d[k] = v; return d; }

        [Fact]
        public void EachOperation_MapsToOneClass()
        {
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpQuery, A(), false));
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpPlan, A(), false));
            Assert.Equal(Permissions.ModifyProject, Permissions.Of(Wire.OpCommit, A(), false));
            Assert.Equal(Permissions.ModifyFamily, Permissions.Of(Wire.OpCommit, A(), true));
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpExec, A(("modo", "consulta")), false));
            Assert.Equal(Permissions.DynamicCode, Permissions.Of(Wire.OpExec, A(("modo", "ejecutar")), false));
            Assert.Equal(Permissions.DynamicCode, Permissions.Of(Wire.OpExec, A(("modo", "ensayo")), false));
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpFam, A(("accion", "buscar")), false));
            Assert.Equal(Permissions.LoadFile, Permissions.Of(Wire.OpFam, A(("accion", "cargar_archivo")), false));
            Assert.Equal(Permissions.ModifyFamily, Permissions.Of(Wire.OpFam, A(("accion", "conector")), true));
            Assert.Equal(Permissions.Save, Permissions.Of(Wire.OpDoc, A(("accion", "guardar")), false));
            Assert.Equal(Permissions.Sync, Permissions.Of(Wire.OpDoc, A(("accion", "sync")), false));
            Assert.Equal(Permissions.Export, Permissions.Of(Wire.OpDoc, A(("accion", "pdf")), false));
            Assert.Equal(Permissions.Ui, Permissions.Of(Wire.OpShow, A(), false));
            Assert.Equal(Permissions.Ui, Permissions.Of(Wire.OpCapture, A(), false));
        }

        [Fact]
        public void Explicit_List_Narrows_And_Publish_IsNeverGranted()
        {
            var eff = Permissions.Effective(new List<string> { "leer", "modificar_proyecto", "publicar" }, "RPW");
            Assert.Null(Permissions.Deny(Permissions.ModifyProject, eff));
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, eff));   // W en niveles, pero la lista explícita no lo da
            Assert.NotNull(Permissions.Deny(Permissions.Sync, eff));
            Assert.NotNull(Permissions.Deny(Permissions.Export, eff));
            Assert.Contains("no está soportado", Permissions.Deny(Permissions.Publish, eff));
            Assert.Contains("no está soportado", Permissions.Deny(Permissions.Publish, Permissions.Effective(null, "RPWD")));
        }

        [Fact]
        public void Derived_FromLevels_KeepsPreviousBehaviour()
        {
            var r = Permissions.Effective(null, "R");
            Assert.Null(Permissions.Deny(Permissions.Read, r));
            Assert.NotNull(Permissions.Deny(Permissions.ModifyProject, r));
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, r));
            var rp = Permissions.Effective(null, "RP");
            Assert.Null(Permissions.Deny(Permissions.ModifyProject, rp));      // llega a la cola retenida, como antes
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, rp));
            Assert.NotNull(Permissions.Deny(Permissions.Save, rp));
            var rd = Permissions.Effective(null, "RD");
            Assert.Null(Permissions.Deny(Permissions.Save, rd));
            Assert.NotNull(Permissions.Deny(Permissions.ModifyProject, rd));
            Assert.Null(Permissions.Deny(Permissions.LoadFile, Permissions.Effective(null, "RPW")));
        }
    }
}
