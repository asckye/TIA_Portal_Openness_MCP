#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(1,48)][int]$Hours=2,
    [ValidateRange(1,500)][int]$MaxEvents=100,
    [string]$JournalDirectory=(Join-Path $env:LOCALAPPDATA 'TiaMcp/diagnostics'),
    [switch]$PlanOnly
)
$ErrorActionPreference='Stop'
if (![IO.Path]::IsPathRooted($OutputDirectory)) { throw 'OutputDirectory must be absolute' }
$destination=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'OutputDirectory must be new; existing evidence is never overwritten' }
$since=[DateTime]::UtcNow.AddHours(-$Hours)
$plan=[ordered]@{ output=$destination; sinceUtc=$since.ToString('o'); maxEvents=$MaxEvents; journalDirectory=$JournalDirectory; dumpContentsCopied=$false; attachesToTia=$false; changesDumpPolicy=$false }
if ($PlanOnly) { $plan | ConvertTo-Json; return }
New-Item -ItemType Directory -Path $destination | Out-Null
$errors=New-Object 'System.Collections.Generic.List[string]'
$events=@()
foreach ($query in @(
    @{LogName='Application'; Id=@(1000,1001,1026); StartTime=$since.ToLocalTime()},
    @{LogName='Application'; ProviderName='Microsoft-Windows-ProcessExitMonitor'; Id=3000; StartTime=$since.ToLocalTime()},
    @{LogName='System'; ProviderName='Microsoft-Windows-Resource-Exhaustion-Detector'; Id=2004; StartTime=$since.ToLocalTime()}
)) {
    try {
        $events+=@(Get-WinEvent -FilterHashtable $query -MaxEvents $MaxEvents -ErrorAction Stop | ForEach-Object {
            [ordered]@{utc=$_.TimeCreated.ToUniversalTime().ToString('o'); log=$_.LogName; provider=$_.ProviderName; eventId=$_.Id; recordId=$_.RecordId; message=$_.Message}
        })
    } catch { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { $errors.Add('Event query: '+$_.Exception.Message) } }
}
$processes=@()
try { $processes=@(Get-Process -Name 'Siemens.Automation.Portal','TiaMcp.Engine.V20','TiaMcp.Engine.V21','TiaMcp.FoundationHost' -ErrorAction SilentlyContinue | ForEach-Object {
    try { [ordered]@{pid=$_.Id; name=$_.ProcessName; startUtc=$_.StartTime.ToUniversalTime().ToString('o')} } catch { $errors.Add('Process metadata: '+$_.Exception.Message) }
}) } catch { $errors.Add('Processes: '+$_.Exception.Message) }
function Get-JournalHash([string]$Path) {
    $hash=[Security.Cryptography.SHA256]::Create()
    $stream=[IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}
$journals=@()
try {
    if (Test-Path -LiteralPath $JournalDirectory -PathType Container) {
        $files=@(Get-ChildItem -LiteralPath $JournalDirectory -File -Filter 'calls-*.jsonl*' | Where-Object {$_.LastWriteTimeUtc -ge $since} | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 20)
        foreach ($file in $files) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -gt 12MB) { $errors.Add('Skipped oversized/reparse journal: '+$file.Name); continue }
            $target=Join-Path $destination $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $journals+=@{name=$file.Name; bytes=(Get-Item -LiteralPath $target).Length; sha256=(Get-JournalHash $target)}
        }
    } else { $errors.Add('Journal directory absent; no invocation evidence was collected') }
} catch { $errors.Add('Journal capture: '+$_.Exception.Message) }
$dumps=@()
$dumpDirectory=Join-Path $env:LOCALAPPDATA 'CrashDumps'
try {
    if (Test-Path -LiteralPath $dumpDirectory -PathType Container) {
        $dumps=@(Get-ChildItem -LiteralPath $dumpDirectory -File -Filter '*.dmp' | Where-Object {$_.LastWriteTimeUtc -ge $since -and $_.Name -match '^(Siemens\.Automation\.Portal|TiaMcp\.Engine\.V(?:20|21)|TiaMcp\.FoundationHost)\.'} | Select-Object -First 50 | ForEach-Object {
            @{path=$_.FullName; bytes=$_.Length; modifiedUtc=$_.LastWriteTimeUtc.ToString('o')}
        })
    }
} catch { $errors.Add('Dump inventory: '+$_.Exception.Message) }
$report=[ordered]@{schemaVersion=1; capturedUtc=[DateTime]::UtcNow.ToString('o'); scope=$plan; events=$events; processes=$processes; journals=$journals; dumpInventory=$dumps; errors=@($errors.ToArray()); complete=($errors.Count -eq 0); note='Local evidence only; missing events or dump files do not prove absence of a crash. Events/paths may contain private information. No dump content or project was copied.'}
[IO.File]::WriteAllText((Join-Path $destination 'evidence.json'),($report | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Output (Join-Path $destination 'evidence.json')
