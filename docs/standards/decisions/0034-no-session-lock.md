# 0034 结果未知只影响本次调用，取消会话锁

- 状态：已关闭（Parts 1–8 已实现，真实 TIA 行为仍待真机复验）
- 日期：2026-11-15
- 决策者：维护者 2026-11-05, recommendations approved 2026-11-11
- 相关：[ADR 0008](0008-worker-timeouts-and-faults.md)、[ADR 0007](0007-one-worker-per-mcp-session.md)、[设计规范第 4.11 节](../design.md#411-未知结果与未确认调用)

## 背景

宿主、worker、通道、绑定回调和 HMI 读取代码分别缓存了故障后的会话锁。一次已经得到回复的未知结果也会禁止后续独立调用，迫使使用者断开会话和重启 worker。Siemens Openness 文档没有要求这种全会话限制。

原生调用在 worker 的所属线程同步运行。外部线程无法安全取消正在执行的工程调用。取消会话锁仍须保留运行期间的串行约束和实际工程身份校验。

## 决定

结果未知是调用的事实。该调用返回 `OUTCOME_UNKNOWN`，禁止自动重放。后续独立请求继续经过参数、版本、项目身份、预览哈希、审批和 stale-plan 检查。

未知结果不触发自动保存、打开项目或重放。一个 MCP 会话仍对应一个 worker，同一 TIA 进程仍由一个会话持有租约。

### 未确认调用

只有非预览调用的最终 `meta.outcome` 为 `unknown`，且类别是 WRITE、ONLINE-WRITE、FILE 或 EXECUTE 时，才加入未确认清单。读取失败、编译错误、确定的批次失败、已核实回滚以及带旧标志的成功结果均按各自最终信封处理。

宿主共用 `UnsettledCalls`，记录请求编号、工具、UTC 时间和供后续相同写入识别使用的摘要。`GetSessionState.data.sessionState.unsettled` 暴露 `{requestId, tool, utc, fromPreviousOwner}`。摘要覆盖工具、去掉 `dryRun` 和 `confirm` 的规范化参数、绑定项目文件，不向调用者暴露参数内容。

`SaveProject` 和 `SaveProjectCopy` 在清单非空时通过 `UNSETTLED_CALLS` 列出请求编号，预览和执行都提示。绑定到不同项目或会话结束清除清单。重启 worker、断开和失去绑定不清除清单。

Part 7 增加确定的只读结算。未知调用结束后，在原执行通道用既有读操作核对；worker 丢失时在下一次精确重绑后核对。根标签表创建核对名称。标签和用户常量创建核对名称、类型和地址或值。外部源删除核对根名称清单，两种方向均可结算。无覆盖根导入只在清单包含请求文件基本名（含扩展名）时记 `applied`，名称缺失继续未知并保留相同写入保护，因为 TIA 可能另行命名。五种 PLC 单文件导出检查按原参数推导的精确文件，非空为 `applied`，不存在为 `not-applied`。`ExportProjectTexts` 由 TIA 直接写最终文件，只在文件不存在时记 `not-applied`，存在的文件即使非空也继续未知，因为可能被中断而不完整。声明不匹配、空文件和读取失败都继续未知。覆盖导入、编译、子组标签表创建及其他无确定读回的操作没有结算行。宿主仅保存结算所需目标字段，不保留导入内容。

每个原生补读使用独立请求身份，固定等待 5 秒，作为默认 120 秒操作预算之外的短暂可选核对。补读对话框和超时不会分离调用者的响应或发布运行中写入。超时继续未决，后台排空期间暂拒绝下一条原生请求，并明确说明补读仍在排空。恢复时每次最多尝试一项可结算记录，后续重绑轮换到其余记录。恢复补读遵循当前请求的取消令牌，只有实际尝试的补读使通道失效或忙碌时，才以补读原因拒绝当前待派发请求。

原信封保留 `OUTCOME_UNKNOWN`，`data.settlement` 和 `sessionState.settled[]` 增加 `{requestId, tool, outcome, utc}`。结算项移出 `unsettled` 并停止保存警告；`not-applied` 解除相同写入保护，`applied` 保持保护直到调用者读取 `GetCallResult`。重绑后的结论也装饰保留的原结果及被淘汰结果的摘要。所有结算只读，绝不重发原写入。未结算的 `ExportProjectTexts` 现在也保留写入摘要，并在 `SaveProject` / `SaveProjectCopy` 时产生 `UNSETTLED_CALLS`。

### 正在运行的调用

Part 2 将读取和写入统一为同一机制。达到客户端等待预算时返回 `in-progress`，worker 继续等待真实结果。其他原生调用得到暂时的 `worker-call-running` 前提拒绝，其中包括工具、请求编号、开始时间和已报告的对话框。

原调用完成后，自行解除忙碌状态。最终结果通过 host-only 的 `GetCallResult(requestId)` 读取，完整执行原调用的绑定更新、审批完成和审计处理。结果保留在会话内存中，最多保留最近 16 个已完成结果及正在运行的结果。`in-progress` 的错误码为 `OPERATION_IN_PROGRESS`，`ok=false`，`execution` 和 `completeness` 均为 `unknown`。回复中的 `activeRequestId` 和最终信封自己的 `meta.requestId` 都能查询同一结果。未知编号返回 `NOT_FOUND`。完整结果按完成顺序淘汰。淘汰后仅为仍受保护的未读写入和未读未确认编号保留摘要，包含工具、结果分类、时间，并提示原始 payload 已无法取回。读取摘要解除相同写入保护并删除该摘要，之后该编号返回 `NOT_FOUND`。已读取的完整结果在最近 16 项范围内仍可查询。批次内部未知写入编号映射到外层结果，读取外层结果或摘要时一并确认这些内部编号。

相同写入在仍运行、结果未读取或未确认结果尚未读取时返回 `same-call-unsettled`，在参数校验后、审批前检查。相同写入身份覆盖工具、去掉 `dryRun` 和 `confirm` 的规范化参数及绑定项目文件，文件系统路径按大小写、分隔符和首尾空白规范化。审批与 plan hash 使用各自原有公式。非布尔 `dryRun` 继续按写入分类，并由参数校验拒绝。调用者通过 `GetCallResult` 读取后仍须检查目标，才能决定是否发起新的写入。预览继续可用。

会话关闭推迟到原生调用结束。`DisconnectPortal` 和 `RestartOpennessWorker` 在调用进行中返回忙碌拒绝。维护者批准 D5，由人在 TIA 回答对话框、关闭 TIA，或通过工作台已有的人工强制停止宿主操作结束无法返回的调用。

### worker 恢复与进程租约

Part 4 在下一次需要原生 worker 的调用上恢复丢失的 worker。恢复前核对旧 worker 没有未答复的已派发请求，而且不是仍然存活、拥有自己启动的 TIA 的 worker。宿主只重建自己的 worker 和句柄。

恢复目标记录 PID、进程开始时间、项目文件和项目名。相同 PID 和开始时间可以重新绑定。新 PID 只有在该版本恰好一个进程打开完全相同的项目文件，且 `AttachedSessions` 为空时才允许重新绑定。歧义、进程消失或项目未打开时返回对应事实，下一次请求可再尝试。

恢复增加绑定 generation，使旧预览和审批身份失效。`WORKER_RESTARTED` 在恢复后的首个结果中说明代次、原因、是否重绑以及丢失的 worker 状态，`sessionState.workerRecovery` 保留该记录。自动恢复保留导出句柄、暂存、生成记录、未确认调用与迟到结果，并保持工作台中的会话活动状态。显式重启继续清理导出句柄和重绑目标。已附着 TIA 中的未保存项目更改仍由 TIA 持有。由 worker 自己启动的 TIA 不自动关闭或重新打开。

租约随持有进程结束。死持有者留下的 BUSY、UNCERTAIN 或遗留 ACTIVE 记录在新连接时转为 `PREVIOUS_OWNER_UNCLEAN` 警告。IDLE 和 RELEASED 记录允许连接，保持无此警告。`data.previousOwner` 保留前持有者状态与请求编号。受保护的活持有者仍阻止另一写入者。断开后允许在同一 MCP 会话重新连接。

同一宿主中的被遗弃会话，在没有调用、审批、工作台参与、操作端所有权或自启 TIA 的情况下，闲置至少 10 分钟或新连接显式给出 `takeOver=true` 时可以让出。健康通道的移交执行所属线程上的非拥有式 Disconnect。若它仍在执行，移交保持等待中的事实。

Part 6 已实现该移交。宿主使用持有者的非阻塞通道和会话准入保留。健康通道确认 Disconnect 后继续新连接，符合条件且无法确认断开的故障持有者通过受保护的 worker 停止释放租约后继续。移交写入审批审计，未确认编号以 `fromPreviousOwner=true` 进入新会话。旧会话的其他原生操作得到 `taken-over` 并提示自行调用 `ConnectPortal`，自动恢复保留该移交事实。`DisconnectPortal` 在本地幂等结清并携带接管警告。被阻塞的 Disconnect 返回新会话的 `in-progress`，完成后继续原连接流程。另一宿主的租约拒绝通过 `.holder` 侧车给出宿主 PID、发布键和释放方式。

### 对话框与预算

维护者批准 D2 和 D3。对话框事件通过有界、去敏的 stderr 诊断传到宿主。允许的默认拒绝仍使用 Cancel、Abort、No 或 NoToAll。记录后可处理 Notification 和只有 Ok 的确认，Yes 类选择由人决定。Win32 探测仅观察窗口标题和启用状态。

Part 5 已实现 `TIA_MCP_DIALOG` 结构化 stderr 上报，每行最多 2 KiB。宿主去敏后将记录放入 `running.dialog`，保留报告时间并提示窗口可能仍打开。未回答的确认立即触发 Part 2 的 `in-progress`，最终结果继续由 `GetCallResult` 取回。忙碌答复和工作台显示该记录。只读 Win32 探测在构建运行中答复时填入 `running.window`，包含主窗口禁用状态与可见顶层窗口标题。

维护者批准 D7。默认等待预算为 120 秒，首次附着和编译、传输、导入导出、归档、库更新均为 300 秒，`--worker-timeout-config` 和 `TIA_MCP_WORKER_TIMEOUTS` 保留覆盖入口。预算到期触发客户端响应分离，原调用继续完成。

### 契约与兼容

4.0 尚未正式发布，此次按 ADR 0001 硬切，`schemaVersion` 仍为 4。Part 1 停止产生 `SESSION_RESET_REQUIRED`，移除未知结果中的 `data.recovery`、会话状态中的重置字段和操作端锁状态，增加未确认清单及保存警告。

Part 8 已完成：8a 逐点重分类，8b 移除 `meta.requiresSessionReset`、`SessionResetRequiredDetails` 和 `SESSION_RESET_REQUIRED`，worker 字段改为 `OutcomeUnknown`，已有 `NativeOutcomeUnknown` 的批次复用该字段，不增加重复字段。维护者 2026-11-15 批准在 beta.5 按 ADR 0001 硬切，`schemaVersion` 仍为 4。宿主准入和未确认清单以最终 `meta.outcome` 为准，调用方读取 `GetSessionState.unsettled`。原有回复形状及伪造字段校验继续执行。

Part 8 逐个重分类标志设置点后，才将 worker 字段改为 `OutcomeUnknown` 并删除旧字段。调用结果分类以 VMFIX-7 D1 和真实证据为准。

## 标准偏差

本项目尚未采纳工程软件 MCP 草案。对草案 WS-23、WS-24、WS-41 要求的未知结果后全会话暂停，本决策记录有意偏差。项目选择保留调用级未知结果、禁止重放和可核对请求编号，继续允许独立调用。采纳范围登记在 [规范索引](../README.md)。

## 实施与验证

NO-LOCK 分八部分实施。Part 1 移除会话锁并增加未确认清单。Part 1 同时保留经审阅的相同 apply 请求保护，已有未知结果且参数与审阅哈希相同的执行请求在派发前返回 `same-call-unsettled`。Part 2 实现运行中与迟到结果，并完成相同写入识别及结果读取后的解除机制。Part 3 调整租约和断开后重连。Part 4 实现 worker 自动恢复。Part 5 实现对话框和预算。Part 6 实现被遗弃会话让出。Part 7 实现有证据的只读核对。Part 8 清理旧字段。

Part 2 已实现调用等待预算到期后的响应分离、迟到结果取回、相同写入保护和会话释放延迟。host-only 的状态探测仍受预算约束，故障按真实传输失败处理。Part 4 将真正的通道故障、宿主探测故障及 `portalLost` 转为延迟恢复触发条件。host-only 状态读取保持缓存视图。恢复失败报告 TIA、项目或连接的当前事实，下一次原生调用继续尝试。Part 3 已将租约内容改为记录，保留未决编号并在新会话标记 `fromPreviousOwner`。确认断开写入 `RELEASED <ids>`。同一 MCP 会话的显式 `ConnectPortal` 启动新代次，旧 worker 的原生调用序列保持原样。断开与显式重启移除 `requiresTiaRestart`、`recoveryMessage` 及对应旧字段，断开状态为 `disconnected`。

2026-11-12，维护者批准本任务所有纯会话锁断言改为本次错误保留、后续独立调用可用。每项断言变更须在任务结果表中列出文件位置、原值和新值。真实结果、禁止重放、错误调用拒绝、项目身份、审批及 stale-plan 断言必须保持。

离线测试验证八版调用、通道、绑定和工作台状态。真机核对尚未运行，清单见 [beta 测试者清单](../../guides/beta-tester-checklist.md)。Studio 自有 bridge 的延迟重启由 D8 指定为独立后续任务。

2026-11-13，维护者批准 Part 6 第二轮决策。故障但仍存活的 attached holder 适用相同的 10 分钟闲置或 takeOver=true、工作台参与和 operator 所有权条件。运行中或未回答的派发请求，以及自启 TIA 的 worker，继续优先拒绝移交。符合条件而无法确认 Disconnect 时，复用 ResetDisconnectedWorker 的受保护停止路径释放租约，审计结果为 taken-over-worker-stopped，未确认编号传入新会话。健康通道确认移交的审计结果仍为 taken-over。被接管会话的 DisconnectPortal 在本地幂等结清并携带接管警告，其他原生操作继续返回 taken-over。
