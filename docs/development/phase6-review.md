# 阶段 6（4.0）评审：契约与迁移

[重构计划](refactor-plan.md) · [响应与异常](response-and-errors.md) · [适配器决策 D1](adapter-merge.md#待维护者决定) · [运行时布局](runtime-layout.md)

**状态：待维护者选择；本文不是实施授权或已发布契约。** 建议采用独立 `compat3` 配置档保留一个主版本周期，4.0 默认使用新契约；兼容档只转换表示，不能恢复隐式覆盖、升级或重试写入。需要旧原生行为的用户固定使用最后一个 3.x 包。先审正文决策，再按需展开机器生成的明细。

取样于任务开始时的本地 master / 本任务 HEAD `e4e434f34a237777de50f96a43aff9c249e15b81`。执行期间 master 前进到 `3e082b6e86c939559ddbf3ef95e38df265c1800f`：只读核对全部表输入及标记涉及的 162 个源文件内容相同，三个项目的 AssemblyName/TargetFramework 相同，新增引擎文件无工具或 legacy 标记，所以下表仍适用；第 H 步已合并，P4-I1 不假定完成。进入实施前仍应在合并后的 master 重跑清点。仅修改本页和计划入口，不改源码、快照、示例或 manifest，不连接 TIA、PLC、VM 或网络。

## 1. 范围与建议

| 候选 / 目标 | 可选方案与建议 | 受影响者；风险；真机门槛 |
|---|---|---|
| 工具合并、名称一致（G1/G5） | A 全面改为领域化名称；**B 仅合并可证明同义的入口、消除误导名称**。具体映射见附表 A；其他名称保持。批量与单项、预览与执行保留区分 | MCP 客户端、prompts/skills、示例和脚本；中；纯别名离线，合并原生行为须 L5 |
| 字符串 JSON → schema 类型（G5） | A 保留字符串并补描述；**B 数组、领域 DTO、受约束字典**。去掉参数名末尾 `Json`；禁止二次 JSON 编码和逗号列表。动态原生属性保留有值类型限制的字典，不伪造固定 SDK 字段 | MCP、桥接、prompts/skills；高；解析等价离线，导入/写入族 L5 |
| 响应与错误（G5/G10） | A 只统一 meta；**B 一个信封、机器码与明确 outcome**。直接调用、桥接、Foundation 同形；旧堆栈仅入诊断日志 | 所有结果解析器、Studio 错误映射、CLI 报告；高；序列化离线，未知结果/部分写入 L5 |
| lite 数据化（G1） | **A 版本化内嵌名单，首批成员不变**；B 按描述标签推断。名单按发布键、契约版本、profile 选择；别名不同时广告，发现/调用/分页入口必须齐全 | MCP 工具缓存、prompts/skills、Studio 配置；中；离线 |
| 同名产品输出改名（G6/G7） | **A Foundation / V20 / V21 分别命名**；B 统一启动器隐藏后端。优先 A，既有版本目录不动；程序集名与文件同步，源命名空间暂不重命名 | CLI、Studio、启动/更新/打包脚本、织入及反射测试、客户配置；高；重定位离线，启动并连接的发布冒烟 L5 |
| 旧探测、私有默认值、安装目录写入（G7） | **A 严格包根 + 显式工作区 + LocalAppData**；B 继续探测并告警。删除附表 E 的不受支持布局回退；开发锚点与正式相邻部署继续支持 | CLI、Studio、搬包用户、伴随 Python、日志采集；中；离线（模板导入另须 L5） |
| 冻结 MCP 文本（G5/G10） | 已决定 4.0 统一英文；**先迁机器码消费者，再改文本**。TIA 原文/工程名仍是数据，Studio Loc 和 CLI doctor 本地化继续保留 | 错误匹配脚本、FindTools 检索、prompts/skills；高；文本离线，原生异常分类 L5 |
| 同名异义、线程与会话（G2/G9） | **显式安全策略、按版本能力拒绝**；另一选择是保留两个具名行为入口。具体见下节；本轮不统一 MTA/STA，不把“不支持”伪装成空结果 | MCP、Studio、工程脚本；最高；逐受影响版本 L5 |
| 吞异常后继续原生调用（G2/G5） | **逐点决策：失败可证明未执行才允许继续**；另一选择是保持行为并延后。删除自动下线再执行等隐式动作；未知写入结果必须要求重建会话，不自动重放 | 自动化、在线操作、现场工程；最高；故障注入 + L5 |

阶段 4 的 `TiaSharedAdapterPaths` 及旧 Studio 原生路径由 G3/J 的 L5 控制，不因“到 4.0”自动删除。WorkerChannel 协议 2 的 nonce、绑定纪元、不重放约束继续保留；worker 合并、框架升级和第三方 JSON 库替换不随产品改名捆绑。G3/G4/G6 已完成的构建/桌面端工作不重新展开，G8 运行时库也不重新并回引擎。

## 2. D1 行为决策及验收边界

以下均为建议的新行为，不能由别名自动选择旧的危险默认值。

| 决策族 | 当前依据与差异 | 建议 4.0；另一选择；证明 |
|---|---|---|
| 设备创建 | 引擎 `DevicesService.AddDeviceWithFallback` 遍历 MLFB×版本并多次尝试创建；Foundation `PlcDeviceAddPolicy` 精确预览/哈希、单次创建，且硬件型号范围有界 | `CreateHardwareDevice` 使用精确 TypeIdentifier，先 preview、后带相同计划/工程身份 apply；不得丢失 Foundation 型号限制。另选保留显式 `ProbeAndCreate…` 高风险入口。L5 核对原生 create 次数、冲突、失败后状态 |
| 块/类型/表导入与导出 | 引擎单块导入固定 Override，副本改版本/BOM，导出可先删文件；Foundation None 默认、不改版本、暂存发布；目录导入继续/遇错停止不同；Studio 还有根组、文本识别及 WithDefaults 差异 | 显式 `overwrite=false`、`versionPolicy=exact`、`onError=stop`，preview/confirm/expectedProjectFile 及计划哈希；统一返回逐项结果，无法覆盖的版本拒绝 `overwrite=true`。另选保持分族入口。L5 覆盖拒绝覆盖、显式覆盖、原文件保全、部分批次与内容回读；SD 文档/软件单元能力不得扩张 |
| 连接、打开、保存、关闭 | Foundation 显式 PID、不启动；引擎可按工程选择/启动并 OpenWithUpgrade，反射回退 Open；Studio 首个可接受进程或启动、复用工程；借用/已修改工程关闭保护与 LocalSession 保存不同 | PID/进程身份与工程身份显式；`upgrade=reject` 默认，`upgrade=allow` 独立确认且版本支持；`reuseOpen=false`，借用/已修改对象拒绝隐式关闭，不自动保存。另选宿主具名策略。L5 逐版本旧工程、副本升级、借用对象及 LocalSession；连接不等于绑定 |
| PLC 路径、外部源、编译 | G9 已拒绝非空未知名称；Foundation 转义路径、Studio 设备名规则仍不同；外部源名称去扩展/删除幂等、批次次序和生成返回值不同；Safety 登录/登出、自动下线不同 | 使用查询返回的精确路径；空值选唯一目标是否保留单独选择。删除需核实、生成返回原生结果或明确 observation；编译显式离线前提并对称结束 Safety 会话。另选保持有说明的策略差异。L5 对别名/歧义、源删除/生成、Safety、失败清理回放；不统一线程 |
| 原生回退 | `OnlineToolPolicy.WithAutoOffline` 在匹配错误文本后下线再重试；`OnlineDownloadService` 可吞 ApplyConfiguration 失败后继续或换路线；VCI 失败后重新获取 workspace | 显式前置条件/路线选择；未知结果立即停止；VCI 仅在已证明只读且对象失效时重新获取。另选逐点延期。L5 注入中断并记录前后调用序列；保留探测可选 API、日志失败、隐私 stderr 等必要吞异常 |

4.0 不能靠移除版本判断“统一能力”。例如 V20 广告的混合 action 工具仍受 `ToolVersionPolicy.CallProblem` 约束；Foundation 的普通 PLC、文件大小、批次数、ASCII/格式与计划校验限制必须保留并进入类型 schema。合并工具要求语义等价证明；做不到就保留独立入口。

## 3. 输入与输出目标

输入族代码供附表 B 使用。所有 DTO 字段采用 camelCase；必填、null、空集合、缺省值分别建模；保留当前长度/深度/数量和枚举限制。兼容档负责旧拼写与字符串解析，新档不同时接收 `x` 和 `xJson`。

| 族 | 建议类型 / 例子（均为提案，不是现在可调用的参数） |
|---|---|
| P 路径段 | `string[]`，`devicePath:["PLC_1"]`；保留大小写与段边界，空数组的根/CPU 含义按工具定义 |
| S 名称/文件列表 | `string[]`，`extensions:[".scl"]`；harmonizeOptions 实际 parser 也读名称数组（描述称 object，迁移时修正）；数字参数列表为 `int[]`（N），`numbers:[1000]`；拒绝逗号字符串 |
| R 反射路径 | `PropertyStep[]`，`objectPath:[{property:"TagTables",name:"Table"}]`；每步 `property:string,name?:string,index?:int`，沿用准入限制 |
| M 属性值字典 | `Dictionary<string,Scalar>`，`properties:{ExternalWritable:true}`；`Scalar=string/number/bool/null`，逐操作校验准确属性名；CPU settings 为 `{exactAttributes:…}` |
| L 语言/回答字典 | `Dictionary<string,string>`，`comments:{"en-US":"Motor"}`；promptAnswers 的键和值必须命中该版本提示选项；accessLevels 为 `Dictionary<string,int>` 且保持现有安全值限制 |
| V 动态值 | 有 schema 的 JSON 值联合，`value:42.5`；仅原有工具允许时可为对象，按读回类型验证，拒绝任意 CLR 类型名 |
| C 调用组合 | `ToolCall{name:string,arguments:object}[]`，`operations:[{name:"GetState",arguments:{}}]`；CallTool/Preflight 的 arguments 依据目标 schema 验证，事务及只读批次各保留自己的 allowlist/上限；伴随 CLI arguments 属 S |
| W 写入项 | `WriteValue{name:string,value:Scalar}[]`，`writes:[{name:"Tag_1",value:50}]`；旧对象简写由兼容档转成数组；保留数量与在线写入保护 |
| B 构造 DTO | 按参数建立不同 DTO：UDT `{members:[{name,datatype}]}`、GlobalDB `{dbName,dbNumber,staticMembers:Member[]}`、tagTable `{tableName,tags:[{name,dataTypeName,logicalAddress}]}`、ST `{operations:Statement[]}`、FlgNet `{callName,parameters:CallParameter[]}`、FC/FB `{blockName,blockNumber,inputs/outputs/inouts/statics/temps:Member[],structuredText}`、LAD `{blockName,blockNumber,networks:[{call:FlgNetCall}]}`。例：`udt:{members:[{name:"Ready",datatype:"Bool"}]}`。嵌套 `callJson` 同步迁为 `call`；Foundation `outputReleaseKey` 必填与 V21-only 输出约束保留 |
| H HMI/AML 设计 DTO | Screen `{screen?,items:ScreenItem[]}`、TagTable `{name,tags:HmiTag[]}`、Package `{name,screenDesign,tagTable}`、Layout `{grid?,columns?,items:LayoutItem[]}`、Theme `{name?,palette:Dictionary<string,string>}`、AML 使用 `DeviceAmlSpec`；例 `theme:{palette:{Text:"0xFF000000"}}`。不能把 Classic 与 Unified 的 Screen DTO 当同型 |
| D 领域结构 | 按 action 的封闭 DTO/联合：网络 `{operations:NetworkOperation[]}`；补丁 `BlockEdit[]`（action、精确 selector、expectedValue、value）；依赖 `Artifact{id,dependencies?,target?,priority?}[]`；模板 `TemplateRow{fileName,values}[]`；梯形别名 `AliasRow{source:string[],destination:string[]}[]`；PLCSIM `{instance,mode,steps:Write/Wait/Assert[]}`。例 `artifacts:[{id:"UDT_A"},{id:"FB_A",dependencies:["UDT_A"]}]` |
| X 可选包/快照结构 | `DccPartner{block,pin}`（updateParameter 使用独立参数 DTO）；MotionTarget 为设备项/通道/DB 成员/PLC tag/地址的互斥联合；TestScope 为 `{kind,softwarePath,groupPath,name}[]`；TeamcenterItem 为 `{itemId?,itemName,revisionId?,teamcenterItemType,comment?,teamcenterFolder?,teamcenterProject?:string[]}`、Revision 为 `{revisionId?,comment?}`；SiVArcDeviceSelection 为 `Dictionary<string,bool>`、References 为 `Dictionary<string,{kind,softwarePath?,path?,libraryName?}或null>`；LibrarySelection 为 `{folder:string}或{type:string}[]`；DynamizationMapping 为 `{kind:Simple/Range/Bitmask,properties:受约束属性字典}[]`。例 `partner:{block:"Block_1",pin:"IN1"}` |

X 的只读/分析子族：GraphicSelectionPage[] 直接类型化现有读回页（保留 pageIndex、身份、完整性与逐项值），例 `beforePages:[原始读回页对象]`；XPathRule[] 和 LintRules 分别从审计与 lint parser 生成 schema，例 `rules:[]` / `rules:{}` 表示现有默认策略；MonitoringOptions `{pollMs?:int,source?:string}`，例 `options:{pollMs:1000,source:"watch-table-export"}`；TemplateIntent `{screenType?:string,targetRuntime?:string,preferredComponents?:string[]}`，例 `templateIntent:{targetRuntime:"Unified"}`。这些类型沿用现有 parser 的字段与枚举，不扩大原生反射范围；其余未列出的 SDK 动态属性必须落入 M 的显式边界，不能用无约束 object 代替 DTO。

附表 B 的每个参数都有族；`rulesJson`、`changesJson`、`argumentsJson` 按工具区分，不能仅按参数名机械替换类型。`PlcBuildAndImport.json` 另迁为 `spec`，以已有 `kind` 选择 B 的具体 DTO；`…JsonPath` 仍为文件路径。对象属性字典和开放协议请求是明确的动态边界；`UnifiedOpenPipeRequest.request` 用 `{message:string,params:object,clientCookie?:string}`，params 依 message 校验，内部向 Open Pipe 序列化时才恢复协议要求的大小写。

推荐统一结果如下（字段始终存在，未知值为 null；时间统一 UTC RFC3339）。业务/执行失败以 MCP `isError=true` 返回同一结构，成功为 false；JSON-RPC 协议级错误保持协议错误，不能冒充工具结果。为只读文本客户端同时返回等价的 JSON 文本与结构内容，不在 Message 里再包一层序列化结果。

```json
{"schemaVersion":4,"ok":false,"data":null,"error":{"code":"PROJECT_NOT_BOUND","message":"No project is bound.","details":{}},"meta":{"timestamp":"2026-10-03T00:00:00Z","releaseKey":"21","outcome":"rejected-before-operation","requiresSessionReset":false,"warnings":[]}}
```

成功 `error:null`，领域字段移入 data；`success`/`ok`/`operationSuccess`/`status` 不再互相代替，批量的 data.items 保存每项结果。outcome 区分 `succeeded`、`rejected-before-operation`、`read-failed`、`partial`、`unknown`；后两者不能表示“未执行”，unknown 必须设置 requiresSessionReset。原 Foundation evidence、Executed、数据完整性与计划哈希必须映射保留，不能只留 message。分页使用显式 data.export 句柄，保持列表、读取、保存、删除能力。

错误码首批建议：参数/枚举/重复参数 → `INVALID_ARGUMENT`（details.parameter、allowedValues）；版本/action → `UNSUPPORTED_CAPABILITY`；未绑定/歧义 → `PROJECT_NOT_BOUND` / `TARGET_AMBIGUOUS`；拒绝覆盖 → `ALREADY_EXISTS`；下线前提 → `OFFLINE_REQUIRED`；执行失败 → `NATIVE_OPERATION_FAILED`；结果未知 → `OUTCOME_UNKNOWN`。附表 C 保留当前族和标记计数，不能把历史约数当作互斥工具数。

文本迁移必须同时替换 `TryCanonicalizeEnumArgument` 的消息解析、`OnlineToolPolicy.IsOnlineModeError` 的字串分支、响应快照拒绝标记、`Test-LocalStability.py` stderr 匹配及 Studio 类型/子串映射。每一旧消息先绑定码和 details，再写英文模板；TIA 原始错误保留为 details 中的数据并脱敏，堆栈只进本地诊断。兼容档保留旧文本快照，新档建立独立黄金字节。

## 4. 兼容策略与实施顺序

| 方案 | 收益 / 成本 | 结论 |
|---|---|---|
| 硬切 4.0 | 无双契约成本；所有 prompts/skills、客户端缓存和脚本同步迁移 | 可选，适合所有部署均受控 |
| 同一名单保留旧名一个发布期 | 易发现 alias；同名参数/响应变化仍无法兼容，工具数增加 | 只适合纯改名，不作全局方案 |
| 工具名加 V4 | 可并存；每个名字永久携版本，发现和示例成倍维护 | 不建议 |
| 独立 `compat3` 档（建议） | 单会话只广告一套 schema；旧参数/结果可测试转换，不增加默认名单 | 4.x 保留，5.0 删除；迁移 notice 放 profile 元数据/stderr，不污染旧响应字节。通过拟增 `--contract-profile v4\|compat3` 选择，和 `--profile lite\|full` 正交 |

不允许 compat3 默默把旧的危险调用翻译成新的 apply，也不能删除 Foundation 的确认要求；此类调用应在建会话时明确列为不兼容，客户端固定 3.x 或人工迁移。文档与启动器的旧环境变量/旧 EXE 别名保留同一周期；旧 root 变量与新变量同时指定且不同应拒绝，优先级明确为 CLI 显式根 → 新环境变量 → 旧别名 → 已知锚点。

1. 固定合并后 master 的八版目录、输入/原始响应、lite 名单与示例；审定本页问题。证明：重跑本页生成器、源码/目录一致，更新 D1 真机台账；P4-I1/G3/J 的未验收状态显式保留。
2. 引入类型与错误码的纯逻辑层及 compat3 转换器，每次只迁一个输入/响应族。证明：边界、空值、大小写、字节、双编码拒绝、未知结果保全；八版构建、离线 TRX 门禁；旧档 P0-02/P0-06 零差异（明确拒绝的危险行为另立迁移用例）。
3. 将 lite 名单生成到单一内嵌数据，接入发现、CallTool 与直接路径；随后逐组改名。证明：附表 A/B 逐项无丢失、无重名、不可用 action 仍拒绝、分页可达、profile 与契约快照一致；Foundation 不凭空获得桥接或完整引擎工具。
4. 分开实施硬件、交换、生命周期、PLC 路径/外部源/编译行为，每族独立审查。证明：列出前后 Siemens 调用顺序、参数、线程；旧失败基线、模拟故障及逐受影响版本 L5。没有 L5 的原生行为不进入发布。
5. 产品输出/配置路径 → 包根回退 → 日志/Python/工作区逐步迁移。证明：构建输出、程序集反射和织入清单、安装/开发路径、旧启动别名、客户配置备份迁移；仓库外完整包及只读安装目录离线验收；必需文件清单/生成 manifest 由生成器同步。
6. 从 `reference/tool-examples` 更新 calls、metadata、sequences、语言/操作示例；重新生成嵌入目录及工具矩阵，逐版检索。更新内置 prompts/skills、CLI/Studio 配置和客户端迁移说明；冻结机器码后统一英文。证明：契约与示例参数匹配、英中文 Studio UI、指南/FindTools 搜索、直接/桥接/批次响应一致。
7. 发布前完整八版 L3、两个契约档快照、L5 台账、严格验包；明确 3.x 固定使用与回滚方法。回滚只换产品/配置，不自动降级工程、恢复已写工程或重放调用。

客户端迁移说明按以下顺序发布：支持版本及新能力边界 → EXE/命令/配置键与环境变量替换 → 名称表 → 去 JSON 字符串编码及新例子 → 新响应/错误码/部分与未知结果 → 隐式覆盖/升级/下线行为变化 → lite/发现与缓存刷新 → compat3 的期限、限制及固定 3.x/回滚方法。每条破坏性变化链接到本页对应表及可执行示例。

## 5. 维护者选择

**维护者决定（2026-10-03）：4.0 硬切，放弃 3.x 兼容。** 不提供 `compat3` 配置档，不保留旧工具名、旧参数、旧响应格式、旧 EXE 启动 shim、旧环境变量别名和根目录兼容启动器；仓库探测等兼容回退在 4.0 删除；冻结的 MCP 文本在 4.0 统一改为英文。需要 3.x 行为的用户固定使用最后一个 3.x 包。第 4 节的 compat3 方案及下列问题中与兼容期相关的选项因此作废，其余问题按此前提重新提出，答复后重写本页。

1. 兼容期限：**A compat3 保留整个 4.x、5.0 删除（建议）** / B 仅 4.0、4.1 删除 / C 4.0 硬切？
2. 改名范围：**A 仅附表 A 的建议项（建议）** / B 全量领域前缀改名 / C 只改误导的设备创建名、其他延期？
3. 输入：**A 强 DTO + 明确动态字典边界（建议）** / B 所有 JSON 仅改为 JsonNode（少约束）？
4. 结果：**A 全宿主同一 V4 信封且直接/桥接同形（建议）** / B 保留宿主外壳只统一 error？
5. D1：**A 采用显式覆盖/升级/创建与确认策略（建议）** / B 保留具名双行为入口？尚无实现的覆盖模式保持不支持。
6. PLC 空路径：**A 保留“仅唯一 PLC 时可选”并在 schema 说明（建议）** / B V4 一律显式路径？
7. lite：**A 首批保留现有成员，仅通过名称映射更新（建议）** / B 与改名同时重新筛选名单？
8. 产品：**A 附表 D 三个输出名（建议）** / B 新增统一启动器；旧 GUI 兼容启动器 A 保留一个周期 / B 4.0 删除？
9. 运行目录：**A 新 bundle-root 输入，日志/Python 迁 LocalAppData，私有工作区显式（建议）** / B 延期其中一项（需指明）？
10. 原生行为发布：**A 按族完成 L5 才进 4.0（建议）** / B 所有族一起验收后一次发布？线程统一和 worker 合并均另立任务。

## 附表：当前事实与提案

以下区块由末尾的一次性 Python 命令生成。当前名字、参数、版本取自源码核对后的契约快照，并与完整目录逐行比较；提案由脚本中的显式映射生成，**不会被误认为代码已有实现**。`全` 表示 V14 SP1、V15.1、V16、V17、V18、V19、V20、V21；`E` 为 V20/V21 完整引擎，`F` 为 V14 SP1–V19 Foundation。可用仅指广告，不证明原生能力/action 或真机验收。

<!-- phase6-generated:start -->

### 基线与计数

| 发布键 | 广告工具 | lite | string …Json 参数 | 涉及工具 |
|---|---|---|---|---|
| 14sp1 | 57 | 不设 lite | 9 | 9 |
| 15.1 | 58 | 不设 lite | 9 | 9 |
| 16 | 60 | 不设 lite | 9 | 9 |
| 17 | 60 | 不设 lite | 9 | 9 |
| 18 | 60 | 不设 lite | 9 | 9 |
| 19 | 62 | 不设 lite | 9 | 9 |
| 20 | 477 | 63 | 272 | 156 |
| 21 | 488 | 63 | 289 | 164 |

八版名称并集 496；V21 后缀 Json 输入 291 个，其中 CallTool/PreflightToolCall.argumentsJson 已为 JsonElement?；另有 PlcBuildAndImport.json。不能把它们统称为 string 参数。源码工具名、V21 字符串参数、V20 差集、lite、八版目录及示例覆盖断言均通过。

<details>
<summary>A. 工具改名 / 合并 / 行为迁移（当前参数自动读取）</summary>

| 当前名称 | 建议名称/合并目标 | 当前发布键 | 当前参数名 | 拟议变化 |
|---|---|---|---|---|
| `AddDeviceWithFallback` | `CreateHardwareDevice` | 19, 20, 21 | 19: `confirm, deviceName, dryRun, expectedPlanHash, expectedProjectFile, family, preferredMlfb, preferredVersion`<br>20/21: `deviceName, family, preferredMlfb, preferredVersion` | 精确 TypeIdentifier + preview/confirm/计划与工程身份；D1 |
| `ApplyUnifiedHmiScreenDesignJson` | `ApplyUnifiedHmiScreenDesign` | 20, 21 | 20/21: `designJson, hmiSoftwarePath, screenName, strict` | Json 表示从工具名去掉；设计对象直接返回 data |
| `BuildUnifiedHmiLayoutDesignJson` | `BuildUnifiedHmiLayoutDesign` | 20, 21 | 20/21: `layoutJson` | Json 表示从工具名去掉；设计对象直接返回 data |
| `BuildUnifiedHmiTemplateApplyDesignJson` | `BuildUnifiedHmiTemplateApplyDesign` | 20, 21 | 20/21: `fallbackHeight, fallbackWidth, templateFile` | Json 表示从工具名去掉；设计对象直接返回 data |
| `BuildUnifiedHmiThemeDesignJson` | `BuildUnifiedHmiThemeDesign` | 20, 21 | 20/21: `themeJson` | Json 表示从工具名去掉；设计对象直接返回 data |
| `CloseProject` | `CloseProject` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile`<br>20/21: `无` | 拒绝借用/已修改对象隐式关闭；D1 |
| `CompileAndDiagnoseHmi` | `CompileSoftware` | 20, 21 | 20/21: `softwarePath` | targetKind=hmi；仅 E，祖先软件查找不得丢失 |
| `CompileAndDiagnosePlc` | `CompileSoftware` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, password, softwarePath`<br>20/21: `password, softwarePath` | targetKind=plc；仅在诊断/离线/Safety 等价后合并 |
| `CompileSoftware` | `CompileSoftware` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, password, softwarePath`<br>20/21: `password, softwarePath` | 保留 softwarePath/password；统一 diagnostics 与 preview/confirm；D1 |
| `Connect` | `Connect` | 全 | 14sp1/15.1/16/17/18/19: `processId`<br>20/21: `allowStart, projectName` | 统一 processId/进程与工程身份；启动显式选择；D1 |
| `ConnectIsolated` | `ConnectIsolated` | 20, 21 | 20/21: `无` | 隔离生命周期保留独立入口 |
| `ConnectToProject` | `Connect` | 20, 21 | 20/21: `processId, processStartUtc, projectPath` | 保留 processId/processStartUtc/projectPath 校验 |
| `DeletePlcExternalSource` | `DeletePlcExternalSource` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedPlanHash, expectedProjectFile, externalSourceName, groupPath, softwarePath`<br>20/21: `externalSourceName, softwarePath` | 精确路径，核实删除；不靠扩展名或幂等吞错 |
| `ExportAsDocuments` | `ExportBlockDocuments` | 20, 21 | 20/21: `blockPath, exportPath, preservePath, softwarePath` | 参数保留，D1 显式发布策略；不放开普通块/GlobalDB 限制 |
| `ExportBlock` | `ExportBlock` | 全 | 14sp1/15.1/16/17/18/19: `blockPath, confirm, dryRun, expectedProjectFile, exportPath, preservePath, softwarePath`<br>20/21: `blockPath, exportPath, preservePath, softwarePath` | overwrite=false、preview/confirm，暂存发布；D1 |
| `ExportBlocks` | `ExportBlocks` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedInventoryHash, expectedProjectFile, exportPath, groupPath, maxItems, recursive, softwarePath`<br>20/21: `exportPath, preservePath, regexName, softwarePath` | 同导出策略；保留 inventoryHash/逐项结果 |
| `ExportBlocksAsDocuments` | `ExportBlockDocumentsBatch` | 20, 21 | 20/21: `exportPath, preservePath, regexName, softwarePath` | 批次保留，不改为循环调用单项工具 |
| `ExportPlcTagTable` | `ExportPlcTagTable` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, exportPath, softwarePath, tagTableName`<br>20/21: `exportPath, softwarePath, tagTableName` | 同导出策略；不合并 XML 与 SD |
| `ExportType` | `ExportType` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, exportPath, preservePath, softwarePath, typePath`<br>20/21: `exportPath, preservePath, softwarePath, typePath` | 同导出策略；保留 preservePath |
| `ExportTypes` | `ExportTypes` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedInventoryHash, expectedProjectFile, exportPath, groupPath, maxItems, recursive, softwarePath`<br>20/21: `exportPath, preservePath, regexName, softwarePath` | 同导出策略；保留 inventoryHash/逐项结果 |
| `GenerateBlocksFromExternalSource` | `GenerateBlocksFromExternalSource` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedPlanHash, expectedProjectFile, externalSourceName, softwarePath`<br>20/21: `externalSourceName, softwarePath` | 保留 14sp1 observation 与其他版原生结果区别 |
| `ImportBlock` | `ImportBlock` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, groupPath, importPath, overwrite, softwarePath`<br>20/21: `groupPath, importPath, softwarePath` | overwrite=false、versionPolicy=exact、preview/confirm；D1 |
| `ImportBlocksFromDirectory` | `ImportBlocksFromDirectory` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dir, dryRun, expectedPlanHash, expectedProjectFile, groupPath, importOrder, maxItems, overwrite, regexName, softwarePath`<br>20/21: `dir, groupPath, overwrite, regexName, softwarePath` | onError=stop，保留顺序/计划/数量；D1 |
| `ImportBlocksFromDocuments` | `ImportBlockDocumentsBatch` | 20, 21 | 20/21: `groupPath, importOption, importPath, regexName, softwarePath` | 保留逐项结果/停止与未知结果 |
| `ImportFromDocuments` | `ImportBlockDocuments` | 20, 21 | 20/21: `fileNameWithoutExtension, groupPath, importOption, importPath, softwarePath` | 参数保留，overwrite=false；不支持覆盖的版本拒绝 true |
| `ImportPlcExternalSource` | `ImportPlcExternalSource` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedPlanHash, expectedProjectFile, filePath, groupPath, softwarePath`<br>20/21: `filePath, groupPath, softwarePath` | 精确源名/文件；preview/confirm 与计划；D1 |
| `ImportPlcProgramFromDirectory` | `ImportPlcProgramFromDirectory` | 全 | 14sp1/15.1/16/17/18/19: `blockGroupPath, compileAfter, confirm, dryRun, expectedPlanHash, expectedProjectFile, importOrder, maxItems, overwrite, regexName, softwarePath, sourceDir, stopOnImportFailure, tagFolderPath, technologyFolderPath, typeGroupPath`<br>20/21: `blockGroupPath, compileAfter, dryRun, regexName, softwarePath, sourceDir, stopOnImportFailure, tagFolderPath, technologyFolderPath, typeGroupPath` | 同批次策略；块/类型/表目的地仍独立 |
| `ImportPlcTagTable` | `ImportPlcTagTable` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, folderPath, importPath, overwrite, softwarePath`<br>20/21: `folderPath, importPath, softwarePath` | 同导入策略；保留 folderPath |
| `ImportType` | `ImportType` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, groupPath, importPath, overwrite, softwarePath`<br>20/21: `groupPath, importPath, softwarePath` | 同导入策略；保留目标组与原生类型限制 |
| `OpenProject` | `OpenProject` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile, path`<br>20/21: `closeForeignProject, path, umacPassword, umacUserName, umacUserType` | upgrade=reject、reuseOpen=false；明确确认与工程身份；D1 |
| `PlanPlcExternalSourceImport` | `PlanPlcExternalSourceImport` | 14sp1, 15.1, 16, 17, 18, 19 | 14sp1/15.1/16/17/18/19: `allowedFilePath, confirm, dryRun, expectedPlanHash, expectedProjectFile, filePath, groupPath, softwarePath` | 保持只预览；不别名到可执行导入 |
| `SaveProject` | `SaveProject` | 全 | 14sp1/15.1/16/17/18/19: `confirm, dryRun, expectedProjectFile`<br>20/21: `无` | 显式 preview/confirm 与工程身份；LocalSession 本地保存 |

此表以外的工具名保持；输入变化另见 B，统一响应影响全部广告工具。合并后可用版本为原入口的并集，具体 action/目标能力保留原门禁。大小写风格（例如 SiVArc/Sivarc）不单独批量改名。

</details>

<details>
<summary>B. 全部 JSON 输入逐工具清单（计数为每个签名的 string …Json 数，不跨版本相加）</summary>

| 当前工具 | 发布键 | string 数 | 旧参数 → 新参数（族） |
|---|---|---|---|
| `ApplyUnifiedHmiLayout` | 20, 21 | 1 | `layoutJson` → `layout` (H) |
| `ApplyUnifiedHmiScreenDesignJson` | 20, 21 | 1 | `designJson` → `design` (H) |
| `ApplyUnifiedHmiTheme` | 20, 21 | 1 | `themeJson` → `theme` (H) |
| `AuditEngineeringExports` | 20, 21 | 1 | `rulesJson` → `rules` (X) |
| `BuildClassicHmiMinimalPackage` | 20, 21 | 1 | `packageJson` → `package` (H) |
| `BuildClassicHmiScreenXml` | 20, 21 | 1 | `designJson` → `design` (H) |
| `BuildClassicHmiTagTableXml` | 20, 21 | 1 | `tableJson` → `table` (H) |
| `BuildDeviceAmlDocument` | 20, 21 | 1 | `specJson` → `spec` (H) |
| `BuildFlgNetCallXml` | 全 | 1 | `flgNetJson` → `flgNet` (B) |
| `BuildPlcGlobalDbXml` | 全 | 1 | `globalDbJson` → `globalDb` (B) |
| `BuildPlcTagTableXml` | 全 | 1 | `tagTableJson` → `tagTable` (B) |
| `BuildPlcUdtXml` | 全 | 1 | `udtJson` → `udt` (B) |
| `BuildStructuredTextXml` | 全 | 1 | `structuredTextJson` → `structuredText` (B) |
| `BuildUnifiedHmiLayoutDesignJson` | 20, 21 | 1 | `layoutJson` → `layout` (H) |
| `BuildUnifiedHmiThemeDesignJson` | 20, 21 | 1 | `themeJson` → `theme` (H) |
| `CallTool` | 20, 21 | 0 | `argumentsJson` → `arguments` (C) |
| `CompareProjects` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `targetDevicePathJson` → `targetDevicePath` (P); `targetItemPathJson` → `targetItemPath` (P) |
| `CompareUnifiedGraphicSelections` | 20, 21 | 2 | `afterPagesJson` → `afterPages` (X); `beforePagesJson` → `beforePages` (X) |
| `CompileDevice` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ComposePlcAliasAlarmLad` | 20, 21 | 1 | `rowsJson` → `rows` (D) |
| `ComposePlcFbBlockXml` | 全 | 1 | `fbBlockJson` → `fbBlock` (B) |
| `ComposePlcFcBlockXml` | 全 | 1 | `fcBlockJson` → `fcBlock` (B) |
| `ComposePlcLadFcBlockXml` | 全 | 1 | `ladFcBlockJson` → `ladFcBlock` (B) |
| `DownloadPlcToFolder` | 20, 21 | 1 | `promptAnswersJson` → `promptAnswers` (L) |
| `DownloadToPlc` | 20, 21 | 1 | `promptAnswersJson` → `promptAnswers` (L) |
| `ExchangeCfcCharts` | 20, 21 | 1 | `chartNamesJson` → `chartNames` (S) |
| `ExchangePlcAlarmTextListsXlsx` | 20, 21 | 2 | `culturesJson` → `cultures` (S); `textListNamesJson` → `textListNames` (S) |
| `ExchangeSystemDiagnosticsSettings` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ExchangeUnifiedTags` | 20, 21 | 1 | `expectedTagNamesJson` → `expectedTagNames` (S) |
| `ExtractPlcBlockMetrics` | 20, 21 | 1 | `extensionsJson` → `extensions` (S) |
| `GenerateOpcUaModelledInterface` | 20, 21 | 1 | `accessLevelsJson` → `accessLevels` (L) |
| `GeneratePlcDocumentation` | 20, 21 | 1 | `extensionsJson` → `extensions` (S) |
| `GeneratePlcLoadableFile` | 20, 21 | 1 | `objectPathsJson` → `objectPaths` (S) |
| `GeneratePlcSourceFromBlocks` | 20, 21 | 1 | `blockPathsJson` → `blockPaths` (S) |
| `GenerateSiVArc` | 20, 21 | 2 | `additionalHmiDeviceNamesJson` → `additionalHmiDeviceNames` (S); `plcSoftwarePathsJson` → `plcSoftwarePaths` (S) |
| `GetUnifiedCrossReferences` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `ImportPlcAlarmInstanceTexts` | 20, 21 | 1 | `culturesJson` → `cultures` (S) |
| `ImportSinumerikAlarmTexts` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `filesJson` → `files` (S) |
| `ImportUnifiedEngineeringList` | 20, 21 | 1 | `expectedNamesJson` → `expectedNames` (S) |
| `InstantiatePlcXmlTemplates` | 20, 21 | 1 | `rowsJson` → `rows` (D) |
| `LintPlcSclSource` | 20, 21 | 1 | `rulesJson` → `rules` (X) |
| `ManageClassicHmiCycle` | 20, 21 | 1 | `attributesJson` → `attributes` (M) |
| `ManageClassicHmiScript` | 20, 21 | 1 | `attributesJson` → `attributes` (M) |
| `ManageClassicHmiTextGraphicList` | 20, 21 | 1 | `attributesJson` → `attributes` (M) |
| `ManageCommunicationConnection` | 21 | 6 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `localInterfaceItemPathJson` → `localInterfaceItemPath` (P); `partnerDevicePathJson` → `partnerDevicePath` (P); `partnerInterfaceItemPathJson` → `partnerInterfaceItemPath` (P); `partnerItemPathJson` → `partnerItemPath` (P) |
| `ManageDcbLibraries` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageDccBlock` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageDccChart` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageDccChartInterface` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageDccChartPartition` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageDccPin` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `partnerJson` → `partner` (X); `propertiesJson` → `properties` (M) |
| `ManageDeviceServiceObjects` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageDeviceUsers` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `permissionsJson` → `permissions` (S) |
| `ManageDriveFunctions` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `valueJson` → `value` (V) |
| `ManageDriveHardwareModule` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageDriveSafetyAcceptanceTest` | 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageDriveSecurity` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageDriveTelegrams` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageHardwareObject` | 20, 21 | 4 | `destinationDevicePathJson` → `destinationDevicePath` (P); `destinationItemPathJson` → `destinationItemPath` (P); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageHardwareUtilities` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageIoSystem` | 20, 21 | 4 | `attributesJson` → `attributes` (M); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageLibraryType` | 20, 21 | 2 | `propertiesJson` → `properties` (M); `scopeSoftwarePathsJson` → `scopeSoftwarePaths` (S) |
| `ManageMotionAxis` | 20, 21 | 2 | `propertiesJson` → `properties` (M); `targetJson` → `target` (X) |
| `ManageNetworkDomain` | 20, 21 | 4 | `attributesJson` → `attributes` (M); `participantDevicePathJson` → `participantDevicePath` (P); `participantItemPathJson` → `participantItemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageOnlineDriveFunctions` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageOpcUaAccessControl` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManagePasswordPolicy` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManagePlcCertificate` | 20, 21 | 5 | `assignmentItemPathJson` → `assignmentItemPath` (P); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M); `subjectAlternativeNamesJson` → `subjectAlternativeNames` (S) |
| `ManagePlcGitRepository` | 20, 21 | 1 | `filesJson` → `files` (S) |
| `ManagePlcProtection` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManagePlcSafety` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManagePlcSoftwareUnit` | 20, 21 | 2 | `commentsJson` → `comments` (L); `propertiesJson` → `properties` (M) |
| `ManagePlcSupervision` | 20, 21 | 1 | `attributesJson` → `attributes` (M) |
| `ManagePlcTagDefinition` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManagePortInterconnection` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `partnerDevicePathJson` → `partnerDevicePath` (P); `partnerItemPathJson` → `partnerItemPath` (P) |
| `ManageProjectCompilationSettings` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManageProjectUserManagement` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageSafetyActivationTest` | 21 | 1 | `groupPathJson` → `groupPath` (P) |
| `ManageSafetyActivationTestGroup` | 21 | 1 | `groupPathJson` → `groupPath` (P) |
| `ManageSafetyFunction` | 21 | 2 | `groupPathJson` → `groupPath` (P); `propertiesJson` → `properties` (M) |
| `ManageSafetyFunctionCondition` | 21 | 2 | `groupPathJson` → `groupPath` (P); `propertiesJson` → `properties` (M) |
| `ManageSafetyGlobalSettings` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManageSiVArcRule` | 20, 21 | 2 | `collectionPathJson` → `collectionPath` (P); `propertiesJson` → `properties` (M) |
| `ManageSinumerikArchive` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `modifiedDevicePathJson` → `modifiedDevicePath` (P); `modifiedItemPathJson` → `modifiedItemPath` (P) |
| `ManageSinumerikSafetyMode` | 20, 21 | 1 | `devicePathJson` → `devicePath` (P) |
| `ManageSivarcBlockDefinition` | 20, 21 | 2 | `propertiesJson` → `properties` (M); `textsJson` → `texts` (L) |
| `ManageSivarcTableRule` | 20, 21 | 4 | `deviceNamesJson` → `deviceNames` (S); `deviceSelectionJson` → `deviceSelection` (X); `propertiesJson` → `properties` (M); `referencesJson` → `references` (X) |
| `ManageStartdriveParameter` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `valueJson` → `value` (V) |
| `ManageSyslogServers` | 20, 21 | 4 | `attributesJson` → `attributes` (M); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `ManageTeamcenterWorkflow` | 20, 21 | 3 | `customAttributesJson` → `customAttributes` (M); `itemDetailsJson` → `itemDetails` (X); `revisionDetailsJson` → `revisionDetails` (X) |
| `ManageTechnologyExtensions` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ManageTechnologyObject` | 20, 21 | 1 | `valueJson` → `value` (V) |
| `ManageTestSuiteCase` | 20, 21 | 1 | `scopeJson` → `scope` (X) |
| `ManageTransferArea` | 20, 21 | 8 | `attributesJson` → `attributes` (M); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `partnerDevicePathJson` → `partnerDevicePath` (P); `partnerItemPathJson` → `partnerItemPath` (P); `propertiesJson` → `properties` (M); `targetDevicePathJson` → `targetDevicePath` (P); `targetItemPathJson` → `targetItemPath` (P) |
| `ManageUnifiedDynamization` | 20, 21 | 3 | `mappingEntriesJson` → `mappingEntries` (X); `objectPathJson` → `objectPath` (R); `propertiesJson` → `properties` (M) |
| `ManageUnifiedEngineeringObject` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManageUnifiedEvent` | 20, 21 | 2 | `objectPathJson` → `objectPath` (R); `scriptPropertiesJson` → `scriptProperties` (M) |
| `ManageUnifiedListEntries` | 20, 21 | 1 | `entryJson` → `entry` (M) |
| `ManageUnifiedLoggingTag` | 20, 21 | 2 | `propertiesJson` → `properties` (M); `tagPathJson` → `tagPath` (P) |
| `ManageUnifiedObjectParts` | 20, 21 | 2 | `objectPathJson` → `objectPath` (R); `propertiesJson` → `properties` (M) |
| `ManageUnifiedPlantNode` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManageUnifiedScreenItem` | 20, 21 | 1 | `propertiesJson` → `properties` (M) |
| `ManageUnifiedScreenLayout` | 20, 21 | 2 | `objectPathJson` → `objectPath` (R); `propertiesJson` → `properties` (M) |
| `ManageWatchForceTableWebAccess` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `PatchPlcBlockDocument` | 20, 21 | 1 | `changesJson` → `changes` (D) |
| `PlanArtifactImportOrder` | 全 | 1 | `artifactsJson` → `artifacts` (D) |
| `PlanGlobalLibraryTemplateReuse` | 20, 21 | 1 | `templateIntentJson` → `templateIntent` (X) |
| `PlanHardwareNetworkConfiguration` | 20, 21 | 1 | `planJson` → `plan` (D) |
| `PlanOnlineReadOnlyDataProvider` | 20, 21 | 2 | `optionsJson` → `options` (X); `tagPathsJson` → `tagPaths` (S) |
| `PlanOnlineReadOnlyMonitoring` | 20, 21 | 1 | `tagPathsJson` → `tagPaths` (S) |
| `PlcBuildAndImport` | 20, 21 | 0 | `json` → `spec` (B) |
| `PreflightToolCall` | 20, 21 | 0 | `argumentsJson` → `arguments` (C) |
| `PreviewToolBatch` | 20, 21 | 1 | `operationsJson` → `operations` (C) |
| `ReadCommunicationConnections` | 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadDccCharts` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadDccObject` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `objectPathJson` → `objectPath` (R) |
| `ReadDeviceAddressing` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadDeviceItemChannels` | 20, 21 | 3 | `attributeNamesJson` → `attributeNames` (S); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadDriveObjects` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadDriveParameters` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `namesJson` → `names` (S); `numbersJson` → `numbers` (N) |
| `ReadHardwareFeatures` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadIoSystems` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadObjectIdentifier` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadOnlineDriveParameters` | 20, 21 | 4 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `namesJson` → `names` (S); `numbersJson` → `numbers` (N) |
| `ReadPlcLiveValuesOpcUa` | 20, 21 | 1 | `nodeIdsJson` → `nodeIds` (S) |
| `ReadPlcLiveValuesS7` | 20, 21 | 1 | `itemsJson` → `items` (S) |
| `ReadPlcSimAdvancedTags` | 20, 21 | 1 | `namesJson` → `names` (S) |
| `ReadPlcWebVars` | 20, 21 | 1 | `varsJson` → `vars` (S) |
| `ReadProjectUserManagement` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadSafetyActivationTests` | 21 | 1 | `groupPathJson` → `groupPath` (P) |
| `ReadSiVArcRules` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `ReadToolBatch` | 20, 21 | 1 | `operationsJson` → `operations` (C) |
| `ReadTransferAreas` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `ReadUnifiedGraphicSelection` | 20, 21 | 1 | `itemNamesJson` → `itemNames` (S) |
| `ReadUnifiedObjectEvents` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `ReadUnifiedObjectProperties` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `ReadUnifiedPlantObject` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `ReadUnifiedRuntimeAlarms` | 20, 21 | 1 | `systemNamesJson` → `systemNames` (S) |
| `ReadUnifiedRuntimeSettings` | 20, 21 | 1 | `fieldsJson` → `fields` (S) |
| `ReadUnifiedRuntimeTags` | 20, 21 | 1 | `tagsJson` → `tags` (S) |
| `ReadUnifiedScreenBranch` | 20, 21 | 1 | `branchJson` → `branch` (P) |
| `ResolveSivarcExpression` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `RunPlcCompanionTool` | 20, 21 | 1 | `argumentsJson` → `arguments` (S) |
| `RunPlcSimAdvancedTestScenario` | 20, 21 | 1 | `scenarioJson` → `scenario` (D) |
| `RunTestSuiteCase` | 20, 21 | 1 | `namesJson` → `names` (S) |
| `RunToolsInTransaction` | 20, 21 | 1 | `callsJson` → `calls` (C) |
| `SamplePlcLiveValuesS7` | 20, 21 | 1 | `itemsJson` → `items` (S) |
| `ScanPlcSourceAnnotations` | 20, 21 | 2 | `extensionsJson` → `extensions` (S); `markersJson` → `markers` (S) |
| `SetCpuCommonSettings` | 20, 21 | 1 | `settingsJson` → `settings` (M) |
| `SetUnifiedLogDuration` | 20, 21 | 1 | `durationPathJson` → `durationPath` (P) |
| `ShowObjectInEditor` | 20, 21 | 2 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `SynchronizeLibrary` | 20, 21 | 3 | `harmonizeOptionsJson` → `harmonizeOptions` (X); `scopeSoftwarePathsJson` → `scopeSoftwarePaths` (S); `selectionJson` → `selection` (X) |
| `UnifiedOpenPipeRequest` | 20, 21 | 1 | `requestJson` → `request` (X) |
| `UpdateDeviceAddress` | 20, 21 | 4 | `attributesJson` → `attributes` (M); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `propertiesJson` → `properties` (M) |
| `UpdateDeviceItemChannel` | 20, 21 | 3 | `attributesJson` → `attributes` (M); `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P) |
| `UpdateUnifiedMultilingualProperty` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `UpdateUnifiedObjectProperties` | 20, 21 | 2 | `objectPathJson` → `objectPath` (R); `propertiesJson` → `properties` (M) |
| `UpdateUnifiedPlantObject` | 20, 21 | 2 | `objectPathJson` → `objectPath` (R); `propertiesJson` → `properties` (M) |
| `UpdateUnifiedRuntimeSettings` | 20, 21 | 1 | `changesJson` → `changes` (M) |
| `UploadDeviceParameters` | 20, 21 | 3 | `devicePathJson` → `devicePath` (P); `itemPathJson` → `itemPath` (P); `promptAnswersJson` → `promptAnswers` (L) |
| `UploadStationFromPlc` | 20, 21 | 1 | `promptAnswersJson` → `promptAnswers` (L) |
| `ValidateClassicHmiMinimalPackagePlcSync` | 20, 21 | 1 | `plcSymbolsJson` → `plcSymbols` (S) |
| `ValidateUnifiedObject` | 20, 21 | 1 | `objectPathJson` → `objectPath` (R) |
| `WriteClassicHmiMinimalPackageFiles` | 20, 21 | 1 | `packageJson` → `package` (H) |
| `WritePlcSimAdvancedTags` | 20, 21 | 1 | `valuesJson` → `values` (W) |
| `WritePlcWebVars` | 20, 21 | 1 | `writesJson` → `writes` (W) |
| `WriteUnifiedRuntimeTags` | 20, 21 | 1 | `writesJson` → `writes` (W) |

按 (工具名,参数名) 去重的族计数：B=9；C=5；D=6；H=10；L=7；M=50；N=2；P=128；R=14；S=38；V=3；W=3；X=17；共 292 项 / 167 个工具。Foundation 的同名参数具有更窄的 parser/schema，不因共用 DTO 放宽。详见 [现有调用与操作示例](../../reference/tool-examples/calls.json)。

</details>

<details>
<summary>C. 响应/错误族 → V4 与 legacy 标记</summary>

| 族 | 当前形状/错误 | 建议目标 | 标记站点数（非工具数） | variant 数量 |
|---|---|---|---|---|
| F1 | VersionPolicyTool：isError 文本 + preflight | error.code/details；准入拒绝的 outcome | 0 | 0 |
| F2 | POCO + Meta；McpException 或 success=false | data + ok/error；消除直接 camelCase/桥接 PascalCase 差异 | 25 | `legacy-existing-meta`:2; `legacy-independent-verdicts`:2; `legacy-late-stamp`:1; `legacy-late-verdict`:2; `legacy-multiple-dynamic-fields`:8; `legacy-roundtrip-data-stamp`:2; `legacy-single-verdict`:3; `legacy-stamp-then-verdict`:2; `legacy-stamp-without-verdict`:2; `legacy-verdict-last`:1 |
| F3 | 执行器/领域服务：operationSuccess/status/error，含旧 meta | 逐项 data + outcome/完整性；未知 verdict 不转为 true | 20 | `legacy-independent-verdicts`:1; `legacy-migration-page`:1; `legacy-multiple-dynamic-fields`:5; `legacy-ok-only`:1; `legacy-plcsim-complete`:1; `legacy-plcsim-failure`:1; `legacy-roundtrip-data-stamp`:2; `legacy-runtime-settings`:1; `legacy-single-verdict`:5; `legacy-success-last`:1; `legacy-verdict-last`:1 |
| F4 | 桥接 Message 内序列化 JSON 或 failed 文本 | 直接透传同一信封；禁止二次字符串 JSON | 2 | `legacy-independent-verdicts`:1; `legacy-stamp-then-verdict`:1 |
| F5 | 导出句柄 ok=true；InvalidParams 异常 | data.export；INVALID_ARGUMENT/ALREADY_EXISTS 等码 | 0 | 0 |
| F6 | 旧 Portal 文本失败且无 meta | 在调用边界补 outcome/error；不能靠 Message 判成功 | 0 | 0 |
| F7 | Foundation PascalCase DTO/裸数组、V17 envelopes、McpException 或 isError 文本 | 保留原 evidence/Executed/RequiresSessionReset，转换为同一信封 | 0 | 0 |
| CLI | 报告 roundtrip/ok/后写判定 | 报告适配 V4；CLI 成功/失败退出码另有黄金样本 | 3 | `legacy-late-verdict`:1; `legacy-ok-report`:1; `legacy-roundtrip-report`:1 |

共 50 个实际注释站点、18 个 variant。F2/F3 按注释所在工具边界/服务或执行器归属计数，混合工具不推断唯一运行时族；F6 无标记不等于不存在无 meta 失败。未标注的手写形状仍由 Inventory-ResponseEnvelopes.py 管理。

| variant | 当前源码（同文件可含多个站点） |
|---|---|
| `legacy-existing-meta` | [ModelContextProtocol/Tools/TechnologyObjectsTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/TechnologyObjectsTools.cs)<br>[ModelContextProtocol/Tools/TypesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/TypesTools.cs) |
| `legacy-independent-verdicts` | [ModelContextProtocol/Tools/McpServer.ToolBridge.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.ToolBridge.cs)<br>[ModelContextProtocol/Tools/ProjectSessionTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/ProjectSessionTools.cs)<br>[ModelContextProtocol/Tools/V21EcosystemTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/V21EcosystemTools.cs)<br>[Siemens/Services/HardwareServicesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/HardwareServicesService.cs) |
| `legacy-late-stamp` | [ModelContextProtocol/Tools/PlcSoftwareTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/PlcSoftwareTools.cs) |
| `legacy-late-verdict` | [Cli/ReportBuilders.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/ReportBuilders.cs)<br>[ModelContextProtocol/Tools/ProjectSessionTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/ProjectSessionTools.cs)<br>[ModelContextProtocol/Tools/SessionTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/SessionTools.cs) |
| `legacy-migration-page` | [Siemens/Services/MigrationReadService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/MigrationReadService.cs) |
| `legacy-multiple-dynamic-fields` | [ModelContextProtocol/Tools/DiagnosticsTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/DiagnosticsTools.cs)<br>[ModelContextProtocol/Tools/DocumentsTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/DocumentsTools.cs)<br>[ModelContextProtocol/Tools/EngineeringDiagnosticsTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/EngineeringDiagnosticsTools.cs)<br>[ModelContextProtocol/Tools/HardwareNetworkTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/HardwareNetworkTools.cs)<br>[ModelContextProtocol/Tools/OfflineSuiteTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/OfflineSuiteTools.cs)<br>[ModelContextProtocol/Tools/PlcBlocksTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/PlcBlocksTools.cs)<br>[ModelContextProtocol/Tools/PlcExternalSourcesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs)<br>[ModelContextProtocol/Tools/TypesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/TypesTools.cs)<br>[Siemens/Services/DevicesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/DevicesService.cs)<br>[Siemens/Services/HardwareNetworkService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/HardwareNetworkService.cs)<br>[Siemens/Services/OpcUaService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/OpcUaService.cs)<br>[Siemens/Services/UnifiedHmiService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/UnifiedHmiService.cs)<br>[Siemens/Services/VersionControlService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/VersionControlService.cs) |
| `legacy-ok-only` | [Siemens/Services/HardwareServicesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/HardwareServicesService.cs) |
| `legacy-ok-report` | [Cli/ReportBuilders.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/ReportBuilders.cs) |
| `legacy-plcsim-complete` | [ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `legacy-plcsim-failure` | [ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `legacy-roundtrip-data-stamp` | [ModelContextProtocol/Tools/LibraryTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/LibraryTools.cs)<br>[ModelContextProtocol/Tools/OfflineSuiteTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/OfflineSuiteTools.cs)<br>[Siemens/Services/OpcUaService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/OpcUaService.cs)<br>[Siemens/Services/PlcTablesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/PlcTablesService.cs) |
| `legacy-roundtrip-report` | [Cli/ReportBuilders.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/ReportBuilders.cs) |
| `legacy-runtime-settings` | [Siemens/Services/RuntimeSettingsService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/RuntimeSettingsService.cs) |
| `legacy-single-verdict` | [ModelContextProtocol/Tools/EcosystemTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/EcosystemTools.cs)<br>[ModelContextProtocol/Tools/GitWorkflowTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/GitWorkflowTools.cs)<br>[ModelContextProtocol/Tools/RuntimeChannelTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/RuntimeChannelTools.cs)<br>[Siemens/Services/DevicesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/DevicesService.cs)<br>[Siemens/Services/HardwareNetworkService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/HardwareNetworkService.cs)<br>[Siemens/Services/OnlineDownloadService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/OnlineDownloadService.cs)<br>[Siemens/Services/PlcTablesService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/PlcTablesService.cs)<br>[Siemens/Services/UnifiedHmiService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/UnifiedHmiService.cs) |
| `legacy-stamp-then-verdict` | [ModelContextProtocol/Tools/McpServer.Maintenance.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Maintenance.cs)<br>[ModelContextProtocol/Tools/McpServer.ToolBridge.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.ToolBridge.cs)<br>[ModelContextProtocol/Tools/SessionTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/SessionTools.cs) |
| `legacy-stamp-without-verdict` | [ModelContextProtocol/Tools/AddressesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/AddressesTools.cs)<br>[ModelContextProtocol/Tools/ModulesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/ModulesTools.cs) |
| `legacy-success-last` | [Siemens/Services/AlarmsService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/AlarmsService.cs) |
| `legacy-verdict-last` | [ModelContextProtocol/Tools/DevicesTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/DevicesTools.cs)<br>[Siemens/Services/UnifiedHmiService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/UnifiedHmiService.cs) |

</details>

<details>
<summary>D. 可执行文件 / 程序集 / 路径与配置</summary>

| 项目 | 版本 | 当前 AssemblyName | 建议 AssemblyName | 安装路径迁移 | 目标框架保持 |
|---|---|---|---|---|---|
| [TiaMcpServer.LegacyHost.csproj](../../tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/TiaMcpServer.LegacyHost.csproj) | 14sp1–19 | `TiaMcpServer` | `TiaMcp.FoundationHost` | `runtime/v<key>/TiaMcpServer.exe` → `runtime/v<key>/TiaMcp.FoundationHost.exe` | net8.0 |
| [TiaMcpServer.V20.csproj](../../tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V20.csproj) | 20 | `TiaMcpServer` | `TiaMcp.Engine.V20` | `runtime/v20/TiaMcpServer.exe` → `runtime/v20/TiaMcp.Engine.V20.exe` | net48 |
| [TiaMcpServer.V21.csproj](../../tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V21.csproj) | 21 | `TiaMcpServer` | `TiaMcp.Engine.V21` | `runtime/v21/TiaMcpServer.exe` → `runtime/v21/TiaMcp.Engine.V21.exe` | net48 |

同基名的 .dll/.exe.config/.deps.json/.runtimeconfig.json（以实际构建输出为准）同步改名；旧 EXE 仅作启动 shim。开发输出保留 bin-v20/Release/net48、bin/Release/net48 与 Foundation bin/Release/net8.0 的目录，仅变基名。程序集友元、反射加载、织入目标、worker 启动与构建/打包/更新脚本均需按新名生成，不能仅重命名磁盘文件。

| 来源 | 当前键/变量（源码提取） | 迁移建议 |
|---|---|---|
| [ModelContextProtocol/Builders/EcosystemFiles.cs](../../tools/tiaportal-mcp/src/TiaMcp.Logic/ModelContextProtocol/Builders/EcosystemFiles.cs) | `TIA_MCP_REPOSITORY_ROOT` | TIA_MCP_BUNDLE_ROOT / --bundle-root；旧名限期别名 |
| [ModelContextProtocol/Tools/McpServer.Profile.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Profile.cs) | `TIA_MCP_PROFILE` | 名称和值 lite/full 保留；与新增 contract-profile 正交 |
| [TiaOpenness.Gui/Configuration/ClientProfiles.cs](../../tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ClientProfiles.cs) | `tia-portal`, `tia-portal-vm` | 配置 entry key 保留，仅 command/args 中产品路径更新 |
| [Cli/McpConfigInstaller.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs) | `mcpServers`, `servers`, `tia-portal` | JSON/TOML 根及 server key 保留，更新 command/args |
| [HostOptions.cs](../../tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/HostOptions.cs) | `--tia-portal-location`, `--worker-exe` | 名称保留；worker-exe 若显式设置则按对应产物迁移 |
| [ModelContextProtocol/Tools/EcosystemTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/EcosystemTools.cs) | `TIA_MCP_PLC_TOOLS_PYTHON` | 名称保留；默认 Python 环境改到 LocalAppData/TiaMcp/ecosystem-python |

Studio TiaOpenness.exe、Bridge 与 TiaMcp.PlcWorker.<key>.exe 不属于上述三个同名程序，建议保持；根目录 TiaMcpConfigurator.exe 为已存在的兼容启动器，选择一周期后移除（替代 runtime/studio/TiaOpenness.exe）。HTTP /mcp 与鉴权键不因 EXE 改名改变。

</details>

<details>
<summary>E. 兼容回退 / 垫片 → 替代（源码定位生成）</summary>

| 当前位置 / 定位词 | 拟删除/收紧的行为 | 替代 |
|---|---|---|
| [ModelContextProtocol/Builders/EcosystemFiles.cs](../../tools/tiaportal-mcp/src/TiaMcp.Logic/ModelContextProtocol/Builders/EcosystemFiles.cs):12 `RepositoryRoot` | R1 任意祖先找桥接脚本/旧 root 覆盖 | BundleLayout + 新 bundle-root；缺资源即失败，旧变量只做别名 |
| [ModelContextProtocol/Tools/McpServer.Maintenance.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/McpServer.Maintenance.cs):46 `FindInstallRoot` | R2 最多 4 层 delivery 探测 | 只接受正式安装锚点与 delivery |
| [Cli/SpecLoader.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/SpecLoader.cs):48 `FindBundleRoot` | R3 最多 12 层 templates/tools 探测 | 显式包根或已知锚点；__BUNDLE__ 未解报参数错误 |
| [Siemens/EngineRouter.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/EngineRouter.cs):35 `FindSiblingExe` | R7 bin/bin-v20/v数字相对回退 | TiaVersionCatalog + 明确布局/新 EXE 名；目标缺失报错 |
| [Cli/McpConfigInstaller.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/McpConfigInstaller.cs):88 `FindSiblingExe` | 跨版本找不到引擎时回退自身 EXE | 禁止给目标版本写错引擎，返回缺失版本路径 |
| [TiaOpenness.Gui/ConfigurationPage.cs](../../tools/tia-openness-studio/src/TiaOpenness.Gui/ConfigurationPage.cs):27 `FindBundleRoot` | R11 祖先包标记探测 | 显式根或 BundleLayout |
| [TiaOpenness.Gui/Configuration/ConfigCore.cs](../../tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigCore.cs):79 `TiaMcpServer.exe` | R11 根标记缺失仍使用原候选 | 严格根校验 + 版本到新输出名映射 |
| [TiaOpenness.Gui/Configuration/UpdateCheck.cs](../../tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/UpdateCheck.cs):45 `FindResource` | R11 解析失败仍拼接传入根路径 | 严格资源解析；保留 worktree 禁止安装更新 |
| [TiaOpenness.Client/BridgeClient.cs](../../tools/tia-openness-studio/src/TiaOpenness.Client/BridgeClient.cs):156 `BundleLayout` | R13 相对开发 Debug/Release 回退 | 保留正式安装/开发锚点与显式 bridgeExePath；删除任意布局回退 |
| [TiaOpenness.Core/Abstractions/SessionFactoryLoader.cs](../../tools/tia-openness-studio/src/TiaOpenness.Core/Abstractions/SessionFactoryLoader.cs):23 `TiaOpenness.Openness` | R14 旧 Studio adapter 加载路径 | 仅 G3/J 真机通过后移除；替代 TiaMcp.Adapter.<key>，非到期强删 |
| [TiaOpenness.Launcher/Launcher.cs](../../tools/tia-openness-studio/src/TiaOpenness.Launcher/Launcher.cs):17 `TiaOpenness.exe` | R12 TiaMcpConfigurator 兼容启动器 | 正式 Studio EXE；按问题 8 决定删除时点 |
| [Program.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Program.cs):26 `DiagLogPathLocal` | 安装目录 startup.log 与 TEMP 共用日志 | LocalAppData/TiaMcp/logs/<releaseKey>，明确日志路径 |
| [TiaOpenness.Gui/App.xaml.cs](../../tools/tia-openness-studio/src/TiaOpenness.Gui/App.xaml.cs):16 `.crash.log` | Studio 安装目录崩溃日志 | LocalAppData/TiaMcp/logs/studio |
| [ModelContextProtocol/Tools/EcosystemTools.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/EcosystemTools.cs):44 `ecosystem-python` | 包根下的私有 Python 环境默认值 | 显式 TIA_MCP_PLC_TOOLS_PYTHON 或 LocalAppData 环境 |
| [Cli/ReportBuilders.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/ReportBuilders.cs):53 `GetWorkspaceRoot` | TMP_EXPORT/tools 向上探测与 cwd 兜底 | 新增显式 --workspace-root/fixture 根；缺输入报错 |
| [Cli/HmiTemplateBuilder.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Cli/HmiTemplateBuilder.cs):63 `TIA_MCP_AI_PACK` | 私有 HMI 模板路径默认值 | 显式模板输入，不能把私人 fixture 当随包资源 |
| [ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs](../../tools/tiaportal-mcp/src/TiaMcp.Logic/ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs):55 `TMP_EXPORT` | suite 私有夹具探测 | 显式 fixture 根；workspaceRoot 既有 MCP 必填不再猜 |
| [ModelContextProtocol/Tools/OnlineToolPolicy.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools/OnlineToolPolicy.cs):33 `WithAutoOffline` | 按错误字串自动下线后再次调用 | OFFLINE_REQUIRED；显式下线后由用户发起新操作；L5 |
| [Siemens/Services/OnlineDownloadService.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Services/OnlineDownloadService.cs):115 `ApplyConfiguration` | 吞配置失败/换候选/原配置回退 | 显式路线，失败/未知结果停止；L5 |

正常部署的 release-key.txt、--worker-exe、已知开发锚点及相邻 adapters/v<key> 不属于无条件删除项。所有候选逐项评审；privacy、可选 API 探测和未知结果保护不能误删。

</details>

<!-- phase6-generated:end -->

## 清点与复跑

本任务不新增 scripts 文件。下面是实际用于生成上表的一次性命令正文；只读取当前 Git 源码/契约并替换本页的 generated 区块，没有编译、启动宿主或网络访问。提案映射是评审输入，事实项由代码读取且带断言；不同 master 的统计必须重新评审。在仓库根复跑：

```powershell
@'
from pathlib import Path
s = Path('docs/development/phase6-review.md').read_text(encoding='utf-8')
code = s.split('```python\n', 1)[1].split('\n```', 1)[0]
exec(compile(code, '<phase6-review generator>', 'exec'))
'@ | python -B -
```

<details>
<summary>一次性生成器正文（事实读取、提案映射与一致性断言）</summary>

```python
import collections, json, pathlib, re, subprocess, sys, xml.etree.ElementTree as ET
sys.dont_write_bytecode = True
root = pathlib.Path.cwd()
read = lambda p: (root / p).read_text(encoding="utf-8-sig")
files = subprocess.check_output(["git", "ls-files"], text=True).splitlines()
keys = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"]
snap = {k: json.loads(read(f"manifest/contracts/baseline/{k}.json")) for k in keys}
tools = {k: {t["name"]: t for t in d["tools"]} for k, d in snap.items()}
names = sorted(set().union(*(set(t) for t in tools.values())))
catalog = dict(re.findall(r"^\| " + chr(96) + r"([^" + chr(96) + r"]+)" + chr(96) + r" \| ([^|]+) \|$", read("docs/reference/version-tool-catalog.md"), re.M))
assert set(catalog) == set(names)
for n in names:
    assert catalog[n].strip().split(", ") == [k for k in keys if n in tools[k]], n
E = "tools/tiaportal-mcp/src/TiaMcpServer/"
L = "tools/tiaportal-mcp/src/TiaMcp.Logic/"
F = "tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/"
S = "tools/tia-openness-studio/src/"
sys.path.insert(0, str(root / "scripts/checks"))
import engine_sources
engine = engine_sources.EngineSources(root)
source_tools = {}
for p, text in engine.sources.items():
    for m in re.finditer(r'\[McpServerTool\(Name\s*=\s*"([^"]+)"', text):
        decl = re.search(r"^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>?,\[\] .]+?\s+(\w+)\s*\(", text[m.end():], re.M)
        assert decl and m[1] not in source_tools
        source_tools[m[1]] = (p.relative_to(root).as_posix(), decl[1])
assert set(source_tools) == set(tools["21"])
policy = read(E + "Siemens/ToolVersionPolicy.cs")
only21 = set(re.findall(r'\["([^"]+)"\]\s*=', policy.split("internal static string ToolProblem")[0]))
assert set(tools["20"]) == set(source_tools) - only21
profile = read(E + "ModelContextProtocol/Tools/McpServer.Profile.cs")
profile = profile.split("private static readonly HashSet<string> LiteToolNames", 1)[1].split("};", 1)[0]
lite = set(re.findall(r'"(\w+)"', profile))
assert all(lite == set(snap[k]["liteTools"]) for k in ["20", "21"])
foundation = "\n".join(read(f) for f in files if f.startswith(F) and f.endswith(".cs"))
definitions = {}
for line in read(F+"FoundationTools.cs").splitlines():
    m=re.match(r'\s*new\("([^"]+)"',line)
    if m:
        response=re.search(r',\s*"([^"]+)"\),?\s*$',line)
        definitions[m[1]]=response[1] if response else ""
helpers=set()
for f in files:
    if f.startswith(F) and f.endswith(".cs") and not f.endswith("FoundationTools.cs"):
        source=read(f)
        helpers.update(re.findall(r'new (?:Offline\w+Tool|PassiveDiagnosticTool)\("([^"]+)"',source))
        helpers.update(re.findall(r'\bName\s*=\s*"([^"]+)"',source))
helpers &= set(names)
for k in keys[:6]:
    major = 14 if k=="14sp1" else int(k.split(".")[0])
    accepted={n for n,r in definitions.items()
        if not (r in ("HardwareCatalog","DeviceAdd") and major<19)
        and not (r=="SpecialExport" and major<16)
        and not (r in ("DocumentExport","BatchDocumentExport","DocumentImport","BatchDocumentImport") and major<20)
        and not (n=="GetPlcWatchTables" and k=="14sp1")}
    assert accepted | helpers == set(tools[k]), (k, (accepted | helpers) ^ set(tools[k]))
for k in keys[:6]:
    for n, t in tools[k].items():
        assert '"' + n + '"' in foundation, n
        for p in t["inputSchema"]["properties"]:
            assert '"' + p + '"' in foundation, (n, p)
calls = json.loads(read("reference/tool-examples/calls.json"))["profiles"]
for k in keys:
    assert set(tools[k]) <= set(calls[snap[k]["profile"]]), k
typed = {}
for n in names:
    for k in keys:
        if n not in tools[k]: continue
        for p, schema in tools[k][n]["inputSchema"]["properties"].items():
            if p.endswith("Json") or (n == "PlcBuildAndImport" and p == "json"):
                typed.setdefault(n, {}).setdefault(p, set()).add(k)
for n, (_, method) in source_tools.items():
    member = engine.member(method, tool=True)
    tokens, _ = engine_sources.lexer.Lexer(member).scan()
    pairs = engine_sources.lexer.matching_pairs(tokens)
    op = next(i for i,t in enumerate(tokens) if t.value == "(")
    sig = member[:tokens[pairs[op]].end]
    actual = set(re.findall(r"\bstring\??\s+(\w*Json)\b", sig))
    expected = {p for p,v in tools["21"][n]["inputSchema"]["properties"].items() if p.endswith("Json") and "string" in str(v.get("type"))}
    assert actual == expected, (n, actual, expected)
out = []
tick = lambda s: chr(96) + str(s) + chr(96)
def table(headers, rows):
    out.extend(["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"])
    out.extend("| " + " | ".join(str(x).replace("|", r"\|").replace("\n", " ") for x in row) + " |" for row in rows)
    out.append("")
def availability(n):
    ks = [k for k in keys if n in tools[k]]
    return "全" if ks == keys else "20, 21" if ks == keys[-2:] else ", ".join(ks)
def link(p, label=None):
    assert p in files or (root / p).is_file(), p
    return "[" + (label or p.removeprefix(E).removeprefix(S).removeprefix(L).removeprefix(F)) + "](../../" + p + ")"
def section(title):
    out.extend(["<details>", "<summary>" + title + "</summary>", ""])
def end():
    out.extend(["</details>", ""])
out.append("### 基线与计数\n")
rows = []
for k in keys:
    pairs = [(t["name"],p,v) for t in tools[k].values() for p,v in t["inputSchema"]["properties"].items() if p.endswith("Json")]
    strings = [(n,p) for n,p,v in pairs if "string" in str(v.get("type"))]
    rows.append([k, len(tools[k]), len(snap[k].get("liteTools", [])) or "不设 lite", len(strings), len(set(n for n,p in strings))])
table(["发布键","广告工具","lite","string …Json 参数","涉及工具"], rows)
out.append(f"八版名称并集 {len(names)}；V21 后缀 Json 输入 {sum(len([p for p in t['inputSchema']['properties'] if p.endswith('Json')]) for t in tools['21'].values())} 个，其中 CallTool/PreflightToolCall.argumentsJson 已为 JsonElement?；另有 PlcBuildAndImport.json。不能把它们统称为 string 参数。源码工具名、V21 字符串参数、V20 差集、lite、八版目录及示例覆盖断言均通过。\n")
# Proposal entries: current keys are asserted, current signatures come from snapshots.
renames = {
 "AddDeviceWithFallback": ("CreateHardwareDevice", "精确 TypeIdentifier + preview/confirm/计划与工程身份；D1"),
 "CompileSoftware": ("CompileSoftware", "保留 softwarePath/password；统一 diagnostics 与 preview/confirm；D1"),
 "CompileAndDiagnosePlc": ("CompileSoftware", "targetKind=plc；仅在诊断/离线/Safety 等价后合并"),
 "CompileAndDiagnoseHmi": ("CompileSoftware", "targetKind=hmi；仅 E，祖先软件查找不得丢失"),
 "Connect": ("Connect", "统一 processId/进程与工程身份；启动显式选择；D1"),
 "ConnectToProject": ("Connect", "保留 processId/processStartUtc/projectPath 校验"),
 "ConnectIsolated": ("ConnectIsolated", "隔离生命周期保留独立入口"),
 "OpenProject": ("OpenProject", "upgrade=reject、reuseOpen=false；明确确认与工程身份；D1"),
 "SaveProject": ("SaveProject", "显式 preview/confirm 与工程身份；LocalSession 本地保存"),
 "CloseProject": ("CloseProject", "拒绝借用/已修改对象隐式关闭；D1"),
 "ImportBlock": ("ImportBlock", "overwrite=false、versionPolicy=exact、preview/confirm；D1"),
 "ImportType": ("ImportType", "同导入策略；保留目标组与原生类型限制"),
 "ImportPlcTagTable": ("ImportPlcTagTable", "同导入策略；保留 folderPath"),
 "ExportBlock": ("ExportBlock", "overwrite=false、preview/confirm，暂存发布；D1"),
 "ExportType": ("ExportType", "同导出策略；保留 preservePath"),
 "ExportPlcTagTable": ("ExportPlcTagTable", "同导出策略；不合并 XML 与 SD"),
 "ImportBlocksFromDirectory": ("ImportBlocksFromDirectory", "onError=stop，保留顺序/计划/数量；D1"),
 "ImportPlcProgramFromDirectory": ("ImportPlcProgramFromDirectory", "同批次策略；块/类型/表目的地仍独立"),
 "ExportBlocks": ("ExportBlocks", "同导出策略；保留 inventoryHash/逐项结果"),
 "ExportTypes": ("ExportTypes", "同导出策略；保留 inventoryHash/逐项结果"),
 "ExportAsDocuments": ("ExportBlockDocuments", "参数保留，D1 显式发布策略；不放开普通块/GlobalDB 限制"),
 "ExportBlocksAsDocuments": ("ExportBlockDocumentsBatch", "批次保留，不改为循环调用单项工具"),
 "ImportFromDocuments": ("ImportBlockDocuments", "参数保留，overwrite=false；不支持覆盖的版本拒绝 true"),
 "ImportBlocksFromDocuments": ("ImportBlockDocumentsBatch", "保留逐项结果/停止与未知结果"),
 "PlanPlcExternalSourceImport": ("PlanPlcExternalSourceImport", "保持只预览；不别名到可执行导入"),
 "ImportPlcExternalSource": ("ImportPlcExternalSource", "精确源名/文件；preview/confirm 与计划；D1"),
 "DeletePlcExternalSource": ("DeletePlcExternalSource", "精确路径，核实删除；不靠扩展名或幂等吞错"),
 "GenerateBlocksFromExternalSource": ("GenerateBlocksFromExternalSource", "保留 14sp1 observation 与其他版原生结果区别"),
}
for n in source_tools:
    if n.endswith("DesignJson"):
        renames[n] = (n[:-4], "Json 表示从工具名去掉；设计对象直接返回 data")
section("A. 工具改名 / 合并 / 行为迁移（当前参数自动读取）")
rows = []
for n,(target,change) in sorted(renames.items()):
    assert n in names
    signatures = collections.defaultdict(list)
    for k in keys:
        if n in tools[k]:
            signatures[", ".join(tools[k][n]["inputSchema"]["properties"]) or "无"].append(k)
    current = "<br>".join("/".join(ks) + ": " + tick(ps) for ps,ks in signatures.items())
    rows.append([tick(n),tick(target),availability(n),current,change])
table(["当前名称","建议名称/合并目标","当前发布键","当前参数名","拟议变化"],rows)
out.append("此表以外的工具名保持；输入变化另见 B，统一响应影响全部广告工具。合并后可用版本为原入口的并集，具体 action/目标能力保留原门禁。大小写风格（例如 SiVArc/Sivarc）不单独批量改名。\n")
end()
# Closed family map: an unclassified parameter fails instead of falling into a catch-all.
family_groups = {
 "P":"assignmentItemPath branch collectionPath destinationDevicePath destinationItemPath devicePath durationPath groupPath itemPath localInterfaceItemPath modifiedDevicePath modifiedItemPath participantDevicePath participantItemPath partnerDevicePath partnerInterfaceItemPath partnerItemPath tagPath targetDevicePath targetItemPath",
 "S":"additionalHmiDeviceNames attributeNames blockPaths chartNames cultures deviceNames expectedNames expectedTagNames extensions fields files itemNames items markers names nodeIds objectPaths permissions plcSoftwarePaths plcSymbols scopeSoftwarePaths subjectAlternativeNames systemNames tagPaths tags textListNames vars",
 "N":"numbers",
 "R":"objectPath",
 "M":"attributes changes customAttributes entry properties scriptProperties settings",
 "L":"accessLevels comments promptAnswers texts",
 "V":"value",
 "C":"arguments calls operations",
 "W":"values writes",
 "B":"fbBlock fcBlock flgNet globalDb ladFcBlock structuredText tagTable udt",
 "H":"design layout package spec table theme",
 "D":"artifacts plan rows scenario",
 "X":"afterPages beforePages deviceSelection harmonizeOptions itemDetails mappingEntries options partner references request revisionDetails rules scope selection target templateIntent",
}
families = {p+"Json":f for f,ps in family_groups.items() for p in ps.split()}
def family(n,p):
    if p == "json": return "B"
    if (n,p) == ("RunPlcCompanionTool","argumentsJson"): return "S"
    if (n,p) == ("PatchPlcBlockDocument","changesJson"): return "D"
    assert p in families, (n,p)
    return families[p]
section("B. 全部 JSON 输入逐工具清单（计数为每个签名的 string …Json 数，不跨版本相加）")
rows = []
for n,ps in sorted(typed.items()):
    k = next(k for k in reversed(keys) if n in tools[k])
    string_count = sum(p.endswith("Json") and "string" in str(tools[k][n]["inputSchema"]["properties"][p].get("type")) for p in ps)
    changes = "; ".join(tick(p)+" → "+tick("spec" if p=="json" else p[:-4])+" ("+family(n,p)+")" for p in sorted(ps))
    rows.append([tick(n),availability(n),string_count,changes])
table(["当前工具","发布键","string 数","旧参数 → 新参数（族）"],rows)
totals = collections.Counter(family(n,p) for n,ps in typed.items() for p in ps)
out.append("按 (工具名,参数名) 去重的族计数：" + "；".join(f"{f}={n}" for f,n in sorted(totals.items())) + f"；共 {sum(totals.values())} 项 / {len(typed)} 个工具。Foundation 的同名参数具有更窄的 parser/schema，不因共用 DTO 放宽。详见 "+link("reference/tool-examples/calls.json","现有调用与操作示例")+"。\n")
end()
# Count annotation sites, not tools or all response construction statements.
marks = collections.defaultdict(collections.Counter)
sites = collections.defaultdict(list)
for p,text in engine.sources.items():
    rel = p.relative_to(root).as_posix()
    for m in re.finditer(r"// envelope: (legacy-[\w-]+)",text):
        if "/Cli/" in rel: fam = "CLI"
        elif rel.endswith("McpServer.ToolBridge.cs"): fam = "F4"
        elif "/Siemens/Services/" in rel or rel.endswith("PlcSimAdvancedTools.cs"): fam = "F3"
        else: fam = "F2"
        marks[fam][m[1]] += 1
        sites[m[1]].append(rel)
section("C. 响应/错误族 → V4 与 legacy 标记")
shapes = [
 ("F1","VersionPolicyTool：isError 文本 + preflight","error.code/details；准入拒绝的 outcome"),
 ("F2","POCO + Meta；McpException 或 success=false","data + ok/error；消除直接 camelCase/桥接 PascalCase 差异"),
 ("F3","执行器/领域服务：operationSuccess/status/error，含旧 meta","逐项 data + outcome/完整性；未知 verdict 不转为 true"),
 ("F4","桥接 Message 内序列化 JSON 或 failed 文本","直接透传同一信封；禁止二次字符串 JSON"),
 ("F5","导出句柄 ok=true；InvalidParams 异常","data.export；INVALID_ARGUMENT/ALREADY_EXISTS 等码"),
 ("F6","旧 Portal 文本失败且无 meta","在调用边界补 outcome/error；不能靠 Message 判成功"),
 ("F7","Foundation PascalCase DTO/裸数组、V17 envelopes、McpException 或 isError 文本","保留原 evidence/Executed/RequiresSessionReset，转换为同一信封"),
 ("CLI","报告 roundtrip/ok/后写判定","报告适配 V4；CLI 成功/失败退出码另有黄金样本"),
]
table(["族","当前形状/错误","建议目标","标记站点数（非工具数）","variant 数量"], [
 [fam,old,new,sum(marks[fam].values()),"; ".join(tick(v)+":"+str(c) for v,c in sorted(marks[fam].items())) or "0"] for fam,old,new in shapes])
out.append(f"共 {sum(sum(v.values()) for v in marks.values())} 个实际注释站点、{len(sites)} 个 variant。F2/F3 按注释所在工具边界/服务或执行器归属计数，混合工具不推断唯一运行时族；F6 无标记不等于不存在无 meta 失败。未标注的手写形状仍由 Inventory-ResponseEnvelopes.py 管理。\n")
table(["variant","当前源码（同文件可含多个站点）"],[[tick(v),"<br>".join(link(p) for p in sorted(set(ps)))] for v,ps in sorted(sites.items())])
end()
section("D. 可执行文件 / 程序集 / 路径与配置")
assemblies = []
for p,target,ks,oldpath in [
 (F+"TiaMcpServer.LegacyHost.csproj","TiaMcp.FoundationHost","14sp1–19","runtime/v<key>/"),
 (E+"TiaMcpServer.V20.csproj","TiaMcp.Engine.V20","20","runtime/v20/"),
 (E+"TiaMcpServer.V21.csproj","TiaMcp.Engine.V21","21","runtime/v21/")]:
    tree=ET.fromstring(read(p)); old=tree.findtext(".//AssemblyName"); tf=tree.findtext(".//TargetFramework")
    assert old == "TiaMcpServer"
    assemblies.append([link(p),ks,tick(old),tick(target),tick(oldpath+old+".exe")+" → "+tick(oldpath+target+".exe"),tf])
table(["项目","版本","当前 AssemblyName","建议 AssemblyName","安装路径迁移","目标框架保持"],assemblies)
out.append("同基名的 .dll/.exe.config/.deps.json/.runtimeconfig.json（以实际构建输出为准）同步改名；旧 EXE 仅作启动 shim。开发输出保留 bin-v20/Release/net48、bin/Release/net48 与 Foundation bin/Release/net8.0 的目录，仅变基名。程序集友元、反射加载、织入目标、worker 启动与构建/打包/更新脚本均需按新名生成，不能仅重命名磁盘文件。\n")
config_specs = [
 (L+"ModelContextProtocol/Builders/EcosystemFiles.cs",r'"(TIA_MCP_REPOSITORY_ROOT)"',"TIA_MCP_BUNDLE_ROOT / --bundle-root；旧名限期别名"),
 (E+"ModelContextProtocol/Tools/McpServer.Profile.cs",r"(TIA_MCP_PROFILE)","名称和值 lite/full 保留；与新增 contract-profile 正交"),
 (S+"TiaOpenness.Gui/Configuration/ClientProfiles.cs",r'"(tia-portal(?:-vm)?)"',"配置 entry key 保留，仅 command/args 中产品路径更新"),
 (E+"Cli/McpConfigInstaller.cs",r'"(mcpServers|servers|tia-portal)"',"JSON/TOML 根及 server key 保留，更新 command/args"),
 (F+"HostOptions.cs",r'"(--worker-exe|--tia-release|--tia-version|--tia-portal-location)"',"名称保留；worker-exe 若显式设置则按对应产物迁移"),
 (E+"ModelContextProtocol/Tools/EcosystemTools.cs",r'"(TIA_MCP_PLC_TOOLS_PYTHON)"',"名称保留；默认 Python 环境改到 LocalAppData/TiaMcp/ecosystem-python"),
]
table(["来源","当前键/变量（源码提取）","迁移建议"],[[link(p),", ".join(tick(v) for v in sorted(set(re.findall(pattern,read(p))))),target] for p,pattern,target in config_specs])
out.append("Studio TiaOpenness.exe、Bridge 与 TiaMcp.PlcWorker.<key>.exe 不属于上述三个同名程序，建议保持；根目录 TiaMcpConfigurator.exe 为已存在的兼容启动器，选择一周期后移除（替代 runtime/studio/TiaOpenness.exe）。HTTP /mcp 与鉴权键不因 EXE 改名改变。\n")
end()
section("E. 兼容回退 / 垫片 → 替代（源码定位生成）")
shims = [
 (L+"ModelContextProtocol/Builders/EcosystemFiles.cs","RepositoryRoot","R1 任意祖先找桥接脚本/旧 root 覆盖","BundleLayout + 新 bundle-root；缺资源即失败，旧变量只做别名"),
 (E+"ModelContextProtocol/Tools/McpServer.Maintenance.cs","FindInstallRoot","R2 最多 4 层 delivery 探测","只接受正式安装锚点与 delivery"),
 (E+"Cli/SpecLoader.cs","FindBundleRoot","R3 最多 12 层 templates/tools 探测","显式包根或已知锚点；__BUNDLE__ 未解报参数错误"),
 (E+"Siemens/EngineRouter.cs","FindSiblingExe","R7 bin/bin-v20/v数字相对回退","TiaVersionCatalog + 明确布局/新 EXE 名；目标缺失报错"),
 (E+"Cli/McpConfigInstaller.cs","FindSiblingExe","跨版本找不到引擎时回退自身 EXE","禁止给目标版本写错引擎，返回缺失版本路径"),
 (S+"TiaOpenness.Gui/ConfigurationPage.cs","FindBundleRoot","R11 祖先包标记探测","显式根或 BundleLayout"),
 (S+"TiaOpenness.Gui/Configuration/ConfigCore.cs","TiaMcpServer.exe","R11 根标记缺失仍使用原候选","严格根校验 + 版本到新输出名映射"),
 (S+"TiaOpenness.Gui/Configuration/UpdateCheck.cs","FindResource","R11 解析失败仍拼接传入根路径","严格资源解析；保留 worktree 禁止安装更新"),
 (S+"TiaOpenness.Client/BridgeClient.cs","BundleLayout","R13 相对开发 Debug/Release 回退","保留正式安装/开发锚点与显式 bridgeExePath；删除任意布局回退"),
 (S+"TiaOpenness.Core/Abstractions/SessionFactoryLoader.cs","TiaOpenness.Openness","R14 旧 Studio adapter 加载路径","仅 G3/J 真机通过后移除；替代 TiaMcp.Adapter.<key>，非到期强删"),
 (S+"TiaOpenness.Launcher/Launcher.cs","TiaOpenness.exe","R12 TiaMcpConfigurator 兼容启动器","正式 Studio EXE；按问题 8 决定删除时点"),
 (E+"Program.cs","DiagLogPathLocal","安装目录 startup.log 与 TEMP 共用日志","LocalAppData/TiaMcp/logs/<releaseKey>，明确日志路径"),
 (S+"TiaOpenness.Gui/App.xaml.cs",".crash.log","Studio 安装目录崩溃日志","LocalAppData/TiaMcp/logs/studio"),
 (E+"ModelContextProtocol/Tools/EcosystemTools.cs","ecosystem-python","包根下的私有 Python 环境默认值","显式 TIA_MCP_PLC_TOOLS_PYTHON 或 LocalAppData 环境"),
 (E+"Cli/ReportBuilders.cs","GetWorkspaceRoot","TMP_EXPORT/tools 向上探测与 cwd 兜底","新增显式 --workspace-root/fixture 根；缺输入报错"),
 (E+"Cli/HmiTemplateBuilder.cs","TIA_MCP_AI_PACK","私有 HMI 模板路径默认值","显式模板输入，不能把私人 fixture 当随包资源"),
 (L+"ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs","TMP_EXPORT","suite 私有夹具探测","显式 fixture 根；workspaceRoot 既有 MCP 必填不再猜"),
 (E+"ModelContextProtocol/Tools/OnlineToolPolicy.cs","WithAutoOffline","按错误字串自动下线后再次调用","OFFLINE_REQUIRED；显式下线后由用户发起新操作；L5"),
 (E+"Siemens/Services/OnlineDownloadService.cs","ApplyConfiguration","吞配置失败/换候选/原配置回退","显式路线，失败/未知结果停止；L5"),
]
rows=[]
for p,needle,old,new in shims:
    text=read(p); assert needle in text,(p,needle)
    line=next(i for i,l in enumerate(text.splitlines(),1) if needle in l)
    rows.append([link(p)+":"+str(line)+" "+tick(needle),old,new])
table(["当前位置 / 定位词","拟删除/收紧的行为","替代"],rows)
out.append("正常部署的 release-key.txt、--worker-exe、已知开发锚点及相邻 adapters/v<key> 不属于无条件删除项。所有候选逐项评审；privacy、可选 API 探测和未知结果保护不能误删。\n")
end()
doc = root / "docs/development/phase6-review.md"
text=doc.read_text(encoding="utf-8")
begin,endmark="<!-- phase6-generated:start -->","<!-- phase6-generated:end -->"
a=text.index(begin)+len(begin); b=text.index(endmark,a)
text=text[:a]+"\n\n"+"\n".join(out)+"\n"+text[b:]
doc.write_text(text,encoding="utf-8",newline="\n")
print(f"Generated: {len(names)} names, {len(typed)} JSON-input tools, {sum(totals.values())} input occurrences; source/catalog assertions passed.")
```

</details>

补充只读检索使用 `git ls-files` 加 Python UTF-8 逐行匹配 `4\.0|until 4|through 4|阶段\s*6|phase.?6|envelope: legacy-`，覆盖产品源码、脚本与开发文档，排除生成目录/第三方和历史发布记录中的产品版本号。结果已纳入 D-G7-2/3/6/8、D-P5-3/5、响应族、C6 原生回退与 D1；未发现另一个未列的明确“到 4.0”承诺。SDK `14.0.1.0`、NuGet 版本及历史 CHANGELOG 不作候选；第三方授权、参考数据和现有安全限制不因清理删除。

后移 master 的核对命令为 `git log --oneline HEAD..master`、`git diff --name-only HEAD master`，以及 Python 对上述生成器的 source_tools、shims、config_specs、sites、Foundation 源码、八版 baseline、版本策略、目录和 calls 取路径并集，逐项将 `git show 3e082b6e86c939559ddbf3ef95e38df265c1800f:<path>` 与 worktree 的 UTF-8/LF 内容断言相等；三个 csproj 只比较上述两个 XML 字段，新增引擎文件断言无注册属性/legacy 标记。均通过，不变基、不复制并行源码，也不把新 master 的验证结果当成本 worktree 的运行结果。

文档门禁：`python scripts/checks/Check-Repository.py --no-binaries`（180 份 Markdown，0 问题）、`pwsh -NoProfile -File scripts/checks/Validate-Bundle.ps1 -Strict -NoBinaries -SkipSourceHashes`（通过）、`python scripts/checks/Check-DeadToolReferences.py`（488 个注册工具、398 个扫描文件，通过）。本次仅文档，无 C# 构建/运行测试或 L5；离线清点不替代实施后的原生验收。
