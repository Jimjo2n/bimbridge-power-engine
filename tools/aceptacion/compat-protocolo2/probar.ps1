# SPDX-License-Identifier: GPL-3.0-only
param([string]$Anterior = "8ce066f8857595c4cf643ece2b3d08a1c1a10f21")

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../../..")).Path
$tempParent = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$temp = (New-Item -ItemType Directory -Path (Join-Path $tempParent ("bimbridge-protocol-" + [Guid]::NewGuid().ToString("N")))).FullName
$originalHome = $env:PDI_REVITMCP_HOME
function Run([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable terminó con código $LASTEXITCODE" }
}
try {
    $old = (New-Item -ItemType Directory -Path (Join-Path $temp "anterior")).FullName
    $env:PDI_REVITMCP_HOME = (New-Item -ItemType Directory -Path (Join-Path $temp "datos")).FullName
    $archive = Join-Path $temp "anterior.tar"
    Run git @("-C", $root, "archive", "--format=tar", "--output=$archive", $Anterior)
    Run tar @("-xf", $archive, "-C", $old)
    $project = Join-Path $PSScriptRoot "ProtocolMatrix.csproj"
    foreach ($case in @(@{ Name = "anterior"; Source = $old }, @{ Name = "actual"; Source = $root })) {
        $name = $case.Name
        $source = $case.Source
        $bridge = Join-Path $source "src/PDI.RevitMcp.Bridge/PDI.RevitMcp.Bridge.csproj"
        Run dotnet @("build", $bridge, "-c", "Release", "-o", (Join-Path $temp "$name-puente"), "-v:q")
        Run dotnet @("build", $project, "-c", "Release", "-p:CompatSource=$source", "-o", (Join-Path $temp "$name-host"), "-v:q")
    }
    Run dotnet @((Join-Path $temp "actual-host/ProtocolMatrix.dll"),
        (Join-Path $temp "anterior-host/ProtocolMatrix.dll"), (Join-Path $temp "anterior-puente/PDI.RevitMcp.Bridge.dll"),
        (Join-Path $temp "actual-host/ProtocolMatrix.dll"), (Join-Path $temp "actual-puente/PDI.RevitMcp.Bridge.dll"))
}
finally {
    $env:PDI_REVITMCP_HOME = $originalHome
    $resolved = (Resolve-Path -LiteralPath $temp).Path
    if ((Split-Path -Parent $resolved) -eq $tempParent -and (Split-Path -Leaf $resolved) -match '^bimbridge-protocol-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
