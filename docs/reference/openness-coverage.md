# 官方 Openness API 覆盖清单（V21，2.7.29）

[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [路线图](../development/roadmap.md) · [Openness 限制](../troubleshooting/openness-limitations.md)

本页回答"西门子官方 Openness API 有、而本项目没有专用封装的是什么"。数据由 [scripts/diagnostics/Audit-OpennessCoverage.ps1](../../scripts/diagnostics/Audit-OpennessCoverage.ps1) 对本机 V21 PublicAPI 的 18 个官方 XML 文档（不含 AddIn.*）与引擎源码逐成员对照生成；官方 XML 与 DLL 不随仓库分发。本次盘点对应 2.7.27 引擎源码（2026-09-18）；上一次为 2.7.26 / 2.7.25 / 2.7.24（同日）与 2.7.18。

## 口径（先读这个，再看数字）

- 分母是 **4,490 个领域成员**（1,239 个方法 + 3,251 个属性），已剔除每个类型都有的 `ToString`/`Equals`/`GetHashCode`/`Parent`/`GetAttribute(s)`/`SetAttribute(s)`/集合枚举等 10,294 个样板成员、构造函数和显式接口实现。
- 判定是**词法**的：方法记为"已引用"须同时满足所属类型简名出现在源码、且 `.方法名(` 出现在源码。这只证明"有专用代码路径"，不证明 MCP 工具已完整封装该能力；反过来，**"未触及"不等于"不可用"**——`DescribeObject` / `InvokeObject` / `InvokeService` 等通用反射工具可以动态到达任何公开成员，而且 2.7.18 的 Unified 事件/部件、库比较等工具是按官方 `GetCompositionInfos` / 泛型 `Create<T>` 等动态接口实现的，类型名以字符串出现，词法扫描看不到（例如 `HmiUnified.UI.Events` 仍显示 0 引用，但 `ReadUnifiedObjectEvents` 会枚举全部 96 种事件类型）。
- 因此下面分两层：**统计表**如实给出词法结果；**状态表**只列同时经工具清单与人工核对的结论。
- **动态覆盖（2.7.26 起）**：类型名从不出现在源码、但由反射/泛型适配器到达的类型，登记在 [scripts/diagnostics/openness-dynamic-coverage.json](../../scripts/diagnostics/openness-dynamic-coverage.json)（模式、工具、机制、真机证据），审计脚本按全名通配匹配后记为 DYNAMIC 并单列一栏。登记的前提是有形状检查或真机验证；没有证据的不登记，仍算"未触及"。
- 结果（V21，2.7.27）：领域成员已引用 **675**（2.7.26 为 663，2.7.18 为 609）、仅类型名 824、未引用 2,991；有领域成员的类型 1,217 个中有专用引用 **231**、仅类型名 102、**动态覆盖 285**、完全未触及 **599**（2.7.26 为 626，2.7.25 为 885）。2.7.27 的变化：真机验证后把 `HmiAlarm` / `HmiLogging` / `RuntimeSettings` 登记为动态覆盖，`UI.Controls` 补上真机创建证据。

## 缺口结构（2.7.29）

556 个"完全未触及"的类型里（2.7.27 为 599，2.7.26 为 626，2.7.25 为 885；差额是登记为动态覆盖的 322 个 Unified 类型），`*Composition` / `*Association` / `*EventArgs` / `*Exception` / `*Result` / `*Info` 这类集合与结果壳子 333 个，Unified `UI.Events`（96）与 `UI.Parts`（85）经 `GetCompositionInfos` / 泛型 `Create<T>` 动态覆盖而词法不可见（2.7.26 起已从"未触及"移入动态覆盖栏）。去掉这三类后，**没有专用封装的功能类型为 267 个 / 1,067 个成员**（2.7.27 为 288 / 1,185，2.7.26 为 308 / 1,286；2.7.26 起所有行统一用审计脚本的壳子规则重算），其中核心程序集（Base / Step7 / WinCC / WinCCUnified）157 个 / 531 个，`WinCC.Extension` 2 个 / 11 个，选件包（SiVArc / Startdrive / DCC / Test Suite / SafetyValidation / Teamcenter / CFC）108 个 / 525 个。

| 程序集 | 未封装功能类型 | 成员 | 具体是什么 |
|---|---:|---:|---|
| WinCCUnified | 0 | 0 | **2.7.26–2.7.29 已封装/登记**：画面对象族（`ManageUnifiedScreenItem` + `DescribeUnifiedScreenItemType`）、报警类与离散/模拟报警（`HmiAlarm`）、记录（`HmiLogging`）、运行时设置（`RuntimeSettings`）——真机验证；2.7.28 新增 `ExchangeUnifiedTags` / `ExchangeUnifiedScriptModules` / `ImportUnifiedOpcUaAlarms`（变量导出核对 2.7.29 真机修复）；系统变量、阈值（`Create()` 被 TIA 拒绝）、替代值（仅外部变量）、审计类真机到达并登记；连接/驱动属性、OPC UA 报警类型、记录变量、文本/图形列表、`Cpm`、`IValidator`、画面组以**仅形状检查**登记（登记表 `verified` 字段如实标注，待下次真机重跑补跑） |
| Base | 89 | 332 | 85 | 310 | HW 深层：`TransferArea` / `MulticastableTransferArea`、`MrpInstance`、`SyncDomain`、`IoSystem` / `IoConnector`、`Channel`、`WebserverUser`、`DeviceGroup`（约 100 个成员）；`GlobalLibrary`(30) / `ILibrary` / 类型版本文件夹与 `UpdateCheck`；Umac 的 `UmcUser` / `UmcUserGroup` / `UmcCredentials`；Security 的 `SyslogServer`、`CertificateTemplate`、`PlcPasswordPolicyService`；`TiaPortalSession` / `Transaction` / 事件参数；`Compare` 结果元素；跨引用 `SourceObject` / `ReferenceObject` |
| Step7 | 44 | 140 | 41 | 136 | 软件单元 `SW.Units`（27）、`PlcDocument*` 与 `PlcChecksumProvider` / `PlcSimulationSettingsProvider`、`PlcBlockWriteProtectionProvider`、系统块/类型组、`PlcForceTableEntry`（刻意不注册）、报警类导入导出结果、`OpcUaCommunicationGroup` |
| WinCC（经典） | 24 | 59 | 弹出画面 / 滑入画面 / 模板的文件夹层次（23）、变量文件夹 |
| Safety | 0 | 0 | **2.7.25 全量封装**：`ManagePlcSafety`（12 个动作，强类型）、`ManageSafetyGlobalSettings`、`ReadSafetyBlockSignatures`、`ExportSafetyPrintout`、下载提示 `SafetyProgram`。统计表里 Safety 仍有 2 个"完全未触及"类型（`RuntimeGroupComposition` / `SafetySignatureComposition` 只有 `IsReadOnly` 一类样板外成员）与若干 OWNER_ONLY 属性（经 `EngineeringScalarProperties` 通用读写，词法看不见）。`SafetyValidation` 选件（14 类型）未做，见选件包行 |
| 选件包 | 110 | 536 | 108 | 522 | Startdrive `MC.Drives`(22 类型) + `DFI`(16)、DCC 图表（62，其中 39 个是异常类）、SiVArc（65）、Test Suite（16）、SafetyValidation（14）、Teamcenter（12） |

已核实为**官方没有 API** 而保持明确拒绝的项不计入缺口，见下文"已核实为官方无 API 的项"。"未封装"不等于"用不了"：`DescribeObject` / `GetObjectProperty` / `InvokeObject` / `InvokeService` 可到达任何公开成员，只是没有带校验、预览与文档的专用工具。

## 能力域状态（2.7.18 建表，2.7.25 增补 Safety，2.7.26–2.7.27 增补 Unified 画面对象与嵌套属性）

| 能力域 | 官方入口（V21 XML） | 2.7.17 | 2.7.18 |
|---|---|---|---|
| Unified 画面对象 | `HmiUnified.UI.Shapes/Widgets/Controls/Screens`（43 个具体类型，V20 37）+ `UI.Base` / `UI.Features` | 只有 Button/Rectangle/Text/IOField 别名 + 版式 JSON | **2.7.26**：`DescribeUnifiedScreenItemType`（目录 + 属性 schema）、`ManageUnifiedScreenItem`（任意类型 `Create<T>`，标量/颜色/部件/多语言嵌套写入并读回） |
| Unified 报警类 / 记录 / 运行时设置嵌套对象 | `HmiAlarm.*`、`HmiLogging.*`、`RuntimeSettings.*` | 标量经通用路径 | **2.7.27**：`UpdateUnifiedObjectProperties` 接受 `#AARRGGBB` 颜色与嵌套部件（报警类四种状态视觉、记录 `Settings/Backup/Segment`、运行时设置子对象），真机写入并复原 |
| Safety（F 程序） | `Safety.*`（12 类型 / 54 成员） | 反射读标量 | **2.7.25**：`ManagePlcSafety` 强类型 12 动作、`ManageSafetyGlobalSettings`、`ReadSafetyBlockSignatures`、`ExportSafetyPrintout`；F 编译不在 PublicAPI 内 |
| 下载提示应答 | `Download.Configurations.*` 43 种 | 只应答 16 种，3 种误作复选框 | 全部按真实形态应答，未应答项回传 |
| 设备上载 / 可达设备扫描 | `Upload.StationUploadProvider`、`Connection.ConfigurationPcInterface.GetAccessibleDevices` | 无 | `ScanAccessibleDevices`、`UploadStationFromPlc`、`UploadDeviceParameters`（V21） |
| 下载到 Windows 文件夹 | `DownloadProvider.Download(DirectoryInfo,…)` | 无 | `DownloadPlcToFolder` |
| DB 快照 / 实际值往返 | `SW.Blocks.Interface.ValueService.*`、`InterfaceSnapshot.Export` | 无 | `ManagePlcDataBlockSnapshot`（V20 无 `ValueService`） |
| 块保护 / 指纹 | `SW.Blocks.PlcBlockProtectionProvider`、`FingerprintData.*` | 无 | `ManagePlcBlockProtection`、`ReadPlcBlockFingerprints`（在线读取） |
| PLC 程序升级 | `SW.PlcSoftware.UpdateProgram` | 无 | `UpdatePlcProgram` |
| OPC UA 访问控制 | `SW.OpcUa.AccessControl.*` | 无 | `ReadOpcUaAccessControl`、`ManageOpcUaAccessControl`（V21） |
| 工程用户/角色/UMAC | `Umac.*`（31 类型） | 只读探测 | `ReadProjectUserManagement`、`ManageProjectUserManagement`（15 种动作）、`ReadProjectProtection`；UMC 同步与启停保护未做 |
| 库/工程比较 | `Library.Compare.*`、`Compare.*`、`PlcSoftware.CompareTo`、`HardwareObject.CompareTo` | 无 | `CompareLibraries`、`CompareProjects`（离线） |
| PLC 报警实例文本导入 / 文本列表 | `PlcAlarmTextProvider.ImportInstanceTextsFromXlsx`、`SW.Alarm.TextLists.*` | 只有导出 | `ImportPlcAlarmInstanceTexts`、`ManagePlcAlarmTextList`（官方无条目 API） |
| Unified 事件与部件对象模型 | `HmiUnified.UI.Events.*`（96）、`UI.Parts.*`（85）、`UI.Dynamization.*` | 只有按钮事件 | `ReadUnifiedObjectEvents`、`ManageUnifiedObjectParts`、`ManageUnifiedDynamization`、`ManageUnifiedScreenLayout`；阈值/列无原生 `Create`，画面复制与布局字段官方无 API |
| Unified 列表条目 | — | 无 | 官方无条目类型；`ManageUnifiedListEntries` 定位列表并明确 NotSupported 变更 |
| 通信连接 | `HW.CommunicationConnections.*`（10 类型） | 只有 HMI 连接 | `ReadCommunicationConnections`、`ManageCommunicationConnection`（V21） |
| 监视/强制表 Web 访问 | `HW.Features.WatchAndForceTableAccessManager` | 无 | `ManageWatchForceTableWebAccess` |
| 系统诊断设置 | `HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider` | 无 | `ExchangeSystemDiagnosticsSettings` |
| AML 导入 | `Cax.CaxProvider.Import` | 只有导出 | `ImportDeviceAml` |
| 多用户 / Project Server | `Multiuser.*`（14 类型） | 本地会话附着 | `ManageMultiuserSession`（服务器连接、工程列表、锁状态、提交）；创建会话沿用 OpenSession |
| Motion 工艺对象 | `SW.TechnologicalObjects.Motion.*`（23 类型） | 凸轮/位地址映射 | `ReadMotionAxisConfiguration`、`ManageMotionAxis`（主值耦合、解释器映射、硬件连接、Ident）；`Connect(Channel)`/`Connect(Telegram)` 未做 |
| ProDiag | `SW.Supervision.*` | XLSX 交换 | `ManagePlcSupervision`（官方无类型化监督组合，经动态组合接口） |
| 经典 HMI 脚本/周期/列表/全球化/面板 | `Hmi.RuntimeScripting.*`、`Hmi.Cycle`、`Hmi.TextGraphicList.*`、`Hmi.Globalization`、`Hmi.Faceplate` | 无 | 6 个 `*ClassicHmi*` 工具；官方无 `Create(string)`，新对象经 XML 导入 |
| 选件 | Startdrive、SiVArc、DCC、CFC、Test Suite、SafetyValidation、Teamcenter | 部分 | 未变：SafetyValidation、Teamcenter 仍无入口；其余保持既有工具 |

## 已核实为"官方无 API"的项（保持明确拒绝）

独立 RUN/STOP（运行时通道 `SetPlcWebOperatingMode` 可做，但不经 Openness）、清除强制、读诊断缓冲区、按块选择性下载、LED/模块在线健康、Unified 画面复制、Unified 布局字段导入导出、Unified 列表条目、经典 HMI 脚本/周期/列表 `Create(string)`、ProDiag 类型化监督组合、Unified 阈值/数据网格/报警行列 `Create`、工程级"已保护"标量。V21 XML 与在线文档均无对应成员。

## 统计表（脚本生成，2.7.29 源码；"动态覆盖"栏来自登记表）

| 程序集 | 命名空间 | 类型数 | 有专用引用 | 仅类型名 | 动态覆盖 | 完全未触及 | 成员数 | 已引用成员 |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Base | `Siemens.Engineering` | 59 | 13 | 6 | 0 | 40 | 185 | 47 |
| Base | `Siemens.Engineering.AdvancedProtection` | 1 | 1 | 0 | 0 | 0 | 3 | 3 |
| Base | `Siemens.Engineering.Compare` | 3 | 0 | 0 | 0 | 3 | 7 | 0 |
| Base | `Siemens.Engineering.Compiler` | 5 | 2 | 0 | 0 | 3 | 14 | 5 |
| Base | `Siemens.Engineering.Connection` | 14 | 5 | 0 | 0 | 9 | 34 | 15 |
| Base | `Siemens.Engineering.CrossReference` | 8 | 2 | 0 | 0 | 6 | 28 | 6 |
| Base | `Siemens.Engineering.CustomIdentity` | 2 | 1 | 1 | 0 | 0 | 4 | 3 |
| Base | `Siemens.Engineering.Download` | 5 | 2 | 0 | 0 | 3 | 21 | 8 |
| Base | `Siemens.Engineering.Download.Configurations` | 31 | 3 | 24 | 0 | 4 | 32 | 3 |
| Base | `Siemens.Engineering.FingerprintData` | 4 | 2 | 0 | 0 | 2 | 6 | 4 |
| Base | `Siemens.Engineering.HW` | 63 | 13 | 1 | 0 | 49 | 204 | 45 |
| Base | `Siemens.Engineering.HW.CommunicationConnections` | 10 | 2 | 8 | 0 | 0 | 90 | 3 |
| Base | `Siemens.Engineering.HW.CustomDataTypes` | 3 | 0 | 0 | 0 | 3 | 4 | 0 |
| Base | `Siemens.Engineering.HW.Extensions` | 1 | 1 | 0 | 0 | 0 | 2 | 2 |
| Base | `Siemens.Engineering.HW.Features` | 43 | 15 | 28 | 0 | 0 | 111 | 19 |
| Base | `Siemens.Engineering.HW.HardwareCatalog` | 1 | 0 | 0 | 0 | 1 | 7 | 0 |
| Base | `Siemens.Engineering.HW.Systemdiagnostics.Settings` | 2 | 1 | 0 | 0 | 1 | 3 | 2 |
| Base | `Siemens.Engineering.HW.Utilities` | 5 | 0 | 0 | 0 | 5 | 9 | 0 |
| Base | `Siemens.Engineering.Library` | 11 | 4 | 0 | 0 | 7 | 83 | 7 |
| Base | `Siemens.Engineering.Library.Compare` | 6 | 0 | 0 | 0 | 6 | 15 | 0 |
| Base | `Siemens.Engineering.Library.MasterCopies` | 8 | 2 | 0 | 0 | 6 | 20 | 5 |
| Base | `Siemens.Engineering.Library.Types` | 13 | 3 | 0 | 0 | 10 | 65 | 24 |
| Base | `Siemens.Engineering.Multiuser` | 14 | 5 | 0 | 0 | 9 | 47 | 18 |
| Base | `Siemens.Engineering.Online` | 2 | 1 | 0 | 0 | 1 | 15 | 5 |
| Base | `Siemens.Engineering.Online.Configurations` | 5 | 2 | 0 | 0 | 3 | 13 | 2 |
| Base | `Siemens.Engineering.Online.Security` | 2 | 0 | 1 | 0 | 1 | 2 | 0 |
| Base | `Siemens.Engineering.Private` | 1 | 0 | 0 | 0 | 1 | 9 | 0 |
| Base | `Siemens.Engineering.Security` | 12 | 3 | 0 | 0 | 9 | 41 | 6 |
| Base | `Siemens.Engineering.Settings` | 4 | 0 | 0 | 0 | 4 | 7 | 0 |
| Base | `Siemens.Engineering.Umac` | 31 | 12 | 2 | 0 | 17 | 107 | 43 |
| Base | `Siemens.Engineering.Upload` | 5 | 2 | 0 | 0 | 3 | 16 | 3 |
| Base | `Siemens.Engineering.Upload.Configurations` | 5 | 0 | 1 | 0 | 4 | 6 | 0 |
| Base | `Siemens.Engineering.VersionControl` | 11 | 5 | 1 | 0 | 5 | 36 | 21 |
| CFC | `Siemens.Engineering.SW.FunctionCharts` | 2 | 0 | 1 | 0 | 1 | 8 | 0 |
| DCC | `Siemens.Engineering.MC.Drives.Dcc` | 25 | 1 | 1 | 0 | 23 | 97 | 3 |
| DCC | `Siemens.Engineering.MC.Drives.Dcc.DccExceptions` | 39 | 0 | 0 | 0 | 39 | 39 | 0 |
| Safety | `Siemens.Engineering.Safety` | 12 | 11 | 1 | 0 | 0 | 54 | 40 |
| Safety | `Siemens.Engineering.Safety.Download.Configurations` | 1 | 0 | 1 | 0 | 0 | 1 | 0 |
| SafetyValidation | `Siemens.Engineering.SafetyValidation` | 14 | 0 | 0 | 0 | 14 | 60 | 0 |
| Sivarc | `Siemens.Engineering.SiVArc` | 72 | 6 | 1 | 0 | 65 | 314 | 6 |
| Startdrive | `Siemens.Engineering.MC.Drives` | 26 | 1 | 3 | 0 | 22 | 95 | 2 |
| Startdrive | `Siemens.Engineering.MC.Drives.DFI` | 16 | 0 | 0 | 0 | 16 | 51 | 0 |
| Startdrive | `Siemens.Engineering.MC.Drives.SecurityObjects` | 2 | 0 | 0 | 0 | 2 | 4 | 0 |
| Startdrive | `Siemens.Engineering.SW.TechnologicalObjects.Motion` | 23 | 3 | 9 | 0 | 11 | 95 | 13 |
| Step7 | `Siemens.Engineering.Cax` | 4 | 2 | 0 | 0 | 2 | 18 | 12 |
| Step7 | `Siemens.Engineering.SW` | 18 | 4 | 0 | 0 | 14 | 40 | 15 |
| Step7 | `Siemens.Engineering.SW.Alarm` | 8 | 2 | 1 | 0 | 5 | 18 | 4 |
| Step7 | `Siemens.Engineering.SW.Alarm.Exceptions` | 1 | 0 | 0 | 0 | 1 | 1 | 0 |
| Step7 | `Siemens.Engineering.SW.Alarm.TextLists` | 5 | 3 | 0 | 0 | 2 | 13 | 6 |
| Step7 | `Siemens.Engineering.SW.Blocks` | 19 | 8 | 3 | 0 | 8 | 72 | 29 |
| Step7 | `Siemens.Engineering.SW.Blocks.Exceptions` | 1 | 0 | 0 | 0 | 1 | 1 | 0 |
| Step7 | `Siemens.Engineering.SW.Blocks.Interface` | 4 | 2 | 1 | 0 | 1 | 8 | 2 |
| Step7 | `Siemens.Engineering.SW.ExternalSources` | 6 | 1 | 0 | 0 | 5 | 21 | 3 |
| Step7 | `Siemens.Engineering.SW.Loader` | 1 | 1 | 0 | 0 | 0 | 2 | 2 |
| Step7 | `Siemens.Engineering.SW.OpcUa` | 9 | 5 | 1 | 0 | 3 | 42 | 16 |
| Step7 | `Siemens.Engineering.SW.OpcUa.AccessControl` | 7 | 3 | 1 | 0 | 3 | 31 | 6 |
| Step7 | `Siemens.Engineering.SW.Supervision` | 6 | 1 | 1 | 0 | 4 | 13 | 2 |
| Step7 | `Siemens.Engineering.SW.Tags` | 11 | 5 | 0 | 0 | 6 | 62 | 28 |
| Step7 | `Siemens.Engineering.SW.TechnologicalObjects` | 8 | 1 | 0 | 0 | 7 | 27 | 4 |
| Step7 | `Siemens.Engineering.SW.TechnologicalObjects.Ident` | 1 | 1 | 0 | 0 | 0 | 2 | 1 |
| Step7 | `Siemens.Engineering.SW.Types` | 11 | 2 | 1 | 0 | 8 | 41 | 15 |
| Step7 | `Siemens.Engineering.SW.Units` | 8 | 2 | 0 | 0 | 6 | 27 | 2 |
| Step7 | `Siemens.Engineering.SW.WatchAndForceTables` | 11 | 4 | 0 | 0 | 7 | 44 | 16 |
| TeamcenterGateway | `Siemens.Engineering.TeamcenterGateway` | 12 | 0 | 0 | 0 | 12 | 51 | 0 |
| TestSuite | `Siemens.Engineering.TestSuite` | 4 | 0 | 1 | 0 | 3 | 15 | 0 |
| TestSuite | `Siemens.Engineering.TestSuite.ApplicationTest` | 7 | 1 | 0 | 0 | 6 | 24 | 5 |
| TestSuite | `Siemens.Engineering.TestSuite.StyleGuide` | 4 | 1 | 0 | 0 | 3 | 15 | 3 |
| TestSuite | `Siemens.Engineering.TestSuite.SystemTest` | 5 | 1 | 0 | 0 | 4 | 17 | 3 |
| WinCC | `Siemens.Engineering.Hmi.Communication` | 2 | 2 | 0 | 0 | 0 | 5 | 4 |
| WinCC | `Siemens.Engineering.Hmi.Cycle` | 2 | 2 | 0 | 0 | 0 | 6 | 4 |
| WinCC | `Siemens.Engineering.Hmi.Faceplate` | 1 | 1 | 0 | 0 | 0 | 1 | 1 |
| WinCC | `Siemens.Engineering.Hmi.Globalization` | 3 | 0 | 1 | 0 | 2 | 6 | 0 |
| WinCC | `Siemens.Engineering.Hmi.RuntimeScripting` | 8 | 4 | 0 | 0 | 4 | 21 | 5 |
| WinCC | `Siemens.Engineering.Hmi.Screen` | 26 | 3 | 0 | 0 | 23 | 63 | 9 |
| WinCC | `Siemens.Engineering.Hmi.Tag` | 9 | 5 | 0 | 0 | 4 | 28 | 14 |
| WinCC | `Siemens.Engineering.Hmi.TextGraphicList` | 4 | 2 | 0 | 0 | 2 | 10 | 6 |
| WinCC.Extension | `Siemens.Engineering.Hmi` | 3 | 1 | 0 | 0 | 2 | 28 | 6 |
| WinCCUnified | `Siemens.Engineering.HmiUnified` | 1 | 1 | 0 | 0 | 0 | 21 | 7 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Common` | 5 | 1 | 0 | 4 | 0 | 13 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Cpm` | 14 | 3 | 0 | 11 | 0 | 70 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAlarm` | 6 | 0 | 0 | 6 | 0 | 32 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon` | 2 | 1 | 0 | 1 | 0 | 22 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAudit` | 3 | 1 | 0 | 2 | 0 | 9 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiConnections` | 5 | 2 | 0 | 3 | 0 | 21 | 11 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiLogging` | 6 | 0 | 0 | 6 | 0 | 10 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon` | 6 | 0 | 0 | 6 | 0 | 28 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiOpcUaAlarm` | 2 | 0 | 0 | 2 | 0 | 10 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiTags` | 12 | 7 | 0 | 5 | 0 | 74 | 27 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.LoggingTags` | 2 | 0 | 0 | 2 | 0 | 22 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.RuntimeSettings` | 12 | 1 | 0 | 11 | 0 | 82 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Scripts` | 2 | 2 | 0 | 0 | 0 | 7 | 6 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.TextGraphicList` | 6 | 0 | 0 | 6 | 0 | 14 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI` | 1 | 0 | 0 | 1 | 0 | 4 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Base` | 11 | 2 | 0 | 9 | 0 | 53 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Controls` | 15 | 2 | 0 | 13 | 0 | 120 | 3 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization` | 6 | 2 | 0 | 4 | 0 | 16 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Flashing` | 1 | 1 | 0 | 0 | 0 | 4 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Script` | 3 | 1 | 0 | 2 | 0 | 12 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Tag` | 7 | 3 | 0 | 4 | 0 | 20 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Events` | 96 | 0 | 0 | 96 | 0 | 243 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Features` | 13 | 0 | 0 | 13 | 0 | 75 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Parts` | 85 | 0 | 0 | 85 | 0 | 370 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.ScreenGroup` | 2 | 1 | 0 | 1 | 0 | 6 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Screens` | 3 | 2 | 0 | 1 | 0 | 33 | 6 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Shapes` | 20 | 5 | 0 | 15 | 0 | 129 | 7 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Widgets` | 19 | 6 | 0 | 13 | 0 | 133 | 13 |

## 动态覆盖的类型（登记表 scripts/diagnostics/openness-dynamic-coverage.json）

- **DescribeUnifiedScreenItemType**：13 个类型（IHmiArcFeature、IHmiAreaFeature、IHmiAxisFeature、IHmiBasicScreenFeature、IHmiBasicScreenItemFeature、IHmiBoxFeature、IHmiLineFeature、IHmiOperabilityFeature、IHmiRotationFeature、IHmiScaleFeature、IHmiScreenWindowFeature、IHmiTimeRangeFeature、IHmiWindowFeature）
- **ExchangeUnifiedTags (import) / ExchangeUnifiedScriptModules (import) / ImportUnifiedEngineeringList**：1 个类型（ImportResult）
- **ManageUnifiedDynamization**：10 个类型（DynamizationBase、DynamizationBaseComposition、ExpressionDynamization、IHmiScript、ScriptDynamization、MappingTableEntryBase、MappingTableEntryBaseComposition、MappingTableEntryBitmask、MappingTableEntrySimple、TagParameterDynamization）
- **ManageUnifiedEvent / ReadUnifiedObjectEvents**：96 个类型（HmiAlarmControlEventHandler、HmiAlarmControlEventHandlerComposition、HmiAlarmIndicatorEventHandler、HmiAlarmIndicatorEventHandlerComposition、HmiAlarmLineControlEventHandler、HmiAlarmLineControlEventHandlerComposition、HmiBarEventHandler、HmiBarEventHandlerComposition、HmiButtonEventHandler、HmiButtonEventHandlerComposition、HmiCheckBoxGroupEventHandler、HmiCheckBoxGroupEventHandlerComposition、HmiCircleEventHandler、HmiCircleEventHandlerComposition、HmiCircleSegmentEventHandler、HmiCircleSegmentEventHandlerComposition、HmiCircularArcEventHandler、HmiCircularArcEventHandlerComposition、HmiClockEventHandler、HmiClockEventHandlerComposition、HmiComboBoxEventHandler、HmiComboBoxEventHandlerComposition、HmiCustomWebControlContainerEventHandler、HmiCustomWebControlContainerEventHandlerComposition、HmiCustomWidgetContainerEventHandler、HmiCustomWidgetContainerEventHandlerComposition、HmiDetailedParameterControlEventHandler、HmiDetailedParameterControlEventHandlerComposition、HmiDotNetControlContainerEventHandler、HmiDotNetControlContainerEventHandlerComposition、HmiEllipseEventHandler、HmiEllipseEventHandlerComposition、HmiEllipseSegmentEventHandler、HmiEllipseSegmentEventHandlerComposition、HmiEllipticalArcEventHandler、HmiEllipticalArcEventHandlerComposition、HmiFaceplateContainerEventHandler、HmiFaceplateContainerEventHandlerComposition、HmiFunctionTrendControlEventHandler、HmiFunctionTrendControlEventHandlerComposition、HmiGaugeEventHandler、HmiGaugeEventHandlerComposition、HmiGraphicViewEventHandler、HmiGraphicViewEventHandlerComposition、HmiIOFieldEventHandler、HmiIOFieldEventHandlerComposition、HmiLineEventHandler、HmiLineEventHandlerComposition、HmiListBoxEventHandler、HmiListBoxEventHandlerComposition、HmiMediaControlEventHandler、HmiMediaControlEventHandlerComposition、HmiPolygonEventHandler、HmiPolygonEventHandlerComposition、HmiPolylineEventHandler、HmiPolylineEventHandlerComposition、HmiProcessControlEventHandler、HmiProcessControlEventHandlerComposition、HmiProcessDiagnosisGraphOverviewControlEventHandler、HmiProcessDiagnosisGraphOverviewControlEventHandlerComposition、HmiProcessDiagnosisOverviewControlEventHandler、HmiProcessDiagnosisOverviewControlEventHandlerComposition、HmiProcessDiagnosisPlcCodeViewerControlEventHandler、HmiProcessDiagnosisPlcCodeViewerControlEventHandlerComposition、HmiProDiagCriteriaAnalysisControlEventHandler、HmiProDiagCriteriaAnalysisControlEventHandlerComposition、HmiRadioButtonGroupEventHandler、HmiRadioButtonGroupEventHandlerComposition、HmiRectangleEventHandler、HmiRectangleEventHandlerComposition、HmiScreenEventHandler、HmiScreenEventHandlerComposition、HmiScreenWindowEventHandler、HmiScreenWindowEventHandlerComposition、HmiSliderEventHandler、HmiSliderEventHandlerComposition、HmiSymbolicIOFieldEventHandler、HmiSymbolicIOFieldEventHandlerComposition、HmiSystemDiagnosisControlEventHandler、HmiSystemDiagnosisControlEventHandlerComposition、HmiTextBoxEventHandler、HmiTextBoxEventHandlerComposition、HmiTextEventHandler、HmiTextEventHandlerComposition、HmiToggleSwitchEventHandler、HmiToggleSwitchEventHandlerComposition、HmiTouchAreaEventHandler、HmiTouchAreaEventHandlerComposition、HmiTrendCompanionEventHandler、HmiTrendCompanionEventHandlerComposition、HmiTrendControlEventHandler、HmiTrendControlEventHandlerComposition、HmiWebControlEventHandler、HmiWebControlEventHandlerComposition、PropertyEventHandler、PropertyEventHandlerComposition）
- **ManageUnifiedHmiGroup (family=screens) / ManageUnifiedScreenLayout**：1 个类型（HmiScreenGroupComposition）
- **ManageUnifiedLoggingTag**：2 个类型（HmiLoggingTag、HmiLoggingTagComposition）
- **ManageUnifiedObjectParts (collectionProperty=Thresholds)**：2 个类型（HmiThreshold、HmiThresholdComposition）
- **ManageUnifiedObjectParts / ManageUnifiedScreenItem**：85 个类型（HmiAlarmColumnPart、HmiAlarmLineColumnPart、HmiAlarmLineColumnPartComposition、HmiAlarmLineViewPart、HmiAlarmStatePart、HmiAlarmStatisticColumnPart、HmiContentPart、HmiControlBarButtonPart、HmiControlBarDisplayPart、HmiControlBarElementPartBase、HmiControlBarElementPartBaseComposition、HmiControlBarLabelPart、HmiControlBarPartBase、HmiControlBarTextBoxPart、HmiControlBarToggleSwitchPart、HmiCornersPart、HmiCurvedScalePart、HmiCustomControlInterface、HmiCustomControlInterfaceComposition、HmiDataGridColumnHeaderPart、HmiDataGridColumnPart、HmiDataGridColumnPartBase、HmiDataGridColumnPartBaseComposition、HmiDataGridHeaderSettingsPart、HmiDataGridViewPart、HmiDataSourcePart、HmiDetailedParameterControlColumnPart、HmiFaceplateInterface、HmiFaceplateInterfaceComposition、HmiFontPart、HmiFunctionTrendAreaPart、HmiFunctionTrendAreaPartComposition、HmiFunctionTrendPart、HmiFunctionTrendPartComposition、HmiGraphOverviewControlColumnPart、HmiHelpLinePart、HmiHelpLinePartComposition、HmiInputBehaviorPart、HmiLegendPart、HmiLinearMovementPart、HmiMatrixViewPart、HmiOverviewParameterControlColumnPart、HmiPaddingPart、HmiPlcDataSourcePart、HmiPressedStateTagPart、HmiPressedStateTagPartComposition、HmiProcessColumnPart、HmiProcessDiagnosisCriteriaAnalysisControlColumnPart、HmiProcessDiagnosisOperationModePart、HmiProcessDiagnosisOverviewElementPart、HmiProcessDiagnosisOverviewElementPartComposition、HmiProcessDiagnosisOverviewPart、HmiQualityPart、HmiRulerPart、HmiScalePartBase、HmiScalingEntryPart、HmiScalingEntryPartComposition、HmiSelectionItemPart、HmiSelectionItemPartComposition、HmiStraightScalePart、HmiSystemDiagnosisControlColumnPart、HmiSystemDiagnosisControlScriptColumnPart、HmiSystemDiagnosisDetailViewPart、HmiSystemDiagnosisHardwareDetailPart、HmiSystemDiagnosisHardwareDetailPartComposition、HmiSystemDiagnosisMatrixColumnPart、HmiTextPart、HmiThresholdPart、HmiThresholdPartComposition、HmiTimeAxisPart、HmiTimeAxisPartComposition、HmiTimeRangeColumnPart、HmiToolBarPart、HmiTrendAreaPart、HmiTrendAreaPartBase、HmiTrendAreaPartComposition、HmiTrendColumnPart、HmiTrendPart、HmiTrendPartBase、HmiTrendPartComposition、HmiValueAxisPartBase、HmiXValueAxisPart、HmiXValueAxisPartComposition、HmiYValueAxisPart、HmiYValueAxisPartComposition）
- **ManageUnifiedScreenItem**：10 个类型（HmiCompanionBase、HmiContainerBase、HmiControlWindowBase、HmiCustomWebControlContainer、HmiCustomWidgetContainer、HmiScreenItemBaseComposition、HmiSimpleScreenItemBase、HmiTrendControlBase、HmiWindowBase、HmiScreenWindow）
- **ManageUnifiedScreenItem / DescribeUnifiedScreenItemType**：28 个类型（HmiCentricShapeBase、HmiCircularArc、HmiCircularShapeBase、HmiEllipse、HmiEllipticalArc、HmiEllipticalShapeBase、HmiGraphicView、HmiLine、HmiPoint、HmiPointBasedShapeBase、HmiPointComposition、HmiPolygon、HmiPolyline、HmiShapeBase、HmiSurfaceShapeBase、HmiAlarmIndicator、HmiBar、HmiCheckBoxGroup、HmiClock、HmiGauge、HmiListBox、HmiRadioButtonGroup、HmiScaleWidgetBase、HmiSelectionGroupBase、HmiSymbolicIOField、HmiTextBox、HmiTextWidgetBase、HmiTouchArea）
- **ManageUnifiedScreenItem / DescribeUnifiedScreenItemType / ManageUnifiedObjectParts**：13 个类型（HmiAlarmControl、HmiAlarmLineControl、HmiDetailedParameterControl、HmiFunctionTrendControl、HmiMediaControl、HmiProcessControl、HmiProcessDiagnosisCriteriaAnalysisControl、HmiProcessDiagnosisGraphOverviewControl、HmiProcessDiagnosisOverviewControl、HmiProcessDiagnosisPlcCodeViewerControl、HmiTrendCompanion、HmiTrendControl、HmiWebControl）
- **ManageUnifiedScreenItem / ManageUnifiedScreenLayout**：1 个类型（UIBase）
- **ManageUnifiedScreenItem / ReadUnifiedObjectProperties**：1 个类型（HmiBase）
- **ReadUnifiedAlarmCommon / ManageUnifiedEngineeringObject / UpdateUnifiedObjectProperties / UpdateUnifiedMultilingualProperty**：7 个类型（HmiAlarmClass、HmiAlarmClassComposition、AlarmStatusVisuals、HmiAnalogAlarm、HmiAnalogAlarmComposition、HmiDiscreteAlarm、HmiDiscreteAlarmComposition）
- **ReadUnifiedAuditSettings / UpdateUnifiedObjectProperties**：2 个类型（HmiAlarmAuditClassComposition、HmiAuditClass）
- **ReadUnifiedEngineeringObjects (category=opcUaAlarmTypes) / ManageUnifiedEngineeringObject**：2 个类型（HmiOpcUaAlarmType、HmiOpcUaAlarmTypeComposition）
- **ReadUnifiedEngineeringObjects (category=systemTags)**：2 个类型（HmiSystemTag、HmiSystemTagComposition）
- **ReadUnifiedEngineeringObjects (textLists / graphicLists / systemTextLists) / ManageUnifiedEngineeringObject / ExportUnifiedEngineeringList / ImportUnifiedEngineeringList / ManageUnifiedListEntries**：6 个类型（HmiGraphicList、HmiGraphicListComposition、HmiSystemTextList、HmiSystemTextListComposition、HmiTextList、HmiTextListComposition）
- **ReadUnifiedEngineeringObjects / ManageUnifiedEngineeringObject / UpdateUnifiedObjectProperties / SetUnifiedLogDuration / ReadUnifiedAuditSettings**：12 个类型（HmiAlarmLog、HmiAlarmLogComposition、HmiAuditTrail、HmiAuditTrailComposition、HmiDataLog、HmiDataLogComposition、LogBackup、LogDuration、LoggingBase、LogSegment、LogSettings、SegmentDuration）
- **ReadUnifiedObjectProperties / ManageUnifiedObjectParts (collectionProperty=DriverProperties) / ImportUnifiedOpcUaAlarms**：3 个类型（DriverProperty、DriverPropertyComposition、HmiConnectionComposition）
- **ReadUnifiedObjectProperties / UpdateUnifiedObjectProperties**：1 个类型（HmiSubstituteValue）
- **ReadUnifiedObjectProperties / UpdateUnifiedObjectProperties / ReadUnifiedRuntimeSettings**：11 个类型（HmiExclusiveOperationSettings、HmiLanguageAndFont、HmiLanguageAndFontAssociation、HmiMaxLoginRuntimeSettings、HmiOpcUaServerRuntimeSettings、HmiProcessDiagnosticsRuntimeSettings、HmiReportingSettings、HmiRuntimeResourceSettings、HmiTelemetryRuntimeSettings、HmiUnifiedTagSettings、HmiUpssRuntimeSettings）
- **ReadUnifiedPlantObject / ManageUnifiedPlantNode / UpdateUnifiedPlantObject**：11 个类型（PlantObjectInterface、PlantObjectInterfaceComposition、PlantObjectInterfaceMember、PlantObjectInterfaceMemberComposition、PlantObjectLoggingTag、PlantObjectLoggingTagComposition、PlantObjectTagRange、PlantObjectTagSubstituteValue、PlantViewComposition、PlantViewNode、PlantViewNodeComposition）
- **ValidateUnifiedObject**：2 个类型（HmiValidationResult、IValidator）

完整的未触及类型及其成员清单体积过大，不入库；在放置了 PublicAPI 的机器上运行下列命令即可重新生成到被忽略的 `bin-build/audits/` 目录（`-SummaryMarkdown` 输出含逐类型列表）：

```powershell
.\scripts\diagnostics\Audit-OpennessCoverage.ps1 -PublicApiDirectory D:\TIA_PublicAPI\TIA_V21_PublicAPI\V21\net48 -Version V21 -SummaryMarkdown .\bin-build\audits\V21-summary.md
```

V21 Update 或新版本安装后重跑并 diff 本页统计表，即为路线图要求的"再核对"步骤。
