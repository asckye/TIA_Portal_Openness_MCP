#Requires -Version 5.1
<#
.SYNOPSIS
    从 manifest/tools-list.json 生成 docs/reference/tool-matrix.md，按大类 → 域分组。
.DESCRIPTION
    tools-list.json 由 Generate-ToolsListFromAssembly.ps1 对已编译 EXE 反射生成，其中 category / domain / operation
    来自引擎内的 ToolTaxonomy（唯一事实来源）。本脚本不再解析源码，因此矩阵与交付清单不会各自漂移；
    Build-Release.ps1 在生成清单后调用本脚本。
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\generate\Generate-ToolCapabilityMatrix.ps1
#>
param(
    [string]$ToolsList = "",
    [string]$OutFile = ""
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
if (-not $ToolsList) { $ToolsList = Join-Path $root "manifest\tools-list.json" }
if (-not $OutFile)   { $OutFile   = Join-Path $root "docs\reference\tool-matrix.md" }

$data = Get-Content -LiteralPath $ToolsList -Raw -Encoding UTF8 | ConvertFrom-Json
$tools = @($data.tools)
if ($tools.Count -ne [int]$data.toolCount) { throw "tools-list.json toolCount ($($data.toolCount)) differs from the tools array ($($tools.Count))" }
if (-not $data.categories) { throw "tools-list.json has no categories block; regenerate it with the current Generate-ToolsListFromAssembly.ps1" }

$operationMeaning = [ordered]@{
    'SESSION'      = '会话与发现，不改工程'
    'READ'         = '读取已打开工程，不改动'
    'WRITE'        = '修改离线工程数据，默认预览，不自动保存/编译/下载'
    'FILE'         = '导出/导入文件或生成离线产物'
    'OFFLINE'      = '纯离线计算，不需要 TIA 会话'
    'ONLINE'       = '联系 PLC/设备/运行时，只读'
    'ONLINE-WRITE' = '改变真实设备或运行时'
    'EXECUTE'      = '执行编译/测试/自检'
}
function Esc([string]$s) { return ($s -replace '\|', '\|' -replace '\r?\n', ' ').Trim() }
function StripPrefix([string]$desc) { return ($desc -replace '^\s*\[L\d\]\[(?:Category:)?[^\]]+\](?:\[[A-Za-z-]+\])?\s*', '') }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# MCP 工具能力矩阵")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [官方 API 覆盖清单](openness-coverage.md)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("本文件由 ``scripts/generate/Generate-ToolCapabilityMatrix.ps1`` 从 ``manifest/tools-list.json``（已编译 EXE 的反射清单）生成，分类来自引擎内的 ``ToolTaxonomy``；运行时以 ``tools/list`` 为准。在会话中调用 ``ListToolCategories`` 可得到同一分类的实时计数，``FindTools(category=…)`` / ``FindTools(domain=…)`` 可按分类检索。")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("- 生成时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
[void]$sb.AppendLine("- 引擎文件版本：$($data.fileVersion)")
[void]$sb.AppendLine("- 工具数量：$($tools.Count)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## 读法")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("每个工具的描述以 ``[层][域][操作]`` 开头。层：L0 会话与引导、L1 常用工程动作、L2 专用深度工具（默认 lite 配置不全部列出，经 ``FindTools`` + ``CallTool`` 调用）。操作类型：")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| 操作 | 含义 | 工具数 |")
[void]$sb.AppendLine("|---|---|---:|")
foreach ($op in $operationMeaning.Keys) {
    $n = @($tools | Where-Object { $_.operation -eq $op }).Count
    [void]$sb.AppendLine("| ``$op`` | $($operationMeaning[$op]) | $n |")
}
$inferred = @($tools | Where-Object { $_.operationInferred }).Count
[void]$sb.AppendLine("")
[void]$sb.AppendLine("描述里未显式标注操作类型的工具（$inferred 个）由 ``ToolTaxonomy.OperationOf`` 按工具名推断，表中以 ``*`` 标记；显式标注优先。")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## 分类总览")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| 大类 | 名称 | 工具数 | 域 |")
[void]$sb.AppendLine("|---|---|---:|---|")
foreach ($c in $data.categories) {
    $n = @($tools | Where-Object { $_.category -eq $c.key }).Count
    $domainCells = @($c.domains | ForEach-Object { $d = $_; "``$d`` ($(@($tools | Where-Object { $_.domain -eq $d }).Count))" }) -join '、'
    [void]$sb.AppendLine("| ``$($c.key)`` | $($c.nameZh) / $($c.nameEn) | $n | $domainCells |")
}
foreach ($c in $data.categories) {
    $inCategory = @($tools | Where-Object { $_.category -eq $c.key })
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## $($c.key) — $($c.nameZh) / $($c.nameEn)（$($inCategory.Count)）")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine((Esc $c.description))
    foreach ($d in $c.domains) {
        $inDomain = @($inCategory | Where-Object { $_.domain -eq $d } | Sort-Object layer, name)
        if ($inDomain.Count -eq 0) { continue }
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("### [$d]（$($inDomain.Count)）")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| 工具 | 层 | 操作 | 说明 |")
        [void]$sb.AppendLine("|---|---|---|---|")
        foreach ($t in $inDomain) {
            $op = if ($t.operationInferred) { "$($t.operation)*" } else { $t.operation }
            [void]$sb.AppendLine("| ``$($t.name)`` | $($t.layer) | $op | $(Esc (StripPrefix $t.description)) |")
        }
    }
}
$unc = @($tools | Where-Object { $_.category -eq 'uncategorized' })
if ($unc.Count) { throw "Uncategorized tools present: $(($unc | ForEach-Object name) -join ', ')" }
[IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "tool-matrix.md: $($tools.Count) tools in $(@($data.categories).Count) categories -> $OutFile"
