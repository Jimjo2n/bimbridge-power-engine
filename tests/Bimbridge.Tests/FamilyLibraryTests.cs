// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bimbridge.Engine.Families;
using Bimbridge.Engine.Qa;
using Bimbridge.Protocol;
using Xunit;

namespace Bimbridge.Tests
{
    /// <summary>0.8.0 package D: authorized family library (without Revit).</summary>
    public class FamilyLibraryTests
    {
        [Fact]
        public void ReloadChecksNamedPortsEvenWhenTheirTotalHasNotChanged()
        {
            Dictionary<string, object> Port(string key, string system, string flow, double radius) =>
                new Dictionary<string, object> { ["clave"] = key, ["sistema"] = system, ["flujo"] = flow, ["radio_mm"] = radius };
            var source = new List<Dictionary<string, object>>
            {
                Port("fria", "DomesticColdWater", "entrada", 6.5),
                Port("caliente", "DomesticHotWater", "entrada", 6.5),
                Port("desague", "Sanitary", "salida", 20),
            };
            var project = (List<object>)Json.Parse(Json.Write(source.AsEnumerable().Reverse().ToList()));
            Assert.Empty(FamilyPortAudit.Compare(source, project));
            project[0] = Port("desague", "DomesticColdWater", "entrada", 25);
            Assert.Collection(FamilyPortAudit.Compare(source, project),
                row => Assert.Contains("sistema", row), row => Assert.Contains("flujo", row), row => Assert.Contains("radio", row));
            Assert.Contains("no placed instance", Assert.Single(FamilyPortAudit.Compare(source, null)));
        }

        private static string Rfa(string dir, string name, int? version, params string[] catalogTypes)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".rfa");
            var body = new byte[4096];
            new Random(name.GetHashCode()).NextBytes(body);
            using (var fs = File.Create(path))
            {
                fs.Write(body, 0, body.Length);
                if (version.HasValue) { var m = Encoding.Unicode.GetBytes("Format: " + version.Value + "\0Build: x"); fs.Write(m, 0, m.Length); }
                fs.Write(body, 0, 100);
            }
            if (catalogTypes.Length > 0) File.WriteAllLines(Path.ChangeExtension(path, ".txt"), new[] { ",Width##LENGTH##FEET" }.Concat(catalogTypes.Select(t => t + ",1")));
            return path;
        }

        [Fact]
        public void Index_Search_Compatibility_Loaded_AndCatalogTypes()
        {
            var root = AH.NewDir("famlib");
            Rfa(Path.Combine(root, "Lighting"), "LTG_DOWNLIGHT-SURFACE - ROUND", 2024, "G", "D");
            Rfa(Path.Combine(root, "Lighting"), "LTG_STRIP LIGHT", 2026);
            Rfa(Path.Combine(root, "Lighting"), "LTG_STRIP LIGHT.0003", 2024);                 // backup copy: not indexed
            Rfa(Path.Combine(root, "Electrical", "Panels"), "EL_PANELBOARD", null);
            var cache = Path.Combine(AH.NewDir("famcache"), "indice.json");
            var lib = new FamilyLibrary(new[] { root }, cache);
            var r = lib.Refresh();
            Assert.Equal(3, r.added);
            Assert.Equal(3, lib.Count);
            var hits = lib.Search("downlight round", 2024, new[] { "LTG_DOWNLIGHT-SURFACE - ROUND" });
            Assert.Equal("LTG_DOWNLIGHT-SURFACE - ROUND", hits[0].Entry.Name);
            Assert.True(hits[0].Loaded);
            Assert.Equal(2024, hits[0].Entry.RevitVersion);
            Assert.Contains("G", hits[0].Entry.CatalogTypes);
            var strip = lib.Search("strip", 2024)[0];
            Assert.False(strip.Compatible);                      // saved in 2026, target 2024
            Assert.Null(lib.Search("panel", 2024).Single().Entry.RevitVersion);   // not guessed

            // cache: a second instance reuses it without rereading
            var lib2 = new FamilyLibrary(new[] { root }, cache);
            Assert.Equal(3, lib2.Refresh().cached);
        }

        [Fact]
        public void Load_RejectsOutsideRoots_Traversal_Unindexed_AndChangedFiles()
        {
            var root = AH.NewDir("famlib2");
            var outside = Rfa(AH.NewDir("famout"), "OUTSIDE", 2024);
            var inside = Rfa(root, "INSIDE", 2024);
            var lib = new FamilyLibrary(new[] { root });
            lib.Refresh();
            Assert.Null(lib.ResolveForLoad(outside, out var e1)); Assert.Contains("authorized", e1);
            Assert.Null(lib.ResolveForLoad(Path.Combine(root, "..", Path.GetFileName(Path.GetDirectoryName(outside)), "OUTSIDE.rfa"), out var e2)); Assert.Contains("authorized", e2);
            var ok = lib.ResolveForLoad(inside, out var e3);
            Assert.Null(e3); Assert.Equal(64, ok.Sha256.Length);
            var late = Rfa(root, "LATE", 2024);
            Assert.Null(lib.ResolveForLoad(late, out var e4)); Assert.Contains("index", e4);
            File.AppendAllText(inside, "x");
            File.SetLastWriteTimeUtc(inside, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(lib.ResolveForLoad(inside, out var e5)); Assert.Contains("changed", e5);
        }

        [Fact]
        public void NoRoots_MeansNothingIndexed()
        {
            var lib = new FamilyLibrary(new string[0]);
            lib.Refresh();
            Assert.Equal(0, lib.Count);
            Assert.Null(lib.RootOf("C:\\x.rfa"));
        }
    }
}
