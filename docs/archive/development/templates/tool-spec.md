# 工具规格：`<ToolName>`

> 使用说明：新工具或 MCP 契约变化（工具名、参数、返回结构、错误语义）在实现前复制本文件为 `docs/tools/<tool-name>.md`（工具名改为小写连字符，例如 `export-plc-block.md`），填写后删除本段和各节中的提示文字。规则见 [MCP 工具设计规范](../../../standards/design.md)，流程见 [开发流程](../../../manuals/workflow.md) 第 3 节。现有工具的事实以生成目录为准，不为它们补写规格。

状态：草稿 | 已审查 | 已实现 · 最后核对：YYYY-MM-DD，master `<提交>`

- 任务：`<任务 ID>`
- 相关：ADR、设计文档、偏差编号

## 1 目的

一两句话说明工具让模型完成什么任务，以及为什么现有工具的动作或可选参数不够用。

## 2 身份

| 项目 | 值 |
|---|---|
| 工具名 | `<ToolName>`（动词在 `build-tools/release/CatalogData.cs` 的闭集中） |
| 描述前缀 | `[<层>][<域>][<操作>]` |
| 分类 | UI、SESSION、READ、WRITE、FILE、OFFLINE、ONLINE、ONLINE-WRITE、EXECUTE 之一，登记在 `ToolMetadata` 或 `[ToolClassification]` |
| 注解 | `readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint` 的值与理由 |
| 档位 | full；是否进入 V20/V21 的 lite 档 |
| 批处理准入 | `BatchRead`、`BatchWrite` 的值与理由 |
| 实现位置 | 声明、共享逻辑、按发布键的适配器 |

## 3 发布键与 API 证据

每个发布键写出支持的动作和官方 API 证据。证据来自 PublicAPI 元数据或官方文档，不按名称推断。至少有一个动作有证据的发布键才注册。

| 发布键 | 支持的动作 | API 证据（类型、成员、文档） | 不支持时的返回 |
|---|---|---|---|
| `14sp1` | 写动作或“不注册” | 写证据或“无” | 写错误码或“不适用” |
| `15.1` | 同上 | 同上 | 同上 |
| `16` | 同上 | 同上 | 同上 |
| `17` | 同上 | 同上 | 同上 |
| `18` | 同上 | 同上 | 同上 |
| `19` | 同上 | 同上 | 同上 |
| `20` | 同上 | 同上 | 同上 |
| `21` | 同上 | 同上 | 同上 |

## 4 输入

| 参数 | 类型与形式 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| `<parameterName>` | 例如字符串，绝对路径，文件 | 是或否 | 写默认值或“无” | 含义、来源（哪个工具返回该值） |

- 互斥参数与 `oneOf` 分组：
- 别名与弃用（如有）：
- 与现有工具同一概念的参数是否使用同一名称和形式：

## 5 输出

- `data` 的字段与含义：
- `meta` 中的特殊内容（警告、分页、审批信息）：
- 大响应是否寄存为导出句柄：
- 预览（`dryRun: true`）返回什么，与执行结果有何差别：

## 6 错误

| 情况 | `error.code` | `details` 关键字段 | `meta.outcome` |
|---|---|---|---|
| 参数无效 | `INVALID_ARGUMENT` | `parameter`、`parameters`、`allowedValues` | `rejected` |
| 本版不支持 | `UNSUPPORTED_CAPABILITY` | `releaseKey`、`capability`、`action` | `rejected` |
| 原生失败 | `NATIVE_OPERATION_FAILED` | `nativeCode`、`nativeMessage`、`evidence` | `failed` 或 `read-failed` |
| 写入中断 | `OUTCOME_UNKNOWN` | `stage`、`evidence` | `unknown` |

按需增删行；错误码和组合以 [V4 工具协议规范](../design-spec/v4-protocol.md) 第 4、5 节为准。

## 7 审批、审计与安全

- 哪些动作需要工作台审批，预览是否完全不写入：
- 计划哈希或预览令牌如何绑定执行：
- 写入审计的内容；带凭据的参数是否会被替换（[安全标准](../../../standards/security.md) SEC-06）：
- 读取的文件、目录或压缩包的路径与大小限制：
- 是否能回滚；失败后项目处于什么状态：

## 8 结果未知时的处理

写出哪些阶段可能结果未知、会话是否锁定、恢复步骤，以及为什么不会自动重放。

## 9 示例

- `reference/tool-examples` 中的记录与 `GetToolUsage` 示例编号：
- 每个动作一条示例；项目相关的值放在绑定和参数来源中：
- `validation` 的文字（验证范围，例如“离线预览，原生执行 NOT RUN”）：
- 真机核对的发布键、日期和台账条目（如有）：

## 10 测试与捕获

- 新增或修改的测试工程与套件（`tests/test-suites.json`）：
- 必须覆盖的失败路径（[测试策略](../../../standards/development.md) 第 5 节）：
- 需要运行的生成器：`generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`、`generate-release-migration`（按需）：
- 契约与响应基线的预期差异：

## 11 兼容性与发布

- 增量变化还是破坏性变化；破坏性变化是否需要升 `schemaVersion`：
- 发布说明与迁移表中的条目：
- 弃用的旧名称或参数及其保留期限：
