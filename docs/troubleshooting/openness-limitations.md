# Openness 与运行时通道的边界

## V20/V21 host 与 worker 的就绪状态

V20/V21 MCP host 在 TIA 未安装、检测到的 TIA 版本不匹配、Openness DLL 无法初始化或用户不在 `Siemens TIA Openness` 组时仍可提供 MCP 诊断。`InitializeEnvironment`、`GetEnvironmentDiagnostics` 和 `GetOpennessWorkerStatus` 显示失败原因与修复建议。需要原生 TIA 的工具在 dispatch 前返回 `RESOURCE_UNAVAILABLE`；环境门禁拒绝时，V4 元数据为 `rejected-before-operation` / `not-started`。读取、离线分类和诊断工具可继续使用。

每个 MCP 会话使用独立 worker，宿主提供传输、鉴权与诊断。派发后超时或崩溃可能使结果未知；先检查工程，再恢复会话并重新绑定。恢复步骤见[worker 与恢复](../guides/openness-worker-isolation.md)。

本页描述当前源码的边界和已知现场问题。八版入口并不具有同一套能力，先核对[版本矩阵](../reference/version-tools.md)与当前服务 `GetToolUsage`。官方 API 存在、工具可列出、离线测试通过、真实工程执行成功是不同证据。

## 已知未解决的现场问题

| 问题 | 当前处理与剩余边界 |
|---|---|
| `ManageStartdriveParameter` 读取 BICO `p2051[0]` 时 TIA 崩溃 | 已按官方 `Parameters.Find(name).Value` 路线改为精确单值读取，仅在找不到条目时检查只读视图；不展开位、限值、枚举或继续读取源参数值。离线回归通过，原生复测 **NOT RUN**，根因未确认。返回另一个 `DriveParameter` 表示连接来源，普通值按标量解释，`null` 保留未知，不能当作零、实时值或未连接证明。 |
| Unified 脚本模块库类型 `Name` 修改 | 指定样本在项目库和独立全局库两次测试中均伴随 TIA 退出，没有已验证修复；“先在全局库改名再同步”也失败。[原生证据](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/history/unified-library-rename-native-20261001.json)。 |
| PLC 与 Unified 原生交叉引用 | PLC 在 V21 有真实退出记录。2026-10-30 维护者决定：V14 SP1–V20 在 API 可用时默认允许；V21 的 PLC 与 Unified 查询仍需人工会话开关或进程变量。工具返回未查询不等于零引用。编译通过也不能证明查询稳定。通用反射入口不能绕过相同策略。 |
| HMI 深层属性读取 | 故障或限制下可能仅返回部分快照；`HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` 暂缓读取。[快照诊断](hmi-snapshots.md)说明完整性、会话阻断与日志。 |

这些项目仍使用统一调用示例库，不再各自建立规范入口。官方示例：[V20 BICO](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)、[V21 BICO](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)。示例的正确参数不能消除尚未确定的原生故障。

V14 SP1–V20 的原生交叉引用策略默认允许，工具仍按官方 API 注册：PLC `GetPlcCrossReferences` 从 V18 起、Unified 从 V19 起可用。删除工具仍须显式 `crossReferences=true`，默认 `false` 表示未查询。V21 的 PLC、Unified 和删除时引用检查要求工作台设置中的 **Allow native cross references (V21) / 允许原生交叉引用（V21）** 人工开关，或启动服务前设置 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1`，任一即可。人工开关仅作用于所选 MCP 会话，在工作台关闭或会话结束后失效；不保存为下次启动偏好，MCP 工具、工作台控制操作和审批均不能修改。人工设置经验证工作台映像与对端 PID 的 operator 通道发送，宿主持有签名密钥和拒绝其他写入/删除的会话文件句柄；worker 仅接受该会话的有效签名与仍存活的工作台身份。该通道不是 MCP 或 Workbench-control 操作。宿主与执行查询的 worker 使用同一会话设置。首次查询前请保存工程；编译通过仍不能保证安全。PLC 的全部块/类型一致性必须可读且为 true；反射入口、列表上限和退出后不自动重试的保护不变。V21 真机确认 **pending / NOT RUN**。

`AnalyzePlcCrossReferences` 在八版复用现有普通 PLC XML 批量导出和 `AnalyzePlcReferences` 离线引擎，一次调用覆盖整个普通软件或指定块组（最多 256 块），文件保留在会话输出目录。结果明确给出软件、导出块数、未导出范围、失败/未尝试项目和索引失败；每块列出显式调用者、被调用者和全局符号位置。软件单元、系统块、SCL 文本体、间接调用、HMI 与外部客户端不在覆盖范围内。它不调用原生 CrossReferenceService，也不编译或保存；空结果、未查询或部分结果都不是“没有引用”或可安全删除的证据。原生失败或退出后先检查工程与 MCP/TIA 日志，再按[恢复步骤](../guides/openness-worker-isolation.md)显式恢复会话并重新绑定，不要自动重试。

## HMI 预览、接口值与版本追溯

旧 `EnsureUnifiedHmi*`、`ApplyUnifiedHmi*` 和 `BindUnifiedHmi*` 工具默认 `dryRun=true`，只返回计划，不调用原生 API。预览只做托管参数检查，不证明目标存在或 TIA 语义有效；审核计划后显式传 `dryRun=false`，执行仍需既有 Workbench 审批。执行失败可能保留部分修改，不自动回滚。

HMI 路径使用绝对 `/Group/Sub/Name`，段按 URI 转义；相对路径和反斜杠写法被拒绝，消息给出改正形式。旧 `screenName/tagTableName` 参数仍可传唯一的单个名称；需要指定目录时必须用绝对路径。`ManageUnifiedHmiGroup.groupPath` 与 Plant 路径要求绝对写法。

通用对象路径可用 `{"property":"Interface","key":"PropertyName","name":"Speed"}` 选中面板接口项，再读取或编辑 `Value`、动态化或属性事件。`key` 只接受 `Name` 和 `PropertyName`，默认 `Name`。写 `Value` 前读取当前值的 CLR 类型，按该类型转换并在写后回读；当前值为 null 或复杂类型无法转换时明确拒绝，不能从 JSON token 猜类型。接口值的真实类型和变量接口绑定仍需在对应 TIA 版本验证。

`ApplyUnifiedHmiScreenDesign` 拒绝通过属性重命名；同名异类对象报冲突。`writeResults` 保留每个几何或属性写入返回值与回读，返回 true 而回读不符仍属失败。`BindUnifiedHmiTagDynamization` 同样回读请求的绑定字段，`EnsureUnifiedHmiTagTable` 核对目标的 Parent。

`GetUnifiedFaceplateInstance` 从实例暴露的 `LibraryTypeInstanceInfo.LibraryTypeVersion` 读取类型及版本身份，并直接读取该连接版本；调用者无需猜 `typePath/version`，二者仅作可选核对条件。服务缺失、返回 null、版本对象或 TypeObject 不可用会返回具体缺口；不会从 ContainedType 推导格式、改用默认版本或搜索无关类型。该服务在真实 Unified 实例上是否非空、版本导出及接口值语义仍未验证。Classic 库类型按类型名和继承关系匹配，版本继续单独返回。

## 工程数据与在线数据

| 通道 | 当前可以做什么 | 前提与限制 |
|---|---|---|
| TIA Openness | 工程对象读写、导入导出、编译；V20/V21还有上线/离线、下载、站上载和在线比较等指定接口。 | 取决于版本、对象、安装选件与原生 API。工程标签或 DB 初始值不是 CPU 实时值。 |
| S7 Web server API | V20/V21 的 `GetPlcWebVars` / `WritePlcWebVars`、`GetPlcWebDiagnostics`、`SetPlcWebOperatingMode`。 | PLC 固件及 Web API 支持、账户权限和证书；独立于 Openness。诊断工具的设备信息和运行模式不等于完整故障缓冲区。 |
| Unified Open Pipe | V20/V21读取/写入 Runtime 标签、读取活动报警及限定的单次消息。 | MCP 运行在 Runtime 本机，用户具备对应组权限；不提供任意远程管道或持续订阅。 |
| PLCSIM Advanced API | V20/V21管理仿真实例、读写标签并运行限定场景。 | 本机安装匹配 API；仿真通过不等于真实 CPU 验收。 |
| 其他运行时/伴随工具 | 按各工具声明的 OPC UA、S7 或独立程序接口执行。 | 分别检查连接目标和实际协议，不能因 Openness 已连接就视为这些通道已就绪。 |

宿主只提供其公开的 PLC 子集，不因V20/V21存在某个运行时工具而自动具备该工具。V20/V21中部分选件入口仅在一个版本有实际调用路径，详情见[逐版 Openness 覆盖](../reference/openness-coverage.md#v20v21-optional-tools-and-scope)。

## 当前没有可承诺的 Openness 路线

- 独立 CPU RUN / STOP 不是普通 Openness 在线接口；下载回调中的停止/启动属于下载流程。需要独立模式切换时核对实际运行时工具。
- 未实现通过 Openness 读取完整 CPU 诊断缓冲区或在线模块 LED / 健康状态的通用路线。
- 不提供运行时强制/解除强制入口。删除离线强制表或下载工程不能作为“已清除 CPU 强制”的证明。
- 没有按任意块集合选择性下载的已验证通用封装。
- 未证实可从零创建 Unified 面板库类型、直接修改类型内部所有控件和局部脚本，或通过公开 API 重建 PLC 交叉引用索引。
- Unified 普通按钮事件与全局脚本模块使用不同接口；CWC ZIP、普通画面对象与面板库类型也不能互换。

`ScanAccessibleDevices` 已能经指定 PC 接口查询可访问设备，不应再列为“只能看工程配置”。监控表定义、修改值和当前值的读取能力依对象而异；定义编辑成功不能冒充已经向 PLC 执行了一次修改。

## 读懂结果再继续

导入先检查状态、消息与实际返回对象；SIMATIC SD 使用目录及不带扩展名的文件名。编译检查错误数和完整嵌套诊断，不能只看请求返回。保存工程需要独立保存操作；下载是另一项明确动作。

当返回 `mayHaveChanged`、部分结果或结果未知时，先核对当前工程与日志，避免自动重复写入。`RETURNED` 日志只证明调用返回，最后一个 `BEFORE` 也不能单独证明崩溃根因。真实验收与历史原文见[证据索引](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md)。
