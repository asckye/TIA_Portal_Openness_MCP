# 路线图与待办（2026-09-17 审计）

[文档目录](../README.md) · [能力与验收边界](../reference/capabilities.md) · [Openness 限制](../troubleshooting/openness-limitations.md)

本页来自 2026-09-17 对仓库结构、西门子官方 V21 在线文档（发布日期 03/2026）及第三方生态的一次系统审计。分四部分：引擎源码待办（需在装有 TIA PublicAPI 的机器上重建）、官方 API 缺口优先级、第三方工具集成候选、合规事项。所有条目均附证据位置；未核实的明确标注。

## 1. 引擎源码待办（下次在 TIA 机器重建时处理）

引擎编译依赖本机 TIA PublicAPI，且 `manifest/tools-list.json`、`manifest/release-build.json` 由已编译 EXE 反射生成并带哈希。以下改动**不在无 TIA 的机器上做**，避免源码与 `runtime/` 二进制漂移。

| # | 问题 | 位置 | 处理 |
|---|---|---|---|
| E1 | 14 个工具的域名标签用大写（`[L2][PROJECT]`、`[LIBRARY]`、`[HARDWARE]`），与其余 284 个工具的 PascalCase 不一致，导致工具矩阵出现 `Project`/`PROJECT` 等重复分节。`FindTools` 匹配已小写化，**不影响检索**。 | `ModelContextProtocol/McpServer.NativeExchange.cs`（6 处）、`McpServer.SpecializedEngineering.cs`（8 处） | 改为 `Project`/`Library`/`Hardware`，重建后重新生成 tools-list 与矩阵 |
| E2 | 工具 `RunV2PlanCompletionAudit`（含 CLI 标志）审计的文档 `docs/TIA_MCP_常见操作全覆盖方案_V2_二次优化计划.md` 已在 2.7.17 删除；同文件还读取不存在的 `tests/TiaMcpServer.Test`（实际为 `TiaMcpServer.Tests`）和 `reports/offline_release_suite`。工具现在总是在空证据上运行。 | `ModelContextProtocol/V2PlanCompletionAuditor.cs:34,36,37`、`McpServer.PlcSoftware.cs:1219`、`CliOptions.cs:68,430`、`Program.cs:238-240` | 移除该工具、审计器和 CLI 标志（工具数 298→297） |
| E3 | Doctor 提示 "Run scripts/check-environment.ps1"，脚本不存在 | `ModelContextProtocol/McpServer.cs:900` | 改为 `TiaMcpServer.exe doctor` |
| E4 | `GetAuthoringGuide` 运行时返回的文本声称存在 `ImportBlockFromScl` / `ImportBlocksFromScl` 别名，实际未注册 | `ModelContextProtocol/McpGuides.cs:58` | 删除该句；同时让 `scripts/checks/Check-DeadToolReferences.py` 扫描 `McpGuides.cs` 与 `SKILL.md`（当前只扫 `[Description]`） |
| E5 | 源码中残留作者本机绝对路径（CONTRIBUTING 禁止） | `ClassicHmiTemporaryImportPreflightSuite.cs:90-91`、`Program.CliProbes.cs:764`、`TiaMcpServer.V20.csproj:19`（`<TiaPortalLocation>D:\app\TIA20\Portal V20`） | 改为从 `TiaPortalLocation` 环境变量 / `-p:SiemensEngineeringDirectory` 读取，csproj 用 `Condition="'$(TiaPortalLocation)'==''"` 给默认值 |
| E6 | V21 新增 `ProgrammingLanguage.ST`（SIMATIC AX / TIAX 导入块）。块枚举与语言判断必须容忍该值。 | 所有 `ProgrammingLanguage` 的 switch/比较 | 加默认分支，返回 `unsupportedLanguage` 而非抛异常；需本地 XML 核实枚举名 |
| E7 | **已确认缺陷（V20+V21）**：`DownloadToPlc` 委托把 `UserManagementDownload` 当复选框调用 `DownloadConfigSetChecked`，但该类型在两版 PublicAPI 中都只有 `CurrentSelection`（枚举 `UserManagementPreDownloadSelections`：KeepOnlineUserManagementData / UpdateUserManagementDataButKeepOnlinePassword / DownloadAllUserManagementDataResetToProject）；`GetProperty("Checked")` 返回 null，提示未应答，按同文件注释的语义下载会中止。此外 V21 共 43 种具体下载提示类型，委托只应答 16 种；未应答的 27 种：`BlockBindingPassword`、`ModuleReadAccessPassword`、`ModuleWriteAccessPassword`、`PlcMasterSecretPassword`（需密码）、`OverwriteOnMemoryCard`、`SwitchBackupToPrimary`、`ResetModule`、`InitializeMemory`、`LoadIdentificationData`、`ProtectionLevelChanged`、`SelectiveDeleteDownload`、`ExpandDownload`、`WaitOnReboot`、`TurnOffSequence`、`UpgradeTargetDevice`、`DowngradeTargetDevice`、`OverwriteTargetLanguages`、`ReplaceDownloadedData`、`TargetForSoftware`、`DownloadWebApplication`、`UpdateWebApplication`、`DeleteWebApplication`、`OverwriteHmiData`、`FitHmiComponents`、`AcceptDownloadOfUnencryptedSensitiveData`、`StartDriveDownloadCheckConfiguration`。（`DataBlockReinitializationOrKeepActualValues` 已由 `keepActualValues` 参数正确处理，早先文档说未处理是错的。） | `Siemens/Portal.Download.cs:486-572` | ① `UserManagementDownload` 改为按 `CurrentSelection` 设置，默认 `KeepOnlineUserManagementData`，暴露 `userManagementMode` 参数；② 为无需密码的提示补默认应答（记录到响应），密码类提示返回明确的 `unhandledPrompt` 错误并列出类型名，而不是静默中止；③ 用 `scripts/diagnostics/Audit-OpennessCoverage.ps1` 的差集作为回归检查 |
| E8 | 工程文件：`TiaMcpServer.csproj:37-40` 有无对象的 `<Compile Remove="tests\**">` 死项（`src/` 下无 `tests/`）；默认 csproj 实为 V21 但无标记；`HttpTests.csproj` 与目录名 `TiaMcpServer.HttpTests` 不对称。**csproj 在 `Validate-Bundle` 的源码哈希集合内，改动必须伴随重建。** | `TiaMcpServer.csproj`、`tests/TiaMcpServer.HttpTests/HttpTests.csproj` | 删除死项；可选重命名为 `TiaMcpServer.V21.csproj`、`TiaMcpServer.HttpTests.csproj`，同步 `Build-Release.ps1`、`Package-Release.py:62`、`Validate-Bundle.ps1:265` |
| E9 | `Generate-ToolCapabilityMatrix.ps1` 未被发布流程调用，`tool-matrix.md` 可能与 `tools-list.json` 漂移 | `scripts/build/Build-Release.ps1` | 在生成 tools-list 之后调用矩阵生成器 |
| E10 | 引擎日志写在 `runtime/v21/TiaMcpServer.startup.log`（EXE 旁），就地运行时污染交付树 | 日志初始化 | 改为 `%LOCALAPPDATA%\TiaMcpServer\` |
| E11 | 引擎源码 186 个 `.cs` 中 41 个带 BOM、145 个不带；`.gitattributes` 无 `*.cs` 规则 | 全部源码 | 统一为无 BOM UTF-8，`.gitattributes` 增加 `*.cs text eol=lf` |

## 2. 官方 Openness API 缺口优先级

基线：[能力与验收边界](../reference/capabilities.md)"尚未完成"清单 + [v2.7.14 覆盖审计](../archive/openness-audit-v2.7.14.md)。2026-09-17 对照官方 V21 在线目录（698 个条目）刷新，并用本机 V21 PublicAPI XML 做了逐成员词法盘点（[官方 API 覆盖清单](../reference/openness-coverage.md)：4,490 个领域成员，方法已引用 169/1,239，类型完全未触及 1,030/1,217——口径与低估原因见该页）。结论：**无 V22**；V21 Update 1/2 不新增 Openness API；下一次核对点为 SPS（11 月）。

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

竞品对照（截至 2026-09-17）：Czarnak/tia-portal-mcp（59 星，MIT，预览-应用安全令牌 + 审计日志）、heilingbrunner/vscode-tiaportal-mcp（55 星）、chewcw/tia-portal-openness-mcpserver（37 星，**无许可证**，有 MCP elicitation/sampling）、a4webdev/tiacommander-mcp（闭源，独立 S7 通道 + 事务回滚 + 孤儿 IDB 分析）、feelautom/T-IA Connect（商业，唯一带 PLCSIM Adv 工具）。本项目差异化：298 工具广度、V20+V21 双运行时、WPF 配置器、虚拟机/宿主机分离、Unified 深度、CLI 蓝图。值得吸收的模式：预览-应用令牌、审计日志、独立 S7 在线通道。

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

## 5. 已在本次整理中完成（无需重建）

删除过期 `手册/`、5 个 `_deprecated` PLC JSON 模板、被取代的 `Generate-ToolsList.py`；`design-qa.md` 归档；修复 14 处过期引用（bug 模板、蓝图文件名、`SetForceTableEntry`/`ImportBlocksFromScl` 等 AI 可见的死工具名、版本号、CHANGELOG 锚点）；`openness-limitations.md` 更正发现扫描断言并补充 RUN/STOP 细节与新下载配置；`capabilities.md` 追加官方对照新缺口并去除与 release-build 重复的计数；补齐第三方许可证清单与原文；`.gitignore` 增加 TIA 工程扩展名；`plugin.json` 内联 MCP 配置并删除根 `.mcp.json`。详见 CHANGELOG。
