# 本项目采用的开发规范

状态：现行 · 最后核对：2026-11-05，beta.4 候选基线 `13eb7725`

本仓库遵循[《工程开发规范》2.0](https://github.com/asckye/engineering-standards/tree/v2.0)（asckye/engineering-standards，tag `v2.0`）；规范只有这一个来源，本仓库不保存副本（[ADR 0032](decisions/0032-published-engineering-standards.md)）。本页只记录采用范围、项目偏差、外部参考和本目录的文件，外部参考一节取代了[参考资料与落地对照](../archive/development/references.md)。与规范冲突时以本页和 [ADR](decisions/README.md) 为准。

## 采用范围

| 项目 | 选择 |
|---|---|
| 规范版本 | [通用规范](https://github.com/asckye/engineering-standards/tree/v2.0/general)和[桌面与本机软件](https://github.com/asckye/engineering-standards/tree/v2.0/desktop) 2.0 |
| 项目类型 | `desktop/`（工作台与本机软件）；`mcp/` 待采纳，采纳前 MCP 服务和工程软件自动化（TIA Portal Openness）按 1.1 的[领域 A](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/a-mcp.md)、[领域 C](https://github.com/asckye/engineering-standards/blob/ec8e722f332cf4ca2091501896037f350bbeb53f/handbook/domains/c-vendor-api.md) |
| 等级 | M（正式产品）；审批、写入安全和防止项目数据损坏的规则按 L 执行 |
| 必读 | 通用规范的最小必做清单、黄金路径、任务手册、快速交付；改动涉及的章节 |
| 任务说明 | 按通用规范模板 [T.4](https://github.com/asckye/engineering-standards/blob/v2.0/general/appendix/t-templates.md#tpl-task)（实现代理任务说明），并加上本仓库 [AGENTS.md](../../AGENTS.md) 的规则 |
| 完成定义 | 通用规范[第 23 节](https://github.com/asckye/engineering-standards/blob/v2.0/general/23-definition-of-done.md#dod)，加上本仓库的生成器 `-Check`、工具定义契约基线和实机清单要求 |

## 项目自己的取舍（偏差登记）

| 主题 | 本项目的做法 | 依据 |
|---|---|---|
| 支持的版本 | TIA Portal V14 SP1、V15.1、V16–V21 八个版本，功能按各版本官方 API 注册 | [ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| 合并与发布 | 主机合并门 `merge-check`，FAST-GATE 目标约 10 分钟，当前宽范围运行报告约 12 分钟，优化前约 21 分钟。候选前手动运行 `merge-check -All`。`prepare` 构建、打包、检查 ZIP 并上传私有草稿，扫描接入前实测约 6 分钟，`publish-prepared` 公开的硬预算为 300 秒。取消 nightly，GitHub CI 不阻塞合并 | [ADR 0031](decisions/0031-release-path.md)与[进度](../plan/progress.md) |
| MCP-11 输入 schema | 工具 `inputSchema` 根节点不使用 `oneOf`、`allOf`、`anyOf`，以兼容拒绝根组合器的客户端。互斥参数和表单字段规则写入描述并在运行时拒绝不合法组合，嵌套属性仍可用组合器。目录测试守住全部八版，`PrefillWorkbenchForm` 使用平铺 schema | 维护者 2026-11-04 决定，DES-4 Part 1。[ADR 0033](decisions/0033-canonical-tool-parameters.md)与[设计规范](design.md) |
| 工作台视觉 | EplanMcp 工作台设计系统；表格行高 36；审批按钮等权重、拒绝在左、回车不批准、拒绝后不撤销 | [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md) |
| 用语 | 界面和文档用“服务”“项目”（TIA 的 project） | [ADR 0026](decisions/0026-documentation-structure-and-language.md) |
| 根目录布局 | `runtime/`、根启动器 `TiaOpenness.exe` 与项目 `bin`/`obj` 保留为忽略的构建输出；checkout 检查依赖与 `manifest/`、`templates/` 同根的运行布局，项目输出沿用 .NET 标准布局。`TiaMcp_Output/` 保留供生态 Python 环境、发布前置检查和产品使用。将 checkout 检查改为从提取的 ZIP 运行安排在 beta.4 之后；`hooks/` 不搬入 `plugin/`，避免旧安装遗留的钩子重复加载 | [ADR 0030 修订](decisions/0030-documentation-and-repository-structure.md#2026-11-04-修订保留运行输出和根目录钩子) |
| 文档目录 | 开发文档分为 `docs/standards/`、`docs/plan/`、`docs/manuals/`、`docs/archive/`，不用通用规范第 2.6 节的目录；ADR 在 `docs/standards/decisions/`（通用规范 DOC-04 写 `docs/adr/`），交接与计划分别在 `docs/plan/progress.md`、`docs/plan/plan.md`（通用规范 DOC-11） | [ADR 0030](decisions/0030-documentation-and-repository-structure.md) |
| 规范版本并存 | 通用规则和桌面规则按 2.0；MCP 和厂商 API 规则按 1.1，链接固定到提交 `ec8e722`（v2.0 已没有这部分正文，也没有 v1.1 tag）。采纳 `mcp/` 时结束 | [ADR 0032](decisions/0032-published-engineering-standards.md) |

## MCP 草案：待采纳

`mcp/`（[工程软件 MCP 开发规范](https://github.com/asckye/engineering-standards/blob/v2.0/mcp/README.md)，草稿 0.1）取代 1.1 的领域 A、C。本项目还没有采纳它，是否采纳、何时采纳由维护者决定。

- 采纳前：MCP 和厂商 API 规则按 1.1，文档写“1.1 MCP-xx”“1.1 VND-xx”，链接固定到提交 `ec8e722`。新旧编号的对应和每处变化见[对照表](https://github.com/asckye/engineering-standards/blob/v2.0/mcp/handbook-mapping.md)。
- 采纳时：把引用改为新编号，更新本页和 [ADR 0032](decisions/0032-published-engineering-standards.md)，并把下面的差距登记为偏差。对 MUST 的例外写 ADR（G-04），偏离 SHOULD 写一行理由。按 `mcp/` 的要求，已发布的工具名和字段不改名，还在 MCP 旧修订上要写明迁移计划。

下面的清单来自 2026-11-05 的逐条对照（master `3de70327`，MCP 2025-06-18，SDK 0.3.0-preview.4）。对照早于草案定稿，部分规则之后改过措辞，所以这不是合规结论；采纳时按当时的代码和规则重新核对。

### 有差距的 MUST

| 规则 | 差距 |
|---|---|
| `PRO-03`、`PRO-08` | 运行在 MCP 2026-07-28 上才适用。项目还在 2025-06-18，迁移时要满足（设计规范偏差 MCP-01、MCP-15） |
| `PRO-09` | 预览令牌和导出句柄是显式的；项目绑定和会话锁仍是隐式会话状态（设计规范偏差 MCP-01） |
| `PRO-12` | 普通写工具的计划哈希不消费，客户端重发会再走审批后再次执行 |
| `PRO-13` | 取消通知已处理，但没有把 HTTP 客户端断开明确当作取消 |
| `PRO-18` | 不符回 403，但允许列表默认含回环同端口，缺 `Origin` 时放行 |
| `PRO-19` | 弃用中的 `--http-api-key` 仍接受命令行明文密钥；没有找到连续失败后的限流 |
| `PRO-21`、`ENG-05` | 调用串行，但等待队列没有长度上限，也没有速率限制（设计规范偏差 MCP-16） |
| `TL-14` | 顶层都设了 `additionalProperties: false`；90 个嵌套对象有 63 个未封闭，也未声明 JSON Schema 2020-12 |
| `TL-17` | 211 个写工具中只有 52 个带目标身份参数，其余依赖会话绑定 |
| `TL-30` | 错误对象没有 `retryable` |
| `TL-31` | 不少消息没有说明下一步 |
| `WS-01`、`WS-02` | 没有独立的破坏性级别，审批风险按工具名前缀判定；`BuildDeviceAmlDocument` 会写文件却标 `readOnlyHint: true` |
| `WS-03` | 命令组固定且经审批，但参数不逐个校验，项目内的测试和配置文件可以含可执行 Python |
| `WS-04` | 4 个 WRITE 工具没有预览；22 个 FILE 工具没有预览，要按草案对写工具和本地工具的定义逐个判断；通用预览的变更清单只是规范化后的参数 |
| `WS-07` | `Compile*`（EXECUTE）不经审批 |
| `WS-09` | 确认按钮有 Alt 访问键；500 毫秒保护只在 AI 切页后生效，请求出现时没有 |
| `WS-10`、`WS-28` | 不剥除控制字符和双向文字控制符；项目名称和路径会拼进 `data.summary` |
| `WS-13` | 复核失败多返回 `CONFIRMATION_REQUIRED` 而不是 `PLAN_STALE`；通用哈希不含对象状态指纹，普通工具的计划不是只执行一次 |
| `WS-17` | `execution` 取值是 `not-started`、`read-only`、`completed`、`partial`、`unknown`：没有 `rolled_back`，确定失败也记 `completed` |
| `WS-21` | 写入编号与会话内 `GetCallResult` 已在 NO-LOCK Part 2 实现。结果限当前会话内存最近 16 个完成调用及运行中的调用，未实现跨重启持久化（设计规范偏差 API-09） |
| `WS-23`、`WS-24`、`WS-41` | 有意偏离未采纳草案的全会话暂停规则。结果未知只记录本次调用，后续独立请求继续，保存携带 `UNSETTLED_CALLS`。见 [ADR 0034](decisions/0034-no-session-lock.md) |
| `WS-26` | 审计写失败被吞掉，写入照常执行 |
| `WS-27` | 恢复备份只覆盖部分路径；备份被跳过时只发 `BACKUP_SKIPPED` 警告，不停止 |
| `WS-29` | 不拒绝 `..`（只规范化），输出路径不查控制字符和保留设备名 |
| `WS-30` | 输出根内 `overwrite=true` 不需要人确认 |
| `WS-31` | CFC 导出 zip 的读取没有数量和大小上限 |
| `WS-32` | WriteGuard 有第二份去敏实现；调用日志默认保存去敏后的参数和结果，项目内容会进日志 |
| `WS-33` | `install-plc-tools` 运行子进程时没有超时 |
| `ENG-14` | 只有 8 个工具用 TIA 事务，其余用回滚日志或写明不回滚 |
| `ENG-15` | 没有逐条行为带编号、版本和日期的 API 笔记 |
| `ENG-16` | 有运行时拒绝表和崩溃防护，没有统一的禁用 API 列表或编译期拦截 |
| `VER-01` | 工具规格只写进不入库的任务说明，仓库里没有规格产物 |
| `VER-02` | 服务器说明不在契约快照内 |
| `VER-09` | 清单和 `NOT RUN` 记录都有，但 beta.2、beta.3 的 VM 原始结果还没有进入台账 |

### 其他规则

- 有差距的 SHOULD：PRO-01、PRO-02、PRO-07、TL-01–TL-06、TL-08–TL-10、TL-13、TL-15、TL-18、TL-19、TL-25、TL-26、TL-32、TL-34、WS-12、ENG-06、ENG-17、ENG-18、VER-04、VER-05、VER-11–VER-13。已发布的 PascalCase 工具名按 `TL-06` 登记，不改名。
- 还没有核对：PRO-22、PRO-23、TL-36–TL-39、WS-36–WS-43、ENG-20–ENG-25、VER-14。
- 目前不适用：PRO-16、WS-16、ENG-04；WS-19 在错误对象加上 `retryable` 后适用。

## 本目录的文件

| 文件 | 内容 |
|---|---|
| [development.md](development.md) | 开发规范：C# 与 .NET 代码、仓库结构、工具开发、测试策略 |
| [design.md](design.md) | 设计规范：架构、V4 工具协议与 worker 协议、运行时布局、MCP 工具设计 |
| [ui.md](ui.md) | 工作台界面规范 |
| [security.md](security.md) | 安全标准与威胁模型 |
| [documentation.md](documentation.md) | 文档规范：文档位置、语言、写法、术语、ADR 规则和文档偏差 |
| [decisions/](decisions/README.md) | 架构决策记录（ADR）、索引和模板 |

计划在 [docs/plan](../plan/plan.md)，操作手册在 [docs/manuals](../manuals/workflow.md)，历史资料在 `docs/archive/`（只读）。

质量评分由 `scorecard` 按需从源码、契约和已有运行结果生成；十四个维度分别报告，涉及版本的指标逐版展示，不合成项目总分。分档集中在 [ScorecardBands.cs](../../build-tools/release/ScorecardBands.cs)，当前标为 `proposed`，待维护者查看首份报告后批准。缺失来源标为 `not measured`，只有其他版本证据时标为 `no evidence`；旧开发版记录标为 `stale`，不参与评分。运行方法和证据边界见[验证](../manuals/validation.md#质量评分)。

## 外部参考

“基线”是冲突时优先的来源（[ADR 0015](decisions/0015-microsoft-design-spec-baseline.md)），“补充”只在基线没有覆盖时使用；具体采纳了哪些规则写在落地文件中。标“待核实”的来源链接或许可证尚未核实。

| 落地位置 | 基线 | 补充 |
|---|---|---|
| [文档规范](documentation.md)、[ADR](decisions/README.md) | [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/)、[Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide)、[Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record) | [Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)、[Keep a Changelog](https://keepachangelog.com/en/1.1.0/)、archify（[ADR 0027](decisions/0027-archify-diagrams.md)，待核实） |
| [开发规范](development.md) | [.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)、[.NET runtime C# coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) | 无 |
| [设计规范](design.md) | [MCP 规范 2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18) 与 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)、[Azure REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md)、Siemens Openness 文档（`reference/siemens-openness/`）与各版本 PublicAPI（[ADR 0014](decisions/0014-tool-counts-follow-official-api.md)） | [语义化版本](https://semver.org/lang/zh-CN/)、JSON Schema 2020-12（`reference/schemas/standards/v1/`）、Anthropic 关于为 AI 代理编写工具的文章（待核实） |
| [工作台界面规范](ui.md) | [Windows 应用设计指南](https://learn.microsoft.com/en-us/windows/apps/design/)、[WCAG 2.2](https://www.w3.org/TR/WCAG22/) | [EplanMcp 工作台设计系统](https://claude.ai/artifact/Kbjk7iPWb7cMGxdVLbaQsg)（[ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md)） |
| [安全标准](security.md) | 无 | STRIDE 威胁分类（待核实）、姊妹项目 EplanMCP 的安全标准与工具规格模板 |
