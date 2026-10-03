# HMI 连接与驱动选择

适用于 V20/V21 完整引擎。先用 `GetHmiProgramInfo` 确认目标是 WinCC Unified 还是 Classic，再读取相应工具的 `GetToolUsage`。V14 SP1–V19 的 MCP 基础目录没有本页 HMI 工具。

## Unified 连接

1. 从 `GetProjectTree` 取得实际 PLC 和 HMI 软件路径。
2. 读取 `GetToolUsage(toolName="EnsureUnifiedHmiConnection")` 并按当前参数创建或核对连接。
3. 检查返回的 `CommunicationDriver` 与 PLC 系列一致，并读回 Partner、Station、Node 等实际连接信息。
4. 用实际变量和地址完成[变量绑定](tag-binding.md)，再编译和核对工程。

| PLC 系列 | 对应驱动系列 |
|---|---|
| S7-1200 | SIMATIC S7 1200 |
| S7-1500、相应 ET 200SP CPU | SIMATIC S7 1500 |
| S7-300、S7-400 | SIMATIC S7 300/400 |
| 仿真目标 | 按被仿真的 CPU 系列选择 |

工具依据实际设备 `TypeIdentifier` 判断系列，不应根据用户自定义设备名猜测。若读回仍与硬件不符，保留诊断并检查 TIA 中的连接设置。IP、子网和伙伴路径均来自目标工程或现场明确配置，不照抄示例值。

## Classic 连接

Classic 与 Unified 使用不同对象模型，不能把 `EnsureUnifiedHmiConnection` 套到 KTP/TP Classic 设备。已有连接可按 `ExportHmiConnection` / `ImportHmiConnection` 的当前示例导出、导入。新建连接及特定设备的编辑能力需检查实际 API；不支持的步骤在 TIA 中完成后读回验证。

选用面板应符合项目需求；不要仅为迁就某条工具路径自动替换用户的设备型号。

## 结果检查

连接对象存在只证明工程组态已建立，不能证明 Runtime 已连通。变量无效时依次核对驱动、伙伴、接口、变量类型和地址。实时通道检查另见[在线读取](../online-monitoring.md)。
