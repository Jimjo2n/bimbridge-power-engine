// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bimbridge.Protocol;

namespace Bimbridge.Setup
{
    /// <summary>Trusted public key for signing update packages (RSA-SHA256). IT decides how keys are managed.</summary>
    public sealed class TrustedKey
    {
        public string Id;
        public byte[] Modulus, Exponent;

        /// <summary>Configuration format: "RSA:&lt;base64 modulus&gt;:&lt;base64 exponent&gt;". The id is the first 16 hex chars of the modulus SHA-256.</summary>
        public static TrustedKey Parse(string text)
        {
            var p = (text ?? "").Trim().Split(':');
            if (p.Length != 3 || !string.Equals(p[0], "RSA", StringComparison.OrdinalIgnoreCase)) throw new FormatException("update key: expected RSA:<modulus>:<exponent>");
            var k = new TrustedKey { Modulus = Convert.FromBase64String(p[1]), Exponent = Convert.FromBase64String(p[2]) };
            if (k.Modulus.Length < 256) throw new FormatException("update key: RSA of at least 2048 bits required");
            k.Id = IdOf(k.Modulus);
            return k;
        }

        public static string IdOf(byte[] modulus)
        {
            using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(modulus).Take(8).Select(b => b.ToString("x2")));
        }

        public static string Format(RSAParameters pub) => "RSA:" + Convert.ToBase64String(pub.Modulus) + ":" + Convert.ToBase64String(pub.Exponent);

        public bool Verify(byte[] data, byte[] signature)
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = Modulus, Exponent = Exponent });
                return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }
    }

    /// <summary>What was learned about a ZIP without installing anything: identity, provenance, integrity and compatibility.</summary>
    public sealed class ZipInspection
    {
        /// <summary>valido_firmado | valido_sin_firma | firma_invalida | clave_desconocida | firma_exigida | hashes_no_coinciden |
        /// ruta_peligrosa | no_es_paquete | demasiado_grande | incompatible | misma_o_anterior</summary>
        public string Estado;
        public string Mensaje;
        public string ZipPath, ZipSha256, ManifestSha256;
        public string Version, Commit, Installer, InstallerSha256, KeyId, Installed, DesdeMinima;
        public List<string> Revit = new List<string>();
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Detalles = new List<string>();
        public bool Signed;
        /// <summary>Can be staged. Unsigned, it also requires explicit confirmation from the user.</summary>
        public bool Ok => Estado == "valido_firmado" || Estado == "valido_sin_firma";
        public bool NeedsUnsignedConfirmation => Estado == "valido_sin_firma";
    }

    /// <summary>A staged update waiting for the user to close Revit. Saved in pendiente.json.</summary>
    public sealed class PendingUpdate
    {
        public string Id, Version, From, Commit, StagedDir, Installer, InstallerSha256, ZipSha256, KeyId, Estado, ConfirmedBy;
        /// <summary>0.9.0: profile it was staged under (produccion | laboratorio). An unsigned package is applied only under laboratorio.</summary>
        public string Perfil = ProfilePolicy.Production;
        public bool Signed, AllowOlder;
        public DateTime CreatedUtc;
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["esquema"] = "bimbridge.pendiente/1", ["id"] = Id, ["version"] = Version, ["desde"] = From, ["commit"] = Commit,
            ["carpeta"] = StagedDir, ["instalador"] = Installer, ["instalador_sha256"] = InstallerSha256, ["zip_sha256"] = ZipSha256,
            ["clave"] = KeyId, ["firmado"] = Signed, ["permitir_anterior"] = AllowOlder, ["estado"] = Estado, ["confirmado_por"] = ConfirmedBy, ["perfil"] = Perfil,
            ["creado_utc"] = CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["archivos"] = Files.ToDictionary(k => k.Key, k => (object)k.Value),
        };

        public static PendingUpdate FromJson(Dictionary<string, object> o)
        {
            var p = new PendingUpdate
            {
                Id = Arg.GetString(o, "id"), Version = Arg.GetString(o, "version"), From = Arg.GetString(o, "desde"), Commit = Arg.GetString(o, "commit"),
                StagedDir = Arg.GetString(o, "carpeta"), Installer = Arg.GetString(o, "instalador"), InstallerSha256 = Arg.GetString(o, "instalador_sha256"),
                ZipSha256 = Arg.GetString(o, "zip_sha256"), KeyId = Arg.GetString(o, "clave"), Signed = Arg.GetBool(o, "firmado") ?? false,
                AllowOlder = Arg.GetBool(o, "permitir_anterior") ?? false, Estado = Arg.GetString(o, "estado"), ConfirmedBy = Arg.GetString(o, "confirmado_por"),
                Perfil = Arg.GetString(o, "perfil") ?? ProfilePolicy.Production,
            };
            DateTime.TryParse(Arg.GetString(o, "creado_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out p.CreatedUtc);
            if (o.TryGetValue("archivos", out var a) && a is Dictionary<string, object> f)
                foreach (var kv in f) p.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return p;
        }
    }

    /// <summary>
    /// 0.8.0 (BIMBRIDGE → Developer Tools → Update from ZIP). Conventional update, no hot reload:
    /// inspect (manifest, signature, hashes, compatibility) → the user confirms → stage (extract and re-verify) →
    /// an external process waits for the USER to close Revit (it never closes it) → installs → verifies → on failure, goes back to the previous one.
    /// Nothing is downloaded from the internet. The AI has no tool that reaches this code: only the ribbon button.
    /// </summary>
    public static class ZipUpdate
    {
        public const string ManifestName = "bimbridge-actualizacion.json";
        public const string SignatureName = "bimbridge-actualizacion.firma";
        public const string Schema = "bimbridge.actualizacion/1";
        public const string InstallerName = Updates.Prefix + ".exe";
        public const int MaxEntries = 500;
        public const long MaxUncompressed = 400L * 1024 * 1024;
        public const string PendingFile = "pendiente.json";
        public const string ResultFile = "resultado.json";

        // ---------------- inspect ----------------

        /// <param name="installed">Installed version (install.json); null if none.</param>
        /// <param name="installedRevit">Revit years of the current installation: the package must cover all of them.</param>
        /// <param name="keys">Trusted keys. If there is any, a signature is mandatory.</param>
        /// <param name="policy">0.9.0: profile. null = production: only packages signed by a trusted key.</param>
        public static ZipInspection Inspect(string zipPath, string installed, IEnumerable<string> installedRevit, IList<TrustedKey> keys, bool allowOlder = false, ProfilePolicy policy = null)
        {
            var r = new ZipInspection { ZipPath = zipPath, Installed = installed };
            keys = keys ?? new List<TrustedKey>();
            policy = policy ?? new ProfilePolicy();
            try
            {
                r.ZipSha256 = SetupCore.FileSha(zipPath);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.Entries.Count > MaxEntries) return Fail(r, "demasiado_grande", "the ZIP has " + zip.Entries.Count + " entries (maximum " + MaxEntries + ")");
                    long total = 0;
                    foreach (var e in zip.Entries)
                    {
                        total += e.Length;
                        if (!SafeEntry(e.FullName)) return Fail(r, "ruta_peligrosa", "entry with a disallowed path: " + e.FullName);
                    }
                    if (total > MaxUncompressed) return Fail(r, "demasiado_grande", "uncompressed it would take " + total / (1024 * 1024) + " MB");

                    var me = Find(zip, ManifestName);
                    if (me == null) return Fail(r, "no_es_paquete", "does not contain " + ManifestName + ": not a BIMBRIDGE package");
                    byte[] manifestBytes;
                    using (var s = me.Open()) manifestBytes = ReadAll(s, 1024 * 1024);
                    r.ManifestSha256 = Sha(manifestBytes);
                    var m = Json.ParseObject(Encoding.UTF8.GetString(manifestBytes));
                    if (Arg.GetString(m, "esquema") != Schema) return Fail(r, "no_es_paquete", "unknown manifest schema");
                    r.Version = Arg.GetString(m, "version");
                    r.Commit = Arg.GetString(m, "commit");
                    r.Installer = Arg.GetString(m, "instalador");
                    r.DesdeMinima = Arg.GetString(m, "desde_minima");
                    r.Revit = Arg.GetStringList(m, "revit") ?? new List<string>();
                    if (m.TryGetValue("archivos", out var fa) && fa is Dictionary<string, object> files)
                        foreach (var kv in files) r.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (Updates.Parse(r.Version) == null || r.Files.Count == 0 || string.IsNullOrEmpty(r.Installer))
                        return Fail(r, "no_es_paquete", "incomplete manifest (version, archivos, instalador)");
                    if (!string.Equals(Path.GetFileName(r.Installer), InstallerName, StringComparison.OrdinalIgnoreCase) || !r.Files.ContainsKey(r.Installer))
                        return Fail(r, "no_es_paquete", "the installer must be " + InstallerName + " and be listed in 'archivos'");
                    r.InstallerSha256 = r.Files[r.Installer];

                    // Provenance. The exact manifest is signed; the manifest hashes bind each file to that signature.
                    var se = Find(zip, SignatureName);
                    if (se != null)
                    {
                        Dictionary<string, object> sig;
                        using (var s = se.Open()) sig = Json.ParseObject(Encoding.UTF8.GetString(ReadAll(s, 64 * 1024)));
                        r.KeyId = Arg.GetString(sig, "clave");
                        var key = keys.FirstOrDefault(k => k.Id == r.KeyId);
                        if (key == null) return Fail(r, "clave_desconocida", "signed with key " + r.KeyId + ", which is not among the trusted ones");
                        if (Arg.GetString(sig, "alg") != "RSA-SHA256") return Fail(r, "firma_invalida", "unsupported signature algorithm");
                        byte[] sigBytes;
                        try { sigBytes = Convert.FromBase64String(Arg.GetString(sig, "firma") ?? ""); } catch (FormatException) { return Fail(r, "firma_invalida", "unreadable signature"); }
                        if (!key.Verify(manifestBytes, sigBytes)) return Fail(r, "firma_invalida", "the signature does not match the manifest: the package was altered or was not signed by that key");
                        r.Signed = true;
                    }
                    else if (keys.Count > 0) return Fail(r, "firma_exigida", "trusted keys are configured and the package is not signed");
                    else
                    {
                        // 0.9.0 (P0): without a signature there is no provenance. Only the lab profile, explicitly enabled, accepts it.
                        var deny = policy.DenyUnsignedUpdate();
                        if (deny != null) return Fail(r, "firma_exigida", deny + ". With no keys configured, install with the installer (Setup.exe) by hand or configure claves_actualizacion.");
                    }

                    // Integrity: exactly the files in the manifest, each with its hash.
                    var bad = new List<string>();
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in zip.Entries)
                    {
                        var n = Norm(e.FullName);
                        if (n.EndsWith("/", StringComparison.Ordinal) || n == ManifestName || n == SignatureName) continue;
                        present.Add(n);
                        if (!r.Files.TryGetValue(n, out var exp)) { bad.Add(n + " is not in the manifest"); continue; }
                        string got; using (var s = e.Open()) got = SetupCore.Sha256(s);
                        if (!string.Equals(got, exp, StringComparison.OrdinalIgnoreCase)) bad.Add(n + " hash mismatch");
                    }
                    foreach (var f in r.Files.Keys) if (!present.Contains(f)) bad.Add(f + " missing from the ZIP");
                    if (bad.Count > 0) { r.Detalles.AddRange(bad); return Fail(r, "hashes_no_coinciden", bad.Count + " file(s) do not match the manifest"); }
                }
            }
            catch (InvalidDataException ex) { return Fail(r, "no_es_paquete", "damaged or invalid ZIP: " + ex.Message); }
            catch (FormatException ex) { return Fail(r, "no_es_paquete", "unreadable manifest: " + ex.Message); }

            // Compatibility.
            var nv = Updates.Parse(r.Version);
            var iv = Updates.Parse(installed);
            var missing = (installedRevit ?? Enumerable.Empty<string>()).Where(y => !r.Revit.Contains(y)).ToList();
            if (missing.Count > 0) return Fail(r, "incompatible", "the package does not include Revit " + string.Join(", ", missing) + ", which is installed");
            var min = Updates.Parse(r.DesdeMinima);
            if (min != null && iv != null && iv < min) return Fail(r, "incompatible", "requires at least " + Updates.Format(min) + " to be installed (found " + Updates.Format(iv) + ")");
            if (iv != null && nv <= iv && !allowOlder) return Fail(r, "misma_o_anterior", "the package is " + Updates.Format(nv) + " and " + Updates.Format(iv) + " is already installed");

            r.Estado = r.Signed ? "valido_firmado" : "valido_sin_firma";
            r.Mensaje = r.Signed
                ? "Signed by trusted key " + r.KeyId + "; " + r.Files.Count + " file(s) verified."
                : "NO provenance SIGNATURE: it was only checked that the files match their own manifest. Install it only if you know where it comes from.";
            return r;
        }

        // ---------------- stage ----------------

        /// <summary>
        /// Extracts to its own folder and re-verifies each hash on disk. Checks that the ZIP has not changed since inspection.
        /// Runs nothing. Writes pendiente.json (there can be only one pending update: the new one replaces the previous one).
        /// </summary>
        public static PendingUpdate Stage(ZipInspection insp, string updatesDir, string confirmedBy, bool unsignedConfirmed, bool allowOlder = false, ProfilePolicy policy = null)
        {
            policy = policy ?? new ProfilePolicy();
            if (insp == null || !insp.Ok) throw new InvalidOperationException("the package did not pass inspection: " + insp?.Mensaje);
            if (!insp.Signed) { var deny = policy.DenyUnsignedUpdate(); if (deny != null) throw new InvalidOperationException(deny); }
            if (insp.NeedsUnsignedConfirmation && !unsignedConfirmed) throw new InvalidOperationException("unsigned package: the user's explicit confirmation is missing");
            if (string.IsNullOrWhiteSpace(confirmedBy)) throw new InvalidOperationException("missing who confirmed");
            if (!string.Equals(SetupCore.FileSha(insp.ZipPath), insp.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the ZIP changed after it was inspected: open it again");

            var id = insp.Version + "-" + insp.ZipSha256.Substring(0, 8);
            var dir = Path.Combine(updatesDir, "preparadas", id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(insp.ZipPath))
                foreach (var e in zip.Entries)
                {
                    var n = Norm(e.FullName);
                    if (n.EndsWith("/", StringComparison.Ordinal)) continue;
                    if (!insp.Files.ContainsKey(n) && n != ManifestName && n != SignatureName) continue;
                    var target = Path.GetFullPath(Path.Combine(dir, n.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("path outside the staged folder: " + n);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var s = e.Open()) using (var d = File.Create(target)) s.CopyTo(d);
                }
            var bad = VerifyStaged(dir, insp.Files);
            if (bad.Count > 0) { TryDeleteDir(dir); throw new InvalidDataException("mismatch after extracting: " + string.Join("; ", bad)); }

            var p = new PendingUpdate
            {
                Id = id, Version = Updates.Format(Updates.Parse(insp.Version)), From = insp.Installed, Commit = insp.Commit, StagedDir = dir,
                Installer = Path.Combine(dir, insp.Installer.Replace('/', Path.DirectorySeparatorChar)), InstallerSha256 = insp.InstallerSha256,
                ZipSha256 = insp.ZipSha256, KeyId = insp.KeyId, Signed = insp.Signed, AllowOlder = allowOlder, Estado = "preparada",
                ConfirmedBy = confirmedBy, CreatedUtc = DateTime.UtcNow, Files = new Dictionary<string, string>(insp.Files, StringComparer.Ordinal),
                Perfil = policy.Name,
            };
            WriteJson(Path.Combine(updatesDir, PendingFile), p.ToJson());
            return p;
        }

        /// <summary>
        /// 0.9.0 (P0): before installing, the external process re-checks PROVENANCE against what is on disk, not against what
        /// pendiente.json says: the staged manifest must be signed by a trusted key and its hashes must be exactly
        /// those of the pending update. Unsigned, only if the current profile (read from config.json, not from the pending file) allows it.
        /// null = OK; otherwise, the reason.
        /// </summary>
        public static string VerifyStagedProvenance(PendingUpdate p, IList<TrustedKey> keys, ProfilePolicy policy)
        {
            keys = keys ?? new List<TrustedKey>();
            policy = policy ?? new ProfilePolicy();
            var mf = Path.Combine(p.StagedDir ?? "", ManifestName);
            if (!File.Exists(mf)) return "the staged manifest is missing";
            var manifestBytes = File.ReadAllBytes(mf);
            Dictionary<string, object> m;
            try { m = Json.ParseObject(Encoding.UTF8.GetString(manifestBytes)); } catch (FormatException) { return "unreadable staged manifest"; }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (m.TryGetValue("archivos", out var fa) && fa is Dictionary<string, object> fd)
                foreach (var kv in fd) files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture).ToLowerInvariant();
            if (files.Count != p.Files.Count || files.Any(kv => !p.Files.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase)))
                return "the hashes in pendiente.json are not those of the staged manifest";
            if (!string.Equals(Arg.GetString(m, "version"), p.Version, StringComparison.Ordinal) && Updates.Parse(Arg.GetString(m, "version")) != Updates.Parse(p.Version))
                return "the version in pendiente.json is not that of the staged manifest";
            var sf = Path.Combine(p.StagedDir, SignatureName);
            if (!File.Exists(sf))
            {
                if (p.Signed) return "pendiente.json says signed but there is no staged signature";
                var deny = policy.DenyUnsignedUpdate();
                return deny == null && p.Perfil == ProfilePolicy.Lab ? null : (deny ?? "unsigned package staged outside the lab profile");
            }
            Dictionary<string, object> sig;
            try { sig = Json.ParseObject(File.ReadAllText(sf)); } catch (FormatException) { return "unreadable staged signature"; }
            var key = keys.FirstOrDefault(k => k.Id == Arg.GetString(sig, "clave"));
            if (key == null) return "the staged signature is not from a configured trusted key";
            byte[] sb;
            try { sb = Convert.FromBase64String(Arg.GetString(sig, "firma") ?? ""); } catch (FormatException) { return "unreadable staged signature"; }
            return key.Verify(manifestBytes, sb) ? null : "the staged signature does not match the manifest";
        }

        public static List<string> VerifyStaged(string dir, IDictionary<string, string> files)
        {
            var bad = new List<string>();
            foreach (var kv in files)
            {
                var f = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) bad.Add(kv.Key + " missing");
                else if (!string.Equals(SetupCore.FileSha(f), kv.Value, StringComparison.OrdinalIgnoreCase)) bad.Add(kv.Key + " hash mismatch");
            }
            return bad;
        }

        public static PendingUpdate ReadPending(string updatesDir)
        {
            var f = Path.Combine(updatesDir, PendingFile);
            try { return File.Exists(f) ? PendingUpdate.FromJson(Json.ParseObject(File.ReadAllText(f))) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Cancel = delete pendiente.json; the waiting process sees it and exits without installing.</summary>
        public static bool CancelPending(string updatesDir)
        {
            var f = Path.Combine(updatesDir, PendingFile);
            if (!File.Exists(f)) return false;
            File.Delete(f);
            return true;
        }

        // ---------------- package (release) ----------------

        /// <summary>Creates an update ZIP. With <paramref name="signer"/> it is signed; without it, it stays UNSIGNED (will require confirmation).</summary>
        public static Dictionary<string, object> Build(string outZip, IDictionary<string, string> files, string version, string commit,
                                                       IEnumerable<string> revit, string desdeMinima, RSA signer)
        {
            var hashes = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in files) hashes[Norm(kv.Key)] = SetupCore.FileSha(kv.Value);
            var installer = hashes.Keys.FirstOrDefault(k => string.Equals(Path.GetFileName(k), InstallerName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException("missing " + InstallerName);
            var manifest = new Dictionary<string, object>
            {
                ["esquema"] = Schema, ["producto"] = "BIMBRIDGE", ["version"] = version, ["commit"] = commit,
                ["revit"] = revit.Cast<object>().ToList(), ["desde_minima"] = desdeMinima, ["instalador"] = installer,
                ["archivos"] = hashes.ToDictionary(k => k.Key, k => k.Value),
            };
            var mbytes = Encoding.UTF8.GetBytes(Json.Write(manifest));
            if (File.Exists(outZip)) File.Delete(outZip);
            using (var zip = ZipFile.Open(outZip, ZipArchiveMode.Create))
            {
                foreach (var kv in files) zip.CreateEntryFromFile(kv.Value, Norm(kv.Key), CompressionLevel.Optimal);
                using (var s = zip.CreateEntry(ManifestName).Open()) s.Write(mbytes, 0, mbytes.Length);
                if (signer != null)
                {
                    var pub = signer.ExportParameters(false);
                    var sig = new Dictionary<string, object>
                    {
                        ["clave"] = TrustedKey.IdOf(pub.Modulus), ["alg"] = "RSA-SHA256",
                        ["firma"] = Convert.ToBase64String(signer.SignData(mbytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                    };
                    var sb = Encoding.UTF8.GetBytes(Json.Write(sig));
                    using (var s = zip.CreateEntry(SignatureName).Open()) s.Write(sb, 0, sb.Length);
                }
            }
            manifest["zip_sha256"] = SetupCore.FileSha(outZip);
            manifest["firmado"] = signer != null;
            return manifest;
        }

        // ---------------- utilities ----------------

        public static bool SafeEntry(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Replace('\\', '/');
            if (n.StartsWith("/", StringComparison.Ordinal) || n.Contains(":") || n.IndexOf('\0') >= 0) return false;
            return n.Split('/').All(seg => seg != "..");
        }

        public static string Norm(string n) => n.Replace('\\', '/').TrimStart('/');

        internal static void WriteJson(string path, Dictionary<string, object> o)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(o));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static ZipArchiveEntry Find(ZipArchive zip, string name) => zip.Entries.FirstOrDefault(e => Norm(e.FullName) == name);

        private static ZipInspection Fail(ZipInspection r, string estado, string msg) { r.Estado = estado; r.Mensaje = msg; return r; }

        private static byte[] ReadAll(Stream s, int max)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) throw new InvalidDataException("control file too large"); }
                return ms.ToArray();
            }
        }

        private static string Sha(byte[] b) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }

        internal static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { } }
    }

    /// <summary>What the applier needs from the system; tests simulate it.</summary>
    /// <summary>0.9.0: keys and profile read from the user's config.json (same file and format as the add-in) for the external process.</summary>
    public static class UpdateConfig
    {
        public static string DefaultPath =>
            Environment.GetEnvironmentVariable("BIMBRIDGE_CONFIG") is string e && e.Length > 0 ? e
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bimbridge", "config.json");

        /// <summary>Unreadable config = production with no keys (the strictest): nothing unsigned is installed.</summary>
        public static void Load(string path, out IList<TrustedKey> keys, out ProfilePolicy policy)
        {
            keys = new List<TrustedKey>(); policy = new ProfilePolicy();
            try
            {
                if (!File.Exists(path)) return;
                var o = Json.ParseObject(File.ReadAllText(path));
                foreach (var k in Arg.GetStringList(o, "claves_actualizacion") ?? new List<string>()) keys.Add(TrustedKey.Parse(k));
                var lab = o.TryGetValue("laboratorio", out var lo) ? lo as Dictionary<string, object> : null;
                policy = ProfilePolicy.Parse(Arg.GetString(o, "perfil"), lab == null ? null : Arg.GetStringList(lab, "carpetas"),
                    lab != null && (Arg.GetBool(lab, "actualizacion_sin_firma") ?? false), Arg.GetStringList(o, "codigo_autorizado"));
            }
            catch (Exception) { keys = new List<TrustedKey>(); policy = new ProfilePolicy(); }
        }
    }

    public sealed class ApplyHost
    {
        public Func<bool> RevitRunning;
        public Action<TimeSpan> Sleep;
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;
        /// <summary>Runs an installer and returns its exit code.</summary>
        public Func<string, string, int> RunInstaller;
        public Func<InstalledInfo> ReadInstalled;
        public Func<Dictionary<string, object>> Diagnose;
        /// <summary>Saved installer of the previous version (to go back), or null.</summary>
        public Func<string, string> CachedSetup;
        public Action<string> Log = _ => { };
        /// <summary>0.9.0: current trusted keys and profile (from config.json), to re-verify provenance before installing.</summary>
        public Func<IList<TrustedKey>> Keys = () => new List<TrustedKey>();
        public Func<ProfilePolicy> Policy = () => new ProfilePolicy();
    }

    public static class RevitExitWait
    {
        public static bool Run(Func<bool> running, Func<bool> continueWaiting, Action<TimeSpan> sleep, TimeSpan poll)
        {
            var clear = 0;
            while (continueWaiting())
            {
                clear = running() ? 0 : clear + 1;
                if (clear == 2) return true;
                sleep(poll);
            }
            return false;
        }
    }

    /// <summary>
    /// The external process that applies a staged update. Waits until no Revit is open (two consecutive reads),
    /// never closes or kills Revit, re-verifies the installer, runs it, verifies the installation and, if it is not right, goes back to the previous one.
    /// </summary>
    public static class PendingApplier
    {
        public static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

        public static Dictionary<string, object> Run(string updatesDir, ApplyHost h, TimeSpan maxWait)
        {
            var p = ZipUpdate.ReadPending(updatesDir);
            if (p == null) return Finish(updatesDir, null, "sin_pendiente", "there is no staged update");
            var start = h.UtcNow();
            SetState(updatesDir, p, "esperando_cierre_revit");
            h.Log("Waiting for the user to close Revit to install " + p.Version + ". BIMBRIDGE does not close Revit.");
            string stopped = null;
            bool ContinueWaiting()
            {
                var cur = ZipUpdate.ReadPending(updatesDir);
                if (cur == null || cur.Id != p.Id) stopped = "cancelada_usuario";
                else if (h.UtcNow() - start > maxWait) stopped = "cancelada_tiempo";
                return stopped == null;
            }
            if (!RevitExitWait.Run(h.RevitRunning, ContinueWaiting, h.Sleep, Poll))
                return stopped == "cancelada_usuario"
                    ? Finish(updatesDir, p, stopped, "the staged update was cancelled or replaced")
                    : Finish(updatesDir, p, stopped, "Revit stayed open for more than " + maxWait.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h; nothing was installed");

            var prov = ZipUpdate.VerifyStagedProvenance(p, h.Keys(), h.Policy());
            if (prov != null) return Finish(updatesDir, p, "procedencia_rechazada", prov + ". Nothing was installed.");
            var bad = ZipUpdate.VerifyStaged(p.StagedDir, p.Files);
            if (bad.Count > 0 || !string.Equals(SetupCore.FileSha(p.Installer), p.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                return Finish(updatesDir, p, "paquete_alterado", "the staged files changed: " + string.Join("; ", bad) + ". Nothing was installed.");

            var before = h.ReadInstalled()?.ProductVersion;
            SetState(updatesDir, p, "instalando");
            var code = h.RunInstaller(p.Installer, "/actualizar" + (p.AllowOlder ? " /permitir-anterior" : ""));
            var problems = Check(h, p.Version);
            if (code == 0 && problems.Count == 0)
                return Finish(updatesDir, p, "aplicada_verificada", "installed and verified " + p.Version + " (previously " + before + ")", new Dictionary<string, object> { ["antes"] = before });

            // Go back to the previous one.
            var why = "installer exit code " + code + (problems.Count > 0 ? "; " + string.Join("; ", problems) : "");
            var prev = string.IsNullOrEmpty(before) ? null : h.CachedSetup(before);
            if (prev == null || !File.Exists(prev))
                return Finish(updatesDir, p, "fallo_sin_reversion", why + ". There is no saved installer of " + before + " to go back to.", new Dictionary<string, object> { ["antes"] = before });
            var rc = h.RunInstaller(prev, "/actualizar /permitir-anterior");
            var back = Check(h, before);
            return rc == 0 && back.Count == 0
                ? Finish(updatesDir, p, "revertida", why + ". Went back to " + before + " and verified it.", new Dictionary<string, object> { ["antes"] = before })
                : Finish(updatesDir, p, "fallo_reversion", why + ". Going back to " + before + " could not be verified either (exit code " + rc + (back.Count > 0 ? "; " + string.Join("; ", back) : "") + ").", new Dictionary<string, object> { ["antes"] = before });
        }

        /// <summary>Installation problems against the expected version: recorded version and hashes per year and of the bridge.</summary>
        public static List<string> Check(ApplyHost h, string expected)
        {
            var res = new List<string>();
            var inst = h.ReadInstalled()?.ProductVersion;
            if (Updates.Parse(inst) != Updates.Parse(expected)) res.Add("installed version " + (inst ?? "none") + ", expected " + expected);
            var d = h.Diagnose();
            if (d != null && d.TryGetValue("addin", out var a) && a is Dictionary<string, object> years)
                foreach (var kv in years)
                    if (kv.Value is Dictionary<string, object> y && !(y.TryGetValue("coinciden", out var c) && c is bool b && b)) res.Add("Revit " + kv.Key + ": files do not match");
            if (d != null && d.TryGetValue("puente", out var pu) && pu is Dictionary<string, object> pd && pd.TryGetValue("coincide", out var pc) && pc is bool pb && !pb) res.Add("bridge: does not match");
            return res;
        }

        private static void SetState(string dir, PendingUpdate p, string estado)
        {
            var cur = ZipUpdate.ReadPending(dir);
            if (cur == null || cur.Id != p.Id) return;
            p.Estado = estado;
            ZipUpdate.WriteJson(Path.Combine(dir, ZipUpdate.PendingFile), p.ToJson());
        }

        private static Dictionary<string, object> Finish(string dir, PendingUpdate p, string estado, string msg, Dictionary<string, object> extra = null)
        {
            var r = new Dictionary<string, object>
            {
                ["esquema"] = "bimbridge.resultado_actualizacion/1", ["estado"] = estado, ["mensaje"] = msg,
                ["id"] = p?.Id, ["version"] = p?.Version, ["commit"] = p?.Commit, ["firmado"] = p?.Signed, ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            if (extra != null) foreach (var kv in extra) r[kv.Key] = kv.Value;
            ZipUpdate.WriteJson(Path.Combine(dir, ZipUpdate.ResultFile), r);
            if (p != null && estado != "cancelada_usuario")
            {
                var cur = ZipUpdate.ReadPending(dir);
                if (cur != null && cur.Id == p.Id) ZipUpdate.CancelPending(dir);
            }
            return r;
        }
    }
}
