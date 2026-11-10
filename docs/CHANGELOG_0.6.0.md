# Cambios 0.6.0 (desde 0.5.0 c87e162)

Columna **comprobado**: `prueba` = prueba automática sin Revit (motor real con modelo falso o lógica pura); `compila` = compila contra la API de 2022–2026; `nativo` = ejecutado dentro de Revit. Nada de 0.6.0 es `nativo` todavía.

## A — Identidad, estados, diario (las 5 regresiones de la revisión técnica)

| Cambio | Comprobado |
|---|---|
| El modo de escritura aceptado (`escribir`, ` ejecutar `, alias con espacios) nunca se clasifica como lectura; modo desconocido → `invalido`, nunca se reinterpreta | prueba (TransportRegressionTests + IdentityJournalTests) |
| Una respuesta `en_proceso` mantiene la operación viva en el diario (no se da por terminada) | prueba |
| `reconciliar` no acepta como "ya aplicado" un contenido distinto con la misma clave | prueba |
| Error parcial de commit exige reconciliar antes de reintentar; `revertido` confirmado permite reintentar | prueba |
| Hash de operación incluye el código resuelto (no sólo el nombre del archivo) y es independiente del orden de argumentos | prueba |
| Resolutor de documento único (handle → ruta exacta → ruta normalizada → clave → título); título ambiguo → `ambiguo` sin elegir | prueba (`DocResolver`) |
| Documentos sin guardar de dos instancias nunca comparten clave; archivo reemplazado en la misma ruta se detecta; Guardar como lleva alias con las operaciones completadas | prueba |
| Diario: línea rota recuperada; dos procesos ven el mismo diario; diario no escribible → `diario_no_disponible` visible | prueba |

## B — Motor de tareas reanudable

| Cambio | Comprobado |
|---|---|
| Operación `tarea` (iniciar, estado, resumen, esperar, unidades, reanudar, reconciliar, cancelar, anotar, olvidar, lista, recetas) en ambos perfiles | prueba (transporte de extremo a extremo con Revit simulado) |
| Unidades con clave estable, lotes adaptativos que ceden Revit entre visitas, checkpoint durable por lote | prueba (1 000 unidades, sin duplicados) |
| Caída entre commit y diario → se verifica (UniqueId creados / Verificar / inferencia segura por generación y guardado), no se repite | prueba (inyección de fallos) |
| Reinicio de Revit antes/después de guardar: lo no guardado se rehace, lo guardado no | prueba |
| Barreras guardar/sync/pdf sólo con todo lo anterior confirmado; evidencia de archivo (hash, bytes, fecha) | prueba (motor) / compila (backend Revit) |
| Pausa (Retener), documento cerrado o Revit ocupado → `esperando_contexto`, continúa sola | prueba |
| Conflicto (otro cambió los elementos) → unidad `conflicto`, no se pisa; cancelación cooperativa | prueba |
| Mismo id dos veces → no arranca un segundo ejecutor; otro contenido → rechazado | prueba |
| Backend real (`RevitTaskBackend`): transacción por unidad con registro de avisos, resultado serializado antes del commit, bloqueo del documento por tarea, rekey en Guardar como, persistencia por generación | compila |

## C — Ejecutor general, recetas, memoria entre clientes

| Cambio | Comprobado |
|---|---|
| Recetas por archivo (`%LOCALAPPDATA%\PDI.RevitMcp\recetas`, `recetas\` del add-in) con versión y SHA-256; la tarea guarda copia exacta | prueba |
| `exec`/`commit`: `archivo=` (ruta completa, `%VARIABLES%`), `refs=`, `docs=`, `reconciliar=` llegan hasta el handler | prueba (transporte) |
| Resumen compacto de tarea: 1 000 unidades → 996 caracteres, idéntico para otro cliente desde el disco | prueba (medición) |
| Métricas del puente por cliente/herramienta (caracteres, no tokens) | prueba |
| Guías `tareas` y `recetas` | prueba |

## D — Ayudas y QC

| Cambio | Comprobado |
|---|---|
| `ManifestQc`: uno a uno, ambigüedad no aprobada, faltantes/sobrantes/casi duplicados, giro con simetría, etiquetas, grupo, montaje, no confirmados | prueba (fixture de aceptación D con desviaciones conocidas) |
| `Xform`: rotación, espejo, traslación, inversa (vínculos y grupos) | prueba |
| `VisibilityQc`: oculto, categoría, filtro, workset, fase, opción, recorte, rango (planta/RCP), qué no es comprobable | prueba |
| `Bim.Grupos/Montaje/Vistas/Manifiesto` sobre la API (rayos con vínculos, rango de vista, por qué no se ve, cambio de tipo de grupo con verificación) | compila |
| 5 recetas incluidas: etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas | compila (los 5 años) |
| `fam abrir` sólo reutiliza un RFA abierto desde ese proyecto y esa familia | compila |
| PDF: `paginas_metodo: orientativo` | compila |

## E — Instalador, matriz, arnés, benchmark

| Cambio | Comprobado |
|---|---|
| `manifest.json` con SHA-256 de cada archivo en el paquete; el instalador verifica antes (no instala nada si no coincide) y después de copiar | prueba |
| `Setup.exe /diagnostico`: instalada, cargada en cada Revit abierto, puente, hashes, reinicio pendiente, config | prueba |
| Instalación silenciosa sin `revit=`/`niveles=` conserva la elección y permisos anteriores (modo directo); un MCP apagado sigue apagado | prueba |
| Actualización con archivos en uso: dice que entra al reiniciar (no finge) | compila |
| Matriz de capacidades con niveles de evidencia (compila / prueba / simulado / nativo) | prueba (se genera desde el código) |
| Arnés de aceptación nativa 2024/2026 (`tools/aceptacion/PROCEDIMIENTO_0.6.0.md`, scripts de manifiesto y perturbación, `recoger_evidencia.ps1`) | compila (scripts) |
| Benchmark equivalente MCP/pyRevit en 3 cargas (`tools/bench_060`) + resumen p50/p95 | compila (lado MCP); pendiente de ejecutar |
| Versión 0.6.0 | — |
