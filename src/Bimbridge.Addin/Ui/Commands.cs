// SPDX-License-Identifier: GPL-3.0-only
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit;

namespace Bimbridge.Addin.Ui
{
    /// <summary>The buttons also work with no model open (Status, Pause, Mode, Help).</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) => true;
    }

    internal static class Guard
    {
        public static bool Ready()
        {
            if (Runtime.Ready) return true;
            TaskDialog.Show("BIMBRIDGE - Power Engine", "The MCP is turned off.\n\nCheck %APPDATA%\\Bimbridge\\config.json (enabled) or the log %LOCALAPPDATA%\\Bimbridge\\addin.log.");
            return false;
        }
    }

    /// <summary>Status: connected AIs, permissions and recent activity.</summary>
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

    /// <summary>Pause / Resume AI: while paused, the add-in rejects every request.</summary>
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

    /// <summary>Live / Hold mode.</summary>
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

    /// <summary>Queue: review, choose, apply or discard what the AI left.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class QueueCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show("BIMBRIDGE - Power Engine", "Open a model to see its change queue."); return Result.Cancelled; }
            var key = DocumentTracker.KeyOf(doc);
            Runtime.Plans.LoadPersisted(key);
            new QueueWindow(data.Application, doc, Runtime.Plans.Held(key)).ShowDialog();
            Runtime.MarkDirty();
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>Settings: Pause, Mode and C# blocking in one window, with their consequences. Same path as the ribbon buttons.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            SettingsWindow.Show(data.Application);
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

    /// <summary>Update: installs a new version found in Downloads/Desktop when Revit closes, or goes back to the previous one.</summary>
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

namespace Bimbridge.Addin.Ui
{
    using System.Collections.Generic;
    using System.Linq;
    using Bimbridge.Engine;

    /// <summary>
    /// R03: the ONLY way to create grants. A person answers one Revit dialog for each AI request.
    /// With no pending requests, it lets the person revoke the active ones.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AuthorizeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            var g = Runtime.Grants;
            if (g == null) { TaskDialog.Show("BIMBRIDGE - Power Engine", "The authorization store is unavailable: tasks that write stay blocked."); return Result.Cancelled; }
            var who = Environment.UserName + " (Revit dialog)";
            var pending = g.PendingRequests();
            foreach (var r in pending)
            {
                var td = new TaskDialog("BIMBRIDGE - Power Engine — authorize task")
                {
                    MainInstruction = "Authorize the task «" + r.Task + "»?",
                    MainContent = "Asks to: " + string.Join(", ", r.Capabilities.Select(Describe)) + "\n" +
                                  "Document: " + r.DocTitle + "\nObjective: " + (r.Objective ?? "(no objective)") + "\nRequested by: " + r.Client + "\n\n" +
                                  "The permission is valid only for this task and this document, for 8 hours. You can revoke it with this same button.",
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
                if (live.Count == 0) { TaskDialog.Show("BIMBRIDGE - Power Engine", "There are no pending requests and no active permissions."); return Result.Succeeded; }
                var td = new TaskDialog("BIMBRIDGE - Power Engine — active permissions")
                {
                    MainInstruction = live.Count + " active permissions",
                    MainContent = string.Join("\n", live.Take(12).Select(x => "• " + x.Task + ": " + string.Join(", ", x.Capabilities.Select(Describe)) + " (expires " + x.Expires.Substring(0, 16).Replace('T', ' ') + " UTC)")),
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Revoke all", "Running tasks stop before their next unit.");
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
                case Capability.Modificar: return "modify the model";
                case Capability.Guardar: return "save";
                case Capability.Sync: return "sync with central";
                case Capability.Exportar: return "export files (PDF…)";
                case Capability.ArchivoEscritura: return "write files";
                case Capability.ReconciliarNoAplicada: return "repeat uncertain units";
                case Capability.RecetaExterna: return "run code that is not one of the installed recipes";
                default: return c;
            }
        }

        private static void TryAudit(string who, string op, string estado, Dictionary<string, object> extra)
        {
            try { Runtime.Audit?.Append(who, op, estado, extra); }
            catch (JournalUnavailableException ex) { Runtime.Log("AUDIT not written (" + op + "): " + ex.Message); }
        }
    }

    /// <summary>"Block C# code injection": one persistent control. It applies to new requests.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class TrustCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            // Allowing widens what the AI can do: the same confirmation as Settings. Blocking does not ask.
            if (Runtime.BlockDynamicCode && !Runtime.ConfirmAllowDynamicCode()) return Result.Cancelled;
            if (!Runtime.ToggleBlockDynamicCode(out var error))
            {
                TaskDialog.Show(Bimbridge.Protocol.Brand.Product, "Could not save the change to config.json, so it was not applied.\n\n" + error);
                return Result.Failed;
            }
            Runtime.OnIdling(data.Application);
            return Result.Succeeded;
        }
    }
}
