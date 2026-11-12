// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Setup
{
    /// <summary>Una Release publicada en GitHub con el instalador y su lista de hashes.</summary>
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
    /// 0.9.1: actualización en línea desde las Releases públicas del repositorio (sin cuenta ni token). Sólo lo dispara una persona
    /// con el botón Actualizar; ninguna herramienta de los agentes llega aquí.
    /// Garantías:
    ///  - sólo HTTPS a api.github.com y a la URL de descarga de ESA release de ESE repositorio (se valida el prefijo exacto);
    ///  - se ignoran borradores y, salvo que se pida, prereleases; la etiqueta debe ser vX.Y.Z;
    ///  - el instalador se descarga a un archivo temporal con tamaño máximo, se calcula su SHA-256 y se compara con SHA256SUMS.txt
    ///    de la misma release; además su versión interna debe coincidir con la etiqueta. Si algo no cuadra se borra y no se instala;
    ///  - no instala nada: deja el instalador verificado en la carpeta de actualizaciones y el flujo existente (/actualizar, con
    ///    reversión a la versión anterior) hace el resto cuando la persona lo confirma.
    /// Límite declarado: el hash publicado junto al instalador protege contra descargas corruptas o truncadas, no contra una release
    /// publicada por alguien con control del repositorio; la autenticidad descansa en HTTPS y en los permisos del repositorio.
    /// </summary>
    public static class OnlineUpdate
    {
        public const string DefaultRepo = "Jimjo2n/bimbridge-power-engine";
        public const string SetupAsset = "PDI.RevitMcp.Setup.exe";
        public const string SumsAsset = "SHA256SUMS.txt";
        public const long MaxSetupBytes = 400L * 1024 * 1024;
        public const long MaxTextBytes = 2L * 1024 * 1024;

        /// <summary>GET de texto (URL, máximo de bytes). Lanza si no es 200 o excede el máximo.</summary>
        public delegate string GetText(string url, long maxBytes);
        /// <summary>Descarga a archivo (URL, destino, máximo de bytes). Lanza si no es 200 o excede el máximo.</summary>
        public delegate void GetFile(string url, string destination, long maxBytes);

        private static readonly Regex RepoRx = new Regex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
        private static readonly Regex TagRx = new Regex("^v([0-9]{1,4})\\.([0-9]{1,4})\\.([0-9]{1,6})$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaRx = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

        public static string ValidRepo(string repo)
        {
            repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
            if (!RepoRx.IsMatch(repo) || repo.Contains("..")) throw new OnlineUpdateException("repositorio de actualización no válido: use propietario/nombre");
            return repo;
        }

        public static string ApiUrl(string repo) => "https://api.github.com/repos/" + ValidRepo(repo) + "/releases?per_page=15";

        /// <summary>
        /// La release publicada más alta que cumpla todo (no borrador, etiqueta vX.Y.Z, con instalador y hashes, URLs del propio
        /// repositorio). Null si no hay ninguna. <paramref name="skipped"/> explica por qué se descartó cada una.
        /// </summary>
        public static OnlineRelease Pick(string releasesJson, string repo, bool allowPrerelease, out List<string> skipped)
        {
            repo = ValidRepo(repo);
            skipped = new List<string>();
            object parsed;
            try { parsed = Json.Parse(releasesJson); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidOperationException || ex is ArgumentException) { throw new OnlineUpdateException("GitHub devolvió una respuesta que no es JSON válido"); }
            if (!(parsed is List<object> list))
            {
                var msg = parsed is Dictionary<string, object> d ? Arg.GetString(d, "message") : null;
                throw new OnlineUpdateException("GitHub no devolvió la lista de releases" + (msg != null ? ": " + msg : ""));
            }
            var ok = new List<OnlineRelease>();
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                var tag = Arg.GetString(item, "tag_name") ?? "";
                if (Arg.GetBool(item, "draft") == true) { skipped.Add(tag + ": borrador"); continue; }
                var pre = Arg.GetBool(item, "prerelease") == true;
                if (pre && !allowPrerelease) { skipped.Add(tag + ": prerelease (no permitida)"); continue; }
                var m = TagRx.Match(tag);
                if (!m.Success) { skipped.Add(tag + ": la etiqueta no es vX.Y.Z"); continue; }
                var prefix = "https://github.com/" + repo + "/releases/download/" + tag + "/";
                string setup = null, sums = null; long size = 0; var badUrl = false;
                var assets = (item.TryGetValue("assets", out var av) ? av as List<object> : null) ?? new List<object>();
                foreach (var a in assets.OfType<Dictionary<string, object>>())
                {
                    var name = Arg.GetString(a, "name");
                    var url = Arg.GetString(a, "browser_download_url") ?? "";
                    if (name != SetupAsset && name != SumsAsset) continue;
                    // La URL debe ser exactamente la de esta release de este repositorio (nada de otros hosts ni rutas).
                    if (!string.Equals(url, prefix + name, StringComparison.Ordinal)) { skipped.Add(tag + ": URL inesperada para " + name); badUrl = true; break; }
                    if (name == SetupAsset) { setup = url; size = Arg.GetLong(a, "size") ?? 0; } else sums = url;
                }
                if (badUrl) continue;
                if (setup == null || sums == null) { skipped.Add(tag + ": falta " + SetupAsset + " o " + SumsAsset); continue; }
                if (size <= 0 || size > MaxSetupBytes) { skipped.Add(tag + ": tamaño del instalador fuera de rango"); continue; }
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

        /// <summary>Hash esperado de <paramref name="fileName"/> en un SHA256SUMS ("hash  nombre" por línea). Null si no está o es ambiguo.</summary>
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
                if (found != null && found != hash) return null;   // dos hashes distintos para el mismo archivo: no se confía en ninguno
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
        /// "nueva": la release es una versión mayor. "mismo_version_otro_commit": misma versión, commit distinto del que corre (se puede
        /// instalar a petición). "al_dia": misma versión y mismo commit (o commit desconocido). "anterior": la release es más antigua.
        /// </summary>
        public static string Compare(Version running, string runningCommit, OnlineRelease r)
        {
            var run = Updates.Normalize(running) ?? new Version(0, 0, 0);
            if (r.Version > run) return "nueva";
            if (r.Version < run) return "anterior";
            if (r.Commit != null && !string.IsNullOrEmpty(runningCommit) && !r.Commit.StartsWith(runningCommit.Trim().ToLowerInvariant(), StringComparison.Ordinal)) return "mismo_version_otro_commit";
            return "al_dia";
        }

        /// <summary>Commit con que se compiló un ensamblado, de su versión informativa "X.Y.Z+abcdef0" (null si no lo lleva).</summary>
        public static string CommitOf(string informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion)) return null;
            var i = informationalVersion.IndexOf('+');
            if (i < 0) return null;
            var c = informationalVersion.Substring(i + 1).Trim().ToLowerInvariant();
            return Regex.IsMatch(c, "^[0-9a-f]{7,40}$") ? c : null;
        }

        /// <summary>
        /// Descarga y verifica el instalador de la release. Devuelve el candidato listo para el flujo de instalación existente.
        /// Cualquier discrepancia borra lo descargado y lanza <see cref="OnlineUpdateException"/>: nunca queda un instalador sin verificar.
        /// </summary>
        public static UpdateCandidate Download(OnlineRelease r, string destinationExe, GetText getText, GetFile getFile)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var sums = getText(r.SumsUrl, MaxTextBytes);
            var expected = ExpectedHash(sums, SetupAsset);
            if (expected == null) throw new OnlineUpdateException("la release " + r.Tag + " no publica un SHA-256 válido para " + SetupAsset);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationExe));
            // Si ya está descargado y verificado (mismo hash), no se vuelve a bajar.
            if (File.Exists(destinationExe) && Sha256File(destinationExe) == expected)
                return Candidate(r, destinationExe);
            var tmp = destinationExe + ".descarga-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                getFile(r.SetupUrl, tmp, MaxSetupBytes);
                var size = new FileInfo(tmp).Length;
                if (size != r.SetupSize) throw new OnlineUpdateException("el instalador descargado mide " + size + " bytes y la release declara " + r.SetupSize + " (descarga incompleta)");
                var actual = Sha256File(tmp);
                if (actual != expected) throw new OnlineUpdateException("el SHA-256 del instalador descargado (" + actual + ") no coincide con el publicado (" + expected + "): no se instala");
                var ver = Updates.VersionReader(tmp);
                if (ver == null) throw new OnlineUpdateException("el archivo descargado no es un instalador de BIMBRIDGE");
                if (Updates.Normalize(ver) != r.Version) throw new OnlineUpdateException("el instalador dice ser la versión " + Updates.Format(ver) + " y la release es " + r.Tag + ": no se instala");
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
