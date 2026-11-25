# 0048 工程操作页改为共享 MCP 会话的操作面板

- 状态：已采纳
- 日期：2026-10-08
- 决策者：维护者
- 置信度：高
- 相关：[工作台控制设计](../../archive/development/design/workbench-control-and-shared-session.md)第 1.3、5、7 节与“维护者决定（2026-10-08）”；[重构计划](../../archive/development/refactor-plan.md) P8-21；[设计规范](../design.md)“操作端与共享会话”；[ADR 0007](0007-one-worker-per-mcp-session.md)、[0011](0011-tia-process-lease-on-all-releases.md)、[0029](0029-authoritative-session-state.md)；提交 `02a4c0f31`、`0c5d7e867`、`c7e87507c`

回溯记录（写于 2026-11-25，待补写编号 B18）。

## 背景

当时工程操作页经桥接进程自建一条 TIA 连接，与 MCP 会话互不知情；保存、导入等写入不经过审批、审计和调用日志，且 V14 SP1–V19 的 worker 与工作台桥接都不取进程租约，可同时附着同一 TIA。

## 选项

会话模型：

1. 面板总是自有一个 worker：违背“同一 worker”，两个 worker 附着同一 TIA，状态不共享。
2. 面板附着到已有 AI 会话：同一宿主、worker、审批与审计，人可以接手；生命周期随 AI 会话。
3. 宿主级命名会话，双方都去附着：改变 ADR 0007 的语义，重新引入一方断开影响另一方的风险。

面板传输中，工作台作为 HTTP 客户端、自拉 stdio 宿主、进程内承载宿主都不能既共享 AI 的 worker 又可靠区分人与 AI，因此采用宿主提供的操作端端点。

## 决定

采用选项 2，并补充没有 AI 会话时的自有会话：

- 默认附着 AI 正在使用的会话；没有 AI 会话时，只在工作台启动的宿主中自建会话，第一期 AI 不加入。
- “人/AI”由传输决定，不取自参数。
- 面板发起的写仍须在审批卡上批准，面板自动打开对应审批卡。
- 面板“断开”前提示会结束 AI 的会话；V14 SP1–V19 也启用 TIA 进程租约（ADR 0011）。

## 后果

- 正面：人与 AI 看到同一状态，工作台的写入同样可追溯。
- 负面与取舍：面板随 AI 会话结束而失去附着；双方共用一条 Openness 通道，长操作互相阻塞。
- 落实：P8-21a–e。后续工作：P8-21f 删除工作台重复的 TIA 适配代码，状态见[开发计划](../../plan/plan.md)。
- 验证方式：`ApprovalHostTests` 与 `PipelineTests` 的人/AI 归属用例。
