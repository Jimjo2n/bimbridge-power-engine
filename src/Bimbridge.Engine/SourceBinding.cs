// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// AUD-19: verifiable link between a batch of operations and the source it comes from (e.g. a PDF), so that idempotency
    /// does not depend on the agent remembering to propagate a key.
    ///
    /// Each operation in the batch carries an origin token in its own content (field <see cref="OpField"/>). Since the agent has
    /// to copy the operations to plan, the token travels with them. When planning, the engine derives the idempotency key and the
    /// fingerprint from (token + canonical content of the operations); on commit it applies them itself, whether or not the agent passed
    /// clave=. The authority is still the existing operation journal (OperationJournal), per target document:
    ///  - same batch, same target → same key and same fingerprint even if the plan_id is new → not repeated;
    ///  - same batch, other target → another journal entry (the key is per document);
    ///  - modified source (different SHA-256), another revision or another registration → another token → another key;
    ///  - interrupted commit or uncertain result → the journal requires reconciliation, like any keyed write.
    /// </summary>
    public static class SourceBinding
    {
        public const string OpField = "origen";
        public const string TokenPrefix = "src1:";

        /// <summary>Origin token: fingerprint of (source type and id, file SHA-256, revision, geometric registration, plan fingerprint, batch).</summary>
        public static string Token(string kind, string sourceId, string sourceSha256, string revision, string registrationFingerprint, string planFingerprint, int batch)
        {
            if (string.IsNullOrEmpty(sourceSha256)) throw new ArgumentException("the source has no SHA-256: cannot bind");
            if (string.IsNullOrEmpty(planFingerprint)) throw new ArgumentException("the plan has no fingerprint: cannot bind");
            return TokenPrefix + Home.Sha256Hex(string.Join("\n", kind ?? "", sourceId ?? "", sourceSha256, revision ?? "", registrationFingerprint ?? "", planFingerprint,
                batch.ToString(System.Globalization.CultureInfo.InvariantCulture)), 40);
        }

        /// <summary>Fingerprint of a geometric registration (page→model matrix): changes if the transformation changes.</summary>
        public static string RegistrationFingerprint(double[] matrix) =>
            matrix == null ? "" : Home.Sha256Hex(string.Join(",", matrix.Select(v => Math.Round(v, 9).ToString("R", System.Globalization.CultureInfo.InvariantCulture))), 24);

        public sealed class Bound
        {
            public string Token, Key, Hash;
            public int Ops;
        }

        /// <summary>
        /// Binding of a batch of raw operations, or null if none carries a token. All must carry the SAME token: mixing
        /// bound operations with others (or two sources) in one plan is rejected, because the key would no longer identify the batch.
        /// </summary>
        public static Bound Resolve(IList<object> rawOps)
        {
            if (rawOps == null || rawOps.Count == 0) return null;
            var ops = rawOps.OfType<Dictionary<string, object>>().ToList();
            var tokens = ops.Select(o => Arg.GetString(o, OpField)).ToList();
            if (tokens.All(string.IsNullOrEmpty)) return null;
            if (ops.Count != rawOps.Count || tokens.Any(string.IsNullOrEmpty))
                throw new QueryException("plan: some operations carry " + OpField + "= (they come from a source) and others do not; plan the source batch as is, without mixing it");
            var distinct = tokens.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count != 1) throw new QueryException("plan: the operations come from " + distinct.Count + " different source batches; plan each batch separately");
            var token = distinct[0];
            if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length != TokenPrefix.Length + 40 || token.Skip(TokenPrefix.Length).Any(c => !Uri.IsHexDigit(c)))
                throw new QueryException("plan: " + OpField + "= is not a valid origin token; use the operations exactly as the source workflow returned them");
            // Canonical content: any change in an operation (a point, a type) changes the fingerprint and the key.
            var canon = string.Join("\n", ops.Select(o => OpSemantics.Canonical(o)));
            var hash = Home.Sha256Hex(token + "\n" + canon, 32);
            return new Bound { Token = token, Hash = "src:" + hash, Key = "src-" + hash.Substring(0, 24), Ops = ops.Count };
        }

        /// <summary>
        /// Key and fingerprint with which the commit of a bound plan enters the journal. The engine sets the key: if the agent passes
        /// none, the bound one is used; if it passes ANOTHER, it is rejected (it cannot "rename" the batch to repeat it). The fingerprint is that of the
        /// content, not the plan_id, so a new plan with the same batch is the same operation.
        /// </summary>
        public static bool JournalIdentity(Bound bound, string givenKey, out string key, out string hash, out string error)
        {
            key = givenKey; hash = null; error = null;
            if (bound == null) return true;
            if (!string.IsNullOrWhiteSpace(givenKey) && !string.Equals(givenKey.Trim(), bound.Key, StringComparison.Ordinal))
            {
                error = "this plan comes from a source and its idempotency key is " + bound.Key + "; no other clave= is accepted (" + givenKey + "). Omit clave= or use the bound one.";
                return false;
            }
            key = bound.Key; hash = bound.Hash;
            return true;
        }
    }
}
