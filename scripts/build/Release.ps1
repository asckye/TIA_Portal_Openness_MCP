#requires -Version 5.1
<#
.SYNOPSIS
  One-shot release: version bump -> Build-Release -> local gates -> commit -> package -> push -> CI -> tag -> upload -> verify.

.DESCRIPTION
  Wraps the maintainer's release routine (docs/development/release-workflow.md, handoff.md section 4) into one command.
  Before running it, write the human parts yourself: the CHANGELOG entry "## [X.Y.Z] - date", docs/releases/vX.Y.Z.md
  and any doc prose. The script refuses to start without them.

  Steps (each one stops the run when it fails):
    1. Preconditions: repo on master, PublicAPI folders found, Python/Git found, no stray TiaMcpServer.exe process,
       CHANGELOG entry + release note present.
    2. Version bump in the mechanical places: .claude-plugin/plugin.json, tools/mcp-configurator/Configurator.cs,
       both csproj, docs/README.md current-release link, docs/reference/capabilities.md intro, docs/development/roadmap.md title.
    3. scripts/build/Build-Release.ps1 (both engines, offline suite, shape checks, configurator, manifests, tool matrix).
    4. Local gates: Check-Repository.py, Check-DeadToolReferences.py, Validate-Bundle.ps1 -Strict.
    5. One commit "Release X.Y.Z: <summary>" (source + docs + manifest/* + tool-matrix.md). The binaries (runtime/v20,
       runtime/v21, TiaMcpConfigurator.exe) are NOT tracked since 2.8.1; Package-Release.py builds the delivery ZIP from
       the commit plus the local binaries and Verify-ReleaseAsset.py proves the ZIP equals the tree + the recorded hashes.
    6. Push, wait for validate-bundle + offline-checks (GitHub API with the same token as the upload).
    7. Annotated tag vX.Y.Z, push it, then Publish-Release.ps1 uploads the ZIP + .sha256 from this machine (draft ->
       verified assets -> published) and the "Verify published release" workflow re-checks the asset against master.

  Token: -Token, else GITHUB_TOKEN, else the credential Git Credential Manager holds for github.com.
  Nothing here writes prose: after a green publish, record it in handoff.md / handoff-checklist.md yourself.

.PARAMETER Version
  X.Y.Z of the release (must match the newest CHANGELOG entry).
.PARAMETER Summary
  Text for the commit message after "Release X.Y.Z: " (English only). Default: the intro line of the CHANGELOG entry.
.PARAMETER Token
  GitHub token with repo scope for the Actions API and the release upload. Default: GITHUB_TOKEN, else git credential fill.
.PARAMETER ReleaseDate
  yyyyMMdd for the delivery ZIP name. Default: today.
.PARAMETER V20ReferenceRoot / V21ReferenceRoot
  PublicAPI folders. Default: <repo>\TIA_V20_PublicAPI\V20 and <repo>\TIA_V21_PublicAPI\V21\net48, else the same names one level above the repo.
.PARAMETER Python
  python.exe. Default: python on PATH, else %LOCALAPPDATA%\Programs\Python\Python312\python.exe.
.PARAMETER SkipBuild
  Reuse the last Build-Release output (only when nothing under tools/tiaportal-mcp changed since; the hash gate will catch a lie).
.PARAMETER NoPush
  Stop after the commit, the package and its verification.
.PARAMETER NoTag
  Push the commits and wait for CI, but do not tag.
.PARAMETER NoWait
  Do not poll GitHub Actions (push and tag immediately).
.PARAMETER DryRun
  Bump + build + gates only; show what would be committed, commit nothing.
.PARAMETER KillStrayEngine
  Kill a TiaMcpServer.exe left behind on this host (it locks runtime\v21\TiaMcpServer.exe) instead of refusing.
.PARAMETER Resume
  The release commit already exists locally (tree clean): skip bump, build, gates and commit; re-package from HEAD when
  needed and continue with push, CI, tag (on HEAD - the verify workflow requires the tag to be master HEAD) and upload.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Release.ps1 -Version 2.7.57 -Summary "short description of the change"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Summary = '',
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate = (Get-Date -Format 'yyyyMMdd'),
    [string]$V20ReferenceRoot = '',
    [string]$V21ReferenceRoot = '',
    [string]$Python = '',
    [string]$Git = 'git',
    [string]$Token = '',
    [switch]$SkipBuild,
    [switch]$NoPush,
    [switch]$NoTag,
    [switch]$NoWait,
    [switch]$DryRun,
    [switch]$KillStrayEngine,
    [switch]$Resume,
    [int]$CiTimeoutMinutes = 30
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repo
$log = Join-Path $repo 'release.log'
function Say([string]$text) { $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text; Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
function Fail([string]$text) { Say ("FAIL: " + $text); exit 1 }
function Run([string]$exe, [string[]]$arguments, [string]$what) {
    Say ("> " + $what)
    # Native programs (git in particular) write ordinary progress to stderr; under $ErrorActionPreference = 'Stop' PowerShell 5.1
    # turns a redirected stderr line into a terminating error, so the preference is relaxed around the call and only the exit code counts.
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $exe @arguments 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value ([string]$_) -Encoding UTF8 } } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { Fail ($what + " exited with " + $LASTEXITCODE + " (see release.log)") }
}
function ReadText([string]$path) { [IO.File]::ReadAllText($path) }
function WriteText([string]$path, [string]$text) {
    # keep the file's own BOM state and line endings
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $enc = New-Object System.Text.UTF8Encoding($bom)
    [IO.File]::WriteAllText($path, $text, $enc)
}
function ReplaceOnce([string]$path, [string]$old, [string]$new, [string]$what) {
    $text = ReadText $path
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -eq 0 -and $text.Contains($new)) { Say ("  already applied: " + $what); return }
    if ($count -ne 1) { Fail ("expected exactly one occurrence for " + $what + " in " + $path + ", found " + $count) }
    WriteText $path ($text.Replace($old, $new))
    Say ("  bumped: " + $what)
}
function CommitWithMessage([string]$message, [string]$what) {
    # the message goes through a UTF-8 file: PowerShell 5.1 hands native processes console-codepage arguments, which garbles Chinese
    $tmp = Join-Path $env:TEMP ('release-msg-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($tmp, $message, (New-Object System.Text.UTF8Encoding($false)))
    try { & $Git commit -q -F $tmp; if ($LASTEXITCODE -ne 0) { Fail ($what + ' failed') } } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}
function FirstExisting([string[]]$candidates) { foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-Path -LiteralPath $c).Path } } return $null }

"" | Out-File -LiteralPath $log -Encoding UTF8
Say ("Release " + $Version + " (" + $ReleaseDate + ") in " + $repo)

# ---------------------------------------------------------------- 1. preconditions
$branch = (& $Git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') { Fail ("current branch is " + $branch + ", releases go from master") }
& $Git fetch origin --quiet
$behind = (& $Git rev-list --count "master..origin/master").Trim()
if ($behind -ne '0') { Fail ("origin/master has " + $behind + " commit(s) this checkout lacks; pull first") }

if (-not $V20ReferenceRoot) { $V20ReferenceRoot = FirstExisting @((Join-Path $repo 'TIA_V20_PublicAPI\V20'), (Join-Path (Split-Path $repo -Parent) 'TIA_V20_PublicAPI\V20')) }
if (-not $V21ReferenceRoot) { $V21ReferenceRoot = FirstExisting @((Join-Path $repo 'TIA_V21_PublicAPI\V21\net48'), (Join-Path (Split-Path $repo -Parent) 'TIA_V21_PublicAPI\V21\net48')) }
if (-not $V20ReferenceRoot -or -not (Test-Path (Join-Path $V20ReferenceRoot 'Siemens.Engineering.dll'))) { Fail 'V20 PublicAPI not found (needs Siemens.Engineering.dll); pass -V20ReferenceRoot' }
if (-not $V21ReferenceRoot -or -not (Test-Path (Join-Path $V21ReferenceRoot 'Siemens.Engineering.Base.dll'))) { Fail 'V21 PublicAPI net48 not found (needs Siemens.Engineering.Base.dll); pass -V21ReferenceRoot' }
if (-not $Python) {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -notlike '*WindowsApps*') { $Python = $cmd.Source } else { $Python = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe' }
}
if (-not (Test-Path -LiteralPath $Python)) { Fail ('python not found at ' + $Python + '; pass -Python') }
Say ("PublicAPI V20 = " + $V20ReferenceRoot); Say ("PublicAPI V21 = " + $V21ReferenceRoot); Say ("Python = " + $Python)

$stray = Get-Process -Name TiaMcpServer -ErrorAction SilentlyContinue
if ($stray) {
    if ($KillStrayEngine) { $stray | Stop-Process -Force; Start-Sleep -Seconds 2; Say 'killed stray TiaMcpServer.exe' }
    else { Fail ('TiaMcpServer.exe is running on this host (PID ' + ($stray.Id -join ',') + ') and would lock runtime\v21; stop it or pass -KillStrayEngine') }
}

$changelog = Join-Path $repo 'CHANGELOG.md'
$clText = ReadText $changelog
$entry = [regex]::Match($clText, '(?m)^## \[(\d+\.\d+\.\d+)\][^\r\n]*\r?\n')
if (-not $entry.Success) { Fail 'CHANGELOG.md has no "## [x.y.z]" entry' }
if ($entry.Groups[1].Value -ne $Version) { Fail ('newest CHANGELOG entry is ' + $entry.Groups[1].Value + ', not ' + $Version + ' - write the entry first') }
$note = Join-Path $repo ('docs\releases\v' + $Version + '.md')
if (-not (Test-Path -LiteralPath $note)) { Fail ('docs/releases/v' + $Version + '.md is missing - write the release note first') }
if (-not $Summary) {
    $after = $clText.Substring($entry.Index + $entry.Length)
    $intro = ($after -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
    $Summary = ($intro -replace '\[([^\]]+)\]\([^)]*\)', '$1').Trim()
    if ($Summary.Length -gt 300) { $Summary = $Summary.Substring(0, 300) }
}
Say ("Summary = " + $Summary)

# ---------------------------------------------------------------- resume after the commits
$resumed = $false
if ($Resume) {
    # The release commit may already be followed by a docs/scripts commit (for instance the fix for whatever stopped the
    # first run). The verify workflow insists that the tag points at master HEAD, so the tag goes on HEAD and the package
    # must come from HEAD as well (re-packaged below when package-result.json names another commit).
    $releaseCommit = @(& $Git log --pretty='%H %s' -20) | Where-Object { $_ -like ('* Release ' + $Version + ':*') -or $_ -like ('* Release ' + $Version + ' (3/3)*') } | Select-Object -First 1
    $dirtyNow = (& $Git status --porcelain) | Where-Object { $_ -notmatch '^\?\?' }
    if (-not $releaseCommit) { Fail ('-Resume needs the "Release ' + $Version + ':" commit within the last 20 commits') }
    if ($dirtyNow) { Fail ('-Resume needs a clean tree; dirty: ' + ($dirtyNow -join '; ')) }
    Say ('resuming after the release commit (' + ($releaseCommit -split ' ')[0].Substring(0, 7) + '); the tag goes on HEAD')
    $resumed = $true
}
if (-not $resumed) {
# ---------------------------------------------------------------- 2. version bump (mechanical places)
$csproj21 = Join-Path $repo 'tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V21.csproj'
$previous = [regex]::Match((ReadText $csproj21), '<InformationalVersion>(\d+\.\d+\.\d+)</InformationalVersion>').Groups[1].Value
if (-not $previous) { Fail 'cannot read InformationalVersion from the V21 csproj' }
if ($previous -eq $Version) { Say ('version already ' + $Version + ' in the csproj; bump skipped') }
else {
    Say ("bumping " + $previous + " -> " + $Version)
    ReplaceOnce (Join-Path $repo '.claude-plugin\plugin.json') ('"version": "' + $previous + '"') ('"version": "' + $Version + '"') 'plugin.json'
    $cfg = Join-Path $repo 'tools\mcp-configurator\Configurator.cs'
    ReplaceOnce $cfg ('[assembly: AssemblyVersion("' + $previous + '.0")]') ('[assembly: AssemblyVersion("' + $Version + '.0")]') 'Configurator AssemblyVersion'
    ReplaceOnce $cfg ('[assembly: AssemblyFileVersion("' + $previous + '.0")]') ('[assembly: AssemblyFileVersion("' + $Version + '.0")]') 'Configurator AssemblyFileVersion'
    foreach ($proj in @('TiaMcpServer.V20.csproj', 'TiaMcpServer.V21.csproj')) {
        $p = Join-Path $repo ('tools\tiaportal-mcp\src\TiaMcpServer\' + $proj)
        ReplaceOnce $p ('<AssemblyVersion>' + $previous + '</AssemblyVersion>') ('<AssemblyVersion>' + $Version + '</AssemblyVersion>') ($proj + ' AssemblyVersion')
        ReplaceOnce $p ('<FileVersion>' + $previous + '.0</FileVersion>') ('<FileVersion>' + $Version + '.0</FileVersion>') ($proj + ' FileVersion')
        ReplaceOnce $p ('<InformationalVersion>' + $previous + '</InformationalVersion>') ('<InformationalVersion>' + $Version + '</InformationalVersion>') ($proj + ' InformationalVersion')
    }
    ReplaceOnce (Join-Path $repo 'docs\README.md') ('[当前发布说明](releases/v' + $previous + '.md)') ('[当前发布说明](releases/v' + $Version + '.md)') 'docs/README.md current release link'
    ReplaceOnce (Join-Path $repo 'docs\reference\capabilities.md') ('本文说明 ' + $previous + ' 引擎的能力与缺口') ('本文说明 ' + $Version + ' 引擎的能力与缺口') 'capabilities.md intro'
    $roadmap = Join-Path $repo 'docs\development\roadmap.md'
    $rm = ReadText $roadmap
    $title = [regex]::Match($rm, '(?m)^# 路线图与待办（[^）]*，' + [regex]::Escape($previous) + ' 更新）')
    if ($title.Success) { WriteText $roadmap ($rm.Replace($title.Value, $title.Value.Replace($previous + ' 更新', $Version + ' 更新'))); Say '  bumped: roadmap title' }
    elseif ($rm.Contains($Version + ' 更新）')) { Say '  already applied: roadmap title' }
    else { Say '  WARNING: roadmap title not bumped (pattern not found) - fix by hand' }
}
foreach ($f in @('docs\reference\capabilities.md', 'docs\development\roadmap.md')) {
    if (-not (ReadText (Join-Path $repo $f)).Contains($Version)) { Say ('  WARNING: ' + $f + ' does not mention ' + $Version + ' - add the section/entry by hand') }
}

# ---------------------------------------------------------------- 3. Build-Release
$buildLog = Join-Path $repo 'build.log'
if ($SkipBuild) { Say 'Build-Release skipped (-SkipBuild)' }
else {
    if (Test-Path -LiteralPath $buildLog) { Remove-Item -LiteralPath $buildLog -Force }
    Say 'Build-Release.ps1 (both engines, offline suite, shape checks, configurator, manifests) ...'
    $buildArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\build\Build-Release.ps1'),
        '-V20ReferenceRoot', $V20ReferenceRoot, '-V21ReferenceRoot', $V21ReferenceRoot, '-Python', $Python, '-ReleaseDate', $ReleaseDate)
    $proc = Start-Process -FilePath 'powershell' -ArgumentList $buildArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $buildLog -RedirectStandardError (Join-Path $repo 'build.err.log')
    $built = $false
    if (Test-Path -LiteralPath $buildLog) { $built = (Get-Content -LiteralPath $buildLog -Raw) -match 'Built and checked both runtimes' }
    if (-not $built) {
        $err = ''
        if (Test-Path (Join-Path $repo 'build.err.log')) { $err = (Get-Content (Join-Path $repo 'build.err.log') -Raw) }
        Fail ('Build-Release did not report "Built and checked both runtimes" (exit ' + $proc.ExitCode + '). Tail of build.err.log: ' + ($err.Substring([Math]::Max(0, $err.Length - 1500))))
    }
    Say 'Build-Release OK'
}

# ---------------------------------------------------------------- 4. local gates
Run $Python @((Join-Path $repo 'scripts\checks\Check-Repository.py')) 'Check-Repository.py'
Run $Python @((Join-Path $repo 'scripts\checks\Check-DeadToolReferences.py')) 'Check-DeadToolReferences.py'
Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\checks\Validate-Bundle.ps1'), '-Strict') 'Validate-Bundle.ps1 -Strict'

# ---------------------------------------------------------------- 5. commit + package
# stage tracked changes plus new files under the known source/doc roots only - never "git add -A" at the repo root;
# the binaries are ignored by .gitignore since 2.8.1, so they can never end up in the commit
& $Git add -u
& $Git add -- docs tools scripts templates hooks manifest .claude-plugin .github CHANGELOG.md CLAUDE.md README.md README.zh-CN.md NOTICE.md
$staged = @(& $Git diff --cached --name-only)
$binariesStaged = @($staged | Where-Object { $_ -like 'runtime/v2*' -or $_ -eq 'TiaMcpConfigurator.exe' })
if ($binariesStaged) { & $Git reset -q; Fail ('binaries must not be committed (2.8.1 policy): ' + ($binariesStaged -join ', ')) }
$others = (& $Git status --porcelain) | Where-Object { $_ -match '^\?\?' }
if ($others) { Say ("untracked files left out (add them by hand if they belong to the release): " + (($others | ForEach-Object { $_.Substring(3) }) -join ', ')) }
Say ("the release commit will contain " + $staged.Count + " file(s)")
if ($DryRun) {
    Say '--- DryRun: nothing committed. Staged:'
    $staged | ForEach-Object { Say ("    " + $_) }
    & $Git reset -q
    exit 0
}
if (-not $staged) { Fail 'nothing to commit - did the bump run?' }
CommitWithMessage ('Release ' + $Version + ': ' + $Summary) 'release commit'
$dirty = (& $Git status --porcelain) | Where-Object { $_ -notmatch '^\?\?' }
if ($dirty) { Fail ('working tree still dirty after the commit: ' + ($dirty -join '; ')) }
Say 'release commit created'
}
# ---------------------------------------------------------------- 5b. package from HEAD (also on -Resume when the package is stale)
$gitExe = (Get-Command $Git).Source
$head = (& $Git rev-parse HEAD).Trim()
$pkgDir = Join-Path $repo ('bin-build\releases\v' + $Version)
$pkgName = ((Get-Content -LiteralPath (Join-Path $repo 'manifest\delivery.json') -Raw) | ConvertFrom-Json).package
$resultPath = Join-Path $pkgDir 'package-result.json'
$packaged = $null
if ($resumed -and (Test-Path -LiteralPath $resultPath)) { $packaged = (Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json).sourceCommit }
if ($packaged -eq $head -and (Test-Path -LiteralPath (Join-Path $pkgDir ($pkgName + '.zip')))) { Say ('package from ' + $head.Substring(0, 12) + ' already exists') }
else {
    foreach ($leftover in @((Join-Path $pkgDir $pkgName), (Join-Path $pkgDir ($pkgName + '.zip')), (Join-Path $pkgDir ($pkgName + '.sha256')), $resultPath)) {
        if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force; Say ('removed earlier output ' + $leftover) }
    }
    Run $Python @((Join-Path $repo 'scripts\build\Package-Release.py'), '--git', $gitExe) 'Package-Release.py'
}
Run $Python @((Join-Path $repo 'scripts\checks\Verify-ReleaseAsset.py'), (Join-Path $pkgDir ($pkgName + '.zip')), '--git', $gitExe) 'Verify-ReleaseAsset.py (ZIP = tree + recorded binaries)'
if ($NoPush) { Say 'stopped before push (-NoPush)'; exit 0 }

# ---------------------------------------------------------------- 6. push + CI (GitHub API with the release token)
if (-not $Token) { $Token = $env:GITHUB_TOKEN }
function GitCredentialToken([string]$gitExe) {
    # git credential fill with the request piped by cmd from a temp file: PowerShell's own pipeline and .NET's
    # redirected stdin both make git answer "missing protocol field" (encoding / launcher quirks), cmd does not
    $tmp = Join-Path $env:TEMP ('git-credential-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllBytes($tmp, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    try {
        $exe = (Get-Command $gitExe).Source
        $text = (& cmd.exe /c ('type "' + $tmp + '" | "' + $exe + '" credential fill 2>nul')) -join "`n"
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    $m = [regex]::Match([string]$text, '(?m)^password=(.+)$')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}
if (-not $Token) { $Token = GitCredentialToken $Git }
if (-not $Token) { Fail 'no GitHub token for the Actions API and the upload: pass -Token, set GITHUB_TOKEN, or push once so Git Credential Manager stores one' }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$apiHeaders = @{ Authorization = ('Bearer ' + $Token); Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'TiaMcp-Release' }
$apiBase = 'https://api.github.com/repos/asckye/TIA_Portal_Openness_MCP'
Run $Git @('push', 'origin', 'master') 'push master'
function WorkflowState([string]$workflowName, [string]$sha) {
    try { $runs = Invoke-RestMethod -Uri ($apiBase + '/actions/runs?head_sha=' + $sha + '&per_page=50') -Headers $apiHeaders -TimeoutSec 60 } catch { return 'api unreachable: ' + $_.Exception.Message }
    $run = @($runs.workflow_runs | Where-Object { $_.name -eq $workflowName } | Sort-Object run_number -Descending) | Select-Object -First 1
    if (-not $run) { return 'not listed yet' }
    if ($run.status -ne 'completed') { return $run.status + ' (run ' + $run.run_number + ')' }
    if ($run.conclusion -eq 'success') { return 'success' }
    return 'failed: ' + $run.conclusion + ' ' + $run.html_url
}
function WaitWorkflows([string[]]$workflows, [string]$sha) {
    $deadline = (Get-Date).AddMinutes($CiTimeoutMinutes)
    while ($true) {
        $states = @{}
        foreach ($w in $workflows) { $states[$w] = WorkflowState $w $sha }
        $summary = ($workflows | ForEach-Object { $_ + '=' + $states[$_] }) -join ' | '
        Say ('  CI: ' + $summary)
        if (($states.Values | Where-Object { $_ -like 'failed*' })) { Fail ('CI failed: ' + $summary) }
        if (-not ($states.Values | Where-Object { $_ -ne 'success' })) { return }
        if ((Get-Date) -gt $deadline) { Fail ('CI did not finish within ' + $CiTimeoutMinutes + ' min: ' + $summary) }
        Start-Sleep -Seconds 30
    }
}
if ($NoWait) { Say 'not waiting for CI (-NoWait)' } else { WaitWorkflows @('validate-bundle', 'offline-checks') $head }
if ($NoTag) { Say 'stopped before tag (-NoTag)'; exit 0 }

# ---------------------------------------------------------------- 7. tag + upload + verify
$tag = 'v' + $Version
if (@(& $Git tag -l $tag) -contains $tag) {
    $tagged = (& $Git rev-list -n 1 $tag).Trim()
    if ($tagged -ne $head) { Fail ('local tag ' + $tag + ' points at ' + $tagged + ', not HEAD ' + $head) }
    Say ('tag ' + $tag + ' already exists on HEAD')
} else {
    $tagMsg = Join-Path $env:TEMP ('release-tag-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($tagMsg, ($tag + ': ' + $Summary), (New-Object System.Text.UTF8Encoding($false)))
    & $Git tag -a $tag -F $tagMsg $head
    Remove-Item -LiteralPath $tagMsg -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { Fail ('tag ' + $tag + ' failed') }
}
Run $Git @('push', 'origin', $tag) ('push tag ' + $tag)
Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\build\Publish-Release.ps1'), '-Version', $Version, '-Token', $Token) 'Publish-Release.ps1 (draft -> upload -> verify -> publish)'
$publishedPath = Join-Path $pkgDir 'published-release.json'
if (-not (Test-Path -LiteralPath $publishedPath)) { Fail 'Publish-Release.ps1 left no published-release.json' }
$publishedRelease = Get-Content -LiteralPath $publishedPath -Raw | ConvertFrom-Json
Say ('published: ' + $publishedRelease.html_url)
if (-not $NoWait) {
    Say 'waiting for "Verify published release" (release event on the tag commit)'
    WaitWorkflows @('Verify published release') $head
}
Say ('DONE ' + $tag + '. Now record the publication in docs/development/handoff.md and handoff-checklist.md and commit that separately.')
