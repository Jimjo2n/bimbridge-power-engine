// SPDX-License-Identifier: GPL-3.0-only
using System.Linq;
using Bimbridge.Engine;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.8.0: sheet_set (persistent sheet set). Validation without Revit; real creation belongs to the native campaign.</summary>
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

        [Fact]
        public void SavedSetAcceptsUnorderedMembershipAndRejectsSubstitutionOrDuplicate()
        {
            var order = Enumerable.Range(1, 26).Select(n => (long)n).Reverse().ToArray();
            Assert.True(PrintSheetSet.MatchesMembership(order, order.Reverse()));
            Assert.False(PrintSheetSet.MatchesMembership(order, order.Skip(1).Append(27)));
            Assert.False(PrintSheetSet.MatchesMembership(order.Skip(1).Append(order[1]).ToArray(), order));
            var preset = Json.ParseObject("{\"combinar\":true,\"color\":\"gris\",\"papel\":\"Default\"}");
            var effective = PrintSheetSet.ApplyPreset(Json.ParseObject("{\"color\":\"color\"}"), preset);
            Assert.Equal("color", effective["color"]);
            Assert.Equal("Default", effective["papel"]);
            Assert.Equal(true, effective["combinar"]);
            Assert.False(PrintSheetSet.OutsidePaper(0, 0, 2.001, 1, 0, 0, 2, 1));
            Assert.True(PrintSheetSet.OutsidePaper(0, 0, 2.01, 1, 0, 0, 2, 1));
        }
    }
}
