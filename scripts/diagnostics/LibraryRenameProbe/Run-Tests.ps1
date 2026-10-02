[CmdletBinding()]
param(
    [string]$SourceLibrary = 'C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\library-general-1133\MCP_GeneralScripts_Rename_20261001_1133\MCP_GeneralScripts_Rename_20261001_1133.al21',
    [ValidateRange(60,1800)][int]$TimeoutSeconds = 600,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'LibraryRenameProbe.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Extract the complete compiled package before running.' }
if ($SelfTest) { & $executable --self-test; exit $LASTEXITCODE }
& $executable --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed.' }
& $executable --preflight
if ($LASTEXITCODE -ne 0) { throw 'V21 installation preflight failed. Run inside the TIA VM.' }
if (!(Test-Path -LiteralPath $SourceLibrary -PathType Leaf)) { throw "Diagnostic source library is missing: $SourceLibrary" }
if ($SourceLibrary.Contains('"')) { throw 'Quote characters in paths are refused.' }
$SourceLibrary = [IO.Path]::GetFullPath($SourceLibrary)
$campaign = Join-Path $PSScriptRoot ('results\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$null = New-Item -ItemType Directory -Path $campaign
$records = [Collections.Generic.List[object]]::new()
$foreign = @(Get-Process -Name 'Siemens.Automation.Portal' -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ pid=$_.Id; startUtc=$_.StartTime.ToUniversalTime().ToString('o') }
})
$foreign | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $campaign 'existing-portals.json') -Encoding UTF8
$campaignStart = Get-Date
$stopReason = $null

function Assert-ExistingPortals {
    foreach ($entry in $foreign) {
        $current = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
        if (!$current -or $current.StartTime.ToUniversalTime().ToString('o') -ne $entry.startUtc) {
            throw "An existing TIA process changed or exited (PID $($entry.pid)); campaign stopped. No process was killed by this runner."
        }
    }
}

function Invoke-ProbeStage([string]$Stage, [string]$Case, [string]$OutputDirectory) {
    Assert-ExistingPortals
    if ($OutputDirectory.Contains('"')) { throw 'Quote characters in paths are refused.' }
    Write-Host "[$(Get-Date -Format HH:mm:ss)] $Case / $Stage"
    $stdout = Join-Path $campaign "$Case-$Stage.stdout.txt"
    $stderr = Join-Path $campaign "$Case-$Stage.stderr.txt"
    $arguments = @('--stage', $Stage, '--case', $Case, '--output', ('"' + $OutputDirectory + '"'), '--source', ('"' + $SourceLibrary + '"'))
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Windows PowerShell 5.1 can lose ExitCode for short-lived Start-Process children
    # unless a process handle is retained before waiting.
    $null = $process.Handle
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $nextProgress = (Get-Date).AddSeconds(30)
    $timedOut = $false
    while (!$process.HasExited) {
        if ((Get-Date) -ge $deadline) {
            # Only terminate the exact child started by this invocation. Never kill TIA by name or infer ownership from recency.
            $process.Kill()
            $process.WaitForExit()
            $timedOut = $true
            break
        }
        if ((Get-Date) -ge $nextProgress) {
            Write-Host '  Still running. If TIA shows the Openness firewall prompt, verify LibraryRenameProbe.exe and allow this test application.'
            $nextProgress = (Get-Date).AddSeconds(30)
        }
        Start-Sleep -Milliseconds 500
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $resultPath = Join-Path $OutputDirectory "$Stage-result.json"
    $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $record = [pscustomobject]@{ case=$Case; stage=$Stage; exitCode=$exitCode; timedOut=$timedOut; result=$result;
        ownedProcessExitedVerified=$false; existingPortalsPreserved=$false }
    $records.Add($record)
    $records | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $campaign 'cases.json') -Encoding UTF8
    if ($exitCode -ne 0 -or $null -eq $result -or $result.status -eq 'FAILED') {
        if ($result -and $result.error) {
            Write-Host ('Failure details:' + [Environment]::NewLine + [string]$result.error)
        } elseif (Test-Path -LiteralPath $stderr) {
            $errorOutput = Get-Content -LiteralPath $stderr -Raw -Encoding UTF8
            if ($errorOutput) { Write-Host ('Probe stderr:' + [Environment]::NewLine + $errorOutput) }
        }
        Write-Host "Result file: $resultPath"
        $eventsPath = Join-Path $OutputDirectory "$Stage-events.jsonl"
        if (Test-Path -LiteralPath $eventsPath) { Write-Host "Native call log: $eventsPath" }
    }
    if ($timedOut) { throw 'Probe timed out. Owned TIA state is uncertain; no additional cases will start. Retain logs and inspect the recorded PID.' }
    if ($null -eq $result) { throw 'Probe exited without a result. No additional cases will start.' }
    # Check process identity from the durable ownership record, not a PID guessed from the process list.
    $identityPath = Join-Path $OutputDirectory "$Stage-owned-process.json"
    if (!(Test-Path -LiteralPath $identityPath)) { throw 'Owned TIA process identity was not recorded; campaign stopped.' }
    $identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $grace = (Get-Date).AddSeconds(20)
    do {
        $owned = Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
        $stillOwned = $owned -and $owned.StartTime.ToUniversalTime().ToString('o') -eq $identity.startUtc
        if (!$stillOwned) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $grace)
    if ($stillOwned) { throw "Owned TIA PID $($identity.pid) is still alive. Campaign stopped; no automatic TIA termination." }
    Assert-ExistingPortals
    $record.ownedProcessExitedVerified=$true
    $record.existingPortalsPreserved=$true
    $records | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $campaign 'cases.json') -Encoding UTF8
    Write-Host "  $($result.status)"
    return $record
}

try {
    $cases = @('control','property','attributes','edit-property','edit-attributes',
        'document-create-control','document-update-control','document-create-rename','document-update-rename')
    Write-Host "Nine isolated cases. Results: $campaign"
    Write-Host 'The diagnostic library is opened read-only. Existing projects are never attached or saved.'
    Write-Host 'Native projects use short per-case paths under %LOCALAPPDATA%\TRP; logs and exported scripts remain in this results folder.'
    foreach ($case in $cases) {
        $caseDirectory = Join-Path $campaign $case
        $prepared = Invoke-ProbeStage 'prepare' $case $caseDirectory
        if ($prepared.exitCode -ne 0 -or $prepared.result.status -ne 'PREPARED') { throw "Preparation failed for $case; remaining cases were not run." }
        $tested = Invoke-ProbeStage 'run' $case $caseDirectory
        if ($case -eq 'control' -and ($tested.exitCode -ne 0 -or $tested.result.status -ne 'CONTROL_PASSED')) {
            throw 'Read/save/reopen control failed; rename cases were not run.'
        }
    }
}
catch { $stopReason = $_.Exception.Message; Write-Warning $stopReason }
finally {
    try {
        Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$campaignStart; Id=1000,1001,1026 } -ErrorAction Stop |
            Where-Object { $_.Message -match 'Siemens|LibraryRenameProbe' } |
            Select-Object TimeCreated, Id, ProviderName, Message |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $campaign 'application-events.json') -Encoding UTF8
    } catch { $_.Exception.Message | Set-Content -LiteralPath (Join-Path $campaign 'event-collection-note.txt') -Encoding UTF8 }
    [pscustomobject]@{ schemaVersion=2; sourceLibrary=$SourceLibrary; completedStages=$records.Count;
        attemptedStages=$records.Count; executionStagesAttempted=@($records | Where-Object { $_.stage -eq 'run' }).Count; stopReason=$stopReason;
        note='Campaign completion is not proof that rename passed. Read each case status; CLONE_RENAMED_PERSISTED has a different type GUID.';
        finishedUtc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $campaign 'summary.json') -Encoding UTF8
    Write-Host "Evidence retained at $campaign"
}
if ($stopReason) { exit 2 }
if (@($records | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) { exit 1 }
exit 0
