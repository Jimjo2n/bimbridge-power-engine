// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace PDI.RevitMcp.Setup
{
    /// <summary>Lo que sólo existe en Windows: procesos, registro de "Aplicaciones" y la CLI de Claude Code.</summary>
    internal static class Platform
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDI.RevitMcp";

        public static string ProductVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static Stream OpenPayload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

        public static bool RevitRunning() => !SetupPaths.IsSandbox && Process.GetProcessesByName("Revit").Length > 0;

        // ---------------- Claude Code ----------------

        /// <summary>Busca la CLI de Claude Code en los lugares habituales.</summary>
        public static string FindClaude()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(appData, "npm", "claude.cmd"),
            }.ToList();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>claude mcp add --scope user pdi-revit -- "bridge.exe" (quitando antes una versión vieja).</summary>
        public static bool RegisterClaude(string claude, string bridgeExe, out string detail)
        {
            if (SetupPaths.IsSandbox) { detail = "sandbox"; return false; }
            RunClaude(claude, "mcp remove --scope user pdi-revit", out _);
            var ok = RunClaude(claude, "mcp add --scope user pdi-revit -- \"" + bridgeExe + "\"", out detail);
            return ok;
        }

        public static void UnregisterClaude()
        {
            if (SetupPaths.IsSandbox) return;
            var claude = FindClaude();
            if (claude != null) RunClaude(claude, "mcp remove --scope user pdi-revit", out _);
        }

        private static bool RunClaude(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : exe,
                    Arguments = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "/c \"\"" + exe + "\" " + args + "\"" : args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true, // comando de configuración de un instante; no es un servidor
                };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } output = "no respondió en 30 s"; return false; }
                    output = (o.Result + " " + e.Result).Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        // ---------------- "Aplicaciones" de Windows ----------------

        public static void RegisterUninstall(SetupPaths p)
        {
            if (SetupPaths.IsSandbox) return;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "BIMBRIDGE - Power Engine");
                k.SetValue("DisplayVersion", ProductVersion);
                k.SetValue("Publisher", "BIMBRIDGE");
                k.SetValue("DisplayIcon", p.SetupCopy + ",0");
                k.SetValue("InstallLocation", p.Home);
                k.SetValue("UninstallString", "\"" + p.SetupCopy + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + p.SetupCopy + "\" /quiet /uninstall");
                k.SetValue("NoModify", 0, RegistryValueKind.DWord);
                k.SetValue("ModifyPath", "\"" + p.SetupCopy + "\"");
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 75000, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        public static void UnregisterUninstall()
        {
            if (SetupPaths.IsSandbox) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        }

        /// <summary>Guarda una copia del instalador para "Modificar" y "Desinstalar" desde Aplicaciones.</summary>
        public static void KeepSetupCopy(SetupPaths p)
        {
            var self = Assembly.GetExecutingAssembly().Location;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(p.SetupCopy), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(p.Home);
            CopyOver(self, p.SetupCopy);
        }

        /// <summary>Último aviso no fatal de AfterInstall (p. ej. la copia para Aplicaciones no se pudo renovar). Null si no hubo.</summary>
        public static string LastWarning;

        public static void CopyOver(string from, string to) => SetupCore.CopyOver(from, to);

        /// <summary>
        /// Pasos comunes después de instalar (ventana, /quiet y /actualizar): copia para Aplicaciones, entrada en Aplicaciones,
        /// copia en la carpeta de actualizaciones (actual + anterior) y papelera para instaladores viejos que quedaron en Descargas
        /// o en el Escritorio. Devuelve qué se mandó a la papelera.
        /// </summary>
        public static List<string> AfterInstall(SetupCore core, SetupPaths p, InstalledInfo before, IEnumerable<string> extraFolders = null)
        {
            var self = Assembly.GetExecutingAssembly().Location;
            var prev = before != null && before.ProductVersion != ProductVersion ? before.ProductVersion : before?.Previous;
            // Primera vez con actualizador: la copia de Aplicaciones aún es la versión anterior; se guarda para poder volver.
            try
            {
                if (prev != null && !File.Exists(p.CachedSetup(prev)) && File.Exists(p.SetupCopy) &&
                    Updates.Format(Updates.ReadVersion(p.SetupCopy)) == prev)
                {
                    Directory.CreateDirectory(p.UpdatesDir);
                    CopyOver(p.SetupCopy, p.CachedSetup(prev));
                }
            }
            catch (Exception) { }
            LastWarning = null;
            // La instalación ya terminó: si la copia para Aplicaciones falla, se avisa y no se da por fallida la instalación.
            try { KeepSetupCopy(p); }
            catch (Exception e) { LastWarning = "No se pudo renovar la copia del instalador en " + p.SetupCopy + ": " + e.Message; }
            RegisterUninstall(p);
            var recycled = new List<string>();
            try
            {
                core.CacheSetup(self, ProductVersion, prev);
                var found = Updates.Scan(Updates.Folders(p, extraFolders).Where(f => !string.Equals(f, p.UpdatesDir, StringComparison.OrdinalIgnoreCase)), p.UpdatesTemp);
                foreach (var f in Updates.Superseded(found, Updates.Parse(ProductVersion), p.UpdatesDir))
                    if (Recycle(f)) recycled.Add(f);
            }
            catch (Exception) { }
            return recycled;
        }

        /// <summary>A la papelera de reciclaje (se puede recuperar), nunca borrado definitivo. Carpetas incluidas.</summary>
        public static bool Recycle(string path)
        {
            try
            {
                if (File.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else return false;
                // Si quedó vacía la carpeta que dejó "Extraer todo" (PDI.RevitMcp.Setup_0.2.1\), también se recicla.
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Path.GetFileName(dir).StartsWith(Updates.Prefix, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>El .exe no se puede borrar a sí mismo mientras corre: se borra unos segundos después de cerrar.</summary>
        public static void DeleteSetupCopyLater(SetupPaths p)
        {
            try
            {
                if (!File.Exists(p.SetupCopy)) return;
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & del /f /q \"" + p.SetupCopy + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception) { }
        }
    }

    /// <summary>Modo /quiet para IT (Intune, SCCM, script de inicio de sesión).</summary>
    internal static class Quiet
    {
        public static int Run(CommandLine c)
        {
            var paths = SetupPaths.Current();
            var core = new SetupCore(paths, Console.WriteLine);
            try
            {
                if (c.Diagnose)
                {
                    // 0.6.0: diagnóstico compacto (instalada / cargada / puente / hashes / reinicio pendiente). Sólo lee.
                    var text = PDI.RevitMcp.Protocol.Json.Write(core.Diagnose());
                    Directory.CreateDirectory(paths.Home);
                    File.WriteAllText(Path.Combine(paths.Home, "diagnostico.json"), text);
                    Console.WriteLine(text);
                    return 0;
                }
                if (c.Restore) return Restore(c, paths);
                if (c.Update) return Update(c, paths, core);
                if (c.ApplyPending) return ApplyPending(paths, core);
                if (Platform.RevitRunning() && c.Uninstall) { Console.Error.WriteLine("Revit está abierto."); return 2; }
                if (c.Uninstall)
                {
                    core.Uninstall(c.RemoveData);
                    Platform.UnregisterClaude();
                    Platform.UnregisterUninstall();
                    Platform.DeleteSetupCopyLater(paths);
                    return 0;
                }
                var before = core.ReadInstalled();
                // 0.6.0: sin revit=/niveles= se conservan la elección y los permisos de la instalación anterior (p. ej. RPW).
                var versions = c.Versions ?? (before != null && before.Versions.Count > 0 ? before.Versions.ToArray() : core.DetectRevit().Where(r => r.Installed).Select(r => r.Version).ToArray());
                if (versions.Length == 0) { Console.Error.WriteLine("No se encontró Revit 2022-2026."); return 1; }
                // Sin hot reload: los binarios sólo cambian con Revit cerrado.
                if (Platform.RevitRunning()) { Console.Error.WriteLine("Revit está abierto: ciérrelo antes de instalar. No se cambió nada."); return 2; }
                // Nunca sobre una instalación existente sin pedirlo; podría ser la de trabajo.
                var conflicts = core.DetectConflicts(Platform.ProductVersion);
                if (conflicts.Count > 0 && !c.Replace)
                {
                    Console.Error.WriteLine("No se instaló nada: " + string.Join("; ", conflicts) + ". Si este perfil es de laboratorio y quiere reemplazarla, repita con /reemplazar (se crea un respaldo).");
                    return 5;
                }
                using (var zip = new System.IO.Compression.ZipArchive(Platform.OpenPayload()))
                    core.Install(zip, new InstallOptions { Versions = versions.ToList(), Levels = c.Levels ?? before?.Levels ?? "R", Codex = c.Codex || (before?.Codex ?? false), AllowInUse = false, RequireManifest = true }, Platform.ProductVersion);
                if (c.Claude)
                {
                    var claude = Platform.FindClaude();
                    if (claude != null) Platform.RegisterClaude(claude, paths.BridgeExe, out _);
                }
                Platform.AfterInstall(core, paths, before);
                if (Platform.LastWarning != null) Console.Error.WriteLine("Aviso: " + Platform.LastWarning);
                if (core.ReplacedInUse > 0) Console.WriteLine("Revit o una conversación de la IA estaba abierta: lo nuevo entra al reiniciarlos.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int Restore(CommandLine c, SetupPaths paths)
        {
            if (Platform.RevitRunning() && !SetupPaths.IsSandbox) { Console.Error.WriteLine("Revit está abierto: ciérrelo antes de restaurar."); return 2; }
            var dir = c.RestoreFrom ?? InstallBackup.Latest(paths);
            if (dir == null) { Console.Error.WriteLine("No hay ningún respaldo de instalación."); return 1; }
            var undo = InstallBackup.Restore(paths, dir, DateTime.UtcNow);
            if (!SetupPaths.IsSandbox && !File.Exists(paths.InstallRecord)) { Platform.UnregisterClaude(); Platform.UnregisterUninstall(); }
            Console.WriteLine("Restaurado desde " + dir);
            Console.WriteLine("Para deshacer: /restaurar=\"" + undo + "\"");
            return 0;
        }

        /// <summary>
        /// /actualizar: lo lanza el botón Actualizar de la cinta. Usa las mismas opciones de la instalación anterior
        /// (versiones de Revit, permisos, Codex; Claude ya apunta al mismo puente) y funciona con Revit abierto.
        /// Códigos: 0 bien, 1 error, 3 no hay instalación previa, 4 ya hay una versión igual o más nueva.
        /// El resultado queda en actualizaciones\ultima.txt para que la cinta lo muestre.
        /// </summary>
        private static int Update(CommandLine c, SetupPaths paths, SetupCore core)
        {
            var before = core.ReadInstalled();
            if (before == null || before.Versions.Count == 0) return Report(paths, 3, "No hay una instalación previa: use el instalador normal.");
            if (Platform.RevitRunning()) return Report(paths, 2, "Revit está abierto: ciérrelo antes de actualizar. No se cambió nada.");
            var mine = Updates.Parse(Platform.ProductVersion);
            var installed = Updates.Parse(before.ProductVersion);
            if (installed != null && mine <= installed && !c.AllowOlder)
                return Report(paths, 4, "Ya está instalada la versión " + before.ProductVersion + ".");
            using (var zip = new System.IO.Compression.ZipArchive(Platform.OpenPayload()))
                core.Install(zip, new InstallOptions { Versions = before.Versions, Levels = before.Levels ?? "R", Codex = before.Codex, AllowInUse = false, RequireManifest = true }, Platform.ProductVersion);
            var recycled = Platform.AfterInstall(core, paths, before);
            var msg = "Instalada la versión " + Platform.ProductVersion + " (antes " + before.ProductVersion + ")." +
                      (core.ReplacedInUse > 0 ? " " + core.ReplacedInUse + " archivo(s) estaban en uso: la versión nueva se carga al reiniciar Revit y la conversación de la IA; hasta entonces sigue la anterior." : "") +
                      (recycled.Count > 0 ? " Instaladores viejos enviados a la papelera: " + recycled.Count + "." : "") +
                      (Platform.LastWarning != null ? " Aviso: " + Platform.LastWarning : "");
            return Report(paths, 0, msg);
        }

        /// <summary>
        /// 0.8.0: aplica la actualización preparada desde "Actualizar desde ZIP". Espera hasta 24 h a que no quede ningún Revit abierto;
        /// no cierra ni mata Revit. El instalador preparado se ejecuta como proceso aparte con /actualizar y luego se verifica.
        /// </summary>
        private static int ApplyPending(SetupPaths paths, SetupCore core)
        {
            var host = new ApplyHost
            {
                RevitRunning = Platform.RevitRunning,
                Sleep = t => System.Threading.Thread.Sleep(t),
                RunInstaller = (exe, args) =>
                {
                    using (var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.UpdatesDir }))
                    {
                        if (!pr.WaitForExit(600000)) return 124;
                        return pr.ExitCode;
                    }
                },
                ReadInstalled = core.ReadInstalled,
                Diagnose = core.Diagnose,
                CachedSetup = v => { var f = paths.CachedSetup(v); return File.Exists(f) ? f : null; },
                Log = Console.WriteLine,
            };
            // 0.9.0: claves y perfil vigentes (no los de pendiente.json) para re-verificar la procedencia justo antes de instalar.
            UpdateConfig.Load(UpdateConfig.DefaultPath, out var keys, out var policy);
            host.Keys = () => keys;
            host.Policy = () => policy;
            var r = PendingApplier.Run(paths.UpdatesDir, host, TimeSpan.FromHours(24));
            var estado = Convert.ToString(r["estado"]);
            return Report(paths, estado == "aplicada_verificada" ? 0 : 1, "ZIP: " + estado + " — " + r["mensaje"]);
        }

        private static int Report(SetupPaths p, int code, string text)
        {
            try
            {
                Directory.CreateDirectory(p.UpdatesDir);
                File.WriteAllText(Path.Combine(p.UpdatesDir, "ultima.txt"), code + "\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n" + text);
            }
            catch (Exception) { }
            (code == 0 ? Console.Out : Console.Error).WriteLine(text);
            return code;
        }
    }
}
