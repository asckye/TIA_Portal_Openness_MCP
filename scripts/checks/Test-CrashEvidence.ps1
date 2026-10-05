# Offline collector checks: fake Windows events/processes and a private dump directory.
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch=Join-Path $repo ('bin-build/crash-evidence-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$oldLocalAppData=$env:LOCALAPPDATA
$evidenceFixture=@{queries=0}
function Get-WinEvent { param($FilterHashtable,$MaxEvents,$ErrorAction)
    $evidenceFixture.queries++
    [pscustomobject]@{TimeCreated=[DateTime]::Now;LogName='Application';ProviderName='OfflineFixture';Id=1000;RecordId=7;Message='Synthetic local evidence'}
}
function Get-Process { param($Name,$ErrorAction) @() }
function Check($Condition,$Message) { if (!$Condition) {throw $Message} }
try {
    $env:LOCALAPPDATA=$scratch
    $collector=Join-Path $repo 'scripts/diagnostics/Collect-TiaCrashEvidence.ps1'
    $journals=Join-Path $scratch 'journals'
    New-Item -ItemType Directory -Path $journals | Out-Null
    [IO.File]::WriteAllText((Join-Path $journals 'calls-offline.jsonl'),'{"id":"fixture","phase":"BEFORE"}')
    $dumps=Join-Path $scratch 'CrashDumps'
    New-Item -ItemType Directory -Path $dumps | Out-Null
    $dumpNames=@('TiaMcp.Engine.V20.exe.1.dmp','TiaMcp.Engine.V21.exe.1.dmp','TiaMcp.FoundationHost.exe.1.dmp')
    foreach($name in $dumpNames){[IO.File]::WriteAllText((Join-Path $dumps $name),'not a real dump')}
    [IO.File]::WriteAllText((Join-Path $dumps 'unrelated.exe.1.dmp'),'not a product dump')
    $output=Join-Path $scratch 'captured'
    $null=& $collector -OutputDirectory $output -JournalDirectory $journals -PlanOnly
    Check (!(Test-Path -LiteralPath $output) -and $evidenceFixture.queries -eq 0) 'PlanOnly read events or wrote output'
    $null=& $collector -OutputDirectory $output -JournalDirectory $journals
    $evidence=Get-Content -LiteralPath (Join-Path $output 'evidence.json') -Raw | ConvertFrom-Json
    Check ($evidence.complete -and $evidenceFixture.queries -eq 3 -and $evidence.events.Count -eq 3) ('Event query evidence incomplete: '+($evidence | ConvertTo-Json -Depth 8)+' queries='+$evidenceFixture.queries)
    Check ($evidence.journals.Count -eq 1 -and $evidence.journals[0].sha256 -ceq 'd7adfb95a9e84a64668941ac83cba05fc8834a20b69df9b3d74766261b24e135') 'Journal not copied and hashed'
    Check ($evidence.dumpInventory.Count -eq 3 -and @($dumpNames | Where-Object {Test-Path -LiteralPath (Join-Path $output $_)}).Count -eq 0) 'Product dump inventory incomplete or dump content was copied'
    $refused=$false
    try { & $collector -OutputDirectory $output -JournalDirectory $journals } catch {$refused=$true}
    Check $refused 'Existing evidence was overwritten'
    $missing=Join-Path $scratch 'missing-report'
    $null=& $collector -OutputDirectory $missing -JournalDirectory (Join-Path $scratch 'absent')
    $failed=Get-Content -LiteralPath (Join-Path $missing 'evidence.json') -Raw | ConvertFrom-Json
    Check (!$failed.complete -and $failed.errors.Count -gt 0) 'Missing journal concealed'
    Write-Output 'COMPLETE: 6 crash evidence checks passed; fake events only'
} finally {
    $env:LOCALAPPDATA=$oldLocalAppData
    # Resolve both paths before recursive cleanup; never touch a user diagnostic directory.
    $resolved=[IO.Path]::GetFullPath($scratch)
    $allowed=[IO.Path]::GetFullPath((Join-Path $repo 'bin-build'))+[IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe test cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
