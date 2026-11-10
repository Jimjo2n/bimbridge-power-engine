# PDI.RevitMcp 0.7.0-rc1 — candidato interno · NATIVE_ACCEPTANCE_PENDING

Rama `feature/0.7.0-rc1`, que nace de `v0.7.0-beta1.1` (c2aca78). **No hay tag `v0.7.0-rc1`**: se pone solo cuando la campaña nativa apruebe.
Toda prueba contra Revit queda `BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`.

## Matriz de funcionalidades
| Capacidad | Operación IR / comando | Riesgo | Validación sin Revit | Validación nativa |
|---|---|---|---|---|
| Consulta tipada con conteo | `consultar` | Lectura | FakeBim | NRT-06 (pendiente) |
| Lectura paginada del alcance (hasta 50×200) con detección de cambios entre páginas | interna de RevitBackend | Lectura | FakeBim (2 por página, total que cambia) | NRT-06 (pendiente) |
| Escritura de un parámetro (texto) | `fijar_parametro` | Escritura persistente | FakeBim + simulador | NRT-05 run2 APROBADA (beta1.1); regresión pendiente |
| **Escritura por lotes** (valor distinto por id, 1 plan y 1 commit, ≤200) | `fijar_parametro_lote` | Escritura persistente | FakeBim, simulador, muerte del proceso en 2 puntos | NRT-06 (pendiente) |
| **QC de luminarias y etiquetas** (sin tag + huérfanas + duplicadas sobrantes = 0) | `verificar_qc receta=qc_luminarias_tags` | Lectura (`check lighting_audit` existente) | FakeBim (4 casos) | NRT-06 (pendiente) |
| Plan multi-operación con dependencias y postcondiciones por operación | PlanRunner (existente) | — | FakeBim + simulador | NRT-06 (pendiente) |
| Autorización por documento y operación | GrantStore + `RealWrites` del operador + huella del documento | — | FakeBim | NRT-06 (pendiente) |
| Cancelación, pausa y reanudación | PlanRunner/servicio (existente) | — | pruebas existentes | — |
| Reconciliación de resultados inciertos | Observe → `requiere_verificacion`/`requiere_reconciliar` | — | simulador + FakeBim | NRT-03 (pendiente) |
| Idempotencia tras reiniciar el AgentHost | diario + clave `opId#fencing` + diario del add-in | — | 5 puntos de caída (FakeBim), procesos reales (simulador) | NRT-03 (pendiente) |
| Persistencia en disco | `doc verificar_disco` con copia estable | Lectura de archivo | archivos sintéticos con modificación concurrente | NRT-05 (pendiente de regresión) |

**Existen en el add-in pero el AgentHost no las ejecuta en rc1:** etiquetado tipado, `parametro_por_lote` y `qc_manifiesto` (recetas del motor de tareas), y la exportación (`doc pdf`). `RevitTaskAdapter` las mapea de forma determinista y se niega a ejecutarlas. Conectarlas exige ampliar contratos con validación nativa inmediata, así que pasan a 0.7.0 estable o 0.8.0 según la campaña. `fijar_parametro_lote` cubre en IR el caso de `parametro_por_lote` para parámetros de texto.

**No incluido:** ejecución arbitraria de C# como sustituto de lo tipado, un segundo motor de transacciones, Python y Rust.

## Comandos nuevos
- `agenthost revit nrt03 | nrt06`: arneses de campaña, ver `CAMPANA_NATIVA_rc1.md`.
- Compuerta: todo `agenthost revit …` y `ejecutar --revit-pid` exigen `PDI_REVIT_NATIVO=AUTORIZADO` **y** `--revit-disponible`. Sin ambas, no conectan e imprimen el estado BLOCKED.
