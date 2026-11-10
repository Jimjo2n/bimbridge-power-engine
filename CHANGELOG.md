# Changelog

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
