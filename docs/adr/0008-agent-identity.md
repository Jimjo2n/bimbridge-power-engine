# 0008 — Declared aliases and verified principal

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
Sessions carry client metadata and agent aliases; local transport has a Windows-principal boundary.

## Decision
Separate attribution labels from authenticated identity and keep per-session state.

## Alternatives
Alternatives may be proposed through a reviewed change with compatibility and validation evidence. No undocumented historical comparison is asserted by this record.

## Consequences
Aliases improve observability but must not grant privileges or be inherited from unrelated sessions.

## Evidence
`docs/0.9.0/AGENT_IDENTITY_PROTOCOL.md` in the verified a291cc6 source baseline, together with the current sanitized import.
