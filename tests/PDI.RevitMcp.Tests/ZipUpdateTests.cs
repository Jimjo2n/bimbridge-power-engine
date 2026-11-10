// SPDX-License-Identifier: GPL-3.0-only
using System;
using PDI.RevitMcp.Protocol;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Setup;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0: Actualizar desde ZIP. Inspección, preparación y aplicación diferida, sin Revit ni instaladores reales.</summary>
    public class ZipUpdateTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-zip-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private static readonly string[] AllYears = { "2022", "2023", "2024", "2025", "2026" };

        public ZipUpdateTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        private string Pkg(string name, string version, RSA signer = null, string[] revit = null, string desde = null)
        {
            var src = Path.Combine(_dir, "src-" + name);
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "setup.exe"), "instalador " + version);
            File.WriteAllText(Path.Combine(src, "LEEME.txt"), "notas " + version);
            var zip = Path.Combine(_dir, name + ".zip");
            ZipUpdate.Build(zip, new Dictionary<string, string> { ["PDI.RevitMcp.Setup.exe"] = Path.Combine(src, "setup.exe"), ["LEEME.txt"] = Path.Combine(src, "LEEME.txt") },
                version, "abc1234", revit ?? AllYears, desde, signer);
            return zip;
        }

        private static readonly RSA Signer = RSA.Create(3072);
        private static readonly ProfilePolicy Lab = ProfilePolicy.Parse("laboratorio", new[] { Path.GetTempPath() }, true, null);
        private static readonly ProfilePolicy Production = new ProfilePolicy();

        private static List<TrustedKey> Keys(RSA rsa) => new List<TrustedKey> { TrustedKey.Parse(TrustedKey.Format(rsa.ExportParameters(false))) };

        [Fact]
        public void Signed_ValidatesWithTrustedKey_ShowsVersions()
        {
            using (var rsa = RSA.Create(2048))
            {
                var r = ZipUpdate.Inspect(Pkg("a", "0.8.1", rsa), "0.8.0", new[] { "2024", "2026" }, Keys(rsa));
                Assert.Equal("valido_firmado", r.Estado);
                Assert.True(r.Signed);
                Assert.Equal("0.8.1", r.Version);
                Assert.Equal("0.8.0", r.Installed);
                Assert.Equal("abc1234", r.Commit);
                Assert.False(r.NeedsUnsignedConfirmation);
            }
        }

        [Fact]
        public void Signature_WrongKey_Tampered_Required_AreRejected()
        {
            using (var good = RSA.Create(2048))
            using (var other = RSA.Create(2048))
            {
                // Firmado con una clave que no es de confianza.
                Assert.Equal("clave_desconocida", ZipUpdate.Inspect(Pkg("b", "0.8.1", other), "0.8.0", null, Keys(good)).Estado);
                // Hay claves configuradas y el paquete no está firmado.
                Assert.Equal("firma_exigida", ZipUpdate.Inspect(Pkg("c", "0.8.1"), "0.8.0", null, Keys(good)).Estado);
                // Manifiesto alterado tras firmar (subir la versión).
                var zip = Pkg("d", "0.8.1", good);
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    var e = z.GetEntry(ZipUpdate.ManifestName);
                    string text; using (var s = new StreamReader(e.Open())) text = s.ReadToEnd();
                    e.Delete();
                    var ne = z.CreateEntry(ZipUpdate.ManifestName);
                    using (var w = new StreamWriter(ne.Open(), new UTF8Encoding(false))) w.Write(text.Replace("0.8.1", "0.9.9"));
                }
                Assert.Equal("firma_invalida", ZipUpdate.Inspect(zip, "0.8.0", null, Keys(good)).Estado);
            }
        }

        [Fact]
        public void Unsigned_WithoutKeys_NeedsExplicitConfirmation_OnlyInLab()
        {
            var zip = Pkg("e", "0.8.1");
            var r = ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: Lab);
            Assert.Equal("valido_sin_firma", r.Estado);
            Assert.True(r.NeedsUnsignedConfirmation);
            Assert.Contains("SIN FIRMA", r.Mensaje);
            Assert.Throws<InvalidOperationException>(() => ZipUpdate.Stage(r, Path.Combine(_dir, "upd"), "operator", unsignedConfirmed: false, policy: Lab));
            var p = ZipUpdate.Stage(r, Path.Combine(_dir, "upd"), "operator", unsignedConfirmed: true, policy: Lab);
            Assert.Equal("preparada", p.Estado);
            Assert.False(p.Signed);
            Assert.Equal(ProfilePolicy.Lab, p.Perfil);
        }

        [Fact]
        public void Production_RejectsUnsigned_EvenWithConfirmation_AndWithoutKeys()
        {
            var zip = Pkg("e2", "0.8.1");
            Assert.Equal("firma_exigida", ZipUpdate.Inspect(zip, "0.8.0", null, null).Estado);
            Assert.Equal("firma_exigida", ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: Production).Estado);
            // laboratorio sin el interruptor explícito tampoco
            var labNoFlag = ProfilePolicy.Parse("laboratorio", new[] { _dir }, false, null);
            Assert.Equal("firma_exigida", ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: labNoFlag).Estado);
            var r = ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: Lab);
            Assert.Throws<InvalidOperationException>(() => ZipUpdate.Stage(r, Path.Combine(_dir, "upd"), "operator", true, policy: Production));
        }

        [Fact]
        public void Apply_LabStagedUnsigned_IsRejectedByProductionApplier()
        {
            var upd = Path.Combine(_dir, "upd-lab");
            ZipUpdate.Stage(ZipUpdate.Inspect(Pkg("lab1", "0.8.1"), "0.8.0", null, null, policy: Lab), upd, "operator", true, policy: Lab);
            var fs = new FakeSystem();
            var h = fs.Host(); h.Policy = () => Production; h.Keys = () => new List<TrustedKey>();
            Assert.Equal("procedencia_rechazada", PendingApplier.Run(upd, h, TimeSpan.FromHours(1))["estado"]);
            Assert.Empty(fs.Runs);
            // el mismo paquete preparado otra vez, con la política de laboratorio, sí se instala
            var upd2 = Path.Combine(_dir, "upd-lab2");
            ZipUpdate.Stage(ZipUpdate.Inspect(Pkg("lab2", "0.8.1"), "0.8.0", null, null, policy: Lab), upd2, "operator", true, policy: Lab);
            var fs2 = new FakeSystem();
            var h2 = fs2.Host(); h2.Policy = () => Lab;
            Assert.Equal("aplicada_verificada", PendingApplier.Run(upd2, h2, TimeSpan.FromHours(1))["estado"]);
        }

        [Fact]
        public void Apply_SignedButPendingManifestEdited_IsRejected()
        {
            var upd = Prepared();
            var pend = Path.Combine(upd, "pendiente.json");
            var txt = File.ReadAllText(pend);
            File.WriteAllText(pend, txt.Replace("\"0.8.1\"", "\"0.9.9\""));
            var fs = new FakeSystem();
            var st = (string)PendingApplier.Run(upd, fs.Host(), TimeSpan.FromHours(1))["estado"];
            Assert.Contains(st, new[] { "procedencia_rechazada", "paquete_alterado" });
            Assert.Empty(fs.Runs);
        }

        [Fact]
        public void Apply_SignedWithKeyNoLongerTrusted_IsRejected()
        {
            var upd = Prepared();
            var fs = new FakeSystem();
            var h = fs.Host();
            using (var other = RSA.Create(3072)) { var k = Keys(other); h.Keys = () => k; }
            Assert.Equal("procedencia_rechazada", PendingApplier.Run(upd, h, TimeSpan.FromHours(1))["estado"]);
            Assert.Empty(fs.Runs);
        }

        [Fact]
        public void Integrity_ExtraFile_ChangedFile_Traversal_AreRejected()
        {
            var zip = Pkg("f", "0.8.1");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
                using (var w = new StreamWriter(z.CreateEntry("extra.dll").Open())) w.Write("x");
            var r = ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: Lab);
            Assert.Equal("hashes_no_coinciden", r.Estado);
            Assert.Contains(r.Detalles, d => d.Contains("extra.dll"));

            var zip2 = Pkg("g", "0.8.1");
            using (var z = ZipFile.Open(zip2, ZipArchiveMode.Update))
            {
                z.GetEntry("LEEME.txt").Delete();
                using (var w = new StreamWriter(z.CreateEntry("LEEME.txt").Open())) w.Write("cambiado");
            }
            Assert.Equal("hashes_no_coinciden", ZipUpdate.Inspect(zip2, "0.8.0", null, null, policy: Lab).Estado);

            var zip3 = Pkg("h", "0.8.1");
            using (var z = ZipFile.Open(zip3, ZipArchiveMode.Update))
                using (var w = new StreamWriter(z.CreateEntry("../../fuera.txt").Open())) w.Write("x");
            Assert.Equal("ruta_peligrosa", ZipUpdate.Inspect(zip3, "0.8.0", null, null, policy: Lab).Estado);
            Assert.False(ZipUpdate.SafeEntry("C:/Windows/x.dll"));
            Assert.False(ZipUpdate.SafeEntry("/etc/x"));

            // Un ZIP cualquiera no es un paquete.
            var plain = Path.Combine(_dir, "plain.zip");
            using (var z = ZipFile.Open(plain, ZipArchiveMode.Create)) using (var w = new StreamWriter(z.CreateEntry("PDI.RevitMcp.Setup.exe").Open())) w.Write("x");
            Assert.Equal("no_es_paquete", ZipUpdate.Inspect(plain, "0.8.0", null, null, policy: Lab).Estado);
        }

        [Fact]
        public void Compatibility_Years_MinimumFrom_AndDowngrade()
        {
            Assert.Equal("incompatible", ZipUpdate.Inspect(Pkg("i", "0.8.1", revit: new[] { "2025", "2026" }), "0.8.0", new[] { "2024" }, null, policy: Lab).Estado);
            Assert.Equal("incompatible", ZipUpdate.Inspect(Pkg("j", "0.9.0", desde: "0.8.0"), "0.7.0", null, null, policy: Lab).Estado);
            Assert.Equal("misma_o_anterior", ZipUpdate.Inspect(Pkg("k", "0.8.0"), "0.8.0", null, null, policy: Lab).Estado);
            Assert.Equal("valido_sin_firma", ZipUpdate.Inspect(Pkg("l", "0.7.0"), "0.8.0", null, null, allowOlder: true, policy: Lab).Estado);
        }

        [Fact]
        public void Stage_RejectsZipChangedAfterInspection()
        {
            var zip = Pkg("m", "0.8.1");
            var r = ZipUpdate.Inspect(zip, "0.8.0", null, null, policy: Lab);
            File.AppendAllText(zip, "basura");
            Assert.Throws<InvalidDataException>(() => ZipUpdate.Stage(r, Path.Combine(_dir, "upd"), "operator", true, policy: Lab));
        }

        // ---------------- aplicación diferida ----------------

        private sealed class FakeSystem
        {
            public Queue<bool> Revit = new Queue<bool>();
            public string Installed = "0.8.0";
            public bool BreakInstall, BreakRollback;
            public List<string> Runs = new List<string>();
            public int Sleeps;
            public DateTime Now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            public string Cached;

            public ApplyHost Host() => new ApplyHost
            {
                RevitRunning = () => Revit.Count > 0 ? Revit.Dequeue() : false,
                Sleep = t => { Sleeps++; Now += t; },
                UtcNow = () => Now,
                RunInstaller = (exe, args) =>
                {
                    Runs.Add(Path.GetFileName(exe) + " " + args);
                    if (exe == Cached) { if (!BreakRollback) Installed = "0.8.0"; return BreakRollback ? 1 : 0; }
                    if (BreakInstall) return 1;
                    Installed = File.ReadAllText(exe).Replace("instalador ", "");
                    return 0;
                },
                ReadInstalled = () => new InstalledInfo { ProductVersion = Installed },
                Diagnose = () => new Dictionary<string, object> { ["addin"] = new Dictionary<string, object> { ["2024"] = new Dictionary<string, object> { ["coinciden"] = true } } },
                CachedSetup = v => Cached,
                Keys = () => Keys(Signer),
                Policy = () => Production,
            };
        }

        private string Prepared(string version = "0.8.1")
        {
            var upd = Path.Combine(_dir, "upd");
            var insp = ZipUpdate.Inspect(Pkg("p" + version.Replace(".", ""), version, Signer), "0.8.0", null, Keys(Signer));
            Assert.True(insp.Signed);
            ZipUpdate.Stage(insp, upd, "operator", false);
            return upd;
        }

        [Fact]
        public void Apply_WaitsForUserToCloseRevit_NeverClosesIt_ThenVerifies()
        {
            var upd = Prepared();
            var fs = new FakeSystem();
            foreach (var b in new[] { true, true, true, false, false }) fs.Revit.Enqueue(b);
            var r = PendingApplier.Run(upd, fs.Host(), TimeSpan.FromHours(8));
            Assert.Equal("aplicada_verificada", r["estado"]);
            Assert.Equal("0.8.1", fs.Installed);
            Assert.Equal(4, fs.Sleeps);                 // esperó mientras Revit seguía abierto (y una lectura de confirmación)
            Assert.Single(fs.Runs);
            Assert.Null(ZipUpdate.ReadPending(upd));     // la pendiente se consume
            Assert.True(File.Exists(Path.Combine(upd, ZipUpdate.ResultFile)));
        }

        [Fact]
        public void Apply_RevitNeverCloses_TimesOut_InstallsNothing()
        {
            var upd = Prepared();
            var fs = new FakeSystem();
            for (var i = 0; i < 100000; i++) fs.Revit.Enqueue(true);
            var r = PendingApplier.Run(upd, fs.Host(), TimeSpan.FromMinutes(10));
            Assert.Equal("cancelada_tiempo", r["estado"]);
            Assert.Empty(fs.Runs);
            Assert.Equal("0.8.0", fs.Installed);
        }

        [Fact]
        public void Apply_CancelledByUser_InstallsNothing()
        {
            var upd = Prepared();
            var fs = new FakeSystem();
            fs.Revit.Enqueue(true);
            var host = fs.Host();
            var inner = host.Sleep;
            host.Sleep = t => { inner(t); ZipUpdate.CancelPending(upd); };
            Assert.Equal("cancelada_usuario", PendingApplier.Run(upd, host, TimeSpan.FromHours(1))["estado"]);
            Assert.Empty(fs.Runs);
        }

        [Fact]
        public void Apply_StagedFilesTampered_InstallsNothing()
        {
            var upd = Prepared();
            var p = ZipUpdate.ReadPending(upd);
            File.AppendAllText(p.Installer, "malicioso");
            var fs = new FakeSystem();
            Assert.Equal("paquete_alterado", PendingApplier.Run(upd, fs.Host(), TimeSpan.FromHours(1))["estado"]);
            Assert.Empty(fs.Runs);
        }

        [Fact]
        public void Apply_FailedInstall_RollsBackToPrevious_AndVerifies()
        {
            var upd = Prepared();
            var fs = new FakeSystem { BreakInstall = true, Cached = Path.Combine(_dir, "PDI.RevitMcp.Setup-0.8.0.exe") };
            File.WriteAllText(fs.Cached, "anterior");
            var r = PendingApplier.Run(upd, fs.Host(), TimeSpan.FromHours(1));
            Assert.Equal("revertida", r["estado"]);
            Assert.Equal("0.8.0", fs.Installed);
            Assert.Equal(2, fs.Runs.Count);
            Assert.Contains("/permitir-anterior", fs.Runs[1]);

            var upd2 = Prepared("0.8.2");
            var fs2 = new FakeSystem { BreakInstall = true, BreakRollback = true, Cached = fs.Cached };
            Assert.Equal("fallo_reversion", PendingApplier.Run(upd2, fs2.Host(), TimeSpan.FromHours(1))["estado"]);

            var upd3 = Prepared("0.8.3");
            var fs3 = new FakeSystem { BreakInstall = true, Cached = null };
            Assert.Equal("fallo_sin_reversion", PendingApplier.Run(upd3, fs3.Host(), TimeSpan.FromHours(1))["estado"]);
        }

        [Fact]
        public void TrustedKey_RejectsShortOrMalformed()
        {
            Assert.Throws<FormatException>(() => TrustedKey.Parse("DSA:abc:def"));
            using (var small = RSA.Create(1024))
                Assert.Throws<FormatException>(() => TrustedKey.Parse(TrustedKey.Format(small.ExportParameters(false))));
        }
    }
}
