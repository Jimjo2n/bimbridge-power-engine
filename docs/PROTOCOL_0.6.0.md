# BIMBRIDGE 0.6.0 protocol

Wire version: **2** (no format changes from 0.5.0; 0.6.0 adds the `tarea` operation and new fields, all optional).
A 0.5.0 client keeps working: no existing operation changed its name or meaning.

## Layers

| Layer | What it is | Where |
|---|---|---|
| AI ↔ bridge | MCP over stdio. **Compact** profile (default, 5 tools: `revit`, `plan`, `commit`, `exec`, `act`) or **complete** profile (20). | `Bimbridge.Bridge.exe` |
| Bridge ↔ add-in | Line-delimited JSON over a local named pipe per Revit instance (announced by `instances\<pid>.json`). | `Bimbridge.Protocol` |
| Add-in ↔ Revit | Revit main thread (ExternalEvent + Idling in budgeted slices). | `Bimbridge.Addin` |

## Request and response

Request: `{"id":n,"op":"<op>","args":{…},"doc":"dN|path|title","clave":"…","tarea":"…","cliente":"claude|codex|…"}`.
Response: `{"id":n,"estado":"<status>","datos":{…},"aviso":"…","rev":n,"ms":n}`.

Response statuses (0.6.0 adds the four in the second row):

`ok, en_cola, en_proceso, retenido, conflicto, revit_ocupado, revertido, vencido, rechazado, invalido, revit_no_disponible, incompatible, error, tiempo_agotado, no_soportado, destino_no_disponible, necesita_usuario, interrumpido, completado_con_diferencias, parcial, desconocido, aplicado_error_resultado, ambiguo, documento_ocupado`
`cancelacion_solicitada, cancelado, esperando_contexto, diario_no_disponible`

Rules that do not change by profile or client:
- **Target document**: resolved once (handle `dN` → exact path → normalized path → key → title) and pinned for every slice. An ambiguous title returns `ambiguo` with the list of candidates and chooses none. The UI's active document never replaces the target.
- **Writes** only in Live mode and level W (or P for plan); the Hold button pauses: `retenido` or, for tasks, `esperando_contexto`.
- **Idempotency**: a repeated `clave=` with the same content returns the earlier result; with different content, `conflicto`. `parcial`/`desconocido`/`interrumpido` require `reconciliar=si|no` before repeating.
- Durable operation **journal** (`operaciones\*.jsonl`, append-only, broken line recovered). If it cannot be written: `diario_no_disponible` (never silent).

## `tarea` operation (new in 0.6.0)

Compact profile: `act "tarea <verb> …"` to write and `revit "tarea <verb> …"` to read. Complete profile: the `tarea` tool with `accion=`.

| Verb | Read/write | Arguments | Returns |
|---|---|---|---|
| `recetas` | read | — | registered recipes: name, version, origin (user/installed), sha |
| `lista` | read | — | tasks with status and progress |
| `iniciar` | write | `receta=` \| `archivo=` (full path) \| `codigo=`; `id=`; `objetivo=`; `args={…}` or `args.field=v`; `refs=`; `doc=`; policy (below); `espera_s=` (≤120, default 10) | status after `espera_s` seconds + `ya_existia`/`ya_en_curso` |
| `estado` / `resumen` | read | `id` | compact summary (below) |
| `esperar` | read | `id`, `desde=<rev>`, `max=` (≤120 s) | returns as soon as the task revision changes (no polling) |
| `unidades` | read | `id`, `estado=`, `desde=`, `n=` | units with status, error and created elements |
| `reanudar` | write | `id`, optional policy | verifies whatever is uncertain and continues |
| `reconciliar` | write | `id`, `decision=aplicada\|no_aplicada`, `claves=`, `nota=` | decides units that could not be verified |
| `cancelar` | write | `id` | stops between units; committed work stays |
| `anotar` | write | `id`, `texto=` | decision in the task memory |
| `olvidar` | write | `id` | archives the folder (`tareas\_archivo\`), does not delete |

Policy (on `iniciar`/`reanudar`): `guardar_cada=N`, `al_final=guardar,sync,pdf`, `en_error=detener|continuar`, `lote_max=`, `avisos=`, `tx=`.

Task identity: `huella = sha16(recipe sha + canonical args + document key + document fingerprint + refs)`. Same `id` with the same fingerprint → `ya_existia`/`ya_en_curso` (no second runner is started). Same `id` with a different fingerprint → `invalido` (reports both fingerprints; use another id).

Task status → response status: `aplicada→ok`, `aplicada_con_diferencias→completado_con_diferencias`, `parcial→parcial`, `interrumpida→interrumpido`, `cancelada→cancelado`, `cancelacion_solicitada→cancelacion_solicitada`, `esperando_contexto→esperando_contexto`, everything else → `en_proceso`.

### Compact summary (what another client sees)

```
estado, tarea, rev, progress n/total, unidades (count per status), motivo,
desviaciones (max. 5 + total), qc_final, artefactos (path, sha256, bytes, pages), siguiente, resumen (one sentence)
```
Measured: a 1,000-unit task with 10 failures → 996 characters; the exact same text for a second client that opens the store from scratch (`TaskEngineTests.Bench_CompactSummary_StaysSmall_ForThousandUnits`).

## Other 0.6.0 wire additions

- `exec`/`commit`: `archivo=` (full path, accepts `%VARIABLES%`), `refs=`, `docs=`, `reconciliar=`.
- `doc pdf`: each file carries `sha256`, `bytes`, `nuevo`, `paginas` and `paginas_metodo: orientativo`.
- `fam abrir`: `procedencia` / `no_reutilizados` (only reuses an RFA opened from that project and that family).
- `metricas` (local to the bridge): characters sent/received per client and tool, in `metricas\puente-AAAAMMDD.jsonl` (14 days).
