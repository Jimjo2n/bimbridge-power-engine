# SPDX-License-Identifier: GPL-3.0-only
# Compila y prueba todo, y arma dist\ en Windows.
# Opcional: -RevitRoot "C:\Program Files\Autodesk" para compilar contra las DLL instaladas en vez de los paquetes de referencia.
param([string]$RevitRoot = "", [string]$Python = "python.exe", [switch]$SkipBenchmarks)
$ErrorActionPreference = "Stop"
# Sin BOM: los workers reciben JSONL por stdin y lo rechazan con razón si llega con BOM.
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Set-Location $PSScriptRoot
# F19: $ErrorActionPreference no detiene el script cuando dotnet falla; se comprueba el código de salida de cada paso.
function Run([string]$what) {
  Invoke-Expression $what
  if ($LASTEXITCODE -ne 0) { throw "Falló ($LASTEXITCODE): $what" }
}
# Reproducible (beta1): sólo desde un árbol limpio; el commit embebido es exactamente HEAD. $env:BIMBRIDGE_ALLOW_DIRTY=1 para desarrollo.
if ((git status --porcelain --untracked-files=no) -and $env:BIMBRIDGE_ALLOW_DIRTY -ne "1") { throw "Hay cambios sin confirmar; confirme antes de armar dist\." }
$env:SourceRevisionId = (git rev-parse HEAD).Trim()
function Clear-BuildFolder([string]$relative) {
  $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
  $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
  if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Ruta fuera del checkout: $path" }
  if (Test-Path -LiteralPath $path) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Ruta de build enlazada: $path" }
    Remove-Item -LiteralPath $path -Recurse -Force
  }
}
Clear-BuildFolder dist
$rustHost = (& rustc -vV | Select-String '^host: ').Line
if ($LASTEXITCODE -ne 0 -or $rustHost -notmatch '^host: x86_64-pc-windows-(gnu|msvc)$') {
  throw "Se requiere Rust para Windows x64: $rustHost"
}
$pythonPath = (Get-Command $Python -CommandType Application -ErrorAction Stop).Source
if ($pythonPath -match '[\\/]WindowsApps[\\/]') { throw "Indique -Python con la ruta de un CPython real" }
$env:BIMBRIDGE_TEST_PYTHON = $pythonPath
$env:PATH = "$(Split-Path $pythonPath);$PSScriptRoot/native/power_engine/target/release;$env:PATH"
Run "cargo test --locked --release --manifest-path native/power_engine/Cargo.toml"
Run "cargo build --locked --release --manifest-path native/power_engine/Cargo.toml"
& $pythonPath -B -m unittest discover -s workers/python/tests -v
if ($LASTEXITCODE -ne 0) { throw "Falló la suite Python" }
Run "dotnet build src/Bimbridge.Scripting -c Release -v q"
$roslyn = "src/Bimbridge.Scripting/bin/Release/netstandard2.0"
foreach ($v in 2022..2027) {
  $dir = if ($RevitRoot) { Join-Path $RevitRoot "Revit $v" }
  $extra = ""
  if ($RevitRoot -and (Test-Path (Join-Path $dir "RevitAPI.dll"))) { $extra = "`"-p:RevitApiDir=$dir`"" }
  $out = "dist/addin/$v/Bimbridge"
  New-Item -ItemType Directory -Force $out | Out-Null
  if ($v -ge 2025) {
    # Revit 2025–2027: ReadyToRun (código nativo x64 + IL de respaldo) para el runtime del año.
    $r2r = "obj/r2r_$v"
    Clear-BuildFolder $r2r
    Run "dotnet publish src/Bimbridge.Addin -c Release -p:RevitVersion=$v -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $r2r -v q $extra"
    Copy-Item "$r2r/Bimbridge.*.dll" $out
  } else {
    # Revit 2022–2024 (.NET Framework 4.8): IL; el add-in precompila el motor en segundo plano al arrancar.
    Run "dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=$v -v q $extra"
    Copy-Item "src/Bimbridge.Addin/bin/Release/R$v/Bimbridge.*.dll" $out
  }
  Copy-Item "src/Bimbridge.Addin/Bimbridge.addin" "dist/addin/$v/"
  New-Item -ItemType Directory -Force "$out/recetas" | Out-Null
  Copy-Item "src/Bimbridge.Addin/recetas/*.cs" "$out/recetas/"
  New-Item -ItemType Directory -Force "$out/roslyn" | Out-Null
  Copy-Item "$roslyn/*.dll", "$roslyn/Bimbridge.Scripting.pdb" "$out/roslyn/"
  Remove-Item "$out/Bimbridge.Scripting.dll" -ErrorAction SilentlyContinue
}
# Pruebas después de compilar los add-ins (las de exec compilan scripts contra el add-in de cada año).
foreach ($v in 2025..2027) { Run "dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=$v -v q" }
Run "dotnet publish src/Bimbridge.Bridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/bridge"
Run "dotnet publish src/Bimbridge.AgentHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/bridge"

$pe = "dist/bridge/power_engine"
New-Item -ItemType Directory -Force $pe | Out-Null
Copy-Item native/power_engine/target/release/bimbridge_power_engine.dll dist/bridge/
Copy-Item native/power_engine/target/release/bimbridge-power-engine.exe $pe/
& $pythonPath -I -B tools/power_engine/vendor_python.py "$pe/python"
if ($LASTEXITCODE -ne 0) { throw "Falló el empaquetado del Python Worker" }
$hello = '{"pe":1,"id":"build","trabajo":"hola","args":{"pe_min":1,"pe_max":1}}'
$helloFile = Join-Path $PSScriptRoot "obj\hello.jsonl"
New-Item -ItemType Directory -Force (Split-Path $helloFile) | Out-Null
[IO.File]::WriteAllText($helloFile, $hello + "`n", (New-Object System.Text.UTF8Encoding($false)))
$pyHello = cmd /c "`"`"$pythonPath`" -I -S -B `"$pe/python/run_worker.py`" < `"$helloFile`"`"" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $pyHello.codigo -ne "OK" -or "pdf_grids" -notin $pyHello.resultado.capacidades) {
  throw "El worker Python del payload no anuncia pdf_grids con sus dependencias empaquetadas"
}
$rustHello = cmd /c "`"`"$PSScriptRoot\dist\bridge\power_engine\bimbridge-power-engine.exe`" < `"$helloFile`"`"" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $rustHello.codigo -ne "OK" -or "components" -notin $rustHello.resultado.capacidades) {
  throw "Falló el saludo IPC de Rust en el payload"
}
$required = @(
  "bridge/Bimbridge.Bridge.exe", "bridge/Bimbridge.AgentHost.exe",
  "bridge/bimbridge_power_engine.dll", "bridge/power_engine/bimbridge-power-engine.exe",
  "bridge/power_engine/python/run_worker.py",
  "bridge/power_engine/python/bimbridge_docworker/__init__.py",
  "bridge/power_engine/python/bimbridge_docworker/worker.py",
  "bridge/power_engine/python/bimbridge_docworker/pdf.py",
  "bridge/power_engine/python/bimbridge_docworker/docx.py",
  "bridge/power_engine/python/bimbridge_docworker/xlsx.py",
  "bridge/power_engine/python/vendor/pdfminer/__init__.py",
  "bridge/power_engine/python/vendor/charset_normalizer/__init__.py"
)
foreach ($v in 2022..2027) {
  $required += "addin/$v/Bimbridge.addin"
  foreach ($dll in "Addin", "Protocol", "Engine", "Setup.Core") {
    $required += "addin/$v/Bimbridge/Bimbridge.$dll.dll"
  }
  $required += "addin/$v/Bimbridge/roslyn/Bimbridge.Scripting.dll"
}
foreach ($file in $required) {
  if (!(Test-Path -LiteralPath "dist/$file" -PathType Leaf) -or (Get-Item -LiteralPath "dist/$file").Length -eq 0) {
    throw "Falta un componente obligatorio del payload: $file"
  }
}

# Instalador de doble clic: payload.zip (add-in + puente) dentro de Bimbridge.Setup.exe
$payload = "src/Bimbridge.Setup/payload.zip"
if (Test-Path $payload) { Remove-Item $payload }
# manifest.json: SHA-256 de cada archivo; el instalador lo verifica antes y después de copiar (0.6.0)
$ver = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$files = [ordered]@{}
Push-Location dist
Get-ChildItem -Recurse -File addin, bridge | Sort-Object FullName | ForEach-Object {
  $rel = (Resolve-Path -Relative $_.FullName).TrimStart('.', '\').Replace('\', '/')
  $files[$rel] = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
}
Pop-Location
@{ version = $ver; commit = $env:SourceRevisionId; estado = "NATIVE_ACCEPTANCE_PENDING"; requeridos = $required; archivos = $files } | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 dist/manifest.json
Compress-Archive -Path dist/addin, dist/bridge, dist/manifest.json -DestinationPath $payload
$testFilter = if ($SkipBenchmarks) { " --filter 'FullyQualifiedName!~MeasurementTests&FullyQualifiedName!~Bench_'" } else { '' }
Run "dotnet test tests/Bimbridge.Tests$testFilter -- RunConfiguration.MaxCpuCount=1 xUnit.MaxParallelThreads=1"
Run "dotnet build src/Bimbridge.Setup -c Release -v q"
Copy-Item src/Bimbridge.Setup/bin/Release/net48/Bimbridge.Setup.exe dist/
Write-Host "Instalador: dist/Bimbridge.Setup.exe"
