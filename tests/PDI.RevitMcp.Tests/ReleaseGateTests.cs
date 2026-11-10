// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Tasks;
using PDI.RevitMcp.Scripting;
using Xunit;
using FakeModel = PDI.RevitMcp.Tests.TaskEngineTests.FakeModel;
using FakeBackend = PDI.RevitMcp.Tests.TaskEngineTests.FakeBackend;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Pruebas de la puerta de salida 0.6.1 (RELEASE_GATE_0.6.1.md). Cada prueba nombra el requisito (Rnn) y el subcriterio que demuestra.
    /// Diseñadas para refutar: cada una intenta el camino que NO debe ocurrir y comprueba que el punto de ejecución lo impide.
    /// </summary>
    public class ReleaseGateTests
    {
        private const string DocKey = "archivo:c:\\practica.rvt";

        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "pdi-gate-" + Guid.NewGuid().ToString("N"));

        private static TaskRecord Draft(string id, int n, Dictionary<string, object> extra = null, string docKey = DocKey, Dictionary<string, object> policy = null)
        {
            var args = new Dictionary<string, object> { ["n"] = (long)n };
            if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value;
            return new TaskRecord
            {
                Id = id, Objective = "prueba", RecipeName = "fake", RecipeVersion = "1", RecipeHash = "h", ContentHash = "c-" + id + n,
                Args = args, Docs = new List<TaskDoc> { new TaskDoc { Key = docKey, Title = "practica", Generation = "inst#1" } },
                Policy = policy ?? new Dictionary<string, object>(),
            };
        }

        private static TaskManager Mgr(string root, FakeModel m, ITaskAuthority auth, AuditLog audit = null, string instance = "A") =>
            new TaskManager(new TaskStore(root), instance, t => new FakeBackend(m))
            {
                Delay = (d, ct) => Task.Delay(5, ct), MaxWait = TimeSpan.FromSeconds(20), BudgetMs = 200, Authority = auth, Audit = audit,
            };

        private static async Task<TaskRecord> Run(TaskManager mgr, TaskRecord draft)
        {
            mgr.Start(draft, "// receta", "prueba");
            await mgr.Completion(draft.Id);
            return mgr.Get(draft.Id, out _);
        }

        private static async Task<TaskRecord> Resume(TaskManager mgr, string id)
        {
            var r = mgr.Resume(id, "prueba", null);
            Assert.Null(r.Error);
            await mgr.Completion(id);
            return mgr.Get(id, out _);
        }

        private sealed class AllowAll : ITaskAuthority { public string Check(TaskRecord t, string c, DateTime n) => null; }

        // =========================== R01 auditoría previa ===========================

        [Fact]
        public async Task R01_IntentFails_NoBatchRuns_ModelUntouched()
        {
            var root = TempRoot();
            var audit = new AuditLog(Path.Combine(root, "aud", "a.jsonl")) { FaultBeforeWrite = op => { if (op == "tarea_lote") throw new IOException("disco lleno (simulado)"); } };
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, new AllowAll(), audit), Draft("r01a", 20));
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.StartsWith("auditoria_no_disponible", t.Reason);
            Assert.Equal(0, m.Visits);
            Assert.Empty(m.Live);
        }

        [Fact]
        public async Task R01_OutcomeFails_AfterCommit_IsRecoverable_NoDuplicates()
        {
            var root = TempRoot();
            var path = Path.Combine(root, "aud", "a.jsonl");
            var writes = 0;
            var audit = new AuditLog(path);
            // falla exactamente el primer "resultado" de un lote: la intención ya está, la escritura ocurre, el resultado no llega.
            audit.FaultBeforeWrite = op => { if (op == "tarea_lote" && ++writes == 2) throw new IOException("disco lleno (simulado)"); };
            var m = new FakeModel();
            var mgr = Mgr(root, m, new AllowAll(), audit);
            var t = await Run(mgr, Draft("r01b", 30));
            Assert.Equal(TaskState.Parcial, t.State);
            Assert.StartsWith("auditoria_no_disponible", t.Reason);
            var pending = AuditLog.PendingIntents(path);
            Assert.Single(pending);
            var done = m.Live.Count;
            Assert.True(done > 0, "el lote sí se ejecutó antes del fallo de auditoría");
            mgr.Get("r01b", out var units);
            Assert.Equal(done, units.Units.Values.Count(u => u.State == UnitState.Confirmada)); // está en unidades.jsonl: recuperable
            audit.FaultBeforeWrite = null;
            t = await Resume(mgr, "r01b");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(30, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
            Assert.True(AuditLog.Verify(path).Ok);
        }

        [Fact]
        public async Task R01_EveryBatchAndBarrier_HasIntentAndOutcome()
        {
            var root = TempRoot();
            var path = Path.Combine(root, "aud", "a.jsonl");
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, new AllowAll(), new AuditLog(path)), Draft("r01c", 40, new Dictionary<string, object> { ["barrera_en"] = 20L }));
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Empty(AuditLog.PendingIntents(path));
            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Contains("\"tarea_barrera_pdf\"") && l.Contains("\"intencion\""));
            Assert.True(lines.Count(l => l.Contains("\"estado\":\"intencion\"")) >= 2);
            Assert.True(AuditLog.Verify(path).Ok);
        }

        [Fact]
        public void R01_AuditGate_RefusesToApply_WhenIntentCannotBePersisted()
        {
            var root = TempRoot();
            var audit = new AuditLog(Path.Combine(root, "a.jsonl")) { FaultBeforeWrite = _ => throw new IOException("sin espacio") };
            var applied = 0;
            var r = AuditGate.Apply(audit, "s", "aplicar_cola", "doc", new Dictionary<string, object>(), () => { applied++; return "aplicado"; }, x => x, why => why, (x, why) => why);
            Assert.Equal(0, applied);
            Assert.StartsWith("auditoria_no_disponible", r);
            Assert.Equal(0, AuditGate.Apply<string>(null, "s", "op", "doc", null, () => { applied++; return "x"; }, x => x, why => "rechazado", (x, why) => why) == "rechazado" ? applied : -1);

            // resultado que falla: se aplicó una vez, la intención queda pendiente
            var path2 = Path.Combine(root, "b.jsonl");
            var a2 = new AuditLog(path2);
            var n = 0;
            a2.FaultBeforeWrite = _ => { if (++n == 2) throw new IOException("se cortó"); };
            var r2 = AuditGate.Apply(a2, "s", "aplicar_cola", "doc", new Dictionary<string, object>(), () => { applied++; return "aplicado"; }, x => x, why => why, (x, why) => x + "|" + why);
            Assert.StartsWith("aplicado|" + AuditGate.Incompleta, r2); // rc3: nunca se devuelve como resultado normal
            Assert.Equal(1, applied);
            Assert.Single(AuditLog.PendingIntents(path2));
        }

        // =========================== R02 integridad y concurrencia ===========================

        [Fact]
        public void R02_InvalidChainAtInit_IsQuarantined_NewChainStartsClean()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            for (var i = 0; i < 3; i++) a.Append("s", "op" + i, "ok");
            File.WriteAllText(p, File.ReadAllText(p).Replace("\"op1\"", "\"opX\"")); // alteración
            var b = new AuditLog(p);
            Assert.Single(b.Quarantined);
            Assert.True(File.Exists(b.Quarantined[0]));
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok);
            Assert.Equal(1, v.Lines);
            Assert.Contains("\"cuarentena\"", File.ReadAllText(p));
            b.Append("s", "despues", "ok");
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void R02_TruncatedTailFromAnotherWriter_IsQuarantinedOnNextAppend()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            File.AppendAllText(p, "{\"seq\":2,\"t\":\"cort"); // otro proceso murió a mitad de línea
            a.Append("s", "op2", "ok");
            Assert.Single(a.Quarantined);
            Assert.True(AuditLog.Verify(p).Ok);
        }

        [Fact]
        public void R02_WriteFailure_IsVisible_ChainStaysValid()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            var a = new AuditLog(p);
            a.Append("s", "op", "ok");
            a.FaultBeforeWrite = _ => throw new IOException("disco lleno");
            Assert.Throws<JournalUnavailableException>(() => a.Append("s", "op", "ok"));
            Assert.Equal(1, a.Failures);
            a.FaultBeforeWrite = null;
            a.Append("s", "op", "ok");
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join(";", v.Errors));
            Assert.Equal(2, v.Lines);
        }

        [Fact]
        public void R02_EightRealProcesses_AppendConcurrently_ChainIsConsistent()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var p = Path.Combine(root, "a.jsonl");
            new AuditLog(p).Append("s", "genesis", "ok");
            var start = DateTime.UtcNow.AddSeconds(4).Ticks.ToString();
            var procs = Enumerable.Range(0, 8).Select(i => LeaseFencingTests.StartProbe(p, "50", "proc" + i, "audit", start)).ToList();
            foreach (var pr in procs) { var o = pr.StandardOutput.ReadToEnd(); Assert.True(pr.WaitForExit(120000)); Assert.Contains("APPENDED 50", o); pr.Dispose(); }
            var v = AuditLog.Verify(p);
            Assert.True(v.Ok, string.Join("; ", v.Errors.Take(5)));
            Assert.Equal(1 + 8 * 50, v.Lines);
            Assert.Empty(Directory.GetFiles(root, "*.cuarentena-*"));
        }

        // =========================== R03 autoridad independiente ===========================

        private static (GrantStore store, GrantAuthority auth) Grants(string root)
        {
            var g = new GrantStore(Path.Combine(root, "autorizaciones"));
            return (g, new GrantAuthority(g));
        }

        [Fact]
        public async Task R03_AgentRequestAlone_DoesNotAuthorize_NothingRuns()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            // el agente "pide" capacidades: queda como solicitud y como política solicitada, no como permiso
            g.Request("r03a", DocKey, null, "practica", new[] { Capability.Modificar }, "agente", "x", DateTime.UtcNow);
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03a", 10, policy: new Dictionary<string, object>
            {
                ["solicitadas"] = new List<object> { Capability.Modificar }, ["capacidades"] = new List<object> { "all" }, ["aprobado"] = true,
            }));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.StartsWith("autorizacion_requerida", t.Reason);
            Assert.Equal(0, m.Visits);
            Assert.Single(g.PendingRequests());
        }

        [Fact]
        public async Task R03_DefaultAuthority_DeniesWrites()
        {
            var m = new FakeModel();
            var mgr = new TaskManager(new TaskStore(TempRoot()), "A", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct) };
            var t = await Run(mgr, Draft("r03b", 5));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Equal(0, m.Visits);
        }

        [Fact]
        public async Task R03_Grant_IsBoundToTaskAndDocument()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("otra-tarea", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            g.Approve("r03c", "archivo:c:\\otro.rvt", null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel();
            var mgr = Mgr(root, m, auth);
            var t = await Run(mgr, Draft("r03c", 5));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("otro documento", t.Reason);
            Assert.Equal(0, m.Visits);
            g.Approve("r03c", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            t = await Resume(mgr, "r03c");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(5, m.Live.Count);
        }

        [Fact]
        public async Task R03_RevocationMidRun_StopsBeforeNextUnit()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            var grant = g.Approve("r03d", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel { MsPerUnit = 50 };
            var countAtRevoke = -1;
            m.OnVisit = v => { if (v == 2) { g.Revoke(grant.Id, DateTime.UtcNow); countAtRevoke = m.Live.Count; } };
            var mgr = Mgr(root, m, auth);
            var t = await Run(mgr, Draft("r03d", 200));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("revocada", t.Reason);
            // la caché de autoridad del último punto seguro dura ≤2 s: como mucho se termina la visita en curso
            Assert.True(m.Live.Count < 200);
            Assert.True(m.Visits <= 3, "visitas tras revocar: " + m.Visits);
        }

        [Fact]
        public async Task R03_ExpiredGrant_IsNotReused()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r03e", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromMilliseconds(1), "persona", DateTime.UtcNow.AddMinutes(-5));
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03e", 5));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("venció", t.Reason);
            Assert.Equal(0, m.Visits);
        }

        [Fact]
        public void R03_ForgedOrEditedGrant_IsIgnored_AndNonGrantableIsRefused()
        {
            var root = TempRoot();
            var (g, _) = Grants(root);
            var ok = g.Approve("t", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var f = Path.Combine(g.Dir, "c_" + ok.Id + ".json");
            File.WriteAllText(f, File.ReadAllText(f).Replace("\"revit_modificar\"", "\"revit_sync\""));
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Sync, DateTime.UtcNow));
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Modificar, DateTime.UtcNow)); // alterada → ignorada entera
            File.WriteAllText(Path.Combine(g.Dir, "c_falsa.json"), "{\"id\":\"falsa\",\"tarea\":\"t\",\"doc\":\"" + DocKey.Replace("\\", "\\\\") + "\",\"capacidades\":[\"revit_modificar\"],\"vence\":\"2099-01-01T00:00:00Z\",\"firma\":\"00\"}");
            Assert.NotNull(g.Check("t", DocKey, null, Capability.Modificar, DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => g.Approve("t", DocKey, null, new[] { "proceso" }, TimeSpan.FromHours(1), "x", DateTime.UtcNow));
        }

        [Fact]
        public async Task R03_BarrierWithoutItsOwnCapability_DoesNotRun()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r03g", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow); // sin revit_exportar
            var m = new FakeModel();
            var t = await Run(Mgr(root, m, auth), Draft("r03g", 10, new Dictionary<string, object> { ["barrera_en"] = 5L }));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains(Capability.Exportar, t.Reason);
            Assert.Equal(0, m.Barriers);
            Assert.Equal(5, m.Live.Count); // lo anterior a la barrera sí (autorizado), la barrera y lo posterior no
        }

        [Fact]
        public async Task R03_ReadOnlyTask_RunsWithoutGrant_UnderDefaultDeny()
        {
            var m = new FakeModel();
            var t = await Run(Mgr(TempRoot(), m, DenyWritesAuthority.Instance), Draft("ro", 5, policy: new Dictionary<string, object> { ["requiere"] = new List<object>() }));
            Assert.Equal(TaskState.Aplicada, t.State);
        }

        // =========================== R04 Roslyn y DLL ===========================

        [Fact]
        public void R04_ExecBoundary_WritesNeedTrustedSession_QueriesAreRestricted()
        {
            Assert.StartsWith("codigo_confiable_requerido", CapabilityPolicy.ExecDecision(true, false, new string[0], out var p1));
            Assert.Equal(ExecutionProfile.RestrictedAutonomous, p1);
            Assert.Null(CapabilityPolicy.ExecDecision(false, false, new string[0], out _));
            Assert.NotNull(CapabilityPolicy.ExecDecision(false, false, new[] { "proceso:System.Diagnostics.Process.Start" }, out _));
            Assert.NotNull(CapabilityPolicy.ExecDecision(false, false, new[] { "archivo_escritura:System.IO.File.WriteAllText" }, out _));
            Assert.Null(CapabilityPolicy.ExecDecision(true, true, new[] { "proceso:System.Diagnostics.Process.Start" }, out var p2));
            Assert.Equal(ExecutionProfile.TrustedInteractive, p2);
        }

        [Fact]
        public void R04_ExternalReferences_DecidedBeforeLoading()
        {
            var root = TempRoot(); Directory.CreateDirectory(root);
            var addin = Path.Combine(root, "addin"); Directory.CreateDirectory(addin);
            var inside = Path.Combine(addin, "Ayuda.dll"); File.WriteAllText(inside, "x");
            var outside = Path.Combine(root, "Extraña.dll"); File.WriteAllText(outside, "contenido-malicioso");
            var known = Path.Combine(root, "Conocida.dll"); File.WriteAllText(known, "contenido-conocido");
            Func<string, string> sha = f => Home.FileSha256(f);
            var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Home.FileSha256(known) };
            Assert.Empty(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { inside, known }, sha, trusted, addin));
            var d = ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { outside }, sha, trusted, addin);
            Assert.Single(d);
            // traversal: una ruta que empieza como la carpeta del add-in pero sale de ella
            Assert.Single(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { Path.Combine(addin, "..", "Extraña.dll") }, sha, trusted, addin));
            // misma ruta, contenido cambiado → ya no es la de confianza
            File.WriteAllText(known, "contenido-cambiado");
            Assert.Single(ReferencePolicy.Denied(ExecutionProfile.RestrictedAutonomous, new[] { known }, sha, trusted, addin));
            Assert.Empty(ReferencePolicy.Denied(ExecutionProfile.TrustedInteractive, new[] { outside }, sha, trusted, addin));
        }

        [Theory, InlineData(2026)]
        public void R04_EvasionAttempts_AreDetected(int year)
        {
            var cases = new[]
            {
                "var t = typeof(object).Assembly.GetType(\"System.Diagnostics.Process\"); return t;",
                "var m = typeof(string).GetMethod(\"Copy\"); return m;",
                "System.Func<int> f = () => 1; return f.DynamicInvoke();",
                "var e = (System.Linq.Expressions.Expression<System.Func<int>>)(() => 1); return e.Compile();",
                "return System.AppDomain.CurrentDomain.GetAssemblies().Length;",
                "System.Threading.Tasks.Task.Run(() => 1); return 1;",
                "var p = new System.IO.Pipes.NamedPipeClientStream(\"x\"); return p;",
                "dynamic d = doc; return d.Title;",
            };
            foreach (var c in cases)
            {
                var caps = ScriptingTests.CapsOf(c, year);
                Assert.True(CapabilityPolicy.Denied(ExecutionProfile.RestrictedAutonomous, caps).Count > 0, "no detectado: " + c);
            }
        }

        [Fact]
        public async Task R04_NonInstalledRecipe_EvenReadOnly_NeedsHumanApproval()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            var m = new FakeModel();
            var mgr = Mgr(root, m, auth);
            // lo que el add-in escribe en la política para codigo= en línea de sólo lectura
            var t = await Run(mgr, Draft("r04x", 5, policy: new Dictionary<string, object> { ["requiere"] = new List<object> { Capability.RecetaExterna } }));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains(Capability.RecetaExterna, t.Reason);
            Assert.Equal(0, m.Visits);
            g.Approve("r04x", DocKey, null, new[] { Capability.RecetaExterna }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            t = await Resume(mgr, "r04x");
            Assert.Equal(TaskState.Aplicada, t.State);
        }

        // =========================== R05 reconciliación ===========================

        [Fact]
        public async Task R05_ConfirmedUnits_CannotBeReconciledBackToPending()
        {
            var root = TempRoot();
            var m = new FakeModel { CrashAfterCommitAt = "u0003" };
            m.Unverifiable.Add("u0003");
            var mgr = Mgr(root, m, new AllowAll());
            var t = await Run(mgr, Draft("r05a", 6));
            mgr = Mgr(root, m, new AllowAll()); // "Revit se cayó": otro motor
            t = await Resume(mgr, "r05a");
            mgr.Get("r05a", out var units);
            Assert.Equal(UnitState.Desconocida, units.Get("u0003").State);
            Assert.Equal(UnitState.Confirmada, units.Get("u0001").State);

            var err = mgr.Reconcile("r05a", new List<string> { "u0001" }, applied: false, note: "quiero repetirla", who: "agente");
            Assert.StartsWith("transicion_ilegal", err);
            Assert.StartsWith("evidencia_requerida", mgr.Reconcile("r05a", new List<string> { "u0003" }, applied: true, note: " ", who: "agente"));
            mgr.Get("r05a", out units);
            Assert.Equal(UnitState.Confirmada, units.Get("u0001").State);
        }

        [Fact]
        public async Task R05_NotAppliedDecision_NeedsHumanGrant_ThenRunsExactlyOnce()
        {
            var root = TempRoot();
            var (g, auth) = Grants(root);
            g.Approve("r05b", DocKey, null, new[] { Capability.Modificar }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            var m = new FakeModel { CrashAfterCommitAt = "u0002" };
            m.Unverifiable.Add("u0002");
            var mgr = Mgr(root, m, auth);
            await Run(mgr, Draft("r05b", 4));
            mgr = Mgr(root, m, auth);
            var t = await Resume(mgr, "r05b");
            Assert.True(t.State == TaskState.Interrumpida && t.Reason.Contains("no se pudieron verificar"), t.State + ": " + t.Reason);
            Assert.StartsWith("autorizacion_requerida", mgr.Reconcile("r05b", null, applied: false, note: "no está en el modelo (consulta id 123)", who: "agente"));
            g.Approve("r05b", DocKey, null, new[] { Capability.ReconciliarNoAplicada }, TimeSpan.FromHours(1), "persona", DateTime.UtcNow);
            m.Live.Remove("u0002"); // la persona comprobó que no quedó
            Assert.Null(mgr.Reconcile("r05b", null, applied: false, note: "no está en el modelo (consulta id 123)", who: "agente"));
            m.Unverifiable.Clear();
            t = await Resume(mgr, "r05b");
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.Equal(4, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }

        [Fact]
        public async Task R05_FinishedTask_IsNotReopened_AndHistoryIsKept()
        {
            var root = TempRoot();
            var m = new FakeModel();
            var mgr = Mgr(root, m, new AllowAll());
            var t = await Run(mgr, Draft("r05c", 3));
            Assert.Equal(TaskState.Aplicada, t.State);
            Assert.StartsWith("transicion_ilegal", mgr.Reconcile("r05c", new List<string> { "u0001" }, applied: false, note: "x", who: "agente"));
            var lines = File.ReadAllLines(Path.Combine(root, "r05c", "unidades.jsonl")).Length;
            Assert.True(lines >= 4);
        }

        [Fact]
        public void R05_HandWrittenIllegalEvent_IsIgnoredOnReplay()
        {
            var log = new UnitLog();
            log.Apply(new Dictionary<string, object> { ["ev"] = "inicio", ["claves"] = new List<object> { "a" } });
            log.Apply(new Dictionary<string, object> { ["ev"] = "fin", ["clave"] = "a", ["estado"] = UnitState.Confirmada, ["seq"] = 1L });
            log.Apply(new Dictionary<string, object> { ["ev"] = "reconciliada", ["clave"] = "a", ["estado"] = UnitState.Pendiente });
            Assert.Equal(UnitState.Confirmada, log.Get("a").State);
            Assert.Equal(1, log.IllegalEvents);
        }

        // =========================== R06 autoridad y documento ===========================

        [Fact]
        public async Task R06_TwoTasksSameDocument_SecondDoesNotRun()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 20 };
            var second = (TaskRecord)null;
            var mgrB = Mgr(root, new FakeModel(), new AllowAll(), instance: "B");
            m.OnVisit = v => { if (v == 2 && second == null) { mgrB.Start(Draft("r06b", 3), "// r", "otro"); second = mgrB.Completion("r06b").ContinueWith(_ => mgrB.Get("r06b", out var __)).Result; } };
            var mgrA = Mgr(root, m, new AllowAll(), instance: "A");
            var a = await Run(mgrA, Draft("r06a", 100));
            Assert.Equal(TaskState.Aplicada, a.State);
            Assert.NotNull(second);
            Assert.Contains("documento lo está modificando", second.Reason);
            // al terminar la primera, la segunda puede seguir
            var b = await Resume(mgrB, "r06b");
            Assert.Equal(TaskState.Aplicada, b.State);
        }

        [Fact]
        public async Task R06_DifferentDocuments_RunInParallel()
        {
            var root = TempRoot();
            var m1 = new FakeModel(); var m2 = new FakeModel();
            var t1 = Run(Mgr(root, m1, new AllowAll(), instance: "A"), Draft("d1", 50));
            var t2 = Run(Mgr(root, m2, new AllowAll(), instance: "B"), Draft("d2", 50, docKey: "archivo:c:\\otro.rvt"));
            Assert.Equal(TaskState.Aplicada, (await t1).State);
            Assert.Equal(TaskState.Aplicada, (await t2).State);
        }

        [Fact]
        public async Task R06_LeaseStolenDuringRevitWait_NoUnitStartsAfterwards()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 10 };
            var thief = new TaskStore(root);
            m.OnVisit = v =>
            {
                // la visita ya entró al "hilo de Revit" (lote validado), pero antes de la primera transacción otro proceso toma la tarea
                if (v == 2) Assert.True(thief.TryLease("r06c", "OTRA", "B", TimeSpan.FromHours(3), DateTime.UtcNow.AddHours(2), out _));
            };
            var t = await Run(Mgr(root, m, new AllowAll()), Draft("r06c", 200));
            Assert.Equal(TaskState.Interrumpida, t.State);
            Assert.Contains("lease_perdido", m.PreflightRefusals);
            var afterSteal = m.Live.Count;
            Assert.True(afterSteal > 0 && afterSteal < 200);
            Assert.Equal(0, m.Duplicates);
        }

        // =========================== R07 checkpoints ===========================

        [Fact]
        public async Task R07_CheckpointFailure_StopsTask_ResumeIsConsistent()
        {
            var root = TempRoot();
            var m = new FakeModel { MsPerUnit = 20 };
            var store = new TaskStore(root);
            var saves = 0;
            var failing = true;
            store.FaultBeforeSave = id => { if (failing && ++saves == 6) throw new IOException("sin espacio (simulado)"); };
            var mgr = new TaskManager(store, "A", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct), Authority = new AllowAll(), BudgetMs = 200 };
            mgr.Start(Draft("r07", 300), "// r", "p");
            await mgr.Completion("r07");
            var mem = mgr.Get("r07", out _);
            Assert.NotEqual(TaskState.Aplicada, mem.State);
            Assert.True(m.Live.Count < 300, "el motor no siguió después de un checkpoint fallido");
            var executed = m.Live.Count;
            failing = false;
            var mgr2 = new TaskManager(new TaskStore(root), "B", t => new FakeBackend(m)) { Delay = (d, ct) => Task.Delay(5, ct), Authority = new AllowAll(), BudgetMs = 200 };
            var t2 = await Resume(mgr2, "r07");
            Assert.Equal(TaskState.Aplicada, t2.State);
            Assert.Equal(300, m.Live.Count);
            Assert.Equal(0, m.Duplicates);
        }
    }
}
