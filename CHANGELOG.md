# Changelog

## Unreleased — consolidated engineering delivery (0.9.0+commit)
Branch `feat/power-engine-consolidation-0.9.0`. Decisions D45–D50 in [ARCHITECTURE_DECISIONS](docs/0.9.0/ARCHITECTURE_DECISIONS.md).
- fix(engine) #21: grant-key reads tolerate transient Windows sharing violations within a bounded window; other I/O errors fail closed without the secret; key creation errors are typed.
- fix(engine) #1: the idle threshold no longer closes sessions with in-flight requests; idle and broken connections are still cleaned up (reason `inactividad`).
- perf(query) #2: full paginated reads materialize once; later pages are served from a revision-bound snapshot via `cursor=` (session- and document-bound, TTL, memory budget, stale cursors rejected).
- fix(engine) #7: background jobs are explicitly supervised (`JobStore.Run` returns void); CS4014 is a build error.
- feat(agents) #3/#4: operating tools require a named agent; duplicate aliases rejected; `ctx desregistrar=true`; correlation ids (agent_id, session_id, request_id, trace_id, job_id, transaction_id) on every journal line.

## Unreleased — repository foundation
- Adopt GPL-3.0-only for original code.
- Document the existing architecture, contribution process and repository provenance.
- Add offline CI, dependency updates and manually requested draft releases.
- Keep product version 0.9.0 and native acceptance pending.

Earlier development: [0.9.0 changelog](docs/0.9.0/CHANGELOG.md) and versioned documents under `docs/`.
