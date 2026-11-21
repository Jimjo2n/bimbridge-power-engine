// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.AgentHost
{
    /// <summary>
    /// Run states of an operation. Only the runner changes them, and only through legal transitions (<see cref="IsLegal"/>);
    /// the journal rejects any other one on recovery.
    /// </summary>
    public static class OpState
    {
        public const string Validated = "validada";
        public const string Running = "en_ejecucion";
        public const string Executed = "ejecutada";
        public const string Verified = "verificada";
        public const string Failed = "fallida";
        /// <summary>There is or may be an effect, but not enough evidence to certify the result. Not repeated.</summary>
        public const string NeedsVerification = "requiere_verificacion";
        /// <summary>Partial or uncertain effect: a person decides. Not repeated.</summary>
        public const string NeedsReconciliation = "requiere_reconciliar";
        public const string Blocked = "bloqueada";
        public const string Cancelled = "cancelada";
        /// <summary>alpha3: TEMPORARY write, verified and reverted (trial mode against Revit). Final; never counts as applied.</summary>
        public const string Trialed = "ensayada";

        public static readonly string[] All = { Validated, Running, Executed, Verified, Failed, NeedsVerification, NeedsReconciliation, Blocked, Cancelled, Trialed };
        public static readonly string[] Final = { Verified, Failed, NeedsVerification, NeedsReconciliation, Blocked, Cancelled, Trialed };
        /// <summary>Final states that need a person or a reasoning agent.</summary>
        public static readonly string[] Exceptions = { Failed, NeedsVerification, NeedsReconciliation, Blocked };

        public static bool IsValid(string s) => Array.IndexOf(All, s) >= 0;
        public static bool IsFinal(string s) => Array.IndexOf(Final, s) >= 0;

        private static readonly Dictionary<string, string[]> Legal = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // validada → fallida: precondition or empty set before writing
            [Validated] = new[] { Running, Failed, Blocked, Cancelled },
            // en_ejecucion → validada: only if the effect was NOT observed (recovery) or the backend rejected without applying (retry)
            [Running] = new[] { Executed, Failed, Validated, NeedsVerification, NeedsReconciliation },
            [Executed] = new[] { Verified, Failed, NeedsVerification, Trialed, NeedsReconciliation },
        };

        /// <summary>
        /// beta1 §6: run-contract name for each existing state (no parallel enum). AUTHORIZED is not an operation state:
        /// it is "validada" with a current authorization for the plan (GrantStore); that is why it is requested separately.
        /// REJECTED = the operation never wrote (blocked or failed before running); ROLLED_BACK = reverted trial.
        /// </summary>
        public static string DirectiveName(string state, bool authorized = false, bool wroteNothing = false)
        {
            switch (state)
            {
                case Validated: return authorized ? "AUTHORIZED" : "PREPARED";
                case Running: return "EXECUTING";
                case Executed: return "COMMITTED";
                case Verified: return "VERIFIED";
                case Trialed: return "ROLLED_BACK";
                case Blocked: case Cancelled: return "REJECTED";
                case Failed: return wroteNothing ? "REJECTED" : "FAILED";
                case NeedsVerification: case NeedsReconciliation: return "OUTCOME_UNKNOWN";
                default: throw new ArgumentException("unknown state: " + state);
            }
        }

        public static bool IsLegal(string from, string to) => Legal.TryGetValue(from, out var t) && Array.IndexOf(t, to) >= 0;
    }

    /// <summary>Error classification (stable: agents use it to decide).</summary>
    public static class ErrorClass
    {
        public const string Precondition = "precondicion";
        public const string NoTargets = "sin_objetivos";
        public const string BackendRejection = "rechazo_backend";
        public const string Transient = "transitorio";
        public const string Verification = "verificacion";
        public const string PartialEffect = "efecto_parcial";
        public const string UncertainEffect = "efecto_incierto";
        public const string InsufficientEvidence = "evidencia_insuficiente";
        public const string Dependency = "dependencia";
        public const string Cancellation = "cancelacion";
        public const string DifferentDocument = "documento_distinto";
        public const string CorruptState = "estado_corrupto";
        public const string PlanInUse = "plan_en_uso";
        public const string Internal = "interno";
    }

    /// <summary>Mutable state of an operation (the only mutable part of the run).</summary>
    public sealed class OpRecord
    {
        public string State = OpState.Validated;
        public int Attempt;
        public Dictionary<string, object> Result;
        public Dictionary<string, object> Evidence;
        public string Error;      // ErrorClass
        public string Note;
        public long LastSeq;

        public OpRecord Clone() => new OpRecord
        {
            State = State, Attempt = Attempt, Result = Result == null ? null : new Dictionary<string, object>(Result), Evidence = Evidence == null ? null : new Dictionary<string, object>(Evidence),
            Error = Error, Note = Note, LastSeq = LastSeq,
        };

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["estado"] = State, ["intento"] = (long)Attempt, ["seq"] = LastSeq };
            if (Result != null) d["resultado"] = Result;
            if (Evidence != null) d["evidencia"] = Evidence;
            if (Error != null) d["error"] = Error;
            if (Note != null) d["nota"] = Note;
            return d;
        }
    }

    /// <summary>State of the plan as a whole (derived from the journal).</summary>
    public static class PlanRunState
    {
        public const string Active = "activo";
        public const string Paused = "pausado";
        public const string Cancelled = "cancelado";
    }

    /// <summary>A recovery diagnostic. Severity: info | aviso | error (error = cannot be recovered automatically).</summary>
    public sealed class Diagnostic
    {
        public string Severity, Code, Detail;
        public Diagnostic(string severity, string code, string detail) { Severity = severity; Code = code; Detail = detail; }
        public override string ToString() => Severity + ":" + Code + (Detail == null ? "" : " — " + Detail);
        public static Diagnostic Info(string c, string d = null) => new Diagnostic("info", c, d);
        public static Diagnostic Warn(string c, string d = null) => new Diagnostic("aviso", c, d);
        public static Diagnostic Error(string c, string d = null) => new Diagnostic("error", c, d);
        public static bool AnyError(IEnumerable<Diagnostic> ds) => ds.Any(d => d.Severity == "error");
    }
}
