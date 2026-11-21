// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bimbridge.Engine.Workflows
{
    /// <summary>
    /// AUD-20: freezing and retrieval of the expectations of a Copy/Monitor attempt. No Revit: the add-in supplies what it read.
    ///  - <see cref="Freeze"/> (preflight): saves the host inventory + expectations exactly once. If the attempt already exists,
    ///    returns what was frozen and the differences from what is seen now; it never replaces it.
    ///  - <see cref="Load"/> (verify): returns what was frozen and the differences from the current state. If any alters the target
    ///    (<see cref="Result.Conflict"/>), do not verify against new expectations: the conflict is reported.
    /// </summary>
    public static class CopyMonitorFlow
    {
        public sealed class Result
        {
            public ReceiptStore.Baseline Baseline;
            public CopyMonitorExpectations Frozen;
            public bool Created;
            public List<string> Differences = new List<string>();
            public bool Conflict => Differences.Any(CopyMonitorExpectations.Blocking);
        }

        public static Result Freeze(ReceiptStore store, string attemptKey, CopyMonitorExpectations now, IEnumerable<long> hostIds)
        {
            var b = store.SaveBaselineOnce(attemptKey, now.HostIdentity, now.LinkIdentity, hostIds, out var created, now.ToText());
            var r = new Result { Baseline = b, Created = created };
            r.Frozen = Parse(b, attemptKey);
            if (!created) r.Differences = r.Frozen.Diff(now);
            return r;
        }

        /// <param name="mappingGiven">false if the verifier gave no mapping or exclusions: the frozen ones are used (not a change).</param>
        public static Result Load(ReceiptStore store, string attemptKey, CopyMonitorExpectations now, bool mappingGiven)
        {
            var b = store.LoadBaseline(attemptKey);
            if (b == null) return null;
            var r = new Result { Baseline = b, Frozen = Parse(b, attemptKey) };
            if (!mappingGiven) now.Mappings = r.Frozen.Mappings;
            r.Differences = r.Frozen.Diff(now);
            return r;
        }

        private static CopyMonitorExpectations Parse(ReceiptStore.Baseline b, string key)
        {
            if (string.IsNullOrEmpty(b.Expectations))
                throw new InvalidDataException("attempt " + key + " was started without frozen expectations (older version): start a new attempt with another clave=");
            return CopyMonitorExpectations.FromText(b.Expectations);
        }
    }
}
