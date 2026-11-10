// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Families
{
    /// <summary>
    /// 0.8.0 (Paquete D): descubrimiento de familias en carpetas AUTORIZADAS. Indexa archivos .rfa sin Revit (nombre, carpeta,
    /// tamaño, fecha, versión de Revit leída del archivo cuando es posible, tipos del catálogo .txt hermano), con caché invalidada por
    /// tamaño y fecha. Nunca explora fuera de las raíces configuradas (ni por "..", ni por enlaces), y la carga posterior exige que el
    /// archivo siga siendo el mismo que se indexó (SHA-256).
    /// </summary>
    public sealed class FamilyLibrary
    {
        public const int MaxFiles = 50000;
        private static readonly Regex Backup = new Regex(@"\.\d{4}\.rfa$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed class Entry
        {
            public string Path, Root, Relative, Name;
            public long Size;
            public long MtimeTicks;
            public int? RevitVersion;        // null = no se pudo leer del archivo
            public List<string> CatalogTypes = new List<string>();
            public string Sha256;            // se calcula al cargar, no al indexar

            public Dictionary<string, object> ToJson(bool full = false)
            {
                var d = new Dictionary<string, object> { ["nombre"] = Name, ["carpeta"] = System.IO.Path.GetDirectoryName(Relative) ?? "", ["revit"] = RevitVersion.HasValue ? (object)(long)RevitVersion.Value : "desconocida" };
                if (CatalogTypes.Count > 0) d["tipos_catalogo"] = CatalogTypes.Take(full ? 500 : 12).Cast<object>().ToList();
                if (full) { d["ruta"] = Path; d["bytes"] = Size; d["modificado"] = new DateTime(MtimeTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture); }
                return d;
            }
        }

        private readonly List<string> _roots;
        private readonly string _cachePath;
        private Dictionary<string, Entry> _byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Roots => _roots;
        public int Count => _byPath.Count;
        public IEnumerable<Entry> Entries => _byPath.Values;

        public FamilyLibrary(IEnumerable<string> roots, string cachePath = null)
        {
            _roots = (roots ?? new string[0]).Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => Normalize(r)).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cachePath = cachePath;
        }

        public static string Normalize(string p) => System.IO.Path.GetFullPath(p.Trim().Trim('"')).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        /// <summary>¿Está dentro de una raíz autorizada? (ruta canónica; ".." y rutas relativas no escapan).</summary>
        public string RootOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = Normalize(path); } catch (Exception) { return null; }
            foreach (var r in _roots)
                if (full.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>Indexa (o refresca) las raíces. Devuelve (nuevos, actualizados, quitados, reutilizados de caché).</summary>
        public (int added, int updated, int removed, int cached) Refresh()
        {
            var old = _byPath.Count > 0 ? _byPath : LoadCache();
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            int added = 0, updated = 0, cached = 0;
            foreach (var root in _roots)
            {
                foreach (var f in SafeEnumerate(root))
                {
                    if (next.Count >= MaxFiles) break;
                    if (Backup.IsMatch(f)) continue;
                    FileInfo fi;
                    try { fi = new FileInfo(f); if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch (Exception) { continue; }
                    if (old.TryGetValue(f, out var prev) && prev.Size == fi.Length && prev.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
                    { next[f] = prev; cached++; continue; }
                    var e = new Entry
                    {
                        Path = f, Root = root, Relative = f.Substring(root.Length + 1), Name = System.IO.Path.GetFileNameWithoutExtension(f),
                        Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks, RevitVersion = ReadRevitVersion(f), CatalogTypes = ReadTypeCatalog(f),
                    };
                    if (prev == null) added++; else updated++;
                    next[f] = e;
                }
            }
            var removed = old.Keys.Count(k => !next.ContainsKey(k));
            _byPath = next;
            SaveCache();
            return (added, updated, removed, cached);
        }

        private static IEnumerable<string> SafeEnumerate(string root)
        {
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files = new string[0], dirs = new string[0];
                try { files = Directory.GetFiles(dir, "*.rfa"); dirs = Directory.GetDirectories(dir); } catch (Exception) { }
                foreach (var f in files) yield return f;
                foreach (var d in dirs)
                {
                    try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch (Exception) { continue; }  // no se siguen enlaces
                    stack.Push(d);
                }
            }
        }

        /// <summary>
        /// Versión de Revit con la que se guardó el RFA. El flujo BasicFileInfo del archivo OLE guarda, en UTF-16, "Format: 2024"
        /// (o en versiones antiguas "Autodesk Revit 2019"). Se busca en los primeros 16 MB; si no aparece, se devuelve null (no se adivina).
        /// </summary>
        public static int? ReadRevitVersion(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var n = (int)Math.Min(fs.Length, 16L * 1024 * 1024);
                    var buf = new byte[n];
                    var read = 0; while (read < n) { var k = fs.Read(buf, read, n - read); if (k <= 0) break; read += k; }
                    return FindVersion(buf, read);
                }
            }
            catch (Exception) { return null; }
        }

        internal static int? FindVersion(byte[] buf, int len)
        {
            foreach (var marker in new[] { "Format: ", "Autodesk Revit " })
            {
                var m = Encoding.Unicode.GetBytes(marker);
                for (var i = 0; i + m.Length + 8 <= len; i++)
                {
                    var hit = true;
                    for (var j = 0; j < m.Length; j++) if (buf[i + j] != m[j]) { hit = false; break; }
                    if (!hit) continue;
                    var s = Encoding.Unicode.GetString(buf, i + m.Length, 8);
                    if (s.Length == 4 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 2000 && v <= 2100) return v;
                }
            }
            return null;
        }

        /// <summary>Catálogo de tipos de Revit: un .txt con el mismo nombre junto al .rfa; la primera columna de cada fila es el tipo.</summary>
        public static List<string> ReadTypeCatalog(string rfa)
        {
            var txt = System.IO.Path.ChangeExtension(rfa, ".txt");
            var res = new List<string>();
            try
            {
                if (!File.Exists(txt)) return res;
                var lines = File.ReadAllLines(txt);
                foreach (var l in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    var name = l.Split(',')[0].Trim().Trim('"');
                    if (name.Length > 0 && res.Count < 2000) res.Add(name);
                }
            }
            catch (Exception) { }
            return res;
        }

        public sealed class Hit
        {
            public Entry Entry;
            public double Score;
            public bool Compatible;
            public bool Loaded;
            public string Why;
        }

        /// <summary>
        /// Búsqueda por palabras (nombre pesa más que carpeta; los tipos del catálogo también cuentan). <paramref name="revitVersion"/>:
        /// la del Revit destino (un RFA de una versión posterior no se puede cargar). <paramref name="loadedFamilies"/>: ya cargadas.
        /// </summary>
        public List<Hit> Search(string query, int revitVersion, IEnumerable<string> loadedFamilies = null, string folderFilter = null, int top = 10)
        {
            var tokens = Tokens(query);
            var loaded = new HashSet<string>(loadedFamilies ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var hits = new List<Hit>();
            if (tokens.Count == 0) return hits;
            foreach (var e in _byPath.Values)
            {
                if (folderFilter != null && e.Relative.IndexOf(folderFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var name = Tokens(e.Name); var folder = Tokens(System.IO.Path.GetDirectoryName(e.Relative) ?? "");
                var types = e.CatalogTypes.SelectMany(Tokens).ToList();
                double score = 0; var matched = 0;
                foreach (var t in tokens)
                {
                    var s = name.Contains(t) ? 3 : name.Any(n => n.StartsWith(t, StringComparison.Ordinal)) ? 2 : folder.Contains(t) ? 1 : types.Contains(t) ? 1 : 0;
                    if (s > 0) matched++;
                    score += s;
                }
                if (matched == 0) continue;
                score = score * matched / tokens.Count;   // las que cubren todas las palabras primero
                var compatible = !e.RevitVersion.HasValue || e.RevitVersion.Value <= revitVersion;
                hits.Add(new Hit
                {
                    Entry = e, Score = score, Compatible = compatible, Loaded = loaded.Contains(e.Name),
                    Why = !compatible ? "guardada en Revit " + e.RevitVersion + ", posterior al destino " + revitVersion : null,
                });
            }
            return hits.OrderByDescending(h => h.Compatible).ThenByDescending(h => h.Score).ThenBy(h => h.Entry.Relative.Length).Take(Math.Max(1, Math.Min(top, 50))).ToList();
        }

        private static List<string> Tokens(string s) =>
            Regex.Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).Distinct().ToList();

        /// <summary>Antes de cargar: dentro de raíz, indexado, sin cambios desde el índice y con hash calculado.</summary>
        public Entry ResolveForLoad(string path, out string error)
        {
            error = null;
            if (RootOf(path) == null) { error = "la ruta no está dentro de una carpeta de familias autorizada"; return null; }
            var full = Normalize(path);
            if (!_byPath.TryGetValue(full, out var e)) { error = "la familia no está en el índice; refresque la biblioteca (fam buscar refrescar=true)"; return null; }
            var fi = new FileInfo(full);
            if (!fi.Exists) { error = "el archivo ya no existe"; return null; }
            if (fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.MtimeTicks) { error = "el archivo cambió desde que se indexó; refresque y vuelva a elegir"; return null; }
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var h = SHA256.Create()) e.Sha256 = string.Concat(h.ComputeHash(fs).Select(b => b.ToString("x2")));
            return e;
        }

        // ---------------- caché ----------------

        private Dictionary<string, Entry> LoadCache()
        {
            var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (_cachePath == null || !File.Exists(_cachePath)) return d;
            try
            {
                if (!(Json.Parse(File.ReadAllText(_cachePath)) is List<object> items)) return d;
                foreach (var o in items.OfType<Dictionary<string, object>>())
                {
                    var e = new Entry
                    {
                        Path = Arg.GetString(o, "p"), Root = Arg.GetString(o, "r"), Relative = Arg.GetString(o, "rel"), Name = Arg.GetString(o, "n"),
                        Size = Arg.GetLong(o, "s") ?? -1, MtimeTicks = Arg.GetLong(o, "m") ?? -1,
                        RevitVersion = Arg.GetLong(o, "v") is long v ? (int?)v : null,
                        CatalogTypes = Arg.GetStringList(o, "t").ToList(),
                    };
                    if (e.Path != null && RootOf(e.Path) != null) d[e.Path] = e;   // una caché vieja no amplía las raíces
                }
            }
            catch (Exception) { }
            return d;
        }

        private void SaveCache()
        {
            if (_cachePath == null) return;
            try
            {
                var items = _byPath.Values.Select(e => (object)new Dictionary<string, object>
                {
                    ["p"] = e.Path, ["r"] = e.Root, ["rel"] = e.Relative, ["n"] = e.Name, ["s"] = e.Size, ["m"] = e.MtimeTicks,
                    ["v"] = e.RevitVersion.HasValue ? (object)(long)e.RevitVersion.Value : null, ["t"] = e.CatalogTypes.Cast<object>().ToList(),
                }).ToList();
                Home.WriteAtomic(_cachePath, Json.Write(items));
            }
            catch (Exception) { }
        }
    }
}
