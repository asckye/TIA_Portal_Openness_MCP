#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [Parameter(Mandatory=$true)][string]$NativeCallWeaverPath,
    [string]$Dotnet='dotnet'
)
$ErrorActionPreference='Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$root=Split-Path $PSScriptRoot -Parent
$worker=Join-Path $root '../TiaMcpServer.PlcWorker/TiaMcpServer.PlcWorker.csproj'
$weaver=(Resolve-Path -LiteralPath $NativeCallWeaverPath).Path
$null=New-Item -ItemType Directory -Force -Path $EvidenceDirectory
$out=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$results=New-Object System.Collections.Generic.List[object]
function Record($name,$pass,$code){$results.Add([ordered]@{name=$name;passed=$pass;exitCode=$code});Write-Output ($name+': '+$pass)}
function HasSelectedReferences($value,$adapterSuffix){
    $references=@($value.Items.ProjectReference)
    # Step B added Contracts; still require exactly one selected native adapter and no others.
    return $references.Count -eq 2 -and
        @($references | Where-Object {$_.Identity.Replace('\','/').EndsWith($adapterSuffix)}).Count -eq 1 -and
        @($references | Where-Object {$_.Identity.Replace('\','/').EndsWith('/TiaMcp.Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj')}).Count -eq 1
}
foreach($key in @('14sp1','15.1','16','17','18','19','20','21')){
    $log=Join-Path $out "selection-$key.json"
    & $Dotnet msbuild $worker -nologo -v:quiet -t:ValidateWorkerAdapter "-p:TiaReleaseKey=$key" -getItem:ProjectReference -getProperty:TargetFramework *> $log
    $code=$LASTEXITCODE
    $value=if($code -eq 0){Get-Content -LiteralPath $log -Raw|ConvertFrom-Json}else{$null}
    $tfm='net48'
    Record "selection-$key" ($code -eq 0 -and (HasSelectedReferences $value "/Adapter.$key.csproj") -and $value.Properties.TargetFramework -eq $tfm) $code
    $binary=Join-Path (Split-Path $worker) "bin/$key/Release/$tfm/TiaMcp.Adapter.$key.dll"
    & $Dotnet $weaver verify $binary (Join-Path $out "coverage-$key.json") *> (Join-Path $out "verify-$key.log")
    Record "coverage-$key" ($LASTEXITCODE -eq 0) $LASTEXITCODE
    & $Dotnet $weaver self-test $binary *> (Join-Path $out "coverage-rejection-$key.log")
    Record "coverage-rejection-$key" ($LASTEXITCODE -eq 0) $LASTEXITCODE
}
$cases=@(
    @('missing-key',@('-p:TargetFramework=net48'),'exact release adapters'),
    @('excluded-v14',@('-p:TiaReleaseKey=14','-p:TargetFramework=net48'),'exact release adapters'),
    @('excluded-v15',@('-p:TiaReleaseKey=15','-p:TargetFramework=net48'),'exact release adapters'),
    @('future-version',@('-p:TiaReleaseKey=22','-p:TargetFramework=net48'),'exact release adapters'),
    @('wrong-framework',@('-p:TiaReleaseKey=20','-p:TargetFramework=net461'),'framework mismatch'),
    @('wrong-platform',@('-p:TiaReleaseKey=20','-p:PlatformTarget=x86'),'x64 executable'),
    @('wrong-output',@('-p:TiaReleaseKey=20','-p:OutputType=Library'),'x64 executable'),
    @('glob-sources',@('-p:TiaReleaseKey=20','-p:EnableDefaultCompileItems=true'),'explicit allowlist')
)
foreach($case in $cases){
    $log=Join-Path $out ($case[0]+'.log')
    & $Dotnet msbuild $worker -nologo -v:minimal -t:ValidateWorkerAdapter @($case[1]) *> $log
    $code=$LASTEXITCODE
    Record $case[0] ($code -ne 0 -and [IO.File]::ReadAllText($log).Contains($case[2])) $code
}
$adapter=Join-Path $root 'V20/Adapter.20.csproj'
$log=Join-Path $out 'identity-override.json'
& $Dotnet msbuild $worker -nologo -v:quiet -t:ValidateWorkerAdapter -p:TiaReleaseKey=20 -p:SelectedAdapterDirectory=V21 -p:SelectedAdapterProject=invalid.csproj -getItem:ProjectReference *> $log
$code=$LASTEXITCODE
$value=if($code -eq 0){Get-Content -LiteralPath $log -Raw|ConvertFrom-Json}else{$null}
Record 'identity-override-ignored' ($code -eq 0 -and (HasSelectedReferences $value '/V20/Adapter.20.csproj')) $code
# The obsolete blanket Publish/Pack refusal was removed when per-release packaging was enabled.
# Build-MultiVersion verifies deployed adapter identities and exercises real host/worker IPC.
$log=Join-Path $out 'missing-weaver.log'
& $Dotnet msbuild $adapter -nologo -v:minimal -t:InstrumentAdapterNativeBoundaries "-p:NativeCallWeaverPath=$out/absent-weaver.dll" *> $log
$code=$LASTEXITCODE
Record 'missing-weaver' ($code -ne 0 -and [IO.File]::ReadAllText($log).Contains('Uninstrumented adapter output is refused')) $code
[IO.File]::WriteAllText((Join-Path $out 'worker-isolation-results.json'),($results|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
if(@($results|Where-Object {!$_.passed}).Count){throw 'Worker isolation checks failed.'}
