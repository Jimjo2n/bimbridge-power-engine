# SPDX-License-Identifier: GPL-3.0-only
# Collects the 0.6.0 acceptance evidence into a zip on the Desktop. Only reads and copies; changes nothing in the MCP or Revit.
# Does NOT include config.json, credentials or model files. Includes: folders of the requested tasks (journal, units, small
# artifacts), metrics, operation journal (operaciones\) of the last 3 days, installer diagnostics and addin.log.
param([string[]]$Tasks = @(), [int]$Days = 3, [int]$MaxMbArtifact = 20)
$ErrorActionPreference = "Stop"
$home_ = Join-Path $env:LOCALAPPDATA "Bimbridge"
$stamp = Get-Date -Format "yyyyMMdd_HHmm"
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) ("evidence_060_" + $stamp)
New-Item -ItemType Directory -Force $out | Out-Null

$setup = Join-Path $home_ "Bimbridge.Setup.exe"
if (Test-Path $setup) { & $setup /diagnostico | Out-File -Encoding utf8 (Join-Path $out "diagnostics.json") }

foreach ($t in $Tasks) {
  $src = Join-Path $home_ ("tareas\" + $t)
  if (-not (Test-Path $src)) { "task $t does not exist" | Add-Content (Join-Path $out "notes.md"); continue }
  $dst = Join-Path $out ("tareas\" + $t)
  New-Item -ItemType Directory -Force $dst | Out-Null
  Get-ChildItem -Recurse -File $src | Where-Object { $_.Length -le $MaxMbArtifact * 1MB } | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length).TrimStart('\')
    $target = Join-Path $dst $rel
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target
  }
}
$since = (Get-Date).AddDays(-$Days)
foreach ($sub in "metricas", "operaciones") {
  $p = Join-Path $home_ $sub
  if (Test-Path $p) {
    New-Item -ItemType Directory -Force (Join-Path $out $sub) | Out-Null
    Get-ChildItem -File $p | Where-Object { $_.LastWriteTime -ge $since } | Copy-Item -Destination (Join-Path $out $sub)
  }
}
$log = Join-Path $home_ "addin.log"
if (Test-Path $log) { Get-Content $log -Tail 4000 | Out-File -Encoding utf8 (Join-Path $out "addin.log") }

# Index with the SHA-256 of each copied file
Get-ChildItem -Recurse -File $out | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLower(), $_.FullName.Substring($out.Length + 1)
} | Out-File -Encoding utf8 (Join-Path $out "SHA256.txt")
Compress-Archive -Force -Path (Join-Path $out "*") -DestinationPath ($out + ".zip")
Write-Host ("Evidence: " + $out + ".zip")
