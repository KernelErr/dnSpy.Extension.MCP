#Requires -Version 5.0
<#
.SYNOPSIS
  Fails when a NuGet dependency this extension pins for net48 drifts from the version dnSpy's own
  net48 build resolves.

.DESCRIPTION
  On .NET Framework 4.8 assembly binding is strict and dnSpy ships no binding redirects for these
  packages, so if we compile against System.Text.Json 8.0.0.5 while dnSpy ships 9.0.0 the mismatch
  is invisible at build time — the extension loads, the HTTP server starts, and the first MCP
  request dies with FileNotFoundException inside HandleHttpRequest. (.NET 10 rolls forward, so the
  net10 build and the net10 end-to-end suite never see it.) That is exactly how issue #21 shipped.

  This compares the versions resolved in each project's restore graph (project.assets.json), so it
  only needs `dotnet restore`, not a full build, and runs in a couple of seconds in CI.

.PARAMETER DnSpyProject
  Path to the dnSpy app project directory whose restore graph is the source of truth.
  Defaults to ../../dnSpy/dnSpy relative to this script.

.PARAMETER Restore
  Run `dotnet restore` for both projects first (needed on a clean checkout / CI).
#>

[CmdletBinding()]
param(
    [string]$DnSpyProject,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$extDir = Split-Path $PSScriptRoot -Parent
if (-not $DnSpyProject) { $DnSpyProject = Join-Path $extDir '..\..\dnSpy\dnSpy' }

# Packages that must match dnSpy's net48 resolution exactly. Add to this list whenever the
# extension takes another net48-only PackageReference.
$guarded = @('System.Text.Json')

function Get-Net48PackageVersions([string]$projectDir, [string]$label)
{
    $assets = Join-Path $projectDir 'obj\project.assets.json'
    if (-not (Test-Path $assets)) {
        throw "$label restore graph not found: $assets`nRun with -Restore, or 'dotnet restore' in $projectDir first."
    }
    $doc = Get-Content $assets -Raw | ConvertFrom-Json
    $versions = @{}
    foreach ($target in $doc.targets.PSObject.Properties) {
        # Restore writes the net48 target either as the short alias ("net48", what this project
        # gets) or the long moniker (".NETFramework,Version=v4.8", what dnSpy gets). Accept both,
        # and skip RID-qualified variants like ".NETFramework,Version=v4.8/win-x64".
        if ($target.Name -notmatch '^(net48|\.NETFramework,Version=v4\.8)$') { continue }
        foreach ($lib in $target.Value.PSObject.Properties) {
            $parts = $lib.Name -split '/', 2
            if ($parts.Count -eq 2) { $versions[$parts[0]] = $parts[1] }
        }
    }
    if ($versions.Count -eq 0) { throw "$label has no .NETFramework,Version=v4.8 target in $assets" }
    return $versions
}

if ($Restore) {
    Write-Host "Restoring dnSpy app project..."
    & dotnet restore $DnSpyProject --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $DnSpyProject" }
    Write-Host "Restoring extension project..."
    & dotnet restore $extDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $extDir" }
}

$hostVersions = Get-Net48PackageVersions $DnSpyProject 'dnSpy'
$extVersions  = Get-Net48PackageVersions $extDir      'extension'

Write-Host "Comparing net48 package versions against dnSpy's resolution"
Write-Host "  dnSpy:     $(Resolve-Path $DnSpyProject)"
Write-Host "  extension: $(Resolve-Path $extDir)"
Write-Host ""

$problems = @()
foreach ($pkg in $guarded)
{
    $hostV = $hostVersions[$pkg]
    $extV  = $extVersions[$pkg]

    if (-not $extV) {
        Write-Host "  SKIP  $pkg — not referenced by the extension for net48" -ForegroundColor DarkGray
        continue
    }
    if (-not $hostV) {
        # dnSpy no longer ships it: we'd be the only provider, which is a different (and also
        # dangerous) situation worth surfacing rather than silently passing.
        $problems += "$pkg : extension pins $extV but dnSpy's net48 build no longer resolves it at all. Confirm the extension still needs it, and that shipping our own copy is safe."
        Write-Host "  FAIL  $pkg  extension=$extV  dnSpy=<absent>" -ForegroundColor Red
        continue
    }
    if ($hostV -ne $extV) {
        $problems += "$pkg : extension pins $extV but dnSpy's net48 build ships $hostV. Update the PackageReference in dnSpy.Extension.MCP.csproj to $hostV."
        Write-Host "  FAIL  $pkg  extension=$extV  dnSpy=$hostV" -ForegroundColor Red
        continue
    }
    Write-Host "  OK    $pkg  $extV" -ForegroundColor Green
}

Write-Host ""
if ($problems.Count -gt 0)
{
    Write-Host "net48 dependency drift detected:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Why this matters: .NET Framework binds assemblies by exact version and dnSpy ships no"
    Write-Host "binding redirect for these packages, so a mismatch builds fine and then throws"
    Write-Host "FileNotFoundException on the first MCP request. See issue #21."
    exit 1
}

Write-Host "All guarded net48 dependencies match dnSpy." -ForegroundColor Green
exit 0
