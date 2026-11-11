// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// AUD-09: el estado del commit (la transacción se confirmó) y el de las postcondiciones (el modelo quedó como se pidió) son cosas
    /// distintas. Un commit confirmado con diferencias en la relectura NO es un éxito funcional pleno.
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
