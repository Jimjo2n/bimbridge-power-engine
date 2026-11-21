// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// 0.9.0: two operating profiles with different policies, enforced by tests (not a cosmetic flag).
    ///
    /// PRODUCTION (default): real documents. Dynamic code only if its SHA-256 is authorized by the user for that
    /// document (or for all) in config.json; EXPERIMENTAL operations rejected; updates only when signed by a trusted
    /// key.
    ///
    /// LAB: research with synthetic, disposable or authorized resources. Allows dynamic code without hash authorization
    /// and experimental operations, but ONLY on documents whose local path is inside the configured lab folders
    /// (never cloud, central or outside paths); an unsigned update package only if the user explicitly enabled it
    /// for the lab. The profile lifts no other protection (permissions, levels, audit, journal).
    ///
    /// Native execution in Revit stays forbidden by a restriction independent of the profile (NativeGate).
    /// </summary>
    public sealed class ProfilePolicy
    {
        public const string Production = "production", Lab = "lab";

        public string Name = Production;
        /// <summary>Local lab folders (LAB profile). Empty = no document is a lab document.</summary>
        public List<string> LabFolders = new List<string>();
        /// <summary>LAB: the user agreed to install unsigned update packages on this lab machine.</summary>
        public bool LabUnsignedUpdates;
        /// <summary>PRODUCTION: authorized dynamic code: "sha256" (any document) or "sha256@document_key".</summary>
        public List<string> AuthorizedCode = new List<string>();

        public bool IsLab => Name == Lab;

        public static ProfilePolicy Parse(string name, IEnumerable<string> labFolders, bool labUnsigned, IEnumerable<string> authorizedCode)
        {
            var n = (name ?? Production).Trim().ToLowerInvariant();
            if (n == "lab" || n == "experimental") n = Lab;
            if (n != Lab) n = Production;   // an unknown value NEVER opens the lab
            return new ProfilePolicy
            {
                Name = n,
                LabFolders = (labFolders ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(NormFolder).ToList(),
                LabUnsignedUpdates = n == Lab && labUnsigned,
                AuthorizedCode = (authorizedCode ?? Enumerable.Empty<string>()).Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
            };
        }

        private static string NormFolder(string f)
        {
            var p = f.Trim().Replace('/', '\\').TrimEnd('\\');
            return p.ToLowerInvariant() + "\\";
        }

        /// <summary>
        /// Local path of a document key ("archivo:c:\x\y.rvt"); null if cloud, central or unknown.
        /// Keys that are not local files never count as lab.
        /// </summary>
        public static string LocalPathOf(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return null;
            const string pfx = "archivo:";
            if (!docKey.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) return null;
            var p = docKey.Substring(pfx.Length).Trim().Replace('/', '\\');
            if (p.StartsWith("\\\\", StringComparison.Ordinal)) return null;   // UNC: network folder, not a local lab
            if (p.Contains("..")) return null;                                   // no path shortcuts
            return p.ToLowerInvariant();
        }

        public bool IsLabDocument(string docKey)
        {
            var p = LocalPathOf(docKey);
            return p != null && LabFolders.Any(f => p.StartsWith(f, StringComparison.Ordinal));
        }

        /// <summary>
        /// Profile policy for an already classified request. null = allowed by the profile (permissions and levels are checked
        /// separately). <paramref name="capabilityState"/>: capability registry state for the operation (EXPERIMENTAL, AVAILABLE…).
        /// </summary>
        public string DenyRequest(string op, string permission, string docKey, string capabilityState)
        {
            var experimental = string.Equals(capabilityState, CapabilityStates.Experimental, StringComparison.Ordinal);
            if (!IsLab)
            {
                if (experimental) return "perfil_produccion: operation " + op + " is EXPERIMENTAL and only exists in the lab profile";
                return null;
            }
            // LAB: nothing outside the lab, not even document reads (context listings touch no model).
            if (op == Wire.OpContext || op == Wire.OpDocs || op == Wire.OpCatalog) return null;
            if (permission == Permissions.Ui) return null;
            if (string.IsNullOrEmpty(docKey)) return null;   // no document (catalog, help): nothing to protect
            if (!IsLabDocument(docKey))
                return "perfil_laboratorio: the document is not in a lab folder (" + (LabFolders.Count == 0 ? "none configured" : string.Join("; ", LabFolders)) +
                       "). The lab only works with disposable or authorized local copies; never cloud, central or working models.";
            return null;
        }

        /// <summary>Dynamic code: in production it requires SHA-256 authorization (and document scope). null = allowed.</summary>
        public string DenyDynamicCode(string codeSha256, string docKey)
        {
            if (IsLab) return IsLabDocument(docKey) || string.IsNullOrEmpty(docKey) ? null : "perfil_laboratorio: dynamic code outside a lab folder";
            var sha = (codeSha256 ?? "").ToLowerInvariant();
            if (sha.Length == 0) return "codigo_no_autorizado: the code SHA-256 is missing";
            var key = (docKey ?? "").ToLowerInvariant();
            foreach (var a in AuthorizedCode)
            {
                var at = a.IndexOf('@');
                if (at < 0 ? a == sha : a.Substring(0, at) == sha && a.Substring(at + 1) == key) return null;
            }
            return "codigo_no_autorizado: in the production profile dynamic code only runs if the user authorized its SHA-256 (" + sha.Substring(0, Math.Min(16, sha.Length)) +
                   "…) for this document in config.json (authorized_code). Use a typed operation or ask the user for the authorization; do not try another way.";
        }

        /// <summary>Unsigned update packages: lab only, and only if the user enabled it. null = accepted (with confirmation).</summary>
        public string DenyUnsignedUpdate() =>
            IsLab && LabUnsignedUpdates ? null
            : IsLab ? "lab_profile: unsigned packages are disabled (lab.unsigned_updates=false)"
            : "perfil_produccion: only packages signed by a trusted key are installed (cryptographic provenance required)";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["profile"] = Name, ["lab_folders"] = LabFolders.Cast<object>().ToList(), ["unsigned_update"] = LabUnsignedUpdates,
            ["authorized_code"] = (long)AuthorizedCode.Count,
        };
    }

    /// <summary>0.9.0: capability registry states (on-demand discovery). Not to be confused with the test matrix states.</summary>
    public static class CapabilityStates
    {
        public const string Available = "AVAILABLE", Experimental = "EXPERIMENTAL", RequiresAuthorization = "REQUIRES_AUTHORIZATION",
            NativeTestPending = "NATIVE_TEST_PENDING", Deferred = "DEFERRED", ApiLimitation = "API_LIMITATION";
        public static readonly string[] All = { Available, Experimental, RequiresAuthorization, NativeTestPending, Deferred, ApiLimitation };
    }
}
