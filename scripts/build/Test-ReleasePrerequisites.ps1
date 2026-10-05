#Requires -Version 5.1
<#
.SYNOPSIS
    Check release prerequisites before changing the tree. No build or TIA connection.
.DESCRIPTION
    Offline only accepts cached, hash-verified archives and an explicitly supplied/environment
    token; it never invokes a credential helper or downloads. SelfTest uses synthetic probes.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$PublicApiRoot = '',
    [string]$V20ReferenceRoot = '',
    [string]$V21ReferenceRoot = '',
    [string]$Dotnet = 'dotnet',
    [string]$PowerShell7 = 'pwsh',
    [string]$Python = 'python',
    [string]$EcosystemPython = $env:TIA_MCP_PLC_TOOLS_PYTHON,
    [string]$Git = 'git',
    [string]$Token = $env:GITHUB_TOKEN,
    [ValidateRange(1,1024)][int]$MinimumFreeGB = 10,
    [switch]$Offline,
    [switch]$SelfTest,
    [switch]$PassThru,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'
function Resolve-ReleaseCommand([string]$Name) {
    $command = Get-Command $Name -CommandType Application -ErrorAction Stop | Select-Object -First 1
    if ($command.Source -like '*WindowsApps*') { throw 'Windows app execution aliases are not supported' }
    return $command.Source
}
function Get-ReleaseToken([string]$Value, [string]$GitExe, [bool]$LocalOnly) {
    if ($Value) { return $Value }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    if ($LocalOnly) { return '' }
    # Capture both streams, never print helper output or the returned secret. Disable prompting.
    $oldPrompt = $env:GIT_TERMINAL_PROMPT; $oldGcm = $env:GCM_INTERACTIVE
    $oldPreference = $ErrorActionPreference
    $requestPath = Join-Path $env:TEMP ('release-credential-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'Never'
        # Keep the existing byte-oriented request: PS 5.1's stdin encoding can otherwise
        # make git report "missing protocol field". The temporary file contains no secret.
        [IO.File]::WriteAllBytes($requestPath, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
        $credentialGit = Resolve-ReleaseCommand $GitExe
        $ErrorActionPreference = 'Continue'
        $response = (& cmd.exe /d /c ('type "' + $requestPath + '" | "' + $credentialGit + '" credential fill 2>nul')) -join "`n"
        if ($LASTEXITCODE -eq 0 -and $response -match '(?m)^password=(.+)$') { return $Matches[1].Trim() }
        return ''
    } finally {
        $env:GIT_TERMINAL_PROMPT = $oldPrompt; $env:GCM_INTERACTIVE = $oldGcm; $ErrorActionPreference = $oldPreference
        if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath -Force }
    }
}
function Invoke-PrerequisiteChecks($Probes) {
    foreach ($probe in $Probes) {
        try {
            # Per-probe values travel in Data: closures from GetNewClosure() cannot see this script's helpers
            # when Release.ps1 invokes the script with '&'.
            $detail = & $probe.Check $probe.Data
            [pscustomobject]@{Check=$probe.Name; Result='PASS'; Detail=[string]$detail}
        } catch {
            # Probes provide safe diagnostics; the token probe never includes helper output.
            [pscustomobject]@{Check=$probe.Name; Result='FAIL'; Detail=$_.Exception.Message}
        }
    }
}
# Files Release.ps1 itself rewrites (version bump and build records). An earlier run that stopped after the build
# leaves them modified; rerunning must stay possible so the validated builds can be reused.
function Test-ReleaseManagedChange([string]$Line) {
    if ($Line.Length -lt 4 -or $Line.Substring(0, 2) -ne ' M') { return $false }
    $path = $Line.Substring(3).Trim().Trim('"').Replace('\', '/')
    return ($path -in @('Version.props', '.claude-plugin/plugin.json', 'docs/README.md', 'docs/development/roadmap.md', 'docs/reference/tool-matrix.md')) -or
        ($path -match '^manifest/[^/]+\.json$')
}
function Assert-PrerequisiteValue([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Invoke-PrerequisiteCommand([string]$Program, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $Program @Arguments 2>&1)
        return [pscustomobject]@{ExitCode=$LASTEXITCODE;Output=$output}
    } finally { $ErrorActionPreference = $saved }
}
function New-ReleaseSourceFixture([string]$Root, [string]$Kind, [string]$Directory, [string]$Api, [string]$PythonExe, [string]$DotnetExe) {
    # Compile only the production methods exercised by the existing deterministic tests.
    # The lexical extractor fails on missing/ambiguous members; no copied algorithm or native stub.
    $extract = @'
import sys
from pathlib import Path
root,kind,out=Path(sys.argv[1]),sys.argv[2],Path(sys.argv[3])
sys.path.insert(0,str(root/'scripts/checks'))
from engine_sources import EngineSources
engine=EngineSources(root)
head='using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Reflection; '
if kind=='match':
    path=root/'src/Logic/Siemens/Guard.cs'
    engine.sources[path]=path.read_text(encoding='utf-8-sig')
    text=head+'namespace TiaMcpServer.Siemens { internal static class Guard {'+engine.member('MatchPlcName',owner='Guard')+'}}'
else:
    names=['FindSubnetOrGatewayAddress','TryCreateTargetAddress','EnumerateDownloadRoutes','ReadReflectedParent','ReadReflectedInt','ReadConfigurationAddresses','SameIpv4Subnet24','ScoreDownloadRoutes','DescribeRoutes','SelectDownloadRoute']
    members='\n'.join(engine.member(n,owner='OnlineDownloadService') for n in names)
    types='\n'.join('private sealed '+engine.type_text(n) for n in ['DownloadRoute','DownloadRouteSelection'])
    helpers='\n'.join(engine.member(n,owner='Portal').replace('private static','public') for n in ['ReadReflectedString','EnumerateReflectedProperty'])
    text=head+'using Siemens.Engineering.Connection; namespace TiaMcpServer.Siemens { public class Portal {'+helpers+'} internal static class EngineeringGroupOperations {'+engine.member('Items',owner='EngineeringGroupOperations')+'} } namespace TiaMcpServer.Siemens.Services { public class OnlineDownloadService { private readonly Portal _session; public OnlineDownloadService(Portal session) { _session=session; }'+types+members+'}}'
out.write_text(text,encoding='utf-8')
'@
    $sourcePath = Join-Path $Directory 'Fixture.cs'
    & $PythonExe -c $extract $Root $Kind $sourcePath
    if ($LASTEXITCODE) { throw 'Production source fixture extraction failed' }
    $sdkLine = @(& $DotnetExe --list-sdks | Where-Object { $_ -match '^10\.\d+\.\d+' }) | Select-Object -Last 1
    if ($sdkLine -notmatch '^(\S+) \[(.+)\]') { throw '.NET 10 SDK compiler is required' }
    $compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
    $framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
    if (-not (Test-Path -LiteralPath $framework)) { throw '.NET Framework 4.8 targeting pack is required' }
    $fixture = Join-Path $Directory 'TiaMcp.Engine.V21.exe'
    $compilerArgs = @('/nologo','/target:library','/nullable:enable','/langversion:latest','/nostdlib+',('/out:"'+$fixture+'"'),('"'+$sourcePath+'"'))
    $compilerArgs += Get-ChildItem -LiteralPath $framework -Filter '*.dll' -File | Where-Object { $_.Name -notin 'System.EnterpriseServices.Wrapper.dll','System.EnterpriseServices.Thunk.dll' } | ForEach-Object { '/reference:"'+$_.FullName+'"' }
    if ($Kind -eq 'route') {
        $compilerArgs += Get-ChildItem -LiteralPath $Api -Filter 'Siemens.Engineering*.dll' -File | ForEach-Object { '/reference:"'+$_.FullName+'"' }
    }
    $response = Join-Path $Directory 'compile.rsp'
    [IO.File]::WriteAllLines($response, $compilerArgs, [Text.UTF8Encoding]::new($false))
    & $DotnetExe $compiler "@$response"
    if ($LASTEXITCODE) { throw 'Production source fixture compilation failed' }
}
if ($FunctionsOnly) { return }
if ($SelfTest) {
    $cases = @(
        @{Name='SDK accepted'; Check={Assert-PrerequisiteValue ('10.0.100 [sdk]' -match '(?m)^10\.\d+\.\d+') 'SDK missing'}},
        @{Name='SDK rejected'; Check={Assert-PrerequisiteValue ('9.0.100 [sdk]' -match '(?m)^10\.\d+\.\d+') 'SDK missing'}},
        @{Name='Python accepted'; Check={Assert-PrerequisiteValue ([version]'3.12.0' -ge [version]'3.11') 'Python too old'}},
        @{Name='Python rejected'; Check={Assert-PrerequisiteValue ([version]'3.10.0' -ge [version]'3.11') 'Python too old'}},
        @{Name='Token absent'; Check={Assert-PrerequisiteValue $false 'GitHub token unavailable'}},
        @{Name='Dirty tree'; Check={Assert-PrerequisiteValue (-not ' M tracked.ps1') 'Working tree is not clean'}},
        @{Name='Disk full'; Check={Assert-PrerequisiteValue (1GB -ge 10GB) 'Insufficient free disk space'}},
        @{Name='Bad archive'; Check={Assert-PrerequisiteValue ('bad' -eq 'pinned') 'Archive SHA-512 mismatch'}},
        @{Name='Last probe still runs'; Check={'all probes evaluated'}},
        @{Name='Probe data A'; Data=@{Value='A'}; Check={param($d) Assert-PrerequisiteValue $true 'helper visible'; $d.Value}},
        @{Name='Probe data B'; Data=@{Value='B'}; Check={param($d) Assert-PrerequisiteValue $true 'helper visible'; $d.Value}}
    )
    $rows = @(Invoke-PrerequisiteChecks $cases)
    $expected = @('PASS','FAIL','PASS','FAIL','FAIL','FAIL','FAIL','FAIL','PASS','PASS','PASS')
    if ($rows[9].Detail -ne 'A' -or $rows[10].Detail -ne 'B') { throw 'Probe data was not bound per probe' }
    $managedCases = @{' M manifest/release-build.json'=$true; ' M Version.props'=$true; ' M scripts/build/Release.ps1'=$false; '?? manifest/new.json'=$false; ' M manifest/contracts/baseline/21.json'=$false}
    foreach ($case in $managedCases.GetEnumerator()) { if ((Test-ReleaseManagedChange $case.Key) -ne $case.Value) { throw ('Release-managed filter failed: ' + $case.Key) } }
    for ($i=0; $i -lt $expected.Count; $i++) {
        if ($rows[$i].Result -ne $expected[$i]) { throw "Prerequisite self-test failed: $($cases[$i].Name)" }
    }
    $shell = Join-Path $PSHOME $(if ($PSVersionTable.PSVersion.Major -ge 7) { 'pwsh.exe' } else { 'powershell.exe' })
    foreach ($exitCode in @(0,7)) {
        $result = Invoke-PrerequisiteCommand $shell @('-NoProfile','-Command',("[Console]::Error.WriteLine('harmless warning'); exit " + $exitCode))
        if ($result.ExitCode -ne $exitCode) { throw 'Native stderr/exit-code handling failed' }
    }
    Write-Host "Prerequisite self-tests: $($expected.Count+2+$managedCases.Count) passed, 0 failed."
    return
}
if (-not $RepoRoot) { $RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$settings = @{}
$probes = @(
    @{Name='.NET 10 SDK'; Check={
        $settings.Dotnet = Resolve-ReleaseCommand $Dotnet
        $result = Invoke-PrerequisiteCommand $settings.Dotnet @('--list-sdks')
        $sdks = $result.Output -join "`n"
        Assert-PrerequisiteValue ($result.ExitCode -eq 0 -and $sdks -match '(?m)^10\.\d+\.\d+') '.NET 10 SDK is required'
        $settings.Dotnet
    }},
    @{Name='PowerShell 7'; Check={
        $settings.PowerShell7 = Resolve-ReleaseCommand $PowerShell7
        $result = Invoke-PrerequisiteCommand $settings.PowerShell7 @('-NoProfile','-NonInteractive','-Command','$PSVersionTable.PSVersion.Major')
        Assert-PrerequisiteValue ($result.ExitCode -eq 0 -and [int]($result.Output -join '') -ge 7) 'PowerShell 7 is required'
        $settings.PowerShell7
    }},
    @{Name='Python >= 3.12'; Check={
        $settings.Python = Resolve-ReleaseCommand $Python
        $result = Invoke-PrerequisiteCommand $settings.Python @('-B','-c','import sys; sys.exit(0 if sys.version_info >= (3,12) else 1)')
        Assert-PrerequisiteValue ($result.ExitCode -eq 0) 'Python >= 3.12 is required by PLC Tools'
        $settings.Python
    }},
    @{Name='Ecosystem Python'; Check={
        $candidate = $EcosystemPython
        if (-not $candidate) { $candidate = Join-Path $RepoRoot 'TiaMcp_Output/ecosystem-python/Scripts/python.exe' }
        $settings.EcosystemPython = Resolve-ReleaseCommand $candidate
        $bridge = Join-Path $RepoRoot 'scripts/ecosystem/plc_tools_bridge.py'
        $code = "import runpy,sys; assert sys.version_info >= (3,12); import reportlab; m=runpy.run_path(sys.argv[1]); group,errors=m['load_groups'](); assert not errors, errors; assert len(m['catalog'](group)) >= 49"
        $result = Invoke-PrerequisiteCommand $settings.EcosystemPython @('-B','-c',$code,$bridge)
        Assert-PrerequisiteValue ($result.ExitCode -eq 0) 'Ecosystem catalogue/dependencies unavailable; set TIA_MCP_PLC_TOOLS_PYTHON'
        $settings.EcosystemPython
    }},
    @{Name='Git / clean tree'; Check={
        $settings.Git = Resolve-ReleaseCommand $Git
        $result = Invoke-PrerequisiteCommand $settings.Git @('-C',$RepoRoot,'status','--porcelain','--untracked-files=normal')
        Assert-PrerequisiteValue ($result.ExitCode -eq 0) 'git status failed'
        $managed = @($result.Output | Where-Object { Test-ReleaseManagedChange $_ })
        $other = @($result.Output | Where-Object { -not (Test-ReleaseManagedChange $_) })
        Assert-PrerequisiteValue ($other.Count -eq 0) ('Working tree must be clean apart from release-managed files; commit or revert: ' + (($other | Select-Object -First 5) -join ', '))
        if ($managed.Count) { 'clean apart from ' + $managed.Count + ' release-managed file(s) from an earlier run' } else { 'clean' }
    }},
    @{Name='GitHub token'; Check={
        $availableToken = Get-ReleaseToken $Token $Git ([bool]$Offline)
        Assert-PrerequisiteValue (-not [string]::IsNullOrWhiteSpace($availableToken)) 'GitHub token unavailable (value never printed)'
        'available (value hidden)'
    }},
    @{Name='Free disk space'; Check={
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($RepoRoot))
        Assert-PrerequisiteValue ($drive.AvailableFreeSpace -ge ($MinimumFreeGB * 1GB)) "At least $MinimumFreeGB GiB free space is required"
        '{0:N1} GiB free; minimum {1} GiB' -f ($drive.AvailableFreeSpace / 1GB), $MinimumFreeGB
    }}
)
if (-not $PublicApiRoot) {
    if ($V20ReferenceRoot) { $PublicApiRoot = Split-Path (Split-Path $V20ReferenceRoot -Parent) -Parent }
    elseif (Test-Path -LiteralPath (Join-Path $RepoRoot 'sdk')) { $PublicApiRoot = Join-Path $RepoRoot 'sdk' }
    elseif (Test-Path -LiteralPath (Join-Path $RepoRoot 'TIA_V21_PublicAPI')) { $PublicApiRoot = $RepoRoot }
    else { $PublicApiRoot = Split-Path $RepoRoot -Parent }
}
$settings.PublicApiRoot = [IO.Path]::GetFullPath($PublicApiRoot)
foreach ($key in @('14sp1','15.1','16','17','18','19','20','21')) {
    $relative = switch ($key) {
        '14sp1' { 'TIA_V14SP1_PublicAPI/V14 SP1' }
        '21' { 'TIA_V21_PublicAPI/V21/net48' }
        default { "TIA_V${key}_PublicAPI/V$key" }
    }
    $canonical = Join-Path $settings.PublicApiRoot $relative
    $directory = $canonical
    if ($key -eq '20' -and $V20ReferenceRoot) { $directory = $V20ReferenceRoot }
    if ($key -eq '21' -and $V21ReferenceRoot) { $directory = $V21ReferenceRoot }
    if ($key -eq '20') { $settings.V20ReferenceRoot = $directory }
    if ($key -eq '21') { $settings.V21ReferenceRoot = $directory }
    $assemblies = if ($key -eq '21') { @('Siemens.Engineering.Base.dll','Siemens.Engineering.Step7.dll') } else { @('Siemens.Engineering.dll') }
    $probes += @{Name="PublicAPI $key"; Data=@{Directory=$directory; Canonical=$canonical; Assemblies=$assemblies}; Check={
        param($d)
        foreach ($assembly in $d.Assemblies) {
            Assert-PrerequisiteValue (Test-Path -LiteralPath (Join-Path $d.Directory $assembly) -PathType Leaf) "Missing $assembly in $($d.Directory)"
            Assert-PrerequisiteValue (Test-Path -LiteralPath (Join-Path $d.Canonical $assembly) -PathType Leaf) "Multi-version build needs $assembly in $($d.Canonical)"
            if ([IO.Path]::GetFullPath($d.Canonical) -ne [IO.Path]::GetFullPath($d.Directory)) {
                Assert-PrerequisiteValue ((Get-FileHash (Join-Path $d.Canonical $assembly)).Hash -eq (Get-FileHash (Join-Path $d.Directory $assembly)).Hash) 'Full-engine and multi-version PublicAPI inputs differ'
            }
        }
        $d.Directory
    }}
}
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bundled-dotnet.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($archive in $pin.archives) {
    $cache = Join-Path $RepoRoot "bin-build/cache/dotnet-$($pin.version)"
    $file = Join-Path $cache $archive.name
    $probes += @{Name=$archive.name; Data=@{Archive=$archive; Cache=$cache; File=$file; Offline=[bool]$Offline}; Check={
        param($d)
        $archive = $d.Archive; $cache = $d.Cache; $file = $d.File; $Offline = $d.Offline
        Assert-PrerequisiteValue ($archive.url -match '^https://builds\.dotnet\.microsoft\.com/dotnet/') 'Archive must use the pinned official Microsoft URL'
        if (-not (Test-Path -LiteralPath $file)) {
            Assert-PrerequisiteValue (-not $Offline) "Archive not cached (offline): $file"
            New-Item -ItemType Directory -Force $cache | Out-Null
            $partial = "$file.$([guid]::NewGuid().ToString('N')).partial"
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $archive.url -OutFile $partial -UseBasicParsing -TimeoutSec 180
                Assert-PrerequisiteValue ((Get-FileHash -LiteralPath $partial -Algorithm SHA512).Hash -eq $archive.sha512) 'Downloaded archive SHA-512 mismatch'
                Move-Item -LiteralPath $partial -Destination $file
            } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
        }
        Assert-PrerequisiteValue ((Get-FileHash -LiteralPath $file -Algorithm SHA512).Hash -eq $archive.sha512) "Cached archive SHA-512 mismatch: $file"
        'cached; SHA-512 verified'
    }}
}
$timer = [Diagnostics.Stopwatch]::StartNew()
$results = @(Invoke-PrerequisiteChecks $probes)
$results | Format-Table Check,Result,Detail -Wrap -AutoSize | Out-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ('Release prerequisites: {0} passed, {1} failed; {2:N2}s' -f ($results.Count-$failed), $failed, $timer.Elapsed.TotalSeconds)
if ($failed) { throw 'Release prerequisite check failed; no build started' }
if ($PassThru) { return [pscustomobject]$settings }
