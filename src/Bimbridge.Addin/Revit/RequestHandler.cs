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
using Bimbridge.Addin.Revit.Geo;
using Bimbridge.Addin.Revit.Writes;
using Bimbridge.Engine;
using Bimbridge.Engine.Geo;
using Bimbridge.Engine.Qc;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// Request router: reads (phase 1-2) and typed writes plan/commit/job (phase 3). Runs on pipe threads; everything that touches
    /// the API goes through MainThread. Never throws: errors come back as clear statuses.
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
        /// <summary>Issue #2: snapshots of paged queries (one model read, pages from memory).</summary>
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
            // Save As changes the durable key: the journal and the tasks follow the new document.
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
            [Wire.WriteReceiptFeature] = true,
        };

        public async Task<Response> HandleAsync(Session session, Request request, CancellationToken ct)
        {
            if (_pause != null && _pause.On)
                return Response.Fail(request.Id, Status.Rejected,
                    "The user paused the AI from Revit (Pause AI button). Stop and tell them; while it stays paused do not make changes in Revit by any means. Continue when they resume it.");
            var targetError = ResolveTarget(session, request);
            if (targetError != null) return targetError;
            // 0.8.0: permission per operation class (in addition to the R/P/W/D levels).
            var permission = Permissions.Of(request.Op, request.Args, string.Equals(DocTarget.Ambient.Value?.Kind, "familia", StringComparison.OrdinalIgnoreCase));
            var denied = Permissions.Deny(permission, _config.EffectivePermissions);
            if (denied != null) return Response.Fail(request.Id, Status.Rejected, denied);
            // 0.9.0: profile policy (production / lab), in addition to permissions and levels.
            var profileDenied = _config.Profile.DenyRequest(request.Op, permission, DocTarget.Ambient.Value?.Key, BimBridge.PowerEngine.PowerEngineCapabilityRegistry.StateOfWire(request.Op, request.Args));
            if (profileDenied != null) return Response.Fail(request.Id, Status.Rejected, profileDenied);
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
                    return Response.Fail(request.Id, Status.Invalid, "unknown operation: " + request.Op);
            }
        }

        public void OnSessionClosed(Session session)
        {
            // Nobody will read those results: they are cancelled inside Revit on their next slice.
            _jobs.CancelSession(session.Id);
            session.Sets.Clear();
            _snapshots.DropSession(session.Id);
        }

        /// <summary>Statistics for the status and the audit: Revit thread, cache and jobs.</summary>
        public Dictionary<string, object> Diagnostics() => new Dictionary<string, object>
        {
            ["hilo_revit"] = _main.Stats(),
            ["cache"] = new Dictionary<string, object> { ["aciertos"] = (long)_cache.Hits, ["fallos"] = (long)_cache.Misses, ["guardados"] = (long)_cache.Count },
            ["trabajos_en_curso"] = (long)_jobs.Running,
            // #9 / #5: C# compilation cache (single-flight) and query snapshots (#2).
            ["compilacion_csharp"] = Scripting.ScriptHost.CacheStats(),
            ["instantaneas_consulta"] = new Dictionary<string, object>
            {
                ["guardadas"] = (long)_snapshots.Count, ["celdas"] = _snapshots.Cells,
                ["bytes_estimados"] = _snapshots.EstimatedBytes, ["presupuesto_bytes"] = _snapshots.MaxBytes,   // estimate, not a heap measurement
            },
        };

        // ---------------- geo (long jobs with wait and cache) ----------------

        private async Task<Response> Geo(Session session, Request request, CancellationToken ct)
        {
            GeoSpec spec;
            try { spec = GeoSpec.Parse(request.Args); }
            catch (Exception ex) when (ex is QueryException || ex is ArgumentException)
            {
                return Response.Fail(request.Id, Status.Invalid, ex.Message);
            }
            List<long> ids = null;
            if (spec.FromSet != null)
            {
                if (!session.Sets.TryGet(spec.FromSet, TargetId, out var setIds, out var setError))
                    return Response.Fail(request.Id, Status.Invalid, setError);
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
                if (runner.Error != null) { _jobs.Fail(job, Status.Invalid, runner.Error); return; }

                Dictionary<string, object> data;
                var sets = new Dictionary<string, long[]>();
                if (runner.IndexInfo != null)
                {
                    data = runner.IndexInfo;
                }
                else if (runner.CacheHit?.Payload is GeoRunner.Outcome cached)
                {
                    data = GeoRunner.Summarize(runner.Spec, cached, out sets);
                    data["reutilizado"] = "the model and links did not change since the previous check (" + runner.CacheHit.RevitMs + " ms of Revit saved)";
                }
                else
                {
                    job.Phase = "computing outside Revit";
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
                    data = GeoRunner.Summarize(runner.Spec, outcome, out sets);
                    if (runner.ModelChanged)
                        data["aviso_modelo"] = "the model changed during the check: repeat the check if those elements were touched";
                    else
                        _cache.Put(runner.CacheKey, new ResultCache.Entry { Payload = outcome, RevitMs = revitMs, CreatedUtc = DateTime.UtcNow });
                    if (runner.MeshesNew > 0 || runner.MeshesReused > 0)
                        data["mallas"] = runner.MeshesNew + " new, " + runner.MeshesReused + " reused";
                }
                foreach (var kv in sets)
                {
                    try { session.Sets.Save(kv.Key, kv.Value, TargetId, TargetTitle); } catch (Exception) { }
                }
                data["ms"] = total.ElapsedMilliseconds;
                data["revit_ms"] = revitMs;
                if (maxSlice > _main.BudgetMs * 2) data["tramo_max_ms"] = maxSlice;
                job.Rev = runner.Rev;
                _jobs.Complete(job, data);
            }
            catch (OperationCanceledException)
            {
                _jobs.Fail(job, JobStatus.Cancelled, "cancelled before finishing; nothing was changed");
            }
            catch (Exception ex)
            {
                _jobs.Fail(job, Status.Error, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void FailJob(Job job, WorkResult r, bool write = false)
        {
            var resp = FromWork(0, r, write);
            var st = resp.State;
            // Cancelled before starting = nothing applied (can be repeated); midway through a write = unknown (reconcile).
            if (r.Kind == WorkResult.KindCancelled) st = write ? (r.Slices > 0 ? Status.Unknown : Status.Rejected) : JobStatus.Cancelled;
            _jobs.Fail(job, st, resp.Warning);
        }

        /// <summary>How long a request waits for its job to finish before returning job_id (leaves margin within the client deadline).</summary>
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
                case JobStatus.Done:
                    return new Response { Id = id, State = job.State, Rev = job.Rev, Data = job.Result, Warning = FinalizeNote(job) };
                case JobStatus.InProgress:
                    return new Response
                    {
                        Id = id, State = Status.InProgress, Data = job.Progress(),
                        Warning = "Still running inside Revit in slices, without blocking it. Call job with job_id=" + job.Id + " to wait for the result; do not repeat the check.",
                    };
                default:
                    var f = Response.Fail(id, job.State, job.Error + (FinalizeNote(job) != null ? " " + FinalizeNote(job) : ""));
                    f.Data = job.Result;
                    return f;
            }
        }

        private static string FinalizeNote(Job job) =>
            job.FinalizeError == null ? null : "WARNING: the journal did not record the end of this job (" + job.FinalizeError + "); if you repeat its key you will be asked to reconcile.";

        // ---------------- revit_context ----------------

        private async Task<Response> Context(Session session, Request request, CancellationToken ct)
        {
            // Identity registration (agent.register): ctx agente="Integration client" [conversacion=… modelo=…]. Affects only this session.
            var agentArg = Arg.GetString(request.Args, "agente");
            if (Arg.GetBool(request.Args, "desregistrar") == true) Ui.Runtime.Sessions.Unregister(session);
            else if (agentArg != null)
            {
                // Issue #3: an alias in use by another active session is rejected (not reassigned or shared).
                switch (Ui.Runtime.Sessions.TryRegister(session, agentArg, Arg.GetString(request.Args, "conversacion"), Arg.GetString(request.Args, "modelo"), out var holder))
                {
                    case RegisterResult.Invalid:
                        return Response.Fail(request.Id, Status.Invalid, "agente= needs a readable name of 1 to " + AgentIdentity.MaxName + " characters");
                    case RegisterResult.Duplicate:
                        return Response.Fail(request.Id, Status.Conflict, "the alias \"" + AgentIdentity.Clean(agentArg) + "\" is already used by another active session (" +
                            holder.ClientName + "); choose another name, e.g. by adding the client or the conversation");
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
                    data["documentos"] = docs.Select(i => (object)(i.Id + " " + i.Kind + " " + i.Title + (i.Active ? " (active)" : ""))).ToList();
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
                // alpha3: durable identity for typed clients (AgentHost): registry key and fingerprint, never the title.
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
                data["permite"] = (_config.Allows('R') ? "read" : "") + (_config.Allows('P') ? ", plan" : "") + (_config.Allows('W') ? ", change" : "") +
                                  (_config.AllowsDocOps ? ", save/sync/pdf" : "") +
                                  (_config.AllowsExec(true) ? ", exec run" : _config.AllowsExec(false) ? ", exec query" : "");
                data["runner"] = ScriptHostState();
                data["csharp_dinamico"] = Ui.Runtime.BlockDynamicCode ? "blocked by the user" : "allowed";
                // Compact response: the capability table (27 rows) only with ctx detalle=true; it almost never changes between calls.
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
                ? "insufficient" + (session.IdentityNote != null ? " (" + session.IdentityNote + ")" : "") + ": register a name with ctx agente=\"…\"; until then no reads or changes are allowed"
                : "declared (" + session.IdentitySource + ")";
            if (session.AgentId != null) data["agent_id"] = session.AgentId;
            data["session_id"] = session.Id;
            return new Response { Id = request.Id, Rev = rev, Data = data };
        }

        private string ScriptHostState()
        {
            if (_config.Runner == "apagado") return "off (config)";
            if (!Scripting.ScriptHost.Present) return "compiler missing (roslyn folder)";
            return "C# (Roslyn)" + (_config.Runner == "consulta" ? ", query only" : "");
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
                return Response.Fail(request.Id, Status.Invalid, ex.Message);
            }

            List<long> explicitIds = null;
            if (q.FromSet != null)
            {
                if (!session.Sets.TryGet(q.FromSet, TargetId, out var setIds, out var setError))
                    return Response.Fail(request.Id, Status.Invalid, setError);
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
                        var reg = _tracker.For(DocTarget.Resolve(app));
                        sinceIds = reg != null ? reg.ChangedSince(q.Since.Value, out _) : new HashSet<long>();
                        runner = new QueryRunner(q, explicitIds, sinceIds);
                    }
                    return runner.Step(app, watch, budget, _tracker);
                }
                finally { apiTicks += Stopwatch.GetTimestamp() - begin; }
            }, highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            if (!result.Ok) return FromWork(request.Id, result);
            if (runner.Error != null) return Response.Fail(request.Id, runner.RevisionChanged ? Status.Conflict : Status.Invalid, runner.Error);

            var dispatchMs = sw.Elapsed.TotalMilliseconds;
            // Filter, group, paginate and format: outside the Revit thread.
            List<Dictionary<string, object>> filtered = null;
            List<long> matched = null;
            Dictionary<string, object> data;
            if (runner.Groups != null) data = runner.Groups.Render();
            else
            {
                filtered = ResultTable.Filter(q, runner.Rows, out matched);
                data = (Dictionary<string, object>)ResultTable.Render(q, filtered, q.Page, q.PageSize);
            }
            if (runner.FastCount.HasValue) data["total"] = runner.FastCount.Value;
            // Issue #2: if there is more than one page, the following ones are served from this read (cursor), without going back to the Revit thread.
            // The read can take several visits to the Revit thread: there is a cursor only if the revision did not change during it.
            if (ResultTable.IsPagedListing(q) && data.ContainsKey("pages") && runner.Registry != null && runner.Registry.Rev == runner.Rev)
            {
                var reg = runner.Registry;
                var id = _snapshots.Put(session.Id, runner.DocId, runner.Rev, q, filtered, () => reg.Rev, out var refusal);
                if (id != null) { data["cursor"] = id; data["rev_instantanea"] = runner.Rev; }
                else if (refusal != null) data["cursor_no_disponible"] = refusal;   // never silently omitted
            }
            if (q.SaveAs != null)
            {
                try
                {
                    session.Sets.Save(q.SaveAs, matched, TargetId, TargetTitle);
                    data["guardado"] = q.SaveAs;
                }
                catch (Exception ex) { data["aviso_conjunto"] = ex.Message; }
            }
            if (Arg.GetBool(request.Args, "timing") == true)
            {
                data["tiempos_consulta"] = new Dictionary<string, object>
                {
                    ["despacho_total_ms"] = dispatchMs,
                    ["hilo_revit_ms"] = apiTicks * 1000.0 / Stopwatch.Frequency,
                    ["procesamiento_resultado_ms"] = sw.Elapsed.TotalMilliseconds - dispatchMs,
                };
            }
            data["ms"] = sw.ElapsedMilliseconds;
            // 0.9.1: benchmark metrics only when requested (they do not bloat every response).
            if (Arg.GetBool(request.Args, "metricas") == true || Arg.GetBool(request.Args, "timing") == true)
                data["metricas"] = WorkflowMetrics.FromMainThread(sw.ElapsedMilliseconds, result.WaitMs, result.BusyMs, result.Slices, result.MaxSliceMs)
                    .Observe("elements_read", runner.ElementsRead).Observe("elements_written", 0).Observe("transactions", 0)
                    .Observe("cache_hits", runner.Names.Hits).Observe("cache_misses", runner.Names.Misses).ToJson();
            return new Response { Id = request.Id, Rev = runner.Rev, Data = data };
        }

        /// <summary>
        /// Issue #2: page N of a snapshot. Does not touch the Revit thread. Rejected if the cursor belongs to another session, expired,
        /// points to another document or the document changed revision (rows from two revisions are never mixed).
        /// </summary>
        private Response QueryFromCursor(Session session, Request request, string cursor)
        {
            var sw = Stopwatch.StartNew();
            var page = Arg.GetLong(request.Args, "page") ?? 1;
            if (page < 1) return Response.Fail(request.Id, Status.Invalid, "page starts at 1");
            switch (_snapshots.Get(cursor, session.Id, TargetId, out var snap))
            {
                case SnapshotLookup.Unknown:
                    return Response.Fail(request.Id, Status.Expired, "unknown or expired cursor (they last " + (int)_snapshots.Ttl.TotalMinutes +
                        " min without use and are only valid in the session that created them); repeat the query without cursor");
                case SnapshotLookup.Evicted:
                    return Response.Fail(request.Id, Status.Expired, "the cursor was discarded (" + (_snapshots.EvictionReason(cursor, session.Id) ?? "budget or expiry") +
                        "); repeat the query without cursor");
                case SnapshotLookup.OtherDocument:
                    return Response.Fail(request.Id, Status.Invalid, "the cursor belongs to another document; repeat the query in the target document");
                case SnapshotLookup.Stale:
                    return Response.Fail(request.Id, Status.Conflict, "the document changed since the first page; repeat the query (pages are not mixed across revisions)");
            }
            var pageSize = snap.Spec.PageSize;
            var data = (Dictionary<string, object>)ResultTable.Render(snap.Spec, snap.Rows, (int)page, pageSize);
            var pages = Math.Max(1, (snap.Rows.Count + pageSize - 1) / pageSize);
            if (page > pages) return Response.Fail(request.Id, Status.Invalid, "page " + page + " out of range (there are " + pages + ")");
            data["page"] = page;
            data["pages"] = (long)pages;
            data["cursor"] = snap.Id;
            data["rev_instantanea"] = snap.Rev;
            data["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = snap.Rev, Data = data };
        }

        // ---------------- check ----------------

        private async Task<Response> Check(Session session, Request request, CancellationToken ct)
        {
            var name = (Arg.GetString(request.Args, "name") ?? "").ToLowerInvariant();
            if (name == "copy_monitor") return await CopyMonitorCheck(session, request, ct).ConfigureAwait(false);
            if (name == "cobertura") return CoverageCheck(request);
            if (name == "historial") return HistoryCheck(session, request);
            if (name == "tabla_audit") return await ScheduleCheck(request, ct).ConfigureAwait(false);
            if (name == "vista_audit") return await VisibilityCheck(request, ct).ConfigureAwait(false);
            if (name == "llamadas_audit") return await CalloutCheck(session, request, ct).ConfigureAwait(false);
            if (name == "terminales_audit") return await TerminalCheck(request, ct).ConfigureAwait(false);
            if (name == "equipos_audit") return await TerminalCheck(request, ct, equipment: true).ConfigureAwait(false);
            if (name == "ruta_audit") return await RouteAuditCheck(request, ct).ConfigureAwait(false);
            if (name == "grupos_audit") return await GroupAuditCheck(request, ct).ConfigureAwait(false);
            if (name == "anotaciones_audit") return await AnnotationAuditCheck(request, ct).ConfigureAwait(false);
            if (name == "riser_geometria") return await RiserDrawingCheck(request, ct).ConfigureAwait(false);
            if (name == "model_health") return await ModelHealthCheck(request, ct).ConfigureAwait(false);
            if (name != "lighting_audit")
                return Response.Fail(request.Id, Status.Invalid, "available checks: lighting_audit, tabla_audit, vista_audit, llamadas_audit, terminales_audit, equipos_audit, ruta_audit, grupos_audit, anotaciones_audit, riser_geometria, copy_monitor, cobertura, historial, model_health");

            var tagRule = Arg.GetString(request.Args, "tags") ?? "uno";
            if (tagRule != "uno" && tagRule != "ninguno" && tagRule != "ignorar")
                return Response.Fail(request.Id, Status.Invalid, "tags must be uno (one tag per fixture), ninguno (no tags) or ignorar");
            List<ManifestQc.Expected> inventory = null;
            ManifestQc.Options inventoryOptions = null;
            if (request.Args.TryGetValue("manifiesto", out var rawManifest))
            {
                if (!(rawManifest is Dictionary<string, object> manifest))
                    return Response.Fail(request.Id, Status.Invalid, "manifiesto requires a JSON object with elements in mm");
                try { inventory = ManifestQc.Parse(manifest, out inventoryOptions); }
                catch (ArgumentException ex) { return Response.Fail(request.Id, Status.Invalid, ex.Message); }
            }
            var runner = new LightingAuditRunner(Arg.GetStringList(request.Args, "lvl"), Arg.GetString(request.Args, "views"),
                Arg.GetBool(request.Args, "exacto") ?? false, readInventory: inventory != null);
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => runner.Step(app, watch, budget, _tracker),
                highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (runner.Error != null) return Response.Fail(request.Id, runner.Stale ? Status.Conflict : Status.Invalid, runner.Error);

            var data = LightingAudit.Summarize(runner.Fixtures, runner.Tags, runner.Views, out var sets, runner.AllFixtureIds, runner.Levels, tagRule);
            var state = Status.Ok;
            if (inventory != null)
            {
                var comparison = LightingAudit.CompareInventory(inventory, inventoryOptions, runner.Inventory, runner.Fixtures, runner.Tags, runner.Views);
                data["inventario"] = comparison;
                if (Equals(comparison["aprobado"], false) || (data.TryGetValue("cumple_regla", out var passed) && Equals(passed, false)))
                    state = Status.CompletedWithDifferences;
            }
            if (sets.Count > 0)
            {
                foreach (var kv in sets) session.Sets.Save(kv.Key, kv.Value, TargetId, TargetTitle);
                data["conjuntos_guardados"] = new List<object>(sets.Keys);
            }
            data["ms"] = sw.ElapsedMilliseconds;
            // 0.9.1: benchmark metrics only when requested (they do not bloat every response).
            if (Arg.GetBool(request.Args, "metricas") == true || Arg.GetBool(request.Args, "timing") == true)
                data["metricas"] = WorkflowMetrics.FromMainThread(sw.ElapsedMilliseconds, result.WaitMs, result.BusyMs, result.Slices, result.MaxSliceMs)
                    .Observe("elements_read", runner.ElementsRead).Observe("elements_written", 0).Observe("transactions", 0)
                    .Observe("cache_hits", runner.Names.Hits).Observe("cache_misses", runner.Names.Misses).ToJson();
            return new Response { Id = request.Id, Rev = runner.Rev, Data = data, State = state };
        }

        // ---------------- plan / commit / job (phase 3) ----------------

        private async Task<Response> PlanOp(Session session, Request request, CancellationToken ct)
        {
            if (!_config.Allows('P'))
                return Response.Fail(request.Id, Status.Rejected,
                    "This connector is not enabled to plan changes in this Revit (levels " + _config.Levels + "; the user decides this in the MCP configuration). " +
                    "Tell them and propose the changes as text. This is not a general prohibition: if the user authorized another tool for this task, you may use it.");

            List<OpSpec> ops;
            var resolved = new List<List<long>>();
            SourceBinding.Bound sourceBound;
            try
            {
                ops = OpSpec.ParseAll(request.Args);
                // AUD-19: if the operations come from a source (origen=), the plan is bound to its key and content fingerprint.
                sourceBound = SourceBinding.Resolve(request.Args["ops"] as List<object>);
                foreach (var op in ops)
                {
                    var ids = new List<long>(op.Ids);
                    if (op.Set != null)
                    {
                        if (!session.Sets.TryGet(op.Set, TargetId, out var setIds, out var setError))
                            return Response.Fail(request.Id, Status.Invalid, setError);
                        ids.AddRange(setIds);
                    }
                    resolved.Add(ids);
                }
            }
            catch (Exception ex) when (ex is QueryException || ex is ArgumentException)
            {
                return Response.Fail(request.Id, Status.Invalid, ex.Message);
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

            // Without geometry: planned in a single visit to the Revit thread.
            if (!ops.Any(NeedsGeo)) return await BuildPlan(request, ops, resolved, plan, new BuildContext(), ct).ConfigureAwait(false);

            // With geometry (snap_to_surface, place with montaje): measured in slices like geo and, if it takes long, job_id is returned.
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
                        job.Phase = "measuring the geometry of op " + ops[i].Index + " (" + ops[i].Kind + ")";
                        var err = ops[i].Kind == "snap_to_surface"
                            ? await MeasureSnap(ops[i], resolved[i], ctx, job).ConfigureAwait(false)
                            : await MeasurePlace(ops[i], ctx, job).ConfigureAwait(false);
                        if (err != null) ctx.GeoErrors[ops[i].Index] = err;
                    }
                    job.Phase = "planning";
                    var resp = await BuildPlan(request, ops, resolved, plan, ctx, job.Cts.Token).ConfigureAwait(false);
                    if (resp.State == Status.Ok)
                    {
                        var d = resp.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(resp.Warning)) d["aviso"] = resp.Warning;
                        d["medicion_ms"] = sw.ElapsedMilliseconds;
                        job.Rev = resp.Rev ?? 0;
                        _jobs.Complete(job, d);
                    }
                    else _jobs.Fail(job, resp.State, resp.Warning);
                }
                catch (OperationCanceledException) { _jobs.Fail(job, JobStatus.Cancelled, "cancelled before finishing; nothing was changed"); }
                catch (Exception ex) { _jobs.Fail(job, Status.Error, ex.GetType().Name + ": " + ex.Message); }
            });
            return await AwaitJob(request, job, ct).ConfigureAwait(false);
        }

        /// <summary>Plan operations that are valid inside an RFA.</summary>
        private static readonly string[] FamilyPlanOps = { "set_param", "set_values", "move", "rotate", "mirror", "copy", "delete" };

        private static bool NeedsGeo(OpSpec op) =>
            op.Kind == "snap_to_surface" || (op.Kind == "place" && Arg.GetString(op.Args, "montaje") != null);

        /// <summary>geo montaje arguments derived from the operation's (lado, contra, puntos, max, vinculos).</summary>
        private static Dictionary<string, object> MountArgs(OpSpec op, bool up)
        {
            var a = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["name"] = "montaje", ["lado"] = up ? "arriba" : "abajo" };
            foreach (var k in new[] { "contra", "puntos", "max", "vinculos", "sub_excluir" })
                if (op.Args.TryGetValue(k, out var v) && v != null) a[k] = v;
            return a;
        }

        /// <summary>Runs a mounting measurement (slices in Revit + computation outside). Returns the rows or an error in <paramref name="error"/>.</summary>
        private async Task<Tuple<List<Dictionary<string, object>>, string>> RunMount(GeoRunner runner, Job job, bool cache)
        {
            var busy = TimeSpan.FromMilliseconds(_config.BusyAfterMs);
            var r = await RunMain(runner.Gather, highPriority: false, busyAfter: busy, ct: job.Cts.Token).ConfigureAwait(false);
            if (!r.Ok) return Tuple.Create<List<Dictionary<string, object>>, string>(null, FromWork(0, r).Warning);
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

        /// <summary>place with montaje: the body of an existing instance of the type, moved to each point, is measured against what is above (or below).</summary>
        private async Task<string> MeasurePlace(OpSpec op, BuildContext ctx, Job job)
        {
            var up = !string.Equals(Arg.GetString(op.Args, "montaje"), "piso", StringComparison.OrdinalIgnoreCase);
            var points = OpCatalog.Points(op.Args["puntos"], "place puntos", 1, 5000);
            long templateId = 0; string levelName = null; double levelElev = 0; string error = null;
            var r = await RunMain((app, sw, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "there is no open model"; return true; }
                try
                {
                    var model = new Writes.ModelIndex(doc);
                    var sym = model.FindType(Arg.GetString(op.Args, "type"), "place", t => t is FamilySymbol, "loaded families");
                    var lvl = model.FindLevel(Arg.GetString(op.Args, "level"), "place");
                    levelName = lvl.Name; levelElev = lvl.ProjectElevation;
                    var sample = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                        .Cast<FamilyInstance>().Where(f => f.Symbol?.Id == sym.Id && f.Location is LocationPoint)
                        .OrderBy(f => f.GroupId == ElementId.InvalidElementId ? 0 : 1).FirstOrDefault();
                    if (sample == null) error = "place montaje: there is no instance of " + sym.FamilyName + ": " + sym.Name + " in the model to measure its body; give elev";
                    else templateId = Ids.ToLong(sample.Id);
                }
                catch (QueryException ex) { error = ex.Message; }
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: job.Cts.Token).ConfigureAwait(false);
            if (!r.Ok) return FromWork(0, r).Warning;
            if (error != null) return error;

            // From very close to the level (slab) or from 3' (floor): the first surface the vertical hits is the one that counts.
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

        /// <summary>Plan: a single visit to the Revit thread with a model index (types, levels, views) shared by all ops.</summary>
        private async Task<Response> BuildPlan(Request request, List<OpSpec> ops, List<List<long>> resolved, Plan plan, BuildContext ctx, CancellationToken ct)
        {
            string error = null;
            long rev = 0;
            var result = await RunMain((app, sw, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "there is no open model"; return true; }
                if (doc.IsFamilyDocument)
                {
                    // In families only the operations that do not depend on views, levels, groups or sheets (F02/R004).
                    var bad = ops.Where(o => !FamilyPlanOps.Contains(o.Kind)).Select(o => o.Kind).Distinct().ToList();
                    if (bad.Count > 0)
                    {
                        error = Status.NotSupported + ": in a family document plan accepts " + string.Join(", ", FamilyPlanOps) + "; not " + string.Join(", ", bad) +
                                ". For connectors and parameters use fam conector / fam param; for everything else, exec.";
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
            if (error != null) return Response.Fail(request.Id, Status.Invalid, error);

            if (string.IsNullOrWhiteSpace(plan.Summary))
                plan.Summary = string.Join("; ", plan.Entries.GroupBy(e => OpCatalog.NameOf(e.Op) + (e.Op == "set_param" ? " " + e.Key : ""))
                    .Select(g => g.Key + " ×" + g.Count()));
            _plans.ExpireOld(TimeSpan.FromMinutes(_config.PlanExpiryMinutes));
            _plans.Add(plan);

            var data = plan.Describe();
            if (Arg.GetBool(request.Args, "timing") == true) data["tiempos_plan"] = result.DispatchJson();
            if (plan.Entries.Count == 0)
            {
                plan.Status = PlanStatus.Discarded;
                plan.StatusDetail = "nothing to change";
                data["estado_plan"] = plan.Status;
                return new Response { Id = request.Id, Rev = rev, Data = data, Warning = "Nothing to change; review the warnings." };
            }
            var holdNow = WillHold(plan.DocKey);
            data["siguiente"] = "commit plan_id=" + plan.Id + " (if the user already authorized the task, apply it without asking for confirmation; if they asked to review, show them the diff first)" +
                                 (holdNow ? ". It will stay in the Revit queue until the user presses Apply queue." : ".");
            return new Response { Id = request.Id, Rev = rev, Data = data };
        }

        /// <summary>What was created is stored in $creados (all) and $creados_n (per operation) for use in the next call without repeating ids.</summary>
        public static List<string> SaveCreated(Session session, Plan plan)
        {
            var saved = new List<string>();
            if (session == null || plan.Status != PlanStatus.Applied || plan.Created.Count == 0) return saved;
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
            // Consolidated check: commit ops=… without plan_id = plan + commit in ONE round trip (same engine, same diff, same verifications).
            // A plan with geometry that returns job_id is not fused: the plan is returned and the agent commits afterwards.
            Dictionary<string, object> fusedPlan = null;
            if (string.IsNullOrEmpty(Arg.GetString(request.Args, "plan_id")) && request.Args.ContainsKey("ops"))
            {
                // clave= promises "applied only once"; if the plan ended without commit (geometry in a job), the journal would consider it fulfilled.
                if (!string.IsNullOrWhiteSpace(Arg.GetString(request.Args, Wire.KeyArg)))
                    return Response.Fail(request.Id, Status.Invalid, "commit ops= does not accept clave=: use plan and then commit plan_id=… clave=… for idempotency");
                var planArgs = new Dictionary<string, object> { ["ops"] = request.Args["ops"] };
                foreach (var k in new[] { "summary", Wire.TaskArg, Wire.DocArg, "timing" }) if (request.Args.TryGetValue(k, out var v)) planArgs[k] = v;
                var pr = await PlanOp(session, new Request { Id = request.Id, Op = Wire.OpPlan, Args = planArgs, DeadlineMs = request.DeadlineMs }, ct).ConfigureAwait(false);
                var pid = pr.Data is Dictionary<string, object> pd0 ? Arg.GetString(pd0, "plan_id") : null;
                if (pr.State != Status.Ok || pid == null || (pr.Data as Dictionary<string, object>)?.ContainsKey("job_id") == true
                    || _plans.Get(pid)?.Status != PlanStatus.Ready)
                {
                    // Always say that NOTHING was applied, also when the plan continues as a job (geometry) or failed.
                    pr.Warning = (pr.Warning == null ? "" : pr.Warning + " ") + "commit NOT run in this round trip: the model was not changed." +
                               (pid != null ? " When the plan is ready, run commit plan_id=" + pid + "." : "");
                    if (pr.Data is Dictionary<string, object> pdx) pdx["commit_ejecutado"] = false;
                    return pr;
                }
                request.Args["plan_id"] = pid;
                var pdesc = (Dictionary<string, object>)pr.Data;
                fusedPlan = new Dictionary<string, object> { ["plan_id"] = pid, ["elementos"] = pdesc.TryGetValue("elementos", out var pe) ? pe : null, ["cambios"] = pdesc.TryGetValue("cambios", out var pc) ? pc : null };
                if (pdesc.TryGetValue("avisos", out var av)) fusedPlan["avisos"] = av;
                if (pdesc.TryGetValue("tiempos_plan", out var tp)) fusedPlan["tiempos_plan"] = tp;
            }
            var committed = await CommitPlan(session, request, ct).ConfigureAwait(false);
            if (fusedPlan != null && committed.Data is Dictionary<string, object> cd) cd["plan_en_el_mismo_viaje"] = fusedPlan;
            return committed;
        }

        private async Task<Response> CommitPlan(Session session, Request request, CancellationToken ct)
        {
            var plan = _plans.Get(Arg.GetString(request.Args, "plan_id"));
            var task = Arg.GetString(request.Args, Wire.TaskArg);
            if (plan == null || (plan.SessionId != session.Id && (string.IsNullOrEmpty(task) || plan.Task != task)))
                return Response.Fail(request.Id, Status.Invalid, "that plan_id does not exist in this conversation (nor in the given task); call plan first");
            _plans.ExpireOld(TimeSpan.FromMinutes(_config.PlanExpiryMinutes));
            if (plan.Status != PlanStatus.Ready)
                return new Response { Id = request.Id, State = MapStatus(plan.Status), Data = plan.Describe(5), Warning = StatusAdvice(plan) };

            // alpha3: modo=ensayo applies with the same engine, verifies and rolls back the group. It is never held nor counted as applied.
            var trialRaw = (Arg.GetString(request.Args, "modo") ?? "").Trim().ToLowerInvariant();
            if (trialRaw.Length > 0 && trialRaw != "ensayo" && trialRaw != "aplicar")
                return Response.Fail(request.Id, Status.Invalid, "commit modo must be aplicar (default) or ensayo");
            var trial = trialRaw == "ensayo";

            // Queue: the user applies it whenever they want from the ribbon. If there is already a queue, it is also held to keep the order.
            if (!trial && WillHold(plan.DocKey))
            {
                if (Guid.TryParseExact(request.AttemptId, "N", out _))
                {
                    plan.ReceiptAttempt = request.AttemptId;
                    plan.ReceiptSession = session.Id;
                    plan.ReceiptAgent = Correlation.Current?.AgentId;
                    plan.ReceiptDocPrint = DocTarget.Ambient.Value?.Print;
                }
                _plans.Hold(plan);
                Ui.Runtime.MarkDirty();
                var pos = _plans.Held(plan.DocKey).FindIndex(p => p.Id == plan.Id) + 1;
                var d = plan.Describe(0);
                d["posicion_en_cola"] = (long)pos;
                if (plan.ReceiptAttempt != null)
                    d["recibo"] = HeldReceipt(plan);
                return new Response
                {
                    Id = request.Id, State = Status.Held, Data = d,
                    Warning = "It is in the Revit queue (position " + pos + "). The user will apply it with 'Apply queue' on the BIMBRIDGE ribbon. " +
                            "Do not repeat it; check job with plan_id=" + plan.Id + " later. You can keep planning: new plans chain on top of the queue.",
                };
            }

            // 0.8.0 (rc1 pending item H2): a persistent commit leaves intent and outcome in the chained audit.
            // Without audit nothing is written (fail-closed, same as exec): an unrecorded write cannot be reconciled.
            string auditIntent = null;
            var originalTarget = DocTarget.Ambient.Value;
            if (!trial)
            {
                if (Ui.Runtime.Audit == null)
                {
                    plan.Status = PlanStatus.Ready;
                    return Response.Fail(request.Id, Status.Rejected, "audit unavailable: the persistent commit is rejected without changing anything");
                }
                try
                {
                    auditIntent = Ui.Runtime.Audit.Intent(session.Id, "commit", plan.DocKey ?? "", new Dictionary<string, object>
                    {
                        ["plan_id"] = plan.Id, ["clave"] = Arg.GetString(request.Args, "clave") ?? "", ["documento"] = plan.DocTitle ?? "",
                        ["intento_id"] = Guid.TryParseExact(request.AttemptId, "N", out _) ? request.AttemptId : null,
                        ["huella_doc"] = originalTarget?.Key == plan.DocKey ? originalTarget?.Print : null,
                        ["agente"] = Correlation.Current?.AgentId,
                    });
                }
                catch (Exception aex) when (aex is IOException || aex is UnauthorizedAccessException || aex is InvalidOperationException)
                {
                    plan.Status = PlanStatus.Ready;
                    return Response.Fail(request.Id, Status.Rejected, "could not record the intent in the audit (" + aex.Message + "); nothing was written");
                }
            }

            string error = null;
            PlanOps.ApplyResult r = null;
            long rev = 0;
            bool applyEntered = false;
            bool wasModified = false, lostModified = false;   // P0: a real commit rolled back by a failure can also clear the flag
            DocWriteBlock blocked = null;
            long queueMs = 0, revitMs = 0;
            long loadMs = 0, beginMs = 0, applyMs = 0, finishMs = 0;   // phases within revit_ms (timing=true)
            plan.Status = PlanStatus.InProgress;
            var sw = Stopwatch.StartNew();
            // The plan is applied to ITS document (handle pinned when planning), even if the user switched tabs.
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
                    if (doc == null) error = "the active model is no longer " + plan.DocTitle + "; ask the user to activate it or plan again";
                }
                if (doc == null) return true;
                // P0 (Codex): checked on the Revit thread right before opening the group; applies to any MCP client.
                if (trial) { error = Engine.TrialGuard.Refuse(doc.IsModified, "commit modo=ensayo"); if (error != null) return true; }
                queueMs = sw.ElapsedMilliseconds;
                wasModified = doc.IsModified;
                // On the Revit thread: this process's commits run serially, and the intent is on disk before opening the group.
                var t0 = sw.ElapsedMilliseconds;
                try
                {
                    _plans.LoadPersisted(plan.DocKey);
                    loadMs = sw.ElapsedMilliseconds - t0;
                    _plans.BeginWrite(plan);
                    beginMs = sw.ElapsedMilliseconds - t0 - loadMs;
                }
                catch (DocWriteBlockedException ex) { blocked = ex.Block; return true; }
                catch (PlanStoreException ex) { error = ex.Message + "; nothing was written"; return true; }
                _tracker.CurrentWriter = session.Id;
                applyEntered = true;
                var t1 = sw.ElapsedMilliseconds;
                try { r = trial ? PlanOps.Trial(doc, plan, _config.SubBatchSize, session.Id) : PlanOps.Apply(doc, plan, _config.SubBatchSize, session.Id); }
                finally { _tracker.CurrentWriter = null; }
                applyMs = sw.ElapsedMilliseconds - t1;
                var t2 = sw.ElapsedMilliseconds;
                revitMs = t2 - queueMs;   // same boundary as before (without Finish): comparable with the A/B campaign
                // Here too: the next commit in the Revit queue must not find an already resolved intent.
                _plans.Finish(plan, r.Status, r.Detail);
                finishMs = sw.ElapsedMilliseconds - t2;
                rev = _tracker.For(doc)?.Rev ?? 0;
                lostModified = wasModified && !doc.IsModified;
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);

            Dictionary<string, object> timing = null;
            if (Arg.GetBool(request.Args, "timing") == true)
            {
                timing = result.DispatchJson();
                timing["carga_ms"] = loadMs;               // LoadPersisted
                timing["intencion_ms"] = beginMs;          // BeginWrite: cross-process lock + intent on disk
                timing["aplicar_verificar_ms"] = applyMs;  // PlanOps.Apply/Trial
                timing["cierre_ms"] = finishMs;            // Finish
            }

            if (!result.Ok)
            {
                if (applyEntered)
                {
                    // It already started writing: repeating it could duplicate. It stays terminal, with durable evidence, and does not go back to Listo.
                    var detail = (trial ? "The trial started" : "The commit started") + " and did not deliver a result confirmation (" + result.Kind + "). Reconcile the document before repeating.";
                    Engine.GroupAbort.PreserveInterrupted(plan, detail);
                    _plans.Finish(plan, Status.Unknown, detail);
                    AuditOutcome(session, auditIntent, Status.Unknown, rev);
                    return Response.Fail(request.Id, Status.Unknown, detail);
                }
                plan.Status = PlanStatus.Ready;
                AuditOutcome(session, auditIntent, result.Kind == "busy" ? "no_ejecutado_ocupado" : "incierto_" + result.Kind, 0);
                return FromWork(request.Id, result);
            }
            if (blocked != null)
            {
                AuditOutcome(session, auditIntent, "rechazado", 0);
                plan.Status = PlanStatus.Ready;
                return BlockedResponse(request.Id, blocked);
            }
            if (error != null)
            {
                AuditOutcome(session, auditIntent, "rechazado", 0);
                plan.Status = PlanStatus.Ready;
                var fr = Response.Fail(request.Id, error.StartsWith(Status.TargetUnavailable, StringComparison.Ordinal) ? Status.TargetUnavailable : Status.Rejected, error);
                if (error.StartsWith(Engine.TrialGuard.Code, StringComparison.Ordinal)) fr.Data = new Dictionary<string, object> { ["codigo"] = Engine.TrialGuard.Code, ["sin_cambios"] = true };
                return fr;
            }
            if (trial)
            {
                // An uncertain trial stays terminal and keeps its status for job.
                // Successful trial: the plan goes back to ready only if the intent was removed from disk.
                var ok = r.Status == PlanStatus.Trialed && _plans.Finish(plan, r.Status, r.Detail);
                if (ok) { plan.Status = PlanStatus.Ready; plan.FinishedUtc = null; }
                else if (r.Status != PlanStatus.Trialed) _plans.Finish(plan, r.Status, r.Detail);
                var td = plan.Describe(0);
                td["ensayo"] = r.Trial ?? new Dictionary<string, object>();
                td["detalle"] = r.Detail;
                td["ms"] = sw.ElapsedMilliseconds;
                td["espera_cola_ms"] = queueMs;
                td["revit_ms"] = revitMs;
                if (timing != null) td["tiempos_commit"] = timing;
                return new Response
                {
                    Id = request.Id, State = ok ? PlanStatus.Trialed : (r.Status == PlanStatus.Conflict ? Status.Conflict : r.Status == PlanStatus.RolledBack ? Status.RolledBack : Status.Unknown),
                    Rev = rev, Data = td,
                    Warning = ok ? "Trial rolled back: the model is as it was. This is NOT a commit; to really apply, commit without modo."
                               : r.Detail,
                };
            }
            _plans.Finish(plan, r.Status, r.Detail);
            var data = plan.Describe(0);
            var saved = SaveCreated(session, plan);
            if (saved.Count > 0) data["conjuntos_guardados"] = saved.Cast<object>().ToList();
            data["ms"] = sw.ElapsedMilliseconds;
            data["espera_cola_ms"] = queueMs;
            data["revit_ms"] = revitMs;
            if (timing != null) data["tiempos_commit"] = timing;
            var advice = StatusAdvice(plan);
            if (lostModified)
            {
                // IsModified is not manipulated: it is reported. The document had unsaved changes and, after rolling back, Revit marks it as not modified.
                data["marca_cambios_sin_guardar_perdida"] = true;
                advice = "WARNING: the document had unsaved changes and, on rollback, Revit stopped marking it as modified; those changes are still unsaved. " + advice;
            }
            var status = MapStatus(plan.Status);
            if (plan.Status == PlanStatus.Applied)
            {
                // AUD-09: commit committed ≠ postconditions met.
                var post = PlanVerification.Postconditions(plan.Verification);
                data["commit"] = "confirmado";
                data["postcondiciones"] = post;
                if (post != PlanVerification.Met)
                {
                    status = Status.CompletedWithDifferences;
                    advice = "It was applied (commit committed), but the re-read does not match what was requested or could not be verified: check 'verificacion'. " + advice;
                }
            }
            var auditSaved = AuditOutcome(session, auditIntent, plan.Status, rev, status);
            if (Guid.TryParseExact(request.AttemptId, "N", out _))
                data["recibo"] = new Dictionary<string, object>
                {
                    ["intento_id"] = request.AttemptId, ["plan_id"] = plan.Id, ["doc"] = plan.DocKey,
                    ["estado"] = auditSaved ? status : Status.Unknown,
                };
            return new Response { Id = request.Id, State = status, Rev = rev, Data = data, Warning = advice };
        }

        private static Dictionary<string, object> HeldReceipt(Plan plan) => new Dictionary<string, object>
        {
            ["intento_id"] = plan.ReceiptAttempt, ["plan_id"] = plan.Id, ["doc"] = plan.DocKey, ["estado"] = Status.Held,
        };

        /// <summary>Closes the audit intent. If it fails, the intent stays pending and PendingIntents reports it for reconciliation.</summary>
        private static bool AuditOutcome(Session session, string intent, string status, long rev, string receiptState = null)
        {
            var audit = Ui.Runtime.Audit;
            if (intent == null || audit == null) return false;
            var extra = new Dictionary<string, object> { ["rev"] = rev };
            if (receiptState != null) extra["estado_recibo"] = receiptState;
            try { audit.Outcome(session.Id, "commit", intent, status, extra); return true; }
            catch (Exception) { return false; } // The intent stays visible in PendingIntents.
        }

        private static Response BlockedResponse(long id, DocWriteBlock block)
        {
            var fail = Response.Fail(id, Status.Rejected, block.Message);
            fail.Data = block.ToJson();
            return fail;
        }

        /// <summary>Common write barrier: re-reads the disk and, when in any doubt, does not let it through.</summary>
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
        /// job op=pendientes: document lock. job op=reconciliar plan_id=…: compares the evidence with the model and releases only if it is
        /// proven that everything was rolled back or applied. Reads the model, does not change it, and works from any conversation because the plan may
        /// be left over from a session before a restart.
        /// </summary>
        private async Task<Response> ReconcileJob(Session session, Request request, string jop, CancellationToken ct)
        {
            var key = DocTarget.Ambient.Value?.Key;
            if (key == null) return Response.Fail(request.Id, Status.TargetUnavailable, "specify the document with doc= or activate it in Revit");
            try { _plans.LoadPersisted(key); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* CheckWritable decide */ }
            if (jop == "pendientes") return new Response { Id = request.Id, Data = _plans.CheckWritable(key).ToJson() };
            if (!_config.Allows('W')) return Response.Fail(request.Id, Status.Rejected, "reconciliar requires write permission (W)");
            var plan = _plans.Get(Arg.GetString(request.Args, "plan_id"));
            if (plan == null || plan.Status == PlanStatus.Reconciled || !string.Equals(plan.DocKey, key, StringComparison.OrdinalIgnoreCase))
                return Response.Fail(request.Id, Status.Invalid, "there is no pending plan with that plan_id in this document (job op=pendientes)");
            ReconcileCheck check = null;
            string error = null;
            var result = await RunMain((app, watch, budget) =>
            {
                var doc = DocTarget.Resolve(app, out var docErr);
                if (doc == null) { error = docErr ?? "there is no open model"; return true; }
                var reader = new Writes.PlanApplier(doc, plan, 1, "reconciliar");
                check = ReconcileEvaluator.Evaluate(plan, reader.Current, id => doc.GetElement(Ids.ToId(id)) != null);
                return true;
            }, highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (error != null) return Response.Fail(request.Id, Status.TargetUnavailable, error);
            bool released;
            try { released = _plans.Reconcile(plan, check, session.Id); }
            catch (PlanStoreException ex) { return Response.Fail(request.Id, Status.Rejected, ex.Message); }
            try { Ui.Runtime.Audit?.Outcome(session.Id, "reconciliar", plan.Id, check.Verdict, check.ToJson()); }
            catch (Exception ex) { check.Reasons.Add("audit: " + ex.Message); }
            var data = check.ToJson();
            data["plan_id"] = plan.Id;
            data["liberado"] = released;
            data["bloqueo"] = _plans.CheckWritable(key).ToJson();
            return new Response
            {
                Id = request.Id, State = released ? Status.Ok : Status.Unknown, Data = data,
                Warning = released ? "Reconciled and recorded. This plan is not repeated; plan again if needed." :
                    "The result could not be proven: the document stays locked. Review the model; if needed, a person must resolve it with AgentHost plan-store with Revit closed.",
            };
        }

        private async Task<Response> Job(Session session, Request request, CancellationToken ct)
        {
            var attempt = Arg.GetString(request.Args, "intento_id");
            if (attempt != null)
            {
                if (!Guid.TryParseExact(attempt, "N", out _) || request.Args.ContainsKey("plan_id") || request.Args.ContainsKey("job_id")
                    || !string.Equals(Arg.GetString(request.Args, "op") ?? "status", "status", StringComparison.OrdinalIgnoreCase))
                    return Response.Fail(request.Id, Status.Invalid, "job intento_id only accepts a status query.");
                return ReadWriteReceipt(session, request.Id, attempt);
            }
            var jop = (Arg.GetString(request.Args, "op") ?? "status").Trim().ToLowerInvariant();
            if (jop == "pendientes" || jop == "reconciliar") return await ReconcileJob(session, request, jop, ct).ConfigureAwait(false);
            var jobId = Arg.GetString(request.Args, "job_id");
            if (!string.IsNullOrEmpty(jobId))
            {
                var job = _jobs.Get(jobId);
                if (!JobStore.Visible(job, session.Id, Arg.GetString(request.Args, Wire.TaskArg)))
                    return Response.Fail(request.Id, Status.Invalid, "that job_id does not exist in this conversation nor in the given task (tarea=…)");
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
                    return Response.Fail(request.Id, Status.Invalid, "job op with job_id: status/wait (default, waits for the result) or cancel");
                return await AwaitJob(request, job, ct).ConfigureAwait(false);
            }

            var id = Arg.GetString(request.Args, "plan_id");
            if (!string.IsNullOrEmpty(id))
            {
                var plan = _plans.Get(id);
                var ptask = Arg.GetString(request.Args, Wire.TaskArg);
                if (plan == null || (plan.SessionId != session.Id && (string.IsNullOrEmpty(ptask) || plan.Task != ptask)))
                    return Response.Fail(request.Id, Status.Invalid, "that plan_id does not exist in this conversation nor in the given task");
                if (jop == "drop")
                {
                    if (plan.Status == PlanStatus.Held)
                        return Response.Fail(request.Id, Status.Rejected,
                            "That plan is already in the user's queue; only they can discard it from Queue on the BIMBRIDGE ribbon. Tell them.");
                    if (plan.Status != PlanStatus.Ready)
                        return new Response { Id = request.Id, State = MapStatus(plan.Status), Data = plan.Describe(0), Warning = "Nothing to discard: " + StatusAdvice(plan) };
                    _plans.Finish(plan, PlanStatus.Discarded, "discarded by the AI before applying it");
                    return new Response { Id = request.Id, Data = new Dictionary<string, object> { ["plan_id"] = plan.Id, ["descartado"] = true } };
                }
                if (jop != "status")
                    return Response.Fail(request.Id, Status.Invalid, "job op: status (default) or drop");
                return new Response { Id = request.Id, State = MapStatus(plan.Status), Data = plan.Describe(10), Warning = StatusAdvice(plan) };
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
                Data = new Dictionary<string, object>
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
                case PlanStatus.Applied: return Status.Ok;
                case PlanStatus.Held: return Status.Held;
                case PlanStatus.Conflict: return Status.Conflict;
                case PlanStatus.RolledBack: return Status.RolledBack;
                case Status.Unknown: return Status.Unknown;
                case PlanStatus.Expired: return Status.Expired;
                case PlanStatus.InProgress: return Status.InProgress;
                case PlanStatus.Discarded: return Status.Rejected;
                default: return Status.Ok;
            }
        }

        private static string StatusAdvice(Plan p)
        {
            switch (p.Status)
            {
                case PlanStatus.Applied: return null;
                case PlanStatus.Ready: return "Plan ready; call commit to apply it.";
                case PlanStatus.Held: return "Still in the Revit queue waiting for the user to press Apply queue. Do not repeat it.";
                case PlanStatus.Conflict: return "Something changed since it was planned and nothing was applied: " + p.StatusDetail;
                case PlanStatus.RolledBack: return "Revit rejected it and it was rolled back entirely: " + p.StatusDetail;
                case PlanStatus.Expired: return "The plan expired without commit; plan again.";
                case PlanStatus.Discarded: return "Discarded: " + p.StatusDetail + ". Do not repeat it without asking the user.";
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
                            Data = new Dictionary<string, object>
                            {
                                ["cols"] = new List<object> { "conjunto", "n", "doc" },
                                ["rows"] = session.Sets.Describe().Cast<object>().ToList(),
                            },
                        };
                    case "drop":
                        var name = Arg.GetString(request.Args, "name");
                        var removed = session.Sets.Remove(name);
                        return new Response { Id = request.Id, Data = new Dictionary<string, object> { ["borrado"] = removed } };
                    default:
                        return Response.Fail(request.Id, Status.Invalid, "op must be list or drop");
                }
            }
            catch (ArgumentException ex)
            {
                return Response.Fail(request.Id, Status.Invalid, ex.Message);
            }
        }

        private Response FromWork(long id, WorkResult r, bool write = false)
        {
            if (r.Kind == WorkResult.KindCancelled && r.Slices > 0 && write)
                return Response.Fail(id, Status.Unknown,
                    "It was cancelled after it had already started writing (" + r.Slices + " slices in Revit): it may have been partially applied. Read the model and reconcile before repeating.");
            switch (r.Kind)
            {
                case "busy":
                    var busy = Response.Fail(id, Status.RevitBusy,
                        "Revit did not respond within " + _config.BusyAfterMs / 1000 + " s: probably a dialog is open, a command is in progress or an edit mode is active. Ask the user to close it and retry.");
                    busy.Data = new Dictionary<string, object> { ["despacho"] = r.DispatchJson() };
                    return busy;
                case "cancelled":
                    return Response.Fail(id, Status.TimedOut,
                        r.Slices > 0
                            ? "Time ran out and the read was cancelled midway (it writes nothing). Narrow the request (fewer levels, views or ids) before repeating it."
                            : "Time ran out and the operation was cancelled before starting in Revit, without changing anything. Narrow the request before repeating it.");
                default:
                    var ex = r.Error;
                    return Response.Fail(id, Status.Error, ex == null ? "unknown error" : ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
