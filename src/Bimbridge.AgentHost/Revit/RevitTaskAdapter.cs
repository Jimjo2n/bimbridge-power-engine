// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bimbridge.AgentHost
{
    /// <summary>What the adapter knows about the add-in (discovered through the bridge, revit_context tool / tarea recetas).</summary>
    public sealed class AdapterCapabilities
    {
        public string RevitVersion;
        public string AddinVersion;
        public List<string> InstalledRecipes = new List<string>();
        public bool WritesEnabled;   // always false in 0.7.0-alpha1
        public string Reason;
    }

    /// <summary>
    /// Contract for submitting an operation to the add-in's EXISTING task engine (0.6.x): tool "tarea", action "iniciar", an installed
    /// recipe and its arguments. There is no new transaction engine: the add-in keeps grants, document leases, two-phase audit
    /// and RecipeGate. The AgentHost never touches Revit API objects.
    /// </summary>
    public sealed class TaskSubmission
    {
        public string Tool = "tarea";
        public string Action = "iniciar";
        public string Recipe;
        public Dictionary<string, object> Args = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>Bridge idempotency key (Wire.KeyArg): the IR operation id. Resending does not duplicate.</summary>
        public string IdempotencyKey;
        /// <summary>Capabilities the add-in will require (granted by a person in Revit, never by the plan).</summary>
        public List<string> RequiredGrants = new List<string>();
        /// <summary>Prior read steps needed to resolve the scope to ids (the bridge returns a named set).</summary>
        public List<Dictionary<string, object>> ResolveScope = new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// Native integration boundary (P1 §7). In 0.7.0-alpha1:
    /// - <see cref="Map"/> translates IR operations into <see cref="TaskSubmission"/> over installed recipes (deterministic, tested);
    /// - <see cref="IExecutionBackend"/> is implemented so that the executor rejects it: TouchesRealModel=true with writes →
    ///   PlanRunner.Open returns escrituras_no_permitidas; Execute/Observe/Prepare answer "sin_conexion_alpha1" without opening the bridge.
    /// Progress, cancellation and results will use the bridge's "tarea estado|cancelar|resumen" once native acceptance exists.
    /// </summary>
    public sealed class RevitTaskAdapter : IExecutionBackend
    {
        public const string NotConnected = "sin_conexion_alpha1: the Revit adapter does not open the bridge in 0.7.0-alpha1 (no native acceptance)";

        /// <summary>IR operation → installed add-in recipe (closed set from 0.6.x).</summary>
        public static readonly Dictionary<string, string> RecipeFor = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fijar_parametro"] = "parametro_por_lote",
            ["verificar_qc"] = null,   // the recipe is the "receta" parameter (qc_manifiesto | montaje_soportes)
        };

        public RevitTaskAdapter(string docKey, string docPrint) { DocKey = docKey; DocPrint = docPrint; }

        public string Name => "revit (boundary, not connected)";
        public bool TouchesRealModel => true;
        public string DocKey { get; }
        public string DocPrint { get; }

        public AdapterCapabilities DiscoverCapabilities() => new AdapterCapabilities
        {
            WritesEnabled = false, Reason = NotConnected,
            InstalledRecipes = new List<string> { "parametro_por_lote", "etiquetar_luces", "exportar_laminas", "montaje_soportes", "qc_manifiesto" },
        };

        /// <summary>Deterministic translation. null + reason if the operation has no installed recipe (no code is improvised).</summary>
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
                    // the recipe receives the value as in the UI ("2700 mm"); explicit units always
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
                    why = "operation without an installed recipe: " + op.Operation;
                    return null;
            }
        }

        public Preflight Prepare(ValidatedOp op) => new Preflight { Error = NotConnected };
        public ExecResult Execute(ValidatedOp op, ExecContext ctx) => ExecResult.Fail(ErrorClass.Rechazo, NotConnected);
        public Observation Observe(ValidatedOp op, ExecContext ctx) => new Observation { Effect = Effect.Incierto, Detail = NotConnected };
        public PostcheckResult CheckPostconditions(ValidatedOp op, ExecContext ctx) => new PostcheckResult { State = PostState.NoEvaluables, Detail = NotConnected };
    }
}
