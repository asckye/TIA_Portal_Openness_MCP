# 工程能力与验收边界

本文合并 v2.7.14–v2.7.15 的能力与缺口说明，适用于当前交付沿用的 2.7.15 引擎。静态清单 298 项，默认 lite 暴露 52 项。工具数量不表示覆盖全部 API。原生方法按本机官方 V20/V21 PublicAPI 对照实现；新增接口尚未完成真实工程写入验收。

## 工具与范围

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
| ManagePlcSafety | read/createRuntimeGroup/deleteRuntimeGroup/updateRuntimeGroup/updateSettings | 可用的签名、运行组、标量设置；保护工程需用户已在 TIA 登录 |
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


---



## 官方能力增补：实现与验收状态

基线：v2.7.14（260 工具）。v2.7.15 新增 38 个入口，静态工具表共 298 个。**不表示官方 API 全覆盖或真实工程验收通过。**

## 已接入源码

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

## 专用选件与原生交换增补

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

## 调用约定

全部新增写入/文件操作默认 `dryRun=true`。预览只确认对象、公开签名及输入类型，不表示 TIA 已接受所有业务参数。执行异常可能留下部分改动或文件，通过 `mayHaveChanged` / `mayHaveWrittenFiles` 说明；不自动回滚或保存。保存/另存/关闭全局库是该工具的显式动作，不会附带到其他操作。

对象路径示例：

```json
[{"property":"TagTables","name":"Table_1"},{"property":"Tags","name":"Tag_1"}]
```

每一步使用官方公开属性和精确名称。最多 24 步；拒绝 Parent 等反向属性、索引和方法表达式。名称中的 `/`、`+`、`-` 保留在 JSON name 中。普通文件夹路径参数则使用相对层级路径。

`dataComplete` 必须结合 `scope` 理解。标量字段齐全不代表对象内部结构完整；复杂字段在 `excludedComplexProperties` 中列出。分页超过 10,000 个候选对象明确失败，不静默截断；不承诺断点游标或读取期间工程变化下的一致性。CPM、交叉引用和原生文件接口保留完整性限制。

## 尚未完成，不能宣称已加入

以下继续沿用 [官方覆盖核对](../archive/openness-audit-v2.7.14.md) 的缺口状态，本次新增接口不能代替：

- Unified 全控件/自定义 Web 事件覆盖、所有动态属性类型、复杂引用赋值、完整面板内部内容、完整列表条目校验；经典 HMI 完整功能族。
- PLC DB 在线快照及实际值装载、完整 ProDiag 和 Motion/Cam、块保护、完整监控/强制表编辑、单元与源的全部操作、PLC 报警文本及 OPC UA 权限族。
- 全部硬件/网络拓扑、工程用户权限/UMC、完整证书信任和设备安全配置。
- 库实例清理/更新全部流程和未在上表列出的动作。
- VCI 增补、多用户、Teamcenter、SafetyValidation；TestSuite、SiVArc、Startdrive、CFC、DCC 未在上表实现的剩余动作。
- 没有官方入口证据的功能仍为待核实，不能以反射占位工具宣称支持。

这些能力需要各自的官方参数/生命周期实现和回归验证；选件与真实设备相关功能还需要匹配环境验收。这些构建记录不包含真实虚拟机工程或在线 PLC 的验收。

## 本地验证

- V20/V21 Release 编译；原有 OpcUaLiveReader 空引用警告仍存在。
- 离线逻辑测试：708 项通过，包括路径边界、重复名称、TimeSpan 精度、新文件限制与哈希内容校验。
- 官方程序集 API 形状及默认预览检查：V20 115 项、V21 130 项（详见 manifest/release-build.json）。仅元数据检查，不代表 TIA 工程实际操作成功。
- 实际 V20/V21 EXE 的本地 HTTP 回归：28 项通过，使用模拟响应与本地回环，不连接博图。
- 新增功能尚未进行真实 TIA 工程验收。真实安装环境和测试工程需另行验收；此状态不计作验收通过。
