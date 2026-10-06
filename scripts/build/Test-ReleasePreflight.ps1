#Requires -Version 5.1
<#
.SYNOPSIS
  Release preflight: every release check that needs no build, run to completion, all failures reported together.
.DESCRIPTION
  Runs in a few minutes before the hour-long release chain (Run-ReleaseBuild.ps1 step 00) so that repository, link,
  CHANGELOG/version, generator, snapshot-format, release-script and approval/relocation checker problems surface at once
  instead of one per chain run. It writes nothing tracked; it needs Python and PowerShell 7 (pwsh) plus Windows PowerShell.
.PARAMETER Python
  Python executable (default: python).
#>
param([string]$Python = 'python')
$ErrorActionPreference = 'Continue'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repo
$failed = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [string]$Exe, [string[]]$Arguments) {
    $global:LASTEXITCODE = 0
    $output = & $Exe @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE) {
        $failed.Add($Name)
        Write-Output "FAIL $Name (exit $LASTEXITCODE)"
        ($output -split "`r?`n" | Where-Object { $_ -match 'FAIL|rror|issue|mismatch|stale|missing' } | Select-Object -First 8) | ForEach-Object { Write-Output "     $_" }
    } else { Write-Output "ok   $Name" }
}
try {
    Check 'repository, links, shipped-document links, CHANGELOG version' $Python @('scripts/checks/Check-Repository.py', '--no-binaries')
    Check 'repository check self-tests' $Python @('scripts/checks/Check-Repository.py', '--self-test')
    Check 'dead tool references (descriptions and current docs)' $Python @('scripts/checks/Check-DeadToolReferences.py')
    Check 'phase-6 tables generator' $Python @('-B', 'scripts/generate/Generate-Phase6Plan.py', '--check')
    Check 'tool usage catalog generator' $Python @('scripts/generate/Generate-ToolUsage.py', '--check')
    if (Test-Path -LiteralPath 'scripts/generate/Generate-ReleaseToolMigration.py') {
        Check 'release migration tables generator' $Python @('scripts/generate/Generate-ReleaseToolMigration.py', '--check')
    }
    Check 'V4 contract snapshots' $Python @('scripts/checks/Snapshot-ToolContracts.py', 'verify')
    Check 'V4 response snapshots' $Python @('scripts/checks/Snapshot-ToolResponses.py', 'verify')
    Check 'suite runner self-tests' $Python @('scripts/checks/Test-DotnetSuites.py', '--self-test')
    Check 'release approval gate self-tests' $Python @('scripts/checks/Test-ReleaseApprovalGate.py', '--self-test')
    Check 'relocation checker self-tests' $Python @('scripts/checks/Test-RelocatedBundle.py', '--self-test')
    Check 'strict bundle rules (pwsh)' 'pwsh' @('-NoProfile', '-File', 'scripts/checks/Validate-Bundle.ps1', '-Strict', '-NoBinaries', '-SkipSourceHashes')
    Check 'strict bundle rules (Windows PowerShell)' 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/checks/Validate-Bundle.ps1', '-Strict', '-NoBinaries', '-SkipSourceHashes')
    Check 'release documentation rules' 'pwsh' @('-NoProfile', '-File', 'scripts/build/Release.ps1', '-DocumentationOnly')
    Check 'release script self-tests' 'pwsh' @('-NoProfile', '-File', 'scripts/build/Release.ps1', '-SelfTest')
    Check 'release prerequisite self-tests' 'pwsh' @('-NoProfile', '-File', 'scripts/build/Test-ReleasePrerequisites.ps1', '-SelfTest')
    Check 'parallel pipeline self-tests' 'pwsh' @('-NoProfile', '-File', 'scripts/build/Build-Release.ps1', '-SelfTest')
} finally { Pop-Location }
if ($failed.Count) {
    Write-Output "PREFLIGHT FAILED: $($failed.Count) check(s): $($failed -join '; ')"
    exit 1
}
Write-Output 'PREFLIGHT PASSED: every build-free release check passed'
