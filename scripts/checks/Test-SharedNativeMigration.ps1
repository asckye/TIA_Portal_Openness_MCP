param(
    [Parameter(Mandatory=$true)][string]$Config,
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [Parameter(Mandatory=$true)][string]$BaselineDirectory,
    [string]$OutputDirectory,
    [string]$BaselineRef = 'master',
    [switch]$SkipBaselineBuild,
    [string[]]$Releases,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$configPath = (Resolve-Path -LiteralPath $Config).Path
$domain = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
$checker = Join-Path $repo 'scripts/checks/Compare-SharedNativePaths.py'
& python $checker --config $configPath --validate-config
if ($LASTEXITCODE -ne 0) { throw 'Invalid domain configuration' }
if (!$OutputDirectory) { $OutputDirectory = "bin-build/shared-native/$($domain.domain)" }
if (!$Releases) { $Releases = @($domain.adapter.releases) }
foreach ($key in $Releases) {
    if ($key -notin $domain.adapter.releases) { throw "Release not configured for $($domain.domain): $key" }
}
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (!$out.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this worktree.'
}
$baseline = [IO.Path]::GetFullPath($BaselineDirectory)
if ($baseline -eq $out) { throw 'BaselineDirectory and OutputDirectory must be different.' }
$sdkRoot = [IO.Path]::GetFullPath($PublicApiRoot)
New-Item -ItemType Directory -Force $out,(Join-Path $out 'empty-feed') | Out-Null
$failedProofs = @()
function Run([string]$Program, [string[]]$Arguments, [string]$Log) {
    & $Program @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { Get-Content $Log -Tail 20; throw "$Program failed: $Log" }
}
function Dump([string]$Assembly, [string]$Inventory, [string]$Directory, [string]$Label) {
    Run 'python' @($checker,'--config',$configPath,'--dump',$Assembly,'--inventory',$Inventory,'--output',$Directory) (Join-Path $out "$Label-dump.log")
    return Join-Path $Directory ((Split-Path $Assembly -Leaf) + '.il.json')
}
function Verify-Variant([string]$Directory, [string]$Label) {
    $weaver = Join-Path $Directory 'weaver/NativeCallWeaver.dll'
    Run 'dotnet' @($weaver,'verify',(Join-Path $Directory "TiaMcp.Adapter.$key.dll"),
        (Join-Path $Directory 'adapter-inventory.json')) (Join-Path $out "$Label-adapter-verify.log")
    if ($engine) {
        Run 'dotnet' @($weaver,'verify',(Join-Path $Directory 'TiaMcpServer.exe'),
            (Join-Path $Directory 'engine-inventory.json')) (Join-Path $out "$Label-engine-verify.log")
    }
}
function Build-Variant([string]$SourceRepo, [string]$Destination, [string]$Label) {
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Push-Location $SourceRepo
    try {
        $project = if ($engine) {"tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V$key.csproj"}
            else {"tools/tiaportal-mcp/src/TiaMcp.Adapters/$folder/Adapter.$key.csproj"}
        $enabled = if ($variant -eq 'shared') {'true'} else {'false'}
        Run 'dotnet' @('build',$project,'-c','Release',"-p:SiemensEngineeringDirectory=$api",
            "-p:TiaSharedAdapterPaths=$enabled","-p:RestoreSources=$(Join-Path $out 'empty-feed')",
            '-p:NuGetAudit=false','-m:1','-nr:false','-p:BuildInParallel=false') (Join-Path $out "$Label-build.log")
        $source = if ($engine) {
            if ($key -eq '20') {'tools/tiaportal-mcp/src/TiaMcpServer/bin-v20/Release/net48'}
            else {'tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48'}
        } else {"tools/tiaportal-mcp/src/TiaMcp.Adapters/$folder/bin/$key/Release/net48"}
        Copy-Item -Path (Join-Path $source '*') -Destination $Destination -Recurse -Force
        # The verifier checks its own binary fingerprint; retain the exact
        # instrumenter used by this source tree for later replay.
        $savedWeaver = Join-Path $Destination 'weaver'
        New-Item -ItemType Directory -Force $savedWeaver | Out-Null
        # Baselines checked out from before the .NET 10 move build the weaver for net8.0.
        $weaverOutput = @('net10.0', 'net8.0') | ForEach-Object { "tools/native-call-weaver/bin/Release/$_" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$weaverOutput) { throw 'Native call weaver output not found' }
        Copy-Item -Path (Join-Path $weaverOutput '*') -Destination $savedWeaver -Recurse -Force
    } finally { Pop-Location }
}
Push-Location $repo
try {
    $baselineRecord = Join-Path $baseline 'baseline.json'
    if (!$SkipBuild -and !$SkipBaselineBuild) {
        if (!$baseline.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'BaselineDirectory must be inside this worktree when building baselines.'
        }
        $baselineRevision = (& git rev-parse --verify "$BaselineRef^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot resolve baseline revision: $BaselineRef" }
        New-Item -ItemType Directory -Force $baseline | Out-Null
        if (Test-Path -LiteralPath $baselineRecord) {
            $saved = Get-Content -Raw -LiteralPath $baselineRecord | ConvertFrom-Json
            if ($saved.revision -ne $baselineRevision) { throw 'Use a new BaselineDirectory for a different revision.' }
        }
        $baselineSource = Join-Path $out ("baseline-source-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $baselineSource | Out-Null
        $archive = Join-Path $baselineSource 'source.tar'
        Run 'git' @('archive','--format=tar',"--output=$archive",$baselineRevision) (Join-Path $out 'baseline-archive.log')
        Run 'tar' @('-xf',$archive,'-C',$baselineSource) (Join-Path $out 'baseline-extract.log')
        @{ revision = $baselineRevision } | ConvertTo-Json | Set-Content -LiteralPath $baselineRecord -Encoding utf8
    } else {
        $baselineRevision = (Get-Content -Raw -LiteralPath $baselineRecord | ConvertFrom-Json).revision
        if (!$baselineRevision) { throw 'Saved baselines must record their source revision.' }
    }
    Run 'dotnet' @('build','tools/native-call-weaver/NativeCallWeaver.csproj','-c','Release',
        "-p:RestoreSources=$(Join-Path $out 'empty-feed')",'-p:NuGetAudit=false','-m:1','-nr:false') (Join-Path $out 'weaver-build.log')
    Run 'python' @($checker,'--self-test') (Join-Path $out 'checker-self-test.log')
    Run 'python' @((Join-Path $repo 'scripts/checks/Test-SharedNativeIlReader.py')) (Join-Path $out 'reader-self-test.log')
    foreach ($key in $Releases) {
        $folder = switch ($key) { '14sp1' {'V14Sp1'} '15.1' {'V15_1'} default {"V$key"} }
        $api = switch ($key) {
            '14sp1' {Join-Path $sdkRoot 'TIA_V14SP1_PublicAPI/V14 SP1'}
            '15.1' {Join-Path $sdkRoot 'TIA_V15.1_PublicAPI/V15.1'}
            '21' {Join-Path $sdkRoot 'TIA_V21_PublicAPI/V21/net48'}
            default {Join-Path $sdkRoot "TIA_V${key}_PublicAPI/V$key"}
        }
        $engine = $key -in $domain.engine.releases
        $variants = if ($engine) { @('default','shared') } else { @('shared') }
        foreach ($variant in $variants) {
            if (!$SkipBuild -and !$SkipBaselineBuild) {
                Build-Variant $baselineSource (Join-Path $baseline "$variant/v$key") "baseline-$variant-v$key"
            }
            $destination = Join-Path $out "$variant/v$key"
            if (!$SkipBuild) { Build-Variant $repo $destination "$variant-v$key" }
            Verify-Variant (Join-Path $baseline "$variant/v$key") "baseline-$variant-v$key"
            Verify-Variant $destination "$variant-v$key"
        }
        $old = Join-Path $baseline "shared/v$key"
        $current = Join-Path $out "shared/v$key"
        $oldAdapter = Dump (Join-Path $old "TiaMcp.Adapter.$key.dll") (Join-Path $old 'adapter-inventory.json') (Join-Path $out "il/baseline-shared-v$key") "baseline-v$key-adapter"
        $newAdapter = Dump (Join-Path $current "TiaMcp.Adapter.$key.dll") (Join-Path $current 'adapter-inventory.json') (Join-Path $out "il/shared-v$key") "shared-v$key-adapter"
        $arguments = @($checker,'--config',$configPath,'--release',$key,'--baseline-adapter',$oldAdapter,'--current-adapter',$newAdapter,
            '--baseline-adapter-inventory',(Join-Path $old 'adapter-inventory.json'),
            '--current-adapter-inventory',(Join-Path $current 'adapter-inventory.json'),
            '--baseline-revision',$baselineRevision,'--output',(Join-Path $out "proof-v$key.json"))
        if ($engine) {
            $default = Join-Path $out "default/v$key"
            $oldDefault = Join-Path $baseline "default/v$key"
            $oldDefaultEngine = Dump (Join-Path $oldDefault 'TiaMcpServer.exe') (Join-Path $oldDefault 'engine-inventory.json') (Join-Path $out "il/baseline-default-v$key") "baseline-default-v$key-engine"
            $oldDefaultAdapter = Dump (Join-Path $oldDefault "TiaMcp.Adapter.$key.dll") (Join-Path $oldDefault 'adapter-inventory.json') (Join-Path $out "il/baseline-default-v$key") "baseline-default-v$key-adapter"
            $defaultAdapter = Dump (Join-Path $default "TiaMcp.Adapter.$key.dll") (Join-Path $default 'adapter-inventory.json') (Join-Path $out "il/default-v$key") "default-v$key-adapter"
            $arguments += @('--baseline-default-engine',$oldDefaultEngine,
                '--baseline-default-engine-inventory',(Join-Path $oldDefault 'engine-inventory.json'),
                '--baseline-default-adapter',$oldDefaultAdapter,
                '--baseline-default-adapter-inventory',(Join-Path $oldDefault 'adapter-inventory.json'),
                '--default-adapter',$defaultAdapter)
            $oldEngine = Dump (Join-Path $old 'TiaMcpServer.exe') (Join-Path $old 'engine-inventory.json') (Join-Path $out "il/baseline-shared-v$key") "baseline-v$key-engine"
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
    if ($failedProofs.Count) { throw "$($domain.domain) proof failed for: $($failedProofs -join ', ')" }
    if (@($domain.adapter.releases | Where-Object { $_ -notin $Releases }).Count -eq 0) {
        Run 'python' @($checker,'--config',$configPath,'--evidence-from',$out,
            '--output',(Join-Path $out 'evidence.json')) (Join-Path $out 'evidence.log')
        Get-Content (Join-Path $out 'evidence.log')
    }
} finally { Pop-Location }
