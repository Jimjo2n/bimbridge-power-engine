// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// Definición de Hecho de alpha1 (§11): un proceso nuevo recibe un plan estructurado, lo valida, ejecuta 1 000 unidades simuladas con
    /// dependencias, sobrevive a terminaciones forzadas (SIGKILL) en siete puntos distintos, recarga modelo y estado desde disco en cada
    /// proceso nuevo, continúa sin repetir nada y produce un informe completo. PDI_AH_EVIDENCIA=&lt;carpeta&gt; guarda el registro.
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

            // elegir, en el momento de cada caída, la k-ésima escritura aún pendiente en el orden de ejecución (para repartir las caídas)
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
                AH.WaitLeaseExpiry(state);
                var r = AH.Run(fault, mark, "ejecutar", processes == 0 ? pf : "-", state, "--modelo", model, "--lease-ms", "2000", "--checkpoint", "50");
                processes++;
                var st = AH.Read(state, out var vplan, out var diags);
                Assert.NotNull(st);
                var verified = st.Ops.Where(kv => kv.Value.State == OpState.Verificada).Select(kv => kv.Key).ToHashSet();
                Log("$ agenthost ejecutar … [PDI_AH_FALLO=" + fault + "] → código " + r.Code + (r.Killed ? " (SIGKILL)" : "") + "\n  " + r.Out.Trim().Replace("\n", "\n  ") +
                    "\n  tras la caída: " + Compact.Summary(vplan, st));
                Assert.True(r.Killed, "debía morir en " + fault + ": " + r);
                Assert.Subset(verified, verifiedBefore);    // lo completado sigue completado
                verifiedBefore = verified;
            }
            AH.WaitLeaseExpiry(state);
            var last = AH.Run(null, null, "ejecutar", "-", state, "--modelo", model, "--lease-ms", "2000");
            processes++;
            Log("$ agenthost ejecutar - (sin fallo, proceso " + processes + ") → código " + last.Code + "\n  " + last.Out.Trim().Replace("\n", "\n  "));
            Assert.Equal(2, last.Code);   // terminó con las excepciones previstas
            Log("caídas registradas:\n  " + File.ReadAllText(mark).Trim().Replace("\n", "\n  "));
            Log("tiempo total (8 procesos): " + sw.ElapsedMilliseconds + " ms");

            // ---- comprobaciones con objetos nuevos leídos de disco ----
            var final = AH.Read(state, out var plan, out _);
            var fresh = DurableSimModel.Open(model);
            Assert.Equal(1000, plan.Ops.Count);
            Assert.Subset(final.Ops.Where(kv => kv.Value.State == OpState.Verificada).Select(kv => kv.Key).ToHashSet(), verifiedBefore);
            // 1. nada repetido: cada escritura, a lo sumo una transacción; las verificadas no vacías, exactamente una
            foreach (var op in plan.Ops.Where(o => o.Writes))
            {
                var n = fresh.TxFor(op.Id, hash).Count;
                Assert.InRange(n, 0, 1);
                if (final.Ops[op.Id].State == OpState.Verificada && !Ids(spec, "vacio_permitido").Contains(op.Id)) Assert.Equal(1, n);
            }
            // 2. dependencias respetadas: ninguna operación empezó antes de que sus dependencias quedaran verificadas
            var journal = AH.Journal(state);
            var verifiedAt = new Dictionary<string, long>();
            foreach (var j in journal.Where(x => Arg.GetString(x, "a") == OpState.Verificada)) verifiedAt[Arg.GetString(j, "op")] = Arg.GetLong(j, "seq").Value;
            foreach (var j in journal.Where(x => Arg.GetString(x, "a") == OpState.EnEjecucion))
                foreach (var d in plan.ById[Arg.GetString(j, "op")].DependsOn)
                    Assert.True(verifiedAt.TryGetValue(d, out var vs) && vs < Arg.GetLong(j, "seq"), "dependencia " + d + " no verificada antes de empezar " + Arg.GetString(j, "op"));
            // 3. los fallos de verificación no son éxitos
            Assert.All(Ids(spec, "esperado_erroneo"), id => { Assert.Equal(OpState.Fallida, final.Ops[id].State); Assert.Equal(ErrorClass.Verificacion, final.Ops[id].Error); });
            Assert.Equal(OpState.Fallida, final.Ops[(string)spec["qc_desviado"]].State);
            Assert.All(Ids(spec, "rechazadas"), id => Assert.Equal(ErrorClass.Rechazo, final.Ops[id].Error));
            Assert.All(Ids(spec, "transitorias_ok"), id => Assert.Equal(OpState.Verificada, final.Ops[id].State));
            Assert.All(Ids(spec, "transitorias_ko"), id => Assert.Equal(ErrorClass.Transitorio, final.Ops[id].Error));
            Assert.All(Ids(spec, "vacio_permitido"), id => Assert.Equal(OpState.Verificada, final.Ops[id].State));
            Assert.All(Ids(spec, "vacio_no_permitido"), id => Assert.Equal(ErrorClass.SinObjetivos, final.Ops[id].Error));
            Assert.DoesNotContain(final.Ops.Values, o => !OpState.IsFinal(o.State));
            // 4. el informe (otro proceso) identifica todo lo no resuelto
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
            Log("recuperaciones en el diario:\n  " + string.Join("\n  ", journal.Where(x => Arg.GetString(x, "ev") == "nota").Select(x => Arg.GetString(x, "op") + " " + Json.Write(x["datos"]))));
            Log("dueños (fencing): " + string.Join(", ", journal.Where(x => Arg.GetString(x, "ev") == "dueno").Select(x => Arg.GetLong(x, "f"))));
            Log("líneas del diario: " + journal.Count + " · transacciones del modelo: " + plan.Ops.Where(o => o.Writes).Sum(o => fresh.TxFor(o.Id, hash).Count));

            var ev = Environment.GetEnvironmentVariable("PDI_AH_EVIDENCIA");
            if (ev != null)
            {
                Directory.CreateDirectory(ev);
                File.WriteAllText(Path.Combine(ev, "escenario_1000.log"), _log.ToString());
                File.Copy(Arg.GetString(repJ, "detalle"), Path.Combine(ev, "escenario_1000_informe.json"), true);
                File.Copy(Path.Combine(state, "diario.jsonl"), Path.Combine(ev, "escenario_1000_diario.jsonl"), true);
            }
        }

        /// <summary>Medición (no prueba de aceptación): tamaño en bytes de las respuestas compactas frente al grafo completo.</summary>
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
                    ["nota"] = "bytes UTF-8; no se midieron tokens (no hay tokenizador disponible sin red); no se afirma ahorro de tokens",
                };
            }
            _out.WriteLine(Json.Write(m));
            Assert.True((long)m["resumen_bytes"] < 200);
            Assert.True((long)m["cambios_ultima_operacion_bytes"] < 200);
            var ev = Environment.GetEnvironmentVariable("PDI_AH_EVIDENCIA");
            if (ev != null) { Directory.CreateDirectory(ev); File.WriteAllText(Path.Combine(ev, "tamanos_mensajes.json"), Json.Write(m)); }
        }
    }
}
