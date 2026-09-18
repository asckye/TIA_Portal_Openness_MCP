param(
    [Parameter(Mandatory=$true)][string]$V20ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$V21ReferenceRoot,
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$NuGetConfig='',
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate=(Get-Date -Format 'yyyyMMdd'),
    [switch]$NoRestore
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
$harnessProject=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj'
Restore $harnessProject @()
Run $Dotnet @('build',$harnessProject,'-c','Release','--no-restore','-v:q') 'build-harness.log'
$harness=Join-Path (Split-Path $harnessProject) 'bin/Release/net48/HttpTests.exe'
$checks=[ordered]@{}
foreach($major in @(20,21)) {
    $api=(Resolve-Path -LiteralPath $(if($major -eq 20){$V20ReferenceRoot}else{$V21ReferenceRoot})).Path
    $project=Join-Path $source $(if($major -eq 20){'TiaMcpServer.V20.csproj'}else{'TiaMcpServer.V21.csproj'})
    [xml]$xml=Get-Content $project -Raw
    if($xml.Project.PropertyGroup.FileVersion -ne $version -or $xml.Project.PropertyGroup.InformationalVersion -ne $release){throw 'V20/V21 source versions differ'}
    $obj=Join-Path $source $(if($major -eq 20){'obj-v20/'}else{'obj/'})
    $properties=@("-p:SiemensEngineeringDirectory=$api","-p:BaseIntermediateOutputPath=$obj","-p:MSBuildProjectExtensionsPath=$obj")
    Restore $project $properties
    Run $Dotnet (@('build',$project,'-c','Release','--no-restore','-v:q')+$properties) "build-v$major.log"
    $built=Join-Path $source $(if($major -eq 20){'bin-v20/Release/net48'}else{'bin/Release/net48'})
    $runtime=Join-Path $repo "runtime/v$major"
    $payload=@(Get-ChildItem -LiteralPath $built -File | Where-Object {$_.Extension -in '.exe','.dll','.config' -and $_.Name -notlike 'Siemens.Engineering*'})
    # Fail on obsolete dependencies so an old DLL is never silently republished.
    foreach($file in Get-ChildItem -LiteralPath $runtime -File | Where-Object {$_.Extension -in '.exe','.dll','.config'}){if($file.Name -notin $payload.Name){throw "Review obsolete runtime file: $($file.FullName)"}}
    $payload | Copy-Item -Destination $runtime -Force
    $exe=Join-Path $runtime 'TiaMcpServer.exe'
    if((Get-Item $exe).VersionInfo.FileVersion -ne $version){throw "V$major runtime version mismatch"}
    Run $harness @($exe,'software-lookup-only',$api) "software-lookup-v$major.log"
    $softwareLookup=[regex]::Match((Get-Content (Join-Path $out "software-lookup-v$major.log") -Raw),'COMPLETE: (\d+) software lookup checks passed')
    if(!$softwareLookup.Success -or [int]$softwareLookup.Groups[1].Value -ne 45){throw 'Software lookup/listing validation did not report complete success'}
    Run $harness @($exe,'engineering-api-only',$api) "engineering-api-v$major.log"
    $engineeringApi=[regex]::Match((Get-Content (Join-Path $out "engineering-api-v$major.log") -Raw),'COMPLETE: (\d+) engineering API checks passed')
    $expectedEngineeringChecks=if($major -eq 21){1032}else{936}
    if(!$engineeringApi.Success -or [int]$engineeringApi.Groups[1].Value -ne $expectedEngineeringChecks){throw 'Engineering API compatibility checks incomplete'}
    Run $harness @($exe) "http-v$major.log"
    Run $harness @($exe,'hmi-only',"$major",$version) "hmi-v$major.log"
    $http=[regex]::Match((Get-Content (Join-Path $out "http-v$major.log") -Raw),'COMPLETE: (\d+) passed')
    $hmi=[regex]::Match((Get-Content (Join-Path $out "hmi-v$major.log") -Raw),'(\d+) HMI traversal assertions, 0 failed')
    if(!$http.Success -or !$hmi.Success){throw 'Runtime regression did not report complete success'}
    # Exercise the actual host methods with SDK dispatch and transports, without
    # changing this machine's Openness group or connecting to a TIA process.
    Run $Python @((Join-Path $repo 'scripts/checks/Test-ResourceDiscovery.py'),'--exe',$exe,'--portal-root',$api,'--major',"$major",'--host-harness',$harness,'--public-api',$api) "resources-v$major.log"
    $resources=[regex]::Match((Get-Content (Join-Path $out "resources-v$major.log") -Raw),'COMPLETE: (\d+) resource discovery checks passed')
    if(!$resources.Success){throw 'Resource discovery validation did not report complete success'}
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
    $checks["V$major"]['engineeringLiveEdits']='NOT TESTED; preview/API shape and offline behavior only'
    $checks["V$major"]['unifiedGraphicLists']=if($major -eq 21){'API present; native import not live-tested'}else{'not exposed by supplied V20 API'}
    $checks["V$major"]['globalScriptBridgePassed']=[int]$globalScript.Groups[1].Value
    $checks["V$major"]['graphicSelectionPassed']=[int]$graphicSelection.Groups[1].Value
    $checks["V$major"]['runtimeSettingsPassed']=[int]$runtimeSettings.Groups[1].Value
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
$manifest.capabilities.liteProfile.note='All other attributed tools remain reachable through FindTools + CallTool; runtime tools/list is authoritative.'
$manifest.validationStatus='Both runtimes compiled and tested locally; new real-project acceptance remains pending'
WriteJson $manifestPath $manifest
$runtimeFiles=@(Get-ChildItem (Join-Path $repo 'runtime') -File -Recurse | Where-Object {$_.Extension -in '.exe','.dll','.config'} | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
# Bind the local validation results to these exact compiler/test inputs.
$sourceFiles=@(Get-ChildItem (Join-Path $repo 'tools/tiaportal-mcp/src'),(Join-Path $repo 'tools/tiaportal-mcp/tests') -File -Recurse | Where-Object {$_.Extension -in '.cs','.csproj','.props','.targets' -and $_.FullName -notmatch '[\\/](obj|obj-v20|bin|bin-v20)[\\/]'} | Sort-Object FullName | ForEach-Object {
    $text=[IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n")
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    [ordered]@{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');sha256=$digest}
})
WriteJson (Join-Path $repo 'manifest/release-build.json') ([ordered]@{release=$release;releaseDate=$ReleaseDate;fileVersion=$version;package=$package;generatedAt=[DateTimeOffset]::UtcNow.ToString('o');validation=[ordered]@{offlinePassed=$offlinePassed;runtimes=$checks};runtimeFiles=$runtimeFiles;sourceFiles=$sourceFiles})
Run 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Prepare-Delivery.ps1'),'-Release',$release,'-ReleaseDate',$ReleaseDate) 'delivery.log'
Write-Output "Built and checked both runtimes: $version. Review and commit changes, then run scripts/build/Package-Release.py. Real TIA acceptance is separate."

