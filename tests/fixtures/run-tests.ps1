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
# Quote the fixture path: Start-Process does not auto-quote -ArgumentList entries, so a
# path containing spaces (e.g. C:\Users\Rui Li\...) would reach dnSpy split at the space
# and the fixture would silently fail to load.
$dnSpyProc = Start-Process -FilePath $dnSpyExeFull -ArgumentList "`"$testDll`"" -PassThru

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

    # ----- step 13: search_string_literals (reverse lookup) -----
    Write-Host ""
    Write-Host "[13] search_string_literals SAVEFILE — find every method using it"
    $hits = Rpc 'search_string_literals' @{ query='SAVEFILE'; assembly_name='TestIL' }
    $saveHits = $hits.items | Where-Object { $_.value -eq 'SAVEFILE' }
    Assert ($saveHits.Count -eq 2) "SAVEFILE found in 2 methods (SaveGame + LoadGame)" "got $($saveHits.Count)"
    $methodsWithSave = @($saveHits | ForEach-Object { $_.method } | Sort-Object -Unique)
    Assert (($methodsWithSave -contains 'SaveGame') -and ($methodsWithSave -contains 'LoadGame')) "both SaveGame and LoadGame returned" "got $($methodsWithSave -join ',')"
    Assert ($saveHits[0].method_token -gt 0 -and $saveHits[0].type -eq 'TestIL.StringKeys') "hit carries type + MDToken"
    # Unique key resolves to exactly one method. Scope to TestIL: a real game assembly may also be
    # open in the dev's dnSpy session (session restore), and game strings could collide.
    $umbra = Rpc 'search_string_literals' @{ query='TheFinaleUmbra'; assembly_name='TestIL' }
    $umbraHits = @($umbra.items | Where-Object { $_.value -eq 'TheFinaleUmbra' })
    Assert ($umbraHits.Count -eq 1 -and $umbraHits[0].method -eq 'LoadGame') "TheFinaleUmbra resolves to LoadGame only" "count=$($umbraHits.Count)"
    # Wildcard match anchored to the whole literal.
    $wild = Rpc 'search_string_literals' @{ query='PlayerPrefs*'; assembly_name='TestIL' }
    $wildHits = @($wild.items | Where-Object { $_.value -eq 'PlayerPrefs.Score' })
    Assert ($wildHits.Count -eq 1) "wildcard 'PlayerPrefs*' matches PlayerPrefs.Score" "count=$($wildHits.Count)"

    # ----- step 14: list_string_constants (per-type / per-method) -----
    Write-Host ""
    Write-Host "[14] list_string_constants on TestIL.StringKeys"
    $allStrings = Rpc 'list_string_constants' @{ assembly_name='TestIL'; type_full_name='TestIL.StringKeys' }
    $values = @($allStrings.items | ForEach-Object { $_.value })
    Assert (($values -contains 'SAVEFILE') -and ($values -contains 'PlayerPrefs.Score') -and ($values -contains 'TheFinaleUmbra')) "type-level listing returns all keys" "got $($values -join ',')"
    Assert (($values | Where-Object { $_ -eq 'SAVEFILE' }).Count -eq 2) "SAVEFILE listed twice (once per method)"
    # Method scope narrows to a single method.
    $loadOnly = Rpc 'list_string_constants' @{ assembly_name='TestIL'; type_full_name='TestIL.StringKeys'; method_name='LoadGame' }
    $loadValues = @($loadOnly.items | ForEach-Object { $_.value } | Sort-Object)
    Assert (($loadValues.Count -eq 2) -and ($loadValues -contains 'SAVEFILE') -and ($loadValues -contains 'TheFinaleUmbra')) "method scope returns only LoadGame's strings" "got $($loadValues -join ',')"

    # ----- step 15: find_callers -----
    Write-Host ""
    Write-Host "[15] find_callers Simple.AddOne — who calls it"
    $callers = Rpc 'find_callers' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne' }
    $addOneCallers = @($callers.items | Where-Object { $_.caller_type -eq 'TestIL.Refs' -and $_.caller_method -eq 'CallsAddOne' })
    Assert ($addOneCallers.Count -eq 1) "Refs.CallsAddOne found as a caller of AddOne" "count=$($addOneCallers.Count)"
    Assert ($addOneCallers[0].opcode -eq 'call' -and $addOneCallers[0].caller_token -gt 0) "caller carries opcode + MDToken"
    # A never-called method yields no callers.
    $noCallers = Rpc 'find_callers' @{ assembly_name='TestIL'; type_full_name='TestIL.Refs'; method_name='CallsAddOne' }
    $selfCallers = @($noCallers.items | Where-Object { $_.caller_assembly -eq 'TestIL' })
    Assert ($selfCallers.Count -eq 0) "CallsAddOne has no in-assembly callers" "count=$($selfCallers.Count)"

    # ----- step 16: find_references (field / type / string) -----
    Write-Host ""
    Write-Host "[16] find_references field sceneToLoad — read + write sites"
    $fieldRefs = Rpc 'find_references' @{ target_kind='field'; assembly_name='TestIL'; type_full_name='TestIL.Refs'; field_name='sceneToLoad' }
    $refMethods = @($fieldRefs.items | Where-Object { $_.caller_assembly -eq 'TestIL' } | ForEach-Object { $_.caller_method } | Sort-Object -Unique)
    Assert (($refMethods -contains 'GetScene') -and ($refMethods -contains 'SetScene')) "sceneToLoad referenced by GetScene (read) + SetScene (write)" "got $($refMethods -join ',')"
    $opcodes = @($fieldRefs.items | Where-Object { $_.caller_assembly -eq 'TestIL' } | ForEach-Object { $_.opcode } | Sort-Object -Unique)
    Assert (($opcodes -contains 'ldsfld') -and ($opcodes -contains 'stsfld')) "both ldsfld and stsfld observed" "got $($opcodes -join ',')"

    Write-Host ""
    Write-Host "[16b] find_references type Simple — ldtoken site"
    $typeRefs = Rpc 'find_references' @{ target_kind='type'; assembly_name='TestIL'; type_full_name='TestIL.Simple' }
    $typeRefMethods = @($typeRefs.items | Where-Object { $_.caller_assembly -eq 'TestIL' } | ForEach-Object { $_.caller_method })
    Assert ($typeRefMethods -contains 'RefType') "Simple referenced via typeof() in Refs.RefType" "got $($typeRefMethods -join ',')"

    Write-Host ""
    Write-Host "[16c] find_references string (parity with search_string_literals)"
    $strRefs = Rpc 'find_references' @{ target_kind='string'; query='TheFinaleUmbra' }
    $strHits = @($strRefs.items | Where-Object { $_.reference -eq 'TheFinaleUmbra' -and $_.caller_assembly -eq 'TestIL' })
    Assert ($strHits.Count -eq 1 -and $strHits[0].caller_method -eq 'LoadGame') "string xref resolves to LoadGame" "count=$($strHits.Count)"

    # ----- step 17: nested / compiler-generated state machines -----
    Write-Host ""
    Write-Host "[17] iterator state machine: discoverable + addressable"
    $coroSearch = Rpc 'search_types' @{ query='*DoCoroutine*' }
    $coroSM = $coroSearch.items | Where-Object { $_.FullName -match 'DoCoroutine' -and $_.IsCompilerGenerated -eq $true } | Select-Object -First 1
    Assert ($coroSM -ne $null) "iterator state machine surfaced by search_types"
    Assert ($coroSM.IsNested -eq $true) "state machine flagged is_nested"
    # Address the nested type's MoveNext directly -> the real body (was unreachable before).
    $coroMoveNext = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name=$coroSM.FullName; method_name='MoveNext' }
    Assert ($coroMoveNext -match 'counter') "MoveNext of <DoCoroutine>d__ decompiles to real body (references counter)" "src:`n$coroMoveNext"
    # Separator tolerance: same type addressed with '.' instead of '/'.
    $coroDotName = $coroSM.FullName -replace '/', '.'
    $coroViaDot = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name=$coroDotName; method_name='MoveNext' }
    Assert ($coroViaDot -match 'counter') "nested type addressable with '.' separator too ($coroDotName)"

    Write-Host ""
    Write-Host "[17b] async void state machine: discoverable + addressable"
    $asyncSearch = Rpc 'search_types' @{ query='*DoAsync*' }
    $asyncSM = $asyncSearch.items | Where-Object { $_.FullName -match 'DoAsync' -and $_.IsCompilerGenerated -eq $true } | Select-Object -First 1
    Assert ($asyncSM -ne $null) "async void state machine surfaced by search_types"
    $asyncMoveNext = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name=$asyncSM.FullName; method_name='MoveNext' }
    Assert ($asyncMoveNext -match '100') "MoveNext of <DoAsync>d__ decompiles to real body (counter += 100)" "src:`n$asyncMoveNext"

    Write-Host ""
    Write-Host "[17c] list_types includes nested types by default, excludes with include_nested=false"
    $withNested = Rpc 'list_types' @{ assembly_name='TestIL' }
    $topOnly = Rpc 'list_types' @{ assembly_name='TestIL'; include_nested=$false }
    Assert ($withNested.total_count -gt $topOnly.total_count) "include_nested surfaces extra (nested) types" "withNested=$($withNested.total_count) topOnly=$($topOnly.total_count)"

    Write-Host ""
    Write-Host "[17d] decompile_method on kickoffs always exposes the real body"
    # The whole point: decompiling the kickoff yields the real logic — either ILSpy reconstructs
    # await/yield inline, or (when its pattern match misses, e.g. on Unity output) we append the
    # raw compiler-generated MoveNext. Either way the body (which touches 'counter') is present.
    $coroKickoff = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name='TestIL.Machines'; method_name='DoCoroutine' }
    Assert ($coroKickoff -match 'counter') "DoCoroutine decompile exposes real body (yield reconstruction or appended MoveNext)" "src:`n$coroKickoff"
    # async void is the case the user hit: kickoff stub only. With the rescue, the body shows up.
    $asyncKickoff = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name='TestIL.Machines'; method_name='DoAsync' }
    Assert ($asyncKickoff -match '100') "DoAsync (async void) decompile exposes real body (counter += 100)" "src:`n$asyncKickoff"
    # Opt-out returns only the kickoff (no appended state machine).
    $asyncBare = RpcText 'decompile_method' @{ assembly_name='TestIL'; type_full_name='TestIL.Machines'; method_name='DoAsync'; include_state_machine=$false }
    Assert ($asyncBare -notmatch 'Raw compiler-generated state machine') "include_state_machine=false suppresses the appended MoveNext"

    # ----- step 18: filtering / scoping / compact -----
    Write-Host ""
    Write-Host "[18] list_assemblies name_filter"
    $asmFiltered = @(Rpc 'list_assemblies' @{ name_filter='TestIL' })
    Assert ((@($asmFiltered | Where-Object { $_.Name -eq 'TestIL' }).Count -eq 1)) "name_filter keeps TestIL"
    Assert ((@($asmFiltered | Where-Object { $_.Name -eq 'mscorlib' -or $_.Name -eq 'System' }).Count -eq 0)) "name_filter drops framework assemblies"

    Write-Host ""
    Write-Host "[18b] search_types scoped to one assembly"
    $scoped = Rpc 'search_types' @{ query='*Simple*'; assembly_name='TestIL' }
    Assert ((@($scoped.items | Where-Object { $_.AssemblyName -ne 'TestIL' }).Count -eq 0)) "scoped search returns only TestIL types"
    Assert ((@($scoped.items | Where-Object { $_.FullName -eq 'TestIL.Simple' }).Count -eq 1)) "TestIL.Simple found in scope"

    Write-Host ""
    Write-Host "[18c] list_types names_only + page_size + base_type"
    $namesOnly = Rpc 'list_types' @{ assembly_name='TestIL'; names_only=$true; page_size=50 }
    Assert ($namesOnly.items -contains 'TestIL.Simple') "names_only returns FullName strings"
    Assert ($namesOnly.returned_count -le 50) "page_size respected"
    $subs = Rpc 'list_types' @{ assembly_name='TestIL'; base_type='BaseEntity'; names_only=$true; page_size=50 }
    $subNames = @($subs.items)
    Assert (($subNames -contains 'TestIL.Player') -and ($subNames -contains 'TestIL.Enemy') -and ($subNames -contains 'TestIL.Boss')) "base_type lists transitive subclasses (incl. Boss : Enemy : BaseEntity)" "got $($subNames -join ',')"
    Assert ($subNames -notcontains 'TestIL.Simple') "base_type excludes non-subclasses"

    Write-Host ""
    Write-Host "[18d] get_type_info compact + members_filter"
    $gi = Rpc 'get_type_info' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; members_filter='Add*'; compact=$true }
    $mnames = @($gi.Methods | ForEach-Object { $_.Name } | Sort-Object -Unique)
    Assert (($mnames -contains 'Add') -and ($mnames -contains 'AddOne')) "members_filter returns Add/AddOne" "got $($mnames -join ',')"
    Assert ($mnames -notcontains 'Greet') "members_filter excludes non-matching members"
    Assert (@($gi.Methods)[0].PSObject.Properties.Name -notcontains 'Parameters') "compact drops per-parameter detail"

    Write-Host ""
    Write-Host "[18e] decompile_by_token (method, by token alone)"
    $simpleMethods = Rpc 'list_methods' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple' }
    $addOneTok = ($simpleMethods.items | Where-Object { $_.name -eq 'AddOne' } | Select-Object -First 1).token
    Assert ($addOneTok -gt 0) "got AddOne token from list_methods"
    $byTok = RpcText 'decompile_by_token' @{ token=$addOneTok; assembly_name='TestIL' }
    Assert ($byTok -match 'AddOne') "decompile_by_token resolves AddOne without a type name"

    Write-Host ""
    Write-Host "[18f] decompile_by_token reaches nested state machine MoveNext"
    $smMethods = Rpc 'list_methods' @{ assembly_name='TestIL'; type_full_name=$asyncSM.FullName }
    $mnTok = ($smMethods.items | Where-Object { $_.name -eq 'MoveNext' } | Select-Object -First 1).token
    $mnByTok = RpcText 'decompile_by_token' @{ token=$mnTok; assembly_name='TestIL' }
    Assert ($mnByTok -match '100') "decompile_by_token reaches <DoAsync>d__ MoveNext (counter += 100)"

    # ----- step 19: search_members (global member search — the missing half of Ctrl+Shift+K) -----
    Write-Host ""
    Write-Host "[19] search_members method by name (no declaring type known)"
    $mAddOne = Rpc 'search_members' @{ query='AddOne'; kinds=@('method'); assembly_name='TestIL' }
    $aoHits = @($mAddOne.items | Where-Object { $_.name -eq 'AddOne' })
    Assert ($aoHits.Count -eq 1) "AddOne found by member search" "count=$($aoHits.Count)"
    Assert ($aoHits[0].declaring_type -eq 'TestIL.Simple' -and $aoHits[0].member_kind -eq 'method') "hit carries declaring_type + member_kind"
    Assert ($aoHits[0].is_static -eq $true -and $aoHits[0].is_public -eq $true) "hit carries is_static/is_public"
    Assert ($aoHits[0].token -gt 0) "hit carries MDToken"
    # The whole point: a bare-name hit is addressable. Feed its token straight to decompile_by_token.
    $aoSrc = RpcText 'decompile_by_token' @{ token=$aoHits[0].token; assembly_name='TestIL' }
    Assert ($aoSrc -match 'AddOne') "search_members token feeds decompile_by_token"

    Write-Host ""
    Write-Host "[19b] search_members field / property / event kinds"
    $mField = Rpc 'search_members' @{ query='sceneToLoad'; kinds=@('field'); assembly_name='TestIL' }
    $fHits = @($mField.items | Where-Object { $_.name -eq 'sceneToLoad' })
    Assert ($fHits.Count -eq 1 -and $fHits[0].declaring_type -eq 'TestIL.Refs' -and $fHits[0].member_kind -eq 'field') "field sceneToLoad found" "count=$($fHits.Count)"
    $mProp = Rpc 'search_members' @{ query='Health'; kinds=@('property'); assembly_name='TestIL' }
    $pHits = @($mProp.items | Where-Object { $_.name -eq 'Health' })
    Assert ($pHits.Count -eq 1 -and $pHits[0].member_kind -eq 'property' -and $pHits[0].declaring_type -eq 'TestIL.Members') "property Health found" "count=$($pHits.Count)"
    $mEvent = Rpc 'search_members' @{ query='OnDied'; kinds=@('event'); assembly_name='TestIL' }
    $eHits = @($mEvent.items | Where-Object { $_.name -eq 'OnDied' })
    Assert ($eHits.Count -eq 1 -and $eHits[0].member_kind -eq 'event') "event OnDied found" "count=$($eHits.Count)"

    Write-Host ""
    Write-Host "[19c] kinds filter narrows results"
    # 'OnDied' as an event also has a compiler-generated backing field + add_/remove_ methods.
    # Default (all kinds) surfaces more than one member_kind; kinds=[event] keeps only the event.
    $mAll = Rpc 'search_members' @{ query='OnDied'; assembly_name='TestIL' }
    $allKinds = @($mAll.items | ForEach-Object { $_.member_kind } | Sort-Object -Unique)
    Assert ($allKinds.Count -ge 2) "default kinds surfaces event + backing field/accessors" "kinds=$($allKinds -join ',')"
    $onlyEvent = @($mEvent.items | Where-Object { $_.member_kind -ne 'event' })
    Assert ($onlyEvent.Count -eq 0) "kinds=[event] excludes the backing field and accessor methods"

    Write-Host ""
    Write-Host "[19d] wildcard + names_only + invalid kind"
    $mWild = Rpc 'search_members' @{ query='*Game'; kinds=@('method'); assembly_name='TestIL' }
    $wildNames = @($mWild.items | ForEach-Object { $_.name } | Sort-Object -Unique)
    Assert (($wildNames -contains 'SaveGame') -and ($wildNames -contains 'LoadGame')) "wildcard '*Game' matches SaveGame + LoadGame" "got $($wildNames -join ',')"
    Assert ($wildNames -notcontains 'AddOne') "wildcard '*Game' excludes AddOne"
    $mNames = Rpc 'search_members' @{ query='AddOne'; kinds=@('method'); assembly_name='TestIL'; names_only=$true }
    Assert (@($mNames.items | Where-Object { $_ -match 'AddOne' }).Count -ge 1) "names_only returns signature strings"
    $badKind = $null
    try { Rpc 'search_members' @{ query='AddOne'; kinds=@('methods') } | Out-Null }
    catch { $badKind = $_.Exception.Message }
    Assert ($badKind -and ($badKind -match 'unknown member kind')) "invalid kind errors with guidance" "got: $badKind"

    # ----- step 20: find_callees (dnSpy Analyze "Uses" — outgoing references of one method) -----
    Write-Host ""
    Write-Host "[20] find_callees on Refs.Uses"
    $callees = Rpc 'find_callees' @{ assembly_name='TestIL'; type_full_name='TestIL.Refs'; method_name='Uses' }
    $mCallees = @($callees.items | Where-Object { $_.ref_kind -eq 'method' })
    $mSigs = ($mCallees | ForEach-Object { $_.signature }) -join "`n"
    Assert ($mSigs -match 'AddOne') "find_callees lists Simple.AddOne as a method callee"
    Assert ($mSigs -match 'Simple::Add\(') "find_callees lists Simple.Add as a method callee"
    $fCallees = @($callees.items | Where-Object { $_.ref_kind -eq 'field' -and $_.signature -match 'sceneToLoad' })
    Assert ($fCallees.Count -eq 1) "find_callees lists sceneToLoad once (deduped across sites)" "count=$($fCallees.Count)"
    $slOps = @($fCallees[0].opcodes)
    Assert (($slOps -contains 'ldsfld') -and ($slOps -contains 'stsfld')) "deduped field row carries both ldsfld + stsfld" "got $($slOps -join ',')"
    Assert ($fCallees[0].occurrences -ge 2) "field callee occurrences counts both sites" "got $($fCallees[0].occurrences)"
    # A callee row's token is addressable: feed it straight to decompile_by_token.
    $addCallee = $mCallees | Where-Object { $_.signature -match 'AddOne' } | Select-Object -First 1
    Assert ($addCallee.token -gt 0) "callee carries resolved MDToken"
    $cbt = RpcText 'decompile_by_token' @{ token=$addCallee.token; assembly_name='TestIL' }
    Assert ($cbt -match 'AddOne') "find_callees token feeds decompile_by_token"

    # ----- step 21: find_overrides (dnSpy Analyze "Overridden By" / "Overrides") -----
    Write-Host ""
    Write-Host "[21] find_overrides overridden_by on BaseEntity.Attack"
    $ob = Rpc 'find_overrides' @{ assembly_name='TestIL'; type_full_name='TestIL.BaseEntity'; method_name='Attack' }
    $obTypes = @($ob.items | Where-Object { $_.assembly -eq 'TestIL' } | ForEach-Object { $_.type } | Sort-Object -Unique)
    Assert (($obTypes -contains 'TestIL.Player') -and ($obTypes -contains 'TestIL.Enemy') -and ($obTypes -contains 'TestIL.Boss')) "overridden_by lists Player + Enemy + Boss (transitive)" "got $($obTypes -join ',')"
    $bossOv = $ob.items | Where-Object { $_.type -eq 'TestIL.Boss' } | Select-Object -First 1
    Assert ($bossOv -and $bossOv.method -eq 'Attack' -and $bossOv.token -gt 0) "override hit carries method + MDToken"

    Write-Host ""
    Write-Host "[21b] find_overrides overrides on Boss.Attack (walk up the base chain)"
    $ov = Rpc 'find_overrides' @{ direction='overrides'; assembly_name='TestIL'; type_full_name='TestIL.Boss'; method_name='Attack' }
    $ovTypes = @($ov.items | ForEach-Object { $_.type })
    Assert ($ovTypes -contains 'TestIL.Enemy') "overrides finds Enemy.Attack (immediate base)" "got $($ovTypes -join ',')"
    Assert ($ovTypes -contains 'TestIL.BaseEntity') "overrides finds BaseEntity.Attack (slot origin)"

    Write-Host ""
    Write-Host "[21c] find_overrides: non-virtual yields nothing; invalid direction errors"
    $none = Rpc 'find_overrides' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple'; method_name='AddOne' }
    $noneHits = @($none.items | Where-Object { $_.assembly -eq 'TestIL' })
    Assert ($noneHits.Count -eq 0) "non-virtual AddOne has no overrides" "count=$($noneHits.Count)"
    $badDir = $null
    try { Rpc 'find_overrides' @{ direction='sideways'; assembly_name='TestIL'; type_full_name='TestIL.BaseEntity'; method_name='Attack' } | Out-Null }
    catch { $badDir = $_.Exception.Message }
    Assert ($badDir -and ($badDir -match 'unknown direction')) "invalid direction errors with guidance" "got: $badDir"

    Write-Host ""
    Write-Host "[21d] find_overrides overridden_by on an interface method (implementors)"
    $impl = Rpc 'find_overrides' @{ assembly_name='TestIL'; type_full_name='TestIL.IDamageable'; method_name='TakeDamage' }
    $implTypes = @($impl.items | Where-Object { $_.assembly -eq 'TestIL' } | ForEach-Object { $_.type } | Sort-Object -Unique)
    Assert (($implTypes -contains 'TestIL.Crate') -and ($implTypes -contains 'TestIL.Wall')) "interface impl lists Crate (implicit) + Wall (explicit)" "got $($implTypes -join ',')"
    $crateHit = $impl.items | Where-Object { $_.type -eq 'TestIL.Crate' } | Select-Object -First 1
    Assert ($crateHit.is_interface_impl -eq $true -and $crateHit.token -gt 0) "interface-impl hit flagged is_interface_impl + carries token"
    # The implementor's token is addressable like any other.
    $crateSrc = RpcText 'decompile_by_token' @{ token=$crateHit.token; assembly_name='TestIL' }
    Assert ($crateSrc -match 'TakeDamage') "interface-impl token feeds decompile_by_token"

    # ----- step 22: force_return / nop_method (high-level body rewrites) — before open_files, which
    # loads duplicate TestIL copies that would confuse FindAssemblyByName for these patches.
    Write-Host ""
    Write-Host "[22] force_return / nop_method"
    # The classic move: make a bool method return true.
    $fr1 = Rpc 'force_return' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='IsPremium'; value=$true }
    Assert ($fr1.has_pending_patch -eq $true) "force_return sets a pending patch (revertible)"
    $fr1ops = @($fr1.instructions | ForEach-Object { $_.opcode })
    Assert (($fr1ops -contains 'ret') -and ($fr1ops.Count -le 3)) "IsPremium reduced to load+ret" "ops=$($fr1ops -join ',')"
    # Force an int to a constant, and a reference type to default (null).
    Rpc 'force_return' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='GetCoins'; value=999 } | Out-Null
    Rpc 'force_return' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='GetName'; value='default' } | Out-Null
    # nop a void method -> a single ret.
    $nop = Rpc 'nop_method' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='Tick' }
    $nopops = @($nop.instructions | ForEach-Object { $_.opcode })
    Assert (($nopops.Count -eq 1) -and ($nopops[0] -eq 'ret')) "nop_method Tick body is a single ret" "ops=$($nopops -join ',')"
    # Persist to a side path and prove the rewritten behavior on disk.
    $forcedPath = Join-Path $binFixture 'TestIL.forced.dll'
    if (Test-Path $forcedPath) { Remove-Item $forcedPath -Force }
    Rpc 'save_assembly' @{ assembly_name='TestIL'; output_path=$forcedPath } | Out-Null
    Assert ((Test-Path $forcedPath)) "force_return side-path saved"
    $beh = & powershell -NoProfile -Command "[Reflection.Assembly]::LoadFile('$forcedPath') | Out-Null; ('{0}|{1}|{2}' -f [TestIL.Patchable]::IsPremium(), [TestIL.Patchable]::GetCoins(), [string]::IsNullOrEmpty([TestIL.Patchable]::GetName()))"
    Assert ($beh -eq 'True|999|True') "on disk: IsPremium()=True, GetCoins()=999, GetName()=null" "got $beh"
    # Revertible like any other patch.
    $revF = Rpc 'revert_method_il' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='IsPremium' }
    Assert ($revF.reverted -eq $true) "force_return is revertible via revert_method_il"
    # force_return with a value on a void method errors helpfully.
    $voidErr = $null
    try { Rpc 'force_return' @{ assembly_name='TestIL'; type_full_name='TestIL.Patchable'; method_name='Tick'; value=1 } | Out-Null } catch { $voidErr = $_.Exception.Message }
    Assert ($voidErr -and ($voidErr -match 'void')) "force_return value on a void method errors helpfully" "got: $voidErr"

    # ----- step 23: loopback binding — 127.0.0.1 must work, browser GET / must not 404 -----
    Write-Host ""
    Write-Host "[23] server reachable via 127.0.0.1 + browser status page"
    # Before the multi-prefix fix, a localhost-only HttpListener prefix made http.sys reject
    # http://127.0.0.1:<port>/ at the kernel level with HTTP 400 "Invalid Hostname".
    $ipHealth = $null
    try { $ipHealth = Invoke-WebRequest -Uri "http://127.0.0.1:$($script:Port)/health" -UseBasicParsing -TimeoutSec 5 } catch { $ipHealth = $_.Exception }
    Assert ($ipHealth.StatusCode -eq 200) "GET http://127.0.0.1:<port>/health returns 200 (not 400 Invalid Hostname)" "got: $ipHealth"
    # A plain browser GET on the root should get a status page, not a bare 404.
    $rootPage = $null
    try { $rootPage = Invoke-WebRequest -Uri "http://localhost:$($script:Port)/" -UseBasicParsing -TimeoutSec 5 } catch { $rootPage = $_.Exception }
    Assert ($rootPage.StatusCode -eq 200 -and $rootPage.Content -match 'MCP') "browser GET / returns a 200 status page (not 404)" "got: $rootPage"

    # ----- step 24: decompile_type (whole-type decompilation) -----
    Write-Host ""
    Write-Host "[24] decompile_type on TestIL.Simple"
    $dt = RpcText 'decompile_type' @{ assembly_name='TestIL'; type_full_name='TestIL.Simple' }
    Assert ($dt -match 'class Simple') "decompile_type emits the type declaration"
    Assert (($dt -match 'AddOne') -and ($dt -match 'Greet') -and ($dt -match 'Branch')) "decompile_type returns the whole type (multiple members in one call)" "missing members"
    $dtErr = $null
    try { RpcText 'decompile_type' @{ assembly_name='TestIL'; type_full_name='TestIL.DoesNotExist' } | Out-Null } catch { $dtErr = $_.Exception.Message }
    Assert ($dtErr -and ($dtErr -match 'Type not found')) "decompile_type errors on a missing type" "got: $dtErr"

    # ----- step 25: open_files (load assemblies from disk) — runs LAST: it loads extra copies of
    # TestIL, which would add duplicate 'TestIL' entries that earlier single-match lookups don't expect.
    Write-Host ""
    Write-Host "[25] open_files: load assemblies by file path and by directory"
    $openDir = Join-Path $binFixture 'opentest'
    if (Test-Path $openDir) { Remove-Item $openDir -Recurse -Force }
    New-Item -ItemType Directory -Path $openDir | Out-Null
    $openA = Join-Path $openDir 'OpenA.dll'
    Copy-Item $testDll $openA -Force
    # File mode: a path not yet loaded.
    $o1 = Rpc 'open_files' @{ paths=@($openA) }
    Assert ($o1.loaded_count -eq 1 -and $o1.failed_count -eq 0) "open_files loads OpenA.dll (1 new)" "loaded=$($o1.loaded_count) already=$($o1.already_loaded_count) failed=$($o1.failed_count)"
    Assert (@($o1.loaded | Where-Object { $_.name -eq 'TestIL' }).Count -ge 1) "loaded entry carries assembly name (TestIL)"
    $afterOpen = @(Rpc 'list_assemblies' @{ name_filter='TestIL' })
    Assert ((@($afterOpen | Where-Object { $_.Name -eq 'TestIL' }).Count) -ge 2) "newly opened assembly shows up in list_assemblies"
    # Idempotent: re-opening the same path is reported as already loaded, not reloaded.
    $o2 = Rpc 'open_files' @{ paths=@($openA) }
    Assert ($o2.loaded_count -eq 0 -and $o2.already_loaded_count -eq 1) "re-opening the same path reports already_loaded" "loaded=$($o2.loaded_count) already=$($o2.already_loaded_count)"
    # Directory mode: drop a 2nd copy in and open the whole folder.
    Copy-Item $testDll (Join-Path $openDir 'OpenB.dll') -Force
    $o3 = Rpc 'open_files' @{ paths=@($openDir) }
    Assert ($o3.loaded_count -eq 1 -and $o3.already_loaded_count -eq 1) "directory mode loads new OpenB.dll, skips already-open OpenA.dll" "loaded=$($o3.loaded_count) already=$($o3.already_loaded_count)"
    # A missing path is reported in failed[], not thrown as a tool error.
    $o4 = Rpc 'open_files' @{ paths=@('C:\does\not\exist\nope.dll') }
    Assert ($o4.failed_count -eq 1 -and $o4.loaded_count -eq 0) "missing file reported in failed[] (not a hard error)" "failed=$($o4.failed_count)"
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
