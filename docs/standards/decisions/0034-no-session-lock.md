# 0034 结果未知只影响本次调用，取消会话锁

- 状态：已采纳
- 日期：2026-11-12
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

`SaveProject` 和 `SaveProjectCopy` 在清单非空时通过 `UNSETTLED_CALLS` 列出请求编号，预览和执行都提示。绑定到不同项目或会话结束清除清单。重启 worker、断开和失去绑定不清除清单。后续阶段支持迟到结果和只读核对消解，提供确定证据前保持原始调用的未知结果。

### 正在运行的调用

后续阶段将读取和写入统一为同一机制。达到客户端等待预算时返回 `in-progress`，worker 继续等待真实结果。其他原生调用得到暂时的 `worker-call-running` 前提拒绝，其中包括工具、请求编号、开始时间和已报告的对话框。

原调用完成后，自行解除忙碌状态。最终结果通过 host-only 的 `GetCallResult(requestId)` 读取，完整执行原调用的绑定更新、审批完成和审计处理。结果保留在会话内存中，最多保留最近 16 个已完成结果及正在运行的结果。

相同写入在仍运行、结果未读取或未确认结果尚未读取时返回 `same-call-unsettled`。调用者通过 `GetCallResult` 读取后仍须检查目标，才能决定是否发起新的写入。预览继续可用。

会话关闭推迟到原生调用结束。`DisconnectPortal` 和 `RestartOpennessWorker` 在调用进行中返回忙碌拒绝。维护者批准 D5，由人在 TIA 回答对话框、关闭 TIA，或通过工作台已有的人工强制停止宿主操作结束无法返回的调用。

### worker 恢复与进程租约

后续阶段在下一次需要 worker 的调用上恢复丢失的 worker。恢复前核对旧 worker 没有未答复的已派发请求，而且不是仍然存活、拥有自己启动的 TIA 的 worker。宿主只重建自己的 worker 和句柄。

恢复目标记录 PID、进程开始时间、项目文件和项目名。相同 PID 和开始时间可以重新绑定。新 PID 只有在该版本恰好一个进程打开完全相同的项目文件，且 `AttachedSessions` 为空时才允许重新绑定。歧义、进程消失或项目未打开时返回对应事实，下一次请求可再尝试。

恢复增加绑定 generation，使旧预览和审批身份失效。`WORKER_RESTARTED` 说明代次、原因、是否重绑以及丢失的 worker 状态。已附着 TIA 中的未保存项目更改仍由 TIA 持有。由 worker 自己启动的 TIA 不自动关闭或重新打开。

租约随持有进程结束。死持有者留下的 BUSY、UNCERTAIN 或 ACTIVE 记录在新连接时转为 `PREVIOUS_OWNER_UNCLEAN` 警告，保留前持有者状态与请求编号。活持有者仍阻止另一写入者。断开后允许在同一 MCP 会话重新连接。

同一宿主中的被遗弃会话，在没有调用、审批、工作台参与、操作端所有权或自启 TIA 的情况下，闲置至少 10 分钟或新连接显式给出 `takeOver=true` 时可以让出。移交执行所属线程上的非拥有式 Disconnect。若它仍在执行，移交保持等待中的事实。

### 对话框与预算

维护者批准 D2 和 D3。对话框事件通过有界、去敏的 stderr 诊断传到宿主。允许的默认拒绝仍使用 Cancel、Abort、No 或 NoToAll。记录后可处理 Notification 和只有 Ok 的确认，Yes 类选择由人决定。Win32 探测仅观察窗口标题和启用状态。

Part 1 保留默认 120 秒、首次附着 300 秒、长类 1800 秒的现有预算。Part 5 将长类等待预算调整为 300 秒，保留配置入口。预算决定客户端等待时间的最终机制在 Part 2 实现。

### 契约与兼容

4.0 尚未正式发布，此次按 ADR 0001 硬切，`schemaVersion` 仍为 4。Part 1 停止产生 `SESSION_RESET_REQUIRED`，移除未知结果中的 `data.recovery`、会话状态中的重置字段和操作端锁状态，增加未确认清单及保存警告。

`meta.requiresSessionReset` 和 worker 的 `RequiresSessionReset` 在 Part 8 前继续透传。宿主准入和未确认清单以调用的最终结果为准。Part 8 逐点重分类前，结果映射继续将旧标志作为写入结果未知的证据，读取不受该标志影响。旧 `SessionResetRequiredDetails` 的读取类型保留到 Part 8。原有回复形状及伪造字段校验继续执行。

Part 8 逐个重分类标志设置点后，才将 worker 字段改为 `OutcomeUnknown` 并删除旧字段。调用结果分类以 VMFIX-7 D1 和真实证据为准。

## 标准偏差

本项目尚未采纳工程软件 MCP 草案。对草案 WS-23、WS-24、WS-41 要求的未知结果后全会话暂停，本决策记录有意偏差。项目选择保留调用级未知结果、禁止重放和可核对请求编号，继续允许独立调用。采纳范围登记在 [规范索引](../README.md)。

## 实施与验证

NO-LOCK 分八部分实施。Part 1 移除会话锁并增加未确认清单。Part 1 同时保留经审阅的相同 apply 请求保护，已有未知结果且参数与审阅哈希相同的执行请求在派发前返回 `same-call-unsettled`。Part 2 实现运行中与迟到结果，并完成相同写入识别及结果读取后的解除机制。Part 3 调整租约和断开后重连。Part 4 实现 worker 自动恢复。Part 5 实现对话框和预算。Part 6 实现被遗弃会话让出。Part 7 实现有证据的只读核对。Part 8 清理旧字段。

Part 1 中真正的通道故障仍通过 `PRECONDITION_FAILED` 的 `worker-faulted` 条件提示显式重启 worker。现有预算、只读超时排空及租约语义在其所属部分实施前继续适用。

2026-11-12，维护者批准本任务所有纯会话锁断言改为本次错误保留、后续独立调用可用。每项断言变更须在任务结果表中列出文件位置、原值和新值。真实结果、禁止重放、错误调用拒绝、项目身份、审批及 stale-plan 断言必须保持。

离线测试验证八版调用、通道、绑定和工作台状态。真机核对尚未运行，清单见 [beta 测试者清单](../../guides/beta-tester-checklist.md)。Studio 自有 bridge 的延迟重启由 D8 指定为独立后续任务。
