// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// AUD-09: the commit status (the transaction was committed) and the postcondition status (the model ended up as requested) are
    /// different things. A committed commit with differences on re-read is NOT a full functional success.
    /// </summary>
    public static class PlanVerification
    {
        public const string Met = "met";
        public const string Differences = "with_differences";
        public const string NotVerified = "unverified";

        public static string Postconditions(IDictionary<string, object> verification)
        {
            if (verification == null) return NotVerified;
            if (verification.ContainsKey("different_items")) return Differences;
            if (verification.TryGetValue("creation_verification", out var vc) && vc is Dictionary<string, object> c)
            {
                var checkedN = Arg.GetLong(c, "reviewed") ?? 0;
                var okN = Arg.GetLong(c, "as_requested") ?? 0;
                if (okN != checkedN || c.ContainsKey("problems")) return Differences;
            }
            return Met;
        }
    }
}
