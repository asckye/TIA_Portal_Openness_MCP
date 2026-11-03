# 本项目采用的开发规范

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本仓库遵循[《工程开发手册》v1.1](engineering-handbook.html)（通用手册，随仓库保存供离线阅读）；本页只记录采用范围、项目偏差、外部参考和本目录的文件，外部参考一节取代了[参考资料与落地对照](../archive/development/references.md)。与手册冲突时以本页和 [ADR](decisions/README.md) 为准。

## 采用范围

| 项目 | 选择 |
|---|---|
| 领域规范 | A（MCP 服务）、C（工程软件自动化：TIA Portal Openness）、D（桌面应用：工作台） |
| 档位 | M（正式产品）；审批、写入安全和防止项目数据损坏的规则按 L 执行 |
| 必读 | 手册的最小必做清单、黄金路径、任务手册、快速交付；改动涉及的章节 |
| 任务说明 | 按手册模板 T.4（实现代理任务说明），并加上本仓库 [AGENTS.md](../../AGENTS.md) 的规则 |
| 完成定义 | 手册第 23 节，加上本仓库的生成器 `-Check`、契约与响应基线和实机清单要求 |

## 项目自己的取舍（偏差登记）

| 主题 | 本项目的做法 | 依据 |
|---|---|---|
| 支持的版本 | TIA Portal V14 SP1、V15.1、V16–V21 八个版本，功能按各版本官方 API 注册 | [ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| 合并检查目标 | 本机 `check-all -Affected` 20 分钟以内（手册约 15–20 分钟）；夜间全量检查 30 分钟以内，上限 45 分钟不变（维护者 2026-11-03，SLIM-1） | [ADR 0024](decisions/0024-ci-does-not-block-merges.md)、[ADR 0025](decisions/0025-release-evidence-nightly-and-verified-build-units.md) |
| 工作台视觉 | EplanMcp 工作台设计系统；表格行高 36；审批按钮等权重、拒绝在左、回车不批准、拒绝后不撤销 | [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md) |
| 用语 | 界面和文档用“服务”“项目”（TIA 的 project） | [ADR 0026](decisions/0026-documentation-structure-and-language.md) |
| 文档目录 | 开发文档分为 `docs/standards/`、`docs/plan/`、`docs/manuals/`、`docs/archive/`，不用手册第 2.6 节的目录；ADR 在 `docs/standards/decisions/`（手册 DOC-04 写 `docs/adr/`），交接在 `docs/plan/progress.md`（手册 DOC-11 写 `handoff.md`） | [ADR 0030](decisions/0030-documentation-and-repository-structure.md) |

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

## 外部参考

“基线”是冲突时优先的来源（[ADR 0015](decisions/0015-microsoft-design-spec-baseline.md)），“补充”只在基线没有覆盖时使用；具体采纳了哪些规则写在落地文件中。标“待核实”的来源链接或许可证尚未核实。

| 落地位置 | 基线 | 补充 |
|---|---|---|
| [文档规范](documentation.md)、[ADR](decisions/README.md) | [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/)、[Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide)、[Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record) | [Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)、[Keep a Changelog](https://keepachangelog.com/en/1.1.0/)、archify（[ADR 0027](decisions/0027-archify-diagrams.md)，待核实） |
| [开发规范](development.md) | [.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)、[.NET runtime C# coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) | 无 |
| [设计规范](design.md) | [MCP 规范 2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18) 与 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)、[Azure REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md)、Siemens Openness 文档（`reference/siemens-openness/`）与各版本 PublicAPI（[ADR 0014](decisions/0014-tool-counts-follow-official-api.md)） | [语义化版本](https://semver.org/lang/zh-CN/)、JSON Schema 2020-12（`schemas/standards/v1/`）、Anthropic 关于为 AI 代理编写工具的文章（待核实） |
| [工作台界面规范](ui.md) | [Windows 应用设计指南](https://learn.microsoft.com/en-us/windows/apps/design/)、[WCAG 2.2](https://www.w3.org/TR/WCAG22/) | [EplanMcp 工作台设计系统](https://claude.ai/artifact/Kbjk7iPWb7cMGxdVLbaQsg)（[ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md)） |
| [安全标准](security.md) | 无 | STRIDE 威胁分类（待核实）、姊妹项目 EplanMCP 的安全标准与工具规格模板 |
