// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine.Workflow;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.8.0: el registro único es coherente (estados, 15 CAP, 14 WF, familias) y docs/MATRIZ_TRAZABILIDAD_0.8.0.md coincide con el código.</summary>
    public class TraceabilityTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("raíz del repo");
        }

        [Fact]
        public void Registry_IsCoherent_AndNeverClaimsNative()
        {
            Assert.Empty(Traceability.Audit());
            Assert.Equal(15, Traceability.Caps.Count);
            Assert.Equal(14, Traceability.Workflows.Count);
            Assert.DoesNotContain(Traceability.All, r => r.Estado == Traceability.Native);
        }

        [Fact]
        public void Traceability_DocMatchesCode()
        {
            var doc = Path.Combine(Root(), "docs", "0.8.0", "MATRIZ_TRAZABILIDAD_0.8.0.md");
            var text = Traceability.Markdown("0.8.0", "Candidato interno; aceptación nativa pendiente.");
            // Regenerar: PDI_GEN_MATRIZ=1 dotnet test --filter Traceability_DocMatchesCode
            if (Environment.GetEnvironmentVariable("PDI_GEN_MATRIZ") == "1") { Directory.CreateDirectory(Path.GetDirectoryName(doc)); File.WriteAllText(doc, text); }
            Assert.True(File.Exists(doc), "falta " + doc);
            Assert.Equal(text.Replace("\r\n", "\n"), File.ReadAllText(doc).Replace("\r\n", "\n"));
        }
    }
}
