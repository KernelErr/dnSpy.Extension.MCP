#Requires -Version 5.0
<#
.SYNOPSIS
  Builds the TestIL.dll fixture used by run-tests.ps1.

.DESCRIPTION
  Compiles TestIL.cs → bin/TestIL.dll via the side-car TestIL.csproj. The DLL
  itself is gitignored; the source + csproj + this script are the tracked
  reproduction recipe.

.PARAMETER Clean
  Delete any previous output (obj/, bin/) before building.
#>

[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$fixtureDir = $PSScriptRoot
$csproj = Join-Path $fixtureDir 'TestIL.csproj'
$binDir = Join-Path $fixtureDir 'bin'
$objDir = Join-Path $fixtureDir 'obj'

if ($Clean)
{
    if (Test-Path $binDir) { Remove-Item -Recurse -Force $binDir }
    if (Test-Path $objDir) { Remove-Item -Recurse -Force $objDir }
}

Write-Host "Building TestIL fixture → $binDir/TestIL.dll"
& dotnet build $csproj -c Release -o $binDir --nologo -v q
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$dll = Join-Path $binDir 'TestIL.dll'
if (-not (Test-Path $dll))
{
    throw "Build reported success but TestIL.dll is missing at $dll"
}

$hash = (Get-FileHash -Algorithm SHA256 $dll).Hash
$bytes = (Get-Item $dll).Length
Write-Host "  path:  $dll"
Write-Host "  size:  $bytes bytes"
Write-Host "  sha256: $hash"
