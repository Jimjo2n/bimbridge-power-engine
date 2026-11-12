// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PDI.RevitMcp.Setup;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1: actualización en línea desde Releases públicas. Sin red: las respuestas de GitHub y las descargas se simulan.
    /// Lo que se comprueba es que NUNCA queda disponible un instalador que no cuadre con lo publicado.
    /// </summary>
    [Collection("updates")]
    public class OnlineUpdateTests : IDisposable
    {
        private const string Repo = "Jimjo2n/bimbridge-power-engine";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb_online_" + Guid.NewGuid().ToString("N"));
        private readonly Func<string, Version> _savedReader = Updates.VersionReader;

        public OnlineUpdateTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { Updates.VersionReader = _savedReader; try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static string Release(string tag, bool draft = false, bool pre = false, string repo = Repo, long size = 5, string commit = "0123456789abcdef0123456789abcdef01234567", bool sums = true, string setupUrl = null)
        {
            var baseUrl = "https://github.com/" + repo + "/releases/download/" + tag + "/";
            var assets = "{\"name\":\"PDI.RevitMcp.Setup.exe\",\"size\":" + size + ",\"browser_download_url\":\"" + (setupUrl ?? baseUrl + "PDI.RevitMcp.Setup.exe") + "\"}";
            if (sums) assets += ",{\"name\":\"SHA256SUMS.txt\",\"size\":90,\"browser_download_url\":\"" + baseUrl + "SHA256SUMS.txt\"}";
            return "{\"tag_name\":\"" + tag + "\",\"name\":\"BIMBRIDGE " + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
                   ",\"target_commitish\":\"" + commit + "\",\"assets\":[" + assets + "]}";
        }

        private static string Sha(byte[] b) { using (var s = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        [Fact]
        public void Pick_ChoosesTheHighestPublishedRelease_AndExplainsWhatItSkips()
        {
            var json = "[" + string.Join(",", Release("v0.9.0"), Release("v0.9.2", draft: true), Release("v0.9.3", pre: true), Release("v0.9.1"), Release("nightly"), Release("v0.9.4", sums: false),
                Release("v0.9.5", setupUrl: "https://evil.example/PDI.RevitMcp.Setup.exe"), Release("v0.9.6", size: 0)) + "]";
            var r = OnlineUpdate.Pick(json, Repo, false, out var skipped);
            Assert.Equal("v0.9.1", r.Tag);
            Assert.Equal(new Version(0, 9, 1), r.Version);
            Assert.Equal("https://github.com/" + Repo + "/releases/download/v0.9.1/PDI.RevitMcp.Setup.exe", r.SetupUrl);
            Assert.Equal(6, skipped.Count);
            Assert.Contains(skipped, s => s.StartsWith("v0.9.5") && s.Contains("URL inesperada"));
            Assert.Equal("v0.9.3", OnlineUpdate.Pick(json, Repo, true, out _).Tag);      // prerelease sólo si se pide
            Assert.Null(OnlineUpdate.Pick("[]", Repo, false, out _));
            // Una release de OTRO repositorio servida en la respuesta no se acepta.
            Assert.Null(OnlineUpdate.Pick("[" + Release("v1.0.0", repo: "otro/repo") + "]", Repo, false, out var sk2));
            Assert.Single(sk2);
        }

        [Fact]
        public void Pick_RejectsBadReposAndNonListResponses()
        {
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.ApiUrl("https://evil.example/x"));
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.ApiUrl("a/b/c"));
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.ApiUrl("../.."));
            Assert.Equal("https://api.github.com/repos/" + Repo + "/releases?per_page=15", OnlineUpdate.ApiUrl(null));
            var ex = Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.Pick("{\"message\":\"API rate limit exceeded\"}", Repo, false, out _));
            Assert.Contains("rate limit", ex.Message);
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.Pick("<html>", Repo, false, out _));
        }

        [Fact]
        public void ExpectedHash_ReadsSumsFile_AndRefusesAmbiguity()
        {
            var h1 = new string('a', 64); var h2 = new string('b', 64);
            Assert.Equal(h1, OnlineUpdate.ExpectedHash("﻿" + h1 + "  PDI.RevitMcp.Setup.exe\r\n" + h2 + "  source.zip\n", "PDI.RevitMcp.Setup.exe"));
            Assert.Equal(h1, OnlineUpdate.ExpectedHash(h1.ToUpperInvariant() + " *PDI.RevitMcp.Setup.exe", "PDI.RevitMcp.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(h2 + "  source.zip", "PDI.RevitMcp.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(h1 + "  PDI.RevitMcp.Setup.exe\n" + h2 + "  PDI.RevitMcp.Setup.exe", "PDI.RevitMcp.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(new string('z', 64) + "  PDI.RevitMcp.Setup.exe", "PDI.RevitMcp.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(null, "PDI.RevitMcp.Setup.exe"));
        }

        [Fact]
        public void Compare_DistinguishesNewVersion_SameVersionOtherCommit_UpToDate_AndOlder()
        {
            var r = OnlineUpdate.Pick("[" + Release("v0.9.1") + "]", Repo, false, out _);
            Assert.Equal("nueva", OnlineUpdate.Compare(new Version(0, 9, 0), "abc1234", r));
            Assert.Equal("anterior", OnlineUpdate.Compare(new Version(0, 9, 2), "abc1234", r));
            Assert.Equal("mismo_version_otro_commit", OnlineUpdate.Compare(new Version(0, 9, 1, 0), "abc1234", r));
            Assert.Equal("al_dia", OnlineUpdate.Compare(new Version(0, 9, 1), "0123456", r));
            Assert.Equal("al_dia", OnlineUpdate.Compare(new Version(0, 9, 1), null, r));            // sin commit conocido no se afirma que sea otro
            Assert.Equal("abc1234", OnlineUpdate.CommitOf("0.9.1+ABC1234"));
            Assert.Null(OnlineUpdate.CommitOf("0.9.1"));
            Assert.Null(OnlineUpdate.CommitOf("0.9.1+no-es-hex"));
            // target_commitish que es un nombre de rama no se toma por commit.
            Assert.Null(OnlineUpdate.Pick("[" + Release("v0.9.1", commit: "main") + "]", Repo, false, out _).Commit);
        }

        [Fact]
        public void Transport_OnlyAllowsGitHubOverHttps()
        {
            Assert.True(OnlineHttp.AllowedFinalHost(new Uri("https://api.github.com/repos/x/y/releases")));
            Assert.True(OnlineHttp.AllowedFinalHost(new Uri("https://release-assets.githubusercontent.com/abc")));
            Assert.False(OnlineHttp.AllowedFinalHost(new Uri("http://github.com/x")));
            Assert.False(OnlineHttp.AllowedFinalHost(new Uri("https://github.com.evil.example/x")));
            Assert.False(OnlineHttp.AllowedFinalHost(new Uri("https://evilgithubusercontent.com/x")));
            Assert.False(OnlineHttp.AllowedFinalHost(null));
            Assert.Throws<OnlineUpdateException>(() => OnlineHttp.GetText("https://example.com/", 10));
            Assert.Throws<OnlineUpdateException>(() => OnlineHttp.GetFile("file:///c:/x", Path.Combine(_dir, "x"), 10));
        }

        /// <summary>Comprobación manual con red (no corre en CI): BIMBRIDGE_TEST_ONLINE=1.</summary>
        [Fact]
        public void Live_PublicApiAnswersWithoutAnAccount_WhenOptedIn()
        {
            if (Environment.GetEnvironmentVariable("BIMBRIDGE_TEST_ONLINE") != "1") return;
            var json = OnlineHttp.GetText(OnlineUpdate.ApiUrl(null), OnlineUpdate.MaxTextBytes);
            OnlineUpdate.Pick(json, null, true, out _);   // no lanza: la respuesta es una lista (vacía si aún no hay releases)
        }

        private UpdateCandidate Run(byte[] served, string sumsHashFor, Version internalVersion, out List<string> urls, long declaredSize = -1, string tag = "v0.9.1")
        {
            var r = OnlineUpdate.Pick("[" + Release(tag, size: declaredSize < 0 ? served.Length : declaredSize) + "]", Repo, false, out _);
            var seen = new List<string>(); urls = seen;
            Updates.VersionReader = _ => internalVersion;
            return OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "PDI.RevitMcp.Setup-0.9.1.exe"),
                (url, max) => { seen.Add(url); return sumsHashFor + "  PDI.RevitMcp.Setup.exe\n"; },
                (url, dest, max) => { seen.Add(url); File.WriteAllBytes(dest, served); });
        }

        private string[] Leftovers() => Directory.Exists(Path.Combine(_dir, "cache")) ? Directory.GetFiles(Path.Combine(_dir, "cache")) : new string[0];

        [Fact]
        public void Download_VerifiedInstaller_BecomesACandidate_AndIsNotDownloadedTwice()
        {
            var bytes = Encoding.ASCII.GetBytes("instalador-verdadero");
            var c = Run(bytes, Sha(bytes), new Version(0, 9, 1), out var urls);
            Assert.Equal(new Version(0, 9, 1), c.Version);
            Assert.Equal(bytes, File.ReadAllBytes(c.Exe));
            Assert.Equal("https://github.com/" + Repo + "/releases/tag/v0.9.1", c.Source);
            Assert.All(urls, u => Assert.StartsWith("https://github.com/" + Repo + "/releases/download/v0.9.1/", u));
            Assert.Single(Leftovers());
            // Segunda vez: ya está y coincide el hash → sólo se pide la lista de hashes.
            Run(bytes, Sha(bytes), new Version(0, 9, 1), out var urls2);
            Assert.Single(urls2);
        }

        [Fact]
        public void Download_AnyMismatch_LeavesNothingInstallable()
        {
            var bytes = Encoding.ASCII.GetBytes("instalador");
            // Hash distinto del publicado (manipulado o corrupto).
            var ex = Assert.Throws<OnlineUpdateException>(() => Run(Encoding.ASCII.GetBytes("instalad0r"), Sha(bytes), new Version(0, 9, 1), out _));
            Assert.Contains("no coincide", ex.Message);
            Assert.Empty(Leftovers());
            // Tamaño distinto del declarado (descarga truncada).
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), new Version(0, 9, 1), out _, declaredSize: bytes.Length + 1));
            Assert.Empty(Leftovers());
            // El archivo no es un instalador nuestro, o dice ser otra versión que la etiqueta.
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), null, out _));
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), new Version(0, 8, 0), out _));
            Assert.Empty(Leftovers());
            // La release no publica hash para el instalador.
            var r = OnlineUpdate.Pick("[" + Release("v0.9.1") + "]", Repo, false, out _);
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "x.exe"), (u, m) => "nada", (u, d, m) => File.WriteAllBytes(d, bytes)));
            // Fallo de red a mitad: no queda temporal.
            Updates.VersionReader = _ => new Version(0, 9, 1);
            Assert.Throws<IOException>(() => OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "x.exe"), (u, m) => Sha(bytes) + "  PDI.RevitMcp.Setup.exe",
                (u, d, m) => { File.WriteAllBytes(d, new byte[3]); throw new IOException("conexión cortada"); }));
            Assert.Empty(Leftovers());
        }
    }
}
