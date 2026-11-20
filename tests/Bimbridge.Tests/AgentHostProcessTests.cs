// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.AgentHost;
using Bimbridge.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Bimbridge.Tests
{
    /// <summary>
    /// AH-01 / AH-02 / AH-04 with INDEPENDENT PROCESSES: each phase is a separate `dotnet Bimbridge.AgentHost.dll` that kills itself
    /// (SIGKILL, no finally) at the injected point; the next phase is another operating system process that only has what
    /// was left on disk. No in-memory reference survives between phases. Each test checks in the model (reopened from
    /// disk) how many committed transactions the operation has: never more than one.
    /// </summary>
    public class AgentHostProcessTests
    {
        private readonly ITestOutputHelper _out;
        public AgentHostProcessTests(ITestOutputHelper o) { _out = o; }

        private sealed class Case
        {
            public string Dir, Model, State, PlanFile, Mark, Hash, Query, SetOp, QcOp;
            public PlanDraft Plan;
        }

        private static Case Setup(int fixtures = 5, long expect = 5, Dictionary<string, object> config = null)
        {
            var c = new Case { Dir = AH.NewDir("proc") };
            c.Plan = AH.BasePlan(expect);
            c.Model = AH.Model(c.Dir, fixtures, config);
            c.State = Path.Combine(c.Dir, "estado");
            c.PlanFile = AH.WritePlan(c.Dir, c.Plan);
            c.Mark = Path.Combine(c.Dir, "caida.txt");
            c.Hash = AH.Hash(c.Plan);
            c.Query = c.Plan.Ops[0].Id; c.SetOp = c.Plan.Ops[1].Id; c.QcOp = c.Plan.Ops[2].Id;
            return c;
        }

        /// <summary>
        /// AUD-04 (#28): the crash injection tests do not test expiry. They use a long lease (no race between machine load
        /// and the TTL before the crash point) and, after a crash, move the dead process's lease expiry forward in the fixture.
        /// Expiry, fencing and renewal have their own tests (AH04_*).
        /// </summary>
        private const string CrashTestLeaseMs = "3600000";

        private AH.ChildResult Exec(Case c, string fault = null, params string[] extra)
        {
            AH.ExpireDeadLease(c.State);
            var r = AH.Run(fault, c.Mark, new[] { "ejecutar", c.PlanFile, c.State, "--modelo", c.Model, "--lease-ms", CrashTestLeaseMs }.Concat(extra).ToArray());
            _out.WriteLine("$ ejecutar" + (fault != null ? "  [BIMBRIDGE_AH_FAULT=" + fault + "]" : "") + "\n" + r);
            return r;
        }

        private static void AssertCrashedAt(Case c, AH.ChildResult r, string point)
        {
            Assert.True(r.Killed, "the process should have died at " + point + ": " + r);
            Assert.Contains(point, File.ReadAllText(c.Mark));
        }

        // ---------------- AH-01 ----------------

        [Fact]
        public void AH01_CrashAfterFourOfFiveElements_RolledBack_ExecutedOnceOnResume()
        {
            var c = Setup();
            var r1 = Exec(c, "backend.mitad_tx@" + c.SetOp + ":4");
            AssertCrashedAt(c, r1, "backend.mitad_tx");
            // the "transaction" never reached the WAL: the on-disk model has NONE of the 4 in-memory changes
            Assert.Equal(0, AH.TxCount(c.Model, c.Hash, c.SetOp));
            var m = DurableSimModel.Open(c.Model);
            Assert.All(m.Targets(new Dictionary<string, object> { ["categoria"] = AH.Cat }), id => Assert.Null(m.Get(id, "Mounting Height")));
            Assert.Equal(OpState.EnEjecucion, AH.StateOf(c.State, c.SetOp));

            var r2 = Exec(c);
            Assert.Equal(0, r2.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(2, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));   // attempt 1 (crashed) + attempt 2 (after observing there was no effect)
            Assert.Contains(AH.Notes(c.State, c.SetOp), n => ((Dictionary<string, object>)n["datos"])["efecto"].ToString() == "NoObservado");
            var st = AH.Read(c.State, out _, out _);
            Assert.Equal(OpState.Verificada, st.Ops[c.SetOp].State);
            Assert.Equal(5L, Arg.GetLong(st.Ops[c.SetOp].Result, "cambiados"));
        }

        [Fact]
        public void AH01_CrashAfterAllElementsCommitted_RecoveredFromModelEvidence_VerifiedWithoutRepeating()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "backend.tras_commit@" + c.SetOp), "backend.tras_commit");
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            var r2 = Exec(c);
            Assert.Equal(0, r2.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));
            var st = AH.Read(c.State, out _, out _);
            var rec = st.Ops[c.SetOp];
            Assert.Equal(OpState.Verificada, rec.State);
            Assert.Equal(true, rec.Result["recuperada"]);
            Assert.Equal(5L, Arg.GetLong(rec.Result, "elementos"));        // rebuilt from the transaction, not invented
            Assert.Equal("Satisfechas", Arg.GetString(rec.Evidence, "postcondiciones"));
            Assert.Equal(OpState.Verificada, st.Ops[c.QcOp].State);
        }

        [Fact]
        public void AH01_CrashAfterUnexpectedNumberOfElements_VerificationFails_NotRepeated()
        {
            var c = Setup(fixtures: 6, expect: 5);   // the model has 6; the plan expected 5
            AssertCrashedAt(c, Exec(c, "backend.tras_commit@" + c.SetOp), "backend.tras_commit");
            var r2 = Exec(c);
            Assert.Equal(2, r2.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            var st = AH.Read(c.State, out _, out _);
            Assert.Equal(OpState.Fallida, st.Ops[c.SetOp].State);
            Assert.Equal(ErrorClass.Verificacion, st.Ops[c.SetOp].Error);
            Assert.Contains("esperado 5, obtenido 6", st.Ops[c.SetOp].Note);
            Assert.Equal(OpState.Bloqueada, st.Ops[c.QcOp].State);
        }

        [Fact]
        public void AH01_CrashAfterCommitBeforeResultSaved_ResultReconstructed_NotRepeated()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "runner.tras_ejecutar@" + c.SetOp), "runner.tras_ejecutar");
            Assert.Equal(OpState.EnEjecucion, AH.StateOf(c.State, c.SetOp));    // the result never reached the journal
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));             // but the transaction did reach the model
            Assert.Equal(0, Exec(c).Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));
            Assert.Equal(OpState.Verificada, AH.StateOf(c.State, c.SetOp));
        }

        [Fact]
        public void AH01_CrashDuringVerification_ReverifiedOnly()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "runner.en_verificacion@" + c.SetOp), "runner.en_verificacion");
            Assert.Equal(OpState.Ejecutada, AH.StateOf(c.State, c.SetOp));
            Assert.Equal(0, Exec(c).Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.Verificada));
        }

        [Fact]
        public void AH01_CrashAfterVerificationBeforeFinalRecord_ReverifiedOnly()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "runner.tras_verificar@" + c.SetOp), "runner.tras_verificar");
            Assert.Equal(OpState.Ejecutada, AH.StateOf(c.State, c.SetOp));
            Assert.Equal(0, Exec(c).Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.Verificada));
        }

        [Fact]
        public void AH01_EffectProvenButEvidenceCompacted_RequiresVerification_NeverVerifiedNorRepeated()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "backend.tras_commit@" + c.SetOp), "backend.tras_commit");
            Assert.Equal(0, AH.Run(null, null, "modelo", "compactar", c.Model).Code);   // the transaction detail disappears
            var r2 = Exec(c);
            Assert.Equal(2, r2.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));                      // proven by the compacted count
            var st = AH.Read(c.State, out _, out _);
            Assert.Equal(OpState.RequiereVerificacion, st.Ops[c.SetOp].State);
            Assert.Equal(ErrorClass.EvidenciaInsuficiente, st.Ops[c.SetOp].Error);
            Assert.Equal("Satisfechas", Arg.GetString(st.Ops[c.SetOp].Evidence, "postcondiciones"));
            Assert.Null(st.Ops[c.SetOp].Result);                                          // no fabricated result
            Assert.Equal(1, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));
        }

        [Fact]
        public void AH01_TargetsChangedAfterCommit_PartialEffect_RequiresReconciliation()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "backend.tras_commit@" + c.SetOp), "backend.tras_commit");
            var m = DurableSimModel.Open(c.Model);   // someone adds a lighting fixture on L03 while the agent was down
            m.CommitExternal(new List<object[]>(), new List<object[]> { new object[] { m.NextElementId(), AH.Cat, new Dictionary<string, object> { ["nivel"] = "L03" } } });
            Assert.Equal(2, Exec(c).Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            var st = AH.Read(c.State, out _, out _);
            Assert.Equal(OpState.RequiereReconciliar, st.Ops[c.SetOp].State);
            Assert.Equal(ErrorClass.EfectoParcial, st.Ops[c.SetOp].Error);
        }

        [Fact]
        public void AH01_FailedVerificationAfterRecovery_IsFailure_NotSuccess()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "backend.tras_commit@" + c.SetOp), "backend.tras_commit");
            var m = DurableSimModel.Open(c.Model);   // another person changes an element's value
            var first = m.Targets(new Dictionary<string, object> { ["categoria"] = AH.Cat }).First();
            m.CommitExternal(new List<object[]> { new object[] { first, "Mounting Height", 2700L, 3000L } });
            Assert.Equal(2, Exec(c).Code);
            var st = AH.Read(c.State, out _, out _);
            Assert.Equal(OpState.Fallida, st.Ops[c.SetOp].State);
            Assert.Equal(ErrorClass.Verificacion, st.Ops[c.SetOp].Error);
            Assert.Contains("4/5", st.Ops[c.SetOp].Note);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
        }

        // ---------------- AH-02 ----------------

        [Fact]
        public void AH02_ModelAndStateSurviveRealProcessDeath_CompletesWithoutDuplicates()
        {
            var dir = AH.NewDir("ah02");
            var ops = Enumerable.Range(0, 20).Select(i => AH.Set("P" + i.ToString("00"), (long)(100 + i), level: "L03", expectElems: 5, expectChanged: 5)).ToArray();
            var plan = AH.Plan(ops);
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            var pf = AH.WritePlan(dir, plan);
            var mark = Path.Combine(dir, "caida.txt");
            var hash = AH.Hash(plan);

            var r1 = AH.Run("runner.entre_operaciones#9", mark, "ejecutar", pf, state, "--modelo", model, "--checkpoint", "5", "--lease-ms", CrashTestLeaseMs);
            _out.WriteLine(r1.ToString());
            Assert.True(r1.Killed, r1.ToString());
            var mid = AH.Read(state, out _, out _);
            Assert.Equal(8, mid.Ops.Values.Count(o => o.State == OpState.Verificada));
            // a new operating system process, with only what is on disk
            AH.ExpireDeadLease(state);
            var r2 = AH.Run(null, null, "ejecutar", "-", state, "--modelo", model);
            _out.WriteLine(r2.ToString());
            Assert.Equal(0, r2.Code);
            var fresh = DurableSimModel.Open(model);
            foreach (var op in plan.Ops)
            {
                Assert.Equal(1, fresh.TxFor(op.Id, hash).Count);
                Assert.All(fresh.Targets(new Dictionary<string, object> { ["categoria"] = AH.Cat }), id => Assert.True(Values.Same(op.Params["valor"], fresh.Get(id, (string)op.Params["parametro"]))));
            }
            Assert.Equal(20, AH.Read(state, out _, out _).Ops.Values.Count(o => o.State == OpState.Verificada));
        }

        [Fact]
        public void AH03_RestartDuringCheckpointWrite_RecoversFromJournal()
        {
            var c = Setup();
            AssertCrashedAt(c, Exec(c, "runner.en_checkpoint", "--checkpoint", "2"), "runner.en_checkpoint");
            Assert.True(Directory.GetFiles(c.State, "estado.json.tmp-*").Length == 1);
            var r2 = Exec(c);
            Assert.Equal(0, r2.Code);
            Assert.Contains("checkpoint_a_medias", r2.Out);
            Assert.Empty(Directory.GetFiles(c.State, "estado.json.tmp-*"));
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
        }

        // ---------------- AH-04 ----------------

        [Fact]
        public void AH04_LeaseExpiresBeforeFirstAction_FailsClosed_NothingWritten()
        {
            // AUD-04 / #28 reproduced deterministically: a controlled 1.5 s delay between initialization and the first action with
            // a 700 ms TTL. This is exactly what happened on the Windows runner: the lease expires before the first check.
            // Correct behavior: fail-closed (lease_perdido, code 4) BEFORE any transition or write.
            var c = Setup();
            var r = AH.RunWithEnv(new Dictionary<string, string> { ["BIMBRIDGE_AH_DELAY"] = "runner.antes_de_ejecutar:1500" }, null, null,
                "ejecutar", c.PlanFile, c.State, "--modelo", c.Model, "--lease-ms", "700");
            _out.WriteLine(r.ToString());
            Assert.Equal(4, r.Code);
            Assert.Contains("lease_perdido", r.Out);
            foreach (var op in c.Plan.Ops) Assert.Equal(0, AH.TxCount(c.Model, c.Hash, op.Id));     // no write
            Assert.Equal(0, AH.Transitions(c.State, c.SetOp, OpState.EnEjecucion));                  // it did not even start
            // No race: the next process (expired lease) takes the plan with a higher fencing token and completes it exactly once.
            var r2 = AH.Run(null, null, "ejecutar", c.PlanFile, c.State, "--modelo", c.Model, "--lease-ms", CrashTestLeaseMs);
            Assert.Equal(0, r2.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Equal(new long?[] { 1, 2 }, AH.Journal(c.State).Where(x => Arg.GetString(x, "ev") == "dueno").Select(x => Arg.GetLong(x, "f")).ToArray());
        }

        [Fact]
        public void AH04_SlowInitializationWithinTtl_IsRenewedBeforeFirstAction_AndCompletesOnce()
        {
            // Same delay, sufficient TTL: the explicit renewal after initialization keeps owner and fencing; no re-acquire.
            var c = Setup();
            var r = AH.RunWithEnv(new Dictionary<string, string> { ["BIMBRIDGE_AH_DELAY"] = "runner.antes_de_ejecutar:1500" }, null, null,
                "ejecutar", c.PlanFile, c.State, "--modelo", c.Model, "--lease-ms", "6000");
            _out.WriteLine(r.ToString());
            Assert.Equal(0, r.Code);
            Assert.Equal(1, AH.TxCount(c.Model, c.Hash, c.SetOp));
            Assert.Single(AH.Journal(c.State), x => Arg.GetString(x, "ev") == "dueno");             // a single owner, fencing 1
        }

        [Fact]
        public void AH04_TwoProcessesCompete_OnlyOneExecutes_NoDuplicates()
        {
            var dir = AH.NewDir("ah04");
            var plan = AH.Plan(Enumerable.Range(0, 15).Select(i => AH.Set("Q" + i.ToString("00"), (long)i, level: "L03")).ToArray());
            var model = AH.Model(dir, config: new Dictionary<string, object> { ["retardo_ms"] = 40L });
            var state = Path.Combine(dir, "estado");
            var pf = AH.WritePlan(dir, plan);
            var a = AH.Start(null, null, "ejecutar", pf, state, "--modelo", model);
            var b = AH.Start(null, null, "ejecutar", pf, state, "--modelo", model);
            var ra = a.Wait(); var rb = b.Wait();
            _out.WriteLine("A: " + ra + "\nB: " + rb);
            var codes = new[] { ra.Code, rb.Code }.OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 0, 3 }, codes);                    // one runs; the other only inspects
            var loser = ra.Code == 3 ? ra : rb;
            Assert.Contains("plan_en_uso", loser.Out);
            var fresh = DurableSimModel.Open(model);
            var hash = AH.Hash(plan);
            Assert.All(plan.Ops, op => Assert.Equal(1, fresh.TxFor(op.Id, hash).Count));
            Assert.Single(AH.Journal(state), r => Arg.GetString(r, "ev") == "dueno");
        }

        [Fact]
        public void AH04_LeaseExpiresAfterCrash_NextProcessTakesOverWithHigherFencing()
        {
            var dir = AH.NewDir("ah04b");
            var plan = AH.Plan(Enumerable.Range(0, 10).Select(i => AH.Set("R" + i, (long)i, level: "L03")).ToArray());
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            var pf = AH.WritePlan(dir, plan);
            var mark = Path.Combine(dir, "caida.txt");
            var r1 = AH.Run("runner.entre_operaciones#4", mark, "ejecutar", pf, state, "--modelo", model, "--lease-ms", "3600000");
            _out.WriteLine("Crash: " + r1);
            Assert.True(r1.Killed, r1.ToString());
            var holder = PlanLease.Peek(state);
            Assert.Equal(1, holder.Fencing);
            Assert.True(holder.Valid(DateTime.UtcNow));
            var r2 = AH.Run(null, null, "ejecutar", "-", state, "--modelo", model);
            _out.WriteLine("Current: " + r2);
            Assert.Equal(3, r2.Code);
            Assert.Contains("plan_en_uso", r2.Out);
            // Both processes have exited. We only change the expiry of this on-disk fixture.
            var path = Path.Combine(state, "control", "plan", "lease.json");
            var lease = Json.ParseObject(File.ReadAllText(path));
            lease["vence"] = "2000-01-01T00:00:00Z";
            File.WriteAllText(path, Json.Write(lease));
            var r3 = AH.Run(null, null, "ejecutar", "-", state, "--modelo", model);
            _out.WriteLine(r1 + "\n" + r2 + "\n" + r3);
            Assert.Equal(0, r3.Code);
            var owners = AH.Journal(state).Where(r => Arg.GetString(r, "ev") == "dueno").Select(r => Arg.GetLong(r, "f")).ToList();
            Assert.Equal(new long?[] { 1, 2 }, owners);
            var fresh = DurableSimModel.Open(model);
            var hash = AH.Hash(plan);
            Assert.All(plan.Ops, op => Assert.Equal(1, fresh.TxFor(op.Id, hash).Count));
            Assert.Equal(2, fresh.MaxFencing(hash));
        }

        /// <summary>rc1 · NRT-03 simulated with batches: the process dies after committing the batch (and in the middle of the "transaction"); on resume,
        /// the batch is applied ONCE, with each element's value, with nothing duplicated or lost.</summary>
        [Theory]
        [InlineData("backend.tras_commit")]
        [InlineData("backend.mitad_tx")]
        public void Rc1_BatchWrite_ProcessDeath_ResumesExactlyOnce(string point)
        {
            var dir = AH.NewDir("rc1-lote-proc");
            var model = AH.Model(dir, 4);
            List<long> ids;
            { var m0 = DurableSimModel.Open(model); ids = m0.Targets(new Dictionary<string, object> { ["categoria"] = AH.Cat }); }
            var w = new OpDraft { Operation = "fijar_parametro_lote" };
            w.Scope["categoria"] = AH.Cat; w.Scope["ids"] = ids.Cast<object>().ToList();
            w.Params["parametro"] = "Comments";
            w.Params["valores"] = ids.ToDictionary(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), i => (object)("V" + i));
            var plan = AH.Plan(w);
            var pf = AH.WritePlan(dir, plan);
            var state = Path.Combine(dir, "estado"); var mark = Path.Combine(dir, "caida.txt");
            var opId = plan.Ops[0].Id;
            var fault = point == "backend.mitad_tx" ? point + "@" + opId + ":2" : point + "@" + opId;
            var r1 = AH.Run(fault, mark, "ejecutar", pf, state, "--modelo", model, "--lease-ms", CrashTestLeaseMs);
            Assert.True(r1.Killed, r1.ToString());
            AH.ExpireDeadLease(state);
            var r2 = AH.Run(null, mark, "ejecutar", pf, state, "--modelo", model, "--lease-ms", CrashTestLeaseMs);
            Assert.True(r2.Code == 0, r2.ToString());
            Assert.Equal(1, AH.TxCount(model, AH.Hash(plan), opId));
            var st = AH.Read(state, out _, out _);
            Assert.Equal(OpState.Verificada, st.Ops[opId].State);
            var m = DurableSimModel.Open(model);
            Assert.All(ids, i => Assert.Equal("V" + i, m.Get(i, "Comments")));
        }
    }
}
