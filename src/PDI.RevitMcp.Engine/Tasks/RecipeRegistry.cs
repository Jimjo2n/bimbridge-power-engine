// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PDI.RevitMcp.Engine.Tasks
{
    /// <summary>
    /// Recetas registradas por ARCHIVO (§5): sin una herramienta MCP por receta. Se buscan, en este orden:
    /// 1. %LOCALAPPDATA%\PDI.RevitMcp\recetas\&lt;nombre&gt;.cs (las del usuario o del equipo; ganan);
    /// 2. la carpeta recetas\ junto al add-in (las que trae el instalador).
    /// Cada una se identifica por nombre + versión declarada + SHA-256 del archivo. La tarea guarda una copia exacta: si el archivo cambia
    /// después, la tarea sigue con su copia y una tarea nueva tendrá otra huella.
    /// </summary>
    public sealed class RecipeRegistry
    {
        public sealed class Entry
        {
            public string Name;
            public string Path;
            public string Origin;
            public string Version;
            public string Description;
            public string Hash;
        }

        private readonly string[] _folders;

        public RecipeRegistry(params string[] folders) { _folders = folders.Where(f => !string.IsNullOrEmpty(f)).ToArray(); }

        public static string UserFolder => Home.Sub("recetas");

        private static readonly Regex VersionRx = new Regex(@"Version\s*=>\s*""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex DescRx = new Regex(@"Descripcion\s*=>\s*""([^""]*)""", RegexOptions.Compiled);

        public List<Entry> List()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Entry>();
            foreach (var f in _folders)
            {
                if (!Directory.Exists(f)) continue;
                foreach (var p in Directory.GetFiles(f, "*.cs").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(p);
                    if (!seen.Add(name)) continue;
                    list.Add(Describe(name, p, f));
                }
            }
            return list;
        }

        public Entry Find(string name)
        {
            var safe = ScriptSource.SafeName(name);
            foreach (var f in _folders)
            {
                var p = System.IO.Path.Combine(f, safe + ".cs");
                if (File.Exists(p)) return Describe(safe, p, f);
            }
            return null;
        }

        private Entry Describe(string name, string path, string folder)
        {
            var code = File.ReadAllText(path);
            return new Entry
            {
                Name = name, Path = path, Origin = folder == UserFolder ? "usuario" : "instalada",
                Version = VersionRx.Match(code) is var m && m.Success ? m.Groups[1].Value : "1",
                Description = DescRx.Match(code) is var d && d.Success ? d.Groups[1].Value : "",
                Hash = Home.Sha256Hex(code),
            };
        }
    }
}
