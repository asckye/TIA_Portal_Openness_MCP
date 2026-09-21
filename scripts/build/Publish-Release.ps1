#requires -Version 5.1
<#
.SYNOPSIS
  Publish a packaged delivery ZIP as a GitHub Release from this machine (draft -> upload -> verify -> publish).

.DESCRIPTION
  Since 2.8.1 the binaries are not tracked in Git and no workflow can build the package, so the maintainer's machine
  uploads it. Release.ps1 calls this after the tag is pushed; it can also be run by hand for a ZIP that
  Package-Release.py already produced (bin-build/releases/vX.Y.Z/<package>.zip + .sha256 + package-result.json).

    1. Token: -Token, else $env:GITHUB_TOKEN, else the credential Git Credential Manager stores for github.com
       (git credential fill). Never printed.
    2. The tag vX.Y.Z must exist on GitHub and point at the packaged sourceCommit; master HEAD must be that commit.
    3. Creates the release as a DRAFT (or reuses a draft for the tag; a published release is never touched),
       body = docs/releases/vX.Y.Z.md with relative links rewritten to blob URLs + package line + SHA-256.
    4. Uploads the ZIP and the .sha256; a stale asset on the draft is replaced; each upload is re-read from the
       API and must report state uploaded, the exact size and sha256 digest (up to 6 attempts).
    5. Publishes the draft (make_latest) and writes bin-build/releases/vX.Y.Z/published-release.json.

  -DraftOnly stops after the uploads (the draft stays for inspection); -DeleteDraft removes a draft of the tag.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build/Publish-Release.ps1 -Version 2.8.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Token = '',
    [string]$Repository = 'asckye/TIA_Portal_Openness_MCP',
    [string]$Git = 'git',
    [switch]$DraftOnly,
    [switch]$DeleteDraft
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Say([string]$text) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }
function Fail([string]$text) { Write-Host ("FAIL: " + $text) -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- token
if (-not $Token) { $Token = $env:GITHUB_TOKEN }
function GitCredentialToken([string]$gitExe) {
    # git credential fill with the request piped by cmd from a temp file: PowerShell's own pipeline and .NET's
    # redirected stdin both make git answer "missing protocol field" (encoding / launcher quirks), cmd does not
    $tmp = Join-Path $env:TEMP ('git-credential-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllBytes($tmp, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    try {
        $exe = (Get-Command $gitExe).Source
        $text = (& cmd.exe /c ('type "' + $tmp + '" | "' + $exe + '" credential fill 2>nul')) -join "`n"
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    $m = [regex]::Match([string]$text, '(?m)^password=(.+)$')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}
if (-not $Token) { $Token = GitCredentialToken $Git }
if (-not $Token) { Fail 'no GitHub token: pass -Token, set GITHUB_TOKEN, or sign in once with git push so Git Credential Manager stores one' }
$api = 'https://api.github.com/repos/' + $Repository
$headers = @{ Authorization = ('Bearer ' + $Token); Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'TiaMcp-Release' }
function Api([string]$method, [string]$url, $body = $null) {
    if ($null -eq $body) { return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -TimeoutSec 120 }
    $json = $body | ConvertTo-Json -Depth 6 -Compress
    return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -TimeoutSec 120 -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
}
function ErrorText($err) { try { $s = $err.Exception.Response.GetResponseStream(); $r = New-Object IO.StreamReader($s); return $r.ReadToEnd() } catch { return $err.Exception.Message } }

$tag = 'v' + $Version
$who = Api GET 'https://api.github.com/user'
Say ('GitHub token for ' + $who.login + ' (' + $Repository + ')')

# ---------------------------------------------------------------- package
$out = Join-Path $repo ('bin-build\releases\' + $tag)
$resultPath = Join-Path $out 'package-result.json'
if (-not (Test-Path -LiteralPath $resultPath)) { Fail ('no package-result.json in ' + $out + ' - run Package-Release.py first') }
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$zip = [string]$result.path
$sidecar = [IO.Path]::ChangeExtension($zip, '.sha256')
if (-not (Test-Path -LiteralPath $zip) -or -not (Test-Path -LiteralPath $sidecar)) { Fail ('ZIP or .sha256 missing: ' + $zip) }
$zipName = [IO.Path]::GetFileName($zip); $sidecarName = [IO.Path]::GetFileName($sidecar)
$zipBytes = [IO.File]::ReadAllBytes($zip)
$digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($digest -ne $result.sha256 -or $zipBytes.Length -ne $result.size) { Fail 'ZIP differs from package-result.json (size or sha256)' }
$commit = [string]$result.sourceCommit
$delivery = Get-Content -LiteralPath (Join-Path $repo 'manifest\delivery.json') -Raw | ConvertFrom-Json
if ($delivery.release -ne $Version -or ($delivery.package + '.zip') -ne $zipName) { Fail ('manifest/delivery.json says ' + $delivery.release + ' / ' + $delivery.package + ', not ' + $Version + ' / ' + $zipName) }
Say ($zipName + ' ' + $zipBytes.Length + ' bytes sha256 ' + $digest + ' from commit ' + $commit.Substring(0, 12))

# ---------------------------------------------------------------- tag + master
$master = Api GET ($api + '/branches/master')
if ($master.commit.sha -ne $commit) { Fail ('master HEAD is ' + $master.commit.sha + ' but the package was built from ' + $commit + ' - re-package or push first') }
try { $ref = Api GET ($api + '/git/ref/tags/' + $tag) } catch { Fail ('tag ' + $tag + ' is not on GitHub yet (' + (ErrorText $_) + '); push it first') }
$target = $ref.object
if ($target.type -eq 'tag') { $target = (Api GET ($api + '/git/tags/' + $target.sha)).object }
if ($target.type -ne 'commit' -or $target.sha -ne $commit) { Fail ('tag ' + $tag + ' points at ' + $target.sha + ', not the packaged commit ' + $commit) }
Say ('tag ' + $tag + ' = master HEAD = ' + $commit.Substring(0, 12))

# ---------------------------------------------------------------- release (draft)
$existing = $null
try { $existing = Api GET ($api + '/releases/tags/' + $tag) } catch { if (-not ((ErrorText $_) -like '*Not Found*')) { Fail ('reading release ' + $tag + ': ' + (ErrorText $_)) } }
if (-not $existing) {
    # releases/tags/<tag> does not return drafts; look through the list
    $existing = @(Api GET ($api + '/releases?per_page=100')) | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
}
if ($DeleteDraft) {
    if ($existing -and $existing.draft) { Api DELETE ($api + '/releases/' + $existing.id) | Out-Null; Say ('draft ' + $tag + ' deleted'); exit 0 }
    Fail ('no draft release for ' + $tag)
}
if ($existing -and -not $existing.draft) { Fail ('release ' + $tag + ' is already published (' + $existing.html_url + '); assets of a published release are never touched - use a new version') }
$notePath = Join-Path $repo ('docs\releases\' + $tag + '.md')
$notes = [IO.File]::ReadAllText($notePath, [Text.Encoding]::UTF8)
$notes = [regex]::Replace($notes, '\]\((\.\./[^)]+)\)', { param($m) '](https://github.com/' + $Repository + '/blob/' + $commit + '/docs/releases/' + $m.Groups[1].Value + ')' })
$body = $notes + "`n`nPackage: ``" + $zipName + "`` (" + $result.files + " files, " + $result.size + " bytes).`n`nSource commit: [" + $commit + "](https://github.com/" + $Repository + "/commit/" + $commit + ").`n`nZIP SHA-256:`n`n``````text`n" + $digest + "`n``````" + "`n"
if ($existing) { $release = $existing; Say ('reusing draft release ' + $release.id) }
else {
    $release = Api POST ($api + '/releases') @{ tag_name = $tag; target_commitish = $commit; name = $tag; body = $body; draft = $true; prerelease = $false; make_latest = 'false' }
    Say ('draft release created: ' + $release.id)
}

# ---------------------------------------------------------------- assets
$uploadBase = [string]$release.upload_url -replace '\{\?name,label\}$', ''
foreach ($file in @($zip, $sidecar)) {
    $name = [IO.Path]::GetFileName($file)
    $bytes = [IO.File]::ReadAllBytes($file)
    $expected = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $contentType = $(if ($name -like '*.zip') { 'application/zip' } else { 'text/plain' })
    $done = $false
    for ($attempt = 1; $attempt -le 6 -and -not $done; $attempt++) {
        $assets = @(Api GET ($api + '/releases/' + $release.id + '/assets?per_page=100'))
        foreach ($stale in ($assets | Where-Object { $_.name -eq $name })) {
            if ($stale.state -eq 'uploaded' -and $stale.size -eq $bytes.Length -and $stale.digest -eq $expected) { Say ($name + ': already uploaded and verified'); $done = $true; break }
            Say ($name + ': removing stale draft asset ' + $stale.id + ' (state ' + $stale.state + ', ' + $stale.size + ' bytes)')
            Api DELETE ($api + '/releases/assets/' + $stale.id) | Out-Null
        }
        if ($done) { break }
        Say ($name + ': uploading ' + $bytes.Length + ' bytes (attempt ' + $attempt + ')')
        try {
            $asset = Invoke-RestMethod -Method POST -Uri ($uploadBase + '?name=' + [Uri]::EscapeDataString($name)) -Headers $headers -ContentType $contentType -InFile $file -TimeoutSec 900
        } catch { Say ($name + ': upload failed: ' + (ErrorText $_)); Start-Sleep -Seconds (15 * $attempt); continue }
        $check = Api GET ($api + '/releases/assets/' + $asset.id)
        if ($check.state -eq 'uploaded' -and $check.size -eq $bytes.Length -and $check.digest -eq $expected) { Say ($name + ': verified ' + $check.size + ' bytes ' + $check.digest); $done = $true }
        else { Say ($name + ': verification failed (state ' + $check.state + ', ' + $check.size + ' bytes, ' + $check.digest + ')'); Start-Sleep -Seconds (15 * $attempt) }
    }
    if (-not $done) { Fail ($name + ': could not upload and verify after 6 attempts; the draft release ' + $release.id + ' is kept for inspection') }
}
if ($DraftOnly) { Say ('draft ' + $tag + ' ready with both assets (-DraftOnly): ' + $release.html_url); exit 0 }

# ---------------------------------------------------------------- publish
$published = Api PATCH ($api + '/releases/' + $release.id) @{ name = $tag; body = $body; draft = $false; prerelease = $false; make_latest = 'true' }
if ($published.draft) { Fail 'release is still a draft after publishing' }
$final = @(Api GET ($api + '/releases/' + $release.id + '/assets?per_page=100'))
if (@($final | Where-Object { $_.name -eq $zipName -and $_.state -eq 'uploaded' }).Count -ne 1 -or @($final | Where-Object { $_.name -eq $sidecarName -and $_.state -eq 'uploaded' }).Count -ne 1) { Fail 'published release does not list both assets' }
[IO.File]::WriteAllText((Join-Path $out 'published-release.json'), ($published | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Say ('PUBLISHED ' + $tag + ': ' + $published.html_url)
