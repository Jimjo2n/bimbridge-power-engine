// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Bimbridge.AgentHost;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.7.0-alpha1 in process: IR v2, state validation before recovery (AH-03), exclusion and fencing (AH-04), empty set
    /// (AH-05), cancellation, repetition and document fingerprint. Each "restart" creates new objects from disk (model included).
    /// </summary>
    public class AgentHostAlphaTests
    {
        private static PlanRunner Open(string state, PlanDraft plan, string model, out List<Diagnostic> diags, Faults f = null, int checkpoint = 64, TimeSpan? ttl = null) =>
            PlanRunner.Open(state, plan, new SimulatedBackend(DurableSimModel.Open(model), f), new RunnerOptions { Faults = f, CheckpointEvery = checkpoint, LeaseTtl = ttl }, out diags);

        private static (string State, string Model, PlanDraft Plan) RunToEnd(PlanDraft plan = null, int fixtures = 5)
        {
            var dir = AH.NewDir("alpha");
            plan = plan ?? AH.BasePlan();
            var model = AH.Model(dir, fixtures);
            var state = Path.Combine(dir, "status");
            using (var r = Open(state, plan, model, out var d)) { Assert.NotNull(r); r.Run(); }
            return (state, model, plan);
        }

        // ---------------- IR v2 ----------------

        [Fact]
        public void IR_IdIncludesDocumentFingerprintAndSource_AndIsKeyOrderIndependent()
        {
            var sc1 = new Dictionary<string, object> { ["category"] = "X", ["level"] = "L3" };
            var sc2 = new Dictionary<string, object> { ["level"] = "L3", ["category"] = "X" };
            var p1 = new Dictionary<string, object> { ["value"] = 1L, ["parameter"] = "A" };
            var p2 = new Dictionary<string, object> { ["parameter"] = "A", ["value"] = 1.0 };
            var a = IrIds.OpId(AH.Doc, AH.Print, sc1, "set_parameter", p1, "s|REQ-1|3");
            Assert.Equal(a, IrIds.OpId(AH.Doc, AH.Print, sc2, "set_parameter", p2, "s|REQ-1|3"));
            Assert.NotEqual(a, IrIds.OpId(AH.Doc, "huella-de-un-modelo-sustituido", sc1, "set_parameter", p1, "s|REQ-1|3"));
            Assert.NotEqual(a, IrIds.OpId(AH.Doc, AH.Print, sc1, "set_parameter", p1, "s|REQ-2|3"));
        }

        [Fact]
        public void IR_ValidatedPlanIsImmutable_AndSurvivesJsonRoundTrip()
        {
            var p = AH.BasePlan();
            Assert.Empty(IrValidator.Validate(PlanDraft.Parse(Json.Write(p.ToJson())), out var vp));
            Assert.Equal(AH.Hash(p), vp.Hash);
            var op = vp.Ops[1];
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)op.Params)["value"] = 1L);
            Assert.Equal(new[] { "model_write" }, op.Authorization);          // derived, not declared
            Assert.Equal("convergent", op.Idempotency);
            Assert.NotEmpty(op.Postconditions);
            // mutating the draft after validation does not change the validated plan
            p.Ops[1].Params["value"] = 9999L;
            Assert.Equal(2700L, vp.Ops[1].Params["value"]);
        }

        [Fact]
        public void IR_RejectsAgentDeclaredSystemFields_ImplicitUnits_DuplicatesAndBadSources()
        {
            var p = AH.BasePlan();
            var json = p.ToJson();
            json["authorization"] = new List<object> { "all" };
            json["coordinates"] = null;
            var ops = (List<object>)json["operations"];
            var set = (Dictionary<string, object>)ops[1];
            set["status"] = "verified";                    // the agent does not set states
            set["authorization"] = new List<object> { "revit_modify" };
            ((Dictionary<string, object>)set["parameters"]).Remove("units");
            set["source"] = new Dictionary<string, object> { ["source"] = "no-declarada", ["requirement"] = "REQ-9" };
            ops.Add(new Dictionary<string, object>(set) { ["id"] = "op-copia" });   // semantic duplicate
            var errs = IrValidator.Validate(PlanDraft.FromJson(json));
            Assert.Contains(errs, e => e.Contains("non_declarable_field: plan.authorization"));
            Assert.Contains(errs, e => e.Contains("non_declarable_field status"));
            Assert.Contains(errs, e => e.Contains("non_declarable_field authorization"));
            Assert.Contains(errs, e => e.Contains("explicit coordinates"));
            Assert.Contains(errs, e => e.Contains("implicit_units"));
            Assert.Contains(errs, e => e.Contains("not declared in the plan"));
            Assert.Contains(errs, e => e.Contains("semantic_duplicate"));
            Assert.Contains(errs, e => e.Contains("nondeterministic_id"));
        }

        [Fact]
        public void IR_DependencyCyclesAreRejected_WithTheirPath()
        {
            var p = AH.Plan(AH.Set("A", 1L), AH.Set("B", 2L, deps: new[] { "#0" }), AH.Set("C", 3L, deps: new[] { "#1" }));
            p.Ops[0].DependsOn.Add(p.Ops[2].Id);   // A → C → B → A
            var errs = IrValidator.Validate(p);
            Assert.Contains(errs, e => e.StartsWith("cycle:") && e.Contains(p.Ops[0].Id) && e.Contains(p.Ops[2].Id));
        }

        [Fact]
        public void IR_OldSchemaVersion1_IsRejectedWithoutBestEffort()
        {
            var json = AH.BasePlan().ToJson();
            json["ir"] = 1L;
            Assert.Contains(IrValidator.Validate(PlanDraft.FromJson(json)), e => e.StartsWith("unsupported_version"));
        }

        // ---------------- document fingerprint ----------------

        [Fact]
        public void DocumentFingerprintMismatch_RefusesBeforeAnyExecution()
        {
            var dir = AH.NewDir("fingerprint");
            var m = Path.Combine(dir, "model");
            DurableSimModel.Create(m, AH.Doc, "huella-OTRO-modelo", new[] { AH.Cat + "=5@L03" });
            var r = Open(Path.Combine(dir, "status"), AH.BasePlan(), m, out var diags);
            Assert.Null(r);
            Assert.Contains(diags, d => d.Code == ErrorClass.DifferentDocument);
            Assert.False(File.Exists(Path.Combine(dir, "status", "journal.jsonl")));
        }

        // ---------------- AH-03 ----------------

        private static void AssertRefused(string state, PlanDraft plan, string model, string code)
        {
            var r = Open(state, plan, model, out var diags);
            Assert.True(r == null, "recovery should have been refused; diags: " + string.Join("; ", diags));
            Assert.Contains(diags, d => d.Severity == "error" && d.Code == code);
            Assert.Null(PlanLease.Peek(state) is var l && l != null && !l.Released ? "lease held" : null);
        }

        [Fact]
        public void AH03_TornLastJournalLine_IsTrimmed_AndRunContinues()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "journal.jsonl");
            File.AppendAllText(j, "{\"ev\":\"transition\",\"op\":\"op-x\",\"de\":\"valid");   // crash in the middle of a write
            using (var r = Open(state, plan, model, out var diags))
            {
                Assert.NotNull(r);
                Assert.Contains(diags, d => d.Code == "journal_truncated_last_line");
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verified, o.State));
            }
            Assert.EndsWith("\n", File.ReadAllText(j));
        }

        [Fact]
        public void AH03_CorruptedMiddleRecord_RefusesRecovery()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "journal.jsonl");
            var lines = File.ReadAllLines(j);
            var i = Array.FindIndex(lines, l => l.Contains("\"a\":\"verified\""));
            lines[i] = lines[i].Replace("\"a\":\"verified\"", "\"a\":\"failed\"");
            File.WriteAllLines(j, lines);
            AssertRefused(state, plan, model, "journal_invalid_hash");
        }

        [Fact]
        public void AH03_TamperedCheckpoint_IsDiscarded_AndRebuiltFromJournal()
        {
            var (state, model, plan) = RunToEnd();
            var cp = Path.Combine(state, "state.json");
            File.WriteAllText(cp, File.ReadAllText(cp).Replace("\"verified\"", "\"validated\""));
            using (var r = Open(state, plan, model, out var diags))
            {
                Assert.NotNull(r);
                Assert.Contains(diags, d => d.Code == "checkpoint_tampered");
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verified, o.State));
            }
        }

        /// <summary>A checkpoint with a VALID seal that certifies something the journal does not say: it is rejected, not trusted.</summary>
        [Fact]
        public void AH03_ForgedCheckpointClaimingCompletion_IsRejected()
        {
            var dir = AH.NewDir("forge");
            var plan = AH.BasePlan();
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "status");
            using (var r = Open(state, plan, model, out _, new Faults().Configure("runner.tras_ejecutar@" + plan.Ops[1].Id, "exception")))
                Assert.Throws<CrashException>(() => r.Run());
            var cpPath = Path.Combine(state, "state.json");
            var cp = Json.ParseObject(File.ReadAllText(cpPath));
            var ops = (Dictionary<string, object>)cp["ops"];
            foreach (var k in ops.Keys.ToList()) ((Dictionary<string, object>)ops[k])["status"] = "verified";
            cp.Remove("seal");
            cp["seal"] = Home.Sha256Hex(OpSemantics.Canonical(cp), 32);
            File.WriteAllText(cpPath, Json.Write(cp));
            AssertRefused(state, plan, model, "checkpoint_contradicts_journal");
        }

        [Fact]
        public void AH03_CheckpointAheadOfJournal_IsRejected()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "journal.jsonl");
            var lines = File.ReadAllLines(j);
            File.WriteAllLines(j, lines.Take(lines.Length - 3));   // committed records are lost
            AssertRefused(state, plan, model, "checkpoint_ahead");
        }

        [Fact]
        public void AH03_MissingJournalWithCheckpoint_IsRejected()
        {
            var (state, model, plan) = RunToEnd();
            File.Delete(Path.Combine(state, "journal.jsonl"));
            AssertRefused(state, plan, model, "journal_missing");
        }

        private static void Forge(string state, Dictionary<string, object> line)
        {
            var log = ChainLog.Open(Path.Combine(state, "journal.jsonl"), false, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "journal");
            log.Append(line);
            File.Delete(Path.Combine(state, "state.json"));
        }

        [Fact]
        public void AH03_IllegalTransition_UnknownOperation_UnknownState_AreRejected()
        {
            var dir = AH.NewDir("illegal");
            var plan = AH.Plan(AH.Set("A", 1L), AH.Set("B", 2L));
            var model = AH.Model(dir);
            var cases = new[]
            {
                (new Dictionary<string, object> { ["ev"] = "transition", ["op"] = plan.Ops[0].Id, ["de"] = "validated", ["a"] = "verified" }, "illegal_transition"),
                (new Dictionary<string, object> { ["ev"] = "transition", ["op"] = "op-inventada", ["de"] = "validated", ["a"] = "executing" }, "unknown_operation"),
                (new Dictionary<string, object> { ["ev"] = "transition", ["op"] = plan.Ops[0].Id, ["de"] = "validated", ["a"] = "approved_by_me" }, "unknown_status"),
                (new Dictionary<string, object> { ["ev"] = "transition", ["op"] = plan.Ops[0].Id, ["de"] = "executed", ["a"] = "verified" }, "inconsistent_transition"),
                (new Dictionary<string, object> { ["ev"] = "transition", ["f"] = 0L, ["op"] = plan.Ops[0].Id, ["de"] = "validated", ["a"] = "executing" }, "stale_writer"),
            };
            foreach (var (line, code) in cases)
            {
                var state = Path.Combine(dir, "estado-" + code);
                using (var r = Open(state, plan, model, out _, ttl: TimeSpan.FromMilliseconds(1))) Assert.NotNull(r);   // open + owner f1
                if (!line.ContainsKey("f")) line["f"] = 1L;
                Forge(state, line);
                Thread.Sleep(5);
                AssertRefused(state, plan, model, code);
            }
        }

        [Fact]
        public void AH03_PlanFileTampered_OrDifferentPlan_IsRejected()
        {
            var (state, model, plan) = RunToEnd();
            var other = AH.Plan(AH.Set("Otro", 1L));
            var r = Open(state, other, model, out var d1);
            Assert.Null(r);
            Assert.Contains(d1, d => d.Code == "plan_different");
            var pf = Path.Combine(state, "plan.json");
            File.WriteAllText(pf, File.ReadAllText(pf).Replace("2700", "2701"));
            Assert.Null(Open(state, null, model, out var d2));
            Assert.Contains(d2, d => d.Code == "saved_plan_invalid" || d.Code == "plan_tampered");
        }

        [Fact]
        public void AH03_JournalOfAnotherSchemaVersion_IsRejected()
        {
            var dir = AH.NewDir("schema");
            var plan = AH.BasePlan();
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "status");
            Directory.CreateDirectory(state);
            Assert.True(IrValidator.Validate(plan, out var vp).Count == 0);
            PlanFile.Write(state, vp);
            var log = ChainLog.Open(Path.Combine(state, "journal.jsonl"), false, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "journal");
            log.Append(new Dictionary<string, object> { ["ev"] = "opening", ["plan"] = vp.Hash, ["schema"] = 99L });
            AssertRefused(state, plan, model, "journal_schema_not_supported");
        }

        // ---------------- AH-04 (in process: zombie) ----------------

        [Fact]
        public void AH04_ZombieOwnerAfterTakeover_CannotWriteModelOrJournal()
        {
            var dir = AH.NewDir("zombie");
            var plan = AH.Plan(AH.Set("A", 1L, level: "L03"), AH.Set("B", 2L, level: "L03"));
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "status");
            var zombie = Open(state, plan, model, out _, ttl: TimeSpan.FromMilliseconds(150));
            Assert.True(zombie.IsOwner);
            Thread.Sleep(250);                                                   // the zombie stops renewing
            using (var owner = Open(state, plan, model, out _, ttl: TimeSpan.FromSeconds(30)))
            {
                Assert.True(owner.IsOwner);
                Assert.Equal(2, owner.State.Fencing);
                Assert.Throws<PlanLease.LeaseLostException>(() => zombie.Run());  // its lease is no longer valid
                Assert.Equal(0, DurableSimModel.Open(model).TxFor(plan.Ops[0].Id, AH.Hash(plan)).Count);
                // and if it skipped the lease, the model rejects it by fencing
                owner.Run();
                var m = DurableSimModel.Open(model);   // model up to date: the only obstacle is fencing
                Assert.Throws<DurableSimModel.StaleFencingException>(() => m.Commit("op-zombi", AH.Hash(plan), 1, 0, "", new List<object[]>()));
            }
            Assert.All(plan.Ops, op => Assert.Equal(1, DurableSimModel.Open(model).TxFor(op.Id, AH.Hash(plan)).Count));
        }

        // ---------------- AH-05 ----------------

        [Fact]
        public void AH05_EmptyTargetSet_WriteFailsWithDiagnostic_NothingWritten()
        {
            var plan = AH.Plan(AH.Set("Mounting Height", 2700L, level: "L99"), AH.Qc("#0"));
            var (state, model, _) = RunToEnd(plan);
            var st = AH.Read(state, out _, out _);
            Assert.Equal(OpState.Failed, st.Ops[plan.Ops[0].Id].State);
            Assert.Equal(ErrorClass.NoTargets, st.Ops[plan.Ops[0].Id].Error);
            Assert.Equal(OpState.Blocked, st.Ops[plan.Ops[1].Id].State);
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
            Assert.Equal(0, AH.Transitions(state, plan.Ops[0].Id, OpState.Running));
        }

        [Fact]
        public void AH05_EmptyTargetSet_ExplicitlyAllowed_IsVerifiedAsEmpty_NotAsModification()
        {
            var plan = AH.Plan(AH.Set("Mounting Height", 2700L, level: "L99", allowEmpty: true));
            var (state, model, _) = RunToEnd(plan);
            var rec = AH.Read(state, out _, out _).Ops[plan.Ops[0].Id];
            Assert.Equal(OpState.Verified, rec.State);
            Assert.Equal(true, rec.Result["empty_value"]);
            Assert.Equal(0L, Arg.GetLong(rec.Result, "elements"));
            Assert.Equal(0L, Arg.GetLong(rec.Evidence, "targets"));
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
        }

        [Fact]
        public void AH05_ObserveWithZeroTargets_IsDistinctFromApplied_AndBackendRefusesDirectly()
        {
            var dir = AH.NewDir("empty_value");
            var model = DurableSimModel.Open(AH.Model(dir));
            Assert.Empty(IrValidator.Validate(AH.Plan(AH.Set("X", 1L, level: "L99")), out var vp));
            var b = new SimulatedBackend(model);
            var ctx = new ExecContext { PlanHash = vp.Hash, Fencing = 1, Attempt = 1 };
            Assert.Equal(Effect.NoTargets, b.Observe(vp.Ops[0], ctx).Effect);                // never "applied" because 0 == 0
            var r = b.Execute(vp.Ops[0], ctx);                                                  // defense in depth
            Assert.False(r.Ok);
            Assert.Equal(ErrorClass.NoTargets, r.ErrorClass);
            Assert.Equal(PostState.NotSatisfied, b.CheckPostconditions(vp.Ops[0], ctx).State);
        }

        // ---------------- cancellation, pause, repetition, retries ----------------

        [Fact]
        public void Cancellation_StopsBetweenOperations_AndCancelMarksTheRest()
        {
            var dir = AH.NewDir("cancel");
            var plan = AH.Plan(Enumerable.Range(0, 10).Select(i => AH.Set("C" + i, (long)i, level: "L03")).ToArray());
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "status");
            var cts = new CancellationTokenSource();
            var f = new Faults();
            using (var r = Open(state, plan, model, out _, f))
            {
                var n = 0;
                // the token is cancelled after the 3rd operation: the runner finishes the current operation and stops between operations
                var first3 = plan.Ops.Select(o => o.Id).ToList();
                r.State.CheckpointEvery = 1000;
                var t = new Thread(() => { while (r.State.Ops.Values.Count(o => o.State == OpState.Verified) < 3) Thread.Sleep(1); cts.Cancel(); });
                t.Start();
                r.Run(cts.Token);
                t.Join();
                n = r.State.Ops.Values.Count(o => o.State == OpState.Verified);
                Assert.InRange(n, 3, 10);
                Assert.DoesNotContain(r.State.Ops.Values, o => o.State == OpState.Running || o.State == OpState.Executed);
                r.Cancel();
                var after = r.State.Ops.Values.Count(o => o.State == OpState.Cancelled);
                Assert.Equal(10 - n, after);
                Assert.Equal(PlanRunState.Cancelled, r.State.PlanState);
            }
            Assert.Equal(PlanRunState.Cancelled, AH.Read(state, out _, out _).PlanState);
        }

        [Fact]
        public void Pause_PersistsAcrossRestart_ResumeContinues()
        {
            var dir = AH.NewDir("pause");
            var plan = AH.Plan(AH.Set("A", 1L, level: "L03"), AH.Set("B", 2L, level: "L03"));
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "status");
            using (var r = Open(state, plan, model, out _)) { r.Pause(); Assert.Contains("paused", r.Run()); }
            using (var r = Open(state, plan, model, out _))
            {
                Assert.Equal(PlanRunState.Paused, r.State.PlanState);
                Assert.Contains("paused", r.Run());
                Assert.Equal(0, r.State.Ops.Values.Count(o => o.State == OpState.Verified));
                r.Resume();
                r.Run();
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verified, o.State));
            }
        }

        [Fact]
        public void RepeatedExecutionOfACompletedPlan_DoesNothing()
        {
            var (state, model, plan) = RunToEnd();
            var before = AH.Journal(state).Count(r => Arg.GetString(r, "ev") == "transition");
            using (var r = Open(state, plan, model, out _)) r.Run();
            using (var r = Open(state, plan, model, out _)) r.Run();
            Assert.Equal(before, AH.Journal(state).Count(r => Arg.GetString(r, "ev") == "transition"));
            Assert.All(plan.Ops.Where(o => o.Operation == "set_parameter"), op => Assert.Equal(1, AH.TxCount(model, AH.Hash(plan), op.Id)));
        }

        [Fact]
        public void TransientRejections_RetriedOnlyUpToPolicy_RejectionsAreNotEffects()
        {
            var dir = AH.NewDir("retries");
            var a = AH.Set("A", 1L, level: "L03"); a.Retries = 2;
            var b = AH.Set("B", 2L, level: "L03"); b.Retries = 1;
            var plan = AH.Plan(a, b);
            var m = Path.Combine(dir, "model");
            DurableSimModel.Create(m, AH.Doc, AH.Print, new[] { AH.Cat + "=5@L03" }, new Dictionary<string, object>
            {
                ["transient"] = new Dictionary<string, object> { [plan.Ops[0].Id] = 2L, [plan.Ops[1].Id] = 5L },
            });
            var state = Path.Combine(dir, "status");
            using (var r = Open(state, plan, m, out _)) r.Run();
            var st = AH.Read(state, out _, out _);
            Assert.Equal(OpState.Verified, st.Ops[plan.Ops[0].Id].State);
            Assert.Equal(3, st.Ops[plan.Ops[0].Id].Attempt);
            Assert.Equal(OpState.Failed, st.Ops[plan.Ops[1].Id].State);
            Assert.Equal(ErrorClass.Transient, st.Ops[plan.Ops[1].Id].Error);
            Assert.Equal(2, st.Ops[plan.Ops[1].Id].Attempt);
            Assert.Equal(1, AH.TxCount(m, AH.Hash(plan), plan.Ops[0].Id));
            Assert.Equal(0, AH.TxCount(m, AH.Hash(plan), plan.Ops[1].Id));
        }

        [Fact]
        public void PreconditionOnTargetCount_FailsBeforeWriting()
        {
            var op = AH.Set("A", 1L, level: "L03");
            op.Preconditions.Add(new Dictionary<string, object> { ["type"] = "targets", ["min"] = 6L });
            var plan = AH.Plan(op);
            var (state, model, _) = RunToEnd(plan);
            var rec = AH.Read(state, out _, out _).Ops[plan.Ops[0].Id];
            Assert.Equal(OpState.Failed, rec.State);
            Assert.Equal(ErrorClass.Precondition, rec.Error);
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
        }

        [Fact]
        public void RealModelBackend_WithWrites_IsRefusedInAlpha1()
        {
            var dir = AH.NewDir("revit");
            var r = PlanRunner.Open(Path.Combine(dir, "status"), AH.BasePlan(), new RevitTaskAdapter(AH.Doc, AH.Print), new RunnerOptions(), out var diags);
            Assert.Null(r);
            Assert.Contains(diags, d => d.Code == "writes_not_allowed");
        }

        // ---------------- durable model ----------------

        [Fact]
        public void Model_TornWalLine_IsNotCommitted_CorruptLineRefuses()
        {
            var dir = AH.NewDir("wal");
            var mdir = AH.Model(dir);
            var m = DurableSimModel.Open(mdir);
            var id = m.Targets(new Dictionary<string, object> { ["category"] = AH.Cat }).First();
            m.Commit("op-a", "plan", 1, 1, "h", new List<object[]> { new object[] { id, "P", null, 5L } });
            File.AppendAllText(Path.Combine(mdir, "model.1.wal"), "{\"tx\":\"tx-1-2\",\"op\":\"op-b\",\"camb");
            var m2 = DurableSimModel.Open(mdir);
            Assert.Equal(5L, m2.Get(id, "P"));
            Assert.Equal(0, m2.TxFor("op-b", "plan").Count);
            Assert.Contains(m2.OpenDiagnostics, d => d.Code == "wal_truncated_last_line");
            var wal = Path.Combine(mdir, "model.1.wal");
            File.WriteAllText(wal, File.ReadAllText(wal).Replace("5", "6"));
            Assert.Throws<DurableSimModel.ModelCorruptException>(() => DurableSimModel.Open(mdir));
        }
    }
}
