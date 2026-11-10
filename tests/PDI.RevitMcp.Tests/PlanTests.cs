// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Planes y cola retenida (inyección asíncrona), sin Revit.</summary>
    public class PlanTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-cola-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private DateTime _now = new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);

        public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

        private PlanStore NewStore() => new PlanStore(_root, () => _now);

        private Plan NewPlan(PlanStore s, string session, params (long id, string before, string after)[] entries)
        {
            var p = new Plan { Id = s.NewId(), SessionId = session, Client = session, DocKey = "C:\\modelos\\Tampa.rvt", DocTitle = "Tampa", CreatedUtc = _now, Summary = "prueba" };
            foreach (var e in entries) p.Entries.Add(new PlanEntry { Id = e.id, Op = "set_elevation", Key = "elev", Before = e.before, After = e.after, Target = double.Parse(e.after) });
            s.Add(p);
            return p;
        }

        [Fact]
        public void OpSpec_ParsesTypedOps_AndRejectsUnknown()
        {
            var args = Json.ParseObject("{\"ops\":[{\"op\":\"set_param\",\"ids\":\"1,2\",\"name\":\"Comments\",\"value\":\"X\"}," +
                                        "{\"op\":\"set_elevation\",\"ids\":\"$l5\",\"ft\":10.5},{\"op\":\"delete\",\"ids\":[3]}]}");
            var ops = OpSpec.ParseAll(args);
            Assert.Equal(3, ops.Count);
            Assert.Equal(new long[] { 1, 2 }, ops[0].Ids);
            Assert.Equal("$l5", ops[1].Set);
            Assert.Equal(10.5, ops[1].Ft);
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[{\"op\":\"run_python\",\"ids\":\"1\"}]}")));
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[{\"op\":\"set_param\",\"ids\":\"1\"}]}")));
            Assert.Throws<QueryException>(() => OpSpec.ParseAll(Json.ParseObject("{\"ops\":[{\"op\":\"delete\"}]}")));
        }

        [Fact]
        public void Canon_IsStableAcrossFormats()
        {
            Assert.Equal(PlanEntry.Canon(10.1234567891), PlanEntry.Canon(10.123457));
            Assert.Equal("1", PlanEntry.Canon(true));
            Assert.Equal("17855175", PlanEntry.Canon(17855175L));
            Assert.Equal("", PlanEntry.Canon(null));
        }

        [Fact]
        public void HeldPlans_SurviveRestart_InOrder_AndFinishDeletesFile()
        {
            var s = NewStore();
            var a = NewPlan(s, "S-claude", (1, "10", "11"));
            var b = NewPlan(s, "S-codex", (2, "10", "12"));
            s.Hold(b);           // llega primero b
            _now = _now.AddSeconds(5);
            s.Hold(a);

            // "Reinicio de Revit": almacén nuevo que lee el disco.
            var s2 = NewStore();
            Assert.Equal(2, s2.LoadPersisted("c:\\MODELOS\\tampa.rvt")); // la ruta no distingue mayúsculas
            var held = s2.Held("C:\\modelos\\Tampa.rvt");
            Assert.Equal(new[] { b.Id, a.Id }, held.Select(p => p.Id).ToArray());
            Assert.Equal("11", held[1].Entries[0].After);
            Assert.Equal(11.0, Convert.ToDouble(held[1].Entries[0].Target));

            s2.Finish(held[0], PlanStatus.Aplicado, "ok");
            Assert.Single(Directory.GetFiles(s2.DocDir("C:\\modelos\\Tampa.rvt"), "P-*.json"));
            Assert.Equal(0, NewStore().LoadPersisted("C:\\otro.rvt"));
        }

        [Fact]
        public void Expiry_AppliesToReadyPlans_NotToHeld()
        {
            var s = NewStore();
            var ready = NewPlan(s, "S", (1, "1", "2"));
            var held = NewPlan(s, "S", (2, "1", "2"));
            s.Hold(held);
            _now = _now.AddHours(3);
            Assert.Equal(1, s.ExpireOld(TimeSpan.FromMinutes(15)));
            Assert.Equal(PlanStatus.Vencido, ready.Status);
            Assert.Equal(PlanStatus.Retenido, held.Status);
        }

        [Fact]
        public void Conflicts_AreValueBased()
        {
            var s = NewStore();
            var p = NewPlan(s, "S", (1, "10", "11"), (2, "10", "12"));
            var model = new Dictionary<long, string> { [1] = "10", [2] = "10" };
            Assert.Empty(p.FindConflicts(e => model[e.Id]));
            model[2] = "9.5"; // el usuario movió la luminaria 2 mientras el plan esperaba
            var c = p.FindConflicts(e => model[e.Id]);
            Assert.Single(c);
            Assert.Equal(2, c[0].Id);
        }

        [Fact]
        public void NewPlans_ChainOnTopOfHeldOnes()
        {
            var s = NewStore();
            var first = NewPlan(s, "S-claude", (1, "10", "11"));
            s.Hold(first);
            Assert.True(s.TryHeldAfter(first.DocKey, 1, "elev", out var after, out var pid));
            Assert.Equal("11", after);
            Assert.Equal(first.Id, pid);
            Assert.False(s.TryHeldAfter(first.DocKey, 2, "elev", out _, out _));
        }

        [Fact]
        public void Discard_And_SessionList()
        {
            var s = NewStore();
            var a = NewPlan(s, "S-claude", (1, "1", "2"));
            var b = NewPlan(s, "S-claude", (2, "1", "2"));
            NewPlan(s, "S-codex", (3, "1", "2"));
            s.Hold(a); s.Hold(b);
            Assert.Equal(2, s.DiscardHeld(a.DocKey, "usuario"));
            Assert.Equal(PlanStatus.Descartado, a.Status);
            Assert.Empty(s.Held(a.DocKey));
            Assert.Equal(2, s.ForSession("S-claude").Count);
            Assert.Equal(0, NewStore().LoadPersisted(a.DocKey));
        }

        [Fact]
        public void HoldSwitch_PersistsInFile()
        {
            Directory.CreateDirectory(_root);
            var h = new HoldSwitch(_root);
            Assert.False(h.On);
            Assert.True(h.Toggle());
            Assert.True(new HoldSwitch(_root).On);
            h.Set(false);
            Assert.False(new HoldSwitch(_root).On);
        }

        [Fact]
        public void Describe_IsCompact()
        {
            var s = NewStore();
            var p = NewPlan(s, "S", Enumerable.Range(1, 40).Select(i => ((long)i, "10", "11")).ToArray());
            var d = p.Describe(5);
            Assert.Equal(5, ((List<object>)d["filas"]).Count);
            Assert.Equal(35L, d["filas_omitidas"]);
            Assert.Equal(40L, d["cambios"]);
        }
    }
}
