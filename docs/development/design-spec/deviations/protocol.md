# V4 协议偏差清单

状态：现行 · 最后核对：2026-10-11，master `3a227657`

> 依据 master `3a227657` 的源码与 `manifest/contracts/v4` 快照，对照 MCP 规范与 Microsoft REST API Guidelines。协议规范见 [V4 工具协议规范](../v4-protocol.md)。
>
> 状态：按维护者 2026-10-11 的决定填写。默认采用各条建议；另有决定的条目（MCP-01、MCP-02、MCP-15、API-04）在该条的“状态”中注明；“4.0 前 - P8-60”的条目由任务 P8-60 实施。状态取值见 [设计规范索引](../README.md)。
>
> 基线优先级（维护者决定 2026-10-11）：先对照 MCP（现行修订 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)；SDK 0.3.0-preview.4 实际协商的最高版本是 [2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18)），再对照 Microsoft REST API Guidelines（[Azure Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md)、[Considerations](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md)、[归档版 Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md)）。
>
> 编号：`MCP-nn` 是相对 MCP 的差异；`API-01…17` 是相对 Microsoft 指南的差异；`API-18…39` 是 V4 自身的不一致（同一概念多种叫法、错误码用法不一、details 缺失等，以快照和源码实测为准）。
>
> 关于“外部影响”：4.0 已经决定硬切换，3.x 客户端无论如何都要按迁移表重建配置。所以在 4.0 之前修改，对 3.x 客户端**不构成额外破坏**，只影响 4.0 开发包（虚拟机测试包）、工作台、CLI、批次、生成编排器等内部消费者，以及契约快照和示例库。在 4.0 之后修改，则会破坏 4.0 客户端，除非是纯增量且已经约定了可扩展性（见 API-03）。

## 摘要

### 按状态分类的总数

| 状态 | MCP-nn | API-01…17（Microsoft） | API-18…39（内部一致性） | 合计 |
|---|---:|---:|---:|---:|
| 4.0 前 - P8-60 | 4 | 7 | 15 | **26** |
| 4.0 后 | 6 | 1 | 0 | **7** |
| 有意偏差（已接受） | 6 | 9 | 7 | **22** |
| 合计 | 16 | 17 | 22 | **55** |

4.0 前的 26 项中，API-08 和 API-15 只需要修改文档，API-36 主要是“保留还是删除”的决定。其余为代码或 schema 修改，多数集中在 `src/Logic/V4`、各个 `*ToolContract.cs` 映射器和工具参数定义；修改后需要按照 `docs/development/validation.md` 重新生成八版契约和响应快照。另外，有意偏差中有 6 项需要在 4.0 前把现状写成规范规则：API-11（4.x 演进政策）、API-26、API-32、API-39、MCP-07、MCP-11，这部分文档工作也由 P8-60 完成。MCP-01 要求的“V4 会话”定义已写入 [V4 工具协议规范](../v4-protocol.md) 第 0.4 节。

### 最重要的 10 项

| # | 编号 | 问题 | 建议与决定 |
|---|---|---|---|
| 1 | MCP-02 | 514 个工具中只有 8 个提供 MCP 工具注解，READ、WRITE、ONLINE-WRITE 等操作类别和“默认预览”只写在描述文本里，客户端无法据此做确认或自动放行 | 4.0 前由 `ToolMetadata` 生成注解并纳入契约快照，197 个推断分类人工核对（已决定，P8-60） |
| 2 | API-14 | 错误码被当作兜底：旧实现的失败统一报 `INVALID_ARGUMENT("arguments")`；参数格式错误报成 `NATIVE_OPERATION_FAILED`；未满足完成条件报 `INTERNAL_ERROR`。这直接误导模型的自我纠正 | 4.0 前修正映射 |
| 3 | MCP-01 | 现行 MCP（2026-07-28）是无状态协议，而 V4 的绑定、会话锁、租约、计划、令牌和句柄都以 MCP 传输会话为作用域，恢复步骤还要求“开新 MCP 会话” | 4.0 保持 MCP 2025-06-18；“V4 会话”已在规范第 0.4 节定义；4.x 随 SDK 升级设计显式的绑定句柄（已决定） |
| 4 | API-25 | `requiresSessionReset: true` 出现在所有 unknown 信封上，但只有发出过原生调用的写入才真正锁会话。这个字段的含义（建议还是强制）不明确 | 4.0 前明确含义，并让字段与实际行为一致 |
| 5 | API-22 + API-21 | 过期的计划有时报 `INVALID_ARGUMENT`，`PLAN_STALE.planHash` 中可能放的是批次令牌；计划哈希又有 `sha256:` 前缀和不带前缀两种格式 | 4.0 前统一 |
| 6 | MCP-03 + API-03 | 只有 87/514 个工具声明了 `outputSchema`，`data`、`error`、`warnings`、`paging` 都没有定型；meta 被设为 `additionalProperties:false`，错误码和枚举都是闭集，读取端严格，4.x 无法以增量方式演进 | 4.0 前给所有 V4 工具统一声明信封 schema，并写明可扩展性规则 |
| 7 | API-18 + API-19 | 预览绑定有五种参数名（`expectedPlanHash`、`expectedToken`、`token`、`expectedFingerprint`、`expectedInventoryHash`）；`expectedProject` 有时是工程名，有时是工程文件 | 4.0 前统一命名（4.0 是最后一次免费改名的机会） |
| 8 | API-11 | 没有成文的 4.x 演进和弃用政策；4.0 是一次不带 `api-version` 的硬切换 | 硬切换保留；4.0 前写明 4.x 只做增量、需要破坏时升 `schemaVersion` 等规则 |
| 9 | MCP-06 + MCP-05 | 30 分钟的 worker 调用、审批等待和 4 小时的生成运行都阻塞在一次 `tools/call` 里，进度通知只有少数批量工具发送 | 4.0 前加宿主级进度心跳；4.0 后评估 MCP tasks 扩展 |
| 10 | API-04 | 字段校验错误时，多个字段名被拼成一个字符串（`"a, b"`、`"offset/length"`），没有逐字段的错误列表，不利于模型自我纠正 | 4.0 前在 `INVALID_ARGUMENT` 中加上字段数组；`details.parameter` 必须填写，消息文本不变（已决定，P8-60） |

---

## 一览表

| 编号 | 类别 | 主题 | 建议 | 状态 |
|---|---|---|---|---|
| MCP-01 | MCP | 状态绑定在 MCP 传输会话上（现行规范为无状态） | 4.0 后 | 4.0 后 |
| MCP-02 | MCP | 工具注解缺失，与操作类别不一致 | 4.0 前 | 4.0 前 - P8-60 |
| MCP-03 | MCP | `outputSchema` 覆盖不全、未定型、封闭 | 4.0 前 | 4.0 前 - P8-60 |
| MCP-04 | MCP | `structuredContent` 内另加信封层 | 保留 | 有意偏差（已接受） |
| MCP-05 | MCP | 长操作同步阻塞，未使用 tasks 扩展 | 4.0 后 | 4.0 后 |
| MCP-06 | MCP | 进度通知覆盖面窄 | 4.0 前 | 4.0 前 - P8-60 |
| MCP-07 | MCP | 已取消的请求仍返回 `CANCELLED` 结果 | 保留 | 有意偏差（已接受）；规范补写 P8-60 |
| MCP-08 | MCP | 工具级分页与 MCP 游标约定的差别 | 保留 | 有意偏差（已接受） |
| MCP-09 | MCP | 大结果用带内导出句柄，未使用资源链接 | 4.0 后 | 4.0 后 |
| MCP-10 | MCP | CallTool 等路径对工具名不区分大小写 | 4.0 前 | 4.0 前 - P8-60 |
| MCP-11 | MCP | “工具不可用”有三种表达方式 | 保留 | 有意偏差（已接受）；规范补写 P8-60 |
| MCP-12 | MCP | 不使用 `_meta`（serverInfo、traceparent），requestId 只由服务器生成 | 4.0 后 | 4.0 后 |
| MCP-13 | MCP | 带外的工作台审批，而不是 MCP 的确认或 elicitation | 保留 | 有意偏差（已接受） |
| MCP-14 | MCP | 没有 `title`/`icons`，分类标签写在描述中 | 4.0 后 | 4.0 后 |
| MCP-15 | MCP | SDK 只支持到 2025-06-18 | 4.0 后 | 4.0 后 |
| MCP-16 | MCP | 没有显式的调用限流 | 保留 | 有意偏差（已接受） |
| API-01 | MS | 错误对象结构（没有 target、details[]、innererror） | 保留 | 有意偏差（已接受） |
| API-02 | MS | 错误码大小写风格（UPPER_SNAKE） | 保留 | 有意偏差（已接受） |
| API-03 | MS | 闭集枚举、严格读取端、封闭的 meta schema | 4.0 前 | 4.0 前 - P8-60 |
| API-04 | MS | 多字段校验错误的表达 | 4.0 前 | 4.0 前 - P8-60 |
| API-05 | MS | 始终输出 null | 保留 | 有意偏差（已接受） |
| API-06 | MS | 集合与分页的形状 | 保留 | 有意偏差（已接受） |
| API-07 | MS | 分页参数名与默认值不统一 | 4.0 前 | 4.0 前 - P8-60 |
| API-08 | MS | offset 分页缺少一致性说明 | 4.0 前（文档） | 4.0 前 - P8-60 |
| API-09 | MS | 没有可重复请求的标识 | 保留 | 有意偏差（已接受） |
| API-10 | MS | 条件写用 `expected*` 参数，读取不返回 ETag | 保留 | 有意偏差（已接受） |
| API-11 | MS | 版本策略：进程级版本、硬切换、没有演进政策 | 保留 + 文档 | 有意偏差（已接受）；规范补写 P8-60 |
| API-12 | MS | 没有弃用信号 | 4.0 后 | 4.0 后 |
| API-13 | MS | 时间戳精度与 data 中的时间格式 | 4.0 前 | 4.0 前 - P8-60 |
| API-14 | MS | 错误码粒度不足与误用 | 4.0 前 | 4.0 前 - P8-60 |
| API-15 | MS | 字符偏移单位未声明 | 4.0 前（文档） | 4.0 前 - P8-60 |
| API-16 | MS | `Manage*` 多路复用工具 | 保留 | 有意偏差（已接受） |
| API-17 | MS | 响应体中的警告 | 保留 | 有意偏差（已接受） |
| API-18 | 内部 | 预览绑定参数五种叫法 | 4.0 前 | 4.0 前 - P8-60 |
| API-19 | 内部 | `expectedProject` 的语义不一 | 4.0 前 | 4.0 前 - P8-60 |
| API-20 | 内部 | 20 种确认开关（19 个 `confirm*` 加 `allowWrite`） | 保留 | 有意偏差（已接受） |
| API-21 | 内部 | 计划哈希的两种格式 | 4.0 前 | 4.0 前 - P8-60 |
| API-22 | 内部 | 计划过期时错误码不一，PLAN_STALE 中放令牌 | 4.0 前 | 4.0 前 - P8-60 |
| API-23 | 内部 | details 中字符串词表混乱 | 4.0 前 | 4.0 前 - P8-60 |
| API-24 | 内部 | 省略 null 的例外；COMPILE_ERRORS 绕过校验 | 4.0 前 | 4.0 前 - P8-60 |
| API-25 | 内部 | `requiresSessionReset` 与实际锁定不一致 | 4.0 前 | 4.0 前 - P8-60 |
| API-26 | 内部 | TIMEOUT 不用于只读，worker 超时一律为 unknown | 保留（文档） | 有意偏差（已接受）；规范补写 P8-60 |
| API-27 | 内部 | 取消的映射不一致 | 4.0 前 | 4.0 前 - P8-60 |
| API-28 | 内部 | 序列化后修改 JSON，绕过校验 | 4.0 前 | 4.0 前 - P8-60 |
| API-29 | 内部 | data 中残留旧的判定字段 | 保留 | 有意偏差（已接受） |
| API-30 | 内部 | 导出句柄的表示不一致 | 4.0 前 | 4.0 前 - P8-60 |
| API-31 | 内部 | 分页单位混用；页级完整度含义不一 | 4.0 前 | 4.0 前 - P8-60 |
| API-32 | 内部 | CallTool 派发前错误的 `meta.tool` 与旧规范不符 | 保留（文档） | 有意偏差（已接受）；规范补写 P8-60 |
| API-33 | 内部 | CLI 退出码的覆盖规则未写入规范 | 4.0 前 | 4.0 前 - P8-60 |
| API-34 | 内部 | 枚举值大小写多套并存 | 保留 | 有意偏差（已接受） |
| API-35 | 内部 | `""` 缺省哨兵 | 保留 | 有意偏差（已接受） |
| API-36 | 内部 | 已定义但未使用的类型和错误码 | 4.0 前 | 4.0 前 - P8-60 |
| API-37 | 内部 | 批次聚合的错误码 | 4.0 前 | 4.0 前 - P8-60 |
| API-38 | 内部 | 命名检查只覆盖改名 | 4.0 前 | 4.0 前 - P8-60 |
| API-39 | 内部 | 规范承诺的 `data.plan` 与实际发布的不符 | 保留（文档） | 有意偏差（已接受）；规范补写 P8-60 |

---

## A. 相对 MCP 的差异（首要基线）

### MCP-01　状态绑定在 MCP 传输会话上

- **基线**：MCP 2026-07-28 [Basic §Statelessness](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#statelessness)：跨请求的状态 MUST 通过客户端每次传入的显式标识引用；服务器 MUST NOT 依赖先前的请求，SHOULD NOT 要求客户端复用同一连接，不得把连接或进程身份当作会话。另见 [Tools §Stateful Tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#stateful-tools) 和 [Changelog 第 1、2 项](https://modelcontextprotocol.io/specification/2026-07-28/changelog)（去掉了 `Mcp-Session-Id`）。
- **现状**：
  - 每个 MCP 会话独占一个 worker，HTTP 宿主按 MCP 会话创建 `HostSession`（`FoundationHost/Program.cs` 中的 `ConfigureSessionOptions`）。
  - `ConnectPortal` 和 `AttachOpenProject` 的绑定是隐含的会话状态；会话锁（`SessionFaults`）以会话为键。
  - 恢复步骤要求“Start a new MCP session”（`SessionBehavior.Recovery`）。
  - `planId` 只在“this MCP session”内有效；导出句柄“in this engine session”；批次令牌、Unified 游标和 TIA 租约的所有者都绑定会话。
  - 按 SDK 实际协商的 2025-06-18 修订，这种设计是合规的；按现行修订则不合规。
- **外部影响**：改为显式句柄会改变几乎所有会话类工具的契约（新增绑定句柄参数，或用 `bindingId` 代替隐式会话），对 4.0 客户端是破坏性变更。不改的话，等客户端升级到 2026-07-28 后，stdio 进程不再等于会话，HTTP 也没有会话头，现有的租约和锁定语义会失效。
- **成本与风险**：高。牵涉宿主、worker、租约、审批、审计和生成编排器，需要真机验收。4.0 之前做风险过大；现有 SDK 也不支持 2026-07-28。
- **建议：4.0 后修改。** 4.0 前在规范中把“V4 会话”定义为由宿主管理的绑定（当前由传输会话承载），并写明所有句柄的作用域与有效期（文档工作，计入 API-11）。4.0 后结合 MCP-15 的 SDK 升级，引入服务器签发的绑定句柄（例如 `ConnectPortal` 返回 `bindingId`，后续调用携带它），让租约和锁定以绑定为键。
- **状态**：4.0 后。维护者 2026-10-11 决定：4.0 保持 MCP 2025-06-18；“V4 会话”的定义、所含状态及其作用域已写入 [V4 工具协议规范](../v4-protocol.md) 第 0.4 节；显式绑定句柄在 4.x 迁移到无状态修订时设计（与 MCP-15 一起）。

### MCP-02　工具注解缺失，与 V4 操作类别不一致

- **基线**：[ToolAnnotations](https://modelcontextprotocol.io/specification/2026-07-28/schema#toolannotations)：`readOnlyHint`（默认 false）、`destructiveHint`（默认 true，仅在 `readOnlyHint` 为 false 时有意义）、`idempotentHint`（默认 false）、`openWorldHint`（默认 true）。这些只是提示，客户端只应信任可信服务器提供的注解。2025-03-26 起已有此字段，SDK 当前协商的版本也支持。
- **现状**：
  - 源码中只有 `WorkbenchControlTools.cs:45` 为 8 个工作台控制工具设置了注解（`ReadOnlyHint = read, DestructiveHint = false, OpenWorldHint = false`）。其余工具的 `[McpServerTool]` 没有设置 ReadOnly/Destructive/Idempotent/OpenWorld，按 SDK 默认值，相当于“可能有破坏性写入、非幂等、开放世界”，连 158 个 READ 工具也是如此。
  - 操作类别（UI 6、SESSION 16、READ 158、WRITE 198、FILE 53、OFFLINE 35、ONLINE 24、ONLINE-WRITE 11、EXECUTE 13；其中 197 个由工具名推断）只以 `[L1][Domain][READ]` 前缀写在描述里。
  - 202 个工具默认 `dryRun=true`，这一点无法通过注解表达。
  - 契约基线只记录 `name`、`inputSchema`、`outputSchema`、`descriptionSha256`，注解的变化不在快照门禁范围内。
  - 需要在构建产物的 `tools/list` 上复核：SDK 是否会在未设置时输出空的注解对象。
- **外部影响**：只改变 `tools/list` 中的元数据，不改变信封，不破坏调用。但客户端（例如按 `readOnlyHint` 自动放行的客户端）的行为会随之改变，所以注解必须准确。
- **成本与风险**：中低。可以从 `ToolMetadata`/`ToolTaxonomy` 自动生成，建议映射如下：
  - READ、OFFLINE、ONLINE → `readOnlyHint: true`。
  - WRITE、ONLINE-WRITE → `readOnlyHint: false`；有删除或覆盖动作的工具 `destructiveHint: true`。
  - `Ensure*` → `idempotentHint: true`。
  - ONLINE、ONLINE-WRITE → `openWorldHint: true`，其余为 false。
  - `Manage*` 中混合读写的工具保持 `readOnlyHint: false`。
  
  主要风险是 197 个推断出来的分类需要人工核对。
- **建议：4.0 前修改。** 理由：4.0 是所有客户端刷新工具缓存的时间点，分类数据已经存在。同时把注解纳入契约快照，并增加一致性检查：注解必须与描述前缀的操作类别一致；有 `dryRun` 参数的工具 `readOnlyHint` 必须为 false。
- **状态**：4.0 前 - P8-60。维护者 2026-10-11 决定：由 `ToolMetadata` 生成工具注解并纳入契约快照；197 个按工具名推断的分类在用于注解前逐个人工核对。

### MCP-03　`outputSchema` 覆盖不全、未定型、封闭

- **基线**：[Tools §Output Schema](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#output-schema)：提供 outputSchema 时，服务器 MUST 返回符合它的 structuredContent，客户端 SHOULD 校验。2026-07-28 允许 2020-12 的任意关键字。
- **现状**：
  - 八版中声明了 `outputSchema` 的工具分别是 78/79/81/81/81/83/87/87 个（V21 为 87/514），只有 Foundation 共享工具和宿主工具声明。V20/V21 的 427 个引擎工具返回同样的信封，却没有 schema。
  - 657 份 outputSchema 内容完全相同（`FoundationV4Result.Schema`），其中 `data` 为 `{"type":["object","null"]}`，`error` 为任意对象，`warnings.items` 为任意对象，`paging` 为任意对象，信封和 meta 都设为 `additionalProperties:false`。
- **外部影响**：给所有工具补上同一份信封 schema，以及 error、warning、paging 子 schema，只是新增元数据，不改变结果。放宽 meta 的 `additionalProperties`，对客户端来说是放宽，不构成破坏。
- **成本与风险**：低。只需在注册层统一挂接，再重新生成契约基线。为每个工具定型 `data` 的成本高，留到 4.0 之后逐步做。
- **建议：4.0 前修改。** 给所有 V4 工具声明信封 schema，补上 `error` 的码别联合、`warning` 和 `paging` 的结构，并按 API-03 决定是否允许 meta 增量扩展。逐工具的 data schema 在 4.0 后推进。
- **状态**：4.0 前 - P8-60

### MCP-04　`structuredContent` 内另加信封层

- **基线**：MCP 的 `CallToolResult` 只规定 `content`、`structuredContent`、`isError` 和 `_meta`（[schema §CallToolResult](https://modelcontextprotocol.io/specification/2026-07-28/schema#calltoolresult)）。
- **现状**：`structuredContent` 是 `{schemaVersion, ok, data, error, meta}`；`ok` 与 `isError` 重复，`meta.requestId` 与 JSON-RPC `id` 重复；`outcome`/`execution`/`completeness` 等语义在 MCP 中没有对应。
- **外部影响**：去掉这一层会推翻整个 V4 契约。
- **成本与风险**：去掉的成本极高。保留的风险是客户端只看 `isError`，可能丢弃 `partial` 或 `unknown` 结果中的证据。
- **建议：保留为有意偏差。** 理由：同一形状要同时服务 MCP、CLI、批次子结果、worker 通道和审计，而且“结果未知、禁止重放”的语义 MCP 无法表达。规范中要写明 `isError = !ok`，并要求消费者在 `isError` 时也读取 `structuredContent`（v4-protocol.md 第 4.7 节）。
- **状态**：有意偏差（已接受）

### MCP-05　长操作同步阻塞，未使用 tasks 扩展

- **基线**：[Tasks 扩展](https://modelcontextprotocol.io/extensions/tasks/overview)（2026-07-28 起为官方扩展 `io.modelcontextprotocol/tasks`）：服务器可以返回持久的 `taskId`，客户端用 `tasks/get` 轮询（状态为 working、input_required、completed、failed、cancelled），用 `tasks/update` 补充输入，用 `tasks/cancel` 取消；人工审批和批量处理都被列为适用场景。次要基线：[Azure §Long-Running Operations & Jobs](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#long-running-operations--jobs)、[§The Status Monitor Resource](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#the-status-monitor-resource)（状态为 NotStarted/Running/Succeeded/Failed/Canceled，配合 `retry-after`，状态至少保留 24 小时）。
- **现状**：
  - worker 调用最长 30 分钟（编译、上下载、导入导出、归档）。
  - 审批等待在 `tools/call` 内阻塞到工作台作出决定或超时。
  - `ApplyGenerationPlan` 在一次调用中执行完整个运行，上限 4 小时或 10000 步。
  - 只有生成运行有状态监视：`runId` 加 `GetGenerationRun`，状态词表为 succeeded/failed/unknown/cancelled/diverged，记录持久保存。
- **外部影响**：引入 tasks 后，客户端需要处理多态结果（`resultType: "task"`），4.0 客户端如果没有声明这项能力，服务器必须继续同步返回，因此可以兼容地增加。
- **成本与风险**：高。SDK 不支持（MCP-15），客户端的支持程度也不一；而且“结果未知”与任务状态如何对应需要设计。
- **建议：4.0 后修改。** 4.0 维持同步。4.0 后先为 `ApplyGenerationPlan`、编译和上下载提供 tasks 方式，`GetGenerationRun` 的状态词表与 tasks 的状态对齐。在此之前，由 MCP-06 的进度心跳缓解客户端超时。
- **状态**：4.0 后

### MCP-06　进度通知覆盖面窄

- **基线**：[Progress](https://modelcontextprotocol.io/specification/2026-07-28/basic/utilities/progress)：请求带 `progressToken` 时服务器 MAY 发送 `notifications/progress`，progress MUST 单调递增，完成后 MUST 停止发送。[Cancellation §Timeouts](https://modelcontextprotocol.io/specification/2026-07-28/basic/utilities/cancellation#timeouts) 允许客户端在收到进度时重置超时。
- **现状**：只有 `PlcBlocksTools`、`TypesTools`、`DocumentsTools` 的批量导出导入会发送进度；V20/V21 的宿主管线会转发 worker 发来的进度（`EngineHostPipeline.cs:267`）。编译、上下载、审批等待和生成运行都不发心跳，客户端的超时可能先于 30 分钟的 worker 预算触发。
- **外部影响**：只新增通知，不改变结果，不破坏任何客户端。
- **成本与风险**：中低。宿主层可以统一实现：调用带 `progressToken` 且超过 N 秒未完成时，周期性发送 `progress` 递增、`message` 为“waiting for Workbench approval”或“worker busy: <tool>”的通知。风险是通知过于频繁，需要限流。
- **建议：4.0 前修改。** 理由：低成本即可减少长操作被客户端超时取消的情况；而 V4 在派发后不响应取消，客户端放弃等待后，用户看不到结果（只能事后从审计和调用日志中找）。
- **状态**：4.0 前 - P8-60

### MCP-07　已取消的请求仍返回 `CANCELLED` 结果

- **基线**：[Cancellation §Behavior Requirements](https://modelcontextprotocol.io/specification/2026-07-28/basic/utilities/cancellation#behavior-requirements)：服务器 SHOULD 停止处理，并且 SHOULD NOT 为已取消的请求发送响应；无法取消时 MAY 忽略；客户端 SHOULD 忽略迟到的响应。
- **现状**：派发前取消时返回 `CANCELLED`（stage `tool-queue`，outcome rejected-before-operation）；审批预检期间取消时返回 `CANCELLED`（`approval-precheck`）；派发后宿主忽略取消，等待真实结果再返回。
- **外部影响**：MCP 客户端会忽略这个响应，没有实际危害。
- **成本与风险**：不改的成本为零。如果不再返回结果，CLI、批次和审计就丢失了“未执行”的记录。
- **建议：保留为有意偏差。** 规范中写明：取消后的 `CANCELLED` 结果只供批次、CLI 和审计使用，MCP 客户端可以忽略；派发后不响应取消，是因为 Openness 调用无法中途安全停止。取消映射的内部问题另见 API-27。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

### MCP-08　工具级分页与 MCP 游标约定的差别

- **基线**：[Pagination](https://modelcontextprotocol.io/specification/2026-07-28/server/utilities/pagination)：游标不透明，页大小由服务器决定，缺少 `nextCursor` 表示结束，空串也是有效游标，无效游标返回 -32602。该规定只适用于列表方法。
- **现状**：
  - offset 模式让客户端指定 `limit`，暴露偏移量运算，最后一页 `nextOffset: null` 而不是省略。
  - cursor 模式（9 个 Unified 工具）的语义与 MCP 一致，但另需 `ReleaseUnifiedReadCursor` 显式释放。
  - 无效的 offset 返回工具错误 `INVALID_ARGUMENT`，这对工具级分页是正确的。
  - `tools/list` 不分页，一次返回最多 514 个工具，这是允许的。
- **外部影响**：改为纯游标会破坏所有 offset 工具。
- **成本与风险**：高，收益小。offset 对模型更直观。
- **建议：保留为有意偏差。** 规范中写明 MCP 的分页规则不适用于工具结果；cursor 模式遵循 MCP 语义；服务器可以返回少于 `limit` 的条目。参数命名的统一见 API-07。
- **状态**：有意偏差（已接受）

### MCP-09　大结果用带内导出句柄，未使用资源链接

- **基线**：[Tools §Resource Links / §Embedded Resources](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#resource-links)：工具可以返回 `resource_link`，由客户端按 URI 读取。
- **现状**：超过 20000 个字符的成功结果被换成 `data{content, export}`，后续用 `GetExportContent` 按字符分页读取，同时 `meta.paging` 改为字符页。结果的形状随大小而变，客户端必须检查 `data.export`。V4 不声明 `resources` 能力。
- **外部影响**：改用资源链接需要客户端支持 `resources/read`，对 4.0 客户端是增量；如果取消句柄则是破坏性变更。
- **成本与风险**：中。需要实现 resources 能力并确定作用域和有效期，还牵涉 MCP-01 的无状态问题。
- **建议：4.0 后修改。** 4.0 保留句柄，并修正 API-30、API-31。4.0 后评估在信封之外同时返回一个 `resource_link` 内容块，让支持资源的客户端走原生路径，句柄继续保留以兼容。
- **状态**：4.0 后

### MCP-10　CallTool 等路径对工具名不区分大小写

- **基线**：[Tools §Tool Names](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#tool-names)：工具名 SHOULD 区分大小写。
- **现状**：直接调用区分大小写（由 SDK 处理）。CallTool 的目录、PreviewToolCall 和大响应寄存层使用 `StringComparer.OrdinalIgnoreCase`（`McpServer.Exports.cs` 的注释举例 `CallTool(name="getexportcontent")` 会成功派发到 `GetExportContent`），`meta.tool` 则规范成正确的拼写。
- **外部影响**：改为精确匹配后，依赖错误大小写的客户端会收到 `TOOL_NOT_FOUND`。这类客户端本来就不符合 MCP。
- **成本与风险**：低。把字典比较器改为 Ordinal，大小写不同的名称放进错误消息作为提示。
- **建议：4.0 前修改。** 理由：同一个名称在直接调用和桥接调用中的行为应当一致；4.0 前修改不额外破坏 3.x 客户端。
- **状态**：4.0 前 - P8-60

### MCP-11　“工具不可用”有三种表达方式

- **基线**：[Tools §Error Handling](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#error-handling)：未知工具是协议错误（-32602）。
- **现状**：在 V20 上直接调用 V21 专有工具，返回 JSON-RPC `-32602 Unknown tool`（快照 11 次）。通过 CallTool 调用同一工具，返回 `UNSUPPORTED_CAPABILITY`（`capability` 为工具名，`action` 为 null）；完全未知的名称返回 `TOOL_NOT_FOUND`。V14 SP1–V19 中缺少某个 action，返回 `UNSUPPORTED_CAPABILITY`。
- **外部影响**：无，各条路径分别符合各自的层次。
- **成本与风险**：不改的成本为零。
- **建议：保留为有意偏差。** 写入规范：直接调用遵循 MCP；CallTool 是应用层代理，用工具错误表达“目录中存在、但本版本不可用”。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

### MCP-12　不使用 `_meta`；requestId 只由服务器生成

- **基线**：[Basic §_meta](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#meta)：结果的 `_meta` SHOULD 带 `io.modelcontextprotocol/serverInfo`；`traceparent`、`tracestate`、`baggage` 保留给 W3C 追踪上下文使用。次要基线：[Azure §Distributed Tracing & Telemetry](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#distributed-tracing--telemetry)。
- **现状**：
  - 结果不带 `_meta`。
  - `meta.requestId` 来自审计调用 ID 或本地 GUID，不读取客户端的 `traceparent`，也不读取 `clientInfo`。
  - `ApplyToolBatch` 为每个子项生成新 ID，与父结果的 ID 没有关联字段。
- **外部影响**：只是新增，不破坏任何客户端。
- **成本与风险**：中。需要 SDK 支持（MCP-15），并改造审计和调用日志的关联方式。
- **建议：4.0 后修改。** 与 SDK 升级一起做：接受 `traceparent` 并写入审计；在结果 `_meta` 中给出 serverInfo；批次子项记录 `parentRequestId`。
- **状态**：4.0 后

### MCP-13　带外的工作台审批，而不是 MCP 的确认或 elicitation

- **基线**：[Tools §User Interaction Model](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#user-interaction-model)（SHOULD 有人在环中）、[MRTR](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr)（服务器返回 `input_required`，客户端补充输入后重试）。
- **现状**：写调用在服务器侧通过本机命名管道等待工作台审批。工作台没有运行时返回 `CONFIRMATION_REQUIRED/workbench-unavailable`。没有使用 MCP 的 elicitation。
- **外部影响**：无，这是 V4 自身的安全层。
- **成本与风险**：改为 elicitation 等于把批准权交给 AI 客户端的界面，安全性会降低。
- **建议：保留为有意偏差。** 理由：审批必须发生在本机可信的界面上，并且与哈希链审计绑定，AI 客户端不能替用户批准。4.0 后可以考虑在审批等待期间用 MRTR 或 tasks 的 `input_required` 只做“请到工作台审批”的提示，不代替工作台审批。
- **状态**：有意偏差（已接受）

### MCP-14　没有 `title`/`icons`；分类标签写在描述中

- **基线**：[Tools §Data Types / Tool](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#tool)：`title` 和 `icons` 是可选字段，用于界面显示。
- **现状**：所有工具的描述都以 `[L1][Domain][OP]` 这样的机器标签开头，没有设置 `title`。
- **外部影响**：只是新增，不破坏任何客户端。
- **成本与风险**：低到中。描述标签被 FindTools 检索和文档生成器使用，不能直接删除。
- **建议：4.0 后修改。** 先完成 MCP-02 的注解，再考虑补上 `title`；描述前缀保留到检索逻辑不再依赖它为止。
- **状态**：4.0 后

### MCP-15　SDK 只支持到 2025-06-18

- **基线**：现行修订 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/changelog) 要求每个结果带 `resultType`，`tools/list` 带 `ttlMs` 和 `cacheScope`；新增 `server/discover` 和每请求的 `_meta`；Logging 被弃用；tasks 改为扩展。
- **现状**：`ModelContextProtocol 0.3.0-preview.4` 的程序集中，协议版本字符串只有 `2025-03-26` 和 `2025-06-18`。V4 的日志写 stderr 和文件，不使用 `notifications/message`，这一点已经符合新的弃用方向。
- **外部影响**：升级 SDK 后，传输层细节会变化；信封不变。
- **成本与风险**：中到高。preview 版 SDK 的 API 有变动，Foundation 与引擎两类宿主都要回归测试。
- **建议：4.0 后修改。** 列入 4.x 路线图，并与 MCP-01、MCP-05、MCP-12 一起规划。4.0 的发布说明中写明所支持的 MCP 修订。
- **状态**：4.0 后。维护者 2026-10-11 决定：4.0 保持 2025-06-18；在 .NET SDK 支持 2026-07-28 后于 4.x 迁移，4.0 发布说明写明支持的修订。

### MCP-16　没有显式的调用限流

- **基线**：[Tools §Security Considerations](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#security-considerations)：服务器 MUST 对工具调用限流。
- **现状**：每个会话只有一条 Openness 独占通道；有请求帧、批次条数、导出存储容量等上限；HTTP 有 Bearer 认证；不使用 TIA 的工具可以并发执行，没有限流。
- **外部影响**：无。
- **成本与风险**：本地单用户场景中，实际风险低。
- **建议：保留为有意偏差。** 规范中写明用通道串行化和上限代替限流；如果 HTTP 宿主将来对多用户开放，再增加限流。
- **状态**：有意偏差（已接受）

---

## B. 相对 Microsoft REST API Guidelines 的差异（次要基线）

### API-01　错误对象结构

- **基线**：[Azure §Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors)、[7.10.2 Error condition responses](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md#7102-error-condition-responses)：`error{code, message, target?, details?: ErrorDetail[], innererror?{code, innererror?}}`。
- **现状**：`error{code, message, details}`，其中 `details` 是按错误码定型的单个对象。例：`{"code":"INVALID_ARGUMENT","details":{"allowedValues":[],"parameter":"arguments"},"message":"Input does not satisfy the declared contract."}`。`parameter`/`target` 放在 details 里，相当于 target；没有 innererror 链。
- **外部影响**：改成 Microsoft 的结构会改变所有错误，对 4.0 客户端是全面破坏。
- **成本与风险**：高，收益小。定型的 details 对模型更友好，也能被校验。
- **建议：保留为有意偏差。** 在规范中写明对应关系：details ≈ innererror 加 target，错误码 ≈ 顶层 code。
- **状态**：有意偏差（已接受）

### API-02　错误码大小写风格

- **基线**：Microsoft 指南的示例使用 PascalCase（如 `MissingApiVersionParameter`），但没有强制的大小写规则。
- **现状**：UPPER_SNAKE（`OUTCOME_UNKNOWN`、`PLAN_STALE`）。
- **外部影响**：改名对所有错误处理代码都是破坏性变更。
- **成本与风险**：高。
- **建议：保留为有意偏差。** 理由：风格在全协议内一致，与 gRPC 的风格相近，模型容易识别；指南本身也没有强制要求。
- **状态**：有意偏差（已接受）

### API-03　闭集枚举、严格读取端、封闭的 meta schema

- **基线**：[Azure §Use Extensible Enums](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#use-extensible-enums)、[§Enums & SDKs](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#enums--sdks-client-libraries)、[Considerations §Design for Change Resiliency](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#design-for-change-resiliency)：可扩展枚举用字符串表示，新增值不构成破坏；[7.10.2](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md#7102-error-condition-responses)：新错误码放进 innererror，避免破坏客户端。
- **现状**：
  - `ErrorCode` 和 `WarningCode` 是闭集，`V4Json` 读取时拒绝未知拼写和未映射成员。
  - outputSchema 对信封和 meta 设置了 `additionalProperties:false`。
  - phase6 中的“初始闭集”在发布前就新增了 `COMPILE_ERRORS` 和 4 个警告码，说明闭集确实会变化。
- **外部影响**：4.0 之后新增任何错误码、警告码或 meta 字段，都会破坏用 `V4Json` 读信封或按 schema 校验的客户端，包括工作台、CLI、批次和生成编排器。
- **成本与风险**：低。只需修改文档和 schema，生产者仍保持闭集校验，消费者按开放集处理。
- **建议：4.0 前修改。** 规范中写明：消费者 MUST 容忍未知的错误码（按 outcome 处理）、未知的警告码（忽略）和未知字段；去掉 meta 的 `additionalProperties:false`，或者明确“新增 meta 字段需要升 schemaVersion”。`V4Json` 增加一个宽松读取模式，供跨版本的消费者使用。
- **状态**：4.0 前 - P8-60

### API-04　多字段校验错误的表达

- **基线**：[Azure §Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors)：`details` 是 ErrorDetail 数组，每项可以带自己的 `target`。MCP 的 [Tools §Error Handling](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#error-handling) 要求工具错误提供可执行的反馈。
- **现状**：
  - `TargetInputError` 把多个字段名拼成 `"Missing or invalid target field(s): a, b."`，details 中 `parameter` 为 `"a, b"`。
  - `GetExportContent` 报 `InvalidInput("offset/length")`。
  - 大小写重复或整体无效时，parameter 为 `"arguments"`（快照中 4003/4013 次）。
  - 只有 `PreviewToolCall` 会在 `data.validationErrors` 中给出逐字段的列表。
- **外部影响**：在 `InvalidArgumentDetails` 中新增 `parameters: string[]`（始终输出），是 details 的增量字段。按 API-03 的严格读取端，4.0 之后再加就是破坏性变更，所以应在 4.0 前完成。
- **成本与风险**：中低。共 45 个构造点，主要在共享准入层；需要重新生成快照。
- **建议：4.0 前修改。** `parameter` 只放一个字段（第一个出错的字段或 JSON 路径），新增 `parameters` 数组列出全部，**不再**拼接字符串。
- **状态**：4.0 前 - P8-60。维护者 2026-10-11 决定：V4 错误填写 `details.parameter`，`message` 文本保持不变（C# 侧见 CS-39，由 P8-62 提供辅助方法）。

### API-05　始终输出 null

- **基线**：[Azure §JSON](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#json)：响应中不要输出 null 字段，省略与 null 等价。
- **现状**：信封、meta、paging 和 details 的字段都显式输出 null（例如 `"paging": null`、`"target": null`）。
- **外部影响**：改为省略会破坏按 required 校验的客户端。
- **成本与风险**：中，收益小。
- **建议：保留为有意偏差。** 理由：固定的形状便于模型和 schema 处理，MCP outputSchema 中可以用 required 表达。两个已经在省略 null 的例外按 API-24 修正。
- **状态**：有意偏差（已接受）

### API-06　集合与分页的形状

- **基线**：[Azure §Collections](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#collections)、[Considerations §Pagination](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#pagination)：数组放在 `value`，`nextLink` 不透明，最后一页省略而不是 null。
- **现状**：数组放在 `data.items`；分页信息在 `meta.paging`；最后一页的 `nextOffset` 或 `nextCursor` 为 null；offset 是明文数字。
- **外部影响**：改动会破坏全部分页工具。
- **成本与风险**：高。MCP 没有 URL，`nextLink` 不适用。
- **建议：保留为有意偏差。** 写明对应关系：`nextOffset`/`nextCursor` ≈ nextLink，`items` ≈ value。
- **状态**：有意偏差（已接受）

### API-07　分页参数名与默认值不统一

- **基线**：[Azure §Query options](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#query-options)（`top`、`skip`、`maxpagesize` 在所有集合上统一）、[Considerations §Use Good Names](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#use-good-names)。
- **现状**：
  - offset 方式用 `offset`（57）+ `limit`（63），`limit` 的默认值有 12/20/30/50/80/100/200/500 等。
  - cursor 方式用 `cursor`（9）+ `pageSize`（8）。
  - 字符分页用 `offset` + `length`（GetExportContent）。
  - 上限类参数有 `maxItems`（9）、`maxResults`（1）。
  - 同一概念用了 `limit`、`pageSize`、`length` 三个名字。
- **外部影响**：把 `pageSize` 和 `length` 改名为 `limit`，会改变 9 个工具的输入 schema；4.0 前修改不额外破坏 3.x 客户端。
- **成本与风险**：低到中。需要同步示例库、迁移表和快照。
- **建议：4.0 前修改。** 统一为 `limit`（页大小或片长度），保留 `offset` 和 `cursor`；不改成 `top`/`skip`，因为 `top` 已经被用作 HMI 坐标。默认页大小写入规范，但不强求所有工具相同。
- **状态**：4.0 前 - P8-60

### API-08　offset 分页缺少一致性说明

- **基线**：[Azure §Collections](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#collections)：不使用快照时，必须说明分页过程中条目可能被跳过或重复。
- **现状**：cursor 模式由宿主维护快照；offset 模式直接读取活动的 TIA 工程，`total` 可能在两次调用之间变化，规范中没有说明。
- **外部影响**：无，只是补充说明。
- **成本与风险**：只需修改文档。
- **建议：4.0 前修改（文档）。** 写明 offset 分页不保证快照一致；需要一致性时使用 cursor 工具，或者先导出再分页。
- **状态**：4.0 前 - P8-60

### API-09　没有可重复请求的标识

- **基线**：[Azure §Repeatability of requests](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#repeatability-of-requests)、[§Exactly Once Behavior](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#exactly-once-behavior--client-retries--service-idempotency)、[Considerations](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md)（所有操作都应幂等）。
- **现状**：客户端不能提供去重 ID；`requestId` 由服务器生成。写入不保证幂等：`Create*` 拒绝已存在的对象（ALREADY_EXISTS），只有 `Ensure*` 是幂等的。结果未知时，后续调用被 `SESSION_RESET_REQUIRED` 拒绝，规范要求不得重放。
- **外部影响**：无。
- **成本与风险**：TIA Openness 不支持去重或回滚，提供 Repeatability 也无法兑现。
- **建议：保留为有意偏差。** 理由：TIA 只能做到“至多一次 + 人工核实”。幂等性的声明见 MCP-02 中的 `idempotentHint`。
- **状态**：有意偏差（已接受）

### API-10　条件写用 `expected*` 参数，读取不返回 ETag

- **基线**：[Azure §Conditional Requests](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#conditional-requests)、[§Computing ETags](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#computing-etags)：读取返回 ETag，写入带 If-Match，不匹配时返回 412。
- **现状**：`expectedPlanHash`、`expectedFingerprint`、`expectedInventoryHash`、`expectedProjectFile` 等参数承担 If-Match 的作用，不匹配时返回 `PLAN_STALE` 或 `IDENTITY_MISMATCH`。读取结果不统一提供 ETag；只有 `GetPlcObjectFingerprints` 和部分预览会返回指纹或哈希。
- **外部影响**：无。
- **成本与风险**：要统一提供 ETag，需要给每类对象定义指纹，成本高。
- **建议：保留为有意偏差。** 概念保留，命名统一见 API-18。
- **状态**：有意偏差（已接受）

### API-11　版本策略

- **基线**：[Azure §API Versioning](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#api-versioning)（每个请求带日期式 `api-version`，不做破坏性变更）、[Considerations §Design for Change Resiliency](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#design-for-change-resiliency)。
- **现状**：版本在进程级固定（`--release-key`）；`schemaVersion` 固定为 4，无法协商；4.0 硬切换，不提供 3.x 别名，靠生成的迁移表；4.x 内的演进政策没有写明。
- **外部影响**：无，只是补充政策。
- **成本与风险**：4.0 硬切换是 2026-10-03 的决定。补写政策只需修改文档。
- **建议：保留为有意偏差，并在 4.0 前补写政策。** 4.x 内只做增量（新工具、新的可选参数、新字段，消费者按 API-03 容忍）；破坏性变更要升 `schemaVersion` 并在发布说明中列出；弃用的方式见 API-12。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

### API-12　没有弃用信号

- **基线**：[Azure §Deprecating Behavior Notification](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#deprecating-behavior-notification)、[Considerations §Communicate Deprecations](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#communicate-deprecations)；MCP 也有[功能生命周期政策](https://modelcontextprotocol.io/specification/2026-07-28/changelog)，弃用窗口至少 12 个月。
- **现状**：没有任何方式在调用时告知工具或参数已被弃用。
- **外部影响**：新增一个警告码（例如 `DEPRECATED`）。按 API-03 的约定，这属于可以容忍的增量。
- **成本与风险**：低。
- **建议：4.0 后修改。** 4.0 没有弃用项；第一次弃用时再引入，前提是 API-03 已完成。
- **状态**：4.0 后

### API-13　时间戳精度与 data 中的时间格式

- **基线**：[Azure §JSON](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#json)（RFC 3339，至多 3 位小数）、[11.2 dates and times](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md#112-guidelines-for-dates-and-times)。
- **现状**：`meta.timestamp` 有 0 到 7 位小数（`yyyy-MM-ddTHH:mm:ss.FFFFFFFZ`）。data 中的时间（如 `expiresUtc`、`createdUtc`）用 `ToString("O")`，固定 7 位小数。Classic HMI 构造器的 `data.timestamp` 使用 `DateTime.Now`，是本地时间带时区偏移（快照的屏蔽规则中注明了这一点）。
- **外部影响**：缩短 meta 的精度不改变字段类型，按 RFC 3339 解析的客户端不受影响；快照中这些值本来就被屏蔽。
- **成本与风险**：低。只需修改 `V4Json.Timestamp`、读取格式和各处 `ToString("O")`。
- **建议：4.0 前修改。** 所有协议层时间统一为 UTC，至多 3 位小数（或者统一为固定 7 位，但要写进规范）。构造器中的本地时间改为 UTC。
- **状态**：4.0 前 - P8-60

### API-14　错误码粒度不足与误用

- **基线**：[Considerations §Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#errors)：顶层错误码属于契约，可恢复的错误要有不同的错误码；[Azure §Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors)。MCP 要求工具错误提供可执行的反馈。
- **现状**：
  - `PlcToolContract.Map`：旧实现报告 `mayHaveChanged=false` 且没有成功时，一律返回 `McpServer.InvalidInput("arguments")`，也就是说“目标不存在”等原生前提失败也被报成参数错误。
  - `McpServer.InfrastructureResult`：旧实现的 success≠true 一律报 `INVALID_ARGUMENT("arguments")`。
  - `FoundationV4Result.Host` 及引擎的同类逻辑：success=false 一律报 `NATIVE_OPERATION_FAILED`。快照中 `CheckProductUpdate {"repository":"x"}` 的结果是 `read-failed` + `NATIVE_OPERATION_FAILED`，而 `data.summary` 写的是 `"repository must be 'owner/name'..."`，这本是参数错误。
  - `ImportOrderTools`：“未满足完成条件”报 `INTERNAL_ERROR`（outcome failed）。
- **外部影响**：错误码和 outcome 会改变（参数错误从 read-failed 变为 rejected-before-operation），这些是契约变化；4.0 前修改不额外破坏 3.x 客户端。
- **成本与风险**：中。需要逐个检查这些映射器的兜底分支，补充类型化的拒绝（旧实现的 status、`PortalErrorCode` 等已经提供了足够的信号），并重新生成响应快照。
- **建议：4.0 前修改。** 原则：兜底分支 MUST NOT 选用 `INVALID_ARGUMENT`，除非确实能证明是输入错误；无法分类时，宁可使用 `NATIVE_OPERATION_FAILED` 或 `INTERNAL_ERROR`，并保留证据。
- **状态**：4.0 前 - P8-60

### API-15　字符偏移单位未声明

- **基线**：[Azure §Returning String Offsets & Lengths](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#returning-string-offsets--lengths-substrings)：返回字符串偏移时，必须说明单位（UTF-8、UTF-16 码元或文本元素）。
- **现状**：`GetExportContent` 的 `offset`/`length` 以及寄存时的 `meta.paging` 都以 UTF-16 码元为单位，`byteLength` 却是 UTF-8 字节数；参数描述只写了“character”。
- **外部影响**：无，只是补充说明。
- **成本与风险**：只需修改文档（可选：在 `export` 中增加 `lengthUnit: "utf16"`）。
- **建议：4.0 前修改（文档）。** 在规范和工具描述中写明单位。
- **状态**：4.0 前 - P8-60

### API-16　`Manage*` 多路复用工具

- **基线**：[Azure §Performing an Action](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#performing-an-action) 与面向资源的标准方法。
- **现状**：95 个 `Manage*` 工具用 `action` 枚举选择读写操作，其中 52 个默认为 `read`。MCP 的注解无法按 action 区分安全性。
- **外部影响**：拆分会使工具数量成倍增加，破坏 4.0 的命名。
- **成本与风险**：高。工具预算对模型很重要。
- **建议：保留为有意偏差。** 理由：模型的上下文有限，而 lite 目录已经有 84 个工具。注解采用保守取值（`readOnlyHint: false`），由 dryRun 默认值和审批兜底。
- **状态**：有意偏差（已接受）

### API-17　响应体中的警告

- **基线**：Microsoft 指南没有响应体警告的概念，只有 `azure-deprecating` 头。
- **现状**：`meta.warnings` 有 12 个码，包括行为披露（`UNVERIFIED_BEHAVIOR`）和恢复提示（`RECOVERY_GUIDANCE`）等。
- **外部影响**：无。
- **成本与风险**：无。
- **建议：保留为有意偏差。** 理由：真机验收状态和恢复步骤必须随每个结果一起送达模型。
- **状态**：有意偏差（已接受）

---

## C. V4 内部不一致（以快照和源码实测为准）

### API-18　预览绑定参数五种叫法

- **基线**：[Considerations §Use Good Names](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#use-good-names)（同一概念始终用同一个名字）。
- **现状**（八版并集中的工具数）：
  - `expectedPlanHash`（16）：内容摘要，SHA-256 十六进制。
  - `expectedToken`（6，Unified HMI 和 `ImportPlcBlockVerified`）：预览令牌，描述为“the token a preceding dryRun=true call returned”。
  - `token`（1，`ApplyToolBatch`）：一次性批次令牌。
  - `expectedFingerprint`（2，描述为“packageFingerprint from the matching inspect/preview”或“documentFingerprint”）。
  - `expectedInventoryHash`（2，描述直接写成“Exact foundation argument: expectedInventoryHash”）。
- **外部影响**：改名会改变 9 个工具的输入 schema；4.0 前修改不额外破坏 3.x 客户端。
- **成本与风险**：低到中。需要同步示例库、迁移表和快照。
- **建议：4.0 前修改。** 只保留两类名称：内容摘要统一为 `expected<Thing>Hash`（`expectedPlanHash`、`expectedInventoryHash`；`expectedFingerprint` 改为 `expectedDocumentHash` 或 `expectedPackageHash`）；一次性令牌统一为 `previewToken`（取代 `expectedToken` 和 `token`）。同时修正泄露实现细节的描述。
- **状态**：4.0 前 - P8-60

### API-19　`expectedProject` 的语义不一

- **现状**：在 Unified 读取工具、`PreviewToolBatch` 和 `RunReadOnlyToolBatch` 中，`expectedProject` 是工程**名称**（“the name of the project … (GetProjectInfo)”）；在 `ApplyGenerationPlan` 中是工程**文件**（“Exact existing or predicted new project file”）；D1 系列工具另有 `expectedProjectFile`（31 个工具）。
- **外部影响**：`ApplyGenerationPlan` 是 4.0 新增的工具，改名不影响 3.x 客户端。
- **成本与风险**：低，只涉及 1 个工具。
- **建议：4.0 前修改。** 把 `ApplyGenerationPlan.expectedProject` 改名为 `expectedProjectFile`；规范中规定 `expectedProject` 只表示工程名。
- **状态**：4.0 前 - P8-60

### API-20　20 种确认开关

- **现状**：`confirmDelete`（34）、`confirm`（30）、`confirmChange`（10）、`confirmWrite`（4）、`confirmImport`（3），以及 `confirmUpload`、`confirmProtectionChange`、`confirmSafetyChange`、`confirmRestart`、`confirmRun`、`confirmExternalExecution`、`allowWrite` 等，共 20 种名称（V21 计数：19 个 `confirm*` 加 `allowWrite`）。
- **外部影响**：统一会改变约 100 个工具的 schema。
- **成本与风险**：高。这些是旧的安全开关，D1 行为族切换到 safe-v4 时会改为 `mode`/`confirm`。
- **建议：保留为有意偏差（4.0）。** 在规范中写明它们属于 current 政策的旧开关；新工具 MUST 使用 `confirm`；各行为族切换到 safe-v4 时一并统一。
- **状态**：有意偏差（已接受）

### API-21　计划哈希的两种格式

- **现状**：V4 的 `Plan.hash`、`CONFIRMATION_REQUIRED.planHash`、`PLAN_STALE.planHash` 和审批的 `PlanHash` 都是 64 位十六进制；生成计划 `tiamcp.plan/1` 的 `planHash` 是 `"sha256:<hex>"`（`CanonicalJson.Hash`）。`ApprovalProtocol.cs:123` 对 `kind == plan` 两种格式都接受，`GenerationExecutionTools.Digest` 会去掉前缀。
- **外部影响**：`ApplyGenerationPlan` 的 `expectedPlanHash` 格式会改变（4.0 新增工具）。
- **成本与风险**：低到中。需要重新生成黄金计划和标准包相关文件。
- **建议：4.0 前修改。** 统一为不带前缀的 64 位十六进制（与 `V4Validation.Hash` 一致）；如果决定保留前缀，就在所有计划哈希上都使用前缀，并写进规范。
- **状态**：4.0 前 - P8-60

### API-22　计划过期时错误码不一；PLAN_STALE 中放令牌

- **现状**：
  - `HostBehavior.BatchPreviewRefusal` 发现计划或工程已变化时，返回 `INVALID_ARGUMENT`（parameter 为 `expectedProjectFile` 或 `expectedPlanHash`），消息为“Plan or project changed; review a fresh preview before applying.”，其他路径对同样的情况返回 `PLAN_STALE`。
  - `ApplyToolBatch` 返回 `PLAN_STALE(planHash: <批次令牌>, reason: "identity"|"preview")`。令牌恰好是 64 位十六进制，能通过哈希校验，但语义错误。
- **外部影响**：错误码会改变。
- **成本与风险**：低。
- **建议：4.0 前修改。** “计划已失效”一律使用 `PLAN_STALE`；令牌不放进 `planHash`，必要时放进 `reason`，或者在 `PlanStaleDetails` 中增加 `previewToken` 字段（与 API-18 一起做）。
- **状态**：4.0 前 - P8-60

### API-23　details 中字符串词表混乱

- **现状**（来自源码中的字面量）：
  - `PRECONDITION_FAILED.condition`：`ConnectPortal`（工具名）、`HMI operation prerequisites`（散文）、`batch-identity`、`native-admission`、`save-explicitly-before-close` 等。
  - `OUTCOME_UNKNOWN.stage`：`worker`、`tool-call`、`PLC operation`、`session operation`、`option-package-operation` 和 `optional-package-operation`（看起来是笔误造成的重复）。
  - `SESSION_RESET_REQUIRED.reason`：`previous-outcome-unknown`、`HMI read session is blocked.`、`HMI read session blocked`。
  - `RESOURCE_UNAVAILABLE.resource`：`tia-openness-environment`、`PLCSIM Advanced API`、`HardwareCatalog`。
  - `IO_FAILED.operation`：`write report`、`CLI input/output`。
- **外部影响**：只改变 details 中的字符串值；按规范这些值仅作诊断用，消费者不应据此分支。
- **成本与风险**：低到中。大约 100 处字面量；快照中可见的不多。
- **建议：4.0 前修改。** 统一为 kebab-case，并为每个字段在规范中列出已知取值（这一层不设闭集，消费者必须容忍未知值）。
- **状态**：4.0 前 - P8-60

### API-24　省略 null 的例外；COMPILE_ERRORS 绕过校验

- **现状**：
  - `PreconditionFailedDetails.Parameter` 和 `OutcomeUnknownDetails.Reason` 带有 `JsonIgnore(WhenWritingNull)`。快照中 PRECONDITION_FAILED 的 details 只有 `{condition, target}`，与“所有字段都输出”的规则冲突。
  - `CompileErrorsDetails` 没有设置 `JsonPropertyOrder`，也不调用 `V4Validation.Details`；`CompileErrorMessage` 中的路径和描述不检查是否为空。
- **外部影响**：details 中会多出 `parameter: null` 和 `reason: null` 两个字段（增量）。
- **成本与风险**：低。
- **建议：4.0 前修改。**
- **状态**：4.0 前 - P8-60

### API-25　`requiresSessionReset` 与实际锁定不一致

- **现状**：`V4Validation.Meta` 要求所有 unknown 信封都设置 `requiresSessionReset: true`。但 `SessionBehavior.LocksSession(nativeIssued, unknown, mutation)` 只在发出过原生调用的写入上锁定会话。例如：`SaveExportContent` 的 `file-write` 未知结果不走 Openness 通道，不会锁会话；在第一次原生调用之前出现的 unknown 也不会锁。`docs/releases/v4.0.0.md` 写的是“Only results that crossed a native call lock the session”。
- **外部影响**：如果改为字段等于实际锁定状态，部分 unknown 信封的 `requiresSessionReset` 会变成 false，同时需要放宽校验。另一种做法是保留 true，但把字段定义成“建议重建会话”。两种做法都改变契约的语义。
- **成本与风险**：低到中。这个字段直接指导模型是否执行恢复流程，含义必须明确。
- **建议：4.0 前修改。** 推荐让 `requiresSessionReset` 表示“后续 Openness 调用会被拒绝，直到完成恢复”，与实际锁定状态严格一致；unknown 但未锁定的情况仍然禁止重放（OUTCOME_UNKNOWN 本身就带有这一要求）。同步修改 `V4Validation`。
- **状态**：4.0 前 - P8-60

### API-26　TIMEOUT 不用于只读；worker 超时一律为 unknown

- **现状**：phase6 规定只读或可证明未发出动作的超时使用 `TIMEOUT`。实际上 `TIMEOUT` 只在工作台控制中出现（1 处）；worker 通道超时（包括只读调用）通过 `workerOutcomeUnknown` 一律归为 `OUTCOME_UNKNOWN`，整个 worker 代次被标为故障。
- **外部影响**：无。
- **成本与风险**：worker 代故障后状态确实未知，现在的行为是合理的。
- **建议：保留为有意偏差（文档）。** 在规范中写明：TIMEOUT 只用于宿主自身可以证明没有派发的超时；worker 通道超时属于通道故障，按 unknown 处理。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

### API-27　取消的映射不一致

- **现状**：
  - 派发前取消：`CANCELLED(tool-queue)`。
  - 审批预检中取消：`CANCELLED(approval-precheck)`。
  - 审批**等待**中取消：`ApprovalPipe` 把 `reason` 设为 `"denied"`，最终返回 `CONFIRMATION_REQUIRED/denied`。
  - 只读调用派发后取消：`HostFailurePolicy.Classify` 返回 Cancelled，`HostBehavior.FailureError` 把 stage 硬编码为 `approval-precheck`，execution 为 `not-started`，但调用实际上已经派发。
- **外部影响**：错误码、stage 和 execution 的值会改变。
- **成本与风险**：低。
- **建议：4.0 前修改。** 审批等待中取消改为 `CANCELLED(approval-wait)`；`FailureError` 接受调用方传入的 stage；已派发的只读调用取消后报 `read-failed` + `CANCELLED`（`TIMEOUT` 和 `CANCELLED` 的约束需要相应放宽）。
- **状态**：4.0 前 - P8-60

### API-28　序列化后修改 JSON，绕过校验

- **现状**：
  - `ApprovalPrecheck.Mark` 直接改写 `meta.outcome`、`execution` 和 `completeness`，并追加警告。
  - `ApprovalResult.Decorate` 改写 `requestId`，并追加 `APPROVAL_DISABLED`。
  - `RecoveryHints.Attach` 追加 `RECOVERY_GUIDANCE`。
  
  这三处都是在 `JsonNode` 上操作，不经过 `Envelope` 的构造器。理论上，Mark 可以产生 `rejected-before-operation` + `OUTCOME_UNKNOWN` 这种校验会拒绝的组合。另外，`APPROVAL_DISABLED`、`RECOVERY_GUIDANCE` 和 `APPROVAL_PRECHECK_REFUSED` 在源码中没有任何 `WarningCode.*` 的引用，只以字面量出现。
- **外部影响**：无。这是内部实现问题，输出字节不变。
- **成本与风险**：低到中。改为反序列化成 `Envelope`，修改后再重新构造。
- **建议：4.0 前修改。** 保证所有发出的信封都经过 `V4Validation`。
- **状态**：4.0 前 - P8-60

### API-29　data 中残留旧的判定字段

- **现状**（快照中 `data` 键的出现次数）：`error` 46、`operationSuccess` 40、`ok` 34、`warnings` 34、`status` 30、`meta` 16、`success`（Foundation 宿主结果）；工作台控制在 `data.controlRefusal` 中重复了一份带 `code` 的拒绝对象（64 次）。例：`ExchangeSystemDiagnosticsSettings` 的 `data{"error":"Native observation failed…","operationSuccess":false,"status":"InvalidState"}` 与 `error.code: PROJECT_NOT_BOUND` 同时出现。
- **外部影响**：删除这些字段会改变大约 400 个旧实现映射工具的 data 形状，破坏依赖这些字段的客户端。
- **成本与风险**：高。这些字段也是原生证据的一部分。
- **建议：保留为有意偏差（4.0）。** 规范中写明：data 中的判定字段只是证据，以 `meta.outcome` 为准；新代码 MUST NOT 在 data 中加入 `ok`、`success`、`operationSuccess` 或 `error` 字段。到 5.0 再考虑清理。
- **状态**：有意偏差（已接受）

### API-30　导出句柄的表示不一致

- **现状**：
  - 寄存时：`data{content, export{id, mediaType:"application/json", byteLength, sha256, expiresUtc}}`，`expiresUtc` 为 V4 时间戳格式。
  - `GetExportContent`：`data{text, export{id, tool, target, createdUtc, totalLength, mediaType:"text/plain", byteLength, expiresUtc, sha256}}`，时间用 `ToString("O")`。
  - 同一个句柄有两种 `mediaType`。`ListExportHandles` 只有 `limit` 没有 `offset`，截断时报 completeness partial，而不是给出 paging。
  - GetExportContent 的工具描述写着“Concatenate data.text pages”，寄存后的第一页字段却叫 `content`。
- **外部影响**：寄存结果的字段名会改变。
- **成本与风险**：低。
- **建议：4.0 前修改。** 统一使用 `text`（或统一使用 `content`）；统一 `mediaType`；`export` 对象统一使用 `ExportHandle` 类型（可以在其中加入 `tool`、`createdUtc`）；`ListExportHandles` 支持 offset 分页。
- **状态**：4.0 前 - P8-60

### API-31　分页单位混用；页级完整度含义不一

- **现状**：
  - `meta.paging` 同时用于条目分页和字符分页（导出句柄），却没有标明单位。
  - FindTools 的中间页报 `completeness: complete`；GetOpennessGuidance 的中间页（`total 282, limit 20`）报 `completeness: partial`，并带有 `INCOMPLETE_DATA`。
  - 字符分页的 `limit` 回显的是请求值，不是实际返回值。
- **外部影响**：会增加一个 `paging.unit` 字段（按 API-03 属于增量，但 meta 目前对 schema 是封闭的），以及改变部分工具的 completeness。
- **成本与风险**：低。
- **建议：4.0 前修改。** 规定 completeness 描述的是本页是否被完整交付，paging.complete 表示后面是否还有更多页；增加 `unit: "items"|"utf16"`，或者把导出句柄的分页信息挪到 `data.export` 中。
- **状态**：4.0 前 - P8-60

### API-32　CallTool 派发前错误的 `meta.tool` 与旧规范不符

- **现状**：phase6 规定派发前失败时 `meta.tool=CallTool`。代码中，目标存在且错误是字段级的 `INVALID_ARGUMENT` 时，使用目标工具名（v4.0.0.md：“names the target tool's field”）。
- **外部影响**：无。
- **成本与风险**：无。
- **建议：保留为有意偏差（文档）。** 把现状写进新规范：字段级的参数错误指向目标工具，其余的派发前错误指向 CallTool。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

### API-33　CLI 退出码的覆盖规则未写入规范

- **现状**：`CliBoundary.Write` 在 succeeded 时还会根据完整度返回退出码（unknown→5、partial→4、none→3）；非工具动词的任何异常都返回 2，与“2 = 操作前拒绝”冲突；在线监视自检失败也返回 2。
- **外部影响**：会改变脚本可见的退出码。
- **成本与风险**：低。
- **建议：4.0 前修改。** 有两种做法：一是把完整度覆盖规则写入规范（建议保留，因为脚本需要识别不完整的数据），二是去掉这一规则。无论哪种，非工具动词的异常都应改为 3（失败）或 70（进程级故障），不再使用 2。
- **状态**：4.0 前 - P8-60

### API-34　枚举值大小写多套并存

- **现状**：信封中的状态词使用 kebab-case（`rejected-before-operation`、`safe-v4`、`plan-confirmation`）；输入选择值使用 camelCase（V21 中 903 个，如 `createFromMasterCopy`）；原生名称保持 PascalCase（364 个）；发布键是数字字符串；还有 `""` 哨兵。
- **外部影响**：统一会改变大量契约。
- **成本与风险**：高。
- **建议：保留为有意偏差。** 写成规则：协议层状态词用 kebab-case；工具输入的选择值用 camelCase；Siemens 原生的名称和枚举保持原样。
- **状态**：有意偏差（已接受）

### API-35　`""` 缺省哨兵

- **现状**：可选字符串参数常以 `""` 作为缺省值，如 `expectedPlanHash`、`expectedProjectFile`、`softwarePath`（20 个工具中 `""` 表示“唯一的 PLC”）、`mode`。这与 phase6 第 2 节“缺省、空值、空数组不相互代替”的原则不一致。
- **外部影响**：改为可空或可省略会改变 schema 和默认行为。
- **成本与风险**：中。牵涉 SDK 的参数推断。
- **建议：保留为有意偏差（4.0）。** 在规范中写明 `""` 的含义；新参数 SHOULD 设为可省略，而不是使用 `""` 哨兵。
- **状态**：有意偏差（已接受）

### API-36　已定义但未使用的类型和错误码

- **现状**：`PreviewData` 从未被构造，因此 `V4Validation.Preview` 在发布路径上从不执行；`CursorScope` 从未被构造；`NATIVE_CAPABILITY_LIMIT` 从未发出；`AUTHENTICATION_REQUIRED` 和 `TIMEOUT` 都只有 1 个构造点。`Plan`、`PlanIdentity` 等只在 safe-v4 候选路径中使用。
- **外部影响**：删除 `NATIVE_CAPABILITY_LIMIT` 会缩小闭集，对“已知码”列表是破坏性变更；标为保留则没有影响。
- **成本与风险**：低。
- **建议：4.0 前修改。** 逐项决定保留还是删除：`NATIVE_CAPABILITY_LIMIT` 要么开始使用，要么删除；`PreviewData` 和 `CursorScope` 在规范中标为“保留给 safe-v4 或 cursor 宿主”，或者真正接入。
- **状态**：4.0 前 - P8-60

### API-37　批次聚合的错误码

- **现状**（`McpServer.BatchResult`）：
  - 全部未开始时，返回 `PRECONDITION_FAILED(condition: "batch-results")`。
  - 没有成功项但已经执行时，outcome 为 failed 或 read-failed，错误码却是 `PRECONDITION_FAILED`。
  - 子项有 partial 但没有成功项时，返回三个计数都为 0 的 `PARTIAL_FAILURE`。
  - 批次项中多出 `outcomeUnknown` 字段（在 `{index, target, result}` 之外）。
- **外部影响**：父信封的错误码会改变。
- **成本与风险**：低。
- **建议：4.0 前修改。** 父错误码改用触发停止的子项（`causeIndex`）的错误码，或者把 `NATIVE_OPERATION_FAILED` 与子项计数放进证据。规范中写明 `outcomeUnknown` 字段，或者改为由子信封本身表达（子信封的 outcome 为 unknown）。
- **状态**：4.0 前 - P8-60

### API-38　命名检查只覆盖改名

- **现状**：`build-tools/release/CatalogData.cs` 只对改名执行动词闭集（66 个）检查，新增的工具只检查 `^[A-Z][A-Za-z0-9]+$`。因此 `PrefillWorkbenchForm` 的 `Prefill` 不在闭集内，但没有被发现。`Manage*` 的对象单复数不统一（`ManageDeviceUsers`/`ManageDeviceUserGroup`、`ManagePlcExternalSources`、`ManageDcbLibraries`、`ManageDriveTelegrams`）。
- **外部影响**：只改检查不影响契约；改名才会影响。
- **成本与风险**：低。
- **建议：4.0 前修改。** 把动词检查扩展到所有名称，在闭集中加入 `Prefill`（或者给工具改名）；`Manage*` 的单复数在规范中定规则（建议用对象类型的单数），已有名称是否改名由维护者逐项决定。
- **状态**：4.0 前 - P8-60

### API-39　规范承诺的 `data.plan` 与实际发布的不符

- **现状**：phase6 第 4 节说预览返回 `data.plan{hash, …}`。4.0 的发布配置全部是 current 政策，预览返回的是旧形状（例如 `data.planHash`、`data.projectFile`），`data.plan` 只出现在 safe-v4 候选路径中（`DeviceCreation.cs:71` 等）。
- **外部影响**：无，只修改文档。
- **成本与风险**：无。
- **建议：保留为有意偏差（文档）。** 新规范把 D1 预览协议标为“safe-v4 族切换后生效”，并列出 current 政策下各工具预览的实际字段。
- **状态**：有意偏差（已接受）；4.0 前把现状写成规范规则（P8-60）

---

## 附：已对照、符合或等价的规则（不计入偏差）

| 规则 | 结论 |
|---|---|
| MCP：未知工具返回 -32602 协议错误 | 符合（快照 11 次） |
| MCP：输入校验错误作为工具执行错误（`isError: true`） | 符合（`BindV4Call` 在 SDK 绑定之前执行） |
| MCP：`structuredContent` 与 TextContent 中的序列化 JSON 一致 | 符合（逐字节相同，`ToolResult` 强制检查） |
| MCP：`isError` 与结果状态一致 | 符合（`isError = !ok`，快照中不一致 0 次） |
| MCP：工具名为 1–128 个字符、ASCII、唯一 | 符合（最长 45 个字符） |
| MCP：`tools/list` 不按连接变化 | 符合（profile 在进程级固定） |
| MCP：不使用已弃用的 Logging，日志写 stderr 和文件 | 符合 2026-07-28 的方向 |
| MCP：服务器校验所有输入、清理输出 | 符合（类型化 DTO；`Clean`、`SafeDiagnostic`） |
| MCP：过期的句柄返回工具执行错误 | 符合（`NOT_FOUND`） |
| Microsoft：JSON 字段名用 camelCase、缩写不全大写 | 符合（753 个参数名中仅 5 个原生键名例外） |
| Microsoft：拒绝无法理解的请求字段 | 符合（封闭 DTO，大小写重复也拒绝） |
| Microsoft：错误码属于契约，消息可以变化 | 符合（消息为英文，消费者不解析） |
