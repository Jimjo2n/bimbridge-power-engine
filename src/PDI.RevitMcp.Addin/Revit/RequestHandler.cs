// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit.Geo;
using PDI.RevitMcp.Addin.Revit.Writes;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Geo;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Enrutador de peticiones: lectura (fase 1-2) y escritura tipada plan/commit/job (fase 3). Corre en hilos de pipe; todo lo que toca
    /// la API pasa por MainThread. Nunca lanza: los errores vuelven como estados claros.
    /// </summary>
    public sealed partial class RequestHandler : IRequestHandler
    {
        private readonly MainThread _main;
        private readonly DocumentTracker _tracker;
        private readonly EngineConfig _config;
        private readonly string _revitVersion;
        private readonly string _addinVersion;
        private readonly PlanStore _plans;
        private readonly HoldSwitch _hold;
        private readonly PauseSwitch _pause;
        private readonly JobStore _jobs = new JobStore();
        private readonly ResultCache _cache = new ResultCache();
        /// <summary>Issue #2: instantáneas de consultas paginadas (una lectura del modelo, páginas desde memoria).</summary>
        private readonly QuerySnapshotStore _snapshots = new QuerySnapshotStore();

        public RequestHandler(MainThread main, DocumentTracker tracker, EngineConfig config, string revitVersion, string addinVersion,
            PlanStore plans, HoldSwitch hold, PauseSwitch pause)
        {
            _pause = pause;
            HookHistory();
            _plans = plans;
            _hold = hold;
            _main = main;
            _tracker = tracker;
            _config = config;
            _revitVersion = revitVersion;
            _addinVersion = addinVersion;
            // Guardar como cambia la clave duradera: el diario y las tareas siguen al documento nuevo.
            _tracker.DocumentRekeyed += OnRekeyed;
            _tracker.DocumentHandleClosed += _snapshots.DropDocument;
            InitTasks();
        }

        public Dictionary<string, object> HelloInfo() => new Dictionary<string, object>
        {
            ["revit"] = _revitVersion,
            ["addin"] = _addinVersion,
            ["niveles"] = _config.Levels,
            ["retener"] = _hold?.On ?? false,
            ["pausado"] = _pause?.On ?? false,
        };

        public async Task<Response> HandleAsync(Session session, Request request, CancellationToken ct)
        {
            if (_pause != null && _pause.On)
                return Response.Fail(request.Id, Status.Rechazado,
                    "El usuario pausó la IA desde Revit (botón Pausar IA). Deténgase y dígaselo; mientras siga en pausa no haga cambios en Revit por ningún medio. Continúe cuando la reanude.");
            var targetError = ResolveTarget(request);
            if (targetError != null) return targetError;
            // 0.8.0: permiso por clase de operación (además de los niveles R/P/W/D).
            var permission = Permissions.Of(request.Op, request.Args, string.Equals(DocTarget.Ambient.Value?.Kind, "familia", StringComparison.OrdinalIgnoreCase));
            var denied = Permissions.Deny(permission, _config.EffectivePermissions);
            if (denied != null) return Response.Fail(request.Id, Status.Rechazado, denied);
            // 0.9.0: política del perfil (producción / laboratorio), además de permisos y niveles.
            var profileDenied = _config.Profile.DenyRequest(request.Op, permission, DocTarget.Ambient.Value?.Key, BimBridge.PowerEngine.PowerEngineCapabilityRegistry.StateOfWire(request.Op, request.Args));
            if (profileDenied != null) return Response.Fail(request.Id, Status.Rechazado, profileDenied);
            return await WithJournal(session, request, () => Dispatch(session, request, ct)).ConfigureAwait(false);
        }

        private async Task<Response> Dispatch(Session session, Request request, CancellationToken ct)
        {
            switch (request.Op)
            {
                case Wire.OpContext: return await Context(session, request, ct).ConfigureAwait(false);
                case Wire.OpDocs: return await Docs(request, ct).ConfigureAwait(false);
                case Wire.OpExec:
                    return string.Equals(Arg.GetString(request.Args, "accion"), "lista", StringComparison.OrdinalIgnoreCase)
                        ? ExecList(request) : await Exec(session, request, ct).ConfigureAwait(false);
                case Wire.OpFam: return await Fam(session, request, ct).ConfigureAwait(false);
                case Wire.OpDoc: return await DocOp(session, request, ct).ConfigureAwait(false);
                case Wire.OpQuery: return await Query(session, request, ct).ConfigureAwait(false);
                case Wire.OpSets: return Sets(session, request);
                case Wire.OpCheck: return await Check(session, request, ct).ConfigureAwait(false);
                case Wire.OpPlan: return await PlanOp(session, request, ct).ConfigureAwait(false);
                case Wire.OpCommit: return await Commit(session, request, ct).ConfigureAwait(false);
                case Wire.OpJob: return await Job(session, request, ct).ConfigureAwait(false);
                case Wire.OpGeo: return await Geo(session, request, ct).ConfigureAwait(false);
                case Wire.OpCatalog: return await Catalog(request, ct).ConfigureAwait(false);
                case Wire.OpParams: return await Params(session, request, ct).ConfigureAwait(false);
                case Wire.OpShow: return await Show(session, request, ct).ConfigureAwait(false);
                case Wire.OpCapture: return await CaptureOp(session, request, ct).ConfigureAwait(false);
                case Wire.OpTask: return await TaskOp(session, request, ct).ConfigureAwait(false);
                default:
                    return Response.Fail(request.Id, Status.Invalido, "operación desconocida: " + request.Op);
            }
        }

        public void OnSessionClosed(Session session)
        {
            // Nadie va a leer esos resultados: se cancelan dentro de Revit en su siguiente tramo.
            _jobs.CancelSession(session.Id);
            session.Sets.Clear();
            _snapshots.DropSession(session.Id);
        }

        /// <summary>Estadísticas para el estado y la auditoría: hilo de Revit, caché y trabajos.</summary>
        public Dictionary<string, object> Diagnostics() => new Dictionary<string, object>
        {
            ["hilo_revit"] = _main.Stats(),
            ["cache"] = new Dictionary<string, object> { ["aciertos"] = (long)_cache.Hits, ["fallos"] = (long)_cache.Misses, ["guardados"] = (long)_cache.Count },
            ["trabajos_en_curso"] = (long)_jobs.Running,
            // #9 / #5: caché de compilación C# (single-flight) e instantáneas de consulta (#2).
            ["compilacion_csharp"] = Scripting.ScriptHost.CacheStats(),
            ["instantaneas_consulta"] = new Dictionary<string, object>
            {
                ["guardadas"] = (long)_snapshots.Count, ["celdas"] = _snapshots.Cells,
                ["bytes_estimados"] = _snapshots.EstimatedBytes, ["presupuesto_bytes"] = _snapshots.MaxBytes,   // estimación, no medición del heap
            },
        };

        // ---------------- geo (trabajos largos con espera y caché) ----------------

        private async Task<Response> Geo(Session session, Request request, CancellationToken ct)
        {
            GeoSpec spec;
            try { spec = GeoSpec.Parse(request.Args); }
            catch (Exception ex) when (ex is QueryException || ex is ArgumentException)
            {
                return Response.Fail(request.Id, Status.Invalido, ex.Message);
            }
            List<long> ids = null;
            if (spec.FromSet != null)
            {
                if (!session.Sets.TryGet(spec.FromSet, TargetId, out var setIds, out var setError))
                    return Response.Fail(request.Id, Status.Invalido, setError);
                ids = new List<long>(setIds);
            }
            if (spec.Ids.Count > 0)
            {
                ids = ids ?? new List<long>();
                ids.AddRange(spec.Ids);
            }
            var job = _jobs.Create(session.Id, "geo " + spec.Recipe);
            var runner = new GeoRunner(spec, ids, _tracker, _cache, job);
            _jobs.Run(job, () => RunGeo(job, runner, session));
            return await AwaitJob(request, job, ct).ConfigureAwait(false);
        }

        private async Task RunGeo(Job job, GeoRunner runner, Session session)
        {
            var total = Stopwatch.StartNew();
            long revitMs = 0, maxSlice = 0, slices = 0;
            var busy = TimeSpan.FromMilliseconds(_config.BusyAfterMs);
            try
            {
                var r = await RunMain(runner.Gather, highPriority: false, busyAfter: busy, ct: job.Cts.Token).ConfigureAwait(false);
                revitMs += r.BusyMs; maxSlice = Math.Max(maxSlice, r.MaxSliceMs); slices += r.Slices;
                if (!r.Ok) { FailJob(job, r); return; }
                if (runner.Error != null) { _jobs.Fail(job, Status.Invalido, runner.Error); return; }

                Dictionary<string, object> datos;
                var sets = new Dictionary<string, long[]>();
                if (runner.IndexInfo != null)
                {
                    datos = runner.IndexInfo;
                }
                else if (runner.CacheHit?.Payload is GeoRunner.Outcome cached)
                {
                    datos = GeoRunner.Summarize(runner.Spec, cached, out sets);
                    datos["reutilizado"] = "el modelo y los vínculos no cambiaron desde la revisión anterior (" + runner.CacheHit.RevitMs + " ms de Revit ahorrados)";
                }
                else
                {
                    job.Phase = "calculando fuera de Revit";
                    await Task.Run(() => runner.Compute(), job.Cts.Token).ConfigureAwait(false);
                    if (runner.NeedsExact)
                    {
                        var r2 = await RunMain(runner.ExactStep, highPriority: false, busyAfter: busy, ct: job.Cts.Token).ConfigureAwait(false);
                        revitMs += r2.BusyMs; maxSlice = Math.Max(maxSlice, r2.MaxSliceMs); slices += r2.Slices;
                        if (!r2.Ok) { FailJob(job, r2); return; }
                        if (runner.Error != null) { _jobs.Fail(job, Status.Error, runner.Error); return; }
                    }
                    runner.FinishClashes();
                    var outcome = runner.ToOutcome();
                    datos = GeoRunner.Summarize(runner.Spec, outcome, out sets);
                    if (runner.ModelChanged)
                        datos["aviso_modelo"] = "el modelo cambió mientras se revisaba: repita la revisión si esos elementos se tocaron";
                    else
                        _cache.Put(runner.CacheKey, new ResultCache.Entry { Payload = outcome, RevitMs = revitMs, CreatedUtc = DateTime.UtcNow });
                    if (runner.MeshesNew > 0 || runner.MeshesReused > 0)
                        datos["mallas"] = runner.MeshesNew + " nuevas, " + runner.MeshesReused + " reutilizadas";
                }
                foreach (var kv in sets)
                {
                    try { session.Sets.Save(kv.Key, kv.Value, TargetId, TargetTitle); } catch (Exception) { }
                }
                datos["ms"] = total.ElapsedMilliseconds;
                datos["revit_ms"] = revitMs;
                if (maxSlice > _main.BudgetMs * 2) datos["tramo_max_ms"] = maxSlice;
                job.Rev = runner.Rev;
                _jobs.Complete(job, datos);
            }
            catch (OperationCanceledException)
            {
                _jobs.Fail(job, JobStatus.Cancelado, "cancelado antes de terminar; no se cambió nada");
            }
            catch (Exception ex)
            {
                _jobs.Fail(job, Status.Error, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void FailJob(Job job, WorkResult r, bool write = false)
        {
            var resp = FromWork(0, r, write);
            var st = resp.Estado;
            // Cancelado antes de empezar = nada aplicado (se puede repetir); a mitad de una escritura = desconocido (reconciliar).
            if (r.Kind == WorkResult.KindCancelled) st = write ? (r.Slices > 0 ? Status.Desconocido : Status.Rechazado) : JobStatus.Cancelado;
            _jobs.Fail(job, st, resp.Aviso);
        }

        /// <summary>Cuánto espera una petición a que termine su trabajo antes de devolver job_id (queda margen dentro del plazo del cliente).</summary>
        private static int WaitBudget(Request r) => r.DeadlineMs > 0 ? Math.Max(500, Math.Min(r.DeadlineMs - 4000, 45000)) : 25000;

        private static async Task<Response> AwaitJob(Request request, Job job, CancellationToken ct)
        {
            if (!job.IsFinal && job.Work != null)
            {
                try { await Task.WhenAny(job.Work, Task.Delay(WaitBudget(request), ct)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            return JobResponse(request.Id, job);
        }

        private static Response JobResponse(long id, Job job)
        {
            switch (job.Status)
            {
                case JobStatus.Listo:
                    return new Response { Id = id, Estado = job.Estado, Rev = job.Rev, Datos = job.Result, Aviso = FinalizeNote(job) };
                case JobStatus.EnProceso:
                    return new Response
                    {
                        Id = id, Estado = Status.EnProceso, Datos = job.Progress(),
                        Aviso = "Sigue dentro de Revit por tramos, sin bloquearlo. Llame job con job_id=" + job.Id + " para esperar el resultado; no repita la revisión.",
                    };
                default:
                    var f = Response.Fail(id, job.Estado, job.Error + (FinalizeNote(job) != null ? " " + FinalizeNote(job) : ""));
                    f.Datos = job.Result;
                    return f;
            }
        }

        private static string FinalizeNote(Job job) =>
            job.FinalizeError == null ? null : "ATENCIÓN: el diario no registró el final de este trabajo (" + job.FinalizeError + "); si repite su clave se pedirá reconciliar.";

        // ---------------- revit_context ----------------

        private async Task<Response> Context(Session session, Request request, CancellationToken ct)
        {
            // Registro de identidad (agent.register): ctx agente="Integration client" [conversacion=… modelo=…]. Sólo afecta a esta sesión.
            var agentArg = Arg.GetString(request.Args, "agente");
            if (Arg.GetBool(request.Args, "desregistrar") == true) Ui.Runtime.Sessions.Unregister(session);
            else if (agentArg != null)
            {
                // Issue #3: un alias en uso por otra sesión activa se rechaza (no se reasigna ni se comparte).
                switch (Ui.Runtime.Sessions.TryRegister(session, agentArg, Arg.GetString(request.Args, "conversacion"), Arg.GetString(request.Args, "modelo"), out var holder))
                {
                    case RegisterResult.Invalid:
                        return Response.Fail(request.Id, Status.Invalido, "agente= necesita un nombre legible de 1 a " + AgentIdentity.MaxName + " caracteres");
                    case RegisterResult.Duplicate:
                        return Response.Fail(request.Id, Status.Conflicto, "el alias \"" + AgentIdentity.Clean(agentArg) + "\" ya lo usa otra sesión activa (" +
                            holder.ClientName + "); elija otro nombre, p. ej. añadiendo el cliente o la conversación");
                }
            }
            Dictionary<string, object> data = null;
            long rev = 0;
            var result = await RunMain((app, sw, budget) =>
            {
                var uidoc = app.ActiveUIDocument;
                var docs = DocRegistry.Instance.Refresh(app);
                var doc = DocTarget.Current != null ? DocTarget.Resolve(app) : uidoc?.Document;
                data = new Dictionary<string, object>
                {
                    ["revit"] = app.Application.VersionNumber,
                    ["build"] = app.Application.VersionBuild,
                    ["usuario"] = app.Application.Username,
                    ["instancia"] = DocRegistry.Instance.InstanceId,
                    ["addin"] = _addinVersion,
                    ["protocolo"] = (long)Wire.ProtocolVersion,
#if NET
                    ["runtime"] = ".NET " + Environment.Version,
#else
                    ["runtime"] = ".NET Framework " + Environment.Version,
#endif
                };
                if (docs.Count > 1)
                    data["documentos"] = docs.Select(i => (object)(i.Id + " " + i.Kind + " " + i.Title + (i.Active ? " (activo)" : ""))).ToList();
                if (doc == null)
                {
                    data["documento"] = null;
                    return true;
                }
                rev = _tracker.For(doc)?.Rev ?? 0;
                data["doc"] = DocRegistry.Instance.IdOf(doc);
                data["documento"] = doc.Title;
                data["tipo"] = DocRegistry.KindOf(doc);
                data["ruta"] = doc.PathName;
                // alpha3: identidad duradera para clientes tipados (AgentHost): clave y huella del registro, nunca el título.
                var ident = DocRegistry.Instance.IdentityOf(doc);
                data["clave_doc"] = ident?.Key;
                data["huella_doc"] = ident?.Print;
                data["pid"] = (long)System.Diagnostics.Process.GetCurrentProcess().Id;
                data["solo_lectura"] = doc.IsReadOnly;
                data["modificado"] = doc.IsModified;
                data["worksharing"] = doc.IsWorkshared;
                if (doc.IsWorkshared)
                {
                    try { data["central"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); }
                    catch (Exception) { }
                }
                data["vista_activa"] = uidoc.ActiveView != null
                    ? new Dictionary<string, object> { ["id"] = Ids.ToLong(uidoc.ActiveView.Id), ["nombre"] = uidoc.ActiveView.Name }
                    : null;
                data["niveles_mcp"] = _config.Levels;
                data["permite"] = (_config.Allows('R') ? "leer" : "") + (_config.Allows('P') ? ", planificar" : "") + (_config.Allows('W') ? ", cambiar" : "") +
                                  (_config.AllowsDocOps ? ", guardar/sincronizar/pdf" : "") +
                                  (_config.AllowsExec(true) ? ", exec ejecutar" : _config.AllowsExec(false) ? ", exec consulta" : "");
                data["runner"] = ScriptHostState();
                data["csharp_dinamico"] = Ui.Runtime.BlockDynamicCode ? "bloqueado por el usuario" : "permitido";
                // Respuesta compacta: la tabla de capacidades (27 filas) sólo con ctx detalle=true; casi nunca cambia entre llamadas.
                if (Arg.GetBool(request.Args, "detalle") == true)
                {
                    int year;
                    int.TryParse(app.Application.VersionNumber, out year);
                    data["capacidades_cols"] = new List<object> { "orden", "limite", "nivel" };
                    data["capacidades"] = Capabilities.ForDoc(DocRegistry.KindOf(doc), year);
                }
                else data["capacidades"] = "ctx detalle=true";
                var notFor = Capabilities.NotFor(DocRegistry.KindOf(doc));
                if (notFor.Count > 0) data["no_aplica_a_este_documento"] = notFor.Cast<object>().ToList();
                data["retener"] = _hold.On;
                var held = _plans.Held(DocumentTracker.KeyOf(doc)).Count;
                if (held > 0) data["cola_retenida"] = (long)held;
                return true;
            }, highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            if (!result.Ok) return FromWork(request.Id, result);
            Scripting.ScriptHost.WarmInBackground(Ui.Runtime.Log);
            data["agente"] = session.AgentName;
            data["identidad"] = session.AgentName == null
                ? "insuficiente" + (session.IdentityNote != null ? " (" + session.IdentityNote + ")" : "") + ": registre un nombre con ctx agente=\"…\"; hasta entonces no se permiten lecturas ni cambios"
                : "declarada (" + session.IdentitySource + ")";
            if (session.AgentId != null) data["agent_id"] = session.AgentId;
            data["session_id"] = session.Id;
            return new Response { Id = request.Id, Rev = rev, Datos = data };
        }

        private string ScriptHostState()
        {
            if (_config.Runner == "apagado") return "apagado (config)";
            if (!Scripting.ScriptHost.Present) return "falta el compilador (carpeta roslyn)";
            return "C# (Roslyn)" + (_config.Runner == "consulta" ? ", sólo consulta" : "");
        }

        // ---------------- query ----------------

        private async Task<Response> Query(Session session, Request request, CancellationToken ct)
        {
            var cursor = Arg.GetString(request.Args, "cursor");
            if (!string.IsNullOrEmpty(cursor)) return QueryFromCursor(session, request, cursor);

            QuerySpec q;
            try { q = QuerySpec.Parse(request.Args); }
            catch (Exception ex) when (ex is QueryException || ex is ArgumentException)
            {
                return Response.Fail(request.Id, Status.Invalido, ex.Message);
            }

            List<long> explicitIds = null;
            if (q.FromSet != null)
            {
                if (!session.Sets.TryGet(q.FromSet, TargetId, out var setIds, out var setError))
                    return Response.Fail(request.Id, Status.Invalido, setError);
                explicitIds = new List<long>(setIds);
            }
            if (q.Ids.Count > 0)
            {
                explicitIds = explicitIds ?? new List<long>();
                explicitIds.AddRange(q.Ids);
            }

            HashSet<long> sinceIds = null;
            var runner = new QueryRunner(q, explicitIds, null);
            var sw = Stopwatch.StartNew();
            long apiTicks = 0;
            var result = await RunMain((app, watch, budget) =>
            {
                var begin = Stopwatch.GetTimestamp();
                try
                {
                    if (q.Since.HasValue && sinceIds == null)
                    {
                        var reg = _tracker.For(app.ActiveUIDocument?.Document);
                        sinceIds = reg != null ? reg.ChangedSince(q.Since.Value, out _) : new HashSet<long>();
                        runner = new QueryRunner(q, explicitIds, sinceIds);
                    }
                    return runner.Step(app, watch, budget, _tracker);
                }
                finally { apiTicks += Stopwatch.GetTimestamp() - begin; }
            }, highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            if (!result.Ok) return FromWork(request.Id, result);
            if (runner.Error != null) return Response.Fail(request.Id, Status.Invalido, runner.Error);

            var dispatchMs = sw.Elapsed.TotalMilliseconds;
            // Filtrar, agrupar, paginar y formatear: fuera del hilo de Revit.
            var filtered = ResultTable.Filter(q, runner.Rows, out var matched);
            var datos = (Dictionary<string, object>)ResultTable.Render(q, filtered, q.Page, q.PageSize);
            if (runner.FastCount.HasValue) datos["total"] = runner.FastCount.Value;
            // Issue #2: si hay más de una página, las siguientes se sirven desde esta lectura (cursor), sin volver al hilo de Revit.
            // La lectura puede ocupar varias visitas al hilo de Revit: sólo hay cursor si la revisión no cambió durante ella.
            if (ResultTable.IsPagedListing(q) && datos.ContainsKey("pages") && runner.Registry != null && runner.Registry.Rev == runner.Rev)
            {
                var reg = runner.Registry;
                var id = _snapshots.Put(session.Id, runner.DocId, runner.Rev, q, filtered, () => reg.Rev, out var refusal);
                if (id != null) { datos["cursor"] = id; datos["rev_instantanea"] = runner.Rev; }
                else if (refusal != null) datos["cursor_no_disponible"] = refusal;   // nunca se omite en silencio
            }
            if (q.SaveAs != null)
            {
                try
                {
                    session.Sets.Save(q.SaveAs, matched, TargetId, TargetTitle);
                    datos["guardado"] = q.SaveAs;
                }
                catch (Exception ex) { datos["aviso_conjunto"] = ex.Message; }
            }
            if (Arg.GetBool(request.Args, "timing") == true)
            {
                datos["tiempos_consulta"] = new Dictionary<string, object>
                {
                    ["despacho_total_ms"] = dispatchMs,
                    ["hilo_revit_ms"] = apiTicks * 1000.0 / Stopwatch.Frequency,
                    ["procesamiento_resultado_ms"] = sw.Elapsed.TotalMilliseconds - dispatchMs,
                };
            }
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = runner.Rev, Datos = datos };
        }

        /// <summary>
        /// Issue #2: página N de una instantánea. No toca el hilo de Revit. Se rechaza si el cursor es de otra sesión, venció,
        /// apunta a otro documento o el documento cambió de revisión (nunca se mezclan filas de dos revisiones).
        /// </summary>
        private Response QueryFromCursor(Session session, Request request, string cursor)
        {
            var sw = Stopwatch.StartNew();
            var page = Arg.GetLong(request.Args, "page") ?? 1;
            if (page < 1) return Response.Fail(request.Id, Status.Invalido, "page empieza en 1");
            switch (_snapshots.Get(cursor, session.Id, TargetId, out var snap))
            {
                case SnapshotLookup.Unknown:
                    return Response.Fail(request.Id, Status.Vencido, "cursor desconocido o vencido (duran " + (int)_snapshots.Ttl.TotalMinutes +
                        " min sin uso y sólo valen en la sesión que los creó); repita la consulta sin cursor");
                case SnapshotLookup.Evicted:
                    return Response.Fail(request.Id, Status.Vencido, "el cursor se descartó (" + (_snapshots.EvictionReason(cursor, session.Id) ?? "presupuesto o vencimiento") +
                        "); repita la consulta sin cursor");
                case SnapshotLookup.OtherDocument:
                    return Response.Fail(request.Id, Status.Invalido, "el cursor es de otro documento; repita la consulta en el documento destino");
                case SnapshotLookup.Stale:
                    return Response.Fail(request.Id, Status.Conflicto, "el documento cambió desde la primera página; repita la consulta (las páginas no se mezclan entre revisiones)");
            }
            var pageSize = snap.Spec.PageSize;
            var datos = (Dictionary<string, object>)ResultTable.Render(snap.Spec, snap.Rows, (int)page, pageSize);
            var pages = Math.Max(1, (snap.Rows.Count + pageSize - 1) / pageSize);
            if (page > pages) return Response.Fail(request.Id, Status.Invalido, "page " + page + " fuera de rango (hay " + pages + ")");
            datos["page"] = page;
            datos["pages"] = (long)pages;
            datos["cursor"] = snap.Id;
            datos["rev_instantanea"] = snap.Rev;
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = snap.Rev, Datos = datos };
        }

        // ---------------- check ----------------

        private async Task<Response> Check(Session session, Request request, CancellationToken ct)
        {
            var name = (Arg.GetString(request.Args, "name") ?? "").ToLowerInvariant();
            if (name == "copy_monitor") return await CopyMonitorCheck(session, request, ct).ConfigureAwait(false);
            if (name == "cobertura") return CoverageCheck(request);
            if (name == "historial") return HistoryCheck(session, request);
            if (name == "model_health") return await ModelHealthCheck(request, ct).ConfigureAwait(false);
            if (name != "lighting_audit")
                return Response.Fail(request.Id, Status.Invalido, "check disponible en esta versión: lighting_audit, copy_monitor");

            var tagRule = Arg.GetString(request.Args, "tags") ?? "uno";
            if (tagRule != "uno" && tagRule != "ninguno" && tagRule != "ignorar")
                return Response.Fail(request.Id, Status.Invalido, "tags debe ser uno (un tag por luminaria), ninguno (sin tags) o ignorar");
            var runner = new LightingAuditRunner(Arg.GetStringList(request.Args, "lvl"), Arg.GetString(request.Args, "views"),
                Arg.GetBool(request.Args, "exacto") ?? false);
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => runner.Step(app, watch, budget, _tracker),
                highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (runner.Error != null) return Response.Fail(request.Id, Status.Invalido, runner.Error);

            var datos = LightingAudit.Summarize(runner.Fixtures, runner.Tags, runner.Views, out var sets, runner.AllFixtureIds, runner.Levels, tagRule);
            if (sets.Count > 0)
            {
                foreach (var kv in sets) session.Sets.Save(kv.Key, kv.Value, TargetId, TargetTitle);
                datos["conjuntos_guardados"] = new List<object>(sets.Keys);
            }
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = runner.Rev, Datos = datos };
        }

        // ---------------- plan / commit / job (fase 3) ----------------

        private async Task<Response> PlanOp(Session session, Request request, CancellationToken ct)
        {
            if (!_config.Allows('P'))
                return Response.Fail(request.Id, Status.Rechazado,
                    "Este conector no tiene habilitado planificar cambios en este Revit (niveles " + _config.Levels + "; lo decide el usuario en la configuración del MCP). " +
                    "Dígaselo y proponga los cambios en texto. No es una prohibición general: si el usuario autorizó otra herramienta para esta tarea, puede usarla.");

            List<OpSpec> ops;
            var resolved = new List<List<long>>();
            SourceBinding.Bound sourceBound;
            try
            {
                ops = OpSpec.ParseAll(request.Args);
                // AUD-19: si las operaciones vienen de una fuente (origen=), el plan queda vinculado a su clave y huella de contenido.
                sourceBound = SourceBinding.Resolve(request.Args["ops"] as List<object>);
                foreach (var op in ops)
                {
                    var ids = new List<long>(op.Ids);
                    if (op.Set != null)
                    {
                        if (!session.Sets.TryGet(op.Set, TargetId, out var setIds, out var setError))
                            return Response.Fail(request.Id, Status.Invalido, setError);
                        ids.AddRange(setIds);
                    }
                    resolved.Add(ids);
                }
            }
            catch (Exception ex) when (ex is QueryException || ex is ArgumentException)
            {
                return Response.Fail(request.Id, Status.Invalido, ex.Message);
            }

            var plan = new Plan
            {
                Id = _plans.NewId(),
                SessionId = session.Id,
                Client = session.ClientName,
                CreatedUtc = DateTime.UtcNow,
                Summary = Arg.GetString(request.Args, "summary"),
                Task = Arg.GetString(request.Args, Wire.TaskArg),
                BoundToken = sourceBound?.Token, BoundKey = sourceBound?.Key, BoundHash = sourceBound?.Hash,
            };

            // Sin geometría: se planifica en una sola visita al hilo de Revit.
            if (!ops.Any(NeedsGeo)) return await BuildPlan(request, ops, resolved, plan, new BuildContext(), ct).ConfigureAwait(false);

            // Con geometría (snap_to_surface, place con montaje): se mide por tramos como geo y, si tarda, se devuelve job_id.
            var job = _jobs.Create(session.Id, "plan");
            _jobs.Run(job, async () =>
            {
                try
                {
                    var ctx = new BuildContext();
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < ops.Count; i++)
                    {
                        if (!NeedsGeo(ops[i])) continue;
                        job.Phase = "midiendo la geometría de la op " + ops[i].Index + " (" + ops[i].Kind + ")";
                        var err = ops[i].Kind == "snap_to_surface"
                            ? await MeasureSnap(ops[i], resolved[i], ctx, job).ConfigureAwait(false)
                            : await MeasurePlace(ops[i], ctx, job).ConfigureAwait(false);
                        if (err != null) ctx.GeoErrors[ops[i].Index] = err;
                    }
                    job.Phase = "planificando";
                    var resp = await BuildPlan(request, ops, resolved, plan, ctx, job.Cts.Token).ConfigureAwait(false);
                    if (resp.Estado == Status.Ok)
                    {
                        var d = resp.Datos as Dictionary<string, object> ?? new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(resp.Aviso)) d["aviso"] = resp.Aviso;
                        d["medicion_ms"] = sw.ElapsedMilliseconds;
                        job.Rev = resp.Rev ?? 0;
                        _jobs.Complete(job, d);
                    }
                    else _jobs.Fail(job, resp.Estado, resp.Aviso);
                }
                catch (OperationCanceledException) { _jobs.Fail(job, JobStatus.Cancelado, "cancelado antes de terminar; no se cambió nada"); }
                catch (Exception ex) { _jobs.Fail(job, Status.Error, ex.GetType().Name + ": " + ex.Message); }
            });
            return await AwaitJob(request, job, ct).ConfigureAwait(false);
        }

        /// <summary>Operaciones de plan que valen dentro de un RFA.</summary>
        private static readonly string[] FamilyPlanOps = { "set_param", "set_values", "move", "rotate", "mirror", "copy", "delete" };

        private static bool NeedsGeo(OpSpec op) =>
            op.Kind == "snap_to_surface" || (op.Kind == "place" && Arg.GetString(op.Args, "montaje") != null);

        /// <summary>Argumentos de geo montaje a partir de los de la operación (lado, contra, puntos, max, vinculos).</summary>
        private static Dictionary<string, object> MountArgs(OpSpec op, bool up)
        {
            var a = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["name"] = "montaje", ["lado"] = up ? "arriba" : "abajo" };
            foreach (var k in new[] { "contra", "puntos", "max", "vinculos", "sub_excluir" })
                if (op.Args.TryGetValue(k, out var v) && v != null) a[k] = v;
            return a;
        }

        /// <summary>Corre una medición de montaje (tramos en Revit + cálculo fuera). Devuelve las filas o un error en <paramref name="error"/>.</summary>
        private async Task<Tuple<List<Dictionary<string, object>>, string>> RunMount(GeoRunner runner, Job job, bool cache)
        {
            var busy = TimeSpan.FromMilliseconds(_config.BusyAfterMs);
            var r = await RunMain(runner.Gather, highPriority: false, busyAfter: busy, ct: job.Cts.Token).ConfigureAwait(false);
            if (!r.Ok) return Tuple.Create<List<Dictionary<string, object>>, string>(null, FromWork(0, r).Aviso);
            if (runner.Error != null) return Tuple.Create<List<Dictionary<string, object>>, string>(null, runner.Error);
            if (runner.CacheHit?.Payload is GeoRunner.Outcome cached) return Tuple.Create(cached.Rows, (string)null);
            await Task.Run(() => runner.Compute(), job.Cts.Token).ConfigureAwait(false);
            if (cache && !runner.ModelChanged && runner.CacheKey != null)
                _cache.Put(runner.CacheKey, new ResultCache.Entry { Payload = runner.ToOutcome(), RevitMs = r.BusyMs, CreatedUtc = DateTime.UtcNow });
            return Tuple.Create(runner.Rows, (string)null);
        }

        private async Task<string> MeasureSnap(OpSpec op, List<long> ids, BuildContext ctx, Job job)
        {
            if (ids.Count == 0) return null;
            var up = !string.Equals(Arg.GetString(op.Args, "lado"), "abajo", StringComparison.OrdinalIgnoreCase);
            var a = MountArgs(op, up);
            a["ids"] = string.Join(",", ids.Distinct());
            var runner = new GeoRunner(GeoSpec.Parse(a), ids.Distinct().ToList(), _tracker, _cache, job);
            var res = await RunMount(runner, job, cache: true).ConfigureAwait(false);
            if (res.Item2 != null) return res.Item2;
            var rows = new Dictionary<long, Dictionary<string, object>>();
            foreach (var row in res.Item1 ?? new List<Dictionary<string, object>>())
                if (row.TryGetValue("id", out var id) && id is long l) rows[l] = row;
            ctx.Geo[op.Index] = rows;
            return null;
        }

        /// <summary>place con montaje: el cuerpo de una instancia existente del tipo, llevado a cada punto, se mide contra lo de arriba (o abajo).</summary>
        private async Task<string> MeasurePlace(OpSpec op, BuildContext ctx, Job job)
        {
            var up = !string.Equals(Arg.GetString(op.Args, "montaje"), "piso", StringComparison.OrdinalIgnoreCase);
            var points = OpCatalog.Points(op.Args["puntos"], "place puntos", 1, 5000);
            long templateId = 0; string levelName = null; double levelElev = 0; string error = null;
            var r = await RunMain((app, sw, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "no hay un modelo abierto"; return true; }
                try
                {
                    var model = new Writes.ModelIndex(doc);
                    var sym = model.FindType(Arg.GetString(op.Args, "type"), "place", t => t is FamilySymbol, "familias cargadas");
                    var lvl = model.FindLevel(Arg.GetString(op.Args, "level"), "place");
                    levelName = lvl.Name; levelElev = lvl.ProjectElevation;
                    var sample = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                        .Cast<FamilyInstance>().Where(f => f.Symbol?.Id == sym.Id && f.Location is LocationPoint)
                        .OrderBy(f => f.GroupId == ElementId.InvalidElementId ? 0 : 1).FirstOrDefault();
                    if (sample == null) error = "place montaje: no hay ninguna instancia de " + sym.FamilyName + ": " + sym.Name + " en el modelo para medir su cuerpo; dé elev";
                    else templateId = Ids.ToLong(sample.Id);
                }
                catch (QueryException ex) { error = ex.Message; }
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: job.Cts.Token).ConfigureAwait(false);
            if (!r.Ok) return FromWork(0, r).Aviso;
            if (error != null) return error;

            // Desde muy cerca del nivel (losa) o desde 3' (piso): la primera superficie que encuentra la vertical es la que manda.
            var nominal = up ? 0.5 : 3.0;
            var a = MountArgs(op, up);
            a["ids"] = templateId.ToString(CultureInfo.InvariantCulture);
            var runner = new GeoRunner(GeoSpec.Parse(a), null, _tracker, null, job)
            {
                Virtual = new GeoRunner.VirtualPlacement
                {
                    TemplateId = templateId, Points = points, Level = levelName, LevelElev = levelElev, Nominal = nominal,
                    RotDeg = OpCatalog.Number(op.Args, "rot", "place") ?? 0,
                },
            };
            var res = await RunMount(runner, job, cache: false).ConfigureAwait(false);
            if (res.Item2 != null) return res.Item2;
            var rows = new Dictionary<long, Dictionary<string, object>>();
            foreach (var row in res.Item1 ?? new List<Dictionary<string, object>>())
                if (row.TryGetValue("id", out var id) && id is long l && l < 0) rows[-l - 1] = row;
            ctx.Geo[op.Index] = rows;
            ctx.GeoNominal[op.Index] = nominal;
            return null;
        }

        /// <summary>Planificar: una sola visita al hilo de Revit con un índice del modelo (tipos, niveles, vistas) compartido por todas las ops.</summary>
        private async Task<Response> BuildPlan(Request request, List<OpSpec> ops, List<List<long>> resolved, Plan plan, BuildContext ctx, CancellationToken ct)
        {
            string error = null;
            long rev = 0;
            var result = await RunMain((app, sw, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "no hay un modelo abierto"; return true; }
                if (doc.IsFamilyDocument)
                {
                    // En familias sólo las operaciones que no dependen de vistas, niveles, grupos ni láminas (F02/R004).
                    var bad = ops.Where(o => !FamilyPlanOps.Contains(o.Kind)).Select(o => o.Kind).Distinct().ToList();
                    if (bad.Count > 0)
                    {
                        error = Status.NoSoportado + ": en un documento de familia plan admite " + string.Join(", ", FamilyPlanOps) + "; no " + string.Join(", ", bad) +
                                ". Para conectores y parámetros use fam conector / fam param; para lo demás, exec.";
                        return true;
                    }
                }
                plan.DocKey = DocumentTracker.KeyOf(doc);
                plan.DocId = DocRegistry.Instance.IdOf(doc);
                plan.DocTitle = doc.Title;
                rev = _tracker.For(doc)?.Rev ?? 0;
                _plans.LoadPersisted(plan.DocKey);
                ctx.Doc = doc;
                ctx.Plan = plan;
                ctx.Model = new Writes.ModelIndex(doc);
                ctx.HeldAfter = (id, key) => _plans.TryHeldAfter(plan.DocKey, id, key, out var after, out var pid) ? Tuple.Create(after, pid) : null;
                try
                {
                    for (var i = 0; i < ops.Count; i++) PlanBuilder.Build(ctx, ops[i], resolved[i]);
                }
                catch (QueryException ex) { error = ex.Message; }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex) { error = ex.Message; }
                return true;
            }, highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            if (!result.Ok) return FromWork(request.Id, result);
            if (error != null) return Response.Fail(request.Id, Status.Invalido, error);

            if (string.IsNullOrWhiteSpace(plan.Summary))
                plan.Summary = string.Join("; ", plan.Entries.GroupBy(e => OpCatalog.NameOf(e.Op) + (e.Op == "set_param" ? " " + e.Key : ""))
                    .Select(g => g.Key + " ×" + g.Count()));
            _plans.ExpireOld(TimeSpan.FromMinutes(_config.PlanExpiryMinutes));
            _plans.Add(plan);

            var datos = plan.Describe();
            if (plan.Entries.Count == 0)
            {
                plan.Status = PlanStatus.Descartado;
                plan.StatusDetail = "no hay nada que cambiar";
                datos["estado_plan"] = plan.Status;
                return new Response { Id = request.Id, Rev = rev, Datos = datos, Aviso = "No hay nada que cambiar; revise los avisos." };
            }
            var holdNow = WillHold(plan.DocKey);
            datos["siguiente"] = "commit plan_id=" + plan.Id + " (si el usuario ya autorizó la tarea, aplíquelo sin pedir confirmación; si pidió revisar, muéstrele el diff antes)" +
                                 (holdNow ? ". Quedará en la cola de Revit hasta que el usuario pulse Aplicar cola." : ".");
            return new Response { Id = request.Id, Rev = rev, Datos = datos };
        }

        /// <summary>Lo creado queda en $creados (todo) y $creados_n (por operación) para usarlo en la siguiente llamada sin repetir ids.</summary>
        public static List<string> SaveCreated(Session session, Plan plan)
        {
            var saved = new List<string>();
            if (session == null || plan.Status != PlanStatus.Aplicado || plan.Created.Count == 0) return saved;
            try { session.Sets.Save("$creados", plan.Created, plan.DocId, plan.DocTitle); saved.Add("$creados"); } catch (Exception) { }
            foreach (var kv in plan.CreatedByOp.OrderBy(k => k.Key))
            {
                if (kv.Value.Count == 0 || plan.CreatedByOp.Count == 1) continue;
                try { session.Sets.Save("$creados_" + kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value, plan.DocId, plan.DocTitle); saved.Add("$creados_" + kv.Key); } catch (Exception) { break; }
            }
            return saved;
        }

        private bool WillHold(string docKey) => !_config.Allows('W') || _hold.On || _plans.Held(docKey).Count > 0;

        private async Task<Response> Commit(Session session, Request request, CancellationToken ct)
        {
            // Revisión consolidada: commit ops=… sin plan_id = plan + commit en UN viaje (mismo motor, mismo diff, mismas verificaciones).
            // Un plan con geometría que devuelve job_id no se fusiona: se devuelve el plan y el agente hace commit después.
            Dictionary<string, object> fusedPlan = null;
            if (string.IsNullOrEmpty(Arg.GetString(request.Args, "plan_id")) && request.Args.ContainsKey("ops"))
            {
                // clave= promete "aplicado una sola vez"; si el plan acabara sin commit (geometría en job), el diario la daría por cumplida.
                if (!string.IsNullOrWhiteSpace(Arg.GetString(request.Args, Wire.KeyArg)))
                    return Response.Fail(request.Id, Status.Invalido, "commit ops= no admite clave=: use plan y después commit plan_id=… clave=… para la idempotencia");
                var planArgs = new Dictionary<string, object> { ["ops"] = request.Args["ops"] };
                foreach (var k in new[] { "summary", Wire.TaskArg, Wire.DocArg }) if (request.Args.TryGetValue(k, out var v)) planArgs[k] = v;
                var pr = await PlanOp(session, new Request { Id = request.Id, Op = Wire.OpPlan, Args = planArgs, DeadlineMs = request.DeadlineMs }, ct).ConfigureAwait(false);
                var pid = pr.Datos is Dictionary<string, object> pd0 ? Arg.GetString(pd0, "plan_id") : null;
                if (pr.Estado != Status.Ok || pid == null || (pr.Datos as Dictionary<string, object>)?.ContainsKey("job_id") == true
                    || _plans.Get(pid)?.Status != PlanStatus.Listo)
                {
                    // Siempre se dice que NO se aplicó nada, también cuando el plan sigue como job (geometría) o falló.
                    pr.Aviso = (pr.Aviso == null ? "" : pr.Aviso + " ") + "commit NO ejecutado en este viaje: no se cambió el modelo." +
                               (pid != null ? " Cuando el plan esté listo, haga commit plan_id=" + pid + "." : "");
                    if (pr.Datos is Dictionary<string, object> pdx) pdx["commit_ejecutado"] = false;
                    return pr;
                }
                request.Args["plan_id"] = pid;
                var pdesc = (Dictionary<string, object>)pr.Datos;
                fusedPlan = new Dictionary<string, object> { ["plan_id"] = pid, ["elementos"] = pdesc.TryGetValue("elementos", out var pe) ? pe : null, ["cambios"] = pdesc.TryGetValue("cambios", out var pc) ? pc : null };
                if (pdesc.TryGetValue("avisos", out var av)) fusedPlan["avisos"] = av;
            }
            var committed = await CommitPlan(session, request, ct).ConfigureAwait(false);
            if (fusedPlan != null && committed.Datos is Dictionary<string, object> cd) cd["plan_en_el_mismo_viaje"] = fusedPlan;
            return committed;
        }

        private async Task<Response> CommitPlan(Session session, Request request, CancellationToken ct)
        {
            var plan = _plans.Get(Arg.GetString(request.Args, "plan_id"));
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            if (plan == null || (plan.SessionId != session.Id && (string.IsNullOrEmpty(task) || plan.Task != task)))
                return Response.Fail(request.Id, Status.Invalido, "no existe ese plan_id en esta conversación (ni en la tarea indicada); llame plan primero");
            _plans.ExpireOld(TimeSpan.FromMinutes(_config.PlanExpiryMinutes));
            if (plan.Status != PlanStatus.Listo)
                return new Response { Id = request.Id, Estado = MapStatus(plan.Status), Datos = plan.Describe(5), Aviso = StatusAdvice(plan) };

            // alpha3: modo=ensayo aplica con el mismo motor, verifica y revierte el grupo. Nunca se retiene ni cuenta como aplicado.
            var trialRaw = (Arg.GetString(request.Args, "modo") ?? "").Trim().ToLowerInvariant();
            if (trialRaw.Length > 0 && trialRaw != "ensayo" && trialRaw != "aplicar")
                return Response.Fail(request.Id, Status.Invalido, "commit modo debe ser aplicar (por defecto) o ensayo");
            var trial = trialRaw == "ensayo";

            // Cola: el usuario la aplica cuando quiera desde la cinta. Si ya hay cola, también se retiene para respetar el orden.
            if (!trial && WillHold(plan.DocKey))
            {
                _plans.Hold(plan);
                Ui.Runtime.MarkDirty();
                var pos = _plans.Held(plan.DocKey).FindIndex(p => p.Id == plan.Id) + 1;
                var d = plan.Describe(0);
                d["posicion_en_cola"] = (long)pos;
                return new Response
                {
                    Id = request.Id, Estado = Status.Retenido, Datos = d,
                    Aviso = "Quedó en la cola de Revit (posición " + pos + "). El usuario lo aplicará con 'Aplicar cola' en la cinta BIMBRIDGE. " +
                            "No lo repita; consulte job con plan_id=" + plan.Id + " más tarde. Puede seguir planificando: los planes nuevos se encadenan sobre la cola.",
                };
            }

            // 0.8.0 (pendiente H2 de rc1): un commit persistente deja intención y resultado en la auditoría encadenada.
            // Sin auditoría no se escribe (fail-closed, igual que exec): una escritura sin registro no se puede reconciliar.
            string auditIntent = null;
            if (!trial)
            {
                if (Ui.Runtime.Audit == null)
                {
                    plan.Status = PlanStatus.Listo;
                    return Response.Fail(request.Id, Status.Rechazado, "auditoría no disponible: el commit persistente se rechaza sin cambiar nada");
                }
                try
                {
                    auditIntent = Ui.Runtime.Audit.Intent(session.Id, "commit", plan.DocKey ?? "", new Dictionary<string, object>
                    {
                        ["plan_id"] = plan.Id, ["clave"] = Arg.GetString(request.Args, "clave") ?? "", ["documento"] = plan.DocTitle ?? "",
                    });
                }
                catch (Exception aex) when (aex is IOException || aex is UnauthorizedAccessException || aex is InvalidOperationException)
                {
                    plan.Status = PlanStatus.Listo;
                    return Response.Fail(request.Id, Status.Rechazado, "no se pudo registrar la intención en la auditoría (" + aex.Message + "); no se escribió nada");
                }
            }

            string error = null;
            PlanOps.ApplyResult r = null;
            long rev = 0;
            bool applyEntered = false;
            bool wasModified = false, lostModified = false;   // P0: un commit real que se revierte por fallo también puede borrar la marca
            DocWriteBlock blocked = null;
            long queueMs = 0, revitMs = 0;
            plan.Status = PlanStatus.EnProceso;
            var sw = Stopwatch.StartNew();
            // El plan se aplica sobre SU documento (handle fijado al planificar), aunque el usuario haya cambiado de pestaña.
            var planTarget = new DocTarget { Id = plan.DocId, Key = plan.DocKey, Title = plan.DocTitle, Explicit = true };
            DocTarget.Ambient.Value = plan.DocId != null ? planTarget : DocTarget.Ambient.Value;
            var result = await RunMain((app, watch, budget) =>
            {
                Document doc;
                if (plan.DocId != null) doc = planTarget.Get(app, out error);
                else
                {
                    doc = app.ActiveUIDocument?.Document;
                    if (doc == null || !string.Equals(DocumentTracker.KeyOf(doc), plan.DocKey, StringComparison.OrdinalIgnoreCase)) doc = null;
                    if (doc == null) error = "el modelo activo ya no es " + plan.DocTitle + "; pida al usuario que lo active o vuelva a planificar";
                }
                if (doc == null) return true;
                // P0 (Codex): comprobado en el hilo de Revit justo antes de abrir el grupo; vale para cualquier cliente MCP.
                if (trial) { error = Engine.TrialGuard.Refuse(doc.IsModified, "commit modo=ensayo"); if (error != null) return true; }
                queueMs = sw.ElapsedMilliseconds;
                wasModified = doc.IsModified;
                // En el hilo de Revit: los commits de este proceso van en serie, y la intención queda en disco antes de abrir el grupo.
                try { _plans.LoadPersisted(plan.DocKey); _plans.BeginWrite(plan); }
                catch (DocWriteBlockedException ex) { blocked = ex.Block; return true; }
                catch (PlanStoreException ex) { error = ex.Message + "; no se escribió nada"; return true; }
                _tracker.CurrentWriter = session.Id;
                applyEntered = true;
                try { r = trial ? PlanOps.Trial(doc, plan, _config.SubBatchSize, session.Id) : PlanOps.Apply(doc, plan, _config.SubBatchSize, session.Id); }
                finally { _tracker.CurrentWriter = null; }
                revitMs = sw.ElapsedMilliseconds - queueMs;
                // También aquí: el siguiente commit de la cola de Revit no debe encontrar una intención ya resuelta.
                _plans.Finish(plan, r.Status, r.Detail);
                rev = _tracker.For(doc)?.Rev ?? 0;
                lostModified = wasModified && !doc.IsModified;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                if (applyEntered)
                {
                    // Ya entró a escribir: repetirlo podría duplicar. Queda terminal, con evidencia durable, y no vuelve a Listo.
                    var detail = (trial ? "El ensayo empezó" : "El commit empezó") + " y no entregó una confirmación de resultado (" + result.Kind + "). Reconcilie el documento antes de repetir.";
                    Engine.GroupAbort.PreserveInterrupted(plan, detail);
                    _plans.Finish(plan, Status.Desconocido, detail);
                    AuditOutcome(session, auditIntent, Status.Desconocido, rev);
                    return Response.Fail(request.Id, Status.Desconocido, detail);
                }
                plan.Status = PlanStatus.Listo;
                AuditOutcome(session, auditIntent, result.Kind == "busy" ? "no_ejecutado_ocupado" : "incierto_" + result.Kind, 0);
                return FromWork(request.Id, result);
            }
            if (blocked != null)
            {
                AuditOutcome(session, auditIntent, "rechazado", 0);
                plan.Status = PlanStatus.Listo;
                return BlockedResponse(request.Id, blocked);
            }
            if (error != null)
            {
                AuditOutcome(session, auditIntent, "rechazado", 0);
                plan.Status = PlanStatus.Listo;
                var fr = Response.Fail(request.Id, error.StartsWith(Status.DestinoNoDisponible, StringComparison.Ordinal) ? Status.DestinoNoDisponible : Status.Rechazado, error);
                if (error.StartsWith(Engine.TrialGuard.Code, StringComparison.Ordinal)) fr.Datos = new Dictionary<string, object> { ["codigo"] = Engine.TrialGuard.Code, ["sin_cambios"] = true };
                return fr;
            }
            if (trial)
            {
                // Un ensayo incierto queda terminal y conserva su estado para job.
                // Ensayo correcto: el plan vuelve a listo sólo si la intención salió del disco.
                var ok = r.Status == PlanStatus.Ensayado && _plans.Finish(plan, r.Status, r.Detail);
                if (ok) { plan.Status = PlanStatus.Listo; plan.FinishedUtc = null; }
                else if (r.Status != PlanStatus.Ensayado) _plans.Finish(plan, r.Status, r.Detail);
                var td = plan.Describe(0);
                td["ensayo"] = r.Trial ?? new Dictionary<string, object>();
                td["detalle"] = r.Detail;
                td["ms"] = sw.ElapsedMilliseconds;
                td["espera_cola_ms"] = queueMs;
                td["revit_ms"] = revitMs;
                return new Response
                {
                    Id = request.Id, Estado = ok ? PlanStatus.Ensayado : (r.Status == PlanStatus.Conflicto ? Status.Conflicto : r.Status == PlanStatus.Revertido ? Status.Revertido : Status.Desconocido),
                    Rev = rev, Datos = td,
                    Aviso = ok ? "Ensayo revertido: el modelo quedó como estaba. Esto NO es un commit; para aplicar de verdad, commit sin modo."
                               : r.Detail,
                };
            }
            _plans.Finish(plan, r.Status, r.Detail);
            AuditOutcome(session, auditIntent, plan.Status, rev);
            var datos = plan.Describe(0);
            var saved = SaveCreated(session, plan);
            if (saved.Count > 0) datos["conjuntos_guardados"] = saved.Cast<object>().ToList();
            datos["ms"] = sw.ElapsedMilliseconds;
            datos["espera_cola_ms"] = queueMs;
            datos["revit_ms"] = revitMs;
            var advice = StatusAdvice(plan);
            if (lostModified)
            {
                // No se manipula IsModified: se informa. El documento tenía cambios sin guardar y, tras revertir, Revit lo marca como no modificado.
                datos["marca_cambios_sin_guardar_perdida"] = true;
                advice = "ATENCIÓN: el documento tenía cambios sin guardar y, al revertir, Revit dejó de marcarlo como modificado; esos cambios siguen sin guardar. " + advice;
            }
            var estado = MapStatus(plan.Status);
            if (plan.Status == PlanStatus.Aplicado)
            {
                // AUD-09: commit confirmado ≠ postcondiciones cumplidas.
                var post = PlanVerification.Postconditions(plan.Verification);
                datos["commit"] = "confirmado";
                datos["postcondiciones"] = post;
                if (post != PlanVerification.Met)
                {
                    estado = Status.ConDiferencias;
                    advice = "Se aplicó (commit confirmado), pero la relectura no coincide con lo pedido o no se pudo verificar: revise 'verificacion'. " + advice;
                }
            }
            return new Response { Id = request.Id, Estado = estado, Rev = rev, Datos = datos, Aviso = advice };
        }

        /// <summary>Cierra la intención de auditoría. Si falla, la intención queda pendiente y PendingIntents la reporta para reconciliar.</summary>
        private static void AuditOutcome(Session session, string intent, string estado, long rev)
        {
            if (intent == null) return;
            try { Ui.Runtime.Audit?.Outcome(session.Id, "commit", intent, estado, new Dictionary<string, object> { ["rev"] = rev }); }
            catch (Exception) { /* queda como intención pendiente: visible en PendingIntents */ }
        }

        private static Response BlockedResponse(long id, DocWriteBlock block)
        {
            var fail = Response.Fail(id, Status.Rechazado, block.Message);
            fail.Datos = block.ToJson();
            return fail;
        }

        /// <summary>Barrera común de escrituras: vuelve a leer el disco y, ante cualquier duda, no deja pasar.</summary>
        private Response WriteBarrier(long id, IEnumerable<string> docs)
        {
            foreach (var d in docs)
            {
                try { _plans.LoadPersisted(d); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* CheckWritable decide */ }
                var block = _plans.CheckWritable(d);
                if (block.Blocked) return BlockedResponse(id, block);
            }
            return null;
        }

        /// <summary>
        /// job op=pendientes: bloqueo del documento. job op=reconciliar plan_id=…: compara la evidencia con el modelo y libera sólo si queda
        /// demostrado que todo se revirtió o se aplicó. Lee el modelo, no lo cambia, y sirve desde cualquier conversación porque el plan pudo
        /// quedar de una sesión anterior a un reinicio.
        /// </summary>
        private async Task<Response> ReconcileJob(Session session, Request request, string jop, CancellationToken ct)
        {
            var key = DocTarget.Ambient.Value?.Key;
            if (key == null) return Response.Fail(request.Id, Status.DestinoNoDisponible, "indique el documento con doc= o actívelo en Revit");
            try { _plans.LoadPersisted(key); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* CheckWritable decide */ }
            if (jop == "pendientes") return new Response { Id = request.Id, Datos = _plans.CheckWritable(key).ToJson() };
            if (!_config.Allows('W')) return Response.Fail(request.Id, Status.Rechazado, "reconciliar exige permiso de escritura (W)");
            var plan = _plans.Get(Arg.GetString(request.Args, "plan_id"));
            if (plan == null || plan.Status == PlanStatus.Reconciliado || !string.Equals(plan.DocKey, key, StringComparison.OrdinalIgnoreCase))
                return Response.Fail(request.Id, Status.Invalido, "no hay un plan pendiente con ese plan_id en este documento (job op=pendientes)");
            ReconcileCheck check = null;
            string error = null;
            var result = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "no hay un modelo abierto"; return true; }
                var reader = new Writes.PlanApplier(doc, plan, 1, "reconciliar");
                check = ReconcileEvaluator.Evaluate(plan, reader.Current, id => doc.GetElement(Ids.ToId(id)) != null);
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (error != null) return Response.Fail(request.Id, Status.DestinoNoDisponible, error);
            bool released;
            try { released = _plans.Reconcile(plan, check, session.Id); }
            catch (PlanStoreException ex) { return Response.Fail(request.Id, Status.Rechazado, ex.Message); }
            try { Ui.Runtime.Audit?.Outcome(session.Id, "reconciliar", plan.Id, check.Verdict, check.ToJson()); }
            catch (Exception ex) { check.Reasons.Add("auditoría: " + ex.Message); }
            var datos = check.ToJson();
            datos["plan_id"] = plan.Id;
            datos["liberado"] = released;
            datos["bloqueo"] = _plans.CheckWritable(key).ToJson();
            return new Response
            {
                Id = request.Id, Estado = released ? Status.Ok : Status.Desconocido, Datos = datos,
                Aviso = released ? "Reconciliado y registrado. Este plan no se repite; planifique de nuevo si hace falta." :
                    "No se pudo demostrar el resultado: el documento sigue bloqueado. Revise el modelo; si hace falta, una persona debe resolverlo con AgentHost plan-store con Revit cerrado.",
            };
        }

        private async Task<Response> Job(Session session, Request request, CancellationToken ct)
        {
            var jop = (Arg.GetString(request.Args, "op") ?? "status").Trim().ToLowerInvariant();
            if (jop == "pendientes" || jop == "reconciliar") return await ReconcileJob(session, request, jop, ct).ConfigureAwait(false);
            var jobId = Arg.GetString(request.Args, "job_id");
            if (!string.IsNullOrEmpty(jobId))
            {
                var job = _jobs.Get(jobId);
                if (!JobStore.Visible(job, session.Id, Arg.GetString(request.Args, Wire.TaskArg)))
                    return Response.Fail(request.Id, Status.Invalido, "no existe ese job_id en esta conversación ni en la tarea indicada (tarea=…)");
                if (jop == "cancel" || jop == "drop")
                {
                    if (_jobs.Cancel(job) && job.Work != null)
                    {
                        try { await Task.WhenAny(job.Work, Task.Delay(3000, ct)).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                    return JobResponse(request.Id, job);
                }
                if (jop != "status" && jop != "wait")
                    return Response.Fail(request.Id, Status.Invalido, "op de job con job_id: status/wait (por defecto, espera el resultado) o cancel");
                return await AwaitJob(request, job, ct).ConfigureAwait(false);
            }

            var id = Arg.GetString(request.Args, "plan_id");
            if (!string.IsNullOrEmpty(id))
            {
                var plan = _plans.Get(id);
                var ptask = Arg.GetString(request.Args, Wire.TaskArg);
                if (plan == null || (plan.SessionId != session.Id && (string.IsNullOrEmpty(ptask) || plan.Task != ptask)))
                    return Response.Fail(request.Id, Status.Invalido, "no existe ese plan_id en esta conversación ni en la tarea indicada");
                if (jop == "drop")
                {
                    if (plan.Status == PlanStatus.Retenido)
                        return Response.Fail(request.Id, Status.Rechazado,
                            "Ese plan ya está en la cola del usuario; sólo él puede descartarlo desde Cola en la cinta BIMBRIDGE. Dígaselo.");
                    if (plan.Status != PlanStatus.Listo)
                        return new Response { Id = request.Id, Estado = MapStatus(plan.Status), Datos = plan.Describe(0), Aviso = "No hay nada que descartar: " + StatusAdvice(plan) };
                    _plans.Finish(plan, PlanStatus.Descartado, "descartado por la IA antes de aplicarlo");
                    return new Response { Id = request.Id, Datos = new Dictionary<string, object> { ["plan_id"] = plan.Id, ["descartado"] = true } };
                }
                if (jop != "status")
                    return Response.Fail(request.Id, Status.Invalido, "op de job: status (por defecto) o drop");
                return new Response { Id = request.Id, Estado = MapStatus(plan.Status), Datos = plan.Describe(10), Aviso = StatusAdvice(plan) };
            }
            var rows = _plans.ForSession(session.Id).Select(p => (object)new object[]
            {
                p.Id, p.Status, (long)p.Entries.Count, p.Summary ?? "", p.StatusDetail ?? "",
            }).ToList();
            foreach (var j in _jobs.ForSession(session.Id, Arg.GetString(request.Args, Wire.TaskArg)))
            {
                var prog = j.IsFinal ? (j.Error ?? "") : (j.Total > 0 ? j.Done + "/" + j.Total + " " : "") + (j.Phase ?? "");
                rows.Add(new object[] { j.Id, j.Status, null, j.Kind, prog });
            }
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["retener"] = _hold.On,
                    ["cols"] = new List<object> { "id", "estado", "cambios", "resumen", "detalle" },
                    ["rows"] = rows,
                },
            };
        }

        private static string MapStatus(string planStatus)
        {
            switch (planStatus)
            {
                case PlanStatus.Aplicado: return Status.Ok;
                case PlanStatus.Retenido: return Status.Retenido;
                case PlanStatus.Conflicto: return Status.Conflicto;
                case PlanStatus.Revertido: return Status.Revertido;
                case Status.Desconocido: return Status.Desconocido;
                case PlanStatus.Vencido: return Status.Vencido;
                case PlanStatus.EnProceso: return Status.EnProceso;
                case PlanStatus.Descartado: return Status.Rechazado;
                default: return Status.Ok;
            }
        }

        private static string StatusAdvice(Plan p)
        {
            switch (p.Status)
            {
                case PlanStatus.Aplicado: return null;
                case PlanStatus.Listo: return "Plan listo; llame commit para aplicarlo.";
                case PlanStatus.Retenido: return "Sigue en la cola de Revit esperando que el usuario pulse Aplicar cola. No lo repita.";
                case PlanStatus.Conflicto: return "Algo cambió desde que se planificó y no se aplicó nada: " + p.StatusDetail;
                case PlanStatus.Revertido: return "Revit lo rechazó y se revirtió entero: " + p.StatusDetail;
                case PlanStatus.Vencido: return "El plan venció sin commit; vuelva a planificar.";
                case PlanStatus.Descartado: return "Descartado: " + p.StatusDetail + ". No lo repita sin preguntar al usuario.";
                default: return p.StatusDetail;
            }
        }

        // ---------------- sets ----------------

        private static Response Sets(Session session, Request request)
        {
            var op = (Arg.GetString(request.Args, "op") ?? "list").ToLowerInvariant();
            try
            {
                switch (op)
                {
                    case "list":
                        return new Response
                        {
                            Id = request.Id,
                            Datos = new Dictionary<string, object>
                            {
                                ["cols"] = new List<object> { "conjunto", "n", "doc" },
                                ["rows"] = session.Sets.Describe().Cast<object>().ToList(),
                            },
                        };
                    case "drop":
                        var name = Arg.GetString(request.Args, "name");
                        var removed = session.Sets.Remove(name);
                        return new Response { Id = request.Id, Datos = new Dictionary<string, object> { ["borrado"] = removed } };
                    default:
                        return Response.Fail(request.Id, Status.Invalido, "op debe ser list o drop");
                }
            }
            catch (ArgumentException ex)
            {
                return Response.Fail(request.Id, Status.Invalido, ex.Message);
            }
        }

        private Response FromWork(long id, WorkResult r, bool write = false)
        {
            if (r.Kind == WorkResult.KindCancelled && r.Slices > 0 && write)
                return Response.Fail(id, Status.Desconocido,
                    "Se canceló cuando ya había empezado a escribir (" + r.Slices + " tramos en Revit): puede haber quedado aplicado en parte. Lea el modelo y reconcilie antes de repetir.");
            switch (r.Kind)
            {
                case "busy":
                    return Response.Fail(id, Status.RevitOcupado,
                        "Revit no atendió en " + _config.BusyAfterMs / 1000 + " s: probablemente hay un diálogo abierto, un comando en curso o un modo de edición. Pida al usuario que lo cierre y reintente.");
                case "cancelled":
                    return Response.Fail(id, Status.TiempoAgotado,
                        r.Slices > 0
                            ? "Se agotó el tiempo y la lectura se canceló a mitad (no escribe nada). Acote la petición (menos niveles, vistas o ids) antes de repetirla."
                            : "Se agotó el tiempo y la operación se canceló antes de empezar en Revit, sin cambiar nada. Acote la petición antes de repetirla.");
                default:
                    var ex = r.Error;
                    return Response.Fail(id, Status.Error, ex == null ? "error desconocido" : ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
