param(
    [Parameter(Mandatory=$true)][string]$V20ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$V21ReferenceRoot,
    [string]$Dotnet='dotnet',
    [switch]$NoRestore
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$source=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer'
$out=Join-Path $repo 'bin-build/defects-2.7.2.6'
New-Item -ItemType Directory -Force $out | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $out 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$restoreArgs=@()
if($NoRestore){$restoreArgs=@('--no-restore')}
& $Dotnet run --project (Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj') -c Release @restoreArgs > (Join-Path $out 'offline.log') 2>&1
if($LASTEXITCODE){throw 'Offline regression failed; see offline.log'}
foreach($major in @(20,21)) {
    $api=(Resolve-Path -LiteralPath $(if($major -eq 20){$V20ReferenceRoot}else{$V21ReferenceRoot})).Path
    $project=Join-Path $source $(if($major -eq 20){'TiaMcpServer.V20.csproj'}else{'TiaMcpServer.csproj'})
    $obj=Join-Path $source $(if($major -eq 20){'obj-v20/'}else{'obj/'})
    & $Dotnet build $project -c Release @restoreArgs "-p:SiemensEngineeringDirectory=$api" "-p:BaseIntermediateOutputPath=$obj" "-p:MSBuildProjectExtensionsPath=$obj" -v:q > (Join-Path $out "build-v$major.log") 2>&1
    if($LASTEXITCODE){throw "V$major build failed; see build-v$major.log"}
    $built=Join-Path $source $(if($major -eq 20){'bin-v20/Release/net48'}else{'bin/Release/net48'})
    $runtime=Join-Path $repo "runtime/v$major"
    New-Item -ItemType Directory -Force $runtime | Out-Null
    Get-ChildItem -LiteralPath $built -File | Where-Object { $_.Extension -in '.exe','.dll','.config' -and $_.Name -notlike 'Siemens.Engineering*' } | Copy-Item -Destination $runtime -Force
    $exe=Join-Path $runtime 'TiaMcpServer.exe'
    if((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne '2.7.2.6'){throw 'Unexpected binary version'}
    Write-Output "Built V$major FileVersion=2.7.2.6 SHA256=$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash)"
}
Write-Output 'Compiled only. Real TIA deployment, archive retrieval and project acceptance must be verified separately.'
