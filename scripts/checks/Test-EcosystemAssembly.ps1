param([Parameter(Mandatory=$true)][string]$Exe,[Parameter(Mandatory=$true)][string]$PublicApiDirectory,[switch]$SkipPdf,[switch]$SkipCompanion)
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
    $logic=[Reflection.Assembly]::LoadFrom((Join-Path $runtime 'TiaMcp.Logic.dll'))
    $bridge=@($type.GetMethods() | Where-Object Name -eq 'CallTool')[0]
    $parse=$logic.GetType('TiaMcp.Logic.V4.V4Json',$true).GetMethod('ParseInput',[Reflection.BindingFlags]'NonPublic,Static')
    $argumentsType=$bridge.GetParameters()[1].ParameterType.GetGenericArguments()[0]
    . (Join-Path $PSScriptRoot 'McpResults.ps1')
    function Call([string]$Name,$Arguments) {
        $json=ConvertTo-Json -InputObject $Arguments -Depth 30 -Compress
        $value=$parse.Invoke($null,[object[]]@([string]$json))
        $typed=[Activator]::CreateInstance($argumentsType,[object[]]@($value.PSObject.BaseObject))
        $decoded=ConvertFrom-McpResult ($bridge.Invoke($null,[object[]]@([string]$Name,$typed.PSObject.BaseObject)))
        [IO.File]::WriteAllText((Join-Path $scratch ("response-$script:passed-$Name.json")),(ConvertTo-Json $decoded -Depth 50),[Text.UTF8Encoding]::new($false))
        return $decoded
    }
    function Check([bool]$Value,[string]$Message) { if(!$Value){throw $Message}; $script:passed++ }
    $guidance=Call 'GetOpennessGuidance' @{}
    Check ($guidance.ok -and $guidance.data.total -ge 32) 'Guidance lookup failed'
    $batch=Call 'RunReadOnlyToolBatch' @{operations=@(@{name='GetOpennessGuidance';arguments=@{query='threading'}})}
    Check ($batch.ok -and $batch.data.items.Count -eq 1) 'Read batch lost its inner result'
    $bad=Call 'RunReadOnlyToolBatch' @{operations=@(@{name='SaveProject';arguments=@{}})}
    Check (!$bad.ok) 'Read batch allowed a write'
    $native=Call 'RunReadOnlyToolBatch' @{operations=@(@{name='GetPlcCrossReferences';arguments=@{}})}
    Check (!$native.ok) 'Native cross references entered batch'
    $apply=Call 'ApplyToolBatch' @{token='not-a-real-token'}
    Check (!$apply.ok) 'Unknown write token accepted'
    $official=Call 'GetToolUsage' @{query='';limit=1}
    Check ($official.ok -and $official.data.totalMatches -gt 0 -and $official.data.referenceOnly) 'Official references are not reachable through the unified library'
    foreach($tool in @('SaveProject','CompilePlcSoftware','ApplyToolBatch','RunPlcCompanionTool','ManagePlcGitRepository')) {
        $transactionCalls=@(@{name=$tool;arguments=@{}})
        $refused=Call 'RunToolTransaction' @{calls=$transactionCalls;text='Offline refusal';dryRun=$false;confirmChange=$true}
        Check (!$refused.ok -and $refused.meta.execution -eq 'not-started') "Transaction admitted $tool before acquiring native state"
    }
    $validTransaction=Call 'RunToolTransaction' @{calls=@(@{name='CreatePlcTypeGroup';arguments=@{softwarePath='PLC_Offline';groupPath='Test'}});text='Preview only'}
    Check ($validTransaction.ok -and !$validTransaction.data.mayHaveChanged) 'Supported transaction preview failed or wrote'
    $badTransaction=Call 'RunToolTransaction' @{calls=@(@{name='CreatePlcTypeGroup';arguments=@{softwarePath='PLC_Offline'}});text='Missing argument'}
    Check (!$badTransaction.ok -and $badTransaction.error.code -eq 'INVALID_ARGUMENT') 'Transaction did not preflight every call'
    $row=@(@{source=@('Input');destination=@('DB','Output')})
    $lad=Call 'BuildPlcAliasAlarmLad' @{blockName='FC_Offline';blockNumber=701;rows=$row}
    Check ($lad.ok -and $lad.data.xml.Contains('Contact')) 'Boolean LAD composer failed'
    $left=Join-Path $scratch 'before.xml';$right=Join-Path $scratch 'after.xml'
    [IO.File]::WriteAllText($left,$lad.data.xml,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($right,$lad.data.xml.Replace('Name="Input"','Name="OtherInput"'),[Text.UTF8Encoding]::new($false))
    $report=Join-Path $scratch 'diff.html'
    $diff=Call 'RenderPlcVisualDiff' @{leftFilePath=$left;rightFilePath=$right;outputPath=$report}
    Check ($diff.ok -and (Test-Path $report) -and $diff.data.state -eq 'Changed') 'Visual diff did not detect changed operand'
    $overwrite=Call 'RenderPlcVisualDiff' @{leftFilePath=$left;rightFilePath=$right;outputPath=$report}
    Check (!$overwrite.ok) 'Visual diff overwrote a report'
    $quality=Call 'AuditEngineeringExports' @{directoryPath=$scratch;blockNamePattern='^Expected_'}
    Check ($quality.ok -and !$quality.data.qualityPassed) 'Quality verdict confused with invocation success'
    if($SkipPdf) { Write-Output 'SKIP PDF renderer: explicit offline run without the companion ReportLab environment' }
    else {
        $pdfPath=Join-Path $scratch 'audit.pdf'
        $pdf=Call 'AuditEngineeringExports' @{directoryPath=$scratch;reportPath=$pdfPath}
        Check ($pdf.ok -and ([IO.File]::ReadAllBytes($pdfPath).Length -gt 1000)) 'Quality PDF report failed'
    }
    $template=Join-Path $scratch 'template.xml';[IO.File]::WriteAllText($template,'<Document><Name>{{Name}}</Name></Document>')
    $rows=@(@{fileName='generated.xml';values=@{Name='A & B'}})
    $preview=Call 'InstantiatePlcTemplates' @{templatePath=$template;rows=$rows;outputDirectory=$scratch}
    Check ($preview.ok -and !(Test-Path (Join-Path $scratch 'generated.xml'))) 'Template preview wrote a file'
    $expand=Call 'InstantiatePlcTemplates' @{templatePath=$template;rows=$rows;outputDirectory=$scratch;dryRun=$false}
    Check ($expand.ok -and $expand.data.files[0].written) 'Template execution failed'
    $gitRoot=Join-Path $scratch 'git';New-Item -ItemType Directory $gitRoot|Out-Null
    & git init --quiet $gitRoot
    if($LASTEXITCODE){throw 'Fixture git init failed'}
    & git -C $gitRoot config user.name 'Offline Test'
    & git -C $gitRoot config user.email 'offline-test@example.invalid'
    [IO.File]::WriteAllText((Join-Path $gitRoot 'a.scl'),'FUNCTION A : VOID END_FUNCTION')
    $gitStatus=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot}
    Check ($gitStatus.ok -and $gitStatus.data.stdout.Contains('a.scl')) 'Git status failed'
    $gitPreview=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot;action='commit';files=@('a.scl');message='Offline fixture'}
    Check ($gitPreview.ok -and !$gitPreview.data.executed) 'Git commit preview executed'
    Check (!(Test-Path (Join-Path $gitRoot '.git/refs/heads/master')) -and !(Test-Path (Join-Path $gitRoot '.git/refs/heads/main'))) 'Git preview created a commit'
    $history=Call 'ManagePlcGitRepository' @{repositoryPath=$gitRoot;action='history'}
    Check (!$history.ok -and $history.data.exitCode -ne 0) 'Empty fixture history concealed missing commits'
    if($SkipCompanion) { Write-Output 'SKIP companion fixtures: explicit run without installed Python dependencies' }
    else {
        $catalog=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch}
        Check ($catalog.ok) ('Companion catalogue failed: '+$catalog.data.stderr)
        $commands=$catalog.data.stdout|ConvertFrom-Json
        Check ($commands.complete -and $commands.commands.Count -ge 49) 'Companion commands incomplete'
        $help=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='help';arguments=@('code','lint')}
        Check ($help.ok -and $help.data.stdout.Contains('Usage:')) 'Companion help failed'
        $plan=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='run';arguments=@('trace','start')}
        Check ($plan.ok -and !$plan.data.executed) 'Online companion ran during dry run'
        $badCommand=Call 'RunPlcCompanionTool' @{workingDirectory=$scratch;mode='run';arguments=@('code','does-not-exist');dryRun=$false}
        Check (!$badCommand.ok -and $badCommand.data.exitCode -ne 0) 'Companion hid nonzero exit'
    }
    $journal=Get-ChildItem -LiteralPath $env:TIA_MCP_DIAGNOSTICS_DIRECTORY -Filter '*.jsonl'
    $entries=@(Get-Content -LiteralPath $journal[0].FullName | ForEach-Object {$_|ConvertFrom-Json})
    Check (@($entries|Where-Object {$_.phase -eq 'BEFORE'}).Count -gt 0 -and @($entries|Where-Object {$_.phase -eq 'RETURNED'}).Count -gt 0) 'Invocation breadcrumbs missing'
    Check (!(Get-Content -LiteralPath $journal[0].FullName -Raw).Contains('offline-test@example.invalid')) 'Journal unexpectedly contains argument data'
    Write-Output "COMPLETE: $passed ecosystem assembly checks passed. Artifacts: $scratch"
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve)
    try { [Console]::InputEncoding=$previousInputEncoding } catch {}
}
