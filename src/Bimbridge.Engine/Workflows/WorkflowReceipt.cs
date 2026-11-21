// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>
    /// 0.9.1 (Block F, #23 CM-01/03): states of an interactive native workflow (Copy/Monitor and similar). Each transition requires
    /// observable evidence; neither elapsed time nor PostCommand being accepted is enough to advance. "Done" does not exist as a
    /// shortcut: the valid end is <see cref="WorkflowState.save_confirmed"/> or <see cref="WorkflowState.verification_passed"/>.
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

    /// <summary>Proof level of a piece of evidence (#23 §6).</summary>
    public enum ProofLevel { observed, inferred, verified }

    /// <summary>Evidence kinds the engine recognizes. Each can only be produced by whoever observes it (add-in, UI executor, verifier).</summary>
    public static class EvidenceKind
    {
        public const string PreflightPassed = "preflight_ok";
        public const string PostCommandAccepted = "postcommand_aceptado";
        public const string CommandActive = "comando_activo";           // the add-in observes the native mode (e.g. command / UI event)
        public const string LinkSelected = "vinculo_seleccionado";
        public const string MappingDialog = "dialogo_mapeo";
        public const string CopyStarted = "copia_iniciada";
        public const string CopyObserved = "copia_observada";           // new elements appeared in the host
        public const string FinishObserved = "finalizar_observado";
        public const string VerificationPassed = "verificacion_ok";
        public const string VerificationFailed = "verificacion_fallida";
        public const string SaveConfirmed = "guardado_confirmado";       // reread after saving (not just the save command)
        public const string Cancelled = "cancelado";
        public const string Uncertain = "incierto";                      // disconnection, modal dialog, restart, timeout
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

    /// <summary>Full correlation of an attempt (#23 §3, #25): without it there is no way to reconcile or avoid duplicate effects.</summary>
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

    /// <summary>Persistent receipt of an attempt. The attempt key is the idempotency key: the same job never opens two receipts.</summary>
    public sealed class WorkflowReceipt
    {
        public const string Schema = "bimbridge.workflow_receipt/1";
        public string Workflow;           // e.g. copy_monitor_fixtures
        public string AttemptKey;
        public WorkflowCorrelation Correlation = new WorkflowCorrelation();
        public WorkflowState State = WorkflowState.preflight;
        public List<WorkflowStep> History = new List<WorkflowStep>();
        public DateTime CreatedUtc;
        public string Note;

        /// <summary>The best proof level reached (verified only if backed by a verification or a confirmed save).</summary>
        public ProofLevel Proof => History.Count == 0 ? ProofLevel.inferred : History.Max(h => h.Evidence.Level);

        public bool IsTerminal => WorkflowMachine.Terminal.Contains(State);

        /// <summary>Is there evidence that the model may already have effects? If so, the command is never repeated blindly.</summary>
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
            if (Arg.GetString(d, "esquema") != Schema) throw new InvalidDataException("receipt with unknown schema");
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

        /// <summary>Stable attempt key derived from what defines the job (workflow, document, link and the client request).</summary>
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
    /// State machine of the native workflows. The only way to change state is <see cref="Apply"/> with evidence whose kind
    /// is allowed from the current state. Anything uncertain (disconnection, dialog, restart, timeout) always leads to
    /// reconciliation_required; the only way out is a reconciliation that states what was observed.
    /// </summary>
    public static class WorkflowMachine
    {
        public static readonly HashSet<WorkflowState> Terminal = new HashSet<WorkflowState>
        {
            // manual_handoff and verification_failed are NOT terminal (AUD-08): verification continues after the handoff or a correction.
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
            // AUD-07/08: an observed preflight does not change state; after a manual handoff verification continues
            // (the user did the copy and Finish in Revit; here only the result can be verified, not the steps observed).
            [(WorkflowState.preflight, EvidenceKind.PreflightPassed)] = WorkflowState.preflight,
            [(WorkflowState.manual_handoff, EvidenceKind.VerificationPassed)] = WorkflowState.verification_passed,
            [(WorkflowState.manual_handoff, EvidenceKind.VerificationFailed)] = WorkflowState.verification_failed,
            // Re-verify after correcting: a failure is not final, but it never jumps to saved.
            [(WorkflowState.verification_failed, EvidenceKind.VerificationPassed)] = WorkflowState.verification_passed,
            [(WorkflowState.verification_failed, EvidenceKind.VerificationFailed)] = WorkflowState.verification_failed,
        };

        /// <summary>AUD-07: privileged evidence that only authorized components (verifier or add-in) can supply, never the client.</summary>
        public static readonly Dictionary<string, string[]> PrivilegedSources = new Dictionary<string, string[]>
        {
            [EvidenceKind.VerificationPassed] = new[] { "verifier" },
            [EvidenceKind.VerificationFailed] = new[] { "verifier" },
            [EvidenceKind.SaveConfirmed] = new[] { "verifier", "addin" },
            [EvidenceKind.CopyObserved] = new[] { "verifier", "addin", "ui_executor" },
            [EvidenceKind.FinishObserved] = new[] { "addin", "ui_executor" },
            [EvidenceKind.CommandActive] = new[] { "addin", "ui_executor" },
        };

        /// <summary>Evidence that requires a minimum proof level ("inferred" is not enough).</summary>
        private static readonly Dictionary<string, ProofLevel> MinLevel = new Dictionary<string, ProofLevel>
        {
            [EvidenceKind.CopyObserved] = ProofLevel.observed,
            [EvidenceKind.FinishObserved] = ProofLevel.observed,
            [EvidenceKind.VerificationPassed] = ProofLevel.verified,
            [EvidenceKind.VerificationFailed] = ProofLevel.verified,
            [EvidenceKind.SaveConfirmed] = ProofLevel.verified,
        };

        /// <summary>State the evidence would lead to, or null if it is not allowed from the current state.</summary>
        public static WorkflowState? Next(WorkflowReceipt r, Evidence e, out string refusal)
        {
            refusal = null;
            if (r.IsTerminal) { refusal = "the attempt already ended in " + r.State; return null; }
            if (e == null || Array.IndexOf(EvidenceKind.All, e.Kind) < 0) { refusal = "unknown evidence"; return null; }
            if (MinLevel.TryGetValue(e.Kind, out var min) && e.Level < min) { refusal = e.Kind + " requires level " + min + " (got " + e.Level + ")"; return null; }
            if (PrivilegedSources.TryGetValue(e.Kind, out var allowed) && Array.IndexOf(allowed, e.Source ?? "") < 0)
            { refusal = e.Kind + " can only be supplied by " + string.Join(" or ", allowed) + " (came from '" + e.Source + "')"; return null; }
            if (e.Kind == EvidenceKind.Uncertain) return WorkflowState.reconciliation_required;
            if (e.Kind == EvidenceKind.Cancelled)
            {
                // Cancelling after a copy may have happened does not erase the effects: it remains pending reconciliation.
                return r.MayHaveEffects ? WorkflowState.reconciliation_required : WorkflowState.cancelled;
            }
            if (r.State == WorkflowState.reconciliation_required)
            {
                if (e.Kind != EvidenceKind.Reconciled) { refusal = "pending reconciliation: only a reconciliation with what was observed is accepted"; return null; }
                return ReconciledState(e.Detail, r, out refusal);
            }
            if (Edges.TryGetValue((r.State, e.Kind), out var to)) return to;
            refusal = "transition not allowed: " + r.State + " + " + e.Kind;
            return null;
        }

        /// <summary>A reconciliation declares the observed state ("detalle" = state name). It can never jump to saved or verified without its own evidence.</summary>
        private static WorkflowState? ReconciledState(string detail, WorkflowReceipt r, out string refusal)
        {
            refusal = null;
            if (!Enum.TryParse(detail ?? "", out WorkflowState s)) { refusal = "reconciliado: detalle must be the observed state"; return null; }
            if (s == WorkflowState.save_confirmed || s == WorkflowState.verification_passed || s == WorkflowState.reconciliation_required)
            { refusal = "a reconciliation cannot declare " + s + ": supply the verification or the save as its own evidence"; return null; }
            if (s == WorkflowState.preflight && r.MayHaveEffects) { refusal = "a copy may already have happened: no return to preflight; reconcile to finished or awaiting_finish"; return null; }
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

        /// <summary>Can the native command be (re)launched? Only from preflight and with no sign of effects (#23: never re-PostCommand blindly).</summary>
        public static bool CanInvoke(WorkflowReceipt r, out string why)
        {
            why = null;
            if (r.State != WorkflowState.preflight) { why = "the attempt is in " + r.State + "; the native command is not repeated (reconcile or open a new attempt)"; return false; }
            if (r.MayHaveEffects) { why = "there are signs of a previous copy; reconcile before repeating"; return false; }
            return true;
        }
    }

    /// <summary>
    /// Receipts persisted by attempt key (one file per attempt, atomic write). Opening the same attempt twice returns
    /// the existing receipt: so a client retry, a disconnection or a restart does not duplicate effects.
    /// </summary>
    public sealed class ReceiptStore
    {
        private readonly string _dir;
        private readonly object _gate = new object();

        public ReceiptStore(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

        private string PathFor(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_'))) throw new ArgumentException("invalid attempt key");
            return Path.Combine(_dir, key + ".json");
        }

        public WorkflowReceipt Load(string key)
        {
            var p = PathFor(key);
            return File.Exists(p) ? WorkflowReceipt.FromJson(Json.ParseObject(File.ReadAllText(p, Encoding.UTF8))) : null;
        }

        /// <summary>Maximum wait for an attempt's lock (another process writing the same receipt).</summary>
        public TimeSpan LockWait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// AUD-07: cross-process exclusion per attempt (file &lt;key&gt;.lock opened without sharing). Read-apply-save happens
        /// entirely under the lock, so two processes cannot overwrite each other's transitions. If it is not acquired in time, it fails closed.
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
                    catch (IOException ex) { throw new TimeoutException("receipt " + key + " is locked by another process: " + ex.Message); }
                    using (fs) return body();
                }
            }
        }

        /// <summary>Opens (or recovers) the attempt. <paramref name="created"/> = false if it already existed.</summary>
        public WorkflowReceipt Begin(string workflow, string key, WorkflowCorrelation corr, out bool created)
        {
            var isNew = false;
            var res = WithKeyLock(key, () =>
            {
                var existing = Load(key);
                if (existing != null)
                {
                    if (existing.Workflow != workflow) throw new InvalidOperationException("the attempt key belongs to another workflow (" + existing.Workflow + ")");
                    if (!string.Equals(existing.Correlation.DocumentIdentity, corr?.DocumentIdentity, StringComparison.Ordinal))
                        throw new InvalidOperationException("the attempt key belongs to another document: target change refused");
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
            var r = Load(key) ?? throw new InvalidOperationException("attempt " + key + " does not exist");
            WorkflowMachine.Apply(r, e);
            Save(r);
            return r;
        });

        // ------------------------------------------------------------------ AUD-12: baseline inventory per attempt

        /// <summary>Baseline inventory (host ids before copying) of an attempt. It is evidence: written once and never overwritten.</summary>
        public sealed class Baseline
        {
            public const string Schema = "bimbridge.workflow_baseline/1";
            public string AttemptKey, DocumentIdentity, LinkIdentity, Sha256;
            public List<long> Ids = new List<long>();
            public DateTime CreatedUtc;
            /// <summary>AUD-20: frozen expectations of the attempt (canonical JSON text; interpreted by the workflow). Included in the fingerprint.</summary>
            public string Expectations;

            public static string Hash(string key, string doc, string link, IEnumerable<long> ids, string expectations = null) =>
                Home.Sha256Hex(key + "\n" + doc + "\n" + link + "\n" + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                               (string.IsNullOrEmpty(expectations) ? "" : "\n" + expectations));
        }

        private string BaselinePath(string key) => Path.ChangeExtension(PathFor(key), ".inv");

        /// <summary>
        /// Saves the attempt's baseline inventory ONLY if it does not exist. A repeated preflight, another session or another process gets the
        /// original inventory (<paramref name="created"/> = false): the initial evidence is never overwritten. If the key belongs to another
        /// document or link, it is refused.
        /// </summary>
        public Baseline SaveBaselineOnce(string key, string documentIdentity, string linkIdentity, IEnumerable<long> ids, out bool created, string expectations = null)
        {
            var isNew = false;
            var res = WithKeyLock(key, () =>
            {
                var existing = LoadBaselineUnlocked(key);
                if (existing != null)
                {
                    if (!string.Equals(existing.DocumentIdentity, documentIdentity, StringComparison.Ordinal) || !string.Equals(existing.LinkIdentity, linkIdentity, StringComparison.Ordinal))
                        throw new InvalidOperationException("the baseline inventory of this attempt belongs to another document or link: target change refused");
                    return existing;
                }
                var list = ids.Distinct().OrderBy(x => x).ToList();
                var b = new Baseline { AttemptKey = key, DocumentIdentity = documentIdentity, LinkIdentity = linkIdentity, Ids = list, CreatedUtc = DateTime.UtcNow, Expectations = expectations };
                b.Sha256 = Baseline.Hash(key, documentIdentity, linkIdentity, list, expectations);
                var json = Json.Write(new Dictionary<string, object>
                {
                    ["esquema"] = Baseline.Schema, ["clave_intento"] = key, ["documento"] = documentIdentity, ["vinculo"] = linkIdentity,
                    ["ids"] = list.Cast<object>().ToList(), ["sha256"] = b.Sha256, ["creado"] = b.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["expectativas"] = expectations,
                });
                var p = BaselinePath(key);
                var tmp = p + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                File.Move(tmp, p);   // fails if it already exists: never overwrites
                isNew = true;
                return b;
            });
            created = isNew;
            return res;
        }

        /// <summary>Baseline inventory of the attempt, or null if there is none. A tampered or unreadable file throws InvalidDataException (it is not used).</summary>
        public Baseline LoadBaseline(string key) => WithKeyLock(key, () => LoadBaselineUnlocked(key));

        private Baseline LoadBaselineUnlocked(string key)
        {
            var p = BaselinePath(key);
            if (!File.Exists(p)) return null;
            try
            {
                var d = Json.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                if (Arg.GetString(d, "esquema") != Baseline.Schema) throw new InvalidDataException("baseline inventory with unknown schema");
                var b = new Baseline
                {
                    AttemptKey = Arg.GetString(d, "clave_intento"), DocumentIdentity = Arg.GetString(d, "documento"), LinkIdentity = Arg.GetString(d, "vinculo"),
                    Sha256 = Arg.GetString(d, "sha256"), Ids = ((List<object>)d["ids"]).Select(x => Convert.ToInt64(x, CultureInfo.InvariantCulture)).ToList(),
                    CreatedUtc = DateTime.Parse(Arg.GetString(d, "creado"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Expectations = Arg.GetString(d, "expectativas"),
                };
                if (b.AttemptKey != key || b.Sha256 != Baseline.Hash(key, b.DocumentIdentity, b.LinkIdentity, b.Ids, b.Expectations))
                    throw new InvalidDataException("the baseline inventory of attempt " + key + " does not match its fingerprint (tampered or damaged)");
                return b;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is KeyNotFoundException || ex is ArgumentException || ex is OverflowException)
            {
                throw new InvalidDataException("the baseline inventory of attempt " + key + " is unreadable: " + ex.GetType().Name);
            }
        }

        private void Save(WorkflowReceipt r)
        {
            var p = PathFor(r.AttemptKey);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, Json.Write(r.ToJson()), new UTF8Encoding(false));
            if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
        }

        /// <summary>Unfinished attempts (to reconcile at startup or after a disconnection).</summary>
        /// <param name="unreadable">Receipts that could not be read: they are reported (never silently discarded) and block their key.</param>
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
