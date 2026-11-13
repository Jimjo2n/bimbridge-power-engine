// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit.Families;
using PDI.RevitMcp.Addin.Scripting;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Families;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.5.0: documentos identificados (docs), runner general de C# (exec), familias y conectores (fam) y operaciones de documento (doc).
    /// Mismas protecciones que el resto: hilo de Revit por ExternalEvent, pausa del usuario, Retener, niveles, transacciones con reversión,
    /// destino fijo y diario de idempotencia.
    /// </summary>
    public sealed partial class RequestHandler
    {
        private readonly OperationJournal _ops = SafeJournal(out _opsError);
        private static string _opsError;

        /// <summary>Coordinación por documento: tareas largas y barreras toman el documento; las escrituras ajenas esperan o se rechazan.</summary>
        internal readonly DocLocks Locks = new DocLocks();

        private static OperationJournal SafeJournal(out string error)
        {
            error = null;
            try { return new OperationJournal(); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static string TargetId => DocTarget.Ambient.Value?.Id;
        private static string TargetTitle => DocTarget.Ambient.Value?.Title;

        /// <summary>Corre un paso en el hilo de Revit con el destino de la petición fijado (F04).</summary>
        private Task<WorkResult> RunMain(Func<UIApplication, Stopwatch, int, bool> step, bool highPriority, TimeSpan busyAfter, CancellationToken ct)
        {
            var target = DocTarget.Ambient.Value;
            return _main.RunAsync((app, sw, budget) =>
            {
                using (DocTarget.Enter(target)) return step(app, sw, budget);
            }, highPriority, busyAfter, ct);
        }

        /// <summary>Destino de la petición: doc= (handle, ruta, clave o título) o el activo ahora. Se resuelve fuera del hilo de Revit con la copia del registro.</summary>
        private Response ResolveTarget(Request request)
        {
            var requested = Arg.GetString(request.Args, Wire.DocArg);
            if (DocRegistry.Instance.Count == 0 && string.IsNullOrEmpty(requested)) { DocTarget.Ambient.Value = null; return null; }
            var info = DocRegistry.Instance.Find(requested, out var error, out var ambiguous);
            if (info == null)
            {
                if (string.IsNullOrEmpty(requested)) { DocTarget.Ambient.Value = null; return null; } // sin documentos conocidos todavía: comportamiento anterior
                return Response.Fail(request.Id, ambiguous ? Status.Ambiguo : Status.DestinoNoDisponible, error);
            }
            DocTarget.Ambient.Value = DocTarget.From(info, !string.IsNullOrEmpty(requested));
            return null;
        }

        /// <summary>
        /// Código efectivamente resuelto de una petición (codigo=, nombre= de un script guardado o archivo=ruta.cs). La huella del diario
        /// lo incluye (H03): cambiar el archivo cambia la huella aunque el nombre sea el mismo.
        /// </summary>
        internal static string ResolveCode(IDictionary<string, object> a, out string source, out string error)
        {
            source = null;
            error = null;
            var code = Arg.GetString(a, "codigo") ?? Arg.GetString(a, "code");
            var name = Arg.GetString(a, "nombre");
            var file = Arg.GetString(a, "archivo");
            try
            {
                if (code != null) { source = "codigo"; return code; }
                if (name != null)
                {
                    var p = ScriptSource.PathOf(name);
                    if (!File.Exists(p)) { error = "no hay un script guardado '" + name + "' (exec accion=lista)"; return null; }
                    source = "nombre:" + name;
                    return File.ReadAllText(p);
                }
                if (file != null)
                {
                    file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                    if (!Path.IsPathRooted(file)) { error = "archivo= necesita una ruta completa (p. ej. %USERPROFILE%\\Documents\\x.cs), no relativa a la carpeta de Revit"; return null; }
                    if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { error = "archivo= debe ser un .cs"; return null; }
                    if (!File.Exists(file)) { error = "archivo= no existe: " + file; return null; }
                    source = "archivo:" + Path.GetFullPath(file);
                    return File.ReadAllText(file);
                }
            }
            catch (ArgumentException ex) { error = ex.Message; return null; }
            catch (IOException ex) { error = "no se pudo leer el código: " + ex.Message; return null; }
            catch (UnauthorizedAccessException ex) { error = "no se pudo leer el código: " + ex.Message; return null; }
            return null;
        }

        private const string ResolvedCodeArg = "_codigo_resuelto";

        /// <summary>
        /// Escrituras con clave=…: se registra la intención (durable) antes y el resultado cuando el trabajo termina de verdad (H02):
        /// - misma clave ya aplicada → se devuelve el resultado guardado sin repetir;
        /// - misma clave en curso → el MISMO job_id (se espera un poco y se informa), nunca un segundo trabajo;
        /// - error, parcial, desconocido o sin final → interrumpido: reconciliar=no_aplicado (repetir) o reconciliar=aplicado (marcar hecho);
        /// - misma clave con otro contenido → inválido siempre, también al reconciliar;
        /// - si el diario no se puede escribir → diario_no_disponible y NO se aplica.
        /// </summary>
        private async Task<Response> WithJournal(Session session, Request request, Func<Task<Response>> run)
        {
            var kind = OpSemantics.Classify(request.Op, request.Args, out var invalid);
            if (kind == OpSemantics.Kind.Invalid) return Response.Fail(request.Id, Status.Invalido, invalid);
            var key = Arg.GetString(request.Args, Wire.KeyArg);
            // alpha3: un ensayo siempre se revierte; registrarlo con clave= lo dejaría como "ya aplicado" en el diario.
            if (!string.IsNullOrWhiteSpace(key) && ((request.Op == Wire.OpCommit && string.Equals((Arg.GetString(request.Args, "modo") ?? "").Trim(), "ensayo", StringComparison.OrdinalIgnoreCase))
                                                   || (request.Op == Wire.OpExec && OpSemantics.ExecMode(request.Args, out _) == OpSemantics.ModeTrial)))
                return Response.Fail(request.Id, Status.Invalido, "clave= es para cambios persistentes; un ensayo se revierte y no se registra como aplicado. Quite clave= (la correlación va en summary o tarea).");
            if (kind != OpSemantics.Kind.Write || request.Op == Wire.OpTask) return await GuardDocLock(session, request, kind, run).ConfigureAwait(false);

            // AUD-19: un plan que viene de una fuente (operaciones con origen=) entra SIEMPRE en el diario con la clave y la huella
            // derivadas de su contenido, haya pasado el agente clave= o no. Olvidar la clave no permite repetir el lote.
            string boundHash = null;
            if (request.Op == Wire.OpCommit)
            {
                var pid = Arg.GetString(request.Args, "plan_id");
                if (string.IsNullOrEmpty(pid) && request.Args.TryGetValue("ops", out var rawOps))
                {
                    try
                    {
                        if (SourceBinding.Resolve(rawOps as List<object>) != null)
                            return Response.Fail(request.Id, Status.Invalido, "commit ops= con operaciones de una fuente (origen=): use plan y después commit plan_id=…; así el lote queda protegido contra repeticiones. No se aplicó nada.");
                    }
                    catch (QueryException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }
                }
                var boundPlan = string.IsNullOrEmpty(pid) ? null : _plans.Get(pid);
                var isTrial = string.Equals((Arg.GetString(request.Args, "modo") ?? "").Trim(), "ensayo", StringComparison.OrdinalIgnoreCase);
                if (boundPlan?.BoundKey != null && !isTrial)
                {
                    var bound = new SourceBinding.Bound { Token = boundPlan.BoundToken, Key = boundPlan.BoundKey, Hash = boundPlan.BoundHash };
                    if (!SourceBinding.JournalIdentity(bound, key, out key, out boundHash, out var bindError))
                        return Response.Fail(request.Id, Status.Invalido, bindError);
                    request.Args[Wire.KeyArg] = key;
                }
            }

            string resolved = null;
            if (request.Op == Wire.OpExec)
            {
                resolved = ResolveCode(request.Args, out _, out var codeError);
                if (resolved == null && codeError != null) return Response.Fail(request.Id, Status.Invalido, codeError);
                if (resolved != null) request.Args[ResolvedCodeArg] = resolved;
            }
            if (string.IsNullOrWhiteSpace(key)) return await GuardDocLock(session, request, kind, run).ConfigureAwait(false);
            if (_ops == null)
                return Response.Fail(request.Id, Status.DiarioNoDisponible,
                    "No se aplicó nada: el diario de operaciones no está disponible (" + _opsError + "), así que clave=" + key + " no puede garantizar que no se repita. " +
                    "Corrija la carpeta de datos del MCP o, si el usuario lo acepta, repita sin clave= (sin idempotencia).");
            var target = DocTarget.Ambient.Value;
            var docKey = target?.Key ?? "(sin documento)";
            OperationJournal.ReconcileMode mode;
            try { mode = OperationJournal.ParseReconcile(request.Args.TryGetValue("reconciliar", out var rv) ? rv : null); }
            catch (ArgumentException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }
            var argsForHash = new Dictionary<string, object>(request.Args);
            argsForHash.Remove(ResolvedCodeArg);
            // Plan vinculado a una fuente: la huella es la del contenido del lote, no la del plan_id (un plan nuevo con el mismo lote es la misma operación).
            var hash = boundHash ?? OperationJournal.HashArgs(request.Op, argsForHash, resolved);
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            OperationJournal.BeginResult b;
            try { b = _ops.Begin(key, docKey, request.Op, hash, task, session.Id, mode, target?.Print, target?.Generation); }
            catch (JournalUnavailableException ex)
            {
                return Response.Fail(request.Id, Status.DiarioNoDisponible, "No se aplicó nada: " + ex.Message + ". Sin diario, clave= no puede evitar repeticiones.");
            }
            switch (b.Kind)
            {
                case OperationJournal.BeginKind.Completed:
                    return new Response
                    {
                        Id = request.Id, Estado = b.Previous.Estado ?? Status.Ok,
                        Datos = new Dictionary<string, object>
                        {
                            ["ya_aplicado"] = true, ["clave"] = key, ["cuando"] = b.Previous.Finished, ["intentos"] = (long)b.Previous.Attempts.Count,
                            ["resultado_original"] = b.Previous.Result,
                        },
                        Aviso = "Esta operación (clave " + key + ") ya se aplicó en este documento; no se repitió.",
                    };
                case OperationJournal.BeginKind.Running:
                {
                    var jid = _ops.RunningJob(key, docKey);
                    var job = string.IsNullOrEmpty(jid) ? null : _jobs.Get(jid);
                    if (job != null) return await AwaitJob(request, job, CancellationToken.None).ConfigureAwait(false);
                    return Response.Fail(request.Id, Status.EnProceso, "la operación con clave " + key + " está corriendo ahora en este Revit; espere y consulte su estado en vez de repetirla");
                }
                case OperationJournal.BeginKind.Mismatch:
                    return Response.Fail(request.Id, Status.Invalido,
                        "la clave " + key + " ya se usó para otra operación distinta en este documento (huella " + b.Previous.Hash + " ≠ " + hash + "); " +
                        "una clave identifica una sola operación, también al reconciliar. Use otra clave.");
                case OperationJournal.BeginKind.DocumentReplaced:
                    return Response.Fail(request.Id, Status.Conflicto,
                        "en " + docKey + " hay ahora OTRO modelo (huella " + target?.Print + "; la operación " + key + " se registró con " + b.Previous.DocPrint + "). " +
                        "El archivo fue sustituido: no se aplica nada. Revise qué archivo es el correcto.");
                case OperationJournal.BeginKind.PossibleCopy:
                    return new Response
                    {
                        Id = request.Id, Estado = Status.Interrumpido,
                        Datos = new Dictionary<string, object> { ["clave"] = key, ["registrada_en"] = b.OtherDoc, ["estado_alli"] = b.Previous.Estado ?? "sin final" },
                        Aviso = "Este modelo parece una copia de " + b.OtherDoc + ", donde la operación " + key + " ya consta. Lea el modelo: si ya contiene el cambio, " +
                                "repita con reconciliar=aplicado; si no, con reconciliar=no_aplicado.",
                    };
                case OperationJournal.BeginKind.Interrupted:
                    return new Response
                    {
                        Id = request.Id, Estado = Status.Interrumpido,
                        Datos = new Dictionary<string, object>
                        {
                            ["clave"] = key, ["empezo"] = b.Previous.Started, ["op"] = b.Previous.Op, ["estado_anterior"] = b.Previous.Estado ?? "sin final",
                            ["intentos"] = (long)b.Previous.Attempts.Count, ["resultado_anterior"] = b.Previous.Result,
                        },
                        Aviso = "Esta operación empezó (" + b.Previous.Started + ") y su final fue " + (b.Previous.Estado ?? "desconocido (Revit pudo cerrarse o cortarse la conexión)") +
                                ": puede haber quedado aplicada en parte. Lea el modelo y repita la MISMA orden con reconciliar=no_aplicado (no quedó nada: se aplica de nuevo) " +
                                "o reconciliar=aplicado (ya está: se marca hecha sin repetir).",
                    };
            }

            Response resp;
            try { resp = await GuardDocLock(session, request, kind, run).ConfigureAwait(false); }
            catch (Exception ex)
            {
                resp = Response.Fail(request.Id, Status.Desconocido, "la operación terminó con una excepción interna (" + ex.GetType().Name + ": " + ex.Message +
                                                                     "); no se sabe qué quedó aplicado: reconcilie antes de repetir");
            }
            // Un trabajo que sigue en Revit (en_proceso) NO está terminado: se registra su job_id y el final lo escribe el propio trabajo.
            if (resp.Estado == Status.EnProceso && resp.Datos is Dictionary<string, object> pd && Arg.GetString(pd, "job_id") is string jobId && _jobs.Get(jobId) is Job running)
            {
                try { _ops.LinkJob(key, docKey, jobId); } catch (JournalUnavailableException) { }
                _jobs.WhenFinal(running, j => _ops.Complete(key, docKey, j.Estado, JobJournalResult(j)));
                return resp;
            }
            try { _ops.Complete(key, docKey, resp.Estado, resp.Datos as Dictionary<string, object> ?? (resp.Aviso != null ? new Dictionary<string, object> { ["aviso"] = resp.Aviso } : null)); }
            catch (JournalUnavailableException ex)
            {
                resp.Aviso = (resp.Aviso == null ? "" : resp.Aviso + " ") + "ATENCIÓN: el diario no registró el final (" + ex.Message + "); si repite clave=" + key + " se pedirá reconciliar.";
            }
            return resp;
        }

        private static Dictionary<string, object> JobJournalResult(Job j)
        {
            var d = j.Result != null ? new Dictionary<string, object>(j.Result) : new Dictionary<string, object>();
            d["job_id"] = j.Id;
            if (j.Error != null) d["error"] = j.Error;
            return d;
        }

        /// <summary>
        /// Escrituras sobre un documento que tiene una tarea larga o una barrera de otro dueño: no se intercalan (documento_ocupado).
        /// Las lecturas pasan siempre.
        /// </summary>
        private async Task<Response> GuardDocLock(Session session, Request request, OpSemantics.Kind kind, Func<Task<Response>> run)
        {
            if (kind == OpSemantics.Kind.Write && request.Op == Wire.OpTask)
            {
                // Iniciar o reanudar escribe; cancelar, anotar y reconciliar una tarea deben seguir disponibles con el documento bloqueado.
                var verb = OpSemantics.TaskVerb(request.Args);
                if (verb == "iniciar" || verb == "reanudar")
                {
                    var taskBlock = WriteBarrier(request.Id, DeclaredDocs(request));
                    if (taskBlock != null) return taskBlock;
                }
            }
            if (kind != OpSemantics.Kind.Write || request.Op == Wire.OpTask) return await run().ConfigureAwait(false);
            var docs = DeclaredDocs(request);
            // commit comprueba el documento de su plan en el hilo de Revit; el resto se detiene aquí.
            if (request.Op != Wire.OpCommit)
            {
                var barrier = WriteBarrier(request.Id, docs);
                if (barrier != null) return barrier;
            }
            foreach (var d in docs)
            {
                var h = Locks.HeldByOther(d, session.Id);
                if (h != null && !IsOwnTask(request, h))
                    return Response.Fail(request.Id, Status.DocumentoOcupado,
                        "No se aplicó: el documento está tomado por " + h.Describe() + ". Espere a que termine (tarea estado) o pida al usuario que la cancele.");
            }
            // Guardar, sincronizar, exportar y cerrar son barreras: mientras corren nadie más escribe en ese documento.
            if (request.Op == Wire.OpDoc || (request.Op == Wire.OpFam && (OpSemantics.FamVerb(request.Args) == "cargar" || OpSemantics.FamVerb(request.Args) == "cargar_archivo")))
            {
                var owner = "barrera:" + session.Id + ":" + request.Id;
                var blocking = await Locks.AcquireAsync(docs, owner, "barrera", request.Op + " " + Arg.GetString(request.Args, "accion"), TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (blocking != null) return Response.Fail(request.Id, Status.DocumentoOcupado, "No se aplicó: el documento está tomado por " + blocking.Describe() + ".");
                var resp = await run().ConfigureAwait(false);
                if (resp.Estado == Status.EnProceso && resp.Datos is Dictionary<string, object> pd && Arg.GetString(pd, "job_id") is string jid && _jobs.Get(jid) is Job j)
                    _jobs.WhenFinal(j, _ => Locks.Release(docs, owner));
                else Locks.Release(docs, owner);
                return resp;
            }
            return await run().ConfigureAwait(false);
        }

        /// <summary>La propia tarea (tarea=id) puede escribir en un documento que ella misma tiene tomado.</summary>
        private static bool IsOwnTask(Request request, DocLocks.Holder h)
        {
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            return task != null && h.Kind == "tarea" && h.Detail == task;
        }

        /// <summary>Documentos que toca una escritura: el destino y, para scripts multidocumento, los de docs=d1,d2 (claves duraderas).</summary>
        private static List<string> DeclaredDocs(Request request)
        {
            var list = new List<string>();
            var t = DocTarget.Ambient.Value;
            if (t?.Key != null) list.Add(t.Key);
            foreach (var h in Arg.GetStringList(request.Args, "docs"))
            {
                var info = DocRegistry.Instance.Find(h, out _);
                if (info?.Key != null && !list.Contains(info.Key)) list.Add(info.Key);
            }
            return list;
        }

        // ---------------- docs ----------------

        private async Task<Response> Docs(Request request, CancellationToken ct)
        {
            List<DocRegistry.Info> list = null;
            var rows = new List<object>();
            var r = await RunMain((app, sw, budget) =>
            {
                list = DocRegistry.Instance.Refresh(app);
                foreach (var i in list)
                {
                    var d = DocRegistry.Instance.Resolve(app, i.Id);
                    if (d == null) continue;
                    string fam = null;
                    try { if (d.IsFamilyDocument) fam = d.OwnerFamily?.FamilyCategory?.Name; } catch (Exception) { }
                    rows.Add(new object[]
                    {
                        i.Id, d.Title, i.Kind, i.Active, d.IsModified, d.IsReadOnly, d.IsWorkshared, string.IsNullOrEmpty(d.PathName) ? "(sin guardar)" : d.PathName,
                        fam ?? "", _tracker.For(d)?.Rev ?? 0L,
                    });
                }
                return true;
            }, highPriority: true, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["instancia"] = DocRegistry.Instance.InstanceId,
                    ["cols"] = new List<object> { "doc", "titulo", "tipo", "activo", "modificado", "solo_lectura", "worksharing", "ruta", "categoria_familia", "rev" },
                    ["rows"] = rows,
                    ["uso"] = "doc=dN en cualquier orden fija ese documento aunque el usuario cambie de pestaña",
                },
            };
        }

        // ---------------- exec ----------------

        /// <summary>
        /// Runner general (0.6.0, H05). Garantías:
        /// - el resultado se convierte a JSON ANTES de confirmar la transacción: si falla, se revierte y se dice (nunca "revertido" tras un commit);
        /// - Start/Commit/Assimilate/RollBack se comprueban (también Pending); si algo queda a medias el estado es parcial o desconocido;
        /// - con tx=ninguna, los cambios reales se miden con los eventos de Revit: sin cambios = nada aplicado; con cambios y error = parcial;
        /// - consulta revierte SU transacción en el documento destino; lo que el script confirme en otros documentos o archivos no se revierte
        ///   y se informa (ya no se suspende el registro de cambios, que lo ocultaba).
        /// </summary>
        private async Task<Response> Exec(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var mode = OpSemantics.ExecMode(a, out var modeError);
            if (mode == null) return Response.Fail(request.Id, Status.Invalido, modeError);
            var tx = (Arg.GetString(a, "tx") ?? "auto").Trim().ToLowerInvariant();
            if (tx != "auto" && tx != "grupo" && tx != "ninguna") return Response.Fail(request.Id, Status.Invalido, "tx debe ser auto, grupo o ninguna");
            if (mode != OpSemantics.ModeRun && tx != "auto") return Response.Fail(request.Id, Status.Invalido, "tx=grupo/ninguna sólo con modo=ejecutar");
            var run = mode == OpSemantics.ModeRun;
            var trial = mode == OpSemantics.ModeTrial;
            // alpha3: ensayo modifica el modelo mientras corre (aunque se revierta): exige el mismo nivel W que escribir.
            if (trial && !_config.AllowsExec(true) && !(_config.Runner == "consulta" && _config.Allows('W')))
                return Response.Fail(request.Id, Status.Rechazado, "ensayo cambia el modelo temporalmente y exige nivel W (niveles " + _config.Levels +
                    "). Lo decide el usuario en la configuración del MCP. Para solo leer use modo=consulta; para probar una escritura tipada, commit modo=ensayo.");
            if (!trial && !_config.AllowsExec(run))
            {
                var why = _config.Runner == "apagado" ? "el runner de C# está apagado en la configuración de este Revit (runner=apagado)"
                    : run && _config.Runner == "consulta" ? "en este Revit el runner sólo admite consultas (runner=consulta)"
                    : "este Revit no tiene habilitados cambios por este conector (niveles " + _config.Levels + ")";
                return Response.Fail(request.Id, Status.Rechazado, why + ". Lo decide el usuario en la configuración del MCP; dígaselo. Si el usuario autorizó otra herramienta para esto, puede usarla.");
            }
            if (run && WillHold(DocTarget.Ambient.Value?.Key))
                return Response.Fail(request.Id, Status.NecesitaUsuario,
                    "Retener está activo (o hay planes en la cola): el usuario revisa cada cambio desde la cola, y un script no tiene un diff que encolar. " +
                    "Pídale que cambie el Modo a En vivo para esta tarea, o use plan/commit (que sí se encola).");

            // Código: en línea, guardado por nombre o archivo .cs (ya resuelto por el diario si había clave=)
            string codeError = null;
            var code = Arg.GetString(a, ResolvedCodeArg) ?? ResolveCode(a, out _, out codeError);
            if (code == null) return Response.Fail(request.Id, Status.Invalido, codeError ?? "falta el código (codigo=…), nombre= de un script guardado o archivo=ruta.cs");
            if (string.IsNullOrWhiteSpace(code)) return Response.Fail(request.Id, Status.Invalido, "el código está vacío");
            // Revisión consolidada: un solo control. Sin «Bloquear inyección de código C#», exec corre para el agente local autorizado
            // (tubería del usuario + nivel de config.json), sin autorización por script. Con el bloqueo, se rechaza todo exec antes de compilar.
            var blocked = DynamicCodePolicy.Decide(Ui.Runtime.BlockDynamicCode);
            if (blocked != null)
            {
                var bf = Response.Fail(request.Id, Status.Rechazado, blocked);
                bf.Datos = new Dictionary<string, object> { ["codigo"] = DynamicCodePolicy.BlockedCode };
                return bf;
            }
            var name = Arg.GetString(a, "nombre");
            try
            {
                var saveAs = Arg.GetString(a, "guardar");
                if (saveAs != null)
                {
                    Directory.CreateDirectory(ScriptSource.ScriptsFolder);
                    File.WriteAllText(ScriptSource.PathOf(saveAs), code);
                }
            }
            catch (ArgumentException ex) { return Response.Fail(request.Id, Status.Invalido, ex.Message); }

            // R04: las DLL extra se deciden ANTES de cargarlas en Revit.
            var trusted = Ui.Runtime.TrustedExecActive;
            var refs = Arg.GetStringList(a, "refs");
            var refDenied = ReferencePolicy.Denied(trusted ? ExecutionProfile.TrustedInteractive : ExecutionProfile.RestrictedAutonomous, refs,
                f => { try { return Home.FileSha256(f); } catch (Exception) { return null; } }, ReferencePolicy.LoadTrusted(), ScriptHost.AddinDir);
            if (refDenied.Count > 0)
                return Response.Fail(request.Id, Status.Rechazado, "referencia_no_confiable: " + string.Join("; ", refDenied.Take(5)) +
                    ". Sin «Código confiable» activo sólo se cargan DLL de la carpeta del add-in o listadas por SHA-256 en " + ReferencePolicy.TrustedListPath);
            // Compilar fuera del hilo de Revit (con referencias explícitas refs= si las hay)
            var compiled = await Task.Run(() =>
            {
                var c = ScriptHost.Compile(code, _revitVersion, _addinVersion, refs, out var err);
                return Tuple.Create(c, err);
            }, ct).ConfigureAwait(false);
            if (compiled.Item1 == null)
                return Response.Fail(request.Id, ScriptHost.LoadError != null && compiled.Item2 == ScriptHost.LoadError ? Status.NoSoportado : Status.Invalido, compiled.Item2);
            var script = compiled.Item1;
            if (script.Run == null)
                return Response.Fail(request.Id, Status.Invalido, "ese código es una receta (clase que hereda de Receta): iníciela con tarea iniciar, no con exec");
            var gate = CapabilityPolicy.ExecDecision(run, trusted, script.Capabilities, out var profile);
            if (gate != null) return Response.Fail(request.Id, Status.Rechazado, gate);
            var auditErr = AuditScript(session, code, script.Key, mode, tx, profile, script, refs, out var execIntent);
            if (auditErr != null && mode != OpSemantics.ModeQuery)
                return Response.Fail(request.Id, Status.Error, "auditoria_no_disponible (" + auditErr + "): por seguridad no se ejecuta código que escribe sin dejar registro durable");

            var job = _jobs.Create(session.Id, "exec " + mode);
            job.Task = Arg.GetString(a, Wire.TaskArg);
            var userArgs = a.TryGetValue("args", out var ua) && ua is Dictionary<string, object> ud ? ud : new Dictionary<string, object>();
            var target = DocTarget.Ambient.Value;
            _jobs.Run(job, async () =>
            {
                var sw = Stopwatch.StartNew();
                Dictionary<string, object> datos = null;
                string fail = null, failStatus = Status.Error;
                var failures = new FailureLog { DeleteWarnings = !(Arg.GetString(a, "avisos") == "dejar") };
                var revBefore = new Dictionary<string, long>();
                var txStates = new Dictionary<string, object>();
                List<string> serErrors = null;
                ScriptContext ctxObj = null;
                job.Phase = "esperando a Revit";
                var r = await RunMain((app, watch, budget) =>
                {
                    job.Phase = "ejecutando en Revit";
                    var doc = DocTarget.Resolve(app, out var docErr);
                    if (doc == null && target != null) { fail = docErr; failStatus = Status.DestinoNoDisponible; return true; }
                    foreach (Document d in app.Application.Documents) if (!d.IsLinked) revBefore[DocRegistry.Instance.IdOf(d)] = _tracker.For(d)?.Rev ?? 0;
                    bool AnyChange()
                    {
                        foreach (Document d in app.Application.Documents)
                        {
                            if (d.IsLinked) continue;
                            var id = DocRegistry.Instance.IdOf(d);
                            if ((_tracker.For(d)?.Rev ?? 0) != (revBefore.TryGetValue(id, out var rb) ? rb : 0)) return true;
                        }
                        return false;
                    }
                    ctxObj = new ScriptContext(app, doc, userArgs, mode, tx, job.Cts.Token, session.Sets, doc != null ? DocRegistry.Instance.IdOf(doc) : null);
                    var txName = DocumentTracker.TransactionPrefix + (run ? " exec " + (name ?? script.Key.Substring(0, 8)) : " temporal exec");
                    TransactionGroup group = null;
                    Transaction t = null;
                    var committed = false;
                    _tracker.CurrentWriter = session.Id;
                    try
                    {
                        // alpha3: consulta corre SIN transacción: Revit rechaza cualquier modificación (antes se abría una y se revertía,
                        // lo que permitía cambios temporales con nivel R). ensayo = grupo + transacción, siempre revertidos.
                        // P0 (Codex): exec ensayo revierte siempre su grupo → rechazo previo si el documento tiene cambios sin guardar.
                        if (trial && doc != null)
                        {
                            var refuse = Engine.TrialGuard.Refuse(doc.IsModified, "exec modo=ensayo");
                            if (refuse != null) { fail = refuse; failStatus = Status.Rechazado; return true; }
                        }
                        if (doc != null && (trial || (run && tx == "grupo")))
                        {
                            group = new TransactionGroup(doc, txName);
                            var gs = group.Start();
                            if (gs != TransactionStatus.Started)
                            {
                                fail = "Revit no permitió abrir el grupo de transacciones (" + gs + "); no se ejecutó el script. ¿Hay un comando o un modo de edición abierto?";
                                failStatus = Status.Revertido;
                                return true;
                            }
                        }
                        if (doc != null && (trial || (run && tx == "auto")))
                        {
                            t = new Transaction(doc, txName);
                            FailureLog.Attach(t, failures);
                            var ts = t.Start();
                            if (ts != TransactionStatus.Started)
                            {
                                fail = "Revit no permitió abrir la transacción (" + ts + "); no se ejecutó el script.";
                                failStatus = Status.Revertido;
                                if (group != null && group.HasStarted()) group.RollBack();
                                return true;
                            }
                        }

                        object value;
                        try { value = script.Run.Invoke(null, new object[] { ctxObj }); }
                        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

                        // 1) Materializar el resultado ANTES del punto irreversible.
                        object json;
                        bool trunc;
                        try { json = ResultSerializer.ToJson(value, doc, out trunc, out serErrors); }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            throw new ResultException("el script terminó, pero su resultado no se pudo convertir: " + ex.GetType().Name + ": " + ex.Message, ex);
                        }
                        datos = new Dictionary<string, object> { ["resultado"] = json };
                        if (trunc) datos["resultado_recortado"] = "se cortó a " + ResultSerializer.MaxItems + " elementos o profundidad " + ResultSerializer.MaxDepth + "; devuelva un resumen o pagine";
                        if (serErrors != null && serErrors.Count > 0) datos["errores_resultado"] = serErrors.Cast<object>().ToList();

                        // 2) Confirmar (o revertir en consulta) comprobando cada estado.
                        if (t != null)
                        {
                            if (!run)
                            {
                                var rs = t.RollBack();
                                txStates["transaccion"] = rs.ToString();
                                if (rs != TransactionStatus.RolledBack) { fail = "el ensayo no se pudo revertir (" + rs + ")"; failStatus = Status.Desconocido; }
                            }
                            else
                            {
                                var st = t.Commit();
                                if (st == TransactionStatus.Committed) committed = true;
                                else if (st == TransactionStatus.RolledBack)
                                {
                                    fail = "Revit rechazó la transacción y la revirtió: " + string.Join(" | ", failures.Errors.Take(5));
                                    failStatus = Status.Revertido;
                                }
                                else
                                {
                                    fail = "la transacción quedó en estado " + st + " (Revit no confirmó ni revirtió; puede haber un aviso pendiente de resolver en pantalla)";
                                    failStatus = Status.Desconocido;
                                }
                            }
                        }
                        if (group != null)
                        {
                            if (!run || fail != null)
                            {
                                var gr = group.RollBack();
                                txStates["grupo"] = gr.ToString();
                                if (gr != TransactionStatus.RolledBack && fail == null) { fail = "el grupo no se pudo revertir (" + gr + ")"; failStatus = Status.Desconocido; }
                            }
                            else
                            {
                                var ga = group.Assimilate();
                                if (ga == TransactionStatus.Committed) committed = true;
                                else if (ga == TransactionStatus.RolledBack) { fail = "Revit revirtió el grupo al unirlo (" + ga + ")"; failStatus = Status.Revertido; }
                                else { fail = "el grupo quedó en estado " + ga; failStatus = AnyChange() ? Status.Desconocido : Status.Revertido; }
                            }
                        }
                        if (run && tx == "ninguna") committed = AnyChange();
                    }
                    catch (Exception ex)
                    {
                        // Revertir lo nuestro y comprobar que de verdad quedó revertido.
                        var rolled = true;
                        try { if (t != null && t.GetStatus() == TransactionStatus.Started) rolled &= t.RollBack() == TransactionStatus.RolledBack; } catch (Exception) { rolled = false; }
                        try { if (group != null && group.HasStarted()) rolled &= group.RollBack() == TransactionStatus.RolledBack; } catch (Exception) { rolled = false; }
                        var changed = AnyChange();
                        var cancelled = ex is OperationCanceledException;
                        var what = ex is ResultException ? ex.Message : (cancelled ? "cancelado: " : "") + Describe(ex);
                        if (!changed && rolled)
                        {
                            fail = what + ". No quedó nada aplicado (se revirtió y Revit no registró cambios): se puede corregir y repetir.";
                            failStatus = Status.Revertido;
                        }
                        else if (tx == "ninguna" || !rolled)
                        {
                            fail = what + ". ATENCIÓN: hay cambios confirmados en el modelo (" + (tx == "ninguna" ? "transacciones propias del script" : "la reversión no se confirmó") +
                                   "): revise el modelo y reconcilie antes de repetir.";
                            failStatus = Status.Parcial;
                        }
                        else
                        {
                            fail = what + ". Se revirtió lo del script, pero Revit registró cambios en otro documento: revíselos.";
                            failStatus = Status.Parcial;
                        }
                    }
                    finally
                    {
                        _tracker.CurrentWriter = null;
                        try { t?.Dispose(); } catch (Exception) { }
                        try { group?.Dispose(); } catch (Exception) { }
                    }
                    return true;
                }, highPriority: mode == OpSemantics.ModeQuery, busyAfter: Busy, ct: job.Cts.Token).ConfigureAwait(false);

                if (!r.Ok) { AuditExecOutcome(session, execIntent, "no_ejecutado"); FailJob(job, r, write: run); return; }
                var auditGap = AuditExecOutcome(session, execIntent, fail == null ? (run ? "confirmado" : trial ? "ensayo_revertido" : "consulta") : failStatus);
                var extra = new Dictionary<string, object>
                {
                    ["modo"] = mode, ["tx"] = tx, ["forma"] = script.Kind,
                    ["compilar_ms"] = script.FromCache ? 0L : script.CompileMs, ["cache"] = script.FromCache,
                    ["total_ms"] = sw.ElapsedMilliseconds,
                };
                if (txStates.Count > 0) extra["tx_estado"] = txStates;   // estado LITERAL de Revit (RolledBack…)
                if (target != null) extra["doc"] = target.Id;
                if (script.ExtraRefs != null) extra["refs"] = script.ExtraRefs;
                if (ctxObj != null && ctxObj.LogLines.Count > 0) extra["log"] = ctxObj.LogLines.Cast<object>().ToList();
                if (failures.Items.Count > 0) extra["avisos_revit"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
                if (script.Diagnostics != null && script.Diagnostics.Length > 0) extra["compilador"] = script.Diagnostics.Cast<object>().ToList();
                // Qué cambió de verdad (según los eventos de Revit), por documento — también en consulta (lo que NO se revirtió).
                var changed = new List<object>();
                try
                {
                    await RunMain((app, w, b) =>
                    {
                        foreach (Document d in app.Application.Documents)
                        {
                            if (d.IsLinked) continue;
                            var id = DocRegistry.Instance.IdOf(d);
                            var reg = _tracker.For(d);
                            var before = revBefore.TryGetValue(id, out var rb) ? rb : 0;
                            if (reg == null || reg.Rev == before) continue;
                            var ids = reg.ChangedSince(before, out var deleted);
                            changed.Add(new Dictionary<string, object> { ["doc"] = id, ["titulo"] = d.Title, ["elementos"] = (long)ids.Count, ["borrados"] = (long)(deleted?.Count ?? 0) });
                            if (ids.Count > 0 && id == target?.Id) { try { session.Sets.Save("$exec_cambiados", ids, id, d.Title); } catch (Exception) { } }
                        }
                        return true;
                    }, highPriority: true, busyAfter: Busy, ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { extra["cambios_error"] = ex.Message; }
                if (run) extra["cambios"] = changed.Count == 0 ? (object)"ninguno" : changed;
                else if (changed.Count > 0)
                {
                    extra["efectos_no_revertidos"] = changed;
                    extra["aviso_consulta"] = "consulta revierte sólo su transacción en el documento destino; el script confirmó cambios en otros documentos y esos quedan aplicados";
                }
                if (auditGap != null)
                {
                    // rc3: resultado real pero auditoría incompleta: nunca "ok" (ni revertido si se confirmó).
                    extra["auditoria"] = AuditGate.Incompleta;
                    extra["reconciliar_intencion"] = execIntent;
                    if (fail == null && run)
                    {
                        extra["aplicado"] = true;
                        if (datos != null) foreach (var kv in extra) datos[kv.Key] = kv.Value;
                        job.Result = datos ?? extra;
                        _jobs.Fail(job, Status.AplicadoErrorResultado, auditGap);
                        return;
                    }
                }
                if (fail != null)
                {
                    job.Result = extra;
                    _jobs.Fail(job, failStatus, fail);
                    return;
                }
                if (datos == null)
                {
                    // Confirmado pero sin resultado (no debería pasar: el resultado se materializa antes). Nunca se informa como revertido.
                    extra["aplicado"] = true;
                    job.Result = extra;
                    _jobs.Fail(job, Status.AplicadoErrorResultado, "los cambios se confirmaron, pero no hay resultado que devolver");
                    return;
                }
                foreach (var kv in extra) datos[kv.Key] = kv.Value;
                var estado = !run && changed.Count > 0 ? Status.ConDiferencias : Status.Ok;
                try { Json.Write(datos); }
                catch (Exception ex)
                {
                    // Fallo al producir la respuesta DESPUÉS de confirmar: aplicado, con error de resultado (nunca rollback falso).
                    _jobs.Fail(job, run ? Status.AplicadoErrorResultado : Status.Error, "el resultado no se pudo escribir: " + ex.Message + (run ? "; los cambios SÍ quedaron aplicados" : ""));
                    job.Result = extra;
                    return;
                }
                _jobs.Complete(job, datos, estado);
            });
            var resp = await AwaitJob(request, job, ct).ConfigureAwait(false);
            if (resp.Estado != Status.Ok && resp.Estado != Status.EnProceso && job.Result != null && resp.Datos == null) resp.Datos = job.Result;
            return resp;
        }

        /// <summary>El script terminó pero su resultado no se pudo materializar (antes de confirmar: se revierte).</summary>
        private sealed class ResultException : Exception
        {
            public ResultException(string message, Exception inner) : base(message, inner) { }
        }

        private static string Describe(Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            // Línea del script (gracias a #line y al PDB): "script:línea N"
            var st = new StackTrace(ex, true);
            foreach (var f in st.GetFrames() ?? new StackFrame[0])
            {
                var file = f.GetFileName();
                if (file != null && file.EndsWith("script", StringComparison.Ordinal) && f.GetFileLineNumber() > 0)
                    return msg + " (línea " + f.GetFileLineNumber() + " del script)";
            }
            if (ex is MissingMethodException || ex is MissingMemberException || ex is TypeLoadException)
                msg += " — ese miembro no existe en este build de Revit (" + Ui.Runtime.RevitVersion + ")";
            return msg;
        }

        /// <summary>Fase 1 (R01): cada script queda en la auditoría durable ANTES de ejecutarse (intención, con SHA-256 del código, perfil,
        /// capacidades detectadas y referencias con su SHA-256); su código completo en scripts\historial.
        /// Devuelve null si quedó registrado; el motivo si no (el llamador rechaza la escritura: fail closed).</summary>
        private static string AuditScript(Session session, string code, string key, string mode, string tx, ExecutionProfile profile,
            ScriptHost.Compiled script, IList<string> refs, out string intent)
        {
            intent = null;
            string path = null;
            try
            {
                var dir = Path.Combine(ScriptSource.ScriptsFolder, "historial");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, key.Substring(0, 16) + ".cs");
                if (!File.Exists(path)) File.WriteAllText(path, code);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { path = null; }
            var extra = new Dictionary<string, object>
            {
                ["modo"] = mode, ["tx"] = tx, ["perfil"] = profile.ToString(), ["huella"] = key.Substring(0, 16), ["sha256"] = Home.Sha256Hex(code), ["archivo"] = path,
                ["capacidades_usadas"] = (script.Capabilities ?? new string[0]).Cast<object>().ToList(),
                ["refs"] = (refs ?? new List<string>()).Select(r => (object)(r + " sha256=" + SafeSha(r))).ToList(),
                ["inicio"] = code.Length > 200 ? code.Substring(0, 200) + "…" : code,
            };
            Ui.Runtime.Journal?.Log(session.Id, "exec_codigo", mode, 0, extra);
            try
            {
                if (Ui.Runtime.Audit == null) return "auditoría no inicializada";
                intent = Ui.Runtime.Audit.Intent(session.Id, "exec_codigo", DocTarget.Ambient.Value?.Key ?? "", extra);
                return null;
            }
            catch (JournalUnavailableException ex) { return ex.Message; }
        }

        private static string SafeSha(string p)
        {
            try { return Home.FileSha256(Environment.ExpandEnvironmentVariables(p)); } catch (Exception) { return "?"; }
        }

        /// <summary>Fase 2: resultado de un exec. Si falla, la intención queda pendiente para reconciliar (AuditLog.PendingIntents).</summary>
        private static string AuditExecOutcome(Session session, string intent, string estado)
        {
            if (intent == null) return null;
            try { Ui.Runtime.Audit?.Outcome(session.Id, "exec_codigo", intent, estado); return null; }
            catch (JournalUnavailableException ex)
            {
                var msg = AuditGate.Incompleta + ": el script terminó (" + estado + "; si confirmó, los cambios SÍ quedaron aplicados), pero su resultado no quedó en la auditoría (" +
                          ex.Message + "). Intención " + intent + " pendiente de reconciliar.";
                Ui.Runtime.Log("AUDITORÍA: " + msg);
                return msg;
            }
        }

        private Response ExecList(Request request)
        {
            var rows = new List<object>();
            try
            {
                if (Directory.Exists(ScriptSource.ScriptsFolder))
                    foreach (var f in Directory.GetFiles(ScriptSource.ScriptsFolder, "*.cs").OrderBy(x => x))
                        rows.Add(new object[] { Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length, File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
            }
            catch (Exception ex) { return Response.Fail(request.Id, Status.Error, ex.Message); }
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["cols"] = new List<object> { "nombre", "bytes", "modificado" }, ["rows"] = rows, ["carpeta"] = ScriptSource.ScriptsFolder,
                    ["compilador"] = ScriptHost.Present ? "presente" : "falta carpeta roslyn",
                    ["en_cache"] = (long)ScriptHost.CachedCount,
                },
            };
        }

        // ---------------- fam ----------------

        private FamilyLibrary _library;
        private FamilyLibrary Library => _library ?? (_library = new FamilyLibrary(_config.FamilyFolders, System.IO.Path.Combine(Home.Root, "familias", "indice.json")));

        /// <summary>fam buscar q=… [carpeta=…] [top=10]: candidatos de la biblioteca autorizada, compatibilidad y si ya está cargada.</summary>
        private Dictionary<string, object> FamilySearch(UIApplication app, Document doc, IDictionary<string, object> a)
        {
            var q = Arg.GetString(a, "q") ?? throw new OpFail(Status.Invalido, "fam buscar: falta q (palabras del nombre, p. ej. q=\"downlight round\")");
            var loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Select(f => f.Name).ToList();
            var ver = int.TryParse(app.Application.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 9999;
            var hits = Library.Search(q, ver, loaded, Arg.GetString(a, "carpeta"), (int)(Arg.GetLong(a, "top") ?? 10));
            return new Dictionary<string, object>
            {
                ["indexadas"] = (long)Library.Count, ["revit_destino"] = (long)ver,
                ["candidatos"] = hits.Select(h =>
                {
                    var d = h.Entry.ToJson();
                    d["ruta"] = h.Entry.Path; d["compatible"] = h.Compatible; d["ya_cargada"] = h.Loaded; d["puntos"] = Math.Round(h.Score, 2);
                    if (h.Why != null) d["motivo"] = h.Why;
                    return (object)d;
                }).ToList(),
            };
        }

        /// <summary>fam cargar_archivo ruta=… [sobrescribir=no|valores]: carga en el proyecto un RFA de la biblioteca autorizada (sin cambios desde el índice).</summary>
        private Dictionary<string, object> FamilyLoadFile(Document doc, IDictionary<string, object> a)
        {
            FamilyOps.RequireProject(doc, "fam cargar_archivo");
            var e = Library.ResolveForLoad(Arg.GetString(a, "ruta"), out var err) ?? throw new OpFail(Status.Rechazado, "fam cargar_archivo: " + err);
            var ver = int.TryParse(doc.Application.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 9999;
            if (e.RevitVersion.HasValue && e.RevitVersion.Value > ver) throw new OpFail(Status.Rechazado, "la familia es de Revit " + e.RevitVersion + " y este es " + ver);
            var mode = (Arg.GetString(a, "sobrescribir") ?? "no").Trim().ToLowerInvariant();
            if (mode != "no" && mode != "valores") throw new OpFail(Status.Invalido, "sobrescribir=no o valores");
            var existed = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            Family fam = null;
            using (var t = new Transaction(doc, DocumentTracker.TransactionPrefix + " fam cargar_archivo " + e.Name))
            {
                t.Start();
                var ok = doc.LoadFamily(e.Path, new FamilyOps.LoadOptions(mode == "valores"), out fam);
                if (!ok && existed == null) { t.RollBack(); throw new OpFail(Status.Error, "Revit no cargó la familia"); }
                if (t.Commit() != TransactionStatus.Committed) throw new OpFail(Status.Error, "la transacción de carga no se confirmó");
            }
            fam = fam ?? existed;
            var types = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Where(n => n != null).OrderBy(n => n).ToList();
            return new Dictionary<string, object>
            {
                ["familia"] = fam.Name, ["id"] = Ids.ToLong(fam.Id), ["ya_existia"] = existed != null, ["tipos"] = types.Cast<object>().ToList(),
                ["sha256"] = e.Sha256, ["ruta"] = e.Path, ["revit_archivo"] = e.RevitVersion.HasValue ? (object)(long)e.RevitVersion.Value : "desconocida",
            };
        }

        private static string FamVerb(IDictionary<string, object> a) => (Arg.GetString(a, "accion") ?? "info").Trim().ToLowerInvariant();

        private async Task<Response> Fam(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var verb = FamVerb(a);
            var write = OpSemantics.FamWrites.Contains(verb);
            if (!write && verb != "info" && verb != "caras" && verb != "buscar")
                return Response.Fail(request.Id, Status.Invalido, "fam: info, caras, buscar, abrir, conector, param, cargar o cargar_archivo");
            // 0.8.0: el índice de la biblioteca se refresca FUERA del hilo de Revit (sólo disco), antes de pedir turno.
            if (verb == "buscar" || verb == "cargar_archivo")
            {
                if (Library.Roots.Count == 0)
                    return Response.Fail(request.Id, Status.NecesitaUsuario, "no hay carpetas de familias autorizadas: el usuario debe añadirlas en la configuración (carpetas_familias)");
                if (Library.Count == 0 || Arg.GetBool(a, "refrescar") == true) await Task.Run(() => Library.Refresh(), ct).ConfigureAwait(false);
            }
            if (write)
            {
                if (!_config.Allows('W'))
                    return Response.Fail(request.Id, Status.Rechazado, "este Revit no tiene habilitados cambios por este conector (niveles " + _config.Levels + "). Dígaselo al usuario; lo cambia en la configuración del MCP.");
                if (WillHold(DocTarget.Ambient.Value?.Key))
                    return Response.Fail(request.Id, Status.NecesitaUsuario, "Retener está activo: los cambios de familia no se encolan. Pida al usuario que cambie el Modo a En vivo para esta tarea.");
            }
            Dictionary<string, object> datos = null;
            string fail = null, failStatus = Status.Error;
            var changes = new List<string>();
            var diffs = new List<string>();
            var pending = new List<string>();
            var failures = new FailureLog();
            var sw = Stopwatch.StartNew();
            var r = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { fail = docErr; failStatus = Status.DestinoNoDisponible; return true; }
                try
                {
                    switch (verb)
                    {
                        case "info": datos = FamilyOps.Info(app, doc, a); break;
                        case "caras": datos = FamilyOps.FacesInfo(doc, a); break;
                        case "buscar": datos = FamilySearch(app, doc, a); break;
                        case "cargar_archivo":
                            datos = FamilyLoadFile(doc, a);
                            changes.Add("familia " + datos["familia"] + " cargada desde la biblioteca");
                            break;
                        case "abrir": datos = FamilyOps.Open(app, doc, a); break;
                        case "cargar":
                            datos = FamilyOps.Load(app, doc, a, diffs);
                            changes.Add("familia cargada en " + datos["cargada_en"]);
                            break;
                        case "conector":
                        case "param":
                            var q = verb == "conector" ? FamilyOps.ConnectorRequest.Parse(a) : null;
                            FamilyOps.RequireFamily(doc, "fam " + verb);
                            _tracker.CurrentWriter = session.Id;
                            using (var t = new Transaction(doc, DocumentTracker.TransactionPrefix + " fam " + verb + (q != null ? " " + q.Key : "")))
                            {
                                FailureLog.Attach(t, failures);
                                t.Start();
                                try
                                {
                                    datos = verb == "conector" ? FamilyOps.UpsertConnector(doc, q, changes, diffs, pending) : FamilyOps.Param(doc, a, changes, diffs);
                                }
                                catch (Exception)
                                {
                                    t.RollBack();
                                    throw;
                                }
                                if (changes.Count == 0) t.RollBack();
                                else if (t.Commit() != TransactionStatus.Committed)
                                {
                                    fail = "Revit rechazó el cambio y se revirtió: " + string.Join(" | ", failures.Errors.Take(5));
                                    failStatus = Status.Revertido;
                                }
                            }
                            break;
                    }
                }
                catch (OpFail ex) { fail = ex.Message; failStatus = ex.Estado; }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { fail = ex.GetType().Name + ": " + ex.Message; failStatus = Status.Error; }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = Describe(ex); failStatus = Status.Error; }
                finally { _tracker.CurrentWriter = null; }
                return true;
            }, highPriority: !write, busyAfter: Busy, ct: ct).ConfigureAwait(false);
            if (!r.Ok) return FromWork(request.Id, r);
            if (fail != null)
            {
                var d = new Dictionary<string, object>();
                if (failures.Items.Count > 0) d["avisos_revit"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
                return new Response { Id = request.Id, Estado = failStatus, Aviso = fail, Datos = d.Count > 0 ? d : null };
            }
            if (write)
            {
                datos["cambios"] = changes.Count == 0 ? (object)"ninguno (ya estaba como se pidió)" : changes.Cast<object>().ToList();
                if (pending.Count > 0) datos["pendiente"] = pending.Cast<object>().ToList();
                if (failures.Items.Count > 0) datos["avisos_revit"] = failures.Items.Select(i => (object)i.ToJson()).ToList();
            }
            if (diffs.Count > 0) datos["diferencias"] = diffs.Cast<object>().ToList();
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response
            {
                Id = request.Id,
                Estado = diffs.Count > 0 ? Status.ConDiferencias : Status.Ok,
                Datos = datos,
                Aviso = diffs.Count > 0 ? "Se aplicó, pero la verificación encontró diferencias con lo pedido: revíselas." : null,
            };
        }

        // ---------------- doc ----------------

        private async Task<Response> DocOp(Session session, Request request, CancellationToken ct)
        {
            var a = request.Args;
            var verb = (Arg.GetString(a, "accion") ?? "").Trim().ToLowerInvariant();
            if (verb == "guardar_como") { verb = "guardar"; if (Arg.GetString(a, "como") == null) return Response.Fail(request.Id, Status.Invalido, "guardar_como necesita como=ruta"); }
            if (verb != "guardar" && verb != "sync" && verb != "sincronizar" && verb != "pdf" && verb != "ifc" && verb != "tabla_csv" && verb != "cerrar" && verb != "verificar_disco")
                return Response.Fail(request.Id, Status.Invalido, "doc: guardar, guardar_como, sync, pdf, ifc, tabla_csv, cerrar o verificar_disco");
            if (!_config.AllowsDocOps)
                return Response.Fail(request.Id, Status.Rechazado, "este Revit no tiene habilitado guardar, sincronizar ni exportar por este conector (niveles " + _config.Levels + "). Dígaselo al usuario.");
            var diffs = new List<string>();
            var pend = new DocOps.Pending();
            Dictionary<string, object> datos = null;
            string fail = null, failStatus = Status.Error;
            var sw = Stopwatch.StartNew();
            var job = _jobs.Create(session.Id, "doc " + verb);
            job.Task = Arg.GetString(a, Wire.TaskArg);
            async Task Body()
            {
                job.Phase = "esperando a Revit";
                var r = await RunMain((app, watch, budget) =>
                {
                    job.Phase = "en Revit: " + verb;
                    var doc = DocTarget.Resolve(app, out var docErr);
                    if (doc == null) { fail = docErr; failStatus = Status.DestinoNoDisponible; return true; }
                    try
                    {
                        switch (verb)
                        {
                            case "guardar": datos = DocOps.Save(app, doc, a, pend, diffs); break;
                            case "sync": case "sincronizar": datos = DocOps.Sync(doc, a, diffs, _tracker); break;
                            case "pdf": datos = DocOps.Pdf(doc, a, session.Sets, DocRegistry.Instance.IdOf(doc), pend, diffs); break;
                            case "ifc": datos = ExportOps.Ifc(doc, a, pend, diffs); break;
                            case "tabla_csv": datos = ExportOps.ScheduleCsv(doc, a, pend, diffs); break;
                            case "cerrar": datos = DocOps.Close(app, doc, a); break;
                            case "verificar_disco": datos = DocOps.VerifyOnDisk(app, doc, a); break;
                        }
                    }
                    catch (OpFail ex) { fail = ex.Message; failStatus = ex.Estado; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { fail = ex.GetType().Name + ": " + ex.Message; failStatus = Status.Error; }
                    return true;
                }, highPriority: false, busyAfter: Busy, ct: job.Cts.Token).ConfigureAwait(false);
                if (!r.Ok) { FailJob(job, r, write: true); return; }
                if (fail != null) { _jobs.Fail(job, failStatus, fail); return; }
                if (pend.Files.Count > 0)
                {
                    job.Phase = "verificando archivos";
                    var expected = datos.TryGetValue("n_hojas", out var nh) && nh is long l ? (int)l : 0;
                    datos["archivos"] = DocOps.VerifyFiles(pend, expected, diffs);
                }
                if (diffs.Count > 0) datos["diferencias"] = diffs.Cast<object>().ToList();
                datos["ms"] = sw.ElapsedMilliseconds;
                _jobs.Complete(job, datos);
            }
            // 0.9.1 (Bloque I): las operaciones de documento pasan por el planificador: reparto justo entre agentes y una sola a la vez
            // por documento. En cola no se ha tocado nada: cola llena o cancelación en cola terminan como rechazo (reintentable).
            var owner = Correlation.Current?.AgentId ?? session.Id;
            var exclusive = "doc:" + (TargetId ?? "activo");
            _jobs.Run(job, async () =>
            {
                var started = false;
                job.Phase = "en cola del planificador";
                try { await _scheduler.Run(owner, exclusive, async () => { started = true; await Body().ConfigureAwait(false); return true; }, job.Cts.Token).ConfigureAwait(false); }
                catch (SchedulerBusyException ex) { _jobs.Fail(job, Status.Rechazado, ex.Message + ". No se aplicó nada."); }
                catch (OperationCanceledException) when (!started) { _jobs.Fail(job, Status.Rechazado, "cancelado en la cola del planificador antes de empezar: no se aplicó nada"); }
            });
            var resp = await AwaitJob(request, job, ct).ConfigureAwait(false);
            if (resp.Estado == Status.Ok && diffs.Count > 0)
            {
                resp.Estado = Status.ConDiferencias;
                resp.Aviso = "Terminó, pero la verificación encontró diferencias: revíselas.";
            }
            return resp;
        }
    }
}
