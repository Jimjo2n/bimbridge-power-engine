// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PDI.RevitMcp.Setup
{
    /// <summary>Un instalador encontrado en una carpeta: el .exe suelto o dentro de un .zip descargado.</summary>
    public sealed class UpdateCandidate
    {
        public Version Version;
        /// <summary>Archivo que vio el usuario (el .zip o el .exe en Descargas, Escritorio…).</summary>
        public string Source;
        /// <summary>El PDI.RevitMcp.Setup.exe listo para ejecutar (si venía en .zip, ya extraído en la carpeta temporal).</summary>
        public string Exe;
        public string VersionText => Updates.Format(Version);
    }

    /// <summary>
    /// Actualización sin reinstalar a mano (flujo de actualización): el add-in busca instaladores nuevos en
    /// Descargas, Escritorio, la carpeta de actualizaciones y las que configure IT, y el instalador en modo
    /// /actualizar los aplica con las mismas opciones, incluso con Revit abierto (la versión nueva entra al reiniciar).
    /// Sólo se aceptan ensamblados llamados PDI.RevitMcp.Setup; nunca se descarga nada de internet.
    /// </summary>
    public static class Updates
    {
        public const string Prefix = "PDI.RevitMcp.Setup";
        public const string LeftoverMark = ".viejo-";

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, (long size, DateTime mtime, UpdateCandidate c)> Cache =
            new Dictionary<string, (long, DateTime, UpdateCandidate)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cómo se lee la versión de un .exe (las pruebas lo sustituyen; en Windows es <see cref="ReadVersion"/>).</summary>
        public static Func<string, Version> VersionReader = ReadVersion;

        public static string Format(Version v) => v == null ? "?" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);

        public static Version Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Version.TryParse(s.Trim(), out var v) ? Normalize(v) : null;
        }

        /// <summary>0.2.1 y 0.2.1.0 son la misma versión.</summary>
        public static Version Normalize(Version v) => v == null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>Carpetas donde se buscan instaladores: Descargas, Escritorio, la de actualizaciones y las extra (red, OneDrive de IT).</summary>
        public static List<string> Folders(SetupPaths p, IEnumerable<string> extra = null)
        {
            var list = new List<string> { p.Downloads, p.Desktop, p.UpdatesDir };
            if (extra != null) list.AddRange(extra.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Environment.ExpandEnvironmentVariables));
            return list.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Todos los instaladores válidos en esas carpetas (y en subcarpetas de primer nivel llamadas PDI.RevitMcp.Setup*,
        /// que es lo que deja "Extraer todo"). Los .zip se abren y su .exe se copia a <paramref name="tempDir"/>.
        /// </summary>
        public static List<UpdateCandidate> Scan(IEnumerable<string> folders, string tempDir)
        {
            var found = new List<UpdateCandidate>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in SafeFiles(folder))
                {
                    var c = Inspect(file, tempDir);
                    if (c != null) found.Add(c);
                }
                foreach (var sub in SafeDirs(folder))
                    foreach (var file in SafeFiles(sub))
                    {
                        var c = Inspect(file, tempDir);
                        if (c != null) found.Add(c);
                    }
            }
            return found;
        }

        /// <summary>El más nuevo que sea mayor que <paramref name="than"/>, o null.</summary>
        public static UpdateCandidate Newest(IEnumerable<UpdateCandidate> found, Version than)
        {
            than = Normalize(than) ?? new Version(0, 0, 0);
            return found.Where(c => c.Version > than).OrderByDescending(c => c.Version).FirstOrDefault();
        }

        /// <summary>Versión de un PDI.RevitMcp.Setup.exe leyendo sólo sus metadatos (no lo ejecuta ni lo carga). Null si no es nuestro.</summary>
        public static Version ReadVersion(string exe)
        {
            try
            {
                var n = AssemblyName.GetAssemblyName(exe);
                if (!string.Equals(n.Name, Prefix, StringComparison.Ordinal)) return null;
                return Normalize(n.Version);
            }
            catch (Exception) { return null; }
        }

        private static UpdateCandidate Inspect(string file, string tempDir)
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".exe" && ext != ".zip") return null;
            if (name.IndexOf(LeftoverMark, StringComparison.OrdinalIgnoreCase) >= 0) return null;

            FileInfo info;
            try { info = new FileInfo(file); } catch (Exception) { return null; }
            lock (CacheLock)
                if (Cache.TryGetValue(file, out var hit) && hit.size == info.Length && hit.mtime == info.LastWriteTimeUtc
                    && (hit.c == null || File.Exists(hit.c.Exe)))
                    return hit.c;

            UpdateCandidate c = null;
            if (ext == ".exe")
            {
                var v = VersionReader(file);
                if (v != null) c = new UpdateCandidate { Version = v, Source = file, Exe = file };
            }
            else
            {
                c = FromZip(file, info, tempDir);
            }
            lock (CacheLock) Cache[file] = (info.Length, info.LastWriteTimeUtc, c);
            return c;
        }

        private static UpdateCandidate FromZip(string zipPath, FileInfo info, string tempDir)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), Prefix + ".exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;
                    Directory.CreateDirectory(tempDir);
                    var key = Hash(zipPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                    var dest = Path.Combine(tempDir, Prefix + "-" + key + ".exe");
                    if (!File.Exists(dest))
                    {
                        var tmp = dest + ".part";
                        using (var s = entry.Open())
                        using (var d = File.Create(tmp)) s.CopyTo(d);
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(tmp, dest);
                    }
                    var v = VersionReader(dest);
                    if (v == null) { TryDelete(dest); return null; }
                    return new UpdateCandidate { Version = v, Source = zipPath, Exe = dest };
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Instaladores ya superados por <paramref name="installed"/> en las carpetas del usuario (no en la caché propia),
        /// para mandarlos a la papelera después de actualizar. Así no se acumulan copias en Descargas.
        /// </summary>
        public static List<string> Superseded(IEnumerable<UpdateCandidate> found, Version installed, string cacheDir)
        {
            installed = Normalize(installed);
            return found.Where(c => c.Version <= installed)
                        .Select(c => c.Source)
                        .Where(s => cacheDir == null || !Path.GetFullPath(s).StartsWith(Path.GetFullPath(cacheDir), StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Borra lo que quedó renombrado como *.viejo-* al reemplazar archivos en uso (ya no lo están tras reiniciar).</summary>
        public static int DeleteLeftovers(IEnumerable<string> dirs)
        {
            var n = 0;
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                string[] files;
                try { files = Directory.GetFiles(d, "*" + LeftoverMark + "*", SearchOption.AllDirectories); }
                catch (Exception) { continue; }
                foreach (var f in files) if (TryDelete(f)) n++;
            }
            return n;
        }

        internal static bool TryDelete(string f)
        {
            try { File.Delete(f); return true; } catch (Exception) { return false; }
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, Prefix + "*"); } catch (Exception) { return new string[0]; }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir, Prefix + "*"); } catch (Exception) { return new string[0]; }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(6).Select(b => b.ToString("x2")));
        }
    }
}
