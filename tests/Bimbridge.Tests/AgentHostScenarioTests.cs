// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.AgentHost;
using Bimbridge.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Bimbridge.Tests
{
    /// <summary>
    /// alpha1 Definition of Done (§11): a new process receives a structured plan, validates it, runs 1,000 simulated units with
    /// dependencies, survives forced terminations (SIGKILL) at seven different points, reloads model and state from disk in each
    /// new process, continues without repeating anything and produces a complete report. BIMBRIDGE_AH_EVIDENCE=&lt;folder&gt; saves the log.
    /// </summary>
    public class AgentHostScenarioTests
    {
        private readonly ITestOutputHelper _out;
        private readonly StringBuilder _log = new StringBuilder();
        public AgentHostScenarioTests(ITestOutputHelper o) { _out = o; }

        private void Log(string s) { _out.WriteLine(s); _log.AppendLine(s); }

        private static List<string> Ids(Dictionary<string, object> spec, string key) => ((List<object>)spec[key]).Select(x => (string)x).ToList();

        [Fact]
        public void Scenario1000_SevenForcedTerminations_CompletesSafely_WithFullReport()
        {
            var dir = AH.NewDir("esc1000");
            var gen = AH.Run(null, null, "escenario", dir);
            Log("$ agenthost escenario <dir>\n" + gen.Out.Trim());
            Assert.Equal(0, gen.Code);
            var spec = Json.ParseObject(File.ReadAllText(Path.Combine(dir, "escenario.json")));
            var hash = (string)spec["plan"];
            var state = Path.Combine(dir, "estado");
            var model = Path.Combine(dir, "modelo");
            var mark = Path.Combine(dir, "caidas.txt");
            var pf = Path.Combine(dir, "plan_entrada.json");

            // pick, at the time of each crash, the k-th write still pending in run order (to spread the crashes)
            var skip = new HashSet<string>(Ids(spec, "rechazadas").Concat(Ids(spec, "transitorias_ko")).Concat(Ids(spec, "vacio_permitido")).Concat(Ids(spec, "vacio_no_permitido")).Concat(Ids(spec, "esperado_erroneo")));
            string Pending(int k)
            {
                var st = AH.Read(state, out var vp, out _);
                return vp.Order.Where(id => vp.ById[id].Writes && !skip.Contains(id) && st.Ops[id].State == OpState.Validada).Skip(k).First();
            }
            var faults = new List<Func<string>>
            {
                () => "runner.entre_operaciones#120",
                () => "backend.mitad_tx@" + Pending(40) + ":7",
                () => "backend.tras_commit@" + Pending(60),
                () => "runner.en_checkpoint#2",
                () => "runner.tras_ejecutar@" + Pending(40),
                () => "runner.en_verificacion@" + Pending(40),
                () => "runner.tras_verificar@" + Pending(40),
            };
            var verifiedBefore = new HashSet<string>();
            var processes = 0;
            var sw = Stopwatch.StartNew();
            foreach (var f in faults)
            {
                var fault = f();
                AH.ExpireDeadLease(state);   // AUD-04: crash ≠ expiry; long lease and the dead process's expiry moved forward
                var r = AH.Run(fault, mark, "ejecutar", processes == 0 ? pf : "-", state, "--modelo", model, "--lease-ms", "3600000", "--checkpoint", "50");
                processes++;
                var st = AH.Read(state, out var vplan, out var diags);
                Assert.NotNull(st);
                var verified = st.Ops.Where(kv => kv.Value.State == OpState.Verificada).Select(kv => kv.Key).ToHashSet();
                Log("$ agenthost ejecutar … [BIMBRIDGE_AH_FAULT=" + fault + "] → code " + r.Code + (r.Killed ? " (SIGKILL)" : "") + "\n  " + r.Out.Trim().Replace("\n", "\n  ") +
                    "\n  after the crash: " + Compact.Summary(vplan, st));
                Assert.True(r.Killed, "should have died at " + fault + ": " + r);
                Assert.Subset(verified, verifiedBefore);    // what was completed stays completed
                verifiedBefore = verified;
            }
            AH.ExpireDeadLease(state);
            var last = AH.Run(null, null, "ejecutar", "-", state, "--modelo", model, "--lease-ms", "3600000");
            processes++;
            Log("$ agenthost ejecutar - (no fault, process " + processes + ") → code " + last.Code + "\n  " + last.Out.Trim().Replace("\n", "\n  "));
            Assert.Equal(2, last.Code);   // finished with the expected exceptions
            Log("recorded crashes:\n  " + File.ReadAllText(mark).Trim().Replace("\n", "\n  "));
            Log("total time (8 processes): " + sw.ElapsedMilliseconds + " ms");

            // ---- checks with new objects read from disk ----
            var final = AH.Read(state, out var plan, out _);
            var fresh = DurableSimModel.Open(model);
            Assert.Equal(1000, plan.Ops.Count);
            Assert.Subset(final.Ops.Where(kv => kv.Value.State == OpState.Verificada).Select(kv => kv.Key).ToHashSet(), verifiedBefore);
            // 1. nothing repeated: each write has at most one transaction; non-empty verified ones, exactly one
            foreach (var op in plan.Ops.Where(o => o.Writes))
            {
                var n = fresh.TxFor(op.Id, hash).Count;
                Assert.InRange(n, 0, 1);
                if (final.Ops[op.Id].State == OpState.Verificada && !Ids(spec, "vacio_permitido").Contains(op.Id)) Assert.Equal(1, n);
            }
            // 2. dependencies respected: no operation started before its dependencies were verified
            var journal = AH.Journal(state);
            var verifiedAt = new Dictionary<string, long>();
            foreach (var j in journal.Where(x => Arg.GetString(x, "a") == OpState.Verificada)) verifiedAt[Arg.GetString(j, "op")] = Arg.GetLong(j, "seq").Value;
            foreach (var j in journal.Where(x => Arg.GetString(x, "a") == OpState.EnEjecucion))
                foreach (var d in plan.ById[Arg.GetString(j, "op")].DependsOn)
                    Assert.True(verifiedAt.TryGetValue(d, out var vs) && vs < Arg.GetLong(j, "seq"), "dependency " + d + " not verified before starting " + Arg.GetString(j, "op"));
            // 3. verification failures are not successes
            Assert.All(Ids(spec, "esperado_erroneo"), id => { Assert.Equal(OpState.Fallida, final.Ops[id].State); Assert.Equal(ErrorClass.Verificacion, final.Ops[id].Error); });
            Assert.Equal(OpState.Fallida, final.Ops[(string)spec["qc_desviado"]].State);
            Assert.All(Ids(spec, "rechazadas"), id => Assert.Equal(ErrorClass.Rechazo, final.Ops[id].Error));
            Assert.All(Ids(spec, "transitorias_ok"), id => Assert.Equal(OpState.Verificada, final.Ops[id].State));
            Assert.All(Ids(spec, "transitorias_ko"), id => Assert.Equal(ErrorClass.Transitorio, final.Ops[id].Error));
            Assert.All(Ids(spec, "vacio_permitido"), id => Assert.Equal(OpState.Verificada, final.Ops[id].State));
            Assert.All(Ids(spec, "vacio_no_permitido"), id => Assert.Equal(ErrorClass.SinObjetivos, final.Ops[id].Error));
            Assert.DoesNotContain(final.Ops.Values, o => !OpState.IsFinal(o.State));
            // 4. the report (another process) identifies everything unresolved
            var rep = AH.Run(null, null, "estado", state, "informe");
            Log("$ agenthost estado <estado> informe\n  " + rep.Out.Trim());
            var repJ = Json.ParseObject(rep.Out.Trim().Split('\n').Last());
            var unresolved = final.Ops.Count(kv => kv.Value.State != OpState.Verificada);
            Assert.Equal((long)unresolved, Arg.GetLong(repJ, "sin_resolver"));
            var detail = Json.ParseObject(File.ReadAllText(Arg.GetString(repJ, "detalle")));
            Assert.Equal(final.Ops.Where(kv => kv.Value.State != OpState.Verificada).Select(kv => kv.Key).OrderBy(x => x),
                         ((List<object>)detail["sin_resolver"]).Select(x => Arg.GetString((Dictionary<string, object>)x, "id")).OrderBy(x => x));
            var exc = AH.Run(null, null, "estado", state, "excepciones");
            Log("$ agenthost estado <estado> excepciones\n  " + exc.Out.Trim().Replace("\n", "\n  "));
            Log("recoveries in the journal:\n  " + string.Join("\n  ", journal.Where(x => Arg.GetString(x, "ev") == "nota").Select(x => Arg.GetString(x, "op") + " " + Json.Write(x["datos"]))));
            Log("owners (fencing): " + string.Join(", ", journal.Where(x => Arg.GetString(x, "ev") == "dueno").Select(x => Arg.GetLong(x, "f"))));
            Log("journal lines: " + journal.Count + " · model transactions: " + plan.Ops.Where(o => o.Writes).Sum(o => fresh.TxFor(o.Id, hash).Count));

            var ev = Environment.GetEnvironmentVariable("BIMBRIDGE_AH_EVIDENCE");
            if (ev != null)
            {
                Directory.CreateDirectory(ev);
                File.WriteAllText(Path.Combine(ev, "escenario_1000.log"), _log.ToString());
                File.Copy(Arg.GetString(repJ, "detalle"), Path.Combine(ev, "escenario_1000_informe.json"), true);
                File.Copy(Path.Combine(state, "diario.jsonl"), Path.Combine(ev, "escenario_1000_diario.jsonl"), true);
            }
        }

        /// <summary>Measurement (not an acceptance test): size in bytes of the compact responses versus the full graph.</summary>
        [Fact]
        public void Measure_CompactMessageSizes_Bytes()
        {
            var dir = AH.NewDir("medida");
            Scenario.Generate(dir);
            var state = Path.Combine(dir, "estado");
            var draft = PlanDraft.Parse(File.ReadAllText(Path.Combine(dir, "plan_entrada.json")));
            var sw = Stopwatch.StartNew();
            Dictionary<string, object> m;
            using (var r = PlanRunner.Open(state, draft, new SimulatedBackend(DurableSimModel.Open(Path.Combine(dir, "modelo"))), new RunnerOptions(), out _))
            {
                var seqBefore = r.State.Seq;
                r.Run();
                var runMs = sw.ElapsedMilliseconds;
                int B(string s) => Encoding.UTF8.GetByteCount(s);
                var planJson = Json.Write(draft.ToJson());
                var cpJson = File.ReadAllText(Path.Combine(state, "estado.json"));
                var lastOp = r.State.Changes.Last();
                m = new Dictionary<string, object>
                {
                    ["operaciones"] = 1000L, ["ejecucion_ms_en_proceso"] = runMs, ["diario_lineas"] = r.State.Seq,
                    ["plan_completo_bytes"] = (long)B(planJson), ["estado_completo_bytes"] = (long)B(cpJson),
                    ["resumen_bytes"] = (long)B(Compact.Summary(r.Plan, r.State)),
                    ["siguiente_bytes"] = (long)B(Compact.NextText(r.Plan, r.State)),
                    ["excepciones_bytes"] = (long)B(Compact.Exceptions(r.Plan, r.State)),
                    ["fallos_verificacion_bytes"] = (long)B(Compact.VerificationFailures(r.Plan, r.State)),
                    ["cambios_ultima_operacion_bytes"] = (long)B(Compact.ChangesSince(r.Plan, r.State, lastOp.Seq - 1)),
                    ["informe_referencia_bytes"] = (long)B(Json.Write(Compact.Report(state, r.Plan, r.State))),
                    ["nota"] = "UTF-8 bytes; tokens were not measured (no tokenizer available offline); no token savings are claimed",
                };
            }
            _out.WriteLine(Json.Write(m));
            Assert.True((long)m["resumen_bytes"] < 200);
            Assert.True((long)m["cambios_ultima_operacion_bytes"] < 200);
            var ev = Environment.GetEnvironmentVariable("BIMBRIDGE_AH_EVIDENCE");
            if (ev != null) { Directory.CreateDirectory(ev); File.WriteAllText(Path.Combine(ev, "tamanos_mensajes.json"), Json.Write(m)); }
        }
    }
}
