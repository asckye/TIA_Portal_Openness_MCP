# 路线图与待办（2026-09-17 审计，2.7.27 更新）

[文档目录](../README.md) · [能力与验收边界](../reference/capabilities.md) · [Openness 限制](../troubleshooting/openness-limitations.md)

本页来自 2026-09-17 对仓库结构、西门子官方 V21 在线文档（发布日期 03/2026）及第三方生态的一次系统审计，并在同日的 2.7.18 发布后更新状态。分五部分：引擎源码待办状态、官方 API 缺口优先级、第三方工具集成候选、合规事项、已完成项。所有条目均附证据位置；未核实的明确标注。

## 1. 引擎源码待办 —— 2.7.18 状态

本机放置 PublicAPI 后引擎可编译，以下项目已在 2.7.18 处理；未处理的注明原因。

| # | 项目 | 状态 |
|---|---|---|
| E1 | 14 个大写域名标签 | 已统一为 PascalCase；分类体系见 `ToolTaxonomy` |
| E2 | `RunV2PlanCompletionAudit` 死工具 | 已移除工具、审计器与 CLI 标志 |
| E3 | Doctor 提示指向不存在的脚本 | 已改为 `TiaMcpServer.exe doctor` |
| E4 | `McpGuides.cs` 中不存在的 `ImportBlocksFromScl` 别名 | 已改为 `ImportBlocksFromDocuments` / `ImportFromDocuments` |
| E5 | 源码硬编码 `D:\app\TIA21` 路径 | 改为 `Engineering.ProbeOpennessAssemblies()` 运行时解析；V20 csproj 的 `TiaPortalLocation` 加 `Condition` 可被覆盖 |
| E6 | `ProgrammingLanguage.ST` 兼容 | 核实无需改动：所有用法为 `ToString` / `Enum.GetName` |
| E7 | 下载提示应答缺陷 | 已修复：`DownloadPromptPolicy` 按真实形态应答 43 种提示，未应答项回传 |
| E8 | csproj 死项 / 命名 | 死 `ItemGroup` 已删除；2.7.19 完成重命名 `TiaMcpServer.V21.csproj` / `TiaMcpServer.HttpTests.csproj`（程序集名不变） |
| E9 | 工具矩阵未纳入发布流程 | 已纳入：`Build-Release.ps1` 生成清单后立即重建矩阵 |
| E10 | 日志写在 EXE 旁 | **保留**：`%TEMP%` 已有副本，EXE 旁的 `startup.log` 便于用户就地查看且已 gitignore |
| E11 | `.cs` BOM 不一致 / `.gitattributes` | 已做（2.7.18 目录整理提交）：全部 `.cs` 无 BOM UTF-8 + LF，`.gitattributes` 固定行尾 |

## 2. 官方 Openness API 缺口优先级

基线：[能力与验收边界](../reference/capabilities.md)"尚未完成"清单 + [v2.7.14 覆盖审计](../archive/openness-audit-v2.7.14.md)。2026-09-17 对照官方 V21 在线目录（698 个条目）刷新，并用本机 V21 PublicAPI XML 做了逐成员词法盘点（[官方 API 覆盖清单](../reference/openness-coverage.md)：4,490 个领域成员，2.7.17 时方法已引用 169/1,239、类型完全未触及 1,030/1,217，2.7.18 后成员已引用 609、类型有专用引用 208——口径与低估原因见该页）。结论：**无 V22**；V21 Update 1/2 不新增 Openness API；下一次核对点为 SPS（11 月）。

### 2.0 官方 API 全量对齐计划（2.7.25 起）

目标：把官方 Openness PublicAPI 的全部功能类型都封装成带校验、预览与文档的专用工具（通用反射入口 `DescribeObject` / `InvokeObject` 早已能到达一切，但没有校验与文档）。按收益从高到低分阶段做，每阶段一个或多个发布；口径以 [官方 API 覆盖清单](../reference/openness-coverage.md#缺口结构2724) 的"未封装功能类型"为准（去掉集合/结果壳子与动态覆盖的 Unified 事件/部件）。

| 阶段 | 程序集 / 领域 | 起点缺口（2.7.24） | 状态 |
|---|---|---|---|
| 1 | **Safety**（`Siemens.Engineering.Safety`） | 10 类型 / 41 成员 | **2.7.25 完成**：12 类型 / 54 成员全部有专用工具（`ManagePlcSafety` 12 个动作、`ManageSafetyGlobalSettings`、`ReadSafetyBlockSignatures`、`ExportSafetyPrintout`、下载提示 `SafetyProgram`）。剩余仅 `SafetyValidation` 选件（阶段 6） |
| 2 | **WinCC Unified**（`Siemens.Engineering.HmiUnified`） | 113 类型 / 685 成员 | **进行中**。① 画面对象族 `UI.Shapes` / `Widgets` / `Controls` / `Base` / `Features` / `HmiScreenWindow`（69 类型）——**2.7.26 完成**：`DescribeUnifiedScreenItemType` + `ManageUnifiedScreenItem`（反射自官方 API 的类型目录与属性 schema，任意类型 `Create<T>`；硬编码属性表在 43 个类型 × 数十属性下不可维护，且与 V20/V21 差异冲突，故走"反射 + 形状检查 + 登记表"）；② 报警类 / 离散 / 模拟报警 `HmiAlarm`（3 类型 / 26 成员，含报警类四种状态视觉的颜色写入）；③ 报警/数据记录与备份/分段设置、审计类 `HmiLogging` / `HmiAudit`（8 / 23）；④ 运行时设置嵌套对象 `RuntimeSettings`（10 / 58）——**2.7.27 真机验证**：`ReadUnifiedObjectProperties` / `UpdateUnifiedObjectProperties` 路径 `[{property:RuntimeSettings},{property:OpcUaServerRuntimeSettings}]` 全部标量可读可写，已登记动态覆盖；⑤ 阈值（`HmiThresholdComposition.Create`）/ 替代值 / 系统变量、记录变量、文本/图形列表、`Cpm`、`OpcUaAlarm.Import`、变量与脚本模块的 xlsx/js 导入导出（`HmiTagComposition` / `HmiScriptModuleComposition` `Export/Import`）。②–⑤ 多数已可经通用属性路径工具到达，先在真机逐族验证、补缺口、再登记为动态覆盖 |
| 3 | **Base 硬件深层与库**（`Siemens.Engineering.HW` / `Library` / `Umac` / `Security` / `Compare` / `CrossReference`） | 85 类型 / 310 成员 | `TransferArea` / `MrpInstance` / `SyncDomain` / `IoSystem` / `Channel` / `WebserverUser` / `DeviceGroup`；`GlobalLibrary` 与类型版本文件夹 / `UpdateCheck`；`UmcUser*`；`SyslogServer` / `CertificateTemplate` / `PlcPasswordPolicyService`；比较结果元素；跨引用 `SourceObject` / `ReferenceObject` |
| 4 | **Step7**（`Siemens.Engineering.SW`） | 41 类型 / 136 成员 | 软件单元 `SW.Units` 27、`PlcDocument*`、`PlcChecksumProvider` / `PlcSimulationSettingsProvider`、`PlcBlockWriteProtectionProvider`、报警类导入导出结果、`OpcUaCommunicationGroup`；`PlcForceTableEntry` 保持刻意不注册 |
| 5 | **经典 WinCC**（`Siemens.Engineering.Hmi`） | 24 类型 / 59 成员 | 弹出/滑入画面与模板的文件夹层次 23、变量文件夹 |
| 6 | **选件包** | 108 类型 / 522 成员 | Startdrive `MC.Drives` 22 + `DFI` 16、DCC 图表 62（39 个异常类）、SiVArc 65、Test Suite 16、SafetyValidation 14、Teamcenter 12——本机无这些选件时只能做形状检查，不能真机验证 |

每阶段的固定动作：对照官方在线文档章节逐页实现 → 纯逻辑离线测试 → 引擎 API 形状检查（`HttpTests engineering-api-only`，逐成员核对 V20/V21 PublicAPI）→ 重跑 `Audit-OpennessCoverage.ps1` 回写覆盖清单 → 有真机条件的在 V21 工程上跑一遍。

### 2.1 高价值且官方 API 存在——优先实现

| 优先级 | 能力 | 官方入口 | 价值说明 |
|---|---|---|---|
| P1 | **下载配置族**：先修 E7（`UserManagementDownload` 缺陷 + 27 种未应答提示），再补下载到 Windows 文件夹生成存储卡镜像（可指向 PLCSIM Advanced）、`StationUpload` | [Downloading PLC to a Windows folder](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/downloading-plc-to-a-windows-folder) | CI 式出卡、不擦保持值的下载——调试最常见痛点；镜像可直接喂 PLCSIM Advanced |
| P1 | **DB 快照 / 实际值往返**：`CreateSnapshot`、`LoadSnapshotAsActualValues`、`LoadStartValuesAsActualValues`、`InterfaceSnapshot.Export` | [Accessing Data blocks](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/accessing-data-blocks) | 下载前后保留配方/设定值 |
| P1 | **在线可达设备扫描 + 站上载**：`GetAccessibleDevices()`、`StationUpload` | [Accessing accessible devices](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-configuration-accessible-devices)、[Uploading PLC device](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/uploading-plc-device) | 棕地"PLC 里到底是什么"工作流 |
| P2 | **Unified 动态化/事件补全**：Dynamization（变量/公式/闪烁/资源）、ScriptDynamization + SyntaxCheck、PropertyEventHandlers、FaceplateContainer UDT 分配 | Unified 目录 Screens 子章节 | AI 生成画面的核心；每个子页官方均有 |
| P2 | **PLC 报警/多语言文本**：`ImportInstanceTextsFromXlsx`、PLC 对象多语言标题/注释、按类别过滤的设备级工程文本导出 | 项目数据章节 | 多语言工程必需；导出半边已有 |
| P2 | **OPC UA 服务器配置**：引用命名空间（含节点生成/重命名）、用户/角色访问控制、服务器接口导入导出、客户端证书 | "Functions on OPC" 章节 | IT/OT 集成 |
| P2 | **块保护**：`PlcBlockProtectionProvider.Protect/Unprotect`、`GetInvalidPasswordCharacters` | [Setting and removing protections](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/setting-and-removing-protections-from-a-block) | 交付前 IP 保护；需单独设计密码处理 |
| P2 | **库生命周期**：从库协调工程、更新库、清理、比较、过期实例、DoNotUse、主副本中的报警文本列表 | "Functions on libraries" 章节 | 标准化工作是 AI 杠杆最大的地方 |
| P3 | ProDiag 创建（FB、监督项、关联 DB/标签、报警导出） | 块章节 | 机器制造商 |
| P3 | 监视/强制表定义 CRUD + `WatchAndForceTableAccessManager` | PLC 服务章节 | 补全现有导入 |
| P3 | 通信连接（S7/ISO/TCP/UDP…）、CiR、共享设备、I-Device GSD 导出、批量硬件参数、App ID | V21 What's-new；**章节页未定位到，先以本地 XML 核实** | 多 PLC 产线 |
| P3 | UMAC/UMC 与工程保护；多用户/Project Server 会话、提交、锁状态 | 各自章节 | 合规与团队协作 |

### 2.2 高价值但 Openness 无 API——保持明确拒绝并给出替代通道

独立 RUN/STOP、清除强制、诊断缓冲区、按块选择性下载、LED/模块健康。2026-09-17 逐条对照官方文档确认仍不支持（RUN/STOP 只能作为下载配置的 `StopModules`/`StartModules` 附带发生）。替代：OPC UA、S7 协议库、PLC Web 服务器 API、PLCSIM Advanced API。

## 3. 第三方工具集成候选

按价值排序；许可证以 2026-09-17 各仓库/NuGet 声明为准。**GPL/AGPL 代码不得进入本 MIT 项目**；LGPL 只能独立进程或动态链接。

| # | 候选 | 增加什么 | 集成方式 | 许可证 | 工作量 | 风险 |
|---|---|---|---|---|---|---|
| 1 | **PLCSIM Advanced .NET API**（`Siemens.Simatic.Simulation.Runtime`） | 闭环测试：编译 → 下载到仿真 CPU → 写输入 → 步进周期 → 断言输出。目前只有闭源商业竞品有此能力。 | 与 Openness 相同的延迟绑定解析（DLL 不可再分发） | Siemens；DLL 随 PLCSIM Adv 安装 | 高 | 版本漂移；需 PLCSIM Adv 许可 |
| 2 | **`Siemens.Simatic.S7.Webserver.API`**（NuGet 3.3.76，net48 + netstandard2.0，官方，[GitHub](https://github.com/siemens/simatic-s7-webserver-api)） | 第二在线通道：HTTPS/JSON-RPC 读写变量、诊断、文件/Web 应用部署；覆盖 Sharp7 做不到的 **S7-1500 仅安全通信（TLS）** 场景；支持 PLCSIM | NuGet 依赖 + 新工具族 | MIT | 低-中 | 引入 Newtonsoft.Json；PLC 需开启 Web 服务器 |
| 3 | **Czarnak/tia-git-addin** 的 SimaticML 结构化 diff（[GitHub](https://github.com/Czarnak/tia-git-addin)） | LAD/SCL "改了什么"的结构化差异，服务 VCI 工作流 | 借用 diff 逻辑 | MIT | 中 | 小项目（13 星）；V21 XML 假设 |
| 4 | **AutoPLC** SCL 知识库与 914 任务基准（[GitHub](https://github.com/cangkui/AutoPLC)） | 西门子 SCL API 库、标注案例，用于调优/回归 AI skill | 数据与提示词，不引入代码 | MIT（逐案例核实来源：OSCAT 为 LGPL、LGF 为 Siemens） | 中 | 内嵌示例的数据许可 |
| 5 | **Agents4PLC** 可验证 ST 基准（[HF 数据集 v2](https://huggingface.co/datasets/Luoji-zju/Agents4PLC_dataset_v2)） | 生成代码质量的 CI 回归 | 数据 | Apache-2.0 | 低 | ST 非 SCL，需方言转换 |
| 6 | **Aml.Engine**（NuGet 4.5.2，MIT 二进制） | 程序化生成/校验 CAx 硬件导入用的 AML：`描述机架 → AML → CaxProvider.Import` | NuGet 依赖 | MIT（源码仅会员） | 中 | 需自行编写 TIA 角色类库 |
| 7 | **TIA Test Suite Advanced 经 Openness** | `run_style_guide_check`、`run_application_test`（有许可时） | 纯 Openness 代码 | Openness | 低 | 付费许可门控，无许可无法测试 |
| 8 | **tree-sitter-iec61131-3-st** + **plc-st-review**（均 MIT） | 导入前的离线 SCL 语法/规范预检 | tree-sitter C# 绑定 + 西门子方言补丁 / Node 子进程 | MIT | 中 / 低 | 方言差异；运行时需 Node |
| 9 | **core-engineering/siemens-plc-tools**（MIT，Python） | 从 V21 导出生成文档（MkDocs/Draw.io）、语义 diff、交叉引用 | 子进程 | MIT | 低 | 5 星、4 个月，成熟度未知 |
| 10 | 文档参考包：Repsay 的 Python 客户端、Siemens 官方 code-snippets、Siemens Open Library（Unified 面板）、Unified-JS-Pro（Unlicense）、TIA-Add-In-ShowScripts、awesome-structured-text | 生态地图；JS 片段种子 | 仅文档链接 | 混合（MIT/Unlicense/Siemens 免版税） | 低 | 链接维护 |

竞品对照（截至 2026-09-17）：Czarnak/tia-portal-mcp（59 星，MIT，预览-应用安全令牌 + 审计日志）、heilingbrunner/vscode-tiaportal-mcp（55 星）、chewcw/tia-portal-openness-mcpserver（37 星，**无许可证**，有 MCP elicitation/sampling）、a4webdev/tiacommander-mcp（闭源，独立 S7 通道 + 事务回滚 + 孤儿 IDB 分析）、feelautom/T-IA Connect（商业，唯一带 PLCSIM Adv 工具）。本项目差异化：350 工具广度、V20+V21 双运行时、WPF 配置器、虚拟机/宿主机分离、Unified 深度、CLI 蓝图。值得吸收的模式：预览-应用令牌、审计日志、独立 S7 在线通道。

**许可证红线（不得引入代码）**：rickgaiser/TiaMcp（AGPL-3.0）、TUM-AIS/IEC611313ANTLRParser（GPL-3.0，唯一的 SCL 专用语法，只能参考设计）、Parozzz/TiaUtilities（GPL-3.0）、node-red-contrib-s7（GPL-3.0）、DotNetSiemensPLCToolBoxLibrary（LGPL-2.1，仅动态链接）、iec-checker / rusty（LGPL-3.0，仅子进程）、OPC Foundation UA-.NETStandard **源码**（非会员 GPL-2.0；NuGet 二进制可用）。

### 3.1 补充扫描新增（中文生态、仿真、Unified 运行时、测试、文档工具）

| # | 候选 | 增加什么 | 许可证 | 工作量 |
|---|---|---|---|---|
| S1 | **WinCC Unified Open Pipe**（官方命名管道 `\\.\pipe\HmiRuntime`，JSON 协议，[手册 109803794](https://support.industry.siemens.com/cs/attachments/109803794/WinCCRTUOpPenUS_en-US.pdf)） | 运行时读写 Unified 变量、报警，**不需要 ODK 许可**；用 .NET `NamedPipeClientStream` 即可，打通"工程侧 Openness → 运行时侧"闭环 | Siemens 官方接口，随 Unified RT 免费 | 低-中（需 Unified RT 测试环境） |
| S2 | **Lorenz-Software/PLCSIM.UnitTest**（[GitHub](https://github.com/Lorenz-Software/PLCSIM.UnitTest)） | 在 PLCSIM Advanced 上跑 TIA 工程单元测试；多版本 Openness/PLCSIM 插件加载 + CLI Runner，可直接借鉴并封装为 `run_plc_unit_tests` | MIT | 中（需 PLCSIM Adv 许可验证；目前只到 V18/Adv 6.0，需升级） |
| S3 | **cmariusz/TiaImportExport.VSExt + TIA Viewer**（[GitHub](https://github.com/cmariusz/TiaImportExport.VSExt)） | SimaticML LAD/FBD/GRAPH → HTML 渲染器（本项目缺的"块可视化预览"）；另有 26 个 Copilot LM Tools 可对照工具设计 | MIT | 中（渲染器为 TS，子进程或移植） |
| S4 | **movioli/TIA_ProjectVersionManager** 的语义 diff（[GitHub](https://github.com/movioli/TIA_ProjectVersionManager)） | 剥离时间戳/UID 的 Myers diff，可做 `compare_blocks` / 变更影响分析 | MIT | 低 |
| S5 | **TIA Selection Tool → CAx AML 导入**（官方 [109748223](https://support.industry.siemens.com/cs/attachments/109748223/109748223_TST_to_TIA_Portal_v20_en.pdf)） | 用 TST 导出的 AML 经 `CaxProvider.Import` 生成硬件/网络/拓扑；与 Aml.Engine 候选配套 | 官方 | 低（本项目已有 `ExportDeviceAml`，补导入半边） |
| S6 | **TIA Openness Explorer**（官方 SIOS 109760816，支持 V21） | 对象树浏览 Openness 属性/方法，开发新工具前验证属性路径 | 官方免费 | 无需集成，写入开发指南 |
| S7 | **Czarnak/totally-integrated-claude**（[GitHub](https://github.com/Czarnak/totally-integrated-claude)） | Skill 路由 + `tia-write-guard.ps1` 写保护钩子，可移植到本项目 SKILL.md 与 hooks | MIT | 低 |
| S8 | `Czarnak/tia-todo`、`d-ani/SCL-UnitTesting`、`Bigcheese18/ai-botu` | TODO 扫描工具思路；SCL 自测试块模板可放入 `templates/plc`；后者的 12 种 LAD 配方 XML 细节（BOM、Wire 引用、并联分支）可对照本项目 LAD 生成 | MIT | 低 |

**互补但不可并入（GPL/AGPL/无许可证）**：`vogler75/winccua-mcp-server`（GPL-3.0，基于 GraphQL 的 Unified **运行时** MCP，可在 README 作为搭配推荐）、`mking2203/CodeGeneratorOpenness`（GPL-3.0，2023 停更）、`Codyte/Tia-Portal-CLI`（AGPL + 商业）、`huahaizo/tia-portal-openness-ai`（65 星但无 LICENSE）、Gitee `TIAOpennessTools`（无许可证）。Factory I/O SDK 为 MS-PL，只能作二进制依赖。

**官方竞品（无公开 API/MCP）**：Siemens Engineering Copilot for TIA（Azure OpenAI 桥接）、Eigen Engineering Agent（2026-04，DEX 订阅）。本项目"开源、本地、任意 MCP 客户端、V20+V21"是明确差异点。

**明确核实为不存在的**：TIA 版 S7-PLCSIM（非 Advanced）没有任何编程 API——PLCSIM Advanced 是唯一仿真通道；独立 GSDML 解析库；第三方 Unified 画面生成器/面板市场；GitHub 上的 SiVArc 规则库；西门子 PLC 代码覆盖率工具；Hugging Face 上的 SCL 数据集。

依赖健康度：Sharp7 仓库 2023 年后冻结但稳定（MIT，协议未变），暂不更换；Workstation.UaClient NuGet 自 2024-02 无发布、处于维护模式，若需证书存储管理/反向连接/复杂 ExtensionObject 方法调用，迁移到 OPC Foundation NuGet（不引源码）。

## 4. 合规事项（需维护者决策）

`runtime/v20|v21` 随包分发的 6 个 `Siemens.Collaboration.Net.*` DLL 适用包内的"Siemens 免版税软件条款"，其目标码授权为**不可再许可、不可转让**，第 1.1 条限制分发；MIT 仅覆盖源码。详见 [第三方组件许可证清单](../licenses/THIRD-PARTY-NOTICES.md)。可选处理：保留并在 NOTICE 明示（已做）；从交付包剔除、改由安装步骤 NuGet 还原；或向 Siemens 确认。


## 5. 2.7.27 已完成

- 引擎：2.7.26 画面对象族真机重跑；修复创建后代理身份核对（`ManageUnifiedScreenItem` / `ManageUnifiedScreenLayout`）、部件内多语言文本、`UpdateUnifiedObjectProperties` 的颜色与嵌套部件写入。运行时设置子对象（子批次 ④）真机验证可读可写，登记为动态覆盖。

## 5.1 2.7.26 已完成

- 引擎：WinCC Unified 画面对象族（§2.0 阶段 2 子批次 ①）：`DescribeUnifiedScreenItemType`、`ManageUnifiedScreenItem`；隐藏 `EventHandlers` 的四种对象类型的歧义修复。审计脚本新增动态覆盖登记表与"动态覆盖"栏（完全未触及 885 → 626）。离线 1402、形状检查 936 / 1032。

## 5.2 2.7.25 已完成

- 引擎：`Siemens.Engineering.Safety` 全量封装（§2.0 阶段 1）。`ManagePlcSafety` 强类型重写并补齐嵌套对象、文档化属性、集体签名（此前从未真正读出）、F-I/O 状态块生成、系统对象清理、F-BaseID、登录/登出/密码设置与撤销；新增 `ManageSafetyGlobalSettings`、`ReadSafetyBlockSignatures`、`ExportSafetyPrintout`；下载提示 `SafetyProgram`。离线 1353、形状检查 886 / 982。

## 5.3 2.7.20–2.7.24 已完成

- 引擎：`DescribeBlockLogic` 在真实 V21 工程上暴露的渲染丢失全部修复并真机回归（SCL 调用 / 命名常量 / 绝对地址 / 数组下标 / 位切片 / `ENO`，LAD `<Call>` 调用框 / 用户命名引脚 / `Not` / EN 链）；`RenderPlcBlockDocument` / `ComparePlcBlockDocuments` / `GeneratePlcDocumentation` 同族修复。SCL 渲染改为按 `SW.PlcBlocks.Access_v5.xsd` 的全部分支实现。
- 配置器：重做为单页双栏连接控制台（虚拟机 ↔ 宿主机 / 同一台电脑），客户端卡片按 CLI 在前排列并加入通义千问 / Kimi / 腾讯元宝 / DeepSeek / 智谱清言 / Grok（写各家官方 CLI 或 OpenCode）。
- 覆盖盘点重跑（2.7.24）：API 封装面较 2.7.18 仅 +12 成员 / +4 类型；去掉壳子类型与动态覆盖后，真实缺口 381 个功能类型 / 1,753 个成员，见[覆盖清单](../reference/openness-coverage.md#缺口结构2724)。据此把 **Safety** 提为第一阶段（§2.0），2.7.25 完成。

## 5.4 2.7.19 已完成

- 引擎：E8、E11 完成，全部 11 项引擎待办处理完毕（E10 保留为设计决定）。新增 9 个工具（共 359）：PLCSIM Advanced 通道 5 个（#1 与 S2 的场景式单元测试，反射后期绑定，**未在真实 PLCSIM Advanced 上验证**）、离线文档 2 个（S3 的块可视化，自研 Markdown/Mermaid 渲染，不移植 TS 渲染器）、SCL 预检 1 个（#8，自研启发式规则，不引入 tree-sitter/Node）、AML 生成 1 个（#6 的生成半边，不引入 Aml.Engine，配合已有 `ImportDeviceAml`）。
- 程序文档（#9）由 `GeneratePlcDocumentation` 覆盖（索引、调用交叉引用、逐块渲染）。写保护钩子与审计日志（S7 及竞品的审计模式）随插件交付。
- §3 候选中仍未落地：AutoPLC / Agents4PLC 数据（#4/#5，skill 调优素材，只在 [生态与参考资源](../reference/ecosystem.md) 登记）；`Siemens.Collaboration.Net.*` 再分发决策（§4）仍待维护者。
- 分类修正：6 个运行时/上载写入工具改为 ONLINE-WRITE。

## 5.5 2.7.18 已完成

- 引擎：E1–E5、E7、E9 处理完毕；新增 52 个官方 Openness 工具、8 个运行时通道工具、3 个离线分析工具与 `ListToolCategories`，共 350 个工具；V20/V21 重建，离线 1158、形状检查 847/758、实际 EXE 回归两版全过；**真实工程验收未执行**。
- §2.1 中的 P1（下载提示、设备上载/扫描、DB 快照、文件夹下载）与大部分 P2（块保护、`UpdateProgram`、OPC UA 访问控制、UMAC 读写、库/工程比较、报警文本导入、Unified 事件/部件/动态化、通信连接、监视/强制表 Web 访问）已实现；P3 的 ProDiag 对象、多用户会话、Motion 对象模型、经典 HMI 脚本亦已实现。仍未做：SafetyValidation、Teamcenter、Startdrive/SiVArc/DCC 剩余动作、UMC 同步与工程保护启停、硬件杂项（App ID、批量参数、PSC、Logo、CiR、共享设备、I-Device GSD 导出）、库实例清理/更新流程。
- §3 候选：已落地 —— `Siemens.Simatic.S7.Webserver.API`（#2）、WinCC Unified Open Pipe（S1）、语义 diff（#3/S4，自研实现）、TST/CaX AML 导入（S5，`ImportDeviceAml`）、TODO 扫描与 SCL 自测试模板（S8）、Test Suite（#7，既有工具）。未落地 —— PLCSIM Advanced API（#1，本机无 DLL，需 PLCSIM Adv 环境）、PLCSIM.UnitTest（S2，同上）、TIA Viewer 渲染器（S3，TS 移植量大）、AutoPLC/Agents4PLC 数据（#4/#5，属 skill 调优）、Aml.Engine（#6，AML 生成侧）、tree-sitter/plc-st-review 预检（#8）、siemens-plc-tools（#9）、写保护 hook（S7）。
- 仓库整理与审计（2.7.17 之后、2.7.18 之前）：删除过期 `手册/`、5 个 `_deprecated` PLC JSON 模板、被取代的 `Generate-ToolsList.py`；`design-qa.md` 归档；修复 14 处过期引用；`openness-limitations.md` 更正发现扫描断言；补齐第三方许可证清单与原文；`.gitignore` 增加 TIA 工程扩展名与本机 PublicAPI 目录；`plugin.json` 内联 MCP 配置并删除根 `.mcp.json`。详见 CHANGELOG。
