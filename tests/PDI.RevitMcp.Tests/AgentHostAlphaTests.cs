// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.7.0-alpha1 en proceso: IR v2, validación del estado antes de recuperar (AH-03), exclusión y fencing (AH-04), conjunto vacío
    /// (AH-05), cancelación, repetición y huella del documento. Cada "reinicio" crea objetos nuevos desde disco (modelo incluido).
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
            var state = Path.Combine(dir, "estado");
            using (var r = Open(state, plan, model, out var d)) { Assert.NotNull(r); r.Run(); }
            return (state, model, plan);
        }

        // ---------------- IR v2 ----------------

        [Fact]
        public void IR_IdIncludesDocumentFingerprintAndSource_AndIsKeyOrderIndependent()
        {
            var sc1 = new Dictionary<string, object> { ["categoria"] = "X", ["nivel"] = "L3" };
            var sc2 = new Dictionary<string, object> { ["nivel"] = "L3", ["categoria"] = "X" };
            var p1 = new Dictionary<string, object> { ["valor"] = 1L, ["parametro"] = "A" };
            var p2 = new Dictionary<string, object> { ["parametro"] = "A", ["valor"] = 1.0 };
            var a = IrIds.OpId(AH.Doc, AH.Print, sc1, "fijar_parametro", p1, "s|REQ-1|3");
            Assert.Equal(a, IrIds.OpId(AH.Doc, AH.Print, sc2, "fijar_parametro", p2, "s|REQ-1|3"));
            Assert.NotEqual(a, IrIds.OpId(AH.Doc, "huella-de-un-modelo-sustituido", sc1, "fijar_parametro", p1, "s|REQ-1|3"));
            Assert.NotEqual(a, IrIds.OpId(AH.Doc, AH.Print, sc1, "fijar_parametro", p1, "s|REQ-2|3"));
        }

        [Fact]
        public void IR_ValidatedPlanIsImmutable_AndSurvivesJsonRoundTrip()
        {
            var p = AH.BasePlan();
            Assert.Empty(IrValidator.Validate(PlanDraft.Parse(Json.Write(p.ToJson())), out var vp));
            Assert.Equal(AH.Hash(p), vp.Hash);
            var op = vp.Ops[1];
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)op.Params)["valor"] = 1L);
            Assert.Equal(new[] { "escritura_modelo" }, op.Authorization);          // derivado, no declarado
            Assert.Equal("convergente", op.Idempotency);
            Assert.NotEmpty(op.Postconditions);
            // mutar el borrador después de validar no cambia el plan validado
            p.Ops[1].Params["valor"] = 9999L;
            Assert.Equal(2700L, vp.Ops[1].Params["valor"]);
        }

        [Fact]
        public void IR_RejectsAgentDeclaredSystemFields_ImplicitUnits_DuplicatesAndBadSources()
        {
            var p = AH.BasePlan();
            var json = p.ToJson();
            json["autorizacion"] = new List<object> { "todo" };
            json["coordenadas"] = null;
            var ops = (List<object>)json["operaciones"];
            var set = (Dictionary<string, object>)ops[1];
            set["estado"] = "verificada";                    // el agente no fija estados
            set["autorizacion"] = new List<object> { "revit_modificar" };
            ((Dictionary<string, object>)set["parametros"]).Remove("unidades");
            set["fuente"] = new Dictionary<string, object> { ["fuente"] = "no-declarada", ["requisito"] = "REQ-9" };
            ops.Add(new Dictionary<string, object>(set) { ["id"] = "op-copia" });   // duplicado semántico
            var errs = IrValidator.Validate(PlanDraft.FromJson(json));
            Assert.Contains(errs, e => e.Contains("campo_no_declarable: plan.autorizacion"));
            Assert.Contains(errs, e => e.Contains("campo_no_declarable estado"));
            Assert.Contains(errs, e => e.Contains("campo_no_declarable autorizacion"));
            Assert.Contains(errs, e => e.Contains("coordenadas explícitas"));
            Assert.Contains(errs, e => e.Contains("unidades_no_explicitas"));
            Assert.Contains(errs, e => e.Contains("no declarada en el plan"));
            Assert.Contains(errs, e => e.Contains("duplicado_semantico"));
            Assert.Contains(errs, e => e.Contains("id_no_determinista"));
        }

        [Fact]
        public void IR_DependencyCyclesAreRejected_WithTheirPath()
        {
            var p = AH.Plan(AH.Set("A", 1L), AH.Set("B", 2L, deps: new[] { "#0" }), AH.Set("C", 3L, deps: new[] { "#1" }));
            p.Ops[0].DependsOn.Add(p.Ops[2].Id);   // A → C → B → A
            var errs = IrValidator.Validate(p);
            Assert.Contains(errs, e => e.StartsWith("ciclo:") && e.Contains(p.Ops[0].Id) && e.Contains(p.Ops[2].Id));
        }

        [Fact]
        public void IR_OldSchemaVersion1_IsRejectedWithoutBestEffort()
        {
            var json = AH.BasePlan().ToJson();
            json["ir"] = 1L;
            Assert.Contains(IrValidator.Validate(PlanDraft.FromJson(json)), e => e.StartsWith("version_no_soportada"));
        }

        // ---------------- huella del documento ----------------

        [Fact]
        public void DocumentFingerprintMismatch_RefusesBeforeAnyExecution()
        {
            var dir = AH.NewDir("huella");
            var m = Path.Combine(dir, "modelo");
            DurableSimModel.Create(m, AH.Doc, "huella-OTRO-modelo", new[] { AH.Cat + "=5@L03" });
            var r = Open(Path.Combine(dir, "estado"), AH.BasePlan(), m, out var diags);
            Assert.Null(r);
            Assert.Contains(diags, d => d.Code == ErrorClass.DocumentoDistinto);
            Assert.False(File.Exists(Path.Combine(dir, "estado", "diario.jsonl")));
        }

        // ---------------- AH-03 ----------------

        private static void AssertRefused(string state, PlanDraft plan, string model, string code)
        {
            var r = Open(state, plan, model, out var diags);
            Assert.True(r == null, "la recuperación debía rechazarse; diags: " + string.Join("; ", diags));
            Assert.Contains(diags, d => d.Severity == "error" && d.Code == code);
            Assert.Null(PlanLease.Peek(state) is var l && l != null && !l.Released ? "lease retenido" : null);
        }

        [Fact]
        public void AH03_TornLastJournalLine_IsTrimmed_AndRunContinues()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "diario.jsonl");
            File.AppendAllText(j, "{\"ev\":\"transicion\",\"op\":\"op-x\",\"de\":\"valid");   // caída a mitad de escritura
            using (var r = Open(state, plan, model, out var diags))
            {
                Assert.NotNull(r);
                Assert.Contains(diags, d => d.Code == "diario_linea_final_truncada");
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verificada, o.State));
            }
            Assert.EndsWith("\n", File.ReadAllText(j));
        }

        [Fact]
        public void AH03_CorruptedMiddleRecord_RefusesRecovery()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "diario.jsonl");
            var lines = File.ReadAllLines(j);
            var i = Array.FindIndex(lines, l => l.Contains("\"a\":\"verificada\""));
            lines[i] = lines[i].Replace("\"a\":\"verificada\"", "\"a\":\"fallida\"");
            File.WriteAllLines(j, lines);
            AssertRefused(state, plan, model, "diario_hash_invalido");
        }

        [Fact]
        public void AH03_TamperedCheckpoint_IsDiscarded_AndRebuiltFromJournal()
        {
            var (state, model, plan) = RunToEnd();
            var cp = Path.Combine(state, "estado.json");
            File.WriteAllText(cp, File.ReadAllText(cp).Replace("\"verificada\"", "\"validada\""));
            using (var r = Open(state, plan, model, out var diags))
            {
                Assert.NotNull(r);
                Assert.Contains(diags, d => d.Code == "checkpoint_alterado");
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verificada, o.State));
            }
        }

        /// <summary>Un checkpoint con sello VÁLIDO que certifica algo que el diario no dice: se rechaza, no se cree.</summary>
        [Fact]
        public void AH03_ForgedCheckpointClaimingCompletion_IsRejected()
        {
            var dir = AH.NewDir("forja");
            var plan = AH.BasePlan();
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            using (var r = Open(state, plan, model, out _, new Faults().Configure("runner.tras_ejecutar@" + plan.Ops[1].Id, "excepcion")))
                Assert.Throws<CrashException>(() => r.Run());
            var cpPath = Path.Combine(state, "estado.json");
            var cp = Json.ParseObject(File.ReadAllText(cpPath));
            var ops = (Dictionary<string, object>)cp["ops"];
            foreach (var k in ops.Keys.ToList()) ((Dictionary<string, object>)ops[k])["estado"] = "verificada";
            cp.Remove("sello");
            cp["sello"] = Home.Sha256Hex(OpSemantics.Canonical(cp), 32);
            File.WriteAllText(cpPath, Json.Write(cp));
            AssertRefused(state, plan, model, "checkpoint_contradice_diario");
        }

        [Fact]
        public void AH03_CheckpointAheadOfJournal_IsRejected()
        {
            var (state, model, plan) = RunToEnd();
            var j = Path.Combine(state, "diario.jsonl");
            var lines = File.ReadAllLines(j);
            File.WriteAllLines(j, lines.Take(lines.Length - 3));   // se pierden registros confirmados
            AssertRefused(state, plan, model, "checkpoint_adelantado");
        }

        [Fact]
        public void AH03_MissingJournalWithCheckpoint_IsRejected()
        {
            var (state, model, plan) = RunToEnd();
            File.Delete(Path.Combine(state, "diario.jsonl"));
            AssertRefused(state, plan, model, "diario_ausente");
        }

        private static void Forge(string state, Dictionary<string, object> line)
        {
            var log = ChainLog.Open(Path.Combine(state, "diario.jsonl"), false, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "diario");
            log.Append(line);
            File.Delete(Path.Combine(state, "estado.json"));
        }

        [Fact]
        public void AH03_IllegalTransition_UnknownOperation_UnknownState_AreRejected()
        {
            var dir = AH.NewDir("ilegal");
            var plan = AH.Plan(AH.Set("A", 1L), AH.Set("B", 2L));
            var model = AH.Model(dir);
            var cases = new[]
            {
                (new Dictionary<string, object> { ["ev"] = "transicion", ["op"] = plan.Ops[0].Id, ["de"] = "validada", ["a"] = "verificada" }, "transicion_ilegal"),
                (new Dictionary<string, object> { ["ev"] = "transicion", ["op"] = "op-inventada", ["de"] = "validada", ["a"] = "en_ejecucion" }, "operacion_desconocida"),
                (new Dictionary<string, object> { ["ev"] = "transicion", ["op"] = plan.Ops[0].Id, ["de"] = "validada", ["a"] = "aprobada_por_mi" }, "estado_desconocido"),
                (new Dictionary<string, object> { ["ev"] = "transicion", ["op"] = plan.Ops[0].Id, ["de"] = "ejecutada", ["a"] = "verificada" }, "transicion_inconsistente"),
                (new Dictionary<string, object> { ["ev"] = "transicion", ["f"] = 0L, ["op"] = plan.Ops[0].Id, ["de"] = "validada", ["a"] = "en_ejecucion" }, "escritor_obsoleto"),
            };
            foreach (var (line, code) in cases)
            {
                var state = Path.Combine(dir, "estado-" + code);
                using (var r = Open(state, plan, model, out _, ttl: TimeSpan.FromMilliseconds(1))) Assert.NotNull(r);   // apertura + dueño f1
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
            Assert.Contains(d1, d => d.Code == "plan_distinto");
            var pf = Path.Combine(state, "plan.json");
            File.WriteAllText(pf, File.ReadAllText(pf).Replace("2700", "2701"));
            Assert.Null(Open(state, null, model, out var d2));
            Assert.Contains(d2, d => d.Code == "plan_guardado_invalido" || d.Code == "plan_alterado");
        }

        [Fact]
        public void AH03_JournalOfAnotherSchemaVersion_IsRejected()
        {
            var dir = AH.NewDir("esquema");
            var plan = AH.BasePlan();
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            Directory.CreateDirectory(state);
            Assert.True(IrValidator.Validate(plan, out var vp).Count == 0);
            PlanFile.Write(state, vp);
            var log = ChainLog.Open(Path.Combine(state, "diario.jsonl"), false, new List<Dictionary<string, object>>(), new List<Diagnostic>(), "diario");
            log.Append(new Dictionary<string, object> { ["ev"] = "apertura", ["plan"] = vp.Hash, ["esquema"] = 99L });
            AssertRefused(state, plan, model, "diario_esquema_no_soportado");
        }

        // ---------------- AH-04 (en proceso: zombi) ----------------

        [Fact]
        public void AH04_ZombieOwnerAfterTakeover_CannotWriteModelOrJournal()
        {
            var dir = AH.NewDir("zombi");
            var plan = AH.Plan(AH.Set("A", 1L, level: "L03"), AH.Set("B", 2L, level: "L03"));
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            var zombie = Open(state, plan, model, out _, ttl: TimeSpan.FromMilliseconds(150));
            Assert.True(zombie.IsOwner);
            Thread.Sleep(250);                                                   // el zombi deja de renovar
            using (var owner = Open(state, plan, model, out _, ttl: TimeSpan.FromSeconds(30)))
            {
                Assert.True(owner.IsOwner);
                Assert.Equal(2, owner.State.Fencing);
                Assert.Throws<PlanLease.LeaseLostException>(() => zombie.Run());  // su lease ya no vale
                Assert.Equal(0, DurableSimModel.Open(model).TxFor(plan.Ops[0].Id, AH.Hash(plan)).Count);
                // y si se saltara el lease, el modelo lo rechaza por fencing
                owner.Run();
                var m = DurableSimModel.Open(model);   // modelo al día: el único obstáculo es el fencing
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
            Assert.Equal(OpState.Fallida, st.Ops[plan.Ops[0].Id].State);
            Assert.Equal(ErrorClass.SinObjetivos, st.Ops[plan.Ops[0].Id].Error);
            Assert.Equal(OpState.Bloqueada, st.Ops[plan.Ops[1].Id].State);
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
            Assert.Equal(0, AH.Transitions(state, plan.Ops[0].Id, OpState.EnEjecucion));
        }

        [Fact]
        public void AH05_EmptyTargetSet_ExplicitlyAllowed_IsVerifiedAsEmpty_NotAsModification()
        {
            var plan = AH.Plan(AH.Set("Mounting Height", 2700L, level: "L99", allowEmpty: true));
            var (state, model, _) = RunToEnd(plan);
            var rec = AH.Read(state, out _, out _).Ops[plan.Ops[0].Id];
            Assert.Equal(OpState.Verificada, rec.State);
            Assert.Equal(true, rec.Result["vacio"]);
            Assert.Equal(0L, Arg.GetLong(rec.Result, "elementos"));
            Assert.Equal(0L, Arg.GetLong(rec.Evidence, "objetivos"));
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
        }

        [Fact]
        public void AH05_ObserveWithZeroTargets_IsDistinctFromApplied_AndBackendRefusesDirectly()
        {
            var dir = AH.NewDir("vacio");
            var model = DurableSimModel.Open(AH.Model(dir));
            Assert.Empty(IrValidator.Validate(AH.Plan(AH.Set("X", 1L, level: "L99")), out var vp));
            var b = new SimulatedBackend(model);
            var ctx = new ExecContext { PlanHash = vp.Hash, Fencing = 1, Attempt = 1 };
            Assert.Equal(Effect.SinObjetivos, b.Observe(vp.Ops[0], ctx).Effect);                // nunca "aplicada" por 0 == 0
            var r = b.Execute(vp.Ops[0], ctx);                                                  // defensa en profundidad
            Assert.False(r.Ok);
            Assert.Equal(ErrorClass.SinObjetivos, r.ErrorClass);
            Assert.Equal(PostState.NoSatisfechas, b.CheckPostconditions(vp.Ops[0], ctx).State);
        }

        // ---------------- cancelación, pausa, repetición, reintentos ----------------

        [Fact]
        public void Cancellation_StopsBetweenOperations_AndCancelMarksTheRest()
        {
            var dir = AH.NewDir("cancelar");
            var plan = AH.Plan(Enumerable.Range(0, 10).Select(i => AH.Set("C" + i, (long)i, level: "L03")).ToArray());
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            var cts = new CancellationTokenSource();
            var f = new Faults();
            using (var r = Open(state, plan, model, out _, f))
            {
                var n = 0;
                // el token se cancela tras la 3.ª operación: el ejecutor termina la operación en curso y para entre operaciones
                var first3 = plan.Ops.Select(o => o.Id).ToList();
                r.State.CheckpointEvery = 1000;
                var t = new Thread(() => { while (r.State.Ops.Values.Count(o => o.State == OpState.Verificada) < 3) Thread.Sleep(1); cts.Cancel(); });
                t.Start();
                r.Run(cts.Token);
                t.Join();
                n = r.State.Ops.Values.Count(o => o.State == OpState.Verificada);
                Assert.InRange(n, 3, 10);
                Assert.DoesNotContain(r.State.Ops.Values, o => o.State == OpState.EnEjecucion || o.State == OpState.Ejecutada);
                r.Cancel();
                var after = r.State.Ops.Values.Count(o => o.State == OpState.Cancelada);
                Assert.Equal(10 - n, after);
                Assert.Equal(PlanRunState.Cancelado, r.State.PlanState);
            }
            Assert.Equal(PlanRunState.Cancelado, AH.Read(state, out _, out _).PlanState);
        }

        [Fact]
        public void Pause_PersistsAcrossRestart_ResumeContinues()
        {
            var dir = AH.NewDir("pausa");
            var plan = AH.Plan(AH.Set("A", 1L, level: "L03"), AH.Set("B", 2L, level: "L03"));
            var model = AH.Model(dir);
            var state = Path.Combine(dir, "estado");
            using (var r = Open(state, plan, model, out _)) { r.Pause(); Assert.Contains("pausado", r.Run()); }
            using (var r = Open(state, plan, model, out _))
            {
                Assert.Equal(PlanRunState.Pausado, r.State.PlanState);
                Assert.Contains("pausado", r.Run());
                Assert.Equal(0, r.State.Ops.Values.Count(o => o.State == OpState.Verificada));
                r.Resume();
                r.Run();
                Assert.All(r.State.Ops.Values, o => Assert.Equal(OpState.Verificada, o.State));
            }
        }

        [Fact]
        public void RepeatedExecutionOfACompletedPlan_DoesNothing()
        {
            var (state, model, plan) = RunToEnd();
            var before = AH.Journal(state).Count(r => Arg.GetString(r, "ev") == "transicion");
            using (var r = Open(state, plan, model, out _)) r.Run();
            using (var r = Open(state, plan, model, out _)) r.Run();
            Assert.Equal(before, AH.Journal(state).Count(r => Arg.GetString(r, "ev") == "transicion"));
            Assert.All(plan.Ops.Where(o => o.Operation == "fijar_parametro"), op => Assert.Equal(1, AH.TxCount(model, AH.Hash(plan), op.Id)));
        }

        [Fact]
        public void TransientRejections_RetriedOnlyUpToPolicy_RejectionsAreNotEffects()
        {
            var dir = AH.NewDir("reintentos");
            var a = AH.Set("A", 1L, level: "L03"); a.Retries = 2;
            var b = AH.Set("B", 2L, level: "L03"); b.Retries = 1;
            var plan = AH.Plan(a, b);
            var m = Path.Combine(dir, "modelo");
            DurableSimModel.Create(m, AH.Doc, AH.Print, new[] { AH.Cat + "=5@L03" }, new Dictionary<string, object>
            {
                ["transitorio"] = new Dictionary<string, object> { [plan.Ops[0].Id] = 2L, [plan.Ops[1].Id] = 5L },
            });
            var state = Path.Combine(dir, "estado");
            using (var r = Open(state, plan, m, out _)) r.Run();
            var st = AH.Read(state, out _, out _);
            Assert.Equal(OpState.Verificada, st.Ops[plan.Ops[0].Id].State);
            Assert.Equal(3, st.Ops[plan.Ops[0].Id].Attempt);
            Assert.Equal(OpState.Fallida, st.Ops[plan.Ops[1].Id].State);
            Assert.Equal(ErrorClass.Transitorio, st.Ops[plan.Ops[1].Id].Error);
            Assert.Equal(2, st.Ops[plan.Ops[1].Id].Attempt);
            Assert.Equal(1, AH.TxCount(m, AH.Hash(plan), plan.Ops[0].Id));
            Assert.Equal(0, AH.TxCount(m, AH.Hash(plan), plan.Ops[1].Id));
        }

        [Fact]
        public void PreconditionOnTargetCount_FailsBeforeWriting()
        {
            var op = AH.Set("A", 1L, level: "L03");
            op.Preconditions.Add(new Dictionary<string, object> { ["tipo"] = "objetivos", ["min"] = 6L });
            var plan = AH.Plan(op);
            var (state, model, _) = RunToEnd(plan);
            var rec = AH.Read(state, out _, out _).Ops[plan.Ops[0].Id];
            Assert.Equal(OpState.Fallida, rec.State);
            Assert.Equal(ErrorClass.Precondicion, rec.Error);
            Assert.Equal(0, AH.TxCount(model, AH.Hash(plan), plan.Ops[0].Id));
        }

        [Fact]
        public void RealModelBackend_WithWrites_IsRefusedInAlpha1()
        {
            var dir = AH.NewDir("revit");
            var r = PlanRunner.Open(Path.Combine(dir, "estado"), AH.BasePlan(), new RevitTaskAdapter(AH.Doc, AH.Print), new RunnerOptions(), out var diags);
            Assert.Null(r);
            Assert.Contains(diags, d => d.Code == "escrituras_no_permitidas");
        }

        // ---------------- modelo durable ----------------

        [Fact]
        public void Model_TornWalLine_IsNotCommitted_CorruptLineRefuses()
        {
            var dir = AH.NewDir("wal");
            var mdir = AH.Model(dir);
            var m = DurableSimModel.Open(mdir);
            var id = m.Targets(new Dictionary<string, object> { ["categoria"] = AH.Cat }).First();
            m.Commit("op-a", "plan", 1, 1, "h", new List<object[]> { new object[] { id, "P", null, 5L } });
            File.AppendAllText(Path.Combine(mdir, "modelo.1.wal"), "{\"tx\":\"tx-1-2\",\"op\":\"op-b\",\"camb");
            var m2 = DurableSimModel.Open(mdir);
            Assert.Equal(5L, m2.Get(id, "P"));
            Assert.Equal(0, m2.TxFor("op-b", "plan").Count);
            Assert.Contains(m2.OpenDiagnostics, d => d.Code == "wal_linea_final_truncada");
            var wal = Path.Combine(mdir, "modelo.1.wal");
            File.WriteAllText(wal, File.ReadAllText(wal).Replace("5", "6"));
            Assert.Throws<DurableSimModel.ModelCorruptException>(() => DurableSimModel.Open(mdir));
        }
    }
}
