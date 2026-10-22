# V4 工具协议规范

状态：现行 · 最后核对：2026-10-11，master `3a227657`

[设计规范索引](README.md) · [V4 协议偏差清单](deviations/protocol.md)

> **依据**：master `3a227657` 的源码、`manifest/contracts/v4/baseline/*.json`（契约快照格式 1）与 `manifest/contracts/v4/responses/*.json`（响应快照格式 3，`captureMode=sdk-only-fixture`，stdio）。本文描述 4.0 的现行协议，同时是新工作的规范：新工具和新代码必须满足标为 MUST 的规则。现有实现与本文或基线的差异逐条列在 [V4 协议偏差清单](deviations/protocol.md)，其中 4.0 前的修改由 P8-60 实施。
>
> **基线（维护者决定 2026-10-11）**：首要基线是 MCP 规范（现行修订 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)；当前依赖的 C# SDK `ModelContextProtocol 0.3.0-preview.4` 内含的协议版本字符串最高为 `2025-06-18`，所以实际协商的是 [2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18) 修订；4.0 保持这一修订，见第 0.4 节）。次要基线是 Microsoft REST API Guidelines：[Azure REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md)、[Considerations for Service Design](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md) 和已归档的 [Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md)。Microsoft 指南用于错误结构、分页、长任务和版本这几类概念；REST 专属规则在第 0.3 节说明不适用的原因。
>
> **规范用语**：MUST / MUST NOT / SHOULD / SHOULD NOT / MAY 按 RFC 2119 / RFC 8174 解释。“生产者”指 MCP 宿主与工具实现；“消费者”指 MCP 客户端、CLI 调用方以及读取信封的桥接、批次和工作台代码。标为“现状”的句子只描述已有行为，不是规范要求。
>
> 每节末尾有 **MCP 基线** 与 **Microsoft 基线** 两段说明，其中的偏差编号（`MCP-nn`、`API-nn`）对应 [偏差清单](deviations/protocol.md) 的条目。

## 0. 基线与映射总则

### 0.1 证据来源

| 主题 | 源码 / 数据 |
|---|---|
| 信封、meta、警告与批次类型 | `src/Logic/V4/Envelope.cs` |
| 错误码与 details 联合 | `src/Logic/V4/Error.cs` |
| 不变量 | `src/Logic/V4/V4Validation.cs` |
| JSON 编解码与枚举拼写 | `src/Logic/V4/V4Json.cs` |
| 分页、游标作用域与导出句柄 | `src/Logic/V4/Paging.cs` |
| 计划（D1 候选） | `src/Logic/V4/Plan.cs` |
| MCP 结果映射与 CLI 退出码 | `src/Logic/V4/ResultMapping.cs` |
| 宿主准入与失败分类 | `src/Logic/V4/HostBehavior.cs`、`src/Shared/HostFailurePolicy.cs`、`src/Shared/SessionBehavior.cs` |
| Foundation 包装 | `src/FoundationHost/FoundationV4Result.cs` |
| 引擎旧实现到 V4 的映射 | `src/Shared/*ToolContract.cs`、`src/Shared/*Contract.cs` |
| CallTool 与批次 | `src/Shared/Host/McpServer.ToolBridge.cs`、`src/Shared/Host/McpServer.Batch.cs` |
| 大响应寄存与导出句柄 | `src/Shared/Host/McpServer.Exports.cs`、`src/Shared/Host/ExportTools.cs`、`src/Logic/ModelContextProtocol/ExportStore.cs` |
| 审批 | `src/EngineHost/ModelContextProtocol/Tools/McpServer.Approval.cs`、`src/Shared/ApprovalPipe.cs`、`src/Shared/ApprovalProtocol.cs`、`src/Shared/ApprovalResult.cs` |
| 恢复提示 | `src/Logic/ModelContextProtocol/RecoveryHints.cs`、`docs/reference/recovery-guidance.md` |
| 生成计划 | `src/Shared/Host/GenerationExecutionTools.cs`、`src/Logic/Generation/*` |
| CLI | `src/Engine/Cli/CliBoundary.cs`、`CliCommands.cs`、`CliToolExecution.cs` |
| 操作类别 | `src/Logic/ModelContextProtocol/ToolTaxonomy.cs`、`ToolMetadata.cs`、`docs/reference/tool-matrix.md` |

数量统计用 Python 脚本直接读取八份快照。并集为 514 个工具，各发布版本为 215/237/253/260/267/277/503/514 个（`docs/releases/v4.0.0.md` 的表格仍是 214–514 的旧数，并标注了 TODO）。响应快照共 7466 次调用，其中 3192 次只记录摘要，4263 个完整 V4 信封（含批次子信封）参与了组合统计。快照主要覆盖准入拒绝和离线调用；离线环境无法产生的原生成功、部分成功和未知结果，结论来自源码。

### 0.2 REST 概念到 MCP/V4 的映射

| Microsoft REST 概念 | MCP 层 | V4 层（本协议） |
|---|---|---|
| 资源 URL + HTTP 方法 | `tools/call` 的 `name` + `arguments` | 工具名（动词 + 对象）与类型化参数；`Manage*` 工具用 `action` 选择操作 |
| HTTP 状态码 | JSON-RPC error（协议错误）或 `CallToolResult.isError` | `meta.outcome` 与 `isError = !ok` |
| `x-ms-error-code` 头 + `error.code` | 无 | `error.code`（闭集，UPPER_SNAKE） |
| `error.target` / `details[]` / `innererror` | 无 | 按错误码定型的 `error.details` 对象 |
| `api-version` 查询参数 | 协商的 MCP 协议版本（2026-07-28 起每个请求放在 `_meta`） | 发布键（宿主进程级）+ `schemaVersion: 4`（固定） |
| `nextLink` / `top` / `skip` / `maxpagesize` | 列表方法的 `cursor` / `nextCursor` | 工具参数 `offset`/`limit` 或 `cursor`/`limit`，结果放在 `meta.paging` |
| `Operation-Location` 状态监视资源 | tasks 扩展（2026-07-28 为官方扩展） | 无通用机制；调用同步阻塞，生成运行有 `runId` + `GetGenerationRun` |
| `Retry-After` | 进度通知可用于重置超时 | 无 |
| ETag / `If-Match` / 412 | 无 | `expectedPlanHash` 等 `expected*` 参数；`PLAN_STALE`、`IDENTITY_MISMATCH` |
| `If-None-Match: *` | 无 | `overwrite: false`；`ALREADY_EXISTS` |
| `Repeatability-Request-ID` | 2026-07-28 要求断流后用新请求 ID 重发 | 不提供去重；未知结果禁止重放，会话被锁定 |
| `x-ms-client-request-id` / traceparent | `_meta` 中的 `traceparent`（2026-07-28） | `meta.requestId` 由服务器生成；不读取客户端追踪上下文 |
| `azure-deprecating` 头 | 规范层的功能生命周期政策 | 无；硬切换，靠迁移表 |

### 0.3 不适用的 REST 规则

以下 REST 规则没有对应的 MCP 载体，本协议不采用，也不计为偏差：URL 结构与路径版本、HTTP 方法语义（GET/PUT/PATCH/DELETE 的幂等约定）、HTTP 状态码的选取、`ETag`/`Last-Modified` 响应头与 304、`Location`/`Operation-Location` 头、CORS、`$select`/`$expand`/`$filter`/`$orderby` OData 查询语言、`Retry-After` 头、JSON Merge Patch。这些规则表达的语义如果在 V4 中有等价物（幂等、条件写、长任务、分页），在对应章节单独评估。

### 0.4 MCP 修订与 V4 会话（维护者决定 2026-10-11）

- 4.0 协商 MCP [2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18) 修订，这是当前依赖的 `ModelContextProtocol 0.3.0-preview.4` 支持的最高修订。现行修订 2026-07-28 是无状态协议：去掉了协议层会话和 `Mcp-Session-Id`，跨请求的状态改用显式标识引用。4.0 不迁移到该修订；迁移放在 4.x，在 .NET SDK 支持该修订后进行，届时一并处理 MCP-01、MCP-05、MCP-12 和 MCP-15。4.0 发布说明写明所支持的 MCP 修订。
- 本规范中的“会话”“本会话”“新会话”都指 **V4 会话**：宿主为一个 MCP 会话维护的全部服务器端状态。4.0 中 V4 会话由 MCP 传输会话承载：stdio 是一个宿主进程中的唯一会话，HTTP 是每个 MCP 会话一个。V4 会话随传输会话开始和结束，不能在另一个传输会话中恢复；“开启新的 MCP 会话”即开始一个新的 V4 会话。
- 下列状态属于 V4 会话，作用域都不超过所属的 V4 会话：

| 状态 | 作用域与有效期 | 依据 |
|---|---|---|
| worker 进程与 worker 代次 | 每个 V4 会话一个 worker；代次故障后由 `RestartOpennessWorker` 或 `DisconnectPortal` 开始新代次 | 第 10 节；[架构概览](architecture.md) 第 6 节 |
| Openness 通道 | 每个 V4 会话一条，原生调用串行 | 第 10.2 节 |
| TIA 进程租约 | 连接期间独占选定的 TIA 进程；`DisconnectPortal` 或会话结束时以非拥有方式释放 | 第 10.4 节 |
| 工程绑定与绑定纪元 | 由 `ConnectPortal`、`AttachOpenProject` 等建立，随会话结束 | 第 10.3 节 |
| 会话锁 | 越过原生调用的写入结果未知后生效，直到 `DisconnectPortal` 后开启新会话 | 第 10.2 节 |
| 生成计划（`planId`）与生成写栅栏 | 计划只属于当前会话和绑定；运行期间写栅栏拒绝本会话中不属于该运行的写调用 | 第 9.6 节 |
| 批次令牌 | 10 分钟，只能使用一次 | 第 9.5 节 |
| 导出句柄 | 只能在签发它的会话中读取和清理，有效期 24 小时 | 第 8 节 |
| Unified 读游标 | 限定在同一发布版本、会话、绑定、查询和快照内 | 第 7.3 节 |
| 导入暂存 | 按会话分目录：`<包根>/staging/<会话>/<批次>/` | [架构概览](architecture.md) 第 8 节 |

生成运行记录（`tiamcp.run/1`）、审计链和调用日志不属于 V4 会话：它们写在数据根中，宿主重启后仍可读取。4.x 迁移到无状态修订时，需要为这些状态设计由服务器签发的显式句柄（MCP-01）。

## 1. 传输映射

### 1.1 工具结果

- 每个 V4 工具结果的 `structuredContent` **MUST** 是第 2 节定义的信封对象。
- `content` **MUST** 恰好包含一个 `TextContent`，其 `text` 是同一信封经 `V4Json.Serialize` 得到的 JSON 文本（UTF-8，无缩进，字段顺序固定）。实现位于 `McpResult.From`。
- `isError` **MUST** 等于 `!ok`。桥接层的 `ToolResult` 会检查文本与 `structuredContent` 是否一致、`isError` 与 `ok` 是否一致，不一致时抛出 `InvalidDataException`。快照中 `isError` 与 `ok` 的不一致数为 0。
- 不使用 image、audio、`resource_link` 或嵌入资源内容块。超大结果改为在信封内返回导出句柄（第 8 节）。
- 不设置结果级 `_meta`（SDK 版本不支持 2026-07-28 的 `serverInfo`/`resultType`）。

### 1.2 协议错误与工具错误

- 只有协议层问题使用 JSON-RPC error。现状：直接调用本版本未注册的工具时，由 SDK 返回 `-32602` `"Unknown tool: '<name>'"`。快照中有 11 次，都是在 V20 上直接调用 V21 专有工具。
- 参数不合法、准入拒绝、执行失败、部分完成和结果未知 **MUST** 作为工具结果返回（`isError: true` + 信封），不得伪装成 JSON-RPC 错误。V4 的直接调用入口在 SDK 绑定之前，执行与 CallTool、批次相同的 `BindV4Call` 准入，所以类型错误、未知字段、大小写重复和二次 JSON 编码都返回 `INVALID_ARGUMENT` 信封。
- 旧实现内部仍然抛出 `McpException`（例如 `ProjectSessionTools.cs` 中有 24 处）。各契约包装层通过 `HostFailurePolicy.Classify` 把这些异常转成 V4 信封，`InvalidParams`（-32602）归类为参数错误。

### 1.3 同形调用面

- 直接调用、`CallTool`、批次子结果和 CLI 的 stdout 使用同一信封。
- `CallTool` 派发成功后 **MUST** 原样返回目标信封，`meta.tool` 和 `meta.requestId` 保持目标的值；**MUST NOT** 把目标结果再包进字符串。
- 派发前失败时，`meta.tool` 为 `CallTool`。例外：目标存在、而且错误是指向具体字段的 `INVALID_ARGUMENT` 时，`meta.tool` 为目标工具名（`McpServer.ToolBridge.cs` 约第 302 行）。
- CLI 工具命令向 stdout 输出一行同样的 JSON，诊断信息只写 stderr（第 14 节）。

> **MCP 基线**：[Tools §Tool Result / §Structured Content](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#structured-content) 规定，返回 `structuredContent` 时 SHOULD 同时在 TextContent 中给出序列化 JSON。V4 符合，而且两者逐字节相同。[§Error Handling](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#error-handling) 区分协议错误与工具执行错误，并把输入校验错误归为工具执行错误；[CallToolResult](https://modelcontextprotocol.io/specification/2026-07-28/schema#calltoolresult) 要求工具产生的错误放在结果中并设置 `isError: true`。V4 符合：未知工具返回 `-32602`，参数错误返回 `isError` 结果。V4 在 `structuredContent` 内另加了一层自己的信封（MCP-04），而且 `isError` 对 `partial` 和 `unknown` 也为 true（见第 4.7 节）。2026-07-28 的结果级 `_meta`/`resultType` 需要升级 SDK 才能提供（MCP-15）。
>
> **Microsoft 基线**：[Azure Guidelines §Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors) 要求错误码同时出现在 `x-ms-error-code` 头和响应体中。MCP 没有响应头，V4 只在信封中给出 `error.code`，并用 `isError` 承担“非成功”标志。这是传输适配，不计偏差。

## 2. 信封

### 2.1 结构

真实示例（V20，`CompileHmiDiagnostics`，未连接 TIA；快照对键排序，并屏蔽了时间戳和请求 ID）：

```json
{"data": null,
 "error": {"code": "PRECONDITION_FAILED",
           "details": {"condition": "ConnectPortal", "target": null},
           "message": "No TIA Portal is connected. Call ConnectPortal with an explicitly selected processId first."},
 "meta": {"behaviorPolicy": "current", "completeness": "none", "execution": "not-started",
          "outcome": "rejected-before-operation", "paging": null, "releaseKey": "20",
          "requestId": "<string:requestId>", "requiresSessionReset": false,
          "timestamp": "<string:timestamp>", "tool": "CompileHmiDiagnostics",
          "warnings": [{"code": "UNVERIFIED_BEHAVIOR", "details": {}, "message": "Native behavior retains the current policy; V4 native acceptance is pending."},
                       {"code": "RECOVERY_GUIDANCE", "details": {"...": "..."}, "message": "▶ RECOVERY: read GetToolUsage, verify the target and the stated preconditions, then review a new preview before another call."}]},
 "ok": false, "schemaVersion": 4}
```

| 字段 | 类型 | 规则 |
|---|---|---|
| `schemaVersion` | 整数 | **MUST** 为 `4` |
| `ok` | 布尔 | **MUST** 等于 `meta.outcome == "succeeded"` |
| `data` | 对象或 `null` | 工具结果；数组 **MUST** 包在对象内（如 `data.items`）；**MUST NOT** 是标量或顶层数组 |
| `error` | 对象或 `null` | 成功时 **MUST** 为 `null`，非成功时 **MUST** 不为 null（第 5 节） |
| `meta` | 对象 | **MUST** 存在（第 3 节） |

### 2.2 编码规则

- 生产者 **MUST** 输出无 BOM 的 UTF-8、无缩进的 JSON，属性名使用 camelCase，按 `JsonPropertyOrder` 固定顺序输出：`schemaVersion, ok, data, error, meta`；meta 的顺序见第 3 节。
- 数字 **MUST** 是有限值（不得为 NaN 或 Infinity）。同一对象中 **MUST NOT** 有重复属性名。数组 **MUST** 存在（无值时为 `[]`），数组元素 **MUST NOT** 为 null。
- 信封、meta 和 details 中的字段在未知或不适用时 **MUST** 输出 `null`，不得省略。PRECONDITION_FAILED.details.parameter 与 OUTCOME_UNKNOWN.details.reason 也必须显式输出 null（API-24）。
- 协议层时间戳 **MUST** 是 UTC RFC3339、固定七位小数且以 Z 结尾；data.timestamp 以及 createdUtc/expiresUtc 等也适用。读取方兼容原有 0–7 位 UTC 拼写；未指定时区的 DateTime 拒绝序列化（API-13）。
- 闭集枚举的拼写：`outcome`、`execution`、`behaviorPolicy` 用 kebab-case；`completeness` 与 `paging.mode` 是单个小写单词；错误码和警告码用 UPPER_SNAKE（`V4Json.CreateOptions`）。
- `V4Json` 的读取端是严格模式：区分大小写，未映射成员会被拒绝（`UnmappedMemberHandling.Disallow`），枚举只接受已定义的拼写。用 `V4Json` 读信封的消费者因此会拒绝任何新增字段或新错误码（API-03）。

### 2.3 不变量

以下规则由构造器强制执行（`V4Validation.Envelope/Meta/Batch/Preview`）。生产者 **MUST** 满足，消费者 **MAY** 依赖：

1. `ok == (outcome == succeeded)`，`ok == (error == null)`。
2. `outcome == partial` 当且仅当 `error.code == PARTIAL_FAILURE`；`outcome == unknown` 当且仅当 `error.code == OUTCOME_UNKNOWN`。
3. `NOT_EXECUTED` 必须伴随 `rejected-before-operation`。`SESSION_RESET_REQUIRED` 必须伴随 `rejected-before-operation` 和 `requiresSessionReset == true`。`TIMEOUT` 只能伴随 `read-failed` 或 `rejected-before-operation`。
4. outcome 与 execution 的对应关系见第 4.4 节；`unknown` 时 `requiresSessionReset` 必须为 true。
5. `succeeded` 且 `completeness == partial` 时，`warnings` 不得为空。
6. `behaviorPolicy == current` 时，必须包含 `UNVERIFIED_BEHAVIOR` 警告。
7. `releaseKey` 只能取八个发布键之一或 null；`tool` 和 `requestId` 不得为空串；`warning.message` 不得为空串。
8. 批次聚合与预览约束见第 11 节和第 9.3 节。

现状限制：审批预检标记、审批装饰和恢复提示是在序列化之后直接修改 JSON 的（`ApprovalPrecheck.Mark`、`ApprovalResult.Decorate`、`RecoveryHints.Attach`），不会重新经过上述校验（API-28）。

> **MCP 基线**：2026-07-28 允许 `structuredContent` 为任意 JSON 值（[Tools §Structured Content](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#structured-content)）；V4 固定使用对象，这个选择是允许的。信封是 V4 在 MCP 之上加的一层，与 MCP 的 `isError`、JSON-RPC `id` 部分重叠（MCP-04，有意保留：同一形状要同时服务 CLI、批次、worker 通道和审批审计）。
>
> **Microsoft 基线**：[Azure §JSON](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#json) 要求字段名用 camelCase、区分大小写、缩写词不全大写，V4 符合。同一节要求省略 null 字段，V4 有意始终输出 null（API-05）。时间戳规则要求 RFC 3339 且至多 3 位小数，V4 最多 7 位（API-13）。未知请求字段要拒绝，V4 符合。

## 3. meta

| 字段 | 类型 | 语义与规则 |
|---|---|---|
| `timestamp` | 字符串 | 信封构造时刻（UTC）。大响应寄存后保留原信封的时间戳 |
| `releaseKey` | 字符串或 null | 宿主进程的发布键（`14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21`）。只有发布键无法确定时才 **MAY** 为 null，**MUST NOT** 猜测。快照中 null 出现 0 次 |
| `tool` | 字符串 | 实际执行的 V4 工具名。CallTool 的规则见第 1.3 节 |
| `requestId` | 字符串 | 调用关联 ID，取值优先级：审计调用 ID → 调用日志关联 ID → 新生成的 32 位小写 GUID（`Meta.Correlate`）。审批过的调用使用审批请求的 ID（`ApprovalResult.Decorate`）。`ApplyToolBatch` 为每个子项生成新 ID。客户端提供的 ID 不会被采用 |
| `outcome` | 枚举 | 第 4 节 |
| `execution` | 枚举 | 第 4 节 |
| `requiresSessionReset` | 布尔 | 表示实际的 Openness 准入锁；unknown 未锁定时为 false，SESSION_RESET_REQUIRED 时必须为 true。未知结果始终禁止重放（第 10.2 节、API-25） |
| `behaviorPolicy` | 枚举 | `not-applicable`（不属于 D1 行为族或纯宿主工具）、`current`（沿用 3.x 原生行为，尚未通过真机验收）、`safe-v4`（已验收的新行为；4.0 的发布配置中没有选用） |
| `completeness` | 枚举 | 第 4.3 节 |
| `paging` | 对象或 null | 第 7 节 |
| `warnings` | 数组 | 第 6 节；按发出顺序排列，装饰类警告追加在末尾 |

> **MCP 基线**：2026-07-28 用请求 `_meta` 传协议版本、客户端信息和追踪上下文，并建议在结果 `_meta` 中给出 `io.modelcontextprotocol/serverInfo`（[Basic §_meta](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#meta)）。V4 的 `meta` 位于 `structuredContent` 内部，是应用层元数据，不占用 MCP 的 `_meta`；V4 也不读取 `traceparent`（MCP-12）。
>
> **Microsoft 基线**：[Azure §Distributed Tracing & Telemetry](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#distributed-tracing--telemetry) 希望支持 W3C 追踪上下文。`requestId` 只是本地关联 ID（MCP-12）。

## 4. outcome、execution 与 completeness

### 4.1 outcome（闭集）

| outcome | ok | error | 含义 | CLI |
|---|---|---|---|---|
| `succeeded` | true | null | 已完成；预览只表示计划已生成 | 0（见第 14 节的完整度覆盖规则） |
| `rejected-before-operation` | false | 任意错误码，但不能是 `PARTIAL_FAILURE` 或 `OUTCOME_UNKNOWN` | 准入、前提或参数拒绝，领域动作尚未开始 | 2 |
| `read-failed` | false | 同上 | 只读执行失败；**MUST NOT** 当作空结果 | 3 |
| `failed` | false | 同上 | 已执行的动作确定失败，没有成功项，后置状态已确认 | 3 |
| `partial` | false | `PARTIAL_FAILURE` | 有确定的成功项，也有失败或未执行项（或已知部分副作用），没有未知写入 | 4 |
| `unknown` | false | `OUTCOME_UNKNOWN` | 已发出的写入、传输或通道操作无法确认结果 | 5 |

聚合优先级：`unknown` > `partial` > `failed`。副作用以证据判断，**MUST NOT** 根据异常文本推测。

### 4.2 execution（闭集）

`not-started`（领域动作未开始）、`read-only`（只读完成或失败；预览也属于此类）、`completed`（写入已执行完毕，无论成败）、`partial`、`unknown`。

### 4.3 completeness（闭集）

`complete`、`partial`、`none`、`unknown`，描述的是观察到的数据是否完整，与 paging 的“这一页之后是否还有”是两回事（但现状有混用，见第 7.5 节）。生产者规则（现状）：

- 拒绝时为 `none`；`unknown` 时为 `unknown`；`read-failed` 一般为 `none`，部分路径为 `unknown`（快照中有 2 例 `read-failed` + `unknown`）。
- 成功但字段受版本限制、截断（`truncated`）、`dataComplete=false`、`unavailableAttributes` 非空等情况时为 `partial`。此时宿主会自动加上 `INCOMPLETE_DATA` 警告（`FoundationV4Result.EnvelopeFor`、`PlcToolContract.Result` 等）。
- Foundation 的 `InitializeEnvironment` 和 `RunCapabilitySelfTest` 固定返回 `partial`。

### 4.4 允许的组合

| outcome | execution | completeness（观察到的取值） | 快照中出现次数 |
|---|---|---|---|
| succeeded | read-only | complete / partial | 76 / 4 |
| succeeded | completed | complete / partial | 0（离线夹具没有原生写入） |
| rejected-before-operation | not-started | none | 4177（INVALID_ARGUMENT 4013、RESOURCE_UNAVAILABLE 64、PROJECT_NOT_BOUND 58、UNSUPPORTED_CAPABILITY 22、CONFIRMATION_REQUIRED 16、PRECONDITION_FAILED 2、NOT_FOUND 2） |
| read-failed | read-only | none / unknown | 4 / 2（都是 NATIVE_OPERATION_FAILED） |
| failed | completed | none / partial / complete | 0 |
| partial | partial | partial | 0 |
| unknown | unknown | unknown | 0 |

规则：`succeeded` 的 execution **MUST** 是 `read-only` 或 `completed`，其余 outcome 各对应唯一的 execution（`V4Validation.Meta`）。

### 4.5 消费者规则

- 消费者 **MUST** 以 `meta.outcome` 为准。`data` 中残留的 `ok`、`success`、`operationSuccess`、`status`、`error` 等旧字段 **MUST NOT** 用来推翻 outcome（API-29）。
- 消费者 **MUST NOT** 把 `completeness: partial` 的成功结果当作完整结果。
- `rejected-before-operation` 表示什么都没有执行。修正参数或前提后，消费者 **MAY** 重新调用。
- `unknown` 时消费者 **MUST NOT** 自动重放，后续步骤见第 10 节。
- 警告不会把失败变成成功；消费者 **MUST NOT** 根据警告推断 outcome。

### 4.6 现状说明

- Foundation 判断写入的依据是：工具有 `dryRun` 参数且显式传入 `dryRun: false`，或者是 Connection/Bind/Disconnect 类工具（`FoundationV4Result.IsMutation`）。
- 引擎侧的旧实现映射器根据 `operationSuccess`、`success`、`ok`、`mayHaveChanged`、`verified`、`state` 和原生结果状态推断 outcome（`PlcToolContract.Map` 等）。原生返回 `PartialSuccess` 或 `None` 时一律视为 `unknown`（`NativeResultState.TryUnsuccessful`）。
- `HostFailureKind.InternalError` 映射为 `read-failed`（仅限只读路径）。

### 4.7 MCP / Microsoft 对照

> **MCP 基线**：MCP 只有布尔的 `isError`。V4 对除 `succeeded` 以外的所有 outcome 都置 `isError: true`，这与 [CallToolResult.isError](https://modelcontextprotocol.io/specification/2026-07-28/schema#calltoolresult) 的含义一致（“调用以错误结束”）。但 `partial` 和 `unknown` 的结果仍带有有效数据和证据，消费者 **MUST** 读取 `structuredContent`，不能只看 `isError` 就丢弃结果。MCP 没有“结果未知”的概念，这一层由 V4 补充（MCP-04）。
>
> **Microsoft 基线**：没有对应的状态码分层。可以粗略对照为：`rejected-before-operation` ≈ 4xx（400/404/409/412）；`read-failed`/`failed` ≈ 4xx 或 5xx；`partial` ≈ 批量 207 风格；`unknown` 在 REST 中没有对应，最接近的是超时后状态不明，由 [Azure §Exactly Once Behavior](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#exactly-once-behavior--client-retries--service-idempotency) 处理的情形。V4 以“禁止重放 + 会话锁定”代替幂等重试（API-09）。

### 4.8 注解与输出 schema

全部已注册 V4 工具 MUST 声明同一份 EnvelopeSchema 输出 schema。error 按 code 选择定型 details，warning 和 paging 也有结构；data 的逐工具定型在 4.x 逐步进行。meta 和 details 允许增量字段。生产者仍由 V4Validation 做闭集检查，消费者可用 V4Json.DeserializeConsumer 保留未知诊断码和字段。

工具注解 MUST 来自实际发布的描述符以及 ToolMetadata，不从同名的另一实现拷贝效果。四个 hint MUST 全部显式输出，以最有副作用的 action/参数组合为准。READ、OFFLINE 的普通读取为 readOnly；WRITE、ONLINE-WRITE、FILE 为非 readOnly。ONLINE 的连接和断开会改变 TIA 状态，必须使用逐工具覆盖；SESSION、EXECUTE、UI 使用逐工具提示。带 dryRun 参数的工具 readOnlyHint MUST 为 false。删除、覆盖、属性替换和目录导出覆盖由具体工具/动作决定 destructiveHint，Ensure 不能代表非破坏性。

宿主收到 progressToken 后，每 15 秒发送一次进度心跳，覆盖排队、审批等待和同步长操作。没有 token 时不发送。通知不代表成功或可取消的原生操作，派发后仍等待真实结果；通知失败不能改变原生请求。worker 进度与宿主心跳共用递增序号。

## 5. error

### 5.1 错误对象

- `error` **MUST** 恰好包含 `code`、`message`、`details` 三个字段。生产者拒绝未知错误码；跨版本消费者 MUST 容忍未知字段和诊断码，并按 meta.outcome 处理失败。`CONFIRMATION_REQUIRED` 的 details **MUST** 显式包含 `reason`、`planHash`、`requestId` 三个键。
- `message` **MUST** 是非空英文文本，供人和模型阅读。它不是机器契约，消费者 **SHOULD NOT** 解析它。
- 参数错误（`INVALID_ARGUMENT`、`LIMIT_EXCEEDED` 等带 `parameter` 的错误）**MUST** 在 `details.parameter` 中指出出错的参数，`message` 文本保持稳定，不因填写参数而改变（维护者 2026-10-11 决定）。`parameter` 表示第一个字段或 JSON 路径；`parameters` 数组列出全部出错字段，始终输出。填充字段 MUST NOT 改变 message 文本。整体或重复字段错误可指向 arguments。
- `details` **MUST** 是该错误码对应的定型对象（`ErrorDetails` 封闭联合，`V4Json.ErrorConverter`）。details 中的文本字段为 `string|null`，列表为 `string[]`（无值时为 `[]`）。
- details 和 data **MUST NOT** 包含堆栈或凭据。现状的清理手段：`Clean()` 删除键名含 `stack` 或等于 `password` 的字段，并把含 `"\n   "` 的多行字符串替换为 `"Diagnostic details retained in the server log."`；`HostBehavior.SafeDiagnostic` 把盘符或 UNC 路径替换为 `<path>`。

### 5.2 错误码

“构造点”指 `src` 下 `new <Code>Details(` 的出现次数（2026-10-11），用来大致反映使用面。

| code | 何时使用 | details | outcome 约束 | 构造点 |
|---|---|---|---|---:|
| `INVALID_ARGUMENT` | 类型、枚举、未知或重复字段、大小写重复、二次 JSON 编码、缺少 apply 参数；也被用作旧实现失败的兜底（API-14） | `parameter: string`、`parameters: string[]`、`allowedValues: string[]` | 通常为 rejected | 45 |
| `LIMIT_EXCEEDED` | 数量、长度、深度、字节预算超限（如批次超过 50 项、worker 请求帧超过上限） | `parameter`、`limit: int64\|null ≥0`、`actual: int64\|null ≥0` | rejected | 24 |
| `UNSUPPORTED_CAPABILITY` | 本发布版本、action 或对象不支持；UI 工具出现在批次中 | `releaseKey`（发布键或 null）、`capability`、`action` | rejected | 48 |
| `TOOL_NOT_FOUND` | CallTool、GetToolUsage 或 worker 的目标不在本版本的完整目录中 | `tool` | rejected | 4 |
| `PROJECT_NOT_BOUND` | 没有绑定工程 | `{}` | rejected | 30 |
| `NOT_FOUND` | 对象、文件、句柄或批次令牌不存在 | `target` | rejected | 44 |
| `TARGET_AMBIGUOUS` | 目标不唯一（PLC 路径、目录项、工作台目标） | `target`、`candidates: string[]` | rejected | 3 |
| `IDENTITY_MISMATCH` | PID 与启动时间、工程或绑定纪元不一致 | `target`、`expected`、`actual`（脱敏） | rejected | 22 |
| `ALREADY_EXISTS` | 拒绝覆盖 | `target` | rejected | 13 |
| `CONFIRMATION_REQUIRED` | 工作台审批被拒绝、超时或不可用；D1 计划确认 | `reason ∈ {denied, timeout, workbench-unavailable, plan-confirmation}`、`planHash: sha256hex\|null`、`requestId: string\|null`；除 `plan-confirmation` 外，后两者 **MUST** 不为 null | rejected | 17 |
| `PLAN_STALE` | 计划、输入文件、库存、身份或令牌已失效（API-22） | `planHash: sha256hex\|null`、`reason` | rejected | 29 |
| `PRECONDITION_FAILED` | 借用、脏工程、所有权、未连接、生成运行中、批次身份变化等 | `condition`、`target`、`parameter`（null 时显式输出） | rejected（批次聚合中也出现于 failed/read-failed，见 API-37） | 92 |
| `OFFLINE_REQUIRED` | 目标或受影响的设备需要离线 | `targets: string[]` | rejected | 3 |
| `AUTHENTICATION_REQUIRED` | 缺少必要认证（编译候选） | `capability`（不回显凭据） | rejected | 1 |
| `ACCESS_DENIED` | 权限拒绝 | `operation`、`target` | rejected | 4 |
| `SESSION_RESET_REQUIRED` | 先前写入结果未知，会话已锁 | `reason` | **MUST** rejected，且 `requiresSessionReset` 为 true | 33 |
| `RESOURCE_UNAVAILABLE` | SDK、Openness 环境、可选服务、工作台或 bundle 资源缺失 | `resource` | rejected | 8 |
| `IO_FAILED` | 本地文件读写失败（动作开始前） | `operation`、`path`（脱敏） | rejected | 17 |
| `NATIVE_OPERATION_FAILED` | 已知的原生失败；只写文件的工具收到原生明确失败状态；旧实现 success=false 的兜底 | `nativeCode`、`nativeMessage`、`evidence: map` | read-failed / failed / rejected | 29 |
| `COMPILE_ERRORS` | 编译完成但有错误（原生路径未提供时，details 路径标为 <unavailable>，原始证据仍在 data） | `errorCount ≥0`、`warningCount ≥0`、`errors: [{path, description}]`（至多 10 条叶子错误） | failed | 1 |
| `CANCELLED` | 派发前取消（`tool-queue`）、审批预检取消（`approval-precheck`）、审批等待取消（`approval-wait`）；已派发只读取消是 read-failed/read-only | `stage` | rejected 或 read-failed | 7 |
| `TIMEOUT` | 宿主可证明没有派发的超时；worker 通道超时使代次故障，一律是 OUTCOME_UNKNOWN，禁止重放 | `stage` | rejected 或 read-failed | 1 |
| `NOT_EXECUTED` | 批次中前面的项停止后，本项未执行 | `causeIndex: int32\|null ≥0` | **MUST** rejected | 8 |
| `PARTIAL_FAILURE` | 有已知成功项，也有失败或未执行项 | `succeeded`、`failed`、`notExecuted`（≥0；要么全为 0，要么 succeeded>0 且另两项至少一项 >0） | **MUST** partial | 30 |
| `OUTCOME_UNKNOWN` | 写入、传输或通道中断，结果未知；**禁止**自动重放 | `stage`、`evidence: map`、可选 `reason`（只能是 `awaiting-openness-confirmation`，null 时显式输出） | **MUST** unknown | 40 |
| `INTERNAL_ERROR` | 非原生、未分类的故障；堆栈只写日志 | `diagnosticId` | read-failed（也用于“未满足完成条件”，API-14） | 8 |

真实示例：

```json
{"code": "CONFIRMATION_REQUIRED",
 "details": {"planHash": "2a6ad5b0410c777ca9086060bc66e0409438ec058d3e803428068a7c338ecde6",
             "reason": "workbench-unavailable", "requestId": "<string:requestId>"},
 "message": "Workbench confirmation is required before this write."}
```

（V20，`AttachDeviceNodeToSubnet`；`details.requestId` 与 `meta.requestId` 相同。）

### 5.3 details 中字符串字段的词表

`condition`、`stage`、`reason`、`resource`、`operation` 是开放字符串，生产者统一为 kebab-case；未知值必须保留并容忍（API-23）。例如：

- `PRECONDITION_FAILED.condition`：`batch-identity`、`native-admission`、`generation-run-active`、`connect-portal`、`hmi-operation-prerequisites`、`save-explicitly-before-close`。
- `OUTCOME_UNKNOWN.stage`：`worker`、`tool-call`、`native-result-state`、`plc-operation`、`session-operation`、`option-package-operation` 与 `optional-package-operation`。
- `SESSION_RESET_REQUIRED.reason`：`previous-outcome-unknown`、`plc-import-unknown`、`hmi-read-session-is-blocked`。

消费者 **SHOULD** 只把这些值当作诊断信息，不据此分支。生产者 **MUST** 使用 kebab-case 短语。

> **MCP 基线**：MCP 不定义工具错误的结构，只要求错误放在结果中、提供可执行的反馈，让模型能够自我纠正（[Tools §Error Handling](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#error-handling)）。V4 的错误码和 `RECOVERY_GUIDANCE` 满足这一要求，而且超出了它。JSON-RPC 层没有使用自定义错误码，也就没有占用 [Basic §Error Codes](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#error-codes) 中保留的 -32020…-32099 区段。
>
> **Microsoft 基线**：[Azure §Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors) 和 [7.10.2 Error condition responses](https://github.com/microsoft/api-guidelines/blob/vNext/graph/Guidelines-deprecated.md#7102-error-condition-responses) 定义的结构是 `error{code, message, target?, details[]?, innererror?}`，示例中的错误码用 PascalCase，可恢复的错误要有不同的错误码，顶层错误码属于契约（[Considerations §Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#errors)），新错误码应放进 innererror 或作为可扩展枚举处理。V4 的定型 `details` 相当于 innererror 加 target，结构上有意不同（API-01）；错误码使用 UPPER_SNAKE（API-02）；没有逐字段的 `details[]`（API-04）；错误码闭集且读取端严格（API-03）；部分路径误用错误码（API-14）。

## 6. 警告

`meta.warnings` 的元素为 `{code, message, details: map}`，code 是闭集。警告 **MUST NOT** 把失败变成成功。

| code | 何时出现 | details | 现状 |
|---|---|---|---|
| `INCOMPLETE_DATA` | completeness 为 partial | `{}` | 18 处；快照 4 次 |
| `NATIVE_WARNING` | 原生结果为 Warning 状态，或旧数据带有 `warnings` | `{nativeState, nativeStateType}` 或 `{}` | 4 处 |
| `CANDIDATE_ONLY` | 离线构造器生成的候选内容，尚未经过导入验证 | `{}` | 快照 22 次 |
| `UNVERIFIED_BEHAVIOR` | `behaviorPolicy == current` | `{}` | 快照 2645 次 |
| `CLEANUP_FAILED` | 编译候选清理 Safety 登录失败 | 候选定义 | 仅 safe-v4 候选路径 |
| `NATIVE_CAPABILITY_LIMIT` | 原生 SDK 能力受限 | 无 | 已定义，**从未发出**（API-36） |
| `DIAGNOSTIC_WRITE_FAILED` | CLI 报告文件写不出 | 报告定义 | 仅 CLI |
| `APPROVAL_DISABLED` | 审批关闭时执行的 MCP 写调用 | `{enabled: false}` | 通过重新构造并校验信封追加，每个信封至多 1 条 |
| `RECOVERY_GUIDANCE` | 错误码为 INVALID_ARGUMENT、PRECONDITION_FAILED、SESSION_RESET_REQUIRED 或 CONFIRMATION_REQUIRED 时 | `{getToolUsage{toolName, operation?}, releaseKey, exampleArguments\|null, exampleOmitted, validation, bindingMeaning}` | 通过重新构造并校验信封追加，每个信封至多 1 条；示例参数不超过 4096 字节 |
| `BACKUP_SKIPPED` | 覆盖导入前的备份被跳过 | `{objects, recoveryDirectory}` | 1 处 |
| `APPROVAL_PRECHECK_REFUSED` | 写入被自身预览拒绝，没有发起审批请求 | `{stage: "approval-precheck"}` | 通过重新构造并校验信封追加 |
| `STAGING_FOLDER_RETAINED` | 暂存目录被保留 | 暂存定义 | 1 处 |

`RECOVERY_GUIDANCE` 中的示例参数取自嵌入的示例库，**MUST NOT** 包含调用方的参数。这条警告不授权、不重试、也不执行任何写入（`docs/reference/recovery-guidance.md`）。它的 message 以装饰性字符 `▶ RECOVERY:` 开头。

> **MCP 基线**：MCP 没有警告概念，只要求错误结果可执行，V4 的警告是附加层。
>
> **Microsoft 基线**：Azure 只有 `azure-deprecating` 头这种响应级提示，没有响应体中的警告（API-17，有意保留）。

## 7. 分页

### 7.1 `meta.paging`

```text
{mode: "offset"|"cursor", unit: "items"|"utf16", offset: int32|null, limit: int32, nextOffset: int32|null,
 cursor: string|null, nextCursor: string|null, total: int32|null, complete: bool}
```

不分页时为 null。不变量（`V4Validation.Paging`）：

- `limit > 0`；`total` 为 null 或 ≥0。
- offset 模式：`offset ≥ 0`；`cursor` 和 `nextCursor` 为 null；`complete == true` 时 `nextOffset` 为 null，否则 `nextOffset > offset`；`offset ≤ total` 且 `nextOffset < total`。
- cursor 模式：`offset` 和 `nextOffset` 为 null；`complete == true` 时 `nextCursor` 为 null，否则 `nextCursor` 非空且不等于 `cursor`。
- 未读完的页 **MUST** 给出能向前推进的 next 值。

示例（V20，`FindTools`，`{"limit":1,"query":"ManageMotionAxis"}`）：

```json
"paging": {"complete": true, "cursor": null, "limit": 1, "mode": "offset",
           "nextCursor": null, "nextOffset": null, "offset": 0, "total": 1}
```

### 7.2 生产者与参数（现状）

| 方式 | 参数（八版并集中出现该参数的工具数） | 生产者 |
|---|---|---|
| offset 项分页 | `offset`（57）+ `limit`（63；默认值有 12/20/30/50/80/100/200/500 等） | FindTools、GetToolUsage、GetOpennessGuidance；旧实现映射器从 data 或 evidence 中的 `offset`/`limit`/`total` 推导 |
| cursor 项分页 | `cursor` + `limit`，另有释放游标的 `ReleaseUnifiedReadCursor` | Unified HMI 读取（`HmiInspectionContract`，需要 `collectionId` 证据） |
| offset 文本分页 | `offset` + `limit`（GetExportContent） | 导出句柄（第 8 节） |
| 上限而非分页 | `maxItems`（9）、`maxResults`（1）、`top`（4，其中部分是 HMI 坐标而非分页） | 批量导出与导入、库查询 |

`CursorScope{releaseKey, sessionId, bindingEpoch, queryHash, snapshotId}` 已经定义，但源码中没有任何地方构造它；Unified 游标使用旧实现自己的作用域检查（API-36）。

### 7.3 规则

- 游标 **MUST** 不透明，并限定在同一发布版本、会话、绑定、查询和快照内；**MUST NOT** 跨工程复用。
- `complete` 表示该快照已读完。它 **MUST NOT** 用来代替工程结果的完整性。
- 消费者 **MUST** 用 `nextOffset` 或 `nextCursor` 请求下一页，**MUST NOT** 自行做 `offset + limit` 运算，因为字符分页在遇到 UTF-16 代理对时可能少返回一个字符。
- 旧实现映射出来的页，`data` 中往往还留着 `offset`、`total`、`dataComplete` 等字段，与 `meta.paging` 重复，以 meta 为准。

### 7.4 字符分页

GetExportContent 和大响应寄存以 UTF-16 码元为单位（`string.Length`），每片最多 20000 个码元，切片不会拆开代理对（`ExportStore.Slice`）。`meta.paging.limit` 回显应用默认值和上限后的正数请求长度；`data.returned` 报告实际返回的码元数。空末页的 returned 为 0，complete 为 true，nextOffset 为 null，limit 仍为请求长度。代理对边界可能缩短一页，消费者用实际 nextOffset 继续读取（API-15、API-31）。

### 7.5 paging 与 completeness 的关系（现状不一致）

FindTools 的中间页报 `completeness: complete`。GetOpennessGuidance 的中间页（`offset 0, limit 20, total 282`）则报 `completeness: partial` 并加上 `INCOMPLETE_DATA`，原因是旧字段 `dataComplete=false`。规则尚未统一（API-31）。

> **MCP 基线**：MCP 的分页（[Pagination](https://modelcontextprotocol.io/specification/2026-07-28/server/utilities/pagination)）只适用于 `tools/list` 等列表方法：游标不透明，页大小由服务器决定，缺少 `nextCursor` 表示结束，空串也是有效游标，无效游标返回 -32602。V4 工具结果的分页属于应用层，MCP 不作规定。V4 的 cursor 模式与 MCP 的语义一致；offset 模式让客户端自己选页大小、暴露偏移量运算，最后一页的 next 字段输出 null 而不是省略（MCP-08，有意保留 offset，因为它对模型更直观）。V4 的 `tools/list` 一次返回全部工具（最多 514 个），这是允许的。
>
> **Microsoft 基线**：[Azure §Collections](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#collections) 用 `value` 数组加不透明的 `nextLink`（最后一页省略），可选参数为 [`top`](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#top)、[`skip`](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#skip)、[`maxpagesize`](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#maxpagesize)，不使用快照时要说明页之间可能有遗漏或重复。V4 的形状不同（API-06，保留）；参数名不统一（API-07）；offset 模式缺少一致性说明（API-08）；字符分页的单位未声明，对应 [Returning String Offsets & Lengths](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#returning-string-offsets--lengths-substrings)（API-15）。

### 7.4 一致性、单位与本页完整度

offset 分页 MUST 明确不提供快照一致性：活动工程在页间变化时，条目可能重复或遗漏，total 也可能变化。需要稳定观察时使用提供快照的 cursor 工具，或先导出再分页。cursor 只能在所属 V4 会话、绑定纪元及查询快照内使用。

meta.completeness 描述本页是否完整交付；meta.paging.complete 表示是否已到末页。有后续页本身不意味着本页 partial。条目页用 unit=items；导出文本切片用 unit=utf16，offset 与 limit 为 UTF-16 码元，byteLength 始终是 UTF-8 字节数。默认预算按工具公布，不强制统一；页预算参数统一名为 limit。

## 8. 大响应与导出句柄

### 8.1 寄存规则（`ResponseGuardTool.Shrink`）

- 阈值为 20000 个字符，可用 `TIA_MCP_MAX_RESPONSE_CHARS` 覆盖。0 或负数表示不限；值写错时使用默认值。
- 只处理“恰好一个 TextContent”的结果。错误结果（`isError`）**MUST NOT** 被寄存。导出工具本身、CallTool 转发到导出工具的调用、已经寄存过的结果都不再处理。寄存失败时原样返回完整结果（fail-open）。
- V4 信封被寄存后，`data` 换成 `{text: <信封 JSON 文本的前 N 个码元>, returned: <实际码元数>, export: ExportHandle}`，`meta.paging` 为字符 offset 页，原来的 `meta` 和 `error` 保留。`ExportHandle = {id, mediaType: "application/json", byteLength(UTF-8), sha256(64 位小写十六进制), expiresUtc}`。
- 消费者 **MUST** 先检查 `data.export` 是否存在。存在时 `data` 已不是工具原来的结果形状，**MUST** 用 GetExportContent 读完全部内容，拼接后再解析。

### 8.2 生命周期工具

| 工具 | 操作类别 | 规则 |
|---|---|---|
| `GetExportContent(exportId, offset=0, length=0)` | READ | 返回 `data{text, export}`；每片最多 20000 字符；句柄不存在、过期或被淘汰时返回 `NOT_FOUND` |
| `ListExportHandles(tool?, limit=20)` | READ | 截断时返回 completeness partial，而不是 paging |
| `SaveExportContent(exportId, outputPath, raw=false, overwrite=false)` | FILE | 写入带 BOM 的 UTF-8。目标已存在时返回 `ALREADY_EXISTS`。写入开始后发生 IO 失败返回 `OUTCOME_UNKNOWN`（stage `file-write`）。指向审批设置等管理目标的路径直接以 `CONFIRMATION_REQUIRED/denied` 拒绝 |
| `DeleteExportHandle(exportId)` | WRITE | 不存在时返回 `NOT_FOUND` |
| `ClearExportHandles(olderThanHours=24)` | WRITE | 0 表示全部清除 |

存储限制：句柄只能在签发它的会话中读取和清理（`SessionExportStore`），有效期 24 小时；存储在宿主进程内共用，最多 32 条、总计 8,000,000 个字符，按 LRU 淘汰（`ExportStore`）。导出句柄的数据表示现状不一致：寄存时为 `content` + `application/json`，GetExportContent 返回 `text` + `text/plain` 并多出 `tool/target/createdUtc/totalLength` 字段（API-30）。

V20/V21 worker 通道的单帧上限为 1 MiB：超限的请求在派发前以 `LIMIT_EXCEEDED` 拒绝，超大的回复保存为导出句柄（`docs/releases/v4.0.0.md`）。

> **MCP 基线**：MCP 原生处理大内容的方式是 `resource_link` 或嵌入资源（[Tools §Resource Links](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#resource-links)），V4 没有使用（MCP-09）。2026-07-28 的 [Stateful Tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#stateful-tools) 建议用服务器签发的不透明句柄、在描述中说明有效期、过期时返回工具执行错误。V4 的导出句柄满足这三点，但作用域绑定在 MCP 会话上（MCP-01）。
>
> **Microsoft 基线**：没有直接对应的规则；[Bring your own Storage](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#bring-your-own-storage-byos) 是类似的思路。

## 9. 写操作：预览、确认、审批与计划哈希

### 9.1 写入分类

工具描述以 `[层][域][操作]` 开头。操作类别为 UI、SESSION、READ、WRITE、FILE、OFFLINE、ONLINE、ONLINE-WRITE 和 EXECUTE；逐版数量由[工具矩阵](../../reference/tool-matrix.md)生成。显式分类登记在 `ToolMetadata` 或 `[ToolClassification]` 中。原先按名称推断的 197 个工具已按维护者 2026-10-11 的逐项复核结果登记显式分类与注解，inferred 标志清除。注解从实际服务的 descriptor 生成，包含四个明确的布尔 hint；同名 Engine/Foundation 工具按各自行为处理（MCP-02）。

是否算作需要审批的写调用，由 `HostBehavior.ApprovalWrite` 判断：

- safe-v4 候选：`mode == "apply"` 时算写调用。
- 有 `dryRun` 参数的工具：显式传入 `dryRun: true`，或者省略 `dryRun` 且默认是预览时，不算写调用。
- 混合文件工具 `ExchangeSystemDiagnosticsSettings`、`ExchangeUnifiedTags`、`ExchangeUnifiedScriptModules` 仅 action=import 触发审批；export/read 保持不触发。`GetEnvironmentDiagnostics` 仅 fix=true 触发；默认 fix=false。`EnsureOpennessUserGroup` 始终触发。
- 其余情况：分类为 WRITE 或 ONLINE-WRITE 的工具，以及 `SaveProject`、`SaveProjectCopy`、`CloseProject`。`ImportDeviceAml` 为 WRITE，两个测试执行工具 `RunPlcSimAdvancedTestScenario`、`RunTestSuiteCase` 为 ONLINE-WRITE（维护者 2026-10-11，P8-60 第二轮）。FILE 分类本身不触发审批；`StageImportFiles` 和 `CleanupStagedImportFiles` 沿用显式文件操作审批。

### 9.2 现行（current 政策）的预览与确认参数

| 参数 | 语义 |
|---|---|
| `dryRun: boolean = true` | 默认预览。生产者 **MUST** 保证预览不创建、不导入、不编译、不保存、不重试写入 |
| `confirm`、`confirmDelete`、`confirmChange`、`confirmWrite` 等（含 `allowWrite`） | 既有安全确认开关，需要与 `dryRun: false` 同时使用 |
| `expectedPlanHash` | 预览给出的 bare SHA-256 计划哈希；不匹配时拒绝 |
| `expectedProjectFile` | 当前绑定的工程文件，包括 ApplyGenerationPlan 的项目身份输入 |
| `expectedProject` | 保留给 Unified 和批次工具中的工程**名称** |
| `previewToken` | PreviewToolBatch 等工具签发的不透明预览令牌，不按计划哈希解析 |
| `expectedDocumentHash`、`expectedPackageHash` | 文档或包的预期哈希；与 expectedPlanHash 分开 |
| `expectedInventoryHash` | 既有库存身份输入 |

规则：预览（`dryRun: true` 或 `mode: preview`）**MUST NOT** 等待审批，也不写审计中的写记录。生产者 **MUST** 在原生调用前完成所有 apply 参数检查。批量目录导入的 apply 缺少 `expectedProjectFile`、`expectedPlanHash`、`confirm` 或 `importOrder` 时，返回 `INVALID_ARGUMENT`（`HostBehavior.BatchApplyRefusal`）。

### 9.3 D1 safe-v4 候选协议（4.0 发布配置中未广告）

候选路径（`src/Logic/V4/*Candidate.cs`、`PlcImport.cs`、`PlcExport.cs`、`DeviceCreation.cs`）使用 `mode = preview|apply`（默认 preview）、`confirm`、`expectedPlanHash` 和 `expectedProjectFile`。预览返回 `data.plan`：

```text
Plan = {hash, releaseKey, tool, argumentsHash, identity, inputHashes, inventoryHash, operations, warnings}
PlanIdentity = {processId, processStartUtc, projectFile, bindingEpoch, workspaceRoot, files: PlanFile[]}
PlanFile = {path, exists, byteLength|null, sha256|null}；PlanOperation = {tool, target, arguments: object}
```

规则：

- 各个哈希 **MUST** 是 64 位小写十六进制的 SHA-256。工程身份 **MUST** 完整包含 PID、启动时间和绑定纪元；离线身份只填 `workspaceRoot`，两者互斥。
- apply 前重新读取身份和库存，不一致时返回 `PLAN_STALE` 或 `IDENTITY_MISMATCH`。一个计划只允许一次执行尝试（例如设备创建每个会话最多保存 128 个计划；计划已被消费时返回 `PLAN_STALE`，reason 为 `missing-or-consumed-plan`）。
- `PreviewData` 类型和 `V4Validation.Preview`（要求预览是 read-only 成功，且 tool 与发布键一致）已经定义，但没有代码构造 `PreviewData`。所以 4.0 实际发布的预览返回的是旧实现的数据形状（例如 `data.planHash`），而不是 `data.plan`（API-39）。

### 9.4 工作台审批流程

引擎在 `McpServer.Approval.cs` 中实现，Foundation 共用 `HostBehavior`、`ApprovalPipe` 和 `ApprovalProtocol`。审批默认开启（`data/config/approval.settings`）。流程：

1. **预检**（`PrecheckBeforeApproval`，仅针对 MCP 写调用）：
   - 生成写栅栏：本会话有活动的生成运行时返回 `PRECONDITION_FAILED`（`generation-run-active`，`data.runId`）。
   - 会话锁：返回 `SESSION_RESET_REQUIRED`（`previous-outcome-unknown`）。
   - 审批开启时检查调用方输入文件。
   - 批量 apply 参数检查。
   - 用同样的参数在只读作用域内跑一次预览（`dryRun: true` 或 `mode: preview`）。预览阻止 apply 时返回 `PRECONDITION_FAILED`；预览失败时，把预览结果标记为 `rejected-before-operation` 并加上 `APPROVAL_PRECHECK_REFUSED` 后返回；预览报告“无效果”时直接返回预览结果，不再审批。
2. **等待审批**（`WaitForApproval`）：创建 `PendingApproval{requestId, planHash（有计划时用计划哈希，否则用参数摘要）, argumentDigest, timeout}`，通过本机命名管道 `TiaMcp.Approval.v1.<hash>` 交给工作台。得到 granted、denied、timeout 或 workbench-unavailable 之一；后三者返回 `CONFIRMATION_REQUIRED`。
3. **派发前复核**（在 Openness 通道内）：再次检查写栅栏和会话锁；`ApprovalStillMatches` 核对参数摘要与绑定身份，不一致时返回 `CONFIRMATION_REQUIRED/denied`；再次检查输入文件。

审批和审计的参数摘要 MUST 在计算 SHA-256 前，按调用日志共用的 `CallJournalPayload` 敏感字段规则把密码、凭据、密钥和令牌替换为固定占位符 `••••`。此规则递归适用于参数对象、数组、名称/值属性表与嵌入 JSON；摘要不使用界面截断后的文本。审批卡 MUST 先脱敏，再生成长文本摘要，避免显示密码的无盐哈希。非敏感参数、发布键、工具和绑定身份仍参与摘要；获批后的复核及生成步骤的单次授权使用相同脱敏形式，实际操作接收原始值。

生成步骤的 `argumentDigest` 和包含步骤参数的计划哈希同样排除敏感值，展示、返回的计划参数使用脱敏副本。编译及回退候选的预览计划原先还绑定密码的会话 HMAC；现在只绑定脱敏参数。因此，仅替换密码可继续使用已经预览、审批的计划；密码不属于人工审阅内容。这不改变工程身份、非敏感操作参数、一次性消费和原有预览检查。批次预览令牌仍引用宿主保存的原始调用，不提供替换密码的输入槽。
4. **执行一次**：开始审计，执行，`ObserveSessionOutcome` 判断是否锁定会话，`FinishApproval` 附加恢复提示、`APPROVAL_DISABLED`，并把 `requestId` 改为审批请求的 ID，最后完成审计。
5. 审批关闭时照常执行，加上 `APPROVAL_DISABLED`；工具自身的 confirm 和计划检查仍然生效。

| `CONFIRMATION_REQUIRED.reason` | 触发条件 |
|---|---|
| `denied` | 工作台拒绝；参数在批准后改变；管理目标写入；**客户端在等待期间取消**（API-27） |
| `timeout` | 审批截止时间已过 |
| `workbench-unavailable` | 工作台未运行或管道不可用 |
| `plan-confirmation` | D1 候选缺少计划确认；planHash 和 requestId 可以为 null |

### 9.5 批次计划（PreviewToolBatch / ApplyToolBatch）

- `PreviewToolBatch(operations[1..50], expectedProject)`：只接受 `BatchWrite: true` 且带布尔 `dryRun` 参数的工具，并强制 `dryRun: true`。准入规则见[第 11 节](#11-批次与桥接)。逐项预览，遇到失败就停止，余下的项标为 `NOT_EXECUTED`。全部成功时签发令牌（两个 GUID 拼成的 64 位十六进制，有效期 10 分钟，只能用一次），返回 `data{token, expiresUtc, operations, items, executed:false, scope}`。
- `ApplyToolBatch(previewToken)`：取出令牌后立即作废（不存在时返回 `NOT_FOUND`）。批次中有 UI 工具时返回 `UNSUPPORTED_CAPABILITY`。复核绑定身份和全部预览，有变化时返回 `PLAN_STALE`（details.previewToken 中放令牌，details.planHash 为 null，见 API-22）。然后按顺序以 `dryRun: false` 执行，遇到失败或未知就停止，余下的项为 `NOT_EXECUTED`。不回滚（`data.rollbackPerformed: false`）。

### 9.6 生成计划（P8-31e）

- `PlanProjectGeneration(packageId, version, machine)`：只读，返回 `tiamcp.plan/1`（`planId`，以及带 `sha256:` 前缀的 `planHash`，见 API-21）。计划属于当前 MCP 会话和绑定。
- `ApplyGenerationPlan(planId, expectedPlanHash, expectedProject, phases?, dryRun=true)`：默认预览。执行时只发起一次工作台计划审批。逐步执行：每步先跑该工具自己的预览，核对已批准的决定字段，只回填 `expectedPlanHash`；遇到拒绝、失败、未知或发散时停止；已完成的写入保留，**不重试**。上限为 4 小时或 10,000 步。运行期间，同一会话中不属于本运行的写调用（包括附着的工作台面板）被写栅栏拒绝；跨会话由 TIA 进程租约保护。设计见 [分阶段生成执行设计](../design/p8-31-staged-generation.md)。
- `GetGenerationRun(runId)`：读取只追加的 `tiamcp.run/1` 记录，宿主重启后仍可读。被中断的步骤记为 unknown，不会重放。运行状态词表包括 `succeeded`、`failed`、`unknown`、`cancelled`、`diverged`。

### 9.7 哈希格式

V4 计划、审批和 PLAN_STALE 中的 `planHash`、`argumentsHash`、`inventoryHash` 以及文件的 `sha256` 都是 64 位小写十六进制；生成计划使用 `sha256:<hex>` 形式，`ApprovalProtocol` 对 `kind == plan` 两种都接受（API-21）。

> **MCP 基线**：MCP 要求有人能在环中拒绝工具调用，SHOULD 为敏感操作提供确认（[Tools §User Interaction Model](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#user-interaction-model)、[§Security Considerations](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#security-considerations)）。V4 在服务器侧用工作台审批实现人在环中，比规范要求更强。MCP 原生的确认途径是客户端确认，以及 2026-07-28 的 MRTR/elicitation（[MRTR](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr)）；V4 有意使用带外的可信工作台（MCP-13）。审批等待会阻塞 `tools/call`；MCP 的 [tasks 扩展](https://modelcontextprotocol.io/extensions/tasks/overview) 明确把人工审批列为适用场景（MCP-05）。工具注解由 ToolMetadata 和实际发布的描述符生成，采用最有副作用的动作/参数组合；dryRun 的默认预览仍由输入 schema 和描述表达（MCP-02）。
>
> **Microsoft 基线**：`expected*` 参数对应 [Azure §Conditional Requests](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#conditional-requests) 中的 `If-Match`/412，`overwrite: false` 对应 `If-None-Match: *`；V4 保留了这个概念，但没有对读取统一返回 ETag（API-10），而且参数命名不一（API-18）。[Performing an Action](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#performing-an-action) 把动作建模为 POST，对应 V4 的 apply 调用。Microsoft 指南没有关于预览（dry-run）的规定，因此不计偏差。

## 10. 未知结果、会话重置与禁止重放

### 10.1 何时产生 unknown

- 写入或文件操作发出后抛出异常（`HostFailurePolicy.Classify`：已派发且非只读，或 worker 报告 `workerOutcomeUnknown`）。
- 原生结果状态为 `PartialSuccess`、`None`、不在预期内的状态，或失败状态且 `mayHaveChanged`（`NativeResultState`）。
- 写后核对失败（`verified=false`、`verifiedAbsent=false`、`state=Unknown`）。
- worker 退出、挂起、帧格式错误或 TIA 进程丢失时，整个 worker 代次被标为故障，包括只读调用（API-26）。
- 批次子项已发出但结果无法读取（`row.outcomeUnknown = true`）。

### 10.2 会话锁

- unknown 信封的 requiresSessionReset 与实际的 Openness 准入锁一致；没有锁也不得重放。
- 只有同时满足以下条件时才真正锁定会话（`SessionBehavior.LocksSession`）：本次调用发出过原生调用（调用日志中有原生边界，或 worker 证据为 `nativeOutcomeUnknown`/`nativeIssued`），属于写入或 FILE/EXECUTE 类别，并且 outcome 为 unknown。
- 锁定后，使用 Openness 通道的工具（`DisconnectPortal`、`RestartOpennessWorker` 和派发类工具除外）在审批和原生调用之前，被 `SESSION_RESET_REQUIRED`（`previous-outcome-unknown`）拒绝。

### 10.3 恢复流程（MUST）

1. 检查 TIA 的实际状态。
2. 调用 `DisconnectPortal`。空闲的 worker 会脱离但不保存、不关闭；忙碌、超时或丢失的 worker 会被终止，回复会说明需要重启 TIA。
3. 开启新的 MCP 会话（即新的 V4 会话，见第 0.4 节），显式调用 `ConnectPortal` 和 `AttachOpenProject`。
4. **MUST NOT** 重放失败的请求。重新预览也不等于获准再次写入。

### 10.4 租约

所选 TIA 进程由一个 MCP 会话独占租用。派发前租约状态为 BUSY，结果已知后为 IDLE，结果未知后为 UNCERTAIN。存活的所有者会拒绝竞争的附加请求。已退出所有者的 RELEASED 或 IDLE 租约可以被接管（ConnectPortal 返回 `data.previousOwnerEndedIdle=true`）；BUSY、UNCERTAIN 以及旧的 ACTIVE 租约仍然拒绝。**MUST NOT** 删除租约文件来绕过这一保护（`docs/releases/v4.0.0.md`）。

### 10.5 超时与取消

- 八版（V14 SP1–V21）共用 worker 通道超时策略，预算与配置见 [4.0 发布说明](../../releases/v4.0.0.md)：默认 120 秒，worker 代次首次附着 300 秒，编译、上下载、导入导出、归档和库更新 30 分钟；可用 `--worker-timeout-config` 或 `TIA_MCP_WORKER_TIMEOUTS` 覆盖。超时会把 worker 代次标为故障，outcome 为 unknown。
- PLC dispatcher 的批量、候选与移植族调用共用 `WorkerTimeoutPolicy`（P8-64）；预览与读取使用默认预算，首次实际附着按 worker 代次使用首次附着预算，Disconnect 保留有界退出等待。
- 派发前收到取消，返回 `CANCELLED`（`tool-queue`）；审批预检中取消，返回 `CANCELLED`（`approval-precheck`）；派发后宿主忽略取消，等待真实结果。

> **MCP 基线**：[Cancellation](https://modelcontextprotocol.io/specification/2026-07-28/basic/utilities/cancellation#behavior-requirements) 规定，服务器收到取消后 SHOULD 停止处理且 SHOULD NOT 再发送响应，无法取消时 MAY 忽略。V4 在派发后忽略取消，这是允许的；派发前仍会返回一个 `CANCELLED` 结果（MCP-07，保留：CLI、批次和审计需要这个记录）。[Timeouts](https://modelcontextprotocol.io/specification/2026-07-28/basic/utilities/cancellation#timeouts) 允许客户端在收到进度通知时重置超时，V4 对携带 progress token 的调用每 15 秒发送单调递增的 heartbeat，并保留原生批量进度（MCP-06）。2026-07-28 的 [Statelessness](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#statelessness) 要求跨请求的状态通过显式标识引用，服务器不得把连接或进程身份当作会话。V4 的恢复步骤“开启新的 MCP 会话”以及会话锁、租约、计划和句柄的作用域都依赖传输层会话（MCP-01）。
>
> **Microsoft 基线**：[Repeatability of requests](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#repeatability-of-requests) 用 `Repeatability-Request-ID` 让 POST 可以安全重试；[Exactly Once Behavior](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#exactly-once-behavior--client-retries--service-idempotency) 要求操作幂等。TIA Openness 无法去重或回滚，所以 V4 有意采用“至多一次 + 锁定 + 人工核实”（API-09）。

## 11. 批次与桥接

**批次准入**：V14 SP1–V21 的全部八个发布版本 MUST 使用同一份经过审核的 `ToolMetadata` 分类表。同一工具的 `BatchRead` 和 `BatchWrite` 标志在八版一致。只读批次 MUST 要求 `BatchRead: true`；预览批次 MUST 要求 `BatchWrite: true` 且带布尔 `dryRun` 参数。MUST NOT 仅凭 `READ`、`OFFLINE`、`WRITE` 或 `FILE` 类别推断准入。版本可用性、参数校验、UI 和嵌套编排限制仍然适用。

2026-10-20 新开放的 `READ` 工具按纯读取标准审查：不改变绑定或会话状态，不改变工程、库或在线状态，不写文件，且执行成本有界。`READ` 工具仍可能因原生副作用、长耗时、挂起或崩溃风险、会话计划存储或导出内容分页而保持关闭。可能很慢的搜索与反射读取不会仅因标为 `READ` 就获准进入批次。4.0 未扩大 `OFFLINE`、`WRITE` 和 `FILE` 的准入范围。

逐工具标志以 [`ToolMetadata` 表](../../../src/Logic/ModelContextProtocol/ToolMetadata.cs)为准。工具的版本可用性发布在[生成的版本工具目录](../../reference/version-tool-catalog.md)和[机器可读版本清单](../../../manifest/version-tools.json)中；这两份生成文件当前尚未列出 `BatchRead` 和 `BatchWrite`，不能用工具在目录中出现来判断批次准入。

- **CallTool(name, arguments)**：在完整目录中查找工具。名称 MUST 精确匹配并区分大小写，包括桥接、预览和导出寄存排除名单。目标是 CallTool 本身时返回 `INVALID_ARGUMENT`（name）。目标属于其他发布版本时返回 `UNSUPPORTED_CAPABILITY`（capability = 工具名），目标完全未知时返回 `TOOL_NOT_FOUND`（MCP-11）。
- **PreviewToolCall(name, arguments?)**：只做校验，不执行。参数错误时仍然返回 `ok: true`，错误放在 `data.validationError(s)` 中；没有绑定工程时返回 `PROJECT_NOT_BOUND`。
- **RunReadOnlyToolBatch(operations[1..50], expectedProject?)**：只接受 `BatchRead: true` 的工具。`GetSessionState` 通过该标志获准进入只读批次。MUST NOT 嵌套编排，也不接受原生交叉引用查询或 `GetExportContent`。按输入顺序执行，不提供一致的原生快照。
- **批次结果**：`data.items = [{index, target, result: Envelope}]`。index 从 0 开始连续递增，顺序与输入一致；`result` 是嵌套对象，**MUST NOT** 是字符串。另外还有 `data.rollbackPerformed: false`，以及在子结果无法读取时出现的 `row.outcomeUnknown: true`。
- **聚合**（`V4Validation.Batch` + `BatchResult`）：

| 子结果情况 | 父 outcome | 父 error |
|---|---|---|
| 任一子项为 unknown | unknown | `OUTCOME_UNKNOWN`（stage `batch`） |
| 全部成功 | succeeded | null |
| 有 partial，或成功与失败混合 | partial | `PARTIAL_FAILURE(succeeded, failed, notExecuted)`；没有成功项时三个计数都为 0 |
| 全部未开始 | rejected-before-operation | `PRECONDITION_FAILED`（`batch-results`） |
| 其他（没有成功项） | failed / read-failed | `PRECONDITION_FAILED`（`batch-results`，API-37） |

> **MCP 基线**：MCP 没有批次或工具代理的概念；JSON-RPC 批处理在 2025-06-18 中已被移除。CallTool 是 V4 的应用层代理，用来在 lite 配置下访问完整目录。
>
> **Microsoft 基线**：Azure 指南没有批处理规则，不计偏差。

## 12. 版本与发布键

| 层 | 标识 | 现状 |
|---|---|---|
| 发布键 | `14sp1`、`15.1`、`16`、`17`、`18`、`19`、`20`、`21` | 每个宿主进程用 `--release-key <key>` 固定发布键；`meta.releaseKey`、`UnsupportedCapabilityDetails.releaseKey`、`Plan.releaseKey` 和 `CursorScope.releaseKey` 都只接受这个闭集；运行目录为 `runtime/v<key>/` |
| 信封版本 | `schemaVersion: 4` | 固定值，不能协商；读取端只接受 4 |
| 工具迁移标记 | `ToolProfiles.resx` 中的 `envelopeVersion` | 值为 4 时启用 V4 准入；`ToolUsageCatalog.ProfileEntries` 只接受 `contractVersion = 4` |
| 契约快照 | 基线格式 1、响应格式 3 | CI 校验名称、目录和覆盖面；schema 与响应的差异在评审时人工核对 |
| 数据格式 | `tiamcp.plan/1`、`tiamcp.run/1`、`tiamcp.machine/1`、审批管道 `v1`、工作台控制契约 `[1]` | 各自独立的版本号 |
| 产品版本 | SemVer `4.0.0`；`/mcp/health` 返回 `fileVersion` | 4.0 硬切换，不提供 3.x 别名，迁移靠生成的对照表 |
| MCP 协议 | SDK 协商（最高 2025-06-18） | 4.0 保持 2025-06-18；现行修订 2026-07-28 的迁移放在 4.x（第 0.4 节） |

兼容规则：生产者按当前版本闭集校验；消费者容忍未知诊断码和增量字段。全部工具共享输出信封 schema，具体演进政策见第 12.1 节。

> **MCP 基线**：MCP 的版本由协议协商决定（2026-07-28 起每个请求都带版本）。规范层采用功能生命周期政策，弃用窗口至少 12 个月（[Changelog §Governance](https://modelcontextprotocol.io/specification/2026-07-28/changelog)）。V4 的 `schemaVersion` 是应用层版本，与 MCP 版本无关，这样做可以接受。SDK 落后于现行修订（MCP-15）。
>
> **Microsoft 基线**：[Azure §API Versioning](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#api-versioning) 要求每个请求带日期式的 `api-version`、不做破坏性变更、使用[可扩展枚举](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#use-extensible-enums)，并[通知弃用](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#deprecating-behavior-notification)。V4 是进程级版本加一次硬切换（API-11，按 2026-10-03 的决定保留），使用闭集枚举（API-03），没有弃用信号（API-12）。

### 12.1 4.x 演进规则

4.x MUST 只做增量：新工具、新可选参数、新字段和新诊断码。消费者 MUST 容忍未知字段、未知 warning code（可忽略）和未知 error code（按 outcome 处理，并保留证据）。改变已有字段含义、移除字段/工具、改名、改变必填参数等破坏性修改 MUST 升 schemaVersion，并在发布说明和迁移表逐项列出。生产者的 enum 和 V4Validation 仍为闭集。

TIMEOUT 仅用于宿主可证明没有派发的超时。worker 超时属于通道故障，按 OUTCOME_UNKNOWN 处理，故障代次必须恢复，不自动重放。requiresSessionReset MUST 表示实际的 Openness 准入锁，直到恢复前后续原生调用会被拒绝；未知结果没有锁时可为 false，但仍禁止重放。取消的派发前信封仍用于 CLI、批次和审计，这是对 MCP 不再回复建议的有意偏差。

## 13. 命名

### 13.1 工具名

- **MUST** 是 PascalCase 的“动词 + 领域对象 + 必要限定”，只用 ASCII 字母和数字。现状：514 个工具中最长的 45 个字符（`ProbeHardwareHmiConnectionWhitelistedServices`），没有连续大写的缩写，`Plc`、`Hmi`、`OpcUa`、`Db` 都写成单词形式。
- 动词 **MUST** 取自 `build-tools/release/CatalogData.cs` 的闭集，包含经维护者批准的 Prefill 和 Publish。生成器检查所有最终工具名，包括新增入口；`PublishGenerationRecord` 保留名称（API-38，维护者 2026-10-11）。
- 单个对象用单数，批量操作用复数（`ExportPlcBlock` / `ExportPlcBlocks`）。`Manage*` 工具的对象单复数不统一（`ManageDeviceUsers`、`ManagePlcExternalSources`、`ManageDcbLibraries`）。
- 名称 **MUST NOT** 暴露 `Json`、`Xml`、`Xlsx` 等序列化格式；`SimaticMl`、`Aml`、`Scl`、`FlgNet` 是领域语言名称，保留。
- 直接调用、CallTool、PreviewToolCall 和大响应寄存层都 MUST 精确匹配工具名。注册时仍拒绝仅大小写不同的冲突名称。

### 13.2 参数名

- **MUST** 是 camelCase。现状：753 个不同的参数名中，非 camelCase 的只有 `Background`、`Border`、`Page`、`Surface`、`Text`，它们是 Unified 主题 `palette` 中 Siemens 原生的键名。缩写写成单词形式（`Id` 出现 19 次，`Ip`、`Url`、`Xml` 等也一样）。
- 常用参数词汇（工具数）：`softwarePath` 224、`dryRun` 202、`action` 107、`name` 86、`limit` 63、`devicePath` 58、`offset` 57、`itemPath` 49、`filePath` 46、`properties` 41、`confirmDelete` 34、`objectPath` 33、`expectedProjectFile` 31、`confirm` 30。
- 路径参数以 `Path`、`File`、`Directory` 结尾。另有 `exportDir`、`sourceDir` 两个缩写形式的例外。
- 操作选择器：`action`（107 个工具，其中 52 个默认为 `read`）、`kind`（22）、`mode`（7）、`operation`（1）。
- 可选字符串参数常以 `""` 作为缺省哨兵（如 `expectedPlanHash`、`expectedProjectFile`、`softwarePath`），与“缺省、null、空值不相互代替”的原则不一致（API-35）。

### 13.3 枚举值

- 自定义的输入选择值使用 camelCase（V21 中 903 个，如 `read`、`createFromMasterCopy`）。
- Siemens 原生枚举名和属性名保持 PascalCase 原样（364 个，如 `OnlyHardware`、`BackColor`）。
- 发布键作为枚举值使用数字形式（`"21"`），另有 `""` 哨兵。
- 信封中的状态词使用 kebab-case。

这几套风格并存（API-34，保留并写成规则）：协议层状态词用 kebab-case，输入选择值用 camelCase，原生名称保持原样。

### 13.4 统一参数与保留类型

内容摘要参数使用 expectedPlanHash、expectedInventoryHash、expectedDocumentHash 或 expectedPackageHash；所有 planHash MUST 为不带 sha256: 前缀的 64 个小写十六进制字符。包内容哈希和参数摘要仍属于各自数据格式，不与计划摘要混用。一次性预览令牌参数统一为 previewToken；令牌不可放入 PLAN_STALE.planHash，使用 details.previewToken。expectedProject 参数只表示工程名称；工程文件参数使用 expectedProjectFile，包括 ApplyGenerationPlan。

PreviewData 保留给 safe-v4 行为族；CursorScope 保留给快照游标宿主。current 工具的预览沿用自己的实际字段（例如 data.planHash、data.projectFile 或 data.token），不能假定都有 data.plan；只有 safe-v4 行为族切换后才保证 data.plan。NATIVE_CAPABILITY_LIMIT 警告用于 Foundation 原生能力拒绝。

新 Manage 工具 SHOULD 使用单数对象类型；已有名称的改名须逐项决定。动词检查涵盖全部名称，并包含 Prefill；其他缺失动词的现有名称需维护者决定。直接调用不可用名称返回 JSON-RPC InvalidParams；桥接到已知但本版不可用名称返回 UNSUPPORTED_CAPABILITY；完全未知名称返回 TOOL_NOT_FOUND。派发前字段级错误的 meta.tool 使用目标工具，其余派发前桥接错误使用 CallTool。

### 13.5 结果字段

旧实现的 `message` 改名为 `data.summary`；数组放在 `data.items`；旧的 meta 内容放在 `data.evidence`。

> **MCP 基线**：[Tools §Tool Names](https://modelcontextprotocol.io/specification/2026-07-28/server/tools#tool-names) 规定工具名 SHOULD 为 1–128 个字符、区分大小写、只用 `[A-Za-z0-9_.-]`、在服务器内唯一。V4 各入口均按精确大小写匹配工具名。MCP 的工具还有可选的 `title` 和 `icons`，V4 没有提供；分类标签写在描述文本里（MCP-14）。
>
> **Microsoft 基线**：[Azure §JSON](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#json)（camelCase、缩写不全大写）V4 符合。[Considerations §Use Good Names](https://github.com/microsoft/api-guidelines/blob/vNext/azure/ConsiderationsForServiceDesign.md#use-good-names) 要求同一概念始终用同一个名字。分页使用 offset/limit，预览绑定使用 previewToken 与 expectedDocumentHash/expectedPackageHash，项目身份输入使用 expectedProjectFile。既有确认开关保留各工具的语义（API-20）。Manage 多路复用工具（API-16）是 MCP 工具预算下的有意取舍。

协议层时间（meta.timestamp、data.timestamp、createdUtc、expiresUtc 等）MUST 使用 UTC RFC3339，固定七位小数并以 Z 结尾。API-13 采用清单允许的七位格式，以保留进程绑定和计划身份的时间精度。原生证据和数值 ticks 保持原有精度。

导出寄存首片和 GetExportContent 都使用 data.text，并复用同一 ExportHandle（id、mediaType、byteLength、sha256、expiresUtc、tool、target、createdUtc、totalLength）。JSON 信封的 mediaType 为 application/json，普通文本为 text/plain。ListExportHandles 用 offset/limit 分页，完整交付一页时 completeness 为 complete。

批次失败父信封采用触发停止的子项错误，并在 data.causeIndex 标明来源。成功/失败混合或子项部分完成仍为 PARTIAL_FAILURE；无可证明的子项成功计数时保持零计数并依赖子信封证据。row.outcomeUnknown 是无法读取子结果时的保守标记，消费者不得忽略它；rollbackPerformed 始终为 false。

## 14. CLI 退出码

| 退出码 | 条件（`CliExitCode`、`CliBoundary.Write`、`CliCommands`） |
|---|---|
| 0 | outcome 为 succeeded，且 completeness 为 complete |
| 2 | rejected-before-operation |
| 3 | read-failed、failed；**succeeded 但 completeness 为 none**；非工具动词异常、在线监视自检失败 |
| 4 | partial；**succeeded 但 completeness 为 partial** |
| 5 | unknown；**succeeded 但 completeness 为 unknown** |
| 64 | CLI 语法错误（不输出信封） |
| 70 | 无法创建工具上下文的进程级故障，或 bundle 资源不可用（不输出信封） |

加粗的三行是 `CliBoundary.Write` 根据完整度覆盖的规则，是规范性覆盖规则（API-33）。stdout 只输出一份信封 JSON，诊断写 stderr。用户要求的人类可读报告属于 data 中的文件或内容，**MUST NOT** 影响机器退出码。

## 15. 旧规范与本次修改前实现的差异（历史证据）

`docs/development/phase6-review.md` 第 3 节（已归档）与 `docs/releases/v4.0.0.md` 相比，现状有以下变化：

1. **错误码与警告码**：phase6 的“初始闭集”中没有 `COMPILE_ERRORS`、`RECOVERY_GUIDANCE`、`BACKUP_SKIPPED`、`APPROVAL_PRECHECK_REFUSED`、`STAGING_FOLDER_RETAINED`。v4.0.0.md 已经列出它们，但它链接的规范仍然是 phase6（文中有 TODO P7-11b）。
2. **“下列字段均始终存在”**：`PRECONDITION_FAILED.details.parameter` 和 `OUTCOME_UNKNOWN.details.reason` 是可选字段，null 时被省略。
3. **CallTool 派发前失败**：phase6 写的是 `meta.tool=CallTool`；现状是目标字段级的参数错误使用目标工具名。
4. **第 4 节 D1**：其中的 mode、confirm、expectedProjectFile 和 `data.plan` 只适用于 safe-v4 候选。4.0 的发布配置全部是 current 政策，预览使用 `dryRun=true` 和旧的数据形状。
5. **CLI**：succeeded 时还会按完整度返回 3/4/5，非工具动词的异常退出码为 2。
6. **TIMEOUT**：phase6 说只读超时用 TIMEOUT；现状中 worker 超时一律为 OUTCOME_UNKNOWN，TIMEOUT 只用于工作台控制。
7. **requiresSessionReset**：phase6 只写“unknown 时为 true”。实际上只有发出过原生调用的写入才会锁会话，v4.0.0.md 已经说明。
8. **CONFIRMATION_REQUIRED**：客户端在审批等待期间取消，会返回 `denied`。
9. **导出句柄**：phase6 只定义了 `data.export`。现状中 GetExportContent 返回的是扩展字段加 `text/plain`，分页以 UTF-16 码元为单位，句柄有效期为 24 小时。
10. **批次**：结果中多出 `rollbackPerformed` 和 `row.outcomeUnknown`。
11. **其他过时内容**：`docs/development/tool-development.md` 的“抛异常与返回失败”一节仍然写着“新代码沿用所在文件的族（F1–F6），直到阶段 6”，没有像 response-and-errors.md 那样加上“当前调用约定”的说明。v4.0.0.md 的工具数表格也是旧数。

## 附录 A　修改前响应摘录（历史快照，键已排序）

**NOT_FOUND**（V20，`GetExportContent`，句柄不存在）：

```json
{"data": null, "error": {"code": "NOT_FOUND", "details": {"target": "<exportId from response>"},
 "message": "Export handle is missing, expired or evicted in this engine session."},
 "meta": {"behaviorPolicy": "not-applicable", "completeness": "none", "execution": "not-started",
          "outcome": "rejected-before-operation", "paging": null, "releaseKey": "20", "requiresSessionReset": false,
          "tool": "GetExportContent", "warnings": [], "...": "..."}, "ok": false, "schemaVersion": 4}
```

**UNSUPPORTED_CAPABILITY**（V20，`CallTool` 的目标是 V21 专有工具；直接调用同一工具则返回 JSON-RPC `-32602`）：

```json
{"error": {"code": "UNSUPPORTED_CAPABILITY",
           "details": {"action": null, "capability": "ListCommunicationConnections", "releaseKey": "20"},
           "message": "ListCommunicationConnections requires the V21 HW.CommunicationConnections API; unavailable on V20."},
 "meta": {"tool": "CallTool", "outcome": "rejected-before-operation", "...": "..."}}
```

**PROJECT_NOT_BOUND，data 中残留旧字段**（V20，`ExchangeSystemDiagnosticsSettings`）：

```json
{"data": {"error": "Native observation failed; details are available in the local diagnostic log.",
          "operationSuccess": false, "status": "InvalidState"},
 "error": {"code": "PROJECT_NOT_BOUND", "details": {}, "message": "No project is bound."}, "...": "..."}
```

**本应是参数错误、却被归为原生失败**（V20，`CheckProductUpdate {"repository":"x"}`）：`outcome: read-failed`，`error.code: NATIVE_OPERATION_FAILED`，`data.summary: "repository must be 'owner/name', e.g. asckye/TIA_Portal_Openness_MCP."`（API-14）。

## 附录 B　MCP 现行修订中需要关注的变化（2026-07-28 相对 2025-06-18/2025-11-25）

依据 [Changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog)：

- 去掉协议层会话和 `Mcp-Session-Id`；跨调用状态改用服务器签发的句柄作为普通参数（SEP-2567）。
- 去掉 initialize 握手，每个请求在 `_meta` 中携带版本和能力；新增 `server/discover`（SEP-2575）。
- tasks 改为官方扩展 `io.modelcontextprotocol/tasks`（SEP-2663）。
- 引入 MRTR（`InputRequiredResult`），替代服务器发起的 elicitation 请求（SEP-2322）。
- 所有结果必须带 `resultType`。
- `tools/list` 必须带 `ttlMs` 和 `cacheScope`，并且 SHOULD 以确定的顺序返回。
- Logging 被弃用，建议改用 stderr 或 OpenTelemetry。V4 本来就没有使用 `notifications/message`，日志写入 stderr 和文件，已经符合。
- 定义了 `_meta` 中 `traceparent` 的约定。
- `inputSchema`/`outputSchema` 放宽为任意 2020-12 关键字，`structuredContent` 放宽为任意 JSON 值。

这些变化对 V4 的影响分别记在 MCP-01、MCP-05、MCP-12、MCP-13、MCP-15。

## 文案、行为披露与审计的来源

新增 MCP 描述、参数说明、错误与警告消息使用英文；工具名、代码、TIA 原文和示例值保持原样。工作台使用中英资源，用户文档按[文档规范](documentation-style.md)维护。既有契约消息在本次文档整理中保持不变。

D1 现行行为由生成的能力表披露。`GetToolUsage` 的空选择器返回所选发布键的 `behaviorCapabilities`，每项包含 `family`、`state`、`l5` 和 `entries`；适用的目标结果保留 `meta.behaviorPolicy` 和 `UNVERIFIED_BEHAVIOR`。桥接或批次派发前拒绝保留桥接身份，同时披露已选定的目标族。只有明确的逐版 L5 证据能切换到 `safe-v4`，见 [ADR 0002](../decisions/0002-native-behavior-by-family-acceptance.md)。候选专用参数在现行目录中不广告；保持已实现的 `confirm`、`expectedProjectFile`、计划哈希及所选版本的身份参数。已撤回的 `ConnectProject` 不作为现行参数示例。

审批先预检、批准后复核，见 [ADR 0004](../decisions/0004-precheck-before-approval.md)。请求、审批决定、start/end 和结果用同一 requestId 关联。审计链在共享数据根串行追加；哈希校验只能证明保留记录间的完整性，不能证明整段日志未被删除。默认启用审批，MCP 客户端不能自批，控制通道不能审批或切换审批。目录、管道和回退规则见[运行时布局](../runtime-layout.md)。`RECOVERY_GUIDANCE` 的生成规则见[恢复指导](../../reference/recovery-guidance.md)，复用 `GetToolUsage` 的现行示例，不创建第二套参数库。
