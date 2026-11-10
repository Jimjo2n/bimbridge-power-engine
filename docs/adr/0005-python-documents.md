# 0005 — Python document processing

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
PDF/DOCX work is isolated in a document worker with a versioned JSONL contract.

## Decision
Keep Python focused on documents with explicit authorized paths; Revit ownership stays in C#.

## Alternatives
Alternatives may be proposed through a reviewed change with compatibility and validation evidence. No undocumented historical comparison is asserted by this record.

## Consequences
CPython selection, encoding and dependency notices are release concerns. PDF features are constrained by packaged dependencies.

## Evidence
`workers/python` in the verified a291cc6 source baseline, together with the current sanitized import.
