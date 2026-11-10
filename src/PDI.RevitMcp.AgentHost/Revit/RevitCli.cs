// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// agenthost revit instancias | identidad --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V
    /// NRT-04 (alpha3): la operación nace en AgentHost (plan IR validado → PlanRunner → RevitBackend → add-in → plan/commit modo=ensayo
    /// → rollback → relectura independiente → evidencia). Nada se guarda ni se sincroniza; el modelo debe quedar como estaba.
    /// </summary>
    public static partial class RevitCli
    {
        public static int Run(string[] a)
        {
            if (NativeGate.Check(a) is int blocked) return blocked;   // rc1: nada toca Revit sin la autorización del operador
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
                    return Campana080(a);
            }
            Console.Error.WriteLine("uso: revit instancias | identidad --revit-pid N | nrt04 --revit-pid N --salida DIR --categoria C --ids a,b,c --parametro P --valor V [--negativo-modificado] [--aceptar-workshared-local]\n       revit nrt05 --revit-pid N --ruta-esperada C:\\...\\copia.rvt --autorizo-persistente --salida DIR --categoria C --ids a,b,c --parametro P [--valor V]");
            return 64;
        }

        private static string Sha(string s) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(x => x.ToString("x2"))); }

        private static int Nrt04(string[] a)
        {
            var outDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("falta --salida");
            Directory.CreateDirectory(outDir);
            var callsPath = Path.Combine(outDir, "llamadas.jsonl");
            var log = new List<string>();
            void Log(string s) { var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s; log.Add(line); Console.WriteLine(line); File.AppendAllText(Path.Combine(outDir, "nrt04.log"), line + "\n"); }
            var pid = int.Parse(Program.Opt(a, "--revit-pid", "0"));
            var cat = Program.Opt(a, "--categoria") ?? throw new ArgumentException("falta --categoria");
            var ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("falta --ids (3 a 5 elementos)")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList();
            if (ids.Count < 3 || ids.Count > 5) throw new ArgumentException("--ids: entre 3 y 5 elementos");
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
                // 1. identidad y política
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
                Log("conectado pid " + pid + " · doc " + ctx.GetValueOrDefault("documento") + " · " + backend.DocKey + " · worksharing " + ctx.GetValueOrDefault("worksharing"));
                var acceptWs = a.Contains("--aceptar-workshared-local");
                var wsBlock = WorksharedPolicy(ctx, backend.DocKey, acceptWs, out var deviation);
                if (wsBlock != null) { Log("BLOQUEADA: " + wsBlock); report["resultado"] = "BLOQUEADA"; report["motivo"] = wsBlock; Save(); return 3; }
                if (deviation != null) { Log("DESVIACIÓN AUTORIZADA POR EL OPERADOR: " + deviation); report["desviacion_operador"] = deviation; }

                // 2. línea base independiente (valores + relaciones)
                Dictionary<string, object> Snapshot(string label)
                {
                    var r = link.Call(Wire.OpQuery, new Dictionary<string, object>
                    {
                        [Wire.DocArg] = ctx["doc"], ["ids"] = ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
                        ["f"] = new List<object> { "id", "cat", "fam", "type", "lvl", "host", param }, ["page_size"] = 100L,
                    }, false);
                    var c2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                    var d = r.Datos as Dictionary<string, object>;
                    var rows = d != null && d.TryGetValue("rows", out var rv) ? rv : null;
                    var json = Json.Write(rows);
                    return new Dictionary<string, object>
                    {
                        ["momento"] = label, ["estado"] = r.Estado, ["filas"] = rows, ["huella"] = Sha(json), ["rev"] = c2.Rev,
                        ["modificado"] = (c2.Datos as Dictionary<string, object>)?.GetValueOrDefault("modificado"),
                    };
                }
                bool ValidSnapshot(Dictionary<string, object> snap, out string why)
                {
                    why = null;
                    if ((string)snap["estado"] != Status.Ok) { why = "consulta " + snap["estado"]; return false; }
                    var n = snap["filas"] is List<object> l ? l.Count : -1;
                    if (n != ids.Count) { why = "la instantánea tiene " + n + " filas y se esperaban " + ids.Count; return false; }
                    return true;
                }
                var pre = Snapshot("antes");
                // run4 (2026-10-28): Revit deja IsModified=false tras revertir un TransactionGroup, aunque el documento tuviera cambios
                // sin guardar. Para no borrar ese aviso a nadie, el ensayo sólo se hace sobre un documento sin cambios pendientes.
                var negative = a.Contains("--negativo-modificado");
                report["prueba"] = negative ? "NRT-04N (negativa IsModified=true)" : "NRT-04";
                if (negative && deviation != null) { Log("BLOQUEADA: la prueba negativa no admite excepciones (--aceptar-workshared-local)"); report["resultado"] = "BLOQUEADA"; report["motivo"] = "negativa sobre workshared"; Save(); return 3; }
                if (negative && !(pre["modificado"] is bool modN && modN)) { Log("BLOQUEADA: la prueba negativa exige un documento desechable con un cambio sin guardar (IsModified=true)"); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "documento sin cambios pendientes"; Save(); return 3; }
                if (!negative && pre["modificado"] is bool mod0 && mod0) { Log("BLOQUEADA: el documento tiene cambios sin guardar (IsModified=true); el rollback de Revit borraría ese aviso"); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "documento con cambios sin guardar"; Save(); return 3; }
                if (!ValidSnapshot(pre, out var whyPre)) { Log("BLOQUEADA: línea base no válida: " + whyPre); report["linea_base"] = pre; report["resultado"] = "BLOQUEADA"; report["motivo"] = "línea base no válida: " + whyPre; Save(); return 3; }
                report["linea_base"] = pre;
                Log("línea base " + ((string)pre["huella"]).Substring(0, 16) + " · rev " + pre["rev"]);

                // 3. plan IR (consultar + fijar_parametro ensayo) — validado por el AgentHost
                var draft = new PlanDraft { Objective = "NRT-04: ensayo E2E AgentHost→Revit sobre copia desechable", DocKey = backend.DocKey, DocPrint = backend.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
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
                if (verrs.Count > 0) { Log("plan inválido: " + string.Join("; ", verrs)); report["resultado"] = "FALLIDA"; report["motivo"] = verrs; Save(); return 1; }
                File.WriteAllText(Path.Combine(outDir, "plan_ir.json"), Json.Write(draft.ToJson()));
                var writeOp = vp.Ops[1];
                report["plan"] = new Dictionary<string, object> { ["hash"] = vp.Hash, ["operaciones"] = vp.Ops.Select(o => (object)(o.Id + " " + o.Operation)).ToList(), ["clave_idempotencia"] = writeOp.Id, ["autorizacion_derivada"] = writeOp.Authorization.Cast<object>().ToList() };
                Log("plan " + vp.Hash + " · escritura " + writeOp.Id + " · valor temporal «" + value + "»");

                // 4. control de autorización: el MISMO plan sin autorización del operador debe rechazarse antes de tocar nada
                using (var denied = PlanRunner.Open(Path.Combine(outDir, "estado_sin_autorizacion"), draft, backend, new RunnerOptions(), out var ddiags))
                {
                    var refused = denied == null && ddiags.Any(x => x.Code == "escrituras_no_permitidas");
                    checks["autorizacion_operador_requerida"] = refused;
                    Log("control: sin autorización del operador → " + (refused ? "rechazado (correcto)" : "NO rechazado"));
                }

                // 5. ejecución real por el PlanRunner (diario, lease, fencing, postcondiciones)
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
                    // P0 (Codex): ambas rutas deben rechazar ANTES de abrir cualquier grupo, y el cambio pendiente y IsModified quedar intactos.
                    var d0n = (Dictionary<string, object>)report["diario"];
                    var nota = Convert.ToString(d0n.GetValueOrDefault("nota"), CultureInfo.InvariantCulture) + " " + Convert.ToString(d0n.GetValueOrDefault("error"), CultureInfo.InvariantCulture);
                    var commitCall = link.Calls.LastOrDefault(c => (string)c["op"] == Wire.OpCommit);
                    checks["commit_ensayo_rechazado_por_guardia"] = (string)d0n["estado_op"] == OpState.Fallida && nota.Contains(Engine.TrialGuard.Code)
                                                                   && commitCall != null && (string)commitCall["estado"] == Status.Rechazado;
                    var er = link.Call(Wire.OpExec, new Dictionary<string, object> { [Wire.DocArg] = ctx["doc"], ["modo"] = "ensayo", ["codigo"] = "return 0;" }, true);
                    report["exec_ensayo"] = new Dictionary<string, object> { ["estado"] = er.Estado, ["aviso"] = er.Aviso };
                    checks["exec_ensayo_rechazado_por_guardia"] = er.Estado == Status.Rechazado && (er.Aviso ?? "").Contains(Engine.TrialGuard.Code);
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

                // 6. relectura independiente tras el rollback
                var post = Snapshot("despues");
                report["despues"] = post;
                var ctx2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false).Datos as Dictionary<string, object>;
                var d0 = (Dictionary<string, object>)report["diario"];
                var ev = d0["evidencia"] as Dictionary<string, object>;
                checks["estado_op_ensayada"] = (string)d0["estado_op"] == OpState.Ensayada;
                checks["grupo_rolledback_literal"] = ev != null && Convert.ToString(ev.GetValueOrDefault("addin_grupo_estado"), CultureInfo.InvariantCulture) == "RolledBack";
                checks["verificacion_temporal_addin"] = ev != null && ev.GetValueOrDefault("addin_temporal_ok") is bool t && t;
                checks["restauracion_addin"] = ev != null && ev.GetValueOrDefault("addin_restauracion_ok") is bool ro && ro;
                checks["relectura_valida"] = ValidSnapshot(post, out _);
                checks["huella_identica"] = (bool)checks["relectura_valida"] && (string)pre["huella"] == (string)post["huella"];
                // run4: el rev del add-in cuenta DocumentChanged también de transacciones revertidas (invalida cachés: conservador).
                // Criterio corregido y documentado: rev no retrocede; la igualdad del modelo la prueban huella + restauración.
                checks["rev_no_retrocede"] = Convert.ToInt64(post["rev"] ?? 0L, CultureInfo.InvariantCulture) >= Convert.ToInt64(pre["rev"] ?? 0L, CultureInfo.InvariantCulture);
                report["rev_antes_despues"] = new List<object> { pre["rev"], post["rev"] };
                checks["modificado_sin_cambios"] = Equals(pre["modificado"], post["modificado"]);
                checks["mismo_documento"] = ctx2 != null && Arg.GetString(ctx2, "clave_doc") == backend.DocKey && Arg.GetLong(ctx2, "pid") == pid;
                checks["sin_guardar_ni_sync"] = !link.Calls.Any(c => (string)c["op"] == Wire.OpDoc);
                checks["no_persistio_como_aplicado"] = (string)d0["estado_op"] != OpState.Verificada;
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
        /// alpha3 (2026-10-28, autorizado por operator): NRT-04 exige documento no workshared. Con --aceptar-workshared-local el operador
        /// acepta un workshared SOLO si es un archivo local (no nube/ACC/BIM 360) cuyo central (si existe) también es local y no es de red
        /// compartida tipo \\servidor; el arnés sigue siendo sólo ensayo (rollback) y nunca sincroniza. Devuelve el motivo de bloqueo o null.
        /// </summary>
        public static string WorksharedPolicy(Dictionary<string, object> ctx, string docKey, bool accept, out string deviation)
        {
            deviation = null;
            if (!(ctx.GetValueOrDefault("worksharing") is bool ws && ws)) return null;
            if (!accept) return "el documento es workshared; NRT-04 sólo sobre copia local desechable (el operador puede aceptar una copia local con --aceptar-workshared-local)";
            bool Local(string p) => !string.IsNullOrWhiteSpace(p) && p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            var ruta = ctx.GetValueOrDefault("ruta") as string;
            var central = ctx.GetValueOrDefault("central") as string;
            if ((docKey ?? "").StartsWith("nube:", StringComparison.OrdinalIgnoreCase)) return "workshared en la nube: no se acepta ni con --aceptar-workshared-local";
            if (!Local(ruta)) return "workshared sin ruta local de unidad (" + (ruta ?? "vacía") + "): no se acepta";
            if (!string.IsNullOrWhiteSpace(central) && !Local(central)) return "workshared con central no local (" + central + "): no se acepta";
            deviation = "documento workshared local aceptado por el operador (--aceptar-workshared-local); ruta " + ruta + "; central " + (central ?? "(ninguno)") + "; sólo ensayo con rollback, sin guardar ni sincronizar";
            return null;
        }
}
}
