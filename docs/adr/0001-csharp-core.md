# 0001 — C# as the integration core

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
The source implements Revit access, orchestration and durable state in C#/.NET.

## Decision
Keep C# responsible for Revit API access and coordination; specialized workers do not own the model.

## Alternatives
A core split across C#, Python and Rust would add ownership and IPC boundaries. The implemented architecture keeps orchestration in C# and limits workers to specialized tasks. This records the architectural tradeoff, not an undocumented historical meeting.
## Consequences
A single transaction and recovery authority is easier to reason about. Cross-language tasks still require explicit contracts.

## Evidence
`src/PDI.RevitMcp.Engine` in the verified a291cc6 source baseline, together with the current sanitized import.
