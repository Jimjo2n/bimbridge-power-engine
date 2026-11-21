// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bimbridge.Protocol;

namespace Bimbridge.Setup
{
    /// <summary>A Release published on GitHub with the installer and its hash list.</summary>
    public sealed class OnlineRelease
    {
        public string Repo, Tag, Name, Commit, SetupUrl, SumsUrl;
        public Version Version;
        public bool Prerelease;
        public long SetupSize;
        public string VersionText => Updates.Format(Version);
    }

    public sealed class OnlineUpdateException : Exception
    {
        public OnlineUpdateException(string message) : base(message) { }
    }

    /// <summary>
    /// 0.9.1: online update from the repository's public Releases (no account or token). Only a person triggers it
    /// with the Update button; no agent tool reaches this code.
    /// Guarantees:
    ///  - HTTPS only, to api.github.com and to the download URL of THAT release of THAT repository (the exact prefix is validated);
    ///  - drafts and, unless requested, prereleases are ignored; the tag must be vX.Y.Z;
    ///  - the installer is downloaded to a temporary file with a maximum size, its SHA-256 is computed and compared with SHA256SUMS.txt
    ///    from the same release; its internal version must also match the tag. If anything does not add up it is deleted and not installed;
    ///  - it installs nothing: it leaves the verified installer in the updates folder and the existing workflow (/actualizar, with
    ///    rollback to the previous version) does the rest when the person confirms.
    /// Declared limit: the hash published next to the installer protects against corrupt or truncated downloads, not against a release
    /// published by someone with control of the repository; authenticity rests on HTTPS and on the repository permissions.
    /// </summary>
    public static class OnlineUpdate
    {
        public const string DefaultRepo = "Jimjo2n/bimbridge-power-engine";
        public const string SetupAsset = "Bimbridge.Setup.exe";
        public const string SumsAsset = "SHA256SUMS.txt";
        public const long MaxSetupBytes = 400L * 1024 * 1024;
        public const long MaxTextBytes = 2L * 1024 * 1024;

        /// <summary>Text GET (URL, maximum bytes). Throws if not 200 or if it exceeds the maximum.</summary>
        public delegate string GetText(string url, long maxBytes);
        /// <summary>Download to file (URL, destination, maximum bytes). Throws if not 200 or if it exceeds the maximum.</summary>
        public delegate void GetFile(string url, string destination, long maxBytes);

        private static readonly Regex RepoRx = new Regex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
        private static readonly Regex TagRx = new Regex("^v([0-9]{1,4})\\.([0-9]{1,4})\\.([0-9]{1,6})$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaRx = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

        public static string ValidRepo(string repo)
        {
            repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
            if (!RepoRx.IsMatch(repo) || repo.Contains("..")) throw new OnlineUpdateException("invalid update repository: use owner/name");
            return repo;
        }

        public static string ApiUrl(string repo) => "https://api.github.com/repos/" + ValidRepo(repo) + "/releases?per_page=15";

        /// <summary>
        /// The highest published release that meets every condition (not a draft, tag vX.Y.Z, with installer and hashes, URLs of the
        /// repository itself). Null if there is none. <paramref name="skipped"/> explains why each one was discarded.
        /// </summary>
        public static OnlineRelease Pick(string releasesJson, string repo, bool allowPrerelease, out List<string> skipped)
        {
            repo = ValidRepo(repo);
            skipped = new List<string>();
            object parsed;
            try { parsed = Json.Parse(releasesJson); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException || ex is ArgumentException) { throw new OnlineUpdateException("GitHub returned a response that is not valid JSON"); }
            if (!(parsed is List<object> list))
            {
                var msg = parsed is Dictionary<string, object> d ? Arg.GetString(d, "message") : null;
                throw new OnlineUpdateException("GitHub did not return the list of releases" + (msg != null ? ": " + msg : ""));
            }
            var ok = new List<OnlineRelease>();
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                var tag = Arg.GetString(item, "tag_name") ?? "";
                if (Arg.GetBool(item, "draft") == true) { skipped.Add(tag + ": draft"); continue; }
                var pre = Arg.GetBool(item, "prerelease") == true;
                if (pre && !allowPrerelease) { skipped.Add(tag + ": prerelease (not allowed)"); continue; }
                var m = TagRx.Match(tag);
                if (!m.Success) { skipped.Add(tag + ": the tag is not vX.Y.Z"); continue; }
                var prefix = "https://github.com/" + repo + "/releases/download/" + tag + "/";
                string setup = null, sums = null; long size = 0; var badUrl = false;
                var assets = (item.TryGetValue("assets", out var av) ? av as List<object> : null) ?? new List<object>();
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    var name = Arg.GetString(a, "name");
                    var url = Arg.GetString(a, "browser_download_url") ?? "";
                    if (name != SetupAsset && name != SumsAsset) continue;
                    // The URL must be exactly that of this release of this repository (no other hosts or paths).
                    if (!string.Equals(url, prefix + name, StringComparison.Ordinal)) { skipped.Add(tag + ": unexpected URL for " + name); badUrl = true; break; }
                    if (name == SetupAsset) { setup = url; size = Arg.GetLong(a, "size") ?? 0; } else sums = url;
                }
                if (badUrl) continue;
                if (setup == null || sums == null) { skipped.Add(tag + ": missing " + SetupAsset + " or " + SumsAsset); continue; }
                if (size <= 0 || size > MaxSetupBytes) { skipped.Add(tag + ": installer size out of range"); continue; }
                var commit = Arg.GetString(item, "target_commitish");
                ok.Add(new OnlineRelease
                {
                    Repo = repo, Tag = tag, Name = Arg.GetString(item, "name") ?? tag, Prerelease = pre, SetupUrl = setup, SumsUrl = sums, SetupSize = size,
                    Version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)),
                    Commit = commit != null && ShaRx.IsMatch(commit) ? commit : null,
                });
            }
            return ok.OrderByDescending(r => r.Version).ThenBy(r => r.Prerelease).FirstOrDefault();
        }

        /// <summary>Expected hash of <paramref name="fileName"/> in a SHA256SUMS ("hash  name" per line). Null if absent or ambiguous.</summary>
        public static string ExpectedHash(string sumsText, string fileName)
        {
            string found = null;
            foreach (var raw in (sumsText ?? "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length < 66) continue;
                var hash = line.Substring(0, 64).ToLowerInvariant();
                var name = line.Substring(64).Trim().TrimStart('*');
                if (!string.Equals(name, fileName, StringComparison.Ordinal)) continue;
                if (!Regex.IsMatch(hash, "^[0-9a-f]{64}$")) return null;
                if (found != null && found != hash) return null;   // two different hashes for the same file: neither is trusted
                found = hash;
            }
            return found;
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// "new": the release is a higher version. "same_version_other_commit": same version, different commit from the running one (can be
        /// installed on request). "up_to_date": same version and same commit (or unknown commit). "previous": the release is older.
        /// </summary>
        public static string Compare(Version running, string runningCommit, OnlineRelease r)
        {
            var run = Updates.Normalize(running) ?? new Version(0, 0, 0);
            if (r.Version > run) return "new";
            if (r.Version < run) return "previous";
            if (r.Commit != null && !string.IsNullOrEmpty(runningCommit) && !r.Commit.StartsWith(runningCommit.Trim().ToLowerInvariant(), StringComparison.Ordinal)) return "same_version_other_commit";
            return "up_to_date";
        }

        /// <summary>Commit an assembly was built from, taken from its informational version "X.Y.Z+abcdef0" (null if it has none).</summary>
        public static string CommitOf(string informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion)) return null;
            var i = informationalVersion.IndexOf('+');
            if (i < 0) return null;
            var c = informationalVersion.Substring(i + 1).Trim().ToLowerInvariant();
            return Regex.IsMatch(c, "^[0-9a-f]{7,40}$") ? c : null;
        }

        /// <summary>
        /// Downloads and verifies the release installer. Returns the candidate ready for the existing install workflow.
        /// Any discrepancy deletes the download and throws <see cref="OnlineUpdateException"/>: an unverified installer is never left behind.
        /// </summary>
        public static UpdateCandidate Download(OnlineRelease r, string destinationExe, GetText getText, GetFile getFile)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var sums = getText(r.SumsUrl, MaxTextBytes);
            var expected = ExpectedHash(sums, SetupAsset);
            if (expected == null) throw new OnlineUpdateException("release " + r.Tag + " does not publish a valid SHA-256 for " + SetupAsset);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationExe));
            // If it is already downloaded and verified (same hash), it is not downloaded again.
            if (File.Exists(destinationExe) && Sha256File(destinationExe) == expected)
                return Candidate(r, destinationExe);
            var tmp = destinationExe + ".descarga-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                getFile(r.SetupUrl, tmp, MaxSetupBytes);
                var size = new FileInfo(tmp).Length;
                if (size != r.SetupSize) throw new OnlineUpdateException("the downloaded installer is " + size + " bytes and the release declares " + r.SetupSize + " (incomplete download)");
                var actual = Sha256File(tmp);
                if (actual != expected) throw new OnlineUpdateException("the SHA-256 of the downloaded installer (" + actual + ") does not match the published one (" + expected + "): not installing");
                var ver = Updates.VersionReader(tmp);
                if (ver == null) throw new OnlineUpdateException("the downloaded file is not a BIMBRIDGE installer");
                if (Updates.Normalize(ver) != r.Version) throw new OnlineUpdateException("the installer claims to be version " + Updates.Format(ver) + " and the release is " + r.Tag + ": not installing");
                if (File.Exists(destinationExe)) File.Delete(destinationExe);
                File.Move(tmp, destinationExe);
                return Candidate(r, destinationExe);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static UpdateCandidate Candidate(OnlineRelease r, string exe) =>
            new UpdateCandidate { Version = r.Version, Exe = exe, Source = "https://github.com/" + r.Repo + "/releases/tag/" + r.Tag };
    }
}
