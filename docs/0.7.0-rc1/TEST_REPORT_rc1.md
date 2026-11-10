# Informe de pruebas rc1 — NATIVE_ACCEPTANCE_PENDING

## Automatizadas (sin Revit)
- **Suite: 389 pruebas** (367 de beta1.1 + 22 de rc1). Se hicieron 6 corridas completas tras los cambios: 4 limpias (389/389) y 2 con un fallo intermitente cada una (detalle abajo).
- **Pruebas nuevas de rc1:**
  - fail-closed con 5 banderas de context ausentes;
  - lote con un valor por elemento y 1 commit;
  - el validador del lote (ids desparejos, claves no numéricas, más de 200, valor vacío);
  - un commit que dice ok y no aplica nunca queda verificado ni se reintenta;
  - lectura paginada y cambio del modelo entre páginas detectado antes de escribir;
  - QC de luminarias y etiquetas (4 casos);
  - NRT-05 se detiene antes de guardar ante cualquier comprobación previa negativa;
  - compuerta nativa;
  - copia estable con modificación concurrente;
  - lote con muerte real del proceso en 2 puntos;
  - arneses NRT-03 y NRT-06 contra el FakeBim, y sus bloqueos;
  - banco de lotes.
- **Separación de fallos reales y de temporización:**
  - **Real, corregido:** el arnés NRT-03 desechaba la conexión compartida al cerrar el backend de cada ronda. Una ronda veía `revit_no_disponible`. En la campaña nativa habría producido un falso FALLIDA.
  - **De temporización, sin cambio:** `Service_ProcessKilled_NewServiceProcessRecoversWithoutDuplicates` y `HeldCommit_WaitsForUser_ThenApplies_AndDetectsConflicts`. Son pruebas antiguas y el código que ejercen no cambió. Fallaron una vez cada una, solo con la suite completa en paralelo, y pasaron 6 de 6 corridas aisladas. Son timeouts por contención de CPU en un contenedor de 4 núcleos.

## Escenarios NRT-03 en simulación (contrato de estados de ejecución)
| # | Escenario | Prueba |
|---|---|---|
| 1 | Interrupción antes del envío | NRT-03 `backend.antes_enviar` (FakeBim y arnés) |
| 2 | Interrupción durante la solicitud | AH-01 `backend.mitad_tx`, incluido el lote (procesos reales) |
| 3 | Respuesta perdida tras un posible commit | `PersistentCommit_TimesOutAfterApplying_IsObserved_NotRepeated` y `backend.tras_commit` |
| 4 | Caída antes de registrar el resultado | `runner.tras_ejecutar` y AH-01 |
| 5 | Caída durante la observación o verificación | `runner.en_verificacion` |
| 6 | Reanudación posterior del AgentHost | las 5 rondas y AH-02 (muerte real del proceso) |
| 7 | Solicitudes repetidas con la misma clave | diario del add-in (pruebas existentes) y reanudación del mismo diario (0 commits) |
| 8 | Operaciones concurrentes sobre un documento | AH-04 (dos procesos, un solo ejecutor, fencing) |

Resultados desconocidos: `requiere_verificacion` o `requiere_reconciliar`; nunca se reintentan a ciegas.

## Nativas
| Prueba | Estado |
|---|---|
| NRT-03 nativa | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| NRT-06 nativa | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| Regresión de NRT-05 con rc1 | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
| Capacidades nuevas con la Revit API real (lote, QC, paginación, verificar_disco estable) | BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION |
