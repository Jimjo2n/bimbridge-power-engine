# SPDX-License-Identifier: GPL-3.0-only
# Junta la evidencia de la aceptación 0.6.0 en un zip en el Escritorio. Sólo lee y copia; no cambia nada del MCP ni de Revit.
# NO incluye config.json, credenciales ni archivos de modelo. Incluye: carpetas de las tareas pedidas (diario, unidades, artefactos
# pequeños), métricas, diario de operaciones (operaciones\) de los últimos 3 días, diagnóstico del instalador y addin.log.
param([string[]]$Tareas = @(), [int]$Dias = 3, [int]$MaxMbArtefacto = 20)
$ErrorActionPreference = "Stop"
$home_ = Join-Path $env:LOCALAPPDATA "PDI.RevitMcp"
$stamp = Get-Date -Format "yyyyMMdd_HHmm"
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) ("evidencia_060_" + $stamp)
New-Item -ItemType Directory -Force $out | Out-Null

$setup = Join-Path $home_ "PDI.RevitMcp.Setup.exe"
if (Test-Path $setup) { & $setup /diagnostico | Out-File -Encoding utf8 (Join-Path $out "diagnostico.json") }

foreach ($t in $Tareas) {
  $src = Join-Path $home_ ("tareas\" + $t)
  if (-not (Test-Path $src)) { "no existe la tarea $t" | Add-Content (Join-Path $out "notas.md"); continue }
  $dst = Join-Path $out ("tareas\" + $t)
  New-Item -ItemType Directory -Force $dst | Out-Null
  Get-ChildItem -Recurse -File $src | Where-Object { $_.Length -le $MaxMbArtefacto * 1MB } | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length).TrimStart('\')
    $target = Join-Path $dst $rel
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target
  }
}
$since = (Get-Date).AddDays(-$Dias)
foreach ($sub in "metricas", "operaciones") {
  $p = Join-Path $home_ $sub
  if (Test-Path $p) {
    New-Item -ItemType Directory -Force (Join-Path $out $sub) | Out-Null
    Get-ChildItem -File $p | Where-Object { $_.LastWriteTime -ge $since } | Copy-Item -Destination (Join-Path $out $sub)
  }
}
$log = Join-Path $home_ "addin.log"
if (Test-Path $log) { Get-Content $log -Tail 4000 | Out-File -Encoding utf8 (Join-Path $out "addin.log") }

# Índice con SHA-256 de cada archivo copiado
Get-ChildItem -Recurse -File $out | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLower(), $_.FullName.Substring($out.Length + 1)
} | Out-File -Encoding utf8 (Join-Path $out "SHA256.txt")
Compress-Archive -Force -Path (Join-Path $out "*") -DestinationPath ($out + ".zip")
Write-Host ("Evidencia: " + $out + ".zip")
