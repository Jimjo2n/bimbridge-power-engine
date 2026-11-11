// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine.Workflows;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1 (Bloque F): contratos veraces de flujos nativos interactivos (#23), verificación de Copy/Monitor (#24) y destino estable y
    /// diagnóstico de conexión (#25). Todo sintético: ningún modelo real, ninguna ruta privada, sin Revit.
    /// </summary>
    public class NativeWorkflowTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb_wf_" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static Evidence Ev(string kind, ProofLevel lvl = ProofLevel.observed, string detail = null) =>
            new Evidence { Kind = kind, Level = lvl, Source = "test", Detail = detail, AtUtc = DateTime.UtcNow };

        private static WorkflowCorrelation Corr(string doc = "doc-A") => new WorkflowCorrelation { AgentId = "a1", SessionId = "s1", RequestId = "r1", TraceId = "t1", JobId = "j1", DocumentIdentity = doc, LinkIdentity = "link-1", RevitVersion = "2026" };

        private ReceiptStore Store() => new ReceiptStore(_dir);

        // ------------------------------------------------------------------ #23 recibos y estados

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
            // Saltar a "terminado" o "verificado" sin la evidencia intermedia no es posible.
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
            // La verificación con nivel "observed" no basta.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.observed)));
            st.Record(key, Ev(EvidenceKind.VerificationPassed, ProofLevel.verified));
            var r = st.Record(key, Ev(EvidenceKind.SaveConfirmed, ProofLevel.verified));
            Assert.Equal(WorkflowState.save_confirmed, r.State);
            Assert.True(r.IsTerminal);
            Assert.Equal(ProofLevel.verified, r.Proof);
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.PostCommandAccepted)));
        }

        [Theory]
        [InlineData(1)]   // tras aceptar la orden, antes de la copia por lotes
        [InlineData(5)]   // con la copia iniciada
        [InlineData(6)]   // con la copia observada, antes de Finalizar
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

            // Reabrir el intento (reintento del cliente / reinicio) devuelve el mismo recibo: no se duplica la orden.
            var again = new ReceiptStore(_dir).Begin("copy_monitor_fixtures", key, Corr(), out var created);
            Assert.False(created);
            Assert.Equal(WorkflowState.reconciliation_required, again.State);

            // Una reconciliación no puede declarar guardado ni verificación.
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "save_confirmed")));
            Assert.Throws<InvalidOperationException>(() => st.Record(key, Ev(EvidenceKind.Reconciled, ProofLevel.observed, "verification_passed")));
            if (stepsBeforeFault >= 5)
            {
                // Con indicios de copia no se vuelve a preflight (eso permitiría repetir la copia).
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
            Assert.Throws<InvalidOperationException>(() => st.Begin("otro_flujo", key, Corr("doc-A"), out _));
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
            Assert.Equal(new[] { k2 }, open.Select(r => r.AttemptKey).ToArray());
            Assert.Single(unreadable);
            var steps = NativeCapabilities.CopyMonitorManualSteps("MEP-LINK", new[] { "WC-A → WC-B" }, saveAuthorized: false);
            Assert.Contains(steps, s => s.Contains("Copia por lotes"));
            Assert.Contains(steps, s => s.Contains("No guarde"));
        }

        // ------------------------------------------------------------------ #23 matriz de capacidades

        [Theory]
        [InlineData(2022)] [InlineData(2023)] [InlineData(2024)] [InlineData(2025)] [InlineData(2026)]
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
            Assert.Equal("bimbridge.native_capability/1", ui.ToJson()["esquema"]);
        }

        [Fact]
        public void Capabilities_RejectUnknownOperationsAndVersions()
        {
            Assert.Throws<ArgumentException>(() => NativeCapabilities.Describe("batch_magic", 2026, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, 2021, null));
        }

        // ------------------------------------------------------------------ #25 destino estable y diagnóstico

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
            Assert.Contains("2 instancias", why);
            Assert.Null(TargetResolver.Resolve(list, "s1", "2026", null, null, out why));
            Assert.Contains("documentos abiertos", why);
            Assert.Null(TargetResolver.Resolve(list, "s2", "2026", null, "d2", out why));
            Assert.Contains("otra sesión", why);
            var b = TargetResolver.Resolve(list, "s1", "2026", null, "d2", out why);
            Assert.NotNull(b);
            Assert.Equal("Z", b.DocumentIdentity);
            Assert.True(TargetResolver.StillValid(b, list, out _));

            // Revit 2026 se reinicia y el sistema reutiliza el pid 200: el destino ya no es el mismo.
            var restarted = new List<RevitInstance> { list[0], Inst(200, "2026", t0.AddMinutes(30), ("d1", "s1", "Z")) };
            Assert.False(TargetResolver.StillValid(b, restarted, out why));
            Assert.Contains("pid 200", why);
            // El documento se cierra.
            var closed = new List<RevitInstance> { list[0], Inst(200, "2026", t0, ("d1", "s1", "Y")) };
            Assert.False(TargetResolver.StillValid(b, closed, out why));
        }

        [Fact]
        public void Diagnostics_SeparateClientRegistrationFromRevitAvailability()
        {
            var healthyBridgeHiddenTools = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, ToolsVisibleToAgent = 0, Instances = new List<RevitInstance>() };
            var r = ConnectionDiagnostics.Report(healthyBridgeHiddenTools, "0.9.1+abc");
            Assert.Equal("herramientas_no_expuestas_al_agente", r["veredicto"]);
            Assert.Equal("0.9.1+abc", r["build"]);

            var noClient = new ConnectionObservations { BridgeInstalled = true };
            noClient.ClientRegistrations["claude"] = false;
            Assert.Equal("cliente_sin_registro", ConnectionDiagnostics.Report(noClient, "b")["veredicto"]);

            var noRevit = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, ToolsVisibleToAgent = 6, Instances = new List<RevitInstance>() };
            Assert.Equal("revit_no_conectado", ConnectionDiagnostics.Report(noRevit, "b")["veredicto"]);

            var t = DateTime.UtcNow;
            var two = new ConnectionObservations { BridgeInstalled = true, HelloOk = true, ToolsListed = 6, Instances = new List<RevitInstance> { Inst(1, "2024", t), Inst(2, "2026", t) } };
            var rep = ConnectionDiagnostics.Report(two, "b");
            Assert.Equal("varias_instancias", rep["veredicto"]);
            Assert.Contains(((List<object>)rep["hallazgos"]).Cast<Dictionary<string, object>>(), f => (string)f["codigo"] == "sin_ejecutor_ui");
        }

        // ------------------------------------------------------------------ #24 verificador Copy/Monitor (aparatos sintéticos)

        /// <summary>Fixture sintético redistribuible: 88 aparatos en 5 tipos (40/2/20/20/6), rejilla regular, rotaciones variadas.</summary>
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
                return new HostFixture { Id = id++, Category = s.Category, Family = parts[0], Type = parts[1], Level = s.Level, X = p.X, Y = p.Y, Z = p.Z, RotationDeg = s.RotationDeg + tf.RotationDeg, Monitoring = true, MonitoredLinkInstances = new List<long> { link } };
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
                Assert.Equal("verificado", r["resultado"]);
                Assert.Equal(88L, ((Dictionary<string, object>)r["correctos"])["n"]);
                Assert.Equal("inferred", r["nivel_prueba_correspondencia"]);
            }
        }

        [Fact]
        public void PlainCopyWithoutMonitoring_Fails()
        {
            var src = Synthetic88();
            var host = PerfectCopy(src, LinkTransform.Identity, 77);
            foreach (var h in host) { h.Monitoring = false; h.MonitoredLinkInstances.Clear(); }
            var r = CopyMonitorVerifier.Verify(src, null, host, null, null, 77);
            Assert.Equal("fallido", r["resultado"]);
            Assert.Equal(88L, ((Dictionary<string, object>)r["sin_supervision"])["n"]);
        }

        [Fact]
        public void SeededDefects_AreEachReportedInTheirOwnBucket()
        {
            var src = Synthetic88();
            var host = PerfectCopy(src, LinkTransform.Identity, 77);
            host.RemoveAt(0);                                                    // ausente
            host[1].Type = "B";                                                  // tipo incorrecto (WC A → WC B)
            host[2].Monitoring = false;                                          // sin supervisión
            host[3].RotationDeg += 90;                                           // girado
            host[4].X += 0.5;                                                    // desplazado
            var dup = host[10];
            host.Add(new HostFixture { Id = 9999, Family = dup.Family, Type = dup.Type, X = dup.X, Y = dup.Y, Z = dup.Z, RotationDeg = dup.RotationDeg, Monitoring = true, MonitoredLinkInstances = new List<long> { 77 } }); // duplicado
            var existing = new HostFixture { Id = 1, Family = "WC", Type = "A", X = -50, Y = -50 };
            host.Add(existing);                                                  // ya estaba antes: no cuenta
            var r = CopyMonitorVerifier.Verify(src, new List<HostFixture> { existing }, host, null, null, 77);
            long N(string b) => (long)((Dictionary<string, object>)r[b])["n"];
            Assert.Equal("fallido", r["resultado"]);
            Assert.Equal(1L, N("ausentes"));
            Assert.Equal(1L, N("tipo_incorrecto"));
            Assert.Equal(1L, N("sin_supervision"));
            Assert.Equal(2L, N("mal_colocados"));
            Assert.Equal(1L, N("duplicados"));
            Assert.Equal(0L, N("inesperados"));
            Assert.Equal(83L, N("correctos"));
        }

        [Fact]
        public void TypeMappingAndExclusions_AreHonoured()
        {
            var src = Synthetic88();
            var maps = new List<TypeMapping> { new TypeMapping { Source = "WC: B", Target = "WC: A" }, new TypeMapping { Source = "Ducha: E", Excluded = true } };
            var kept = src.Where(s => !(s.Family == "Ducha")).ToList();
            var host = PerfectCopy(kept, LinkTransform.Identity, 77, s => s.Family == "WC" && s.Type == "B" ? "WC: A" : null);
            var r = CopyMonitorVerifier.Verify(src, null, host, null, maps, 77);
            Assert.Equal("verificado", r["resultado"]);
            Assert.Equal(82L, r["esperados"]);
            Assert.Equal(6L, ((Dictionary<string, object>)r["excluidos"])["n"]);

            // Sin aplicar el mapeo (copió el tipo original): tipo incorrecto en los 2 WC B.
            var unmapped = PerfectCopy(kept, LinkTransform.Identity, 77);
            var r2 = CopyMonitorVerifier.Verify(src, null, unmapped, null, maps, 77);
            Assert.Equal(2L, ((Dictionary<string, object>)r2["tipo_incorrecto"])["n"]);
        }
    }
}
