#Requires -Version 5.1
# Lays out the pinned Microsoft .NET runtime in runtime/dotnet. Archives come from the official
# Microsoft download location, are cached under bin-build/cache and must match the SHA-512 recorded
# in bundled-dotnet.json (copied from Microsoft's release metadata) before anything is extracted.
param(
    [string]$Cache='',
    [switch]$Offline
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$pin=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bundled-dotnet.json') -Raw | ConvertFrom-Json
if(!$Cache){$Cache=Join-Path $repo "bin-build/cache/dotnet-$($pin.version)"}
New-Item -ItemType Directory -Force $Cache | Out-Null
$target=Join-Path $repo $pin.target
$archives=@()
foreach($archive in $pin.archives) {
    if($archive.url -notmatch '^https://builds\.dotnet\.microsoft\.com/dotnet/'){throw "Only the official Microsoft download location is allowed: $($archive.url)"}
    $file=Join-Path $Cache $archive.name
    if(!(Test-Path -LiteralPath $file)) {
        if($Offline){throw "Missing cached archive (download disabled): $file"}
        $partial="$file.partial"
        [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $archive.url -OutFile $partial -UseBasicParsing
        Move-Item -LiteralPath $partial -Destination $file -Force
    }
    $actual=(Get-FileHash -LiteralPath $file -Algorithm SHA512).Hash.ToLowerInvariant()
    if($actual -ne $archive.sha512.ToLowerInvariant()){Remove-Item -LiteralPath $file -Force; throw "SHA-512 mismatch for $($archive.name); the cached copy was removed"}
    $archives+=$file
}
if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
New-Item -ItemType Directory -Force $target | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$seen=@{}
foreach($file in $archives) {
    $zip=[IO.Compression.ZipFile]::OpenRead($file)
    try {
        foreach($entry in $zip.Entries) {
            if(!$entry.Name){continue}
            $relative=$entry.FullName.Replace('\','/')
            if($relative -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($relative)){throw "Unsafe archive path: $relative"}
            # The two archives share no files; a repeat would mean a changed layout that needs review.
            if($seen.ContainsKey($relative)){throw "Archives overlap at $relative"}
            $seen[$relative]=$true
            $destination=Join-Path $target $relative
            New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$destination,$false)
        }
    } finally {$zip.Dispose()}
}
foreach($framework in $pin.frameworks) {
    if(!(Test-Path -LiteralPath (Join-Path $target "shared/$framework/$($pin.version)"))){throw "Bundled runtime lacks $framework $($pin.version)"}
}
if(!(Test-Path -LiteralPath (Join-Path $target "host/fxr/$($pin.version)/hostfxr.dll"))){throw 'Bundled runtime lacks hostfxr'}
foreach($name in 'LICENSE.txt','ThirdPartyNotices.txt'){if(!(Test-Path -LiteralPath (Join-Path $target $name))){throw "Bundled runtime lacks $name"}}
Write-Output "Microsoft .NET $($pin.version) ($($pin.frameworks -join ', ')) laid out in $($pin.target): $($seen.Count) files"
