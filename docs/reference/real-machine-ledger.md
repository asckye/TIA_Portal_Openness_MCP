# 真机台账（逐工具）

[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [交接](../development/handoff.md)

2026-09-20 在维护者新建的空工程 `项目1`（TIA Portal V21，引擎 2.7.45）里用引擎自建的设备把全部工具各跑了一遍，2.7.46 / 2.7.47 部署后（2026-09-21）把 🔁 行与刻意绕开的项重跑；2.7.48 新增 `ManageOpcUaInterface`（448 个）；2.7.48 部署后（2026-09-21）跑了 OPC UA 清理、PID 工艺对象往返和对 PLCSIM Advanced 实例 `MCP_SIM` 的在线族（下载 / 上线被路由与 PLCSIM 设置器缺陷挡住，2.7.49 修）；2.7.49 部署后（2026-09-21）路由选择已通过、下载 / 上线卡在 PG 侧（虚拟网卡无 IP），监控表条目与 PLCSIM 设置器再修（2.7.50）；2.7.50 部署后（2026-09-21）清掉垃圾表、监控表行被 `ModifyIntention` 只读挡住、PLCSIM 8.0 的接口选择原来是全局 `NetworkMode`、站上载不收 MAC（2.7.51 修）；2.7.51 部署后（2026-09-21）监控表行往返通过、Softbus 网络模式让 TIA 出现 'PLCSIM' 接口（在线族可跑）：`MCP_PLC`（CPU 1515F-2 PN V2.9）、`MCP_TP700`（TP700 Comfort V17）、`MCP_UCP`（MTP700 Unified Comfort V21）、`MCP_S120`（S120 CU320-2 PN V5.2 + 驱动轴_1：电机模块 / 电机 / 编码器）；批跑器每步之后检查 TIA 进程还在不在，结果按工具记录在此。状态：

| 状态 | 含义 | 数量 |
|---|---|---:|
| ✅ 通过 | 该工具至少一次真实调用成功（读回验证） | 323 |
| ✅ 手工单跑 | 会话级工具，单独手工跑通 | 6 |
| ✅ 早期真机 | 今天没跑，但 2.7.39–2.7.45 的真机会话跑过 | 25 |
| 🔁 已修待重跑 | 真机暴露了缺陷，源码已修，部署后要重跑 | 3 |
| ⛔ TIA/环境拒绝 | 调用到 TIA/环境，被其规则拒绝或对象不提供（不是引擎缺陷） | 31 |
| ⚠ 参数/前置条件 | 只跑到参数/前置条件拒绝（工具逻辑正常，需要更完整的对象或输入） | 60 |
| 🚫 不运行 | 刻意不跑 | 0 |
| ❌ 未跑 | 未跑 | 0 |

TIA 退出点（都已写进交接 §5）：⑧ `AddDevice` 建 WinCC Unified 面板用了 `/20.0.0.0` 标识（TIA V21，2.7.46 守卫）；⑨ `ManageTechnologyObject create TO_PositioningAxis 6.0`（1515F-2 PN V2.9；5.0 正常）；⑩ 经典画面 XML 的尺寸与面板不一致（640×480 导入 TP700 800×480，2.7.48 守卫）。

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
| `RunOnlineMonitoringSafetySelfTest` | ✅ 通过 | 2.7.47 重跑通过 |
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
| `DescribeHmiScreen` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
| `DescribeHmiScreenItem` | ⚠ 参数/前置条件 | 画面导入后 ScreenItems 里没找到 Btn（经典构建器的按钮项名/结构待核） |
| `DescribeHmiSoftware` | ✅ 通过 |  |
| `DescribeHmiTag` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `DescribeHmiTagTable` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `ExportHmiConnection` | ⚠ 参数/前置条件 | 无 HMI 连接对象 / 文件不存在 |
| `ExportHmiProgram` | ✅ 通过 |  |
| `ExportHmiScreen` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
| `ExportHmiTagTable` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `GenerateSiVArc` | ✅ 通过 |  |
| `GetHmiConnections` | ✅ 通过 |  |
| `GetHmiProgramInfo` | ✅ 通过 |  |
| `GetHmiScreens` | ✅ 通过 |  |
| `GetHmiTagTables` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `GetHmiTags` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `ImportHmiConnection` | ⚠ 参数/前置条件 | 无 HMI 连接对象 / 文件不存在 |
| `ImportHmiScreen` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
| `ImportHmiScreensFromDirectory` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
| `ImportHmiTagTable` | ✅ 通过 | 2.7.47 重跑通过：用户文件夹里的表可见，未知表 NotFound |
| `ImportHmiTagTablesFromDirectory` | ✅ 通过 |  |
| `ListHmiScreenPaths` | ✅ 通过 |  |
| `ManageSiVArcRule` | ⚠ 参数/前置条件 | 需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过 |
| `ManageSivarcRuleContainer` | ✅ 通过 |  |
| `ManageSivarcScreenLayout` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
| `ManageSivarcTableRule` | ⚠ 参数/前置条件 | 需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过 |
| `ReadHmiScreenSnapshot` | ✅ 通过 | 2.7.47 重跑通过：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩ |
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
| `ImportMasterCopyFromGlobalLibrary` | ⚠ 参数/前置条件 | 画面向的助手（把主副本放到 HMI 画面上）；库里只有块 / UDT / 设备主副本，没有画面主副本可用 |
| `PlanGlobalLibraryTemplateReuse` | ✅ 通过 |  |
| `ProbeGlobalLibrary` | ✅ 通过 | 2.7.47 重跑通过：openMode 为空可 open/close；探针不再关掉已打开的库 |

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
| `AddDevice` | ✅ 通过 | 1515F-2 PN V2.9、TP700 Comfort V17、S120 V5.2、MTP700 Unified /21.0.0.0 通过；/20.0.0.0 现被守卫拒绝（crash ⑧） |
| `AddDeviceWithFallback` | ✅ 通过 |  |
| `AddGsdDeviceWithProbe` | ✅ 通过 |  |
| `AddHardwareCatalogDeviceWithProbe` | ✅ 通过 | 2.7.47 重跑通过：MTP700 Unified 探针插入成功（41 s） |
| `AttachDeviceNodeToSubnet` | ✅ 通过 |  |
| `BuildDeviceAmlDocument` | ✅ 通过 |  |
| `ConnectDeviceNodesToProfinetSubnet` | ✅ 通过 | 2.7.47 重跑通过：MCP_PLC ↔ MCP_TP700 接到 MCP_PN |
| `DumpDeviceAttributes` | ✅ 通过 | 2.7.47 重跑通过：`Ip/Name/Cycle` 匹配 25 项 |
| `EnsureSubnet` | ✅ 通过 |  |
| `ExchangeSystemDiagnosticsSettings` | ✅ 通过 | 2.7.47 重跑通过：.dat 导出 407 字节 + import 预览 |
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
| `ManageCommunicationConnection` | ⛔ TIA/环境拒绝 | create HmiConnection：原生 Create 返回对象但连接数不增（IsValid=false）——TIA 语义待查 |
| `ManageDcbLibraries` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManageDccBlock` | ✅ 通过 |  |
| `ManageDccChart` | ✅ 通过 | 2.7.47 重跑通过：不传 driveObjectNumber 也可 |
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
| `ManageHardwareObject` | ✅ 通过 | 2.7.47 重跑通过：面板端口经 Items 回退解析，PLC 端口 1 ↔ TP700 端口 1 connect 成功；deleteItem / deleteDevice 通过 |
| `ManageHardwareUtilities` | ✅ 通过 |  |
| `ManageIoSystem` | ✅ 通过 |  |
| `ManageNetworkDomain` | ✅ 通过 |  |
| `ManageOnlineDriveFunctions` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManagePortInterconnection` | ✅ 通过 | 2.7.47 重跑通过：面板端口经 Items 回退解析，PLC 端口 1 ↔ TP700 端口 1 connect 成功；deleteItem / deleteDevice 通过 |
| `ManageStartdriveParameter` | ✅ 通过 |  |
| `ManageTechnologyExtensions` | ✅ 通过 |  |
| `ManageTransferArea` | ⚠ 参数/前置条件 | kind 须 standard/multicast；接口无传输区（ReadTransferAreas 通过） |
| `ManageWatchForceTableWebAccess` | ✅ 通过 |  |
| `PlanHardwareNetworkConfiguration` | ⚠ 参数/前置条件 | 计划校验按设计报错（subnetType 等） |
| `PlugDeviceItem` | ✅ 通过 | 2.7.47 重跑通过：导轨槽 2 插 DI16 读回 IsPlugged=true |
| `ProbeHardwareHmiConnectionOwnerCandidates` | ✅ 通过 |  |
| `ProbeHardwareHmiConnectionWhitelistedServices` | ✅ 通过 |  |
| `ReadCommunicationConnections` | ✅ 通过 | 2.7.47 重跑通过：经 CommunicationManagement 读到 0 条 |
| `ReadDccCharts` | ✅ 通过 |  |
| `ReadDccObject` | ✅ 通过 | 2.7.47 重跑通过：不传 driveObjectNumber 也可 |
| `ReadDeviceAddressing` | ✅ 通过 |  |
| `ReadDeviceItemChannels` | ✅ 通过 | 2.7.47 重跑通过：子项路径 MCP_DI/MCP_DI，起始地址 0→20→30，通道读取 |
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
| `SetDeviceItemIoAddress` | ✅ 通过 | 2.7.47 重跑通过：子项路径 MCP_DI/MCP_DI，起始地址 0→20→30，通道读取 |
| `SetPutGetAccess` | ⛔ TIA/环境拒绝 | 1515F-2 PN V2.9 不把 PUT/GET 暴露为 Openness 属性（253 个属性里没有） |
| `UpdateDeviceAddress` | ✅ 通过 | 2.7.47 重跑通过：子项路径 MCP_DI/MCP_DI，起始地址 0→20→30，通道读取 |
| `UpdateDeviceItemChannel` | ⛔ TIA/环境拒绝 | DI16 通道属性 InputDelay 按 GetAttributeInfos 不可写（守卫正确） |

## Library（13）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CheckLibraryUpdates` | ✅ 通过 |  |
| `CompareLibraries` | ✅ 通过 |  |
| `CompareLibraryObjects` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `CreateLibraryMasterCopy` | ✅ 通过 |  |
| `ImportLibraryTypeDocuments` | ⚠ 参数/前置条件 | 全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过 |
| `ManageGlobalLibrary` | ✅ 通过 | 2.7.47 重跑通过：openMode 为空可 open/close；探针不再关掉已打开的库 |
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
| `GetPlcRunStateS7` | ✅ 通过 | 2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；2.7.48 真机：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错 |
| `MonitorWatchTableLiveS7` | ✅ 通过 | 只读 S7 协议对当时运行中的 PLCSIM Advanced 实例（192.168.0.1，CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势、监控表实时值 |
| `PlanOnlineReadOnlyDataProvider` | ⚠ 参数/前置条件 | mode 须 current-values/watch-table-export-plan；数据源计划拒绝了未知标签（预期） |
| `PlanOnlineReadOnlyMonitoring` | ⚠ 参数/前置条件 | mode 须 current-values/watch-table-export-plan；数据源计划拒绝了未知标签（预期） |
| `ProbePlcMonitorOnlineCapabilities` | ✅ 通过 |  |
| `ProbeS7CpuIdentity` | ✅ 通过 | 2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；2.7.48 真机：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错 |
| `ReadPlcLiveValuesOpcUa` | ⛔ TIA/环境拒绝 | 192.168.0.1:4840 拒绝连接（CPU 未开 OPC UA） |
| `ReadPlcLiveValuesS7` | ✅ 通过 | 2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；2.7.48 真机：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错 |
| `ReadPlcWatchTableCurrentValuesReadOnly` | ⛔ TIA/环境拒绝 | 离线监控表不暴露当前值属性 |
| `ReadPlcWebDiagnostics` | ✅ 通过 | 2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；2.7.48 真机：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错 |
| `ReadPlcWebVars` | ✅ 通过 | 2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；2.7.48 真机：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错 |
| `ReadUnifiedRuntimeAlarms` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |
| `ReadUnifiedRuntimeTags` | ⛔ TIA/环境拒绝 | 虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过 |
| `SamplePlcLiveValuesS7` | ✅ 通过 | 只读 S7 协议对当时运行中的 PLCSIM Advanced 实例（192.168.0.1，CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势、监控表实时值 |
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
| `ExportAlarmClasses` | ✅ 通过 | 2.7.47 重跑通过：.DAT 导出/导入 State=Success，其它扩展名前置拒绝 |
| `ExportAlarmInstanceTexts` | ⛔ TIA/环境拒绝 | 类型化调用后 TIA 回 "There is no text list" / "没有可导出的报警"（空 PLC 的规则），异常信息完整 |
| `ExportAlarmTextLists` | ⛔ TIA/环境拒绝 | 类型化调用后 TIA 回 "There is no text list" / "没有可导出的报警"（空 PLC 的规则），异常信息完整 |
| `ImportAlarmClasses` | ✅ 通过 | 2.7.47 重跑通过：.DAT 导出/导入 State=Success，其它扩展名前置拒绝 |
| `ImportAlarmTextLists` | ✅ 通过 | 2.7.47 重跑通过：文件不存在时诚实报错（导入本身需要 xlsx） |
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
| `CheckDownloadReadiness` | ✅ 通过 | 2.7.51 真机：Softbus 后路由树只剩 PC 接口 'PLCSIM'（子网 MCP_PN 192.168.0.1，两块物理网卡消失），CheckDownloadReadiness Ready=true、两条 PLCSIM 路由 |
| `CompareSoftwareToOnline` | 🔁 已修待重跑 | 2.7.48 真机：依赖在线（GoOnline 未成）；2.7.50 真机：未重跑，同上 |
| `DownloadPlcToFolder` | ⚠ 参数/前置条件 | targetForSoftware 须 CPU/PlcSimulationAdvanced |
| `DownloadToPlc` | 🔁 已修待重跑 | 2.7.49 真机：路由选择通过（'-> address 192.168.0.1 (subnet MCP_PN)'），TIA 真正发起下载，失败在 PG 侧 '连接到模块 MCP_PLC 失败'（虚拟网卡无 IP）；2.7.50 真机：PG 侧未变（网卡无 IP），未重跑；给网卡配 192.168.0.x 或 2.7.51 的 Softbus 网络模式后重跑 |
| `GetOnlineState` | ✅ 通过 | 2.7.48 真机：实例表 / Offline / 下线 / allOffline / ready；扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 02-C0-A8-00-F1-00 找到 S7-1500 (PLCSIM) |
| `GetPlcForceTables` | ✅ 通过 |  |
| `GoOffline` | ✅ 通过 | 2.7.48 真机：实例表 / Offline / 下线 / allOffline / ready；扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 02-C0-A8-00-F1-00 找到 S7-1500 (PLCSIM) |
| `GoOfflineAll` | ✅ 通过 | 2.7.48 真机：实例表 / Offline / 下线 / allOffline / ready；扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 02-C0-A8-00-F1-00 找到 S7-1500 (PLCSIM) |
| `GoOnline` | 🔁 已修待重跑 | 2.7.49 真机：路由套用后 TIA 真正尝试连接（'The connection partner is not responding'，PG 侧无 IP）；2.7.50 真机：未重跑，同上 |
| `ManagePlcDataBlockSnapshot` | ✅ 通过 | 2.7.48 真机：createSnapshot 原生返回 + exportSnapshot 2602 字节（离线；值不可独立核验） |
| `ReadPlcBlockFingerprints` | ⛔ TIA/环境拒绝 | 2.7.48 真机：FingerprintDataProvider 在 1515F-2 PN V2.9（TIA V21）上 GetService 为 null（PlcSoftware 与 CPU 项都没有） |
| `ReadTransferRoutes` | ✅ 通过 | 2.7.51 真机：Softbus 后路由树只剩 PC 接口 'PLCSIM'（子网 MCP_PN 192.168.0.1，两块物理网卡消失），CheckDownloadReadiness Ready=true、两条 PLCSIM 路由 |
| `ScanAccessibleDevices` | ✅ 通过 | 2.7.48 真机：扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 找到实例；2.7.50 真机：重注册后 MAC 02-C0-A8-00-C8-00 'S7-1500 (PLCSIM)' |
| `SetWatchTableModifyValue` | ✅ 通过 | 2.7.51 真机：%M0.0 行 appended（DisplayFormat 由 TIA 定为 Bool）、"MCP_Start" 行 updated（保留 %I0.0），readbackVerified 都 true，ManagePlcTableEntries read 核对 2 行；ModifyIntention 读回仍 false（TIA 不按 ModifyValue 推导，Openness 也写不了） |
| `UploadDeviceParameters` | ⛔ TIA/环境拒绝 | 2.7.48 真机：ParameterUploadProvider 在 1515F 的 Device / 导轨 / CPU 项上都不可用（GetService 为 null） |
| `UploadStationFromPlc` | ⛔ TIA/环境拒绝 | 2.7.50 真机：PcInterface.Addresses.Create(MAC) 被 TIA 拒 "'02-C0-A8-00-C8-00' does not specify a valid address"——ConfigurationAddressComposition.Create 只收 IP（官方页只用 IP），未下载过的 PLCSIM 实例 IP 为 0.0.0.0；2.7.51 拒绝信息明说；有 IP 后再跑 |

## PLC-OpcUA（7）

| 工具 | 状态 | 说明 |
|---|---|---|
| `ExportOpcUaInterface` | ✅ 通过 |  |
| `GetOpcUaConfig` | ✅ 通过 |  |
| `ImportOpcUaInterface` | ✅ 通过 | 2.7.47 重跑通过：文件不存在 → 诚实报错，不再建空接口 |
| `ManageOpcUaAccessControl` | ⛔ TIA/环境拒绝 | ServerInterfaceGroup.AccessControl 在 FW 2.9 上不可用 |
| `ManageOpcUaInterface` | ✅ 通过 | 2.7.48 真机：read + delete 空接口 mcp46_opcua（verifiedAbsent），随后 CompileAndDiagnosePlc Success |
| `ReadOpcUaAccessControl` | ⛔ TIA/环境拒绝 | ServerInterfaceGroup.AccessControl 在 FW 2.9 上不可用 |
| `SetOpcUaInterfaceEnabled` | ✅ 通过 |  |

## PLC-Software（75）

| 工具 | 状态 | 说明 |
|---|---|---|
| `CompileAndDiagnosePlc` | ✅ 通过 |  |
| `CompileSoftware` | ✅ 通过 |  |
| `CreatePlcBlockGroup` | ✅ 通过 |  |
| `CreatePlcInstanceDb` | ✅ 通过 | 2.7.47 重跑通过：autoNumber+number=0 不再生成 DB0 |
| `CreatePlcTypeGroup` | ✅ 通过 |  |
| `DeleteEmptyPlcBlockGroup` | ✅ 通过 |  |
| `DeletePlcBlock` | ✅ 通过 |  |
| `DeletePlcExternalSource` | ✅ 通过 |  |
| `DeletePlcTagTable` | ✅ 通过 |  |
| `DeletePlcType` | ✅ 通过 |  |
| `DescribeBlockLogic` | ✅ 通过 |  |
| `ExchangeCfcCharts` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ExchangePlcSupervisions` | ✅ 通过 | 2.7.47 重跑通过：export 不再解析 importOptions，nativeState=Success |
| `ExportAsDocuments` | ✅ 通过 |  |
| `ExportBlock` | ✅ 通过 | 2.7.47 重跑通过：.xml 结尾按文件写出并回报 exportedFile，随后导入成功 |
| `ExportBlocks` | ✅ 通过 | 2.7.47 重跑通过：经 CallTool 桥接导出 4 块 / 1 类型 |
| `ExportBlocksAsDocuments` | ✅ 通过 |  |
| `ExportPlcProDiagInfo` | ⛔ TIA/环境拒绝 | 块不是 ProDiag FB（守卫正确） |
| `ExportPlcTagTable` | ✅ 通过 |  |
| `ExportPlcWatchTable` | ✅ 通过 | 2.7.50 真机：删表后列出 MCP_W/MCP_WT；导出到桌面 mcp50_wt.xml；路由树两块网卡仍 addresses: []（PG 侧无 IP） |
| `ExportPlcWatchTablesToDirectory` | ✅ 通过 |  |
| `ExportType` | ✅ 通过 | 2.7.47 重跑通过：.xml 结尾按文件写出并回报 exportedFile，随后导入成功 |
| `ExportTypes` | ✅ 通过 | 2.7.47 重跑通过：经 CallTool 桥接导出 4 块 / 1 类型 |
| `GenerateBlocksFromExternalSource` | ⚠ 参数/前置条件 | 旧工具只搜根外部源组（用户组里的找不到，描述指向 ManagePlcExternalSources generateBlocks，后者通过） |
| `GeneratePlcLoadableFile` | ⛔ TIA/环境拒绝 | targetOption 枚举名已列出（None/Plc/PlcSim）；`LoadableProvider` 在 1515F-2 PN V2.9 上不可用（GetService 为 null） |
| `GeneratePlcSourceFromBlocks` | ✅ 通过 |  |
| `GetBlockInfo` | ✅ 通过 |  |
| `GetBlocks` | ✅ 通过 |  |
| `GetBlocksWithHierarchy` | ✅ 通过 |  |
| `GetCrossReferences` | ✅ 通过 | 2.7.47 重跑通过：filter 为空按 AllObjects；非法 filter 列出合法值 |
| `GetPlcExternalSources` | ✅ 通过 |  |
| `GetPlcTagTables` | ✅ 通过 |  |
| `GetPlcWatchTables` | ✅ 通过 | 2.7.50 真机：删表后列出 MCP_W/MCP_WT；导出到桌面 mcp50_wt.xml；路由树两块网卡仍 addresses: []（PG 侧无 IP） |
| `GetSoftwareInfo` | ✅ 通过 |  |
| `GetSoftwareTree` | ✅ 通过 |  |
| `GetTypeInfo` | ✅ 通过 |  |
| `GetTypes` | ✅ 通过 |  |
| `ImportBlock` | ✅ 通过 | 2.7.47 重跑通过：.xml 结尾按文件写出并回报 exportedFile，随后导入成功 |
| `ImportBlocksFromDirectory` | ⚠ 参数/前置条件 | 同名块进另一组由 TIA 拒绝 |
| `ImportBlocksFromDocuments` | ✅ 通过 |  |
| `ImportFromDocuments` | ✅ 通过 |  |
| `ImportPlcExternalSource` | ✅ 通过 |  |
| `ImportPlcProgramFromDirectory` | ✅ 通过 |  |
| `ImportPlcTagTable` | ✅ 通过 |  |
| `ImportPlcTagTablesFromDirectory` | ✅ 通过 |  |
| `ImportPlcWatchTableOffline` | ✅ 通过 |  |
| `ImportTechnologyObject` | ✅ 通过 | 2.7.48 真机：PID 导入进 MCP_TO 文件夹 / 批量导入 1，删除后编译 Success |
| `ImportTechnologyObjectsFromDirectory` | ✅ 通过 | 2.7.48 真机：PID 导入进 MCP_TO 文件夹 / 批量导入 1，删除后编译 Success |
| `ImportType` | ✅ 通过 | 2.7.47 重跑通过：.xml 结尾按文件写出并回报 exportedFile，随后导入成功 |
| `ManageCfcChartProtection` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `ManagePlcBlockProtection` | ✅ 通过 |  |
| `ManagePlcBlockWriteProtection` | ✅ 通过 |  |
| `ManagePlcDocuments` | ✅ 通过 |  |
| `ManagePlcExternalSources` | ✅ 通过 |  |
| `ManagePlcSoftwareUnit` | ✅ 通过 |  |
| `ManagePlcSupervision` | ✅ 通过 |  |
| `ManagePlcTableEntries` | ✅ 通过 | 2.7.50 真机：deleteTable 把根级 MCP_WT_1…MCP_WT_5 逐张删掉并读回缺席（dryRun 先报 0 行），GetPlcWatchTables 只剩 MCP_W/MCP_WT，工程已保存；read / createComment / deleteEntry 早期真机通过 |
| `ManagePlcTagDefinition` | ✅ 通过 | 2.7.47 重跑通过：注释按语言写入/读回（字符串 → 编辑语言，对象 → 指定语言，未激活语言 NotFound） |
| `ManagePlcUserGroup` | ✅ 通过 |  |
| `ManageSivarcBlockDefinition` | ✅ 通过 |  |
| `ManageTechnologyObject` | ✅ 通过 | PID_Compact 2.3 / TO_SpeedAxis 5.0 / TO_PositioningAxis 5.0 可建、读、删；crash ⑨ 只在 TO_PositioningAxis 6.0；setParameter 对 PID 2.3 被 TIA 拒（set_Value not supported） |
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
| `ConfigureMotionHardwareConnection` | ✅ 通过 | 在自建 TO_SpeedAxis 5.0 上通过（硬件连接 read：AxisEncoderHardwareConnectionInterface） |
| `ExchangeMotionCamData` | ⛔ TIA/环境拒绝 | CamDataSupport 只有 TO_Cam 提供（轴上按规则拒绝）；无 S7-1500T |
| `ExportTechnologyObject` | ✅ 通过 | 2.7.48 真机：PID_Compact 2.3 编译后单个 / 批量导出通过；2.7.49 真机：刚导入未编译的 TO 被 TIA 拒 'Inconsistent blocks … cannot be exported'（规则：导入后先编译） |
| `ExportTechnologyObjectsToDirectory` | ✅ 通过 | 2.7.48 真机：PID_Compact 2.3 编译后单个 / 批量导出通过；2.7.49 真机：刚导入未编译的 TO 被 TIA 拒 'Inconsistent blocks … cannot be exported'（规则：导入后先编译） |
| `GetTechnologyObjects` | ✅ 通过 | 2.7.49 真机：MCP_TO 文件夹里的 PID 列出 1 个，删除后 0 个（递归生效） |
| `ManageMotionAxis` | ✅ 通过 | 在自建 TO_SpeedAxis 5.0 上通过（硬件连接 read：AxisEncoderHardwareConnectionInterface） |
| `ReadMotionAxisConfiguration` | ✅ 通过 | 在自建 TO_SpeedAxis 5.0 上通过（硬件连接 read：AxisEncoderHardwareConnectionInterface） |
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
| `CreateProject` | ✅ 通过 | 2.7.47 真跑：SaveAs 到副本、.zap21 还原绑定、Scaffold 新建工程 7 步全过、CreateProject 通过（外来工程时拒绝） |
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
| `RetrieveProjectArchive` | ✅ 通过 | 2.7.47 真跑：SaveAs 到副本、.zap21 还原绑定、Scaffold 新建工程 7 步全过、CreateProject 通过（外来工程时拒绝） |
| `RunTestSuiteCase` | ✅ 早期真机 | 2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取） |
| `RunToolsInTransaction` | ✅ 通过 |  |
| `SaveAsProject` | ✅ 通过 | 2.7.47 真跑：SaveAs 到副本、.zap21 还原绑定、Scaffold 新建工程 7 步全过、CreateProject 通过（外来工程时拒绝） |
| `SaveProject` | ✅ 通过 |  |
| `ScaffoldProject` | ✅ 通过 | 2.7.47 真跑：SaveAs 到副本、.zap21 还原绑定、Scaffold 新建工程 7 步全过、CreateProject 通过（外来工程时拒绝） |
| `ShowObjectInEditor` | ✅ 通过 |  |

## Reflection（7）

| 工具 | 状态 | 说明 |
|---|---|---|
| `DescribeObject` | ✅ 通过 |  |
| `DescribeObjectProperty` | ✅ 通过 |  |
| `DescribeService` | ✅ 通过 |  |
| `GetObjectProperty` | ✅ 通过 |  |
| `InvokeObject` | ✅ 通过 | 2.7.47 重跑通过：args 给 JSON 字符串也可 |
| `InvokeService` | ✅ 通过 | 2.7.47 重跑通过：args 给 JSON 字符串也可 |
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
| `ManagePlcSimAdvancedInstance` | ✅ 通过 | 2.7.51 真机：powerOff → unregister → register CPU1500_Unspecified communicationInterface=Softbus：route=SimulationRuntimeManager.NetworkMode，TCPIPSingleAdapter → Softbus，实例读回 Softbus；powerOn 后 Stop、controllerIP 192.168.0.1（Softbus 下自带默认 IP，TCPIP 下曾是 0.0.0.0） |
| `ReadPlcSimAdvancedInstances` | ✅ 通过 | 2.7.50 真机：memberFilter 列出实例成员；2.7.51 真机：api.networkMode=TCPIPSingleAdapter、managerMembers='SimulationRuntimeManager.NetworkMode {get;set}' |
| `ReadPlcSimAdvancedTags` | ✅ 通过 | 2.7.48 真机：实例未下载程序时 0 标签（列表 / 按名读都如实报 0） |
| `RunPlcSimAdvancedTestScenario` | ✅ 通过 | 2.7.49 真机：不存在的标签 / 失败场景现在 operationSuccess=false（实例无程序，读写内容待下载后） |
| `WritePlcSimAdvancedTags` | ✅ 通过 | 2.7.49 真机：不存在的标签 / 失败场景现在 operationSuccess=false（实例无程序，读写内容待下载后） |

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
