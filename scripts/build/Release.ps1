#requires -Version 5.1
<#
.SYNOPSIS
  One-shot release: version bump -> Build-Release -> local gates -> 3 commits -> push -> CI -> tag -> publish check.

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
    5. Commits "Release X.Y.Z (1/3)" (source + docs), "(2/3)" (runtime/v20 exe), "(3/3)" (runtime/v21 exe + configurator +
       manifest/* + tool-matrix.md); then Package-Release.py as a local dry run.
    6. Push each commit on its own (large exes; http.postBuffer raised), wait for validate-bundle + offline-checks.
    7. Annotated tag vX.Y.Z, push it, wait for "Publish complete release", verify the ZIP asset on the release page.

  Nothing here writes prose: after a green publish, record it in handoff.md / handoff-checklist.md yourself.

.PARAMETER Version
  X.Y.Z of the release (must match the newest CHANGELOG entry).
.PARAMETER Summary
  Text for the (1/3) commit message after "Release X.Y.Z (1/3): ". Default: the intro line of the CHANGELOG entry.
.PARAMETER ReleaseDate
  yyyyMMdd for the delivery ZIP name. Default: today.
.PARAMETER V20ReferenceRoot / V21ReferenceRoot
  PublicAPI folders. Default: <repo>\TIA_V20_PublicAPI\V20 and <repo>\TIA_V21_PublicAPI\V21\net48, else the same names one level above the repo.
.PARAMETER Python
  python.exe. Default: python on PATH, else %LOCALAPPDATA%\Programs\Python\Python312\python.exe.
.PARAMETER SkipBuild
  Reuse the last Build-Release output (only when nothing under tools/tiaportal-mcp changed since; the hash gate will catch a lie).
.PARAMETER NoPush
  Stop after the three commits and the packaging dry run.
.PARAMETER NoTag
  Push the commits and wait for CI, but do not tag.
.PARAMETER NoWait
  Do not poll GitHub Actions (push and tag immediately).
.PARAMETER DryRun
  Bump + build + gates only; show what would be committed, commit nothing.
.PARAMETER KillStrayEngine
  Kill a TiaMcpServer.exe left behind on this host (it locks runtime\v21\TiaMcpServer.exe) instead of refusing.
.PARAMETER Resume
  The three release commits already exist locally (tree clean): skip bump, build, gates and commits and continue with
  push, CI, tag (on HEAD - the publish workflow requires the tag to be master HEAD) and publish (for a run that stopped after committing).

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
    # The (3/3) commit may already be followed by a docs/scripts commit (for instance the fix for whatever stopped the
    # first run). The publish workflow insists that the tag points at master HEAD ("Master changed" otherwise - the
    # 2.7.57 run 58 failure), so the tag goes on HEAD; every push triggers validate-bundle / offline-checks, which are
    # waited for on HEAD's own commit title.
    $threeOfThree = @(& $Git log --pretty='%H %s' -20) | Where-Object { $_ -like ('* Release ' + $Version + ' (3/3)*') } | Select-Object -First 1
    $dirtyNow = (& $Git status --porcelain) | Where-Object { $_ -notmatch '^\?\?' }
    if (-not $threeOfThree) { Fail ('-Resume needs the "Release ' + $Version + ' (3/3)" commit within the last 20 commits') }
    if ($dirtyNow) { Fail ('-Resume needs a clean tree; dirty: ' + ($dirtyNow -join '; ')) }
    Say ('resuming after the three commits (' + ($threeOfThree -split ' ')[0].Substring(0, 7) + '); the tag goes on HEAD')
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

# ---------------------------------------------------------------- 5. commits
$part3 = @('runtime/v21/TiaMcpServer.exe', 'TiaMcpConfigurator.exe', 'manifest/configurator-build.json', 'manifest/delivery.json', 'manifest/package-manifest.json', 'manifest/release-build.json', 'manifest/tools-list.json', 'docs/reference/tool-matrix.md')
$part2 = @('runtime/v20/TiaMcpServer.exe')
# stage tracked changes plus new files under the known source/doc roots only - never "git add -A" at the repo root
& $Git add -u
& $Git add -- docs tools scripts templates hooks manifest .claude-plugin .github CHANGELOG.md CLAUDE.md README.md README.zh-CN.md NOTICE.md
& $Git reset -q -- $part2 $part3
$staged = (& $Git diff --cached --name-only)
$others = (& $Git status --porcelain) | Where-Object { $_ -match '^\?\?' }
if ($others) { Say ("untracked files left out (add them by hand if they belong to the release): " + (($others | ForEach-Object { $_.Substring(3) }) -join ', ')) }
Say ("(1/3) will contain " + (@($staged).Count) + " file(s)")
if ($DryRun) {
    Say '--- DryRun: nothing committed. Staged for (1/3):'
    $staged | ForEach-Object { Say ("    " + $_) }
    Say ('--- (2/3): ' + ($part2 -join ', '))
    Say ('--- (3/3): ' + ($part3 -join ', '))
    & $Git reset -q
    exit 0
}
if (-not $staged) { Fail 'nothing to commit for (1/3) - did the bump run?' }
CommitWithMessage ('Release ' + $Version + ' (1/3): ' + $Summary) 'commit (1/3)'
& $Git add -- $part2
CommitWithMessage ('Release ' + $Version + ' (2/3): V20 runtime ' + $Version + '.0') 'commit (2/3)'
& $Git add -- $part3
CommitWithMessage ('Release ' + $Version + ' (3/3): V21 runtime, configurator and validated manifests') 'commit (3/3)'
$dirty = (& $Git status --porcelain) | Where-Object { $_ -notmatch '^\?\?' }
if ($dirty) { Fail ('working tree still dirty after the three commits: ' + ($dirty -join '; ')) }
Say 'three release commits created'
$gitExe = (Get-Command $Git).Source
$pkgDir = Join-Path $repo ('bin-build\releases\v' + $Version)
$pkgName = ((Get-Content -LiteralPath (Join-Path $repo 'manifest\delivery.json') -Raw) | ConvertFrom-Json).package
foreach ($leftover in @((Join-Path $pkgDir $pkgName), (Join-Path $pkgDir ($pkgName + '.zip')))) {
    if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force; Say ('removed earlier dry-run output ' + $leftover) }
}
Run $Python @((Join-Path $repo 'scripts\build\Package-Release.py'), '--git', $gitExe) 'Package-Release.py (local dry run)'
if ($NoPush) { Say 'stopped before push (-NoPush)'; exit 0 }
}

# ---------------------------------------------------------------- 6. push + CI
$shas = (& $Git rev-list --reverse --first-parent origin/master..master)
foreach ($sha in $shas) {
    Run $Git @('-c', 'http.postBuffer=157286400', 'push', 'origin', ($sha + ':refs/heads/master')) ('push ' + $sha)
}
# CI runs are found by the commit title on the workflow pages; HEAD is the (3/3) commit unless a follow-up commit was added before -Resume.
$headTitle = (& $Git log -1 --pretty=%s).Trim()
$title3 = $(if ($headTitle.Length -gt 60) { $headTitle.Substring(0, 60) } else { $headTitle })
function WorkflowState([string]$workflow, [string]$titlePart) {
    # GitHub's unauthenticated API is rate-limited from here; the workflow page's aria-labels carry the same information.
    try {
        $html = (Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/asckye/TIA_Portal_Openness_MCP/actions/workflows/' + $workflow) -TimeoutSec 60).Content
    } catch { return 'unreachable' }
    $m = [regex]::Match($html, 'aria-label="([^"]*' + [regex]::Escape($titlePart) + '[^"]*)"')
    if (-not $m.Success) { return 'not listed yet' }
    $label = $m.Groups[1].Value
    if ($label -like 'completed successfully*') { return 'success' }
    if ($label -like '*failed*' -or $label -like '*cancelled*') { return 'failed: ' + $label }
    return 'running: ' + $label
}
function WaitWorkflows([string[]]$workflows, [string]$titlePart) {
    $deadline = (Get-Date).AddMinutes($CiTimeoutMinutes)
    while ($true) {
        $states = @{}
        foreach ($w in $workflows) { $states[$w] = WorkflowState $w $titlePart }
        $summary = ($workflows | ForEach-Object { $_ + '=' + $states[$_] }) -join ' | '
        Say ('  CI: ' + $summary)
        if (($states.Values | Where-Object { $_ -like 'failed*' })) { Fail ('CI failed: ' + $summary) }
        if (-not ($states.Values | Where-Object { $_ -ne 'success' })) { return }
        if ((Get-Date) -gt $deadline) { Fail ('CI did not finish within ' + $CiTimeoutMinutes + ' min: ' + $summary) }
        Start-Sleep -Seconds 45
    }
}
if ($NoWait) { Say 'not waiting for CI (-NoWait)' } else { WaitWorkflows @('validate.yml', 'offline-checks.yml') $title3 }
if ($NoTag) { Say 'stopped before tag (-NoTag)'; exit 0 }

# ---------------------------------------------------------------- 7. tag + publish
$tag = 'v' + $Version
$head = (& $Git rev-parse HEAD).Trim()
$tagMsg = Join-Path $env:TEMP ('release-tag-' + [guid]::NewGuid().ToString('N') + '.txt')
[IO.File]::WriteAllText($tagMsg, ($tag + ': ' + $Summary), (New-Object System.Text.UTF8Encoding($false)))
& $Git tag -a $tag -F $tagMsg $head
Remove-Item -LiteralPath $tagMsg -Force -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0) { Fail ('tag ' + $tag + ' failed (already exists?)') }
Run $Git @('push', 'origin', $tag) ('push tag ' + $tag)
Say ('tag ' + $tag + ' pushed -> Publish complete release')
if (-not $NoWait) {
    WaitWorkflows @('release.yml') $title3
    # the ZIP carries the date recorded by Build-Release (may differ from today after -SkipBuild)
    $delivery = (Get-Content -LiteralPath (Join-Path $repo 'manifest\delivery.json') -Raw) | ConvertFrom-Json
    $asset = $delivery.package + '.zip'
    $found = $false
    for ($i = 0; $i -lt 12 -and -not $found; $i++) {
        try { $page = (Invoke-WebRequest -UseBasicParsing -Uri ('https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/' + $tag) -TimeoutSec 60).Content; $found = $page.Contains($asset) } catch { }
        if (-not $found) { Start-Sleep -Seconds 10 }
    }
    if (-not $found) { Fail ('release page does not list ' + $asset + ' yet - check https://github.com/asckye/TIA_Portal_Openness_MCP/releases/tag/' + $tag) }
    Say ('published: ' + $asset)
}
Say ('DONE ' + $tag + '. Now record the publication in docs/development/handoff.md and handoff-checklist.md and commit that separately.')
