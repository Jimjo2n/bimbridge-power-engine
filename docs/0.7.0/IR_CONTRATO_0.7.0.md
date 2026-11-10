# Contrato IR (esquema 2) — 0.7.0-alpha1

Ejemplo real generado por `agenthost documento evidencia/especificacion_ejemplo.txt` → `evidencia/plan_desde_documento.json` (log en `evidencia/documento_pipeline.log`).

## Plan (entrada no confiable)
| Campo | Obligatorio | Notas |
|---|---|---|
| `ir` | sí | `2` |
| `objetivo` | sí | texto |
| `documento.clave`, `documento.huella` | sí | deben coincidir con el modelo; si no ⇒ `documento_distinto` |
| `unidades.longitud` | sí | `mm`, `m`, `ft`, `in` |
| `coordenadas` | sí | `interno_revit` / `compartidas` |
| `fuentes[]` | si hay `fuente` en ops | `id`, `nombre`, `sha256` |
| `operaciones[]` | sí | ver abajo |

## Operación
`id` (lo calcula el sistema), `operacion` (catálogo), `alcance`, `parametros`, `depende_de[]` (ids o `#n`), `precondiciones[]` (`objetivos` min/max), `resultado_esperado`, `permitir_vacio`, `reintentos` (0–3), `fuente` {fuente, pagina, seccion, requisito, cita}.
**No declarables** (rechazados con `campo_no_declarable`): `autorizacion`, `postcondiciones`, `idempotencia`, `estado`.

## Catálogo
| operación | escribe | parámetros | alcance | autorización derivada | postcondición |
|---|---|---|---|---|---|
| consultar | no | categoria | — | — | elementos |
| fijar_parametro | sí, convergente | parametro, valor, unidades, tolerancia | categoria, nivel | escritura_modelo | todos los objetivos con el valor ± tolerancia |
| verificar_qc | no | receta (qc_manifiesto, montaje_soportes) | nivel | — | desviaciones = 0 |

## Estado de ejecución (mutable, en `diario.jsonl`)
Estados: validada, en_ejecucion, ejecutada, verificada, fallida, requiere_verificacion, requiere_reconciliar, bloqueada, cancelada.
Clases de error: precondicion, sin_objetivos, rechazo_backend, transitorio, verificacion, efecto_parcial, efecto_incierto, evidencia_insuficiente, dependencia, cancelacion, documento_distinto, estado_corrupto, plan_en_uso, interno.

## Códigos de salida CLI
0 todo verificado · 1 error · 2 hay excepciones · 3 plan en uso · 4 lease perdido · 5 pausado · 64 uso.
