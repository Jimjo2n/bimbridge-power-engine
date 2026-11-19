// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine.Tasks;
using Xunit;
using static PDI.RevitMcp.Tests.TaskEngineTests;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Dos Revit sobre la misma carpeta de tareas: quien no tiene el lease no toca tarea.json (ni al cancelar/anotar/reconciliar/archivar,
    /// ni el motor que perdió la tarea al detenerse); el motor que termina bien guarda ANTES de soltar el lease.
    /// </summary>
    public class TaskCrossInstanceTests
    {
        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "pdi-tareas-" + Guid.NewGuid().ToString("N"));

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

        /// <summary>Fix 4: con el lease vigente de OTRO Revit, cancelar/anotar/reconciliar/archivar desde aquí no cambia ni mueve la tarea.</summary>
        [Fact]
        public void HeldByOtherInstance_ManagerDoesNotTouchTask()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            store.Create(Draft("ajena", 5, TaskState.Interrumpida), "// fake");
            Assert.True(store.TryLease("ajena", "inst:B", "B", TimeSpan.FromHours(1), DateTime.UtcNow, out _));
            var file = Path.Combine(store.Dir("ajena"), "tarea.json");
            var before = File.ReadAllText(file);
            var mgr = new TaskManager(new TaskStore(root), "A", t => new FakeBackend(new FakeModel())) { Authority = new AllowAll() };

            var t1 = mgr.Cancel("ajena", out var busy);
            Assert.NotNull(busy);
            Assert.Contains("otro Revit", busy);
            Assert.Equal(TaskState.Interrumpida, t1.State);
            Assert.Contains("otro Revit", mgr.Note("ajena", "nota", "prueba"));
            Assert.Contains("otro Revit", mgr.Reconcile("ajena", null, true, "comprobado", "prueba"));
            Assert.Contains("otro Revit", mgr.Archive("ajena"));

            Assert.True(store.Exists("ajena"));
            Assert.Equal(before, File.ReadAllText(file));
        }

        /// <summary>Fix 5: el motor que pierde el lease (otro dueño lo toma con fencing mayor y guarda su tarea.json) no pisa ese tarea.json al detenerse.</summary>
        [Fact]
        public async Task LeaseLost_RunnerDoesNotOverwriteNewOwnersTaskJson()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            var thief = new TaskStore(root);
            var t = Draft("robada", 200, TaskState.EnCola);
            store.Create(t, "// fake");
            var m = new FakeModel { MsPerUnit = 50 };
            var stolen = false;
            var marked = false;
            m.OnVisit = v =>
            {
                if (v != 3) return;
                // Tras vencer (now+2h) otro dueño la toma: fencing nuevo.
                Assert.True(thief.TryLease("robada", "inst:B", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out var l));
                Assert.True(l.Fencing >= 2);
                stolen = true;
            };
            var runner = Runner(store, m, t);
            // El nuevo dueño guarda su tarea.json en el siguiente punto de autoridad (RenewLease → Now), ya pasado el checkpoint del lote 3.
            // Fuera del lote: el Preflight por unidad también llama a Now, pero dentro de lock(m.Lock) en el mismo hilo.
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
            Assert.Equal(TaskState.Interrumpida, runner.Task_.State);
            Assert.Contains("lease perdido", runner.Task_.Reason);
            var onDisk = store.Load("robada");
            Assert.Equal(424242, onDisk.ConfirmSeq);
            Assert.Contains(onDisk.Decisions, d => (d["texto"] as string) == "marcador-B");
            var lease = store.ReadLease("robada");
            Assert.Equal("inst:B", lease.Owner);
            Assert.False(lease.Released);
        }

        /// <summary>Fix 5: el motor que termina bien hace el último checkpoint con el lease aún suyo (antes de soltarlo).</summary>
        [Fact]
        public async Task NormalFinish_LastCheckpointBeforeLeaseRelease()
        {
            var root = TempRoot();
            var store = new TaskStore(root);
            var t = Draft("normal", 5, TaskState.EnCola);
            store.Create(t, "// fake");
            var leaseAtSave = new List<TaskStore.Lease>();
            store.FaultBeforeSave = id => leaseAtSave.Add(store.ReadLease(id));
            var runner = Runner(store, new FakeModel(), t);

            await runner.RunAsync(CancellationToken.None);

            Assert.True(runner.Task_.State == TaskState.Aplicada, runner.Task_.Reason);
            var last = leaseAtSave.Last();
            Assert.NotNull(last);
            Assert.Equal("inst:A", last.Owner);
            Assert.False(last.Released, "el último tarea.json se escribió después de soltar el lease");
            Assert.True(store.ReadLease("normal").Released);
            Assert.Equal(TaskState.Aplicada, store.Load("normal").State);
        }
    }
}
