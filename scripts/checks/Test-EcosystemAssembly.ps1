param([Parameter(Mandatory=$true)][string]$Exe,[Parameter(Mandatory=$true)][string]$PublicApiDirectory)
$ErrorActionPreference='Stop'
$exePath=(Resolve-Path -LiteralPath $Exe).Path
$api=(Resolve-Path -LiteralPath $PublicApiDirectory).Path
$runtime=Split-Path $exePath
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$env:TIA_MCP_BUNDLE_ROOT=$repo
$scratch=Join-Path $repo ('TiaMcp_Output/ecosystem-assembly-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$env:TIA_MCP_DIAGNOSTICS_DIRECTORY=Join-Path $scratch 'diagnostics'
$resolve=[ResolveEventHandler]{param($sender,$eventArgs)
    $name=([Reflection.AssemblyName]::new($eventArgs.Name).Name)+'.dll'
    foreach($directory in @($runtime,$api)) { $candidate=Join-Path $directory $name; if(Test-Path -LiteralPath $candidate){return [Reflection.Assembly]::LoadFrom($candidate)} }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
$passed=0
# Mirror the engine's startup (Program.cs): .NET Framework writes child stdin with Console.InputEncoding,
# which adds a UTF-8 BOM under console code page 65001 and breaks the companion JSON bridges.
$previousInputEncoding=[Console]::InputEncoding
try { [Console]::InputEncoding=[Text.UTF8Encoding]::new($false) } catch {}
try {
    $assembly=[Reflection.Assembly]::LoadFrom($exePath)
    if(![IDisposable].IsAssignableFrom($assembly.GetType('TiaMcpServer.Siemens.Portal',$true))){throw 'Portal cleanup is not registered with IDisposable'}
    $type=$assembly.GetType('TiaMcpServer.ModelContextProtocol.McpServer',$true)
    $bridge=$type.GetMethod('CallTool',[type[]]@([string],[string]))
    function Call([string]$Name,$Arguments) {
        $json=ConvertTo-Json -InputObject $Arguments -Depth 30 -Compress
        $result=$bridge.Invoke($null,[object[]]@([string]$Name,[string]$json))
        if(!(($result.Meta.ToJsonString()|ConvertFrom-Json).bridgeSuccess)){throw "Bridge failed for ${Name}: $($result.Message)"}
        return $result.Message|ConvertFrom-Json
    }
    function Check([bool]$Value,[string]$Message) { if(!$Value){throw $Message}; $script:passed++ }
    $guidance=Call 'ReadOpennessGuidance' @{}
    Check ($guidance.Meta.success -and $guidance.Meta.total -ge 32) 'Guidance lookup failed'
    $batch=Call 'ReadToolBatch' @{operationsJson='[{"name":"ReadOpennessGuidance","arguments":{"query":"threading"}}]'}
    Check ($batch.Meta.success -and $batch.Meta.results.Count -eq 1) 'Read batch lost its inner result'
    $bad=Call 'ReadToolBatch' @{operationsJson='[{"name":"SaveProject","arguments":{}}]'}
    Check (!$bad.Meta.success) 'Read batch allowed a write'
    $native=Call 'ReadToolBatch' @{operationsJson='[{"name":"GetCrossReferences","arguments":{}}]'}
    Check (!$native.Meta.success) 'Native cross references entered batch'
    $apply=Call 'ApplyToolBatch' @{token='not-a-real-token'}
    Check (!$apply.Meta.success) 'Unknown write token accepted'
    $official=Call 'GetAuthoringGuide' @{topic='openness-workflow'}
    Check ($official.Meta.success -and $official.Meta.usage.totalMatches -gt 0 -and $official.Meta.usage.referenceOnly) 'Official references are not reachable through the unified library'
    foreach($tool in @('SaveProject','CompileSoftware','ApplyToolBatch','RunPlcCompanionTool','ManagePlcGitRepository')) {
        $transactionCalls=ConvertTo-Json -InputObject @(@{name=$tool;arguments=@{}}) -Compress -Depth 8
        $refused=Call 'RunToolsInTransaction' @{callsJson=$transactionCalls;text='Offline refusal';dryRun=$false;confirmChange=$true}
        Check (!$refused.Meta.success -and $refused.Message.Contains('not supported inside')) "Transaction admitted $tool before acquiring native state"
    }
    $validTransaction=Call 'RunToolsInTransaction' @{callsJson='[{"name":"CreatePlcTypeGroup","arguments":{"softwarePath":"PLC_Offline","groupPath":"Test"}}]';text='Preview only'}
    Check ($validTransaction.Meta.success -and !$validTransaction.Meta.mayHaveChanged) 'Supported transaction preview failed or wrote'
    $badTransaction=Call 'RunToolsInTransaction' @{callsJson='[{"name":"CreatePlcTypeGroup","arguments":{"softwarePath":"PLC_Offline"}}]';text='Missing argument'}
    Check (!$badTransaction.Meta.success -and $badTransaction.Message.Contains('Preflight failed')) 'Transaction did not preflight every call'
    $row='[{"source":["Input"],"destination":["DB","Output"]}]'
    $lad=Call 'ComposePlcAliasAlarmLad' @{blockName='FC_Offline';blockNumber=701;rowsJson=$row}
    Check ($lad.Meta.success -and $lad.Meta.xml.Contains('Contact')) 'Boolean LAD composer failed'
    $left=Join-Path $scratch 'before.xml';$right=Join-Path $scratch 'after.xml'
    [IO.File]::WriteAllText($left,$lad.Meta.xml,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($right,$lad.Meta.xml.Replace('Name="Input"','Name="OtherInput"'),[Text.UTF8Encoding]::new($false))
    $report=Join-Path $scratch 'diff.html'
    $diff=Call 'RenderPlcVisualDiff' @{leftFilePath=$left;rightFilePath=$right;outputPath=$report}
    Check ($diff.Meta.success -and (Test-Path $report) -and $diff.Meta.state -eq 'Changed') 'Visual diff did not detect changed operand'
    $overwrite=Call 'RenderPlcVisualDiff' @{leftFilePath=$left;rightFilePath=$right;outputPath=$report}
    Check (!$overwrite.Meta.success) 'Visual diff overwrote a report'
    $quality=Call 'AuditEngineeringExports' @{directoryPath=$scratch;blockNamePattern='^Expected_'}
    Check ($quality.Meta.success -and !$quality.Meta.qualityPassed) 'Quality verdict confused with invocation success'
    $pdfPath=Join-Path $scratch 'audit.pdf'
    $pdf=Call 'AuditEngineeringExports' @{directoryPath=$scratch;reportPath=$pdfPath}
    Check ($pdf.Meta.success -and ([IO.File]::ReadAllBytes($pdfPath).Length -gt 1000)) 'Quality PDF report failed'
    $template=Join-Path $scratch 'template.xml';[IO.File]::WriteAllText($template,'<Document><Name>{{Name}}</Name></Document>')
    $rows='[{"fileName":"generated.xml","values":{"Name":"A & B"}}]'
    $preview=Call 'InstantiatePlcXmlTemplates' @{templatePath=$template;rowsJson=$rows;outputDirectory=$scratch}
    Check ($preview.Meta.success -and !(Test-Path (Join-Path $scratch 'generated.xml'))) 'Template preview wrote a file'
    $expand=Call 'InstantiatePlcXmlTemplates' @{templatePath=$template;rowsJson=$rows;outputDirectory=$scratch;dryRun=$false}
    Check ($expand.Meta.success -and $expand.Meta.files[0].written) 'Template execution failed'
    $gitRoot=Join-Path $scratch 'git';New-Item -ItemType Directory $gitRoot|Out-Null
    & git init --quiet $gitRoot
    if($LASTEXITCODE){throw 'Fixture git init failed'}
    & git -C $gitRoot config user.name 'Offline Test'
    & git -C $gitRoot config user.email 'offline-test@example.invalid'
    [IO.File]::WriteAllText((Join-Path $gitRoot 'a.scl'),'FUNCTION A : VOID END_FUNCTION')
    $gitStatus=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot}
    Check ($gitStatus.Meta.success -and $gitStatus.Meta.stdout.Contains('a.scl')) 'Git status failed'
    $gitPreview=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot;action='commit';filesJson='["a.scl"]';message='Offline fixture'}
    Check ($gitPreview.Meta.success -and !$gitPreview.Meta.executed) 'Git commit preview executed'
    $gitCommit=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot;action='commit';filesJson='["a.scl"]';message='Offline fixture';dryRun=$false}
    Check ($gitCommit.Meta.success) ('Git commit fixture failed: '+$gitCommit.Meta.stderr)
    $history=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot;action='history'}
    Check ($history.Meta.success -and $history.Meta.stdout.Contains('Offline fixture')) 'Git history lost commit'
    $catalog=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch}
    Check ($catalog.Meta.success) ('Companion catalogue failed: '+$catalog.Meta.stderr)
    $commands=$catalog.Meta.stdout|ConvertFrom-Json
    Check ($commands.complete -and $commands.commands.Count -ge 49) 'Companion commands incomplete'
    $help=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='help';argumentsJson='["code","lint"]'}
    Check ($help.Meta.success -and $help.Meta.stdout.Contains('Usage:')) 'Companion help failed'
    $plan=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='run';argumentsJson='["trace","start"]'}
    Check ($plan.Meta.success -and !$plan.Meta.executed) 'Online companion ran during dry run'
    $badCommand=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='run';argumentsJson='["code","does-not-exist"]';dryRun=$false}
    Check (!$badCommand.Meta.success -and $badCommand.Meta.exitCode -ne 0) 'Companion hid nonzero exit'
    $journal=Get-ChildItem -LiteralPath $env:TIA_MCP_DIAGNOSTICS_DIRECTORY -Filter '*.jsonl'
    $entries=@(Get-Content -LiteralPath $journal[0].FullName | ForEach-Object {$_|ConvertFrom-Json})
    Check (@($entries|Where-Object {$_.phase -eq 'BEFORE'}).Count -gt 0 -and @($entries|Where-Object {$_.phase -eq 'RETURNED'}).Count -gt 0) 'Invocation breadcrumbs missing'
    Check (!(Get-Content -LiteralPath $journal[0].FullName -Raw).Contains('offline-test@example.invalid')) 'Journal unexpectedly contains argument data'
    Write-Output "COMPLETE: $passed ecosystem assembly checks passed. Artifacts: $scratch"
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve)
    try { [Console]::InputEncoding=$previousInputEncoding } catch {}
}
