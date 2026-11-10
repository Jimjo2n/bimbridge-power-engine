// SPDX-License-Identifier: GPL-3.0-only
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0: sheet_set (conjunto de impresión persistente). Validación sin Revit; la creación real es de la campaña nativa.</summary>
    public class SheetSetOpTests
    {
        private static void Bad(string op) => Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[" + op + "]}")));

        [Fact]
        public void SheetSet_ValidatesNameAndSheets()
        {
            var ok = OpSpec.ParseAll(Json.ParseObject("{\"ops\":[{\"op\":\"sheet_set\",\"nombre\":\"NON-UNIT LIGHTING\",\"hojas\":[\"10E.01.NUL\",17732201]}]}"));
            Assert.Single(ok);
            Bad("{\"op\":\"sheet_set\",\"nombre\":\"X\",\"hojas\":[]}");
            Bad("{\"op\":\"sheet_set\",\"nombre\":\" \",\"hojas\":[\"A1\"]}");
            Bad("{\"op\":\"sheet_set\",\"nombre\":\"X\",\"hojas\":[\"@1\"]}");
            Bad("{\"op\":\"sheet_set\",\"nombre\":\"X\",\"hojas\":\"A1\"}");
            Assert.True(OpCatalog.ByKind["sheet_set"].Creates);
        }
    }
}
