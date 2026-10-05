<#
.SYNOPSIS
  Offline self-test of hooks/tia-write-guard.ps1 (the Claude Code PreToolUse write guard).
  Feeds sample hook payloads through the script with the repository as CLAUDE_PLUGIN_ROOT and
  checks deny/allow decisions and the audit log. No TIA Portal, no MCP server needed.
#>
param([string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$guard = Join-Path $RepoRoot 'hooks\tia-write-guard.ps1'
if (-not (Test-Path $guard)) { throw "guard script missing: $guard" }
$work = Join-Path (Join-Path $RepoRoot 'bin-build/write-guard') ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$audit = Join-Path $work 'audit.jsonl'
$pass = 0; $fail = 0
# Stable 3.3.0 identities select cases; generated currentName/name supplies every call name.
[xml]$profiles = Get-Content (Join-Path $RepoRoot 'src/Logic/ModelContextProtocol/ToolProfiles.resx') -Raw -Encoding UTF8
$runtime = ($profiles.root.data | Where-Object name -eq 'Catalog').value | ConvertFrom-Json
$rows = @($runtime.releases.'21')
function Profile-Row([string]$sourceName) {
    $matches = @($rows | Where-Object sourceName -eq $sourceName)
    if ($matches.Count -ne 1) { throw "Expected one generated mapping for $sourceName" }
    return $matches[0]
}
$watch = (Profile-Row 'SetWatchTableModifyValue').currentName
$write = (Profile-Row 'WritePlcWebVars').currentName
$bridgeName = (Profile-Row 'CallTool').currentName
$previewName = (Profile-Row 'PreflightToolCall').currentName
$readBatchName = (Profile-Row 'ReadToolBatch').currentName
$previewBatchName = (Profile-Row 'PreviewToolBatch').currentName
$applyBatchName = (Profile-Row 'ApplyToolBatch').currentName
$sessionName = (Profile-Row 'GetState').currentName
$executeName = (Profile-Row 'CompileSoftware').currentName
$baseline = Get-Content (Join-Path $RepoRoot 'scripts/checks/write-guard-operations-v3.3.0.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$onlineRows = @($rows | Where-Object { $baseline.operations.($_.sourceName) -eq 'ONLINE-WRITE' })
if (-not $onlineRows.Count) { throw 'No generated online-write mappings found' }
$list = Get-Content (Join-Path $RepoRoot 'manifest/tools-list.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$entries = @{}
foreach ($entry in $list.tools) {
    if ($entries.ContainsKey($entry.name)) { throw "Duplicate manifest entry: $($entry.name)" }
    $entries[$entry.name] = $entry
}
foreach ($row in $rows) {
    if (-not $entries.ContainsKey($row.currentName)) { throw "Stale manifest: missing $($row.currentName)" }
}
function Invoke-Guard([string]$json, [hashtable]$envOverrides) {
    $file = Join-Path $work ('in-' + [Guid]::NewGuid().ToString('N') + '.json')
    [System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false))
    $saved = @{}
    $vars = @{ CLAUDE_PLUGIN_ROOT = $RepoRoot; TIA_MCP_AUDIT_LOG = $audit; TIA_MCP_GUARD_DEBUG = ''; TIA_MCP_ALLOW_ONLINE_WRITE = ''; TIA_MCP_WRITE_GUARD = ''; TIA_MCP_GUARD_DENY_OPERATIONS = '' }
    foreach ($k in $envOverrides.Keys) { $vars[$k] = $envOverrides[$k] }
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    try {
        $out = & cmd.exe /c "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$guard`" < `"$file`" 2>nul" | Out-String
    } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    return $out.Trim()
}
function Check([bool]$ok, [string]$what) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host "  FAIL: $what" } }

function Invoke-Call([string]$name, $arguments, [hashtable]$overrides = @{}) {
    return Invoke-Guard (@{ session_id = 'guard-test'; cwd = 'C:/w'; tool_name = 'mcp__tia-portal__' + $name; tool_input = $arguments } | ConvertTo-Json -Compress -Depth 30) $overrides
}
function Expect-Deny([string]$result, [string]$target, [string]$what) {
    Check ($result -match '"permissionDecision":"deny"' -and $result.Contains($target)) ($what + ': ' + $result)
}
function Expect-Allow([string]$result, [string]$what) { Check ([string]::IsNullOrEmpty($result)) ($what + ': ' + $result) }

# Check every released online write against the real manifest, including pending renames.
foreach ($row in $onlineRows) {
    $target = $row.currentName
    Check ($entries[$target].operation -eq 'ONLINE-WRITE') "$target retains its online-write category"
    Expect-Deny (Invoke-Call $target @{ dryRun = $false }) $target 'online roster write denied'
    Expect-Allow (Invoke-Call $target @{ dryRun = $false } @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'online roster allow switch'
}

# The watch-table tool has no dryRun parameter; a supplied flag cannot make it safe.
Expect-Deny (Invoke-Call $watch @{}) $watch 'V4 watch-table rename denied'
Expect-Deny (Invoke-Call $watch @{ dryRun = $true }) $watch 'unsupported preview flag cannot bypass guard'
Expect-Allow (Invoke-Call $watch @{} @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'V4 watch-table name is known'
Expect-Deny (Invoke-Call $bridgeName @{ name = $watch; arguments = @{ dryRun = $true } }) $watch 'bridge cannot invent a preview parameter'
$apply = @{ dryRun = $false; password = 'hidden-password-123'; nested = @{ credentials = 'hidden-nested-456'; items = @(@{ token = 'hidden-token-789' }) } }
Expect-Deny (Invoke-Call $write $apply) $write 'V4 online write denied'
Expect-Allow (Invoke-Call $write $apply @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'allow switch'
$beforeDisabled = @(Get-Content $audit).Count
Expect-Allow (Invoke-Call $write $apply @{ TIA_MCP_WRITE_GUARD = '0' }) 'guard disabled'
Check (@(Get-Content $audit).Count -eq $beforeDisabled) 'disabled guard does not audit'
Expect-Allow (Invoke-Call $write @{ dryRun = $true }) 'direct explicit preview'
Expect-Allow (Invoke-Call $write @{}) 'direct default preview'
foreach ($value in @($false, 'false', 0)) {
    Expect-Deny (Invoke-Call $write @{ dryRun = $value }) $write 'false preview representations denied'
}
foreach ($arguments in @(@{ dryRun = $false }, @{ dryRun = $true }, @{})) {
    $result = Invoke-Call $bridgeName @{ name = $write; arguments = $arguments }
    if ($arguments.ContainsKey('dryRun') -and $arguments.dryRun -eq $false) { Expect-Deny $result $write 'CallTool arguments dryRun=false' }
    else { Expect-Allow $result 'CallTool arguments dryRun=true/absent' }
}
Expect-Deny (Invoke-Call $bridgeName @{ name = $write; arguments = @{ dryRun = $false }; argumentsJson = '{"dryRun":true}' }) $write 'obsolete argumentsJson cannot override V4 arguments'
Expect-Deny (Invoke-Call $bridgeName @{ name = $write; arguments = '{"dryRun":true}' }) $write 'double-encoded arguments refused'
Expect-Allow (Invoke-Call $bridgeName @{ name = $write; arguments = $apply } @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'bridge allow switch'
Expect-Allow (Invoke-Call $previewName @{ name = $write; arguments = $apply }) 'bridge preview'
$operations = @(@{ name = $sessionName; arguments = @{} }, @{ name = $write; arguments = $apply })
Expect-Allow (Invoke-Call $readBatchName @{ operations = @(@{ name = $sessionName; arguments = @{} }) }) 'read-only batch passes'
Expect-Deny (Invoke-Call $readBatchName @{ operations = $operations; dryRun = $true }) $write 'outer dryRun cannot hide batch write'
Expect-Allow (Invoke-Call $readBatchName @{ operations = $operations } @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'batch allow switch'
Expect-Allow (Invoke-Call $previewBatchName @{ operations = $operations }) 'batch preview'
Expect-Deny (Invoke-Call $applyBatchName @{ operations = @(@{ name = $write; arguments = @{ dryRun = $true } }) }) $write 'apply forces execution'
Expect-Deny (Invoke-Call $applyBatchName @{ token = 'hidden-plan-012' }) 'opaque stored plan' 'token-only batch fails closed'
Expect-Allow (Invoke-Call $applyBatchName @{ token = 'hidden-plan-012' } @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'token batch allow switch'
Expect-Deny (Invoke-Call $bridgeName @{ name = $applyBatchName; arguments = @{ token = 'hidden-plan-012' } }) 'opaque stored plan' 'nested token batch'
Expect-Deny (Invoke-Call $bridgeName @{ name = $readBatchName; arguments = @{ operations = $operations } }) $write 'nested bridge/batch'

$unknown = 'UnknownV4Tool'
foreach ($overrides in @(@{}, @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' })) {
    $r = Invoke-Call $unknown @{} $overrides
    Expect-Deny $r $unknown 'unknown tool denied even with online allow'
    Check ($r -match 'tool list is out of date') 'unknown reason explains stale manifest'
}
Expect-Allow (Invoke-Call $unknown @{} @{ TIA_MCP_WRITE_GUARD = '0' }) 'disabled guard allows unknown'
foreach ($bridge in @($bridgeName, $previewName)) {
    Expect-Deny (Invoke-Call $bridge @{ name = $unknown; arguments = @{} }) $unknown 'unknown bridge target'
}
foreach ($batch in @($readBatchName, $previewBatchName, $applyBatchName)) {
    Expect-Deny (Invoke-Call $batch @{ operations = @(@{ name = $unknown; arguments = @{} }) } @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) $unknown 'unknown batch target'
}
Expect-Allow (Invoke-Call $sessionName @{}) 'session read'
Expect-Deny (Invoke-Call $executeName @{} @{ TIA_MCP_GUARD_DENY_OPERATIONS = 'EXECUTE' }) $executeName 'extra denied category'
Expect-Allow (Invoke-Guard '{"tool_name":"mcp__other__Thing","tool_input":{}}' @{}) 'non-TIA tool ignored'
Expect-Allow (Invoke-Guard 'not json' @{}) 'malformed payload without identifiable tool ignored'

# Exercise the final V4 names without editing tool sources owned by parallel tasks.
$fixture = Join-Path $work 'plugin'
New-Item -ItemType Directory -Force (Join-Path $fixture 'manifest') | Out-Null
$fixtureManifest = Join-Path $fixture 'manifest/tools-list.json'
foreach ($row in $rows) { $entries[$row.currentName].name = $row.name }
[IO.File]::WriteAllText($fixtureManifest, ($list | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding $false))
foreach ($row in $onlineRows) {
    $target = $row.name
    Expect-Deny (Invoke-Call $target @{ dryRun = $false } @{ CLAUDE_PLUGIN_ROOT = $fixture }) $target 'final V4 online write denied'
    Expect-Allow (Invoke-Call $target @{ dryRun = $false } @{ CLAUDE_PLUGIN_ROOT = $fixture; TIA_MCP_ALLOW_ONLINE_WRITE = '1' }) 'final V4 online write allow switch'
}
[IO.File]::WriteAllText($fixtureManifest, 'not json')
Expect-Deny (Invoke-Call $write $apply @{ CLAUDE_PLUGIN_ROOT = $fixture }) $write 'corrupt manifest fails closed'
Expect-Deny (Invoke-Call $write $apply @{ CLAUDE_PLUGIN_ROOT = (Join-Path $work 'missing') }) $write 'missing manifest fails closed'
Expect-Deny (Invoke-Call $write $apply @{ TIA_MCP_AUDIT_LOG = $work }) $write 'audit failure cannot bypass denial'

$lines = @(Get-Content $audit -Encoding UTF8)
Check ($lines.Count -ge 20) "audit log covers non-read calls ($($lines.Count))"
Check (($lines -join "`n") -notmatch 'hidden-(password|nested|token|plan)-') 'nested credentials and plan token redacted'
$records = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
Check (@($records | Where-Object tool -eq $sessionName).Count -eq 0) 'read calls are not audited'
Check (@($records | Where-Object { $_.tool -eq $write -and $_.operation -eq 'ONLINE-WRITE' -and -not $_.preview }).Count -ge 1) 'actual writes audited as ONLINE-WRITE'
Check (@($records | Where-Object { $_.tool -eq $write -and $_.preview }).Count -ge 1) 'previews audited'
Check (@($records | Where-Object { $_.tool -eq $unknown -and $_.operation -eq 'UNKNOWN' }).Count -ge 1) 'unknown targets audited'
$first = [System.IO.File]::ReadAllBytes($audit)
Check (-not ($first.Length -ge 3 -and $first[0] -eq 0xEF -and $first[1] -eq 0xBB)) 'audit log is UTF-8 without BOM'
# Verify the resolved generated target stays within the test directory before cleanup.
$resolvedWork = [IO.Path]::GetFullPath($work)
$testRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'bin-build/write-guard')) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedWork.StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
Remove-Item -LiteralPath $resolvedWork -Recurse -Force
Write-Host "Test-WriteGuard: $pass passed, $fail failed; $($onlineRows.Count) online-write mappings checked under current and final V4 names."
if ($fail -gt 0) { exit 1 }
