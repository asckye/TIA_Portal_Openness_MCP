# 0011 八个版本都用 TIA 进程租约把一个 TIA 进程保留给一个 MCP 会话

- 状态：已采纳
- 日期：2026-10-08，2026-11-13 按 [ADR 0034](0034-no-session-lock.md) 修订
- 决策者：维护者
- 置信度：高
- 相关：[工作台控制设计](../../archive/development/design/workbench-control-and-shared-session.md)“维护者决定（2026-10-08）”；[运行时布局](../design.md)“TIA 进程租约”；[4.0 发布说明草稿](../../releases/v4.0.0.md)；[重构计划](../../plan/plan.md) P8-21；[架构概览](../design.md) 第 6.2 节

回溯记录（写于 2026-10-11）。

## 背景

V20/V21 已经对选定的 TIA 进程取租约，V14 SP1–V19 没有。工作台改为共享 MCP 会话的操作面板（P8-21）后，两个会话可能附着同一个 TIA 进程并同时写入；写入结果未知后，也需要阻止别的会话接管可能仍在执行的进程。

## 选项

1. 只在 V20/V21 取租约：旧版保持原样；旧版上多个会话可能同时写同一工程。
2. 八个版本统一取租约：同一规则；旧版需要一次真机复验。

## 决定

采用选项 2（P8-21b 于 2026-10-09 实施）。八个版本都在附着前对选定的 TIA 进程取租约，使用相同的租约文件（`<数据根>/leases/<PID>-<进程启动 UTC ticks>.lease`）和拒绝文本；一个 TIA 进程只服务一个 MCP 会话。租约状态为 `IDLE`、`BUSY`、`UNCERTAIN`、`RELEASED`：

- 另一个存活的会话持有时返回 `LeaseReserved`；
- 租约随持有者结束。独占文件句柄能够重新打开时，新会话可以取得租约，包括 `BUSY`、`UNCERTAIN` 和 3.x 遗留的 `ACTIVE` 记录。
- `BUSY <requestId>` 记录正在执行的请求。未决编号以逗号分隔，结果未知写入 `UNCERTAIN <ids>`，确认断开写入 `RELEASED <ids>`。
- 新连接返回 `data.previousOwner {state, requests}`。`BUSY`、`UNCERTAIN` 或遗留 `ACTIVE` 增加 `PREVIOUS_OWNER_UNCLEAN`，`IDLE` 和 `RELEASED` 保持无此警告。
- 前持有者编号进入新会话的未确认清单，标记 `fromPreviousOwner=true`，保存继续提示 `UNSETTLED_CALLS`。

## 后果

- 活持有者阻止第二个写入者。死持有者的记录保留可核对证据，新会话可以显式连接。
- 原生调用被中断或结果未知后仍须检查目标，禁止自动重放。断开后可以在同一 MCP 会话调用 `ConnectPortal` 和 `AttachOpenProject`。八版原生行为仍待真机复验。
- 验证方式：租约测试与 P8-21b 的离线证据；真机复验并入最终验收（ADR 0013）。
