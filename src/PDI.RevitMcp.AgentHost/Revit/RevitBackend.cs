// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PDI.RevitMcp.Bridge;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Transporte tipado hacia UN Revit elegido por el operador (alpha3). Reutiliza la conexión del puente (<see cref="RevitConnection"/>:
    /// saludo de protocolo, comprobación de que el servidor del pipe es el Revit anunciado, latido, timeouts que NO se confunden con
    /// "no aplicado"). Nunca elige un Revit por su cuenta: pid explícito, o un pipe de prueba explícito.
    /// Cada llamada se registra (op, ms, bytes, estado) para la evidencia.
    /// </summary>
    public sealed class RevitLink : IDisposable
    {
        private readonly RevitConnection _c = new RevitConnection { ClientName = "agenthost" };
        public int ExpectedPid { get; }
        public TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
        public TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);
        public readonly List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
        /// <summary>Se llama tras cada respuesta (para volcar evidencia a disco al momento).</summary>
        public Action<Dictionary<string, object>> OnCall;

        private RevitLink(int pid, string pipe, InstanceDescriptor d)
        {
            ExpectedPid = pid;
            _c.FixedTarget = (pipe, d);
        }

        /// <summary>Por pid: debe existir un descriptor de instancia con ese pid (el add-in cargado en ESE Revit).</summary>
        public static RevitLink ForPid(int pid, out string error)
        {
            error = null;
            if (pid <= 0) { error = "falta --revit-pid: el AgentHost nunca elige un Revit por su cuenta"; return null; }
            var d = InstanceDescriptor.ReadAll().FirstOrDefault(x => x.Pid == pid);
            if (d == null)
            {
                var all = InstanceDescriptor.ReadAll();
                error = "no hay un Revit con el add-in cargado y pid " + pid + ". Abiertos con add-in: " +
                        (all.Count == 0 ? "ninguno" : string.Join(", ", all.Select(x => x.Pid + " (" + x.RevitVersion + ")")));
                return null;
            }
            return new RevitLink(pid, d.PipeName, d);
        }

        /// <summary>Pruebas sin Revit: pipe explícito y el pid que el servidor dirá tener en context.</summary>
        public static RevitLink ForTestPipe(string pipe, int pidInContext) => new RevitLink(pidInContext, pipe, null);

        /// <summary>Reintentos de una lectura ante revit_ocupado (5 → hasta 6 intentos, ~40 s en total con la espera del add-in).</summary>
        public int ReadBusyRetries { get; set; } = 5;

        public Response Call(string op, Dictionary<string, object> args, bool write)
        {
            var sw = Stopwatch.StartNew();
            var reqBytes = Json.Write(args ?? new Dictionary<string, object>()).Length;
            // rc1.1: Revit en reposo a veces tarda >5 s en atender el ExternalEvent (medido en la campaña nativa rc1).
            // Solo las LECTURAS se reintentan ante revit_ocupado: no cambian nada, así que repetirlas es seguro.
            // Las escrituras nunca se reintentan aquí; su incertidumbre la resuelve el runner leyendo el modelo.
            var intentos = 0;
            Response r;
            while (true)
            {
                intentos++;
                r = _c.CallAsync(op, args ?? new Dictionary<string, object>(), write ? WriteTimeout : ReadTimeout, CancellationToken.None).GetAwaiter().GetResult();
                if (write || r.Estado != Status.RevitOcupado || intentos > ReadBusyRetries) break;
                Thread.Sleep(Math.Min(4000, 500 * intentos));
            }
            var rec = new Dictionary<string, object>
            {
                ["t"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["op"] = op, ["ms"] = sw.ElapsedMilliseconds,
                ["bytes_peticion"] = (long)reqBytes, ["bytes_respuesta"] = (long)Json.Write(r.ToJson()).Length, ["estado"] = r.Estado,
                ["pid"] = (long)(_c.Instance?.Pid ?? ExpectedPid), ["pipe"] = _c.FixedTarget?.Pipe,
            };
            if (intentos > 1) rec["intentos"] = (long)intentos;
            if (args != null && args.TryGetValue("modo", out var m)) rec["modo"] = m;
            Calls.Add(rec);
            OnCall?.Invoke(rec);
            return r;
        }

        public void Dispose() => _c.Dispose();
    }

    /// <summary>
    /// Backend REAL de AgentHost (alpha3): BIM Task IR → <see cref="RevitLink"/> → add-in → motor tipado plan/commit EXISTENTE.
    /// No hay motor de transacciones nuevo ni código C# libre: fijar_parametro = plan set_param + commit (modo ensayo o aplicar).
    ///
    /// Garantías:
    /// - identidad: pid del context = pid pedido; clave y huella del documento = las del plan; el plan ve SOLO su documento (doc=dN);
    /// - alcance: los ids que el add-in planifica deben ser exactamente los objetivos resueltos (si no, no se confirma nada);
    /// - autorización: el modo (ensayo/persistente) lo fija el operador al lanzar; el add-in exige además su nivel W;
    ///   persistente sólo sobre un documento local, no workshared, no de sólo lectura, y sin cola retenida;
    /// - incertidumbre: timeout o conexión perdida en un commit ⇒ <see cref="UncertainEffectException"/> (el ejecutor observa, no repite);
    /// - ensayo: Ok sólo si el add-in devuelve grupo RolledBack + restauración, Y una relectura independiente coincide con la línea base.
    /// Catálogo alpha3: consultar; fijar_parametro con valores de texto. verificar_qc y valores con unidades: no soportados aquí (se dice).
    /// </summary>
    public sealed class RevitBackend : IExecutionBackend, IDisposable
    {
        private readonly RevitLink _link;
        private readonly Dictionary<string, Baseline> _baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, object>> _trialEvidence = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private string _docHandle;
        /// <summary>Puntos de caída para NRT-03 (sólo pruebas; por defecto inertes).</summary>
        public Faults Faults = new Faults();
        /// <summary>El plan del add-in devuelve hasta 30 filas de diff; alpha3 exige ver todas para comprobar el alcance.</summary>
        /// <summary>rc1: 200 = una página del add-in; el diff del plan se comprueba fila a fila en una sola respuesta (medido en PERFORMANCE_rc1).</summary>
        public const int MaxTargets = 200;
        /// <summary>Páginas máximas de una lectura de alcance (observación/postcondiciones): 50 × 200 = 10 000 elementos.</summary>
        public const int MaxPages = 50;
        /// <summary>Límite de page_size del add-in (CatalogRunner.MaxPageSize). NRT-04 run3 (2026-10-28) falló por pedir 1000.</summary>
        public const int AddinMaxPageSize = 200;

        public sealed class Baseline
        {
            public List<long> Ids = new List<long>();
            public Dictionary<long, string> Values = new Dictionary<long, string>();
        }

        public string Name => "revit (pid " + _link.ExpectedPid + ", " + WriteMode + ")";
        public bool TouchesRealModel => true;
        public string DocKey { get; private set; }
        public string DocPrint { get; private set; }
        public string WriteMode { get; }
        public Dictionary<string, object> Context { get; private set; }
        public RevitLink Link => _link;

        private RevitBackend(RevitLink link, string mode) { _link = link; WriteMode = mode; }

        /// <summary>Conecta y fija identidad. mode: "ensayo" | "persistente" | "lectura".</summary>
        public static RevitBackend Open(RevitLink link, string mode, out string error)
        {
            error = null;
            if (mode != "ensayo" && mode != "persistente" && mode != "lectura") { error = "modo debe ser lectura, ensayo o persistente"; return null; }
            var b = new RevitBackend(link, mode);
            var r = link.Call(Wire.OpContext, new Dictionary<string, object>(), false);
            if (r.Estado != Status.Ok || !(r.Datos is Dictionary<string, object> d)) { error = "context: " + r.Estado + " " + r.Aviso; return null; }
            b.Context = d;
            var pid = Arg.GetLong(d, "pid");
            if (pid != link.ExpectedPid) { error = "el Revit que respondió tiene pid " + pid + ", no " + link.ExpectedPid + "; no se continúa"; return null; }
            b.DocKey = Arg.GetString(d, "clave_doc");
            b.DocPrint = Arg.GetString(d, "huella_doc");
            b._docHandle = Arg.GetString(d, "doc");
            if (b.DocKey == null || b._docHandle == null) { error = "el add-in no informó la identidad del documento activo (clave_doc/doc): add-in anterior a alpha3 o sin documento"; return null; }
            if (mode != "lectura")
            {
                // rc1 P0 fail-closed: una bandera ausente o no booleana NO es "seguro"; se exige el valor explícito.
                foreach (var flag in new[] { "worksharing", "solo_lectura", "modificado", "retener" })
                    if (Arg.GetBool(d, flag) == null) { error = "el add-in no informó «" + flag + "» como booleano; sin ese dato no se escribe (fail-closed)"; return null; }
                if (string.IsNullOrEmpty(b.DocPrint)) { error = "el add-in no informó la huella del documento; sin identidad fuerte no se escribe"; return null; }
                var niveles = Arg.GetString(d, "niveles_mcp") ?? "";
                if (niveles.IndexOf('W') < 0) { error = "el add-in de ese Revit no permite cambios (niveles " + niveles + "); lo decide el usuario"; return null; }
            }
            if (mode == "persistente")
            {
                if (Arg.GetBool(d, "worksharing") != false) { error = "persistente rechazado: el documento es workshared"; return null; }
                if (Arg.GetBool(d, "solo_lectura") != false) { error = "persistente rechazado: documento de sólo lectura"; return null; }
                var ruta = Arg.GetString(d, "ruta") ?? "";
                if (ruta.Length == 0 || ruta.StartsWith("BIM 360", StringComparison.OrdinalIgnoreCase) || ruta.StartsWith("Autodesk Docs", StringComparison.OrdinalIgnoreCase) || b.DocKey.StartsWith("nube:", StringComparison.OrdinalIgnoreCase))
                { error = "persistente rechazado: el documento no es un archivo local (" + ruta + ")"; return null; }
                if (Arg.GetBool(d, "retener") != false || (Arg.GetLong(d, "cola_retenida") ?? 0) > 0)
                { error = "persistente rechazado: Retener/cola activa en Revit (el usuario aplicaría el cambio después; no se puede reconciliar)"; return null; }
            }
            return b;
        }

        // =====================================================================================================

        public const string QcLightingTags = "qc_luminarias_tags";

        private static bool IsWrite(ValidatedOp op) => op.Operation == "fijar_parametro" || op.Operation == "fijar_parametro_lote";

        /// <summary>Valor esperado por id: el mismo para fijar_parametro; el del mapa para fijar_parametro_lote.</summary>
        private static Func<long, string> Expected(ValidatedOp op)
        {
            if (op.Operation == "fijar_parametro_lote")
            {
                var map = ((IDictionary<string, object>)op.Params["valores"]).ToDictionary(kv => long.Parse(kv.Key, CultureInfo.InvariantCulture), kv => Values.Show(kv.Value));
                return id => map.TryGetValue(id, out var v) ? v : null;
            }
            var value = Values.Show(op.Params["valor"]);
            return _ => value;
        }

        /// <summary>QC de luminarias y etiquetas (check lighting_audit del add-in, sólo lectura): desviaciones = sin tag + huérfanos + duplicados sobrantes.</summary>
        private ExecResult RunQc(ValidatedOp op, out long deviations)
        {
            deviations = -1;
            var a = new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["name"] = "lighting_audit", ["tags"] = "uno" };
            var lvl = op.ScopeValue("nivel"); if (lvl != null) a["lvl"] = new List<object> { lvl };
            var r = _link.Call(Wire.OpCheck, a, false);
            if (r.Estado != Status.Ok || !(r.Datos is Dictionary<string, object> d)) return ExecResult.Fail(ErrorClass.Rechazo, "check: " + r.Estado + " " + r.Aviso);
            long N(string k, string f = "n") => d.TryGetValue(k, out var v) && v is Dictionary<string, object> b ? (Arg.GetLong(b, f) ?? -1) : -1;
            var sinTag = N("sin_tag"); var huerf = N("tags_huerfanos"); var dup = N("tags_duplicados", "sobrantes");
            if (sinTag < 0 || huerf < 0 || dup < 0) return ExecResult.Fail(ErrorClass.Rechazo, "check: respuesta incompleta del add-in (sin_tag/tags_huerfanos/tags_duplicados); no se certifica");
            deviations = sinTag + huerf + dup;
            var ok = new ExecResult { Ok = true };
            ok.Data["desviaciones"] = deviations; ok.Data["sin_tag"] = sinTag; ok.Data["tags_huerfanos"] = huerf; ok.Data["tags_duplicados_sobrantes"] = dup;
            ok.Data["luminarias"] = Arg.GetLong(d, "luminarias") ?? -1;
            return ok;
        }

        private static List<long> ScopeIds(ValidatedOp op)
        {
            if (!op.Scope.TryGetValue("ids", out var v) || v == null) return null;
            return ((System.Collections.IEnumerable)v).Cast<object>().Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList();
        }

        private string Unsupported(ValidatedOp op)
        {
            switch (op.Operation)
            {
                case "consultar": return null;
                case "fijar_parametro":
                    if (op.Param("unidades") != "texto") return "no_soportado_alpha3: fijar_parametro contra Revit sólo con unidades=texto (los valores con unidades se comparan en alpha2/beta1)";
                    if (op.Params.ContainsKey("tolerancia")) return "no_soportado_alpha3: tolerancia no aplica a texto";
                    return null;
                case "fijar_parametro_lote": return null;   // valores de texto por id (validados por IrValidator)
                case "verificar_qc":
                    return op.Param("receta") == QcLightingTags ? null : "no_soportado_rc1: verificar_qc contra Revit sólo con receta=" + QcLightingTags + " (sólo lectura)";
                case "verificar_independiente": return null;
                default: return "no_soportado_rc1: " + op.Operation + " no está en el catálogo de Revit de alpha3";
            }
        }

        /// <summary>Lee (ids, valor del parámetro) del alcance de la operación. Sólo lectura.</summary>
        private List<(long Id, string Value)> Read(ValidatedOp op, out string error)
        {
            var rows = ReadRows(op, new[] { op.Param("parametro") }, out error);
            if (rows == null) return null;
            var res = rows.Select(r => (r.Id, r.Values[0])).ToList();
            var ids = ScopeIds(op);
            if (ids != null && (res.Count != ids.Count || res.Any(x => !ids.Contains(x.Item1))))
            { error = "alcance.ids: el documento no contiene exactamente esos elementos con ese filtro (" + res.Count + "/" + ids.Count + ")"; return null; }
            return res;
        }

        /// <summary>0.8.0: lectura paginada genérica de varios campos del alcance (sólo lectura, fail-closed si el total cambia).</summary>
        private List<(long Id, string[] Values)> ReadRows(ValidatedOp op, string[] fields, out string error)
        {
            error = null;
            var f = new List<object> { "id" }; f.AddRange(fields);
            var a = new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["page_size"] = (long)AddinMaxPageSize, ["f"] = f };
            var ids = ScopeIds(op);
            if (ids != null) a["ids"] = ids.Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList();
            var cat = op.ScopeValue("categoria"); if (cat != null) a["cat"] = new List<object> { cat };
            var lvl = op.ScopeValue("nivel"); if (lvl != null) a["lvl"] = new List<object> { lvl };
            var r = _link.Call(Wire.OpQuery, a, false);
            if (r.Estado != Status.Ok || !(r.Datos is Dictionary<string, object> d)) { error = "query: " + r.Estado + " " + r.Aviso; return null; }
            var total = Arg.GetLong(d, "total");
            if (total == null) { error = "query: el add-in no informó total; no se puede comprobar que se leyó todo (fail-closed)"; return null; }
            var rows = d.TryGetValue("rows", out var rv) && rv is List<object> rl ? new List<object>(rl) : new List<object>();
            // rc1: paginación tipada. Se piden las páginas siguientes hasta tener EXACTAMENTE total filas; si el total cambia entre páginas, se aborta.
            var pages = Arg.GetLong(d, "pages") ?? 1;
            if (pages > MaxPages) { error = "el alcance ocupa " + pages + " páginas (máximo " + MaxPages + "); acótelo"; return null; }
            for (var pg = 2; pg <= pages && rows.Count < total; pg++)
            {
                var a2 = new Dictionary<string, object>(a) { ["page"] = (long)pg };
                var r2 = _link.Call(Wire.OpQuery, a2, false);
                if (r2.Estado != Status.Ok || !(r2.Datos is Dictionary<string, object> d2)) { error = "query página " + pg + ": " + r2.Estado + " " + r2.Aviso; return null; }
                if (Arg.GetLong(d2, "total") != total) { error = "el total cambió entre páginas (" + total + " → " + Arg.GetLong(d2, "total") + "): el modelo se modificó durante la lectura"; return null; }
                if (d2.TryGetValue("rows", out var rv2) && rv2 is List<object> rl2) rows.AddRange(rl2);
            }
            if (total != rows.Count) { error = "el alcance tiene " + total + " elementos y la lectura devolvió " + rows.Count; return null; }
            var res = new List<(long, string[])>();
            foreach (var row in rows.OfType<List<object>>())
            {
                var vals = new string[fields.Length];
                for (var i = 0; i < fields.Length; i++) vals[i] = row.Count > i + 1 && row[i + 1] != null ? Convert.ToString(row[i + 1], CultureInfo.InvariantCulture) : null;
                res.Add((Convert.ToInt64(row[0], CultureInfo.InvariantCulture), vals));
            }
            return res;
        }

        /// <summary>
        /// 0.8.0 (CAP-008): verificar_independiente. Lo esperado viene de una fuente independiente declarada en el plan; lo observado se
        /// lee del modelo. Se rechaza si la fuente esperada es la misma que guió la construcción. Sólo lectura.
        /// </summary>
        private static List<string> SplitList(string s) =>
            string.IsNullOrWhiteSpace(s) ? new List<string>() : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private ExecResult RunIndependent(ValidatedOp op, out long deviations)
        {
            deviations = -1;
            var keyParam = op.Param("parametro_clave");
            var attr = op.Param("parametro");
            var fields = attr == null ? new[] { keyParam } : new[] { keyParam, attr };
            var rows = ReadRows(op, fields, out var err);
            if (rows == null) return ExecResult.Fail(ErrorClass.Rechazo, err);
            var input = new Engine.Workflow.IndependentVerifier.Input
            {
                ExpectedSource = new Engine.Workflow.SourceRef { Id = op.Param("fuente_id"), Kind = op.Param("fuente_tipo"), Revision = op.Param("fuente_revision"),
                                                                 Sha256 = op.Param("fuente_sha256"), DerivedFrom = SplitList(op.Param("fuente_derivada_de")) },
                BuildSources = { new Engine.Workflow.SourceRef { Id = op.Param("fuente_construccion"), Sha256 = op.Param("fuente_construccion_sha256"), DerivedFrom = SplitList(op.Param("construccion_derivada_de")) } },
                BuildPlanFingerprint = op.Param("huella_plan"), RequiredRevision = op.Param("revision_exigida"),
                CompareAttrs = attr == null ? new string[0] : new[] { attr },
            };
            foreach (var kv in (IDictionary<string, object>)op.Params["esperado"])
            {
                var it = new Engine.Workflow.IndependentVerifier.Item { Key = kv.Key };
                if (attr != null) it.Attrs[attr] = Values.Show(kv.Value);
                input.Expected.Add(it);
            }
            if (op.Params.TryGetValue("excepciones", out var ex) && ex is IDictionary<string, object> exm)
                foreach (var kv in exm) input.Exceptions[kv.Key] = Values.Show(kv.Value);
            foreach (var row in rows)
            {
                var it = new Engine.Workflow.IndependentVerifier.Item { Key = string.IsNullOrEmpty(row.Values[0]) ? null : row.Values[0], ElementId = row.Id };
                if (attr != null) it.Attrs[attr] = row.Values[1];
                if (it.Key == null) { it.Key = "(sin " + keyParam + ") #" + row.Id.ToString(CultureInfo.InvariantCulture); }
                input.Observed.Add(it);
            }
            var v = Engine.Workflow.IndependentVerifier.Verify(input);
            if (v.Estado != "ok" && v.Estado != "diferencias") return ExecResult.Fail(ErrorClass.Precondicion, "verificar_independiente: " + v.Estado + " — " + v.Motivo);
            deviations = v.Missing.Count + v.Extra.Count + v.Duplicates.Count + v.Mismatches.Count;
            var ok = new ExecResult { Ok = true };
            ok.Data["desviaciones"] = deviations;
            foreach (var kv in v.ToJson()) ok.Data[kv.Key] = kv.Value;
            return ok;
        }

        public Preflight Prepare(ValidatedOp op)
        {
            var u = Unsupported(op);
            if (u != null) return new Preflight { Error = u };
            if (op.Operation == "consultar")
            {
                var r = _link.Call(Wire.OpQuery, new Dictionary<string, object> { [Wire.DocArg] = _docHandle, ["cat"] = new List<object> { op.Param("categoria") }, ["count"] = true }, false);
                if (r.Estado != Status.Ok || !(r.Datos is Dictionary<string, object> d)) return new Preflight { Error = "query: " + r.Estado + " " + r.Aviso };
                return new Preflight { Targets = (int)(Arg.GetLong(d, "total") ?? 0) };
            }
            if (op.Operation == "verificar_qc" || op.Operation == "verificar_independiente") return new Preflight { Targets = 1 };
            if (WriteMode == "lectura") return new Preflight { Error = "escrituras no autorizadas por el operador (modo lectura)" };
            var rows = Read(op, out var err);
            if (rows == null) return new Preflight { Error = err };
            if (rows.Count > MaxTargets) return new Preflight { Error = "máximo " + MaxTargets + " objetivos por operación (el diff del plan se comprueba fila a fila); hay " + rows.Count };
            return new Preflight { Targets = rows.Count };
        }

        public ExecResult Execute(ValidatedOp op, ExecContext ctx)
        {
            if (op.Operation == "consultar")
            {
                var p = Prepare(op);
                if (p.Error != null) return ExecResult.Fail(ErrorClass.Rechazo, p.Error);
                var ok = new ExecResult { Ok = true };
                ok.Data["elementos"] = (long)p.Targets;
                return ok;
            }
            if (op.Operation == "verificar_qc") return RunQc(op, out _);
            if (op.Operation == "verificar_independiente") return RunIndependent(op, out _);
            var param = op.Param("parametro");
            var expected = Expected(op);
            // 1. línea base (antes de cualquier escritura): valores y relaciones que el ensayo debe restaurar
            var rows = Read(op, out var err);
            if (rows == null) return ExecResult.Fail(ErrorClass.Precondicion, err);
            var bl = new Baseline { Ids = rows.Select(x => x.Id).ToList(), Values = rows.ToDictionary(x => x.Id, x => x.Value) };
            _baselines[op.Id] = bl;
            if (bl.Ids.Count == 0) return ExecResult.Fail(ErrorClass.SinObjetivos, "sin objetivos al ejecutar");

            // 2. plan tipado (no escribe; el add-in valida tipo de parámetro, sólo lectura, permisos de edición)
            // rc1: un plan con una operación set_param por valor distinto (lote = varios valores, un solo commit/TransactionGroup).
            var groups = bl.Ids.GroupBy(i => expected(i)).ToList();
            if (groups.Any(g => g.Key == null)) return ExecResult.Fail(ErrorClass.Precondicion, "fijar_parametro_lote: hay objetivos sin valor en el mapa");
            var opsJson = groups.Select(g => (object)new Dictionary<string, object> { ["op"] = "set_param", ["ids"] = string.Join(",", g), ["name"] = param, ["value"] = g.Key }).ToList();
            var pr = _link.Call(Wire.OpPlan, new Dictionary<string, object>
            {
                [Wire.DocArg] = _docHandle, ["ops"] = opsJson,
                ["summary"] = "AgentHost " + ctx.PlanHash + " " + op.Id + " intento " + ctx.Attempt + " (" + WriteMode + ")",
            }, false);
            if (pr.Estado != Status.Ok || !(pr.Datos is Dictionary<string, object> pd)) return ExecResult.Fail(ErrorClass.Rechazo, "plan: " + pr.Estado + " " + pr.Aviso);
            var planId = Arg.GetString(pd, "plan_id");
            List<long> planned;
            try { planned = PlannedIds(pd); }
            catch (InvalidOperationException ex) { Drop(planId); return ExecResult.Fail(ErrorClass.Precondicion, ex.Message); }
            // 3. alcance: lo planificado son EXACTAMENTE los objetivos (ni uno más); si Revit omitió alguno, se dice y no se confirma
            if (planned.Except(bl.Ids).Any()) { Drop(planId); return ExecResult.Fail(ErrorClass.Precondicion, "el plan del add-in toca elementos fuera del alcance: " + string.Join(",", planned.Except(bl.Ids).Take(5))); }
            var unchanged = bl.Ids.Where(i => bl.Values[i] == expected(i)).ToList();
            var expectChanged = bl.Ids.Count - unchanged.Count;
            if (planned.Count != expectChanged)
            { Drop(planId); return ExecResult.Fail(ErrorClass.Precondicion, "el add-in planificó " + planned.Count + " cambios y se esperaban " + expectChanged + " (" + Arg.GetString(pd, "detalle") + ")"); }
            if (expectChanged == 0)
            {
                Drop(planId);
                var same = new ExecResult { Ok = true };
                same.Data["elementos"] = (long)bl.Ids.Count; same.Data["cambiados"] = 0L; same.Data["ya_estaba"] = true;
                if (WriteMode == "ensayo") { same.Data["ensayo"] = true; _trialEvidence[op.Id] = new Dictionary<string, object> { ["sin_cambios"] = true }; }
                return same;
            }

            // 4. commit (ensayo o aplicar). Desde aquí, un fallo de transporte = efecto incierto.
            var args = new Dictionary<string, object> { ["plan_id"] = planId };
            if (WriteMode == "ensayo") args["modo"] = "ensayo";
            else args[Wire.KeyArg] = op.Id + "#" + ctx.Fencing.ToString(CultureInfo.InvariantCulture);
            Faults.Hit("backend.antes_enviar", op.Id);
            var cr = _link.Call(Wire.OpCommit, args, true);
            Faults.Hit("backend.tras_commit", op.Id);   // el add-in pudo aplicar y el AgentHost cae antes de registrar
            if (Faults.Fire("backend.respuesta_perdida", op.Id))   // NRT-07: el add-in aplicó, pero la respuesta IPC se pierde
                cr = Response.Fail(cr.Id, Status.Desconocido, "simulado: respuesta del commit perdida en la tubería (el efecto real se desconoce)");
            var cd = cr.Datos as Dictionary<string, object>;
            if (WriteMode == "ensayo")
            {
                if (cr.Estado == "ensayado" && cd != null)
                {
                    var ens = cd.TryGetValue("ensayo", out var ev) ? ev as Dictionary<string, object> : null;
                    var res = new ExecResult { Ok = true };
                    res.Data["elementos"] = (long)bl.Ids.Count;
                    res.Data["cambiados"] = (long)planned.Count;
                    res.Data["ensayo"] = true;
                    res.Data["plan_id"] = planId;
                    if (ens != null) res.Data["evidencia_addin"] = ens;
                    _trialEvidence[op.Id] = ens ?? new Dictionary<string, object>();
                    return res;
                }
                if (cr.Estado == Status.Conflicto || cr.Estado == Status.Revertido || cr.Estado == Status.Invalido || cr.Estado == Status.Rechazado)
                    return ExecResult.Fail(cr.Estado == Status.Conflicto ? ErrorClass.Precondicion : ErrorClass.Rechazo, "commit ensayo: " + cr.Estado + " " + cr.Aviso);
                throw new UncertainEffectException("commit ensayo: " + cr.Estado + " " + cr.Aviso);
            }
            if (cr.Estado == Status.Ok && cd != null)
            {
                var res = new ExecResult { Ok = true };
                res.Data["elementos"] = (long)bl.Ids.Count;
                res.Data["cambiados"] = (long)planned.Count;
                res.Data["plan_id"] = planId;
                if (cd.TryGetValue("verificacion", out var vv)) res.Data["verificacion_addin"] = vv;
                return res;
            }
            if (cr.Estado == Status.Conflicto || cr.Estado == Status.Revertido || cr.Estado == Status.Invalido || cr.Estado == Status.Rechazado)
                return ExecResult.Fail(cr.Estado == Status.Conflicto ? ErrorClass.Precondicion : ErrorClass.Rechazo, "commit: " + cr.Estado + " " + cr.Aviso);
            throw new UncertainEffectException("commit: " + cr.Estado + " " + cr.Aviso);
        }

        private static List<long> PlannedIds(Dictionary<string, object> pd)
        {
            var ids = new List<long>();
            if (pd.TryGetValue("filas", out var f) && f is List<object> rows)
                foreach (var row in rows.OfType<List<object>>())
                    if (row.Count > 0 && row[0] is long l) ids.Add(l);
            var total = Arg.GetLong(pd, "cambios") ?? ids.Count;
            if (total != ids.Count) throw new InvalidOperationException("el plan devolvió " + total + " cambios pero sólo " + ids.Count + " filas (filas_omitidas); alpha3 exige ver todas");
            return ids;
        }

        private void Drop(string planId)
        {
            if (planId == null) return;
            try { _link.Call(Wire.OpJob, new Dictionary<string, object> { ["plan_id"] = planId, ["op"] = "drop" }, false); } catch (Exception) { }
        }

        /// <summary>Recuperación: qué hay en el modelo AHORA para esta operación. Sólo lectura.</summary>
        public Observation Observe(ValidatedOp op, ExecContext ctx)
        {
            if (!IsWrite(op)) return new Observation { Effect = Effect.NoObservado, Detail = "lectura: sin efecto" };
            List<(long Id, string Value)> rows;
            try { rows = Read(op, out var err); if (rows == null) return new Observation { Effect = Effect.Incierto, Detail = err }; }
            catch (Exception ex) { return new Observation { Effect = Effect.Incierto, Detail = "no se pudo leer el modelo: " + ex.Message }; }
            var expected = Expected(op);
            if (rows.Count == 0) return new Observation { Effect = Effect.SinObjetivos, Detail = "el alcance no selecciona elementos" };
            var have = rows.Count(x => x.Value == expected(x.Id));
            _baselines.TryGetValue(op.Id, out var bl);
            if (WriteMode == "ensayo")
            {
                // un ensayo nunca deja efecto: si la línea base existe y coincide, no hubo efecto persistente.
                if (bl != null && rows.All(x => bl.Values.TryGetValue(x.Id, out var b0) && b0 == x.Value)) return new Observation { Effect = Effect.NoObservado, Detail = "ensayo: el modelo coincide con la línea base" };
                if (bl == null && have == 0) return new Observation { Effect = Effect.NoObservado, Detail = "ensayo: ningún objetivo tiene el valor temporal" };
                return new Observation { Effect = Effect.Incierto, Detail = "ensayo: " + have + "/" + rows.Count + " objetivos tienen el valor temporal o difieren de la línea base" };
            }
            if (have == rows.Count) return new Observation { Effect = Effect.Observado, Detail = "todos los objetivos tienen el valor (" + rows.Count + ")" };
            if (have == 0) return new Observation { Effect = Effect.NoObservado, Detail = "ningún objetivo tiene el valor" };
            return new Observation { Effect = Effect.Parcial, Detail = have + "/" + rows.Count + " objetivos tienen el valor" };
        }

        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx)
        {
            if (op.Operation == "consultar") return new PostcheckResult { State = PostState.Satisfechas, Detail = "consulta" };
            if (op.Operation == "verificar_qc" || op.Operation == "verificar_independiente")
            {
                var q = op.Operation == "verificar_qc" ? RunQc(op, out var dev) : RunIndependent(op, out dev);
                if (!q.Ok) return new PostcheckResult { State = PostState.NoEvaluables, Detail = q.Error ?? "check sin datos" };
                var pq = new PostcheckResult { State = dev == 0 ? PostState.Satisfechas : PostState.NoSatisfechas, Detail = "desviaciones = " + dev };
                foreach (var kv in q.Data) pq.Evidence[kv.Key] = kv.Value;
                return pq;
            }
            var rows = Read(op, out var err);
            if (rows == null) return new PostcheckResult { State = PostState.NoEvaluables, Detail = err };
            var expected = Expected(op);
            var post = new PostcheckResult();
            post.Evidence["releidos"] = (long)rows.Count;
            if (WriteMode == "ensayo")
            {
                if (!_baselines.TryGetValue(op.Id, out var bl) || !_trialEvidence.TryGetValue(op.Id, out var ev))
                    return new PostcheckResult { State = PostState.NoEvaluables, Detail = "sin línea base ni evidencia del ensayo en este proceso (se reinició): no se certifica" };
                var restored = rows.Count(x => bl.Values.TryGetValue(x.Id, out var b0) && b0 == x.Value);
                post.Evidence["restaurados_relectura"] = (long)restored;
                post.Evidence["linea_base"] = bl.Ids.Select(i => (object)(i + "=" + (bl.Values[i] ?? "<null>"))).ToList();
                foreach (var kv in ev) post.Evidence["addin_" + kv.Key] = kv.Value;
                var sinCambios = ev.ContainsKey("sin_cambios");
                var grupo = ev.TryGetValue("grupo_estado", out var g) ? Convert.ToString(g, CultureInfo.InvariantCulture) : null;
                var tempOk = ev.TryGetValue("temporal_ok", out var t) && t is bool tb && tb;
                var restOk = ev.TryGetValue("restauracion_ok", out var ro) && ro is bool rb && rb;
                var ok = restored == rows.Count && rows.Count == bl.Ids.Count && (sinCambios || (grupo == "RolledBack" && tempOk && restOk));
                post.State = ok ? PostState.Satisfechas : PostState.NoSatisfechas;
                post.Detail = ok ? (sinCambios ? "sin cambios necesarios; modelo intacto" : "temporal verificado por el add-in, grupo " + grupo + ", relectura independiente = línea base (" + restored + "/" + rows.Count + ")")
                                 : "grupo " + (grupo ?? "?") + ", temporal_ok " + tempOk + ", restauración add-in " + restOk + ", relectura " + restored + "/" + rows.Count;
                return post;
            }
            var have = rows.Count(x => x.Value == expected(x.Id));
            post.Evidence["con_valor"] = (long)have;
            post.State = have == rows.Count ? PostState.Satisfechas : PostState.NoSatisfechas;
            post.Detail = have + "/" + rows.Count + " objetivos con " + op.Param("parametro") + " = valor esperado";
            return post;
        }

        public void Dispose() => _link.Dispose();
    }
}
