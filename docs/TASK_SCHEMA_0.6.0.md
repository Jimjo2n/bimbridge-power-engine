# Task and checkpoint schema (0.6.0, schema 1)

Everything lives on disk, outside the conversation: `%LOCALAPPDATA%\Bimbridge\tareas\<id>\`. Any client (Claude, Codex) and any
Revit process that opens the same document can read it. Code: `src/Bimbridge.Engine/Tasks/` (TaskModel, TaskStore, TaskRunner).

| File | Write | Content |
|---|---|---|
| `tarea.json` | atomic (temp file + replace) at every checkpoint | the task memory (below) |
| `receta.cs` | once, at start | exact copy of the code; resume uses THIS copy and checks its SHA-256 |
| `unidades.jsonl` | append-only, one line per event, written straight to disk | start/end/verification of each unit |
| `lease.json` | on acquire/renew | owner (Revit instance) and expiry: two processes never run the same task |
| `artefactos\` | by the recipe or by the barriers | CSV/JSON/PDF with SHA-256 |

## tarea.json

| Field | Type | Meaning |
|---|---|---|
| `esquema` | int | 1 |
| `id`, `objetivo`, `alcance` | text | what the AI asked for |
| `receta` | {nombre, version, hash, origen} | recipe identity |
| `huella` | text | sha16 of recipe + canonical args + document + refs |
| `args`, `politica` | object | inputs and policy (guardar_cada, al_final, en_error, lote_max, avisos, tx) |
| `docs[]` | {clave, huella, generacion, handle, titulo, tipo} | target document. `clave` = normalized path (or cloud/instance id); `huella` = model identity (detects a file replaced at the same path); `generacion` = a specific opening of the file |
| `estado`, `motivo` | text | see states |
| `creada`, `actualizada` | ISO 8601 UTC | |
| `rev` | int | increments on every change; `tarea esperar desde=rev` waits for the next one |
| `cliente`, `clientes[]` | text | who started it and who touched it |
| `instancia` | text | Revit process that last ran it |
| `unidades_total` | int | enumerated so far (enumeration is lazy) |
| `seq_confirmacion` | int | last unit-journal event included in this checkpoint |
| `decisiones[]`, `excepciones[]`, `artefactos[]` | list | working memory: what the recipe or the AI decided, declared exceptions, hashed outputs |
| `ultimo_guardado` | {t, seq, generacion, archivo, bytes, mtime, tipo} | evidence of the last committed save/sync |
| `guardado_por_generacion` | {generation: ISO} | last persisted instant per opening; basis of the safe inference after a crash |
| `metricas` | object | visits, batches, checkpoints, ms per unit, batch size |
| `postcondicion` | object | final QC of the recipe (with `desviaciones`) |
| `resumen` | text | one sentence for another client |

## unidades.jsonl (events)

`{"seq":n,"t":"ISO","ev":"inicio|fin|verificada|reconciliada|pendiente|sin_ejecutar","clave":"…","estado":"…","generacion":"…","error":"…","motivo":"…","creados":[[id,uid],…],"resultado":…,"ms":n,"claves":[…]}`

- `inicio` is written BEFORE each batch (with the batch's `claves` list); `fin` after the commits, per unit, with the created UniqueIds. Every line uses WriteThrough + Flush(true).
- An `inicio` without a `fin` is the commit→journal window: on resume it is **verified**, never blindly repeated.

## States

Task: `preparada → en_cola → ejecutando ⇄ esperando_contexto → aplicada | aplicada_con_diferencias | parcial | interrumpida | cancelacion_solicitada → cancelada`.

Unit: `pendiente, en_curso, confirmada, revertida, conflicto, parcial, desconocida, omitida`.

## Resume without duplicating (verification order for an `en_curso` unit)

1. If it recorded created elements and **all** their UniqueIds exist → `confirmada`.
2. If none exists and the file was reopened (another generation) → `pendiente` (it runs again).
3. Otherwise, the recipe's `Verify` (Applied / NotApplied).
4. If the recipe does not know: inference only when it is safe — the unit started in a generation and **there was no save** of that generation after its `inicio` → the work was lost with the crash → `pendiente`.
5. Otherwise `desconocida`: requires `tarea reconciliar decision=…` (the client or the user decides; never the engine).

## Barriers

`guardar`, `sync`, `pdf` (and those declared by the recipe) only run when every earlier unit is `confirmada`/`omitida`.
After running: file verification (exists, hash, date, size, approximate pages) → `artefactos[]` and `ultimo_guardado`.
