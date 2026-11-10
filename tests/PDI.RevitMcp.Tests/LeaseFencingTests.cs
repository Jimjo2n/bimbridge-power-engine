// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PDI.RevitMcp.Engine.Tasks;
using Xunit;

namespace PDI.RevitMcp.Tests
{
    /// <summary>0.6.1 P0: lease entre procesos reales + fencing token monotónico.</summary>
    public class LeaseFencingTests
    {
        private static string NewRoot() { var d = Path.Combine(Path.GetTempPath(), "pdilease_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

        private static string ProbeDll()
        {
            var cfg = new DirectoryInfo(AppContext.BaseDirectory).Parent.Name; // net8.0 -> Debug|Release
            var baseDir = AppContext.BaseDirectory;
            var p = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "PDI.RevitMcp.LeaseProbe", "bin", cfg, "net8.0", "PDI.RevitMcp.LeaseProbe.dll"));
            Assert.True(File.Exists(p), "falta la sonda " + p);
            return p;
        }

        public static Process StartProbe(params string[] args) => Start(args);

        private static Process Start(params string[] args) => Start(null, args);

        private static Process Start(string lockTimeoutMs, string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
            if (lockTimeoutMs != null) psi.Environment["PDI_PROBE_LOCK_TIMEOUT_MS"] = lockTimeoutMs;
            psi.ArgumentList.Add(ProbeDll());
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }

        private static string Run(params string[] args)
        {
            using var p = Start(args);
            var o = p.StandardOutput.ReadToEnd().Trim();
            Assert.True(p.WaitForExit(60000));
            return o;
        }

        [Fact]
        public void Lease_TwentyRealProcesses_ExactlyOneWins()
        {
            var root = NewRoot();
            var startAt = DateTime.UtcNow.AddSeconds(4).Ticks.ToString();
            // Espera de bloqueo amplia: un STOREERR por CPU saturada no es lo que se mide aquí (ver el caso siguiente).
            var procs = Enumerable.Range(0, 20).Select(i => Start("180000", new[] { root, "t1", "dueno" + i, "acquire", "60000", startAt })).ToList();
            var outs = procs.Select(p => { var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(240000); p.Dispose(); return o; }).ToList();
            var seen = string.Join(" | ", outs.GroupBy(o => o).Select(g => g.Count() + "x " + g.Key));
            Assert.True(outs.Count(o => o.StartsWith("WIN")) == 1, seen);
            Assert.True(outs.Count(o => o == "LOSE") == 19, seen);
            Assert.Equal("WIN 1", outs.Single(o => o.StartsWith("WIN")));
        }

        [Fact]
        public void Lease_LockNotObtained_FailsClosed_NeverWins()
        {
            var root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "t"));
            using (new FileStream(Path.Combine(root, "t", "lease.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var p = Start("200", new[] { root, "t", "A", "acquire", "60000", "0" });
                var o = p.StandardOutput.ReadToEnd().Trim();
                Assert.True(p.WaitForExit(120000));
                Assert.StartsWith("STOREERR", o);
                Assert.Equal(3, p.ExitCode);
            }
            Assert.False(File.Exists(Path.Combine(root, "t", "lease.json")));
        }

        [Fact]
        public void Lease_ExpiryTakeover_FencingStrictlyIncreases_OldOwnerRejected()
        {
            var root = NewRoot();
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string At(int ms) => start.AddMilliseconds(ms).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("WIN 1", Run(root, "t", "A", "acquire", "300", "0", At(0)));
            Assert.Equal("LOSE", Run(root, "t", "B", "acquire", "60000", "0", At(299)));
            Assert.Equal("WIN 2", Run(root, "t", "B", "acquire", "60000", "0", At(300)));
            Assert.Equal("INVALID", Run(root, "t", "A", "validate", "1", At(301)));
            Assert.Equal("LOST", Run(root, "t", "A", "renew", "1", "60000", At(301)));
            Assert.Equal("VALID", Run(root, "t", "B", "validate", "2", At(301)));
        }

        [Fact]
        public void Lease_RenewalKeepsFencing_ReleaseKeepsCounterMonotonic()
        {
            var root = NewRoot(); var s = new TaskStore(root); var now = DateTime.UtcNow;
            Assert.True(s.TryLease("t", "A", "i", TimeSpan.FromMinutes(1), now, out var l1));
            Assert.True(s.RenewLease("t", "A", l1.Fencing, TimeSpan.FromMinutes(1), now.AddSeconds(10)));
            Assert.Equal(l1.Fencing, s.GetCurrentLease("t").Fencing);
            Assert.True(s.TryLease("t", "A", "i", TimeSpan.FromMinutes(1), now.AddSeconds(20), out var l2));
            Assert.Equal(l1.Fencing, l2.Fencing);
            s.ReleaseLease("t", "A");
            Assert.True(File.Exists(Path.Combine(root, "t", "lease.json")));
            Assert.False(s.ValidateOwnership("t", "A", l1.Fencing, now.AddSeconds(21)));
            Assert.True(s.TryLease("t", "A", "i", TimeSpan.FromMinutes(1), now.AddSeconds(22), out var l3));
            Assert.Equal(l1.Fencing + 1, l3.Fencing);
        }

        [Fact]
        public void Lease_LegacyFileWithoutFencing_ReadsAsZero_NextAcquireIsOne()
        {
            var root = NewRoot(); Directory.CreateDirectory(Path.Combine(root, "t"));
            File.WriteAllText(Path.Combine(root, "t", "lease.json"), "{\"dueno\":\"X\",\"instancia\":\"i\",\"vence\":\"2000-01-01T00:00:00Z\"}");
            var s = new TaskStore(root);
            Assert.Equal(0, s.GetCurrentLease("t").Fencing);
            Assert.True(s.TryLease("t", "Y", "i", TimeSpan.FromMinutes(1), DateTime.UtcNow, out var l));
            Assert.Equal(1, l.Fencing);
        }

        [Fact]
        public void Lease_CorruptFile_FailsSafe_NotTreatedAsFree()
        {
            var root = NewRoot(); Directory.CreateDirectory(Path.Combine(root, "t"));
            File.WriteAllText(Path.Combine(root, "t", "lease.json"), "{roto");
            var s = new TaskStore(root);
            Assert.Throws<TaskStore.LeaseStoreException>(() => s.TryLease("t", "Y", "i", TimeSpan.FromMinutes(1), DateTime.UtcNow, out _));
            Assert.False(s.ValidateOwnership("t", "Y", 1, DateTime.UtcNow));
        }

        [Fact]
        public void Lease_LockHeldByAnotherHandle_TimesOutExplicitly()
        {
            var root = NewRoot(); Directory.CreateDirectory(Path.Combine(root, "t"));
            var s = new TaskStore(root);
            var old = TaskStore.LeaseLockTimeout;
            TaskStore.LeaseLockTimeout = TimeSpan.FromMilliseconds(300);
            try
            {
                using (new FileStream(Path.Combine(root, "t", "lease.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (OperatingSystem.IsWindows())
                        Assert.Throws<TaskStore.LeaseStoreException>(() => s.TryLease("t", "Y", "i", TimeSpan.FromMinutes(1), DateTime.UtcNow, out _));
                }
            }
            finally { TaskStore.LeaseLockTimeout = old; }
        }
    }
}

namespace PDI.RevitMcp.Tests
{
    public class LeaseCostMeasurement
    {
        [Fact]
        public void Measure_ValidateOwnership_Cost()
        {
            var root = Path.Combine(Path.GetTempPath(), "pdileasecost_" + Guid.NewGuid().ToString("N"));
            var s = new PDI.RevitMcp.Engine.Tasks.TaskStore(root);
            Assert.True(s.TryLease("t", "A", "i", TimeSpan.FromHours(1), DateTime.UtcNow, out var l));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int n = 1000;
            for (var i = 0; i < n; i++) Assert.True(s.ValidateOwnership("t", "A", l.Fencing, DateTime.UtcNow));
            var v = sw.Elapsed.TotalMilliseconds / n;
            sw.Restart();
            for (var i = 0; i < n; i++) Assert.True(s.RenewLease("t", "A", l.Fencing, TimeSpan.FromHours(1), DateTime.UtcNow));
            var r = sw.Elapsed.TotalMilliseconds / n;
            var outp = Environment.GetEnvironmentVariable("PDI_MEASURE_OUT");
            if (!string.IsNullOrEmpty(outp)) File.WriteAllText(outp, FormattableString.Invariant($"validate_ms={v:F4}\nrenew_ms={r:F4}\n"));
            Assert.True(v < 50);
        }
    }
}
