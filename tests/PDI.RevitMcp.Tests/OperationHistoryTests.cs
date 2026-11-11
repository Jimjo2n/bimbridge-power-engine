// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.9.1 (Bloque I): historial persistente de operaciones, filtros, paginación y consejo de reintento seguro.</summary>
    public class OperationHistoryTests
    {
        private static string NewPath() => Path.Combine(Path.GetTempPath(), "bb_hist_" + Guid.NewGuid().ToString("N"), "operaciones.jsonl");

        [Fact]
        public void EveryFinishedJob_IsRecorded_Persisted_AndFilterable()
        {
            var p = NewPath();
            var jobs = new JobStore();
            var h = new OperationHistory(p);
            jobs.OnAnyFinal = j => h.Add(j);
            var a = jobs.Create("S1", "doc pdf"); jobs.Complete(a, new System.Collections.Generic.Dictionary<string, object>());
            var b = jobs.Create("S1", "commit"); jobs.Fail(b, Status.Conflicto, "cambió el modelo");
            var c = jobs.Create("S2", "commit"); jobs.Fail(c, Status.Desconocido, "se cortó");
            var d = jobs.Create("S2", "check"); jobs.Fail(d, JobStatus.Cancelado, "cancelado");
            Assert.Equal(4, h.Count);
            var (commits, n) = h.Query(null, "commit", null, null, null);
            Assert.Equal(2, n);
            Assert.Equal(c.Id, commits[0].JobId);                           // más reciente primero
            Assert.Equal("reconciliar_antes", commits[0].Retry);           // incierto: nunca reintentar a ciegas
            Assert.Equal("reintentable", commits[1].Retry);                // conflicto: no se aplicó nada
            Assert.Equal("reconciliar_antes", h.Query(Status.Cancelado, null, null, null, null).Page[0].Retry);
            Assert.Equal(2, h.Query(null, null, "S2", null, null).Total);
            Assert.Equal(1, h.Query(null, null, null, null, "cortó").Total);

            // Reinicio: se recarga desde el archivo; una línea rota se informa, no se descarta en silencio.
            File.AppendAllText(p, "{roto\n");
            var again = new OperationHistory(p);
            Assert.Equal(4, again.Count);
            Assert.Single(again.LoadErrors);
            var (p1, total) = again.Query(null, null, null, null, null, page: 2, pageSize: 3);
            Assert.Equal(4, total);
            Assert.Single(p1);
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
