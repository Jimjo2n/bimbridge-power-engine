# SPDX-License-Identifier: GPL-3.0-only
# Builds and tests everything, and assembles dist\ on Windows.
# Optional: -RevitRoot "C:\Program Files\Autodesk" to compile against the installed DLLs instead of the reference packages.
param([string]$RevitRoot = "", [string]$Python = "python.exe", [switch]$SkipBenchmarks)
$ErrorActionPreference = "Stop"
# No BOM: the workers read JSONL on stdin and rightly reject it if it arrives with a BOM.
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Set-Location $PSScriptRoot
# F19: $ErrorActionPreference does not stop the script when dotnet fails; the exit code of every step is checked.
function Run([string]$what) {
  Invoke-Expression $what
  if ($LASTEXITCODE -ne 0) { throw "Failed ($LASTEXITCODE): $what" }
}
# Reproducible (beta1): only from a clean tree; the embedded commit is exactly HEAD. $env:BIMBRIDGE_ALLOW_DIRTY=1 for development.
if ((git status --porcelain --untracked-files=no) -and $env:BIMBRIDGE_ALLOW_DIRTY -ne "1") { throw "There are uncommitted changes; commit before assembling dist\." }
$env:SourceRevisionId = (git rev-parse HEAD).Trim()
function Clear-BuildFolder([string]$relative) {
  $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
  $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
  if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside the checkout: $path" }
  if (Test-Path -LiteralPath $path) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Build path is a link: $path" }
    Remove-Item -LiteralPath $path -Recurse -Force
  }
}
Clear-BuildFolder dist
$rustHost = (& rustc -vV | Select-String '^host: ').Line
if ($LASTEXITCODE -ne 0 -or $rustHost -notmatch '^host: x86_64-pc-windows-(gnu|msvc)$') {
  throw "Rust for Windows x64 is required: $rustHost"
}
$pythonPath = (Get-Command $Python -CommandType Application -ErrorAction Stop).Source
if ($pythonPath -match '[\\/]WindowsApps[\\/]') { throw "Pass -Python with the path of a real CPython" }
$env:BIMBRIDGE_TEST_PYTHON = $pythonPath
$env:PATH = "$(Split-Path $pythonPath);$PSScriptRoot/native/power_engine/target/release;$env:PATH"
Run "cargo test --locked --release --manifest-path native/power_engine/Cargo.toml"
Run "cargo build --locked --release --manifest-path native/power_engine/Cargo.toml"
& $pythonPath -B -m unittest discover -s workers/python/tests -v
if ($LASTEXITCODE -ne 0) { throw "Python suite failed" }
Run "dotnet build src/Bimbridge.Scripting -c Release -v q"
$roslyn = "src/Bimbridge.Scripting/bin/Release/netstandard2.0"
foreach ($v in 2022..2027) {
  $dir = if ($RevitRoot) { Join-Path $RevitRoot "Revit $v" }
  $extra = ""
  if ($RevitRoot -and (Test-Path (Join-Path $dir "RevitAPI.dll"))) { $extra = "`"-p:RevitApiDir=$dir`"" }
  $out = "dist/addin/$v/Bimbridge"
  New-Item -ItemType Directory -Force $out | Out-Null
  if ($v -ge 2025) {
    # Revit 2025–2027: ReadyToRun (native x64 code + fallback IL) for that year's runtime.
    $r2r = "obj/r2r_$v"
    Clear-BuildFolder $r2r
    Run "dotnet publish src/Bimbridge.Addin -c Release -p:RevitVersion=$v -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $r2r -v q $extra"
    Copy-Item "$r2r/Bimbridge.*.dll" $out
  } else {
    # Revit 2022–2024 (.NET Framework 4.8): IL; the add-in precompiles the engine in the background at startup.
    Run "dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=$v -v q $extra"
    Copy-Item "src/Bimbridge.Addin/bin/Release/R$v/Bimbridge.*.dll" $out
  }
  Copy-Item "src/Bimbridge.Addin/Bimbridge.addin" "dist/addin/$v/"
  New-Item -ItemType Directory -Force "$out/recipes" | Out-Null
  Copy-Item "src/Bimbridge.Addin/recipes/*.cs" "$out/recipes/"
  New-Item -ItemType Directory -Force "$out/roslyn" | Out-Null
  Copy-Item "$roslyn/*.dll", "$roslyn/Bimbridge.Scripting.pdb" "$out/roslyn/"
  Remove-Item "$out/Bimbridge.Scripting.dll" -ErrorAction SilentlyContinue
}
# Tests after building the add-ins (the exec tests compile scripts against each year's add-in).
foreach ($v in 2025..2027) { Run "dotnet build src/Bimbridge.Addin -c Release -p:RevitVersion=$v -v q" }
Run "dotnet publish src/Bimbridge.Bridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/bridge"
Run "dotnet publish src/Bimbridge.AgentHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/bridge"

$pe = "dist/bridge/power_engine"
New-Item -ItemType Directory -Force $pe | Out-Null
Copy-Item native/power_engine/target/release/bimbridge_power_engine.dll dist/bridge/
Copy-Item native/power_engine/target/release/bimbridge-power-engine.exe $pe/
& $pythonPath -I -B tools/power_engine/vendor_python.py "$pe/python"
if ($LASTEXITCODE -ne 0) { throw "Python Worker packaging failed" }
$hello = '{"pe":1,"id":"build","job":"hola","args":{"pe_min":1,"pe_max":1}}'
$helloFile = Join-Path $PSScriptRoot "obj\hello.jsonl"
New-Item -ItemType Directory -Force (Split-Path $helloFile) | Out-Null
[IO.File]::WriteAllText($helloFile, $hello + "`n", (New-Object System.Text.UTF8Encoding($false)))
$pyHello = cmd /c "`"`"$pythonPath`" -I -S -B `"$pe/python/run_worker.py`" < `"$helloFile`"`"" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $pyHello.code -ne "OK" -or "pdf_grids" -notin $pyHello.result.capacidades) {
  throw "The payload Python worker does not advertise pdf_grids with its packaged dependencies"
}
$rustHello = cmd /c "`"`"$PSScriptRoot\dist\bridge\power_engine\bimbridge-power-engine.exe`" < `"$helloFile`"`"" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $rustHello.code -ne "OK" -or "components" -notin $rustHello.result.capacidades) {
  throw "Rust IPC handshake failed in the payload"
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
    throw "A required payload component is missing: $file"
  }
}

# Double-click installer: payload.zip (add-in + bridge) inside Bimbridge.Setup.exe
$payload = "src/Bimbridge.Setup/payload.zip"
if (Test-Path $payload) { Remove-Item $payload }
# manifest.json: SHA-256 of every file; the installer verifies it before and after copying (0.6.0)
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
Write-Host "Installer: dist/Bimbridge.Setup.exe"
