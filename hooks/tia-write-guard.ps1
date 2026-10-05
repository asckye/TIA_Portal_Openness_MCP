# TIA write guard: Claude Code PreToolUse hook for the tia-portal MCP server.
# Classifies direct calls and V4 bridge/batch targets with manifest/tools-list.json.
# Unknown names fail closed; previews are allowed but still classified and audited.
# Environment:
#   TIA_MCP_WRITE_GUARD=0             disable the guard entirely (no audit, no deny)
#   TIA_MCP_ALLOW_ONLINE_WRITE=1      allow known online writes (still audited)
#   TIA_MCP_GUARD_DENY_OPERATIONS     extra operations to deny, comma separated
#   TIA_MCP_AUDIT_LOG                 audit file path override
#   TIA_MCP_GUARD_DEBUG=1             print internal errors to stderr

$ErrorActionPreference = 'Stop'
$name = ''
function Deny([string]$reason) {
    $out = @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } }
    [Console]::Out.Write(($out | ConvertTo-Json -Compress -Depth 4))
}
function Redact($value, [int]$depth = 0) {
    if ($null -eq $value) { return $null }
    if ($depth -ge 20) { return '<truncated>' }
    if ($value -is [pscustomobject]) {
        $result = [ordered]@{}
        foreach ($p in $value.PSObject.Properties) {
            if ($p.Name -match 'password|secret|token|credential') { $result[$p.Name] = '<redacted>' }
            else { $result[$p.Name] = Redact $p.Value ($depth + 1) }
        }
        return $result
    }
    if ($value -is [array]) { return ,@(foreach ($item in $value) { Redact $item ($depth + 1) }) }
    $text = [string]$value
    if ($text.Length -gt 200) { $text = $text.Substring(0, 200) + '...' }
    return $text
}
function Audit([string]$target, $arguments, [string]$operation, [string]$category, [bool]$preview) {
    if ($operation -in @('READ', 'SESSION', 'OFFLINE')) { return }
    # Audit failures must not bypass the deny policy.
    try {
        $logPath = $env:TIA_MCP_AUDIT_LOG
        if ([string]::IsNullOrWhiteSpace($logPath)) { $logPath = Join-Path $env:LOCALAPPDATA 'TiaMcpServer/audit/tool-calls.jsonl' }
        $line = [ordered]@{
            timestamp = (Get-Date).ToString('o'); session = [string]$payload.session_id; cwd = [string]$payload.cwd
            tool = $target; category = $category; operation = $operation; preview = $preview; arguments = (Redact $arguments)
        } | ConvertTo-Json -Compress -Depth 30
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
        [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
    } catch { }
}
function Inspect-Call([string]$target, $arguments, [bool]$preview = $false, [bool]$apply = $false, [int]$depth = 0) {
    $entry = $entries[$target]
    if (-not $entry -or $entry.operation -notin @('READ', 'SESSION', 'OFFLINE', 'WRITE', 'FILE', 'EXECUTE', 'ONLINE', 'ONLINE-WRITE')) {
        Audit $target $arguments 'UNKNOWN' '' $preview
        return "TIA write guard: '$target' is unknown; the tool list is out of date. Regenerate manifest/tools-list.json from the current engines (or set TIA_MCP_WRITE_GUARD=0 to disable the guard)."
    }
    if ($depth -ge 20) { return "TIA write guard: '$target' exceeds the dispatch nesting limit." }
    if ($null -eq $arguments) { $arguments = [pscustomobject]@{} }
    if ($arguments -isnot [pscustomobject]) { return "TIA write guard: '$target' arguments must be a V4 object." }
    $hasDryRun = @($entry.parameters) -contains 'dryRun'
    $dryRunValue = $arguments.dryRun
    $isPreview = $preview -or (-not $apply -and $hasDryRun -and
        -not ($dryRunValue -is [bool] -and $dryRunValue -eq $false) -and -not ("$dryRunValue" -match '^(false|0)$'))
    Audit $target $arguments $entry.operation $entry.category $isPreview

    # Inspect every child even when an earlier child was denied, so the audit covers the batch.
    $reasons = @()
    if ($target -in @('CallTool', 'PreviewToolCall')) {
        $reasons += Inspect-Call ([string]$arguments.name) $arguments.arguments ($preview -or $target -eq 'PreviewToolCall') $apply ($depth + 1)
    } elseif ($target -in @('RunReadOnlyToolBatch', 'PreviewToolBatch', 'ApplyToolBatch')) {
        foreach ($call in @($arguments.operations)) {
            if ($null -ne $call) {
                $reasons += Inspect-Call ([string]$call.name) $call.arguments ($preview -or $target -eq 'PreviewToolBatch') ($apply -or $target -eq 'ApplyToolBatch') ($depth + 1)
            }
        }
        # ApplyToolBatch accepts only a token. Its server-side plan is invisible to this hook;
        # caller-supplied operations cannot establish what the stored plan will actually execute.
        if ($target -eq 'ApplyToolBatch' -and -not $preview -and -not $allowed) {
            $reasons += "TIA write guard: 'ApplyToolBatch' has an opaque stored plan; its targets cannot be classified. Set TIA_MCP_ALLOW_ONLINE_WRITE=1 to execute the batch."
        }
    }
    if ($entry.operation -in $deny -and -not $isPreview -and -not $allowed) {
        $reasons += "TIA write guard: '$target' is a $($entry.operation) operation on a real controller/runtime/simulation. " +
            $(if ($hasDryRun) { 'Run it with dryRun=true first; to execute, ' } else { 'To execute, ' }) +
            'set TIA_MCP_ALLOW_ONLINE_WRITE=1 in the environment of this Claude Code session (or TIA_MCP_WRITE_GUARD=0 to disable the guard). Every write is recorded in the audit log.'
    }
    return $reasons
}
try {
    if (($env:TIA_MCP_WRITE_GUARD + '') -match '^(0|off|false)$') { exit 0 }
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json
    if ([string]$payload.tool_name -notmatch '^mcp__tia-portal__(.+)$') { exit 0 }
    $name = $Matches[1]
    $root = $env:CLAUDE_PLUGIN_ROOT
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $PSScriptRoot }
    $manifest = Join-Path $root 'manifest/tools-list.json'
    $entries = @{}
    if (Test-Path -LiteralPath $manifest) {
        $list = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($entry in $list.tools) {
            if ($entries.ContainsKey($entry.name)) { throw 'Duplicate tool list entry' }
            $entries[$entry.name] = $entry
        }
    }
    $deny = @('ONLINE-WRITE')
    if ($env:TIA_MCP_GUARD_DENY_OPERATIONS) { $deny += ($env:TIA_MCP_GUARD_DENY_OPERATIONS -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ }) }
    $allowed = ($env:TIA_MCP_ALLOW_ONLINE_WRITE + '') -match '^(1|true|yes)$'
    $reasons = @(Inspect-Call $name $payload.tool_input)
    if ($reasons.Count) { Deny ($reasons -join ' ') }
} catch {
    if ($env:TIA_MCP_GUARD_DEBUG) { [Console]::Error.WriteLine("tia-write-guard: $($_.Exception.Message)") }
    if ($name) {
        Audit $name $payload.tool_input 'UNKNOWN' '' $false
        Deny "TIA write guard: '$name' cannot be classified; the tool list is out of date or unreadable. Regenerate manifest/tools-list.json (or set TIA_MCP_WRITE_GUARD=0 to disable the guard)."
    }
}
exit 0
