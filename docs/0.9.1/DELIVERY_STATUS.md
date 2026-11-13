# BIMBRIDGE 0.9.1 — estado de entrega (rama `feat/revit-functional-coverage-0.9.1`, PR #29)

Estado: **candidato a decisión de merge, sin campaña nativa**. Nada es NATIVE_VERIFIED. La versión del producto sigue en 0.9.0+commit (#23).
Las cifras de este documento salen de `docs/0.9.1/capabilities/coverage.md` y `docs/0.9.1/inventory/revit-symbols-summary.json`, regenerados desde el código en este commit.

## Inventario (fuente: registro generado)
- **Símbolos públicos de la API** (RevitAPI + RevitAPIUI, Nice3point 2022.1.80 … 2026.4.10): 31.592 / 31.924 / 32.621 / 33.118 / 33.873 por versión, listados gzip deterministas con SHA-256 del ensamblado y del listado (`docs/0.9.1/inventory/revit-symbols-*`). Presencia técnica, sin semántica.
- **Capacidades: 223**. OFFLINE_VERIFIED 13, IMPLEMENTED_UNVERIFIED 72, DYNAMIC_CSHARP_ONLY 92, MISSING 7, UNKNOWN 32, UNSUPPORTED_BY_PUBLIC_API 2, NOT_APPLICABLE 5.
- **Comandos de la cinta: 596**, todos enlazados. Por estado: DYNAMIC_CSHARP_ONLY 256, IMPLEMENTED_UNVERIFIED 91, **UNKNOWN 189** (analizados, con investigación pendiente en `commands.json`), NOT_APPLICABLE 48, UNSUPPORTED_BY_PUBLIC_API 4, MISSING 8. DYNAMIC_CSHARP_ONLY = hay API pública verificada, no automatización completa.
- **Descubrimiento**: `check name=cobertura` (filas compactas paginadas; contrato por id). Benchmarks del índice con el catálogo real de 223: `docs/0.9.1/benchmarks/inventory-bench.md`.

## Funcionalidad de la rama (estado real)
| Pieza | Estado |
|---|---|
| Operaciones tipadas de modelado (nivel, rejilla, muro, suelo, techo, habitación, conducto, tubería, circuito, tabla) con verificación de lo creado | implementadas; 44 operaciones en el catálogo; efecto en Revit pendiente de campaña |
| Copy/Monitor: preflight, traspaso manual, verificación | implementado de sólo lectura; **expectativas congeladas** por intento (AUD-20); correspondencia origen→copia **inferida**, no identidad; sin ejecutor de UI |
| Recibos de flujos nativos | implementados con bloqueo entre procesos y evidencia privilegiada |
| Exportación `doc ifc`, `doc tabla_csv` | implementadas, pasan por el planificador |
| `model_health`, `historial`, `cobertura` | implementados (sólo lectura / memoria) |
| Planificador de operaciones largas | implementado; gobierna operaciones de documento; **no** gobierna commit/exec (ver PERFORMANCE_HARDENING §G) |
| Panel de estado #5 | sección «Operaciones largas» con filtro y Cancelar; ventana WPF sin prueba automática |
| PDF→Revit | plan → planes tipados con testigo de origen; **idempotencia efectiva** (AUD-19); la aplicación en Revit queda para la campaña nativa |
| Actualización en línea (solicitada por Abel) | implementada contra Releases públicas con verificación SHA-256; **sin prueba con una Release real**: el repositorio no tiene Releases |

## Rondas de auditoría
- **Segunda (AUD-12…AUD-17)**: ver historial del PR (inventario por intento, aislamiento del historial, huella canónica, benchmarks, `model_health` troceado).
- **Final (AUD-18…AUD-21)**:
  - AUD-18: `JobStore.Complete` decide `con_diferencias` antes de notificar; respuesta diferida, consulta del `job_id` e historial coinciden; consejo `revisar_diferencias`. `OperationHistoryTests.JobFinishingWithDifferences_*`.
  - AUD-19: `SourceBinding` — cada operación de una fuente lleva un testigo de origen (SHA-256 de la fuente, revisión, registro geométrico, huella del plan, lote); el motor deriva clave y huella de contenido al planificar y las aplica en el commit sobre `OperationJournal`, aunque no se pase `clave=`; otra `clave=` se rechaza; `commit ops=` con origen se rechaza. `SourceBindingTests` (intentos idénticos, reconexión, plan_id nuevo, mismo PDF otro destino/copia, PDF modificado, commit interrumpido, resultado incierto, reconciliación).
  - AUD-20: el preflight de Copy/Monitor congela anfitrión, vínculo y revisión, elementos de origen, transformación, mapeo, exclusiones y categoría (huella de expectativas); verificar usa lo congelado y cualquier cambio del objetivo es conflicto. `NativeWorkflowTests` (congelación, conflictos, manipulación).
  - AUD-21: este documento, el CHANGELOG, la matriz y el registro regenerados desde la fuente (223).
  - Rendimiento: `docs/0.9.1/PERFORMANCE_HARDENING.md` (perfil de `RunMain`, cachés, métricas, harness `tools/bench/WorkflowBench`).

## Decisiones (D54–D66)
D54 operaciones de modelado con verificación de lo creado · D55 flujos nativos por evidencia · D56 Copy/Monitor sin API de copia por lotes · D57 IFC con transacción revertida · D58 inventario completo en gzip determinista · D59 índice inmutable y descubrimiento paginado · D60 UNKNOWN con investigación pendiente, sin promociones por nombre · D61 recibos con bloqueo por intento y evidencia privilegiada · D62 historial con consejo de reintento y ámbito por agente ·
D63 **estado final del trabajo se decide en `JobStore.Complete`** (antes de notificar) · D64 **idempotencia de PDF→Revit sobre el diario de operaciones existente**, con testigo de origen en las operaciones (sin segundo sistema) · D65 **expectativas de Copy/Monitor congeladas por intento** y conflicto ante cambios del objetivo · D66 **rendimiento: reducir accesos al hilo de Revit con cachés por invocación** (no globales, no cruzan documento ni revisión) y medir offline antes de afirmar.

## Pendiente (explícito)
- 189 comandos UNKNOWN; operaciones que requieren UI (Copy/Monitor completo, Coordination Review); elementos sin verificación nativa (todos).
- Benchmarks reales frente a pyRevit: requieren Revit (campaña nativa, tras la decisión de merge).
- Publicación de la 0.9.1 y prueba de la actualización en línea con una Release real.
- Campaña nativa única: requiere autorización de instancia y modelo desechable.
