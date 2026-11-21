# BIMBRIDGE 0.8.0 — internal candidate status

**Release status:** internal candidate **NATIVE_ACCEPTANCE_PENDING**. Branch `feature/0.8.0` from `v0.7.0-rc1` (14ec21d). No tag until the native campaign passes. Nothing in 0.8.0 has run in Revit: acceptance requires an isolated test instance and an approved scope (`BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION`).

This document is the only 0.8.0 report: status, tests, performance, security, limitations and roadmap. The matrix is in [MATRIZ_TRAZABILIDAD_0.8.0.md](MATRIZ_TRAZABILIDAD_0.8.0.md) and the campaign in `tools/acceptance/0.8.0/PROCEDURE_0.8.0.md`.

## 1. What changed since rc1

| Package | Commit | Content |
|---|---|---|
| A1 | 974f1db | Technical close-out of 0.7.0: each persistent commit leaves an intent and a result in `auditoria.jsonl` (rc1 H2); `Home.WriteAtomic` retries and leaves no temp files; NRT-07 harness (IPC response lost after commit). |
| A2–A3 | ca3e4e1, 65099e7 | Requirements with provenance and authority (OBSERVED/INFERRED/UNKNOWN; an inference is never OBSERVED); `verificar_independiente` (independent source → model → differences; same source as the construction = `no_independiente`); 12 delivery states with receipts; structured handoff. |
| C1–C2 | 099724f, dfc2b0e | 2D core without Revit (`Draw2D`) and 7 new operations: `detail_line`, `detail_rect`, `detail_polygon`, `detail_arc`, `detail_component`, `dimension`, `sheet_set`; `view_create tipo=dibujo`. Catalog: 34 operations. |
| D1 | d9925fc | Family library: `fam buscar` only indexes `carpetas_familias`; `fam cargar_archivo` loads an indexed and unchanged RFA, with sha-256. |
| E1 | ec979fe | **BIMBRIDGE → Developer Tools → Update from ZIP** (details in §4). Ribbon tab renamed to BIMBRIDGE. |
| E2 | 45ac7e9 | ESTIMATED tokens (characters/4) in the metrics; reproducible B0 benchmark; B1–B8 catalog. |
| E3 | 426132c | Permissions split into 10 classes, enforced in the add-in before dispatching. |
| F1 | 8ec9c51 | Single capability registry and traceability matrix generated from code. |
| G1 | 40b2c43 | 13-item native campaign with the `revit campana080` harness. |

## 2. Tests

| Set | Result |
|---|---|
| Full suite (without Revit) | **440/440** in the last run. In an earlier run of the same tree, `EndToEndTests.HeldCommit_WaitsForUser_ThenApplies_AndDetectsConflicts` failed once on timing (12 s under load) and passed on repeat: flaky, not a regression. |
| New in 0.8.0 | ZipUpdate 12, Permissions 3, Traceability 2, Campaign080 4, SheetSetOp 1, FamilyLibrary 3, Draw2D 12, Workflow 9, V080 (AgentHost) 4, Bench B0 1 |
| Add-in | compiles without errors for Revit 2022–2026 |
| 0.8.0 native | **0 run**. 13 items BLOCKED (see procedure). |
| Earlier native (another build) | rc1 14ec21d on Revit 2024, disposable copies: NRT-03 15/15, NRT-05, NRT-06 11/11. They do not count for 0.8.0. |

## 3. Performance and tokens

All figures are **MEDIDO_SIMULADO** (measured, simulated): fake add-in over the real pipe, without Revit. Tokens are **ESTIMATED** (characters/4). To reproduce: `BIMBRIDGE_BENCH_OUT=<folder> dotnet test --filter Bench_B0`.

| B0 · 586 elements (read and change one parameter) | One by one | BIMBRIDGE plan |
|---|---|---|
| Agent calls | 1,172 | 3 |
| Wire calls | 1,759 | 16 |
| Commits | 586 | 3 (batches of ≤200) |
| Characters the agent sees | 254,762 | 12,799 |
| Agent estimated tokens | 63,691 | 3,200 |

Reduction: 99.74 % of agent calls and 94.98 % of estimated tokens. It matches the report's hypothesis (1,172 → 3), now measured in simulation; Revit time is not measured. B1–B8 (comparison with pyRevit): **NO_MEDIDO** (not measured), with the scenario and harness defined in `Benchmarks.Catalog`.

## 4. Security

| Topic | Status in 0.8.0 |
|---|---|
| Wrong document | The AgentHost pins pid, key and fingerprint; the campaign injects the preflight document into each step (a step cannot choose another, except the marked negative ones). Fail-closed if a context flag is missing (rc1). |
| Permissions | 10 classes: leer, modificar_proyecto, modificar_familia, cargar_archivo, guardar, sincronizar, exportar, publicar, codigo_dinamico, interfaz. `permisos` in config.json restricts; empty = derived from the R/P/W/D levels (same behavior as before). Publish does not exist and is always denied. The gate is in the add-in, which both the bridge and the AgentHost go through. |
| Dynamic code (level B) | `exec ejecutar/ensayo` requires W, runner=auto, the codigo_dinamico class and is recorded in the journal and the audit; with Hold active it is rejected; extra DLLs only from the add-in folder or by SHA-256 unless «Trusted code». **Residual risk:** with W + Live there is no per-call human review and the code runs with the user's privileges inside Revit. For production, a `permisos` list without codigo_dinamico is recommended. |
| UI (level C) | Only `show` and `capture`. There are no automated clicks. |
| Disks and families | `fam buscar` only walks configured folders, without following links and with a file limit; `cargar_archivo` rejects paths outside them or changed since indexing. Corporate RFAs are not edited: family edits require modificar_familia. |
| Update from ZIP | Only a person can press it (the AI has no tool). Inspection: manifest, entries without dangerous paths, size limit, exactly the files in the manifest with their SHA-256, RSA-SHA256 signature against trusted keys, version and Revit year compatibility. Shows the installed and new versions and asks for confirmation (double if unsigned). Stages and re-verifies on disk; a separate process waits for the user to close Revit (never closes it), re-verifies, installs, verifies and, if it fails, rolls back to the previous version. No hot reload. **Pending IT:** the signing key; while no keys are configured, unsigned packages are accepted only with explicit confirmation. |
| Data | No secrets in the repository or the package. Benchmarks and tests use synthetic data. |

## 5. Known limitations

- No new operation has run in Revit; the real behavior of `sheet_set` (PrintManager), `dimension`, `detail_component` and `cargar_archivo` may differ by version.
- `sheet_set` does not control print order (Revit decides it) and leaves `PrintRange=Select` in the document.
- `dimension` only dimensions existing detail lines (not `@n` from the same plan).
- Verification of the exported PDF compares pages and the receipt sha; there is no PDF content reader.
- Tokens: character-based estimate; the real tokenizer is known only to the client.
- Pending from rc1 without new native evidence: real reopen in NRT-05 (H1), intermittent Revit slowness, leftover temp file (fixed in code, not checked in Revit).
- The ZIP in this delivery is unsigned (there is no IT key).

## 6. Roadmap

- **0.8.0 → approved:** 13-item native campaign with operator authorization; native B0 and first pair against pyRevit.
- **0.9.0:** drawing–model registration (CAP-009), group coverage (CAP-010), physical routing (CAP-011), riser graph (CAP-012), legibility on paper (CAP-013), PDF/table source adapters (CAP-008), minimal events (CAP-015). Rust only if a measurement justifies it; the core stays in C#/.NET.
