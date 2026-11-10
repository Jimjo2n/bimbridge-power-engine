# Esquema de tarea y checkpoint (0.6.0, esquema 1)

Todo vive en disco, fuera de la conversación: `%LOCALAPPDATA%\PDI.RevitMcp\tareas\<id>\`. Cualquier cliente (Claude, Codex) y cualquier
proceso de Revit que abra el mismo documento puede leerla. Código: `src/PDI.RevitMcp.Engine/Tasks/` (TaskModel, TaskStore, TaskRunner).

| Archivo | Escritura | Contenido |
|---|---|---|
| `tarea.json` | atómica (temporal + reemplazo) en cada checkpoint | la memoria de la tarea (abajo) |
| `receta.cs` | una vez, al iniciar | copia exacta del código; reanudar usa ESTA copia y comprueba su SHA-256 |
| `unidades.jsonl` | sólo anexar, una línea por evento, escritura directa a disco | inicio/fin/verificación de cada unidad |
| `lease.json` | al tomar/renovar | dueño (instancia de Revit) y vencimiento: dos procesos no corren la misma tarea |
| `artefactos\` | por la receta o por las barreras | CSV/JSON/PDF con SHA-256 |

## tarea.json

| Campo | Tipo | Significado |
|---|---|---|
| `esquema` | int | 1 |
| `id`, `objetivo`, `alcance` | texto | lo que pidió la IA |
| `receta` | {nombre, version, hash, origen} | identidad de la receta |
| `huella` | texto | sha16 de receta + args canónicos + documento + refs |
| `args`, `politica` | objeto | entradas y política (guardar_cada, al_final, en_error, lote_max, avisos, tx) |
| `docs[]` | {clave, huella, generacion, handle, titulo, tipo} | documento destino. `clave` = ruta normalizada (o id de nube/instancia); `huella` = identidad del modelo (detecta archivo reemplazado en la misma ruta); `generacion` = apertura concreta del archivo |
| `estado`, `motivo` | texto | ver estados |
| `creada`, `actualizada` | ISO 8601 UTC | |
| `rev` | int | sube en cada cambio; `tarea esperar desde=rev` espera el siguiente |
| `cliente`, `clientes[]` | texto | quién la inició y quién la tocó |
| `instancia` | texto | proceso de Revit que la corrió por última vez |
| `unidades_total` | int | enumeradas hasta ahora (la enumeración es perezosa) |
| `seq_confirmacion` | int | último evento del diario de unidades incluido en este checkpoint |
| `decisiones[]`, `excepciones[]`, `artefactos[]` | lista | memoria de trabajo: lo que decidió la receta o la IA, excepciones declaradas, salidas con hash |
| `ultimo_guardado` | {t, seq, generacion, archivo, bytes, mtime, tipo} | evidencia del último guardado/sync confirmado |
| `guardado_por_generacion` | {generación: ISO} | último instante persistido por apertura; base de la inferencia segura tras una caída |
| `metricas` | objeto | visitas, lotes, checkpoints, ms por unidad, tamaño de lote |
| `postcondicion` | objeto | QC final de la receta (con `desviaciones`) |
| `resumen` | texto | una frase para otro cliente |

## unidades.jsonl (eventos)

`{"seq":n,"t":"ISO","ev":"inicio|fin|verificada|reconciliada|pendiente|sin_ejecutar","clave":"…","estado":"…","generacion":"…","error":"…","motivo":"…","creados":[[id,uid],…],"resultado":…,"ms":n,"claves":[…]}`

- `inicio` se escribe ANTES de cada lote (con la lista `claves` del lote); `fin` después de los commits, por unidad, con los UniqueId creados. Cada línea con WriteThrough + Flush(true).
- Un `inicio` sin `fin` es la ventana commit→diario: al reanudar se **verifica**, nunca se repite a ciegas.

## Estados

Tarea: `preparada → en_cola → ejecutando ⇄ esperando_contexto → aplicada | aplicada_con_diferencias | parcial | interrumpida | cancelacion_solicitada → cancelada`.

Unidad: `pendiente, en_curso, confirmada, revertida, conflicto, parcial, desconocida, omitida`.

## Reanudar sin duplicar (orden de verificación de una unidad `en_curso`)

1. Si registró elementos creados y **todos** sus UniqueId existen → `confirmada`.
2. Si no existe ninguno y el archivo se reabrió (otra generación) → `pendiente` (se ejecuta otra vez).
3. Si no, `Verificar` de la receta (Aplicada / NoAplicada).
4. Si la receta no sabe: inferencia sólo cuando es segura — la unidad empezó en una generación y **no hubo ningún guardado** de esa generación después de su `inicio` → lo hecho se perdió con la caída → `pendiente`.
5. En otro caso `desconocida`: exige `tarea reconciliar decision=…` (el cliente o el usuario deciden; nunca el motor).

## Barreras

`guardar`, `sync`, `pdf` (y las declaradas por la receta) sólo corren cuando todas las unidades anteriores están `confirmada`/`omitida`.
Tras correr: verificación del archivo (existe, hash, fecha, tamaño, páginas orientativas) → `artefactos[]` y `ultimo_guardado`.
