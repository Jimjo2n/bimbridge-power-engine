// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Setup
{
    /// <summary>
    /// Respaldo y retorno de instalación (Native Validation): add-ins, puente y Power Engine, configuración, registro de instalación,
    /// copia del instalador y config.toml de Codex. Lo que no existía queda anotado y se borra al volver.
    /// El retorno verifica todos los hashes antes de tocar nada y guarda antes el estado actual, para poder deshacerlo.
    /// No cierra ni detecta Revit: quien llama exige que esté cerrado.
    /// </summary>
    public static class InstallBackup
    {
        public const string ManifestName = "respaldo.json";
        public const int Keep = 3;

        public static string Root(SetupPaths p) => Path.Combine(p.Home, "respaldos");

        /// <summary>Unidades del respaldo: nombre estable → archivo o carpeta real.</summary>
        private static IEnumerable<KeyValuePair<string, string>> Units(SetupPaths p, IEnumerable<string> versions)
        {
            foreach (var v in versions)
            {
                yield return new KeyValuePair<string, string>("addin/" + v + "/PDI.RevitMcp.addin", Path.Combine(p.AddinsDir(v), "PDI.RevitMcp.addin"));
                yield return new KeyValuePair<string, string>("addin/" + v + "/PDI.RevitMcp", Path.Combine(p.AddinsDir(v), "PDI.RevitMcp"));
            }
            yield return new KeyValuePair<string, string>("bin", p.BinDir);
            yield return new KeyValuePair<string, string>("config.json", p.ConfigFile);
            yield return new KeyValuePair<string, string>("install.json", p.InstallRecord);
            yield return new KeyValuePair<string, string>("setup.exe", p.SetupCopy);
            yield return new KeyValuePair<string, string>("codex.toml", p.CodexConfig);
        }

        public static string Create(SetupPaths p, IEnumerable<string> versions, string reason, DateTime utcNow)
        {
            var id = utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var dir = Path.Combine(Root(p), id);
            var filesDir = Path.Combine(dir, "archivos");
            Directory.CreateDirectory(filesDir);
            var files = new Dictionary<string, object>();
            var absent = new List<object>();
            var units = new List<object>();
            foreach (var u in Units(p, versions.Distinct().OrderBy(x => x, StringComparer.Ordinal)))
            {
                units.Add(u.Key);
                if (File.Exists(u.Value))
                {
                    var dest = Path.Combine(filesDir, u.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(u.Value, dest);
                    files[u.Key] = SetupCore.FileSha(dest);
                }
                else if (Directory.Exists(u.Value))
                {
                    foreach (var f in Directory.GetFiles(u.Value, "*", SearchOption.AllDirectories))
                    {
                        var key = u.Key + "/" + f.Substring(u.Value.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                        var dest = Path.Combine(filesDir, key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest);
                        files[key] = SetupCore.FileSha(dest);
                    }
                }
                else absent.Add(u.Key);
            }
            var manifest = new Dictionary<string, object>
            {
                ["esquema"] = "bimbridge.respaldo/1", ["id"] = id, ["motivo"] = reason,
                ["creado_utc"] = utcNow.ToString("o", CultureInfo.InvariantCulture),
                ["revit"] = versions.Distinct().Cast<object>().ToList(), ["unidades"] = units, ["ausentes"] = absent, ["archivos"] = files,
            };
            File.WriteAllText(Path.Combine(dir, ManifestName), Json.Write(manifest));
            Verify(dir);
            return dir;
        }

        public static string Latest(SetupPaths p)
        {
            var root = Root(p);
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, ManifestName)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>Lanza InvalidDataException si falta o cambió cualquier archivo del respaldo.</summary>
        public static Dictionary<string, object> Verify(string dir)
        {
            var path = Path.Combine(dir, ManifestName);
            if (!File.Exists(path)) throw new InvalidDataException("respaldo sin manifiesto: " + dir);
            var m = Json.ParseObject(File.ReadAllText(path));
            if (Arg.GetString(m, "esquema") != "bimbridge.respaldo/1") throw new InvalidDataException("esquema de respaldo desconocido");
            if (!(m.TryGetValue("archivos", out var fo) && fo is Dictionary<string, object> files)) throw new InvalidDataException("respaldo sin lista de archivos");
            foreach (var kv in files)
            {
                if (kv.Key.Split('/').Any(x => x == ".." || x.Length == 0)) throw new InvalidDataException("ruta inválida en el respaldo: " + kv.Key);
                var f = Path.Combine(dir, "archivos", kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) throw new InvalidDataException("falta en el respaldo: " + kv.Key);
                if (!string.Equals(SetupCore.FileSha(f), kv.Value as string, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("hash distinto en el respaldo: " + kv.Key);
            }
            return m;
        }

        /// <summary>
        /// Vuelve al estado del respaldo. Devuelve la carpeta del respaldo previo al retorno.
        /// Si algo falla a medias intenta deshacer con ese respaldo y relanza indicando el resultado.
        /// </summary>
        public static string Restore(SetupPaths p, string dir, DateTime utcNow)
        {
            var m = Verify(dir);
            var versions = ((List<object>)m["revit"]).Select(x => (string)x).ToList();
            var before = Create(p, versions, "antes de restaurar " + Path.GetFileName(dir), utcNow);
            try { Apply(p, dir, m, versions); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                try { Apply(p, before, Verify(before), versions); }
                catch (Exception undo) when (undo is IOException || undo is UnauthorizedAccessException || undo is InvalidDataException)
                {
                    throw new IOException("El retorno falló (" + ex.Message + ") y tampoco se pudo deshacer (" + undo.Message + "). Respaldo intacto en " + before + ".", ex);
                }
                throw new IOException("El retorno falló y se dejó el estado anterior: " + ex.Message, ex);
            }
            return before;
        }

        private static void Apply(SetupPaths p, string dir, Dictionary<string, object> m, List<string> versions)
        {
            var files = (Dictionary<string, object>)m["archivos"];
            foreach (var u in Units(p, versions))
            {
                // config.toml es del usuario: sólo vuelve la sección de BIMBRIDGE; lo que cambió fuera de ella se conserva.
                if (u.Key == "codex.toml") { RestoreCodexSection(p, dir, files); continue; }
                if (Directory.Exists(u.Value)) Directory.Delete(u.Value, true);
                else if (File.Exists(u.Value)) { File.SetAttributes(u.Value, FileAttributes.Normal); File.Delete(u.Value); }
                foreach (var kv in files.Where(x => x.Key == u.Key || x.Key.StartsWith(u.Key + "/", StringComparison.Ordinal)))
                {
                    var target = kv.Key == u.Key ? u.Value : Path.Combine(u.Value, kv.Key.Substring(u.Key.Length + 1).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(dir, "archivos", kv.Key.Replace('/', Path.DirectorySeparatorChar)), target);
                    if (!string.Equals(SetupCore.FileSha(target), kv.Value as string, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("hash distinto tras restaurar: " + kv.Key);
                }
            }
        }

        private static void RestoreCodexSection(SetupPaths p, string dir, Dictionary<string, object> files)
        {
            var saved = files.ContainsKey("codex.toml") ? File.ReadAllText(Path.Combine(dir, "archivos", "codex.toml")) : "";
            var current = File.Exists(p.CodexConfig) ? File.ReadAllText(p.CodexConfig) : "";
            if (current == saved) return;
            var result = SetupCore.ReplaceTomlSection(current, saved, SetupCore.CodexSection);
            if (result == current) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p.CodexConfig));
            File.WriteAllText(p.CodexConfig, result);
        }

        /// <summary>Conserva los últimos respaldos. Nunca falla la instalación por limpiar.</summary>
        public static void Prune(SetupPaths p)
        {
            var root = Root(p);
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Skip(Keep))
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
