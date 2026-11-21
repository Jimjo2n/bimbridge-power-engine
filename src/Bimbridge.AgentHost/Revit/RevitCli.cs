// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// agenthost revit instancias | identidad --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V
    /// NRT-04 (alpha3): the operation originates in AgentHost (validated IR plan → PlanRunner → RevitBackend → add-in → plan/commit modo=ensayo
    /// → rollback → independent re-read → evidence). Nothing is saved or synced; the model must be left as it was.
    /// </summary>
    public static partial class RevitCli
    {
        public static int Run(string[] a)
        {
            if (NativeGate.Check(a) is int blocked) return blocked;   // rc1: nothing touches Revit without the operator's authorization
            switch (a[1])
            {
                case "instancias":
                    foreach (var d in InstanceDescriptor.ReadAll()) Console.WriteLine(d.Pid + " Revit " + d.RevitVersion + " add-in " + d.AddinVersion + " pipe " + d.PipeName);
                    return 0;
                case "identidad":
                {
                    var link = RevitLink.ForPid(int.Parse(Program.Opt(a, "--revit-pid", "0")), out var err);
                    if (link == null) { Console.WriteLine("error " + err); return 1; }
                    using (link)
                    {
                        var b = RevitBackend.Open(link, "lectura", out err);
                        if (b == null) { Console.WriteLine("error " + err); return 1; }
                        var c = b.Context;
                        Console.WriteLine(Json.Write(new Dictionary<string, object>
                        {
                            ["pid"] = c.GetValueOrDefault("pid"), ["revit"] = c.GetValueOrDefault("revit"), ["addin"] = c.GetValueOrDefault("addin"),
                            ["doc"] = c.GetValueOrDefault("doc"), ["documento"] = c.GetValueOrDefault("documento"), ["ruta"] = c.GetValueOrDefault("ruta"),
                            ["clave_doc"] = b.DocKey, ["huella_doc"] = b.DocPrint, ["worksharing"] = c.GetValueOrDefault("worksharing"),
                            ["modificado"] = c.GetValueOrDefault("modificado"), ["niveles_mcp"] = c.GetValueOrDefault("niveles_mcp"),
                        }));
                        return 0;
                    }
                }
                case "nrt04":
                    return Nrt04(a);
                case "nrt05":
                    return Nrt05(a);
                case "nrt03":
                    return Nrt03(a);
                case "nrt06":
                    return Nrt06(a);
                case "nrt07":
                    return Nrt07(a);
                case "campana080":
                    return RunCampaign080(a);
            }
            Console.Error.WriteLine("usage: revit instancias | identidad --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V [--negativo-modificado] [--aceptar-workshared-local]\n       revit nrt05 --revit-pid N --ruta-esperada C:\\...\\copia.rvt --autorizo-persistente --salida DIR --categoria C --ids a,b,c --parametro P [--valor V]");
            return 64;
        }

        private static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(x => x.ToString("x2"))); }

        private static int Nrt04(string[] a)
        {
            var outDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("missing --salida");
            Directory.CreateDirectory(outDir);
            var callsPath = Path.Combine(outDir, "llamadas.jsonl");
            var log = new List<string>();
            void Log(string s) { var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s; log.Add(line); Console.WriteLine(line); File.AppendAllText(Path.Combine(outDir, "nrt04.log"), line + "\n"); }
            var pid = int.Parse(Program.Opt(a, "--revit-pid", "0"));
            var cat = Program.Opt(a, "--categoria") ?? throw new ArgumentException("missing --categoria");
            var ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("missing --ids (3 to 5 elements)")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList();
            if (ids.Count < 3 || ids.Count > 5) throw new ArgumentException("--ids: between 3 and 5 elements");
            var param = Program.Opt(a, "--parametro", "Comments");
            var value = Program.Opt(a, "--valor") ?? ("NRT-04 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-04", ["version"] = AgentHostService.Version, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            report["comprobaciones"] = checks;
            void Save() => File.WriteAllText(Path.Combine(outDir, "nrt04_informe.json"), Json.Write(report));

            var link = RevitLink.ForPid(pid, out var err);
            if (link == null) { Log("BLOQUEADA: " + err); report["resultado"] = "BLOQUEADA"; report["motivo"] = err; Save(); return 3; }
            link.OnCall = c => File.AppendAllText(callsPath, Json.Write(c) + "\n");
            using (link)
            {
                // 1. identity and policy
                var backend = RevitBackend.Open(link, "ensayo", out err);
                if (backend == null) { Log("BLOQUEADA: " + err); report["resultado"] = "BLOQUEADA"; report["motivo"] = err; Save(); return 3; }
                var ctx = backend.Context;
                report["revit"] = new Dictionary<string, object>
                {
                    ["pid"] = ctx.GetValueOrDefault("pid"), ["version"] = ctx.GetValueOrDefault("revit"), ["build"] = ctx.GetValueOrDefault("build"),
                    ["addin"] = ctx.GetValueOrDefault("addin"), ["instancia"] = ctx.GetValueOrDefault("instancia"), ["pipe"] = link.Calls.LastOrDefault()?.GetValueOrDefault("pipe"),
                    ["niveles_mcp"] = ctx.GetValueOrDefault("niveles_mcp"),
                };
                report["documento"] = new Dictionary<string, object>
                {
                    ["handle"] = ctx.GetValueOrDefault("doc"), ["titulo"] = ctx.GetValueOrDefault("documento"), ["ruta"] = ctx.GetValueOrDefault("ruta"),
                    ["clave"] = backend.DocKey, ["huella"] = backend.DocPrint, ["worksharing"] = ctx.GetValueOrDefault("worksharing"),
                    ["modificado_antes"] = ctx.GetValueOrDefault("modificado"),
                };
                Log("connected pid " + pid + " · doc " + ctx.GetValueOrDefault("documento") + " · " + backend.DocKey + " · worksharing " + ctx.GetValueOrDefault("worksharing"));
                var acceptWs = a.Contains("--aceptar-workshared-local");
                var wsBlock = WorksharedPolicy(ctx, backend.DocKey, acceptWs, out var deviation);
                if (wsBlock != null) { Log("BLOQUEADA: " + wsBlock); report["resultado"] = "BLOQUEADA"; report["motivo"] = wsBlock; Save(); return 3; }
                if (deviation != null) { Log("DEVIATION AUTHORIZED BY THE OPERATOR: " + deviation); report["desviacion_operador"] = deviation; }

                // 2. independent baseline (values + relations)
                Dictionary<string, object> Snapshot(string label)
                {
                    var r = link.Call(Wire.OpQuery, new Dictionary<string, object>
                    {
                        [Wire.DocArg] = ctx["doc"], ["ids"] = ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
                        ["f"] = new List<object> { "id", "cat", "fam", "type", "lvl", "host", param }, ["page_size"] = 100L,
                    }, false);
                    var c2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                    var d = r.Data as Dictionary<string, object>;
                    var rows = d != null && d.TryGetValue("rows", out var rv) ? rv : null;
                    var json = Json.Write(rows);
                    return new Dictionary<string, object>
                    {
                        ["momento"] = label, ["estado"] = r.State, ["filas"] = rows, ["huella"] = Sha(json), ["rev"] = c2.Rev,
                        ["modificado"] = (c2.Data as Dictionary<string, object>)?.GetValueOrDefault("modificado"),
                    };
                }
                bool ValidSnapshot(Dictionary<string, object> snap, out string why)
                {
                    why = null;
                    if ((string)snap["estado"] != Status.Ok) { why = "query " + snap["estado"]; return false; }
                    var n = snap["filas"] is List<object> l ? l.Count : -1;
                    if (n != ids.Count) { why = "the snapshot has " + n + " rows and " + ids.Count + " were expected"; return false; }
                    return true;
                }
                var pre = Snapshot("antes");
                // run4 (2026-10-28): Revit leaves IsModified=false after rolling back a TransactionGroup, even if the document had unsaved
                // changes. So as not to erase that warning for anyone, the trial only runs on a document with no pending changes.
                var negative = a.Contains("--negativo-modificado");
                report["prueba"] = negative ? "NRT-04N (negative IsModified=true)" : "NRT-04";
                if (negative && deviation != null) { Log("BLOQUEADA: the negative test admits no exceptions (--aceptar-workshared-local)"); report["resultado"] = "BLOQUEADA"; report["motivo"] = "negative test on workshared"; Save(); return 3; }
                if (negative && !(pre["modificado"] is bool modN && modN)) { Log("BLOQUEADA: the negative test requires a disposable document with an unsaved change (IsModified=true)"); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "document has no pending changes"; Save(); return 3; }
                if (!negative && pre["modificado"] is bool mod0 && mod0) { Log("BLOQUEADA: the document has unsaved changes (IsModified=true); the Revit rollback would erase that warning"); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "document has unsaved changes"; Save(); return 3; }
                if (!ValidSnapshot(pre, out var whyPre)) { Log("BLOQUEADA: invalid baseline: " + whyPre); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "invalid baseline: " + whyPre; Save(); return 3; }
                report["linea_base"] = pre;
                Log("baseline " + ((string)pre["huella"]).Substring(0, 16) + " · rev " + pre["rev"]);

                // 3. IR plan (consultar + fijar_parametro trial) — validated by the AgentHost
                var draft = new PlanDraft { Objective = "NRT-04: E2E AgentHost→Revit trial on a disposable copy", DocKey = backend.DocKey, DocPrint = backend.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
                var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = cat;
                draft.Ops.Add(q);
                var w = new OpDraft { Operation = "fijar_parametro" };
                w.Scope["categoria"] = cat; w.Scope["ids"] = ids.Cast<object>().ToList();
                w.Params["parametro"] = param; w.Params["valor"] = value; w.Params["unidades"] = "texto";
                w.Preconditions.Add(new Dictionary<string, object> { ["tipo"] = "objetivos", ["min"] = 3L, ["max"] = 5L });
                w.DependsOn.Add("#0");
                draft.Ops.Add(w);
                draft.AssignIds();
                var verrs = IrValidator.Validate(draft, out var vp);
                if (verrs.Count > 0) { Log("invalid plan: " + string.Join("; ", verrs)); report["resultado"] = "FALLIDA"; report["motivo"] = verrs; Save(); return 1; }
                File.WriteAllText(Path.Combine(outDir, "plan_ir.json"), Json.Write(draft.ToJson()));
                var writeOp = vp.Ops[1];
                report["plan"] = new Dictionary<string, object> { ["hash"] = vp.Hash, ["operaciones"] = vp.Ops.Select(o => (object)(o.Id + " " + o.Operation)).ToList(), ["clave_idempotencia"] = writeOp.Id, ["autorizacion_derivada"] = writeOp.Authorization.Cast<object>().ToList() };
                Log("plan " + vp.Hash + " · write " + writeOp.Id + " · temporary value '" + value + "'");

                // 4. authorization control: the SAME plan without operator authorization must be rejected before touching anything
                using (var denied = PlanRunner.Open(Path.Combine(outDir, "estado_sin_autorizacion"), draft, backend, new RunnerOptions(), out var ddiags))
                {
                    var refused = denied == null && ddiags.Any(x => x.Code == "escrituras_no_permitidas");
                    checks["autorizacion_operador_requerida"] = refused;
                    Log("control: without operator authorization → " + (refused ? "rejected (correct)" : "NOT rejected"));
                }

                // 5. real run by the PlanRunner (journal, lease, fencing, postconditions)
                var stateDir = Path.Combine(outDir, "estado");
                string summary;
                using (var r = PlanRunner.Open(stateDir, draft, backend, new RunnerOptions { RealWrites = "ensayo" }, out var diags))
                {
                    foreach (var d in diags) Log("diag " + d);
                    if (r == null) { report["resultado"] = "FALLIDA"; report["motivo"] = diags.Select(x => (object)x.ToString()).ToList(); Save(); return 1; }
                    summary = r.Run();
                    var rec = r.State.Ops[writeOp.Id];
                    report["diario"] = new Dictionary<string, object>
                    {
                        ["resumen"] = summary, ["estado_op"] = rec.State, ["intento"] = (long)rec.Attempt, ["error"] = rec.Error, ["nota"] = rec.Note,
                        ["resultado"] = rec.Result, ["evidencia"] = rec.Evidence, ["seq"] = r.State.Seq, ["fencing"] = r.State.Fencing,
                    };
                    Log("runner: " + summary);
                }

                if (negative)
                {
                    // P0 (Codex): both paths must reject BEFORE opening any group, and the pending change and IsModified must stay intact.
                    var d0n = (Dictionary<string, object>)report["diario"];
                    var noteText = Convert.ToString(d0n.GetValueOrDefault("nota"), CultureInfo.InvariantCulture) + " " + Convert.ToString(d0n.GetValueOrDefault("error"), CultureInfo.InvariantCulture);
                    var commitCall = link.Calls.LastOrDefault(c => (string)c["op"] == Wire.OpCommit);
                    checks["commit_ensayo_rechazado_por_guardia"] = (string)d0n["estado_op"] == OpState.Failed && noteText.Contains(Engine.TrialGuard.Code)
                                                                   && commitCall != null && (string)commitCall["estado"] == Status.Rejected;
                    var er = link.Call(Wire.OpExec, new Dictionary<string, object> { [Wire.DocArg] = ctx["doc"], ["modo"] = "ensayo", ["codigo"] = "return 0;" }, true);
                    report["exec_ensayo"] = new Dictionary<string, object> { ["estado"] = er.State, ["aviso"] = er.Warning };
                    checks["exec_ensayo_rechazado_por_guardia"] = er.State == Status.Rejected && (er.Warning ?? "").Contains(Engine.TrialGuard.Code);
                    var postN = Snapshot("despues");
                    report["despues"] = postN;
                    checks["relectura_valida"] = ValidSnapshot(postN, out _);
                    checks["huella_identica"] = (bool)checks["relectura_valida"] && (string)pre["huella"] == (string)postN["huella"];
                    checks["modificado_sigue_true"] = postN["modificado"] is bool m2 && m2;
                    checks["sin_guardar_ni_sync"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
                    var passN = checks.Values.All(v => v is bool b && b);
                    report["resultado"] = passN ? "APROBADA" : "FALLIDA";
                    report["fin"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    report["llamadas"] = link.Calls.Cast<object>().ToList();
                    Save();
                    foreach (var kv in checks) Log((kv.Value is bool b3 && b3 ? "✓ " : "✗ ") + kv.Key);
                    Log("NRT-04N " + report["resultado"]);
                    return passN ? 0 : 2;
                }

                // 6. independent re-read after the rollback
                var post = Snapshot("despues");
                report["despues"] = post;
                var ctx2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false).Data as Dictionary<string, object>;
                var d0 = (Dictionary<string, object>)report["diario"];
                var ev = d0["evidencia"] as Dictionary<string, object>;
                checks["estado_op_ensayada"] = (string)d0["estado_op"] == OpState.Trialed;
                checks["grupo_rolledback_literal"] = ev != null && Convert.ToString(ev.GetValueOrDefault("addin_grupo_estado"), CultureInfo.InvariantCulture) == "RolledBack";
                checks["verificacion_temporal_addin"] = ev != null && ev.GetValueOrDefault("addin_temporal_ok") is bool t && t;
                checks["restauracion_addin"] = ev != null && ev.GetValueOrDefault("addin_restauracion_ok") is bool ro && ro;
                checks["relectura_valida"] = ValidSnapshot(post, out _);
                checks["huella_identica"] = (bool)checks["relectura_valida"] && (string)pre["huella"] == (string)post["huella"];
                // run4: the add-in rev also counts DocumentChanged from rolled-back transactions (invalidates caches: conservative).
                // Corrected and documented criterion: rev does not go back; model equality is proven by fingerprint + restoration.
                checks["rev_no_retrocede"] = Convert.ToInt64(post["rev"] ?? 0L, CultureInfo.InvariantCulture) >= Convert.ToInt64(pre["rev"] ?? 0L, CultureInfo.InvariantCulture);
                report["rev_antes_despues"] = new List<object> { pre["rev"], post["rev"] };
                checks["modificado_sin_cambios"] = Equals(pre["modificado"], post["modificado"]);
                checks["mismo_documento"] = ctx2 != null && Arg.GetString(ctx2, "clave_doc") == backend.DocKey && Arg.GetLong(ctx2, "pid") == pid;
                checks["sin_guardar_ni_sync"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
                checks["no_persistio_como_aplicado"] = (string)d0["estado_op"] != OpState.Verified;
                var pass = checks.Values.All(v => v is bool b && b);
                report["resultado"] = pass ? "APROBADA" : "FALLIDA";
                report["fin"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                report["llamadas"] = link.Calls.Cast<object>().ToList();
                Save();
                foreach (var kv in checks) Log((kv.Value is bool b2 && b2 ? "✓ " : "✗ ") + kv.Key);
                Log("NRT-04 " + report["resultado"]);
                return pass ? 0 : 2;
            }
        }
    
        /// <summary>
        /// alpha3 (2026-10-28, authorized by operator): NRT-04 requires a non-workshared document. With --aceptar-workshared-local the operator
        /// accepts a workshared one ONLY if it is a local file (not cloud/ACC/BIM 360) whose central (if any) is also local and not on a network
        /// share like \\server; the harness stays trial-only (rollback) and never syncs. Returns the blocking reason or null.
        /// </summary>
        public static string WorksharedPolicy(Dictionary<string, object> ctx, string docKey, bool accept, out string deviation)
        {
            deviation = null;
            if (!(ctx.GetValueOrDefault("worksharing") is bool ws && ws)) return null;
            if (!accept) return "the document is workshared; NRT-04 only on a disposable local copy (the operator can accept a local copy with --aceptar-workshared-local)";
            bool Local(string p) => !string.IsNullOrWhiteSpace(p) && p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            var docPath = ctx.GetValueOrDefault("ruta") as string;
            var central = ctx.GetValueOrDefault("central") as string;
            if ((docKey ?? "").StartsWith("nube:", StringComparison.OrdinalIgnoreCase)) return "workshared in the cloud: not accepted even with --aceptar-workshared-local";
            if (!Local(docPath)) return "workshared without a local drive path (" + (docPath ?? "empty") + "): not accepted";
            if (!string.IsNullOrWhiteSpace(central) && !Local(central)) return "workshared with a non-local central (" + central + "): not accepted";
            deviation = "local workshared document accepted by the operator (--aceptar-workshared-local); path " + docPath + "; central " + (central ?? "(none)") + "; trial with rollback only, no save or sync";
            return null;
        }
}
}
