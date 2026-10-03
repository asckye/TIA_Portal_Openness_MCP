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
    runtime\v21 and TiaMcpConfigurator.exe are not tracked in Git, so their presence, versions and hashes are skipped;
    manifests, versions, launchers' syntax and the recorded source hashes are still checked.
.PARAMETER SkipSourceHashes
    Skip recorded source-file existence and hash comparisons in build manifests for push/PR CI.
    Source hashes are regenerated and fully verified at release time.
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = "",
    [switch]$Strict,
    [switch]$NoBinaries,
    [switch]$SkipSourceHashes
)

$ErrorActionPreference = "Stop"

function Resolve-BundleRoot {
    if ($BundleRoot -and (Test-Path -LiteralPath $BundleRoot)) {
        return (Resolve-Path -LiteralPath $BundleRoot).Path
    }
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
}

$root = Resolve-BundleRoot
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

if ($SkipSourceHashes) { Write-Host "[INFO] Source hashes are verified at release time; recorded source-file checks skipped" -ForegroundColor Cyan }

if ($NoBinaries) { Write-Host "[INFO] -NoBinaries: runtime\ and TiaMcpConfigurator.exe are build outputs, not checked here" -ForegroundColor Cyan }
foreach ($guiFile in @(
    'TiaMcpConfigurator.exe', 'docs/getting-started/configuration.md', 'scripts/build/Build-Configurator.ps1',
    'tools/mcp-configurator/Launcher.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/TiaOpenness.Gui.csproj',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigurationView.xaml',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigurationView.xaml.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigCore.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ClientProfiles.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/UpdateCheck.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ModernJson.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Localization/Strings.cs',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Themes/Palette.Light.xaml',
    'tools/tia-openness-studio/src/TiaOpenness.Gui/Themes/Palette.Dark.xaml',
    'tools/tia-openness-studio/tests/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj',
    'tools/tia-openness-studio/tests/TiaOpenness.Configuration.Tests/Tests.cs'
)) {
    if ($NoBinaries -and $guiFile -eq 'TiaMcpConfigurator.exe') { continue }
    if (Test-Path -LiteralPath (Join-Path $root $guiFile)) { Ok "GUI entry present: $guiFile" }
    else { Fail "Missing GUI entry: $guiFile" }
}

# The checkout and delivery use the same canonical runtime paths.
$exe = Join-Path $root 'runtime/v21/TiaMcpServer.exe'
if ($NoBinaries) { $exe = $null }
elseif (!(Test-Path -LiteralPath $exe)) { Fail "Missing V21 runtime: $exe"; $exe=$null }
else { Ok "TiaMcpServer.exe present ($exe)" }

# Sentinel: every launcher must point at an engine that actually exists in this checkout.
# The .cmd/.bat files and this script drifted apart once already — the validator checked one
# path while every user ran another — so the launchers are now parsed and verified here.
$launchers = @('scripts\operations\预热.bat','scripts\operations\生成工程.bat')
foreach ($removed in @('tia.cmd','tia-v20.cmd','配置MCP.bat','配置MCP-v20.bat')) {
    if (Test-Path -LiteralPath (Join-Path $root $removed)) { Fail "Replaced launcher must be removed: $removed" }
}
foreach ($rel in $launchers) {
    $lp = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $lp)) { Fail ("Missing launcher: " + $rel); continue }
    $ldir = Split-Path -Parent $lp
    $text = Get-Content -LiteralPath $lp -Raw -Encoding UTF8
    $refs = @([regex]::Matches($text, '%~dp0([^"%]*TiaMcpServer\.exe)') |
              ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($refs.Count -eq 0) { Fail ($rel + ': references no TiaMcpServer.exe path'); continue }
    if ($NoBinaries) { Ok ($rel + ' -> ' + ($refs -join ' ; ') + ' (existence not checked without binaries)'); continue }
    $anyPresent = $false
    foreach ($r in $refs) { if (Test-Path -LiteralPath (Join-Path $ldir $r)) { $anyPresent = $true } }
    if ($anyPresent) { Ok ($rel + ' -> an engine present in this checkout') }
    else { Fail ($rel + ' points at no engine present here: ' + ($refs -join ' ; ')) }
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

$skill = Join-Path $root "tools\tiaportal-mcp\skill\SKILL.md"
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
[xml]$versionXml = Get-Content -LiteralPath $versionProps -Raw -Encoding UTF8
$sourceRelease = [string]$versionXml.Project.PropertyGroup.TiaMcpRelease
if ($sourceRelease -notmatch '^\d+\.\d+\.\d+$') { Fail 'Version.props: release must be X.Y.Z' }
$plugin = Get-Content (Join-Path $root '.claude-plugin/plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($plugin.version -ne $sourceRelease) { Fail 'Plugin version differs from Version.props' }
else { Ok 'Plugin version matches Version.props' }
$manifest  = Join-Path $root "manifest\package-manifest.json"

if ((Test-Path -LiteralPath $changelog) -and (Test-Path -LiteralPath $versionProps) -and (Test-Path -LiteralPath $manifest)) {
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
        $exe = Join-Path $root "runtime\v21\TiaMcpServer.exe"
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

if ($Strict -and (Test-Path -LiteralPath (Join-Path $root 'manifest/release-build.json'))) {
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
                $engine = Join-Path $root "runtime/v$key/TiaMcpServer.exe"
                if (-not (Test-Path -LiteralPath $engine)) { Fail "V$key runtime missing" }
                elseif ((Get-Item -LiteralPath $engine).VersionInfo.FileVersion -ne $build.fileVersion) { Fail "V$key runtime version is stale" }
                if (-not (Test-Path -LiteralPath (Join-Path $root "runtime/studio/bridge/adapters/v$key/TiaOpenness.Openness.dll"))) { Fail "Studio V$key adapter missing" }
            }
            $studio = Join-Path $root 'runtime/studio/TiaOpenness.exe'
            if (-not (Test-Path -LiteralPath $studio)) { Fail 'Studio executable missing' }
            elseif ((Get-Item -LiteralPath $studio).VersionInfo.FileVersion -ne $build.fileVersion) { Fail 'Studio version is stale' }
        }
    }
    if (-not $SkipSourceHashes) {
        foreach ($row in @($gui.sourceFiles) + @($build.sourceFiles) + @($multi.sourceFiles)) {
            if ($null -eq $row) { continue }
            $file = Join-Path $root $row.path
            if (!(Test-Path -LiteralPath $file)) { Fail "Validated source missing: $($row.path)"; continue }
            $bytes = if ([IO.Path]::GetExtension($file) -eq ".ttf") { [IO.File]::ReadAllBytes($file) } else { [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($file).Replace("`r`n", "`n")) }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-','') }
            finally { $algorithm.Dispose() }
            if ($digest -ne $row.sha256) { Fail "Source changed after validation: $($row.path)" }
        }
    }
    foreach ($major in @(20,21)) {
        if (($sourceRelease + '.0') -ne $build.fileVersion) { Fail "V$major source/runtime version differs" }
        if ($NoBinaries) { continue }
        $engine = Join-Path $root "runtime/v$major/TiaMcpServer.exe"
        if (!(Test-Path -LiteralPath $engine)) { Fail "V$major runtime missing"; continue }
        if ((Get-Item -LiteralPath $engine).VersionInfo.FileVersion -ne $build.fileVersion) { Fail "V$major runtime is stale" }
    }
    foreach ($row in @($build.runtimeFiles) + @($multi.files)) {
        if ($null -eq $row) { continue }
        if ($NoBinaries) { break }
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
    if ($failures.Count -eq 0) { Ok $(if ($NoBinaries -and $SkipSourceHashes) { 'Build records and versions match (binaries and source hashes not checked)' } elseif ($NoBinaries) { 'Build records, versions and source hashes match (binaries not checked)' } elseif ($SkipSourceHashes) { 'Both runtime versions and build manifest hashes match (source hashes not checked)' } else { 'Both runtime versions and all build manifest hashes match' }) }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Validation FAILED ($($failures.Count) issue(s))." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Validation PASSED." -ForegroundColor Green
exit 0
