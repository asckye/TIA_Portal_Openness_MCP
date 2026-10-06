#requires -Version 5.1
<#
.SYNOPSIS
  One-shot release: prerequisites -> bump -> cheap gates -> reuse/parallel builds -> binary gates -> commit -> package -> publish.

.DESCRIPTION
  Wraps the maintainer's release routine (docs/development/release-workflow.md, handoff.md section 4) into one command.
  Before running it, write the human parts yourself: the CHANGELOG entry "## [X.Y.Z] - date", docs/releases/vX.Y.Z.md
  and any doc prose. The script refuses to start without them.

  Steps (each one stops the run when it fails):
    1. Prerequisite table before any mutation/network operation; then master/upstream and stray-engine checks.
    2. Version bump in Version.props and .claude-plugin/plugin.json,
       docs/README.md current-release link and docs/development/roadmap.md title.
    3. Early source/documentation gates; reuse matching records or run concurrent V20/V21 pipelines and the multi-version build.
    4. Post-build repository/binary/hash gates, including Validate-Bundle.ps1 -Strict.
    5. One commit "Release X.Y.Z: <summary>" (source + docs + manifest/* + tool-matrix.md). The binaries (runtime/v20,
       runtime/v21, TiaOpenness.exe) are NOT tracked since 2.8.1; Package-Release.py builds the delivery ZIP from
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
  PublicAPI folders. Default: version folders below -PublicApiRoot (by default the parent of this repository).
.PARAMETER Python
  Python 3.12+ executable. Default: python on PATH. Ecosystem checks use TIA_MCP_PLC_TOOLS_PYTHON.
.PARAMETER SkipBuild
  Require both build records to be reusable; refuse any version/source/binary mismatch.
.PARAMETER NoReuse
  Force both builds and their full validation suites, including on Resume.
.PARAMETER EarlyGatesOnly
  Run the post-bump early gates in isolation; no prerequisite check, bump, Git writes or remote operation.
.PARAMETER DocumentationOnly
  Routine CI assertions for release notes, CHANGELOG, Version.props, README and roadmap.
.PARAMETER SelfTest
  Offline reuse and archive tests using synthetic files in bin-build.
.PARAMETER NoPush
  Stop after the commit, the package and its verification.
.PARAMETER NoTag
  Push the commits and wait for CI, but do not tag.
.PARAMETER NoWait
  Do not poll GitHub Actions (push and tag immediately).
.PARAMETER DryRun
  Bump + build + gates only; show what would be committed, commit nothing.
.PARAMETER KillStrayEngine
  Kill a TiaMcp.Engine.V20.exe / TiaMcp.Engine.V21.exe / TiaMcp.FoundationHost.exe left behind on this host (it locks runtime\v21\TiaMcp.Engine.V21.exe) instead of refusing.
.PARAMETER Resume
  The release commit exists locally. Recheck prerequisites, early gates and hashes. Matching records skip rebuild/commit;
  changed inputs (or NoReuse) return to the build/commit path. Archive old output and package from the current HEAD.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Release.ps1 -Version 2.7.57 -Summary "short description of the change"
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Summary = '',
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate = (Get-Date -Format 'yyyyMMdd'),
    [string]$V20ReferenceRoot = '',
    [string]$V21ReferenceRoot = '',
    [string]$PublicApiRoot = '',
    [string]$PowerShell7 = 'pwsh',
    [string]$Python = '',
    [string]$Git = 'git',
    [string]$Token = '',
    [switch]$SkipBuild,
    [switch]$NoReuse,
    [switch]$EarlyGatesOnly,
    [switch]$DocumentationOnly,
    [switch]$SelfTest,
    [switch]$FunctionsOnly,
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
# Isolated host roots live outside the repository: tests that create Git repositories or look for a source checkout must
# not find this one. The outermost script fixes the root; nested scripts inherit it through the environment.
if(-not $env:TIA_MCP_RELEASE_TEMP_ROOT){$env:TIA_MCP_RELEASE_TEMP_ROOT=Join-Path ([IO.Path]::GetTempPath()) ('tmr-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$releaseTempRoot=[IO.Path]::GetFullPath($env:TIA_MCP_RELEASE_TEMP_ROOT)
# Build servers started under an isolated TEMP would outlive the check and lock files there.
$env:UseSharedCompilation='false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
if($releaseTempRoot.StartsWith(([IO.Path]::GetFullPath($repo).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw "Release temp root must be outside the repository: $releaseTempRoot"}
Set-Location $repo
$log = Join-Path $repo 'release.log'
$script:releaseRunTemp = ''
function Say([string]$text) { $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text; Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
function Fail([string]$text) { Say ("FAIL: " + $text); exit 1 }
function Run([string]$exe, [string[]]$arguments, [string]$what) {
    Say ("> " + $what)
    if (-not $script:releaseRunTemp) {
        $script:releaseRunTemp = Join-Path $releaseTempRoot ('rel-' + [guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Force -Path $script:releaseRunTemp | Out-Null
    }
    # Short ids keep nested paths below MAX_PATH; check.txt names the command.
    $hostRoot = Join-Path $script:releaseRunTemp ([guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $hostRoot 'check.txt'), [IO.Path]::GetFileNameWithoutExtension($exe))
    New-Item -ItemType Directory -Force -Path (Join-Path $hostRoot 'config'),(Join-Path $hostRoot 'temp'),(Join-Path $hostRoot 'local-app-data'),(Join-Path $hostRoot 'app-data') | Out-Null
    [IO.File]::WriteAllText((Join-Path $hostRoot 'config/approval.settings'), "enabled=false`ntimeoutSeconds=120`n", (New-Object System.Text.UTF8Encoding($false)))
    $savedData = $env:TIA_MCP_DATA_DIRECTORY; $savedDiagnostics = $env:TIA_MCP_DIAGNOSTICS_DIRECTORY
    $savedLocalAppData = $env:LOCALAPPDATA; $savedAppData = $env:APPDATA; $savedTemp = $env:TEMP; $savedTmp = $env:TMP
    $completed = $false; $exitCode = $null
    # Native programs (git in particular) write ordinary progress to stderr; under $ErrorActionPreference = 'Stop' PowerShell 5.1
    # turns a redirected stderr line into a terminating error, so the preference is relaxed around the call and only the exit code counts.
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $env:TIA_MCP_DATA_DIRECTORY = $hostRoot
        $env:TIA_MCP_DIAGNOSTICS_DIRECTORY = Join-Path $hostRoot 'diagnostics'
        $env:LOCALAPPDATA = Join-Path $hostRoot 'local-app-data'
        $env:APPDATA = Join-Path $hostRoot 'app-data'
        $env:TEMP = Join-Path $hostRoot 'temp'; $env:TMP = $env:TEMP
        & $exe @arguments 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value ([string]$_) -Encoding UTF8 }
        $exitCode = $LASTEXITCODE
        $completed = ($exitCode -eq 0)
    } finally {
        $ErrorActionPreference = $previous
        $env:TIA_MCP_DATA_DIRECTORY = $savedData; $env:TIA_MCP_DIAGNOSTICS_DIRECTORY = $savedDiagnostics
        $env:LOCALAPPDATA = $savedLocalAppData; $env:APPDATA = $savedAppData; $env:TEMP = $savedTemp; $env:TMP = $savedTmp
        if($completed){try{Remove-Item -LiteralPath $hostRoot -Recurse -Force -ErrorAction Stop}catch{Write-Warning ("release check data still in use (kept): "+$hostRoot)}}
        else { Write-Host "Retained failed release check data: $hostRoot" }
    }
    if ($exitCode -ne 0) { Fail ($what + " exited with " + $exitCode + " (see release.log)") }
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

function Get-ReleaseSources([string]$Root, [ValidateSet('engine','multi')][string]$Kind) {
    # Preserve the exact engine sourceFiles contract enforced by Package-Release.py.
    $roots = @('src/Engine','src/FoundationHost','src/Worker','src/Logic','src/Runtime','src/WorkerChannel','src/Adapters','src/Adapters.Contracts','src/Tools/WriteGuard','tests/Engine','tests/Tools','tests/test-suites.json','build-tools/native-call-weaver','src/Shared',
        'third_party/TiaGitAddIn.Core','third_party/SiemensOpcUaModelled')
    $extensions = @('.cs','.csproj','.props','.targets','.xml','.json')
    $files = @()
    if ($Kind -eq 'engine') { $files += Get-Item -LiteralPath (Join-Path $Root 'Version.props') }
    else {
        $roots = @('src/Engine','src/FoundationHost','src/Worker','src/Logic','src/Runtime','src/WorkerChannel','src/Adapters','src/Adapters.Contracts','src/Tools/WriteGuard','tests/Engine','tests/Tools','tests/test-suites.json','src/Shared','src/Studio','tests/Studio','third_party/tia-openness-studio',
            'build-tools/native-call-weaver','scripts/build','scripts/checks','scripts/diagnostics','scripts/generate')
        $extensions = @('.cs','.csproj','.props','.targets','.xaml','.ps1','.py','.json')
    }
    $files += Get-ChildItem ($roots | ForEach-Object { Join-Path $Root $_ }) -Recurse -File | Where-Object {
        $_.Extension -in $extensions -and $_.FullName -notmatch '[\\/](obj|obj-v20|bin|bin-v20)[\\/]'
    }
    foreach ($file in ($files | Sort-Object FullName -Unique)) {
        [pscustomobject]@{path=$file.FullName.Substring($Root.Length+1).Replace('\','/');sha256=(Get-ReleaseSourceHash $file.FullName)}
    }
}
function Get-ReleaseValidationInputs([string]$Root, [ValidateSet('engine','multi')][string]$Kind) {
    $roots = @('src/Engine','src/FoundationHost','src/Worker','src/Logic','src/Runtime','src/WorkerChannel','src/Adapters','src/Adapters.Contracts','src/Tools/WriteGuard','tests/Engine','tests/Tools','tests/test-suites.json','build-tools/native-call-weaver','src/Shared',
        'third_party/eido-import-planner','third_party/siemens-plc-tools','third_party/SiemensOpcUaModelled','third_party/simaticml-decoder','third_party/TiaGitAddIn.Core','scripts/build','scripts/checks','scripts/diagnostics','scripts/generate','scripts/ecosystem',
        'reference','templates')
    if ($Kind -eq 'multi') { $roots += @('src/Studio','tests/Studio','third_party/tia-openness-studio') }
    $files = @((Get-Item -LiteralPath (Join-Path $Root 'Version.props')))
    $files += Get-ChildItem ($roots | ForEach-Object { Join-Path $Root $_ }) -Recurse -File | Where-Object {
        $_.Extension -in '.cs','.csproj','.props','.targets','.xml','.json','.xaml','.ps1','.py','.toml','.xsd','.ttf','.otf' -and
        $_.FullName -notmatch '[\\/](obj|obj-v20|bin|bin-v20|__pycache__|\.venv)[\\/]'
    }
    foreach ($file in ($files | Sort-Object FullName -Unique)) {
        [pscustomobject]@{path=$file.FullName.Substring($Root.Length+1).Replace('\','/');sha256=(Get-ReleaseSourceHash $file.FullName)}
    }
}
function Get-ReleaseSourceHash([string]$Path) {
    if ([IO.Path]::GetExtension($Path) -in '.ttf','.otf') { $bytes = [IO.File]::ReadAllBytes($Path) }
    else { $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($Path).Replace("`r`n","`n")) }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Get-ReleaseRuntimeFiles([string]$Root, [switch]$Prepared) {
    $bundled=Join-Path $Root 'runtime/dotnet'
    Get-ChildItem -LiteralPath (Join-Path $Root 'runtime') -Recurse -File | Where-Object {
        ($_.Extension -in '.exe','.dll','.config','.json','.txt' -or $_.FullName.StartsWith($bundled+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) -and
        $_.Name -ne 'README.md' -and (-not $Prepared -or $_.FullName -notmatch '[\\/]runtime[\\/](v20|v21|verification)[\\/]')
    } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($Root.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    }
}
function Assert-ReleaseRuntimePreparation([string]$Root) {
    $dependencies=@('TiaMcp.WorkerChannel.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')
    $required=@('runtime/studio/TiaOpenness.exe','runtime/studio/TiaMcp.WorkerChannel.dll')
    $required+=@($dependencies | ForEach-Object {"runtime/studio/bridge/$_"})
    $required+=@('14sp1','15.1','16','17','18','19','20','21' | ForEach-Object {"runtime/studio/bridge/adapters/v$_/TiaOpenness.Openness.dll"})
    foreach($key in @('14sp1','15.1','16','17','18','19')) {
        $required+="runtime/v$key/TiaMcp.FoundationHost.exe"
        $required+=@($dependencies | ForEach-Object {"runtime/v$key/worker/$_"})
        $required+="runtime/v$key/TiaMcp.WorkerChannel.dll"
    }
    $missing=@($required | Where-Object {!(Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf)})
    # Foundation loads these from the bundled framework, not the host directory.
    foreach($name in @('System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll')) {
        $path="runtime/dotnet/shared/Microsoft.NETCore.App/*/$name"
        if(!(Test-Path -Path (Join-Path $Root $path) -PathType Leaf)){$missing+=$path}
    }
    if($missing.Count){throw ('Delivery prerequisites missing; run Build-MultiVersion.ps1 -PrepareOnly -Test first: '+($missing -join ', '))}
}
function Assert-MultiVersionPreparation([string]$Root, $Record, [string]$Release, $Sources, $Inputs, $Files, [string]$Api) {
    $reason=Test-ReleaseReuse $Root $Record $Release $Sources 'files'
    if($reason){throw "Multi-version preparation cannot be completed: $reason"}
    if($Record.publicApiRoot -ne $Api){throw 'Preparation PublicApiRoot changed'}
    if((($Record.files.path | Sort-Object) -join "`n") -cne (($Files.path | Sort-Object) -join "`n")){throw 'Prepared binary inventory changed'}
    $proof=[pscustomobject]@{release=$Record.release;fileVersion=$Record.fileVersion;sourceFiles=$Record.validationInputs;files=$Record.evidence}
    $reason=Test-ReleaseReuse $Root $proof $Release $Inputs 'files'
    if($reason){throw "Prepared validation inputs/evidence changed: $reason"}
}
function Invoke-ReleaseBuildStages([scriptblock]$Prepare, [scriptblock]$Engine, [scriptblock]$Complete) {
    & $Prepare
    & $Engine
    & $Complete
}
function Test-ReleaseAuditEvidence([string]$Root, $Record) {
    if(@($Record.validationArtifacts).Count -ne 4){return 'full-engine audit evidence record missing; rebuild once'}
    $prefix="bin-build/releases/v$($Record.release)/"
    $expected=@(foreach($major in @(20,21)){"${prefix}v$major/native-call-coverage-v$major.json";"${prefix}v$major/tool-usage-v$major.json"})
    if((($Record.validationArtifacts.path | Sort-Object) -join "`n") -cne (($expected | Sort-Object) -join "`n")){return 'full-engine audit evidence inventory changed'}
    foreach($row in $Record.validationArtifacts) {
        $path=Join-Path $Root $row.path
        if(!(Test-Path -LiteralPath $path -PathType Leaf)){return "audit evidence missing: $($row.path)"}
        if((Get-FileHash -LiteralPath $path).Hash -ne $row.sha256){return "audit evidence changed: $($row.path)"}
    }
    return ''
}
function Restore-ArchivedAuditEvidence([string]$Root, [string]$Archive, $Record) {
    $parent=[IO.Path]::GetFullPath((Join-Path $Root 'bin-build/releases'))
    if((Split-Path ([IO.Path]::GetFullPath($Archive)) -Parent) -ne $parent){throw 'Audit archive escaped releases directory'}
    foreach($row in $Record.validationArtifacts) {
        $prefix="bin-build/releases/v$($Record.release)/"
        if(!$row.path.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Audit evidence path escaped release output'}
        $suffix=$row.path.Substring($prefix.Length)
        $sourcePath=[IO.Path]::GetFullPath((Join-Path $Archive $suffix))
        if(!$sourcePath.StartsWith($Archive+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Audit evidence path escaped archive'}
        if(!(Test-Path -LiteralPath $sourcePath) -or (Get-FileHash -LiteralPath $sourcePath).Hash -ne $row.sha256){throw "Archived audit evidence changed: $($row.path)"}
        $target=Join-Path $Root $row.path
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $target
    }
}
function Test-ReleaseReuse([string]$Root, $Record, [string]$Release, $Sources, [string]$FilesProperty) {
    # A missing or malformed record is always a cache miss, never permission to skip validation.
    try {
        if ($Record.release -ne $Release -or $Record.fileVersion -ne "$Release.0") { return 'release/fileVersion changed' }
        if (-not @($Record.sourceFiles).Count -or -not @($Record.$FilesProperty).Count) { return 'empty source or binary inventory' }
        $recorded = @{}
        foreach ($row in $Record.sourceFiles) {
            if (-not $row.path -or $recorded.ContainsKey($row.path)) { return 'invalid/duplicate source inventory' }
            $recorded[$row.path] = $row.sha256
        }
        if ($recorded.Count -ne @($Sources).Count) { return 'source inventory changed (added/removed input)' }
        foreach ($row in $Sources) { if ($recorded[$row.path] -ne $row.sha256) { return "source changed: $($row.path)" } }
        $seen = @{}
        foreach ($row in $Record.$FilesProperty) {
            $path = [IO.Path]::GetFullPath((Join-Path $Root $row.path))
            if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($row.path)) { return 'invalid/duplicate binary inventory' }
            $seen[$row.path] = $true
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "binary missing: $($row.path)" }
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $row.sha256) { return "binary changed: $($row.path)" }
        }
        return ''
    } catch { return 'record or input is unreadable' }
}
function Get-ReleaseReuseReason([string]$Kind) {
    if ($NoReuse) { return 'forced by -NoReuse' }
    $name = if ($Kind -eq 'engine') { 'release-build.json' } else { 'multi-version-build.json' }
    try { $record = Get-Content -LiteralPath (Join-Path $repo "manifest/$name") -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { return 'build record missing or invalid' }
    if ($Kind -eq 'engine') {
        $reason=Test-ReleaseAuditEvidence $repo $record
        if($reason){return $reason}
        if ($record.validation.offlinePassed -le 0 -or $record.validation.offlineV20Passed -le 0 -or $record.validation.versionPolicySdkPassed -le 0) { return 'offline validation missing' }
        foreach ($major in @(20,21)) {
            $check = $record.validation.runtimes."V$major"
            if ($check.localStability.status -ne 'passed' -or $check.localStability.runs.Count -ne 4 -or $check.isolatedLocalStability.status -ne 'passed' -or $check.isolatedLocalStability.runs.Count -ne 4 -or -not $check.isolatedLocalStability.isolatedWorker) { return "V$major validation incomplete" }
            if ($check.approvalSafety.status -ne 'passed' -or $check.approvalSafety.checksPassed -ne 3 -or -not $check.approvalSafety.defaultEnabled -or -not $check.approvalSafety.directWriteRefusedBeforeDispatch -or -not $check.approvalSafety.callToolWriteRefusedBeforeDispatch -or -not $check.approvalSafety.readSucceeded -or $check.approvalSafety.workbenchConnected -or $check.approvalSafety.tiaConnected) { return "V$major default-approval gate incomplete" }
        }
        $property = 'runtimeFiles'
    } else {
        foreach ($flag in @('foundationTransportExecuted','studioFunctionalTestsExecuted','configurationFunctionalTestsExecuted','toolUsageCoverageExecuted','approvalSafetyExecuted')) {
            if (-not $record.validation.$flag) { return "validation missing: $flag" }
        }
        if ($record.validation.foundationApprovalSafetyChecksPassed -ne 18 -or (($record.validation.foundationCallToolUnsupportedReleases | Sort-Object) -join ',') -cne '14sp1,15.1,16,17,18,19') { return 'Foundation default-approval gate incomplete' }
        $foundationRows = @($record.releases | Where-Object { $_.profile -eq 'plc-foundation' })
        if ($foundationRows.Count -ne 6 -or (($foundationRows.releaseKey | Sort-Object) -join ',') -cne '14sp1,15.1,16,17,18,19') { return 'Foundation release approval inventory incomplete' }
        foreach ($releaseRow in $foundationRows) {
            if ($releaseRow.approvalSafetyChecksPassed -ne 3 -or -not $releaseRow.approvalDefaultEnabled -or $releaseRow.approvalSafety.status -ne 'passed' -or -not $releaseRow.approvalSafety.directWriteRefusedBeforeDispatch -or -not $releaseRow.approvalSafety.readSucceeded -or $releaseRow.approvalSafety.callTool -ne 'not-advertised-by-Foundation-V4') { return "Foundation approval gate incomplete: $($releaseRow.releaseKey)" }
        }
        if (($record.studioReleaseKeys -join ',') -ne '14sp1,15.1,16,17,18,19,20,21') { return 'eight-version validation missing' }
        $property = 'files'
    }
    $runtimeRoots = if ($Kind -eq 'engine') { @('runtime/v20','runtime/v21','runtime/verification') } else { @('runtime') }
    $onDisk = @(Get-ChildItem ($runtimeRoots | ForEach-Object { Join-Path $repo $_ }) -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
        if ($Kind -eq 'engine') { $_.Extension -in '.exe','.dll','.config' -or $_.Name -in 'NativeCallWeaver.deps.json','NativeCallWeaver.runtimeconfig.json' }
        else { ($_.Extension -in '.exe','.dll','.config','.json','.txt' -or $_.FullName.StartsWith((Join-Path $repo 'runtime/dotnet') + [IO.Path]::DirectorySeparatorChar)) -and $_.Name -ne 'README.md' }
    } | ForEach-Object { $_.FullName.Substring($repo.Length+1).Replace('\','/') } | Sort-Object)
    if (($onDisk -join "`n") -cne (@($record.$property.path | Sort-Object) -join "`n")) { return 'runtime inventory changed (added/removed binary)' }
    if (-not $record.validationInputs) { return 'supplemental validation input record missing; rebuild once with this pipeline' }
    $inputs = @(Get-ReleaseValidationInputs $repo $Kind)
    $proof = [pscustomobject]@{release=$record.release;fileVersion=$record.fileVersion;sourceFiles=$record.validationInputs;binaries=$record.$property}
    $reason = Test-ReleaseReuse $repo $proof $Version $inputs 'binaries'
    if ($reason) { return $reason }
    return Test-ReleaseReuse $repo $record $Version @(Get-ReleaseSources $repo $Kind) $property
}
function Move-PreviousReleaseOutput([string]$Root, [string]$Release) {
    $parent = [IO.Path]::GetFullPath((Join-Path $Root 'bin-build/releases'))
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $parent "v$Release"))
    $targetPath = [IO.Path]::GetFullPath(($sourcePath + '.previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)))
    if ((Split-Path $sourcePath -Parent) -ne $parent -or (Split-Path $targetPath -Parent) -ne $parent) { throw 'Archive path escaped the releases directory' }
    if (Test-Path -LiteralPath $sourcePath) {
        if ((Get-Item -LiteralPath $sourcePath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release output must not be a reparse point' }
        Move-Item -LiteralPath $sourcePath -Destination $targetPath
        return $targetPath
    }
}
function Test-ReleaseDocumentation([bool]$RequireNewest) {
    [xml]$props = ReadText (Join-Path $repo 'Version.props')
    $actual = [string]$props.Project.PropertyGroup.TiaMcpRelease
    if ($actual -ne $Version) { Fail 'Early gates require Version.props to match -Version (run after the mechanical bump)' }
    $entry = [regex]::Match((ReadText (Join-Path $repo 'CHANGELOG.md')), '(?m)^## \[(\d+\.\d+\.\d+)\]')
    if (-not $entry.Success -or [version]$entry.Groups[1].Value -lt [version]$Version -or ($RequireNewest -and $entry.Groups[1].Value -ne $Version)) { Fail 'Newest CHANGELOG entry differs from the requested release' }
    $noteVersion = $entry.Groups[1].Value
    if (-not (Test-Path -LiteralPath (Join-Path $repo "docs/releases/v$noteVersion.md"))) { Fail 'Matching release note missing' }
    if (-not (ReadText (Join-Path $repo 'docs/README.md')).Contains("[当前版本说明](releases/v$Version.md)")) { Fail 'docs/README.md current release link is stale' }
    if (-not ((ReadText (Join-Path $repo 'docs/development/roadmap.md')) -match ('(?m)^# .*' + [regex]::Escape($Version)))) { Fail 'Roadmap title is stale' }
}
function Invoke-ReleaseEarlyGates {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Test-ReleaseDocumentation $true
    Run $Python @('scripts/checks/Check-Repository.py','--no-binaries') 'repository, links, bundle layout and delivery set'
    Run $Python @('scripts/checks/Check-DeadToolReferences.py') 'dead tool references'
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/checks/Validate-Bundle.ps1','-Strict','-NoBinaries','-SkipSourceHashes','-PendingRelease',$Version) 'repository bundle validation before build'
    Run $Python @('scripts/checks/Check-BundleLayout.py','--self-test') 'bundle layout / delivery self-tests'
    Run $Python @('scripts/generate/Generate-ToolUsage.py','--check') 'tool usage catalog'
    Run $Python @('scripts/checks/Test-VersionCatalogWiring.py') 'version catalog wiring'
    Run $Python @('scripts/checks/Test-NativeLifecycle.py','--self-test') 'native supervisor safety (no live branch)'
    Run $Python @('scripts/checks/Test-NativeMcpSession.py','--self-test') 'native MCP safety (no live branch)'
    Run $Python @('scripts/checks/Test-DotnetSuites.py','--suite','crash-evidence') 'C# crash evidence suite'
    Run $Python @('scripts/checks/Test-DotnetSuites.py','--suite','write-guard') 'C# write guard suite'
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/checks/Test-DownloadRouteSelection.ps1','-SourceOnly','-PublicApiDirectory',$V21ReferenceRoot,'-Python',$Python) 'download route production source fixture'
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/checks/Test-MatchPlcName.ps1','-SourceOnly','-Python',$Python) 'PLC name production source fixture'
    Say ('Early gates passed; {0:N2}s' -f $timer.Elapsed.TotalSeconds)
}
if ($FunctionsOnly) { return }
if ($SelfTest) {
    $scratch = Join-Path $repo ('bin-build/release-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        $inputPath = Join-Path $scratch 'source.cs'; $binary = Join-Path $scratch 'engine.exe'
        [IO.File]::WriteAllText($inputPath, "source`r`n")
        [IO.File]::WriteAllText($binary, 'binary')
        $sourceRows = @([pscustomobject]@{path='source.cs';sha256=(Get-ReleaseSourceHash $inputPath)})
        $record = [pscustomobject]@{release='4.0.0';fileVersion='4.0.0.0';sourceFiles=$sourceRows;runtimeFiles=@([pscustomobject]@{path='engine.exe';sha256=(Get-FileHash $binary).Hash})}
        [IO.File]::WriteAllText((Join-Path $scratch 'empty.py'), '')
        if ((Get-ReleaseSourceHash (Join-Path $scratch 'empty.py')) -ne 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855') { throw 'Empty source hash failed' }
        $passed = 1
        foreach ($property in @('runtimeFiles','files')) {
            $record | Add-Member -Force NoteProperty $property $record.runtimeFiles
            if (Test-ReleaseReuse $scratch $record '4.0.0' $sourceRows $property) { throw 'Identical build rejected' }; $passed++
            [IO.File]::WriteAllText($inputPath, "source`n")
            if ((Get-ReleaseSourceHash $inputPath) -ne $sourceRows[0].sha256) { throw 'Line ending normalization failed' }; $passed++
            if (-not (Test-ReleaseReuse $scratch $record '4.0.1' $sourceRows $property)) { throw 'Changed version reused' }; $passed++
            $record.fileVersion='4.0.0.1'
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' $sourceRows $property)) { throw 'Changed fileVersion reused' }; $passed++
            $record.fileVersion='4.0.0.0'
            $changed = @([pscustomobject]@{path='source.cs';sha256='changed'})
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' $changed $property)) { throw 'Changed source reused' }; $passed++
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' @() $property)) { throw 'Removed source reused' }; $passed++
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' ($sourceRows + @([pscustomobject]@{path='added.cs';sha256='new'})) $property)) { throw 'Added source reused' }; $passed++
            [IO.File]::WriteAllText($binary, 'changed')
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' $sourceRows $property)) { throw 'Changed binary reused' }; $passed++
            Remove-Item -LiteralPath $binary
            if (-not (Test-ReleaseReuse $scratch $record '4.0.0' $sourceRows $property)) { throw 'Missing binary reused' }; $passed++
            [IO.File]::WriteAllText($binary, 'binary')
        }
        $prior = Join-Path $scratch 'bin-build/releases/v4.0.0'
        New-Item -ItemType Directory -Path $prior -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $prior 'evidence.txt'), 'preserved')
        $archived = Move-PreviousReleaseOutput $scratch '4.0.0'
        if ((Test-Path -LiteralPath $prior) -or (ReadText (Join-Path $archived 'evidence.txt')) -ne 'preserved') { throw 'Archive did not preserve evidence' }; $passed++
        $events=New-Object 'System.Collections.Generic.List[string]'
        Invoke-ReleaseBuildStages {$events.Add('multi-prepare')} {$events.Add('engine')} {$events.Add('multi-complete')}
        if(($events -join ',') -cne 'multi-prepare,engine,multi-complete'){throw 'Wrong release build order'};$passed++
        $events.Clear()
        try {Invoke-ReleaseBuildStages {$events.Add('prepare');throw 'synthetic preparation failure'} {$events.Add('engine')} {$events.Add('complete')}} catch {}
        if(($events -join ',') -cne 'prepare'){throw 'Failed preparation did not stop the chain'};$passed++
        foreach($extension in @('.ttf','.otf','.OTF')) {
            $font=Join-Path $scratch ('font'+$extension)
            [IO.File]::WriteAllBytes($font,[byte[]](255,0,13,10,128))
            if((Get-ReleaseSourceHash $font) -ne (Get-FileHash $font).Hash.ToLowerInvariant()){throw 'Font must be hashed as raw bytes'};$passed++
        }
        $prepared=[pscustomobject]@{release='4.0.0';fileVersion='4.0.0.0';publicApiRoot='local-api';sourceFiles=$sourceRows;validationInputs=$sourceRows;files=$record.runtimeFiles;evidence=$record.runtimeFiles}
        Assert-MultiVersionPreparation $scratch $prepared '4.0.0' $sourceRows $sourceRows $record.runtimeFiles 'local-api';$passed++
        foreach($case in @('source','validation','binary','inventory','api')) {
            $newSources=$sourceRows;$newInputs=$sourceRows;$newFiles=$record.runtimeFiles;$newApi='local-api'
            switch($case) {
                'source' {$newSources=@([pscustomobject]@{path='source.cs';sha256='changed'})}
                'validation' {$newInputs=@([pscustomobject]@{path='source.cs';sha256='changed'})}
                'binary' {[IO.File]::WriteAllText($binary,'changed')}
                'inventory' {$newFiles+=@([pscustomobject]@{path='added.dll';sha256='changed'})}
                'api' {$newApi='other-api'}
            }
            $rejected=$false
            try {Assert-MultiVersionPreparation $scratch $prepared '4.0.0' $newSources $newInputs $newFiles $newApi} catch {$rejected=$true}
            if(!$rejected){throw "Changed preparation accepted: $case"};$passed++
            [IO.File]::WriteAllText($binary,'binary')
        }
        $artifactRows=@(foreach($major in @(20,21)){foreach($name in @("native-call-coverage-v$major.json","tool-usage-v$major.json")){
            $relative="bin-build/releases/v4.0.0/v$major/$name";$path=Join-Path $scratch $relative
            New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
            [IO.File]::WriteAllText($path,'audit')
            [pscustomobject]@{path=$relative;sha256=(Get-FileHash $path).Hash}
        }})
        $audit=[pscustomobject]@{release='4.0.0';validationArtifacts=$artifactRows}
        if(Test-ReleaseAuditEvidence $scratch $audit){throw 'Matching audit evidence rejected'};$passed++
        $archive=Move-PreviousReleaseOutput $scratch '4.0.0'
        if(-not (Test-ReleaseAuditEvidence $scratch $audit)){throw 'Missing audit evidence accepted'};$passed++
        Restore-ArchivedAuditEvidence $scratch $archive $audit
        if(Test-ReleaseAuditEvidence $scratch $audit){throw 'Archived audit evidence was not restored'};$passed++
        [IO.File]::WriteAllText((Join-Path $scratch $artifactRows[0].path),'changed')
        if(-not (Test-ReleaseAuditEvidence $scratch $audit)){throw 'Changed audit evidence accepted'};$passed++
        $rejected=$false;try{Assert-ReleaseRuntimePreparation $scratch}catch{$rejected=$_.Exception.Message -like '*-PrepareOnly -Test first*'}
        if(!$rejected){throw 'Clean checkout did not report preparation prerequisite'};$passed++
        $channelNames=@('TiaMcp.WorkerChannel.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')
        $fixturePaths=@('runtime/studio/TiaOpenness.exe','runtime/studio/TiaMcp.WorkerChannel.dll')
        $fixturePaths+=@($channelNames | ForEach-Object {"runtime/studio/bridge/$_"})
        foreach($key in @('14sp1','15.1','16','17','18','19','20','21')) {
            $fixturePaths+="runtime/studio/bridge/adapters/v$key/TiaOpenness.Openness.dll"
            if($key -notin '20','21') {
                $fixturePaths+=@("runtime/v$key/TiaMcp.FoundationHost.exe","runtime/v$key/TiaMcp.WorkerChannel.dll")
                $fixturePaths+=@($channelNames | ForEach-Object {"runtime/v$key/worker/$_"})
            }
        }
        $fixturePaths+=@('System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll' | ForEach-Object {"runtime/dotnet/shared/Microsoft.NETCore.App/10.0.12/$_"})
        foreach($relative in $fixturePaths) {
            $path=Join-Path $scratch $relative
            New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
            [IO.File]::WriteAllText($path,'fixture')
        }
        Assert-ReleaseRuntimePreparation $scratch;$passed++
        Remove-Item -LiteralPath (Join-Path $scratch 'runtime/dotnet/shared/Microsoft.NETCore.App/10.0.12/System.Text.Json.dll')
        $rejected=$false;try{Assert-ReleaseRuntimePreparation $scratch}catch{$rejected=$_.Exception.Message -like '*Microsoft.NETCore.App*System.Text.Json.dll*'}
        if(!$rejected){throw 'Missing bundled Foundation dependency accepted'};$passed++
        Write-Host "Release reuse/archive self-tests: $passed passed, 0 failed."
    } finally {
        if ((Split-Path ([IO.Path]::GetFullPath($scratch)) -Parent) -ne (Join-Path $repo 'bin-build')) { throw 'Unsafe fixture cleanup' }
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
    return
}
if ($DocumentationOnly) {
    [xml]$props = ReadText (Join-Path $repo 'Version.props')
    $Version = [string]$props.Project.PropertyGroup.TiaMcpRelease
    Test-ReleaseDocumentation $false
    Write-Host 'Release documentation assertions passed.'
    return
}
# Refuse candidate build artifacts before prerequisites, network, or release mutations.
$testPolicyMarker = 'TiaMcpTestPolicy'
$policyRoots = @('runtime', 'src/Engine/bin', 'src/Engine/bin-v20', 'src/FoundationHost/bin', 'src/Worker/bin')
foreach ($policyRoot in $policyRoots) {
    $policyDirectory = Join-Path $repo $policyRoot
    if (-not (Test-Path -LiteralPath $policyDirectory)) { continue }
    foreach ($policyFile in Get-ChildItem -LiteralPath $policyDirectory -Recurse -File | Where-Object { $_.Extension -in '.dll','.exe' }) {
        if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($policyFile.FullName)).Contains($testPolicyMarker)) {
            throw "Test-only behavior policy assembly cannot be released: $($policyFile.FullName)"
        }
    }
}
if (-not $Version) { throw '-Version is required' }
if ($NoReuse -and $SkipBuild) { throw '-NoReuse and -SkipBuild cannot be combined' }
if ($EarlyGatesOnly) {
    if (-not $Python) { $Python = 'python' }
    Invoke-ReleaseEarlyGates
    return
}

# This is the first release action: no log truncation, fetch, bump, move or build until it passes.
$prerequisites = & (Join-Path $PSScriptRoot 'Test-ReleasePrerequisites.ps1') -PublicApiRoot $PublicApiRoot -V20ReferenceRoot $V20ReferenceRoot -V21ReferenceRoot $V21ReferenceRoot -PowerShell7 $PowerShell7 -Python $(if ($Python) { $Python } else { 'python' }) -Git $Git -Token $Token -PassThru
$PublicApiRoot = $prerequisites.PublicApiRoot; $V20ReferenceRoot = $prerequisites.V20ReferenceRoot; $V21ReferenceRoot = $prerequisites.V21ReferenceRoot
$PowerShell7 = $prerequisites.PowerShell7; $Python = $prerequisites.Python; $Git = $prerequisites.Git
$env:TIA_MCP_PLC_TOOLS_PYTHON = $prerequisites.EcosystemPython
function Get-TokenForRelease($OriginalToken, $GitExe) {
    . (Join-Path $PSScriptRoot 'Test-ReleasePrerequisites.ps1') -FunctionsOnly
    Get-ReleaseToken $OriginalToken $GitExe $false
}
$Token = Get-TokenForRelease $Token $Git

"" | Out-File -LiteralPath $log -Encoding UTF8
Say ("Release " + $Version + " (" + $ReleaseDate + ") in " + $repo)

# ---------------------------------------------------------------- 1. preconditions
$branch = (& $Git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') { Fail ("current branch is " + $branch + ", releases go from master") }
& $Git fetch origin --quiet
$behind = (& $Git rev-list --count "master..origin/master").Trim()
if ($behind -ne '0') { Fail ("origin/master has " + $behind + " commit(s) this checkout lacks; pull first") }

$stray = Get-Process -Name 'TiaMcp.Engine.V20','TiaMcp.Engine.V21','TiaMcp.FoundationHost' -ErrorAction SilentlyContinue
if ($stray) {
    if ($KillStrayEngine) { $stray | Stop-Process -Force; Start-Sleep -Seconds 2; Say 'killed stray TiaMcp.Engine.V20.exe / TiaMcp.Engine.V21.exe / TiaMcp.FoundationHost.exe' }
    else { Fail ('TiaMcp.Engine.V20.exe / TiaMcp.Engine.V21.exe / TiaMcp.FoundationHost.exe is running on this host (PID ' + ($stray.Id -join ',') + ') and would lock runtime\v21; stop it or pass -KillStrayEngine') }
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
    $resumeEngineReason = Get-ReleaseReuseReason 'engine'
    $resumeMultiReason = Get-ReleaseReuseReason 'multi'
    if ($resumeEngineReason -or $resumeMultiReason) {
        Say ('Resume needs refreshed build evidence; engine: ' + $resumeEngineReason + '; multi-version: ' + $resumeMultiReason)
        $resumed = $false
    } else {
        Say 'Resume reused both unchanged validated builds; build records retained'
        Invoke-ReleaseEarlyGates
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/checks/Validate-Bundle.ps1','-Strict') 'strict validation of resumed binaries'
    }
}
# Keep only hash-bound audit inputs available after archiving old packages and logs.
$auditRecord = $null
try {
    $candidate = Get-Content -LiteralPath (Join-Path $repo 'manifest/release-build.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if(-not (Test-ReleaseAuditEvidence $repo $candidate)){$auditRecord=$candidate}
} catch { Say 'Earlier full-engine audit evidence is unavailable; rebuild if needed' }
$archivedOutput = Move-PreviousReleaseOutput $repo $Version
if ($archivedOutput) { Say ('Preserved earlier release output: ' + $archivedOutput) }
if($archivedOutput -and $auditRecord -and $auditRecord.release -eq $Version){Restore-ArchivedAuditEvidence $repo $archivedOutput $auditRecord}
if (-not $resumed) {
# ---------------------------------------------------------------- 2. version bump (mechanical places)
$versionProps = Join-Path $repo 'Version.props'
$previous = [regex]::Match((ReadText $versionProps), '<TiaMcpRelease>(\d+\.\d+\.\d+)</TiaMcpRelease>').Groups[1].Value
if (-not $previous) { Fail 'cannot read TiaMcpRelease from Version.props' }
if ($previous -eq $Version) { Say ('version already ' + $Version + ' in Version.props; bump skipped') }
else {
    Say ("bumping " + $previous + " -> " + $Version)
    ReplaceOnce (Join-Path $repo '.claude-plugin\plugin.json') ('"version": "' + $previous + '"') ('"version": "' + $Version + '"') 'plugin.json'
    ReplaceOnce $versionProps ('<TiaMcpRelease>' + $previous + '</TiaMcpRelease>') ('<TiaMcpRelease>' + $Version + '</TiaMcpRelease>') 'Version.props'
    ReplaceOnce (Join-Path $repo 'docs\README.md') ('[当前版本说明](releases/v' + $previous + '.md)') ('[当前版本说明](releases/v' + $Version + '.md)') 'docs/README.md current release link'
    # Capability prose is maintained with the release note; do not rewrite historical entries.
    $roadmap = Join-Path $repo 'docs\development\roadmap.md'
    $rm = ReadText $roadmap
    $title = [regex]::Match($rm, '(?m)^# 路线图与待办（[^）]*，' + [regex]::Escape($previous) + ' 更新）')
    if (-not $title.Success) { $title = [regex]::Match($rm, '(?m)^# 路线图与待办（v' + [regex]::Escape($previous) + '）') }
    if ($title.Success) { WriteText $roadmap ($rm.Replace($title.Value, $title.Value.Replace($previous + ' 更新', $Version + ' 更新').Replace('（v' + $previous + '）', '（v' + $Version + '）'))); Say '  bumped: roadmap title' }
    elseif ($rm.Contains($Version + ' 更新）')) { Say '  already applied: roadmap title' }
    else { Say '  WARNING: roadmap title not bumped (pattern not found) - fix by hand' }
}
foreach ($f in @('docs\reference\capabilities.md', 'docs\development\roadmap.md')) {
    if (-not (ReadText (Join-Path $repo $f)).Contains($Version)) { Say ('  WARNING: ' + $f + ' does not mention ' + $Version + ' - add the section/entry by hand') }
}

# ---------------------------------------------------------------- 3. cheap gates, then independently reusable builds
Invoke-ReleaseEarlyGates
$buildOutput = Join-Path $repo ('bin-build/releases/v' + $Version)
New-Item -ItemType Directory -Force $buildOutput | Out-Null
$buildLog = Join-Path $buildOutput 'build.log'
$engineReason = Get-ReleaseReuseReason 'engine'
$multiReason = Get-ReleaseReuseReason 'multi'
if($SkipBuild -and ($engineReason -or $multiReason)){Fail ('-SkipBuild refused: engine='+$engineReason+'; multi='+$multiReason)}
# A new full-engine build changes files included in the multi-version record.
if($engineReason -and -not $multiReason){$multiReason='full engines will be rebuilt; revalidate the combined inventory'}
Invoke-ReleaseBuildStages {
    if($multiReason) {
        Say ('Preparing Build-MultiVersion: ' + $multiReason)
        Run $PowerShell7 @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Build-MultiVersion.ps1',
            '-PublicApiRoot',$PublicApiRoot,'-Python',$Python,'-PrepareOnly','-Test') 'Build-MultiVersion.ps1 preparation (eight releases and Studio)'
    } else { Say 'Reused Build-MultiVersion preparation: recorded inputs, binaries and validation match' }
} {
# Preparation must not silently alter a reused engine's recorded binaries or inputs.
$engineReason = Get-ReleaseReuseReason 'engine'
if (-not $engineReason) {
    Say 'Reused Build-Release: release/fileVersion, complete source inventory and recorded binary hashes match; validation retained unchanged'
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Prepare-Delivery.ps1','-Release',$Version,'-ReleaseDate',$ReleaseDate) 'refresh delivery/configurator from the unchanged engine record'
} else {
    if ($SkipBuild) { Fail ('-SkipBuild refused: ' + $engineReason) }
    Say ('Rebuilding Build-Release: ' + $engineReason)
    if (Test-Path -LiteralPath $buildLog) { Remove-Item -LiteralPath $buildLog -Force }
    Say 'Build-Release.ps1 (both engines, offline suite, shape checks, configurator, manifests) ...'
    $buildArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\build\Build-Release.ps1'),
        '-V20ReferenceRoot', $V20ReferenceRoot, '-V21ReferenceRoot', $V21ReferenceRoot, '-Python', $Python, '-ReleaseDate', $ReleaseDate)
    # Direct invocation applies PowerShell's Windows PowerShell module-path handling
    # and waits for this script, rather than long-lived compiler server descendants.
    $savedBuildPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & powershell.exe @buildArgs > $buildLog 2> (Join-Path $buildOutput 'build.err.log')
        $buildExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedBuildPreference }
    $built = $false
    if (Test-Path -LiteralPath $buildLog) { $built = (Get-Content -LiteralPath $buildLog -Raw) -match 'Built and checked both runtimes' }
    if ($buildExitCode -ne 0 -or -not $built) {
        $err = ''
        if (Test-Path (Join-Path $buildOutput 'build.err.log')) { $err = (Get-Content (Join-Path $buildOutput 'build.err.log') -Raw) }
        Fail ('Build-Release did not report "Built and checked both runtimes" (exit ' + $buildExitCode + '). Tail of build.err.log: ' + ($err.Substring([Math]::Max(0, $err.Length - 1500))))
    }
    Say 'Build-Release OK'
 }
} {
if ($multiReason) {
    Run $PowerShell7 @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Build-MultiVersion.ps1',
        '-PublicApiRoot',$PublicApiRoot,'-Python',$Python,'-CompleteOnly','-Test') 'Build-MultiVersion.ps1 completion (current full-engine audits and record binding)'
} else {
    $reason=Get-ReleaseReuseReason 'multi'
    if($reason){Fail ('Reused multi-version build changed during delivery preparation: '+$reason)}
    Say 'Reused Build-MultiVersion: release/fileVersion, complete source inventory and recorded binary hashes match; validation retained unchanged'
    # Prepare-Delivery may have refreshed only the engine/configurator binding. Bind the unchanged record, not new test results.
    $deliveryPath = Join-Path $repo 'manifest/delivery.json'
    $delivery = Get-Content -LiteralPath $deliveryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $delivery | Add-Member -Force NoteProperty multiVersionBuildSha256 (Get-FileHash (Join-Path $repo 'manifest/multi-version-build.json')).Hash.ToLowerInvariant()
    $delivery | Add-Member -Force NoteProperty releaseKeys @('14sp1','15.1','16','17','18','19','20','21')
    [IO.File]::WriteAllText($deliveryPath, ($delivery | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
}

# ---------------------------------------------------------------- 4. local gates
$deliveryRecord = Get-Content -LiteralPath (Join-Path $repo 'manifest/delivery.json') -Raw | ConvertFrom-Json
if (-not $deliveryRecord.multiVersionBuildSha256) { Fail 'A tested eight-version build is required for publication; run Build-MultiVersion.ps1 -Test' }
Run $Python @((Join-Path $repo 'scripts\checks\Check-Repository.py')) 'Check-Repository.py'
Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\checks\Validate-Bundle.ps1'), '-Strict') 'Validate-Bundle.ps1 -Strict'

# ---------------------------------------------------------------- 5. commit + package
# stage tracked changes plus new files under the known source/doc roots only - never "git add -A" at the repo root;
# the binaries are ignored by .gitignore since 2.8.1, so they can never end up in the commit
$changedPaths = @(& $Git diff --name-only) + @(& $Git ls-files --others --exclude-standard -- docs src tests third_party build-tools plugin scripts templates hooks manifest reference .claude-plugin .github)
if ($DryRun) {
    Say ('DryRun passed; would commit ' + $changedPaths.Count + ' file(s); commit/package/push/tag/upload skipped')
    $changedPaths | Sort-Object -Unique | ForEach-Object { Say ('    ' + $_) }
    exit 0
}
if ($changedPaths.Count) { & $Git add -- @changedPaths }
$staged = @(& $Git diff --cached --name-only)
$binariesStaged = @($staged | Where-Object { ($_ -like 'runtime/*' -and $_ -ne 'runtime/README.md') -or $_ -eq 'TiaOpenness.exe' })
if ($binariesStaged) { & $Git reset -q; Fail ('binaries must not be committed (2.8.1 policy): ' + ($binariesStaged -join ', ')) }
$others = (& $Git status --porcelain) | Where-Object { $_ -match '^\?\?' }
if ($others) { Say ("untracked files left out (add them by hand if they belong to the release): " + (($others | ForEach-Object { $_.Substring(3) }) -join ', ')) }
Say ("the release commit will contain " + $staged.Count + " file(s)")
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
Run $Python @((Join-Path $repo 'scripts\build\Package-Release.py'), '--git', $gitExe) 'Package-Release.py'
Run $Python @((Join-Path $repo 'scripts\checks\Verify-ReleaseAsset.py'), (Join-Path $pkgDir ($pkgName + '.zip')), '--git', $gitExe) 'Verify-ReleaseAsset.py (ZIP = tree + recorded binaries)'
if ($NoPush) { Say 'stopped before push (-NoPush)'; exit 0 }

# ---------------------------------------------------------------- 6. push + CI (GitHub API with the release token)
if (-not $Token) { $Token = $env:GITHUB_TOKEN }
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
    # the workflow sets run-name "Verify vX.Y.Z", which is what the runs API reports as the name
    Say 'waiting for "Verify published release" (release event on the tag commit)'
    WaitWorkflows @('Verify ' + $tag) $head
}
Say ('DONE ' + $tag + '. Now record the publication in docs/development/handoff.md and handoff-checklist.md and commit that separately.')
