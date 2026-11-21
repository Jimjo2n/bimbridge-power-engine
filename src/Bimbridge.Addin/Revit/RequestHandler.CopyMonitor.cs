// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bimbridge.Engine;
using Bimbridge.Engine.Workflows;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit
{
    /// <summary>
    /// 0.9.1 (Block F, #23/#24): check name=copy_monitor, READ ONLY.
    ///  - fase=preflight (default): link, transform, source devices by type, devices already present in the host
    ///    (saved as set $cm_antes for verification), this client's actual native capability and the manual handoff
    ///    steps. There is no public batch Copy API: "copied" is never declared from here.
    ///  - fase=verificar: re-reads the host and compares it with the source (expected type after mapping, position, orientation, monitoring,
    ///    duplicates). A copy without a monitoring relationship fails.
    /// </summary>
    public sealed partial class RequestHandler
    {
        /// <summary>
        /// 0.9.1: check name=cobertura — on-demand discovery of the capability catalog without entering the Revit thread.
        /// id=F20-CIRCUIT-CREATE-001 returns the full contract; otherwise compact paged rows filtered by
        /// q (text), dominio, estado, estrategia, version, categoria, documento, prioridad, pagina, por_pagina.
        /// </summary>
        /// <summary>0.9.1 (Block H): check name=model_health [cats=…] [requeridos=Param1,Param2] — model health, read only, in slices.</summary>
        private async Task<Response> ModelHealthCheck(Request request, CancellationToken ct)
        {
            var probe = new ModelHealthProbe(Arg.GetStringList(request.Args, "cats"), Arg.GetStringList(request.Args, "requeridos"), out var err);
            if (err != null) return Response.Fail(request.Id, Status.Invalid, "model_health: " + err);
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => probe.Step(app, watch, budget, _tracker),
                highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (probe.Error != null) return Response.Fail(request.Id, Status.Invalid, probe.Error);
            var data = Engine.Qa.ModelHealth.Report(probe.Warnings, probe.Elements, probe.Connectors, probe.Sheets, probe.Views, Arg.GetStringList(request.Args, "requeridos"));
            var trunc = probe.Truncation();
            if (trunc.Count > 0) data["truncado"] = trunc;   // very large model: report what was left unchecked
            data["ms"] = sw.ElapsedMilliseconds;
            // 0.9.1: benchmark metrics (observed: Revit thread, elements read, type and level cache hits).
            if (Arg.GetBool(request.Args, "metricas") == true)
            data["metricas"] = WorkflowMetrics.FromMainThread(sw.ElapsedMilliseconds, result.WaitMs, result.BusyMs, result.Slices, result.MaxSliceMs)
                .Observe("elements_read", probe.Elements.Count).Observe("elements_written", 0).Observe("transactions", 0)
                .Observe("cache_hits", probe.CacheHits).Observe("cache_misses", probe.CacheMisses).ToJson();
            return new Response { Id = request.Id, Rev = probe.Rev, Data = data };
        }

        /// <summary>0.9.1 (Block I): fair sharing of long operations between agents (save, sync, export).</summary>
        private readonly OperationScheduler _scheduler = new OperationScheduler();

        /// <summary>Status panel (#5): the PERSON sees it in their Revit, so it includes every session. Memory only; never the Revit API.</summary>
        public (List<Job> Running, List<OperationHistory.Record> Recent, Dictionary<string, object> Scheduler) OperationsForPanel(string status, string text)
        {
            var recent = _history == null ? new List<OperationHistory.Record>()
                : _history.Query(new OperationHistory.Scope { SeeAll = true }, status, null, null, null, text, 1, 25).Page;
            return (_jobs.AllRunning(), recent, _scheduler.Stats());
        }

        /// <summary>Cancellation requested by the person from the panel. Returns false if the job already finished or does not exist.</summary>
        public bool CancelFromPanel(string jobId) => _jobs.Cancel(_jobs.Get(jobId));

        private OperationHistory _history;

        /// <summary>0.9.1 (Block I): every finished job is kept in the persistent history (failing to open it does not prevent work).</summary>
        private void HookHistory()
        {
            try { _history = new OperationHistory(System.IO.Path.Combine(Home.Root, "historial", "operaciones.jsonl")); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { _history = new OperationHistory(null); _history.LoadErrors.Add("history in memory only: " + ex.Message); }
            _jobs.OnAnyFinal = j => _history.Add(j);
        }

        /// <summary>
        /// 0.9.1 (Block I): check name=historial [estado=] [tipo=] [sesion=] [desde=ISO] [q=] [pagina=] [por_pagina=] — finished long operations
        /// with their retry advice; this session's running jobs with their progress (cancellable with job cancelar).
        /// </summary>
        private Response HistoryCheck(Session session, Request request)
        {
            var a = request.Args;
            DateTime? since = null;
            var ds = Arg.GetString(a, "desde");
            if (ds != null)
            {
                if (!DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    return Response.Fail(request.Id, Status.Invalid, "historial desde: ISO 8601 date");
                since = d;
            }
            // AUD-13: by default only your own (same session or same named agent). todos=si requires the user's explicit authorization
            // in the configuration (historial_compartido); asking without it is rejected, not silently downgraded.
            var agentId = Correlation.AgentIdOf(session.Principal, session.AgentName);
            var wantAll = Arg.GetBool(a, "todos") ?? false;
            var scope = OperationHistory.ScopeFor(session.Id, agentId, wantAll, _config.SharedHistory, out var refusal);
            if (scope == null) return Response.Fail(request.Id, Status.Rejected, "historial todos=si: " + refusal);
            var (page, total) = _history.Query(scope, Arg.GetString(a, "estado"), Arg.GetString(a, "tipo"), Arg.GetString(a, "sesion"), since, Arg.GetString(a, "q"),
                (int)(Arg.GetLong(a, "pagina") ?? 1), (int)(Arg.GetLong(a, "por_pagina") ?? 20));
            var data = new Dictionary<string, object>
            {
                ["total"] = (long)total,
                ["ambito"] = wantAll ? "all sessions (authorized by the user)" : "own (this session and this agent)",
                ["cols"] = new List<object> { "job_id", "tipo", "estado", "ms", "reintento", "fin", "error" },
                ["rows"] = page.Select(r => (object)new List<object> { r.JobId, r.Kind, r.State, r.Ms, r.Retry, r.FinishedUtc.ToString("o", CultureInfo.InvariantCulture), r.Error }).ToList(),
                ["en_curso"] = _jobs.ForOwner(session.Id, agentId).Where(j => !j.IsFinal).Select(j => (object)j.Progress()).ToList(),
                ["reintento_significa"] = "reintentable = nothing was applied; reconciliar_antes = uncertain or partial result: check the model before repeating; " +
                                          "revisar_diferencias = applied but does not match the request: review or reconcile, do not repeat",
            };
            if (_history.LoadErrors.Count > 0) data["avisos_historial"] = _history.LoadErrors.Cast<object>().ToList();
            return new Response { Id = request.Id, Data = data };
        }

        private static Response CoverageCheck(Request request)
        {
            var a = request.Args;
            var idx = Engine.Coverage.CapabilityIndex.Default;
            var id = Arg.GetString(a, "id");
            if (id != null)
            {
                var c = idx.Get(id);
                return c == null ? Response.Fail(request.Id, Status.Invalid, "capability does not exist: " + id)
                                 : new Response { Id = request.Id, Data = Engine.Coverage.CapabilityIndex.Contract(c) };
            }
            var q = new Engine.Coverage.CapabilityQuery
            {
                Text = Arg.GetString(a, "q"), Domain = Arg.GetString(a, "dominio"), Status = Arg.GetString(a, "estado"), Strategy = Arg.GetString(a, "estrategia"),
                Category = Arg.GetString(a, "categoria"), DocumentKind = Arg.GetString(a, "documento"), Priority = Arg.GetString(a, "prioridad"),
                Version = (int?)Arg.GetLong(a, "version"), Page = (int)(Arg.GetLong(a, "pagina") ?? 1), PageSize = (int)(Arg.GetLong(a, "por_pagina") ?? 0),
                Relevance = string.Equals(Arg.GetString(a, "modo"), "relevancia", StringComparison.OrdinalIgnoreCase),
            };
            var (page, total) = idx.Search(q);
            return new Response
            {
                Id = request.Id,
                Data = new Dictionary<string, object>
                {
                    ["total"] = (long)total, ["pagina"] = (long)Math.Max(1, q.Page), ["catalogo"] = idx.Version,
                    ["cols"] = Engine.Coverage.CapabilityIndex.RowColumns, ["rows"] = page.Select(c => (object)Engine.Coverage.CapabilityIndex.Row(c)).ToList(),
                    ["detalle"] = "check name=cobertura id=<id> for the full contract",
                },
            };
        }

        private async Task<Response> CopyMonitorCheck(Session session, Request request, CancellationToken ct)
        {
            var link = Arg.GetString(request.Args, "vinculo");
            if (string.IsNullOrWhiteSpace(link)) return Response.Fail(request.Id, Status.Invalid, "copy_monitor: missing vinculo (name or id of the link instance)");
            var phase = (Arg.GetString(request.Args, "fase") ?? "preflight").Trim().ToLowerInvariant();
            if (phase != "preflight" && phase != "verificar") return Response.Fail(request.Id, Status.Invalid, "copy_monitor fase: preflight or verificar");
            var maps = new List<TypeMapping>();
            if (request.Args.TryGetValue("mapeo", out var m) && m != null)
            {
                if (!(m is Dictionary<string, object> md)) return Response.Fail(request.Id, Status.Invalid, "copy_monitor mapeo: {\"Family: Source type\": \"Family: Target type\"}");
                maps.AddRange(md.Select(kv => new TypeMapping { Source = kv.Key.Trim(), Target = Convert.ToString(kv.Value, CultureInfo.InvariantCulture)?.Trim() }));
            }
            maps.AddRange(Arg.GetStringList(request.Args, "excluir").Select(x => new TypeMapping { Source = x.Trim(), Excluded = true }));
            var duplicate = maps.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                return Response.Fail(request.Id, Status.Invalid, "copy_monitor: source type «" + duplicate.Key + "» appears more than once in mapeo/excluir");

            var probe = new CopyMonitorProbe(link, Arg.GetString(request.Args, "categoria"));
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => probe.Step(app, watch, budget, _tracker),
                highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (probe.Error != null) return Response.Fail(request.Id, Status.Invalid, probe.Error);

            var setName = "cm_antes_" + probe.LinkInstanceId.ToString(CultureInfo.InvariantCulture);

            // AUD-07: persistent attempt receipt (idempotent by document + link + client key), shared across processes.
            var store = new ReceiptStore(System.IO.Path.Combine(Home.Root, "flujos"));
            var attemptKey = WorkflowReceipt.KeyFor(NativeCapabilities.CopyMonitorFixtures, probe.DocumentIdentity, probe.LinkIdentity, Arg.GetString(request.Args, "clave") ?? "predeterminada");
            var c = Correlation.Current;
            var corr = new WorkflowCorrelation
            {
                AgentId = c?.AgentId, SessionId = c?.SessionId ?? session.Id, RequestId = c?.RequestId, TraceId = c?.TraceId, JobId = c?.JobId,
                DocumentIdentity = probe.DocumentIdentity, LinkIdentity = probe.LinkIdentity, RevitVersion = probe.RevitVersion, ProductBuild = _addinVersion,
            };
            Evidence Ev(string kind, ProofLevel lvl, string source, string detail) => new Evidence { Kind = kind, Level = lvl, Source = source, Detail = detail, AtUtc = DateTime.UtcNow };
            WorkflowReceipt receipt;
            try { receipt = phase == "preflight" ? store.Begin(NativeCapabilities.CopyMonitorFixtures, attemptKey, corr, out _) : store.Load(attemptKey); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException) { return Response.Fail(request.Id, Status.Conflict, "copy_monitor: " + ex.Message); }
            // AUD-20: what this attempt expects, from what is seen NOW. Frozen in preflight; compared against the frozen copy in verificar.
            var mappingGiven = request.Args.ContainsKey("mapeo") || request.Args.ContainsKey("excluir");
            var now = new CopyMonitorExpectations
            {
                HostIdentity = probe.DocumentIdentity, LinkIdentity = probe.LinkIdentity, LinkRevision = probe.LinkRevision, Category = probe.Category,
                Transform = probe.Transform, Mappings = maps, Source = probe.Source,
            };
            var data = new Dictionary<string, object>
            {
                ["vinculo"] = probe.LinkName, ["vinculo_id"] = probe.LinkInstanceId,
                ["transformacion"] = new Dictionary<string, object>
                {
                    ["dx"] = probe.Transform.Dx, ["dy"] = probe.Transform.Dy, ["dz"] = probe.Transform.Dz, ["rot_deg"] = probe.Transform.RotationDeg,
                    ["identidad"] = Math.Abs(probe.Transform.Dx) + Math.Abs(probe.Transform.Dy) + Math.Abs(probe.Transform.Dz) < 1e-9 && Math.Abs(probe.Transform.RotationDeg) < 1e-9,
                },
                ["origen"] = (long)probe.Source.Count,
                ["origen_por_tipo"] = probe.Source.GroupBy(s => CopyMonitorVerifier.Key(s.Family, s.Type)).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => (object)(long)g.Count()),
                ["anfitrion"] = (long)probe.Host.Count,
                ["anfitrion_supervisados"] = (long)probe.Host.Count(h => h.Monitoring),
            };

            if (phase == "preflight")
            {
                // AUD-12: the initial inventory is persisted per attempt key and only the first time. A repeated preflight, another
                // session or a concurrent attempt get the original; it is never overwritten (after copying, what was copied would pass as «previous»).
                CopyMonitorFlow.Result frozenP;
                try { frozenP = CopyMonitorFlow.Freeze(store, attemptKey, now, probe.Host.Select(h => h.Id)); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is System.IO.InvalidDataException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflict, "copy_monitor: " + ex.Message); }
                var baseline = frozenP.Baseline;
                var baselineCreated = frozenP.Created;
                data["expectativas"] = new Dictionary<string, object> { ["huella"] = frozenP.Frozen.Fingerprint, ["huella_origen"] = frozenP.Frozen.SourceFingerprint, ["congeladas"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) };
                if (frozenP.Differences.Count > 0) data["cambios_desde_el_preflight"] = frozenP.Differences.Cast<object>().ToList();
                if (frozenP.Conflict)
                    return new Response
                    {
                        Id = request.Id, State = Status.Conflict, Rev = probe.Rev, Data = data,
                        Warning = "This attempt already froze other expectations and conditions are now different (" + string.Join("; ", frozenP.Differences) + "). They are not replaced: " +
                                "if you want the original target, restore the conditions; if the target changed, start a new attempt with another clave=.",
                    };
                var baseSet = new HashSet<long>(baseline.Ids);
                var newCount = probe.Host.Count(h => !baseSet.Contains(h.Id));
                data["inventario_inicial"] = new Dictionary<string, object>
                {
                    ["elementos"] = (long)baseline.Ids.Count, ["creado"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["sha256"] = baseline.Sha256,
                    ["conservado"] = !baselineCreated, ["elementos_nuevos_desde_entonces"] = (long)newCount,
                };
                if (!baselineCreated) data["aviso_inventario"] = "this attempt's initial inventory is kept (not overwritten); to start from scratch use another clave=";
                // Session convenience (not the evidence): the previous ids as a named set.
                session.Sets.Save(setName, baseline.Ids, TargetId, TargetTitle);
                data["conjunto_antes"] = "$" + setName;
                var year = int.TryParse(probe.RevitVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
                if (year >= 2022 && year <= 2027)
                {
                    // No authorized UI executor (this add-in has none): the truthful path is the manual handoff.
                    var cap = NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, year, new ClientEnvironment());
                    data["capacidad"] = cap.ToJson();
                }
                data["pasos_manuales"] = NativeCapabilities.CopyMonitorManualSteps(probe.LinkName,
                    maps.Where(x => !x.Excluded).Select(x => x.Source + " → " + x.Target).Concat(maps.Where(x => x.Excluded).Select(x => x.Source + " → do not copy")), false).Cast<object>().ToList();
                data["siguiente"] = "after Finish in Revit: check name=copy_monitor vinculo=" + probe.LinkInstanceId + " fase=verificar (same mapeo/excluir and clave)";
                if (receipt.State == WorkflowState.preflight)
                {
                    receipt = store.Record(attemptKey, Ev(EvidenceKind.PreflightPassed, ProofLevel.observed, "addin", probe.Source.Count + " source devices"));
                    // This add-in has no UI executor: the truthful path is the manual handoff (never re-PostCommand).
                    receipt = store.Record(attemptKey, Ev(EvidenceKind.NoUiExecutor, ProofLevel.observed, "addin", "no authorized UI executor"));
                }
                else data["aviso_intento"] = "the attempt already exists in " + receipt.State + ": not repeated; continue with fase=verificar or use another clave";
            }
            else
            {
                // AUD-12: the previous inventory comes from the attempt's persistent store (survives reconnects and restarts), not from the session.
                ReceiptStore.Baseline baseline;
                try { baseline = store.LoadBaseline(attemptKey); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is TimeoutException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflict, "copy_monitor verificar: " + ex.Message); }
                if (baseline == null)
                    return Response.Fail(request.Id, Status.Invalid, "copy_monitor verificar: no initial inventory for this attempt (document, link and clave): run fase=preflight before copying");
                if (baseline.DocumentIdentity != probe.DocumentIdentity || baseline.LinkIdentity != probe.LinkIdentity)
                    return Response.Fail(request.Id, Status.Conflict, "copy_monitor verificar: the initial inventory belongs to another document or link");
                // AUD-20: verified against the FROZEN expectations. If the conditions that define the target changed, conflict.
                CopyMonitorFlow.Result frozenV;
                try { frozenV = CopyMonitorFlow.Load(store, attemptKey, now, mappingGiven); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is TimeoutException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflict, "copy_monitor verificar: " + ex.Message); }
                data["expectativas"] = new Dictionary<string, object> { ["huella"] = frozenV.Frozen.Fingerprint, ["huella_origen"] = frozenV.Frozen.SourceFingerprint, ["congeladas"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) };
                if (frozenV.Differences.Count > 0) data["cambios_desde_el_preflight"] = frozenV.Differences.Cast<object>().ToList();
                if (frozenV.Conflict)
                    return new Response
                    {
                        Id = request.Id, State = Status.Conflict, Rev = probe.Rev, Data = data,
                        Warning = "Not verified: conditions changed since the preflight (" + string.Join("; ", frozenV.Differences.Where(CopyMonitorExpectations.Blocking)) + "). " +
                                "The original expectations are not replaced. Reconcile: restore the conditions and verify again, or start a new attempt with another clave=.",
                    };
                data["inventario_inicial"] = new Dictionary<string, object> { ["elementos"] = (long)baseline.Ids.Count, ["creado"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["sha256"] = baseline.Sha256 };
                var beforeSet = new HashSet<long>(baseline.Ids);
                var hostBefore = probe.Host.Where(h => beforeSet.Contains(h.Id)).ToList();
                var report = CopyMonitorVerifier.Verify(frozenV.Frozen.Source, hostBefore, probe.Host, frozenV.Frozen.Transform, frozenV.Frozen.Mappings, probe.LinkInstanceId);
                foreach (var kv in report) data[kv.Key] = kv.Value;
                if (receipt == null) data["aviso_intento"] = "no receipt for this attempt (preflight on another machine or with another clave): verification not recorded";
                else
                {
                    var res = Arg.GetString(report, "resultado");
                    if (res == "verificado" || res == "fallido")
                    {
                        var kind = res == "verificado" ? EvidenceKind.VerificationPassed : EvidenceKind.VerificationFailed;
                        try
                        {
                            if (WorkflowMachine.Next(receipt, Ev(kind, ProofLevel.verified, "verifier", res), out var why) != null)
                                receipt = store.Record(attemptKey, Ev(kind, ProofLevel.verified, "verifier", res));
                            else data["aviso_intento"] = why;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException) { data["aviso_intento"] = ex.Message; }
                    }
                }
                data["nota"] = "source→copy relationship inferred by type and position: GetMonitoredLinkElementIds returns link instances, not source devices";
            }
            if (receipt != null) data["recibo"] = new Dictionary<string, object> { ["clave_intento"] = attemptKey, ["estado"] = receipt.State.ToString(), ["prueba"] = receipt.Proof.ToString() };
            data["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = probe.Rev, Data = data };
        }
    }
}
