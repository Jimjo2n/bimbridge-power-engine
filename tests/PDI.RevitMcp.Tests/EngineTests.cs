// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    public class JsonTests
    {
        [Fact]
        public void RoundTrip_PreservesTypesAndUnicode()
        {
            var src = new Dictionary<string, object>
            {
                ["s"] = "Ñandú \"comillas\" \n línea",
                ["l"] = 17855175L,
                ["big"] = 9007199254740993L,
                ["d"] = 10.375,
                ["b"] = true,
                ["n"] = null,
                ["arr"] = new List<object> { 1L, "x", false },
            };
            var back = Json.ParseObject(Json.Write(src));
            Assert.Equal("Ñandú \"comillas\" \n línea", back["s"]);
            Assert.Equal(17855175L, back["l"]);
            Assert.Equal(9007199254740993L, back["big"]);
            Assert.Equal(10.375, back["d"]);
            Assert.Equal(true, back["b"]);
            Assert.Null(back["n"]);
            Assert.Equal(3, ((List<object>)back["arr"]).Count);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1,2")]
        [InlineData("{} x")]
        public void Parse_RejectsMalformed(string text)
        {
            Assert.Throws<FormatException>(() => Json.Parse(text));
        }

        [Fact]
        public async Task Frame_RoundTripAndCleanEof()
        {
            var ms = new MemoryStream();
            await Frame.WriteAsync(ms, "{\"a\":1}", CancellationToken.None);
            await Frame.WriteAsync(ms, "[]", CancellationToken.None);
            ms.Position = 0;
            Assert.Equal("{\"a\":1}", await Frame.ReadAsync(ms, CancellationToken.None));
            Assert.Equal("[]", await Frame.ReadAsync(ms, CancellationToken.None));
            Assert.Null(await Frame.ReadAsync(ms, CancellationToken.None));
        }
    }

    public class SchedulerTests
    {
        [Fact]
        public void RoundRobin_AlternatesBetweenSessions()
        {
            var s = new WriteScheduler<string>();
            for (var i = 1; i <= 4; i++) s.Enqueue("claude", "C" + i);
            s.Enqueue("codex", "X1");
            s.Enqueue("codex", "X2");

            var order = new List<string>();
            while (s.TryDequeue(out _, out var item)) order.Add(item);
            Assert.Equal(new[] { "C1", "X1", "C2", "X2", "C3", "C4" }, order);
        }

        [Fact]
        public void SmallJob_DoesNotWaitBehindBigJob()
        {
            var s = new WriteScheduler<int>();
            for (var i = 0; i < 50; i++) s.Enqueue("claude", i);   // plan grande: 50 sub-lotes
            s.Enqueue("codex", 999);                                   // 3 elementos de Codex
            Assert.Equal(1, s.PositionOf("codex"));
            s.TryDequeue(out var first, out _);
            s.TryDequeue(out var second, out var item);
            Assert.Equal("claude", first);
            Assert.Equal("codex", second);
            Assert.Equal(999, item);
        }

        [Fact]
        public void Barrier_StopsDeliveryButKeepsQueue()
        {
            var s = new WriteScheduler<int>();
            s.Enqueue("a", 1);
            s.CloseBarrier("sincronizando");
            Assert.False(s.TryDequeue(out _, out _));
            Assert.Equal("sincronizando", s.BarrierReason);
            Assert.Equal(1, s.Count);
            s.OpenBarrier();
            Assert.True(s.TryDequeue(out _, out _));
        }

        [Fact]
        public void RemoveSession_DropsItsWorkOnly()
        {
            var s = new WriteScheduler<string>();
            s.Enqueue("a", "a1"); s.Enqueue("b", "b1"); s.Enqueue("a", "a2"); s.Enqueue("c", "c1");
            Assert.Equal(2, s.RemoveSession("a").Count);
            var order = new List<string>();
            while (s.TryDequeue(out _, out var item)) order.Add(item);
            Assert.Equal(new[] { "b1", "c1" }, order);
        }
    }

    public class VersionTests
    {
        [Fact]
        public void Conflict_DetectsOtherAgentAndHumanChanges()
        {
            var v = new VersionRegistry();
            v.Record(new long[] { 1, 2, 3 }, null, null, "S-claude");
            var snap = v.Snapshot(new long[] { 1, 2, 3, 4 });

            v.Record(null, new long[] { 2 }, null, "S-codex");
            v.Record(null, null, new long[] { 3 }, VersionRegistry.HumanAuthor);

            var c = v.FindConflicts(snap).OrderBy(x => x.Id).ToList();
            Assert.Equal(2, c.Count);
            Assert.Equal("2 fue cambiado por otro agente", c[0].Describe("S-claude"));
            Assert.Equal("3 fue borrado por el usuario en Revit", c[1].Describe("S-claude"));
        }

        [Fact]
        public void ChangedSince_ReturnsOnlyNewer()
        {
            var v = new VersionRegistry();
            v.Record(new long[] { 10 }, null, null, null);
            var rev = v.Rev;
            v.Record(null, new long[] { 11 }, new long[] { 12 }, null);
            var changed = v.ChangedSince(rev, out var deleted);
            Assert.Equal(new long[] { 11 }, changed.ToArray());
            Assert.Equal(new long[] { 12 }, deleted.ToArray());
        }
    }

    public class QueryTests
    {
        [Fact]
        public void QueryDigestCoversAllMatchedRowsAndIgnoresOrderAndPagination()
        {
            var first = new Dictionary<string, object> { ["id"] = 1L, ["comments"] = "a" };
            var second = new Dictionary<string, object> { ["id"] = 2L, ["comments"] = "b" };
            var q = QuerySpec.Parse(A(("ids", "1,2"), ("digest", true), ("f", "id,comments"), ("page_size", 1L)));
            Dictionary<string, object> Build(params Dictionary<string, object>[] rows) =>
                (Dictionary<string, object>)ResultTable.Build(q, rows, out _);
            var result = Build(first, second);
            q.Page = 2;
            Assert.Equal(result["sha256"], Build(second, first)["sha256"]);
            Assert.Equal(2L, result["total"]);
            Assert.False(result.ContainsKey("rows"));
            second["comments"] = "c";
            Assert.NotEqual(result["sha256"], Build(first, second)["sha256"]);
        }

        private static Dictionary<string, object> A(params (string k, object v)[] kv)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void Parse_RequiresScope()
        {
            Assert.Throws<QueryException>(() => QuerySpec.Parse(A(("f", "id"))));
        }

        [Fact]
        public void Parse_SetAndConditions()
        {
            var q = QuerySpec.Parse(A(("ids", "$l5_perimetro"), ("where", new List<object> { "elev>=10.5", "comments?", "type~STRIP" })));
            Assert.Equal("$l5_perimetro", q.FromSet);
            Assert.Equal(">=", q.Where[0].Op);
            Assert.Equal("notempty", q.Where[1].Op);
            Assert.Equal("~", q.Where[2].Op);
        }

        [Theory]
        [InlineData("elev>10", 10.5, true)]
        [InlineData("elev>10", 9.0, false)]
        [InlineData("Mark=lt-01", "LT-01", true)]
        [InlineData("Mark!=LT-01", "LT-02", true)]
        [InlineData("type~strip", "LTG_STRIP LIGHT", true)]
        [InlineData("comments?", "", false)]
        [InlineData("!comments", null, true)]
        public void Condition_Matches(string cond, object value, bool expected)
        {
            Assert.Equal(expected, Condition.Parse(cond).Matches(value));
        }

        [Theory]
        [InlineData("elev>>3")]
        [InlineData("mark==A")]
        [InlineData("elev>abc")]
        [InlineData("elev<=")]
        public void Condition_Malformed_IsRejected(string cond)
        {
            // Antes "elev>>3" devolvía total 0 sin error y la IA concluía que no había nada.
            Assert.Throws<QueryException>(() => Condition.Parse(cond));
        }

        [Fact]
        public void Parse_PageSizeCapped()
        {
            Assert.Throws<QueryException>(() => QuerySpec.Parse(A(("cat", "LightingFixtures"), ("page_size", 1000L))));
            Assert.Throws<QueryException>(() => QuerySpec.Parse(A(("cat", "LightingFixtures"), ("units", "in"))));
        }

        [Fact]
        public void Table_TrimsLongTextUnlessFull()
        {
            var rows = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["id"] = 1L, ["comments"] = new string('x', 600) } };
            var r = (Dictionary<string, object>)ResultTable.Build(QuerySpec.Parse(A(("ids", "1"), ("f", "id,comments"))), rows, out _);
            var v = (string)((object[])((List<object>)r["rows"])[0])[1];
            Assert.Equal(QuerySpec.MaxTextLength, v.Length);
            Assert.EndsWith("…", v);
            Assert.True(r.ContainsKey("aviso_texto"));

            r = (Dictionary<string, object>)ResultTable.Build(QuerySpec.Parse(A(("ids", "1"), ("f", "id,comments"), ("full", true))), rows, out _);
            Assert.Equal(600, ((string)((object[])((List<object>)r["rows"])[0])[1]).Length);
        }

        [Fact]
        public void Table_MillimetersAppliesToOutputAndWhere()
        {
            var q = QuerySpec.Parse(A(("cat", "LightingFixtures"), ("f", "id,elev"), ("units", "mm"), ("where", new List<object> { "elev>3300" })));
            var r = (Dictionary<string, object>)ResultTable.Build(q, Rows(), out var ids);
            // 10 ft = 3048 mm no pasa; 11 ft = 3352,8 mm sí. Filas con elev ≥ 11 ft: 2 + 1 + 3 = 6.
            Assert.Equal(6L, r["total"]);
            Assert.Equal("mm", r["unidades"]);
            Assert.Equal(3352.8, (double)((object[])((List<object>)r["rows"])[0])[1], 1);
        }

        private static List<Dictionary<string, object>> Rows()
        {
            var rows = new List<Dictionary<string, object>>();
            long id = 1;
            foreach (var (lvl, type, n) in new[] { ("LEVEL05", "G", 3), ("LEVEL05", "L5", 2), ("LEVEL06", "G", 4) })
                for (var i = 0; i < n; i++)
                    rows.Add(new Dictionary<string, object> { ["id"] = id++, ["lvl"] = lvl, ["type"] = type, ["elev"] = 10.0 + i });
            return rows;
        }

        [Fact]
        public void Table_GroupByCounts()
        {
            var q = QuerySpec.Parse(A(("cat", "LightingFixtures"), ("group_by", "lvl,type")));
            var r = (Dictionary<string, object>)ResultTable.Build(q, Rows(), out var ids);
            Assert.Equal(9L, r["total"]);
            var groups = (List<object>)r["groups"];
            Assert.Equal(3, groups.Count);
            Assert.Equal(new object[] { "LEVEL05", "G", 3L }, (object[])groups[0]);
            Assert.Equal(9, ids.Count);
        }

        [Fact]
        public void Table_FiltersAndPaginates()
        {
            var q = QuerySpec.Parse(A(("cat", "LightingFixtures"), ("where", "elev>=11"), ("f", "id,elev"), ("page_size", 2L), ("page", 2L)));
            var r = (Dictionary<string, object>)ResultTable.Build(q, Rows(), out var ids);
            Assert.Equal(6L, r["total"]);          // elev 11,12 | 11 | 11,12,13
            Assert.Equal(3L, r["pages"]);
            Assert.Equal(2, ((List<object>)r["rows"]).Count);
            Assert.Equal(6, ids.Count);
        }

        [Fact]
        public void NamedSets_ValidateNames()
        {
            var s = new NamedSets();
            s.Save("l5_perimetro", new long[] { 1, 2 });
            Assert.True(s.TryGet("$L5_PERIMETRO", out var ids));
            Assert.Equal(2, ids.Length);
            Assert.Throws<ArgumentException>(() => s.Save("$mal nombre", new long[0]));
        }

        [Fact]
        public void Sessions_GetDistinctIds()
        {
            var r = new SessionRegistry();
            var a = r.Open("Claude Code", 4812);
            var b = r.Open("codex-mcp-client", 900);
            var c = r.Open("Claude Code", 4812);
            // Prefijo legible (cliente y pid) + sufijo aleatorio por instancia (revisión PR #22): distintos aunque coincidan cliente y pid.
            Assert.Matches("^S-claude-code-4812-[0-9a-f]{16}$", a.Id);
            Assert.Matches("^S-codex-mcp-client-900-[0-9a-f]{16}$", b.Id);
            Assert.StartsWith("S-claude-code-4812-", c.Id);
            Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
        }

        [Fact]
        public void Sessions_IdsAreNeverReused_AfterCloseAndReconnectWithSamePid()
        {
            var r = new SessionRegistry();
            var seen = new HashSet<string>();
            for (var i = 0; i < 200; i++)
            {
                var s = r.Open("Claude Code", 4812);          // mismo cliente, mismo pid, contador de peticiones reiniciado
                Assert.True(seen.Add(s.Id), "session_id repetido: " + s.Id);
                var req = new PDI.RevitMcp.Protocol.Request { Id = 1, Op = PDI.RevitMcp.Protocol.Wire.OpPing, Args = new Dictionary<string, object>() };
                Assert.True(seen.Add(Correlation.For(s, req).RequestId), "request_id repetido");
                r.Close(s);
            }
        }
    }
}

namespace PDI.RevitMcp.Tests
{
    public class LightingAuditTests
    {
        [Fact]
        public void Audit_FindsUntaggedOrphanAndDuplicateTags()
        {
            var fx = new List<LightingAudit.Fixture>
            {
                new LightingAudit.Fixture { Id = 1, Level = "LEVEL05", Workset = "LIGHTING - DIST", Type = "LTG: G" },
                new LightingAudit.Fixture { Id = 2, Level = "LEVEL05", Workset = "LIGHTING - DIST", Type = "LTG: G" },
                new LightingAudit.Fixture { Id = 3, Level = "LEVEL06", Workset = "LIGHTING-EMERGENCY - DIST", Type = "LTG: S" },
            };
            var tags = new List<LightingAudit.Tag>
            {
                new LightingAudit.Tag { Id = 100, ViewId = 9, Tagged = new long[] { 1 } },
                new LightingAudit.Tag { Id = 101, ViewId = 9, Tagged = new long[] { 1 } },   // duplicado de 1 en la vista 9
                new LightingAudit.Tag { Id = 102, ViewId = 8, Tagged = new long[] { 1 } },   // otra vista: válido (doble altura)
                new LightingAudit.Tag { Id = 103, ViewId = 9, Tagged = new long[] { 3 } },
                new LightingAudit.Tag { Id = 104, ViewId = 9, Tagged = new long[0] },        // huérfano
            };
            var r = LightingAudit.Summarize(fx, tags, null, out var sets);

            Assert.Equal(3L, r["luminarias"]);
            Assert.Equal(1L, ((Dictionary<string, object>)r["sin_tag"])["n"]);
            Assert.Equal(1L, ((Dictionary<string, object>)r["tags_huerfanos"])["n"]);
            Assert.Equal(1L, ((Dictionary<string, object>)r["tags_duplicados"])["sobrantes"]);
            Assert.Equal(false, r["cumple_regla"]);
            Assert.Equal(new long[] { 2 }, sets["$sin_tag"]);
            Assert.Equal(new long[] { 104 }, sets["$tags_huerfanos"]);
            Assert.Equal(new long[] { 101 }, sets["$tags_sobrantes"]);
            Assert.Equal(2L, ((Dictionary<string, object>)r["por_nivel"])["LEVEL05"]);
        }

        [Fact]
        public void Audit_ViewsByLevel_NoRegeneration()
        {
            var fx = new List<LightingAudit.Fixture>
            {
                new LightingAudit.Fixture { Id = 1, Level = "LEVEL05" },
                new LightingAudit.Fixture { Id = 2, Level = "LEVEL05" },
                new LightingAudit.Fixture { Id = 3, Level = "LEVEL06" },
            };
            var tags = new List<LightingAudit.Tag>
            {
                new LightingAudit.Tag { Id = 10, ViewId = 50, Tagged = new long[] { 1 } },
                new LightingAudit.Tag { Id = 11, ViewId = 60, Tagged = new long[] { 3 } },
            };
            var views = new List<LightingAudit.ViewCount>
            {
                new LightingAudit.ViewCount { ViewId = 50, Name = "LEVEL05 - NON-UNIT LIGHTING", Level = "LEVEL05" },
                new LightingAudit.ViewCount { ViewId = 60, Name = "LEVEL06 - NON-UNIT LIGHTING", Level = "LEVEL06", Visible = new long[] { 3 } },
            };
            var r = LightingAudit.Summarize(fx, tags, views, out _);
            var rows = (List<object>)r["vistas"];
            Assert.Equal(new object[] { 50L, "LEVEL05 - NON-UNIT LIGHTING", "LEVEL05", 2L, 1L, 1L }, (object[])rows[0]);
            Assert.Equal(new object[] { 60L, "LEVEL06 - NON-UNIT LIGHTING", "LEVEL06", 1L, 1L, 0L }, (object[])rows[1]);
            Assert.StartsWith("nivel", (string)r["vistas_metodo"]);
        }

        [Fact]
        public void Audit_LevelFilter_DoesNotFlagOtherLevelsTagsAsOrphans()
        {
            // Regresión 0.2.2: con lvl=LEVEL 02 salían 877 huérfanos falsos.
            var all = new long[] { 1, 2, 3 };
            var fx = new List<LightingAudit.Fixture> { new LightingAudit.Fixture { Id = 1, Level = "LEVEL 02" } };
            var tags = new List<LightingAudit.Tag>
            {
                new LightingAudit.Tag { Id = 10, ViewId = 50, ViewLevel = "LEVEL 02", Tagged = new long[] { 1 } },
                new LightingAudit.Tag { Id = 11, ViewId = 60, ViewLevel = "LEVEL 03", Tagged = new long[] { 2 } },   // de otro nivel
                new LightingAudit.Tag { Id = 12, ViewId = 60, ViewLevel = "LEVEL 03", Tagged = new long[0] },        // huérfano de otro nivel
                new LightingAudit.Tag { Id = 13, ViewId = 50, ViewLevel = "LEVEL 02", Tagged = new long[0] },        // huérfano de este nivel
            };
            var r = LightingAudit.Summarize(fx, tags, null, out var sets, all, new List<string> { "level 02" });
            Assert.Equal(2L, r["tags"]);
            Assert.Equal(new long[] { 13 }, sets["$tags_huerfanos"]);
            Assert.Equal(0L, ((Dictionary<string, object>)r["sin_tag"])["n"]);
        }

        [Fact]
        public void Audit_CleanModelPassesRule()
        {
            var fx = new List<LightingAudit.Fixture> { new LightingAudit.Fixture { Id = 1, Level = "L" } };
            var tags = new List<LightingAudit.Tag> { new LightingAudit.Tag { Id = 2, ViewId = 5, Tagged = new long[] { 1 } } };
            var r = LightingAudit.Summarize(fx, tags, null, out var sets);
            Assert.Equal(true, r["cumple_regla"]);
            Assert.Empty(sets);
        }
    }
}
