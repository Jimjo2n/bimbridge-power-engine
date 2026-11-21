// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// State shared between the server (pipe threads) and the ribbon/windows (Revit thread).
    /// App.OnStartup fills it. If the MCP is turned off, it stays empty and the buttons say so.
    /// </summary>
    public static class Runtime
    {
        public static PlanStore Plans;
        public static HoldSwitch Hold;
        public static PauseSwitch Pause;
        public static SessionRegistry Sessions;
        public static DocumentTracker Tracker;
        public static Journal Journal;
        /// <summary>Durable audit (0.6.1). Telemetry = Journal; authority = Audit.</summary>
        public static AuditLog Audit;
        /// <summary>Capability grants (R03). Only a person creates them, with the ribbon's "Authorize" button.</summary>
        public static GrantStore Grants;
        /// <summary>#5: C# engine statistics (Revit thread, compilation and query caches, jobs). Memory only; never the Revit API.</summary>
        public static Func<System.Collections.Generic.Dictionary<string, object>> EngineDiagnostics;
        /// <summary>#5 (0.9.1): long operations in progress, recent history (status and text filter) and scheduler. Memory only.</summary>
        public static Func<string, string, (List<Job> Running, List<OperationHistory.Record> Recent, Dictionary<string, object> Scheduler)> Operations;
        /// <summary>#5 (0.9.1): cancel a job from the panel (pressed by a person).</summary>
        public static Func<string, bool> CancelOperation;

        /// <summary>
        /// "Block C# code injection" (consolidated review). NOT blocked by default: exec runs for authorized local agents.
        /// Only a person changes it, on the ribbon; it persists in config.json. No MCP operation changes it.
        /// </summary>
        public static volatile bool BlockDynamicCode;
        /// <summary>Add-in start time (set by App.OnStartup); the panel shows the uptime from here.</summary>
        public static DateTime StartedUtc = DateTime.UtcNow;
        /// <summary>Compatibility: full execution profile when code is not blocked.</summary>
        public static bool TrustedExecActive => !BlockDynamicCode;
        public static PushButton TrustButton, AuthorizeButton;

        /// <summary>Toggles the block and persists it. If it cannot be saved, it does not change (and says so).</summary>
        public static bool ToggleBlockDynamicCode(out string error)
        {
            error = null;
            var on = !BlockDynamicCode;
            try { DynamicCodePolicy.SaveBlocked(EngineConfig.DefaultPath, on); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                error = ex.Message;
                Log("Could not save the C# block: " + ex.Message);
                return false;
            }
            BlockDynamicCode = on;
            if (Config != null) Config.BlockDynamicCode = on;
            try { Audit?.Append(VersionRegistry.HumanAuthor, "bloquear_inyeccion_csharp", on ? "bloqueado" : "permitido"); }
            catch (JournalUnavailableException ex) { Log("AUDIT not written (C# block): " + ex.Message); }
            Log("C# code injection: " + (on ? "BLOCKED" : "allowed"));
            MarkDirty();
            return true;
        }
        /// <summary>
        /// The single confirmation to go from blocked to allowed (ribbon and Settings). "No" by default. Blocking is not confirmed.
        /// </summary>
        public static bool ConfirmAllowDynamicCode()
        {
            var td = new TaskDialog(Bimbridge.Protocol.Brand.Product)
            {
                MainInstruction = "Allow dynamic C#?",
                MainContent = "The authorized local AI will be able to run C# code on the Revit API (exec), audited. " +
                              "It is not a sandbox: it runs with your user's permissions and its effects outside the document are not rolled back.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            return td.Show() == TaskDialogResult.Yes;
        }

        // ---- UI preferences (this user only, next to the rest of the add-in's data; never config.json) ----
        private static string UiPrefsPath => Home.Sub("interfaz.json");

        // Read once, before Runtime is first used (App.OnStartup fills it at startup).
        static Runtime()
        {
            try
            {
                if (System.IO.File.Exists(UiPrefsPath) && Bimbridge.Protocol.Json.Parse(System.IO.File.ReadAllText(UiPrefsPath)) is Dictionary<string, object> d)
                    Theme.ReduceMotionOverride = d.TryGetValue("movimiento", out var m) && "reducido".Equals(m as string, StringComparison.Ordinal);
            }
            catch (Exception) { } // unreadable or corrupt: automatic motion (follows Windows)
        }

        /// <summary>"Motion: Reduced" applies immediately; if it cannot be saved, it holds until Revit closes and returns the error.</summary>
        public static bool SetReducedMotion(bool reduced, out string error)
        {
            error = null;
            Theme.ReduceMotionOverride = reduced;
            try { Home.WriteAtomic(UiPrefsPath, Bimbridge.Protocol.Json.Write(new Dictionary<string, object> { ["movimiento"] = reduced ? "reducido" : "automatico" })); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                Log("Could not save the motion preference: " + ex.Message);
                return false;
            }
            return true;
        }

        public static EngineConfig Config;
        public static string RevitVersion = "";
        public static Action<string> Log = _ => { };

        /// <summary>Active model title and queued plans (updated on Idling; read by the Status window).</summary>
        public static volatile string ActiveDocTitle;
        /// <summary>Durable key of the active document (the panel uses it to check write blocks).</summary>
        public static volatile string ActiveDocKey;
        public static volatile int HeldCount;

        public static bool Ready => Plans != null && Hold != null && Pause != null && Tracker != null && Config != null;

        // ---- ribbon ----
        public static PushButton StatusButton, PauseButton, ModeButton, QueueButton;
        private static volatile bool _dirty = true;
        private static string _lastDocKey;
        private static string _lastVisual;

        public static void MarkDirty() => _dirty = true;

        public static void TogglePause()
        {
            var on = Pause.Toggle();
            Journal?.Log(VersionRegistry.HumanAuthor, "pausar", on ? "si" : "no", 0);
            Log("Pause AI: " + (on ? "YES" : "NO"));
            MarkDirty();
        }

        public static void ToggleHold()
        {
            var on = Hold.Toggle();
            Journal?.Log(VersionRegistry.HumanAuthor, "retener", on ? "si" : "no", 0);
            Log("Hold: " + (on ? "YES" : "NO"));
            MarkDirty();
        }

        /// <summary>Applies a held plan. Call only in a valid API context (a command or a command's modal window).</summary>
        public static PlanOps.ApplyResult ApplyHeld(Document doc, Plan p)
        {
            var sw = Stopwatch.StartNew();
            // Fail-closed: with unreconciled operations the plan stays in the queue and the model is not touched.
            var key0 = DocumentTracker.KeyOf(doc) ?? p.DocKey;
            if (p.ReceiptDocPrint != null && !string.Equals(DocRegistry.Instance.IdentityOf(doc)?.Print, p.ReceiptDocPrint, StringComparison.Ordinal))
                return new PlanOps.ApplyResult { Status = PlanStatus.Held, Detail = "The active document does not match the fingerprint of the held attempt." };
            try { Plans.LoadPersisted(key0); } catch (Exception ex) { Log("Queue: " + ex.Message); }
            var block0 = Plans.CheckWritable(key0);
            if (block0.Blocked) { Log("Queue: plan " + p.Id + " NOT applied: " + block0.Message); return new PlanOps.ApplyResult { Status = PlanStatus.Held, Detail = block0.Message }; }
            // R01: durable intent BEFORE touching the model (AuditGate); without it nothing is applied. Result afterwards.
            var ex0 = new Dictionary<string, object> { ["plan"] = p.Id, ["cambios"] = (long)p.Entries.Count };
            if (p.ReceiptAttempt != null)
            {
                ex0["plan_id"] = p.Id;
                ex0["intento_id"] = p.ReceiptAttempt;
                ex0["sesion_intento"] = p.ReceiptSession;
                ex0["agente"] = p.ReceiptAgent;
                ex0["huella_doc"] = p.ReceiptDocPrint;
            }
            return AuditGate.Apply(Audit, p.SessionId, "aplicar_cola", DocumentTracker.KeyOf(doc) ?? "", ex0, () =>
            {
                try { Plans.BeginWrite(p); }
                catch (Exception ex) when (ex is DocWriteBlockedException || ex is PlanStoreException)
                {
                    Log("Queue: plan " + p.Id + " NOT applied: " + ex.Message);
                    return new PlanOps.ApplyResult { Status = PlanStatus.Held, Detail = ex.Message };
                }
                p.Status = PlanStatus.InProgress;
                PlanOps.ApplyResult r;
                Tracker.CurrentWriter = p.SessionId;
                try { r = PlanOps.Apply(doc, p, Config.SubBatchSize, Theme.ClientName(p.Client ?? p.SessionId)); }
                catch (Exception ex)
                {
                    // An exception after starting does not prove it was rolled back.
                    GroupAbort.PreserveInterrupted(p, ex.GetType().Name + ": " + ex.Message);
                    r = new PlanOps.ApplyResult { Status = Bimbridge.Protocol.Status.Unknown, Detail = ex.Message };
                }
                finally { Tracker.CurrentWriter = null; }
                Plans.Finish(p, r.Status, r.Detail);
                try
                {
                    var owner = Sessions?.All().FirstOrDefault(s => s.Id == p.SessionId);
                    if (owner != null) RequestHandler.SaveCreated(owner, p);
                }
                catch (Exception ex) { Log("Queue: could not save the created elements: " + ex.Message); }
                ex0["detalle"] = r.Detail;
                if (r.Status == PlanStatus.Applied && PlanVerification.Postconditions(p.Verification) != PlanVerification.Met)
                    ex0["estado_recibo"] = Bimbridge.Protocol.Status.CompletedWithDifferences;
                Journal?.Log(p.SessionId, "aplicar_cola", r.Status, sw.ElapsedMilliseconds, ex0);
                MarkDirty();
                return r;
            }, r => r.Status, why =>
            {
                Log("Queue: plan " + p.Id + " NOT applied: " + why);
                return new PlanOps.ApplyResult { Status = PlanStatus.RolledBack, Detail = why };
            }, (r, why) =>
            {
                // rc3: what was applied stays applied; the plan is flagged and the client sees it as unverified.
                Log("AUDIT: plan " + p.Id + ": " + why);
                r.Warnings.Add(why);
                r.Detail = (r.Detail != null ? r.Detail + " | " : "") + why;
                r.AuditIncomplete = true;
                try { Plans.Finish(p, r.Status, r.Detail); } catch (Exception ex) { Log("Queue: " + ex.Message); }
                return r;
            });
        }

        /// <summary>Called on Idling (Revit thread). Only redraws if something visible changed.</summary>
        public static void OnIdling(UIApplication app)
        {
            try { UpdateService.RefreshButton(); } catch (Exception ex) { Log("Update button: " + ex.Message); }
            if (!Ready) return;
            var doc = app.ActiveUIDocument?.Document;
            var key = DocumentTracker.KeyOf(doc);
            var sessions = Sessions?.Count ?? 0;
            var paused = Pause.On;
            var hold = Hold.On;
            if (!_dirty && key == _lastDocKey && _lastVisual != null && _lastVisual.StartsWith(sessions + "|" + paused + "|" + hold + "|", StringComparison.Ordinal)) return;
            _dirty = false;
            if (key != null && key != _lastDocKey)
            {
                try { Plans.LoadPersisted(key); } catch (Exception ex) { Log("Queue: could not read from disk: " + ex.Message); }
            }
            _lastDocKey = key;
            ActiveDocKey = key;
            ActiveDocTitle = doc?.Title;
            var n = key == null ? 0 : Plans.Held(key).Count;
            HeldCount = n;
            var visual = sessions + "|" + paused + "|" + hold + "|" + n + "|" + BlockDynamicCode;
            if (visual == _lastVisual) return;
            _lastVisual = visual;
            try { UpdateRibbon(sessions, paused, hold, n); } catch (Exception ex) { Log("Ribbon: " + ex.Message); }
        }

        // Last state drawn on each button: the image is only touched (and animated) when its own state changes. null = not painted yet.
        private static System.Windows.Media.Color? _shownStatus;
        private static bool? _shownTrust, _shownPaused, _shownHold;
        private static int? _shownHeld;

        private static void UpdateRibbon(int sessions, bool paused, bool hold, int held)
        {
            var writes = Config.Allows('P');
            if (StatusButton != null)
            {
                var c = paused ? Theme.Red : sessions > 0 ? Theme.Green : Theme.Slate;
                if (_shownStatus != c)
                {
                    var from = _shownStatus ?? c;
                    RibbonMotion.Play(StatusButton, "status|" + from + ">" + c, _shownStatus != null, (t, s) => Icons.Status(from, c, t, s), s => Icons.Status(c, s));
                    _shownStatus = c;
                }
                StatusButton.ItemText = "Power Engine\n" + (paused ? "Status · paused" : sessions == 0 ? "Status · no AI" : "Status · " + sessions + " AI");
            }
            if (TrustButton != null)
            {
                var block = BlockDynamicCode;
                if (_shownTrust != block)
                {
                    RibbonMotion.Play(TrustButton, "mode|" + block, _shownTrust != null, (t, s) => Icons.Mode(block, t, s), s => Icons.Mode(block, s));
                    _shownTrust = block;
                }
                TrustButton.ItemText = block ? "Dynamic C#\nblocked" : "Block\nC# injection";
            }
            if (PauseButton != null)
            {
                if (_shownPaused != paused)
                {
                    RibbonMotion.Play(PauseButton, "pause|" + paused, _shownPaused != null, (t, s) => Icons.Pause(paused, t, s), s => Icons.Pause(paused, s));
                    _shownPaused = paused;
                }
                PauseButton.ItemText = paused ? "Resume\nAI" : "Pause\nAI";
            }
            if (ModeButton != null)
            {
                var effectiveHold = hold || !Config.Allows('W');
                if (_shownHold != effectiveHold)
                {
                    RibbonMotion.Play(ModeButton, "mode|" + effectiveHold, _shownHold != null, (t, s) => Icons.Mode(effectiveHold, t, s), s => Icons.Mode(effectiveHold, s));
                    _shownHold = effectiveHold;
                }
                ModeButton.ItemText = effectiveHold ? "Mode\nHold" : "Mode\nLive";
                ModeButton.Enabled = writes && Config.Allows('W');
            }
            if (QueueButton != null)
            {
                if (_shownHeld != held)
                {
                    // The badge only "appears" with a new number; when the queue empties it disappears at once (exit is faster than entrance).
                    RibbonMotion.Play(QueueButton, "queue|" + held, _shownHeld != null && held > 0, (t, s) => Icons.Queue(held, t, s), s => Icons.Queue(held, s));
                    _shownHeld = held;
                }
                QueueButton.ItemText = held == 0 ? "Queue\nempty" : held == 1 ? "Queue\n1 plan" : "Queue\n" + held + " plans";
            }
        }
    }
}
