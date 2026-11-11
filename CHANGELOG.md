# Changelog

## Unreleased — 0.9.1 functional coverage candidate (0.9.0+commit)
Branch `feat/revit-functional-coverage-0.9.1`. Status and decisions D54–D62 in [DELIVERY_STATUS](docs/0.9.1/DELIVERY_STATUS.md). No native campaign yet: nothing is NATIVE_VERIFIED.
- fix(audit) AUD-01/01B: batch-aware head recovery, durable claims, tail repair into fragments; crash-point tests for batches of 1–64.
- fix(query) AUD-02/03: snapshot revalidation after LiveRev; incremental projection within budget.
- fix(agenthost) AUD-04 #28: deterministic reproduction; crash injection separated from lease expiry.
- fix(scripting) AUD-05: ref-counted compile gates, exact entry limit, per-agent accounting.
- feat(coverage): offline Revit API inventory 2022–2026 and capability catalog F01–F37 with registry.
- feat(write): typed level/grid/wall/floor/ceiling/room/duct/pipe/circuit/schedule creation with offline validation and `verificacion_creacion` (44 ops).
- feat(workflows) #23–#25: evidence-driven receipts, idempotent attempt keys, reconciliation, native capability descriptors, `check copy_monitor` (read-only preflight, manual handoff, verification), stable multi-Revit targeting, connection diagnostics.
- feat(export): `doc ifc` (transaction always rolled back) and `doc tabla_csv`.
- fix AUD-06…AUD-11: non-finite verification values, cross-process receipts with privileged evidence, Copy/Monitor dimensions and manual-handoff continuity, commit vs postconditions, durable tail-repair fragment, full link transforms.
- feat(inventory): every public RevitAPI/RevitAPIUI symbol with full signatures per version, deterministic gzip listings with SHA-256 traceability and version diffs.
- feat(coverage): immutable capability index, `check name=cobertura` paged discovery, offline benchmarks up to 10,000 entries; 222 capabilities; all 596 PostableCommands linked (189 UNKNOWN with recorded pending research, 48 NOT_APPLICABLE).
- feat(qa): `check name=model_health` (warnings, duplicates, required parameters, free MEP connectors, documentation).
- feat(ops): persistent operation history with filters, cancellation and safe-retry advice (`check name=historial`).

## Unreleased — consolidated engineering delivery (0.9.0+commit)
Branch `feat/power-engine-consolidation-0.9.0`. Decisions D45–D53 in [ARCHITECTURE_DECISIONS](docs/0.9.0/ARCHITECTURE_DECISIONS.md).
- fix(engine) #21: grant-key reads tolerate transient Windows sharing violations within a bounded window; other I/O errors fail closed without the secret; key creation errors are typed.
- fix(engine) #1: the idle threshold no longer closes sessions with in-flight requests; idle and broken connections are still cleaned up (reason `inactividad`).
- perf(query) #2: full paginated reads materialize once; later pages are served from a revision-bound snapshot via `cursor=` (session- and document-bound, TTL, stale cursors rejected).
- fix(engine) #7: background jobs are explicitly supervised (`JobStore.Run` returns void); CS4014 is a build error.
- feat(agents) #3/#4: with `identidad_obligatoria=true` an unnamed session may only send hello, ping and the registration (`ctx agente=…`); reads and writes require a named agent; duplicate aliases rejected; `ctx desregistrar=true`; legacy mode marks anonymous operations; correlation ids (agent_id, session_id, request_id, trace_id, job_id, transaction_id) on every journal line.
- fix(engine): session ids are unique per session instance (64-bit random suffix, never reissued), so request ids cannot repeat after a reconnect with the same pid.
- fix(query): snapshot memory budget in estimated bytes over a compact projection (64 MB global, 32 MB per document, 4 per session, 5 min TTL); oversized results and evicted cursors are reported to the agent, never dropped silently. The figure is an estimate, not a heap measurement.
- fix(audit) #27: cross-process group commit for the audit journal. Root cause confirmed from the failing Windows run: one of eight writers exhausted the 10 s lock wait. Pending entries are written in one append, one fsync and one head update, and each writer is confirmed only after persistence. The 10 s limit is unchanged. Local measurement (same harness): 8×50 appends 21.7 s → 9.3 s, lock-wait p95 1.3–1.8 s → 0.14–0.26 s; 8×1500 438.8 s → 274.9 s.
- test(concurrency) #27: the eight-process audit test supervises children with a single deadline, bounded stream reads, tree kill and disposal, verifies the chain even when a child fails, and checks exactly 50 lines per process; deterministic group-commit fault tests added.

## Unreleased — repository foundation
- Adopt GPL-3.0-only for original code.
- Document the existing architecture, contribution process and repository provenance.
- Add offline CI, dependency updates and manually requested draft releases.
- Keep product version 0.9.0 and native acceptance pending.

Earlier development: [0.9.0 changelog](docs/0.9.0/CHANGELOG.md) and versioned documents under `docs/`.
