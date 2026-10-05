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
    4. Downloads the ZIP + .sha256 to <root>\.update\, verifies the SHA-256, extracts under <root>\data\temp\ when
       the longest extracted path is below 240 characters (otherwise %TEMP%) and checks the package layout.
       Windows PowerShell's Expand-Archive, Copy-Item and Remove-Item stop at
       260-character paths and the package holds paths of ~158 characters below its root, so every tree copy and
       delete goes through robocopy (long-path safe) and only the extraction folder is length-checked.
    5. Backs up the current install to <root>\.previous\<package>\ (last two kept), replaces runtime\ and manifest\
       using recorded file ownership, removes retired development files and overlays the new package.
       Rollback restores the prior layout; data\ and unknown user files are preserved.
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
    [switch]$RelaunchConfigurator,
    [switch]$SelfTest
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

# robocopy copies and deletes trees regardless of the 260-character limit that stops Copy-Item / Remove-Item
# (the install root + .previous\<package>\ + the deepest package file easily exceed it). Exit codes below 8 are success.
function CopyTree([string]$from, [string]$to, [string[]]$excludeDirs = @(), [string[]]$excludeFiles = @()) {
    $rc = @($from, $to, '/E', '/COPY:DAT', '/DCOPY:T', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($excludeDirs.Count -gt 0) { $rc += '/XD'; $rc += $excludeDirs }
    if ($excludeFiles.Count -gt 0) { $rc += '/XF'; $rc += $excludeFiles }
    & robocopy.exe @rc | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ('robocopy failed (exit ' + $LASTEXITCODE + ') copying ' + $from + ' -> ' + $to) }
}
function ReadDeliveryRules([string]$directory) {
    $rules = Get-Content -LiteralPath (Join-Path $directory 'scripts/operations/delivery-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($rules.schemaVersion -ne 1) { throw 'Unsupported delivery-files schema' }
    return $rules
}
function MatchesRule([string]$relative, $rule) {
    if (@($rule.files) -ccontains $relative) { return $true }
    foreach ($prefix in $rule.prefixes) { if ($relative.StartsWith($prefix, [StringComparison]::Ordinal)) { return $true } }
    return $false
}
function InDelivery([string]$relative, $rules) {
    return (MatchesRule $relative $rules.include) -and -not (MatchesRule $relative $rules.exclude)
}
function OwnedPath([string]$directory, [string]$relative) {
    if (-not $relative -or $relative -match '[\\:*?\[\]]' -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw ('Unsafe package path: ' + $relative) }
    if ($relative.Split('/')[0] -in @('data', '.previous', '.update', 'TiaMcp_Output', '.git', 'bin-build')) { throw ('Protected package path: ' + $relative) }
    $boundary = [IO.Path]::GetFullPath($directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $directory $relative))
    if (-not $path.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw ('Package path leaves install root: ' + $relative) }
    for ($probe = $path; $probe.Length -ge $boundary.Length; $probe = Split-Path -Parent $probe) {
        if ((Test-Path -LiteralPath $probe) -and ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse point in package path: ' + $relative) }
    }
    return $path
}
function RemoveOwnedFile([string]$directory, [string]$relative, [string]$expectedHash) {
    $path = OwnedPath $directory $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash) {
        Say ('preserving modified file ' + $relative)
        return
    }
    Remove-Item -LiteralPath $path -Force
    $boundary = [IO.Path]::GetFullPath($directory).TrimEnd('\', '/')
    $parent = Split-Path -Parent $path
    while ($parent.Length -gt $boundary.Length -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) {
        # Every ancestor is inside the already checked install boundary; no recursive deletion.
        Remove-Item -LiteralPath $parent -Force
        $parent = Split-Path -Parent $parent
    }
}
function InstalledInventory([string]$directory) {
    $inventory = @{}
    $record = Join-Path $directory 'manifest/release-file-hashes.json'
    if (Test-Path -LiteralPath $record) {
        $hashes = (Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json).files
        foreach ($property in $hashes.PSObject.Properties) { $inventory[$property.Name] = [string]$property.Value }
        $inventory['manifest/release-file-hashes.json'] = (Get-FileHash -LiteralPath $record -Algorithm SHA256).Hash
    } else {
        # Runtime-only packages have no generated file-list extra. Their build records
        # still identify every binary; unknown user files are never inferred from folders.
        foreach ($name in @('release-build.json', 'multi-version-build.json')) {
            $record = Get-Content -LiteralPath (Join-Path $directory "manifest/$name") -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($row in @($record.runtimeFiles) + @($record.files)) {
                if ($null -ne $row) { $inventory[$row.path] = [string]$row.sha256 }
            }
        }
    }
    return $inventory
}
function RemoveLegacyFiles([string]$directory, $rules, $inventory) {
    foreach ($relative in $inventory.Keys) {
        if ((MatchesRule $relative $rules.legacyCleanup) -and -not (InDelivery $relative $rules)) {
            RemoveOwnedFile $directory $relative $inventory[$relative]
        }
    }
}
function RemoveAddedFiles([string]$directory, [string]$backupDirectory, $receipt) {
    foreach ($property in $receipt.PSObject.Properties) {
        $previous = OwnedPath $backupDirectory $property.Name
        if (-not (Test-Path -LiteralPath $previous)) { RemoveOwnedFile $directory $property.Name ([string]$property.Value) }
    }
}
if ($SelfTest) {
    # Offline fixture only: no install probing, process control, network or real update.
    $testParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../bin-build'))
    $fixture = Join-Path $testParent ('updater-test-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    $passed = 0
    function AssertTest([bool]$condition, [string]$label) {
        if (-not $condition) { throw ('Self-test failed: ' + $label) }
        $script:passed++; Write-Output ('PASS: ' + $label)
    }
    try {
        $rules = ReadDeliveryRules (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
        $inventory = @{}
        foreach ($relative in @('tools/old/source.cs', 'tools/old/user.txt', 'tools/old/modified.cs', 'plugin/skill/SKILL.md', 'scripts/checks/old.py', 'AGENTS.md', 'user.txt', 'data/config/user.json', '.previous/keep', '.update/keep', 'TiaMcp_Output/keep')) {
            $path = Join-Path $fixture $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            Set-Content -LiteralPath $path -Value 'original'
            if ($relative -notmatch '(user|keep)') { $inventory[$relative] = (Get-FileHash -LiteralPath $path).Hash }
        }
        Set-Content -LiteralPath (Join-Path $fixture 'tools/old/modified.cs') -Value 'user edit'
        RemoveLegacyFiles $fixture $rules $inventory
        foreach ($relative in @('tools/old/source.cs', 'scripts/checks/old.py', 'AGENTS.md')) { AssertTest (-not (Test-Path -LiteralPath (Join-Path $fixture $relative))) ('removed ' + $relative) }
        foreach ($relative in @('tools/old/user.txt', 'tools/old/modified.cs', 'plugin/skill/SKILL.md', 'user.txt', 'data/config/user.json', '.previous/keep', '.update/keep', 'TiaMcp_Output/keep')) { AssertTest (Test-Path -LiteralPath (Join-Path $fixture $relative)) ('preserved ' + $relative) }
        foreach ($relative in @('../outside', '/absolute', 'C:/outside', 'data/config/user.json', '.previous/keep', '.update/keep', 'TiaMcp_Output/keep')) {
            $rejected = $false
            try { $null = OwnedPath $fixture $relative } catch { $rejected = $true }
            AssertTest $rejected ('reject ' + $relative)
        }
        AssertTest (InDelivery 'runtime/v21/TiaMcpServer.exe' $rules) 'include runtime'
        AssertTest (-not (InDelivery 'runtime/verification/NativeCallWeaver.dll' $rules)) 'exclude verifier'
        $backup = Join-Path $fixture 'backup'
        New-Item -ItemType Directory -Path $backup | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixture 'user.txt') -Destination $backup
        Set-Content -LiteralPath (Join-Path $fixture 'new.txt') -Value 'new package file'
        $receipt = [pscustomobject]@{ 'new.txt' = (Get-FileHash -LiteralPath (Join-Path $fixture 'new.txt')).Hash; 'user.txt' = (Get-FileHash -LiteralPath (Join-Path $fixture 'user.txt')).Hash }
        RemoveAddedFiles $fixture $backup $receipt
        AssertTest (-not (Test-Path -LiteralPath (Join-Path $fixture 'new.txt'))) 'rollback removes added package file'
        AssertTest (Test-Path -LiteralPath (Join-Path $fixture 'user.txt')) 'rollback retains previous layout file'
        $install = Join-Path $fixture 'install'
        $snapshot = Join-Path $fixture 'snapshot'
        New-Item -ItemType Directory -Path $install | Out-Null
        $before = @{}
        foreach ($relative in @('tools/development/source.cs', 'tools/development/user.txt', 'runtime/v21/old.dll', 'README.md', 'data/config/keep.json')) {
            $path = Join-Path $install $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            Set-Content -LiteralPath $path -Value $relative
            $before[$relative] = (Get-FileHash -LiteralPath $path).Hash
        }
        New-Item -ItemType Directory -Path (Join-Path $install 'manifest') | Out-Null
        @{ files = @{ 'tools/development/source.cs' = $before['tools/development/source.cs']; 'runtime/v21/old.dll' = $before['runtime/v21/old.dll'] } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $install 'manifest/release-file-hashes.json') -Encoding UTF8
        $before['manifest/release-file-hashes.json'] = (Get-FileHash -LiteralPath (Join-Path $install 'manifest/release-file-hashes.json')).Hash
        $owned = InstalledInventory $install
        AssertTest ($owned.ContainsKey('tools/development/source.cs')) 'read old full-package inventory'
        AssertTest (-not $owned.ContainsKey('tools/development/user.txt')) 'inventory excludes unknown user file'
        CopyTree $install $snapshot @((Join-Path $install 'data'))
        RemoveLegacyFiles $install $rules $owned
        RemoveOwnedFile $install 'runtime/v21/old.dll' $before['runtime/v21/old.dll']
        New-Item -ItemType Directory -Force -Path (Join-Path $install 'runtime/v21') | Out-Null
        Set-Content -LiteralPath (Join-Path $install 'runtime/v21/new.dll') -Value 'new runtime'
        Set-Content -LiteralPath (Join-Path $install 'README.md') -Value 'new README'
        $receipt = [pscustomobject]@{ 'runtime/v21/new.dll' = (Get-FileHash -LiteralPath (Join-Path $install 'runtime/v21/new.dll')).Hash; 'README.md' = (Get-FileHash -LiteralPath (Join-Path $install 'README.md')).Hash }
        RemoveAddedFiles $install $snapshot $receipt
        CopyTree $snapshot $install
        $after = @{}
        foreach ($file in Get-ChildItem -LiteralPath $install -Recurse -File) { $after[$file.FullName.Substring($install.Length + 1).Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName).Hash }
        AssertTest ((($before.Keys | Sort-Object) -join ',') -ceq (($after.Keys | Sort-Object) -join ',')) 'rollback restores exact file set'
        foreach ($relative in $before.Keys) { AssertTest ($after[$relative] -eq $before[$relative]) ('rollback bytes ' + $relative) }
        Write-Output ("Updater self-tests: $passed passed, 0 failed")
    } finally {
        if ([IO.Path]::GetFullPath((Split-Path -Parent $fixture)) -ne $testParent) { throw 'Fixture escaped test directory' }
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
    exit 0
}

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
function GetScratchRoot([int]$longestEntry = 0) {
    $candidate = Join-Path $root 'data\temp'
    $extraction = Join-Path $candidate ('tia-mcp-update-' + $PID)
    # Leave room below Windows PowerShell's 260-character extraction limit. The archive
    # entry length includes its top-level package folder; robocopy handles longer copies.
    if ($extraction.Length + 1 + $longestEntry -lt 240) { return $candidate }
    return [IO.Path]::GetTempPath()
}
$scratchRoot = GetScratchRoot
function RemoveTree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $empty = Join-Path $scratchRoot ('tia-mcp-empty-' + $PID)
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
    $receiptPath = $backup.FullName + '.update-receipt.json'
    if (Test-Path -LiteralPath $receiptPath) {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        RemoveAddedFiles $root $backup.FullName $receipt
    } else {
        # Backups made by older updaters have no receipt; retain their rollback
        # support using the installed release's recorded files, never directory guesses.
        RemoveAddedFiles $root $backup.FullName ([pscustomobject](InstalledInventory $root))
    }
    CopyTree $backup.FullName $root @((Join-Path $backup.FullName 'data'))
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
# Prefer package-local scratch only below 240 characters, otherwise retain the short %TEMP% path.
# Windows PowerShell cannot extract beyond 260; check the actual archive paths before touching the install.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$longest = 0; $longestName = ''; $longestEntry = 0
$archive = [IO.Compression.ZipFile]::OpenRead($zipFile)
try {
    foreach ($entry in $archive.Entries) {
        $rel = $entry.FullName
        $longestEntry = [math]::Max($longestEntry, $rel.Length)
        $cut = $rel.IndexOf('/')
        if ($cut -ge 0) { $rel = $rel.Substring($cut + 1) }   # strip the top-level package folder
        if ($rel.Length -gt $longest) { $longest = $rel.Length; $longestName = $rel }
    }
} finally { $archive.Dispose() }
$scratchRoot = GetScratchRoot $longestEntry
$extract = Join-Path $scratchRoot ('tia-mcp-update-' + $PID)
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
$limit = 250
$extractLongest = $extract.Length + 1 + $longestEntry
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
$rules = ReadDeliveryRules $package
$newFiles = @{}
foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File -Force) {
    $relative = $file.FullName.Substring($package.Length + 1).Replace('\', '/')
    $null = OwnedPath $package $relative
    if (-not (InDelivery $relative $rules)) { Fail ('File outside delivery set: ' + $relative) }
    $newFiles[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
foreach ($must in @($rules.include.files) + @($rules.include.prefixes)) {
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
CopyTree $root $backupDir @((Join-Path $root '.previous'), (Join-Path $root '.update'), (Join-Path $root 'data'), (Join-Path $root 'TiaMcp_Output'), (Join-Path $root 'bin-build')) @('*.log')
# The receipt is written before any install mutation, so a failed update can also roll back.
# Store metadata beside the snapshot, outside the restored install namespace.
$newFiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($backupDir + '.update-receipt.json') -Encoding UTF8
(Get-Item -LiteralPath $backupDir).LastWriteTime = Get-Date
$oldFiles = InstalledInventory $root
foreach ($old in (Get-ChildItem -LiteralPath $previousRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -Skip 2)) {
    $oldReceipt = OwnedPath $previousRoot ($old.Name + '.update-receipt.json')
    if (Test-Path -LiteralPath $oldReceipt) { Remove-Item -LiteralPath $oldReceipt -Force }
    RemoveTree $old.FullName; Say ("dropped old backup " + $old.Name)
}
Say 'removing retired package files and overlaying the new delivery'
RemoveLegacyFiles $root $rules $oldFiles
foreach ($relative in $oldFiles.Keys) {
    if (($relative.StartsWith('runtime/') -or $relative.StartsWith('manifest/')) -and -not $newFiles.ContainsKey($relative)) {
        RemoveOwnedFile $root $relative $oldFiles[$relative]
    }
}
CopyTree $package $root @((Join-Path $package 'data'))
Remove-Item -LiteralPath $work -Recurse -Force
RemoveTree $extract

$now = Get-Content -LiteralPath $deliveryJson -Raw | ConvertFrom-Json
$exeVersion = (Get-Item -LiteralPath (Join-Path $root 'runtime\v21\TiaMcpServer.exe')).VersionInfo.FileVersion
Say ("DONE: " + $installed + " -> " + $now.release + " (runtime\v21\TiaMcpServer.exe " + $exeVersion + "). Start the engine and call Bootstrap to confirm serverVersion; -Rollback restores " + $installedPackage + ".")
Relaunch
