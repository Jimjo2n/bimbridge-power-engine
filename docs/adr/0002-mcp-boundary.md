# 0002 — MCP client boundary

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
The bridge registers tools and transports requests over stdio.

## Decision
Expose client-facing operations through MCP and isolate protocol stdout from diagnostics.

## Alternatives
Alternatives may be proposed through a reviewed change with compatibility and validation evidence. No undocumented historical comparison is asserted by this record.

## Consequences
Different client profiles require explicit registration and schema compatibility. Client metadata remains declared.

## Evidence
`src/PDI.RevitMcp.Bridge` in the verified a291cc6 source baseline, together with the current sanitized import.
