# 0007 每个 MCP 会话一个 worker

- 状态：已采纳
- 日期：2026-10-08
- 决策者：维护者
- 置信度：高
- 相关：[重构计划](../refactor-plan.md) P7-07a；[架构概览](../design-spec/architecture.md) 第 3 节；[运行时布局](../runtime-layout.md)；[4.0 发布说明草稿](../../releases/v4.0.0.md)；[V4 工具协议规范](../design-spec/v4-protocol.md) 第 0.4 节；[阶段 7 设计](../design/p7-host-worker-split.md)

回溯记录（写于 2026-10-11）。本决定修改了阶段 7 设计中的“每个宿主进程一个共享 worker”。

## 背景

阶段 7 设计原定每个宿主进程一个共享 worker。V20 真机第一轮验收后（发现 37–42，由 P7-07a 处理），需要决定 HTTP 宿主上多个同时存活的 MCP 会话如何使用 worker：共享 worker 时，一个会话的断开、故障或重启会影响同一宿主上的其他会话，导入暂存与导出句柄也无法按会话隔离。

## 选项

1. 每个宿主进程一个共享 worker：进程少；会话之间互相影响。
2. 每个 MCP 会话一个 worker：会话互相隔离；HTTP 的并发会话数受 worker 内存与 TIA 实例数限制。

## 决定

采用选项 2。stdio 是一个会话一个 worker，HTTP 是每个存活的会话一个 worker；会话的断开、故障、重启、导入暂存与导出句柄互不影响；会话结束时结束它的 worker；每个 worker 绑定宿主的 Windows job object，宿主退出或被终止时 worker 随之结束。八个版本相同。

## 后果

- 正面：会话是隔离单元，故障与恢复只影响本会话；这也是协议规范中“V4 会话”的基础。
- 负面与取舍：HTTP 宿主不设会话数上限，部署时须按 worker 内存和 TIA 实例数控制并发；导出句柄的进程内存储容量仍由全进程共用。
- 验证方式：HTTP 会话回归测试（`EngineHttpSessionTests`）；会话结束时 worker 退出与租约释放的测试。
