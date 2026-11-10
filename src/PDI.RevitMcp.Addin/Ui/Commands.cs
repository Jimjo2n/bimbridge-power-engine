// SPDX-License-Identifier: GPL-3.0-only
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>Los botones funcionan también sin modelo abierto (Estado, Pausar, Modo, Ayuda).</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) => true;
    }

    internal static class Guard
    {
        public static bool Ready()
        {
            if (Runtime.Ready) return true;
            TaskDialog.Show("BIMBRIDGE - Power Engine", "El MCP está apagado.\n\nRevise %APPDATA%\\PDI.RevitMcp\\config.json (enabled) o el registro %LOCALAPPDATA%\\PDI.RevitMcp\\addin.log.");
            return false;
        }
    }

    /// <summary>Estado: IAs conectadas, permisos y actividad reciente.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            StatusWindow.Show(data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }

    /// <summary>Pausar / Reanudar IA: mientras está en pausa, el add-in rechaza toda petición.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PauseCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            Runtime.TogglePause();
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>Modo En vivo / Retener.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ModeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            Runtime.ToggleHold();
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>Cola: revisar, elegir, aplicar o descartar lo que dejó la IA.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class QueueCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show("BIMBRIDGE - Power Engine", "Abra un modelo para ver su cola de cambios."); return Result.Cancelled; }
            var key = DocumentTracker.KeyOf(doc);
            Runtime.Plans.LoadPersisted(key);
            new QueueWindow(data.Application, doc, Runtime.Plans.Held(key)).ShowDialog();
            Runtime.MarkDirty();
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class HelpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            HelpWindow.Show(data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }

    /// <summary>Actualizar: instala una versión nueva encontrada en Descargas/Escritorio sin cerrar Revit, o vuelve a la anterior.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class UpdateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UpdateWindow.Show(data.Application.MainWindowHandle);
            UpdateService.RefreshButton();
            return Result.Succeeded;
        }
    }
}

namespace PDI.RevitMcp.Addin.Ui
{
    using System.Collections.Generic;
    using System.Linq;
    using PDI.RevitMcp.Engine;

    /// <summary>
    /// R03: la ÚNICA vía para crear concesiones. Una persona responde un diálogo de Revit por cada solicitud de la IA.
    /// Sin solicitudes pendientes, permite revocar las vigentes.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AuthorizeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            var g = Runtime.Grants;
            if (g == null) { TaskDialog.Show("BIMBRIDGE - Power Engine", "El almacén de autorizaciones no está disponible: las tareas que escriben quedan bloqueadas."); return Result.Cancelled; }
            var who = Environment.UserName + " (diálogo de Revit)";
            var pending = g.PendingRequests();
            foreach (var r in pending)
            {
                var td = new TaskDialog("BIMBRIDGE - Power Engine — autorizar tarea")
                {
                    MainInstruction = "¿Autorizar la tarea «" + r.Task + "»?",
                    MainContent = "Pide: " + string.Join(", ", r.Capabilities.Select(Describe)) + "\n" +
                                  "Documento: " + r.DocTitle + "\nObjetivo: " + (r.Objective ?? "(sin objetivo)") + "\nPedido por: " + r.Client + "\n\n" +
                                  "El permiso vale sólo para esta tarea y este documento, durante 8 horas. Puede revocarlo con este mismo botón.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.No,
                };
                var res = td.Show();
                if (res == TaskDialogResult.Cancel) break;
                if (res == TaskDialogResult.Yes)
                {
                    var grant = g.Approve(r.Task, r.DocKey, r.DocPrint, r.Capabilities, TimeSpan.FromHours(8), who, DateTime.UtcNow);
                    TryAudit(who, "concesion", "aprobada", new Dictionary<string, object> { ["concesion"] = grant.Id, ["tarea"] = r.Task, ["doc"] = r.DocKey, ["capacidades"] = r.Capabilities.Cast<object>().ToList() });
                }
                else TryAudit(who, "concesion", "rechazada", new Dictionary<string, object> { ["tarea"] = r.Task, ["capacidades"] = r.Capabilities.Cast<object>().ToList() });
                g.CloseRequest(r.Id);
            }
            if (pending.Count == 0)
            {
                var live = g.All().Where(x => x.ValidAt(DateTime.UtcNow)).ToList();
                if (live.Count == 0) { TaskDialog.Show("BIMBRIDGE - Power Engine", "No hay solicitudes pendientes ni permisos vigentes."); return Result.Succeeded; }
                var td = new TaskDialog("BIMBRIDGE - Power Engine — permisos vigentes")
                {
                    MainInstruction = live.Count + " permisos vigentes",
                    MainContent = string.Join("\n", live.Take(12).Select(x => "• " + x.Task + ": " + string.Join(", ", x.Capabilities.Select(Describe)) + " (vence " + x.Expires.Substring(0, 16).Replace('T', ' ') + " UTC)")),
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Revocar todos", "Las tareas en curso se detienen antes de su siguiente unidad.");
                if (td.Show() == TaskDialogResult.CommandLink1)
                {
                    foreach (var x in live) g.Revoke(x.Id, DateTime.UtcNow);
                    TryAudit(who, "concesion", "revocadas", new Dictionary<string, object> { ["n"] = (long)live.Count });
                }
            }
            Runtime.MarkDirty();
            return Result.Succeeded;
        }

        private static string Describe(string c)
        {
            switch (c)
            {
                case Capability.Modificar: return "modificar el modelo";
                case Capability.Guardar: return "guardar";
                case Capability.Sync: return "sincronizar con el central";
                case Capability.Exportar: return "exportar archivos (PDF…)";
                case Capability.ArchivoEscritura: return "escribir archivos";
                case Capability.ReconciliarNoAplicada: return "repetir unidades dudosas";
                case Capability.RecetaExterna: return "ejecutar código que no es de las recetas instaladas";
                default: return c;
            }
        }

        private static void TryAudit(string who, string op, string estado, Dictionary<string, object> extra)
        {
            try { Runtime.Audit?.Append(who, op, estado, extra); }
            catch (JournalUnavailableException ex) { Runtime.Log("AUDITORÍA no escrita (" + op + "): " + ex.Message); }
        }
    }

    /// <summary>«Bloquear inyección de código C#»: un solo control, persistente. Se aplica a las peticiones nuevas.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TrustCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            if (!Runtime.ToggleBlockDynamicCode(out var error))
            {
                TaskDialog.Show(PDI.RevitMcp.Protocol.Brand.Product, "No se pudo guardar el cambio en config.json, así que no se aplicó.\n\n" + error);
                return Result.Failed;
            }
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }
}
