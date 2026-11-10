# 0004 — Rust FFI and IPC

Status: recorded during repository preparation, 2026-11-10. This is a source-backed record, not a claim about the date of an original design discussion.

## Context
Geometry kernels are exposed as a native library and an IPC worker.

## Decision
Keep both supported boundaries and choose based on workload and measured benefit.

## Alternatives
Alternatives may be proposed through a reviewed change with compatibility and validation evidence. No undocumented historical comparison is asserted by this record.

## Consequences
FFI reduces some transport costs but shares a process boundary; IPC has serialization and lifecycle costs.

## Evidence
`native/power_engine/Cargo.toml` in the verified a291cc6 source baseline, together with the current sanitized import.
