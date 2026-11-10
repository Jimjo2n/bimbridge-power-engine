// SPDX-License-Identifier: GPL-3.0-only
using System;
using PDI.RevitMcp.Protocol;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Setup;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// 0.8.0: BIMBRIDGE → Developer Tools → Actualizar desde ZIP. Lo pulsa una persona; la IA no tiene ninguna herramienta que llegue aquí.
    /// Selector de archivo → inspección (manifiesto, firma, hashes, compatibilidad) → versiones y procedencia en pantalla → confirmación
    /// (doble si no está firmado) → preparación verificada → proceso externo que espera a que el usuario cierre Revit.
    /// Sin hot reload, sin cerrar ni reiniciar Revit, sin descargas.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ZipUpdateCommand : IExternalCommand
    {
        internal static List<string> KeysConfig = new List<string>();
        /// <summary>0.9.0: perfil vigente (producción por defecto: sólo paquetes firmados).</summary>
        internal static ProfilePolicy Policy = new ProfilePolicy();

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var paths = UpdateService.Paths;
            var pending = ZipUpdate.ReadPending(paths.UpdatesDir);
            if (pending != null)
            {
                var td0 = new TaskDialog("BIMBRIDGE · Actualización preparada")
                {
                    MainInstruction = "Ya hay una actualización a la " + pending.Version + " esperando a que cierre Revit",
                    MainContent = "Estado: " + pending.Estado + ". Preparada el " + pending.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                                  (pending.Signed ? " (firmada, clave " + pending.KeyId + ")." : " (SIN FIRMA, confirmada por " + pending.ConfirmedBy + ")."),
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td0.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Cancelar la actualización preparada");
                td0.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Elegir otro ZIP (reemplaza la preparada)");
                var a = td0.Show();
                if (a == TaskDialogResult.CommandLink1) { ZipUpdate.CancelPending(paths.UpdatesDir); Log("cancelada", pending.Version, null); return Result.Succeeded; }
                if (a != TaskDialogResult.CommandLink2) return Result.Cancelled;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Paquete de actualización de BIMBRIDGE", Filter = "Paquete BIMBRIDGE (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
            if (dlg.ShowDialog() != true) return Result.Cancelled;

            List<TrustedKey> keys;
            try { keys = KeysConfig.Select(TrustedKey.Parse).ToList(); }
            catch (FormatException ex) { TaskDialog.Show("BIMBRIDGE", "Las claves de actualización de config.json no son válidas (" + ex.Message + "). No se puede validar ningún paquete."); return Result.Failed; }

            var info = new SetupCore(paths).ReadInstalled();
            var insp = ZipUpdate.Inspect(dlg.FileName, info?.ProductVersion ?? Updates.Format(UpdateService.Running), info?.Versions, keys, policy: Policy);
            if (!insp.Ok)
            {
                Log("rechazado_" + insp.Estado, insp.Version, insp);
                TaskDialog.Show("BIMBRIDGE · Paquete rechazado", insp.Mensaje + (insp.Detalles.Count > 0 ? "\n\n" + string.Join("\n", insp.Detalles.Take(10)) : "") + "\n\nNo se instaló nada.");
                return Result.Failed;
            }

            var td = new TaskDialog("BIMBRIDGE · Actualizar desde ZIP")
            {
                MainInstruction = "Instalada " + insp.Installed + "  →  nueva " + insp.Version,
                MainContent = "Commit: " + insp.Commit + "\nRevit: " + string.Join(", ", insp.Revit) + "\nArchivos verificados: " + insp.Files.Count +
                              "\nSHA-256 del ZIP: " + insp.ZipSha256.Substring(0, 16) + "…\n\n" + insp.Mensaje +
                              "\n\nSe preparará ahora y se instalará cuando USTED cierre Revit. BIMBRIDGE no cierra ni reinicia Revit. Si la instalación no queda verificada, se vuelve a la " + insp.Installed + ".",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Preparar la actualización");
            if (td.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;

            var unsignedOk = false;
            if (insp.NeedsUnsignedConfirmation)
            {
                var w = new TaskDialog("BIMBRIDGE · Paquete sin firma")
                {
                    MainInstruction = "Este paquete no tiene firma de procedencia (perfil de LABORATORIO)",
                    MainContent = "Sólo se pudo comprobar que sus archivos coinciden con su propio manifiesto, no quién lo generó. Continúe únicamente si lo recibió de una fuente que conoce.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Cancel,
                };
                w.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Entiendo el riesgo: instalar sin firma");
                if (w.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                unsignedOk = true;
            }

            try
            {
                var who = Environment.UserName + " (diálogo de Revit)";
                var p = ZipUpdate.Stage(insp, paths.UpdatesDir, who, unsignedOk, policy: Policy);
                Process.Start(new ProcessStartInfo(p.Installer, "/aplicar-pendiente") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.UpdatesDir });
                Log("preparada", p.Version, insp);
                TaskDialog.Show("BIMBRIDGE", "Actualización a la " + p.Version + " preparada y verificada.\n\nSe instalará cuando cierre Revit (todas las ventanas de Revit). Hasta entonces sigue la " + insp.Installed + ". El resultado aparecerá al volver a abrir Revit.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log("error_preparar", insp.Version, insp);
                TaskDialog.Show("BIMBRIDGE", "No se pudo preparar: " + ex.Message + "\n\nNo se instaló nada.");
                return Result.Failed;
            }
        }

        /// <summary>Al arrancar: si el proceso externo dejó un resultado nuevo, se registra y se avisa una vez.</summary>
        internal static string TakeResult()
        {
            var f = Path.Combine(UpdateService.Paths.UpdatesDir, ZipUpdate.ResultFile);
            try
            {
                if (!File.Exists(f)) return null;
                var o = PDI.RevitMcp.Protocol.Json.ParseObject(File.ReadAllText(f));
                File.Move(f, f + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".visto");
                var estado = Convert.ToString(o.TryGetValue("estado", out var e) ? e : null);
                Runtime.Audit?.Append(VersionRegistry.HumanAuthor, "actualizar_zip_resultado", estado, new Dictionary<string, object> { ["version"] = o.TryGetValue("version", out var v) ? v : null });
                return "Actualizar desde ZIP: " + estado + ". " + (o.TryGetValue("mensaje", out var m) ? m : "");
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
                Runtime.Log("Actualizar desde ZIP: " + estado + " " + version);
            }
            catch (Exception) { }
        }
    }
}
