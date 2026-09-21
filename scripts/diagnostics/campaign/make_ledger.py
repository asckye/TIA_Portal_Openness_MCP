# -*- coding: utf-8 -*-
"""Generate docs/reference/real-machine-ledger.md from today's campaign ledgers plus curated overrides."""
import json, io, os, glob, collections
S = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.abspath(os.path.join(S, "..", "..", ".."))
t = json.load(io.open(os.path.join(ROOT, "manifest/tools-list.json"), encoding="utf-8-sig"))
tools = {x["name"]: x for x in (t["tools"] if isinstance(t, dict) else t)}
runs = collections.defaultdict(list)
# evidence: the packed campaign runs (2026-09-20/21, gzip of every ledger/*.jsonl of those sessions) plus any new ledger/*.jsonl of this machine
import gzip
packed = os.path.join(S, "ledger-runs.jsonl.gz")
if os.path.exists(packed):
    for l in gzip.open(packed, "rt", encoding="utf-8"):
        if l.strip(): r = json.loads(l); runs[r["tool"]].append(r)
for f in sorted(glob.glob(os.path.join(S, "ledger", "*.jsonl"))):
    for l in io.open(f, encoding="utf-8"):
        if l.strip(): r = json.loads(l); runs[r["tool"]].append(r)
prev = json.load(io.open(os.path.join(S, "vm_ledger.json"), encoding="utf-8"))["invoked"]

PASS, EARLY, FIXED, TIA, PARAM, REALCPU, MANUAL, SKIP = "✅ 通过", "✅ 早期真机", "🔁 已修待重跑", "⛔ TIA/环境拒绝", "⚠ 参数/前置条件", "🚫 不运行", "✅ 手工单跑", "❌ 未跑"
O = {}
def o(names, status, note=""):
    for n in names.split(): O[n] = (status, note)

o("ManagePlcTagDefinition", FIXED, "注释是 MultilingualText，2.7.45 拒绝；2.7.46 按语言写（标量 update/create/delete 今天通过）")
o("ExportBlocks ExportTypes", FIXED, "CallTool 桥接给不了 server/context（async 工具），2.7.46 桥接传 null 并等待 Task")
o("ImportBlock ImportType", FIXED, "ExportBlock/ExportType 报的路径不是实际文件（目录 + Name.xml），2.7.46 报 exportedFile 且 .xml 结尾按文件处理；同名块导入另一组由 TIA 拒绝")
o("GetCrossReferences", FIXED, "filter 为空 → 反射吞异常报 service unavailable；2.7.46 类型化并回报原因（filter=AllObjects 今天通过）")
o("ExportAlarmClasses ImportAlarmClasses", FIXED, "只接受 .DAT（官方格式），2.7.46 前置检查")
o("ExportAlarmTextLists ImportAlarmTextLists ExportAlarmInstanceTexts", FIXED, "反射找错对象/传 null 语言，2.7.46 改类型化调用；空 PLC 上 ExportToXlsx 抛 TextListNotFoundException")
o("ExchangePlcSupervisions", FIXED, "export 时 importOptions 为空被拒；2.7.46 只在 import 解析（export 今天 nativeState=Success）")
o("GeneratePlcLoadableFile", FIXED, "targetOption 枚举名是 None/Plc/PlcSim，2.7.46 错误里列出")
o("ReadCommunicationConnections ManageCommunicationConnection", FIXED, "HW 连接组合不是服务，在 Features.CommunicationManagement.Connections 上；2.7.46 改取法")
o("ExchangeSystemDiagnosticsSettings", FIXED, "文件必须 .dat（真机 'Filename suffix must be .dat'），2.7.46 前置检查")
o("ConnectDeviceNodesToProfinetSubnet", FIXED, "Comfort 面板的以太网口在 IE_CP_1 下两层，扫描不递归硬件组件；2.7.46 递归")
o("PlugDeviceItem", FIXED, "S7-1500 导轨/S120 Device 级插入后读回失败（模块落在宿主之下一层）；2.7.46 按名递归读回；插入本身今天通过（DI16、电机模块、电机、编码器）")
o("ManagePortInterconnection ManageHardwareObject", FIXED, "精确硬件路径解析不看 Items（面板子项 / 导轨模块），2.7.46 回退到 Items；deleteItem/deleteDevice/copyItem/moveItem 今天通过")
o("DumpDeviceAttributes", FIXED, "nameFilter 只支持单个子串，2.7.46 支持 '|' 多备选（无过滤时今天通过，253 属性）")
o("ImportHmiTagTable GetHmiTagTables GetHmiTags DescribeHmiTagTable DescribeHmiTag ExportHmiTagTable", FIXED, "导入进用户文件夹后根级查找说没有；2.7.46 递归用户文件夹，未知表回 NotFound")
o("ImportHmiScreen ImportHmiScreensFromDirectory", FIXED, "TP700 Comfort V17 拒绝 Button 的 <Visible>（set_Visible not supported）；2.7.46 剥掉该属性重试")
o("DescribeHmiScreen DescribeHmiScreenItem ExportHmiScreen ReadHmiScreenSnapshot ManageSivarcScreenLayout", FIXED, "依赖画面导入（见 ImportHmiScreen）")
o("ManageGlobalLibrary", FIXED, "openMode 为空时 create/save/close 报 '要在此字符串中进行分析'；2.7.46 容忍；create/list/infos/save/archive/saveAs/open/close 今天通过，saveAs 后库名变成目录名（TIA 语义）")
o("ProbeGlobalLibrary", FIXED, "会把已打开的库关掉；2.7.46 复用已打开的库不关闭")
o("ImportMasterCopyFromGlobalLibrary", PARAM, "画面向的助手（把主副本放到 HMI 画面上）；库里只有块 / UDT / 设备主副本，没有画面主副本可用")
o("ImportOpcUaInterface", FIXED, "文件不存在也报 '已创建并导入' 并留下空接口；2.7.46 先查文件、导入失败回滚")
o("RunOnlineMonitoringSafetySelfTest", FIXED, "把 ManageWatchForceTableWebAccess 当强制写工具（名字启发式误报）；2.7.46 白名单")
o("InvokeObject InvokeService", FIXED, "args 传 JSON 字符串被拒；2.7.46 桥接接受字符串数组（数组形式今天通过）")
o("ManageDccChart ReadDccObject", FIXED, "driveObjectNumber 无默认值（同族其它工具默认 0），2.7.46 补默认；DCC 全族今天在 MCP_S120 V5.2 驱动轴上通过")
o("CreatePlcInstanceDb", FIXED, "autoNumber 且 number=0 生成 DB0；2.7.46 传 1（number=1 今天通过）")
o("ManageTechnologyObject", REALCPU + "（会退出 TIA）", "crash ⑨：TO_PositioningAxis V6.0 在 1515F-2 PN V2.9 上 Create 抛 NonRecoverableException，TIA 退出；2.7.46 描述里标明，优先 ImportTechnologyObject")
o("ImportTechnologyObject ImportTechnologyObjectsFromDirectory ExportTechnologyObject ExportTechnologyObjectsToDirectory ManageMotionAxis ReadMotionAxisConfiguration ConfigureMotionHardwareConnection ExchangeMotionCamData", SKIP, "需要一个工艺对象；Openness 建 TO 会让 TIA 退出（crash ⑨），未从参考工程拿到 TO XML")
o("GoOnline DownloadToPlc CompareSoftwareToOnline UploadStationFromPlc UploadDeviceParameters ReadPlcBlockFingerprints", REALCPU, "虚拟机网段 192.168.0.1 上有维护者的真实 CPU 1510SP F（RUN）；只做过只读 S7 探测，不上线/不下载")
o("ProbeS7CpuIdentity GetPlcRunStateS7 ReadPlcLiveValuesS7 SamplePlcLiveValuesS7 MonitorWatchTableLiveS7", PASS, "只读 S7 协议对 192.168.0.1（CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势采样、监控表实时值")
o("TraceTagCauseLive TraceTagCause", TIA, "块未编译一致时跳过（'compile first'），S7 读通道正常")
o("ReadPlcLiveValuesOpcUa", TIA, "192.168.0.1:4840 拒绝连接（CPU 未开 OPC UA）")
o("ReadUnifiedRuntimeTags ReadUnifiedRuntimeAlarms WriteUnifiedRuntimeTags UnifiedOpenPipeRequest", TIA, "虚拟机上没有运行中的 WinCC Unified Runtime（Open Pipe 超时）；UnifiedOpenPipeRequest 预览通过")
o("ReadPlcWebDiagnostics ReadPlcWebVars WritePlcWebVars SetPlcWebOperatingMode", PARAM, "需要 Web 服务器用户名；未对真实 CPU 发请求")
o("GetPutGetAccess SetPutGetAccess", TIA, "1515F-2 PN V2.9 不把 PUT/GET 暴露为 Openness 属性（253 个属性里没有）")
o("ReadOpcUaAccessControl ManageOpcUaAccessControl", TIA, "ServerInterfaceGroup.AccessControl 在 FW 2.9 上不可用")
o("ReadClassicHmiGlobalization ManageClassicHmiGraphic", TIA, "TP700 Comfort V17 的 HmiTarget 不提供 GraphicsProvider")
o("ManageDeviceServiceObjects", TIA, "1515F 的 CPU 项不提供 DefaultWebPagesFeature；family 名 webApplications/telecontrolDataPoints/certificateServices")
o("ManageDriveFunctions ReadDriveParameters", PARAM, "valueJson 须为对象 / source 为 read|write；2.7.45 真机在 S120 上 read 通过（见 handoff §6）")
o("CompileAndDiagnoseHmi", TIA, "新面板没有起始画面（'A start screen has not been configured'）——编译诊断本身正常")
o("ExportHmiConnection ImportHmiConnection", PARAM, "无 HMI 连接对象 / 文件不存在")
o("CompareProjects", PARAM, "kind 为 software/softwareToLibrary/hardware，且源目标须不同")
o("CompareLibraryObjects ReadLibraryType ManageLibraryType ManageLibraryTypeVersion SynchronizeLibrary ImportLibraryTypeDocuments", PARAM, "全局库里没有库类型（文档导入需要 .s7dcl 文件，今天路径不存在）；库文件夹/主副本/比较/更新检查通过")
o("ManageSiVArcRule ManageSivarcTableRule ResolveSivarcExpression", PARAM, "需要 rule 名/createOption/libraryItemKind；规则文件夹/表 create/delete、ReadSiVArcRules、GenerateSiVArc 预览通过")
o("ManageTransferArea", PARAM, "kind 须 standard/multicast；接口无传输区（ReadTransferAreas 通过）")
o("SetDeviceItemIoAddress UpdateDeviceAddress UpdateDeviceItemChannel", PARAM, "DI 模块的地址在子项 MCP_DI/MCP_DI 上（工具已提示），通道更新需 attributesJson")
o("ExchangeUnifiedScriptModules", PARAM, "导出目录须为新目录")
o("ExportUnifiedEngineeringList ImportUnifiedEngineeringList ManageUnifiedListEntries", TIA, "textLists 没有 Create(string)，无法自建列表；列表读取通过")
o("ReadUnifiedRuntimeSettings UpdateUnifiedRuntimeSettings", PARAM, "fieldsJson 须 1..12 个根设置；update 需要预览 token")
o("SetUnifiedLogDuration", PARAM, "kind 须 log/segment")
o("ReadUnifiedGlobalScript UpdateUnifiedGlobalScript ReadUnifiedLibraryType ListUnifiedLibraryFolder ReadUnifiedFaceplateInstance", PARAM, "工程里没有全局脚本/库类型/面板实例（读取路径本身通过，返回空集合）")
o("DeleteUnifiedHmiButtonEvent DeleteUnifiedHmiDynamization", PARAM, "目标事件/动态化不存在（NotFound 正确）")
o("EnsureStartStopUnifiedHmi", PARAM, "需要已有画面")
o("UpdateUnifiedPlantObject", TIA, "PlantView.Comment 不可写（create/read/delete 通过）")
o("ImportUnifiedOpcUaAlarms", TIA, "只有 OPC UA 连接暴露 OpcUaAlarm 服务")
o("ExportPlcProDiagInfo", TIA, "块不是 ProDiag FB（守卫正确）")
o("ImportPlcAlarmInstanceTexts ExchangePlcAlarmTextListsXlsx", PARAM, "需要已存在的 xlsx / 空 PLC 无文本列表")
o("GenerateBlocksFromExternalSource", PARAM, "旧工具只搜根外部源组（用户组里的找不到，描述指向 ManagePlcExternalSources generateBlocks，后者通过）")
o("ImportBlocksFromDirectory", PARAM, "同名块进另一组由 TIA 拒绝")
o("SetPlcUnitObjectAccess", PARAM, "单元里没有块")
o("ScanAccessibleDevices", PARAM, "PG/PC 接口名须精确（ReadTransferRoutes 列出 'Intel(R) 82574L Gigabit Network Connection'）")
o("DownloadPlcToFolder", PARAM, "targetForSoftware 须 CPU/PlcSimulationAdvanced")
o("RunPlcSimAdvancedTestScenario", PARAM, "步骤须含 write/assert/cycles…；register/powerOn/powerOff/unregister、ReadPlcSimAdvancedTags 通过")
o("PlanOnlineReadOnlyMonitoring PlanOnlineReadOnlyDataProvider", PARAM, "mode 须 current-values/watch-table-export-plan；数据源计划拒绝了未知标签（预期）")
o("ReadPlcWatchTableCurrentValuesReadOnly", TIA, "离线监控表不暴露当前值属性")
o("PlanHardwareNetworkConfiguration", PARAM, "计划校验按设计报错（subnetType 等）")
o("AnalyzeGlobalLibraryPackage AnalyzeHmiTemplateReference BuildUnifiedHmiTemplateApplyDesignJson RunHmiTemplatePlcSyncPrecheckSuite RunOfflineReleaseValidationSuite ComparePlcBlockDocuments ExtractPlcBlockMetrics GeneratePlcDocumentation RenderPlcBlockDocument ScanPlcSourceAnnotations LintPlcSclSource GenerateAcceptanceReport SeedProjectFromReference", PARAM, "离线助手：输入目录/文件/JSON 形状不满足（有明确的拒绝信息）")
o("BuildFlgNetCallXml ComposePlcLadFcBlockXml", PARAM, "参数需要 symbol 字段（离线构建器）")
o("CompareUnifiedGraphicSelections", PARAM, "需要完整兼容的选择页")
o("BuildReleaseManifest BuildReleaseDiagnosticReport BuildReleaseRunbook RebuildReleaseHandoffArtifacts", PARAM, "需要 RunOfflineReleaseValidationSuite 的 JSON 报告（该套件需要仓库工作区）")
o("Connect Disconnect CloseProject OpenProject AttachToOpenProject", MANUAL, "Close → OpenProject(path) → Disconnect → Connect → Attach 往返通过")
o("ConnectIsolated", MANUAL, "已有连接时按设计拒绝（'Start a fresh MCP process'）")
o("CreateProject SaveAsProject RetrieveProjectArchive ScaffoldProject", SKIP, "会在维护者的 UI 实例里新建/切换工程，未在 `项目1` 会话里跑")
o("AddDevice", PASS, "1515F-2 PN V2.9、TP700 Comfort V17、S120 V5.2、MTP700 Unified /21.0.0.0 通过；/20.0.0.0 让 TIA 退出（crash ⑧，2.7.46 守卫）")
o("GetDevicePlugLocations", PASS, "CPU 项 / Device / 导轨 / S120 Device 四种宿主")


# ---- 2.7.47 rerun (2026-09-21): verified rows ----
RERUN = "2.7.47 重跑通过"
o("ManagePlcTagDefinition", PASS, RERUN + "：注释按语言写入/读回（字符串 → 编辑语言，对象 → 指定语言，未激活语言 NotFound）")
o("ExportBlocks ExportTypes", PASS, RERUN + "：经 CallTool 桥接导出 4 块 / 1 类型")
o("ExportBlock ExportType ImportBlock ImportType", PASS, RERUN + "：.xml 结尾按文件写出并回报 exportedFile，随后导入成功")
o("GetCrossReferences", PASS, RERUN + "：filter 为空按 AllObjects；非法 filter 列出合法值")
o("CreatePlcInstanceDb", PASS, RERUN + "：autoNumber+number=0 不再生成 DB0")
o("GeneratePlcLoadableFile", TIA, "targetOption 枚举名已列出（None/Plc/PlcSim）；`LoadableProvider` 在 1515F-2 PN V2.9 上不可用（GetService 为 null）")
o("ExportAlarmClasses ImportAlarmClasses", PASS, RERUN + "：.DAT 导出/导入 State=Success，其它扩展名前置拒绝")
o("ExportAlarmTextLists ExportAlarmInstanceTexts", TIA, "类型化调用后 TIA 回 \"There is no text list\" / \"没有可导出的报警\"（空 PLC 的规则），异常信息完整")
o("ImportAlarmTextLists", PASS, RERUN + "：文件不存在时诚实报错（导入本身需要 xlsx）")
o("ExchangePlcSupervisions", PASS, RERUN + "：export 不再解析 importOptions，nativeState=Success")
o("ReadCommunicationConnections", PASS, RERUN + "：经 CommunicationManagement 读到 0 条")
o("ManageCommunicationConnection", TIA, "create HmiConnection：原生 Create 返回对象但连接数不增（IsValid=false）——TIA 语义待查")
o("ExchangeSystemDiagnosticsSettings", PASS, RERUN + "：.dat 导出 407 字节 + import 预览")
o("ConnectDeviceNodesToProfinetSubnet", PASS, RERUN + "：MCP_PLC ↔ MCP_TP700 接到 MCP_PN")
o("PlugDeviceItem", PASS, RERUN + "：导轨槽 2 插 DI16 读回 IsPlugged=true")
o("SetDeviceItemIoAddress UpdateDeviceAddress ReadDeviceItemChannels", PASS, RERUN + "：子项路径 MCP_DI/MCP_DI，起始地址 0→20→30，通道读取")
o("UpdateDeviceItemChannel", TIA, "DI16 通道属性 InputDelay 按 GetAttributeInfos 不可写（守卫正确）")
o("ManagePortInterconnection ManageHardwareObject", PASS, RERUN + "：面板端口经 Items 回退解析，PLC 端口 1 ↔ TP700 端口 1 connect 成功；deleteItem / deleteDevice 通过")
o("DumpDeviceAttributes", PASS, RERUN + "：`Ip|Name|Cycle` 匹配 25 项")
o("ImportHmiTagTable GetHmiTagTables GetHmiTags DescribeHmiTagTable DescribeHmiTag ExportHmiTagTable", PASS, RERUN + "：用户文件夹里的表可见，未知表 NotFound")
o("ImportHmiScreen ImportHmiScreensFromDirectory DescribeHmiScreen ExportHmiScreen ReadHmiScreenSnapshot ManageSivarcScreenLayout", PASS, RERUN + "：800×480 画面导入 TP700（剥掉 Button.Visible）；640×480 = crash ⑩")
o("DescribeHmiScreenItem", PARAM, "画面导入后 ScreenItems 里没找到 Btn（经典构建器的按钮项名/结构待核）")
o("ManageGlobalLibrary ProbeGlobalLibrary", PASS, RERUN + "：openMode 为空可 open/close；探针不再关掉已打开的库")
o("ImportOpcUaInterface", PASS, RERUN + "：文件不存在 → 诚实报错，不再建空接口")
o("RunOnlineMonitoringSafetySelfTest", PASS, RERUN)
o("InvokeObject InvokeService", PASS, RERUN + "：args 给 JSON 字符串也可")
o("ManageDccChart ReadDccObject", PASS, RERUN + "：不传 driveObjectNumber 也可")
o("AddDevice", PASS, "1515F-2 PN V2.9、TP700 Comfort V17、S120 V5.2、MTP700 Unified /21.0.0.0 通过；/20.0.0.0 现被守卫拒绝（crash ⑧）")
o("AddHardwareCatalogDeviceWithProbe", PASS, RERUN + "：MTP700 Unified 探针插入成功（41 s）")
o("SaveAsProject RetrieveProjectArchive ScaffoldProject CreateProject", PASS, "2.7.47 真跑：SaveAs 到副本、.zap21 还原绑定、Scaffold 新建工程 7 步全过、CreateProject 通过（外来工程时拒绝）")
o("ManageTechnologyObject", PASS, "PID_Compact 2.3 / TO_SpeedAxis 5.0 / TO_PositioningAxis 5.0 可建、读、删；crash ⑨ 只在 TO_PositioningAxis 6.0；setParameter 对 PID 2.3 被 TIA 拒（set_Value not supported）")
o("ReadMotionAxisConfiguration ManageMotionAxis ConfigureMotionHardwareConnection", PASS, "在自建 TO_SpeedAxis 5.0 上通过（硬件连接 read：AxisEncoderHardwareConnectionInterface）")
o("ExchangeMotionCamData", TIA, "CamDataSupport 只有 TO_Cam 提供（轴上按规则拒绝）；无 S7-1500T")
o("ExportTechnologyObject ExportTechnologyObjectsToDirectory", TIA, "TO 未编译一致（PLC 因空 OPC UA 接口编译失败）→ TIA 拒绝导出；2.7.48 删接口后重跑")
o("ImportTechnologyObject ImportTechnologyObjectsFromDirectory", FIXED, "反射找错属性名（TechnologyObjectGroup），2.7.48 类型化；待编译通过后导出再导入")
o("GoOnline DownloadToPlc CompareSoftwareToOnline UploadStationFromPlc UploadDeviceParameters ReadPlcBlockFingerprints", SKIP, "目标改为 PLCSIM Advanced 虚拟 PLC（2.7.48 加了 communicationInterface=TCPIP）；需要 PLC 先编译通过（空 OPC UA 接口待 2.7.48 的 ManageOpcUaInterface 删除）")
o("ProbeS7CpuIdentity GetPlcRunStateS7 ReadPlcLiveValuesS7 SamplePlcLiveValuesS7 MonitorWatchTableLiveS7", PASS, "只读 S7 协议对当时运行中的 PLCSIM Advanced 实例（192.168.0.1，CPU 1510SP F，RUN）：身份、运行态、M0.0、趋势、监控表实时值")
o("ManageOpcUaInterface", SKIP, "2.7.48 新增（read / delete），待部署")

# ---- 2.7.48 deployment (2026-09-21): OPC UA cleanup, TO round trip, online family against PLCSIM Advanced MCP_SIM ----
D48 = "2.7.48 真机"
o("ManageOpcUaInterface", PASS, D48 + "：read + delete 空接口 mcp46_opcua（verifiedAbsent），随后 CompileAndDiagnosePlc Success")
o("ExportTechnologyObject ExportTechnologyObjectsToDirectory", PASS, D48 + "：PID_Compact 2.3 单个导出 / 批量导出 1；TO_SpeedAxis 5.0 没有驱动报文不能编译一致 → TIA 拒绝导出（规则）；2.7.49 起也导出用户文件夹里的 TO")
o("ImportTechnologyObject ImportTechnologyObjectsFromDirectory", PASS, D48 + "：PID 导入进 MCP_TO 文件夹 / 批量导入 1，删除后编译 Success")
o("GetTechnologyObjects", FIXED, D48 + "：只列根级——导入进 MCP_TO 的 TO 报 0 个（ReadTechnologyObjectTree 能看到）；2.7.49 递归用户文件夹并回报 Folder")
o("ReadPlcSimAdvancedInstances GetOnlineState GoOffline GoOfflineAll CheckDownloadReadiness ScanAccessibleDevices", PASS, D48 + "：实例表 / Offline / 下线 / allOffline / ready；扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 02-C0-A8-00-F1-00 找到 S7-1500 (PLCSIM)")
o("ManagePlcSimAdvancedInstance", FIXED, D48 + "：register/powerOn 带 communicationInterface 时 PropertyInfo.SetValue 抛 '未找到属性设置方法'（实例类的公开属性只读，setter 在 IInstance 接口上）——实例已被 RegisterInstance 建出但工具报失败；2.7.49 经接口 setter；powerOff 后 'state now .' 改文案；stop/powerOff/unregister 通过")
o("ReadPlcSimAdvancedTags", PASS, D48 + "：实例未下载程序时 0 标签（列表 / 按名读都如实报 0）")
o("WritePlcSimAdvancedTags", FIXED, D48 + "：不存在的标签 'Wrote 0/1' 却 operationSuccess=true；2.7.49 改为 false")
o("RunPlcSimAdvancedTestScenario", FIXED, D48 + "：'Scenario FAILED' 却 operationSuccess=true；2.7.49 改为 false（步骤解析、模式恢复通过）")
o("DownloadToPlc", FIXED, D48 + "：路由树里 192.168.0.1 只在 PcInterface.Subnets[MCP_PN].Addresses 下、目标接口 1 X1 无地址 → 'No download route reaches'；2.7.49 取子网/网关地址，没有时官方 Addresses.Create + 5 参 Download；虚拟网卡无 IP（[no IP]）时还要在虚拟机给它配 192.168.0.x，或实例改 Softbus")
o("GoOnline", FIXED, D48 + "：从不套用路由（GoOnline() 用 TIA 上次的路由）→ 'The connection cannot be established'；2.7.49 选路由 + GoOnline(ConfigurationAddress)，新增 pgPcInterface")
o("CompareSoftwareToOnline", FIXED, D48 + "：依赖在线（GoOnline 未成）；2.7.49 上线后重跑")
o("ReadPlcBlockFingerprints", TIA, D48 + "：FingerprintDataProvider 在 1515F-2 PN V2.9（TIA V21）上 GetService 为 null（PlcSoftware 与 CPU 项都没有）")
o("UploadDeviceParameters", TIA, D48 + "：ParameterUploadProvider 在 1515F 的 Device / 导轨 / CPU 项上都不可用（GetService 为 null）")
o("UploadStationFromPlc", FIXED, D48 + "：扫描到的 PLCSIM 实例只有 MAC（无 IP），路由树无该地址 → 拒绝；2.7.49 对扫描到的地址 Addresses.Create 后再选")
o("ManagePlcDataBlockSnapshot", PASS, D48 + "：createSnapshot 原生返回 + exportSnapshot 2602 字节（离线；值不可独立核验）")
o("SetWatchTableModifyValue", FIXED, D48 + "：Entries.Create(address) 不存在（官方是 PlcTableCommentEntryComposition.Create() + SetAttribute）→ 'Could not create entry'；2.7.49 改官方写法并读回")
o("ProbeS7CpuIdentity GetPlcRunStateS7 ReadPlcLiveValuesS7 ReadPlcWebDiagnostics ReadPlcWebVars", PASS, "2.7.47：只读 S7 协议对当时运行中的 PLCSIM Advanced 实例通过；" + D48 + "：MCP_SIM 未下载（无 IP）时 TCP 连接错误 / Web 超时如实报错")

# ---- 2.7.49 deployment (2026-09-21): route selection verified, watch table / PLCSIM setter facts ----
D49 = "2.7.49 真机"
o("DownloadToPlc", FIXED, D49 + "：路由选择通过（'-> address 192.168.0.1 (subnet MCP_PN)'），TIA 真正发起下载，失败在 PG 侧 '连接到模块 MCP_PLC 失败'（虚拟网卡无 IP）；给网卡配 192.168.0.x 或实例改 Softbus 后重跑")
o("GoOnline", FIXED, D49 + "：路由套用后 TIA 真正尝试连接（'The connection partner is not responding'，PG 侧无 IP）；网卡配 IP 后重跑")
o("GetTechnologyObjects", PASS, D49 + "：MCP_TO 文件夹里的 PID 列出 1 个，删除后 0 个（递归生效）")
o("ExportTechnologyObject ExportTechnologyObjectsToDirectory", PASS, D48 + "：PID_Compact 2.3 编译后单个 / 批量导出通过；" + D49 + "：刚导入未编译的 TO 被 TIA 拒 'Inconsistent blocks … cannot be exported'（规则：导入后先编译）")
o("SetWatchTableModifyValue", FIXED, D49 + "：官方 Entries.Create() 只建注释行，Address/Name/ModifyValue 对 PlcTableCommentEntry 都 'not supported'；且旧代码只在根级找表，每次调用新建一张 MCP_WT_n；2.7.50 改 SimaticML 往返（导出 → 加行 → Override 导入 → 读回）并递归找表")
o("ManagePlcTableEntries", FIXED, "2.7.50 新增 deleteTable（删掉真机上留下的 MCP_WT_1…5）；read / createComment / deleteEntry 早期真机通过")
o("ManagePlcSimAdvancedInstance", FIXED, D49 + "：register … communicationInterface=Softbus 仍失败——实例类与其全部接口都没有可写的 CommunicationInterface 属性（8.0 API）；2.7.50 再试 SetCommunicationInterface()/set_ 方法并把匹配成员列进拒绝信息；powerOff/unregister/register/powerOn 本身通过")
o("WritePlcSimAdvancedTags RunPlcSimAdvancedTestScenario", PASS, D49 + "：不存在的标签 / 失败场景现在 operationSuccess=false（实例无程序，读写内容待下载后）")
o("UploadStationFromPlc", FIXED, D49 + "：工程级 StationUploadProvider 的 PC 接口既无目标接口也无子网，MAC 地址建不了；2.7.50 再试 PcInterface.Addresses.Create")

# ---- 2.7.50 deployment (2026-09-21): garbage tables gone, ModifyIntention read-only, PLCSIM 8.0 network mode is manager-level, MAC is not an address ----
D50 = "2.7.50 真机"
o("ManagePlcTableEntries", PASS, D50 + "：deleteTable 把根级 MCP_WT_1…MCP_WT_5 逐张删掉并读回缺席（dryRun 先报 0 行），GetPlcWatchTables 只剩 MCP_W/MCP_WT，工程已保存；read / createComment / deleteEntry 早期真机通过")
o("GetPlcWatchTables ExportPlcWatchTable ReadTransferRoutes", PASS, D50 + "：删表后列出 MCP_W/MCP_WT；导出到桌面 mcp50_wt.xml；路由树两块网卡仍 addresses: []（PG 侧无 IP）")
o("SetWatchTableModifyValue", FIXED, D50 + "：SimaticML 往返按组路径找到表（entriesBefore 1、appended），Import(Override) 被 TIA 拒 \"'set_ModifyIntention' is not supported … The property 'ModifyIntention' is read-only\"（原表未受影响）；2.7.51 不再写 ModifyIntention 并在导入前从每行剥掉")
o("ReadPlcSimAdvancedInstances", PASS, D50 + "：memberFilter 列出实例对象真实成员（CommunicationInterface 在 CInstanceNet / IInstance 上都只有 {get}，无 Set… 方法；PowerOn() / PowerOn(UInt32)）；2.7.51 另回报 api.networkMode 与 managerMembers")
o("ManagePlcSimAdvancedInstance", FIXED, D50 + "：虚拟机重启后无实例；register CPU1500_Unspecified 建出 MCP_SIM，communicationInterface=Softbus 仍设不上（8.0 API 没有任何 setter），powerOn 通过（Stop，controllerIP 0.0.0.0）；手册：接口选择是全局 SimulationRuntimeManager.NetworkMode——2.7.51 改走它")
o("ScanAccessibleDevices", PASS, D48 + "：扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 找到实例；" + D50 + "：重注册后 MAC 02-C0-A8-00-C8-00 'S7-1500 (PLCSIM)'")
o("UploadStationFromPlc", TIA, D50 + "：PcInterface.Addresses.Create(MAC) 被 TIA 拒 \"'02-C0-A8-00-C8-00' does not specify a valid address\"——ConfigurationAddressComposition.Create 只收 IP（官方页只用 IP），未下载过的 PLCSIM 实例 IP 为 0.0.0.0；2.7.51 拒绝信息明说；有 IP 后再跑")
o("DownloadToPlc", FIXED, D49 + "：路由选择通过（'-> address 192.168.0.1 (subnet MCP_PN)'），TIA 真正发起下载，失败在 PG 侧 '连接到模块 MCP_PLC 失败'（虚拟网卡无 IP）；" + D50 + "：PG 侧未变（网卡无 IP），未重跑；给网卡配 192.168.0.x 或 2.7.51 的 Softbus 网络模式后重跑")
o("GoOnline", FIXED, D49 + "：路由套用后 TIA 真正尝试连接（'The connection partner is not responding'，PG 侧无 IP）；" + D50 + "：未重跑，同上")
o("CompareSoftwareToOnline", FIXED, D48 + "：依赖在线（GoOnline 未成）；" + D50 + "：未重跑，同上")

# ---- 2.7.51 deployment (2026-09-21): watch-table rows round-trip, Softbus network mode gives TIA a "PLCSIM" PG/PC interface ----
D51 = "2.7.51 真机"
o("SetWatchTableModifyValue", PASS, D51 + "：%M0.0 行 appended（DisplayFormat 由 TIA 定为 Bool）、\"MCP_Start\" 行 updated（保留 %I0.0），readbackVerified 都 true，ManagePlcTableEntries read 核对 2 行；ModifyIntention 读回仍 false（TIA 不按 ModifyValue 推导，Openness 也写不了）")
o("ReadPlcSimAdvancedInstances", PASS, D50 + "：memberFilter 列出实例成员；" + D51 + "：api.networkMode=TCPIPSingleAdapter、managerMembers='SimulationRuntimeManager.NetworkMode {get;set}'")
o("ManagePlcSimAdvancedInstance", PASS, D51 + "：powerOff → unregister → register CPU1500_Unspecified communicationInterface=Softbus：route=SimulationRuntimeManager.NetworkMode，TCPIPSingleAdapter → Softbus，实例读回 Softbus；powerOn 后 Stop、controllerIP 192.168.0.1（Softbus 下自带默认 IP，TCPIP 下曾是 0.0.0.0）")
o("ReadTransferRoutes CheckDownloadReadiness", PASS, D51 + "：Softbus 后路由树只剩 PC 接口 'PLCSIM'（子网 MCP_PN 192.168.0.1，两块物理网卡消失），CheckDownloadReadiness Ready=true、两条 PLCSIM 路由")
o("ScanAccessibleDevices", PASS, D48 + "：扫描在 'Siemens PLCSIM Virtual Ethernet Adapter' 上按 MAC 找到实例；" + D50 + "：重注册后 MAC 02-C0-A8-00-C8-00 'S7-1500 (PLCSIM)'；" + D51 + "：Softbus 下在 'PLCSIM' 接口上看到 'S7-1500 CPU:192.168.0.1'（MAC FF-FF-C0-A8-00-01）")
o("DownloadToPlc", FIXED, D49 + "：路由选择通过、失败在 PG 侧（虚拟网卡无 IP）；" + D51 + "：Softbus 后路由 'PLCSIM [no IP] -> 1 X1 -> address 192.168.0.1 (subnet MCP_PN)'，TIA 报 '连接到模块 MCP_PLC 失败'——同 GoOnline，FW 2.9 的 TLS 信任提示无人应答；2.7.52 总是订阅 OnlineLegitimation 并按 trustDeviceCertificate 答 Trusted")
o("GoOnline", FIXED, D49 + "：路由套用后 'The connection partner is not responding'（PG 侧无 IP）；" + D51 + "：经 'PLCSIM' 接口 TIA 报 'Connection to device cannot be established. The device is not trusted. Please check the certificate.'——TlsVerificationConfiguration 从没被应答（处理器只在有密码时订阅）；2.7.52 修")
o("CompareSoftwareToOnline", FIXED, D48 + "：依赖在线（GoOnline 未成）；" + D51 + "：仍等在线（TLS 信任）；2.7.52 后重跑")
o("CompileSoftware", PASS, D51 + "：MCP_PLC 编译 Success（0/0，安全程序一致）")

# ---- 2.7.52 deployment (2026-09-21): TLS prompt answered, online state Incompatible, download blocked by the F-CPU security settings ----
D52 = "2.7.52 真机"
o("GoOnline", FIXED, D51 + "：'The device is not trusted'；" + D52 + "：meta.tlsVerification {plcName MCP_PLC, verificationInfo 'certificate not matching', NonVerified -> Trusted}，提示已应答、TIA 记住；GoOnline 仍抛无正文异常但 GetOnlineState=Incompatible（实例未下载过，预期）；2.7.53 补 ManagePlcProtection 后随下载重跑")
o("GetOnlineState", PASS, D48 + "：Offline；" + D52 + "：TLS 应答后 Incompatible（'online but firmware/config mismatch. Download required.'）——连接已建立")
o("DownloadToPlc", FIXED, D51 + "：'连接到模块 MCP_PLC 失败'（TLS）；" + D52 + "：过了 TLS，TIA 报 '硬件配置编译完成，但出现错误'——F-CPU V2.9 访问级别 NoAccess 无完全访问密码、机密组态数据无密码、通信证书建不了；2.7.53 新增 ManagePlcProtection / CompileDevice 后重跑")
o("CompareSoftwareToOnline", FIXED, D48 + "：依赖在线；" + D52 + "：仍等下载后的在线；2.7.53 后重跑")
o("InvokeService", PASS, D52 + "：Device MCP_PLC 的 ICompilable.Compile() 经桥接跑通（CompilerResult 树回来，3 错 2 警）；PlcMasterSecretConfigurator.ToString 通过；枚举 / SecureString 参数传不了（2.7.53 补）")
o("InvokeObject", PASS, D52 + "：DeviceItem MCP_PLC GetAttribute('PlcProtectionAccessLevel') 被 TIA 拒 'not supported'（访问级别不是设备项属性，是 PlcAccessLevelProvider 的建模属性）")
o("ReadHardwareFeatures", PASS, D52 + "：CPU 项 [导轨_0, MCP_PLC] 上 PlcAccessLevelProvider present、PlcProtectionAccessLevel=NoAccess，PlcMasterSecretConfigurator present（值保留）；itemPathJson 必须是名字数组（[{property,name}] 形式被拒）")
o("DumpDeviceAttributes", PASS, D52 + "：nameFilter 'protect|access|password|secret|certificate' 只见 PlcCommunicationCertificate / Protection* / DisplayProtection——访问级别与主密钥不在属性表里")
o("GetDeviceItemTree", PASS, D52 + "：MCP_PLC 两层树；反射工具的 DeviceItem objectPath 用 'MCP_PLC'（不是 'MCP_PLC/导轨_0/MCP_PLC'）")
o("ManagePlcProtection CompileDevice", FIXED, "2.7.53 新增（" + D52 + " 暴露的缺口：F-CPU V2.9 访问级别 NoAccess 无密码、机密组态数据无密码，硬件编译 3 错拒绝下载）；部署后先 read → setAccessLevel FullAccessIncludingFailsafe → protectMasterSecret → CompileDevice 0 错")

# ---- 2.7.53 deployment (2026-09-21): the online family runs end to end on the standard CPU MCP_STD + PLCSIM Advanced ----
D53 = "2.7.53 真机"
o("ManagePlcProtection", PASS, D53 + "：MCP_PLC / 新建 MCP_STD 都读到 NoAccess + WithoutPassword（TIA V21 新建默认）；setAccessLevel FullAccessIncludingFailsafe / FullAccess、protectMasterSecret 读回 WithPassword，readbackVerified 都 true")
o("CompileDevice", PASS, D53 + "：MCP_PLC 与 MCP_STD 硬件编译 0 错 3 警（无保护级别、显示屏无密码、MCP_S120 未分配 IO 控制器），诊断树摊平")
o("DownloadToPlc", PASS, D53 + "：MCP_PLC（F-CPU）被 TIA 拒 'Loading or overloading fail-safe data in Openness is not permitted'（官方规则）；标准 CPU MCP_STD 到 PLCSIM Advanced 实例 Success ×2——首次 created 地址 192.168.0.1 + masterSecretPassword 应答 PlcMasterSecretPassword（实例变 CPU1515 / 192.168.0.3），第二次子网路由（Stop/Start 提示应答）")
o("GoOnline", PASS, D53 + "：MCP_STD {ipAddress 192.168.0.3, pgPcInterface PLCSIM} Online（路由 PLCSIM -> 1 X1 -> 192.168.0.3 subnet MCP_PN），两次；" + D52 + " 的无正文异常 2.7.54 摊平内层链")
o("GetOnlineState", PASS, D52 + "：Incompatible（未下载）；" + D53 + "：下载后 Online")
o("CompareSoftwareToOnline", PASS, D53 + "：MCP_STD 在线比较 0 差异（6 文件夹 / 2→4 对象一致）")
o("ReadPlcSimAdvancedTags", PASS, D48 + "：无程序时 0 标签；" + D53 + "：下载后列出 MCP_SimDB + 4 成员，按名读 4/4")
o("WritePlcSimAdvancedTags", PASS, D53 + "：MCP_SimDB.Speed 12.5 写入 1/1、readBack 12.5")
o("RunPlcSimAdvancedTestScenario", FIXED, D53 + "：default 模式 PASSED（6/6 步、2/2 断言，Running 跟随 Start）；singleStep 模式 '未找到请求的值 SingleStep'——8.0 API 只有 SingleStep_C/_CT/_P/_CP/…；2.7.54 先试 SingleStep_CP")
o("UploadStationFromPlc", FIXED, D53 + "：{targetIpAddress 192.168.0.3, pgPcInterface PLCSIM} 被引擎拒 'pgPcInterface is ambiguous … PLCSIM (#1) ×3'（工程级提供者按模式重复列出同一网卡）；2.7.54 同名同号视为一块网卡")
o("ReadPlcBlockFingerprints", TIA, D49 + "：1515F-2 PN V2.9 服务为 null；" + D53 + "：1515-2 PN V2.9 同样 FingerprintDataProvider unavailable（TIA 侧）")
o("AddDeviceWithFallback ConnectDeviceNodesToProfinetSubnet GetDeviceIpAddress SearchHardwareCatalog", PASS, D53 + "：MCP_STD（6ES7 515-2AM02-0AB0 V2.9）加入并接到 MCP_PN，X1 192.168.0.3")
o("WritePlcSclSourceFile ManagePlcExternalSources", PASS, D53 + "：SCL 写到桌面（Counter 是 SCL 保留字，改 Cycles）→ createFromFile → generateBlocks 生成 GlobalDB / FC / OB（SCL OB1 覆盖默认 Main）")

# ---- 2.7.54 deployment (2026-09-21): single-step mode switches but steps do not wait for the sync point; station upload refused by TIA for a PLCSIM instance ----
D54 = "2.7.54 真机"
o("RunPlcSimAdvancedTestScenario", FIXED, D53 + "：default 模式 PASSED；" + D54 + "：singleStep 模式切换生效（operatingModeApplied SingleStep_CP，步间 Freeze），Running 断言过，但 {cycles:5} 只推进 1 周期（303→304）——RunToNextSyncPoint 异步、连发被吞；2.7.55 每周期等 Freeze 并优先 SingleStep_C")
o("UploadStationFromPlc", TIA, D54 + "：接口去重生效（PN/IE PLCSIM #1）；工程级扫描把 Softbus 实例列为 192.168.0.1，预览通过（地址在 PLCSIM 接口上创建）；真跑被 TIA 拒 'The selected object cannot be uploaded from the device.'——PLCSIM Advanced 实例不支持上载为新站，设备数仍 5")
o("ScanAccessibleDevices", PASS, D48 + "：虚拟网卡上按 MAC 找到实例；" + D51 + "：PLCSIM 接口上 'S7-1500 CPU:192.168.0.1'；" + D54 + "：无 softwarePath 走工程级 StationUploadProvider（模式 MPI / PROFIBUS / PN/IE），Softbus 实例仍列为 192.168.0.1（虽已配置 192.168.0.3）")
o("GoOnline", PASS, D53 + "：MCP_STD Online ×2；" + D54 + "：MCP_PLC 到 192.168.0.1（实例已是 MCP_STD/.3）报 TIA 原文 'The connection to the target module cannot be established' + onlineStateAfter Offline（预期）")

# ---- 2.7.55 deployment (2026-09-21): deterministic single step, project saved ----
D55 = "2.7.55 真机"
o("RunPlcSimAdvancedTestScenario", PASS, D53 + "：default 模式 PASSED；" + D55 + "：singleStep PASSED 8/8、4/4——operatingModeApplied SingleStep_C，cyclesStepped 5（71 ms），Cycles 304→309 恰好 +5，Start=false 后 2 周期不再增加")
o("SaveProject", PASS, D50 + "：删垃圾表后保存；" + D55 + "：保存 MCP_STD + 程序、两台 CPU 的保护设置、监控表两行（IsModified false）")
o("Connect ListPortalProcessProjects", PASS, D55 + "：虚拟机上同时开着维护者的 AutomaticDipCoatingMachine（pid 15100）与 项目1（4840）时，Connect 没有附加而是又启动了一个空 TIA（pid 15748，MCP 调用超时 60 s 后返回）——ListPortalProcessProjects 列出三个进程，AttachToOpenProject 按名重新绑到 4840；空实例只能在虚拟机上手动关；2.7.56 要改 Connect：有进程时附加 / 拒绝，不再自启")

# ---- 2.7.56 deployment (2026-09-21): Connect attaches to the process holding the wanted project, never spawns ----
D56 = "2.7.56 真机"
o("Connect ListPortalProcessProjects", PASS, D55 + "：两个 TIA 进程时 Connect 误启第三个空实例（15748）；" + D56 + "：引擎重启后两个进程（4840 项目1、15100 维护者工程），Connect {projectName:项目1} 70 ms 附加到 4840、startedNew false、命中即停（candidates 只列 4840），进程数前后都是 2；名字不存在时探测两个进程后绑 4840 并带 warning，仍不自启；GetState boundProcessId 4840 / project 项目1")

# ---- 2.7.57 deployment (2026-09-21, via Update-Engine.ps1): examples in descriptions, preflight report, update check ----
D57 = "2.7.57 真机"
D62 = "2.7.62 真机"
o("Connect ListPortalProcessProjects", PASS, D56 + "：两个进程时按名 70 ms 附加到持有 项目1 的 4840、不自启；" + D57 + "：只开着维护者工程（pid 11104，项目1 未打开）时 Connect {projectName:项目1} 绑到 11104 并带 warning（10.8 s 附加）、startedNew false、消息末尾单句号")
o("Bootstrap", PASS, D57 + "：serverVersion 2.7.57.0，lite 58 / 452，规则含 PLAN, DO NOT PROBE；tools/list 里 45 个描述末尾带 Example")
o("PreflightToolCall", PASS, D57 + "：{name:downloadtoplc, argumentsJson:{SoftwarePath:MCP_STD, targetIp:192.168.0.3}} → NOT READY，UNKNOWN targetIp (did you mean targetIpAddress?)、Case SoftwarePath -> softwarePath、Class L1 PLC-Online ONLINE-WRITE、前提 = 已绑工程、附示例，未执行")
o("CheckForUpdate", PASS, D57 + "：虚拟机有外网，source api，latestVersion 2.7.57 = currentVersion，updateAvailable false，installRoot C:\\Users\\SIEMENS\\Desktop\\TIA_MCP_Delivery_v2.7.57_20260921，updaterScript 找到，ZIP 16035177 B + digest + .sha256；" + D62 + "：howToUpdate 三条、无 -ZipPath，installRoot C:\\Users\\SIEMENS\\Desktop\\TIA_MCP_Delivery_v2.7.62_20260921\\TIA_MCP_Delivery_v2.7.62_20260921，ZIP 16147645 B")

# ---- 2.7.58 deployment (2026-09-21, Update-Engine.ps1 online): schema hints, automatic preflight on failures, recipes ----
D58 = "2.7.58 真机"
o("GetRecipe", PASS, D58 + "：无参列出 12 条配方（topics）；{topic:watch-table} 3 步精确调用")
o("GetBlocks", PASS, D50 + "：MCP_PLC 用户块；" + D58 + "：故意错的直接调用 {SoftwarePath, regex} 被参数诊断拒绝（未执行）并附 preflight（unknown regex → regexName、SoftwarePath → softwarePath、示例、下一步）；正确调用 {softwarePath:MCP_STD, regexName:x} 正常回空表")
o("GetBlockInfo", PASS, D50 + "：读块信息；" + D58 + "：不存在的块 → Block not found + preflight（示例 + '消息已点明原因'）")
o("ExportPlcWatchTable", PASS, D50 + "：导出监控表；" + D58 + "：经 CallTool 缺 watchTableName/exportPath → Meta.preflight.missing + 示例")
o("Bootstrap", PASS, D57 + "：2.7.57.0；" + D58 + "：serverVersion 2.7.58.0，lite 59 / 453，tools/list 59 个工具 0 个属性缺 description，GetAuthoringGuide.topic / ImportFromDocuments.importOption / PlcBuildAndImport.kind 带 enum，default / examples 到位")

# ---- 2.7.59 deployment (2026-09-21): batch-2 enums live, derived-example placeholder defect found ----
D59 = "2.7.59 真机"
o("ManageDccChart", PASS, "2.7.45 真机：临时 S120 驱动轴上图表建删；" + D59 + "：action=bogus 被引擎拒绝并带 preflight.allowedValues.action（10 个取值）")
o("FindTools", PASS, D50 + "：按能力词查工具；" + D59 + "：dcc chart 查到 ManageDccChart 签名与 action 描述，派生示例把 devicePathJson 显示成 '['、chartPath 当主机路径（2.7.60 修）；" + D62 + "：派生示例 devicePathJson \"[]\"、chartPath <Folder/Name>，修正确认")

def status_of(n):
    if n in O: return O[n]
    rs = runs.get(n)
    if rs:
        if any(r["ok"] for r in rs): return (PASS, "")
        return (PARAM, (rs[-1]["text"] or "")[:90])
    if n in prev: return (EARLY, "2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取）")
    return (SKIP, "")

by = collections.defaultdict(list)
for n, x in tools.items(): by[x["domain"]].append(n)
counts = collections.Counter(status_of(n)[0].split("（")[0] for n in tools)
lines = ["# 真机台账（逐工具）", "", "[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [交接](../development/handoff.md)", "",
    "2026-09-20 在维护者新建的空工程 `项目1`（TIA Portal V21，引擎 2.7.45）里用引擎自建的设备把全部工具各跑了一遍，2.7.46 / 2.7.47 部署后（2026-09-21）把 🔁 行与刻意绕开的项重跑；2.7.48 新增 `ManageOpcUaInterface`（448 个）；2.7.48 部署后（2026-09-21）跑了 OPC UA 清理、PID 工艺对象往返和对 PLCSIM Advanced 实例 `MCP_SIM` 的在线族（下载 / 上线被路由与 PLCSIM 设置器缺陷挡住，2.7.49 修）；2.7.49 部署后（2026-09-21）路由选择已通过、下载 / 上线卡在 PG 侧（虚拟网卡无 IP），监控表条目与 PLCSIM 设置器再修（2.7.50）；2.7.50 部署后（2026-09-21）清掉垃圾表、监控表行被 `ModifyIntention` 只读挡住、PLCSIM 8.0 的接口选择原来是全局 `NetworkMode`、站上载不收 MAC（2.7.51 修）；2.7.51 部署后（2026-09-21）监控表行往返通过、Softbus 网络模式让 TIA 出现 'PLCSIM' 接口，但上线 / 下载被 FW 2.9 的 TLS 证书信任提示挡住（2.7.52 应答）；2.7.52 部署后（2026-09-21）TLS 过了、在线态 Incompatible，下载被 F-CPU 的安全设置挡住（2.7.53 新增 ManagePlcProtection / CompileDevice）；2.7.53 部署后（2026-09-21）F-CPU 下载是 Openness 规则拒绝，在标准 CPU `MCP_STD` 上在线族全链走通（下载 / 上线 / 比较 / PLCSIM 读写 / 场景），2.7.54 修 singleStep 枚举名、PLCSIM 接口重复、GoOnline 文案；2.7.54 部署后（2026-09-21）单步只推进 1 周期（2.7.55 按同步点等待）、站上载被 TIA 拒（PLCSIM 实例不支持）；2.7.55 部署后（2026-09-21）单步场景恰好 +5 通过、工程已保存，Connect 在多 TIA 进程时误启新实例（2.7.56 修）；2.7.56 部署后（2026-09-21）Connect 按 projectName 附加到持有 项目1 的进程、不再自启（进程数不增）；2.7.57 部署后（2026-09-21，用更新器）示例进描述、PreflightToolCall 报告、CheckForUpdate 通过；2.7.58 部署后（2026-09-21，更新器在线）schema enum / default / examples、失败自动 preflight、GetRecipe 通过：`MCP_PLC`（CPU 1515F-2 PN V2.9）、`MCP_TP700`（TP700 Comfort V17）、`MCP_UCP`（MTP700 Unified Comfort V21）、`MCP_S120`（S120 CU320-2 PN V5.2 + 驱动轴_1：电机模块 / 电机 / 编码器）；批跑器每步之后检查 TIA 进程还在不在，结果按工具记录在此。状态：",
    "", "| 状态 | 含义 | 数量 |", "|---|---|---:|"]
for k, m in ((PASS, "该工具至少一次真实调用成功（读回验证）"), (MANUAL, "会话级工具，单独手工跑通"), (EARLY, "今天没跑，但 2.7.39–2.7.45 的真机会话跑过"), (FIXED, "真机暴露了缺陷，源码已修，部署后要重跑"), (TIA, "调用到 TIA/环境，被其规则拒绝或对象不提供（不是引擎缺陷）"), (PARAM, "只跑到参数/前置条件拒绝（工具逻辑正常，需要更完整的对象或输入）"), (REALCPU, "刻意不跑"), (SKIP, "未跑")):
    lines.append(f"| {k} | {m} | {counts.get(k, 0)} |")
lines += ["", "TIA 退出点（都已写进交接 §5）：⑧ `AddDevice` 建 WinCC Unified 面板用了 `/20.0.0.0` 标识（TIA V21，2.7.46 守卫）；⑨ `ManageTechnologyObject create TO_PositioningAxis 6.0`（1515F-2 PN V2.9；5.0 正常）；⑩ 经典画面 XML 的尺寸与面板不一致（640×480 导入 TP700 800×480，2.7.48 守卫）。", ""]
for d in sorted(by):
    lines += [f"## {d}（{len(by[d])}）", "", "| 工具 | 状态 | 说明 |", "|---|---|---|"]
    for n in sorted(by[d]):
        st, note = status_of(n)
        lines.append(f"| `{n}` | {st} | {note.replace('|', '/')} |")
    lines.append("")
out = os.path.join(ROOT, "docs/reference/real-machine-ledger.md")
io.open(out, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
print(out); print(json.dumps(dict(counts), ensure_ascii=True))
