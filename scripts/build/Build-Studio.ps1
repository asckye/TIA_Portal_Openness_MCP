param(
    [string]$V20ReferenceRoot,
    [string]$V21ReferenceRoot,
    [string]$PublicApiRoot,
    [string[]]$ReleaseKeys,
    [string]$Dotnet='dotnet',
    [string]$NuGetConfig='',
    [switch]$Test
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$studio=Join-Path $repo 'tools/tia-openness-studio'
$logs=Join-Path $repo 'bin-build/studio-native'
New-Item -ItemType Directory -Force $logs | Out-Null
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
function RunDotnet([string[]]$Arguments,[string]$Log) {
    if($NuGetConfig){$Arguments+=('-p:RestoreConfigFile='+(Resolve-Path -LiteralPath $NuGetConfig).Path)}
    & $Dotnet @Arguments > (Join-Path $logs $Log) 2>&1
    if($LASTEXITCODE){throw "Studio build/test failed; see $logs/$Log"}
}
RunDotnet @('build',(Join-Path $studio 'src/TiaOpenness.Gui/TiaOpenness.Gui.csproj'),'-c','Release','--nologo') 'gui.log'
$app=Join-Path $studio 'src/TiaOpenness.Gui/bin/Release/net10.0-windows'
$native=@()
if(!$ReleaseKeys){$ReleaseKeys=if($PublicApiRoot){@('14sp1','15.1','16','17','18','19','20','21')}else{@('20','21')}}
foreach($key in $ReleaseKeys) {
    $folder=switch($key){'14sp1'{'V14Sp1'} '15.1'{'V15_1'} default{'V'+$key}}
    $relative=switch($key){'14sp1'{'TIA_V14SP1_PublicAPI/V14 SP1'} '15.1'{'TIA_V15.1_PublicAPI/V15.1'} '21'{'TIA_V21_PublicAPI/V21/net48'} default{"TIA_V${key}_PublicAPI/V$key"}}
    $inputApi=if($key -eq '20' -and $V20ReferenceRoot){$V20ReferenceRoot}elseif($key -eq '21' -and $V21ReferenceRoot){$V21ReferenceRoot}elseif($PublicApiRoot){Join-Path $PublicApiRoot $relative}else{throw "Missing PublicAPI root for $key"}
    $api=(Resolve-Path -LiteralPath $inputApi).Path
    $project=Join-Path $studio "src/TiaOpenness.Openness/$folder/StudioOpenness.$folder.csproj"
    RunDotnet @('build',$project,'-c','Release','--nologo',"-p:SiemensEngineeringDirectory=$api") "native-v$key.log"
    $built=Join-Path (Split-Path $project -Parent) 'bin/Release/net48'
    if(Get-ChildItem -LiteralPath $built -Filter 'Siemens.*.dll' -File){throw 'Siemens assemblies must not be copied into the Studio build'}
    $destination=Join-Path $app "bridge/adapters/v$key"
    New-Item -ItemType Directory -Force $destination | Out-Null
    $assembly=Join-Path $built 'TiaOpenness.Openness.dll'
    Copy-Item -LiteralPath $assembly -Destination $destination -Force
    $native+=@{releaseKey=$key;assemblySha256=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant();nativeAcceptance='NOT RUN'}
}
if($Test) {
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj'),'-c','Release','--nologo') 'client-tests.log'
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj'),'-c','Release','--nologo') 'gui-tests.log'
}
if(Get-ChildItem -LiteralPath $app -Recurse -File -Filter 'Siemens.*.dll'){throw 'Studio output contains a Siemens assembly'}
$record=@{createdAt=[DateTimeOffset]::UtcNow.ToString('o');app=$app;nativeAdapters=$native;functionalTestsExecuted=[bool]$Test;nativeTiaExecuted=$false}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $logs 'result.json') -Encoding UTF8
Write-Output "Studio and selected direct Openness adapters built: $app"
Write-Output 'Native TIA/project acceptance NOT RUN. Start TiaOpenness.exe --mock for the synthetic workflow.'
