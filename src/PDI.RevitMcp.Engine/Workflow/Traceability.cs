// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PDI.RevitMcp.Engine.Workflow
{
    /// <summary>
    /// 0.8.0: registro ÚNICO de capacidades y matriz de trazabilidad Workflow → CAP → Operación → Componente → Estado → Prueba → Resultado → Benchmark.
    /// docs/MATRIZ_TRAZABILIDAD_0.8.0.md se genera desde aquí (una prueba comprueba que coincide). El Estado es el de ESTE build 0.8.0:
    /// la evidencia nativa de builds anteriores va aparte (columna "nativo previo") y nunca se cuenta como prueba de este build.
    /// </summary>
    public static class Traceability
    {
        public const string Implemented = "IMPLEMENTED", Unit = "TESTED_UNIT", Simulated = "TESTED_SIMULATED", Native = "TESTED_NATIVE",
            NativePending = "NATIVE_ACCEPTANCE_PENDING", Blocked = "BLOCKED", NotSupported = "NOT_SUPPORTED", Deferred = "DEFERRED";

        public static readonly string[] States = { Implemented, Unit, Simulated, Native, NativePending, Blocked, NotSupported, Deferred };

        public sealed class Row
        {
            public string Workflows, Cap, Operacion, Familia, Componente, Estado, Prueba, Resultado, Benchmark, NativoPrevio;
        }

        public static readonly IReadOnlyDictionary<string, string> Workflows = new Dictionary<string, string>
        {
            ["WF-01"] = "Configurar plantilla y clasificación de vistas", ["WF-02"] = "Colocar iluminación a partir de planos",
            ["WF-03"] = "Montaje de luces y visibilidad en RCP", ["WF-04"] = "Emitir set de iluminación y hoja de QC",
            ["WF-05"] = "Prototipos de unidad, paneles y grupos", ["WF-06"] = "Modelar markups interiores y sanear RCP DIM",
            ["WF-07"] = "Modelar y auditar dos familias de feeds físicos", ["WF-08"] = "Ajustar cruces, colores y lámina de coordinación",
            ["WF-09"] = "Preparar un set parcial de impresión", ["WF-10"] = "Construir un riser unifilar 2D",
            ["WF-11"] = "Auditar y preparar el riser para emisión", ["WF-12"] = "Ejercicios de aula y edición de familias",
            ["WF-13"] = "Reparar conectores y reanudar trabajo", ["WF-14"] = "Transferir conocimiento y guiar al usuario",
        };

        public static readonly IReadOnlyDictionary<string, string> Caps = new Dictionary<string, string>
        {
            ["CAP-001"] = "Identidad, preflight y capacidades", ["CAP-002"] = "Consultas tipadas y compactas",
            ["CAP-003"] = "Plan transaccional y exclusión mutua", ["CAP-004"] = "Checkpoint, reanudación y sync",
            ["CAP-005"] = "Operaciones reutilizables sobre elementos y vistas", ["CAP-006"] = "Lámina, set, preset y emisión comprobable",
            ["CAP-007"] = "Verificación nativa y visual", ["CAP-008"] = "Fuentes, versiones y contraste independiente",
            ["CAP-009"] = "Registro geométrico y montaje", ["CAP-010"] = "Grupos, variantes y propagación",
            ["CAP-011"] = "Redes físicas y coordinación", ["CAP-012"] = "Grafo y composición de riser 2D",
            ["CAP-013"] = "Legibilidad de anotaciones y vistas demostrativas", ["CAP-014"] = "Editor de familias y conectores MEP",
            ["CAP-015"] = "Eventos, handoff y extensiones",
        };

        public static readonly string[] OpFamilies = { "modelado", "edición", "familias", "geometría", "MEP", "vistas", "láminas", "anotaciones", "documentos", "interoperabilidad", "coordinación", "publicación", "administración", "núcleo" };

        // Evidencia nativa de builds ANTERIORES (no cuenta para 0.8.0).
        private const string Rc1 = "rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11)";
        private const string Read024 = "0.2.x · Revit 2024 · lectura";
        private const string Read050 = "0.5.0 · Revit 2026 · lectura, una muestra";
        private const string Bk = "BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION";
        private const string SimOk = "OK sin Revit; nativo: " + Bk;

        public static readonly List<Row> All = new List<Row>
        {
            // ---------------- núcleo: identidad, plan, reanudación ----------------
            R("WF-13", "CAP-001", "ctx / identidad / instancias", "núcleo", "Add-in · AgentHost RevitLink", NativePending, "Rc1_FailClosed_MissingContextFlag (5), AgentHostRevitTests", SimOk, "B7", Rc1),
            R("WF-13", "CAP-001", "capacidades por documento (ctx) y este registro", "núcleo", "Engine Capabilities + Traceability", Unit, "Capabilities_DocMatchesCode, Traceability_DocMatchesCode", "OK", "—", Read050),
            R("WF-02,WF-05", "CAP-002", "consultar / q / get / params (paginado, proyección)", "núcleo", "Add-in Query · AgentHost ReadRows", NativePending, "Rc1 paginación (ForcePage, total que cambia)", SimOk, "B0", Read024 + "; " + Rc1),
            R("WF-02,WF-09,WF-13", "CAP-003", "plan → commit (diff, un Deshacer, idempotencia clave)", "núcleo", "Add-in PlanBuilder/PlanApplier · AgentHost PlanRunner", NativePending, "PlanTests, PlanQueueTests, AgentHostRevitTests", SimOk + " (0.8.0 cambió la auditoría del commit)", "B0, B7", Rc1),
            R("WF-13", "CAP-003", "exclusión por documento: lease + fencing", "núcleo", "AgentHost ExecJournal/Lease", NativePending, "LeaseFencingTests", SimOk, "B7", Rc1),
            R("WF-13", "CAP-003", "auditoría durable de cada commit persistente (intención → resultado)", "núcleo", "Add-in RequestHandler.Commit · Engine AuditLog", NativePending, "AuditLogTests, V080_Nrt07Harness", SimOk + " (cierra H2 de rc1)", "B7", "—"),
            R("WF-13", "CAP-004", "reanudar tras caída; incierto ⇒ observar, nunca repetir", "núcleo", "AgentHost PlanRunner/RevitBackend", NativePending, "NRT-03/05/06 simulados, V080_Handoff_AfterCrash", SimOk, "B7", Rc1),
            R("WF-13", "CAP-004", "respuesta IPC perdida tras commit (NRT-07)", "núcleo", "AgentHost RevitBackend + Faults", NativePending, "V080_Nrt07Harness_LostCommitResponse", SimOk, "B7", "—"),
            R("WF-13,WF-14", "CAP-015", "handoff estructurado (confirmadas / inciertas / siguiente paso seguro)", "núcleo", "AgentHost Handoff", Simulated, "V080_Handoff_AfterCrash_ProposesReconcile", "OK", "B7", "—"),
            R("WF-13", "CAP-015", "eventos firmados, agenda y notificaciones", "núcleo", "—", Deferred, "—", "0.9.0 (eventos mínimos)", "—", "—"),
            R("WF-13", "CAP-001", "escritura atómica de archivos de estado (temporal residual)", "núcleo", "Engine Home.WriteAtomic", Unit, "DurabilityTests", "OK; residuo nativo de rc1 sin recomprobar", "B7", "—"),

            // ---------------- fuentes, requisitos, verificación ----------------
            R("WF-02,WF-10,WF-11", "CAP-008", "requisitos con procedencia y autoridad; inferencia nunca OBSERVED", "núcleo", "Engine Workflow.Requirements", Unit, "WorkflowTests (9)", "OK", "B2, B5", "—"),
            R("WF-02,WF-11", "CAP-008", "verificar_independiente: fuente independiente → modelo → diferencias", "coordinación", "Engine IndependentVerifier · AgentHost RevitBackend", NativePending, "V080_IndependentVerification_AgainstModel", SimOk + " (sólo lectura)", "B2, B5", "—"),
            R("WF-02", "CAP-008", "adaptadores de fuentes (extraer PDF/tablas con coordenadas)", "coordinación", "—", Deferred, "—", "0.9.0", "B2", "—"),
            R("WF-04,WF-09,WF-11", "CAP-006", "estados de entrega (12 etapas) y recibos", "documentos", "Engine Workflow.Delivery", Unit, "WorkflowTests", "OK", "B6", "—"),

            // ---------------- edición y modelado ----------------
            R("WF-02", "CAP-005", "set_param / set_values / fijar_parametro_lote", "edición", "Add-in PlanApplier · AgentHost", NativePending, "PlanTests, Rc11_SlowIdleRevit, Bench_B0_586", SimOk, "B0", Rc1),
            R("WF-02", "CAP-005", "change_type / set_type_param / set_level / set_workset / set_elevation", "edición", "Add-in PlanApplier", NativePending, "PlanTests, WriteEngineTests", SimOk, "B2", "—"),
            R("WF-02,WF-05", "CAP-005", "move / rotate / mirror / copy / delete", "edición", "Add-in PlanApplier", NativePending, "WriteEngineTests.BuildMoved, PlanTests", SimOk, "—", "—"),
            R("WF-02", "CAP-005", "place (instancia por punto, montaje losa/nivel)", "modelado", "Add-in PlanApplier", NativePending, "WriteEngineTests.Parse_Validates", SimOk, "B2", "—"),
            R("—", "CAP-005", "muros, suelos, techos, cubiertas, escaleras (creación tipada)", "modelado", "—", NotSupported, "—", "sólo por exec (nivel B, código_dinámico)", "—", "—"),
            R("WF-05", "CAP-010", "group_create / ungroup / group_place", "edición", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B3", "—"),
            R("WF-05", "CAP-010", "cobertura de grupos por tipo/planta y propagación", "edición", "—", Deferred, "—", "0.9.0", "B3", "—"),

            // ---------------- geometría y montaje ----------------
            R("WF-03", "CAP-009", "geo montaje / choques / rangos / cuartos (mallas, rayos, vínculos)", "geometría", "Add-in GeoRunner · Engine TriMesh", NativePending, "GeoComputeTests, MeshTests", SimOk, "B2", Read024),
            R("WF-03", "CAP-009", "snap_to_surface (cara inferior real de losa/rampa)", "geometría", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B2", "—"),
            R("WF-02", "CAP-009", "registro plano–modelo con anclajes", "geometría", "—", Deferred, "—", "0.9.0", "B2", "—"),

            // ---------------- vistas ----------------
            R("WF-01,WF-03", "CAP-005", "view_create (planta, RCP, sección, 3D, dibujo) / view_set (plantilla, rango, detalle)", "vistas", "Add-in PlanApplier · WriteKit", NativePending, "WriteEngineTests, Draw2DTests", SimOk, "B1", "—"),
            R("WF-01,WF-08", "CAP-007", "view_hide / view_override (colores, medio tono, transparencia)", "vistas", "Add-in PlanApplier", NativePending, "WriteEngineTests", SimOk, "B1", "—"),
            R("WF-03,WF-11", "CAP-007", "chk lighting_audit / QC manifiesto por vista", "vistas", "Add-in Check · Engine QC", NativePending, "QcFixtureTests", SimOk, "B2", Read024),
            R("WF-06,WF-11", "CAP-007", "show / capture (señalar y capturar)", "vistas", "Add-in Show/Capture", NativePending, "—", "nativo: " + Bk, "—", Read024),

            // ---------------- láminas y emisión ----------------
            R("WF-04,WF-09", "CAP-006", "sheet_create / sheet_place", "láminas", "Add-in PlanApplier", NativePending, "WriteEngineTests", SimOk, "B6", "—"),
            R("WF-04,WF-09", "CAP-006", "sheet_set (conjunto de impresión persistente)", "láminas", "Add-in PlanApplier", NativePending, "SheetSetOpTests", SimOk + " (el orden lo decide Revit)", "B6", "—"),
            R("WF-04,WF-09", "CAP-006", "doc pdf (exportar hojas o conjunto y verificar archivos)", "documentos", "Add-in DocOps", NativePending, "—", "nativo: " + Bk + " (exige permiso exportar)", "B6", "—"),
            R("WF-04", "CAP-006", "verificar el PDF exportado (páginas, sha, revisión)", "documentos", "Engine Delivery (recibos)", Unit, "WorkflowTests (página ≠ set, sha ≠ export)", "OK; el lector de PDF es 0.9.0", "B6", "—"),

            // ---------------- anotación y dibujo 2D ----------------
            R("WF-02", "CAP-013", "tag / tag_move", "anotaciones", "Add-in PlanApplier", NativePending, "PlanTests", SimOk, "B3", "—"),
            R("WF-06", "CAP-013", "text_note / filled_region (contorno validado)", "anotaciones", "Add-in PlanApplier · Engine Draw2D", NativePending, "Draw2DTests", SimOk, "B5", "—"),
            R("WF-10", "CAP-012", "detail_line / detail_rect / detail_polygon / detail_arc (estilos de línea)", "anotaciones", "Add-in PlanApplier · Engine Draw2D", NativePending, "Draw2DTests (12)", SimOk, "B5", "—"),
            R("WF-10", "CAP-012", "detail_component (símbolos y familias de detalle)", "anotaciones", "Add-in PlanApplier", NativePending, "WriteEngineTests.Catalog", SimOk, "B5", "—"),
            R("WF-06", "CAP-013", "dimension (cota lineal entre líneas de detalle)", "anotaciones", "Add-in PlanApplier", NativePending, "Draw2DTests", SimOk, "B5", "—"),
            R("WF-10,WF-11", "CAP-012", "grafo de riser (nodos/aristas) y auditoría de conectividad", "anotaciones", "—", Deferred, "—", "0.9.0", "B5", "—"),
            R("WF-06,WF-08", "CAP-013", "legibilidad en papel (densidad, leaders, colisiones)", "anotaciones", "Engine Draw2D (papel↔vista, alinear, distribuir)", Deferred, "Draw2DTests (transformaciones)", "base 2D lista; optimización 0.9.0", "—", "—"),

            // ---------------- familias ----------------
            R("WF-12", "CAP-014", "fam info / caras", "familias", "Add-in FamilyOps", NativePending, "—", "nativo: " + Bk, "B8", Read050),
            R("WF-12,WF-07", "CAP-014", "fam abrir / conector / param / cargar", "familias", "Add-in FamilyOps", NativePending, "ScriptingTests (compila por año)", "nativo: " + Bk + " (exige permiso modificar_familia)", "B8", "—"),
            R("WF-12", "CAP-014", "fam buscar (índice de carpetas autorizadas, sin explorar discos)", "familias", "Engine FamilyLibrary", Unit, "FamilyLibraryTests (3)", "OK", "B8", "—"),
            R("WF-12", "CAP-014", "fam cargar_archivo (RFA indexado y sin cambios, sha-256)", "familias", "Engine FamilyLibrary · Add-in", NativePending, "FamilyLibraryTests", SimOk + " (exige permiso cargar_archivo)", "B8", "—"),

            // ---------------- MEP y coordinación ----------------
            R("WF-07,WF-08", "CAP-011", "routing físico, fittings, stubs y reconciliación de cotas", "MEP", "—", Deferred, "—", "0.9.0", "B4", "—"),
            R("WF-08", "CAP-011", "detección de choques con vínculos", "coordinación", "Add-in GeoRunner", NativePending, "GeoComputeTests", SimOk, "B4", Read024),

            // ---------------- documentos ----------------
            R("WF-04,WF-13", "CAP-004", "doc guardar / guardar_como (verificación de archivo y hash)", "documentos", "Add-in DocOps", NativePending, "NRT-05 simulado", SimOk + " (exige permiso guardar)", "B6", Rc1),
            R("WF-04,WF-13", "CAP-004", "doc sync (sólo workshared, permiso sincronizar)", "documentos", "Add-in DocOps", NativePending, "WorksharedPolicyTests", "nativo: " + Bk + " (nunca sobre centrales corporativas)", "B6", "—"),

            // ---------------- interoperabilidad, publicación, administración ----------------
            R("—", "CAP-006", "exportar IFC / DWG / NWC (tipado)", "interoperabilidad", "—", NotSupported, "—", "sólo por exec (nivel B)", "—", "—"),
            R("—", "CAP-006", "publicar (ACC, BIM 360, correo)", "publicación", "—", NotSupported, "PermissionsTests (siempre denegado)", "no existe operación de publicar", "—", "—"),
            R("WF-13", "CAP-001", "permisos separados (10 clases) aplicados en el add-in", "administración", "Protocol Permissions · Add-in", NativePending, "PermissionsTests (3)", SimOk, "—", "—"),
            R("WF-13", "CAP-005", "exec C# controlado (consulta / ensayo / ejecutar) con revisión y auditoría", "administración", "Add-in Exec · Scripting", NativePending, "ScriptingTests, TrialGuardTests", SimOk, "—", "—"),
            R("—", "CAP-005", "interacción supervisada con la interfaz (nivel C)", "administración", "—", NotSupported, "—", "sólo show/capture; ningún clic automatizado", "—", "—"),
            R("WF-13", "CAP-001", "Actualizar desde ZIP (firma, hashes, compatibilidad, aplicar al cerrar Revit, reversión)", "administración", "Setup.Core ZipUpdate · Setup /aplicar-pendiente · Add-in", NativePending, "ZipUpdateTests (12)", SimOk + "; firma: clave pendiente de IT", "—", "—"),
            R("WF-13", "CAP-001", "instalador con manifiesto SHA-256 y /diagnostico", "administración", "Setup.Core", Unit, "SetupTests", "OK", "B8", Rc1),
            R("—", "CAP-001", "medición de tokens ESTIMADOS y llamadas por cable", "administración", "Engine TokenEstimate · AgentHost Bench", Simulated, "Bench_B0_586_OneByOne_vs_Plan", "OK (1.172 → 3 llamadas del agente, simulado)", "B0", "—"),
        };

        private static Row R(string wf, string cap, string op, string fam, string comp, string estado, string prueba, string res, string bench, string prev) =>
            new Row { Workflows = wf, Cap = cap, Operacion = op, Familia = fam, Componente = comp, Estado = estado, Prueba = prueba, Resultado = res, Benchmark = bench, NativoPrevio = prev };

        /// <summary>Problemas de coherencia del registro (estados, CAP y WF conocidos, cobertura). Vacío = coherente.</summary>
        public static List<string> Audit()
        {
            var p = new List<string>();
            foreach (var r in All)
            {
                if (!States.Contains(r.Estado)) p.Add(r.Operacion + ": estado " + r.Estado);
                if (!Caps.ContainsKey(r.Cap)) p.Add(r.Operacion + ": CAP " + r.Cap);
                if (!OpFamilies.Contains(r.Familia)) p.Add(r.Operacion + ": familia " + r.Familia);
                foreach (var w in Split(r.Workflows)) if (!Workflows.ContainsKey(w)) p.Add(r.Operacion + ": " + w);
                if (r.Estado == Native) p.Add(r.Operacion + ": TESTED_NATIVE sin campaña nativa de 0.8.0");
            }
            foreach (var c in Caps.Keys) if (!All.Any(r => r.Cap == c)) p.Add(c + " sin filas");
            foreach (var w in Workflows.Keys) if (!All.Any(r => Split(r.Workflows).Contains(w))) p.Add(w + " sin filas");
            foreach (var f in OpFamilies) if (!All.Any(r => r.Familia == f)) p.Add("familia " + f + " sin filas");
            return p;
        }

        private static IEnumerable<string> Split(string wf) => (wf ?? "").Split(',').Select(x => x.Trim()).Where(x => x.StartsWith("WF-", StringComparison.Ordinal));

        public static Dictionary<string, int> CountByState() => States.ToDictionary(s => s, s => All.Count(r => r.Estado == s));

        public static string Markdown(string version, string commitNote)
        {
            string Esc(string s) => (s ?? "").Replace("|", "\\|");
            var sb = new StringBuilder();
            sb.Append("# Matriz de trazabilidad — BIMBRIDGE ").Append(version).Append("\n\n");
            sb.Append("Generada desde `src/PDI.RevitMcp.Engine/Workflow/Traceability.cs` (una prueba comprueba que coincide). ").Append(commitNote).Append("\n\n");
            sb.Append("**Estado** es el de este build 0.8.0. Ninguna fila es TESTED_NATIVE: no hubo campaña nativa de 0.8.0 (entorno nativo de prueba no disponible). ");
            sb.Append("La evidencia nativa de builds anteriores está en *nativo previo* y no se cuenta como prueba de este build. ");
            sb.Append("Los tokens son ESTIMADOS (caracteres/4).\n\n");
            sb.Append("Recuento: ").Append(string.Join(" · ", CountByState().Where(k => k.Value > 0).Select(k => k.Key + " " + k.Value))).Append(" · total ").Append(All.Count).Append("\n\n");
            sb.Append("## Matriz única\n\n| Workflow | CAP | Operación | Familia | Componente | Estado | Prueba | Resultado | Benchmark | Nativo previo |\n|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in All)
                sb.Append("| ").Append(Esc(r.Workflows)).Append(" | ").Append(r.Cap).Append(" | ").Append(Esc(r.Operacion)).Append(" | ").Append(r.Familia).Append(" | ")
                  .Append(Esc(r.Componente)).Append(" | ").Append(r.Estado).Append(" | ").Append(Esc(r.Prueba)).Append(" | ").Append(Esc(r.Resultado)).Append(" | ")
                  .Append(Esc(r.Benchmark)).Append(" | ").Append(Esc(r.NativoPrevio)).Append(" |\n");

            sb.Append("\n## Por capacidad (CAP)\n\n| CAP | Nombre | Filas | Estados |\n|---|---|---|---|\n");
            foreach (var c in Caps)
            {
                var rows = All.Where(r => r.Cap == c.Key).ToList();
                sb.Append("| ").Append(c.Key).Append(" | ").Append(c.Value).Append(" | ").Append(rows.Count).Append(" | ")
                  .Append(string.Join(", ", rows.GroupBy(r => r.Estado).OrderBy(g => Array.IndexOf(States, g.Key)).Select(g => g.Key + " " + g.Count()))).Append(" |\n");
            }
            sb.Append("\n## Por workflow\n\n| WF | Nombre | Operaciones | Implementadas (sin aceptación nativa de 0.8.0) | Diferidas / no soportadas |\n|---|---|---|---|---|\n");
            foreach (var w in Workflows)
            {
                var rows = All.Where(r => Split(r.Workflows).Contains(w.Key)).ToList();
                var ok = rows.Count(r => r.Estado == Unit || r.Estado == Simulated || r.Estado == NativePending);
                var no = rows.Count(r => r.Estado == Deferred || r.Estado == NotSupported);
                sb.Append("| ").Append(w.Key).Append(" | ").Append(w.Value).Append(" | ").Append(rows.Count).Append(" | ").Append(ok).Append(" | ").Append(no).Append(" |\n");
            }
            sb.Append("\n## Por familia de operaciones de Revit\n\n| Familia | Filas | Estados |\n|---|---|---|\n");
            foreach (var f in OpFamilies)
            {
                var rows = All.Where(r => r.Familia == f).ToList();
                sb.Append("| ").Append(f).Append(" | ").Append(rows.Count).Append(" | ")
                  .Append(string.Join(", ", rows.GroupBy(r => r.Estado).OrderBy(g => Array.IndexOf(States, g.Key)).Select(g => g.Key + " " + g.Count()))).Append(" |\n");
            }
            sb.Append("\n*Implementadas* = el código existe en este build (con o sin pruebas sin Revit, ver la columna Prueba); para el usuario sólo cuentan tras la campaña nativa.\n");
            return sb.ToString();
        }
    }
}
