# 路线图与待办（2026-09-17 审计，2.7.46 更新）

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
| 2 | **WinCC Unified**（`Siemens.Engineering.HmiUnified`） | 113 类型 / 685 成员 | **完成（2.7.29 收口）**。① 画面对象族 `UI.Shapes` / `Widgets` / `Controls` / `Base` / `Features` / `HmiScreenWindow`（69 类型）——**2.7.26 完成**：`DescribeUnifiedScreenItemType` + `ManageUnifiedScreenItem`（反射自官方 API 的类型目录与属性 schema，任意类型 `Create<T>`；硬编码属性表在 43 个类型 × 数十属性下不可维护，且与 V20/V21 差异冲突，故走"反射 + 形状检查 + 登记表"）；② 报警类 / 离散 / 模拟报警 `HmiAlarm`——**2.7.27 真机验证**（状态视觉颜色经 `UpdateUnifiedObjectProperties` 写入并复原），已登记；③ 报警/数据记录与备份/分段设置 `HmiLogging`——**2.7.27 真机验证**（`Settings.LogMaxSize` 写入并复原），已登记；审计类 `HmiAudit`（1 / 6）待验证；④ 运行时设置嵌套对象 `RuntimeSettings`（10 / 58）——**2.7.27 真机验证**：`ReadUnifiedObjectProperties` / `UpdateUnifiedObjectProperties` 路径 `[{property:RuntimeSettings},{property:OpcUaServerRuntimeSettings}]` 全部标量可读可写，已登记动态覆盖；⑤ **2.7.28**：新增 `ExchangeUnifiedTags`（WinCC ML `.hmi.yml`）、`ExchangeUnifiedScriptModules`、`ImportUnifiedOpcUaAlarms`；**2.7.29**：真机发现并修复变量导出回报路径无扩展名的核对失败；系统变量、阈值（`Create()` 被 TIA 拒绝）、替代值（仅外部变量）、审计类真机到达并登记，连接/驱动属性、OPC UA 报警类型、记录变量、列表、`Cpm`、`IValidator`、画面组以"仅形状检查"登记（该工程无 OPC UA 连接与工厂视图）。Unified 未封装功能类型 21 → 0；剩余真机补跑随下次重跑进行 |
| 3 | **Base 硬件深层与库**（`Siemens.Engineering.HW` / `Library` / `Umac` / `Security` / `Compare` / `CrossReference`） | 85 类型 / 310 成员 | **完成（2.7.33 收口，Base 功能类型缺口 0 / 0）**。③-④ Base 收尾——**2.7.33 完成**：`ReadPortalInfo`（`TiaPortalProcess` / `TiaPortalSession` / `TiaPortalProduct` / `TextCategory` / `HwUtilities`）、`ReadTransferRoutes`（`Connection.*` 路由树、`RHDownloadProvider` / `RHOnlineProvider`）、`ManageHardwareUtilities`（`ModuleInformationProvider` / `OpcUaExportProvider` / `CardReaderPscProvider`）、`ManageDeviceServiceObjects`（`WebApplicationConfiguration` / `Telecontrol*DataPoint` / `CertificateManagementConfiguration` + `CertificateSupportedService`）、`ReadObjectIdentifier` / `ShowObjectInEditor` / `RunToolsInTransaction`（`ObjectIdentifierProvider` / `IShowable` / `Transaction`）、`OpenProject` UMAC 凭据、`GoOnline` 用户认证与 R/H、`DownloadToPlc` R/H、类型化的传输提示 / 结果 / `AttributeConfiguration` 批量写 / 交叉引用 / 比较 / 目录 / 多用户 / 版本控制 / 设置行、`ExceptionMessageData`（Base 未封装 52 / 161 → 0 / 0；`CompileProvider` / `Private.ProcessHelper` 为 internal 从分母排除；真机已验 2026-09-19）。③-③ 用户管理与安全——**2.7.32 完成**：`ManageSyslogServers`（工程级 `SyslogServerProvider` / `SyslogServer` / `AssignedModules` + CPU `SysLogConfigurationManager`）、`ManagePasswordPolicy`（`PasswordPolicyConfigurator` / `PlcPasswordPolicyService` / `LegacyPlcPasswordPolicyService`）、`ManageUmcUsers`（offline 与服务器导入的 `UmcUser` / `UmcUserGroup`、`UmcServerConfigurator` 一致性检查与同步、`Authentication` 凭据）、`ManagePlcCertificate` 的 `CertificateTemplate` / `SubjectAlternativeName` / 密码导入、匿名用户激活（Base 未封装 61 / 194 → 52 / 161，`Security` / `Umac` 归零；真机已验 2026-09-18）。③-② 库深层——**2.7.31 完成**：6 个强类型工具封装 `ILibrary` 的 `UpdateCheck` / `UpdateLibrary` / `UpdateProject` / `HarmonizeProject` / `CleanUpLibrary`、`FindType` / `FindVersion`、类型与版本深层字段（Status / SetForUpdate / DoNotUse / Dependencies / Dependents / MasterCopiesContainingInstances / OriginalLibrary）、`DetailedCompareResult`、`GlobalLibraryInfo` / `Archive`、`TypeCreateTransferResults` / `VersionCreateTransferResults`（Base 未封装 75 / 271 → 61 / 194，Library 归零；真机已验 2026-09-18）。③-① 硬件网络深层——**2.7.30 完成**：13 个强类型工具封装 `IoSystem` / `IoController` / `IoConnector`、`SyncDomain` / `MrpDomain` / `MrpInstance`、`TransferArea` / `MulticastableTransferArea` / `TransferAreaMappingRule`、`Channel`、`Address` / `HwIdentifier` 及其控制器、`DeviceUserGroup` / `DeviceGroup`、`WebserverUser` / `SimpleWebserverUser` / `OpcUaUser`、`NetworkPort` 互连（Base 未封装 89 / 332 → 75 / 271；真机 2026-09-18 全部到达，域组合陈旧代理与释放异常误判两处缺陷待 2.7.31）；Base 里剩下的只是经 `ReadHardwareFeatures` 反射读取的"仅类型名"特性类（六种通信连接标量、`GsdDevice(Item)`、`FrontPanelDisplay`、`PlcAccessControlConfigurationProvider`、`PlcAccountLockingAtRuntimeFeature`、`SystemWebPagesFeature`、`ModuleDescriptionUpdater`、`PcInterfaceAssignment`），不计入缺口 |
| 4 | **Step7**（`Siemens.Engineering.SW`） | 44 类型 / 140 成员 | **完成（2.7.36 收口，Step7 功能类型缺口 0 / 0）**。④-③ 工艺对象映射——**2.7.36 完成**：`ReadTechnologyObjectTree`（`TechnologicalInstanceDBGroup` / `TechnologicalInstanceDB` / `TechnologicalParameter`）+ `ReadMotionAxisConfiguration` / `ManageMotionAxis` / `ManageTechnologyObject` / `ConfigureMotionHardwareConnection` 类型化（`AxisHardwareConnectionProvider` + `AxisEncoderHardwareConnectionInterface` / `TorqueHardwareConnectionInterface`、测量输入 / 输出凸轮、主值关联、V21 `TOMapping` / `DBMemberMapping` / `SuperimposingAxes` / Ident；新增 `Connect(Channel)` 目标；2026-09-19 在临时 `TO_SpeedAxis` 上真机验证通过）。④-② Step7 收尾——**2.7.35 完成并真机验证**：`ManagePlcExternalSources`、`ReadPlcSystemGroups`、`ReadPlcTagTableConstants`、`ExchangePlcAlarmTextListsXlsx`、`ManagePlcTableEntries`、`ExportPlcProDiagInfo` + 访问规则 / OPC UA / 报警类 / 监控设置 / 库类型 / 用户组类型化。④-① 软件单元与 `PlcSoftware` 小服务——**2.7.34 完成并真机验证**。`PlcForceTableEntry` 只读 |
| 5 | **经典 WinCC**（`Siemens.Engineering.Hmi`） | 24 类型 / 59 成员 | **完成（2.7.37，经典 WinCC 与 WinCC.Extension 功能类型缺口 0 / 0）**：`ReadClassicHmiScreenTree`、`ManageClassicHmiScreenObject`（弹出 / 模板 / 滑入 / 总览 / 全局元素）、`ManageClassicHmiFolder`（五类用户文件夹）、`ManageClassicHmiGraphic`（V21 `GraphicsProvider`）+ VB 脚本 / 库类型 / `ConstValue` / `NullableDateTime` 类型化。参考工程 HMI 是 Unified：形状检查 + 拒绝路径真机验证 |
| 6 | **选件包** | 107 类型 / 515 成员（2.7.37 审计） | **进行中**。⑥-① SiVArc——**2.7.38 完成（33 / 198 → 0 / 0）**：`ReadSivarcRuleTree` / `ManageSivarcRuleContainer` / `ManageSivarcRule`（六个规则族）、`ReadSivarcBlockDefinitions` / `ManageSivarcBlockDefinition`、`ResolveSivarcExpression`、`ManageSivarcScreenLayout`（V21）、`UpgradeSivarcDefinitions` + 类型化 `GenerateSiVArc`。⑥-② Startdrive + DCC——**2.7.39 完成（34 / 117 + 13 / 68 → 0 / 0）**：`ReadDriveObjects` / `ReadDriveParameters` / `ManageDriveTelegrams` / `ManageDriveFunctions` / `ManageDriveSecurity` / `ManageTechnologyExtensions` / `ManageDriveHardwareModule` / `ManageDriveSafetyAcceptanceTest` / `ReadOnlineDriveParameters` / `ManageOnlineDriveFunctions` + 类型化 `ManageStartdriveParameter`；`ReadDccCharts` / `ManageDccBlock` / `ManageDccPin` / `ManageDccChartInterface` / `ManageDccChartPartition` / `ManageDcbLibraries` + 类型化 `ManageDccChart` / `ReadDccObject`。⑥-③ SafetyValidation + Test Suite + Teamcenter + CFC——**2.7.42 完成（8 / 34 + 9 / 44 + 7 / 37 + 1 / 7 → 0 / 0，阶段 6 收口）**：`ReadSafetyActivationTests` / `ManageSafetyActivationTest` / `ManageSafetyActivationTestGroup` / `ManageSafetyFunction` / `ManageSafetyFunctionCondition`（V21）；类型化 `ReadTestSuiteCases` / `ExchangeTestSuiteCase` / `RunTestSuiteCase` + `ManageTestSuiteCase`；`ManageTeamcenterConnection` / `ManageTeamcenterDataset` / `ManageTeamcenterWorkflow`；类型化 `ExchangeCfcCharts` + `ManageCfcChartProtection`。虚拟机装有 Startdrive Advanced + DCC + CFC + Test Suite Advanced（2.7.38 `ReadPortalInfo`）；SafetyValidation / Teamcenter 许可未知，真机只能验 NotSupported 路径 |

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

状态（2.7.46）：本表各项已在 2.7.18–2.7.36 间落地（对应版本见 §5 各段与 CHANGELOG），本表保留为当时的优先级依据；未做的只剩 §2.2 的无 API 项与 §2.0 阶段 6 的选件包。

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


## 5. 2.7.46 已完成

- 全量真机重测：447 个工具在 `项目1` 各跑一遍，逐工具台账 `docs/reference/real-machine-ledger.md`；23 处修复 + 2 个 TIA 退出守卫（详见 CHANGELOG）。

## 5.0 2.7.45 已完成

- 引擎：`GetState` 重绑尊重显式绑定；`PlugDeviceItem` / `GetDevicePlugLocations` 的 `plugOnDevice`（Device 级驱动组件插入）与按名读回；`ManageDccPin` Value 按当前类型写；`sequenceIndex` 单独 update。
- 真机（2.7.44 引擎，空工程 `项目1`，自建 S120 V5.2 + 电机模块 + 电机 + 编码器）：**DCC 全族首次真机通过**（图表 / 子图 / 块 / 引脚 / 分区 / 顺序 / 导出导入 / DCB 库；图表接口 Create 不支持）；S120 投影读取与 `changeType` 被 TIA 拒绝；一次会话重绑事故（临时面板进了维护者工程，已删）。

## 5.1 2.7.44 已完成

- 引擎：CFC 预检解析器按真实的空导出结构修正（文件夹 / 列表容器不算图表）。
- 真机（2.7.43 引擎）：三处守卫（CFC 未知图表、Test Suite 空组、条件 NotRelevant）全部拦住，TIA 未退出。

## 5.2 2.7.43 已完成

- 引擎：2.7.42 真机重跑的三处 TIA 退出守卫——CFC `CompleteExport` 预检（图表清单解析，`skipChartPreflight`）、Test Suite 空组 `runAll` 拒绝、Safety Validation 条件 NotRelevant 按信号用途预校验 + `changeEvaluationDevice` 只认 `EvaluationDevices()`。
- 真机（2.7.42 引擎）：Test Suite 三组空表 / 样式指南空组执行；CFC 空导出；**Safety Validation Assistant 全链路通过（虚拟机有选件）**；Teamcenter 提供者存在无服务器；三次 TIA 退出引擎都活着。

## 5.3 2.7.42 已完成

- 引擎：阶段 6 ⑥-③（阶段 6 收口）：Safety Validation Assistant（V21 `Siemens.Engineering.SafetyValidation`）五个工具——激活测试与用户组（`ActivationTestComposition.Create / CreateFrom / Import`、`ChangeEvaluationDevice`、`ActivationTestPrintout.Generate`、`Export`）、安全功能（`SafetyFunctionComposition.Create / CreateFrom / Import`、`ResetTestResult`、`TraceConfiguration`）、条件（`ConditionComposition.Create(DeviceItem, SignalUsage, signal)`）与 `TestValidity.CheckValidity`；Test Suite 三个反射工具类型化改造（`RuleSet` / `TestCase` / `SystemTestCase` / `ApplicationTestSet` 与三类 `LoadFromFile` / 执行器的全部 `Run` 重载、递归 `TestResultsMessage`）+ `ManageTestSuiteCase`（`SetScope` 三种形态、`CopyScope`、`CreateFrom(MasterCopy)`、`ShowInEditor`）；Teamcenter Gateway 三个工具（`TeamcenterConnectionProvider.Connect / ConnectSSO / Disconnect`、`TcGatewayLockProvider`、`TcGatewaySearchAndDownloadProvider`、`TcGatewayWorkflowProvider` 九个保存动作与自定义属性）；CFC `ExchangeCfcCharts` 类型化（+ `SelectiveExport` / `ExportInstructionData`）+ `ManageCfcChartProtection`。审计：未封装功能类型 25 / 122 → **0 / 0**，有专用引用 629 → 668。

## 5.4 2.7.41 已完成

- 引擎：`App.config` `legacyUnhandledExceptionPolicy` + 未处理异常安全记录（TIA 退出后引擎不再随之崩溃）；`GetState` 对已退出的 TIA 进程回 `processAlive=false` 而不抛；`ManageDriveTelegrams` 对没有报文的驱动对象拒绝 `Can*` / `Insert*` / `Erase`；PLCSIM Advanced 桥改为缓存接口 + 变量表装载一次 + 串行调用（维护者报告 2.7.38 连续读写后崩溃）。
- 真机（2.7.40 引擎，临时设备）：`processAlive` 诊断、`includeValue=false`、`Bits` 位名解析通过；新建未联网 G120C 上的报文 `Can*` 让 TIA 退出并连带引擎崩溃。

## 5.5 2.7.40 已完成

- 引擎：2.7.39 真机重跑的守卫与诊断——`ManageDriveTelegrams` 已有主报文时跳过 `CanInsertMainTelegram` / `InsertMainTelegram`；`ReadDriveParameters` / `ReadOnlineDriveParameters` 的 `includeValue`（`Value` 最后读）；位参数名经 `Bits` 解析；绑定的 TIA 进程存活诊断（`GetPortalProcessHealth`，`GetState` / 连接失败保护回 `portalProcess`）；`GenerateSiVArc` 软件名 → 设备名两次尝试；`ManageSivarcTableRule` 文案改正。
- 真机（2.7.39 引擎，临时设备）：SiVArc 类型化规则工具与通用删除修复通过；Startdrive 驱动对象、参数读写、BICO 通过；发现三处让 TIA Portal V21 退出的 Startdrive 调用（`r2139` 读值、已有主报文时 `CanInsertMainTelegram`、新建驱动上未接线 `p840[0]` 读值）。

## 5.6 2.7.39 已完成

- 引擎：阶段 6 ⑥-② Startdrive + DCC 选件包：Startdrive `ReadDriveObjects`（`DriveObjectContainer` / `DriveObject` / `Telegram` / `DriveFunctionInterface` 视图、`ModuleAccessPoint`、V21 `DriveItemHardwareModule`）、`ReadDriveParameters`（`ReadDriveParameter` / `DriveParameter` 含 BICO 源、位参数、枚举表）、`ManageDriveTelegrams`（`TelegramComposition` 的 Can* / Insert* / Erase、`Telegram.ChangeSize` / `TelegramNumber`、V21 SDR 接口 / V20 基接口的 `Connect(Telegram[, ConnectOption])`）、`ManageDriveFunctions`（`DriveObjectTypeHandler` / `DriveObjectActivation` / `FunctionInUse` / `Commissioning` / `SafetyCommissioning` / `HardwareProjection` + `MotorConfiguration` / `EncoderConfiguration` / `ConfigurationEntry`）、`ManageDriveSecurity`（`UmacConfiguration` / `DriveDataEncryption`）、`ManageTechnologyExtensions`（`TechnologyExtensionContainer` / `TechnologyExtensionInstallationProvider`）、`ManageDriveHardwareModule`（V21）、`ManageDriveSafetyAcceptanceTest`（V21）、`ReadOnlineDriveParameters` / `ManageOnlineDriveFunctions`（`OnlineDriveObjectContainer` / `OnlineDriveFunctionInterface` / `DriveDomainFunctions`）+ 类型化 `ManageStartdriveParameter`（新增 `driveObjectIndex`、BICO 写入）与两个 Startdrive 上载提示；DCC `ReadDccCharts`、`ManageDccBlock`、`ManageDccPin`、`ManageDccChartInterface`、`ManageDccChartPartition`、`ManageDcbLibraries`（V21 `DcbLibraryImporter`）+ 类型化 `ManageDccChart`（子图路径、自动命名、`exportAll` / `readSequence` / `showEditor`、`MoveInRuntimeSequence`、`confirmDelete`）/ `ReadDccObject`，39 个 `DccException` 子类在 `Portal.KnownDccExceptions` 里分类回报。全部对照 Startdrive 手册 "Functions for Startdrive"（文档地图 `WtRgStszJ5UCQ8X~aGwgnA`）与 DCC 手册 "Functions for DCC"（`3BtKZ42eEtW7HPmLDWNxyw`）。Startdrive 34 / 117 与 DCC 13 / 68 功能类型缺口归零；选件包剩 25 / 122。
- 修复 2.7.38 真机的三处缺陷：`ManageSivarcRule` 改名 `ManageSivarcTableRule`（`CallTool` 的映射不分大小写，被旧 `ManageSiVArcRule` 遮蔽），`Check-DeadToolReferences.py` 新增"工具名不分大小写唯一"闸门；`GenerateSiVArc` 改传 PLC 设备名（`Sivarc.Generate` 的 `plcs` 是设备名）；通用 `ManageSiVArcRule delete` 后从锚点重导航再核对。
- 验证：离线 2056 项、形状检查 V20 2589 / V21 2809、工具 437。

## 5.7 2.7.38 已完成

- 引擎：阶段 6 ⑥-① SiVArc 选件包：`ReadSivarcRuleTree`、`ManageSivarcRuleContainer`、`ManageSivarcRule`、`ReadSivarcBlockDefinitions`、`ManageSivarcBlockDefinition`、`ResolveSivarcExpression`、`ManageSivarcScreenLayout`、`UpgradeSivarcDefinitions` + `GenerateSiVArc` / `ReadSiVArcRules` / `ManageSiVArcRule` / `ReadLibraryType typeKind` 的类型化改造，全部对照 SiVArc 手册 "SiVArc Openness" 章（规则表 / 文件夹、规则与规则组、母本复制、库对象引用、PLC / HMI 设备列、变量 / 文本定义、变量成员设置、表达式解析器、布局字段导入导出、定义升级、生成）与 `Siemens.Engineering.Sivarc.xml`。有专用引用 505 → 573，完全未触及 322 → 255，未封装功能类型 107 / 515 → 72 / 307（**SiVArc 33 / 198 → 0 / 0**）。离线 1953、形状检查 2301 / 2497。真机不可验证（无 SiVArc 许可）。

## 5.8 2.7.37 已完成

- 引擎：阶段 5 经典 WinCC 文件夹层次：`ReadClassicHmiScreenTree`、`ManageClassicHmiScreenObject`、`ManageClassicHmiFolder`、`ManageClassicHmiGraphic` + `ReadClassicHmiScripts` / `ManageClassicHmiScript` / `ReadLibraryType` / `EngineeringScalarProperties.Json` 的类型化改造，全部对照官方 "Exporting / Importing a pop-up screen" / "Exporting / Importing a slide-in screen" / "Exporting screen templates from a folder" / "Deleting a user-defined folder of an HMI device" / "Creating user-defined folders for HMI tags" / "Exporting/importing graphics" 章节与 `Siemens.Engineering.WinCC(.Extension).xml`。有专用引用 469 → 505，完全未触及 357 → 322，未封装功能类型 133 / 585 → 107 / 515（**经典 WinCC 24 / 59 → 0 / 0，WinCC.Extension 2 / 11 → 0 / 0；核心程序集全部收口**）。离线 1881、形状检查 1957 / 2140。真机（2026-09-19）：四个工具对 Unified / PLC 目标按预期拒绝，`typeKind` 在 1,038 个工程库类型上核对（Unified `ScriptModuleType` 与基类 `LibraryType` 面板暂回 `other`，2.7.38 补），`ConstValue` / `NullableDateTime` 在 Unified 里不出现；经典实做路径无经典 HMI 工程可验。

## 5.9 2.7.36 已完成

- 引擎：2.7.35 真机缺陷修复（监控表注释行计数从表重新导航）；阶段 4 子批次 ④-③ 工艺对象映射：`ReadTechnologyObjectTree` + `ReadMotionAxisConfiguration` / `ManageMotionAxis` / `ManageTechnologyObject` / `ConfigureMotionHardwareConnection` 的类型化改造（`TechnologicalInstanceDBGroup` / `TechnologicalParameter` / `TechnologicalInstanceDBAssociation`、`AxisHardwareConnectionProvider` 与两种硬件连接接口的全部重载、测量输入 / 输出凸轮、V21 `TOMapping` / `DBMemberMapping` / `SuperimposingAxes` / Ident，`Connect(Channel)` 目标），全部对照 `Siemens.Engineering.Step7.xml` 的 `SW.TechnologicalObjects` 摘要。有专用引用 451 → 469，完全未触及 370 → 357，未封装功能类型 139 / 637 → 133 / 585（**Step7 5 / 42 → 0 / 0，阶段 4 收口**）。离线 1836、形状检查 1879 / 2045。真机验证通过（2026-09-19，临时 `TO_SpeedAxis` V9.0）。

## 5.10 2.7.35 已完成

- 引擎：2.7.34 真机缺陷修复（单元关系回读从单元组重新导航）；阶段 4 子批次 ④-② Step7 收尾：`ManagePlcExternalSources`、`ReadPlcSystemGroups`、`ReadPlcTagTableConstants`、`ExchangePlcAlarmTextListsXlsx`、`ManagePlcTableEntries`、`ExportPlcProDiagInfo` + 访问规则 / OPC UA / 报警类 / 监控设置 / 库类型 / 用户组的类型化改造，全部对照官方 "Generating blocks from source" / "Generating block/UDT from external source file in specific user group" / "Querying the system group for system blocks" / "Export/Import of Plc Alarm TextLists" / "Export/Import of Alarm classes" / "Exporting ProDiag alarm message" 章节与 `Siemens.Engineering.Step7.xml`。有专用引用 417 → 451，完全未触及 406 → 370，未封装功能类型 162 / 695 → 139 / 637（Step7 28 / 100 → 5 / 42）。离线 1825、形状检查 1784 / 1929。真机验证通过（2026-09-19）。

## 5.11 2.7.34 已完成

- 引擎：2.7.33 真机事务缺陷修复（`ForceRealExecution` 无键也写 `dryRun=false`）；阶段 4 子批次 ④-① Step7 软件单元与 `PlcSoftware` 小服务：`ReadPlcSoftwareUnits`、`ManagePlcDocuments`、`ReadPlcChecksums`、`ReadPlcObjectFingerprints`、`ManagePlcBlockWriteProtection`、`ManageProjectCompilationSettings` + `ManagePlcSoftwareUnit` / `ReadDeviceItemChannels` / `UpdateDeviceAddress` / `ImportFromDocuments` 扩展，全部对照官方 "Accessing software unit" / "Accessing the SafetyUnit" / "Accessing name value type document" / "Exporting UDT as document" / "Accessing Software Checksum" / "Changing blocks using fingerprints" / "Setting up write protection of blocks" / "Updating project properties" 章节与 `Siemens.Engineering.Step7.xml`。有专用引用 396 → 417，完全未触及 428 → 406，未封装功能类型 178 / 735 → 162 / 695（Step7 44 / 140 → 28 / 100）。离线 1754、形状检查 1670 / 1810。真机验证通过（2026-09-19）。

## 5.12 2.7.33 已完成

- 引擎：2.7.32 真机问题修复（工程级 syslog `create` 改为只预览、证书 `template` 免 `certificateId`、`UmacDevice` 文案、`CheckLibraryUpdates parts` 扁平渲染、多 TIA 实例下的改绑守卫）；阶段 3 子批次 ③-④ Base 收尾：`ReadPortalInfo`、`ReadTransferRoutes`、`ManageHardwareUtilities`、`ManageDeviceServiceObjects`、`ReadObjectIdentifier`、`ShowObjectInEditor`、`RunToolsInTransaction` + `OpenProject` / `GoOnline` / `DownloadToPlc` 扩展与十余处类型化改造，全部对照官方 "Diagnostic interfaces" / "Transaction handling" / "Identifying cross session object" / HwUtilities / "Managing dynamic certificate settings" 章节与 `Siemens.Engineering.Base.xml`。有专用引用 314 → 396，完全未触及 490 → 428，未封装功能类型 230 / 896 → 178 / 735（**Base 52 / 161 → 0 / 0，阶段 3 收口**）。离线 1667、形状检查 1585 / 1708。真机已验（2026-09-18，见 `docs/releases/v2.7.32.md#真机结果`）。

## 5.13 2.7.32 已完成

- 引擎：2.7.31 真机缺陷修复（`ProjectLibrary` 无 `Name` 的库标签、系统库 null `TypeFolder` 守卫、`UpdateCheck` 预拒绝、用户库限定动作的文案）；阶段 3 子批次 ③-③ 用户管理与安全：`ManageSyslogServers`、`ManagePasswordPolicy`、`ManageUmcUsers` + `ManagePlcCertificate`（`template` / SAN / 密码导入）与 `ManageProjectUserManagement`（匿名用户）扩展，全部对照官方 UMAC / UMC / 密码策略 / 证书 / Syslog 章节与 `Siemens.Engineering.Base.xml`。有专用引用 288 → 314，完全未触及 513 → 490，未封装功能类型 239 / 929 → 230 / 896（Base 61 / 194 → 52 / 161）。离线 1620、形状检查 1403 / 1500。真机已验（2026-09-18，见 `docs/releases/v2.7.31.md#真机结果`）。

## 5.14 2.7.31 已完成

- 引擎：2.7.30 真机问题修复（HW 组合代理刷新、就地捕获已释放代理、通道属性访问模式）；阶段 3 子批次 ③-② 库深层：`ReadLibraryOverview`、`ReadLibraryType`、`ManageLibraryType`、`CheckLibraryUpdates`、`SynchronizeLibrary`、`CompareLibraryObjects` + `ManageGlobalLibrary` / `ImportLibraryTypeDocuments` 扩展，全部对照官方 "Functions on libraries" 章节与 `Siemens.Engineering.Base.xml`。有专用引用 269 → 288，完全未触及 535 → 513，未封装功能类型 253 / 1,006 → 239 / 929。离线 1550、形状检查 1291 / 1388。真机已验（2026-09-18，见 `docs/releases/v2.7.30.md#真机结果`）。

## 5.15 2.7.30 已完成

- 引擎：阶段 3 子批次 ③-① Base 硬件网络深层：13 个强类型工具（IO 系统、同步域 / MRP 域 / MRP 实例、传输区与映射规则、CCDX 多播传输区、通道、地址 / 硬件标识符及控制器、设备用户组、Web 服务器 / SIWAREX / OPC UA 用户、端口互连），全部对照官方 "Functions on networks" / "Functions on device items" / PLC 服务章节与 `Siemens.Engineering.Base.xml`。有专用引用 238 → 269，完全未触及 556 → 535，未封装功能类型 267 / 1,067 → 253 / 1,006。离线 1506、形状检查 1158 / 1255。真机重跑（2026-09-18）：13 个工具全部到达真实对象，读取与可复原写入通过；发现域组合代理在 Create/Delete 后陈旧、`EngineeringObjectDisposedException` 误触发连接失败保护两处缺陷，2.7.31 修。

## 5.16 2.7.29 已完成

- 引擎：`ExchangeUnifiedTags export` 回报路径解析修复 + `directoryListing`；剩余 Unified 类型登记（真机 / 仅形状检查如实标注）；完全未触及 599 → 556，未封装功能类型 288 / 1,185 → 267 / 1,067。离线 1446、形状检查 990 / 1087。WinCC Unified 阶段收口，下一阶段为 Base 硬件深层 / 库 / UMC / 安全（§2.0 阶段 3）。

## 5.17 2.7.28 已完成

- 引擎：WinCC Unified 子批次 ⑤：`ExchangeUnifiedTags`、`ExchangeUnifiedScriptModules`、`ImportUnifiedOpcUaAlarms`；其余剩余类型经既有通用工具真机验证并登记。离线 1437、形状检查 990 / 1087。

## 5.18 2.7.27 已完成

- 引擎：2.7.26 画面对象族真机重跑；修复创建后代理身份核对（`ManageUnifiedScreenItem` / `ManageUnifiedScreenLayout`）、部件内多语言文本、`UpdateUnifiedObjectProperties` 的颜色与嵌套部件写入；2.7.27 真机重跑全部通过。子批次 ②③④（`HmiAlarm` / `HmiLogging` / `RuntimeSettings`）与 `UI.Controls` 真机验证后登记为动态覆盖：完全未触及 626 → 599，Unified 未封装功能类型 41 → 21。

## 5.19 2.7.26 已完成

- 引擎：WinCC Unified 画面对象族（§2.0 阶段 2 子批次 ①）：`DescribeUnifiedScreenItemType`、`ManageUnifiedScreenItem`；隐藏 `EventHandlers` 的四种对象类型的歧义修复。审计脚本新增动态覆盖登记表与"动态覆盖"栏（完全未触及 885 → 626）。离线 1402、形状检查 936 / 1032。

## 5.20 2.7.25 已完成

- 引擎：`Siemens.Engineering.Safety` 全量封装（§2.0 阶段 1）。`ManagePlcSafety` 强类型重写并补齐嵌套对象、文档化属性、集体签名（此前从未真正读出）、F-I/O 状态块生成、系统对象清理、F-BaseID、登录/登出/密码设置与撤销；新增 `ManageSafetyGlobalSettings`、`ReadSafetyBlockSignatures`、`ExportSafetyPrintout`；下载提示 `SafetyProgram`。离线 1353、形状检查 886 / 982。

## 5.21 2.7.20–2.7.24 已完成

- 引擎：`DescribeBlockLogic` 在真实 V21 工程上暴露的渲染丢失全部修复并真机回归（SCL 调用 / 命名常量 / 绝对地址 / 数组下标 / 位切片 / `ENO`，LAD `<Call>` 调用框 / 用户命名引脚 / `Not` / EN 链）；`RenderPlcBlockDocument` / `ComparePlcBlockDocuments` / `GeneratePlcDocumentation` 同族修复。SCL 渲染改为按 `SW.PlcBlocks.Access_v5.xsd` 的全部分支实现。
- 配置器：重做为单页双栏连接控制台（虚拟机 ↔ 宿主机 / 同一台电脑），客户端卡片按 CLI 在前排列并加入通义千问 / Kimi / 腾讯元宝 / DeepSeek / 智谱清言 / Grok（写各家官方 CLI 或 OpenCode）。
- 覆盖盘点重跑（2.7.24）：API 封装面较 2.7.18 仅 +12 成员 / +4 类型；去掉壳子类型与动态覆盖后，真实缺口 381 个功能类型 / 1,753 个成员，见[覆盖清单](../reference/openness-coverage.md#缺口结构2724)。据此把 **Safety** 提为第一阶段（§2.0），2.7.25 完成。

## 5.22 2.7.19 已完成

- 引擎：E8、E11 完成，全部 11 项引擎待办处理完毕（E10 保留为设计决定）。新增 9 个工具（共 359）：PLCSIM Advanced 通道 5 个（#1 与 S2 的场景式单元测试，反射后期绑定，**未在真实 PLCSIM Advanced 上验证**）、离线文档 2 个（S3 的块可视化，自研 Markdown/Mermaid 渲染，不移植 TS 渲染器）、SCL 预检 1 个（#8，自研启发式规则，不引入 tree-sitter/Node）、AML 生成 1 个（#6 的生成半边，不引入 Aml.Engine，配合已有 `ImportDeviceAml`）。
- 程序文档（#9）由 `GeneratePlcDocumentation` 覆盖（索引、调用交叉引用、逐块渲染）。写保护钩子与审计日志（S7 及竞品的审计模式）随插件交付。
- §3 候选中仍未落地：AutoPLC / Agents4PLC 数据（#4/#5，skill 调优素材，只在 [生态与参考资源](../reference/ecosystem.md) 登记）；`Siemens.Collaboration.Net.*` 再分发决策（§4）仍待维护者。
- 分类修正：6 个运行时/上载写入工具改为 ONLINE-WRITE。

## 5.23 2.7.18 已完成

- 引擎：E1–E5、E7、E9 处理完毕；新增 52 个官方 Openness 工具、8 个运行时通道工具、3 个离线分析工具与 `ListToolCategories`，共 350 个工具；V20/V21 重建，离线 1158、形状检查 847/758、实际 EXE 回归两版全过；**真实工程验收未执行**。
- §2.1 中的 P1（下载提示、设备上载/扫描、DB 快照、文件夹下载）与大部分 P2（块保护、`UpdateProgram`、OPC UA 访问控制、UMAC 读写、库/工程比较、报警文本导入、Unified 事件/部件/动态化、通信连接、监视/强制表 Web 访问）已实现；P3 的 ProDiag 对象、多用户会话、Motion 对象模型、经典 HMI 脚本亦已实现。仍未做：SafetyValidation、Teamcenter、Startdrive/SiVArc/DCC 剩余动作、UMC 同步与工程保护启停、硬件杂项（App ID、批量参数、PSC、Logo、CiR、共享设备、I-Device GSD 导出）、库实例清理/更新流程。
- §3 候选：已落地 —— `Siemens.Simatic.S7.Webserver.API`（#2）、WinCC Unified Open Pipe（S1）、语义 diff（#3/S4，自研实现）、TST/CaX AML 导入（S5，`ImportDeviceAml`）、TODO 扫描与 SCL 自测试模板（S8）、Test Suite（#7，既有工具）。未落地 —— PLCSIM Advanced API（#1，本机无 DLL，需 PLCSIM Adv 环境）、PLCSIM.UnitTest（S2，同上）、TIA Viewer 渲染器（S3，TS 移植量大）、AutoPLC/Agents4PLC 数据（#4/#5，属 skill 调优）、Aml.Engine（#6，AML 生成侧）、tree-sitter/plc-st-review 预检（#8）、siemens-plc-tools（#9）、写保护 hook（S7）。
- 仓库整理与审计（2.7.17 之后、2.7.18 之前）：删除过期 `手册/`、5 个 `_deprecated` PLC JSON 模板、被取代的 `Generate-ToolsList.py`；`design-qa.md` 归档；修复 14 处过期引用；`openness-limitations.md` 更正发现扫描断言；补齐第三方许可证清单与原文；`.gitignore` 增加 TIA 工程扩展名与本机 PublicAPI 目录；`plugin.json` 内联 MCP 配置并删除根 `.mcp.json`。详见 CHANGELOG。
