# 官方 Openness 与 MCP v2.7.14 覆盖核对

核对日期：2026-09-15；产品基线：`9c0069a3f2349ec3a64d9fe64c059a118c151462`，260 个 MCP 工具。

## 结论与口径

**官方仍有大量能力未封装，v2.7.14 不是官方 API 全覆盖。** 上一版新增 15 个工具覆盖的是十类常用工程能力中的特定动作；同领域其他动作、复杂属性和原生服务仍有缺口。

西门子提供的是 .NET Openness API，不是与本项目一一对应的 MCP 工具。一项 MCP 工具可能组合多个 API，也可能只是规划、离线文件生成或反射探测。不能用 260 除以官方方法数计算覆盖率。

本次完成全部已提供 XML 的成员盘点、全部 260 个工具的清单核对，并按下面的工程能力域检查源码。**没有逐个运行数万个成员，也没有完成每个成员的调用链证明**。原始成员表的 `coverageVerdict` 保留 `NOT_ASSESSED_PER_MEMBER`；源码词法命中只是查找线索，不能冒充实现证明。动态属性、许可证、设备型号和未提供的产品组件不在静态盘点的完整性保证内。

状态：**有接口**＝有对应专用入口，只对所列范围成立；**部分**＝已有相关入口但不能完成整个能力域；**缺专用**＝260 个入口中未找到完整的专用操作，不排除个别动作可以经通用反射到达；**另类**＝插件基础设施等，不应机械转换为 MCP 工具。所有状态均不等于真实工程验收通过。

## 全量盘点范围

| 版本 | 官方 XML 文件 | 类型 T | 方法 M | 属性 P | 字段/枚举 F | 事件 E | 合计 |
|---|---:|---:|---:|---:|---:|---:|---:|
| V21 | 18 | 2,847 | 13,871 | 5,115 | 12,319 | 11 | 34,163 |
| V20（已提供集合） | 2 | 2,395 | 12,030 | 4,481 | 11,105 | 8 | 30,019 |

V21 粗略排除集合遍历、通用属性访问和对象基础方法后，剩下 1,663 个“领域方法候选”，仍包含重载、插件基础设施及辅助方法，**不是 1,663 项业务功能，更不是 1,663 项缺失工具**。

两版精确标识交集为 26,392。V21 中 7,771 个标识不在已提供的 V20 XML，不能据此认定全部是 V21 新增：V21 包含选件及 AddIn 文件，V20 仅有两个合并文档，采样范围不同。

### V21 组件覆盖

| 官方组件 | 当前项目状态 |
|---|---|
| Base | 部分：工程、设备、网络、库、在线、证书、VCI；用户权限、完整多用户和大量服务缺专用封装 |
| Step7 | 部分：块、UDT、标签表、源文件、技术对象、软件单元；ProDiag、DB 快照、装载文件等缺口见后文 |
| WinCCUnified | 部分：画面、变量、脚本、启动设置、报警等；复杂绑定及多个独立对象域仍缺 |
| WinCC | 部分：经典 HMI 的连接、画面、标签表导入导出；脚本/周期/列表/模板等不完整 |
| WinCC.Extension | 另类：辅助值类型；不能按“独立工具缺失”计算 |
| Safety | 部分：运行组及部分标量设置；其他安全工程能力未封装 |
| SafetyValidation | 缺专用：激活测试、条件、评估设备、结果、有效性及报告 |
| TestSuite | 缺专用：StyleGuide、ApplicationTest、SystemTest 原生加载/范围/运行/结果 |
| Startdrive | 缺专用：驱动对象、参数、报文、电机/编码器配置等 |
| CFC | 缺专用：ChartProvider 图表导入导出、保护等 |
| DCC | 缺专用：DCC 图表、块、引脚连接、运行顺序、DCB 库 |
| Sivarc | 缺专用：规则表、表达式解析、布局及生成 |
| TeamcenterGateway | 缺专用：连接、查找、签入签出、下载、保存及修订 |
| AddIn.Base | 另类：TIA 内置菜单/选择/工作流插件，当前外部 MCP 未集成 |
| AddIn.Permissions | 另类：插件权限对象，不是工程对象编辑工具 |
| AddIn.Safety | 另类：安全编译/导入插件回调 |
| AddIn.Step7 | 另类：CAx 导入导出工作流回调 |
| AddIn.Utilities | 另类：插件进程辅助类型，不应当作待开放的任意进程执行工具 |

## HMI Unified 重点差距

以下与本地 `Siemens.Engineering.WinCCUnified.xml` 的类型/方法/属性对照；官方在线目录见文末。只讨论工程 API，不混同 Runtime JavaScript 或在线历史数据访问。

| 能力 | 状态及当前入口 | 剩余差距 / 官方定位 |
|---|---|---|
| 页面与对象列举、创建、导入导出 | 部分：GetHmiScreens、EnsureUnifiedHmiScreen、EnsureUnifiedHmiScreenItem、ImportHmiScreen、ExportHmiScreen | 不等于覆盖全部控件、全属性、复制/删除/尺寸调整；`UI.Screens`、`UI.Base`、`ResizeScreen` |
| 页面/变量表分组 | 有接口：ManageUnifiedHmiGroup | 当前 screens/tags 的创建、重命名、删除空组；没有通用移动/复制及非空递归删除 |
| 图形组合 | 部分：ReadUnifiedGraphicSelection、CompareUnifiedGraphicSelections | 对象选择与坐标包围盒不等于 TIA 原生组合对象 CRUD；不能把普通几何包围盒当组合归属 |
| 变量、结构成员、数组 | 部分：ReadUnifiedTagDefinitions、EnsureUnifiedHmiTag、ImportHmiTagTable | 完整成员修改、阈值/替代值等复杂属性编辑、删除和 UDT 绑定缺完整专用链路；HmiTag、HmiThreshold、Range |
| 系统变量 | 缺专用 | `HmiSystemTag` 专用枚举/定义读取；普通变量清单不能代表系统变量完整覆盖 |
| 连接 | 部分：GetHmiConnections、EnsureUnifiedHmiConnection、ImportHmiConnection | 缺专用删除和所有驱动配置；`HmiConnections` 的 OPC UA 扩展不等于现有 PLC OPC UA 工具 |
| 报警类、模拟报警、离散报警 | 部分：ReadUnifiedEngineeringObjects、ManageUnifiedEngineeringObject | 当前重点为公开标量；多语言文本、关联对象、复杂条件和 AuditClass 不能统一当可写标量 |
| 报警/数据归档 | 部分：同上 | `HmiLoggingCommon.SetLogDuration/SetSegmentDuration`、时长单位及关联对象缺专门适配 |
| **归档变量 LoggingTags** | **缺专用** | `HmiTag.LoggingTags` 的建立/删除、采集触发、周期、归档关联、聚合/平滑；创建 DataLog 并不会自动补齐这些 |
| **审计 AuditTrails/AuditClass** | **缺专用** | HmiAuditTrail 属性与审计配置；只按官方访问性提供支持，不能声称每个审计对象都可任意创建/修改 |
| 文本/图形列表 | 部分：ImportUnifiedEngineeringList、ReadUnifiedEngineeringObjects | 缺专门的原生导出闭环、列表内容/多语言条目校验；标量读取不包含完整列表内容。V20 图形列表在当前 API 中有限制 |
| 系统文本列表 | 缺专用 | `HmiSoftware.HmiSystemTextLists` 与普通 HmiTextLists 是不同集合，当前七类集合适配未覆盖 |
| 全局脚本 | 部分：ListUnifiedGlobalScripts、ReadUnifiedGlobalScript、UpdateUnifiedGlobalScript | 已有原生 JS/YAML 与正文路径；完整调用图、运行时拼接引用、创建/删除模块流程及所有变体仍不能宣称全覆盖 |
| 对象事件、属性事件 | 部分：按钮事件工具、ReadUnifiedScreenBranch | 当前按钮事件编辑不等于全部 EventHandlers/PropertyEventHandlers、画面事件及各控件事件 |
| 动态属性 | 部分：EnsureUnifiedHmiDynamization、BindUnifiedHmiTagDynamization 等 | 公式、资源、闪烁、脚本触发器与复杂关联需要逐类适配；`UI.Dynamization` |
| 脚本语法检查 | 部分，已存在可选调用 | Portal.Software.cs 已有 SyntaxCheck 路径并默认跳过；源码记录过 Portal 崩溃风险，不能再次当作“完全没做”的新工具 |
| **HMI 交叉引用** | **缺专用** | 官方支持画面/对象/变量/报警/连接等 CrossReferenceService；当前 GetCrossReferences 只选择 PLC Block 或 Type |
| **对象原生 Validate/诊断** | **缺专用统一接口** | 连接/变量/归档变量/RuntimeSettings/画面等 Validate 及 HmiValidationResult 诊断结果；CompileAndDiagnoseHmi 不等同逐对象诊断 |
| Runtime 设置 | 部分：ReadUnifiedRuntimeSettings、UpdateUnifiedRuntimeSettings | 只允许 12 个根标量字段；嵌套 OPC UA、Reporting、语言字体、登录、UPSS、Telemetry、UnifiedTagSettings 等尚未完整封装 |
| 面板实例/类型 | 部分：ReadUnifiedFaceplateInstance、ReadUnifiedLibraryType | 官方容器和 ContainedType 属性、UDT 分配与“类型内部画面完整导出”不同；此前元数据 XML 不能认定内部绑定已读到 |
| 复杂控件、阈值、列、工具栏 | 部分：ReadUnifiedScreenBranch、布局/设计工具 | 有深度/节点/类型适配限制；`UI.Parts` 等集合的 CanCreate/CanDelete/Create/Delete 尚未形成完整专用编辑与验收 |
| 自定义 Web 控件/动态 Widget | 部分通用对象访问，缺完整专用管理 | 官方容器属性可见不等于控件包安装、部署及内部脚本均有 API |
| **PlantViews / PlantViewNodes** | **缺专用** | `Cpm.PlantViewsProvider`、PlantView、PlantViewNode 的枚举/创建/删除/配置 |
| **CPM / PlantObject** | **缺专用** | PlantObject、接口/成员变量、归档变量、Validate；不等同普通 HMI 根变量 |
| **HMI OPC UA Alarm 类型** | **缺专用** | `HmiOpcUaAlarm`、`SetNodeIdAndConnection`；现有报警标量适配只选七类集合，不包含此域 |
| 许可证/设备支持性探测 | 部分：Doctor、能力探测 | 基础环境通过不能说明每个 Unified 可选功能均可用；需按设备/组件返回支持证据 |

**没有证据证明可以直接新增的项目**：Unified 任意脚本文件夹、任意面板类型内部内容访问、工程中的全部配方/报表/调度任务 CRUD。不能因为 TIA 界面或 Runtime API 有这些功能，就认定 Openness 有相同入口。相关控件或 Reporting 设置存在，也不等于配方/报表定义集合可完整编辑。这些应标为待核实，而非“官方已支持但 MCP 漏了”。

## PLC、工程、硬件、库等差距

| 能力域 | 状态 / 当前工具 | 剩余官方能力示例 |
|---|---|---|
| 连接、打开、保存、另存、关闭 | 有接口：Connect、AttachToOpenProject、OpenProject、SaveProject 等 | 单独的权限/打开模式、跨会话标识和所有异常语义不能认为全覆盖 |
| 归档/恢复工程 | 部分：ArchiveSavedProject | ProjectComposition.Retrieve / RetrieveWithUpgrade 缺专用恢复入口；OpenProject 不是解归档工具 |
| 工程语言、多语言文本、图形资源 | 部分：HMI 文本读取及部分文本写入 | ExportProjectTexts / ImportProjectTexts、工程语言管理、图形资源管理缺完整专用流程 |
| 工程保护、用户/角色/UMC | 缺专用 | Umac、AdvancedProtection、角色/功能权限、UMC 同步；通用设备属性设置不等于用户管理 |
| 事务、事件、对象自描述、诊断 | 部分内部基础设施：DescribeObject/Service、InvokeObject/Service | 内部独占访问不等于通用事务接口；动态服务/属性必须按实际对象确认，反射不能视作全 API 已支持 |
| 块/UDT 列表、读取、导入导出、删除、分组 | 有接口但范围有限：GetBlocks、DescribeBlockLogic、ImportType、ManagePlcUserGroup 等 | 并非任意语言/块类型/保护状态均支持；非空组删除、复杂移动及所有重载不在已验证范围 |
| **原生实例 DB 创建** | 缺专用 | PlcBlockComposition.CreateInstanceDB；离线 XML GlobalDB 构建不等于基于 FB 的原生实例 DB 创建 |
| **DB 接口值/快照** | 缺专用 | SW.Blocks.Interface 的 CreateSnapshot、LoadSnapshotAsActualValues、LoadStartValuesAsActualValues；包含在线影响的操作不能混为只读 |
| 外部源 | 部分：WritePlcSclSourceFile、ImportPlcExternalSource、GenerateBlocksFromExternalSource | 官方 GenerateSource（从块反向生成源）缺专用入口 |
| **装载文件** | 缺专用 | SW.Loader.GenerateLoadable；与 DownloadToPlc、XML 导出不同 |
| 块保护、写保护、指纹修改 | 缺专用 | Protect/Unprotect、块 Change、指纹与绑定密码；需独立作用范围与权限设计 |
| **ProDiag** | 缺专用 | 创建 ProDiag-FB、监督项、关联 DB/标签、监督 Excel 导入导出和设置 |
| PLC 标签与常量 | 部分：GetPlcTagTables、ImportPlcTagTable、PlcBuildAndImport 等 | 单条标签/常量的直接 CRUD、独立导入导出、完整属性及链接引用缺专用闭环 |
| 监视/强制表 | 部分：枚举/导出、ImportPlcWatchTableOffline | 表/条目直接 CRUD 与分组管理未齐全；强制表导入和在线强制刻意不纳入新导入工具 |
| 技术对象 | 部分：ManageTechnologyObject、Import/ExportTechnologyObject | Motion 轴连接/断开、凸轮点表/二进制/源导入导出、Ident.Connect 等尚未专用封装 |
| 软件单元 | 部分：ManagePlcSoftwareUnit、SetPlcUnitObjectAccess | 单元内源文件、命名空间、主副本、装载文件、全部内部对象操作未齐全 |
| Safety | 部分：ManagePlcSafety | F 基础 ID、默认安全程序、清理系统对象、SafetyPrintout、F-I/O 块生成等未齐全；密码/安全模式并非本次工具范围 |
| **SafetyUnit** | 缺专用 | 安全单元生成、关系、监督项、发布；普通软件单元管理不能代替 |
| **Safety Validation Assistant** | 缺专用 | 测试创建/复制/导入导出、设备/条件配置、有效性、结果、报告 |
| PLC 报警 | 部分：ExportAlarmClasses、ImportAlarmClasses、ExportAlarmTextLists 等 | 报警实例文本目前有导出工具，原生 ImportInstanceTextsFromXlsx 没有配对专用工具 |
| PLC 交叉引用 | 部分：GetCrossReferences | 当前定位 Block/Type；不等于所有 PLC 对象及 HMI 引用全覆盖 |
| OPC UA | 部分：GetOpcUaConfig、SetOpcUaInterfaceEnabled、Import/ExportOpcUaInterface | 引用命名空间、角色权限、节点细节、安全策略等缺完整专用配置；在线 ReadPlcLiveValuesOpcUa 是不同层面 |
| 证书 | 部分：ManagePlcCertificate | 当前指定本地证书存储及 Web/OPC 分配；全局信任、所有动态证书策略/所有者及密码导入不能视为已覆盖 |
| Webserver、Syslog、TLS 等 | 缺完整专用 | Web 应用/系统页面、SIWAREX 用户、Syslog、PLC 账户锁定/密码策略/主密钥；属性探测不是完整配置管理 |
| 硬件目录、设备/模块增删移动复制 | 部分：AddDevice、PlugDeviceItem、ManageHardwareObject 等 | ChangeType、硬件组、硬件标识控制、用户 Logo、所有模块专有参数缺专门适配 |
| 网络、子网、IO 系统 | 部分：EnsureSubnet、ConnectDeviceNodesToProfinetSubnet 等 | 子网删除、断开、IO 系统/DP 主站完整 CRUD、端口连线拓扑配置未齐全 |
| 特殊硬件网络 | 缺完整专用 | 扩展机架、PnPn/CCDX 传输区、等时同步、冗余、虚拟模块、遥控数据点、IPC 扩展 |
| AML / CAx | 部分：ExportDeviceAml | 原生 CaxProvider.Import 与多种拓扑/模块/标准化标识导入选项缺专用入口 |
| 系统诊断配置 | 缺专用 | HW.Systemdiagnostics.Settings 的原生 Export/Import，与工程诊断报告不同 |
| 下载、在线状态、在线比较 | 部分：DownloadToPlc、GoOnline、CompareSoftwareToOnline | R/H 主备独立目标、各种下载/认证重载、完整硬件比较与配置扫描未形成完整专用覆盖 |
| **设备上载** | 缺专用 | StationUpload / ParameterUpload；导出当前打开的离线工程不等于从 PLC 上载 |
| RUN/STOP、复位、主密钥 | 缺专用或受限制 | 读运行状态≠控制运行状态；不能计入只读工具，也不能自动现场验证 |
| 库生命周期 | 部分：ProbeGlobalLibrary、库模板分析及导入 | 新建/另存/归档/恢复/比较/清理/协调库、库文件夹 CRUD 未齐全 |
| 库类型/版本/主副本 | 部分：ManageLibraryTypeVersion、CreateLibraryMasterCopy | Discard、CreateFromDocuments、FindInstances、UpdateCheck、UpdateLibrary、主副本复制/删除/比较未齐全 |
| VCI | 部分：GetVersionControlWorkspaces、Create/SyncVersionControlWorkspace 等 | 分组/映射删除、单对象导出、子状态、格式和语言选项等缺专门接口 |
| 多用户协作 | 部分内部支持 | 源码能附着/打开本地会话；服务器连接、项目添加、创建会话、标记、锁状态、提交等完整管理缺 MCP 入口 |
| Teamcenter | 缺专用 | 连接/SSO、查找、签入签出、保存新项/修订、下载、属性 |
| Test Suite | 缺专用 | 原生 StyleGuide/ApplicationTest/SystemTest；本项目 RunOfflineReleaseValidationSuite 是自身测试，不是 Siemens Test Suite |
| Startdrive / Drive Controller | 缺专用 | 电机/编码器、报文、驱动对象、参数与配置；PLC 技术对象 API 不能代替驱动 API |
| CFC / DCC | 缺专用 | CFC 导入导出；DCC 连接、图表、参数、顺序、DCB 库 |
| SiVArc | 缺专用 | 规则表、布局、表达式、HMI 自动生成；BuildUnifiedHmiThemeDesignJson 等自有生成器不是 SiVArc |
| 经典 HMI（非 Unified） | 部分 | 周期、VB 脚本及脚本组、文本/图形列表、画面模板/全局元素、删除等；不能套用 Unified 工具认定已实现 |

## 实现证据与容易误判之处

- `manifest/tools-list.json`：260 个反射得到的工具；旧 `docs/tool-capability-matrix.md` 曾显示 231，本次已按全部 partial 源文件重新生成至 260，不能用旧表判定缺口。
- `Siemens/Portal.UnifiedEngineering.cs`：七类集合白名单和标量读写；不含 LoggingTags、AuditTrails、CPM、SystemTags 或 HMI OPC UA Alarm。
- `Siemens/UnifiedRuntimeSettingsAccess.cs`：12 个允许的根字段；嵌套 Runtime 子对象不在当前适配范围。
- `Siemens/Portal.Software.cs` 的 GetCrossReferences：只按 Block / Type 选择目标；HMI 官方交叉引用尚未接入该工具。
- 同文件已有 Script SyntaxCheck 可选调用及默认禁用说明，不能把它误判为完全缺失。
- `Siemens/Portal.LibraryManagement.cs`、`Portal.SafetyManagement.cs`、`Portal.TechnologyManagement.cs` 等：按 action 白名单支持一部分动作；工具名包含 Library/Safety/Technology 不代表整个产品域已完成。
- 通用 `InvokeObject` / `InvokeService` 有路径、参数转换和策略限制；即使能够调用某方法，也不自动具备分页、生命周期、异常、修改确认及结果回读的业务封装。
- 官方 XML 中含 Private、AddIn 内部接口、枚举和辅助类型；不能把这些都变成需要补齐的公开 MCP 操作。

## 建议的补齐顺序

1. HMI 只读完整性：LoggingTags、SystemTags、HMI CrossReference、对象 Validate、文本/图形列表原生导出、复杂 Runtime 子设置读取。
2. HMI 定点编辑：属性事件、归档变量关联、复杂报警/阈值、列表条目及面板实例接口；保留不支持与未完整标记。
3. PLC/工程常用离线：Retrieve、工程多语言导入导出、原生实例 DB、GenerateSource、ProDiag、技术对象 Motion/凸轮、库/VCI 补齐。
4. 按项目需求启用的选件：Test Suite、SiVArc、Startdrive、CFC/DCC、Safety Validation、多用户、Teamcenter。
5. 独立处理现场动作与身份权限：上载/下载、RUN/STOP、强制、复位、安全密码、用户角色、主密钥。静态 API 存在不提供现场操作授权。

本次是核对与文档产物，没有调用工程写入、在线控制或部署新版 EXE；没有重新发布 Release。

## 复核材料与来源

机器盘点由 `scripts/Audit-OpennessSurface.py` 生成。输出位于本地 `bin-build/audits/openness-v21-v20-2.7.14/`，包含两版全部成员 CSV、V21 领域方法候选、260 工具清单及带官方 XML SHA-256 的统计 JSON。CSV 的版本差异为精确标识差异，不是支持性判定。

官方在线资料（2026-09-15 核对）：

- [V21 Openness API 总目录](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api)
- [V21 Unified 专用目录](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device)
- [LoggingTags](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/loggingtags/description-loggingtags)
- [AuditTrails](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/audittrails/description-audittrails)
- [全局脚本原生接口](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/description-of-global-script)

本地官方 XML 是各表具体 API 标识的主要依据；未复制官方说明正文、示例全文或 PublicAPI DLL。当前没有获得的官方接口与动态字段不以猜测补齐。
