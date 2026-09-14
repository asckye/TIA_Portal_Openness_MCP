param(
    [Parameter(Mandatory=$true)][string]$V20ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$V21ReferenceRoot,
    [string]$Dotnet = 'dotnet',
    [string]$NugetConfig = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer'
$delivery = Join-Path $repo 'bin-build/multilingual-fix-2.7.2-asckye.5-multilingual.1'
New-Item -ItemType Directory -Force $delivery | Out-Null
$restore = @()
if ($NugetConfig) { $restore = @('--configfile', (Resolve-Path -LiteralPath $NugetConfig).Path) }
$tests = Join-Path $repo 'tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj'
& $Dotnet restore $tests @restore
if ($LASTEXITCODE) { throw 'Test restore failed' }
& $Dotnet run --no-restore --project $tests -c Release 2>&1 | Tee-Object (Join-Path $delivery 'offline-tests.log')
if ($LASTEXITCODE) { throw 'Offline tests failed' }
$manifest = @()
foreach ($version in @(20,21)) {
    $referenceRoot = if ($version -eq 20) { $V20ReferenceRoot } else { $V21ReferenceRoot }
    $referenceRoot = (Resolve-Path -LiteralPath $referenceRoot).Path
    $project = if ($version -eq 20) { 'TiaMcpServer.V20.csproj' } else { 'TiaMcpServer.csproj' }
    $obj = Join-Path $source $(if ($version -eq 20) { 'obj-v20/' } else { 'obj/' })
    # Absolute early properties keep V20 and V21 NuGet assets separate.
    & $Dotnet build (Join-Path $source $project) -c Release @restore "-p:TiaPortalLocation=$referenceRoot" "-p:BaseIntermediateOutputPath=$obj" "-p:MSBuildProjectExtensionsPath=$obj" 2>&1 |
        Tee-Object (Join-Path $delivery "build-v$version.log")
    if ($LASTEXITCODE) { throw "V$version build failed; no deployable package declared" }
    $output = Join-Path $source $(if ($version -eq 20) { 'bin-v20/Release/net48' } else { 'bin/Release/net48' })
    $destination = Join-Path $delivery "v$version"
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem -LiteralPath $output -File | Copy-Item -Destination $destination
    foreach ($file in Get-ChildItem -LiteralPath $destination -File) {
        $manifest += [ordered]@{ tiaVersion=$version; file="v$version/$($file.Name)"; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
}
[ordered]@{
    build='2.7.2-asckye.5-multilingual.1'; fileVersion='2.7.2.5';
    baselineCommit='2b4f70c'; liveAcceptance='NOT PERFORMED'; files=$manifest
} | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $delivery 'manifest.json')
Copy-Item -LiteralPath (Join-Path $repo 'docs/TIA_MCP_多语言修复与验收报告.md') -Destination $delivery
Compress-Archive -Path (Join-Path $delivery '*') -DestinationPath "$delivery.zip" -Force
Write-Output "Built package: $delivery.zip (not deployed or live-accepted)"
