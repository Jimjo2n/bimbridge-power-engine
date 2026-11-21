// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Engine.Tasks;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// Task engine with a FAKE model (not Revit): tests ordering, batches, checkpoints, resume, barriers, cancellation and the
    /// commit->journal window with fault injection. Acceptance inside Revit is in tools/acceptance (not yet run).
    /// </summary>
    public class TaskEngineTests
    {
        /// <summary>Fake model: what is "applied" in Revit memory, what is saved in the file, open generation and injected faults.</summary>
        public sealed class FakeModel
        {
            public readonly object Lock = new object();
            public Dictionary<string, string> Live = new Dictionary<string, string>();
            public Dictionary<string, string> Saved = new Dictionary<string, string>();
            public int Generation = 1;
            public int Duplicates;
            public int Visits;
            public int MsPerUnit = 2;
            public volatile bool Paused;
            public volatile bool DocClosed;
            public string CrashAfterCommitAt;
            public HashSet<string> FailUnits = new HashSet<string>();
            public HashSet<string> ConflictUnits = new HashSet<string>();
            public HashSet<string> Unverifiable = new HashSet<string>();
            public int Barriers;
            public Action<int> OnVisit;
            public int Count;
            public List<string> PreflightRefusals = new List<string>();

            /// <summary>Revit crashes and the file is reopened: unsaved work is lost and the generation changes.</summary>
            public void RestartRevit()
            {
                lock (Lock) { Live = new Dictionary<string, string>(Saved); Generation++; }
            }
        }

        public sealed class CrashException : Exception { public CrashException() : base("Revit died (simulated)") { } }

        public sealed class FakeBackend : ITaskBackend
        {
            private readonly FakeModel _m;
            public FakeBackend(FakeModel m) { _m = m; }

            public BackendResult<bool> CheckContext(TaskRecord task)
            {
                if (_m.Paused) return BackendResult<bool>.Waiting("the user paused the AI", "press Resume AI in Revit");
                if (_m.DocClosed) return BackendResult<bool>.Waiting("the document is closed", "open the document");
                return BackendResult<bool>.Ok(true);
            }

            public Task<BackendResult<List<UnitRef>>> EnumerateAsync(TaskRecord task, CancellationToken ct)
            {
                var n = (int)(Bimbridge.Protocol.Arg.GetLong(task.Args, "n") ?? 10);
                var list = Enumerable.Range(1, n).Select(i => new UnitRef { Key = "u" + i.ToString("D4") }).ToList();
                var barrierAt = Bimbridge.Protocol.Arg.GetLong(task.Args, "barrier_at");
                if (barrierAt.HasValue) list.Insert((int)barrierAt.Value, new UnitRef { Key = "pdf-intermedio", Barrier = "pdf", BarrierArgs = new Dictionary<string, object>() });
                return Task.FromResult(BackendResult<List<UnitRef>>.Ok(list));
            }

            public Task<BackendResult<List<UnitOutcome>>> RunBatchAsync(TaskRecord task, IReadOnlyList<UnitRef> units, int budgetMs, CancellationToken ct)
            {
                var outs = new List<UnitOutcome>();
                lock (_m.Lock)
                {
                    _m.Visits++;
                    _m.OnVisit?.Invoke(_m.Visits);
                    if (_m.Paused) return Task.FromResult(BackendResult<List<UnitOutcome>>.Waiting("the user paused the AI"));
                    long spent = 0;
                    foreach (var u in units)
                    {
                        if (spent + _m.MsPerUnit > budgetMs && outs.Count > 0) break; // visit budget
                        if (task.Preflight?.Invoke() is string pf) { _m.PreflightRefusals.Add(pf); break; } // last safe point (R06)
                        spent += _m.MsPerUnit;
                        if (_m.ConflictUnits.Contains(u.Key)) { outs.Add(new UnitOutcome { Key = u.Key, State = UnitState.Conflict, Error = "another user changed the element", Ms = _m.MsPerUnit }); continue; }
                        if (_m.FailUnits.Contains(u.Key)) { outs.Add(new UnitOutcome { Key = u.Key, State = UnitState.RolledBack, Error = "Revit rejected the change", Ms = _m.MsPerUnit }); continue; }
                        if (_m.Live.ContainsKey(u.Key)) _m.Duplicates++;
                        var uid = "uid-" + u.Key + "-" + (++_m.Count);
                        _m.Live[u.Key] = uid;
                        if (_m.CrashAfterCommitAt == u.Key) { _m.CrashAfterCommitAt = null; throw new CrashException(); }
                        var o = new UnitOutcome { Key = u.Key, State = UnitState.Committed, Ms = _m.MsPerUnit, Changed = 1 };
                        o.Created.Add(new object[] { (long)_m.Count, uid });
                        outs.Add(o);
                    }
                }
                return Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(outs));
            }

            public Task<BackendResult<UnitOutcome>> RunBarrierAsync(TaskRecord task, UnitRef barrier, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    _m.Barriers++;
                    var o = new UnitOutcome { Key = barrier.Key, State = UnitState.Committed, Ms = 50 };
                    if (barrier.Barrier == "save")
                    {
                        _m.Saved = new Dictionary<string, string>(_m.Live);
                        o.Persist = new PersistEvidence { Kind = "save", When = DateTime.UtcNow.ToString("o"), File = "practica.rvt", Bytes = _m.Saved.Count };
                    }
                    if (barrier.Barrier == "pdf")
                        o.Artifacts.Add(new Dictionary<string, object> { ["path"] = "salida.pdf", ["sha256"] = "abc", ["units_in_model"] = (long)_m.Live.Count });
                    return Task.FromResult(BackendResult<UnitOutcome>.Ok(o));
                }
            }

            public Task<BackendResult<List<UnitOutcome>>> VerifyAsync(TaskRecord task, IReadOnlyList<UnitRecord> units, bool reopened, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    var outs = units.Select(u => _m.Unverifiable.Contains(u.Key) ? UnitOutcome.Of(u.Key, UnitState.Unknown, "cannot be checked")
                        : _m.Live.ContainsKey(u.Key) ? UnitOutcome.Of(u.Key, UnitState.Committed) : UnitOutcome.Of(u.Key, UnitState.Pending, "not in the model")).ToList();
                    return Task.FromResult(BackendResult<List<UnitOutcome>>.Ok(outs));
                }
            }

            public Task<BackendResult<Dictionary<string, object>>> PostconditionAsync(TaskRecord task, CancellationToken ct)
            {
                lock (_m.Lock)
                {
                    var n = (int)(Bimbridge.Protocol.Arg.GetLong(task.Args, "n") ?? 10);
                    var missing = Enumerable.Range(1, n).Select(i => "u" + i.ToString("D4")).Where(k => !_m.Live.ContainsKey(k)).Cast<object>().ToList();
                    return Task.FromResult(BackendResult<Dictionary<string, object>>.Ok(new Dictionary<string, object>
                    {
                        ["in_model"] = (long)_m.Live.Count, ["deviations"] = missing,
                    }));
                }
            }

            public string CurrentGeneration(TaskRecord task) { lock (_m.Lock) return _m.DocClosed ? null : "inst#" + _m.Generation; }
        }

        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "bimbridge-tareas-" + Guid.NewGuid().ToString("N"));

        public sealed class AllowAll : ITaskAuthority
        {
            public string Check(TaskRecord task, string capability, DateTime now) => null;
        }

        private static TaskManager Manager(string root, FakeModel m, string instance = "A") => new TaskManager(new TaskStore(root), instance, t => new FakeBackend(m))
        {
            Authority = new AllowAll(),
            Delay = (d, ct) => Task.Delay(5, ct),
            MaxWait = TimeSpan.FromSeconds(20),
            BudgetMs = 200,
        };

        private static TaskRecord Draft(string id, int n, Dictionary<string, object> policy = null, Dictionary<string, object> extra = null)
        {
            var args = new Dictionary<string, object> { ["n"] = (long)n };
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value;
            return new TaskRecord
            {
                Id = id, Objective = "test", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c-" + n,
                Args = args, Docs = new List<TaskDoc> { new TaskDoc { Key = "archivo:c:\\practica.rvt", Title = "practice", Generation = "inst#1" } },
                Policy = policy ?? new Dictionary<string, object>(),
            };
        }

        private static async Task<TaskRecord> RunToEnd(TaskManager mgr, string id)
        {
            await mgr.Completion(id);
            return mgr.Get(id, out _);
        }

        /// <summary>INV-001/002 (0.6.1): if another instance takes the task (new fencing token), the previous runner does not run a single batch more.</summary>
        [Fact]
        public async Task LeaseStolen_MidRun_OldRunnerStopsBeforeNextBatch()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 50 };
            var thief = new TaskStore(root);
            int visitsAtSteal = -1;
            m.OnVisit = v =>
            {
                if (v == 3)
                {
                    Assert.True(thief.TryLease("theft", "OTRA-INSTANCIA", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out var l));
                    Assert.True(l.Fencing >= 2);
                    visitsAtSteal = v;
                }
            };
            var mgr = Manager(root, m);
            mgr.Start(Draft("theft", 200), "// fake", "test");
            var t = await RunToEnd(mgr, "theft");
            // The task belongs to B (valid lease): the old runner does not write its "interrupted" over B's tarea.json.
            Assert.Equal(TaskState.Running, t.State);
            Assert.Equal(3, m.Visits); // no batch after the takeover
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task ThousandUnits_RunInManyVisits_WithAdaptiveBatches_NoDuplicates()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("t1000", 1000), "recipe", "claude");
            var t = await RunToEnd(mgr, "t1000");
            mgr.Get("t1000", out var units);
            Assert.True(t.State == TaskState.Applied, t.Reason);
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(m.Visits > 5, "must yield the thread between visits: " + m.Visits);
            Assert.True(m.Visits < 200, "the batch must grow with the measurement: " + m.Visits);
            Assert.True(t.Metric("checkpoints") >= m.Visits);
            Assert.Equal(1000, units.Units.Values.Count(u => u.State == UnitState.Committed));
            Assert.Contains("1000/1000", t.Summary);
        }

        /// <summary>
        /// Measurement (as well as a test): how much text the AI sees to follow and recover a 1000-unit task, with failures and exceptions.
        /// It is the real TaskSummary output over the real engine with the FAKE model; it does not measure Revit. BIMBRIDGE_BENCH_OUT=file saves it.
        /// </summary>
        [Fact]
        public async Task Bench_CompactSummary_StaysSmall_ForThousandUnits()
        {
            var m = new FakeModel();
            for (var i = 0; i < 1000; i += 97) m.FailUnits.Add("u" + i.ToString("D4"));
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("tb", 1000, new Dictionary<string, object> { ["on_error"] = "continue" }), "recipe", "claude");
            var t = await RunToEnd(mgr, "tb");
            mgr.Get("tb", out var units);
            var status = Bimbridge.Protocol.Json.Write(TaskSummary.Status(t, units, true));
            var compact = CompactText.Write(TaskSummary.Status(t, units, true));
            // Another client: opens the store from scratch (as after losing the chat) and asks for the summary.
            var other = Manager(root, m);
            var t2 = other.Get("tb", out var units2);
            var recovered = CompactText.Write(TaskSummary.Status(t2, units2, true));
            Assert.True(units.Units.Count == units2.Units.Count, "another client sees " + units2.Units.Count + " units and the first " + units.Units.Count + " state " + t.State + " " + t.Summary);
            Assert.True(compact.Length < 3000, "resumen compacto demasiado grande: " + compact.Length);
            Assert.Equal(compact, recovered);
            var outFile = Environment.GetEnvironmentVariable("BIMBRIDGE_BENCH_OUT");
            if (!string.IsNullOrEmpty(outFile))
                File.WriteAllText(outFile, Bimbridge.Protocol.Json.Write(new Dictionary<string, object>
                {
                    ["units"] = 1000L, ["failed"] = (long)m.FailUnits.Count, ["status"] = t.State.ToString(),
                    ["summary_json_chars"] = (long)status.Length, ["compact_summary_chars"] = (long)compact.Length,
                    ["recovered_by_other_client_chars"] = (long)recovered.Length, ["compact_summary"] = compact,
                }));
        }

        [Fact]
        public async Task CrashBetweenCommitAndJournal_Resume_VerifiesAndDoesNotDuplicate()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0500" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("crash", 1000), "recipe", "claude");
            var t = await RunToEnd(mgr, "crash");
            mgr.Get("crash", out var units);
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Equal(UnitState.InProgress, units.Get("u0500").State); // the commit happened, the "end" was not written

            // Another process / another client (Codex) resumes by id, without the previous session.
            var mgr2 = Manager(root, m, "B");
            var r = mgr2.Resume("crash", "codex", null);
            Assert.Null(r.Error);
            t = await RunToEnd(mgr2, "crash");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(t.Metric("verified") >= 1);
            Assert.Contains("codex", t.Clients);
        }

        [Fact]
        public async Task RevitRestartBeforeSave_LostUnitsAreReexecuted_SavedOnesAreNot()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0450" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("restart", 1000, new Dictionary<string, object> { ["save_every"] = 300L }), "recipe", "claude");
            var t = await RunToEnd(mgr, "restart");
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.NotNull(t.LastPersist);
            var savedSeq = t.LastPersist.Seq;
            Assert.True(savedSeq >= 300 && savedSeq < 450);
            // Revit closes without saving: the file only has what the barrier saved.
            m.RestartRevit();
            Assert.Equal((int)savedSeq, m.Live.Count);
            var mgr2 = Manager(root, m, "B");
            mgr2.Resume("restart", "claude", null);
            t = await RunToEnd(mgr2, "restart");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(1000, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.Equal(1L, t.Metric("reopenings"));
            Assert.True(t.Metric("verified") >= 450 - savedSeq);
        }

        [Fact]
        public async Task RevitRestartRightAfterSave_NothingIsLost_NothingRepeated()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0301" };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("aftersave", 600, new Dictionary<string, object> { ["save_every"] = 300L }), "recipe", "claude");
            await RunToEnd(mgr, "aftersave");
            m.RestartRevit();
            Assert.Equal(300, m.Live.Count);
            var mgr2 = Manager(root, m, "B");
            mgr2.Resume("aftersave", "claude", null);
            var t = await RunToEnd(mgr2, "aftersave");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(600, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task Cancel_IsCooperative_AndConfirmedWorkIsReported()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            m.OnVisit = v => { if (v == 4) mgr.Cancel("cancel", out _); };
            mgr.MaxWait = TimeSpan.FromSeconds(5);
            var draft = Draft("cancel", 1000, new Dictionary<string, object> { ["batch_max"] = 20L });
            mgr.Start(draft, "recipe", "claude");
            var t = await RunToEnd(mgr, "cancel");
            mgr.Get("cancel", out var units);
            Assert.Equal(TaskState.Cancelled, t.State);
            var confirmed = units.Units.Values.Count(u => u.State == UnitState.Committed);
            Assert.True(confirmed > 0 && confirmed < 1000);
            Assert.Equal(confirmed, m.Live.Count); // what is committed is exactly what is in the model
            Assert.Contains("stays applied", t.Reason);
        }

        [Fact]
        public async Task ExportBarrier_DoesNotRunWhileEarlierUnitsAreNotConfirmed()
        {
            var m = new FakeModel { FailUnits = new HashSet<string> { "u0003" } };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("barrier", 10, new Dictionary<string, object> { ["on_error"] = "continue" }, new Dictionary<string, object> { ["barrier_at"] = 6L }), "recipe", "claude");
            var t = await RunToEnd(mgr, "barrier");
            Assert.Equal(TaskState.Partial, t.State);
            Assert.Contains("barrier pdf does not run", t.Reason);
            Assert.Equal(0, m.Barriers);
            // With the fault fixed, resume retries the rolled-back unit and now exports, with the artifact recorded.
            m.FailUnits.Clear();
            mgr.Resume("barrier", "claude", null);
            t = await RunToEnd(mgr, "barrier");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(1, m.Barriers);
            Assert.Single(t.Artifacts);
            Assert.Equal(6L, t.Artifacts[0]["units_in_model"]);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task PauseAndClosedDocument_WaitWithoutLosingProgress()
        {
            var m = new FakeModel();
            var mgr = Manager(TempRoot(), m);
            m.OnVisit = v =>
            {
                if (v == 3) { m.Paused = true; Task.Run(async () => { await Task.Delay(150); m.Paused = false; m.DocClosed = true; await Task.Delay(150); m.DocClosed = false; }); }
            };
            mgr.Start(Draft("pause", 300, new Dictionary<string, object> { ["batch_max"] = 10L }), "recipe", "claude");
            var t = await RunToEnd(mgr, "pause");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(300, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(t.Metric("waits") >= 1);
        }

        [Fact]
        public async Task ConflictsAreReported_NotOverwritten()
        {
            var m = new FakeModel { ConflictUnits = new HashSet<string> { "u0002", "u0007" } };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("conflicts", 10), "recipe", "claude");
            var t = await RunToEnd(mgr, "conflicts");
            Assert.Equal(TaskState.AppliedWithDifferences, t.State);
            Assert.Equal(8, m.Live.Count);
            var status = TaskSummary.Status(t, mgr.Store.LoadUnits("conflicts"), true);
            Assert.Equal(2, ((List<object>)status["deviations"]).Count);
        }

        [Fact]
        public async Task UnverifiableUnits_RequireExplicitReconcile()
        {
            var m = new FakeModel { CrashAfterCommitAt = "u0005", Unverifiable = new HashSet<string> { "u0005" } };
            var root = TempRoot();
            var mgr = Manager(root, m);
            mgr.Start(Draft("reconc", 10), "recipe", "claude");
            await RunToEnd(mgr, "reconc");
            mgr.Resume("reconc", "claude", null);
            var t = await RunToEnd(mgr, "reconc");
            Assert.Equal(TaskState.Interrupted, t.State);
            Assert.Contains("reconcile", t.Reason);
            Assert.Null(mgr.Reconcile("reconc", new List<string> { "u0005" }, applied: true, note: "comprobado en el modelo", who: "claude"));
            mgr.Resume("reconc", "claude", null);
            t = await RunToEnd(mgr, "reconc");
            Assert.Equal(TaskState.Applied, t.State);
            Assert.Equal(0, m.Duplicates);
            Assert.Contains(t.Decisions, d => ((string)d["text"]).Contains("reconciled 1"));
        }

        [Fact]
        public async Task SameTaskIdTwice_DoesNotStartASecondRunner_AndDifferentContentIsRejected()
        {
            var m = new FakeModel();
            var gate = new ManualResetEventSlim(false);
            m.OnVisit = v => { if (v == 1) gate.Wait(2000); };
            var mgr = Manager(TempRoot(), m);
            var a = mgr.Start(Draft("two", 50), "recipe", "claude");
            var b = mgr.Start(Draft("two", 50), "recipe", "codex");
            Assert.True(a.Created);
            Assert.True(b.AlreadyRunning);
            var c = mgr.Start(Draft("two", 51), "recipe", "codex");
            Assert.NotNull(c.Error);
            gate.Set();
            var t = await RunToEnd(mgr, "two");
            Assert.Equal(50, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task UnitJournalUnwritable_StopsVisibly()
        {
            var m = new FakeModel();
            var root = TempRoot();
            var mgr = Manager(root, m);
            m.OnVisit = v =>
            {
                if (v != 2) return;
                // The units log becomes impossible to write (disk, permissions...): it is replaced by a folder.
                var p = Path.Combine(root, "broken", "units.jsonl");
                File.Delete(p);
                Directory.CreateDirectory(p);
            };
            mgr.Start(Draft("broken", 100, new Dictionary<string, object> { ["batch_max"] = 5L }), "recipe", "claude");
            var t = await RunToEnd(mgr, "broken");
            Assert.Equal(TaskState.Partial, t.State);
            Assert.Contains("unit log could not be written", t.Reason);
        }

        [Fact]
        public async Task WaitReturnsOnRevisionChange_WithoutPolling()
        {
            var m = new FakeModel();
            var gate = new ManualResetEventSlim(false);
            m.OnVisit = v => { if (v == 1) gate.Wait(3000); };
            var mgr = Manager(TempRoot(), m);
            mgr.Start(Draft("wait", 20), "recipe", "claude");
            var rev0 = mgr.Get("wait", out _).Rev;
            var waiting = mgr.WaitAsync("wait", rev0, TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            gate.Set();
            var t = await waiting;
            Assert.True(t.Rev > rev0);
            await mgr.Completion("wait");
        }

        [Fact]
        public void DocLocks_AllOrNothing_Reentrant_NoDeadlock()
        {
            var l = new DocLocks();
            Assert.True(l.TryAcquire(new[] { "b", "a" }, "tarea:1", "task", "1", out _));
            Assert.True(l.TryAcquire(new[] { "a" }, "tarea:1", "task", "1", out _)); // reentrant
            Assert.False(l.TryAcquire(new[] { "c", "a" }, "other", "barrier", null, out var h));
            Assert.Equal("tarea:1", h.Owner);
            Assert.Null(l.HeldByOther("c", "x")); // did not take "c" halfway
            l.Release(new[] { "a" }, "tarea:1");
            Assert.NotNull(l.HeldByOther("a", "other")); // still held (depth 2 -> 1)
            l.Release(new[] { "a", "b" }, "tarea:1");
            Assert.True(l.TryAcquire(new[] { "a", "b", "c" }, "other", "barrier", null, out _));
        }
    }
}
