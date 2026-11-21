// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimbridge.Engine.Workflows;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.9.1 (Block F): truthful contracts for interactive native workflows (#23), Copy/Monitor verification (#24) and stable target and
    /// connection diagnostics (#25). All synthetic: no real model, no private path, no Revit.
    /// </summary>
    public class NativeWorkflowTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb_wf_" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static Evidence Ev(string kind, ProofLevel lvl = ProofLevel.observed, string detail = null, string source = null) =>
            new Evidence
            {
                Kind = kind, Level = lvl, Detail = detail, AtUtc = DateTime.UtcNow,
                Source = source ?? (WorkflowMachine.PrivilegedSources.TryGetValue(kind, out var s) ? s[0] : "client"),
            };

        private static WorkflowCorrelation Corr(string doc = "doc-A") => new WorkflowCorrelation { AgentId = "a1", SessionId = "s1", RequestId = "r1", TraceId = "t1", JobId = "j1", DocumentIdentity = doc, LinkIdentity = "link-1", RevitVersion = "2026" };

        private ReceiptStore Store() => new ReceiptStore(_dir);

        // ------------------------------------------------------------------ #23 receipts and states

        [Fact]
        public void PostCommandAccepted_WithoutFurtherProof_StaysCommandAccepted_NeverCompleted()
        {
            var st = Store();
            var key = WorkflowReceipt.KeyFor(NativeCapabilities.CopyMonitorFixtures, "doc-A", "link-1", "c1");
            st.Begin(NativeCapabilities.CopyMonitorFixtures, key, Corr(), out var created);
            Assert.True(created);
            var r = st.Record(key, Ev(EvidenceKind.PostCommandAccepted));
            Assert.Equal(WorkflowState.command_accepted, r.State);
            Assert.False(r.IsTerminal);
            Assert.NotEqual(ProofLevel.verified, r.Proof);
            // Jumping to "finished" or "verified" without the intermediate evidence is not possible.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.FinishObserved)));
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.verified)));
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.SaveConfirmed, ProofLevel.verified)));
            Assert.Equal(WorkflowState.command_accepted, st.Load(key).State);
        }

        [Fact]
        public void FullHappyPath_RequiresEvidenceAtEachStep_AndVerifiedLevelsAtTheEnd()
        {
            var st = Store();
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "c2");
            st.Begin("copy_monitor_fixtures", key, Corr(), out _);
            foreach (var k in new[] { EvidenceKind.PostCommandAccepted, EvidenceKind.CommandActive, EvidenceKind.LinkSelected, EvidenceKind.MappingDialog, EvidenceKind.CopyStarted, EvidenceKind.CopyObserved, EvidenceKind.FinishObserved })
                st.Record(key, Ev(k));
            Assert.Equal(WorkflowState.finished, st.Load(key).State);
            // Verification at level "observed" is not enough.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.observed)));
            st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.verified));
            var r = st.Record(key, Ev(EvidenceKind.SaveConfirmed, ProofLevel.verified));
            Assert.Equal(WorkflowState.save_confirmed, r.State);
            Assert.True(r.IsTerminal);
            Assert.Equal(ProofLevel.verified, r.Proof);
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.PostCommandAccepted)));
        }

        [Theory]
        [InlineData(1)]   // after accepting the command, before the batch copy
        [InlineData(5)]   // with the copy started
        [InlineData(6)]   // with the copy observed, before Finish
        public void FaultAtAnyStep_GoesToReconciliation_AndNeverAllowsABlindRePostCommand(int stepsBeforeFault)
        {
            var steps = new[] { EvidenceKind.PostCommandAccepted, EvidenceKind.CommandActive, EvidenceKind.LinkSelected, EvidenceKind.MappingDialog, EvidenceKind.CopyStarted, EvidenceKind.CopyObserved };
            var st = Store();
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "fault" + stepsBeforeFault);
            st.Begin("copy_monitor_fixtures", key, Corr(), out _);
            foreach (var k in steps.Take(stepsBeforeFault)) st.Record(key, Ev(k));
            var r = st.Record(key, Ev(EvidenceKind.Uncertain, ProofLevel.observed, "transporte perdido"));
            Assert.Equal(WorkflowState.reconciliation_required, r.State);
            Assert.False(WorkflowMachine.CanInvoke(r, out _));

            // Reopening the attempt (client retry / restart) returns the same receipt: the command is not duplicated.
            var again = new ReceiptStore(_dir).Begin("copy_monitor_fixtures", key, Corr(), out var created);
            Assert.False(created);
            Assert.Equal(WorkflowState.reconciliation_required, again.State);

            // A reconciliation cannot declare a save or a verification.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "save_confirmed")));
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "verification_passed")));
            if (stepsBeforeFault >= 5)
            {
                // With signs of a copy it does not go back to preflight (that would allow repeating the copy).
                Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "preflight")));
                Assert.Equal(WorkflowState.awaiting_finish, st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "awaiting_finish")).State);
            }
            else
            {
                var back = st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "preflight"));
                Assert.Equal(WorkflowState.preflight, back.State);
                Assert.True(WorkflowMachine.CanInvoke(back, out _));
            }
        }

        [Fact]
        public void CancelAfterCopy_IsNotACleanCancel()
        {
            var st = Store();
            var k1 = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "cancel-early");
            st.Begin("copy_monitor_fixtures", k1, Corr(), out _);
            st.Record(k1, Ev(EvidenceKind.PostCommandAccepted));
            Assert.Equal(WorkflowState.cancelled, st.Record(k1, Ev(EvidenceKind.Cancelled)).State);

            var k2 = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "cancel-late");
            st.Begin("copy_monitor_fixtures", k2, Corr(), out _);
            foreach (var k in new[] { EvidenceKind.PostCommandAccepted, EvidenceKind.CommandActive, EvidenceKind.LinkSelected, EvidenceKind.CopyStarted })
                st.Record(k2, Ev(k));
            Assert.Equal(WorkflowState.reconciliation_required, st.Record(k2, Ev(EvidenceKind.Cancelled)).State);
        }

        [Fact]
        public void AttemptKey_IsBoundToItsDocument_TargetSwitchIsRejected()
        {
            var st = Store();
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "c3");
            st.Begin("copy_monitor_fixtures", key, Corr("doc-A"), out _);
            Assert.Throws<InvalidOperationException>(() => st.Begin("copy_monitor_fixtures", key, Corr("doc-B"), out _));
            Assert.Throws<InvalidOperationException>(() => st.Begin("other_workflow", key, Corr("doc-A"), out _));
            Assert.NotEqual(key, WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-B", "link-1", "c3"));
            Assert.Throws<ArgumentException>(() => st.Load("../fuera"));
        }

        [Fact]
        public void NoUiExecutor_EndsInATruthfulManualHandoff_AndOpenListsOnlyUnfinished()
        {
            var st = Store();
            var k1 = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "manual");
            st.Begin("copy_monitor_fixtures", k1, Corr(), out _);
            Assert.Equal(WorkflowState.manual_handoff, st.Record(k1, Ev(EvidenceKind.NoUiExecutor)).State);
            var k2 = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "open");
            st.Begin("copy_monitor_fixtures", k2, Corr(), out _);
            File.WriteAllText(Path.Combine(_dir, "roto.json"), "{no es json");
            var open = st.Open(out var unreadable);
            // AUD-08: the manual handoff stays open, pending the later verification.
            Assert.Equal(new[] { k1, k2 }.OrderBy(x => x), open.Select(r => r.AttemptKey).OrderBy(x => x));
            Assert.Single(unreadable);
            var steps = NativeCapabilities.CopyMonitorManualSteps("MEP-LINK", new[] { "WC-A → WC-B" }, saveAuthorized: false);
            Assert.Contains(steps, s => s.Contains("Batch Copy"));
            Assert.Contains(steps, s => s.Contains("Do not save"));
        }

        [Fact]
        public void PrivilegedEvidence_OnlyFromAuthorizedComponents_AndVerificationContinuesAfterManualHandoff()
        {
            var st = Store();
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "priv");
            st.Begin("copy_monitor_fixtures", key, Corr(), out _);
            st.Record(key, Ev(EvidenceKind.PreflightPassed));
            st.Record(key, Ev(EvidenceKind.NoUiExecutor));
            // A client cannot declare the verification or the save.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.verified, source: "client")));
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.SaveConfirmed, ProofLevel.verified, source: "client")));
            // After the manual handoff (and a reconnection: another store instance), the verifier continues.
            var again = new ReceiptStore(_dir);
            Assert.Equal(WorkflowState.verification_failed, again.Record(key, Ev(EvidenceKind.VerificationFailed, ProofLevel.verified)).State);
            Assert.Equal(WorkflowState.verification_passed, again.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.verified)).State);
            Assert.Equal(WorkflowState.save_confirmed, again.Record(key, Ev(EvidenceKind.SaveConfirmed, ProofLevel.verified)).State);
        }

        [Fact]
        public void ConcurrentWriters_DoNotLoseTransitions_AndAHeldLockFailsClosed()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "conc");
            Store().Begin("copy_monitor_fixtures", key, Corr(), out _);
            // Two "processes" (independent stores) submit preflights at the same time: no transition is lost.
            var tasks = Enumerable.Range(0, 2).Select(_ => System.Threading.Tasks.Task.Run(() =>
            {
                var s = new ReceiptStore(_dir);
                for (var i = 0; i < 20; i++) s.Record(key, Ev(EvidenceKind.PreflightPassed));
            })).ToArray();
            System.Threading.Tasks.Task.WaitAll(tasks);
            Assert.Equal(40, Store().Load(key).History.Count);

            var held = new ReceiptStore(_dir) { LockWait = TimeSpan.FromMilliseconds(200) };
            using (new FileStream(Path.Combine(_dir, key + ".json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<TimeoutException>(() => held.Record(key, Ev(EvidenceKind.PreflightPassed)));
        }

        // ------------------------------------------------------------------ AUD-12 initial inventory per attempt

        [Fact]
        public void Baseline_IsWrittenOncePerAttempt_RepeatedPreflightNeverOverwritesIt()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "b1");
            var first = Store().SaveBaselineOnce(key, "doc-A", "link-1", new long[] { 3, 1, 2, 2 }, out var created);
            Assert.True(created);
            Assert.Equal(new long[] { 1, 2, 3 }, first.Ids);
            // The user copies in Revit (10 and 11 appear) and someone repeats the preflight: the original inventory is kept.
            var second = Store().SaveBaselineOnce(key, "doc-A", "link-1", new long[] { 1, 2, 3, 10, 11 }, out created);
            Assert.False(created);
            Assert.Equal(new long[] { 1, 2, 3 }, second.Ids);
            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(first.CreatedUtc, second.CreatedUtc);
            // Reconnection / restart: another store instance (another session) reads the same; it does not depend on session sets.
            var afterReconnect = new ReceiptStore(_dir).LoadBaseline(key);
            Assert.Equal(new long[] { 1, 2, 3 }, afterReconnect.Ids);
            // The inventory is not confused with an unreadable receipt.
            Store().Begin("copy_monitor_fixtures", key, Corr(), out _);
            Store().Open(out var unreadable);
            Assert.Empty(unreadable);
            Assert.Null(Store().LoadBaseline(WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "otra-clave")));
        }

        [Fact]
        public void Baseline_ConcurrentPreflights_ExactlyOneWins_AndEveryoneSeesTheSameEvidence()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "b2");
            const int n = 8;
            var results = new ReceiptStore.Baseline[n];
            var createdFlags = new bool[n];
            using (var start = new System.Threading.ManualResetEventSlim())
            {
                var threads = Enumerable.Range(0, n).Select(i => new System.Threading.Thread(() =>
                {
                    var store = new ReceiptStore(_dir);   // each "session/process" with its own store
                    start.Wait();
                    results[i] = store.SaveBaselineOnce(key, "doc-A", "link-1", new long[] { 100 + i, 200 + i }, out createdFlags[i]);
                })).ToList();
                threads.ForEach(t => t.Start());
                start.Set();
                threads.ForEach(t => t.Join());
            }
            Assert.Equal(1, createdFlags.Count(c => c));
            var winner = Array.IndexOf(createdFlags, true);
            Assert.Equal(new long[] { 100 + winner, 200 + winner }, results[winner].Ids);
            Assert.All(results, r => Assert.Equal(results[winner].Sha256, r.Sha256));
            Assert.Equal(results[winner].Ids, new ReceiptStore(_dir).LoadBaseline(key).Ids);
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Fact]
        public void Baseline_IsBoundToDocumentAndLink_AndTamperingIsDetected()
        {
            // Document change: the attempt key is already different per document, so each document has its own inventory...
            var keyA = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "b3");
            var keyB = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-B", "link-1", "b3");
            Assert.NotEqual(keyA, keyB);
            Store().SaveBaselineOnce(keyA, "doc-A", "link-1", new long[] { 1, 2 }, out _);
            Store().SaveBaselineOnce(keyB, "doc-B", "link-1", new long[] { 7 }, out var createdB);
            Assert.True(createdB);
            Assert.Equal(new long[] { 1, 2 }, Store().LoadBaseline(keyA).Ids);
            Assert.Equal(new long[] { 7 }, Store().LoadBaseline(keyB).Ids);
            // ...and if someone presents A's key with another document or another link, it is rejected (neither reused nor overwritten).
            Assert.Throws<InvalidOperationException>(() => Store().SaveBaselineOnce(keyA, "doc-B", "link-1", new long[] { 9 }, out _));
            Assert.Throws<InvalidOperationException>(() => Store().SaveBaselineOnce(keyA, "doc-A", "link-2", new long[] { 9 }, out _));
            Assert.Equal(new long[] { 1, 2 }, Store().LoadBaseline(keyA).Ids);

            // Tampering with the file (adding an id to "hide" a copy) invalidates the fingerprint: it is not used.
            var file = Path.Combine(_dir, keyA + ".inv");
            File.WriteAllText(file, File.ReadAllText(file).Replace("[1,2]", "[1,2,55]"));
            Assert.Throws<InvalidDataException>(() => Store().LoadBaseline(keyA));
            Assert.Throws<InvalidDataException>(() => Store().SaveBaselineOnce(keyA, "doc-A", "link-1", new long[] { 1, 2 }, out _));
            File.WriteAllText(file, "{no es json");
            Assert.Throws<InvalidDataException>(() => Store().LoadBaseline(keyA));
        }

        // ------------------------------------------------------------------ AUD-20 frozen expectations

        private static CopyMonitorExpectations Expect(List<SourceFixture> src = null, LinkTransform tf = null, List<TypeMapping> maps = null, string category = "PlumbingFixtures",
                                                      string host = "doc-A", string link = "link-1", string rev = "g1#3") =>
            new CopyMonitorExpectations { HostIdentity = host, LinkIdentity = link, LinkRevision = rev, Category = category, Transform = tf ?? LinkTransform.Identity, Mappings = maps ?? new List<TypeMapping>(), Source = src ?? Synthetic88() };

        [Fact]
        public void Preflight_FreezesTheWholeObjective_AndARepeatedPreflightCannotReplaceIt()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "e1");
            var maps = new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" }, new TypeMapping { Source = "Ducha: E", Excluded = true } };
            var first = CopyMonitorFlow.Freeze(Store(), key, Expect(maps: maps), new long[] { 1, 2 });
            Assert.True(first.Created); Assert.False(first.Conflict);
            Assert.Equal(88, first.Frozen.Source.Count);
            // Same target again (another session, reconnection): it is kept, with no differences.
            var again = CopyMonitorFlow.Freeze(new ReceiptStore(_dir), key, Expect(maps: maps.AsEnumerable().Reverse().ToList()), new long[] { 1, 2, 99 });
            Assert.False(again.Created); Assert.Empty(again.Differences);
            Assert.Equal(first.Frozen.Fingerprint, again.Frozen.Fingerprint);
            Assert.Equal(new long[] { 1, 2 }, again.Baseline.Ids);
            // A preflight with ANOTHER target does not replace it: it returns the frozen one and the conflict.
            var other = CopyMonitorFlow.Freeze(Store(), key, Expect(), new long[] { 1, 2 });
            Assert.True(other.Conflict);
            Assert.Equal(first.Frozen.Fingerprint, other.Frozen.Fingerprint);
            Assert.Contains(other.Differences, x => x.StartsWith("different type mapping"));
            Assert.Contains(other.Differences, x => x.StartsWith("different exclusions"));
        }

        [Fact]
        public void Verification_UsesFrozenExpectations_AndAnyChangeToTheObjectiveIsAConflict()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "e2");
            var maps = new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" } };
            CopyMonitorFlow.Freeze(Store(), key, Expect(maps: maps), new long[0]);
            // Verify without repeating the mapping: the frozen one is used (it is not a change). After reconnecting.
            var ok = CopyMonitorFlow.Load(new ReceiptStore(_dir), key, Expect(), mappingGiven: false);
            Assert.False(ok.Conflict);
            Assert.Single(ok.Frozen.Mappings);
            // Another saved revision of the link with no changes to the fixtures: reported, not blocking.
            var saved = CopyMonitorFlow.Load(Store(), key, Expect(maps: maps, rev: "g2#4"), true);
            Assert.False(saved.Conflict); Assert.Single(saved.Differences);

            // Each condition that alters the target -> conflict, and the original expectations stay intact.
            var moved = Synthetic88(); moved[5].X += 2;
            var fewer = Synthetic88(); fewer.RemoveAt(0);
            var retyped = Synthetic88(); retyped[3].Type = "Z";
            foreach (var changed in new[]
            {
                Expect(moved, maps: maps), Expect(fewer, maps: maps), Expect(retyped, maps: maps),
                Expect(maps: maps, tf: new LinkTransform { Dx = 10 }),
                Expect(maps: maps, tf: new LinkTransform { BasisX = new[] { -1.0, 0, 0 }, BasisY = new[] { 0.0, 1, 0 }, BasisZ = new[] { 0.0, 0, 1 } }),
                Expect(maps: maps, category: "LightingFixtures"),
                Expect(maps: new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: C" } }),
                Expect(maps: maps.Concat(new[] { new TypeMapping { Source = "Ducha: E", Excluded = true } }).ToList()),
            })
            {
                var r = CopyMonitorFlow.Load(Store(), key, changed, true);
                Assert.True(r.Conflict, string.Join("; ", r.Differences));
                Assert.Equal(ok.Frozen.Fingerprint, r.Frozen.Fingerprint);
            }
            Assert.Contains(CopyMonitorFlow.Load(Store(), key, Expect(fewer, maps: maps), true).Differences, x => x.Contains("+0 −1 modified 0"));

            // Without a preflight there is nothing to verify; an old attempt without expectations is not accepted.
            Assert.Null(CopyMonitorFlow.Load(Store(), WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "nothing"), Expect(), true));
            var legacy = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "old");
            Store().SaveBaselineOnce(legacy, "doc-A", "link-1", new long[] { 1 }, out _);
            Assert.Throws<InvalidDataException>(() => CopyMonitorFlow.Load(Store(), legacy, Expect(), true));
        }

        [Fact]
        public void FrozenExpectations_AreTamperEvident_AndRoundTrip()
        {
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "e3");
            var e = Expect(tf: new LinkTransform { Dx = 100, Dy = -20, RotationDeg = 30 }, maps: new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" } });
            CopyMonitorFlow.Freeze(Store(), key, e, new long[] { 7 });
            var back = CopyMonitorExpectations.FromText(e.ToText());
            Assert.Equal(e.Fingerprint, back.Fingerprint);
            Assert.Empty(e.Diff(back));
            // Changing an expectation in the file (removing an expected fixture) invalidates the inventory fingerprint: it is not used.
            var file = Path.Combine(_dir, key + ".inv");
            File.WriteAllText(file, File.ReadAllText(file).Replace("S87", "S86x"));
            Assert.Throws<InvalidDataException>(() => CopyMonitorFlow.Load(Store(), key, e, true));
            Assert.Throws<InvalidDataException>(() => CopyMonitorExpectations.FromText(e.ToText().Replace("\"WC: A\"", "\"WC: C\"")));
        }

        [Fact]
        public void LegacyBaseline_WithSpanishKeys_IsStillReadable()
        {
            // A baseline frozen by 0.9.4: Spanish keys in the .inv and in the expectations text it carries (hash over that text).
            var key = WorkflowReceipt.KeyFor("copy_monitor_fixtures", "doc-A", "link-1", "predeterminada");
            var e = Expect(maps: new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" } });
            var text = e.ToText();
            foreach (var kv in new Dictionary<string, string> { ["schema"] = "esquema", ["host"] = "anfitrion", ["link"] = "vinculo", ["link_revision"] = "revision_vinculo",
                         ["category"] = "categoria", ["transformation"] = "transformacion", ["mapping"] = "mapeo", ["origin"] = "origen", ["target"] = "destino",
                         ["excluded"] = "excluido", ["origin_fingerprint"] = "huella_origen", ["fingerprint"] = "huella" })
                text = text.Replace("\"" + kv.Key + "\":", "\"" + kv.Value + "\":");
            Assert.DoesNotContain("\"schema\"", text);
            var created = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, key + ".inv"), Bimbridge.Protocol.Json.Write(new Dictionary<string, object>
            {
                ["esquema"] = ReceiptStore.Baseline.Schema, ["clave_intento"] = key, ["documento"] = "doc-A", ["vinculo"] = "link-1", ["ids"] = new List<object> { 7L },
                ["sha256"] = ReceiptStore.Baseline.Hash(key, "doc-A", "link-1", new long[] { 7 }, text), ["creado"] = created, ["expectativas"] = text,
            }));
            var r = CopyMonitorFlow.Load(Store(), key, e, true);
            Assert.False(r.Conflict, string.Join("; ", r.Differences));
            Assert.Equal(e.Fingerprint, r.Frozen.Fingerprint);
            Assert.Equal(new long[] { 7 }, r.Baseline.Ids);
        }

        // ------------------------------------------------------------------ #23 capability matrix

        [Theory]
        [InlineData(2022)] [InlineData(2023)] [InlineData(2024)] [InlineData(2025)] [InlineData(2026)] [InlineData(2027)]
        public void CopyMonitor_HasNoDirectApi_AndUiAssistedDependsOnTheRealClient(int year)
        {
            var noUi = NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, year, new ClientEnvironment());
            Assert.Equal(SupportLevel.unavailable, noUi.LevelOf(ExecMode.direct_api));
            Assert.Equal(SupportLevel.unavailable, noUi.LevelOf(ExecMode.ui_assisted));
            Assert.Equal(ExecMode.manual_handoff, noUi.Recommended);
            Assert.DoesNotContain(noUi.Modes, m => m.Level == SupportLevel.proven);
            var ui = NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, year, new ClientEnvironment { UiExecutorAvailable = true, UiExecutorName = "x" });
            Assert.Equal(SupportLevel.experimental, ui.LevelOf(ExecMode.ui_assisted));
            Assert.Equal(ExecMode.ui_assisted, ui.Recommended);
            Assert.Equal("bimbridge.native_capability/1", ui.ToJson()["schema"]);
        }

        [Fact]
        public void Capabilities_RejectUnknownOperationsAndVersions()
        {
            Assert.Throws<ArgumentException>(() => NativeCapabilities.Describe("batch_magic", 2026, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, 2021, null));
        }

        // ------------------------------------------------------------------ #25 stable target and diagnostics

        private static RevitInstance Inst(int pid, string ver, DateTime start, params (string h, string s, string id)[] docs) => new RevitInstance
        {
            Pid = pid, Version = ver, StartedUtc = start,
            Documents = docs.Select(d => new RevitDocumentRef { Handle = d.h, SessionId = d.s, Identity = d.id }).ToList(),
        };

        [Fact]
        public void TwoRevitInstances_RequireAnExplicitTarget_AndStaleHandlesCannotWrite()
        {
            var t0 = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
            var list = new List<RevitInstance> { Inst(100, "2024", t0, ("d1", "s1", "X")), Inst(200, "2026", t0, ("d1", "s1", "Y"), ("d2", "s1", "Z")) };
            Assert.Null(TargetResolver.Resolve(list, "s1", null, null, "d1", out var why));
            Assert.Contains("2 Revit instances", why);
            Assert.Null(TargetResolver.Resolve(list, "s1", "2026", null, null, out why));
            Assert.Contains("documents are open", why);
            Assert.Null(TargetResolver.Resolve(list, "s2", "2026", null, "d2", out why));
            Assert.Contains("another session", why);
            var b = TargetResolver.Resolve(list, "s1", "2026", null, "d2", out why);
            Assert.NotNull(b);
            Assert.Equal("Z", b.DocumentIdentity);
            Assert.True(TargetResolver.StillValid(b, list, out _));

            // Revit 2026 restarts and the OS reuses pid 200: the target is no longer the same.
            var restarted = new List<RevitInstance> { list[0], Inst(200, "2026", t0.AddMinutes(30), ("d1", "s1", "Z")) };
            Assert.False(TargetResolver.StillValid(b, restarted, out why));
            Assert.Contains("pid 200", why);
            // The document is closed.
            var closed = new List<RevitInstance> { list[0], Inst(200, "2026", t0, ("d1", "s1", "Y")) };
            Assert.False(TargetResolver.StillValid(b, closed, out why));
        }

        [Fact]
        public void Diagnostics_SeparateClientRegistrationFromRevitAvailability()
        {
            var healthyBridgeHiddenTools = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, ToolsVisibleToAgent = 0, Instances = new List<RevitInstance>() };
            var r = ConnectionDiagnostics.Report(healthyBridgeHiddenTools, "0.9.1+abc");
            Assert.Equal("tools_not_exposed_to_agent", r["verdict"]);
            Assert.Equal("0.9.1+abc", r["build"]);

            var noClient = new ConnectionObservations { BridgeInstalled = true };
            noClient.ClientRegistrations["claude"] = false;
            Assert.Equal("client_unregistered", ConnectionDiagnostics.Report(noClient, "b")["verdict"]);

            var noRevit = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, ToolsVisibleToAgent = 6, Instances = new List<RevitInstance>() };
            Assert.Equal("revit_not_connected", ConnectionDiagnostics.Report(noRevit, "b")["verdict"]);

            var t = DateTime.UtcNow;
            var two = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, Instances = new List<RevitInstance> { Inst(1, "2024", t), Inst(2, "2026", t) } };
            var rep = ConnectionDiagnostics.Report(two, "b");
            Assert.Equal("multiple_instances", rep["verdict"]);
            Assert.Contains(((List<object>)rep["findings"]).Cast<Dictionary<string, object>>(), f => (string)f["code"] == "no_ui_executor");
        }

        // ------------------------------------------------------------------ #24 Copy/Monitor verifier (synthetic fixtures)

        /// <summary>Redistributable synthetic fixture: 88 fixtures in 5 types (40/2/20/20/6), regular grid, varied rotations.</summary>
        private static List<SourceFixture> Synthetic88()
        {
            var types = new[] { ("WC", "A", 40), ("WC", "B", 2), ("Lavabo", "C", 20), ("Urinario", "D", 20), ("Ducha", "E", 6) };
            var l = new List<SourceFixture>();
            var n = 0;
            foreach (var (f, t, count) in types)
                for (var i = 0; i < count; i++, n++)
                    l.Add(new SourceFixture { UniqueId = "S" + n, Category = "PlumbingFixtures", Family = f, Type = t, Level = "L1", X = (n % 10) * 6.0, Y = (n / 10) * 8.0, Z = 0, RotationDeg = (n % 4) * 90 });
            return l;
        }

        private static List<HostFixture> PerfectCopy(List<SourceFixture> src, LinkTransform tf, long link, Func<SourceFixture, string> type = null)
        {
            long id = 5000;
            return src.Select(s =>
            {
                var p = tf.Apply(s.X, s.Y, s.Z);
                var k = type?.Invoke(s) ?? (s.Family + ": " + s.Type);
                var parts = k.Split(new[] { ": " }, StringSplitOptions.None);
                return new HostFixture { Id = id++, Category = s.Category, Family = parts[0], Type = parts[1], Level = s.Level, X = p.X, Y = p.Y, Z = p.Z, RotationDeg = tf.RotateAngle(s.RotationDeg), Monitoring = true, MonitoredLinkInstances = new List<long> { link } };
            }).ToList();
        }

        [Fact]
        public void Synthetic88_PerfectMonitoredCopy_Verifies_EvenWithARotatedLink()
        {
            var src = Synthetic88();
            Assert.Equal(new[] { 40, 2, 20, 20, 6 }, src.GroupBy(s => s.Family + s.Type).Select(g => g.Count()).ToArray());
            foreach (var tf in new[] { LinkTransform.Identity, new LinkTransform { Dx = 100, Dy = -20, Dz = 0.5, RotationDeg = 30 } })
            {
                var r = CopyMonitorVerifier.Verify(src, new List<HostFixture>(), PerfectCopy(src, tf, 77), tf, null, 77);
                Assert.Equal("verified", r["result"]);
                Assert.Equal(88L, ((Dictionary<string, object>)r["correct"])["n"]);
                Assert.Equal("inferred", r["correspondence_test_level"]);
            }
        }

        [Fact]
        public void MirroredLink_IsHandled_AndUnrepresentableTransformsAreRejected()
        {
            // AUD-11: reflection X -> -X with translation.
            var src = Synthetic88();
            var mirror = new LinkTransform { Dx = 10, Dy = 5, BasisX = new[] { -1.0, 0, 0 }, BasisY = new[] { 0.0, 1, 0 }, BasisZ = new[] { 0.0, 0, 1 } };
            Assert.True(mirror.Mirrored);
            var r = CopyMonitorVerifier.Verify(src, null, PerfectCopy(src, mirror, 77), mirror, null, 77);
            Assert.Equal("verified", r["result"]);
            Assert.Equal(true, r["mirrored_link"]);
            // Copied as if there were no reflection: wrong positions.
            var naive = PerfectCopy(src, new LinkTransform { Dx = 10, Dy = 5 }, 77);
            Assert.Equal("failed", CopyMonitorVerifier.Verify(src, null, naive, mirror, null, 77)["result"]);

            var tilted = new LinkTransform { BasisX = new[] { 1.0, 0, 0 }, BasisY = new[] { 0.0, Math.Cos(0.3), Math.Sin(0.3) }, BasisZ = new[] { 0.0, -Math.Sin(0.3), Math.Cos(0.3) } };
            Assert.Equal("not_verifiable", CopyMonitorVerifier.Verify(src, null, new List<HostFixture>(), tilted, null, 77)["result"]);
            var scaled = new LinkTransform { BasisX = new[] { 2.0, 0, 0 }, BasisY = new[] { 0.0, 1, 0 }, BasisZ = new[] { 0.0, 0, 1 } };
            Assert.Contains("scale", (string)CopyMonitorVerifier.Verify(src, null, new List<HostFixture>(), scaled, null, 77)["reason"]);
        }

        [Fact]
        public void Report_SeparatesGeometryMonitoringAndIndividualCorrespondence()
        {
            var src = Synthetic88();
            var host = PerfectCopy(src, LinkTransform.Identity, 77);
            host[0].Monitoring = false;
            var d = (Dictionary<string, object>)CopyMonitorVerifier.Verify(src, null, host, null, null, 77)["dimensions_list"];
            Assert.Equal(88L, d["geometric_consistency"]);
            Assert.Equal(87L, d["link_supervision"]);
            Assert.StartsWith("not_demonstrated", (string)d["individual_origin_correspondence"]);
        }

        [Fact]
        public void PlainCopyWithoutMonitoring_Fails()
        {
            var src = Synthetic88();
            var host = PerfectCopy(src, LinkTransform.Identity, 77);
            foreach (var h in host) { h.Monitoring = false; h.MonitoredLinkInstances.Clear(); }
            var r = CopyMonitorVerifier.Verify(src, null, host, null, null, 77);
            Assert.Equal("failed", r["result"]);
            Assert.Equal(88L, ((Dictionary<string, object>)r["unsupervised"])["n"]);
        }

        [Fact]
        public void SeededDefects_AreEachReportedInTheirOwnBucket()
        {
            var src = Synthetic88();
            var host = PerfectCopy(src, LinkTransform.Identity, 77);
            host.RemoveAt(0);                                                    // missing
            host[1].Type = "B";                                                  // wrong type (WC A -> WC B)
            host[2].Monitoring = false;                                          // not monitored
            host[3].RotationDeg += 90;                                           // rotated
            host[4].X += 0.5;                                                    // moved
            var dup = host[10];
            host.Add(new HostFixture { Id = 9999, Family = dup.Family, Type = dup.Type, X = dup.X, Y = dup.Y, Z = dup.Z, RotationDeg = dup.RotationDeg, Monitoring = true, MonitoredLinkInstances = new List<long> { 77 } }); // duplicate
            var existing = new HostFixture { Id = 1, Family = "WC", Type = "A", X = -50, Y = -50 };
            host.Add(existing);                                                  // already there before: does not count
            var r = CopyMonitorVerifier.Verify(src, new List<HostFixture> { existing }, host, null, null, 77);
            long N(string b) => (long)((Dictionary<string, object>)r[b])["n"];
            Assert.Equal("failed", r["result"]);
            Assert.Equal(1L, N("missing"));
            Assert.Equal(1L, N("wrong_type"));
            Assert.Equal(1L, N("unsupervised"));
            Assert.Equal(2L, N("misplaced"));
            Assert.Equal(1L, N("duplicates"));
            Assert.Equal(0L, N("unexpected"));
            Assert.Equal(83L, N("correct"));
        }

        [Fact]
        public void TypeMappingAndExclusions_AreHonoured()
        {
            var src = Synthetic88();
            var maps = new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" }, new TypeMapping { Source = "Ducha: E", Excluded = true } };
            var kept = src.Where(s => !(s.Family == "Ducha")).ToList();
            var host = PerfectCopy(kept, LinkTransform.Identity, 77, s => s.Family == "WC" && s.Type == "B" ? "WC: A" : null);
            var r = CopyMonitorVerifier.Verify(src, null, host, null, maps, 77);
            Assert.Equal("verified", r["result"]);
            Assert.Equal(82L, r["expected_items"]);
            Assert.Equal(6L, ((Dictionary<string, object>)r["excluded_items"])["n"]);

            // Without applying the mapping (copied the original type): wrong type on the 2 WC B.
            var unmapped = PerfectCopy(kept, LinkTransform.Identity, 77);
            var r2 = CopyMonitorVerifier.Verify(src, null, unmapped, null, maps, 77);
            Assert.Equal(2L, ((Dictionary<string, object>)r2["wrong_type"])["n"]);
        }
    }
}
