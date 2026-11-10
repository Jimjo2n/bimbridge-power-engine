# 0002 — MCP client boundary

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
The bridge registers tools and transports requests over stdio.

## Decision
Expose client-facing operations through MCP and isolate protocol stdout from diagnostics.

## Alternatives
Client-specific direct integrations would couple tool definitions to individual clients. MCP provides a shared client-facing boundary, while the local Revit transport remains an implementation concern.
## Consequences
Different client profiles require explicit registration and schema compatibility. Client metadata remains declared.

## Evidence
`src/PDI.RevitMcp.Bridge` in the verified a291cc6 source baseline, together with the current sanitized import.
