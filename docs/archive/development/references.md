# 参考资料与落地对照

状态：现行 · 最后核对：2026-11-01，master `902a86fe`

[文档目录](../../README.md) · [设计规范索引](design-spec/README.md) · [架构决策记录](../../standards/decisions/README.md)

本表列出项目采用的外部规范和实践：采纳了什么、落在哪里。“基线”是冲突时优先的来源（[ADR 0015](../../standards/decisions/0015-microsoft-design-spec-baseline.md)）；“补充”只在基线没有覆盖时使用。

链接只使用仓库中已经引用过的地址。写着“链接待核实”的来源，由维护者或 Claude 联网核实后补上链接。

## 基线

| 来源 | 采纳要点 | 落地位置 | 类别 |
|---|---|---|---|
| [Microsoft Writing Style Guide](https://learn.microsoft.com/en-us/style-guide/welcome/) | 英文写作、标题、列表、表格、步骤、开发者元素的格式 | [文档写作规范](../../standards/documentation.md) | 基线 |
| [Chinese (Simplified) Localization Style Guide](https://aka.ms/chinese-simplified-styleguide) | 中文语气、标点、空格、量词、界面用语 | [文档写作规范](../../standards/documentation.md) 第 3、8 节 | 基线 |
| [.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/) | C# 命名、类型与成员设计、异常 | [C# 设计规范](design-spec/csharp-design.md) | 基线 |
| [.NET runtime C# coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) | C# 代码格式 | [C# 设计规范](design-spec/csharp-design.md) | 基线 |
| [MCP 规范 2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18) 与 [2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28) | 工具结果与错误、工具名、注解、输出 schema、版本协商 | [V4 工具协议规范](design-spec/v4-protocol.md)、[MCP 工具设计规范](../../standards/design.md) | 基线（协议） |
| [Azure REST API Guidelines](https://github.com/microsoft/api-guidelines/blob/vNext/azure/Guidelines.md) | 错误模型（含 `target`、`innererror`）、命名、版本与弃用通知、可扩展枚举 | [V4 工具协议规范](design-spec/v4-protocol.md) 第 5、12、13 节；[MCP 工具设计规范](../../standards/design.md) 第 5、9 节 | 基线（协议的补充来源） |
| [Windows 应用设计指南](https://learn.microsoft.com/en-us/windows/apps/design/) | 键盘、焦点、高对比度、无障碍名称 | [工作台界面规范](../../standards/ui.md) | 基线 |
| [WCAG 2.2](https://www.w3.org/TR/WCAG22/) | AA 级对比度、键盘、焦点可见、目标大小 | [工作台界面规范](../../standards/ui.md) | 基线 |
| [Maintain an architecture decision record (ADR)](https://learn.microsoft.com/en-us/azure/well-architected/architect-role/architecture-decision-record) | 一页一个决定、只追加、备选方案、取舍、置信度 | [架构决策记录](../../standards/decisions/README.md)、[文档写作规范](../../standards/documentation.md) 第 14 节 | 基线 |
| Siemens TIA Portal Openness 文档（随包 `reference/siemens-openness/`）与各版本 PublicAPI | 只为官方 API 支持的能力注册工具；按发布键判断动作可用性 | [ADR 0012](../../standards/decisions/0012-old-release-tool-registration.md)、[ADR 0014](../../standards/decisions/0014-tool-counts-follow-official-api.md)、[官方 API 覆盖](../../reference/openness-coverage.md) | 基线（产品事实） |

## 补充

| 来源 | 采纳要点 | 落地位置 | 类别 |
|---|---|---|---|
| [Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)（Michael Nygard） | 背景、决定、状态、后果 | [ADR 模板](../../standards/decisions/0000-template.md) | 补充 |
| [语义化版本](https://semver.org/lang/zh-CN/) | 主、次、修订号的含义；预发布标签 | [CHANGELOG.md](../../../CHANGELOG.md)、[MCP 工具设计规范](../../standards/design.md) 第 9 节 | 补充 |
| [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) | 最新版本在前、`[Unreleased]` 段 | [CHANGELOG.md](../../../CHANGELOG.md) | 补充 |
| JSON Schema 2020-12（元模式 `https://json-schema.org/draft/2020-12/schema`） | 标准包校验；参数 schema 的 `required`、`enum`、`pattern`、`oneOf` 约定 | `schemas/standards/v1/`、[MCP 工具设计规范](../../standards/design.md) 第 4.2 节 | 补充 |
| Ant Design v5（链接待核实） | 工作台的视觉 token、组件和布局 | [ADR 0018](../../standards/decisions/0018-workbench-ant-design.md)、[工作台界面规范](../../standards/ui.md) | 补充 |
| EplanMCP（姊妹项目，本地仓库） | 文档目录结构、安全标准与威胁模型、工具规格模板、archify 图、工作台字体与风格 | [ADR 0021](../../standards/decisions/0021-workbench-eplanmcp-font-and-style.md)、[ADR 0026](../../standards/decisions/0026-documentation-structure-and-language.md)、[ADR 0027](../../standards/decisions/0027-archify-diagrams.md)、[安全标准](../../standards/security.md) | 补充 |
| STRIDE 威胁分类（链接待核实） | 仿冒、篡改、否认、信息泄露、拒绝服务、权限提升六类威胁 | [安全标准](../../standards/security.md) 第 4 节 | 补充 |
| archify（Claude Code 技能 `tt-a1i/archify`，链接与许可证待核实） | JSON 源生成可交互的架构、流程和时序图 | [ADR 0027](../../standards/decisions/0027-archify-diagrams.md) | 补充 |
| arc42 架构文档模板（链接待核实） | 目标、约束、上下文、构建块、运行时、部署、质量场景、风险 | 文档重组批次 D2a 中改写的架构说明 | 补充（尚未落地） |
| Amazon“单向门与双向门”（链接待核实） | ADR 中标出决定是否容易回退 | 文档重组批次 D3a 中的 ADR 模板字段 | 补充（尚未落地） |
| Anthropic 关于为 AI 代理编写工具的文章（链接待核实） | 少而精的工具；描述先写用途和前提；示例经过验证 | [MCP 工具设计规范](../../standards/design.md) | 补充 |
