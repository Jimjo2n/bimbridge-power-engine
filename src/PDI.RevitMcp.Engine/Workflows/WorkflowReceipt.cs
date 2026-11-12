// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>
    /// 0.9.1 (Bloque F, #23 CM-01/03): estados de un flujo nativo interactivo (Copy/Monitor y similares). Cada transición exige
    /// evidencia observable; ni el tiempo transcurrido ni que PostCommand se aceptara bastan para avanzar. "Terminado" no existe como
    /// atajo: el final válido es <see cref="WorkflowState.save_confirmed"/> o <see cref="WorkflowState.verification_passed"/>.
    /// </summary>
    public enum WorkflowState
    {
        preflight,
        command_accepted,
        native_mode_observed,
        awaiting_link_selection,
        awaiting_mapping_dialog,
        copying,
        awaiting_finish,
        finished,
        verification_passed,
        verification_failed,
        save_confirmed,
        manual_handoff,
        cancelled,
        reconciliation_required,
    }

    /// <summary>Nivel de prueba de una evidencia (#23 §6).</summary>
    public enum ProofLevel { observed, inferred, verified }

    /// <summary>Tipos de evidencia que el motor reconoce. Cada uno sólo puede producirlo quien lo observa (add-in, ejecutor de UI, verificador).</summary>
    public static class EvidenceKind
    {
        public const string PreflightPassed = "preflight_ok";
        public const string PostCommandAccepted = "postcommand_aceptado";
        public const string CommandActive = "comando_activo";           // el add-in observa el modo nativo (p. ej. evento de comando / UI)
        public const string LinkSelected = "vinculo_seleccionado";
        public const string MappingDialog = "dialogo_mapeo";
        public const string CopyStarted = "copia_iniciada";
        public const string CopyObserved = "copia_observada";           // elementos nuevos aparecidos en el anfitrión
        public const string FinishObserved = "finalizar_observado";
        public const string VerificationPassed = "verificacion_ok";
        public const string VerificationFailed = "verificacion_fallida";
        public const string SaveConfirmed = "guardado_confirmado";       // relectura tras guardar (no sólo la orden de guardar)
        public const string Cancelled = "cancelado";
        public const string Uncertain = "incierto";                      // desconexión, diálogo modal, reinicio, tiempo agotado
        public const string NoUiExecutor = "sin_ejecutor_ui";
        public const string Reconciled = "reconciliado";

        public static readonly string[] All =
        {
            PreflightPassed, PostCommandAccepted, CommandActive, LinkSelected, MappingDialog, CopyStarted, CopyObserved, FinishObserved,
            VerificationPassed, VerificationFailed, SaveConfirmed, Cancelled, Uncertain, NoUiExecutor, Reconciled,
        };
    }

    public sealed class Evidence
    {
        public string Kind;
        public ProofLevel Level;
        public string Source;     // addin | ui_executor | verifier | client | manual
        public string Detail;
        public DateTime AtUtc;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["tipo"] = Kind, ["nivel"] = Level.ToString(), ["fuente"] = Source, ["detalle"] = Detail ?? "",
            ["en"] = AtUtc.ToString("o", CultureInfo.InvariantCulture),
        };

        public static Evidence FromJson(Dictionary<string, object> d) => new Evidence
        {
            Kind = Arg.GetString(d, "tipo"),
            Level = (ProofLevel)Enum.Parse(typeof(ProofLevel), Arg.GetString(d, "nivel") ?? "observed"),
            Source = Arg.GetString(d, "fuente"),
            Detail = Arg.GetString(d, "detalle"),
            AtUtc = DateTime.Parse(Arg.GetString(d, "en"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    /// <summary>Correlación completa de un intento (#23 §3, #25): sin esto no se puede reconciliar ni evitar efectos duplicados.</summary>
    public sealed class WorkflowCorrelation
    {
        public string AgentId, SessionId, RequestId, TraceId, JobId;
        public string DocumentIdentity, LinkIdentity;
        public string RevitVersion, RevitBuild, ProductBuild;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["agent_id"] = AgentId, ["session_id"] = SessionId, ["request_id"] = RequestId, ["trace_id"] = TraceId, ["job_id"] = JobId,
            ["documento"] = DocumentIdentity, ["vinculo"] = LinkIdentity, ["revit"] = RevitVersion, ["revit_build"] = RevitBuild, ["build"] = ProductBuild,
        };

        public static WorkflowCorrelation FromJson(Dictionary<string, object> d) => new WorkflowCorrelation
        {
            AgentId = Arg.GetString(d, "agent_id"), SessionId = Arg.GetString(d, "session_id"), RequestId = Arg.GetString(d, "request_id"),
            TraceId = Arg.GetString(d, "trace_id"), JobId = Arg.GetString(d, "job_id"), DocumentIdentity = Arg.GetString(d, "documento"),
            LinkIdentity = Arg.GetString(d, "vinculo"), RevitVersion = Arg.GetString(d, "revit"), RevitBuild = Arg.GetString(d, "revit_build"),
            ProductBuild = Arg.GetString(d, "build"),
        };
    }

    public sealed class WorkflowStep
    {
        public WorkflowState From, To;
        public Evidence Evidence;
    }

    /// <summary>Recibo persistente de un intento. La clave de intento es la idempotencia: el mismo trabajo nunca abre dos recibos.</summary>
    public sealed class WorkflowReceipt
    {
        public const string Schema = "bimbridge.workflow_receipt/1";
        public string Workflow;           // p. ej. copy_monitor_fixtures
        public string AttemptKey;
        public WorkflowCorrelation Correlation = new WorkflowCorrelation();
        public WorkflowState State = WorkflowState.preflight;
        public List<WorkflowStep> History = new List<WorkflowStep>();
        public DateTime CreatedUtc;
        public string Note;

        /// <summary>El mejor nivel de prueba alcanzado (verified sólo si una verificación o un guardado confirmado lo respaldan).</summary>
        public ProofLevel Proof => History.Count == 0 ? ProofLevel.inferred : History.Max(h => h.Evidence.Level);

        public bool IsTerminal => WorkflowMachine.Terminal.Contains(State);

        /// <summary>¿Hay evidencia de que ya pudo haber efectos en el modelo? Si sí, nunca se repite la orden a ciegas.</summary>
        public bool MayHaveEffects => History.Any(h => EffectStates.Contains(h.To) || h.Evidence.Kind == EvidenceKind.CopyStarted || h.Evidence.Kind == EvidenceKind.CopyObserved);

        private static readonly HashSet<WorkflowState> EffectStates = new HashSet<WorkflowState>
        {
            WorkflowState.copying, WorkflowState.awaiting_finish, WorkflowState.finished, WorkflowState.verification_passed,
            WorkflowState.verification_failed, WorkflowState.save_confirmed,
        };

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["esquema"] = Schema, ["flujo"] = Workflow, ["clave_intento"] = AttemptKey, ["estado"] = State.ToString(),
            ["prueba"] = Proof.ToString(), ["puede_tener_efectos"] = MayHaveEffects, ["correlacion"] = Correlation.ToJson(),
            ["creado"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["nota"] = Note ?? "",
            ["historial"] = History.Select(h => (object)new Dictionary<string, object> { ["de"] = h.From.ToString(), ["a"] = h.To.ToString(), ["evidencia"] = h.Evidence.ToJson() }).ToList(),
        };

        public static WorkflowReceipt FromJson(Dictionary<string, object> d)
        {
            if (Arg.GetString(d, "esquema") != Schema) throw new InvalidDataException("recibo con esquema desconocido");
            var r = new WorkflowReceipt
            {
                Workflow = Arg.GetString(d, "flujo"),
                AttemptKey = Arg.GetString(d, "clave_intento"),
                State = (WorkflowState)Enum.Parse(typeof(WorkflowState), Arg.GetString(d, "estado")),
                Correlation = WorkflowCorrelation.FromJson((Dictionary<string, object>)d["correlacion"]),
                CreatedUtc = DateTime.Parse(Arg.GetString(d, "creado"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Note = Arg.GetString(d, "nota"),
            };
            foreach (Dictionary<string, object> h in (List<object>)d["historial"])
                r.History.Add(new WorkflowStep
                {
                    From = (WorkflowState)Enum.Parse(typeof(WorkflowState), Arg.GetString(h, "de")),
                    To = (WorkflowState)Enum.Parse(typeof(WorkflowState), Arg.GetString(h, "a")),
                    Evidence = Evidence.FromJson((Dictionary<string, object>)h["evidencia"]),
                });
            return r;
        }

        /// <summary>Clave de intento estable a partir de lo que define el trabajo (flujo, documento, vínculo y la petición del cliente).</summary>
        public static string KeyFor(string workflow, string documentIdentity, string linkIdentity, string clientKey)
        {
            using (var sha = SHA256.Create())
            {
                var raw = string.Join("\n", workflow ?? "", documentIdentity ?? "", linkIdentity ?? "", clientKey ?? "");
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").Substring(0, 32).ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Máquina de estados de los flujos nativos. La única forma de cambiar de estado es <see cref="Apply"/> con una evidencia cuyo tipo
    /// está permitido desde el estado actual. Lo incierto (desconexión, diálogo, reinicio, tiempo agotado) lleva siempre a
    /// reconciliation_required; desde ahí sólo se sale con una reconciliación que diga qué se observó.
    /// </summary>
    public static class WorkflowMachine
    {
        public static readonly HashSet<WorkflowState> Terminal = new HashSet<WorkflowState>
        {
            // manual_handoff y verification_failed NO son terminales (AUD-08): la verificación continúa tras el traspaso o una corrección.
            WorkflowState.save_confirmed, WorkflowState.cancelled,
        };

        private static readonly Dictionary<(WorkflowState, string), WorkflowState> Edges = new Dictionary<(WorkflowState, string), WorkflowState>
        {
            [(WorkflowState.preflight, EvidenceKind.PostCommandAccepted)] = WorkflowState.command_accepted,
            [(WorkflowState.preflight, EvidenceKind.NoUiExecutor)] = WorkflowState.manual_handoff,
            [(WorkflowState.command_accepted, EvidenceKind.CommandActive)] = WorkflowState.native_mode_observed,
            [(WorkflowState.native_mode_observed, EvidenceKind.CommandActive)] = WorkflowState.awaiting_link_selection,
            [(WorkflowState.native_mode_observed, EvidenceKind.LinkSelected)] = WorkflowState.awaiting_mapping_dialog,
            [(WorkflowState.awaiting_link_selection, EvidenceKind.LinkSelected)] = WorkflowState.awaiting_mapping_dialog,
            [(WorkflowState.awaiting_mapping_dialog, EvidenceKind.MappingDialog)] = WorkflowState.awaiting_mapping_dialog,
            [(WorkflowState.awaiting_mapping_dialog, EvidenceKind.CopyStarted)] = WorkflowState.copying,
            [(WorkflowState.copying, EvidenceKind.CopyObserved)] = WorkflowState.awaiting_finish,
            [(WorkflowState.awaiting_finish, EvidenceKind.FinishObserved)] = WorkflowState.finished,
            [(WorkflowState.finished, EvidenceKind.VerificationPassed)] = WorkflowState.verification_passed,
            [(WorkflowState.finished, EvidenceKind.VerificationFailed)] = WorkflowState.verification_failed,
            [(WorkflowState.verification_passed, EvidenceKind.SaveConfirmed)] = WorkflowState.save_confirmed,
            // AUD-07/08: el preflight observado no cambia de estado; tras un traspaso manual la verificación continúa
            // (el usuario hizo la copia y Finalizar en Revit; aquí sólo se puede verificar el resultado, no observar los pasos).
            [(WorkflowState.preflight, EvidenceKind.PreflightPassed)] = WorkflowState.preflight,
            [(WorkflowState.manual_handoff, EvidenceKind.VerificationPassed)] = WorkflowState.verification_passed,
            [(WorkflowState.manual_handoff, EvidenceKind.VerificationFailed)] = WorkflowState.verification_failed,
            // Re-verificar tras corregir: un fallo no es definitivo, pero nunca salta a guardado.
            [(WorkflowState.verification_failed, EvidenceKind.VerificationPassed)] = WorkflowState.verification_passed,
            [(WorkflowState.verification_failed, EvidenceKind.VerificationFailed)] = WorkflowState.verification_failed,
        };

        /// <summary>AUD-07: evidencias privilegiadas que sólo pueden aportar componentes autorizados (verificador o add-in), nunca el cliente.</summary>
        public static readonly Dictionary<string, string[]> PrivilegedSources = new Dictionary<string, string[]>
        {
            [EvidenceKind.VerificationPassed] = new[] { "verifier" },
            [EvidenceKind.VerificationFailed] = new[] { "verifier" },
            [EvidenceKind.SaveConfirmed] = new[] { "verifier", "addin" },
            [EvidenceKind.CopyObserved] = new[] { "verifier", "addin", "ui_executor" },
            [EvidenceKind.FinishObserved] = new[] { "addin", "ui_executor" },
            [EvidenceKind.CommandActive] = new[] { "addin", "ui_executor" },
        };

        /// <summary>Evidencias que exigen un nivel mínimo de prueba (no basta con "inferido").</summary>
        private static readonly Dictionary<string, ProofLevel> MinLevel = new Dictionary<string, ProofLevel>
        {
            [EvidenceKind.CopyObserved] = ProofLevel.observed,
            [EvidenceKind.FinishObserved] = ProofLevel.observed,
            [EvidenceKind.VerificationPassed] = ProofLevel.verified,
            [EvidenceKind.VerificationFailed] = ProofLevel.verified,
            [EvidenceKind.SaveConfirmed] = ProofLevel.verified,
        };

        /// <summary>Estado al que llevaría la evidencia, o null si no está permitida desde el estado actual.</summary>
        public static WorkflowState? Next(WorkflowReceipt r, Evidence e, out string refusal)
        {
            refusal = null;
            if (r.IsTerminal) { refusal = "el intento ya terminó en " + r.State; return null; }
            if (e == null || Array.IndexOf(EvidenceKind.All, e.Kind) < 0) { refusal = "evidencia desconocida"; return null; }
            if (MinLevel.TryGetValue(e.Kind, out var min) && e.Level < min) { refusal = e.Kind + " exige nivel " + min + " (llegó " + e.Level + ")"; return null; }
            if (PrivilegedSources.TryGetValue(e.Kind, out var allowed) && Array.IndexOf(allowed, e.Source ?? "") < 0)
            { refusal = e.Kind + " sólo lo puede aportar " + string.Join(" o ", allowed) + " (llegó de '" + e.Source + "')"; return null; }
            if (e.Kind == EvidenceKind.Uncertain) return WorkflowState.reconciliation_required;
            if (e.Kind == EvidenceKind.Cancelled)
            {
                // Cancelar después de que pudo haber copia no borra los efectos: queda pendiente de reconciliar.
                return r.MayHaveEffects ? WorkflowState.reconciliation_required : WorkflowState.cancelled;
            }
            if (r.State == WorkflowState.reconciliation_required)
            {
                if (e.Kind != EvidenceKind.Reconciled) { refusal = "pendiente de reconciliar: sólo se admite una reconciliación con lo observado"; return null; }
                return ReconciledState(e.Detail, r, out refusal);
            }
            if (Edges.TryGetValue((r.State, e.Kind), out var to)) return to;
            refusal = "transición no permitida: " + r.State + " + " + e.Kind;
            return null;
        }

        /// <summary>Una reconciliación declara el estado observado ("detalle" = nombre del estado). Nunca puede saltar a guardado o verificado sin su evidencia.</summary>
        private static WorkflowState? ReconciledState(string detail, WorkflowReceipt r, out string refusal)
        {
            refusal = null;
            if (!Enum.TryParse(detail ?? "", out WorkflowState s)) { refusal = "reconciliado: detalle debe ser el estado observado"; return null; }
            if (s == WorkflowState.save_confirmed || s == WorkflowState.verification_passed || s == WorkflowState.reconciliation_required)
            { refusal = "una reconciliación no puede declarar " + s + ": aporte la verificación o el guardado como evidencia propia"; return null; }
            if (s == WorkflowState.preflight && r.MayHaveEffects) { refusal = "ya pudo haber copia: no se vuelve a preflight; reconcilie a finished o awaiting_finish"; return null; }
            return s;
        }

        public static WorkflowReceipt Apply(WorkflowReceipt r, Evidence e)
        {
            var to = Next(r, e, out var refusal);
            if (to == null) throw new InvalidOperationException(refusal);
            r.History.Add(new WorkflowStep { From = r.State, To = to.Value, Evidence = e });
            r.State = to.Value;
            return r;
        }

        /// <summary>¿Se puede (re)lanzar la orden nativa? Sólo desde preflight y sin indicios de efectos (#23: nunca re-PostCommand a ciegas).</summary>
        public static bool CanInvoke(WorkflowReceipt r, out string why)
        {
            why = null;
            if (r.State != WorkflowState.preflight) { why = "el intento está en " + r.State + "; no se repite la orden nativa (reconcilie o abra un intento nuevo)"; return false; }
            if (r.MayHaveEffects) { why = "hay indicios de copia previa; reconcilie antes de repetir"; return false; }
            return true;
        }
    }

    /// <summary>
    /// Recibos persistidos por clave de intento (un archivo por intento, escritura atómica). Abrir el mismo intento dos veces devuelve
    /// el recibo existente: así un reintento del cliente, una desconexión o un reinicio no duplican efectos.
    /// </summary>
    public sealed class ReceiptStore
    {
        private readonly string _dir;
        private readonly object _gate = new object();

        public ReceiptStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

        private string PathFor(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_'))) throw new ArgumentException("clave de intento no válida");
            return Path.Combine(_dir, key + ".json");
        }

        public WorkflowReceipt Load(string key)
        {
            var p = PathFor(key);
            return File.Exists(p) ? WorkflowReceipt.FromJson(Json.ParseObject(File.ReadAllText(p, Encoding.UTF8))) : null;
        }

        /// <summary>Espera máxima por el bloqueo de un intento (otro proceso escribiendo el mismo recibo).</summary>
        public TimeSpan LockWait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// AUD-07: exclusión entre procesos por intento (archivo &lt;clave&gt;.lock abierto sin compartir). Leer-aplicar-guardar ocurre
        /// entero bajo el bloqueo, así dos procesos no pueden pisar transiciones. Si no se obtiene a tiempo, falla cerrado.
        /// </summary>
        private T WithKeyLock<T>(string key, Func<T> body)
        {
            var lockPath = PathFor(key) + ".lock";
            var deadline = DateTime.UtcNow + LockWait;
            lock (_gate)
            {
                while (true)
                {
                    FileStream fs = null;
                    try { fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) when (DateTime.UtcNow < deadline) { System.Threading.Thread.Sleep(15); continue; }
                    catch (IOException ex) { throw new TimeoutException("el recibo " + key + " está bloqueado por otro proceso: " + ex.Message); }
                    using (fs) return body();
                }
            }
        }

        /// <summary>Abre (o recupera) el intento. <paramref name="created"/> = false si ya existía.</summary>
        public WorkflowReceipt Begin(string workflow, string key, WorkflowCorrelation corr, out bool created)
        {
            var isNew = false;
            var res = WithKeyLock(key, () =>
            {
                var existing = Load(key);
                if (existing != null)
                {
                    if (existing.Workflow != workflow) throw new InvalidOperationException("la clave de intento pertenece a otro flujo (" + existing.Workflow + ")");
                    if (!string.Equals(existing.Correlation.DocumentIdentity, corr?.DocumentIdentity, StringComparison.Ordinal))
                        throw new InvalidOperationException("la clave de intento pertenece a otro documento: cambio de destino rechazado");
                    return existing;
                }
                isNew = true;
                var r = new WorkflowReceipt { Workflow = workflow, AttemptKey = key, Correlation = corr ?? new WorkflowCorrelation(), CreatedUtc = DateTime.UtcNow };
                Save(r);
                return r;
            });
            created = isNew;
            return res;
        }

        public WorkflowReceipt Record(string key, Evidence e) => WithKeyLock(key, () =>
        {
            var r = Load(key) ?? throw new InvalidOperationException("no existe el intento " + key);
            WorkflowMachine.Apply(r, e);
            Save(r);
            return r;
        });

        // ------------------------------------------------------------------ AUD-12: inventario inicial por intento

        /// <summary>Inventario inicial (ids del anfitrión antes de copiar) de un intento. Es evidencia: se escribe una vez y no se sobrescribe.</summary>
        public sealed class Baseline
        {
            public const string Schema = "bimbridge.workflow_baseline/1";
            public string AttemptKey, DocumentIdentity, LinkIdentity, Sha256;
            public List<long> Ids = new List<long>();
            public DateTime CreatedUtc;

            public static string Hash(string key, string doc, string link, IEnumerable<long> ids) =>
                Home.Sha256Hex(key + "\n" + doc + "\n" + link + "\n" + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }

        private string BaselinePath(string key) => Path.ChangeExtension(PathFor(key), ".inv");

        /// <summary>
        /// Guarda el inventario inicial del intento SOLO si no existe. Un preflight repetido, otra sesión u otro proceso reciben el
        /// inventario original (<paramref name="created"/> = false): la evidencia inicial nunca se pisa. Si la clave pertenece a otro
        /// documento o vínculo, se rechaza.
        /// </summary>
        public Baseline SaveBaselineOnce(string key, string documentIdentity, string linkIdentity, IEnumerable<long> ids, out bool created)
        {
            var isNew = false;
            var res = WithKeyLock(key, () =>
            {
                var existing = LoadBaselineUnlocked(key);
                if (existing != null)
                {
                    if (!string.Equals(existing.DocumentIdentity, documentIdentity, StringComparison.Ordinal) || !string.Equals(existing.LinkIdentity, linkIdentity, StringComparison.Ordinal))
                        throw new InvalidOperationException("el inventario inicial de este intento pertenece a otro documento o vínculo: cambio de destino rechazado");
                    return existing;
                }
                var list = ids.Distinct().OrderBy(x => x).ToList();
                var b = new Baseline { AttemptKey = key, DocumentIdentity = documentIdentity, LinkIdentity = linkIdentity, Ids = list, CreatedUtc = DateTime.UtcNow };
                b.Sha256 = Baseline.Hash(key, documentIdentity, linkIdentity, list);
                var json = Json.Write(new Dictionary<string, object>
                {
                    ["esquema"] = Baseline.Schema, ["clave_intento"] = key, ["documento"] = documentIdentity, ["vinculo"] = linkIdentity,
                    ["ids"] = list.Cast<object>().ToList(), ["sha256"] = b.Sha256, ["creado"] = b.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
                });
                var p = BaselinePath(key);
                var tmp = p + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                File.Move(tmp, p);   // falla si ya existe: nunca sobrescribe
                isNew = true;
                return b;
            });
            created = isNew;
            return res;
        }

        /// <summary>Inventario inicial del intento, o null si no hay. Un archivo alterado o ilegible lanza InvalidDataException (no se usa).</summary>
        public Baseline LoadBaseline(string key) => WithKeyLock(key, () => LoadBaselineUnlocked(key));

        private Baseline LoadBaselineUnlocked(string key)
        {
            var p = BaselinePath(key);
            if (!File.Exists(p)) return null;
            try
            {
                var d = Json.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                if (Arg.GetString(d, "esquema") != Baseline.Schema) throw new InvalidDataException("inventario inicial con esquema desconocido");
                var b = new Baseline
                {
                    AttemptKey = Arg.GetString(d, "clave_intento"), DocumentIdentity = Arg.GetString(d, "documento"), LinkIdentity = Arg.GetString(d, "vinculo"),
                    Sha256 = Arg.GetString(d, "sha256"), Ids = ((List<object>)d["ids"]).Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList(),
                    CreatedUtc = DateTime.Parse(Arg.GetString(d, "creado"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                };
                if (b.AttemptKey != key || b.Sha256 != Baseline.Hash(key, b.DocumentIdentity, b.LinkIdentity, b.Ids))
                    throw new InvalidDataException("el inventario inicial del intento " + key + " no coincide con su huella (alterado o dañado)");
                return b;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is KeyNotFoundException || ex is ArgumentException || ex is OverflowException)
            {
                throw new InvalidDataException("el inventario inicial del intento " + key + " es ilegible: " + ex.GetType().Name);
            }
        }

        private void Save(WorkflowReceipt r)
        {
            var p = PathFor(r.AttemptKey);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, Json.Write(r.ToJson()), new UTF8Encoding(false));
            if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
        }

        /// <summary>Intentos sin terminar (para reconciliar al arrancar o tras una desconexión).</summary>
        /// <param name="unreadable">Recibos que no se pudieron leer: se informan (nunca se descartan en silencio) y bloquean su clave.</param>
        public List<WorkflowReceipt> Open(out List<string> unreadable)
        {
            var open = new List<WorkflowReceipt>();
            unreadable = new List<string>();
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                WorkflowReceipt r;
                try { r = WorkflowReceipt.FromJson(Json.ParseObject(File.ReadAllText(f, Encoding.UTF8))); }
                catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is ArgumentException || ex is InvalidCastException || ex is KeyNotFoundException || ex is InvalidOperationException)
                {
                    unreadable.Add(Path.GetFileNameWithoutExtension(f) + ": " + ex.GetType().Name);
                    continue;
                }
                if (!r.IsTerminal) open.Add(r);
            }
            return open;
        }
    }
}
