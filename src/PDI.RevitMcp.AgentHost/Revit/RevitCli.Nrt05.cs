// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Parámetros de NRT-05 (beta1). Todos los decide el operador al lanzar; el plan no puede cambiarlos.</summary>
    public sealed class Nrt05Options
    {
        public int Pid;
        public string OutDir, Category, Param = "Comments", Value, ExpectedPath;
        public List<long> Ids = new List<long>();
        /// <summary>--autorizo-persistente: autorización explícita del operador para escribir, guardar y verificar en disco.</summary>
        public bool Authorized;
        public TimeSpan DocTimeout = TimeSpan.FromMinutes(10);
        /// <summary>Ruta de Revit → ruta legible por este proceso (identidad en la PC; las pruebas la redirigen a un temporal).</summary>
        public Func<string, string> DiskPath = p => p;
    }

    /// <summary>
    /// NRT-05 (beta1): escritura PERSISTENTE real nacida en AgentHost, y su persistencia en disco.
    /// BIM Task IR → AgentHost (PlanRunner, diario, lease, fencing) → RevitBackend persistente → add-in → commit → observe → verify
    /// → doc guardar → SHA-256 del .rvt (debe CAMBIAR) → doc verificar_disco (abre una COPIA del archivo guardado en segundo plano
    /// y relee los valores) → reanudar el mismo diario (no debe haber un segundo commit) → evidencia.
    /// Sólo sobre un .rvt local, no workshared, desechable, cuya ruta real coincide con --ruta-esperada. Nunca sincroniza ni cierra.
    /// </summary>
    public static partial class RevitCli
    {
        private static int Nrt05(string[] a)
        {
            var o = new Nrt05Options
            {
                Pid = int.Parse(Program.Opt(a, "--revit-pid", "0"), CultureInfo.InvariantCulture),
                OutDir = Program.Opt(a, "--salida") ?? throw new ArgumentException("falta --salida"),
                Category = Program.Opt(a, "--categoria") ?? throw new ArgumentException("falta --categoria"),
                Param = Program.Opt(a, "--parametro", "Comments"),
                Value = Program.Opt(a, "--valor"),
                ExpectedPath = Program.Opt(a, "--ruta-esperada"),
                Authorized = a.Contains("--autorizo-persistente"),
                Ids = (Program.Opt(a, "--ids") ?? throw new ArgumentException("falta --ids (3 a 5 elementos)")).Split(',').Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToList(),
            };
            var link = RevitLink.ForPid(o.Pid, out var err);
            if (link == null)
            {
                Directory.CreateDirectory(o.OutDir);
                File.WriteAllText(Path.Combine(o.OutDir, "nrt05_informe.json"), Json.Write(new Dictionary<string, object> { ["prueba"] = "NRT-05", ["resultado"] = "BLOQUEADA", ["motivo"] = err }));
                Console.WriteLine("BLOQUEADA: " + err);
                return 3;
            }
            using (link) return Nrt05Run(link, o);
        }

        public static string FileSha256(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var h = SHA256.Create())
                return string.Concat(h.ComputeHash(s).Select(x => x.ToString("x2")));
        }

        /// <summary>Núcleo de NRT-05 sobre una conexión ya abierta (también lo usan las pruebas con un add-in falso).</summary>
        public static int Nrt05Run(RevitLink link, Nrt05Options o)
        {
            Directory.CreateDirectory(o.OutDir);
            var callsPath = Path.Combine(o.OutDir, "llamadas.jsonl");
            void Log(string s)
            {
                var line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s;
                Console.WriteLine(line); File.AppendAllText(Path.Combine(o.OutDir, "nrt05.log"), line + "\n");
            }
            var value = o.Value ?? ("NRT-05 " + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            var report = new Dictionary<string, object> { ["prueba"] = "NRT-05", ["version"] = AgentHostService.Version, ["inicio"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            var checks = new Dictionary<string, object>();
            report["comprobaciones"] = checks;
            void Save() => File.WriteAllText(Path.Combine(o.OutDir, "nrt05_informe.json"), Json.Write(report));
            int Blocked(string why) { Log("BLOQUEADA: " + why); report["resultado"] = "BLOQUEADA"; report["motivo"] = why; report["llamadas"] = link.Calls.Cast<object>().ToList(); Save(); return 3; }
            int Failed(object why) { Log("FALLIDA: " + Json.Write(why)); report["resultado"] = "FALLIDA"; report["motivo"] = why; report["llamadas"] = link.Calls.Cast<object>().ToList(); Save(); return 1; }

            if (!o.Authorized) return Blocked("falta --autorizo-persistente: NRT-05 guarda el archivo; sólo con autorización explícita del operador");
            if (o.Ids.Count < 3 || o.Ids.Count > 5) return Blocked("--ids: entre 3 y 5 elementos");
            if (string.IsNullOrWhiteSpace(o.ExpectedPath)) return Blocked("falta --ruta-esperada (la ruta real del .rvt desechable)");
            link.OnCall = c => File.AppendAllText(callsPath, Json.Write(c) + "\n");

            // 1. identidad + política: el backend persistente ya rechaza workshared, nube/no local, sólo lectura y cola retenida.
            var backend = RevitBackend.Open(link, "persistente", out var err);
            if (backend == null) return Blocked(err);
            var ctx = backend.Context;
            var ruta = ctx.GetValueOrDefault("ruta") as string;
            report["revit"] = new Dictionary<string, object>
            {
                ["pid"] = ctx.GetValueOrDefault("pid"), ["version"] = ctx.GetValueOrDefault("revit"), ["build"] = ctx.GetValueOrDefault("build"),
                ["addin"] = ctx.GetValueOrDefault("addin"), ["instancia"] = ctx.GetValueOrDefault("instancia"), ["pipe"] = link.Calls.LastOrDefault()?.GetValueOrDefault("pipe"),
                ["niveles_mcp"] = ctx.GetValueOrDefault("niveles_mcp"),
            };
            report["documento"] = new Dictionary<string, object>
            {
                ["handle"] = ctx.GetValueOrDefault("doc"), ["titulo"] = ctx.GetValueOrDefault("documento"), ["ruta"] = ruta, ["clave"] = backend.DocKey,
                ["huella"] = backend.DocPrint, ["worksharing"] = ctx.GetValueOrDefault("worksharing"), ["modificado_antes"] = ctx.GetValueOrDefault("modificado"),
            };
            Log("conectado pid " + o.Pid + " · " + ruta + " · " + backend.DocKey);
            string Norm(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
            if (!string.Equals(Norm(ruta), Norm(o.ExpectedPath), StringComparison.OrdinalIgnoreCase))
                return Blocked("la ruta real del documento (" + ruta + ") no es la autorizada (" + o.ExpectedPath + ")");
            var file = o.DiskPath(ruta);
            if (!File.Exists(file)) return Blocked("no se encuentra el archivo en disco: " + ruta);

            Dictionary<string, object> Snapshot(string label)
            {
                var r = link.Call(Wire.OpQuery, new Dictionary<string, object>
                {
                    [Wire.DocArg] = ctx["doc"], ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
                    ["f"] = new List<object> { "id", o.Param }, ["page_size"] = 100L,
                }, false);
                var c2 = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
                var rows = (r.Datos as Dictionary<string, object>)?.GetValueOrDefault("rows") as List<object>;
                var vals = new Dictionary<string, object>();
                foreach (var row in rows ?? new List<object>())
                    if (row is List<object> l && l.Count >= 2) vals[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = l[1];
                return new Dictionary<string, object>
                {
                    ["momento"] = label, ["estado"] = r.Estado, ["valores"] = vals, ["rev"] = c2.Rev,
                    ["modificado"] = (c2.Datos as Dictionary<string, object>)?.GetValueOrDefault("modificado"),
                    ["clave_doc"] = (c2.Datos as Dictionary<string, object>)?.GetValueOrDefault("clave_doc"),
                };
            }
            bool AllEqual(Dictionary<string, object> vals, string v) =>
                vals.Count == o.Ids.Count && o.Ids.All(i => vals.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var x) && Equals(Convert.ToString(x, CultureInfo.InvariantCulture), v));

            // 2. línea base: documento sin cambios pendientes (el guardado del arnés no debe arrastrar cambios ajenos) y SHA del archivo.
            var pre = Snapshot("antes");
            report["linea_base"] = pre;
            if (pre["estado"] as string != Status.Ok || ((Dictionary<string, object>)pre["valores"]).Count != o.Ids.Count) return Blocked("línea base no válida (" + pre["estado"] + ")");
            if (pre["modificado"] is bool m0 && m0) return Blocked("el documento tiene cambios sin guardar; NRT-05 guardaría cambios que no son suyos");
            if (AllEqual((Dictionary<string, object>)pre["valores"], value)) return Blocked("los elementos ya tienen el valor «" + value + "»: el guardado no probaría nada");
            var shaBefore = FileSha256(file);
            report["sha256_antes"] = shaBefore;
            Log("línea base · rev " + pre["rev"] + " · sha " + shaBefore.Substring(0, 16));

            // 3. plan IR
            var draft = new PlanDraft { Objective = "NRT-05: escritura persistente E2E AgentHost→Revit sobre copia desechable", DocKey = backend.DocKey, DocPrint = backend.DocPrint, LengthUnit = "mm", Coordinates = "interno_revit" };
            var q = new OpDraft { Operation = "consultar" }; q.Params["categoria"] = o.Category;
            draft.Ops.Add(q);
            var w = new OpDraft { Operation = "fijar_parametro" };
            w.Scope["categoria"] = o.Category; w.Scope["ids"] = o.Ids.Cast<object>().ToList();
            w.Params["parametro"] = o.Param; w.Params["valor"] = value; w.Params["unidades"] = "texto";
            w.Preconditions.Add(new Dictionary<string, object> { ["tipo"] = "objetivos", ["min"] = 3L, ["max"] = 5L });
            w.DependsOn.Add("#0");
            draft.Ops.Add(w);
            draft.AssignIds();
            var verrs = IrValidator.Validate(draft, out var vp);
            if (verrs.Count > 0) return Failed(verrs);
            File.WriteAllText(Path.Combine(o.OutDir, "plan_ir.json"), Json.Write(draft.ToJson()));
            var writeOp = vp.Ops[1];
            report["plan"] = new Dictionary<string, object>
            {
                ["hash"] = vp.Hash, ["operaciones"] = vp.Ops.Select(x => (object)(x.Id + " " + x.Operation)).ToList(), ["op_id"] = writeOp.Id,
                ["autorizacion_derivada"] = writeOp.Authorization.Cast<object>().ToList(),
                ["valores_originales"] = pre["valores"], ["valor_esperado"] = value,
            };
            Log("plan " + vp.Hash + " · escritura " + writeOp.Id + " · valor «" + value + "»");

            // 4. control: sin autorización del operador el mismo plan se rechaza antes de tocar nada
            using (var denied = PlanRunner.Open(Path.Combine(o.OutDir, "estado_sin_autorizacion"), draft, backend, new RunnerOptions(), out var ddiags))
                checks["autorizacion_operador_requerida"] = denied == null && ddiags.Any(x => x.Code == "escrituras_no_permitidas");

            // 5. ejecución persistente (commit real) por el PlanRunner
            var stateDir = Path.Combine(o.OutDir, "estado");
            using (var r = PlanRunner.Open(stateDir, draft, backend, new RunnerOptions { RealWrites = "persistente" }, out var diags))
            {
                if (r == null) return Failed(diags.Select(x => (object)x.ToString()).ToList());
                var summary = r.Run();
                var rec = r.State.Ops[writeOp.Id];
                report["diario"] = new Dictionary<string, object>
                {
                    ["resumen"] = summary, ["estado_op"] = rec.State, ["estado_directiva"] = OpState.DirectiveName(rec.State, wroteNothing: rec.Attempt == 0),
                    ["intento"] = (long)rec.Attempt, ["error"] = rec.Error, ["nota"] = rec.Note, ["resultado"] = rec.Result, ["evidencia"] = rec.Evidence,
                    ["seq"] = r.State.Seq, ["fencing"] = r.State.Fencing, ["clave_idempotencia"] = writeOp.Id + "#" + r.State.Fencing.ToString(CultureInfo.InvariantCulture),
                    ["ruta_diario"] = r.State.JournalPath,
                };
                Log("runner: " + summary + " · " + rec.State);
            }
            var d0 = (Dictionary<string, object>)report["diario"];
            checks["estado_op_verificada"] = (string)d0["estado_op"] == OpState.Verificada;
            var commits1 = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit);
            checks["un_solo_commit"] = commits1 == 1;
            report["transaccion"] = link.Calls.Where(c => (string)c["op"] == Wire.OpCommit).Select(c => (object)new Dictionary<string, object> { ["estado"] = c["estado"], ["ms"] = c["ms"], ["modo"] = c.GetValueOrDefault("modo") }).ToList();

            // 6. observación independiente tras el commit (en memoria)
            var post = Snapshot("tras_commit");
            report["tras_commit"] = post;
            checks["valores_tras_commit"] = AllEqual((Dictionary<string, object>)post["valores"], value);
            checks["mismo_documento"] = (post["clave_doc"] as string) == backend.DocKey;
            checks["modificado_tras_commit"] = post["modificado"] is bool m1 && m1;
            // rc1 P0: guardado condicionado. CUALQUIER comprobación previa negativa (autorización, op, commit único, valores,
            // documento, modificado) detiene el arnés antes de guardar; nada potencialmente persistente se hace después.
            var failedBefore = checks.Where(kv => !(kv.Value is bool ok0 && ok0)).Select(kv => kv.Key).ToList();
            if (failedBefore.Count > 0)
            {
                report["resultado"] = "FALLIDA"; report["motivo"] = "comprobaciones previas negativas (" + string.Join(", ", failedBefore) + "): no se guarda"; report["llamadas"] = link.Calls.Cast<object>().ToList(); Save();
                Log("FALLIDA antes de guardar: " + string.Join(", ", failedBefore));
                return 2;
            }

            // 7. guardar (evento distinto del commit) y comprobar que el archivo CAMBIÓ en disco
            var oldTimeout = link.WriteTimeout;
            link.WriteTimeout = o.DocTimeout;
            var save = link.Call(Wire.OpDoc, new Dictionary<string, object> { [Wire.DocArg] = ctx["doc"], ["accion"] = "guardar" }, true);
            report["guardar"] = new Dictionary<string, object> { ["estado"] = save.Estado, ["aviso"] = save.Aviso, ["datos"] = save.Datos };
            checks["guardado_ok"] = save.Estado == Status.Ok;
            var shaAfter = File.Exists(file) ? FileSha256(file) : null;
            report["sha256_despues"] = shaAfter;
            checks["sha256_cambio"] = shaAfter != null && shaAfter != shaBefore;
            var afterSave = Snapshot("tras_guardar");
            report["tras_guardar"] = afterSave;
            checks["sin_cambios_pendientes_tras_guardar"] = afterSave["modificado"] is bool m2 && !m2;
            Log("guardar " + save.Estado + " · sha " + (shaAfter ?? "?").Substring(0, Math.Min(16, (shaAfter ?? "?").Length)));

            if (!(bool)checks["guardado_ok"] || !(bool)checks["sha256_cambio"] || !(bool)checks["sin_cambios_pendientes_tras_guardar"])
            {
                report["resultado"] = "FALLIDA"; report["motivo"] = "el guardado no quedó demostrado: no se verifica en disco ni se reanuda"; report["llamadas"] = link.Calls.Cast<object>().ToList(); Save();
                foreach (var kv in checks) Log((kv.Value is bool bx && bx ? "✓ " : "✗ ") + kv.Key);
                Log("NRT-05 FALLIDA tras guardar");
                return 2;
            }

            // 8. persistencia: abrir una COPIA del archivo guardado (otro Document, sin tocar el abierto) y releer
            var disk = link.Call(Wire.OpDoc, new Dictionary<string, object>
            {
                [Wire.DocArg] = ctx["doc"], ["accion"] = "verificar_disco", ["parametro"] = o.Param,
                ["ids"] = o.Ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList(),
            }, true);
            link.WriteTimeout = oldTimeout;
            var dd = disk.Datos as Dictionary<string, object>;
            report["verificacion_disco"] = new Dictionary<string, object> { ["estado"] = disk.Estado, ["aviso"] = disk.Aviso, ["datos"] = dd };
            var diskVals = new Dictionary<string, object>();
            foreach (var row in (dd?.GetValueOrDefault("filas") as List<object>) ?? new List<object>())
                if (row is List<object> l && l.Count >= 3 && l[1] is bool ex && ex) diskVals[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = l[2];
            checks["disco_valores_esperados"] = disk.Estado == Status.Ok && AllEqual(diskVals, value);
            checks["disco_sha_es_el_guardado"] = dd != null && string.Equals(dd.GetValueOrDefault("sha256") as string, shaAfter, StringComparison.OrdinalIgnoreCase);
            Log("verificar_disco " + disk.Estado);

            // 9. duplicado: reanudar EL MISMO diario no debe producir un segundo commit ni cambiar el modelo
            using (var again = PlanRunner.Open(stateDir, null, backend, new RunnerOptions { RealWrites = "persistente" }, out var diags2))
            {
                if (again != null) report["reanudacion"] = again.Run();
                else report["reanudacion"] = diags2.Select(x => (object)x.ToString()).ToList();
            }
            checks["sin_commit_duplicado"] = link.Calls.Count(c => (string)c["op"] == Wire.OpCommit) == commits1;
            var post2 = Snapshot("tras_reanudar");
            report["tras_reanudar"] = post2;
            checks["reanudar_no_modifica"] = post2["modificado"] is bool m3 && !m3 && AllEqual((Dictionary<string, object>)post2["valores"], value);

            // 10. métricas (de llamadas.jsonl)
            report["metricas"] = link.Calls.GroupBy(c => (string)c["op"]).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object>
            {
                ["n"] = (long)g.Count(), ["ms_total"] = g.Sum(c => Convert.ToInt64(c["ms"], CultureInfo.InvariantCulture)),
                ["ms_max"] = g.Max(c => Convert.ToInt64(c["ms"], CultureInfo.InvariantCulture)),
                ["bytes_peticion"] = g.Sum(c => Convert.ToInt64(c["bytes_peticion"], CultureInfo.InvariantCulture)),
                ["bytes_respuesta"] = g.Sum(c => Convert.ToInt64(c["bytes_respuesta"], CultureInfo.InvariantCulture)),
            });
            var pass = checks.Values.All(v => v is bool b && b);
            report["resultado"] = pass ? "APROBADA" : "FALLIDA";
            report["fin"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report["llamadas"] = link.Calls.Cast<object>().ToList();
            Save();
            foreach (var kv in checks) Log((kv.Value is bool b2 && b2 ? "✓ " : "✗ ") + kv.Key);
            Log("NRT-05 " + report["resultado"]);
            return pass ? 0 : 2;
        }
    }
}
