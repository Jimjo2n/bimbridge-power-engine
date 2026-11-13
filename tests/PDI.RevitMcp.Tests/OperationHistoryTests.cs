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
    /// <summary>
    /// 0.9.1 (Bloque I): historial persistente de operaciones, filtros, paginación y consejo de reintento seguro.
    /// AUD-13: aislamiento entre sesiones y agentes (lo ajeno sólo con autorización explícita). AUD-14: con_diferencias exige revisión.
    /// </summary>
    public class OperationHistoryTests
    {
        private static string NewPath() => Path.Combine(Path.GetTempPath(), "bb_hist_" + Guid.NewGuid().ToString("N"), "operaciones.jsonl");
        private static readonly OperationHistory.Scope All = new OperationHistory.Scope { SeeAll = true };
        private static OperationHistory.Scope Me(string session, string agent = null) => new OperationHistory.Scope { SessionId = session, AgentId = agent };

        private static Job Done(JobStore jobs, string session, string agent, string kind, string failStatus = null, string error = null)
        {
            var j = jobs.Create(session, kind);
            j.AgentId = agent;
            if (failStatus == null) jobs.Complete(j, new Dictionary<string, object>()); else jobs.Fail(j, failStatus, error);
            return j;
        }

        [Fact]
        public void EveryFinishedJob_IsRecorded_Persisted_AndFilterable()
        {
            var p = NewPath();
            var jobs = new JobStore();
            var h = new OperationHistory(p);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S1", null, "doc pdf");
            Done(jobs, "S1", null, "commit", Status.Conflicto, "cambió el modelo");
            var c = Done(jobs, "S2", null, "commit", Status.Desconocido, "se cortó");
            Done(jobs, "S2", null, "check", JobStatus.Cancelado, "cancelado");
            Assert.Equal(4, h.Count);
            var (commits, n) = h.Query(All, null, "commit", null, null, null);
            Assert.Equal(2, n);
            Assert.Equal(c.Id, commits[0].JobId);                           // más reciente primero
            Assert.Equal("reconciliar_antes", commits[0].Retry);           // incierto: nunca reintentar a ciegas
            Assert.Equal("reintentable", commits[1].Retry);                // conflicto: no se aplicó nada
            Assert.Equal("reconciliar_antes", h.Query(All, Status.Cancelado, null, null, null, null).Page[0].Retry);
            Assert.Equal(2, h.Query(All, null, null, "S2", null, null).Total);
            Assert.Equal(1, h.Query(All, null, null, null, null, "cortó").Total);

            // Reinicio: se recarga desde el archivo; una línea rota se informa, no se descarta en silencio.
            File.AppendAllText(p, "{roto\n");
            var again = new OperationHistory(p);
            Assert.Equal(4, again.Count);
            Assert.Single(again.LoadErrors);
            var (p1, total) = again.Query(All, null, null, null, null, null, page: 2, pageSize: 3);
            Assert.Equal(4, total);
            Assert.Single(p1);
        }

        [Fact]
        public void ConDiferencias_RequiresReview_NotSuccessWithoutPendingActions()
        {
            // AUD-14
            Assert.Equal("revisar_diferencias", OperationHistory.RetryAdvice(Status.ConDiferencias));
            Assert.Equal("no_necesario", OperationHistory.RetryAdvice(Status.Ok));
            Assert.Equal("reconciliar_antes", OperationHistory.RetryAdvice("un_estado_nuevo_desconocido"));
        }

        [Fact]
        public void Sessions_OnlySeeTheirOwnOperations_UnlessExplicitlyAuthorized()
        {
            // AUD-13: dos agentes con sesiones concurrentes.
            var jobs = new JobStore();
            var h = new OperationHistory(null);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S-ana-1", "A-ana", "commit");
            Done(jobs, "S-ana-1", "A-ana", "doc pdf");
            Done(jobs, "S-bob-1", "A-bob", "commit", Status.Desconocido, "secreto de bob");
            Done(jobs, "S-anon-1", null, "check");

            var ana = h.Query(Me("S-ana-1", "A-ana"), null, null, null, null, null);
            Assert.Equal(2, ana.Total);
            Assert.All(ana.Page, r => Assert.Equal("A-ana", r.Agent));
            // Los filtros no amplían el ámbito: pedir la sesión o el texto de otro no lo muestra.
            Assert.Equal(0, h.Query(Me("S-ana-1", "A-ana"), null, null, "S-bob-1", null, null).Total);
            Assert.Equal(0, h.Query(Me("S-ana-1", "A-ana"), null, null, null, null, "secreto").Total);
            Assert.Equal(1, h.Query(Me("S-bob-1", "A-bob"), null, null, null, null, null).Total);
            // Una sesión anónima sólo ve su propia sesión; otra sesión anónima no ve la primera (null no es identidad compartida).
            Assert.Equal(1, h.Query(Me("S-anon-1"), null, null, null, null, null).Total);
            Assert.Equal(0, h.Query(Me("S-anon-2"), null, null, null, null, null).Total);
            // Sin identidad alguna no se ve nada; el ámbito es obligatorio.
            Assert.Equal(0, h.Query(new OperationHistory.Scope(), null, null, null, null, null).Total);
            Assert.Throws<ArgumentNullException>(() => h.Query(null, null, null, null, null, null));
            // Pedir «todos» sin autorización del usuario se rechaza (no se concede por pedirlo ni se degrada en silencio).
            Assert.Null(OperationHistory.ScopeFor("S-ana-1", "A-ana", wantAll: true, sharedAuthorized: false, out var refusal));
            Assert.Contains("historial_compartido", refusal);
            Assert.False(new EngineConfig().SharedHistory);                 // por defecto, no autorizado
            Assert.Equal(2, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", false, false, out _), null, null, null, null, null).Total);
            Assert.Equal(2, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", false, true, out _), null, null, null, null, null).Total);   // autorizado pero no pedido: lo propio
            // Con la autorización explícita (historial_compartido) y pidiéndolo, se ve todo.
            Assert.Equal(4, h.Query(OperationHistory.ScopeFor("S-ana-1", "A-ana", true, true, out _), null, null, null, null, null).Total);
            // En curso: mismo criterio.
            var running = jobs.Create("S-bob-1", "geo"); running.AgentId = "A-bob";
            Assert.Empty(jobs.ForOwner("S-ana-1", "A-ana").Where(j => !j.IsFinal));
            Assert.Single(jobs.ForOwner("S-bob-2", "A-bob").Where(j => !j.IsFinal));
            Assert.Empty(jobs.ForOwner("S-anon-2", null).Where(j => !j.IsFinal));
        }

        [Fact]
        public void Reconnection_SameNamedAgentKeepsItsHistory_AcrossSessionsAndRestart()
        {
            // AUD-13: al reconectar la sesión es otra (ids únicos por instancia); el agente con nombre conserva lo suyo, también tras reiniciar.
            var p = NewPath();
            var jobs = new JobStore();
            var h = new OperationHistory(p);
            jobs.OnAnyFinal = j => h.Add(j);
            Done(jobs, "S-ana-1", "A-ana", "commit");
            Done(jobs, "S-bob-1", "A-bob", "commit");
            Assert.Equal(1, h.Query(Me("S-ana-2", "A-ana"), null, null, null, null, null).Total);
            var restarted = new OperationHistory(p);
            Assert.Equal(1, restarted.Query(Me("S-ana-3", "A-ana"), null, null, null, null, null).Total);
            Assert.Equal("S-ana-1", restarted.Query(Me("S-ana-3", "A-ana"), null, null, null, null, null).Page[0].Session);
            Assert.Equal(0, restarted.Query(Me("S-eve-1", "A-eve"), null, null, null, null, null).Total);
        }

        [Fact]
        public void ConcurrentSessions_WritingAndReading_StayIsolatedAndComplete()
        {
            var jobs = new JobStore();
            var h = new OperationHistory(NewPath());
            jobs.OnAnyFinal = j => h.Add(j);
            const int perAgent = 60;
            var leaks = 0;
            using (var start = new ManualResetEventSlim())
            {
                var writers = new[] { "ana", "bob", "eve" }.Select(name => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < perAgent; i++)
                    {
                        Done(jobs, "S-" + name, "A-" + name, "commit " + name);
                        var mine = h.Query(Me("S-" + name, "A-" + name), null, null, null, null, null, pageSize: 100);
                        if (mine.Page.Any(r => r.Agent != "A-" + name)) Interlocked.Increment(ref leaks);
                    }
                })).ToArray();
                start.Set();
                Task.WaitAll(writers);
            }
            Assert.Equal(0, leaks);
            foreach (var name in new[] { "ana", "bob", "eve" })
                Assert.Equal(perAgent, h.Query(Me("S-" + name, "A-" + name), null, null, null, null, null).Total);
            Assert.Equal(3 * perAgent, h.Query(All, null, null, null, null, null).Total);
        }

        [Fact]
        public async Task JobFinishingWithDifferences_AfterClientSawEnProceso_IsNeverRecordedAsOk()
        {
            // AUD-18: el cliente recibió en_proceso; el trabajo termina después con diferencias de verificación.
            var jobs = new JobStore();
            var h = new OperationHistory(NewPath());
            jobs.OnAnyFinal = j => h.Add(j);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = jobs.Create("S1", "doc pdf");
            jobs.Run(job, async () =>
            {
                await release.Task;
                // Como DocOp: resultado con la lista de diferencias, sin pasar estado.
                jobs.Complete(job, new Dictionary<string, object> { ["archivo"] = "x.pdf", ["diferencias"] = new List<object> { "el PDF tiene 3 páginas y se pidieron 4 hojas" } });
            });
            Assert.Equal(Status.EnProceso, job.Estado);                    // lo que vio el cliente al principio
            Assert.Equal(0, h.Count);
            release.SetResult(true);
            await job.Work;
            Assert.Equal(Status.ConDiferencias, job.Estado);               // respuesta diferida
            Assert.Equal(Status.ConDiferencias, jobs.Get(job.Id).Estado);  // consulta posterior del job_id
            var rec = h.Query(All, null, null, null, null, null).Page.Single();
            Assert.Equal(Status.ConDiferencias, rec.Estado);               // historial
            Assert.Equal("revisar_diferencias", rec.Retry);                // exige revisión, no "no_necesario"
            Assert.Equal(0, h.Query(All, Status.Ok, null, null, null, null).Total);

            // Un "ok" explícito no enmascara diferencias; otros estados explícitos se respetan; sin diferencias sigue siendo ok.
            var a = jobs.Create("S1", "doc ifc"); jobs.Complete(a, new Dictionary<string, object> { ["diferencias"] = new List<object> { "x" } }, Status.Ok);
            Assert.Equal(Status.ConDiferencias, a.Estado);
            var b = jobs.Create("S1", "exec"); jobs.Complete(b, new Dictionary<string, object> { ["diferencias"] = new List<object> { "x" } }, "parcial");
            Assert.Equal("parcial", b.Estado);
            var c = jobs.Create("S1", "doc pdf"); jobs.Complete(c, new Dictionary<string, object> { ["diferencias"] = new List<object>() });
            Assert.Equal(Status.Ok, c.Estado);
        }

        [Fact]
        public async Task CancelledRunningJob_EndsCancelled_AndIsRecorded()
        {
            var jobs = new JobStore();
            var h = new OperationHistory(null);
            jobs.OnAnyFinal = j => h.Add(j);
            var j1 = jobs.Create("S1", "geo");
            jobs.Run(j1, async () => { await Task.Delay(5000, j1.Cts.Token); jobs.Complete(j1, null); });
            Assert.True(jobs.Cancel(j1));
            await j1.Work;
            Assert.Equal(JobStatus.Cancelado, j1.Status);
            Assert.Equal(1, h.Count);
            Assert.False(jobs.Cancel(j1));                                  // ya terminado: cancelar no hace nada
        }
    }
}
