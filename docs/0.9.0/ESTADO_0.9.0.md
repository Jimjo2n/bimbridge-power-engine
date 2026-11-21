# BIMBRIDGE 0.9.0 — status

**Status:** internal candidate **NATIVE_ACCEPTANCE_PENDING**. Branch `feature/0.9.0` from 0.8.0 (2413aec), which is also still NATIVE_ACCEPTANCE_PENDING. Nothing in 0.8.0 or 0.9.0 has run in Revit: every native test is **BLOCKED — REVIT_IN_USE — AWAITING_USER_AUTHORIZATION**. The numbers in this document come from tests without Revit or from measurements in a Linux container; none is native evidence.

Architecture: [POWER_ENGINE_ARCHITECTURE.md](POWER_ENGINE_ARCHITECTURE.md). Traceability: [MATRIZ_TRAZABILIDAD_0.9.0.md](MATRIZ_TRAZABILIDAD_0.9.0.md). Native campaign: `tools/acceptance/0.9.0/PROCEDURE_0.9.0.md`.

## 1. Fixes to 0.8.0

| Id | Fix | Test |
|---|---|---|
| P0-1 | Updates with mandatory cryptographic provenance (RSA-SHA256 against trusted keys) in production; the signature and hashes are re-verified before installing | ZipUpdateTests (16) |
| P0-2 | Unsigned package only in the lab profile with explicit `actualizacion_sin_firma`; a package staged in the lab is rejected in production | ZipUpdateTests |
| P0-3 | Demonstrable QA/QC independence: SHA-256 and lineage different from the construction; otherwise `no_independiente` | Hardening090Tests, PdfToModelWorkflowTests |
| P0-4 | Critical export and delivery receipts with revision, origin and COMPROBADO (verified) evidence | Hardening090Tests |
| P1-5 | Draw2D: degenerate geometry and overlapping collinear segments rejected | Hardening090Tests |
| P1-6 | Flaky Hold test (HeldCommit) stabilized | full suite |
| P1-7 | B1/B8 references corrected against the procedure | Hardening090Tests |

Behavior change: in production, `exec ejecutar/ensayo` requires the SHA-256 of the code in `codigo_autorizado`.

## 2. New in 0.9.0

- **Power Engine** (C# core, Python documents, Rust geometry) with the pe=1 contract, a dispatcher by measured cost, a capability registry and controlled degradation.
- **Python Document Worker**: `pdf_info`, `pdf_extract`, `pdf_tables`, `pdf_symbols`, `pdf_grids`, `docx_extract`.
- **Rust Geometry Engine** via FFI (DLL) and IPC (process): `transform3`, `nearest`, `match`, `duplicates`, `segment_contacts`, `components`.
- **PDF → model workflow**: grids and anchors, drawing–model registration, deterministic placement plan, `place` ops for `plan`→`commit`, independent verification.
- **Engines**: three-layer MEP network, risers (diagram ≠ physical), groups and variants, tag distribution.
- **`power` tool** in the compact profile: `cap`, `estado`, `pdf …`, `docx`, `flujo`, `red`. It does not change the model.
- **AgentHost**: `power-engine estado | flujo-pdf | bench | calibrar`.
- **Installer and build**: Rust DLL and exe for Windows and the Python Worker with pure dependencies pinned by SHA-256 are installed next to the bridge, verified by hash.

## 3. Tests

| Set | Result |
|---|---|
| C# (xUnit, without Revit) | **505/505** |
| Python Worker (unittest, real subprocess) | **30/30** |
| Rust (cargo test) | **6/6** |
| Add-in | compiles for Revit 2022–2026; the `*_090.cs` scripts compile against all 5 years |
| 0.9.0 native | **0 run**. N9-01…N9-06 and the benchmark on the PC: BLOCKED |
| BIMBRIDGE versus pyRevit (B1–B8) | DEFERRED: reproducible comparison pending in an isolated environment |

## 4. Measured performance (Linux x64 container, 4 CPUs; not the test machine)

Median of 5 rounds, ms. Full file: `bench_power_engine_2026-11-04_linux4cpu.json`.

| Job | n | C# | Rust FFI | Rust IPC | FFI vs C# |
|---|---|---|---|---|---|
| nearest | 1,000 | 1.33 | 0.16 | 2.20 | ×8.1 |
| nearest | 100,000 | 85.0 | 13.2 | — | ×6.5 |
| match | 100,000 | 66.6 | 19.3 | — | ×3.5 |
| duplicates | 100,000 | 87.1 | 35.3 | — | ×2.5 |
| segment_contacts | 20,000 | 37.6 | 3.3 | 32.6 | ×11.3 |
| transform3 | 100,000 | 1.87 | 2.21 | — | ×0.85 |
| components | 100,000 | 4.6 | 3.6 | — | ×1.3 |

- Fixed cost per call: C# 0.0002 ms, Rust FFI 0.0002 ms, Rust IPC 0.14 ms. IPC only pays off to isolate a failure, not for speed.
- Default calibration (measured, 3 campaigns): Rust FFI from n=100 for nearest, match, duplicates and segment_contacts; transform3 and components stay in C# (Rust does not win at every size). On the PC it is recalibrated with `power-engine calibrar`.
- Documents: DOCX .NET 0.19 ms versus Python 0.92 ms (the dispatcher uses .NET if there is no Python); Python startup plus first job 71 ms; synthetic single-sheet PDF 28–34 ms per job.
- Full PDF→plan workflow (two Python jobs + registration + plan) in the tests: seconds of suite time, no isolated measurement on the PC.

## 5. Token efficiency (ESTIMATED = characters/4; not real client tokens)

| Case | Before | Now | Reduction |
|---|---|---|---|
| Discovery: full catalog versus search + one card | 2,253 | 402 | 82 % |
| Placing 72 lighting fixtures: 72 separate commands versus one batch | 10,260 characters, 72 calls | 959 characters, 1 call | 91 % of characters, 99 % of calls |
| `power flujo`: response without one-by-one items (summary, ambiguous items and ops only) | — | < 20,000 characters for 72 elements | — |
| Fixed cost of the `power` tool in the tool list | 6,778 characters | ≈ 7,400 | +9 % |

Verification is not trimmed to save tokens: the diff is compact, but every difference is checked.

## 6. Limitations

- No operation has run in Revit for 0.9.0; placement, tags and native reads may differ by version.
- PDF: vector only. Scanned → `SIN_CONTENIDO` (vision-based interpretation requires authorization). Tables without rules are not detected. Symbols are candidates, not confirmed lighting fixtures.
- Registration: needs grids with bubbles and the same name in the drawing and the model; curved grids are not anchors.
- `mep.riser`, `groups.variants` and `annot.layout` only as a library (no `power` command yet); `annot.layout` is EXPERIMENTAL.
- The Python interpreter is not packaged; AES-encrypted PDFs require `cryptography` on the machine.
- Benchmarks measured on Linux; the Windows PC may give other thresholds.
- B1–B8 versus pyRevit: NO_MEDIDO (not measured).

## 7. Next phase

1. With authorization: N9 campaign on a disposable copy and benchmark on the PC.
2. `power` commands for risers, groups and annotations, and native reading of grids and conduit integrated into `flujo`.
3. Tables without rules and reading of multi-line legends.
4. B1–B8 versus pyRevit require an isolated environment, identified versions and comparable workloads.
