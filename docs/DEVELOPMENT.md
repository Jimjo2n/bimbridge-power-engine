# Development

## Prerequisites
The repository selects SDK 8.0.427 through `global.json`, allowing later patches in the same feature band and excluding prereleases. Installing .NET 8 without selecting it is insufficient on a machine that also has newer SDKs. See [Microsoft SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json). CI reads this file when installing the SDK.
The .NET SDK supplies the CLI/MSBuild used by the scripts. Visual Studio is optional for editing/debugging. The net48 projects use Microsoft.NETFramework.ReferenceAssemblies packages; see [installation](getting-started/INSTALLATION.md) for build inputs and offline-cache requirements.
Windows, Git, .NET 8 SDK, Rust x64 with Cargo, and CPython 3. Install .NET Framework 4.8 targeting support for Revit 2022–2024. The add-in targets .NET 8 for Revit 2025–2026. Reference packages can compile the add-in without an installed Revit; they do not prove native compatibility.

## Checks without Revit
In a clean interpreter/checkout, first run `python tools/repository/prepare_python_tests.py`. It reuses the existing packaging routine to verify and prepare the checked-in pure wheels in the ignored worker vendor directory, without downloading dependencies. Use a fresh checkout if that directory already exists. AES-encrypted PDF support remains limited by the packaged cryptography substitute; this is not a full cryptography installation.
```powershell
cargo test --locked --release --manifest-path native/power_engine/Cargo.toml
cargo build --locked --release --manifest-path native/power_engine/Cargo.toml
python -B -m unittest discover -s workers/python/tests -v
dotnet test tests/PDI.RevitMcp.Tests/PDI.RevitMcp.Tests.csproj -c Release
```

Some C# checks depend on previously compiled add-ins or native workers. The authoritative full packaging sequence is `build.ps1`: it builds add-ins for 2022–2026 before running the complete suite. Use a real CPython path:
```powershell
./build.ps1 -Python C:/path/to/python.exe
```

Build from committed changes; do not use the dirty-tree bypass for release evidence. Outputs go to ignored `dist/`. Do not commit generated binaries. Python wheels are existing pinned source dependencies verified by the vendor script against `workers/python/wheels/LOCK.txt`.

## Native verification
CI runs without a live Revit model. Native acceptance requires a separately authorized session and disposable test models. Record exact commit, Revit version, procedure, results and sanitized evidence. Do not turn old success reports into current acceptance. See `docs/0.9.0/NATIVE_VALIDATION.md`.

## Compatibility
Keep the current solution layout and assembly names unless a separate migration changes all references and is verified. Installer and update signing are distinct from GitHub release checksums; never store signing keys in Git.
