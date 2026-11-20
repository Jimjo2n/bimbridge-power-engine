// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using Bimbridge.Protocol;

namespace Bimbridge.AgentHost
{
    /// <summary>Context the executor passes to the backend on every call: plan, fencing of the current owner, and attempt.</summary>
    public sealed class ExecContext
    {
        public string PlanHash;
        public long Fencing;
        public int Attempt;
    }

    /// <summary>
    /// Result of <see cref="IExecutionBackend.Execute"/>. Contract: <c>Ok=false</c> means NOTHING was committed to the model
    /// (the backend undid it or never started). A backend that cannot guarantee this must throw, not return Ok=false.
    /// </summary>
    public sealed class ExecResult
    {
        public bool Ok;
        public string ErrorClass;
        public string Error;
        public Dictionary<string, object> Data = new Dictionary<string, object>(StringComparer.Ordinal);

        public static ExecResult Fail(string cls, string msg) => new ExecResult { Ok = false, ErrorClass = cls, Error = msg };
    }

    /// <summary>What is observed in the model about an operation left half done (AH-01).</summary>
    public enum Effect
    {
        /// <summary>The full effect of this operation is committed in the model.</summary>
        Observado,
        /// <summary>There is no effect of this operation in the model: it can run without duplicating.</summary>
        NoObservado,
        /// <summary>There is an effect, but it does not cover what the operation asks for now (e.g. the targets changed).</summary>
        Parcial,
        /// <summary>Cannot be known (unreadable store, effect applied more than once…).</summary>
        Incierto,
        /// <summary>There is no effect and no targets either: the operation would select an empty set.</summary>
        SinObjetivos,
    }

    public sealed class Observation
    {
        public Effect Effect;
        /// <summary>Committed transactions attributed to the operation.</summary>
        public long Transactions;
        /// <summary>Result reconstructed from model evidence, or null if the evidence is not enough (nothing is invented).</summary>
        public Dictionary<string, object> Reconstructed;
        public string Detail;
    }

    public enum PostState { Satisfechas, NoSatisfechas, NoEvaluables }

    /// <summary>Postcondition check against the AUTHORITATIVE model state (not against what the executor said).</summary>
    public sealed class PostcheckResult
    {
        public PostState State;
        public string Detail;
        public Dictionary<string, object> Evidence = new Dictionary<string, object>(StringComparer.Ordinal);
    }

    /// <summary>Read-only preparation: how many targets the operation would select now.</summary>
    public sealed class Preflight
    {
        public int Targets;
        public string Error;   // if it cannot be evaluated
    }

    /// <summary>
    /// AgentHost execution contract (0.7.0). The simulated one (<see cref="SimulatedBackend"/>) is the only one that writes in alpha1; the
    /// Revit adapter (<see cref="RevitTaskAdapter"/>) defines the boundary but rejects writes.
    /// </summary>
    public interface IExecutionBackend
    {
        string Name { get; }
        /// <summary>true if the backend can write to a real model. The alpha1 AgentHost rejects plans with writes in that case.</summary>
        bool TouchesRealModel { get; }
        string DocKey { get; }
        string DocPrint { get; }
        Preflight Prepare(ValidatedOp op);
        ExecResult Execute(ValidatedOp op, ExecContext ctx);
        /// <summary>For an interrupted operation: what effect does it have in the model? Never modifies the model.</summary>
        Observation Observe(ValidatedOp op, ExecContext ctx);
        /// <summary>Postconditions against the current model state. Never modifies the model.</summary>
        PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx);
        /// <summary>alpha3: "simulado", "ensayo" (temporary real write, always rolled back) or "persistente".</summary>
        string WriteMode => "simulado";
    }

    /// <summary>alpha3: the backend cannot know whether its write was committed (timeout, connection lost midway). The executor does not repeat it:
    /// it observes the model and decides (AH-01).</summary>
    public sealed class UncertainEffectException : Exception
    {
        public UncertainEffectException(string message) : base(message) { }
    }

    public static class Values
    {
        /// <summary>Equality by canonical form (2700 == 2700.0; "a" != "A").</summary>
        public static bool Same(object a, object b) => OpSemantics.Canonical(a) == OpSemantics.Canonical(b);
        public static string Show(object v) => v == null ? "null" : Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}
