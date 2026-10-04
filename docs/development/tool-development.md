# 工具开发

[重构计划](refactor-plan.md) · [仓库结构](repository-layout.md) · [验证分层](validation.md) · [响应与异常设计](response-and-errors.md)

完整引擎的工具声明位于 `tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/Tools`。
新增工具时同时检查所属文件、类型化调用、版本策略和注册目录；示例、参数来源及结果解释统一维护在
`reference/tool-examples`，按验证分层执行受影响版本的构建、离线测试和契约快照。

## 运行时资源

工具使用随包指南、模板或伴随脚本时，复用 `tools/openness-shared/BundleLayout.cs` 的资源表与安装/开发锚点。
各调用方的显式覆盖和兼容回退并不相同；不要复制向上探测仓库的旧逻辑。
新增资源的清单、布局测试和仓库外验证步骤见[运行时布局](runtime-layout.md#新增运行时资源)。
`reference/v21-ecosystem.json` 由 V20/V21 引擎直接嵌入；修改该源文件后须重建，运行时不会从磁盘覆盖嵌入数据。

## MCP 文案语言

按[运行时布局与清理](runtime-layout.md#用户可见文案语言)的维护者决定（2026-10-03）：
阶段 0–5 冻结全部既有 MCP 可见文本，包括描述、参数说明、消息、异常、meta、中文与错别字；
重构不能顺便翻译或润色。**新增 MCP 可见文本一律英文；中文只作为数据出现**，例如 TIA 原文、
工程对象名与示例名称。阶段 6（4.0）统一为英文、增加稳定机器错误码，并提供旧→新对照表。

MCP 响应不引入本地化层，以保持快照和确定性；本地化仅用于 Studio 的 `Loc` 和 CLI doctor 输出。
`TryCanonicalizeEnumArgument` 解析的拒绝文本、快照标记、`Test-LocalStability` 使用的 stderr 和
FindTools 检索关键词具有功能意义，在阶段 6 引入错误码之前不得改动。

[Check-McpText.py](../../scripts/checks/Check-McpText.py)约束中文字面量只减不增；
[基线中的 allowlist](../../scripts/checks/mcp-text-baseline.json)逐条保存双语表、报告、TIA 数据及 CLI 输出的理由。
豁免是既有字面量及其出现次数，不是对文件的豁免；新增中文数据须单独审查并记录理由。
XML/报告构造器里的异常和消息仍受检查。只减不增门禁不授权修改已有响应字节，兼容快照仍须通过。

## 注释改写

按[清理设计](runtime-layout.md#注释与-leftovers)执行；本阶段先建立门禁，注释修改随后续领域任务进行：

- 删除产品版本号、维护者归属和旧 `Phase N` / `sub-batch` 叙事，把修复经过改写为当前必须保持的不变量。
- 原生行为证据保留，写明 TIA/PLCSIM 版本、日期和[真机验收清单](../reference/real-machine-ledger.md)链接。
- 删除迁移墓碑、注释掉的代码和过时工具数；改写时保留原注释的语言。
- 生成脚本字符串中的 `TODO`、注释及中文属于响应数据，不能当作 C# 注释清理。
- `*Leftovers*` 随阶段 3 的领域迁移逐步消解，不先单独改名；步骤 15 结束时文件数应为 0。

[Check-CommentHygiene.py](../../scripts/checks/Check-CommentHygiene.py)按工程和类别报告候选，
[注释基线](../../scripts/checks/comment-hygiene-baseline.json)按内容及重复次数守护，文件移动不会增加额度。
具体计数口径、自检及基线收缩命令见[验证分层](validation.md#注释与-mcp-中文门禁)。

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
