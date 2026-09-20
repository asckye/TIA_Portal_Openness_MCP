# 真机台账（逐工具）

[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [交接](../development/handoff.md)

2026-09-20 在维护者新建的空工程 `项目1`（TIA Portal V21，引擎 2.7.45）里用引擎自建的设备把全部 447 个工具各跑了一遍：`MCP_PLC`（CPU 1515F-2 PN V2.9）、`MCP_TP700`（TP700 Comfort V17）、`MCP_UCP`（MTP700 Unified Comfort V21）、`MCP_S120`（S120 CU320-2 PN V5.2 + 驱动轴_1：电机模块 / 电机 / 编码器）；批跑器每步之后检查 TIA 进程还在不在，结果按工具记录在此。状态：

| 状态 | 含义 | 数量 |
|---|---|---:|
| ✅ 通过 | 该工具至少一次真实调用成功（读回验证） | 265 |
| ✅ 手工单跑 | 会话级工具，单独手工跑通 | 6 |
| ✅ 早期真机 | 今天没跑，但 2.7.39–2.7.45 的真机会话跑过 | 26 |
| 🔁 2.7.46 已修 | 今天真机暴露了缺陷，2.7.46 源码已修，部署后要重跑 | 45 |
| ⛔ TIA/环境拒绝 | 调用到 TIA/环境，被其规则拒绝或对象不提供（不是引擎缺陷） | 22 |
| ⚠ 参数/前置条件 | 只跑到参数/前置条件拒绝（工具逻辑正常，需要更完整的对象或输入） | 64 |
| 🚫 不运行 | 刻意不跑 | 7 |
| ❌ 未跑 | 未跑 | 12 |

TIA 退出点（都已写进交接 §5）：⑧ `AddDevice` 建 WinCC Unified 面板用了 `/20.0.0.0` 标识（TIA V21）；⑨ `ManageTechnologyObject create TO_PositioningAxis 6.0`（1515F-2 PN V2.9）。

## Bootstrap（1）

| 工具 | 状态 | 说明 |
|---|---|---|
| `Bootstrap` | ✅ 通过 |  |

## Diagnostics（5）

| 工具 | 状态 | 说明 |
|---|---|---|
| `Doctor` | ✅ 通过 |  |
| `RunCapabilitySelfTest` | ✅ 通过 |  |
| `RunHmiActionScriptRecipeSafetySelfTest` | ✅ 通过 |  |
| `RunOnlineMonitoringSafetySelfTest` | 🔁 2.7.46 已修 | 把 ManageWatchForceTableWebAccess 当强制写工具（名字启发式误报）；2.7.46 白名单 |
| `ValidateAutomationContext` | ✅ 通过 |  |

## Exports（5）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ClearExports` | ✅ 通过 |  |
| `DeleteExport` | ✅ 通过 |  |
| `GetExport` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ListExports` | ✅ 通过 |  |
| `SaveExport` | ✅ 通过 |  |

## Guide（1）

| 工具 | 状态 | 说明 |
|---|---|---|
| `GetAuthoringGuide` | ✅ 通过 |  |

## HMI（30）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CompileAndDiagnoseHmi` | ⛔ TIA/环境拒绝 | 新面板没有起始画面（'A start screen has not been configured'）——编译诊断本身正常 |
| `DescribeHmiScreen` | 🔁 2.7.46 已修 | 依赖画面导入（见 ImportHmiScreen） |
| `DescribeHmiScreenItem` | 🔁 2.7.46 已修 | 依赖画面导入（见 ImportHmiScreen） |
| `DescribeHmiSoftware` | ✅ 通过 |  |
| `DescribeHmiTag` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `DescribeHmiTagTable` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `ExportHmiConnection` | ⚠ 参数/前置条件 | 无 HMI 连接对象 / 文件不存在 |
| `ExportHmiProgram` | ✅ 通过 |  |
| `ExportHmiScreen` | 🔁 2.7.46 已修 | 依赖画面导入（见 ImportHmiScreen） |
| `ExportHmiTagTable` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `GenerateSiVArc` | ✅ 通过 |  |
| `GetHmiConnections` | ✅ 通过 |  |
| `GetHmiProgramInfo` | ✅ 通过 |  |
| `GetHmiScreens` | ✅ 通过 |  |
| `GetHmiTagTables` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `GetHmiTags` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `ImportHmiConnection` | ⚠ 参数/前置条件 | 无 HMI 连接对象 / 文件不存在 |
| `ImportHmiScreen` | 🔁 2.7.46 已修 | TP700 Comfort V17 拒绝 Button 的 <Visible>（set_Visible not supported）；2.7.46 剥掉该属性重试 |
| `ImportHmiScreensFromDirectory` | 🔁 2.7.46 已修 | TP700 Comfort V17 拒绝 Button 的 <Visible>（set_Visible not supported）；2.7.46 剥掉该属性重试 |
| `ImportHmiTagTable` | 🔁 2.7.46 已修 | 导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound |
| `ImportHmiTagTablesFromDirectory` | ✅ 通过 |  |
| `ListHmiScreenPaths` | ✅ 通过 |  |
| `ManageSiVArcRule` | ⚠ 参数/前置条件 | 需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过 |
| `ManageSivarcRuleContainer` | ✅ 通过 |  |
| `ManageSivarcScreenLayout` | 🔁 2.7.46 已修 | 依赖画面导入（见 ImportHmiScreen） |
| `ManageSivarcTableRule` | ⚠ 参数/前置条件 | 需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过 |
| `ReadHmiScreenSnapshot` | 🔁 2.7.46 已修 | 依赖画面导入（见 ImportHmiScreen） |
| `ReadSiVArcRules` | ✅ 通过 |  |
| `ReadSivarcRuleTree` | ✅ 通过 |  |
| `ResolveSivarcExpression` | ⚠ 参数/前置条件 | 需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过 |

## HMI-Classic（18）

| 工具 | 状态 | 说明 |
|---|---|---|
| `BuildClassicHmiMinimalPackage` | ✅ 通过 |  |
| `BuildClassicHmiScreenXml` | ✅ 通过 |  |
| `BuildClassicHmiTagTableXml` | ✅ 通过 |  |
| `ManageClassicHmiCycle` | ✅ 通过 |  |
| `ManageClassicHmiFolder` | ✅ 通过 |  |
| `ManageClassicHmiGraphic` | ⛔ TIA/环境拒绝 | TP700 Comfort V17 的 HmiTarget 不提供 GraphicsProvider |
| `ManageClassicHmiScreenObject` | ✅ 通过 |  |
| `ManageClassicHmiScript` | ✅ 通过 |  |
| `ManageClassicHmiTextGraphicList` | ✅ 通过 |  |
| `ReadClassicHmiFaceplates` | ✅ 通过 |  |
| `ReadClassicHmiGlobalization` | ⛔ TIA/环境拒绝 | TP700 Comfort V17 的 HmiTarget 不提供 GraphicsProvider |
| `ReadClassicHmiScreenTree` | ✅ 通过 |  |
| `ReadClassicHmiScripts` | ✅ 通过 |  |
| `RunClassicHmiOfflineValidationSuite` | ✅ 通过 |  |
| `RunClassicHmiTemporaryImportPreflight` | ✅ 通过 |  |
| `ValidateClassicHmiMinimalPackageFiles` | ✅ 通过 |  |
| `ValidateClassicHmiMinimalPackagePlcSync` | ✅ 通过 |  |
| `WriteClassicHmiMinimalPackageFiles` | ✅ 通过 |  |

## HMI-Library（6）

| 工具 | 状态 | 说明 |
|---|---|---|
| `AnalyzeGlobalLibraryPackage` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `AnalyzeHmiTemplateReference` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `AnalyzeUnifiedHmiTemplateLayout` | ✅ 通过 |  |
| `ImportMasterCopyFromGlobalLibrary` | 🔁 2.7.46 已修 | 会把已打开的库关掉；2.7.46 复用已打开的库不关闭（probe 今天通过；import 是画面向的助手，块主副本不适用） |
| `PlanGlobalLibraryTemplateReuse` | ✅ 通过 |  |
| `ProbeGlobalLibrary` | 🔁 2.7.46 已修 | 会把已打开的库关掉；2.7.46 复用已打开的库不关闭（probe 今天通过；import 是画面向的助手，块主副本不适用） |

## HMI-Unified（70）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ApplyUnifiedHmiLayout` | ✅ 通过 |  |
| `ApplyUnifiedHmiScreenDesignJson` | ✅ 通过 |  |
| `ApplyUnifiedHmiTheme` | ✅ 通过 |  |
| `BindUnifiedHmiButtonPressedTag` | ✅ 通过 |  |
| `BindUnifiedHmiTagDynamization` | ✅ 通过 |  |
| `BuildUnifiedHmiButtonActionScript` | ✅ 通过 |  |
| `BuildUnifiedHmiLayoutDesignJson` | ✅ 通过 |  |
| `BuildUnifiedHmiTemplateApplyDesignJson` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `BuildUnifiedHmiTemplateApplyDesignManifest` | ✅ 通过 |  |
| `BuildUnifiedHmiThemeDesignJson` | ✅ 通过 |  |
| `CompareUnifiedGraphicSelections` | ⚠ 参数/前置条件 | 需要完整兼容的选择页 |
| `DeleteEmptyUnifiedHmiScreenGroup` | ✅ 通过 |  |
| `DeleteUnifiedHmiButtonEvent` | ⚠ 参数/前置条件 | 目标事件/动态化不存在（NotFound 正确） |
| `DeleteUnifiedHmiDynamization` | ⚠ 参数/前置条件 | 目标事件/动态化不存在（NotFound 正确） |
| `DescribeUnifiedHmiButtonEventScript` | ✅ 通过 |  |
| `DescribeUnifiedScreenItemType` | ✅ 通过 |  |
| `EnsureStartStopUnifiedHmi` | ⚠ 参数/前置条件 | 需要已有画面 |
| `EnsureUnifiedHmiButtonAction` | ✅ 通过 |  |
| `EnsureUnifiedHmiButtonEventHandler` | ✅ 通过 |  |
| `EnsureUnifiedHmiConnection` | ✅ 通过 |  |
| `EnsureUnifiedHmiDynamization` | ✅ 通过 |  |
| `EnsureUnifiedHmiScreen` | ✅ 通过 |  |
| `EnsureUnifiedHmiScreenItem` | ✅ 通过 |  |
| `EnsureUnifiedHmiTag` | ✅ 通过 |  |
| `EnsureUnifiedHmiTagTable` | ✅ 通过 |  |
| `ExchangeUnifiedScriptModules` | ⚠ 参数/前置条件 | 导出目录须为新目录 |
| `ExchangeUnifiedTags` | ✅ 通过 |  |
| `ExportUnifiedEngineeringList` | ⛔ TIA/环境拒绝 | textLists 没有 Create(string)，无法自建列表；列表读取通过 |
| `GetUnifiedCrossReferences` | ✅ 通过 |  |
| `ImportUnifiedEngineeringList` | ⛔ TIA/环境拒绝 | textLists 没有 Create(string)，无法自建列表；列表读取通过 |
| `ImportUnifiedOpcUaAlarms` | ⛔ TIA/环境拒绝 | 只有 OPC UA 连接暴露 OpcUaAlarm 服务 |
| `ListUnifiedGlobalScripts` | ✅ 通过 |  |
| `ListUnifiedHmiApiTypes` | ✅ 通过 |  |
| `ListUnifiedLibraryFolder` | ⚠ 参数/前置条件 | 工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合） |
| `ManageUnifiedDynamization` | ✅ 通过 |  |
| `ManageUnifiedEngineeringObject` | ✅ 通过 |  |
| `ManageUnifiedEvent` | ✅ 通过 |  |
| `ManageUnifiedHmiGroup` | ✅ 通过 |  |
| `ManageUnifiedListEntries` | ⛔ TIA/环境拒绝 | textLists 没有 Create(string)，无法自建列表；列表读取通过 |
| `ManageUnifiedLoggingTag` | ✅ 通过 |  |
| `ManageUnifiedObjectParts` | ✅ 通过 |  |
| `ManageUnifiedOpcUaAlarmType` | ✅ 通过 |  |
| `ManageUnifiedPlantNode` | ✅ 通过 |  |
| `ManageUnifiedScreenItem` | ✅ 通过 |  |
| `ManageUnifiedScreenLayout` | ✅ 通过 |  |
| `ReadUnifiedAlarmCommon` | ✅ 通过 |  |
| `ReadUnifiedAuditSettings` | ✅ 通过 |  |
| `ReadUnifiedEngineeringObjects` | ✅ 通过 |  |
| `ReadUnifiedFaceplateInstance` | ⚠ 参数/前置条件 | 工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合） |
| `ReadUnifiedGlobalScript` | ⚠ 参数/前置条件 | 工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合） |
| `ReadUnifiedGraphicSelection` | ✅ 通过 |  |
| `ReadUnifiedHmiButtonEvent` | ✅ 通过 |  |
| `ReadUnifiedHmiDynamization` | ✅ 通过 |  |
| `ReadUnifiedHmiTexts` | ✅ 通过 |  |
| `ReadUnifiedLibraryType` | ⚠ 参数/前置条件 | 工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合） |
| `ReadUnifiedObjectEvents` | ✅ 通过 |  |
| `ReadUnifiedObjectProperties` | ✅ 通过 |  |
| `ReadUnifiedPlantObject` | ✅ 通过 |  |
| `ReadUnifiedRuntimeSettings` | ⚠ 参数/前置条件 | fieldsJson 须 1..12 个根设置；update 需要预览 token |
| `ReadUnifiedScreenBranch` | ✅ 通过 |  |
| `ReadUnifiedTagDefinitions` | ✅ 通过 |  |
| `ReleaseUnifiedReadCursor` | ✅ 通过 |  |
| `SetUnifiedHmiButtonEventScriptCode` | ✅ 通过 |  |
| `SetUnifiedLogDuration` | ⚠ 参数/前置条件 | kind 须 log/segment |
| `UpdateUnifiedGlobalScript` | ⚠ 参数/前置条件 | 工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合） |
| `UpdateUnifiedMultilingualProperty` | ✅ 通过 |  |
| `UpdateUnifiedObjectProperties` | ✅ 通过 |  |
| `UpdateUnifiedPlantObject` | ⛔ TIA/环境拒绝 | PlantView.Comment 不可写（create/read/delete 通过） |
| `UpdateUnifiedRuntimeSettings` | ⚠ 参数/前置条件 | fieldsJson 须 1..12 个根设置；update 需要预览 token |
| `ValidateUnifiedObject` | ✅ 通过 |  |

## Hardware（71）

| 工具 | 状态 | 说明 |
|---|---|---|
| `AddDevice` | ✅ 通过 | 1515F-2 PN V2.9、TP700 Comfort V17、S120 V5.2、MTP700 Unified /21.0.0.0 通过；/20.0.0.0 让 TIA 退出（crash ⑧，2.7.46 守卫） |
| `AddDeviceWithFallback` | ✅ 通过 |  |
| `AddGsdDeviceWithProbe` | ✅ 通过 |  |
| `AddHardwareCatalogDeviceWithProbe` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `AttachDeviceNodeToSubnet` | ✅ 通过 |  |
| `BuildDeviceAmlDocument` | ✅ 通过 |  |
| `ConnectDeviceNodesToProfinetSubnet` | 🔁 2.7.46 已修 | Comfort 面板的以太网口在 IE_CP_1 下两层，扫描不递归硬件组件；2.7.46 递归 |
| `DumpDeviceAttributes` | 🔁 2.7.46 已修 | nameFilter 只支持单个子串，2.7.46 支持 '/' 多备选（无过滤时今天通过，253 属性） |
| `EnsureSubnet` | ✅ 通过 |  |
| `ExchangeSystemDiagnosticsSettings` | 🔁 2.7.46 已修 | 文件必须 .dat（真机 'Filename suffix must be .dat'），2.7.46 前置检查 |
| `ExportDeviceAml` | ✅ 通过 |  |
| `GetDeviceInfo` | ✅ 通过 |  |
| `GetDeviceIpAddress` | ✅ 通过 |  |
| `GetDeviceItemInfo` | ✅ 通过 |  |
| `GetDeviceItemIoAddresses` | ✅ 通过 |  |
| `GetDeviceItemNetworkInfo` | ✅ 通过 |  |
| `GetDeviceItemTree` | ✅ 通过 |  |
| `GetDevicePlugLocations` | ✅ 通过 | CPU 项 / Device / 导轨 / S120 Device 四种宿主 |
| `GetDevices` | ✅ 通过 |  |
| `GetProjectTopology` | ✅ 通过 |  |
| `GetPutGetAccess` | ⛔ TIA/环境拒绝 | 1515F-2 PN V2.9 不把 PUT/GET 暴露为 Openness 属性（253 个属性里没有） |
| `ImportDeviceAml` | ✅ 通过 |  |
| `ManageCommunicationConnection` | 🔁 2.7.46 已修 | HW 连接组合不是服务，在 Features.CommunicationManagement.Connections 上；2.7.46 改取法 |
| `ManageDcbLibraries` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDccBlock` | ✅ 通过 |  |
| `ManageDccChart` | 🔁 2.7.46 已修 | driveObjectNumber 无默认值（同族其它工具默认 0），2.7.46 补默认；DCC 全族今天在 MCP_S120 V5.2 驱动轴上通过 |
| `ManageDccChartInterface` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDccChartPartition` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDccPin` | ✅ 通过 |  |
| `ManageDeviceServiceObjects` | ⛔ TIA/环境拒绝 | 1515F 的 CPU 项不提供 DefaultWebPagesFeature；family 名 webApplications/telecontrolDataPoints/certificateServices |
| `ManageDeviceUserGroup` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDeviceUsers` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDriveFunctions` | ⚠ 参数/前置条件 | valueJson 须为对象 / source 为 read/write；2.7.45 真机在 S120 上 read 通过（见 handoff §6） |
| `ManageDriveHardwareModule` | ✅ 通过 |  |
| `ManageDriveSafetyAcceptanceTest` | ✅ 通过 |  |
| `ManageDriveSecurity` | ✅ 通过 |  |
| `ManageDriveTelegrams` | ✅ 通过 |  |
| `ManageHardwareObject` | 🔁 2.7.46 已修 | 精确硬件路径解析不看 Items（面板子项 / 导轨模块），2.7.46 回退到 Items；deleteItem/deleteDevice/copyItem/moveItem 今天通过 |
| `ManageHardwareUtilities` | ✅ 通过 |  |
| `ManageIoSystem` | ✅ 通过 |  |
| `ManageNetworkDomain` | ✅ 通过 |  |
| `ManageOnlineDriveFunctions` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManagePortInterconnection` | 🔁 2.7.46 已修 | 精确硬件路径解析不看 Items（面板子项 / 导轨模块），2.7.46 回退到 Items；deleteItem/deleteDevice/copyItem/moveItem 今天通过 |
| `ManageStartdriveParameter` | ✅ 通过 |  |
| `ManageTechnologyExtensions` | ✅ 通过 |  |
| `ManageTransferArea` | ⚠ 参数/前置条件 | kind 须 standard/multicast；接口无传输区（ReadTransferAreas 通过） |
| `ManageWatchForceTableWebAccess` | ✅ 通过 |  |
| `PlanHardwareNetworkConfiguration` | ⚠ 参数/前置条件 | 计划校验按设计报错（subnetType 等） |
| `PlugDeviceItem` | 🔁 2.7.46 已修 | S7-1500 导轨/S120 Device 级插入后读回失败（模块落在宿主之下一层）；2.7.46 按名递归读回；插入本身今天通过（DI16、电机模块、电机、编码器） |
| `ProbeHardwareHmiConnectionOwnerCandidates` | ✅ 通过 |  |
| `ProbeHardwareHmiConnectionWhitelistedServices` | ✅ 通过 |  |
| `ReadCommunicationConnections` | 🔁 2.7.46 已修 | HW 连接组合不是服务，在 Features.CommunicationManagement.Connections 上；2.7.46 改取法 |
| `ReadDccCharts` | ✅ 通过 |  |
| `ReadDccObject` | 🔁 2.7.46 已修 | driveObjectNumber 无默认值（同族其它工具默认 0），2.7.46 补默认；DCC 全族今天在 MCP_S120 V5.2 驱动轴上通过 |
| `ReadDeviceAddressing` | ✅ 通过 |  |
| `ReadDeviceItemChannels` | ✅ 通过 |  |
| `ReadDriveObjects` | ✅ 通过 |  |
| `ReadDriveParameters` | ⚠ 参数/前置条件 | valueJson 须为对象 / source 为 read/write；2.7.45 真机在 S120 上 read 通过（见 handoff §6） |
| `ReadHardwareFeatures` | ✅ 通过 |  |
| `ReadIoSystems` | ✅ 通过 |  |
| `ReadNetworkDomains` | ✅ 通过 |  |
| `ReadOnlineDriveParameters` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ReadTransferAreas` | ✅ 通过 |  |
| `SearchHardwareCatalog` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `SearchInstalledGsdDevices` | ✅ 通过 |  |
| `SetCpuCommonSettings` | ✅ 通过 |  |
| `SetDeviceItemAttribute` | ✅ 通过 |  |
| `SetDeviceItemIoAddress` | ⚠ 参数/前置条件 | DI 模块的地址在子项 MCP_DI/MCP_DI 上（工具已提示），通道更新需 attributesJson |
| `SetPutGetAccess` | ⛔ TIA/环境拒绝 | 1515F-2 PN V2.9 不把 PUT/GET 暴露为 Openness 属性（253 个属性里没有） |
| `UpdateDeviceAddress` | ⚠ 参数/前置条件 | DI 模块的地址在子项 MCP_DI/MCP_DI 上（工具已提示），通道更新需 attributesJson |
| `UpdateDeviceItemChannel` | ⚠ 参数/前置条件 | DI 模块的地址在子项 MCP_DI/MCP_DI 上（工具已提示），通道更新需 attributesJson |

## Library（13）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CheckLibraryUpdates` | ✅ 通过 |  |
| `CompareLibraries` | ✅ 通过 |  |
| `CompareLibraryObjects` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `CreateLibraryMasterCopy` | ✅ 通过 |  |
| `ImportLibraryTypeDocuments` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `ManageGlobalLibrary` | 🔁 2.7.46 已修 | openMode 为空时 create/save/close 报 '要在此字符串中进行分析'；2.7.46 容忍；create/list/infos/save/archive/saveAs/open/close 今天通过，saveAs 后库名变成目录名（TIA 语义） |
| `ManageLibraryFolder` | ✅ 通过 |  |
| `ManageLibraryMasterCopy` | ✅ 通过 |  |
| `ManageLibraryType` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `ManageLibraryTypeVersion` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `ReadLibraryOverview` | ✅ 通过 |  |
| `ReadLibraryType` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `SynchronizeLibrary` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |

## Meta（3）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CallTool` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `FindTools` | ✅ 通过 |  |
| `ListToolCategories` | ✅ 通过 |  |

## Online-Monitoring（20）

| 工具 | 状态 | 说明 |
|---|---|---|
| `GetPlcRunStateS7` | ✅ 通过 | 只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值 |
| `MonitorWatchTableLiveS7` | ✅ 通过 | 只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值 |
| `PlanOnlineReadOnlyDataProvider` | ⚠ 参数/前置条件 | mode 须 current-values/watch-table-export-plan；数据源计划拒绝了未知标签（预期） |
| `PlanOnlineReadOnlyMonitoring` | ⚠ 参数/前置条件 | mode 须 current-values/watch-table-export-plan；数据源计划拒绝了未知标签（预期） |
| `ProbePlcMonitorOnlineCapabilities` | ✅ 通过 |  |
| `ProbeS7CpuIdentity` | ✅ 通过 | 只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值 |
| `ReadPlcLiveValuesOpcUa` | ⛔ TIA/环境拒绝 | 192.168.0.1:4840 拒绝连接（CPU 未开 OPC UA） |
| `ReadPlcLiveValuesS7` | ✅ 通过 | 只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值 |
| `ReadPlcWatchTableCurrentValuesReadOnly` | ⛔ TIA/环境拒绝 | 离线监控表不暴露当前值属性 |
| `ReadPlcWebDiagnostics` | ⚠ 参数/前置条件 | 需要 Web 服务器用户名；未对真实 CPU 发请求 |
| `ReadPlcWebVars` | ⚠ 参数/前置条件 | 需要 Web 服务器用户名；未对真实 CPU 发请求 |
| `ReadUnifiedRuntimeAlarms` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |
| `ReadUnifiedRuntimeTags` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |
| `SamplePlcLiveValuesS7` | ✅ 通过 | 只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值 |
| `SetPlcWebOperatingMode` | ⚠ 参数/前置条件 | 需要 Web 服务器用户名；未对真实 CPU 发请求 |
| `TraceTagCause` | ⛔ TIA/环境拒绝 | 块未编译一致时跳过（'compile first'），S7 读通道正常 |
| `TraceTagCauseLive` | ⛔ TIA/环境拒绝 | 块未编译一致时跳过（'compile first'），S7 读通道正常 |
| `UnifiedOpenPipeRequest` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |
| `WritePlcWebVars` | ⚠ 参数/前置条件 | 需要 Web 服务器用户名；未对真实 CPU 发请求 |
| `WriteUnifiedRuntimeTags` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |

## PLC-Alarms（8）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ExchangePlcAlarmTextListsXlsx` | ⚠ 参数/前置条件 | 需要已存在的 xlsx / 空 PLC 无文本列表 |
| `ExportAlarmClasses` | 🔁 2.7.46 已修 | 只接受 .DAT（官方格式），2.7.46 前置检查 |
| `ExportAlarmInstanceTexts` | 🔁 2.7.46 已修 | 反射找错对象/传 null 语言，2.7.46 改类型化调用；空 PLC 上 ExportToXlsx 抛 TextListNotFoundException |
| `ExportAlarmTextLists` | 🔁 2.7.46 已修 | 反射找错对象/传 null 语言，2.7.46 改类型化调用；空 PLC 上 ExportToXlsx 抛 TextListNotFoundException |
| `ImportAlarmClasses` | 🔁 2.7.46 已修 | 只接受 .DAT（官方格式），2.7.46 前置检查 |
| `ImportAlarmTextLists` | 🔁 2.7.46 已修 | 反射找错对象/传 null 语言，2.7.46 改类型化调用；空 PLC 上 ExportToXlsx 抛 TextListNotFoundException |
| `ImportPlcAlarmInstanceTexts` | ⚠ 参数/前置条件 | 需要已存在的 xlsx / 空 PLC 无文本列表 |
| `ManagePlcAlarmTextList` | ✅ 通过 |  |

## PLC-Builders（9）

| 工具 | 状态 | 说明 |
|---|---|---|
| `BuildFlgNetCallXml` | ⚠ 参数/前置条件 | 参数需要 symbol 字段（离线构建器） |
| `BuildPlcGlobalDbXml` | ✅ 通过 |  |
| `BuildPlcSymbolManifestFromXmlPath` | ✅ 通过 |  |
| `BuildPlcTagTableXml` | ✅ 通过 |  |
| `BuildPlcUdtXml` | ✅ 通过 |  |
| `BuildStructuredTextXml` | ✅ 通过 |  |
| `ComposePlcFbBlockXml` | ✅ 通过 |  |
| `ComposePlcFcBlockXml` | ✅ 通过 |  |
| `ComposePlcLadFcBlockXml` | ⚠ 参数/前置条件 | 参数需要 symbol 字段（离线构建器） |

## PLC-Online（16）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CheckDownloadReadiness` | ✅ 通过 |  |
| `CompareSoftwareToOnline` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |
| `DownloadPlcToFolder` | ⚠ 参数/前置条件 | targetForSoftware 须 CPU/PlcSimulationAdvanced |
| `DownloadToPlc` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |
| `GetOnlineState` | ✅ 通过 |  |
| `GetPlcForceTables` | ✅ 通过 |  |
| `GoOffline` | ✅ 通过 |  |
| `GoOfflineAll` | ✅ 通过 |  |
| `GoOnline` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |
| `ManagePlcDataBlockSnapshot` | ✅ 通过 |  |
| `ReadPlcBlockFingerprints` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |
| `ReadTransferRoutes` | ✅ 通过 |  |
| `ScanAccessibleDevices` | ⚠ 参数/前置条件 | PG/PC 接口名须精确（ReadTransferRoutes 列出 'Intel(R) 82574L Gigabit Network Connection'） |
| `SetWatchTableModifyValue` | ✅ 通过 |  |
| `UploadDeviceParameters` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |
| `UploadStationFromPlc` | 🚫 不运行 | 虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载 |

## PLC-OpcUA（6）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ExportOpcUaInterface` | ✅ 通过 |  |
| `GetOpcUaConfig` | ✅ 通过 |  |
| `ImportOpcUaInterface` | 🔁 2.7.46 已修 | 文件不存在也报 '已创建并导入' 并留下空接口；2.7.46 先查文件、导入失败回滚 |
| `ManageOpcUaAccessControl` | ⛔ TIA/环境拒绝 | ServerInterfaceGroup.AccessControl 在 FW 2.9 上不可用 |
| `ReadOpcUaAccessControl` | ⛔ TIA/环境拒绝 | ServerInterfaceGroup.AccessControl 在 FW 2.9 上不可用 |
| `SetOpcUaInterfaceEnabled` | ✅ 通过 |  |

## PLC-Software（75）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CompileAndDiagnosePlc` | ✅ 通过 |  |
| `CompileSoftware` | ✅ 通过 |  |
| `CreatePlcBlockGroup` | ✅ 通过 |  |
| `CreatePlcInstanceDb` | 🔁 2.7.46 已修 | autoNumber 且 number=0 生成 DB0；2.7.46 传 1（number=1 今天通过） |
| `CreatePlcTypeGroup` | ✅ 通过 |  |
| `DeleteEmptyPlcBlockGroup` | ✅ 通过 |  |
| `DeletePlcBlock` | ✅ 通过 |  |
| `DeletePlcExternalSource` | ✅ 通过 |  |
| `DeletePlcTagTable` | ✅ 通过 |  |
| `DeletePlcType` | ✅ 通过 |  |
| `DescribeBlockLogic` | ✅ 通过 |  |
| `ExchangeCfcCharts` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ExchangePlcSupervisions` | 🔁 2.7.46 已修 | export 时 importOptions 为空被拒；2.7.46 只在 import 解析（export 今天 nativeState=Success） |
| `ExportAsDocuments` | ✅ 通过 |  |
| `ExportBlock` | ✅ 通过 |  |
| `ExportBlocks` | 🔁 2.7.46 已修 | CallTool 桥接给不了 server/context（async 工具），2.7.46 桥接传 null 并等待 Task |
| `ExportBlocksAsDocuments` | ✅ 通过 |  |
| `ExportPlcProDiagInfo` | ⛔ TIA/环境拒绝 | 块不是 ProDiag FB（守卫正确） |
| `ExportPlcTagTable` | ✅ 通过 |  |
| `ExportPlcWatchTable` | ✅ 通过 |  |
| `ExportPlcWatchTablesToDirectory` | ✅ 通过 |  |
| `ExportType` | ✅ 通过 |  |
| `ExportTypes` | 🔁 2.7.46 已修 | CallTool 桥接给不了 server/context（async 工具），2.7.46 桥接传 null 并等待 Task |
| `GenerateBlocksFromExternalSource` | 🔁 2.7.46 已修 | 只搜根组的外部源（用户组里的找不到）；ManagePlcExternalSources generateBlocks 通过 |
| `GeneratePlcLoadableFile` | 🔁 2.7.46 已修 | targetOption 枚举名是 None/Plc/PlcSim，2.7.46 错误里列出 |
| `GeneratePlcSourceFromBlocks` | ✅ 通过 |  |
| `GetBlockInfo` | ✅ 通过 |  |
| `GetBlocks` | ✅ 通过 |  |
| `GetBlocksWithHierarchy` | ✅ 通过 |  |
| `GetCrossReferences` | 🔁 2.7.46 已修 | filter 为空 → 反射吞异常报 service unavailable；2.7.46 类型化并回报原因（filter=AllObjects 今天通过） |
| `GetPlcExternalSources` | ✅ 通过 |  |
| `GetPlcTagTables` | ✅ 通过 |  |
| `GetPlcWatchTables` | ✅ 通过 |  |
| `GetSoftwareInfo` | ✅ 通过 |  |
| `GetSoftwareTree` | ✅ 通过 |  |
| `GetTypeInfo` | ✅ 通过 |  |
| `GetTypes` | ✅ 通过 |  |
| `ImportBlock` | 🔁 2.7.46 已修 | ExportBlock/ExportType 报的路径不是实际文件（目录 + Name.xml），2.7.46 报 exportedFile 且 .xml 结尾按文件处理；同名块导入另一组由 TIA 拒绝 |
| `ImportBlocksFromDirectory` | ⚠ 参数/前置条件 | 同名块进另一组由 TIA 拒绝 |
| `ImportBlocksFromDocuments` | ✅ 通过 |  |
| `ImportFromDocuments` | ✅ 通过 |  |
| `ImportPlcExternalSource` | ✅ 通过 |  |
| `ImportPlcProgramFromDirectory` | ✅ 通过 |  |
| `ImportPlcTagTable` | ✅ 通过 |  |
| `ImportPlcTagTablesFromDirectory` | ✅ 通过 |  |
| `ImportPlcWatchTableOffline` | ✅ 通过 |  |
| `ImportTechnologyObject` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ImportTechnologyObjectsFromDirectory` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ImportType` | 🔁 2.7.46 已修 | ExportBlock/ExportType 报的路径不是实际文件（目录 + Name.xml），2.7.46 报 exportedFile 且 .xml 结尾按文件处理；同名块导入另一组由 TIA 拒绝 |
| `ManageCfcChartProtection` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManagePlcBlockProtection` | ✅ 通过 |  |
| `ManagePlcBlockWriteProtection` | ✅ 通过 |  |
| `ManagePlcDocuments` | ✅ 通过 |  |
| `ManagePlcExternalSources` | ✅ 通过 |  |
| `ManagePlcSoftwareUnit` | ✅ 通过 |  |
| `ManagePlcSupervision` | ✅ 通过 |  |
| `ManagePlcTableEntries` | ✅ 通过 |  |
| `ManagePlcTagDefinition` | 🔁 2.7.46 已修 | 注释是 MultilingualText，2.7.45 拒绝；2.7.46 按语言写（标量 update/create/delete 今天通过） |
| `ManagePlcUserGroup` | ✅ 通过 |  |
| `ManageSivarcBlockDefinition` | ✅ 通过 |  |
| `ManageTechnologyObject` | 🚫 不运行（会退出 TIA） | crash ⑨：TO_PositioningAxis V6.0 在 1515F-2 PN V2.9 上 Create 抛 NonRecoverableException，TIA 退出；2.7.46 描述里标明，优先 ImportTechnologyObject |
| `MoveBlockToGroup` | ✅ 通过 |  |
| `PlcBuildAndImport` | ✅ 通过 |  |
| `ReadPlcChecksums` | ✅ 通过 |  |
| `ReadPlcObjectFingerprints` | ✅ 通过 |  |
| `ReadPlcSoftwareUnits` | ✅ 通过 |  |
| `ReadPlcSystemGroups` | ✅ 通过 |  |
| `ReadPlcTagTableConstants` | ✅ 通过 |  |
| `ReadSivarcBlockDefinitions` | ✅ 通过 |  |
| `RepairAndReimportBlock` | ✅ 通过 |  |
| `SeedProjectFromReference` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `SetPlcUnitObjectAccess` | ⚠ 参数/前置条件 | 单元里没有块 |
| `UpdatePlcProgram` | ✅ 通过 |  |
| `UpgradeSivarcDefinitions` | ✅ 通过 |  |
| `WritePlcSclSourceFile` | ✅ 通过 |  |

## PLC-TechnologyObjects（8）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ConfigureMotionHardwareConnection` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ExchangeMotionCamData` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ExportTechnologyObject` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ExportTechnologyObjectsToDirectory` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `GetTechnologyObjects` | ✅ 通过 |  |
| `ManageMotionAxis` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ReadMotionAxisConfiguration` | ❌ 未跑 | 需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML |
| `ReadTechnologyObjectTree` | ✅ 通过 |  |

## Portal（7）

| 工具 | 状态 | 说明 |
|---|---|---|
| `Connect` | ✅ 手工单跑 | Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过 |
| `ConnectIsolated` | ✅ 手工单跑 | 已有连接时按设计拒绝（'Start a fresh MCP process'） |
| `Disconnect` | ✅ 手工单跑 | Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过 |
| `EnsureOpennessUserGroup` | ✅ 通过 |  |
| `GetState` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ListPortalProcessProjects` | ✅ 通过 |  |
| `ReadPortalInfo` | ✅ 通过 |  |

## Project（25）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ArchiveSavedProject` | ✅ 通过 |  |
| `AttachToOpenProject` | ✅ 手工单跑 | Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过 |
| `CloseProject` | ✅ 手工单跑 | Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过 |
| `CompareProjects` | ⚠ 参数/前置条件 | kind 为 software/softwareToLibrary/hardware，且源目标须不同 |
| `CreateProject` | ❌ 未跑 | 会在维护者的 UI 实例里新建/切换工程，未在 `项目1` 会话里跑 |
| `ExchangeTestSuiteCase` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ExportProjectTexts` | ✅ 通过 |  |
| `GetProject` | ✅ 通过 |  |
| `GetProjectTree` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ImportProjectTexts` | ✅ 通过 |  |
| `ManageMultiuserSession` | ✅ 通过 |  |
| `ManageProjectCompilationSettings` | ✅ 通过 |  |
| `ManageProjectLanguage` | ✅ 通过 |  |
| `ManageTestSuiteCase` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `OpenProject` | ✅ 手工单跑 | Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过 |
| `ReadObjectIdentifier` | ✅ 通过 |  |
| `ReadProjectSettings` | ✅ 通过 |  |
| `ReadTestSuiteCases` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `RetrieveProjectArchive` | ❌ 未跑 | 会在维护者的 UI 实例里新建/切换工程，未在 `项目1` 会话里跑 |
| `RunTestSuiteCase` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `RunToolsInTransaction` | ✅ 通过 |  |
| `SaveAsProject` | ❌ 未跑 | 会在维护者的 UI 实例里新建/切换工程，未在 `项目1` 会话里跑 |
| `SaveProject` | ✅ 通过 |  |
| `ScaffoldProject` | ❌ 未跑 | 会在维护者的 UI 实例里新建/切换工程，未在 `项目1` 会话里跑 |
| `ShowObjectInEditor` | ✅ 通过 |  |

## Reflection（7）

| 工具 | 状态 | 说明 |
|---|---|---|
| `DescribeObject` | ✅ 通过 |  |
| `DescribeObjectProperty` | ✅ 通过 |  |
| `DescribeService` | ✅ 通过 |  |
| `GetObjectProperty` | ✅ 通过 |  |
| `InvokeObject` | 🔁 2.7.46 已修 | args 传 JSON 字符串被拒；2.7.46 桥接接受字符串数组（数组形式今天通过） |
| `InvokeService` | 🔁 2.7.46 已修 | args 传 JSON 字符串被拒；2.7.46 桥接接受字符串数组（数组形式今天通过） |
| `ListObjectChildren` | ✅ 通过 |  |

## Reports（6）

| 工具 | 状态 | 说明 |
|---|---|---|
| `BuildReleaseDiagnosticReport` | ⚠ 参数/前置条件 | 需要 RunOfflineReleaseValidationSuite 的 JSON 报告（该套件需要仓库工作区） |
| `BuildReleaseManifest` | ⚠ 参数/前置条件 | 需要 RunOfflineReleaseValidationSuite 的 JSON 报告（该套件需要仓库工作区） |
| `BuildReleaseRunbook` | ⚠ 参数/前置条件 | 需要 RunOfflineReleaseValidationSuite 的 JSON 报告（该套件需要仓库工作区） |
| `GenerateAcceptanceReport` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `GenerateErrorReport` | ✅ 通过 |  |
| `RebuildReleaseHandoffArtifacts` | ⚠ 参数/前置条件 | 需要 RunOfflineReleaseValidationSuite 的 JSON 报告（该套件需要仓库工作区） |

## Safety（9）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ExportSafetyPrintout` | ✅ 通过 |  |
| `ManagePlcSafety` | ✅ 通过 |  |
| `ManageSafetyActivationTest` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageSafetyActivationTestGroup` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageSafetyFunction` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageSafetyFunctionCondition` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageSafetyGlobalSettings` | ✅ 通过 |  |
| `ReadSafetyActivationTests` | ✅ 通过 |  |
| `ReadSafetyBlockSignatures` | ✅ 通过 |  |

## Security（7）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ManagePasswordPolicy` | ✅ 通过 |  |
| `ManagePlcCertificate` | ✅ 通过 |  |
| `ManageProjectUserManagement` | ✅ 通过 |  |
| `ManageSyslogServers` | ✅ 通过 |  |
| `ManageUmcUsers` | ✅ 通过 |  |
| `ReadProjectProtection` | ✅ 通过 |  |
| `ReadProjectUserManagement` | ✅ 通过 |  |

## Simulation（5）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ManagePlcSimAdvancedInstance` | ✅ 通过 |  |
| `ReadPlcSimAdvancedInstances` | ✅ 通过 |  |
| `ReadPlcSimAdvancedTags` | ✅ 通过 |  |
| `RunPlcSimAdvancedTestScenario` | ⚠ 参数/前置条件 | 步骤须含 write/assert/cycles…；register/powerOn/powerOff/unregister、ReadPlcSimAdvancedTags 通过 |
| `WritePlcSimAdvancedTags` | ✅ 通过 |  |

## Validation（8）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ComparePlcBlockDocuments` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `ExtractPlcBlockMetrics` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `GeneratePlcDocumentation` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `LintPlcSclSource` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `RenderPlcBlockDocument` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `RunHmiTemplatePlcSyncPrecheckSuite` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `RunOfflineReleaseValidationSuite` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |
| `ScanPlcSourceAnnotations` | ⚠ 参数/前置条件 | 离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息） |

## VersionControl（8）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ConnectProjectToWorkspace` | ✅ 通过 |  |
| `CreateVersionControlWorkspace` | ✅ 通过 |  |
| `GetVersionControlStatus` | ✅ 通过 |  |
| `GetVersionControlWorkspaces` | ✅ 通过 |  |
| `ManageTeamcenterConnection` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageTeamcenterDataset` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageTeamcenterWorkflow` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `SyncVersionControlWorkspace` | ✅ 通过 |  |
