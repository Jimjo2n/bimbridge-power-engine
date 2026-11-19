// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit;
using PDI.RevitMcp.Engine;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Estado compartido entre el servidor (hilos de pipe) y el ribbon/ventanas (hilo de Revit).
    /// Lo llena App.OnStartup. Si el MCP está apagado, queda vacío y los botones lo dicen.
    /// </summary>
    public static class Runtime
    {
        public static PlanStore Plans;
        public static HoldSwitch Hold;
        public static PauseSwitch Pause;
        public static SessionRegistry Sessions;
        public static DocumentTracker Tracker;
        public static Journal Journal;
        /// <summary>Auditoría durable (0.6.1). Telemetría = Journal; autoridad = Audit.</summary>
        public static AuditLog Audit;
        /// <summary>Concesiones de capacidades (R03). Sólo las crea una persona con el botón «Autorizar» de la cinta.</summary>
        public static GrantStore Grants;
        /// <summary>#5: estadísticas del motor C# (hilo de Revit, cachés de compilación y de consultas, trabajos). Sólo memoria; nunca la API de Revit.</summary>
        public static Func<System.Collections.Generic.Dictionary<string, object>> EngineDiagnostics;
        /// <summary>#5 (0.9.1): operaciones largas en curso, historial reciente (filtro de estado y texto) y planificador. Sólo memoria.</summary>
        public static Func<string, string, (List<Job> Running, List<OperationHistory.Record> Recent, Dictionary<string, object> Scheduler)> Operations;
        /// <summary>#5 (0.9.1): cancelar un trabajo desde el panel (lo pulsa una persona).</summary>
        public static Func<string, bool> CancelOperation;

        /// <summary>
        /// «Bloquear inyección de código C#» (revisión consolidada). Por defecto NO bloqueado: exec corre para los agentes locales autorizados.
        /// Lo cambia sólo una persona en la cinta; persiste en config.json. Ninguna operación MCP lo cambia.
        /// </summary>
        public static volatile bool BlockDynamicCode;
        /// <summary>Arranque del add-in (lo fija App.OnStartup); el panel muestra el tiempo activo desde aquí.</summary>
        public static DateTime StartedUtc = DateTime.UtcNow;
        /// <summary>Compatibilidad: perfil de ejecución completo cuando el código no está bloqueado.</summary>
        public static bool TrustedExecActive => !BlockDynamicCode;
        public static PushButton TrustButton, AuthorizeButton;

        /// <summary>Cambia el bloqueo y lo persiste. Si no se puede guardar, no cambia (y lo avisa).</summary>
        public static bool ToggleBlockDynamicCode(out string error)
        {
            error = null;
            var on = !BlockDynamicCode;
            try { DynamicCodePolicy.SaveBlocked(EngineConfig.DefaultPath, on); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                error = ex.Message;
                Log("No se pudo guardar el bloqueo de C#: " + ex.Message);
                return false;
            }
            BlockDynamicCode = on;
            if (Config != null) Config.BlockDynamicCode = on;
            try { Audit?.Append(VersionRegistry.HumanAuthor, "bloquear_inyeccion_csharp", on ? "bloqueado" : "permitido"); }
            catch (JournalUnavailableException ex) { Log("AUDITORÍA no escrita (bloqueo C#): " + ex.Message); }
            Log("Inyección de código C#: " + (on ? "BLOQUEADA" : "permitida"));
            MarkDirty();
            return true;
        }
        /// <summary>
        /// Única confirmación para pasar de bloqueado a permitido (cinta y Ajustes). "No" por defecto. Bloquear no se confirma.
        /// </summary>
        public static bool ConfirmAllowDynamicCode()
        {
            var td = new TaskDialog(PDI.RevitMcp.Protocol.Brand.Product)
            {
                MainInstruction = "¿Permitir C# dinámico?",
                MainContent = "La IA local autorizada podrá ejecutar código C# sobre la API de Revit (exec), auditado. " +
                              "No es un sandbox: corre con los permisos de su usuario y sus efectos fuera del documento no se revierten.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            return td.Show() == TaskDialogResult.Yes;
        }

        // ---- preferencias de interfaz (sólo de este usuario, junto a los demás datos del complemento; nunca config.json) ----
        private static string UiPrefsPath => Home.Sub("interfaz.json");

        // Se lee una vez, antes del primer uso de Runtime (App.OnStartup lo llena al arrancar).
        static Runtime()
        {
            try
            {
                if (System.IO.File.Exists(UiPrefsPath) && PDI.RevitMcp.Protocol.Json.Parse(System.IO.File.ReadAllText(UiPrefsPath)) is Dictionary<string, object> d)
                    Theme.ReduceMotionOverride = d.TryGetValue("movimiento", out var m) && "reducido".Equals(m as string, StringComparison.Ordinal);
            }
            catch (Exception) { } // ilegible o dañado: movimiento automático (sigue a Windows)
        }

        /// <summary>«Movimiento: Reducido» se aplica al momento; si no se puede guardar, vale hasta cerrar Revit y devuelve el error.</summary>
        public static bool SetReducedMotion(bool reduced, out string error)
        {
            error = null;
            Theme.ReduceMotionOverride = reduced;
            try { Home.WriteAtomic(UiPrefsPath, PDI.RevitMcp.Protocol.Json.Write(new Dictionary<string, object> { ["movimiento"] = reduced ? "reducido" : "automatico" })); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                Log("No se pudo guardar la preferencia de movimiento: " + ex.Message);
                return false;
            }
            return true;
        }

        public static EngineConfig Config;
        public static string RevitVersion = "";
        public static Action<string> Log = _ => { };

        /// <summary>Título del modelo activo y planes en cola (se actualizan en Idling; los lee la ventana Estado).</summary>
        public static volatile string ActiveDocTitle;
        /// <summary>Clave durable del documento activo (la usa el panel para comprobar bloqueos de escritura).</summary>
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
            Log("Pausar IA: " + (on ? "SÍ" : "NO"));
            MarkDirty();
        }

        public static void ToggleHold()
        {
            var on = Hold.Toggle();
            Journal?.Log(VersionRegistry.HumanAuthor, "retener", on ? "si" : "no", 0);
            Log("Retener: " + (on ? "SÍ" : "NO"));
            MarkDirty();
        }

        /// <summary>Aplica un plan retenido. Llamar sólo en contexto válido de la API (comando o ventana modal de un comando).</summary>
        public static PlanOps.ApplyResult ApplyHeld(Document doc, Plan p)
        {
            var sw = Stopwatch.StartNew();
            // Fail-closed: con operaciones sin reconciliar el plan sigue en la cola y no se toca el modelo.
            var key0 = DocumentTracker.KeyOf(doc) ?? p.DocKey;
            if (p.ReceiptDocPrint != null && !string.Equals(DocRegistry.Instance.IdentityOf(doc)?.Print, p.ReceiptDocPrint, StringComparison.Ordinal))
                return new PlanOps.ApplyResult { Status = PlanStatus.Retenido, Detail = "El documento activo no coincide con la huella del intento retenido." };
            try { Plans.LoadPersisted(key0); } catch (Exception ex) { Log("Cola: " + ex.Message); }
            var block0 = Plans.CheckWritable(key0);
            if (block0.Blocked) { Log("Cola: plan " + p.Id + " NO aplicado: " + block0.Message); return new PlanOps.ApplyResult { Status = PlanStatus.Retenido, Detail = block0.Message }; }
            // R01: intención durable ANTES de tocar el modelo (AuditGate); sin ella no se aplica nada. Resultado después.
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
                    Log("Cola: plan " + p.Id + " NO aplicado: " + ex.Message);
                    return new PlanOps.ApplyResult { Status = PlanStatus.Retenido, Detail = ex.Message };
                }
                p.Status = PlanStatus.EnProceso;
                PlanOps.ApplyResult r;
                Tracker.CurrentWriter = p.SessionId;
                try { r = PlanOps.Apply(doc, p, Config.SubBatchSize, Theme.ClientName(p.Client ?? p.SessionId)); }
                catch (Exception ex)
                {
                    // Una excepción después de empezar no demuestra que se revirtió.
                    GroupAbort.PreserveInterrupted(p, ex.GetType().Name + ": " + ex.Message);
                    r = new PlanOps.ApplyResult { Status = PDI.RevitMcp.Protocol.Status.Desconocido, Detail = ex.Message };
                }
                finally { Tracker.CurrentWriter = null; }
                Plans.Finish(p, r.Status, r.Detail);
                try
                {
                    var owner = Sessions?.All().FirstOrDefault(s => s.Id == p.SessionId);
                    if (owner != null) RequestHandler.SaveCreated(owner, p);
                }
                catch (Exception ex) { Log("Cola: no se pudieron guardar los creados: " + ex.Message); }
                ex0["detalle"] = r.Detail;
                if (r.Status == PlanStatus.Aplicado && PlanVerification.Postconditions(p.Verification) != PlanVerification.Met)
                    ex0["estado_recibo"] = PDI.RevitMcp.Protocol.Status.ConDiferencias;
                Journal?.Log(p.SessionId, "aplicar_cola", r.Status, sw.ElapsedMilliseconds, ex0);
                MarkDirty();
                return r;
            }, r => r.Status, why =>
            {
                Log("Cola: plan " + p.Id + " NO aplicado: " + why);
                return new PlanOps.ApplyResult { Status = PlanStatus.Revertido, Detail = why };
            }, (r, why) =>
            {
                // rc3: lo aplicado sigue aplicado; el plan queda marcado y el cliente lo ve como no verificado.
                Log("AUDITORÍA: plan " + p.Id + ": " + why);
                r.Warnings.Add(why);
                r.Detail = (r.Detail != null ? r.Detail + " | " : "") + why;
                r.AuditIncomplete = true;
                try { Plans.Finish(p, r.Status, r.Detail); } catch (Exception ex) { Log("Cola: " + ex.Message); }
                return r;
            });
        }

        /// <summary>Se llama en Idling (hilo de Revit). Sólo redibuja si algo visible cambió.</summary>
        public static void OnIdling(UIApplication app)
        {
            try { UpdateService.RefreshButton(); } catch (Exception ex) { Log("Botón Actualizar: " + ex.Message); }
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
                try { Plans.LoadPersisted(key); } catch (Exception ex) { Log("Cola: no se pudo leer del disco: " + ex.Message); }
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

        // Último estado dibujado en cada botón: la imagen sólo se toca (y se anima) cuando el suyo cambia. null = aún sin pintar.
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
                StatusButton.ItemText = "Power Engine\n" + (paused ? "Estado · pausa" : sessions == 0 ? "Estado · sin IA" : "Estado · " + sessions + " IA");
            }
            if (TrustButton != null)
            {
                var block = BlockDynamicCode;
                if (_shownTrust != block)
                {
                    RibbonMotion.Play(TrustButton, "mode|" + block, _shownTrust != null, (t, s) => Icons.Mode(block, t, s), s => Icons.Mode(block, s));
                    _shownTrust = block;
                }
                TrustButton.ItemText = block ? "C# dinámico\nbloqueado" : "Bloquear\ninyección C#";
            }
            if (PauseButton != null)
            {
                if (_shownPaused != paused)
                {
                    RibbonMotion.Play(PauseButton, "pause|" + paused, _shownPaused != null, (t, s) => Icons.Pause(paused, t, s), s => Icons.Pause(paused, s));
                    _shownPaused = paused;
                }
                PauseButton.ItemText = paused ? "Reanudar\nIA" : "Pausar\nIA";
            }
            if (ModeButton != null)
            {
                var effectiveHold = hold || !Config.Allows('W');
                if (_shownHold != effectiveHold)
                {
                    RibbonMotion.Play(ModeButton, "mode|" + effectiveHold, _shownHold != null, (t, s) => Icons.Mode(effectiveHold, t, s), s => Icons.Mode(effectiveHold, s));
                    _shownHold = effectiveHold;
                }
                ModeButton.ItemText = effectiveHold ? "Modo\nRetener" : "Modo\nEn vivo";
                ModeButton.Enabled = writes && Config.Allows('W');
            }
            if (QueueButton != null)
            {
                if (_shownHeld != held)
                {
                    // El globo sólo "aparece" con un número nuevo; al vaciarse la cola desaparece al momento (la salida es más rápida que la entrada).
                    RibbonMotion.Play(QueueButton, "queue|" + held, _shownHeld != null && held > 0, (t, s) => Icons.Queue(held, t, s), s => Icons.Queue(held, s));
                    _shownHeld = held;
                }
                QueueButton.ItemText = held == 0 ? "Cola\nvacía" : held == 1 ? "Cola\n1 plan" : "Cola\n" + held + " planes";
            }
        }
    }
}
