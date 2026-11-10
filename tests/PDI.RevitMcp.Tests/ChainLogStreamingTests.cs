// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using PDI.RevitMcp.AgentHost;
using PDI.RevitMcp.Protocol;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>alpha3 P1-C: ChainLog.Open valida en streaming con memoria acotada, sin perder detección de corrupción.</summary>
    public class ChainLogStreamingTests
    {
        private static string NewPath() { var d = Path.Combine(Path.GetTempPath(), "pdi-chain-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return Path.Combine(d, "diario.jsonl"); }

        [Fact]
        public void LargeJournal_StreamsWithBoundedMemory()
        {
            var path = NewPath();
            var w = ChainLog.Open(path, true, _ => true, new List<Diagnostic>(), "t");
            var pad = new string('x', 900);
            const int N = 40000;   // ~40 MB de diario
            for (var i = 0; i < N; i++) w.Append(new Dictionary<string, object> { ["ev"] = "x", ["i"] = (long)i, ["pad"] = pad });
            Assert.True(new FileInfo(path).Length > 35_000_000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            long peak = 0, seen = 0;
            var diags = new List<Diagnostic>();
            var log = ChainLog.Open(path, false, r => { seen++; if ((seen & 4095) == 0) peak = Math.Max(peak, GC.GetTotalMemory(false)); return true; }, diags, "t");
            Assert.False(Diagnostic.AnyError(diags));
            Assert.Equal(N, seen);
            Assert.Equal(N, log.LastSeq);
            // el pico de memoria viva no crece con el tamaño del archivo (antes: byte[] del archivo entero + lista de registros)
            Assert.True(peak < 200_000_000, "pico " + peak);
        }

        [Fact]
        public void Corruption_InTheMiddle_IsStillAnError_AndTornTailIsTrimmed()
        {
            var path = NewPath();
            var w = ChainLog.Open(path, true, _ => true, new List<Diagnostic>(), "t");
            for (var i = 0; i < 5; i++) w.Append(new Dictionary<string, object> { ["i"] = (long)i });
            File.AppendAllText(path, "{\"i\":9");        // cola cortada
            var d1 = new List<Diagnostic>();
            var l1 = ChainLog.Open(path, true, _ => true, d1, "t");
            Assert.False(Diagnostic.AnyError(d1));
            Assert.Contains(d1, d => d.Code == "t_linea_final_truncada");
            Assert.Equal(5, l1.LastSeq);
            Assert.EndsWith("\n", File.ReadAllText(path));

            var lines = File.ReadAllLines(path);
            lines[2] = lines[2].Replace("\"i\":2", "\"i\":7");   // contenido alterado
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            var d2 = new List<Diagnostic>(); var got = 0;
            ChainLog.Open(path, true, _ => { got++; return true; }, d2, "t");
            Assert.Contains(d2, d => d.Code == "t_hash_invalido");
            Assert.Equal(2, got);
        }
    }
}
