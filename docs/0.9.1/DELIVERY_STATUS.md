# BIMBRIDGE 0.9.1 — estado de entrega (rama `feat/revit-functional-coverage-0.9.1`)

Estado: **en curso, sin campaña nativa**. Nada es NATIVE_VERIFIED. La versión del producto sigue en 0.9.0+commit (#23: no subir versión sin compuerta de aceptación).

## Hecho
| Bloque | Commit | Contenido | Pruebas offline |
|---|---|---|---|
| A | cf5acde, d837a7d, 9870f72, f70ed73 | AUD-01/01B auditoría (cabeza por lotes, reclamos durables, reparación de cola), AUD-02/03 instantáneas, AUD-04 #28, AUD-05 caché de compilación | AuditRecoveryTests, QuerySnapshotTests, AgentHostProcessTests, CompileCacheTests |
| B | 5143dc1 | Inventario offline API 2022–2026; catálogo F01–F37 | CapabilityCatalogTests |
| C/D/E | 990ae2a | level/grid/wall/floor/ceiling/room/duct/pipe/circuit/schedule_create con `verificacion_creacion` | ModelOpsTests |
| F | 1948fda, 551f0c2 | Recibos por evidencia, capacidades nativas, verificador Copy/Monitor, `check copy_monitor`, destino estable, diagnóstico (#23–#25) | NativeWorkflowTests |
| G | 03f6d15 | `doc ifc` (transacción revertida) y `doc tabla_csv` | (sólo compilación 2022–2026) |

## Decisiones
- **D54** — Operaciones de modelado: validación offline en `Engine.ModelOps`; resolución, guardas y creación en clases parciales `PlanBuilder.Model`/`PlanApplier.Model`; relectura de lo creado frente a lo pedido.
- **D55** — Flujos nativos: el estado sólo avanza con evidencia; PostCommand aceptado ≠ terminado; lo incierto exige reconciliación; clave de intento idempotente y ligada al documento.
- **D56** — Copy/Monitor: sin API pública de copia por lotes (inventario). Producto = preflight + traspaso manual veraz + verificación; correspondencia origen→copia declarada como inferida.
- **D57** — Exportación IFC dentro de una transacción siempre revertida.

## Pendiente
- Bloques H/I (QA/QC interactivo, PDF→Revit ejecutado, panel #5, scheduler): **PARCIAL** (lo existente de 0.9.0).
- Bloque J: CHANGELOG final e informe completo.
- Build completo (`build_gh`, incluye Rust/Python/instalador), CI, SHA del instalador, PR 0.9.1 sin merge.
- Campaña nativa única: requiere autorización explícita de instancia y modelo de prueba desechable.
- Pruebas que fallan sólo sin build completo: PowerEngineGeometryTests (biblioteca Rust no compilada en la ejecución parcial).
