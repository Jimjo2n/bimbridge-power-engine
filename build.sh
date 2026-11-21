#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
# Builds and tests everything, and assembles dist/ (Linux/macOS or WSL). On Windows use build.ps1.
set -euo pipefail
cd "$(dirname "$0")"
# Reproducible (beta1): only from a clean, committed tree; the embedded commit is exactly HEAD.
# BIMBRIDGE_ALLOW_DIRTY=1 allows a development build, but that dist/ is not distributed.
if [ -n "$(git status --porcelain --untracked-files=no)" ] && [ "${BIMBRIDGE_ALLOW_DIRTY:-0}" != "1" ]; then
  echo "build.sh: there are uncommitted changes; commit before assembling dist/ (or BIMBRIDGE_ALLOW_DIRTY=1 for development)." >&2; exit 2
fi
export SourceRevisionId="$(git rev-parse HEAD)"
rm -rf dist && mkdir -p dist
# Compiler for the exec runner (Roslyn): goes in addin/<v>/Bimbridge/roslyn/ and is only loaded on the first exec.
dotnet build src/Bimbridge.Scripting -c Release -v q
ROSLYN=src/Bimbridge.Scripting/bin/Release/netstandard2.0
for v in 2022 2023 2024 2025 2026 2027; do
  mkdir -p dist/addin/$v/Bimbridge
  if [ "$v" -ge 2025 ]; then
    # Revit 2025–2027: ReadyToRun (native x64 code + fallback IL) for that year's runtime.
    rm -rf obj/r2r_$v
    dotnet publish src/Bimbridge.Addin -c Release -p:RevitVersion=$v -r win-x64 --self-contained false \
      -p:PublishReadyToRun=true -o obj/r2r_$v -v q
    cp obj/r2r_$v/Bimbridge.*.dll dist/addin/$v/Bimbridge/
  else
    # Revit 2022–2024 (.NET Framework 4.8): IL; the add-in precompiles the engine in the background at startup (Warmup).
    dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=$v -v q
    cp src/Bimbridge.Addin/bin/Release/R$v/Bimbridge.*.dll dist/addin/$v/Bimbridge/
  fi
  cp src/Bimbridge.Addin/Bimbridge.addin dist/addin/$v/
  # bundled recipes (0.6.0): tarea iniciar receta=<name>; the user's own in %LOCALAPPDATA%\Bimbridge\recetas take precedence
  mkdir -p dist/addin/$v/Bimbridge/recipes
  cp src/Bimbridge.Addin/recipes/*.cs dist/addin/$v/Bimbridge/recipes/
  mkdir -p dist/addin/$v/Bimbridge/roslyn
  cp $ROSLYN/*.dll $ROSLYN/Bimbridge.Scripting.pdb dist/addin/$v/Bimbridge/roslyn/
  # the compiler does NOT go next to the add-in: only in roslyn/ (otherwise .NET would load it in the wrong context)
  rm -f dist/addin/$v/Bimbridge/Bimbridge.Scripting.dll
done
# Tests AFTER building the add-ins: the exec tests compile scripts against each year's add-in.
dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=2025 -v q
dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=2026 -v q
dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=2027 -v q
dotnet test tests/Bimbridge.Tests
dotnet publish src/Bimbridge.Bridge -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist/bridge
# 0.9.0: BIMBRIDGE Power Engine next to the bridge and the AgentHost. Rust (geometry): DLL for FFI + IPC executable, built for
# Windows x64 when the toolchain is present; without them the C# core does everything (controlled degradation). Python (documents): worker code +
# pure dependencies verified by SHA-256; the interpreter is NOT packaged (config.json → power_engine.python or python.exe from PATH).
PE=dist/bridge/power_engine
mkdir -p $PE
if command -v cargo >/dev/null && rustup target list --installed 2>/dev/null | grep -q x86_64-pc-windows-gnu; then
  (cd native/power_engine && cargo test --release -q && cargo build --release -q --target x86_64-pc-windows-gnu)
  cp native/power_engine/target/x86_64-pc-windows-gnu/release/bimbridge_power_engine.dll dist/bridge/
  cp native/power_engine/target/x86_64-pc-windows-gnu/release/bimbridge-power-engine.exe $PE/
else
  echo "build.sh: no Rust toolchain for Windows; the Power Engine will use only C# for geometry" >&2
fi
python3 -I tools/power_engine/vendor_python.py $PE/python
python3 -B -m unittest discover -s workers/python/tests -q
# alpha3: self-contained AgentHost (CLI `revit nrt04` for native acceptance; not installed in Revit)
dotnet publish src/Bimbridge.AgentHost -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist/agenthost
[ -f dist/bridge/bimbridge_power_engine.dll ] && cp dist/bridge/bimbridge_power_engine.dll dist/agenthost/
cp -r $PE dist/agenthost/

# Double-click installer: payload.zip (add-in + bridge) inside Bimbridge.Setup.exe
rm -f src/Bimbridge.Setup/payload.zip
# manifest.json: SHA-256 of every file; the installer verifies it before and after copying (0.6.0)
VERSION=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' Directory.Build.props)
(cd dist && { printf '{"version":"%s","archivos":{' "$VERSION"; first=1; \
  find addin bridge -type f | LC_ALL=C sort | while read -r f; do h=$(sha256sum "$f" | cut -d' ' -f1); \
    if [ $first = 1 ]; then first=0; else printf ','; fi; printf '"%s":"%s"' "$f" "$h"; done; printf '}}'; } > manifest.json)
(cd dist && zip -qr ../src/Bimbridge.Setup/payload.zip addin bridge manifest.json)
dotnet build src/Bimbridge.Setup -c Release -v q
cp src/Bimbridge.Setup/bin/Release/net48/Bimbridge.Setup.exe dist/
echo "Installer: dist/Bimbridge.Setup.exe"
# 0.8.0: package for "Update from ZIP" (manifest with SHA-256; signed only if BIMBRIDGE_UPDATE_KEY points to the IT private key).
FIRMA=()
if [ -n "${BIMBRIDGE_UPDATE_KEY:-}" ]; then FIRMA=(--clave-privada "$BIMBRIDGE_UPDATE_KEY"); fi
dotnet run -c Release --project src/Bimbridge.AgentHost -- paquete-actualizacion --salida "dist/BIMBRIDGE-$VERSION-actualizacion.zip" \
  --setup dist/Bimbridge.Setup.exe --version "$VERSION" --commit "$SourceRevisionId" --desde-minima 0.7.0 "${FIRMA[@]}" > dist/actualizacion-manifiesto.json
echo "Update: dist/BIMBRIDGE-$VERSION-actualizacion.zip"
