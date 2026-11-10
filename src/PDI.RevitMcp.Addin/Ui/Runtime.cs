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
            try { Plans.LoadPersisted(key0); } catch (Exception ex) { Log("Cola: " + ex.Message); }
            var block0 = Plans.CheckWritable(key0);
            if (block0.Blocked) { Log("Cola: plan " + p.Id + " NO aplicado: " + block0.Message); return new PlanOps.ApplyResult { Status = PlanStatus.Retenido, Detail = block0.Message }; }
            // R01: intención durable ANTES de tocar el modelo (AuditGate); sin ella no se aplica nada. Resultado después.
            var ex0 = new Dictionary<string, object> { ["plan"] = p.Id, ["cambios"] = (long)p.Entries.Count };
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

        private static void UpdateRibbon(int sessions, bool paused, bool hold, int held)
        {
            var writes = Config.Allows('P');
            if (StatusButton != null)
            {
                var c = paused ? Theme.Red : sessions > 0 ? Theme.Green : Theme.Slate;
                StatusButton.LargeImage = Icons.Status(c, 32);
                StatusButton.Image = Icons.Status(c, 16);
                StatusButton.ItemText = "Power Engine\n" + (paused ? "Estado · pausa" : sessions == 0 ? "Estado · sin IA" : "Estado · " + sessions + " IA");
            }
            if (TrustButton != null)
            {
                TrustButton.LargeImage = Icons.Mode(BlockDynamicCode, 32);
                TrustButton.Image = Icons.Mode(BlockDynamicCode, 16);
                TrustButton.ItemText = BlockDynamicCode ? "C# dinámico\nbloqueado" : "Bloquear\ninyección C#";
            }
            if (PauseButton != null)
            {
                PauseButton.LargeImage = Icons.Pause(paused, 32);
                PauseButton.Image = Icons.Pause(paused, 16);
                PauseButton.ItemText = paused ? "Reanudar\nIA" : "Pausar\nIA";
            }
            if (ModeButton != null)
            {
                var effectiveHold = hold || !Config.Allows('W');
                ModeButton.LargeImage = Icons.Mode(effectiveHold, 32);
                ModeButton.Image = Icons.Mode(effectiveHold, 16);
                ModeButton.ItemText = effectiveHold ? "Modo\nRetener" : "Modo\nEn vivo";
                ModeButton.Enabled = writes && Config.Allows('W');
            }
            if (QueueButton != null)
            {
                QueueButton.LargeImage = Icons.Queue(held, 32);
                QueueButton.Image = Icons.Queue(held, 16);
                QueueButton.ItemText = held == 0 ? "Cola\nvacía" : held == 1 ? "Cola\n1 plan" : "Cola\n" + held + " planes";
            }
        }
    }
}
