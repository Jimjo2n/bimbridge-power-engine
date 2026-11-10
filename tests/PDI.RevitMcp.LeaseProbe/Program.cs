// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Threading;
using PDI.RevitMcp.Engine.Tasks;

// Sonda de proceso independiente para pruebas de lease entre procesos reales.
// uso: probe <raiz> <id> <dueno> <accion> [args]
//   acquire <ttlMs> <startAtTicksUtc>   -> imprime "WIN <fencing>" o "LOSE"
//   validate <fencing>                   -> "VALID" o "INVALID"
//   renew <fencing> <ttlMs>              -> "RENEWED" o "LOST"
internal static class Program
{
    // Reloj inyectado sólo en la sonda de pruebas. El almacén y los locks son reales.
    private static DateTime Clock(string[] args, int index) => args.Length > index
        ? new DateTime(long.Parse(args[index], CultureInfo.InvariantCulture), DateTimeKind.Utc)
        : DateTime.UtcNow;

    private static int Main(string[] a)
    {
        // Sólo la sonda: separa la exclusión (lo que se prueba) de la espera máxima del bloqueo bajo carga extrema.
        var lockMs = Environment.GetEnvironmentVariable("PDI_PROBE_LOCK_TIMEOUT_MS");
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
                    // a[0]=ruta del archivo de auditoría; a[1]=n; a[4]=inicio
                    var startA = new DateTime(long.Parse(a[4], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    var log = new PDI.RevitMcp.Engine.AuditLog(a[0]);
                    while (DateTime.UtcNow < startA) Thread.SpinWait(100);
                    var n = int.Parse(a[1], CultureInfo.InvariantCulture);
                    try { for (var i = 0; i < n; i++) log.Append(owner, "prueba", "ok"); }
                    catch (PDI.RevitMcp.Engine.JournalUnavailableException)
                    {
                        // Diagnóstico de la sonda: contadores antes de propagar el fallo (el código de salida sigue siendo distinto de cero).
                        Console.Error.WriteLine("PROBE_FAIL " + PDI.RevitMcp.Engine.AuditProfile.BatchReport() + " reply_errors=" + PDI.RevitMcp.Engine.AuditProfile.ReplyErrors + " sent=[" + PDI.RevitMcp.Engine.AuditProfile.RepliesSent + "]"
                            + " pend=[" + string.Join(",", System.IO.Directory.Exists(a[0] + ".pend") ? System.IO.Directory.GetFiles(a[0] + ".pend") : new string[0]) + "]");
                        throw;
                    }
                    Console.WriteLine("APPENDED " + n);
                    if (Environment.GetEnvironmentVariable("PDI_PROBE_PROFILE") == "1") Console.Error.WriteLine("PROFILE " + PDI.RevitMcp.Engine.AuditProfile.Report() + " " + PDI.RevitMcp.Engine.AuditProfile.BatchReport() + " " + PDI.RevitMcp.Engine.AuditProfile.PhaseReport());
                    return 0;
                case "clave":
                    // a[0]=carpeta de concesiones; a[4]=inicio. Crea el almacén a la vez que otros procesos y aprueba una concesión propia.
                    var startK = new DateTime(long.Parse(a[4], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    while (DateTime.UtcNow < startK) Thread.SpinWait(100);
                    var gs = new PDI.RevitMcp.Engine.GrantStore(a[0]);
                    var g = gs.Approve("t" + Environment.ProcessId, "doc", "P", new[] { PDI.RevitMcp.Engine.Capability.Modificar }, TimeSpan.FromHours(1), "sonda", DateTime.UtcNow);
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
