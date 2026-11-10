# 0003 — Revit dispatcher

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
Revit API operations run through the add-in dispatcher and ExternalEvent context.

## Decision
Schedule model operations in the supported Revit context rather than arbitrary worker threads.

## Alternatives
Arbitrary background-thread Revit calls are outside the supported execution model. UI automation would couple operations to window state. The implemented dispatcher uses Revit's API execution context.
## Consequences
Long work may affect UI responsiveness; cancellation and batching must respect transaction boundaries.

## Evidence
`src/PDI.RevitMcp.Addin/Revit/MainThread.cs` in the verified a291cc6 source baseline, together with the current sanitized import.
