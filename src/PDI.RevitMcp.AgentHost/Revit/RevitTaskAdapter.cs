// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PDI.RevitMcp.AgentHost
{
    /// <summary>Lo que el adaptador sabe del add-in (lo descubre por el puente, herramienta revit_context / tarea recetas).</summary>
    public sealed class AdapterCapabilities
    {
        public string RevitVersion;
        public string AddinVersion;
        public List<string> InstalledRecipes = new List<string>();
        public bool WritesEnabled;   // siempre false en 0.7.0-alpha1
        public string Reason;
    }

    /// <summary>
    /// Contrato de envío de una operación al motor de tareas EXISTENTE del add-in (0.6.x): herramienta "tarea", acción "iniciar", una receta
    /// instalada y sus argumentos. No hay un motor de transacciones nuevo: el add-in conserva concesiones, leases de documento, auditoría
    /// en dos fases y RecipeGate. El AgentHost nunca toca objetos de la API de Revit.
    /// </summary>
    public sealed class TaskSubmission
    {
        public string Tool = "tarea";
        public string Action = "iniciar";
        public string Recipe;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>Clave de idempotencia del puente (Wire.KeyArg): el id de la operación del IR. Reenviar no duplica.</summary>
        public string IdempotencyKey;
        /// <summary>Capacidades que el add-in exigirá (concedidas por una persona en Revit, nunca por el plan).</summary>
        public List<string> RequiredGrants = new List<string>();
        /// <summary>Pasos previos de lectura necesarios para resolver el alcance a ids (el puente devuelve un conjunto con nombre).</summary>
        public List<Dictionary<string, object>> ResolveScope = new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// Frontera de integración nativa (P1 §7). En 0.7.0-alpha1:
    /// - <see cref="Map"/> traduce operaciones del IR a <see cref="TaskSubmission"/> sobre recetas instaladas (determinista, probado);
    /// - <see cref="IExecutionBackend"/> está implementado para que el ejecutor lo rechace: TouchesRealModel=true con escrituras →
    ///   PlanRunner.Open devuelve escrituras_no_permitidas; Execute/Observe/Prepare responden "sin_conexion_alpha1" sin abrir el puente.
    /// Progreso, cancelación y resultados se harán con "tarea estado|cancelar|resumen" del puente cuando exista aceptación nativa.
    /// </summary>
    public sealed class RevitTaskAdapter : IExecutionBackend
    {
        public const string NotConnected = "sin_conexion_alpha1: el adaptador de Revit no abre el puente en 0.7.0-alpha1 (sin aceptación nativa)";

        /// <summary>Operación del IR → receta instalada del add-in (conjunto cerrado de 0.6.x).</summary>
        public static readonly Dictionary<string, string> RecipeFor = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fijar_parametro"] = "parametro_por_lote",
            ["verificar_qc"] = null,   // la receta es el parámetro "receta" (qc_manifiesto | montaje_soportes)
        };

        public RevitTaskAdapter(string docKey, string docPrint) { DocKey = docKey; DocPrint = docPrint; }

        public string Name => "revit (frontera, sin conexión)";
        public bool TouchesRealModel => true;
        public string DocKey { get; }
        public string DocPrint { get; }

        public AdapterCapabilities DiscoverCapabilities() => new AdapterCapabilities
        {
            WritesEnabled = false, Reason = NotConnected,
            InstalledRecipes = new List<string> { "parametro_por_lote", "etiquetar_luces", "exportar_laminas", "montaje_soportes", "qc_manifiesto" },
        };

        /// <summary>Traducción determinista. null + motivo si la operación no tiene receta instalada (no se improvisa código).</summary>
        public TaskSubmission Map(ValidatedOp op, out string why)
        {
            why = null;
            var s = new TaskSubmission { IdempotencyKey = op.Id };
            switch (op.Operation)
            {
                case "fijar_parametro":
                {
                    s.Recipe = "parametro_por_lote";
                    var name = "ah_" + op.Id.Substring(3, 12);
                    s.ResolveScope.Add(new Dictionary<string, object>
                    {
                        ["herramienta"] = "query", ["categoria"] = op.ScopeValue("categoria"), ["nivel"] = op.ScopeValue("nivel"), ["guardar_como"] = name,
                    });
                    s.Args["conjunto"] = "$" + name;
                    s.Args["parametro"] = op.Param("parametro");
                    var unit = op.Param("unidades");
                    var v = op.Params["valor"];
                    // la receta recibe el valor como en la interfaz ("2700 mm"); unidades explícitas siempre
                    s.Args["valor"] = unit == null || unit == "texto" || unit == "adimensional" ? Values.Show(v) : Values.Show(v) + " " + unit;
                    s.Args["guardar_al_final"] = "no";
                    s.RequiredGrants.Add("revit_modificar");
                    return s;
                }
                case "verificar_qc":
                    s.Recipe = op.Param("receta");
                    if (op.ScopeValue("nivel") != null) s.Args["nivel"] = op.ScopeValue("nivel");
                    return s;
                case "consultar":
                    s.Tool = "query"; s.Action = null; s.Recipe = null;
                    s.Args["categoria"] = op.Param("categoria");
                    s.Args["contar"] = true;
                    return s;
                default:
                    why = "operación sin receta instalada: " + op.Operation;
                    return null;
            }
        }

        public Preflight Prepare(ValidatedOp op) => new Preflight { Error = NotConnected };
        public ExecResult Execute(ValidatedOp op, ExecContext ctx) => ExecResult.Fail(ErrorClass.Rechazo, NotConnected);
        public Observation Observe(ValidatedOp op, ExecContext ctx) => new Observation { Effect = Effect.Incierto, Detail = NotConnected };
        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx) => new PostcheckResult { State = PostState.NoEvaluables, Detail = NotConnected };
    }
}
