# 0005 V20/V21 与其他六个版本采用同一架构：FoundationHost 加每版本 worker

- 状态：已采纳
- 日期：2026-10-08
- 决策者：维护者
- 置信度：高
- 相关：[重构计划](../refactor-plan.md) 阶段 7 与 P7-01、P7-04；[阶段 7 设计](../design/p7-host-worker-split.md)；[架构概览](../design-spec/architecture.md)；[ADR 0006](0006-engine-exe-as-v20-v21-worker.md)、[0007](0007-one-worker-per-mcp-session.md)、[0008](0008-worker-timeouts-and-faults.md)、[0009](0009-port-v20-v21-tools-to-foundation-adapters.md)

回溯记录（写于 2026-10-11）。

## 背景

到 2026-10-07，4.0 计划保留两个程序：V14 SP1–V19 用 FoundationHost（net10）加 PLC worker；V20/V21 用完整的 .NET Framework 引擎，同时承担 MCP 宿主和 Openness 调用。两类宿主共用一份行为规则，合并推迟到 4.x（P6-65 中的维护者决定）。两套宿主意味着审批、审计、会话与恢复规则要在两处实现，八版共有的工具在 V20/V21 上也是另一份代码和参数。

## 选项

1. 维持两个程序到 4.x：改动小；行为一致性只能靠测试保证，八版共有工具的用法不同。
2. V20/V21 也改用 FoundationHost，引擎降为 worker：宿主规则一处实现，八版共有工具同一份代码；改动面大，V20/V21 需要重新验收。

## 决定

采用选项 2。V20/V21 与 V14 SP1–V19 采用同一架构：FoundationHost（net10）加每个发布版本自己的 worker，保留全部 V20/V21 工具。八版共有的工具（V19 的 67 个）在 V20/V21 上使用同一份 Foundation 代码、同样的参数和用法（先 `ConnectPortal` 再 `AttachOpenProject`）；V20/V21 的 worker 中，Foundation 代码与引擎代码共用一个 MTA 所属线程和一个 TIA 连接。本决定取代 2026-10-07 的“4.0 保留两个程序”。

## 后果

- 正面：八个版本只有一个 MCP 宿主程序（`runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`），宿主规则只在一处实现。
- 负面与取舍：V20/V21 需要 net10 宿主与 net48 worker 两套依赖，引擎放在 `worker/` 子目录；V20/V21 在新宿主上的真机验收（P7-07）并入阶段 8 结束后的最终验收。
- 后续工作：引擎 worker 的形态见 ADR 0006；V20/V21 专有工具逐族移入 Foundation 适配器见 ADR 0009。
- 验证方式：经 FoundationHost 的 V20/V21 契约与 `baseline/20.json`、`baseline/21.json` 逐字节一致；响应快照差异逐项审查。
