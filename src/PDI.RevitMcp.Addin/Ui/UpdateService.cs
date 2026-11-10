// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Setup;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Botón "Actualizar": busca instaladores nuevos (Descargas, Escritorio, carpeta de actualizaciones y las que configure IT)
    /// cada 10 minutos en un hilo de fondo, sin tocar Revit. Al pulsar Instalar ejecuta el instalador en modo /actualizar,
    /// que reemplaza los archivos aunque Revit esté abierto; la versión nueva entra al reiniciar Revit.
    /// Nunca descarga nada de internet ni instala sin que el usuario pulse el botón.
    /// </summary>
    internal static class UpdateService
    {
        public static readonly SetupPaths Paths = SetupPaths.Current();
        public static Version Running = new Version(0, 0, 0);
        public static volatile UpdateCandidate Available;
        public static volatile string InstalledVersion;
        public static volatile string PreviousVersion;
        public static PushButton Button;

        private static Timer _timer;
        private static List<string> _extraFolders = new List<string>();
        private static readonly object CheckLock = new object();
        private static string _lastVisual;

        public static void Start(EngineConfig config)
        {
            Running = Updates.Normalize(Assembly.GetExecutingAssembly().GetName().Version);
            _extraFolders = config?.UpdateFolders ?? new List<string>();
            // Lo que quedó apartado al actualizar con Revit abierto ya no está en uso: se borra.
            try
            {
                var n = Updates.DeleteLeftovers(Paths.LeftoverDirs(SetupCore.SupportedVersions));
                if (n > 0) Runtime.Log("Actualización: " + n + " archivos de la versión anterior borrados.");
            }
            catch (Exception) { }
            _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        public static bool RestartPending
        {
            get
            {
                var inst = Updates.Parse(InstalledVersion);
                return inst != null && inst > Running;
            }
        }

        public static List<string> Folders => Updates.Folders(Paths, _extraFolders);

        /// <summary>Copia de la versión anterior guardada por el instalador, para volver atrás.</summary>
        public static string PreviousSetup
        {
            get
            {
                var prev = PreviousVersion;
                if (string.IsNullOrEmpty(prev)) return null;
                var f = Paths.CachedSetup(prev);
                return File.Exists(f) ? f : null;
            }
        }

        /// <summary>Busca de nuevo. Rápido salvo la primera vez que ve un .zip nuevo (lo abre para leer la versión).</summary>
        public static void Check()
        {
            if (!Monitor.TryEnter(CheckLock)) return;
            try
            {
                var info = new SetupCore(Paths).ReadInstalled();
                InstalledVersion = info?.ProductVersion;
                PreviousVersion = info?.Previous;
                var baseline = Updates.Parse(InstalledVersion);
                if (baseline == null || baseline < Running) baseline = Running;
                var found = Updates.Scan(Folders, Paths.UpdatesTemp);
                Available = Updates.Newest(found, baseline);
            }
            catch (Exception ex)
            {
                Runtime.Log("Actualización: no se pudo buscar: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(CheckLock);
                Runtime.MarkDirty();
            }
        }

        /// <summary>
        /// Ejecuta el instalador en modo /actualizar y espera. Se copia antes a la carpeta de actualizaciones
        /// (así sirve para volver atrás y no depende del archivo en Descargas). Devuelve (éxito, mensaje).
        /// </summary>
        public static (bool ok, string text) Install(UpdateCandidate c, bool allowOlder)
        {
            try
            {
                Directory.CreateDirectory(Paths.UpdatesDir);
                var exe = Paths.CachedSetup(c.VersionText);
                if (!string.Equals(Path.GetFullPath(c.Exe), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                    File.Copy(c.Exe, exe, true);
                var report = Path.Combine(Paths.UpdatesDir, "ultima.txt");
                try { File.Delete(report); } catch (Exception) { }

                var psi = new ProcessStartInfo(exe, "/actualizar" + (allowOlder ? " /permitir-anterior" : ""))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Paths.UpdatesDir,
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(180000)) return (false, "El instalador no terminó en 3 minutos. Revise " + report + ".");
                    var text = File.Exists(report) ? File.ReadAllLines(report).Skip(2).FirstOrDefault() : null;
                    Runtime.Journal?.Log("usuario", "actualizar", p.ExitCode == 0 ? "ok" : "error", 0,
                        new Dictionary<string, object> { ["version"] = c.VersionText, ["codigo"] = (long)p.ExitCode, ["detalle"] = text });
                    Runtime.Log("Actualización a " + c.VersionText + ": código " + p.ExitCode + " " + text);
                    return (p.ExitCode == 0, text ?? "El instalador terminó con el código " + p.ExitCode + ".");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                Check();
            }
        }

        /// <summary>Se llama en Idling: cambia icono y texto del botón sólo si cambió el estado.</summary>
        public static void RefreshButton()
        {
            if (Button == null) return;
            var a = Available;
            var state = RestartPending ? 2 : a != null ? 1 : 0;
            var visual = state + "|" + (a?.VersionText ?? InstalledVersion ?? "");
            if (visual == _lastVisual) return;
            _lastVisual = visual;
            Button.LargeImage = Icons.Update(state, 32);
            Button.Image = Icons.Update(state, 16);
            Button.ItemText = state == 2 ? "Reiniciar\nRevit" : state == 1 ? "Actualizar\na " + a.VersionText : "Versión\n" + Updates.Format(Running);
            Button.ToolTip = state == 2
                ? "La versión " + InstalledVersion + " ya está instalada: cierre y abra Revit para usarla."
                : state == 1
                    ? "Hay una versión nueva (" + a.VersionText + "). Pulse para instalarla sin cerrar Revit."
                    : "Está al día. Pulse para buscar de nuevo o volver a la versión anterior.";
        }
    }
}
