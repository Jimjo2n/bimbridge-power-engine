# 0.9.1 — performance hardening (final round)

Everything measured here is **offline** (outside the Revit thread, `tools/bench/WorkflowBench`). What is expected in Revit is marked **hypothesis**: Revit has not been run.
Figures from this machine (4 CPUs, other load in parallel): they are indicative, not performance results against pyRevit.

## A. `RunMain()` consumers (static profile)

| Path | Thread visits | Dominant cost in Revit | Frequency | Optimizable? | Risk | Status |
|---|---|---|---|---|---|---|
| `query` (QueryRunner) | 1 + slices | `doc.GetElement` per candidate; parameter reads; type and level per instance | very high | yes: cache by type/level/target id and data class | low (lives for one query) | **done** |
| `check lighting_audit` | 1 + slices | level and workset per fixture; view name/level per tag | medium | yes: cache by id | low | **done** |
| `check model_health` | 1 per phase | `GetWarnings`, type per instance, one collector per sheet | medium | yes: type/level by id, schedules per sheet in one pass, chunked phases | low | **done** (AUD-17 + this round) |
| `check copy_monitor` | 1 | source and host collectors | low (per attempt) | partly: already a single visit | low | unchanged (already 1 visit) |
| `geo` (GeoRunner) | 1–2 + slices | geometry and meshes | medium | already has a per-revision cache and reused meshes | — | unchanged |
| `plan` without geometry | 1 | type/level resolution (`ModelIndex` cached per planning) | high | already in one visit | — | unchanged |
| `commit` | 1 group + batches | transactions per batch; `Regenerate` | high | see D | high (integrity) | unchanged (see D) |
| `doc pdf/ifc/csv/guardar/sync` | 1 + wait | I/O and export | low | no: Revit work | — | now go through the scheduler |
| `exec` | 1 | compilation and execution | medium | existing compilation cache | — | unchanged |

What was NOT touched, and why: `commit`/`exec` stay on the Revit thread's own queue (it has not been shown that the scheduler preserves write ordering and the existing locks; see §G).

## B. Changes per path (bottleneck → change → risk → measurement → expected impact)

| File / method | Bottleneck | Change | Risk | Offline measurement | Impact in Revit |
|---|---|---|---|---|---|
| `ElementFields.ParamValue/ReadOne` | `GetDataType()` per value; `doc.GetElement` of the type per instance for type parameters; repeated target name | per-invocation cache: data class per `Definition`, type element by id, target by id; hit/miss counters | low: lifetime of one query, inside the Revit thread, never crosses documents or revisions | not measurable outside Revit (needs the API) | **hypothesis**: fewer `GetElement`/`GetDataType` calls, proportional to instances/types; measure in BM-01/BM-02 |
| `ModelHealthProbe` (phase 1) | up to 3 `GetElement(typeId)` per instance + 1 per required non-instance parameter + level per instance | `TypeMeta` and `LevelName` by id; type parameter values resolved once per type | low | not measurable outside Revit | **hypothesis**: from O(instances) to O(types) in type accesses |
| `LightingAuditRunner` | level and workset per fixture; view per tag | level cache (NameCache), workset by id, view by id | low | not measurable outside Revit | **hypothesis**: same |
| `ModelHealth.Duplicates` | quadratic `List.Contains` in very dense groups; non-finite/huge coordinates overflowed the quantization | membership `HashSet`; exclusion of non-finite values and \|x\|≥1e9 | low (tests) | group of 20,000 coincident: **42.6 ms → 4.7 ms (9.0×)**; sparse models of 10k–50k: unchanged (0.9–1.0×, within noise) | none in Revit (runs outside the thread) |
| `JobStore.Complete` / `DocOp` | — (AUD-18, a fix, not an optimization) | final status decided before notifying | low | `OperationHistoryTests` | — |
| `OperationScheduler` in `DocOp` | document operations without ordering between agents | per-document exclusion and fair sharing (see BM-09) | medium: does not touch commit/exec | BM-09 | **hypothesis** |

Verified and **unchanged** on purpose:
- `Document.Regenerate()` (3 calls in `PlanApplier`): only after `FamilySymbol.Activate()`, conditioned on `!IsActive`; Revit requires regeneration before placing. They are not redundant; they are not removed.
- `PlanApplier` transactions: one per operation (or per sub-batch of N changes) inside ONE assimilated `TransactionGroup`; they are already per batch. Merging further would break per-batch recovery (checkpoint of ids before each batch). Not touched.
- `fsync` of the audit and checkpoints: not reduced (durability guarantees).

## C. Cache and budget

| Cache | Key | Scope | Invalidation | Budget | Metrics |
|---|---|---|---|---|---|
| `ElementFields.NameCache` (new: type, target, data class) | type id / target id / `Definition` | one query, Revit thread | dies with the query (revision and document fixed during it) | bounded by the number of distinct types/definitions | observed `Hits`/`Misses` → `metricas` |
| `ModelHealthProbe` types and levels (new) | type id / level | one revision | dies with the probe | number of types and levels | `CacheHits`/`CacheMisses` |
| `QuerySnapshotStore` | session + document + revision | session | revision, 5 min TTL, close | 64 MB global / 32 MB per document / 4 per session | evictions with reason |
| `ResultCache` (geo) | doc + revision + signature | process | revision | 48 entries | hits/misses |
| `CompileCache` | code hash + references | process | assemblies are not unloaded | 1024 entries, per agent | per agent |
| `CapabilityIndex` | id/domain/… | process, immutable | content fingerprint | catalog | — |

No new cache is global or keeps Revit objects between queries.

## D. Verification and writes
`Verify()` already rereads only what was planned (plan entries) and the created elements by id; `VerifyModelCreations` rereads each created element once. It is not reduced to "exists". Unchanged.

## E. Instrumentation (`WorkflowMetrics`)
Official names: `total_ms, queue_ms, revit_main_thread_ms, revit_slices, revit_max_slice_ms, api_calls, elements_read, elements_written, transactions, regenerations, serialization_ms, payload_bytes, worker_ms, cache_hits, cache_misses, verification_ms, audit_ms, allocations_bytes`.
**Observed** today: `total_ms, queue_ms, revit_main_thread_ms, revit_slices, revit_max_slice_ms` (from `MainThread`), `elements_read`, `elements_written`, `transactions` (0 for reads), `cache_hits/misses` (from the new caches). They are returned in `metricas` with `check name=model_health metricas=true`, `query metricas=true` (or `timing=true`) and `lighting_audit`.
**Not instrumented** (listed as such in the response): `api_calls` (no complete counter: it is not declared partial), `regenerations`, `serialization_ms`, `payload_bytes` in the add-in, `worker_ms`, `verification_ms`, `audit_ms`, `allocations_bytes`. `payload_bytes` and `serialization_ms` are measured in the offline harness.

## F. Benchmark readiness
- Offline harness: `dotnet tools/bench/WorkflowBench/bin/Release/net8.0/WorkflowBench.dll docs/0.9.1/benchmarks/workflow-bench.md docs/0.9.1/benchmarks/workflow-bench.json 7`. Results in those files (median, p95, throughput, allocation; before/after).
- Scenarios measured offline: BM-03 (report and duplicates), BM-06 (validation of N operations versus one composite: **no offline gain**; the expected gain is in thread visits and transactions, not measurable here), BM-07 (verification of the 88-device fixture and of 10,000), BM-09 (scheduler sharing with 1/2/4/8 agents).
- Scenarios that require Revit (BM-01, 02, 04, 05, 08, 10 and the native part of 03, 06, 07, 09): specified in `workloads_nativos` of the JSON with sizes, common input and correctness criterion.
- Comparison procedure against pyRevit (native campaign): same Revit, model, version and machine; same number of operations and functional verification (identical reread); separate (1) workflow time inside Revit, (2) total time with MCP communication, (3) direct API work, (4) human interaction and (5) our own instrumentation; do not attribute LLM latency to pyRevit; a reasonably written pyRevit baseline (batch reads, one transaction per batch). Report median, p95, peak memory, throughput, speedup, correctness, regressions and functional differences. The 2–5× targets are **hypotheses**.

## G. Scheduler
The scheduler only governs document operations. Extending it to `commit`/`exec` would require showing that it respects write ordering, transaction policies and locks (`GuardDocLock`, `PlanStore`'s `BeginWrite`, the `MainThread` queue); without that proof it is not extended. Current lock order: scheduler (exclusive key `doc:<id>`) → Revit thread → document lock; no path takes them in reverse order. Timings: BM-09 shows that 2–8 agents with 5 ms of synthetic work finish in ≈341 ms versus ≈499 ms with 1 (per-agent limit 2; global 4).
Pending: separate timings for scheduler wait, thread wait, execution, verification and persistence (today `queue_ms` is the thread wait; the scheduler wait is not recorded yet).
