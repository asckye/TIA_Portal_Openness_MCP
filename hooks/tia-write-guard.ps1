# TIA write guard — Claude Code PreToolUse hook for the tia-portal MCP server.
#
# Reads the hook payload (JSON) from stdin, classifies the tool with manifest/tools-list.json
# (the ToolTaxonomy operation of every tool) and
#   1. appends an audit line for every non-read call to %LOCALAPPDATA%\TiaMcpServer\audit\tool-calls.jsonl
#      (passwords/secrets are redacted, never logged),
#   2. denies ONLINE-WRITE tools (DownloadToPlc, UploadStationFromPlc, runtime/PLCSIM writes, ...)
#      unless the call is a preview (dryRun not false on a tool that has dryRun) or
#      TIA_MCP_ALLOW_ONLINE_WRITE=1 is set.
# Environment:
#   TIA_MCP_WRITE_GUARD=0             disable the guard entirely (no audit, no deny)
#   TIA_MCP_ALLOW_ONLINE_WRITE=1      allow online writes (still audited)
#   TIA_MCP_GUARD_DENY_OPERATIONS     extra operations to deny, comma separated (e.g. "EXECUTE,WRITE")
#   TIA_MCP_AUDIT_LOG                 audit file path override
#   TIA_MCP_GUARD_DEBUG=1             print internal errors to stderr
# Output: nothing (allow normal permission flow) or a PreToolUse deny decision as JSON on stdout.
# Never blocks reads and never throws: any internal error lets the call through.

$ErrorActionPreference = 'Stop'
try {
    if (($env:TIA_MCP_WRITE_GUARD + '') -match '^(0|off|false)$') { exit 0 }
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json
    $toolName = [string]$payload.tool_name
    if ($toolName -notmatch '^mcp__tia-portal__(.+)$') { exit 0 }
    $name = $Matches[1]
    $toolInput = $payload.tool_input
    if ($null -eq $toolInput) { $toolInput = [pscustomobject]@{} }

    # The CallTool bridge invokes another tool by name; classify the target instead.
    if ($name -eq 'CallTool' -and $toolInput.name) {
        $name = [string]$toolInput.name
        $inner = [pscustomobject]@{}
        if ($toolInput.argumentsJson) { try { $inner = ($toolInput.argumentsJson | ConvertFrom-Json) } catch { } }
        $toolInput = $inner
    }

    $root = $env:CLAUDE_PLUGIN_ROOT
    if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $PSScriptRoot }
    $manifest = Join-Path $root 'manifest\tools-list.json'
    $operation = 'UNKNOWN'; $parameters = @(); $category = ''
    if (Test-Path $manifest) {
        $list = Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry = $list.tools | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($entry) { $operation = [string]$entry.operation; $parameters = @($entry.parameters); $category = [string]$entry.category }
    }

    $hasDryRun = $parameters -contains 'dryRun'
    $dryRunValue = $null
    if ($toolInput.PSObject.Properties['dryRun']) { $dryRunValue = $toolInput.dryRun }
    $isPreview = $hasDryRun -and -not ($dryRunValue -is [bool] -and $dryRunValue -eq $false) -and -not ("$dryRunValue" -match '^(false|0)$')

    # ---- audit (everything that is not a plain read/session call)
    if ($operation -notin @('READ', 'SESSION', 'OFFLINE')) {
        $redacted = [ordered]@{}
        foreach ($p in $toolInput.PSObject.Properties) {
            if ($p.Name -match 'password|secret|token|credential') { $redacted[$p.Name] = '<redacted>' }
            else {
                $v = "$($p.Value)"
                if ($v.Length -gt 200) { $v = $v.Substring(0, 200) + '...' }
                $redacted[$p.Name] = $v
            }
        }
        $logPath = $env:TIA_MCP_AUDIT_LOG
        if ([string]::IsNullOrWhiteSpace($logPath)) { $logPath = Join-Path $env:LOCALAPPDATA 'TiaMcpServer\audit\tool-calls.jsonl' }
        $line = [ordered]@{
            timestamp = (Get-Date).ToString('o'); session = [string]$payload.session_id; cwd = [string]$payload.cwd
            tool = $name; category = $category; operation = $operation; preview = $isPreview; arguments = $redacted
        } | ConvertTo-Json -Compress -Depth 4
        try {
            $dir = Split-Path -Parent $logPath
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
            [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding $false))
        } catch { }
    }

    # ---- deny policy
    $deny = @('ONLINE-WRITE')
    if ($env:TIA_MCP_GUARD_DENY_OPERATIONS) { $deny += ($env:TIA_MCP_GUARD_DENY_OPERATIONS -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ }) }
    $allowed = ($env:TIA_MCP_ALLOW_ONLINE_WRITE + '') -match '^(1|true|yes)$'
    if ($operation -in $deny -and -not $isPreview -and -not $allowed) {
        $reason = "TIA write guard: '$name' is a $operation operation on a real controller/runtime/simulation. " +
                  $(if ($hasDryRun) { "Run it with dryRun=true first; to execute, " } else { "To execute, " }) +
                  "set TIA_MCP_ALLOW_ONLINE_WRITE=1 in the environment of this Claude Code session (or TIA_MCP_WRITE_GUARD=0 to disable the guard). Every write is recorded in the audit log."
        $out = @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } }
        [Console]::Out.Write(($out | ConvertTo-Json -Compress -Depth 4))
        exit 0
    }
    exit 0
} catch {
    if ($env:TIA_MCP_GUARD_DEBUG) { [Console]::Error.WriteLine("tia-write-guard: $($_.Exception.Message)") }
    exit 0
}
