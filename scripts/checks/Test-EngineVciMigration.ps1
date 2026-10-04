param(
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [Parameter(Mandatory=$true)][string]$BaselineDirectory,
    [string]$OutputDirectory = 'bin-build/engine-vci-migration',
    [string[]]$Releases = @('14sp1','15.1','16','17','18','19','20','21'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (!$out.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this worktree.'
}
$baseline = [IO.Path]::GetFullPath($BaselineDirectory)
$sdkRoot = [IO.Path]::GetFullPath($PublicApiRoot)
New-Item -ItemType Directory -Force $out,(Join-Path $out 'empty-feed') | Out-Null
$weaver = Join-Path $repo 'tools/native-call-weaver/bin/Release/net8.0/NativeCallWeaver.dll'
$checker = Join-Path $repo 'scripts/checks/Compare-VciNativePaths.py'
$failedProofs = @()
function Run([string]$Program, [string[]]$Arguments, [string]$Log) {
    & $Program @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { Get-Content $Log -Tail 20; throw "$Program failed: $Log" }
}
function Dump([string]$Assembly, [string]$Inventory, [string]$Directory, [string]$Label) {
    Run 'python' @($checker,'--dump',$Assembly,'--inventory',$Inventory,'--output',$Directory) (Join-Path $out "$Label-dump.log")
    return Join-Path $Directory ((Split-Path $Assembly -Leaf) + '.il.json')
}
Push-Location $repo
try {
    foreach ($key in $Releases) {
        $folder = switch ($key) { '14sp1' {'V14Sp1'} '15.1' {'V15_1'} default {"V$key"} }
        $api = switch ($key) {
            '14sp1' {Join-Path $sdkRoot 'TIA_V14SP1_PublicAPI/V14 SP1'}
            '15.1' {Join-Path $sdkRoot 'TIA_V15.1_PublicAPI/V15.1'}
            '21' {Join-Path $sdkRoot 'TIA_V21_PublicAPI/V21/net48'}
            default {Join-Path $sdkRoot "TIA_V${key}_PublicAPI/V$key"}
        }
        $engine = $key -in '20','21'
        $variants = if ($engine) { @('default','shared') } else { @('shared') }
        foreach ($variant in $variants) {
            $destination = Join-Path $out "$variant/v$key"
            New-Item -ItemType Directory -Force $destination | Out-Null
            if (!$SkipBuild) {
                $project = if ($engine) {"tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V$key.csproj"}
                    else {"tools/tiaportal-mcp/src/TiaMcp.Adapters/$folder/Adapter.$key.csproj"}
                $enabled = if ($variant -eq 'shared') {'true'} else {'false'}
                Run 'dotnet' @('build',$project,'-c','Release',"-p:SiemensEngineeringDirectory=$api",
                    "-p:TiaSharedAdapterPaths=$enabled","-p:RestoreSources=$(Join-Path $out 'empty-feed')",
                    '-p:NuGetAudit=false','-m:1','-nr:false','-p:BuildInParallel=false') (Join-Path $out "$variant-v$key-build.log")
                $source = if ($engine) {
                    if ($key -eq '20') {'tools/tiaportal-mcp/src/TiaMcpServer/bin-v20/Release/net48'}
                    else {'tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48'}
                } else {"tools/tiaportal-mcp/src/TiaMcp.Adapters/$folder/bin/$key/Release/net48"}
                Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
            }
            Run 'dotnet' @($weaver,'verify',(Join-Path $destination "TiaMcp.Adapter.$key.dll"),
                (Join-Path $destination 'adapter-inventory.json')) (Join-Path $out "$variant-v$key-adapter-verify.log")
            if ($engine) {
                Run 'dotnet' @($weaver,'verify',(Join-Path $destination 'TiaMcpServer.exe'),
                    (Join-Path $destination 'engine-inventory.json')) (Join-Path $out "$variant-v$key-engine-verify.log")
            }
        }
        $old = Join-Path $baseline "v$key"
        $current = Join-Path $out "shared/v$key"
        $oldAdapter = Dump (Join-Path $old "TiaMcp.Adapter.$key.dll") (Join-Path $old 'adapter-inventory.json') (Join-Path $out "il/baseline-v$key") "baseline-v$key-adapter"
        $newAdapter = Dump (Join-Path $current "TiaMcp.Adapter.$key.dll") (Join-Path $current 'adapter-inventory.json') (Join-Path $out "il/shared-v$key") "shared-v$key-adapter"
        $arguments = @($checker,'--baseline-adapter',$oldAdapter,'--current-adapter',$newAdapter,
            '--baseline-adapter-inventory',(Join-Path $old 'adapter-inventory.json'),
            '--current-adapter-inventory',(Join-Path $current 'adapter-inventory.json'),
            '--output',(Join-Path $out "proof-v$key.json"))
        if ($engine) {
            $default = Join-Path $out "default/v$key"
            $oldEngine = Dump (Join-Path $old 'TiaMcpServer.exe') (Join-Path $old 'engine-inventory.json') (Join-Path $out "il/baseline-v$key") "baseline-v$key-engine"
            $defaultEngine = Dump (Join-Path $default 'TiaMcpServer.exe') (Join-Path $default 'engine-inventory.json') (Join-Path $out "il/default-v$key") "default-v$key-engine"
            $newEngine = Dump (Join-Path $current 'TiaMcpServer.exe') (Join-Path $current 'engine-inventory.json') (Join-Path $out "il/shared-v$key") "shared-v$key-engine"
            $arguments += @('--baseline-engine',$oldEngine,'--default-engine',$defaultEngine,'--shared-engine',$newEngine,
                '--baseline-engine-inventory',(Join-Path $old 'engine-inventory.json'),
                '--default-engine-inventory',(Join-Path $default 'engine-inventory.json'),
                '--shared-engine-inventory',(Join-Path $current 'engine-inventory.json'))
        }
        & python @arguments *> (Join-Path $out "proof-v$key.log")
        if ($LASTEXITCODE -ne 0) { $failedProofs += $key }
        Get-Content (Join-Path $out "proof-v$key.log") -Tail 4
    }
    if ($failedProofs.Count) { throw "VCI proof failed for: $($failedProofs -join ', ')" }
} finally { Pop-Location }
