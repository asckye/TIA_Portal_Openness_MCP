# P8-30 项目生成框架：方案与待定问题（研究稿，2026-10-08）

口径：master `f02b4cdd`（工作树另有未提交的 `src/Logic/ModelContextProtocol/ToolProfiles.resx`）。工具清单取自
`manifest/tools-list.json`（V21 完整目录 498 个）与 `manifest/contracts/v4/baseline/19.json`（V19 Foundation 67 个）；
API 可用性取自本地 `sdk/TIA_V*_PublicAPI` XML（不随仓库分发）。外部资料访问日期均为 2026-10-08。
本稿只读调研，未构建、未测试。API 存在、工具存在都不等于原生行为已验收；各族仍按 `current / NOT RUN` 处理。

## 0. 结论摘要

- 标准包推荐 **目录形态 + JSON 清单与分部文件**（Git 友好，可在工作台编辑），分发时把整个目录打成 zip；
  全局库文件只作为可选资源按 TIA 版本引用。所有部分用 JSON Schema 2020-12 校验，包本身用 SemVer，格式用整数 `schemaVersion`。
- 规则只用 **声明式结构 + 受限占位符**，不执行包内代码；同一份命名、结构、库、报警、HMI 定义同时驱动生成和风格检查（“生成即自检”）。
- 设备清单推荐 **规范 JSON 机器描述（MachineDescription）作为唯一生成输入**；首版收 JSON（AI 客户端整理自然语言或表格）
  和由标准包导出的 xlsx/CSV 模板，工作台表单只是同一 JSON 的编辑器。
- 首批推荐 **`tiamcp.basic`**（由现有模板整理，自写 SCL，八版 PLC 通用）和 **ISA-88/PackML 风格状态模型包**（按公开状态模型自写实现，不声明认证）。
  SAF 和 VASS、SICAR 等企业标准都是专有内容，框架保证能由用户自行编写或导入，仓库不带它们的内容。
- 流程拆为 **Plan（离线展开 + 读回比对，不写）→ 预览 → Apply（一次审批绑定计划哈希，逐步走现有工具并逐步审计）→ Check**。
  续跑就是对当前工程重新规划，按幂等键只补缺。
- 主要能力缺口：库类型和主模板实例化（`PlcBlockComposition.CreateFrom(CodeBlockLibraryTypeVersion)` 等，PublicAPI 14 SP1 起即有）没有工具；
  旧版（14 SP1–19）的硬件、分组、库、报警和 HMI 工具要等 P8-03 的 B2/B3/B5/B6/B10 批次。

## 1. 现有能力盘点

### 1.1 三条现有生成路径

| 路径 | 入口与文件 | 版本 | 能做什么 | 离“标准驱动”还差什么 |
|---|---|---|---|---|
| 一次性脚手架 | `BuildProjectScaffold`（`src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs:231`、`ScaffoldOperations.cs`）；CLI `gen`/`patch`（`docs/getting-started/cli.md`）；spec 例 `templates/project-blueprints/scaffold_spec_motor.json`、`scaffold_spec_start_stop.json` | 20/21（worker 引擎） | 一个 JSON spec：建工程、1 个 PLC、可选 1 个 Unified HMI、UDT/全局 DB/变量表、SCL 源、S7DCL、编译、HMI 连接/画面/变量、保存；`dryRun` 默认 true 只做离线校验 | 单 PLC/单 HMI，无设备清单和重复展开，无命名/结构规则；一次调用内部直接调服务，审批与审计只有一条；自动连接、自动保存；无读回比对，不能对已有工程补缺 |
| 蓝图 + 逐步调用 | `templates/project-blueprints/full_plc_hmi_project.json`（硬件档案、导入顺序、HMI 模板、`requiredBundleFiles`）；`docs/guides/project-generation.md`；`GetToolUsage` 的 `sequence/connect-project`、`sequence/hardware-device`、`sequence/plc-scl-block`、`sequence/hmi-unified-screen` 等 30 个序列 | 主要 20/21；PLC 部分八版有对应 Foundation 序列 | AI 客户端按序列逐个调工具，每个写调用照常预检、审批、审计 | 规则在 AI 的上下文里，不可复现、不可版本化；每步一次审批，大工程不可操作 |
| 参考目录播种 / XML 模板展开 | `SeedProjectFromReference`（`src/Engine/Siemens/Services/TypesService.cs:167`：`manifest.json` + `{{PLACEHOLDER}}` 替换，`plc/blocks`、`plc/types`、`hmi/screens`、`hmi/tags`）；`InstantiatePlcTemplates`（`TemplateTools.cs`：XML 模板 `{{Name}}` 结构化替换，1..100 行，只写文件） | 20/21 | 从导出的参考对象复制并替换占位符 | 只有文本替换，没有设备类型、信号、地址分配和调用关系；无检查 |

### 1.2 可复用的构件（按生成阶段）

“八版”指 Foundation 目录在 14 SP1–21 都有；“20/21”指目前只在 V20/V21 完整目录（旧版待 P8-03 对应批次，批次见 `docs/development/phase8-port-plan.md` 第 5 节）。

| 阶段 | 工具 / 文件 | 版本 | 说明 |
|---|---|---|---|
| 工程 | `CreateProject`、`OpenProject`、`AttachOpenProject`、`SaveProject`；`SaveProjectCopy`、`ArchiveSavedProject` | 八版；后两者 20/21（F05，B8） | 保存/另存照常审批 |
| 硬件 | `CreateHardwareDevice`（V19–21，仅 CPU 1211C、1513 两个已文档化型号）；`CreateDevice`、`CreateHardwareCatalogDevice`、`SearchHardwareCatalog`、`PlugDeviceItem`、`SetDeviceAddress`、`SetDeviceItemIoAddress`、`ManageHardwareObject`、`ManageDeviceUserGroup` | 20/21（F18/F19，B2 起旧版） | `PlugDeviceItem` 支持 `positionNumber=-1` 自动选槽，生成计划应改为显式槽号 |
| 网络 | `EnsureSubnet`、`AttachDeviceNodeToSubnet`、`ConnectDeviceNodesToProfinetSubnet`、`ManageIoSystem`；离线 `PlanHardwareNetworkConfiguration` | 20/21（F20，B2） | 离线计划校验可直接复用为“网络阶段预检” |
| 批量硬件 | `BuildDeviceAmlDocument`（离线生成 CAEX 2.15）+ `ImportDeviceAml` | 20/21（F21） | 多站点时比逐个建设备快；需同版本参考 AML 才标 `importVerified` |
| PLC 类型/数据 | `BuildPlcUdt`、`BuildPlcGlobalDb`（`outputReleaseKey` 支持八版格式）、`BuildPlcTagTable`；`ImportPlcType`、`ImportPlcTagTable`、`CreatePlcTagTable`、`CreatePlcTag`、`CreatePlcUserConstant` | 八版 | 其余 Build* 只输出 V21 候选 XML |
| PLC 程序 | `StageImportFiles` → `ImportPlcExternalSource` → `GenerateBlocksFromExternalSource` → `CompilePlcDiagnostics`；`ImportPlcProgramFromDirectory`；`PlanArtifactImportOrder`（依赖排序） | 八版 | **SCL 外部源是八版通用的程序生成通道**：FB、UDT、调用 FC、以及 `DATA_BLOCK "IDB_x" "FB_y"` 形式的实例 DB 都可写在源里 |
| PLC 结构 | `CreatePlcBlockGroup`、`CreatePlcTypeGroup`、`ManagePlcUserGroup`、`CreatePlcInstanceDb`、`ManagePlcSoftwareUnit`（含 `createFromMasterCopy`）、`ManagePlcDocuments`（含 UDT 的 `createFromLibraryType`） | 20/21（F08/F09/F16，B3/B4） | 旧版目前不能建块分组，结构规则在旧版只能检查不能生成 |
| 库 | `ManageGlobalLibrary`、`ManageLibraryType`、`ManageLibraryTypeVersion`（`updateInstances`/`findInstances`）、`SynchronizeLibrary`（`updateProject`/`harmonizeProject`）、`CreateLibraryMasterCopy`、`ImportLibraryTypeDocuments`、`CheckLibraryUpdates`、`CompareLibraries` | 20/21（F23，B6） | **没有把 FB/FC 库类型或块/变量表/设备主模板放进工程的工具**（见缺口 G3） |
| 报警 | `ManagePlcAlarmTextList`、`ImportAlarmClasses`、`ImportPlcAlarmInstanceTexts`（xlsx）、`BuildPlcAliasAlarmLad`；Unified `ManageUnifiedEngineeringObject`（alarmClasses/discreteAlarms/analogAlarms） | 20/21（F14 16+，B5；F27） | SCL 中的 `Program_Alarm` 实例随源导入，八版可用 |
| HMI（Unified） | `EnsureUnifiedHmiConnection/TagTable/Tag/Screen/ScreenItem`、`ApplyUnifiedHmiScreenDesign/Theme/Layout`、`ManageUnifiedScreenItem`（可用 `containedType` 建面板容器）、`EnsureUnifiedHmiButtonAction`、`BindUnifiedHmiTagDynamization`；离线 `BuildUnifiedHmiTemplateApplyDesign`、`BuildUnifiedHmiThemeDesign`、`BuildUnifiedHmiLayoutDesign`、`AnalyzeUnifiedHmiTemplateLayout`、`RunHmiTemplatePlcSyncPrecheckSuite`；模板 `templates/hmi/*.json`（7 个画面） | 20/21（F26/F27，B10 起 V19） | 面板类型的编写是已知边界（`GetOpennessCompatibility` 说明）；面板接口绑定未验证 |
| HMI（Classic）/SiVArc | `BuildClassicHmiTagTable/Screen/MinimalPackage`（离线）；`ManageSivarc*`、`GenerateSivarc` | 20/21（F25 B7，F29 B11） | SiVArc 本身就是西门子“规则生成 HMI”的产品化方案，可作为某些包的后端 |
| 模板素材 | `templates/plc/plcbuild-json/*`（`UDT_BasicStatus`、`DB_BasicStatus`、`DB_HMI_Interface`、`Basic_Signals`）、`templates/plc/scl-examples/*`（`FB_BasicLatch`、`FB_StepSequenceDemo`、`FB_TimerCounterDemo`、`FC_BasicScaleLimit` 等）、`templates/plc/lad-recipes`、`templates/plc/instruction-recipes` | — | basic 包的起点 |
| 西门子参考 | `reference/siemens-openness/skills/global-library/SKILL.md`（MIT） | — | 给出库类型/主模板放置的官方写法：`Blocks.CreateFrom(CodeBlockLibraryTypeVersion, UpdatePathsMode)`、按 `ContentDescriptions` 分流、`GlobalLib://库文件名/Master copies/...` 引用格式、放置后改名的幂等三步 |

### 1.3 检查与报告

| 能力 | 工具 | 版本 | 说明 |
|---|---|---|---|
| 导出文件规则检查 | `AuditEngineeringExports`（`QualityAuditTools.cs`）：块名正则、重名、块/网络注释、元数据、网络数，及 `rules=[{id,files,xpath,minCount,maxCount,valuePattern,severity}]`；HTML/PDF 报告 | 20/21（F02 离线，B1 可下放八版） | 已有“规则 → 发现”的执行器，风格检查可直接复用其 XPath 规则格式 |
| TIA Test Suite 风格指南 | `ListTestSuiteCases`、`ManageTestSuiteCase`、`ExchangeTestSuiteCase`、`RunTestSuiteCase`（`RuleSetExecutor`） | 20/21（F34，17+ 且需 Test Suite 产品，B12） | 原生规则集可经 XML 导入导出 |
| 其他 | `RunPlcCompanionTool`（MIT siemens-plc-tools 的 SCL lint）、`CompareProjects`（`softwareToLibrary`）、`GetPlcObjectFingerprints`、`ValidateAutomationContext`、`ExportPlcBlocks`/`ExportPlcTypes`/`ExportPlcTagTable`（八版） | 各异 | 指纹与库对比可用于幂等判定和“库实例是否被改过” |

### 1.4 编排、审批与审计

- `PreviewToolBatch`/`ApplyToolBatch`（`src/Engine/ModelContextProtocol/Tools/McpServer.Batch.cs`）：1..50 个写工具，令牌 10 分钟，执行前复核身份与预览，失败即停、余下标 `NOT_EXECUTED`，无回滚；只在 20/21 宿主。
  `ApplyToolBatch` 本身不审批，内部每个写调用各自审批（`McpServer.Approval.cs:152`）。
- `RunToolTransaction`：原生事务，只接受少数分组/属性编辑白名单。
- 审批：写调用默认在工作台逐个审批（`docs/getting-started/configuration.md` “V4 结果、审批与审计”）。审批协议 `src/Shared/ApprovalProtocol.cs` 的 `PendingApproval`
  已有 `PlanHash`、`ArgumentDigest`、`Operations: ApprovalAction[]（tool/action/target）`，**可以承载“一次审批一份计划”**，需要新增请求种类和工作台视图。
- 审计：`data/logs/audit` 哈希链，同一写调用的 request/决定/start/end 共用 requestId，可带 planHash。

### 1.5 缺口

| 编号 | 缺口 | 影响 | 处理 |
|---|---|---|---|
| G1 | 没有“标准包”概念：无 schema、加载器、版本、导入导出、存放位置 | 规则散在模板、文档和 AI 上下文 | P8-31a/d |
| G2 | 没有规范的机器/设备清单模型；scaffold spec 是扁平的单 PLC/单 HMI | 不能从 N 台设备展开 | 第 4 节 |
| G3 | 没有库类型/主模板实例化工具：`PlcBlockComposition.CreateFrom(CodeBlockLibraryTypeVersion)`、`PlcTypeComposition.CreateFrom(PlcTypeLibraryTypeVersion)`（UDT 部分已在 `ManagePlcDocuments`）、`PlcBlockComposition/PlcTagTableComposition/DeviceComposition/DeviceItemComposition.CreateFrom(MasterCopy)`。本地 PublicAPI XML：14 SP1 起都有，20+ 增加 `MasterCopyMode`/`UpdatePathsMode` 重载 | 以全局库分发的标准块库（SAF、LGF 一类及企业库）无法落到工程 | 新工具进 F23/F09/F18 迁移批次（P8-31f），按 P8-03 门禁 |
| G4 | 调用网络生成：LAD/FBD 构建器只出 V21 候选 XML | 旧版不能生成图形调用网络 | 调用组织块统一生成 SCL 源（八版） |
| G5 | 没有幂等的“确保”语义和对已有工程的差异计划；`ApplyToolBatch` 限 50 步、仅 20/21、无续跑 | 再生成会重复或冲突 | 第 6.5 节 |
| G6 | 每步一次审批 | 上百步的生成无法操作 | 第 6.6 节，计划级审批 |
| G7 | 风格检查只有通用规则，没有“按标准”的规则包，也没有与 Test Suite 规则集的对应 | Eigen 有、本项目弱（`gap-review-2026-10.md` 第二节） | 第 3.5 节 |
| G8 | 旧版能力：14 SP1–19 无硬件（V19 仅两个 CPU）、无分组、无库、无报警、无 HMI 工具 | 旧版只能生成 PLC 程序部分 | 计划逐步标注版本可用性，随批次开放 |
| G9 | Unified 面板实例的接口绑定未验证；面板类型编写不支持 | HMI 约定只能先用“画面元素组”实现 | basic 包 1.0 不用面板类型 |
| G10 | 工作台没有生成模块；工作台自身 TIA 会话不能写工程（P8-20 红线） | 向导必须经共享 MCP 会话执行 | 依赖 P8-20/P8-21 |

## 2. 公开标准与参考

| 来源 | 是什么 | 可为标准包提供 | 许可与使用限制 |
|---|---|---|---|
| 西门子 Automation Framework（SAF） | 西门子 2026-06-17 新闻稿称 Eigen 生成的项目“遵循 Siemens Automation Framework”，即西门子组织 TIA 工程的最佳实践参考；contentpath 有应用示例《Automation Framework for SIMATIC PLC/HMI and SINAMICS Drives》，正文需在 SIOS 获取 | 工程结构、标准库使用方式、PLC/HMI/驱动一体的组织方式；Eigen 的“自然语言 → 符合标准的工程”正是本框架要覆盖的工作流 | 公开资料只有简介，细节和库属专有；仓库不能带其内容。框架应支持用户凭自己的 SAF 资料和库编写适配包（外部库引用） |
| SIMATIC Modular Application Creator | 西门子按“参数化设备模块 → 生成整个 TIA 工程”的生成器，设备模块按 365 天许可单独出售，V18+ | 证明“设备类型 + 参数 → 硬件、块、参数”的规则模型可行；与本框架的 `deviceTypes` 概念一致 | 专有，只作思路参考 |
| ISA-88 / ANSI/ISA-TR88.00.02（PackML）、OMAC | 单元控制模式（Production、Maintenance、Manual，及用户自定义模式）、状态机（Idle、Starting、Execute、Holding、Suspended、Completing、Complete、Resetting、Stopping、Stopped、Aborting、Aborted、Clearing 等）、命令和 PackTags 数据结构；ISA-88 物理模型（单元/设备模块/控制模块） | `modes` 部分（模式、状态、转换、模式-状态可用矩阵）；机器描述的拓扑层级；状态数据接口 UDT | ISA 文本收费且有版权；状态与模式名称、编号属互操作事实，可按公开资料自写实现，不复制文本和表格，不声明认证。“PackML”为 OMAC 名称，包名用“ISA-88/PackML 风格”措辞 |
| OPC 30050 PackML（OPC UA 配套规范） | 与 OMAC 共同制定，基于 TR88.00.02，定义状态机对象类型、模式与状态、命令与状态标签；在 reference.opcfoundation.org 公开阅读 | 状态编号与 OPC UA 映射；可接现有 `GenerateOpcUaModelledInterface`/`ManageOpcUaInterface` | 受 OPC Foundation 规范许可约束；实现规范允许，转载文本不允许 |
| OPC 40001-1 Machinery 及 VDMA 配套规范 | `MachineryItemState`、`MachineryOperationMode`、标识、计数等机器级构件（现行 1.04.0） | 机器级状态/模式的对外映射、机器标识字段 | 同上 |
| 西门子 LPMLV30（OMAC PackML V3.0 for S7-1200/1500，SIOS 49970441）与 Packaging Toolbox | 西门子对 OMAC 状态管理器的实现；工具箱同时支持 OMAC、PackML、Weihenstephan | 用户已有该库时，状态模型包可声明它为可替换实现 | 西门子应用示例条款：非独占、不可转授，仅可随自有产品转交；只能作“用户自备”外部引用 |
| Weihenstephan Standards（WS Pack 等）、OPC 40600 | 灌装/包装线的数据采集接口与机器模板，正向 OPC UA 迁移 | 数据接口规则（某类设备必须提供的数据点） | 规范由 TUM 用户组管理，按其条款使用 |
| VDI/VDE/NAMUR 2658（MTP） | 流程工业模块类型包：状态与服务模型、HMI 建模、数据对象库、OPC UA 接口 | “包描述模块”的成熟先例；状态/服务模型；将来可作为导出目标 | 标准收费，按规范实现 |
| ISA-18.2 / IEC 62682 | 报警管理生命周期：理念、识别、合理化（分级、优先级、文档化）、详细设计、监视与变更 | `alarms` 部分字段：类别、优先级、确认模型、原因/后果/处置文本、抑制 | 标准收费；只取字段概念 |
| ISA-101 | 过程自动化 HMI 的理念、风格指南、导航层级与生命周期 | `hmi` 部分：画面层级、导航、颜色约定、报警呈现 | 同上 |
| IEC 81346 | 参考代号：功能（=）、产品（-）、位置（+）三个方面 | 机器描述中的设备 ID 与命名模板的分段方式 | 标准收费；结构概念可用 |
| PLCopen Coding Guidelines v1.0（2016，免费下载） | 命名、注释、编码实践等 60 余条规则 | `checks` 规则（引用规则编号，自写说明） | 免费下载；不转载原文 |
| 西门子 Programming Styleguide（S7-1200/1500，SIOS 81318674；英文 v1.1、德文 v2.0）、Programming Guideline、HMI Styleguide、Unified JS Styleguide（109758536）、WinCC Unified Engineering Guideline（109827603）、HMI Template Suite（91174767） | 带编号的命名、数据、结构规则；Unified 画面与脚本约定 | 西门子风格检查包：只引用规则编号并自写简述 | 应用示例条款（同上）；不转载原文 |
| TIA Portal Test Suite 风格指南 | 规则集覆盖变量、块、块接口、UDT 的名称长度、包含、前后缀、大小写、属性；规则集可经全局库或 XML 导入导出（V17 Advanced 起有 Openness 支持） | 检查的原生后端：标准包的命名规则可编译成 Test Suite 规则集 | 需用户安装与许可 Test Suite |
| 企业/行业 OEM 标准：VASS（大众集团，V6 基于 TIA，以 DB 取代位存储，需认证）、SICAR@TIA（西门子汽车行业平台：运行模式区、TecUnit、ProDiag、面板）、Daimler Integra 等 | 专有、需认证或授权 | 框架需覆盖的特征：模式区、设备类型单元（TecUnit 类）、GRAPH 顺序、ProDiag 报警、Safety | 仓库不带任何内容；由持有者自行编写包。维护者自用的编程规范（`OBJECT_FUNCTION_BLOCKTYPE`、`_IDB`、SICAR 式程序组）可作为第一个“企业包”的试点，是否入库由维护者定 |
| `siemens/tia-portal-ai-extensions`（MIT，已收录于 `reference/siemens-openness`） | 官方 Openness 指南 | 库放置算法、`GlobalLib://` 引用格式、放置后改名的幂等写法 | MIT，保留出处 |

对照：Eigen 的“标准化项目生成”绑定 SAF 和西门子云模型；本框架的差异是标准包开放、可替换、可审阅，生成前有完整计划预览，执行走审批与审计。

## 3. 标准包格式选项

### 3.1 包要表达的内容

| 部分 | 内容 | 生成时用途 | 检查时用途 |
|---|---|---|---|
| `package.json` | id、SemVer 版本、`schemaVersion`、标题（多语言）、发布者、SPDX 许可证、`targets`（TIA 发布键、CPU 系列、HMI 种类）、`requires`（框架版本、可选产品如 Test Suite/SiVArc/ProDiag）、`extends`、`dependsOn`、文件清单与 SHA-256、必需语言 | 选包、版本门禁、完整性 | 同左 |
| `naming` | 各对象种类（设备、站、FB/FC/OB/DB/实例 DB、UDT、变量表、变量、HMI 画面/变量、报警）的命名模板与校验正则、长度、字符集、大小写、保留字、缩写词典、注释语言要求 | 生成名称 | 名称规则 |
| `structure` | 设备分组、块/类型/变量表分组树、软件单元（可选）、OB 布局与调用顺序、编号区段 | 建分组、放置对象 | 位置与存在性规则 |
| `hardware` | 设备角色 → 订货号/固件（按 TIA 版本给可选值）、模块槽位模板、IP 与 PROFINET 设备名分配规则、IO 系统命名 | 建站、插模块、地址 | 硬件约定检查 |
| `library` | 逻辑类型 ID → 各版本实现（自带 SCL 源 / 全局库类型 / 用户自备外部库）；**接口契约**：成员名、类型和语义角色（`signal.run`、`hmi.interface`、`alarm.overload` 等） | 实例化与接线按角色而不是按名字 | 实例是否来自批准类型、版本是否最新 |
| `modes` | 模式、状态、命令、转换、模式-状态可用矩阵，实现它的类型与数据接口 | 生成模式/状态管理与接口 | 模式管理器是否存在、接口是否完整 |
| `alarms` | 报警类别（优先级、确认、颜色）、按设备类型的报警模板与文本键、编号方案、落地方式（Program_Alarm / ProDiag / HMI 离散报警 / 文本列表） | 生成报警 | 文本与语言完整性 |
| `hmi` | 分辨率档、主题调色板（`BuildUnifiedHmiThemeDesign` 格式）、画面模板（`designJson` + 占位符）、导航层级、设备类型 → 控件组或面板类型、HMI 变量表分组 | 生成画面 | 画面命名、尺寸、主题色、导航 |
| `rules/*.json` | 每个设备类型：参数 schema、信号表、`emit` 列表（硬件、变量、实例、调用、报警、HMI） | 展开设备清单 | 由同一定义推导“应有对象” |
| `checks` | 额外检查（禁用写法、注释要求、导出 XPath、库版本、Test Suite 映射）；由 `naming`/`structure` 自动推导的规则不必重复写 | 生成后自检 | 风格检查 |
| `sources/`、`libraries/`、`examples/` | 自写 SCL/designJson 资源；可选的按版本全局库；示例机器描述与黄金计划 | 产物 | 包自测 |

### 3.2 选项比较

| | A. 目录 + JSON 清单与分部文件（zip 仅作分发） | B. 单个 zip，内部分部件按 schema 校验（类 OPC 打包） | C. TIA 全局库为主体 + 旁注规则文件 |
|---|---|---|---|
| 编写与审阅 | 文本文件，可 diff、可评审、可在工作台逐部分编辑 | 需解包才能看和改；Git 中是二进制 | 需在 TIA 中编辑；规则另写 |
| 离线校验 | 全部可用 JSON Schema 和交叉引用检查 | 同 A | 库内容只能在 TIA 中读（离线仅 `AnalyzeGlobalLibraryPackage` 看目录结构） |
| TIA 版本 | 自带 SCL 源可八版通用；全局库按版本分别引用 | 同 A | 库文件按版本一份（.al14…/.al21），只能升不能降 |
| 硬件/画面素材 | 需自己序列化（JSON） | 同 A | 主模板可直接带站点、画面、面板 |
| 厂商中立 | 是 | 是 | 绑定 TIA 库格式；AI 客户端无法直接读 |
| 第三方内容 | 外部引用即可，不必携带 | 同 A | 容易把专有库连同包一起分发 |
| 完整性/分发 | 清单内 SHA-256；分发时打 zip | 天然单文件 | 库文件本身 |
| 主要风险 | 规则与库的一致性要靠校验 | 不利于版本管理和协作 | 无法离线验证；难以由 AI 和工作台编写 |

**推荐 A**，并吸收 B 的分发方式：仓库和数据目录中以目录为准，`ManageStandardPackage export` 把目录无损打成 zip（扩展名待定，如 `.tiastd.zip`），import 时解包校验后放入数据目录。C 的优势通过 `library.implementations` 中的 `globalLibrary` 实现方式和主模板引用保留下来。

### 3.3 推荐格式的细节

目录布局（内置包建议放 `templates/standards/<id>/`，随交付包；用户导入的包放数据根 `data/standards/<id>/<version>/`）：

```text
tiamcp.basic/
  package.json        naming.json      structure.json   hardware.json
  library.json        modes.json       alarms.json      hmi.json
  checks.json         rules/motor.dol.json  rules/valve.2pos.json  ...
  sources/plc/*.scl   sources/hmi/*.json
  libraries/          （可选，按 TIA 版本的全局库；basic 不带）
  examples/machine.json  examples/plan.21.json
  README.md  CHANGELOG.md  LICENSE  NOTICE
```

清单示意：

```json
{
  "schemaVersion": 1,
  "id": "tiamcp.basic",
  "version": "1.0.0",
  "title": { "zh-CN": "基础标准", "en-US": "Basic standard" },
  "license": "MIT",
  "extends": null,
  "targets": { "releases": ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"],
               "plcFamilies": ["S7-1200", "S7-1500"], "hmi": ["WinCCUnified"] },
  "requires": { "framework": ">=1.0.0", "optionalProducts": [] },
  "languages": { "required": ["zh-CN", "en-US"], "default": "zh-CN" },
  "parts": { "naming": "naming.json", "structure": "structure.json", "library": "library.json",
             "modes": "modes.json", "alarms": "alarms.json", "hmi": "hmi.json",
             "hardware": "hardware.json", "checks": "checks.json", "rules": ["rules/*.json"] },
  "files": [ { "path": "sources/plc/FB_MotorDol.scl", "sha256": "…" } ]
}
```

- **校验**：每部分一份 JSON Schema 2020-12（建议 `schemas/standards/v1/*.schema.json`，随交付包；`GetToolUsage` 与 `DescribeStandardPackage` 引用），
  再加交叉引用检查：规则引用的类型、命名键、画面模板必须存在；每个声明的目标版本都有类型实现；所有生成名称通过本包命名规则；文件哈希一致；规则展开确定性（同输入同输出）。
  C# 侧放在 `TiaMcp.Logic`（net48;net10.0）。校验库候选 JsonSchema.Net（MIT，基于 System.Text.Json），引入新依赖前核对目标框架与许可；也可只用 schema 文件给编辑器和 AI，C# 侧手写等价校验。
- **版本**：包用 SemVer；`schemaVersion` 是框架格式的大版本，只在不兼容时加一；`dependsOn`/`extends` 用插入号范围（`^1.2`）。
  库类型版本在 `library.json` 中声明，规则以 `lib:<库>/<类型>@<范围>` 引用。工程生成记录写明包 id、版本与包哈希，升级包后重新规划即可看到差异（如库类型版本提升 → `SynchronizeLibrary updateProject` 或 `ManageLibraryTypeVersion updateInstances` 步骤）。
- **派生包**：`extends` 允许企业在中性包上覆盖命名、结构或替换类型实现。覆盖按条目 id 整条替换，不做深层合并，保证结果可预测；工作台显示合并后的有效定义及每条的来源。
- **规则如何引用库类型**：

```json
{
  "types": [{
    "id": "fb.motorDol", "kind": "FB", "version": "1.0.0",
    "interface": {
      "in":    [{ "name": "Feedback", "type": "Bool", "role": "signal.feedback" },
                { "name": "Overload", "type": "Bool", "role": "signal.overload" }],
      "out":   [{ "name": "Run", "type": "Bool", "role": "signal.run" }],
      "inOut": [{ "name": "Hmi", "type": "\"UDT_MotorHmi\"", "role": "hmi.interface" }]
    },
    "implementations": [
      { "releases": "*", "kind": "sclSource", "files": ["sources/plc/UDT_MotorHmi.scl", "sources/plc/FB_MotorDol.scl"] },
      { "releases": ">=17", "kind": "globalLibrary", "library": "acme.motion",
        "typePath": "Motors/FB_MotorDol", "versionRange": "^1.2" }
    ]
  }],
  "libraries": [{ "id": "acme.motion", "provision": "external",
                  "fileNames": { "21": "AcmeMotion.al21" }, "note": "用户自备，不随包分发" }]
}
```

  `sclSource` 走外部源导入（八版）；`globalLibrary` 走新的库放置工具（G3）；`external` 要求用户先用 `ManageGlobalLibrary` 打开该库，计划阶段核对库名、类型路径和版本，缺失即在预览中报前置条件。

- **规则（设备类型）示意**：

```json
{
  "deviceType": "motor.dol",
  "title": { "zh-CN": "直接启动电机" },
  "params": { "type": "object", "properties": { "powerKw": { "type": "number", "minimum": 0 } } },
  "signals": [ { "role": "run", "dir": "DO" }, { "role": "feedback", "dir": "DI" },
               { "role": "overload", "dir": "DI", "optional": true } ],
  "emit": [
    { "kind": "plc.tag", "forEach": "signals", "table": "{{unit.tagTable}}",
      "name": "{{naming.ioTag(device, signal)}}", "address": "{{alloc.io(signal)}}" },
    { "kind": "plc.instance", "type": "lib:tiamcp.basic/fb.motorDol@^1",
      "name": "{{naming.idb(device)}}", "callIn": "{{unit.callBlock}}",
      "bind": { "signal.run": "{{tag(signal.run)}}", "signal.feedback": "{{tag(signal.feedback)}}",
                "hmi.interface": "{{naming.hmiDb(unit)}}.{{device.id}}" } },
    { "kind": "alarm", "when": "signals.overload", "class": "Fault", "textKey": "motor.overload" },
    { "kind": "hmi.widget", "template": "sources/hmi/motor_widget.json",
      "screen": "{{device.hmi.screen}}", "slot": "auto" }
  ]
}
```

### 3.4 规则表达能力

- 只允许：`forEach`（遍历信号、子设备或清单集合）、`when`（存在、相等、属于集合）、命名模板、`{{ }}` 占位符和框架内置函数（`naming.*`、`tag()`、`alloc.io()`、`alloc.ip()`、`seq()`、`pad()`、`upper()`、`lower()`、`text()`）。
- 不允许包内脚本、正则替换链、任意表达式或网络访问。理由：包可能来自第三方，导入后不应执行代码；与 `InstantiatePlcTemplates` 不执行定制脚本、HMI 动作脚本只允许安全配方的现有原则一致；展开结果可确定、可测试、可哈希。
- 需要更复杂逻辑的地方（比如设备的控制算法）放在包的 SCL 源或库类型里，而不是放在生成规则里。

### 3.5 同一标准包驱动风格检查

| 包部分 | 推导出的检查 | 后端 | 版本 |
|---|---|---|---|
| `naming` | 各对象种类名称正则、长度、字符集、大小写、注释语言 | 读回（`ListPlcBlocks`、`ListPlcTags`、`ListPlcTypes`、`GetProjectTree` 等）；可编译为 Test Suite 规则集 | 读回八版；Test Suite 17+ 且需产品 |
| `structure` | 必需分组存在；对象位于规定分组；OB 集合；编号区段 | 读回块层次、软件树 | 八版 |
| `library` | 设备控制块是否为批准类型的实例；类型版本是否为发布的默认版本；实例是否被改过 | `CheckLibraryUpdates`、`ManageLibraryTypeVersion findInstances`、`GetPlcObjectFingerprints`（LibraryType 指纹）、`CompareProjects softwareToLibrary` | 20/21（B6 起旧版） |
| `alarms` | 报警类别存在且属性一致；必需语言文本齐全 | 报警导出/读取 | 20/21（B5 起 16+） |
| `hmi` | 画面命名、尺寸、主题色、导航；控件/面板来自批准模板 | Unified 读取；导出 + `AnalyzeUnifiedHmiTemplateLayout` 一类检查 | 20/21（B10 起 V19） |
| `checks`（显式） | 禁用写法（如 M 区绝对地址：`//*[local-name()='Address'][@Area='Memory']`，示意）、块/网络注释、复杂度 | 导出 XML + `AuditEngineeringExports` 规则执行器；SCL 经伴随工具 lint | 导出八版；执行器下放见 B1 |

检查结果统一为发现列表（规则 id、严重性、对象路径、说明、修复提示、是否可自动修复），输出 JSON 与 HTML，可选 SARIF 2.1.0 便于接入 CI。
生成计划在执行前先对“将要生成的对象”跑同一套检查，保证生成结果本身合规；自动修复（如改名）以后可产出一份生成计划，走同一条 Apply 通道。

## 4. 设备清单输入

### 4.1 选项

| | 表格模板（xlsx/CSV） | 工作台表单 | AI 整理的 JSON |
|---|---|---|---|
| 适合 | 电气/工艺已有设备表、ECAD 导出、几十到几百台设备 | 小机器、补录、修改 | 自然语言描述、杂乱表格，由 AI 客户端整理 |
| 优点 | 工程师熟悉；可由包生成带下拉校验的模板 | 引导输入、即时校验 | 不需要解析；严格 schema 校验 |
| 缺点 | 需要 xlsx 读写（仓库现无该依赖）；合并单元格、自由文本 | 大量设备时效率低 | 可能编造或漏项，必须预览确认 |
| 依赖 | 表格库或只收 CSV | P8-20（AI 预填）、工作台模块 | `DescribeStandardPackage` 给出设备类型与参数 schema |

### 4.2 规范中间模型 MachineDescription

所有输入都先转成同一份 JSON，生成器只读它；它也是工作台表单编辑的对象和 AI 预填的内容。

```json
{
  "schema": "tiamcp.machine/1",
  "machine": { "id": "L1", "name": { "zh-CN": "灌装线 1", "en-US": "Filling line 1" } },
  "standard": { "package": "tiamcp.basic", "version": "^1.0" },
  "target": { "release": "21", "project": { "mode": "new", "name": "Line1", "directory": "D:\\Projects" } },
  "stations": [
    { "id": "PLC1", "role": "plc.main", "article": "6ES7 513-1AM03-0AB0", "firmware": "V3.1", "ip": "auto" },
    { "id": "HMI1", "role": "hmi.panel", "kind": "WinCCUnifiedPC" }
  ],
  "topology": [
    { "id": "U01", "kind": "unit", "name": { "zh-CN": "进料" },
      "children": [ { "id": "EM01", "kind": "equipmentModule", "name": { "zh-CN": "输送" } } ] }
  ],
  "devices": [
    { "id": "M101", "type": "motor.dol", "parent": "EM01", "station": "PLC1",
      "name": { "zh-CN": "进料输送电机" }, "params": { "powerKw": 0.75 },
      "io": { "run": "auto", "feedback": "auto", "overload": "%I4.2" },
      "alarms": { "overload": true }, "hmi": { "screen": "U01" } }
  ],
  "options": { "languages": ["zh-CN", "en-US"] }
}
```

要点：拓扑沿用 ISA-88 物理模型（单元/设备模块/控制模块），设备 ID 可用 IEC 81346 风格；`type` 必须是所选包的设备类型，`params` 按该类型的参数 schema 校验；
IO 与 IP 可写 `auto` 由包的分配规则确定。机器描述与包分离，同一份描述可换包重新生成（命名和结构随包变化）。P8-40 的 ECAD/AML 导入可输出同一模型的 `stations` 与 IO 部分。

### 4.3 表格模板（由包生成）

| 工作表 | 列 |
|---|---|
| `Machine` | 机器 ID、名称（每种必需语言一列）、包 id 与版本、目标 TIA 版本、工程名与目录 |
| `Stations` | 站 ID、角色（下拉）、订货号、固件、IP（或 auto）、PROFINET 设备名 |
| `Units` | 节点 ID、种类（unit/equipmentModule）、上级、名称（各语言） |
| `Devices` | 设备 ID、类型（下拉，来自包）、上级、站、名称（各语言）、`Param:<名>`（按类型展开的参数列）、`IO:<角色>`（地址或 auto）、`Alarm:<键>`（是/否）、HMI 画面、备注 |
| `IO`（可选） | 显式 IO 清单：信号、地址、类型、设备 ID、角色、注释（用于从 ECAD 表直接导入） |
| `Lists`（隐藏） | 下拉列表数据 |

CSV 形态：一个目录，每个工作表一个 CSV（UTF-8 带 BOM，便于 Excel 打开）。

### 4.4 推荐

规范 JSON 为唯一生成输入；首版收两种来源：JSON（AI/MCP 客户端，含自然语言整理结果）和由包导出的 xlsx/CSV 模板；工作台表单是同一 JSON 的表格编辑器，不另设格式。
xlsx 读写放在主机（net10）侧，候选 DocumentFormat.OpenXml（MIT）；如不想引入依赖，首版只收 CSV，xlsx 由 AI 客户端转为 JSON。

## 5. 首批标准

### 5.1 选项

| 选项 | 内容 | 评价 |
|---|---|---|
| 只做 basic | 由现有模板整理的中性包 | 风险最低，但不能证明“可替换”与状态机/模式部分 |
| **basic + ISA-88/PackML 风格状态模型包（推荐）** | 第二个包在 basic 之上（`extends`）增加单元模式/状态管理和标准数据接口 | 两个包共用设备类型，能演示派生、替换和检查差异；内容全部自写 |
| basic + SAF 适配包 | 规则自写，库由用户自备 | 公开资料不足以写出可靠的类型映射，且需要 SAF 许可与文档；建议在维护者取得资料后作为私有包或社区包 |

### 5.2 `tiamcp.basic` 需要什么

- 来源：`templates/plc/plcbuild-json`（`UDT_BasicStatus`、`DB_HMI_Interface`）、`templates/plc/scl-examples`（`FB_BasicLatch`、`FB_StepSequenceDemo`、`FB_TimerCounterDemo`、`FC_BasicScaleLimit`）、`templates/hmi`（概览、控制条、参数、趋势、事件、诊断画面）、两份 scaffold spec。
- 新写（自有 MIT）：`FB_MotorDol`、`FB_Valve2Pos`、`FB_DigitalSensor`、`FB_AnalogIn`（包装 `FC_BasicScaleLimit`）、简化的 `FB_ModeManager`（自动/手动/维护 × 停止/就绪/运行/故障）、对应 UDT 与 HMI 接口 UDT；生成的单元调用 FC 用 SCL。避免 S7-1500 专有指令，使 S7-1200 也可用。
- 命名：沿用现有模板的前缀风格（`FB_`、`FC_`、`UDT_`、`DB_`、实例 DB `IDB_<设备>`），设备 ID 用 `单元-设备`。
- 结构：`00_System`、`10_Modes`、`20_Units/<单元>`、`90_Hmi` 一类分组（旧版暂不能建分组时只出现在检查与报告中）。
- 报警：SCL `Program_Alarm`（八版）；20/21 另生成 Unified 报警类别。
- HMI：Unified，按设备类型的控件组模板（不用面板类型，见 G9），主题沿用现有模板调色板。
- 设备类型首批 4–5 个：`motor.dol`、`valve.2pos`、`sensor.digital`、`analog.in`、`unit`（单元本身：模式管理、调用 FC、单元画面）。
- 示例：`examples/machine.json`（8 台电机、4 个阀、6 个传感器、2 路模拟量）和各目标版本的黄金计划。

### 5.3 ISA-88/PackML 风格状态模型包需要什么

- `extends: tiamcp.basic`；替换 `FB_ModeManager` 为按公开状态模型自写的 `FB_UnitStateManager`（模式、状态、命令、模式-状态可用矩阵、状态完成握手），状态数据 UDT（命令、状态、计数等最小集合）。
- 可选 OPC UA 映射：按 OPC 30050 的对象类型生成接口（20/21 现有 OPC UA 工具，B5 起 15.1+）。
- 若用户有西门子 LPMLV30，可在 `library.implementations` 中声明为外部实现，接口契约映射到其参数；仓库不带该库。
- 测试：20/21 用 `RunPlcSimAdvancedTestScenario` 驱动状态转换；旧版用 SCL 自测骨架（`templates/plc/scl-examples/FB_SelfTest_Template.scl`）。
- 措辞：包名与文档写“ISA-88/PackML 风格”，不声称符合 OMAC/ISA 认证；只引用规范章节与链接，不转载文本和表格。

### 5.4 许可证规则

- 仓库与交付包只放自有内容（MIT）和开放许可内容（保留出处与许可证，沿用 `reference/siemens-openness` 做法）。
- 西门子应用示例与库（LGF、LPMLV30、HMI Template Suite、Styleguide 等）：条款为非独占、不可转授，仅可随自有产品转交；只作“用户自备”外部引用，检查规则只写规则编号与自写简述。
- ISA/IEC/VDI 标准与 OPC 配套规范：只按公开信息自写实现和字段设计，不转载文本、表格与图。
- 企业标准（VASS、SICAR、Integra、维护者自用规范等）：由持有者编写为私有包；框架的 import/export 与 `extends` 保证它们能在不入库的情况下使用。

## 6. 生成流程与 MCP 工具草案

### 6.1 流程

```text
机器描述（JSON / xlsx·CSV / 表单 / AI 整理）
   │ ManageMachineDescription(validate|import)        —— 离线，八版
   ▼
标准包（内置或导入） ── ValidateStandardPackage / DescribeStandardPackage
   │
   ▼
PlanProjectGeneration ── 规则展开 → 期望模型 → （已有工程）读回比对 → 步骤与产物 → 版本可用性 → 自检
   │  返回 planId、planHash、按阶段的步骤、差异树、产物哈希；不写工程
   ▼
预览（AI 客户端或工作台向导；可选：逐步 dryRun 预览，只读、不审批）
   │
   ▼
ApplyGenerationPlan(planId, expectedPlanHash, expectedProject, phases?)
   │  一次工作台审批（绑定计划哈希与全部步骤）→ 逐步经现有工具执行 → 逐步审计 → 失败即停
   ▼
GetGenerationRun（结果） ──► CheckProjectStandard（同一包检查，报告）
```

### 6.2 工具草案

| 工具 | 操作类别 | 执行位置 | 版本 | 作用 |
|---|---|---|---|---|
| `ListStandardPackages` | READ | 主机 | 八版 | 列出内置与已导入的包：id、版本、来源、目标版本、校验状态 |
| `ManageStandardPackage` | FILE | 主机 | 八版 | `import`（目录或 zip → 数据目录，先校验）、`export`（→ 新 zip）、`remove`；默认预览 |
| `ValidateStandardPackage` | OFFLINE | 主机 | 八版 | schema、交叉引用、哈希、各目标版本的实现覆盖、规则确定性、生成名称自检 |
| `DescribeStandardPackage` | READ | 主机 | 八版 | 设备类型、参数 schema、信号、命名示例、版本覆盖；供 AI 整理机器描述 |
| `ManageMachineDescription` | FILE | 主机 | 八版 | `validate`、`import`（xlsx/CSV → JSON）、`exportTemplate`（按包生成空模板） |
| `PlanProjectGeneration` | READ | 主机，读回经 worker | 八版 | 输入包引用、机器描述、目标（新建/已有工程、`softwarePath` 绑定）；输出计划 |
| `ApplyGenerationPlan` | WRITE | 主机编排 | 八版（步骤按版本可用性） | 执行计划或选定阶段；一次审批；失败即停 |
| `GetGenerationRun` | READ | 主机 | 八版 | 运行状态、逐步结果、冲突、未执行项、审计 requestId |
| `CheckProjectStandard` | READ（写报告时 FILE） | 主机，读回与导出经 worker | 八版（检查项按版本） | 发现列表与报告；可选导出 Test Suite 规则集 |

全部使用 V4 信封；`GetToolUsage` 增加各工具示例与 `sequence/project-generation`。lite 目录建议收 `DescribeStandardPackage`、`PlanProjectGeneration`、`ApplyGenerationPlan`、`CheckProjectStandard`。

### 6.3 计划结构

```json
{
  "schema": "tiamcp.plan/1",
  "planId": "…", "planHash": "sha256:…",
  "package": { "id": "tiamcp.basic", "version": "1.0.0", "hash": "sha256:…" },
  "machineHash": "sha256:…",
  "target": { "release": "21", "projectIdentity": "…", "mode": "existing" },
  "phases": ["project", "hardware", "network", "plcStructure", "plcTypes", "plcLibrary",
             "plcProgram", "plcTags", "alarms", "hmi", "compile", "save"],
  "steps": [
    { "id": "s042", "phase": "plcProgram", "key": "PLC1/block/IDB_U01-M101",
      "op": "create", "tool": "ImportPlcExternalSource", "arguments": { "…": "…" },
      "dependsOn": ["s031"], "argumentDigest": "sha256:…",
      "availability": { "tool": "present", "behaviorPolicy": "current", "native": "NOT RUN" },
      "expect": { "readback": "ListPlcBlocks", "contains": "IDB_U01-M101" } }
  ],
  "skipped": [ { "key": "PLC1/block/FB_MotorDol", "reason": "exists-identical" } ],
  "conflicts": [ { "key": "PLC1/tag/U01-M102_RUN", "reason": "exists-different", "detail": "…" } ],
  "unavailable": [ { "phase": "hmi", "reason": "release 17 has no Unified HMI tools" } ],
  "artifacts": [ { "path": "staging/<planId>/U01_Calls.scl", "sha256": "…" } ],
  "selfCheck": { "errors": 0, "warnings": 0 }
}
```

原则：**计划内参数全部预先确定**。名称、分组路径、设备路径、槽号、地址都由规则确定性给出，不在执行时回填；需要执行后才能知道的值（如 TIA 自动分配的编号）不进入后续步骤参数，必要时分两次规划。这样审批看到的就是将执行的全部内容，`argumentDigest` 可逐步核对。

### 6.4 步骤到现有工具的映射

| 计划步骤 | 工具 | 当前可用版本 |
|---|---|---|
| 建/开工程 | `CreateProject`；`OpenProject`/`AttachOpenProject` | 八版 |
| 备份（已有工程默认） | `SaveProjectCopy` 或 `ArchiveSavedProject` | 20/21（B8） |
| 建站 | `CreateDevice`、`CreateHardwareCatalogDevice`；多站时 `BuildDeviceAmlDocument` + `ImportDeviceAml`；将来 `DeviceComposition.CreateFrom(MasterCopy)`（新） | 20/21；V19 仅 `CreateHardwareDevice` 两个 CPU |
| 插模块、地址 | `PlugDeviceItem`（显式槽号）、`SetDeviceAddress`、`SetDeviceItemIoAddress` | 20/21（B2） |
| 网络 | 先 `PlanHardwareNetworkConfiguration` 离线校验，再 `EnsureSubnet`/`AttachDeviceNodeToSubnet`/`ManageIoSystem` | 20/21（B2） |
| 设备分组、PLC 分组 | `ManageDeviceUserGroup`、`CreatePlcBlockGroup`、`CreatePlcTypeGroup`、`ManagePlcUserGroup` | 20/21（B2/B3） |
| UDT | `BuildPlcUdt` + `ImportPlcType`，或 SCL 源 | 八版 |
| 库类型放置 | 新 `PlaceLibraryType`/`CreateFromMasterCopy`（G3）；UDT 现可用 `ManagePlcDocuments createFromLibraryType` | 新工具随 B6；API 14 SP1+ |
| 标准 FB 与调用 FC、实例 DB | `StageImportFiles` → `ImportPlcExternalSource` → `GenerateBlocksFromExternalSource`；或 `CreatePlcInstanceDb` | 八版；后者 20/21 |
| 变量表与变量 | `BuildPlcTagTable` + `ImportPlcTagTable`，或 `CreatePlcTagTable`/`CreatePlcTag` | 八版 |
| 报警 | 源中的 `Program_Alarm`；`ImportAlarmClasses`、`ManagePlcAlarmTextList`、`ImportPlcAlarmInstanceTexts`；Unified `ManageUnifiedEngineeringObject` | 八版（源）；其余 20/21（B5/B10） |
| HMI | `EnsureUnifiedHmiConnection/TagTable/Tag/Screen`、`ApplyUnifiedHmiScreenDesign`（离线先 `BuildUnifiedHmiTemplateApplyDesign`）、`EnsureUnifiedHmiButtonAction`、`BindUnifiedHmiTagDynamization`；SiVArc 包可用 `GenerateSivarc` | 20/21（B10 起 V19） |
| 编译 | `CompilePlcDiagnostics`；HMI `CompileDevice` | 八版；后者 20/21 |
| 保存 | `SaveProject`（作为计划末步，不自动保存） | 八版 |
| 检查 | `CheckProjectStandard` | 八版（检查项按版本） |

预览中每步标注：该版本目录是否有此工具、行为族状态（`current / NOT RUN` 或已验收）。含不可用步骤的阶段默认整体跳过并在结果中列出，不在执行中途才失败。

### 6.5 幂等与再生成

- 每个期望对象有稳定的幂等键（`站/种类/名称`，名称由规则确定）。规划时读回已有对象：不存在 → `create`；存在且一致 → `skip`；存在但不同 → `conflict`（默认不改，报告差异）。
  “一致”的判定：块与 UDT 用 `GetPlcObjectFingerprints`（Interface/Code/LibraryType 等指纹）与本次产物比较；变量比数据类型与地址；硬件比订货号、固件、槽位与地址；画面比模板哈希记录。
- 生成记录：工程目录旁写 `<工程名>.tiagen.json`（包 id/版本/哈希、机器描述哈希、计划哈希、每个对象的幂等键与生成时指纹、运行 id 与审计 requestId），用于下次规划区分“生成后被人改过”和“从未生成”。不往工程对象的作者、族等字段写标记。
- 续跑：不做单独的断点续跑协议；失败或结果未知后，处理完会话（按现有规则断开或重连）再调用 `PlanProjectGeneration`（已有工程模式），新计划只含缺失步骤。
- 包升级：以新版本包重新规划，差异以 `update` 步骤列出（如库类型版本更新、规则新增对象），同样需审批。

### 6.6 审批与审计

- `ApplyGenerationPlan` 发起**一次**工作台审批：`PendingApproval` 的 `PlanHash` 为计划哈希，`Operations` 列出全部步骤（tool/action/target），`ParametersJson` 放摘要（各阶段对象数、冲突、跳过、不可用阶段、产物哈希）；工作台审批抽屉新增计划视图（差异树 + 步骤表 + 完整计划链接）。
- 批准后，编排器逐步经与直接调用相同的路径派发：每步先做该工具自己的预检，再核对该步 `argumentDigest` 与已批准计划一致，一致则视为已获批，不再逐步弹审批；不一致即拒绝并停止。
- 审计：Apply 本身一条 request（planHash）；每一步照常记 request/start/end，附 `planId`、`stepId` 与 Apply 的 requestId，审计页可按计划聚合。
- 每步派发前复核工程与会话身份（同 `ApplyToolBatch` 的做法）；共享会话被其他客户端改动时停止。
- 关闭审批时照常执行并记录“审批已关闭”。`dryRun` 预览不审批、不计写审计。

### 6.7 部分失败

- 失败即停，余下步骤标 `NOT_EXECUTED`；已执行的写入保留（TIA 没有跨工具事务；`RunToolTransaction` 只覆盖白名单编辑）。
- `OUTCOME_UNKNOWN` 沿用现有规则锁定会话，不自动重放；运行记录给出核对建议和重新规划入口。
- 已有工程模式默认先备份（版本支持时）；新建工程模式失败后可直接丢弃工程。
- 产物（生成的 SCL/XML）在规划时写入暂存目录并记录哈希，执行时只读取这些文件；执行后按现有 `CleanupStagedImportFiles` 规则清理。

### 6.8 工作台模块

页面：标准包（列表、导入导出、版本、校验结果） · 包编辑器（按部分的表单 + JSON 原文 + 实时校验 + 命名预览 + `extends` 合并视图） ·
生成向导（选包 → 机器描述：导入表格/表格编辑/接收 AI 预填 → 目标：新建或已有工程与 TIA 版本 → 计划预览：差异树、步骤、版本可用性、自检 → 提交执行） ·
运行结果（逐步状态、冲突、未执行、链接审计） · 检查报告（按规则/对象分组，导出 HTML/JSON）。
向导的“提交执行”经共享 MCP 会话调用 `ApplyGenerationPlan`（P8-21），不使用工作台自身的 TIA 连接；AI 预填向导经 P8-20 的控制通道，由人确认。

## 7. 分步实施建议与验收

| 任务 | 内容 | 依赖 | 验收 |
|---|---|---|---|
| P8-31a 模型与 schema | 包各部分、机器描述、计划、检查结果的 JSON Schema 2020-12；`TiaMcp.Logic` 中的 C# 模型、加载、规范化 JSON 与哈希 | 本稿决定 | T：schema 正反例、哈希稳定；V |
| P8-31b 规则展开与规划 | 命名引擎、IO/IP/编号分配、规则展开为期望模型、读回比对、依赖排序（复用 `PlanArtifactImportOrder` 逻辑）、版本可用性标注、生成即自检、SCL 调用 FC 生成 | a | T：黄金计划逐字节一致；同输入同哈希；生成名称全部通过自检；V |
| P8-31c `tiamcp.basic` 包 | 第 5.2 节内容与示例、黄金计划 | a | T：包校验通过；SCL 经伴随工具 lint；L5：V17、V19、V21 导入编译 0 错误 |
| P8-31d 包管理与离线工具 | `ListStandardPackages`、`ManageStandardPackage`、`ValidateStandardPackage`、`DescribeStandardPackage`、`ManageMachineDescription`（先 JSON/CSV）；`reference/tool-examples` 示例并生成嵌入目录 | a、b | T、V；八版契约增量逐条评审；八版检索与功能检查 |
| P8-31e 规划与执行 | `PlanProjectGeneration`、`ApplyGenerationPlan`、`GetGenerationRun`；审批协议计划请求、工作台审批抽屉计划视图、逐步审计关联、身份复核、生成记录文件 | b、d | T：故障注入（第 k 步失败、结果未知、身份变化、审批拒绝/超时、参数摘要不符）；两类主机一致性用例；L5：V21 整机生成、V19/V17 PLC 部分 |
| P8-31f 库与主模板实例化工具 | `PlaceLibraryType`（CodeBlock/PlcType LibraryTypeVersion，含改名与 `UpdatePathsMode`）、`CreateFromMasterCopy`（块、变量表、设备、模块），按 Foundation 适配器写法进 F23/F09/F18 | P8-02；B6 | P8-03 门禁 G1–G11 |
| P8-31g 检查引擎 | `CheckProjectStandard`：读回 + 导出 + XPath 规则（复用 `AuditEngineeringExports` 执行器）、库与指纹检查、HTML/JSON（可选 SARIF）报告、可选导出 Test Suite 规则集 | a、b | T：对生成工程 0 错误；对预置违规样例命中预期规则；L5 |
| P8-31h 工作台模块 | 第 6.8 节页面 | P8-20、P8-21、d、e、g | T（视图模型）、V；L5 体验 |
| P8-31i 状态模型包 | 第 5.3 节内容 | b、c | 同 c；20/21 加 PLCSIM Advanced 状态转换场景 |
| P8-31j 文档 | 重写 `docs/guides/project-generation.md`；包编写指南；更新 `templates/README.md`、仓库布局说明 | d、e、g | 链接、路径、工具适用版本与引用核对 |

里程碑：M1 离线闭环（a、b、c、d：包 → 机器描述 → 计划，八版）；M2 执行（e：PLC 部分八版，硬件/HMI/报警 20/21）；M3 检查（g）；M4 工作台（h）；M5 状态模型包（i）；之后随 P8-03 批次逐版开放硬件、分组、库、报警与 HMI 步骤。

框架整体验收（L5，记入真机台账）：在 V21、V19、V17 虚拟机上，用示例机器描述生成 → 编译 0 错误 → `CheckProjectStandard` 0 错误 →
立即重新规划得到 0 个步骤 → 人工改动一个生成对象后重新规划，报告冲突而不覆盖 → 换用状态模型包重新规划，差异符合预期。

## 8. 需要维护者决定的问题

| # | 问题 | 选项 | 推荐 |
|---|---|---|---|
| Q1 | 标准包用什么形态？ | A. 目录 + JSON 清单与分部文件，分发时整目录打 zip，全局库作可选资源 · B. 只用单个 zip 分部包 · C. 以 TIA 全局库为主体，规则作旁注文件 | **A**：可 diff、可离线校验、工作台和 AI 都能编辑，又保留库引用 |
| Q2 | 包和机器描述用什么书写格式？ | A. 只用 JSON（JSON Schema 2020-12） · B. JSON 为准，另收 YAML 便于手写 · C. 只用 YAML | **A**：System.Text.Json 原生、哈希稳定、AI 输出可靠；YAML 以后可作导入格式 |
| Q3 | 生成规则允许多强的表达能力？ | A. 声明式规则 + 受限占位符与内置函数，不执行包内代码 · B. 引入通用模板引擎（Scriban/Liquid，沙箱化） · C. 允许包内脚本（C#/JS） | **A**：第三方包导入后不执行代码，结果确定可测 |
| Q4 | 设备清单输入格式？ | A. 规范 JSON 为唯一生成输入；首版收 JSON（AI/MCP）和由包导出的 xlsx/CSV 模板，表单编辑同一 JSON · B. 只收 JSON，表格和自然语言都由 AI 客户端整理 · C. 以工作台表单为主，暂不收表格 | **A**；若暂不引入 xlsx 依赖，首版只收 CSV，xlsx 由 AI 转 JSON |
| Q5 | 首批标准包？ | A. 中性 `tiamcp.basic` + ISA-88/PackML 风格状态模型包（均自写，basic 用八版通用的 SCL 源） · B. 只做 basic · C. basic + SAF 适配包（用户自备 SAF 库） | **A**：能证明可替换与派生；SAF 待取得资料与许可后作私有包 |
| Q6 | 生成写入如何审批？ | A. 每一步单独审批（现状） · B. 每次 Apply 一次审批，绑定计划哈希与全部步骤（可只选部分阶段），逐步审计 · C. 固定按阶段（硬件/PLC/HMI/保存）各审批一次 | **B**：审批看到的就是全部将执行的内容，步骤参数摘要逐一核对；需要阶段间检查时按阶段分次 Apply |
| Q7 | 执行器放在哪里？ | A. Foundation 主机内的生成编排器，八版共用，逐步走现有工具的预检、审批与审计 · B. 只提供 Plan，由 AI 客户端逐个调用工具 · C. 复用 20/21 的 `PreviewToolBatch`/`ApplyToolBatch` | **A**：八版一致、可做计划级审批与身份复核；C 只有 20/21 且限 50 步 |
| Q8 | 对已有工程再生成时，遇到已存在但不同的对象怎么办？ | A. 只补缺：一致则跳过，不同则报冲突不改 · B. 先备份再覆盖 · C. 删除生成区域后整体重建 | **A**：不破坏人工修改；覆盖可作为以后显式的“更新”操作 |
| Q9 | 生成记录放在哪里？ | A. 工程目录旁的 `.tiagen.json` + 审计关联 · B. 写进工程（常量表、DB 或项目文本） · C. 只靠审计日志 | **A**：不改工程内容，又能支持幂等判定与升级差异 |
| Q10 | 首版覆盖哪些 TIA 版本？ | A. 离线部分（包、机器描述、计划、检查）八版；执行 PLC 部分八版，硬件/HMI/库/报警先 20/21，其余随 P8-03 批次开放 · B. 先只做 V20/V21 · C. 等 P8-03 全部完成再做 | **A**：PLC 程序通道（SCL 外部源、变量、编译）八版已具备 |

另需确认（不必作为选择题）：风格检查后端推荐“自有规则引擎为主，可选导出 TIA Test Suite 规则集”；包在仓库中的位置推荐 `templates/standards/`；
维护者自用编程规范是否作为第一个企业包试点及是否入库。

## 来源（访问日期 2026-10-08）

仓库内：`docs/development/roadmap.md`（阶段 8）、`docs/development/refactor-plan.md`（P8-30/31）、`docs/development/gap-review-2026-10.md`、
`docs/development/phase8-port-plan.md`（第 2、5、7 节）、`docs/guides/project-generation.md`、`templates/`、`manifest/tools-list.json`、
`manifest/contracts/v4/baseline/19.json`、`reference/siemens-openness/skills/global-library/SKILL.md`、`src/Shared/ApprovalProtocol.cs`、
`src/Engine/ModelContextProtocol/Tools/McpServer.Batch.cs`、`McpServer.Approval.cs`、`ProjectSessionTools.cs`、`src/Engine/Siemens/Services/TypesService.cs`；本地 `sdk/TIA_V*_PublicAPI`（不随仓库分发）。

外部：

- 西门子新闻稿，Eigen Engineering Agent 新增 ECAD 集成与符合标准的项目生成（2026-06-17）：https://assets.new.siemens.com/siemens/assets/api/uuid:b244e71e-20e6-4eec-9faf-cda34d0959a5/HQCOPR202606167406EN.pdf
- 西门子标准化机器工程（Automation Framework、标准库）：https://contentpath.siemens.com/standardization/en_machine-engineering
- 应用示例条目《Automation Framework for SIMATIC PLC/HMI and SINAMICS Drives》：https://contentpath.siemens.com/standardization/c/applicationexample-automation-framework
- SIMATIC Modular Application Creator：https://www.siemens.com/global/en/company/stories/industry/factory-automation/modular-application-creator-ab-electric.html ；https://www.parmley-graham.co.uk/content/blog/simatic-modular-application-creator-software-module-generation-for-tia-portal/ ；https://contentpath.siemens.com/efficient_engineering/en_automated-engineering
- PackML 概述：https://en.wikipedia.org/wiki/PackML ；ISA-TR88.00.02-2015 预览：https://www.isa.org/getmedia/300dbd50-d549-41ac-b372-a5e52f32fc97/tr_880002_preview.pdf
- OPC 30050 PackML：https://reference.opcfoundation.org/PackML ；https://reference.opcfoundation.org/specs/OPC-30050/full
- OPC 40001-1 Machinery：https://reference.opcfoundation.org/Machinery
- 西门子 LPMLV30（OMAC PackML V3.0 for S7-1200/1500，条目 49970441）：https://support.industry.siemens.com/cs/ww/en/view/49970441 ；https://cache.industry.siemens.com/dl/files/441/49970441/att_1093215/v1/LPMLV30_SIMATIC_V3_0_en_09_2021.pdf
- 西门子 Packaging Toolbox 新闻稿（2020）：https://assets.new.siemens.com/siemens/assets/api/uuid:c44fe8c9-7bb0-4708-9d6d-f202a3ea3eb5/HQDIPR202005135871EN.pdf
- Weihenstephan Standards：https://opcfoundation.org/markets-collaboration/ws/ ；OPC 40600：https://reference.opcfoundation.org/specs/OPC-40600/1.2.3
- VDI/VDE/NAMUR 2658 Blatt 1：https://vdi.de/en/home/vdi-standards/details/vdivdenamur-2658-blatt-1-automation-engineering-of-modular-systems-in-the-process-industry-general-concept-and-interfaces
- ISA-18.2 报警管理（Yokogawa 转载 Control Engineering 文章）：https://www.yokogawa.com/us/library/resources/media-publications/implementing-alarm-management-per-the-ansi-isa-182-standard-control-engineering/
- ISA-101：https://isa.org/standards-and-publications/isa-standards/isa-101-standards
- IEC 81346-1 预览：https://www.vde-verlag.de/iec-normen/preview-pdf/info_iec81346-1{ed2.0}b.pdf ；参考代号说明：https://myelectrical.com/notes/entryid/24/iec-reference-designations
- PLCopen Coding Guidelines 发布：https://www.controleng.com/plc-coding-guidelines-released/
- 西门子 Programming Styleguide：https://cache.industry.siemens.com/dl/files/084/109478084/att_851379/v1/81318674_Programming_Styleguide_DOCU_v11_en.pdf ；https://cache.industry.siemens.com/dl/files/084/109478084/att_1022098/v1/81318674_Programming_Styleguide_DOC_v20_de.pdf
- 西门子 Programming Guideline：https://cache.industry.siemens.com/dl/files/040/90885040/att_861867/v1/81318674_Programming_guideline_DOCU_v14_en.pdf
- 西门子 HMI Styleguide：https://support.industry.siemens.com/cs/attachments/81318674/81318674_HMI_Styleguide_DOC_v10_en.pdf ；WinCC Unified Engineering Guideline：https://support.industry.siemens.com/cs/attachments/109827603/109827603_WinCC_Unified_engineering_guideline_DOC_V1_en.pdf ；HMI Template Suite Unified：https://cache.industry.siemens.com/dl/files/767/91174767/att_1106605/v2/Final_91174767_HMITemplateSuiteUnified_V31_DOC_en.pdf ；Unified JS Styleguide：https://cache.industry.siemens.com/dl/files/536/109758536/att_1029277/v4/109758536_Unified_JS_Styleguide_V10_de.pdf
- TIA Portal Test Suite：https://cache.industry.siemens.com/dl/files/099/109793099/att_1066552/v1/TIAPortal_TestSuiteAdvanced.pdf ；https://blog.electro-matic.com/increase-serviceability-of-programming-code-with-tia-test-suite
- 西门子 LGF 文档（应用示例法律条款）：https://cache.industry.siemens.com/dl/files/728/109479728/att_1019474/v3/109479728_LGF_TIAV16_DOC_V5_0_0_en.pdf
- VASS 6（SITRAIN）：https://www.sitrain-learning.siemens.com/zh/rw57945/VASS-6-OEM-Workshop-for-Beginners ；SICAR in TIA Portal（SITRAIN）：https://www.sitrain-learning.siemens.com/zh/rw32785/Online-Training-SICAR-in-TIA-Portal
- siemens/tia-portal-ai-extensions：https://github.com/siemens/tia-portal-ai-extensions

## 维护者决定（2026-10-08）

- 首批标准包：中性的基础包（由现有模板整理，八个版本通用）加一个按 ISA-88/PackML 公开状态模型自写的状态模型包（不声明认证）。
  SAF、LPMLV30、LGF、VASS、SICAR 等专有标准不入库，只能由用户自备后作为外部引用或私有包。
- 生成写入每次执行审批一次：审批绑定计划哈希与全部步骤，之后逐步执行、逐步审计，失败即停。
- 按推荐执行的其余各项：标准包为“目录 + JSON 清单与分部文件”，以 zip 分发，只用 JSON（JSON Schema 2020-12 校验）；规则为声明式加受限占位符，
  不执行包内代码；设备清单以规范 JSON 为唯一生成输入，另收由标准包导出的 xlsx/CSV 模板；执行器为 Foundation 主机内的编排器；
  已有工程遇到不同对象只补缺并报告冲突；生成记录写在工程目录旁的 `.tiagen.json` 并进审计；首版离线部分与 PLC 执行覆盖八个版本，
  其余能力随 P8-03 各批开放；风格检查以自有规则引擎为主，可选导出 TIA Test Suite 规则集；仓库内的包放在 `templates/standards/`。
- 维护者自用的编程规范是否作为第一个企业包试点、是否入库：待维护者另行决定。
