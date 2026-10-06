#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [string]$Dotnet='dotnet',
    [string]$Python='python',
    [string]$NuGetConfig='',
    [switch]$SkipFullEngines,
    [switch]$PrepareOnly,
    [switch]$CompleteOnly,
    [switch]$Offline,
    [switch]$Test
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if($PrepareOnly -and $CompleteOnly){throw '-PrepareOnly and -CompleteOnly cannot be combined'}
[xml]$versionXml=Get-Content -LiteralPath (Join-Path $repo 'Version.props') -Raw
$release=[string]$versionXml.Project.PropertyGroup.TiaMcpRelease
$version=$release+'.0'
if($release -notmatch '^\d+\.\d+\.\d+$'){throw 'Public release version must be X.Y.Z without fork or feature suffixes'}
function Read-MultiVersionInputs([switch]$Validation) {
    . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
    if ($Validation) { @(Get-ReleaseValidationInputs $repo 'multi') } else { @(Get-ReleaseSources $repo 'multi') }
}
$sources=@(Read-MultiVersionInputs)
$validationInputs=@(Read-MultiVersionInputs -Validation)
$api=(Resolve-Path -LiteralPath $PublicApiRoot).Path
$env:TIA_MCP_TEST_PUBLIC_API_ROOT=$api
$logs=Join-Path $repo 'bin-build/multi-version'
New-Item -ItemType Directory -Force $logs | Out-Null
$pendingPath=Join-Path $logs 'prepared-build.json'
function Read-RuntimeFiles([switch]$Prepared) {
    . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
    @(Get-ReleaseRuntimeFiles $repo -Prepared:$Prepared)
}
function Assert-PreparedBuild($Record) {
    . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
    Assert-MultiVersionPreparation $repo $Record $release @(Read-MultiVersionInputs) @(Read-MultiVersionInputs -Validation) @(Read-RuntimeFiles -Prepared) $api
}
function Read-EngineReuseReason {
    . (Join-Path $PSScriptRoot 'Release.ps1') -FunctionsOnly
    $Version=$release
    Get-ReleaseReuseReason 'engine'
}
if($CompleteOnly) {
    if(!(Test-Path -LiteralPath $pendingPath)){throw 'Multi-version preparation missing; run Build-MultiVersion.ps1 -PrepareOnly -Test before Build-Release.ps1'}
    $prepared=Get-Content -LiteralPath $pendingPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-PreparedBuild $prepared
    if([bool]$prepared.test -ne [bool]$Test){throw 'Preparation/completion -Test must match; rebuild the preparation'}
    $records=@($prepared.releases)
    $validation=$prepared.validation
} else {
    # Nothing in this stage reads full-engine outputs or previous release records.
    if(Test-Path -LiteralPath $pendingPath){Remove-Item -LiteralPath $pendingPath -Force}
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$weaver=Join-Path $repo 'build-tools/native-call-weaver/NativeCallWeaver.csproj'
& $Dotnet build $weaver -c Release -v:q *> (Join-Path $logs 'weaver.log')
if($LASTEXITCODE){throw 'Native weaver build failed'}
& (Join-Path $PSScriptRoot 'Get-BundledDotnet.ps1') -Offline:$Offline *> (Join-Path $logs 'bundled-dotnet.log')
if(!$?){throw 'Bundled .NET runtime layout failed'}
& (Join-Path $PSScriptRoot 'Build-PlcAdapterWorkers.ps1') -PublicApiRoot $api -Dotnet $Dotnet -NuGetConfig $NuGetConfig -UseReferenceAssemblyPackage -EvidenceDirectory (Join-Path $logs 'adapters')
& (Join-Path $PSScriptRoot 'Build-Studio.ps1') -PublicApiRoot $api -Dotnet $Dotnet -NuGetConfig $NuGetConfig -Test:$Test
$hostProject=Join-Path $repo 'src/FoundationHost/TiaMcpServer.LegacyHost.csproj'
$publish=Join-Path $logs 'foundation-host'
$publishArgs=@('publish',$hostProject,'-c','Release','-o',$publish,'-v:q',"-p:Version=$release","-p:FileVersion=$version")
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
    $framework='net48'
    $workerBuild=Join-Path $repo "src/Worker/bin/$key/Release/$framework"
    Get-ChildItem -LiteralPath $workerBuild -File | Where-Object {$_.Extension -in '.exe','.dll','.config'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $worker -Force}
    if(@(Get-ChildItem -LiteralPath $worker -Filter 'TiaMcp.Adapter.*.dll').Count -ne 1){throw "Exactly one matching adapter is required: $key"}
    & (Join-Path $runtime 'TiaMcp.FoundationHost.exe') --catalog | Out-File -LiteralPath (Join-Path $logs "tools-$key.json") -Encoding utf8
    if($LASTEXITCODE){throw "Catalog failed: $key"}
    $catalog=Get-Content -LiteralPath (Join-Path $logs "tools-$key.json") -Raw | ConvertFrom-Json
    $records+=@{releaseKey=$key;profile='plc-foundation';toolCount=$catalog.tools.Count;nativeAcceptance='NOT RUN'}
}
$studioBuild=Join-Path $repo 'src/Studio/Gui/bin/Release/net10.0-windows'
$studioOutput=Join-Path $repo 'runtime/studio'
New-Item -ItemType Directory -Force $studioOutput | Out-Null
Get-ChildItem -LiteralPath $studioBuild | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $studioOutput -Recurse -Force}
# The build apphost only knows installed .NET; the published one also searches the bundled ../dotnet.
$studioApphost=Join-Path $logs 'studio-apphost'
& $Dotnet publish (Join-Path $repo 'src/Studio/Gui/TiaOpenness.Gui.csproj') -c Release --no-build -o $studioApphost -v:q -p:TiaSharedAdapterPaths=false *> (Join-Path $logs 'studio-apphost.log')
if($LASTEXITCODE){throw 'Studio apphost publication failed'}
Copy-Item -LiteralPath (Join-Path $studioApphost 'TiaOpenness.exe') -Destination $studioOutput -Force
function Assert-BundledRuntime([string]$Exe,[string[]]$Arguments,[string]$Name) {
    $trace=Join-Path $logs "corehost-$Name.txt"
    Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
    $env:COREHOST_TRACE='1'; $env:COREHOST_TRACEFILE=$trace; $env:TIA_OPENNESS_NETWORK_NO_DIALOG='1'
    try {Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -NoNewWindow -RedirectStandardOutput (Join-Path $logs "corehost-$Name.out") -RedirectStandardError (Join-Path $logs "corehost-$Name.err")}
    finally {Remove-Item Env:COREHOST_TRACE,Env:COREHOST_TRACEFILE,Env:TIA_OPENNESS_NETWORK_NO_DIALOG -ErrorAction SilentlyContinue}
    $fxr=[IO.Path]::GetFullPath((Join-Path $repo 'runtime/dotnet/host/fxr'))
    if(!(Select-String -LiteralPath $trace -SimpleMatch -Pattern "Resolved fxr [$fxr" -Quiet)){throw "$Name does not load the bundled .NET runtime; see $trace"}
}
Assert-BundledRuntime (Join-Path $repo 'runtime/v14sp1/TiaMcp.FoundationHost.exe') @('--catalog') 'foundation-host'
# The elevated network helper path exits at once with a handled error, so it proves the GUI host without a window.
Assert-BundledRuntime (Join-Path $studioOutput 'TiaOpenness.exe') @('--network','127.0.0.1','not-a-port','S-1-5-18') 'studio'
if(Get-ChildItem -LiteralPath (Join-Path $repo 'runtime') -Recurse -File -Filter 'Siemens.Engineering*.dll'){throw 'Siemens PublicAPI redistribution is forbidden'}
$validation=@{nativeTiaExecuted=$false;studioFunctionalTestsExecuted=[bool]$Test;configurationFunctionalTestsExecuted=[bool]$Test;foundationTransportExecuted=$false;toolUsageCoverageExecuted=$false}
if($Test) {
    $suiteResults=Join-Path $logs 'dotnet-suites'
    $validation.dotnetSuites=@{}
    foreach($suite in @('foundation-api','prompt-registration','software-read','special-export-shape','device-add','hardware-catalog','diagnostic-membership')) {
        $suiteArgs=@((Join-Path $repo 'scripts/checks/Test-DotnetSuites.py'),'--suite',$suite,'--dotnet',$Dotnet,'--results-directory',$suiteResults)
        if($NuGetConfig){$suiteArgs+=('--dotnet-arg=-p:RestoreConfigFile='+(Resolve-Path -LiteralPath $NuGetConfig).Path)}
        & $Python @suiteArgs *> (Join-Path $logs "$suite-tests.log")
        if($LASTEXITCODE){throw "TRX suite gate failed: $suite"}
        $summary=Get-Content -LiteralPath (Join-Path $suiteResults "$suite.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        $validation.dotnetSuites[$suite]=@{passed=[int]$summary.passed;failed=[int]$summary.failed;skipped=[int]$summary.skipped;total=[int]$summary.total;minimumPassed=[int]$summary.minimumPassed;maximumSkipped=[int]$summary.maximumSkipped}
    }
    $fixture=Join-Path $repo 'tests/Engine/TiaMcpServer.TransportFixture/TransportFixture.csproj'
    & $Dotnet build $fixture -c Release -v:q *> (Join-Path $logs 'fixture-build.log')
    if($LASTEXITCODE){throw 'Transport fixture build failed'}
    $fixtureExe=Join-Path (Split-Path $fixture -Parent) 'bin/Release/net10.0/TransportFixture.exe'
    & $Python (Join-Path $repo 'scripts/checks/Test-FoundationTransport.py') --fixture $fixtureExe --output (Join-Path $logs 'transport') *> (Join-Path $logs 'transport.log')
    if($LASTEXITCODE){throw 'Foundation transport test failed'}
    $validation.foundationTransportExecuted=$true
}
# Bind the preparation to inputs, binaries and the transport/adapter audit evidence.
$preparedFiles=@(Read-RuntimeFiles -Prepared)
$evidence=@(Get-ChildItem -LiteralPath (Join-Path $logs 'adapters') -Filter 'coverage-*.json' -File)
$evidence+=@(Get-ChildItem -LiteralPath $logs -Filter 'tools-*.json' -File)
if($Test){$evidence+=Get-Item -LiteralPath (Join-Path $logs 'transport/tool-usage.json')}
$evidenceRows=@($evidence | Sort-Object FullName | ForEach-Object {
    @{path=$_.FullName.Substring($repo.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$prepared=@{release=$release;fileVersion=$version;publicApiRoot=$api;test=[bool]$Test;releases=$records;validation=$validation;sourceFiles=$sources;validationInputs=$validationInputs;files=$preparedFiles;evidence=$evidenceRows}
Assert-PreparedBuild $prepared
[IO.File]::WriteAllText($pendingPath,($prepared | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
if($PrepareOnly){Write-Output 'Multi-version preparation passed; build/reuse the full engines, then run -CompleteOnly with the same -Test setting.';return}
if(!$SkipFullEngines) {
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') -V20ReferenceRoot (Join-Path $api 'TIA_V20_PublicAPI/V20') -V21ReferenceRoot (Join-Path $api 'TIA_V21_PublicAPI/V21/net48') -Dotnet $Dotnet -Python $Python -NuGetConfig $NuGetConfig
    if($LASTEXITCODE){throw 'Full-engine build failed'}
}
}
$engineReason=Read-EngineReuseReason
if($engineReason){throw "Full-engine inputs, binaries or audit evidence do not match: $engineReason; run Build-Release.ps1 before multi-version completion"}
$deliveryPath=Join-Path $repo 'manifest/delivery.json'
if(!(Test-Path -LiteralPath $deliveryPath)){throw 'Delivery record missing; run Prepare-Delivery.ps1 before multi-version completion'}
$delivery=Get-Content -LiteralPath $deliveryPath -Raw | ConvertFrom-Json
if($delivery.release -ne $release -or $delivery.fileVersion -ne $version -or $delivery.engineBuildSha256 -ne (Get-FileHash -LiteralPath (Join-Path $repo 'manifest/release-build.json')).Hash){throw 'Delivery does not bind the current engine record; run Prepare-Delivery.ps1 before multi-version completion'}
& $Python (Join-Path $repo 'scripts/diagnostics/Audit-VersionTools.py') --public-api-root $api *> (Join-Path $logs 'api-audit.log')
if($LASTEXITCODE){throw 'Per-version tool/API audit failed; build full engines first'}
if($Test) {
    & $Python (Join-Path $repo 'scripts/diagnostics/Audit-ToolUsage.py') *> (Join-Path $logs 'tool-usage.log')
    if($LASTEXITCODE){throw 'All-release usage coverage failed'}
    $validation.toolUsageCoverageExecuted=$true
}
$files=@(Read-RuntimeFiles)
Assert-PreparedBuild $prepared
$currentValidation=@(Read-MultiVersionInputs -Validation)
if(($validationInputs|ConvertTo-Json -Depth 4 -Compress) -cne ($currentValidation|ConvertTo-Json -Depth 4 -Compress)){throw 'Validation inputs changed during the build; refusing to record results'}
$currentInputs=@(Read-MultiVersionInputs)
if(($sources|ConvertTo-Json -Depth 4 -Compress) -cne ($currentInputs|ConvertTo-Json -Depth 4 -Compress)){throw 'Multi-version inputs changed during validation; refusing to record results'}
$engineReason=Read-EngineReuseReason
if($engineReason){throw "Full-engine inputs or evidence changed during multi-version audits: $engineReason"}
if(($files|ConvertTo-Json -Depth 4 -Compress) -cne (@(Read-RuntimeFiles)|ConvertTo-Json -Depth 4 -Compress)){throw 'Runtime files changed during multi-version audits; refusing to record results'}
$record=@{createdAt=[DateTimeOffset]::UtcNow.ToString('o');release=$release;fileVersion=$version;releases=$records;studioReleaseKeys=@('14sp1','15.1','16','17','18','19','20','21');nativeAcceptance='NOT RUN';validation=$validation;files=$files;sourceFiles=$sources;validationInputs=$validationInputs}
[IO.File]::WriteAllText((Join-Path $repo 'manifest/multi-version-build.json'),($record | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$delivery | Add-Member -Force NoteProperty configuratorBuildSha256 (Get-FileHash -LiteralPath (Join-Path $repo 'manifest/configurator-build.json')).Hash.ToLowerInvariant()
$delivery | Add-Member -Force NoteProperty multiVersionBuildSha256 (Get-FileHash -LiteralPath (Join-Path $repo 'manifest/multi-version-build.json')).Hash.ToLowerInvariant()
$delivery | Add-Member -Force NoteProperty releaseKeys $record.studioReleaseKeys
[IO.File]::WriteAllText($deliveryPath,($delivery | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output 'Eight release adapters, six foundation runtimes, direct-Openness Studio and the bundled .NET runtime deployed to runtime/. Native TIA acceptance NOT RUN.'
