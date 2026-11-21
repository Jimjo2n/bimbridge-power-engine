// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bimbridge.Engine.Tasks
{
    /// <summary>
    /// Recipes registered by FILE (§5): no MCP tool per recipe. They are looked up, in this order:
    /// 1. %LOCALAPPDATA%\Bimbridge\recetas\&lt;name&gt;.cs (the user's or the team's; these win);
    /// 2. the recetas\ folder next to the add-in (the ones shipped by the installer).
    /// Each is identified by name + declared version + file SHA-256. The task keeps an exact copy: if the file changes
    /// later, the task continues with its copy and a new task will have another fingerprint.
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
        private static readonly Regex DescRx = new Regex(@"Description\s*=>\s*""([^""]*)""", RegexOptions.Compiled);

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
