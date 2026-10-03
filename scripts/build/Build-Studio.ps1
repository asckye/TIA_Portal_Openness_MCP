param(
    [Parameter(Mandatory=$true)][string]$V20ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$V21ReferenceRoot,
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
foreach($major in @(20,21)) {
    $api=(Resolve-Path -LiteralPath $(if($major -eq 20){$V20ReferenceRoot}else{$V21ReferenceRoot})).Path
    $project=Join-Path $studio "src/TiaOpenness.Openness/V$major/StudioOpenness.V$major.csproj"
    RunDotnet @('build',$project,'-c','Release','--nologo',"-p:SiemensEngineeringDirectory=$api") "native-v$major.log"
    $built=Join-Path (Split-Path $project -Parent) 'bin/Release/net48'
    if(Get-ChildItem -LiteralPath $built -Filter 'Siemens.*.dll' -File){throw 'Siemens assemblies must not be copied into the Studio build'}
    $destination=Join-Path $app "bridge/adapters/v$major"
    New-Item -ItemType Directory -Force $destination | Out-Null
    $assembly=Join-Path $built 'TiaOpenness.Openness.dll'
    Copy-Item -LiteralPath $assembly -Destination $destination -Force
    $native+=@{version=$major;assemblySha256=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant();nativeAcceptance='NOT RUN'}
}
if($Test) {
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj'),'-c','Release','--nologo') 'client-tests.log'
    RunDotnet @('test',(Join-Path $studio 'tests/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj'),'-c','Release','--nologo') 'gui-tests.log'
}
if(Get-ChildItem -LiteralPath $app -Recurse -File -Filter 'Siemens.*.dll'){throw 'Studio output contains a Siemens assembly'}
$record=@{createdAt=[DateTimeOffset]::UtcNow.ToString('o');app=$app;nativeAdapters=$native;functionalTestsExecuted=[bool]$Test;nativeTiaExecuted=$false}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $logs 'result.json') -Encoding UTF8
Write-Output "Studio and V20/V21 direct Openness adapters built: $app"
Write-Output 'Native TIA/project acceptance NOT RUN. Start TiaOpenness.exe --mock for the synthetic workflow.'
