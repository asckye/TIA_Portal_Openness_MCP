#requires -Version 5.1
<#
.SYNOPSIS
  Update this TIA MCP delivery in place from the latest GitHub release (or a local ZIP), with the engine stopped.

.DESCRIPTION
  Runs from inside an unpacked delivery (the folder holding manifest\delivery.json, runtime\, TiaMcpConfigurator.exe ...).
  The maintainer chose "stop, then update" over a self-replacing hot update, so the script:

    1. Reads the installed version from manifest\delivery.json.
    2. REFUSES while any TiaMcpServer.exe or TiaMcpConfigurator.exe is running (lists the PIDs; never kills them).
    3. Finds the release: GitHub API releases/latest (or -Version vX.Y.Z), falling back to the release page when the
       API is rate-limited; or takes -ZipPath for an offline update (the .sha256 sidecar next to it is used when present).
    4. Downloads the ZIP + .sha256 to <root>\.update\, verifies the SHA-256, extracts, checks the package layout.
    5. Backs up the current install to <root>\.previous\<package>\ (last two kept), replaces runtime\ and manifest\
       wholesale and overlays everything else from the package.
    6. Prints the new version. Start the engine again and call Bootstrap to confirm serverVersion.

  -Check only reports the installed and latest versions. -Rollback restores the newest backup (engine stopped as well).
  Nothing here touches TIA Portal, projects or client configurations.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1 -Check
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1 -ZipPath D:\Downloads\TIA_MCP_Delivery_v2.7.57_20260922.zip
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1 -Rollback
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$ZipPath = '',
    [string]$Version = '',
    [switch]$Rollback,
    [switch]$Force,
    [switch]$SkipHashCheck,
    [string]$Repository = 'asckye/TIA_Portal_Openness_MCP',
    [string]$InstallRoot = '',
    [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Say([string]$text) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }
function Fail([string]$text) { Write-Host ("FAIL: " + $text) -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- install root + installed version
if (-not $InstallRoot) { $InstallRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$deliveryJson = Join-Path $root 'manifest\delivery.json'
if (-not (Test-Path -LiteralPath $deliveryJson)) { Fail ("not a TIA MCP delivery: " + $root + " has no manifest\delivery.json (pass -InstallRoot)") }
$delivery = Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json
$installed = [string]$delivery.release
$installedPackage = [string]$delivery.package
Say ("install root " + $root + " - installed " + $installed + " (" + $installedPackage + ")")

function Running() {
    $list = @()
    foreach ($n in 'TiaMcpServer', 'TiaMcpConfigurator') {
        foreach ($p in @(Get-Process -Name $n -ErrorAction SilentlyContinue)) {
            $path = ''; try { $path = $p.Path } catch { }
            $list += ($n + '.exe PID ' + $p.Id + ($(if ($path) { ' (' + $path + ')' } else { '' })))
        }
    }
    return $list
}
function RequireStopped() {
    $running = Running
    if ($running.Count -gt 0) {
        Fail ("the engine is running - close it first, then run this script again. Running: " + ($running -join '; ') + ". (An MCP client such as Claude Code or VS Code may have started it: stop that client's server / close the session.)")
    }
}
function ReadVersion([string]$text) { if ($text -match '^v?(\d+)\.(\d+)\.(\d+)') { return [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) } return $null }

# ---------------------------------------------------------------- rollback
$previousRoot = Join-Path $root '.previous'
if ($Rollback) {
    if (-not (Test-Path -LiteralPath $previousRoot)) { Fail 'nothing to roll back to (.previous is missing)' }
    $backup = Get-ChildItem -LiteralPath $previousRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $backup) { Fail 'nothing to roll back to (.previous is empty)' }
    $backupDelivery = Join-Path $backup.FullName 'manifest\delivery.json'
    if (-not (Test-Path -LiteralPath $backupDelivery)) { Fail ('backup ' + $backup.FullName + ' has no manifest\delivery.json') }
    $backupVersion = [string]((Get-Content -LiteralPath $backupDelivery -Raw | ConvertFrom-Json).release)
    RequireStopped
    Say ("rolling back " + $installed + " -> " + $backupVersion + " from " + $backup.FullName)
    foreach ($dir in 'runtime', 'manifest') {
        $target = Join-Path $root $dir
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
    Copy-Item -Path (Join-Path $backup.FullName '*') -Destination $root -Recurse -Force
    $now = [string]((Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json).release)
    Say ("DONE: installed version is now " + $now + ". Start the engine and call Bootstrap to confirm serverVersion.")
    exit 0
}

# ---------------------------------------------------------------- locate the release
$headers = @{ 'User-Agent' = 'TiaMcp-Update-Engine/1'; 'Accept' = 'application/vnd.github+json' }
$zipUrl = ''; $shaUrl = ''; $tag = ''; $latest = ''
if ($ZipPath) {
    if (-not (Test-Path -LiteralPath $ZipPath)) { Fail ('ZIP not found: ' + $ZipPath) }
    $ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
    $zipName = Split-Path $ZipPath -Leaf
    if ($zipName -notmatch '^TIA_MCP_Delivery_v(\d+\.\d+\.\d+)_\d{8}\.zip$') { Fail ('not a delivery ZIP name: ' + $zipName + ' (expected TIA_MCP_Delivery_vX.Y.Z_YYYYMMDD.zip)') }
    $latest = $Matches[1]; $tag = 'v' + $latest
    Say ("offline update from " + $ZipPath + " (" + $latest + ")")
}
else {
    if ($Version -and $Version -notmatch '^v?\d+\.\d+\.\d+$') { Fail '-Version must be vX.Y.Z' }
    $wantedTag = $(if ($Version) { 'v' + $Version.TrimStart('v') } else { '' })
    $apiUrl = $(if ($wantedTag) { "https://api.github.com/repos/$Repository/releases/tags/$wantedTag" } else { "https://api.github.com/repos/$Repository/releases/latest" })
    $release = $null
    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec $TimeoutSeconds
        $tag = [string]$release.tag_name
        foreach ($a in @($release.assets)) {
            if ($a.name -match '^TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.zip$') { $zipUrl = [string]$a.browser_download_url; $zipName = [string]$a.name }
            if ($a.name -match '^TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.sha256$') { $shaUrl = [string]$a.browser_download_url }
        }
        Say ("GitHub API: " + $tag + " (" + $release.published_at + ")")
    }
    catch {
        Say ("GitHub API not usable (" + $_.Exception.Message + "); reading the release page instead")
        # The redirect target of /releases/latest carries the tag; the expanded-assets fragment lists the download links.
        if ($wantedTag) { $tag = $wantedTag }
        else {
            $req = [Net.HttpWebRequest]::Create("https://github.com/$Repository/releases/latest"); $req.AllowAutoRedirect = $false; $req.UserAgent = 'TiaMcp-Update-Engine/1'; $req.Timeout = $TimeoutSeconds * 1000
            $resp = $req.GetResponse(); $location = [string]$resp.Headers['Location']; $resp.Close()
            if ($location -match '/releases/tag/([^/?#]+)$') { $tag = $Matches[1] } else { Fail ('could not determine the latest release tag from ' + $location) }
        }
        $html = (Invoke-WebRequest -Uri "https://github.com/$Repository/releases/expanded_assets/$tag" -Headers @{ 'User-Agent' = 'TiaMcp-Update-Engine/1' } -UseBasicParsing -TimeoutSec $TimeoutSeconds).Content
        $m = [regex]::Match($html, '/releases/download/' + [regex]::Escape($tag) + '/(TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.zip)')
        if ($m.Success) { $zipName = $m.Groups[1].Value; $zipUrl = 'https://github.com/' + $Repository + $m.Value }
        $m2 = [regex]::Match($html, '/releases/download/' + [regex]::Escape($tag) + '/(TIA_MCP_Delivery_v\d+\.\d+\.\d+_\d{8}\.sha256)')
        if ($m2.Success) { $shaUrl = 'https://github.com/' + $Repository + $m2.Value }
        Say ("release page: " + $tag)
    }
    if (-not $zipUrl) { Fail ('release ' + $tag + ' carries no TIA_MCP_Delivery ZIP asset; see https://github.com/' + $Repository + '/releases') }
    $v = ReadVersion $tag
    if (-not $v) { Fail ('release tag is not a version: ' + $tag) }
    $latest = $v.ToString()
}

$cmp = $null
$vi = ReadVersion $installed; $vl = ReadVersion $latest
if ($vi -and $vl) { $cmp = $vl.CompareTo($vi) }
if ($cmp -gt 0) { Say ("UPDATE AVAILABLE: " + $installed + " -> " + $latest) }
elseif ($cmp -eq 0) { Say ("UP TO DATE: " + $installed + " is the latest release") }
elseif ($cmp -lt 0) { Say ("installed " + $installed + " is newer than " + $latest) }
if ($Check) { if ($zipUrl) { Say ("asset: " + $zipUrl) }; exit 0 }
if ($cmp -le 0 -and -not $Force) { Say 'nothing to do (pass -Force to reinstall anyway)'; exit 0 }

# ---------------------------------------------------------------- engine must be stopped from here on
RequireStopped

# ---------------------------------------------------------------- download + verify
$work = Join-Path $root '.update'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
if ($ZipPath) {
    $zipFile = $ZipPath
    $shaFile = [IO.Path]::ChangeExtension($ZipPath, '.sha256')
    if (-not (Test-Path -LiteralPath $shaFile)) { $shaFile = '' }
}
else {
    $zipFile = Join-Path $work $zipName
    Say ("downloading " + $zipUrl)
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile -Headers @{ 'User-Agent' = 'TiaMcp-Update-Engine/1' } -UseBasicParsing -TimeoutSec ($TimeoutSeconds * 10)
    $shaFile = ''
    if ($shaUrl) {
        $shaFile = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($zipName) + '.sha256')
        Invoke-WebRequest -Uri $shaUrl -OutFile $shaFile -Headers @{ 'User-Agent' = 'TiaMcp-Update-Engine/1' } -UseBasicParsing -TimeoutSec $TimeoutSeconds
    }
}
Say ("ZIP " + $zipFile + " (" + [math]::Round((Get-Item -LiteralPath $zipFile).Length / 1MB, 1) + " MB)")
if ($shaFile) {
    $expected = ((Get-Content -LiteralPath $shaFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { Fail ("SHA-256 mismatch: sidecar " + $expected + " vs file " + $actual + " - the download is corrupt or tampered; nothing was changed") }
    Say ("SHA-256 verified " + $actual)
}
elseif (-not $SkipHashCheck) { Fail 'no .sha256 sidecar found (download it next to the ZIP, or pass -SkipHashCheck to proceed unverified)' }
else { Say 'WARNING: hash check skipped' }

# ---------------------------------------------------------------- extract + check layout
$extract = Join-Path $work 'extract'
Expand-Archive -LiteralPath $zipFile -DestinationPath $extract -Force
$top = @(Get-ChildItem -LiteralPath $extract -Directory)
$package = $(if ($top.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $top[0].FullName 'manifest\delivery.json'))) { $top[0].FullName } elseif (Test-Path -LiteralPath (Join-Path $extract 'manifest\delivery.json')) { $extract } else { '' })
if (-not $package) { Fail 'the ZIP does not contain a delivery (no manifest\delivery.json at its root)' }
foreach ($must in 'runtime\v21\TiaMcpServer.exe', 'runtime\v20\TiaMcpServer.exe', 'TiaMcpConfigurator.exe', 'manifest\package-manifest.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $package $must))) { Fail ('package is incomplete: missing ' + $must) }
}
$newDelivery = Get-Content -LiteralPath (Join-Path $package 'manifest\delivery.json') -Raw | ConvertFrom-Json
Say ("package " + $newDelivery.package + " = engine " + $newDelivery.engineRelease)

# ---------------------------------------------------------------- backup, then replace
if (-not (Test-Path -LiteralPath $previousRoot)) { New-Item -ItemType Directory -Path $previousRoot | Out-Null }
$backupDir = Join-Path $previousRoot $installedPackage
if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
New-Item -ItemType Directory -Path $backupDir | Out-Null
Say ("backing up the current install to " + $backupDir)
foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
    if ($item.Name -in '.previous', '.update', 'TiaMcp_Output', 'bin-build') { continue }
    if ($item.Extension -eq '.log') { continue }
    Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $backupDir $item.Name) -Recurse -Force
}
foreach ($old in (Get-ChildItem -LiteralPath $previousRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2)) {
    Remove-Item -LiteralPath $old.FullName -Recurse -Force; Say ("dropped old backup " + $old.Name)
}
Say 'replacing runtime\ and manifest\, overlaying the rest'
foreach ($dir in 'runtime', 'manifest') {
    $target = Join-Path $root $dir
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
Copy-Item -Path (Join-Path $package '*') -Destination $root -Recurse -Force
Remove-Item -LiteralPath $work -Recurse -Force

$now = Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json
$exeVersion = (Get-Item -LiteralPath (Join-Path $root 'runtime\v21\TiaMcpServer.exe')).VersionInfo.FileVersion
Say ("DONE: " + $installed + " -> " + $now.release + " (runtime\v21\TiaMcpServer.exe " + $exeVersion + "). Start the engine and call Bootstrap to confirm serverVersion; -Rollback restores " + $installedPackage + ".")
