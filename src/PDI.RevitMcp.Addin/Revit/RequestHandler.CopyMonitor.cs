// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Engine.Workflows;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Addin.Revit
{
    /// <summary>
    /// 0.9.1 (Bloque F, #23/#24): check name=copy_monitor, de SOLO LECTURA.
    ///  - fase=preflight (por defecto): vínculo, transformación, aparatos de origen por tipo, aparatos ya presentes en el anfitrión
    ///    (se guardan como conjunto $cm_antes para la verificación), la capacidad nativa real de este cliente y los pasos del traspaso
    ///    manual. No hay API pública de Copia por lotes: nunca se declara "copiado" desde aquí.
    ///  - fase=verificar: relee el anfitrión y compara con el origen (tipo esperado tras el mapeo, posición, orientación, supervisión,
    ///    duplicados). Una copia sin relación de supervisión falla.
    /// </summary>
    public sealed partial class RequestHandler
    {
        /// <summary>
        /// 0.9.1: check name=cobertura — descubrimiento bajo demanda del catálogo de capacidades sin entrar al hilo de Revit.
        /// id=F20-CIRCUIT-CREATE-001 devuelve el contrato completo; si no, filas compactas paginadas filtradas por
        /// q (texto), dominio, estado, estrategia, version, categoria, documento, prioridad, pagina, por_pagina.
        /// </summary>
        /// <summary>0.9.1 (Bloque H): check name=model_health [cats=…] [requeridos=Param1,Param2] — salud del modelo, sólo lectura, por tramos.</summary>
        private async Task<Response> ModelHealthCheck(Request request, CancellationToken ct)
        {
            var probe = new ModelHealthProbe(Arg.GetStringList(request.Args, "cats"), Arg.GetStringList(request.Args, "requeridos"), out var err);
            if (err != null) return Response.Fail(request.Id, Status.Invalido, "model_health: " + err);
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => probe.Step(app, watch, budget, _tracker),
                highPriority: false, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (probe.Error != null) return Response.Fail(request.Id, Status.Invalido, probe.Error);
            var datos = Engine.Qa.ModelHealth.Report(probe.Warnings, probe.Elements, probe.Connectors, probe.Sheets, probe.Views, Arg.GetStringList(request.Args, "requeridos"));
            var trunc = probe.Truncation();
            if (trunc.Count > 0) datos["truncado"] = trunc;   // modelo muy grande: se dice qué quedó sin revisar
            datos["ms"] = sw.ElapsedMilliseconds;
            // 0.9.1: métricas para el benchmark (observadas: hilo de Revit, elementos leídos, aciertos de caché de tipos y niveles).
            if (Arg.GetBool(request.Args, "metricas") == true)
            datos["metricas"] = WorkflowMetrics.FromMainThread(sw.ElapsedMilliseconds, result.WaitMs, result.BusyMs, result.Slices, result.MaxSliceMs)
                .Observe("elements_read", probe.Elements.Count).Observe("elements_written", 0).Observe("transactions", 0)
                .Observe("cache_hits", probe.CacheHits).Observe("cache_misses", probe.CacheMisses).ToJson();
            return new Response { Id = request.Id, Rev = probe.Rev, Datos = datos };
        }

        /// <summary>0.9.1 (Bloque I): reparto justo de operaciones largas entre agentes (guardar, sincronizar, exportar).</summary>
        private readonly OperationScheduler _scheduler = new OperationScheduler();

        /// <summary>Panel de estado (#5): lo ve la PERSONA en su Revit, por eso incluye todas las sesiones. Sólo memoria; nunca la API de Revit.</summary>
        public (List<Job> Running, List<OperationHistory.Record> Recent, Dictionary<string, object> Scheduler) OperationsForPanel(string estado, string texto)
        {
            var recent = _history == null ? new List<OperationHistory.Record>()
                : _history.Query(new OperationHistory.Scope { SeeAll = true }, estado, null, null, null, texto, 1, 25).Page;
            return (_jobs.AllRunning(), recent, _scheduler.Stats());
        }

        /// <summary>Cancelación pedida por la persona desde el panel. Devuelve false si el trabajo ya terminó o no existe.</summary>
        public bool CancelFromPanel(string jobId) => _jobs.Cancel(_jobs.Get(jobId));

        private OperationHistory _history;

        /// <summary>0.9.1 (Bloque I): todo trabajo terminado queda en el historial persistente (el error al abrirlo no impide trabajar).</summary>
        private void HookHistory()
        {
            try { _history = new OperationHistory(System.IO.Path.Combine(Home.Root, "historial", "operaciones.jsonl")); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { _history = new OperationHistory(null); _history.LoadErrors.Add("historial sólo en memoria: " + ex.Message); }
            _jobs.OnAnyFinal = j => _history.Add(j);
        }

        /// <summary>
        /// 0.9.1 (Bloque I): check name=historial [estado=] [tipo=] [sesion=] [desde=ISO] [q=] [pagina=] [por_pagina=] — operaciones largas
        /// terminadas con su consejo de reintento; trabajos en curso de esta sesión con su progreso (cancelables con job cancelar).
        /// </summary>
        private Response HistoryCheck(Session session, Request request)
        {
            var a = request.Args;
            DateTime? since = null;
            var ds = Arg.GetString(a, "desde");
            if (ds != null)
            {
                if (!DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    return Response.Fail(request.Id, Status.Invalido, "historial desde: fecha ISO 8601");
                since = d;
            }
            // AUD-13: por defecto sólo lo propio (misma sesión o mismo agente con nombre). todos=si exige la autorización explícita del
            // usuario en la configuración (historial_compartido); pedirlo sin ella se rechaza, no se degrada en silencio.
            var agentId = Correlation.AgentIdOf(session.Principal, session.AgentName);
            var wantAll = Arg.GetBool(a, "todos") ?? false;
            var scope = OperationHistory.ScopeFor(session.Id, agentId, wantAll, _config.SharedHistory, out var refusal);
            if (scope == null) return Response.Fail(request.Id, Status.Rechazado, "historial todos=si: " + refusal);
            var (page, total) = _history.Query(scope, Arg.GetString(a, "estado"), Arg.GetString(a, "tipo"), Arg.GetString(a, "sesion"), since, Arg.GetString(a, "q"),
                (int)(Arg.GetLong(a, "pagina") ?? 1), (int)(Arg.GetLong(a, "por_pagina") ?? 20));
            var datos = new Dictionary<string, object>
            {
                ["total"] = (long)total,
                ["ambito"] = wantAll ? "todas las sesiones (autorizado por el usuario)" : "propio (esta sesión y este agente)",
                ["cols"] = new List<object> { "job_id", "tipo", "estado", "ms", "reintento", "fin", "error" },
                ["rows"] = page.Select(r => (object)new List<object> { r.JobId, r.Kind, r.Estado, r.Ms, r.Retry, r.FinishedUtc.ToString("o", CultureInfo.InvariantCulture), r.Error }).ToList(),
                ["en_curso"] = _jobs.ForOwner(session.Id, agentId).Where(j => !j.IsFinal).Select(j => (object)j.Progress()).ToList(),
                ["reintento_significa"] = "reintentable = no se aplicó nada; reconciliar_antes = resultado incierto o parcial: compruebe el modelo antes de repetir; " +
                                          "revisar_diferencias = se aplicó pero no coincide con lo pedido: revise o reconcilie, no repita",
            };
            if (_history.LoadErrors.Count > 0) datos["avisos_historial"] = _history.LoadErrors.Cast<object>().ToList();
            return new Response { Id = request.Id, Datos = datos };
        }

        private static Response CoverageCheck(Request request)
        {
            var a = request.Args;
            var idx = Engine.Coverage.CapabilityIndex.Default;
            var id = Arg.GetString(a, "id");
            if (id != null)
            {
                var c = idx.Get(id);
                return c == null ? Response.Fail(request.Id, Status.Invalido, "no existe la capacidad " + id)
                                 : new Response { Id = request.Id, Datos = Engine.Coverage.CapabilityIndex.Contract(c) };
            }
            var q = new Engine.Coverage.CapabilityQuery
            {
                Text = Arg.GetString(a, "q"), Domain = Arg.GetString(a, "dominio"), Status = Arg.GetString(a, "estado"), Strategy = Arg.GetString(a, "estrategia"),
                Category = Arg.GetString(a, "categoria"), DocumentKind = Arg.GetString(a, "documento"), Priority = Arg.GetString(a, "prioridad"),
                Version = (int?)Arg.GetLong(a, "version"), Page = (int)(Arg.GetLong(a, "pagina") ?? 1), PageSize = (int)(Arg.GetLong(a, "por_pagina") ?? 20),
            };
            var (page, total) = idx.Search(q);
            return new Response
            {
                Id = request.Id,
                Datos = new Dictionary<string, object>
                {
                    ["total"] = (long)total, ["pagina"] = (long)Math.Max(1, q.Page), ["catalogo"] = idx.Version,
                    ["cols"] = Engine.Coverage.CapabilityIndex.RowColumns, ["rows"] = page.Select(c => (object)Engine.Coverage.CapabilityIndex.Row(c)).ToList(),
                    ["detalle"] = "check name=cobertura id=<id> para el contrato completo",
                },
            };
        }

        private async Task<Response> CopyMonitorCheck(Session session, Request request, CancellationToken ct)
        {
            var link = Arg.GetString(request.Args, "vinculo");
            if (string.IsNullOrWhiteSpace(link)) return Response.Fail(request.Id, Status.Invalido, "copy_monitor: falta vinculo (nombre o id de la instancia del vínculo)");
            var phase = (Arg.GetString(request.Args, "fase") ?? "preflight").Trim().ToLowerInvariant();
            if (phase != "preflight" && phase != "verificar") return Response.Fail(request.Id, Status.Invalido, "copy_monitor fase: preflight o verificar");
            var maps = new List<TypeMapping>();
            if (request.Args.TryGetValue("mapeo", out var m) && m != null)
            {
                if (!(m is Dictionary<string, object> md)) return Response.Fail(request.Id, Status.Invalido, "copy_monitor mapeo: {\"Familia: Tipo origen\": \"Familia: Tipo destino\"}");
                maps.AddRange(md.Select(kv => new TypeMapping { Source = kv.Key, Target = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) }));
            }
            maps.AddRange(Arg.GetStringList(request.Args, "excluir").Select(x => new TypeMapping { Source = x.Trim(), Excluded = true }));

            var probe = new CopyMonitorProbe(link, Arg.GetString(request.Args, "categoria"));
            var sw = Stopwatch.StartNew();
            var result = await RunMain((app, watch, budget) => probe.Step(app, watch, budget, _tracker),
                highPriority: true, busyAfter: TimeSpan.FromMilliseconds(_config.BusyAfterMs), ct: ct).ConfigureAwait(false);
            if (!result.Ok) return FromWork(request.Id, result);
            if (probe.Error != null) return Response.Fail(request.Id, Status.Invalido, probe.Error);

            var setName = "cm_antes_" + probe.LinkInstanceId.ToString(CultureInfo.InvariantCulture);

            // AUD-07: recibo persistente del intento (idempotente por documento + vínculo + clave del cliente), compartido entre procesos.
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
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException) { return Response.Fail(request.Id, Status.Conflicto, "copy_monitor: " + ex.Message); }
            // AUD-20: lo que este intento espera, con lo que se ve AHORA. En preflight se congela; en verificar se compara con lo congelado.
            var mappingGiven = maps.Count > 0;
            var now = new CopyMonitorExpectations
            {
                HostIdentity = probe.DocumentIdentity, LinkIdentity = probe.LinkIdentity, LinkRevision = probe.LinkRevision, Category = probe.Category,
                Transform = probe.Transform, Mappings = maps, Source = probe.Source,
            };
            var datos = new Dictionary<string, object>
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
                // AUD-12: el inventario inicial se persiste por clave de intento y sólo la primera vez. Un preflight repetido, otra
                // sesión o un intento simultáneo reciben el original; nunca se sobrescribe (después de copiar, lo ya copiado pasaría por «previo»).
                CopyMonitorFlow.Result frozenP;
                try { frozenP = CopyMonitorFlow.Freeze(store, attemptKey, now, probe.Host.Select(h => h.Id)); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is System.IO.InvalidDataException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflicto, "copy_monitor: " + ex.Message); }
                var baseline = frozenP.Baseline;
                var baselineCreated = frozenP.Created;
                datos["expectativas"] = new Dictionary<string, object> { ["huella"] = frozenP.Frozen.Fingerprint, ["huella_origen"] = frozenP.Frozen.SourceFingerprint, ["congeladas"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) };
                if (frozenP.Differences.Count > 0) datos["cambios_desde_el_preflight"] = frozenP.Differences.Cast<object>().ToList();
                if (frozenP.Conflict)
                    return new Response
                    {
                        Id = request.Id, Estado = Status.Conflicto, Rev = probe.Rev, Datos = datos,
                        Aviso = "Este intento ya congeló otras expectativas y ahora las condiciones son distintas (" + string.Join("; ", frozenP.Differences) + "). No se sustituyen: " +
                                "si quiere el objetivo original, restaure las condiciones; si el objetivo cambió, inicie un intento nuevo con otra clave=.",
                    };
                var baseSet = new HashSet<long>(baseline.Ids);
                var nuevos = probe.Host.Count(h => !baseSet.Contains(h.Id));
                datos["inventario_inicial"] = new Dictionary<string, object>
                {
                    ["elementos"] = (long)baseline.Ids.Count, ["creado"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["sha256"] = baseline.Sha256,
                    ["conservado"] = !baselineCreated, ["elementos_nuevos_desde_entonces"] = (long)nuevos,
                };
                if (!baselineCreated) datos["aviso_inventario"] = "se conserva el inventario inicial de este intento (no se sobrescribe); para empezar de cero use otra clave=";
                // Comodidad de la sesión (no es la evidencia): los ids previos como conjunto con nombre.
                session.Sets.Save(setName, baseline.Ids, TargetId, TargetTitle);
                datos["conjunto_antes"] = "$" + setName;
                var year = int.TryParse(probe.RevitVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
                if (year >= 2022 && year <= 2026)
                {
                    // Sin ejecutor de UI autorizado (este add-in no lo tiene): el camino veraz es el traspaso manual.
                    var cap = NativeCapabilities.Describe(NativeCapabilities.CopyMonitorFixtures, year, new ClientEnvironment());
                    datos["capacidad"] = cap.ToJson();
                }
                datos["pasos_manuales"] = NativeCapabilities.CopyMonitorManualSteps(probe.LinkName,
                    maps.Where(x => !x.Excluded).Select(x => x.Source + " → " + x.Target).Concat(maps.Where(x => x.Excluded).Select(x => x.Source + " → no copiar")), false).Cast<object>().ToList();
                datos["siguiente"] = "tras Finalizar en Revit: check name=copy_monitor vinculo=" + probe.LinkInstanceId + " fase=verificar (mismo mapeo/excluir y clave)";
                if (receipt.State == WorkflowState.preflight)
                {
                    receipt = store.Record(attemptKey, Ev(EvidenceKind.PreflightPassed, ProofLevel.observed, "addin", probe.Source.Count + " aparatos de origen"));
                    // Este add-in no tiene ejecutor de UI: el camino veraz es el traspaso manual (nunca re-PostCommand).
                    receipt = store.Record(attemptKey, Ev(EvidenceKind.NoUiExecutor, ProofLevel.observed, "addin", "sin ejecutor de UI autorizado"));
                }
                else datos["aviso_intento"] = "el intento ya existe en " + receipt.State + ": no se repite; continúe con fase=verificar o use otra clave";
            }
            else
            {
                // AUD-12: el inventario previo sale del almacén persistente del intento (sobrevive a reconexiones y reinicios), no de la sesión.
                ReceiptStore.Baseline baseline;
                try { baseline = store.LoadBaseline(attemptKey); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is TimeoutException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflicto, "copy_monitor verificar: " + ex.Message); }
                if (baseline == null)
                    return Response.Fail(request.Id, Status.Invalido, "copy_monitor verificar: no hay inventario inicial de este intento (documento, vínculo y clave): ejecute fase=preflight antes de copiar");
                if (baseline.DocumentIdentity != probe.DocumentIdentity || baseline.LinkIdentity != probe.LinkIdentity)
                    return Response.Fail(request.Id, Status.Conflicto, "copy_monitor verificar: el inventario inicial pertenece a otro documento o vínculo");
                // AUD-20: se verifica contra las expectativas CONGELADAS. Si las condiciones que definen el objetivo cambiaron, conflicto.
                CopyMonitorFlow.Result frozenV;
                try { frozenV = CopyMonitorFlow.Load(store, attemptKey, now, mappingGiven); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is TimeoutException || ex is System.IO.IOException)
                { return Response.Fail(request.Id, Status.Conflicto, "copy_monitor verificar: " + ex.Message); }
                datos["expectativas"] = new Dictionary<string, object> { ["huella"] = frozenV.Frozen.Fingerprint, ["huella_origen"] = frozenV.Frozen.SourceFingerprint, ["congeladas"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) };
                if (frozenV.Differences.Count > 0) datos["cambios_desde_el_preflight"] = frozenV.Differences.Cast<object>().ToList();
                if (frozenV.Conflict)
                    return new Response
                    {
                        Id = request.Id, Estado = Status.Conflicto, Rev = probe.Rev, Datos = datos,
                        Aviso = "No se verificó: las condiciones cambiaron desde el preflight (" + string.Join("; ", frozenV.Differences.Where(CopyMonitorExpectations.Blocking)) + "). " +
                                "Las expectativas originales no se sustituyen. Reconcilie: restaure las condiciones y verifique de nuevo, o inicie un intento nuevo con otra clave=.",
                    };
                datos["inventario_inicial"] = new Dictionary<string, object> { ["elementos"] = (long)baseline.Ids.Count, ["creado"] = baseline.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["sha256"] = baseline.Sha256 };
                var beforeSet = new HashSet<long>(baseline.Ids);
                var hostBefore = probe.Host.Where(h => beforeSet.Contains(h.Id)).ToList();
                var report = CopyMonitorVerifier.Verify(frozenV.Frozen.Source, hostBefore, probe.Host, frozenV.Frozen.Transform, frozenV.Frozen.Mappings, probe.LinkInstanceId);
                foreach (var kv in report) datos[kv.Key] = kv.Value;
                if (receipt == null) datos["aviso_intento"] = "no hay recibo de este intento (preflight en otro equipo o con otra clave): verificación sin registrar";
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
                            else datos["aviso_intento"] = why;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException) { datos["aviso_intento"] = ex.Message; }
                    }
                }
                datos["nota"] = "relación origen→copia inferida por tipo y posición: GetMonitoredLinkElementIds devuelve instancias de vínculo, no aparatos de origen";
            }
            if (receipt != null) datos["recibo"] = new Dictionary<string, object> { ["clave_intento"] = attemptKey, ["estado"] = receipt.State.ToString(), ["prueba"] = receipt.Proof.ToString() };
            datos["ms"] = sw.ElapsedMilliseconds;
            return new Response { Id = request.Id, Rev = probe.Rev, Datos = datos };
        }
    }
}
