#Requires -Version 5.0
<#
.SYNOPSIS
  Deploys the headless MCP host into a dnSpy installation, next to dnSpy.Console.exe, the way dnSpy's
  own build.ps1 deploys dnSpy.Console.exe.

.DESCRIPTION
  The headless exe loads dnSpy's decompiler and metadata assemblies from the installation it sits in,
  and the extension DLL from bin\Extensions\dnSpy.Extension.MCP\ (deploy that separately, as for the
  GUI). Only this project's own files are copied; the rest of the layout mirrors dnSpy.Console for
  each dnSpy flavor:

  - net48: <root>\dnSpy.Extension.MCP.Headless.exe, plus a copy of the bundle's dnSpy.exe.config as
    its .exe.config: probing privatePath="bin" and the exact binding redirects for the assemblies
    this bundle ships, i.e. the binding environment the extension already runs under inside dnSpy.
  - net10: the DLL goes where dnSpy.Console.dll is (the root of a plain build, bin\ in build.ps1's
    layout) with a copy of the bundle's dnSpy.Console.runtimeconfig.json, so it is framework-dependent
    or self-contained exactly as the bundle is. The apphost goes next to dnSpy.Console.exe and, when
    the DLL is in bin\, is patched with AppHostPatcher -d bin like dnSpy.Console.exe. No deps.json is
    deployed: the .NET host then treats every assembly in the app directory as an app assembly.
    The apphost must be built for the bundle's architecture (-r win-x86 for the x86 bundle); the
    script refuses a mismatch rather than ship an exe that can't load the bundle's runtime.

.PARAMETER DnSpyDir
  The dnSpy installation root: the folder holding dnSpy.Console.exe.

.PARAMETER BuildOutput
  The headless project's build output for the TFM matching that dnSpy (...\headless\bin\Release\<tfm>).

.PARAMETER AppHostPatcher
  dnSpy's AppHostPatcher.exe, needed only for a net10 build.ps1 layout. Defaults to the one build.ps1
  builds in the parent dnSpyEx checkout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$DnSpyDir,
    [Parameter(Mandatory = $true)] [string]$BuildOutput,
    [string]$AppHostPatcher
)

$ErrorActionPreference = 'Stop'
$name = 'dnSpy.Extension.MCP.Headless'
$DnSpyDir = (Resolve-Path $DnSpyDir).Path
$BuildOutput = (Resolve-Path $BuildOutput).Path
if (-not $AppHostPatcher) {
    $AppHostPatcher = Join-Path $PSScriptRoot '..\..\..\Build\AppHostPatcher\bin\Release\net48\AppHostPatcher.exe'
}

if (-not (Test-Path (Join-Path $DnSpyDir 'dnSpy.Console.exe'))) {
    throw "No dnSpy.Console.exe in $DnSpyDir; pass the dnSpy installation root."
}
if (-not (Test-Path (Join-Path $BuildOutput "$name.exe"))) {
    throw "No $name.exe in $BuildOutput; build headless\$name.csproj first."
}

function Copy-WithPdb([string]$file, [string]$destDir)
{
    Copy-Item $file $destDir -Force
    $pdb = [System.IO.Path]::ChangeExtension($file, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb $destDir -Force }
}

# The machine type in a PE header: an apphost is native code for exactly one architecture.
function Get-PeMachine([string]$path)
{
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32() + 4
        switch ($reader.ReadUInt16()) { 0x014C { 'x86' } 0x8664 { 'x64' } 0xAA64 { 'arm64' } default { 'unknown' } }
    }
    finally { $stream.Dispose() }
}

# A net10 build output has the managed DLL next to the apphost; a net48 one is just the exe.
$isNet = Test-Path (Join-Path $BuildOutput "$name.dll")

if (-not $isNet) {
    $dnSpyConfig = Join-Path $DnSpyDir 'dnSpy.exe.config'
    if (-not (Test-Path $dnSpyConfig)) {
        throw "$DnSpyDir is not a .NET Framework dnSpy (no dnSpy.exe.config), but $BuildOutput is the net48 build."
    }
    Copy-WithPdb (Join-Path $BuildOutput "$name.exe") $DnSpyDir
    Copy-Item $dnSpyConfig (Join-Path $DnSpyDir "$name.exe.config") -Force
    Write-Host "  headless (net48) -> $DnSpyDir\$name.exe"
    return
}

$consoleDll = @($DnSpyDir, (Join-Path $DnSpyDir 'bin')) |
    ForEach-Object { Join-Path $_ 'dnSpy.Console.dll' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $consoleDll) {
    throw "$DnSpyDir is not a .NET dnSpy (no dnSpy.Console.dll), but $BuildOutput is the net10 build."
}
$appDir = Split-Path $consoleDll -Parent

# The apphost must match the bundle's architecture: a self-contained bundle's runtime (hostfxr in
# bin\) is native code, and an x64 apphost cannot load the win-x86 bundle's x86 hostfxr.
$ourArch = Get-PeMachine (Join-Path $BuildOutput "$name.exe")
$dnSpyArch = Get-PeMachine (Join-Path $DnSpyDir 'dnSpy.Console.exe')
if ($ourArch -ne $dnSpyArch) {
    throw "The headless apphost in $BuildOutput is $ourArch but this dnSpy's dnSpy.Console.exe is $dnSpyArch. " +
          "Build it for that architecture (dotnet build -f net10.0-windows -r win-$dnSpyArch --self-contained false) and pass that output."
}

Copy-WithPdb (Join-Path $BuildOutput "$name.dll") $appDir
Copy-Item (Join-Path $appDir 'dnSpy.Console.runtimeconfig.json') (Join-Path $appDir "$name.runtimeconfig.json") -Force
$exe = Join-Path $DnSpyDir "$name.exe"
Copy-Item (Join-Path $BuildOutput "$name.exe") $exe -Force
if ($appDir -ne $DnSpyDir) {
    if (-not (Test-Path $AppHostPatcher)) { throw "AppHostPatcher not found at $AppHostPatcher (build it with dnSpy's build.ps1)." }
    & $AppHostPatcher $exe -d (Split-Path $appDir -Leaf)
    if ($LASTEXITCODE -ne 0) { throw "AppHostPatcher failed on $exe" }
}
Write-Host "  headless (net10) -> $exe (app: $appDir)"
