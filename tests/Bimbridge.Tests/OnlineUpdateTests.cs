// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Setup;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>
    /// 0.9.1: online update from public Releases. No network: GitHub responses and downloads are simulated.
    /// What is checked is that an installer that does not match what was published is NEVER made available.
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
            var assets = "{\"name\":\"Bimbridge.Setup.exe\",\"size\":" + size + ",\"browser_download_url\":\"" + (setupUrl ?? baseUrl + "Bimbridge.Setup.exe") + "\"}";
            if (sums) assets += ",{\"name\":\"SHA256SUMS.txt\",\"size\":90,\"browser_download_url\":\"" + baseUrl + "SHA256SUMS.txt\"}";
            return "{\"tag_name\":\"" + tag + "\",\"name\":\"BIMBRIDGE " + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
                   ",\"target_commitish\":\"" + commit + "\",\"assets\":[" + assets + "]}";
        }

        private static string Sha(byte[] b) { using (var s = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        [Fact]
        public void Pick_ChoosesTheHighestPublishedRelease_AndExplainsWhatItSkips()
        {
            var json = "[" + string.Join(",", Release("v0.9.0"), Release("v0.9.2", draft: true), Release("v0.9.3", pre: true), Release("v0.9.1"), Release("nightly"), Release("v0.9.4", sums: false),
                Release("v0.9.5", setupUrl: "https://evil.example/Bimbridge.Setup.exe"), Release("v0.9.6", size: 0)) + "]";
            var r = OnlineUpdate.Pick(json, Repo, false, out var skipped);
            Assert.Equal("v0.9.1", r.Tag);
            Assert.Equal(new Version(0, 9, 1), r.Version);
            Assert.Equal("https://github.com/" + Repo + "/releases/download/v0.9.1/Bimbridge.Setup.exe", r.SetupUrl);
            Assert.Equal(6, skipped.Count);
            Assert.Contains(skipped, s => s.StartsWith("v0.9.5") && s.Contains("unexpected URL"));
            Assert.Equal("v0.9.3", OnlineUpdate.Pick(json, Repo, true, out _).Tag);      // prerelease only if requested
            Assert.Null(OnlineUpdate.Pick("[]", Repo, false, out _));
            // A release from ANOTHER repository served in the response is not accepted.
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
            Assert.Equal(h1, OnlineUpdate.ExpectedHash("﻿" + h1 + "  Bimbridge.Setup.exe\r\n" + h2 + "  source.zip\n", "Bimbridge.Setup.exe"));
            Assert.Equal(h1, OnlineUpdate.ExpectedHash(h1.ToUpperInvariant() + " *Bimbridge.Setup.exe", "Bimbridge.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(h2 + "  source.zip", "Bimbridge.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(h1 + "  Bimbridge.Setup.exe\n" + h2 + "  Bimbridge.Setup.exe", "Bimbridge.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(new string('z', 64) + "  Bimbridge.Setup.exe", "Bimbridge.Setup.exe"));
            Assert.Null(OnlineUpdate.ExpectedHash(null, "Bimbridge.Setup.exe"));
        }

        [Fact]
        public void Compare_DistinguishesNewVersion_SameVersionOtherCommit_UpToDate_AndOlder()
        {
            var r = OnlineUpdate.Pick("[" + Release("v0.9.1") + "]", Repo, false, out _);
            Assert.Equal("nueva", OnlineUpdate.Compare(new Version(0, 9, 0), "abc1234", r));
            Assert.Equal("anterior", OnlineUpdate.Compare(new Version(0, 9, 2), "abc1234", r));
            Assert.Equal("mismo_version_otro_commit", OnlineUpdate.Compare(new Version(0, 9, 1, 0), "abc1234", r));
            Assert.Equal("al_dia", OnlineUpdate.Compare(new Version(0, 9, 1), "0123456", r));
            Assert.Equal("al_dia", OnlineUpdate.Compare(new Version(0, 9, 1), null, r));            // without a known commit it is not claimed to be different
            Assert.Equal("abc1234", OnlineUpdate.CommitOf("0.9.1+ABC1234"));
            Assert.Null(OnlineUpdate.CommitOf("0.9.1"));
            Assert.Null(OnlineUpdate.CommitOf("0.9.1+no-es-hex"));
            // a target_commitish that is a branch name is not taken as a commit.
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

        /// <summary>Manual check with network (does not run in CI): BIMBRIDGE_TEST_ONLINE=1.</summary>
        [Fact]
        public void Live_PublicApiAnswersWithoutAnAccount_WhenOptedIn()
        {
            if (Environment.GetEnvironmentVariable("BIMBRIDGE_TEST_ONLINE") != "1") return;
            var json = OnlineHttp.GetText(OnlineUpdate.ApiUrl(null), OnlineUpdate.MaxTextBytes);
            OnlineUpdate.Pick(json, null, true, out _);   // does not throw: the response is a list (empty if there are no releases yet)
        }

        private UpdateCandidate Run(byte[] served, string sumsHashFor, Version internalVersion, out List<string> urls, long declaredSize = -1, string tag = "v0.9.1")
        {
            var r = OnlineUpdate.Pick("[" + Release(tag, size: declaredSize < 0 ? served.Length : declaredSize) + "]", Repo, false, out _);
            var seen = new List<string>(); urls = seen;
            Updates.VersionReader = _ => internalVersion;
            return OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "Bimbridge.Setup-0.9.1.exe"),
                (url, max) => { seen.Add(url); return sumsHashFor + "  Bimbridge.Setup.exe\n"; },
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
            // Second time: already present and the hash matches -> only the hash list is requested.
            Run(bytes, Sha(bytes), new Version(0, 9, 1), out var urls2);
            Assert.Single(urls2);
        }

        [Fact]
        public void Download_AnyMismatch_LeavesNothingInstallable()
        {
            var bytes = Encoding.ASCII.GetBytes("instalador");
            // Hash differs from the published one (tampered or corrupt).
            var ex = Assert.Throws<OnlineUpdateException>(() => Run(Encoding.ASCII.GetBytes("instalad0r"), Sha(bytes), new Version(0, 9, 1), out _));
            Assert.Contains("does not match", ex.Message);
            Assert.Empty(Leftovers());
            // Size differs from the declared one (truncated download).
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), new Version(0, 9, 1), out _, declaredSize: bytes.Length + 1));
            Assert.Empty(Leftovers());
            // The file is not one of our installers, or claims a different version than the tag.
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), null, out _));
            Assert.Throws<OnlineUpdateException>(() => Run(bytes, Sha(bytes), new Version(0, 8, 0), out _));
            Assert.Empty(Leftovers());
            // The release publishes no hash for the installer.
            var r = OnlineUpdate.Pick("[" + Release("v0.9.1") + "]", Repo, false, out _);
            Assert.Throws<OnlineUpdateException>(() => OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "x.exe"), (u, m) => "nada", (u, d, m) => File.WriteAllBytes(d, bytes)));
            // Network failure midway: no temp file is left.
            Updates.VersionReader = _ => new Version(0, 9, 1);
            Assert.Throws<IOException>(() => OnlineUpdate.Download(r, Path.Combine(_dir, "cache", "x.exe"), (u, m) => Sha(bytes) + "  Bimbridge.Setup.exe",
                (u, d, m) => { File.WriteAllBytes(d, new byte[3]); throw new IOException("connection dropped"); }));
            Assert.Empty(Leftovers());
        }
    }
}
