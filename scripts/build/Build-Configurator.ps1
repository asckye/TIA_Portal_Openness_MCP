#Requires -Version 5.1
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('Configurator.cs', 'ConfigCore.cs', 'ClientProfiles.cs') | ForEach-Object { Join-Path $root "tools\mcp-configurator\$_" }
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$references = @('/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', "/r:$wpf\WindowsBase.dll", "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll")
$resource = "/resource:$root\tools\mcp-configurator\MainWindow.xaml,MainWindow.xaml"
& $compiler /nologo /target:winexe /optimize+ /utf8output "/out:$root\TiaMcpConfigurator.exe" @references $resource @sources
if ($LASTEXITCODE -ne 0) { throw 'Configurator build failed.' }
if ($Test) {
    $output = Join-Path $root 'bin-build\configurator-tests'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & $compiler /nologo /target:exe /utf8output /main:TiaMcpConfigurator.Tests "/out:$output\Tests.exe" @references $resource @sources (Join-Path $root 'tools\mcp-configurator\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $results = & "$output\Tests.exe" $output
    $results | Write-Output
    if ($LASTEXITCODE -ne 0) { throw 'Configurator tests failed.' }
    $match = [regex]::Match(($results -join "`n"), '(?m)^Passed: (\d+)\s*$')
    if (!$match.Success) { throw 'Missing configurator test result.' }
    $inputs = @(Get-ChildItem (Join-Path $root 'tools/mcp-configurator') -File | Where-Object { $_.Extension -in '.cs','.xaml' }) + @(Get-Item $PSCommandPath)
    $sourceFiles = @($inputs | Sort-Object FullName | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n")
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','').ToLowerInvariant() }
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
