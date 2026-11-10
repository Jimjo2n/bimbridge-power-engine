# Development status

## Current candidate

**BIMBRIDGE – Power Engine 0.9.0** is a development candidate with native acceptance pending. No release tag is assigned by this document.

The implementation uses C# for the Revit integration and orchestration, Rust for geometry through FFI and IPC, and Python for document processing. Workers use the versioned `pe=1` contract and do not directly access Revit.

## Implemented areas

- Local MCP bridge, per-client sessions and agent identity.
- Declarative model queries, compact tables, result digests and timing.
- Typed plans, conflict detection, durable write intent and reconciliation.
- Dynamic C# execution with configurable permissions and an explicit blocking control.
- Rust geometry kernels and Python document extraction.
- Windows installer, backup and recovery procedures.
- Operational status panel and session metrics.

See [architecture](docs/0.9.0/POWER_ENGINE_ARCHITECTURE.md), [capabilities](docs/MATRIZ_CAPACIDADES.md) and [architecture decisions](docs/0.9.0/ARCHITECTURE_DECISIONS.md).

## Validation

Offline tests and native acceptance are separate. Historical reports under `docs/` describe their recorded source snapshots; they do not certify a later import or binary package.

The repository preparation verified Rust unit/IPC tests, Python document-worker tests and a selected C# regression suite. A full distribution build, hosted CI and native acceptance remain release requirements. Current evidence and limitations must be recorded against the exact source commit.

Native acceptance uses an isolated Revit instance and disposable, authorized models. Procedures: [native validation](docs/0.9.0/NATIVE_VALIDATION.md) and [0.9.0 acceptance](tools/aceptacion/0.9.0/PROCEDIMIENTO_0.9.0.md).

## Open work

- Complete native acceptance for the distribution being reviewed.
- Measure end-to-end latency using comparable cold/warm workloads.
- Validate document inference against additional public or synthetic fixtures.
- Review resolved third-party dependencies and distribution notices.
- Configure and verify hosted CI and branch protections after upload.

## Maintenance

Maintainer: [Jimjo2n](https://github.com/Jimjo2n). Contributions follow [CONTRIBUTING.md](CONTRIBUTING.md); reviewed releases follow [RELEASING.md](docs/RELEASING.md). The product version changes for consolidated accepted deliveries.

