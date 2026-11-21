# BIMBRIDGE 0.6.0: delivery report (candidate)

## 1. What 0.6.0 can finish that 0.5.0 could not

- **A long task with a single command.** 0.5.0 forced the AI to split the work into plans of ≤200 operations and to keep count itself. In 0.6.0 the AI says `tarea iniciar receta=… id=…` and the add-in enumerates the units, runs them in batches with one transaction per unit, yields Revit between visits, saves a checkpoint per batch and finishes with save/sync/PDF only when everything before is committed.
- **Surviving the loss of the chat, the bridge or Revit without duplicating.** A unit caught between the commit and the journal is verified (UniqueIds of what was created, the recipe's `Verificar`, or safe inference by opening and save) instead of being repeated. Whatever cannot be proven stays `desconocida` and waits for an explicit decision (`tarea reconciliar`).
- **Letting another client pick up the work.** Codex or Claude read `tarea resumen <id>` from disk: ~1,000 characters for a 1,000-unit task (status, progress, deviations, hashed artifacts and next step), without resending code or context.
- **Reusable file-based recipes**, with their version and SHA-256 frozen in the task. Five bundled: `etiquetar_luces` (exactly one tag per lighting fixture, resumable), `parametro_por_lote`, `qc_manifiesto`, `montaje_soportes` and `exportar_laminas`.
- **QC that does not pass on totals.** There is a one-to-one QC against a reference manifest that detects missing, extra and near-duplicate elements, position, height, rotation, type, level, tag, group and mounting, and does not approve an ambiguous case. Mounting is classified against the real support, also inside links (flush, recessed, offset or no reference). And there is a "why is it not visible?", which distinguishes filter, category, workset, phase, crop and view range, and says what cannot be checked through the API.
- **A verifiable installer.** It checks the SHA-256 of each file before and after copying. `Setup.exe /diagnostico` reports the installed version, the version loaded in each open Revit, the bridge status and whether a restart is pending. It also keeps direct mode and the chosen versions on update.

## 2. What was tested inside Revit

**Nothing in 0.6.0 has run inside Revit yet.** This environment has no Revit.

| Level | What it covers |
|---|---|
| Compiled | Compiles for Revit 2022, 2023, 2024, 2025 and 2026. The 5 recipes, the benchmark scripts and the acceptance scripts compile against the API of all 5 years; an automated test checks this. |
| Tested without Revit | **193 tests green.** The real task engine was tested with a simulated model and fault injection: crash between commit and journal, Revit restart before and after saving, pause, closed document, conflict, cancellation and unwritable journal. Also tested: end-to-end transport in both profiles, document identity, the 5 transport regressions, manifest QC with fixture D of known deviations, the installer (hashes, diagnostics, preserved configuration) and the summary measurement. |
| Native | There is only earlier 0.5.0 evidence on Revit 2026: read, one sample, on 20-10-2026. The Revit task backend (`RevitTaskBackend`), the `Bim.*` helpers and the recipes are **only compiled**. |

Native acceptance is written step by step for 2024 and 2026 on practice copies, with its pass criteria: [tools/acceptance/PROCEDURE_0.6.0.md](../tools/acceptance/PROCEDURE_0.6.0.md). None of those steps is declared passed here.

## 3. Benchmark results

| Measure | 0.5.0 | 0.6.0 | How |
|---|---|---|---|
| Fixed cost, compact profile (what the AI loads per conversation) | 5 tools, 5,810 characters | 5 tools, **5,685 characters** (~1,780 estimated tokens), with `tarea` included | bench `tokens` |
| Fixed cost, complete profile | — | 20 tools, 18,437 characters | bench `tokens` |
| Following/resuming a 1,000-unit task with 10 failures | did not exist: the AI rebuilt it with many calls | **996 characters**, identical for a second client | `Bench_CompactSummary_*` |
| MCP vs pyRevit in Revit, 3 workloads, 1 cold + 10 warm | — | **not run** | `tools/bench_060` |

The only native comparison with pyRevit is still the 0.5.0 one on Revit 2026: read, one sample per test and non-identical counts. It gave MCP ≈1.3–1.5 s versus pyRevit ≈3.6–5.2 s, with between ½ and ¼ of the text. It is not an equivalent benchmark.

For the equivalent benchmark the three workloads are ready in both engines, with the same algorithm and scope: family, geometric QC of lighting fixtures, and groups/views/sheets/PDF. The run template and the p50/p95 summary are also ready; the summary is only valid if the QC results match between engines.

Limits of these figures:
- Token figures are characters ÷ 3.2, not real client tokens.
- Measurements without Revit say nothing about time inside Revit.

## 4. Material limitation that remains

**The real task path inside Revit has not been run.** The engine is tested and the Revit adapter compiles, but per-unit transactions with real warnings, rays against links, PDF export from the task and reopening after a real crash will only be confirmed with the native procedure. Until then, 0.6.0 is a candidate, not approved.

Other known limits (due to the API, not by choice):
- The view range of plan regions is not readable through the public API: `PorQueNoSeVe` marks it as not checkable.
- The PDF page count is approximate.
- The API does not say which design option a view shows.
- Whether an entire group is mirrored is inferred from its members.

## 5. Installation

1. Unzip `Bimbridge.Setup_0.6.0.zip`.
2. Check the SHA-256 of the `.exe` against `MANIFIESTO_0.6.0.txt`.
3. Run it and check 2024 and/or 2026. With Revit open, the new version takes effect on restart; the diagnostics say so.
4. Diagnostics: `Bimbridge.Setup.exe /diagnostico`.
5. Roll back to the previous version: the ribbon "Version" button → previous version. The previous installer is kept.

Short test of a real task: [QUICK_TEST_OPERATOR.md](QUICK_TEST_OPERATOR.md).

## 6. Delivery files

| File | What it is |
|---|---|
| `Bimbridge.Setup_0.6.0.zip` | 2022–2026 installer, with a `manifest.json` of hashes inside |
| `MANIFIESTO_0.6.0.txt` | SHA-256 of each delivery file and each package file |
| `Bimbridge_codigo_0.6.0_<commit>.zip` and `codigo_0.6.0.gitbundle` | full source and repository with history (tag v0.6.0) |
| `CHANGELOG_0.6.0.md` | changes with their verification level |
| `PROTOCOL_0.6.0.md`, `TASK_SCHEMA_0.6.0.md`, `RECIPES.md` | contract of the `tarea` operation, checkpoint schema, recipes |
| `MATRIZ_CAPACIDADES.md`, `REQUIREMENTS_STATUS_0.6.0.csv` | capability per command and requirement, with evidence |
| `acceptance\` | native procedure, manifest and perturbation scripts, `collect_evidence.ps1` |
| `bench_060\` | equivalent MCP/pyRevit benchmark and summary |
| `evidencia\` | test output, cost measurements and task summary |
