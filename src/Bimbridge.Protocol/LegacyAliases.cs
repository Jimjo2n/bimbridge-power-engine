// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// LEGACY ALIAS TABLE (protocol layer). The only place in the protocol where pre-English (Spanish) names may appear.
    /// Readers accept both the English name and its legacy alias for one version; writers never emit the legacy name
    /// once the English one is adopted. Remove the aliases after that version.
    /// </summary>
    public static class LegacyAliases
    {
        /// <summary>Frame keys of Request/Response: English name → legacy name.</summary>
        public static readonly IReadOnlyDictionary<string, string> FrameKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attempt_id"] = "intento_id",
            ["status"] = "estado",
            ["data"] = "datos",
            ["warning"] = "aviso",
        };

        /// <summary>Value under <paramref name="key"/>, or under its legacy alias when the key is absent.</summary>
        public static bool TryGet(IDictionary<string, object> o, string key, out object value)
        {
            value = null;
            if (o == null) return false;
            if (o.TryGetValue(key, out value)) return true;
            return FrameKeys.TryGetValue(key, out var legacy) && o.TryGetValue(legacy, out value);
        }

        /// <summary>String value under <paramref name="key"/> or its legacy alias (null if neither is present).</summary>
        public static string GetString(IDictionary<string, object> o, string key) =>
            TryGet(o, key, out var v) && v != null ? v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
