# 0008 worker 调用按类超时，故障使 worker 代次失效且从不重放

- 状态：已采纳
- 日期：2026-10-08
- 决策者：主导者受维护者委托
- 置信度：高
- 相关：[阶段 7 设计](../design/p7-host-worker-split.md)；[重构计划](../refactor-plan.md) P7-05；[4.0 发布说明草稿](../../releases/v4.0.0.md)；[架构概览](../design-spec/architecture.md) 第 4 节与第 6.3 节；[V4 工具协议规范](../design-spec/v4-protocol.md) 第 10 节；[ADR 0007](0007-one-worker-per-mcp-session.md)

回溯记录（写于 2026-10-11）。

## 背景

Openness 调用无法中途安全停止，也无法从异常推断写入是否已经发生。阶段 7 之前，worker 客户端固定 2 分钟超时，取消、超时和超大消息都会使通道失效，worker 也从不重启；编译、下载、导入导出等长操作可能在 2 分钟内完不成。

## 选项

1. 统一的固定超时：简单；长操作会被误判为结果未知。
2. 按类超时并允许配置：长操作有足够的预算；需要维护分类表。

故障后的处理另有两种做法：自动重试或重启（可能重复写入），或者由人检查后显式恢复。

## 决定

采用选项 2，故障后显式恢复。八版（V14 SP1–V21）的 worker 调用默认 120 秒，worker 代次首次附着 300 秒，编译、下载上传、导入导出、归档和库更新 30 分钟，可用 `--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 覆盖（`WorkerTimeoutPolicy`）。派发前取消返回 `CANCELLED`；派发后宿主等待真实结果。worker 退出、卡死、坏帧或 TIA 进程丢失使 worker 代次故障，须显式调用 `RestartOpennessWorker` 或 `DisconnectPortal` 后重新绑定；失败的请求从不重放。

## 后果

- 正面：长操作不再因超时被误判；故障后的状态由人确认，不会重复写入。
- 负面与取舍：worker 通道超时（含只读调用）一律按结果未知处理（API-26，有意偏差）；客户端自己的超时可能早于 30 分钟预算（MCP-06，4.0 前加进度心跳）。
- 后续工作：维护者于 2026-10-11 决定的 P8-64 已实现：V14 SP1–V19 的 PLC worker 共用按类超时策略，见 [设计规范索引](../design-spec/README.md#决定摘要)。
- 验证方式：WorkerChannel 测试与 worker 故障注入夹具（`TiaMcp.EngineWorker.FaultFixture`）。
