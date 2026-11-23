// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bimbridge.Addin.Scripting
{
    /// <summary>How a recipe's units are committed.</summary>
    public enum TxPolicy
    {
        /// <summary>Each unit in its own transaction (Undo per unit; a failure rolls back only that unit).</summary>
        PerUnit,
        /// <summary>The recipe opens its own transactions. The engine measures the actual changes to tell "nothing applied" from "partial".</summary>
        None,
        /// <summary>Read-only (QC, inventories): opens no transactions; a detected change is reported.</summary>
        ReadOnly,
    }

    public enum Verification { Applied, NotApplied, Unknown }

    /// <summary>
    /// A unit of work: a stable key (the same when resuming on the same model), data to run it and, optionally, the
    /// elements it reads or changes (if another user changes them between enumeration and run, the unit goes into conflict instead of overwriting).
    /// It can also be a barrier: guardar, sync, pdf, cargar (runs only when everything before it is committed).
    /// </summary>
    public sealed class Unit
    {
        public Unit(string key, object data = null, IEnumerable<long> ids = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("every unit needs a stable key");
            Key = key;
            Data = data;
            Ids = ids?.ToList() ?? new List<long>();
        }

        public string Key { get; }
        public object Data { get; }
        public IList<long> Ids { get; }
        public string BarrierAction { get; private set; }
        public Dictionary<string, object> BarrierArgs { get; private set; }

        /// <summary>Data as a dictionary (if passed that way) to read with D("field").</summary>
        public object D(string field) => Data is IDictionary<string, object> d && d.TryGetValue(field, out var v) ? v : null;

        public static Unit Save(string key = "save") => new Unit(key) { BarrierAction = "save", BarrierArgs = new Dictionary<string, object>() };
        public static Unit Sync(string key = "sync", string comment = null) =>
            new Unit(key) { BarrierAction = "sync", BarrierArgs = new Dictionary<string, object> { ["comment"] = comment } };
        public static Unit Pdf(string key, Dictionary<string, object> options) => new Unit(key) { BarrierAction = "pdf", BarrierArgs = options ?? new Dictionary<string, object>() };
        public static Unit Barrier(string key, string action, Dictionary<string, object> args = null) =>
            new Unit(key) { BarrierAction = action, BarrierArgs = args ?? new Dictionary<string, object>() };
    }

    /// <summary>The recipe decided to do nothing on this unit (it was already fine, or a declared exception). It is recorded with the reason.</summary>
    public sealed class SkipUnit : Exception
    {
        public SkipUnit(string reason) : base(reason) { }
    }

    /// <summary>
    /// Step-by-step recipe for the resumable engine (0.6.0, §5). Contract:
    /// - <see cref="Prepare"/>: validates inputs (throws ArgumentException if any are missing) — read-only;
    /// - <see cref="Units"/>: enumerates units with STABLE, deterministic keys on the same model; it can be lazy
    ///   (yield): the engine walks it in chunks without blocking Revit;
    /// - <see cref="Run"/>: applies ONE unit; with PerUnit the engine opens and commits the transaction and the result is materialized
    ///   before committing;
    /// - <see cref="Verify"/>: after a crash, was it applied? By default the engine checks whether the created elements exist (UniqueId);
    /// - <see cref="Postcondition"/>: final read-only QC; return a dictionary with "deviations" (list) to flag differences.
    /// Registered by file (recipes\name.cs) or sent as code; no MCP tool per recipe is needed.
    /// </summary>
    public abstract class Recipe
    {
        /// <summary>Declared version: part of the task fingerprint.</summary>
        public virtual string Version => "1";
        public virtual string Description => "";
        public virtual TxPolicy Transaction => TxPolicy.PerUnit;
        /// <summary>Inputs expected in args (only for documentation and presence validation).</summary>
        public virtual IEnumerable<string> Inputs => Enumerable.Empty<string>();

        public virtual void Prepare(ScriptContext ctx)
        {
            var missing = Inputs.Where(e => !ctx.Args.ContainsKey(e)).ToList();
            if (missing.Count > 0) throw new ArgumentException("missing inputs in args: " + string.Join(", ", missing));
        }

        public abstract IEnumerable<Unit> Units(ScriptContext ctx);

        public abstract object Run(ScriptContext ctx, Unit u);

        public virtual Verification Verify(ScriptContext ctx, Unit u) => Verification.Unknown;

        public virtual object Postcondition(ScriptContext ctx) => null;
    }
}
