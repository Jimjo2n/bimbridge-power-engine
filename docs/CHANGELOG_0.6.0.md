# Changes in 0.6.0 (since 0.5.0 c87e162)

**Verified** column: `test` = automated test without Revit (real engine with a fake model or pure logic); `compiles` = compiles against the 2022–2026 API; `native` = run inside Revit. Nothing in 0.6.0 is `native` yet.

## A — Identity, states, journal (the 5 regressions from the technical review)

| Change | Verified |
|---|---|
| The accepted write mode (`escribir`, ` ejecutar `, aliases with spaces) is never classified as a read; unknown mode → `invalido`, never reinterpreted | test (TransportRegressionTests + IdentityJournalTests) |
| An `en_proceso` response keeps the operation alive in the journal (it is not treated as finished) | test |
| `reconciliar` does not accept different content with the same key as "already applied" | test |
| A partial commit error requires reconciliation before retrying; a confirmed `revertido` allows a retry | test |
| The operation hash includes the resolved code (not just the file name) and does not depend on argument order | test |
| Single document resolver (handle → exact path → normalized path → key → title); ambiguous title → `ambiguo` without choosing | test (`DocResolver`) |
| Unsaved documents from two instances never share a key; a file replaced at the same path is detected; Save As carries an alias with the completed operations | test |
| Journal: broken line recovered; two processes see the same journal; unwritable journal → visible `diario_no_disponible` | test |

## B — Resumable task engine

| Change | Verified |
|---|---|
| `tarea` operation (iniciar, estado, resumen, esperar, unidades, reanudar, reconciliar, cancelar, anotar, olvidar, lista, recetas) in both profiles | test (end-to-end transport with simulated Revit) |
| Units with a stable key, adaptive batches that yield Revit between visits, durable checkpoint per batch | test (1,000 units, no duplicates) |
| Crash between commit and journal → verified (created UniqueIds / Verify / safe inference by generation and save), not repeated | test (fault injection) |
| Revit restart before/after saving: unsaved work is redone, saved work is not | test |
| save/sync/pdf barriers only once everything before them is committed; file evidence (hash, bytes, date) | test (engine) / compiles (Revit backend) |
| Pause (Hold), closed document or busy Revit → `esperando_contexto`, resumes on its own | test |
| Conflict (someone else changed the elements) → unit `conflicto`, not overwritten; cooperative cancellation | test |
| Same id twice → no second runner starts; different content → rejected | test |
| Real backend (`RevitTaskBackend`): one transaction per unit with warning capture, result serialized before commit, per-task document lock, rekey on Save As, persistence by generation | compiles |

## C — General runner, recipes, memory across clients

| Change | Verified |
|---|---|
| File-based recipes (`%LOCALAPPDATA%\Bimbridge\recetas`, the add-in's `recetas\`) with version and SHA-256; the task keeps an exact copy | test |
| `exec`/`commit`: `archivo=` (full path, `%VARIABLES%`), `refs=`, `docs=`, `reconciliar=` reach the handler | test (transport) |
| Compact task summary: 1,000 units → 996 characters, identical for another client reading from disk | test (measurement) |
| Bridge metrics per client/tool (characters, not tokens) | test |
| `tareas` and `recetas` guides | test |

## D — Helpers and QC

| Change | Verified |
|---|---|
| `ManifestQc`: one-to-one, ambiguity not approved, missing/extra/near-duplicates, rotation with symmetry, tags, group, mounting, unconfirmed | test (acceptance fixture D with known deviations) |
| `Xform`: rotation, mirror, translation, inverse (links and groups) | test |
| `VisibilityQc`: hidden, category, filter, workset, phase, option, crop, range (floor plan/RCP), what cannot be checked | test |
| `Bim.Grupos/Montaje/Vistas/Manifiesto` over the API (rays with links, view range, why it is not visible, group type change with verification) | compiles |
| 5 bundled recipes: etiquetar_luces, parametro_por_lote, qc_manifiesto, montaje_soportes, exportar_laminas | compiles (all 5 years) |
| `fam abrir` only reuses an RFA opened from that project and that family | compiles |
| PDF: `paginas_metodo: orientativo` | compiles |

## E — Installer, matrix, harness, benchmark

| Change | Verified |
|---|---|
| `manifest.json` with the SHA-256 of every file in the package; the installer verifies before (installs nothing on mismatch) and after copying | test |
| `Setup.exe /diagnostico`: installed, loaded in each open Revit, bridge, hashes, pending restart, config | test |
| Silent install without `revit=`/`niveles=` keeps the previous choice and permissions (direct mode); a disabled MCP stays disabled | test |
| Update with files in use: says it takes effect on restart (does not pretend) | compiles |
| Capability matrix with evidence levels (compiles / test / simulated / native) | test (generated from the code) |
| Native acceptance harness for 2024/2026 (`tools/acceptance/PROCEDURE_0.6.0.md`, manifest and perturbation scripts, `collect_evidence.ps1`) | compiles (scripts) |
| Equivalent MCP/pyRevit benchmark over 3 workloads (`tools/bench_060`) + p50/p95 summary | compiles (MCP side); not yet run |
| Version 0.6.0 | — |
