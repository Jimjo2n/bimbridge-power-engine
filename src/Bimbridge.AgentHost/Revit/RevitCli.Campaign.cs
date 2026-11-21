// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// rc1: native campaign harnesses. PREPARED, NOT RUN against Revit (BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION).
    /// Same testable core as NRT-05: they receive an already open connection (in tests, a fake add-in over a pipe).
    ///
    /// NRT-03: the AgentHost "crashes" (injected exception; Revit stays alive, it is never killed) at every critical point of a persistent
    ///         write and another runner resumes the SAME journal: never a second commit, result verified by observing the model.
    /// NRT-06: integrated functional acceptance: paged read, multi-operation plan with dependencies (consultar → batch → set →
    ///         lighting fixture/tag QC), postconditions, rejection without authorization, out-of-scope rejection, idempotency on resume.
    /// None of them saves: the disposable model is left modified and the operator closes it WITHOUT saving (rollback procedure).
    /// </summary>
    public static partial class RevitCli
    {
        public static readonly string[] Nrt03Points = { "backend.antes_enviar", "backend.tras_commit", "runner.tras_ejecutar", "runner.en_verificacion", "runner.tras_verificar" };

        private static Nrt05Options CampaignOptions(string[] a) => new Nrt05Options
        {
            Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
            OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("missing --salida"),
            Category = Program.Opt(a, "--categoria") ?? throw new ArgumentException("missing --categoria"),
            Param = Program.Opt(a, "--parametro", "Comments"),
            ExpectedPath = Program.Opt(a, "--ruta-esperada"),
            Authorized = a.Contains("--autorizo-persistente"),
            Ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("missing --ids")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList(),
        };

        private static int WithLink(string[] a, Func<RevitLink, Nrt05Options, int> run)
        {
            var o = CampaignOptions(a);
            var link = RevitLink.ForPid(o.Pid, out var err);
            if (link == null) { Directory.CreateDirectory(o.OutDir); File.WriteAllText(Path.Combine(o.OutDir, "informe.json"), Json.Write(new Dictionary<string, object> { ["resultado"] = "BLOQUEADA", ["motivo"] = err })); Console.WriteLine("BLOQUEADA: " + err); return 3; }
            using (link) return run(link, o);
        }

        private static int Nrt03(string[] a) => WithLink(a, Nrt03Run);
        private static int Nrt06(string[] a) => WithLink(a, Nrt06Run);
        private static int Nrt07(string[] a) => WithLink(a, Nrt07Run);

        /// <summary>
        /// NRT-07 (0.8.0, prepared): lost IPC response. The add-in applies the commit, but the AgentHost receives "desconocido"
        /// (no crash). It must record an uncertain effect, NOT repeat the commit, and confirm the result by observing the model.
        /// </summary>
        public static int Nrt07Run(RevitLink link, Nrt05Options o)
        {
            Directory.CreateDirectory(o.OutDir);
            link.OnCall = c => File.AppendAllText(Path.Combine(o.OutDir, "llamadas.jsonl"), Json.Write(c) + "\n");
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-07", ["version"] = BuildInfo.Informational, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            var b = CampaignPreflight(link, o, report, out var why);
            if (b == null) { report["resultado"] = "BLOQUEADA"; report["motivo"] = why; File.WriteAllText(Path.Combine(o.OutDir, "nrt07_informe.json"), Json.Write(report)); Console.WriteLine("BLOQUEADA: " + why); return 3; }
            var value = "NRT-07 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var plan = WritePlan(b, o, "NRT-07 lost response", value);
            var writeId = plan.Ops[1].Id;
            var f = new Faults().Configure("backend.respuesta_perdida@" + writeId, "excepcion");
            b.Faults = f;
            var dir = Path.Combine(o.OutDir, "plan");
            var before = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
            string state = null, note = null;
            try
            {
                using (var r = PlanRunner.Open(dir, plan, b, new RunnerOptions { RealWrites = "persistente", Faults = f, LeaseTtl = NativeLease }, out var d))
                {
                    if (r == null) note = string.Join("; ", d.Select(x => x.ToString()));
                    else { r.Run(); var rec = r.State.Ops[writeId]; state = rec.State; note = rec.Note; }
                }
            }
            catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; }
            var commits = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) - before;
            var journal = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText).FirstOrDefault(t => t.Contains("efecto_incierto")) : null;
            checks["respuesta_perdida_registrada_como_incierta"] = journal != null;
            checks["un_solo_commit"] = commits == 1;
            checks["verificada_por_observacion"] = state == OpState.Verified;
            report["estado_final"] = state; report["nota"] = note; report["commits"] = (long)commits;
            return Finish(o.OutDir, "nrt07", report, checks, link);
        }

        /// <summary>Common campaign preconditions (fail-closed). Returns the backend, or null with the reason.</summary>
        private static RevitBackend CampaignPreflight(RevitLink link, Nrt05Options o, Dictionary<string, object> report, out string why)
        {
            why = null;
            if (!o.Authorized) { why = "missing --autorizo-persistente"; return null; }
            if (o.Ids.Count < 3 || o.Ids.Count > 30) { why = "--ids: between 3 and 30 elements"; return null; }
            if (string.IsNullOrWhiteSpace(o.ExpectedPath)) { why = "missing --ruta-esperada"; return null; }
            var b = RevitBackend.Open(link, "persistente", out var err);
            if (b == null) { why = err; return null; }
            var docPath = b.Context.GetValueOrDefault("ruta") as string;
            string Norm(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            if (!string.Equals(Norm(docPath), Norm(o.ExpectedPath), StringComparison.OrdinalIgnoreCase)) { b.Dispose(); why = "actual path " + docPath + " ≠ authorized " + o.ExpectedPath; return null; }
            report["revit"] = new Dictionary<string, object> { ["pid"] = b.Context.GetValueOrDefault("pid"), ["addin"] = b.Context.GetValueOrDefault("addin"), ["documento"] = b.Context.GetValueOrDefault("documento"), ["ruta"] = docPath, ["clave"] = b.DocKey, ["huella"] = b.DocPrint };
            return b;
        }

        private static int Finish(string outDir, string name, Dictionary<string, object> report, Dictionary<string, object> checks, RevitLink link)
        {
            var pass = checks.Count > 0 && checks.Values.All(v => v is bool b && b);
            report["comprobaciones"] = checks;
            report["resultado"] = pass ? "APROBADA" : "FALLIDA";
            report["fin"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["metricas"] = link.Calls.GroupBy(c => (string)c["op"]).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object>
            {
                ["n"] = (long)g.Count(), ["ms_total"] = g.Sum(c => Convert.ToInt64(c["ms"], CultureInfo.InvariantCulture)),
                ["bytes"] = g.Sum(c => Convert.ToInt64(c["bytes_peticion"], CultureInfo.InvariantCulture) + Convert.ToInt64(c["bytes_respuesta"], CultureInfo.InvariantCulture)),
            });
            report["llamadas"] = link.Calls.Cast<object>().ToList();
            File.WriteAllText(Path.Combine(outDir, name + "_informe.json"), Json.Write(report));
            foreach (var kv in checks) Console.WriteLine((kv.Value is bool b2 && b2 ? "✓ " : "✗ ") + kv.Key);
            Console.WriteLine(name.ToUpperInvariant() + " " + report["resultado"]);
            return pass ? 0 : 2;
        }

        private static PlanDraft WritePlan(RevitBackend b, Nrt05Options o, string objective, string value)
        {
            var d = new PlanDraft { Objective = objective, DocKey = b.DocKey, DocPrint = b.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
            var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = o.Category; d.Ops.Add(q);
            var w = new OpDraft { Operation = "fijar_parametro" };
            w.Scope["categoria"] = o.Category; w.Scope["ids"] = o.Ids.Cast<object>().ToList();
            w.Params["parametro"] = o.Param; w.Params["valor"] = value; w.Params["unidades"] = "texto";
            w.DependsOn.Add("#0"); d.Ops.Add(w);
            return d.AssignIds();
        }

        /// <summary>NRT-03 (native, prepared). One crash point per round; each round with its own value and journal.</summary>
        public static int Nrt03Run(RevitLink link, Nrt05Options o) => Nrt03Run(link, o, Nrt03Points, NativeLease);

        /// <summary>Lease of the "crashed" AgentHost in the native campaign. It must exceed the real Revit latency (up to ~40 s with
        /// read retries); with 1.5 s it expired in the middle of a 5.8 s query (campaign a1fc395, round 3).</summary>
        public static readonly TimeSpan NativeLease = TimeSpan.FromSeconds(60);

        public static int Nrt03Run(RevitLink link, Nrt05Options o, IEnumerable<string> points, TimeSpan lease)
        {
            Directory.CreateDirectory(o.OutDir);
            link.OnCall = c => File.AppendAllText(Path.Combine(o.OutDir, "llamadas.jsonl"), Json.Write(c) + "\n");
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-03", ["version"] = BuildInfo.Informational, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            var pre = CampaignPreflight(link, o, report, out var why);
            if (pre == null) { report["resultado"] = "BLOQUEADA"; report["motivo"] = why; File.WriteAllText(Path.Combine(o.OutDir, "nrt03_informe.json"), Json.Write(report)); Console.WriteLine("BLOQUEADA: " + why); return 3; }
            // NOTE: RevitBackend.Dispose closes the shared connection; the round backends are NOT disposed (rc1 run: 'revit_no_disponible').
            var rounds = new List<object>();
            var n = 0;
            foreach (var point in points)
            {
                n++;
                var value = "NRT-03 " + n.ToString(CultureInfo.InvariantCulture) + " " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                var dir = Path.Combine(o.OutDir, "ronda" + n.ToString(CultureInfo.InvariantCulture));
                var commitsBefore = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
                string state1 = null, state2 = null, note = null, crashWhere = null; var crashed = false;
                PlanDraft plan;
                var b1 = RevitBackend.Open(link, "persistente", out var e1);
                {
                    if (b1 == null) { checks["ronda" + n + "_conexion"] = false; note = e1; rounds.Add(new Dictionary<string, object> { ["punto"] = point, ["error"] = e1 }); continue; }
                    plan = WritePlan(b1, o, "NRT-03 " + point, value);
                    // rc1 (native review): without "@op" the crash fired on the FIRST operation that passed the point
                    // (the read query) and rounds 3–5 did not exercise the write. It is armed ONLY for the write.
                    var writeId = plan.Ops[1].Id;
                    var f = new Faults().Configure(point + "@" + writeId, "excepcion");
                    b1.Faults = f;
                    using (var r1 = PlanRunner.Open(dir, plan, b1, new RunnerOptions { RealWrites = "persistente", Faults = f, LeaseTtl = lease }, out var d1))
                    {
                        if (r1 == null) { checks["ronda" + n + "_abre"] = false; rounds.Add(new Dictionary<string, object> { ["punto"] = point, ["diag"] = d1.Select(x => (object)x.ToString()).ToList() }); continue; }
                        try { r1.Run(); }
                        catch (CrashException cx) { crashWhere = cx.Message; crashed = cx.Message.Contains("@" + writeId); }
                        catch (Exception ex) { note = "non-simulated exception before the crash: " + ex.GetType().Name + ": " + ex.Message; }
                        state1 = r1.State.Ops[plan.Ops[1].Id].State;
                    }
                }
                Thread.Sleep(lease + TimeSpan.FromMilliseconds(300));   // the "crashed" AgentHost's lease expires
                var b2 = RevitBackend.Open(link, "persistente", out var e2);
                using (var r2 = b2 == null ? null : PlanRunner.Open(dir, null, b2, new RunnerOptions { RealWrites = "persistente", LeaseTtl = TimeSpan.FromSeconds(30) }, out _))
                {
                    if (r2 != null) { try { r2.Run(); } catch (Exception ex) { note = "exception on resume: " + ex.GetType().Name + ": " + ex.Message; } var rec = r2.State.Ops[r2.Plan.Ops[1].Id]; state2 = rec.State; note = note == null ? rec.Note : note + " | " + rec.Note; }
                    else note = e2 ?? "could not reopen the journal";
                }
                var commits = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) - commitsBefore;
                var expectCommits = 1;
                checks["ronda" + n + "_" + point + "_cayo_en_escritura"] = crashed;
                checks["ronda" + n + "_" + point + "_verificada"] = state2 == OpState.Verified;
                checks["ronda" + n + "_" + point + "_un_commit"] = commits == expectCommits;
                rounds.Add(new Dictionary<string, object> { ["punto"] = point, ["valor"] = value, ["caida_en"] = crashWhere, ["op_escritura"] = plan.Ops[1].Id, ["estado_tras_caida"] = state1, ["estado_final"] = state2, ["commits"] = (long)commits, ["nota"] = note });
            }
            report["rondas"] = rounds;
            return Finish(o.OutDir, "nrt03", report, checks, link);
        }

        /// <summary>NRT-06 (native, prepared): integrated functional acceptance in a single multi-operation plan + negative controls.</summary>
        public static int Nrt06Run(RevitLink link, Nrt05Options o)
        {
            Directory.CreateDirectory(o.OutDir);
            link.OnCall = c => File.AppendAllText(Path.Combine(o.OutDir, "llamadas.jsonl"), Json.Write(c) + "\n");
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-06", ["version"] = BuildInfo.Informational, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            var b = CampaignPreflight(link, o, report, out var why);
            if (b == null) { report["resultado"] = "BLOQUEADA"; report["motivo"] = why; File.WriteAllText(Path.Combine(o.OutDir, "nrt06_informe.json"), Json.Write(report)); Console.WriteLine("BLOQUEADA: " + why); return 3; }
            using (b)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                // integrated plan: consultar → batch (distinct value per element) → set (same value on the first 2) → read-only QC
                var d = new PlanDraft { Objective = "NRT-06 integrated", DocKey = b.DocKey, DocPrint = b.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
                var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = o.Category; d.Ops.Add(q);
                var batch = new OpDraft { Operation = "fijar_parametro_lote" };
                batch.Scope["categoria"] = o.Category; batch.Scope["ids"] = o.Ids.Cast<object>().ToList();
                batch.Params["parametro"] = o.Param;
                batch.Params["valores"] = o.Ids.ToDictionary(i => i.ToString(CultureInfo.InvariantCulture), i => (object)("NRT-06 " + stamp + " #" + i.ToString(CultureInfo.InvariantCulture)));
                batch.DependsOn.Add("#0"); d.Ops.Add(batch);
                var fix = new OpDraft { Operation = "fijar_parametro" };
                fix.Scope["categoria"] = o.Category; fix.Scope["ids"] = o.Ids.Take(2).Cast<object>().ToList();
                fix.Params["parametro"] = o.Param; fix.Params["valor"] = "NRT-06 " + stamp + " fixed"; fix.Params["unidades"] = "texto";
                fix.DependsOn.Add("#1"); d.Ops.Add(fix);
                var qc = new OpDraft { Operation = "verificar_qc" }; qc.Params["receta"] = RevitBackend.QcLightingTags; d.Ops.Add(qc);
                d.AssignIds();
                var errs = IrValidator.Validate(d, out var vp);
                checks["plan_valido"] = errs.Count == 0;
                if (errs.Count > 0) { report["errores_plan"] = errs.Cast<object>().ToList(); return Finish(o.OutDir, "nrt06", report, checks, link); }
                File.WriteAllText(Path.Combine(o.OutDir, "plan_ir.json"), Json.Write(d.ToJson()));

                // rejection 1: without operator authorization
                using (var denied = PlanRunner.Open(Path.Combine(o.OutDir, "estado_sin_autorizacion"), d, b, new RunnerOptions(), out var dd))
                    checks["rechazo_sin_autorizacion"] = denied == null && dd.Any(x => x.Code == "escrituras_no_permitidas");
                var commits0 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);

                // rejection 2: scope with a nonexistent id → precondition, no commit
                var bad = new PlanDraft { Objective = "NRT-06 out of scope", DocKey = b.DocKey, DocPrint = b.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
                var bw = new OpDraft { Operation = "fijar_parametro" };
                bw.Scope["categoria"] = o.Category; bw.Scope["ids"] = new List<object> { o.Ids[0], 999999999999L };
                bw.Params["parametro"] = o.Param; bw.Params["valor"] = "NO"; bw.Params["unidades"] = "texto"; bad.Ops.Add(bw); bad.AssignIds();
                using (var rb = PlanRunner.Open(Path.Combine(o.OutDir, "estado_fuera_alcance"), bad, b, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    rb?.Run();
                    checks["rechazo_fuera_de_alcance"] = rb != null && rb.State.Ops[bad.Ops[0].Id].State == OpState.Failed
                                                        && link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits0;
                }

                // integrated run
                var stateDir = Path.Combine(o.OutDir, "estado");
                using (var r = PlanRunner.Open(stateDir, d, b, new RunnerOptions { RealWrites = "persistente" }, out var diags))
                {
                    if (r == null) { checks["abre_plan"] = false; report["diag"] = diags.Select(x => (object)x.ToString()).ToList(); return Finish(o.OutDir, "nrt06", report, checks, link); }
                    report["resumen"] = r.Run();
                    var ops = vp.Ops.ToDictionary(x => x.Operation, x => r.State.Ops[x.Id]);
                    report["operaciones"] = vp.Ops.Select(x => (object)new Dictionary<string, object> { ["id"] = x.Id, ["op"] = x.Operation, ["estado"] = r.State.Ops[x.Id].State, ["directiva"] = OpState.DirectiveName(r.State.Ops[x.Id].State), ["nota"] = r.State.Ops[x.Id].Note, ["resultado"] = r.State.Ops[x.Id].Result }).ToList();
                    checks["consulta_verificada"] = ops["consultar"].State == OpState.Verified;
                    checks["lote_verificado"] = ops["fijar_parametro_lote"].State == OpState.Verified;
                    checks["fijar_verificado_tras_lote"] = ops["fijar_parametro"].State == OpState.Verified;
                    // the sample model QC may have real deviations: it must RUN and leave evidence, not come out 0
                    checks["qc_ejecutado_con_evidencia"] = ops["verificar_qc"].State == OpState.Verified || (ops["verificar_qc"].State == OpState.Failed && ops["verificar_qc"].Error == ErrorClass.Verification);
                }
                var commits1 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) - commits0;
                checks["dos_commits_exactos"] = commits1 == 2;   // batch + set; consultar and QC do not write
                // independent postcondition: the first 2 with the fixed value, the rest with their batch value
                var rq = link.Call(Wire.OpQuery, new Dictionary<string, object> { [Wire.DocArg] = b.Context["doc"], ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(), ["f"] = new List<object> { "id", o.Param }, ["page_size"] = 200L }, false);
                var got = new Dictionary<long, string>();
                foreach (var row in ((rq.Data as Dictionary<string, object>)?.GetValueOrDefault("rows") as List<object>) ?? new List<object>())
                    if (row is List<object> l && l.Count >= 2) got[Convert.ToInt64(l[0], CultureInfo.InvariantCulture)] = Convert.ToString(l[1], CultureInfo.InvariantCulture);
                checks["relectura_independiente"] = o.Ids.Select((id, k) => got.TryGetValue(id, out var v) && v == (k < 2 ? "NRT-06 " + stamp + " fixed" : "NRT-06 " + stamp + " #" + id.ToString(CultureInfo.InvariantCulture))).All(x => x);
                // idempotency: resuming the same journal does not write
                var commits2 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
                using (var again = PlanRunner.Open(stateDir, null, b, new RunnerOptions { RealWrites = "persistente" }, out _)) again?.Run();
                checks["reanudar_sin_escritura"] = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits2;
                checks["sin_guardar_ni_sync"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
            }
            return Finish(o.OutDir, "nrt06", report, checks, link);
        }
    }
}
