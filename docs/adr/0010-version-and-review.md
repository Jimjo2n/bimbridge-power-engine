# 0010 — Consolidated versions and reviewed releases

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
The baseline remains 0.9.0 with partial native acceptance.

## Decision
Use 0.9.0+commit during consolidation, reviewed PRs and manually prepared draft releases. Reserve 1.0.0 for product acceptance.

## Alternatives
A version for every commit would fragment consolidated acceptance. Automatic publication would remove the explicit distribution review. The implemented policy uses commit identifiers during development and manually reviewed draft releases.
## Consequences
A Git tag, compiled package and accepted release are different artifacts. No workflow changes visibility.

## Evidence
`docs/RELEASING.md` in the verified a291cc6 source baseline, together with the current sanitized import.
