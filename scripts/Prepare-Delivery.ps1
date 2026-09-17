#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Release,
    [ValidatePattern('^\d{8}$')][string]$ReleaseDate=(Get-Date -Format 'yyyyMMdd')
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$null=[DateTime]::ParseExact($ReleaseDate,'yyyyMMdd',[Globalization.CultureInfo]::InvariantCulture)
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-Configurator.ps1') -Test
if($LASTEXITCODE){throw 'Configurator validation failed'}
$buildPath=Join-Path $root 'manifest/release-build.json'
$build=Get-Content $buildPath -Raw -Encoding UTF8 | ConvertFrom-Json
$package="TIA_MCP_Delivery_v${Release}_${ReleaseDate}"
function WriteJson($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12).Replace("`r`n", "`n"),[Text.UTF8Encoding]::new($false))}
# Preserve the original engine build record, including its date and test results.
# Package validation still checks every source input and runtime against that record.
$delivery=[ordered]@{
    release=$Release; releaseDate=$ReleaseDate; package=$package
    engineRelease=$build.release; fileVersion=$build.fileVersion
    engineBuildSha256=(Get-FileHash $buildPath).Hash.ToLowerInvariant()
    configuratorBuildSha256=(Get-FileHash (Join-Path $root 'manifest/configurator-build.json')).Hash.ToLowerInvariant()
    generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
}
WriteJson (Join-Path $root 'manifest/delivery.json') $delivery
$manifestPath=Join-Path $root 'manifest/package-manifest.json'
$manifest=Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.bundleVersion=$Release
$manifest.packageName=$package
$manifest.fileVersion=$build.fileVersion
$manifest.refreshedAt=$delivery.generatedAt
$manifest.entrypoints | Add-Member -Force NoteProperty configurator 'TiaMcpConfigurator.exe'
$manifest.validationStatus="Delivery $Release; engine $($build.release) validation retained with exact source/runtime hashes; configurator tested separately; real TIA acceptance pending"
WriteJson $manifestPath $manifest
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Validate-Bundle.ps1') -Strict
if($LASTEXITCODE){throw 'Delivery validation failed'}
Write-Output "Prepared $package using engine $($build.fileVersion). Review and commit before packaging."
