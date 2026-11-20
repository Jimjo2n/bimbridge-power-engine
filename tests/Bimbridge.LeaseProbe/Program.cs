// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Threading;
using Bimbridge.Engine.Tasks;

// Standalone process probe for lease tests across real processes.
// usage: probe <root> <id> <owner> <action> [args]
//   acquire <ttlMs> <startAtTicksUtc>   -> prints "WIN <fencing>" or "LOSE"
//   validate <fencing>                   -> "VALID" or "INVALID"
//   renew <fencing> <ttlMs>              -> "RENEWED" or "LOST"
internal static class Program
{
    // Clock injected only in the test probe. The store and the locks are real.
    private static DateTime Clock(string[] args, int index) => args.Length > index
        ? new DateTime(long.Parse(args[index], CultureInfo.InvariantCulture), DateTimeKind.Utc)
        : DateTime.UtcNow;

    private static int Main(string[] a)
    {
        // Probe only: separates exclusion (what is tested) from the maximum lock wait under extreme load.
        var lockMs = Environment.GetEnvironmentVariable("BIMBRIDGE_PROBE_LOCK_TIMEOUT_MS");
        if (lockMs != null) TaskStore.LeaseLockTimeout = TimeSpan.FromMilliseconds(double.Parse(lockMs, CultureInfo.InvariantCulture));
        var store = a[3] == "audit" || a[3] == "clave" ? null : new TaskStore(a[0]);
        var id = a[1]; var owner = a[2];
        try
        {
            switch (a[3])
            {
                case "acquire":
                    var ttl = TimeSpan.FromMilliseconds(double.Parse(a[4], CultureInfo.InvariantCulture));
                    var start = new DateTime(long.Parse(a[5], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    while (DateTime.UtcNow < start) Thread.SpinWait(100);
                    Console.WriteLine(store.TryLease(id, owner, "pid" + Environment.ProcessId, ttl, Clock(a, 6), out var h) ? "WIN " + h.Fencing : "LOSE");
                    return 0;
                case "audit":
                    // a[0]=audit file path; a[1]=n; a[4]=start
                    var startA = new DateTime(long.Parse(a[4], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    var log = new Bimbridge.Engine.AuditLog(a[0]);
                    while (DateTime.UtcNow < startA) Thread.SpinWait(100);
                    var n = int.Parse(a[1], CultureInfo.InvariantCulture);
                    try { for (var i = 0; i < n; i++) log.Append(owner, "prueba", "ok"); }
                    catch (Bimbridge.Engine.JournalUnavailableException)
                    {
                        // Probe diagnostics: counters before propagating the failure (the exit code is still non-zero).
                        Console.Error.WriteLine("PROBE_FAIL " + Bimbridge.Engine.AuditProfile.BatchReport() + " reply_errors=" + Bimbridge.Engine.AuditProfile.ReplyErrors + " sent=[" + Bimbridge.Engine.AuditProfile.RepliesSent + "]"
                            + " pend=[" + string.Join(",", System.IO.Directory.Exists(a[0] + ".pend") ? System.IO.Directory.GetFiles(a[0] + ".pend") : new string[0]) + "]");
                        throw;
                    }
                    Console.WriteLine("APPENDED " + n);
                    if (Environment.GetEnvironmentVariable("BIMBRIDGE_PROBE_PROFILE") == "1") Console.Error.WriteLine("PROFILE " + Bimbridge.Engine.AuditProfile.Report() + " " + Bimbridge.Engine.AuditProfile.BatchReport() + " " + Bimbridge.Engine.AuditProfile.PhaseReport());
                    return 0;
                case "clave":
                    // a[0]=grants folder; a[4]=start. Creates the store at the same time as other processes and approves its own grant.
                    var startK = new DateTime(long.Parse(a[4], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    while (DateTime.UtcNow < startK) Thread.SpinWait(100);
                    var gs = new Bimbridge.Engine.GrantStore(a[0]);
                    var g = gs.Approve("t" + Environment.ProcessId, "doc", "P", new[] { Bimbridge.Engine.Capability.Modificar }, TimeSpan.FromHours(1), "sonda", DateTime.UtcNow);
                    Console.WriteLine("GRANT " + g.Task);
                    return 0;
                case "validate":
                    Console.WriteLine(store.ValidateOwnership(id, owner, long.Parse(a[4]), Clock(a, 5)) ? "VALID" : "INVALID");
                    return 0;
                case "renew":
                    Console.WriteLine(store.RenewLease(id, owner, long.Parse(a[4]), TimeSpan.FromMilliseconds(double.Parse(a[5])), Clock(a, 6)) ? "RENEWED" : "LOST");
                    return 0;
            }
        }
        catch (TaskStore.LeaseStoreException ex) { Console.WriteLine("STOREERR " + ex.Message); return 3; }
        return 2;
    }
}
