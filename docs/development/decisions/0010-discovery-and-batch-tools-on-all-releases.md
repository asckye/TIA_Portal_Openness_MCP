# 0010 发现、桥接与批处理工具在全部八个版本发布

- 状态：已采纳
- 日期：2026-10-08
- 决策者：维护者（决定编号 U8）
- 置信度：高
- 相关：[阶段 8 迁移方案](../phase8-port-plan.md)“B1 迁移状态”；[4.0 源码版本范围](../../reference/version-tools.md)；[交接](../handoff.md)；[ADR 0009](0009-port-v20-v21-tools-to-foundation-adapters.md)、[0012](0012-old-release-tool-registration.md)

回溯记录（写于 2026-10-11）。

## 背景

F01 族（发现、桥接、预览与批处理工具，包括 `FindTools`、`CallTool`、`PreviewToolCall`、`ApplyToolBatch`）在 3.x 只存在于 V20/V21。阶段 8 把它们移入 net10 宿主后，需要决定旧版是否也发布这些工具。

## 选项

1. 只在 V20/V21 发布：旧版目录不变；客户端在不同版本上没有统一的发现与批处理入口。
2. 八个版本都发布：入口统一；桥接必须只到达本版已注册的工具。

## 决定

采用选项 2。F01 的工具在全部八个版本发布；桥接只到达本版已注册的工具；旧版保留完整目录，不启用 lite 档。

## 后果

- 正面：八个版本有同一套发现、桥接和批处理入口。
- 负面与取舍：旧版新增的工具行为为 `current / NOT RUN`，需要真机验收。`CallTool` 对本版未注册的工具返回 `UNSUPPORTED_CAPABILITY`，而直接调用同一工具返回 JSON-RPC 错误（MCP-11，有意偏差）。
- 验证方式：`PortedFamilies` 逐工具、逐版表；八版契约快照。
