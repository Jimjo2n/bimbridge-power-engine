// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>
    /// Estados de ejecución de una operación. Sólo el ejecutor los cambia, y sólo por transiciones legales (<see cref="IsLegal"/>);
    /// el diario rechaza cualquier otra al recuperar.
    /// </summary>
    public static class OpState
    {
        public const string Validada = "validada";
        public const string EnEjecucion = "en_ejecucion";
        public const string Ejecutada = "ejecutada";
        public const string Verificada = "verificada";
        public const string Fallida = "fallida";
        /// <summary>Hay efecto o puede haberlo, pero no hay evidencia suficiente para certificar el resultado. No se repite.</summary>
        public const string RequiereVerificacion = "requiere_verificacion";
        /// <summary>Efecto parcial o incierto: lo decide una persona. No se repite.</summary>
        public const string RequiereReconciliar = "requiere_reconciliar";
        public const string Bloqueada = "bloqueada";
        public const string Cancelada = "cancelada";
        /// <summary>alpha3: escritura TEMPORAL verificada y revertida (modo ensayo contra Revit). Final; nunca cuenta como aplicada.</summary>
        public const string Ensayada = "ensayada";

        public static readonly string[] All = { Validada, EnEjecucion, Ejecutada, Verificada, Fallida, RequiereVerificacion, RequiereReconciliar, Bloqueada, Cancelada, Ensayada };
        public static readonly string[] Final = { Verificada, Fallida, RequiereVerificacion, RequiereReconciliar, Bloqueada, Cancelada, Ensayada };
        /// <summary>Finales que necesitan a una persona o a un agente que razone.</summary>
        public static readonly string[] Exceptions = { Fallida, RequiereVerificacion, RequiereReconciliar, Bloqueada };

        public static bool IsValid(string s) => Array.IndexOf(All, s) >= 0;
        public static bool IsFinal(string s) => Array.IndexOf(Final, s) >= 0;

        private static readonly Dictionary<string, string[]> Legal = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // validada → fallida: precondición o conjunto vacío antes de escribir
            [Validada] = new[] { EnEjecucion, Fallida, Bloqueada, Cancelada },
            // en_ejecucion → validada: sólo si el efecto NO se observó (recuperación) o el backend rechazó sin aplicar (reintento)
            [EnEjecucion] = new[] { Ejecutada, Fallida, Validada, RequiereVerificacion, RequiereReconciliar },
            [Ejecutada] = new[] { Verificada, Fallida, RequiereVerificacion, Ensayada, RequiereReconciliar },
        };

        /// <summary>
        /// beta1 §6: nombre del contrato de ejecución para cada estado existente (sin un enum paralelo). AUTHORIZED no es un estado de operación:
        /// es "validada" con una autorización vigente del plan (GrantStore); por eso se pide aparte.
        /// REJECTED = la operación no llegó a escribir (bloqueada o fallida antes de ejecutar); ROLLED_BACK = ensayo revertido.
        /// </summary>
        public static string DirectiveName(string state, bool authorized = false, bool wroteNothing = false)
        {
            switch (state)
            {
                case Validada: return authorized ? "AUTHORIZED" : "PREPARED";
                case EnEjecucion: return "EXECUTING";
                case Ejecutada: return "COMMITTED";
                case Verificada: return "VERIFIED";
                case Ensayada: return "ROLLED_BACK";
                case Bloqueada: case Cancelada: return "REJECTED";
                case Fallida: return wroteNothing ? "REJECTED" : "FAILED";
                case RequiereVerificacion: case RequiereReconciliar: return "OUTCOME_UNKNOWN";
                default: throw new ArgumentException("estado desconocido: " + state);
            }
        }

        public static bool IsLegal(string from, string to) => Legal.TryGetValue(from, out var t) && Array.IndexOf(t, to) >= 0;
    }

    /// <summary>Clasificación de errores (estable: los agentes la usan para decidir).</summary>
    public static class ErrorClass
    {
        public const string Precondicion = "precondicion";
        public const string SinObjetivos = "sin_objetivos";
        public const string Rechazo = "rechazo_backend";
        public const string Transitorio = "transitorio";
        public const string Verificacion = "verificacion";
        public const string EfectoParcial = "efecto_parcial";
        public const string EfectoIncierto = "efecto_incierto";
        public const string EvidenciaInsuficiente = "evidencia_insuficiente";
        public const string Dependencia = "dependencia";
        public const string Cancelacion = "cancelacion";
        public const string DocumentoDistinto = "documento_distinto";
        public const string EstadoCorrupto = "estado_corrupto";
        public const string PlanEnUso = "plan_en_uso";
        public const string Interno = "interno";
    }

    /// <summary>Estado mutable de una operación (lo único mutable de la ejecución).</summary>
    public sealed class OpRecord
    {
        public string State = OpState.Validada;
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

    /// <summary>Estado del plan en conjunto (derivado del diario).</summary>
    public static class PlanRunState
    {
        public const string Activo = "activo";
        public const string Pausado = "pausado";
        public const string Cancelado = "cancelado";
    }

    /// <summary>Un diagnóstico de recuperación. Severidad: info | aviso | error (error = no se puede recuperar automáticamente).</summary>
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
