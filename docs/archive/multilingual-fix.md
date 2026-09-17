# WinCC Unified 多语言文本修复与验收报告

> 历史技术记录，仅说明当时的排查与验证结果；当前能力与边界见 [能力说明](../reference/capabilities.md)。


日期：2026-09-13。公开版已移除本机路径和现场项目标识；下方路径为占位示例。

**状态：源码已修复，离线回归及 V20/V21 构建通过；虚拟机 MCP 服务尚未替换，实际 TIA 修复版验收未执行。不能据此称当前 MCP 已修复。**

## 1. 基线、交接来源与范围

- 当前分支：master；基线提交：`2b4f70c`。本报告随修复源码及发布标签交付；以 Git 历史记录实际提交。
- 已阅读原始多语言缺陷修复交接报告；原报告保存位置不随公开版披露。
- 源码根目录：本仓库根目录。
- 原交接源码快照为 `65445e01...`；本次重新检查当前分支，四项问题仍存在。
- 修复公共文本路径与返回契约，不修改现场画面、Logo、脚本、PLC 变量绑定；本轮现场操作仅 Bootstrap、FindTools、GetObjectProperty 只读请求。

## 2. 根因和修复位置

| 问题 | 基线证据 | 修复位置及行为 |
|---|---|---|
| 指定语言写错 | `Portal.Software.cs` 原 `TrySetMultilingualText` 读取 `it.Culture`，随后 `cultureMatch ?? first` | 新 `Siemens/UnifiedMultilingualText.cs`，使用 `Language.Culture.Name`，大小写无关但不做前缀/父文化匹配；缺失、重复、无效结构直接失败；不增添语言 |
| 空串跳过 | `ApplyUnifiedHmiScreenDesignJson` 使用 `!string.IsNullOrEmpty(text)` | `ReadRequest` 区分属性是否存在；空字符串写入真正空串；null/非字符串报错 |
| 回读循环 | `Portal.Helpers.cs/GetObjectProperty` 将原生 MultilingualText 放入 Value，CallTool 序列化 Culture.Parent | MultilingualText 转换为 `[{culture,text}]`；新增 `ReadUnifiedHmiTexts`，只返回字符串，无原生 Language/Culture 对象 |
| 桥接成功掩盖业务失败 | `McpServer.ToolBridge.cs/CallTool` 无条件 `BridgeMeta(true)` | `ToolBridgeStatus.cs` 分离 bridgeSuccess、operationSuccess、success 和 operationStatus；保留 Message 中原工具 JSON |

相关注册入口：`ModelContextProtocol/McpServer.PlcSoftware.cs`。原 setter 已删除，其唯一调用点已替换为共用、可离线测试的实现。

V21 随附文档已核对：`V21-References/PublicAPI/V21/net48/Siemens.Engineering.Base.xml` 第 6396 行 Language.Culture、第 6979 行 MultilingualText.Items、第 7122 行 MultilingualTextItem.Language、第 7128 行 MultilingualTextItem.Text。

## 3. 现场证据与结论边界

原报告环境为 V21 / WinCC Unified 测试工程副本。报告记载一个文本项的三种语言内容不同，旧三语言成功回执缺少逐语言回读。该历史观察引用原报告，未重新截图核实；工程和对象标识在公开版中省略。

本轮新采集证据：

- Bootstrap 返回 V21、connected=true、同名工程、serverVersion=2.7.2.0。此值是 AssemblyVersion，不能单独识别修复版。
- FindTools 搜索 `GetObjectProperty ReadUnifiedHmiTexts`，当前服务仅找到 GetObjectProperty，没有新增回读工具。
- 2026-09-14T02:43:01 +08:00，当前 MCP 的 GetObjectProperty 只读请求再次失败：`$.Value.Items.Language.Culture.Parent.Parent...`。
- 原始现场回执不再公开分发；本文保留已脱敏的诊断结论。

请求参数：

```json
{"objectKind":"HmiScreenItem","objectPath":"<已核对HMI路径>:<测试画面>:<测试文本项>","propertyPath":"Text"}
```

因此本轮已经验证**旧服务缺陷仍可复现**，没有验证修复版在实际 TIA 上生效。

## 4. 输入、回读和失败契约

| 输入 | 行为 |
|---|---|
| 省略 text | 不修改文本 |
| `text: ""` | 目标语言 Text 写入空字符串，回读须为空 |
| `text: null` / 数字 / 对象 | 明确失败；该项创建/更新前拒绝文本参数 |
| 纯空白 | 保留空白，按普通文本包装 HTML，不 trim 内容 |
| 普通文字 | HTML 转义，再包装 `<body><p>...</p></body>` |
| 以 body 开始的 HTML | 保留原始字符串；空 HTML 也保留 |
| 未提供 culture | 兼容原入口，默认 zh-CN |
| 缺失/无效 culture、重复语言 | 不选择第一种语言；报错，包含目标及可用语言（能正常读取集合时） |

Text、AlternateText、ToolTipText 共用实现，前提是该控件实际存在对应的 MultilingualText 属性。普通字符串 Text 不会被 GetObjectProperty 错当成多语言对象。

写前读取完整集合并预检目标，现有控件先验证目标再应用该项布局属性；写后重新读取集合，逐语言核对目标预期值与非目标完整原文。验证失败不会返回成功。所有成功文本项在 `Meta.textReadback` 返回 name、property、verified=true、languages。

当前采用保守的原始文本精确比较。若 TIA 自动正规化 HTML 导致原始字符串不同，则返回回读不一致，不能静默放行；需保留实际读回文本，在实际验收中确认正规化规则后再调整比较逻辑。此限制可能产生假失败，但不会据此把未经验证的写入当作成功。

批量操作不是事务：失败前的其他写入可能已生效，新建对象也可能保留。strict=true/false 均以 Meta.success=false 报告任何失败；strict=false 允许读取部分结果摘要。`changed` 表示完成该项处理，不是持久化或文本正确性的独立证明；应同时检查 failed、success 和 textReadback。返回 persistence 明确本工具不保存工程。

专用回读示例（可直接调用，或通过 CallTool）：

```json
{"name":"ReadUnifiedHmiTexts","argumentsJson":"{\"hmiSoftwarePath\":\"<已核对的测试HMI路径>\",\"screenName\":\"LanguageProbe\",\"itemName\":\"btnStart\",\"textProperty\":\"Text\"}"}
```

返回业务响应中 `Meta.languages` 为 `[{"culture":"zh-CN","text":"..."}, ...]`。旧 GetObjectProperty 的 `propertyPath="Text"` 也返回相同形式的 Value 数组；`Text.Items` 的旧通用集合路径仍是名称列表，逐语言验收请读 Text 或使用专用工具。

CallTool 契约（MCP 协议 isError 与下表不是同一层）：

| 状态 | bridgeSuccess | operationSuccess | success | operationStatus |
|---|---|---|---|---|
| 内部 Meta.success=true | true | true | true | succeeded |
| 内部 Meta.success=false | true | false | false | failed |
| 内部没有明确状态 | true | null | null | unknown |
| 参数/反射/序列化失败 | false | null | false | notCompleted |

兼容性变化：以前仅凭 CallTool 外层 success=true 判定结果的调用方应迁移到此契约。unknown 必须阅读原始结果，不应推断为成功；协议正常返回也不代表业务成功。直接工具保留其原有业务 Meta.success。

## 5. 离线测试与构建

环境：项目内 .NET SDK 8.0.425；net8.0 控制台回归套件，使用 `dotnet run`，不使用会空跑的 `dotnet test`。

最终离线结果：**328 passed, 0 failed, 0 skipped**。其中新增多语言/桥接检查 173 项，原有检查 155 项。

覆盖 Text/AlternateText/ToolTipText × en/de/zh、zh/en/de、de/zh/en 三排列；分别写每种语言及重复写；非目标不变；缺失/无效/重复语言；空集合/错误结构；省略/空串/null/非字符串/空白；中文、特殊字符、HTML/空HTML；回读序列化；setter 忽略写入；非目标被副作用改动；直接/CallTool 结果；参数错误、反射异常、序列化循环。

离线直接运行生产的 UnifiedMultilingualText、ToolBridgeStatus 与 CallTool 源码。CallTool 测试仅用最小 SDK attribute/response 边界替身，不代表真实 MCP 传输端到端验收，也不代表 Siemens 运行时验收。

| 验证 | V20 | V21 |
|---|---|---|
| Release/net48 构建 | 通过，0 error | 通过，0 error |
| Siemens 引用 | V20-References/PublicAPI/V20 | V21-References/PublicAPI/V21/net48 |
| 共享离线回归 | 328/0/0（同一套源码） | 328/0/0（同一套源码） |
| 警告 | 既有 OpcUaLiveReader.cs:195 CS8602 | 同左 |
| 修复版实际 TIA 读写 | 未执行 | 未执行 |
| 虚拟机服务替换 | 未执行 | 未执行 |

默认构建最初因本机未安装 TIA 缺少引用而失败，后续使用原交接目录的正式 PublicAPI 引用重新构建成功。V20 通过绝对 BaseIntermediateOutputPath/MSBuildProjectExtensionsPath 隔离还原缓存，避免 V20/V21 资产混用。

当时使用的专用构建脚本已由统一流程替代。当前构建与交付步骤见 [发布流程](../development/release-workflow.md)；以下版本和结果是历史快照。

## 6. 构建标识、交付与部署步骤

- FileVersion：`2.7.2.5`。
- InformationalVersion：`2.7.2-asckye.5-multilingual.1`。
- AssemblyVersion 保持 `2.7.2.0`，因此不能仅看 Bootstrap.serverVersion 判断部署。
- 交付：`bin-build/multilingual-fix-2.7.2-asckye.5-multilingual.1.zip`，内有 v20/v21 完整构建输出、manifest.json、测试/构建日志、本报告。
- 本次发布同步更新仓库 runtime/v20、runtime/v21 为修复版；已有虚拟机部署仍须独立更新并核对运行路径及哈希。

虚拟机部署：

1. 确认目标 VM/TIA 主版本、MCP 真实启动配置与 EXE 路径、现有进程 PID、FileVersion/ProductVersion 和 SHA-256，保存基线。不要猜测交付目录。
2. 将 ZIP 复制到 VM 新的版本目录，核对 manifest 中对应版本的每个文件 SHA-256。V20 和 V21 不混用；本包不分发 Siemens PublicAPI DLL，运行环境须已有对应 TIA/Openness。
3. 完成工程备份，正常停止旧 MCP 宿主/连接；不要强杀 TIA，不要依靠重启抹掉未保存状态。
4. 将 MCP 启动配置指向新版本目录对应 EXE，保持原有参数/连接配置；保留旧目录以便回退。使用 GUI 时请更新对应 runtime 目录的整套输出，不能只改源码目录。
5. 重新启动 MCP、重新建立客户端连接；在 VM 检查真实运行进程路径，并对该路径的 EXE 再做版本与 SHA-256 校验。
6. 用 FindTools 找到 ReadUnifiedHmiTexts；执行下面验收。在全部证据完成前将部署状态记为“已启动，待验收”，不能记为修复完成。
7. 如失败，记录回执和工程状态，停止新 MCP，恢复旧启动配置/目录。二进制回退不撤销工程写入，工程恢复须基于备份单独处理。

VM 核对示例：

```powershell
Get-CimInstance Win32_Process -Filter "Name='TiaMcpServer.exe'" |
  Select-Object ProcessId, ExecutablePath, CommandLine
# 将下一行换成上面确认的真实运行路径
$runningExe = 'C:\<已确认版本目录>\v21\TiaMcpServer.exe'
(Get-Item -LiteralPath $runningExe).VersionInfo | Select-Object FileVersion, ProductVersion
Get-FileHash -LiteralPath $runningExe -Algorithm SHA256
```

## 7. 实际 TIA 验收矩阵（未执行）

在工程副本新增专用 LanguageProbe 测试画面，使用已启用 en-US/de-DE/zh-CN 的按钮 btnStart；先用 TIA 文本表初始化三语言不同内容并保存 before。不要以真实生产按钮作为覆盖测试对象。

最小写入参数：

```json
{
  "hmiSoftwarePath":"<测试HMI实际路径>",
  "screenName":"LanguageProbe",
  "designJson":"{\"items\":[{\"name\":\"btnStart\",\"type\":\"Button\",\"text\":\"启动\",\"culture\":\"zh-CN\",\"textProperty\":\"Text\"}]}",
  "strict":true
}
```

逐项要求：

1. ReadUnifiedHmiTexts 三属性回读成功，language code 与原始 Text 齐全，空文本不丢失；GetObjectProperty(Text) 也可序列化。
2. 分别写 zh-CN/en-US/de-DE，各次只改变目标语言；改变项目语言枚举顺序后重复验证，第二次相同写入仍正确。
3. text="" 清空指定语言；省略 text 不变；null 明确失败；空白保留。HTML 若正规化导致严格回读失败，保存实际值，不能将失败标为通过。
4. 缺失语言在写前失败，不能写首语言；重复语言主要由离线构造测试覆盖，因为实际 API 可能不允许创建重复项，现场不得破坏工程制造重复。
5. Text/AlternateText/ToolTipText 都验证，控件没有某属性时应明确报告不支持；不得把该项省略算通过。
6. 直接调用和 CallTool 对业务失败一致；strict=false 有失败项时仍 success=false，bridgeSuccess=true 不掩盖失败。
7. 正常编译 HMI，保存工程；重新打开或重新连接，再次逐语言回读确认持久化。记录已有 PLC/HMI 编译错误，与基线分开，不能将文本修复等同整机可运行。
8. 前后比较位置/尺寸、字体/颜色、Logo、动态属性、脚本、PLC 绑定及其他画面；必须保持不变。测试画面之外只做只读比较。
9. 保存 actual-exe-path、file/product version、SHA-256、before/after、全部工具回执、重连后回读、文本表截图、编译结果及差异检查。
10. V20/V21 分别签署通过/失败/未执行。只有该版本实际服务和工程验收完成后，才能声称该版本当前 MCP 已修复。
