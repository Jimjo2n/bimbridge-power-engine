// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Setup
{
    /// <summary>Clave pública de confianza para firmar paquetes de actualización (RSA-SHA256). La gestión de claves la decide IT.</summary>
    public sealed class TrustedKey
    {
        public string Id;
        public byte[] Modulus, Exponent;

        /// <summary>Formato de configuración: "RSA:&lt;módulo base64&gt;:&lt;exponente base64&gt;". El id son los 16 primeros hex del SHA-256 del módulo.</summary>
        public static TrustedKey Parse(string text)
        {
            var p = (text ?? "").Trim().Split(':');
            if (p.Length != 3 || !string.Equals(p[0], "RSA", StringComparison.OrdinalIgnoreCase)) throw new FormatException("clave de actualización: se espera RSA:<módulo>:<exponente>");
            var k = new TrustedKey { Modulus = Convert.FromBase64String(p[1]), Exponent = Convert.FromBase64String(p[2]) };
            if (k.Modulus.Length < 256) throw new FormatException("clave de actualización: RSA de al menos 2048 bits");
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

    /// <summary>Lo que se averiguó de un ZIP sin instalar nada: identidad, procedencia, integridad y compatibilidad.</summary>
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
        /// <summary>Se puede preparar. Sin firma exige además una confirmación explícita del usuario.</summary>
        public bool Ok => Estado == "valido_firmado" || Estado == "valido_sin_firma";
        public bool NeedsUnsignedConfirmation => Estado == "valido_sin_firma";
    }

    /// <summary>Una actualización preparada que espera a que el usuario cierre Revit. Se guarda en pendiente.json.</summary>
    public sealed class PendingUpdate
    {
        public string Id, Version, From, Commit, StagedDir, Installer, InstallerSha256, ZipSha256, KeyId, Estado, ConfirmedBy;
        /// <summary>0.9.0: perfil con el que se preparó (produccion | laboratorio). Un paquete sin firma sólo se aplica si es laboratorio.</summary>
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
    /// 0.8.0 (BIMBRIDGE → Developer Tools → Actualizar desde ZIP). Actualización convencional, sin hot reload:
    /// inspeccionar (manifiesto, firma, hashes, compatibilidad) → el usuario confirma → preparar (extraer y re-verificar) →
    /// un proceso externo espera a que el USUARIO cierre Revit (nunca lo cierra) → instala → verifica → si falla, vuelve a la anterior.
    /// Nada se descarga de internet. La IA no tiene ninguna herramienta que llegue aquí: sólo el botón de la cinta.
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

        // ---------------- inspeccionar ----------------

        /// <param name="installed">Versión instalada (install.json); null si no hay.</param>
        /// <param name="installedRevit">Años de Revit de la instalación actual: el paquete debe cubrirlos todos.</param>
        /// <param name="keys">Claves de confianza. Si hay alguna, la firma es obligatoria.</param>
        /// <param name="policy">0.9.0: perfil. null = producción: sólo paquetes firmados por una clave de confianza.</param>
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
                    if (zip.Entries.Count > MaxEntries) return Fail(r, "demasiado_grande", "el ZIP tiene " + zip.Entries.Count + " entradas (máximo " + MaxEntries + ")");
                    long total = 0;
                    foreach (var e in zip.Entries)
                    {
                        total += e.Length;
                        if (!SafeEntry(e.FullName)) return Fail(r, "ruta_peligrosa", "entrada con ruta no permitida: " + e.FullName);
                    }
                    if (total > MaxUncompressed) return Fail(r, "demasiado_grande", "descomprimido ocuparía " + total / (1024 * 1024) + " MB");

                    var me = Find(zip, ManifestName);
                    if (me == null) return Fail(r, "no_es_paquete", "no contiene " + ManifestName + ": no es un paquete de BIMBRIDGE");
                    byte[] manifestBytes;
                    using (var s = me.Open()) manifestBytes = ReadAll(s, 1024 * 1024);
                    r.ManifestSha256 = Sha(manifestBytes);
                    var m = Json.ParseObject(Encoding.UTF8.GetString(manifestBytes));
                    if (Arg.GetString(m, "esquema") != Schema) return Fail(r, "no_es_paquete", "esquema de manifiesto desconocido");
                    r.Version = Arg.GetString(m, "version");
                    r.Commit = Arg.GetString(m, "commit");
                    r.Installer = Arg.GetString(m, "instalador");
                    r.DesdeMinima = Arg.GetString(m, "desde_minima");
                    r.Revit = Arg.GetStringList(m, "revit") ?? new List<string>();
                    if (m.TryGetValue("archivos", out var fa) && fa is Dictionary<string, object> files)
                        foreach (var kv in files) r.Files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (Updates.Parse(r.Version) == null || r.Files.Count == 0 || string.IsNullOrEmpty(r.Installer))
                        return Fail(r, "no_es_paquete", "manifiesto incompleto (version, archivos, instalador)");
                    if (!string.Equals(Path.GetFileName(r.Installer), InstallerName, StringComparison.OrdinalIgnoreCase) || !r.Files.ContainsKey(r.Installer))
                        return Fail(r, "no_es_paquete", "el instalador debe ser " + InstallerName + " y estar en 'archivos'");
                    r.InstallerSha256 = r.Files[r.Installer];

                    // Procedencia. Se firma el manifiesto exacto; los hashes del manifiesto atan cada archivo a esa firma.
                    var se = Find(zip, SignatureName);
                    if (se != null)
                    {
                        Dictionary<string, object> sig;
                        using (var s = se.Open()) sig = Json.ParseObject(Encoding.UTF8.GetString(ReadAll(s, 64 * 1024)));
                        r.KeyId = Arg.GetString(sig, "clave");
                        var key = keys.FirstOrDefault(k => k.Id == r.KeyId);
                        if (key == null) return Fail(r, "clave_desconocida", "firmado con la clave " + r.KeyId + ", que no está entre las de confianza");
                        if (Arg.GetString(sig, "alg") != "RSA-SHA256") return Fail(r, "firma_invalida", "algoritmo de firma no admitido");
                        byte[] sigBytes;
                        try { sigBytes = Convert.FromBase64String(Arg.GetString(sig, "firma") ?? ""); } catch (FormatException) { return Fail(r, "firma_invalida", "firma ilegible"); }
                        if (!key.Verify(manifestBytes, sigBytes)) return Fail(r, "firma_invalida", "la firma no corresponde al manifiesto: el paquete fue alterado o no lo firmó esa clave");
                        r.Signed = true;
                    }
                    else if (keys.Count > 0) return Fail(r, "firma_exigida", "hay claves de confianza configuradas y el paquete no está firmado");
                    else
                    {
                        // 0.9.0 (P0): sin firma no hay procedencia. Sólo el perfil de laboratorio, habilitado expresamente, lo admite.
                        var deny = policy.DenyUnsignedUpdate();
                        if (deny != null) return Fail(r, "firma_exigida", deny + ". Sin claves configuradas, instale con el instalador (Setup.exe) a mano o configure claves_actualizacion.");
                    }

                    // Integridad: exactamente los archivos del manifiesto, cada uno con su hash.
                    var bad = new List<string>();
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in zip.Entries)
                    {
                        var n = Norm(e.FullName);
                        if (n.EndsWith("/", StringComparison.Ordinal) || n == ManifestName || n == SignatureName) continue;
                        present.Add(n);
                        if (!r.Files.TryGetValue(n, out var exp)) { bad.Add(n + " no está en el manifiesto"); continue; }
                        string got; using (var s = e.Open()) got = SetupCore.Sha256(s);
                        if (!string.Equals(got, exp, StringComparison.OrdinalIgnoreCase)) bad.Add(n + " hash distinto");
                    }
                    foreach (var f in r.Files.Keys) if (!present.Contains(f)) bad.Add(f + " falta en el ZIP");
                    if (bad.Count > 0) { r.Detalles.AddRange(bad); return Fail(r, "hashes_no_coinciden", bad.Count + " archivo(s) no coinciden con el manifiesto"); }
                }
            }
            catch (InvalidDataException ex) { return Fail(r, "no_es_paquete", "ZIP dañado o no válido: " + ex.Message); }
            catch (FormatException ex) { return Fail(r, "no_es_paquete", "manifiesto ilegible: " + ex.Message); }

            // Compatibilidad.
            var nv = Updates.Parse(r.Version);
            var iv = Updates.Parse(installed);
            var missing = (installedRevit ?? Enumerable.Empty<string>()).Where(y => !r.Revit.Contains(y)).ToList();
            if (missing.Count > 0) return Fail(r, "incompatible", "el paquete no incluye Revit " + string.Join(", ", missing) + ", que está instalado");
            var min = Updates.Parse(r.DesdeMinima);
            if (min != null && iv != null && iv < min) return Fail(r, "incompatible", "requiere tener instalada al menos la " + Updates.Format(min) + " (hay " + Updates.Format(iv) + ")");
            if (iv != null && nv <= iv && !allowOlder) return Fail(r, "misma_o_anterior", "el paquete es la " + Updates.Format(nv) + " y ya está instalada la " + Updates.Format(iv));

            r.Estado = r.Signed ? "valido_firmado" : "valido_sin_firma";
            r.Mensaje = r.Signed
                ? "Firmado por la clave de confianza " + r.KeyId + "; " + r.Files.Count + " archivo(s) verificados."
                : "SIN FIRMA de procedencia: sólo se comprobó que los archivos coinciden con su propio manifiesto. Instálelo únicamente si sabe de dónde viene.";
            return r;
        }

        // ---------------- preparar ----------------

        /// <summary>
        /// Extrae a una carpeta propia y vuelve a verificar cada hash en disco. Comprueba que el ZIP no cambió desde la inspección.
        /// No ejecuta nada. Escribe pendiente.json (sólo puede haber una pendiente: la nueva reemplaza a la anterior).
        /// </summary>
        public static PendingUpdate Stage(ZipInspection insp, string updatesDir, string confirmedBy, bool unsignedConfirmed, bool allowOlder = false, ProfilePolicy policy = null)
        {
            policy = policy ?? new ProfilePolicy();
            if (insp == null || !insp.Ok) throw new InvalidOperationException("el paquete no pasó la inspección: " + insp?.Mensaje);
            if (!insp.Signed) { var deny = policy.DenyUnsignedUpdate(); if (deny != null) throw new InvalidOperationException(deny); }
            if (insp.NeedsUnsignedConfirmation && !unsignedConfirmed) throw new InvalidOperationException("paquete sin firma: falta la confirmación explícita del usuario");
            if (string.IsNullOrWhiteSpace(confirmedBy)) throw new InvalidOperationException("falta quién confirmó");
            if (!string.Equals(SetupCore.FileSha(insp.ZipPath), insp.ZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("el ZIP cambió después de inspeccionarlo: vuelva a abrirlo");

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
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ruta fuera de la carpeta preparada: " + n);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var s = e.Open()) using (var d = File.Create(target)) s.CopyTo(d);
                }
            var bad = VerifyStaged(dir, insp.Files);
            if (bad.Count > 0) { TryDeleteDir(dir); throw new InvalidDataException("tras extraer no coinciden: " + string.Join("; ", bad)); }

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
        /// 0.9.0 (P0): antes de instalar, el proceso externo vuelve a comprobar la PROCEDENCIA con lo que hay en disco, no con lo que
        /// dice pendiente.json: el manifiesto preparado debe estar firmado por una clave de confianza y sus hashes deben ser exactamente
        /// los de la actualización pendiente. Sin firma, sólo si el perfil actual (leído de config.json, no del pendiente) lo admite.
        /// null = conforme; si no, el motivo.
        /// </summary>
        public static string VerifyStagedProvenance(PendingUpdate p, IList<TrustedKey> keys, ProfilePolicy policy)
        {
            keys = keys ?? new List<TrustedKey>();
            policy = policy ?? new ProfilePolicy();
            var mf = Path.Combine(p.StagedDir ?? "", ManifestName);
            if (!File.Exists(mf)) return "falta el manifiesto preparado";
            var manifestBytes = File.ReadAllBytes(mf);
            Dictionary<string, object> m;
            try { m = Json.ParseObject(Encoding.UTF8.GetString(manifestBytes)); } catch (FormatException) { return "manifiesto preparado ilegible"; }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (m.TryGetValue("archivos", out var fa) && fa is Dictionary<string, object> fd)
                foreach (var kv in fd) files[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture).ToLowerInvariant();
            if (files.Count != p.Files.Count || files.Any(kv => !p.Files.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase)))
                return "los hashes de pendiente.json no son los del manifiesto preparado";
            if (!string.Equals(Arg.GetString(m, "version"), p.Version, StringComparison.Ordinal) && Updates.Parse(Arg.GetString(m, "version")) != Updates.Parse(p.Version))
                return "la versión de pendiente.json no es la del manifiesto preparado";
            var sf = Path.Combine(p.StagedDir, SignatureName);
            if (!File.Exists(sf))
            {
                if (p.Signed) return "pendiente.json dice firmado pero no hay firma preparada";
                var deny = policy.DenyUnsignedUpdate();
                return deny == null && p.Perfil == ProfilePolicy.Lab ? null : (deny ?? "paquete sin firma preparado fuera del perfil de laboratorio");
            }
            Dictionary<string, object> sig;
            try { sig = Json.ParseObject(File.ReadAllText(sf)); } catch (FormatException) { return "firma preparada ilegible"; }
            var key = keys.FirstOrDefault(k => k.Id == Arg.GetString(sig, "clave"));
            if (key == null) return "la firma preparada no es de una clave de confianza configurada";
            byte[] sb;
            try { sb = Convert.FromBase64String(Arg.GetString(sig, "firma") ?? ""); } catch (FormatException) { return "firma preparada ilegible"; }
            return key.Verify(manifestBytes, sb) ? null : "la firma preparada no corresponde al manifiesto";
        }

        public static List<string> VerifyStaged(string dir, IDictionary<string, string> files)
        {
            var bad = new List<string>();
            foreach (var kv in files)
            {
                var f = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) bad.Add(kv.Key + " falta");
                else if (!string.Equals(SetupCore.FileSha(f), kv.Value, StringComparison.OrdinalIgnoreCase)) bad.Add(kv.Key + " hash distinto");
            }
            return bad;
        }

        public static PendingUpdate ReadPending(string updatesDir)
        {
            var f = Path.Combine(updatesDir, PendingFile);
            try { return File.Exists(f) ? PendingUpdate.FromJson(Json.ParseObject(File.ReadAllText(f))) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Cancelar = borrar pendiente.json; el proceso que espera lo ve y termina sin instalar.</summary>
        public static bool CancelPending(string updatesDir)
        {
            var f = Path.Combine(updatesDir, PendingFile);
            if (!File.Exists(f)) return false;
            File.Delete(f);
            return true;
        }

        // ---------------- empaquetar (release) ----------------

        /// <summary>Crea un ZIP de actualización. Con <paramref name="signer"/> lo firma; sin él queda SIN FIRMA (exigirá confirmación).</summary>
        public static Dictionary<string, object> Build(string outZip, IDictionary<string, string> files, string version, string commit,
                                                       IEnumerable<string> revit, string desdeMinima, RSA signer)
        {
            var hashes = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in files) hashes[Norm(kv.Key)] = SetupCore.FileSha(kv.Value);
            var installer = hashes.Keys.FirstOrDefault(k => string.Equals(Path.GetFileName(k), InstallerName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException("falta " + InstallerName);
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

        // ---------------- utilidades ----------------

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
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > max) throw new InvalidDataException("archivo de control demasiado grande"); }
                return ms.ToArray();
            }
        }

        private static string Sha(byte[] b) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }

        internal static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { } }
    }

    /// <summary>Lo que el aplicador necesita del sistema; las pruebas lo simulan.</summary>
    /// <summary>0.9.0: claves y perfil leídos de config.json del usuario (mismo archivo y formato que el add-in) para el proceso externo.</summary>
    public static class UpdateConfig
    {
        public static string DefaultPath =>
            Environment.GetEnvironmentVariable("PDI_REVITMCP_CONFIG") is string e && e.Length > 0 ? e
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDI.RevitMcp", "config.json");

        /// <summary>Config ilegible = producción sin claves (lo más estricto): no se instala nada sin firma.</summary>
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
        /// <summary>Ejecuta un instalador y devuelve su código de salida.</summary>
        public Func<string, string, int> RunInstaller;
        public Func<InstalledInfo> ReadInstalled;
        public Func<Dictionary<string, object>> Diagnose;
        /// <summary>Instalador guardado de la versión anterior (para volver atrás), o null.</summary>
        public Func<string, string> CachedSetup;
        public Action<string> Log = _ => { };
        /// <summary>0.9.0: claves de confianza y perfil vigentes (de config.json), para re-verificar la procedencia antes de instalar.</summary>
        public Func<IList<TrustedKey>> Keys = () => new List<TrustedKey>();
        public Func<ProfilePolicy> Policy = () => new ProfilePolicy();
    }

    /// <summary>
    /// El proceso externo que aplica una actualización preparada. Espera a que no haya ningún Revit abierto (dos lecturas seguidas),
    /// nunca cierra ni mata Revit, re-verifica el instalador, lo ejecuta, verifica la instalación y, si no queda bien, vuelve a la anterior.
    /// </summary>
    public static class PendingApplier
    {
        public static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

        public static Dictionary<string, object> Run(string updatesDir, ApplyHost h, TimeSpan maxWait)
        {
            var p = ZipUpdate.ReadPending(updatesDir);
            if (p == null) return Finish(updatesDir, null, "sin_pendiente", "no hay ninguna actualización preparada");
            var start = h.UtcNow();
            SetState(updatesDir, p, "esperando_cierre_revit");
            h.Log("Esperando a que el usuario cierre Revit para instalar " + p.Version + ". BIMBRIDGE no cierra Revit.");
            var clear = 0;
            while (clear < 2)
            {
                var cur = ZipUpdate.ReadPending(updatesDir);
                if (cur == null || cur.Id != p.Id) return Finish(updatesDir, p, "cancelada_usuario", "se canceló o reemplazó la actualización preparada");
                if (h.UtcNow() - start > maxWait) return Finish(updatesDir, p, "cancelada_tiempo", "Revit siguió abierto más de " + maxWait.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h; no se instaló nada");
                clear = h.RevitRunning() ? 0 : clear + 1;
                if (clear < 2) h.Sleep(Poll);
            }

            var prov = ZipUpdate.VerifyStagedProvenance(p, h.Keys(), h.Policy());
            if (prov != null) return Finish(updatesDir, p, "procedencia_rechazada", prov + ". No se instaló nada.");
            var bad = ZipUpdate.VerifyStaged(p.StagedDir, p.Files);
            if (bad.Count > 0 || !string.Equals(SetupCore.FileSha(p.Installer), p.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                return Finish(updatesDir, p, "paquete_alterado", "los archivos preparados cambiaron: " + string.Join("; ", bad) + ". No se instaló nada.");

            var before = h.ReadInstalled()?.ProductVersion;
            SetState(updatesDir, p, "instalando");
            var code = h.RunInstaller(p.Installer, "/actualizar" + (p.AllowOlder ? " /permitir-anterior" : ""));
            var problems = Check(h, p.Version);
            if (code == 0 && problems.Count == 0)
                return Finish(updatesDir, p, "aplicada_verificada", "instalada y verificada la " + p.Version + " (antes " + before + ")", new Dictionary<string, object> { ["antes"] = before });

            // Volver a la anterior.
            var why = "instalador código " + code + (problems.Count > 0 ? "; " + string.Join("; ", problems) : "");
            var prev = string.IsNullOrEmpty(before) ? null : h.CachedSetup(before);
            if (prev == null || !File.Exists(prev))
                return Finish(updatesDir, p, "fallo_sin_reversion", why + ". No hay instalador guardado de la " + before + " para volver atrás.", new Dictionary<string, object> { ["antes"] = before });
            var rc = h.RunInstaller(prev, "/actualizar /permitir-anterior");
            var back = Check(h, before);
            return rc == 0 && back.Count == 0
                ? Finish(updatesDir, p, "revertida", why + ". Se volvió a la " + before + " y se verificó.", new Dictionary<string, object> { ["antes"] = before })
                : Finish(updatesDir, p, "fallo_reversion", why + ". La vuelta a la " + before + " tampoco quedó verificada (código " + rc + (back.Count > 0 ? "; " + string.Join("; ", back) : "") + ").", new Dictionary<string, object> { ["antes"] = before });
        }

        /// <summary>Problemas de la instalación frente a la versión esperada: versión registrada y hashes por año y del puente.</summary>
        public static List<string> Check(ApplyHost h, string expected)
        {
            var res = new List<string>();
            var inst = h.ReadInstalled()?.ProductVersion;
            if (Updates.Parse(inst) != Updates.Parse(expected)) res.Add("versión instalada " + (inst ?? "ninguna") + ", se esperaba " + expected);
            var d = h.Diagnose();
            if (d != null && d.TryGetValue("addin", out var a) && a is Dictionary<string, object> years)
                foreach (var kv in years)
                    if (kv.Value is Dictionary<string, object> y && !(y.TryGetValue("coinciden", out var c) && c is bool b && b)) res.Add("Revit " + kv.Key + ": archivos no coinciden");
            if (d != null && d.TryGetValue("puente", out var pu) && pu is Dictionary<string, object> pd && pd.TryGetValue("coincide", out var pc) && pc is bool pb && !pb) res.Add("puente: no coincide");
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
