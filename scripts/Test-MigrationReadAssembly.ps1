param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$PublicApiDirectory
)
$ErrorActionPreference='Stop'
$exePath=(Resolve-Path -LiteralPath $Exe).Path
$runtimeDirectory=Split-Path $exePath -Parent
$apiDirectory=(Resolve-Path -LiteralPath $PublicApiDirectory).Path
$resolver=[ResolveEventHandler]{param($sender,$eventArgs)
    $file=([Reflection.AssemblyName]::new($eventArgs.Name).Name)+'.dll'
    foreach($directory in @($runtimeDirectory,$apiDirectory)){
        $candidate=Join-Path $directory $file
        if(Test-Path -LiteralPath $candidate){return [Reflection.Assembly]::LoadFrom($candidate)}
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $assembly=[Reflection.Assembly]::LoadFrom($exePath)
    $server=$assembly.GetType('TiaMcpServer.ModelContextProtocol.McpServer',$true)
    $names=@('ListUnifiedGlobalScripts','ReadUnifiedGlobalScript','ReadUnifiedTagDefinitions','ReadUnifiedScreenBranch','ReadUnifiedLibraryType','ReadUnifiedFaceplateInstance','ListUnifiedLibraryFolder','ReleaseUnifiedReadCursor')
    foreach($name in $names){
        $method=$server.GetMethod($name,[Reflection.BindingFlags]'Public,Static')
        if(!$method){throw "Missing tool: $name"}
        $attributes=[Reflection.CustomAttributeData]::GetCustomAttributes($method)
        if(!($attributes | Where-Object {$_.AttributeType.Name -eq 'McpServerToolAttribute'})){throw "Unregistered tool: $name"}
    }
    $reader=$assembly.GetType('TiaMcpServer.Siemens.JavaScriptEvidence',$true)
    $parse=$reader.GetMethod('Analyze',[Reflection.BindingFlags]'NonPublic,Static')
    $source="import * as C from 'Colors'; export function f(x) { return Tags(x+'.v').Read(); }"
    $result=$parse.Invoke($null,@($source,'fixture.js'))
    $data=$result.ToJsonString($null) | ConvertFrom-Json
    if(!$data.parseComplete -or !$data.bodyReadSuccess -or $data.rawText -cne $source -or $data.functions.Count -ne 1){throw 'Compiled EXE cannot preserve and parse script body'}
    if($data.references[0].resolution -ne 'runtimeExpressionUnresolved'){throw 'Compiled EXE incorrectly resolves dynamic name'}
    Write-Output "PASS actual EXE: 8 read-only migration tools registered; original JS and dynamic reference verified under .NET Framework. FileVersion=$((Get-Item -LiteralPath $exePath).VersionInfo.FileVersion)"
}
finally {[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
