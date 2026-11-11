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

        private static string Put(QuerySnapshotStore store, string session, string doc, long rev, QuerySpec q, IReadOnlyList<Dictionary<string, object>> rows, Func<long> live)
            => store.Put(session, doc, rev, q, rows, live, out _);

        [Fact]
        public void Cursor_IsBoundToSessionDocumentAndRevision()
        {
            var store = new QuerySnapshotStore();
            long rev = 7;
            var q = Spec(100);
            var id = Put(store, "s1", "d1", rev, q, Rows(300), () => rev);
            Assert.NotNull(id);

            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out var snap));
            Assert.Equal(300, snap.Rows.Count);
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", "d1", out _));
            Assert.Equal(SnapshotLookup.Unknown, store.Get(id, "s2", null, out _));          // otra sesión: no se revela nada
            Assert.Equal(SnapshotLookup.OtherDocument, store.Get(id, "s1", "d2", out _));    // otro documento
            Assert.Equal(SnapshotLookup.Unknown, store.Get("qnoexiste", "s1", null, out _));

            rev = 8;                                                                          // el modelo cambió
            Assert.Equal(SnapshotLookup.Stale, store.Get(id, "s1", null, out _));
            Assert.Equal(SnapshotLookup.Unknown, store.Get(id, "s1", null, out _));          // y se descartó
            Assert.Equal(0, store.Count);
            Assert.Equal(0, store.EstimatedBytes);
        }

        [Fact]
        public void LiveRevFailure_FailsClosed()
        {
            var store = new QuerySnapshotStore();
            var id = Put(store, "s1", "d1", 1, Spec(10), Rows(30), () => throw new InvalidOperationException("documento cerrado"));
            Assert.Equal(SnapshotLookup.Stale, store.Get(id, "s1", null, out _));
        }

        [Fact]
        public void Ttl_ExpiresUnusedSnapshots_AccessExtendsIt_AndExpiryIsReported()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new QuerySnapshotStore(TimeSpan.FromMinutes(5), clock: () => now);
            var id = Put(store, "s1", "d1", 1, Spec(10), Rows(30), () => 1);
            now = now.AddMinutes(4);
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out _));
            now = now.AddMinutes(4);   // 8 min desde la creación, 4 desde el último uso
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out _));
            now = now.AddMinutes(6);
            Assert.Equal(SnapshotLookup.Evicted, store.Get(id, "s1", null, out _));          // se informa, no se invalida en silencio
            Assert.Contains("vencido", store.EvictionReason(id, "s1"));
            Assert.Null(store.EvictionReason(id, "s2"));                                     // el motivo sólo se da a la sesión dueña
            Assert.Equal(0, store.Cells);
            Assert.Equal(0, store.EstimatedBytes);
        }

        [Fact]
        public void Budget_IsInEstimatedBytes_LongTextsCountByTheirSize()
        {
            // Mismo número de celdas, distinto tamaño real: el presupuesto ve la diferencia (antes, por celdas, eran iguales).
            var shortRows = Rows(200);
            var longRows = Rows(200).Select(r => { r["fam"] = new string('x', 5000); return r; }).ToList();
            var q = Spec(100);
            var small = QuerySnapshotStore.EstimateBytes(QuerySnapshotStore.Project(q, shortRows));
            var large = QuerySnapshotStore.EstimateBytes(QuerySnapshotStore.Project(q, longRows));
            // fam pasa de "F0".."F6" (2 caracteres) a 5000 caracteres: la estimación crece exactamente 2 bytes UTF-16 por carácter añadido.
            Assert.Equal(200L * 2 * (5000 - 2), large - small);

            // Presupuesto de 1 MB: las filas cortas caben; 200 textos de 5000 caracteres (~2 MB) no, y se dice por qué.
            var store = new QuerySnapshotStore(maxBytes: 1024 * 1024, maxBytesPerDocument: 1024 * 1024);
            Assert.NotNull(Put(store, "s1", "d1", 1, q, shortRows, () => 1));
            Assert.Null(store.Put("s1", "d1", 1, q, longRows, () => 1, out var refusal));
            Assert.Contains("MB estimados", refusal);
            Assert.True(store.EstimatedBytes <= store.MaxBytes);
        }

        [Fact]
        public void Projection_KeepsOnlyRequestedColumns_AndPagesStayIdentical()
        {
            var q = Spec(50, "mm", "lvl!=L0");
            var raw = Rows(1000);
            foreach (var r in raw) { r["comments"] = new string('c', 300); r["ws"] = "Workset largo de filtro"; }   // columnas no pedidas
            var filtered = ResultTable.Filter(q, raw, out _);
            var store = new QuerySnapshotStore();
            var id = Put(store, "s1", "d1", 1, q, filtered, () => 1);
            Assert.Equal(SnapshotLookup.Ok, store.Get(id, "s1", null, out var snap));
            Assert.All(snap.Rows, r => Assert.Equal(q.Fields.Count, r.Count));             // proyección compacta
            var pages = (int)Math.Ceiling(filtered.Count / 50.0);
            for (var p = 1; p <= pages; p++)
                Assert.Equal(RowsOf(ResultTable.Render(q, filtered, p, 50)), RowsOf(ResultTable.Render(snap.Spec, snap.Rows, p, 50)));
        }

        [Fact]
        public void GlobalAndPerDocumentBudgets_EvictLeastRecentlyUsed_AndReportIt()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var q = Spec(10);
            var rows = Rows(100);
            var one = QuerySnapshotStore.EstimateBytes(QuerySnapshotStore.Project(q, rows));
            // Global para 3 instantáneas; por documento para 2.
            var store = new QuerySnapshotStore(maxBytes: one * 3 + 10, maxBytesPerDocument: one * 2 + 10, maxPerSession: 10, clock: () => now);
            var a = Put(store, "s1", "d1", 1, q, rows, () => 1); now = now.AddSeconds(1);
            var b = Put(store, "s2", "d1", 1, q, rows, () => 1); now = now.AddSeconds(1);
            Assert.Equal(SnapshotLookup.Ok, store.Get(a, "s1", null, out _)); now = now.AddSeconds(1);   // a pasa a ser la más reciente
            var c = Put(store, "s3", "d1", 1, q, rows, () => 1); now = now.AddSeconds(1);                // excede d1: sale b (LRU de d1)
            Assert.Equal(SnapshotLookup.Evicted, store.Get(b, "s2", null, out _));
            Assert.Contains("documento", store.EvictionReason(b, "s2"));
            var d = Put(store, "s4", "d2", 1, q, rows, () => 1); now = now.AddSeconds(1);                // cabe en el global (3)
            var e = Put(store, "s5", "d3", 1, q, rows, () => 1);                                         // excede el global: sale la LRU (a)
            Assert.Equal(SnapshotLookup.Evicted, store.Get(a, "s1", null, out _));
            Assert.Contains("global", store.EvictionReason(a, "s1"));
            foreach (var (id, s) in new[] { (c, "s3"), (d, "s4"), (e, "s5") }) Assert.Equal(SnapshotLookup.Ok, store.Get(id, s, null, out _));
            Assert.True(store.EstimatedBytes <= store.MaxBytes);
        }

        [Fact]
        public void ManyAgentsAndDocuments_StayWithinBudget_NoCrossDocumentOrSessionLeak()
        {
            var q = Spec(20);
            var store = new QuerySnapshotStore(maxBytes: 2 * 1024 * 1024, maxBytesPerDocument: 512 * 1024, maxPerSession: 3);
            var issued = new List<(string id, string session, string doc)>();
            System.Threading.Tasks.Parallel.For(0, 64, i =>
            {
                var session = "s" + (i % 16); var doc = "d" + (i % 5);
                var id = Put(store, session, doc, 1, q, Rows(400), () => 1);
                if (id != null) lock (issued) issued.Add((id, session, doc));
            });
            Assert.True(store.EstimatedBytes <= store.MaxBytes, store.EstimatedBytes + " > " + store.MaxBytes);
            foreach (var (id, session, doc) in issued)
            {
                var r = store.Get(id, session, doc, out var snap);
                Assert.Contains(r, new[] { SnapshotLookup.Ok, SnapshotLookup.Evicted });
                if (r == SnapshotLookup.Ok) { Assert.Equal(doc, snap.DocId); Assert.Equal(session, snap.SessionId); Assert.Equal(400, snap.Rows.Count); }
                Assert.NotEqual(SnapshotLookup.Ok, store.Get(id, session + "x", doc, out _));     // nunca a otra sesión
                Assert.NotEqual(SnapshotLookup.Ok, store.Get(id, session, doc + "x", out _));     // nunca a otro documento
            }
        }

        [Fact]
        public void PerSessionLimit_And_DropSessionAndDocument()
        {
            var store = new QuerySnapshotStore(maxPerSession: 2);
            var ids = Enumerable.Range(0, 3).Select(_ => Put(store, "s1", "d1", 1, Spec(10), Rows(20), () => 1)).ToList();
            Assert.Equal(2, store.Count);
            Assert.Equal(SnapshotLookup.Evicted, store.Get(ids[0], "s1", null, out _));
            Assert.Equal(SnapshotLookup.Ok, store.Get(ids[2], "s1", null, out _));
            var other = Put(store, "s2", "d2", 1, Spec(10), Rows(20), () => 1);
            store.DropSession("s1");
            Assert.Equal(1, store.Count);
            store.DropDocument("d2");
            Assert.Equal(SnapshotLookup.Evicted, store.Get(other, "s2", null, out _));
            Assert.Contains("documento se cerró", store.EvictionReason(other, "s2"));
            Assert.Equal(0, store.Cells);
            Assert.Equal(0, store.EstimatedBytes);
        }

        [Fact]
        public void FullRead_51Pages_ServedFromSnapshot_IsFast()
        {
            // Referencia del issue: 51 llamadas, 19 727 ms (cada una releía el modelo). Desde la instantánea, 50 páginas son trabajo
            // en memoria. Umbral holgado para máquinas de CI lentas; lo que se comprueba es el orden de magnitud.
            var q = Spec(100);
            var filtered = ResultTable.Filter(q, Rows(5100), out _);
            var store = new QuerySnapshotStore();
            var id = Put(store, "s1", "d1", 3, q, filtered, () => 3);
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

        [Theory]
        [InlineData("documento")]
        [InlineData("sesion")]
        public void Get_RevalidatesAfterLiveRev_ConcurrentDropIsNeverServed(string drop)
        {
            // AUD-02: LiveRev se evalúa fuera del bloqueo. Una barrera lo detiene ahí mientras otro hilo descarta la instantánea:
            // al reanudar, Get NO puede devolver Ok con un objeto ya descartado.
            using (var inLiveRev = new System.Threading.ManualResetEventSlim(false))
            using (var release = new System.Threading.ManualResetEventSlim(false))
            {
                var store = new QuerySnapshotStore();
                var block = true;
                var id = Put(store, "s1", "d1", 5, Spec(10), Rows(30), () =>
                {
                    if (block) { inLiveRev.Set(); release.Wait(10000); }
                    return 5;
                });
                SnapshotLookup result = SnapshotLookup.Ok;
                QuerySnapshot served = null;
                var reader = new System.Threading.Thread(() => result = store.Get(id, "s1", "d1", out served));
                reader.Start();
                Assert.True(inLiveRev.Wait(10000));
                if (drop == "documento") store.DropDocument("d1"); else store.DropSession("s1");
                release.Set();
                Assert.True(reader.Join(10000));
                Assert.NotEqual(SnapshotLookup.Ok, result);
                Assert.Null(served);
                Assert.Equal(drop == "documento" ? SnapshotLookup.Evicted : SnapshotLookup.Unknown, result);
                block = false;
                Assert.Equal(0, store.Count);
                Assert.Equal(0, store.EstimatedBytes);
            }
        }

        [Fact]
        public void Get_ManyThreadsRacingWithDrops_NeverServesADroppedSnapshot()
        {
            var store = new QuerySnapshotStore(maxPerSession: 64);
            var ids = Enumerable.Range(0, 32).Select(i => Put(store, "s1", "d" + (i % 4), 1, Spec(10), Rows(20), () => { System.Threading.Thread.SpinWait(200); return 1; })).ToList();
            var dropped = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            var violations = 0;
            var readers = Enumerable.Range(0, 8).Select(r => new System.Threading.Thread(() =>
            {
                for (var k = 0; k < 400; k++)
                {
                    var i = (k * 7 + r) % ids.Count;
                    var doc = "d" + (i % 4);
                    var droppedBefore = dropped.ContainsKey(doc);   // el descarte terminó ANTES de empezar esta lectura
                    var res = store.Get(ids[i], "s1", doc, out var snap);
                    if (res == SnapshotLookup.Ok && droppedBefore) System.Threading.Interlocked.Increment(ref violations);
                }
            })).ToList();
            readers.ForEach(t => t.Start());
            foreach (var d in new[] { "d0", "d1", "d2", "d3" }) { System.Threading.Thread.Sleep(5); store.DropDocument(d); dropped[d] = true; }
            readers.ForEach(t => t.Join(20000));
            // Tras DropDocument nada de ese documento se sirve: una lectura que empieza después del descarte nunca obtiene Ok.
            Assert.Equal(0, violations);
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void IncrementalProjection_StopsAsSoonAsBudgetIsExceeded_NoPartialResult()
        {
            // AUD-03: con 10 000 filas de textos de 2 000 caracteres (~40 MB estimados) y un límite de 1 MB, la proyección se detiene
            // en cuanto lo supera: devuelve null (nunca filas recortadas) y no llega a construir el resto.
            var q = Spec(100);
            var visited = 0;
            var rows = new CountingList(Rows(10000).Select(r => { r["fam"] = new string('y', 2000); return r; }).ToList(), () => visited++);
            var res = QuerySnapshotStore.ProjectWithinBudget(q, rows, 1024 * 1024, out var bytes);
            Assert.Null(res);
            Assert.True(bytes > 1024 * 1024);
            Assert.True(visited < 1000, "se detuvo tras " + visited + " filas");     // ~250 filas de ~4 KB cada una
            // Si cabe, devuelve todas las filas y la misma estimación que el cálculo completo.
            var small = Rows(500);
            var ok = QuerySnapshotStore.ProjectWithinBudget(q, small, 64L * 1024 * 1024, out var b2);
            Assert.Equal(500, ok.Count);
            Assert.Equal(QuerySnapshotStore.EstimateBytes(QuerySnapshotStore.Project(q, small)), b2);
        }

        /// <summary>Lista de sólo lectura que cuenta cuántas filas se recorren.</summary>
        private sealed class CountingList : IReadOnlyList<Dictionary<string, object>>
        {
            private readonly List<Dictionary<string, object>> _inner; private readonly Action _onVisit;
            public CountingList(List<Dictionary<string, object>> inner, Action onVisit) { _inner = inner; _onVisit = onVisit; }
            public Dictionary<string, object> this[int index] { get { _onVisit(); return _inner[index]; } }
            public int Count => _inner.Count;
            public IEnumerator<Dictionary<string, object>> GetEnumerator() { foreach (var r in _inner) { _onVisit(); yield return r; } }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}