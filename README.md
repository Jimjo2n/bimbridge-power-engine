<img src="docs/assets/github-cover.svg" width="1120" height="270" alt="">

# BIMBRIDGE · Power Engine

**Local runtime for agent-driven BIM automation in Autodesk Revit.** BIMBRIDGE exposes the Revit API through MCP over stdio and Windows named pipes. Its C# add-in dispatches API work on Revit’s main thread through `ExternalEvent`, while Power Engine prepares geometry, document data and typed plans outside Revit.

[Español](README.es.md) · [Installation](docs/getting-started/INSTALLATION.md) · [Connect a client](docs/integrations/MCP.md) · [Examples](examples/README.md) · [Documentation](docs/INDEX.md)

## Engineering highlights

### 01 · Revision-bound query snapshots

<img src="docs/assets/github-query.svg" width="430" height="210" alt="Model snapshot bound to a session, document and revision, serving multiple cursor pages">

`QuerySnapshotStore` materializes query rows once and serves later pages from memory. Each cursor is bound to its session, document and revision. A TTL and an LRU memory budget govern retention. The bridge and AgentHost carry the cursor across requests, preserving row-level results through pagination. [Implementation and verification](docs/0.9.0/CONSOLIDATED_DELIVERY_REPORT.md)

### 02 · Transactional writes with durable recovery

<img src="docs/assets/github-review.svg" width="430" height="210" alt="Review of a typed parameter change, with the previous and proposed values">

Typed `plan` and `commit` separate change preparation from execution. The add-in applies reviewed operations in Revit transactions with conflict checks and records write intent before execution. The recovery path uses reconciliation when the outcome is uncertain. Dynamic C# execution uses a Roslyn compilation cache with single-flight coordination to share compilation across concurrent requests for the same key. [Architecture and reliability](docs/PROJECT_OVERVIEW.md) · [Roslyn cache verification](docs/0.9.0/CONSOLIDATED_DELIVERY_REPORT.md)

### 03 · Native geometry and document workers

<img src="docs/assets/github-workers.svg" width="430" height="210" alt="Native geometry mesh beside PDF and DOCX inputs to the document worker">

`PowerEngineDispatcher` selects C# kernels or Rust geometry operations such as nearest-neighbor search, matching and segment contacts. Rust can run through FFI or IPC. The Python process handles PDF and DOCX extraction. The versioned `pe=1` process protocol uses JSON lines, capability negotiation, correlation IDs and job limits. Telemetry records engine, IPC, serialization and total time separately. [Engine architecture and protocol](docs/0.9.0/POWER_ENGINE_ARCHITECTURE.md)

### 04 · PDF-to-model registration and independent verification

<img src="docs/assets/github-registration.svg" width="430" height="210" alt="Matched grid anchors in PDF page coordinates and Revit model coordinates">

Python extracts grid intersections and symbol candidates in page coordinates. C# matches named anchors, fits a similarity or affine transform, checks registration residuals and sheet scale, and resolves placement into typed operations. `PlacementVerifier` compares against independent evidence and reports discrepancies in position, type, rotation, elevation and duplicate instances. [Registration and verification](docs/0.9.0/POWER_ENGINE_ARCHITECTURE.md)


## Project evidence and maintenance

| If you want to… | Start here |
| --- | --- |
| Review what has been tested | [Validation](docs/quality/VALIDATION.md) · [CI scope](docs/quality/CI.md) |
| Assess performance | [Benchmarks and methodology](docs/performance/BENCHMARKS.md) |
| Follow work in progress | [Roadmap](docs/roadmap/BACKLOG.md) · [Development state](DEVELOPMENT_STATE.md) |
| Understand the source history | [Repository provenance](docs/REPOSITORY_SETUP.md) |
| Contribute or report a problem | [Contributing](CONTRIBUTING.md) · [Security reporting](SECURITY.md) |

Maintained by [Jose Jimenez](https://github.com/Jimjo2n). First-party code is [GPL-3.0-only](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md) for bundled components.
