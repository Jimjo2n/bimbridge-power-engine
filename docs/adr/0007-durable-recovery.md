# 0007 — Durable write recovery

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
Plans, write intent, journal state and reconciliation exist in the source.

## Decision
Treat missing evidence as uncertainty, check conflicts and reconcile rather than blindly repeating writes.

## Alternatives
In-memory state alone would disappear at process exit. Blind retries after lost responses can duplicate effects. The implemented journal/reconciliation design instead retains uncertainty until document evidence resolves it.
## Consequences
Disk failures may block new writes. A timeout does not imply rollback or that an operation stopped.

## Evidence
`src/PDI.RevitMcp.Engine` in the verified a291cc6 source baseline, together with the current sanitized import.
