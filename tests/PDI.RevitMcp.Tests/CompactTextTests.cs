// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Engine;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class CompactTextTests
    {
        [Fact]
        public void Table_FixedColumns_Dictionary_AndRanges()
        {
            var rows = new List<object>();
            for (var i = 0; i < 20; i++) rows.Add(new object[] { 100L + i, "LTG_DOWNLIGHT-SURFACE - ROUND", i % 2 == 0 ? "LEVEL05" : "LEVEL06", "DIST" });
            var t = CompactText.Write(new Dictionary<string, object>
            {
                ["total"] = 20L, ["cols"] = new List<object> { "id", "fam", "lvl", "ws" }, ["rows"] = rows,
                ["ids"] = new List<object> { 1L, 2L, 3L, 4L, 9L },
            });
            Assert.Contains("fijo: fam=LTG_DOWNLIGHT-SURFACE - ROUND; ws=DIST", t);
            Assert.Contains("ids: 1-4,9", t);
            Assert.Contains("id|lvl#", t);
            Assert.Contains("lvl#: 1=LEVEL05, 2=LEVEL06", t);
        }

        [Fact]
        public void Command_Parses_Aliases_Where_AndOps()
        {
            var c = Command.Parse("q LF lvl=LEVEL05 g=lvl,type elev>9 \"type~LT 06\" s=$x");
            Assert.Equal("q", c.Verb);
            Assert.Equal("LF", c.Positional[0]);
            Assert.Equal("lvl,type", c.Args["group_by"]);
            Assert.Equal("$x", c.Args["save_as"]);
            Assert.Equal(2, c.Where.Count);
            var ops = Command.ParseOps("snap $flotando\nset $x name=Comments value=\"A B\"");
            var a = (Dictionary<string, object>)ops[1];
            Assert.Equal("set_param", a["op"]);
            Assert.Equal("$x", a["ids"]);
            Assert.Equal("A B", a["value"]);
        }
    }
}
