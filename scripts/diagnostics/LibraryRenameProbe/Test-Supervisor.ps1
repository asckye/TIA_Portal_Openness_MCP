[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$testRoot=Join-Path $repository ('bin-build\rename-supervisor-tests-' + [guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $testRoot
$source=@'
using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class FakeProbe {
 static int Main(string[] args) {
  if(args.Length==1) return 0;
  var a=new Dictionary<string,string>(); for(int i=0;i<args.Length;i+=2) a.Add(args[i],args[i+1]);
  string stage=a["--stage"], name=a["--case"], output=a["--output"], mode=Environment.GetEnvironmentVariable("RENAME_SUPERVISOR_TEST_MODE");
  Directory.CreateDirectory(output);
  var json=new JavaScriptSerializer();
  int pid=Process.GetCurrentProcess().Id;
  if(mode=="alive") pid=int.Parse(Environment.GetEnvironmentVariable("RENAME_SUPERVISOR_TEST_PID"));
  string start=Process.GetProcessById(pid).StartTime.ToUniversalTime().ToString("o");
  File.WriteAllText(Path.Combine(output,stage+"-owned-process.json"),json.Serialize(new{pid=pid,startUtc=start,owned=true}));
  if(mode=="missing") { Console.Error.WriteLine("SIMULATED_BOOTSTRAP_EXCEPTION"); return 1; }
  bool fail=(mode=="matrix"&&stage=="run"&&name=="property")||mode=="prepare-failure";
  string status=fail?"FAILED":stage=="prepare"?"PREPARED":name=="control"?"CONTROL_PASSED":"SIMULATED_SUCCESS";
  File.WriteAllText(Path.Combine(output,stage+"-result.json"),json.Serialize(new{status=status,nativeExecuted=false,pid=pid,startUtc=start,ownedPortalAlive=false,simulation=true,detail="path limit: \u8def\u5f84",error=fail?"SIMULATED_NATIVE_EXCEPTION":null}));
  return fail?1:0;
 }
}
'@
$fixture=Join-Path $testRoot 'FakeProbe.cs'
Set-Content -LiteralPath $fixture -Value $source -Encoding UTF8
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fake=Join-Path $testRoot 'LibraryRenameProbe.exe'
& $compiler /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll "/out:$fake" $fixture
if($LASTEXITCODE -ne 0){throw 'Supervisor fixture compilation failed'}
$sourceLibrary=Join-Path $testRoot 'MCP_GeneralScripts_Rename_fake.al21'
Set-Content -LiteralPath $sourceLibrary -Value 'Offline supervisor fixture; never passed to Siemens.'
$outcomes=[Collections.Generic.List[object]]::new()
try {
 foreach($mode in @('matrix','prepare-failure','missing','alive')) {
  $folder=New-Item -ItemType Directory -Path (Join-Path $testRoot $mode)
  Copy-Item -LiteralPath $fake -Destination $folder.FullName
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-Tests.ps1') -Destination $folder.FullName
  $env:RENAME_SUPERVISOR_TEST_MODE=$mode
  $env:RENAME_SUPERVISOR_TEST_PID=[string]$PID
  $runner=Join-Path $folder.FullName 'Run-Tests.ps1'
  $process=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$runner+'"'),'-SourceLibrary',('"'+$sourceLibrary+'"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $folder.FullName 'stdout.txt') -RedirectStandardError (Join-Path $folder.FullName 'stderr.txt')
  $summaryFile=@(Get-ChildItem -LiteralPath (Join-Path $folder.FullName 'results') -Filter summary.json -Recurse)
  if($summaryFile.Count -ne 1){throw "Missing summary for $mode"}
  $summary=Get-Content -LiteralPath $summaryFile[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
  $caseResults=@(Get-Content -LiteralPath (Join-Path $summaryFile[0].Directory.FullName 'cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
  $expectedUnicode='path limit: ' + [char]0x8def + [char]0x5f84
  if($mode -ne 'missing' -and @($caseResults | Where-Object { $_.result.detail -ne $expectedUnicode }).Count){throw 'BOM-less UTF-8 result did not survive PowerShell 5.1 aggregation'}
  $expected=if($mode -eq 'matrix'){18}else{1}
  $exitCode=if($mode -eq 'matrix'){1}else{2}
  if($summary.completedStages -ne $expected -or $process.ExitCode -ne $exitCode){throw "Unexpected campaign behavior for $mode; stages=$($summary.completedStages), exit=$($process.ExitCode)"}
  if($mode -eq 'matrix' -and $summary.stopReason){throw 'Recoverable isolated failure did not continue'}
  if($mode -ne 'matrix' -and !$summary.stopReason){throw 'Unsafe campaign state did not stop'}
  if($mode -eq 'alive' -and $summary.stopReason -notmatch 'still alive'){throw 'Owned process guard did not identify the live fixture'}
  if($mode -eq 'matrix' -and @($caseResults | Where-Object { !$_.ownedProcessExitedVerified -or !$_.existingPortalsPreserved }).Count){throw 'Post-exit identity verification was not persisted'}
  if($mode -ne 'matrix' -and $summary.executionStagesAttempted -ne 0){throw 'Failed preparation was counted as rename execution'}
  $console=Get-Content -LiteralPath (Join-Path $folder.FullName 'stdout.txt') -Raw
  if(($mode -eq 'matrix' -or $mode -eq 'prepare-failure') -and $console -notmatch 'SIMULATED_NATIVE_EXCEPTION'){throw 'Native failure details were hidden from the console'}
  if($mode -eq 'missing' -and $console -notmatch 'SIMULATED_BOOTSTRAP_EXCEPTION'){throw 'Bootstrap stderr was hidden from the console'}
  $outcomes.Add([pscustomobject]@{scenario=$mode;passed=$true;utf8Preserved=($mode -ne 'missing');completedStages=$summary.completedStages;exitCode=$process.ExitCode;stopReason=$summary.stopReason})
 }
} finally {
 Remove-Item Env:RENAME_SUPERVISOR_TEST_MODE -ErrorAction SilentlyContinue
 Remove-Item Env:RENAME_SUPERVISOR_TEST_PID -ErrorAction SilentlyContinue
}
[pscustomobject]@{nativeExecuted=$false;fixture='Independent .NET Framework child simulator; no Siemens API';scenarios=$outcomes} |
 ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'supervisor-validation.json') -Encoding UTF8
Write-Output "Four offline supervisor scenarios passed: $testRoot"
