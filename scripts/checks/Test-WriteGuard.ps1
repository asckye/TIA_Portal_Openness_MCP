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
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('tia-write-guard-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$audit = Join-Path $work 'audit.jsonl'
$pass = 0; $fail = 0
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

$download = '{"session_id":"t","cwd":"C:/w","tool_name":"mcp__tia-portal__DownloadToPlc","tool_input":{"softwarePath":"PLC_1","password":"secret"}}'
$r = Invoke-Guard $download @{}
Check ($r -match '"permissionDecision":"deny"' -and $r -match 'DownloadToPlc') "real download denied by default: $r"
Check ($r -notmatch 'secret') 'password never appears in the decision'
$r = Invoke-Guard $download @{ TIA_MCP_ALLOW_ONLINE_WRITE = '1' }
Check ([string]::IsNullOrEmpty($r)) "download allowed with TIA_MCP_ALLOW_ONLINE_WRITE=1: $r"
$r = Invoke-Guard $download @{ TIA_MCP_WRITE_GUARD = '0' }
Check ([string]::IsNullOrEmpty($r)) 'guard disabled -> no decision'
$r = Invoke-Guard '{"tool_name":"mcp__tia-portal__UploadStationFromPlc","tool_input":{"targetIpAddress":"192.168.0.1"}}' @{}
Check ([string]::IsNullOrEmpty($r)) "preview (dryRun default) of an online write passes: $r"
$r = Invoke-Guard '{"tool_name":"mcp__tia-portal__UploadStationFromPlc","tool_input":{"targetIpAddress":"192.168.0.1","dryRun":false,"confirmUpload":true}}' @{}
Check ($r -match '"permissionDecision":"deny"' -and $r -match 'dryRun=true first') "explicit dryRun=false upload denied: $r"
$r = Invoke-Guard '{"tool_name":"mcp__tia-portal__CallTool","tool_input":{"name":"WritePlcWebVars","argumentsJson":"{\"host\":\"h\",\"dryRun\":false,\"confirmWrite\":true}"}}' @{}
Check ($r -match '"permissionDecision":"deny"' -and $r -match 'WritePlcWebVars') "CallTool bridge target classified: $r"
$r = Invoke-Guard '{"tool_name":"mcp__tia-portal__GetState","tool_input":{}}' @{}
Check ([string]::IsNullOrEmpty($r)) 'read/session tool passes'
$r = Invoke-Guard '{"tool_name":"mcp__tia-portal__CompileSoftware","tool_input":{"softwarePath":"PLC_1"}}' @{ TIA_MCP_GUARD_DENY_OPERATIONS = 'EXECUTE' }
Check ($r -match '"permissionDecision":"deny"') "extra deny operation honoured: $r"
$r = Invoke-Guard '{"tool_name":"mcp__other__Thing","tool_input":{}}' @{}
Check ([string]::IsNullOrEmpty($r)) 'non tia-portal tools ignored'
$r = Invoke-Guard 'not json' @{}
Check ([string]::IsNullOrEmpty($r)) '[sentinel] malformed payload never blocks'

$lines = @(Get-Content $audit -Encoding UTF8)
Check ($lines.Count -ge 5) "audit log has one line per non-read call ($($lines.Count))"
Check (($lines -join "`n") -notmatch 'secret') 'audit log never contains the password'
Check (($lines | Where-Object { $_ -match '"tool":"GetState"' }).Count -eq 0) 'read calls are not audited'
Check (($lines | Where-Object { $_ -match '"tool":"DownloadToPlc"' -and $_ -match '"operation":"ONLINE-WRITE"' }).Count -ge 1) 'download audited as ONLINE-WRITE'
$first = [System.IO.File]::ReadAllBytes($audit)
Check (-not ($first.Length -ge 3 -and $first[0] -eq 0xEF -and $first[1] -eq 0xBB)) 'audit log is UTF-8 without BOM'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "Test-WriteGuard: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
