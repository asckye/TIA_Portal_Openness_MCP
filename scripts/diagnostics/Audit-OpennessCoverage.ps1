<#
.SYNOPSIS
  逐成员对照官方 Openness PublicAPI XML 与引擎源码，找出官方有而源码从未引用的类型/方法。

.DESCRIPTION
  只读取本机 PublicAPI 的 XML 文档（每个公开成员都有 T:/M:/P: 标识），不加载任何 Siemens DLL，
  不启动 TIA。判定规则（词法，不是可达性证明）：
    - 方法 REFERENCED：所属类型简名出现在源码 且 ".方法名(" 出现在源码
    - 方法 OWNER_ONLY：只有所属类型简名出现在源码
    - 方法 UNREFERENCED：类型简名都没出现
    - 类型 UNTOUCHED：类型简名不在源码，且其全部非样板方法/属性均 UNREFERENCED
  排除：样板成员（Equals/GetAttribute/GetService 等）、显式接口实现（#IEngineering…）、
  构造函数、枚举字段、事件、AddIn.* 程序集（TIA 内置插件基础设施，不属于外部 MCP 范围）。

  注意：通用反射工具（DescribeObject/InvokeObject/InvokeService）可以动态到达未被词法引用的成员，
  因此 UNREFERENCED 表示"没有专用封装"，不表示"完全不可用"。

.PARAMETER PublicApiDirectory
  含 Siemens.Engineering*.xml 的目录，例如 TIA_V21_PublicAPI\V21\net48。
.PARAMETER Version
  标签，如 V21。
.PARAMETER OutputDirectory
  输出目录（默认 bin-build\audits\openness-coverage-<日期>，被 .gitignore 忽略）。
.PARAMETER SummaryMarkdown
  可选：把汇总写成 Markdown（例如 docs\reference\openness-coverage.md）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicApiDirectory,
    [string]$Version = 'V21',
    [string]$OutputDirectory = '',
    [string]$SummaryMarkdown = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$src  = Join-Path $repo 'tools\tiaportal-mcp\src\TiaMcpServer'
if (-not (Test-Path $PublicApiDirectory)) { throw "PublicAPI directory not found: $PublicApiDirectory" }
if ($OutputDirectory -eq '') { $OutputDirectory = Join-Path $repo ("bin-build\audits\openness-coverage-" + (Get-Date -Format 'yyyyMMdd')) }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$boiler = @('Equals','GetHashCode','ToString','GetEnumerator','Any','Contains','IndexOf','CopyTo',
            'GetAttribute','GetAttributes','GetAttributeInfos','SetAttribute','SetAttributes',
            'GetComposition','GetCompositionInfos','GetInvocationInfos','GetCreationInfos',
            'GetService','GetServiceInfos','Invoke','Find','Count','Parent','Dispose','Clone',
            'CompareTo','GetType','MemberwiseClone','Finalize','Item')
$boilerSet = New-Object 'System.Collections.Generic.HashSet[string]'
$boiler | ForEach-Object { [void]$boilerSet.Add($_) }

# ---- 1. 源码词法集合 ------------------------------------------------------------
Write-Host "Scanning engine source under $src ..."
$tokens  = New-Object 'System.Collections.Generic.HashSet[string]'   # 所有标识符
$calls   = New-Object 'System.Collections.Generic.HashSet[string]'   # .Name( 形式
$access  = New-Object 'System.Collections.Generic.HashSet[string]'   # .Name 形式（属性/字段访问）
$csFiles = Get-ChildItem -Path $src -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin|obj-v20|bin-v20)\\' }
$rxIdent = [regex]'[A-Za-z_][A-Za-z_0-9]*'
$rxCall  = [regex]'\.([A-Za-z_][A-Za-z_0-9]*)\s*(?:<[^>()]*>)?\s*\('
$rxAcc   = [regex]'\.([A-Za-z_][A-Za-z_0-9]*)\b'
foreach ($f in $csFiles) {
    $text = [IO.File]::ReadAllText($f.FullName)
    foreach ($m in $rxIdent.Matches($text)) { [void]$tokens.Add($m.Value) }
    foreach ($m in $rxCall.Matches($text))  { [void]$calls.Add($m.Groups[1].Value) }
    foreach ($m in $rxAcc.Matches($text))   { [void]$access.Add($m.Groups[1].Value) }
}
Write-Host ("  {0} files, {1} identifiers, {2} call names, {3} member accesses" -f $csFiles.Count, $tokens.Count, $calls.Count, $access.Count)

# ---- 2. 解析 XML 成员 -------------------------------------------------------------
$rows = New-Object 'System.Collections.Generic.List[object]'
$xmlFiles = Get-ChildItem -Path $PublicApiDirectory -Filter 'Siemens.Engineering*.xml' | Sort-Object Name
foreach ($xf in $xmlFiles) {
    $assembly = [IO.Path]::GetFileNameWithoutExtension($xf.Name)
    if ($assembly -like 'Siemens.Engineering.AddIn*') { continue }
    [xml]$doc = [IO.File]::ReadAllText($xf.FullName)
    foreach ($m in $doc.doc.members.member) {
        $sig = [string]$m.name
        if ($sig.Length -lt 3) { continue }
        $kind = $sig.Substring(0,1)
        if ($kind -notin @('T','M','P')) { continue }
        $full = $sig.Substring(2)
        $stem = ($full -split '\(',2)[0]
        if ($kind -eq 'T') { $owner = $stem; $member = '' }
        else {
            $idx = $stem.LastIndexOf('.')
            $owner = $stem.Substring(0,$idx); $member = $stem.Substring($idx+1)
        }
        if ($member -match '#') {
            if ($member -match '#ctor|#cctor' -or $member -match '#IEngineering|#I[A-Z]') { continue }
            $member = ($member -split '#')[-1]
        }
        $ownerSimple = ($owner -split '\.')[-1] -replace '`\d+$',''
        $ns = if ($owner.Contains('.')) { $owner.Substring(0, $owner.LastIndexOf('.')) } else { $owner }
        # 泛型嵌套类型 Outer`1.Inner 的命名空间修正
        $ns = $ns -replace '`\d+',''
        $isBoiler = ($kind -eq 'M' -and $boilerSet.Contains($member)) -or ($kind -eq 'P' -and $boilerSet.Contains($member))
        $rows.Add([pscustomobject]@{
            version=$Version; assembly=$assembly; namespace=$ns; kind=$kind
            owner=$owner; ownerSimple=$ownerSimple; member=$member; signature=$sig; boilerplate=$isBoiler
        })
    }
}
Write-Host ("  {0} XML files, {1} T/M/P members (AddIn.* excluded)" -f $xmlFiles.Count, $rows.Count)

# ---- 3. 逐成员判定 ----------------------------------------------------------------
foreach ($r in $rows) {
    $ownerHit = $tokens.Contains($r.ownerSimple)
    if ($r.kind -eq 'T') {
        $r | Add-Member -NotePropertyName verdict -NotePropertyValue ($(if ($ownerHit) {'TYPE_NAMED'} else {'TYPE_UNNAMED'}))
        continue
    }
    if ($r.boilerplate) { $r | Add-Member -NotePropertyName verdict -NotePropertyValue 'BOILERPLATE'; continue }
    $memberHit = if ($r.kind -eq 'M') { $calls.Contains($r.member) } else { $access.Contains($r.member) }
    $v = if ($ownerHit -and $memberHit) { 'REFERENCED' } elseif ($ownerHit) { 'OWNER_ONLY' } else { 'UNREFERENCED' }
    $r | Add-Member -NotePropertyName verdict -NotePropertyValue $v
}

# ---- 4. 类型级与命名空间级汇总 ------------------------------------------------------
$byOwner = $rows | Where-Object { $_.kind -ne 'T' -and -not $_.boilerplate } | Group-Object owner
$typeSummary = foreach ($g in $byOwner) {
    $ref = @($g.Group | Where-Object verdict -eq 'REFERENCED').Count
    $own = @($g.Group | Where-Object verdict -eq 'OWNER_ONLY').Count
    $unr = @($g.Group | Where-Object verdict -eq 'UNREFERENCED').Count
    $first = $g.Group[0]
    $status = if ($ref -gt 0) { 'PARTIAL_OR_COVERED' } elseif ($own -gt 0) { 'TYPE_NAMED_NO_MEMBER' } else { 'UNTOUCHED' }
    [pscustomobject]@{ assembly=$first.assembly; namespace=$first.namespace; type=$g.Name; typeSimple=$first.ownerSimple
        members=$g.Count; referenced=$ref; ownerOnly=$own; unreferenced=$unr; status=$status
        unreferencedMembers = (($g.Group | Where-Object { $_.verdict -ne 'REFERENCED' } | Select-Object -ExpandProperty member | Sort-Object -Unique) -join ' ') }
}
$nsSummary = $typeSummary | Group-Object namespace | ForEach-Object {
    $t = $_.Group
    [pscustomobject]@{ namespace=$_.Name; assembly=($t[0].assembly)
        types=$t.Count; typesCovered=@($t | Where-Object status -eq 'PARTIAL_OR_COVERED').Count
        typesNamedOnly=@($t | Where-Object status -eq 'TYPE_NAMED_NO_MEMBER').Count
        typesUntouched=@($t | Where-Object status -eq 'UNTOUCHED').Count
        members=($t | Measure-Object members -Sum).Sum
        membersReferenced=($t | Measure-Object referenced -Sum).Sum }
} | Sort-Object assembly, namespace

# ---- 5. 输出 --------------------------------------------------------------------------
$rows        | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputDirectory "$Version-members.csv")
$typeSummary | Sort-Object assembly, namespace, type | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputDirectory "$Version-types.csv")
$nsSummary   | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputDirectory "$Version-namespaces.csv")

$total = @($rows | Where-Object { $_.kind -ne 'T' -and -not $_.boilerplate })
$stats = [ordered]@{
    version=$Version; publicApiDirectory=$PublicApiDirectory; generatedAt=(Get-Date).ToString('s')
    xmlFiles=$xmlFiles.Count; sourceFiles=$csFiles.Count
    domainMembers=$total.Count
    referenced=@($total | Where-Object verdict -eq 'REFERENCED').Count
    ownerOnly=@($total | Where-Object verdict -eq 'OWNER_ONLY').Count
    unreferenced=@($total | Where-Object verdict -eq 'UNREFERENCED').Count
    types=$typeSummary.Count
    typesCovered=@($typeSummary | Where-Object status -eq 'PARTIAL_OR_COVERED').Count
    typesNamedOnly=@($typeSummary | Where-Object status -eq 'TYPE_NAMED_NO_MEMBER').Count
    typesUntouched=@($typeSummary | Where-Object status -eq 'UNTOUCHED').Count
    caveat='Lexical only. REFERENCED = owner type name AND .member( both appear in engine source. Generic reflection tools can reach unreferenced members dynamically. AddIn.* assemblies excluded.'
}
($stats | ConvertTo-Json -Depth 3) | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory "$Version-summary.json")
Write-Host ""
Write-Host ("Domain members {0}: referenced {1} / owner-only {2} / unreferenced {3}" -f $stats.domainMembers,$stats.referenced,$stats.ownerOnly,$stats.unreferenced)
Write-Host ("Types {0}: covered {1} / named-only {2} / untouched {3}" -f $stats.types,$stats.typesCovered,$stats.typesNamedOnly,$stats.typesUntouched)
Write-Host "Output: $OutputDirectory"

if ($SummaryMarkdown -ne '') {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("<!-- 由 scripts/diagnostics/Audit-OpennessCoverage.ps1 生成，勿手工编辑数字 -->")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| 程序集 | 命名空间 | 类型数 | 有专用引用 | 仅类型名 | 完全未触及 | 成员数 | 已引用成员 |")
    [void]$sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|")
    foreach ($n in $nsSummary) {
        [void]$sb.AppendLine(("| {0} | ``{1}`` | {2} | {3} | {4} | {5} | {6} | {7} |" -f ($n.assembly -replace '^Siemens\.Engineering\.?',''), $n.namespace, $n.types, $n.typesCovered, $n.typesNamedOnly, $n.typesUntouched, $n.members, $n.membersReferenced))
    }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 完全未触及的类型（按命名空间）")
    [void]$sb.AppendLine("")
    foreach ($grp in ($typeSummary | Where-Object status -eq 'UNTOUCHED' | Group-Object namespace | Sort-Object Name)) {
        [void]$sb.AppendLine(("### ``{0}``" -f $grp.Name))
        [void]$sb.AppendLine("")
        foreach ($t in ($grp.Group | Sort-Object type)) {
            $mem = if ($t.unreferencedMembers.Length -gt 220) { $t.unreferencedMembers.Substring(0,220) + ' …' } else { $t.unreferencedMembers }
            [void]$sb.AppendLine(("- **{0}**（{1} 个成员）：{2}" -f $t.typeSimple, $t.members, $mem))
        }
        [void]$sb.AppendLine("")
    }
    [IO.File]::WriteAllText($SummaryMarkdown, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Summary markdown: $SummaryMarkdown"
}
