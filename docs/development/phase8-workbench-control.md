# P8-20 / P8-21 设计：工作台控制通道与共享会话操作面板

- 日期：2026-10-08；基线：master `f02b4cdd`（P7-11a 改名已在 `9588be3e` 合并，重构计划表中 P7-11a 仍标 todo，需顺手更新）。
- 范围：设计与分步实施记录。任务来源：[重构计划](refactor-plan.md) P8-20、P8-21；
  [路线图](roadmap.md)“阶段 8”第 88–94 行。
- 文中 `文件:行` 均以基线源码为准；标识符保持英文。

## 0 结论摘要

| 问题 | 推荐 |
|---|---|
| 控制通道传输 | 新的本机命名管道 `TiaMcp.Workbench.v1.<sha256>`，方向与审批管道相同（工作台为服务端，MCP 宿主为客户端），每次调用一条连接、一请求一响应；复用审批管道的 DACL、拒绝远程、首实例与双向 SID 核对（抽成共享 `LocalPipeSecurity`），协议、DTO、管道名与审批完全分开 |
| 工具 | 8 个宿主工具：`ShowWorkbenchPage`、`ShowWorkbenchBlock`、`ShowWorkbenchCall`、`ShowWorkbenchLadder`、`ShowWorkbenchAtlas`（UI 类）；`GetWorkbenchState`、`GetWorkbenchSelection`（READ）；`PrefillWorkbenchForm`（UI 类）。不派发 worker、不进审批、不写审计链，八版同一实现 |
| 红线 | 协议操作是封闭枚举，没有审批、开关、服务启停或工程执行的消息；工作台端处理器只拿到窄接口 `IWorkbenchControlSurface`，构造不到 `IApprovalService`、`ConfigurationView`、`IStudioClient`；预填只开放执行动作“无副作用或经 MCP”的表单；以反射和源码扫描测试固定 |
| P8-21 面板传输 | 宿主新增“操作端端点”（operator endpoint）：每个宿主进程一条同用户命名管道，工作台作为客户端，握手后在管道上跑 MCP JSON-RPC（SDK 的 `StreamServerTransport`/`StreamClientTransport`），调用被路由到所选会话已有的工具对象，因此与 AI 走同一 `FoundationV4Tool` 审批、调用日志、审计、lane 和 worker；“人/AI”由传输决定（`ActorScope`），不取自任何参数 |
| P8-21 会话 | 默认附着到已有的 AI 会话（共享该会话的 worker）；没有 AI 会话时，工作台在自己启动的宿主里建“工作台自有会话”。第一期不允许 AI 加入工作台自有会话；V14 SP1–V19 补上与 V20/V21 相同的 TIA 进程租约，八版都拒绝两个 worker 附着同一 TIA |
| 删除 | P8-21 完成后删除 `TiaOpenness.Bridge`、`TiaOpenness.Client`、Core 的 RPC/适配/会话抽象/Mock、`src/Studio/Openness/**` 八个适配器工程与 `src/Adapters/Native/Studio` 中的 Studio 会话实现；VCI 实现按 F33 迁入 Foundation 适配器而非删除 |

## 1 现状

### 1.1 审批管道

| 方面 | 实现 | 位置 |
|---|---|---|
| 源码归属 | `SessionBehavior`、`ApprovalSettings`、`ApprovalProtocol`、`ApprovalPipe`、`ApprovalResult` 以链接方式编译进 `TiaMcp.Logic`（net48;net10.0）与工作台 GUI | `src/Shared/ApprovalSources.props:3-7`；`src/Logic/TiaMcp.Logic.csproj:3`；`src/Studio/Gui/TiaOpenness.Gui.csproj:3` |
| 管道名 | `TiaMcp.Approval.v1.` + SHA-256(当前用户 SID + `\n` + 设置文件绝对路径大写)，设置文件为 `<config>/approval.settings` | `src/Shared/ApprovalPipe.cs:33-35`；`src/Shared/ApprovalSettings.cs:15` |
| 安全描述符 | `O:<SID>D:P(A;;GA;;;<SID>)`：受保护 DACL，只授权当前 SID | `src/Shared/ApprovalPipe.cs:36` |
| 管道标志 | 双工 + 重叠 I/O，首实例时加 `FILE_FLAG_FIRST_PIPE_INSTANCE`，`PIPE_REJECT_REMOTE_CLIENTS`（pipeMode 8），字节模式，最多 255 实例，64 KiB 缓冲 | `src/Shared/ApprovalPipe.cs:38-52` |
| 双向身份核对 | 服务端（工作台）读完首帧后 `RunAsClient` 取连接方 SID（客户端必须以 `TokenImpersonationLevel.Identification` 连接）；客户端（宿主）用 `GetNamedPipeServerProcessId` + 进程令牌核对服务进程 SID | `src/Shared/ApprovalPipe.cs:53-68`；工作台调用点 `src/Studio/Gui/Services/ApprovalService.cs:110-111`；宿主调用点 `src/Shared/ApprovalPipe.cs:144` |
| 帧格式 | 4 字节小端长度 + UTF-8 JSON，单帧上限 1 MiB；拒绝重复字段、缺少必需字段，DTO 标 `JsonUnmappedMemberHandling.Disallow` | `src/Shared/ApprovalProtocol.cs:144-184`、`19-20`、`125-133` |
| 请求 | `PendingApproval` v1：`RequestId`（宿主生成）、`Host`、`ReleaseKey`、`Tool`、`PlanHash`、`ArgumentDigest`（完整参数与身份的规范 JSON 摘要）、脱敏后的显示参数（单值 4096 字符、128 项、64 KiB 界限）、`Deadline`；`Validate` 检查版本、哈希形态、超时 1–3600 s | `src/Shared/ApprovalProtocol.cs:20-113` |
| 决定 | `ApprovalDecision` 必须匹配 `RequestId`、`PlanHash`、`ArgumentDigest`，只有 granted/denied | `src/Shared/ApprovalProtocol.cs:133-142` |
| 宿主生命周期 | 每个待批写调用新开一条连接：250 ms 连接超时、核对服务端 SID、写请求、等决定；同一 `RequestId` 只允许一次（`Used`）；迟到、不匹配、格式错误一律 denied；超时/无工作台分别为 timeout/workbench-unavailable；结果写审计链 | `src/Shared/ApprovalPipe.cs:120-161` |
| 结果通知 | 执行结束后宿主再开一条连接发 `Kind=result` 的同一请求（1 s 限时），工作台只更新该行状态，不能授予批准 | `src/Shared/ApprovalPipe.cs:102-118`；`src/Studio/Gui/Services/ApprovalService.cs:112-123` |
| 工作台生命周期 | 构造时创建首实例并开始监听；每接受一条连接，先建好下一个实例再处理；读首帧 5 s 上限；最多 50 个待批；等待期间监视宿主断连；关闭时全部待批记为 Disconnected | `src/Studio/Gui/Services/ApprovalService.cs:35-42`、`86-100`、`101-151`、`182-191` |
| 宿主接入点 | Foundation：`FoundationV4Tool.InvokeAsync` 先按写分类决定是否审批，`InvokeCoreAsync` 生成 `PendingApproval`、做预检（preview）、再 `ApprovalClient.Wait`；批准后重新计算摘要，目标变化则拒绝 | `src/FoundationHost/FoundationV4Tool.cs:169-176`、`183-205`、`250-307`、`316-322` |
| 写分类 | candidate 只在 `mode=apply` 时审批；有 dryRun 的按显式 false；另外 Save/SaveProjectCopy/Close 强制审批 | `src/Logic/V4/HostBehavior.cs:18-29` |
| 开关 | `approval.settings` 两行文本；缺失或损坏时视为开启；变化写 `approval-switch` 审计事件；MCP 导出工具不能写这个文件（`IsAdministrativeTarget` 按文件 ID 比较） | `src/Shared/ApprovalSettings.cs:16-40`、`53-88`；`src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs:143-151` |
| 工作台界面 | 状态栏菜单与设置抽屉开关审批，关闭需确认覆盖层；`IApprovalService` 提供 Enabled/Timeout/Approve/Deny | `src/Studio/Gui/MainWindow.xaml:116-121`、`166-178`；`src/Studio/Gui/WorkbenchShell.cs:173-188`；`src/Studio/Gui/Services/IApprovalService.cs:7-18` |
| 威胁模型 | “AI 经 MCP 工具越权写入”：MCP 客户端不能经参数、元数据或批次自批；不防同一用户下的恶意本机进程 | `docs/development/runtime-layout.md:40-44`；`docs/development/phase6-review.md:335-336` |

审批行在工作台显示时 `Client`/`Transport` 固定为 `"MCP"`（`ApprovalService.cs:132`），当前没有“人发起的写”这一来源。

### 1.2 工作台结构、页面与视图模型

进程与工程：

| 组成 | 说明 | 位置 |
|---|---|---|
| `TiaOpenness.exe`（Gui） | net10.0-windows WPF；编译审批与审计共享源码；引用 `TiaMcp.Logic`（仅用于渲染梯形图/图集）；构建后把桥接复制到 `bridge/` | `src/Studio/Gui/TiaOpenness.Gui.csproj:1-31` |
| `TiaOpenness.Bridge.exe` | net48 x64，STA 主线程，WorkerChannel `ChannelProfile.Studio` 服务端，经 stdin/stdout 与 GUI 通信 | `src/Studio/Bridge/Program.cs:16-71`；`src/Studio/Bridge/TiaOpenness.Bridge.csproj:2` |
| `TiaOpenness.Client` | `BridgeClient` 启动桥接（nonce、桥接与适配器哈希、首次附着超时提示），`TiaClient` 为类型化 RPC 包装 | `src/Studio/Client/BridgeClient.cs:67-153`、`189-233`；`src/Studio/Client/TiaClient.cs:13-190` |
| `TiaOpenness.Core` | RPC 分派、会话抽象、Mock 会话、环境检查、检查规则；net48 与 net10 双目标；`TiaSharedAdapterPaths` 决定走哪条适配路径 | `src/Studio/Core/TiaOpenness.Core.csproj:3-12`；`src/Studio/Core/Rpc/RpcDispatcher.cs:99-195` |
| `TiaOpenness.Openness`（八版） | `src/Studio/Openness/V*/` 每版一个工程，`Studio.Common.props` 链接 `src/Adapters/Native/Studio/OpennessSession.cs` 等文件并引用对应 PublicAPI | `src/Studio/Openness/Studio.Common.props:14-25`；`src/Studio/Openness/OpennessSessionFactory.cs:15-48` |
| 共享适配路径（开关） | `TiaSharedAdapterPaths=true` 时桥接改用 Foundation 适配器程序集里的 `StudioAdapter` | `src/Studio/Core/Abstractions/SessionFactoryLoader.cs:13-33`；`src/Adapters/Native/Studio/StudioAdapter.cs:15-90` |

页面与导航（`MainWindow.xaml` + `WorkbenchShell.cs` 分部类）：

- 一级页签：`Mcp`（MCP 与客户端）、`Engineering`（工程操作）、`Calls`（AI 调用）、`Audit`、`Environment`、`Log`（`MainWindow.xaml:64-80`）；
  工程操作下的二级页签：`Engineering`（概览，即 `ProjectOperationsView`）、`Blocks`（程序块）、`VersionControl`（`MainWindow.xaml:81-87`）。
  中文标签见 `src/Studio/Gui/Localization/Strings.cs:22-36`。
- 导航入口只有 `MainWindow.Navigate(string page)`：`Blocks`/`VersionControl` 需要已打开工程，切页时收起各页选项层、切换可见性并同步页签（`WorkbenchShell.cs:94-122`）；
  快捷键 Ctrl+1…8 经 `WorkbenchCommands.Navigate`（`MainWindow.xaml:5-16`、`WorkbenchShell.cs:124-131`）。
- 抽屉：`Settings`、`Approvals`、`CallDetail`（`WorkbenchShell.cs:140-172`，`MainWindow.xaml:138-165`）；关闭审批的确认覆盖层（`MainWindow.xaml:166-178`）；
  审批 toast 与最小化时的系统通知（`WorkbenchFeaturePages.cs:60-71`）。
- 视图模型：`MainViewModel` 组合 `SessionViewModel`、`EngineeringViewModel`、`VersionControlViewModel`、`WorkbenchActivity` 与 `AtlasService`（`src/Studio/Gui/ViewModels/MainViewModel.cs:19-34`）；
  `FeaturePagesViewModel` 承载调用、审计、环境与审批抽屉（`WorkbenchFeaturePages.cs:19-43`）；`ConfigurationView` 承载 MCP 服务配置与启停（`ConfigurationPage.cs:38-53`）。
- 线程：WPF 单 UI 线程。来自后台的变化统一以 `DispatcherPriority.Background` 合并刷新（`FeaturePagesViewModel.cs:187-239`、`WorkbenchShell.cs:51-57`）；
  P6-57 已为界面响应建立 p95 门槛和 `ResponsivenessTests`。

### 1.3 “工程操作”页现在做什么

所有按钮经 `IStudioClient`（`src/Studio/Gui/Services/IStudioClient.cs:10-35`）→ `StudioClient` → `TiaClient` → `BridgeClient` → 桥接进程 STA → `RpcDispatcher` → 每版 `OpennessSession`，
即工作台自己的一条 TIA 连接，与 MCP 会话互不知情。

| 界面动作 | 视图模型 | RPC | 性质 | 审批/审计/调用日志 |
|---|---|---|---|---|
| 浏览、连接/打开工程 | `SessionViewModel.ConnectCoreAsync` / `OpenProjectAsync`（`SessionViewModel.cs:157-173`、`181-199`）；首次使用时启动桥接（`205-217`） | `session.connect`、`project.open` | 会话 | 无 |
| 断开 | `SessionViewModel.DisconnectAsync`（`139-147`） | `session.disconnect` | 会话 | 无 |
| 选择软件、加载块 | `EngineeringViewModel.LoadDevicesAsync` / `RefreshBlocksAsync`（`EngineeringViewModel.cs:127-173`） | `device.list`、`block.list` | 读 | 无 |
| 保存 | `SaveAsync`（`267-279`，仅一个确认对话框） | `project.save` | **写工程** | 无 |
| 导入 | `ImportAsync`（`211-232`，文件对话框 + 覆盖询问） | `block.import` | **写工程** | 无 |
| 编译 | `CompileAsync`（`234-249`） | `compile.device` | 改编译状态 | 无 |
| 导出 | `ExportAsync`（`186-209`） | `block.export` | 写文件 | 无 |
| 检查 | `InspectAsync`（`251-265`），规则在 `Core/Inspection/InspectionEngine.cs` | `inspect.run` | 读 | 无 |
| 查看梯形图 / 生成图集 | `AtlasPresentation.GenerateAsync`（`src/Studio/Gui/Views/AtlasPresentation.cs:51-92`）经 `AtlasService` 导出到 `data/reports` 再本地渲染（`AtlasService.cs:26-50`），用系统浏览器打开（`AtlasPresentation.cs:94-106`，明确不用嵌入式 WebView） | `block.export` | 写文件 | 无 |
| 版本控制页 | `VersionControlViewModel` | `vc.*`，其中 `vc.sync` 工作区→工程会覆盖块 | **可写工程** | 无 |

页面卡片布局见 `ProjectOperationsView.xaml:8-64`（工程卡）、`76-97`（编译）、`98-128`（传输）、`129-151`（检查）、`152-173`（环境）。
写方法在桥接侧被标为非只读（`src/Studio/Core/Rpc/BridgeChannel.cs:30-65`），但这只影响通道的未知结果语义，不经过审批或审计。

### 1.4 MCP 宿主的会话模型（与 P8-21 直接相关）

- 八版 MCP 入口均为 `runtime/v<key>/TiaMcp.FoundationHost.exe`（`src/Shared/BundleLayout.cs:269-276`）。
- V14 SP1–V19：HTTP 每个 MCP 会话在 `ConfigureSessionOptions` 新建一个 `WorkerClient` 和一套工具对象，在 `RunSessionHandler` 结束时释放（`src/FoundationHost/Program.cs:52-67`）；
  stdio 只有一个会话、一个 worker（`Program.cs:83-95`）。HTTP 以 Bearer 密钥鉴权，健康检查例外（`Program.cs:70-80`）。
- V20/V21：同样每个 MCP 会话一个引擎 worker 与 `EngineHostPipeline`（P7-07a，`src/FoundationHost/EngineReleaseHost.cs:78-121`）；
  `SessionTool` 拒绝来自其他 MCP 会话 ID 的调用（`src/EngineHost/EngineHostPipeline.cs:116-126`）。
- worker 内原生调用由 `WorkerClient.serial`（lane）串行（`src/FoundationHost/WorkerClient.cs:34-49`）；Disconnect 结束该会话，之后的 Attach 需要新的宿主会话（`WorkerClient.cs:90-95`）；
  会话的工程身份缓存在 `ApprovalIdentity`（`WorkerClient.cs:153-162`），读取它不调用 TIA。
- TIA 进程租约 `PortalProcessLease`（同一用户、跨所有 MCP 端口与版本）只在 V20/V21 引擎 worker 附着时获取（`src/Engine/WorkerMode/EngineWorkerHost.cs:175-181`、`src/Logic/Siemens/PortalProcessLease.cs:16-35`）；
  V14 SP1–V19 的 PlcWorker 与工作台桥接都不取租约，因此今天工作台桥接与 AI 会话可以同时附着同一 TIA。
- 宿主工具目录：`LegacyHostToolRegistry.Create` 汇总 Foundation 工具、离线构建器、诊断与 `GetToolUsage`，统一包上 `FoundationV4Tool` 与 `UsageHintTool`（`src/FoundationHost/FoundationPassiveDiagnostics.cs:241-263`）；
  V20/V21 经 `SharedToolCatalog` 并入引擎目录，lite 档包含以 V19 注册表计算的必备工具（`src/EngineHost/SharedToolCatalog.cs:30-58`、`EngineReleaseHost.cs:61-66`）。
  不需要 TIA 的工具必须登记在 `ToolTaxonomy.WithoutTia`，否则默认走独占 Openness lane（`src/Logic/ModelContextProtocol/ToolTaxonomy.cs:16-30`）。

### 1.5 共享数据文件

| 文件 | 位置 | 写入方 | 读取方 |
|---|---|---|---|
| `approval.settings` | `<config>/approval.settings`（`ApprovalSettings.cs:15`） | 工作台 | 宿主（每次写调用加载） |
| `journal-retention.settings` | `<config>/`（`src/Shared/JournalRetention.cs:16`） | 工作台审计页 | 宿主调用日志 |
| `http-v<key>.json`、`client.json` | `<config>/`（`ConfigurationView.xaml.cs:75`、`322`、`340`；密钥以 DPAPI 保护，`ConfigCore.cs:47-55`） | 工作台配置页 | 工作台 |
| UI 设置 | `DataLocations.UiFilePath`（`src/Shared/DataLocations.cs:130`，`UiSettings.cs:29`） | 工作台 | 工作台 |
| 审计链 | `<data>/logs/audit/audit-*.jsonl`（`DataLocations.cs:152-155`） | 宿主（写调用、审批）与工作台（开关） | 工作台审计页、CLI |
| 调用日志 | `<diagnostics>/calls-<pid-key>.jsonl`（`src/Shared/InvocationJournal.cs:87-90`） | 每个宿主进程 | 工作台“AI 调用”页（`CallJournalService`） |
| 进程租约 | `LeasesDirectory`（`DataLocations.cs:128`） | V20/V21 引擎 worker | 同上 |
| 报告 | `<data>/reports`（`DataLocations.cs:257`；图集 `AtlasService.cs:23`） | 工作台图集、宿主渲染工具 | 用户 |
| 暂存 | 包内 `staging/`，清单含 `hostPid`、`hostInstanceId`、`mcpSessionId` | 宿主 | 宿主（跨会话列举，`ImportStagingStore.cs:363-380` 的进程存活判断可复用） |

服务启停：配置页以 HTTP 模式启动 Foundation 宿主（`--transport http --http-prefix … --http-api-key …`，`ConfigCore.cs:195-201`；`ConfigurationView.xaml.cs:490-518`），停止即结束进程（`519-527`）。
本地 stdio 模式由 AI 客户端自己拉起宿主，工作台只写客户端配置（`ConfigCore.cs:86-96`、`ClientProfiles.cs:251-256`），并不拥有那个宿主进程。

### 1.6 现状结论

1. MCP 与工作台之间只有审批管道、上述文件与服务进程启停；AI 无法切页、填表或读取界面状态。
2. 工程操作页的保存、导入、编译、VCI 同步经工作台自己的 TIA 连接执行，绕开审批、审计与调用日志；与 AI 会话可以同时附着同一 TIA（V14 SP1–V19 无租约；V20/V21 的租约只约束引擎 worker）。
3. 页面导航已有唯一入口 `Navigate`，视图模型状态集中，适合加一个受限的“控制面”适配层；但目前没有任何界面状态快照或外部调用入口。

## 2 控制通道设计（P8-20）

```text
            MCP 客户端（本机 stdio 或远端 HTTP）
                         │ MCP
                ┌────────▼────────┐  ① 审批管道 TiaMcp.Approval.v1.*   ┌──────────────┐
                │ Foundation 宿主 │ ─────────────────────────────────▶ │              │
                │  （每会话 worker）│  ② 控制管道 TiaMcp.Workbench.v1.*  │   工作台      │
                │                 │ ─────────────────────────────────▶ │ TiaOpenness  │
                │                 │  ③ 操作端端点 TiaMcp.Operator.v1.*  │              │
                │                 │ ◀───────────────────────────────── │  （P8-21）    │
                └─────────────────┘                                     └──────────────┘
```

三条通道各自独立：① 只承载审批（不变）；② 只承载显示、读取、预填；③ 只承载工作台面板以“人”的身份调用 MCP 工具（P8-21）。

### 2.1 传输选型

| 方案 | 结论 |
|---|---|
| 新命名管道，复用审批管道的安全原语 | **采用**。与审批的同用户、拒绝远程、首实例、双向 SID 核对一致；不开端口、不需要 URL ACL 或防火墙；宿主在 VM 中、AI 在远端时同样成立（控制只发生在宿主进程与本机工作台之间） |
| 在审批管道上加消息类型 | 否。违反“与审批管道分开”，也让控制消息与审批决定共用解析器 |
| 工作台开本机 HTTP 监听 | 否。需要 URL 预留与鉴权密钥，暴露面更大 |
| COM/窗口消息 | 否。无法做 SID 核对，测试困难 |

- 方向：工作台为服务端，宿主为客户端（与审批相同）。工作台是用户唯一的界面实例，“谁在控制”由工作台在 UI 上展示。
- 连接模型：每次工具调用一条连接、一帧请求、一帧响应，然后关闭，与审批一致；不保留长连接，工作台重启后下一次调用自动可用。
- 实例：服务端最多 8 个并发实例（防止调用洪泛占满 255 个实例）；首实例创建失败（已有工作台或被占用）时工作台在状态栏显示“控制通道不可用”，不影响审批。

### 2.2 命名、发现与工作台未运行

- 管道名：`TiaMcp.Workbench.v1.` + SHA-256(SID + `\n` + `ApprovalSettings.SettingsPath` 的完整大写路径 + `\n` + `"workbench-control"`)。
  作用域刻意与审批管道相同：凡是审批能配对的宿主与工作台，控制也能配对；开发构建与安装包使用不同数据根时互不串线。
- 发现：宿主不读注册表或文件，直接按名连接，250 ms 超时（同 `ApprovalPipe.cs:142`）。
- 工作台未运行、运行在其他数据根或其他用户下：返回类型化拒绝，不尝试启动工作台（MCP 不应在 VM 桌面上拉起 GUI）：
  `error.code = RESOURCE_UNAVAILABLE`，`details.resource = "workbench-control"`，`meta.outcome = rejected-before-operation`，`meta.execution = not-started`；
  消息为英文，例如 “The TIA Workbench is not running for this Windows user and data root. Start TiaOpenness.exe on the TIA machine, then retry.”。

### 2.3 身份与同用户核对

1. 把 `ApprovalPipe` 中与审批无关的三个原语抽到新文件 `src/Shared/LocalPipeSecurity.cs`：`CreateServer(name, sid, first, maxInstances)`、`PeerIsCurrentUser`、`ServerIsCurrentUser`；
   `ApprovalPipe` 改为委托调用，管道名、标志、DACL 与帧字节不变（由现有 `ApprovalServiceTests`、`ApprovalHostTests` 守护）。
2. 控制管道沿用同一顺序：工作台先读首帧（`ImpersonateNamedPipeClient` 在读到数据前不可用，审批也是先读后验，`ApprovalService.cs:110-111`），再核对连接方 SID，失败直接断开；
   宿主连接后、写请求前核对服务进程 SID，不一致返回 `ACCESS_DENIED`（`operation = "workbench-control"`，`target = "pipe-owner"`）。
3. 加固（推荐，不改变威胁模型）：双方再核对对端进程映像路径——工作台要求客户端是同一包根下的 `TiaMcp.FoundationHost.exe`，宿主要求服务端是同一包根下的 `TiaOpenness.exe`
   （`GetNamedPipeClientProcessId`/`GetNamedPipeServerProcessId` + `QueryFullProcessImageName`，路径来自 `BundleLayout`）。这能挡住开发构建与安装包误配对，但挡不住同一用户的恶意代码，文档照旧写明。

### 2.4 消息契约 v1

帧：沿用 4 字节小端长度 + UTF-8 JSON，单帧上限 256 KiB；拒绝重复字段与未知字段；新文件 `src/Shared/WorkbenchControlProtocol.cs` 定义 DTO 与读写器，与 `ApprovalFrames` 不共享类型，
避免一个解析器的放宽影响另一个。

请求（宿主 → 工作台）：

```json
{
  "protocol": "tiamcp.workbench-control",
  "version": 1,
  "requestId": "<32 位小写十六进制，等于 V4 meta.requestId>",
  "deadlineUtc": "2026-10-08T12:00:05.000Z",
  "origin": {
    "host": "foundation",
    "releaseKey": "19",
    "hostProcessId": 4242,
    "mcpSession": "<SHA-256(MCP 会话 ID) 前 16 位>",
    "clientName": "claude-code",
    "boundProjectFile": "D:\\Projects\\Line.ap19"
  },
  "operation": "display.block",
  "arguments": { "softwarePath": "PLC_1", "blockPath": "Motion/FB_Axis" }
}
```

- `operation` 为封闭枚举：`display.page`、`display.block`、`display.call`、`display.ladder`、`display.atlas`、`read.state`、`read.selection`、`prefill.form`。没有任何其他值；
  未知值、缺字段、多字段都按格式错误拒绝。每个 operation 的 `arguments` 有独立的封闭 DTO（同样 `Disallow` 未知成员）。
- `clientName` 取自 MCP initialize 的 `clientInfo`，不可验证，只用于显示；`boundProjectFile` 取自调用者会话缓存的 `ApprovalIdentity`（不调用 TIA），用于工作台做工程一致性检查。
- `deadlineUtc`：显示与预填为发送时刻 + 5 s，读取为 + 2 s；工作台对超过 `TimeoutSeconds` 上限或已过期的请求直接拒绝。

响应（工作台 → 宿主）：

```json
{
  "protocol": "tiamcp.workbench-control",
  "version": 1,
  "requestId": "…",
  "status": "done",
  "data": { "page": "blocks", "device": { "id": "S71500/ET200MP-Station_1", "displayName": "PLC_1" }, "block": { "path": "Motion/FB_Axis", "kind": "FB", "number": 10 } },
  "refusal": null,
  "workbench": { "version": "4.1.0", "contracts": [1] }
}
```

- `status`：`done` | `refused` | `failed`。`refused` 时 `refusal = { "code": "<V4 错误码>", "condition": "…", "target": "…", "candidates": [] }`；`failed` 表示已开始应用界面变化后出错。
- 版本：宿主发送自己支持的最高版本；工作台不支持时回 `refused` + `code = UNSUPPORTED_CAPABILITY` + `workbench.contracts` 列出支持的版本，宿主不降级重试（同包发布，只在开发或半更新时出现）。
- 宿主侧把响应映射为 V4 信封（`schemaVersion 4`、`ok/data/error/meta`）；`meta.behaviorPolicy = not-applicable`。映射须通过 `V4Validation`，超时和失败都不得锁定 MCP 会话
  （`SessionBehavior.LocksSession` 只对“原生已发出 + 结果未知 + 变更”成立，这里三者都不满足）。

### 2.5 并发：多个 MCP 会话对一个工作台

- 读取（`read.*`）不进入 UI 线程：工作台维护一个不可变快照 `WorkbenchSnapshot`，由 UI 线程在相关属性变化后以 Background 优先级合并发布（复用 `FeaturePagesViewModel.OnChanged/Flush` 的做法），
  读请求在管道线程上直接读最近的快照，可并发、无锁、不受界面忙碌影响。
- 显示与预填进入单一的 `Channel<ControlWork>`（容量 16，FIFO）；每个来源会话同时最多 1 个在途请求；同一来源的切页最短间隔 300 ms（防闪烁）；队列满或违反限制回 `PRECONDITION_FAILED`，`condition = "workbench-busy"`。
- 后到者覆盖前者的界面位置（切页本身无冲突）；预填按表单加锁：一个会话的未确认预填存在时，其他会话对同一表单的预填被拒绝（`condition = "prefill-pending"`），同一会话可以覆盖或清除自己的预填，人随时可以清除。
- 每个响应都带 `requestId`，工作台控制日志用同一 ID，便于在“AI 调用”页把宿主调用记录与界面动作对应起来。

### 2.6 UI 线程

- 监听与收发在线程池（模式同 `ApprovalService.Listen/Handle`）；帧解析、身份核对、参数校验都在 UI 线程之外完成。
- 应用界面变化：后台读取器 `await dispatcher.InvokeAsync(work.Apply, DispatcherPriority.Normal, deadlineToken)`；`Apply` 在 UI 线程上重新检查守卫条件（状态可能已变），只做同步的属性与可见性修改，预算 16 ms；
  不弹模态对话框，不在 UI 线程上 await I/O。打开浏览器（梯形图/图集）在 UI 部分完成后回到线程池执行。
- 截止时间到达时尚未开始的工作直接丢弃并回 `TIMEOUT`（`details.stage = "workbench-ui"`）；已开始的工作完成后照常记录，但宿主那边已按超时返回。
- 窗口关闭（`MainWindow.Closed` → `DisposeShell`）时先停止接受新连接，再以 `refused` + `condition = "workbench-closing"` 结束排队项。
- 把控制操作纳入 P6-57 的 `ResponsivenessTests`：在持续的控制请求下，界面 p95 不得超过现有门槛。

### 2.7 人优先与防误触

- 下列情况拒绝显示与预填（读取不受影响）：工作台“允许 AI 控制界面”开关关闭（`condition = "workbench-control-disabled"`）；有模态对话框、确认覆盖层、设置或审批抽屉打开（`"workbench-modal-open"`）；
  最近 1.5 s 内有人的键鼠输入（`"workbench-user-active"`）；预填目标所在页面的命令正在执行（`WorkbenchActivity.Busy`，`"workbench-busy"`）。
- 人的输入包括鼠标按下、释放、滚轮、键盘输入，以及屏幕光标位置相对上次采样确实变化的鼠标移动。工作台通过 `GetCursorPos`
  读取屏幕像素坐标；首次移动采样只建立基准，采样失败不推断人的活动。布局、切页、窗口显示在静止光标下产生的 `MouseMove`/`MouseEnter`
  不计为人的活动，也不延长已有的 1.5 s 窗口。测试可注入活动来源、光标位置与时钟，队列、限流和响应性检查不依赖桌面输入。
- 每次由控制引起的切页或定位之后 500 ms 内，窗口忽略对执行类与审批类按钮的点击（`ControlInputGuard`，在根元素的 PreviewMouseDown/PreviewKeyDown 检查），防止人原本要点 A 却落在 AI 刚切出来的 B 上。
- 界面标识：状态栏显示“AI 操作了界面 · <工具> · <客户端名> · n 秒前”的提示片；被预填的字段显示“AI 预填，待确认”标记和“清除预填”按钮；定位只做滚动与高亮，不改变复选框（复选框即导出选择）。
- 开关只能在工作台设置抽屉里由人修改，保存在 UI 设置文件；不提供任何读写它的 MCP 工具或控制消息（读取状态只回显当前值）。

### 2.8 控制调用的记录

- 宿主侧：每个控制工具调用照常经 `FoundationV4Tool` 写调用日志（`InvocationJournal.Observe`），参数经 `CallJournalPayload` 脱敏，因此自动出现在“AI 调用”页，`IsWrite = false`。
- 工作台侧：新增控制日志 `<diagnostics>/workbench-control-<pid-key>.jsonl`（字段：utc、requestId、origin、operation、参数摘要、status、refusal、耗时），沿用 `JournalRetention` 的轮转；
  调用详情抽屉按 `requestId` 附上“工作台已执行：切换到程序块并定位 Motion/FB_Axis”。
- 审计链不记录控制调用。审计链的定义是“写调用与审批”（`AuditLog.cs:46`），控制通道在代码上不能改工程，把界面动作写进防篡改链只会稀释它。若维护者要求，预填可另加一个审计事件，见第 7 节决定 5。

### 2.9 故障模式

| 情形 | 宿主返回 | 说明 |
|---|---|---|
| 工作台未运行 / 数据根不同 / 其他用户 | `RESOURCE_UNAVAILABLE`，rejected-before-operation | 250 ms 内连接失败 |
| 管道服务端 SID 不符（或映像不符） | `ACCESS_DENIED`（`target = "pipe-owner"`） | 不写请求 |
| 工作台版本不支持 v1 | `UNSUPPORTED_CAPABILITY`（`capability = "workbench-control.v1"`） | 列出支持版本 |
| 控制开关关闭、模态打开、人正在操作、忙、队列满 | `PRECONDITION_FAILED`，`condition` 如上 | rejected-before-operation |
| 页面需要工程但未打开 | `PRECONDITION_FAILED`（`"workbench-no-project"`） | — |
| 工作台工程与调用者绑定的工程不同 | `IDENTITY_MISMATCH`（`target = "project"`，expected/actual 为文件路径） | 防止“在错的工程里定位” |
| 软件或块不存在 / 不唯一 | `NOT_FOUND` / `TARGET_AMBIGUOUS`（最多 16 个候选） | — |
| 块树尚未加载 | `PRECONDITION_FAILED`（`"workbench-tree-not-loaded"`） | 控制通道不替人刷新（见第 4 节） |
| 调用记录是待批请求 | `PRECONDITION_FAILED`（`"approval-pending"`） | 控制通道不把人引向审批 |
| 渲染产物不存在或哈希已变 | `NOT_FOUND` / `IDENTITY_MISMATCH`（`target = "render-artifact"`） | — |
| 超过截止时间 | `TIMEOUT`（`stage = "workbench-ui"`） | 不锁会话 |
| 帧格式错误、管道中断、响应 requestId 不匹配 | `INTERNAL_ERROR`（诊断 ID），outcome failed | 不回显原始异常 |

## 3 工具清单

### 3.1 分类与目录

- 层 `L1`；新域 `Workbench`（归入 `session` 大类）；操作类：显示与预填为新操作类 **`UI`**（含义：“改变工作台界面状态：切页、定位、预填；不改工程、不写文件、不需要 TIA 会话”），读取为 `READ`。
  需同步修改 `ToolTaxonomy.OperationMeaning`（`ToolTaxonomy.cs:107-117`）、`ToolMetadata` 分类表、`PreflightLogic.Precautions`、`SharedToolCatalog` 的只读/写标志（UI 既非只读也非写）以及工具矩阵生成器。
- 8 个工具全部登记到 `ToolTaxonomy.WithoutTia`，不占 Openness lane；可在 `--offline` 宿主上调用；`HostBehavior.ApprovalWrite` 对它们恒为 false；不调用 `AuditInvocation`。
- 实现位置：`src/FoundationHost/WorkbenchControlTools.cs`，在 `LegacyHostToolRegistry.Create` 中与 `ToolUsageTool` 并列加入。宿主工具直接返回完整 V4 信封（复用 `FoundationV4Result` 的构造），
  经 `FoundationV4Tool` 的直通分支，使 requestId、调用日志与其他工具一致（实现方式参照 `FoundationTool.InvokeV4Async` 传入 `id`）。
- 八版：V14 SP1–V19 通过 `LegacyHostToolRegistry` 直接出现；V20/V21 通过 `SharedToolCatalog` 以 `execution = "foundation"` 并入完整档，并因 lite 必备集取自 V19 注册表而同时出现在 lite 档。
  每版目录各增加 8 个工具；`manifest/contracts/v4/baseline/*.json` 与 `responses/*.json` 由快照工具重建（工作台不存在时的 `RESOURCE_UNAVAILABLE` 响应是确定的，可作为离线响应样例），不手改。
- `GetToolUsage`：在 `reference/tool-examples` 增加每个工具的参数来源与结果解释，并加序列 `sequence/workbench-show-block`（`ListPlcBlocks` → `ShowWorkbenchBlock`）与 `sequence/workbench-prefill-inspection`；
  按 CLAUDE.md 重新生成嵌入目录并做八版检索与功能检查。
- 批处理：`ApplyToolBatch`、`RunReadOnlyToolBatch`、`PreviewToolBatch` 拒绝 `UI` 类工具（`UNSUPPORTED_CAPABILITY`）；`CallTool` 可以转调（lite 档需要）。

### 3.2 工具

| 工具 | 类 | 输入（全部 `additionalProperties: false`） | `data`（成功时） | 主要拒绝 |
|---|---|---|---|---|
| `ShowWorkbenchPage` | UI | `page`: `overview` \| `blocks` \| `versionControl` \| `calls` \| `audit` \| `environment` \| `log` | `page`、`previousPage` | 无工程时 blocks/versionControl → `workbench-no-project` |
| `ShowWorkbenchBlock` | UI | `softwarePath`（与 MCP 工具相同的写法）、`blockPath`（组限定路径） | `page = "blocks"`、`device {id, displayName}`、`block {path, name, kind, number}`、`focused: true` | `NOT_FOUND`、`TARGET_AMBIGUOUS`、`IDENTITY_MISMATCH`、`workbench-tree-not-loaded` |
| `ShowWorkbenchCall` | UI | `requestId`（32 位十六进制，来自任一工具的 `meta.requestId`） | `page = "calls"`、`requestId`、`tool`、`result` | `NOT_FOUND`、`approval-pending` |
| `ShowWorkbenchLadder` | UI | `softwarePath`、`blockPath`、可选 `renderRequestId`（本会话一次 `RenderPlcBlock` 的 requestId） | 定位结果 + `opened: bool`；未给 `renderRequestId` 时 `nextStep = "user-click-view-ladder"` | 同 `ShowWorkbenchBlock`；产物 `NOT_FOUND`/`IDENTITY_MISMATCH` |
| `ShowWorkbenchAtlas` | UI | 可选 `renderRequestId`（本会话一次 `RenderPlcProgramAtlas` 的 requestId） | `opened: bool` 或 `nextStep = "user-click-generate-atlas"` | 产物 `NOT_FOUND`/`IDENTITY_MISMATCH` |
| `GetWorkbenchState` | READ | 无 | `workbench {version, contracts, controlEnabled}`、`page`、`release`、`session {source, connected, project {name, path}}`、`device`、`busy`、`prefill {form, fields, origin}` | 仅通道类错误 |
| `GetWorkbenchSelection` | READ | `offset`（默认 0）、`limit`（默认 100，最大 256） | `page`、`device`、`focusedBlock`、`checkedBlocks[]`、`selectedCall`；`meta.paging` | 仅通道类错误 |
| `PrefillWorkbenchForm` | UI | `form`（封闭枚举，见 3.4）、`mode`: `set` \| `clear`、`fields`（按表单的封闭对象） | `form`、`applied[]`、`awaitingConfirmation: true`、`page` | `prefill-pending`、`workbench-busy`、字段校验 → `INVALID_ARGUMENT` |

补充约定：

- `session.source`：P8-21 之前为 `"workbench-bridge"`（工作台自己的桥接会话），之后为 `"shared-mcp-session"` 并附 `sessionKey`；AI 不能据此假定工作台工程就是自己的绑定工程，`Show*` 会用 `boundProjectFile` 自动核对。
- 定位规则与 Foundation 一致而不放宽：`softwarePath` 精确匹配设备 `DisplayName`、`Name` 或 `Id`（区分大小写），唯一才命中；`blockPath` 精确匹配 `BlockInfo.Path`。
  P8-20 阶段工作台的树来自桥接，组名转义与 Foundation 可能不同，属于已知差异（第 7 节风险 3）；P8-21 后树来自同一 MCP 结果，差异消失。
- 打开 HTML：宿主在本会话 `RenderPlcBlock`/`RenderPlcProgramAtlas` 成功后登记产物（requestId → 绝对路径、SHA-256、种类），`Show*` 只能引用本会话的产物；
  工作台打开前核对：本机盘符路径、普通文件（无重解析点、无 ADS）、扩展名 `.html`、哈希一致，然后沿用 `AtlasPresentation.Open` 的系统浏览器打开方式。

### 3.3 可定位的页面与对象

- 页面白名单：`overview`、`blocks`、`versionControl`、`calls`、`audit`、`environment`、`log`。**不含** `Mcp`（MCP 与客户端，含服务启停与密钥）、设置抽屉、审批抽屉、关闭审批的确认层。
- 对象：PLC 软件与程序块（块树）、调用记录（按 requestId）、本会话渲染产物。审计记录定位不在 v1 内（审计投影 `AuditEvent` 不含 requestId，`FeatureContracts.cs:41-42`），需要时在 v2 增加。

### 3.4 预填表单注册表

注册表是工作台源码中的封闭表，每个表单声明字段类型与上限，以及其执行按钮的去向 `executes`：`none`（只影响界面）或 `mcp`（经 MCP 工具，照常审批与审计）。
**执行去向为工作台桥接的表单不得注册**，这是“确需写入时同样走 MCP 审批与审计”在代码上的落点。

| 表单 | 字段 | executes | 开放时间 |
|---|---|---|---|
| `inspectionRules` | `namePattern`（≤ 256 字符，可编译的正则，1 s 超时） | none（检查规则在本地运行） | P8-20 |
| `blockFilter` | `filter`（≤ 256） | none | P8-20 |
| `blockSelection` | `softwarePath`、`blockPaths[]`（≤ 256） | none（只勾选；导出与图集仍由人点） | P8-20 |
| `connectProject` | `projectPath`（绝对 `.ap*`/`.als*` 路径，仅格式校验） | mcp | P8-21（面板迁移后） |
| `exportBlocks` | `outputDirectory`、`format` | mcp | P8-21 |
| `importBlocks` | `paths[]`、`overwrite` | mcp | P8-21 |
| `generationWizard` | 由 P8-30/31 定义 | mcp | P8-31 |

P8-20 阶段导入、导出、保存、编译、VCI 同步仍经桥接执行，因此都不开放预填。

## 4 红线如何在代码里强制

| 红线 | 机制 | 测试 |
|---|---|---|
| 与审批管道分开 | 不同管道名前缀、不同 DTO 与帧读写器（`WorkbenchControlProtocol.cs` 不引用 `PendingApproval`/`ApprovalDecision`/`ApprovalFrames`）；共用的只有 `LocalPipeSecurity` | 同一作用域下两名不同；向控制管道发审批帧、向审批管道发控制帧都被拒绝；`ApprovalServiceTests`、`ApprovalHostTests` 不改仍通过 |
| 通道上没有审批、审批开关 | 协议 `operation` 封闭枚举；工作台端 `WorkbenchControlServer` 只依赖 `IWorkbenchControlSurface`（导航、定位、打开产物、预填、快照），实现类 `WorkbenchControlSurface` 由 `MainWindow` 用委托构造，不持有 `Approvals`、`SettingsContent`、`ConfirmOverlay` 的引用 | 反射：`WorkbenchControlOperation` 成员与快照完全相等；`IWorkbenchControlSurface` 成员白名单；控制目录下所有类型的构造参数、字段类型不含 `IApprovalService`、`ApprovalService`、`ApprovalSettings`、`ConfigurationView`；源码扫描 `src/Studio/Gui/Control/**` 不出现 `Approv`、`ApprovalSettings`、`OnStartServer`、`OnStopServer` |
| 通道上没有服务启停 | 页面枚举不含 `Mcp`；表单注册表不含配置页字段；Surface 无相应成员 | 枚举快照测试；`ShowWorkbenchPage("mcp")` 在宿主 schema 层即为 `INVALID_ARGUMENT` |
| 不经工作台自身 TIA 会话写工程 | 控制处理器不持有 `IStudioClient`/`TiaClient`/`BridgeClient`（P8-21 后也不持有面板的 MCP 客户端）；定位只用已加载数据，树未加载就拒绝；预填只注册 `executes ∈ {none, mcp}` 的表单（特性 `[PrefillTarget(Executes = …)]`，执行命令必须是 MCP 面板命令） | 构造依赖反射测试；注册表快照测试；对每个注册表单断言其执行命令类型属于 MCP 面板或无副作用命令；控制请求全程 `FakeStudioClient` 的调用计数为 0 |
| 宿主侧不进审批、不写审计 | 工具类不引用 `ApprovalClient`、`ApprovalSettings`、`AuditInvocation`；分类为 UI/READ；`WithoutTia` 登记 | 审批开启且无工作台时，8 个工具返回 `RESOURCE_UNAVAILABLE` 而非 `CONFIRMATION_REQUIRED`；调用前后审计目录字节不变；`ToolTaxonomy.UsesOpennessLane` 为 false |
| AI 不能借控制把人引向审批 | `display.call` 拒绝待批行；页面白名单不含审批抽屉；切页后 500 ms 输入守卫覆盖审批按钮 | 待批 requestId → `approval-pending`；守卫期内对 `ApproveRequest` 的模拟点击不触发 `Approve` |
| 开关只能由人改 | “允许 AI 控制界面”只在设置抽屉、只写 UI 设置文件；没有任何工具或控制操作写它 | 源码扫描：控制目录不写 `UiSettings`；目录快照中无相关工具 |

## 5 P8-21：工程操作页改为共享 MCP 会话的操作面板

### 5.1 面板如何调用 MCP 工具

| 方案 | 能否共享 AI 的 worker | “人/AI”能否可靠区分 | 结论 |
|---|---|---|---|
| A. 工作台作为 HTTP 客户端连到自己启动的服务 | 否（HTTP 每会话一个 worker）；本地 stdio 宿主由 AI 客户端拉起，工作台连不到 | 否（与 AI 共用 Bearer 密钥） | 不采用 |
| B. 工作台自己拉一个 stdio 宿主 | 否（另一进程、另一 worker） | 是 | 只适合“无 AI 时”的自有会话，不能作为唯一方案 |
| C. 工作台进程内承载宿主 | 否 | 是 | 不采用：重复宿主，且工作台与发布键解耦 |
| **D. 宿主提供操作端端点（operator endpoint）** | **是**：调用路由到所选会话已有的工具对象与 worker | **是**：由传输决定 | **采用** |

操作端端点设计：

1. 每个宿主进程（stdio、HTTP、V20/V21 引擎宿主都在 `TiaMcp.FoundationHost.exe` 内）启动时创建同用户命名管道 `TiaMcp.Operator.v1.<SHA-256(SID, 数据根, 宿主 PID, 宿主启动时间)>`，
   使用同一 `LocalPipeSecurity`；宿主核对客户端 SID 与映像（必须是同包 `TiaOpenness.exe`，这里是强制的，因为“人”的归属依赖它）。
2. 发现：宿主在 `<data>/run/hosts/<pid>-<startTicks>.json` 原子写入一条记录（版本、`hostInstanceId`、发布键、传输方式、管道名、是否由工作台启动），退出时删除；
   工作台按 PID + 启动时间判断存活（复用 `ImportStagingStore.OwnerState` 的判断方式，`ImportStagingStore.cs:363-380`），并核对管道服务端 PID 与记录一致。
3. 握手：首帧 `{protocol:"tiamcp.operator", version:1, action:"list"|"attach"|"create", sessionKey?, workbench:{pid, version}}`。
   `list` 返回该宿主的会话（`sessionKey`、MCP 客户端名（不可验证）、创建时间、绑定工程、worker 状态）后关闭；`attach`/`create` 成功后，同一管道切换为 MCP JSON-RPC，
   宿主以 `McpServerFactory` + `StreamServerTransport`、工作台以 `McpClientFactory` + `StreamClientTransport` 通信（两者在 ModelContextProtocol.Core 0.3.0-preview.4 中已有；实现前先用最小夹具验证）。
4. 宿主侧引入 `IHostSession`：V14 SP1–V19 包装（`WorkerClient` + 本会话工具集合），V20/V21 包装（`EngineWorkerClient` + `EngineHostPipeline`）；在 `Program.cs:53-67`、`83-95` 与 `EngineReleaseHost.cs:87-107` 注册与注销。
   操作端的工具集合由 `OperatorTool(inner, hostSession)` 组成：进入该会话上下文（引擎为 `EngineHostPipeline.EnterSession`）、设置 `ActorScope.Workbench`，再调用**与 AI 相同的内层工具对象**。
   `SessionTool` 的会话 ID 检查（`EngineHostPipeline.cs:121-122`）不能靠伪造 `SessionId` 绕过，应由流水线公开一个显式的 `OperatorTools` 视图；暂存归属按被附着会话解析。
5. `create` 只在由工作台启动的宿主中允许（启动参数 `--operator-owner <工作台 PID>`；HTTP 服务或新增的 `--transport operator` 仅操作端模式）。由 AI 客户端拉起的宿主拒绝 `create`，避免在别人的宿主里多开 worker。

### 5.2 会话语义（与 P7-07a 每会话一个 worker 的关系）

| 选项 | 优点 | 问题 |
|---|---|---|
| 面板总是自有一个 worker 会话 | 生命周期独立 | 违背“同一 worker”；两个 worker 附着同一 TIA（V20/V21 被租约拒绝，V14 SP1–V19 无保护）；状态不共享，无法接力。等于把今天的桥接问题搬进宿主 |
| **面板附着到已有 AI 会话** | 正是“同一宿主与 worker、同一审批与审计”；实时状态；人可以接 AI 的手 | 生命周期随 AI 会话；无 AI 时无法工作；任一方 DisconnectPortal 结束整个会话 |
| 宿主级命名会话，MCP 会话与面板都去附着 | 对称，能熬过 AI 重连 | 改变 P7-07a 语义，重新引入发现 37 那类“一方断开影响另一方”的风险；生命周期与回收复杂 |

推荐：

1. **默认附着**：面板列出本机各宿主的活动会话，人选择后附着；附着期间该会话的规则对所有参与方一致：
   - 原生调用由 worker lane 串行，面板显示“等待：AI 正在执行 CompilePlcSoftware（12 s）”，可取消自己的排队请求；
   - 结果未知锁定会话、`SessionResetRequired`、`DisconnectPortal`/`RestartOpennessWorker` 对所有参与方生效；
   - 交错写入由现有计划哈希、`expectedProjectFile` 与批准后摘要复核保护（`FoundationV4Tool.cs:316-322`），对方改动工程后旧计划得到 `PLAN_STALE`/目标变化拒绝，不需新机制。
2. **面板“分离”与“断开”分开**：非所有者面板默认只提供“分离面板”（无原生影响）；`DisconnectPortal` 需二次确认并说明“这会结束 AI 客户端 <名称> 的会话，AI 需新建 MCP 会话后重新连接”；
   Disconnect 回执增加 `endedBy: "workbench" | "mcp"`，让 AI 得到可读的原因。
3. **工作台自有会话**：没有 AI 会话时，面板在工作台启动的宿主中 `create` 一个会话（工作台是所有者）。第一期 AI 不能加入这个会话；人要交给 AI 时点“释放”（干净分离并释放租约），由 AI 自己 `ConnectPortal` → `AttachOpenProject`。
   “AI 经 MCP 加入工作台会话”放到后续任务，需人在工作台确认（见决定 1）。
4. **八版同一租约规则**：V14 SP1–V19 的 PlcWorker 在 Attach 时同样获取 `PortalProcessLease`（同一文件、同一拒绝文本 `SessionBehavior.LeaseReserved/LeaseNotReleased`），
   这样自有会话与 AI 会话、或两个 AI 会话，都不会同时附着同一 TIA。这只在 worker 的附着前后加文件锁，不改 Siemens 调用序列，但仍属原生路径附近的改动，需要真机复验（决定 2）。
5. AI 客户端重连会产生新的 MCP 会话与新 worker（发现 37 之后的正常行为），已附着的面板随旧会话结束而回到“未附着”，并提示附着到同一客户端的新会话；不自动跟随。

### 5.3 人与 AI 的记录

- `ActorScope`（`src/Shared`，AsyncLocal）：操作端端点进入调用时设为 `workbench`，其余一律 `mcp`；只由传输决定，不读取任何参数、`_meta` 或客户端名。
- 审计链：`AuditRecord` 增加可选字段 `actor`（`"mcp"` | `"workbench"`）。`Canonical` 仅在非空时写出，`Parse` 接受 12 或 13 个字段，因此旧链逐字节校验不变、新旧混合链可校验
  （改动点：`AuditLog.cs:16-30`、`201-217`、`223-239`；`AuditInvocation.Begin` 增加 actor；工作台 `AuditTailReader` 与 CLI `audit verify` 同步）。
- 调用日志：`InvocationJournal` 的调用行增加 `actor` 与 `mcpSession`（会话 ID 哈希）；“AI 调用”页增加“人/AI”列与筛选（页名保留，内容扩展为全部调用）。
- 审批：`PendingApproval` 增加 v2 字段 `Actor` 与可选 `OperatorCallId`。`actor = mcp` 时宿主继续发送 v1，帧字节与今天完全相同；面板发起的写才发 v2。
  工作台审批行的 `Client` 显示“工作台（人）”或“MCP 客户端”；面板发起的请求自动打开对应审批卡（凭 `OperatorCallId` 匹配），但仍需人点“批准”（决定 3）。
- 审批与审计的统一：面板写调用同样经 `FoundationV4Tool` 的预检 → 审批 → 执行 → `ApprovalClient.Complete` → 审计 end，所以“同一审批与审计”不需要第二套代码。

### 5.4 页面动作到 MCP 工具的映射

| 页面动作 | MCP 工具（V4 名） | 备注 |
|---|---|---|
| 连接 | `ListPortalProcessProjects` → `ConnectPortal(processId)` → `AttachOpenProject` | Foundation 只附着已运行的 TIA，不启动 TIA；面板提示人先启动 TIA（与 AI 相同限制） |
| 打开工程 | `OpenProject`（preview → apply） | — |
| 保存 | `SaveProject`（preview → apply，强制审批） | 原先只有一个确认框 |
| 断开 | `DisconnectPortal` | 见 5.2 第 2 点 |
| 软件选择、设备树 | `GetProjectTree` | — |
| 程序块 | `ListPlcBlocks` / `GetPlcBlockHierarchy` | 与 AI 使用的路径写法一致 |
| 编译 | `CompilePlcSoftware`、`CompilePlcDiagnostics` | candidate 策略下 apply 走审批 |
| 导出 | `ExportPlcBlocks` / `ExportPlcBlock` | 是否审批随该版本的 P6-EXPORT 策略，与 AI 相同 |
| 导入 | `ImportPlcBlock` / `ImportPlcBlocksFromDirectory`（本机路径，不需 `StageImportFiles`） | 审批 |
| 检查 | `ListPlcBlocks` + `GetPlcBlockInfo`，规则仍用本地 `InspectionEngine` | 没有引用信息时“未使用块”规则停用（`InspectionEngine.Run` 的 `referencedNames = null` 语义） |
| 梯形图、图集 | `ExportPlcBlocks` 到报告目录 → `RenderPlcBlock` / `RenderPlcProgramAtlas` | 渲染为宿主文件工具，不需 TIA |
| 版本控制 | V20/V21：引擎 VCI 工具（`ListVersionControlWorkspaces`、`GetVersionControlStatus`、`CreateVersionControlWorkspace`、`ConnectProjectToWorkspace`、`SynchronizeVersionControlWorkspace`）；V16–V19：等 F33（P8-03 批次 B8） | 见决定 4 |
| 环境检查 | 本地 `OpennessDoctor`，不变 | 不涉及 TIA 会话 |

结果呈现统一解析 V4 信封（`ok/data/error/meta`），面板与 AI 看到的是同一份结果；`error.code` 经现有 `EngineResultText` 本地化显示。

### 5.5 实时会话状态

操作端端点在附着后推送通知 `notifications/tiamcp/sessionState`（全部来自宿主缓存，不调用 TIA）：

`sessionKey`、发布键、参与方（MCP 客户端名，标“未验证”；工作台）、worker `{pid, generation, state: idle | busy | poisoned | disconnected}`、
lane `{tool, actor, requestId, sinceUtc}`、绑定 `{processId, projectFile, bindingEpoch}`（取自 `ApprovalIdentity`）、`sessionLocked`、`approvalEnabled`、最近 20 次调用 `{requestId, tool, actor, outcome, ms}`。
在 lane 获取/释放、绑定纪元变化、会话锁定、断开时发送，工作台以 Background 优先级合并刷新。

### 5.6 迁移后可删除的代码

| 删除 | 位置 | 前提 |
|---|---|---|
| 桥接进程与客户端 | `src/Studio/Bridge/**`、`src/Studio/Client/**`、Gui 工程的 `CopyNativeBridge` 目标与项目引用（`TiaOpenness.Gui.csproj:15-31`） | 所有页面改走面板客户端 |
| Core 中的会话栈 | `Core/Rpc/**`、`Core/Adapters/**`、`Core/Abstractions/{ITiaSession,SessionFactoryLoader,UnavailableSessionFactory}`、`Core/Mock/**`、`Core/Environment/OpennessAssemblyResolver.cs`；`TiaSharedAdapterPaths` 分支（`TiaOpenness.Core.csproj:4-12`） | 测试改用假宿主；保留 `OpennessDoctor`、`OpennessLocator`、`InspectionEngine`、`GitWorkspaceDiff`、`CallJournalReader` |
| 八版 Studio 适配器 | `src/Studio/Openness/**`（含 `Studio.Common.props`、`OpennessSessionFactory.cs`） | 同上 |
| Studio 会话实现 | `src/Adapters/Native/Studio/{OpennessSession,PlcNavigator,HmiNavigator,EngineeringExtensions,StudioAdapter}.cs`、`src/Adapters/Policy/StudioThreadGuard.cs`、`Adapter.Sources.props:75-81` 对应条目 | `OpennessVersionControl.cs` 按 F33 迁入 Foundation 适配器，不随之删除 |
| Studio 契约 | `src/Adapters.Contracts/Studio/{Dto,Enums}.cs`、`StudioInterfaces.cs` 的 `IStudioSession`、`AdapterCapabilities.StudioSession`；`ChannelProfile.Studio`（`src/WorkerChannel/ChannelClient.cs:82`、`157`，`ChannelCodec.cs:17`、`107`、`121`） | VCI DTO 视 F33 需要保留 |
| 布局与发布 | `BundleLayout` 的工作台桥接与适配器路径函数（`BundleLayout.cs:370-404`）；`scripts/operations/delivery-files.json:232` 的桥接条目；`manifest/multi-version-build.json` 的 `studioReleaseKeys` 与桥接适配器文件；`build-tools/release` 的 build-studio 步骤 | 发布链改动按 release-workflow 重新生成清单，不手改哈希 |
| 测试 | `tests/Studio/TiaOpenness.Core.Tests` 中桥接与适配器测试、`tests/Studio/Test-BridgeSmoke.py`、`AdapterSourceClosureTests` 中的 Studio 检查、`StudioContractTests`、`StudioGoldenSamples` | 由面板与假宿主测试替代 |

`TiaOpenness.Contracts` 的 DTO 被视图模型使用，迁移时改为 Gui 内部视图记录后再删（或保留为纯界面模型，视实现方便）。`--mock` 演示模式改为连接一个离线夹具宿主，或者删除（决定 6）。

## 6 分步实施与验收

每个任务独立分支、可单独审查；Codex 实现，Claude 复跑验收。命令以 `docs/development/validation.md` 为准。

| 任务 | 内容 | 允许修改的路径 | 验收 |
|---|---|---|---|
| P8-20a | 抽出 `LocalPipeSecurity`（审批行为与字节不变）；新增控制协议 DTO、帧读写、管道名、版本协商；`WorkbenchControlSources.props` | `src/Shared/**`、引用它的 csproj、`tests/Shared` 或宿主测试 | 现有审批测试原样通过；协议单元测试：未知/重复字段、封闭枚举、超限帧、版本拒绝、两管道名不同、交叉投递被拒 |
| P8-20b | 工作台端：`WorkbenchControlServer`（监听、身份、队列、截止、限流）、`IWorkbenchControlSurface` 与 `MainWindow` 适配、快照发布、预填注册表 v1、输入守卫、状态栏提示、控制日志、设置开关与本地化 | `src/Studio/Gui/Control/**`（新）、`WorkbenchShell.cs`、`MainWindow.xaml(.cs)`、相关视图与 `Localization`、`Settings/UiSettings.cs`、`tests/Studio/TiaOpenness.Gui.Tests` | 在 `WpfContext` 中起真实窗口与真实管道（随机管道名）：每个操作的成功与拒绝路径；第 4 节全部架构测试；`ResponsivenessTests` 不退化；`tests/test-suites.json` 新增 `workbench-control` 套件 |
| P8-20c | 宿主端 8 个工具、分类（`UI`、`Workbench` 域、`WithoutTia`）、V4 映射、渲染产物登记、批处理拒绝 UI | `src/FoundationHost/**`、`src/Logic/ModelContextProtocol/{ToolTaxonomy,ToolMetadata,PreflightLogic}.cs`、`src/EngineHost/SharedToolCatalog.cs`、宿主测试 | 以测试内假工作台管道服务端驱动：每个错误码映射、无工作台时 `RESOURCE_UNAVAILABLE`、不审批不写审计、不占 lane；V20/V21 完整与 lite 目录都包含 8 个工具（`engine-host`、`shared-pipeline` 套件） |
| P8-20d | 目录与文档：重建八版 `manifest/contracts/v4` 基线与响应快照；`reference/tool-examples` 示例与序列并重新生成嵌入目录；`runtime-layout.md` 增“工作台控制通道”一节；版本工具矩阵；计划表状态 | `manifest/contracts/v4/**`（工具生成）、`reference/tool-examples/**`、`docs/**` | 契约差异仅为新增 8 个工具；八版 `GetToolUsage` 检索与功能检查；`Check-Repository.py --no-binaries` |
| P8-20e | 端到端：真实宿主（`--offline`，V19 与 V21 各一）+ 真实工作台（测试数据根）经真实管道；C# 编写（仓库禁止新增 PowerShell） | `build-tools/release` 或测试工程 | 显示、读取、预填全流程；关闭工作台后的类型化错误；工作台与宿主数据根不同时互不可见；然后 L5：VM（V19、V21）上 Claude Code stdio 与宿主机经 HTTP 远程两种接法 |
| P8-21a | 归属：`ActorScope`；审计可选 `actor`（向后兼容规范化）；调用日志 `actor`/`mcpSession`；`PendingApproval` v2（MCP 路径仍发 v1）；审批、调用、审计页显示人/AI；CLI 校验 | `src/Shared/**`、`src/FoundationHost/FoundationV4Tool.cs`、`src/Engine/ModelContextProtocol/Tools/McpServer.Approval.cs`、Gui 服务与视图、测试 | 旧审计链逐字节校验通过；新旧混合链通过；篡改仍被发现；MCP 路径审批帧与基线逐字节相同 |
| P8-21b（review；L5 NOT RUN） | V14 SP1–V19 Attach 获取 `PortalProcessLease`（与 V20/V21 同文件、同拒绝文本），DisconnectPortal 干净释放 | `src/PlcWorker/**`、`src/Adapters/Native/Session/**`（仅附着前后）、测试 | 两个 worker 附着同一 PID 时第二个得到 `LeaseReserved`；未干净释放得到 `LeaseNotReleased`；原生调用清单不变；L5 复验（需维护者批准，决定 2） |
| P8-21c | 宿主操作端：`IHostSession`、主机登记文件、管道与身份（强制映像核对）、握手、MCP-over-pipe、`OperatorTool`/`OperatorTools`、`--operator-owner` 与 `--transport operator`、状态通知、`endedBy` | `src/FoundationHost/**`、`src/EngineHost/**`、`src/Shared/**`、宿主测试 | 夹具 worker：两个 MCP 会话 + 面板附着其一；面板与 AI 调用经同一 lane 串行；面板写调用审批帧带 `Actor=workbench`；一方 DisconnectPortal 后另一方得到既有拒绝文本与 `endedBy`；会话锁定对双方生效；AI 拉起的宿主拒绝 `create` |
| P8-21d | 工作台面板：`IMcpSessionClient`（管道 + MCP 客户端）、会话选择与附着、实时状态区、概览/程序块/图集/检查改走 MCP；预填表单 `connectProject`、`exportBlocks`、`importBlocks` 开放 | `src/Studio/Gui/**`、`tests/Studio/**` | 假宿主（测试内操作端服务端）驱动全部按钮的 preview → apply → 审批流程；结果按 V4 信封呈现；`FakeStudioClient` 不再被页面使用 |
| P8-21e | 版本控制页：V20/V21 改用引擎 VCI 工具；V16–V19 按决定 4 处理 | `src/Studio/Gui/**`、测试 | 按决定 |
| P8-21f | 删除第 5.6 节清单，清理发布链与清单生成 | 第 5.6 节所列路径、`build-tools/release/**`、`scripts/operations/**` | `branch-gate`；L3 八版构建；仓库检查；L5：VM 上 AI 与人在同一会话接力（AI 编译 → 人保存 → AI 继续导出），审计中人/AI 区分正确 |

验收层次小结：

P8-20a 已实现（待审查）：`LocalPipeSecurity` 保留审批的 SID-only 检查、DACL、标志与 255 实例，
控制通道可使用 SID + 对端完整映像路径检查，最多 8 实例。`WorkbenchControlSources.props` 在 Logic
（net48/net10，Foundation 宿主通过既有项目引用使用）与 Gui 中导入，共享安全源码只编译一次。
协议独立使用 camelCase UTF-8 JSON、4 字节小端长度与 256 KiB 上限；递归拒绝重复字段、未知字段、
缺必需字段及数值/未知枚举。8 个操作、7 个页面及当前 3 个预填表单封闭；每个操作与表单有独立参数 DTO。
响应包含封闭状态、错误码、最多 16 个候选及支持版本列表；不支持的请求版本返回 v1 类型化拒绝，不降级。
读取与 UI 截止上限分别为 2/5 秒，截止判断由后续服务器在接收/派发时调用。
渲染参数可附带宿主解析的 `artifact {requestId,path,sha256,kind}`，工作台仍须核对文件、哈希与会话归属。
通知 DTO 仅为 `stateChanged` 快照模型；v1 一请求一响应，不订阅、不推送通知，不引入新操作。
`workbench-control-protocol` 套件在无 SDK 与有 SDK 的 Windows checkout 均为最低 74 通过、0 跳过；
Linux/macOS 为最低 71 通过、最多 3 跳过（Windows 管道安全测试）。现有审批测试与审批协议源码未修改。
P8-20b/c 负责监听与调用顺序、映像路径的 BundleLayout 解析、UI 队列/守卫、产物检查及 V4 映射。

- 单元：协议与帧、管道安全、工作台控制面、宿主映射与分类、审计兼容、租约。
- 假工作台端到端：P8-20c 用测试内控制管道服务端驱动宿主工具；P8-20b 用真实窗口 + 真实管道驱动工作台；P8-20e 串起真实宿主与真实工作台。
- 假宿主端到端：P8-21c 用夹具 worker 驱动操作端；P8-21d 用测试内操作端服务端驱动工作台面板。
- 真机（L5）：P8-20e（V19、V21，两种接法）、P8-21b（租约）、P8-21f（接力与审计）。

## 7 风险与待维护者决定

风险：

1. 信任边界是“同一 Windows 用户”。能在本机执行代码的 AI 代理（例如有 shell 的客户端）可以冒充工作台或直接连管道；三条管道都只防“经 MCP 工具越权”。映像路径核对只防误配对。文档沿用 runtime-layout 的表述。
2. 界面被抢：频繁切页、焦点跳动或点击落空。以队列限流、人优先守卫、500 ms 输入守卫与状态栏提示缓解；P6-57 响应门槛纳入回归。
3. P8-20 阶段工作台的树来自桥接，与 Foundation 的 `softwarePath`/`blockPath` 写法可能存在转义差异，定位可能误报 `NOT_FOUND`；P8-21 后消失。
4. 审计记录格式扩展会波及 CLI 校验、工作台审计视图与发布检查；必须保证旧链规范化字节不变。
5. 共享会话的 lane 竞争：人的长编译会阻塞 AI，反之亦然；Disconnect 结束双方会话。需在界面上把这些后果讲清楚。
6. AI 客户端重连频繁时，面板的附着会随旧会话结束而失效。
7. MCP SDK 仍是预览版，`StreamServerTransport` 在命名管道上的行为（关闭、取消、通知）需先用夹具确认；不行时操作端退回自定义帧 RPC，工具对象与归属设计不变。
8. 每版目录增加 8 个工具，lite 档也增加，影响客户端上下文长度；工具描述需保持简短。

待决定：

1. **P8-21 会话模型**：默认附着到已有 AI 会话 + 只在工作台启动的宿主中提供工作台自有会话；第一期不支持 AI 加入工作台自有会话。（推荐：同意；“AI 加入”另立任务，并要求人在工作台确认。）
2. **V14 SP1–V19 是否也取 TIA 进程租约**，使八版都拒绝两个 worker 附着同一 TIA。（推荐：是，P8-21b，需一次真机复验。）
3. **面板发起的写是否仍需在审批卡上单独点“批准”**。（推荐：是，同一规则、没有第二条批准路径；面板自动打开对应审批卡以减少操作。）
4. **V16–V19 版本控制页在 F33（B8）迁入前怎么办**：保留桥接仅用于 VCI 读取并禁用“工作区→工程”同步，或在此之前隐藏该页。（推荐：前者；P8-21f 中桥接的最后删除等 B8 完成。）
5. **审计链是否记录 UI 控制调用（含预填）**。（推荐：否，只记写调用与审批；控制调用进调用日志与工作台控制日志。）
6. **`--mock` 演示模式**：改为连接离线夹具宿主，还是删除。（推荐：改为夹具宿主，保留截图与演示用途。）
7. **“允许 AI 控制界面”开关的默认值**，以及 AI 是否可直接打开系统浏览器显示本会话渲染产物。（推荐：默认开启；浏览器打开仅限哈希一致的本会话产物。）

## 维护者决定（2026-10-08）

- 工程操作面板默认附着 AI 正在使用的会话，人与 AI 接力操作、看到同一状态；没有 AI 会话时工作台自建会话（只在工作台启动的宿主里，第一期 AI 不加入）。面板“断开”前提示会结束 AI 的会话。
- “允许 AI 控制工作台界面”默认开启，保留人优先保护（人正在操作时不抢焦点，切页后短时间内忽略执行与审批按钮的点击）。
- 按推荐执行的其余各项：面板发起的写仍需在审批卡上批准（面板自动打开对应审批卡）；界面控制调用不进审计链，进调用日志与工作台控制日志；
  V14 SP1–V19 也启用 TIA 进程租约，与 V20/V21 一致（需一次真机复验）；V16–V19 版本控制页在 F33 迁入前保留桥接只做 VCI 读取，禁用“工作区→工程”同步；
  `--mock` 演示模式改为连接离线夹具宿主；浏览器只打开本会话渲染且哈希一致的文件。
