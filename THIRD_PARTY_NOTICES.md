# Third-party components

GPL-3.0-only applies to original BIMBRIDGE code. It does not replace third-party license terms.

## Existing dependency inventory
- .NET NuGet references are declared in individual `src/**/*.csproj` and `tests/**/*.csproj`: MCP SDK, Microsoft hosting/Roslyn and xUnit among others.
- Rust direct dependency: serde_json. Exact resolved versions and transitive packages: `native/power_engine/Cargo.lock`.
- Existing Python wheels: pdfminer.six 20260107 and charset-normalizer 3.5.2. The repository lock labels both MIT; preserve and inspect their embedded license notices when packaging.
- Revit API reference packages or installed Autodesk DLLs are compile-time inputs. Do not upload Autodesk API binaries as source or release assets.

Before distributing a release, inventory resolved NuGet, Cargo and wheel contents and carry applicable notices into the package. This initial inventory is not a completed transitive dependency review. Keep that review and artifact inspection in the release acceptance record.
