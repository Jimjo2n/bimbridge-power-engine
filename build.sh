#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
# Compila y prueba todo, y arma dist/ (Linux/macOS o WSL). En Windows use build.ps1.
set -euo pipefail
cd "$(dirname "$0")"
# Reproducible (beta1): sólo desde un árbol limpio y confirmado; el commit embebido es exactamente HEAD.
# PDI_ALLOW_DIRTY=1 permite compilar para desarrollo, pero ese dist/ no se distribuye.
if [ -n "$(git status --porcelain --untracked-files=no)" ] && [ "${PDI_ALLOW_DIRTY:-0}" != "1" ]; then
  echo "build.sh: hay cambios sin confirmar; confirme antes de armar dist/ (o PDI_ALLOW_DIRTY=1 para desarrollo)." >&2; exit 2
fi
export SourceRevisionId="$(git rev-parse HEAD)"
rm -rf dist && mkdir -p dist
# Compilador del runner exec (Roslyn): va en addin/<v>/PDI.RevitMcp/roslyn/ y sólo se carga al primer exec.
dotnet build src/PDI.RevitMcp.Scripting -c Release -v q
ROSLYN=src/PDI.RevitMcp.Scripting/bin/Release/netstandard2.0
for v in 2022 2023 2024 2025 2026; do
  mkdir -p dist/addin/$v/PDI.RevitMcp
  if [ "$v" -ge 2025 ]; then
    # Revit 2025–2026 (.NET 8): DLL precompiladas ReadyToRun (código nativo x64 + IL de respaldo) → sin compilar al primer uso.
    rm -rf obj/r2r_$v
    dotnet publish src/PDI.RevitMcp.Addin -c Release -p:RevitVersion=$v -r win-x64 --self-contained false \
      -p:PublishReadyToRun=true -o obj/r2r_$v -v q
    cp obj/r2r_$v/PDI.RevitMcp.*.dll dist/addin/$v/PDI.RevitMcp/
  else
    # Revit 2022–2024 (.NET Framework 4.8): IL; el add-in precompila el motor en segundo plano al arrancar (Warmup).
    dotnet build src/PDI.RevitMcp.Addin -c Release -p:RevitVersion=$v -v q
    cp src/PDI.RevitMcp.Addin/bin/Release/R$v/PDI.RevitMcp.*.dll dist/addin/$v/PDI.RevitMcp/
  fi
  cp src/PDI.RevitMcp.Addin/PDI.RevitMcp.addin dist/addin/$v/
  # recetas incluidas (0.6.0): tarea iniciar receta=<nombre>; las del usuario en %LOCALAPPDATA%\PDI.RevitMcp\recetas ganan
  mkdir -p dist/addin/$v/PDI.RevitMcp/recetas
  cp src/PDI.RevitMcp.Addin/recetas/*.cs dist/addin/$v/PDI.RevitMcp/recetas/
  mkdir -p dist/addin/$v/PDI.RevitMcp/roslyn
  cp $ROSLYN/*.dll $ROSLYN/PDI.RevitMcp.Scripting.pdb dist/addin/$v/PDI.RevitMcp/roslyn/
  # el compilador NO va junto al add-in: sólo en roslyn/ (si no, .NET lo cargaría en el contexto equivocado)
  rm -f dist/addin/$v/PDI.RevitMcp/PDI.RevitMcp.Scripting.dll
done
# Pruebas DESPUÉS de compilar los add-ins: las de exec compilan scripts contra el add-in de cada año.
dotnet build src/PDI.RevitMcp.Addin -c Release -p:RevitVersion=2025 -v q
dotnet build src/PDI.RevitMcp.Addin -c Release -p:RevitVersion=2026 -v q
dotnet test tests/PDI.RevitMcp.Tests
dotnet publish src/PDI.RevitMcp.Bridge -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist/bridge
# 0.9.0: BIMBRIDGE Power Engine junto al puente y al AgentHost. Rust (geometría): DLL para FFI + ejecutable IPC, compilados para
# Windows x64 si hay toolchain; sin ellos el núcleo C# hace todo (degradación controlada). Python (documentos): código del worker +
# dependencias puras verificadas por SHA-256; el intérprete NO se empaqueta (config.json → power_engine.python o python.exe del PATH).
PE=dist/bridge/power_engine
mkdir -p $PE
if command -v cargo >/dev/null && rustup target list --installed 2>/dev/null | grep -q x86_64-pc-windows-gnu; then
  (cd native/power_engine && cargo test --release -q && cargo build --release -q --target x86_64-pc-windows-gnu)
  cp native/power_engine/target/x86_64-pc-windows-gnu/release/bimbridge_power_engine.dll dist/bridge/
  cp native/power_engine/target/x86_64-pc-windows-gnu/release/bimbridge-power-engine.exe $PE/
else
  echo "build.sh: sin toolchain Rust para Windows; el Power Engine usará sólo C# para geometría" >&2
fi
python3 -I tools/power_engine/vendor_python.py $PE/python
python3 -B -m unittest discover -s workers/python/tests -q
# alpha3: AgentHost autocontenido (CLI `revit nrt04` para la aceptación nativa; no se instala en Revit)
dotnet publish src/PDI.RevitMcp.AgentHost -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist/agenthost
[ -f dist/bridge/bimbridge_power_engine.dll ] && cp dist/bridge/bimbridge_power_engine.dll dist/agenthost/
cp -r $PE dist/agenthost/

# Instalador de doble clic: payload.zip (add-in + puente) dentro de PDI.RevitMcp.Setup.exe
rm -f src/PDI.RevitMcp.Setup/payload.zip
# manifest.json: SHA-256 de cada archivo; el instalador lo verifica antes de copiar y después de copiar (0.6.0)
VERSION=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' Directory.Build.props)
(cd dist && { printf '{"version":"%s","archivos":{' "$VERSION"; first=1; \
  find addin bridge -type f | LC_ALL=C sort | while read -r f; do h=$(sha256sum "$f" | cut -d' ' -f1); \
    if [ $first = 1 ]; then first=0; else printf ','; fi; printf '"%s":"%s"' "$f" "$h"; done; printf '}}'; } > manifest.json)
(cd dist && zip -qr ../src/PDI.RevitMcp.Setup/payload.zip addin bridge manifest.json)
dotnet build src/PDI.RevitMcp.Setup -c Release -v q
cp src/PDI.RevitMcp.Setup/bin/Release/net48/PDI.RevitMcp.Setup.exe dist/
echo "Instalador: dist/PDI.RevitMcp.Setup.exe"
# 0.8.0: paquete para "Actualizar desde ZIP" (manifiesto con SHA-256; firmado sólo si PDI_CLAVE_ACTUALIZACION apunta a la clave privada de IT).
FIRMA=()
if [ -n "${PDI_CLAVE_ACTUALIZACION:-}" ]; then FIRMA=(--clave-privada "$PDI_CLAVE_ACTUALIZACION"); fi
dotnet run -c Release --project src/PDI.RevitMcp.AgentHost -- paquete-actualizacion --salida "dist/BIMBRIDGE-$VERSION-actualizacion.zip" \
  --setup dist/PDI.RevitMcp.Setup.exe --version "$VERSION" --commit "$SourceRevisionId" --desde-minima 0.7.0 "${FIRMA[@]}" > dist/actualizacion-manifiesto.json
echo "Actualización: dist/BIMBRIDGE-$VERSION-actualizacion.zip"
