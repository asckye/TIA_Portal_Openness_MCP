#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [string[]]$ReleaseKeys=@('14sp1','15.1','16','17','18','19','20','21'),
    [string]$Dotnet='dotnet',
    [string]$NuGetConfig='',
    [string]$NativeCallWeaverPath='',
    [switch]$UseReferenceAssemblyPackage,
    [switch]$NoRestore,
    [switch]$Rebuild,
    [string]$SourceRoot='',
    [string]$EvidenceDirectory=''
)
$ErrorActionPreference='Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker/TiaMcpServer.PlcWorker.csproj'
if(!$NativeCallWeaverPath){$NativeCallWeaverPath=Join-Path $repo 'tools/native-call-weaver/bin/Release/net8.0/NativeCallWeaver.dll'}
$weaver=(Resolve-Path -LiteralPath $NativeCallWeaverPath).Path
$apiRoot=(Resolve-Path -LiteralPath $PublicApiRoot).Path
if(!$SourceRoot){$SourceRoot=Join-Path $repo 'tools/tiaportal-mcp/src'}
$source=(Resolve-Path -LiteralPath $SourceRoot).Path
if(!$EvidenceDirectory){$EvidenceDirectory=Join-Path $repo 'bin-build/plc-adapter-workers'}
$null=New-Item -ItemType Directory -Force -Path $EvidenceDirectory
$out=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$releases=@{
    '14sp1'=@('TIA_V14SP1_PublicAPI/V14 SP1','net461','Siemens.Engineering')
    '15.1'=@('TIA_V15.1_PublicAPI/V15.1','net461','Siemens.Engineering')
    '16'=@('TIA_V16_PublicAPI/V16','net461','Siemens.Engineering')
    '17'=@('TIA_V17_PublicAPI/V17','net48','Siemens.Engineering')
    '18'=@('TIA_V18_PublicAPI/V18','net48','Siemens.Engineering')
    '19'=@('TIA_V19_PublicAPI/V19','net48','Siemens.Engineering')
    '20'=@('TIA_V20_PublicAPI/V20','net48','Siemens.Engineering')
    '21'=@('TIA_V21_PublicAPI/V21/net48','net48','Siemens.Engineering.Base')
}
if(!$ReleaseKeys.Count){throw 'Select at least one exact release key.'}
foreach($key in $ReleaseKeys){if(!$releases.ContainsKey($key)){throw "Unsupported precise release key: $key"}}
$results=@()
foreach($key in $ReleaseKeys){
    $release=$releases[$key]
    $api=(Resolve-Path -LiteralPath (Join-Path $apiRoot $release[0])).Path
    $props=@("-p:TiaReleaseKey=$key","-p:SiemensEngineeringDirectory=$api","-p:AdapterSourceRoot=$source","-p:WorkerSourceRoot=$(Join-Path $source 'TiaMcpServer.PlcWorker')",'-p:NuGetAudit=false','-p:UseSharedCompilation=false')
    if($UseReferenceAssemblyPackage){$props+='-p:UseReferenceAssemblyPackage=true'}
    if($NativeCallWeaverPath){$props+="-p:NativeCallWeaverPath=$((Resolve-Path -LiteralPath $NativeCallWeaverPath).Path)"}
    if(!$NoRestore){
        $restore=@('restore',$project,'-v:minimal')+$props
        if($NuGetConfig){$restore+=@('--configfile',(Resolve-Path -LiteralPath $NuGetConfig).Path)}
        & $Dotnet @restore *> (Join-Path $out "restore-$key.log")
        if($LASTEXITCODE -ne 0){throw "Worker restore failed for $key; see restore-$key.log."}
    }
    $build=@('build',$project,'-c','Release','--no-restore','-v:minimal')
    if($Rebuild){$build+='--no-incremental'}
    & $Dotnet @build @props *> (Join-Path $out "build-$key.log")
    if($LASTEXITCODE -ne 0){throw "Worker compile failed for $key; see build-$key.log. No native code was run."}
    $output=Join-Path (Split-Path $project) "bin/$key/Release/$($release[1])"
    $worker=Join-Path $output "TiaMcp.PlcWorker.$key.exe"
    $adapter=Join-Path $output "TiaMcp.Adapter.$key.dll"
    & $Dotnet $weaver verify $adapter (Join-Path $out "coverage-$key.json") *> (Join-Path $out "verify-$key.log")
    if($LASTEXITCODE -ne 0){throw "Copied Adapter DLL coverage verification failed for $key; see verify-$key.log."}
    $adapters=@(Get-ChildItem -LiteralPath $output -File -Filter 'TiaMcp.Adapter.*.dll')
    if($adapters.Count -ne 1 -or $adapters[0].Name -cne "TiaMcp.Adapter.$key.dll"){throw "Worker output must contain exactly its selected adapter: $output"}
    if(@(Get-ChildItem -LiteralPath $output -File | Where-Object {$_.Name -like 'Siemens*.dll' -or $_.Name -like 'TiaMcp.PlcFoundation*.dll'}).Count){throw "Unexpected Siemens/old Foundation dependency copied to $output"}
    $core=Join-Path $api ($release[2]+'.dll')
    $results+=[ordered]@{releaseKey=$key;targetFramework=$release[1];referenceIdentity=[Reflection.AssemblyName]::GetAssemblyName($core).FullName;referenceSha256=(Get-FileHash -LiteralPath $core).Hash;workerSha256=(Get-FileHash -LiteralPath $worker).Hash;adapterSha256=(Get-FileHash -LiteralPath $adapter).Hash;compile='passed';nativeAcceptance='NOT RUN';nativeCallInstrumentation='Adapter DLL weave and verify required by build';deployment='Use Build-MultiVersion.ps1';scope='PLC foundation worker; full engines remain separate'}
    Write-Output "$key worker + single adapter compiled; native acceptance NOT RUN; ready for foundation bundle tests"
}
$evidence=[ordered]@{generatedAt=[DateTimeOffset]::UtcNow.ToString('o');scope='compile only; no worker/TIA launch, no change to full V20/V21 engines';results=$results}
[IO.File]::WriteAllText((Join-Path $out 'summary.json'),($evidence|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
