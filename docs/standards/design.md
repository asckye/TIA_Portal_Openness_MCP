# 设计规范

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本文是项目唯一的设计规范，规定运行架构、运行时布局、V4 工具协议、worker 协议、MCP 工具设计、版本差异处理和生成框架的现行规则，并登记仍未关闭的协议偏差；它取代已归档的[设计规范索引](../archive/development/design-spec/README.md)、[4.0 架构概览](../archive/development/design-spec/architecture.md)、[V4 工具协议规范](../archive/development/design-spec/v4-protocol.md)、[worker 通道协议 2](../archive/development/design-spec/worker-protocol.md)、[V4 协议偏差清单](../archive/development/design-spec/deviations/protocol.md)、[统一版本框架](../archive/development/unified-version-framework.md)、[运行时布局](../archive/development/runtime-layout.md)、[响应与异常设计](../archive/development/response-and-errors.md)，以及本文件原先的 MCP 工具设计规范和 `runtime/README.md` 的运行输出说明。

[规范目录](README.md) · [《工程开发规范》2.0](https://github.com/asckye/engineering-standards/tree/v2.0) · [架构决策记录](decisions/README.md) · [安全标准](security.md) · [开发规范](development.md)

## 1 范围与依据

本文对新代码、新工具和新文档是规范性的；MUST、SHOULD、MAY 按[文档规范](documentation.md)第 0 节解释。现有实现与本文不一致之处登记在第 9 节，不在代码中散布抑制。

| 领域 | 基线 | 本文章节 |
|---|---|---|
| MCP 服务 | [《工程开发规范》1.1 领域 A](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md)；[MCP 规范](https://modelcontextprotocol.io/specification/2025-06-18)为主（4.0 协商 2025-06-18，现行修订为 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)）；[Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md) 为辅，只用于错误、分页、长操作和版本 | 第 4、6 节 |
| 工程软件自动化 | [《工程开发规范》1.1 领域 C](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/c-vendor-api.md) | 第 2、5、7 节 |
| 桌面应用 | [《工程开发规范》2.0 桌面与本机软件](https://github.com/asckye/engineering-standards/blob/v2.0/desktop/README.md#pd)；[工作台界面规范](ui.md) | 第 2.5 节 |
| 基线的选用 | [ADR 0015](decisions/0015-microsoft-design-spec-baseline.md) | 全文 |

不在本文的内容：C# 代码、新工具的开发步骤与测试见[开发规范](development.md)，安全措施 SEC-nn 见[安全标准](security.md)，界面规则见[工作台界面规范](ui.md)，验证与发布步骤见[验证分层](../manuals/validation.md)和[发布流程](../manuals/release.md)。《工程开发规范》1.1 的规则编号与第 9 节的偏差编号（`MCP-nn`、`API-nn`）是两套编号；本文引用 1.1 的规则时写“1.1 MCP-09”“1.1 VND-02”，采纳 `mcp/` 草案前不改用新编号（见[采用说明](README.md#mcp-草案待采纳)）。

规则冲突时从上到下取上位：

1. 外部契约与安全不变量：已冻结的 V4 契约（`manifest/contracts/v4/baseline/*.json`）、MCP 线上协议、worker 协议 2 的线格式、Siemens Openness 的调用顺序、参数与线程归属、文件格式与 manifest。不得为满足命名或风格改变它们。
2. 仓库规则：[CLAUDE.md](../../CLAUDE.md)、[AGENTS.md](../../AGENTS.md) 及它们要求遵守的流程文档。
3. 本文与 [ADR](decisions/README.md)；两者冲突时以较新的 ADR 为准。
4. 《工程开发规范》、MCP 规范和 Microsoft 来源。
5. 所在文件的既有风格：只在偏差处理完成前作为过渡，新文件必须遵守本文。

新工作需要长期偏离 MUST 规则时，先在第 9 节登记再合并；偏差编号不重复使用，规则的严重性只收紧不放宽。改变结构、契约、安全或数据完整性边界、发布规则或外部基线的决定写 ADR（[文档规范](documentation.md)第 14 节）。

## 2 架构

### 2.1 组成与总则

| 组件 | 程序 | 框架 | 职责 |
|---|---|---|---|
| 包根启动器 | `TiaOpenness.exe` | .NET Framework（csc 编译） | 只启动 `runtime/studio/TiaOpenness.exe` |
| 工作台 | `runtime/studio/TiaOpenness.exe` | net10 WPF | MCP 与客户端配置、项目页、AI 调用、审批、审计、环境检查、版本控制、设置 |
| MCP 宿主 | `runtime/v<发布键>/TiaMcp.FoundationHost.exe` 与 `release-key.txt` | net10，随包 .NET 10 | MCP 传输、工具目录、参数校验、预检、审批、审计、调用日志、Openness 通道和宿主本地工具 |
| PLC worker | `runtime/v<发布键>/worker/TiaMcp.PlcWorker.<发布键>.exe` | net48 | 每个 MCP 会话一个，是唯一接触 Siemens 对象的进程 |
| Foundation 适配器 | `worker/TiaMcp.Adapter.<发布键>.dll` | net48 | 同一份源码按各版精确 PublicAPI 编译八次 |
| Studio 桥接 | `runtime/studio/bridge/` | net48 | 迁移期遗留，只供 V16–V19 版本控制页只读 VCI（第 2.7 节） |
| 更新器 | `runtime/tools/TiaMcp.Updater.exe` | net48 | 交付包更新与回滚（第 3.5 节） |
| 写入防护钩子 | `runtime/tools/TiaMcp.WriteGuard.exe` | net10 | Claude Code 插件的客户端侧写入钩子（SEC-13） |

| 规则 | 依据 |
|---|---|
| MUST：八个发布键使用同一个宿主程序、同一条调用路径和同一种 worker；除注册的工具集合和官方 API 要求的差异外，不按版本区分逻辑、默认值、文字或包装 | [ADR 0005](decisions/0005-v20-v21-on-foundation-host.md)、[ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| MUST：每个 MCP 会话一个 worker：stdio 是一个会话一个 worker，HTTP 的 N 个存活会话对应 N 个 worker | [ADR 0007](decisions/0007-one-worker-per-mcp-session.md) |
| MUST：只有 worker 接触 Siemens 对象；宿主与 worker 之间只用 worker 协议 2（第 5 节）；Siemens 对象不跨请求持有、不跨进程传递 | 1.1 VND-01 |
| MUST：worker 在所属线程上串行原生调用，V14 SP1–V19 为 STA，V20/V21 为 MTA（`src/Adapters/Policy/PlcLifecyclePolicy.cs`）；不得通过替换某个工具的名称或线程原语改变一个版本的所属单元 | 1.1 VND-02 |
| MUST：每个会话一条 Openness 通道，原生调用在会话内串行；不调用 TIA 的工具登记在 `ToolTaxonomy.WithoutTia`，走并发的本地通道，不等待正在运行的原生调用 | `src/Shared/Host/ToolDispatchLanes.cs` |
| MUST：一个 TIA 进程同时只租给一个 MCP 会话（第 2.4 节） | [ADR 0011](decisions/0011-tia-process-lease-on-all-releases.md) |
| MUST：MCP 写调用默认在工作台审批，先预检再审批，批准后复核再执行（第 4.10 节） | [ADR 0003](decisions/0003-workbench-approval-for-mcp-writes.md)、[ADR 0004](decisions/0004-precheck-before-approval.md)、SEC-01 |
| MUST：worker 调用按类超时；超时、卡死、退出或坏帧使 worker 代次失效，从不重放（第 5 节） | [ADR 0008](decisions/0008-worker-timeouts-and-faults.md) |
| MUST：会话状态只有一份：宿主经 `SharedSessionState` 投影 worker 缓存的绑定身份，宿主工具、生成规划、工作台控制和操作端快照共用，不另存项目绑定；worker 代次和重置要求仍由 worker 与管线持有 | [ADR 0029](decisions/0029-authoritative-session-state.md) |
| MUST：工具只在官方 API 至少支持它的一个动作时注册（第 7 节） | [ADR 0012](decisions/0012-old-release-tool-registration.md)、[ADR 0014](decisions/0014-tool-counts-follow-official-api.md) |

### 2.2 进程与会话

- HTTP 宿主不设会话数上限，部署时按 worker 内存和 TIA 实例数限制客户端并发。`/mcp/health` 不鉴权，返回 `fileVersion`、发布键和档案名；`/mcp/ready` 与 MCP 调用使用同一 Bearer 密钥，只证明宿主已就绪（SEC-05）。
- 每个 worker 绑定宿主的 Windows job object（`WorkerJob`），宿主退出或被终止时 worker 随之结束；TIA 进程不属于 job，不受宿主退出影响。
- 会话结束、stdio EOF、Ctrl+C、控制台关闭和 HTTP 宿主停止都关闭 worker 通道。空闲 worker 最多等待 10 秒，在所属线程上非拥有式分离并把租约写为 `RELEASED`，不保存也不关闭项目；只有请求仍在进行或等待超时才结束 worker。
- `DisconnectPortal` 只结束本会话的原生连接，`RestartOpennessWorker` 只重启本会话的 worker；新的 HTTP 会话从未绑定状态开始。
- 断开、故障、重启、审批与故障键、暂存批次、预演计划和导出句柄都按会话隔离；导出内容的进程内容量（32 个句柄、8,000,000 个字符）全进程共用，避免并发会话扩大缓存。
- 宿主在 `<数据根>/run/hosts/<PID>-<启动 ticks>.json` 原子写入登记记录（发布键、宿主版本、UTC 启动时间、传输、HTTP 前缀、操作端管道名、会话快照），退出时删除。工作台据此列出同一数据根的存活宿主（包括无会话的宿主），按 PID 与启动时间判断存活；登记只是提示，是否忙碌由宿主在准入边界判定。
- 工作台停止宿主前再次核对 PID、启动时间和映像路径，并由人确认结束全部会话。HTTP 重启先核对已保存的端点和受保护密钥，再启动当前包中的宿主；stdio 宿主的输入输出流由 MCP 客户端持有，只能在该客户端中重启。

### 2.3 调用的准入顺序

每次工具调用按以下顺序经过宿主，任一步拒绝都返回 `rejected-before-operation`，不进入后续步骤：

1. 进入会话上下文；`ActorScope` 由传输决定（MCP 客户端为 `mcp`，操作端为 `workbench`），不读取任何参数；写调用日志。
2. 按工具 `inputSchema` 绑定并校验参数；直接调用、`CallTool` 和批次共用同一准入（`BindV4Call`），在 SDK 绑定之前执行。
3. 检查版本可用性、生成写栅栏（写调用）、会话锁、TIA 就绪和连接前提。
4. 写调用：审计 `request`，用同一参数在只读作用域运行一次预览（预检），再等待工作台审批（第 4.10 节）。审批时限在预检结束、开始等待时才计算。
5. 获取本会话的 Openness 通道。写调用在通道内复核写栅栏、会话锁、批次身份、参数摘要和调用方输入文件，然后写审计 `start`。
6. 经 worker 协议派发 `adapter.<操作>`（请求 id、绑定纪元）。worker 在派发前把租约写为 `BUSY` 并落盘，织入的原生调用诊断先落盘 BEFORE 行，再调用 Openness；结果已知后租约回到 `IDLE`。
7. 把回复映射为 V4 信封；结果未知且满足锁定条件时锁定会话（第 4.11 节）；按错误码附 `RECOVERY_GUIDANCE`；写调用发送审批结果通知并写审计 `end`。

### 2.4 生命周期

| 会话状态 | 进入 | 离开 |
|---|---|---|
| 已建立、未连接 | MCP 初始化；worker 在第一次需要原生调用时启动 | `ConnectPortal` 选定进程并取得租约 |
| 已连接 TIA 进程 | `ConnectPortal` | `AttachOpenProject`、`OpenProject` 或 `CreateProject` 绑定项目；`DisconnectPortal` 回到未连接 |
| 已绑定项目 | 绑定成功 | `CloseProject` 回到已连接；会话结束时非拥有式分离，不保存不关闭 |
| 会话锁定 | 越过原生调用的写入结果未知（第 4.11 节） | 检查 TIA 后 `DisconnectPortal`，再开新的 MCP 会话 |
| worker 代次故障 | 超时、worker 退出或卡死、坏帧、TIA 进程丢失 | `RestartOpennessWorker` 或 `DisconnectPortal`，之后重新绑定 |

- 没有连接 TIA 时，需要 TIA 的工具在派发前返回带 `ConnectPortal` 提示的 `PRECONDITION_FAILED`（`connect-portal`），会话保持可用，不锁会话。
- 锁定或故障后的恢复对八个版本相同：先检查 TIA，再调用 `DisconnectPortal`。空闲 worker 干净分离并释放租约；忙碌、超时或丢失的 worker 被结束，回复说明需要重启 TIA。然后新建 MCP 会话并重新绑定，不重放失败的请求。
- `RestartOpennessWorker` 先让空闲 worker 干净分离，再启动新代次；故障 worker 保留租约并报告 `requiresTiaRestart`。
- 绑定要求显式选定、当前枚举到的进程 ID，以及准确的项目名称和绝对路径。`CloseProject` 要求本会话拥有且未修改的项目，不静默保存。`DisconnectPortal` 是终结操作：不保存、不关闭、不结束 TIA、不自动重连。

#### TIA 进程租约

八版 PLC worker 在附着前、在所属线程上取得租约（`src/Logic/Siemens/PortalProcessLease.cs`）。租约文件为 `<数据根>/leases/<PID>-<进程启动 UTC ticks>.lease`，独占文件句柄覆盖整个连接期，内容是一行 UTF-8 状态：

| 状态 | 含义 | 另一会话附着时 |
|---|---|---|
| `IDLE` | 已附着且空闲 | 持有者存活时 `LeaseReserved`；持有者已结束时可接管，`ConnectPortal` 返回 `data.previousOwnerEndedIdle=true` |
| `BUSY` | 可能调用原生 API 的请求已派发，派发前写入并持久刷新 | `LeaseNotReleased` |
| `UNCERTAIN` | 结果未知 | `LeaseNotReleased` |
| `RELEASED` | 非拥有式分离后释放 | 可接管 |
| `ACTIVE` | 3.x 遗留状态 | `LeaseNotReleased` |

- MUST NOT 删除租约文件来绕过保护；`LeaseNotReleased` 时先检查原生调用的结果，再重启该 TIA 实例。
- 强制结束空闲 worker 保留 `IDLE`，下次可以接管；中断原生请求保留 `BUSY`，结果未知退出保留 `UNCERTAIN`。正常替换安装包不需要重启 TIA。
- 租约只在附着前后加文件锁，不增加 Siemens 调用。

### 2.5 工作台与本机管道

工作台与宿主之间只有三条本机命名管道，各自独立，协议、DTO 和帧读写器不共用：

| 管道 | 名称 | 服务端 | 承载 | 帧与时限 |
|---|---|---|---|---|
| 审批 | `TiaMcp.Approval.v1.<sha256>`，摘要绑定当前用户 SID 与 `approval.settings` 的绝对路径 | 工作台 | 审批请求、决定和结果通知（第 4.10 节） | 4 字节小端长度加 UTF-8 JSON，单帧 ≤ 1 MiB；一次连接一个请求一个决定 |
| 工作台控制 | `TiaMcp.Workbench.v1.<sha256>`，作用域同审批管道，另加 `workbench-control` 域标记 | 工作台 | 显示、读取、预填；8 个宿主工具 | 同上，单帧 ≤ 256 KiB；宿主连接超时 250 ms；读取 2 秒、界面操作 5 秒截止 |
| 操作端 | `TiaMcp.Operator.v1.<sha256>`，摘要绑定 SID、数据根、宿主 PID 与启动时间 | 宿主 | 工作台以“人”的身份附着 MCP 会话并调用工具 | 握手后在同一管道上运行 MCP JSON-RPC |

三条管道都使用只授权当前用户的受保护 DACL、拒绝远程客户端、双方核对对端 SID；控制管道和操作端还核对对端是同一包中的工作台映像，操作端的映像核对是强制的。信任边界是“同一 Windows 用户”：管道防止 MCP 客户端经工具参数、元数据或批次越权（自批写调用、关闭审批），不防同一用户下能执行代码的本机进程（SEC-02、SEC-03）。方案比较、实施步骤和红线的测试手段见已归档的[工作台控制与共享会话设计](../archive/development/design/workbench-control-and-shared-session.md)。

#### 工作台控制通道

- 8 个工具属于 `Workbench` 域，八版相同，每版的 full 与 lite 档都包含：`ShowWorkbenchPage`、`ShowWorkbenchBlock`、`ShowWorkbenchCall`、`ShowWorkbenchLadder`、`ShowWorkbenchAtlas`、`PrefillWorkbenchForm` 为 `UI`，`GetWorkbenchState`、`GetWorkbenchSelection` 为 `READ`。全部登记 `WithoutTia`，可在 `--offline` 宿主上调用。
- 控制调用不进入 worker、Openness 通道、审批或审计链，只写调用日志和工作台控制日志；结果映射为 V4 信封，`behaviorPolicy=not-applicable`，超时和失败都不锁会话。
- 协议操作是封闭枚举（`display.page`、`display.block`、`display.call`、`display.ladder`、`display.atlas`、`read.state`、`read.selection`、`prefill.form`），没有审批、审批开关、服务启停或项目执行的消息；工作台端处理器只能拿到窄接口 `IWorkbenchControlSurface`。
- 可显示的页面只有 `overview`、`blocks`、`versionControl`、`calls`、`audit`、`environment`、`log`，不含 MCP 配置页、设置和审批抽屉。预填表单是工作台源码中的封闭注册表（`inspectionRules`、`blockFilter`、`blockSelection`、`connectProject`、`exportBlocks`、`importBlocks`），每个表单的执行去向只能是“只影响界面”或“经 MCP 工具”；预填成功后仍等待人保留或清除。
- 人优先：工作台“允许 AI 控制界面”关闭、有模态或抽屉打开、最近 1.5 秒内有人的输入、页面忙或另一来源有待确认的预填时，显示与预填被拒绝，读取不受影响。控制引起的切页后 500 毫秒内忽略执行类与审批类按钮的点击。该开关默认开启，只能由人在设置中更改，工具只回显状态。
- `ShowWorkbenchCall` 拒绝指向待批请求的调用记录；`ShowWorkbenchLadder` 与 `ShowWorkbenchAtlas` 只接受本会话一次成功渲染的 `renderRequestId`，产物的项目、软件和块身份须来自同会话的成功导出，哈希一致；工作台打开前再次核对本机普通 HTML 文件与哈希。
- 错误映射：工作台未运行或数据根不同为 `RESOURCE_UNAVAILABLE`；管道属主或映像不符为 `ACCESS_DENIED`（`target=pipe-owner`）；截止超时为 `TIMEOUT`（`workbench-read` 或 `workbench-ui`）；断连、坏帧或响应不匹配为 `INTERNAL_ERROR` 加诊断编号，不回显异常文本。工具从不启动工作台，身份失败时不降级、不重试。

#### 操作端与共享会话

- 项目页默认附着 AI 正在使用的 MCP 会话：同一工具对象、同一 worker、同一 Openness 通道、审批和审计。没有 AI 会话时，只有工作台启动的宿主（`--transport operator --operator-owner <工作台 PID>`）允许 `create` 工作台自有会话，AI 不能加入；AI 客户端拉起的宿主拒绝 `create`。
- 握手首帧的 `action` 为 `list`、`attach`、`create`、`native-cross-references` 或 `shutdown`。`native-cross-references` 只修改所选会话的人工原生交叉引用许可，宿主只把该会话的随机 HMAC 密钥传给它的 worker；它不是 MCP 工具，也不经审批。`shutdown` 在有调用进行或占用通道时返回 `HOST_BUSY`，否则封闭新的准入后走宿主停止路径；工作台按 20 秒宿主退出预算等待（含 worker 的 10 秒宽限），超时后须再经人确认才强制结束。
- 会话规则对所有参与方相同：Openness 通道串行，结果未知锁定会话，`DisconnectPortal` 与 `RestartOpennessWorker` 影响整个会话；`DisconnectPortal` 的回执带 `endedBy`。面板发起的写同样经预检和审批卡，没有第二条批准路径。
- 审计行、调用日志和审批请求都带 `actor`（`mcp` 或 `workbench`）。附着后宿主从缓存推送 `notifications/tiamcp/sessionState`，不调用 TIA。
- `GetWorkbenchState.data.session` 是请求方的 MCP 会话，`selectedSession` 是工作台当前的选择，两者可以不同；读取不改变人的选择。

### 2.6 代码位置

| 关注点 | 主要位置 |
|---|---|
| 宿主入口、选项、传输 | `src/FoundationHost/Program.cs`、`HostOptions.cs`、`EngineReleaseHost.cs`（`SharedReleaseHost`） |
| 会话对象、人与 AI 归属、会话视图 | `src/FoundationHost/HostSession.cs`、`SharedSessionState.cs`、`src/Shared/ActorScope.cs` |
| 工具注册与准入、结果映射 | `src/FoundationHost/FoundationPassiveDiagnostics.cs`（`LegacyHostToolRegistry`）、`FoundationV4Tool.cs`、`FoundationV4Result.cs`、`PortedToolContract.cs`、`src/Logic/V4/HostBehavior.cs`、`src/Shared/SessionBehavior.cs` |
| 桥接、批次、导出句柄 | `src/EngineHost/EngineHostPipeline.cs`、`src/Shared/Host/McpServer.{ToolBridge,Batch,Exports}.cs`（`Engine` 前缀是历史名称） |
| Openness 通道 | `src/Shared/Host/ToolDispatchLanes.cs` |
| worker 客户端与 job object | `src/FoundationHost/WorkerClient.cs`、`WorkerJob.cs` |
| worker 协议 | `src/WorkerChannel/` |
| PLC worker 与租约 | `src/PlcWorker/Program.cs`、`FoundationWorkerDispatcher.cs`、`FoundationProcessLease.cs`、`src/Logic/Siemens/PortalProcessLease.cs` |
| 族与发布键注册表 | `src/Adapters.Contracts/PortedFamilies.cs` |
| Foundation 适配器 | `src/Adapters/Native/`、`src/Adapters/Policy/`、`src/Adapters/build/Adapter.Sources.props` |
| 版本目录与编译特性 | `src/Logic/Siemens/TiaVersionCatalog.cs`、`src/Shared/TiaFeatures.props`、`src/Shared/TiaPublicApi.props` |
| V4 信封、错误、计划 | `src/Logic/V4/` |
| 工具分类、执行位置、档案、用法 | `src/Logic/ModelContextProtocol/ToolTaxonomy.cs`、`ToolMetadata.cs`、`ToolExecution.cs`、`ToolProfiles.resx`；`src/Shared/ToolUsageCatalog.cs` |
| 安装布局与数据目录 | `src/Shared/BundleLayout.cs`、`src/Shared/DataLocations.cs` |
| 审批 | `src/Shared/ApprovalPipe.cs`、`ApprovalProtocol.cs`、`ApprovalSettings.cs`；工作台 `src/Studio/Gui/Services/ApprovalService.cs` |
| 审计链与调用日志 | `src/Shared/AuditLog.cs`、`AuditInvocation.cs`、`InvocationJournal.cs`、`JournalRetention.cs`、`build-tools/native-call-weaver` |
| 工作台控制与操作端 | `src/Shared/WorkbenchControl*.cs`、`OperatorPipe.cs`、`OperatorProtocol.cs`、`LocalPipeSecurity.cs`；`src/FoundationHost/WorkbenchControl*.cs`、`OperatorEndpoint.cs`、`HostRegistration.cs`；`src/Studio/Gui/Control/` |
| 生成框架 | `src/Logic/Generation/`、`src/Shared/Host/Generation*.cs`、`reference/schemas/standards/v1/`、`templates/standards/` |
| 导入暂存 | `src/Logic/ModelContextProtocol/ImportStagingStore.cs` |
| 运行时通道（S7、OPC UA、Web API、Open Pipe） | `src/OnlineChannels/` |

### 2.7 过渡状态与已知缺口

| 项目 | 现状 | 计划 |
|---|---|---|
| 引擎残留 | 引擎程序已删除，宿主以退出码 64 拒绝 `--engine-*`；Q2/Q3 已删除 `src/Engine` 残余源码、宿主引擎客户端和死条件分支；worker 协议的 `ChannelProfile.Engine` 和历史目录名 `src/EngineHost` 仍在 | 后续精简（[开发计划](../plan/plan.md)） |
| Studio 桥接与 Studio 适配器 | 工作台项目与版本控制页使用 MCP 操作端；保留的桥接仍使用 WorkerChannel 的 Studio profile，旧桥接与适配器代码待清理 | P8-21f |
| 八版对外呈现 | 八版默认精简目录、共用服务器说明，公开档案名统一为 `plc-foundation`；工具集合按各版能力提供 | P8-04n1 已合并（N1-PORT）；行为路由统一仍见 UNIFY-1 |
| 一项功能一个实现 | V21 上部分重复的旧工具失败 | DES-2 |
| 工作台控制端到端 | 真实宿主加真实工作台的验收未完成 | P8-20e |
| Studio 可选共享适配器路径 | `TiaSharedAdapterPaths` 仍由 Studio 工程使用，默认 `false`，`prepare` 显式设为 `false`；它不再选择引擎路径 | 真机验收、P8-21f |
| Foundation 绑定快照 | 观测原语已有，worker 生命周期未接入，附着仍只凭 PID（第 7 节） | 未排期 |
| 真机验收 | 八版 D1 行为族与已移植族为 `NOT RUN`；预发布由测试者使用，我们按真机核对清单做 VM 回归 | [ADR 0020](decisions/0020-prereleases-tested-by-users.md)、[真机台账](../reference/real-machine-ledger.md) |

## 3 运行时布局

### 3.1 构建输出与交付包

`dotnet run --project build-tools/release -- prepare -Version <版本> -BuildOnly -PublicApiRoot <SDK-root>` 产生以下被忽略的本地输出：

| 发布 | 可执行文件 | 组成 |
|---|---|---|
| V14 SP1、V15.1、V16–V19 | `runtime/v<发布键>/TiaMcp.FoundationHost.exe` | FoundationHost 与 PLC worker；lite 与 full 两档目录 |
| V20、V21 | `runtime/v<发布键>/TiaMcp.FoundationHost.exe` | FoundationHost 与 PLC worker；lite 与 full 两档目录 |
| 工作台 | 包根 `TiaOpenness.exe` 启动 `runtime/studio/TiaOpenness.exe` | 一个窗口中的项目操作与 MCP 配置；共享 MCP 会话 |

```text
runtime/v<发布键>/        （八个发布键相同）
  TiaMcp.FoundationHost.exe + .dll/.deps.json/.runtimeconfig.json
  release-key.txt
  net10 依赖
  worker/
    TiaMcp.PlcWorker.<发布键>.exe + .exe.config
    TiaMcp.Adapter.<发布键>.dll、TiaMcp.Adapters.Contracts.dll、TiaMcp.WorkerChannel.dll 与 net48 依赖
```

- 每个运行目录的依赖和 `worker/` 必须放在一起；net10 与 net48 依赖分目录，不能混放。V14 SP1 与 V15.1 保留准确的发布键；原版 V14、V15 不在支持范围。
- `runtime/dotnet` 是随包的 .NET 10 运行时（基础、ASP.NET Core 与 Windows Desktop），FoundationHost 和工作台优先加载；PLC worker 需要 .NET Framework 4.8。只运行 AI 的电脑也保留完整交付包。对应版本的 Siemens PublicAPI 程序集来自已安装的 TIA Portal，不随包分发。
- 宿主用 EXE 旁的 `release-key.txt` 选择并核对发布键（`--release-key <发布键>` 须与之一致），从 `BundleLayout` 定位 `worker/TiaMcp.PlcWorker.<发布键>.exe`；显式 `--worker-exe` 选择开发 worker。宿主自己生成工具目录，交付包不含引擎程序与 `tool-catalog.json`。
- 宿主支持 stdio 与带鉴权的 HTTP、`--catalog`、`--public-api`、`--release-key` 和 `--profile`（默认 `lite`，也可用 `TIA_MCP_PROFILE`；八版均有 lite 档，展示名称统一为 `plc-foundation`）。`--offline` 显式禁用原生 worker 调用；正常启动不附着 TIA，worker 在第一次调用原生工具时启动。
- `CallTool` 是 MCP 工具，不是命令行动词。八版宿主的命令行动词为 `doctor`、`audit verify`、`schema`、`version`、`config`、`install-plc-tools` 和 `help`。
- 审批请求、审计和调用日志的 host 标记沿用原值：`FoundationV4Tool` 执行的工具为 `foundation`，`src/EngineHost` 的宿主流程（桥接、批次及其审批路径）为 `engine`，八版相同（`src/EngineHost/ReleaseCatalogData.cs`）。

工具数量由以下目录块生成。

<!-- generated:tool-counts:begin -->
由 `generate-tool-counts` 从 `manifest/version-tools.json` 和 `ToolProfiles.resx` 生成。以运行时 `tools/list` 为准。

| TIA 版本 | 注册工具数 | lite 档 |
|---|---:|---:|
| V14 SP1 | 280 | 89 |
| V15.1 | 320 | 92 |
| V16 | 349 | 93 |
| V17 | 372 | 93 |
| V18 | 398 | 95 |
| V19 | 474 | 95 |
| V20 | 497 | 98 |
| V21 | 512 | 101 |
<!-- generated:tool-counts:end -->

交付包由 `prepare` 按 [`delivery-files.json`](../../scripts/operations/delivery-files.json) 过滤 Git 文件集，再加入清单中的运行文件，排除 `runtime/verification/`；开发源码、检查脚本和开发文档不分发，不能只复制 EXE。文件清单 `manifest/multi-version-build.json` 只在暂存目录生成，不入 Git；工具范围见[版本工具矩阵](../reference/version-tools.md)。已发布的归档保留各自记录的范围。

以下交付路径已冻结，不能作为仓库目录整理的一部分移动：

| 路径 | 固定原因 |
|---|---|
| `scripts/operations/delivery-files.json` | 已安装的 Updater 在 `ApplyRelease` 中从新包读取交付规则 |
| `manifest/delivery.json` | 已安装的 Updater、工作台和 `UpdateCheck` 读取版本信息 |
| `runtime/tools/TiaMcp.Updater.exe`、`.exe.config` | 更新器自身路径与交付规则的 `requiredFiles` |
| `runtime/v<发布键>/TiaMcp.FoundationHost.exe` | 用户的 MCP 客户端配置保存此绝对路径 |
| `runtime/tools/TiaMcp.WriteGuard.exe`、`runtime/tools/TiaMcp.PluginLauncher.exe` | `hooks/hooks.json` 与 `.claude-plugin/plugin.json` 通过 `${CLAUDE_PLUGIN_ROOT}` 引用 |
| `hooks/hooks.json` | 插件根自动加载；移动后，旧清单无法清理的副本可能重复加载 |
| `TiaOpenness.exe` | 包根启动器与用户快捷方式 |
| `.claude-plugin/` | Claude Code 插件约定 |

### 3.2 包根与资源定位

[`BundleLayout.cs`](../../src/Shared/BundleLayout.cs) 以 `manifest/package-manifest.json` 为包根标记，维护资源相对路径和同级产品路由；宿主（含命令行）、工作台和适配器使用同一解析规则。

- 选择顺序：`--bundle-root <绝对路径>` → `TIA_MCP_BUNDLE_ROOT` → 正式安装或开发锚点；两项显式输入并存时命令行优先。相对路径或缺参数是语法错误（命令行退出 64）；目录不存在或缺根标记退出 70。显式根无效时立即报 `RESOURCE_UNAVAILABLE`，不尝试其他根，不启动工具。启动时选定的根供资源、数据目录和路由共用，并传给子进程。
- 解析不依赖当前工作目录、`.git` 或写权限，不向祖先目录查找资源，也不读取已退役的 `TIA_MCP_REPOSITORY_ROOT`；资源缺失由 `RequirePath`/`RequireResource` 报 `RESOURCE_UNAVAILABLE`，携带选定根下的预期路径。
- 同级路由（`BundleLayout.RequireEngine`，方法名沿用旧称）在安装布局使用 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`，在开发锚点使用对应的 Release 或 Debug 输出；目标缺失即拒绝，不改用当前 EXE，也不接受任意 `bin` 或 `v数字` 目录。
- 工作台用 `BundleLayout.ResolveWorkbenchRoot` 与 `RequireWorkbenchRoot` 按同一规则定位包根，缺目标版本宿主即拒绝；源码 worktree 拒绝安装更新。`CheckProductUpdate` 只把选定根下的正式 `runtime/v<发布键>` 认作安装。

| 锚点 | 基目录（相对包根；`<configuration>` 为 `Release` 或 `Debug`） |
|---|---|
| 包根 | 含 `manifest/package-manifest.json` 的目录 |
| MCP 安装输出 | `runtime/v14sp1`、`runtime/v15.1`、`runtime/v16`–`runtime/v21` |
| PLC worker 开发输出 | `src/PlcWorker/bin/<发布键>/<configuration>/net48` |
| Foundation 开发输出 | `src/FoundationHost/bin/<configuration>/net10.0` |
| 工作台安装输出 | `runtime/studio`、`runtime/studio/bridge` |
| 工作台开发输出 | `src/Studio/Gui/bin/<configuration>/net10.0-windows` 及其 `bridge` 子目录 |
| 随包独立工具与 .NET 运行时 | `runtime/tools`、`runtime/dotnet`；apphost 先找 `../dotnet`，再找 `DOTNET_ROOT` 和已安装的 .NET |

| 资源 | 包根相对路径 |
|---|---|
| 根标记、交付清单 | `manifest/package-manifest.json`、`manifest/delivery.json` |
| 指南及来源 | `reference/siemens-openness/skills`、`reference/siemens-openness/UPSTREAM.json` |
| V21 生态目录参考副本 | `reference/v21-ecosystem.json` |
| 写入防护工具 | `runtime/tools/TiaMcp.WriteGuard.exe`，由 `hooks/hooks.json` 经 `${CLAUDE_PLUGIN_ROOT}` 启动 |
| PLC Tools 与 SimaticML 桥接 | `scripts/ecosystem/plc_tools_bridge.py`、`scripts/ecosystem/simaticml_decode_bridge.py` |
| 更新器 | `runtime/tools/TiaMcp.Updater.exe` 及其 `.config` |
| 命令行模板 | `templates`，默认 HMI 模板 `templates/hmi`；`__BUNDLE__` 引用的具体文件也必须在选定根中 |

- V21 生态目录查询读取 `TiaMcp.EngineHost` 程序集中嵌入的 `TiaMcp.V21Ecosystem.json`，磁盘副本缺失不影响查询；可编辑源是 [`reference/v21-ecosystem.json`](../../reference/v21-ecosystem.json)，修改后须重建宿主。
- 伴随 Python 的解释器由 `TIA_MCP_PLC_TOOLS_PYTHON` 指定，缺省为 `%LOCALAPPDATA%\TiaMcp\ecosystem-python\Scripts\python.exe`；缺失或不可写报 `IO_FAILED` 与具体路径，不在安装目录创建环境。
- 私人报告与夹具命令行要求 `--workspace-root <已存在的绝对目录>`（独立于 `--bundle-root`）；HMI 模板要求 `--hmi-template-directory`，组件目录分析要求具体的 `--global-library-probe-json-path`。缺输入返回 `INVALID_ARGUMENT`，不猜当前目录、私人模板目录或最新报告。

### 3.3 数据根

数据根集中保存软件自身的配置、日志、审计、诊断和运行状态；用户项目和调用方指定的输出目录不在其中。

#### 软件自身的数据目录

[`DataLocations.cs`](../../src/Shared/DataLocations.cs) 在进程首次使用时确定一个数据根，并在链接该源码的程序集之间共享；之后不因环境变量或目录权限变化重新选择：

1. 非空的 `TIA_MCP_DATA_DIRECTORY`：必须是绝对路径；无效或不可写时报错，不静默回退。
2. 启动时选定包根下的 `data`：创建目录并写入、删除一个小探测文件，成功才采用。
3. 未找到包根或探测失败（例如安装在不可写的 `C:\Program Files`）时，按用途回退到下表的用户目录。

| 数据根下的目录 | 内容 | 写入者 | 无数据根时的位置 |
|---|---|---|---|
| `diagnostics` | 调用日志 `calls-*.jsonl`、原生调用诊断（先落盘再执行原生调用）、工作台控制日志 | 宿主、worker、工作台 | `%LOCALAPPDATA%\TiaMcp\diagnostics` |
| `leases` | TIA 进程租约 | worker | `%LOCALAPPDATA%\TiaMcp\instance-leases` |
| `config` | `approval.settings`、`journal-retention.settings`、`http-v<版本>.json`、`client.json` | 工作台 | `%LOCALAPPDATA%\TiaPortalMcp` |
| `ui` | `ui.settings` 语言、主题与“允许 AI 控制界面”开关 | 工作台 | `%LOCALAPPDATA%\TiaOpennessStudio` |
| `logs/<发布键>` | 宿主主日志、HMI 读取与原生导出日志 | 宿主 | `%TEMP%\TiaMcp\logs\<发布键>` |
| `logs/studio` | 工作台崩溃日志 | 工作台 | `%TEMP%\TiaMcp\logs\studio` |
| `logs/audit` | 写调用与审批的哈希链（第 3.4 节） | 宿主、工作台 | `%LOCALAPPDATA%\TiaMcp\logs\audit` |
| `run/hosts` | 宿主登记（第 2.2 节） | 宿主 | 需要数据根 |
| `generation` | 生成计划产物、运行记录、已消费声明（第 8 节） | 宿主 | 需要数据根，否则生成工具报错 |
| `worker-spills` | 超大 worker 回复的临时转存（第 5 节） | worker 写入，宿主校验后删除 | `%LOCALAPPDATA%\TiaMcp\worker-spills` |
| `reports` | 未指定输出目录时的诊断报告与图集 | 宿主、工作台 | `%TEMP%\TiaMcpReports` |
| `temp` | 临时文件、默认脚手架和模拟项目 | 宿主、工作台 | `%TEMP%` |

导入暂存在包根 `staging/<会话>/<批次>/`，按会话分目录。

- `TIA_MCP_DIAGNOSTICS_DIRECTORY` 优先于数据根决定诊断目录；`TIA_STUDIO_MOCK_STATE_ROOT` 优先决定模拟版本控制状态的位置。调用方明确给出的报告或项目目录不变。
- 首次使用数据根中的 `config` 或 `ui` 时，只从旧用户目录复制缺失的文件；已有文件不覆盖，旧文件不移动、不删除。诊断、租约、日志和临时文件不迁移。客户端自身配置、Siemens ProgramData、TIA 默认项目目录、URL ACL 与防火墙设置保持原行为。
- 日志文件名含 PID、UTC 启动时间和 GUID：宿主为 `TiaMcpServer-<PID>-<yyyyMMdd-HHmmss-fffffff UTC>-<GUID N>.log`，HMI 读取、原生导出和工作台崩溃日志分别以 `TiaMcpServer.hmi-read`、`TiaMcpServer.native-export`、`TiaOpenness.crash` 为前缀。宿主与工作台启动时按用途前缀保留最新 32 份（`PlainLogCopies`）；被打开的文件和活动进程的文件保留，不递归、不穿过重解析点、不碰其他文件。
- 日志目录不可写时按用途回退；没有可写位置报 `IO_FAILED`，诊断或审计写失败报 `DIAGNOSTIC_WRITE_FAILED`，都带目的路径，不写到可执行文件旁，也不改变工具结果。

### 3.4 审计链与调用日志

- 宿主记录分类为写操作的调用的 `request`、`start` 和 `end`，`end` 保留 V4 的 `meta.outcome`；预览按写分类记录 `request`，只读工具不记录。`CallTool` 目标和批次写项分别记录；未执行项记 `rejected-before-operation`，结果读不到或异常离开边界记 `unknown`。不记录参数、业务内容、凭据或原生对象。审批请求、决定、超时和开关变化写入同一条链，与写调用共用 `requestId`。
- 审计文件为 `audit-<首条序号，20 位补零>.jsonl`，每条是无 BOM、LF 结尾的 UTF-8 JSON 对象。字段 `approvalEnabled,event,host,index,outcome,planHash,previousHash,processId,release,requestId,tool,utc`（可选 `actor`，只在非空时写出）按字段名 ordinal 升序、无空白输出；SHA-256 对这份规范 JSON 计算，首条 `previousHash` 为 64 个零。10 MiB 轮转，轮转后的首条链接上一文件末条；审计文件不自动删除。
- 同一数据根的所有进程写入 `logs/audit` 的一条链：先以独占方式打开 `.audit.lock`，再读末条、分配序号、轮转、追加并 `Flush(true)`；竞争每 10 毫秒重试，30 秒超时报 I/O 错误。崩溃留下的不完整末行不自动修复，校验会报告它。
- `audit verify --path <绝对目录>` 输出 `chain,passed,count,breakIndex,file,reason`；未指定路径时分别校验主目录和已存在的用户回退目录。全部完整退出 0，任一链断链或读取失败退出 3。哈希链只能发现保留记录的修改、缺失和插入，不证明日志完整（SEC-04）。
- 调用日志按进程写独立文件流，默认每流 50 MiB × 16 份，工作台可在 `journal-retention.settings` 中调整为 1–1024 MiB、1–256 份，宿主在下一次写入时生效。日志行带 `actor` 与会话摘要；服务的 `Coverage` 返回保留文件中首末完整记录的时间，工作台据此显示覆盖窗口。

### 3.5 更新器

`runtime/tools/TiaMcp.Updater.exe` 面向 .NET Framework 4.8，不依赖随包运行时。启动前把自身复制到系统临时目录，以便替换根启动器、运行时文件和自身；使用 Windows 扩展路径处理长路径；操作前检查当前包的宿主、worker 和工作台进程，列出 PID 后退出，不终止它们。`-Check` 只检查版本和资产，默认调用执行更新，`-Rollback` 恢复最新备份（[配置指南](../getting-started/configuration.md#更新)）。

- 按新包的 `delivery-files.json` 接受资源；备份到 `.previous` 后，只删除旧包 `manifest/release-file-hashes.json` 记录且哈希仍匹配的退役文件，保留用户新增或改写的内容；没有旧完整记录时只从构建记录识别旧运行二进制。
- 每次更新记录新文件收据，回滚先删除备份中不存在且未被改写的新文件，再恢复原文件；只保留最近两份备份。`data`、`.previous`、`.update`、`bin-build` 不参与清理，数据目录在覆盖和回滚时保留。

### 3.6 新增运行时资源

1. 把交付资源放进受版本管理的包内路径，在 `BundleResource` 与 `ResourcePaths` 增加一项；目录资源同步 `FindResource` 的目录判断。编译生成的 EXE 与 DLL 由构建和交付清单管理。
2. 同步 `delivery-files.json` 与 C# `BundleManifestRequirements`，核对仓库检查和打包脚本的必需文件清单；清单与源码哈希由构建生成器更新，不手工改哈希。
3. 调用方复用解析器和版本目录表，需要新布局时显式扩充锚点，不增加新的仓库探测；只供单个程序集使用的固定数据可以嵌入，并保留唯一的可编辑源。
4. 补充布局测试（缺文件、显式覆盖、仓库外重定位），运行 `check-bundle-layout -SelfTest`、`check-bundle-layout`、和 `check-repository -NoBinaries`（均经 `dotnet run --project build-tools/release --`），其余按[验证分层](../manuals/validation.md)。

## 4 V4 协议

V4 协议是全部 MCP 工具结果的形状，也被 CLI、批次、worker 通道、审批和审计复用。证据来源是 `src/Logic/V4/`（信封、错误、校验、编解码、分页、计划）、`src/Shared/HostFailurePolicy.cs` 与 `src/Shared/Host/McpServer.{ToolBridge,Batch,Exports}.cs`。1.1 [领域 A](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md) 的通用规则在此不重复。

### 4.1 MCP 修订与 V4 会话

- 4.0 协商 MCP 2025-06-18（当前 C# SDK `ModelContextProtocol 0.3.0-preview.4` 支持的最高修订），发布说明写明所支持的修订。现行修订 2026-07-28 是无状态协议，迁移放在 4.x，待 .NET SDK 支持后与 MCP-01、MCP-05、MCP-12、MCP-15 一起处理。
- 本文中的“会话”指 **V4 会话**：宿主为一个 MCP 会话维护的全部服务器端状态，由 MCP 传输会话承载（stdio 是宿主进程中唯一的会话，HTTP 是每个 MCP 会话一个），随传输会话开始和结束，不能在另一个传输会话中恢复。

| 状态 | 作用域与有效期 |
|---|---|
| worker 进程与代次 | 每个 V4 会话一个 worker；代次故障后由 `RestartOpennessWorker` 或 `DisconnectPortal` 开始新代次 |
| Openness 通道 | 每个 V4 会话一条，原生调用串行 |
| TIA 进程租约 | 连接期间独占选定进程；`DisconnectPortal` 或会话结束时非拥有式释放 |
| 项目绑定与绑定纪元 | 由 `ConnectPortal`、`AttachOpenProject` 等建立，随会话结束 |
| 会话锁 | 越过原生调用的写入结果未知后生效，直到 `DisconnectPortal` 后开启新会话 |
| 生成计划与写栅栏 | 计划只属于当前会话和绑定；运行期间写栅栏拒绝本会话中不属于该运行的写调用 |
| 批次令牌 | 10 分钟，只能使用一次 |
| 导出句柄 | 只能在签发它的会话中读取和清理，24 小时 |
| 读游标 | 限定在同一发布键、会话、绑定、查询和快照内 |
| 导入暂存 | `<包根>/staging/<会话>/<批次>/` |

生成运行记录、审计链和调用日志不属于 V4 会话，宿主重启后仍可读取。

### 4.2 传输映射

- 每个 V4 工具结果的 `structuredContent` MUST 是第 4.3 节的信封；`content` MUST 恰好一个 `TextContent`，文本是同一信封经 `V4Json.Serialize` 得到的 JSON；`isError` MUST 等于 `!ok`。不使用图像、音频、`resource_link` 或嵌入资源，不设置结果级 `_meta`。
- 只有协议层问题用 JSON-RPC 错误：直接调用本版本未注册的工具由 SDK 返回 `-32602`。参数不合法、准入拒绝、执行失败、部分完成和结果未知 MUST 作为 `isError: true` 的工具结果返回；类型错误、未知字段、大小写重复和二次 JSON 编码都返回 `INVALID_ARGUMENT` 信封。内部异常经 `HostFailurePolicy.Classify` 转成信封。
- 直接调用、`CallTool`、批次子结果和 CLI 的 stdout 使用同一信封。`CallTool` 派发成功后 MUST 原样返回目标信封（`meta.tool` 与 `meta.requestId` 保持目标的值），MUST NOT 把结果再包进字符串；派发前失败时 `meta.tool` 为 `CallTool`，例外是目标存在且错误是指向具体字段的 `INVALID_ARGUMENT`，此时为目标工具名。
- `partial` 与 `unknown` 的结果仍带有效数据和证据，消费者 MUST 读取 `structuredContent`，不能只看 `isError` 就丢弃结果。

### 4.3 信封

示意（V20，未连接 TIA 时调用 `CompileHmiDiagnostics`；省略号代替时间、请求 ID 和消息）：

```json
{"schemaVersion": 4, "ok": false, "data": null,
 "error": {"code": "PRECONDITION_FAILED", "message": "No TIA Portal is connected. Call ConnectPortal with an explicitly selected processId first.",
           "details": {"condition": "connect-portal", "target": null, "parameter": null}},
 "meta": {"timestamp": "…", "releaseKey": "20", "tool": "CompileHmiDiagnostics", "requestId": "…",
          "outcome": "rejected-before-operation", "execution": "not-started", "requiresSessionReset": false,
          "behaviorPolicy": "current", "completeness": "none", "paging": null,
          "warnings": [{"code": "UNVERIFIED_BEHAVIOR", "message": "…", "details": {}}]}}
```

| 字段 | 规则 |
|---|---|
| `schemaVersion` | MUST 为整数 `4`，不能协商；读取端只接受 4 |
| `ok` | MUST 等于 `meta.outcome == "succeeded"`，也等于 `error == null` |
| `data` | 对象或 `null`；数组 MUST 包在对象内（如 `data.items`），不得是标量或顶层数组 |
| `error` | 成功时 MUST 为 `null`，否则 MUST 是第 4.6 节的错误对象 |
| `meta` | MUST 存在（第 4.4 节） |

编码规则：

- 无 BOM 的 UTF-8、无缩进，属性名 camelCase，按 `JsonPropertyOrder` 固定顺序输出（`schemaVersion, ok, data, error, meta`）。数字是有限值；同一对象不得有重复属性名；数组 MUST 存在（无值为 `[]`），元素不得为 null。
- 信封、`meta` 和 `details` 中未知或不适用的字段 MUST 输出 `null`，不得省略（API-05）。
- 协议层时间（`meta.timestamp`、`data.timestamp`、`createdUtc`、`expiresUtc` 等）MUST 是 UTC RFC 3339，固定七位小数并以 `Z` 结尾；读取方兼容 0–7 位小数；未指定时区的时间拒绝序列化。原生证据和数值 ticks 保持原有精度。
- 枚举拼写：`outcome`、`execution`、`behaviorPolicy` 和状态词用 kebab-case；`completeness` 与 `paging.mode` 是单个小写单词；错误码和警告码用 UPPER_SNAKE；工具输入的选择值用 camelCase；Siemens 原生名称保持原样（API-34）。
- 生产者由 `V4Validation` 按闭集校验每个发出的信封，包括审批、恢复提示等后加的装饰；`V4Json` 的严格读取端拒绝未知成员。跨版本的消费者用 `V4Json.DeserializeConsumer`，保留未知字段和诊断码。

不变量（生产者 MUST 满足，消费者 MAY 依赖）：

1. `outcome == partial` 当且仅当 `error.code == PARTIAL_FAILURE`；`outcome == unknown` 当且仅当 `error.code == OUTCOME_UNKNOWN`。
2. `NOT_EXECUTED` 只伴随 `rejected-before-operation`；`SESSION_RESET_REQUIRED` 只伴随 `rejected-before-operation` 且 `requiresSessionReset == true`；`TIMEOUT` 只伴随 `read-failed` 或 `rejected-before-operation`。
3. `succeeded` 且 `completeness == partial` 时 `warnings` 不为空；`behaviorPolicy == current` 时含 `UNVERIFIED_BEHAVIOR`。
4. `releaseKey` 只取八个发布键之一或 null；`tool`、`requestId` 和警告的 `message` 不得为空串。

### 4.4 meta

| 字段 | 规则 |
|---|---|
| `timestamp` | 信封构造时刻（UTC）；大响应寄存后保留原信封的时间 |
| `releaseKey` | 宿主进程的发布键；只有无法确定时才 MAY 为 null，不得猜测 |
| `tool` | 实际执行的工具名（`CallTool` 规则见第 4.2 节） |
| `requestId` | 服务器生成的关联 ID：审计调用 ID → 调用日志关联 ID → 新的 32 位小写 GUID；审批过的调用使用审批请求的 ID；批次为每个子项生成新 ID；不采用客户端提供的 ID |
| `outcome`、`execution`、`completeness` | 第 4.5 节 |
| `requiresSessionReset` | 表示实际的 Openness 准入锁：为 true 时，直到恢复前后续原生调用被拒绝；`unknown` 但未锁定时为 false，仍禁止重放（第 4.11 节） |
| `behaviorPolicy` | `not-applicable`（纯宿主工具或不属于 D1 行为族）、`current`（沿用既有原生行为，未经真机验收）、`safe-v4`（已验收的新行为；发布配置中没有选用） |
| `paging` | 第 4.8 节；不分页时为 null |
| `warnings` | 第 4.7 节；按发出顺序，装饰类警告追加在末尾 |

`meta` 位于 `structuredContent` 内部，是应用层元数据，不占用 MCP 的 `_meta`，也不读取 `traceparent`（MCP-12）。

### 4.5 outcome、execution 与 completeness

| outcome | 含义 | execution | completeness | CLI |
|---|---|---|---|---|
| `succeeded` | 已完成；预览只表示计划已生成 | `read-only` 或 `completed` | `complete` 或 `partial` | 0 |
| `rejected-before-operation` | 参数、前提、准入或审批拒绝，领域动作尚未开始 | `not-started` | `none` | 2 |
| `read-failed` | 只读执行失败；MUST NOT 当作空结果 | `read-only` | `none` 或 `unknown` | 3 |
| `failed` | 已执行的动作确定失败，没有成功项，后置状态已确认 | `completed` | `none`、`partial` 或 `complete` | 3 |
| `partial` | 有确定的成功项，也有失败或未执行项，没有未知写入 | `partial` | `partial` | 4 |
| `unknown` | 已发出的写入、传输或通道操作无法确认结果 | `unknown` | `unknown` | 5 |

- 聚合优先级 `unknown` > `partial` > `failed`。副作用以证据判断，MUST NOT 根据异常文本推测；原生结果状态 `PartialSuccess`、`None` 或不在预期内时一律为 `unknown`。
- `completeness` 描述本页数据是否完整交付，与 `paging.complete`（是否已到末页）是两回事；有后续页本身不意味着本页 partial。字段受版本限制、截断或有不可读属性时为 `partial`，宿主自动加 `INCOMPLETE_DATA`。
- 消费者 MUST 以 `meta.outcome` 为准；`data` 中残留的 `ok`、`success`、`operationSuccess`、`status`、`error` 等字段只是证据，不得用来推翻 outcome（API-29），新代码不得在 `data` 中加入这些字段。消费者 MUST NOT 把 `completeness: partial` 的成功当作完整结果，MUST NOT 根据警告推断 outcome；`rejected-before-operation` 表示什么都没执行，修正后 MAY 重新调用；`unknown` 时 MUST NOT 自动重放。
- 结果字段：旧实现的 `message` 改名为 `data.summary`，数组放在 `data.items`，旧 meta 内容放在 `data.evidence`。

### 4.6 错误

- `error` MUST 恰好包含 `code`、`message`、`details` 三个字段（DES-4 增加 `innererror` 时同步修改本条，第 6.5 节）。生产者拒绝未知错误码；跨版本消费者 MUST 容忍未知字段和错误码，按 `meta.outcome` 处理。
- `message` 是非空、稳定的英文文本，不承担机器语义，消费者 SHOULD NOT 解析它；填写 `details` 字段不得改变 `message`。
- `details` 是按错误码定型的对象（`ErrorDetails` 封闭联合）：文本字段为 `string|null`，列表为 `string[]`（无值为 `[]`）。参数错误 MUST 在 `parameter` 中给出第一个出错字段或 JSON 路径，`parameters` 列出全部出错字段且始终输出；整体或重复字段错误可以指向 `arguments`。
- `details` 和 `data` MUST NOT 包含堆栈或凭据：键名含 `stack` 或等于 `password` 的字段被删除，多行异常文本替换为 `Diagnostic details retained in the server log.`，盘符或 UNC 路径替换为 `<path>`（SEC-06）。
- 兜底分支 MUST NOT 选用 `INVALID_ARGUMENT`，除非能证明是输入错误；无法分类时用 `NATIVE_OPERATION_FAILED` 或 `INTERNAL_ERROR` 并保留证据（API-14）。

| code | 何时使用 | details | outcome |
|---|---|---|---|
| `INVALID_ARGUMENT` | 类型、枚举、未知或重复字段、大小写重复、二次 JSON 编码、缺少 apply 参数 | `parameter`、`parameters`、`allowedValues` | rejected |
| `LIMIT_EXCEEDED` | 数量、长度、深度或字节预算超限（如批次超过 50 项、worker 请求帧超限） | `parameter`、`limit`、`actual` | rejected |
| `UNSUPPORTED_CAPABILITY` | 本发布键、动作或对象不支持；UI 工具出现在批次中 | `releaseKey`、`capability`、`action` | rejected |
| `TOOL_NOT_FOUND` | `CallTool`、`GetToolUsage` 或 worker 的目标不在任何版本的目录中 | `tool` | rejected |
| `PROJECT_NOT_BOUND` | 没有绑定项目 | `{}` | rejected |
| `NOT_FOUND` | 对象、文件、句柄或批次令牌不存在 | `target` | rejected |
| `TARGET_AMBIGUOUS` | 目标不唯一 | `target`、`candidates` | rejected |
| `IDENTITY_MISMATCH` | PID 与启动时间、项目或绑定纪元不一致 | `target`、`expected`、`actual`（脱敏） | rejected |
| `ALREADY_EXISTS` | 拒绝覆盖 | `target` | rejected |
| `CONFIRMATION_REQUIRED` | 工作台审批被拒、超时或不可用；D1 计划确认 | `reason`、`planHash`、`requestId`（三键必出；除 `plan-confirmation` 外后两者非 null） | rejected |
| `PLAN_STALE` | 计划、输入文件、清单、身份或令牌已失效 | `planHash`、`reason`、`previewToken` | rejected |
| `PRECONDITION_FAILED` | 借用、脏项目、所有权、未连接、生成运行中、批次身份变化等 | `condition`、`target`、`parameter` | rejected |
| `OFFLINE_REQUIRED` | 目标或受影响的设备需要离线 | `targets` | rejected |
| `AUTHENTICATION_REQUIRED` | 缺少必要认证 | `capability`（不回显凭据） | rejected |
| `ACCESS_DENIED` | 权限拒绝 | `operation`、`target` | rejected |
| `SESSION_RESET_REQUIRED` | 先前写入结果未知，会话已锁 | `reason` | rejected，`requiresSessionReset` 为 true |
| `RESOURCE_UNAVAILABLE` | SDK、Openness 环境、可选服务、工作台或包内资源缺失 | `resource` | rejected |
| `IO_FAILED` | 动作开始前的本地文件读写失败 | `operation`、`path`（脱敏） | rejected |
| `NATIVE_OPERATION_FAILED` | 已知的原生失败 | `nativeCode`、`nativeMessage`、`evidence` | read-failed、failed 或 rejected |
| `COMPILE_ERRORS` | 编译完成但有错误 | `errorCount`、`warningCount`、`errors[{path, description}]`（至多 10 条叶子错误） | failed |
| `CANCELLED` | 派发前取消（`tool-queue`）、预检中取消（`approval-precheck`）、审批等待中取消（`approval-wait`）；已派发只读调用的取消 | `stage` | rejected 或 read-failed |
| `TIMEOUT` | 宿主能证明没有派发的超时 | `stage` | rejected 或 read-failed |
| `NOT_EXECUTED` | 批次中前面的项停止后本项未执行 | `causeIndex` | rejected |
| `PARTIAL_FAILURE` | 有已知成功项，也有失败或未执行项 | `succeeded`、`failed`、`notExecuted` | partial |
| `OUTCOME_UNKNOWN` | 写入、传输或通道中断，结果未知；禁止自动重放 | `stage`、`evidence`、`reason`（只能是 `awaiting-openness-confirmation` 或 null） | unknown |
| `INTERNAL_ERROR` | 非原生、未分类的故障；堆栈只写日志 | `diagnosticId` | read-failed |

`condition`、`stage`、`reason`、`resource`、`operation` 是开放词表，生产者 MUST 用 kebab-case 短语，消费者只作诊断、不据此分支，并容忍未知值（API-23）。已知取值例如 `PRECONDITION_FAILED.condition` 的 `batch-identity`、`generation-run-active`、`connect-portal`、`save-explicitly-before-close`，`OUTCOME_UNKNOWN.stage` 的 `worker`、`tool-call`、`native-result-state`，`SESSION_RESET_REQUIRED.reason` 的 `previous-outcome-unknown`。

### 4.7 警告

`meta.warnings` 的元素为 `{code, message, details}`，code 是闭集。警告 MUST NOT 把失败变成成功。

| code | 何时出现 | details |
|---|---|---|
| `INCOMPLETE_DATA` | `completeness` 为 `partial` | `{}` |
| `NATIVE_WARNING` | 原生结果为 Warning 状态，或旧数据带警告 | `nativeState`、`nativeStateType` 或 `{}` |
| `CANDIDATE_ONLY` | 离线构造器生成的候选内容，尚未经过导入验证 | `{}` |
| `UNVERIFIED_BEHAVIOR` | `behaviorPolicy == current` | `{}` |
| `NATIVE_CAPABILITY_LIMIT` | Foundation 原生能力拒绝 | 无定型字段 |
| `CLEANUP_FAILED` | 保留的编译清理错误码；当前无候选执行路径，契约枚举保留 | 历史候选定义已归档 |
| `DIAGNOSTIC_WRITE_FAILED` | CLI 报告文件写不出 | 报告定义 |
| `APPROVAL_DISABLED` | 审批关闭时执行的 MCP 写调用，每个信封至多 1 条 | `enabled: false` |
| `APPROVAL_PRECHECK_REFUSED` | 写入被自身预览拒绝，没有发起审批 | `stage: approval-precheck` |
| `RECOVERY_GUIDANCE` | 错误码为 `INVALID_ARGUMENT`、`PRECONDITION_FAILED`、`SESSION_RESET_REQUIRED` 或 `CONFIRMATION_REQUIRED` 时，每个信封至多 1 条 | `getToolUsage{toolName, operation?}`、`releaseKey`、`exampleArguments`（≤ 4096 字节）、`exampleOmitted`、`validation`、`bindingMeaning` |
| `BACKUP_SKIPPED` | 覆盖导入前的备份被跳过 | `objects`、`recoveryDirectory` |
| `STAGING_FOLDER_RETAINED` | 暂存目录被保留 | 暂存定义 |

`RECOVERY_GUIDANCE` 的示例参数取自 `GetToolUsage` 的现行示例库，MUST NOT 包含调用方的参数；它不授权、不重试、不执行任何写入，`message` 以 `▶ RECOVERY:` 开头（[恢复指导](../reference/recovery-guidance.md)）。

### 4.8 分页

```text
paging = {mode: "offset"|"cursor", unit: "items"|"utf16", offset: int32|null, limit: int32, nextOffset: int32|null,
          cursor: string|null, nextCursor: string|null, total: int32|null, complete: bool}
```

- `limit > 0`，`total` 为 null 或 ≥ 0。offset 模式：`cursor`、`nextCursor` 为 null；`complete` 时 `nextOffset` 为 null，否则 `nextOffset > offset`。cursor 模式：`offset`、`nextOffset` 为 null；`complete` 时 `nextCursor` 为 null，否则非空且不等于 `cursor`。未读完的页 MUST 给出能向前推进的 next 值。
- 消费者 MUST 用 `nextOffset` 或 `nextCursor` 请求下一页，不得自行计算 `offset + limit`。`data` 中残留的 `offset`、`total`、`dataComplete` 与 `meta.paging` 重复时以 `meta` 为准。
- 页预算参数统一叫 `limit`，另有 `offset` 和 `cursor`；默认值按工具公布，不强求统一。`maxItems`、`maxResults` 是上限而不是分页。
- offset 分页不提供快照一致性：项目在页间变化时，条目可能重复或遗漏，`total` 也可能变化。需要稳定观察时用提供快照的 cursor 工具，或先导出再分页。游标不透明，限定在同一发布键、会话、绑定纪元、查询和快照内，不得跨项目复用；清单变化时拒绝旧游标，需从空游标重新开始。
- 条目页 `unit=items`；导出文本切片 `unit=utf16`，`offset` 与 `limit` 是 UTF-16 码元，切片不拆开代理对，所以一页可能少一个码元；`byteLength` 始终是 UTF-8 字节数。
- `tools/list` 一次返回全部工具，MCP 的列表分页规则不适用于工具结果（MCP-08）。

### 4.9 大响应与导出句柄

- 直接调用或 `CallTool` 的结果超过响应上限（默认 20,000 个 UTF-16 码元，`TIA_MCP_MAX_RESPONSE_CHARS` 覆盖，0 或负数表示不限）时寄存在本会话中。只处理恰好一个 `TextContent` 的结果；错误结果、导出工具本身、经 `CallTool` 转发到导出工具的调用和已寄存的结果不再寄存；寄存失败时原样返回完整结果。
- 寄存后 `data` 换成 `{export, continuation, text, returned}`：`export` 是导出句柄，`continuation` 给出下一次 `GetExportContent` 的名称和参数，`text` 是首片；`meta.paging` 为 UTF-16 offset 页，原来的 `meta` 和 `error` 保留。消费者 MUST 先检查 `data.export`，存在时用 `GetExportContent(exportId, offset=meta.paging.nextOffset)` 读到 `complete`，按顺序拼接全部 `data.text` 后再解析原 JSON（分页可能切开 JSON 记号）。
- 导出句柄 `ExportHandle = {id, mediaType, byteLength, sha256, expiresUtc, tool, target, createdUtc, totalLength}`；JSON 信封的 `mediaType` 为 `application/json`，普通文本为 `text/plain`；`sha256` 是 64 位小写十六进制。

| 工具 | 类别 | 规则 |
|---|---|---|
| `GetExportContent(exportId, offset, limit)` | READ | 每片序列化后不超过 20,000 个字符；句柄不存在、过期或被淘汰时返回 `NOT_FOUND`，需重跑原工具 |
| `ListExportHandles(tool?, offset, limit)` | READ | offset 分页 |
| `SaveExportContent(exportId, outputPath, raw, overwrite=false)` | FILE | 写入带 BOM 的 UTF-8，`raw=true` 保存完整原文；目标已存在且未给 `overwrite=true` 时返回 `ALREADY_EXISTS`；写入开始后 I/O 失败返回 `OUTCOME_UNKNOWN`（`file-write`）；输出根之外的目标须工作台审批；指向 `approval.settings` 或 `.git` 等管理目标（含别名和链接）时先按 Human-only 规则拒绝，审批不能覆盖 |
| `DeleteExportHandle(exportId)`、`ClearExportHandles(olderThanHours=24)` | WRITE | 只清理进程内句柄；`olderThanHours=0` 清除全部 |

句柄只能在签发它的会话中读取和清理，有效期 24 小时；存储全进程共用，最多 32 条、共 8,000,000 个字符，按 LRU 淘汰。worker 通道的超大回复处理见第 5 节。

### 4.10 写操作：预览、审批与计划哈希

工具描述以 `[层][域][操作]` 开头，操作类别为 `UI`、`SESSION`、`READ`、`WRITE`、`FILE`、`OFFLINE`、`ONLINE`、`ONLINE-WRITE` 和 `EXECUTE`，显式登记在 `ToolMetadata` 或 `[ToolClassification]` 中，逐版数量见[工具矩阵](../reference/tool-matrix.md)。

需要审批的写调用由 `HostBehavior.ApprovalWrite` 判定：

| 情形 | 是否审批 |
|---|---|
| 分类为 `WRITE` 或 `ONLINE-WRITE` 的实际执行；`SaveProject`、`SaveProjectCopy`、`CloseProject`；`StageImportFiles`、`CleanupStagedImportFiles` | 审批 |
| 有 `dryRun` 参数的工具显式传 `dryRun: true`，或省略且默认是预览 | 不审批，也不作为写请求写审计 |
| `ExchangeSystemDiagnosticsSettings`、`ExchangeUnifiedTags`、`ExchangeUnifiedScriptModules` | 只有 `action=import` 审批 |
| `GetEnvironmentDiagnostics` | 只有 `fix=true` 审批（默认 `false`） |
| `EnsureOpennessUserGroup` | 始终审批 |
| `FILE`、`ONLINE`、`SESSION`、`EXECUTE`，以及连接、附着、打开和断开 | 除下列文件目标规则外不审批；新增写动作须登记为写类别 |
| MCP 调用向调用方指定的输出根之外写文件（含 `BuildDeviceAmlDocument`） | 按写调用审批；预览、产品自行选择的输出位置、工作台操作端及已批准的生成计划步骤沿用原行为；空参数由其他调用方输入派生位置时仍检查实际目录。MCP 的 Git stage/commit 无论目录是否在根内均须审批。输出根由 `GetSessionState` 的 `data.outputRoot` 返回；根及其子路径须无重解析点 |

预览与确认参数：

| 参数 | 语义 |
|---|---|
| `dryRun: boolean = true` | 默认预览；预览 MUST NOT 创建、导入、编译、保存或重试写入，MUST NOT 等待审批 |
| `confirm` 及既有的 `confirm*`、`allowWrite` | 安全确认开关，与 `dryRun: false` 同时使用；新工具 MUST 只用 `confirm`（API-20） |
| `expectedPlanHash` | 预览给出的计划哈希；不匹配时拒绝 |
| `expectedProjectFile` | 当前绑定的项目文件，包括 `ApplyGenerationPlan` |
| `expectedProject` | 只表示项目**名称**（Unified 与批次工具） |
| `previewToken` | `PreviewToolBatch` 等签发的一次性预览令牌，不按计划哈希解析，不得放入 `PLAN_STALE.planHash` |
| `expectedDocumentHash`、`expectedPackageHash`、`expectedInventoryHash` | 文档、包或清单的预期哈希，与 `expectedPlanHash` 分开 |

- 生产者 MUST 在原生调用前完成全部 apply 参数检查；批量目录导入的 apply 缺少 `expectedProjectFile`、`expectedPlanHash`、`confirm` 或 `importOrder` 时返回 `INVALID_ARGUMENT`。
- 协议层的计划哈希、参数摘要和文件 `sha256` MUST 是 64 位小写十六进制、不带 `sha256:` 前缀；`tiamcp.plan/1` 等数据格式内部的哈希属于各自格式。
- `current` 工具的预览沿用自己的实际字段（如 `data.planHash`、`data.projectFile`、`data.token`），不能假定都有 `data.plan`。D1 候选协议（`mode = preview|apply`、`data.plan`，计划含 PID、启动时间、项目文件与绑定纪元组成的身份、输入哈希、清单哈希和操作列表，一个计划只允许一次执行尝试）的类型与约束保留，不代表当前可调用的路径：SafeV4 候选实现已归档到标签 `archive/safev4-candidates`，2026-11-02 从当前代码删除，各族通过 L5 后基于届时的现行代码重建（API-39，[ADR 0002](decisions/0002-native-behavior-by-family-acceptance.md)）。

#### MCP 写操作审批

审批默认开启，只适用于 MCP 调用；命令行由本机用户直接执行，不经审批。

1. **预检**：检查生成写栅栏（`PRECONDITION_FAILED`，`generation-run-active`，带 `data.runId`）、会话锁（`SESSION_RESET_REQUIRED`）、调用方输入文件和批量 apply 参数，再用同一参数在只读作用域运行一次预览。预览拒绝时标为 `rejected-before-operation` 并加 `APPROVAL_PRECHECK_REFUSED` 返回；预览报告没有变化时直接返回预览结果，不请求审批。
2. **等待审批**：`PendingApproval` 带随机请求 ID、工具与发布键、缓存及请求指定的项目身份、对象与动作清单、计划哈希（没有计划时用参数摘要）、完整参数与身份摘要和截止时间，经审批管道交给工作台。审批卡显示脱敏后的参数；结果为批准、拒绝、超时或工作台不可用之一，后三者返回 `CONFIRMATION_REQUIRED`（`denied`、`timeout`、`workbench-unavailable`）。批准 MUST 匹配请求 ID、计划哈希与参数摘要，只能用一次；重复、迟到、格式异常或断连一律拒绝。
3. **派发前复核**：在 Openness 通道内再次检查写栅栏和会话锁，`ApprovalStillMatches` 核对参数摘要与绑定身份，不一致返回 `CONFIRMATION_REQUIRED`（`denied`）；再次检查输入文件。
4. **执行一次**：审计 `start`，执行，判断是否锁会话，向工作台发送结果通知（只更新该行状态，不能授予批准），把 `meta.requestId` 设为审批请求 ID，审计 `end`。

- 设置在 `data/config/approval.settings`，两行 `enabled=true|false` 与 `timeoutSeconds=1..3600`；缺失、损坏或不可读时视为开启。工作台可选 60、120、300 秒，默认 120 秒。离线快照或测试可在专用 `TIA_MCP_DATA_DIRECTORY` 下关闭审批，此设置不得进入交付缺省值。
- 审批关闭时照常执行，信封加 `APPROVAL_DISABLED`；工具自身的确认、计划哈希和项目身份检查仍然生效。
- 审批与审计的参数摘要 MUST 先按调用日志共用的 `CallJournalPayload` 敏感字段规则把密码、凭据、密钥和令牌替换为 `••••`（递归作用于对象、数组、名称/值表与嵌入 JSON），再计算 SHA-256；审批卡先脱敏再生成长文本摘要。非敏感参数、发布键、工具和绑定身份仍参与摘要；只替换密码时可继续使用已预览、已审批的计划，实际操作接收原始值（SEC-06）。
- 等待发生在 Openness 通道之外、动作调用之前，不改变 Siemens 调用序列、参数、线程归属或会话。客户端在等待期间取消返回 `CANCELLED`（`approval-wait`）。
- 生成计划只审批一次（第 8 节）；`ApplyToolBatch` 本身不审批，其中每个写项各自审批。

### 4.11 未知结果、会话锁与恢复

- 产生 `unknown` 的情形：写入或文件操作发出后抛出异常；原生结果状态为 `PartialSuccess`、`None`、不在预期内，或失败且 `mayHaveChanged`；写后核对失败；worker 退出、挂起、帧格式错误或 TIA 进程丢失（整个代次故障，包括只读调用，API-26）；批次子项已发出但结果读不到。
- 只有同时满足以下条件才锁定会话（`SessionBehavior.LocksSession`）：本次调用发出过原生调用（调用日志有原生边界，或 worker 证据为 `nativeOutcomeUnknown`、`nativeIssued`），属于写入或 `FILE`、`EXECUTE` 类别，并且 outcome 为 `unknown`。
- 锁定后，使用 Openness 通道的工具（`DisconnectPortal`、`RestartOpennessWorker` 和派发类工具除外）在审批和原生调用之前被 `SESSION_RESET_REQUIRED`（`previous-outcome-unknown`）拒绝。
- 恢复（MUST）：检查 TIA 的实际状态 → `DisconnectPortal` → 开启新的 MCP 会话并显式 `ConnectPortal`、`AttachOpenProject`。MUST NOT 重放失败的请求；重新预览也不等于获准再次写入（SEC-12）。
- `TIMEOUT` 只用于宿主能证明没有派发的超时；worker 通道超时是通道故障，按 `OUTCOME_UNKNOWN` 处理（API-26）。派发前取消返回 `CANCELLED`（`tool-queue`），派发后宿主忽略取消、等待真实结果；派发前的 `CANCELLED` 结果仍用于 CLI、批次和审计（MCP-07）。
- 收到 progress token 时，宿主每 15 秒发送一次单调递增的进度心跳，覆盖排队、审批等待和长操作；worker 进度与宿主心跳共用序号。通知不代表成功，失败也不改变原生请求。

### 4.12 批次与桥接

- 八个发布键使用同一份 `ToolMetadata` 准入表：只读批次 MUST 要求目标 `BatchRead: true`，预览批次 MUST 要求 `BatchWrite: true` 且有布尔 `dryRun` 参数；MUST NOT 按 `READ`、`OFFLINE`、`WRITE` 或 `FILE` 类别推断准入。新开放只读批次的工具不改变绑定、会话、项目、库或在线状态，不写文件，执行成本有界；原生交叉引用查询和 `GetExportContent` 不进只读批次。版本工具目录不列出批次标志，不能用工具出现在目录中判断准入。
- `CallTool(name, arguments)` 在完整目录中精确匹配工具名（区分大小写）；目标是 `CallTool` 本身返回 `INVALID_ARGUMENT`；目标属于其他版本返回 `UNSUPPORTED_CAPABILITY`（`capability` 为工具名）；完全未知返回 `TOOL_NOT_FOUND`（MCP-11）。`PreviewToolCall` 只校验不执行，参数错误时仍返回 `ok: true`，错误放在 `data.validationError(s)`。
- `RunReadOnlyToolBatch(operations[1..50])` 按输入顺序执行，不嵌套编排，不提供一致的原生快照。
- `PreviewToolBatch(operations[1..50], expectedProject)` 强制 `dryRun: true` 逐项预览，失败即停，余下项为 `NOT_EXECUTED`；全部成功时签发 64 位十六进制令牌（10 分钟，只能用一次）。`ApplyToolBatch(previewToken)` 取出令牌即作废；批次含 `UI` 工具返回 `UNSUPPORTED_CAPABILITY`；复核绑定身份和全部预览，有变化返回 `PLAN_STALE`（令牌放在 `details.previewToken`）；然后按顺序以 `dryRun: false` 执行，遇失败或未知停止，不回滚（`data.rollbackPerformed: false`）。
- 批次结果 `data.items = [{index, target, result}]`：`index` 从 0 连续递增，`result` 是嵌套信封对象，不是字符串；子结果读不到时 `row.outcomeUnknown: true`，消费者不得忽略。

| 子结果情况 | 父 outcome | 父 error |
|---|---|---|
| 任一子项 `unknown` | `unknown` | `OUTCOME_UNKNOWN`（`batch`） |
| 全部成功 | `succeeded` | null |
| 有 partial，或成功与失败混合 | `partial` | `PARTIAL_FAILURE`；没有可证明的成功计数时三项为 0，依赖子信封证据 |
| 全部未开始 | `rejected-before-operation` | 触发停止的子项错误，`data.causeIndex` 标明来源 |
| 其他（没有成功项） | `failed` 或 `read-failed` | 同上 |

### 4.13 版本与演进

| 层 | 标识 | 规则 |
|---|---|---|
| 发布键 | `14sp1`、`15.1`、`16`–`21` | 宿主进程用 `--release-key` 固定；运行目录为 `runtime/v<发布键>/` |
| 信封 | `schemaVersion: 4` | 固定，不协商 |
| 工具迁移标记 | `ToolProfiles.resx` 的 `envelopeVersion` | 值为 4 时启用 V4 准入 |
| 契约快照 | 基线格式 1、响应格式 3 | 名称、目录和覆盖面由检查校验，schema 与响应差异在评审时逐项核对 |
| 数据格式 | `tiamcp.plan/1`、`tiamcp.run/1`、`tiamcp.machine/1`、审批管道 `v1`、工作台控制契约 `[1]` | 各自独立版本 |
| 产品版本 | 语义化版本；`/mcp/health` 返回 `fileVersion` | 见下 |
| MCP 协议 | SDK 协商（最高 2025-06-18） | 第 4.1 节 |

- 产品版本从 2.8.0 起按[语义化版本](https://semver.org/lang/zh-CN/)：工具名、参数或返回形状的不兼容改动和删除工具升主版本；新工具、新参数和新功能升次版本；缺陷修复和文案改动升修订号。预发布写作 `X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`。
- 4.0 是硬切换，不提供 3.x 别名，迁移靠生成的对照表（[ADR 0001](decisions/0001-hard-cut-v4-contract.md)）。
- 4.x MUST 只做增量：新工具、新可选参数、新字段和新诊断码。改变已有字段含义、移除字段或工具、改名、改变必填参数等破坏性修改 MUST 升 `schemaVersion`，并在发布说明和迁移表中逐项列出（API-11）。消费者 MUST 容忍未知字段、未知警告码（忽略）和未知错误码（按 outcome 处理并保留证据）；生产者的枚举和 `V4Validation` 仍是闭集。
- 已决定、尚未实现（维护者 2026-11-01）：工具和参数可以标记为弃用，目录和 `GetToolUsage` 输出弃用信息，调用时在 `meta` 中给出弃用警告，迁移表按版本维护；弃用窗口与字段待定稿，参数别名先按“保留一个主版本”执行（API-12）。

### 4.14 V4 D1 行为能力披露

- 发布行为与 V4 名称、输入类型和信封版本无关。`ToolProfiles.resx` 的 `behaviorPolicies` 由[真机台账](../reference/real-machine-ledger.md)的逐发布键 L5 记录生成；八版的 DEVICE、IMPORT、EXPORT、SESSION、CLOSE、SOURCE、COMPILE、FALLBACK 行为族目前都是 `current / NOT RUN`，没有启用的 safe-v4 族。只有明确的逐版 L5 证据能把一个族切换到 safe-v4（[ADR 0002](decisions/0002-native-behavior-by-family-acceptance.md)）。
- `GetToolUsage` 的空选择器返回所选发布键的 `data.behaviorCapabilities`（每项 `family`、`state`、`l5`、`entries`；空 `entries` 表示该宿主没有此族入口）；`InitializeEnvironment`、`RunCapabilitySelfTest` 和离线 `--catalog` 报告同一张表。适用的目标信封无论成败都保留 `meta.behaviorPolicy=current` 与 `UNVERIFIED_BEHAVIOR`；桥接或批次派发前的拒绝保留桥接身份，同时披露已选定的目标族。
- 当前 schema 保留已实现的参数和真实缺省（如 `confirm`、`expectedProjectFile`、计划哈希），不能只凭参数名删除现行能力；候选专用参数不在现行目录中广告，Strict 验包与 Release 构建拒绝 `TiaMcpTestPolicy` 标记。契约快照记录每个工具的 `outputSchema` 和实际查询的行为能力表。

### 4.15 CLI 退出码

| 退出码 | 条件 |
|---|---|
| 0 | `succeeded` 且 `completeness` 为 `complete` |
| 2 | `rejected-before-operation` |
| 3 | `read-failed`、`failed`；`succeeded` 但 `completeness` 为 `none`；非工具动词的异常 |
| 4 | `partial`；`succeeded` 但 `completeness` 为 `partial` |
| 5 | `unknown`；`succeeded` 但 `completeness` 为 `unknown` |
| 64 | 命令行语法错误，不输出信封 |
| 70 | 无法创建工具上下文的进程级故障或包内资源不可用，不输出信封 |

stdout 只输出一份信封 JSON，诊断写 stderr；用户要求的人类可读报告属于 `data` 中的文件或内容，MUST NOT 影响退出码（API-33）。

## 5 worker 协议

宿主与 worker 之间是 WorkerChannel 协议 2，线格式以 `src/WorkerChannel` 的帧、状态机和 profile 为准；MCP 对外信封与内部通道信封分开，内部 outcome 不暴露为 MCP 枚举。八版 PLC worker 使用 `adapter.*` 方法。

| 方面 | 规则 |
|---|---|
| 分帧 | 每行一个无 BOM 的 UTF-8 JSON-RPC 2.0 对象；请求 ≤ 1 MiB，worker 输出行 ≤ 16 MiB（不计末尾 LF）。先限长，再验证 UTF-8 和 JSON；缺换行的末尾、空行、BOM、重复或未知信封字段被拒绝；发送端执行相同限制 |
| 握手 | worker 先发无 id 的 `hello`：`protocol: 2`、`releaseKey`、`workerSha256`、`adapterSha256`、`pid`、64 位十六进制 `nonce`、`bindingEpoch: 0`、`bound: false`。宿主核对实际文件哈希、子进程 PID 和每次启动生成的 32 字节随机 nonce；worker 必须新建且未绑定；不接受协议降级（SEC-07） |
| 请求 | 只有一个在途请求，正整数 id 严格递增；请求含 `jsonrpc`、`id`、`method`、`params`、`bindingEpoch`，可选 `requestId` 与 `toolName`（各 1–128 个 ASCII 字符 `A-Za-z0-9-_.:`，`toolName` 需同时有 `requestId`；非法值拒绝，不截断）。客户端拒绝时不发送、不消耗 id；worker 拒绝时不派发并使通道失效；必须先验证 hello；通道重入或并发使用使会话失效 |
| 回复 | 相同 id、`bindingEpochBefore`、`bindingEpochAfter`，以及唯一的 `result` 或 `error`。错误保留原 code 与 message，`data` 含 `outcome`（`RejectedBeforeNative`、`ReadFailed` 或 `Unknown`）和 `evidence`；宿主负责映射 |
| 调用日志投影 | 宿主为每次 `adapter.*` 派发在同一请求 id 下记录 `worker:<工具名>` 的 BEFORE、RETURNED、THREW；worker 用相同 id 写 `callProjection: 1` 的 BEFORE、RETURNED（未完成为 INTERRUPTED），适配器原生行也用此 id。内部派发投影只记身份，不记参数与结果 |
| 超大回复 | 先完整编码；超过 16 MiB 时，在发送任何字节前调用 spill 回调，返回 `result.workerReplySpill`（32 位 GUID `id`、`byteLength`、`sha256`），文件在 `<数据根>/worker-spills`，worker 用扩展长度路径写入；宿主核对身份、普通文件、UTF-8、长度、哈希与 JSON 后删除文件。没有回调但有有界的 `OversizedResultJson` 时发送该拒绝结果（如 `LIMIT_EXCEEDED`）；两者都没有或转存失败时为结果未知并使通道失效，不重试派发。序列化后超过 8 MiB 的结果寄存为导出句柄 |
| 进度 | 无 id 通知，含 `requestId`、从 1 递增的 `sequence` 和 0–100 的 `percent`；每个请求最多 1,024 条，只能针对当前调用。跨线程、回调结束后、空闲期间、旧或未知 id 的进度，以及重复回复、再次 hello 都使通道失效；进度不延长调用预算 |
| 绑定纪元 | 来自 worker 每次派发前后的托管状态缓存，不为核对身份额外调用 Openness；PID、项目路径、所有权或本地会话状态改变时递增。回复的 before 必须等于宿主保存的纪元；附着、打开、创建、关闭和断开按状态机验证变化，异常变化使会话失效 |
| 取消与故障 | 派发前取消不发送、不消耗 id、不使后续调用失效。派发后的写入或读取错误、超时和无可信回复都是结果未知并使代次失效，禁止重试或自动重启。通道级终止不能证明 TIA 已取消；释放通道只关闭自己的管道，不结束 TIA |
| 超时 | 默认 120 秒，worker 代次首次附着 300 秒，编译、下载上传、导入导出、归档和库更新 30 分钟；可用 `--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 覆盖（`WorkerTimeoutPolicy`，[ADR 0008](decisions/0008-worker-timeouts-and-faults.md)） |

[worker-channel 套件](../../tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/README.md)覆盖双端状态机与独立进程故障，`TiaMcp.WorkerChannel.TransportFixture` 提供协议替身；这些证据不替代原生行为验收。

## 6 MCP 工具设计规则

本节是新增或修改 MCP 工具时的项目规则，通用部分见 1.1 [领域 A](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md) 与[新增工具清单](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md#mcp-checklist)；协议细节见第 4 节。

### 6.1 何时新增工具

| 规则 | 依据 |
|---|---|
| MUST：只为官方 Openness API 支持的能力注册工具；一个工具在某个发布键上至少有一个动作能在该版 API 上运行才注册，不支持的动作在原生调用前返回 `UNSUPPORTED_CAPABILITY`，不靠名称推断可用性 | [ADR 0012](decisions/0012-old-release-tool-registration.md)、[ADR 0014](decisions/0014-tool-counts-follow-official-api.md) |
| MUST：各版本的工具数量随 API 支持而定，不为一致而拉平；数量只由生成目录给出，文档不手写 | [版本工具目录](../reference/version-tool-catalog.md) |
| SHOULD：先考虑给现有工具加动作或可选参数；新增可选参数是增量变化，新工具会增加模型的上下文负担 | 1.1 MCP-34 |
| MUST：同一能力只保留一个实现，八个发布键共用一份声明与逻辑，版本 API 差异放在按发布键编译的适配器中 | [ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| MUST：新工具或契约变化先按[开发流程](../manuals/workflow.md#附录-工具规格模板)附录的模板把工具规格写进任务说明，审查后再实现 | 1.1 [新增工具清单](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md#mcp-checklist) |

### 6.2 命名与描述

| 规则 | 依据 |
|---|---|
| MUST：工具名是 PascalCase 的“动词 + 领域对象 + 必要限定”，只用 ASCII 字母和数字；缩写写成单词形式（`Plc`、`Hmi`、`OpcUa`、`Db`）；动词取自闭集（含经批准的 `Prefill`、`Publish`），生成器检查全部工具名 | `build-tools/release/CatalogData.cs`；1.1 MCP-07 |
| MUST：名称不暴露 `Json`、`Xml`、`Xlsx` 等序列化格式；`SimaticMl`、`Aml`、`Scl`、`FlgNet` 是领域语言名，保留。单个对象用单数，批量操作用复数；新 `Manage*` 工具 SHOULD 用单数对象类型，已有名称的改名逐项由维护者决定 | 1.1 MCP-08 |
| MUST：直接调用、`CallTool`、`PreviewToolCall` 和大响应寄存层都精确匹配工具名；注册时拒绝仅大小写不同的名称 | 第 4.12 节 |
| MUST：描述以 `[层][域][操作]` 开头，操作类别取自第 4.10 节的闭集 | `ToolMetadata` |
| SHOULD：描述先写工具做什么和前提条件（是否需要绑定项目、需要离线、是否写入），再写限制与相关工具；不写影响模型使用其他工具的指令 | 1.1 MCP-09、MCP-10 |

#### 用户可见文案语言

- MUST：新增的 MCP 可见文本（工具描述、参数说明、错误与警告消息、服务器说明）用英文；工具名、代码、TIA 原文、对象名和示例值保持原样，中文只作为数据出现。MCP 响应不做本地化；本地化只在工作台和命令行 `doctor` 输出中进行。
- MUST：错误使用稳定的错误码，文本不承担功能；既有契约消息保持不变。工作台使用中英资源，用户文档按[文档规范](documentation.md)维护。

### 6.3 分类、注解与审批

| 规则 | 依据 |
|---|---|
| MUST：每个工具在 `ToolMetadata`（`src/Logic/ModelContextProtocol/ToolMetadata.cs`）或 `[ToolClassification]` 中登记显式分类，不按名称推断。分类是安全开关：会改变项目、设备、包外文件或 Windows 状态的动作必须落在需要审批的类别中 | 1.1 MCP-22 |
| MUST：注解由实际发布的描述符和 `ToolMetadata` 生成，四个 hint 全部显式输出，按最有副作用的动作与参数组合取值；带 `dryRun` 参数的工具 `readOnlyHint` 为 false；删除、覆盖、属性替换和目录导出覆盖按具体动作决定 `destructiveHint`，`Ensure` 不代表非破坏性；`ONLINE` 的连接与断开、`SESSION`、`EXECUTE`、`UI` 用逐工具取值 | 1.1 MCP-22 |
| MUST：`WRITE`、`ONLINE-WRITE` 的实际执行经工作台审批；不得新增绕过审批的入口 | 第 4.10 节；SEC-01 |
| MUST：写工具提供预览，有 `dryRun` 参数的工具默认预览，预览路径不写项目、文件或在线设备。维护者 2026-11-01 决定每个写入都可预览、多步写入失败时回滚，由 DES-3 实施，尚未完成 | 1.1 MCP-23 |
| MUST：写工具显式要求目标身份：项目文件用 `expectedProjectFile`，`expectedProject` 只表示项目名称；内容摘要用 `expected<对象>Hash`，一次性令牌用 `previewToken` | 第 4.10 节；1.1 MCP-14 |
| MUST：结果未知时返回 `OUTCOME_UNKNOWN`，不自动重放 | 第 4.11 节；[ADR 0008](decisions/0008-worker-timeouts-and-faults.md) |

### 6.4 参数

规范形式、逐工具路径别名和缺省导出位置见 [ADR 0032](decisions/0032-canonical-tool-parameters.md)。文件输出用 `exportFile`，目录输出用 `exportDirectory`，设备路径用名称数组；旧参数保留至 5.0 并返回 `DEPRECATED_PARAMETER`。项目名称与项目文件身份仍为两个独立断言。

现行规则：

- MUST：参数名用 camelCase；Unified 主题 `palette` 中 Siemens 原生的键名（如 `Background`）例外。路径参数以 `Path`、`File`、`Directory` 结尾（`exportDir`、`sourceDir` 是既有例外）。
- MUST：内容摘要用 64 个小写十六进制字符，不带 `sha256:` 前缀；页预算参数用 `limit`。
- MUST：操作选择器用 `action`（另有既有的 `kind`、`mode`、`operation`）；自定义的选择值用 camelCase，Siemens 原生枚举名保持原样。
- 现状偏差：部分可选字符串参数用 `""` 作为缺省哨兵（API-35）；同一概念在不同工具中的形式不一致，例如导出目标有的是目录、有的是新文件，`devicePath` 有的是字符串、有的是数组。

参数模型已由 ADR 0032 落地；本节其余错误与诊断要求由 DES-4 后续部分实施：

- 参数 schema 按 JSON Schema 2020-12：必填列在 `required`，可选参数省略即未提供，不用 `""` 哨兵；闭集用 `enum`，固定格式用 `pattern`，互斥参数的选择规则写在各参数说明中，并在运行时校验；对象 `additionalProperties: false`（1.1 MCP-11）。
- MUST：任何已发布工具的 `inputSchema` 根层均不得包含 `oneOf`、`allOf` 或 `anyOf`；嵌套对象或字段仍可使用。维护者 2026-11-04 收到用户报告，称 Claude API 对根层组合返回 400，导致整个工具列表被拒绝；这是兼容性用户报告，不是官方规范保证。条件必填、互斥与工作台表单的封闭字段必须保留运行时拒绝，不能仅删除 schema 约束。
- 同一概念在全部工具中只有一种规范形式；文件与目录用不同的参数名区分（例如 `exportDirectory` 与 `exportFile`）。
- 旧参数名在仍有使用的地方保留为有文档的别名，只保留一个主版本，使用时在 `meta` 中给出弃用警告；不得静默改变旧参数的含义。

### 6.5 错误与诊断编号

- MUST：错误对象符合第 4.6 节；参数错误在 `details.parameter` 中指出出错的参数，`parameters` 列出全部出错字段，枚举错误给出 `allowedValues`。
- 现状偏差：schema 校验失败时 `parameter` 有时只写 `arguments`，部分枚举错误没有列出允许值，个别消息指错了领域（API-04、API-14）。
- 已决定、由 DES-4 实施（维护者 2026-11-01，依据 [Azure REST API Guidelines 的 Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors)）：每个 `INVALID_ARGUMENT` 用 JSON 指针写出准确的参数路径和期望的类型或形式；错误对象增加 `innererror`，承载原生异常类型、HResult 等下层信息，字段名和位置由 DES-4 定稿并同步修改第 4.6 节“恰好三个字段”的规则；`details` 中的 `target` 与 `parameter` 继续表示出错的对象或参数。
- 已决定、由 DES-4 实施：每个原生失败信封返回去敏后的原生消息首行、原生异常类型、HResult 和关联服务器日志的 `diagnosticId`，堆栈与多行详情只进日志；`GetNativeInvocationLog` 支持按请求号和诊断编号筛选并分页，超出上限的 `take` 返回 `INVALID_ARGUMENT` 并给出上限。现状：只有 `INTERNAL_ERROR` 带 `diagnosticId`，`nativeMessage` 多数为空。去敏沿用 SEC-06。

### 6.6 示例

- MUST：工具参数、调用序列、结果解释和官方出处只在 `reference/tool-examples` 中维护，由 `GetToolUsage` 按发布键返回；兼容入口复用同一数据，不另写规则文档（[真机测试](../manuals/real-machine-testing.md#工具示例库)）。
- MUST：每个注册工具和动作都有示例；示例使用所选发布键的真实 schema，并通过运行时参数别名与互斥校验；项目相关的值（项目、PLC、组、路径）放在绑定和参数来源中，不写成看似通用的常量；`validation` 文字如实写明验证范围，例如“离线预览，原生执行 NOT RUN”。
- MUST：示例带在真实 TIA Portal 上正确的参数，让 AI 客户端第一次调用就能成功（维护者 2026-11-01）；核对结果记入[真机台账](../reference/real-machine-ledger.md)。示例数据中如何标记“已真机核对”尚未决定。
- 修改示例后依次运行 `generate-catalogs`、`generate-tool-usage`，再构建并执行八版检索和功能检查。

### 6.7 吞异常的记录

`catch` 的 `swallow(<类别>)` 标注与棘轮见[开发规范](development.md) E-06、E-09；本节只规定运行时的记录方式。

- MUST：`SwallowedExceptions.Note(site, exception)` 只记录位置、异常类型全名和 `HResult`，不读取 `Message`、不调用 `ToString()`；每个位置每进程首次记录，之后只计数；自身从不抛出。
- MUST：输出默认关闭；`TIA_MCP_LOG_SWALLOWED=1` 让 PLC worker 和 Studio 桥接每个位置写一行 stderr。这些诊断不写入调用日志、日志计数器或任何 `meta` 字段，因而不进入 MCP 响应。
- MUST：日志自身失败的 `catch` 和出于隐私丢弃可能含工具输入的 stderr 的 `catch` 保持原样；真机验收前不把原生路径的 `catch` 改为上抛。

### 6.8 契约变更流程

1. 写或更新工具规格，在审查中确认分类、审批、发布键范围和破坏性变化。
2. 修改声明与实现；需要时修改 `reference/tool-examples` 与 `reference/tool-catalog`。
3. 依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`；迁移表有变化时运行 `generate-release-migration`；各命令的 `-Check` 必须通过。
4. 构建后重新捕获契约与响应基线，用 `compare` 列出全部差异（[验证分层](../manuals/validation.md)“工具契约兼容检查”）。
5. 在任务报告和合并审查中逐项解释差异；破坏性变化另写入发布说明与迁移表（第 4.13 节）。

### 6.9 评审清单

- [ ] 每个注册的发布键上至少有一个动作有官方 API 证据。
- [ ] 名称的动词在闭集中，描述以 `[层][域][操作]` 开头，MCP 文本为英文。
- [ ] 显式分类和四个注解已登记；写入默认预览、经审批，预览不写入；目标身份参数齐全。
- [ ] 参数符合第 6.4 节，新参数不使用 `""` 哨兵。
- [ ] 参数错误指出具体参数；原生失败不泄露堆栈和凭据。
- [ ] 每个动作都有示例，`validation` 如实写明验证范围。
- [ ] 生成器 `-Check` 通过，契约与响应差异已逐项解释。
- [ ] 失败路径有测试（[开发规范](development.md)第 5.6 节“必须覆盖的失败路径”）。

## 7 版本差异处理

八个发布键由 `TiaVersionCatalog` 统一解释；原版 V14、V15 不视为 V14 SP1 或 V15.1 的别名，服务包与小版本键也不是主版本号的别名。1.1 [领域 C](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/c-vendor-api.md) 的版本规则（VND-06–VND-08）在此适用。

| 规则 | 位置 |
|---|---|
| MUST：一个进程只使用一个准确的 API 身份；解析器核对所选目录和程序集身份（包括 V21 拆分的程序集）。选择发布键不转换 XML、不升级项目，也不让该版不可用的工具变得可用 | `src/Logic/Siemens/TiaVersionCatalog.cs`、`src/Shared/TiaPublicApi.props` |
| MUST：注册、发现、用法、桥接和被动诊断共用逐工具、逐发布键的支持表；部分动作缺 API 的工具保留，并在原生调用前对该动作返回 `UNSUPPORTED_CAPABILITY`；已知但本版不可用的名称与完全未知的名称可区分 | `src/Adapters.Contracts/PortedFamilies.cs` |
| MUST：编译期差异只用 `src/Shared/TiaFeatures.props` 的特性符号表达（如过程映像服务只在 V21），适配器按精确 PublicAPI 编译八次；运行期不靠工具名或相邻版本推断能力。`TiaFeatureProfile` 选择消费方：`Adapter` 启用 `PLC_*`，`Studio` 启用 `STUDIO_*` | `src/Shared/TiaFeatures.props` |
| MUST：特性符号表的任何变化都要显式审查期望差异：`check-tia-features` 在 Release 配置下求值八版 `DefineConstants`，与提交的期望比较，不还原、不编译、不加载 Siemens API；捕获只写候选文件，从不自动更新期望 | `scripts/checks/tia-feature-expectations.json` |
| MUST：产品选件、许可证和对象级支持是独立于版本可用性的检查；被动的 `InitializeEnvironment` 与 `RunCapabilitySelfTest` 只检查配置、注册表形态和操作映射，不探测原生就绪，也不调用 worker；缓存的 `GetSessionState` 不能证明原生连接存活 | 宿主被动诊断 |
| MUST：工具名不是 API 契约；参数名、预览与确认输入、信封和错误以所选宿主的 schema 和 `GetToolUsage` 为准；各版工具数量与排除项见[版本工具矩阵](../reference/version-tools.md)，工作流缺口见[功能矩阵](../../reference/version-feature-matrix.json) | 生成目录 |
| MUST：新增能力同时更新类型化路由、准入、统一示例和有意义的功能检查 | 第 6.8 节 |

格式与导入边界：

- UDT 与全局 DB 声明构造器按准确的版本格式输出，并对照对应接口 XSD 片段检查；整文档与原生验证另行进行；其他共享构造器仍输出 V21 候选并如实说明。
- V14 SP1 的 SCL XML 可能只含接口信息，不能当作完整的程序恢复；改文件扩展名或 XML 版本号不是转换。
- SIMATIC SD 语言支持取决于版本与更新：V20 的 DB、UDT、LAD 与 V21 的 SCL、FBD 和混合语言是不同契约；V20 Update 4 之前的 LAD 导出需要重新生成。
- 导入路径保留原文件字节，不改写版本头；批次顺序和逐项结果显式返回。HMI 与工艺对象导入在进入原生调用后不再改写输入或换重载重试，失败时项目可能已改变；这些包装都不提供通用的事务回滚。
- 编译检查顶层、未分组和嵌套的项目设备，要求所选 PLC 和全部可观测提供者有明确的离线观测；不提供状态的设备列在 `offlineStateNotExposedByDevices`，不断言为离线。项目离线不等于 CPU STOP。
- V14 SP1 的外部源生成返回 void，不能用清单观测伪造原生的生成对象列表；源导入、生成、编译和保存是分开的操作。
- VCI 在 V14 SP1 与 V15.1 不可用；V16–V19 用旧的工作区映射 API，V20/V21 用新的映射对象 API。SDK 签名本身不证明导出导入往返完整。

绑定快照：`BindingSnapshotContract`、`BindingSnapshotProcessSource` 与 `BindingSnapshotAdapter` 是已测试的观测原语，但 worker 生命周期派发尚未调用它们，附着仍只凭 PID；Siemens 的 `AcquisitionTime` 只是诊断快照时间，不是进程创建时间。接入时 MUST 在所属线程上用 Begin/Complete 包住实际的附着、绑定和解绑，附着前取得进程启动锚点，每次生命周期转换（包括同路径重新绑定）都递增纪元，结果未知或中断时使会话故障；未知或弱观测不得转成“未绑定”成功；DTO 转换在对应 worker 中进行，只用观测到的状态，不用请求提供的身份。

## 8 生成框架

本节只保留生成框架的长期规则；方案比较、任务拆分和验收场景见已归档的[项目生成框架](../archive/development/design/project-generation-framework.md)与[分阶段生成执行设计](../archive/development/design/p8-31-staged-generation.md)，任务状态见[开发计划](../plan/plan.md)。

### 8.1 标准包与机器描述

| 规则 | 依据 |
|---|---|
| MUST：标准包是“目录 + JSON 清单与分部文件”，分发时整个目录无损打成 zip；包清单与分部只用 JSON，按 [`reference/schemas/standards/v1`](../../reference/schemas/standards/v1/README.md) 的 JSON Schema 2020-12 校验，并做静态交叉引用检查。包用 SemVer，格式用整数 `schemaVersion` | ADR 待补写 B22 |
| MUST：规则只用声明式结构（`forEach`、`when`、命名模板）和受限占位符（字段路径与白名单函数）；不执行包内代码，不用正则替换链、任意表达式或网络访问；包可以带 SCL、库文件等资源，但不运行它们。复杂逻辑放在包的 SCL 源或库类型中 | ADR 待补写 B22；SEC-08 |
| MUST：`extends` 与 `dependsOn` 用包 id 加版本范围；派生包按条目 id 整条替换，不做深层合并；`parts.rules` 是精确文件路径列表，不展开 glob | `src/Logic/Generation/` |
| MUST：加载限制默认 1,024 个文件、2,048 个条目、单文件 16 MiB、总量 64 MiB、压缩 zip 32 MiB；路径最多 240 个 UTF-16 单元、16 段，NFC、正斜杠，拒绝越界、链接、Windows 保留名、大小写重复和文件与目录冲突；JSON 是无 BOM 的严格 UTF-8，拒绝重复键和非法 Unicode，深度最多 64；zip 只在内存中读取，不解压 | SEC-08 |
| MUST：哈希用精确十进制的规范 JSON（键按 ordinal UTF-16 排序；有意不采用浮点 RFC 8785）；包哈希包含清单及全部文件的排序摘要；计划哈希只排除根 `planHash`；布局换行不影响哈希，字符串和 SCL 中的换行属于内容 | `src/Logic/Generation/CanonicalJson.cs` |
| MUST：机器描述 `tiamcp.machine/1` 是唯一的生成输入；由包导出的 xlsx/CSV 模板和工作台表单都只是同一份 JSON 的来源或编辑器；`type` 必须是所选包的设备类型，`params` 按该类型的 schema 校验，IO 与 IP 可写 `auto` 由包的分配规则确定 | ADR 待补写 B22 |
| MUST：内置包放在 `templates/standards/<id>/`，用户导入的包放在 `<数据根>/standards/<id>/<版本>/`。仓库只放自有内容和开放许可内容；SAF、LGF、LPMLV30、VASS、SICAR 等专有标准不入库，只能由用户自备后作为外部引用或私有包；ISA、IEC、VDI 和 OPC 规范只按公开信息自写实现，不转载原文 | ADR 待补写 B23 |

首批包是中性的 `tiamcp.basic`（八版通用的 SCL 源）和按 ISA-88/PackML 公开状态模型自写的状态模型包（不声明认证）。维护者自用编程规范是否作为第一个企业包试点尚未决定（ADR 待补写 B27）。

### 8.2 规划与执行

流程是：`PlanProjectGeneration`（只读）→ 预览 → `ApplyGenerationPlan`（一次审批）→ `GetGenerationRun` → `PublishGenerationRecord`。编排器在宿主内，八版相同，步骤是否可用取决于该版目录（`src/Shared/Host/GenerationExecutionTools.cs`）。

| 编号 | 必须保持的安全性质 |
|---|---|
| S1 | 每个写入都由该工具自己的前置检查在写入前一刻对照真实状态检查；预览哈希相等仍由工具判断，执行器只能多拒，不能少拒 |
| S2 | 人审批的是实际会发生的事：目标、内容、要新建或可能被覆盖的对象、调用的原生操作、写入的文件 |
| S3 | 结果未知即停，锁定会话，不自动重试，不重放；运行记录给出核对建议 |
| S4 | 每一步都进审计（`request`、`start`、`end`，带 planId、stepId、runId、parentRequestId），运行记录能逐步重建 |
| S5 | 没有旁门：每步都经 `CallToolCore` 和宿主准入、版本门禁、Openness 通道与调用日志；授权不能从 MCP 参数、管道元数据或批次中伪造 |
| S6 | 每次 Apply 只审批一次；需要阶段之间检查时按阶段分几次 Apply |
| S7 | 版本可用性在规划时标注，含不可用步骤的阶段整体跳过并在结果中列出 |

- 规划只用 READ 工具读回完整清单（分页读完），不写项目，返回 `tiamcp.plan/1`：planId、planHash、按阶段的步骤、`observed`（规划时观测到的源名与对象键集合）、每步可选的 `review` 块、产物哈希。计划属于当前 MCP 会话和绑定；没有运行时，同样的输入得到同样的哈希；计划种子含该项目最近一次运行的 `{runId, status}`，任何运行之后重新规划都得到新的 planId。
- **决定性字段**（目标、文件内容哈希、源名、要新建的对象、禁止覆盖的对象、原生调用种类、写入的文件路径）全部在计划中确定并由人审批。**见证字段**（工具预览哈希、会话内源标识、清单哈希、修改时间、绑定纪元）只证明“状态没变”。唯一可迟填的参数是 `expectedPlanHash`，由该工具在执行时自己的预览给出。
- 需要预览哈希的写步骤，执行前先经 `CallToolCore` 调用该工具自己的只读预览，按逐工具规则投影后与已批准的预期比较：决定性与预测字段必须相等，见证字段只检查约束，出现禁止项即发散；比较通过才填入 `expectedPlanHash`，用覆盖最终参数的单步授权执行。单步授权只在进程内、一次只对一步，去掉迟填槽后的参数摘要必须等于审批过的摘要。写后按声明核对效果。
- 运行事实只来自本运行内成功步骤的结果和预览，按 `stepId.字段` 引用；生成只针对本运行导入的源，上次运行遗留的已归属源先删除再重新导入。
- 运行期间同一会话中不属于本运行的写调用（包括附着的工作台面板）被写栅栏拒绝（`PRECONDITION_FAILED`，`generation-run-active`），读调用不受影响；跨会话由 TIA 进程租约保护。取消只在步骤之间生效，状态为 `cancelled`，已完成的写入保留。
- 计划的 10 分钟期限只约束审批前与首个写入前；批准后的运行另有 4 小时和 10,000 步上限。计划审批载荷 `PlanJson` 内联在审批帧中（≤ 1 MiB），超限时在规划时要求按阶段或单元拆分。
- 新建项目在同一次运行内换绑：`CreateProject` 的项目文件按 `MutationIdentityPolicy` 预测（`<目录>\<名称>\<名称>.ap<主版本>`），后续步骤预先绑定这个路径；建成后核对同一进程与启动时间、路径一致、绑定纪元前进、读回为空项目，全部通过才采用新绑定。建成的项目在失败时保留，没有删除项目的工具。

| 停止类型 | 是否已写 | 运行状态 |
|---|---|---|
| 预览被拒 | 否 | `rejected-before-operation` |
| 发散（固定或预测字段不符、出现禁止项） | 否 | `diverged` |
| 工具 apply 时重算哈希不等 | 否 | `rejected-before-operation` |
| 效果超出或少于声明 | 是 | `effect-diverged`，Apply 为 partial |
| 新建项目后核对不符 | 是 | `diverged`，Apply 为 partial |
| 结果未知或派发异常 | 可能 | `unknown`，会话锁定 |
| 步骤之间取消 | 不适用 | `cancelled` |

发散后只停止：结果给出 `nextAction: "replan"` 与差异证据，重新规划得到只含剩余步骤的新计划，再审批一次；不就地续批。

### 8.3 运行记录、归属与清理

- `<数据根>/generation/runs/*.jsonl` 的只追加 `tiamcp.run/1` 记录是事实来源，宿主重启后仍可由 `GetGenerationRun` 读取；被中断的步骤记为 `unknown`，从不重放。运行事件包括步骤预览、发散、事实、换绑、写栅栏获取与释放和取消；预览是只读调用，只进调用日志。写步骤审计行的 `planHash` 只写工具自己的哈希，生成计划哈希只放在父 Apply 行和运行记录里。
- 生成记录 `tiamcp.generation-record/1` 由宿主 FILE 工具 `PublishGenerationRecord` 写到项目文件夹内、与 `.ap<主版本>` 同级的 `<项目>.tiagen.json`（归档与恢复、VCI 不受影响待真机确认，否则改到项目文件夹旁）。它是计划中可见的最后一步，同样预览、核对前一份记录的哈希、走单步授权并进审计；写入用同目录临时文件加原子替换，拒绝重解析点。记录缺失或 `recordHash` 校验失败时，规划用运行日志重建候选归属。
- 每个生成对象记录规划期的期望指纹和执行后的原生见证：V20/V21 用 `GetPlcObjectFingerprints`，V14 SP1–V19 用块与类型信息的修改时间（稳定性待真机确认，不稳定时改用去时间戳的导出哈希）。见证在保存之后统一采集。
- 已有项目只补缺，不覆盖：不存在的对象规划为创建；已归属且见证未变的对象跳过；已归属但见证已变、期望指纹已变、结果未知或无记录且不一致的对象报告冲突，不覆盖；结果未知的对象经人核对后可在 `PlanProjectGeneration` 的 `adopt` 中列出并转为已归属。
- 外部源一律导入程序块根组，块分组由后续的移动步骤完成。编译成功后、保存前的清理阶段删除本运行导入的外部源（经事实链核对标识），内容哈希和产物留在数据目录。
- 未执行计划的暂存产物在计划过期 24 小时后、下一次规划时清理（只清理没有被运行引用的目录）；已执行运行的产物作为证据保留，由 `ManageGenerationArtifacts` 显式清理；运行日志和已消费声明与审计链一样不自动删除；备份路径写入运行记录。

## 9 偏差登记

本节登记 V4 协议相对 MCP 规范（`MCP-nn`）、Microsoft REST API Guidelines（`API-01`–`API-17`）和自身一致性（`API-18`–`API-39`）仍未关闭的偏差；编号不重复使用。逐条的基线、证据与成本分析见已归档的 [V4 协议偏差清单](../archive/development/design-spec/deviations/protocol.md)。

待修改：

| 编号 | 偏差 | 状态 |
|---|---|---|
| API-01 | 错误对象没有 `target` 路径与 `innererror`；定型 `details` 相当于 innererror 加 target | 维护者 2026-11-01 决定按 Azure 错误模型补充，DES-4 实施（第 6.5 节） |
| API-04 | 多字段校验错误没有逐字段列表，`parameter` 有时只写 `arguments` | P8-60 已合并，beta.2 真机仍复现；VMFIX-1 与 DES-4 |
| API-12 | 没有弃用信号 | 已决定，未实施（第 4.13 节） |
| API-14 | 错误码被当作兜底：旧实现失败报 `INVALID_ARGUMENT`，参数格式错误报 `NATIVE_OPERATION_FAILED`，未满足完成条件报 `INTERNAL_ERROR` | P8-60 已合并，beta.2 真机仍复现；VMFIX-1 与 DES-4 |
| API-18 | 预览绑定参数有多种叫法，规则是 `expected<对象>Hash` 与 `previewToken` | P8-60 已合并，beta.2 真机仍复现；VMFIX-1 与 DES-4 |
| API-19 | `expectedProject` 有时被当作项目文件 | P8-60 已合并，beta.2 真机仍复现；VMFIX-1 |
| API-25 | `requiresSessionReset` 与实际锁定不一致 | P8-60 已合并，beta.2 真机仍复现；VMFIX-1 |
| API-35 | 可选字符串参数用 `""` 作为缺省哨兵 | 原为有意偏差；维护者 2026-11-01 决定由 DES-4 改为省略即未提供 |

4.x 迁移到 MCP 现行修订时处理：

| 编号 | 偏差 | 处理 |
|---|---|---|
| MCP-01 | 会话锁、租约、绑定、计划、令牌和句柄都以 MCP 传输会话为作用域，恢复步骤要求开新 MCP 会话；2026-07-28 是无状态协议 | 设计服务器签发的绑定句柄（如 `ConnectPortal` 返回 `bindingId`），租约和锁以绑定为键；“V4 会话”的定义见第 4.1 节 |
| MCP-05 | 长操作、审批等待和生成运行同步阻塞一次 `tools/call`，未使用 tasks 扩展 | 先为 `ApplyGenerationPlan`、编译和上下载提供 tasks 方式，运行状态词表与 tasks 对齐；之前由进度心跳缓解 |
| MCP-09 | 大结果用带内导出句柄，未使用 `resource_link` | 评估在信封之外同时返回 `resource_link`，句柄保留以兼容 |
| MCP-12 | 不使用 `_meta`（serverInfo、traceparent），`requestId` 只由服务器生成 | 随 SDK 升级接受 `traceparent` 并写入审计，结果 `_meta` 给出 serverInfo，批次子项记录 `parentRequestId` |
| MCP-14 | 没有 `title` 与 `icons`，分类标签写在描述中 | 在检索不再依赖描述前缀后补 `title` |
| MCP-15 | SDK 只支持到 2025-06-18 | 4.0 发布说明写明修订；SDK 支持后迁移，同时处理 2026-07-28 的 `resultType`、`tools/list` 的 `ttlMs` 与 `cacheScope`、MRTR 和 tasks 扩展 |

有意偏差（已接受，规则已写入本文）：

| 编号 | 偏差 | 规则 |
|---|---|---|
| MCP-04 | `structuredContent` 内另加一层 V4 信封 | 第 4.2、4.3 节 |
| MCP-07 | 派发前取消仍返回 `CANCELLED` 结果，派发后不响应取消 | 第 4.11 节 |
| MCP-08 | 工具结果的分页不遵循 MCP 列表分页约定，保留 offset 模式 | 第 4.8 节 |
| MCP-11 | “工具不可用”有三种表达：直接调用为 JSON-RPC `-32602`，`CallTool` 为 `UNSUPPORTED_CAPABILITY` 或 `TOOL_NOT_FOUND` | 第 4.2、4.12 节 |
| MCP-13 | 审批在带外的可信工作台进行，不用 MCP 确认或 elicitation；以后可用 MRTR 或 tasks 只做“请到工作台审批”的提示 | 第 4.10 节 |
| MCP-16 | 没有显式调用限流，以通道串行与数量上限代替；HTTP 宿主对多用户开放时再加限流 | 第 2.1、4.12 节 |
| API-02 | 错误码用 UPPER_SNAKE | 第 4.3 节 |
| API-05 | 始终输出 null，不省略字段 | 第 4.3 节 |
| API-06 | 集合与分页形状：`nextOffset`/`nextCursor` 对应 nextLink，`items` 对应 value | 第 4.8 节 |
| API-09 | 没有可重复请求标识，采用“至多一次 + 锁定 + 人工核实” | 第 4.11 节 |
| API-10 | 条件写用 `expected*` 参数，读取不返回 ETag | 第 4.10 节 |
| API-11 | 进程级版本与 4.0 硬切换，4.x 只做增量 | 第 4.13 节 |
| API-16 | `Manage*` 多路复用工具，注解取保守值 | 第 6.2、6.3 节 |
| API-17 | 警告放在响应体中 | 第 4.7 节 |
| API-20 | 既有的 `confirm*` 与 `allowWrite` 确认开关，新工具只用 `confirm`，行为族切换到 safe-v4 时统一 | 第 4.10 节 |
| API-26 | `TIMEOUT` 不用于已派发的调用，worker 超时一律为 `unknown` | 第 4.11 节 |
| API-29 | `data` 中残留旧的判定字段，以 `meta.outcome` 为准 | 第 4.5 节 |
| API-32 | `CallTool` 派发前的字段级错误指向目标工具，其余指向 `CallTool` | 第 4.2 节 |
| API-34 | 状态词 kebab-case、输入选择值 camelCase、原生名称原样，三套并存 | 第 4.3 节 |
| API-39 | 发布配置中的预览返回旧数据形状，`data.plan` 只在 safe-v4 族切换后保证 | 第 4.10 节 |

以下条目由 P8-60 修改并已合并，对应规则已写入本文，逐项核对后从登记中删除：MCP-03（统一的信封输出 schema）、MCP-06（宿主进度心跳）、MCP-10（工具名精确匹配）、API-03（消费者容忍未知字段与错误码）、API-07（分页参数 `limit`）、API-08（offset 分页的一致性说明）、API-13（时间戳格式）、API-15（字符偏移单位）、API-21（计划哈希格式）、API-22（计划过期一律 `PLAN_STALE`）、API-23（details 词表）、API-24（省略 null 的例外）、API-27（取消的映射）、API-28（后加装饰也经信封校验）、API-30（导出句柄表示）、API-31（分页单位与页级完整度）、API-33（CLI 退出码覆盖规则）、API-36（未使用的类型与错误码）、API-37（批次聚合错误码）、API-38（动词检查覆盖全部名称）。
