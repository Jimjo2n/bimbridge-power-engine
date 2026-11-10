// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using PDI.RevitMcp.Engine.Tasks;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Propiedad exclusiva de la ejecución de un plan (AH-04). No es un mecanismo nuevo: reutiliza el lease con fencing de
    /// <see cref="TaskStore"/> (0.6.x, el mismo que usan las tareas del add-in; bloqueo de archivo + fencing que sube en cada adquisición).
    /// El token de fencing se escribe en el diario (líneas "dueno" y cada transición) y se presenta al modelo en cada transacción: un
    /// proceso zombi cuyo lease venció y fue tomado por otro queda rechazado en los tres sitios.
    /// </summary>
    public sealed class PlanLease : IDisposable
    {
        public static TimeSpan DefaultTtl = TimeSpan.FromSeconds(ParseMs(Environment.GetEnvironmentVariable("PDI_AH_LEASE_MS"), 30000) / 1000.0);
        private const string LeaseId = "plan";
        private readonly TaskStore _store;
        private DateTime _lastRenew;
        public string Owner { get; }
        public long Fencing { get; }
        public TimeSpan Ttl { get; }
        public bool Released { get; private set; }

        private PlanLease(TaskStore store, string owner, long fencing, TimeSpan ttl)
        {
            _store = store; Owner = owner; Fencing = fencing; Ttl = ttl; _lastRenew = DateTime.UtcNow;
        }

        private static double ParseMs(string s, double d) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : d;

        public sealed class LeaseLostException : Exception { public LeaseLostException(string m) : base(m) { } }

        public static string NewOwner() => "agenthost:" + Environment.MachineName + ":" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ":" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Intenta tomar el plan. null + titular si otro proceso lo tiene vigente. LeaseStoreException si el almacén no es fiable.</summary>
        public static PlanLease TryAcquire(string planDir, TimeSpan? ttl, out TaskStore.Lease holder)
        {
            var store = new TaskStore(Path.Combine(planDir, "control"));
            var owner = NewOwner();
            var t = ttl ?? DefaultTtl;
            if (!store.TryLease(LeaseId, owner, owner, t, DateTime.UtcNow, out holder)) return null;
            return new PlanLease(store, owner, holder.Fencing, t);
        }

        /// <summary>Lee quién tiene el plan (sin tomarlo).</summary>
        public static TaskStore.Lease Peek(string planDir) => new TaskStore(Path.Combine(planDir, "control")).ReadLease(LeaseId);

        /// <summary>Punto de autoridad antes de cada acción con efecto: renueva si toca y comprueba dueño + fencing. Si no, lanza.</summary>
        public void Check()
        {
            if (Released) throw new LeaseLostException("lease liberado");
            var now = DateTime.UtcNow;
            if (now - _lastRenew >= TimeSpan.FromTicks(Ttl.Ticks / 3))
            {
                bool ok;
                try { ok = _store.RenewLease(LeaseId, Owner, Fencing, Ttl, now); }
                catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("almacén de lease no fiable: " + ex.Message); }
                if (!ok) throw new LeaseLostException("otro proceso tomó el plan o el lease venció (fencing " + Fencing + ")");
                _lastRenew = now;
                return;
            }
            if (!_store.ValidateOwnership(LeaseId, Owner, Fencing, now)) throw new LeaseLostException("otro proceso tomó el plan o el lease venció (fencing " + Fencing + ")");
        }

        public void Release()
        {
            if (Released) return;
            Released = true;
            _store.ReleaseLease(LeaseId, Owner);
        }

        public void Dispose() => Release();
    }
}
