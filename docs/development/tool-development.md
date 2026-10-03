# 工具开发

[重构计划](refactor-plan.md) · [仓库结构](repository-layout.md) · [验证分层](validation.md) · [响应与异常设计](response-and-errors.md)

完整引擎的工具声明位于 `tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools`。
新增工具时同时检查所属文件、类型化调用、版本策略和注册目录；示例、参数来源及结果解释统一维护在
`reference/tool-examples`，按验证分层执行受影响版本的构建、离线测试和契约快照。

## 抛异常与返回失败

阶段 0–5 按工具所属的族决定错误行为，不按异常种类统一转换。**新代码沿用所在文件的族，直到阶段 6**；
也不借响应构造器重构改变已有工具的错误语义。以下规则来自[响应与异常设计](response-and-errors.md)。

| 族 | 行为 |
|---|---|
| F1 准入（包装层） | 返回 `isError:true` 纯文本，附 `preflight` |
| F2 手写 POCO 工具 | 成功返回 POCO + `{timestamp, success:true}`；执行失败抛 `McpException`；业务结果为假时返回 `success=false` |
| F3 执行器工具 | 业务失败不抛，返回 `meta.success=false` 与 `operationSuccess`、`status`、`error` |
| F4 桥接 | 从不抛，内层异常转成 `"X failed: …"` |
| F5 导出句柄 | 句柄缺失或拒绝覆盖时抛 `McpException(InvalidParams)`，成功时 `meta.ok=true` |
| F6 旧 Portal 方法 | 失败只在 Message 文本里，没有 meta |

响应兼容性包含属性顺序、null、转义、数值类型和时间戳的编码。SDK 默认选项、`BridgeJson`、
`DisciplineJson` 与无参数 `ToJsonString()` 的事实由
[HttpTests 黄金数据](../../tools/tiaportal-mcp/tests/TiaMcpServer.HttpTests/README.md)记录；
修改响应代码须比较规范化快照和格式 3 原始文本哈希。`meta.error` 按维护者决策 D1 只将首行
“类型: 消息”作为执行器黄金比较的契约，堆栈帧允许随迁移变化；这不授权改写实际返回内容。
