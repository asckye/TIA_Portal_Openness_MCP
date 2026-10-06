#Requires -Version 5.1
<#
.SYNOPSIS
    Offline validation: required files, JSON parse, blueprint bundle list, tool roster count.
.DESCRIPTION
    Run from any directory. Default bundle root = two levels above this script's folder (the delivery package root).
    Does not start TiaMcpServer or TIA Portal.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\checks\Validate-Bundle.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\checks\Validate-Bundle.ps1 -BundleRoot "D:\kits\TIA_MCP_交付包"
.PARAMETER NoBinaries
    Source checkout without build outputs (CI, or a fresh clone before Build-Release.ps1): since 2.8.1 runtime\v20,
    runtime\v21 and TiaOpenness.exe are not tracked in Git, so their presence, versions and hashes are skipped;
    manifests, versions, launchers' syntax and the recorded source hashes are still checked.
.PARAMETER PackageMode
    Validate an extracted runtime-only delivery with no development tree. Also detected when Version.props is absent.
    Run this checker from the repository; check scripts are not shipped.
.PARAMETER SkipSourceHashes
    Skip recorded source-file existence and hash comparisons in build manifests for push/PR CI.
    Source hashes are regenerated and fully verified at release time.
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = "",
    [switch]$Strict,
    [switch]$NoBinaries,
    [switch]$SkipSourceHashes,
    [switch]$PackageMode,
    [string]$PendingRelease = '',
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Test-ChangelogVersion([string]$Newest, [string]$Released, [bool]$RepositoryOnly, [bool]$NoteExists) {
    if ($Newest -notmatch '^\d+\.\d+\.\d+$' -or $Released -notmatch '^\d+\.\d+\.\d+$') { return $false }
    return $Newest -eq $Released -or ($RepositoryOnly -and $NoteExists -and [version]$Newest -gt [version]$Released)
}
if ($SelfTest) {
    $cases = @(
        @('3.3.0','3.3.0',$false,$false,$true),
        @('4.0.0','3.3.0',$true,$true,$true),
        @('4.0.0','3.3.0',$true,$false,$false),
        @('4.0.0','3.3.0',$false,$true,$false),
        @('3.2.0','3.3.0',$true,$true,$false),
        @('3.10.0','3.9.0',$true,$true,$true),
        @('invalid','3.3.0',$true,$true,$false)
    )
    foreach ($case in $cases) {
        if ((Test-ChangelogVersion $case[0] $case[1] $case[2] $case[3]) -ne $case[4]) { throw "CHANGELOG self-test failed: $($case -join ',')" }
    }
    Write-Host "CHANGELOG self-tests: $($cases.Count) passed, 0 failed."
    exit 0
}
if ($PendingRelease -and (-not $NoBinaries -or -not $SkipSourceHashes -or $PackageMode)) {
    throw '-PendingRelease is only for the repository pre-build gate with -NoBinaries -SkipSourceHashes'
}

function Resolve-BundleRoot {
    if ($BundleRoot) {
        if (-not (Test-Path -LiteralPath $BundleRoot -PathType Container)) { throw "Bundle root does not exist: $BundleRoot" }
        return (Resolve-Path -LiteralPath $BundleRoot).Path
    }
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
}

$root = Resolve-BundleRoot
$PackageMode = $PackageMode -or -not (Test-Path -LiteralPath (Join-Path $root 'Version.props'))
$deliveryRules = Get-Content -LiteralPath (Join-Path $root 'scripts/operations/delivery-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($deliveryRules.schemaVersion -ne 1) { throw 'Unsupported delivery-files schema' }
function MatchesDeliveryRule([string]$path, $rule) {
    if (@($rule.files) -ccontains $path) { return $true }
    foreach ($prefix in $rule.prefixes) { if ($path.StartsWith($prefix, [StringComparison]::Ordinal)) { return $true } }
    return $false
}
function IsDeliveryFile([string]$path) {
    return (MatchesDeliveryRule $path $deliveryRules.include) -and -not (MatchesDeliveryRule $path $deliveryRules.exclude)
}
$failures = New-Object System.Collections.Generic.List[string]

function Fail([string]$msg) {
    [void]$failures.Add($msg)
    Write-Host "[FAIL] $msg" -ForegroundColor Red
}

function Ok([string]$msg) {
    Write-Host "[ OK ] $msg" -ForegroundColor Green
}

function FileHash([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

Write-Host "Bundle root: $root"
Write-Host ("Validation mode: " + $(if ($PackageMode) { 'package' } else { 'repository' }))
if ($Strict) {
    # ECMA-335 custom-attribute SerString sequence; no assembly code is loaded.
    $testPolicyMarkers = @('TiaMcpTestPolicy', 'TiaMcpTestWorkerDouble')
    $policyFiles = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.dll','.exe' })
    if (Test-Path -LiteralPath (Join-Path $root 'runtime')) {
        $policyFiles += @(Get-ChildItem -LiteralPath (Join-Path $root 'runtime') -Recurse -File | Where-Object { $_.Extension -in '.dll','.exe' })
    }
    foreach ($policyFile in $policyFiles) {
        $bytes = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($policyFile.FullName))
        foreach ($testPolicyMarker in $testPolicyMarkers) {
            if ($bytes.Contains($testPolicyMarker)) { Fail "Test-only behavior assembly cannot enter a release bundle: $($policyFile.FullName)" }
        }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\\', '/')
    if ((IsDeliveryFile $relative) -and $relative.StartsWith('runtime/', [StringComparison]::Ordinal) -or
        (IsDeliveryFile $relative) -and $relative.StartsWith('hooks/', [StringComparison]::Ordinal)) {
        if ($file.Extension -in '.cs', '.csproj') { Fail "Shipped runtime/hook tree contains source: $relative" }
    }
}
foreach ($path in @($deliveryRules.include.files) + @($deliveryRules.include.prefixes)) {
    if ($NoBinaries -and ($path -in @('TiaOpenness.exe','runtime/tools/TiaMcp.Updater.exe','runtime/tools/TiaMcp.Updater.exe.config') -or ($path.StartsWith('runtime/') -and $path -ne 'runtime/README.md'))) { continue }
    # Outside a package the updater is staged in bin-build/updater; Package-Release copies it to runtime/tools.
    if (-not $PackageMode -and $path -in @('runtime/tools/TiaMcp.Updater.exe','runtime/tools/TiaMcp.Updater.exe.config') -and
        (Test-Path -LiteralPath (Join-Path (Join-Path $root 'bin-build/updater') (Split-Path $path -Leaf)))) { Ok "Staged updater build output present: $path"; continue }
    if (Test-Path -LiteralPath (Join-Path $root $path)) { Ok "Delivery resource present: $path" }
    else { Fail "Missing delivery resource: $path" }
}
if ($PackageMode) {
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not (IsDeliveryFile $relative)) { Fail "File outside delivery set: $relative" }
    }
}


if ($SkipSourceHashes) { Write-Host "[INFO] Source hashes are verified at release time; recorded source-file checks skipped" -ForegroundColor Cyan }

if ($NoBinaries) { Write-Host "[INFO] -NoBinaries: runtime\ and TiaOpenness.exe are build outputs, not checked here" -ForegroundColor Cyan }
# Check-BundleLayout.py compares the code table with this enforced resource list.
# The embedded V21 catalog is also shipped as readable reference documentation.
$bundleResourcePaths = @(
    'manifest/package-manifest.json',
    'manifest/delivery.json',
    'reference/siemens-openness/skills',
    'reference/siemens-openness/UPSTREAM.json',
    'reference/v21-ecosystem.json',
    'scripts/ecosystem/plc_tools_bridge.py',
    'scripts/ecosystem/simaticml_decode_bridge.py',
    'runtime/tools/TiaMcp.WriteGuard.exe',
    'runtime/tools/TiaMcp.Updater.exe',
    'templates'
)
foreach ($resource in $bundleResourcePaths) {
    if (Test-Path -LiteralPath (Join-Path $root $resource)) { Ok "Bundle resource present: $resource" }
    elseif ($resource -eq 'runtime/tools/TiaMcp.WriteGuard.exe' -and -not $PackageMode -and $NoBinaries) { Ok "Generated bundle resource will be built before packaging: $resource" }
    elseif ($resource -eq 'runtime/tools/TiaMcp.WriteGuard.exe' -and -not $PackageMode -and (Test-Path -LiteralPath (Join-Path $root 'src/Tools/WriteGuard/bin/Release/net10.0/TiaMcp.WriteGuard.exe'))) { Ok "Generated bundle resource build output present: $resource" }
    elseif ($resource -eq 'runtime/tools/TiaMcp.Updater.exe' -and -not $PackageMode -and $NoBinaries) { Ok "Generated bundle resource will be built before packaging: $resource" }
    elseif ($resource -eq 'runtime/tools/TiaMcp.Updater.exe' -and -not $PackageMode -and (Test-Path -LiteralPath (Join-Path $root 'bin-build/updater/TiaMcp.Updater.exe'))) { Ok "Generated bundle resource build output present: $resource" }
    else { Fail "Missing bundle resource: $resource" }
}
foreach ($guiFile in @(
    'src/Shared/BundleLayout.cs',
    'scripts/checks/Check-BundleLayout.py',
    'tests/Engine/TiaMcpServer.Tests/BundleLayoutTests.cs',
    'src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj',
    'src/Adapters.Contracts/packages.lock.json',
    'src/Adapters/Native/Plc/PlcServices.cs',
    'src/Engine/ModelContextProtocol/InvocationJournal.Adapter.cs',
    'tests/Engine/TiaMcpServer.HttpTests/AdapterIntegrationChecks.cs',
    'TiaOpenness.exe', 'docs/getting-started/configuration.md', 'scripts/build/Build-Configurator.ps1',
    'src/Studio/Launcher/Launcher.cs',
    'src/Studio/Gui/Themes/Glass.xaml',
    'src/Studio/Gui/Controls/GlassLogView.cs',
    'src/Studio/Gui/Fonts/Manrope-Regular.ttf',
    'src/Studio/Gui/Fonts/Manrope-Medium.ttf',
    'src/Studio/Gui/Fonts/Manrope-SemiBold.ttf',
    'src/Studio/Gui/Fonts/Manrope-Bold.ttf',
    'src/Studio/Gui/Fonts/Manrope-OFL.txt',
    'src/Studio/Gui/Fonts/JetBrainsMono-Regular.ttf',
    'src/Studio/Gui/Fonts/JetBrainsMono-Medium.ttf',
    'src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt',
    'src/Studio/Gui/Fonts/NotoSansSC-Regular.otf',
    'src/Studio/Gui/Fonts/NotoSansSC-Bold.otf',
    'src/Studio/Gui/Fonts/NotoSansSC-OFL.txt',
    'src/Studio/Gui/Fonts/SOURCES.txt',
    'src/Studio/Gui/TiaOpenness.Gui.csproj',
    'src/Studio/Gui/Configuration/ConfigurationView.xaml',
    'src/Studio/Gui/Configuration/ConfigurationView.xaml.cs',
    'src/Studio/Gui/Configuration/ConfigCore.cs',
    'src/Studio/Gui/Configuration/ClientProfiles.cs',
    'src/Studio/Gui/Configuration/UpdateCheck.cs',
    'src/Studio/Gui/Configuration/ModernJson.cs',
    'src/Studio/Gui/Localization/Strings.cs',
    'src/Studio/Gui/Themes/Palette.Light.xaml',
    'src/Studio/Gui/Themes/Palette.Dark.xaml',
    'tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj',
    'tests/Studio/TiaOpenness.Configuration.Tests/Tests.cs'
)) {
    if ($PackageMode -and -not (IsDeliveryFile $guiFile)) { continue }
    if ($NoBinaries -and $guiFile -eq 'TiaOpenness.exe') { continue }
    if (Test-Path -LiteralPath (Join-Path $root $guiFile)) { Ok "GUI entry present: $guiFile" }
    else { Fail "Missing GUI entry: $guiFile" }
}

foreach ($name in @('TiaMcp.Runtime.csproj','S7LiveReader.cs','OpcUaLiveReader.cs','S7WebApiChannel.cs','UnifiedOpenPipeChannel.cs')) {
    $path = 'src/Runtime/' + $name
    if ($PackageMode) { continue }
    if (!(Test-Path -LiteralPath (Join-Path $root $path))) { Fail "Missing runtime channel source: $path" }
}
if (!$NoBinaries) {
    $verifier = Join-Path $root 'runtime/verification/NativeCallWeaver.dll'
    foreach ($name in @('NativeCallWeaver.dll','NativeCallWeaver.deps.json','NativeCallWeaver.runtimeconfig.json','Mono.Cecil.dll')) {
        if ($PackageMode) { continue }
        if (!(Test-Path -LiteralPath (Join-Path $root "runtime/verification/$name"))) { Fail "Missing packaged native verifier: $name" }
    }
    foreach ($major in @(20,21)) {
        if (!(Test-Path -LiteralPath (Join-Path $root "runtime/v$major/TiaMcp.Runtime.dll"))) { Fail "Missing runtime channel assembly: V$major" }
        foreach ($name in @("TiaMcp.Adapter.$major.dll",'TiaMcp.Adapters.Contracts.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $root "runtime/v$major/$name"))) { Fail "Missing shared adapter dependency: V$major/$name" }
        }
        $adapters = @(Get-ChildItem -LiteralPath (Join-Path $root "runtime/v$major") -Filter 'TiaMcp.Adapter.*.dll' -File -ErrorAction SilentlyContinue)
        if ($adapters.Count -ne 1 -or $adapters[0].Name -cne "TiaMcp.Adapter.$major.dll") { Fail "V$major must contain exactly its matching adapter" }
        elseif (Test-Path -LiteralPath $verifier) {
            try {
                if ([Reflection.AssemblyName]::GetAssemblyName($adapters[0].FullName).Name -cne "TiaMcp.Adapter.$major") { Fail "Packaged V$major adapter assembly identity mismatch" }
            } catch { Fail "Packaged V$major adapter metadata is unreadable" }
            & dotnet $verifier verify $adapters[0].FullName
            if ($LASTEXITCODE -ne 0) { Fail "Packaged V$major adapter weave verification failed" }
            else { Ok "Packaged V$major adapter weave verified" }
        }
    }
}

foreach ($name in @('ChannelMessage.cs','LineFraming.cs','ChannelCodec.cs','ChannelClient.cs','ChannelServer.cs','TiaMcp.WorkerChannel.csproj','packages.lock.json')) {
    $path = 'src/WorkerChannel/' + $name
    if ($PackageMode) { continue }
    if (!(Test-Path -LiteralPath (Join-Path $root $path))) { Fail "Missing worker channel source: $path" }
}
if (!$NoBinaries) {
    if (!(Test-Path -LiteralPath (Join-Path $root 'runtime/studio/TiaMcp.WorkerChannel.dll'))) { Fail 'Missing Studio client worker channel' }
    foreach ($name in @('TiaMcp.WorkerChannel.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $root "runtime/studio/bridge/$name"))) { Fail "Missing Studio bridge channel dependency: $name" }
    }
    # The .NET 10 Foundation hosts load System.Text.Json, System.Text.Encodings.Web and System.IO.Pipelines from the bundled shared framework.
    foreach ($name in @('System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll')) {
        if (!(Test-Path -Path (Join-Path $root "runtime/dotnet/shared/Microsoft.NETCore.App/*/$name"))) { Fail "Missing bundled framework assembly for the Foundation hosts: $name" }
    }
    foreach ($key in @('14sp1','15.1','16','17','18','19')) {
        foreach ($name in @('TiaMcp.WorkerChannel.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $root "runtime/v$key/$name"))) { Fail "Missing worker channel host dependency: v$key/$name" }
        }
        foreach ($name in @('TiaMcp.WorkerChannel.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.IO.Pipelines.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path $root "runtime/v$key/worker/$name"))) { Fail "Missing worker channel dependency: v$key/worker/$name" }
        }
    }
}
if (-not $PackageMode -and !(Test-Path -LiteralPath (Join-Path $root 'src/Studio/Core/Rpc/BridgeChannel.cs'))) { Fail 'Missing Studio channel codec source' }

# The checkout and delivery use the same canonical runtime paths.
$exe = Join-Path $root 'runtime/v21/TiaMcp.Engine.V21.exe'
if ($NoBinaries) { $exe = $null }
elseif (!(Test-Path -LiteralPath $exe)) { Fail "Missing V21 runtime: $exe"; $exe=$null }
else { Ok "TiaMcp.Engine.V21.exe present ($exe)" }

# The generation and prewarm shortcuts are documented as direct engine commands.
foreach ($removed in @('tia.cmd','tia-v20.cmd','配置MCP.bat','配置MCP-v20.bat')) {
    if (Test-Path -LiteralPath (Join-Path $root $removed)) { Fail "Replaced launcher must be removed: $removed" }
}
$cliGuide = Join-Path $root 'docs/getting-started/cli.md'
$cliText = if (Test-Path -LiteralPath $cliGuide) { Get-Content -LiteralPath $cliGuide -Raw -Encoding UTF8 } else { '' }
foreach ($command in @('runtime\v21\TiaMcp.Engine.V21.exe gen <spec>', 'runtime\v21\TiaMcp.Engine.V21.exe prewarm', 'runtime\v20\TiaMcp.Engine.V20.exe gen <spec>', 'runtime\v20\TiaMcp.Engine.V20.exe prewarm')) {
    if ($cliText.Contains($command)) { Ok "Documented CLI command: $command" } else { Fail "Missing documented CLI command: $command" }
}

# The engine ships the trimmed lite roster by default, so it MUST carry the FindTools/CallTool
# bridge — an engine with the small roster but without the bridge is the one genuinely broken
# combination: the remaining tools become unreachable with no way to discover them.
# Binary marker scan on purpose, so the gate never depends on being able to start the engine.
# Key on 'FindTools': 'CallTool' also occurs inside the MCP SDK ("CallToolRequest") and would
# pass on an engine that never defined the bridge at all.
if ($exe) {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    if ([System.Text.Encoding]::ASCII.GetString($bytes).Contains('FindTools') -or
        [System.Text.Encoding]::Unicode.GetString($bytes).Contains('FindTools')) {
        Ok 'Engine carries the FindTools/CallTool bridge'
    }
    else {
        Fail ('Engine has NO FindTools bridge - it predates the lite-by-default change and ' +
              'would hide the remaining tools with no way to reach them. Rebuild it.')
    }
}

$readme = Join-Path $root "README.md"
if (-not (Test-Path -LiteralPath $readme)) { Fail "Missing README.md" } else { Ok "README.md present" }

$skill = Join-Path $root "plugin\skill\SKILL.md"
if (-not (Test-Path -LiteralPath $skill)) { Fail "Missing SKILL.md" } else { Ok "SKILL.md present" }

$blueprintPath = Join-Path $root "templates\project-blueprints\full_plc_hmi_project.json"
if (-not (Test-Path -LiteralPath $blueprintPath)) {
    Fail "Missing blueprint JSON"
}
else {
    try {
        $blueprint = Get-Content -LiteralPath $blueprintPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Ok "Blueprint JSON parses"
        if ($blueprint.requiredBundleFiles) {
            foreach ($rel in $blueprint.requiredBundleFiles) {
                if ($PackageMode -and $rel -eq 'scripts/checks/Validate-Bundle.ps1') { continue } # Repository-only historical metadata.
                $p = Join-Path $root ($rel -replace "/", [IO.Path]::DirectorySeparatorChar)
                if (-not (Test-Path -LiteralPath $p)) {
                    Fail "Blueprint requiredBundleFiles missing: $rel"
                }
            }
            Ok ("Blueprint requiredBundleFiles all exist ({0} paths)" -f $blueprint.requiredBundleFiles.Count)
        }
    }
    catch {
        Fail ("Blueprint JSON invalid: " + $_.Exception.Message)
    }
}

$manifestPath = Join-Path $root "manifest\package-manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Ok "package-manifest.json parses"
        $expectedTools = $manifest.capabilities.mcpToolCount
        $toolsPath = Join-Path $root "manifest\tools-list.json"
        if (Test-Path -LiteralPath $toolsPath) {
            $toolsDoc = Get-Content -LiteralPath $toolsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $n = @($toolsDoc.tools).Count
            if ($expectedTools -and ($n -ne $expectedTools)) {
                $msg = "tools-list tool count ($n) != manifest mcpToolCount ($expectedTools)"
                if ($Strict) { Fail $msg } else { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
            }
            else {
                Ok ("tools-list count matches manifest ({0})" -f $n)
            }
        }
    }
    catch {
        Fail ("manifest JSON invalid: " + $_.Exception.Message)
    }
}
else {
    Fail "Missing manifest\package-manifest.json"
}

$plcJsonDir = Join-Path $root "templates\plc\plcbuild-json"
if (Test-Path -LiteralPath $plcJsonDir) {
    Get-ChildItem -LiteralPath $plcJsonDir -Filter "*.json" -File | ForEach-Object {
        try {
            $null = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Fail ("plcbuild-json invalid: $($_.Name) — " + $_.Exception.Message)
        }
    }
    Ok ("All plcbuild-json files parse ({0} files)" -f @((Get-ChildItem -LiteralPath $plcJsonDir -Filter "*.json" -File)).Count)
}

$hmiDir = Join-Path $root "templates\hmi"
if (Test-Path -LiteralPath $hmiDir) {
    Get-ChildItem -LiteralPath $hmiDir -Filter "*.json" -File | ForEach-Object {
        try {
            $null = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Fail ("HMI template JSON invalid: $($_.Name) — " + $_.Exception.Message)
        }
    }
    Ok ("All templates/hmi JSON files parse ({0} files)" -f @((Get-ChildItem -LiteralPath $hmiDir -Filter "*.json" -File)).Count)
}

# ── Version consistency ───────────────────────────────────────────────────────
# The release version used to live in four places that nobody diffed against each
# other, so they drifted: csproj said 2.5.0, the manifest said 2.5.1, and the
# 2.5.1 fix reached the v21 branch but never master, a tag, or a release. The
# Version.props is the product version source; release records must match it.
$changelog = Join-Path $root "CHANGELOG.md"
$versionProps = Join-Path $root "Version.props"
if ($PackageMode) {
    $sourceRelease = [string]((Get-Content -LiteralPath (Join-Path $root 'manifest/delivery.json') -Raw -Encoding UTF8 | ConvertFrom-Json).release)
} else {
    [xml]$versionXml = Get-Content -LiteralPath $versionProps -Raw -Encoding UTF8
    $sourceRelease = [string]$versionXml.Project.PropertyGroup.TiaMcpRelease
}
if ($sourceRelease -notmatch '^\d+\.\d+\.\d+$') { Fail 'Release must be X.Y.Z' }
$plugin = Get-Content (Join-Path $root '.claude-plugin/plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($plugin.version -ne $sourceRelease) { Fail 'Plugin version differs from release version' }
else { Ok 'Plugin version matches release version' }
if ($PendingRelease) {
    if ($PackageMode -or $sourceRelease -ne $PendingRelease) { Fail 'Pending release must match Version.props in a repository' }
    # Only this explicit pre-build mode permits the mechanical version bump before rebuilding.
    # The old manifests must still agree with each other and their recorded hashes.
    $sourceRelease = [string]((Get-Content (Join-Path $root 'manifest/delivery.json') -Raw -Encoding UTF8 | ConvertFrom-Json).release)
}
$manifest  = Join-Path $root "manifest\package-manifest.json"

if ((Test-Path -LiteralPath $changelog) -and ($PackageMode -or (Test-Path -LiteralPath $versionProps)) -and (Test-Path -LiteralPath $manifest)) {
    $clText = Get-Content -LiteralPath $changelog -Raw -Encoding UTF8
    $clMatch = [regex]::Match($clText, '(?m)^##\s*\[(?<v>\d+\.\d+\.\d+)\]')
    if (-not $clMatch.Success) {
        Fail "CHANGELOG.md: no '## [x.y.z]' entry found — cannot determine the release version"
    }
    else {
        $version = $clMatch.Groups['v'].Value
        $versionFailures = $failures.Count
        $build = Get-Content (Join-Path $root 'manifest/release-build.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $delivery = Get-Content (Join-Path $root 'manifest/delivery.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $hasNote = Test-Path -LiteralPath (Join-Path $root "docs/releases/v$version.md") -PathType Leaf
        if (-not (Test-ChangelogVersion $version $delivery.release ($NoBinaries -and -not $PackageMode) $hasNote)) {
            Fail 'CHANGELOG must match the release, or name a newer documented release in repository -NoBinaries mode'
        }
        if ($PendingRelease -and $version -ne $PendingRelease) { Fail 'Newest CHANGELOG entry must match the pending release' }
        if ($NoBinaries -and -not $PackageMode -and [version]$version -gt [version]$delivery.release -and $hasNote) {
            Ok "Unreleased CHANGELOG $version has matching release notes; validating published records for $($delivery.release)"
            $version = $delivery.release
        }
        $engineVersion = $build.release
        if ($delivery.release -ne $version -or $delivery.engineRelease -ne $engineVersion -or $delivery.fileVersion -ne $build.fileVersion) {
            Fail 'Delivery version differs from CHANGELOG or engine build record'
        }

        if ($sourceRelease -ne $engineVersion -or ($sourceRelease + '.0') -ne $build.fileVersion) {
            Fail 'Engine build version differs from Version.props'
        }

        $mf = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($mf.bundleVersion -ne $version) {
            Fail ("Version mismatch: CHANGELOG says {0}, manifest bundleVersion says {1}" -f $version, $mf.bundleVersion)
        }
        if ($mf.packageName -notmatch ("v{0}[_-]" -f [regex]::Escape($version))) {
            Fail ("Version mismatch: manifest packageName '{0}' does not carry v{1}" -f $mf.packageName, $version)
        }

        # The shipped engine is a binary, so a stale runtime/ is invisible in a diff.
        $exe = Join-Path $root "runtime\v21\TiaMcp.Engine.V21.exe"
        if (-not $NoBinaries -and (Test-Path -LiteralPath $exe)) {
            $fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            if ($fileVersion -ne $build.fileVersion) {
                Fail ("Engine version mismatch: validated build says {0}, runtime reports {1}" -f $build.fileVersion, $fileVersion)
            }
        }

        if ($failures.Count -eq $versionFailures) {
            Ok ("Delivery {0} and engine {1} versions match their build records" -f $version, $engineVersion)
        }
    }
}

# The pre-build gate (-PendingRelease) runs before the records are regenerated; a run that stopped mid-build can leave
# them out of step, and the post-build validation checks them strictly.
if ($Strict -and -not $PendingRelease -and (Test-Path -LiteralPath (Join-Path $root 'manifest/release-build.json'))) {
    $delivery = Get-Content (Join-Path $root 'manifest/delivery.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in @(@('manifest/release-build.json',$delivery.engineBuildSha256),@('manifest/configurator-build.json',$delivery.configuratorBuildSha256))) {
        if ((FileHash (Join-Path $root $entry[0])) -ne $entry[1]) { Fail "Build record changed: $($entry[0])" }
    }
    $gui = Get-Content (Join-Path $root 'manifest/configurator-build.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($gui.testsPassed -le 0) { Fail 'Configurator test result missing' }
    if (-not $NoBinaries -and (FileHash (Join-Path $root $gui.executable.path)) -ne $gui.executable.sha256) { Fail 'Configurator EXE changed after validation' }
    $licensePath = Join-Path $root 'LICENSE'
    if (!(Test-Path -LiteralPath (Join-Path $root 'NOTICE.md'))) { Fail 'Source and copyright notice missing: NOTICE.md' }
    if (!(Test-Path -LiteralPath $licensePath) -or
        (Get-Content -LiteralPath $licensePath -Raw -Encoding UTF8) -notmatch 'Copyright \(c\) 2026 bulaofen0036-coder') {
        Fail 'Original MIT copyright notice must be preserved'
    }
    $build = Get-Content -LiteralPath (Join-Path $root 'manifest/release-build.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $multi = $null
    if ($delivery.multiVersionBuildSha256) {
        $multiPath = Join-Path $root 'manifest/multi-version-build.json'
        if ((FileHash $multiPath) -ne $delivery.multiVersionBuildSha256) { Fail 'Multi-version build record changed' }
        $multi = Get-Content -LiteralPath $multiPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($multi.release -ne $delivery.release -or $multi.fileVersion -ne $build.fileVersion) { Fail 'Multi-version release/version mismatch' }
        if (-not $multi.validation.foundationTransportExecuted -or -not $multi.validation.studioFunctionalTestsExecuted -or -not $multi.validation.configurationFunctionalTestsExecuted -or -not $multi.validation.toolUsageCoverageExecuted) { Fail 'Multi-version functional validation incomplete' }
        if (($multi.studioReleaseKeys -join ',') -ne '14sp1,15.1,16,17,18,19,20,21') { Fail 'Eight Studio/MCP release keys required' }
        if (-not $NoBinaries) {
            foreach ($key in $multi.studioReleaseKeys) {
                $engineName = if ($key -in '20','21') { "TiaMcp.Engine.V$key.exe" } else { 'TiaMcp.FoundationHost.exe' }
                $engine = Join-Path $root "runtime/v$key/$engineName"
                if (-not (Test-Path -LiteralPath $engine)) { Fail "V$key runtime missing" }
                elseif ((Get-Item -LiteralPath $engine).VersionInfo.FileVersion -ne $build.fileVersion) { Fail "V$key runtime version is stale" }
                if (-not (Test-Path -LiteralPath (Join-Path $root "runtime/studio/bridge/adapters/v$key/TiaOpenness.Openness.dll"))) { Fail "Studio V$key adapter missing" }
            }
            $studio = Join-Path $root 'runtime/studio/TiaOpenness.exe'
            if (-not (Test-Path -LiteralPath $studio)) { Fail 'Studio executable missing' }
            elseif ((Get-Item -LiteralPath $studio).VersionInfo.FileVersion -ne $build.fileVersion) { Fail 'Studio version is stale' }
            if (-not $PackageMode) {
                $pin = Get-Content -LiteralPath (Join-Path $root 'scripts/build/bundled-dotnet.json') -Raw -Encoding UTF8 | ConvertFrom-Json
                if (-not (Test-Path -LiteralPath (Join-Path $root "runtime/dotnet/host/fxr/$($pin.version)/hostfxr.dll"))) { Fail "Bundled .NET $($pin.version) host missing" }
                foreach ($framework in $pin.frameworks) { if (-not (Test-Path -LiteralPath (Join-Path $root "runtime/dotnet/shared/$framework/$($pin.version)"))) { Fail "Bundled $framework $($pin.version) missing" } }
            } else {
                # The recorded runtime inventory supplies exact versions and hashes in a package.
                foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $root "runtime/dotnet/$name"))) { Fail "Bundled .NET license missing: $name" }
                }
                foreach ($pattern in @('runtime/dotnet/host/fxr/*/hostfxr.dll', 'runtime/dotnet/shared/Microsoft.NETCore.App/*/Microsoft.NETCore.App.deps.json', 'runtime/dotnet/shared/Microsoft.AspNetCore.App/*/Microsoft.AspNetCore.App.deps.json', 'runtime/dotnet/shared/Microsoft.WindowsDesktop.App/*/Microsoft.WindowsDesktop.App.deps.json')) {
                    $recorded = @($build.runtimeFiles) + @($multi.files) | Where-Object { $_.path -like $pattern }
                    if (-not $recorded) { Fail "Bundled .NET runtime record missing: $pattern" }
                }
            }
        }
    }
    if (-not $PackageMode -and -not $SkipSourceHashes) {
        foreach ($row in @($gui.sourceFiles) + @($build.sourceFiles) + @($multi.sourceFiles) + @($build.validationInputs) + @($multi.validationInputs)) {
            if ($null -eq $row) { continue }
            $file = Join-Path $root $row.path
            if (!(Test-Path -LiteralPath $file)) { Fail "Validated source missing: $($row.path)"; continue }
            if ([IO.Path]::GetExtension($file) -in ".ttf", ".otf") { $bytes = [IO.File]::ReadAllBytes($file) }
            else { $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($file).Replace("`r`n", "`n")) }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') }
            finally { $algorithm.Dispose() }
            if ($digest -ne $row.sha256) { Fail "Source changed after validation: $($row.path)" }
        }
    }
    foreach ($major in @(20,21)) {
        if (($sourceRelease + '.0') -ne $build.fileVersion) { Fail "V$major source/runtime version differs" }
        if ($NoBinaries) { continue }
        $engine = Join-Path $root "runtime/v$major/TiaMcp.Engine.V$major.exe"
        if (!(Test-Path -LiteralPath $engine)) { Fail "V$major runtime missing"; continue }
        if ((Get-Item -LiteralPath $engine).VersionInfo.FileVersion -ne $build.fileVersion) { Fail "V$major runtime is stale" }
    }
    foreach ($row in @($build.runtimeFiles) + @($multi.files)) {
        if ($null -eq $row) { continue }
        if ($PackageMode -and -not (IsDeliveryFile $row.path)) {
            if (-not $row.path.StartsWith('runtime/verification/')) { Fail "Recorded runtime excluded from delivery: $($row.path)" }
            continue
        }
        if ($NoBinaries) { continue }
        $file = Join-Path $root $row.path
        if (!(Test-Path -LiteralPath $file)) { Fail "Runtime dependency missing: $($row.path)" }
        else {
            $stream = [IO.File]::OpenRead($file)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose(); $algorithm.Dispose() }
            if ($digest -ne $row.sha256) { Fail "Runtime hash differs: $($row.path)" }
        }
    }
    if ($failures.Count -eq 0) { Ok $(if ($PackageMode) { 'Delivery records and package resources match (source checks run in the repository)' } elseif ($NoBinaries -and $SkipSourceHashes) { 'Build records and versions match (binaries and source hashes not checked)' } elseif ($NoBinaries) { 'Build records, versions and source hashes match (binaries not checked)' } elseif ($SkipSourceHashes) { 'Both runtime versions and build manifest hashes match (source hashes not checked)' } else { 'Both runtime versions and all build manifest hashes match' }) }
}

if (-not $PackageMode) {
    $contractPaths = @('manifest/history/contracts-v3/README.md', 'manifest/history/contracts-v3/provenance.json')
    foreach ($directory in @('manifest/history/contracts-v3', 'manifest/contracts/v4')) {
        foreach ($category in @('baseline', 'responses')) {
            foreach ($key in @('14sp1', '15.1', '16', '17', '18', '19', '20', '21')) {
                $contractPaths += "$directory/$category/$key.json"
            }
        }
    }
    foreach ($path in $contractPaths) {
        if (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf) { Ok "Contract snapshot present: $path" }
        else { Fail "Missing contract snapshot: $path" }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Validation FAILED ($($failures.Count) issue(s))." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Validation PASSED." -ForegroundColor Green
exit 0
