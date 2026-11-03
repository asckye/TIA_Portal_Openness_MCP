# 阶段 7 设计：V20/V21 改用 Foundation 宿主与引擎 worker

状态：历史记录（已实施：P7-01–P7-08）· 最后核对：2026-10-11，master `3a227657`

[设计规范索引](../design-spec/README.md) · [架构概览](../design-spec/architecture.md) · [架构决策记录](../decisions/README.md) · [重构计划](../refactor-plan.md)

本文是阶段 7 的评审设计（2026-10-08，原文为英文，保存在仓库外），2026-10-11 移入仓库时译为中文。设计判断保持原样，下列内容已按现状修订或注明：

- 已被 P7-07a 修改：原文的“每个宿主进程一个共享 worker”改为每个 MCP 会话一个 worker（stdio 一个，HTTP 每个存活的会话一个），见 [ADR 0007](../decisions/0007-one-worker-per-mcp-session.md)。
- 路径与项目名已按 P7-11a 改名后的名称更新，例如 `src/Engine/WorkerMode/EngineWorkerHost.cs`、`src/Engine/TiaMcp.Engine.V20.csproj`、`src/FoundationHost/TiaMcp.FoundationHost.csproj`、`src/EngineHost/TiaMcp.EngineHost.csproj`、`tests/FoundationHost/TiaMcp.EngineHost.Tests`；原文中的行号已删除。
- 原文列出的部分 Python 检查脚本和测试工程此后已删除，或在 P8-10 中改为 `build-tools/release` 中的 C# 实现；第 4 节保留当时的名单以说明改动面，并标出已不存在的条目。
- lite 档的成员数已变化（以契约基线的 `liteTools` 为准）；原文“隔离时期的描述在 P7-08 中更新”已完成。
- 文中的决定已写成 ADR：[0005](../decisions/0005-v20-v21-on-foundation-host.md)、[0006](../decisions/0006-engine-exe-as-v20-v21-worker.md)、[0007](../decisions/0007-one-worker-per-mcp-session.md)、[0008](../decisions/0008-worker-timeouts-and-faults.md)。现行运行架构见 [架构概览](../design-spec/architecture.md)。

## 0 决定

主导者受维护者委托于 2026-10-08 做出以下决定：

- 复用引擎 EXE 作为 worker。
- 每个宿主进程一个共享 worker（已被 P7-07a 修改为每个 MCP 会话一个 worker）。
- 默认调用超时 120 秒，编译、下载、导入导出、归档等类别在 P7-05 中按类覆盖。
- TIA 进程丢失即 worker 代次故障，须显式调用 `RestartOpennessWorker` 后重新绑定。
- 进度经 worker 侧的 `IMcpServer` 垫片转发（P7-05）。
- 目标版本 4.0。
- 隔离时期的工具描述在 P7-08 中作为经评审的基线更新修改。
- P6-54 被取代。

## 1 当时的引擎

- 组成：`src/Engine/ModelContextProtocol/Tools/ToolCatalog.cs`（`ToolTypeNames` 86 个、`ServiceTypeNames`；`Methods` 为名称到 `MethodInfo` 的映射；候选重载经 `BehaviorCapabilities.SelectMethod(..., McpServer.ReleaseKey)` 选择）；`CreateTool`/`CreateToolCore` 以 `request.Services ?? EngineServices.Get(type)` 为目标构建 SDK 工具，生成类型化输入 schema，并执行 `RemoveV4NullDefaults` 与 `InlineSchema`。`McpServer.Profile.cs` 装饰描述（`ToolExamples.Decorate`、`ToolUsageCatalog.Hint`、`WithSchemaHints`）；`GetAllTools`/`GetLiteTools` 按 `VersionToolProblem` 过滤；lite 集合来自 `ToolUsageCatalog.ProfileEntries(ReleaseKey)`；`ResolvedProfile()` 依次取 `--profile`、`TIA_MCP_PROFILE`，默认 lite。`EngineRegistration.AddEngine(includeSession)` 注册目录和工具单例；`Portal`、`IEngineeringSession`、`IHmiToolSession` 与各服务只在 `OpennessReadiness.Ready` 时注册。`EngineServices` 有宿主提供者和独立的惰性提供者（CLI 已经能在没有 MCP 的情况下运行工具）。
- 包装链（`McpServer.ArgDiagnostics.cs` 的 `WrapTools`，由外到内）：`AuditCallTool` → `VersionPolicyTool`（V4 准入 = `ValidateTypedFamilies` + `BindV4Call`）→ `OpennessReadinessGuard.GuardedTool` → `SerializedCallTool` → `ResponseGuardTool` → `SchemaHintedTool` → SDK 工具。`SerializedCallTool` 内的顺序：调用日志 Observe → `PrecheckBeforeApproval`（在自己的通道中预览）→ `WaitForApproval` → `ToolDispatchLanes.Acquire` → `SessionPrecheckRefusal` → `ApprovalStillMatches` → `ValidateRuntimeTool`（`Portal.VerifyBinding`、原生读取）→ `AuditInvocation.StartCurrent` → `ValidateCallerInputFiles` → 调用并校验 `ToolResult` → `DiscloseTargets` → `ObserveSessionOutcome` → `FinishApproval`（`McpHints.WithRecovery`、`ApprovalResult.Decorate`、`ApprovalClient.Complete`）。
- 无传输的调用路径：`McpServer.ToolBridge.cs`（现位于 `src/Shared/Host/`）中的 `BindV4Call` 与私有的 `InvokeToolMethod`（调用日志 BEFORE/RETURNED、投影、版本检查、`ValidateRuntimeBinding`、`EngineServices.Get`、调用、解包 Task、`TargetFailure`）。
- 信封：工具体通过 `SessionToolContract`、`HardwareContract`、`PlcExchangeContract` 返回 `CallToolResult`，由 ToolBridge 中的 `McpServer.V4Result`、`V4Reject`、`TargetFailure` 生成；`V4Result` 写入 `ReleaseKey`、requestId（`AuditInvocation.CurrentRequestId ?? InvocationJournal.CorrelationId`）和 `BehaviorCapabilities.EntryPolicy`。
- 状态归属：
  - `ReleaseKey`：worker 中为常量，共享代码从宿主取得可设置的发布键；
  - 审批 AsyncLocal：宿主；worker 只在 `preview:true` 时进入预览作用域；
  - `SessionFaults`：宿主，以 worker 代次为键；
  - `ToolDispatchLanes`：宿主；Openness 通道即串行的 worker 客户端，本地通道为 `SemaphoreSlim(8)`；
  - 调用日志区间：宿主；原生行与 `BeginNativeCallScope`：worker，按 requestId 关联，返回 `nativeCallIssued`；
  - `BindingSnapshot`：worker 每次回复带上缓存的快照，宿主缓存；
  - `ExportStore` 与 `BatchPlanStore`：宿主；
  - `OpennessReadiness`：在 worker 中计算，宿主镜像；档位：宿主；`AuditInvocation`：只在宿主；
  - `ProcessLostObserved`：worker 报告，宿主据此使代次故障；
  - `BehaviorCapabilities` 表：目录携带 worker 的表，宿主核对相等；
  - `RunToolTransaction`（`ProjectSessionTools`）留在 worker。

## 2 worker 接缝

`engine.invoke` = `UseCorrelation(requestId)`（预览时再加 `BeginReadOnlyApprovalPreview` 与 `AuditInvocation.ReadOnlyPreview`）+ `BeginNativeCallScope` → `BindV4Call` → 准入错误，或 `ToolResult(InvokeToolMethod)` → 回复 `{原样结果, nativeCallIssued, 绑定快照, 会话状态, 原生故障}`。

- worker：MTA 主线程（Foundation 的 PlcWorker 是 STA，两者绝不共用一个进程）；就绪检查与 `Openness.Initialize` 取自引擎 `Program.cs`，但不经 `EngineRouter` 重新路由；`AddEngine` + `SetServiceProvider`；显式 `--bundle-root`（当时 `BundleLayout` 没有 `runtime/v2x/worker` 锚点）；继承数据根与调用日志；`--with-ui`；`TiaPortalLocation`；目录导出动词。
- WorkerChannel 的 `ChannelProfile.Engine`：`engine.status`、`engine.invoke`；载荷进度；回复中的 Unknown 不是终止状态；上限（当时请求 1 MiB、响应 16 MiB，超限使通道失效，所以在宿主预检，在 worker 中返回 `LIMIT_EXCEEDED` 或转存）；纪元：预览与状态查询为 None，其余为 MayAdvance；进度在所属线程上发出（异步续延在线程池上运行，因此在所属线程上泵一个队列）。
- `EngineWorkerClient`（由 `WorkerClient` 演化）：启动进程、nonce、worker 与适配器哈希、串行通道；可配置的超时（当时固定 2 分钟）；派发后不取消（当时取消会使通道失效）；可重启的代次（当时 `WorkerClient` 从不重启）；首次附着的超时提示。
- 进度：4 个工具接收 `IMcpServer`/`RequestContext`（`DocumentsTools`、`PlcBlocksTools`、`TypesTools`），由垫片经 Engine 配置转发，宿主再用调用方的 progressToken 转发出去。

## 3 由宿主发布目录

- 目录在构建时生成：`--write-tool-catalog <file>` → `runtime/v2x/worker/tool-catalog.json`，内容为 `{release, formatVersion, workerSha256, 按 GetAllTools() 顺序排列的 SDK Tool 对象, liteTools, behaviorCapabilities, ServerInstructions（McpGuides.cs）, descriptors}`。
- 描述符：原始 `[Description]`、分类、`RenderSignature`、`SpecsOf` 规格（名称、友好类型、CLR 类型、是否必需、默认值 JSON、默认值 `ToString()`、描述、是否合成、允许值）、`dryRun` 是否存在及其默认值、候选族、执行位置 host|worker。陷阱：`VersionCallProblem` 用 `DefaultValue?.ToString()`（"True"），而 `RenderSignature` 输出 "true"。绝不在 net10 上重新计算 schema。宿主核对 `workerSha256`。
- lite：宿主侧的 `ToolUsageCatalog`（当时 60 个，含 `FindTools`、`CallTool`）；给 `HostOptions` 增加 `--profile` 与 `TIA_MCP_PROFILE`（当时 `Parse` 拒绝未知选项）。
- 宿主本地工具：
  - 编排与元工具：`CallTool`、经 `engine.status` 的 `PreviewToolCall`、`FindTools`、`ListToolCategories`、`RunReadOnlyToolBatch`、`PreviewToolBatch`、`ApplyToolBatch`、`GetToolUsage`（档位字符串保持 "full-engine"）；
  - 导出句柄：`GetExportContent`、`ListExportHandles`、`SaveExportContent`、`DeleteExportHandle`、`ClearExportHandles`；
  - 导入暂存；
  - worker 控制：`GetOpennessWorkerStatus`、`RestartOpennessWorker`、`GetNativeInvocationLog`，worker 卡死时也必须可用；
  - `ToolTaxonomy.WithoutTia` 的工具体：`Build*`、`PlanArtifactImportOrder`、`DecodePlcSimaticMl`、`ValidatePlcDocumentSchemas`、`Render*`、`ScanPlcSourceAnnotations`、`GetOpennessGuidance`、`GetV21EcosystemCatalog`、`InspectSimaticSdCompatibility`，保持 P6-58 的规则（本地工具不排在 Openness 调用之后）。
  - 当时的 `tests/Engine/TiaMcp.Engine.Tests` 已经在 net10 上链接许多宿主层文件，并通过响应字节 golden。属于 `WithoutTia` 但仍放在 worker 的有 `GetOpennessCompatibility`；即使没有 TIA 也走 worker 通道的有 `InitializeEnvironment`、`GetEnvironmentDiagnostics`、`CheckProductUpdate`。
- 不把 V20/V21 工具包进 `FoundationV4Tool`：两者的顺序和信封不同（`RecoveryHints.Attach` 对 `McpHints.WithRecovery`，以及 `FoundationV4Result` 的构造器）。注册一条重现引擎顺序的 `EngineCatalogTool` 链；复用 `HostBehavior`、`HostFailurePolicy`、`SessionBehavior`、`ApprovalPrecheck`、`CallerInputFiles`；分类决定 `IsWrite`/`ApprovalWrite`；`ToolTaxonomy.UsesOpennessLane` 决定通道；预检 = 带 `dryRun=true` 或 `mode=preview` 的 `engine.invoke preview:true`；`CallerInputFiles` 在宿主侧引擎的两个检查点执行；会话锁取自 `ObserveSessionOutcome` 与 worker 的 `nativeCallIssued`；`meta.requestId` 为宿主审计 id；审批的宿主标记为 "engine"。不为 V20/V21 注册 `LegacyHostPassiveDiagnosticTools`（超过 `MaxTools=256` 时 `Inspect` 抛异常）。链接 `McpPrompts.cs`；资源处理器为空，与引擎 `Program.cs` 相同。
- 拆分：以 `ToolDescriptor` 的 `IToolCatalogView` 加 `IToolInvoker` 取代 `MethodInfo` 的用法（`AllToolMethods`、`SpecsOf`、`ClassificationOf`、`TryDryRunDefault`、`ToolDescription`、`GetToolUsage` 中的 `CreateTool(..).ProtocolTool`、`McpHints.WithRecovery`）；通过 `.props` 链接清单共享源码（沿用 `ApprovalSources.props` 的做法）；各程序集的差异通过分部钩子实现（`ApprovalSessionKey`、`ApprovalBindingIdentity`、`EnterTargetLane`、`ReadSessionState`、`ValidateRuntimeBinding`）；把 `ToolVersionPolicy.cs` 移到 Logic（现为 `src/Logic/Siemens/ToolVersionPolicy.cs`）。

## 4 构建与打包

- 项目：`src/Engine/TiaMcp.Engine.V20.csproj` 与 `TiaMcp.Engine.V21.csproj` 增加 net48 的 WorkerChannel 引用、`src/Engine/WorkerMode/EngineWorkerHost.cs`、`src/Engine/Cli/ToolCatalogExport.cs`；新增 `src/EngineHost/TiaMcp.EngineHost.csproj`（net10），链接宿主层源码，由 `src/FoundationHost/TiaMcp.FoundationHost.csproj` 引用；PlcWorker 的 20/21 构建不再随包发布（保留为编译覆盖，或从 `ReleaseCommands.cs` 的 build-plc-workers 中去掉）。
- 布局：`runtime/v2x/` 放 net10 的 FoundationHost 与 `release-key.txt`；`runtime/v2x/worker/` 放 `TiaMcp.Engine.V2x.exe`（及其配置）、net48 依赖、`TiaMcp.Adapter.2x.dll` 和 `tool-catalog.json`。子目录是必需的：Logic、WorkerChannel、ModelContextProtocol、System.Text.Json 在 net48 与 net10 之间不同。每个版本约增加 12 MB。
- 表：`src/Shared/BundleLayout.cs` 的 Products 改为 FoundationHost `--release-key`，以及 `WorkerPath`、`EngineExecutablePath`、开发锚点；`src/Logic/Siemens/TiaVersionCatalog.cs` 拆分 `IsFullEngine`（保留它表示完整目录，另加用于选择产品的宿主/worker 种类）。
- 发布工具（`build-tools/release/`）：`BuildReleasePipeline.cs`（把引擎复制到 `worker/`、生成目录、织入器校验路径）；`ReleaseCommands.cs`（FoundationHost 复制循环当时硬编码 14sp1–19；prepare-delivery 的 mcpServerExe 与 cli.exe）；`ReleaseCandidateChecks.cs`（经引擎 EXE 与 HttpTests 的捕获）；`ReleaseRecords.cs`；`BranchGate.cs`；`ReleaseProduction.cs`。
- 工作台：`src/Studio/Core/Environment/WorkbenchEnvironmentChecks.cs`、`src/Studio/Gui/Configuration/ConfigCore.cs`、`src/Studio/Gui/Services/DiagnosticBundleService.cs`、`src/Studio/Gui/Services/TiaExitEvidenceCollector.cs`。
- 更新器：`src/Updater/Updater.cs` 的 `ValidatePackage` 硬编码 `runtime/v2x/TiaMcp.Engine.V2x.exe`（另见 `src/Logic/ModelContextProtocol/UpdateLogic.cs`），改为由 `delivery-files.json` 驱动的表；同时修改插件 `plugin.json` 命令和 `manifest/package-manifest.json`。
- 脚本（当时的名单）：`Snapshot-ToolContracts.py`、`Snapshot-ToolResponses.py`（`capture_release`、`sdk_only_harness`）、`Test-ResourceDiscovery.py`、`Check-BundleLayout.py`、`Check-LiteProfile.py`、`Test-RelocatedBundle.py`、`Test-ReleaseSmoke.py`、`Verify-ReleaseAsset.py`、`Test-FoundationTransport.py`（当时只覆盖六个版本）；`Package-*.py`、`Test-WorkerIsolation.py`（退役）、`Test-HostBehaviorParity.py` 已不在 `scripts/` 中。
- 测试：HttpTests 的传输与无 TIA 模式以及 `WorkerSupervisorTests` 退役或改换目标（两者均已删除）；新增 `tests/FoundationHost/TiaMcp.EngineHost.Tests`（假 worker），并在 `tests/test-suites.json` 中注册套件；WorkerChannel 测试增加 Engine 用例；sdk-only 捕获需要一个 worker 能读到的、仅供测试的就绪覆盖（当时 `TIA_MCP_TEST_READINESS_READY` 只被 HttpTests 的 `Program.cs` 认可），按测试构建的程序集元数据门控，与 `TiaMcpTestPolicy` 相同。

## 5 一致性判据

- 经 FoundationHost 的契约（full 与 lite，stdio）与 `baseline/20.json`、`baseline/21.json` 逐字节相等：名称、schema、`descriptionSha256`、`liteTools`、`behaviorCapabilities`、档位 full-engine。
- 经 FoundationHost 与 worker 的响应（sdk-only 与 packaged-no-tia）：两次捕获一致、`rawTextBlocks` 哈希、覆盖清单、直接调用与经 lite 桥接的 `REJECT_ARGUMENTS`；与 `responses/2x.json` 比较时使用经评审的迁移允许清单。预期差异：`GetOpennessWorkerStatus`/`RestartOpennessWorker` 返回监管快照而不是 `{"state":"InProcess"}`；`GetSessionState`/`InitializeEnvironment` 中可能出现的 worker 与证据字段；隔离措辞（保留到 P7-08）；requestId 屏蔽。
- 两者并存期间，用同一构建对引擎和新宿主做 A/B 捕获。另核对：提示词与资源一致；一致性脚本的第三条路径（EngineHost 加假 worker）；P6-58 的延迟（worker 忙 60 秒时本地工具 p95 小于 100 毫秒）；IL 与织入清单不变（`Compare-NativeCallOrder.py`、`native-call-coverage.json`）；切换默认前完成 L5。

## 6 风险

- 绑定 `MethodInfo` 的桥接（先重构，P7-02）。
- net48 与 net10 重新序列化导致原始字节漂移（没有装饰时原样传递 worker 文本）。
- MTA 与所属线程上的进度。
- 当时取消、超时与超限都会使通道失效。
- 没有 TIA 时的就绪状态（没有 Foundation 被动探测文本）。
- Siemens 程序集解析与选件包（同一 EXE 保留解析器、exe.config 重定向、延迟加载、首次附着确认身份）。
- 从 `worker/` 子目录确定包根与数据根。
- 审计与调用日志的重复行（worker 从不开始审计）。
- 能力测试策略不一致（启动时核对）。
- TIA 进程丢失的语义。
- HTTP 会话（原文为每个宿主进程共享一个 worker，已由 P7-07a 改为每个会话一个）。
- 升级路径（更新器清单、CLI 路径、客户端配置；无动词运行引擎 EXE 时打印新命令）。
- 与进行中的 P6-69、P6-70 重叠。
