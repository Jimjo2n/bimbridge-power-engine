# 0003 — Revit dispatcher

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
Revit API operations run through the add-in dispatcher and ExternalEvent context.

## Decision
Schedule model operations in the supported Revit context rather than arbitrary worker threads.

## Alternatives
Alternatives may be proposed through a reviewed change with compatibility and validation evidence. No undocumented historical comparison is asserted by this record.

## Consequences
Long work may affect UI responsiveness; cancellation and batching must respect transaction boundaries.

## Evidence
`src/PDI.RevitMcp.Addin/Revit/MainThread.cs` in the verified a291cc6 source baseline, together with the current sanitized import.
