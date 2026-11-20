// SPDX-License-Identifier: GPL-3.0-only
using System;
using Bimbridge.Protocol;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Engine;
using Bimbridge.Setup;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// 0.8.0: BIMBRIDGE → Developer Tools → Update from ZIP. A person presses it; the AI has no tool that reaches here.
    /// File picker → inspection (manifest, signature, hashes, compatibility) → versions and provenance on screen → confirmation
    /// (double if unsigned) → verified staging → external process that waits for the user to close Revit.
    /// No hot reload, no closing or restarting Revit, no downloads.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ZipUpdateCommand : IExternalCommand
    {
        internal static List<string> KeysConfig = new List<string>();
        /// <summary>0.9.0: active profile (production by default: signed packages only).</summary>
        internal static ProfilePolicy Policy = new ProfilePolicy();

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var paths = UpdateService.Paths;
            var pending = ZipUpdate.ReadPending(paths.UpdatesDir);
            if (pending != null)
            {
                var td0 = new TaskDialog("BIMBRIDGE · Update staged")
                {
                    MainInstruction = "An update to " + pending.Version + " is already waiting for Revit to close",
                    MainContent = "Status: " + pending.Estado + ". Staged on " + pending.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                                  (pending.Signed ? " (signed, key " + pending.KeyId + ")." : " (UNSIGNED, confirmed by " + pending.ConfirmedBy + ")."),
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td0.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Cancel the staged update");
                td0.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Choose another ZIP (replaces the staged one)");
                var a = td0.Show();
                if (a == TaskDialogResult.CommandLink1) { ZipUpdate.CancelPending(paths.UpdatesDir); Log("cancelada", pending.Version, null); return Result.Succeeded; }
                if (a != TaskDialogResult.CommandLink2) return Result.Cancelled;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "BIMBRIDGE update package", Filter = "BIMBRIDGE package (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            List<TrustedKey> keys;
            try { keys = KeysConfig.Select(TrustedKey.Parse).ToList(); }
            catch (FormatException ex) { TaskDialog.Show("BIMBRIDGE", "The update keys in config.json are not valid (" + ex.Message + "). No package can be validated."); return Result.Failed; }

            var info = new SetupCore(paths).ReadInstalled();
            var insp = ZipUpdate.Inspect(dlg.FileName, info?.ProductVersion ?? Updates.Format(UpdateService.Running), info?.Versions, keys, policy: Policy);
            if (!insp.Ok)
            {
                Log("rechazado_" + insp.Estado, insp.Version, insp);
                TaskDialog.Show("BIMBRIDGE · Package rejected", insp.Mensaje + (insp.Detalles.Count > 0 ? "\n\n" + string.Join("\n", insp.Detalles.Take(10)) : "") + "\n\nNothing was installed.");
                return Result.Failed;
            }

            var td = new TaskDialog("BIMBRIDGE · Update from ZIP")
            {
                MainInstruction = "Installed " + insp.Installed + "  →  new " + insp.Version,
                MainContent = "Commit: " + insp.Commit + "\nRevit: " + string.Join(", ", insp.Revit) + "\nVerified files: " + insp.Files.Count +
                              "\nZIP SHA-256: " + insp.ZipSha256.Substring(0, 16) + "…\n\n" + insp.Mensaje +
                              "\n\nIt will be staged now and installed when YOU close Revit. BIMBRIDGE does not close or restart Revit. If the installation cannot be verified, it goes back to " + insp.Installed + ".",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Stage the update");
            if (td.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;

            var unsignedOk = false;
            if (insp.NeedsUnsignedConfirmation)
            {
                var w = new TaskDialog("BIMBRIDGE · Unsigned package")
                {
                    MainInstruction = "This package has no provenance signature (LAB profile)",
                    MainContent = "It could only be checked that its files match its own manifest, not who produced it. Continue only if you received it from a source you know.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Cancel,
                };
                w.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "I understand the risk: install unsigned");
                if (w.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                unsignedOk = true;
            }

            try
            {
                var who = Environment.UserName + " (Revit dialog)";
                var p = ZipUpdate.Stage(insp, paths.UpdatesDir, who, unsignedOk, policy: Policy);
                Process.Start(new ProcessStartInfo(p.Installer, "/aplicar-pendiente") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.UpdatesDir });
                Log("preparada", p.Version, insp);
                TaskDialog.Show("BIMBRIDGE", "Update to " + p.Version + " staged and verified.\n\nIt will be installed when you close Revit (all Revit windows). Until then " + insp.Installed + " stays in use. The result will appear when you reopen Revit.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log("error_preparar", insp.Version, insp);
                TaskDialog.Show("BIMBRIDGE", "Could not stage: " + ex.Message + "\n\nNothing was installed.");
                return Result.Failed;
            }
        }

        /// <summary>At startup: if the external process left a new result, it is recorded and reported once.</summary>
        internal static string TakeResult()
        {
            var f = Path.Combine(UpdateService.Paths.UpdatesDir, ZipUpdate.ResultFile);
            try
            {
                if (!File.Exists(f)) return null;
                var o = Bimbridge.Protocol.Json.ParseObject(File.ReadAllText(f));
                File.Move(f, f + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".visto");
                var estado = Convert.ToString(o.TryGetValue("estado", out var e) ? e : null);
                Runtime.Audit?.Append(VersionRegistry.HumanAuthor, "actualizar_zip_resultado", estado, new Dictionary<string, object> { ["version"] = o.TryGetValue("version", out var v) ? v : null });
                return "Update from ZIP: " + estado + ". " + (o.TryGetValue("mensaje", out var m) ? m : "");
            }
            catch (Exception) { return null; }
        }

        private static void Log(string estado, string version, ZipInspection insp)
        {
            try
            {
                Runtime.Audit?.Append(VersionRegistry.HumanAuthor, "actualizar_zip", estado, new Dictionary<string, object>
                {
                    ["version"] = version, ["zip_sha256"] = insp?.ZipSha256, ["firmado"] = insp?.Signed, ["clave"] = insp?.KeyId, ["mensaje"] = insp?.Mensaje,
                });
                Runtime.Log("Update from ZIP: " + estado + " " + version);
            }
            catch (Exception) { }
        }
    }
}
