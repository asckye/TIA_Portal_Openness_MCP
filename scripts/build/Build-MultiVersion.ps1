#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$NuGetConfig='',
    [switch]$SkipFullEngines,
    [switch]$Test
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$api=(Resolve-Path -LiteralPath $PublicApiRoot).Path
$env:TIA_MCP_TEST_PUBLIC_API_ROOT=$api
$logs=Join-Path $repo 'bin-build/multi-version'
New-Item -ItemType Directory -Force $logs | Out-Null
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
if(!$SkipFullEngines) {
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') -V20ReferenceRoot (Join-Path $api 'TIA_V20_PublicAPI/V20') -V21ReferenceRoot (Join-Path $api 'TIA_V21_PublicAPI/V21/net48') -Dotnet $Dotnet -Python $Python -NuGetConfig $NuGetConfig
    if($LASTEXITCODE){throw 'Full-engine build failed'}
}
$weaver=Join-Path $repo 'tools/native-call-weaver/NativeCallWeaver.csproj'
& $Dotnet build $weaver -c Release -v:q *> (Join-Path $logs 'weaver.log')
if($LASTEXITCODE){throw 'Native weaver build failed'}
& (Join-Path $PSScriptRoot 'Build-PlcAdapterWorkers.ps1') -PublicApiRoot $api -Dotnet $Dotnet -NuGetConfig $NuGetConfig -UseReferenceAssemblyPackage -EvidenceDirectory (Join-Path $logs 'adapters')
& (Join-Path $PSScriptRoot 'Build-Studio.ps1') -PublicApiRoot $api -Dotnet $Dotnet -NuGetConfig $NuGetConfig -Test:$Test
$hostProject=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/TiaMcpServer.LegacyHost.csproj'
$publish=Join-Path $logs 'foundation-host'
$engineRecord=Get-Content -LiteralPath (Join-Path $repo 'manifest/release-build.json') -Raw | ConvertFrom-Json
$publishArgs=@('publish',$hostProject,'-c','Release','-o',$publish,'-v:q',"-p:Version=$($engineRecord.release)","-p:FileVersion=$($engineRecord.fileVersion)")
if($NuGetConfig){$publishArgs+=('-p:RestoreConfigFile='+(Resolve-Path -LiteralPath $NuGetConfig).Path)}
& $Dotnet @publishArgs *> (Join-Path $logs 'host.log')
if($LASTEXITCODE){throw 'Foundation host publication failed'}
$records=@()
foreach($key in @('14sp1','15.1','16','17','18','19')) {
    $runtime=Join-Path $repo "runtime/v$key"
    $worker=Join-Path $runtime 'worker'
    New-Item -ItemType Directory -Force $runtime,$worker | Out-Null
    Get-ChildItem -LiteralPath $publish -File | Where-Object {$_.Extension -in '.exe','.dll','.config','.json'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $runtime -Force}
    [IO.File]::WriteAllText((Join-Path $runtime 'release-key.txt'),$key,[Text.UTF8Encoding]::new($false))
    $framework=if($key -in @('14sp1','15.1','16')){'net461'}else{'net48'}
    $workerBuild=Join-Path $repo "tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker/bin/$key/Release/$framework"
    Get-ChildItem -LiteralPath $workerBuild -File | Where-Object {$_.Extension -in '.exe','.dll','.config'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $worker -Force}
    if(@(Get-ChildItem -LiteralPath $worker -Filter 'TiaMcp.Adapter.*.dll').Count -ne 1){throw "Exactly one matching adapter is required: $key"}
    & (Join-Path $runtime 'TiaMcpServer.exe') --catalog > (Join-Path $logs "tools-$key.json")
    if($LASTEXITCODE){throw "Catalog failed: $key"}
    $catalog=Get-Content -LiteralPath (Join-Path $logs "tools-$key.json") -Raw | ConvertFrom-Json
    $records+=@{releaseKey=$key;profile='plc-foundation';toolCount=$catalog.tools.Count;nativeAcceptance='NOT RUN'}
}
$studioBuild=Join-Path $repo 'tools/tia-openness-studio/src/TiaOpenness.Gui/bin/Release/net10.0-windows'
$studioOutput=Join-Path $repo 'runtime/studio'
New-Item -ItemType Directory -Force $studioOutput | Out-Null
Get-ChildItem -LiteralPath $studioBuild | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $studioOutput -Recurse -Force}
if(Get-ChildItem -LiteralPath (Join-Path $repo 'runtime') -Recurse -File -Filter 'Siemens.Engineering*.dll'){throw 'Siemens PublicAPI redistribution is forbidden'}
$validation=@{nativeTiaExecuted=$false;studioFunctionalTestsExecuted=[bool]$Test;foundationTransportExecuted=$false}
if($Test) {
    & $Dotnet run --project (Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.LegacyHostTests/TiaMcpServer.LegacyHostTests.csproj') -c Release -- $api (Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcp.Adapters') *> (Join-Path $logs 'foundation-tests.log')
    if($LASTEXITCODE){throw 'Foundation functional tests failed'}
    $fixture=Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.TransportFixture/TransportFixture.csproj'
    & $Dotnet build $fixture -c Release -v:q *> (Join-Path $logs 'fixture-build.log')
    if($LASTEXITCODE){throw 'Transport fixture build failed'}
    $fixtureExe=Join-Path (Split-Path $fixture -Parent) 'bin/Release/net8.0/TransportFixture.exe'
    & $Python (Join-Path $repo 'scripts/checks/Test-FoundationTransport.py') --fixture $fixtureExe --output (Join-Path $logs 'transport') *> (Join-Path $logs 'transport.log')
    if($LASTEXITCODE){throw 'Foundation transport test failed'}
    $validation.foundationTransportExecuted=$true
}
& $Python (Join-Path $repo 'scripts/diagnostics/Audit-VersionTools.py') --public-api-root $api *> (Join-Path $logs 'api-audit.log')
if($LASTEXITCODE){throw 'Per-version tool/API audit failed; build full engines first'}
if($Test) {
    & $Python (Join-Path $repo 'scripts/diagnostics/Audit-ToolUsage.py') *> (Join-Path $logs 'tool-usage.log')
    if($LASTEXITCODE){throw 'All-release usage coverage failed'}
    $validation.toolUsageCoverageExecuted=$true
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $repo 'runtime') -Recurse -File | Where-Object {$_.Extension -in '.exe','.dll','.config','.json','.txt' -and $_.Name -ne 'README.md'} | Sort-Object FullName | ForEach-Object {
    @{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$sourceRoots=@('tools/tiaportal-mcp/src','tools/tiaportal-mcp/tests','tools/openness-shared','tools/tia-openness-studio','tools/native-call-weaver','scripts/build','scripts/checks','scripts/diagnostics','scripts/generate') | ForEach-Object {Join-Path $repo $_}
$algorithm=[Security.Cryptography.SHA256]::Create()
try {
    $sources=@(Get-ChildItem $sourceRoots -Recurse -File | Where-Object {$_.Extension -in '.cs','.csproj','.props','.targets','.xaml','.ps1','.py','.json' -and $_.FullName -notmatch '[\\/](obj|bin|obj-v20|bin-v20)[\\/]'} | Sort-Object FullName | ForEach-Object {
        $bytes=[Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n"))
        @{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');sha256=[BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()}
    })
} finally {$algorithm.Dispose()}
$record=@{createdAt=[DateTimeOffset]::UtcNow.ToString('o');release=$engineRecord.release;fileVersion=$engineRecord.fileVersion;releases=$records;studioReleaseKeys=@('14sp1','15.1','16','17','18','19','20','21');nativeAcceptance='NOT RUN';validation=$validation;files=$files;sourceFiles=$sources}
[IO.File]::WriteAllText((Join-Path $repo 'manifest/multi-version-build.json'),($record | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$deliveryPath=Join-Path $repo 'manifest/delivery.json'
$delivery=Get-Content -LiteralPath $deliveryPath -Raw | ConvertFrom-Json
$delivery | Add-Member -Force NoteProperty multiVersionBuildSha256 (Get-FileHash -LiteralPath (Join-Path $repo 'manifest/multi-version-build.json')).Hash.ToLowerInvariant()
$delivery | Add-Member -Force NoteProperty releaseKeys $record.studioReleaseKeys
[IO.File]::WriteAllText($deliveryPath,($delivery | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output 'Eight release adapters, six foundation runtimes and direct-Openness Studio deployed to runtime/. Native TIA acceptance NOT RUN.'
