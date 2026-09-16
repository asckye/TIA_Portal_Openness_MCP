# 工程能力扩展与验收边界（v2.7.14）

本表对应 2026-09-15 请求的十类缺口和补充 HMI 能力，不表示覆盖西门子全部 API。原生方法按本机官方 V20/V21 PublicAPI 对照实现；新增接口尚未完成真实工程写入验收。

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
