// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.8.0: separate permissions (read, modify project/family, load file, save, sync, export, publish, dynamic code, ui).</summary>
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
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpExec, A(("mode", "query")), false));
            Assert.Equal(Permissions.DynamicCode, Permissions.Of(Wire.OpExec, A(("mode", "run")), false));
            Assert.Equal(Permissions.DynamicCode, Permissions.Of(Wire.OpExec, A(("mode", "trial")), false));
            Assert.Equal(Permissions.Read, Permissions.Of(Wire.OpFam, A(("action", "search")), false));
            Assert.Equal(Permissions.LoadFile, Permissions.Of(Wire.OpFam, A(("action", "load_file")), false));
            Assert.Equal(Permissions.ModifyFamily, Permissions.Of(Wire.OpFam, A(("action", "connector")), true));
            Assert.Equal(Permissions.Save, Permissions.Of(Wire.OpDoc, A(("action", "save")), false));
            Assert.Equal(Permissions.Sync, Permissions.Of(Wire.OpDoc, A(("action", "sync")), false));
            Assert.Equal(Permissions.Export, Permissions.Of(Wire.OpDoc, A(("action", "pdf")), false));
            Assert.Equal(Permissions.Ui, Permissions.Of(Wire.OpShow, A(), false));
            Assert.Equal(Permissions.Ui, Permissions.Of(Wire.OpCapture, A(), false));
        }

        [Fact]
        public void Explicit_List_Narrows_And_Publish_IsNeverGranted()
        {
            var eff = Permissions.Effective(new List<string> { "read", "modify_project", "publish" }, "RPW");
            Assert.Null(Permissions.Deny(Permissions.ModifyProject, eff));
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, eff));   // W in levels, but the explicit list does not grant it
            Assert.NotNull(Permissions.Deny(Permissions.Sync, eff));
            Assert.NotNull(Permissions.Deny(Permissions.Export, eff));
            Assert.Contains("is not supported", Permissions.Deny(Permissions.Publish, eff));
            Assert.Contains("is not supported", Permissions.Deny(Permissions.Publish, Permissions.Effective(null, "RPWD")));
        }

        [Fact]
        public void Derived_FromLevels_KeepsPreviousBehaviour()
        {
            var r = Permissions.Effective(null, "R");
            Assert.Null(Permissions.Deny(Permissions.Read, r));
            Assert.NotNull(Permissions.Deny(Permissions.ModifyProject, r));
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, r));
            var rp = Permissions.Effective(null, "RP");
            Assert.Null(Permissions.Deny(Permissions.ModifyProject, rp));      // reaches the held queue, as before
            Assert.NotNull(Permissions.Deny(Permissions.DynamicCode, rp));
            Assert.NotNull(Permissions.Deny(Permissions.Save, rp));
            var rd = Permissions.Effective(null, "RD");
            Assert.Null(Permissions.Deny(Permissions.Save, rd));
            Assert.NotNull(Permissions.Deny(Permissions.ModifyProject, rd));
            Assert.Null(Permissions.Deny(Permissions.LoadFile, Permissions.Effective(null, "RPW")));
        }
    }
}
