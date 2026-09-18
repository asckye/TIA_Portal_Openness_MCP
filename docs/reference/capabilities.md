# 工程能力与验收边界

本文说明 2.7.18 引擎的能力与缺口，沿用 v2.7.14–v2.7.15 的表格并追加 2.7.18 新增的工具族。静态清单 350 项（7 个大类，见 `ListToolCategories` 与 [工具矩阵](tool-matrix.md)），默认 lite 暴露 56 项。工具数量不表示覆盖全部 API。原生方法按本机官方 V20/V21 PublicAPI 对照实现，每个调用的成员在构建时做程序集形状检查；**所有新增接口均未完成真实工程验收**。

## 2.7.19 新增工具族

| 族 | 工具 | 边界 |
|---|---|---|
| PLCSIM Advanced（`Simulation` 域） | `ReadPlcSimAdvancedInstances`、`ManagePlcSimAdvancedInstance`、`ReadPlcSimAdvancedTags`、`WritePlcSimAdvancedTags`、`RunPlcSimAdvancedTestScenario` | 官方 `Siemens.Simatic.Simulation.Runtime` API 运行时定位并反射调用，DLL 不随包分发；未安装返回 `ApiNotFound`。实例变更 / 写值 / 场景默认预览，需 `confirmInstanceChange` / `confirmWrite` / `confirmRun`。**本机无 PLCSIM Advanced，未做真实运行验证**：API 成员名按官方 V4–V7 文档，版本差异（如 `UpdateTagList` 重载）已做回退，仍可能在真实环境暴露差异 |
| 离线文档 | `RenderPlcBlockDocument`、`GeneratePlcDocumentation` | Mermaid 图按导出连线生成，不是梯形图版式；SCL 由令牌还原，不可回导；手册最多 2000 个文档 |
| SCL 预检 | `LintPlcSclSource` | 13 条启发式规则，"无发现"不等于可编译；`CompileSoftware` 是判决 |
| AML 生成 | `BuildDeviceAmlDocument` | 推荐传 `referenceAmlPath`（`ExportDeviceAml` 导出）复用版本匹配的头部与角色类库；内置骨架**未经真实导入验证**，`Meta.importVerified=false` |
| 写保护钩子 | `hooks/tia-write-guard.ps1`（Claude Code PreToolUse） | 审计非只读调用、拒绝真实 ONLINE-WRITE；只对 Claude Code 插件生效，其他 MCP 客户端仍靠工具自身的 `dryRun` + `confirm*` |

分类修正：6 个运行时/上载写入工具由 WRITE 改为 ONLINE-WRITE。

## 2.7.18 新增工具族

| 族 | 工具 | 边界 |
|---|---|---|
| 下载提示（缺陷修复） | `DownloadToPlc` 新参数 `userManagementMode`、`promptAnswersJson`、`moduleAccessPassword`、`blockBindingPassword`、`masterSecretPassword` | 43 种提示按真实形态应答；破坏性提示默认 NoAction/NoChange，需 `promptAnswersJson` 显式指定；未应答提示回传 `Meta.promptsUnanswered` |
| 设备传输 | `ScanAccessibleDevices`、`UploadStationFromPlc`、`UploadDeviceParameters`、`DownloadPlcToFolder` | 扫描为在线网络探测；上载要求 `confirmUpload`，目标地址须与扫描结果精确一致；参数上载 V20 无 API；文件夹下载要求新目录或空目录 |
| PLC 块服务 | `ManagePlcBlockProtection`、`ManagePlcDataBlockSnapshot`、`UpdatePlcProgram`、`ReadPlcBlockFingerprints`、`ImportPlcAlarmInstanceTexts`、`ManagePlcAlarmTextList` | 保护/取消保护需 `confirmProtectionChange`；快照装载改变 CPU 实际值需 `confirmValueChange`，V20 无 `ValueService`；指纹读取是在线调用；报警文本列表 API 无条目与 `Create(string)`，只有主副本创建/删除 |
| 工程安全与协作 | `ReadProjectUserManagement`、`ManageProjectUserManagement`、`ReadProjectProtection`、`ManageMultiuserSession`、`CompareLibraries`、`CompareProjects`、`ReadProjectSettings` | UMAC 15 种动作需 `confirmChange`；不启用/停用工程保护、不做 UMC 同步；官方无工程级"已保护"标量；比较接口 V20/V21 命名空间不同，按反射绑定 |
| 硬件服务 | `ReadCommunicationConnections`、`ManageCommunicationConnection`、`ManageWatchForceTableWebAccess`、`ExchangeSystemDiagnosticsSettings`、`ReadOpcUaAccessControl`、`ManageOpcUaAccessControl`、`ImportDeviceAml`、`ReadHardwareFeatures` | 通信连接与 OPC UA 访问控制 V20 无 API；连接创建要求调用方精确指定拥有 `ConnectionComposition` 的对象；AML 导入需 `confirmImport` 并回传原生日志哈希 |
| Unified 画面对象（2.7.26，2.7.27 真机修复） | `DescribeUnifiedScreenItemType`、`ManageUnifiedScreenItem` | 目录与 schema 反射自加载的官方 API（V21 43 个具体类型 / V20 37 个）；`create` 经原生 `Create<T>(name[, containedType])`，按名称查回核对；部件嵌套对象、多语言按 culture（任意深度，如 `Title.Text`）、颜色 `#AARRGGBB`，每个叶子读回；`delete` 需 `confirmDelete`；事件/动态化仍用各自工具 |
| Unified UI 对象模型 | `ReadUnifiedObjectEvents`、`ManageUnifiedObjectParts`、`ManageUnifiedDynamization`、`ManageUnifiedScreenLayout`、`ManageUnifiedListEntries`、`ReadUnifiedAlarmCommon`、`ReadUnifiedAuditSettings` | 阈值/数据网格/报警行列无原生 `Create`；画面复制与布局字段导入导出官方无 API；Unified 列表条目官方无类型，`ManageUnifiedListEntries` 的变更动作明确 NotSupported |
| Motion / ProDiag / 经典 HMI | `ReadMotionAxisConfiguration`、`ManageMotionAxis`、`ManagePlcSupervision`、`ReadClassicHmiScripts`、`ManageClassicHmiScript`、`ManageClassicHmiCycle`、`ManageClassicHmiTextGraphicList`、`ReadClassicHmiGlobalization`、`ReadClassicHmiFaceplates` | ProDiag 无类型化监督组合，只能经官方动态组合接口；经典 HMI 脚本/周期/列表无 `Create(string)`，新对象只能经原生 XML 导入 |
| 运行时通道（不经 Openness） | `ReadPlcWebVars`、`WritePlcWebVars`、`ReadPlcWebDiagnostics`、`SetPlcWebOperatingMode`；`ReadUnifiedRuntimeTags`、`WriteUnifiedRuntimeTags`、`ReadUnifiedRuntimeAlarms`、`UnifiedOpenPipeRequest` | 写入与模式切换需 `confirmWrite` / `confirmModeChange`；证书默认校验；Open Pipe 只在本机、需 "SIMATIC HMI" 组；订阅类消息拒绝 |
| 离线分析 | `ComparePlcBlockDocuments`、`ScanPlcSourceAnnotations`、`ExtractPlcBlockMetrics` | 指标来自导出文档，不是西门子质量判定 |
| 分类 | `ListToolCategories`；`FindTools(category=…, domain=…)` | 分类来自引擎内 `ToolTaxonomy` |

## 2.7.14 工具族（工具与范围）

| 工具 | 已实现动作 | 参数重点 |
|---|---|---|
| CreatePlcTypeGroup | 类型组多级创建、幂等复用 | softwarePath、groupPath |
| ManagePlcUserGroup | blocks/types/tags/technology 四类组的 create、rename、deleteEmpty | family、相对 groupPath、newName |
| ManageTechnologyObject | read、create、delete、setParameter | objectPath；创建需官方 typeIdentifier/version；参数需 parameter/valueJson |
| ImportPlcWatchTableOffline | 原生监视表 XML 导入 | filePath、现有 groupPath；仅离线、无覆盖、拒绝强制表/混合对象/DTD |
| ManageHardwareObject | deleteDevice/deleteItem/moveItem/copyItem | devicePathJson/itemPathJson 精确名称数组；移动/复制需目的路径及 position，通过 CanPlug 检查 |
| ManagePlcSoftwareUnit | list/read/create/delete/update/createRelation/deleteRelation | name、relatedUnit、官方 relationType；update 仅公开可写标量属性 |
| SetPlcUnitObjectAccess | 对单元内块/UDT 设置 Published/Unpublished | unitName、objectKind、objectPath、access；原生 API 不支持 OB 发布 |
| CreateLibraryMasterCopy | 从准确块/UDT/设备/画面创建主副本 | sourceKind/sourcePath、已存在 folderPath；device 路径使用 JSON 数组 |
| ManageLibraryTypeVersion | read/edit/release/setDefault/deleteVersion/updateInstances | typePath、version；release 需 newVersion/dependenciesMode；更新实例限定 targetSoftwarePath |
| ManagePlcSafety | read/createRuntimeGroup/deleteRuntimeGroup/updateRuntimeGroup/updateSettings/generateGlobalFIOStatusBlock/cleanSystemGeneratedObjects/generateBaseId/login/logoff/setPassword/revokePassword（2.7.25 强类型重写） | read 含块号段、安全系统版本、文档化属性、运行组 FOB 属性、集体签名（V21）、GlobalSettings、CPU F 能力；写入需 Offline，密码保护需已登录（可 `action=login`，密码直传不记录）；删除/生成/清理/密码类动作需 confirmSafetyChange |
| ManageSafetyGlobalSettings | read/update（2.7.25） | TIA Portal 级 SafetyModificationsPossible、GenerationOfDefaultFailsafeProgram、ManagementOfFailsafeInSoftwareUnitsEnvironment、UsernameForFChangeHistory |
| ReadSafetyBlockSignatures | 单块或整 PLC 逐块 F 签名（2.7.25） | blockPath 可空；值 0 = 无有效签名；离线工程值，不读 CPU |
| ExportSafetyPrintout | 官方安全打印件写文件（2.7.25） | printer PDF/XPS、option All/Compact、documentLayout；拒绝覆盖；返回 SHA-256；TIA 机器需启用对应 Windows 打印驱动 |
| ManagePlcCertificate | list/read/create/import/export/delete/assign/unassign | 精确 DeviceItem 与 certificateId；usage/template properties；可用 assignmentItemPathJson 指定 OPC UA 分配属性所属子模块 |
| ManageUnifiedHmiGroup | screens/tags 的 create/rename/deleteEmpty | 精确相对 groupPath，不支持原生不存在的 ScriptGroups |
| ReadUnifiedEngineeringObjects | 七类集合分页标量读取 | category、精确可选 name、offset/limit |
| ManageUnifiedEngineeringObject | 原生 create/update/delete | category、name、公开可写标量 propertiesJson；不以反射绕过复杂引用类型 |
| ImportUnifiedEngineeringList | 文本/图形列表原生导入 | textLists/graphicLists、filePath、expectedNamesJson；核对导入返回值与名称存在性 |

HMI 七类集合：`alarmClasses`、`discreteAlarms`、`analogAlarms`、`alarmLogs`、`dataLogs`、`textLists`、`graphicLists`。前五类具有原生 Create(string)。后两类没有该方法，创建须走原生文件导入；不能把创建失败解释为集合为空。

## 共同约束

- 新增写入接口默认 dryRun=true。预览不做原生持久修改，不能保证执行时通过许可证、设备能力和语义检查。
- 所有真实修改取得 TIA 独占访问。PLC 用户组、工艺对象、监视表导入、软件单元及 Safety 编辑要求确认 Offline，不自动 GoOffline。
- 精确匹配名称，拒绝歧义；没有“只有一个 PLC 就随便选它”的写入回退。硬件路径使用 JSON 字符串数组，以保留站名中的 `/`。
- 组删除只允许空用户组。设备、软件单元、报警等对象删除可能影响内部对象及外部引用，预览明确说明不包含依赖影响分析。
- 不自动保存、编译、下载或关闭 TIA。原生库版本 Edit/Release/Update 可能按自身语义改变依赖；不是事务回滚。
- 多步骤失败保留可能已修改标志和可用的已完成列表；不要自动重复提交。
- HMI/IPC 句柄失效沿用会话阻断机制，不自动重新绑定继续读取。
- 原有强制操作和通用反射在线写入拦截保持不变；离线监视表文件导入不执行监视表中的修改值。

## 读取完整性与原生限制

- HMI 新读取接口只读取公开标量属性；每对象返回 values、failures、excludedComplexProperties、dataComplete 和 fullObjectComplete。dataComplete 只针对声明的标量范围，不能等同整对象完整。
- offset 分页是实时集合视图，期间应保持集合不变；不是冻结游标。单次最多 500 个对象，超过总枚举边界明确失败。
- 文本/图形列表导入返回 nativeSuccess 和预期名称核对结果，不宣称条目或变量绑定已逐项验证。整个文件可能影响预期名单外的列表。
- V20 supplied API 没有 HmiGraphicLists；返回明确不支持。V20 部分属性通过动态属性写入而不是 CLR setter，目前通用标量修改只支持公开 CLR setter；组重命名单独兼容官方 SetAttribute(Name)。
- Unified ScriptGroups 不存在；Audit Class 的只读字段不可假装支持写入。复杂多语言文本、对象引用、数组和列表条目仍需要专门结构适配。
- 库 updateInstances 调用类型级 UpdateProject，原生 API 选择适用版本；version 参数用于准确证据定位，不承诺强制降级到该版本。
- 库对象仅使用项目库或已在 TIA 打开的全局库，不隐式打开、关闭或保存全局库。
- 证书不处理密码保护的导入或私钥导出，导出拒绝覆盖文件。Safety 不处理登录密码，不设置 SafetyModeCanBeDisabled。
- 已有面板内部正文/绑定、原生图形组合、深层完整快照的未验收问题继续保留，不能因工具数增加就认定解决。

## 官方参考

- [V21 Openness API 目录](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api)
- [工艺对象](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects)
- [Unified 离散报警](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/discretealarms/description-discretealarms)
- [软件单元对象发布](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/publishing-software-unit-object)
- [SafetyAdministration](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness/safetyadministration/safetyadministration)

发布清单中的 engineeringApiShapePassed 只证明被检查的 API 签名、工具暴露及默认预览参数；engineeringLiveEdits 明确标为未测试。真实验证应在可恢复的测试工程中逐类执行，并保存预览、执行结果及读回证据。


## 2.7.15 工具族

v2.7.15 新增的 38 个入口，按范围与边界列出；同样未经真实工程验收。

### 已接入源码

| 范围 | 接口 | 精确边界 |
|---|---|---|
| Unified 定点对象 | ReadUnifiedObjectProperties、UpdateUnifiedObjectProperties | 公开标量与属性模式；复杂字段另行定位。只读分页为实时 offset，不是稳定快照 |
| 多语言 | UpdateUnifiedMultilingualProperty | 现有语言条目，目标回读与其他语言不变检查；不是任意语言集合 CRUD |
| 原生校验 | ValidateUnifiedObject | 参数为空的对象 Validate，独立返回 validationPassed；不调用 SyntaxCheck/Compile |
| HMI 交叉引用 | GetUnifiedCrossReferences | 原生 Sources/References/Locations；不展开运行时拼接名称，不保证复杂字段完整 |
| 原生列表导出 | ExportUnifiedEngineeringList | 单个文本/图形/系统文本列表，必须存在对应 Export 签名；新目录、非空文件、SHA-256；不把哈希校验视为条目核对 |
| 归档变量 | ManageUnifiedLoggingTag | 普通 HmiTag 的 LoggingTags 读/建/改/删，验证 DataLog 名称及标量回读 |
| 归档时长 | SetUnifiedLogDuration | 原生 LogDuration/SegmentDuration 五分量设置，返回原生字符串/数值；不宣称独立单位换算验证 |
| OPC UA 报警类型 | ManageUnifiedOpcUaAlarmType | 原生三参数创建或 NodeId/Connection 更新；连接必须存在，完整绑定有效性需真实验证 |
| 工厂视图/CPM | ReadUnifiedPlantObject、ManageUnifiedPlantNode、UpdateUnifiedPlantObject | 视图/节点精确定位，CPM 接口/成员公开标量，创建和叶节点删除；无递归删除或完整内部绑定保证 |
| 原生实例 DB | CreatePlcInstanceDb | 精确 FB 和目标组，原生 CreateInstanceDB；不是离线 XML 模拟 |
| 原生源/装载文件 | GeneratePlcSourceFromBlocks、GeneratePlcLoadableFile | 显式对象集合与选项，新文件、非空及哈希校验；不下载，不保证语义完整 |
| 工程恢复 | RetrieveProjectArchive | 已连接但没有打开工程/会话的 Portal，新目录，显式 upgrade；绝不自动关闭现有工程 |
| 工程文本 | ExportProjectTexts、ImportProjectTexts | 原生 XLSX 交换；导入返回 ProjectTextResult。updateSourceLanguage 表示更新源语言 |
| 工程语言 | ManageProjectLanguage | 激活/停用/设置编辑与参考语言并回读；禁止停用正在使用的编辑或参考语言 |
| 全局库 | ManageGlobalLibrary | 列举、创建、打开、恢复、保存、另存、显式关闭。升级打开要求 ReadWrite；不隐式保存或关闭工程 |
| 库文件夹 | ManageLibraryFolder | types/masterCopies 精确文件夹读/建/重命名/删除；只删空文件夹 |
| PLC 标签/常量 | ManagePlcTagDefinition | 单条原生读/建/改/删，精确标签表路径；写入要求 Offline，不写在线值 |

已有 ReadUnifiedEngineeringObjects 额外接入 systemTags、systemTextLists、auditTrails、opcUaAlarmTypes。只读系统集合不能经 ManageUnifiedEngineeringObject 写入；审计对象没有对应创建签名时明确返回不支持。

V20 的 PlantViews 是工程属性，V21 是 PlantViewsProvider 服务，分别编译适配；不以一个版本的签名推断另一版本。官方项目访问说明：[Plant views](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/plantobjecttags/plantviews/description-plant-view)。实际名称和签名以本地相应版本官方程序集为准。

### 专用选件与原生交换增补

| 接口 | 实现范围与限制 |
|---|---|
| ExchangePlcSupervisions | ProDiag XLSX 导出/导入/设置导入，返回原生状态及日志路径；不是全部 ProDiag FB 操作 |
| ExchangeCfcCharts | ChartProviderS7 完整 ZIP 导出/导入，显式 modelVersion/filter/deleteAtTarget；不含选择性导出 |
| ReadTestSuiteCases、ExchangeTestSuiteCase、RunTestSuiteCase | styleGuide/application/system 精确案例读取/文件交换/单案例执行；应用和系统测试真执行另需 confirmExternalExecution。未配置测试服务器，也未执行任何测试工程 |
| ExchangeMotionCamData、ConfigureMotionHardwareConnection | Cam 文本/二进制/点列表交换；离线轴驱动/传感器/转矩映射，地址单位为 bit；不向驱动发送运动命令 |
| ManageUnifiedEvent | 普通事件/属性事件精确读取、创建、脚本字段更新和删除；执行修改需预览 token；不执行脚本或 SyntaxCheck，不涵盖所有自定义 Web 事件 |
| ManageStartdriveParameter | 离线 DriveObject 精确编号和参数名读取/修改；不获取 OnlineDriveObject，不写在线参数 |
| ReadSiVArcRules、ManageSiVArcRule、GenerateSiVArc | 规则公开标量、支持 Create(string) 的集合操作、指定 HMI/PLC 原生生成；不代表复杂规则引用全覆盖 |
| ManageLibraryMasterCopy、ImportLibraryTypeDocuments | 主副本精确复制/比较/删除；原生类型文档导入。已有 ManageLibraryTypeVersion 增加 discard/findInstances |
| ManageDccChart、ReadDccObject | 离线 DCC 图表读/建/改/删/文件交换/优化顺序；块、引脚等定点标量读取。不含全部块/引脚连接编辑 |

依赖安装版本、许可及选中对象是否提供服务。缺少组件或签名时返回明确不支持，不降级为成功空列表。V21 选件签名已通过本地官方程序集检查；V20 选件行为未获真实工程验证。

### 调用约定

全部新增写入/文件操作默认 `dryRun=true`。预览只确认对象、公开签名及输入类型，不表示 TIA 已接受所有业务参数。执行异常可能留下部分改动或文件，通过 `mayHaveChanged` / `mayHaveWrittenFiles` 说明；不自动回滚或保存。保存/另存/关闭全局库是该工具的显式动作，不会附带到其他操作。

对象路径示例：

```json
[{"property":"TagTables","name":"Table_1"},{"property":"Tags","name":"Tag_1"}]
```

每一步使用官方公开属性和精确名称。最多 24 步；拒绝 Parent 等反向属性、索引和方法表达式。名称中的 `/`、`+`、`-` 保留在 JSON name 中。普通文件夹路径参数则使用相对层级路径。

`dataComplete` 必须结合 `scope` 理解。标量字段齐全不代表对象内部结构完整；复杂字段在 `excludedComplexProperties` 中列出。分页超过 10,000 个候选对象明确失败，不静默截断；不承诺断点游标或读取期间工程变化下的一致性。CPM、交叉引用和原生文件接口保留完整性限制。

## 尚未完成，不能宣称已加入

2.7.18 之后仍未实现或官方无 API 的项（全量对照见 [官方 API 覆盖清单](openness-coverage.md)）：

- **官方无 API，保持明确拒绝**：独立 RUN/STOP（只能经运行时通道或下载附带）、清除强制、诊断缓冲区、按块选择性下载、Unified 画面复制、Unified 布局字段导入导出、Unified 列表条目类型、经典 HMI 脚本/周期/列表的 `Create(string)`、ProDiag 类型化监督组合、阈值/数据网格/报警行列的 `Create`、工程级"已保护"标量。
- **选件与协作**：SafetyValidation、Teamcenter、UMC 服务器同步与用户/组创建、启用/停用工程保护、Startdrive/SiVArc/DCC/CFC/TestSuite 未在表中列出的剩余动作、主副本/类型版本的 `DetailedCompareResult` 比较、`Connect(Channel)` 与 V20 `Connect(Telegram, …)` 重载。
- **硬件杂项**：App ID、批量硬件参数、Software Controller PSC/资源配置、自定义 Logo、CiR、I-Device PN-GSD 导出、共享设备、GSDX 签名状态、向 PLC 下载附加用户文件；`SelectiveDeleteDownload`、`Upgrade/DowngradeTargetDevice`、`TurnOffSequence`、`OverwriteHmiData`、Startdrive 下载提示无内置默认，需经 `promptAnswersJson` 显式指定。
- **库**：实例清理/更新全部流程、HMI-Library 之外的模板分析。
- **未纳入组件表的 V21 程序集**：`Siemens.Engineering.ScadaExporter.dll`、`SafeKinematics.dll`、`Sinumerik.dll`。
- 没有官方入口证据的功能仍为待核实，不能以反射占位工具宣称支持。`ProgrammingLanguage.ST`（V21，SIMATIC AX 导入块）已核实：引擎对该枚举值只做 `ToString`/`Enum.GetName`，不会失败。

这些能力需要各自的官方参数/生命周期实现和回归验证；选件与真实设备相关功能还需要匹配环境验收。这些构建记录不包含真实虚拟机工程或在线 PLC 的验收。

## 本地验证

离线测试、官方程序集 API 形状检查和实际 EXE HTTP 回归的项数与日期以 [manifest/release-build.json](../../manifest/release-build.json) 为准，本页不重复维护快照（口径见[验证说明](../development/validation.md)）。三类检查都是元数据或本地回环级别，不连接博图，不代表 TIA 工程实际操作成功。

新增功能尚未进行真实 TIA 工程验收。真实安装环境和测试工程需另行验收；此状态不计作验收通过。
