# 已接入的生态工具与外部参考

本页区分实际集成和候选资源。工具按版本开放；八版共用工具、部分版本工具和专属工具以[版本矩阵](version-tools.md)为准。实际参数和语言示例统一从 `GetToolUsage` 取得，不在这里另存一套调用模板。

## 实际复用

| 来源 | 本项目当前用途 | 来源与许可记录 |
|---|---|---|
| Siemens `tia-portal-ai-extensions` | 官方 Openness 指南供检索和统一示例引用；资料不会自行执行。 | [固定来源](../../reference/siemens-openness/UPSTREAM.json) |
| Siemens Openness Code Snippets | C# 官方示例作为参考语料纳入统一示例库，区分官方源代码与本项目 MCP 参数。 | [固定来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/reference/siemens-code-snippets/UPSTREAM.json) |
| `asckye/tia-openness-studio` | 已合入桌面工程；八个直接 Openness 适配器、共用工作流、中英界面，不再使用 MCP 通道。 | [Studio 说明](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/src/Studio/README.md)、[来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/tia-openness-studio/upstream.json) |
| EidoTiaWorkbench | `PlanArtifactImportOrder` 复用依赖排序算法，八版共用；移除原 V21 域启发式假设。 | [改动与 MIT 许可](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/eido-import-planner/README.md) |
| `Czarnak/tia-git-addin` | MIT Core 的 SimaticML 解析、结构差异和 LAD 布局；结合本项目 HTML/Git 适配器。未复制桌面 UI。 | [固定来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/TiaGitAddIn.Core/UPSTREAM.json) |
| `core-engineering/siemens-plc-tools` | 固定版本的 Python 包和 CLI，通过独立伴随进程调用；安装环境另外准备。 | [固定来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/siemens-plc-tools/UPSTREAM.json) |
| Siemens OPC UA Modelled Interface Add-In | 复用接口 XML 生成阶段，导入使用现有 MCP 工具；不是安装上游 Add-In。 | [改动记录](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/SiemensOpcUaModelled/UPSTREAM.json) |
| `Czarnak/simaticml-decoder` | 独立 Python 进程只读分析 V21 FC/FB 导出；输出不可直接回编译或导入。 | [固定来源](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/simaticml-decoder/UPSTREAM.json) |

随包许可、依赖与版权见[第三方清单](../licenses/THIRD-PARTY-NOTICES.md)。Siemens PublicAPI 和第三方测试工程不随交付包分发。

MCP 的 `WRITE` / `ONLINE-WRITE` 调用默认先由 Workbench 审批再派发；拒绝、超时或工作台不可用会在操作前返回 `CONFIRMATION_REQUIRED`。审计记录保存在数据根的 `data/logs/audit`，可通过 Workbench 审计页或 `tia audit verify` 检查。哈希链只能验证留存记录间的连续性，不能证明完整日志未被删除。引擎日志和诊断调用包位于 `data/logs` 与 `data/diagnostics`；只读安装时使用运行时布局文档列出的用户目录回退。

伴随 Python 环境由 `scripts/ecosystem/Install-PlcTools.ps1` 准备，默认位于
`%LOCALAPPDATA%\TiaMcp\ecosystem-python`；需要 Python 3.12+。引擎优先使用显式
`TIA_MCP_PLC_TOOLS_PYTHON`，否则读取该环境的 `Scripts\python.exe`。LocalAppData 缺失、
环境不可写或解释器不存在时返回 `IO_FAILED` 与路径，不从旧私人环境自动复制或执行。
开发者可用 `-EnvironmentPath <absolute-path>` 保留自选位置；维护者现有仓库 `TiaMcp_Output`
环境也必须通过该参数或解释器变量显式选择，不作为默认值。

## 现有工具用途

| 工具 | 使用范围与结果判断 |
|---|---|
| `ReadOpennessGuidance` | 检索随包固定版本官方指南；对调用示例优先用 `GetToolUsage`。 |
| `PlanArtifactImportOrder` | 根据输入依赖生成导入顺序，不执行导入。 |
| `RenderPlcVisualDiff` | 单块 SimaticML 结构比较及 LAD 并排 SVG/HTML；不宣称支持全部语言图形差异。 |
| `RenderPlcBlock` / `RenderPlcProgramAtlas` | 从精确版本导出证据生成静态单块梯形图或程序图册；不在线编辑或下载，且不覆盖已存在的输出文件。图册中的检查结论是证据说明，不代表原生验收。 |
| `ManagePlcGitRepository` | 导出/VCI 目录的状态、历史、差异、暂存和提交；只提交指定文件。 |
| `RunPlcCompanionTool` | 调用已登记的 PLC Tools CLI；具体命令由工具读取当前清单。命令可能读写文件或访问配置中的设备，需先明确目标。 |
| `GenerateOpcUaModelledInterface` | 从选定 PLC/软件单元生成接口 XML；需另行导入并编译。 |
| `AuditEngineeringExports` | 输入导出的命名、元数据、结构及自定义规则审计。`qualityPassed` 才是规则结果，`success` 只代表检查运行完成。 |
| `InstantiatePlcXmlTemplates` / `ComposePlcAliasAlarmLad` | 模板展开和限定的 V21 布尔 LAD 网络生成；不自动导入、保存或下载。 |
| `ValidatePlcXmlSchemas` | 用本地匹配版本 XSD 检查识别出的 XML 片段。`fragmentSchemasPassed` 不等于整文档可导入。 |
| `DecodePlcSimaticMl` | V21 FC/FB 的可读逻辑与元数据分析；V20、S7DCL 和未知语义有明确限制。 |
| `ManageUnifiedCwcPackage` | 检查 CWC 文件夹并打包新 ZIP；不执行 JavaScript、不部署到 TIA、不认证运行行为。CWC 与面板库类型不同。 |
| `ReadV21EcosystemCatalog` | V20/V21 完整引擎查询嵌入的 V21 生态快照，包含版本证据、许可元数据及集成状态；不是在线搜索。 |

完整引擎的 `ReadToolBatch`、`PreviewToolBatch` / `ApplyToolBatch` 可组合支持的工具。批处理失败可能保留先前修改，不是原子事务；原生 `RunToolsInTransaction` 只支持自身声明的同步编辑白名单。不能把编译、保存、在线、文件或嵌套编排塞入事务。

## PLC 编辑与离线引用

`ReadPlcBlockEditCapabilities` 检查实际文档的语言、文本、标量初始值和库绑定。`PatchPlcBlockDocument` 只修改存在且匹配的目标并写入新文件。`ImportPlcBlockVerified` 按精确工程、对象和预览令牌导入后读回；库连接块、实例 DB、Safety 等有明确排除范围。

`AnalyzePlcReferences` 仅分析输入 SimaticML 的调用和全局符号，不能替代完整原生交叉引用。遗漏文件、未知调用和歧义需随结果一起报告。编辑工具不会自动保存工程，编译选项必须按实际 schema 指定；导入成功、内容核对成功、编译成功分别判断。完整示例统一见[调用示例库](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)。

## PLC Tools 环境

在运行 MCP 的电脑上，按 [伴随工具说明](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/third_party/siemens-plc-docs/development/repository-layout.md)准备 Python 环境，并通过 `TIA_MCP_PLC_TOOLS_PYTHON` 指向其解释器。Python 环境不是发布 ZIP 的内置运行时。

V20/V21 完整引擎通过安装布局解析器定位包根，再读取 `reference/siemens-openness` 中的指南以及
`scripts/ecosystem` 中的 Python 桥接；完整交付包可在仓库外运行。包根可用 `--bundle-root` 或
`TIA_MCP_BUNDLE_ROOT` 显式指定，具体校验和默认 Python 路径见[运行时布局](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md)。
此覆盖不影响嵌入的生态目录，也不扩展 Foundation 的工具范围。

`code` 提供代码分析、测试、文档与差异；`iol` 提供 I/O 与标签交换；`net`、`sim`、`sup`、`trace` 按各自配置处理网络、仿真、监督及跟踪。部分命令执行项目 Python、访问设备或抓包，不应把所有伴随命令当作离线只读检查。上游 PDF 文档命令所需的 pandoc/TeX 等环境与本项目 ReportLab 审计报告是不同路线。

## 外部参考与尚未集成项

[生态目录快照](../../reference/v21-ecosystem.json)保存当次检索证据。上游 README 声称支持 V21，不等于本项目验收；`not_confirmed`、`adaptation_required`、`unsupported_v21` 不能视为可用。目录数量与 GitHub 星数均不作为成熟度或覆盖率结论。

这份 JSON 是目录的唯一可编辑源，构建时直接嵌入 V20/V21 引擎；随包副本缺失不影响
`ReadV21EcosystemCatalog`，修改磁盘副本也不会覆盖编译进引擎的数据。目录更新需要重新构建。

- 官方 Openness Explorer 可辅助检查对象模型；Project Check、Modular Application Creator 和 SiOME 仍需独立安装及各自环境，没有自动安装或整包嵌入。
- TiaImportExport.VSExt 的已审阅公开树不足以复用完整扩展核心；TIAOpennessManager / AnyAutomation Studio 也未建立可直接并入的许可核心证据。
- TiaCommander 的部分需求由本项目现有代码和官方 API 实现，没有复制其源代码或二进制。
- tia-linter、TiaUtilities 的相关功能采用独立实现；没有复制其 GPL 源码。候选 README 和外部原生测试不能替代本项目测试。
- AutoPLC、Agents4PLC 等数据集可供后续案例评估；目前不作为随包已验证程序库。

新候选应补足精确来源、许可证、目标版本、实际可复用代码和功能验证，再登记为已集成。不以完整项目名称代替已完成的具体功能。
