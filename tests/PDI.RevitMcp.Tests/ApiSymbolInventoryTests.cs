// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>
    /// 0.9.1: el inventario completo de símbolos públicos (tools/inventory/RevitApiInventory symbols) es trazable y coherente:
    /// el SHA-256 declarado coincide con el listado, está ordenado y sin duplicados, y contiene símbolos conocidos con su firma.
    /// </summary>
    public class ApiSymbolInventoryTests
    {
        private static string Inv()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PDI.RevitMcp.sln"))) d = d.Parent;
            return Path.Combine(d.FullName, "docs", "0.9.1", "inventory");
        }

        private static byte[] Raw(int year)
        {
            using var fs = File.OpenRead(Path.Combine(Inv(), "revit-symbols-" + year + ".txt.gz"));
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return ms.ToArray();
        }

        [Theory]
        [InlineData(2022)] [InlineData(2023)] [InlineData(2024)] [InlineData(2025)] [InlineData(2026)]
        public void Listing_IsTraceable_Sorted_AndContainsKnownSignatures(int year)
        {
            var raw = Raw(year);
            using var sum = JsonDocument.Parse(File.ReadAllText(Path.Combine(Inv(), "revit-symbols-summary.json")));
            var v = sum.RootElement.GetProperty("versiones").EnumerateArray().Single(x => x.GetProperty("revit").GetInt32() == year);
            using (var sha = SHA256.Create())
                Assert.Equal(v.GetProperty("simbolos_sha256").GetString(), BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant());
            var lines = Encoding.UTF8.GetString(raw).Split('\n').Where(l => l.Length > 0).ToList();
            Assert.Equal(v.GetProperty("simbolos").GetInt32(), lines.Count);
            Assert.Equal(lines.OrderBy(l => l, StringComparer.Ordinal), lines);
            Assert.Equal(lines.Count, lines.Select(l => string.Join("|", l.Split('|').Take(3))).Distinct().Count());
            Assert.Contains(lines, l => l.StartsWith("RevitAPI|M|Autodesk.Revit.DB.Wall::Create(Autodesk.Revit.DB.Document,Autodesk.Revit.DB.Curve,Autodesk.Revit.DB.ElementId,Autodesk.Revit.DB.ElementId,Double,Double,Boolean,Boolean)|ret=Autodesk.Revit.DB.Wall|static", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("RevitAPIUI|F|Autodesk.Revit.UI.PostableCommand::", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("RevitAPI|T|Autodesk.Revit.DB.Floor|class", StringComparison.Ordinal));
            Assert.Equal(year >= 2024, lines.Any(l => l.StartsWith("RevitAPI|P|Autodesk.Revit.DB.ElementId::Value|", StringComparison.Ordinal)));
            Assert.DoesNotContain(lines, l => l.Contains("CopyMonitorOptions"));
        }
    }
}
