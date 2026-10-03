param(
    [Parameter(Mandatory=$true)][string]$V20ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$V21ReferenceRoot,
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$NuGetConfig='',
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate=(Get-Date -Format 'yyyyMMdd'),
    [switch]$NoRestore,
    [ValidateRange(10,10000)][int]$LocalStabilityRounds=50
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer'
[xml]$projectXml=Get-Content (Join-Path $source 'TiaMcpServer.V21.csproj') -Raw
$version=[string]$projectXml.Project.PropertyGroup.FileVersion
$release=[string]$projectXml.Project.PropertyGroup.InformationalVersion
if($release -notmatch '^\d+\.\d+\.\d+$'){throw 'Public release version must be X.Y.Z without fork or feature suffixes'}
$null=[DateTime]::ParseExact($ReleaseDate,'yyyyMMdd',[Globalization.CultureInfo]::InvariantCulture)
$package="TIA_MCP_Delivery_v${release}_$ReleaseDate"
$out=Join-Path $repo "bin-build/releases/v$release"
New-Item -ItemType Directory -Force $out | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $out 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
function Run([string]$Program,[string[]]$Arguments,[string]$Log) {
    # Windows PowerShell wraps native stderr as ErrorRecord even for warnings.
    $savedPreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        & $Program @Arguments > (Join-Path $out $Log) 2>&1
        $exitCode=$LASTEXITCODE
    } finally { $ErrorActionPreference=$savedPreference }
    if($exitCode){throw "$Program failed ($exitCode); see $out/$Log"}
}
function Restore([string]$Project,[string[]]$Properties) {
    if($NoRestore){return}
    $argsList=@('restore',$Project,'-v:q')+$Properties
    if($NuGetConfig){$argsList+=@('--configfile',(Resolve-Path -LiteralPath $NuGetConfig).Path)}
    Run $Dotnet $argsList ('restore-'+[IO.Path]::GetFileName($Project)+'.log')
}
$offline=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj'
Restore $offline @()
Run $Dotnet @('run','--project',$offline,'-c','Release','--no-restore') 'offline.log'
$match=[regex]::Match((Get-Content (Join-Path $out 'offline.log') -Raw),'(\d+) passed, 0 failed, 0 skipped')
if(!$match.Success){throw 'Offline suite did not report complete success'}
$offlinePassed=[int]$match.Groups[1].Value
Run $Python @((Join-Path $repo 'scripts/checks/Test-VersionCatalogWiring.py')) 'version-catalog-wiring.log'
# Exercise admission and bridge refusal with both compiled identities, not only
# the default V21 symbol. These are offline tests, never native TIA calls.
Run $Dotnet @('run','--project',$offline,'-c','Release','--no-restore','-p:DefineConstants=TIA_V20') 'offline-v20.log'
$v20Offline=[regex]::Match((Get-Content (Join-Path $out 'offline-v20.log') -Raw),'(\d+) passed, 0 failed, 0 skipped')
if(!$v20Offline.Success){throw 'V20-symbol offline suite did not report complete success'}
$versionPolicyProject=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.VersionPolicyTests/TiaMcpServer.VersionPolicyTests.csproj'
Restore $versionPolicyProject @()
Run $Dotnet @('run','--project',$versionPolicyProject,'-c','Release','--no-restore') 'version-policy-sdk.log'
$versionPolicySdk=[regex]::Match((Get-Content (Join-Path $out 'version-policy-sdk.log') -Raw),'(\d+) passed, 0 failed, 0 skipped')
if(!$versionPolicySdk.Success){throw 'MCP SDK version-policy checks incomplete'}
$harnessProject=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj'
Restore $harnessProject @()
Run $Dotnet @('build',$harnessProject,'-c','Release','--no-restore','-v:q') 'build-harness.log'
$harness=Join-Path (Split-Path $harnessProject) 'bin/Release/net48/HttpTests.exe'
$weaverProject=Join-Path $repo 'tools/native-call-weaver/NativeCallWeaver.csproj'
Restore $weaverProject @()
Run $Dotnet @('build',$weaverProject,'-c','Release','--no-restore','-v:q') 'native-weaver-build.log'
$weaver=Join-Path $repo 'tools/native-call-weaver/bin/Release/net8.0/NativeCallWeaver.dll'
$diagnosticProject=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.DiagnosticsTests/DiagnosticsTests.csproj'
Restore $diagnosticProject @()
Run $Dotnet @('build',$diagnosticProject,'-c','Release','--no-restore','-v:q') 'native-diagnostics-build.log'
$diagnosticFixture=Join-Path (Split-Path $diagnosticProject) 'bin/Release/net48/DiagnosticsTests.exe'
$diagnosticOut=Join-Path $out ('native-diagnostics-'+[Guid]::NewGuid().ToString('N'))
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeDiagnostics.py'),'--fixture',$diagnosticFixture,'--weaver',$weaver,'--output',$diagnosticOut) 'native-diagnostics-tests.log'
$diagnosticTests=Get-Content (Join-Path $diagnosticOut 'result.json') -Raw | ConvertFrom-Json
if($diagnosticTests.behaviorChecks -lt 36 -or $diagnosticTests.rejectionChecks -ne 5 -or $diagnosticTests.nativeTiaExecuted){throw 'Native diagnostic fixture gate incomplete'}
# Compile the separate opt-in native harness, but execute ONLY its offline safety
# checks here. Live TIA creation belongs to a dedicated, explicitly enabled run.
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeLifecycle.py'),'--self-test') 'native-supervisor.log'
$nativeSupervisor=[regex]::Match((Get-Content (Join-Path $out 'native-supervisor.log') -Raw),'COMPLETE: (\d+) native supervisor checks passed; live TIA tests NOT RUN')
if(!$nativeSupervisor.Success){throw 'Native supervisor offline checks incomplete'}
Run $Python @((Join-Path $repo 'scripts/checks/Test-NativeMcpSession.py'),'--self-test') 'native-mcp-safety.log'
if ((Get-Content (Join-Path $out 'native-mcp-safety.log') -Raw) -notmatch 'COMPLETE: 8 native MCP safety checks passed') { throw 'Native MCP safety checks incomplete' }
Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-CrashEvidence.ps1')) 'crash-evidence-tests.log'
if ((Get-Content (Join-Path $out 'crash-evidence-tests.log') -Raw) -notmatch 'COMPLETE: 6 crash evidence checks passed') { throw 'Crash evidence collector checks incomplete' }
Run $Python @((Join-Path $repo 'scripts/generate/Generate-ToolUsage.py'),'--check') 'tool-usage-catalog.log'
$checks=[ordered]@{}
foreach($major in @(20,21)) {
    $api=(Resolve-Path -LiteralPath $(if($major -eq 20){$V20ReferenceRoot}else{$V21ReferenceRoot})).Path
    $project=Join-Path $source $(if($major -eq 20){'TiaMcpServer.V20.csproj'}else{'TiaMcpServer.V21.csproj'})
    [xml]$xml=Get-Content $project -Raw
    if($xml.Project.PropertyGroup.FileVersion -ne $version -or $xml.Project.PropertyGroup.InformationalVersion -ne $release){throw 'V20/V21 source versions differ'}
    $obj=Join-Path $source $(if($major -eq 20){'obj-v20/'}else{'obj/'})
    $properties=@("-p:SiemensEngineeringDirectory=$api")
    $nativeProject=Join-Path $repo "tools/tiaportal-mcp/tests/TiaMcpServer.NativeTests/V$major/NativeTests.V$major.csproj"
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
    $payload=@(Get-ChildItem -LiteralPath $built -File | Where-Object {$_.Extension -in '.exe','.dll','.config' -and $_.Name -notlike 'Siemens.Engineering*'})
    # Fail on obsolete dependencies so an old DLL is never silently republished.
    foreach($file in Get-ChildItem -LiteralPath $runtime -File | Where-Object {$_.Extension -in '.exe','.dll','.config'}){if($file.Name -notin $payload.Name){throw "Review obsolete runtime file: $($file.FullName)"}}
    $payload | Copy-Item -Destination $runtime -Force
    $exe=Join-Path $runtime 'TiaMcpServer.exe'
    if((Get-Item $exe).VersionInfo.FileVersion -ne $version){throw "V$major runtime version mismatch"}
    Run $harness @($exe,'example-library-only') "example-library-v$major.log"
    $coveragePath=Join-Path $out "native-call-coverage-v$major.json"
    Run $Dotnet @($weaver,'verify',$exe,$coveragePath) "native-coverage-v$major.log"
    $coverage=Get-Content $coveragePath -Raw | ConvertFrom-Json
    Run $harness @($exe,'native-diagnostics-only',$api) "native-jit-v$major.log"
    $nativeJit=[regex]::Match((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw),'COMPLETE: (\d+) native diagnostic wrappers JIT prepared; (\d+) open generic wrappers')
    if(!$nativeJit.Success -or ([int]$nativeJit.Groups[1].Value+[int]$nativeJit.Groups[2].Value) -ne $coverage.count){throw 'Diagnostic wrapper JIT/inventory mismatch'}
    if((Get-Content (Join-Path $out "native-jit-v$major.log") -Raw) -notmatch 'COMPLETE: 3 native journal reader checks passed'){throw 'Native journal reader checks incomplete'}
    Run $harness @($exe,'process-leases-only') "process-leases-v$major.log"
    if ((Get-Content (Join-Path $out "process-leases-v$major.log") -Raw) -notmatch 'COMPLETE: 2 process lease checks passed') { throw 'Cross-process lease checks incomplete' }
    Run $harness @($exe,'worker-supervisor-only') "worker-supervisor-v$major.log"
    $workerFaults=[regex]::Match((Get-Content (Join-Path $out "worker-supervisor-v$major.log") -Raw),'COMPLETE: (\d+) worker supervisor checks passed; no TIA connection attempted')
    if(!$workerFaults.Success -or [int]$workerFaults.Groups[1].Value -lt 25){throw 'Worker supervisor fault checks incomplete'}
    Run $Python @((Join-Path $repo 'scripts/checks/Test-WorkerIsolation.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api) "worker-protocol-v$major.log"
    $workerProtocol=[regex]::Match((Get-Content (Join-Path $out "worker-protocol-v$major.log") -Raw),'COMPLETE: (\d+) isolated MCP checks passed; no TIA connection attempted')
    if(!$workerProtocol.Success -or [int]$workerProtocol.Groups[1].Value -lt 58){throw 'Isolated MCP protocol checks incomplete'}
    Run $harness @($exe,'software-lookup-only',$api) "software-lookup-v$major.log"
    $softwareLookup=[regex]::Match((Get-Content (Join-Path $out "software-lookup-v$major.log") -Raw),'COMPLETE: (\d+) software lookup checks passed')
    if(!$softwareLookup.Success -or [int]$softwareLookup.Groups[1].Value -ne 45){throw 'Software lookup/listing validation did not report complete success'}
    Run $harness @($exe,'engineering-api-only',$api) "engineering-api-v$major.log"
    $engineeringApi=[regex]::Match((Get-Content (Join-Path $out "engineering-api-v$major.log") -Raw),'COMPLETE: (\d+) engineering API checks passed')
    $expectedEngineeringChecks=if($major -eq 21){3126}else{2840}
    if(!$engineeringApi.Success -or [int]$engineeringApi.Groups[1].Value -ne $expectedEngineeringChecks){throw 'Engineering API compatibility checks incomplete'}
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
    Run $Python @((Join-Path $repo 'scripts/checks/Test-V21Ecosystem.py'),'--exe',$exe,'--major',"$major",'--host-harness',$harness,'--public-api',$api,'--schema-root',$v21Schemas,'--output',$v21EcosystemOut) "v21-ecosystem-v$major.log"
    $v21EcosystemFiles=@(Get-ChildItem -LiteralPath $v21EcosystemOut -Filter result.json -Recurse -File)
    if($v21EcosystemFiles.Count -ne 1){throw 'V21 ecosystem evidence missing or ambiguous'}
    $v21Ecosystem=Get-Content -LiteralPath $v21EcosystemFiles[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if($v21Ecosystem.status -ne 'passed' -or $v21Ecosystem.checks -lt 75 -or $v21Ecosystem.selfTestOnly -or $v21Ecosystem.nativeTiaExecuted -or $v21Ecosystem.runtimeSha256 -ne (Get-FileHash $exe).Hash.ToLowerInvariant()){throw 'V21 ecosystem adapter checks incomplete'}
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
    if(!$globalScript.Success){throw 'Global script bridge validation did not report complete success'}
    if($major -eq 21 -and [int]$globalScript.Groups[1].Value -ne 8){throw 'V21 native script API signatures were not verified'}
    Run $harness @($exe,'graphic-selection-only',$api) "graphic-selection-v$major.log"
    $graphicSelection=[regex]::Match((Get-Content (Join-Path $out "graphic-selection-v$major.log") -Raw),'COMPLETE: (\d+) graphical selection checks passed')
    if(!$graphicSelection.Success -or [int]$graphicSelection.Groups[1].Value -ne 8){throw 'Graphical selection runtime validation did not report complete success'}
    Run $harness @($exe,'runtime-settings-only',$api) "runtime-settings-v$major.log"
    $runtimeSettings=[regex]::Match((Get-Content (Join-Path $out "runtime-settings-v$major.log") -Raw),'COMPLETE: (\d+) runtime settings checks passed')
    $expectedRuntimeChecks=if($major -eq 21){9}else{8}
    if(!$runtimeSettings.Success -or [int]$runtimeSettings.Groups[1].Value -ne $expectedRuntimeChecks){throw 'Runtime settings validation did not report complete success'}
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-MigrationReadAssembly.ps1'),'-Exe',$exe,'-PublicApiDirectory',$api) "assembly-v$major.log"
    $checks["V$major"]=[ordered]@{httpPassed=[int]$http.Groups[1].Value;hmiPassed=[int]$hmi.Groups[1].Value;resourceDiscoveryPassed=[int]$resources.Groups[1].Value;nativeExportRemotingPassed=[int]$nativeExport.Groups[1].Value;migrationAssembly='passed';realProjectAcceptance='NOT PERFORMED for this release'}
    $checks["V$major"]['hmiSnapshotRemotingPassed']=[int]$snapshot.Groups[1].Value
    $checks["V$major"]['softwareLookupPassed']=[int]$softwareLookup.Groups[1].Value
    $checks["V$major"]['engineeringApiShapePassed']=[int]$engineeringApi.Groups[1].Value
    $checks["V$major"]['nativeHarness']=[ordered]@{compiled=$true;safetyChecksPassed=[int]$nativeSafety.Groups[1].Value;supervisorChecksPassed=[int]$nativeSupervisor.Groups[1].Value;exeSha256=(Get-FileHash $nativeExe).Hash.ToLowerInvariant();supervisorSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-NativeLifecycle.py')).Hash.ToLowerInvariant();liveAcceptance='NOT RUN; explicit opt-in required'}
    $checks["V$major"]['localStability']=$stability
    $checks["V$major"]['v21EcosystemAdapters']=$v21Ecosystem
    $checks["V$major"]['isolatedLocalStability']=$isolatedStability
    $checks["V$major"]['sessionStability']=[ordered]@{processLeaseChecksPassed=2;nativeMcpSafetyChecksPassed=8;crashEvidenceChecksPassed=6;nativeMcpExecuted=$false}
    $categories=[ordered]@{}
    $coverage.sites | Group-Object category | ForEach-Object {$categories[$_.Name]=$_.Count}
    $checks["V$major"]['nativeDiagnostics']=[ordered]@{status='passed';sites=$coverage.count;categories=$categories;uncoveredSupportedBoundaries=0;coverageSha256=(Get-FileHash $coveragePath).Hash.ToLowerInvariant();instrumenterSha256=$coverage.instrumenterSha256;jitPrepared=[int]$nativeJit.Groups[1].Value;openGenericWrappers=[int]$nativeJit.Groups[2].Value;fixture=$diagnosticTests;scriptSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-NativeDiagnostics.py')).Hash.ToLowerInvariant();liveTiaExecuted=$false;scope='Engine-owned Openness call sites; not SDK/server internals or a native stability claim'}
    $checks["V$major"]['workerIsolation']=[ordered]@{enabledByDefault=$false;faultChecksPassed=[int]$workerFaults.Groups[1].Value;protocolChecksPassed=[int]$workerProtocol.Groups[1].Value;nativeAcceptance='NOT RUN';protocolScriptSha256=(Get-FileHash (Join-Path $repo 'scripts/checks/Test-WorkerIsolation.py')).Hash.ToLowerInvariant()}
    $checks["V$major"]['engineeringLiveEdits']='NOT TESTED; preview/API shape and offline behavior only'
    $checks["V$major"]['unifiedGraphicLists']=if($major -eq 21){'API present; native import not live-tested'}else{'not exposed by supplied V20 API'}
    $checks["V$major"]['globalScriptBridgePassed']=[int]$globalScript.Groups[1].Value
    $checks["V$major"]['graphicSelectionPassed']=[int]$graphicSelection.Groups[1].Value
    $checks["V$major"]['runtimeSettingsPassed']=[int]$runtimeSettings.Groups[1].Value
    Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-EcosystemAssembly.ps1'),'-Exe',$exe,'-PublicApiDirectory',$api) "ecosystem-v$major.log"
    $ecosystem=[regex]::Match((Get-Content (Join-Path $out "ecosystem-v$major.log") -Raw),'COMPLETE: (\d+) ecosystem assembly checks passed')
    if(!$ecosystem.Success -or [int]$ecosystem.Groups[1].Value -ne 31){throw 'Ecosystem runtime validation incomplete; install the companion Python environment first'}
    $checks["V$major"]['ecosystemAssemblyPassed']=[int]$ecosystem.Groups[1].Value
    $checks["V$major"]['globalScriptNativeApiSignature']=if($major -eq 21){'verified in referenced V21 DLL; live import not tested'}else{'not established; bridge checks only'}
    if($major -eq 21){
        # 两个确定性离线测试直接反射已发布的 V21 EXE：PG/PC 路由选择与 softwarePath 匹配器。
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-DownloadRouteSelection.ps1'),'-PublicApiDirectory',$api) 'route-selection-v21.log'
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-MatchPlcName.ps1')) 'match-plc-name-v21.log'
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/checks/Test-WriteGuard.ps1')) 'write-guard.log'
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/generate/Generate-ToolsListFromAssembly.ps1'),'-Exe',$exe,'-PublicApiDirectory',$api,'-OutputPath',(Join-Path $repo 'manifest/tools-list.json'),'-PackageName',$package) 'tools-list.log'
        # 工具矩阵与清单同源：清单刚生成就重建矩阵，docs/reference/tool-matrix.md 不再手工维护。
        Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'scripts/generate/Generate-ToolCapabilityMatrix.ps1'),'-ToolsList',(Join-Path $repo 'manifest/tools-list.json'),'-OutFile',(Join-Path $repo 'docs/reference/tool-matrix.md')) 'tool-matrix.log'
    }
}
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
# Derive lite count from the explicit source roster, and reject missing/duplicate registrations.
$profile=Get-Content (Join-Path $source 'ModelContextProtocol/Tools/McpServer.Profile.cs') -Raw -Encoding UTF8
$liteBody=[regex]::Match($profile,'(?s)LiteToolNames\s*=.*?\{(.*?)\};').Groups[1].Value
if(!$liteBody){throw 'Could not locate the explicit lite tool roster'}
$liteBody=[regex]::Replace($liteBody,'(?m)//[^\r\n]*','')
$liteNames=@([regex]::Matches($liteBody,'"([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
if(($liteNames | Select-Object -Unique).Count -ne $liteNames.Count){throw 'Duplicate lite tool names'}
foreach($name in $liteNames){if($name -notin $roster.tools.name){throw "Lite tool missing from compiled roster: $name"}}
$manifest.capabilities.liteProfile.toolCount=$liteNames.Count
$manifest.capabilities.liteProfile.note='Other available tools remain reachable through FindTools + CallTool; per-version admission excludes unsupported routes and runtime tools/list is authoritative.'
$manifest.validationStatus='Both runtimes compiled and tested locally; new real-project acceptance remains pending'
WriteJson $manifestPath $manifest
$runtimeFiles=@(Get-ChildItem (Join-Path $repo 'runtime/v20'),(Join-Path $repo 'runtime/v21') -File -Recurse | Where-Object {$_.Extension -in '.exe','.dll','.config'} | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
# Bind the local validation results to these exact compiler/test inputs.
$sourceFiles=@(Get-ChildItem (Join-Path $repo 'tools/tiaportal-mcp/src'),(Join-Path $repo 'tools/tiaportal-mcp/tests'),(Join-Path $repo 'tools/native-call-weaver'),(Join-Path $repo 'tools/openness-shared'),(Join-Path $repo 'tools/third-party/TiaGitAddIn.Core'),(Join-Path $repo 'tools/third-party/SiemensOpcUaModelled') -File -Recurse | Where-Object {$_.Extension -in '.cs','.csproj','.props','.targets','.xml','.json' -and $_.FullName -notmatch '[\\/](obj|obj-v20|bin|bin-v20)[\\/]'} | Sort-Object FullName | ForEach-Object {
    $text=[IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n")
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    [ordered]@{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');sha256=$digest}
})
WriteJson (Join-Path $repo 'manifest/release-build.json') ([ordered]@{release=$release;releaseDate=$ReleaseDate;fileVersion=$version;package=$package;generatedAt=[DateTimeOffset]::UtcNow.ToString('o');validation=[ordered]@{offlinePassed=$offlinePassed;offlineV20Passed=[int]$v20Offline.Groups[1].Value;versionPolicySdkPassed=[int]$versionPolicySdk.Groups[1].Value;runtimes=$checks};runtimeFiles=$runtimeFiles;sourceFiles=$sourceFiles})
Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Prepare-Delivery.ps1'),'-Release',$release,'-ReleaseDate',$ReleaseDate) 'delivery.log'
Write-Output "Built and checked both runtimes: $version. Review and commit changes, then run scripts/build/Package-Release.py. Real TIA acceptance is separate."
