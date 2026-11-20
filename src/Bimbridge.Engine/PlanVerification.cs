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
        public const string Met = "cumplidas";
        public const string Differences = "con_diferencias";
        public const string NotVerified = "sin_verificar";

        public static string Postconditions(IDictionary<string, object> verification)
        {
            if (verification == null) return NotVerified;
            if (verification.ContainsKey("distintos")) return Differences;
            if (verification.TryGetValue("verificacion_creacion", out var vc) && vc is Dictionary<string, object> c)
            {
                var checkedN = Arg.GetLong(c, "revisados") ?? 0;
                var okN = Arg.GetLong(c, "como_se_pidio") ?? 0;
                if (okN != checkedN || c.ContainsKey("problemas")) return Differences;
            }
            return Met;
        }
    }
}
