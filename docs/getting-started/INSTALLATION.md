# Installation and configuration

This guide describes the source-inspected installation interface. No installer is executed during repository preparation.

## Build versus installation

The .NET 8 SDK provides the CLI/MSBuild used by build.ps1. The projects reference Microsoft.NETFramework.ReferenceAssemblies for net48 targets. Visual Studio is optional for editing/debugging; a successful CLI build does not require claiming a particular Visual Studio installation.

Revit reference packages are selected per target year in the add-in project. They are compile-time inputs and excluded from runtime distribution. A local Revit API path can instead be supplied through the existing build options. Offline rebuilding requires all required NuGet and Rust packages already cached; the checked-in Python wheels are hash-pinned.

## Reviewed package

Obtain a package tied to a reviewed commit, verify its published SHA-256 and inspect signing/acceptance status. The incoming audit installer is not an approved release and is not attached to this repository.

Close Revit before installation or replacement. Launch the reviewed Windows installer, choose the supported target versions and configure supported MCP clients. Existing installations require the explicit replacement flow. Silent installation is not a way around the closed-Revit or replacement checks.

The product is named BIMBRIDGE – Power Engine. Assembly names, add-in GUIDs and PDI.RevitMcp storage/configuration identifiers remain for compatibility.

## Worker setup

Configure a real CPython executable when PATH resolves only to a Windows application alias. The interpreter is not bundled. The Python worker uses authorized document paths; do not grant arbitrary access to private folders. Rust library/worker files are packaged by the existing build script.

## First use

Connect a supported MCP client, inspect its active profile and capabilities, confirm the target document and begin with reads on a disposable fixture. Understand the [dynamic-code trust model](../security/TRUST_MODEL.md) before enabling workflows that execute general code.

Native installation, UI behavior and recovery acceptance are distinct from compilation. Use the [native validation guide](../0.9.0/NATIVE_VALIDATION.md) and record exact source/package versions.
