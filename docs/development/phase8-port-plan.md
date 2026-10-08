# P8-01 迁移清单与方案：V20/V21 独有工具迁入 Foundation 适配器

研究产物（只读调查，未改动仓库其他文件，未构建、未连接 TIA/VM）。日期 2026-10-08，基于 master `5623a784` 的当前工作区。
上游任务：[重构计划](refactor-plan.md) 阶段 8 P8-01；背景见 [路线图](roadmap.md)“阶段 8”与 [差距复查](gap-review-2026-10.md)。

## 0. 摘要

| 项 | 结论 |
|---|---|
| 范围 | `manifest/contracts/v4/baseline/20.json`、`21.json` 中不在 `19.json` 的工具名：**431 个**（V20 420、V21 431；V21 独有 11 个，V20 无独有）。核对：V19 67、V20 487、V21 498 个工具；487−67=420，498−67=431；19 ⊂ 20 ⊂ 21 |
| 分族 | **35 个能力族**（F01–F35），每个工具恰属一族；附录 A 逐行列出 431 个工具，合计与基线一致 |
| 执行位置 | 按 `src/Logic/ModelContextProtocol/ToolExecution.cs`：29 个已在 Foundation 主机执行（host），402 个在引擎 worker 内由引擎代码执行（worker） |
| 无 Openness 依赖的族 | F01–F04（83 个工具）：宿主元工具/诊断报告、PLC 离线分析、HMI 离线设计、运行时通道（S7/Web API/OPC UA 客户端/Open Pipe/PLCSIM Adv） |
| 旧版 API 全六版齐全（yes） | F19 硬件地址、F21 CAx、F25 Classic HMI、F35 反射；另 F01/F02/F04 无 API 依赖 |
| 旧版 no 的分布 | F26–F28 Unified 在 14sp1–18（HmiUnified 19+）；F32 CFC 在 14sp1–17；F14 报警、F30 Safety 在 14sp1–15.1；F07、F15、F16、F29、F31 在 14sp1 |
| 样板族（P8-02） | **F19 硬件地址**（5 个工具，读写兼有，八版 API 齐全，含一处真实的版本 API 差异，建立后续硬件族共用的定位/读回基础） |
| 批次 | B1 F01+F02+F03（无 Openness，可与 P8-02 并行）→ 样板 F19 → B2 F18+F20+F21 → B3 F08+F09+F11+F17 → B4 F12+F13+F16 → B5 F14+F15+F10 → B6 F06+F35+F23 → B7 F24+F25 → B8 F05+F07+F33 → B9 F22+F04 → B10 F26+F27+F28 → B11 F29+F30+F31 → B12 F32+F34 |
| 关键前提 | ①V20/V21 契约来源须脱离引擎（当前由引擎 `--write-tool-catalog` 生成）；②适配器不得引用 JSON 库（`Check-AdapterBoundary`），引擎服务直接构造 `JsonObject`，须改为 DTO + 宿主映射；③V20/V21 的迁移前真机证据（P7-07）是“迁移后复验”的对照基线 |
| 主要未知 | 成功路径响应形状无离线基线；Put/Get 等属性名驱动的能力；可选产品在各 VM 的安装与许可；在线设置/重置 PLC 主密钥无专用 API 证据；通信连接参数可写性；旧版 XML/AML/Classic 包格式；Unified 反射代码在 V19 上的成员差异 |

### 方法与证据口径

- **工具与实现**：工具名取自三份基线；实现文件取 `McpServerTool(Name = …)` 所在的 `src/Engine/ModelContextProtocol/Tools/*.cs`（431 个全部找到、无重复）；服务按 `*Tools → *Service（src/Engine/Siemens/Services）→ Portal.*（src/Engine/Siemens/Portal）` 追踪；操作分类取 `manifest/tools-list.json`。
- **API 证据（两路独立）**：①本地 XML 文档成员 id（`<member name="T:/M:/P:…">`）；②同目录 DLL 的程序集元数据（`System.Reflection.Metadata` 只读解析 TypeDef/MethodDef/PropertyDef 与基类/接口链，只计 public/nested public 类型；不加载程序集、不启动 TIA）。文件见附录 B 首表。
  - 标记：`Y` XML 与元数据均有；`m` 仅元数据（如泛型 `GetService``1`，XML id 带 arity 后缀）；`x` 仅 XML；`.` 均无。
  - 成员按继承链判断（如 V17 起 `Project.LanguageSettings` 由 `ProjectBase` 提供）。同简名类型（如 14sp1 `Siemens.Engineering.Startdrive.DriveParameter`）不视为等价。
- **矩阵口径**：以 V20 的 API 面为参照（V21 独有 API/工具不算旧版缺口）。`yes` 族内各工具所需关键成员齐全；`partial` 族可迁但部分工具或动作缺 API（应在原生调用前按动作拒绝）；`no` 族核心入口缺失；`unknown` 无法用元数据证明。
  **API 存在不等于许可证、可选组件已安装或行为正确**；所有在旧版启用的族保持 `current / NOT RUN`，直到该版真机验收。不按工具名、相邻版本或 API 名称推断。

## 1. 能力族清单

操作缩写：R=READ、W=WRITE、F=FILE、Off=OFFLINE、On=ONLINE、OnW=ONLINE-WRITE、X=EXECUTE、S=SESSION。执行位置：host=Foundation 主机内（net10），worker=引擎 worker 内（net48）。
“主要服务/类”只列族专用文件；`Portal.cs`、`Portal.Helpers.cs`、`Portal.Software.cs`、`Portal.SessionResolvers.cs`、`EngineeringSessionHelpers.cs`、`IEngineeringSession.cs` 等跨族共用部分见第 3 节。

| 族 | 名称 | V20/V21 | 操作构成 | 执行位置 | V21 独有 | 引擎工具文件 | 主要服务/类（src/Engine/…） |
|---|---|---:|---|---|---|---|---|
| F01 | 宿主元工具与诊断报告（无 Openness 原生调用） | 28/28 | R11 W4 F4 Off3 X2 S4 | host20 worker8 | — | DiagnosticsTools, EcosystemTools, EngineeringDiagnosticsTools, ExportTools, McpServer.Batch, McpServer.Doctor, McpServer.Maintenance, McpServer.ToolBridge, McpServer.Worker, OfflineSuiteTools, V21EcosystemTools | — |
| F02 | PLC 离线分析、文档与模板（无 TIA） | 16/16 | R3 F6 Off7 | host6 worker10 | — | EcosystemTools, EngineeringDiagnosticsTools, OfflineAnalysisTools, PlcBlocksTools, PlcDocumentationTools, PlcExternalSourcesTools, QualityAuditTools, TemplateTools, V21EcosystemTools | — |
| F03 | HMI 离线设计、包与校验（无 TIA） | 20/20 | R2 F2 Off12 X4 | host3 worker17 | — | LibraryTools, OfflineSuiteTools, UnifiedHmiTools, V21EcosystemTools, XmlBuilderTools | — |
| F04 | 运行时通道与仿真（S7/Web API/OPC UA 客户端/Open Pipe/PLCSIM Adv） | 19/19 | R2 On10 OnW6 X1 | worker19 | — | EcosystemTools, PlcSimAdvancedTools, RuntimeChannelTools, RuntimeTools | Runtime/PlcSimAdvancedChannel |
| F05 | 门户会话与工程生命周期 | 9/9 | R2 W3 F2 S2 | worker9 | — | DevicesTools, HmiInspectionTools, NativeExchangeTools, ProjectSecurityTools, ProjectSessionTools, SessionTools | Tools/ScaffoldOperations, ProjectArchive, Portal/Portal.FoundationSession, Worker/SharedSessionLifecycle |
| F06 | 工程服务：语言与项目文本、设置、比较、对象标识、事务 | 10/10 | R4 W5 F1 | worker10 | — | NativeExchangeTools, ProjectSecurityTools, ProjectSessionTools, SoftwareUnitDeepTools | Portal/Portal.ProjectExchange, Portal/Portal.ObjectIdentity, Portal/Portal.Transactions, ToolTransactionRules, ObjectIdentityRules |
| F07 | 安全：工程保护、UMAC/UMC、证书、PLC 访问保护与设备用户 | 12/12 | R3 W9 | worker12 | — | CertificateManagementTools, HardwareNetworkTools, HardwareServicesTools, ProjectSecurityTools, SecurityDeepTools | Svc/ProjectSecurityService, Portal/Portal.ProjectSecurity, ProjectSecurityLogic, Svc/SecurityDeepService, SecurityDeepLogic, Svc/CertificateManagementService, EngineeringCredentialRules |
| F08 | PLC 对象组织：分组、删除、移动、块保护、系统组 | 10/10 | R1 W9 | worker10 | — | PlcBlocksTools, PlcExternalSourcesTools | EmptyPlcGroupDeletion, EngineeringGroupOperations, PlcTypeGroupCreation, Portal/Portal.EngineeringGroups |
| F09 | PLC 读改扩展：变量/常量定义、实例 DB、校验导入、修补重导、程序更新 | 11/11 | R3 W8 | worker11 | — | NativeExchangeTools, PlcBlocksTools, PlcBuildTools, PlcTablesTools, TypesTools | Builders/PlcVerifiedImport, Svc/PlcSingleImportRecovery, Svc/TypesService, Portal/Portal.Software.LibrarySeed, Portal/Portal.Blocks |
| F10 | SIMATIC SD 文档交换与块作用域 | 7/7 | R1 W4 F2 | worker7 | — | DocumentsTools, EngineeringAuditTools, SoftwareUnitDeepTools | Svc/DocumentsService, Svc/EngineeringAuditService, Portal/Portal.EngineeringAudit, Tools/DocumentImportGuidance |
| F11 | 外部源扩展、源生成与交叉引用 | 3/3 | R1 W1 F1 | worker3 | — | NativeExchangeTools, PlcExternalSourcesTools | Svc/PlcExternalSourcesService, Portal/Portal.Software.CrossReferences, CrossReferenceTreeReader, Portal/Portal.PlcNativeFiles |
| F12 | 监控/强制表（离线）、在线监视与因果追踪 | 12/12 | R3 W2 F1 On5 OnW1 | worker12 | — | PlcTablesTools, RuntimeTools | Svc/PlcTablesService, Portal/Portal.Software.PlcTables, Portal/Portal.CausalTrace |
| F13 | 工艺对象与运动控制 | 9/9 | R2 W6 F1 | worker9 | — | MotionProDiagClassicHmiTools, TechnologyObjectsTools | Svc/TechnologyObjectsService, Portal/Portal.Software.TechnologyObjects, Portal/Portal.TechnologyMapping, Portal/Portal.MotionExchange |
| F14 | PLC 报警、ProDiag 与 Supervision | 11/11 | R1 W7 F3 | worker11 | — | AlarmsTools, MotionProDiagClassicHmiTools, SpecializedExchangeTools | Svc/AlarmsService, Svc/SpecializedExchangeService |
| F15 | PLC OPC UA 服务器接口与访问控制 | 8/8 | R2 W4 F2 | worker8 | — | OpcUaTools | Svc/OpcUaService |
| F16 | 软件单元与 PLC 完整性（校验和/指纹/写保护） | 5/6 | R3 W3 | worker6 | ManagePlcBlockWriteProtection | SoftwareUnitDeepTools, SoftwareUnitManagementTools | Svc/SoftwareUnitDeepService, Portal/Portal.SoftwareUnitDeep, Svc/SoftwareUnitManagementService |
| F17 | 设备与 HMI 编译 | 2/2 | X2 | worker2 | — | HardwareServicesTools, HmiDescribeTools | Portal/Portal.CompileCandidate, Tools/PlcCompilation |
| F18 | 硬件设备、模块、属性、设备分组与硬件服务 | 19/19 | R8 W9 F2 | worker19 | — | DevicesTools, HardwareManagementTools, HardwareNetworkTools, HardwareServicesTools, ModulesTools | Svc/DevicesService, Portal/Portal.Devices, Svc/ModulesService, Svc/HardwareManagementService, Svc/HardwareServicesService, Portal/Portal.HardwareServices, DeviceServiceObjectRules, HardwareUtilityRules |
| F19 | 硬件地址（IO/IP/HW 标识） | 5/5 | R3 W2 | worker5 | — | AddressesTools | Svc/AddressesService |
| F20 | 网络：子网、IO 系统、拓扑、域、通道、传输区、通信连接 | 17/19 | R9 W9 Off1 | worker19 | ListCommunicationConnections, ManageCommunicationConnection | HardwareNetworkTools, HardwareServicesTools | Svc/HardwareNetworkService, Portal/Portal.HardwareNetwork |
| F21 | CAx / AutomationML 设备交换 | 3/3 | F2 Off1 | worker3 | — | HardwareAmlTools | Svc/HardwareAmlService |
| F22 | 在线、下载、上传、在线比较与在线数据 | 15/15 | R2 F2 On8 OnW3 | worker15 | — | NativeExchangeTools, OnlineDownloadTools, PlcBlocksTools | Svc/OnlineDownloadService, Svc/OnlineDownloadService.FallbackCandidate, Portal/Portal.Download, Tools/OnlineToolPolicy |
| F23 | 项目库与全局库 | 14/14 | R5 W9 | worker14 | — | LibraryTools | Svc/LibraryService, Portal/Portal.LibraryDeep, Portal/Portal.LibraryExchange, Portal/Portal.LibraryManagement |
| F24 | HMI 通用读取与 XML 交换（Classic/Unified 反射路径） | 22/22 | R12 W6 F4 | worker22 | — | HmiDescribeTools, HmiExchangeTools, HmiInspectionTools, HmiTagDeletionTools | Svc/HmiDescribeService, Svc/HmiExchangeService, Portal/Portal.Software.HmiExchange, Svc/HmiInspectionService, Hmi/HmiSnapshot, Hmi/HmiScreenTraversal, Hmi/HmiExactAccess, Svc/HmiTagDeletionService, Hmi/HmiTagDeletion, Portal/Portal.HmiOperation |
| F25 | Classic HMI 工程（画面树、文件夹、脚本、周期、文本/图形列表、面板、多语言图形） | 9/10 | R4 W6 | worker10 | ManageClassicHmiGraphic | ClassicHmiFoldersTools, MotionProDiagClassicHmiTools | Svc/ClassicHmiFoldersService, Portal/Portal.ClassicHmiFolders, Svc/MotionProDiagClassicHmiService, MotionProDiagClassicHmiLogic, Portal/Portal.MotionProDiagClassicHmi |
| F26 | Unified 画面建造与画面元素（Ensure/Apply/Bind/事件/动态化/分组） | 26/26 | R6 W20 | worker26 | — | HmiInspectionTools, UnifiedHmiGroupsTools, UnifiedHmiTools, UnifiedScreenItemsTools | Svc/UnifiedHmiService, Portal/Portal.Software.UnifiedHmiHelpers, Svc/UnifiedScreenItemsService, Hmi/UnifiedScreenItemLogic, Portal/Portal.UnifiedScreenItems, Svc/UnifiedHmiGroupsService, Hmi/HmiEventAccess |
| F27 | Unified 对象模型：对象服务、UI 模型、事件、工程对象（报警/日志/列表） | 23/23 | R8 W14 F1 | worker23 | — | UnifiedEngineeringTools, UnifiedEventsTools, UnifiedObjectServicesTools, UnifiedUiModelTools | Svc/UnifiedObjectServicesService, Portal/Portal.UnifiedObjectServices, Svc/UnifiedUiModelService, Hmi/UnifiedUiModelLogic, Svc/UnifiedEventsService, Hmi/UnifiedEventOperations, Svc/UnifiedEngineeringService, Portal/Portal.UnifiedEngineering, Hmi/UnifiedMultilingualText |
| F28 | Unified 交换、脚本、运行时设置、图形选择与迁移只读 | 16/16 | R11 W3 F2 | worker16 | — | GlobalScriptEditTools, GraphicSelectionTools, MigrationReadTools, RuntimeSettingsTools, UnifiedExchangeTools | Svc/UnifiedExchangeService, Hmi/UnifiedExchangeLogic, Svc/GlobalScriptEditService, Hmi/UnifiedGlobalScriptEdit, Svc/RuntimeSettingsService, Hmi/UnifiedRuntimeSettingsAccess, Svc/GraphicSelectionService, Hmi/UnifiedGraphicSelection, Svc/MigrationReadService, MigrationRead, MigrationPages, Hmi/UnifiedNativeRead, Hmi/UnifiedScriptAccess, Hmi/UnifiedTagDefinitions |
| F29 | SiVArc | 10/11 | R4 W7 | worker11 | ManageSivarcScreenLayout | OptionalEngineeringTools, SivarcTools | Svc/SivarcService, Portal/Portal.Sivarc, Svc/OptionalEngineeringService, Portal/Portal.OptionalEngineering |
| F30 | Safety（F 程序管理与 V21 安全验证） | 4/9 | R2 W6 F1 | worker9 | ListSafetyActivationTests, ManageSafetyActivationTest, ManageSafetyActivationTestGroup, ManageSafetyFunction, ManageSafetyFunctionCondition | SafetyManagementTools, SafetyValidationTools | Svc/SafetyManagementService, Svc/SafetyValidationService |
| F31 | Startdrive 驱动与 DCC | 18/19 | R4 W13 On1 OnW1 | worker19 | ManageDriveSafetyAcceptanceTest | DccTools, StartdriveTools | Svc/StartdriveService, StartdriveLogic, Portal/Portal.Startdrive, Svc/DccService, Portal/Portal.Dcc |
| F32 | CFC 图表 | 2/2 | W2 | worker2 | — | CfcTools | Svc/CfcService |
| F33 | 版本控制与协同：VCI、Git、Teamcenter | 9/9 | R2 W6 F1 | worker9 | — | GitWorkflowTools, TeamcenterTools, VersionControlTools | Svc/VersionControlService, Svc/VersionControlService.FallbackCandidate, Svc/TeamcenterService |
| F34 | Test Suite 与 V20 可选产品（SINUMERIK/SIMOTION/SCADA Export） | 9/9 | R2 W6 X1 | worker9 | — | TestSuiteTools, V20OptionsTools | Svc/TestSuiteService, Svc/V20OptionsService |
| F35 | Openness 反射（对象/服务通用访问） | 7/7 | R5 W2 | worker7 | — | ReflectionTools | Svc/ReflectionService, Portal/Portal.Software.Reflection, PropertyPathReader, EngineeringScalarProperties |
| **合计** | 35 族 | **420/431** | | | | | |

### 1.1 族的 Siemens API 主入口（证据逐版见附录 B）

| 族 | 主入口（Siemens.Engineering 省略前缀） |
|---|---|
| F01–F04 | 无 Openness 主入口。F04 用 Sharp7（S7）、`Siemens.Simatic.S7.Webserver.API`、`Workstation.UaClient`、Open Pipe、运行时定位的 PLCSIM Advanced API；`GetPlcLiveValuesS7` 可选读取 Put/Get 设备属性；F02 `RenderPlcBlockDocument` 可选在线导出块（`SW.Blocks.PlcBlock.Export`/`ExportAsDocuments`） |
| F05 | `TiaPortal.GetProcesses`、`TiaPortalProcess.AttachedSessions`、`Project.SaveAs`/`Archive`、`ProjectComposition.Create`/`Retrieve`/`RetrieveWithUpgrade`、`Multiuser.ProjectServer.CreateLocalSession`、`Multiuser.LocalSession`、`Multiuser.MarkingService` |
| F06 | `ProjectBase.LanguageSettings`/`ExportProjectTexts`/`ImportProjectTexts`、`TiaPortal.SettingsFolders`、`SW.PlcSoftware.CompareTo`、`HW.HardwareObject.CompareTo`、`Library.ProjectLibrary.CompareToLibrary`、`ObjectIdentifierProvider`、`IShowable.ShowInEditor`、`ExclusiveAccess.Transaction`、`ProjectBase.IsSimulationDuringBlockCompilationEnabled`/`SW.PlcSimulationSettingsProvider` |
| F07 | `AdvancedProtection.ProtectionProviderBase`、`HW.Features.PlcAccessLevelProvider`/`PlcMasterSecretConfigurator`/`WebserverUserManagement`/`WatchAndForceTableAccessManager`/`SysLogConfigurationManager`、`Security.LocalCertificateManager`/`SyslogServerProvider`/`PlcPasswordPolicyService`、`Umac.UmacConfigurator`/`UmcServerConfigurator`/`PasswordPolicyConfigurator`；Put/Get 走 `DeviceItem` 属性 |
| F08 | `SW.Blocks.PlcBlockUserGroupComposition.Create`、`PlcBlock.Delete`、`SW.Types.PlcType.Delete`、`SW.Tags.PlcTagTable.Delete`、`PlcBlockSystemGroup.SystemBlockGroups`、`PlcTypeSystemGroup.SystemTypeGroups`、`SW.Blocks.PlcBlockProtectionProvider`、各类 `*UserGroupComposition`；`MovePlcBlockToGroup` 为导出-删除-导入（S7DCL 优先，XML 回退） |
| F09 | `SW.Tags.PlcTagComposition.Create`、`PlcUserConstantComposition.Create`、`PlcBlockComposition.CreateInstanceDB`/`Import`、`PlcBlock.Export`、`PlcTagTableComposition.Import`、`SW.PlcSoftware.UpdateProgram` |
| F10 | `SW.Blocks.PlcBlock.ExportAsDocuments`、`PlcBlockComposition.ImportFromDocuments`、`SW.PlcDocument`、`SW.Types.PlcTypeGroup.Documents`、`SW.Units.PlcUnit`/`PlcSafetyUnit` |
| F11 | `PlcExternalSourceSystemGroup.GenerateSource`、`PlcExternalSourceUserGroupComposition.Create`、`CrossReference.CrossReferenceService.GetCrossReferences` |
| F12 | `SW.WatchAndForceTables.PlcWatchTableComposition`/`PlcForceTableComposition`（`Import`）、`PlcWatchTable.Export`/`PlcForceTable.Export`、`PlcWatchTable.Entries`、`PlcWatchTableEntry.Address`/`ModifyValue`；在线值走 F04 的 S7 通道 |
| F13 | `TechnologicalInstanceDBComposition.Create`/`Import`、`TechnologicalInstanceDB.Export`、`TechnologicalInstanceDBGroup.Groups`、`SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider`/`OutputCamHardwareConnectionProvider`/`CamDataSupport` |
| F14 | `SW.Alarm.PlcAlarmTextListProvider.ExportToXlsx`/`ImportFromXlsx`、`PlcAlarmTextProvider`、`AlarmClassDataProvider.Export`/`Import`、`SW.Alarm.TextLists.PlcAlarmTextlistGroup`、`CodeBlock.ExportProDIAGInfo`、`SW.Supervision.SupervisionProvider`/`SupervisionSettingsProvider` |
| F15 | `SW.OpcUa.OpcUaProvider`、`ServerInterfaceGroup`、`ServerInterface.Export`/`Import`、`ServerInterfaceComposition.Create`、`SimaticInterfaces`、`ReferenceNamespaces`、`HW.Features.OpcUaUserManagement`；建模接口生成用 third_party `SiemensOpcUaModelled` |
| F16 | `SW.PlcChecksumProvider`、`SW.FingerprintProvider`、`SW.Units.PlcUnitProvider`/`PlcUnitComposition.Create`/`UnitAccessType`/`PlcUnitRelation`/`PlcSafetyUnit`、`SW.Blocks.PlcBlockWriteProtectionProvider`（21） |
| F17 | `Compiler.ICompilable`、`Compiler.CompilerResult`、`Hmi.HmiTarget`、`HmiUnified.HmiSoftware` |
| F18 | `HW.DeviceComposition.CreateWithItem`/`CreateFrom`、`HW.HardwareObject.PlugNew`/`GetPlugLocations`/`PlugMove`/`PlugCopy`、`DeviceItem.Delete`、`HW.DeviceUserGroupComposition.Create`、`ProjectBase.HwUtilities`、`HW.Utilities.ModuleInformationProvider`/`CardReaderPscProvider`、`HW.HardwareCatalog.HardwareCatalog`、`HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider`、`HW.Features.*` 服务 |
| F19 | `HW.DeviceItem.Addresses`、`HW.Address.StartAddress`、`HW.HardwareObject.HwIdentifiers`、`HW.Features.AddressController`/`HwIdentifierController`、`HW.Features.NetworkInterface.Nodes`、`HW.Address.AssignProcessImageToOrganizationBlock`（≤20）/`SW.ProcessImageProvider`（21） |
| F20 | `HW.SubnetComposition.Create`、`HW.Node.ConnectToSubnet`、`HW.IoController.CreateIoSystem`、`HW.IoConnector.ConnectToIoSystem`、`HW.Features.NetworkPort.ConnectToPort`、`DeviceItem.Channels`、`NetworkInterface.TransferAreas`、`HW.Features.MrpDomainOwner`/`SyncDomainOwner`/`MrpInstancesOwner`、`HW.CommunicationConnections.*`（21） |
| F21 | `Cax.CaxProvider.Export`/`Import`、`Cax.CaxImportOptions` |
| F22 | `Online.OnlineProvider.GoOnline`/`GoOffline`、`Connection.ConnectionConfiguration.ApplyConfiguration`、`Connection.ConfigurationPcInterface.GetAccessibleDevices`、`SW.PlcSoftware.CompareToOnline`、`Download.DownloadProvider.Download`/`RHDownloadProvider`、`Upload.StationUploadProvider.StationUpload`、`FingerprintData.FingerprintDataProvider`、`SW.Blocks.InterfaceSnapshot`、`SW.Loader.LoadableProvider`、`Online.Configurations.TlsVerificationConfiguration` |
| F23 | `ProjectBase.ProjectLibrary`、`Library.GlobalLibraryComposition.Open`、`MasterCopies.MasterCopyComposition.Create`、`ProjectLibrary.UpdateCheck`/`UpdateProject`/`UpdateLibrary`、`LibraryTypeVersion.Export`、`ProjectLibrary.CompareToLibrary`、`Library.Compare.*`、`LibraryTypeComposition.CreateFromDocuments` |
| F24 | `Hmi.HmiTarget`（`ScreenFolder`/`TagFolder`/`Connections`）、`Hmi.Screen.ScreenComposition.Import`/`Screen.Export`、`Hmi.Tag.TagTableComposition.Import`/`Tag.Delete`、`Hmi.Communication.ConnectionComposition.Import`、`HmiUnified.HmiSoftware`；引擎大量以反射字符串（`"Screens"`、`"TagTables"`、`"Connections"`）访问 |
| F25 | `Hmi.Screen.ScreenUserFolder`、`Hmi.RuntimeScripting.VBScriptComposition.Import`、`Hmi.Cycle.CycleComposition.Import`、`Hmi.TextGraphicList.TextList`/`GraphicList`、`Hmi.Faceplate.FaceplateLibraryType`、`Hmi.Globalization.MultiLingualGraphic`、`Hmi.Globalization.GraphicsProvider`（21） |
| F26 | `HmiUnified.HmiSoftware`、`HmiUnified.UI.Screens.HmiScreen`、`UI.Base.HmiScreenItemBase`、`UI.Dynamization.*`、`UI.Events.*`、`UI.ScreenGroup.HmiScreenGroup`、`HmiConnections.HmiConnection`、`HmiTags.*`；引擎多为反射访问，脚本解析用 Esprima |
| F27 | `HmiUnified.Cpm.*`（`PlantView`，21 有 `PlantViewsProvider`）、`HmiAlarm.*`、`HmiLogging.*`、`LoggingTags.HmiLoggingTag`、`HmiOpcUaAlarm.*`、`UI.Dynamization.*`、`TextGraphicList.*`（20+）、`HmiAudit.*`（20+） |
| F28 | `HmiTags.HmiTagComposition.Export`/`Import`（WinCC ML YAML）、`HmiUnified.Scripts.*`、`RuntimeSettings.*`（部分 20+）、`Library.ScriptModuleType`；YamlDotNet、Esprima |
| F29 | `SiVArc.Sivarc.Generate`、`SiVArc.*Rule*`、`SivarcDataProvider`、`TagDefinition`、`*RuleTable`、`SivarcDefinitionsUpgrader`、`LayoutData`（21） |
| F30 | `Safety.GlobalSettings`、`SafetyAdministration`、`SafetySignatureProvider`、`SafetyPrintout`、`SafetyValidation.SafetyValidationAssistant`（21） |
| F31 | `MC.Drives.DriveObjectContainer`/`DriveParameter`/`Telegram`/`OnlineDriveObjectContainer`/`TechnologyExtensionContainer`、`MC.Drives.DFI.DriveFunctionInterface`、`DriveObject.Security`、`MC.Drives.Dcc.*`（`DriveControlChartContainer`、`DccBlock`、`DccPin`、`DcbLibrary`） |
| F32 | `SW.FunctionCharts.ChartProvider`/`ChartProviderS7`（`CompleteExport`、`SelectiveExport`、`Import`、`Add/Change/RemoveChartProtection`） |
| F33 | `VersionControl.VersionControlInterface`/`Workspace`/`WorkspaceMapping`（16–20）/`MappedObject`（20+）、`TeamcenterGateway.TeamcenterConnectionProvider`/`TcGatewaySearchAndDownloadProvider`/`TcGatewayWorkflowProvider`；Git 部分直接调用 git.exe |
| F34 | `TestSuite.TestSuiteService`、`ApplicationTest.TestCaseExecutor`、`SystemTest.SystemTestCaseExecutor`、`StyleGuide.RuleSetExecutor`、`SCADAExporter.ScadaExportProvider`、`MC.Sinumerik.SafetyModeProvider`/`SinumerikAlarmTextProvider`、`HW.Utilities.SinumerikArchiveProvider`、`Simotion.SimotionProvider` |
| F35 | `IEngineeringObject.GetAttributeInfos`/`GetCompositionInfos`/`GetInvocationInfos`/`Invoke`、`IEngineeringServiceProvider.GetService`、`EngineeringServiceInfo` |

### 1.2 与 openness-coverage.md 29 行工作流的对应

| coverage 行 | 族 |
|---|---|
| Ordinary PLC browsing；PLC XML exchange；external source create/generate；compile；source pipeline；offline builders | Foundation 已有（67 个共有工具）；扩展部分：F08、F09、F11、F17、F02/F03 |
| Technology objects（read/import/user groups/export） | F13 |
| Watch-table listing and XML exchange | F12 |
| Hardware catalog；Create a device | F18 |
| Network interface and connection | F19、F20 |
| CAx device exchange | F21 |
| Project/global library | F23 |
| VCI；Teamcenter | F33 |
| Classic HMI；Unified HMI | F24、F25、F03；F26、F27、F28 |
| PLC software units | F16（作用域枚举另见 F10） |
| Safety offline login | F30（编译登录仍在 Foundation 编译族） |
| Startdrive parameters；DCC | F31 |
| SiVArc；CFC；Test Suite | F29；F32；F34 |
| PLC upload/download | F22 |
| coverage 表未列（差距复查第 4、7 项） | F01、F04、F05、F06、F07、F14、F15、F35 |

### 1.3 族内 V20/V21 差异（须作为“版本 API 差异”单独维护）

| 族 | 差异（证据见附录 B） | 引擎 `#if TIA_V20` 处数 |
|---|---|---|
| F06 | 编译设置：V20 `ProjectBase.IsSimulationDuringBlockCompilationEnabled`/`IsVirtualPlcDuringBlockCompilationEnabled`（16–20）→ V21 `SW.PlcSimulationSettingsProvider`/`VirtualPlcSettingsProvider` | SoftwareUnitDeepService 5 处之一 |
| F07 | 项目级 `Security.SyslogServerProvider` 20+；设备级 `SysLogConfigurationManager` 19+ | HardwareServicesService 8（与 F18 共用文件） |
| F10 | V20 文档导入需要 `.s7res`，V21 可选（Foundation `ImportFromDocuments` 描述）；`PlcDocumentComposition.CreateFrom(MasterCopy / PlcDocumentLibraryTypeVersion)` 仅 21 | SoftwareUnitDeepService 5 处之二 |
| F11 | 外部源组改名仅 21（`PLC_EXTERNAL_SOURCE_GROUP_RENAME`，`src/Shared/shared-native/plc-documents.props`） | PlcExternalSourcesService 1 |
| F13 | `Motion.AxisHardwareConnectionSDRProvider` 仅 21 | Portal.TechnologyMapping 3 |
| F15 | `SW.OpcUa.AccessControl` 命名空间仅 21 | OpcUaService 1 |
| F16 | `PlcBlockWriteProtectionProvider` 仅 21（V21 独有工具）；`PlcUnitSystemGroup.Name` 仅 21 | SoftwareUnitDeepService 5 处之二 |
| F18 | `HW.Features.DefaultWebPagesFeature` 仅 21；`TelecontrolManagement` 20+ | Portal.Devices 2 |
| F19 | 过程映像分配 ≤20 用 `HW.Address.AssignProcessImageToOrganizationBlock`，21 用 `SW.ProcessImageProvider` | AddressesService 2 |
| F20 | `HW.CommunicationConnections` 仅 21（2 个 V21 独有工具） | HardwareNetworkService 1 |
| F22 | `SW.Blocks.Interface.ValueService`、`Upload.ParameterUploadProvider` 仅 21；V20 上相应动作须拒绝 | OnlineDownloadService 4 |
| F25 | `Hmi.Globalization.GraphicsProvider` 仅 21（`ManageClassicHmiGraphic` V21 独有；`GetClassicHmiGlobalization` 在 V20 报 NotSupported） | ClassicHmiFoldersService 2、MotionProDiagClassicHmiService 5 |
| F27 | `HmiUnified.Cpm.PlantViewsProvider` 仅 21 | UnifiedObjectServicesService 1 |
| F29 | `SiVArc.LayoutData` 仅 21（`ManageSivarcScreenLayout` V21 独有） | SivarcService 6 |
| F30 | `SafetyValidation` 命名空间、`Safety.SafetyBaseIdProvider` 仅 21（5 个 V21 独有工具） | SafetyManagementService 2、SafetyValidationService 2 |
| F31 | `MC.Drives.DriveItemHardwareModule`、`SafetyAcceptanceTestProvider`、`Dcc.DcbLibraryImporter` 仅 21 | StartdriveService 6、DccService 1、Portal.Startdrive 1 |
| F33 | V20 同有 `WorkspaceMapping` 与 `MappedObject`，V21 仅 `MappedObject`（适配器已有 `STUDIO_VCI_INITIAL/legacy/MODERN` 三分） | — |
| F34 | SCADA Export、SINUMERIK、SIMOTION 在 V21 SDK 中无对应类型（V21 路由拒绝） | V20OptionsService 6 |
| 全部 HMI/可选族 | V21 SDK 拆分程序集：`Adapter.21` 目前只引用 `Base/Step7/Safety`（`src/Adapters/V21/Release.props`）；HMI 需 `WinCC.dll`/`WinCCUnified.dll`，可选族需 `Startdrive/DCC/CFC/Sivarc/TestSuite/TeamcenterGateway/SafetyValidation.dll`（已核对类型所在程序集）。V14 SP1–V20 均在单一 `Siemens.Engineering.dll` | — |

## 2. 旧版 API 可用性矩阵

格式：结论 + 关键分界（成员 id 的完整逐版证据见附录 B，前缀 `Siemens.Engineering.` 省略）。`⁰` 表示该族不调用 Openness，结论只说明无 API 障碍，不代表在旧版发布已获决定；`†` 表示关键类型证据齐全，但引擎经反射访问的成员未逐一核对（U5）。

| 族 | 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 关键分界 |
|---|---|---|---|---|---|---|---|
| F01 宿主元工具与诊断 | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | 无 Openness；旧版 Foundation 目录刻意不含桥接/lite（`docs/reference/version-tools.md`），在旧版发布 `CallTool/FindTools/ApplyToolBatch` 等属产品决定 |
| F02 PLC 离线分析 | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | 无 Openness；`RenderPlcBlockDocument` 在线导出模式用 `M:SW.Blocks.PlcBlock.Export`（全版本）；`ValidatePlcDocumentSchemas`/`DecodePlcSimaticMl` 以 V20/V21 格式为准 |
| F03 HMI 离线设计 | partial⁰ | partial⁰ | partial⁰ | partial⁰ | partial⁰ | partial⁰ | 离线可运行；Unified 产物落点需 `T:HmiUnified.HmiSoftware`（19+）；Classic 包与 `RunClassicHmiTemporaryImportPreflight` 以 V21 格式/环境为准 |
| F04 运行时通道 | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | yes⁰ | 非 Openness（`src/Runtime` net48）；Put/Get 预检的属性名逐版 unknown |
| F05 门户会话与生命周期 | partial | partial | partial | partial | partial | partial | `M:Project.SaveAs`/`Archive`、`M:ProjectComposition.Retrieve` 15.1+；`T:Multiuser.LocalSession`、`M:Multiuser.ProjectServer.CreateLocalSession` 17+；`BuildProjectScaffold` 的 S7DCL 导入需 `M:SW.Blocks.PlcBlockComposition.ImportFromDocuments`（20+） |
| F06 工程服务 | partial | partial | partial | partial | partial | partial | 语言/项目文本/设置/事务/ShowInEditor 全版本；`M:HW.Device.CompareTo` 15.1+；`T:CustomIdentity.CustomIdentityProvider` 16+；编译设置属性 16–20；`M:Library.ProjectLibrary.CompareToLibrary` 18+；`T:ObjectIdentifierProvider` 20+ |
| F07 安全 | no（Put/Get unknown） | partial | partial | partial | partial | partial | `T:AdvancedProtection.ProtectionProviderBase`、`T:HW.Features.PlcAccessLevelProvider` 15.1+；证书、Web 用户、监控表 Web 访问 16+；UMAC/UMC、主密钥 17+；密码策略、简易 Web 用户 18+；设备 Syslog 19+；项目 Syslog 20+ |
| F08 PLC 对象组织 | partial | partial | partial | partial | partial | yes | 分组/删除全版本；`T:SW.Blocks.PlcBlockProtectionProvider`、`P:SW.Types.PlcTypeSystemGroup.SystemTypeGroups`、监控表用户组 15.1+；`T:SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup` 19+ |
| F09 PLC 读改扩展 | partial | partial | partial | partial | partial | yes | `M:SW.Blocks.PlcBlockComposition.CreateInstanceDB` 15.1+；`M:SW.PlcSoftware.UpdateProgram` 18+；`SeedProjectFromReference` 的 Unified 部分 19+ |
| F10 SIMATIC SD 文档 | partial（仅 1/7） | partial（1/7） | partial（1/7） | partial（1/7） | partial（1/7） | partial（1/7） | `M:SW.Blocks.PlcBlock.ExportAsDocuments`、`ImportFromDocuments`、`T:SW.PlcDocument` 20+；仅 `GetPlcBlockScopes` 可迁（单元 16+、安全单元 18+） |
| F11 外部源与交叉引用 | partial | partial | partial | partial | yes | yes | `T:CrossReference.CrossReferenceService` 18+（V21 有崩溃隔离，默认禁用） |
| F12 监控/强制表 | partial | partial | partial | yes | yes | yes | `T:SW.WatchAndForceTables.PlcWatchTableComposition`/`PlcForceTableComposition` 及 `Import`/`Export` 15.1+（14sp1 仅剩 `TraceTagCause*`、`PlanOnline*`）；`P:…PlcWatchTableEntry.Address`/`ModifyValue` 17+（15.1/16 条目无类型化属性） |
| F13 工艺对象与运动 | partial | partial | partial | partial | partial | yes | `T:…Motion.CamDataSupport` 15.1+；`M:…TechnologicalInstanceDBComposition.Import` 16+；`P:…TechnologicalInstanceDBGroup.Groups` 19+ |
| F14 报警/ProDiag/Supervision | no | no | partial | partial | yes | yes | `T:SW.Alarm.PlcAlarmTextListProvider`、`PlcAlarmTextProvider` 16+；`AlarmClassDataProvider`、`M:SW.Blocks.CodeBlock.ExportProDIAGInfo`、`SW.Supervision.*` 17+；`T:SW.Alarm.TextLists.PlcAlarmTextlistGroup` 18+ |
| F15 OPC UA | no | partial | yes | yes | yes | yes | `T:SW.OpcUa.OpcUaProvider`、`ServerInterface.Export/Import` 15.1+；`SimaticInterfaces`、`ReferenceNamespaces`、`T:HW.Features.OpcUaUserManagement` 16+ |
| F16 单元与完整性 | no | partial | partial | partial | yes | yes | `T:SW.PlcChecksumProvider`、`T:SW.FingerprintProvider` 15.1+；`T:SW.Units.PlcUnitProvider` 等 16+；`T:SW.Units.PlcSafetyUnit` 18+ |
| F17 设备与 HMI 编译 | partial | partial | partial | partial | partial | yes | `T:Compiler.ICompilable`、`T:Hmi.HmiTarget` 全版本；`T:HmiUnified.HmiSoftware` 19+ |
| F18 硬件设备与服务 | partial | partial | partial | partial | partial | partial | 创建/插拔/删除/分组/HwUtilities 全版本；`CardReaderPscProvider` 15.1+；系统诊断设置 17+；`T:HW.HardwareCatalog.HardwareCatalog` 18+；`TelecontrolManagement` 20+ |
| F19 硬件地址 | yes | yes | yes | yes | yes | yes | 全部关键成员全版本；过程映像分配 ≤20 与 21 走不同 API |
| F20 网络 | partial | partial | partial | partial | partial | partial | 子网/IO 系统/端口/通道全版本；`TransferAreas` 15.1+；MRP/Sync 域 16+；`MrpInstancesOwner` 20+；`HW.CommunicationConnections` 仅 21 |
| F21 CAx/AML | yes | yes | yes | yes | yes | yes | `T:Cax.CaxProvider`、`Export`/`Import`、`CaxImportOptions` 全版本（AML 内容的版本差异 unknown） |
| F22 在线/下载/上传 | partial | partial | partial | partial | partial | yes | `GoOnline`/`CompareToOnline` 全版本；`DownloadProvider`、`StationUploadProvider`、`InterfaceSnapshot` 15.1+；`FingerprintDataProvider` 16+；TLS 校验 17+；`LoadableProvider` 18+；`GetAccessibleDevices` 19+ |
| F23 库 | partial | partial | partial | partial | partial | partial | 库核心全版本；`M:…LibraryTypeVersion.Export` 15.1+；`CompareToLibrary`/`Library.Compare` 18+；`M:…LibraryTypeComposition.CreateFromDocuments` 20+ |
| F24 HMI 通用读取与交换 | partial | partial | partial | partial | partial | yes | Classic 全版本；Unified 19+ |
| F25 Classic HMI | yes | yes | yes | yes | yes | yes | 画面/脚本/周期/列表/面板/多语言图形全版本；`GraphicsProvider` 仅 21（V20 同缺） |
| F26 Unified 画面建造 | no | no | no | no | no | yes† | `T:HmiUnified.HmiSoftware` 等 19+；族内直接引用的类型均 19+，成员大多经反射访问 |
| F27 Unified 对象模型 | no | no | no | no | no | partial† | 19+；`T:HmiUnified.TextGraphicList.HmiTextList`/`HmiSystemTextList`、`T:HmiUnified.HmiAudit.HmiAlarmAuditClass`、`T:…UI.Dynamization.ExpressionDynamization`/`TagParameterDynamization` 20+（`UnifiedUiModelLogic`、`Portal.UnifiedEngineering` 直接引用）；`Cpm.PlantViewsProvider` 仅 21 |
| F28 Unified 交换与设置 | no | no | no | no | no | partial† | 19+；`T:HmiUnified.RuntimeSettings.HmiRuntimeSettingsCommon.GeneralESIGCommentsStrategy` 20+（`UnifiedRuntimeSettingsAccess` 直接引用）；RuntimeSettings 公共类型 19 有 8 个、20 有 11 个 |
| F29 SiVArc | no | partial | partial | partial | partial | partial | `T:SiVArc.Sivarc`/`Generate` 15.1+；`SivarcDataProvider`、`TagDefinition` 17+；规则表 18+；`SivarcDefinitionsUpgrader` 20+ |
| F30 Safety | no | no | partial | yes | yes | yes | `T:Safety.GlobalSettings` 16+；`SafetyAdministration`、`SafetySignatureProvider`、`SafetyPrintout` 17+ |
| F31 Startdrive/DCC | no | partial | partial | partial | partial | partial | `T:MC.Drives.*` 15.1+（14sp1 仅旧 `Startdrive.*` 命名空间，API 不同）；DCC 容器 16+；`DccBlock`、`TechnologyExtensionContainer` 18+；`DriveObject.Security` 19+；`ReadDriveParameter` 20+ |
| F32 CFC | no | no | no | no | yes | yes | `T:SW.FunctionCharts.ChartProvider` 18+（含保护成员） |
| F33 VCI/Git/Teamcenter | partial | partial | partial | partial | yes | yes | Git 无 API；`T:VersionControl.VersionControlInterface` 16+；`TeamcenterGateway.*` 18+ |
| F34 Test Suite 与 V20 可选 | partial | partial | partial | partial | yes | yes | `SimotionProvider` 14sp1/15.1/17–20（16 缺）；SINUMERIK 安全模式/归档 16–20；`TestSuite.TestSuiteService`、`ScadaExportProvider` 17+；`SinumerikAlarmTextProvider`、`SystemTestCaseExecutor` 18+ |
| F35 反射 | yes | yes | yes | yes | yes | yes | `IEngineeringObject` 反射成员与 `GetService` 全版本（被调成员仍逐版不同） |

统计（Openness 族 F05–F35 共 31 族×6 版=186 格）：yes 53、partial 105、no 28（含 F07 14sp1 的“no（Put/Get unknown）”）；没有整格判为 unknown，属性名、反射成员、可选产品与格式层面的未知列在第 8 节。

## 3. 依赖

### 3.1 共享基础设施（引擎现状 → 迁移时的 Foundation 落点）

| 编号 | 基础设施 | 引擎现状 | 使用族 | Foundation 落点与要求 |
|---|---|---|---|---|
| I1 | 会话绑定与生命周期 | `Portal`、`IEngineeringSession`、`Worker/SharedSessionLifecycle.cs`（P7-04b）、绑定快照、进程租约 | 全部 Openness 族；F05 最深 | 适配器会话（`OpennessAdapter`/`PlcFoundationEngine`）需公开 portal/project/进程与独占访问；14sp1–19 为 STA，20/21 worker 为 MTA（P7-04），新代码须线程模型中立 |
| I2 | 对象定位 | softwarePath/别名（Foundation 已有 `PlcReadPathPolicy`、`NativePathSelection`）；单元作用域 unitName+unitKind；硬件 devicePath/itemPath 名数组（`ExactEngineeringHardware`、`HardwareOwnerPath`，19 个引擎文件）；HMI 路径；库路径 | F08–F17（PLC）、F07/F13/F18–F22/F31（硬件）、F24–F29（HMI）、F23/F29（库） | 逐类一次实现为 shared-native 原语，样板族先做硬件定位 |
| I3 | 步骤封装 | `RunHmiStepTool`（名为 HMI，实为通用：meta、dryRun、mayHaveChanged、before/after 读回、错误映射，54 个文件使用）、`AcquireHmiEditAccess`（50 个文件） | 几乎全部写族 | 宿主侧统一信封与审批（已有 P6-65 共用规则），适配器侧统一“预检-执行-读回-结果未知即锁定”回复接口 |
| I4 | 标量属性/属性读写 | `EngineeringScalarProperties`、`PropertyPathReader`、`ApplyScalarsAndAttributes`（47 个文件） | F07、F18–F20、F26–F28、F35 等 | 适配器原语，类型转换表逐版编译 |
| I5 | 预检/审批/计划哈希/expectedProjectFile/会话锁定 | 宿主 `McpServer.Approval`；worker `MutationIdentityPolicy`、`WorkerSessionOutcomeState` | 全部 WRITE/ONLINE-WRITE/FILE | 已在两类主机共用（P6-60/P6-65）；新族只需声明分类与预检 |
| I6 | 文件、导出与暂存 | `NativeInputPolicy` 路径规范化、`NativeExportCapture`、`EngineeringExport`、`EngineeringFileNames`；导出句柄（宿主 `McpServer.Exports`）；暂存（P6-67） | F09–F15、F21–F25、F28、F33 | 路径规则已共享；导出句柄随 B1 迁出引擎目录 |
| I7 | 编译 | Foundation `CompileAdapter`；引擎 `PlcCompilation` | F09、F17、F22、F30、F05 | 复用 Foundation 编译族（P6-COMPILE） |
| I8 | 在线路由与凭据 | `ConnectionConfiguration`/PG-PC 接口选择、`OnlineToolPolicy`、`EngineeringCredentialRules`（SecureString，不回显） | F22、F12、F31 在线部分、F07、F30、F33（Teamcenter） | 一处实现；ONLINE-WRITE 审批与安全闸门不变 |
| I9 | HMI 访问与崩溃隔离 | `HmiExactAccess`、`HmiSnapshot`、`Portal.Software.UnifiedHmiHelpers` 反射助手；已知崩溃规避（SyntaxCheck、脚本改名、Classic 画面尺寸、CFC 未知图表保护查询） | F24–F29、F32 | 隔离规则逐条带入适配器，不得在迁移中丢失 |
| I10 | 原生调用诊断与织入 | `InvocationJournal`、`NativeCallDiagnostics`、`NativeCallWeaver` verify | 全部适配器代码 | 已为八版适配器强制；新文件加入 `Adapter.Sources.props` 白名单 |
| I11 | 第三方 | TiaGitAddIn.Core（`RenderPlcVisualDiff`）、SiemensOpcUaModelled（F15）、Esprima/YamlDotNet（F28、Unified 脚本）、Sharp7/S7 WebAPI/Workstation.UaClient（`src/Runtime`，F04/F12）、PLCSIM Adv API（运行时定位）、siemens-plc-tools Python（`RunPlcCompanionTool`） | F01–F04、F12、F15、F28 | 只放宿主或 Siemens 无关库；不得进入适配器（边界检查） |
| I12 | 工具描述符与目录 | V20/V21 目录由引擎 `--write-tool-catalog` 生成并经 `EngineCatalog` 校验执行所有权；`ToolMetadata`、`ToolExecution`、`BehaviorCapabilities`、`GetToolUsage` 示例 | 全部 | P8-02 须先给描述符一个不依赖引擎的来源（见第 5 节），否则 P8-04 无法退役引擎 |
| I13 | DTO/JSON 边界 | 引擎服务直接返回 `JsonObject`/`ResponseMessage`；适配器禁止引用 Logic/JSON 库（`scripts/checks/Check-AdapterBoundary.py`） | 全部 Openness 族 | 每族在 `src/Adapters.Contracts` 定义类型化 DTO，worker 用 `WorkerJson` 序列化，宿主映射为与基线一致的 V4 JSON |

### 3.2 族间依赖

| 族 | 依赖 | 说明 |
|---|---|---|
| F01 | 全部族 | `CallTool`/`ApplyToolBatch`/`PreviewToolCall` 通过目录调用任意工具；须随目录来源（I12）一起迁 |
| F05 | F18、F09、F10、F17、F20、F26、Foundation 导入/编译 | `BuildProjectScaffold` 组合创建设备、构建器、外部源、S7DCL、编译、Unified HMI 与 HMI 连接 |
| F06 | F08、F18、F26、F27 | `RunToolTransaction` 只接受 CreatePlcTypeGroup、DeleteEmptyPlcBlockGroup、ManagePlcUserGroup、ManageDeviceUserGroup、ManageUnifiedHmiGroup、DeleteEmptyUnifiedHmiScreenGroup、SetUnifiedObject… 等子调用；`CompareLibraries` 与 F23 共用库定位 |
| F07 | F12、F18、F22 | Web 访问规则引用监控/强制表路径；PLC 保护与 `CompileDevice`/`DownloadPlc` 的密码提示关联 |
| F08 | F10、Foundation 导出/导入 | `MovePlcBlockToGroup` 为导出-删除-导入（S7DCL 优先，旧版只能走 XML） |
| F09 | Foundation 导入/编译/构建器、F24 | `SeedProjectFromReference` 也导入 HMI 画面与变量表 |
| F12 | F04、Foundation 块导出 | `MonitorPlcWatchTableS7`/`TraceTagCauseLive` 用 S7 通道；`TraceTagCause` 导出块做静态分析 |
| F13 | F18 | 轴/凸轮硬件连接引用设备项 |
| F14 | F23、F16 | 文本列表 `createFromMasterCopy` 用库主副本；单元作用域 |
| F17 | Foundation 编译族、F07 | 编译错误提示指向保护设置 |
| F19 | F20 | `GetDeviceIpAddress` 与网络节点读取共用 `NetworkInterface.Nodes` |
| F22 | F17、F07、F04（PLCSIM 作安全目标） | 下载前编译与保护密码；在线写审批 |
| F23 | F26 | `ImportMasterCopyFromGlobalLibrary` 写入 Unified 画面并读回 |
| F24 | F35 | `DescribeHmi*` 走反射描述 |
| F29 | F23、F24/F26、F09 | SiVArc 规则引用库类型/主副本、画面与块 |
| F30 | Foundation 编译（PLC_SAFETY 17+）、F16 | F 程序登录与安全单元 |
| F31 | F18 | 驱动对象挂在设备项上 |
| F33 | Foundation 导出/导入、F10 | VCI 同步 SimaticML/文档 |
| F34 | F16 | Test Suite 引用单元 |

### 3.3 现有候选与行为族约束

V20/V21 基线的 `behaviorCapabilities` 中已有包含独有工具的行为族，迁移后表格须逐字节不变：P6-DEVICE（F18 `CreateDevice`/`CreateGsdDevice`/`CreateHardwareCatalogDevice`）、P6-IMPORT/P6-EXPORT（F10 文档工具、F09 `ImportPlcTagTablesFromDirectory`）、P6-SESSION（F05 `ConnectIsolatedPortal`）、P6-CLOSE（F05 `SaveProjectCopy`）、P6-SOURCE（F11 `ManagePlcExternalSources`）、P6-COMPILE（F17）、P6-FALLBACK（F22 `DownloadPlc`/`DownloadPlcToFolder`、F10 文档、F33 VCI 五个工具）。
P6-FALLBACK 已有适配器侧先例：`src/Adapters/Native/Vci/VciFallbackAdapter.cs`（16–21 编译进适配器，也以 `TIA_ENGINE_LOCAL_PRIMITIVES` 编进引擎）、`src/Adapters.Contracts/FallbackDelegateAdapter.cs`；`PlcDocumentPrimitives`、`WatchTechnologyPrimitives`、`HardwarePrimitives`、`PlcBlockPrimitives` 已通过 `src/Shared/shared-native/*.props` 同时编进八版适配器与引擎。这是“共用逻辑 + 版本差异”的现成写法。

## 4. 迁移批次建议

排序原则：先无 Openness 和只读/离线写为主、旧版支持面宽的族；每批复用前一批建立的基础设施；在线写与可选产品放后。批次表在本文件维护（P8-03 引用）。

| 批次 | 族 | 工具 V20/V21 | 旧版可启用范围（按第 2 节） | 理由 | 前置 |
|---|---|---:|---|---|---|
| B1（可与 P8-02 并行） | F01、F02、F03 | 64/64 | 无 API 障碍；F01 是否在 14sp1–19 发布桥接/批处理须维护者决定；F03 产物格式标注 V21 | 已有 29 个在主机执行；其余不调用 Openness（仅 `RenderPlcBlockDocument` 可选导出），迁出 `src/Engine` 目录是引擎退役的必要条件 | P7-11 改名 |
| P8-02 样板 | F19 | 5/5 | 14sp1–19 全部 | 见第 5 节 | P8-01、P7-11 |
| B2 | F18、F20、F21 | 39/41 | F21 全部；F18/F20 全部为 partial（动作级排除） | 复用样板的硬件定位与读回；差距第 2 项（硬件与网络）；离线写、默认 dryRun | 样板 |
| B3 | F08、F09、F11、F17 | 26/26 | F08/F09 19 为 yes，其余 partial；F11 18+ yes；F17 19 yes | PLC 软件离线扩展，API 多为全版本；差距第 5 项（删除/分组/保护）；复用 Foundation 导出/导入/编译 | 样板 |
| B4 | F12、F13、F16 | 26/27 | F12 15.1+（17+ yes）；F13 19 yes；F16 15.1+（18+ yes） | 监控/强制表导入、工艺对象导入、软件单元（差距第 5、7 项）；Foundation 已有 `WatchTechnologyPrimitives` | B3 |
| B5 | F14、F15、F10 | 26/26 | F14 16+；F15 15.1+；F10 仅 `GetPlcBlockScopes` | 报警/ProDiag、OPC UA（差距第 7 项）以导出/读取为主；F10 复用已有 `PlcDocument*` 原语 | B4（单元作用域） |
| B6 | F06、F35、F23 | 31/31 | F35 全部；F06/F23 全部 partial | 语言与项目文本、库（差距第 3、4 项）；`RunToolTransaction` 需 B2/B3 的写操作已迁 | B2、B3 |
| B7 | F24、F25 | 31/32 | F25 全部；F24 Classic 全部、Unified 19 | 经典 HMI（差距第 3 项）；建立 HMI 访问与崩溃隔离（I9）；V21 需加 `WinCC.dll` 引用 | B6（F35 反射描述） |
| B8 | F05、F07、F33 | 30/30 | F05/F07 partial；F33 16+（18+ yes），Git 全部 | 另存/归档/恢复、多用户、安全与用户管理、VCI（差距第 4、6、7 项）；涉及会话生命周期与凭据，风险较高；VCI 有 `VciFallbackAdapter` 先例 | B6 |
| B9 | F22、F04 | 34/34 | F22 15.1+（19 yes）；F04 无 API 障碍 | 在线/下载/上传/比较（差距第 1 项，用户价值最高）；ONLINE-WRITE，需要 PLCSIM Advanced 作安全目标 | B3（编译）、B8（凭据） |
| B10 | F26、F27、F28 | 65/65 | 仅 19（F26 yes†，F27/F28 partial†） | Unified 体量最大、反射为主；旧版收益仅 V19；可拆两个 PR | B7 |
| B11 | F29、F30、F31 | 32/39 | F29 15.1+；F30 16/17+；F31 15.1+；均依赖可选产品 | 可选产品与安全相关写入；V21 需加拆分程序集引用 | B7、B10（SiVArc 画面）、B4 |
| B12 | F32、F34 | 11/11 | F32 18+；F34 按产品分段 | 可选产品收尾；完成后进入 P8-04 | B11 |

批次合计 V20 420、V21 431，与基线一致。若维护者按用户价值优先在线功能，可在 B3 之后提前 B9（依赖已满足），代价是更早进入 ONLINE-WRITE 的真机风险。

## 5. 样板族建议（P8-02）：F19 硬件地址

选择 F19（`GetDeviceAddressing`、`GetDeviceIpAddress`、`GetDeviceItemIoAddresses`、`SetDeviceAddress`、`SetDeviceItemIoAddress`），理由：

1. **范围小而闭合**：单一工具文件 `AddressesTools.cs`（346 行）与单一服务 `AddressesService.cs`（394 行）；3 个 READ、2 个 WRITE（默认 dryRun）。
2. **八版 API 齐全**：`P:HW.DeviceItem.Addresses`、`P:HW.Address.StartAddress`、`P:HW.HardwareObject.HwIdentifiers`、`T:HW.Features.AddressController`、`T:HW.Features.HwIdentifierController`、`P:HW.Features.NetworkInterface.Nodes` 在 14sp1–21 全部为 `Y`，可在全部六台旧版 VM 验收“按版启用”。
3. **含一处真实的版本 API 差异**：过程映像分配在 14sp1–20 用 `M:HW.Address.AssignProcessImageToOrganizationBlock`，21 改为 `T:SW.ProcessImageProvider`（引擎 `#if TIA_V20` 2 处），正好演示“共用逻辑 + 版本差异分开维护”（`TiaFeatures.props` 新增特性符号或 shared-native 原语分支）。
4. **建立后续批次的共用设施**：硬件对象定位（devicePath/itemPath 名数组、`HardwareOwnerPath`）、地址行 DTO、标量/属性写入与读回、独占访问、步骤回复接口，被 F18/F20/F07/F13/F22/F31 复用。
5. **覆盖写路径全部规则**：预检拒绝、审批、计划/项目身份核对、读回校验、结果未知锁定会话；无可选产品、无在线操作，标准测试工程（PLC + IO 模块）即可在 VM 复验。
6. **基线覆盖**：V20/V21 响应基线已有这 5 个工具的直接拒绝、桥接拒绝与 `GetToolUsage` 摘要调用。

备选：F35 反射（全版本，但通用 `InvokeObject/InvokeService` 不宜作为首个写法范例）；F21 CAx（仅 3 个 FILE 工具，AML 内容版本未知）；F33 VCI（已有适配器先例，但 16+、外部工作区状态与 3 个写操作）。

P8-02 在样板族上应确定并留下的框架（其余批次照抄）：

| 项 | 建议 |
|---|---|
| 工具声明来源 | 把 `[McpServerTool]` 签名与描述从引擎类中拆出，放进不引用 Siemens 的共享声明（宿主可反射），V20/V21 契约逐字节不变；目录生成不再依赖引擎 `--write-tool-catalog`（I12） |
| 宿主契约 | 按 `src/FoundationHost/*Contract.cs` 现有写法：参数校验、预检、审批分类、回复到 V4 信封的映射；版本可用性按 `FoundationTools.Available` 一类的显式表，不按名称推断 |
| 适配器 | `src/Adapters/Native/Hardware/<Family>.cs`（native）+ `<Family>Policy.cs`（纯逻辑）+ shared-native 原语；版本差异用 `src/Shared/TiaFeatures.props` 特性符号；新文件逐个列入 `Adapter.Sources.props` |
| 契约 DTO | `src/Adapters.Contracts` 类型化 DTO，加 golden JSON；不得在适配器使用 JSON 库 |
| worker 分发 | `PlcFoundationEngine` 单一外观 + `WorkerOperations.Names` 白名单 + 按类型逐个判断 `RequiresSessionReset` 的写法无法扩展到约 400 个操作：建议按族注册操作模块（`adapter.<family>.<op>`）、统一回复接口（只读标记与会话重置位），一次改好 |
| 执行所有权 | `ToolExecution` 由 `worker` 改为 Foundation 所有者；`EngineCatalog` 的所有权检查随之更新 |
| 引擎删除 | 同一 PR 删除该族工具声明、服务、Portal partial 与只被其使用的帮助代码 |
| 旧版启用 | 每族一个行为族条目（`current / NOT RUN`），按第 2 节逐版开关；partial 动作在原生调用前返回 `UNSUPPORTED_CAPABILITY` |

## 6. 每批验收方式

| 闸门 | 内容 | 通过标准 |
|---|---|---|
| G1 V20/V21 契约零差异 | 同一构建捕获两次（`Snapshot-ToolContracts.py`），比对 `manifest/contracts/v4/baseline/20.json`、`21.json` | 逐字节一致（tools、inputSchema、descriptionSha256、liteTools、behaviorCapabilities）；不手改哈希 |
| G2 V20/V21 响应零差异 | `Snapshot-ToolResponses.py`（sdk-only-fixture）比对 `responses/20.json`、`21.json` | 已迁工具的直接拒绝、桥接拒绝、`GetToolUsage` 摘要与离线行为调用逐字节一致（V20 当前 2044 次调用，420 个独有工具全部有调用） |
| G3 旧版契约增量 | 14sp1–19 基线只新增被启用的工具记录与响应调用 | 原 62–67 个工具记录不变；新增逐条评审 |
| G4 实现归属与删除 | `ToolExecution` 所有者变更；引擎中该族声明、服务、Portal partial 删除；`EngineCatalog` 所有权检查通过 | 引擎源码不再包含该族工具名的实现 |
| G5 原生调用清单守恒 | 用 `NativeCallWeaver` 的 inventory/verify：该族 Siemens 成员引用多重集“原引擎 = 新适配器”（V20/V21）；旧版适配器只增该族成员 | 与 `src/Adapters/README.md` Studio 迁移相同的逐站点比较，无未解释差异 |
| G6 单元与夹具 | Policy 纯逻辑测试（net10）、DTO golden JSON（`TiaMcp.Adapters.Contracts.Tests`）、worker 白名单/分发、两类主机一致性用例（P6-65）、每个版本差异分支至少一例 | `Test-DotnetSuites.py` 最低数只升不降（`tests/test-suites.json`） |
| G7 八版编译与边界 | 8 个 `Adapter.<key>.csproj` 逐版编译；`Test-AdapterInputs.cs`、`Test-WorkerIsolation.cs`；`Check-AdapterBoundary.py` | 全部通过；V21 新增程序集引用须有精确强名称 |
| G8 示例与用法 | `reference/tool-examples` 补启用版本的示例并生成嵌入目录 | 八版检索与功能检查通过 |
| G9 V20/V21 真机复验（L5） | 迁移前在同一测试工程录制该族脚本（读取、dryRun 预览、一次确认写、典型拒绝）；迁移后重放，规范化易变字段后比较 `data`/`error`/`meta.outcome` | 无未评审差异；记入真机台账 |
| G10 旧版启用验收（L5） | 只对第 2 节 yes/partial 的版本开启；在该版 VM 跑同一脚本，partial 动作确认在原生调用前拒绝；同步更新 `openness-coverage.md` 与 `reference/version-feature-matrix.json` | 每个启用版本一条台账；未验收版本保持 `current / NOT RUN` |
| G11 发布链 | `build-multi-version -Test`、`branch-gate`、`run-release-build -Tier package`（VM 测试包） | 通过 |

按批次的补充：B1 证明执行位置变化不改响应（G2）；B8 增加会话生命周期夹具（绑定、租约、重启后重新绑定）与凭据不回显检查；B9 以 PLCSIM Advanced 实例为在线目标，逐个确认 ONLINE-WRITE 审批，物理 PLC 只在维护者授权后；B10/B11 在台账中记录 VM 已安装的可选产品与许可证，服务缺失时须给出类型化前置条件拒绝。

## 7. V20/V21 补深度与官方差距在各族中的落点

| 项 | 来源 | 族（工具） | API 证据 | 处理建议 |
|---|---|---|---|---|
| Multiuser 新建本地会话与对象标记 | 补深度 | F05 `ManageMultiuserSession` | `M:Multiuser.ProjectServer.CreateLocalSession` 17+；`M:Multiuser.MarkingService.MarkObjects`/`UnmarkObjects` 20+ | B8 迁移后在适配器上补动作；17–19 只开放新建会话 |
| 强制表导入导出 | 补深度 | F12 `ListPlcForceTables`、`ManagePlcTableEntries`、`ExportPlcWatchTablesToDirectory` | `M:SW.WatchAndForceTables.PlcForceTableComposition.Import`、`M:…PlcForceTable.Export` 15.1+ | B4 一并补，15.1+ 同步启用 |
| 通信连接参数修改 | 补深度 | F20 `ManageCommunicationConnection` | `T:HW.CommunicationConnections.ConnectionComposition` 仅 21；连接属性可写性 unknown | B2 之后单独调研（V21 实测 SetAttribute） |
| 在线设置/重置 PLC 主密钥 | 补深度 | F07 `ManagePlcProtection`；F22 `DownloadPlc` | 离线 `T:HW.Features.PlcMasterSecretConfigurator` 17+；下载提示 `T:Download.Configurations.PlcMasterSecretPassword` 17+；按名称未找到在线设置/重置成员 | unknown，B8/B9 调研；不得以离线配置冒充在线操作 |
| PLC 交叉引用稳定性 | 补深度 | F11 `GetPlcCrossReferences` | `T:CrossReference.CrossReferenceService` 18+ | B3 迁移保持默认禁用与隔离；在 18/19 VM 单独验证 |
| Startdrive `p2051[0]` 读取崩溃 | 补深度 | F31 `GetDriveParameters`、`ManageStartdriveParameter` | `T:MC.Drives.DriveParameter` 15.1+ | B11 迁移保留拒绝/隔离规则 |
| 在线、下载、上传、在线比较（15.1+） | 差距 1 | F22、F04、F12 在线部分 | 见第 2 节 F22 | B9 |
| 硬件与网络组态 | 差距 2 | F18、F19、F20、F21 | 核心全版本 | 样板 + B2 |
| 库与经典 HMI | 差距 3 | F23、F24、F25 | 库核心与 Classic 全版本 | B6、B7 |
| 另存、归档、恢复、升级、语言与项目文本 | 差距 4 | F05、F06 | 另存/归档/恢复 15.1+；语言与文本全版本 | B6、B8 |
| 监控表导入、工艺对象导入、PLC 删除/分组/保护 | 差距 5 | F12、F13、F08 | 监控表 15.1+；TO 导入 16+；删除/分组全版本，保护 15.1+ | B3、B4 |
| VCI | 差距 6 | F33 | 16+ | B8 |
| 软件单元、报警/ProDiag、OPC UA、Safety、DCC/CFC、Startdrive、AML、Teamcenter、测试套件、用户管理、证书与保护、SiVArc | 差距 7 | F16、F14、F15、F30、F31/F32、F21、F33、F34、F07、F29 | 见第 2 节 | B4、B5、B2、B8、B11、B12 |
| 表格批量导入设备与硬件 | Eigen 对照（P8-40） | F18、F19、F20（可经 F21） | 设备创建/网络全版本 | B2 之后 |
| ECAD（XML/AML）生成设备、连接与变量 | Eigen 对照 | F21、F20、F09 | CaxProvider 全版本 | B3 之后 |
| Classic VB 脚本迁移到 Unified JS | Eigen 对照 | F25（读 VBScript）、F28（写全局脚本模块）、F03（离线转换） | Classic 全版本；Unified 19+ | B10 之后 |
| 风格指南检查 | Eigen 对照 | F34（`RuleSetExecutor` 17+）、F02（`AuditEngineeringExports`） | TestSuite 17+ | B1、B12 |
| 项目生成框架（P8-30/31） | 维护者方向 | F05 `BuildProjectScaffold`、F18–F20、F09、F26 | 见各族 | P8-03 相应批次完成后 |

## 8. 风险与未知

| 编号 | 风险/未知 | 影响 | 应对 |
|---|---|---|---|
| R1 | V20/V21 契约当前由引擎生成；声明与实现同处引擎类 | 若重写描述，G1 难以逐字节一致；P8-04 无法退役引擎 | P8-02 先把声明移出引擎，复用原字符串 |
| R2 | 适配器禁止 JSON；引擎服务直接构造 JSON（字段顺序、数字格式、null 处理） | 成功路径响应形状变化；CI 响应基线只覆盖拒绝与离线调用，捕捉不到 | DTO + 宿主映射按原字段顺序；以 P7-07 的 V20/V21 真机输出为 golden，G9 逐字段比较 |
| R3 | P7-07（V20/V21 真机验收）尚未完成 | G9 缺少迁移前对照 | 每批迁移前先录制该族真机证据 |
| R4 | 14sp1–19 PlcWorker 为 STA，20/21 worker 为 MTA 共用线程 | 线程相关的原生行为差异 | 适配器代码不假定套间；沿用 `StudioThreadGuard` 一类所有权检查 |
| R5 | 引擎大量反射访问（HMI 交换按字符串成员、Unified 服务无 Siemens using、`GetHardwareFeatures` 按 V21 目录探测） | 第 2 节只证明命名空间/关键类型存在，具体成员在 19 等版本可能不同 | 迁移时改为编译期类型绑定或逐版成员清单；19 上以 VM 验证 |
| R6 | 可选产品（SiVArc、Startdrive、DCC、CFC、Safety、Test Suite、Teamcenter、SINUMERIK、SIMOTION、SCADA Export、WinCC Classic/Unified）未必安装或无许可 | API 存在但运行时服务为空 | 服务缺失给类型化前置条件拒绝；台账记录安装状态 |
| R7 | 格式版本：离线构建器输出 V21 候选 XML；Classic HMI 包、AML、S7DCL 格式逐版不同 | F03、F05（脚手架）、F09（`BuildAndImportPlcArtifact`）在旧版只能部分可用 | 旧版只开放原生导出/导入；构建器按 `plc-xml-builders` 现有逐版格式工作推进 |
| R8 | 已知原生崩溃（V21 交叉引用、Unified SyntaxCheck/脚本改名、Classic 画面尺寸、CFC 未知图表保护查询、Startdrive `p2051[0]`） | 迁移若丢失守卫会导致 TIA 退出 | 守卫作为 Policy 单元测试固定下来（I9） |
| R9 | V20 注册但依赖 V21 独有 API 的动作（如 `UploadDeviceParameters` 需 `Upload.ParameterUploadProvider`，`GetClassicHmiGlobalization` 在 V20 NotSupported） | 迁移中可能误把 V20 拒绝改成执行或反之 | 以 1.3 表为准，逐动作保留现有 V20 行为，G2 校验 |
| R10 | V21 拆分程序集；`Adapter.21` 现只引用 Base/Step7/Safety | HMI 与可选族在 V21 编译失败或强名称校验失败 | 每批在 `src/Adapters/V21/Release.props` 增加精确 `AdapterApi`，`Test-AdapterInputs` 同步 |
| R11 | worker 分发写法不可扩展（单一外观、逐类型判断会话重置） | 约 400 个操作时易错 | P8-02 一次改为按族注册与统一回复接口 |
| R12 | 宿主/离线工具改变执行位置（B1） | 文件路径解析与日志位置可能变化 | 仅在 G2 证明零差异后改所有者 |
| R13 | 旧版目录增加工具改变产品形态（14sp1–19 现为 62–67 个工具、无桥接） | 客户端工具缓存与文档需刷新 | 维护者决定 F01 桥接是否下放；发布说明列出新增 |
| R14 | `docs/reference/version-tools.md` 计数陈旧（写 59–64/477/488，基线为 62–67/487/498） | 迁移后对照易混乱 | 后续文档任务刷新（本任务不改） |
| R15 | P7-11 改名未合并 | P8-02 起的路径与命名空间会变 | 按计划在 P7-11 合并后开工 |

未知（需真机或维护者决定）：

| 编号 | 未知项 | 判定方式 |
|---|---|---|
| U1 | Put/Get 等由设备属性名驱动的设置在 14sp1–19 的属性名 | VM 上 `GetAttributeInfos` 读取 |
| U2 | AML（CAEX 版本、导入选项语义）在各版的兼容性 | 各版导出→导入往返 |
| U3 | 在线设置/重置 PLC 主密钥是否有官方 API | 按完整 SDK 与官方手册复查；当前按名称未找到 |
| U4 | V21 通信连接属性的可写性 | V21 VM 实测 |
| U5 | Unified 反射代码在 V19 上的成员差异 | 逐成员清单 + V19 VM |
| U6 | 各 VM 的可选产品安装与许可 | 台账记录 |
| U7 | 成功路径响应在 DTO 重构后的逐字节一致性 | R2 的 golden 对照 |
| U8 | F01 桥接/批处理与 lite 是否在 14sp1–19 发布 | 维护者决定 |
| U9 | F34 SIMOTION 在 V16 SDK 中缺失（`T:Simotion.SimotionProvider` 14sp1/15.1/17–20 有、16 无；`sdk/TIA_V16_PublicAPI/V16` 也是唯一没有 `Siemens.MC.Simotion.Scripting.dll` 的目录）是否为 SDK 拷贝不全 | 对照安装版 V16 Openness |

## 附录 A：工具 → 族（全部 431 个）

“版本”列：20/21 表示两版均注册，21 表示 V21 独有。合计 431；V20 420、V21 431；与第 0 节基线核对一致。

| # | 工具 | 族 | 操作 | 执行 | 版本 | 引擎工具文件 |
|---:|---|---|---|---|---|---|
| 1 | AnalyzeGlobalLibraryPackage | F03 | OFFLINE | worker | 20/21 | LibraryTools.cs |
| 2 | AnalyzeHmiTemplateReference | F03 | OFFLINE | worker | 20/21 | LibraryTools.cs |
| 3 | AnalyzePlcReferences | F02 | OFFLINE | worker | 20/21 | PlcBlocksTools.cs |
| 4 | AnalyzePlcSclSource | F02 | OFFLINE | worker | 20/21 | PlcDocumentationTools.cs |
| 5 | AnalyzeUnifiedHmiTemplateLayout | F03 | OFFLINE | worker | 20/21 | LibraryTools.cs |
| 6 | ApplyToolBatch | F01 | WRITE | host | 20/21 | McpServer.Batch.cs |
| 7 | ApplyUnifiedHmiLayout | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 8 | ApplyUnifiedHmiScreenDesign | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 9 | ApplyUnifiedHmiTheme | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 10 | ArchiveSavedProject | F05 | FILE | worker | 20/21 | HmiInspectionTools.cs |
| 11 | AttachDeviceNodeToSubnet | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 12 | AuditEngineeringExports | F02 | FILE | worker | 20/21 | QualityAuditTools.cs |
| 13 | BindUnifiedHmiButtonPressedTag | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 14 | BindUnifiedHmiTagDynamization | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 15 | BuildAndImportPlcArtifact | F09 | WRITE | worker | 20/21 | PlcBuildTools.cs |
| 16 | BuildClassicHmiMinimalPackage | F03 | OFFLINE | host | 20/21 | OfflineSuiteTools.cs |
| 17 | BuildClassicHmiScreen | F03 | OFFLINE | host | 20/21 | XmlBuilderTools.cs |
| 18 | BuildClassicHmiTagTable | F03 | OFFLINE | host | 20/21 | OfflineSuiteTools.cs |
| 19 | BuildDeviceAmlDocument | F21 | OFFLINE | worker | 20/21 | HardwareAmlTools.cs |
| 20 | BuildPlcAliasAlarmLad | F02 | OFFLINE | host | 20/21 | TemplateTools.cs |
| 21 | BuildProjectScaffold | F05 | WRITE | worker | 20/21 | ProjectSessionTools.cs |
| 22 | BuildReleaseDiagnosticReport | F01 | OFFLINE | host | 20/21 | OfflineSuiteTools.cs |
| 23 | BuildReleaseHandoffArtifacts | F01 | FILE | worker | 20/21 | OfflineSuiteTools.cs |
| 24 | BuildReleaseManifest | F01 | OFFLINE | host | 20/21 | OfflineSuiteTools.cs |
| 25 | BuildReleaseRunbook | F01 | OFFLINE | host | 20/21 | OfflineSuiteTools.cs |
| 26 | BuildUnifiedHmiButtonActionScript | F03 | OFFLINE | worker | 20/21 | UnifiedHmiTools.cs |
| 27 | BuildUnifiedHmiLayoutDesign | F03 | OFFLINE | worker | 20/21 | UnifiedHmiTools.cs |
| 28 | BuildUnifiedHmiTemplateApplyDesign | F03 | OFFLINE | worker | 20/21 | OfflineSuiteTools.cs |
| 29 | BuildUnifiedHmiTemplateApplyDesignManifest | F03 | OFFLINE | worker | 20/21 | OfflineSuiteTools.cs |
| 30 | BuildUnifiedHmiThemeDesign | F03 | OFFLINE | worker | 20/21 | UnifiedHmiTools.cs |
| 31 | CallTool | F01 | SESSION | host | 20/21 | McpServer.ToolBridge.cs |
| 32 | CheckDownloadReadiness | F22 | READ | worker | 20/21 | OnlineDownloadTools.cs |
| 33 | CheckLibraryUpdates | F23 | READ | worker | 20/21 | LibraryTools.cs |
| 34 | CheckProductUpdate | F01 | SESSION | worker | 20/21 | McpServer.Maintenance.cs |
| 35 | ClearExportHandles | F01 | WRITE | host | 20/21 | ExportTools.cs |
| 36 | CompareLibraries | F06 | READ | worker | 20/21 | ProjectSecurityTools.cs |
| 37 | CompareLibraryObjects | F23 | READ | worker | 20/21 | LibraryTools.cs |
| 38 | ComparePlcBlockDocuments | F02 | READ | worker | 20/21 | OfflineAnalysisTools.cs |
| 39 | CompareProjects | F06 | READ | worker | 20/21 | ProjectSecurityTools.cs |
| 40 | CompareSoftwareToOnline | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 41 | CompareUnifiedGraphicSelections | F28 | READ | worker | 20/21 | GraphicSelectionTools.cs |
| 42 | CompileDevice | F17 | EXECUTE | worker | 20/21 | HardwareServicesTools.cs |
| 43 | CompileHmiDiagnostics | F17 | EXECUTE | worker | 20/21 | HmiDescribeTools.cs |
| 44 | ConfigureMotionHardwareConnection | F13 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 45 | ConnectDeviceNodesToProfinetSubnet | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 46 | ConnectIsolatedPortal | F05 | SESSION | worker | 20/21 | SessionTools.cs |
| 47 | ConnectOnlinePlc | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 48 | ConnectProjectToWorkspace | F33 | WRITE | worker | 20/21 | VersionControlTools.cs |
| 49 | CreateDevice | F18 | WRITE | worker | 20/21 | DevicesTools.cs |
| 50 | CreateGsdDevice | F18 | WRITE | worker | 20/21 | DevicesTools.cs |
| 51 | CreateHardwareCatalogDevice | F18 | WRITE | worker | 20/21 | DevicesTools.cs |
| 52 | CreateLibraryMasterCopy | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 53 | CreatePlcBlockGroup | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 54 | CreatePlcInstanceDb | F09 | WRITE | worker | 20/21 | NativeExchangeTools.cs |
| 55 | CreatePlcTypeGroup | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 56 | CreateVersionControlWorkspace | F33 | WRITE | worker | 20/21 | VersionControlTools.cs |
| 57 | DecodePlcSimaticMl | F02 | OFFLINE | host | 20/21 | V21EcosystemTools.cs |
| 58 | DeleteEmptyPlcBlockGroup | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 59 | DeleteEmptyUnifiedHmiScreenGroup | F26 | WRITE | worker | 20/21 | HmiInspectionTools.cs |
| 60 | DeleteExportHandle | F01 | WRITE | host | 20/21 | ExportTools.cs |
| 61 | DeleteHmiTag | F24 | WRITE | worker | 20/21 | HmiTagDeletionTools.cs |
| 62 | DeletePlcBlock | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 63 | DeletePlcTagTable | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 64 | DeletePlcType | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 65 | DeleteUnifiedHmiButtonEvent | F26 | WRITE | worker | 20/21 | HmiInspectionTools.cs |
| 66 | DeleteUnifiedHmiDynamization | F26 | WRITE | worker | 20/21 | HmiInspectionTools.cs |
| 67 | DescribeHmiScreen | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 68 | DescribeHmiScreenItem | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 69 | DescribeHmiSoftware | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 70 | DescribeHmiTag | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 71 | DescribeHmiTagTable | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 72 | DescribeObject | F35 | READ | worker | 20/21 | ReflectionTools.cs |
| 73 | DescribeObjectProperty | F35 | READ | worker | 20/21 | ReflectionTools.cs |
| 74 | DescribePlcBlockLogic | F09 | READ | worker | 20/21 | PlcBlocksTools.cs |
| 75 | DescribeService | F35 | READ | worker | 20/21 | ReflectionTools.cs |
| 76 | DescribeUnifiedHmiButtonEventScript | F26 | READ | worker | 20/21 | UnifiedHmiTools.cs |
| 77 | DescribeUnifiedScreenItemType | F26 | READ | worker | 20/21 | UnifiedScreenItemsTools.cs |
| 78 | DisconnectOnlinePlc | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 79 | DisconnectOnlinePlcs | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 80 | DownloadPlc | F22 | ONLINE-WRITE | worker | 20/21 | OnlineDownloadTools.cs |
| 81 | DownloadPlcToFolder | F22 | FILE | worker | 20/21 | OnlineDownloadTools.cs |
| 82 | EnsureOpennessUserGroup | F05 | SESSION | worker | 20/21 | SessionTools.cs |
| 83 | EnsureSubnet | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 84 | EnsureUnifiedHmiButtonAction | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 85 | EnsureUnifiedHmiButtonEventHandler | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 86 | EnsureUnifiedHmiConnection | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 87 | EnsureUnifiedHmiDynamization | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 88 | EnsureUnifiedHmiScreen | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 89 | EnsureUnifiedHmiScreenItem | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 90 | EnsureUnifiedHmiTag | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 91 | EnsureUnifiedHmiTagTable | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 92 | ExchangeCfcCharts | F32 | WRITE | worker | 20/21 | CfcTools.cs |
| 93 | ExchangeMotionCamData | F13 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 94 | ExchangePlcAlarmTextLists | F14 | WRITE | worker | 20/21 | AlarmsTools.cs |
| 95 | ExchangePlcSupervisions | F14 | WRITE | worker | 20/21 | SpecializedExchangeTools.cs |
| 96 | ExchangeSystemDiagnosticsSettings | F18 | FILE | worker | 20/21 | HardwareServicesTools.cs |
| 97 | ExchangeTestSuiteCase | F34 | WRITE | worker | 20/21 | TestSuiteTools.cs |
| 98 | ExchangeUnifiedScriptModules | F28 | FILE | worker | 20/21 | UnifiedExchangeTools.cs |
| 99 | ExchangeUnifiedTags | F28 | FILE | worker | 20/21 | UnifiedExchangeTools.cs |
| 100 | ExportAlarmClasses | F14 | FILE | worker | 20/21 | AlarmsTools.cs |
| 101 | ExportAlarmInstanceTexts | F14 | FILE | worker | 20/21 | AlarmsTools.cs |
| 102 | ExportAlarmTextLists | F14 | FILE | worker | 20/21 | AlarmsTools.cs |
| 103 | ExportDeviceAml | F21 | FILE | worker | 20/21 | HardwareAmlTools.cs |
| 104 | ExportHmiConnection | F24 | FILE | worker | 20/21 | HmiExchangeTools.cs |
| 105 | ExportHmiProgram | F24 | FILE | worker | 20/21 | HmiExchangeTools.cs |
| 106 | ExportHmiScreen | F24 | FILE | worker | 20/21 | HmiExchangeTools.cs |
| 107 | ExportHmiTagTable | F24 | FILE | worker | 20/21 | HmiExchangeTools.cs |
| 108 | ExportOpcUaInterface | F15 | FILE | worker | 20/21 | OpcUaTools.cs |
| 109 | ExportPlcBlockDocuments | F10 | FILE | worker | 20/21 | DocumentsTools.cs |
| 110 | ExportPlcBlocksDocuments | F10 | FILE | worker | 20/21 | DocumentsTools.cs |
| 111 | ExportPlcProDiagInfo | F14 | READ | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 112 | ExportPlcWatchTablesToDirectory | F12 | FILE | worker | 20/21 | PlcTablesTools.cs |
| 113 | ExportProjectTexts | F06 | FILE | worker | 20/21 | NativeExchangeTools.cs |
| 114 | ExportSafetyPrintout | F30 | FILE | worker | 20/21 | SafetyManagementTools.cs |
| 115 | ExportScadaData | F34 | READ | worker | 20/21 | V20OptionsTools.cs |
| 116 | ExportTechnologyObjectsToDirectory | F13 | FILE | worker | 20/21 | TechnologyObjectsTools.cs |
| 117 | ExportUnifiedEngineeringList | F27 | FILE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 118 | ExtractPlcBlockMetrics | F02 | READ | worker | 20/21 | OfflineAnalysisTools.cs |
| 119 | FindTools | F01 | READ | host | 20/21 | McpServer.ToolBridge.cs |
| 120 | GenerateAcceptanceReport | F01 | FILE | worker | 20/21 | DiagnosticsTools.cs |
| 121 | GenerateErrorReport | F01 | FILE | worker | 20/21 | DiagnosticsTools.cs |
| 122 | GenerateOpcUaModelledInterface | F15 | FILE | worker | 20/21 | OpcUaTools.cs |
| 123 | GeneratePlcDocumentation | F02 | FILE | worker | 20/21 | PlcDocumentationTools.cs |
| 124 | GeneratePlcLoadableFile | F22 | FILE | worker | 20/21 | NativeExchangeTools.cs |
| 125 | GeneratePlcSourceFromBlocks | F11 | FILE | worker | 20/21 | NativeExchangeTools.cs |
| 126 | GenerateSivarc | F29 | WRITE | worker | 20/21 | SivarcTools.cs |
| 127 | GetClassicHmiGlobalization | F25 | READ | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 128 | GetClassicHmiScreenTree | F25 | READ | worker | 20/21 | ClassicHmiFoldersTools.cs |
| 129 | GetDccObject | F31 | READ | worker | 20/21 | DccTools.cs |
| 130 | GetDeviceAddressing | F19 | READ | worker | 20/21 | AddressesTools.cs |
| 131 | GetDeviceAttributes | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 132 | GetDeviceInfo | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 133 | GetDeviceIpAddress | F19 | READ | worker | 20/21 | AddressesTools.cs |
| 134 | GetDeviceItemInfo | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 135 | GetDeviceItemIoAddresses | F19 | READ | worker | 20/21 | AddressesTools.cs |
| 136 | GetDeviceItemNetworkInfo | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 137 | GetDeviceItemTree | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 138 | GetDevicePlugLocations | F18 | READ | worker | 20/21 | ModulesTools.cs |
| 139 | GetDriveParameters | F31 | READ | worker | 20/21 | StartdriveTools.cs |
| 140 | GetEnvironmentDiagnostics | F01 | SESSION | worker | 20/21 | McpServer.Doctor.cs |
| 141 | GetExportContent | F01 | READ | host | 20/21 | ExportTools.cs |
| 142 | GetHardwareFeatures | F18 | READ | worker | 20/21 | HardwareServicesTools.cs |
| 143 | GetHmiProgramInfo | F24 | READ | worker | 20/21 | HmiDescribeTools.cs |
| 144 | GetHmiScreenSnapshot | F24 | READ | worker | 20/21 | HmiInspectionTools.cs |
| 145 | GetLibraryOverview | F23 | READ | worker | 20/21 | LibraryTools.cs |
| 146 | GetLibraryType | F23 | READ | worker | 20/21 | LibraryTools.cs |
| 147 | GetMotionAxisConfiguration | F13 | READ | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 148 | GetNativeInvocationLog | F01 | READ | host | 20/21 | EngineeringDiagnosticsTools.cs |
| 149 | GetObjectIdentifier | F06 | READ | worker | 20/21 | ProjectSessionTools.cs |
| 150 | GetObjectProperty | F35 | READ | worker | 20/21 | ReflectionTools.cs |
| 151 | GetOnlineDriveParameters | F31 | ONLINE | worker | 20/21 | StartdriveTools.cs |
| 152 | GetOnlineState | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 153 | GetOpcUaAccessControl | F15 | READ | worker | 20/21 | OpcUaTools.cs |
| 154 | GetOpennessCompatibility | F01 | READ | worker | 20/21 | EngineeringDiagnosticsTools.cs |
| 155 | GetOpennessGuidance | F01 | READ | host | 20/21 | EcosystemTools.cs |
| 156 | GetOpennessWorkerStatus | F01 | READ | host | 20/21 | McpServer.Worker.cs |
| 157 | GetPlcBlockEditCapabilities | F09 | READ | worker | 20/21 | PlcBlocksTools.cs |
| 158 | GetPlcBlockFingerprints | F22 | ONLINE | worker | 20/21 | PlcBlocksTools.cs |
| 159 | GetPlcBlockScopes | F10 | READ | worker | 20/21 | EngineeringAuditTools.cs |
| 160 | GetPlcChecksums | F16 | READ | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 161 | GetPlcCrossReferences | F11 | READ | worker | 20/21 | PlcExternalSourcesTools.cs |
| 162 | GetPlcLiveValuesOpcUa | F04 | ONLINE | worker | 20/21 | RuntimeTools.cs |
| 163 | GetPlcLiveValuesS7 | F04 | ONLINE | worker | 20/21 | RuntimeTools.cs |
| 164 | GetPlcObjectFingerprints | F16 | READ | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 165 | GetPlcOpcUaConfiguration | F15 | READ | worker | 20/21 | OpcUaTools.cs |
| 166 | GetPlcPutGetAccess | F07 | READ | worker | 20/21 | HardwareServicesTools.cs |
| 167 | GetPlcRunStateS7 | F04 | READ | worker | 20/21 | RuntimeTools.cs |
| 168 | GetPlcSimAdvancedTags | F04 | ONLINE | worker | 20/21 | PlcSimAdvancedTools.cs |
| 169 | GetPlcTagTableConstants | F09 | READ | worker | 20/21 | PlcTablesTools.cs |
| 170 | GetPlcWatchTableCurrentValuesReadOnly | F12 | READ | worker | 20/21 | PlcTablesTools.cs |
| 171 | GetPlcWebDiagnostics | F04 | ONLINE | worker | 20/21 | RuntimeChannelTools.cs |
| 172 | GetPlcWebVars | F04 | ONLINE | worker | 20/21 | RuntimeChannelTools.cs |
| 173 | GetPortalInfo | F05 | READ | worker | 20/21 | SessionTools.cs |
| 174 | GetProjectProtection | F07 | READ | worker | 20/21 | ProjectSecurityTools.cs |
| 175 | GetProjectSettings | F06 | READ | worker | 20/21 | ProjectSecurityTools.cs |
| 176 | GetProjectTopology | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 177 | GetProjectUserManagement | F07 | READ | worker | 20/21 | ProjectSecurityTools.cs |
| 178 | GetSafetyBlockSignatures | F30 | READ | worker | 20/21 | SafetyManagementTools.cs |
| 179 | GetSivarcRuleTree | F29 | READ | worker | 20/21 | SivarcTools.cs |
| 180 | GetTechnologyObjectTree | F13 | READ | worker | 20/21 | TechnologyObjectsTools.cs |
| 181 | GetUnifiedAlarmCommon | F27 | READ | worker | 20/21 | UnifiedUiModelTools.cs |
| 182 | GetUnifiedAuditSettings | F27 | READ | worker | 20/21 | UnifiedUiModelTools.cs |
| 183 | GetUnifiedCrossReferences | F27 | READ | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 184 | GetUnifiedFaceplateInstance | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 185 | GetUnifiedGlobalScript | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 186 | GetUnifiedGraphicSelection | F28 | READ | worker | 20/21 | GraphicSelectionTools.cs |
| 187 | GetUnifiedHmiButtonEvent | F26 | READ | worker | 20/21 | HmiInspectionTools.cs |
| 188 | GetUnifiedHmiDynamization | F26 | READ | worker | 20/21 | HmiInspectionTools.cs |
| 189 | GetUnifiedHmiTexts | F26 | READ | worker | 20/21 | UnifiedHmiTools.cs |
| 190 | GetUnifiedLibraryType | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 191 | GetUnifiedObjectEvents | F27 | READ | worker | 20/21 | UnifiedUiModelTools.cs |
| 192 | GetUnifiedObjectProperties | F27 | READ | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 193 | GetUnifiedPlantObject | F27 | READ | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 194 | GetUnifiedRuntimeAlarms | F04 | ONLINE | worker | 20/21 | RuntimeChannelTools.cs |
| 195 | GetUnifiedRuntimeSettings | F28 | READ | worker | 20/21 | RuntimeSettingsTools.cs |
| 196 | GetUnifiedRuntimeTags | F04 | ONLINE | worker | 20/21 | RuntimeChannelTools.cs |
| 197 | GetUnifiedScreenBranch | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 198 | GetV21EcosystemCatalog | F01 | READ | host | 20/21 | V21EcosystemTools.cs |
| 199 | GetVersionControlStatus | F33 | READ | worker | 20/21 | VersionControlTools.cs |
| 200 | ImportAlarmClasses | F14 | WRITE | worker | 20/21 | AlarmsTools.cs |
| 201 | ImportAlarmTextLists | F14 | WRITE | worker | 20/21 | AlarmsTools.cs |
| 202 | ImportDeviceAml | F21 | FILE | worker | 20/21 | HardwareAmlTools.cs |
| 203 | ImportHmiConnection | F24 | WRITE | worker | 20/21 | HmiExchangeTools.cs |
| 204 | ImportHmiScreen | F24 | WRITE | worker | 20/21 | HmiExchangeTools.cs |
| 205 | ImportHmiScreensFromDirectory | F24 | WRITE | worker | 20/21 | HmiExchangeTools.cs |
| 206 | ImportHmiTagTable | F24 | WRITE | worker | 20/21 | HmiExchangeTools.cs |
| 207 | ImportHmiTagTablesFromDirectory | F24 | WRITE | worker | 20/21 | HmiExchangeTools.cs |
| 208 | ImportLibraryTypeDocuments | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 209 | ImportMasterCopyFromGlobalLibrary | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 210 | ImportOpcUaInterface | F15 | WRITE | worker | 20/21 | OpcUaTools.cs |
| 211 | ImportPlcAlarmInstanceTexts | F14 | WRITE | worker | 20/21 | AlarmsTools.cs |
| 212 | ImportPlcBlockDocuments | F10 | WRITE | worker | 20/21 | DocumentsTools.cs |
| 213 | ImportPlcBlockVerified | F09 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 214 | ImportPlcBlocksDocuments | F10 | WRITE | worker | 20/21 | DocumentsTools.cs |
| 215 | ImportPlcTagTablesFromDirectory | F09 | WRITE | worker | 20/21 | PlcTablesTools.cs |
| 216 | ImportPlcWatchTableOffline | F12 | WRITE | worker | 20/21 | PlcTablesTools.cs |
| 217 | ImportProjectTexts | F06 | WRITE | worker | 20/21 | NativeExchangeTools.cs |
| 218 | ImportSinumerikAlarmTexts | F34 | WRITE | worker | 20/21 | V20OptionsTools.cs |
| 219 | ImportTechnologyObject | F13 | WRITE | worker | 20/21 | TechnologyObjectsTools.cs |
| 220 | ImportTechnologyObjectsFromDirectory | F13 | WRITE | worker | 20/21 | TechnologyObjectsTools.cs |
| 221 | ImportUnifiedEngineeringList | F27 | WRITE | worker | 20/21 | UnifiedEngineeringTools.cs |
| 222 | ImportUnifiedOpcUaAlarms | F28 | WRITE | worker | 20/21 | UnifiedExchangeTools.cs |
| 223 | InitializeSimotionScripting | F34 | WRITE | worker | 20/21 | V20OptionsTools.cs |
| 224 | InspectSimaticSdCompatibility | F02 | READ | host | 20/21 | EngineeringDiagnosticsTools.cs |
| 225 | InstantiatePlcTemplates | F02 | FILE | worker | 20/21 | TemplateTools.cs |
| 226 | InvokeObject | F35 | WRITE | worker | 20/21 | ReflectionTools.cs |
| 227 | InvokeService | F35 | WRITE | worker | 20/21 | ReflectionTools.cs |
| 228 | InvokeUnifiedOpenPipe | F04 | ONLINE-WRITE | worker | 20/21 | RuntimeChannelTools.cs |
| 229 | ListClassicHmiFaceplates | F25 | READ | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 230 | ListClassicHmiScripts | F25 | READ | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 231 | ListCommunicationConnections | F20 | READ | worker | 21 | HardwareServicesTools.cs |
| 232 | ListDccCharts | F31 | READ | worker | 20/21 | DccTools.cs |
| 233 | ListDeviceItemChannels | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 234 | ListDevices | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 235 | ListDriveObjects | F31 | READ | worker | 20/21 | StartdriveTools.cs |
| 236 | ListExportHandles | F01 | READ | host | 20/21 | ExportTools.cs |
| 237 | ListHmiConnections | F24 | READ | worker | 20/21 | HmiExchangeTools.cs |
| 238 | ListHmiScreenPaths | F24 | READ | worker | 20/21 | HmiInspectionTools.cs |
| 239 | ListHmiScreens | F24 | READ | worker | 20/21 | HmiExchangeTools.cs |
| 240 | ListHmiTagTables | F24 | READ | worker | 20/21 | HmiExchangeTools.cs |
| 241 | ListHmiTags | F24 | READ | worker | 20/21 | HmiExchangeTools.cs |
| 242 | ListIoSystems | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 243 | ListNetworkDomains | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 244 | ListObjectChildren | F35 | READ | worker | 20/21 | ReflectionTools.cs |
| 245 | ListPlcForceTables | F12 | READ | worker | 20/21 | PlcTablesTools.cs |
| 246 | ListPlcSimAdvancedInstances | F04 | ONLINE | worker | 20/21 | PlcSimAdvancedTools.cs |
| 247 | ListPlcSoftwareUnits | F16 | READ | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 248 | ListPlcSystemGroups | F08 | READ | worker | 20/21 | PlcExternalSourcesTools.cs |
| 249 | ListSafetyActivationTests | F30 | READ | worker | 21 | SafetyValidationTools.cs |
| 250 | ListSivarcBlockDefinitions | F29 | READ | worker | 20/21 | SivarcTools.cs |
| 251 | ListSivarcRules | F29 | READ | worker | 20/21 | OptionalEngineeringTools.cs |
| 252 | ListTestSuiteCases | F34 | READ | worker | 20/21 | TestSuiteTools.cs |
| 253 | ListToolCategories | F01 | READ | host | 20/21 | McpServer.ToolBridge.cs |
| 254 | ListTransferAreas | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 255 | ListTransferRoutes | F22 | READ | worker | 20/21 | OnlineDownloadTools.cs |
| 256 | ListUnifiedEngineeringObjects | F27 | READ | worker | 20/21 | UnifiedEngineeringTools.cs |
| 257 | ListUnifiedGlobalScripts | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 258 | ListUnifiedHmiApiTypes | F26 | READ | worker | 20/21 | UnifiedHmiTools.cs |
| 259 | ListUnifiedLibraryFolderEntries | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 260 | ListUnifiedTagDefinitions | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 261 | ListVersionControlWorkspaces | F33 | READ | worker | 20/21 | VersionControlTools.cs |
| 262 | ManageCfcChartProtection | F32 | WRITE | worker | 20/21 | CfcTools.cs |
| 263 | ManageClassicHmiCycle | F25 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 264 | ManageClassicHmiFolder | F25 | WRITE | worker | 20/21 | ClassicHmiFoldersTools.cs |
| 265 | ManageClassicHmiGraphic | F25 | WRITE | worker | 21 | ClassicHmiFoldersTools.cs |
| 266 | ManageClassicHmiScreenObject | F25 | WRITE | worker | 20/21 | ClassicHmiFoldersTools.cs |
| 267 | ManageClassicHmiScript | F25 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 268 | ManageClassicHmiTextGraphicList | F25 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 269 | ManageCommunicationConnection | F20 | WRITE | worker | 21 | HardwareServicesTools.cs |
| 270 | ManageDcbLibraries | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 271 | ManageDccBlock | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 272 | ManageDccChart | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 273 | ManageDccChartInterface | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 274 | ManageDccChartPartition | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 275 | ManageDccPin | F31 | WRITE | worker | 20/21 | DccTools.cs |
| 276 | ManageDeviceServiceObjects | F18 | WRITE | worker | 20/21 | HardwareServicesTools.cs |
| 277 | ManageDeviceUserGroup | F18 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 278 | ManageDeviceUsers | F07 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 279 | ManageDriveFunctions | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 280 | ManageDriveHardwareModule | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 281 | ManageDriveSafetyAcceptanceTest | F31 | WRITE | worker | 21 | StartdriveTools.cs |
| 282 | ManageDriveSecurity | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 283 | ManageDriveTelegrams | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 284 | ManageGlobalLibrary | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 285 | ManageHardwareObject | F18 | WRITE | worker | 20/21 | HardwareManagementTools.cs |
| 286 | ManageHardwareUtilities | F18 | FILE | worker | 20/21 | HardwareServicesTools.cs |
| 287 | ManageIoSystem | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 288 | ManageLibraryFolder | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 289 | ManageLibraryMasterCopy | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 290 | ManageLibraryType | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 291 | ManageLibraryTypeVersion | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 292 | ManageMotionAxis | F13 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 293 | ManageMultiuserSession | F05 | WRITE | worker | 20/21 | ProjectSecurityTools.cs |
| 294 | ManageNetworkDomain | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 295 | ManageOnlineDriveFunctions | F31 | ONLINE-WRITE | worker | 20/21 | StartdriveTools.cs |
| 296 | ManageOpcUaAccessControl | F15 | WRITE | worker | 20/21 | OpcUaTools.cs |
| 297 | ManageOpcUaInterface | F15 | WRITE | worker | 20/21 | OpcUaTools.cs |
| 298 | ManagePasswordPolicy | F07 | WRITE | worker | 20/21 | SecurityDeepTools.cs |
| 299 | ManagePlcAlarmTextList | F14 | WRITE | worker | 20/21 | AlarmsTools.cs |
| 300 | ManagePlcBlockDocuments | F10 | WRITE | worker | 20/21 | EngineeringAuditTools.cs |
| 301 | ManagePlcBlockProtection | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 302 | ManagePlcBlockWriteProtection | F16 | WRITE | worker | 21 | SoftwareUnitDeepTools.cs |
| 303 | ManagePlcCertificate | F07 | WRITE | worker | 20/21 | CertificateManagementTools.cs |
| 304 | ManagePlcDataBlockSnapshot | F22 | ONLINE | worker | 20/21 | PlcBlocksTools.cs |
| 305 | ManagePlcDocuments | F10 | WRITE | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 306 | ManagePlcExternalSources | F11 | WRITE | worker | 20/21 | PlcExternalSourcesTools.cs |
| 307 | ManagePlcGitRepository | F33 | FILE | worker | 20/21 | GitWorkflowTools.cs |
| 308 | ManagePlcProtection | F07 | WRITE | worker | 20/21 | HardwareServicesTools.cs |
| 309 | ManagePlcSafety | F30 | WRITE | worker | 20/21 | SafetyManagementTools.cs |
| 310 | ManagePlcSimAdvancedInstance | F04 | ONLINE-WRITE | worker | 20/21 | PlcSimAdvancedTools.cs |
| 311 | ManagePlcSoftwareUnit | F16 | WRITE | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 312 | ManagePlcSupervision | F14 | WRITE | worker | 20/21 | MotionProDiagClassicHmiTools.cs |
| 313 | ManagePlcTableEntries | F12 | WRITE | worker | 20/21 | PlcTablesTools.cs |
| 314 | ManagePlcTagDefinition | F09 | WRITE | worker | 20/21 | NativeExchangeTools.cs |
| 315 | ManagePlcUserGroup | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 316 | ManagePortInterconnection | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 317 | ManageProjectCompilationSettings | F06 | WRITE | worker | 20/21 | SoftwareUnitDeepTools.cs |
| 318 | ManageProjectLanguage | F06 | WRITE | worker | 20/21 | NativeExchangeTools.cs |
| 319 | ManageProjectUserManagement | F07 | WRITE | worker | 20/21 | ProjectSecurityTools.cs |
| 320 | ManageSafetyActivationTest | F30 | WRITE | worker | 21 | SafetyValidationTools.cs |
| 321 | ManageSafetyActivationTestGroup | F30 | WRITE | worker | 21 | SafetyValidationTools.cs |
| 322 | ManageSafetyFunction | F30 | WRITE | worker | 21 | SafetyValidationTools.cs |
| 323 | ManageSafetyFunctionCondition | F30 | WRITE | worker | 21 | SafetyValidationTools.cs |
| 324 | ManageSafetyGlobalSettings | F30 | WRITE | worker | 20/21 | SafetyManagementTools.cs |
| 325 | ManageSinumerikArchive | F34 | WRITE | worker | 20/21 | V20OptionsTools.cs |
| 326 | ManageSinumerikSafetyMode | F34 | WRITE | worker | 20/21 | V20OptionsTools.cs |
| 327 | ManageSivarcBlockDefinition | F29 | WRITE | worker | 20/21 | SivarcTools.cs |
| 328 | ManageSivarcRule | F29 | WRITE | worker | 20/21 | OptionalEngineeringTools.cs |
| 329 | ManageSivarcRuleContainer | F29 | WRITE | worker | 20/21 | SivarcTools.cs |
| 330 | ManageSivarcScreenLayout | F29 | WRITE | worker | 21 | SivarcTools.cs |
| 331 | ManageSivarcTableRule | F29 | WRITE | worker | 20/21 | SivarcTools.cs |
| 332 | ManageStartdriveParameter | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 333 | ManageSyslogServers | F07 | WRITE | worker | 20/21 | SecurityDeepTools.cs |
| 334 | ManageTeamcenterConnection | F33 | WRITE | worker | 20/21 | TeamcenterTools.cs |
| 335 | ManageTeamcenterDataset | F33 | WRITE | worker | 20/21 | TeamcenterTools.cs |
| 336 | ManageTeamcenterWorkflow | F33 | WRITE | worker | 20/21 | TeamcenterTools.cs |
| 337 | ManageTechnologyExtensions | F31 | WRITE | worker | 20/21 | StartdriveTools.cs |
| 338 | ManageTechnologyObject | F13 | WRITE | worker | 20/21 | TechnologyObjectsTools.cs |
| 339 | ManageTestSuiteCase | F34 | WRITE | worker | 20/21 | TestSuiteTools.cs |
| 340 | ManageTransferArea | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 341 | ManageUmcUsers | F07 | WRITE | worker | 20/21 | SecurityDeepTools.cs |
| 342 | ManageUnifiedCwcPackage | F03 | FILE | worker | 20/21 | V21EcosystemTools.cs |
| 343 | ManageUnifiedDynamization | F27 | WRITE | worker | 20/21 | UnifiedUiModelTools.cs |
| 344 | ManageUnifiedEngineeringObject | F27 | WRITE | worker | 20/21 | UnifiedEngineeringTools.cs |
| 345 | ManageUnifiedEvent | F27 | WRITE | worker | 20/21 | UnifiedEventsTools.cs |
| 346 | ManageUnifiedHmiGroup | F26 | WRITE | worker | 20/21 | UnifiedHmiGroupsTools.cs |
| 347 | ManageUnifiedListEntries | F27 | WRITE | worker | 20/21 | UnifiedUiModelTools.cs |
| 348 | ManageUnifiedLoggingTag | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 349 | ManageUnifiedObjectParts | F27 | WRITE | worker | 20/21 | UnifiedUiModelTools.cs |
| 350 | ManageUnifiedOpcUaAlarmType | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 351 | ManageUnifiedPlantNode | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 352 | ManageUnifiedScreenItem | F26 | WRITE | worker | 20/21 | UnifiedScreenItemsTools.cs |
| 353 | ManageUnifiedScreenLayout | F27 | WRITE | worker | 20/21 | UnifiedUiModelTools.cs |
| 354 | ManageWatchForceTableWebAccess | F07 | WRITE | worker | 20/21 | HardwareServicesTools.cs |
| 355 | MonitorPlcWatchTableS7 | F12 | ONLINE | worker | 20/21 | PlcTablesTools.cs |
| 356 | MovePlcBlockToGroup | F08 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 357 | PatchPlcBlockDocument | F02 | FILE | worker | 20/21 | PlcBlocksTools.cs |
| 358 | PlanGlobalLibraryTemplateReuse | F03 | OFFLINE | worker | 20/21 | LibraryTools.cs |
| 359 | PlanHardwareNetworkConfiguration | F20 | OFFLINE | worker | 20/21 | HardwareNetworkTools.cs |
| 360 | PlanOnlineReadOnlyDataProvider | F12 | ONLINE | worker | 20/21 | PlcTablesTools.cs |
| 361 | PlanOnlineReadOnlyMonitoring | F12 | ONLINE | worker | 20/21 | PlcTablesTools.cs |
| 362 | PlugDeviceItem | F18 | WRITE | worker | 20/21 | ModulesTools.cs |
| 363 | PreviewToolBatch | F01 | READ | host | 20/21 | McpServer.Batch.cs |
| 364 | PreviewToolCall | F01 | SESSION | host | 20/21 | McpServer.ToolBridge.cs |
| 365 | ProbeGlobalLibrary | F23 | READ | worker | 20/21 | LibraryTools.cs |
| 366 | ProbeHardwareHmiConnectionOwnerCandidates | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 367 | ProbeHardwareHmiConnectionWhitelistedServices | F20 | READ | worker | 20/21 | HardwareNetworkTools.cs |
| 368 | ProbePlcMonitorOnlineCapabilities | F12 | ONLINE | worker | 20/21 | PlcTablesTools.cs |
| 369 | ProbeS7CpuIdentity | F04 | READ | worker | 20/21 | RuntimeTools.cs |
| 370 | ReleaseUnifiedReadCursor | F28 | READ | worker | 20/21 | MigrationReadTools.cs |
| 371 | RenderPlcBlockDocument | F02 | OFFLINE | worker | 20/21 | PlcDocumentationTools.cs |
| 372 | RenderPlcVisualDiff | F02 | FILE | host | 20/21 | EcosystemTools.cs |
| 373 | RepairAndReimportPlcBlock | F09 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 374 | ResolveSivarcExpression | F29 | READ | worker | 20/21 | SivarcTools.cs |
| 375 | RestartOpennessWorker | F01 | WRITE | host | 20/21 | McpServer.Worker.cs |
| 376 | RetrieveProjectArchive | F05 | WRITE | worker | 20/21 | NativeExchangeTools.cs |
| 377 | RunClassicHmiOfflineValidationSuite | F03 | EXECUTE | worker | 20/21 | OfflineSuiteTools.cs |
| 378 | RunClassicHmiTemporaryImportPreflight | F03 | EXECUTE | worker | 20/21 | OfflineSuiteTools.cs |
| 379 | RunHmiActionScriptRecipeSafetySelfTest | F03 | EXECUTE | worker | 20/21 | UnifiedHmiTools.cs |
| 380 | RunHmiTemplatePlcSyncPrecheckSuite | F03 | EXECUTE | worker | 20/21 | OfflineSuiteTools.cs |
| 381 | RunOfflineReleaseValidationSuite | F01 | EXECUTE | worker | 20/21 | OfflineSuiteTools.cs |
| 382 | RunOnlineMonitoringSafetySelfTest | F01 | EXECUTE | worker | 20/21 | DiagnosticsTools.cs |
| 383 | RunPlcCompanionTool | F04 | ONLINE | worker | 20/21 | EcosystemTools.cs |
| 384 | RunPlcSimAdvancedTestScenario | F04 | EXECUTE | worker | 20/21 | PlcSimAdvancedTools.cs |
| 385 | RunReadOnlyToolBatch | F01 | READ | host | 20/21 | McpServer.Batch.cs |
| 386 | RunTestSuiteCase | F34 | EXECUTE | worker | 20/21 | TestSuiteTools.cs |
| 387 | RunToolTransaction | F06 | WRITE | worker | 20/21 | ProjectSessionTools.cs |
| 388 | SamplePlcLiveValuesS7 | F04 | ONLINE | worker | 20/21 | RuntimeTools.cs |
| 389 | SaveExportContent | F01 | FILE | host | 20/21 | ExportTools.cs |
| 390 | SaveProjectCopy | F05 | FILE | worker | 20/21 | ProjectSessionTools.cs |
| 391 | ScanAccessibleDevices | F22 | ONLINE | worker | 20/21 | OnlineDownloadTools.cs |
| 392 | ScanPlcSourceAnnotations | F02 | FILE | host | 20/21 | OfflineAnalysisTools.cs |
| 393 | SearchInstalledGsdDevices | F18 | READ | worker | 20/21 | DevicesTools.cs |
| 394 | SeedProjectFromReference | F09 | WRITE | worker | 20/21 | TypesTools.cs |
| 395 | SetDeviceAddress | F19 | WRITE | worker | 20/21 | AddressesTools.cs |
| 396 | SetDeviceItemAttribute | F18 | WRITE | worker | 20/21 | DevicesTools.cs |
| 397 | SetDeviceItemChannel | F20 | WRITE | worker | 20/21 | HardwareNetworkTools.cs |
| 398 | SetDeviceItemIoAddress | F19 | WRITE | worker | 20/21 | AddressesTools.cs |
| 399 | SetOpcUaInterfaceEnabled | F15 | WRITE | worker | 20/21 | OpcUaTools.cs |
| 400 | SetPlcCpuSettings | F18 | WRITE | worker | 20/21 | DevicesTools.cs |
| 401 | SetPlcProgram | F09 | WRITE | worker | 20/21 | PlcBlocksTools.cs |
| 402 | SetPlcPutGetAccess | F07 | WRITE | worker | 20/21 | HardwareServicesTools.cs |
| 403 | SetPlcUnitObjectAccess | F16 | WRITE | worker | 20/21 | SoftwareUnitManagementTools.cs |
| 404 | SetPlcWatchTableModifyValue | F12 | ONLINE-WRITE | worker | 20/21 | PlcTablesTools.cs |
| 405 | SetPlcWebOperatingMode | F04 | ONLINE-WRITE | worker | 20/21 | RuntimeChannelTools.cs |
| 406 | SetUnifiedGlobalScript | F28 | WRITE | worker | 20/21 | GlobalScriptEditTools.cs |
| 407 | SetUnifiedHmiButtonEventScriptCode | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 408 | SetUnifiedHmiRuntimeState | F26 | WRITE | worker | 20/21 | UnifiedHmiTools.cs |
| 409 | SetUnifiedLogDuration | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 410 | SetUnifiedMultilingualProperty | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 411 | SetUnifiedObjectProperties | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 412 | SetUnifiedPlantObject | F27 | WRITE | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 413 | SetUnifiedRuntimeSettings | F28 | WRITE | worker | 20/21 | RuntimeSettingsTools.cs |
| 414 | ShowObjectInEditor | F06 | WRITE | worker | 20/21 | ProjectSessionTools.cs |
| 415 | SynchronizeLibrary | F23 | WRITE | worker | 20/21 | LibraryTools.cs |
| 416 | SynchronizeVersionControlWorkspace | F33 | WRITE | worker | 20/21 | VersionControlTools.cs |
| 417 | TraceTagCause | F12 | READ | worker | 20/21 | RuntimeTools.cs |
| 418 | TraceTagCauseLive | F12 | ONLINE | worker | 20/21 | RuntimeTools.cs |
| 419 | UpgradeSivarcDefinitions | F29 | WRITE | worker | 20/21 | SivarcTools.cs |
| 420 | UploadDeviceParameters | F22 | ONLINE-WRITE | worker | 20/21 | OnlineDownloadTools.cs |
| 421 | UploadStationFromPlc | F22 | ONLINE-WRITE | worker | 20/21 | OnlineDownloadTools.cs |
| 422 | ValidateAutomationContext | F05 | READ | worker | 20/21 | DevicesTools.cs |
| 423 | ValidateClassicHmiMinimalPackageFiles | F03 | READ | worker | 20/21 | OfflineSuiteTools.cs |
| 424 | ValidateClassicHmiMinimalPackagePlcSync | F03 | READ | worker | 20/21 | OfflineSuiteTools.cs |
| 425 | ValidatePlcDocumentSchemas | F02 | OFFLINE | host | 20/21 | V21EcosystemTools.cs |
| 426 | ValidateUnifiedObject | F27 | READ | worker | 20/21 | UnifiedObjectServicesTools.cs |
| 427 | WriteClassicHmiMinimalPackageFiles | F03 | FILE | worker | 20/21 | OfflineSuiteTools.cs |
| 428 | WritePlcSclSourceFile | F02 | OFFLINE | worker | 20/21 | PlcExternalSourcesTools.cs |
| 429 | WritePlcSimAdvancedTags | F04 | ONLINE-WRITE | worker | 20/21 | PlcSimAdvancedTools.cs |
| 430 | WritePlcWebVars | F04 | ONLINE-WRITE | worker | 20/21 | RuntimeChannelTools.cs |
| 431 | WriteUnifiedRuntimeTags | F04 | ONLINE-WRITE | worker | 20/21 | RuntimeChannelTools.cs |

## 附录 B：API 证据

证据文件（仓库 `sdk/` 下，本地不分发）：

| 发布键 | XML 文档 | 元数据（DLL） |
|---|---|---|
| 14sp1 | `TIA_V14SP1_PublicAPI/V14 SP1/Siemens.Engineering.xml`、`Siemens.Engineering.Hmi.xml` | 同目录 `Siemens.Engineering.dll`、`Siemens.Engineering.Hmi.dll` |
| 15.1 | `TIA_V15.1_PublicAPI/V15.1/Siemens.Engineering.xml`、`.Hmi.xml` | 同目录同名 DLL |
| 16 | `TIA_V16_PublicAPI/V16/Siemens.Engineering.xml`、`.Hmi.xml` | 同上 |
| 17 | `TIA_V17_PublicAPI/V17/Siemens.Engineering.xml`、`.Hmi.xml` | 同上 |
| 18 | `TIA_V18_PublicAPI/V18/Siemens.Engineering.xml`、`.Hmi.xml` | 同上 |
| 19 | `TIA_V19_PublicAPI/V19/Siemens.Engineering.xml`、`.Hmi.xml` | 同上 |
| 20 | `TIA_V20_PublicAPI/V20/Siemens.Engineering.xml`、`.Hmi.xml` | 同上 |
| 21 | `TIA_V21_PublicAPI/V21/net48/Siemens.Engineering.{Base,Step7,Safety,SafetyValidation,WinCC,WinCC.Extension,WinCCUnified,Startdrive,DCC,CFC,Sivarc,TestSuite,TeamcenterGateway}.xml` | 同目录同名 DLL（不含 AddIn） |

各版 XML 成员数：14sp1 3,441；15.1 8,635；16 13,663；17 17,212；18 20,679；19 26,895；20 30,019；21 33,421。各版公共类型命名空间的出现区间（XML `T:` 统计）可复核：例如 `Siemens.Engineering.HmiUnified` 19–21、`SW.Units` 16–21、`Safety` 16–21、`SafetyValidation` 仅 21、`CrossReference` 18–21、`SW.FunctionCharts` 18–21、`TeamcenterGateway` 18–21、`VersionControl` 16–21、`Multiuser` 17–21、`MC.Sinumerik` 16–20、`SCADAExporter` 17–20、`Simotion` 14sp1/15.1/17–20、`Startdrive`（旧）仅 14sp1、`MC.Drives` 15.1–21。

F01–F04 不调用 Openness：431 个工具中，这四族工具方法体除 `RenderPlcBlockDocument`（可选在线导出）与 `GetPlcLiveValuesS7`（可选 Put/Get 预检）外，不访问引擎会话、Portal 或 `GetService`；`src/Runtime/TiaMcp.Runtime.csproj` 不引用 Siemens.Engineering。

逐族关键成员（列为 14sp1…21，标记见第 0 节）：

**F05 门户会话与工程生命周期**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.TiaPortal.GetProcesses` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.TiaPortalProcess.AttachedSessions` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Project.SaveAs` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Project.Archive` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.ProjectComposition.Retrieve` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.ProjectComposition.Create` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Multiuser.LocalSession` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Multiuser.MarkingService` |
| . | . | . | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Multiuser.ProjectServer.CreateLocalSession` |
| . | . | . | . | . | . | Y | Y | `M:Siemens.Engineering.Multiuser.MarkingService.MarkObjects` |
| . | . | . | Y | Y | Y | Y | Y | `P:Siemens.Engineering.TiaPortal.ProjectServers` |

**F06 工程服务：语言与项目文本、设置、比较、对象标识、事务**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.Project.LanguageSettings` （部分版本经 `ProjectBase` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Project.ExportProjectTexts` （部分版本经 `ProjectBase` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Project.ImportProjectTexts` （部分版本经 `ProjectBase` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.TiaPortal.SettingsFolders` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.PlcSoftware.CompareTo` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.Device.CompareTo` （部分版本经 `HW.HardwareObject` 提供） |
| . | . | . | . | Y | Y | Y | Y | `M:Siemens.Engineering.Library.ProjectLibrary.CompareToLibrary` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.ObjectIdentifierProvider` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.IShowable.ShowInEditor` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.ExclusiveAccess.Transaction` |
| . | . | Y | Y | Y | Y | Y | . | `P:Siemens.Engineering.Project.IsSimulationDuringBlockCompilationEnabled` （部分版本经 `ProjectBase` 提供） |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SW.PlcSimulationSettingsProvider` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.CustomIdentity.CustomIdentityProvider` |

**F07 安全：工程保护、UMAC/UMC、证书、PLC 访问保护与设备用户**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.AdvancedProtection.ProtectionProviderBase` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.PlcAccessLevelProvider` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Security.LocalCertificateManager` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.WebserverUserManagement` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.WatchAndForceTableAccessManager` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Umac.UmacConfigurator` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Umac.UmcServerConfigurator` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.PlcMasterSecretConfigurator` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.Umac.PasswordPolicyConfigurator` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.Security.PlcPasswordPolicyService` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.SimpleWebserverUserManagement` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HW.Features.SysLogConfigurationManager` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.Security.SyslogServerProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Download.Configurations.PlcMasterSecretPassword` |

**F08 PLC 对象组织：分组、删除、移动、块保护、系统组**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlockUserGroupComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Types.PlcTypeUserGroupComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlock.Delete` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Types.PlcType.Delete` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Tags.PlcTagTable.Delete` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.Blocks.PlcBlockSystemGroup.SystemBlockGroups` |
| . | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.Types.PlcTypeSystemGroup.SystemTypeGroups` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Blocks.PlcBlockProtectionProvider` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableUserGroup` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroupComposition.Create` |

**F09 PLC 读改扩展：变量/常量定义、实例 DB、校验导入、修补重导、程序更新**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Tags.PlcTagComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Tags.PlcUserConstantComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.Tags.PlcTagTable.SystemConstants` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlockComposition.CreateInstanceDB` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlockComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlock.Export` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Tags.PlcTagTableComposition.Import` |
| . | . | . | . | Y | Y | Y | Y | `M:Siemens.Engineering.SW.PlcSoftware.UpdateProgram` |

**F10 SIMATIC SD 文档交换与块作用域**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | . | . | . | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlock.ExportAsDocuments` |
| . | . | . | . | . | . | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlockComposition.ImportFromDocuments` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.SW.PlcDocument` |
| . | . | . | . | . | . | Y | Y | `P:Siemens.Engineering.SW.Types.PlcTypeGroup.Documents` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.SW.DocumentExportResult` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.PlcUnit` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.PlcSafetyUnit` |

**F11 外部源扩展、源生成与交叉引用**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.ExternalSources.PlcExternalSourceComposition.CreateFromFile` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.ExternalSources.PlcExternalSourceSystemGroup.GenerateSource` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.ExternalSources.PlcExternalSourceUserGroup.Delete` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.CrossReference.CrossReferenceService` |
| . | . | . | . | Y | Y | Y | Y | `M:Siemens.Engineering.CrossReference.CrossReferenceService.GetCrossReferences` |

**F12 监控/强制表（离线）、在线监视与因果追踪**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableComposition` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableComposition.Import` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable.Export` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.WatchAndForceTables.PlcForceTableComposition` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.WatchAndForceTables.PlcForceTableComposition.Import` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.WatchAndForceTables.PlcForceTable.Export` |
| . | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable.Entries` |
| . | . | . | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry.Address` |
| . | . | . | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTableEntry.ModifyValue` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.PlcBlock.Export` |

**F13 工艺对象与运动控制**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB.Export` |
| . | . | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBComposition.Import` |
| . | . | . | . | . | Y | Y | Y | `P:Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup.Groups` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionProvider` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.TechnologicalObjects.Motion.OutputCamHardwareConnectionProvider` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.TechnologicalObjects.Motion.CamDataSupport` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SW.TechnologicalObjects.Motion.AxisHardwareConnectionSDRProvider` |

**F14 PLC 报警、ProDiag 与 Supervision**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Alarm.PlcAlarmTextListProvider` |
| . | . | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Alarm.PlcAlarmTextListProvider.ExportToXlsx` |
| . | . | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Alarm.PlcAlarmTextProvider.ImportInstanceTextsFromXlsx` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Alarm.AlarmClassDataProvider` |
| . | . | . | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Blocks.CodeBlock.ExportProDIAGInfo` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Supervision.SupervisionProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Alarm.TextLists.PlcAlarmTextlistGroup` |

**F15 PLC OPC UA 服务器接口与访问控制**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.OpcUa.OpcUaProvider` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.OpcUa.ServerInterfaceGroup` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.OpcUa.ServerInterface.Export` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.OpcUa.ServerInterface.Import` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.OpcUa.ServerInterfaceComposition.Create` |
| . | . | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.OpcUa.ServerInterfaceGroup.SimaticInterfaces` |
| . | . | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.SW.OpcUa.ServerInterfaceGroup.ReferenceNamespaces` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.OpcUaUserManagement` |

**F16 软件单元与 PLC 完整性（校验和/指纹/写保护）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.PlcChecksumProvider` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.FingerprintProvider` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.PlcUnitProvider` |
| . | . | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.Units.PlcUnitComposition.Create` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.UnitAccessType` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.PlcUnitRelation` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Units.PlcSafetyUnit` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SW.Blocks.PlcBlockWriteProtectionProvider` |

**F17 设备与 HMI 编译**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Compiler.ICompilable` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Compiler.CompilerResult` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.HmiTarget` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiSoftware` |

**F18 硬件设备、模块、属性、设备分组与硬件服务**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.DeviceComposition.CreateWithItem` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.DeviceComposition.CreateFrom` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.HardwareObject.PlugNew` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.HardwareObject.GetPlugLocations` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.HardwareObject.PlugMove` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.DeviceItem.Delete` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.DeviceUserGroupComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.Project.HwUtilities` （部分版本经 `ProjectBase` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Utilities.ModuleInformationProvider` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.HW.HardwareCatalog.HardwareCatalog` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Utilities.CardReaderPscProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Systemdiagnostics.Settings.SystemdiagnosticsSettingsDataProvider` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HW.Features.TelecontrolManagement` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.HW.Features.DefaultWebPagesFeature` |

**F19 硬件地址（IO/IP/HW 标识）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.DeviceItem.Addresses` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.Address.StartAddress` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.DeviceItem.HwIdentifiers` （部分版本经 `HW.HardwareObject` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.AddressController` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.HwIdentifierController` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.Features.NetworkInterface.Nodes` |
| Y | Y | Y | Y | Y | Y | Y | . | `M:Siemens.Engineering.HW.Address.AssignProcessImageToOrganizationBlock` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SW.ProcessImageProvider` |

**F20 网络：子网、IO 系统、拓扑、域、通道、传输区、通信连接**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.SubnetComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.Node.ConnectToSubnet` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.IoController.CreateIoSystem` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.IoConnector.ConnectToIoSystem` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.HW.Features.NetworkPort.ConnectToPort` |
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.DeviceItem.Channels` |
| . | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.HW.Features.NetworkInterface.TransferAreas` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.MrpDomainOwner` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.HW.Features.SyncDomainOwner` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HW.Features.MrpInstancesOwner` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition` ；同简名类型（不视为等价）：14sp1: `Hmi.Communication.ConnectionComposition`; 15.1: `Hmi.Communication.ConnectionComposition`; 16: `Hmi.Communication.ConnectionComposition`; 17: `Hmi.Communication.ConnectionComposition`; 18: `Hmi.Communication.ConnectionComposition`; 19: `Hmi.Communication.ConnectionComposition`; 20: `Hmi.Communication.ConnectionComposition` |

**F21 CAx / AutomationML 设备交换**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Cax.CaxProvider` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Cax.CaxProvider.Export` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Cax.CaxProvider.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Cax.CaxImportOptions` |

**F22 在线、下载、上传、在线比较与在线数据**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Online.OnlineProvider.GoOnline` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Connection.ConnectionConfiguration.ApplyConfiguration` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SW.PlcSoftware.CompareToOnline` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Download.DownloadProvider.Download` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Upload.StationUploadProvider.StationUpload` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Blocks.InterfaceSnapshot` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.FingerprintData.FingerprintDataProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Online.Configurations.TlsVerificationConfiguration` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.Loader.LoadableProvider` |
| . | . | . | . | . | Y | Y | Y | `M:Siemens.Engineering.Connection.ConfigurationPcInterface.GetAccessibleDevices` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SW.Blocks.Interface.ValueService` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.Upload.ParameterUploadProvider` |

**F23 项目库与全局库**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `P:Siemens.Engineering.Project.ProjectLibrary` （部分版本经 `ProjectBase` 提供） |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Library.GlobalLibraryComposition.Open` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Library.MasterCopies.MasterCopyComposition.Create` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Library.ProjectLibrary.UpdateCheck` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Library.ProjectLibrary.UpdateProject` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Library.Types.LibraryTypeVersion.Export` |
| . | . | . | . | Y | Y | Y | Y | `M:Siemens.Engineering.Library.ProjectLibrary.CompareToLibrary` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.Library.Compare.LibraryCompareResult` |
| . | . | . | . | . | . | Y | Y | `M:Siemens.Engineering.Library.Types.LibraryTypeComposition.CreateFromDocuments` |

**F24 HMI 通用读取与 XML 交换（Classic/Unified 反射路径）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.HmiTarget` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Screen.ScreenComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Screen.Screen.Export` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Tag.TagTableComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Communication.ConnectionComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Tag.Tag.Delete` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiSoftware` |
| . | . | . | . | . | Y | Y | Y | `M:Siemens.Engineering.HmiUnified.HmiTags.HmiTagComposition.Export` |

**F25 Classic HMI 工程（画面树、文件夹、脚本、周期、文本/图形列表、面板、多语言图形）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.Screen.ScreenUserFolder` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.RuntimeScripting.VBScriptComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.Hmi.Cycle.CycleComposition.Import` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.TextGraphicList.TextList` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryType` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Hmi.Globalization.MultiLingualGraphic` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.Hmi.Globalization.GraphicsProvider` |

**F26 Unified 画面建造与画面元素（Ensure/Apply/Bind/事件/动态化/分组）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiSoftware` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Dynamization.Script.ScriptDynamization` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.ScreenGroup.HmiScreenGroup` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiConnections.HmiConnection` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Events.HmiButtonEventHandler` |

**F27 Unified 对象模型：对象服务、UI 模型、事件、工程对象（报警/日志/列表）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.Cpm.PlantView` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.HmiUnified.Cpm.PlantViewsProvider` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmClass` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiLogging.HmiDataLog` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.LoggingTags.HmiLoggingTag` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiOpcUaAlarm.HmiOpcUaAlarmType` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.TextGraphicList.HmiTextList` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.TextGraphicList.HmiSystemTextList` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiAudit.HmiAlarmAuditClass` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Dynamization.ExpressionDynamization` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.UI.Dynamization.TagParameterDynamization` |

**F28 Unified 交换、脚本、运行时设置、图形选择与迁移只读**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | . | . | Y | Y | Y | `M:Siemens.Engineering.HmiUnified.HmiTags.HmiTagComposition.Import` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.Scripts.HmiScriptModule` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.RuntimeSettings.HmiRuntimeSetting` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.Library.ScriptModuleType` |
| . | . | . | . | . | Y | Y | Y | `T:Siemens.Engineering.HmiUnified.HmiOpcUaAlarm.HmiOpcUaAlarmType` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.HmiUnified.RuntimeSettings.HmiRuntimeSettingsCommon.GeneralESIGCommentsStrategy` |

**F29 SiVArc**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SiVArc.Sivarc` |
| . | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.SiVArc.Sivarc.Generate` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SiVArc.ScreenRule` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SiVArc.SivarcDataProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.SiVArc.TagDefinition` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SiVArc.AlarmRuleTable` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.SiVArc.SivarcDefinitionsUpgrader` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SiVArc.LayoutData` |

**F30 Safety（F 程序管理与 V21 安全验证）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Safety.GlobalSettings` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Safety.SafetyAdministration` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Safety.SafetySignatureProvider` |
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.Safety.SafetyPrintout` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.SafetyValidation.SafetyValidationAssistant` |

**F31 Startdrive 驱动与 DCC**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.DriveObjectContainer` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.DriveParameter` ；同简名类型（不视为等价）：14sp1: `Startdrive.DriveParameter` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.Telegram` ；同简名类型（不视为等价）：14sp1: `Startdrive.Telegram` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.DFI.DriveFunctionInterface` |
| . | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.OnlineDriveObjectContainer` |
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.Dcc.DriveControlChartContainer` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.Dcc.DccBlock` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.MC.Drives.TechnologyExtensionContainer` |
| . | . | . | . | . | Y | Y | Y | `P:Siemens.Engineering.MC.Drives.DriveObject.Security` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.MC.Drives.ReadDriveParameter` |
| . | . | . | . | . | . | . | Y | `T:Siemens.Engineering.MC.Drives.DriveItemHardwareModule` |
| Y | . | . | . | . | . | . | . | `T:Siemens.Engineering.Startdrive.DriveObject` ；同简名类型（不视为等价）：15.1: `MC.Drives.DriveObject`; 16: `MC.DriveConfiguration.DriveObject, MC.Drives.DriveObject`; 17: `MC.DriveConfiguration.DriveObject, MC.Drives.DriveObject`; 18: `MC.DriveConfiguration.DriveObject, MC.Drives.DriveObject`; 19: `MC.DriveConfiguration.DriveObject, MC.Drives.DriveObject`; 20: `MC.DriveConfiguration.DriveObject, MC.Drives.DriveObject`; 21: `MC.Drives.DriveObject` |

**F32 CFC 图表**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.FunctionCharts.ChartProvider` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.SW.FunctionCharts.ChartProviderS7` |
| . | . | . | . | Y | Y | Y | Y | `M:Siemens.Engineering.SW.FunctionCharts.ChartProvider.CompleteExport` |

**F33 版本控制与协同：VCI、Git、Teamcenter**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.VersionControl.VersionControlInterface` |
| . | . | Y | Y | Y | Y | Y | . | `T:Siemens.Engineering.VersionControl.WorkspaceMapping` |
| . | . | . | . | . | . | Y | Y | `T:Siemens.Engineering.VersionControl.MappedObject` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.TeamcenterGateway.TeamcenterConnectionProvider` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.TeamcenterGateway.TcGatewaySearchAndDownloadProvider` |

**F34 Test Suite 与 V20 可选产品（SINUMERIK/SIMOTION/SCADA Export）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| . | . | . | Y | Y | Y | Y | Y | `T:Siemens.Engineering.TestSuite.TestSuiteService` |
| . | . | . | . | Y | Y | Y | Y | `T:Siemens.Engineering.TestSuite.SystemTest.SystemTestCaseExecutor` |
| . | . | . | Y | Y | Y | Y | . | `T:Siemens.Engineering.SCADAExporter.ScadaExportProvider` |
| . | . | Y | Y | Y | Y | Y | . | `T:Siemens.Engineering.MC.Sinumerik.SafetyModeProvider` |
| . | . | . | . | Y | Y | Y | . | `T:Siemens.Engineering.MC.Sinumerik.SinumerikAlarmTextProvider` |
| . | . | Y | Y | Y | Y | Y | . | `T:Siemens.Engineering.HW.Utilities.SinumerikArchiveProvider` |
| Y | Y | . | Y | Y | Y | Y | . | `T:Siemens.Engineering.Simotion.SimotionProvider` |

**F35 Openness 反射（对象/服务通用访问）**

| 14sp1 | 15.1 | 16 | 17 | 18 | 19 | 20 | 21 | 成员 id |
|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.IEngineeringObject.GetAttributeInfos` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.IEngineeringObject.GetCompositionInfos` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.IEngineeringObject.GetInvocationInfos` |
| Y | Y | Y | Y | Y | Y | Y | Y | `M:Siemens.Engineering.IEngineeringObject.Invoke` |
| Y | Y | Y | Y | Y | Y | Y | Y | `T:Siemens.Engineering.EngineeringServiceInfo` |
| m | m | m | m | m | m | m | m | `M:Siemens.Engineering.IEngineeringServiceProvider.GetService` |

复核方法：XML 侧按 `<member name="…">` 精确匹配（方法忽略参数表）；元数据侧用 `System.Reflection.Metadata` 读取上表 DLL 的 TypeDef、公共 MethodDef、PropertyDef 及基类/接口，成员沿继承链查找。两侧均为只读，不加载程序集。调查脚本放在会话临时目录，未入库。
