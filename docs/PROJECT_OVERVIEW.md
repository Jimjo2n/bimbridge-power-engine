# Project overview

BIMBRIDGE – Power Engine connects local AI clients to Autodesk Revit through MCP. It combines a Revit add-in, a stdio bridge, workflow orchestration and specialized geometry/document workers.

Maintainer: [Jimjo2n](https://github.com/Jimjo2n).

## Design

Revit API operations execute through the add-in dispatcher on Revit's main thread. The bridge translates MCP requests into the local protocol, and the engine manages sessions, queries, execution state and durable records.

Typed writes separate preparation from application: a plan describes the proposed changes, and commit applies a reviewed plan with conflict and recovery checks. General C# execution supports broader API access and has different trust boundaries, documented in the execution policy.

Rust provides geometry operations through FFI or IPC. Python extracts structured information from documents. These workers have narrow responsibilities; orchestration and model access stay in C#.

## Reliability

The system records write intent before applying changes, checks conflicts, tracks uncertain outcomes and requires reconciliation when evidence is incomplete. A rollback affects model transactions; it cannot undo arbitrary file or network side effects of general code.

Multiple clients have separate sessions and declared aliases. Client labels help attribution but are not verified identities. The Windows principal and local transport controls form part of the access boundary.

## Repository

- `src/`: C# implementation and installer projects.
- `native/power_engine/`: Rust library and IPC worker.
- `workers/python/`: document extraction.
- `tests/`: offline verification and simulated integration.
- `tools/`: acceptance harnesses, benchmarks and packaging utilities.
- `docs/`: architecture, protocols, operating procedures and historical reports.

Legacy assembly names remain for compatibility. Repository organization follows the actual solution rather than an aspirational directory layout.

## Reading order

1. [Development and build](DEVELOPMENT.md).
2. [Architecture](0.9.0/POWER_ENGINE_ARCHITECTURE.md).
3. [Capabilities](MATRIZ_CAPACIDADES.md).
4. [Execution policy](0.9.0/DYNAMIC_CODE_EXECUTION_POLICY.md).
5. [Native validation](0.9.0/NATIVE_VALIDATION.md).
6. [Contribution and review process](../CONTRIBUTING.md).

The candidate remains at 0.9.0 with native acceptance pending. A successful offline suite does not establish production readiness.

