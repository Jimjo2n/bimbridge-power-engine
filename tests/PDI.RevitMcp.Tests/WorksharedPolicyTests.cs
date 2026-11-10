// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.AgentHost;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class WorksharedPolicyTests
    {
        private static Dictionary<string, object> Ctx(bool ws, string ruta, string central = null)
        {
            var d = new Dictionary<string, object> { ["worksharing"] = ws, ["ruta"] = ruta };
            if (central != null) d["central"] = central;
            return d;
        }

        [Fact]
        public void NotWorkshared_Passes_WithoutDeviation()
        {
            Assert.Null(RevitCli.WorksharedPolicy(Ctx(false, "C:\\x\\a.rvt"), "archivo:c:\\x\\a.rvt", false, out var dev));
            Assert.Null(dev);
        }

        [Fact]
        public void Workshared_WithoutFlag_IsBlocked()
        {
            Assert.Contains("--aceptar-workshared-local", RevitCli.WorksharedPolicy(Ctx(true, "C:\\x\\a.rvt"), "archivo:c:\\x\\a.rvt", false, out _));
        }

        [Fact]
        public void Workshared_Local_WithFlag_PassesAsRecordedDeviation()
        {
            Assert.Null(RevitCli.WorksharedPolicy(Ctx(true, "C:\\U\\Desktop\\NRT04_R21_detached.rvt", "C:\\U\\Desktop\\NRT04_R21_detached.rvt"), "archivo:c:\\u\\desktop\\nrt04_r21_detached.rvt", true, out var dev));
            Assert.Contains("aceptado por el operador", dev);
        }

        [Theory]
        [InlineData("BIM 360://Proyecto/a.rvt", null, "archivo:x")]
        [InlineData("Autodesk Docs://P/a.rvt", null, "archivo:x")]
        [InlineData("C:\\a.rvt", null, "nube:abc")]
        [InlineData("\\\\servidor\\c\\a.rvt", null, "archivo:x")]
        [InlineData("C:\\a.rvt", "\\\\servidor\\central.rvt", "archivo:x")]
        [InlineData("C:\\a.rvt", "Autodesk Docs://P/central.rvt", "archivo:x")]
        [InlineData("", null, "archivo:x")]
        public void Workshared_CloudOrNetwork_IsBlocked_EvenWithFlag(string ruta, string central, string key)
        {
            Assert.NotNull(RevitCli.WorksharedPolicy(Ctx(true, ruta, central), key, true, out var dev));
            Assert.Null(dev);
        }
    }
}
