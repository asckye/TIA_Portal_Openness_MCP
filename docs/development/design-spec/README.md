# 设计规范

状态：现行 · 最后核对：2026-10-11，master `3a227657`

[文档目录](../../README.md) · [当前交接](../handoff.md) · [重构计划](../refactor-plan.md) · [架构决策记录](../decisions/README.md)

本目录是 TIA Portal Openness MCP 的设计规范：说明 4.0 的运行架构，并规定 C# 代码、V4 工具协议、工作台界面和文档写作的规则。维护者于 2026-10-11 决定以 Microsoft 规范为基线编写这些规范（[ADR 0015](../decisions/0015-microsoft-design-spec-baseline.md)）。规范对新工作是规范性的：新代码、新工具、新界面和新文档必须满足其中的 MUST 规则。现有代码和文档与规范的差异逐条记在偏差清单中，每条的状态已在同日决定，4.0 前的修改分配给任务 P8-60–P8-64。

## 规范与基线

| 领域 | 规范 | 基线 | 偏差清单 |
|---|---|---|---|
| 运行架构 | [4.0 架构概览](architecture.md) | 不适用（描述源码现状；决定见 ADR） | 不适用（过渡状态与缺口见该文第 9 节） |
| C# 代码与 API | [C# 与 .NET API 设计规范](csharp-design.md) | [.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)；[.NET runtime C# coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) 与 [C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)；`.editorconfig` 与 .NET SDK 自带的[代码分析](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/overview) | [C# 偏差清单](deviations/csharp.md)（CS-nn） |
| V4 工具协议 | [V4 工具协议规范](v4-protocol.md) | 主：[MCP 规范](https://modelcontextprotocol.io/specification/2025-06-18)（4.0 协商 2025-06-18 修订；现行修订为 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)）；辅：[Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md)（错误、分页、长操作、版本） | [V4 协议偏差清单](deviations/protocol.md)（MCP-nn、API-nn） |
| 工作台界面 | [工作台界面设计规范](workbench-ui.md) | [Windows 应用设计指南](https://learn.microsoft.com/en-us/windows/apps/design/)；[WCAG 2.2](https://www.w3.org/TR/WCAG22/) AA（按 [WCAG2ICT](https://www.w3.org/TR/wcag2ict-22/) 应用于桌面软件） | [工作台偏差清单](deviations/workbench.md)（UI-nn） |
| 文档写作 | [文档写作规范](documentation-style.md) | [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/)；[Microsoft 简体中文本地化风格指南](https://aka.ms/chinese-simplified-styleguide) | [文档偏差清单](deviations/docs.md)（DOC-nn） |

## 优先级

规则冲突时从上到下取上位：

1. 外部契约与安全不变量：已冻结的 V4 契约（`manifest/contracts/v4/baseline/*.json`）、MCP 线上协议、worker 协议 2 的线格式、Siemens Openness 的调用顺序、参数与线程归属、文件格式与 manifest。规范不得为满足命名或风格而改变它们。
2. 仓库规则：[CLAUDE.md](../../../CLAUDE.md)、[AGENTS.md](../../../AGENTS.md) 以及它们要求遵守的开发流程文档。
3. 本设计规范。
4. Microsoft 来源（以及规范引用的 MCP、WCAG 条款）。
5. 所在文件的既有风格：只在偏差处理完成前作为过渡；新文件必须遵守本规范。

各规范内部的细化顺序（例如工作台规范把审批与 AI 控制红线放在最前）不改变上面的次序。

## 偏差与决定的记录方式

- 每份规范有一份偏差清单，条目编号（`CS-nn`、`MCP-nn`、`API-nn`、`UI-nn`、`DOC-nn`）不重复使用。每条记录规则、证据、影响、建议和状态。
- 状态只有三种：

| 状态 | 含义 |
|---|---|
| 4.0 前 - P8-6x | 4.0 发布前修改，由对应任务实施（见下文“计划任务”） |
| 4.0 后 | 4.0 发布后修改；规则对新代码立即生效 |
| 有意偏差（已接受） | 长期保留，理由写在清单中，规范已据此写明项目取舍 |

- 默认按清单的“建议”决定；维护者另有决定的条目在清单中注明日期。一条中有部分内容另行处理时，状态列写明（例如“4.0 后；`src/Studio` 为有意偏差”）。
- 新工作需要长期偏离 MUST 规则时，先在清单中新增条目再合并，不在代码中散布抑制；规则的严重性只收紧，不放宽。
- 改变结构、契约、安全或数据完整性边界、发布与验收规则、外部基线的决定写 ADR（[文档写作规范](documentation-style.md) 第 14 节，[ADR 索引](../decisions/README.md)）。

## 决定摘要

维护者 2026-10-11 的决定：

- 基线：C# 按 .NET Framework Design Guidelines，加 `.editorconfig` 与 SDK 自带分析器；V4 协议以 MCP 规范为主、Microsoft REST API Guidelines 为辅；工作台按 Windows 应用设计指南并以 WCAG 2.2 AA 为无障碍标准；文档按 Microsoft Writing Style Guide 与 Microsoft 简体中文风格指南。规范对新工作是规范性的；现有差异记入偏差清单，在 4.0 前决定。
- 每条偏差默认采用清单中的建议（4.0 前修改、4.0 后修改或有意偏差），另有以下决定：
  - 协议：4.0 保持 MCP 2025-06-18。现行修订 2026-07-28 是无状态协议，在 .NET SDK 支持后于 4.x 迁移；规范定义“V4 会话”（[V4 工具协议规范](v4-protocol.md) 第 0.4 节；MCP-01、MCP-15）。
  - 协议：4.0 前由 `ToolMetadata` 生成 MCP 工具注解，197 个按工具名推断的分类逐个人工核对（MCP-02）。
  - 协议与 C#：V4 错误填写 `details.parameter`，消息文本保持不变（API-04、CS-39）。
  - 工作台（4.0 前）：高对比主题与 UI Automation 名称；焦点管理与访问键；文字对比 4.5:1，测试门槛相应提高；更小的最小窗口；风险确认默认选择取消；可见的预填标记（[工作台界面设计规范](workbench-ui.md) 第 0.4 节）。
  - worker 超时：P8-64 已实现，V14 SP1–V21 共用按类超时策略（[架构概览](architecture.md) 第 4、9 节；[ADR 0008](../decisions/0008-worker-timeouts-and-faults.md)）。
  - C#：4.0 前加入 `.editorconfig` 与分析器配置，列入发布源码清单，先启用最小规则集（[C# 规范](csharp-design.md) 第 13.3 节的 4.0 规则集；CS-55）；`src/Studio` 保留 `TiaOpenness` 命名空间，其余代码 4.0 后迁到 `TiaMcp`（N-07、CS-30）；新代码 SHOULD 每行不超过 120 字符，现有代码不重新排版（S-12、CS-19）。
  - 文档：改正 `--isolate-openness` 的指导（DOC-01）；工具数量由生成器写入（DOC-02）；刷新状态类文档（DOC-05）；4.0 前写出首批约 10 份 ADR（已写入 [0001–0016](../decisions/README.md)）。

## 计划任务

| 任务 | 范围 | 偏差 |
|---|---|---|
| P8-60 | V4 协议 | [V4 协议偏差清单](deviations/protocol.md) 中状态为“4.0 前 - P8-60”的 26 项，以及 6 项有意偏差的规范补写 |
| P8-61 | 工作台界面 | [工作台偏差清单](deviations/workbench.md) 中“4.0 前 - P8-61”的 32 项 |
| P8-62 | C# | [C# 偏差清单](deviations/csharp.md) 中“4.0 前 - P8-62”的 11 项，以及 CS-31 的线程安全审查 |
| P8-63 | 文档（与 P7-11b 一起） | [文档偏差清单](deviations/docs.md) 中“4.0 前 - P8-63”的 21 项，以及 4.0 前待补写的 ADR |
| P8-64 | 旧版 worker 超时 | 已实现：八版共用按类超时策略（[架构概览](architecture.md) 第 9 节） |

## 文件

规范：

- [4.0 架构概览](architecture.md)：进程、会话、关键流程、生命周期、代码位置、信任边界和过渡状态。
- [worker 通道协议 2](worker-protocol.md)：分帧、握手、纪元、进度、取消与故障。
- [C# 与 .NET API 设计规范](csharp-design.md)：命名、类型与成员设计、异常、使用规范、可空、线程与 Openness 调用、格式、工程规则和分析器配置。
- [V4 工具协议规范](v4-protocol.md)：传输映射、信封、outcome、错误、警告、分页、导出句柄、写操作、未知结果、批次、版本、命名和 CLI 退出码。
- [工作台界面设计规范](workbench-ui.md)：布局、字体、颜色与主题、命令、对话框、审批与 AI 控制红线、键盘与焦点、无障碍和本地化。
- [文档写作规范](documentation-style.md)：语言、结构、中文排版、术语表、链接、生成文档、发布记录和 ADR。

偏差清单：

- [C# 偏差清单](deviations/csharp.md)
- [V4 协议偏差清单](deviations/protocol.md)
- [工作台偏差清单](deviations/workbench.md)
- [文档偏差清单](deviations/docs.md)

架构决策记录（[索引与待补写清单](../decisions/README.md)，[模板](../decisions/0000-template.md)）：

- [0001 4.0 硬切换到 V4 契约，不提供 3.x 兼容层](../decisions/0001-hard-cut-v4-contract.md)
- [0002 原生行为按 D1 行为族真机验收后才切换为 safe-v4](../decisions/0002-native-behavior-by-family-acceptance.md)
- [0003 MCP 写调用默认须在工作台审批，工作台功能纳入 4.0](../decisions/0003-workbench-approval-for-mcp-writes.md)
- [0004 写请求先预检再审批，批准后复核再执行](../decisions/0004-precheck-before-approval.md)
- [0005 V20/V21 与其他六个版本采用同一架构](../decisions/0005-v20-v21-on-foundation-host.md)
- [0006 复用引擎 EXE 作为 V20/V21 的 worker，引擎宿主层退役](../decisions/0006-engine-exe-as-v20-v21-worker.md)
- [0007 每个 MCP 会话一个 worker](../decisions/0007-one-worker-per-mcp-session.md)
- [0008 worker 调用按类超时，故障使 worker 代次失效且从不重放](../decisions/0008-worker-timeouts-and-faults.md)
- [0009 V20/V21 专有工具按能力族移入 Foundation 适配器，之后引擎退役](../decisions/0009-port-v20-v21-tools-to-foundation-adapters.md)
- [0010 发现、桥接与批处理工具在全部八个版本发布](../decisions/0010-discovery-and-batch-tools-on-all-releases.md)
- [0011 八个版本都用 TIA 进程租约](../decisions/0011-tia-process-lease-on-all-releases.md)
- [0012 旧版只注册至少有一个动作能在该版 API 上运行的工具](../decisions/0012-old-release-tool-registration.md)
- [0013 4.0 在阶段 7 与阶段 8 全部完成后发布](../decisions/0013-release-4-0-after-phases-7-and-8.md)
- [0014 各版本的工具数量按官方 API 支持决定](../decisions/0014-tool-counts-follow-official-api.md)
- [0015 项目设计规范以 Microsoft 规范为基线](../decisions/0015-microsoft-design-spec-baseline.md)
- [0016 设计决策用 ADR 记录](../decisions/0016-architecture-decision-records.md)

设计记录：

- [阶段 7 设计：V20/V21 改用 Foundation 宿主与引擎 worker](../design/p7-host-worker-split.md)（历史记录）
- [P8-31 分阶段生成执行设计](../design/p8-31-staged-generation.md)（现行）
