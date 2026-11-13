# 0.9.1 — endurecimiento de rendimiento (ronda final)

Todo lo medido aquí es **offline** (fuera del hilo de Revit, `tools/bench/WorkflowBench`). Lo que se espera en Revit se marca **hipótesis**: no se ha ejecutado Revit.
Cifras de este equipo (4 CPU, otra carga en paralelo): son orientativas, no resultados de rendimiento frente a pyRevit.

## A. Consumidores de `RunMain()` (perfil estático)

| Ruta | Visitas al hilo | Coste dominante en Revit | Frecuencia | ¿Optimizable? | Riesgo | Estado |
|---|---|---|---|---|---|---|
| `query` (QueryRunner) | 1 + tramos | `doc.GetElement` por candidato; lecturas de parámetros; tipo y nivel por instancia | muy alta | sí: caché por id de tipo/nivel/destino y clase de dato | bajo (vive una consulta) | **hecho** |
| `check lighting_audit` | 1 + tramos | nivel y workset por luminaria; nombre/nivel de vista por etiqueta | media | sí: caché por id | bajo | **hecho** |
| `check model_health` | 1 por fase | `GetWarnings`, tipo por instancia, un colector por lámina | media | sí: tipo/nivel por id, tablas por lámina en una pasada, fases troceadas | bajo | **hecho** (AUD-17 + esta ronda) |
| `check copy_monitor` | 1 | colectores de origen y anfitrión | baja (por intento) | sí en parte: ya una sola visita | bajo | sin cambio (ya 1 visita) |
| `geo` (GeoRunner) | 1–2 + tramos | geometría y mallas | media | ya tiene caché por revisión y mallas reutilizadas | — | sin cambio |
| `plan` sin geometría | 1 | resolución de tipos/niveles (`ModelIndex` cacheado por planificación) | alta | ya en una visita | — | sin cambio |
| `commit` | 1 grupo + lotes | transacciones por lote; `Regenerate` | alta | ver D | alto (integridad) | sin cambio (ver D) |
| `doc pdf/ifc/csv/guardar/sync` | 1 + espera | E/S y exportación | baja | no: trabajo de Revit | — | ahora pasan por el planificador |
| `exec` | 1 | compilación y ejecución | media | caché de compilación ya existente | — | sin cambio |

Lo que NO se tocó, y por qué: `commit`/`exec` siguen en la cola propia del hilo de Revit (no se ha demostrado que el planificador preserve el orden de escrituras y los locks existentes; ver §G).

## B. Cambios por ruta (cuello → cambio → riesgo → medición → impacto esperado)

| Archivo / método | Cuello | Cambio | Riesgo | Medición offline | Impacto en Revit |
|---|---|---|---|---|---|
| `ElementFields.ParamValue/ReadOne` | `GetDataType()` por valor; `doc.GetElement` del tipo por instancia en parámetros de tipo; nombre de destino repetido | caché por invocación: clase de dato por `Definition`, elemento de tipo por id, destino por id; contadores de aciertos/fallos | bajo: vida de una consulta, dentro del hilo de Revit, no cruza documentos ni revisiones | no medible fuera de Revit (necesita la API) | **hipótesis**: menos llamadas `GetElement`/`GetDataType` proporcionales a instancias/tipos; medir en BM-01/BM-02 |
| `ModelHealthProbe` (fase 1) | hasta 3 `GetElement(typeId)` por instancia + 1 por parámetro requerido no de instancia + nivel por instancia | `TypeMeta` y `LevelName` por id; valores de parámetros de tipo resueltos una vez por tipo | bajo | no medible fuera de Revit | **hipótesis**: de O(instancias) a O(tipos) en accesos de tipo |
| `LightingAuditRunner` | nivel y workset por luminaria; vista por etiqueta | caché de nivel (NameCache), workset por id, vista por id | bajo | no medible fuera de Revit | **hipótesis**: igual |
| `ModelHealth.Duplicates` | `List.Contains` cuadrático en grupos muy densos; coordenadas no finitas/enormes desbordaban la cuantización | `HashSet` de pertenencia; exclusión de no finitos y \|x\|≥1e9 | bajo (pruebas) | grupo de 20.000 coincidentes: **42,6 ms → 4,7 ms (9,0×)**; modelos dispersos de 10k–50k: sin cambio (0,9–1,0×, dentro del ruido) | ninguno en Revit (corre fuera del hilo) |
| `JobStore.Complete` / `DocOp` | — (AUD-18, corrección, no optimización) | estado final decidido antes de notificar | bajo | `OperationHistoryTests` | — |
| `OperationScheduler` en `DocOp` | operaciones de documento sin orden entre agentes | exclusión por documento y reparto justo (ver BM-09) | medio: no toca commit/exec | BM-09 | **hipótesis** |

Verificado y **sin cambio** a propósito:
- `Document.Regenerate()` (3 llamadas en `PlanApplier`): sólo tras `FamilySymbol.Activate()`, condicionadas a `!IsActive`; Revit exige la regeneración antes de colocar. No son redundantes; no se suprimen.
- Transacciones de `PlanApplier`: una por operación (o por sub-lote de N cambios) dentro de UN `TransactionGroup` asimilado; ya están por lote. Fusionar más rompería la recuperación por lote (checkpoint de ids antes de cada lote). No se toca.
- `fsync` de la auditoría y checkpoints: no se reducen (garantías de durabilidad).

## C. Caché y presupuesto

| Caché | Clave | Alcance | Invalidación | Presupuesto | Métricas |
|---|---|---|---|---|---|
| `ElementFields.NameCache` (nuevo: tipo, destino, clase de dato) | id de tipo / id de destino / `Definition` | una consulta, hilo de Revit | muere con la consulta (revisión y documento fijos durante ella) | acotado por nº de tipos/definiciones distintos | `Hits`/`Misses` observados → `metricas` |
| `ModelHealthProbe` tipos y niveles (nuevo) | id de tipo / nivel | una revisión | muere con el probe | nº de tipos y niveles | `CacheHits`/`CacheMisses` |
| `QuerySnapshotStore` | sesión + documento + revisión | sesión | revisión, TTL 5 min, cierre | 64 MB global / 32 MB documento / 4 por sesión | evicciones con motivo |
| `ResultCache` (geo) | doc + revisión + firma | proceso | revisión | 48 entradas | aciertos/fallos |
| `CompileCache` | hash de código + referencias | proceso | no se descargan ensamblados | 1024 entradas, por agente | por agente |
| `CapabilityIndex` | id/dominio/… | proceso, inmutable | huella de contenido | catálogo | — |

Ninguna caché nueva es global ni conserva objetos de Revit entre consultas.

## D. Verificación y escrituras
`Verify()` ya relee sólo lo planificado (entradas del plan) y los creados por id; `VerifyModelCreations` relee cada creado una vez. No se reduce a «existe». Sin cambio.

## E. Instrumentación (`WorkflowMetrics`)
Nombres oficiales: `total_ms, queue_ms, revit_main_thread_ms, revit_slices, revit_max_slice_ms, api_calls, elements_read, elements_written, transactions, regenerations, serialization_ms, payload_bytes, worker_ms, cache_hits, cache_misses, verification_ms, audit_ms, allocations_bytes`.
Hoy **observadas**: `total_ms, queue_ms, revit_main_thread_ms, revit_slices, revit_max_slice_ms` (de `MainThread`), `elements_read`, `elements_written`, `transactions` (0 en lecturas), `cache_hits/misses` (de las cachés nuevas). Se devuelven en `metricas` con `check name=model_health metricas=true`, `query metricas=true` (o `timing=true`) y `lighting_audit`.
**No instrumentadas** (se listan como tales en la respuesta): `api_calls` (sin contador completo: no se declara parcial), `regenerations`, `serialization_ms`, `payload_bytes` en el add-in, `worker_ms`, `verification_ms`, `audit_ms`, `allocations_bytes`. `payload_bytes` y `serialization_ms` sí se miden en el harness offline.

## F. Benchmark readiness
- Harness offline: `dotnet tools/bench/WorkflowBench/bin/Release/net8.0/WorkflowBench.dll docs/0.9.1/benchmarks/workflow-bench.md docs/0.9.1/benchmarks/workflow-bench.json 7`. Resultados en esos archivos (mediana, p95, throughput, asignación; antes/después).
- Escenarios medidos offline: BM-03 (informe y duplicados), BM-06 (validación de N operaciones frente a una compuesta: **sin ganancia offline**; la ganancia esperada es en visitas al hilo y transacciones, no medible aquí), BM-07 (verificación del fixture de 88 aparatos y de 10.000), BM-09 (reparto del planificador con 1/2/4/8 agentes).
- Escenarios que requieren Revit (BM-01, 02, 04, 05, 08, 10 y la parte nativa de 03, 06, 07, 09): especificados en `workloads_nativos` del JSON con tamaños, entrada común y criterio de corrección.
- Procedimiento de comparación con pyRevit (campaña nativa): mismo Revit, modelo, versión y máquina; mismo número de operaciones y verificación funcional (relectura idéntica); separar (1) tiempo del workflow dentro de Revit, (2) tiempo total con la comunicación MCP, (3) trabajo directo por API, (4) interacción humana y (5) instrumentación propia; no atribuir latencia de LLM a pyRevit; baseline de pyRevit escrito de forma razonable (lectura por lote, una transacción por lote). Informar mediana, p95, memoria pico, throughput, speedup, corrección, regresiones y diferencias de funcionalidad. Los objetivos 2–5× son **hipótesis**.

## G. Scheduler
El planificador sólo gobierna operaciones de documento. Para ampliarlo a `commit`/`exec` habría que demostrar que respeta el orden de escrituras, las políticas de transacción y los locks (`GuardDocLock`, `BeginWrite` del `PlanStore`, cola de `MainThread`); sin esa demostración no se amplía. Orden de locks actual: scheduler (clave exclusiva `doc:<id>`) → hilo de Revit → bloqueo del documento; ninguna ruta los toma en orden inverso. Tiempos: BM-09 muestra que 2–8 agentes con 5 ms sintéticos terminan en ≈341 ms frente a ≈499 ms con 1 (límite por agente 2; global 4).
Pendiente: tiempos separados de espera del scheduler, espera del hilo, ejecución, verificación y persistencia (hoy `queue_ms` es la espera del hilo; la del scheduler no se registra aún).
