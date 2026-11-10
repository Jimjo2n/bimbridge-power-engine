# 0006 — Dynamic C# execution

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
The current implementation enables dynamic C# for authorized local agents and provides a persistent blocking control.

## Decision
Document the actual default and trust boundary; do not label it a sandbox.

## Alternatives
Typed operations alone offer a narrower fixed contract; general C# offers broader Revit API access with user-level privileges. Blocking dynamic code is available without treating typed writes and arbitrary code as equivalent trust surfaces.
## Consequences
User-level file/network privileges and external side effects exceed document rollback guarantees. Typed operations retain a narrower contract.

## Evidence
`docs/0.9.0/DYNAMIC_CODE_EXECUTION_POLICY.md` in the verified a291cc6 source baseline, together with the current sanitized import.
