// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Protocol;
using PDI.RevitMcp.Setup;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>Instalador: detecta Revit, instala por versión, conserva la config, Codex y desinstala.</summary>
    public class SetupTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly SetupPaths _p;

        public SetupTests()
        {
            _p = new SetupPaths
            {
                AppData = Path.Combine(_root, "Roaming"),
                LocalAppData = Path.Combine(_root, "Local"),
                UserProfile = Path.Combine(_root, "user"),
                ProgramFiles = Path.Combine(_root, "PF"),
            };
            foreach (var v in new[] { "2024", "2025" })
            {
                Directory.CreateDirectory(Path.Combine(_p.ProgramFiles, "Autodesk", "Revit " + v));
                File.WriteAllText(Path.Combine(_p.ProgramFiles, "Autodesk", "Revit " + v, "Revit.exe"), "");
            }
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

        private static ZipArchive Payload()
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Add(string name, string content)
                {
                    using (var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8)) w.Write(content);
                }
                foreach (var v in SetupCore.SupportedVersions)
                {
                    Add("addin/" + v + "/PDI.RevitMcp.addin", "<RevitAddIns/>");
                    Add("addin/" + v + "/PDI.RevitMcp/PDI.RevitMcp.Addin.dll", "dll" + v);
                }
                Add("bridge/PDI.RevitMcp.Bridge.exe", "exe");
            }
            ms.Position = 0;
            return new ZipArchive(ms, ZipArchiveMode.Read);
        }

        private static ZipArchive PayloadWithoutBridge()
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (var w = new StreamWriter(zip.CreateEntry("addin/2024/PDI.RevitMcp/PDI.RevitMcp.Addin.dll").Open(), Encoding.UTF8)) w.Write("dll-nuevo");
            ms.Position = 0;
            return new ZipArchive(ms, ZipArchiveMode.Read);
        }

        [Fact]
        public void FailureHalfway_ReturnsToThePreviousInstall()
        {
            var core = new SetupCore(_p);
            core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "R" }, "0.5.0");
            var dll = Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll");
            var record = File.ReadAllText(_p.InstallRecord);
            var ex = Assert.Throws<IOException>(() => core.Install(PayloadWithoutBridge(), new InstallOptions { Versions = { "2024" }, Levels = "RPW" }, "0.6.0"));
            Assert.Contains("estado anterior", ex.Message);
            Assert.Equal("dll2024", File.ReadAllText(dll));
            Assert.Equal(record, File.ReadAllText(_p.InstallRecord));
        }

        [Fact]
        public void BridgeInUse_StopsBeforeChangingAnything()
        {
            var core = new SetupCore(_p);
            core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "R" }, "0.5.0");
            var record = File.ReadAllText(_p.InstallRecord);
            using (new FileStream(_p.BridgeExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = Assert.Throws<IOException>(() => core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "RPW" }, "0.6.0"));
                Assert.Contains("No se cambió nada", ex.Message);
            }
            Assert.Equal(record, File.ReadAllText(_p.InstallRecord));
        }

        [Fact]
        public void DetectsInstalledRevitVersions()
        {
            var found = new SetupCore(_p).DetectRevit().Where(r => r.Installed).Select(r => r.Version).ToArray();
            Assert.Equal(new[] { "2024", "2025" }, found);
        }

        [Fact]
        public void Update_KeepsMcpOff_AndCodexExtraKeys()
        {
            // F19/A31: el usuario apagó el MCP y añadió claves propias a la sección de Codex.
            Directory.CreateDirectory(Path.GetDirectoryName(_p.ConfigFile));
            File.WriteAllText(_p.ConfigFile, "{\"enabled\":false,\"levels\":\"R\"}");
            Directory.CreateDirectory(Path.GetDirectoryName(_p.CodexConfig));
            File.WriteAllText(_p.CodexConfig, "[mcp_servers.pdi-revit]\ncommand = \"viejo.exe\"\nstartup_timeout_sec = 30\n\n[mcp_servers.pdi-revit.env]\nPDI_REVITMCP_PERFIL = \"completo\"\n\n[otra]\nx = 1\n");
            var core = new SetupCore(_p);
            core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "RPW", Codex = true }, "0.5.0");
            var cfg = Json.ParseObject(File.ReadAllText(_p.ConfigFile));
            Assert.Equal(false, cfg["enabled"]);
            Assert.Equal("RPW", cfg["levels"]);
            var toml = File.ReadAllText(_p.CodexConfig);
            Assert.Contains("startup_timeout_sec = 30", toml);
            Assert.Contains("[mcp_servers.pdi-revit.env]", toml);
            Assert.Contains("PDI_REVITMCP_PERFIL = \"completo\"", toml);
            Assert.Contains("[otra]", toml);
            Assert.DoesNotContain("viejo.exe", toml);
            Assert.Contains("PDI.RevitMcp.Bridge.exe", toml);
            // Reinstalar no duplica nada.
            core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "RPW", Codex = true }, "0.5.0");
            var toml2 = File.ReadAllText(_p.CodexConfig);
            Assert.Equal(1, toml2.Split(new[] { "startup_timeout_sec" }, StringSplitOptions.None).Length - 1);
            Assert.Equal(1, toml2.Split(new[] { "[mcp_servers.pdi-revit.env]" }, StringSplitOptions.None).Length - 1);
        }

        /// <summary>Paquete con manifest.json (como lo genera build.sh). tamper = archivo cuyo contenido no coincide con el hash.</summary>
        private static ZipArchive SignedPayload(string tamper = null, string missing = null)
        {
            var files = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var v in SetupCore.SupportedVersions)
            {
                files["addin/" + v + "/PDI.RevitMcp.addin"] = "<RevitAddIns/>";
                files["addin/" + v + "/PDI.RevitMcp/PDI.RevitMcp.Addin.dll"] = "dll" + v;
            }
            files["bridge/PDI.RevitMcp.Bridge.exe"] = "exe";
            files["bridge/bimbridge_power_engine.dll"] = "rustdll";
            files["bridge/power_engine/python/run_worker.py"] = "py";
            string Sha(string t) { using (var m = new MemoryStream(Encoding.UTF8.GetBytes(t))) return SetupCore.Sha256(m); }
            var manifest = new System.Collections.Generic.Dictionary<string, object>
            {
                ["version"] = "0.9.0",
                ["requeridos"] = files.Keys.Cast<object>().ToList(),
                ["archivos"] = files.ToDictionary(kv => kv.Key, kv => (object)Sha(kv.Value)),
            };
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var kv in files.Where(kv => kv.Key != missing))
                    using (var w = new StreamWriter(zip.CreateEntry(kv.Key).Open(), new UTF8Encoding(false))) w.Write(kv.Key == tamper ? kv.Value + "X" : kv.Value);
                using (var w = new StreamWriter(zip.CreateEntry(SetupCore.PayloadManifest).Open(), new UTF8Encoding(false))) w.Write(Json.Write(manifest));
            }
            ms.Position = 0;
            return new ZipArchive(ms, ZipArchiveMode.Read);
        }

        /// <summary>0.9.0: el Power Engine del paquete (DLL Rust y Python Worker) se instala junto al puente, verificado por hash.</summary>
        [Fact]
        public void PowerEngine_FilesInstalledNextToBridge_AndTamperIsRejected()
        {
            var core = new SetupCore(_p);
            core.Install(SignedPayload(), new InstallOptions { Versions = { "2024" }, Levels = "R" }, "0.9.0");
            Assert.Equal("rustdll", File.ReadAllText(Path.Combine(_p.BinDir, "bimbridge_power_engine.dll")));
            Assert.Equal("py", File.ReadAllText(Path.Combine(_p.BinDir, "power_engine", "python", "run_worker.py")));
            Assert.Throws<InvalidDataException>(() => new SetupCore(_p).Install(SignedPayload("bridge/power_engine/python/run_worker.py"), new InstallOptions { Versions = { "2024" }, Levels = "R" }, "0.9.0"));
            Assert.Equal("py", File.ReadAllText(Path.Combine(_p.BinDir, "power_engine", "python", "run_worker.py")));
        }

        [Fact]
        public void MissingRequiredWorkerStopsInstallBeforeAnyCopy()
        {
            using var zip = SignedPayload(missing: "bridge/power_engine/python/run_worker.py");
            var core = new SetupCore(_p);
            Assert.Throws<InvalidDataException>(() =>
                core.Install(zip, new InstallOptions { Versions = { "2024" }, Levels = "R" }, "0.9.0"));
            Assert.False(Directory.Exists(_p.AddinsDir("2024")));
            Assert.False(File.Exists(_p.BridgeExe));
        }

        /// <summary>El payload REAL que arma build.sh (si existe) pasa su propia verificación e instala los 5 años con hashes registrados.</summary>
        [Fact]
        public void RealPayload_FromBuild_VerifiesAndInstalls()
        {
            var d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "PDI.RevitMcp.sln"))) d = Path.GetDirectoryName(d);
            var payload = Path.Combine(d, "src", "PDI.RevitMcp.Setup", "payload.zip");
            if (!File.Exists(payload)) return; // sin build.sh no hay payload: no aplica
            using (var zip = ZipFile.OpenRead(payload))
            {
                var manifest = SetupCore.ReadPayloadManifest(zip);
                Assert.NotNull(manifest);
                var core = new SetupCore(_p);
                core.Install(zip, new InstallOptions { Versions = SetupCore.SupportedVersions.ToList(), Levels = "RPW" }, "0.6.0");
                var diag = core.Diagnose();
                foreach (var v in SetupCore.SupportedVersions)
                    Assert.Equal(true, ((System.Collections.Generic.Dictionary<string, object>)((System.Collections.Generic.Dictionary<string, object>)diag["addin"])[v])["coinciden"]);
                Assert.True(File.Exists(Path.Combine(_p.AddinsDir("2026"), "PDI.RevitMcp", "recetas", "etiquetar_luces.cs")));
                Assert.True(File.Exists(Path.Combine(_p.BinDir, "PDI.RevitMcp.AgentHost.exe")));
                Assert.True(File.Exists(Path.Combine(_p.BinDir, "power_engine", "bimbridge-power-engine.exe")));
            }
        }

        [Fact]
        public void Manifest_TamperedPayload_InstallsNothing()
        {
            var core = new SetupCore(_p);
            var ex = Assert.Throws<InvalidDataException>(() =>
                core.Install(SignedPayload("addin/2024/PDI.RevitMcp/PDI.RevitMcp.Addin.dll"), new InstallOptions { Versions = { "2024" }, Levels = "RPW" }, "0.6.0"));
            Assert.Contains("hash distinto", ex.Message);
            Assert.False(Directory.Exists(_p.AddinsDir("2024")));
            Assert.False(File.Exists(_p.BridgeExe));
            Assert.Null(core.ReadInstalled());
            // Un archivo manipulado de un año NO elegido no impide instalar los elegidos.
            core.Install(SignedPayload("addin/2022/PDI.RevitMcp/PDI.RevitMcp.Addin.dll"), new InstallOptions { Versions = { "2024" }, Levels = "RPW" }, "0.6.0");
            Assert.True(File.Exists(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp.addin")));
        }

        [Fact]
        public void Diagnose_ReportsHashes_ChangedFiles_PendingRestart_LoadedVersion_AndConfig()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_p.ConfigFile));
            // Modo directo autorizado (W + runner auto) y un MCP apagado por el usuario: una actualización no los cambia.
            File.WriteAllText(_p.ConfigFile, "{\"levels\":\"RPW\",\"runner\":\"auto\",\"enabled\":false}");
            var core = new SetupCore(_p);
            core.Install(SignedPayload(), new InstallOptions { Versions = { "2024", "2026" }, Levels = "RPW" }, "0.6.0");
            var d = core.Diagnose();
            Assert.Equal("0.6.0", d["instalada"]);
            var addin = (System.Collections.Generic.Dictionary<string, object>)d["addin"];
            var y24 = (System.Collections.Generic.Dictionary<string, object>)addin["2024"];
            Assert.Equal(true, y24["coinciden"]);
            Assert.Equal(false, y24["reinicio_pendiente"]);
            Assert.Equal("no instalado", addin["2023"]);
            var cfg = (System.Collections.Generic.Dictionary<string, object>)d["config"];
            Assert.Equal(false, cfg["enabled"]);
            Assert.Equal("RPW", cfg["levels"]);
            Assert.Equal("auto", cfg["runner"]);

            // Alguien cambia la DLL y queda un archivo apartado en uso: el diagnóstico lo dice.
            var dll = Path.Combine(_p.AddinsDir("2026"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll");
            File.WriteAllText(dll, "otra");
            File.WriteAllText(dll + Updates.LeftoverMark + "1", "vieja");
            d = core.Diagnose();
            var y26 = (System.Collections.Generic.Dictionary<string, object>)((System.Collections.Generic.Dictionary<string, object>)d["addin"])["2026"];
            Assert.Equal(false, y26["coinciden"]);
            Assert.Equal(true, y26["reinicio_pendiente"]);
            Assert.Contains("cambió", string.Join(",", (System.Collections.Generic.List<object>)y26["diferencias"]));

            // Revit abierto con el add-in cargado: el descriptor del proceso actual cuenta como "vivo".
            var inst = Path.Combine(_p.Home, "instances");
            Directory.CreateDirectory(inst);
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(Path.Combine(inst, pid + ".json"), Json.Write(new InstanceDescriptor { Pid = pid, RevitVersion = "2026", PipeName = "x", Protocol = 1, AddinVersion = "0.5.0.0", StartedUtc = "2026-10-20T00:00:00Z" }.ToJson()));
            d = core.Diagnose();
            var open = (System.Collections.Generic.List<object>)d["revit_abiertos"];
            Assert.Single(open);
            Assert.Equal(false, ((System.Collections.Generic.Dictionary<string, object>)open[0])["coincide_con_instalada"]);
        }

        [Fact]
        public void Install_Update_Uninstall()
        {
            // Config previa del usuario con otros valores: deben conservarse.
            Directory.CreateDirectory(Path.GetDirectoryName(_p.ConfigFile));
            File.WriteAllText(_p.ConfigFile, "{\"levels\":\"R\",\"journal_days\":30}");
            // Codex con otra configuración: no se toca.
            Directory.CreateDirectory(Path.GetDirectoryName(_p.CodexConfig));
            File.WriteAllText(_p.CodexConfig, "model = \"gpt-5\"\n\n[mcp_servers.otro]\ncommand = \"x.exe\"\n");

            var core = new SetupCore(_p);
            core.Install(Payload(), new InstallOptions { Versions = { "2024", "2025" }, Levels = "RPW", Codex = true }, "0.2.0");

            Assert.Equal("dll2024", File.ReadAllText(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp", "PDI.RevitMcp.Addin.dll")));
            Assert.True(File.Exists(Path.Combine(_p.AddinsDir("2025"), "PDI.RevitMcp.addin")));
            Assert.False(Directory.Exists(_p.AddinsDir("2023")));
            Assert.True(File.Exists(_p.BridgeExe));
            var cfg = Json.ParseObject(File.ReadAllText(_p.ConfigFile));
            Assert.Equal("RPW", cfg["levels"]);
            Assert.Equal(30L, cfg["journal_days"]);
            var toml = File.ReadAllText(_p.CodexConfig);
            Assert.Contains("[mcp_servers.otro]", toml);
            Assert.Contains("[mcp_servers.pdi-revit]", toml);
            Assert.Contains("PDI.RevitMcp.Bridge.exe", toml);

            var info = core.ReadInstalled();
            Assert.Equal("0.2.0", info.ProductVersion);
            Assert.Equal(new[] { "2024", "2025" }, info.Versions.ToArray());

            // Actualizar: sólo 2025, sólo lectura, sin Codex. 2024 se quita; la sección de Codex también.
            core.Install(Payload(), new InstallOptions { Versions = { "2025" }, Levels = "R", Codex = false }, "0.2.1");
            Assert.False(File.Exists(Path.Combine(_p.AddinsDir("2024"), "PDI.RevitMcp.addin")));
            Assert.Equal("R", Json.ParseObject(File.ReadAllText(_p.ConfigFile))["levels"]);
            toml = File.ReadAllText(_p.CodexConfig);
            Assert.DoesNotContain("pdi-revit", toml);
            Assert.Contains("[mcp_servers.otro]", toml);
            Assert.Equal(1, toml.Split(new[] { "[mcp_servers.otro]" }, StringSplitOptions.None).Length - 1);

            // Reinstalar con Codex dos veces no duplica la sección.
            core.Install(Payload(), new InstallOptions { Versions = { "2025" }, Levels = "RP", Codex = true }, "0.2.1");
            core.Install(Payload(), new InstallOptions { Versions = { "2025" }, Levels = "RP", Codex = true }, "0.2.1");
            Assert.Equal(2, File.ReadAllText(_p.CodexConfig).Split(new[] { "pdi-revit" }, StringSplitOptions.None).Length);

            // Desinstalar conservando datos y luego todo.
            File.WriteAllText(Path.Combine(_p.Home, "addin.log"), "x");
            core.Uninstall(removeData: false);
            Assert.False(File.Exists(Path.Combine(_p.AddinsDir("2025"), "PDI.RevitMcp.addin")));
            Assert.False(File.Exists(_p.BridgeExe));
            Assert.True(File.Exists(Path.Combine(_p.Home, "addin.log")));
            Assert.Null(core.ReadInstalled());
            Assert.DoesNotContain("pdi-revit", File.ReadAllText(_p.CodexConfig));
            core.Uninstall(removeData: true);
            Assert.False(File.Exists(Path.Combine(_p.Home, "addin.log")));
            Assert.False(File.Exists(_p.ConfigFile));
        }

        [Fact]
        public void RejectsBadOptions()
        {
            var core = new SetupCore(_p);
            Assert.Throws<ArgumentException>(() => core.Install(Payload(), new InstallOptions(), "0"));
            Assert.Throws<ArgumentException>(() => core.Install(Payload(), new InstallOptions { Versions = { "2024" }, Levels = "W" }, "0"));
        }

        [Fact]
        public void TomlSectionRemoval_KeepsOtherSections()
        {
            var t = "a = 1\n\n[mcp_servers.pdi-revit]\ncommand = \"x\"\n\n[mcp_servers.pdi-revit.env]\nK = \"v\"\n\n[otra]\nb = 2\n";
            var r = SetupCore.RemoveTomlSection(t, SetupCore.CodexSection);
            Assert.DoesNotContain("pdi-revit", r);
            Assert.Contains("a = 1", r);
            Assert.Contains("[otra]\nb = 2", r);
        }
    }
}

namespace PDI.RevitMcp.Tests
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using PDI.RevitMcp.Setup;
    using Xunit;

    /// <summary>Botón Actualizar: buscar instaladores, elegir el más nuevo, guardar actual + anterior y reciclar los viejos.</summary>
    [Collection("updates")]
    public class UpdateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pdi-upd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly Func<string, Version> _savedReader = Updates.VersionReader;

        public UpdateTests()
        {
            Directory.CreateDirectory(_root);
            // En Linux no hay un PDI.RevitMcp.Setup.exe real: el "exe" de prueba es un texto con su versión.
            Updates.VersionReader = f =>
            {
                var t = File.ReadAllText(f);
                return t.StartsWith("PDI.RevitMcp.Setup ") ? Updates.Parse(t.Substring(19)) : null;
            };
        }

        public void Dispose()
        {
            Updates.VersionReader = _savedReader;
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private SetupPaths Paths() => new SetupPaths
        {
            AppData = Path.Combine(_root, "Roaming"), LocalAppData = Path.Combine(_root, "Local"),
            UserProfile = Path.Combine(_root, "user"), ProgramFiles = Path.Combine(_root, "pf"), Desktop = Path.Combine(_root, "user", "Desktop"),
        };

        private static void FakeExe(string path, string version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "PDI.RevitMcp.Setup " + version);
        }

        private static void FakeZip(string zipPath, string version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("PDI.RevitMcp.Setup.exe").Open()))
                w.Write("PDI.RevitMcp.Setup " + version);
        }

        [Fact]
        public void Scan_FindsZipLooseExeAndExtractedFolder_PicksNewest()
        {
            var p = Paths();
            FakeZip(Path.Combine(p.Downloads, "PDI.RevitMcp.Setup_0.2.1.zip"), "0.2.1");
            FakeZip(Path.Combine(p.Downloads, "PDI.RevitMcp.Setup_0.2.3 (1).zip"), "0.2.3");
            FakeExe(Path.Combine(p.Desktop, "PDI.RevitMcp.Setup_0.2.2", "PDI.RevitMcp.Setup.exe"), "0.2.2");
            FakeExe(Path.Combine(p.Desktop, "otra cosa.exe"), "9.9.9");                       // no empieza por el prefijo
            File.WriteAllText(Path.Combine(p.Downloads, "PDI.RevitMcp.Setup.exe"), "no soy un instalador");  // nombre correcto, no es nuestro

            var found = Updates.Scan(Updates.Folders(p), p.UpdatesTemp);
            Assert.Equal(new[] { "0.2.1", "0.2.2", "0.2.3" }, found.Select(c => c.VersionText).OrderBy(x => x).ToArray());

            var best = Updates.Newest(found, new Version(0, 2, 1));
            Assert.Equal("0.2.3", best.VersionText);
            Assert.EndsWith("(1).zip", best.Source);
            Assert.True(File.Exists(best.Exe));                       // extraído para ejecutarlo
            Assert.Null(Updates.Newest(found, new Version(0, 2, 3)));  // ya al día

            var old = Updates.Superseded(found, new Version(0, 2, 3), p.UpdatesDir);
            Assert.Equal(3, old.Count);                                // los tres van a la papelera tras instalar 0.2.3
        }

        [Fact]
        public void CacheSetup_KeepsCurrentAndPrevious()
        {
            var p = Paths();
            var core = new SetupCore(p);
            foreach (var v in new[] { "0.2.0", "0.2.1" }) FakeExe(p.CachedSetup(v), v);
            var self = Path.Combine(_root, "dl", "PDI.RevitMcp.Setup.exe");
            FakeExe(self, "0.2.2");

            var removed = core.CacheSetup(self, "0.2.2", "0.2.1");
            Assert.True(File.Exists(p.CachedSetup("0.2.2")));
            Assert.True(File.Exists(p.CachedSetup("0.2.1")));
            Assert.False(File.Exists(p.CachedSetup("0.2.0")));
            Assert.Single(removed);

            // La caché propia nunca se manda a la papelera.
            var found = Updates.Scan(Updates.Folders(p), p.UpdatesTemp);
            Assert.Empty(Updates.Superseded(found, new Version(0, 2, 2), p.UpdatesDir));
        }

        [Fact]
        public void Leftovers_AreDeleted()
        {
            var p = Paths();
            var f = Path.Combine(p.BinDir, "PDI.RevitMcp.Bridge.exe" + Updates.LeftoverMark + "123");
            FakeExe(f, "0.0.1");
            Assert.Equal(1, Updates.DeleteLeftovers(p.LeftoverDirs(SetupCore.SupportedVersions)));
            Assert.False(File.Exists(f));
        }

        [Fact]
        public void CopyOver_SobrescribeDestinoSoloLectura()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pdi-copyover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var src = Path.Combine(dir, "nuevo.exe"); var dst = Path.Combine(dir, "Setup.exe");
                File.WriteAllText(src, "v2"); File.WriteAllText(dst, "v1");
                File.SetAttributes(dst, File.GetAttributes(dst) | FileAttributes.ReadOnly);
                SetupCore.CopyOver(src, dst);
                Assert.Equal("v2", File.ReadAllText(dst));
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
        }
    }
}
