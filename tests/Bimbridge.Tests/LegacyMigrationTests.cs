// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using Bimbridge.Engine;
using Bimbridge.Setup;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>Migration from the former product name. Old layouts are built from LegacyMigration's constants, never literals.</summary>
    public class LegacyMigrationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bimbridge-legacy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly SetupPaths _p;
        private readonly List<string> _log = new List<string>();

        public LegacyMigrationTests()
        {
            _p = new SetupPaths
            {
                AppData = Path.Combine(_root, "Roaming"), LocalAppData = Path.Combine(_root, "Local"),
                UserProfile = Path.Combine(_root, "user"), ProgramFiles = Path.Combine(_root, "PF"),
            };
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private string Old(params string[] parts) => Path.Combine(LegacyMigration.LegacyHome(_p), Path.Combine(parts));

        private void BuildOldLayout()
        {
            Write(Old("install.json"), "{\"version\":\"0.9.3\",\"revit\":[\"2024\"],\"levels\":\"RPW\",\"codex\":true,\"previous\":\"0.9.2\"}");
            Write(Old("tareas", "t1.json"), "tarea");
            Write(Old("bin", LegacyMigration.LegacyBridgeExe), "exe");
            Write(Old("instances", "123.json"), "{}");
            Write(Old(LegacyMigration.LegacySetupPrefix + ".exe"), "setup");
            Write(Old("actualizaciones", LegacyMigration.LegacySetupPrefix + "-0.9.2.exe"), "setup");
            Write(Old("autorizaciones", "clave.bin"), LegacyMigration.LegacyKeyHeader + "\nQUJD\n");
            Write(Path.Combine(LegacyMigration.LegacyConfigDir(_p), "config.json"),
                "{\"levels\":\"RPW\",\"scripts\":\"" + Old("scripts").Replace("\\", "\\\\") + "\"}");
            Write(LegacyMigration.LegacyAddinManifest(_p, "2024"), "<RevitAddIns/>");
            Write(Path.Combine(LegacyMigration.LegacyAddinFolder(_p, "2024"), "x.dll"), "dll");
            var bridge = LegacyMigration.LegacyBridge(_p).Replace("\\", "\\\\");
            Write(_p.CodexConfig, "model = \"o3\"\n\n" + LegacyMigration.LegacyCodexSection + "\ncommand = \"" + bridge + "\"\nstartup_timeout_sec = 30\n\n" +
                "[" + LegacyMigration.LegacyCodexSection.Trim('[', ']') + ".env]\n" + LegacyMigration.LegacyEnvPrefix + "PERFIL = \"completo\"\n" +
                LegacyMigration.LegacyNativeGate + " = \"1\"\n\n[otro]\nx = 1\n");
            Write(Old("recetas", "mia.cs"), "public class Mia : " + LegacyMigration.LegacyNamespacePrefix + "Addin.Scripting.Receta { }\n// " + LegacyMigration.LegacyNamespacePrefix + "Engine\n");
            Write(Old("scripts", "ok.cs"), "return 1;\n");
            Write(Old("tareas", "t2", "receta.cs"), "class T : " + LegacyMigration.LegacyNamespacePrefix + "Addin.Scripting.Receta { }");
        }

        private void MigrateAndInstall()
        {
            LegacyMigration.MigrateData(_p, _log.Add);
            LegacyMigration.RemoveOldInstall(_p, _log.Add);
        }

        [Fact]
        public void FailedInstallAfterMigrateDataKeepsOldAddin()
        {
            BuildOldLayout();
            Assert.True(LegacyMigration.MigrateData(_p, _log.Add));
            using (var ms = new MemoryStream())
            {
                using (new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) { }
                ms.Position = 0;
                using (var zip = new System.IO.Compression.ZipArchive(ms))
                    Assert.ThrowsAny<Exception>(() => new SetupCore(_p).Install(zip,
                        new InstallOptions { Versions = new List<string> { "2024" }, Levels = "R", RequireManifest = true }, "1.0.0"));
            }
            // The caller skips RemoveOldInstall when Install throws: the old add-in and bridge still work.
            Assert.True(File.Exists(LegacyMigration.LegacyAddinManifest(_p, "2024")));
            Assert.True(File.Exists(Path.Combine(LegacyMigration.LegacyAddinFolder(_p, "2024"), "x.dll")));
            Assert.True(File.Exists(LegacyMigration.LegacyBridge(_p)));
            Assert.Equal("tarea", File.ReadAllText(Path.Combine(_p.Home, "tareas", "t1.json")));
        }

        [Fact]
        public void RewritesSavedRecipesButNotPausedTasks()
        {
            BuildOldLayout();
            MigrateAndInstall();
            var recipe = File.ReadAllText(Path.Combine(_p.Home, "recetas", "mia.cs"));
            Assert.Contains(": Bimbridge.Addin.Scripting.Recipe {", recipe);
            Assert.Contains("// Bimbridge.Engine", recipe);
            Assert.DoesNotContain(LegacyMigration.LegacyFolder, recipe);
            Assert.Equal("return 1;\n", File.ReadAllText(Path.Combine(_p.Home, "scripts", "ok.cs")));
            Assert.Contains(LegacyMigration.LegacyNamespacePrefix, File.ReadAllText(Path.Combine(_p.Home, "tareas", "t2", "receta.cs")));
        }

        [Fact]
        public void RewritesOldScriptingApiNames()
        {
            // Test data: a recipe written against the pre-rename (Spanish) scripting API.
            var old = "public class X : Receta {\n" +
                      "    public override string Descripcion => \"Ejecutar\";\n" +
                      "    public override PoliticaTx Transaccion => PoliticaTx.Lectura;\n" +
                      "    public override IEnumerable<string> Entradas => new[] { \"nivel\" };\n" +
                      "    public override void Preparar(ScriptContext ctx) { base.Preparar(ctx); }\n" +
                      "    public override IEnumerable<Unidad> Unidades(ScriptContext ctx) { yield return new Unidad(\"k\", null); yield return Unidad.Guardar(); }\n" +
                      "    public override object Ejecutar(ScriptContext ctx, Unidad u) { if (u.Clave == \"\") ctx.Omitir(\"x\"); ctx.Excepcion(\"m\"); ctx.Artefacto(\"a\", \"b\"); return Bim.Montaje.Vista3D(ctx.Doc); }\n" +
                      "    public override Verificacion Verificar(ScriptContext ctx, Unidad u) => Verificacion.NoAplicada;\n" +
                      "    public override object Postcondicion(ScriptContext ctx) => null;\n}\n";
            var expected = "public class X : Recipe {\n" +
                      "    public override string Description => \"Ejecutar\";\n" +
                      "    public override TxPolicy Transaction => TxPolicy.ReadOnly;\n" +
                      "    public override IEnumerable<string> Inputs => new[] { \"nivel\" };\n" +
                      "    public override void Prepare(ScriptContext ctx) { base.Prepare(ctx); }\n" +
                      "    public override IEnumerable<Unit> Units(ScriptContext ctx) { yield return new Unit(\"k\", null); yield return Unit.Save(); }\n" +
                      "    public override object Run(ScriptContext ctx, Unit u) { if (u.Key == \"\") ctx.Skip(\"x\"); ctx.Exception(\"m\"); ctx.Artifact(\"a\", \"b\"); return Bim.Mounting.View3D(ctx.Doc); }\n" +
                      "    public override Verification Verify(ScriptContext ctx, Unit u) => Verification.NotApplied;\n" +
                      "    public override object Postcondition(ScriptContext ctx) => null;\n}\n";
            Assert.Equal(expected, LegacyMigration.RewriteScriptText(old));
            Assert.Equal(expected, LegacyMigration.RewriteScriptText(expected));
        }

        [Fact]
        public void ReadsClaudeEnvWithRenamedKeys()
        {
            var json = Path.Combine(_root, "claude.json");
            Write(json, "{\"mcpServers\":{\"" + LegacyMigration.LegacyMcpName + "\":{\"command\":\"x\",\"env\":{\"" +
                LegacyMigration.LegacyEnvPrefix + "PERFIL\":\"completo\",\"" + LegacyMigration.LegacyEnvPrefix + "AGENTE\":\"a\",\"" +
                LegacyMigration.LegacyNativeGate + "\":\"1\",\"OTHER\":\"o\"}}}}");
            var env = LegacyMigration.ReadClaudeEnv(json, LegacyMigration.LegacyMcpName);
            Assert.Equal("completo", env["BIMBRIDGE_PROFILE"]);
            Assert.Equal("a", env["BIMBRIDGE_AGENT"]);
            Assert.Equal("1", env["BIMBRIDGE_REVIT_NATIVE"]);
            Assert.Equal("o", env["OTHER"]);
            Assert.Empty(LegacyMigration.ReadClaudeEnv(json, "bimbridge"));
            Assert.Empty(LegacyMigration.ReadClaudeEnv(Path.Combine(_root, "missing.json"), LegacyMigration.LegacyMcpName));
        }

        [Fact]
        public void OldInstallCountsAsInstalledBeforeMigration()
        {
            BuildOldLayout();
            var info = new SetupCore(_p).ReadInstalled();
            Assert.Equal("0.9.3", info.ProductVersion);
            Assert.True(info.Codex);
            Assert.Contains("2024", info.Versions);
        }

        [Fact]
        public void MigratesDataConfigAddinCodexAndGrantKey_Idempotently()
        {
            BuildOldLayout();
            Assert.True(LegacyMigration.MigrateData(_p, _log.Add));
            Assert.True(LegacyMigration.RemoveOldInstall(_p, _log.Add));

            Assert.False(Directory.Exists(LegacyMigration.LegacyHome(_p)));
            Assert.False(Directory.Exists(LegacyMigration.LegacyConfigDir(_p)));
            Assert.Equal("tarea", File.ReadAllText(Path.Combine(_p.Home, "tareas", "t1.json")));
            Assert.True(File.Exists(_p.InstallRecord));
            Assert.False(Directory.Exists(Path.Combine(_p.Home, "bin")));
            Assert.False(Directory.Exists(Path.Combine(_p.Home, "instances")));
            Assert.Empty(Directory.GetFiles(_p.Home, LegacyMigration.LegacySetupPrefix + "*", SearchOption.AllDirectories));

            var config = File.ReadAllText(_p.ConfigFile);
            Assert.Contains(Path.Combine(_p.Home, "scripts").Replace("\\", "\\\\"), config);
            Assert.DoesNotContain(LegacyMigration.LegacyFolder, config);

            Assert.False(File.Exists(LegacyMigration.LegacyAddinManifest(_p, "2024")));
            Assert.False(Directory.Exists(LegacyMigration.LegacyAddinFolder(_p, "2024")));

            Assert.StartsWith(GrantStore.KeyHeader + "\n", File.ReadAllText(Path.Combine(_p.Home, "autorizaciones", "clave.bin")));
            Assert.Equal(GrantStore.KeyHeader, LegacyMigration.NewKeyHeader);

            var toml = File.ReadAllText(_p.CodexConfig);
            Assert.DoesNotContain(LegacyMigration.LegacyCodexSection.Trim('[', ']'), toml);
            Assert.Contains(SetupCore.CodexSection + "\ncommand = \"" + _p.BridgeExe.Replace("\\", "\\\\") + "\"\nstartup_timeout_sec = 30", toml);
            Assert.Contains("[" + SetupCore.CodexSection.Trim('[', ']') + ".env]\nBIMBRIDGE_PROFILE = \"completo\"\nBIMBRIDGE_REVIT_NATIVE = \"1\"", toml);
            Assert.Contains("[otro]\nx = 1", toml);
            Assert.Contains("model = \"o3\"", toml);

            Assert.Equal("0.9.3", new SetupCore(_p).ReadInstalled().ProductVersion);
            Assert.False(LegacyMigration.MigrateData(_p, _log.Add));
            Assert.False(LegacyMigration.RemoveOldInstall(_p, _log.Add));
        }

        [Fact]
        public void MergeKeepsNewerFilesWhenTargetExists()
        {
            BuildOldLayout();
            Write(Path.Combine(_p.Home, "tareas", "t1.json"), "nueva");
            Write(Path.Combine(_p.Home, "instalador.lock"), "");
            MigrateAndInstall();

            Assert.Equal("nueva", File.ReadAllText(Path.Combine(_p.Home, "tareas", "t1.json")));
            Assert.True(File.Exists(_p.InstallRecord));
            // The conflicting old file stays where it was; nothing else is left behind.
            Assert.True(File.Exists(Old("tareas", "t1.json")));
            Assert.False(File.Exists(Old("install.json")));
            Assert.Contains(_log, l => l.Contains("Kept the old copy"));
        }

        [Fact]
        public void PreRenameBackupIsRefused()
        {
            var dir = InstallBackup.Create(_p, new[] { "2024" }, "test", DateTime.UtcNow);
            var key = "addin/2024/" + LegacyMigration.LegacyFolder + "/x.dll";
            Write(Path.Combine(dir, "archivos", key.Replace('/', Path.DirectorySeparatorChar)), "dll");
            var manifest = Path.Combine(dir, InstallBackup.ManifestName);
            var m = Bimbridge.Protocol.Json.ParseObject(File.ReadAllText(manifest));
            ((Dictionary<string, object>)m["archivos"])[key] = SetupCore.FileSha(Path.Combine(dir, "archivos", key.Replace('/', Path.DirectorySeparatorChar)));
            File.WriteAllText(manifest, Bimbridge.Protocol.Json.Write(m));
            Assert.Throws<InvalidDataException>(() => InstallBackup.Restore(_p, dir, DateTime.UtcNow));
        }
    }
}
