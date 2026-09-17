param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$PublicApiDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$PackageName='TIA_MCP_Delivery_v2.7.2-asckye.6-defects.1'
)
$ErrorActionPreference='Stop'
$exePath=(Resolve-Path -LiteralPath $Exe).Path
$apiPath=(Resolve-Path -LiteralPath $PublicApiDirectory).Path
$runtimePath=Split-Path $exePath -Parent
$resolver=[ResolveEventHandler]{param($sender,$eventArgs)
    $file=([Reflection.AssemblyName]::new($eventArgs.Name).Name)+'.dll'
    foreach($directory in @($runtimePath,$apiPath)){
        $candidate=Join-Path $directory $file
        if(Test-Path -LiteralPath $candidate){return [Reflection.Assembly]::LoadFrom($candidate)}
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $assembly=[Reflection.Assembly]::LoadFrom($exePath)
    $type=$assembly.GetType('TiaMcpServer.ModelContextProtocol.McpServer',$true)
    $rows=@(foreach($method in $type.GetMethods([Reflection.BindingFlags]'Public,Static')){
        $attributes=[Reflection.CustomAttributeData]::GetCustomAttributes($method)
        $tool=$attributes | Where-Object { $_.AttributeType.Name -eq 'McpServerToolAttribute' } | Select-Object -First 1
        if(!$tool){continue}
        $name=$method.Name
        foreach($arg in $tool.NamedArguments){if($arg.MemberName -eq 'Name'){$name=[string]$arg.TypedValue.Value}}
        $desc=$attributes | Where-Object { $_.AttributeType.FullName -eq 'System.ComponentModel.DescriptionAttribute' } | Select-Object -First 1
        $description=if($desc){[string]$desc.ConstructorArguments[0].Value}else{''}
        $layer=if($description -match '\[(L\d)\]'){$Matches[1]}else{'L2'}
        $domain=if($description -match '\[L\d\]\[(?:Category:)?([^\]]+)\]'){$Matches[1]}else{''}
        [ordered]@{name=$name;layer=$layer;domain=$domain;method=$method.Name;returnType=$method.ReturnType.Name;
            parameters=@($method.GetParameters() | ForEach-Object Name);description=$description}
    })
    $data=[ordered]@{
        package=$PackageName;generatedAt=[DateTimeOffset]::UtcNow.ToString('o');
        source='Reflection metadata from the compiled EXE; no live TIA or host tools/list claimed';
        fileVersion=(Get-Item -LiteralPath $exePath).VersionInfo.FileVersion;
        exeSha256=(Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant();
        toolCount=$rows.Count;note='Full attributed tool roster. Default lite profile uses FindTools + CallTool for the remaining tools. Runtime tools/list is authoritative.';
        tools=@($rows | Sort-Object name)
    }
    [IO.File]::WriteAllText($OutputPath,($data|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output "Compiled EXE tool metadata: $($rows.Count) tools"
}
finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
