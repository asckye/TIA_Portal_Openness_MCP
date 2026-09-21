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
    # 分类的唯一事实来源在引擎里（ToolTaxonomy）；这里经反射取大类与操作类型，避免脚本与二进制各存一份分类表。
    $taxonomy=$assembly.GetType('TiaMcpServer.ModelContextProtocol.ToolTaxonomy',$true)
    $categoryOf=$taxonomy.GetMethod('CategoryOf'); $parseTag=$taxonomy.GetMethod('Parse'); $operationOf=$taxonomy.GetMethod('OperationOf')
    $categories=@(foreach($c in $taxonomy.GetField('Categories').GetValue($null)){[ordered]@{key=$c.Key;nameZh=$c.NameZh;nameEn=$c.NameEn;description=$c.Description;domains=@($c.Domains)}})
    # 2.7.57: the worked examples (ToolExamples) are appended to listed tool descriptions and printed by FindTools / PreflightToolCall;
    # an example that no longer fits its tool (renamed parameter, dropped tool) fails the build here rather than misleading callers.
    $exampleProblems=@($type.GetMethod('ValidateToolExamples').Invoke($null,@()))
    if($exampleProblems.Count){throw "Tool examples do not fit their tools (fix ToolExamples.cs): $($exampleProblems -join '; ')"}
    $findExample=$assembly.GetType('TiaMcpServer.ModelContextProtocol.ToolExamples',$true).GetMethod('Find')
    $rows=@(foreach($method in $type.GetMethods([Reflection.BindingFlags]'Public,Static')){
        $attributes=[Reflection.CustomAttributeData]::GetCustomAttributes($method)
        $tool=$attributes | Where-Object { $_.AttributeType.Name -eq 'McpServerToolAttribute' } | Select-Object -First 1
        if(!$tool){continue}
        $name=$method.Name
        foreach($arg in $tool.NamedArguments){if($arg.MemberName -eq 'Name'){$name=[string]$arg.TypedValue.Value}}
        $desc=$attributes | Where-Object { $_.AttributeType.FullName -eq 'System.ComponentModel.DescriptionAttribute' } | Select-Object -First 1
        $description=if($desc){[string]$desc.ConstructorArguments[0].Value}else{''}
        $tag=$parseTag.Invoke($null,@($description))
        $layer=[string]$tag.Item1; $domain=[string]$tag.Item2
        $op=$operationOf.Invoke($null,@($name,$description)); $operation=[string]$op.Item1; $operationInferred=[bool]$op.Item2
        $category=[string]$categoryOf.Invoke($null,@($domain))
        $example=$findExample.Invoke($null,@($name))
        [ordered]@{name=$name;layer=$layer;category=$category;domain=$domain;operation=$operation;operationInferred=$operationInferred;method=$method.Name;returnType=$method.ReturnType.Name;
            parameters=@($method.GetParameters() | ForEach-Object Name);description=$description;example=$(if($example){$example.ArgumentsJson}else{$null})}
    })
    $uncategorized=@($rows | Where-Object { $_.category -eq 'uncategorized' } | ForEach-Object { $_.name })
    if($uncategorized.Count){throw "Tools with an unregistered domain tag (register the domain in ToolTaxonomy or fix the description prefix): $($uncategorized -join ', ')"}
    $data=[ordered]@{
        package=$PackageName;generatedAt=[DateTimeOffset]::UtcNow.ToString('o');
        source='Reflection metadata from the compiled EXE; no live TIA or host tools/list claimed';
        fileVersion=(Get-Item -LiteralPath $exePath).VersionInfo.FileVersion;
        exeSha256=(Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant();
        toolCount=$rows.Count;note='Full attributed tool roster. Default lite profile uses FindTools + CallTool for the remaining tools. Runtime tools/list is authoritative.';
        categories=$categories;
        tools=@($rows | Sort-Object name)
    }
    [IO.File]::WriteAllText($OutputPath,($data|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output "Compiled EXE tool metadata: $($rows.Count) tools; $(@($rows | Where-Object { $_.example }).Count) with a validated example"
}
finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
