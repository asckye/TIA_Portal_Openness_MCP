#Requires -Version 5.1
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$studio = Join-Path $root 'tools/tia-openness-studio'
$configurationTests = Join-Path $studio 'tests/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj'
$versionCatalog = Join-Path $root 'tools/tiaportal-mcp/src/TiaMcp.Logic/Siemens/TiaVersionCatalog.cs'
$processArguments = Join-Path $root 'tools/openness-shared/ProcessArguments.cs'
$opennessEnvironment = Join-Path $root 'tools/openness-shared/OpennessEnvironment.cs'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$references = @('/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', "/r:$wpf\WindowsBase.dll", "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll")
$glass = Join-Path $root 'tools\ui-glass'
$resourceOutput = Join-Path $root 'bin-build\configurator-resources'
New-Item -ItemType Directory -Force -Path $resourceOutput | Out-Null
$metadata = Join-Path $resourceOutput 'DesktopVersion.cs'
[xml]$versionXml = Get-Content (Join-Path $root 'Version.props') -Raw
$release = [string]$versionXml.Project.PropertyGroup.TiaMcpRelease
if ($release -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be X.Y.Z' }
$attributes = @(
    '[assembly: AssemblyTitle("TIA Portal Workbench")]',
    '[assembly: AssemblyDescription("Unified TIA Portal engineering, MCP service and AI client desktop")]'
)
$attributes += '[assembly: AssemblyVersion("' + $release + '")]'
$attributes += '[assembly: AssemblyFileVersion("' + $release + '.0")]'
$attributes += '[assembly: AssemblyInformationalVersion("' + $release + '")]'
$attributes += '[assembly: AssemblyMetadata("TiaMcpRelease", "' + $release + '")]'
[IO.File]::WriteAllText($metadata, "using System.Reflection;`n" + ($attributes -join "`n"), [Text.UTF8Encoding]::new($false))
& $compiler /nologo /target:winexe /optimize+ /utf8output "/out:$root\TiaMcpConfigurator.exe" @references (Join-Path $root 'tools/mcp-configurator/Launcher.cs') $processArguments $metadata
if ($LASTEXITCODE -ne 0) { throw 'Configurator build failed.' }
if ($Test) {
    $output = Join-Path $root 'bin-build\configurator-tests'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $results = & dotnet run --project $configurationTests -c Release -- $output
    $results | Write-Output
    if ($LASTEXITCODE -ne 0) { throw 'Configurator tests failed.' }
    $match = [regex]::Match(($results -join "`n"), '(?m)^Passed: (\d+)\s*$')
    if (!$match.Success) { throw 'Missing configurator test result.' }
    if ([int]$match.Groups[1].Value -lt 157) { throw 'Expected at least 157 configurator checks.' }
    # Record the .NET 10 test host and its desktop/bridge project inputs as well as the launcher.
    $projectInputs = foreach ($project in @('Gui', 'Client', 'Core', 'Contracts', 'Bridge')) {
        Get-ChildItem (Join-Path $studio "src/TiaOpenness.$project") -Recurse -File |
            Where-Object { $_.Extension -in '.cs','.xaml','.csproj' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
    $testInputs = Get-ChildItem (Split-Path -Parent $configurationTests) -File | Where-Object { $_.Extension -in '.cs','.csproj' }
    $inputs = @($projectInputs) + @($testInputs) + @(
        Get-Item (Join-Path $root 'tools/mcp-configurator/Launcher.cs')
        Get-Item $PSCommandPath
        Get-Item (Join-Path $root 'Version.props')
        Get-Item (Join-Path $studio 'Directory.Build.props')
        Get-Item (Join-Path $studio 'tests/Directory.Build.props')
        Get-Item (Join-Path $root 'tools/openness-shared/LocalProcess.cs')
    ) + @(Get-Item $versionCatalog) + @(Get-Item $processArguments) + @(Get-Item $opennessEnvironment) + @(Get-ChildItem $glass -Recurse -File)
    $sourceFiles = @($inputs | Sort-Object FullName | ForEach-Object {
        $bytes = if ($_.Extension -eq ".ttf") { [IO.File]::ReadAllBytes($_.FullName) } else { [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n")) }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        [ordered]@{ path=$_.FullName.Substring($root.Length+1).Replace('\','/'); sha256=$digest }
    })
    $record = [ordered]@{
        generatedAt=[DateTimeOffset]::UtcNow.ToString('o')
        testsPassed=[int]$match.Groups[1].Value
        realTiaAcceptance='NOT PERFORMED; isolated client configuration and WPF tests only'
        executable=[ordered]@{ path='TiaMcpConfigurator.exe'; sha256=(Get-FileHash (Join-Path $root 'TiaMcpConfigurator.exe')).Hash.ToLowerInvariant() }
        sourceFiles=$sourceFiles
    }
    [IO.File]::WriteAllText((Join-Path $root 'manifest/configurator-build.json'), ($record | ConvertTo-Json -Depth 6).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}
