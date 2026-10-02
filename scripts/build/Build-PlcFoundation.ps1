#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [string[]]$ReleaseKeys=@('14sp1','15.1','16','17','18','19','20','21'),
    [string]$Dotnet='dotnet',
    [string]$NuGetConfig='',
    [switch]$UseReferenceAssemblyPackage,
    [switch]$NoRestore,
    [string]$EvidenceDirectory=''
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project=Join-Path $repo 'tools/tiaportal-mcp/src/TiaMcpServer.PlcFoundation/TiaMcpServer.PlcFoundation.csproj'
$apiRoot=(Resolve-Path -LiteralPath $PublicApiRoot).Path
if(!$EvidenceDirectory){$EvidenceDirectory=Join-Path $repo 'bin-build/plc-foundation'}
$null=New-Item -ItemType Directory -Force -Path $EvidenceDirectory
$out=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$directories=@{
    '14sp1'='TIA_V14SP1_PublicAPI/V14 SP1'; '15.1'='TIA_V15.1_PublicAPI/V15.1'
    '16'='TIA_V16_PublicAPI/V16'; '17'='TIA_V17_PublicAPI/V17'; '18'='TIA_V18_PublicAPI/V18'
    '19'='TIA_V19_PublicAPI/V19'; '20'='TIA_V20_PublicAPI/V20'; '21'='TIA_V21_PublicAPI/V21/net48'
}
$results=@()
foreach($key in $ReleaseKeys){
    if(!$directories.ContainsKey($key)){throw "Unsupported precise release key: $key (original V14/V15 are excluded)."}
    $api=(Resolve-Path -LiteralPath (Join-Path $apiRoot $directories[$key])).Path
    $core=Join-Path $api $(if($key -eq '21'){'Siemens.Engineering.Base.dll'}else{'Siemens.Engineering.dll'})
    # GetAssemblyName reads PE identity only; no Siemens code is loaded or invoked.
    $identity=[Reflection.AssemblyName]::GetAssemblyName($core).FullName
    $props=@("-p:TiaReleaseKey=$key","-p:SiemensEngineeringDirectory=$api",'-p:NuGetAudit=false')
    if($UseReferenceAssemblyPackage){$props+='-p:UseReferenceAssemblyPackage=true'}
    if(!$NoRestore){
        $restore=@('restore',$project,'-v:minimal')+$props
        if($NuGetConfig){$restore+=@('--configfile',(Resolve-Path -LiteralPath $NuGetConfig).Path)}
        & $Dotnet @restore *> (Join-Path $out "restore-$key.log")
        if($LASTEXITCODE -ne 0){throw "Reference restore failed for $key; see restore-$key.log. No framework substitution is allowed."}
    }
    & $Dotnet build $project -c Release --no-restore -v:minimal @props *> (Join-Path $out "build-$key.log")
    if($LASTEXITCODE -ne 0){throw "Compile-only PLC foundation failed for $key; see build-$key.log."}
    $framework=if($key -in @('14sp1','15.1','16')){'net461'}else{'net48'}
    $binary=Join-Path (Split-Path $project) "bin/$key/Release/$framework/TiaMcp.PlcFoundation.$key.dll"
    $results+= [ordered]@{releaseKey=$key;targetFramework=$framework;referenceIdentity=$identity;referenceSha256=(Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash;binarySha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash;compile='passed';nativeAcceptance='NOT RUN';productionCatalog='disabled for legacy releases'}
    Write-Output "$key $framework compile passed; native acceptance NOT RUN"
}
$evidence=[ordered]@{generatedAt=[DateTimeOffset]::UtcNow.ToString('o');scope='typed PLC foundation modules only, no host launch or Siemens API execution';results=$results}
[IO.File]::WriteAllText((Join-Path $out 'summary.json'),($evidence | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
