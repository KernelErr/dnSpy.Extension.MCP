#Requires -Version 5.0
<#
.SYNOPSIS
  End-to-end smoke test for the MCP extension's IL view/edit/save tools.

.DESCRIPTION
  1. Builds TestIL.dll fixture (via build-fixture.ps1).
  2. Deploys the just-built MCP extension DLL into dnSpy's Extensions folder.
  3. Launches dnSpy (net10.0-windows release build) with TestIL.dll preloaded.
  4. Waits for /health on the configured port (default 3000, with +N fallback).
  5. Walks through a sequence of list_methods / decompile_method / get_method_il /
     patch_method_il / revert_method_il / save_assembly calls, asserting expected
     responses and on-disk state.

  Each step prints PASS/FAIL. Non-zero exit code on first FAIL.

.PARAMETER Port
  First port to probe (default 3000). Falls back to 3000..3019 if dnSpy landed
  on a later port due to the in-use fallback.

.PARAMETER DnSpyExe
  Path to dnSpy.exe. Defaults to the Release/net10.0-windows build in-tree.

.PARAMETER SkipBuild
  Skip `dotnet build` steps (assume fixture + extension are already up to date).

.PARAMETER KeepDnSpy
  Leave dnSpy running after the tests exit (handy for ad-hoc follow-up curls).
#>

[CmdletBinding()]
param(
    # Default 3100 because WSL's wslrelay.exe typically holds 3000 on dev boxes,
    # which collides with HttpListener.Start even when TcpListener probes clean.
    [int]$Port = 3100,
    [string]$DnSpyExe,
    [switch]$SkipBuild,
    [switch]$KeepDnSpy
)

$ErrorActionPreference = 'Stop'
$fixtureDir = $PSScriptRoot
if (-not $DnSpyExe) { $DnSpyExe = Join-Path $fixtureDir '..\..\..\..\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.exe' }

# Force dnSpy's MCP server onto the port we want by rewriting the persisted setting.
# The GUID must match McpSettingsImpl's SETTINGS_GUID.
function Set-McpPortInSettings([int]$p)
{
    $cfg = Join-Path $env:APPDATA 'dnSpy\dnSpy.xml'
    if (-not (Test-Path $cfg)) { return }
    [xml]$doc = Get-Content -Raw -Path $cfg
    $sect = $doc.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']")
    if ($sect -eq $null) { return }
    $sect.SetAttribute('Port', "$p")
    $sect.SetAttribute('EnableServer', 'True')
    $doc.Save($cfg)
    Write-Host "  dnSpy.xml: set MCP Port=$p, EnableServer=True"
}
$extDir = Split-Path $fixtureDir -Parent | Split-Path -Parent
$binFixture = Join-Path $fixtureDir 'bin'
$testDll = Join-Path $binFixture 'TestIL.dll'
$extDllSrc = Join-Path $extDir 'bin\Release\net10.0-windows\dnSpy.Extension.MCP.x.dll'
$resolved = Resolve-Path $DnSpyExe -ErrorAction SilentlyContinue
if (-not $resolved) { throw "dnSpy.exe not found at $DnSpyExe. Build the Release/net10.0-windows config first." }
$dnSpyExeFull = $resolved.Path
$extDeployDir = Join-Path (Split-Path $dnSpyExeFull -Parent) 'bin\Extensions\dnSpy.Extension.MCP'

$pass = 0; $fail = 0
function Assert($condition, [string]$label, [string]$detail = '')
{
    if ($condition) { Write-Host "  PASS  $label" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  FAIL  $label  $detail" -ForegroundColor Red; $script:fail++ }
}

function Rpc([string]$tool, [hashtable]$arguments, [int]$p = $script:Port)
{
    $payload = @{ jsonrpc='2.0'; id=1; method='tools/call'; params=@{ name=$tool; arguments=$arguments } } | ConvertTo-Json -Depth 10 -Compress
    $resp = Invoke-WebRequest -Uri "http://localhost:$p/" -Method Post -ContentType 'application/json' -Body $payload -UseBasicParsing
    $jr = $resp.Content | ConvertFrom-Json
    if ($jr.error) { throw "Tool $tool RPC error: $($jr.error.message)" }
    if ($jr.result.isError -eq $true) { throw "Tool $tool returned error: $($jr.result.content[0].text)" }
    $text = $jr.result.content[0].text
    return ($text | ConvertFrom-Json)
}

function RpcText([string]$tool, [hashtable]$arguments, [int]$p = $script:Port)
{
    $payload = @{ jsonrpc='2.0'; id=1; method='tools/call'; params=@{ name=$tool; arguments=$arguments } } | ConvertTo-Json -Depth 10 -Compress
    $resp = Invoke-WebRequest -Uri "http://localhost:$p/" -Method Post -ContentType 'application/json' -Body $payload -UseBasicParsing
    $jr = $resp.Content | ConvertFrom-Json
    if ($jr.error) { throw "Tool $tool RPC error: $($jr.error.message)" }
    if ($jr.result.isError -eq $true) { throw "Tool $tool returned error: $($jr.result.content[0].text)" }
    return $jr.result.content[0].text
}

# ----- step 1: build fixture -----
if (-not $SkipBuild)
{
    Write-Host "[1] Building TestIL.dll fixture"
    & (Join-Path $fixtureDir 'build-fixture.ps1') -Clean
    Write-Host ""

    Write-Host "[2] Building MCP extension (Release)"
    Push-Location $extDir
    try { & dotnet build -c Release --nologo -v q; if ($LASTEXITCODE -ne 0) { throw "extension build failed" } }
    finally { Pop-Location }
    Write-Host ""
}

if (-not (Test-Path $testDll)) { throw "Fixture DLL missing: $testDll" }
if (-not (Test-Path $extDllSrc)) { throw "Extension DLL missing: $extDllSrc" }

$originalHash = (Get-FileHash -Algorithm SHA256 $testDll).Hash
Write-Host "[*] Fixture SHA256 (pre-patch): $originalHash"

# ----- step 3: deploy extension -----
Write-Host "[3] Deploying extension → $extDeployDir"
if (-not (Test-Path $extDeployDir)) { New-Item -ItemType Directory -Path $extDeployDir | Out-Null }
Copy-Item $extDllSrc $extDeployDir -Force
$pdb = [System.IO.Path]::ChangeExtension($extDllSrc, '.pdb')
if (Test-Path $pdb) { Copy-Item $pdb $extDeployDir -Force }

# ----- step 4: launch dnSpy with the fixture preloaded -----
Write-Host "[4] Launching dnSpy + loading $testDll"
Set-McpPortInSettings $Port
$dnSpyProc = Start-Process -FilePath $dnSpyExeFull -ArgumentList $testDll -PassThru

# Poll for /health on the configured port with +N fallback (McpServer's FindAvailablePort tries up to 20).
$found = $false
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline -and -not $found)
{
    for ($p = $Port; $p -lt $Port + 20; $p++)
    {
        try
        {
            $h = Invoke-WebRequest -Uri "http://localhost:$p/health" -UseBasicParsing -TimeoutSec 1
            if ($h.StatusCode -eq 200) { $script:Port = $p; $found = $true; break }
        }
        catch { }
    }
    if (-not $found) { Start-Sleep -Milliseconds 500 }
}
if (-not $found) { throw "MCP server never came up on ports $Port..$($Port+19)" }
Write-Host "  MCP server is up on port $script:Port"

# Wait for TestIL to actually appear in the tree. dnSpy loads CLI-provided files
# asynchronously, so the health port can come up before the assembly is indexed.
$loaded = $false
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and -not $loaded)
{
    try
    {
        $asmList = Rpc 'list_assemblies' @{}
        if ($asmList | Where-Object { $_.Name -eq 'TestIL' }) { $loaded = $true; break }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $loaded) { throw "TestIL never appeared in list_assemblies — did dnSpy fail to load the fixture?" }
Write-Host "  TestIL assembly is loaded"
Write-Host ""

try
{
    # ----- step 5-7: reads on TestIL.Simple -----
    Write-Host "[5] list_methods on TestIL.Simple"
    $listed = Rpc 'list_methods' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple' }
    $addOverloads = $listed.items | Where-Object { $_.name -eq 'Add' }
    Assert ($addOverloads.Count -eq 2) "two Add overloads in list_methods" "got $($addOverloads.Count)"
    $addOne = $listed.items | Where-Object { $_.name -eq 'AddOne' } | Select-Object -First 1
    Assert ($addOne -ne $null) "AddOne present"
    Assert ($addOne.parameter_types.Count -eq 1 -and $addOne.parameter_types[0] -eq 'System.Int32') "AddOne param types"
    Assert ($addOne.token -gt 0) "AddOne MDToken > 0"

    Write-Host ""
    Write-Host "[6] decompile_method Add(int,int,int) — overload disambiguation"
    $srcText = RpcText 'decompile_method' @{
        assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='Add';
        parameter_types=@('System.Int32','System.Int32','System.Int32')
    }
    Assert ($srcText -match 'a \+ b \+ c' -or $srcText -match 'int c') "3-arg overload selected (source references c)" "src:`n$srcText"
    # Also confirm the 2-arg lookup works.
    $srcText2 = RpcText 'decompile_method' @{
        assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='Add';
        parameter_types=@('System.Int32','System.Int32')
    }
    Assert (($srcText2 -match 'a \+ b') -and ($srcText2 -notmatch '\+ c')) "2-arg overload selected (no c)"
    # Ambiguous call without disambiguator should get a helpful error.
    $ambiguous = $null
    try { Rpc 'decompile_method' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='Add' } | Out-Null }
    catch { $ambiguous = $_.Exception.Message }
    Assert ($ambiguous -and ($ambiguous -match 'overloaded' -or $ambiguous -match 'parameter_types')) "ambiguous Add call errors with helpful guidance" "got: $ambiguous"

    Write-Host ""
    Write-Host "[7] get_method_il on AddOne"
    $il = Rpc 'get_method_il' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne' }
    $opcodes = @($il.instructions.opcode)
    Assert ($opcodes -contains 'ldarg.0') "AddOne IL contains ldarg.0"
    Assert ($opcodes -contains 'add') "AddOne IL contains add"
    Assert ($opcodes -contains 'ret') "AddOne IL contains ret"
    $one = $il.instructions | Where-Object { $_.opcode -match '^ldc\.i4' -and $_.operand -eq 'int:1' }
    if (-not $one) { $one = $il.instructions | Where-Object { $_.opcode -eq 'ldc.i4.1' } }
    Assert ($one -ne $null) "AddOne loads constant 1 (ldc.i4.1 or ldc.i4 int:1)"

    # ----- step 8-10: patch + revert in memory -----
    Write-Host ""
    Write-Host "[8] patch_method_il AddOne: replace the +1 constant with +41"
    $loadOneIdx = ($il.instructions | Where-Object { $_.opcode -match '^ldc\.i4' -and ($_.operand -eq 'int:1' -or $_.opcode -eq 'ldc.i4.1') } | Select-Object -First 1).index
    Assert ($loadOneIdx -ne $null) "found ldc.i4 loading 1"
    $patched = Rpc 'patch_method_il' @{
        assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne';
        edits = @(@{ op='replace'; index=$loadOneIdx; opcode='ldc.i4'; operand='int:41' })
    }
    Assert ($patched.edits_applied -eq 1) "edits_applied == 1"
    Assert ($patched.has_pending_patch -eq $true) "has_pending_patch is true"
    $after = $patched.instructions | Where-Object { $_.index -eq $loadOneIdx }
    Assert ($after.opcode -eq 'ldc.i4' -and $after.operand -eq 'int:41') "instruction is now ldc.i4 int:41"

    Write-Host ""
    Write-Host "[9] revert_method_il AddOne"
    $reverted = Rpc 'revert_method_il' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne' }
    Assert ($reverted.reverted -eq $true) "revert returned reverted=true"
    $restored = $reverted.instructions | Where-Object { $_.index -eq $loadOneIdx }
    $originalOpcode = ($il.instructions | Where-Object { $_.index -eq $loadOneIdx }).opcode
    $originalOperand = ($il.instructions | Where-Object { $_.index -eq $loadOneIdx }).operand
    Assert (($restored.opcode -eq $originalOpcode) -and ($restored.operand -eq $originalOperand)) "AddOne instruction restored to original shape" "restored=$($restored.opcode)/$($restored.operand) original=$originalOpcode/$originalOperand"

    # ----- step 10: save to a side path (non-destructive) -----
    Write-Host ""
    Write-Host "[10] re-apply patch + save_assembly to side path"
    Rpc 'patch_method_il' @{
        assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne';
        edits=@(@{ op='replace'; index=$loadOneIdx; opcode='ldc.i4'; operand='int:41' })
    } | Out-Null
    $sidePath = Join-Path $binFixture 'TestIL.patched.dll'
    if (Test-Path $sidePath) { Remove-Item $sidePath -Force }
    $saved = Rpc 'save_assembly' @{ assembly_name='TestIL'; output_path=$sidePath }
    Assert ((Test-Path $sidePath)) "side-path file written"
    Assert ($saved.backup_path -eq $null) "no backup for side-path save"
    Assert ($saved.bytes_written -gt 0) "bytes_written > 0"

    # Prove the IL change is persisted on disk.
    $verify = & powershell -NoProfile -Command "[Reflection.Assembly]::LoadFile('$sidePath') | Out-Null; [TestIL.Simple]::AddOne(10)"
    Assert ($verify -eq '51') "AddOne(10) on disk returns 51 (was 11 before patch)" "got $verify"

    # ----- step 11: overwrite original with backup -----
    Write-Host ""
    Write-Host "[11] save_assembly overwriting original (backup-then-overwrite)"
    $savedOver = Rpc 'save_assembly' @{ assembly_name='TestIL' }
    Assert ($savedOver.backup_path -ne $null -and (Test-Path $savedOver.backup_path)) "backup exists" "backup=$($savedOver.backup_path)"
    $backupHash = (Get-FileHash -Algorithm SHA256 $savedOver.backup_path).Hash
    Assert ($backupHash -eq $originalHash) "backup SHA matches pre-patch bytes" "backup=$backupHash original=$originalHash"
    $newHash = (Get-FileHash -Algorithm SHA256 $testDll).Hash
    Assert ($newHash -ne $originalHash) "original file bytes changed"

    Write-Host ""
    Write-Host "[12] cleanup revert (drop snapshot)"
    $reverted2 = Rpc 'revert_method_il' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne' }
    Assert ($reverted2.reverted -eq $true) "second revert works"
}
finally
{
    Write-Host ""
    Write-Host "===== SUMMARY: $pass pass / $fail fail ====="
    if (-not $KeepDnSpy -and $dnSpyProc -and -not $dnSpyProc.HasExited)
    {
        Write-Host "Stopping dnSpy PID $($dnSpyProc.Id)"
        try { Stop-Process -Id $dnSpyProc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    elseif ($KeepDnSpy -and $dnSpyProc)
    {
        Write-Host "Leaving dnSpy (PID $($dnSpyProc.Id)) running per -KeepDnSpy"
    }
}

if ($fail -ne 0) { exit 1 } else { exit 0 }
