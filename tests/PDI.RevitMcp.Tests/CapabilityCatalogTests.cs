// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PDI.RevitMcp.Engine.Coverage;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1 Bloque B: el catálogo de capacidades no puede afirmar más de lo que el código y las pruebas demuestran.
    /// Usa el inventario offline de la API (docs/0.9.1/inventory/revit-api-*.json, generado por tools/inventory/RevitApiInventory).
    /// </summary>
    public class CapabilityCatalogTests
    {
        private static readonly int[] Years = { 2022, 2023, 2024, 2025, 2026 };

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PDI.RevitMcp.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static JsonDocument Inventory(int year) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "0.9.1", "inventory", "revit-api-" + year + ".json")));

        private static HashSet<string> Commands(int year)
        {
            using var j = Inventory(year);
            return new HashSet<string>(j.RootElement.GetProperty("postable_command").EnumerateArray().Select(e => e.GetString()), StringComparer.Ordinal);
        }

        private static Dictionary<string, bool> Surfaces(int year)
        {
            using var j = Inventory(year);
            return j.RootElement.GetProperty("superficies").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean(), StringComparer.Ordinal);
        }

        [Fact]
        public void Ids_AreUnique_AndEveryDomainF01ToF37_HasAtLeastOneEntry()
        {
            var all = CapabilityCatalog.All;
            Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.All(all, c => Assert.Matches("^F(0[1-9]|[12][0-9]|3[0-7])-[A-Z0-9-]+-\\d{3}$", c.Id));
            Assert.All(all, c => Assert.StartsWith(c.Domain + "-", c.Id));
            for (var f = 1; f <= 37; f++) Assert.Contains(all, c => c.Domain == "F" + f.ToString("00"));
        }

        [Fact]
        public void NothingIsNativeVerified_WithoutANativeCampaign()
        {
            // Todavía no hay campaña nativa de 0.9.1: ninguna capacidad puede declararse NATIVE_VERIFIED.
            Assert.DoesNotContain(CapabilityCatalog.All, c => c.Status == CoverageStatus.NATIVE_VERIFIED);
        }

        [Fact]
        public void ImplementedClaims_PointToFilesThatExist_AndOfflineVerified_PointToTestsThatExist()
        {
            var root = Root();
            var asm = typeof(CapabilityCatalogTests).Assembly;
            foreach (var c in CapabilityCatalog.All)
            {
                var implemented = c.Status == CoverageStatus.IMPLEMENTED_UNVERIFIED || c.Status == CoverageStatus.OFFLINE_VERIFIED || c.Status == CoverageStatus.PARTIAL
                                  || c.Status == CoverageStatus.DYNAMIC_CSHARP_ONLY;
                if (implemented)
                {
                    Assert.True(c.Implementation.Length > 0, c.Id + ": sin archivo de implementación");
                    foreach (var f in c.Implementation) Assert.True(File.Exists(Path.Combine(root, f)), c.Id + ": no existe " + f);
                }
                if (c.Status == CoverageStatus.OFFLINE_VERIFIED) Assert.True(c.Tests.Length > 0, c.Id + ": OFFLINE_VERIFIED sin pruebas");
                foreach (var t in c.Tests)
                {
                    var cls = t.Split('.')[0];
                    var type = asm.GetType("PDI.RevitMcp.Tests." + cls);
                    Assert.True(type != null, c.Id + ": no existe la prueba " + t);
                    if (t.Contains('.')) Assert.True(type.GetMethod(t.Split('.')[1]) != null, c.Id + ": no existe el método " + t);
                }
            }
        }

        [Fact]
        public void ApiClaims_MatchTheOfflineInventory()
        {
            var surfaces = Years.ToDictionary(y => y, Surfaces);
            foreach (var c in CapabilityCatalog.All)
            {
                foreach (var s in c.ApiSurfaces)
                    Assert.True(surfaces[2026].ContainsKey(s), c.Id + ": la superficie " + s + " no está en el inventario");
                if (c.Status == CoverageStatus.DYNAMIC_CSHARP_ONLY && c.ApiSurfaces.Length > 0)
                    Assert.True(c.ApiSurfaces.Any(s => Years.Any(y => surfaces[y][s])), c.Id + ": DYNAMIC_CSHARP_ONLY sin API pública en ninguna versión");
                // "No hay API pública" se demuestra: la superficie citada no existe en NINGUNA versión 2022–2026.
                if (c.Status == CoverageStatus.UNSUPPORTED_BY_PUBLIC_API)
                    Assert.All(c.ApiSurfaces, s => Assert.True(Years.All(y => !surfaces[y][s]), c.Id + ": " + s + " sí existe en alguna versión"));
            }
        }

        [Fact]
        public void PostableCommandsCited_ExistInAtLeastOneVersion_NoInventedNames()
        {
            var union = new HashSet<string>(Years.SelectMany(Commands), StringComparer.Ordinal);
            var invented = CapabilityCatalog.All.SelectMany(c => c.PostableCommands.Select(p => c.Id + ":" + p)).Where(x => !union.Contains(x.Split(':')[1])).ToList();
            Assert.True(invented.Count == 0, "comandos que no existen en PostableCommand: " + string.Join(", ", invented));
        }

        [Fact]
        public void EveryPostableCommand_OfEveryVersion_IsClassifiedIntoADomain()
        {
            foreach (var y in Years)
            {
                var cmds = Commands(y);
                Assert.True(cmds.Count > 500, y + ": inventario incompleto (" + cmds.Count + ")");
                foreach (var cmd in cmds)
                {
                    var d = CommandClassifier.Classify(cmd, out _);
                    Assert.Matches("^F(0[1-9]|[12][0-9]|3[0-7])$", d);
                }
            }
        }

        [Fact]
        public void TypedStrategies_ReferenceOperationsThatExistInTheOpCatalog()
        {
            // Una capacidad "typed:<op>" sólo puede citar una operación de escritura que exista de verdad (OpCatalog).
            var kinds = new HashSet<string>(PDI.RevitMcp.Engine.OpCatalog.All.Select(o => o.Kind), StringComparer.Ordinal);
            foreach (var c in CapabilityCatalog.All.Where(c => c.Strategy != null && c.Strategy.StartsWith("typed:", StringComparison.Ordinal)))
                Assert.True(kinds.Contains(c.Strategy.Substring(6)), c.Id + ": la operación " + c.Strategy + " no existe en OpCatalog");
        }

        [Fact]
        public void Summary_CountsMatchTheCatalog()
        {
            var s = CapabilityCatalog.Summary();
            Assert.Equal((long)CapabilityCatalog.All.Count, s["capacidades"]);
            var byStatus = (Dictionary<string, object>)s["por_estado"];
            Assert.Equal((long)CapabilityCatalog.All.Count, byStatus.Values.Sum(v => (long)v));
        }
    }
}
