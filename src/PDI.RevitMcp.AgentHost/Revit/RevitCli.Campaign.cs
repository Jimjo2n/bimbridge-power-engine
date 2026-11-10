// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// rc1: arneses de la campaña nativa. PREPARADOS, NO EJECUTADOS contra Revit (BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION).
    /// Mismo núcleo testeable que NRT-05: reciben una conexión ya abierta (en pruebas, un add-in falso por tubería).
    ///
    /// NRT-03: el AgentHost "cae" (excepción inyectada; Revit sigue vivo, nunca se mata) en cada punto crítico de una escritura
    ///         persistente y otro runner retoma el MISMO diario: nunca un segundo commit, resultado verificado observando el modelo.
    /// NRT-06: aceptación funcional integrada: lectura paginada, plan multi-operación con dependencias (consultar → lote → fijar →
    ///         QC de luminarias/tags), postcondiciones, rechazo sin autorización, rechazo fuera de alcance, idempotencia al reanudar.
    /// Ninguno guarda: el modelo desechable queda modificado y el operador lo cierra SIN guardar (procedimiento de reversión).
    /// </summary>
    public static partial class RevitCli
    {
        public static readonly string[] Nrt03Points = { "backend.antes_enviar", "backend.tras_commit", "runner.tras_ejecutar", "runner.en_verificacion", "runner.tras_verificar" };

        private static Nrt05Options CampaignOptions(string[] a) => new Nrt05Options
        {
            Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
            OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("falta --salida"),
            Category = Program.Opt(a, "--categoria") ?? throw new ArgumentException("falta --categoria"),
            Param = Program.Opt(a, "--parametro", "Comments"),
            ExpectedPath = Program.Opt(a, "--ruta-esperada"),
            Authorized = a.Contains("--autorizo-persistente"),
            Ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("falta --ids")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList(),
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
        /// NRT-07 (0.8.0, preparado): respuesta IPC perdida. El add-in aplica el commit, pero el AgentHost recibe "desconocido"
        /// (sin caída). Debe registrar efecto incierto, NO repetir el commit y confirmar el resultado observando el modelo.
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
            var plan = WritePlan(b, o, "NRT-07 respuesta perdida", value);
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
            checks["verificada_por_observacion"] = state == OpState.Verificada;
            report["estado_final"] = state; report["nota"] = note; report["commits"] = (long)commits;
            return Finish(o.OutDir, "nrt07", report, checks, link);
        }

        /// <summary>Precondiciones comunes de la campaña (fail-closed). Devuelve el backend o null con el motivo.</summary>
        private static RevitBackend CampaignPreflight(RevitLink link, Nrt05Options o, Dictionary<string, object> report, out string why)
        {
            why = null;
            if (!o.Authorized) { why = "falta --autorizo-persistente"; return null; }
            if (o.Ids.Count < 3 || o.Ids.Count > 30) { why = "--ids: entre 3 y 30 elementos"; return null; }
            if (string.IsNullOrWhiteSpace(o.ExpectedPath)) { why = "falta --ruta-esperada"; return null; }
            var b = RevitBackend.Open(link, "persistente", out var err);
            if (b == null) { why = err; return null; }
            var ruta = b.Context.GetValueOrDefault("ruta") as string;
            string Norm(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            if (!string.Equals(Norm(ruta), Norm(o.ExpectedPath), StringComparison.OrdinalIgnoreCase)) { b.Dispose(); why = "ruta real " + ruta + " ≠ autorizada " + o.ExpectedPath; return null; }
            report["revit"] = new Dictionary<string, object> { ["pid"] = b.Context.GetValueOrDefault("pid"), ["addin"] = b.Context.GetValueOrDefault("addin"), ["documento"] = b.Context.GetValueOrDefault("documento"), ["ruta"] = ruta, ["clave"] = b.DocKey, ["huella"] = b.DocPrint };
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

        /// <summary>NRT-03 (nativo, preparado). Un punto de caída por ronda; cada ronda con su valor y su diario.</summary>
        public static int Nrt03Run(RevitLink link, Nrt05Options o) => Nrt03Run(link, o, Nrt03Points, NativeLease);

        /// <summary>Lease del AgentHost "caído" en la campaña nativa. Debe superar la latencia real de Revit (hasta ~40 s con los
        /// reintentos de lectura); con 1,5 s venció en mitad de una consulta de 5,8 s (campaña a1fc395, ronda 3).</summary>
        public static readonly TimeSpan NativeLease = TimeSpan.FromSeconds(60);

        public static int Nrt03Run(RevitLink link, Nrt05Options o, IEnumerable<string> points, TimeSpan lease)
        {
            Directory.CreateDirectory(o.OutDir);
            link.OnCall = c => File.AppendAllText(Path.Combine(o.OutDir, "llamadas.jsonl"), Json.Write(c) + "\n");
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-03", ["version"] = BuildInfo.Informational, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            var pre = CampaignPreflight(link, o, report, out var why);
            if (pre == null) { report["resultado"] = "BLOQUEADA"; report["motivo"] = why; File.WriteAllText(Path.Combine(o.OutDir, "nrt03_informe.json"), Json.Write(report)); Console.WriteLine("BLOQUEADA: " + why); return 3; }
            // OJO: RevitBackend.Dispose cierra la conexión compartida; los backends de las rondas NO se desechan (run de rc1: «revit_no_disponible»).
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
                    // rc1 (revisión nativa): sin "@op" la caída se disparaba en la PRIMERA operación que pasaba por el punto
                    // (la consulta de lectura) y las rondas 3–5 no ejercían la escritura. Se arma SOLO para la escritura.
                    var writeId = plan.Ops[1].Id;
                    var f = new Faults().Configure(point + "@" + writeId, "excepcion");
                    b1.Faults = f;
                    using (var r1 = PlanRunner.Open(dir, plan, b1, new RunnerOptions { RealWrites = "persistente", Faults = f, LeaseTtl = lease }, out var d1))
                    {
                        if (r1 == null) { checks["ronda" + n + "_abre"] = false; rounds.Add(new Dictionary<string, object> { ["punto"] = point, ["diag"] = d1.Select(x => (object)x.ToString()).ToList() }); continue; }
                        try { r1.Run(); }
                        catch (CrashException cx) { crashWhere = cx.Message; crashed = cx.Message.Contains("@" + writeId); }
                        catch (Exception ex) { note = "excepción no simulada antes de la caída: " + ex.GetType().Name + ": " + ex.Message; }
                        state1 = r1.State.Ops[plan.Ops[1].Id].State;
                    }
                }
                Thread.Sleep(lease + TimeSpan.FromMilliseconds(300));   // vence el lease del AgentHost "caído"
                var b2 = RevitBackend.Open(link, "persistente", out var e2);
                using (var r2 = b2 == null ? null : PlanRunner.Open(dir, null, b2, new RunnerOptions { RealWrites = "persistente", LeaseTtl = TimeSpan.FromSeconds(30) }, out _))
                {
                    if (r2 != null) { try { r2.Run(); } catch (Exception ex) { note = "excepción al reanudar: " + ex.GetType().Name + ": " + ex.Message; } var rec = r2.State.Ops[r2.Plan.Ops[1].Id]; state2 = rec.State; note = note == null ? rec.Note : note + " | " + rec.Note; }
                    else note = e2 ?? "no se pudo reabrir el diario";
                }
                var commits = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) - commitsBefore;
                var expectCommits = 1;
                checks["ronda" + n + "_" + point + "_cayo_en_escritura"] = crashed;
                checks["ronda" + n + "_" + point + "_verificada"] = state2 == OpState.Verificada;
                checks["ronda" + n + "_" + point + "_un_commit"] = commits == expectCommits;
                rounds.Add(new Dictionary<string, object> { ["punto"] = point, ["valor"] = value, ["caida_en"] = crashWhere, ["op_escritura"] = plan.Ops[1].Id, ["estado_tras_caida"] = state1, ["estado_final"] = state2, ["commits"] = (long)commits, ["nota"] = note });
            }
            report["rondas"] = rounds;
            return Finish(o.OutDir, "nrt03", report, checks, link);
        }

        /// <summary>NRT-06 (nativo, preparado): aceptación funcional integrada en un único plan multi-operación + controles negativos.</summary>
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
                // plan integrado: consultar → lote (valor distinto por elemento) → fijar (mismo valor en los 2 primeros) → QC lectura
                var d = new PlanDraft { Objective = "NRT-06 integrada", DocKey = b.DocKey, DocPrint = b.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
                var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = o.Category; d.Ops.Add(q);
                var lote = new OpDraft { Operation = "fijar_parametro_lote" };
                lote.Scope["categoria"] = o.Category; lote.Scope["ids"] = o.Ids.Cast<object>().ToList();
                lote.Params["parametro"] = o.Param;
                lote.Params["valores"] = o.Ids.ToDictionary(i => i.ToString(CultureInfo.InvariantCulture), i => (object)("NRT-06 " + stamp + " #" + i.ToString(CultureInfo.InvariantCulture)));
                lote.DependsOn.Add("#0"); d.Ops.Add(lote);
                var fix = new OpDraft { Operation = "fijar_parametro" };
                fix.Scope["categoria"] = o.Category; fix.Scope["ids"] = o.Ids.Take(2).Cast<object>().ToList();
                fix.Params["parametro"] = o.Param; fix.Params["valor"] = "NRT-06 " + stamp + " fijo"; fix.Params["unidades"] = "texto";
                fix.DependsOn.Add("#1"); d.Ops.Add(fix);
                var qc = new OpDraft { Operation = "verificar_qc" }; qc.Params["receta"] = RevitBackend.QcLightingTags; d.Ops.Add(qc);
                d.AssignIds();
                var errs = IrValidator.Validate(d, out var vp);
                checks["plan_valido"] = errs.Count == 0;
                if (errs.Count > 0) { report["errores_plan"] = errs.Cast<object>().ToList(); return Finish(o.OutDir, "nrt06", report, checks, link); }
                File.WriteAllText(Path.Combine(o.OutDir, "plan_ir.json"), Json.Write(d.ToJson()));

                // rechazo 1: sin autorización del operador
                using (var denied = PlanRunner.Open(Path.Combine(o.OutDir, "estado_sin_autorizacion"), d, b, new RunnerOptions(), out var dd))
                    checks["rechazo_sin_autorizacion"] = denied == null && dd.Any(x => x.Code == "escrituras_no_permitidas");
                var commits0 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);

                // rechazo 2: alcance con un id que no existe → precondición, ningún commit
                var bad = new PlanDraft { Objective = "NRT-06 fuera de alcance", DocKey = b.DocKey, DocPrint = b.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
                var bw = new OpDraft { Operation = "fijar_parametro" };
                bw.Scope["categoria"] = o.Category; bw.Scope["ids"] = new List<object> { o.Ids[0], 999999999999L };
                bw.Params["parametro"] = o.Param; bw.Params["valor"] = "NO"; bw.Params["unidades"] = "texto"; bad.Ops.Add(bw); bad.AssignIds();
                using (var rb = PlanRunner.Open(Path.Combine(o.OutDir, "estado_fuera_alcance"), bad, b, new RunnerOptions { RealWrites = "persistente" }, out _))
                {
                    rb?.Run();
                    checks["rechazo_fuera_de_alcance"] = rb != null && rb.State.Ops[bad.Ops[0].Id].State == OpState.Fallida
                                                        && link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits0;
                }

                // ejecución integrada
                var stateDir = Path.Combine(o.OutDir, "estado");
                using (var r = PlanRunner.Open(stateDir, d, b, new RunnerOptions { RealWrites = "persistente" }, out var diags))
                {
                    if (r == null) { checks["abre_plan"] = false; report["diag"] = diags.Select(x => (object)x.ToString()).ToList(); return Finish(o.OutDir, "nrt06", report, checks, link); }
                    report["resumen"] = r.Run();
                    var ops = vp.Ops.ToDictionary(x => x.Operation, x => r.State.Ops[x.Id]);
                    report["operaciones"] = vp.Ops.Select(x => (object)new Dictionary<string, object> { ["id"] = x.Id, ["op"] = x.Operation, ["estado"] = r.State.Ops[x.Id].State, ["directiva"] = OpState.DirectiveName(r.State.Ops[x.Id].State), ["nota"] = r.State.Ops[x.Id].Note, ["resultado"] = r.State.Ops[x.Id].Result }).ToList();
                    checks["consulta_verificada"] = ops["consultar"].State == OpState.Verificada;
                    checks["lote_verificado"] = ops["fijar_parametro_lote"].State == OpState.Verificada;
                    checks["fijar_verificado_tras_lote"] = ops["fijar_parametro"].State == OpState.Verificada;
                    // el QC del modelo de muestra puede tener desviaciones reales: se exige que se EJECUTE y deje evidencia, no que salga 0
                    checks["qc_ejecutado_con_evidencia"] = ops["verificar_qc"].State == OpState.Verificada || (ops["verificar_qc"].State == OpState.Fallida && ops["verificar_qc"].Error == ErrorClass.Verificacion);
                }
                var commits1 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) - commits0;
                checks["dos_commits_exactos"] = commits1 == 2;   // lote + fijar; consultar y QC no escriben
                // postcondición independiente: los 2 primeros con el valor fijo, el resto con su valor del lote
                var rq = link.Call(Wire.OpQuery, new Dictionary<string, object> { [Wire.DocArg] = b.Context["doc"], ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(), ["f"] = new List<object> { "id", o.Param }, ["page_size"] = 200L }, false);
                var got = new Dictionary<long, string>();
                foreach (var row in ((rq.Datos as Dictionary<string, object>)?.GetValueOrDefault("rows") as List<object>) ?? new List<object>())
                    if (row is List<object> l && l.Count >= 2) got[Convert.ToInt64(l[0], CultureInfo.InvariantCulture)] = Convert.ToString(l[1], CultureInfo.InvariantCulture);
                checks["relectura_independiente"] = o.Ids.Select((id, k) => got.TryGetValue(id, out var v) && v == (k < 2 ? "NRT-06 " + stamp + " fijo" : "NRT-06 " + stamp + " #" + id.ToString(CultureInfo.InvariantCulture))).All(x => x);
                // idempotencia: reanudar el mismo diario no escribe
                var commits2 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
                using (var again = PlanRunner.Open(stateDir, null, b, new RunnerOptions { RealWrites = "persistente" }, out _)) again?.Run();
                checks["reanudar_sin_escritura"] = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits2;
                checks["sin_guardar_ni_sync"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
            }
            return Finish(o.OutDir, "nrt06", report, checks, link);
        }
    }
}
