// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// AUD-19: vínculo verificable entre un lote de operaciones y la fuente de la que sale (p. ej. un PDF), para que la idempotencia
    /// no dependa de que el agente se acuerde de propagar una clave.
    ///
    /// Cada operación del lote lleva un testigo de origen en su propio contenido (campo <see cref="OpField"/>). Como el agente tiene
    /// que copiar las operaciones para planificar, el testigo viaja con ellas. Al planificar, el motor deriva de (testigo + contenido
    /// canónico de las operaciones) la clave de idempotencia y la huella; al hacer commit las aplica él mismo, haya pasado el agente
    /// clave= o no. La autoridad sigue siendo el diario de operaciones existente (OperationJournal), por documento de destino:
    ///  - mismo lote, mismo destino → misma clave y misma huella aunque el plan_id sea nuevo → no se repite;
    ///  - mismo lote, otro destino → otra entrada del diario (la clave es por documento);
    ///  - fuente modificada (otro SHA-256), otra revisión u otro registro → otro testigo → otra clave;
    ///  - commit interrumpido o resultado incierto → el diario exige reconciliar, como cualquier escritura con clave.
    /// </summary>
    public static class SourceBinding
    {
        public const string OpField = "origen";
        public const string TokenPrefix = "src1:";

        /// <summary>Testigo de origen: huella de (tipo y id de la fuente, SHA-256 del archivo, revisión, registro geométrico, huella del plan, lote).</summary>
        public static string Token(string kind, string sourceId, string sourceSha256, string revision, string registrationFingerprint, string planFingerprint, int batch)
        {
            if (string.IsNullOrEmpty(sourceSha256)) throw new ArgumentException("la fuente no tiene SHA-256: no se puede vincular");
            if (string.IsNullOrEmpty(planFingerprint)) throw new ArgumentException("el plan no tiene huella: no se puede vincular");
            return TokenPrefix + Home.Sha256Hex(string.Join("\n", kind ?? "", sourceId ?? "", sourceSha256, revision ?? "", registrationFingerprint ?? "", planFingerprint,
                batch.ToString(System.Globalization.CultureInfo.InvariantCulture)), 40);
        }

        /// <summary>Huella de un registro geométrico (matriz página→modelo): cambia si cambia la transformación.</summary>
        public static string RegistrationFingerprint(double[] matrix) =>
            matrix == null ? "" : Home.Sha256Hex(string.Join(",", matrix.Select(v => Math.Round(v, 9).ToString("R", System.Globalization.CultureInfo.InvariantCulture))), 24);

        public sealed class Bound
        {
            public string Token, Key, Hash;
            public int Ops;
        }

        /// <summary>
        /// Vínculo de un lote de operaciones crudas, o null si ninguna lleva testigo. Todas deben llevar el MISMO testigo: mezclar
        /// operaciones vinculadas con otras (o dos fuentes) en un plan se rechaza, porque la clave dejaría de identificar el lote.
        /// </summary>
        public static Bound Resolve(IList<object> rawOps)
        {
            if (rawOps == null || rawOps.Count == 0) return null;
            var ops = rawOps.OfType<Dictionary<string, object>>().ToList();
            var tokens = ops.Select(o => Arg.GetString(o, OpField)).ToList();
            if (tokens.All(string.IsNullOrEmpty)) return null;
            if (ops.Count != rawOps.Count || tokens.Any(string.IsNullOrEmpty))
                throw new QueryException("plan: unas operaciones llevan " + OpField + "= (vienen de una fuente) y otras no; planifique el lote de la fuente tal cual, sin mezclarlo");
            var distinct = tokens.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count != 1) throw new QueryException("plan: las operaciones vienen de " + distinct.Count + " lotes de fuente distintos; planifique cada lote por separado");
            var token = distinct[0];
            if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length != TokenPrefix.Length + 40 || token.Skip(TokenPrefix.Length).Any(c => !Uri.IsHexDigit(c)))
                throw new QueryException("plan: " + OpField + "= no es un testigo de origen válido; use las operaciones tal como las devolvió el flujo de la fuente");
            // Contenido canónico: cualquier cambio en una operación (un punto, un tipo) cambia la huella y la clave.
            var canon = string.Join("\n", ops.Select(o => OpSemantics.Canonical(o)));
            var hash = Home.Sha256Hex(token + "\n" + canon, 32);
            return new Bound { Token = token, Hash = "src:" + hash, Key = "src-" + hash.Substring(0, 24), Ops = ops.Count };
        }

        /// <summary>
        /// Clave y huella con que el commit de un plan vinculado entra en el diario. La clave la pone el motor: si el agente no pasa
        /// ninguna se usa la vinculada; si pasa OTRA se rechaza (no puede «renombrar» el lote para repetirlo). La huella es la del
        /// contenido, no la del plan_id, así que un plan nuevo con el mismo lote es la misma operación.
        /// </summary>
        public static bool JournalIdentity(Bound bound, string givenKey, out string key, out string hash, out string error)
        {
            key = givenKey; hash = null; error = null;
            if (bound == null) return true;
            if (!string.IsNullOrWhiteSpace(givenKey) && !string.Equals(givenKey.Trim(), bound.Key, StringComparison.Ordinal))
            {
                error = "este plan viene de una fuente y su clave de idempotencia es " + bound.Key + "; no se admite otra clave= (" + givenKey + "). Omita clave= o use la vinculada.";
                return false;
            }
            key = bound.Key; hash = bound.Hash;
            return true;
        }
    }
}
