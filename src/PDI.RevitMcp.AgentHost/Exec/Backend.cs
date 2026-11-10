// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Contexto que el ejecutor pasa al backend en cada llamada: plan, fencing del dueño actual e intento.</summary>
    public sealed class ExecContext
    {
        public string PlanHash;
        public long Fencing;
        public int Attempt;
    }

    /// <summary>
    /// Resultado de <see cref="IExecutionBackend.Execute"/>. Contrato: <c>Ok=false</c> significa que NADA se confirmó en el modelo
    /// (el backend deshizo o no empezó). Un backend que no pueda garantizarlo debe lanzar, no devolver Ok=false.
    /// </summary>
    public sealed class ExecResult
    {
        public bool Ok;
        public string ErrorClass;
        public string Error;
        public Dictionary<string, object> Data = new Dictionary<string, object>(StringComparer.Ordinal);

        public static ExecResult Fail(string cls, string msg) => new ExecResult { Ok = false, ErrorClass = cls, Error = msg };
    }

    /// <summary>Qué se observa en el modelo sobre una operación que quedó a medias (AH-01).</summary>
    public enum Effect
    {
        /// <summary>El efecto completo de esta operación está confirmado en el modelo.</summary>
        Observado,
        /// <summary>No hay ningún efecto de esta operación en el modelo: se puede ejecutar sin duplicar.</summary>
        NoObservado,
        /// <summary>Hay efecto, pero no cubre lo que la operación pide ahora (p. ej. los objetivos cambiaron).</summary>
        Parcial,
        /// <summary>No se puede saber (almacén ilegible, efecto aplicado más de una vez…).</summary>
        Incierto,
        /// <summary>No hay efecto y tampoco hay objetivos: la operación seleccionaría un conjunto vacío.</summary>
        SinObjetivos,
    }

    public sealed class Observation
    {
        public Effect Effect;
        /// <summary>Transacciones confirmadas atribuidas a la operación.</summary>
        public long Transactions;
        /// <summary>Resultado reconstruido desde evidencia del modelo, o null si la evidencia no basta (no se inventa).</summary>
        public Dictionary<string, object> Reconstructed;
        public string Detail;
    }

    public enum PostState { Satisfechas, NoSatisfechas, NoEvaluables }

    /// <summary>Comprobación de postcondiciones contra el estado AUTORITATIVO del modelo (no contra lo que dijo el ejecutor).</summary>
    public sealed class PostcheckResult
    {
        public PostState State;
        public string Detail;
        public Dictionary<string, object> Evidence = new Dictionary<string, object>(StringComparer.Ordinal);
    }

    /// <summary>Preparación de solo lectura: cuántos objetivos seleccionaría la operación ahora.</summary>
    public sealed class Preflight
    {
        public int Targets;
        public string Error;   // si no se puede evaluar
    }

    /// <summary>
    /// Contrato de ejecución del AgentHost (0.7.0). El simulado (<see cref="SimulatedBackend"/>) es el único que escribe en alpha1; el
    /// adaptador de Revit (<see cref="RevitTaskAdapter"/>) define la frontera pero rechaza escrituras.
    /// </summary>
    public interface IExecutionBackend
    {
        string Name { get; }
        /// <summary>true si el backend puede escribir en un modelo real. El AgentHost alpha1 rechaza planes con escrituras en ese caso.</summary>
        bool TouchesRealModel { get; }
        string DocKey { get; }
        string DocPrint { get; }
        Preflight Prepare(ValidatedOp op);
        ExecResult Execute(ValidatedOp op, ExecContext ctx);
        /// <summary>Para una operación interrumpida: ¿qué efecto tiene en el modelo? Nunca modifica el modelo.</summary>
        Observation Observe(ValidatedOp op, ExecContext ctx);
        /// <summary>Postcondiciones contra el estado actual del modelo. Nunca modifica el modelo.</summary>
        PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx);
        /// <summary>alpha3: "simulado", "ensayo" (escritura real temporal, siempre revertida) o "persistente".</summary>
        string WriteMode => "simulado";
    }

    /// <summary>alpha3: el backend no puede saber si su escritura se confirmó (timeout, conexión perdida a mitad). El ejecutor no la repite:
    /// observa el modelo y decide (AH-01).</summary>
    public sealed class UncertainEffectException : Exception
    {
        public UncertainEffectException(string message) : base(message) { }
    }

    public static class Values
    {
        /// <summary>Igualdad por forma canónica (2700 == 2700.0; "a" != "A").</summary>
        public static bool Same(object a, object b) => OpSemantics.Canonical(a) == OpSemantics.Canonical(b);
        public static string Show(object v) => v == null ? "null" : Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}
