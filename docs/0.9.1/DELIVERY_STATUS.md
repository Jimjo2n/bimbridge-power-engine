# BIMBRIDGE 0.9.1 — delivery status (branch `feat/revit-functional-coverage-0.9.1`, PR #29)

Status: **candidate for a merge decision, without a native campaign**. Nothing is NATIVE_VERIFIED. The product version stays at 0.9.0+commit (#23).
The figures in this document come from `docs/0.9.1/capabilities/coverage.md` and `docs/0.9.1/inventory/revit-symbols-summary.json`, regenerated from the code at this commit.

## Inventory (source: generated registry)
- **Public API symbols** (RevitAPI + RevitAPIUI, Nice3point 2022.1.80 … 2026.4.10): 31,592 / 31,924 / 32,621 / 33,118 / 33,873 per version, deterministic gzip listings with the SHA-256 of the assembly and of the listing (`docs/0.9.1/inventory/revit-symbols-*`). Technical presence, no semantics.
- **Capabilities: 223**. OFFLINE_VERIFIED 13, IMPLEMENTED_UNVERIFIED 72, DYNAMIC_CSHARP_ONLY 92, MISSING 7, UNKNOWN 32, UNSUPPORTED_BY_PUBLIC_API 2, NOT_APPLICABLE 5.
- **Ribbon commands: 596**, all linked. By status: DYNAMIC_CSHARP_ONLY 256, IMPLEMENTED_UNVERIFIED 91, **UNKNOWN 189** (analyzed, with pending research in `commands.json`), NOT_APPLICABLE 48, UNSUPPORTED_BY_PUBLIC_API 4, MISSING 8. DYNAMIC_CSHARP_ONLY = verified public API exists, not full automation.
- **Discovery**: `check name=cobertura` (paginated compact rows; contract by id). Index benchmarks with the real catalog of 223: `docs/0.9.1/benchmarks/inventory-bench.md`.

## Branch functionality (actual status)
| Piece | Status |
|---|---|
| Typed modeling operations (level, grid, wall, floor, ceiling, room, duct, pipe, circuit, schedule) with verification of what was created | implemented; 44 operations in the catalog; effect in Revit pending the campaign |
| Copy/Monitor: preflight, manual handoff, verification | implemented read-only; **expectations frozen** per attempt (AUD-20); source→copy correspondence is **inferred**, not identity; no UI executor |
| Native workflow receipts | implemented with cross-process locking and privileged evidence |
| Export `doc ifc`, `doc tabla_csv` | implemented, go through the scheduler |
| `model_health`, `historial`, `cobertura` | implemented (read-only / in memory) |
| Long-operation scheduler | implemented; governs document operations; does **not** govern commit/exec (see PERFORMANCE_HARDENING §G) |
| Status panel #5 | "Long operations" section with filter and Cancel; WPF window without an automated test |
| PDF→Revit | plan → typed plans with a source witness; **effective idempotency** (AUD-19); applying it in Revit is left for the native campaign |
| Online update (requested by Abel) | implemented against public Releases with SHA-256 verification; **not tested with a real Release**: the repository has no Releases |

## Audit rounds
- **Second (AUD-12…AUD-17)**: see the PR history (inventory per attempt, history isolation, canonical fingerprint, benchmarks, chunked `model_health`).
- **Final (AUD-18…AUD-21)**:
  - AUD-18: `JobStore.Complete` decides `con_diferencias` before notifying; the deferred response, the `job_id` query and the history agree; advice `revisar_diferencias`. `OperationHistoryTests.JobFinishingWithDifferences_*`.
  - AUD-19: `SourceBinding` — every operation from a source carries a source witness (source SHA-256, revision, geometric registration, plan fingerprint, batch); the engine derives the key and content fingerprint when planning and applies them at commit on `OperationJournal`, even when `clave=` is not passed; a different `clave=` is rejected; `commit ops=` with a source is rejected. `SourceBindingTests` (identical attempts, reconnection, new plan_id, same PDF to another target/copy, modified PDF, interrupted commit, uncertain result, reconciliation).
  - AUD-20: the Copy/Monitor preflight freezes host, link and revision, source elements, transform, mapping, exclusions and category (expectations fingerprint); verify uses what was frozen and any change in the target is a conflict. `NativeWorkflowTests` (freezing, conflicts, tampering).
  - AUD-21: this document, the CHANGELOG, the matrix and the registry regenerated from source (223).
  - Performance: `docs/0.9.1/PERFORMANCE_HARDENING.md` (`RunMain` profile, caches, metrics, `tools/bench/WorkflowBench` harness).

## Decisions (D54–D66)
D54 modeling operations with verification of what was created · D55 native workflows by evidence · D56 Copy/Monitor without a batch copy API · D57 IFC with a rolled-back transaction · D58 full inventory in deterministic gzip · D59 immutable index and paginated discovery · D60 UNKNOWN with pending research, no promotions by name · D61 receipts with per-attempt locking and privileged evidence · D62 history with retry advice and per-agent scope ·
D63 **the final job status is decided in `JobStore.Complete`** (before notifying) · D64 **PDF→Revit idempotency on the existing operation journal**, with a source witness in the operations (no second system) · D65 **Copy/Monitor expectations frozen per attempt** and a conflict when the target changes · D66 **performance: reduce Revit-thread round trips with per-invocation caches** (not global, never crossing documents or revisions) and measure offline before claiming.

## Pending (explicit)
- 189 UNKNOWN commands; operations that require the UI (full Copy/Monitor, Coordination Review); elements without native verification (all of them).
- Real benchmarks against pyRevit: require Revit (native campaign, after the merge decision).
- Publishing 0.9.1 and testing the online update with a real Release.
- Single native campaign: requires authorization for an instance and a disposable model.
