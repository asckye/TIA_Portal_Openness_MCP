#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$PublicApiRoot,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$Dotnet='dotnet'
)
$ErrorActionPreference='Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$root=Split-Path $PSScriptRoot -Parent
$null=New-Item -ItemType Directory -Force -Path $EvidenceDirectory
$out=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$source=(Resolve-Path -LiteralPath $SourceRoot).Path
$apiRoot=(Resolve-Path -LiteralPath $PublicApiRoot).Path
$cases=@(
    @('V14Sp1','14sp1','TIA_V14SP1_PublicAPI/V14 SP1'),
    @('V15_1','15.1','TIA_V15.1_PublicAPI/V15.1'),
    @('V16','16','TIA_V16_PublicAPI/V16'),
    @('V17','17','TIA_V17_PublicAPI/V17'),
    @('V18','18','TIA_V18_PublicAPI/V18'),
    @('V19','19','TIA_V19_PublicAPI/V19'),
    @('V20','20','TIA_V20_PublicAPI/V20'),
    @('V21','21','TIA_V21_PublicAPI/V21/net48')
)
$results=New-Object System.Collections.Generic.List[object]
function Check([string]$name,[object[]]$case,[string]$api,[string[]]$extra,[string]$expectedError=''){
    $project=Join-Path $root ($case[0]+'/Adapter.'+$case[1]+'.csproj')
    $log=Join-Path $out ($name+'.log')
    $buildOutput=Join-Path $out ($name+'-'+[Guid]::NewGuid().ToString('N'))
    # This target only checks source existence and PE identity; no restore,
    # compilation, worker process, Siemens assembly load, or native invocation.
    & $Dotnet msbuild $project -nologo -v:minimal -t:ValidateAdapterInputs "-p:AdapterSourceRoot=$source" "-p:SiemensEngineeringDirectory=$api" "-p:BaseIntermediateOutputPath=$buildOutput/obj/" "-p:BaseOutputPath=$buildOutput/bin/" @extra *> $log
    $code=$LASTEXITCODE
    $text=[IO.File]::ReadAllText($log)
    $noCompileOutput=!(Test-Path -LiteralPath $buildOutput) -or @(Get-ChildItem -LiteralPath $buildOutput -Recurse -File).Count -eq 0
    $passed=if(!$expectedError){$code -eq 0}else{$code -ne 0 -and $text.Contains($expectedError)}
    $passed=$passed -and $noCompileOutput
    $results.Add([ordered]@{name=$name;passed=$passed;exitCode=$code;expectedError=$expectedError;noCompileOutput=$noCompileOutput;log=$log})
    Write-Output ($name+': '+$passed)
}
foreach($c in $cases){Check ('valid-'+$c[1]) $c (Join-Path $apiRoot $c[2]) @()}
$v20=$cases[6]; $api20=Join-Path $apiRoot $v20[2]
Check 'wrong-api-version' $v20 (Join-Path $apiRoot $cases[5][2]) @() 'Wrong adapter API identity'
# TiaPublicApi.props rejects a missing core module before ValidateAdapterInputs runs.
Check 'missing-api-directory' $v20 (Join-Path $out 'absent-sdk') @() 'PublicAPI 20 requires Siemens.Engineering.dll'
Check 'missing-api-module' $cases[7] $api20 @() 'PublicAPI 21 requires Siemens.Engineering.Base.dll'
Check 'wrong-release' $v20 $api20 @('-p:TiaReleaseKey=21') 'Adapter release mismatch'
Check 'override-project-identity' $v20 $api20 @('-p:AdapterReleaseKey=19','-p:TiaReleaseKey=19') 'Adapter release mismatch'
Check 'excluded-v14' $cases[0] (Join-Path $apiRoot $cases[0][2]) @('-p:TiaReleaseKey=14') 'Adapter release mismatch'
Check 'excluded-v15' $cases[1] (Join-Path $apiRoot $cases[1][2]) @('-p:TiaReleaseKey=15') 'Adapter release mismatch'
Check 'wrong-framework' $v20 $api20 @('-p:TargetFramework=net461') 'Adapter framework mismatch'
Check 'wrong-platform' $v20 $api20 @('-p:PlatformTarget=x86') 'x64 library'
Check 'executable-output' $v20 $api20 @('-p:OutputType=Exe') 'x64 library'
Check 'wildcard-sources' $v20 $api20 @('-p:EnableDefaultCompileItems=true') 'explicit allowlist'
Check 'missing-source' $v20 $api20 @("-p:AdapterSourceRoot=$out/absent-source") 'Missing allowlisted adapter source'
[IO.File]::WriteAllText((Join-Path $out 'input-results.json'),($results|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
if(@($results|Where-Object {!$_.passed}).Count){throw 'Adapter input checks failed; inspect input-results.json.'}
