# worker 通道协议 2

状态：现行 · 最后核对：2026-10-11，master `578a05ee`

[架构概览](architecture.md) · [V4 协议](v4-protocol.md) · [历史适配器设计](../adapter-merge.md)

本页承接阶段 4 设计中的协议 2 定义。线上协议以 `src/WorkerChannel` 的帧、状态机和各 profile 为准；MCP 对外信封与内部通道信封分开。Foundation 使用 `adapter.*`；V20/V21 引擎 worker 使用 Engine profile。旧引擎隔离协议已退役，旧 Studio profile 仅供待清理桥接使用。

## 分帧与握手

每行一个无 BOM 的 UTF-8 JSON-RPC 2.0 对象。请求上限为 1 MiB，worker 输出行上限为 16 MiB，不计末尾 LF。先限长，再验证 UTF-8 和 JSON；缺少换行的末尾、空行、BOM、重复或未知信封字段被拒绝。发送也执行相同限制。

worker 首先发送无 id 的 `hello` 通知。`params` 包含 `protocol: 2`、`releaseKey`、`workerSha256`、`adapterSha256`、`pid`、64 位十六进制 `nonce`、`bindingEpoch: 0` 和 `bound: false`。宿主核对实际文件哈希、子进程 PID 和每次启动生成的 32 字节随机 nonce；worker 必须新建且未绑定，不接受协议降级。

## 请求、结果与进度

只有一个在途请求，正整数 id 严格递增。Foundation 请求含 `jsonrpc`、`id`、`method`、原参数对象 `params` 和 `bindingEpoch`。Foundation 与 Engine 都接受可选的 `requestId` 和 `toolName`：两者限 1–128 个 ASCII 字符，字符集为 `A-Z`、`a-z`、`0-9`、`-`、`_`、`.`、`:`；`toolName` 需要同时提供 `requestId`。非法值直接拒绝，不截断。客户端拒绝时不发送、不消耗 id；worker 拒绝时不派发且使通道失效。Studio 不接受这些字段。必须先验证 hello。通道重入或并发使用使会话失效；宿主的调度在通道之外。

宿主为 served tool 的每次 `adapter.*` 派发记录同一宿主 request id 下的 `worker:<规范工具名>` BEFORE / RETURNED / THREW。worker 使用相同 id 和工具名写入 `callProjection: 1` 的 BEFORE / RETURNED（未完成为 INTERRUPTED）行；适配器原生行也使用此 id。内部 DTO 派发投影只记录身份，不记录参数与结果；MCP 边界的既有脱敏投影保留内容。

回复包含相同 id、`bindingEpochBefore`、`bindingEpochAfter`，以及唯一的 `result` 或 `error`。错误保留原 code/message，`data` 包含 `outcome`（`RejectedBeforeNative`、`ReadFailed` 或 `Unknown`）及 `evidence`（对象或 null）。各宿主 profile 负责映射，不把内部 outcome 当作 MCP 外部枚举。

Foundation 与 Engine 共用超大回复处理：先完整编码，超过 16 MiB 时，在发送任何回复字节前调用已注册的 spill 回调，返回 `result.workerReplySpill`（`id` 为 32 位 GUID N、`byteLength`、`sha256`）。文件位于数据根既有 `worker-spills` 目录；宿主检查身份、普通文件、UTF-8、长度、哈希与 JSON，验证后删除该文件。没有 spill 回调且提供有界 `OversizedResultJson` 时，发送该拒绝 result（例如 `error.code=LIMIT_EXCEEDED`），不改变拒绝内容。未配置这两种回退或 spill 写入失败时，保留原有未知结果/通道失效行为，不重试派发。两种客户端按相同的 8 MiB 序列化结果阈值寄存为既有导出句柄；旧版的 shared-session 模式不因此改变。

Foundation 仍只接受百分比进度；含 `payload` 的进度仅用于 Engine profile，保留其现有测试，直到引擎退役任务确认覆盖迁移。

`progress` 是无 id 通知，含 `requestId`、从 1 递增的 `sequence` 和 0–100 的 `percent`。每个请求最多 1,024 条通知；只能发送当前调用的进度。跨线程和回调结束后的进度被拒绝。空闲期间进度、旧或未知 id、重复回复、再次 hello 都使通道失效；进度不延长调用预算。

## 绑定、故障与取消

绑定纪元来自 worker 每次派发前后的纯托管状态缓存，不为核对身份额外调用 Openness。PID、工程路径、所有权或本地会话状态改变时递增；回复的 before 必须与宿主保存的纪元相同。附着、打开、创建、关闭和断开按状态机验证变化。外部改绑检测与 TIA 进程租约是独立边界，见[架构概览](architecture.md)。

派发前取消不发送、不消耗 id、不使后续调用失效。派发后的写入或读取错误、超时和无可信回复导致未知结果及代次失效，禁止重试或自动重启。产品的 MCP 取消在派发后等待真实结果；通道级终止不能证明 TIA 已取消。释放通道只关闭自己的管道，不结束 TIA。

默认、首次附着和长操作预算见 [ADR 0008](../decisions/0008-worker-timeouts-and-faults.md)。每会话 worker 与线程归属见 [ADR 0007](../decisions/0007-one-worker-per-mcp-session.md)。

## 验证

[worker-channel 套件](../../../tests/WorkerChannel/TiaMcp.WorkerChannel.Tests/README.md)覆盖双端状态机与独立进程故障，`TiaMcp.WorkerChannel.TransportFixture` 提供协议替身。这些证据不替代原生行为验收。
