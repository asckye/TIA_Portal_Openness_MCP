# 0008 worker 调用按类超时，故障使 worker 代次失效且从不重放

- 状态：部分被 0034 取代
- 日期：2026-10-08
- 决策者：主导者受维护者委托
- 置信度：高
- 相关：[阶段 7 设计](../../archive/development/design/p7-host-worker-split.md)；[重构计划](../../plan/plan.md) P7-05；[4.0 发布说明草稿](../../releases/v4.0.0.md)；[架构概览](../design.md) 第 4 节与第 6.3 节；[V4 工具协议规范](../design.md) 第 10 节；[ADR 0007](0007-one-worker-per-mcp-session.md)

回溯记录（写于 2026-10-11）。

## 背景

Openness 调用无法中途安全停止，也无法从异常推断写入是否已经发生。阶段 7 之前，worker 客户端固定 2 分钟超时，取消、超时和超大消息都会使通道失效，worker 也从不重启；编译、下载、导入导出等长操作可能在 2 分钟内完不成。

## 选项

1. 统一的固定超时：简单；长操作会被误判为结果未知。
2. 按类超时并允许配置：长操作有足够的预算；需要维护分类表。

故障后的处理另有两种做法：自动重试或重启（可能重复写入），或者由人检查后显式恢复。

## 决定

采用选项 2，故障后显式恢复。八版（V14 SP1–V21）的 worker 调用默认 120 秒，worker 代次首次附着 300 秒，编译、下载上传、导入导出、归档和库更新 300 秒（2026-11-13 按 NO-LOCK D7 修订，原值 1800 秒），可用 `--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 覆盖（`WorkerTimeoutPolicy`）。派发前取消返回 `CANCELLED`；派发后宿主等待真实结果。worker 退出、卡死、坏帧或 TIA 进程丢失使 worker 代次故障，须显式调用 `RestartOpennessWorker` 或 `DisconnectPortal` 后重新绑定；失败的请求从不重放。

## 后果

- 正面：长操作不再因超时被误判；故障后的状态由人确认，不会重复写入。
- 负面与取舍：worker 通道超时（含只读调用）一律按结果未知处理（API-26，有意偏差）；客户端自己的超时可能早于 300 秒长类等待预算（MCP-06，4.0 前加进度心跳）。
- 后续工作：维护者于 2026-10-11 决定的 P8-64 已实现：V14 SP1–V19 的 PLC worker 共用按类超时策略，见 [设计规范索引](../design.md)。
- 验证方式：WorkerChannel 测试与 worker 故障注入夹具（`TiaMcp.PlcWorker.FaultFixture`）。

## 2026-11-11 修订

维护者批准 VMFIX-7 的 D1。八版宿主按调用性质和已知证据分类。只读调用丢失 worker 应答时返回 `read-failed`、`read-only` 和 `NATIVE_OPERATION_FAILED`。只读调用超过 worker 预算时返回 `read-failed`、`read-only` 和 `TIMEOUT`，等待排空后恢复使用。已派发的原生写入缺少确定结果的证据时返回 `unknown` 和 `OUTCOME_UNKNOWN`。

此修订替代上文 API-26 对只读调用的结果分类。worker 代次故障后的显式恢复仍按[设计规范第 4.11 节](../design.md#411-未知结果会话锁与恢复)执行。失败请求从不重放。

## 2026-11-12 会话锁移除

[ADR 0034](0034-no-session-lock.md) 取代故障结果导致会话锁定、answered-unknown 后显式恢复的规则。只读丢失回复的分类沿用 VMFIX-7 D1。Part 2 将客户端等待预算到期后的读取和写入统一返回 `in-progress`，通过 `GetCallResult` 取回真实结果。调用期间拒绝断开和重启，会话关闭延迟释放。host-only 状态探测继续按预算报告故障。NO-LOCK Part 5 按维护者批准的 D7 将五个长类预算从 1800 秒改为 300 秒，默认 120 秒、首次附着 300 秒和配置覆盖入口保持现行值。未回答的 TIA 确认即时返回同一路径的 `in-progress`，并携带去敏后的对话框记录。真正的 worker 传输故障在 Part 4 前仍要求显式重启 worker。
