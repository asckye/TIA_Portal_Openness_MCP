param(
    [string]$V20ReferenceRoot,
    [string]$V21ReferenceRoot,
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$NuGetConfig='',
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate=(Get-Date -Format 'yyyyMMdd'),
    [switch]$NoRestore,
    [ValidateSet(0,20,21)][int]$PipelineMajor=0,
    [switch]$SelfTest,
    [ValidateRange(10,10000)][int]$LocalStabilityRounds=50
)
$ErrorActionPreference='Stop'

$expectedCheckCounts=[ordered]@{
    diagnosticBehavior=@{value=36;mode='min'}
    diagnosticRejection=@{value=5;mode='min'}
    nativeMcpSafety=@{value=8;mode='min'}
    nativeJournalReader=@{value=3;mode='min'}
    adapterJournal=@{value=8;mode='min'}
    processLeases=@{value=2;mode='min'}
    workerFaults=@{value=25;mode='min'}
    workerProtocol=@{value=58;mode='min'}
    approvalSafety=@{value=4;mode='exact'}
    softwareLookup=@{value=45;mode='min'}
    engineeringApiV20=@{value=2840;mode='min'}
    engineeringApiV21=@{value=3126;mode='min'}
    v21Ecosystem=@{value=75;mode='min'}
    # V21 bridge verification enumerates the eight fixed native script API signatures.
    globalScriptV21=@{value=8;mode='exact'}
    graphicSelection=@{value=8;mode='min'}
    runtimeSettingsV20=@{value=8;mode='min'}
    runtimeSettingsV21=@{value=9;mode='min'}
    ecosystem=@{value=31;mode='min'}
}
function Assert-CheckCount([string]$Key,$Actual,[string]$Message) {
    $expected=$expectedCheckCounts[$Key]
    if($null -eq $expected){throw "Unknown check-count gate: $Key"}
    if($null -eq $Actual -or
        ($expected.mode -eq 'min' -and $Actual -lt $expected.value) -or
        ($expected.mode -eq 'exact' -and $Actual -ne $expected.value)){
        $reportedActual=if($null -eq $Actual){'missing'}else{$Actual}
        throw "$Message; expected $($expected.mode) $($expected.value), actual $reportedActual"
    }
}
function Assert-MatchedCheckCount([string]$Key,[System.Text.RegularExpressions.Match]$Match,[string]$Message) {
    $actual=if($Match.Success){[int]$Match.Groups[1].Value}else{$null}
    Assert-CheckCount $Key $actual $Message
}
function Get-WorkerIsolationDefault([string]$Path) {
    $assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom([IO.Path]::GetFullPath($Path))
    $rows=@($assembly.GetCustomAttributesData() | Where-Object {
        $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and
        $_.ConstructorArguments.Count -eq 2 -and $_.ConstructorArguments[0].Value -eq 'TiaMcpWorkerIsolationDefault'
    })
    if($rows.Count -ne 1){throw "Worker isolation default metadata missing or duplicated: $Path"}
    $value=[string]$rows[0].ConstructorArguments[1].Value
    if($value -notin 'true','false'){throw "Invalid worker isolation default metadata '$value': $Path"}
    return ($value -eq 'true')
}
function Wait-VersionPipelines($Jobs,[string]$Output) {
    $null=$Jobs | Wait-Job
    $failed=@()
    foreach($job in $Jobs) {
        $messages=@(Receive-Job $job -ErrorAction Continue 2>&1)
        $messages | Out-File -LiteralPath (Join-Path $Output ($job.Name+'.log')) -Encoding utf8
        Write-Host "$($job.Name): $($job.State); log: $Output/$($job.Name).log"
        if($job.State -ne 'Completed'){$failed+=$job.Name}
    }
    if($failed.Count){throw ('Version pipelines failed: '+($failed -join ', ')+'; both logs retained')}
}

$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# Isolated host roots live outside the repository: tests that create Git repositories or look for a source checkout must
# not find this one. The outermost script fixes the root; nested scripts inherit it through the environment.
if(-not $env:TIA_MCP_RELEASE_TEMP_ROOT){$env:TIA_MCP_RELEASE_TEMP_ROOT=Join-Path ([IO.Path]::GetTempPath()) ('tmr-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$releaseTempRoot=[IO.Path]::GetFullPath($env:TIA_MCP_RELEASE_TEMP_ROOT)
# Build servers started under an isolated TEMP would outlive the check and lock files there.
$env:UseSharedCompilation='false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
if($releaseTempRoot.StartsWith(([IO.Path]::GetFullPath($repo).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw "Release temp root must be outside the repository: $releaseTempRoot"}
if($SelfTest){
    $scratch=Join-Path $repo ('bin-build/parallel-selftest-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $scratch | Out-Null
    $passed=0
    try {
        foreach($failKeys in @('', '20', '20,21')) {
            $case=Join-Path $scratch ('case-'+[guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory $case | Out-Null
            $jobs=@()
            try {
                foreach($major in @(20,21)) {
                    $jobs+=Start-Job -Name "release-v$major" -ScriptBlock {
                        param($Directory,$Major,$FailKeys)
                        [IO.File]::WriteAllText((Join-Path $Directory "$Major.ready"),'ready')
                        $other=if($Major -eq 20){21}else{20}
                        $deadline=(Get-Date).AddSeconds(15)
                        while(!(Test-Path -LiteralPath (Join-Path $Directory "$other.ready"))) {
                            if((Get-Date) -gt $deadline){throw 'Pipelines did not overlap'}
                            Start-Sleep -Milliseconds 50
                        }
                        if("$Major" -in ($FailKeys -split ',')){throw "Synthetic V$Major failure"}
                        "V$Major passed"
                    } -ArgumentList $case,$major,$failKeys
                }
                $failure=''
                try {Wait-VersionPipelines $jobs $case} catch {$failure=$_.Exception.Message}
                if([bool]$failure -ne [bool]$failKeys){throw 'Pipeline aggregate accepted the wrong result'}
                foreach($key in ($failKeys -split ',' | Where-Object {$_})) {if($failure -notlike "*release-v$key*"){throw 'Aggregate omitted a failing version'}}
                $passed++
                foreach($major in @(20,21)) {
                    $contents=Get-Content (Join-Path $case "release-v$major.log") -Raw
                    if(!$contents -or $contents -like '*did not overlap*'){throw 'Separate pipeline evidence missing or pipelines ran serially'}
                    $passed++
                }
            } finally {foreach($job in $jobs){if($job.State -eq 'Running'){Stop-Job $job};Remove-Job $job -Force}}
        }
        Write-Host "Parallel pipeline self-tests: $passed passed, 0 failed."
    } finally {
        if((Split-Path ([IO.Path]::GetFullPath($scratch)) -Parent) -ne (Join-Path $repo 'bin-build')){throw 'Unsafe fixture cleanup'}
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
    return
}
if(-not $V20ReferenceRoot -or -not $V21ReferenceRoot){throw '-V20ReferenceRoot and -V21ReferenceRoot are required'}
$source=Join-Path $repo 'src/Engine'
[xml]$versionXml=Get-Content (Join-Path $repo 'Version.props') -Raw
$release=[string]$versionXml.Project.PropertyGroup.TiaMcpRelease
$version=$release + '.0'
if($release -notmatch '^\d+\.\d+\.\d+$'){throw 'Public release version must be X.Y.Z without fork or feature suffixes'}
if(-not $PipelineMajor) {
    function Assert-DeliveryPreparation {
        . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
        Assert-ReleaseRuntimePreparation $repo
    }
    Assert-DeliveryPreparation
}
$null=[DateTime]::ParseExact($ReleaseDate,'yyyyMMdd',[Globalization.CultureInfo]::InvariantCulture)
$package="TIA_MCP_Delivery_v${release}_$ReleaseDate"
$out=Join-Path $repo "bin-build/releases/v$release"
New-Item -ItemType Directory -Force $out | Out-Null
$sharedOut=$out
if($PipelineMajor){$out=Join-Path $sharedOut "v$PipelineMajor"; New-Item -ItemType Directory -Force $out | Out-Null}
$env:DOTNET_CLI_HOME=Join-Path $out 'dotnet-home'
$env:MSBUILDDISABLENODEREUSE='1'
$env:TEMP=Join-Path $out 'temp'; $env:TMP=$env:TEMP
$env:TIA_MCP_DIAGNOSTICS_DIRECTORY=Join-Path $out 'diagnostics'
New-Item -ItemType Directory -Force $env:TEMP,$env:TIA_MCP_DIAGNOSTICS_DIRECTORY | Out-Null
function Read-ReleaseInputs([switch]$Validation) {
    . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
    if ($Validation) { @(Get-ReleaseValidationInputs $repo 'engine') } else { @(Get-ReleaseSources $repo 'engine') }
}
if(-not $PipelineMajor){$sourceFiles=@(Read-ReleaseInputs);$validationInputs=@(Read-ReleaseInputs -Validation)}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
function Run([string]$Program,[string[]]$Arguments,[string]$Log,[bool]$ApprovalEnabled=$false) {
    if($Program -eq $Dotnet -and $Arguments[0] -in 'build','restore'){$Arguments+=@('-nodeReuse:false','-p:UseSharedCompilation=false')}
    $dataBase=Join-Path $releaseTempRoot 'br'
    $safeName=[IO.Path]::GetFileNameWithoutExtension($Log) -replace '[^A-Za-z0-9._-]','_'
    # Short ids keep nested paths below MAX_PATH; check.txt names the step.
    $dataRoot=Join-Path $dataBase ([guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Force $dataRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $dataRoot 'check.txt'),$safeName)
    New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'config'),(Join-Path $dataRoot 'temp'),(Join-Path $dataRoot 'local-app-data'),(Join-Path $dataRoot 'app-data') | Out-Null
    [IO.File]::WriteAllText((Join-Path $dataRoot 'config/approval.settings'),('enabled='+$ApprovalEnabled.ToString().ToLowerInvariant()+"`ntimeoutSeconds=120`n"),[Text.UTF8Encoding]::new($false))
    $savedData=$env:TIA_MCP_DATA_DIRECTORY;$savedDiagnostics=$env:TIA_MCP_DIAGNOSTICS_DIRECTORY
    $savedLocalAppData=$env:LOCALAPPDATA;$savedAppData=$env:APPDATA;$savedTemp=$env:TEMP;$savedTmp=$env:TMP
    $completed=$false
    # Windows PowerShell wraps native stderr as ErrorRecord even for warnings.
    $savedPreference=$ErrorActionPreference
    try {
        $env:TIA_MCP_DATA_DIRECTORY=$dataRoot
        $env:TIA_MCP_DIAGNOSTICS_DIRECTORY=Join-Path $dataRoot 'diagnostics'
        $env:LOCALAPPDATA=Join-Path $dataRoot 'local-app-data'
        $env:APPDATA=Join-Path $dataRoot 'app-data'
        $env:TEMP=Join-Path $dataRoot 'temp';$env:TMP=$env:TEMP
        $ErrorActionPreference='Continue'
        & $Program @Arguments > (Join-Path $out $Log) 2>&1
        $exitCode=$LASTEXITCODE
        $completed=($exitCode -eq 0)
    } finally {
        $ErrorActionPreference=$savedPreference
        $env:TIA_MCP_DATA_DIRECTORY=$savedData;$env:TIA_MCP_DIAGNOSTICS_DIRECTORY=$savedDiagnostics
        $env:LOCALAPPDATA=$savedLocalAppData;$env:APPDATA=$savedAppData;$env:TEMP=$savedTemp;$env:TMP=$savedTmp
        if($completed){try{Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction Stop}catch{Write-Warning ("release check data still in use (kept): "+$dataRoot)}}
        else{Write-Host "Retained failed release check data ($safeName): $dataRoot"}
    }
    if($exitCode){throw "$Program failed ($exitCode); see $out/$Log"}
}
function Restore([string]$Project,[string[]]$Properties) {
    if($NoRestore){return}
    $argsList=@('restore',$Project,'-v:q')+$Properties
    if($NuGetConfig){$argsList+=@('--configfile',(Resolve-Path -LiteralPath $NuGetConfig).Path)}
    Run $Dotnet $argsList ('restore-'+[IO.Path]::GetFileName($Project)+'.log')
}
function Run-DotnetSuite([string]$Name,[string]$Log) {
    $results=Join-Path $out 'dotnet-suites'
    Run $Python @((Join-Path $repo 'scripts/checks/Test-DotnetSuites.py'),'--suite',$Name,'--dotnet',$Dotnet,'--no-restore','--results-directory',$results) $Log
    $summary=Get-Content (Join-Path $results "$Name.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    return [int]$summary.passed
}
if(-not $PipelineMajor) {
Run $Python @((Join-Path $repo 'scripts/checks/Test-ReleaseApprovalGate.py'),'--self-test') 'approval-gate-self-test.log'
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeLifecycle.py'),'--self-test') 'native-supervisor.log'
$nativeSupervisor=[regex]::Match((Get-Content (Join-Path $out 'native-supervisor.log') -Raw),'COMPLETE: (\d+) native supervisor checks passed; live TIA tests NOT RUN')
if(!$nativeSupervisor.Success){throw 'Native supervisor offline checks incomplete'}
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeMcpSession.py'),'--self-test') 'native-mcp-safety.log'
$nativeMcpSafety=[regex]::Match((Get-Content (Join-Path $out 'native-mcp-safety.log') -Raw),'COMPLETE: (\d+) native MCP safety checks passed')
Assert-MatchedCheckCount 'nativeMcpSafety' $nativeMcpSafety 'Native MCP safety checks incomplete'
$shippedToolsProject=Join-Path $repo 'tests/Tools/TiaMcp.ShippedTools.Tests/TiaMcp.ShippedTools.Tests.csproj'
Restore $shippedToolsProject @()
$guiTestsProject=Join-Path $repo 'tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj'
Restore $guiTestsProject @()
$writeGuardPassed=Run-DotnetSuite 'write-guard' 'write-guard.log'
$crashEvidencePassed=Run-DotnetSuite 'crash-evidence' 'crash-evidence-tests.log'
$writeGuardProject=Join-Path $repo 'src/Tools/WriteGuard/TiaMcp.WriteGuard.csproj'
Run $Dotnet @('publish',$writeGuardProject,'-c','Release','-o',(Join-Path $repo 'runtime/tools'),'--no-restore','-p:PublishAot=false','-p:UseAppHost=true','-p:UseSharedCompilation=false') 'write-guard-publish.log'
Run $Python @((Join-Path $repo 'scripts/generate/Generate-ToolUsage.py'),'--check') 'tool-usage-catalog.log'
Run $Python @((Join-Path $repo 'scripts/checks/Test-VersionCatalogWiring.py')) 'version-catalog-wiring.log'
$offline=Join-Path $repo 'tests/Engine/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj'
Restore $offline @()
$offlinePassed=Run-DotnetSuite 'offline' 'offline.log'
# Exercise admission and bridge refusal with both compiled identities, not only
# the default V21 symbol. These are offline tests, never native TIA calls.
$offlineV20Passed=Run-DotnetSuite 'offline-v20' 'offline-v20.log'
$versionPolicyProject=Join-Path $repo 'tests/Engine/TiaMcpServer.VersionPolicyTests/TiaMcpServer.VersionPolicyTests.csproj'
Restore $versionPolicyProject @()
$versionPolicySdkPassed=Run-DotnetSuite 'version-policy' 'version-policy-sdk.log'
$updaterSuiteProject=Join-Path $repo 'tests/Updater/TiaMcp.Updater.Tests.csproj'
Restore $updaterSuiteProject @()
$updaterPassed=Run-DotnetSuite 'updater' 'updater.log'
$updaterProject=Join-Path $repo 'src/Updater/TiaMcp.Updater.csproj'
Restore $updaterProject @()
Run $Dotnet @('build',$updaterProject,'-c','Release','-f','net48','--no-restore','-v:q') 'build-updater.log'
$updaterExe=Join-Path $repo 'bin-build/updater/TiaMcp.Updater.exe'
$updaterConfig=Join-Path $repo 'bin-build/updater/TiaMcp.Updater.exe.config'
if(!(Test-Path -LiteralPath $updaterExe) -or !(Test-Path -LiteralPath $updaterConfig)){throw 'The .NET Framework updater output is incomplete.'}
$harnessProject=Join-Path $repo 'tests/Engine/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj'
Restore $harnessProject @()
Run $Dotnet @('build',$harnessProject,'-c','Release','--no-restore','-v:q') 'build-harness.log'
$harness=Join-Path (Split-Path $harnessProject) 'bin/Release/net48/HttpTests.exe'
$weaverProject=Join-Path $repo 'build-tools/native-call-weaver/NativeCallWeaver.csproj'
Restore $weaverProject @()
Run $Dotnet @('build',$weaverProject,'-c','Release','--no-restore','-v:q') 'native-weaver-build.log'
$weaver=Join-Path $repo 'build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll'
$verifierDirectory=Join-Path $repo 'runtime/verification'
New-Item -ItemType Directory -Force $verifierDirectory | Out-Null
foreach ($name in @('NativeCallWeaver.dll','NativeCallWeaver.deps.json','NativeCallWeaver.runtimeconfig.json','Mono.Cecil.dll')) {
    Copy-Item -LiteralPath (Join-Path (Split-Path $weaver) $name) -Destination $verifierDirectory -Force
}
$packagedWeaver=Join-Path $verifierDirectory 'NativeCallWeaver.dll'
$diagnosticProject=Join-Path $repo 'tests/Engine/TiaMcpServer.DiagnosticsTests/DiagnosticsTests.csproj'
Restore $diagnosticProject @()
Run $Dotnet @('build',$diagnosticProject,'-c','Release','--no-restore','-v:q') 'native-diagnostics-build.log'
$diagnosticFixture=Join-Path (Split-Path $diagnosticProject) 'bin/Release/net48/DiagnosticsTests.exe'
$diagnosticOut=Join-Path $out ('native-diagnostics-'+[Guid]::NewGuid().ToString('N'))
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeDiagnostics.py'),'--fixture',$diagnosticFixture,'--weaver',$weaver,'--output',$diagnosticOut) 'native-diagnostics-tests.log'
$diagnosticTests=Get-Content (Join-Path $diagnosticOut 'result.json') -Raw | ConvertFrom-Json
Assert-CheckCount 'diagnosticBehavior' $diagnosticTests.behaviorChecks 'Native diagnostic fixture behavior checks incomplete'
Assert-CheckCount 'diagnosticRejection' $diagnosticTests.rejectionChecks 'Native diagnostic fixture rejection checks incomplete'
if($diagnosticTests.nativeTiaExecuted){throw 'Native diagnostic fixture gate incomplete'}
# Compile the separate opt-in native harness, but execute ONLY its offline safety
# checks here. Live TIA creation belongs to a dedicated, explicitly enabled run.
$common=@{diagnosticTests=$diagnosticTests;crashEvidenceChecksPassed=$crashEvidencePassed;writeGuardChecksPassed=$writeGuardPassed}
[IO.File]::WriteAllText((Join-Path $sharedOut 'common.json'),($common|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
} else {
    $harness=Join-Path $repo 'tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe'
    $weaver=Join-Path $repo 'build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll'
    $verifierDirectory=Join-Path $repo 'runtime/verification'
    $packagedWeaver=Join-Path $verifierDirectory 'NativeCallWeaver.dll'
    $diagnosticTests=(Get-Content (Join-Path $sharedOut 'common.json') -Raw | ConvertFrom-Json).diagnosticTests
    $nativeSupervisor=[regex]::Match((Get-Content (Join-Path $sharedOut 'native-supervisor.log') -Raw),'COMPLETE: (\d+) native supervisor checks passed; live TIA tests NOT RUN')
    $nativeMcpSafety=[regex]::Match((Get-Content (Join-Path $sharedOut 'native-mcp-safety.log') -Raw),'COMPLETE: (\d+) native MCP safety checks passed')
    $crashEvidencePassed=[int](Get-Content (Join-Path $sharedOut 'common.json') -Raw | ConvertFrom-Json).crashEvidenceChecksPassed
}
$checks=[ordered]@{}
if($PipelineMajor) {
foreach($major in @($PipelineMajor)) {
    $api=(Resolve-Path -LiteralPath $(if($major -eq 20){$V20ReferenceRoot}else{$V21ReferenceRoot})).Path
    $project=Join-Path $source $(if($major -eq 20){'TiaMcpServer.V20.csproj'}else{'TiaMcpServer.V21.csproj'})
    $obj=Join-Path $source $(if($major -eq 20){'obj-v20/'}else{'obj/'})
    $properties=@("-p:SiemensEngineeringDirectory=$api")
    $nativeProject=Join-Path $repo "tests/Engine/TiaMcpServer.NativeTests/V$major/NativeTests.V$major.csproj"
    # Both graphs write Logic/Runtime/contracts/third-party obj/bin. Hold a worktree-specific
    # mutex through restore, compile and payload copy; all subsequent gates run concurrently.
    $mutexHash=[Security.Cryptography.SHA256]::Create()
    try{$mutexName='Local\TIA-Release-'+[BitConverter]::ToString($mutexHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Replace('-','')}finally{$mutexHash.Dispose()}
    $buildMutex=[Threading.Mutex]::new($false,$mutexName)
    $locked=$false
    try {
    try {$locked=$buildMutex.WaitOne()} catch [Threading.AbandonedMutexException] {$locked=$true}
    Restore $nativeProject $properties
    Run $Dotnet (@('build',$nativeProject,'-c','Release','--no-restore','-v:q')+$properties) "native-build-v$major.log"
    $nativeOutput=Join-Path (Split-Path $nativeProject) 'bin/Release/net48'
    $nativeExe=Join-Path $nativeOutput "NativeTests.V$major.exe"
    if(@(Get-ChildItem -LiteralPath $nativeOutput -Filter 'Siemens.Engineering*.dll' -File).Count){throw 'Native test harness must not copy Siemens assemblies locally'}
    Run $nativeExe @('--self-test') "native-safety-v$major.log"
    $nativeSafety=[regex]::Match((Get-Content (Join-Path $out "native-safety-v$major.log") -Raw),'COMPLETE: (\d+) native harness safety checks passed; live TIA tests NOT RUN')
    if(!$nativeSafety.Success){throw 'Native harness offline safety checks incomplete'}
    Restore $project $properties
    Run $Dotnet (@('build',$project,'-c','Release','--no-restore','-v:q')+$properties) "build-v$major.log"
    $built=Join-Path $source $(if($major -eq 20){'bin-v20/Release/net48'}else{'bin/Release/net48'})
    $runtime=Join-Path $repo "runtime/v$major"
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $payload=@(Get-ChildItem -LiteralPath $built -File | Where-Object {
        $_.Extension -in '.exe','.dll','.config' -and $_.Name -notlike 'Siemens.Engineering*' -and
        ($_.Extension -ne '.exe' -or $_.Name -ceq "TiaMcp.Engine.V$major.exe") -and
        ($_.Extension -ne '.config' -or $_.Name -ceq "TiaMcp.Engine.V$major.exe.config")
    })
    if ('TiaMcp.Runtime.dll' -notin $payload.Name) { throw "V$major runtime channel assembly missing from build output" }
    foreach ($name in @("TiaMcp.Adapter.$major.dll",'TiaMcp.Adapters.Contracts.dll')) {
        if ($name -notin $payload.Name) { throw "V$major shared adapter dependency missing: $name" }
    }
    if (@($payload | Where-Object { $_.Name -like 'TiaMcp.Adapter.*.dll' }).Count -ne 1) { throw "V$major must ship exactly its own shared adapter" }
    # Fail on obsolete dependencies so an old DLL is never silently republished.
    foreach($file in Get-ChildItem -LiteralPath $runtime -File | Where-Object {$_.Extension -in '.exe','.dll','.config'}){if($file.Name -notin $payload.Name){throw "Review obsolete runtime file: $($file.FullName)"}}
    $payload | Copy-Item -Destination $runtime -Force
    } finally {if($locked){$buildMutex.ReleaseMutex()};$buildMutex.Dispose()}
    $exe=Join-Path $runtime "TiaMcp.Engine.V$major.exe"
    if((Get-Item $exe).VersionInfo.FileVersion -ne $version){throw "V$major runtime version mismatch"}
    Run $harness @($exe,'example-library-only') "example-library-v$major.log"
    $coveragePath=Join-Path $out "native-call-coverage-v$major.json"
    Run $Dotnet @($weaver,'verify',$exe,$coveragePath) "native-coverage-v$major.log"
    $coverage=Get-Content $coveragePath -Raw | ConvertFrom-Json
    $adapterCoveragePath=Join-Path $out "adapter-native-call-coverage-v$major.json"
    Run $Dotnet @($packagedWeaver,'verify',(Join-Path $runtime "TiaMcp.Adapter.$major.dll"),$adapterCoveragePath) "adapter-native-coverage-v$major.log"
    $adapterCoverage=Get-Content $adapterCoveragePath -Raw | ConvertFrom-Json
    Run $harness @($exe,'native-diagnostics-only',$api) "native-jit-v$major.log"
    $nativeJit=[regex]::Match((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw),'COMPLETE: (\d+) native diagnostic wrappers JIT prepared; (\d+) open generic wrappers')
    if(!$nativeJit.Success -or ([int]$nativeJit.Groups[1].Value+[int]$nativeJit.Groups[2].Value) -ne $coverage.count){throw 'Diagnostic wrapper JIT/inventory mismatch'}
    $adapterJit=[regex]::Match((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw),'COMPLETE: (\d+) adapter native diagnostic wrappers JIT prepared; (\d+) open generic wrappers')
    if(!$adapterJit.Success -or ([int]$adapterJit.Groups[1].Value+[int]$adapterJit.Groups[2].Value) -ne $adapterCoverage.count){throw 'Adapter diagnostic wrapper JIT/inventory mismatch'}
    $adapterJournal=[regex]::Match((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw),'COMPLETE: (\d+) adapter integration diagnostic checks passed')
    Assert-MatchedCheckCount 'adapterJournal' $adapterJournal 'Shared adapter journal checks incomplete'
    $nativeJournalReader=[regex]::Match((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw),'COMPLETE: (\d+) native journal reader checks passed')
    Assert-MatchedCheckCount 'nativeJournalReader' $nativeJournalReader 'Native journal reader checks incomplete'
    Run $harness @($exe,'process-leases-only') "process-leases-v$major.log"
    $processLeases=[regex]::Match((Get-Content (Join-Path $out "process-leases-v$major.log") -Raw),'COMPLETE: (\d+) process lease checks passed')
    Assert-MatchedCheckCount 'processLeases' $processLeases 'Cross-process lease checks incomplete'
    Run $harness @($exe,'worker-supervisor-only') "worker-supervisor-v$major.log"
    $workerFaults=[regex]::Match((Get-Content (Join-Path $out "worker-supervisor-v$major.log") -Raw),'COMPLETE: (\d+) worker supervisor checks passed; no TIA connection attempted')
    Assert-MatchedCheckCount 'workerFaults' $workerFaults 'Worker supervisor fault checks incomplete'
    Run $Python @((Join-Path $repo 'scripts/checks/Test-WorkerIsolation.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api) "worker-protocol-v$major.log"
    $workerProtocol=[regex]::Match((Get-Content (Join-Path $out "worker-protocol-v$major.log") -Raw),'COMPLETE: (\d+) isolated MCP checks passed; no TIA connection attempted')
    Assert-MatchedCheckCount 'workerProtocol' $workerProtocol 'Isolated MCP protocol checks incomplete'
    $approvalOut=Join-Path $out ("approval-default-v$major-"+[guid]::NewGuid().ToString('N'))
    Run $Python @((Join-Path $repo 'scripts/checks/Test-ReleaseApprovalGate.py'),'--product','engine','--major',"$major",'--exe',$exe,'--portal-root',$api,'--host-harness',$harness,'--public-api',$api,'--temp-root',$env:TEMP,'--output',$approvalOut) "approval-safety-v$major.log" $true
    $approvalResult=Get-Content (Join-Path $approvalOut 'result.json') -Raw | ConvertFrom-Json
    Assert-CheckCount 'approvalSafety' $approvalResult.checksPassed "V$major default-approval checks incomplete"
    if($approvalResult.status -ne 'passed' -or $approvalResult.checksExpected -ne 4 -or $approvalResult.workbenchConnected -ne $false -or $approvalResult.tiaConnected -ne $false -or
        $approvalResult.results.direct -ne 'refused-before-dispatch; read-succeeded' -or $approvalResult.results.CallTool -ne 'refused-before-dispatch') {throw "V$major default-approval gate result is invalid"}
    Run $harness @($exe,'software-lookup-only',$api) "software-lookup-v$major.log"
    $softwareLookup=[regex]::Match((Get-Content (Join-Path $out "software-lookup-v$major.log") -Raw),'COMPLETE: (\d+) software lookup checks passed')
    Assert-MatchedCheckCount 'softwareLookup' $softwareLookup 'Software lookup/listing validation did not report complete success'
    Run $harness @($exe,'engineering-api-only',$api) "engineering-api-v$major.log"
    $engineeringApi=[regex]::Match((Get-Content (Join-Path $out "engineering-api-v$major.log") -Raw),'COMPLETE: (\d+) engineering API checks passed')
    Assert-MatchedCheckCount "engineeringApiV$major" $engineeringApi 'Engineering API compatibility checks incomplete'
    Run $harness @($exe) "http-v$major.log"
    Run $harness @($exe,'hmi-only',"$major",$version) "hmi-v$major.log"
    $http=[regex]::Match((Get-Content (Join-Path $out "http-v$major.log") -Raw),'COMPLETE: (\d+) passed')
    $hmi=[regex]::Match((Get-Content (Join-Path $out "hmi-v$major.log") -Raw),'(\d+) HMI traversal assertions, 0 failed')
    if(!$http.Success -or !$hmi.Success){throw 'Runtime regression did not report complete success'}
    # Exercise the actual host methods with SDK dispatch and transports, without
    # changing this machine's Openness group or connecting to a TIA process.
    Run $Python @((Join-Path $repo 'scripts/checks/Test-ResourceDiscovery.py'),'--exe',$exe,'--portal-root',$api,'--major',"$major",'--host-harness',$harness,'--public-api',$api,'--usage-output',(Join-Path $out "tool-usage-v$major.json")) "resources-v$major.log"
    $resources=[regex]::Match((Get-Content (Join-Path $out "resources-v$major.log") -Raw),'COMPLETE: (\d+) resource discovery checks passed')
    if(!$resources.Success){throw 'Resource discovery validation did not report complete success'}
    # V21 document adapters are offline on both runtimes; use the supplied V21 schemas.
    $v21Schemas=Join-Path (Split-Path (Resolve-Path -LiteralPath $V21ReferenceRoot).Path -Parent) 'Schemas'
    $v21EcosystemOut=Join-Path $out ("v21-ecosystem-v$major-"+[Guid]::NewGuid().ToString('N'))
    $ecosystemPython=if($env:TIA_MCP_PLC_TOOLS_PYTHON){$env:TIA_MCP_PLC_TOOLS_PYTHON}else{$Python}
    Run $ecosystemPython @((Join-Path $repo 'scripts/checks/Test-V21Ecosystem.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api,'--schema-root',$v21Schemas,'--output',$v21EcosystemOut) "v21-ecosystem-v$major.log"
    $v21EcosystemFiles=@(Get-ChildItem -LiteralPath $v21EcosystemOut -Filter result.json -Recurse -File)
    if($v21EcosystemFiles.Count -ne 1){throw 'V21 ecosystem evidence missing or ambiguous'}
    $v21Ecosystem=Get-Content -LiteralPath $v21EcosystemFiles[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-CheckCount 'v21Ecosystem' $v21Ecosystem.checks 'V21 ecosystem adapter checks incomplete'
    if($v21Ecosystem.status -ne 'passed' -or $v21Ecosystem.selfTestOnly -or $v21Ecosystem.nativeTiaExecuted -or $v21Ecosystem.runtimeSha256 -ne (Get-FileHash $exe).Hash.ToLowerInvariant()){throw 'V21 ecosystem adapter checks incomplete'}
    # Local-only release gate: mixed success/failure calls, full/lite, STDIO and
    # eight HTTP clients. This explicitly cannot certify native TIA stability.
    $stabilityOut=Join-Path $out ("stability-v$major-"+[Guid]::NewGuid().ToString('N'))
    Run $Python @((Join-Path $repo 'scripts/checks/Test-LocalStability.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api,'--rounds',"$LocalStabilityRounds",'--output',$stabilityOut) "stability-v$major.log"
    $isolatedOut=Join-Path $out ("isolated-stability-v$major-"+[Guid]::NewGuid().ToString('N'))
    Run $Python @((Join-Path $repo 'scripts/checks/Test-LocalStability.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api,'--rounds',"$LocalStabilityRounds",'--output',$isolatedOut,'--isolate-openness') "isolated-stability-v$major.log"
    $isolatedStability=Get-Content (Join-Path $isolatedOut 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($isolatedStability.status -ne 'passed' -or $isolatedStability.runs.Count -ne 4 -or !$isolatedStability.isolatedWorker -or $isolatedStability.runtimeSha256 -ne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Isolated local stability checks failed or used a different EXE'}
    $stability=Get-Content (Join-Path $stabilityOut 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($stability.status -ne 'passed' -or $stability.runs.Count -ne 4 -or $stability.runtimeSha256 -ne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Local stability validation incomplete or used a different EXE'}
    Run $harness @($exe,'native-export-only') "native-export-v$major.log"
    $nativeExport=[regex]::Match((Get-Content (Join-Path $out "native-export-v$major.log") -Raw),'COMPLETE: (\d+) native export remoting checks passed')
    if(!$nativeExport.Success){throw 'Native export remoting validation did not report complete success'}
    Run $harness @($exe,'hmi-snapshot-only') "hmi-snapshot-v$major.log"
    $snapshot=[regex]::Match((Get-Content (Join-Path $out "hmi-snapshot-v$major.log") -Raw),'COMPLETE: (\d+) HMI snapshot remoting checks passed')
    if(!$snapshot.Success){throw 'HMI snapshot remoting validation did not report complete success'}
    Run $harness @($exe,'global-script-only',$api) "global-script-v$major.log"
    $globalScript=[regex]::Match((Get-Content (Join-Path $out "global-script-v$major.log") -Raw),'COMPLETE: (\d+) global script bridge checks passed')
    if($major -eq 21){Assert-MatchedCheckCount 'globalScriptV21' $globalScript 'V21 native script API signatures were not verified'}
    elseif(!$globalScript.Success){throw 'Global script bridge validation did not report complete success'}
    Run $harness @($exe,'graphic-selection-only',$api) "graphic-selection-v$major.log"
    $graphicSelection=[regex]::Match((Get-Content (Join-Path $out "graphic-selection-v$major.log") -Raw),'COMPLETE: (\d+) graphical selection checks passed')
    Assert-MatchedCheckCount 'graphicSelection' $graphicSelection 'Graphical selection runtime validation did not report complete success'
    Run $harness @($exe,'runtime-settings-only',$api) "runtime-settings-v$major.log"
    $runtimeSettings=[regex]::Match((Get-Content (Join-Path $out "runtime-settings-v$major.log") -Raw),'COMPLETE: (\d+) runtime settings checks passed')
    Assert-MatchedCheckCount "runtimeSettingsV$major" $runtimeSettings 'Runtime settings validation did not report complete success'
    Run $harness @($exe,'test-migration-read-assembly',$api) "assembly-v$major.log"
    $checks["V$major"]=[ordered]@{httpPassed=[int]$http.Groups[1].Value;hmiPassed=[int]$hmi.Groups[1].Value;resourceDiscoveryPassed=[int]$resources.Groups[1].Value;nativeExportRemotingPassed=[int]$nativeExport.Groups[1].Value;migrationAssembly='passed';realProjectAcceptance='NOT PERFORMED for this release'}
    $checks["V$major"]['hmiSnapshotRemotingPassed']=[int]$snapshot.Groups[1].Value
    $checks["V$major"]['softwareLookupPassed']=[int]$softwareLookup.Groups[1].Value
    $checks["V$major"]['engineeringApiShapePassed']=[int]$engineeringApi.Groups[1].Value
    $checks["V$major"]['nativeHarness']=[ordered]@{compiled=$true;safetyChecksPassed=[int]$nativeSafety.Groups[1].Value;supervisorChecksPassed=[int]$nativeSupervisor.Groups[1].Value;exeSha256=(Get-FileHash $nativeExe).Hash.ToLowerInvariant();supervisorSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-NativeLifecycle.py')).Hash.ToLowerInvariant();liveAcceptance='NOT RUN; explicit opt-in required'}
    $checks["V$major"]['localStability']=$stability
    $checks["V$major"]['v21EcosystemAdapters']=$v21Ecosystem
    $checks["V$major"]['isolatedLocalStability']=$isolatedStability
    $checks["V$major"]['sessionStability']=[ordered]@{processLeaseChecksPassed=[int]$processLeases.Groups[1].Value;nativeMcpSafetyChecksPassed=[int]$nativeMcpSafety.Groups[1].Value;crashEvidenceChecksPassed=$crashEvidencePassed;nativeMcpExecuted=$false}
    $categories=[ordered]@{}
    $coverage.sites | Group-Object category | ForEach-Object {$categories[$_.Name]=$_.Count}
    $checks["V$major"]['nativeDiagnostics']=[ordered]@{status='passed';sites=$coverage.count;categories=$categories;uncoveredSupportedBoundaries=0;coverageSha256=(Get-FileHash $coveragePath).Hash.ToLowerInvariant();instrumenterSha256=$coverage.instrumenterSha256;jitPrepared=[int]$nativeJit.Groups[1].Value;openGenericWrappers=[int]$nativeJit.Groups[2].Value;fixture=$diagnosticTests;scriptSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-NativeDiagnostics.py')).Hash.ToLowerInvariant();liveTiaExecuted=$false;scope='Engine-owned Openness call sites; not SDK/server internals or a native stability claim'}
    $workerIsolationDefault=Get-WorkerIsolationDefault $exe
    $checks["V$major"]['workerIsolation']=[ordered]@{enabledByDefault=$workerIsolationDefault;faultChecksPassed=[int]$workerFaults.Groups[1].Value;protocolChecksPassed=[int]$workerProtocol.Groups[1].Value;nativeAcceptance='NOT RUN';protocolScriptSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-WorkerIsolation.py')).Hash.ToLowerInvariant()}
    $checks["V$major"]['approvalSafety']=[ordered]@{status='passed';checksPassed=[int]$approvalResult.checksPassed;defaultEnabled=$true;directWriteRefusedBeforeDispatch=$true;callToolWriteRefusedBeforeDispatch=$true;readSucceeded=$true;workbenchConnected=$false;tiaConnected=$false;scriptSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-ReleaseApprovalGate.py')).Hash.ToLowerInvariant()}
    $checks["V$major"]['engineeringLiveEdits']='NOT TESTED; preview/API shape and offline behavior only'
    $checks["V$major"]['unifiedGraphicLists']=if($major -eq 21){'API present; native import not live-tested'}else{'not exposed by supplied V20 API'}
    $checks["V$major"]['globalScriptBridgePassed']=[int]$globalScript.Groups[1].Value
    $checks["V$major"]['graphicSelectionPassed']=[int]$graphicSelection.Groups[1].Value
    $checks["V$major"]['runtimeSettingsPassed']=[int]$runtimeSettings.Groups[1].Value
    Run $harness @($exe,'test-ecosystem-assembly',$api) "ecosystem-v$major.log"
    $ecosystem=[regex]::Match((Get-Content (Join-Path $out "ecosystem-v$major.log") -Raw),'COMPLETE: (\d+) ecosystem assembly checks passed')
    Assert-MatchedCheckCount 'ecosystem' $ecosystem 'Ecosystem runtime validation incomplete; install the companion Python environment first'
    $checks["V$major"]['ecosystemAssemblyPassed']=[int]$ecosystem.Groups[1].Value
    $checks["V$major"]['globalScriptNativeApiSignature']=if($major -eq 21){'verified in referenced V21 DLL; live import not tested'}else{'not established; bridge checks only'}
}
[IO.File]::WriteAllText((Join-Path $out 'pipeline-result.json'),($checks["V$PipelineMajor"]|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output "V$PipelineMajor pipeline passed; logs: $out"
return
}
$jobs=@()
try {
    foreach($major in @(20,21)) {
        $parameters=@{V20ReferenceRoot=$V20ReferenceRoot;V21ReferenceRoot=$V21ReferenceRoot;Dotnet=$Dotnet;Python=$Python;NuGetConfig=$NuGetConfig;ReleaseDate=$ReleaseDate;NoRestore=[bool]$NoRestore;LocalStabilityRounds=$LocalStabilityRounds;PipelineMajor=$major}
        $jobs+=Start-Job -Name "release-v$major" -ScriptBlock {
            param($Script,$Parameters,$WorkingDirectory)
            Set-Location -LiteralPath $WorkingDirectory
            & $Script @Parameters
        } -ArgumentList $PSCommandPath,$parameters,$repo
    }
    Wait-VersionPipelines $jobs $out
    foreach($major in @(20,21)) {
        $result=Join-Path $out "v$major/pipeline-result.json"
        if(!(Test-Path -LiteralPath $result)){throw "V$major pipeline returned no validation record"}
        $checks["V$major"]=Get-Content -LiteralPath $result -Raw -Encoding UTF8 | ConvertFrom-Json
    }
} finally {
    foreach($job in $jobs){if($job.State -eq 'Running'){Stop-Job $job};Remove-Job $job -Force}
}
# Validate the shipped V21 assembly as well as the early production-source fixtures.
$exe=Join-Path $repo 'runtime/v21/TiaMcp.Engine.V21.exe';$api=$V21ReferenceRoot
Run $harness @($exe,'test-download-route',$api) 'route-selection-v21.log'
Run $harness @($exe,'test-match-plc-name') 'match-plc-name-v21.log'
Run $harness @($exe,'generate-tools-list',$api,(Join-Path $repo 'manifest/tools-list.json'),$package) 'tools-list.log'
# 工具矩阵与清单同源：清单刚生成就重建矩阵，docs/reference/tool-matrix.md 不再手工维护。
Run $Dotnet @('run',(Join-Path $repo 'scripts/generate/Generate-ToolCapabilityMatrix.cs'),'--','--tools-list',(Join-Path $repo 'manifest/tools-list.json'),'--out-file',(Join-Path $repo 'docs/reference/tool-matrix.md')) 'tool-matrix.log'
function WriteJson($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))}
$roster=Get-Content (Join-Path $repo 'manifest/tools-list.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestPath=Join-Path $repo 'manifest/package-manifest.json'
$manifest=Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.packageName=$package
$manifest.bundleVersion=$release
$manifest.fileVersion=$version
$manifest.refreshedAt=[DateTimeOffset]::UtcNow.ToString('o')
$manifest.capabilities.mcpToolCount=$roster.toolCount
$layers=[ordered]@{}
$roster.tools | Group-Object layer | ForEach-Object {$layers[$_.Name]=$_.Count}
$manifest.capabilities.mcpToolLayers=$layers
# Derive lite count from the generated V4 catalog (the single source of the profiles), and reject missing/duplicate registrations.
[xml]$toolProfiles=Get-Content (Join-Path $repo 'src/Logic/ModelContextProtocol/ToolProfiles.resx') -Raw -Encoding UTF8
$catalogJson=@($toolProfiles.root.data | Where-Object {$_.name -eq 'Catalog'})[0].value
if(!$catalogJson){throw 'Could not locate the generated tool catalog'}
$liteNames=@(($catalogJson | ConvertFrom-Json).releases.'21' | Where-Object {$_.profiles -contains 'lite'} | ForEach-Object {$_.currentName})
if(!$liteNames){throw 'Could not locate the generated lite tool roster'}
if(($liteNames | Select-Object -Unique).Count -ne $liteNames.Count){throw 'Duplicate lite tool names'}
foreach($name in $liteNames){if($name -notin $roster.tools.name){throw "Lite tool missing from compiled roster: $name"}}
$manifest.capabilities.liteProfile.toolCount=$liteNames.Count
$manifest.capabilities.liteProfile.note='Other available tools remain reachable through FindTools + CallTool; per-version admission excludes unsupported routes and runtime tools/list is authoritative.'
$manifest.validationStatus='Both runtimes compiled and tested locally; new real-project acceptance remains pending'
WriteJson $manifestPath $manifest
$runtimeFiles=@(Get-ChildItem (Join-Path $repo 'runtime/v20'),(Join-Path $repo 'runtime/v21'),(Join-Path $repo 'runtime/tools'),$verifierDirectory -File -Recurse | Where-Object {$_.Extension -in '.exe','.dll','.config' -or ($_.DirectoryName -eq $verifierDirectory -and $_.Name -in 'NativeCallWeaver.deps.json','NativeCallWeaver.runtimeconfig.json')} | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
# Bind the local validation results to these exact compiler/test inputs.
$currentValidation=@(Read-ReleaseInputs -Validation)
if(($validationInputs|ConvertTo-Json -Depth 4 -Compress) -cne ($currentValidation|ConvertTo-Json -Depth 4 -Compress)){throw 'Validation inputs changed during the build; refusing to record results'}
$currentInputs=@(Read-ReleaseInputs)
if(($sourceFiles|ConvertTo-Json -Depth 4 -Compress) -cne ($currentInputs|ConvertTo-Json -Depth 4 -Compress)){throw 'Build inputs changed while validation was running; refusing to record results'}
$validationArtifacts=@(foreach($major in @(20,21)) {
    foreach($name in @("native-call-coverage-v$major.json","tool-usage-v$major.json")) {
        $path=Join-Path $out "v$major/$name"
        [ordered]@{path=$path.Substring($repo.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
    }
})
$isolationDefault20=Get-WorkerIsolationDefault (Join-Path $repo 'runtime/v20/TiaMcp.Engine.V20.exe')
$isolationDefault21=Get-WorkerIsolationDefault (Join-Path $repo 'runtime/v21/TiaMcp.Engine.V21.exe')
if($isolationDefault20 -ne $isolationDefault21){throw 'V20/V21 worker isolation defaults differ.'}
WriteJson (Join-Path $repo 'manifest/release-build.json') ([ordered]@{release=$release;releaseDate=$ReleaseDate;fileVersion=$version;package=$package;generatedAt=[DateTimeOffset]::UtcNow.ToString('o');workerIsolation=[ordered]@{enabledByDefault=$isolationDefault21};validation=[ordered]@{offlinePassed=$offlinePassed;offlineV20Passed=$offlineV20Passed;versionPolicySdkPassed=$versionPolicySdkPassed;updaterPassed=$updaterPassed;runtimes=$checks};runtimeFiles=$runtimeFiles;sourceFiles=$sourceFiles;validationInputs=$validationInputs;validationArtifacts=$validationArtifacts})
Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Prepare-Delivery.ps1'),'-Release',$release,'-ReleaseDate',$ReleaseDate) 'delivery.log'
Write-Output "Built and checked both runtimes: $version. Review and commit changes, then run scripts/build/Package-Release.py. Real TIA acceptance is separate."
