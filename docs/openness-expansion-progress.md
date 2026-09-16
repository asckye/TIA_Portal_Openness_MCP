# 官方能力增补：实现与验收状态

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

以下继续沿用 [官方覆盖核对](openness-audit-v2.7.14.md) 的缺口状态，本次新增接口不能代替：

- Unified 全控件/自定义 Web 事件覆盖、所有动态属性类型、复杂引用赋值、完整面板内部内容、完整列表条目校验；经典 HMI 完整功能族。
- PLC DB 在线快照及实际值装载、完整 ProDiag 和 Motion/Cam、块保护、完整监控/强制表编辑、单元与源的全部操作、PLC 报警文本及 OPC UA 权限族。
- 全部硬件/网络拓扑、工程用户权限/UMC、完整证书信任和设备安全配置。
- 库实例清理/更新全部流程和未在上表列出的动作。
- VCI 增补、多用户、Teamcenter、SafetyValidation；TestSuite、SiVArc、Startdrive、CFC、DCC 未在上表实现的剩余动作。
- 没有官方入口证据的功能仍为待核实，不能以反射占位工具宣称支持。

这些能力需要各自的官方参数/生命周期实现和回归验证；选件与真实设备相关功能还需要匹配环境验收。当前没有操作用户虚拟机的工程或在线 PLC。

## 本地验证

- V20/V21 Release 编译；原有 OpcUaLiveReader 空引用警告仍存在。
- 离线逻辑测试：708 项通过，包括路径边界、重复名称、TimeSpan 精度、新文件限制与哈希内容校验。
- 官方程序集 API 形状及默认预览检查：V20 115 项、V21 130 项（详见 manifest/release-build.json）。仅元数据检查，不代表 TIA 工程实际操作成功。
- 实际 V20/V21 EXE 的本地 HTTP 回归：28 项通过，使用模拟响应与本地回环，不连接博图。
- 新增功能尚未进行真实 TIA 工程验收。用户已确认安装选件，环境与测试项目待提供；此状态不计作验收通过。
