param(
    [string]$V20ReferenceRoot,
    [string]$V21ReferenceRoot,
    [string]$PublicApiRoot,
    [string[]]$ReleaseKeys,
    [string]$Dotnet='dotnet',
    [string]$NuGetConfig='',
    [ValidateSet('true','false')][string]$TiaSharedAdapterPaths,
    [switch]$Test
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$studio=Join-Path $repo 'tools/tia-openness-studio'
if(!$TiaSharedAdapterPaths){
    [xml]$sharedProps=Get-Content -Raw (Join-Path $repo 'tools/openness-shared/TiaSharedAdapterPaths.props')
    $TiaSharedAdapterPaths=[string]$sharedProps.Project.PropertyGroup.TiaSharedAdapterPaths.'#text'
}
$shared=$TiaSharedAdapterPaths -eq 'true'
$outputSubdirectory=if($shared){'shared-adapter/'}else{''}
$logs=Join-Path $repo 'bin-build/studio-native'
if($shared){$logs=Join-Path $repo 'bin-build/studio-shared-adapter'}
New-Item -ItemType Directory -Force $logs | Out-Null
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$configurationArguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Build-Configurator.ps1'))
if($Test -and !$shared){$configurationArguments+='-Test'}
& powershell.exe @configurationArguments
if($LASTEXITCODE){throw 'Desktop launcher/configuration module build failed'}
function RunDotnet([string[]]$Arguments,[string]$Log) {
    $Arguments+="-p:TiaSharedAdapterPaths=$TiaSharedAdapterPaths"
    if($NuGetConfig){$Arguments+=('-p:RestoreConfigFile='+(Resolve-Path -LiteralPath $NuGetConfig).Path)}
    & $Dotnet @Arguments > (Join-Path $logs $Log) 2>&1
    if($LASTEXITCODE){throw "Studio build/test failed; see $logs/$Log"}
}
RunDotnet @('build',(Join-Path $studio 'src/TiaOpenness.Gui/TiaOpenness.Gui.csproj'),'-c','Release','--nologo') 'gui.log'
$app=Join-Path $studio "src/TiaOpenness.Gui/bin/Release/${outputSubdirectory}net10.0-windows"
$legacyJson=Get-ChildItem -LiteralPath $app -Recurse -File -Filter 'Newtonsoft.Json.dll' | Where-Object {
    !$shared -or !$_.FullName.StartsWith((Join-Path $app 'bridge/adapters')+[IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
if($legacyJson){throw 'Studio payload must use only System.Text.Json; remove stale Newtonsoft output before packaging'}
if (!(Test-Path -LiteralPath (Join-Path $app 'TiaMcp.WorkerChannel.dll'))) { throw 'Studio client worker channel is missing' }
foreach ($name in @('TiaMcp.WorkerChannel.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $app "bridge/$name"))) { throw "Studio bridge channel dependency is missing: $name" }
}
$native=@()
if($shared){
    RunDotnet @('build',(Join-Path $repo 'tools/native-call-weaver/NativeCallWeaver.csproj'),'-c','Release','--nologo') 'weaver.log'
    $weaver=Join-Path $repo 'tools/native-call-weaver/bin/Release/net8.0/NativeCallWeaver.dll'
}
if(!$ReleaseKeys){$ReleaseKeys=if($PublicApiRoot){@('14sp1','15.1','16','17','18','19','20','21')}else{@('20','21')}}
foreach($key in $ReleaseKeys) {
    $folder=switch($key){'14sp1'{'V14Sp1'} '15.1'{'V15_1'} default{'V'+$key}}
    $relative=switch($key){'14sp1'{'TIA_V14SP1_PublicAPI/V14 SP1'} '15.1'{'TIA_V15.1_PublicAPI/V15.1'} '21'{'TIA_V21_PublicAPI/V21/net48'} default{"TIA_V${key}_PublicAPI/V$key"}}
    $inputApi=if($key -eq '20' -and $V20ReferenceRoot){$V20ReferenceRoot}elseif($key -eq '21' -and $V21ReferenceRoot){$V21ReferenceRoot}elseif($PublicApiRoot){Join-Path $PublicApiRoot $relative}else{throw "Missing PublicAPI root for $key"}
    $api=(Resolve-Path -LiteralPath $inputApi).Path
    $project=if($shared){Join-Path $repo "tools/tiaportal-mcp/src/TiaMcp.Adapters/$folder/Adapter.$key.csproj"}else{Join-Path $studio "src/TiaOpenness.Openness/$folder/StudioOpenness.$folder.csproj"}
    RunDotnet @('build',$project,'-c','Release','--nologo',"-p:SiemensEngineeringDirectory=$api") "native-v$key.log"
    $built=Join-Path (Split-Path $project -Parent) $(if($shared){"bin/$key/Release/net48"}else{'bin/Release/net48'})
    if(Get-ChildItem -LiteralPath $built -Filter 'Siemens.*.dll' -File){throw 'Siemens assemblies must not be copied into the Studio build'}
    $destination=Join-Path $app "bridge/adapters/v$key"
    New-Item -ItemType Directory -Force $destination | Out-Null
    if($shared){
        $assembly=Join-Path $built "TiaMcp.Adapter.$key.dll"
        Get-ChildItem -LiteralPath $built -Filter '*.dll' -File | Copy-Item -Destination $destination -Force
        $packaged=Join-Path $destination "TiaMcp.Adapter.$key.dll"
        & $Dotnet $weaver verify $packaged > (Join-Path $logs "weave-v$key.log") 2>&1
        if($LASTEXITCODE){throw "Packaged Studio adapter weave verification failed: $packaged"}
    }else{
        $assembly=Join-Path $built 'TiaOpenness.Openness.dll'
        Copy-Item -LiteralPath $assembly -Destination $destination -Force
    }
    $native+=@{releaseKey=$key;assemblySha256=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant();nativeAcceptance='NOT RUN'}
}
if($Test) {
    RunDotnet @('build',(Join-Path $studio 'tests/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj'),'-c','Release','--nologo') 'configuration-build.log'
    & (Join-Path $studio "tests/TiaOpenness.Configuration.Tests/bin/Release/${outputSubdirectory}net10.0-windows/TiaOpenness.Configuration.Tests.exe") (Join-Path $logs 'configuration-tests') > (Join-Path $logs 'configuration-tests.log') 2>&1
    if($LASTEXITCODE){throw "Embedded configuration tests failed; see $logs/configuration-tests.log"}
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj'),'-c','Release','--nologo') 'client-tests.log'
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj'),'-c','Release','--nologo') 'gui-tests.log'
}
if(Get-ChildItem -LiteralPath $app -Recurse -File -Filter 'Siemens.*.dll'){throw 'Studio output contains a Siemens assembly'}
$record=@{createdAt=[DateTimeOffset]::UtcNow.ToString('o');app=$app;nativeAdapters=$native;functionalTestsExecuted=[bool]$Test;nativeTiaExecuted=$false}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $logs 'result.json') -Encoding UTF8
Write-Output "Studio and selected direct Openness adapters built: $app"
Write-Output 'Native TIA/project acceptance NOT RUN. Start TiaOpenness.exe --mock for the synthetic workflow.'
