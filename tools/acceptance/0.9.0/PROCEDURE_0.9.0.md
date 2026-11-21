# Native campaign 0.9.0 — procedure

**Status: BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION.** The campaign requires an isolated test instance and disposable models. No native evidence exists for this revision. The simulated results of the suite are not native evidence.

## Before starting (once authorized)

1. A **disposable LOCAL copy**, not workshared, of an authorized public or synthetic model, with named straight grids and a `OneLevelBased` fixture loaded. Never SAMPLE PROJECT or ACC/BIM 360.
2. Install 0.9.0 by hand (`Bimbridge.Setup.exe`); installation is a user operation, separate from the automated tests.
3. `config.json`: levels `RPW`, `perfil: laboratorio` with the copy's folder in `laboratorio.carpetas`, `carpetas_documentos` with the test PDF folder.
4. Test PDF: a synthetic one from `workers/python/tests/fixtures.py` (or a public drawing), with grids whose names exist in the copy.
5. `Bimbridge.AgentHost power-engine estado` → Python and Rust available (or the expected degradation, noted).

## Steps

| Item | What it tests | How |
|---|---|---|
| N9-01 | Identity, version 0.9.0, pinned document | `revit campana080 --revit-pid N --ruta-esperada <copy.rvt> --pasos N9-01.json --salida DIR` |
| N9-02 | Model grids (read only) | `--pasos N9-02.json --var codigo_ejes=@tools\acceptance\0.9.0\model_grids_090.cs`; the artifact `model_grids_090.json` is the workflow input |
| — | PDF → ops workflow (without Revit) | `power-engine flujo-pdf --pdf plan.pdf --nivel "Level 1" --reglas rules.json --ejes model_grids_090.json --salida DIR\flujo`; review `plan.json` (registration `ok`, residuals, ambiguous) |
| N9-03 | Trial: applies, verifies and reverts | `--pasos N9-03.json --var ops=@DIR\flujo\ops.json --autorizo-persistente`; the final count must equal N9-01's |
| N9-04 | Persistent placement and count | `--pasos N9-04.json --var ops=@DIR\flujo\ops.json --var nivel="Level 1" --autorizo-persistente`; count = before + placed by the plan |
| — | Independent QA/QC | Read what was placed (`revit q LF lvl=… g=type,x,y`) and verify against a source different from the PDF (the fixture's truth table); `no_independiente` if the same PDF is used |
| N9-05 | Three-layer conduit network | `--pasos N9-05.json --var codigo_conductos=@tools\acceptance\0.9.0\conduits_090.cs`; then `power red ruta=<conduits_090.json>` (copied to a documents folder) |
| N9-06 | Tags on what was placed | `--pasos N9-06.json --var ids_colocados=… --var vista=<RCP> --autorizo-persistente` |
| B-PE | Benchmark on the PC | `power-engine bench --docx … --pdf … --salida bench_pc.json` and `power-engine calibrar --salida bin\power_engine\calibracion.json` |
| B1–B8 | BIMBRIDGE versus pyRevit | **DEFERRED**: pyRevit is restricted in BIMBRIDGE; only with express authorization and on the copy |

## Criteria

- No step writes outside the copy; the pinned document does not change between steps.
- N9-03 leaves the model identical; N9-04 places exactly the points of the plan, without duplicates.
- Ambiguous items of the plan are not placed.
- Collect the whole `DIR` with `collect_evidence.ps1` and attach `bench_pc.json`.
