# Matriz de trazabilidad — BIMBRIDGE 0.8.0

Generada desde `src/PDI.RevitMcp.Engine/Workflow/Traceability.cs` (una prueba comprueba que coincide). Candidato interno; aceptación nativa pendiente.

**Estado** es el de este build 0.8.0. Ninguna fila es TESTED_NATIVE: no hubo campaña nativa de 0.8.0 (entorno nativo de prueba no disponible). La evidencia nativa de builds anteriores está en *nativo previo* y no se cuenta como prueba de este build. Los tokens son ESTIMADOS (caracteres/4).

Recuento: TESTED_UNIT 7 · TESTED_SIMULATED 2 · NATIVE_ACCEPTANCE_PENDING 36 · NOT_SUPPORTED 4 · DEFERRED 7 · total 56

## Matriz única

| Workflow | CAP | Operación | Familia | Componente | Estado | Prueba | Resultado | Benchmark | Nativo previo |
|---|---|---|---|---|---|---|---|---|---|
| WF-13 | CAP-001 | ctx / identidad / instancias | núcleo | Add-in · AgentHost RevitLink | NATIVE_ACCEPTANCE_PENDING | Rc1_FailClosed_MissingContextFlag (5), AgentHostRevitTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-001 | capacidades por documento (ctx) y este registro | núcleo | Engine Capabilities + Traceability | TESTED_UNIT | Capabilities_DocMatchesCode, Traceability_DocMatchesCode | OK | — | 0.5.0 · Revit 2026 · lectura, una muestra |
| WF-02,WF-05 | CAP-002 | consultar / q / get / params (paginado, proyección) | núcleo | Add-in Query · AgentHost ReadRows | NATIVE_ACCEPTANCE_PENDING | Rc1 paginación (ForcePage, total que cambia) | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B0 | 0.2.x · Revit 2024 · lectura; rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-02,WF-09,WF-13 | CAP-003 | plan → commit (diff, un Deshacer, idempotencia clave) | núcleo | Add-in PlanBuilder/PlanApplier · AgentHost PlanRunner | NATIVE_ACCEPTANCE_PENDING | PlanTests, PlanQueueTests, AgentHostRevitTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (0.8.0 cambió la auditoría del commit) | B0, B7 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-003 | exclusión por documento: lease + fencing | núcleo | AgentHost ExecJournal/Lease | NATIVE_ACCEPTANCE_PENDING | LeaseFencingTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-003 | auditoría durable de cada commit persistente (intención → resultado) | núcleo | Add-in RequestHandler.Commit · Engine AuditLog | NATIVE_ACCEPTANCE_PENDING | AuditLogTests, V080_Nrt07Harness | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (cierra H2 de rc1) | B7 | — |
| WF-13 | CAP-004 | reanudar tras caída; incierto ⇒ observar, nunca repetir | núcleo | AgentHost PlanRunner/RevitBackend | NATIVE_ACCEPTANCE_PENDING | NRT-03/05/06 simulados, V080_Handoff_AfterCrash | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-13 | CAP-004 | respuesta IPC perdida tras commit (NRT-07) | núcleo | AgentHost RevitBackend + Faults | NATIVE_ACCEPTANCE_PENDING | V080_Nrt07Harness_LostCommitResponse | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B7 | — |
| WF-13,WF-14 | CAP-015 | handoff estructurado (confirmadas / inciertas / siguiente paso seguro) | núcleo | AgentHost Handoff | TESTED_SIMULATED | V080_Handoff_AfterCrash_ProposesReconcile | OK | B7 | — |
| WF-13 | CAP-015 | eventos firmados, agenda y notificaciones | núcleo | — | DEFERRED | — | 0.9.0 (eventos mínimos) | — | — |
| WF-13 | CAP-001 | escritura atómica de archivos de estado (temporal residual) | núcleo | Engine Home.WriteAtomic | TESTED_UNIT | DurabilityTests | OK; residuo nativo de rc1 sin recomprobar | B7 | — |
| WF-02,WF-10,WF-11 | CAP-008 | requisitos con procedencia y autoridad; inferencia nunca OBSERVED | núcleo | Engine Workflow.Requirements | TESTED_UNIT | WorkflowTests (9) | OK | B2, B5 | — |
| WF-02,WF-11 | CAP-008 | verificar_independiente: fuente independiente → modelo → diferencias | coordinación | Engine IndependentVerifier · AgentHost RevitBackend | NATIVE_ACCEPTANCE_PENDING | V080_IndependentVerification_AgainstModel | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (sólo lectura) | B2, B5 | — |
| WF-02 | CAP-008 | adaptadores de fuentes (extraer PDF/tablas con coordenadas) | coordinación | — | DEFERRED | — | 0.9.0 | B2 | — |
| WF-04,WF-09,WF-11 | CAP-006 | estados de entrega (12 etapas) y recibos | documentos | Engine Workflow.Delivery | TESTED_UNIT | WorkflowTests | OK | B6 | — |
| WF-02 | CAP-005 | set_param / set_values / fijar_parametro_lote | edición | Add-in PlanApplier · AgentHost | NATIVE_ACCEPTANCE_PENDING | PlanTests, Rc11_SlowIdleRevit, Bench_B0_586 | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B0 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-02 | CAP-005 | change_type / set_type_param / set_level / set_workset / set_elevation | edición | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests, WriteEngineTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| WF-02,WF-05 | CAP-005 | move / rotate / mirror / copy / delete | edición | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.BuildMoved, PlanTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| WF-02 | CAP-005 | place (instancia por punto, montaje losa/nivel) | modelado | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.Parse_Validates | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| — | CAP-005 | muros, suelos, techos, cubiertas, escaleras (creación tipada) | modelado | — | NOT_SUPPORTED | — | sólo por exec (nivel B, código_dinámico) | — | — |
| WF-05 | CAP-010 | group_create / ungroup / group_place | edición | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B3 | — |
| WF-05 | CAP-010 | cobertura de grupos por tipo/planta y propagación | edición | — | DEFERRED | — | 0.9.0 | B3 | — |
| WF-03 | CAP-009 | geo montaje / choques / rangos / cuartos (mallas, rayos, vínculos) | geometría | Add-in GeoRunner · Engine TriMesh | NATIVE_ACCEPTANCE_PENDING | GeoComputeTests, MeshTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | 0.2.x · Revit 2024 · lectura |
| WF-03 | CAP-009 | snap_to_surface (cara inferior real de losa/rampa) | geometría | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | — |
| WF-02 | CAP-009 | registro plano–modelo con anclajes | geometría | — | DEFERRED | — | 0.9.0 | B2 | — |
| WF-01,WF-03 | CAP-005 | view_create (planta, RCP, sección, 3D, dibujo) / view_set (plantilla, rango, detalle) | vistas | Add-in PlanApplier · WriteKit | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests, Draw2DTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B1 | — |
| WF-01,WF-08 | CAP-007 | view_hide / view_override (colores, medio tono, transparencia) | vistas | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B1 | — |
| WF-03,WF-11 | CAP-007 | chk lighting_audit / QC manifiesto por vista | vistas | Add-in Check · Engine QC | NATIVE_ACCEPTANCE_PENDING | QcFixtureTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B2 | 0.2.x · Revit 2024 · lectura |
| WF-06,WF-11 | CAP-007 | show / capture (señalar y capturar) | vistas | Add-in Show/Capture | NATIVE_ACCEPTANCE_PENDING | — | nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | 0.2.x · Revit 2024 · lectura |
| WF-04,WF-09 | CAP-006 | sheet_create / sheet_place | láminas | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B6 | — |
| WF-04,WF-09 | CAP-006 | sheet_set (conjunto de impresión persistente) | láminas | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | SheetSetOpTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (el orden lo decide Revit) | B6 | — |
| WF-04,WF-09 | CAP-006 | doc pdf (exportar hojas o conjunto y verificar archivos) | documentos | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | — | nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (exige permiso exportar) | B6 | — |
| WF-04 | CAP-006 | verificar el PDF exportado (páginas, sha, revisión) | documentos | Engine Delivery (recibos) | TESTED_UNIT | WorkflowTests (página ≠ set, sha ≠ export) | OK; el lector de PDF es 0.9.0 | B6 | — |
| WF-02 | CAP-013 | tag / tag_move | anotaciones | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | PlanTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B3 | — |
| WF-06 | CAP-013 | text_note / filled_region (contorno validado) | anotaciones | Add-in PlanApplier · Engine Draw2D | NATIVE_ACCEPTANCE_PENDING | Draw2DTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10 | CAP-012 | detail_line / detail_rect / detail_polygon / detail_arc (estilos de línea) | anotaciones | Add-in PlanApplier · Engine Draw2D | NATIVE_ACCEPTANCE_PENDING | Draw2DTests (12) | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10 | CAP-012 | detail_component (símbolos y familias de detalle) | anotaciones | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | WriteEngineTests.Catalog | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-06 | CAP-013 | dimension (cota lineal entre líneas de detalle) | anotaciones | Add-in PlanApplier | NATIVE_ACCEPTANCE_PENDING | Draw2DTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B5 | — |
| WF-10,WF-11 | CAP-012 | grafo de riser (nodos/aristas) y auditoría de conectividad | anotaciones | — | DEFERRED | — | 0.9.0 | B5 | — |
| WF-06,WF-08 | CAP-013 | legibilidad en papel (densidad, leaders, colisiones) | anotaciones | Engine Draw2D (papel↔vista, alinear, distribuir) | DEFERRED | Draw2DTests (transformaciones) | base 2D lista; optimización 0.9.0 | — | — |
| WF-12 | CAP-014 | fam info / caras | familias | Add-in FamilyOps | NATIVE_ACCEPTANCE_PENDING | — | nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B8 | 0.5.0 · Revit 2026 · lectura, una muestra |
| WF-12,WF-07 | CAP-014 | fam abrir / conector / param / cargar | familias | Add-in FamilyOps | NATIVE_ACCEPTANCE_PENDING | ScriptingTests (compila por año) | nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (exige permiso modificar_familia) | B8 | — |
| WF-12 | CAP-014 | fam buscar (índice de carpetas autorizadas, sin explorar discos) | familias | Engine FamilyLibrary | TESTED_UNIT | FamilyLibraryTests (3) | OK | B8 | — |
| WF-12 | CAP-014 | fam cargar_archivo (RFA indexado y sin cambios, sha-256) | familias | Engine FamilyLibrary · Add-in | NATIVE_ACCEPTANCE_PENDING | FamilyLibraryTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (exige permiso cargar_archivo) | B8 | — |
| WF-07,WF-08 | CAP-011 | routing físico, fittings, stubs y reconciliación de cotas | MEP | — | DEFERRED | — | 0.9.0 | B4 | — |
| WF-08 | CAP-011 | detección de choques con vínculos | coordinación | Add-in GeoRunner | NATIVE_ACCEPTANCE_PENDING | GeoComputeTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | B4 | 0.2.x · Revit 2024 · lectura |
| WF-04,WF-13 | CAP-004 | doc guardar / guardar_como (verificación de archivo y hash) | documentos | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | NRT-05 simulado | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (exige permiso guardar) | B6 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| WF-04,WF-13 | CAP-004 | doc sync (sólo workshared, permiso sincronizar) | documentos | Add-in DocOps | NATIVE_ACCEPTANCE_PENDING | WorksharedPolicyTests | nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION (nunca sobre centrales corporativas) | B6 | — |
| — | CAP-006 | exportar IFC / DWG / NWC (tipado) | interoperabilidad | — | NOT_SUPPORTED | — | sólo por exec (nivel B) | — | — |
| — | CAP-006 | publicar (ACC, BIM 360, correo) | publicación | — | NOT_SUPPORTED | PermissionsTests (siempre denegado) | no existe operación de publicar | — | — |
| WF-13 | CAP-001 | permisos separados (10 clases) aplicados en el add-in | administración | Protocol Permissions · Add-in | NATIVE_ACCEPTANCE_PENDING | PermissionsTests (3) | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| WF-13 | CAP-005 | exec C# controlado (consulta / ensayo / ejecutar) con revisión y auditoría | administración | Add-in Exec · Scripting | NATIVE_ACCEPTANCE_PENDING | ScriptingTests, TrialGuardTests | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION | — | — |
| — | CAP-005 | interacción supervisada con la interfaz (nivel C) | administración | — | NOT_SUPPORTED | — | sólo show/capture; ningún clic automatizado | — | — |
| WF-13 | CAP-001 | Actualizar desde ZIP (firma, hashes, compatibilidad, aplicar al cerrar Revit, reversión) | administración | Setup.Core ZipUpdate · Setup /aplicar-pendiente · Add-in | NATIVE_ACCEPTANCE_PENDING | ZipUpdateTests (12) | OK sin Revit; nativo: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION; firma: clave pendiente de IT | — | — |
| WF-13 | CAP-001 | instalador con manifiesto SHA-256 y /diagnostico | administración | Setup.Core | TESTED_UNIT | SetupTests | OK | B8 | rc1 14ec21d · Revit 2024 · copia desechable (NRT-03 15/15, NRT-05, NRT-06 11/11) |
| — | CAP-001 | medición de tokens ESTIMADOS y llamadas por cable | administración | Engine TokenEstimate · AgentHost Bench | TESTED_SIMULATED | Bench_B0_586_OneByOne_vs_Plan | OK (1.172 → 3 llamadas del agente, simulado) | B0 | — |

## Por capacidad (CAP)

| CAP | Nombre | Filas | Estados |
|---|---|---|---|
| CAP-001 | Identidad, preflight y capacidades | 7 | TESTED_UNIT 3, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-002 | Consultas tipadas y compactas | 1 | NATIVE_ACCEPTANCE_PENDING 1 |
| CAP-003 | Plan transaccional y exclusión mutua | 3 | NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-004 | Checkpoint, reanudación y sync | 4 | NATIVE_ACCEPTANCE_PENDING 4 |
| CAP-005 | Operaciones reutilizables sobre elementos y vistas | 8 | NATIVE_ACCEPTANCE_PENDING 6, NOT_SUPPORTED 2 |
| CAP-006 | Lámina, set, preset y emisión comprobable | 7 | TESTED_UNIT 2, NATIVE_ACCEPTANCE_PENDING 3, NOT_SUPPORTED 2 |
| CAP-007 | Verificación nativa y visual | 3 | NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-008 | Fuentes, versiones y contraste independiente | 3 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-009 | Registro geométrico y montaje | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| CAP-010 | Grupos, variantes y propagación | 2 | NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-011 | Redes físicas y coordinación | 2 | NATIVE_ACCEPTANCE_PENDING 1, DEFERRED 1 |
| CAP-012 | Grafo y composición de riser 2D | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| CAP-013 | Legibilidad de anotaciones y vistas demostrativas | 4 | NATIVE_ACCEPTANCE_PENDING 3, DEFERRED 1 |
| CAP-014 | Editor de familias y conectores MEP | 4 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 3 |
| CAP-015 | Eventos, handoff y extensiones | 2 | TESTED_SIMULATED 1, DEFERRED 1 |

## Por workflow

| WF | Nombre | Operaciones | Implementadas (sin aceptación nativa de 0.8.0) | Diferidas / no soportadas |
|---|---|---|---|---|
| WF-01 | Configurar plantilla y clasificación de vistas | 2 | 2 | 0 |
| WF-02 | Colocar iluminación a partir de planos | 11 | 9 | 2 |
| WF-03 | Montaje de luces y visibilidad en RCP | 4 | 4 | 0 |
| WF-04 | Emitir set de iluminación y hoja de QC | 7 | 7 | 0 |
| WF-05 | Prototipos de unidad, paneles y grupos | 4 | 3 | 1 |
| WF-06 | Modelar markups interiores y sanear RCP DIM | 4 | 3 | 1 |
| WF-07 | Modelar y auditar dos familias de feeds físicos | 2 | 1 | 1 |
| WF-08 | Ajustar cruces, colores y lámina de coordinación | 4 | 2 | 2 |
| WF-09 | Preparar un set parcial de impresión | 5 | 5 | 0 |
| WF-10 | Construir un riser unifilar 2D | 4 | 3 | 1 |
| WF-11 | Auditar y preparar el riser para emisión | 6 | 5 | 1 |
| WF-12 | Ejercicios de aula y edición de familias | 4 | 4 | 0 |
| WF-13 | Reparar conectores y reanudar trabajo | 16 | 15 | 1 |
| WF-14 | Transferir conocimiento y guiar al usuario | 1 | 1 | 0 |

## Por familia de operaciones de Revit

| Familia | Filas | Estados |
|---|---|---|
| modelado | 2 | NATIVE_ACCEPTANCE_PENDING 1, NOT_SUPPORTED 1 |
| edición | 5 | NATIVE_ACCEPTANCE_PENDING 4, DEFERRED 1 |
| familias | 4 | TESTED_UNIT 1, NATIVE_ACCEPTANCE_PENDING 3 |
| geometría | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| MEP | 1 | DEFERRED 1 |
| vistas | 4 | NATIVE_ACCEPTANCE_PENDING 4 |
| láminas | 2 | NATIVE_ACCEPTANCE_PENDING 2 |
| anotaciones | 7 | NATIVE_ACCEPTANCE_PENDING 5, DEFERRED 2 |
| documentos | 5 | TESTED_UNIT 2, NATIVE_ACCEPTANCE_PENDING 3 |
| interoperabilidad | 1 | NOT_SUPPORTED 1 |
| coordinación | 3 | NATIVE_ACCEPTANCE_PENDING 2, DEFERRED 1 |
| publicación | 1 | NOT_SUPPORTED 1 |
| administración | 6 | TESTED_UNIT 1, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 3, NOT_SUPPORTED 1 |
| núcleo | 12 | TESTED_UNIT 3, TESTED_SIMULATED 1, NATIVE_ACCEPTANCE_PENDING 7, DEFERRED 1 |

*Implementadas* = el código existe en este build (con o sin pruebas sin Revit, ver la columna Prueba); para el usuario sólo cuentan tras la campaña nativa.
