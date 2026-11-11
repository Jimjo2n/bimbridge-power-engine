// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDI.RevitMcp.Engine.Workflows
{
    /// <summary>Modos de ejecución de una capacidad nativa (#23 §1).</summary>
    public enum ExecMode { read, preflight, plan, direct_api, interactive_invocation, ui_assisted, manual_handoff }

    /// <summary>Nivel de soporte de un modo: probado (campaña nativa), experimental (implementado sin prueba nativa) o no disponible.</summary>
    public enum SupportLevel { proven, experimental, unavailable }

    /// <summary>Lo que el cliente puede hacer de verdad, medido en tiempo de ejecución (no supuesto por el catálogo de herramientas).</summary>
    public sealed class ClientEnvironment
    {
        /// <summary>Hay un ejecutor de UI autorizado que puede responder diálogos de Revit (no lo hay por defecto).</summary>
        public bool UiExecutorAvailable;
        public string UiExecutorName;
        /// <summary>El usuario autorizó guardar el documento destino en esta sesión.</summary>
        public bool SaveAuthorized;
    }

    public sealed class ModeSupport
    {
        public ExecMode Mode;
        public SupportLevel Level;
        public string Why;
    }

    /// <summary>Descriptor versionado y descubrible de una operación nativa por versión de Revit y entorno del cliente (#23 §1).</summary>
    public sealed class NativeCapability
    {
        public const string Schema = "bimbridge.native_capability/1";
        public string Operation;
        public int RevitVersion;
        public List<ModeSupport> Modes = new List<ModeSupport>();
        public bool RequiresUiExecutor;
        public List<string> Validations = new List<string>();
        public List<string> Limitations = new List<string>();

        public SupportLevel LevelOf(ExecMode m) => Modes.FirstOrDefault(x => x.Mode == m)?.Level ?? SupportLevel.unavailable;

        /// <summary>El mejor modo para llegar al final: API directa, si no UI asistida, si no traspaso manual.</summary>
        public ExecMode Recommended =>
            LevelOf(ExecMode.direct_api) != SupportLevel.unavailable ? ExecMode.direct_api
            : LevelOf(ExecMode.ui_assisted) != SupportLevel.unavailable ? ExecMode.ui_assisted
            : ExecMode.manual_handoff;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["esquema"] = Schema, ["operacion"] = Operation, ["revit"] = (long)RevitVersion, ["recomendado"] = Recommended.ToString(),
            ["requiere_ejecutor_ui"] = RequiresUiExecutor,
            ["modos"] = Modes.Select(m => (object)new Dictionary<string, object> { ["modo"] = m.Mode.ToString(), ["soporte"] = m.Level.ToString(), ["motivo"] = m.Why }).ToList(),
            ["validaciones"] = Validations.Cast<object>().ToList(), ["limitaciones"] = Limitations.Cast<object>().ToList(),
        };
    }

    /// <summary>
    /// Matriz de capacidades nativas. Se apoya en el inventario offline de la API (0.9.1 Bloque B): Copy/Monitor NO tiene API pública de
    /// copia por lotes en 2022–2026 (no existe CopyMonitorOptions ni equivalente); sí la hay para leer vínculos e inspeccionar relaciones
    /// de supervisión (Element.IsMonitoringLinkElement / GetMonitoredLinkElementIds) y para invocar el comando (PostCommand).
    /// Nada aquí es "proven" hasta la campaña nativa única.
    /// </summary>
    public static class NativeCapabilities
    {
        public const string CopyMonitorFixtures = "copy_monitor_fixtures";
        public static readonly string[] Operations = { CopyMonitorFixtures };

        public static NativeCapability Describe(string operation, int revitVersion, ClientEnvironment env)
        {
            if (revitVersion < 2022 || revitVersion > 2026) throw new ArgumentOutOfRangeException(nameof(revitVersion), "Revit 2022–2026");
            env = env ?? new ClientEnvironment();
            switch (operation)
            {
                case CopyMonitorFixtures:
                {
                    var c = new NativeCapability { Operation = operation, RevitVersion = revitVersion, RequiresUiExecutor = true };
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.read, Level = SupportLevel.experimental, Why = "inventario de vínculos, aparatos de origen y del anfitrión con la API pública" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.preflight, Level = SupportLevel.experimental, Why = "vínculo cargado, transformación, niveles, categorías y conteos por tipo" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.plan, Level = SupportLevel.experimental, Why = "mapeo de tipos propuesto y excepciones esperadas, sin tocar el modelo" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.direct_api, Level = SupportLevel.unavailable, Why = "no hay API pública de Copia por lotes / Coordination Settings en 2022–2026 (inventario offline)" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.interactive_invocation, Level = SupportLevel.experimental, Why = "PostCommand(CopyMonitorSelectLink) sólo abre el modo nativo: aceptar la orden no es copiar" });
                    c.Modes.Add(env.UiExecutorAvailable
                        ? new ModeSupport { Mode = ExecMode.ui_assisted, Level = SupportLevel.experimental, Why = "ejecutor de UI declarado: " + (env.UiExecutorName ?? "sin nombre") + "; cada paso se demuestra con evidencia observada" }
                        : new ModeSupport { Mode = ExecMode.ui_assisted, Level = SupportLevel.unavailable, Why = "no hay ejecutor de UI autorizado en este cliente" });
                    c.Modes.Add(new ModeSupport { Mode = ExecMode.manual_handoff, Level = SupportLevel.experimental, Why = "pasos exactos para el usuario + verificación posterior con la API" });
                    c.Validations.AddRange(new[]
                    {
                        "conteo y tipo de aparatos de origen por familia/tipo", "transformación del vínculo (identidad o la declarada)",
                        "relación de supervisión de cada copia (IsMonitoringLinkElement)", "familia/tipo esperado tras el mapeo", "posición y orientación tras la transformación",
                        "duplicados, ausentes, tipo incorrecto y sin supervisión", "guardado sólo con autorización expresa y relectura posterior",
                    });
                    c.Limitations.AddRange(new[]
                    {
                        "GetMonitoredLinkElementIds devuelve ids de INSTANCIAS de vínculo, no del aparato de origen: la correspondencia origen→copia se infiere por tipo y posición",
                        "ElementTransformUtils.CopyElements no crea relaciones de supervisión: nunca sustituye a Copy/Monitor",
                        "sin campaña nativa: ningún modo es 'proven'",
                    });
                    return c;
                }
                default:
                    throw new ArgumentException("operación nativa desconocida: " + operation + ". Conocidas: " + string.Join(", ", Operations));
            }
        }

        /// <summary>Pasos exactos del flujo nativo para el usuario cuando no hay ejecutor de UI (traspaso manual veraz).</summary>
        public static List<string> CopyMonitorManualSteps(string linkName, IEnumerable<string> typeMappings, bool saveAuthorized)
        {
            var steps = new List<string>
            {
                "Pestaña Colaborar > Copiar/Supervisar > Seleccionar vínculo, y elija el vínculo '" + linkName + "'.",
                "En la barra Copiar/Supervisar, abra Parámetros de coordinación.",
                "Categoría Aparatos sanitarios (Plumbing Fixtures): marque 'Permitir copia por lotes'.",
            };
            var maps = (typeMappings ?? Enumerable.Empty<string>()).ToList();
            steps.Add(maps.Count == 0
                ? "Mapeo de tipos: deje 'Copiar tipo original' para todos los tipos."
                : "Mapeo de tipos: " + string.Join("; ", maps) + ".");
            steps.Add("Pulse Aceptar y luego Copia por lotes; en 'Aparatos encontrados' elija copiar los aparatos.");
            steps.Add("Pulse Finalizar en la barra Copiar/Supervisar.");
            steps.Add(saveAuthorized ? "Guarde el documento destino." : "No guarde todavía: primero se verifica el resultado.");
            steps.Add("Avise al asistente para que lea el modelo y verifique conteos, tipos, posición y supervisión.");
            return steps;
        }
    }
}
