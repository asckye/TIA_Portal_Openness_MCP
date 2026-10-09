# 阶段 6：4.0 契约规范与实施计划

[重构计划](refactor-plan.md) · [当前响应族](response-and-errors.md) · [D1 行为边界](adapter-merge.md) · [当前布局](runtime-layout.md) · [真机台账](../reference/real-machine-ledger.md)

**状态：4.0 目标规范；P6-02 公共类型及 D334 源码目录整理已完成，工具接线与产品迁移待实施。** 2026-10-03 决定硬切，4.0 只提供新名称、类型输入、V4 信封和新产品入口。需要 3.x 的部署固定使用最后一个 3.x 包。本文中的调用为目标示例，不能向当前运行程序发送。第 I 步 P4-I1、I2、I3、I4a、I4b 已全部合并，`docs/development/evidence/p4-i*-native-evidence.json` 已再生成且静态 `accepted: true`（附表 I）；`src/Shared/TiaSharedAdapterPaths.props` 仍默认 `false`，这五项及 Studio 共享路径的原生验收仍为 **NOT RUN**，G3/J 与发布门槛不变。P6-01 仅冻结文档和生成器事实，不修改产品、测试、构建或 CI，不进行原生调用。

八版以发布键 `14sp1, 15.1, 16, 17, 18, 19, 20, 21` 为准。附表的输入是契约基线、当前源码、版本策略、版本目录和示例库；生成器交叉检查后输出计数，不把广告能力当作真机验收。P6-01 已按合并后的 `src/Engine`、`src/FoundationHost`、`src/Logic`、`src/Adapters`、`src/Shared`、`src/Studio` 及 `tests`、`build-tools`、`plugin` 路径重新生成；D334 只完成源码目录迁移，产品入口与运行目录仍由 P6-36～39 实施。

## 1. 命名与合并规则

工具名为 PascalCase 的“动词 + 领域对象 + 必要限定”，参数和 DTO 字段为 camelCase。缩写统一为 `Plc/Hmi/OpcUa/Sivarc/Gsd/Cpu/Db/Udt/Cfc/Dcc` 等单词形式，版本限定仅在能力确实绑定该版本时保留。名字不暴露 `Json/Xml/Xlsx` 序列化表示；`SimaticMl/Aml/Scl/FlgNet` 指独立领域语言或交换标准，保留以避免把不同能力混同。格式仍在参数 schema、结果 mediaType 和描述中明确。

统一动词表以生成器 `VERBS` 为闭集：`Get` 读取一个值或复合快照，`List` 枚举对象；`Describe` 返回结构解释，`Find/Search/Scan/Probe` 分别用于工具发现、目录查询、环境扫描和能力探测。`Create` 新建、`Ensure` 确保状态、`Set` 修改属性、`Manage` 为已有 action 判别的多操作入口；`Build` 离线构造、`Generate` 从工程生成、`Compile` 原生编译。其余领域动作（Import/Export/Connect 等）各用一个拼写。统一 `Read→Get/List`、`Compose→Build`、`Update→Set`、`Sync→Synchronize`、`Preflight→Preview`、`Add→Create`，每个例外在生成器 `SPECIAL_NAMES` 中列明。

单项使用对象单数，批量使用被操作对象复数（如 `ExportPlcBlock` / `ExportPlcBlocks`）；单个块的多份文档为 `ExportPlcBlockDocuments`，多个块的文档为 `ExportPlcBlocksDocuments`。Tree/Hierarchy/Settings/Diagnostics 是一个复合结果，不因含数组改成 List。只读批次和事务批次分别命名，不能合并。

附表 A **覆盖全部当前名称，包含不变项**。同一目标只允许一份目录定义；多个来源必须属于封闭的已证明同义合并组，参数映射和源码证明均在表中。指南/配方读取同一个示例库，可以合并到 `GetToolUsage`；编译的诊断范围、连接的进程选择/绑定/启动不同，保留独立工具。任何新合并都必须补证明并通过重复目标检查，不能通过扩大允许名单掩盖冲突。

可用性按每个发布键独立计算：只映射该版已广告的入口。合并不引入该版原本没有的 action、对象类型或 native API；Foundation 的候选 XML 输出版本、普通 PLC 限制和完整引擎的 `ToolVersionPolicy.CallProblem` 门禁继续有效。

## 2. 强类型输入

附表 B 对每个 `…Json`（包括已为 JsonElement 的参数）和 `PlcBuildAndImport.json` 给出目标类型；后者改名为 `spec`，其余只去掉后缀。`…JsonPath` 是文件路径，保持 string。以下 `?` 表示可省略；只有显式包含 null 的联合允许 null。封闭 DTO 拒绝未知字段、重复字段、错误大小写与二次 JSON 编码；动态字典的键遵从 SDK 的精确拼写，不做 camelCase 转换。缺省、空值、空数组不相互代替。顶层必填性及业务缺省值取当前逐版 inputSchema，D1 表明确改动的缺省值除外。

| 族 | 目标结构与一个完整参数例子 | 必须保留的校验 |
|---|---|---|
| P 路径段 | `string[]`；`{"devicePath":["PLC_1"]}` | 段边界、转义、大小写、深度及根路径含义；不按斜杠/逗号重新拆分 |
| S 名称/文件列表 | `string[]`；`{"extensions":[".scl"]}` | 逐操作允许值、重复项、长度、数量；harmonizeOptions 是名称数组而非对象 |
| N 数字列表 | `int32[]`；`{"numbers":[1000]}`；原 parser 接受 `{number,arrayIndex}` 处用 `ParameterRef{number:int32,arrayIndex?:int32}[]`（省略下标即原 -1） | 拒绝小数和溢出，原有参数号、下标范围与数量保持 |
| R 反射路径 | `PropertyStep{property:string,name?:string,index?:int32}[]`；`{"objectPath":[{"property":"TagTables","name":"Table"}]}` | name/index 选择规则、原有属性准入和遍历预算；不开放任意 CLR 类型 |
| M 属性字典 | `AttributeMap<Scalar>`，`Scalar=string\|number\|bool\|null`；`{"properties":{"ExternalWritable":true}}`；原本支持复合属性的 HMI 屏幕项属性写入使用 `CompositeAttributeMap=map<string,Scalar\|map<string,Scalar>>`（仅一层，如 `{"Font":{"Size":14}}`） | 按动作/版本限制属性、可写性、值类型和范围；CPU 使用 `{"settings":{"exactAttributes":{"Name":"PLC_1"}}}`，键须来自该操作支持的原生属性；不把这个形状当成所有 CPU 都支持 Name 的证明 |
| L 文本/回答 | `map<string,string>`；`{"comments":{"en-US":"Motor"}}`；accessLevels 特例为 `map<string,int32>` | culture、提示 ID/选项及访问等级枚举；凭据不回显 |
| V 原生值 | `NativeValue=Scalar\|NativeValue[]\|map<string,NativeValue>`，每个 action 收窄为其 parser 已允许的分支；`{"value":42.5}` | 数组/对象只在原操作支持时接收，原有深度和数据类型检查保留 |
| C 调用 | `ToolCall{name:string,arguments:ToolArguments}[]`；`{"operations":[{"name":"GetSessionState","arguments":{}}]}` | arguments 按目标 V4 inputSchema 校验；直接 CallTool 的 arguments 是对象；读批次、预览和事务各保留 allowlist、数量和嵌套限制 |
| W 写入 | `WriteValue{name:string,value:NativeValue}[]`；`{"writes":[{"name":"Ready","value":true}]}` | 值分支按通道收窄、数量/唯一名及在线保护；PLCSIM values 的键值对象改为同样数组 |
| B PLC 构造 | `UdtSpec{name?:string,members:Member[]}`；`{"udt":{"name":"UDT_Status","members":[{"name":"Ready","datatype":"Bool"}]}}` | 不放宽 Foundation 边界；不能将“可构造候选”解释为“可导入主机版本” |
| H HMI/AML | `UnifiedThemeSpec{name?:string,palette:map<string,string>}`；`{"theme":{"name":"Example","palette":{"Text":"0xFF202020"}}}` | Classic/Unified 是不同 DTO；颜色、尺寸、控件类型及原白名单保持 |
| D 领域计划 | `Artifact{id:string,dependencies?:string[],target?:string,priority?:int32}[]`；`{"artifacts":[{"id":"UDT_A"},{"id":"FB_A","dependencies":["UDT_A"]}]}` | DAG/重复 ID、选择器、预期旧值、顺序、路径及执行确认 |
| X 可选包/快照 | `DccPartnerSpec={block:string,pin:string}\|{chartInterface:string}`；`{"partner":{"block":"Block_1","pin":"IN1"}}` | 判别 action；互斥字段、现有可选包/版本能力及只读限制 |

B 的其余封闭 DTO：`Member{name,datatype,externalWritable?,comment?,commentZhCn?,startValue?}` 按各 builder 允许字段收窄；`GlobalDbSpec{dbName,dbNumber,staticMembers:Member[]}`；`PlcTagTableSpec{tableName,tags:[{name,dataTypeName,logicalAddress}]}`；`StructuredTextSpec{firstUid?,operations:Statement[]}`（Statement 按 op 判别，仅沿用现有赋值、条件、符号、literal、token、空白和换行语法）；`FlgNetCallSpec{callName,parameters:CallParameter[]}`，参数 `{name,section,dataType,sourceKind?,symbolPath?,constantValue?}` 按现有 global/local/constant 分支校验；`FcBlockSpec/FbBlockSpec{blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredText}`，FC 的 inputs/outputs 必填；`LadFcBlockSpec{blockName,blockNumber,inputs?,outputs?,networks:[{call:FlgNetCallSpec}]}`。嵌套 callJson 同时改为 call。`PlcArtifactSpec` 由外层 kind=udt/tagtable/globaldb/fc/fb 选择对应类型，不接受其他构造器类型或任意对象。

H 的其余类型：`ClassicScreenSpec/UnifiedScreenSpec{screen?:{name,width?,height?},items:ScreenItem[]}`，ScreenItem 按 type 区分已支持的控件，公共位置字段为 name/left/top/width/height，其余字段取对应 builder；`ClassicTagTableSpec{name,tags:[{name,dataType,length?}]}`；`ClassicPackageSpec{name,screenDesign,tagTable}`；`UnifiedLayoutSpec{columns?,cellWidth?,cellHeight?,grid?,items:LayoutItem[]}`（name/type/row/col 等）；`DeviceAmlSpec{projectName,devices:[{name,typeIdentifier,deviceItems:DeviceItemSpec[]}]}`。这些类型分别从现有 parser 定义生成 schema，不能用统一的无约束 object 代替控件联合。

D 的其他结构为 `NetworkPlan{operations:NetworkOperation[]}`（type 判别现有网络操作）、`BlockEdit[]`（action 判别现有精确 selector、field/culture、expectedValue/value）、`TemplateRow{fileName,values:map<string,string>}[]`、`PlcAliasRow{source:string[],destination:string[]}[]`、`PlcSimScenario{instance,mode,steps:({write:map<string,Scalar>}|{waitMs:int32}|{assert:map<string,Scalar>})[]}`。步骤次序原样保留，不合并写入。

X 的其他具名类型如下；封闭字段及枚举以表中对应工具的现有 parser 为输入生成，下面明确需要维持的区别：

| 类型 | V4 结构/判别 |
|---|---|
| MotionTarget | 以字段组合互斥判别：`{devicePath,itemPath}`、另加 secondItemPath 或 channelIndex 或完整 channelType/channelIoType/channelNumber、`{dbMemberPath}`、`{plcTagPath}`、`{inputBitAddress,outputBitAddress}`、`{address}`；connectOption 仅用于双设备/双地址分支；无额外 kind/mode 输入，保留 `MotionProDiagClassicHmiLogic.ParseConnectionTarget` 的推导与限制 |
| TestScope | `{kind,softwarePath?,groupPath?,name?}[]`；softwarePath 仅 plc/blocks/tags/types/units，name 仅 deviceGroup，groupPath 仅 blocks/tags/types |
| TeamcenterItemSpec / RevisionSpec | `{itemId?,itemName,revisionId?,teamcenterItemType,comment?,teamcenterFolder?,teamcenterProject?:string[]}` / `{revisionId?,comment?}` |
| SivarcReference / deviceSelection | `{kind,softwarePath?,path?,libraryName?}\|null` 的具名字典 / bool 字典；不把空引用变为自动选择 |
| LibrarySelection | `({folder:string}\|{type:string})[]`；harmonizeOptions 另为 string[] |
| DynamizationMapping | `{kind,properties:AttributeMap<Scalar>}[]`；kind 为现有 Simple/Range/Bitmask 分支 |
| GraphicSelectionPage | 当前读回页逐字段类型化，保留 pageIndex、身份、完整性和逐项值；before/after 都要求完整有序页，不接受摘要代替 |
| XPathRule / LintRules | `{id,files?,xpath,minCount?,maxCount?,valuePattern?,severity?}[]` / `{disabled?:string[],maxLineLength?:int32,maxNesting?:int32,markers?:string[]}`；`rules:[]` / `rules:{}` 表示当前默认策略，不是字符串 |
| MonitoringOptions / TemplateIntent | `{pollMs?:int32,source?:string}` / `{screenType?,targetRuntime?,preferredComponents?:string[]}` |
| OpenPipeRequest | `{message:string,params:MessageParams,clientCookie?:string}`；params 按 message 的现有协议 schema 选择，只有协议指定的扩展字段为受约束字典；发送时恢复 Message/Params 大小写 |

附表 B1 保留逐版参数约束原文及 parser 的数值边界，作为迁移验收输入。Foundation 的 [OfflineCompositionBuilders](../../src/FoundationHost/OfflineCompositionBuilders.cs)、[OfflineXmlBuilders](../../src/FoundationHost/OfflineXmlBuilders.cs) 和 [OfflineLadderBuilders](../../src/FoundationHost/OfflineLadderBuilders.cs) 限制包括 JSON 字符预算 262144、深度 16、字符串 4096、项目 1000、XML 字符预算 1048576、LAD 网络 64。类型化后仍按无缩进规范化 JSON 计算字符预算，并保留输出预算；不得借移除字符串参数去掉预算。普通 PLC/ASCII 标识符、禁 raw XML/DTD/外部解析、路径与格式、重复字段、显式 outputReleaseKey 和 V21-only 构造器的限制逐项保持。

实施时每族必须提供旧 parser 接受/拒绝样本与新 DTO 的等价测试；旧大小写/同义字段按本页规范转换后的样本才能用于等价比较。原来只在 parser 内执行的限制必须仍在业务验证层执行，并把能表达的部分放入 schema。不能仅有 schema 而绕过 native 前的校验。

## 3. V4 信封、错误与退出码

全部宿主及直接/桥接/批次/CLI 工具结果共用以下形状。下列字段均始终存在，未知或不适用时为 null；字典和数组分别用 `{}`、`[]`。属性名精确区分大小写，JSON 数字不得为 NaN/Infinity；timestamp 为 UTC RFC3339。schemaVersion 固定为整数 4。

```json
{
  "schemaVersion": 4,
  "ok": false,
  "data": null,
  "error": {"code": "PROJECT_NOT_BOUND", "message": "No project is bound.", "details": {}},
  "meta": {
    "timestamp": "2026-10-03T00:00:00Z",
    "releaseKey": "21",
    "tool": "GetPlcBlockInfo",
    "requestId": "request-1",
    "outcome": "rejected-before-operation",
    "execution": "not-started",
    "requiresSessionReset": false,
    "behaviorPolicy": "not-applicable",
    "completeness": "none",
    "paging": null,
    "warnings": []
  }
}
```

`data` 是工具 outputSchema 定义的对象或 null；原领域数组放入 data.items，原 message 信息按语义放入 data.summary 或 error.message，不保留第二个结果字符串。`error` 为 null 或 `{code,message,details}`；details 是该错误码规定的受约束对象，可携带脱敏 nativeCode/nativeMessage、目标、候选与证据，不能包含堆栈或凭据。requestId 使用调用日志关联 ID（没有上游 ID 时本地生成）；tool 是最终被执行的 V4 工具名。releaseKey 在早期无法识别时为 null，不猜版本。

| outcome（闭集） | ok / error / execution | 含义与 CLI 退出码 |
|---|---|---|
| succeeded | true / null / read-only 或 completed | 已完成，preview 仅表示计划已生成；CLI 0 |
| rejected-before-operation | false / 非 null / not-started | 准入或前置条件拒绝，尚未执行领域动作；CLI 2 |
| read-failed | false / 非 null / read-only | 只读执行失败，不能宣称空结果；CLI 3 |
| failed | false / 非 null / completed | 已执行的动作确定失败，后置状态已确认且没有成功项；CLI 3 |
| partial | false / PARTIAL_FAILURE / partial | 有确定成功及失败/未执行项，或已知部分副作用，没有未知写入；CLI 4 |
| unknown | false / OUTCOME_UNKNOWN / unknown | 任何已发出的写入无法确认结果；CLI 5；requiresSessionReset=true |

优先级 unknown > partial > failed；失败是否产生副作用由证据决定，不按异常文本猜测。`completeness=complete|partial|none|unknown` 表示观察的数据完整性；成功读取但字段受版本限制可以为 partial，必须警告。`behaviorPolicy=not-applicable|current|safe-v4` 表示该行为族实际实现，未真机验收的族为 current，不能报告 safe-v4。preview 的 execution=read-only；计划可完整但尚未执行，data.plan 明确说明。

批次 `data.items=[{index:int32,target:string|null,result:Envelope}]`，顺序与输入一致；未执行项 result 为 rejected-before-operation / NOT_EXECUTED。父结果保留成功子项、失败项、原 Foundation evidence、Executed 的逐项证据、观察来源及计划哈希。V14 SP1 外部源生成只得到 observation 时，data.observation 明示来源，data.nativeResult=null，不把观察推断为原生返回。已知副作用和不完整快照均不得在聚合时丢失。

| error.code（V4 初始闭集） | 意义 / details 的专用字段 |
|---|---|
| INVALID_ARGUMENT | 类型、枚举、重复/未知字段、双编码；parameter、allowedValues（可选） |
| LIMIT_EXCEEDED | 数量/长度/深度/字节预算；parameter、limit、actual |
| UNSUPPORTED_CAPABILITY | 该版本/action/对象不支持；releaseKey、capability、action |
| TOOL_NOT_FOUND | 目标工具不在该版完整目录；tool |
| PROJECT_NOT_BOUND | 无工程绑定；无专用字段 |
| NOT_FOUND | 对象/文件/句柄不存在；target |
| TARGET_AMBIGUOUS | 非唯一目标；target、candidates |
| IDENTITY_MISMATCH | PID/start time、工程或绑定纪元不一致；target、expected、actual（脱敏） |
| ALREADY_EXISTS | 拒绝覆盖；target |
| CONFIRMATION_REQUIRED | 执行缺少确认；reason（denied / timeout / workbench-unavailable / plan-confirmation）、planHash、requestId |
| PLAN_STALE | 计划、输入文件、目录库存或状态已变化；planHash、reason |
| PRECONDITION_FAILED | 借用、脏工程、所有权等前提；condition、target |
| OFFLINE_REQUIRED | 目标或受影响设备需离线；targets |
| AUTHENTICATION_REQUIRED | 缺少必要认证；capability（不回显凭据） |
| ACCESS_DENIED | 权限拒绝；operation、target |
| SESSION_RESET_REQUIRED | 会话已失效，拒绝新操作；reason |
| RESOURCE_UNAVAILABLE | SDK、可选服务、bundle 资源或通道缺失；resource |
| IO_FAILED | 本地文件读写失败；operation、path（脱敏） |
| NATIVE_OPERATION_FAILED | 已知原生失败；nativeCode、nativeMessage、evidence |
| CANCELLED | 确认取消且未发生未知写入；stage |
| TIMEOUT | 只读或可证明未发出动作的超时；stage |
| NOT_EXECUTED | 批次前序停止导致本项未执行；causeIndex |
| PARTIAL_FAILURE | 有已知成功和失败；succeeded、failed、notExecuted |
| OUTCOME_UNKNOWN | 写入/传输/通道中断导致结果未知；stage、evidence；禁止自动重放 |
| INTERNAL_ERROR | 非原生未分类故障；diagnosticId；堆栈仅入日志 |

`meta.warnings` 为 `{code,message,details}[]`，code 初始闭集为 `INCOMPLETE_DATA`（不完整观察）、`NATIVE_WARNING`（原生诊断）、`CANDIDATE_ONLY`（构造候选未验证导入）、`UNVERIFIED_BEHAVIOR`（current 政策）、`CLEANUP_FAILED`（清理失败）、`NATIVE_CAPABILITY_LIMIT`（SDK 能力自身限制）、`DIAGNOSTIC_WRITE_FAILED`（日志不可写）、`APPROVAL_DISABLED`（本次 MCP 写调用关闭了工作台审批，details={enabled:false}）。警告不把工程失败转为成功；cleanup 若使状态未知则仍为 unknown。

P6-44：CONFIRMATION_REQUIRED 的 reason 为上述四值的必填闭集。既有 D1 确认拒绝使用 plan-confirmation；planHash 与 requestId 始终输出，可为 null。宿主审批拒绝必须给出本次请求的非空 requestId 和 SHA-256 planHash（无计划时为规范化参数及目标身份的摘要），且 outcome=rejected-before-operation、execution=not-started。批准绑定这两个值，仅允许一次执行尝试；审批关闭不取消 D1 确认与计划校验。

分页时 `meta.paging={mode:"offset"|"cursor",offset:int32|null,limit:int32,nextOffset:int32|null,cursor:string|null,nextCursor:string|null,total:int32|null,complete:bool}`；不用分页则 null。页大小上限取原工具，offset 非负，游标限定于同一 release/session/binding/查询/快照，不得跨工程复用。complete 表示该快照已读完，next 字段为 null；不能用它代替工程结果完整性。大结果缓存另给 `data.export={id:string,mediaType:string,byteLength:int64,sha256:string,expiresUtc:string|null}`，由 ListExportHandles/GetExportContent/SaveExportContent/DeleteExportHandle/ClearExportHandles 提供完整生命周期。原来的页身份和校验信息放入 data，过期/缺失返回 NOT_FOUND，不偷偷创建新快照。

MCP `structuredContent` **就是上述 Envelope**；同时提供一个 TextContent，其 text 为同一 Envelope 的 JSON（为了文本客户端），`isError=!ok`。`CallTool` 成功分派后原样返回目标的同一个信封，meta.tool/requestId 也保持目标值；分派前失败则 meta.tool=CallTool。禁止把目标 JSON 再放入 Message/data.result 字符串。真正 JSON-RPC 协议错误仍为协议错误，不伪造成工具结果。批次只有 result 属性嵌套对象，不嵌套字符串。

CLI 的工具命令 stdout 输出一份相同 JSON，stderr 仅诊断；上述 0/2/3/4/5 为结果退出码。CLI 语法错误退出 64，无法创建工具上下文的进程级故障退出 70；这两者不得输出假成功信封。用户要求的人类报告属于 data 内文件或内容，不能改变机器退出码。错误码落地后再把冻结 MCP 文本统一为英文，并同时迁移 `TryCanonicalizeEnumArgument`、`OnlineToolPolicy.IsOnlineModeError`、Studio 错误映射、快照及 `Test-LocalStability.py` 的文本消费者。

P6-02 落地补充（以下每项固定一个原规范未定细节）：

- 公共 V4 类型位于 `src/Logic/V4`，命名空间 `TiaMcp.Logic.V4`（net48/net10.0）；`V4Json` 是唯一编解码入口，现有工具与 `ResponseMeta` 不接线，Contracts 继续不依赖 JSON 库。
- JSON 使用无 BOM UTF-8、无缩进、STJ 默认转义与 invariant culture；固定字段按本节示例/表格次序输出，闭集字符串精确匹配，拒绝缺字段、未知字段、重复字段、数值枚举和非有限数字。
- 时间使用 UTC `Z`、秒后 0–7 位小数（去尾零）；构造时转换时区，读取只接受该 UTC RFC3339 形式，未指定时区的 DateTime 拒绝序列化。
- 错误详情中的文本字段均为 `string|null`；allowedValues/candidates/targets 为 `string[]`（无值为 `[]`），limit/actual 为非负 `int64|null`，causeIndex 为非负 `int32|null`，succeeded/failed/notExecuted 为非负 int32；可选 allowedValues 也始终输出。
- PARTIAL_FAILURE 的计数必须同时包含成功项及失败/未执行项；没有可计数项目的已知部分副作用使用三个 0，副作用证据放在 data，不能据计数猜测原生状态。
- 原生 evidence 和警告 details 为 `map<string,JSON值>`，保留精确键名、嵌套对象、数组与显式 null，字典按 ordinal 键序冻结；脱敏由证据生产者负责，禁止传入堆栈和凭据。
- 信封 data 在边界冻结为 JSON 对象；领域 DTO 通过同一 V4Json 转换，data 内数组不改序，批次 index 从 0 连续递增，批次构造/读取校验 unknown 优先于 partial 并保留全部子信封。
- requestId 是非空字符串，优先保留上游日志 ID；无上游 ID 时使用本地 GUID 的 32 位小写无连字符形式；SESSION_RESET_REQUIRED 必须为拒绝且 requiresSessionReset=true，current 必须含 UNVERIFIED_BEHAVIOR。
- 分页 limit 为正 int32；offset 页仅填 offset/nextOffset，cursor 页仅填 cursor/nextCursor，未完成页必须给出前进的 next；`CursorScope={releaseKey:string,sessionId:string,bindingEpoch:int64,queryHash:string,snapshotId:string}` 供宿主核对游标作用域，不能据完整页推断完整工程结果。
- 计划沿用第 4 节的 hash 字段（错误详情中仍名为 planHash）；hash/argumentsHash/inventoryHash/文件 sha256 均为 64 位小写十六进制 SHA-256，inputHashes 为按 ordinal 路径键排序的 `map<string,string>`，inventoryHash 不适用为 null，实际规范化和摘要计算由后续领域政策实现。
- 计划 identity 固定为 `{processId:int32|null,processStartUtc:string|null,projectFile:string|null,bindingEpoch:int64|null,workspaceRoot:string|null,files:PlanFile[]}`；工程身份须完整 PID/启动时间/工程/绑定纪元，离线身份只填 workspaceRoot 与文件身份，二者互斥。
- `PlanFile={path:string,exists:bool,byteLength:int64|null,sha256:string|null}` 区分现有输入/输出与尚不存在的输出；存在时长度和哈希必填，不存在时二者为 null；`PlanOperation={tool:string,target:string|null,arguments:object}` 保留操作顺序，arguments 后续按领域 schema 收窄。
- PreviewData 只有 plan 字段，生成的预览必须是 read-only 成功且计划与信封的 tool/releaseKey 相同；MCP 映射为无 SDK 依赖的值对象，从一次序列化同时生成 structuredContent 和单个 TextContent，CLI 的 64/70 通过独立进程故障枚举映射。

## 4. D1 安全策略和验收

所有写操作统一 `mode="preview"|"apply"`（默认 preview）、`confirm=false`；apply 要求 confirm=true、expectedPlanHash 和 expectedProjectFile。过程身份使用 `{processId,processStartUtc}`，工程身份还包括绑定纪元；离线文件操作用显式 workspaceRoot 和输入/输出文件身份代替工程身份。preview 可读目标/目录，但不得创建、导入、编译、下线、保存或重试写入。

preview 的 `data.plan` 固定包含 `{hash,releaseKey,tool,argumentsHash,identity,inputHashes,inventoryHash,operations,warnings}`；hash 基于排序键、保序数组、规范化数值/路径和完整目标身份的 SHA-256，排除密码明文、时间戳与 confirm/mode。敏感输入变化仍须通过会话内不可逆摘要令计划失效。apply 重读身份与文件/库存并比对；任何差异返回 PLAN_STALE 或 IDENTITY_MISMATCH。确认令牌只允许一次执行尝试，未知结果消耗令牌，不重放；重新 preview 不等于允许再次写入，先核实实际状态并重建会话。批次默认遇错停止，不承诺原子回滚。

| 行为族 | 4.0 参数和缺省值 | preview / apply 和必须验收的行为 |
|---|---|---|
| 设备创建 | typeIdentifier 必填且精确；deviceName、family；不提供候选轮询缺省 | preview 返回命中的单一目录项、能力范围、名称冲突和计划；apply 只调用一次 Create。Foundation 型号范围继续限制；验证失败后无残留/未知状态 |
| 导入 | overwrite=false、versionPolicy=exact、onError=stop、compileAfter=false；目录顺序显式 importOrder，保留各版 maxItems 上限 | 预览文件哈希、目标组、库存、可覆盖能力；apply 使用原内容，不修版本/BOM。overwrite=true 仅在该版本/对象已支持且本族验收时可用；否则 UNSUPPORTED_CAPABILITY |
| 导出 | overwrite=false、onError=stop、preservePath 保留原支持边界；输出绝对路径或 workspaceRoot 下路径 | 预览目的文件/目录、对象清单和哈希；暂存后发布，发布失败保留原文件与逐项证据；不先删除目的文件。批次/SD/软件单元不扩张为普通块能力 |
| connect / open | 进程必须显式选择；startNew=false、reuseOpen=false、upgrade=reject；upgrade=allow 需独立确认及 copyPath | 连接不等于绑定；列出并校验 PID/start time/工程文件；旧工程升级只针对副本，缺原生支持即拒绝；不关闭陌生或借用工程，不隐式启动 |
| save / close | 保存为显式操作；close 的 saveChanges=false，discardChanges=false；不隐式保存 | 借用工程拒绝关闭；脏工程要求先保存，或显式 discardChanges=true 重新预览确认；LocalSession 使用对应本地保存路径，不替换成普通 Project.Save |
| PLC 路径 | softwarePath="" 仅在工程内唯一 PLC 时选中，否则 TARGET_AMBIGUOUS 并列候选；非空精确匹配 | 使用查询返回的完整路径/结构名称映射，未知非空路径 NOT_FOUND；读取和写入同规则；保留 G9 缓存失效行为，不扩大路径匹配 |
| 外部源 | overwrite=false、onError=stop；sourceName/groupPath 精确；删除 missingPolicy=reject | 预览文件、源身份、生成/删除清单；apply 逐项核实；V14 SP1 observation 与原生结果分列，不按去扩展名猜源、不吞删除错误 |
| 编译 | offlinePolicy=require、password 可选，target kind/软件对象保持各入口边界 | 预览能力、离线前提及 Safety 权限；apply 不自动下线；仅结束本次建立的 Safety 登录，清理失败保留证据；编译诊断不能只看根 ErrorCount |
| 原生回退 | route 必须取已发现的精确路线；retryPolicy=never；refreshReadHandle=false | ApplyConfiguration 失败不继续下载；写入未知立即停止并要求会话重建。VCI 仅在显式 refreshReadHandle=true 且证据证明只读未执行时重取句柄一次；不得换路线重放 |

本表为 **验收后的目标行为**。维护者决定按族推进：未完成该版本/行为族真机验收时保持原行为，工具描述、能力查询和 meta.behaviorPolicy=current / UNVERIFIED_BEHAVIOR 明确披露；不得广告尚未支持的策略参数或伪装已生效的安全缺省。对应参数 schema 在本族切换时一次更新，生成的发布契约记录实际状态。4.0 的名称/类型/信封仍统一；原生政策是否切换是独立能力事实。第 I 步和 G3/J 的开关及发布门槛不因进入阶段 6 自动解除，MTA/STA 归属不改变。

每族先在测试构建完成离线故障证明与前后 Siemens 调用顺序/参数/线程表，再在明确授权的测试工程副本上验收，最后切换该族的发布能力。真机登记见[台账的 P6 项目](../reference/real-machine-ledger.md)；本任务全部为 **NOT RUN**。

## 5. lite、产品与运行目录

lite 的准入标准：覆盖“发现与示例→环境/绑定→定位→常见 PLC 交换和编译→HMI 定位→结果导出”的最短工作流程；低频可选包、在线写入/下载、通用反射、深层 HMI 编辑及自测通过完整目录发现后按需 CallTool。每个候选必须在 V20/V21 都存在，且 `reference/tool-examples/calls.json` 各版 profile 有 arguments 示例；不能仅因为在旧 lite 中就入选。附表 F 和数据提案逐项给出理由，精确数量由生成器输出。

名单按 releaseKey + contractVersion + profile 内嵌；直接调用、FindTools、GetToolUsage、CallTool 使用同一完整目录及版本门禁，lite 只控制 tools/list 的广告成员。发现和分页/导出入口始终可达。Foundation **继续不设 lite**，也不新增它今天没有的 CallTool 或完整引擎能力。P6-07 将名单和过渡名称映射嵌入正式资源；运行时按当前注册名称广告。

产品输出采用 `TiaMcp.FoundationHost.exe`、`TiaMcp.Engine.V20.exe`、`TiaMcp.Engine.V21.exe`；AssemblyName 同步，关联 .dll/.exe.config/.deps.json/.runtimeconfig.json 按实际构建产物同步。发布键目录与开发输出目录不变，源命名空间不为这次改名批量移动。根 `TiaOpenness.exe` 为正式 Studio 启动器，启动 `runtime/studio/TiaOpenness.exe`；根启动器的 AssemblyName 为 TiaOpenness.Launcher，构建复制为根 EXE，避免同基名程序集身份混淆。删除旧根配置器入口；Bridge、worker、adapter 名称保持现有职责。

所有入口接受 `--bundle-root <absolute-path>` / `TIA_MCP_BUNDLE_ROOT`。选择顺序：显式 CLI → 环境变量 → BundleLayout 的已知安装/开发锚点；CLI 与环境变量并存时 CLI 优先。显式值无效立即报错，不向别处查找。根必须含 manifest/package-manifest.json，资源必须在该根，缺失返回 RESOURCE_UNAVAILABLE；删除任意祖先仓库、templates/tools、TMP_EXPORT、同级引擎找不到时改用自身等探测。Foundation 的 release-key.txt、显式 --worker-exe、正式相邻 bridge/adapters 部署继续支持；Studio 新旧原生路径的选择仍服从 G3/J 真机验收。

日志、诊断、配置、界面设置、报告与临时文件使用 [数据目录](runtime-layout.md#软件自身的数据目录) 的 `<bundle>\data` 政策（只读安装回退到原用户目录）；主日志与调用日志已接入 `src/Shared/DataLocations.cs`，引擎启动日志及 Studio 崩溃日志仍有安装目录路径（附表 E1）。P6-39 收口这些路径并按发布键与 `studio` 细分 `data\logs`，diagnostics 仍为诊断证据专用；并发进程使用 request/session/PID 区分文件。Python 默认环境为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python`，显式 `TIA_MCP_PLC_TOOLS_PYTHON` 仍优先。不自动复制执行旧私有环境、不把 Python 环境写入安装目录；缺 LocalAppData 或目录不可写按用途报 IO_FAILED/DIAGNOSTIC_WRITE_FAILED，不能回退安装目录。私人模板、报告、fixture 用显式 `--workspace-root` / 已有 workspaceRoot 参数及具体模板输入，缺失即 INVALID_ARGUMENT，不猜 cwd 或私人目录。

附表 E 生成全部相关第一方文本命中位置，覆盖构建/Package-Release/Validate-Bundle/Check-Repository 必需清单、织入与反射、Studio ConfigCore/ClientProfiles、CLI 配置器、操作脚本和文档。实施先改生成源，再运行产物生成器；不手改 manifest 哈希。客户端配置的 server key、HTTP /mcp 和鉴权键不因 EXE 改名变化，命令/参数由新的产品表生成；已有用户配置先备份再显式迁移。验收包括仓库外完整包、只读安装目录、空格/中文路径、无效显式根、不同 cwd、缺引擎及 worktree 禁止安装更新。

## 6. 决策记录（2026-10-03）

维护者决定：4.0 硬切，放弃 3.x 兼容；硬切前提下重新提出的问题 1–11 全部采用建议。

1. 统一“动词 + 对象”命名，全部不符合项改名，合并证明同义的入口，数量由生成器统计。
2. 输入使用强类型 DTO；参数去 Json，真正动态属性使用受约束字典。
3. 全宿主/调用方式使用同一个 V4 信封，CallTool 与直接调用同形。
4. D1 统一显式安全政策：默认不覆盖、拒绝升级、精确创建、写操作预览后确认。
5. PLC 空路径仅在工程内唯一 PLC 时选择，多 PLC 列候选并拒绝。
6. 按明确标准重新筛选约 60 个 lite 工具，以数据文件维护，每个成员有示例。
7. 三个同名程序改为 FoundationHost / Engine.V20 / Engine.V21。
8. 根目录使用新产品名启动器打开 Studio，删除旧根配置器入口。
9. 使用 bundle-root 输入，私人工作区显式指定；原决定的日志/Python LocalAppData 目标中，日志已由后续 data 目录政策取代，Python 目标不变（第 5 节）。
10. 原生行为按族完成真机验收后进入 4.0，未验收的族保持原行为。
11. 发布说明最后附生成的新旧名称/参数对照表，仅作为文档。
12. （2026-10-05 增补）工作台写操作审批、AI 调用面板、哈希链审计日志与环境体检纳入 4.0，见第 8 节。
13. （2026-10-05 增补）梯形图出图与程序图册纳入 4.0（P6-48），见第 8 节。

## 7. 实施与生成证明

P6-07 过渡规则（审查决定）：各组原地迁移；每个工具始终只注册当前名称或已迁移的 V4 名称，不设别名或双注册。
直接调用、CallTool 和批次中的目标结果同形，桥接不重新包装目标结果；未迁移目标保留 legacy 结果，已迁移目标返回 V4 信封。
桥接自己的名称、参数对象、版本准入、批次数量/白名单与嵌套错误使用 P6-02 错误码。
lite 按 V4 名称维护，生成资源按 releaseKey、contractVersion 记录 V4 名称、当前注册名称和 profile 成员；tools/list 广告当前注册名称。
直接调用、FindTools、GetToolUsage 和 CallTool 始终访问完整目录并保留版本门禁。
导出的非递归 schema 全部内联；真正递归的类型才保留根本地 $defs/$ref。

P6-07 的正式运行资源为 `src/Logic/ModelContextProtocol/ToolProfiles.resx`：Catalog 字符串保存 JSON，
由 SDK 默认 EmbeddedResource 项嵌入 TiaMcp.Logic，不需修改公共项目文件。生成器是唯一写入者；
每版记录包含 name（V4 名称）、currentName、sourceName、profiles 与 arguments，Foundation 无 lite 记录。
5 个导出生命周期入口属于附表 G 的 P6-11/ExportTools.cs；P6-07 只接线本组的大响应分页边界。

P6-07b 共享边界：运行资源每条记录的 `envelopeVersion` 是唯一的 V4 准入标记，注册层不维护工具名称白名单。
生成器把已返回 `CallToolResult` 的入口标为 4，并同时核对附表 A 的名称和 B 的参数目标；尚未迁移的入口标为 3，
继续核对旧字符串参数和保留旧拒绝文本。仅改名不能跳过目标类型检查，也不接受以 `JsonElement` 等擦除载体代替附表 B 的类型；
Foundation 按实际宿主包装及六版准入生成名册。响应快照按同一标记区分 V4 拒绝与旧文本拒绝。
`Generate-ToolUsage.py` 从当前源码注册和版本准入取得八版覆盖集合，不读取旧构建目录或依赖尚未刷新的 profile 资源。

`ToolCatalog` 在 SDK 推断前排除具有自定义契约的参数，再嵌入 `TypedToolInput` 取得的同一族 `InputContract.Schema`
（Construction 使用 `ConstructionInput`）。可选参数的缺省不调用拒绝 null 的 DTO 写转换器，也不广告无效的 null 默认值；
显式 null 仍由类型契约判定。非递归定义完全内联；递归引用提升到工具 schema 根的 `$defs`，重复导出不继续展开。
共享准入在 SDK 绑定、CallTool 和批次目标派发前运行同一族校验，保留 `INVALID_ARGUMENT` / `LIMIT_EXCEEDED`
及参数、预算详情；拒绝为 `rejected-before-operation` / `not-started`。桥接派发前失败仍使用桥接工具身份，
成功派发后保留目标信封。领域依赖的属性白名单、版本/action 与原生能力校验仍归领域入口；共享边界不调用 Siemens API。

P6-07c 补齐外层参数选择的闭合联合：`PlcArtifactSpec` 广告 UdtSpec、PlcTagTableSpec、GlobalDbSpec、FcBlockSpec、FbBlockSpec
五个成员 schema 的内联 `anyOf`，共享准入接受至少一个成员契约成立的对象，并保留族预算、重复字段与错误详情。
绑定得到已验证但未选择成员的 `PlcArtifactSpec`；领域入口调用 `spec.Resolve(kind)`（或 `PlcArtifactSpec.Deserialize`）按外层 `kind`
解析具体成员。FC/FB 结构重叠时不猜测类型，kind/member 匹配仍是领域检查；线上的 spec 保持对象，不引入 JSON 字符串或擦除参数类型。

V4 schema 在 SDK 推断后及提示增强后移除 null 默认值，覆盖可选 `string[]` 等 SDK 推断参数及嵌套属性；
仅 `envelopeVersion=4` 启用，未迁移工具的 schema 保持原样。省略参数仍绑定其声明的默认值，不自动创建空集合；
显式 null 按类型契约判断。嵌入的 typed 参数 schema 保留参数自身 `[Description]` 文本，提示增强不得覆盖其类型约束或描述。

直接调用的 V4 入口在版本包装器之前执行与 CallTool、批次相同的 `BindV4Call` 准入（大小写重复、版本、类型契约、绑定），
因此 P6-07 的 8 个基础设施入口对大小写重复参数也返回 V4 拒绝；P6-07b 据此刷新了这些直接拒绝的响应基线。

P6-14 第二轮补充：`ManagePlcCertificate.subjectAlternativeNames` 使用闭合的 `SubjectAlternativeName[]`，
每项为 `{type:"Dns"|"Email"|"IP"|"Uri",value:string(1..255)}`，最多 64 项；保留原 parser 的非空白值检查和同类型值不区分大小写的去重规则。
`ManageSafetyFunction.properties` 仍为 `AttributeMap<Scalar>`，trace 信号列表移至独立可选参数 `signals:string[]`。
仅 `action=setTrace` 接受 signals；其他 action 即使给空数组也在原生调用前返回 `INVALID_ARGUMENT`。
省略 signals 不写入 Signals 属性，显式空数组表示清空；名称、重复项和顺序原样交给既有服务与 `TraceConfiguration.SetAttribute("Signals", ...)`。
`properties.signals` 不再是有效输入，原有 action、确认、版本和原生调用规则保持不变。

`Check-DeadToolReferences.py --fix` 只在旧名已不注册且附表 A 目标名已注册时修正文案字符串中的完整名称，
逐处输出 REWRITE / KEEP。代码、注释、数据判别值和无法连续定位的转义/拼接拼写保留并报告人工复核；不会改注册或业务分支。


任务顺序、依赖及每项验收见[重构计划阶段 6](refactor-plan.md#阶段-6破坏性变更40)；附表 G 固定工具文件所有权，附表 H 覆盖 P6-01～47 的当前路径，附表 I 固定已合并事实。目录项只定位所有权，实施说明仍须列出精确文件。本文的每项未实现内容都有对应任务；最后一个任务 P6-43 生成发布说明的新旧名称/参数对照。改名、类型化、响应以及安全政策分别证明，不把离线测试称为原生验收。

生成器仅用 Python 标准库和仓库检查器，不加载二进制、不访问网络、无需 SDK。可从任意 cwd 对干净 checkout 运行；只写本页标记块和 lite 运行资源。运行两遍必须字节完全相同，`--check` 不写文件。源码目录与八版基线、当前 lite、示例覆盖、闭合类型族、合并证明和目标唯一性任何一项不符都失败；任务路径必须在当前 Git 文件清单中。六个负例自检拒绝漏映射、未证明重名、表示词、未经证明的编译合并、遗漏任务和旧目录路径。第 I 步五份静态证据、默认关闭开关和八个 P6 行为族的 NOT RUN 状态同时核对。

**V4 契约快照规则（由 P6-41 实施，本次不创建或移动快照）：**

| 内容 | 固定目录与文件名 | 生成工具及规则 |
|---|---|---|
| 实际发布名称、描述摘要、inputSchema/outputSchema、full/lite 名单及逐族行为能力 | `manifest/contracts/v4/baseline/<releaseKey>.json` | 扩展现有 `scripts/checks/Snapshot-ToolContracts.py` 的 capture/compare，读取八版实际产物；只广告实际实现与验收状态，不从提案伪造能力 |
| 离线响应、准入拒绝、直接/CallTool/批次的 V4 信封 | `manifest/contracts/v4/responses/<releaseKey>.json` | 扩展现有 `scripts/checks/Snapshot-ToolResponses.py` 的 capture/compare；只运行明确允许的离线调用，记录完整结构与规范化/原始文本摘要，保留失败、partial、unknown 证据 |
| 3.x 原始基线 | `manifest/history/contracts-v3/baseline/<releaseKey>.json`、`manifest/history/contracts-v3/responses/<releaseKey>.json` | P6-41 将当前 `manifest/contracts/baseline` 与 `manifest/contracts/responses` 的 16 个文件逐字节归档，迁移前后 SHA-256 相同；只读保留，不重新序列化、不重算内容中的哈希，不用 V4 生成结果覆盖 |

`<releaseKey>` 只能为 `14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21`，文件名不带 `v` 前缀，也不用 `14`/`15` 代称；每类快照恰好八份。P6-41 还须记录归档来源提交、版本、每个文件的 SHA-256 及归档只读规则，并同步比较脚本、必需文件清单与本生成器的旧事实读取路径。P6-35 可先使用此 V4 目录生成实际发布候选快照，P6-41 冻结格式与 CI 检查。先捕获到临时目录，两轮独立捕获经明确的易变字段规则归一化后字节相同，比较通过并审查后才更新 V4 基线；不得自动学习掩码或用改哈希掩盖差异。旧 3.x 目录在 P6-41 归档前保持原样。`Generate-Phase6Plan.py` 仅生成提案和路径表，不生成发布契约，也不在 P6-01 加入 CI。

P6-01 核对 `docs/reference/real-machine-ledger.md` 已有 P6-DEVICE、IMPORT、EXPORT、SESSION、CLOSE、SOURCE、COMPILE、FALLBACK 八行，均为 **NOT RUN**，另有 P6-PRODUCT 为 **NOT RUN**；无需补行，未借用 3.3.0 的历史验收结果。

P6-01 冻结差量（相对原生成块；数值只表示源码/目录事实）：

| 项目 | 旧 → 新 | 原因 |
|---|---|---|
| 八版广告工具 | 57/58/60/60/60/62/477/488 → 不变 | 基线、源码、版本目录、示例覆盖重新交叉核对；第 I 步未改变入口 |
| 当前/V4 名称并集；改名/不变入口 | 496/494；183/313 → 不变 | A 表所有名称、合并和发布键映射无差异 |
| 类型参数/工具；当前 lite/提案 | 292/167；63/60 → 不变 | B/F 表及提案 JSON 数据无差异；Foundation 仍不设 lite，调用示例仍覆盖每项 |
| B1 数值边界表达式 | 218 → 220 | P6-02 新增 `src/Logic/V4/Plan.cs` 与 `V4Validation.cs` 两条匹配；`ArgumentRules.cs` 四个行号各加 1；五个共享原语纳入扫描但无新增数值表达式匹配 |
| D 表 Foundation 框架 | net8.0 → net10.0 | 已合并的 .NET 10 迁移；另外从源码补列 Logic、Studio Gui/Core 的 3 项目标框架，非本任务改框架 |
| E1 定位/政策条目 | 19 → 21 | 补列 DataLocations 与 InvocationJournal；纠正主日志已接数据根、启动/崩溃日志仍待迁移的事实，目标随 data 政策更新 |
| E 布局候选文件 | 141 → 144 | 新增 10 个当前命中文件、移除 6 个仅旧源码路径命中的文件，并消除目录导航合并形成的 1 个重复项 |
| 完整任务路径清单 / 第 I 步证据清单 | 未列全 → 47 项 / 5 项 | 新增 H/I；G 原有 17 组的 488 个注册入口归属不变，H 展开对应 Service 并补 P6-44～47 |
| 生成器负例 | 4 → 6 | 增加缺少任务、旧源码目录两种拒绝；原四种名称映射拒绝保留 |

E 表新增命中为 `docs/development/layout-proposal-4.0.md`、`release-workflow.md`，`docs/reference/real-machine-ledger.md`、`docs/releases/v3.3.0.md`，`scripts/build/Test-ReleasePrerequisites.ps1`、`scripts/operations/delivery-files.json`，`src/Adapters/build/Adapter.Sources.props`、`src/Shared/DataLocations.cs`，`tests/Engine/TiaMcpServer.Tests/DataLocationsTests.cs`、`tests/Studio/TiaOpenness.Gui.Tests/UiSettingsTests.cs`：分别对应目录说明、发布流程/证据、预检/交付清单、共享路径及 data 目录与测试的合并结果。

仅由 D334 移动路径引起的变化：B1/E 按当前路径重排；E 中 `scripts/checks/Check-DeadToolReferences.py`、`Test-ImportSelectionSources.py`、`engine_sources.py`、`scripts/diagnostics/Audit-OpennessCoverage.ps1`、`tests/Engine/TiaMcpServer.LegacyHostTests/AdapterSourceClosureTests.cs`、`tests/Engine/TiaMcpServer.Tests/UnifiedScriptSyntaxCheckTests.cs` 不再因旧源码目录中的产品基名而命中，文件本身仍存在；原目录导航已并入 `docs/development/repository-layout.md`，故去掉其重复行。其余 E 命中行号/定位词按当前文件重新提取（含发布记录再生成、data 路径和本次计划文本），不代表工具映射改变；历史 `docs/releases/v3.2.0.md` 与 `v3.3.0.md` 明确标为只读证据，不列为产品改写授权。C 的 50 个响应标记/18 个 variant 保持不变。第 I 步服务、原语和 props 链接及 P6-02 的 7 个 V4 文件在 I 表固定。

```powershell
python -B scripts/generate/Generate-Phase6Plan.py --self-test
python -B scripts/generate/Generate-Phase6Plan.py
python -B scripts/generate/Generate-Phase6Plan.py
python -B scripts/generate/Generate-Phase6Plan.py --check
python scripts/checks/Check-Repository.py --no-binaries
python scripts/checks/Check-DeadToolReferences.py
pwsh -NoProfile -File scripts/checks/Validate-Bundle.ps1 -Strict -NoBinaries -SkipSourceHashes
```

并执行 [.github/workflows/offline-checks.yml](../../.github/workflows/offline-checks.yml) 中 source-contracts 的每一个 run 步骤；该任务均为纯源码检查。schema/产品/原生实现阶段另按[验证分层](validation.md)运行 TRX 最低数量门禁、八版构建、V4 快照与相应 L5。现在的生成表为提案事实，不替换 3.x 的契约或生成资源。

## 8. 工作台与 MCP 融合（2026-10-05 增补）

维护者决定将以下各项纳入 4.0（P6-44～48）。产品行为参考上游作者的商业桌面版，只借鉴思路，不引入其代码。
数据位置沿用 master 上的 [数据目录](runtime-layout.md#软件自身的数据目录)（`<bundle>\data`）。

**写操作审批（P6-44）**

文件归属：引擎 `src/Engine/Program.cs`、`src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs`、`McpServer.ToolBridge.cs`、`McpServer.Batch.cs` 与 `ToolCatalog.cs`，Foundation 的 `src/FoundationHost/Program.cs`、`FoundationTools.cs` 负责执行前门禁；`src/Shared` 新增本机审批协议，`src/Studio/Core`、`src/Studio/Gui/Services` 与 `ViewModels` 新增审批服务/视图，接入 `src/Studio/Gui/MainWindow.xaml`、`MainWindow.xaml.cs`、`Settings/UiSettings.cs` 和 `Localization`。`src/Logic/V4/Error.cs`、`V4Json.cs`、`V4Validation.cs` 承接拒绝详情；完整路径与测试归属见[附表 H/P6-44](#phase6-path-p6-44)。

- 范围：经 MCP（HTTP/stdio，含 CallTool 与批次）到达完整引擎或 Foundation 宿主的每一次写调用。读写分类取自工具目录的写标记；SaveProject、SaveProjectCopy（save-as）与 CloseProject 即使在目录中标为 SESSION 也须审批，其他连接、附着、打开和断开会话操作仍不审批；
  按单个调用判断：只读调用不排队，批次逐项列出写项。CLI 由本机用户直接执行，不经审批。
- 流程：D1 已切换的族在 apply 处、仍为 current 的族在执行前，宿主把待批请求（请求 ID、tool、releaseKey、目标工程身份、
  对象与动作清单、planHash 或参数摘要）推送给工作台并等待；用户逐条批准或拒绝。批准只对该请求 ID 与 planHash 生效一次。
  session、save/close、source、compile 及 device/import/export 的 safe-v4 apply 共用该审批点；候选 apply 视为写调用，
  不改工具目录原有 SESSION/FILE/EXECUTE 分类或 D1 开关。候选 preview 保持不排队。
- 结果：拒绝、超时（默认 120 秒，可配置）或工作台未连接均为操作前拒绝：outcome=rejected-before-operation、
  execution=not-started，错误码 CONFIRMATION_REQUIRED，详情区分 denied/timeout/workbench-unavailable
  （扩展 ConfirmationRequiredDetails 时先改第 3 节）。不确定即拒绝，不放行。
- 开关：默认开启；在 MCP 菜单关闭后只保留 D1 的 confirm/planHash 检查。状态在标题栏 MCP 状态片和每次写结果的
  meta.warnings 中可见，设置保存在 `data\config`。
  关闭期间的宿主 V4 出口（成功、准入/审批拒绝、失败、取消和 worker 中断的 unknown，含 CallTool、批次写项）
  均携带 APPROVAL_DISABLED；保留既有错误码、outcome/execution 和脱敏证据。SDK 在宿主派发前抛出的协议错误
  （例如无法解码 tools/call、未知 SDK 工具或无效基础请求形态）不属于 V4 结果，没有评估审批，也不添加审批元数据。
  宿主拥有的退出不再返回原始异常文本；取消/中断终结已有批准为 unknown，管道失联使待批项失效。
- 通道：审批走宿主与工作台之间的本机通道（仅当前用户可连接的命名管道），不作为 MCP 工具或 HTTP 端点暴露，MCP 客户端不能自行批准。
  威胁模型是“AI 经 MCP 工具越权写入”，不防同一用户下的恶意本机进程，文档须写明。
- 等待审批不占用 Openness 线程，不改 Siemens 调用顺序、线程归属与会话。
  审批决定与开关事件复用每个 data 根唯一的审计链，工作台审计视图读取该链，不按宿主另建审批日志。

**AI 调用面板（P6-45）**

文件归属：`src/Shared/InvocationJournal.cs` 与 `src/Engine/ModelContextProtocol/InvocationJournal.cs` 的调用记录投影，`src/Studio/Core` 的日志读取器、`src/Studio/Gui/ViewModels`/`Controls` 的调用面板及 `MainWindow.xaml` 接线；复制连接信息复用 `src/Studio/Gui/Configuration/ClientProfiles.cs`。新增视图及测试由本任务负责，日志保留策略交 P6-46，见[附表 H/P6-45](#phase6-path-p6-45)。

- 工作台显示调用日志（`data\diagnostics`）：时间、宿主/版本、工具、读/写、outcome、耗时、目标摘要；展开后看参数与结果
  （截断到上限、凭据脱敏）。待批请求在同一面板置顶。
- 只读取宿主写入的日志，不另开采集通道；处理轮转、多进程并发写入与中断的末行。
- 提供复制 MCP 连接信息（URL 与客户端配置片段，不含密钥明文）。

**审计日志与保留（P6-46）**

文件归属：`src/Shared` 新增审计日志与哈希链校验，修改 `src/Shared/InvocationJournal.cs`、`NativeCallDiagnostics.Journal.cs`、`DataLocations.cs` 的日志边界；`src/Engine/Cli/CliCommands.cs` 与 `src/Studio/Gui/ViewModels` 接入校验入口，`src/Studio/Gui/Settings/UiSettings.cs` 管理保留配置。调用面板已有文件在 P6-45 后串行集成，见[附表 H/P6-46](#phase6-path-p6-46)。

- 写调用的请求、批准/拒绝、开始/结束 outcome 及审批开关变化写入 `data\logs\audit`，每条记录含前一条规范 JSON 的 SHA-256；
  轮转后新文件首条链接上一文件末条。
- CLI 子命令和工作台按钮可校验哈希链并报告第一处断链。须写明限制：同一用户能整体删除或截断尾部，哈希链只证明中间未被改动，不证明完整。
- 调用日志的单文件上限与保留份数可配置，默认覆盖至少一个工作日的插桩日志，面板显示当前保留的时间窗口（替代 10 MB 加一份 `.previous`）。

**环境体检（P6-47）**

文件归属：复用 `src/Shared/Host/EnvironmentDoctor.cs`、`src/Shared/Host/McpServer.Doctor.cs`、`src/FoundationHost/FoundationPassiveDiagnostics.cs`、`src/Studio/Core/Environment/OpennessDoctor.cs` 及 `src/Shared/OpennessEnvironment.cs`；`src/Shared/DataLocations.cs` 提供数据目录状态，`src/Studio/Gui/ViewModels`/`Configuration` 新增体检和诊断包视图并接入 `MainWindow.xaml`、`Localization`。启动器的独立提示不纳入本任务，见[附表 H/P6-47](#phase6-path-p6-47)。

- 工作台“环境体检”页逐项检查并给出修复方法：已安装的 TIA 版本、Openness 用户组（含需重新登录）、.NET Framework 4.8、
  随包 .NET 运行时、各版本引擎/worker 文件、`data` 可写、HTTP 端口占用、URL 保留与防火墙，以及首次附着时 TIA 的 Openness 访问确认。
  复用现有 doctor 检查，不新增 Siemens 写调用。
- 一键把体检结果、最近日志和配置（密钥脱敏）打包到 `data\reports`。
- 启动器在缺少 .NET Framework 4.8 时的中文提示另行完成，不依赖工作台。

**梯形图出图与程序图册（P6-48，2026-10-05 增补）**

- 输入是 SimaticML 导出（单块 XML 或导出目录）。出图是纯逻辑：不调用 Siemens、不改工程；需要先导出时复用现有只读导出工具，
  并服从当时导出行为族的状态。
- 单块页：块头（块号、类型、语言、标题、注释、来源文件、接口）；逐程序段显示标题、注释与梯形图（触点、线圈、并联分支、
  功能框的实例名/引脚/预设值）。FBD 按梯形图等效画法并注明，SCL/STL 显示为代码，空段标注。
- 程序图册：目录页列出块号、名称、语言、程序段计数（梯形图/代码/空）、调用与被调用、来源文件；每块一页，调用处可跳转。
  "被调用"只统计图册内的块并写明范围。
- 可选把现有逻辑检查结论（如常量触点）标在对应元素上。
- 输出自包含静态 HTML（内联 SVG/CSS，无外部资源），写到显式输出路径的新文件，不覆盖已有文件，结果使用 V4 信封。
- 八个版本均可用：Foundation 宿主与完整引擎同一实现，逐版本核对 SimaticML 命名空间与元素差异，每版至少一份有来源记录的真实导出样本。
- 把 `RenderPlcVisualDiff` 的梯形图布局与 SVG 绘制抽成共享逻辑复用，该工具输出逐字节不变。
- 工作台在程序块页提供"查看梯形图"（选中块）与"生成程序图册"（当前 PLC），完成后显示路径并可打开。
- 不包含在线能流着色和编辑。

## 附表：机器生成的当前事实与 V4 提案


<!-- phase6-generated:start -->

### 当前基线与 V4 提案计数

| 发布键 | 当前广告工具 | 当前 lite | string …Json | 涉及工具 | V4 工具 | V4 lite 提案 |
|---|---|---|---|---|---|---|
| 14sp1 | 106 | 不设 | 61 | 27 | 106 | 不设 |
| 15.1 | 107 | 不设 | 61 | 27 | 107 | 不设 |
| 16 | 109 | 不设 | 61 | 27 | 109 | 不设 |
| 17 | 109 | 不设 | 61 | 27 | 109 | 不设 |
| 18 | 109 | 不设 | 61 | 27 | 109 | 不设 |
| 19 | 111 | 不设 | 61 | 27 | 111 | 不设 |
| 20 | 482 | 63 | 272 | 156 | 480 | 73 |
| 21 | 493 | 63 | 289 | 164 | 491 | 73 |

八版当前名称并集 501；V4 名称并集 499；改名/合并入口 183；不变 318。数字只指目录，不代表原生能力验收。

<details>
<summary>A. 全量 current name → 4.0 name（包括不变项）</summary>

| 当前名称 | 4.0 名称 | 保留发布键 | 依据/合并证明 |
|---|---|---|---|
| `AddDevice` | `CreateDevice` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；精确创建政策须 D1/L5，未验收前保持原行为并披露能力状态；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `AddDeviceWithFallback` | `CreateHardwareDevice` | 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；精确创建政策须 D1/L5，未验收前保持原行为并披露能力状态；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `AddGsdDeviceWithProbe` | `CreateGsdDevice` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；精确创建政策须 D1/L5，未验收前保持原行为并披露能力状态；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `AddHardwareCatalogDeviceWithProbe` | `CreateHardwareCatalogDevice` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；精确创建政策须 D1/L5，未验收前保持原行为并披露能力状态；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `AnalyzeGlobalLibraryPackage` | `AnalyzeGlobalLibraryPackage` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `AnalyzeHmiTemplateReference` | `AnalyzeHmiTemplateReference` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `AnalyzePlcReferences` | `AnalyzePlcReferences` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/PlcOfflineTools.cs) |
| `AnalyzeUnifiedHmiTemplateLayout` | `AnalyzeUnifiedHmiTemplateLayout` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `ApplyToolBatch` | `ApplyToolBatch` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.Batch.cs) |
| `ApplyUnifiedHmiLayout` | `ApplyUnifiedHmiLayout` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ApplyUnifiedHmiScreenDesignJson` | `ApplyUnifiedHmiScreenDesign` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ApplyUnifiedHmiTheme` | `ApplyUnifiedHmiTheme` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ArchiveSavedProject` | `ArchiveSavedProject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `AttachDeviceNodeToSubnet` | `AttachDeviceNodeToSubnet` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `AttachToOpenProject` | `AttachOpenProject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `AuditEngineeringExports` | `AuditEngineeringExports` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/QualityAuditTools.cs) |
| `BindUnifiedHmiButtonPressedTag` | `BindUnifiedHmiButtonPressedTag` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `BindUnifiedHmiTagDynamization` | `BindUnifiedHmiTagDynamization` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `Bootstrap` | `InitializeEnvironment` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `BuildClassicHmiMinimalPackage` | `BuildClassicHmiMinimalPackage` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildClassicHmiScreenXml` | `BuildClassicHmiScreen` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildClassicHmiTagTableXml` | `BuildClassicHmiTagTable` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildDeviceAmlDocument` | `BuildDeviceAmlDocument` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAmlTools.cs) |
| `BuildFlgNetCallXml` | `BuildFlgNetCall` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcGlobalDbXml` | `BuildPlcGlobalDb` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcSymbolManifestFromXmlPath` | `BuildPlcSymbolManifestFromPath` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildPlcTagTableXml` | `BuildPlcTagTable` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcUdtXml` | `BuildPlcUdt` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildReleaseDiagnosticReport` | `BuildReleaseDiagnosticReport` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildReleaseManifest` | `BuildReleaseManifest` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildReleaseRunbook` | `BuildReleaseRunbook` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildStructuredTextXml` | `BuildStructuredText` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildUnifiedHmiButtonActionScript` | `BuildUnifiedHmiButtonActionScript` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `BuildUnifiedHmiLayoutDesignJson` | `BuildUnifiedHmiLayoutDesign` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `BuildUnifiedHmiTemplateApplyDesignJson` | `BuildUnifiedHmiTemplateApplyDesign` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildUnifiedHmiTemplateApplyDesignManifest` | `BuildUnifiedHmiTemplateApplyDesignManifest` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildUnifiedHmiThemeDesignJson` | `BuildUnifiedHmiThemeDesign` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `CallTool` | `CallTool` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `CheckDownloadReadiness` | `CheckDownloadReadiness` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `CheckForUpdate` | `CheckProductUpdate` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/McpServer.Maintenance.cs) |
| `CheckLibraryUpdates` | `CheckLibraryUpdates` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `CleanupStagedImportFiles` | `CleanupStagedImportFiles` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 4.0 新增；P6-67；操作分类 FILE；[源码](../../src/Engine/ModelContextProtocol/Tools/ImportStagingTools.cs) |
| `ClearExports` | `ClearExportHandles` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/ExportTools.cs) |
| `CloseProject` | `CloseProject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `CompareLibraries` | `CompareLibraries` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareLibraryObjects` | `CompareLibraryObjects` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ComparePlcBlockDocuments` | `ComparePlcBlockDocuments` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `CompareProjects` | `CompareProjects` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareSoftwareToOnline` | `CompareSoftwareToOnline` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `CompareUnifiedGraphicSelections` | `CompareUnifiedGraphicSelections` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs) |
| `CompileAndDiagnoseHmi` | `CompileHmiDiagnostics` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `CompileAndDiagnosePlc` | `CompilePlcDiagnostics` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `CompileDevice` | `CompileDevice` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `CompileSoftware` | `CompilePlcSoftware` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs) |
| `ComposePlcAliasAlarmLad` | `BuildPlcAliasAlarmLad` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/TemplateTools.cs) |
| `ComposePlcFbBlockXml` | `BuildPlcFbBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `ComposePlcFcBlockXml` | `BuildPlcFcBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `ComposePlcLadFcBlockXml` | `BuildPlcLadFcBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/XmlBuilderTools.cs) |
| `ConfigureMotionHardwareConnection` | `ConfigureMotionHardwareConnection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `Connect` | `ConnectPortal` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `ConnectDeviceNodesToProfinetSubnet` | `ConnectDeviceNodesToProfinetSubnet` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ConnectIsolated` | `ConnectIsolatedPortal` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `ConnectProjectToWorkspace` | `ConnectProjectToWorkspace` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) |
| `ConnectToProject` | `ConnectProject` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；不合并：绑定/启动、诊断范围或目标不同，源码未证明同义；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `CreateLibraryMasterCopy` | `CreateLibraryMasterCopy` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `CreatePlcBlockGroup` | `CreatePlcBlockGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `CreatePlcInstanceDb` | `CreatePlcInstanceDb` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `CreatePlcTag` | `CreatePlcTag` | 14sp1, 15.1, 16, 17, 18, 19 | 不变；符合命名规则；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `CreatePlcTagTable` | `CreatePlcTagTable` | 14sp1, 15.1, 16, 17, 18, 19 | 不变；符合命名规则；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `CreatePlcTypeGroup` | `CreatePlcTypeGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `CreatePlcUserConstant` | `CreatePlcUserConstant` | 14sp1, 15.1, 16, 17, 18, 19 | 不变；符合命名规则；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `CreateProject` | `CreateProject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `CreateVersionControlWorkspace` | `CreateVersionControlWorkspace` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) |
| `DecodePlcSimaticMl` | `DecodePlcSimaticMl` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/V21EcosystemTools.cs) |
| `DeleteEmptyPlcBlockGroup` | `DeleteEmptyPlcBlockGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `DeleteEmptyUnifiedHmiScreenGroup` | `DeleteEmptyUnifiedHmiScreenGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `DeleteExport` | `DeleteExportHandle` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/ExportTools.cs) |
| `DeleteHmiTag` | `DeleteHmiTag` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiTagDeletionTools.cs) |
| `DeletePlcBlock` | `DeletePlcBlock` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `DeletePlcExternalSource` | `DeletePlcExternalSource` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `DeletePlcTagTable` | `DeletePlcTagTable` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `DeletePlcType` | `DeletePlcType` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `DeleteUnifiedHmiButtonEvent` | `DeleteUnifiedHmiButtonEvent` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `DeleteUnifiedHmiDynamization` | `DeleteUnifiedHmiDynamization` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `DescribeBlockLogic` | `DescribePlcBlockLogic` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `DescribeHmiScreen` | `DescribeHmiScreen` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `DescribeHmiScreenItem` | `DescribeHmiScreenItem` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `DescribeHmiSoftware` | `DescribeHmiSoftware` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `DescribeHmiTag` | `DescribeHmiTag` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `DescribeHmiTagTable` | `DescribeHmiTagTable` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `DescribeObject` | `DescribeObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `DescribeObjectProperty` | `DescribeObjectProperty` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `DescribeService` | `DescribeService` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `DescribeUnifiedHmiButtonEventScript` | `DescribeUnifiedHmiButtonEventScript` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `DescribeUnifiedScreenItemType` | `DescribeUnifiedScreenItemType` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs) |
| `DiagnosePortalConnectReadiness` | `GetPortalConnectionReadiness` | 14sp1, 15.1, 16, 17, 18, 19 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `Disconnect` | `DisconnectPortal` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `Doctor` | `GetEnvironmentDiagnostics` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/McpServer.Doctor.cs) |
| `DownloadPlcToFolder` | `DownloadPlcToFolder` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `DownloadToPlc` | `DownloadPlc` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `DumpDeviceAttributes` | `GetDeviceAttributes` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `EnsureOpennessUserGroup` | `EnsureOpennessUserGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `EnsureStartStopUnifiedHmi` | `SetUnifiedHmiRuntimeState` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureSubnet` | `EnsureSubnet` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `EnsureUnifiedHmiButtonAction` | `EnsureUnifiedHmiButtonAction` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiButtonEventHandler` | `EnsureUnifiedHmiButtonEventHandler` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiConnection` | `EnsureUnifiedHmiConnection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiDynamization` | `EnsureUnifiedHmiDynamization` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiScreen` | `EnsureUnifiedHmiScreen` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiScreenItem` | `EnsureUnifiedHmiScreenItem` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiTag` | `EnsureUnifiedHmiTag` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `EnsureUnifiedHmiTagTable` | `EnsureUnifiedHmiTagTable` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ExchangeCfcCharts` | `ExchangeCfcCharts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs) |
| `ExchangeMotionCamData` | `ExchangeMotionCamData` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ExchangePlcAlarmTextListsXlsx` | `ExchangePlcAlarmTextLists` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExchangePlcSupervisions` | `ExchangePlcSupervisions` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SpecializedExchangeTools.cs) |
| `ExchangeSystemDiagnosticsSettings` | `ExchangeSystemDiagnosticsSettings` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ExchangeTestSuiteCase` | `ExchangeTestSuiteCase` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `ExchangeUnifiedScriptModules` | `ExchangeUnifiedScriptModules` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs) |
| `ExchangeUnifiedTags` | `ExchangeUnifiedTags` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs) |
| `ExportAlarmClasses` | `ExportAlarmClasses` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExportAlarmInstanceTexts` | `ExportAlarmInstanceTexts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExportAlarmTextLists` | `ExportAlarmTextLists` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExportAsDocuments` | `ExportPlcBlockDocuments` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs) |
| `ExportBlock` | `ExportPlcBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ExportBlocks` | `ExportPlcBlocks` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ExportBlocksAsDocuments` | `ExportPlcBlocksDocuments` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs) |
| `ExportDeviceAml` | `ExportDeviceAml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAmlTools.cs) |
| `ExportHmiConnection` | `ExportHmiConnection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ExportHmiProgram` | `ExportHmiProgram` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ExportHmiScreen` | `ExportHmiScreen` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ExportHmiTagTable` | `ExportHmiTagTable` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ExportOpcUaInterface` | `ExportOpcUaInterface` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ExportPlcProDiagInfo` | `ExportPlcProDiagInfo` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ExportPlcTagTable` | `ExportPlcTagTable` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ExportPlcWatchTable` | `ExportPlcWatchTable` | 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ExportPlcWatchTablesToDirectory` | `ExportPlcWatchTablesToDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ExportProjectTexts` | `ExportProjectTexts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `ExportSafetyPrintout` | `ExportSafetyPrintout` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ExportScadaData` | `ExportScadaData` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ExportTechnologyObject` | `ExportTechnologyObject` | 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ExportTechnologyObjectsToDirectory` | `ExportTechnologyObjectsToDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ExportType` | `ExportPlcType` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `ExportTypes` | `ExportPlcTypes` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `ExportUnifiedEngineeringList` | `ExportUnifiedEngineeringList` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ExtractPlcBlockMetrics` | `ExtractPlcBlockMetrics` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `FindTools` | `FindTools` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `GenerateAcceptanceReport` | `GenerateAcceptanceReport` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HostMetaTools.cs) |
| `GenerateBlocksFromExternalSource` | `GenerateBlocksFromExternalSource` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `GenerateErrorReport` | `GenerateErrorReport` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HostMetaTools.cs) |
| `GenerateOpcUaModelledInterface` | `GenerateOpcUaModelledInterface` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `GeneratePlcDocumentation` | `GeneratePlcDocumentation` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/PlcDocumentationTools.cs) |
| `GeneratePlcLoadableFile` | `GeneratePlcLoadableFile` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `GeneratePlcSourceFromBlocks` | `GeneratePlcSourceFromBlocks` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `GenerateSiVArc` | `GenerateSivarc` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `GetAuthoringGuide` | `GetToolUsage` | 20, 21 | 原指南入口已删除；ToolUsageCatalog.GuideSelection 保留逐主题选择器映射；GetToolUsage 和 ToolRecipes.Rows 读取同一 Sequences/语言示例库，保留目的、前置条件、步骤、预期与说明，无原生动作。；[源码](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs) |
| `GetBlockInfo` | `GetPlcBlockInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `GetBlocks` | `ListPlcBlocks` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `GetBlocksWithHierarchy` | `GetPlcBlockHierarchy` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `GetCrossReferences` | `GetPlcCrossReferences` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `GetDeviceInfo` | `GetDeviceInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `GetDeviceIpAddress` | `GetDeviceIpAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAddressTools.cs) |
| `GetDeviceItemInfo` | `GetDeviceItemInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `GetDeviceItemIoAddresses` | `GetDeviceItemIoAddresses` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAddressTools.cs) |
| `GetDeviceItemNetworkInfo` | `GetDeviceItemNetworkInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `GetDeviceItemTree` | `GetDeviceItemTree` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `GetDevicePlugLocations` | `GetDevicePlugLocations` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/ModulesTools.cs) |
| `GetDevices` | `ListDevices` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `GetExport` | `GetExportContent` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/ExportTools.cs) |
| `GetHmiConnections` | `ListHmiConnections` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `GetHmiProgramInfo` | `GetHmiProgramInfo` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs) |
| `GetHmiScreens` | `ListHmiScreens` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `GetHmiTagTables` | `ListHmiTagTables` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `GetHmiTags` | `ListHmiTags` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `GetObjectProperty` | `GetObjectProperty` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `GetOnlineState` | `GetOnlineState` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `GetOpcUaConfig` | `GetPlcOpcUaConfiguration` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `GetPlcExternalSources` | `ListPlcExternalSources` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `GetPlcForceTables` | `ListPlcForceTables` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `GetPlcRunStateS7` | `GetPlcRunStateS7` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `GetPlcTagTables` | `ListPlcTagTables` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `GetPlcWatchTables` | `ListPlcWatchTables` | 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `GetProject` | `GetProjectInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `GetProjectTopology` | `GetProjectTopology` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `GetProjectTree` | `GetProjectTree` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs) |
| `GetPutGetAccess` | `GetPlcPutGetAccess` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `GetRecipe` | `GetToolUsage` | 20, 21 | 原指南入口已删除；ToolUsageCatalog.GuideSelection 保留逐主题选择器映射；GetToolUsage 和 ToolRecipes.Rows 读取同一 Sequences/语言示例库，保留目的、前置条件、步骤、预期与说明，无原生动作。；[源码](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs) |
| `GetSoftwareInfo` | `GetSoftwareInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs) |
| `GetSoftwareTree` | `GetSoftwareTree` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs) |
| `GetState` | `GetSessionState` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `GetTechnologyObjects` | `ListTechnologyObjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `GetToolUsage` | `GetToolUsage` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 原指南入口已删除；ToolUsageCatalog.GuideSelection 保留逐主题选择器映射；GetToolUsage 和 ToolRecipes.Rows 读取同一 Sequences/语言示例库，保留目的、前置条件、步骤、预期与说明，无原生动作。；[源码](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs) |
| `GetTypeInfo` | `GetPlcTypeInfo` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `GetTypes` | `ListPlcTypes` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `GetUnifiedCrossReferences` | `GetUnifiedCrossReferences` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `GetVersionControlStatus` | `GetVersionControlStatus` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) |
| `GetVersionControlWorkspaces` | `ListVersionControlWorkspaces` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) |
| `GoOffline` | `DisconnectOnlinePlc` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `GoOfflineAll` | `DisconnectOnlinePlcs` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `GoOnline` | `ConnectOnlinePlc` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ImportAlarmClasses` | `ImportAlarmClasses` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ImportAlarmTextLists` | `ImportAlarmTextLists` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ImportBlock` | `ImportPlcBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ImportBlocksFromDirectory` | `ImportPlcBlocksFromDirectory` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ImportBlocksFromDocuments` | `ImportPlcBlocksDocuments` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs) |
| `ImportDeviceAml` | `ImportDeviceAml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAmlTools.cs) |
| `ImportFromDocuments` | `ImportPlcBlockDocuments` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs) |
| `ImportHmiConnection` | `ImportHmiConnection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ImportHmiScreen` | `ImportHmiScreen` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ImportHmiScreensFromDirectory` | `ImportHmiScreensFromDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ImportHmiTagTable` | `ImportHmiTagTable` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ImportHmiTagTablesFromDirectory` | `ImportHmiTagTablesFromDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs) |
| `ImportLibraryTypeDocuments` | `ImportLibraryTypeDocuments` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ImportMasterCopyFromGlobalLibrary` | `ImportMasterCopyFromGlobalLibrary` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ImportOpcUaInterface` | `ImportOpcUaInterface` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ImportPlcAlarmInstanceTexts` | `ImportPlcAlarmInstanceTexts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ImportPlcBlockVerified` | `ImportPlcBlockVerified` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ImportPlcExternalSource` | `ImportPlcExternalSource` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `ImportPlcProgramFromDirectory` | `ImportPlcProgramFromDirectory` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ImportPlcTagTable` | `ImportPlcTagTable` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ImportPlcTagTablesFromDirectory` | `ImportPlcTagTablesFromDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ImportPlcWatchTableOffline` | `ImportPlcWatchTableOffline` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ImportProjectTexts` | `ImportProjectTexts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `ImportSinumerikAlarmTexts` | `ImportSinumerikAlarmTexts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ImportTechnologyObject` | `ImportTechnologyObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ImportTechnologyObjectsFromDirectory` | `ImportTechnologyObjectsFromDirectory` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ImportType` | `ImportPlcType` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `ImportUnifiedEngineeringList` | `ImportUnifiedEngineeringList` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs) |
| `ImportUnifiedOpcUaAlarms` | `ImportUnifiedOpcUaAlarms` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs) |
| `InitializeSimotionScripting` | `InitializeSimotionScripting` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `InspectSimaticSdCompatibility` | `InspectSimaticSdCompatibility` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/EngineeringDiagnosticsTools.cs) |
| `InstantiatePlcXmlTemplates` | `InstantiatePlcTemplates` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/TemplateTools.cs) |
| `InvokeObject` | `InvokeObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `InvokeService` | `InvokeService` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `LintPlcSclSource` | `AnalyzePlcSclSource` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/PlcDocumentationTools.cs) |
| `ListExports` | `ListExportHandles` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/ExportTools.cs) |
| `ListHmiScreenPaths` | `ListHmiScreenPaths` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `ListObjectChildren` | `ListObjectChildren` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs) |
| `ListPortalProcessProjects` | `ListPortalProcessProjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `ListStagedImportFiles` | `ListStagedImportFiles` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 4.0 新增；P6-67；操作分类 READ；[源码](../../src/Engine/ModelContextProtocol/Tools/ImportStagingTools.cs) |
| `ListToolCategories` | `ListToolCategories` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `ListUnifiedGlobalScripts` | `ListUnifiedGlobalScripts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ListUnifiedHmiApiTypes` | `ListUnifiedHmiApiTypes` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ListUnifiedLibraryFolder` | `ListUnifiedLibraryFolderEntries` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ManageCfcChartProtection` | `ManageCfcChartProtection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs) |
| `ManageClassicHmiCycle` | `ManageClassicHmiCycle` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageClassicHmiFolder` | `ManageClassicHmiFolder` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs) |
| `ManageClassicHmiGraphic` | `ManageClassicHmiGraphic` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs) |
| `ManageClassicHmiScreenObject` | `ManageClassicHmiScreenObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs) |
| `ManageClassicHmiScript` | `ManageClassicHmiScript` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageClassicHmiTextGraphicList` | `ManageClassicHmiTextGraphicList` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageCommunicationConnection` | `ManageCommunicationConnection` | 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDcbLibraries` | `ManageDcbLibraries` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccBlock` | `ManageDccBlock` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChart` | `ManageDccChart` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartInterface` | `ManageDccChartInterface` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartPartition` | `ManageDccChartPartition` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccPin` | `ManageDccPin` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDeviceServiceObjects` | `ManageDeviceServiceObjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDeviceUserGroup` | `ManageDeviceUserGroup` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageDeviceUsers` | `ManageDeviceUsers` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs) |
| `ManageDriveFunctions` | `ManageDriveFunctions` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveHardwareModule` | `ManageDriveHardwareModule` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSafetyAcceptanceTest` | `ManageDriveSafetyAcceptanceTest` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSecurity` | `ManageDriveSecurity` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveTelegrams` | `ManageDriveTelegrams` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageGlobalLibrary` | `ManageGlobalLibrary` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageHardwareObject` | `ManageHardwareObject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareManagementTools.cs) |
| `ManageHardwareUtilities` | `ManageHardwareUtilities` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageIoSystem` | `ManageIoSystem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageLibraryFolder` | `ManageLibraryFolder` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageLibraryMasterCopy` | `ManageLibraryMasterCopy` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageLibraryType` | `ManageLibraryType` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageLibraryTypeVersion` | `ManageLibraryTypeVersion` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageMotionAxis` | `ManageMotionAxis` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageMultiuserSession` | `ManageMultiuserSession` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ManageNetworkDomain` | `ManageNetworkDomain` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageOnlineDriveFunctions` | `ManageOnlineDriveFunctions` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageOpcUaAccessControl` | `ManageOpcUaAccessControl` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ManageOpcUaInterface` | `ManageOpcUaInterface` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ManagePasswordPolicy` | `ManagePasswordPolicy` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManagePlcAlarmTextList` | `ManagePlcAlarmTextList` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ManagePlcBlockDocuments` | `ManagePlcBlockDocuments` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/EngineeringAuditTools.cs) |
| `ManagePlcBlockProtection` | `ManagePlcBlockProtection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ManagePlcBlockWriteProtection` | `ManagePlcBlockWriteProtection` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManagePlcCertificate` | `ManagePlcCertificate` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcDataBlockSnapshot` | `ManagePlcDataBlockSnapshot` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ManagePlcDocuments` | `ManagePlcDocuments` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManagePlcExternalSources` | `ManagePlcExternalSources` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `ManagePlcGitRepository` | `ManagePlcGitRepository` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs) |
| `ManagePlcProtection` | `ManagePlcProtection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `ManagePlcSafety` | `ManagePlcSafety` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ManagePlcSimAdvancedInstance` | `ManagePlcSimAdvancedInstance` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `ManagePlcSoftwareUnit` | `ManagePlcSoftwareUnit` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManagePlcSupervision` | `ManagePlcSupervision` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManagePlcTableEntries` | `ManagePlcTableEntries` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ManagePlcTagDefinition` | `ManagePlcTagDefinition` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `ManagePlcUserGroup` | `ManagePlcUserGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ManagePortInterconnection` | `ManagePortInterconnection` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageProjectCompilationSettings` | `ManageProjectCompilationSettings` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManageProjectLanguage` | `ManageProjectLanguage` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `ManageProjectUserManagement` | `ManageProjectUserManagement` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ManageSafetyActivationTest` | `ManageSafetyActivationTest` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyActivationTestGroup` | `ManageSafetyActivationTestGroup` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunction` | `ManageSafetyFunction` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunctionCondition` | `ManageSafetyFunctionCondition` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyGlobalSettings` | `ManageSafetyGlobalSettings` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ManageSiVArcRule` | `ManageSivarcRule` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) |
| `ManageSinumerikArchive` | `ManageSinumerikArchive` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSinumerikSafetyMode` | `ManageSinumerikSafetyMode` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSivarcBlockDefinition` | `ManageSivarcBlockDefinition` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcRuleContainer` | `ManageSivarcRuleContainer` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcScreenLayout` | `ManageSivarcScreenLayout` | 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcTableRule` | `ManageSivarcTableRule` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageStartdriveParameter` | `ManageStartdriveParameter` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageSyslogServers` | `ManageSyslogServers` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageTeamcenterConnection` | `ManageTeamcenterConnection` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTeamcenterDataset` | `ManageTeamcenterDataset` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTeamcenterWorkflow` | `ManageTeamcenterWorkflow` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTechnologyExtensions` | `ManageTechnologyExtensions` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageTechnologyObject` | `ManageTechnologyObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ManageTestSuiteCase` | `ManageTestSuiteCase` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `ManageTransferArea` | `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageUmcUsers` | `ManageUmcUsers` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageUnifiedCwcPackage` | `ManageUnifiedCwcPackage` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/V21EcosystemTools.cs) |
| `ManageUnifiedDynamization` | `ManageUnifiedDynamization` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedEngineeringObject` | `ManageUnifiedEngineeringObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs) |
| `ManageUnifiedEvent` | `ManageUnifiedEvent` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs) |
| `ManageUnifiedHmiGroup` | `ManageUnifiedHmiGroup` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiGroupsTools.cs) |
| `ManageUnifiedListEntries` | `ManageUnifiedListEntries` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedLoggingTag` | `ManageUnifiedLoggingTag` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedObjectParts` | `ManageUnifiedObjectParts` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedOpcUaAlarmType` | `ManageUnifiedOpcUaAlarmType` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedPlantNode` | `ManageUnifiedPlantNode` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedScreenItem` | `ManageUnifiedScreenItem` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs) |
| `ManageUnifiedScreenLayout` | `ManageUnifiedScreenLayout` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageWatchForceTableWebAccess` | `ManageWatchForceTableWebAccess` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `MonitorWatchTableLiveS7` | `MonitorPlcWatchTableS7` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `MoveBlockToGroup` | `MovePlcBlockToGroup` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `OpenProject` | `OpenProject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `PatchPlcBlockDocument` | `PatchPlcBlockDocument` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/PlcOfflineTools.cs) |
| `PlanArtifactImportOrder` | `PlanArtifactImportOrder` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs) |
| `PlanGlobalLibraryTemplateReuse` | `PlanGlobalLibraryTemplateReuse` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `PlanHardwareNetworkConfiguration` | `PlanHardwareNetworkConfiguration` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `PlanOnlineReadOnlyDataProvider` | `PlanOnlineReadOnlyDataProvider` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `PlanOnlineReadOnlyMonitoring` | `PlanOnlineReadOnlyMonitoring` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `PlanPlcExternalSourceImport` | `PlanPlcExternalSourceImport` | 14sp1, 15.1, 16, 17, 18, 19 | 不变；符合命名规则；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `PlcBuildAndImport` | `BuildAndImportPlcArtifact` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBuildTools.cs) |
| `PlugDeviceItem` | `PlugDeviceItem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/ModulesTools.cs) |
| `PreflightToolCall` | `PreviewToolCall` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `PreviewToolBatch` | `PreviewToolBatch` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.Batch.cs) |
| `ProbeGlobalLibrary` | `ProbeGlobalLibrary` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ProbeHardwareHmiConnectionOwnerCandidates` | `ProbeHardwareHmiConnectionOwnerCandidates` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ProbeHardwareHmiConnectionWhitelistedServices` | `ProbeHardwareHmiConnectionWhitelistedServices` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ProbePlcMonitorOnlineCapabilities` | `ProbePlcMonitorOnlineCapabilities` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ProbeS7CpuIdentity` | `ProbeS7CpuIdentity` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ReadClassicHmiFaceplates` | `ListClassicHmiFaceplates` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ReadClassicHmiGlobalization` | `GetClassicHmiGlobalization` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ReadClassicHmiScreenTree` | `GetClassicHmiScreenTree` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs) |
| `ReadClassicHmiScripts` | `ListClassicHmiScripts` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ReadCommunicationConnections` | `ListCommunicationConnections` | 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadDccCharts` | `ListDccCharts` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDccObject` | `GetDccObject` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDeviceAddressing` | `GetDeviceAddressing` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareAddressTools.cs) |
| `ReadDeviceItemChannels` | `ListDeviceItemChannels` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadDriveObjects` | `ListDriveObjects` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveParameters` | `GetDriveParameters` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadHardwareFeatures` | `GetHardwareFeatures` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadHmiScreenSnapshot` | `GetHmiScreenSnapshot` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `ReadIoSystems` | `ListIoSystems` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadLibraryOverview` | `GetLibraryOverview` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ReadLibraryType` | `GetLibraryType` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ReadMotionAxisConfiguration` | `GetMotionAxisConfiguration` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ReadNativeInvocationLog` | `GetNativeInvocationLog` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/EngineeringDiagnosticsTools.cs) |
| `ReadNetworkDomains` | `ListNetworkDomains` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadObjectIdentifier` | `GetObjectIdentifier` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ReadOnlineDriveParameters` | `GetOnlineDriveParameters` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadOpcUaAccessControl` | `GetOpcUaAccessControl` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ReadOpennessCompatibility` | `GetOpennessCompatibility` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/EngineeringDiagnosticsTools.cs) |
| `ReadOpennessGuidance` | `GetOpennessGuidance` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/EcosystemTools.cs) |
| `ReadOpennessWorkerStatus` | `GetOpennessWorkerStatus` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/McpServer.Worker.cs) |
| `ReadPlcBlockEditCapabilities` | `GetPlcBlockEditCapabilities` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ReadPlcBlockFingerprints` | `GetPlcBlockFingerprints` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ReadPlcBlockScopes` | `GetPlcBlockScopes` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/EngineeringAuditTools.cs) |
| `ReadPlcChecksums` | `GetPlcChecksums` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ReadPlcLiveValuesOpcUa` | `GetPlcLiveValuesOpcUa` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ReadPlcLiveValuesS7` | `GetPlcLiveValuesS7` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ReadPlcObjectFingerprints` | `GetPlcObjectFingerprints` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ReadPlcSimAdvancedInstances` | `ListPlcSimAdvancedInstances` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `ReadPlcSimAdvancedTags` | `GetPlcSimAdvancedTags` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `ReadPlcSoftwareUnits` | `ListPlcSoftwareUnits` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ReadPlcSystemConstants` | `ListPlcSystemConstants` | 14sp1, 15.1, 16, 17, 18, 19 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `ReadPlcSystemGroups` | `ListPlcSystemGroups` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs) |
| `ReadPlcTagTableConstants` | `GetPlcTagTableConstants` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ReadPlcTags` | `ListPlcTags` | 14sp1, 15.1, 16, 17, 18, 19 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `ReadPlcUserConstants` | `ListPlcUserConstants` | 14sp1, 15.1, 16, 17, 18, 19 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/FoundationHost/FoundationTools.cs) |
| `ReadPlcWatchTableCurrentValuesReadOnly` | `GetPlcWatchTableCurrentValuesReadOnly` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ReadPlcWebDiagnostics` | `GetPlcWebDiagnostics` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadPlcWebVars` | `GetPlcWebVars` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadPortalInfo` | `GetPortalInfo` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| `ReadProjectProtection` | `GetProjectProtection` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ReadProjectSettings` | `GetProjectSettings` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ReadProjectUserManagement` | `GetProjectUserManagement` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ReadSafetyActivationTests` | `ListSafetyActivationTests` | 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ReadSafetyBlockSignatures` | `GetSafetyBlockSignatures` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ReadSiVArcRules` | `ListSivarcRules` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) |
| `ReadSivarcBlockDefinitions` | `ListSivarcBlockDefinitions` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ReadSivarcRuleTree` | `GetSivarcRuleTree` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ReadTechnologyObjectTree` | `GetTechnologyObjectTree` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ReadTestSuiteCases` | `ListTestSuiteCases` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `ReadToolBatch` | `RunReadOnlyToolBatch` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/McpServer.Batch.cs) |
| `ReadTransferAreas` | `ListTransferAreas` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadTransferRoutes` | `ListTransferRoutes` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ReadUnifiedAlarmCommon` | `GetUnifiedAlarmCommon` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ReadUnifiedAuditSettings` | `GetUnifiedAuditSettings` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ReadUnifiedEngineeringObjects` | `ListUnifiedEngineeringObjects` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs) |
| `ReadUnifiedFaceplateInstance` | `GetUnifiedFaceplateInstance` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ReadUnifiedGlobalScript` | `GetUnifiedGlobalScript` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ReadUnifiedGraphicSelection` | `GetUnifiedGraphicSelection` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs) |
| `ReadUnifiedHmiButtonEvent` | `GetUnifiedHmiButtonEvent` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `ReadUnifiedHmiDynamization` | `GetUnifiedHmiDynamization` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs) |
| `ReadUnifiedHmiTexts` | `GetUnifiedHmiTexts` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ReadUnifiedLibraryType` | `GetUnifiedLibraryType` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ReadUnifiedObjectEvents` | `GetUnifiedObjectEvents` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ReadUnifiedObjectProperties` | `GetUnifiedObjectProperties` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ReadUnifiedPlantObject` | `GetUnifiedPlantObject` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ReadUnifiedRuntimeAlarms` | `GetUnifiedRuntimeAlarms` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadUnifiedRuntimeSettings` | `GetUnifiedRuntimeSettings` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs) |
| `ReadUnifiedRuntimeTags` | `GetUnifiedRuntimeTags` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadUnifiedScreenBranch` | `GetUnifiedScreenBranch` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ReadUnifiedTagDefinitions` | `ListUnifiedTagDefinitions` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ReadV21EcosystemCatalog` | `GetV21EcosystemCatalog` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/V21EcosystemTools.cs) |
| `RebuildReleaseHandoffArtifacts` | `BuildReleaseHandoffArtifacts` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `ReleaseUnifiedReadCursor` | `ReleaseUnifiedReadCursor` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `RenderPlcBlock` | `RenderPlcBlock` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 4.0 新增；P6-48；操作分类 FILE；[源码](../../src/Shared/Host/EcosystemTools.cs) |
| `RenderPlcBlockDocument` | `RenderPlcBlockDocument` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/PlcDocumentationTools.cs) |
| `RenderPlcProgramAtlas` | `RenderPlcProgramAtlas` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 4.0 新增；P6-48；操作分类 FILE；[源码](../../src/Shared/Host/EcosystemTools.cs) |
| `RenderPlcVisualDiff` | `RenderPlcVisualDiff` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/EcosystemTools.cs) |
| `RepairAndReimportBlock` | `RepairAndReimportPlcBlock` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `ResolveSivarcExpression` | `ResolveSivarcExpression` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `RestartOpennessWorker` | `RestartOpennessWorker` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/McpServer.Worker.cs) |
| `RetrieveProjectArchive` | `RetrieveProjectArchive` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `RunCapabilitySelfTest` | `RunCapabilitySelfTest` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DiagnosticsTools.cs) |
| `RunClassicHmiOfflineValidationSuite` | `RunClassicHmiOfflineValidationSuite` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `RunClassicHmiTemporaryImportPreflight` | `RunClassicHmiTemporaryImportPreflight` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `RunHmiActionScriptRecipeSafetySelfTest` | `RunHmiActionScriptRecipeSafetySelfTest` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HmiOfflineTools.cs) |
| `RunHmiTemplatePlcSyncPrecheckSuite` | `RunHmiTemplatePlcSyncPrecheckSuite` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `RunOfflineReleaseValidationSuite` | `RunOfflineReleaseValidationSuite` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `RunOnlineMonitoringSafetySelfTest` | `RunOnlineMonitoringSafetySelfTest` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HostMetaTools.cs) |
| `RunPlcCompanionTool` | `RunPlcCompanionTool` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/EcosystemTools.cs) |
| `RunPlcSimAdvancedTestScenario` | `RunPlcSimAdvancedTestScenario` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `RunTestSuiteCase` | `RunTestSuiteCase` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `RunToolsInTransaction` | `RunToolTransaction` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `SamplePlcLiveValuesS7` | `SamplePlcLiveValuesS7` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `SaveAsProject` | `SaveProjectCopy` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `SaveExport` | `SaveExportContent` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/ExportTools.cs) |
| `SaveProject` | `SaveProject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ScaffoldProject` | `BuildProjectScaffold` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ScanAccessibleDevices` | `ScanAccessibleDevices` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ScanPlcSourceAnnotations` | `ScanPlcSourceAnnotations` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `SearchHardwareCatalog` | `SearchHardwareCatalog` | 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `SearchInstalledGsdDevices` | `SearchInstalledGsdDevices` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `SeedProjectFromReference` | `SeedProjectFromReference` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| `SetCpuCommonSettings` | `SetPlcCpuSettings` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `SetDeviceItemAttribute` | `SetDeviceItemAttribute` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareDevicesTools.cs) |
| `SetDeviceItemIoAddress` | `SetDeviceItemIoAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/HardwareAddressTools.cs) |
| `SetOpcUaInterfaceEnabled` | `SetOpcUaInterfaceEnabled` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `SetPlcUnitObjectAccess` | `SetPlcUnitObjectAccess` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitManagementTools.cs) |
| `SetPlcWebOperatingMode` | `SetPlcWebOperatingMode` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `SetPutGetAccess` | `SetPlcPutGetAccess` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `SetUnifiedHmiButtonEventScriptCode` | `SetUnifiedHmiButtonEventScriptCode` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `SetUnifiedLogDuration` | `SetUnifiedLogDuration` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `SetWatchTableModifyValue` | `SetPlcWatchTableModifyValue` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `ShowObjectInEditor` | `ShowObjectInEditor` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `StageImportFiles` | `StageImportFiles` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 4.0 新增；P6-67；操作分类 FILE；[源码](../../src/Engine/ModelContextProtocol/Tools/ImportStagingTools.cs) |
| `SyncVersionControlWorkspace` | `SynchronizeVersionControlWorkspace` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) |
| `SynchronizeLibrary` | `SynchronizeLibrary` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `TraceTagCause` | `TraceTagCause` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `TraceTagCauseLive` | `TraceTagCauseLive` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `UnifiedOpenPipeRequest` | `InvokeUnifiedOpenPipe` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `UpdateDeviceAddress` | `SetDeviceAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareAddressTools.cs) |
| `UpdateDeviceItemChannel` | `SetDeviceItemChannel` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/HardwareNetworkTools.cs) |
| `UpdatePlcProgram` | `SetPlcProgram` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs) |
| `UpdateUnifiedGlobalScript` | `SetUnifiedGlobalScript` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/GlobalScriptEditTools.cs) |
| `UpdateUnifiedMultilingualProperty` | `SetUnifiedMultilingualProperty` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedObjectProperties` | `SetUnifiedObjectProperties` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedPlantObject` | `SetUnifiedPlantObject` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedRuntimeSettings` | `SetUnifiedRuntimeSettings` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs) |
| `UpgradeSivarcDefinitions` | `UpgradeSivarcDefinitions` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `UploadDeviceParameters` | `UploadDeviceParameters` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `UploadStationFromPlc` | `UploadStationFromPlc` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ValidateAutomationContext` | `ValidateAutomationContext` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs) |
| `ValidateClassicHmiMinimalPackageFiles` | `ValidateClassicHmiMinimalPackageFiles` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `ValidateClassicHmiMinimalPackagePlcSync` | `ValidateClassicHmiMinimalPackagePlcSync` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `ValidatePlcXmlSchemas` | `ValidatePlcDocumentSchemas` | 20, 21 | 规则：动词、对象、领域、复数或大小写/表示规范化；[源码](../../src/Shared/Host/V21EcosystemTools.cs) |
| `ValidateUnifiedObject` | `ValidateUnifiedObject` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `WriteClassicHmiMinimalPackageFiles` | `WriteClassicHmiMinimalPackageFiles` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `WritePlcSclSourceFile` | `WritePlcSclSourceFile` | 20, 21 | 不变；符合命名规则；[源码](../../src/Shared/PlcOfflineTools.cs) |
| `WritePlcSimAdvancedTags` | `WritePlcSimAdvancedTags` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `WritePlcWebVars` | `WritePlcWebVars` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `WriteUnifiedRuntimeTags` | `WriteUnifiedRuntimeTags` | 20, 21 | 不变；符合命名规则；[源码](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |

| 合并来源 | 参数映射（仅文档） |
|---|---|
| `GetAuthoringGuide` | topic trim/lower 后：workflow→exampleId=sequence/connect-project；openness-workflow→query=openness-base；startdrive-bico→toolName=ManageStartdriveParameter,operation=read；hmi→language=hmi-javascript；errors→空选择；其余→language=原 topic。offset=0,limit=80。 |
| `GetRecipe` | topic trim 后非空→exampleId=sequence/<精确目录 topic>；空→exampleKind=sequence（新增可选枚举过滤器，默认 all）；按当前发布版过滤目录；保留 purpose/preconditions/steps/expect/notes；未知 topic→NOT_FOUND。 |
| `GetToolUsage` | toolName 按 A 表转换；query/documentId/offset/limit/operation/language/exampleId 同名；新增 exampleKind=all\|sequence\|language 默认 all；旧默认列表仍含 tools、languages、examples。 |

每版仅以该版已有来源构造目标目录；每个来源的 action、targetKind、输出版本门禁逐项保留。Compile/Connect 的相似名字不构成同义证明。合并后的 data 由现有目录记录投影；空配方列表须按 exampleKind=sequence 过滤，不把所有示例当配方。

</details>

<details>
<summary>B. 全部 …Json 与 PlcBuildAndImport.json 的类型目标</summary>

| 当前工具 | 发布键 | 参数 | 当前 schema 类型 | 族 | 4.0 类型 | 来源 |
|---|---|---|---|---|---|---|
| `ApplyUnifiedHmiLayout` | 20, 21 | `layoutJson` → `layout` | string | H | `UnifiedLayoutSpec` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ApplyUnifiedHmiScreenDesignJson` | 20, 21 | `designJson` → `design` | string | H | `UnifiedScreenSpec` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `ApplyUnifiedHmiTheme` | 20, 21 | `themeJson` → `theme` | string | H | `UnifiedThemeSpec` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs) |
| `AuditEngineeringExports` | 20, 21 | `rulesJson` → `rules` | string | X | `XPathRule[]` | [入口及校验调用](../../src/Shared/Host/QualityAuditTools.cs) |
| `BuildClassicHmiMinimalPackage` | 20, 21 | `packageJson` → `package` | string | H | `ClassicPackageSpec` | [入口及校验调用](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildClassicHmiScreenXml` | 20, 21 | `designJson` → `design` | string | H | `ClassicScreenSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildClassicHmiTagTableXml` | 20, 21 | `tableJson` → `table` | string | H | `ClassicTagTableSpec` | [入口及校验调用](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `BuildDeviceAmlDocument` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `specJson` → `spec` | string | H | `DeviceAmlSpec` | [入口及校验调用](../../src/Shared/HardwareAmlTools.cs) |
| `BuildFlgNetCallXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `flgNetJson` → `flgNet` | string | B | `FlgNetCallSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcGlobalDbXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `globalDbJson` → `globalDb` | string | B | `GlobalDbSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcTagTableXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `tagTableJson` → `tagTable` | string | B | `PlcTagTableSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildPlcUdtXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `udtJson` → `udt` | string | B | `UdtSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildStructuredTextXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `structuredTextJson` → `structuredText` | string | B | `StructuredTextSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `BuildUnifiedHmiLayoutDesignJson` | 20, 21 | `layoutJson` → `layout` | string | H | `UnifiedLayoutSpec` | [入口及校验调用](../../src/Shared/HmiOfflineTools.cs) |
| `BuildUnifiedHmiThemeDesignJson` | 20, 21 | `themeJson` → `theme` | string | H | `UnifiedThemeSpec` | [入口及校验调用](../../src/Shared/HmiOfflineTools.cs) |
| `CallTool` | 20, 21 | `argumentsJson` → `arguments` | None | C | `ToolArguments(target inputSchema)` | [入口及校验调用](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `CompareProjects` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareProjects` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareProjects` | 20, 21 | `targetDevicePathJson` → `targetDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareProjects` | 20, 21 | `targetItemPathJson` → `targetItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `CompareUnifiedGraphicSelections` | 20, 21 | `afterPagesJson` → `afterPages` | string | X | `GraphicSelectionPage[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs) |
| `CompareUnifiedGraphicSelections` | 20, 21 | `beforePagesJson` → `beforePages` | string | X | `GraphicSelectionPage[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs) |
| `CompileDevice` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `CompileDevice` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `ComposePlcAliasAlarmLad` | 20, 21 | `rowsJson` → `rows` | string | D | `PlcAliasRow[]` | [入口及校验调用](../../src/Shared/Host/TemplateTools.cs) |
| `ComposePlcFbBlockXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `fbBlockJson` → `fbBlock` | string | B | `FbBlockSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `ComposePlcFcBlockXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `fcBlockJson` → `fcBlock` | string | B | `FcBlockSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `ComposePlcLadFcBlockXml` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `ladFcBlockJson` → `ladFcBlock` | string | B | `LadFcBlockSpec` | [入口及校验调用](../../src/Shared/Host/XmlBuilderTools.cs) |
| `DownloadPlcToFolder` | 20, 21 | `promptAnswersJson` → `promptAnswers` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `DownloadToPlc` | 20, 21 | `promptAnswersJson` → `promptAnswers` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ExchangeCfcCharts` | 20, 21 | `chartNamesJson` → `chartNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs) |
| `ExchangePlcAlarmTextListsXlsx` | 20, 21 | `culturesJson` → `cultures` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExchangePlcAlarmTextListsXlsx` | 20, 21 | `textListNamesJson` → `textListNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ExchangeSystemDiagnosticsSettings` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ExchangeSystemDiagnosticsSettings` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ExchangeUnifiedTags` | 20, 21 | `expectedTagNamesJson` → `expectedTagNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs) |
| `ExtractPlcBlockMetrics` | 20, 21 | `extensionsJson` → `extensions` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `GenerateOpcUaModelledInterface` | 20, 21 | `accessLevelsJson` → `accessLevels` | string | L | `map<string,int32>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `GeneratePlcDocumentation` | 20, 21 | `extensionsJson` → `extensions` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/PlcDocumentationTools.cs) |
| `GeneratePlcLoadableFile` | 20, 21 | `objectPathsJson` → `objectPaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `GeneratePlcSourceFromBlocks` | 20, 21 | `blockPathsJson` → `blockPaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `GenerateSiVArc` | 20, 21 | `additionalHmiDeviceNamesJson` → `additionalHmiDeviceNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `GenerateSiVArc` | 20, 21 | `plcSoftwarePathsJson` → `plcSoftwarePaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `GetUnifiedCrossReferences` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ImportPlcAlarmInstanceTexts` | 20, 21 | `culturesJson` → `cultures` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs) |
| `ImportSinumerikAlarmTexts` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ImportSinumerikAlarmTexts` | 20, 21 | `filesJson` → `files` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ImportUnifiedEngineeringList` | 20, 21 | `expectedNamesJson` → `expectedNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs) |
| `InstantiatePlcXmlTemplates` | 20, 21 | `rowsJson` → `rows` | string | D | `TemplateRow[]` | [入口及校验调用](../../src/Shared/Host/TemplateTools.cs) |
| `LintPlcSclSource` | 20, 21 | `rulesJson` → `rules` | string | X | `LintRules` | [入口及校验调用](../../src/Shared/Host/PlcDocumentationTools.cs) |
| `ManageClassicHmiCycle` | 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageClassicHmiScript` | 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageClassicHmiTextGraphicList` | 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageCommunicationConnection` | 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageCommunicationConnection` | 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageCommunicationConnection` | 21 | `localInterfaceItemPathJson` → `localInterfaceItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageCommunicationConnection` | 21 | `partnerDevicePathJson` → `partnerDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageCommunicationConnection` | 21 | `partnerInterfaceItemPathJson` → `partnerInterfaceItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageCommunicationConnection` | 21 | `partnerItemPathJson` → `partnerItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDcbLibraries` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDcbLibraries` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccBlock` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccBlock` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccBlock` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChart` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChart` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChart` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartInterface` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartInterface` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartInterface` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartPartition` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartPartition` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccChartPartition` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccPin` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccPin` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccPin` | 20, 21 | `partnerJson` → `partner` | string | X | `DccPartnerSpec(action)` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDccPin` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ManageDeviceServiceObjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDeviceServiceObjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDeviceServiceObjects` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageDeviceUsers` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs) |
| `ManageDeviceUsers` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs) |
| `ManageDeviceUsers` | 20, 21 | `permissionsJson` → `permissions` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs) |
| `ManageDriveFunctions` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveFunctions` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveFunctions` | 20, 21 | `valueJson` → `value` | string | V | `NativeValue` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveHardwareModule` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveHardwareModule` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSafetyAcceptanceTest` | 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSafetyAcceptanceTest` | 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSecurity` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveSecurity` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveTelegrams` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageDriveTelegrams` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageHardwareObject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `destinationDevicePathJson` → `destinationDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareManagementTools.cs) |
| `ManageHardwareObject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `destinationItemPathJson` → `destinationItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareManagementTools.cs) |
| `ManageHardwareObject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareManagementTools.cs) |
| `ManageHardwareObject` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareManagementTools.cs) |
| `ManageHardwareUtilities` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageHardwareUtilities` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ManageIoSystem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageIoSystem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageIoSystem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageIoSystem` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageLibraryType` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageLibraryType` | 20, 21 | `scopeSoftwarePathsJson` → `scopeSoftwarePaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `ManageMotionAxis` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageMotionAxis` | 20, 21 | `targetJson` → `target` | string | X | `MotionTarget` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManageNetworkDomain` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageNetworkDomain` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `participantDevicePathJson` → `participantDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageNetworkDomain` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `participantItemPathJson` → `participantItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageNetworkDomain` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageOnlineDriveFunctions` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageOnlineDriveFunctions` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageOpcUaAccessControl` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs) |
| `ManagePasswordPolicy` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManagePlcCertificate` | 20, 21 | `assignmentItemPathJson` → `assignmentItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcCertificate` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcCertificate` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcCertificate` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcCertificate` | 20, 21 | `subjectAlternativeNamesJson` → `subjectAlternativeNames` | string | S | `SubjectAlternativeName[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs) |
| `ManagePlcGitRepository` | 20, 21 | `filesJson` → `files` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs) |
| `ManagePlcProtection` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `ManagePlcProtection` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `ManagePlcSafety` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ManagePlcSoftwareUnit` | 20, 21 | `commentsJson` → `comments` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManagePlcSoftwareUnit` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManagePlcSupervision` | 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) |
| `ManagePlcTagDefinition` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs) |
| `ManagePortInterconnection` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManagePortInterconnection` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManagePortInterconnection` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `partnerDevicePathJson` → `partnerDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManagePortInterconnection` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `partnerItemPathJson` → `partnerItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageProjectCompilationSettings` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs) |
| `ManageProjectUserManagement` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ManageProjectUserManagement` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ManageSafetyActivationTest` | 21 | `groupPathJson` → `groupPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyActivationTestGroup` | 21 | `groupPathJson` → `groupPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunction` | 21 | `groupPathJson` → `groupPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunction` | 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunctionCondition` | 21 | `groupPathJson` → `groupPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyFunctionCondition` | 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ManageSafetyGlobalSettings` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs) |
| `ManageSiVArcRule` | 20, 21 | `collectionPathJson` → `collectionPath` | string | P | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) |
| `ManageSiVArcRule` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) |
| `ManageSinumerikArchive` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSinumerikArchive` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSinumerikArchive` | 20, 21 | `modifiedDevicePathJson` → `modifiedDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSinumerikArchive` | 20, 21 | `modifiedItemPathJson` → `modifiedItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSinumerikSafetyMode` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) |
| `ManageSivarcBlockDefinition` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcBlockDefinition` | 20, 21 | `textsJson` → `texts` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcTableRule` | 20, 21 | `deviceNamesJson` → `deviceNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcTableRule` | 20, 21 | `deviceSelectionJson` → `deviceSelection` | string | X | `map<string,bool>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcTableRule` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageSivarcTableRule` | 20, 21 | `referencesJson` → `references` | string | X | `map<string,SivarcReference\|null>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ManageStartdriveParameter` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageStartdriveParameter` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageStartdriveParameter` | 20, 21 | `valueJson` → `value` | string | V | `NativeValue` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageSyslogServers` | 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageSyslogServers` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageSyslogServers` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageSyslogServers` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) |
| `ManageTeamcenterWorkflow` | 20, 21 | `customAttributesJson` → `customAttributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTeamcenterWorkflow` | 20, 21 | `itemDetailsJson` → `itemDetails` | string | X | `TeamcenterItemSpec` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTeamcenterWorkflow` | 20, 21 | `revisionDetailsJson` → `revisionDetails` | string | X | `TeamcenterRevisionSpec` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) |
| `ManageTechnologyExtensions` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageTechnologyExtensions` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ManageTechnologyObject` | 20, 21 | `valueJson` → `value` | string | V | `NativeValue` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) |
| `ManageTestSuiteCase` | 20, 21 | `scopeJson` → `scope` | string | X | `TestScope[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `partnerDevicePathJson` → `partnerDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `partnerItemPathJson` → `partnerItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `targetDevicePathJson` → `targetDevicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageTransferArea` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `targetItemPathJson` → `targetItemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ManageUnifiedDynamization` | 20, 21 | `mappingEntriesJson` → `mappingEntries` | string | X | `DynamizationMapping[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedDynamization` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedDynamization` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedEngineeringObject` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs) |
| `ManageUnifiedEvent` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs) |
| `ManageUnifiedEvent` | 20, 21 | `scriptPropertiesJson` → `scriptProperties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs) |
| `ManageUnifiedListEntries` | 20, 21 | `entryJson` → `entry` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedLoggingTag` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedLoggingTag` | 20, 21 | `tagPathJson` → `tagPath` | string | P | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedObjectParts` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedObjectParts` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedPlantNode` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ManageUnifiedScreenItem` | 20, 21 | `propertiesJson` → `properties` | string | M | `CompositeAttributeMap` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs) |
| `ManageUnifiedScreenLayout` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageUnifiedScreenLayout` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ManageWatchForceTableWebAccess` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `ManageWatchForceTableWebAccess` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs) |
| `PatchPlcBlockDocument` | 20, 21 | `changesJson` → `changes` | string | D | `BlockEdit[]` | [入口及校验调用](../../src/Shared/PlcOfflineTools.cs) |
| `PlanArtifactImportOrder` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `artifactsJson` → `artifacts` | string | D | `Artifact[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs) |
| `PlanGlobalLibraryTemplateReuse` | 20, 21 | `templateIntentJson` → `templateIntent` | string | X | `TemplateIntent` | [入口及校验调用](../../src/Shared/HmiOfflineTools.cs) |
| `PlanHardwareNetworkConfiguration` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `planJson` → `plan` | string | D | `NetworkPlan` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `PlanOnlineReadOnlyDataProvider` | 20, 21 | `optionsJson` → `options` | string | X | `MonitoringOptions` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `PlanOnlineReadOnlyDataProvider` | 20, 21 | `tagPathsJson` → `tagPaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `PlanOnlineReadOnlyMonitoring` | 20, 21 | `tagPathsJson` → `tagPaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs) |
| `PlcBuildAndImport` | 20, 21 | `json` → `spec` | string | B | `PlcArtifactSpec(kind)` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcBuildTools.cs) |
| `PreflightToolCall` | 20, 21 | `argumentsJson` → `arguments` | None | C | `ToolArguments(target inputSchema)` | [入口及校验调用](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| `PreviewToolBatch` | 20, 21 | `operationsJson` → `operations` | string | C | `ToolCall[]` | [入口及校验调用](../../src/Shared/Host/McpServer.Batch.cs) |
| `ReadCommunicationConnections` | 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadCommunicationConnections` | 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadDccCharts` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDccCharts` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDccObject` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDccObject` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDccObject` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs) |
| `ReadDeviceAddressing` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `ReadDeviceAddressing` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `ReadDeviceItemChannels` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributeNamesJson` → `attributeNames` | string | S | `string[]` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadDeviceItemChannels` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadDeviceItemChannels` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadDriveObjects` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveObjects` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveParameters` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveParameters` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveParameters` | 20, 21 | `namesJson` → `names` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadDriveParameters` | 20, 21 | `numbersJson` → `numbers` | string | N | `ParameterRef[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadHardwareFeatures` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadHardwareFeatures` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareServicesPortTools.cs) |
| `ReadIoSystems` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadIoSystems` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadObjectIdentifier` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ReadObjectIdentifier` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ReadOnlineDriveParameters` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadOnlineDriveParameters` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadOnlineDriveParameters` | 20, 21 | `namesJson` → `names` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadOnlineDriveParameters` | 20, 21 | `numbersJson` → `numbers` | string | N | `ParameterRef[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs) |
| `ReadPlcLiveValuesOpcUa` | 20, 21 | `nodeIdsJson` → `nodeIds` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ReadPlcLiveValuesS7` | 20, 21 | `itemsJson` → `items` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ReadPlcSimAdvancedTags` | 20, 21 | `namesJson` → `names` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `ReadPlcWebVars` | 20, 21 | `varsJson` → `vars` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadProjectUserManagement` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ReadProjectUserManagement` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs) |
| `ReadSafetyActivationTests` | 21 | `groupPathJson` → `groupPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs) |
| `ReadSiVArcRules` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs) |
| `ReadToolBatch` | 20, 21 | `operationsJson` → `operations` | string | C | `ToolCall[]` | [入口及校验调用](../../src/Shared/Host/McpServer.Batch.cs) |
| `ReadTransferAreas` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadTransferAreas` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `ReadUnifiedGraphicSelection` | 20, 21 | `itemNamesJson` → `itemNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs) |
| `ReadUnifiedObjectEvents` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs) |
| `ReadUnifiedObjectProperties` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ReadUnifiedPlantObject` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ReadUnifiedRuntimeAlarms` | 20, 21 | `systemNamesJson` → `systemNames` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadUnifiedRuntimeSettings` | 20, 21 | `fieldsJson` → `fields` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs) |
| `ReadUnifiedRuntimeTags` | 20, 21 | `tagsJson` → `tags` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `ReadUnifiedScreenBranch` | 20, 21 | `branchJson` → `branch` | string | P | `BranchStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs) |
| `ResolveSivarcExpression` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `ResolveSivarcExpression` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs) |
| `RunPlcCompanionTool` | 20, 21 | `argumentsJson` → `arguments` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/EcosystemTools.cs) |
| `RunPlcSimAdvancedTestScenario` | 20, 21 | `scenarioJson` → `scenario` | string | D | `PlcSimScenario` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `RunTestSuiteCase` | 20, 21 | `namesJson` → `names` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs) |
| `RunToolsInTransaction` | 20, 21 | `callsJson` → `calls` | string | C | `ToolCall[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `SamplePlcLiveValuesS7` | 20, 21 | `itemsJson` → `items` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) |
| `ScanPlcSourceAnnotations` | 20, 21 | `extensionsJson` → `extensions` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `ScanPlcSourceAnnotations` | 20, 21 | `markersJson` → `markers` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/OfflineAnalysisTools.cs) |
| `SetCpuCommonSettings` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `settingsJson` → `settings` | string | M | `CpuSettings{exactAttributes:AttributeMap<Scalar>}` | [入口及校验调用](../../src/Shared/HardwareDevicesTools.cs) |
| `SetUnifiedLogDuration` | 20, 21 | `durationPathJson` → `durationPath` | string | P | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `ShowObjectInEditor` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `ShowObjectInEditor` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs) |
| `SynchronizeLibrary` | 20, 21 | `harmonizeOptionsJson` → `harmonizeOptions` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `SynchronizeLibrary` | 20, 21 | `scopeSoftwarePathsJson` → `scopeSoftwarePaths` | string | S | `string[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `SynchronizeLibrary` | 20, 21 | `selectionJson` → `selection` | string | X | `LibrarySelection[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs) |
| `UnifiedOpenPipeRequest` | 20, 21 | `requestJson` → `request` | string | X | `OpenPipeRequest(message)` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `UpdateDeviceAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `UpdateDeviceAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `UpdateDeviceAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `UpdateDeviceAddress` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareAddressTools.cs) |
| `UpdateDeviceItemChannel` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `attributesJson` → `attributes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `UpdateDeviceItemChannel` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `UpdateDeviceItemChannel` | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Shared/HardwareNetworkTools.cs) |
| `UpdateUnifiedMultilingualProperty` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedObjectProperties` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedObjectProperties` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedPlantObject` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedPlantObject` | 20, 21 | `propertiesJson` → `properties` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `UpdateUnifiedRuntimeSettings` | 20, 21 | `changesJson` → `changes` | string | M | `AttributeMap<Scalar>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs) |
| `UploadDeviceParameters` | 20, 21 | `devicePathJson` → `devicePath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `UploadDeviceParameters` | 20, 21 | `itemPathJson` → `itemPath` | string | P | `string[]（路径段）` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `UploadDeviceParameters` | 20, 21 | `promptAnswersJson` → `promptAnswers` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `UploadStationFromPlc` | 20, 21 | `promptAnswersJson` → `promptAnswers` | string | L | `map<string,string>` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs) |
| `ValidateClassicHmiMinimalPackagePlcSync` | 20, 21 | `plcSymbolsJson` → `plcSymbols` | string | S | `string[]` | [入口及校验调用](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `ValidateUnifiedObject` | 20, 21 | `objectPathJson` → `objectPath` | string | R | `PropertyStep[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) |
| `WriteClassicHmiMinimalPackageFiles` | 20, 21 | `packageJson` → `package` | string | H | `ClassicPackageSpec` | [入口及校验调用](../../src/Shared/Host/OfflineSuiteTools.cs) |
| `WritePlcSimAdvancedTags` | 20, 21 | `valuesJson` → `values` | string | W | `WriteValue[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| `WritePlcWebVars` | 20, 21 | `writesJson` → `writes` | string | W | `WriteValue[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |
| `WriteUnifiedRuntimeTags` | 20, 21 | `writesJson` → `writes` | string | W | `WriteValue[]` | [入口及校验调用](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs) |

按 (当前工具,参数) 去重：B=9；C=5；D=6；H=10；L=7；M=50；N=2；P=128；R=14；S=39；V=3；W=3；X=16；共 292 项 / 167 个工具。argumentsJson 的 JsonElement 输入也迁名；…JsonPath 仍为文件路径。

</details>

<details>
<summary>B1. 当前输入限制原文（生成提取；迁移不得放宽）</summary>

| 输入 | 发布键 | 当前参数约束原文 | 其他 schema 约束 |
|---|---|---|---|
| ApplyUnifiedHmiLayout.layoutJson | 20, 21 | layoutJson: JSON accepted by BuildUnifiedHmiLayoutDesignJson. | {} |
| ApplyUnifiedHmiScreenDesignJson.designJson | 20, 21 | designJson: JSON object with optional screen properties and items array | {} |
| ApplyUnifiedHmiTheme.themeJson | 20, 21 | themeJson: JSON accepted by BuildUnifiedHmiThemeDesignJson. | {} |
| AuditEngineeringExports.rulesJson | 20, 21 | JSON array of explicit XPath count/value policies; unmatched rules are unevaluated. | {} |
| BuildClassicHmiMinimalPackage.packageJson | 20, 21 | packageJson: JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through Tag/HmiTag/ProcessValueTag or Properties.*Tag. | {} |
| BuildClassicHmiScreenXml.designJson | 20, 21 | designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties. | {} |
| BuildClassicHmiTagTableXml.tableJson | 20, 21 | tableJson: JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and ControllerTag/PlcTag. | {} |
| BuildDeviceAmlDocument.specJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | specJson: JSON object describing the document to build (see the tool description). | {} |
| BuildFlgNetCallXml.flgNetJson | 14sp1, 15.1, 16, 17, 18, 19 | Bounded call {callName,parameters:[]} or block {blockName,blockNumber,networks:[{callJson:call}]}; optional inputs/outputs. Exact Input/Output directions; constant source must be explicit; omitted sourceKind means global; simple symbol components. 64 networks, 1000 total parameters, 1000 interface members, strings <=4096. No raw XML, paths, caller UIds or other LAD elements. See legacy-offline-ladder-candidate.md. | {"maxLength": 262144} |
| BuildFlgNetCallXml.flgNetJson | 20, 21 | flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value. | {} |
| BuildPlcGlobalDbXml.globalDbJson | 14sp1, 15.1, 16, 17, 18, 19 | JSON {dbName,dbNumber:positive integer,staticMembers:[{name,datatype,externalWritable?:boolean,commentZhCn?:string,startValue?:string}]}; flat members, 1..1000 rows, strings <=4096. Aliases documented in legacy-offline-composition-candidate.md. | {"maxLength": 262144} |
| BuildPlcGlobalDbXml.globalDbJson | 20, 21 | globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[]. | {} |
| BuildPlcTagTableXml.tagTableJson | 14sp1, 15.1, 16, 17, 18, 19 |  | {"maxLength": 262144} |
| BuildPlcTagTableXml.tagTableJson | 20, 21 | tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address. | {} |
| BuildPlcUdtXml.udtJson | 14sp1, 15.1, 16, 17, 18, 19 |  | {"maxLength": 262144} |
| BuildPlcUdtXml.udtJson | 20, 21 | udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment. | {} |
| BuildStructuredTextXml.structuredTextJson | 14sp1, 15.1, 16, 17, 18, 19 | JSON {firstUid?:1..1000000000,operations:[{op,...}]}; if/elsif/else/endif, assignment (target and exactly one source or value), token, blank, newline, global/local/symbol/literal, line items. 1..1000 rows; strings <=4096; no unknown, duplicate or conflicting fields. Candidate generation, not SCL validation. | {"maxLength": 262144} |
| BuildStructuredTextXml.structuredTextJson | 20, 21 | structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text. | {} |
| BuildUnifiedHmiLayoutDesignJson.layoutJson | 20, 21 | layoutJson: JSON {grid?,left?,top?,gap?,columns?,cellWidth?,cellHeight?,items:[{name,type?,row?,col?,rowSpan?,colSpan?,text?,properties?}]}. | {} |
| BuildUnifiedHmiThemeDesignJson.themeJson | 20, 21 | themeJson: JSON {name?, palette:{Page?,Surface?,Text?,Border?,...}} with TIA ARGB colors like 0xFFF4F6F8. | {} |
| CallTool.argumentsJson | 20, 21 | argumentsJson: the tool's arguments as a JSON object - either the object itself ({"softwarePath":"PLC_1"}) or that object as a JSON string. Omit for a no-argument tool. Parameters ending in Json (devicePathJson, propertiesJson, ...) may likewise be given as the object/array itself; enum-like values (action, kind, ...) are matched case-insensitively; numbers and booleans are accepted as strings. | {"examples": [{}]} |
| CompareProjects.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| CompareProjects.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| CompareProjects.targetDevicePathJson | 20, 21 | targetDevicePathJson: JSON array naming the target station. | {} |
| CompareProjects.targetItemPathJson | 20, 21 | targetItemPathJson: JSON array of device-item names on the target. | {} |
| CompareUnifiedGraphicSelections.afterPagesJson | 20, 21 | afterPagesJson: JSON array of the pages read after the change. | {} |
| CompareUnifiedGraphicSelections.beforePagesJson | 20, 21 | beforePagesJson: JSON array of the pages read before the change. | {} |
| CompileDevice.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station to compile, e.g. ["PLC_1"]. | {"examples": ["[\"PLC_1\"]"]} |
| CompileDevice.itemPathJson | 20, 21 | itemPathJson: JSON array of device-item names when one item (e.g. the CPU) is to be compiled; [] = the whole station. | {} |
| ComposePlcAliasAlarmLad.rowsJson | 20, 21 | Structured JSON rows; exact shape is specified in the tool description. | {} |
| ComposePlcFbBlockXml.fbBlockJson | 14sp1, 15.1, 16, 17, 18, 19 | JSON {blockName,blockNumber,inputs:[],outputs:[],structuredText:{operations:[...]}}; FB also allows optional inouts/statics/temps. FC requires inputs/outputs arrays (empty allowed); FB interface arrays optional. Flat members {name,datatype,commentZhCn?}; 1000 members total; strings <=4096. Raw XML rejected. Exact aliases and comments documented in legacy-offline-block-composition-candidate.md. | {"maxLength": 262144} |
| ComposePlcFbBlockXml.fbBlockJson | 20, 21 | fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[]. | {} |
| ComposePlcFcBlockXml.fcBlockJson | 14sp1, 15.1, 16, 17, 18, 19 | JSON {blockName,blockNumber,inputs:[],outputs:[],structuredText:{operations:[...]}}; FB also allows optional inouts/statics/temps. FC requires inputs/outputs arrays (empty allowed); FB interface arrays optional. Flat members {name,datatype,commentZhCn?}; 1000 members total; strings <=4096. Raw XML rejected. Exact aliases and comments documented in legacy-offline-block-composition-candidate.md. | {"maxLength": 262144} |
| ComposePlcFcBlockXml.fcBlockJson | 20, 21 | fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[]. | {} |
| ComposePlcLadFcBlockXml.ladFcBlockJson | 14sp1, 15.1, 16, 17, 18, 19 | Bounded call {callName,parameters:[]} or block {blockName,blockNumber,networks:[{callJson:call}]}; optional inputs/outputs. Exact Input/Output directions; constant source must be explicit; omitted sourceKind means global; simple symbol components. 64 networks, 1000 total parameters, 1000 interface members, strings <=4096. No raw XML, paths, caller UIds or other LAD elements. See legacy-offline-ladder-candidate.md. | {"maxLength": 262144} |
| ComposePlcLadFcBlockXml.ladFcBlockJson | 20, 21 | ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level. | {} |
| DownloadPlcToFolder.promptAnswersJson | 20, 21 | promptAnswersJson: JSON object of explicit answers to TIA prompts by prompt type name. | {} |
| DownloadToPlc.promptAnswersJson | 20, 21 | promptAnswersJson: optional JSON object of explicit answers for download prompts by type name, e.g. {"ResetModule":"DeleteAll","OverwriteHmiData":true}. Selection prompts take an enum name, checkbox prompts take true/false. Without an entry, destructive prompts (InitializeMemory, OverwriteOnMemoryCard, OverwriteSystemData, ResetModule, SwitchBackupToPrimary, ProtectionLevelChanged) default to NoAction/NoChange and prompts without a known default stay unanswered; Meta.promptsAnswered / Meta.promptsUnanswered list what happened. | {} |
| ExchangeCfcCharts.chartNamesJson | 20, 21 | chartNamesJson: JSON array of chart paths. | {} |
| ExchangePlcAlarmTextListsXlsx.culturesJson | 20, 21 | culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages). | {} |
| ExchangePlcAlarmTextListsXlsx.textListNamesJson | 20, 21 | textListNamesJson: JSON array of text list names ('[]' = all). | {} |
| ExchangeSystemDiagnosticsSettings.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ExchangeSystemDiagnosticsSettings.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ExchangeUnifiedTags.expectedTagNamesJson | 20, 21 | expectedTagNamesJson: JSON array of tag names expected after the import (verified). | {} |
| ExtractPlcBlockMetrics.extensionsJson | 20, 21 | extensionsJson: JSON array of file extensions to include, e.g. ['.scl','.s7dcl']. | {} |
| GenerateOpcUaModelledInterface.accessLevelsJson | 20, 21 | JSON area-to-level map; see tool description; safety permits only 0 or 1. | {} |
| GeneratePlcDocumentation.extensionsJson | 20, 21 | extensionsJson: JSON array of file extensions to include, e.g. ['.scl','.s7dcl']. | {} |
| GeneratePlcLoadableFile.objectPathsJson | 20, 21 | objectPathsJson: JSON array of object paths. | {} |
| GeneratePlcSourceFromBlocks.blockPathsJson | 20, 21 | blockPathsJson: JSON array of block paths. | {} |
| GenerateSiVArc.additionalHmiDeviceNamesJson | 20, 21 | additionalHmiDeviceNamesJson: JSON array of further HMI device names. | {} |
| GenerateSiVArc.plcSoftwarePathsJson | 20, 21 | plcSoftwarePathsJson: JSON array of PLC software paths included in the generation. | {} |
| GetUnifiedCrossReferences.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ImportPlcAlarmInstanceTexts.culturesJson | 20, 21 | culturesJson: JSON array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages). | {} |
| ImportSinumerikAlarmTexts.devicePathJson | 20, 21 | JSON array of exact device-group/device names. | {} |
| ImportSinumerikAlarmTexts.filesJson | 20, 21 | JSON array of 1..200 existing absolute .ts/.csv alarm-text files. | {} |
| ImportUnifiedEngineeringList.expectedNamesJson | 20, 21 | expectedNamesJson: JSON array of names expected after the import (verified). | {} |
| InstantiatePlcXmlTemplates.rowsJson | 20, 21 | Structured JSON rows; exact shape is specified in the tool description. | {} |
| LintPlcSclSource.rulesJson | 20, 21 | rulesJson: JSON object of lint rules to enable / disable ('{}' = defaults). | {} |
| ManageClassicHmiCycle.attributesJson | 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageClassicHmiScript.attributesJson | 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageClassicHmiTextGraphicList.attributesJson | 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageCommunicationConnection.devicePathJson | 21 | devicePathJson: JSON array naming the station that owns the connections, e.g. ["PLC_1"]. | {} |
| ManageCommunicationConnection.itemPathJson | 21 | itemPathJson: JSON array of device-item names down to the item carrying the CommunicationConnections service (usually the CPU); [] = the station. | {} |
| ManageCommunicationConnection.localInterfaceItemPathJson | 21 | localInterfaceItemPathJson: JSON array path of the local interface item, e.g. ["PROFINET interface_1"]. | {} |
| ManageCommunicationConnection.partnerDevicePathJson | 21 | partnerDevicePathJson: JSON array naming the partner station. | {} |
| ManageCommunicationConnection.partnerInterfaceItemPathJson | 21 | partnerInterfaceItemPathJson: JSON array path of the partner's interface item. | {} |
| ManageCommunicationConnection.partnerItemPathJson | 21 | partnerItemPathJson: JSON array of device-item names on the partner. | {} |
| ManageDcbLibraries.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDcbLibraries.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccBlock.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDccBlock.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccBlock.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDccChart.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDccChart.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccChart.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDccChartInterface.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDccChartInterface.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccChartInterface.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDccChartPartition.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDccChartPartition.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccChartPartition.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDccPin.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDccPin.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDccPin.partnerJson | 20, 21 | partnerJson: JSON object naming the partner pin {block, pin} (or the parameter for updateParameter). | {} |
| ManageDccPin.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDeviceServiceObjects.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDeviceServiceObjects.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDeviceServiceObjects.propertiesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageDeviceUsers.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDeviceUsers.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDeviceUsers.permissionsJson | 20, 21 | permissionsJson: JSON array of permission names (see the tool description). | {} |
| ManageDriveFunctions.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDriveFunctions.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDriveFunctions.valueJson | 20, 21 | valueJson: the value to write, as JSON (number, string, boolean or object as the parameter expects). | {} |
| ManageDriveHardwareModule.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDriveHardwareModule.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDriveSafetyAcceptanceTest.devicePathJson | 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDriveSafetyAcceptanceTest.itemPathJson | 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDriveSecurity.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDriveSecurity.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageDriveTelegrams.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageDriveTelegrams.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageHardwareObject.destinationDevicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | destinationDevicePathJson: for moveItem / copyItem - JSON array naming the destination station. | {} |
| ManageHardwareObject.destinationItemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | destinationItemPathJson: for moveItem / copyItem - JSON array of device-item names of the destination container. | {} |
| ManageHardwareObject.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name, e.g. ["PLC_2"]. | {"examples": ["[\"PLC_2\"]"]} |
| ManageHardwareObject.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names (deleteItem / moveItem / copyItem); [] for deleteDevice. | {} |
| ManageHardwareUtilities.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageHardwareUtilities.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageIoSystem.attributesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageIoSystem.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageIoSystem.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageIoSystem.propertiesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageLibraryType.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageLibraryType.scopeSoftwarePathsJson | 20, 21 | scopeSoftwarePathsJson: JSON array of software paths that limit the update scope ('[]' = whole project). | {} |
| ManageMotionAxis.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageMotionAxis.targetJson | 20, 21 | targetJson: JSON object naming the target (see the tool description). | {} |
| ManageNetworkDomain.attributesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageNetworkDomain.participantDevicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | participantDevicePathJson: JSON array naming the station to add to the domain. | {} |
| ManageNetworkDomain.participantItemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | participantItemPathJson: JSON array of device-item names of the participant's interface. | {} |
| ManageNetworkDomain.propertiesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageOnlineDriveFunctions.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageOnlineDriveFunctions.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageOpcUaAccessControl.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManagePasswordPolicy.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManagePlcCertificate.assignmentItemPathJson | 20, 21 | assignmentItemPathJson: JSON array of device-item names the certificate is assigned to. | {} |
| ManagePlcCertificate.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManagePlcCertificate.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManagePlcCertificate.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManagePlcCertificate.subjectAlternativeNamesJson | 20, 21 | subjectAlternativeNamesJson: JSON array of subject alternative names for the certificate. | {} |
| ManagePlcGitRepository.filesJson | 20, 21 | JSON array of explicit repository-relative file paths. | {} |
| ManagePlcProtection.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station, e.g. ["PLC_1"] (or [group, ..., station]). | {"examples": ["[\"PLC_1\"]"]} |
| ManagePlcProtection.itemPathJson | 20, 21 | itemPathJson: JSON array of device-item names down to the CPU; [] (default) resolves the station's CPU. | {} |
| ManagePlcSafety.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManagePlcSoftwareUnit.commentsJson | 20, 21 | commentsJson: JSON object language tag -> comment text. | {} |
| ManagePlcSoftwareUnit.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManagePlcSupervision.attributesJson | 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManagePlcTagDefinition.propertiesJson | 20, 21 | propertiesJson: JSON object of further scalar properties (ExternalAccessible / ExternalVisible / ExternalWritable / LogicalAddress / DataTypeName; Comment as text or per culture). | {} |
| ManagePortInterconnection.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManagePortInterconnection.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManagePortInterconnection.partnerDevicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | partnerDevicePathJson: JSON array naming the partner station, like devicePathJson. | {} |
| ManagePortInterconnection.partnerItemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | partnerItemPathJson: JSON array of exact device-item names on the partner, like itemPathJson. | {} |
| ManageProjectCompilationSettings.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageProjectUserManagement.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageProjectUserManagement.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageSafetyActivationTest.groupPathJson | 21 | groupPathJson: JSON array path of the group, e.g. ["Folder","Subfolder"]; [] = the root. | {} |
| ManageSafetyActivationTestGroup.groupPathJson | 21 | groupPathJson: JSON array path of the group, e.g. ["Folder","Subfolder"]; [] = the root. | {} |
| ManageSafetyFunction.groupPathJson | 21 | groupPathJson: JSON array path of the group, e.g. ["Folder","Subfolder"]; [] = the root. | {} |
| ManageSafetyFunction.propertiesJson | 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSafetyFunctionCondition.groupPathJson | 21 | groupPathJson: JSON array path of the group, e.g. ["Folder","Subfolder"]; [] = the root. | {} |
| ManageSafetyFunctionCondition.propertiesJson | 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSafetyGlobalSettings.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSiVArcRule.collectionPathJson | 20, 21 | collectionPathJson: JSON array path of the rule collection. | {} |
| ManageSiVArcRule.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSinumerikArchive.devicePathJson | 20, 21 | JSON array of exact device-group/device names. | {} |
| ManageSinumerikArchive.itemPathJson | 20, 21 | JSON array of exact PLC device-item names below the selected device. | {} |
| ManageSinumerikArchive.modifiedDevicePathJson | 20, 21 | JSON array of exact modified PLC device-group/device names. | {} |
| ManageSinumerikArchive.modifiedItemPathJson | 20, 21 | JSON array selecting the modified PLC item for F-address archive. | {} |
| ManageSinumerikSafetyMode.devicePathJson | 20, 21 | JSON array of exact device-group/device names. | {} |
| ManageSivarcBlockDefinition.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSivarcBlockDefinition.textsJson | 20, 21 | textsJson: JSON object language tag -> text. | {} |
| ManageSivarcTableRule.deviceNamesJson | 20, 21 | deviceNamesJson: JSON array of device names. | {} |
| ManageSivarcTableRule.deviceSelectionJson | 20, 21 | deviceSelectionJson: JSON object selecting PLC / HMI devices for the rule. | {} |
| ManageSivarcTableRule.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageSivarcTableRule.referencesJson | 20, 21 | referencesJson: JSON object of library references for the rule (see the tool description). | {} |
| ManageStartdriveParameter.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {"examples": ["[\"<exact drive device>\"]"]} |
| ManageStartdriveParameter.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {"examples": ["[\"<exact drive/control unit item>\"]"]} |
| ManageStartdriveParameter.valueJson | 20, 21 | valueJson: the value to write, as JSON (number, string, boolean or object as the parameter expects). | {} |
| ManageSyslogServers.attributesJson | 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageSyslogServers.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageSyslogServers.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageSyslogServers.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageTeamcenterWorkflow.customAttributesJson | 20, 21 | customAttributesJson: JSON object attribute name -> value. | {} |
| ManageTeamcenterWorkflow.itemDetailsJson | 20, 21 | itemDetailsJson: JSON object of item details (see the tool description). | {} |
| ManageTeamcenterWorkflow.revisionDetailsJson | 20, 21 | revisionDetailsJson: JSON object of revision details. | {} |
| ManageTechnologyExtensions.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageTechnologyExtensions.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageTechnologyObject.valueJson | 20, 21 | valueJson: the value to write, as JSON (number, string, boolean or object as the parameter expects). | {} |
| ManageTestSuiteCase.scopeJson | 20, 21 | scopeJson: JSON object describing the test scope (see the tool description). | {} |
| ManageTransferArea.attributesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| ManageTransferArea.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageTransferArea.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ManageTransferArea.partnerDevicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | partnerDevicePathJson: JSON array naming the partner station, like devicePathJson. | {} |
| ManageTransferArea.partnerItemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | partnerItemPathJson: JSON array of exact device-item names on the partner, like itemPathJson. | {} |
| ManageTransferArea.propertiesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageTransferArea.targetDevicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | targetDevicePathJson: JSON array naming the target station. | {} |
| ManageTransferArea.targetItemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | targetItemPathJson: JSON array of device-item names on the target. | {} |
| ManageUnifiedDynamization.mappingEntriesJson | 20, 21 | mappingEntriesJson: JSON array of mapping entries. | {} |
| ManageUnifiedDynamization.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ManageUnifiedDynamization.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedEngineeringObject.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedEvent.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ManageUnifiedEvent.scriptPropertiesJson | 20, 21 | scriptPropertiesJson: JSON object of script properties to set. | {} |
| ManageUnifiedListEntries.entryJson | 20, 21 | entryJson: JSON object of the entry's properties. | {} |
| ManageUnifiedLoggingTag.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedLoggingTag.tagPathJson | 20, 21 | tagPathJson: JSON array path of the logging tag. | {} |
| ManageUnifiedObjectParts.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ManageUnifiedObjectParts.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedPlantNode.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedScreenItem.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageUnifiedScreenLayout.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ManageUnifiedScreenLayout.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| ManageWatchForceTableWebAccess.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ManageWatchForceTableWebAccess.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| PatchPlcBlockDocument.changesJson | 20, 21 | JSON array of objects with action=setBlockText/setNetworkText/setMemberStartValue, exact target fields, expectedValue and value. The selector key is action. Example: [{"action":"setBlockText","field":"Title","culture":"en-US","expectedValue":"Old title","value":"Example"}]. | {} |
| PlanArtifactImportOrder.artifactsJson | 14sp1, 15.1, 16, 17, 18, 19 |  | {"maxLength": 1048576} |
| PlanArtifactImportOrder.artifactsJson | 20, 21 | JSON array, e.g. [{"Id":"UDT_A"},{"Id":"FB_A","Dependencies":["UDT_A"]}]. IDs are unique ignoring case; dependency IDs must be included. Optional Target and integer Priority order independent artifacts. | {} |
| PlanGlobalLibraryTemplateReuse.templateIntentJson | 20, 21 | templateIntentJson: optional JSON {"screenType":"overview","targetRuntime":"Unified","preferredComponents":[...]}. | {} |
| PlanHardwareNetworkConfiguration.planJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | planJson: JSON with operations[]. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetCpuCommonSettings. This is offline-only and performs validation only. | {} |
| PlanOnlineReadOnlyDataProvider.optionsJson | 20, 21 | optionsJson: optional JSON object such as {"pollMs":1000,"source":"watch-table-export"}. | {} |
| PlanOnlineReadOnlyDataProvider.tagPathsJson | 20, 21 | tagPathsJson: JSON array of declared symbolic PLC tags/DB members. Guessed M bits and unsafe intent names are rejected. | {} |
| PlanOnlineReadOnlyMonitoring.tagPathsJson | 20, 21 | tagPathsJson: JSON array of symbolic PLC tag/member paths, for example ["DB_HMI.MotorRun","DB_HMI.SpeedSet"]. Do not pass guessed M bits. | {} |
| PlcBuildAndImport.json | 20, 21 | json: structured JSON matching the corresponding BuildPlc* tool. | {"examples": ["{\"blockName\":\"FC_DryRun\",\"blockNumber\":12,\"inputs\":[{\"name\":\"Start\",\"datatype\":\"Bool\"}],\"outputs\":[{\"name\":\"Run\",\"datatype\":\"Bool\"}],\"structuredText\":{\"operations\":[{\"op\":\"if\",\"condition\":\"Start\"},{\"op\":\"assignment\",\"target\":\"Run\",\"value\":\"TRUE\",\"indent\":2},{\"op\":\"endif\"}]}}"]} |
| PreflightToolCall.argumentsJson | 20, 21 | argumentsJson: the arguments you intend to send - the JSON object itself or that object as a JSON string. Omit to see the signature, example and prerequisites only. | {"examples": [{"softwarePath": "PLC_1"}]} |
| PreviewToolBatch.operationsJson | 20, 21 | Ordered JSON array of {name,arguments:{...}}; 1..50 operations. | {} |
| ReadCommunicationConnections.devicePathJson | 21 | devicePathJson: JSON array naming the station, e.g. ["PLC_1"]. | {} |
| ReadCommunicationConnections.itemPathJson | 21 | itemPathJson: JSON array of device-item names down to the item with the service; [] = the station. | {} |
| ReadDccCharts.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadDccCharts.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadDccObject.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadDccObject.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadDccObject.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ReadDeviceAddressing.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadDeviceAddressing.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadDeviceItemChannels.attributeNamesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributeNamesJson: JSON array of attribute names to read ('[]' = the documented set). | {} |
| ReadDeviceItemChannels.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadDeviceItemChannels.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadDriveObjects.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {"examples": ["[\"<exact drive device>\"]"]} |
| ReadDriveObjects.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {"examples": ["[\"<exact drive/control unit item>\"]"]} |
| ReadDriveParameters.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {"examples": ["[\"<exact drive device>\"]"]} |
| ReadDriveParameters.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {"examples": ["[\"<exact drive/control unit item>\"]"]} |
| ReadDriveParameters.namesJson | 20, 21 | namesJson: JSON array of exact names (or a comma-separated list where the tool says so). | {"examples": ["[\"p1070[0]\"]"]} |
| ReadDriveParameters.numbersJson | 20, 21 | numbersJson: JSON array of parameter numbers. | {} |
| ReadHardwareFeatures.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadHardwareFeatures.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadIoSystems.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadIoSystems.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadObjectIdentifier.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadObjectIdentifier.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadOnlineDriveParameters.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadOnlineDriveParameters.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadOnlineDriveParameters.namesJson | 20, 21 | namesJson: JSON array of exact names (or a comma-separated list where the tool says so). | {} |
| ReadOnlineDriveParameters.numbersJson | 20, 21 | numbersJson: JSON array of parameter numbers. | {} |
| ReadPlcLiveValuesOpcUa.nodeIdsJson | 20, 21 | nodeIdsJson: JSON array or comma-separated list of OPC UA NodeIds, e.g. ["ns=3;s=\"DB10\".\"X\"","ns=3;s=\"DB10\".\"Y\""]. | {} |
| ReadPlcLiveValuesS7.itemsJson | 20, 21 | itemsJson: JSON array or comma-separated list of absolute S7 addresses, e.g. ["DB10.DBD0:REAL","DB10.DBD4:REAL","M0.0"]. | {} |
| ReadPlcSimAdvancedTags.namesJson | 20, 21 | namesJson: tag names to read (JSON array or comma-separated). Empty = list tags instead. | {"examples": ["[\"MCP_SimDB.Cycles\",\"MCP_SimDB.Running\"]"]} |
| ReadPlcWebVars.varsJson | 20, 21 | varsJson: JSON array of symbolic names, e.g. ["\"DB1\".\"Speed\"", "\"Motor_On\""], or a comma-separated list. Max 500. | {} |
| ReadProjectUserManagement.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadProjectUserManagement.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadSafetyActivationTests.groupPathJson | 21 | groupPathJson: JSON array path of the group, e.g. ["Folder","Subfolder"]; [] = the root. | {} |
| ReadSiVArcRules.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ReadToolBatch.operationsJson | 20, 21 | Ordered JSON array of {name,arguments:{...}}; 1..50 operations. | {} |
| ReadTransferAreas.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ReadTransferAreas.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| ReadUnifiedGraphicSelection.itemNamesJson | 20, 21 | itemNamesJson: JSON array of screen item names. | {} |
| ReadUnifiedObjectEvents.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ReadUnifiedObjectProperties.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ReadUnifiedPlantObject.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| ReadUnifiedRuntimeAlarms.systemNamesJson | 20, 21 | systemNamesJson: JSON array of runtime system names, e.g. ["HMI_RT_1"]; empty array or empty string = all systems. | {} |
| ReadUnifiedRuntimeSettings.fieldsJson | 20, 21 | fieldsJson: JSON array of field names to read ('[]' = all). | {} |
| ReadUnifiedRuntimeTags.tagsJson | 20, 21 | tagsJson: JSON array of runtime tag names, e.g. ["Tag_1","Motor.Speed"], or a comma-separated list. Max 500. | {} |
| ReadUnifiedScreenBranch.branchJson | 20, 21 | branchJson: JSON array path of the screen branch to read. | {} |
| ResolveSivarcExpression.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ResolveSivarcExpression.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| RunPlcCompanionTool.argumentsJson | 20, 21 | JSON string array of exact CLI arguments; no shell syntax. | {} |
| RunPlcSimAdvancedTestScenario.scenarioJson | 20, 21 | scenarioJson: scenario object (see description). Max 500 steps. | {"examples": ["{\"instance\":\"MCP_SIM\",\"mode\":\"default\",\"steps\":[{\"write\":{\"MCP_SimDB.Start\":true}},{\"waitMs\":300},{\"assert\":{\"MCP_SimDB.Running\":true}}]}"]} |
| RunTestSuiteCase.namesJson | 20, 21 | namesJson: JSON array of exact names (or a comma-separated list where the tool says so). | {} |
| RunToolsInTransaction.callsJson | 20, 21 | callsJson: JSON array of {name, arguments:{...}} supported tool calls to run inside one transaction. | {} |
| SamplePlcLiveValuesS7.itemsJson | 20, 21 | itemsJson: JSON array or comma-separated list of absolute S7 addresses, e.g. ["DB10.DBD0:REAL","M0.0"]. | {} |
| ScanPlcSourceAnnotations.extensionsJson | 20, 21 | extensionsJson: JSON array of file extensions to include, e.g. ['.scl','.s7dcl']. | {} |
| ScanPlcSourceAnnotations.markersJson | 20, 21 | markersJson: JSON array of annotation markers to look for, e.g. ['TODO','FIXME']. | {} |
| SetCpuCommonSettings.settingsJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | settingsJson: JSON object { "exactAttributes": { "ExactAttributeNameFromReadback": "value" } }. No aliases or guessed attribute names are accepted. | {} |
| SetUnifiedLogDuration.durationPathJson | 20, 21 | durationPathJson: JSON array path of the duration property. | {} |
| ShowObjectInEditor.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| ShowObjectInEditor.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| SynchronizeLibrary.harmonizeOptionsJson | 20, 21 | harmonizeOptionsJson: JSON object of harmonisation options (see the tool description). | {} |
| SynchronizeLibrary.scopeSoftwarePathsJson | 20, 21 | scopeSoftwarePathsJson: JSON array of software paths that limit the update scope ('[]' = whole project). | {} |
| SynchronizeLibrary.selectionJson | 20, 21 | selectionJson: JSON array selecting the types / instances to synchronise ('[]' = all). | {} |
| UnifiedOpenPipeRequest.requestJson | 20, 21 | requestJson: single-line JSON object, e.g. {"Message":"BrowseTags","Params":{"Filter":"*Motor*","PageSize":50}}. A ClientCookie is generated when missing. | {} |
| UpdateDeviceAddress.attributesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| UpdateDeviceAddress.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| UpdateDeviceAddress.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| UpdateDeviceAddress.propertiesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| UpdateDeviceItemChannel.attributesJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | attributesJson: JSON object attribute name -> value to write. | {} |
| UpdateDeviceItemChannel.devicePathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| UpdateDeviceItemChannel.itemPathJson | 14sp1, 15.1, 16, 17, 18, 19, 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| UpdateUnifiedMultilingualProperty.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| UpdateUnifiedObjectProperties.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| UpdateUnifiedObjectProperties.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| UpdateUnifiedPlantObject.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| UpdateUnifiedPlantObject.propertiesJson | 20, 21 | propertiesJson: JSON object property name -> value to write (scalars; the tool description lists the supported names). | {} |
| UpdateUnifiedRuntimeSettings.changesJson | 20, 21 | changesJson: JSON object field name -> new value. | {} |
| UploadDeviceParameters.devicePathJson | 20, 21 | devicePathJson: JSON array naming the station - [group, ..., station] or the unique station name alone, e.g. ["PLC_1"] (the array itself or its JSON text). | {} |
| UploadDeviceParameters.itemPathJson | 20, 21 | itemPathJson: JSON array of exact device-item names below the station, e.g. ["PROFINET interface_1"]; [] means the station itself (or its CPU where the tool says so). | {} |
| UploadDeviceParameters.promptAnswersJson | 20, 21 | promptAnswersJson: JSON object of explicit answers to TIA prompts by prompt type name. | {} |
| UploadStationFromPlc.promptAnswersJson | 20, 21 | promptAnswersJson: JSON object of explicit answers to upload prompts by prompt type name. | {} |
| ValidateClassicHmiMinimalPackagePlcSync.plcSymbolsJson | 20, 21 | plcSymbolsJson: JSON array of exact PLC symbols, or object with Symbols[]. Example: ["DB1_MotorData.Motor.Start"]. | {} |
| ValidateUnifiedObject.objectPathJson | 20, 21 | objectPathJson: JSON string containing property steps [{"property":"TagTables","name":"Table"},{"property":"Tags","name":"Tag"}]. property selects a public property; optional name selects an exact collection member. [] selects the root. No parent/backlinks. | {} |
| WriteClassicHmiMinimalPackageFiles.packageJson | 20, 21 | packageJson: JSON object with Name, ScreenDesign, and TagTable. | {} |
| WritePlcSimAdvancedTags.valuesJson | 20, 21 | valuesJson: JSON object tag name -> value, or array of {name, value}. Max 500. | {"examples": ["{\"MCP_SimDB.Start\":true}"]} |
| WritePlcWebVars.writesJson | 20, 21 | writesJson: JSON array of {"name":"\"DB1\".\"Setpoint\"","value":42.5} objects, or a JSON object {"\"Tag_1\"":true}. Max 500. | {} |
| WriteUnifiedRuntimeTags.writesJson | 20, 21 | writesJson: JSON array of {"name":"Tag_1","value":50} objects, or a JSON object {"Tag_1":50,"Flag":true}. Max 500. | {} |

| parser/策略来源:行 | 原始边界表达式 |
|---|---|
| [ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs):42 | `if (nodes.Count > 500) throw new ArgumentException("At most 500 selected files.");` |
| [ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs):55 | `if (paths.Count != 1 \|\| revision.Length > 128 \|\| !System.Text.RegularExpressions.Regex.IsMatch(revision, @"\A[A-Za-z0-9][A-Za-z0-9_./~^{}@-]*\z")) throw new ArgumentException("show needs exactly one file and a safe revision name/hash.");` |
| [ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs):59 | `if (action == "commit" && (string.IsNullOrWhiteSpace(message) \|\| message.Length > 10000)) throw new ArgumentException("A commit message of 1..10000 characters is required.");` |
| [ModelContextProtocol/Tools/ImportOrderTools.cs](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs):29 | `if (artifactsJson == null \|\| artifactsJson.Length > 1024 * 1024) throw new ArgumentException("Provide at most one MiB of JSON.");` |
| [ModelContextProtocol/Tools/ImportOrderTools.cs](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs):162 | `if (response is ResponseXmlBuild xml && xml.Xml != null && xml.Xml.Length > 1048576)` |
| [ModelContextProtocol/Tools/LibraryTools.cs](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs):148 | `if (action == "harmonizeProject") LibraryDeepLogic.JoinHarmonizeOptions(HardwareNetworkLogic.ParseNames(harmonizeOptionsJson, "harmonizeOptions", 2));` |
| [ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs):417 | `[Description("Preview only by default; false applies after approval.")] bool dryRun = true, [Description("Complete explicit relative-file manifest from preview.")] string[]? importOrder = null, [Description("Exact planHash from the reviewed preview.")] string expectedPlanHash = "", [Description("Explicit confirmation for project mutation.")] bool confirm = false, [Description("Exact absolute bound project path.")] string expectedProjectFile = "", [Description("Selected file limit 1..256; no truncation.")] int maxItems = 128)` |
| [ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs):451 | `[Description("Preview only by default; false applies after approval.")] bool dryRun = true, [Description("Complete explicit relative-file manifest from preview.")] string[]? importOrder = null, [Description("Exact planHash from the reviewed preview.")] string expectedPlanHash = "", [Description("Explicit confirmation for project mutation.")] bool confirm = false, [Description("Exact absolute bound project path.")] string expectedProjectFile = "", [Description("Allow reviewed replacements with recovery export before any import.")] bool overwrite = false, [Description("Selected file limit 1..256; no truncation.")] int maxItems = 128)` |
| [ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs):515 | `if (conflicts.Count > 16)` |
| [ModelContextProtocol/Tools/ProjectSessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs):565 | `if (calls.Length > 20) return V4Reject(tool, new Error("Transaction count exceeds its limit.", new LimitExceededDetails("calls", 20, calls.Length)));` |
| [ModelContextProtocol/Tools/ProjectSessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs):566 | `if (string.IsNullOrWhiteSpace(text) \|\| text.Length > 200) return V4Reject(tool, InvalidInput("text"));` |
| [ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs):32 | `[McpServerTool(Name="ManageUnifiedScreenItem"), Description("[L2][HMI-Unified][WRITE] Any screen item type on one exact Unified screen (screenPath = unique screen name or /Group/Screen): list (name/type/geometry, paged), read (scalars, #AARRGGBB colors, parts and collections to depth, every MultilingualText language, features, event/dynamization counts), create (itemType from DescribeUnifiedScreenItemType via native Create<T>(name) or Create<T>(name, containedType) for faceplate/custom widget containers, with initial properties), update, delete (confirmDelete=true). properties accepts scalars or one level of parts ({\"Font\":{\"Size\":14},\"BackColor\":\"#FF0000FF\"}) and multilingual texts per culture ({\"Text\":{\"en-US\":\"Start\"}}); at most 50 leaves, every leaf is read back. Default preview; no save/compile/download. Events: ManageUnifiedEvent; dynamizations: ManageUnifiedDynamization. Current native policy; V4 safety behavior is not yet accepted.")]` |
| [ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs):71 | `if (!string.IsNullOrWhiteSpace(culture.Name) && culture.Name.Length <= 16)` |
| [Siemens/Hmi/UnifiedExchangeLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedExchangeLogic.cs):34 | `if (name.Length > 128 \|\| name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 \|\| name.IndexOfAny(new[] { '/', '\\' }) >= 0 \|\| name == "." \|\| name == "..")` |
| [Siemens/Hmi/UnifiedScreenItemLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedScreenItemLogic.cs):27 | `if (!string.IsNullOrEmpty(itemName) && (itemName.Length > 128 \|\| itemName.IndexOfAny(new[] { '/', '\\' }) >= 0)) throw new ArgumentException("itemName must be 1..128 characters without path separators.");` |
| [Siemens/Hmi/UnifiedScreenItemLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedScreenItemLogic.cs):39 | `if (string.IsNullOrWhiteSpace(propertiesJson) \|\| propertiesJson.Length > 65536) throw new ArgumentException("propertiesJson must be a JSON object (<= 64 KB).");` |
| [Siemens/Hmi/UnifiedScreenItemLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedScreenItemLogic.cs):156 | `if (texts.Count > 50) throw new ArgumentException("At most 50 multilingual entries per request.");` |
| [Siemens/Hmi/UnifiedScreenItemLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedScreenItemLogic.cs):173 | `if (string.IsNullOrWhiteSpace(language.Key) \|\| language.Key.Length > 16) throw new ArgumentException("Culture name required for " + name + ".");` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):59 | `if (text.Length == 7) argb = unchecked((int)0xFF000000) \| argb;` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):99 | `foreach (var item in sequence) { if (item == null) continue; if (++count > 500) { row["dataComplete"] = false; items.Add(new JsonObject { ["truncated"] = true }); break; } items.Add(Tree(item, depth - 1)); }` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):284 | `if (array.Count > 100) throw new ArgumentException("At most 100 mapping entries per request.");` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):307 | `if (string.IsNullOrWhiteSpace(name) \|\| name.Length > 128 \|\| name.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact screen name of 1..128 characters without path separators required.");` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):320 | `foreach (var info in attributes) { if (((JsonArray)result["attributes"]!).Count >= 512) break; ((JsonArray)result["attributes"]!).Add(new JsonObject { ["name"] = info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString(), ["accessMode"] = info?.GetType().GetProperty("AccessMode")?.GetValue(info)?.ToString() }); }` |
| [Siemens/Hmi/UnifiedUiModelLogic.cs](../../src/Engine/Siemens/Hmi/UnifiedUiModelLogic.cs):322 | `foreach (var info in compositions) { if (((JsonArray)result["compositions"]!).Count >= 128) break; ((JsonArray)result["compositions"]!).Add(info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString()); }` |
| [Siemens/MotionProDiagClassicHmiLogic.cs](../../src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs):113 | `if (json == null \|\| json.Length > 16384) throw new ArgumentException("targetJson exceeds 16 KiB.");` |
| [Siemens/MotionProDiagClassicHmiLogic.cs](../../src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs):122 | `if (names.Length == 0 \|\| names.Length > 64 \|\| names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(key + " must contain 1-64 nonempty exact names.");` |
| [Siemens/MotionProDiagClassicHmiLogic.cs](../../src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs):180 | `if (string.IsNullOrWhiteSpace(value) \|\| value.Length > 256 \|\| value.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ArgumentException("Exact " + what + " (single nonempty segment) required.");` |
| [Siemens/MotionProDiagClassicHmiLogic.cs](../../src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs):189 | `if (json == null \|\| json.Length > 65536) throw new ArgumentException("attributesJson exceeds 64 KiB.");` |
| [Siemens/MotionProDiagClassicHmiLogic.cs](../../src/Engine/Siemens/MotionProDiagClassicHmiLogic.cs):191 | `if (obj.Count > 50) throw new ArgumentException("At most 50 attributes per request.");` |
| [Siemens/ObjectIdentityRules.cs](../../src/Engine/Siemens/ObjectIdentityRules.cs):18 | `if (!string.IsNullOrEmpty(identifier)) { if (identifier.Length > 4096) throw new ArgumentException("identifier too long."); return; }` |
| [Siemens/ProjectSecurityLogic.cs](../../src/Engine/Siemens/ProjectSecurityLogic.cs):47 | `else if (string.IsNullOrWhiteSpace(name) \|\| name.Length > 256) throw new ArgumentException("Exact nonempty " + request.Target + " name required (max 256 chars).");` |
| [Siemens/ProjectSecurityLogic.cs](../../src/Engine/Siemens/ProjectSecurityLogic.cs):62 | `if (names.Count < 1 \|\| names.Count > 64 \|\| names.Any(n => n is not JsonValue \|\| string.IsNullOrWhiteSpace(n!.GetValue<string>()))) throw new ArgumentException("devicePathJson needs 1-64 exact nonempty names.");` |
| [Siemens/SecurityDeepLogic.cs](../../src/Engine/Siemens/SecurityDeepLogic.cs):155 | `if (array.Count > 64) throw new ArgumentException("At most 64 subject alternative names.");` |
| [Siemens/SecurityDeepLogic.cs](../../src/Engine/Siemens/SecurityDeepLogic.cs):161 | `if (string.IsNullOrWhiteSpace(value) \|\| value.Length > 255) throw new ArgumentException("Subject alternative name value must be 1..255 chars.");` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):16 | `private static void RequireText(string value, string parameter, int max = 256)` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):91 | `if (selector.Names.Any(n => string.IsNullOrWhiteSpace(n) \|\| n.Length > 64) \|\| selector.Names.Distinct(StringComparer.Ordinal).Count() != selector.Names.Length) throw new ArgumentException("namesJson needs distinct nonempty parameter names such as p1000[0], r47 or p2080[0].6.");` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):104 | `if (selector.Names.Length + selector.Numbers.Length > 200) throw new ArgumentException("At most 200 parameters per call.");` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):117 | `if (string.IsNullOrEmpty(name) \|\| name.Length > 64) return false;` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):179 | `RequireText(softwarePath, "softwarePath"); RequireText(objectPath, "objectPath", 1024); EngineeringGroupOperations.Parts(objectPath);` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):260 | `if (entries.Count == 0 \|\| entries.Count > 200) throw new ArgumentException("valueJson.entries needs 1..200 entries.");` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):296 | `RequireText(filePath, "filePath", 1024);` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):310 | `if (action == "changeType") { RequireText(typeIdentifier, "typeIdentifier"); if (!typeIdentifier.StartsWith("OrderNumber:", StringComparison.Ordinal) && !typeIdentifier.StartsWith("GSD:", StringComparison.Ordinal) && !typeIdentifier.StartsWith("System:", StringComparison.Ordinal)) throw new ArgumentException("typeIdentifier must be an official TypeIdentifier such as OrderNumber:6SL3120-2TE21-8Axx//10014."); }` |
| [Siemens/StartdriveLogic.cs](../../src/Engine/Siemens/StartdriveLogic.cs):323 | `if (action == "createProtocol") { RequireText(filePath, "filePath", 1024); RequireOneOf(fileOperation, FileOperations, "fileOperation"); }` |
| [Siemens/ToolTransactionRules.cs](../../src/Engine/Siemens/ToolTransactionRules.cs):17 | `if (array.Count < 1 \|\| array.Count > 20) throw new ArgumentException("callsJson needs 1..20 tool calls.");` |
| [Siemens/ToolTransactionRules.cs](../../src/Engine/Siemens/ToolTransactionRules.cs):33 | `if (string.IsNullOrWhiteSpace(text) \|\| text.Length > 200) throw new ArgumentException("text (the undo description shown in TIA) must be 1..200 characters; TIA refuses an empty one.");` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):19 | `if (string.IsNullOrWhiteSpace(json) \|\| json.Length > OfflineCompositionBuilders.MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):20 | `using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):36 | `if (value.Value.ValueKind != JsonValueKind.Array \|\| value.Value.GetArrayLength() > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Bounded array required.");` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):39 | `if (++count > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Total interface member limit exceeded.");` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):67 | `if (xml.Length > OfflineCompositionBuilders.MaxXmlCharacters) throw new ArgumentException("Output size limit exceeded.");` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):68 | `using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = OfflineCompositionBuilders.MaxXmlCharacters });` |
| [OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs):119 | `if ((required && string.IsNullOrWhiteSpace(text)) \|\| text.Length > OfflineCompositionBuilders.MaxStringCharacters) throw new ArgumentException("Invalid string size.");` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):12 | `internal const int MaxJsonCharacters = 262144;` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):13 | `internal const int MaxXmlCharacters = 1048576;` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):14 | `internal const int MaxItems = 1000;` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):15 | `internal const int MaxStringCharacters = 4096;` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):23 | `if (string.IsNullOrWhiteSpace(json) \|\| json.Length > MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):24 | `using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):148 | `DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters + 256` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):180 | `if (xml.Length > MaxXmlCharacters) throw new ArgumentException("Output size limit exceeded.");` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):200 | `if (value.ValueKind != JsonValueKind.Array \|\| value.GetArrayLength() is < 1 or > MaxItems) throw new ArgumentException("Invalid array size.");` |
| [OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs):209 | `if ((required && string.IsNullOrWhiteSpace(text)) \|\| text.Length > MaxStringCharacters) throw new ArgumentException("Invalid string size.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):14 | `internal const int MaxNetworks = 64;` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):20 | `if (string.IsNullOrWhiteSpace(json) \|\| json.Length > OfflineCompositionBuilders.MaxJsonCharacters) throw new ArgumentException("Invalid JSON size.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):21 | `using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):45 | `if (++memberCount > OfflineCompositionBuilders.MaxItems) throw new ArgumentException("Member limit exceeded.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):71 | `if (xml.Length > OfflineCompositionBuilders.MaxXmlCharacters) throw new ArgumentException("Output limit exceeded.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):72 | `using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = OfflineCompositionBuilders.MaxXmlCharacters });` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):125 | `if (array.ValueKind != JsonValueKind.Array \|\| array.GetArrayLength() is < 1 or > 32) throw new ArgumentException("Bounded symbol path required.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):129 | `if (components.Length > 32) throw new ArgumentException("Symbol depth exceeded.");` |
| [OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs):200 | `if ((required && string.IsNullOrWhiteSpace(text)) \|\| text.Length > OfflineCompositionBuilders.MaxStringCharacters) throw new ArgumentException("Invalid string size.");` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):12 | `internal const int MaxJsonCharacters = 262144;` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):13 | `internal const int MaxItems = 1000;` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):14 | `internal const int MaxStringCharacters = 4096;` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):15 | `internal const int MaxXmlCharacters = 1048576;` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):23 | `if (string.IsNullOrWhiteSpace(json) \|\| json.Length > MaxJsonCharacters)` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):25 | `using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):60 | `if (xml.Length > MaxXmlCharacters) throw new ArgumentException("Generated XML exceeds one Mi character.");` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):63 | `DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters` |
| [OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs):120 | `if ((required && string.IsNullOrWhiteSpace(text)) \|\| text.Length > MaxStringCharacters)` |
| [CliOptions.cs](../../src/Logic/CliOptions.cs):192 | `if (++i >= args.Length \|\| !int.TryParse(args[i], out int workerTimeout) \|\| workerTimeout < 10 \|\| workerTimeout > 180)` |
| [Generation/CanonicalJson.cs](../../src/Logic/Generation/CanonicalJson.cs):14 | `public const int MaximumDepth = 64;` |
| [Generation/CanonicalJson.cs](../../src/Logic/Generation/CanonicalJson.cs):23 | `using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });` |
| [Generation/CanonicalJson.cs](../../src/Logic/Generation/CanonicalJson.cs):135 | `if (exponent >= 0 && trimmed.Length + exponent <= 21)` |
| [Generation/GenerationAllocator.cs](../../src/Logic/Generation/GenerationAllocator.cs):97 | `if (parts.Length != 2 \|\| !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) \|\| prefix < 1 \|\| prefix > 30)` |
| [Generation/GenerationDocuments.cs](../../src/Logic/Generation/GenerationDocuments.cs):14 | `MaxDepth = CanonicalJson.MaximumDepth` |
| [Generation/GenerationNamingEngine.cs](../../src/Logic/Generation/GenerationNamingEngine.cs):41 | `if (result.Length > 4096) throw CanonicalJson.Failure("", "name-length", "Expanded text exceeds 4096 characters.");` |
| [Generation/GenerationNamingEngine.cs](../../src/Logic/Generation/GenerationNamingEngine.cs):43 | `if (result.Length > 4096) throw CanonicalJson.Failure("", "name-length", "Expanded text exceeds 4096 characters.");` |
| [Generation/GenerationNamingEngine.cs](../../src/Logic/Generation/GenerationNamingEngine.cs):105 | `if (name.Length < (rule.MinLength ?? 1) \|\| name.Length > (rule.MaxLength ?? 4096) \|\| name.Any(c => char.IsControl(c) \|\| c == '"')) return false;` |
| [Generation/GenerationPlanner.cs](../../src/Logic/Generation/GenerationPlanner.cs):158 | `if (steps.Count > 10000) throw CanonicalJson.Failure("/steps", "limit", "Plan exceeds 10000 steps.");` |
| [Generation/GenerationPlanner.cs](../../src/Logic/Generation/GenerationPlanner.cs):671 | `if (entries.Count >= options.MaximumObjects) throw CanonicalJson.Failure("/expected", "limit", "Expanded model exceeds its object budget.");` |
| [Generation/GenerationSchemas.cs](../../src/Logic/Generation/GenerationSchemas.cs):51 | `if (schemas.Count != 14) throw new InvalidOperationException("Generation schema resources are missing.");` |
| [Generation/GenerationSchemas.cs](../../src/Logic/Generation/GenerationSchemas.cs):57 | `if (errors.Count >= 100) return;` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):141 | `if (files.Count >= limits.MaximumFiles) throw CanonicalJson.Failure(name, "file-count", "Too many package files.");` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):169 | `if (source.Length > limits.MaximumArchiveBytes) throw CanonicalJson.Failure("", "archive-size", "Archive exceeds the compressed size limit.");` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):171 | `if (archive.Entries.Count > limits.MaximumEntries) throw CanonicalJson.Failure("", "entry-count", "Too many archive entries.");` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):190 | `if (files.Count >= limits.MaximumFiles) throw CanonicalJson.Failure(name, "file-count", "Too many package files.");` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):202 | `if (name.Length == 0 \|\| name.Length > 240 \|\| name.Split('/').Length > 16 \|\| name != name.Normalize(NormalizationForm.FormC)` |
| [Generation/StandardPackageLoader.cs](../../src/Logic/Generation/StandardPackageLoader.cs):245 | `if (output.Length + read > limits.MaximumFileBytes) throw CanonicalJson.Failure(name, "file-size", "Decompressed file exceeds the size limit.");` |
| [ModelContextProtocol/BatchPlanStore.cs](../../src/Logic/ModelContextProtocol/BatchPlanStore.cs):24 | `if (_plans.Count >= 32) throw new InvalidOperationException("Too many pending previews; use or wait for expiry.");` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):24 | `if (rules.Count > 50) throw new ArgumentException("At most 50 XML rules.");` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):33 | `if (xpath.Length == 0 \|\| xpath.Length > 1024) throw new ArgumentException("Each rule needs an XPath selecting elements (max 1024 chars).");` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):44 | `{ totalFindings++; if (findings.Count < 2000) findings.Add(new JsonObject { ["file"] = file, ["rule"] = rule, ["severity"] = severity, ["message"] = detail }); }` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):49 | `if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Export exceeds 16 MiB audit limit.");` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):54 | `using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 })) xml = XDocument.Load(reader);` |
| [ModelContextProtocol/Builders/EngineeringQualityAudit.cs](../../src/Logic/ModelContextProtocol/Builders/EngineeringQualityAudit.cs):60 | `if (selected.Count < (rule["minCount"]?.GetValue<int>() ?? 0) \|\| selected.Count > (rule["maxCount"]?.GetValue<int>() ?? int.MaxValue)) Finding(file, id, rule["severity"]?.GetValue<string>() ?? "warning", "XPath match count: " + selected.Count);` |
| [ModelContextProtocol/Builders/HmiTemplateLayoutAnalyzer.cs](../../src/Logic/ModelContextProtocol/Builders/HmiTemplateLayoutAnalyzer.cs):196 | `if (severeOverlapCount <= 20) warnings.Add("layout-overlap: " + boxes[i]["name"] + " overlaps " + boxes[j]["name"]);` |
| [ModelContextProtocol/Builders/HmiTemplateReferenceAnalyzer.cs](../../src/Logic/ModelContextProtocol/Builders/HmiTemplateReferenceAnalyzer.cs):255 | `if (count > 0 && sampleStrings.Count < 80)` |
| [ModelContextProtocol/Builders/OfflineAnalysisLogic.cs](../../src/Logic/ModelContextProtocol/Builders/OfflineAnalysisLogic.cs):29 | `public const int MaxLimit = 500;` |
| [ModelContextProtocol/Builders/OfflineAnalysisLogic.cs](../../src/Logic/ModelContextProtocol/Builders/OfflineAnalysisLogic.cs):30 | `public const int MaxDiffLinesPerSide = 60000;` |
| [ModelContextProtocol/Builders/OfflineAnalysisLogic.cs](../../src/Logic/ModelContextProtocol/Builders/OfflineAnalysisLogic.cs):31 | `private const int MaxLineLength = 400;` |
| [ModelContextProtocol/Builders/OfflineAnalysisLogic.cs](../../src/Logic/ModelContextProtocol/Builders/OfflineAnalysisLogic.cs):32 | `private const int MaxAnnotationText = 300;` |
| [ModelContextProtocol/Builders/OfflineAnalysisLogic.cs](../../src/Logic/ModelContextProtocol/Builders/OfflineAnalysisLogic.cs):697 | `if (a.Count > MaxDiffLinesPerSide \|\| b.Count > MaxDiffLinesPerSide)` |
| [ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs](../../src/Logic/ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs):15 | `if (rows.Count < 1 \|\| rows.Count > 500) throw new ArgumentException("Use 1..500 Boolean mapping/alarm rows.");` |
| [ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs](../../src/Logic/ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs):36 | `if (array.Count < 1 \|\| array.Count > 32) throw new ArgumentException("Use 1..32 symbol components.");` |
| [ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs](../../src/Logic/ModelContextProtocol/Builders/PlcAliasAlarmBuilder.cs):38 | `if (parts.Any(p => string.IsNullOrWhiteSpace(p) \|\| p.Length > 128 \|\| p.Contains("[") \|\| p.Contains("]"))) throw new ArgumentException("Empty/oversized components and array-index syntax are unsupported; use an exported XML template for indexed operands.");` |
| [ModelContextProtocol/Builders/PlcDocumentEditing.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs):15 | `public const int MaxBytes = 16 * 1024 * 1024;` |
| [ModelContextProtocol/Builders/PlcDocumentEditing.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs):22 | `if (!f.Exists \|\| f.Length > MaxBytes) throw new ArgumentException("XML file is missing or exceeds 16 MiB.");` |
| [ModelContextProtocol/Builders/PlcDocumentEditing.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs):30 | `if (Encoding.UTF8.GetByteCount(xml) > MaxBytes) throw new ArgumentException("XML exceeds 16 MiB.");` |
| [ModelContextProtocol/Builders/PlcDocumentEditing.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs):31 | `using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });` |
| [ModelContextProtocol/Builders/PlcDocumentEditing.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentEditing.cs):126 | `if (changes.Count < 1 \|\| changes.Count > 100) throw new ArgumentException("Supply 1..100 changes.");` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):25 | `public const int MaxNetworksPerBlock = 2000;` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):26 | `public const int MaxHandbookFiles = 2000;` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):130 | `if (doc.Networks.Count > MaxNetworksPerBlock) throw new InvalidDataException("Block has " + doc.Networks.Count + " networks; limit is " + MaxNetworksPerBlock + ".");` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):431 | `if (files.Count > MaxHandbookFiles) throw new InvalidDataException(files.Count + " documents exceed the handbook limit of " + MaxHandbookFiles + ".");` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):533 | `if (!(node is JsonObject obj)) throw new ArgumentException("rulesJson must be a JSON object, e.g. {\"disable\":[\"SCL007\"],\"maxLineLength\":120,\"maxNesting\":5}.");` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):535 | `if (obj["maxLineLength"] != null) o.MaxLineLength = obj["maxLineLength"]!.GetValue<int>();` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):538 | `if (o.MaxLineLength < 40 \|\| o.MaxLineLength > 1000) throw new ArgumentException("maxLineLength must be between 40 and 1000.");` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):564 | `if (raw.Length > options.MaxLineLength) Add("SCL007", "info", lineNo, "Line longer than " + options.MaxLineLength + " characters (" + raw.Length + ").", raw);` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):603 | `if (stack.Count == 0) { Add("SCL001", "error", lineNo, upper + " without a matching opener.", raw.Trim()); continue; }` |
| [ModelContextProtocol/Builders/PlcDocumentationLogic.cs](../../src/Logic/ModelContextProtocol/Builders/PlcDocumentationLogic.cs):621 | `if (firstWord == "REGION" && words.Count == 1 && code.Trim().Equals("REGION", StringComparison.OrdinalIgnoreCase)) Add("SCL012", "info", lineNo, "REGION without a name.", raw.Trim());` |
| [ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs](../../src/Logic/ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs):295 | `if (count < 1 \|\| count > PlcProgramRenderer.MaxElements) throw new ArgumentException("Instruction pin cardinality exceeds the render budget.");` |
| [ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs](../../src/Logic/ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs):332 | `if (rail && ends.Count > 0) ends.Insert(0, (24, ends[0].y, true));` |
| [ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs](../../src/Logic/ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs):379 | `if (node.Finding.Length > 0) b.Append("<circle class=\"finding\" cx=\"").Append(x + 23).Append("\" cy=\"").Append(y - 18).Append("\" r=\"4\"><title>").Append(E(node.Finding)).Append("</title></circle>");` |
| [ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs](../../src/Logic/ModelContextProtocol/Builders/PlcLadderDrawing.Network.cs):389 | `if (node.Preset.Length > 0) Text(b, "instruction-type", node.X, node.Top + header - 7 - (Wrap(node.Preset, 22).Count() - 1) * 16, node.Preset, "middle", 22);` |
| [ModelContextProtocol/Builders/PlcOfflineReferences.cs](../../src/Logic/ModelContextProtocol/Builders/PlcOfflineReferences.cs):37 | `files.Add(file); if (files.Count > 2000) throw new ArgumentException("Maximum 2000 export documents.");` |
| [ModelContextProtocol/Builders/PlcOfflineReferences.cs](../../src/Logic/ModelContextProtocol/Builders/PlcOfflineReferences.cs):39 | `foreach (var sub in Directory.EnumerateDirectories(folder)) { pending.Push(sub); if (pending.Count > 2000) throw new ArgumentException("Directory traversal limit exceeded."); }` |
| [ModelContextProtocol/Builders/PlcOfflineReferences.cs](../../src/Logic/ModelContextProtocol/Builders/PlcOfflineReferences.cs):124 | `if (++visits > 10000 \|\| rows.Count >= 2000) { truncated = true; return; }` |
| [ModelContextProtocol/Builders/PlcProgramRenderer.cs](../../src/Logic/ModelContextProtocol/Builders/PlcProgramRenderer.cs):20 | `public const int MaxFiles = 256;` |
| [ModelContextProtocol/Builders/PlcProgramRenderer.cs](../../src/Logic/ModelContextProtocol/Builders/PlcProgramRenderer.cs):21 | `public const int MaxNetworks = 512;` |
| [ModelContextProtocol/Builders/PlcProgramRenderer.cs](../../src/Logic/ModelContextProtocol/Builders/PlcProgramRenderer.cs):22 | `public const int MaxElements = 256;` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):19 | `if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("XML exceeds 16 MiB.");` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):21 | `using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):59 | `if (!file.Exists \|\| file.Length > 16 * 1024 * 1024) throw new ArgumentException("Existing XML <=16 MiB required.");` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):64 | `if (paths.Length == 0 \|\| paths.Length > 128) throw new ArgumentException("Schema directory must contain 1..128 XSD files.");` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):70 | `if ((total += bytes.Length) > 32 * 1024 * 1024) throw new ArgumentException("XSD byte budget exceeded.");` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):77 | `if (nodes.Length > 2000) throw new ArgumentException("Fragment budget exceeded.");` |
| [ModelContextProtocol/Builders/PlcSchemaValidation.cs](../../src/Logic/ModelContextProtocol/Builders/PlcSchemaValidation.cs):97 | `if (issues.Count < 200) issues.Add(new JsonObject { ["severity"] = args.Severity.ToString(), ["element"] = node.Name.ToString(), ["message"] = args.Message });` |
| [ModelContextProtocol/Builders/PlcTemplateExpansion.cs](../../src/Logic/ModelContextProtocol/Builders/PlcTemplateExpansion.cs):20 | `using (var reader = XmlReader.Create(templatePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 })) template = XDocument.Load(reader, LoadOptions.PreserveWhitespace);` |
| [ModelContextProtocol/Builders/PlcTemplateExpansion.cs](../../src/Logic/ModelContextProtocol/Builders/PlcTemplateExpansion.cs):22 | `if (rows.Count < 1 \|\| rows.Count > 100) throw new ArgumentException("Use 1..100 rows.");` |
| [ModelContextProtocol/Builders/UnifiedCwcPackage.cs](../../src/Logic/ModelContextProtocol/Builders/UnifiedCwcPackage.cs):39 | `if (item is DirectoryInfo child) { if (queue.Count >= 256) throw new ArgumentException("CWC directory budget exceeded."); queue.Enqueue(child); continue; }` |
| [ModelContextProtocol/Builders/UnifiedCwcPackage.cs](../../src/Logic/ModelContextProtocol/Builders/UnifiedCwcPackage.cs):41 | `if (files.Count >= 1024 \|\| file.Length > 16 * 1024 * 1024 \|\| total + file.Length > 64 * 1024 * 1024) throw new ArgumentException("CWC supports <=1024 files, <=16 MiB each and <=64 MiB total.");` |
| [ModelContextProtocol/Builders/UnifiedCwcPackage.cs](../../src/Logic/ModelContextProtocol/Builders/UnifiedCwcPackage.cs):44 | `while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { total += n; if (content.Length + n > 16 * 1024 * 1024 \|\| total > 64 * 1024 * 1024) throw new ArgumentException("CWC byte budget exceeded while reading."); content.Write(buffer, 0, n); }` |
| [ModelContextProtocol/Builders/UnifiedCwcPackage.cs](../../src/Logic/ModelContextProtocol/Builders/UnifiedCwcPackage.cs):48 | `if (!files.TryGetValue("manifest.json", out var manifest) \|\| manifest.Length > 1024 * 1024) throw new ArgumentException("Root manifest.json <=1 MiB required.");` |
| [ModelContextProtocol/ExportStore.cs](../../src/Logic/ModelContextProtocol/ExportStore.cs):66 | `public const int MaxSliceChars = 20000;` |
| [ModelContextProtocol/ExportStore.cs](../../src/Logic/ModelContextProtocol/ExportStore.cs):70 | `private const int MaxEntries = 32;` |
| [ModelContextProtocol/ExportStore.cs](../../src/Logic/ModelContextProtocol/ExportStore.cs):71 | `private const int MaxTotalChars = 8_000_000;` |
| [ModelContextProtocol/ExportStore.cs](../../src/Logic/ModelContextProtocol/ExportStore.cs):82 | `private const int MaxTombstones = 512;` |
| [ModelContextProtocol/ImportStagingSession.cs](../../src/Logic/ModelContextProtocol/ImportStagingSession.cs):20 | `if (!Guid.TryParseExact(sessionId, "N", out _) \|\| string.IsNullOrWhiteSpace(mcpSessionId) \|\| mcpSessionId.Length > 256)` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):27 | `public const int MaximumFiles = 128, MaximumBatches = 32;` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):37 | `public const int MaximumListedBatches = 200;` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):92 | `if (file == null \|\| string.IsNullOrWhiteSpace(file.FileName) \|\| file.FileName.Length > 128 \|\| file.FileName.Trim() != file.FileName` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):104 | `if (string.IsNullOrWhiteSpace(file.Content) \|\| file.Content.Length > MaximumFileBytes) throw new ArgumentException("Content must be nonempty and at most 4 MiB.", "files");` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):108 | `if (bytes.LongLength > MaximumFileBytes) throw new ArgumentException("UTF-8 file size exceeds 4 MiB.", "files");` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):111 | `var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes };` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):129 | `if (files == null \|\| files.Length < 1 \|\| files.Length > MaximumFiles) throw new ArgumentException("Supply 1..128 files.", "files");` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):143 | `if (batches.Count >= MaximumBatches \|\| count + files.Length > MaximumFiles \|\| retained + total > MaximumBytes) throw new ArgumentException("Session staging quota exceeded; review and clean up staged batches.", "files");` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):147 | `if (Path.DirectorySeparatorChar == '\\' && Path.Combine(root, new string('0', 32), file.FileName).Length >= 260)` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):231 | `if (stream.Length < 1 \|\| stream.Length > 128 * 1024) return null;` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):237 | `if ((int?)input["manifestVersion"] == 2 && ((string?)input["mcpSessionId"] is not string mcp \|\| string.IsNullOrWhiteSpace(mcp) \|\| mcp.Length > 256` |
| [ModelContextProtocol/ImportStagingStore.cs](../../src/Logic/ModelContextProtocol/ImportStagingStore.cs):250 | `if (leaf == null \|\| leaf.Length < 1 \|\| leaf.Length > 128 \|\| leaf != Path.GetFileName(leaf) \|\| leaf.Contains("..")` |
| [ModelContextProtocol/PreflightLogic.cs](../../src/Logic/ModelContextProtocol/PreflightLogic.cs):173 | `if (tail.Length >= 4 && string.Equals(tail, givenTail, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 50);` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):18 | `public const int MaxScenarioSteps = 500;` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):19 | `public const int MaxTagsPerCall = 500;` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):114 | `if (n.Length > 64 \|\| n.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '\|', '\0' }) >= 0) throw new ArgumentException("instanceName must be 1..64 characters without path separators or wildcard characters.");` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):142 | `if (list.Count > MaxTagsPerCall) throw new ArgumentException(parameterName + " has " + list.Count + " entries; limit is " + MaxTagsPerCall + ".");` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):161 | `if (names.Count > MaxTagsPerCall) throw new ArgumentException(parameterName + " has " + names.Count + " names; limit is " + MaxTagsPerCall + ".");` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):290 | `if (steps.Count > MaxScenarioSteps) throw new ArgumentException("scenarioJson has " + steps.Count + " steps; limit is " + MaxScenarioSteps + ".");` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):301 | `if (o["cycles"] != null) { kinds.Add("cycles"); step.Count = o["cycles"]!.GetValue<int>(); if (step.Count < 1 \|\| step.Count > 100000) throw new ArgumentException("Step " + index + ": cycles must be 1..100000."); }` |
| [Runtime/PlcSimAdvancedLogic.cs](../../src/Logic/Runtime/PlcSimAdvancedLogic.cs):302 | `if (o["waitMs"] != null) { kinds.Add("wait"); step.Count = o["waitMs"]!.GetValue<int>(); if (step.Count < 1 \|\| step.Count > 60000) throw new ArgumentException("Step " + index + ": waitMs must be 1..60000."); }` |
| [Runtime/RuntimeChannelsLogic.cs](../../src/Logic/Runtime/RuntimeChannelsLogic.cs):54 | `public const int MaxItemsPerCall = 500;` |
| [Runtime/RuntimeChannelsLogic.cs](../../src/Logic/Runtime/RuntimeChannelsLogic.cs):102 | `if (list.Count > MaxItemsPerCall) throw new ArgumentException($"{paramName} has {list.Count} entries; at most {MaxItemsPerCall} per call.");` |
| [Runtime/RuntimeChannelsLogic.cs](../../src/Logic/Runtime/RuntimeChannelsLogic.cs):144 | `if (items.Count > MaxItemsPerCall) throw new ArgumentException($"{paramName} has {items.Count} entries; at most {MaxItemsPerCall} per call.");` |
| [Runtime/RuntimeChannelsLogic.cs](../../src/Logic/Runtime/RuntimeChannelsLogic.cs):225 | `if (h.Length == 0) throw new ArgumentException("host is empty; give the CPU web server address, e.g. '192.168.0.1' or 'plc.local:443'.");` |
| [Siemens/ArgumentRules.cs](../../src/Logic/Siemens/ArgumentRules.cs):57 | `if (empty \|\| json.Length > 16384) throw new ArgumentException("propertiesJson must be a JSON object (<= 16 KB).");` |
| [Siemens/ArgumentRules.cs](../../src/Logic/Siemens/ArgumentRules.cs):66 | `if (rule == ObjectRule.HardwareNetwork && json.Length > 32768) throw new ArgumentException(parameter + " exceeds 32 KiB.");` |
| [Siemens/ArgumentRules.cs](../../src/Logic/Siemens/ArgumentRules.cs):77 | `if (rule == ObjectRule.Safety && obj.Count > 50) throw new ArgumentException("At most 50 properties per request.");` |
| [Siemens/ArgumentRules.cs](../../src/Logic/Siemens/ArgumentRules.cs):80 | `if (obj.Count > 50) throw new ArgumentException(parameter + ": at most 50 entries.");` |
| [Siemens/CfcLogic.cs](../../src/Logic/Siemens/CfcLogic.cs):21 | `private static void RequireText(string value, string parameter, int max = 256)` |
| [Siemens/CfcLogic.cs](../../src/Logic/Siemens/CfcLogic.cs):51 | `if (elements.Count < 200) elements.Add(reader.LocalName);` |
| [Siemens/CfcLogic.cs](../../src/Logic/Siemens/CfcLogic.cs):69 | `else RequireText(modelVersion, "modelVersion", 32);                                     // official example: "V2.0" (S7TIA exchange model version)` |
| [Siemens/CfcLogic.cs](../../src/Logic/Siemens/CfcLogic.cs):86 | `if (action == "add" \|\| action == "change") RequireText(newHashedPassword, "newHashedPassword", 4096);` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):14 | `private static void RequireText(string value, string parameter, int max = 256)` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):21 | `internal static string[] ChartParts(string chartPath) { RequireText(chartPath, "chartPath", 1024); return EngineeringPath.Parts(chartPath); }` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):55 | `RequireText(filePath, "filePath", 1024);` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):77 | `if ((action != "create" && action != "read") \|\| !string.IsNullOrEmpty(blockName)) RequireText(blockName, "blockName", 128);   // read lists all blocks, create may auto-name` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):79 | `if (action == "create") { RequireText(blockType, "blockType", 128); r.BlockType = blockType; r.LibraryName = libraryName ?? ""; if (!string.IsNullOrEmpty(libraryName)) RequireText(libraryName, "libraryName", 128); }` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):102 | `RequireText(blockName, "blockName", 128); RequireText(pinName, "pinName", 128);` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):109 | `if (partner.ContainsKey("chartInterface")) { if (partner.ContainsKey("block") \|\| partner.ContainsKey("pin")) throw new ArgumentException("partnerJson: give chartInterface or block + pin, not both."); r.PartnerInterface = Str("chartInterface"); RequireText(r.PartnerInterface, "partnerJson.chartInterface", 128); }` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):110 | `else { r.PartnerBlock = Str("block"); r.PartnerPin = Str("pin"); RequireText(r.PartnerBlock, "partnerJson.block", 128); RequireText(r.PartnerPin, "partnerJson.pin", 128); }` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):134 | `if (action != "read" && action != "create") RequireText(interfaceName, "interfaceName", 128);` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):135 | `if (action == "create") { RequireText(sourceBlock, "sourceBlock", 128); RequireText(sourcePin, "sourcePin", 128); r.SourceBlock = sourceBlock; r.SourcePin = sourcePin; Refuse(interfaceName, "interfaceName", "is not accepted on create: DccChartInterfaceComposition.Create(DccPin) names the interface after the pin."); }` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):149 | `if (action != "read") RequireText(partitionName, "partitionName", 128);` |
| [Siemens/DccLogic.cs](../../src/Logic/Siemens/DccLogic.cs):164 | `RequireText(filePath, "filePath", 1024);` |
| [Siemens/DownloadPromptPolicy.cs](../../src/Logic/Siemens/DownloadPromptPolicy.cs):54 | `if (string.IsNullOrWhiteSpace(text) \|\| text.Length > 64) throw new ArgumentException("Prompt answer out of range: " + kv.Key);` |
| [Siemens/EngineeringPath.cs](../../src/Logic/Siemens/EngineeringPath.cs):12 | `if (parts.Length > 64 \|\| parts.Any(x => string.IsNullOrWhiteSpace(x) \|\| x == "." \|\| x == ".."))` |
| [Siemens/HardwareAmlLogic.cs](../../src/Logic/Siemens/HardwareAmlLogic.cs):30 | `public const int MaxElements = 5000;` |
| [Siemens/HardwareAmlLogic.cs](../../src/Logic/Siemens/HardwareAmlLogic.cs):107 | `if (dev.TypeIdentifier.Length == 0) throw new ArgumentException("Device '" + dev.Name + "' needs a typeIdentifier such as 'System:Device.S71500'.");` |
| [Siemens/HardwareAmlLogic.cs](../../src/Logic/Siemens/HardwareAmlLogic.cs):127 | `if (++count > MaxElements) throw new ArgumentException("More than " + MaxElements + " device items.");` |
| [Siemens/HardwareAmlLogic.cs](../../src/Logic/Siemens/HardwareAmlLogic.cs):140 | `if ((item.Role == "DeviceItem" \|\| item.Role == "Rack") && !item.BuiltIn && item.TypeIdentifier.Length == 0) throw new ArgumentException("Item '" + item.Name + "' needs a typeIdentifier (e.g. 'OrderNumber:6ES7 521-1BL00-0AB0/V2.0') unless builtIn=true.");` |
| [Siemens/HardwareNetworkLogic.cs](../../src/Logic/Siemens/HardwareNetworkLogic.cs):51 | `internal static string[] ParseNames(string json, string parameter, int max = 64)` |
| [Siemens/HardwareNetworkLogic.cs](../../src/Logic/Siemens/HardwareNetworkLogic.cs):72 | `if (string.IsNullOrEmpty(password) \|\| password.Length > 128 \|\| password.Trim().Length == 0) throw new ArgumentException(parameter + " must be 1-128 characters (never echoed).");` |
| [Siemens/HardwareNetworkLogic.cs](../../src/Logic/Siemens/HardwareNetworkLogic.cs):132 | `if (action == "create" && name.Length > 256) throw new ArgumentException("name too long.");` |
| [Siemens/Hmi/HmiReadSafety.cs](../../src/Logic/Siemens/Hmi/HmiReadSafety.cs):80 | `if (Writer != null && Writer.BaseStream.Length > 8 * 1024 * 1024)` |
| [Siemens/LibraryDeepLogic.cs](../../src/Logic/Siemens/LibraryDeepLogic.cs):84 | `if (array.Count > 200) throw new ArgumentException("selectionJson: at most 200 entries.");` |
| [Siemens/LibraryDeepLogic.cs](../../src/Logic/Siemens/LibraryDeepLogic.cs):101 | `var scopes = HardwareNetworkLogic.ParseNames(string.IsNullOrWhiteSpace(json) ? "[]" : json, "scopeSoftwarePathsJson", 32);` |
| [Siemens/LibraryDeepLogic.cs](../../src/Logic/Siemens/LibraryDeepLogic.cs):146 | `if (string.IsNullOrWhiteSpace(archiveName) \|\| archiveName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 \|\| archiveName.Length > 128) throw new ArgumentException("archiveName must be a plain file name (extension optional; TIA adds .zalXX for compressed modes when none is given).");` |
| [Siemens/LibraryDeepLogic.cs](../../src/Logic/Siemens/LibraryDeepLogic.cs):151 | `if (maxItems < 1 \|\| maxItems > 5000) throw new ArgumentException("maxItems must be 1..5000.");` |
| [Siemens/PlcBlockServicesLogic.cs](../../src/Logic/Siemens/PlcBlockServicesLogic.cs):33 | `if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");` |
| [Siemens/PlcBlockServicesLogic.cs](../../src/Logic/Siemens/PlcBlockServicesLogic.cs):78 | `if (string.IsNullOrWhiteSpace(json) \|\| json.Length > 4096) throw new ArgumentException("culturesJson must be a JSON array of 1..64 culture names.");` |
| [Siemens/PlcBlockServicesLogic.cs](../../src/Logic/Siemens/PlcBlockServicesLogic.cs):81 | `if (names.Length < 1 \|\| names.Length > 64 \|\| names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Provide 1..64 nonempty culture names.");` |
| [Siemens/PlcTagEditingLogic.cs](../../src/Logic/Siemens/PlcTagEditingLogic.cs):57 | `if (string.IsNullOrWhiteSpace(pair.Key) \|\| pair.Key.Length > 16) throw new ArgumentException("Comment culture names look like \"zh-CN\" / \"en-US\": " + pair.Key);` |
| [Siemens/SafetyLogic.cs](../../src/Logic/Siemens/SafetyLogic.cs):45 | `if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");` |
| [Siemens/SafetyLogic.cs](../../src/Logic/Siemens/SafetyLogic.cs):143 | `if (text.Length > 256) throw new ArgumentException("UsernameForFChangeHistory is limited to 256 characters by TIA; longer input is refused rather than silently cut.");` |
| [Siemens/SafetyLogic.cs](../../src/Logic/Siemens/SafetyLogic.cs):165 | `if (layout.Length > 128 \|\| layout.Any(c => char.IsControl(c) \|\| c == '"')) throw new ArgumentException("documentLayout must be a plain layout name such as DocuInfo_ISO_A4_Portrait.");` |
| [Siemens/SafetyValidationLogic.cs](../../src/Logic/Siemens/SafetyValidationLogic.cs):14 | `private static void RequireText(string value, string parameter, int max = 256)` |
| [Siemens/SafetyValidationLogic.cs](../../src/Logic/Siemens/SafetyValidationLogic.cs):65 | `if (action == "createFromMasterCopy") RequireText(masterCopyPath, "masterCopyPath", 1024); else Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");` |
| [Siemens/SafetyValidationLogic.cs](../../src/Logic/Siemens/SafetyValidationLogic.cs):67 | `if (action == "generateReport" \|\| action == "export" \|\| action == "import") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "applies to generateReport / export / import only.");` |
| [Siemens/SafetyValidationLogic.cs](../../src/Logic/Siemens/SafetyValidationLogic.cs):82 | `if (action == "createFromMasterCopy") RequireText(masterCopyPath, "masterCopyPath", 1024); else Refuse(masterCopyPath, "masterCopyPath", "applies to createFromMasterCopy only.");` |
| [Siemens/SafetyValidationLogic.cs](../../src/Logic/Siemens/SafetyValidationLogic.cs):106 | `if (action == "export" \|\| action == "import") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "applies to export / import only.");` |
| [Siemens/SivarcLogic.cs](../../src/Logic/Siemens/SivarcLogic.cs):148 | `if ((pair.Key == "Condition" \|\| pair.Key == "Comment") && (pair.Value?.GetValue<string>() ?? "").Length > 500) throw new ArgumentException(pair.Key + " exceeds the 500 character limit SiVArc enforces.");` |
| [Siemens/SivarcLogic.cs](../../src/Logic/Siemens/SivarcLogic.cs):149 | `if (pair.Key == "Name" && (pair.Value?.GetValue<string>() ?? "").Length > 128) throw new ArgumentException("Name exceeds the 128 character limit SiVArc enforces.");` |
| [Siemens/SivarcLogic.cs](../../src/Logic/Siemens/SivarcLogic.cs):231 | `if (ParseNames(devicePathJson, "devicePathJson").Length == 0 \|\| ParseNames(itemPathJson, "itemPathJson").Length == 0) throw new ArgumentException("devicePathJson and itemPathJson identify the HMI device item (the PNV HMI device).");` |
| [Siemens/SivarcLogic.cs](../../src/Logic/Siemens/SivarcLogic.cs):233 | `if (string.IsNullOrWhiteSpace(expression) \|\| expression.Length > 4000) throw new ArgumentException("expression (SiVArc expression text, max 4000 chars) required.");` |
| [Siemens/SoftwareUnitDeepLogic.cs](../../src/Logic/Siemens/SoftwareUnitDeepLogic.cs):134 | `if (file.Exists && file.Length > 0) { using var stream = file.OpenRead(); row["sha256"] = ArgumentRules.Hash(stream); }` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):15 | `private static void RequireText(string value, string parameter, int max = 1024)` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):40 | `RequireText(userName, "userName", 256); if (string.IsNullOrEmpty(password)) throw new ArgumentException("password is required for connect (official: TeamcenterConnectionProvider.Connect takes a SecureString; it is never logged).");` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):41 | `RequireText(hostUrl, "hostUrl"); RequireText(instance, "instance", 256);` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):46 | `RequireText(hostUrl, "hostUrl"); RequireText(instance, "instance", 256); RequireText(loginUrl, "loginUrl"); RequireText(applicationId, "applicationId", 256);` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):66 | `RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); r.DatasetType = RequireOneOf(datasetType, DatasetTypes, "datasetType"); RequireText(datasetName, "datasetName", 256);` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):80 | `RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); r.LocalCacheOption = RequireOneOf(localCacheOption, LocalCacheOptions, "localCacheOption");` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):100 | `RequireText(r.ItemName, "itemDetailsJson.itemName", 256); RequireText(r.TeamcenterItemType, "itemDetailsJson.teamcenterItemType", 256);   // official: ItemName and TeamcenterItemType are required` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):134 | `if (r.Target == "globalLibrary") RequireText(libraryName, "libraryName", 256); else Refuse(libraryName, "libraryName", "applies to target globalLibrary only.");` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):139 | `if (action == "readCustomAttributes") RequireText(itemType, "itemType", 256);` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):140 | `else if (newRevision) { if (!string.IsNullOrEmpty(itemType)) RequireText(itemType, "itemType", 256); }` |
| [Siemens/TeamcenterLogic.cs](../../src/Logic/Siemens/TeamcenterLogic.cs):142 | `if (toItem) { RequireText(itemId, "itemId", 256); RequireText(revisionId, "revisionId", 256); } else { Refuse(itemId, "itemId", "applies to saveToItem* only."); Refuse(revisionId, "revisionId", "applies to saveToItem* only."); }` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):14 | `private static void RequireText(string value, string parameter, int max = 256)` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):71 | `if (action != "delete") RequireText(filePath, "filePath", 1024); else Refuse(filePath, "filePath", "does not apply to delete.");` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):107 | `if (needsSoftware) RequireText(e.SoftwarePath, "scopeJson.softwarePath", 1024); else Refuse(e.SoftwarePath, "scopeJson.softwarePath", "applies to plc / blocks / tags / types / units entries only.");` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):124 | `if (action == "createFromMasterCopy") { RequireText(masterCopyPath, "masterCopyPath", 1024); if (r.Kind == "testSet") throw new ArgumentException("ApplicationTestSetComposition has no CreateFrom(MasterCopy); kind case only."); }` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):135 | `RequireText(softwarePath, "softwarePath", 1024);` |
| [Siemens/TestSuiteLogic.cs](../../src/Logic/Siemens/TestSuiteLogic.cs):141 | `RequireText(opcUaServerAddress, "opcUaServerAddress", 1024);` |
| [Siemens/WatchTableImportValidation.cs](../../src/Logic/Siemens/WatchTableImportValidation.cs):13 | `using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });` |
| [V4/CompileCandidate.cs](../../src/Logic/V4/CompileCandidate.cs):50 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/CompileResultMapping.cs](../../src/Logic/V4/CompileResultMapping.cs):11 | `public const int MaximumErrorMessages = 10;` |
| [V4/Construction/ConstructionAdapter.cs](../../src/Logic/V4/Construction/ConstructionAdapter.cs):125 | `MaxCharactersInDocument = ConstructionJson.MaxXmlCharacters + (innerOnly ? 256 : 0)` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):29 | `internal const int MaxJsonCharacters = 262144;` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):30 | `internal const int MaxDepth = 16;` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):31 | `internal const int MaxStringCharacters = 4096;` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):32 | `internal const int MaxItems = 1000;` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):33 | `internal const int MaxXmlCharacters = 1048576;` |
| [V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs):34 | `internal const int MaxNetworks = 64;` |
| [V4/Construction/FoundationConstructionValidation.cs](../../src/Logic/V4/Construction/FoundationConstructionValidation.cs):103 | `case BlankStatement blank: Require((blank.Count ?? 1) >= 1 && (blank.Count ?? 1) <= 4096, "blank count must be 1..4096."); break;` |
| [V4/DeviceCreation.cs](../../src/Logic/V4/DeviceCreation.cs):46 | `if (mode == "apply" && (expectedPlanHash.Length != 64 \|\| expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");` |
| [V4/DeviceCreation.cs](../../src/Logic/V4/DeviceCreation.cs):74 | `if (plans.Count >= 128 && !plans.ContainsKey(plan.Hash)) Refuse("The session plan budget is exhausted.", new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/DeviceCreation.cs](../../src/Logic/V4/DeviceCreation.cs):137 | `if (rows == null \|\| rows.Count > 1000 \|\| rows.Any(r => r == null \|\| string.IsNullOrEmpty(r.TypeIdentifier)))` |
| [V4/DeviceCreation.cs](../../src/Logic/V4/DeviceCreation.cs):165 | `if (rows == null \|\| rows.Count > 4096 \|\| rows.Any(i => i == null \|\| string.IsNullOrEmpty(i.Id) \|\| string.IsNullOrEmpty(i.Name) \|\| string.IsNullOrEmpty(i.ParentId))` |
| [V4/DeviceCreationContract.cs](../../src/Logic/V4/DeviceCreationContract.cs):17 | `""typeIdentifier"":{""type"":""string"",""minLength"":1,""maxLength"":512,""description"":""Exact TypeIdentifier from the installed catalog; no normalization or candidate probing.""},` |
| [V4/Domain/DomainNativeValue.cs](../../src/Logic/V4/Domain/DomainNativeValue.cs):11 | `internal const int MaxBytes = 10 * 1024 * 1024;` |
| [V4/Domain/DomainNativeValue.cs](../../src/Logic/V4/Domain/DomainNativeValue.cs):12 | `internal const int MaxDepth = V4Json.MaximumInputDepth;` |
| [V4/Domain/DomainValidation.cs](../../src/Logic/V4/Domain/DomainValidation.cs):118 | `InputGuard.Limit(artifacts!.Count, 256);` |
| [V4/FallbackCandidate.cs](../../src/Logic/V4/FallbackCandidate.cs):53 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/Hmi/HmiSchemas.cs](../../src/Logic/V4/Hmi/HmiSchemas.cs):103 | `if (field == "deviceItems") schema["maxItems"] = 5000;` |
| [V4/Inputs/InputSchema.cs](../../src/Logic/V4/Inputs/InputSchema.cs):148 | `if (!active.Add(name) \|\| active.Count > V4Json.MaximumInputDepth)` |
| [V4/Inputs/NativeValueInputs.cs](../../src/Logic/V4/Inputs/NativeValueInputs.cs):33 | `if (name.Length < 2 \|\| name.Length > 64 \|\| "pPrR".IndexOf(name[0]) < 0) return false;` |
| [V4/Inputs/SequenceInputs.cs](../../src/Logic/V4/Inputs/SequenceInputs.cs):89 | `if (nameCount < 0 \|\| nameCount > 200) throw new ArgumentOutOfRangeException(nameof(nameCount));` |
| [V4/Inputs/SequenceInputs.cs](../../src/Logic/V4/Inputs/SequenceInputs.cs):97 | `if (nameCount < 0 \|\| nameCount > 200) throw new ArgumentOutOfRangeException(nameof(nameCount));` |
| [V4/Plan.cs](../../src/Logic/V4/Plan.cs):80 | `V4Validation.Require(exists ? byteLength >= 0 && sha256 != null : byteLength == null && sha256 == null,` |
| [V4/PlcExport.cs](../../src/Logic/V4/PlcExport.cs):79 | `if (objects.Length < 1 \|\| objects.Length > request.MaxItems \|\| objects.Any(o => o == null \|\| string.IsNullOrEmpty(o.Id) \|\| string.IsNullOrEmpty(o.Path)` |
| [V4/PlcExport.cs](../../src/Logic/V4/PlcExport.cs):106 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/PlcExport.cs](../../src/Logic/V4/PlcExport.cs):129 | `if (readback.Length != attempt.Staged.Length \|\| readback.Where((f, i) => !f.Exists \|\| f.ByteLength != attempt.Staged[i].ByteLength \|\| f.Sha256 != attempt.Staged[i].Sha256).Any())` |
| [V4/PlcExport.cs](../../src/Logic/V4/PlcExport.cs):179 | `if (!unknown && children.Any(c => c.Result.Ok)) error = new Error("Verified exports succeeded before the batch stopped.", new PartialFailureDetails(children.Count(c => c.Result.Ok), 1, Math.Max(0, objects.Length - index - 1)));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):75 | `if (mode == "apply" && !confirm) Refuse("Apply requires explicit confirmation.", new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 && expectedPlanHash.All(c => "0123456789abcdef".Contains(c)) ? expectedPlanHash : null));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):76 | `if (mode == "apply" && (expectedPlanHash.Length != 64 \|\| expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):86 | `if (inputs.Length < 1 \|\| inputs.Length > request.MaxItems) Refuse("Selected imports exceed the item budget or are empty.", new LimitExceededDetails("inputs", request.MaxItems, inputs.Length));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):112 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse("The session plan budget is exhausted.", new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):192 | `if (failureOutcome != Outcome.Unknown && succeeded > 0) error = new Error("Verified imports succeeded before the batch stopped.", new PartialFailureDetails(succeeded, index < inputs.Length ? 1 : 0, Math.Max(0, inputs.Length - index - 1)));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):205 | `if (!stream.CanRead \|\| !stream.CanSeek \|\| stream.Length < 1 \|\| stream.Length > 16 * 1024 * 1024) Invalid("file-size");` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):210 | `{ if (memory.Length + count > 16 * 1024 * 1024) Invalid("file-size"); memory.Write(buffer, 0, count); }` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):218 | `if (files.Sum(f => f.ByteLength!.Value) > 128 * 1024 * 1024) Refuse("The aggregate file budget is exceeded.", new LimitExceededDetails("inputBytes", 128 * 1024 * 1024, files.Sum(f => f.ByteLength!.Value)));` |
| [V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs):229 | `if (rows == null \|\| rows.Count > 4096 \|\| rows.Any(r => r == null \|\| string.IsNullOrEmpty(r.Id) \|\| string.IsNullOrEmpty(r.Name) \|\| string.IsNullOrEmpty(r.Kind))` |
| [V4/SaveCloseCandidate.cs](../../src/Logic/V4/SaveCloseCandidate.cs):37 | `if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null));` |
| [V4/SaveCloseCandidate.cs](../../src/Logic/V4/SaveCloseCandidate.cs):39 | `Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null), "Discard needs its own confirmDiscard=true confirmation.");` |
| [V4/SaveCloseCandidate.cs](../../src/Logic/V4/SaveCloseCandidate.cs):40 | `if (mode == "apply" && (expectedPlanHash.Length != 64 \|\| expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");` |
| [V4/SaveCloseCandidate.cs](../../src/Logic/V4/SaveCloseCandidate.cs):77 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):44 | `if (mode == "apply" && !confirm) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null));` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):45 | `if (mode == "apply" && request.Upgrade == "allow" && !confirmUpgrade) Refuse(new ConfirmationRequiredDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null), "Upgrade needs its own confirmUpgrade=true confirmation.");` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):46 | `if (mode == "apply" && (expectedPlanHash.Length != 64 \|\| expectedPlanHash.Any(c => !"0123456789abcdef".Contains(c)))) Invalid("expectedPlanHash");` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):106 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):155 | `if (string.IsNullOrWhiteSpace(path) \|\| path.Length > 4096 \|\| path.Any(char.IsControl) \|\| !Path.IsPathRooted(path)` |
| [V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs):169 | `if (observation == null \|\| observation.State == null \|\| observation.Processes == null \|\| observation.Processes.Length > 1024` |
| [V4/SourceCandidate.cs](../../src/Logic/V4/SourceCandidate.cs):31 | `if (request.Items.Length > 256) Refuse(new LimitExceededDetails("items", 256, request.Items.Length));` |
| [V4/SourceCandidate.cs](../../src/Logic/V4/SourceCandidate.cs):76 | `if (plans.Count >= 128 && !plans.ContainsKey(hash)) Refuse(new LimitExceededDetails("plans", 128, plans.Count + 1));` |
| [V4/SourceCandidate.cs](../../src/Logic/V4/SourceCandidate.cs):104 | `if (!plans.TryGetValue(expectedPlanHash, out var reviewed) \|\| consumed.Contains(expectedPlanHash)) Refuse(new PlanStaleDetails(expectedPlanHash.Length == 64 ? expectedPlanHash : null, "missing-or-consumed-plan"));` |
| [V4/V4Json.cs](../../src/Logic/V4/V4Json.cs):58 | `internal const int MaximumInputDepth = 64;` |
| [V4/V4Json.cs](../../src/Logic/V4/V4Json.cs):68 | `using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumInputDepth + 1 });` |
| [V4/V4Json.cs](../../src/Logic/V4/V4Json.cs):77 | `var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = MaximumInputDepth + 1 });` |
| [V4/V4Validation.cs](../../src/Logic/V4/V4Validation.cs):57 | `internal static void Hash(string value) => Require(value != null && value.Length == 64` |
| [src/Shared/HardwareAddressTools.cs](../../src/Shared/HardwareAddressTools.cs):274 | `[McpServerTool(Name="SetDeviceAddress"), Description("[L2][Hardware][WRITE] Edit one exact Address of a device item, identified by ioType (Input/Output/Diagnosis/Substitute) and its current startAddress: properties StartAddress/Length and attributes ProcessImage/IsochronousMode/InterruptObNumber, each read back. processImageObName (with softwarePath) assigns the process image partition to that OB: Address.AssignProcessImageToOrganizationBlock on V20, the address's ProcessImageProvider service on V21. Changing StartAddress may move the opposite IoType of the module and never rewires tags. Default dryRun=true; no save/compile/download.")]` |
| [src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs):91 | `if (args.Count > 100 \|\| args.Any(a => !(a is JsonValue v) \|\| !v.TryGetValue<string>(out _))) throw new ArgumentException("Use at most 100 string arguments.");` |
| [src/Shared/Host/EngineeringDiagnosticsTools.cs](../../src/Shared/Host/EngineeringDiagnosticsTools.cs):29 | `if (!Path.IsPathRooted(filePath) \|\| !file.Exists \|\| !file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) \|\| file.Length > 20 * 1024 * 1024)` |
| [src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs):162 | `if (operations.Length > 50) return new Error("Batch count exceeds its limit.", new LimitExceededDetails("operations", 50, operations.Length));` |
| [src/Shared/Host/OfflineAnalysisTools.cs](../../src/Shared/Host/OfflineAnalysisTools.cs):19 | `[McpServerTool(Name = "ComparePlcBlockDocuments"), Description("[L2][Validation][READ] Semantic diff of two exported PLC block documents (SimaticML .xml, SIMATIC SD .s7dcl with sibling .s7res, or external .scl) with volatile noise removed (ID/UId/IId/RefId, DocumentInfo timestamps and product versions, GUIDs, ISO timestamps, MLC_* ids). Each side is EITHER an existing absolute file path (leftFilePath/rightFilePath; no TIA Portal needed) OR an exact block path in the open project (leftBlockPath/rightBlockPath + softwarePath; the block is exported to a temp directory that is deleted afterwards). Returns identicalAfterNormalization, a structural report (block attributes, interface members added/removed/type-changed, network count/titles/languages) and paginated Myers line hunks over the canonical form. Both sides must be given; mixing a file and a block is allowed. Diff refused above 60000 normalized lines per side. Nothing is saved, compiled or downloaded. Native export branches retain behaviorPolicy=current pending V4 native acceptance.")]` |
| [src/Shared/Host/PlcDocumentationTools.cs](../../src/Shared/Host/PlcDocumentationTools.cs):145 | `if (source.Length > 4_000_000) throw new ArgumentException("Source exceeds 4 MB.");` |

共 314 个边界表达式；包含第 I 步由 src/Shared/shared-native/*.props 引用的共享原语及 src/Logic/V4 校验。路径移动只改变排序/行号，不改变输入契约。

</details>

<details>
<summary>C. 当前响应族 → V4 与保留的标记事实</summary>

| 族 | 当前形状 | V4 映射 | 标记站点（非工具数） | variant |
|---|---|---|---|---|
| F1 | VersionPolicyTool 的 isError 文本/preflight | 准入→rejected-before-operation，error.code/details；无原生动作 | 0 | 0 |
| F2 | POCO/Meta、McpException、success=false | 领域字段→data；异常由错误分类器生成 error；message 不判断成功 | 23 | legacy-existing-meta:2; legacy-independent-verdicts:1; legacy-late-stamp:1; legacy-late-verdict:1; legacy-multiple-dynamic-fields:8; legacy-roundtrip-data-stamp:2; legacy-single-verdict:3; legacy-stamp-then-verdict:2; legacy-stamp-without-verdict:2; legacy-verdict-last:1 |
| F3 | operationSuccess/status/error、执行器 | 按执行证据确定 outcome；逐项结果→data.items；保留完整性与原生 verdict | 16 | legacy-independent-verdicts:1; legacy-migration-page:1; legacy-multiple-dynamic-fields:3; legacy-ok-only:1; legacy-plcsim-complete:1; legacy-plcsim-failure:1; legacy-roundtrip-data-stamp:2; legacy-runtime-settings:1; legacy-single-verdict:3; legacy-success-last:1; legacy-verdict-last:1 |
| F4 | Message 中序列化 JSON/failed 文本 | CallTool 透传目标 envelope；批次逐项 envelope；无二次编码 | 2 | legacy-independent-verdicts:1; legacy-stamp-then-verdict:1 |
| F5 | 导出句柄 ok/InvalidParams | data.export 与 meta.paging；缺句柄 NOT_FOUND，覆盖 ALREADY_EXISTS | 0 | 0 |
| F6 | Portal 文本失败，无 meta | 按实际分支判定，边界生成 error/outcome；无法证实写入结果则 unknown | 0 | 0 |
| F7 | Foundation PascalCase DTO/裸数组/V17 envelope | data 保留原领域数据及 evidence；Executed→meta.execution，RequiresSessionReset→meta；裸数组→data.items | 0 | 0 |
| CLI | 报告 ok/roundtrip/后写判定 | 同 envelope、同 outcome；退出码见正文；报告正文/路径进入 data | 0 | 0 |

共 41 个注释站点、16 个 variant；未标记的手写形状仍由 Inventory-ResponseEnvelopes.py 管理。F6 无标记不代表无此类结果。

| variant | 源码文件 |
|---|---|
| legacy-existing-meta | [ModelContextProtocol/Tools/TechnologyObjectsTools.cs](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs)<br>[ModelContextProtocol/Tools/TypesTools.cs](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs) |
| legacy-independent-verdicts | [Siemens/Services/HardwareServicesService.cs](../../src/Engine/Siemens/Services/HardwareServicesService.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs)<br>[src/Shared/Host/V21EcosystemTools.cs](../../src/Shared/Host/V21EcosystemTools.cs) |
| legacy-late-stamp | [ModelContextProtocol/Tools/PlcSoftwareTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs) |
| legacy-late-verdict | [ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs) |
| legacy-migration-page | [Siemens/Services/MigrationReadService.cs](../../src/Engine/Siemens/Services/MigrationReadService.cs) |
| legacy-multiple-dynamic-fields | [ModelContextProtocol/Tools/DocumentsTools.cs](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs)<br>[ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs)<br>[ModelContextProtocol/Tools/PlcExternalSourcesTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs)<br>[ModelContextProtocol/Tools/TypesTools.cs](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs)<br>[Siemens/Services/OpcUaService.cs](../../src/Engine/Siemens/Services/OpcUaService.cs)<br>[Siemens/Services/UnifiedHmiService.cs](../../src/Engine/Siemens/Services/UnifiedHmiService.cs)<br>[Siemens/Services/VersionControlService.cs](../../src/Engine/Siemens/Services/VersionControlService.cs)<br>[src/Shared/CapabilitySelfTestLogic.cs](../../src/Shared/CapabilitySelfTestLogic.cs)<br>[src/Shared/HardwareNetworkTools.cs](../../src/Shared/HardwareNetworkTools.cs)<br>[src/Shared/Host/EngineeringDiagnosticsTools.cs](../../src/Shared/Host/EngineeringDiagnosticsTools.cs)<br>[src/Shared/Host/OfflineSuiteTools.cs](../../src/Shared/Host/OfflineSuiteTools.cs) |
| legacy-ok-only | [Siemens/Services/HardwareServicesService.cs](../../src/Engine/Siemens/Services/HardwareServicesService.cs) |
| legacy-plcsim-complete | [ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| legacy-plcsim-failure | [ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs) |
| legacy-roundtrip-data-stamp | [Siemens/Services/OpcUaService.cs](../../src/Engine/Siemens/Services/OpcUaService.cs)<br>[Siemens/Services/PlcTablesService.cs](../../src/Engine/Siemens/Services/PlcTablesService.cs)<br>[src/Shared/HmiOfflineTools.cs](../../src/Shared/HmiOfflineTools.cs)<br>[src/Shared/Host/OfflineSuiteTools.cs](../../src/Shared/Host/OfflineSuiteTools.cs) |
| legacy-runtime-settings | [Siemens/Services/RuntimeSettingsService.cs](../../src/Engine/Siemens/Services/RuntimeSettingsService.cs) |
| legacy-single-verdict | [ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs)<br>[ModelContextProtocol/Tools/RuntimeChannelTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs)<br>[Siemens/Services/OnlineDownloadService.cs](../../src/Engine/Siemens/Services/OnlineDownloadService.cs)<br>[Siemens/Services/PlcTablesService.cs](../../src/Engine/Siemens/Services/PlcTablesService.cs)<br>[Siemens/Services/UnifiedHmiService.cs](../../src/Engine/Siemens/Services/UnifiedHmiService.cs)<br>[src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs) |
| legacy-stamp-then-verdict | [ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Shared/Host/McpServer.Maintenance.cs](../../src/Shared/Host/McpServer.Maintenance.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs) |
| legacy-stamp-without-verdict | [src/Shared/HardwareAddressTools.cs](../../src/Shared/HardwareAddressTools.cs)<br>[src/Shared/ModulesTools.cs](../../src/Shared/ModulesTools.cs) |
| legacy-success-last | [Siemens/Services/AlarmsService.cs](../../src/Engine/Siemens/Services/AlarmsService.cs) |
| legacy-verdict-last | [Siemens/Services/UnifiedHmiService.cs](../../src/Engine/Siemens/Services/UnifiedHmiService.cs)<br>[src/Shared/HardwareDevicesTools.cs](../../src/Shared/HardwareDevicesTools.cs) |

</details>

<details>
<summary>D. 产品输出与程序集（读取项目属性）</summary>

| 项目 | 发布键 | AssemblyName | 4.0 安装 EXE | 框架不变 |
|---|---|---|---|---|
| [TiaMcp.FoundationHost.csproj](../../src/FoundationHost/TiaMcp.FoundationHost.csproj) | 14sp1–19 | TiaMcp.FoundationHost | runtime/v<key>/TiaMcp.FoundationHost.exe | net10.0 |
| [TiaMcp.Engine.V20.csproj](../../src/Engine/TiaMcp.Engine.V20.csproj) | 20 | TiaMcp.Engine.V20 | runtime/v20/TiaMcp.Engine.V20.exe | net48 |
| [TiaMcp.Engine.V21.csproj](../../src/Engine/TiaMcp.Engine.V21.csproj) | 21 | TiaMcp.Engine.V21 | runtime/v21/TiaMcp.Engine.V21.exe | net48 |

| 公共库/桌面项目 | 当前目标框架（源码属性） |
|---|---|
| [TiaMcp.Logic.csproj](../../src/Logic/TiaMcp.Logic.csproj) | net48;net10.0 |
| [Gui/TiaOpenness.Gui.csproj](../../src/Studio/Gui/TiaOpenness.Gui.csproj) | net10.0-windows |
| [Core/TiaOpenness.Core.csproj](../../src/Studio/Core/TiaOpenness.Core.csproj) | net48;net10.0-windows |

| 配置来源 | 当前键/变量（源码提取） | 4.0 处理 |
|---|---|---|
| [src/Shared/BundleLayout.cs](../../src/Shared/BundleLayout.cs) | `--bundle-root`, `TIA_MCP_BUNDLE_ROOT` | P6-37：--bundle-root 优先于 TIA_MCP_BUNDLE_ROOT，再用已知锚点；旧 TIA_MCP_REPOSITORY_ROOT 已删除 |
| [ModelContextProtocol/Tools/McpServer.Profile.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Profile.cs) | `TIA_MCP_PROFILE` | lite/full 名称保留，名单改为 V4 数据 |
| [Gui/Configuration/ClientProfiles.cs](../../src/Studio/Gui/Configuration/ClientProfiles.cs) | `tia-portal`, `tia-portal-vm` | server key 保留，command/args 改用新产品表 |
| [Cli/McpConfigInstaller.cs](../../src/Engine/Cli/McpConfigInstaller.cs) | `mcpServers`, `servers`, `tia-portal` | JSON/TOML 根及 server key 保留，command/args 更新 |
| [HostOptions.cs](../../src/FoundationHost/HostOptions.cs) | `--tia-portal-location`, `--worker-exe` | 精确版本与显式 worker 输入继续支持 |
| [src/Shared/DataLocations.cs](../../src/Shared/DataLocations.cs) | `TIA_MCP_PLC_TOOLS_PYTHON` | P6-39：显式 Python 优先；缺省环境为 LocalAppData/TiaMcp/ecosystem-python |

</details>

<details>
<summary>E1. 当前资源定位/私有默认值与目标政策（源码定位生成）</summary>

| 当前位置/定位词 | 当前事实 | 4.0 目标 |
|---|---|---|
| [ModelContextProtocol/Builders/EcosystemFiles.cs](../../src/Logic/ModelContextProtocol/Builders/EcosystemFiles.cs):12 `RepositoryRoot` | P6-37：仅委托 BundleLayout.RequireRoot；不再探测祖先脚本或旧 root 变量 | 严格 bundle-root；缺资源拒绝 |
| [src/Shared/Host/McpServer.Maintenance.cs](../../src/Shared/Host/McpServer.Maintenance.cs):51 `FindInstallRoot` | P6-37：BundleLayout.FindRoot 后核对精确 runtime 发布键与 manifest/delivery.json；仅用于更新器资格判断 | 已知安装根 + delivery 标记 |
| [Cli/SpecLoader.cs](../../src/Engine/Cli/SpecLoader.cs):48 `FindBundleRoot` | P6-37：BundleLayout.RequireRoot 并要求 templates 资源；仅解析 __BUNDLE__ token | 显式根/已知锚点；未解析 __BUNDLE__ 报错 |
| [Siemens/EngineRouter.cs](../../src/Engine/Siemens/EngineRouter.cs):35 `FindSiblingExe` | P6-37：通过 BundleLayout.RequireEngine 选择版本表中的精确产品名；无候选名扫描 | 版本目录表 + 精确新产品名 |
| [Cli/McpConfigInstaller.cs](../../src/Engine/Cli/McpConfigInstaller.cs):86 `RequireEngine` | P6-37 已改：目标版本引擎缺失时报 RESOURCE_UNAVAILABLE，不再使用自身 | 缺版本引擎报 RESOURCE_UNAVAILABLE |
| [Gui/ConfigurationPage.cs](../../src/Studio/Gui/ConfigurationPage.cs):27 `FindBundleRoot` | P6-38：仅委托 BundleLayout.RequireWorkbenchRoot，校验正式包标记或已知 Studio 锚点 | 显式根或已知锚点 |
| [Gui/Configuration/ConfigCore.cs](../../src/Studio/Gui/Configuration/ConfigCore.cs):80 `RequireWorkbenchRoot` | P6-38 已改：工作台经 BundleLayout 严格定位根并从产品表取引擎路径 | 严格根校验、新产品目录 |
| [Gui/Configuration/UpdateCheck.cs](../../src/Studio/Gui/Configuration/UpdateCheck.cs):42 `RequirePath` | P6-38 已改：资源只在包根内严格解析，缺失即报错 | 严格资源解析，worktree 更新保护保留 |
| [Client/BridgeClient.cs](../../src/Studio/Client/BridgeClient.cs):77 `BundleLayout` | P6-38：BundleLayout.RequireWorkbenchBridge 使用正式相邻布局或已知 worktree 输出锚点；不猜同级 Debug/Release | 仅正式相邻部署/已知开发锚点/显式 bridgeExePath |
| [Core/Abstractions/SessionFactoryLoader.cs](../../src/Studio/Core/Abstractions/SessionFactoryLoader.cs):23 `TiaOpenness.Openness` | R14 当前 Studio adapter 路径 | 仍由 G3/J 验收控制，不随布局变更切换 |
| [Launcher/Launcher.cs](../../src/Studio/Launcher/Launcher.cs):29 `TiaOpenness.exe` | R12 根启动器目标 | 正式根 TiaOpenness.exe 启动 runtime/studio/TiaOpenness.exe |
| [src/Shared/DataLocations.cs](../../src/Shared/DataLocations.cs):70 `TIA_MCP_DATA_DIRECTORY` | 显式数据根或 bundle/data；不可写时按用途回退用户目录 | 沿用数据根政策；logs 按发布键/studio 分组 |
| [src/Shared/InvocationJournal.cs](../../src/Shared/InvocationJournal.cs):87 `DiagnosticsDirectory` | P6-39：调用/原生证据仍在 data/diagnostics；PID + 启动时间 + GUID 独立日志流 | P6-45 读取；P6-46 保留与时间窗口，logs/audit 为每数据根单链 |
| [Program.cs](../../src/Engine/Program.cs):1042 `AppendLog` | P6-39：启动与主日志合为 logs/<releaseKey>/TiaMcpServer-<processKey>.log | 只读安装回退 TEMP/TiaMcp/logs；每用途保留最新 32 份；失败每进程报一次 IO_FAILED |
| [Gui/App.xaml.cs](../../src/Studio/Gui/App.xaml.cs):14 `.crash.log` | P6-39：Studio 崩溃日志在 logs/studio，PID/启动时间文件名 | Workbench 根政策；无可写位置报 IO_FAILED |
| [src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs):95 `EcosystemPythonExecutable` | P6-39：LocalAppData/TiaMcp/ecosystem-python，显式 Python 优先 | 不执行或迁移旧私有环境；缺失/不可写报 IO_FAILED |
| [Cli/ReportBuilders.cs](../../src/Engine/Cli/ReportBuilders.cs):44 `GetWorkspaceRoot` | P6-39：显式 --workspace-root；无包根/cwd/私人目录探测 | 缺输入 INVALID_ARGUMENT；CLI 语法退出 64 |
| [Cli/HmiTemplateBuilder.cs](../../src/Engine/Cli/HmiTemplateBuilder.cs):63 `RequireInput` | P6-39：显式 HMI 模板路径与 workspace 输出根 | 缺输入 INVALID_ARGUMENT；不猜 TIA_MCP_AI_PACK |
| [ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs](../../src/Logic/ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs):17 `string workspaceRoot` | P6-39：显式 fixtureDirectory 与 workspaceRoot；不从夹具反推包根 | 缺输入 INVALID_ARGUMENT；先拒绝再写报告 |
| [ModelContextProtocol/Tools/OnlineToolPolicy.cs](../../src/Engine/ModelContextProtocol/Tools/OnlineToolPolicy.cs):26 `WithAutoOffline` | D1 行为保持 current；L5 未运行前仍保留现行路线实现 | L5 通过后切换 OFFLINE_REQUIRED，不重试 |
| [Siemens/Services/OnlineDownloadService.cs](../../src/Engine/Siemens/Services/OnlineDownloadService.cs):116 `ApplyConfiguration` | D1 行为保持 current；L5 未运行前仍保留现行候选路线 | L5 通过后显式路线，失败/未知即停止 |

</details>

<details>
<summary>E. 布局、构建、打包、校验、Studio 和文档修改位置（生成扫描）</summary>

| 文件 | 类别:全部命中行 | 实施方式 |
|---|---|---|
| [docs/development/repository-layout.md](../../docs/development/repository-layout.md) | 退役根变量:56 | BundleLayout 迁移证据只读保留；现行定位规则在同页明确 |
| [docs/development/runtime-layout.md](../../docs/development/runtime-layout.md) | 退役根变量:114 | BundleLayout 迁移证据只读保留；现行定位规则在同页明确 |
| [docs/releases/v3.2.0.md](../../docs/releases/v3.2.0.md) | 退役产品名:48 | 历史发布/验收证据只读保留，不作为 V4 改写目标 |
| [docs/releases/v3.3.0.md](../../docs/releases/v3.3.0.md) | 退役产品名:24,76 | 历史发布/验收证据只读保留，不作为 V4 改写目标 |
| [manifest/history/contracts-v3/responses/20.json](../../manifest/history/contracts-v3/responses/20.json) | 退役产品名:66 | 历史契约只读归档；不编辑内容、不重算哈希 |
| [manifest/history/contracts-v3/responses/21.json](../../manifest/history/contracts-v3/responses/21.json) | 退役产品名:55 | 历史契约只读归档；不编辑内容、不重算哈希 |
| [manifest/ecosystem-validation.json](../../manifest/ecosystem-validation.json) | 退役产品名:31,37 | 生成记录只由所属生成器重建；不手工改写扫描命中 |
| [scripts/operations/delivery-files.json](../../scripts/operations/delivery-files.json) | 退役产品名:91,92,97,98,137,138,143,144 | 更新器的排除与旧文件清理名单：按名删除退役文件，不是现行引用 |

共 8 个候选文件。扫描只匹配退役的产品文件名、TIA_MCP_REPOSITORY_ROOT 和私人默认路径标记；E1 表逐项核对仍在使用的严格 BundleLayout 入口。发布/验收/迁移证据和生成清单按其只读或再生成规则标注。扫描不命中已批准的新产品名、BundleLayout 包装器、显式 workspaceRoot 或当前日志文件名。

</details>

<details>
<summary>F. V20/V21 lite 数据提案（每项均有现有调用示例）</summary>

| 4.0 名称 | 当前示例入口 | 选择理由 | 版本 |
|---|---|---|---|
| `AttachOpenProject` | `AttachOpenProject` | Shared Foundation contract | 20, 21 |
| `BuildFlgNetCall` | `BuildFlgNetCall` | Shared Foundation contract | 20, 21 |
| `BuildPlcFbBlock` | `BuildPlcFbBlock` | Shared Foundation contract | 20, 21 |
| `BuildPlcFcBlock` | `BuildPlcFcBlock` | Shared Foundation contract | 20, 21 |
| `BuildPlcGlobalDb` | `BuildPlcGlobalDb` | Shared Foundation contract | 20, 21 |
| `BuildPlcLadFcBlock` | `BuildPlcLadFcBlock` | Shared Foundation contract | 20, 21 |
| `BuildPlcSymbolManifestFromPath` | `BuildPlcSymbolManifestFromPath` | Shared Foundation contract | 20, 21 |
| `BuildPlcTagTable` | `BuildPlcTagTable` | Shared Foundation contract | 20, 21 |
| `BuildPlcUdt` | `BuildPlcUdt` | Shared Foundation contract | 20, 21 |
| `BuildStructuredText` | `BuildStructuredText` | Shared Foundation contract | 20, 21 |
| `CallTool` | `CallTool` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `CleanupStagedImportFiles` | `CleanupStagedImportFiles` | Shared Foundation contract | 20, 21 |
| `CloseProject` | `CloseProject` | Shared Foundation contract | 20, 21 |
| `CompilePlcDiagnostics` | `CompilePlcDiagnostics` | Shared Foundation contract | 20, 21 |
| `CompilePlcSoftware` | `CompilePlcSoftware` | Shared Foundation contract | 20, 21 |
| `ConnectPortal` | `ConnectPortal` | Shared Foundation contract | 20, 21 |
| `CreateHardwareDevice` | `CreateHardwareDevice` | Shared Foundation contract | 20, 21 |
| `CreatePlcTag` | `CreatePlcTag` | Shared Foundation contract | 20, 21 |
| `CreatePlcTagTable` | `CreatePlcTagTable` | Shared Foundation contract | 20, 21 |
| `CreatePlcUserConstant` | `CreatePlcUserConstant` | Shared Foundation contract | 20, 21 |
| `CreateProject` | `CreateProject` | Shared Foundation contract | 20, 21 |
| `DeletePlcExternalSource` | `DeletePlcExternalSource` | Shared Foundation contract | 20, 21 |
| `DisconnectPortal` | `DisconnectPortal` | Shared Foundation contract | 20, 21 |
| `ExportPlcBlock` | `ExportPlcBlock` | Shared Foundation contract | 20, 21 |
| `ExportPlcBlocks` | `ExportPlcBlocks` | Shared Foundation contract | 20, 21 |
| `ExportPlcTagTable` | `ExportPlcTagTable` | Shared Foundation contract | 20, 21 |
| `ExportPlcType` | `ExportPlcType` | Shared Foundation contract | 20, 21 |
| `ExportPlcTypes` | `ExportPlcTypes` | Shared Foundation contract | 20, 21 |
| `ExportPlcWatchTable` | `ExportPlcWatchTable` | Shared Foundation contract | 20, 21 |
| `ExportTechnologyObject` | `ExportTechnologyObject` | Shared Foundation contract | 20, 21 |
| `FindTools` | `FindTools` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `GenerateBlocksFromExternalSource` | `GenerateBlocksFromExternalSource` | Shared Foundation contract | 20, 21 |
| `GetOpennessWorkerStatus` | `GetOpennessWorkerStatus` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `GetPlcBlockHierarchy` | `GetPlcBlockHierarchy` | Shared Foundation contract | 20, 21 |
| `GetPlcBlockInfo` | `GetPlcBlockInfo` | Shared Foundation contract | 20, 21 |
| `GetPlcTypeInfo` | `GetPlcTypeInfo` | Shared Foundation contract | 20, 21 |
| `GetPortalConnectionReadiness` | `GetPortalConnectionReadiness` | Shared Foundation contract | 20, 21 |
| `GetProjectInfo` | `GetProjectInfo` | Shared Foundation contract | 20, 21 |
| `GetProjectTree` | `GetProjectTree` | Shared Foundation contract | 20, 21 |
| `GetSessionState` | `GetSessionState` | Shared Foundation contract | 20, 21 |
| `GetSoftwareInfo` | `GetSoftwareInfo` | Shared Foundation contract | 20, 21 |
| `GetSoftwareTree` | `GetSoftwareTree` | Shared Foundation contract | 20, 21 |
| `GetToolUsage` | `GetToolUsage` | Shared Foundation contract | 20, 21 |
| `ImportPlcBlock` | `ImportPlcBlock` | Shared Foundation contract | 20, 21 |
| `ImportPlcBlocksFromDirectory` | `ImportPlcBlocksFromDirectory` | Shared Foundation contract | 20, 21 |
| `ImportPlcExternalSource` | `ImportPlcExternalSource` | Shared Foundation contract | 20, 21 |
| `ImportPlcProgramFromDirectory` | `ImportPlcProgramFromDirectory` | Shared Foundation contract | 20, 21 |
| `ImportPlcTagTable` | `ImportPlcTagTable` | Shared Foundation contract | 20, 21 |
| `ImportPlcType` | `ImportPlcType` | Shared Foundation contract | 20, 21 |
| `InitializeEnvironment` | `InitializeEnvironment` | Shared Foundation contract | 20, 21 |
| `ListPlcBlocks` | `ListPlcBlocks` | Shared Foundation contract | 20, 21 |
| `ListPlcExternalSources` | `ListPlcExternalSources` | Shared Foundation contract | 20, 21 |
| `ListPlcSystemConstants` | `ListPlcSystemConstants` | Shared Foundation contract | 20, 21 |
| `ListPlcTagTables` | `ListPlcTagTables` | Shared Foundation contract | 20, 21 |
| `ListPlcTags` | `ListPlcTags` | Shared Foundation contract | 20, 21 |
| `ListPlcTypes` | `ListPlcTypes` | Shared Foundation contract | 20, 21 |
| `ListPlcUserConstants` | `ListPlcUserConstants` | Shared Foundation contract | 20, 21 |
| `ListPlcWatchTables` | `ListPlcWatchTables` | Shared Foundation contract | 20, 21 |
| `ListPortalProcessProjects` | `ListPortalProcessProjects` | Shared Foundation contract | 20, 21 |
| `ListStagedImportFiles` | `ListStagedImportFiles` | Shared Foundation contract | 20, 21 |
| `ListTechnologyObjects` | `ListTechnologyObjects` | Shared Foundation contract | 20, 21 |
| `ListToolCategories` | `ListToolCategories` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `OpenProject` | `OpenProject` | Shared Foundation contract | 20, 21 |
| `PlanArtifactImportOrder` | `PlanArtifactImportOrder` | Shared Foundation contract | 20, 21 |
| `PlanPlcExternalSourceImport` | `PlanPlcExternalSourceImport` | Shared Foundation contract | 20, 21 |
| `PreviewToolCall` | `PreviewToolCall` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `RenderPlcBlock` | `RenderPlcBlock` | Shared Foundation contract | 20, 21 |
| `RenderPlcProgramAtlas` | `RenderPlcProgramAtlas` | Shared Foundation contract | 20, 21 |
| `RestartOpennessWorker` | `RestartOpennessWorker` | Engine discovery/bridge/worker supervisor | 20, 21 |
| `RunCapabilitySelfTest` | `RunCapabilitySelfTest` | Shared Foundation contract | 20, 21 |
| `SaveProject` | `SaveProject` | Shared Foundation contract | 20, 21 |
| `SearchHardwareCatalog` | `SearchHardwareCatalog` | Shared Foundation contract | 20, 21 |
| `StageImportFiles` | `StageImportFiles` | Shared Foundation contract | 20, 21 |

数据文件：[ToolProfiles.resx](../../src/Logic/ModelContextProtocol/ToolProfiles.resx)。Catalog JSON 按 contractVersion、releaseKey 记录 V4/current/source 名称、profiles 和 arguments；参数示例取自 reference/tool-examples/calls.json，按当前契约转换并逐版验证。Foundation 继续不设 lite。

</details>

<details>
<summary>G. 完整引擎契约迁移任务的工具文件所有权</summary>

| 任务 | 工具源文件 | 当前注册入口数 |
|---|---|---|
| P6-67 | [ModelContextProtocol/Tools/ImportStagingTools.cs](../../src/Engine/ModelContextProtocol/Tools/ImportStagingTools.cs) | 3 |
| P6-07 | [ModelContextProtocol/Tools/McpServer.CallDiscipline.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.CallDiscipline.cs)<br>[ModelContextProtocol/Tools/ToolUsageTools.cs](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs)<br>[src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs)<br>[src/Shared/Host/McpServer.Exports.cs](../../src/Shared/Host/McpServer.Exports.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs) | 10 |
| P6-09 | [ModelContextProtocol/Tools/EngineeringAuditTools.cs](../../src/Engine/ModelContextProtocol/Tools/EngineeringAuditTools.cs)<br>[ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs)<br>[ModelContextProtocol/Tools/ImportOrderTools.cs](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs)<br>[ModelContextProtocol/Tools/PlcBuildTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBuildTools.cs)<br>[src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs)<br>[src/Shared/Host/OfflineAnalysisTools.cs](../../src/Shared/Host/OfflineAnalysisTools.cs)<br>[src/Shared/Host/OfflineSuiteTools.cs](../../src/Shared/Host/OfflineSuiteTools.cs)<br>[src/Shared/Host/PlcDocumentationTools.cs](../../src/Shared/Host/PlcDocumentationTools.cs)<br>[src/Shared/Host/QualityAuditTools.cs](../../src/Shared/Host/QualityAuditTools.cs)<br>[src/Shared/Host/TemplateTools.cs](../../src/Shared/Host/TemplateTools.cs)<br>[src/Shared/Host/V21EcosystemTools.cs](../../src/Shared/Host/V21EcosystemTools.cs)<br>[src/Shared/Host/XmlBuilderTools.cs](../../src/Shared/Host/XmlBuilderTools.cs) | 48 |
| P6-10 | [ModelContextProtocol/Tools/McpServer.BlockImportVerification.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.BlockImportVerification.cs)<br>[ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs)<br>[ModelContextProtocol/Tools/PlcSoftwareTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs)<br>[ModelContextProtocol/Tools/PlcTablesTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs)<br>[ModelContextProtocol/Tools/TypesTools.cs](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs)<br>[src/Shared/PlcOfflineTools.cs](../../src/Shared/PlcOfflineTools.cs)<br>[src/Shared/PlcToolContract.cs](../../src/Shared/PlcToolContract.cs) | 54 |
| P6-11 | [ModelContextProtocol/Tools/DocumentsTools.cs](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs)<br>[ModelContextProtocol/Tools/McpServer.Patch.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Patch.cs)<br>[ModelContextProtocol/Tools/NativeExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs)<br>[ModelContextProtocol/Tools/PlcExternalSourcesTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs)<br>[src/Shared/Host/ExportTools.cs](../../src/Shared/Host/ExportTools.cs) | 24 |
| P6-12 | [ModelContextProtocol/Tools/DevicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs)<br>[src/Shared/HardwareAddressTools.cs](../../src/Shared/HardwareAddressTools.cs)<br>[src/Shared/HardwareAmlTools.cs](../../src/Shared/HardwareAmlTools.cs)<br>[src/Shared/HardwareDevicesTools.cs](../../src/Shared/HardwareDevicesTools.cs)<br>[src/Shared/HardwareManagementTools.cs](../../src/Shared/HardwareManagementTools.cs)<br>[src/Shared/ModulesTools.cs](../../src/Shared/ModulesTools.cs) | 26 |
| P6-13 | [ModelContextProtocol/Tools/HardwareNetworkTools.cs](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs)<br>[ModelContextProtocol/Tools/HardwareServicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs)<br>[src/Shared/HardwareNetworkTools.cs](../../src/Shared/HardwareNetworkTools.cs)<br>[src/Shared/HardwareServicesPortTools.cs](../../src/Shared/HardwareServicesPortTools.cs) | 30 |
| P6-14 | [ModelContextProtocol/Tools/CertificateManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs)<br>[ModelContextProtocol/Tools/ProjectSecurityTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs)<br>[ModelContextProtocol/Tools/SafetyManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs)<br>[ModelContextProtocol/Tools/SafetyValidationTools.cs](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs)<br>[ModelContextProtocol/Tools/SecurityDeepTools.cs](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs) | 20 |
| P6-15 | [ModelContextProtocol/Tools/AlarmsTools.cs](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs)<br>[ModelContextProtocol/Tools/OpcUaTools.cs](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs)<br>[ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs)<br>[ModelContextProtocol/Tools/SoftwareUnitManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitManagementTools.cs)<br>[ModelContextProtocol/Tools/TechnologyObjectsTools.cs](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs) | 31 |
| P6-16 | [ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs)<br>[ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs) | 16 |
| P6-17 | [ModelContextProtocol/Tools/UnifiedHmiGroupsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiGroupsTools.cs)<br>[ModelContextProtocol/Tools/UnifiedHmiTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs)<br>[ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs)<br>[ModelContextProtocol/Tools/UnifiedUiModelTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs)<br>[src/Shared/HmiOfflineTools.cs](../../src/Shared/HmiOfflineTools.cs) | 36 |
| P6-18 | [ModelContextProtocol/Tools/HmiExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs)<br>[ModelContextProtocol/Tools/HmiTagDeletionTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiTagDeletionTools.cs)<br>[ModelContextProtocol/Tools/UnifiedExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs) | 17 |
| P6-19 | [ModelContextProtocol/Tools/GlobalScriptEditTools.cs](../../src/Engine/ModelContextProtocol/Tools/GlobalScriptEditTools.cs)<br>[ModelContextProtocol/Tools/GraphicSelectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs)<br>[ModelContextProtocol/Tools/HmiDescribeTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs)<br>[ModelContextProtocol/Tools/HmiInspectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs)<br>[ModelContextProtocol/Tools/MigrationReadTools.cs](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs)<br>[ModelContextProtocol/Tools/ReflectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs)<br>[ModelContextProtocol/Tools/UnifiedEngineeringTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs)<br>[ModelContextProtocol/Tools/UnifiedEventsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs)<br>[ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs) | 49 |
| P6-20 | [ModelContextProtocol/Tools/CfcTools.cs](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs)<br>[ModelContextProtocol/Tools/OptionalEngineeringTools.cs](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs)<br>[ModelContextProtocol/Tools/SpecializedExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/SpecializedExchangeTools.cs)<br>[ModelContextProtocol/Tools/TestSuiteTools.cs](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs)<br>[ModelContextProtocol/Tools/V20OptionsTools.cs](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs) | 14 |
| P6-21 | [ModelContextProtocol/Tools/DccTools.cs](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs)<br>[ModelContextProtocol/Tools/StartdriveTools.cs](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs)<br>[ModelContextProtocol/Tools/TeamcenterTools.cs](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs) | 22 |
| P6-22 | [ModelContextProtocol/Tools/LibraryTools.cs](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs)<br>[ModelContextProtocol/Tools/SivarcTools.cs](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs)<br>[ModelContextProtocol/Tools/VersionControlTools.cs](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs) | 28 |
| P6-23 | [ModelContextProtocol/Tools/OnlineDownloadTools.cs](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs)<br>[ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs)<br>[ModelContextProtocol/Tools/RuntimeChannelTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs)<br>[ModelContextProtocol/Tools/RuntimeSettingsTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs)<br>[ModelContextProtocol/Tools/RuntimeTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs) | 34 |
| P6-24 | [ModelContextProtocol/Tools/DiagnosticsTools.cs](../../src/Engine/ModelContextProtocol/Tools/DiagnosticsTools.cs)<br>[ModelContextProtocol/Tools/ProjectSessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs)<br>[ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Shared/Host/EngineeringDiagnosticsTools.cs](../../src/Shared/Host/EngineeringDiagnosticsTools.cs)<br>[src/Shared/Host/McpServer.Doctor.cs](../../src/Shared/Host/McpServer.Doctor.cs)<br>[src/Shared/Host/McpServer.Maintenance.cs](../../src/Shared/Host/McpServer.Maintenance.cs)<br>[src/Shared/Host/McpServer.Worker.cs](../../src/Shared/Host/McpServer.Worker.cs)<br>[src/Shared/HostMetaTools.cs](../../src/Shared/HostMetaTools.cs) | 31 |

每个完整引擎注册入口恰有一个文件所有者；同 stem 的 Service 路径在 H 表展开。本领域独占规则随该任务，公共 src/Engine/Siemens/Portal 与基础设施由 P6-24 串行集成。Foundation 由 P6-08 单独负责；公共 DTO 与项目文件不归并行领域任务编辑。

</details>

<details>
<summary>H. 全部阶段 6 任务的当前路径与所有权</summary>

| 任务 | 当前源码/文档/验证入口（仓库根相对路径） |
|---|---|
| <a id="phase6-path-p6-01"></a>P6-01 | [scripts/generate/Generate-Phase6Plan.py](../../scripts/generate/Generate-Phase6Plan.py)<br>[src/Logic/ModelContextProtocol/ToolProfiles.resx](../../src/Logic/ModelContextProtocol/ToolProfiles.resx)<br>[docs/development/phase6-review.md](../../docs/development/phase6-review.md)<br>[docs/development/refactor-plan.md](../../docs/development/refactor-plan.md)<br>[docs/reference/real-machine-ledger.md](../../docs/reference/real-machine-ledger.md) |
| <a id="phase6-path-p6-02"></a>P6-02 | [src/Logic/V4](../../src/Logic/V4)<br>[src/Logic/TiaMcp.Logic.csproj](../../src/Logic/TiaMcp.Logic.csproj)<br>[tests/Engine/TiaMcp.Engine.Tests/V4EnvelopeTests.cs](../../tests/Engine/TiaMcp.Engine.Tests/V4EnvelopeTests.cs) |
| <a id="phase6-path-p6-03"></a>P6-03 | [src/Logic/V4](../../src/Logic/V4)<br>[src/Logic/Siemens/ArgumentRules.cs](../../src/Logic/Siemens/ArgumentRules.cs)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests) |
| <a id="phase6-path-p6-04"></a>P6-04 | [src/Logic/V4](../../src/Logic/V4)<br>[src/Logic/ModelContextProtocol/Builders](../../src/Logic/ModelContextProtocol/Builders)<br>[src/FoundationHost/OfflineCompositionBuilders.cs](../../src/FoundationHost/OfflineCompositionBuilders.cs)<br>[src/FoundationHost/OfflineBlockCompositionBuilders.cs](../../src/FoundationHost/OfflineBlockCompositionBuilders.cs)<br>[src/FoundationHost/OfflineLadderBuilders.cs](../../src/FoundationHost/OfflineLadderBuilders.cs)<br>[src/FoundationHost/OfflineXmlBuilders.cs](../../src/FoundationHost/OfflineXmlBuilders.cs)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests) |
| <a id="phase6-path-p6-05"></a>P6-05 | [src/Logic/V4](../../src/Logic/V4)<br>[src/Logic/ModelContextProtocol/Builders](../../src/Logic/ModelContextProtocol/Builders)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests) |
| <a id="phase6-path-p6-06"></a>P6-06 | [src/Logic/V4](../../src/Logic/V4)<br>[src/Engine/Siemens](../../src/Engine/Siemens)<br>[src/Logic/Siemens](../../src/Logic/Siemens)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests) |
| <a id="phase6-path-p6-07"></a>P6-07 | [src/Engine/ModelContextProtocol/Tools/McpServer.CallDiscipline.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.CallDiscipline.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs](../../src/Engine/ModelContextProtocol/Tools/ToolUsageTools.cs)<br>[src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs)<br>[src/Shared/Host/McpServer.Exports.cs](../../src/Shared/Host/McpServer.Exports.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.Profile.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Profile.cs)<br>[src/Shared/ToolUsageCatalog.cs](../../src/Shared/ToolUsageCatalog.cs)<br>[src/Logic/ModelContextProtocol/ToolRecipes.cs](../../src/Logic/ModelContextProtocol/ToolRecipes.cs) |
| <a id="phase6-path-p6-08"></a>P6-08 | [src/FoundationHost/](../../src/FoundationHost/)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture](../../tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture)<br>[scripts/checks/Test-FoundationTransport.py](../../scripts/checks/Test-FoundationTransport.py) |
| <a id="phase6-path-p6-09"></a>P6-09 | [src/Engine/ModelContextProtocol/Tools/EngineeringAuditTools.cs](../../src/Engine/ModelContextProtocol/Tools/EngineeringAuditTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs](../../src/Engine/ModelContextProtocol/Tools/GitWorkflowTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs](../../src/Engine/ModelContextProtocol/Tools/ImportOrderTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcBuildTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBuildTools.cs)<br>[src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs)<br>[src/Shared/Host/OfflineAnalysisTools.cs](../../src/Shared/Host/OfflineAnalysisTools.cs)<br>[src/Shared/Host/OfflineSuiteTools.cs](../../src/Shared/Host/OfflineSuiteTools.cs)<br>[src/Shared/Host/PlcDocumentationTools.cs](../../src/Shared/Host/PlcDocumentationTools.cs)<br>[src/Shared/Host/QualityAuditTools.cs](../../src/Shared/Host/QualityAuditTools.cs)<br>[src/Shared/Host/TemplateTools.cs](../../src/Shared/Host/TemplateTools.cs)<br>[src/Shared/Host/V21EcosystemTools.cs](../../src/Shared/Host/V21EcosystemTools.cs)<br>[src/Shared/Host/XmlBuilderTools.cs](../../src/Shared/Host/XmlBuilderTools.cs)<br>[src/Engine/Siemens/Services/EngineeringAuditService.cs](../../src/Engine/Siemens/Services/EngineeringAuditService.cs) |
| <a id="phase6-path-p6-10"></a>P6-10 | [src/Engine/ModelContextProtocol/Tools/McpServer.BlockImportVerification.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.BlockImportVerification.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSoftwareTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcTablesTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/TypesTools.cs](../../src/Engine/ModelContextProtocol/Tools/TypesTools.cs)<br>[src/Shared/PlcOfflineTools.cs](../../src/Shared/PlcOfflineTools.cs)<br>[src/Shared/PlcToolContract.cs](../../src/Shared/PlcToolContract.cs)<br>[src/Engine/Siemens/Services/PlcBlocksService.cs](../../src/Engine/Siemens/Services/PlcBlocksService.cs)<br>[src/Engine/Siemens/Services/PlcSoftwareService.cs](../../src/Engine/Siemens/Services/PlcSoftwareService.cs)<br>[src/Engine/Siemens/Services/PlcTablesService.cs](../../src/Engine/Siemens/Services/PlcTablesService.cs)<br>[src/Engine/Siemens/Services/TypesService.cs](../../src/Engine/Siemens/Services/TypesService.cs) |
| <a id="phase6-path-p6-11"></a>P6-11 | [src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs](../../src/Engine/ModelContextProtocol/Tools/DocumentsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.Patch.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Patch.cs)<br>[src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/NativeExchangeTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcExternalSourcesTools.cs)<br>[src/Shared/Host/ExportTools.cs](../../src/Shared/Host/ExportTools.cs)<br>[src/Engine/Siemens/Services/DocumentsService.cs](../../src/Engine/Siemens/Services/DocumentsService.cs)<br>[src/Engine/Siemens/Services/NativeExchangeService.cs](../../src/Engine/Siemens/Services/NativeExchangeService.cs)<br>[src/Engine/Siemens/Services/PlcExternalSourcesService.cs](../../src/Engine/Siemens/Services/PlcExternalSourcesService.cs) |
| <a id="phase6-path-p6-12"></a>P6-12 | [src/Engine/ModelContextProtocol/Tools/DevicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/DevicesTools.cs)<br>[src/Shared/HardwareAddressTools.cs](../../src/Shared/HardwareAddressTools.cs)<br>[src/Shared/HardwareAmlTools.cs](../../src/Shared/HardwareAmlTools.cs)<br>[src/Shared/HardwareDevicesTools.cs](../../src/Shared/HardwareDevicesTools.cs)<br>[src/Shared/HardwareManagementTools.cs](../../src/Shared/HardwareManagementTools.cs)<br>[src/Shared/ModulesTools.cs](../../src/Shared/ModulesTools.cs)<br>[src/Engine/Siemens/Services/DevicesService.cs](../../src/Engine/Siemens/Services/DevicesService.cs) |
| <a id="phase6-path-p6-13"></a>P6-13 | [src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs](../../src/Engine/ModelContextProtocol/Tools/HardwareNetworkTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/HardwareServicesTools.cs)<br>[src/Shared/HardwareNetworkTools.cs](../../src/Shared/HardwareNetworkTools.cs)<br>[src/Shared/HardwareServicesPortTools.cs](../../src/Shared/HardwareServicesPortTools.cs)<br>[src/Engine/Siemens/Services/HardwareNetworkService.cs](../../src/Engine/Siemens/Services/HardwareNetworkService.cs)<br>[src/Engine/Siemens/Services/HardwareServicesService.cs](../../src/Engine/Siemens/Services/HardwareServicesService.cs) |
| <a id="phase6-path-p6-14"></a>P6-14 | [src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/CertificateManagementTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSecurityTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/SafetyManagementTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs](../../src/Engine/ModelContextProtocol/Tools/SafetyValidationTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs](../../src/Engine/ModelContextProtocol/Tools/SecurityDeepTools.cs)<br>[src/Engine/Siemens/Services/CertificateManagementService.cs](../../src/Engine/Siemens/Services/CertificateManagementService.cs)<br>[src/Engine/Siemens/Services/ProjectSecurityService.cs](../../src/Engine/Siemens/Services/ProjectSecurityService.cs)<br>[src/Engine/Siemens/Services/SafetyManagementService.cs](../../src/Engine/Siemens/Services/SafetyManagementService.cs)<br>[src/Engine/Siemens/Services/SafetyValidationService.cs](../../src/Engine/Siemens/Services/SafetyValidationService.cs)<br>[src/Engine/Siemens/Services/SecurityDeepService.cs](../../src/Engine/Siemens/Services/SecurityDeepService.cs) |
| <a id="phase6-path-p6-15"></a>P6-15 | [src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs](../../src/Engine/ModelContextProtocol/Tools/AlarmsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs](../../src/Engine/ModelContextProtocol/Tools/OpcUaTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitDeepTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SoftwareUnitManagementTools.cs](../../src/Engine/ModelContextProtocol/Tools/SoftwareUnitManagementTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs](../../src/Engine/ModelContextProtocol/Tools/TechnologyObjectsTools.cs)<br>[src/Engine/Siemens/Services/AlarmsService.cs](../../src/Engine/Siemens/Services/AlarmsService.cs)<br>[src/Engine/Siemens/Services/OpcUaService.cs](../../src/Engine/Siemens/Services/OpcUaService.cs)<br>[src/Engine/Siemens/Services/SoftwareUnitDeepService.cs](../../src/Engine/Siemens/Services/SoftwareUnitDeepService.cs)<br>[src/Engine/Siemens/Services/SoftwareUnitManagementService.cs](../../src/Engine/Siemens/Services/SoftwareUnitManagementService.cs)<br>[src/Engine/Siemens/Services/TechnologyObjectsService.cs](../../src/Engine/Siemens/Services/TechnologyObjectsService.cs) |
| <a id="phase6-path-p6-16"></a>P6-16 | [src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs](../../src/Engine/ModelContextProtocol/Tools/ClassicHmiFoldersTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs](../../src/Engine/ModelContextProtocol/Tools/MotionProDiagClassicHmiTools.cs)<br>[src/Engine/Siemens/Services/ClassicHmiFoldersService.cs](../../src/Engine/Siemens/Services/ClassicHmiFoldersService.cs)<br>[src/Engine/Siemens/Services/MotionProDiagClassicHmiService.cs](../../src/Engine/Siemens/Services/MotionProDiagClassicHmiService.cs) |
| <a id="phase6-path-p6-17"></a>P6-17 | [src/Engine/ModelContextProtocol/Tools/UnifiedHmiGroupsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiGroupsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedHmiTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedScreenItemsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedUiModelTools.cs)<br>[src/Shared/HmiOfflineTools.cs](../../src/Shared/HmiOfflineTools.cs)<br>[src/Engine/Siemens/Services/UnifiedHmiGroupsService.cs](../../src/Engine/Siemens/Services/UnifiedHmiGroupsService.cs)<br>[src/Engine/Siemens/Services/UnifiedHmiService.cs](../../src/Engine/Siemens/Services/UnifiedHmiService.cs)<br>[src/Engine/Siemens/Services/UnifiedScreenItemsService.cs](../../src/Engine/Siemens/Services/UnifiedScreenItemsService.cs)<br>[src/Engine/Siemens/Services/UnifiedUiModelService.cs](../../src/Engine/Siemens/Services/UnifiedUiModelService.cs) |
| <a id="phase6-path-p6-18"></a>P6-18 | [src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiExchangeTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/HmiTagDeletionTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiTagDeletionTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedExchangeTools.cs)<br>[src/Engine/Siemens/Services/HmiExchangeService.cs](../../src/Engine/Siemens/Services/HmiExchangeService.cs)<br>[src/Engine/Siemens/Services/HmiTagDeletionService.cs](../../src/Engine/Siemens/Services/HmiTagDeletionService.cs)<br>[src/Engine/Siemens/Services/UnifiedExchangeService.cs](../../src/Engine/Siemens/Services/UnifiedExchangeService.cs) |
| <a id="phase6-path-p6-19"></a>P6-19 | [src/Engine/ModelContextProtocol/Tools/GlobalScriptEditTools.cs](../../src/Engine/ModelContextProtocol/Tools/GlobalScriptEditTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/GraphicSelectionTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiDescribeTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/HmiInspectionTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs](../../src/Engine/ModelContextProtocol/Tools/MigrationReadTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ReflectionTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedEngineeringTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedEventsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs](../../src/Engine/ModelContextProtocol/Tools/UnifiedObjectServicesTools.cs)<br>[src/Engine/Siemens/Services/GlobalScriptEditService.cs](../../src/Engine/Siemens/Services/GlobalScriptEditService.cs)<br>[src/Engine/Siemens/Services/GraphicSelectionService.cs](../../src/Engine/Siemens/Services/GraphicSelectionService.cs)<br>[src/Engine/Siemens/Services/HmiDescribeService.cs](../../src/Engine/Siemens/Services/HmiDescribeService.cs)<br>[src/Engine/Siemens/Services/HmiInspectionService.cs](../../src/Engine/Siemens/Services/HmiInspectionService.cs)<br>[src/Engine/Siemens/Services/MigrationReadService.cs](../../src/Engine/Siemens/Services/MigrationReadService.cs)<br>[src/Engine/Siemens/Services/ReflectionService.cs](../../src/Engine/Siemens/Services/ReflectionService.cs)<br>[src/Engine/Siemens/Services/UnifiedEngineeringService.cs](../../src/Engine/Siemens/Services/UnifiedEngineeringService.cs)<br>[src/Engine/Siemens/Services/UnifiedEventsService.cs](../../src/Engine/Siemens/Services/UnifiedEventsService.cs)<br>[src/Engine/Siemens/Services/UnifiedObjectServicesService.cs](../../src/Engine/Siemens/Services/UnifiedObjectServicesService.cs) |
| <a id="phase6-path-p6-20"></a>P6-20 | [src/Engine/ModelContextProtocol/Tools/CfcTools.cs](../../src/Engine/ModelContextProtocol/Tools/CfcTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs](../../src/Engine/ModelContextProtocol/Tools/OptionalEngineeringTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SpecializedExchangeTools.cs](../../src/Engine/ModelContextProtocol/Tools/SpecializedExchangeTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs](../../src/Engine/ModelContextProtocol/Tools/TestSuiteTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs](../../src/Engine/ModelContextProtocol/Tools/V20OptionsTools.cs)<br>[src/Engine/Siemens/Services/CfcService.cs](../../src/Engine/Siemens/Services/CfcService.cs)<br>[src/Engine/Siemens/Services/OptionalEngineeringService.cs](../../src/Engine/Siemens/Services/OptionalEngineeringService.cs)<br>[src/Engine/Siemens/Services/SpecializedExchangeService.cs](../../src/Engine/Siemens/Services/SpecializedExchangeService.cs)<br>[src/Engine/Siemens/Services/TestSuiteService.cs](../../src/Engine/Siemens/Services/TestSuiteService.cs)<br>[src/Engine/Siemens/Services/V20OptionsService.cs](../../src/Engine/Siemens/Services/V20OptionsService.cs) |
| <a id="phase6-path-p6-21"></a>P6-21 | [src/Engine/ModelContextProtocol/Tools/DccTools.cs](../../src/Engine/ModelContextProtocol/Tools/DccTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs](../../src/Engine/ModelContextProtocol/Tools/StartdriveTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs](../../src/Engine/ModelContextProtocol/Tools/TeamcenterTools.cs)<br>[src/Engine/Siemens/Services/DccService.cs](../../src/Engine/Siemens/Services/DccService.cs)<br>[src/Engine/Siemens/Services/StartdriveService.cs](../../src/Engine/Siemens/Services/StartdriveService.cs)<br>[src/Engine/Siemens/Services/TeamcenterService.cs](../../src/Engine/Siemens/Services/TeamcenterService.cs) |
| <a id="phase6-path-p6-22"></a>P6-22 | [src/Engine/ModelContextProtocol/Tools/LibraryTools.cs](../../src/Engine/ModelContextProtocol/Tools/LibraryTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SivarcTools.cs](../../src/Engine/ModelContextProtocol/Tools/SivarcTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs](../../src/Engine/ModelContextProtocol/Tools/VersionControlTools.cs)<br>[src/Engine/Siemens/Services/LibraryService.cs](../../src/Engine/Siemens/Services/LibraryService.cs)<br>[src/Engine/Siemens/Services/SivarcService.cs](../../src/Engine/Siemens/Services/SivarcService.cs)<br>[src/Engine/Siemens/Services/VersionControlService.cs](../../src/Engine/Siemens/Services/VersionControlService.cs) |
| <a id="phase6-path-p6-23"></a>P6-23 | [src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs](../../src/Engine/ModelContextProtocol/Tools/OnlineDownloadTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcSimAdvancedTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeChannelTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeSettingsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs](../../src/Engine/ModelContextProtocol/Tools/RuntimeTools.cs)<br>[src/Engine/Siemens/Services/OnlineDownloadService.cs](../../src/Engine/Siemens/Services/OnlineDownloadService.cs)<br>[src/Engine/Siemens/Services/RuntimeSettingsService.cs](../../src/Engine/Siemens/Services/RuntimeSettingsService.cs)<br>[src/OnlineChannels](../../src/OnlineChannels)<br>[src/Engine/Runtime](../../src/Engine/Runtime) |
| <a id="phase6-path-p6-24"></a>P6-24 | [src/Engine/ModelContextProtocol/Tools/DiagnosticsTools.cs](../../src/Engine/ModelContextProtocol/Tools/DiagnosticsTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/ProjectSessionTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Shared/Host/EngineeringDiagnosticsTools.cs](../../src/Shared/Host/EngineeringDiagnosticsTools.cs)<br>[src/Shared/Host/McpServer.Doctor.cs](../../src/Shared/Host/McpServer.Doctor.cs)<br>[src/Shared/Host/McpServer.Maintenance.cs](../../src/Shared/Host/McpServer.Maintenance.cs)<br>[src/Shared/Host/McpServer.Worker.cs](../../src/Shared/Host/McpServer.Worker.cs)<br>[src/Shared/HostMetaTools.cs](../../src/Shared/HostMetaTools.cs)<br>[src/Engine/Siemens/Portal](../../src/Engine/Siemens/Portal)<br>[src/Engine/EngineServices.cs](../../src/Engine/EngineServices.cs)<br>[src/Engine/EngineRegistration.cs](../../src/Engine/EngineRegistration.cs)<br>[src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs](../../src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs)<br>[src/Engine/TiaMcp.Engine.V20.csproj](../../src/Engine/TiaMcp.Engine.V20.csproj)<br>[src/Engine/TiaMcp.Engine.V21.csproj](../../src/Engine/TiaMcp.Engine.V21.csproj)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness) |
| <a id="phase6-path-p6-25"></a>P6-25 | [src/Engine/Cli](../../src/Engine/Cli)<br>[src/Studio/Client](../../src/Studio/Client)<br>[src/Studio/Core](../../src/Studio/Core)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-26"></a>P6-26 | [src/Engine/ModelContextProtocol](../../src/Engine/ModelContextProtocol)<br>[src/FoundationHost/](../../src/FoundationHost/)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[scripts/checks/Check-McpText.py](../../scripts/checks/Check-McpText.py)<br>[scripts/checks/mcp-text-baseline.json](../../scripts/checks/mcp-text-baseline.json)<br>[scripts/checks/Test-LocalStability.py](../../scripts/checks/Test-LocalStability.py)<br>[scripts/checks/Snapshot-ToolContracts.py](../../scripts/checks/Snapshot-ToolContracts.py)<br>[scripts/checks/Snapshot-ToolResponses.py](../../scripts/checks/Snapshot-ToolResponses.py) |
| <a id="phase6-path-p6-27"></a>P6-27 | [src/Engine/Siemens/Services/DevicesService.cs](../../src/Engine/Siemens/Services/DevicesService.cs)<br>[src/Adapters/Native/Hardware](../../src/Adapters/Native/Hardware)<br>[src/FoundationHost/DeviceAddContract.cs](../../src/FoundationHost/DeviceAddContract.cs)<br>[tests/FoundationHost/TiaMcp.FoundationHost.DeviceAdd.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.DeviceAdd.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.HardwareCatalog.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.HardwareCatalog.Tests) |
| <a id="phase6-path-p6-28"></a>P6-28 | [src/Engine/Siemens/Services/PlcBlocksService.cs](../../src/Engine/Siemens/Services/PlcBlocksService.cs)<br>[src/Engine/Siemens/Services/TypesService.cs](../../src/Engine/Siemens/Services/TypesService.cs)<br>[src/Engine/Siemens/Services/PlcTablesService.cs](../../src/Engine/Siemens/Services/PlcTablesService.cs)<br>[src/Adapters/Native/Plc](../../src/Adapters/Native/Plc)<br>[src/Adapters/Policy/PlcBlockXmlPolicy.cs](../../src/Adapters/Policy/PlcBlockXmlPolicy.cs)<br>[src/FoundationHost/BatchImportContract.cs](../../src/FoundationHost/BatchImportContract.cs)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests) |
| <a id="phase6-path-p6-29"></a>P6-29 | [src/Engine/Siemens/Services/NativeExchangeService.cs](../../src/Engine/Siemens/Services/NativeExchangeService.cs)<br>[src/Engine/Siemens/Services/DocumentsService.cs](../../src/Engine/Siemens/Services/DocumentsService.cs)<br>[src/Engine/Siemens/EngineeringExport.cs](../../src/Engine/Siemens/EngineeringExport.cs)<br>[src/Adapters/Native/Plc](../../src/Adapters/Native/Plc)<br>[src/FoundationHost/BatchExportContract.cs](../../src/FoundationHost/BatchExportContract.cs)<br>[src/FoundationHost/BatchDocumentExportContract.cs](../../src/FoundationHost/BatchDocumentExportContract.cs)<br>[src/FoundationHost/SpecialExportContract.cs](../../src/FoundationHost/SpecialExportContract.cs) |
| <a id="phase6-path-p6-30"></a>P6-30 | [src/Engine/Siemens/Portal](../../src/Engine/Siemens/Portal)<br>[src/Adapters/Native/Session](../../src/Adapters/Native/Session)<br>[src/Adapters/Policy/PlcLifecyclePolicy.cs](../../src/Adapters/Policy/PlcLifecyclePolicy.cs)<br>[src/FoundationHost/WorkerClient.cs](../../src/FoundationHost/WorkerClient.cs)<br>[src/Studio/Core/Adapters](../../src/Studio/Core/Adapters) |
| <a id="phase6-path-p6-31"></a>P6-31 | [src/Engine/Siemens/Portal](../../src/Engine/Siemens/Portal)<br>[src/Adapters/Native/Session](../../src/Adapters/Native/Session)<br>[src/Adapters/Policy/PlcLifecyclePolicy.cs](../../src/Adapters/Policy/PlcLifecyclePolicy.cs)<br>[src/FoundationHost/DisconnectContract.cs](../../src/FoundationHost/DisconnectContract.cs)<br>[src/Studio/Core/Adapters](../../src/Studio/Core/Adapters) |
| <a id="phase6-path-p6-32"></a>P6-32 | [src/Engine/Siemens/PlcBlockLookup.cs](../../src/Engine/Siemens/PlcBlockLookup.cs)<br>[src/Engine/Siemens/Services/PlcExternalSourcesService.cs](../../src/Engine/Siemens/Services/PlcExternalSourcesService.cs)<br>[src/Adapters/Native/Plc](../../src/Adapters/Native/Plc)<br>[src/FoundationHost/ExternalSourcePlanContract.cs](../../src/FoundationHost/ExternalSourcePlanContract.cs)<br>[src/FoundationHost/ExternalSourceWorkflowContract.cs](../../src/FoundationHost/ExternalSourceWorkflowContract.cs)<br>[src/FoundationHost/ExternalSourceDeleteContract.cs](../../src/FoundationHost/ExternalSourceDeleteContract.cs) |
| <a id="phase6-path-p6-33"></a>P6-33 | [src/Engine/Siemens/Services/PlcSoftwareService.cs](../../src/Engine/Siemens/Services/PlcSoftwareService.cs)<br>[src/Engine/Siemens/Services/PlcBlocksService.cs](../../src/Engine/Siemens/Services/PlcBlocksService.cs)<br>[src/Engine/Siemens/Services/HmiDescribeService.cs](../../src/Engine/Siemens/Services/HmiDescribeService.cs)<br>[src/Engine/Siemens/Services/HardwareServicesService.cs](../../src/Engine/Siemens/Services/HardwareServicesService.cs)<br>[src/Adapters/Native/Plc/PlcBlockPrimitives.cs](../../src/Adapters/Native/Plc/PlcBlockPrimitives.cs)<br>[src/FoundationHost/V17CompileEnvelope.cs](../../src/FoundationHost/V17CompileEnvelope.cs) |
| <a id="phase6-path-p6-34"></a>P6-34 | [src/Engine/ModelContextProtocol/Tools/OnlineToolPolicy.cs](../../src/Engine/ModelContextProtocol/Tools/OnlineToolPolicy.cs)<br>[src/Engine/Siemens/Services/OnlineDownloadService.cs](../../src/Engine/Siemens/Services/OnlineDownloadService.cs)<br>[src/Engine/Siemens/Services/VersionControlService.cs](../../src/Engine/Siemens/Services/VersionControlService.cs)<br>[src/Adapters/Native/Vci](../../src/Adapters/Native/Vci) |
| <a id="phase6-path-p6-35"></a>P6-35 | [src/Logic/Siemens/ToolVersionPolicy.cs](../../src/Logic/Siemens/ToolVersionPolicy.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs](../../src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs)<br>[src/FoundationHost/FoundationTools.cs](../../src/FoundationHost/FoundationTools.cs)<br>[reference/version-feature-matrix.json](../../reference/version-feature-matrix.json)<br>[docs/reference/real-machine-ledger.md](../../docs/reference/real-machine-ledger.md)<br>[scripts/checks/Snapshot-ToolContracts.py](../../scripts/checks/Snapshot-ToolContracts.py)<br>[scripts/checks/Snapshot-ToolResponses.py](../../scripts/checks/Snapshot-ToolResponses.py) |
| <a id="phase6-path-p6-36"></a>P6-36 | [src/Engine/TiaMcp.Engine.V20.csproj](../../src/Engine/TiaMcp.Engine.V20.csproj)<br>[src/Engine/TiaMcp.Engine.V21.csproj](../../src/Engine/TiaMcp.Engine.V21.csproj)<br>[src/FoundationHost/TiaMcp.FoundationHost.csproj](../../src/FoundationHost/TiaMcp.FoundationHost.csproj)<br>[src/Studio/Launcher/Launcher.cs](../../src/Studio/Launcher/Launcher.cs)<br>[build-tools](../../build-tools)<br>[scripts/checks](../../scripts/checks)<br>[build-tools/native-call-weaver](../../build-tools/native-call-weaver)<br>[scripts/operations/delivery-files.json](../../scripts/operations/delivery-files.json) |
| <a id="phase6-path-p6-37"></a>P6-37 | [src/Shared/BundleLayout.cs](../../src/Shared/BundleLayout.cs)<br>[src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/Cli](../../src/Engine/Cli)<br>[src/Engine/Siemens/EngineRouter.cs](../../src/Engine/Siemens/EngineRouter.cs)<br>[src/FoundationHost/HostOptions.cs](../../src/FoundationHost/HostOptions.cs)<br>[src/FoundationHost/Program.cs](../../src/FoundationHost/Program.cs)<br>[src/Logic/ModelContextProtocol/Builders/EcosystemFiles.cs](../../src/Logic/ModelContextProtocol/Builders/EcosystemFiles.cs)<br>[tests/Engine/TiaMcp.Engine.Tests/BundleLayoutTests.cs](../../tests/Engine/TiaMcp.Engine.Tests/BundleLayoutTests.cs) |
| <a id="phase6-path-p6-38"></a>P6-38 | [src/Studio/Gui/Configuration](../../src/Studio/Gui/Configuration)<br>[src/Studio/Gui/ConfigurationPage.cs](../../src/Studio/Gui/ConfigurationPage.cs)<br>[src/Studio/Client/BridgeClient.cs](../../src/Studio/Client/BridgeClient.cs)<br>[src/Studio/Core/Abstractions/SessionFactoryLoader.cs](../../src/Studio/Core/Abstractions/SessionFactoryLoader.cs)<br>[src/Studio/Core/Adapters/SessionFactoryLoader.cs](../../src/Studio/Core/Adapters/SessionFactoryLoader.cs)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-39"></a>P6-39 | [src/Shared/DataLocations.cs](../../src/Shared/DataLocations.cs)<br>[src/Shared/InvocationJournal.cs](../../src/Shared/InvocationJournal.cs)<br>[src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/Cli/ReportBuilders.cs](../../src/Engine/Cli/ReportBuilders.cs)<br>[src/Engine/Cli/HmiTemplateBuilder.cs](../../src/Engine/Cli/HmiTemplateBuilder.cs)<br>[src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs)<br>[src/Logic/ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs](../../src/Logic/ModelContextProtocol/Builders/PlcBuilderOfflineValidationSuite.cs)<br>[src/Studio/Gui/App.xaml.cs](../../src/Studio/Gui/App.xaml.cs)<br>[src/Engine/Cli/InstallPlcToolsCommand.cs](../../src/Engine/Cli/InstallPlcToolsCommand.cs) |
| <a id="phase6-path-p6-40"></a>P6-40 | [reference/tool-examples](../../reference/tool-examples)<br>[src/Engine/ModelContextProtocol/McpPrompts.cs](../../src/Engine/ModelContextProtocol/McpPrompts.cs)<br>[plugin/skill](../../plugin/skill)<br>[docs](../../docs)<br>[scripts/generate/Generate-ToolUsage.py](../../scripts/generate/Generate-ToolUsage.py)<br>[scripts/generate/Generate-ToolCapabilityMatrix.cs](../../scripts/generate/Generate-ToolCapabilityMatrix.cs)<br>[src/Shared/ToolUsageData.json](../../src/Shared/ToolUsageData.json)<br>[docs/reference/tool-matrix.md](../../docs/reference/tool-matrix.md) |
| <a id="phase6-path-p6-41"></a>P6-41 | [manifest/contracts](../../manifest/contracts)<br>[scripts/checks/Snapshot-ToolContracts.py](../../scripts/checks/Snapshot-ToolContracts.py)<br>[scripts/checks/Snapshot-ToolResponses.py](../../scripts/checks/Snapshot-ToolResponses.py)<br>[scripts/generate/Generate-Phase6Plan.py](../../scripts/generate/Generate-Phase6Plan.py)<br>[.github/workflows/offline-checks.yml](../../.github/workflows/offline-checks.yml)<br>[scripts/checks/Check-Repository.py](../../scripts/checks/Check-Repository.py)<br>[scripts/checks](../../scripts/checks)<br>[build-tools/Package-Release.py](../../build-tools/Package-Release.py) |
| <a id="phase6-path-p6-42"></a>P6-42 | [build-tools/release](../../build-tools/release)<br>[tests/test-suites.json](../../tests/test-suites.json)<br>[scripts/checks](../../scripts/checks)<br>[docs/reference/real-machine-ledger.md](../../docs/reference/real-machine-ledger.md) |
| <a id="phase6-path-p6-43"></a>P6-43 | [docs/releases](../../docs/releases)<br>[docs/development/phase6-review.md](../../docs/development/phase6-review.md)<br>[reference/tool-examples](../../reference/tool-examples)<br>[manifest/contracts](../../manifest/contracts) |
| <a id="phase6-path-p6-44"></a>P6-44 | [src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs)<br>[src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs](../../src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs)<br>[src/FoundationHost/Program.cs](../../src/FoundationHost/Program.cs)<br>[src/FoundationHost/FoundationTools.cs](../../src/FoundationHost/FoundationTools.cs)<br>[src/Logic/V4/Error.cs](../../src/Logic/V4/Error.cs)<br>[src/Logic/V4/V4Json.cs](../../src/Logic/V4/V4Json.cs)<br>[src/Logic/V4/V4Validation.cs](../../src/Logic/V4/V4Validation.cs)<br>[src/Shared/](../../src/Shared/)<br>[src/Studio/Core](../../src/Studio/Core)<br>[src/Studio/Gui/MainWindow.xaml](../../src/Studio/Gui/MainWindow.xaml)<br>[src/Studio/Gui/MainWindow.xaml.cs](../../src/Studio/Gui/MainWindow.xaml.cs)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[src/Studio/Gui/Services](../../src/Studio/Gui/Services)<br>[src/Studio/Gui/Settings/UiSettings.cs](../../src/Studio/Gui/Settings/UiSettings.cs)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-45"></a>P6-45 | [src/Shared/InvocationJournal.cs](../../src/Shared/InvocationJournal.cs)<br>[src/Engine/ModelContextProtocol/InvocationJournal.cs](../../src/Engine/ModelContextProtocol/InvocationJournal.cs)<br>[src/Studio/Core](../../src/Studio/Core)<br>[src/Studio/Gui/MainWindow.xaml](../../src/Studio/Gui/MainWindow.xaml)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[src/Studio/Gui/Controls](../../src/Studio/Gui/Controls)<br>[src/Studio/Gui/Configuration/ClientProfiles.cs](../../src/Studio/Gui/Configuration/ClientProfiles.cs)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-46"></a>P6-46 | [src/Shared/InvocationJournal.cs](../../src/Shared/InvocationJournal.cs)<br>[src/Shared/NativeCallDiagnostics.Journal.cs](../../src/Shared/NativeCallDiagnostics.Journal.cs)<br>[src/Shared/DataLocations.cs](../../src/Shared/DataLocations.cs)<br>[src/Engine/ModelContextProtocol/InvocationJournal.cs](../../src/Engine/ModelContextProtocol/InvocationJournal.cs)<br>[src/Engine/Cli/CliCommands.cs](../../src/Engine/Cli/CliCommands.cs)<br>[src/Studio/Core](../../src/Studio/Core)<br>[src/Studio/Gui/Settings/UiSettings.cs](../../src/Studio/Gui/Settings/UiSettings.cs)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[tests/Engine/TiaMcp.Engine.Diagnostics.Tests](../../tests/Engine/TiaMcp.Engine.Diagnostics.Tests)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-47"></a>P6-47 | [src/Shared/Host/EnvironmentDoctor.cs](../../src/Shared/Host/EnvironmentDoctor.cs)<br>[src/Shared/Host/McpServer.Doctor.cs](../../src/Shared/Host/McpServer.Doctor.cs)<br>[src/FoundationHost/FoundationPassiveDiagnostics.cs](../../src/FoundationHost/FoundationPassiveDiagnostics.cs)<br>[src/Studio/Core/Environment/OpennessDoctor.cs](../../src/Studio/Core/Environment/OpennessDoctor.cs)<br>[src/Shared/OpennessEnvironment.cs](../../src/Shared/OpennessEnvironment.cs)<br>[src/Shared/DataLocations.cs](../../src/Shared/DataLocations.cs)<br>[src/Studio/Gui/MainWindow.xaml](../../src/Studio/Gui/MainWindow.xaml)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[src/Studio/Gui/Configuration](../../src/Studio/Gui/Configuration)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-48"></a>P6-48 | [src/Logic/ModelContextProtocol/Builders/PlcVisualComparison.cs](../../src/Logic/ModelContextProtocol/Builders/PlcVisualComparison.cs)<br>[src/Logic/ModelContextProtocol/Builders/LadTextRenderer.cs](../../src/Logic/ModelContextProtocol/Builders/LadTextRenderer.cs)<br>[src/Shared/Host/EcosystemTools.cs](../../src/Shared/Host/EcosystemTools.cs)<br>[src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs](../../src/Engine/ModelContextProtocol/Tools/PlcBlocksTools.cs)<br>[src/FoundationHost/FoundationTools.cs](../../src/FoundationHost/FoundationTools.cs)<br>[src/Studio/Gui/MainWindow.xaml](../../src/Studio/Gui/MainWindow.xaml)<br>[src/Studio/Gui/ViewModels](../../src/Studio/Gui/ViewModels)<br>[src/Studio/Gui/Localization](../../src/Studio/Gui/Localization)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Studio/](../../tests/Studio/) |
| <a id="phase6-path-p6-49"></a>P6-49 | [src/Engine/ModelContextProtocol/Tools/SessionToolContract.cs](../../src/Engine/ModelContextProtocol/Tools/SessionToolContract.cs)<br>[src/WorkerChannel/ChannelClient.cs](../../src/WorkerChannel/ChannelClient.cs)<br>[src/Studio/Launcher/Launcher.cs](../../src/Studio/Launcher/Launcher.cs)<br>[src/Shared/Host/EnvironmentDoctor.cs](../../src/Shared/Host/EnvironmentDoctor.cs)<br>[src/Studio/Core/Environment/OpennessDoctor.cs](../../src/Studio/Core/Environment/OpennessDoctor.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs](../../src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs)<br>[docs/development/roadmap.md](../../docs/development/roadmap.md) |
| <a id="phase6-path-p6-50"></a>P6-50 | [hooks](../../hooks)<br>[scripts/operations](../../scripts/operations)<br>[scripts/ecosystem](../../scripts/ecosystem)<br>[scripts/diagnostics](../../scripts/diagnostics)<br>[src/Studio/Core/Environment](../../src/Studio/Core/Environment)<br>[docs/getting-started/cli.md](../../docs/getting-started/cli.md)<br>[scripts/README.md](../../scripts/README.md) |
| <a id="phase6-path-p6-51"></a>P6-51 | [src/Studio/Gui/Configuration/UpdateCheck.cs](../../src/Studio/Gui/Configuration/UpdateCheck.cs)<br>[src/Logic/ModelContextProtocol/UpdateLogic.cs](../../src/Logic/ModelContextProtocol/UpdateLogic.cs)<br>[src/Shared/BundleLayout.cs](../../src/Shared/BundleLayout.cs)<br>[src/Shared/Host/McpServer.Maintenance.cs](../../src/Shared/Host/McpServer.Maintenance.cs)<br>[scripts/operations/delivery-files.json](../../scripts/operations/delivery-files.json) |
| <a id="phase6-path-p6-52"></a>P6-52 | [scripts/checks](../../scripts/checks)<br>[scripts/generate](../../scripts/generate)<br>[src/Adapters/build](../../src/Adapters/build)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[tests/test-suites.json](../../tests/test-suites.json) |
| <a id="phase6-path-p6-53"></a>P6-53 | [build-tools/release](../../build-tools/release)<br>[tests/Release](../../tests/Release)<br>[build-tools](../../build-tools)<br>[scripts/checks](../../scripts/checks)<br>[scripts/generate](../../scripts/generate)<br>[build-tools/Package-Release.py](../../build-tools/Package-Release.py)<br>[build-tools/Package-MultiVersion.py](../../build-tools/Package-MultiVersion.py)<br>[tests/test-suites.json](../../tests/test-suites.json)<br>[.github/workflows](../../.github/workflows)<br>[AGENTS.md](../../AGENTS.md)<br>[CLAUDE.md](../../CLAUDE.md)<br>[docs/development/release-workflow.md](../../docs/development/release-workflow.md)<br>[docs/development/validation.md](../../docs/development/validation.md)<br>[docs/development/repository-layout.md](../../docs/development/repository-layout.md)<br>[docs/development/handoff.md](../../docs/development/handoff.md)<br>[scripts/README.md](../../scripts/README.md)<br>[.gitattributes](../../.gitattributes) |
| <a id="phase6-path-p6-54"></a>P6-54 | [src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/Isolation](../../src/Engine/Isolation)<br>[src/Logic/CliOptions.cs](../../src/Logic/CliOptions.cs)<br>[src/Shared/Host/McpServer.Worker.cs](../../src/Shared/Host/McpServer.Worker.cs)<br>[src/Engine/ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Shared/Host/EnvironmentDoctor.cs](../../src/Shared/Host/EnvironmentDoctor.cs)<br>[src/Studio/Gui/Configuration](../../src/Studio/Gui/Configuration)<br>[scripts/checks/Test-RelocatedBundle.py](../../scripts/checks/Test-RelocatedBundle.py)<br>[docs/reference/real-machine-ledger.md](../../docs/reference/real-machine-ledger.md) |
| <a id="phase6-path-p6-55"></a>P6-55 | [src/FoundationHost](../../src/FoundationHost)<br>[src/PlcWorker](../../src/PlcWorker)<br>[src/Adapters/Native/Plc](../../src/Adapters/Native/Plc)<br>[src/Adapters/Native/Session/PlcFoundationEngine.cs](../../src/Adapters/Native/Session/PlcFoundationEngine.cs)<br>[src/Adapters.Contracts/AdapterPreconditionException.cs](../../src/Adapters.Contracts/AdapterPreconditionException.cs)<br>[src/Shared/AuditInvocation.cs](../../src/Shared/AuditInvocation.cs)<br>[src/Shared/ApprovalPipe.cs](../../src/Shared/ApprovalPipe.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs)<br>[reference/tool-examples/calls.json](../../reference/tool-examples/calls.json) |
| <a id="phase6-path-p6-56"></a>P6-56 | [scripts/checks/Snapshot-ToolResponses.py](../../scripts/checks/Snapshot-ToolResponses.py)<br>[scripts/checks/Test-ResourceDiscovery.py](../../scripts/checks/Test-ResourceDiscovery.py)<br>[build-tools/release](../../build-tools/release)<br>[src/Shared/Host/EnvironmentDoctor.cs](../../src/Shared/Host/EnvironmentDoctor.cs)<br>[src/Shared/Host/McpServer.Doctor.cs](../../src/Shared/Host/McpServer.Doctor.cs)<br>[src/Engine/ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Engine/Isolation/OpennessReadinessGuard.cs](../../src/Engine/Isolation/OpennessReadinessGuard.cs)<br>[src/FoundationHost/FoundationPassiveDiagnostics.cs](../../src/FoundationHost/FoundationPassiveDiagnostics.cs)<br>[src/FoundationHost/FoundationPassiveDiagnosticTools.cs](../../src/FoundationHost/FoundationPassiveDiagnosticTools.cs)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[docs/development/release-workflow.md](../../docs/development/release-workflow.md)<br>[docs/development/validation.md](../../docs/development/validation.md) |
| <a id="phase6-path-p6-57"></a>P6-57 | [src/Studio/Gui](../../src/Studio/Gui)<br>[src/Studio/Core](../../src/Studio/Core)<br>[src/Shared/CallJournalPayload.cs](../../src/Shared/CallJournalPayload.cs)<br>[src/Shared/ApprovalSettings.cs](../../src/Shared/ApprovalSettings.cs)<br>[tests/Studio](../../tests/Studio)<br>[docs/getting-started](../../docs/getting-started)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-58"></a>P6-58 | [src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs)<br>[src/Shared/Host/McpServer.Worker.cs](../../src/Shared/Host/McpServer.Worker.cs)<br>[src/Shared/Host/McpServer.Exports.cs](../../src/Shared/Host/McpServer.Exports.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.CallDiscipline.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.CallDiscipline.cs)<br>[src/Shared/Host/McpServer.Doctor.cs](../../src/Shared/Host/McpServer.Doctor.cs)<br>[src/Engine/ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Engine/HttpMcpServer.cs](../../src/Engine/HttpMcpServer.cs)<br>[src/Logic/ModelContextProtocol/ToolTaxonomy.cs](../../src/Logic/ModelContextProtocol/ToolTaxonomy.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs)<br>[src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs)<br>[src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs](../../src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs)<br>[src/Engine/Isolation](../../src/Engine/Isolation)<br>[src/Engine/EngineRegistration.cs](../../src/Engine/EngineRegistration.cs)<br>[src/Engine/Program.cs](../../src/Engine/Program.cs)<br>[src/Engine/ModelContextProtocol/InvocationJournal.cs](../../src/Engine/ModelContextProtocol/InvocationJournal.cs)<br>[src/Shared/InvocationJournal.cs](../../src/Shared/InvocationJournal.cs)<br>[src/Shared/AuditInvocation.cs](../../src/Shared/AuditInvocation.cs)<br>[src/Shared/AuditLog.cs](../../src/Shared/AuditLog.cs)<br>[src/Shared/JournalFileLock.cs](../../src/Shared/JournalFileLock.cs)<br>[src/FoundationHost](../../src/FoundationHost)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-59"></a>P6-59 | [reference/tool-examples](../../reference/tool-examples)<br>[src/Shared/ToolUsageData.json](../../src/Shared/ToolUsageData.json)<br>[manifest/tool-usage-coverage.json](../../manifest/tool-usage-coverage.json)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[scripts/generate/Generate-ToolUsage.py](../../scripts/generate/Generate-ToolUsage.py)<br>[scripts/checks](../../scripts/checks)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-60"></a>P6-60 | [src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs)<br>[src/Shared/Host/McpServer.ToolBridge.cs](../../src/Shared/Host/McpServer.ToolBridge.cs)<br>[src/Shared/Host/McpServer.Batch.cs](../../src/Shared/Host/McpServer.Batch.cs)<br>[src/Shared/Host/McpServer.Exports.cs](../../src/Shared/Host/McpServer.Exports.cs)<br>[src/Engine/ModelContextProtocol/Tools/SessionTools.cs](../../src/Engine/ModelContextProtocol/Tools/SessionTools.cs)<br>[src/Shared/ApprovalPipe.cs](../../src/Shared/ApprovalPipe.cs)<br>[src/Shared/AuditInvocation.cs](../../src/Shared/AuditInvocation.cs)<br>[src/FoundationHost](../../src/FoundationHost)<br>[src/Adapters/Native/Session](../../src/Adapters/Native/Session)<br>[src/Adapters/Native/Plc/PlcFoundationPolicy.cs](../../src/Adapters/Native/Plc/PlcFoundationPolicy.cs)<br>[src/Adapters/Native/Plc/PlcBatchImportPolicy.cs](../../src/Adapters/Native/Plc/PlcBatchImportPolicy.cs)<br>[src/Adapters/Native/Plc/PlcExchangePolicy.cs](../../src/Adapters/Native/Plc/PlcExchangePolicy.cs)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[reference/tool-examples](../../reference/tool-examples)<br>[src/Shared/ToolUsageData.json](../../src/Shared/ToolUsageData.json)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-61"></a>P6-61 | [src/Studio/Gui](../../src/Studio/Gui)<br>[src/Studio/README.md](../../src/Studio/README.md)<br>[build-tools/release/BundleManifestRequirements.cs](../../build-tools/release/BundleManifestRequirements.cs)<br>[scripts/checks/Check-Repository.py](../../scripts/checks/Check-Repository.py)<br>[build-tools/Package-Release.py](../../build-tools/Package-Release.py)<br>[scripts/checks/Check-BundleLayout.py](../../scripts/checks/Check-BundleLayout.py)<br>[scripts/operations/delivery-files.json](../../scripts/operations/delivery-files.json)<br>[docs/licenses](../../docs/licenses)<br>[tests/Studio/TiaOpenness.Gui.Tests](../../tests/Studio/TiaOpenness.Gui.Tests)<br>[tests/Studio/TiaOpenness.Configuration.Tests](../../tests/Studio/TiaOpenness.Configuration.Tests)<br>[docs/getting-started](../../docs/getting-started)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-62"></a>P6-62 | [src/Logic/ModelContextProtocol/Builders/PlcProgramRenderer.cs](../../src/Logic/ModelContextProtocol/Builders/PlcProgramRenderer.cs)<br>[tests/Engine/TiaMcp.Engine.Tests/PlcProgramRendererTests.cs](../../tests/Engine/TiaMcp.Engine.Tests/PlcProgramRendererTests.cs)<br>[tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender](../../tests/Engine/TiaMcp.Engine.Tests/Fixtures/PlcRender)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-63"></a>P6-63 | [reference/tool-examples](../../reference/tool-examples)<br>[src/Shared/ToolUsageData.json](../../src/Shared/ToolUsageData.json)<br>[src/Logic/ModelContextProtocol/ToolProfiles.resx](../../src/Logic/ModelContextProtocol/ToolProfiles.resx)<br>[manifest/tool-usage-coverage.json](../../manifest/tool-usage-coverage.json)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[scripts/generate/Generate-ToolUsage.py](../../scripts/generate/Generate-ToolUsage.py)<br>[scripts/checks](../../scripts/checks)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-64"></a>P6-64 | [src/Adapters/Native/Plc](../../src/Adapters/Native/Plc)<br>[src/Adapters/Native/Session](../../src/Adapters/Native/Session)<br>[src/FoundationHost](../../src/FoundationHost)<br>[src/Shared](../../src/Shared)<br>[src/Logic/ModelContextProtocol](../../src/Logic/ModelContextProtocol)<br>[src/Engine/ModelContextProtocol/McpHints.cs](../../src/Engine/ModelContextProtocol/McpHints.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs)<br>[src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs](../../src/Engine/ModelContextProtocol/Tools/McpServer.SerializedCalls.cs)<br>[reference/tool-examples](../../reference/tool-examples)<br>[src/Shared/ToolUsageData.json](../../src/Shared/ToolUsageData.json)<br>[src/Logic/ModelContextProtocol/ToolProfiles.resx](../../src/Logic/ModelContextProtocol/ToolProfiles.resx)<br>[manifest/tool-usage-coverage.json](../../manifest/tool-usage-coverage.json)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[scripts/checks](../../scripts/checks)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-65"></a>P6-65 | [src/Shared](../../src/Shared)<br>[src/Logic](../../src/Logic)<br>[src/Engine/ModelContextProtocol](../../src/Engine/ModelContextProtocol)<br>[src/Engine/Siemens](../../src/Engine/Siemens)<br>[src/Engine/Isolation](../../src/Engine/Isolation)<br>[src/FoundationHost](../../src/FoundationHost)<br>[src/PlcWorker](../../src/PlcWorker)<br>[src/Adapters/Native](../../src/Adapters/Native)<br>[manifest/contracts/v4](../../manifest/contracts/v4)<br>[scripts/checks](../../scripts/checks)<br>[build-tools/release](../../build-tools/release)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[docs/reference](../../docs/reference) |
| <a id="phase6-path-p6-66"></a>P6-66 | [build-tools/release](../../build-tools/release)<br>[tests/Release](../../tests/Release)<br>[build-tools/Package-Release.py](../../build-tools/Package-Release.py)<br>[scripts/checks](../../scripts/checks)<br>[docs/development/release-workflow.md](../../docs/development/release-workflow.md)<br>[docs/development/validation.md](../../docs/development/validation.md) |
| <a id="phase6-path-p6-67"></a>P6-67 | [src/Shared](../../src/Shared)<br>[src/Logic](../../src/Logic)<br>[src/FoundationHost](../../src/FoundationHost)<br>[src/Adapters](../../src/Adapters)<br>[src/Adapters.Contracts](../../src/Adapters.Contracts)<br>[src/WorkerChannel](../../src/WorkerChannel)<br>[src/PlcWorker](../../src/PlcWorker)<br>[tests/Engine](../../tests/Engine)<br>[src/Engine/ModelContextProtocol](../../src/Engine/ModelContextProtocol)<br>[src/Engine/Siemens](../../src/Engine/Siemens)<br>[reference/tool-examples](../../reference/tool-examples)<br>[manifest](../../manifest)<br>[scripts/checks](../../scripts/checks)<br>[scripts/generate](../../scripts/generate)<br>[tests/Engine/TiaMcp.Engine.Tests](../../tests/Engine/TiaMcp.Engine.Tests)<br>[tests/FoundationHost/TiaMcp.FoundationHost.Tests](../../tests/FoundationHost/TiaMcp.FoundationHost.Tests)<br>[tests/Engine/TiaMcp.Engine.Harness](../../tests/Engine/TiaMcp.Engine.Harness)<br>[docs/reference](../../docs/reference)<br>[docs/getting-started](../../docs/getting-started) |
| <a id="phase6-path-p6-68"></a>P6-68 | [src](../../src)<br>[tests](../../tests)<br>[reference/tool-examples](../../reference/tool-examples)<br>[scripts/checks](../../scripts/checks)<br>[scripts/generate](../../scripts/generate)<br>[manifest](../../manifest)<br>[docs/reference](../../docs/reference)<br>[docs/getting-started](../../docs/getting-started)<br>[docs/development/phase6-review.md](../../docs/development/phase6-review.md)<br>[docs/releases/v4.0.0-tool-migration.md](../../docs/releases/v4.0.0-tool-migration.md) |
| <a id="phase6-path-p6-69"></a>P6-69 | [src/FoundationHost](../../src/FoundationHost)<br>[src/Logic](../../src/Logic)<br>[src/Shared](../../src/Shared)<br>[src/Engine](../../src/Engine)<br>[src/PlcWorker](../../src/PlcWorker)<br>[tests](../../tests)<br>[reference/tool-examples](../../reference/tool-examples)<br>[scripts/checks/phase6_groups.py](../../scripts/checks/phase6_groups.py)<br>[manifest](../../manifest)<br>[docs/reference](../../docs/reference)<br>[docs/development/phase6-review.md](../../docs/development/phase6-review.md) |
| <a id="phase6-path-p6-70"></a>P6-70 | [src/FoundationHost](../../src/FoundationHost)<br>[src/Logic](../../src/Logic)<br>[src/Shared](../../src/Shared)<br>[src/Engine](../../src/Engine)<br>[src/PlcWorker](../../src/PlcWorker)<br>[src/Adapters/Native/Hardware](../../src/Adapters/Native/Hardware)<br>[src/Adapters/Native/Session](../../src/Adapters/Native/Session)<br>[tests](../../tests)<br>[reference/tool-examples](../../reference/tool-examples)<br>[scripts/checks/phase6_groups.py](../../scripts/checks/phase6_groups.py)<br>[manifest](../../manifest)<br>[docs/reference](../../docs/reference)<br>[docs/development/phase6-review.md](../../docs/development/phase6-review.md) |

共 48 项。目录项是所有权定位范围，不是整目录修改授权；每份实施说明仍须列出精确文件。03–06 只在 src/Logic/V4 与所属测试目录新增本族 DTO/转换/测试，表内现有 parser 是只读依据。07、24 串行接线公共文件，08–23 仅改 G 表工具、对应 Service 和独占规则，共享原语留给 27–34 串行政策任务。

44 拥有引擎/Foundation 执行前审批门、src/Shared 内新增的当前用户命名管道协议、src/Studio/Core 与 Gui 的审批服务/视图/设置及 V4 拒绝详情；45 拥有调用日志脱敏投影与 Core 读取器/Gui 调用面板，不改变原生日志调用顺序；46 拥有 src/Shared 内新增审计写入/校验、InvocationJournal 保留策略、CLI/Studio 校验入口；47 拥有现有 doctor 的复用适配、Gui 体检视图与诊断包。四项的新增文件由各自任务固定名称，交叉文件按依赖串行集成。48 拥有把 RenderPlcVisualDiff 的梯形图布局/SVG 抽成共享逻辑、八版单块出图与图册工具（完整引擎与 Foundation 同一实现）及工作台入口；RenderPlcVisualDiff 输出不变。

35/41 的新快照及旧基线归档位置按正文第 7 节；40 的 ToolUsageData/tool-matrix、41 的 manifest 产物只运行所属生成器。P6-43 仅更新新发布说明，不改历史发布事实。

</details>

<details>
<summary>I. 已合并的第 I 步、V4 类型与原生验收边界</summary>

| 任务 | 当前服务 / 原语 | 再生成的静态证据 | 原生验收 |
|---|---|---|---|
| P4-I1 | [Siemens/Services/VersionControlService.cs](../../src/Engine/Siemens/Services/VersionControlService.cs)<br>[src/Adapters/Native/Vci/VersionControlPrimitives.cs](../../src/Adapters/Native/Vci/VersionControlPrimitives.cs) | [accepted: true（静态）](../../docs/development/evidence/p4-i1-native-evidence.json) | NOT RUN |
| P4-I2 | [Siemens/Services/PlcTablesService.cs](../../src/Engine/Siemens/Services/PlcTablesService.cs)<br>[Siemens/Services/TechnologyObjectsService.cs](../../src/Engine/Siemens/Services/TechnologyObjectsService.cs)<br>[src/Adapters/Native/Plc/WatchTechnologyPrimitives.cs](../../src/Adapters/Native/Plc/WatchTechnologyPrimitives.cs) | [accepted: true（静态）](../../docs/development/evidence/p4-i2-native-evidence.json) | NOT RUN |
| P4-I3 | [Siemens/Services/DevicesService.cs](../../src/Engine/Siemens/Services/DevicesService.cs)<br>[src/Adapters/Native/Hardware/HardwarePrimitives.cs](../../src/Adapters/Native/Hardware/HardwarePrimitives.cs) | [accepted: true（静态）](../../docs/development/evidence/p4-i3-native-evidence.json) | NOT RUN |
| P4-I4A | [Siemens/Services/PlcBlocksService.cs](../../src/Engine/Siemens/Services/PlcBlocksService.cs)<br>[Siemens/Services/PlcSoftwareService.cs](../../src/Engine/Siemens/Services/PlcSoftwareService.cs)<br>[Siemens/Services/TypesService.cs](../../src/Engine/Siemens/Services/TypesService.cs)<br>[src/Adapters/Native/Plc/PlcBlockPrimitives.cs](../../src/Adapters/Native/Plc/PlcBlockPrimitives.cs) | [accepted: true（静态）](../../docs/development/evidence/p4-i4a-native-evidence.json) | NOT RUN |
| P4-I4B | [Siemens/Services/DocumentsService.cs](../../src/Engine/Siemens/Services/DocumentsService.cs)<br>[Siemens/Services/PlcExternalSourcesService.cs](../../src/Engine/Siemens/Services/PlcExternalSourcesService.cs)<br>[src/Adapters/Native/Plc/PlcDocumentPrimitives.cs](../../src/Adapters/Native/Plc/PlcDocumentPrimitives.cs) | [accepted: true（静态）](../../docs/development/evidence/p4-i4b-native-evidence.json) | NOT RUN |

五项已合并；共享原语清单从 [src/Shared/TiaSharedAdapterPaths.props](../../src/Shared/TiaSharedAdapterPaths.props) 的 shared-native/*.props 提取，开关默认 false。静态 accepted 不等于 L5，G3/J 与发布门槛不变。

| 已完成项目 | 当前文件 |
|---|---|
| P6-02：未接线的 V4 信封/错误/分页/批次/计划与单一序列化校验 | [V4/BehaviorCapabilities.cs](../../src/Logic/V4/BehaviorCapabilities.cs)<br>[V4/CallerInputFiles.cs](../../src/Logic/V4/CallerInputFiles.cs)<br>[V4/CandidateHostMapping.cs](../../src/Logic/V4/CandidateHostMapping.cs)<br>[V4/CompileCandidate.cs](../../src/Logic/V4/CompileCandidate.cs)<br>[V4/CompileContract.cs](../../src/Logic/V4/CompileContract.cs)<br>[V4/CompileResultMapping.cs](../../src/Logic/V4/CompileResultMapping.cs)<br>[V4/Construction/Blocks.cs](../../src/Logic/V4/Construction/Blocks.cs)<br>[V4/Construction/ConstructionAdapter.cs](../../src/Logic/V4/Construction/ConstructionAdapter.cs)<br>[V4/Construction/ConstructionInput.cs](../../src/Logic/V4/Construction/ConstructionInput.cs)<br>[V4/Construction/ConstructionJson.cs](../../src/Logic/V4/Construction/ConstructionJson.cs)<br>[V4/Construction/ConstructionSchemas.cs](../../src/Logic/V4/Construction/ConstructionSchemas.cs)<br>[V4/Construction/Declarations.cs](../../src/Logic/V4/Construction/Declarations.cs)<br>[V4/Construction/FoundationConstructionValidation.cs](../../src/Logic/V4/Construction/FoundationConstructionValidation.cs)<br>[V4/Construction/StructuredText.cs](../../src/Logic/V4/Construction/StructuredText.cs)<br>[V4/DeviceCreation.cs](../../src/Logic/V4/DeviceCreation.cs)<br>[V4/DeviceCreationContract.cs](../../src/Logic/V4/DeviceCreationContract.cs)<br>[V4/Domain/BranchStep.cs](../../src/Logic/V4/Domain/BranchStep.cs)<br>[V4/Domain/DomainDto.cs](../../src/Logic/V4/Domain/DomainDto.cs)<br>[V4/Domain/DomainInputs.cs](../../src/Logic/V4/Domain/DomainInputs.cs)<br>[V4/Domain/DomainModels.cs](../../src/Logic/V4/Domain/DomainModels.cs)<br>[V4/Domain/DomainNativeValue.cs](../../src/Logic/V4/Domain/DomainNativeValue.cs)<br>[V4/Domain/DomainSchemas.cs](../../src/Logic/V4/Domain/DomainSchemas.cs)<br>[V4/Domain/DomainShape.cs](../../src/Logic/V4/Domain/DomainShape.cs)<br>[V4/Domain/DomainValidation.cs](../../src/Logic/V4/Domain/DomainValidation.cs)<br>[V4/Domain/DynamizationValidation.cs](../../src/Logic/V4/Domain/DynamizationValidation.cs)<br>[V4/Domain/GraphicSelectionModels.cs](../../src/Logic/V4/Domain/GraphicSelectionModels.cs)<br>[V4/Domain/GraphicSelectionSchemas.cs](../../src/Logic/V4/Domain/GraphicSelectionSchemas.cs)<br>[V4/Domain/GraphicSelectionValidation.cs](../../src/Logic/V4/Domain/GraphicSelectionValidation.cs)<br>[V4/Domain/OpenPipeRequest.cs](../../src/Logic/V4/Domain/OpenPipeRequest.cs)<br>[V4/Domain/SubjectAlternativeName.cs](../../src/Logic/V4/Domain/SubjectAlternativeName.cs)<br>[V4/Envelope.cs](../../src/Logic/V4/Envelope.cs)<br>[V4/Error.cs](../../src/Logic/V4/Error.cs)<br>[V4/FallbackCandidate.cs](../../src/Logic/V4/FallbackCandidate.cs)<br>[V4/FallbackContract.cs](../../src/Logic/V4/FallbackContract.cs)<br>[V4/HardwareCatalogAdmission.cs](../../src/Logic/V4/HardwareCatalogAdmission.cs)<br>[V4/Hmi/ClassicSpecs.cs](../../src/Logic/V4/Hmi/ClassicSpecs.cs)<br>[V4/Hmi/DeviceAmlSpec.cs](../../src/Logic/V4/Hmi/DeviceAmlSpec.cs)<br>[V4/Hmi/HmiBuilderAdapter.cs](../../src/Logic/V4/Hmi/HmiBuilderAdapter.cs)<br>[V4/Hmi/HmiJson.cs](../../src/Logic/V4/Hmi/HmiJson.cs)<br>[V4/Hmi/HmiRules.cs](../../src/Logic/V4/Hmi/HmiRules.cs)<br>[V4/Hmi/HmiSchemas.cs](../../src/Logic/V4/Hmi/HmiSchemas.cs)<br>[V4/Hmi/UnifiedSpecs.cs](../../src/Logic/V4/Hmi/UnifiedSpecs.cs)<br>[V4/HostBehavior.cs](../../src/Logic/V4/HostBehavior.cs)<br>[V4/Inputs/CompositeAttributeMap.cs](../../src/Logic/V4/Inputs/CompositeAttributeMap.cs)<br>[V4/Inputs/DriveFunctionPolicy.cs](../../src/Logic/V4/Inputs/DriveFunctionPolicy.cs)<br>[V4/Inputs/InputSchema.cs](../../src/Logic/V4/Inputs/InputSchema.cs)<br>[V4/Inputs/InputValidation.cs](../../src/Logic/V4/Inputs/InputValidation.cs)<br>[V4/Inputs/InputValues.cs](../../src/Logic/V4/Inputs/InputValues.cs)<br>[V4/Inputs/MapInputs.cs](../../src/Logic/V4/Inputs/MapInputs.cs)<br>[V4/Inputs/NativeValueInputs.cs](../../src/Logic/V4/Inputs/NativeValueInputs.cs)<br>[V4/Inputs/ParameterRef.cs](../../src/Logic/V4/Inputs/ParameterRef.cs)<br>[V4/Inputs/SequenceInputs.cs](../../src/Logic/V4/Inputs/SequenceInputs.cs)<br>[V4/Inputs/ToolCallInputs.cs](../../src/Logic/V4/Inputs/ToolCallInputs.cs)<br>[V4/Inputs/TypedToolInputs.cs](../../src/Logic/V4/Inputs/TypedToolInputs.cs)<br>[V4/NativeResultState.cs](../../src/Logic/V4/NativeResultState.cs)<br>[V4/Paging.cs](../../src/Logic/V4/Paging.cs)<br>[V4/Plan.cs](../../src/Logic/V4/Plan.cs)<br>[V4/PlcBatchImportResultMapping.cs](../../src/Logic/V4/PlcBatchImportResultMapping.cs)<br>[V4/PlcExport.cs](../../src/Logic/V4/PlcExport.cs)<br>[V4/PlcExportContract.cs](../../src/Logic/V4/PlcExportContract.cs)<br>[V4/PlcImport.cs](../../src/Logic/V4/PlcImport.cs)<br>[V4/PlcImportContract.cs](../../src/Logic/V4/PlcImportContract.cs)<br>[V4/PlcImportFiles.cs](../../src/Logic/V4/PlcImportFiles.cs)<br>[V4/ResultMapping.cs](../../src/Logic/V4/ResultMapping.cs)<br>[V4/SaveCloseCandidate.cs](../../src/Logic/V4/SaveCloseCandidate.cs)<br>[V4/SaveCloseContract.cs](../../src/Logic/V4/SaveCloseContract.cs)<br>[V4/SessionCandidate.cs](../../src/Logic/V4/SessionCandidate.cs)<br>[V4/SessionCandidateContract.cs](../../src/Logic/V4/SessionCandidateContract.cs)<br>[V4/SourceCandidate.cs](../../src/Logic/V4/SourceCandidate.cs)<br>[V4/SourceContract.cs](../../src/Logic/V4/SourceContract.cs)<br>[V4/V4Json.cs](../../src/Logic/V4/V4Json.cs)<br>[V4/V4Validation.cs](../../src/Logic/V4/V4Validation.cs) |
| D334：源码目录迁移完成；产品名/运行目录仍待 36–39 | [docs/development/repository-layout.md](../../docs/development/repository-layout.md) |

D1 发布政策按台账逐版本生成；只有族行中明确的 `L5[releaseKey]=PASSED` 启用 safe-v4，未记录或失败均保持 current。当前 88/88 条记录为 current。生成器不运行原生调用，测试构建覆盖不改变台账或发布记录。

</details>

<!-- phase6-generated:end -->
