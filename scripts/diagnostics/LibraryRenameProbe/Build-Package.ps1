[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository ('bin-build\diagnostics\LibraryRenameProbe-V21-r3-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output must be a new directory; existing packages are never overwritten.' }
$zipPath = $OutputDirectory + '.zip'
if (Test-Path -LiteralPath $zipPath) { throw 'ZIP already exists.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$env:DOTNET_CLI_HOME = Join-Path $repository 'bin-build\releases\v3.1.0\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet build (Join-Path $PSScriptRoot 'LibraryRenameProbe.csproj') -c Release -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic probe build failed.' }
& (Join-Path $OutputDirectory 'LibraryRenameProbe.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed.' }
if (@(Get-ChildItem -LiteralPath $OutputDirectory -Filter 'Siemens*.dll' -Recurse).Count -ne 0) { throw 'Siemens redistributable found; refuse packaging.' }
foreach ($file in @('Run-Tests.cmd','Run-Tests.ps1','README.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $OutputDirectory }
$source = New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'source')
foreach ($file in @('Program.cs','Native.cs','LibraryRenameProbe.csproj','Build-Package.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $source.FullName }
$evidenceSets = @(
    @{manifest='manifest\unified-library-rename-followup-20261001.json'; directory='existing-mcp-evidence'},
    @{manifest='manifest\library-rename-probe-first-vm-run-20261001.json'; directory='first-vm-run-evidence'},
    @{manifest='manifest\library-rename-probe-second-vm-run-20261001.json'; directory='second-vm-run-evidence'}
)
foreach ($evidenceSet in $evidenceSets) {
    $evidenceManifest = Join-Path $repository $evidenceSet.manifest
    if (!(Test-Path -LiteralPath $evidenceManifest)) { continue }
    $evidenceDirectory = New-Item -ItemType Directory -Path (Join-Path $OutputDirectory $evidenceSet.directory)
    Copy-Item -LiteralPath $evidenceManifest -Destination $evidenceDirectory.FullName
    $evidence = Get-Content -LiteralPath $evidenceManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $index = @($evidence.evidence | ForEach-Object {
        $file = [IO.Path]::GetFullPath((Join-Path $repository $_.path))
        if (!$file.StartsWith($repository.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence path escaped repository.' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256) { throw 'Evidence hash changed.' }
        Copy-Item -LiteralPath $file -Destination $evidenceDirectory.FullName
        [pscustomobject]@{ key=$_.key; file=[IO.Path]::GetFileName($file); sha256=$_.sha256 }
    })
    $index | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDirectory.FullName 'index.json') -Encoding UTF8
}
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path=$_.FullName.Substring($OutputDirectory.Length + 1); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[pscustomobject]@{ schemaVersion=1; packageRevision=3; builtUtc=[DateTime]::UtcNow.ToString('o'); target='net48 / x64 / TIA V21'; nativeExecution='NOT_RUN_BY_BUILD'; files=$files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-manifest.json') -Encoding UTF8
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $zipPath -CompressionLevel Optimal
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
Write-Host "Package: $zipPath"
