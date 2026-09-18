# 官方 Openness API 覆盖清单（V21，2.7.24）

[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [路线图](../development/roadmap.md) · [Openness 限制](../troubleshooting/openness-limitations.md)

本页回答"西门子官方 Openness API 有、而本项目没有专用封装的是什么"。数据由 [scripts/diagnostics/Audit-OpennessCoverage.ps1](../../scripts/diagnostics/Audit-OpennessCoverage.ps1) 对本机 V21 PublicAPI 的 18 个官方 XML 文档（不含 AddIn.*）与引擎源码逐成员对照生成；官方 XML 与 DLL 不随仓库分发。本次盘点对应 2.7.24 引擎源码（2026-09-18）；上一次为 2.7.18。

## 口径（先读这个，再看数字）

- 分母是 **4,490 个领域成员**（1,239 个方法 + 3,251 个属性），已剔除每个类型都有的 `ToString`/`Equals`/`GetHashCode`/`Parent`/`GetAttribute(s)`/`SetAttribute(s)`/集合枚举等 10,294 个样板成员、构造函数和显式接口实现。
- 判定是**词法**的：方法记为"已引用"须同时满足所属类型简名出现在源码、且 `.方法名(` 出现在源码。这只证明"有专用代码路径"，不证明 MCP 工具已完整封装该能力；反过来，**"未触及"不等于"不可用"**——`DescribeObject` / `InvokeObject` / `InvokeService` 等通用反射工具可以动态到达任何公开成员，而且 2.7.18 的 Unified 事件/部件、库比较等工具是按官方 `GetCompositionInfos` / 泛型 `Create<T>` 等动态接口实现的，类型名以字符串出现，词法扫描看不到（例如 `HmiUnified.UI.Events` 仍显示 0 引用，但 `ReadUnifiedObjectEvents` 会枚举全部 96 种事件类型）。
- 因此下面分两层：**统计表**如实给出词法结果；**状态表**只列同时经工具清单与人工核对的结论。
- 结果（V21，2.7.24）：领域成员已引用 **621**（2.7.18 为 609，2.7.17 为 426）、仅类型名 776、未引用 3,093；有领域成员的类型 1,217 个中有专用引用 **212**（2.7.18 为 208）、仅类型名 110、完全未触及 895。2.7.19–2.7.24 的改动集中在渲染器、配置器与 PLCSIM Advanced 通道，API 封装面几乎未变。属性侧数字被"通用属性读写"显著低估，方法侧更接近真实封装度。

## 缺口结构（2.7.24）

895 个"完全未触及"的类型里，`*Composition` / `*Association` / `*EventArgs` / `*Exception` / `*Result` / `*Info` 这类集合与结果壳子 333 个，Unified `UI.Events`（96）与 `UI.Parts`（85）经 `GetCompositionInfos` / 泛型 `Create<T>` 动态覆盖而词法不可见。去掉这三类后，**没有专用封装的功能类型为 381 个 / 1,753 个成员**，其中核心程序集（Base / Step7 / WinCC / WinCCUnified / Safety）273 个 / 1,231 个，选件包（SiVArc / Startdrive / DCC / Test Suite / SafetyValidation / Teamcenter / CFC）108 个 / 522 个。

| 程序集 | 未封装功能类型 | 成员 | 具体是什么 |
|---|---:|---:|---|
| WinCCUnified | 113 | 685 | 画面对象属性（`UI.Shapes` 18 / `UI.Widgets` 16 / `UI.Controls` 13 / `UI.Features` 13）、运行时设置（`HmiOpcUaServerRuntimeSettings` 23、`HmiLanguageAndFont` 9、诊断/报告/登录/遥测）、报警类与离散/模拟报警（`HmiAlarm` 32）、报警/数据记录与审计（`HmiLogging` 38）、文本/图形列表（14）、`HmiScreenWindow`（16）、阈值/替代值/系统变量、`Cpm`（11 类型） |
| Base | 85 | 310 | HW 深层：`TransferArea` / `MulticastableTransferArea`、`MrpInstance`、`SyncDomain`、`IoSystem` / `IoConnector`、`Channel`、`WebserverUser`、`DeviceGroup`（约 100 个成员）；`GlobalLibrary`(30) / `ILibrary` / 类型版本文件夹与 `UpdateCheck`；Umac 的 `UmcUser` / `UmcUserGroup` / `UmcCredentials`；Security 的 `SyslogServer`、`CertificateTemplate`、`PlcPasswordPolicyService`；`TiaPortalSession` / `Transaction` / 事件参数；`Compare` 结果元素；跨引用 `SourceObject` / `ReferenceObject` |
| Step7 | 41 | 136 | 软件单元 `SW.Units`（27）、`PlcDocument*` 与 `PlcChecksumProvider` / `PlcSimulationSettingsProvider`、`PlcBlockWriteProtectionProvider`、系统块/类型组、`PlcForceTableEntry`（刻意不注册）、报警类导入导出结果、`OpcUaCommunicationGroup` |
| WinCC（经典） | 24 | 59 | 弹出画面 / 滑入画面 / 模板的文件夹层次（23）、变量文件夹 |
| Safety | 10 | 41 | 已有的 `ManagePlcSafety(action=read)` 经反射读取 `SafetyAdministration` / `SafetySettings` / `RuntimeGroup` 的标量与 `ProgramSignatures`（词法扫描看不到）。仍缺：`SafetySettings.AssignmentOfBlockNumbers`（F 块号范围）与 `SafetySystemVersion` 等嵌套对象、工程级 `GlobalSettings`、逐块的 `SafetySignatureProvider.Signatures`、官方安全打印输出 `SafetyPrintout.Print`；`GenerateAcceptanceReport` 也未纳入 Safety。见路线图 P1 |
| 选件包 | 108 | 522 | Startdrive `MC.Drives`(22 类型) + `DFI`(16)、DCC 图表（62，其中 39 个是异常类）、SiVArc（65）、Test Suite（16）、SafetyValidation（14）、Teamcenter（12） |

已核实为**官方没有 API** 而保持明确拒绝的项不计入缺口，见下文"已核实为官方无 API 的项"。"未封装"不等于"用不了"：`DescribeObject` / `GetObjectProperty` / `InvokeObject` / `InvokeService` 可到达任何公开成员，只是没有带校验、预览与文档的专用工具。

## 能力域状态（2.7.18，此后无 API 侧变化）

| 能力域 | 官方入口（V21 XML） | 2.7.17 | 2.7.18 |
|---|---|---|---|
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

## 统计表（脚本生成，2.7.24 源码）

| 程序集 | 命名空间 | 类型数 | 有专用引用 | 仅类型名 | 完全未触及 | 成员数 | 已引用成员 |
|---|---|---:|---:|---:|---:|---:|---:|
| Base | `Siemens.Engineering` | 59 | 13 | 6 | 40 | 185 | 47 |
| Base | `Siemens.Engineering.AdvancedProtection` | 1 | 1 | 0 | 0 | 3 | 3 |
| Base | `Siemens.Engineering.Compare` | 3 | 0 | 0 | 3 | 7 | 0 |
| Base | `Siemens.Engineering.Compiler` | 5 | 2 | 0 | 3 | 14 | 5 |
| Base | `Siemens.Engineering.Connection` | 14 | 5 | 0 | 9 | 34 | 15 |
| Base | `Siemens.Engineering.CrossReference` | 8 | 2 | 0 | 6 | 28 | 6 |
| Base | `Siemens.Engineering.CustomIdentity` | 2 | 1 | 1 | 0 | 4 | 3 |
| Base | `Siemens.Engineering.Download` | 5 | 2 | 0 | 3 | 21 | 8 |
| Base | `Siemens.Engineering.Download.Configurations` | 31 | 3 | 24 | 4 | 32 | 3 |
| Base | `Siemens.Engineering.FingerprintData` | 4 | 2 | 0 | 2 | 6 | 4 |
| Base | `Siemens.Engineering.HW` | 63 | 13 | 1 | 49 | 204 | 45 |
| Base | `Siemens.Engineering.HW.CommunicationConnections` | 10 | 2 | 8 | 0 | 90 | 3 |
| Base | `Siemens.Engineering.HW.CustomDataTypes` | 3 | 0 | 0 | 3 | 4 | 0 |
| Base | `Siemens.Engineering.HW.Extensions` | 1 | 1 | 0 | 0 | 2 | 2 |
| Base | `Siemens.Engineering.HW.Features` | 43 | 14 | 29 | 0 | 111 | 18 |
| Base | `Siemens.Engineering.HW.HardwareCatalog` | 1 | 0 | 0 | 1 | 7 | 0 |
| Base | `Siemens.Engineering.HW.Systemdiagnostics.Settings` | 2 | 1 | 0 | 1 | 3 | 2 |
| Base | `Siemens.Engineering.HW.Utilities` | 5 | 0 | 0 | 5 | 9 | 0 |
| Base | `Siemens.Engineering.Library` | 11 | 4 | 0 | 7 | 83 | 7 |
| Base | `Siemens.Engineering.Library.Compare` | 6 | 0 | 0 | 6 | 15 | 0 |
| Base | `Siemens.Engineering.Library.MasterCopies` | 8 | 2 | 0 | 6 | 20 | 5 |
| Base | `Siemens.Engineering.Library.Types` | 13 | 3 | 0 | 10 | 65 | 24 |
| Base | `Siemens.Engineering.Multiuser` | 14 | 5 | 0 | 9 | 47 | 18 |
| Base | `Siemens.Engineering.Online` | 2 | 1 | 0 | 1 | 15 | 5 |
| Base | `Siemens.Engineering.Online.Configurations` | 5 | 2 | 0 | 3 | 13 | 2 |
| Base | `Siemens.Engineering.Online.Security` | 2 | 0 | 1 | 1 | 2 | 0 |
| Base | `Siemens.Engineering.Private` | 1 | 0 | 0 | 1 | 9 | 0 |
| Base | `Siemens.Engineering.Security` | 12 | 3 | 0 | 9 | 41 | 6 |
| Base | `Siemens.Engineering.Settings` | 4 | 0 | 0 | 4 | 7 | 0 |
| Base | `Siemens.Engineering.Umac` | 31 | 12 | 2 | 17 | 107 | 43 |
| Base | `Siemens.Engineering.Upload` | 5 | 2 | 0 | 3 | 16 | 3 |
| Base | `Siemens.Engineering.Upload.Configurations` | 5 | 0 | 1 | 4 | 6 | 0 |
| Base | `Siemens.Engineering.VersionControl` | 11 | 5 | 1 | 5 | 36 | 21 |
| CFC | `Siemens.Engineering.SW.FunctionCharts` | 2 | 0 | 1 | 1 | 8 | 0 |
| DCC | `Siemens.Engineering.MC.Drives.Dcc` | 25 | 1 | 1 | 23 | 97 | 3 |
| DCC | `Siemens.Engineering.MC.Drives.Dcc.DccExceptions` | 39 | 0 | 0 | 39 | 39 | 0 |
| Safety | `Siemens.Engineering.Safety` | 12 | 1 | 0 | 11 | 54 | 3 |
| Safety | `Siemens.Engineering.Safety.Download.Configurations` | 1 | 0 | 0 | 1 | 1 | 0 |
| SafetyValidation | `Siemens.Engineering.SafetyValidation` | 14 | 0 | 0 | 14 | 60 | 0 |
| Sivarc | `Siemens.Engineering.SiVArc` | 72 | 6 | 1 | 65 | 314 | 6 |
| Startdrive | `Siemens.Engineering.MC.Drives` | 26 | 1 | 3 | 22 | 95 | 2 |
| Startdrive | `Siemens.Engineering.MC.Drives.DFI` | 16 | 0 | 0 | 16 | 51 | 0 |
| Startdrive | `Siemens.Engineering.MC.Drives.SecurityObjects` | 2 | 0 | 0 | 2 | 4 | 0 |
| Startdrive | `Siemens.Engineering.SW.TechnologicalObjects.Motion` | 23 | 3 | 9 | 11 | 95 | 13 |
| Step7 | `Siemens.Engineering.Cax` | 4 | 2 | 0 | 2 | 18 | 12 |
| Step7 | `Siemens.Engineering.SW` | 18 | 4 | 0 | 14 | 40 | 15 |
| Step7 | `Siemens.Engineering.SW.Alarm` | 8 | 2 | 1 | 5 | 18 | 4 |
| Step7 | `Siemens.Engineering.SW.Alarm.Exceptions` | 1 | 0 | 0 | 1 | 1 | 0 |
| Step7 | `Siemens.Engineering.SW.Alarm.TextLists` | 5 | 3 | 0 | 2 | 13 | 6 |
| Step7 | `Siemens.Engineering.SW.Blocks` | 19 | 8 | 3 | 8 | 72 | 29 |
| Step7 | `Siemens.Engineering.SW.Blocks.Exceptions` | 1 | 0 | 0 | 1 | 1 | 0 |
| Step7 | `Siemens.Engineering.SW.Blocks.Interface` | 4 | 2 | 1 | 1 | 8 | 2 |
| Step7 | `Siemens.Engineering.SW.ExternalSources` | 6 | 1 | 0 | 5 | 21 | 3 |
| Step7 | `Siemens.Engineering.SW.Loader` | 1 | 1 | 0 | 0 | 2 | 2 |
| Step7 | `Siemens.Engineering.SW.OpcUa` | 9 | 5 | 1 | 3 | 42 | 16 |
| Step7 | `Siemens.Engineering.SW.OpcUa.AccessControl` | 7 | 3 | 1 | 3 | 31 | 6 |
| Step7 | `Siemens.Engineering.SW.Supervision` | 6 | 1 | 1 | 4 | 13 | 2 |
| Step7 | `Siemens.Engineering.SW.Tags` | 11 | 5 | 0 | 6 | 62 | 28 |
| Step7 | `Siemens.Engineering.SW.TechnologicalObjects` | 8 | 1 | 0 | 7 | 27 | 4 |
| Step7 | `Siemens.Engineering.SW.TechnologicalObjects.Ident` | 1 | 1 | 0 | 0 | 2 | 1 |
| Step7 | `Siemens.Engineering.SW.Types` | 11 | 2 | 1 | 8 | 41 | 15 |
| Step7 | `Siemens.Engineering.SW.Units` | 8 | 2 | 0 | 6 | 27 | 2 |
| Step7 | `Siemens.Engineering.SW.WatchAndForceTables` | 11 | 4 | 0 | 7 | 44 | 16 |
| TeamcenterGateway | `Siemens.Engineering.TeamcenterGateway` | 12 | 0 | 0 | 12 | 51 | 0 |
| TestSuite | `Siemens.Engineering.TestSuite` | 4 | 0 | 1 | 3 | 15 | 0 |
| TestSuite | `Siemens.Engineering.TestSuite.ApplicationTest` | 7 | 1 | 0 | 6 | 24 | 5 |
| TestSuite | `Siemens.Engineering.TestSuite.StyleGuide` | 4 | 1 | 0 | 3 | 15 | 3 |
| TestSuite | `Siemens.Engineering.TestSuite.SystemTest` | 5 | 1 | 0 | 4 | 17 | 3 |
| WinCC | `Siemens.Engineering.Hmi.Communication` | 2 | 2 | 0 | 0 | 5 | 4 |
| WinCC | `Siemens.Engineering.Hmi.Cycle` | 2 | 2 | 0 | 0 | 6 | 4 |
| WinCC | `Siemens.Engineering.Hmi.Faceplate` | 1 | 1 | 0 | 0 | 1 | 1 |
| WinCC | `Siemens.Engineering.Hmi.Globalization` | 3 | 0 | 1 | 2 | 6 | 0 |
| WinCC | `Siemens.Engineering.Hmi.RuntimeScripting` | 8 | 4 | 0 | 4 | 21 | 5 |
| WinCC | `Siemens.Engineering.Hmi.Screen` | 26 | 3 | 0 | 23 | 63 | 9 |
| WinCC | `Siemens.Engineering.Hmi.Tag` | 9 | 5 | 0 | 4 | 28 | 14 |
| WinCC | `Siemens.Engineering.Hmi.TextGraphicList` | 4 | 2 | 0 | 2 | 10 | 6 |
| WinCC.Extension | `Siemens.Engineering.Hmi` | 3 | 1 | 0 | 2 | 28 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified` | 1 | 1 | 0 | 0 | 21 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Common` | 5 | 0 | 0 | 5 | 13 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Cpm` | 14 | 3 | 0 | 11 | 70 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAlarm` | 6 | 0 | 0 | 6 | 32 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon` | 2 | 1 | 1 | 0 | 22 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiAudit` | 3 | 1 | 0 | 2 | 9 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiConnections` | 5 | 1 | 0 | 4 | 21 | 6 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiLogging` | 6 | 0 | 0 | 6 | 10 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon` | 6 | 0 | 2 | 4 | 28 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiOpcUaAlarm` | 2 | 0 | 0 | 2 | 10 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.HmiTags` | 12 | 4 | 0 | 8 | 74 | 19 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.LoggingTags` | 2 | 0 | 0 | 2 | 22 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.RuntimeSettings` | 12 | 1 | 0 | 11 | 82 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.Scripts` | 2 | 1 | 0 | 1 | 7 | 3 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.TextGraphicList` | 6 | 0 | 0 | 6 | 14 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI` | 1 | 0 | 0 | 1 | 4 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Base` | 11 | 1 | 0 | 10 | 53 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Controls` | 15 | 1 | 1 | 13 | 120 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization` | 6 | 2 | 2 | 2 | 16 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Flashing` | 1 | 1 | 0 | 0 | 4 | 1 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Script` | 3 | 1 | 1 | 1 | 12 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Dynamization.Tag` | 7 | 3 | 2 | 2 | 20 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Events` | 96 | 0 | 0 | 96 | 243 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Features` | 13 | 0 | 0 | 13 | 75 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Parts` | 85 | 0 | 0 | 85 | 370 | 0 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.ScreenGroup` | 2 | 1 | 0 | 1 | 6 | 4 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Screens` | 3 | 2 | 0 | 1 | 33 | 5 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Shapes` | 20 | 1 | 1 | 18 | 129 | 2 |
| WinCCUnified | `Siemens.Engineering.HmiUnified.UI.Widgets` | 19 | 3 | 0 | 16 | 133 | 7 |


完整的未触及类型及其成员清单体积过大，不入库；在放置了 PublicAPI 的机器上运行下列命令即可重新生成到被忽略的 `bin-build/audits/` 目录（`-SummaryMarkdown` 输出含逐类型列表）：

```powershell
.\scripts\diagnostics\Audit-OpennessCoverage.ps1 -PublicApiDirectory D:\TIA_PublicAPI\TIA_V21_PublicAPI\V21\net48 -Version V21 -SummaryMarkdown .\bin-build\audits\V21-summary.md
```

V21 Update 或新版本安装后重跑并 diff 本页统计表，即为路线图要求的"再核对"步骤。
