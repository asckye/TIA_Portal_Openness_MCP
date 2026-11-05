# 本项目采用的开发规范

状态：现行 · 最后核对：2026-11-04，master `871d54da`

本仓库遵循[《工程开发手册》v1.1](engineering-handbook.html)（通用手册，随仓库保存供离线阅读）；本页只记录采用范围、项目偏差、外部参考和本目录的文件，外部参考一节取代了[参考资料与落地对照](../archive/development/references.md)。与手册冲突时以本页和 [ADR](decisions/README.md) 为准。

## 采用范围

| 项目 | 选择 |
|---|---|
| 领域规范 | A（MCP 服务）、C（工程软件自动化：TIA Portal Openness）、D（桌面应用：工作台） |
| 档位 | M（正式产品）；审批、写入安全和防止项目数据损坏的规则按 L 执行 |
| 必读 | 手册的最小必做清单、黄金路径、任务手册、快速交付；改动涉及的章节 |
| 任务说明 | 按手册模板 T.4（实现代理任务说明），并加上本仓库 [AGENTS.md](../../AGENTS.md) 的规则 |
| 完成定义 | 手册第 23 节，加上本仓库的生成器 `-Check`、工具定义契约基线和实机清单要求 |

## 项目自己的取舍（偏差登记）

| 主题 | 本项目的做法 | 依据 |
|---|---|---|
| 支持的版本 | TIA Portal V14 SP1、V15.1、V16–V21 八个版本，功能按各版本官方 API 注册 | [ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md) |
| 合并与发布 | 主机合并门 `merge-check`（宽范围改动实测 21–28 分钟），候选前手动运行 `merge-check -All`；`prepare` 构建、打包、检查 ZIP 并上传私有草稿（实测约 6 分钟），`publish-prepared` 公开的硬预算为 300 秒；取消 nightly，GitHub CI 不阻塞合并 | [ADR 0031](decisions/0031-release-path.md)；实现状态见[进度](../plan/progress.md) |
| 工作台视觉 | EplanMcp 工作台设计系统；表格行高 36；审批按钮等权重、拒绝在左、回车不批准、拒绝后不撤销 | [ADR 0028](decisions/0028-workbench-visual-design-eplanmcp.md) |
| 用语 | 界面和文档用“服务”“项目”（TIA 的 project） | [ADR 0026](decisions/0026-documentation-structure-and-language.md) |
| 根目录布局 | `runtime/`、根启动器 `TiaOpenness.exe` 与项目 `bin`/`obj` 保留为忽略的构建输出；checkout 检查依赖与 `manifest/`、`templates/` 同根的运行布局，项目输出沿用 .NET 标准布局。`TiaMcp_Output/` 保留供生态 Python 环境、发布前置检查和产品使用。将 checkout 检查改为从提取的 ZIP 运行安排在 beta.4 之后；`hooks/` 不搬入 `plugin/`，避免旧安装遗留的钩子重复加载 | [ADR 0030 修订](decisions/0030-documentation-and-repository-structure.md#2026-11-04-修订保留运行输出和根目录钩子) |
| 文档目录 | 开发文档分为 `docs/standards/`、`docs/plan/`、`docs/manuals/`、`docs/archive/`，不用手册第 2.6 节的目录；ADR 在 `docs/standards/decisions/`（手册 DOC-04 写 `docs/adr/`），交接与计划分别在 `docs/plan/progress.md`、`docs/plan/plan.md`（手册 DOC-11） | [ADR 0030](decisions/0030-documentation-and-repository-structure.md) |

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
