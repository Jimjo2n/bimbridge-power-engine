// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine.Tasks;
using Xunit;
using static Bimbridge.Tests.TaskEngineTests;

namespace Bimbridge.Tests
{
    /// <summary>
    /// Two Revits on the same tasks folder: whoever does not hold the lease does not touch tarea.json (not on cancel/note/reconcile/archive,
    /// nor the engine that lost the task when it stops); the engine that finishes cleanly saves BEFORE releasing the lease.
    /// </summary>
    public class TaskCrossInstanceTests
    {
        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "bimbridge-tareas-" + Guid.NewGuid().ToString("N"));

        private static TaskRecord Draft(string id, int n, string state) => new TaskRecord
        {
            Id = id, Objective = "prueba", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c-" + n, State = state,
            Args = new Dictionary<string, object> { ["n"] = (long)n },
            Docs = new List<TaskDoc> { new TaskDoc { Key = "archivo:c:\\practica.rvt", Title = "practica", Generation = "inst#1" } },
            Policy = new Dictionary<string, object>(),
        };

        private static TaskRunner Runner(TaskStore store, FakeModel m, TaskRecord t) => new TaskRunner(store, new FakeBackend(m), t, "inst:A", "A", null)
        {
            Authority = new AllowAll(), Delay = (d, ct) => Task.Delay(5, ct), MaxWait = TimeSpan.FromSeconds(20), BudgetMs = 200,
        };

        /// <summary>Fix 4: with ANOTHER Revit's valid lease, cancel/note/reconcile/archive from here neither changes nor moves the task.</summary>
        [Fact]
        public void HeldByOtherInstance_ManagerDoesNotTouchTask()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            store.Create(Draft("ajena", 5, TaskState.Interrupted), "// fake");
            Assert.True(store.TryLease("ajena", "inst:B", "B", TimeSpan.FromHours(1), DateTime.UtcNow, out _));
            var file = Path.Combine(store.Dir("ajena"), "tarea.json");
            var before = File.ReadAllText(file);
            var mgr = new TaskManager(new TaskStore(root), "A", t => new FakeBackend(new FakeModel())) { Authority = new AllowAll() };

            var t1 = mgr.Cancel("ajena", out var busy);
            Assert.NotNull(busy);
            Assert.Contains("another Revit", busy);
            Assert.Equal(TaskState.Interrupted, t1.State);
            Assert.Contains("another Revit", mgr.Note("ajena", "nota", "prueba"));
            Assert.Contains("another Revit", mgr.Reconcile("ajena", null, true, "comprobado", "prueba"));
            Assert.Contains("another Revit", mgr.Archive("ajena"));

            Assert.True(store.Exists("ajena"));
            Assert.Equal(before, File.ReadAllText(file));
        }

        /// <summary>Fix 5: the engine that loses the lease (another owner takes it with a higher fencing token and saves its tarea.json) does not overwrite that tarea.json when it stops.</summary>
        [Fact]
        public async Task LeaseLost_RunnerDoesNotOverwriteNewOwnersTaskJson()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            var thief = new TaskStore(root);
            var t = Draft("robada", 200, TaskState.Queued);
            store.Create(t, "// fake");
            var m = new FakeModel { MsPerUnit = 50 };
            var stolen = false;
            var marked = false;
            m.OnVisit = v =>
            {
                if (v != 3) return;
                // After expiry (now+2h) another owner takes it: new fencing token.
                Assert.True(thief.TryLease("robada", "inst:B", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out var l));
                Assert.True(l.Fencing >= 2);
                stolen = true;
            };
            var runner = Runner(store, m, t);
            // The new owner saves its tarea.json at the next authority point (RenewLease -> Now), after the batch 3 checkpoint.
            // Outside the batch: the per-unit Preflight also calls Now, but inside lock(m.Lock) on the same thread.
            runner.Now = () =>
            {
                if (stolen && !marked && !Monitor.IsEntered(m.Lock))
                {
                    marked = true;
                    var other = thief.Load("robada");
                    other.ConfirmSeq = 424242;
                    other.Decisions.Add(new Dictionary<string, object> { ["t"] = "x", ["texto"] = "marcador-B", ["quien"] = "B" });
                    thief.Save(other);
                }
                return DateTime.UtcNow;
            };

            await runner.RunAsync(CancellationToken.None);

            Assert.True(marked);
            Assert.Equal(TaskState.Interrupted, runner.Task_.State);
            Assert.Contains("lease lost",runner.Task_.Reason);
            var onDisk = store.Load("robada");
            Assert.Equal(424242, onDisk.ConfirmSeq);
            Assert.Contains(onDisk.Decisions, d => (d["texto"] as string) == "marcador-B");
            var lease = store.ReadLease("robada");
            Assert.Equal("inst:B", lease.Owner);
            Assert.False(lease.Released);
        }

        /// <summary>Fix 5: the engine that finishes cleanly takes the last checkpoint while it still holds the lease (before releasing it).</summary>
        [Fact]
        public async Task NormalFinish_LastCheckpointBeforeLeaseRelease()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            var t = Draft("normal", 5, TaskState.Queued);
            store.Create(t, "// fake");
            var leaseAtSave = new List<TaskStore.Lease>();
            store.FaultBeforeSave = id => leaseAtSave.Add(store.ReadLease(id));
            var runner = Runner(store, new FakeModel(), t);

            await runner.RunAsync(CancellationToken.None);

            Assert.True(runner.Task_.State == TaskState.Applied, runner.Task_.Reason);
            var last = leaseAtSave.Last();
            Assert.NotNull(last);
            Assert.Equal("inst:A", last.Owner);
            Assert.False(last.Released, "the last tarea.json was written after releasing the lease");
            Assert.True(store.ReadLease("normal").Released);
            Assert.Equal(TaskState.Applied, store.Load("normal").State);
        }
    }
}
