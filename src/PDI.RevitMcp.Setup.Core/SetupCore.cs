// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Setup
{
    /// <summary>Carpetas del usuario. En pruebas se apuntan a temporales.</summary>
    public sealed class SetupPaths
    {
        public string AppData;        // %APPDATA%
        public string LocalAppData;   // %LOCALAPPDATA%
        public string UserProfile;    // %USERPROFILE%
        public string ProgramFiles;   // C:\Program Files
        public string Desktop;        // Escritorio (puede estar en OneDrive)

        /// <summary>Pruebas offline: con BIMBRIDGE_SETUP_SANDBOX=carpeta todas las rutas caen dentro de ella; ni registro de Windows ni Claude.</summary>
        public static string Sandbox => Environment.GetEnvironmentVariable("BIMBRIDGE_SETUP_SANDBOX");
        public static bool IsSandbox => !string.IsNullOrEmpty(Sandbox);

        public static SetupPaths Current() => IsSandbox ? new SetupPaths
        {
            AppData = Path.Combine(Sandbox, "Roaming"), LocalAppData = Path.Combine(Sandbox, "Local"), UserProfile = Path.Combine(Sandbox, "User"),
            ProgramFiles = Path.Combine(Sandbox, "PF"), Desktop = Path.Combine(Sandbox, "Desktop"),
        } : new SetupPaths
        {
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        public string Home => Path.Combine(LocalAppData, "PDI.RevitMcp");
        public string BinDir => Path.Combine(Home, "bin");
        public string BridgeExe => Path.Combine(BinDir, "PDI.RevitMcp.Bridge.exe");
        public string SetupCopy => Path.Combine(Home, "PDI.RevitMcp.Setup.exe");
        public string InstallRecord => Path.Combine(Home, "install.json");
        public string ConfigFile => Path.Combine(AppData, "PDI.RevitMcp", "config.json");
        public string AddinsDir(string version) => Path.Combine(AppData, "Autodesk", "Revit", "Addins", version);
        public string CodexConfig => Path.Combine(UserProfile, ".codex", "config.toml");
        public string Downloads => Path.Combine(UserProfile, "Downloads");
        /// <summary>Copias de los instaladores (actual y anterior) para "Volver a la versión anterior", y la carpeta temporal.</summary>
        public string UpdatesDir => Path.Combine(Home, "actualizaciones");
        public string UpdatesTemp => Path.Combine(UpdatesDir, "tmp");
        public string CachedSetup(string version) => Path.Combine(UpdatesDir, "PDI.RevitMcp.Setup-" + version + ".exe");
        /// <summary>Carpetas donde puede haber archivos renombrados *.viejo-* al reemplazar algo en uso.</summary>
        public IEnumerable<string> LeftoverDirs(IEnumerable<string> versions)
        {
            yield return BinDir;
            foreach (var v in versions) yield return Path.Combine(AddinsDir(v), "PDI.RevitMcp");
        }
    }

    public sealed class RevitInstall
    {
        public string Version;
        public bool Installed;   // Revit.exe encontrado
        public string Path;
    }

    public sealed class InstallOptions
    {
        public List<string> Versions = new List<string>();
        /// <summary>R, RP o RPW.</summary>
        public string Levels = "RPW";
        public bool Codex;
        /// <summary>
        /// true (modo /actualizar): si un archivo está en uso por Revit o por una conversación de la IA, se renombra a *.viejo-*
        /// y se pone el nuevo al lado. Windows permite renombrar un .dll/.exe cargado aunque no borrarlo. Lo nuevo entra al reiniciar.
        /// </summary>
        public bool AllowInUse;
        /// <summary>true: sin manifiesto no se instala nada.</summary>
        public bool RequireManifest;
    }

    public sealed class InstalledInfo
    {
        public string ProductVersion;
        public List<string> Versions = new List<string>();
        public string Levels;
        public DateTime InstalledUtc;
        public bool Codex;
        /// <summary>Versión que había antes de la última actualización (para volver atrás).</summary>
        public string Previous;
    }

    /// <summary>
    /// Instala, actualiza y desinstala BIMBRIDGE - Power Engine para el usuario actual, sin permisos de administrador:
    ///   add-in  → %APPDATA%\Autodesk\Revit\Addins\&lt;versión&gt;\PDI.RevitMcp.addin + PDI.RevitMcp\
    ///   puente  → %LOCALAPPDATA%\PDI.RevitMcp\bin\PDI.RevitMcp.Bridge.exe
    ///   config  → %APPDATA%\PDI.RevitMcp\config.json (sólo cambia "levels"; el resto se conserva)
    ///   Codex   → sección [mcp_servers.pdi-revit] en ~\.codex\config.toml (opcional)
    /// Claude Code y el registro de Windows los maneja el .exe (necesitan procesos y registro).
    /// </summary>
    public sealed class SetupCore
    {
        public static readonly string[] SupportedVersions = { "2022", "2023", "2024", "2025", "2026" };
        public const string CodexSection = "[mcp_servers.pdi-revit]";

        private readonly SetupPaths _p;
        private readonly Action<string> _log;

        public SetupCore(SetupPaths paths, Action<string> log = null)
        {
            _p = paths;
            _log = log ?? (_ => { });
        }

        public SetupPaths Paths => _p;

        // ---------------- detección ----------------

        public List<RevitInstall> DetectRevit()
        {
            var list = new List<RevitInstall>();
            foreach (var v in SupportedVersions)
            {
                var dir = Path.Combine(_p.ProgramFiles ?? "", "Autodesk", "Revit " + v);
                list.Add(new RevitInstall { Version = v, Path = dir, Installed = File.Exists(Path.Combine(dir, "Revit.exe")) });
            }
            return list;
        }

        public bool CodexPresent => Directory.Exists(Path.GetDirectoryName(_p.CodexConfig));

        public InstalledInfo ReadInstalled()
        {
            try
            {
                if (!File.Exists(_p.InstallRecord)) return null;
                var o = Json.ParseObject(File.ReadAllText(_p.InstallRecord));
                var info = new InstalledInfo
                {
                    ProductVersion = Arg.GetString(o, "version"),
                    Levels = Arg.GetString(o, "levels"),
                    Versions = Arg.GetStringList(o, "revit"),
                    Codex = Arg.GetBool(o, "codex") ?? false,
                    Previous = Arg.GetString(o, "previous"),
                };
                DateTime.TryParse(Arg.GetString(o, "installed_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out info.InstalledUtc);
                return info;
            }
            catch (Exception) { return null; }
        }

        // ---------------- instalar ----------------

        /// <summary>Copia lo necesario desde el paquete (zip). Lanza IOException con un mensaje claro si un archivo está en uso.</summary>
        /// <summary>Respaldo creado por la última instalación (null si no había nada instalado).</summary>
        public string LastBackup { get; private set; }

        public void Install(ZipArchive payload, InstallOptions o, string productVersion)
        {
            if (o.Versions.Count == 0) throw new ArgumentException("elija al menos una versión de Revit");
            var levels = (o.Levels ?? "R").ToUpperInvariant();
            if (levels != "R" && levels != "RP" && levels != "RPW") throw new ArgumentException("niveles no válidos: " + levels);

            var previous = ReadInstalled();
            // 0.6.0: el paquete trae manifest.json con el SHA-256 de cada archivo; si no coincide, no se instala nada.
            var manifest = ReadPayloadManifest(payload);
            if (manifest == null && o.RequireManifest) throw new InvalidOperationException("el paquete no trae manifest.json: no se puede verificar su integridad y no se instaló nada");
            if (manifest != null) VerifyPayload(payload, manifest, o.Versions);
            // Native Validation: antes de tocar nada se respalda lo instalado, para poder volver (Setup.exe /restaurar).
            LastBackup = previous == null && !Directory.Exists(_p.BinDir) ? null
                : InstallBackup.Create(_p, o.Versions.Concat(previous?.Versions ?? new List<string>()).Distinct(), "antes de instalar " + productVersion, DateTime.UtcNow);
            // Antes de tocar nada: si el puente está en uso y no se permite renombrar, se detiene aquí sin cambios.
            if (!o.AllowInUse && File.Exists(_p.BridgeExe))
            {
                try { using (new FileStream(_p.BridgeExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException)
                {
                    throw new IOException("No se cambió nada: " + _p.BridgeExe + " está en uso por una conversación de Claude o Codex. Ciérrela y repita.");
                }
            }
            InstallBackup.Prune(_p);
            try { InstallSteps(payload, o, productVersion, levels, previous, manifest); }
            catch (Exception ex) when (LastBackup != null && (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is InvalidDataException))
            {
                // Atómica desde fuera: un fallo a mitad vuelve al respaldo previo en vez de dejar archivos mezclados.
                try { InstallBackup.Restore(_p, LastBackup, DateTime.UtcNow); }
                catch (Exception undo) { throw new IOException("La instalación falló (" + ex.Message + ") y no se pudo volver al respaldo (" + undo.Message + "). Use /restaurar=\"" + LastBackup + "\".", ex); }
                throw new IOException("La instalación falló y se volvió al estado anterior: " + ex.Message, ex);
            }
        }

        private void InstallSteps(ZipArchive payload, InstallOptions o, string productVersion, string levels, InstalledInfo previous, Dictionary<string, string> manifest)
        {
            _installed.Clear();
            Updates.DeleteLeftovers(_p.LeftoverDirs(SupportedVersions));
            _allowInUse = o.AllowInUse;
            _replacedInUse = 0;

            // 1) Add-in por versión
            foreach (var v in o.Versions)
            {
                var prefix = "addin/" + v + "/";
                // Compress-Archive de PowerShell 5 guarda rutas con "\": se normalizan.
                var entries = payload.Entries.Where(e => Norm(e).StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
                if (entries.Count == 0) throw new InvalidOperationException("el paquete no trae el add-in para Revit " + v);
                var dest = _p.AddinsDir(v);
                foreach (var e in entries)
                {
                    var target = Path.Combine(dest, Norm(e).Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar));
                    Extract(e, target, "Revit " + v);
                    Record(Norm(e), target, manifest);
                }
                _log("Add-in instalado para Revit " + v);
            }
            // Versiones que estaban y ya no se eligieron: se quitan.
            if (previous != null)
                foreach (var v in previous.Versions.Where(x => !o.Versions.Contains(x)))
                {
                    RemoveAddin(v);
                    _log("Add-in quitado de Revit " + v);
                }

            // 2) Puente
            var bridge = payload.Entries.FirstOrDefault(e => Norm(e) == "bridge/PDI.RevitMcp.Bridge.exe") ?? throw new InvalidOperationException("el paquete no trae el puente");
            Extract(bridge, _p.BridgeExe, "el puente (¿hay una conversación de Claude o Codex abierta?)");
            Record(Norm(bridge), _p.BridgeExe, manifest);
            // 0.9.0: AgentHost y Power Engine junto al puente.
            var peFiles = 0;
            foreach (var e in payload.Entries)
            {
                var n = Norm(e);
                if (!n.StartsWith("bridge/", StringComparison.Ordinal) || n == "bridge/PDI.RevitMcp.Bridge.exe" || n.EndsWith("/", StringComparison.Ordinal)) continue;
                var rel = n.Substring("bridge/".Length);
                if (rel.Split('/').Any(x => x == ".." || x.Length == 0)) throw new InvalidOperationException("ruta inválida en el paquete: " + n);
                var target = Path.Combine(_p.BinDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Extract(e, target, "el Power Engine");
                Record(n, target, manifest);
                peFiles++;
            }
            _log("Puente instalado en " + _p.BinDir + (peFiles > 0 ? " (Power Engine: " + peFiles + " archivos)" : ""));

            // 3) Configuración: sólo "levels" y "enabled"; lo demás se conserva.
            WriteConfig(levels);
            _log("Permisos de la IA: " + LevelsText(levels));

            // 4) Codex
            if (o.Codex) { RegisterCodex(); _log("Conectado con Codex"); }
            else if (previous != null && previous.Codex) { UnregisterCodex(); }

            // 5) Registro de la instalación
            var record = new Dictionary<string, object>
            {
                ["version"] = productVersion,
                ["revit"] = o.Versions.Cast<object>().ToList(),
                ["levels"] = levels,
                ["codex"] = o.Codex,
                ["previous"] = previous == null ? null
                    : previous.ProductVersion != productVersion ? previous.ProductVersion : previous.Previous,
                ["installed_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                // 0.6.0: hash de cada archivo instalado (relativo a su carpeta raíz) para el diagnóstico posterior.
                ["archivos"] = _installed.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                ["manifiesto_paquete"] = manifest != null,
                ["reemplazados_en_uso"] = _replacedInUse,
            };
            Directory.CreateDirectory(_p.Home);
            File.WriteAllText(_p.InstallRecord, Json.Write(record));
        }


        // ---------------- verificación por hash y diagnóstico (0.6.0) ----------------

        public const string PayloadManifest = "manifest.json";
        private readonly Dictionary<string, string> _installed = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Sha256(Stream s)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Copia sobrescribiendo aunque el destino viejo esté marcado como sólo lectura (causa del "Acceso denegado" de alpha3).</summary>
        public static void CopyOver(string from, string to)
        {
            if (File.Exists(to))
            {
                var at = File.GetAttributes(to);
                if ((at & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, at & ~FileAttributes.ReadOnly);
            }
            File.Copy(from, to, true);
        }

        public static string FileSha(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return Sha256(f);
        }

        public static Dictionary<string, string> ReadPayloadManifest(ZipArchive payload)
        {
            var m = payload.Entries.FirstOrDefault(e => Norm(e) == PayloadManifest);
            if (m == null) return null;
            using (var r = new StreamReader(m.Open(), Encoding.UTF8))
            {
                var o = Json.ParseObject(r.ReadToEnd());
                if (!(o.TryGetValue("archivos", out var a) && a is Dictionary<string, object> files)) throw new InvalidDataException("manifest.json sin 'archivos'");
                if (o.ContainsKey("requeridos"))
                {
                    var required = Arg.GetStringList(o, "requeridos");
                    var present = new HashSet<string>(payload.Entries.Where(e => e.Name.Length > 0 && e.Length > 0).Select(Norm), StringComparer.Ordinal);
                    if (required.Count == 0 || required.Any(n => !present.Contains(n) || !files.ContainsKey(n)))
                        throw new InvalidDataException("faltan componentes obligatorios del payload");
                }
                return files.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
            }
        }

        /// <summary>Cada archivo que se va a instalar debe estar en el manifiesto y coincidir su SHA-256; si no, InvalidDataException (no se toca nada).</summary>
        public static void VerifyPayload(ZipArchive payload, Dictionary<string, string> manifest, IEnumerable<string> versions)
        {
            var bad = new List<string>();
            var wanted = versions.Select(v => "addin/" + v + "/").ToList();
            bool Selected(string n) => n.StartsWith("bridge/", StringComparison.Ordinal) || wanted.Any(w => n.StartsWith(w, StringComparison.Ordinal));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in payload.Entries.Where(x => x.Name.Length > 0))
            {
                var n = Norm(e);
                if (!Selected(n)) continue;
                if (!seen.Add(n) || n.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.Contains(":")))
                {
                    bad.Add(n + " (ruta inválida o duplicada)");
                    continue;
                }
                string sha;
                using (var s = e.Open()) sha = Sha256(s);
                if (!manifest.TryGetValue(n, out var exp)) bad.Add(n + " (no está en el manifiesto)");
                else if (!string.Equals(exp, sha, StringComparison.OrdinalIgnoreCase)) bad.Add(n + " (hash distinto)");
            }
            foreach (var expected in manifest.Keys.Where(Selected))
                if (!seen.Contains(expected)) bad.Add(expected + " (ausente en el payload)");
            if (bad.Count > 0) throw new InvalidDataException("el paquete no coincide con su manifiesto; no se instaló nada: " + string.Join("; ", bad.Take(5)) + (bad.Count > 5 ? " …" : ""));
        }

        private void Record(string entry, string target, Dictionary<string, string> manifest)
        {
            var sha = FileSha(target);
            if (manifest != null && manifest.TryGetValue(entry, out var exp) && !string.Equals(exp, sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("tras copiar, " + entry + " no coincide con el manifiesto (disco o antivirus); vuelva a instalar");
            _installed[entry] = sha;
        }

        /// <summary>
        /// Diagnóstico compacto: versión instalada, por año si los archivos coinciden con lo instalado, si hay un reinicio pendiente
        /// (archivos apartados *.viejo-*), versión cargada en cada Revit abierto (por su descriptor) y el puente. Sólo lee.
        /// </summary>
        public Dictionary<string, object> Diagnose()
        {
            var info = ReadInstalled();
            Dictionary<string, object> files = null;
            try { if (File.Exists(_p.InstallRecord)) files = Json.ParseObject(File.ReadAllText(_p.InstallRecord)).TryGetValue("archivos", out var a) ? a as Dictionary<string, object> : null; }
            catch (Exception) { }
            var res = new Dictionary<string, object>
            {
                ["instalada"] = info?.ProductVersion, ["anterior"] = info?.Previous, ["revit_elegidos"] = info?.Versions?.Cast<object>().ToList(),
            };
            var years = new Dictionary<string, object>();
            foreach (var v in SupportedVersions)
            {
                var dir = _p.AddinsDir(v);
                var manifestFile = Path.Combine(dir, "PDI.RevitMcp.addin");
                if (!File.Exists(manifestFile)) { years[v] = "no instalado"; continue; }
                var diff = new List<object>();
                var n = 0;
                if (files != null)
                    foreach (var kv in files.Where(k => k.Key.StartsWith("addin/" + v + "/", StringComparison.Ordinal)))
                    {
                        n++;
                        var path = Path.Combine(dir, kv.Key.Substring(("addin/" + v + "/").Length).Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(path)) diff.Add(kv.Key + " falta");
                        else if (!string.Equals(FileSha(path), Convert.ToString(kv.Value), StringComparison.OrdinalIgnoreCase)) diff.Add(kv.Key + " cambió");
                    }
                var pending = Directory.Exists(Path.Combine(dir, "PDI.RevitMcp"))
                    && Directory.GetFiles(Path.Combine(dir, "PDI.RevitMcp"), "*" + Updates.LeftoverMark + "*", SearchOption.AllDirectories).Length > 0;
                years[v] = new Dictionary<string, object>
                {
                    ["archivos"] = n, ["coinciden"] = files == null ? (object)"sin registro de hashes (instalación anterior a 0.6.0)" : diff.Count == 0,
                    ["diferencias"] = diff, ["reinicio_pendiente"] = pending,
                };
            }
            res["addin"] = years;
            var bridgeOk = File.Exists(_p.BridgeExe);
            object bridgeMatch = null;
            if (bridgeOk && files != null && files.TryGetValue("bridge/PDI.RevitMcp.Bridge.exe", out var bs))
                bridgeMatch = string.Equals(FileSha(_p.BridgeExe), Convert.ToString(bs), StringComparison.OrdinalIgnoreCase);
            res["puente"] = new Dictionary<string, object>
            {
                ["existe"] = bridgeOk, ["coincide"] = bridgeMatch,
                ["reinicio_pendiente"] = Directory.Exists(_p.BinDir) && Directory.GetFiles(_p.BinDir, "*" + Updates.LeftoverMark + "*").Length > 0,
            };
            var loaded = new List<object>();
            var inst = Path.Combine(_p.Home, "instances");
            if (Directory.Exists(inst))
                foreach (var f in Directory.GetFiles(inst, "*.json"))
                {
                    try
                    {
                        var d = InstanceDescriptor.FromJson(File.ReadAllText(f));
                        bool alive;
                        try { alive = !System.Diagnostics.Process.GetProcessById(d.Pid).HasExited; } catch (Exception) { alive = false; }
                        if (!alive) continue;
                        loaded.Add(new Dictionary<string, object>
                        {
                            ["revit"] = d.RevitVersion, ["pid"] = d.Pid, ["addin_cargado"] = d.AddinVersion, ["desde"] = d.StartedUtc,
                            ["coincide_con_instalada"] = info?.ProductVersion != null && d.AddinVersion != null && d.AddinVersion.StartsWith(info.ProductVersion, StringComparison.Ordinal),
                        });
                    }
                    catch (Exception) { }
                }
            res["revit_abiertos"] = loaded;
            try
            {
                if (File.Exists(_p.ConfigFile))
                {
                    var c = Json.ParseObject(File.ReadAllText(_p.ConfigFile));
                    res["config"] = new Dictionary<string, object> { ["enabled"] = Arg.GetBool(c, "enabled"), ["levels"] = Arg.GetString(c, "levels"), ["runner"] = Arg.GetString(c, "runner") ?? "auto" };
                }
            }
            catch (Exception ex) { res["config"] = "ilegible: " + ex.Message; }
            return res;
        }

        private static string Norm(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');

        public static string LevelsText(string levels)
        {
            switch (levels)
            {
                case "R": return "sólo leer el modelo";
                case "RP": return "leer y proponer cambios; todo cambio espera en la cola";
                default: return "leer y cambiar, con su aprobación (En vivo o Retener)";
            }
        }

        private bool _allowInUse;
        private int _replacedInUse;

        /// <summary>Cuántos archivos estaban en uso y se reemplazaron por renombre en la última instalación (entran al reiniciar).</summary>
        public int ReplacedInUse => _replacedInUse;

        private void Extract(ZipArchiveEntry e, string dest, string owner)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var tmp = dest + ".nuevo";
            using (var src = e.Open())
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (_allowInUse)
                {
                    try
                    {
                        // En uso: se aparta con otro nombre (permitido para archivos cargados) y el nuevo ocupa su lugar.
                        var aside = dest + Updates.LeftoverMark + DateTime.UtcNow.Ticks;
                        File.Move(dest, aside);
                        File.Move(tmp, dest);
                        _replacedInUse++;
                        return;
                    }
                    catch (Exception) { }
                }
                try { File.Delete(tmp); } catch (Exception) { }
                throw new IOException("No se pudo reemplazar " + Path.GetFileName(dest) + ": está en uso por " + owner + ". Ciérrelo y vuelva a intentar.", ex);
            }
        }

        /// <summary>
        /// Guarda una copia del instalador en la carpeta de actualizaciones y deja sólo la actual y la anterior
        /// (la anterior sirve para "Volver a la versión anterior"). Devuelve las copias borradas.
        /// </summary>
        public List<string> CacheSetup(string selfExe, string productVersion, string previousVersion)
        {
            var removed = new List<string>();
            Directory.CreateDirectory(_p.UpdatesDir);
            var target = _p.CachedSetup(productVersion);
            if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                CopyOver(selfExe, target);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
            if (!string.IsNullOrEmpty(previousVersion)) keep.Add(_p.CachedSetup(previousVersion));
            foreach (var f in Directory.GetFiles(_p.UpdatesDir, "PDI.RevitMcp.Setup-*.exe"))
                if (!keep.Contains(f) && Updates.TryDelete(f)) removed.Add(f);
            // Temporales de .zip ya revisados
            if (Directory.Exists(_p.UpdatesTemp))
                foreach (var f in Directory.GetFiles(_p.UpdatesTemp)) Updates.TryDelete(f);
            return removed;
        }

        public void WriteConfig(string levels)
        {
            Dictionary<string, object> o = null;
            if (File.Exists(_p.ConfigFile))
            {
                try { o = Json.ParseObject(File.ReadAllText(_p.ConfigFile)); }
                catch (Exception) { File.Copy(_p.ConfigFile, _p.ConfigFile + ".danado", true); }
            }
            // F19: una actualización no vuelve a encender un MCP que el usuario apagó; sólo una instalación nueva lo deja encendido.
            if (o == null) o = new Dictionary<string, object> { ["enabled"] = true };
            else if (!o.ContainsKey("enabled")) o["enabled"] = true;
            o["levels"] = levels;
            Directory.CreateDirectory(Path.GetDirectoryName(_p.ConfigFile));
            File.WriteAllText(_p.ConfigFile, Json.Write(o));
        }

        // ---------------- Codex ----------------

        /// <summary>Añade o actualiza [mcp_servers.pdi-revit] en config.toml sin tocar el resto del archivo.</summary>
        public void RegisterCodex()
        {
            var path = _p.CodexConfig;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            // F19/A31: se conservan las demás claves de la sección (args, env, startup_timeout_sec…) y sus subtablas; sólo cambia command.
            var kept = TomlSectionBody(text, CodexSection, out var subtables);
            var block = CodexSection + "\ncommand = \"" + _p.BridgeExe.Replace("\\", "\\\\") + "\"\n" + kept + subtables;
            var original = File.Exists(path) ? text : null;
            text = RemoveTomlSection(text, CodexSection);
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
            if (text.Length > 0) text += "\n";
            var updated = text + block;
            if (original == updated) return;
            // Copia junto al archivo antes de cambiarlo; el reemplazo es atómico para no dejarlo a medias.
            if (original != null) File.Copy(path, path + ".bimbridge-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak", true);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, updated);
            if (original != null) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Devuelve current con la sección header (y sus subtablas) tal como está en source, o sin ella si source no la tiene.</summary>
        public static string ReplaceTomlSection(string current, string source, string header)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var inSource = lines.Any(l => l.Trim() == header);
            var rest = RemoveTomlSection(current, header);
            if (!inSource) return rest;
            var kept = TomlSectionBody(source, header, out var subtables);
            var command = lines.SkipWhile(l => l.Trim() != header).Skip(1).TakeWhile(l => !l.Trim().StartsWith("[", StringComparison.Ordinal))
                .FirstOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^command\s*="));
            if (rest.Length > 0 && !rest.EndsWith("\n", StringComparison.Ordinal)) rest += "\n";
            if (rest.Length > 0) rest += "\n";
            return rest + header + "\n" + (command != null ? command + "\n" : "") + kept + subtables;
        }

        /// <summary>Conflictos con lo que ya hay en este perfil. Vacío = no hay nada instalado.</summary>
        public List<string> DetectConflicts(string productVersion)
        {
            var list = new List<string>();
            var installed = ReadInstalled();
            if (installed != null) list.Add("ya hay una instalación " + installed.ProductVersion + " para Revit " + string.Join(", ", installed.Versions));
            else if (File.Exists(_p.InstallRecord)) list.Add("el registro de instalación existe y no se puede leer: " + _p.InstallRecord);
            else if (Directory.Exists(_p.BinDir)) list.Add("hay archivos en " + _p.BinDir + " sin registro de instalación");
            foreach (var v in SupportedVersions)
                if (installed == null && File.Exists(Path.Combine(_p.AddinsDir(v), "PDI.RevitMcp.addin"))) list.Add("hay un add-in sin registro en Revit " + v);
            return list;
        }

        public void UnregisterCodex()
        {
            var path = _p.CodexConfig;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var cleaned = RemoveTomlSection(text, CodexSection);
            if (cleaned != text) File.WriteAllText(path, cleaned);
        }

        /// <summary>Líneas de la sección [x] salvo su encabezado y "command =" (texto con \n final), y aparte sus subtablas [x.algo] completas.</summary>
        public static string TomlSectionBody(string text, string header, out string subtables)
        {
            var name = header.Trim('[', ']');
            var body = new StringBuilder();
            var subs = new StringBuilder();
            var where = 0; // 0 fuera, 1 en la sección, 2 en una subtabla
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    var h = t.Trim('[', ']').Trim();
                    where = h == name ? 1 : h.StartsWith(name + ".", StringComparison.Ordinal) ? 2 : 0;
                    if (where == 2) subs.Append('\n').Append(line).Append('\n');
                    continue;
                }
                if (where == 1)
                {
                    if (t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^command\s*=")) continue;
                    body.Append(line).Append('\n');
                }
                else if (where == 2 && t.Length > 0) subs.Append(line).Append('\n');
            }
            subtables = subs.ToString();
            return body.ToString();
        }

        /// <summary>Quita una sección [x] y sus líneas hasta la siguiente sección (incluidas subtablas [x.algo]).</summary>
        public static string RemoveTomlSection(string text, string header)
        {
            var name = header.Trim('[', ']');
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            var skipping = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    var h = t.Trim('[', ']').Trim();
                    skipping = h == name || h.StartsWith(name + ".", StringComparison.Ordinal);
                }
                if (!skipping) sb.Append(line).Append('\n');
            }
            var result = sb.ToString();
            while (result.EndsWith("\n\n", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1);
            return result == "\n" ? "" : result;
        }

        // ---------------- desinstalar ----------------

        public void Uninstall(bool removeData)
        {
            var info = ReadInstalled();
            foreach (var v in SupportedVersions) RemoveAddin(v);
            _log("Add-in quitado de Revit");
            try { if (Directory.Exists(_p.BinDir)) Directory.Delete(_p.BinDir, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new IOException("No se pudo quitar el puente: está en uso. Cierre las conversaciones de Claude o Codex y vuelva a intentar.", ex); }
            UnregisterCodex();
            try { File.Delete(_p.InstallRecord); } catch (Exception) { }
            if (removeData)
            {
                foreach (var d in new[] { Path.Combine(_p.Home, "journal"), Path.Combine(_p.Home, "cola"), Path.Combine(_p.Home, "instances"),
                                       Path.Combine(_p.Home, "operaciones"), Path.Combine(_p.Home, "resultados"), Path.Combine(_p.Home, "scripts") })
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { }
                foreach (var f in new[] { "addin.log", "retener.on", "pausado.on" })
                    try { File.Delete(Path.Combine(_p.Home, f)); } catch (Exception) { }
                try { var cfg = Path.GetDirectoryName(_p.ConfigFile); if (Directory.Exists(cfg)) Directory.Delete(cfg, true); } catch (Exception) { }
                _log("Registros, cola y configuración borrados");
            }
        }

        private void RemoveAddin(string v)
        {
            var dir = _p.AddinsDir(v);
            var manifest = Path.Combine(dir, "PDI.RevitMcp.addin");
            var folder = Path.Combine(dir, "PDI.RevitMcp");
            try
            {
                if (File.Exists(manifest)) File.Delete(manifest);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("No se pudo quitar el add-in de Revit " + v + ": Revit está abierto. Ciérrelo y vuelva a intentar.", ex);
            }
        }
    }
}
