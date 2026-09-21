#requires -Version 5.1
<#
.SYNOPSIS
  Update this TIA MCP delivery in place from the latest GitHub release, with the engine stopped.

.DESCRIPTION
  Runs from inside an unpacked delivery (the folder holding manifest\delivery.json, runtime\, TiaMcpConfigurator.exe ...).
  The maintainer chose "stop, then update" over a self-replacing hot update, so the script:

    1. Reads the installed version from manifest\delivery.json.
    2. REFUSES while any TiaMcpServer.exe or TiaMcpConfigurator.exe is running (lists the PIDs; never kills them).
    3. Finds the release: GitHub API releases/latest (or -Version vX.Y.Z), falling back to the release page when the
       API is rate-limited. The TIA machine needs internet access to github.com.
    4. Downloads the ZIP + .sha256 to <root>\.update\, verifies the SHA-256, extracts into a short folder under
       %TEMP% and checks the package layout. Windows PowerShell's Expand-Archive, Copy-Item and Remove-Item stop at
       260-character paths and the package holds paths of ~110 characters below its root, so every tree copy and
       delete goes through robocopy (long-path safe) and only the extraction folder is length-checked.
    5. Backs up the current install to <root>\.previous\<package>\ (last two kept), replaces runtime\ and manifest\
       wholesale and overlays everything else from the package.
    6. Prints the new version. Start the engine again and call Bootstrap to confirm serverVersion.

  -Check only reports the installed and latest versions. -Rollback restores the newest backup (engine stopped as well).
  Nothing here touches TIA Portal, projects or client configurations.

  The configurator's "Update engine" menu item (2.8.0) runs this same script in its own window after closing itself:
  -WaitForPid <pid> waits for that configurator process to exit before the running-process check, and
  -RelaunchConfigurator starts TiaMcpConfigurator.exe from the install root again when the script ends.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1 -Check
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\operations\Update-Engine.ps1 -Rollback
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Version = '',
    [switch]$Rollback,
    [switch]$Force,
    [string]$Repository = 'asckye/TIA_Portal_Openness_MCP',
    [string]$InstallRoot = '',
    [int]$TimeoutSeconds = 60,
    [int]$WaitForPid = 0,
    [switch]$RelaunchConfigurator
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Say([string]$text) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }
function Relaunch() {
    if (-not $RelaunchConfigurator -or -not $root) { return }
    $exe = Join-Path $root 'TiaMcpConfigurator.exe'
    if (Test-Path -LiteralPath $exe) { Say ('reopening ' + $exe); Start-Process -FilePath $exe -WorkingDirectory $root }
}
function Fail([string]$text) { Write-Host ("FAIL: " + $text) -ForegroundColor Red; Relaunch; exit 1 }
# Any unexpected terminating error (network, archive, file system) still ends with a FAIL line and the relaunch.
trap { Write-Host ("FAIL: " + $_.Exception.Message) -ForegroundColor Red; Relaunch; exit 1 }

# ---------------------------------------------------------------- install root + installed version
if (-not $InstallRoot) { $InstallRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$deliveryJson = Join-Path $root 'manifest\delivery.json'
if (-not (Test-Path -LiteralPath $deliveryJson)) { Fail ("not a TIA MCP delivery: " + $root + " has no manifest\delivery.json (pass -InstallRoot)") }
$delivery = Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json
$installed = [string]$delivery.release
$installedPackage = [string]$delivery.package
Say ("install root " + $root + " - installed " + $installed + " (" + $installedPackage + ")")
if ($WaitForPid -gt 0) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Get-Process -Id $WaitForPid -ErrorAction SilentlyContinue) { Fail ('process ' + $WaitForPid + ' (the configurator that launched this update) is still running after 30 s') }
}

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
# robocopy copies and deletes trees regardless of the 260-character limit that stops Copy-Item / Remove-Item
# (the install root + .previous\<package>\ + the deepest package file easily exceed it). Exit codes below 8 are success.
function CopyTree([string]$from, [string]$to, [string[]]$excludeDirs = @(), [string[]]$excludeFiles = @()) {
    $rc = @($from, $to, '/E', '/COPY:DAT', '/DCOPY:T', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($excludeDirs.Count -gt 0) { $rc += '/XD'; $rc += $excludeDirs }
    if ($excludeFiles.Count -gt 0) { $rc += '/XF'; $rc += $excludeFiles }
    & robocopy.exe @rc | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ('robocopy failed (exit ' + $LASTEXITCODE + ') copying ' + $from + ' -> ' + $to) }
}
function RemoveTree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $empty = Join-Path ([IO.Path]::GetTempPath()) ('tia-mcp-empty-' + $PID)
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    & robocopy.exe $empty $path /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ('robocopy failed (exit ' + $LASTEXITCODE + ') clearing ' + $path) }
    Remove-Item -LiteralPath $path -Recurse -Force
    Remove-Item -LiteralPath $empty -Recurse -Force
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
    foreach ($dir in 'runtime', 'manifest') { RemoveTree (Join-Path $root $dir) }
    CopyTree $backup.FullName $root
    $now = [string]((Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json).release)
    Say ("DONE: installed version is now " + $now + ". Start the engine and call Bootstrap to confirm serverVersion.")
    Relaunch
    exit 0
}

# ---------------------------------------------------------------- locate the release (GitHub API, release page as fallback)
$headers = @{ 'User-Agent' = 'TiaMcp-Update-Engine/1'; 'Accept' = 'application/vnd.github+json' }
$zipUrl = ''; $shaUrl = ''; $tag = ''; $zipName = ''
if ($Version -and $Version -notmatch '^v?\d+\.\d+\.\d+$') { Fail '-Version must be vX.Y.Z' }
$wantedTag = $(if ($Version) { 'v' + $Version.TrimStart('v') } else { '' })
$apiUrl = $(if ($wantedTag) { "https://api.github.com/repos/$Repository/releases/tags/$wantedTag" } else { "https://api.github.com/repos/$Repository/releases/latest" })
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
        try {
            $req = [Net.HttpWebRequest]::Create("https://github.com/$Repository/releases/latest"); $req.AllowAutoRedirect = $false; $req.UserAgent = 'TiaMcp-Update-Engine/1'; $req.Timeout = $TimeoutSeconds * 1000
            $resp = $req.GetResponse(); $location = [string]$resp.Headers['Location']; $resp.Close()
        }
        catch { Fail ('github.com is not reachable from this machine (' + $_.Exception.Message + '). The updater needs internet access; check the connection or proxy and run it again.') }
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
if (-not $shaUrl) { Fail ('release ' + $tag + ' carries no .sha256 asset; the download cannot be verified, nothing was changed') }
$v = ReadVersion $tag
if (-not $v) { Fail ('release tag is not a version: ' + $tag) }
$latest = $v.ToString()

$cmp = $null
$vi = ReadVersion $installed
if ($vi) { $cmp = $v.CompareTo($vi) }
if ($cmp -gt 0) { Say ("UPDATE AVAILABLE: " + $installed + " -> " + $latest) }
elseif ($cmp -eq 0) { Say ("UP TO DATE: " + $installed + " is the latest release") }
elseif ($cmp -lt 0) { Say ("installed " + $installed + " is newer than " + $latest) }
if ($Check) { Say ("asset: " + $zipUrl); exit 0 }
if ($cmp -le 0 -and -not $Force) { Say 'nothing to do (pass -Force to reinstall anyway)'; Relaunch; exit 0 }

# ---------------------------------------------------------------- engine must be stopped from here on
RequireStopped

# ---------------------------------------------------------------- download + verify
$work = Join-Path $root '.update'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
$zipFile = Join-Path $work $zipName
Say ("downloading " + $zipUrl)
Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile -Headers @{ 'User-Agent' = 'TiaMcp-Update-Engine/1' } -UseBasicParsing -TimeoutSec ($TimeoutSeconds * 10)
$shaFile = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($zipName) + '.sha256')
Invoke-WebRequest -Uri $shaUrl -OutFile $shaFile -Headers @{ 'User-Agent' = 'TiaMcp-Update-Engine/1' } -UseBasicParsing -TimeoutSec $TimeoutSeconds
Say ("ZIP " + $zipFile + " (" + [math]::Round((Get-Item -LiteralPath $zipFile).Length / 1MB, 1) + " MB)")
$expected = ((Get-Content -LiteralPath $shaFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) { Fail ("SHA-256 mismatch: sidecar " + $expected + " vs file " + $actual + " - the download is corrupt or tampered; nothing was changed") }
Say ("SHA-256 verified " + $actual)

# ---------------------------------------------------------------- extract + check layout
# Extract under %TEMP% (short) rather than under the install root: the package's longest relative path is ~145
# characters and Windows PowerShell cannot extract or copy beyond 260. Check both destinations before touching anything.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$longest = 0; $longestName = ''
$archive = [IO.Compression.ZipFile]::OpenRead($zipFile)
try {
    foreach ($entry in $archive.Entries) {
        $rel = $entry.FullName
        $cut = $rel.IndexOf('/')
        if ($cut -ge 0) { $rel = $rel.Substring($cut + 1) }   # strip the top-level package folder
        if ($rel.Length -gt $longest) { $longest = $rel.Length; $longestName = $rel }
    }
} finally { $archive.Dispose() }
$extract = Join-Path ([IO.Path]::GetTempPath()) ('tia-mcp-update-' + $PID)
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
$limit = 250
$extractLongest = $extract.Length + 1 + $zipName.Length - 4 + 1 + $longest
if ($extractLongest -gt $limit) { Fail ('the temp folder is too deep for extraction: ' + $extract + ' -> ' + $extractLongest + ' > ' + $limit + ' characters; set TEMP to a shorter folder and run again') }
if ($root.Length + 1 + $longest -gt $limit) { Say ('note: the install root is deep (' + $root.Length + ' characters); the update copies with robocopy, but Explorer and other tools may not open the deepest source files (' + $longestName + ')') }
Say ("extracting to " + $extract + " (longest package path " + $longest + " characters)")
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
RemoveTree $backupDir
New-Item -ItemType Directory -Path $backupDir | Out-Null
Say ("backing up the current install to " + $backupDir)
CopyTree $root $backupDir @((Join-Path $root '.previous'), (Join-Path $root '.update'), (Join-Path $root 'TiaMcp_Output'), (Join-Path $root 'bin-build')) @('*.log')
foreach ($old in (Get-ChildItem -LiteralPath $previousRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2)) {
    RemoveTree $old.FullName; Say ("dropped old backup " + $old.Name)
}
Say 'replacing runtime\ and manifest\, overlaying the rest'
foreach ($dir in 'runtime', 'manifest') { RemoveTree (Join-Path $root $dir) }
CopyTree $package $root
Remove-Item -LiteralPath $work -Recurse -Force
RemoveTree $extract

$now = Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json
$exeVersion = (Get-Item -LiteralPath (Join-Path $root 'runtime\v21\TiaMcpServer.exe')).VersionInfo.FileVersion
Say ("DONE: " + $installed + " -> " + $now.release + " (runtime\v21\TiaMcpServer.exe " + $exeVersion + "). Start the engine and call Bootstrap to confirm serverVersion; -Rollback restores " + $installedPackage + ".")
Relaunch
