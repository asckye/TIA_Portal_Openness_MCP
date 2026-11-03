# MCP 工具设计规范

状态：现行 · 最后核对：2026-11-01，master `902a86fe`

[文档目录](../README.md) · [V4 工具协议规范](design.md) · [工具开发](development.md) · [工具规格模板](../manuals/workflow.md) · [安全标准](security.md)

本文是新增或修改 MCP 工具时的设计规则，面向工具作者和审查者。信封、错误码、分页、警告和恢复流程以 [V4 工具协议规范](design.md) 为准，本文只链接，不重复。

本文同时记录维护者 2026-11-01 批准的工具规则：经真机核对的示例、JSON Schema 2020-12 参数约定、Microsoft REST 错误模型的 `target` 与 `innererror`、诊断编号、语义化版本与弃用策略。这些规则大部分尚未实现，每节都写明现状和负责的任务。

规范用语按 [文档写作规范](documentation.md) 第 0 节：MUST、SHOULD、MAY。

括号中只写“第 N 节”时，指 [V4 工具协议规范](design.md) 的章节；本文自己的章节写“本文第 N 节”。

## 1 先判断是否需要新工具

- MUST：只为官方 Openness API 支持的能力注册工具。一个工具在某个发布键上至少有一个动作能在该版 API 上运行时才注册；不支持的动作返回 `UNSUPPORTED_CAPABILITY`，不靠名称推断可用性（[ADR 0012](decisions/0012-old-release-tool-registration.md)、[ADR 0014](decisions/0014-tool-counts-follow-official-api.md)）。
- MUST：各版本的工具数量随 API 支持而定，不为一致而拉平；数量只由生成目录给出，文档不手写（[生成的工具目录](../reference/version-tool-catalog.md)）。
- SHOULD：先查是否能扩展现有工具的动作或可选参数。新增可选参数是增量变化；新增工具会增加模型的上下文负担。
- MUST：同一能力只保留一个实现，八个发布键共用一份声明与逻辑，版本 API 差异放在按发布键编译的适配器中（[工具开发](development.md)“现行工具归属”）。
- MUST：新工具或契约变化先写工具规格（[工具规格模板](../manuals/workflow.md)），再实现。

## 2 命名与描述

- MUST：工具名是 PascalCase 的“动词 + 领域对象 + 必要限定”，只用 ASCII 字母和数字；动词取自 `build-tools/release/CatalogData.cs` 的闭集，生成器检查全部工具名（[V4 工具协议规范](design.md) 第 13.1 节）。
- MUST：名称不暴露 `Json`、`Xml`、`Xlsx` 等序列化格式；单个对象用单数，批量操作用复数。
- MUST：描述以 `[层][域][操作]` 开头，操作类别取自 UI、SESSION、READ、WRITE、FILE、OFFLINE、ONLINE、ONLINE-WRITE、EXECUTE（第 9.1 节）。
- MUST：MCP 可见文本（描述、参数说明、消息）用英文；中文只作为数据出现，例如 TIA 原文和对象名（ADR 待办 B02，见 [架构决策记录](decisions/README.md)）。
- SHOULD：描述先写工具做什么和前提条件（需要绑定项目、需要离线、是否写入），再写限制。

## 3 分类、注解与审批

- MUST：每个工具在 `ToolMetadata`（`src/Logic/ModelContextProtocol/ToolMetadata.cs`）或 `[ToolClassification]` 中登记显式分类，不按名称推断。
- MUST：四个注解 hint 全部显式输出，按最有副作用的动作与参数组合决定；带 `dryRun` 参数的工具 `readOnlyHint` 为 false（第 4.8 节）。
- MUST：分类为 WRITE、ONLINE-WRITE 的实际执行经工作台审批（[安全标准](security.md) SEC-01）；不得新增绕过审批的入口。
- MUST：写工具提供预览。有 `dryRun` 参数的工具，预览路径不写项目、文件或在线设备。维护者 2026-11-01 决定每个写入都可预览并能回滚，由 DES-3 实现，尚未完成。
- MUST：结果未知时返回 `OUTCOME_UNKNOWN`，不自动重放（第 10 节）。

## 4 参数约定

### 4.1 现行规则

- MUST：参数名用 camelCase；路径参数以 `Path`、`File`、`Directory` 结尾（第 13.2 节）。
- MUST：一次性预览令牌统一叫 `previewToken`；`expectedProject` 只表示项目名称，项目文件用 `expectedProjectFile`（第 13.4 节）。
- MUST：内容摘要用 64 个小写十六进制字符，不带 `sha256:` 前缀。
- 现状偏差：部分可选字符串参数用 `""` 作为缺省哨兵（API-35）；同一概念在不同工具中的形式不一致，例如导出目标有的是目录、有的是新文件，`devicePath` 有的是字符串、有的是数组。

### 4.2 JSON Schema 2020-12 约定（已决定，DES-4 实施）

维护者 2026-11-01 批准参数 schema 按 JSON Schema 2020-12 的写法统一。DES-4 先写参数模型 ADR，再按 ADR 修改八个发布键的工具、示例和描述。合并前以本文第 4.1 节为准。

- 必填参数列在 `required`；可选参数不用空字符串哨兵表示“未提供”，省略即未提供。
- 闭集取值用 `enum`；有固定格式的字符串用 `pattern`。
- 互斥的参数组用 `oneOf` 表达。
- 同一概念在全部工具中只有一种规范形式：路径参数统一一种写法；文件与目录用不同的参数名明确区分（例如 `exportDirectory` 与 `exportFile`）。
- 旧参数名在仍有使用的地方保留为有文档的别名，只保留一个主版本，使用时在 `meta` 中给出弃用警告；不得静默改变旧参数的含义。

## 5 错误

- MUST：错误对象符合第 5 节：`code`、`message`、`details`；`message` 是稳定的英文文本，不承担机器语义。
- MUST：参数错误在 `details.parameter` 中指出出错的参数，`parameters` 列出全部出错字段；枚举错误给出 `allowedValues`。
- 现状偏差：schema 校验失败时 `parameter` 有时只写 `arguments`；部分枚举错误没有列出允许值；个别消息指错了领域（见 [V4 协议偏差清单](design.md) API-04、API-14）。

### 5.1 target 与 innererror（已决定，DES-4 实施）

维护者 2026-11-01 批准按 [Azure REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md) 的 [Handling Errors](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md#handling-errors) 补充错误信息：

- 每个 `INVALID_ARGUMENT` 用 JSON 指针写出准确的参数路径，并写出期望的类型或形式。
- 错误对象增加 `innererror`，承载原生异常类型、HResult 等下层信息。字段名和位置由 DES-4 定稿，并同步修改 V4 工具协议规范第 5.1 节“恰好包含三个字段”的规则。
- 现有定型 `details` 中的 `target` 与 `parameter` 继续表示出错的对象或参数。

## 6 诊断编号（已决定，DES-4 实施）

- 现状：`INTERNAL_ERROR` 的 `details` 带 `diagnosticId`。`NATIVE_OPERATION_FAILED` 的 `nativeMessage` 多数为空，详情只在服务器日志中；`GetNativeInvocationLog` 不能按请求号筛选。
- 决定（维护者 2026-11-01）：每个原生失败信封返回去敏后的原生消息首行、原生异常类型、HResult 和关联服务器日志的 `diagnosticId`；堆栈和多行详情只进日志。`GetNativeInvocationLog` 支持按请求号和诊断编号筛选并分页；超出上限的 `take` 返回 `INVALID_ARGUMENT` 并给出上限。
- 去敏沿用现有规则（[安全标准](security.md) SEC-06）。诊断编号的格式由 DES-4 定稿。

## 7 示例

- MUST：工具参数、调用序列、结果解释和官方出处只在 `reference/tool-examples` 中维护，由 `GetToolUsage` 按发布键返回；兼容入口复用同一数据，不另写规则文档（[统一工具示例](../manuals/real-machine-testing.md)）。
- MUST：每个注册工具和操作都有示例；示例使用所选发布键的真实 schema；项目相关的值（项目、PLC、组、路径）放在绑定和参数来源中，不写成看似通用的常量。
- MUST：每条示例的 `validation` 文字如实写明验证范围，例如“离线预览，原生执行 NOT RUN”。
- 决定（维护者 2026-11-01）：示例必须带在真实 TIA Portal 上正确的参数，让其他 AI 客户端第一次调用就能成功，不靠试错。beta.2 虚拟机测试中的大部分失败来自示例参数（项目名称与路径混用、分组的块与设备路径、导出目录与文件混用、预览令牌的叫法）。核对结果记入 [真机台账](../reference/real-machine-ledger.md)；示例数据中如何标记“已经真机核对”尚未决定。
- 修改示例后依次运行 `generate-catalogs`、`generate-tool-usage`，再构建并执行八版检索和功能检查（[CLAUDE.md](../../CLAUDE.md)）。

## 8 批处理准入

- MUST：八个发布键使用同一份 `ToolMetadata` 分类表；只读批次要求 `BatchRead: true`，预览批次要求 `BatchWrite: true` 且有布尔 `dryRun` 参数；不按类别推断准入（第 11 节）。
- MUST：新开放只读批次的工具不改变绑定、会话、项目、库或在线状态，不写文件，执行成本有界。

## 9 版本与弃用

- 现行：版本号从 2.8.0 起按 [语义化版本](https://semver.org/lang/zh-CN/)：工具名、参数或返回形状的不兼容改动和删除工具升主版本；新工具、新参数和新功能升次版本；缺陷修复和文案改动升修订号。预发布写作 `X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`（[CHANGELOG.md](../../CHANGELOG.md) 开头的说明）。
- 现行：4.x 只做增量：新工具、新可选参数、新字段和新诊断码；改名、删除、改变必填参数或字段含义等破坏性修改必须升 `schemaVersion`，并在发布说明和迁移表中逐项列出（第 12.1 节）。
- 现行：4.0 是硬切换，不提供 3.x 别名，迁移靠生成的对照表（[ADR 0001](decisions/0001-hard-cut-v4-contract.md)）。
- 决定（维护者 2026-11-01），尚未实现：
  - 把上述语义化版本规则写入协议规范第 12.1 节，作为 MCP 契约的版本政策。
  - 工具和参数可以标记为弃用：目录和 `GetToolUsage` 输出弃用信息，调用时在 `meta` 中给出弃用警告，迁移表按版本维护。
  - 弃用窗口的长度和弃用信息的字段尚未定稿；DES-4 的参数别名先按“保留一个主版本”执行。

## 10 契约变更流程

1. 写或更新工具规格，在审查中确认分类、审批、发布键范围和破坏性变化。
2. 修改声明与实现；需要时修改 `reference/tool-examples` 和 `reference/tool-catalog`。
3. 依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`；迁移表有变化时运行 `generate-release-migration`。各命令的 `-Check` 必须通过。
4. 构建后重新捕获契约与响应基线，用 `compare` 列出全部差异（[验证分层](../manuals/validation.md)“工具契约兼容检查”）。
5. 在任务报告和合并审查中逐项解释差异；破坏性变化另写入发布说明与迁移表。

## 11 评审清单

- [ ] 至少一个动作在每个注册的发布键上有官方 API 证据。
- [ ] 名称的动词在闭集中，描述以 `[层][域][操作]` 开头，MCP 文本为英文。
- [ ] 显式分类和四个注解 hint 已登记；写入经审批；预览不写入。
- [ ] 参数符合本文第 4 节；新参数不使用空字符串哨兵。
- [ ] 参数错误指出具体参数；原生失败不泄露堆栈和凭据。
- [ ] 每个操作都有示例，`validation` 如实写明验证范围。
- [ ] 生成器 `-Check` 通过，契约与响应差异已逐项解释。
- [ ] 失败路径有测试（[测试策略](development.md) 第 5 节）。
