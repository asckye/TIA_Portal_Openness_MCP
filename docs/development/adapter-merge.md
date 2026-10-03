# 按版本的类型化适配器设计（重构阶段 4）

[重构计划](refactor-plan.md) · [引擎拆分设计](engine-decomposition.md) · [验证分层](validation.md) · [版本框架](unified-version-framework.md)

本页是 P4-01 的设计结论。路径前缀：**E** = `tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/`，
**F** = `tools/tiaportal-mcp/src/TiaMcp.Adapters/Native/` 与 `Policy/`（第 D 步迁移后），**S** = `tools/tia-openness-studio/src/TiaOpenness.Openness/OpennessSession.cs`。
数字为 2026-10-03 的源码统计。

## 现状：三套原生实现

| 实现 | 规模 | 编译 | 生产使用 |
|---|---|---|---|
| 引擎 Portal | 87 个 partial，约 3.1 万行 | V20/V21，net48，织入插桩 | V20/V21 全部工具 |
| Foundation（`TiaMcp.Adapters/V*` 链接 F 源码） | 46 个文件，约 4.4 千行 | 8 次：14sp1–16 用 net461，17–21 用 net48，织入插桩 | V14 SP1–V19 worker；V20/V21 适配器只构建不使用 |
| Studio | 约 2.4 千行 | 8 次，全部 net48，未织入 | 桌面端桥接进程 |

### 能力重叠

| 能力 | 引擎 | Foundation | Studio | 行为差异 |
|---|---|---|---|---|
| 连接 | E `Portal.cs`、`Portal.Binding.cs`：按项目名、PID+启动时间+路径选择，或启动/隔离连接；进程租约；MTA 线程带超时 | 只接受显式 PID，从不启动 TIA | 连第一个接受的进程，否则启动 TIA | 三种选择规则；只有引擎核对进程身份 |
| 打开/保存/关闭 | `OpenWithUpgrade`（含 UMAC），反射回退到 `Open`；保存跳过本地会话；关闭无条件 | `Open` 不升级；拒绝已打开的项目；dryRun/confirm；关闭拒绝已修改或借用的项目 | 复用已打开项目；关闭无条件；无新建 | 升级与否、复用与拒绝、关闭保护 |
| PLC 查找 | 精确匹配后模糊匹配（G9）；跳过未分组设备并吞掉异常 | 读取接受唯一别名，写入要求精确转义路径；含未分组设备，深度 ≤128 | 设备名不区分大小写，取设备上第一个 PLC 软件 | 三种路径语法 |
| XML 导出/导入 | 导出先删已有文件；单个导入总是 `Override`，在临时副本上改写 `<Engineering version>` 和 BOM；目录导入默认覆盖且失败后继续 | 从不覆盖（暂存发布）；要求块一致且为离线目标；默认 `None`；输入文件加锁并计算哈希；批量遇错即停 | `ExportOptions.WithDefaults`；拒绝已有文件；逐项继续；只导入根组，按文本识别 UDT，遇错即停 | 覆盖行为和选项三者互相矛盾 |
| 文档 | E `Blocks.cs` | `PlcDocument*`，V20/V21，仅 GlobalDB | — | 准入规则不同 |
| 外部源 | 名称宽松匹配（去扩展名）；删除幂等 | 全部 8 版；V14 SP1 生成返回 void | 用 `GenerateSource` 导出；导入时建临时源、生成、删除 | 幂等删除与核实删除 |
| 编译 | Safety 登录不登出；Unified HMI 向上查找 | 离线检查；Safety 登录并登出 | 软件或其所在设备；无 Safety 处理 | 登出与离线前提不同 |
| VCI | `VersionControlService.cs` 直接调用新版 API | — | `OpennessVersionControl.cs`：16/17 初版、18/19 旧版、20/21 新版 | V20/V21 新版 API 有两份实现 |
| 硬件目录/添加设备 | `Portal.Devices.cs`：反射加评分；`AddDeviceWithFallback` 遍历 MLFB×版本列表，多次原生创建 | 仅 19–21：类型化 `Find`，恰好一次 `CreateWithItem`，计划哈希 | — | 同名工具，回退语义相反 |
| 监控表、工艺对象 | 导出、目录导出、按 `Override` 导入更新；监视与映射 | 名称 15.1–21，导出 16–21；读取 8 版（V18 及以前只有根组） | — | |

### 横向差异

- **线程**：引擎在线程池 MTA 线程上运行，由一个静态信号量串行化，`VerifyBinding` 拒绝非 MTA 线程；Foundation 为
  `[STAThread]` worker，构造要求 STA，每次调用核对所属线程；Studio 在桥接进程的 STA 主线程上同步分派。
- **超时**：引擎隔离 10–180 秒（默认 120）后结束子进程；LegacyHost 2 分钟后标记会话失效，只关闭 stdin，不结束 worker；
  Studio 10 分钟后报错，释放时结束桥接进程。
- **错误**：引擎 `PortalException`（8 个代码），打开返回 bool + `LastConnectError`；Foundation 为
  `{code −32602|−32603, outcome, evidence}` 加 `RequiresSessionReset`；Studio 按异常类型和消息子串映射到 −32000…−32004，
  `data` 带堆栈。
- **结果形状**：引擎 `Responses.cs` 与 `JsonObject`；Foundation 为 PascalCase POCO，外包 `V17*Envelope`；Studio 为 PascalCase
  DTO 加字符串枚举。

## 目标契约

**原则**：阶段 4 统一原生代码的位置、契约、线程模型和协议，**不统一**工具的可观察行为。行为不同的地方，共享原语提供显式选项，
各宿主保留自己的策略。让行为一致属于破坏性变更，放到阶段 6。

- **程序集**：`TiaMcp.Adapter.<key>` 仍在 `TiaMcp.Adapters/V*/`，保留显式允许清单、精确身份核对和编译后织入+校验。F 源码移到
  `TiaMcp.Adapters/Native/{Session,Plc,Hardware,Vci,Hmi}` 与 `Policy/`；Studio 专有的原生代码作为扩展面并入：连接或启动、
  `HmiNavigator`、VCI 初版/旧版、`GenerateSource`。只有不接触原生对象和原生调用序列的策略可以离开织入程序集；跨程序集传入
  `IEnumerable` 的调用会被织入为“枚举输入”，移动这类代码会改变覆盖。
- **契约**：新建 `TiaMcp.Adapters.Contracts`（net461;net48;net8.0），不依赖西门子和 JSON 库。根接口 `IOpennessAdapter`
  （ReleaseKey、ApiIdentity、`AdapterCapabilities`）；扩展面不可用时为 null（沿用 Studio 的做法）：`IPortalSession`、
  `IPlcProgram`、`IPlcData`、`IHardware`、`IVersionControl`、`IHmiExport`。DTO 即现有 Foundation POCO，名称和成员顺序不变。
  对象用 Foundation 的规范转义路径寻址，原生句柄不跨越契约。
- **错误**：`AdapterException` 带 `AdapterErrorCode`（InvalidArgument、NotFound、Ambiguous、InvalidState、
  NotSupportedOnRelease、NativeFailure、ProcessLost）、`AdapterOutcome`（RejectedBeforeNative、ReadFailed、Unknown）和证据。
  错误文本属于兼容契约，由宿主拥有：分别映射到 `PortalErrorCode`、worker 的 `outcome` 或 Studio 的 RPC 代码。
- **线程**：宿主创建会话时传入 `ThreadPolicy`。worker 和桥接进程为“所属线程 + STA”，引擎为“任意线程串行 + MTA”，都与现在一致。
  适配器执行策略、拒绝重入、从不切换单元。
- **取消、超时、进度**：只在进入原生代码前和批量项之间检查取消。超时仍是宿主策略；原生调用已开始后超时一律为 Unknown，会话
  标记失效，调用不重放。进度为引擎线程上的同步 `IProgress`。
- **引擎在进程内使用适配器（必需）**：HMI 和设备代码与 PLC 路径共用同一个 `TiaPortal`/`ProjectBase`，第二个 Openness 客户端会
  与引擎的进程租约冲突。`TiaMcpServer.V20/V21` 引用 `Adapter.20/21`（都是 net48）；PLC 扩展面通过仅 net48 的类型化入口
  `PlcServices.Over(Func<ProjectBase>)` 建在阶段 3 内核持有的句柄上。Unified HMI、设备、在线和 Safety 仍留在引擎。
- **织入**：适配器已在构建时织入并校验；`Build-Release.ps1`、`Build-Studio.ps1`、`Validate-Bundle.ps1` 对打包的副本重新校验。
  适配器日志增加输出接口，引擎只写一份带同一关联 ID 的日志（前提：P2-04 合并两份 `InvocationJournal`）。“西门子成员多重集
  不变”的验收改为引擎 ∪ 适配器，每步附预期增减清单。
- **版本特性**：一张表 `build/TiaFeatures.props`，适配器、Studio 和引擎共同导入。先输出现有宏名：`PLC_SOURCE_RESULTS`、
  `PLC_RH`、`PLC_WATCH_READ`（15.1+），`PLC_SPECIAL_EXPORT`（16–20），`PLC_WATCH_EXPORT`（21），`PLC_SAFETY`（17+），
  `PLC_TECH_GROUP_READ`、`PLC_HARDWARE_CATALOG`（19+），`PLC_DOCUMENT_EXPORT`（20+），`STUDIO_VCI`（16+），
  `STUDIO_VCI_INITIAL`（16–17），`STUDIO_VCI_MODERN`（20–21），`TIA_V20`（20）。同时生成 `AdapterCapabilities`，
  `FoundationTools.Available` 不再硬编码 `major >= 19` 之类的判断。

## Worker 协议

| | 引擎隔离 | Foundation 协议 2 | Studio 桥接 | 预览 v2（`TiaMcp.WorkerProtocol.*`） |
|---|---|---|---|---|
| 分帧 | 换行 | 换行 | 换行 | 4 字节长度前缀 + 终止符；nonce/PID 前导 |
| 信封 | MCP JSON-RPC（`initialize`、`tools/list`、`tools/call`），id 为 `worker_N` | JSON-RPC 2.0，`adapter.*` | 协议 2 Studio profile，保留原方法名（含 `ping`） | 类型化 v2 帧 |
| 握手 | hello（协议 1、引擎 SHA、pid）+ 目录比对 | hello：版本、worker/adapter SHA-256、PID、nonce、初始纪元 | hello：版本、桥接/adapter SHA-256、PID、nonce、初始纪元 | hello：版本、哈希、令牌、绑定纪元 |
| 失败模型 | nativeOutcomeUnknown、代次 | `error.data` 的 outcome + 证据、失效后不重放 | 记录 outcome，`rpc` 保留原错误；已处理错误继续会话，通道故障使其失效 | ReadFailed / 失效 |
| JSON 库 | STJ | 信封 STJ；DTO worker Newtonsoft / 宿主 STJ | 信封 STJ；参数/DTO/原错误仍用 Newtonsoft | 两套编解码 |
| 状态 | 已发布（可选） | P4-E2 已实现，真机验收前不发布 | P4-F 已接入共享通道，真机验收前不发布 | 第 A 步已删除 |

**协议 2（P4-E2 / P4-F 实现）**：

- 换行分隔的 JSON-RPC 2.0。三套已发布协议中两套已在用，并在 Windows 上验证过。
- 必须先发 hello 行：版本键、worker 与适配器 SHA-256、pid、宿主提供的启动 nonce。
- 同时只有一个请求，id 严格递增。Foundation 的 `error.data = {outcome, evidence}`，Studio 另带原始 `rpc`。支持进度通知。
- 大小上限：请求 1 MiB，响应 16 MiB。
- Foundation worker 和 Studio 桥接进程通过共享库 `TiaMcp.WorkerChannel`（net48;net8.0，无 Siemens 引用）取代
  `WorkerClient`、`BridgeClient` 和 PlcWorker 的读取循环；Foundation 使用 `adapter.*`，Studio 保留原方法名。
- Studio 如实记录 `RejectedBeforeNative` / `ReadFailed` / `Unknown`；桥接已捕获并返回的合法错误保持会话可用，
  不改变绑定纪元或设置 `_faulted`，UI 仍收到原错误。Foundation 的 `Unknown` 仍使会话失效。
  Studio 的通道故障、超时和分派后取消终止会话，不重放、不自动重启；未发送的取消不影响后续调用。
- 引擎隔离子进程代理全部 488 个工具，继续使用 MCP 方法，但采用同样的 hello、上限和 outcome 规则。

Foundation 的具体信封如下（每行一个 UTF-8 JSON 对象，无 BOM）：

- `hello` 是无 id 通知，`params` 包含 `protocol:2`、`releaseKey`、`workerSha256`、`adapterSha256`、`pid`、64 位十六进制 `nonce`、`bindingEpoch:0` 和 `bound:false`。worker 必须新建且未绑定。宿主用启动文件的实际哈希、子进程 PID 和每次启动随机生成的 32 字节 nonce 核对；不接受协议降级。
- 请求包含 `jsonrpc:"2.0"`、严格递增的正整数 `id`、`method:"adapter.<operation>"`、原有参数对象 `params`、`bindingEpoch`。宿主先完成 hello 验证，再发送请求。通道只允许一个在途调用，重入或并发使用使会话失效；LegacyHost 的既有串行调度仍在通道之外。
- 回复包含相同 id、`bindingEpochBefore`、`bindingEpochAfter`，以及唯一的 `result` 或 `error`。`error` 含原有 code/message，`data` 必须有 `outcome`（`RejectedBeforeNative` / `ReadFailed` / `Unknown`）和 `evidence`（对象或 null）。LegacyHost 将其映射回原有 MCP 错误码、消息和 evidence 文本。
- `progress` 是无 id 通知，`params` 包含 `requestId`、从 1 递增的 `sequence`、0–100 的 `percent`，每次请求至多 1024 个通知；只有匹配的在途请求可以接收。同步 server 拒绝跨线程、回调结束后的进度。客户端持续读取 stdout，空闲期间的进度、旧/未知 id、重复回复和再次 hello 同样导致失效。
- 请求上限 1 MiB、worker 输出行上限 16 MiB，均按 UTF-8 字节计（不含 LF）；读取时先限长，再验证 UTF-8 和解析 JSON。缺少换行的末尾、空行、BOM、重复/未知信封字段都拒绝。发送也检查相同上限。
- 取消发生在分派前时不发送、不消耗 id、不使会话失效。分派后的写入/读取错误、超时、取消或会话错误一律为 Unknown，不重试、不自动重启；进度不延长调用预算。通道释放只关闭自己的管道，不结束 worker 或 TIA 进程。

绑定纪元来自 worker 在每次分派前后读取的 `PlcFoundationEngine.ReadState()` **纯托管缓存**（PID、项目路径、项目所有权、LocalSession 标志）。这些字段改变时纪元加一；成功 Disconnect 后使用终止状态，避免再次调用已终止的适配器。宿主按 Attach/BindProject、非 dryRun 的 Open/Create/Close 预期加一，Disconnect 允许零或一次递增，其余成功调用及已知失败要求不变。每个回复的 before 必须等于宿主保存的纪元，异常变更使会话失效。这不提供外部 TIA 进程启动时间或其他客户端改绑的验证；原有 BindingSnapshot 生命周期接线仍属于后续任务。

信封统一使用宿主已有的 `System.Text.Json 10.0.0-preview.4.25258.110`；worker 的 Newtonsoft DTO 结果和错误证据通过 `WriteRawValue` 原样嵌入，宿主的 DTO codec 仍是 STJ，P2-04 再统一 DTO codec。worker 部署包含 WorkerChannel、STJ 及 net48 的传递依赖；构建脚本既有的 DLL 复制规则会一起部署，三份发布文件清单分别校验它们。

[worker-channel 回归套件及预览规则映射](../../tools/tiaportal-mcp/tests/TiaMcp.WorkerChannel.Tests/README.md)覆盖双方状态机和独立进程管道故障。`TiaMcpServer.TransportFixture` 是 Foundation 协议 2 的夹具；第 A 步删除了无 `Server` 前缀的两个预览夹具及其项目。

**第 A 步删除 `TiaMcp.WorkerProtocol.*`**：8 个源码目录、7 个测试项目（实测 6,676 个断言）以及
`TiaMcp.TransportFixture`/`TiaMcp.EndpointFixture`。理由：生产中没有使用；长度前缀分帧只在 Linux 上验证过；维护两套并行编解码，
与 P2-04 的单一序列化边界相矛盾；JsonV2 不支持 net461，无法服务 14sp1–16 的 worker。删除前已把适用规则改写为协议 2 的要求，
并在 `TiaMcpServer.TransportFixture` 上测试：启动 nonce 身份、每次调用前后核对绑定纪元、拒绝重放、只读失败类别、迟到进度
导致失效。

## 第 A 步最终规则核对

删除前重新逐项阅读了七套预览测试的 `Program.cs`、Endpoint 的 `EndpointUnitCases.cs`、
JsonLegacy 的 `DifferentialCodec.cs` 和两个夹具。原套件实测断言数：Core 105、JsonV2 343、
JsonLegacy 5,462、HostPreview 78、AsyncPreview 203、HostTransport 102、Endpoint 383（含进程测试 302），
共 **6,676**。删除 70 个版本化文件、10,572 行（其中 C# 25 个文件、3,156 行）。循环、差分语料和进程断言均按原计数器统计，不等同于 xunit 用例数。
下表按断言规则归组；`P` = `ProtocolTests`，`S` = `ServerTests`，`X` = `ProcessTests`，
`A` = 新增的 `PreviewRuleTests`，均在 [worker-channel 测试目录](../../tools/tiaportal-mcp/tests/TiaMcp.WorkerChannel.Tests)。

| 预览断言组（包含循环变体） | 协议 2 测试或不移植原因 |
|---|---|
| 无 Siemens 依赖、八版身份往返、必填哈希/会话身份；版本协商、hello 前请求、非初始绑定、重复 hello | A.`ExactReleaseHelloRoundTrip` / `LaunchIdentityRequiresCompleteFields`；P.`FreshUnboundHelloRequired` / `BadHelloPoisonsSession` / `DuplicateHelloTerminal`；S.`NoHelloTerminal` / `NonfreshHello`；X.`HandshakeFailedBeforeWrites`。精确版本允许清单仍由 Foundation 版本策略负责，通道匹配宿主给定值，不另设版本清单 |
| 缺失、重复、大小写混淆、未知信封/嵌套字段；非整数/越界 id、错误版本、错误结果/错误形状、进度数值 | A.`StrictEnvelopeRejectedBeforeDispatchOrResult` 穷举 hello/request/reply/error/progress 字段；P/S.`BadFramingPoisonsBeforeValidation`；P.`InvalidProgressTerminal` |
| 帧大小上下界、截断、UTF-8/BOM、分片读取、发送上限、精确上限 | P.`OversizedLinePoisonsBeforeParsing` / `ExactFrameCapAccepted`；S.`RequestByteCapAndFramingBeforeDispatch` / `EmitFailureTerminal`；A.`FragmentedUtf8RequestAndExactByteCap`；X.`TerminalAmbiguityNoReplay` |
| 一次分派、重复/过期请求、错误响应 id、无回复/重复回复/迟到 hello、失败后无额外发送 | S.`DuplicatePoisonsWorker` / `OwningThreadAndRawDtoArePreserved`；P.`UnknownReplyIdOrBindingEpochPoisons` / `LateProgressAndTrailingFramesPoison`；X.`TerminalAmbiguityNoReplay` / `TrailingFramesPoisonIdlePeer` |
| 绑定/关闭递增、调用前后纪元匹配、已知失败不得改变绑定、写入不能声称 ReadFailed | P.`CloseAdvancesBindingEpoch` / `UnknownReplyIdOrBindingEpochPoisons` / `ReadFailureOutcomePreserved`；S.`BadFramingPoisonsBeforeValidation`；A.`KnownFailureCannotChangeBindingEpoch` |
| 读取失败/操作前拒绝可继续，Unknown 失效，code/message/evidence、DTO/null、独立结果与进度可复用 | P/X.`ReadFailureOutcomePreserved`；P.`ProgressAcceptedAndDtoBytesPreserved`；S.`OwningThreadAndRawDtoArePreserved`；A.`ExplicitNullAndReadErrorCodePreserved` / `ProgressCapAndPerRequestSequence`；Foundation 既有 DTO/业务结果测试继续保留 |
| 发送前取消、发送后取消/超时/管道错误、不合作 I/O、迟到完成、整个调用一个预算 | P.`CancelBeforeDispatchHasNoSendOrPoison` / `UnknownResultPoisonsHostNoReplay` / `BudgetNeverResetsOnProgressOrCleanup`；X.`UnsentCancellationStaysUsable` / `TerminalAmbiguityNoReplay`；A.`UncooperativeWriteIsBoundedAndNeverReplayed` |
| 并发、回调/观察器/回复写入重入；非法/迟到/跨线程/过量进度；吞掉进度写出异常 | P/X.`ConcurrentEndpointPoisoned`；S.`SwallowedInvalidProgressAndReentryTerminal` / `LateProgressTerminal` / `ObserverTerminal` / `EmitFailureTerminal`；A.`SwallowedReentryAndProgressEmissionFailureAreTerminal` / `ProgressCapAndPerRequestSequence` |
| 不同进程管道所有权、释放不影响另一个会话 | X.`ProgressAcceptedAndDistinctOwnedHandles`；预览的强制杀进程、回收确认不移植，生产通道仅关闭自有管道 |
| 长度前缀/二进制终止符、帧枚举单次消费及其复制/Dispose/Abort/Reset、可配 FrameLimits、交换总字节预算 | 专属于删除的 IV2Exchange/帧 API；协议 2 改用持续行读取、固定字节/进度上限和单一调用预算，不保留枚举结束后的提交边界 |
| String worker_N id、重复的引擎/会话/操作身份、相关 id、RequestLogContext 允许字段/异步作用域/日志脱敏 | 预览专属信封与日志 API 删除；协议 2 使用启动身份及递增数字 id，不记录 payload；适配器日志未改变 |
| 项目哈希、进程启动时间、完整 BindingSnapshot、操作绑定前提、未知操作的通道准入 | 不向共享通道迁入尚未接线的原生观察/业务策略；Foundation 的 WorkerOperations、路径/身份与准入测试保留，外部改绑检测仍待后续任务 |
| 固定脱敏错误文本、null 拒绝、opaque payload 重名/大小写/代理项校验、32 层深度、codec 规范化、Unicode/fuzz 差分、全局 Newtonsoft 配置隔离 | 这些是预览 codec 合同；协议 2 保留原有 DTO codec、原始结果字节、null 和错误文本。严格检查信封与 UTF-8，但不声称复制两套 codec 的 5,462 个差分断言；DTO 黄金测试/响应快照继续守护生产边界 |
| 注入时钟倒退、2 小时上限、编码期间耗时/取消消耗 id、参数编码失败后可继续、校验回调结束才提交 | 预览 TimeProvider/验证回调 API 不存在于协议 2；使用实际单调计时预算，取消发送前不消耗 id；编码错误终止通道。业务校验仍在 LegacyHost，验证失败使 WorkerOutcomeState 失效 |
| AsyncPreview 并发拒绝但在途调用成功 | 不移植其相反策略；按 D7/P4-E2 使用 Endpoint 的并发即失效规则；LegacyHost 原有串行队列保留 |

LegacyHost 旧 `WorkerProtocol.Decode` 无生产调用方，已删除。逐处处理如下：

| 原调用处 | 处理 |
|---|---|
| `WorkerClientTests`：null + 5 个坏响应 | 删除 6 个重复断言；由 A.`ExplicitNullAndReadErrorCodePreserved`、A.`StrictEnvelopeRejectedBeforeDispatchOrResult` 与 P 的 id 检查覆盖 |
| `PathAndIdentityTests`：三种 outcome | 删除 3 个旧解码 code 断言；协议 2 的错误码由 P/A 的错误测试覆盖；以宿主异常继续验证 3 个会话重置策略断言 |
| `ExchangeContractTests`：unknown + evidence | 删除 1 个重复断言；P.`ReadFailureOutcomePreserved` 验证证据与失效，原有 Foundation 工具错误映射测试保留 |
| `AdditionalMutationResultTests` | 用宿主 `WorkerOperationException` 输入保留 KnownNoMutation、poison 与禁止重放断言；协议 2 分类在 P/X 验证 |
| `RuntimeQueryTests`、`SoftwareReadDispatchTests` | 直接把 worker 的 Newtonsoft DTO 文本传入 JsonNode，与现行 WorkerClient 的结果边界相同；保留全部业务/日期/nullable 断言 |
| `SupplementaryReadTests` | 以宿主异常连接脱敏策略和 MCP 边界，保留全部断言；不再声称调用旧解码器 |

foundation / foundation-api 的最低数量各减少上述 **10** 项；offline / offline-v20 不变。
`Check-TiaFeatures.py` 的动态工程 glob 与 expectation、三份必需文件/发布清单、Build 脚本及 test-suites
均没有预览工程显式条目，无需改写。三个源码门禁的预览豁免已删除，原基线不含预览条目。

## JSON（P2-04 的输入）

- 适配器和契约不使用 JSON 库。适配器日志目前用 Newtonsoft `JObject`，改为扁平行写入器；`BindingSnapshot` 改为 `Func<string?>`。
- 只在进程边界序列化，每侧一个编解码文件：宿主侧（引擎、LegacyHost、Studio 桌面端）用 System.Text.Json；worker 侧（PlcWorker、
  Studio 桥接）在 14sp1–16 仍为 net461 期间用 Newtonsoft 13.0.4（STJ 7 起不再支持 net461）。D4 已决定把所有 worker 改为 net48，
  P4-E2 仅把信封改为宿主已有 STJ 版本，DTO 继续用原有 codec；全部 DTO 统一及删除 Newtonsoft 留到 P2-04。
- Studio 的 Contracts、RpcDispatcher 和 BridgeClient 改用 STJ，显式配置 PascalCase 和字符串枚举。
- 每个 DTO 有 worker 编解码 ↔ 宿主编解码的黄金样本测试，保护 P0-06 响应快照。

## 迁移顺序

每一步都要通过 L0/L1/L2/L4、P0-06 响应快照、`foundation`/`foundation-api` 最低数量、Studio xunit 套件和
`Test-FoundationTransport.py`；B–E 和 G–I 还需 L3。

改变原生路径的步骤用构建开关 `-p:TiaSharedAdapterPaths`（默认 false，L5 后再打开）隔离在发布二进制之外，CI 构建两种变体。
这样在真机测试暂缓期间，master 仍可发布。

| 步骤 | 内容 | 原生风险 | L5 前可发布 | 与阶段 3 的关系 |
|---|---|---|---|---|
| A | 删除 WorkerProtocol 项目、夹具和测试；删除 `offline-checks.yml` 中对应两行；更新 slnx、文档和必需文件清单 | 无 | 是 | 在第 E 步移植规则和测试之后（D7） |
| B | 新建 `TiaMcp.Adapters.Contracts`：原样移动 DTO，增加错误类型和黄金 JSON 测试；适配器源码链接的公开 `TiaMcp.Versioning.TiaVersionCatalog` 改为 internal | 无（各版本织入清单不变） | 是 | 独立 |
| C | `build/TiaFeatures.props`，并用评估测试证明各项目 DefineConstants 不变 | 无（IL 相同） | 是 | 只动引擎 props |
| D（P4-02） | F 移到 `Native/` 与 `Policy/`，扩展面以委托实现；更新 `Adapter.Sources.props`、`AdapterSourceClosureTests`、5 个假 SDK 测试项目和 `Test-WorkerIsolation.ps1` | 无 | 是 | 独立 |
| E（P4-02） | PlcWorker、LegacyHost、TransportFixture 和 `Test-FoundationTransport.py` 改用 WorkerChannel + 协议 2 | 低（解析改变，原生分派不变） | 八版 PublicAPI 构建和离线冒烟后仍须按真机台账逐 Foundation 版本验收 | P4-E1 已统一 net48；P4-E2 信封先用 STJ，DTO codec 留给 P2-04 |
| F（P4-03） | Studio 桥接进程和客户端改用通道；方法名不变 | 无 | 是 | 独立 |
| G（P4-03） | Studio `ITiaSession` 在 `TiaOpenness.Core` 中基于扩展面重新实现；桥接进程加载织入的 `TiaMcp.Adapter.<key>`；删除 `StudioOpenness.V*` | 高（替换连接和遍历代码，新增织入） | 否（构建开关） | 独立 |
| H（P4-04） | 引擎引用 `Adapter.20/21`，打包并校验 DLL；日志输出接口；`EngineSurface` 也搜索适配器 | 无（尚无调用） | 是 | P2-04 日志合并及阶段 3 第 3–4 步之后 |
| I（P4-04） | 引擎各领域改用共享原语，保留引擎专有选项（顺序见下） | 高 | 否（构建开关） | 见下 |
| J | L5 之后：去掉开关和旧路径；列出阶段 6 的语义统一候选 | — | — | — |

第 I 步的顺序：VCI（与 Studio 共用新版扩展面）在阶段 3 第 8 步之后；监控表和工艺对象在第 9 步之后；硬件目录和添加设备在
第 11 步之后；块、文档、外部源、编译在第 13 步之后；会话连接/打开/保存/关闭在第 15 步之后；PLC 解析在第 17 步（G9）之后。

A–G 不触及引擎路径（C 只改引擎 props），可以与阶段 3 并行。

## 待维护者决定

- **D1 阶段 4 中 G2 的含义**：每个版本一个原生程序集、一套契约和协议，而不是一种行为。同名工具目前行为相反
  （`AddDeviceWithFallback`、`ImportBlock`/`ExportBlock` 覆盖、打开时升级），统一它们是破坏性变更，放到阶段 6。
- **D2 只维护 master 与 L5 暂缓**：使用构建开关（建议），或阶段 4 期间冻结发布。
- **D3 线程**：引擎保持 MTA、worker 保持 STA；有 L5 证据后再考虑统一。
- **D4 worker 框架**：所有 worker 和适配器是否改为 net48？Studio 已对所有版本要求 4.8。改后只需一个 JSON 库（STJ）。
  维护者已决定（2026-10-03）：确认原版 API 支持 4.8 后改为 net48。核对结果：V14 SP1、V15.1、V16 的
  `Siemens.Engineering.dll` 都以 .NET Framework 4.6.1 为目标、运行时为 CLR v4.0.30319（V17 起以 4.8 为目标）；.NET
  Framework 4.x 是原位升级，三者都能在本机 4.8.1 运行时中完整加载（导出类型 348/606/1198 个，`TiaPortal.GetProcesses`
  可解析）；西门子说明 V16 环境同时提供 V14 SP1、V15、V15.1 的 Openness DLL，旧应用可以不加修改地运行；Studio 桥接进程
  已对这三个版本按 net48 构建，README 也已要求 4.8。尚缺真机验证，作为第 E 步发布前的 L5 项目（V14 SP1–V16 各连接一次真实 TIA）。
- **D5 合并 worker 可执行文件**：Studio 桥接进程和 PlcWorker 是否合并为每版本一个 worker（`--profile studio|foundation`）？
  这会改变 Studio 的 `bridge/adapters/v<key>` 部署结构。
- **D6 Studio 日志**：织入后的 Studio 适配器会在 `%LOCALAPPDATA%\TiaMcp\diagnostics` 写日志，默认开启还是可选？
- **D7 批准删除 WorkerProtocol**（约 6,600 个断言）。维护者已批准（2026-10-03）。顺序调整为先在第 E 步把启动 nonce、
  绑定纪元、拒绝重放、只读失败类别和迟到进度失效等规则连同测试移植到协议 2，再执行第 A 步删除。

## 风险

- **R1 重复的公开类型**：适配器源码链接的 `TiaVersionCatalog`/`TiaVersionDescriptor` 在 `TiaMcp.Logic` 中也是公开的；引擎同时引用
  两者会出现 CS0433。第 B 步解决。
- **R2 同一进程两份日志**：引擎和适配器各有静态 `InvocationJournal`，进程内会产生两份互不关联的日志。前提是 P2-04 合并日志分支。
- **R3 引擎构建引用**：引擎通过 NuGet Openness 包的 targets 解析西门子引用（回退到注册表或包），适配器要求显式 PublicAPI 目录和
  精确身份核对。引擎构建必须固定同一目录并增加同样的核对。
- **R4 宏含义重叠**：`PLC_SAFETY` 同时选择 `ProjectBase`/LocalSession API，后续单独拆出一个特性宏。
- **R5 路径换算**：引擎 `softwarePath`、Foundation 转义路径和 Studio 设备名之间的换算本身就是行为变化，放在 G9 之后。
- **R6 测试范围**：编译、假 SDK 和传输测试不能证明原生语义，第 G、I 步完全依赖 L5。

## 关键文件

- `tools/tiaportal-mcp/src/TiaMcp.Adapters/build/Adapter.Sources.props`
- `tools/tiaportal-mcp/src/TiaMcp.Adapters/Native/Session/PlcFoundationEngine.cs`
- `tools/tiaportal-mcp/src/TiaMcpServer.PlcWorker/Program.cs`
- `tools/tiaportal-mcp/src/TiaMcpServer.LegacyHost/WorkerClient.cs`
- `tools/tia-openness-studio/src/TiaOpenness.Openness/OpennessSession.cs`
