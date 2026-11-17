<img src="docs/assets/github-cover.svg" width="1120" height="270" alt="">

# BIMBRIDGE · Power Engine

**Local BIM automation and AI integration for Autodesk Revit.** Connect an MCP client to inspect models, review proposed changes, and run Revit workflows from your Windows workstation.

[Español](README.es.md) · [Installation](docs/getting-started/INSTALLATION.md) · [Connect a client](docs/integrations/MCP.md) · [Examples](examples/README.md) · [Documentation](docs/INDEX.md)

> [!NOTE]
> **Experimental releases.** The source targets Revit 2022–2026. Consult the validation record for the operations and Revit environments tested.
> [Releases](https://github.com/Jimjo2n/bimbridge-power-engine/releases) · [Validation](docs/quality/VALIDATION.md)

## See BIMBRIDGE at work

The illustrations use sample data. BIMBRIDGE runs on your Windows workstation. The MCP client sends requests to a local C# bridge, and the Revit add-in runs model operations through the Revit API.

### 01 · Inspect the model

<img src="docs/assets/github-query.svg" width="430" height="210" alt="Illustrated floor plan with six highlighted fixtures and a result of six">

Ask for a lighting fixture count with `q LF n=true` in the compact MCP profile. BIMBRIDGE reads the selected Revit document and returns the count. You can also filter elements and request compact results. This query leaves model elements unchanged.

### 02 · Review a change before applying it

<img src="docs/assets/github-review.svg" width="430" height="210" alt="Example of a proposed parameter change from mark L-01 to L-02">

Select the target document and prepare a typed plan. The plan lets you inspect what would change, such as a fixture's **Mark** parameter. Once reviewed, commit the plan. BIMBRIDGE checks for conflicts and uses a Revit transaction to apply the changes. See the [plan and commit recipes](docs/RECETAS.md).

### 03 · Process geometry and documents

<img src="docs/assets/github-workers.svg" width="430" height="210" alt="Wireframe geometry beside a document with PDF and DOCX labels">

The workflow host can use Rust for geometry operations and Python to extract structured content from PDFs and DOCX files. The C# add-in handles Revit model access. See the [component overview](docs/PROJECT_OVERVIEW.md).


> [!IMPORTANT]
> Dynamic C# runs with the Revit user's privileges. Use the [trust model](docs/security/TRUST_MODEL.md) to understand execution controls and the effects that Revit transactions can reverse.

## Get started

1. **Install on Windows.** Use the [installation guide](docs/getting-started/INSTALLATION.md) and review the status of the package you select.
2. **Connect your MCP client.** Follow the [integration guide](docs/integrations/MCP.md), then try a query with the [examples](examples/README.md).
3. **Build from source.** See [development and build](docs/DEVELOPMENT.md) for SDK requirements, the build command and test instructions. The Revit 2022–2024 targets use .NET Framework 4.8. The 2025–2026 targets use .NET 8.

## Project evidence and maintenance

| If you want to… | Start here |
| --- | --- |
| Review what has been tested | [Validation](docs/quality/VALIDATION.md) · [CI scope](docs/quality/CI.md) |
| Assess performance | [Benchmarks and methodology](docs/performance/BENCHMARKS.md) |
| Follow work in progress | [Roadmap](docs/roadmap/BACKLOG.md) · [Development state](DEVELOPMENT_STATE.md) |
| Understand the source history | [Repository provenance](docs/REPOSITORY_SETUP.md) |
| Contribute or report a problem | [Contributing](CONTRIBUTING.md) · [Security reporting](SECURITY.md) |

Maintained by [Jose Jimenez](https://github.com/Jimjo2n). First-party code is [GPL-3.0-only](LICENSE). See [third-party notices](THIRD_PARTY_NOTICES.md) for bundled components.
