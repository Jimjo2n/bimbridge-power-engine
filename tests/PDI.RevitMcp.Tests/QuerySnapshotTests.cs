// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Issue #2: lectura completa paginada desde una sola instantánea, ligada a sesión, documento y revisión.</summary>
    public class QuerySnapshotTests
    {
        private static List<Dictionary<string, object>> Rows(int n) =>
            Enumerable.Range(1, n).Select(i => new Dictionary<string, object>
            {
                ["id"] = (long)i, ["fam"] = "F" + (i % 7), ["type"] = "T" + (i % 3), ["lvl"] = "L" + (i % 5), ["elev"] = i * 0.5,
            }).ToList();

        private static QuerySpec Spec(int pageSize, string units = "ft", params string[] where)
        {
            var a = new Dictionary<string, object> { ["cat"] = "LightingFixtures", ["f"] = "id,fam,type,lvl,elev", ["page_size"] = (long)pageSize, ["units"] = units };
            if (where.Length > 0) a["where"] = where.Cast<object>().ToList();
            return QuerySpec.Parse(a);
        }

        private static List<string> RowsOf(object rendered) =>
            ((List<object>)((Dictionary<string, object>)rendered)["rows"]).Select(r => Json.Write(r)).ToList();

        [Fact]
        public void PagedFromSnapshot_EqualsLegacyPerPageReads_IncludingUnitsAndWhere()
        {
            // Lectura clásica: cada página vuelve a leer y filtrar (lo que hacía el add-in en cada llamada).
            const int n = 5100, ps = 100;
            var legacy = new List<string>();
            var pages = 0;
            for (var p = 1; ; p++)
            {
                var q = Spec(ps, "mm", "lvl!=L0"); q.Page = p;
                var r = (Dictionary<string, object>)ResultTable.Build(q, Rows(n), out _);
                legacy.AddRange(RowsOf(r));
                pages = (int)(long)r["pages"];
                if (p >= pages) break;
            }

            // Nueva: una lectura + filtro, páginas renderizadas desde la instantánea.
            var q1 = Spec(ps, "mm", "lvl!=L0");
            var filtered = ResultTable.Filter(q1, Rows(n), out var matched);
            var fromSnap = new List<string>();
            for (var p = 1; p <= pages; p++) fromSnap.AddRange(RowsOf(ResultTable.Render(q1, filtered, p, ps)));
            // Renderizar dos veces no altera las filas (no hay doble conversión a mm).
            var again = new List<string>();
            for (var p = 1; p <= pages; p++) again.AddRange(RowsOf(ResultTable.Render(q1, filtered, p, ps)));

            Assert.Equal(matched.Count, legacy.Count);
            Assert.Equal(legacy, fromSnap);
            Assert.Equal(fromSnap, again);
        }

        [Fact]
        public void Cursor_IsBoundToSessionDocumentAndRevision()
        {
            var store = new QuerySnapshotStore();
            long rev = 7;
            var q = Spec(100);
            var id = store.Put("s1", "d1", rev, q, Rows(300), () => rev);
            Assert.NotNull(id);

            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out var snap));
            Assert.Equal(300, snap.Rows.Count);
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", "d1", out _));
            Assert.Equal(SnapshotLookup.Unknown, store.Get(id, "s2", null, out _));          // otra sesión
            Assert.Equal(SnapshotLookup.OtherDocument, store.Get(id, "s1", "d2", out _));    // otro documento
            Assert.Equal(SnapshotLookup.Unknown, store.Get("qnoexiste", "s1", null, out _));

            rev = 8;                                                                          // el modelo cambió
            Assert.Equal(SnapshotLookup.Stale, store.Get(id, "s1", null, out _));
            Assert.Equal(SnapshotLookup.Unknown, store.Get(id, "s1", null, out _));          // y se descartó
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void LiveRevFailure_FailsClosed()
        {
            var store = new QuerySnapshotStore();
            var id = store.Put("s1", "d1", 1, Spec(10), Rows(30), () => throw new InvalidOperationException("documento cerrado"));
            Assert.Equal(SnapshotLookup.Stale, store.Get(id, "s1", null, out _));
        }

        [Fact]
        public void Ttl_ExpiresUnusedSnapshots_AccessExtendsIt()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new QuerySnapshotStore(TimeSpan.FromMinutes(5), clock: () => now);
            var id = store.Put("s1", "d1", 1, Spec(10), Rows(30), () => 1);
            now = now.AddMinutes(4);
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out _));
            now = now.AddMinutes(4);   // 8 min desde la creación, 4 desde el último uso
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out _));
            now = now.AddMinutes(6);
            Assert.Equal(SnapshotLookup.Unknown, store.Get(id, "s1", null, out _));
            Assert.Equal(0, store.Cells);
        }

        [Fact]
        public void MemoryBudget_EvictsLeastRecentlyUsed_AndRejectsOversized()
        {
            // 5 campos por fila: 100 filas = 500 celdas. Presupuesto 1200 celdas → caben 2.
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new QuerySnapshotStore(maxCells: 1200, maxPerSession: 10, clock: () => now);
            var a = store.Put("s1", "d1", 1, Spec(10), Rows(100), () => 1); now = now.AddSeconds(1);
            var b = store.Put("s2", "d1", 1, Spec(10), Rows(100), () => 1); now = now.AddSeconds(1);
            Assert.Equal(SnapshotLookup.Ok, store.Get(a, "s1", null, out _)); now = now.AddSeconds(1);   // a pasa a ser la más reciente
            var c = store.Put("s3", "d1", 1, Spec(10), Rows(100), () => 1);
            Assert.Equal(SnapshotLookup.Unknown, store.Get(b, "s2", null, out _));   // b era la menos usada
            Assert.Equal(SnapshotLookup.Ok, store.Get(a, "s1", null, out _));
            Assert.Equal(SnapshotLookup.Ok, store.Get(c, "s3", null, out _));
            Assert.True(store.Cells <= 1200);
            Assert.Null(store.Put("s1", "d1", 1, Spec(10), Rows(300), () => 1));     // 1500 celdas: no cabe sola → sin cursor
        }

        [Fact]
        public void PerSessionLimit_And_DropSessionAndDocument()
        {
            var store = new QuerySnapshotStore(maxPerSession: 2);
            var ids = Enumerable.Range(0, 3).Select(_ => store.Put("s1", "d1", 1, Spec(10), Rows(20), () => 1)).ToList();
            Assert.Equal(2, store.Count);
            Assert.Equal(SnapshotLookup.Ok, store.Get(ids[2], "s1", null, out _));
            var other = store.Put("s2", "d2", 1, Spec(10), Rows(20), () => 1);
            store.DropSession("s1");
            Assert.Equal(1, store.Count);
            store.DropDocument("d2");
            Assert.Equal(SnapshotLookup.Unknown, store.Get(other, "s2", null, out _));
            Assert.Equal(0, store.Cells);
        }

        [Fact]
        public void FullRead_51Pages_ServedFromSnapshot_IsFast()
        {
            // Referencia del issue: 51 llamadas, 19 727 ms (cada una releía el modelo). Desde la instantánea, 50 páginas son trabajo
            // en memoria. Umbral holgado para máquinas de CI lentas; lo que se comprueba es el orden de magnitud.
            var q = Spec(100);
            var filtered = ResultTable.Filter(q, Rows(5100), out _);
            var store = new QuerySnapshotStore();
            var id = store.Put("s1", "d1", 3, q, filtered, () => 3);
            var sw = Stopwatch.StartNew();
            var total = 100;
            for (var p = 2; p <= 51; p++)
            {
                Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", "d1", out var s));
                total += RowsOf(ResultTable.Render(s.Spec, s.Rows, p, s.Spec.PageSize)).Count;
            }
            sw.Stop();
            Assert.Equal(5100, total);
            Assert.True(sw.ElapsedMilliseconds < 2000, "50 páginas desde instantánea: " + sw.ElapsedMilliseconds + " ms");
        }
    }
}
