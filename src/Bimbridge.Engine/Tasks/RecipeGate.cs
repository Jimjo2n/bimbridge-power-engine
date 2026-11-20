// SPDX-License-Identifier: GPL-3.0-only
using System;

namespace Bimbridge.Engine.Tasks
{
    /// <summary>
    /// Recipe execution boundary (rc3). ALL of a recipe's code (constructor and static initializers, properties,
    /// Preparar, the Unidades enumerator (each MoveNext), Ejecutar and Verificar) runs through <see cref="Run"/>:
    ///
    /// 1. before running, <c>authorize</c> (task lease, document lease and current grants) must return null;
    ///    otherwise the recipe never runs;
    /// 2. in read/planning phases (everything except Ejecutar), a model write counter (in Revit: the revision of the
    ///    DocumentTracker, which goes up with each committed transaction, plus "a transaction is open") must stay the same; if it changes,
    ///    the phase fails with <c>escritura_en_fase_de_lectura</c> and the task stops.
    ///
    /// This is not a sandbox: what the recipe can do outside the model is limited by the analyzer (closed set of installed
    /// recipes, receta_externa, always-denied capabilities). The boundary guarantees ORDER: authorization before code.
    /// </summary>
    public sealed class RecipeGate
    {
        private readonly Func<string> _authorize;
        private readonly Func<long> _writeCounter;

        public RecipeGate(Func<string> authorize, Func<long> writeCounter)
        {
            _authorize = authorize ?? (() => "no authority configured");
            _writeCounter = writeCounter ?? (() => 0);
        }

        /// <summary>Phases in which the recipe can NOT modify the model.</summary>
        public static bool IsReadPhase(string phase) => phase != Phase.Ejecutar;

        public static class Phase
        {
            public const string Constructor = "constructor";
            public const string Preparar = "preparar";
            public const string Enumerar = "enumerar";
            public const string Ejecutar = "ejecutar";
            public const string Verificar = "verificar";
            public const string Postcondicion = "postcondicion";
        }

        /// <summary>Runs <paramref name="body"/> only if the authority allows it. Returns null or the reason for the refusal/failure.
        /// Recipe exceptions propagate (the caller already turns them into a task failure).</summary>
        public string Run(string phase, Action body)
        {
            var why = _authorize();
            if (why != null) return "autorizacion_requerida (" + phase + "): " + why + ". The recipe was not run.";
            var before = _writeCounter();
            body();
            if (IsReadPhase(phase))
            {
                var after = _writeCounter();
                if (after != before)
                    return "escritura_en_fase_de_lectura: the recipe modified the model during '" + phase + "' (only Ejecutar may write, inside the engine's transaction). The task stops.";
            }
            return null;
        }

        /// <summary>Creates the recipe (constructor + initializers) inside the boundary.</summary>
        public string Create<T>(Func<T> factory, out T recipe) where T : class
        {
            T r = null;
            var why = Run(Phase.Constructor, () => r = factory());
            recipe = why == null ? r : null;
            return why;
        }
    }
}
