// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Bimbridge.Engine.Tasks;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Exclusive ownership of a plan run (AH-04). Not a new mechanism: it reuses the fenced lease of
    /// <see cref="TaskStore"/> (0.6.x, the same one the add-in tasks use; file lock + fencing that increases on each acquisition).
    /// The fencing token is written to the journal ("dueno" lines and every transition) and presented to the model in every transaction: a
    /// zombie process whose lease expired and was taken by another is rejected in all three places.
    /// </summary>
    public sealed class PlanLease : IDisposable
    {
        public static TimeSpan DefaultTtl = TimeSpan.FromSeconds(ParseMs(Environment.GetEnvironmentVariable("BIMBRIDGE_AH_LEASE_MS"), 30000) / 1000.0);
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

        /// <summary>Tries to take the plan. null + holder if another process holds it. LeaseStoreException if the store is unreliable.</summary>
        public static PlanLease TryAcquire(string planDir, TimeSpan? ttl, out TaskStore.Lease holder)
        {
            var store = new TaskStore(Path.Combine(planDir, "control"));
            var owner = NewOwner();
            var t = ttl ?? DefaultTtl;
            if (!store.TryLease(LeaseId, owner, owner, t, DateTime.UtcNow, out holder)) return null;
            return new PlanLease(store, owner, holder.Fencing, t);
        }

        /// <summary>Reads who holds the plan (without taking it).</summary>
        public static TaskStore.Lease Peek(string planDir) => new TaskStore(Path.Combine(planDir, "control")).ReadLease(LeaseId);

        /// <summary>Authority point before every action with side effects: renews when due and checks owner + fencing. Otherwise throws.</summary>
        public void Check()
        {
            if (Released) throw new LeaseLostException("lease released");
            var now = DateTime.UtcNow;
            if (now - _lastRenew >= TimeSpan.FromTicks(Ttl.Ticks / 3))
            {
                bool ok;
                try { ok = _store.RenewLease(LeaseId, Owner, Fencing, Ttl, now); }
                catch (TaskStore.LeaseStoreException ex) { throw new LeaseLostException("lease store unreliable: " + ex.Message); }
                if (!ok) throw new LeaseLostException("another process took the plan or the lease expired (fencing " + Fencing + ")");
                _lastRenew = now;
                return;
            }
            if (!_store.ValidateOwnership(LeaseId, Owner, Fencing, now)) throw new LeaseLostException("another process took the plan or the lease expired (fencing " + Fencing + ")");
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
