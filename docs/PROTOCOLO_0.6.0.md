# Protocolo PDI Revit MCP 0.6.0

Versión de cable: **2** (sin cambios de formato respecto a 0.5.0; 0.6.0 añade la operación `tarea` y campos nuevos, todos opcionales).
Un cliente 0.5.0 sigue funcionando: ninguna operación existente cambió de nombre ni de significado.

## Capas

| Capa | Qué es | Dónde |
|---|---|---|
| IA ↔ puente | MCP por stdio. Perfil **compacto** (por defecto, 5 herramientas: `revit`, `plan`, `commit`, `exec`, `act`) o **completo** (20). | `PDI.RevitMcp.Bridge.exe` |
| Puente ↔ add-in | JSON por línea en una tubería con nombre local por instancia de Revit (`instances\<pid>.json` la anuncia). | `PDI.RevitMcp.Protocol` |
| Add-in ↔ Revit | Hilo principal de Revit (ExternalEvent + Idling por tramos con presupuesto). | `PDI.RevitMcp.Addin` |

## Petición y respuesta

Petición: `{"id":n,"op":"<op>","args":{…},"doc":"dN|ruta|título","clave":"…","tarea":"…","cliente":"claude|codex|…"}`.
Respuesta: `{"id":n,"estado":"<estado>","datos":{…},"aviso":"…","rev":n,"ms":n}`.

Estados de respuesta (0.6.0 añade los cuatro últimos de la segunda fila):

`ok, en_cola, en_proceso, retenido, conflicto, revit_ocupado, revertido, vencido, rechazado, invalido, revit_no_disponible, incompatible, error, tiempo_agotado, no_soportado, destino_no_disponible, necesita_usuario, interrumpido, completado_con_diferencias, parcial, desconocido, aplicado_error_resultado, ambiguo, documento_ocupado`
`cancelacion_solicitada, cancelado, esperando_contexto, diario_no_disponible`

Reglas que no cambian por perfil ni por cliente:
- **Documento destino**: se resuelve una vez (handle `dN` → ruta exacta → ruta normalizada → clave → título) y queda fijado en todos los tramos. Un título ambiguo devuelve `ambiguo` con la lista de candidatos y no elige ninguno. El documento activo de la interfaz nunca sustituye al destino.
- **Escritura** sólo en modo En vivo y nivel W (o P para plan); el botón Retener pausa: `retenido` o, en tareas, `esperando_contexto`.
- **Idempotencia**: `clave=` repetida con el mismo contenido devuelve el resultado anterior; con contenido distinto, `conflicto`. `parcial`/`desconocido`/`interrumpido` exigen `reconciliar=si|no` antes de repetir.
- **Diario** de operaciones durable (`operaciones\*.jsonl`, sólo anexar, línea rota recuperada). Si no se puede escribir: `diario_no_disponible` (nunca silencioso).

## Operación `tarea` (nueva en 0.6.0)

Perfil compacto: `act "tarea <verbo> …"` para escribir y `revit "tarea <verbo> …"` para leer. Perfil completo: herramienta `tarea` con `accion=`.

| Verbo | Lee/escribe | Argumentos | Devuelve |
|---|---|---|---|
| `recetas` | lee | — | recetas registradas: nombre, versión, origen (usuario/instalada), sha |
| `lista` | lee | — | tareas con estado y progreso |
| `iniciar` | escribe | `receta=` \| `archivo=` (ruta completa) \| `codigo=`; `id=`; `objetivo=`; `args={…}` o `args.campo=v`; `refs=`; `doc=`; política (abajo); `espera_s=` (≤120, 10 por defecto) | estado a los `espera_s` segundos + `ya_existia`/`ya_en_curso` |
| `estado` / `resumen` | lee | `id` | resumen compacto (abajo) |
| `esperar` | lee | `id`, `desde=<rev>`, `max=` (≤120 s) | vuelve en cuanto cambia la revisión de la tarea (sin sondeo) |
| `unidades` | lee | `id`, `estado=`, `desde=`, `n=` | unidades con estado, error y elementos creados |
| `reanudar` | escribe | `id`, política opcional | verifica lo dudoso y continúa |
| `reconciliar` | escribe | `id`, `decision=aplicada\|no_aplicada`, `claves=`, `nota=` | decide unidades que no se pudieron verificar |
| `cancelar` | escribe | `id` | se detiene entre unidades; lo confirmado queda |
| `anotar` | escribe | `id`, `texto=` | decisión en la memoria de la tarea |
| `olvidar` | escribe | `id` | archiva la carpeta (`tareas\_archivo\`), no borra |

Política (en `iniciar`/`reanudar`): `guardar_cada=N`, `al_final=guardar,sync,pdf`, `en_error=detener|continuar`, `lote_max=`, `avisos=`, `tx=`.

Identidad de la tarea: `huella = sha16(receta sha + args canónicos + clave del documento + huella del documento + refs)`. Mismo `id` con la misma huella → `ya_existia`/`ya_en_curso` (no se arranca un segundo ejecutor). Mismo `id` con otra huella → `invalido` (dice ambas huellas; use otro id).

Estado de tarea → estado de respuesta: `aplicada→ok`, `aplicada_con_diferencias→completado_con_diferencias`, `parcial→parcial`, `interrumpida→interrumpido`, `cancelada→cancelado`, `cancelacion_solicitada→cancelacion_solicitada`, `esperando_contexto→esperando_contexto`, resto → `en_proceso`.

### Resumen compacto (lo que ve otro cliente)

```
estado, tarea, rev, progreso n/total, unidades (conteo por estado), motivo,
desviaciones (máx. 5 + total), qc_final, artefactos (ruta, sha256, bytes, páginas), siguiente, resumen (una frase)
```
Medido: una tarea de 1 000 unidades con 10 fallos → 996 caracteres; el mismo texto exacto para un segundo cliente que abre el almacén desde cero (`TaskEngineTests.Bench_CompactSummary_StaysSmall_ForThousandUnits`).

## Otras novedades de 0.6.0 en el cable

- `exec`/`commit`: `archivo=` (ruta completa, admite `%VARIABLES%`), `refs=`, `docs=`, `reconciliar=`.
- `doc pdf`: cada archivo lleva `sha256`, `bytes`, `nuevo`, `paginas` y `paginas_metodo: orientativo`.
- `fam abrir`: `procedencia` / `no_reutilizados` (sólo reutiliza un RFA abierto desde ese proyecto y esa familia).
- `metricas` (local al puente): caracteres enviados/recibidos por cliente y herramienta, en `metricas\puente-AAAAMMDD.jsonl` (14 días).
