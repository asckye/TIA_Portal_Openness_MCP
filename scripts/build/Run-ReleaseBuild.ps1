#Requires -Version 5.1
<#
.SYNOPSIS
  Offline reviewer chain: prepare, build, complete, package, validate, then compare V4 snapshots.
.DESCRIPTION
  No Git writes or publication. Every step restores the original tracked release records byte for byte.
  Prepare local SDKs, NuGet packages, pinned runtime archives and the companion Python environment first.
#>
param(
    [string]$PublicApiRoot='',
    [string]$OutputDirectory='',
    [string]$NuGetConfig='',
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$CompanionPython='',
    [switch]$DryRun,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# Isolated host roots live outside the repository: tests that create Git repositories or look for a source checkout must
# not find this one. The outermost script fixes the root; nested scripts inherit it through the environment.
if(-not $env:TIA_MCP_RELEASE_TEMP_ROOT){$env:TIA_MCP_RELEASE_TEMP_ROOT=Join-Path ([IO.Path]::GetTempPath()) ('tmr-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$releaseTempRoot=[IO.Path]::GetFullPath($env:TIA_MCP_RELEASE_TEMP_ROOT)
# Build servers started under an isolated TEMP would outlive the check and lock files there.
$env:UseSharedCompilation='false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
if($releaseTempRoot.StartsWith(([IO.Path]::GetFullPath($repo).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw "Release temp root must be outside the repository: $releaseTempRoot"}
function Get-BundleDirectory([string]$PackagePath) {
    if([IO.Path]::GetExtension($PackagePath) -ne '.zip'){throw 'Package result must name a ZIP'}
    # ChangeExtension(path,$null) receives '' from PowerShell and leaves a trailing dot.
    Join-Path (Split-Path -Parent $PackagePath) ([IO.Path]::GetFileNameWithoutExtension($PackagePath))
}
function Assert-StepOrder([string[]]$Names) {
    $expected='00-preflight,01-multi-version,02-build-release,03-package-local,04-validate-bundle,05-prompt-registration,06-v4-contracts-capture,07-v4-contracts-compare,08-v4-responses-capture,09-v4-responses-compare'
    if(($Names -join ',') -cne $expected){throw 'Reviewer chain order changed; preparation must precede full engines and snapshots must follow binary validation'}
}
function Assert-OfflineNuGet([string]$Path) {
    [xml]$config=Get-Content -LiteralPath $Path -Raw
    if(!$config.SelectSingleNode('/configuration/packageSources/clear')){throw 'NuGetConfig must clear inherited package feeds'}
    foreach($source in $config.SelectNodes('/configuration/packageSources/add')) {
        $value=[string]$source.value
        if(!$value -or $value -match '^[a-z][a-z0-9+.-]*:' -and $value -notmatch '^[a-z]:[\\/]' -or $value.StartsWith('\\') -or $value.StartsWith('//')){throw "Network NuGet feed is forbidden: $value"}
    }
}
function Run-Command([string]$Exe,[string[]]$Arguments,[switch]$ProductDefaults) {
    $null=Get-Command $Exe -ErrorAction Stop
    $tempBase=Join-Path $releaseTempRoot 'rc'
    # Short ids keep nested Git fixtures below MAX_PATH; check.txt names the command.
    $hostRoot=Join-Path $tempBase ([guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Force $hostRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $hostRoot 'check.txt'),((@($Exe)+$Arguments) -join ' '))
    New-Item -ItemType Directory -Force -Path (Join-Path $hostRoot 'config'),(Join-Path $hostRoot 'temp'),(Join-Path $hostRoot 'local-app-data'),(Join-Path $hostRoot 'app-data') | Out-Null
    # Host checks run without the Workbench, so approvals are off for them; snapshot captures keep the product
    # defaults because the V4 baselines record what a fresh installation answers.
    if(!$ProductDefaults){[IO.File]::WriteAllText((Join-Path $hostRoot 'config/approval.settings'),"enabled=false`ntimeoutSeconds=120`n",[Text.UTF8Encoding]::new($false))}
    $savedData=$env:TIA_MCP_DATA_DIRECTORY;$savedDiagnostics=$env:TIA_MCP_DIAGNOSTICS_DIRECTORY
    $savedLocalAppData=$env:LOCALAPPDATA;$savedAppData=$env:APPDATA;$savedTemp=$env:TEMP;$savedTmp=$env:TMP
    $savedPreference=$ErrorActionPreference
    $completed=$false;$code=$null
    try {
        $env:TIA_MCP_DATA_DIRECTORY=$hostRoot
        $env:TIA_MCP_DIAGNOSTICS_DIRECTORY=Join-Path $hostRoot 'diagnostics'
        $env:LOCALAPPDATA=Join-Path $hostRoot 'local-app-data'
        $env:APPDATA=Join-Path $hostRoot 'app-data'
        $env:TEMP=Join-Path $hostRoot 'temp';$env:TMP=$env:TEMP
        $ErrorActionPreference='Continue'
        & $Exe @Arguments 2>&1
        $code=$LASTEXITCODE
        $completed=($code -eq 0)
    } finally {
        $ErrorActionPreference=$savedPreference
        $env:TIA_MCP_DATA_DIRECTORY=$savedData;$env:TIA_MCP_DIAGNOSTICS_DIRECTORY=$savedDiagnostics
        $env:LOCALAPPDATA=$savedLocalAppData;$env:APPDATA=$savedAppData;$env:TEMP=$savedTemp;$env:TMP=$savedTmp
        if($completed){try{Remove-Item -LiteralPath $hostRoot -Recurse -Force -ErrorAction Stop}catch{Write-Warning ("release check data still in use (kept): "+$hostRoot)}}
        else{Write-Output "Retained failed release check data: $hostRoot"}
    }
    if($code -ne 0){throw "$Exe exited $code"}
}
function Restore-Records {
    foreach($relative in $backedUp) {
        Copy-Item -LiteralPath (Join-Path $original $relative) -Destination (Join-Path $repo $relative) -Force
        if((Get-FileHash -LiteralPath (Join-Path $repo $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $original $relative)).Hash){throw "Release record was not restored: $relative"}
    }
}
function Run-Step([string]$Name,[scriptblock]$Action) {
    $log=Join-Path $logs "$Name.log"
    Write-Output "START $Name; $log"
    try {
        foreach($relative in $backedUp){Copy-Item -LiteralPath (Join-Path $state $relative) -Destination (Join-Path $repo $relative) -Force}
        & $Action *> $log
        foreach($relative in $backedUp){Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination (Join-Path $state $relative) -Force}
        Write-Output "PASS $Name; $log"
    } catch {
        Add-Content -LiteralPath $log -Value $_.Exception.Message -Encoding UTF8
        throw "$Name failed; see $log; $($_.Exception.Message)"
    } finally {Restore-Records}
}
$common=@('-Dotnet',$Dotnet,'-Python',$Python)
$harness=Join-Path $repo 'tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe'
$steps=@(
    # Build-free release checks first: minutes instead of an hour-long run per problem, all failures at once.
    @{label='00-preflight';action={Run-Command 'pwsh' @('-NoProfile','-File','scripts/build/Test-ReleasePreflight.ps1','-Python',$Python)}},
    @{label='01-multi-version';action={
        Run-Command 'powershell.exe' (@('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Build-MultiVersion.ps1','-PublicApiRoot',$api,'-PrepareOnly','-Offline','-Test')+$common)
    }},
    @{label='02-build-release';action={
        Run-Command 'powershell.exe' (@('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Build-Release.ps1','-V20ReferenceRoot',(Join-Path $api 'TIA_V20_PublicAPI/V20'),'-V21ReferenceRoot',(Join-Path $api 'TIA_V21_PublicAPI/V21/net48'))+$common)
        Run-Command 'powershell.exe' (@('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/build/Build-MultiVersion.ps1','-PublicApiRoot',$api,'-CompleteOnly','-Offline','-Test')+$common)
    }},
    @{label='03-package-local';action={Run-Command $Python @('scripts/build/Package-Release.py','--local','--output-directory',$output)}},
    @{label='04-validate-bundle';action={
        $package=Get-Content -LiteralPath (Join-Path $output 'package-result.json') -Raw | ConvertFrom-Json
        $bundle=Get-BundleDirectory ([string]$package.path)
        if($bundle.EndsWith('.')){throw 'Bundle directory must not have a trailing dot'}
        Run-Command 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/checks/Validate-Bundle.ps1','-Strict','-BundleRoot',$bundle,'-PackageMode')
    }},
    @{label='05-prompt-registration';action={Run-Command $Python @('scripts/checks/Test-DotnetSuites.py','--suite','prompt-registration','--dotnet',$Dotnet,'--results-directory',(Join-Path $logs 'dotnet-suites'))}},
    @{label='06-v4-contracts-capture';action={Run-Command $Python (@('scripts/checks/Snapshot-ToolContracts.py','capture')+$snapshots+@('--output',(Join-Path $logs 'contracts'))) -ProductDefaults}},
    @{label='07-v4-contracts-compare';action={Run-Command $Python @('scripts/checks/Snapshot-ToolContracts.py','compare','--baseline','manifest/contracts/v4/baseline','--current',(Join-Path $logs 'contracts'),'--releases','20','21')}},
    @{label='08-v4-responses-capture';action={Run-Command $Python (@('scripts/checks/Snapshot-ToolResponses.py','capture')+$snapshots+@('--output',(Join-Path $logs 'responses'),'--temp-root',$logs)) -ProductDefaults}},
    @{label='09-v4-responses-compare';action={Run-Command $Python @('scripts/checks/Snapshot-ToolResponses.py','compare','--baseline','manifest/contracts/v4/responses','--current',(Join-Path $logs 'responses'),'--releases','20','21')}
    }
)
Assert-StepOrder @($steps.label)
if($SelfTest) {
    $passed=0
    Assert-StepOrder @($steps.label);$passed++
    $wrong=@($steps.label);$wrong[1]='02-build-release';$wrong[2]='01-multi-version'
    $rejected=$false;try{Assert-StepOrder $wrong}catch{$rejected=$true}
    if(!$rejected){throw 'Wrong build order accepted'};$passed++
    foreach($name in @('delivery.zip','delivery.v4.zip','delivery with spaces.zip')) {
        $zip=Join-Path $repo $name
        $bundle=Get-BundleDirectory $zip
        if($bundle.EndsWith('.') -or $bundle -ne (Join-Path $repo ([IO.Path]::GetFileNameWithoutExtension($zip)))){throw 'Trailing-dot bundle regression'};$passed++
    }
    # Exercise record restoration for success, PowerShell failure and native exit failure.
    $scratch=Join-Path $repo ('bin-build/reviewer-selftest-'+[guid]::NewGuid().ToString('N'))
    $actualRepo=$repo
    try {
        $repo=$scratch;$logs=Join-Path $scratch 'logs';$original=Join-Path $scratch 'original';$state=Join-Path $scratch 'state'
        New-Item -ItemType Directory -Path $scratch,$logs,$original,$state | Out-Null
        $backedUp=@('record.json')
        foreach($directory in @($scratch,$original,$state)){[IO.File]::WriteAllBytes((Join-Path $directory 'record.json'),[byte[]](0,13,10,255))}
        foreach($mode in @('success','throw','native')) {
            $failed=$false
            $failure=''
            try {Run-Step $mode {
                [IO.File]::WriteAllText((Join-Path $repo 'record.json'),'candidate')
                if($mode -eq 'throw'){throw 'synthetic failure'}
                if($mode -eq 'native'){Run-Command 'powershell.exe' @('-NoProfile','-Command','exit 7')}
            }} catch {$failed=$true;$failure=$_.Exception.Message}
            if($failed -ne ($mode -ne 'success')){throw 'Wrong step failure status'}
            if($mode -eq 'native' -and $failure -notlike '*exited 7*'){throw 'Native exit code was lost'}
            if((Get-FileHash (Join-Path $repo 'record.json')).Hash -ne (Get-FileHash (Join-Path $original 'record.json')).Hash){throw 'Record restoration failed'};$passed++
        }
        Run-Command 'powershell.exe' @('-NoProfile','-Command','exit 0');$passed++
        # Host checks get approvals off; snapshot captures see the product defaults (no approval settings file).
        $probe='if(Test-Path (Join-Path $env:TIA_MCP_DATA_DIRECTORY ''config/approval.settings'')){exit 3}'
        Run-Command 'powershell.exe' @('-NoProfile','-Command',$probe) -ProductDefaults;$passed++
        $rejected=$false;try{Run-Command 'powershell.exe' @('-NoProfile','-Command',$probe)}catch{$rejected=$_.Exception.Message -like '*exited 3*'}
        if(!$rejected){throw 'Host checks did not get approvals off'};$passed++
        $config=Join-Path $scratch 'nuget.config'
        foreach($source in @('https://example.invalid/feed','file://server/feed','//server/feed','\\server\feed')) {
            [IO.File]::WriteAllText($config,"<configuration><packageSources><clear/><add key='test' value='$source'/></packageSources></configuration>")
            $rejected=$false;try{Assert-OfflineNuGet $config}catch{$rejected=$true}
            if(!$rejected){throw 'Network feed accepted'};$passed++
        }
        Write-Output "Reviewer chain self-tests: $passed passed, 0 failed."
    } finally {
        $repo=$actualRepo
        if((Split-Path ([IO.Path]::GetFullPath($scratch)) -Parent) -ne (Join-Path $repo 'bin-build')){throw 'Unsafe reviewer fixture cleanup'}
        if(Test-Path -LiteralPath $scratch){Remove-Item -LiteralPath $scratch -Recurse -Force}
    }
    return
}
if($DryRun) {
    foreach($step in $steps){Write-Output "DRY $($step.label)"}
    Write-Output '02: full engines -> multi-version completion; 04: ZIP parent + filename without extension (no trailing dot).'
    Write-Output 'Real run: local SDK/NuGet/runtime caches required; tracked records backed up and restored after every step, including failure.'
    return
}
if(!$PublicApiRoot -or !$OutputDirectory){throw '-PublicApiRoot and -OutputDirectory are required unless -DryRun or -SelfTest is used'}
$api=(Resolve-Path -LiteralPath $PublicApiRoot).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Choose a new OutputDirectory; existing packages are never overwritten'}
if($output.StartsWith($repo+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and !$output.StartsWith((Join-Path $repo 'bin-build')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Repository output must be under bin-build'}
$logs=Join-Path $repo ('bin-build/release-review/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
$original=Join-Path $logs 'original-records';$state=Join-Path $logs 'build-records'
New-Item -ItemType Directory -Path $logs,$original,$state | Out-Null
$backedUp=@()
$oldPython=$env:TIA_MCP_PLC_TOOLS_PYTHON;$oldRestoreConfig=$env:RestoreConfigFile;$oldNuGetAudit=$env:NuGetAudit
Push-Location $repo
try {
    if(!$CompanionPython){$CompanionPython=$env:TIA_MCP_PLC_TOOLS_PYTHON}
    if(!$CompanionPython){throw 'Prepare a companion Python environment and pass -CompanionPython (or set TIA_MCP_PLC_TOOLS_PYTHON)'}
    $env:TIA_MCP_PLC_TOOLS_PYTHON=(Resolve-Path -LiteralPath $CompanionPython).Path
    if(!$NuGetConfig) {
        $NuGetConfig=Join-Path $logs 'offline-nuget.config'
        [IO.File]::WriteAllText($NuGetConfig,'<configuration><packageSources><clear /></packageSources></configuration>',[Text.UTF8Encoding]::new($false))
    }
    $NuGetConfig=(Resolve-Path -LiteralPath $NuGetConfig).Path
    Assert-OfflineNuGet $NuGetConfig
    $env:RestoreConfigFile=$NuGetConfig;$env:NuGetAudit='false'
    $common+=@('-NuGetConfig',$NuGetConfig)
    $pin=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bundled-dotnet.json') -Raw | ConvertFrom-Json
    foreach($archive in $pin.archives) {
        $cached=Join-Path $repo "bin-build/cache/dotnet-$($pin.version)/$($archive.name)"
        if(!(Test-Path -LiteralPath $cached)){throw "Prepare the pinned runtime cache before this offline proof: $cached"}
        if((Get-FileHash -LiteralPath $cached -Algorithm SHA512).Hash -ne $archive.sha512){throw "Pinned runtime cache hash mismatch: $cached"}
    }
    $records=@(& git ls-files -- 'manifest/*' 'docs/reference/version-tool-catalog.md' 'docs/reference/tool-matrix.md')
    if($LASTEXITCODE -ne 0 -or !$records){throw 'Cannot enumerate tracked release records'}
    foreach($relative in $records) {
        foreach($directory in @($original,$state)) {
            $target=Join-Path $directory $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
        }
        $backedUp+=$relative
    }
    # V4 snapshots run the real engine EXEs (P6-54): the readiness gate answers on a machine without TIA.
    $snapshots=@('--repo-root',$repo,'--public-api-root',$api,'--releases','20','21',
        '--exe',"20=$repo/runtime/v20/TiaMcp.Engine.V20.exe",'--exe',"21=$repo/runtime/v21/TiaMcp.Engine.V21.exe")
    foreach($step in $steps){Run-Step $step.label $step.action}
    $package=Get-Content -LiteralPath (Join-Path $output 'package-result.json') -Raw | ConvertFrom-Json
    Write-Output "COMPLETE: bundle=$(Get-BundleDirectory ([string]$package.path)); logs=$logs; all release records restored"
} catch {
    Add-Content -LiteralPath (Join-Path $logs 'failure.log') -Value $_.Exception.Message -Encoding UTF8
    throw
} finally {
    try {Restore-Records} finally {
        $env:TIA_MCP_PLC_TOOLS_PYTHON=$oldPython;$env:RestoreConfigFile=$oldRestoreConfig;$env:NuGetAudit=$oldNuGetAudit
        Pop-Location
    }
}
