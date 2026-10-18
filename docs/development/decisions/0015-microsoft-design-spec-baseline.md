# 0015 项目设计规范以 Microsoft 规范为基线

- 状态：已采纳
- 日期：2026-10-11
- 决策者：维护者
- 置信度：高
- 相关：[设计规范索引](../design-spec/README.md)；[C# 与 .NET API 设计规范](../design-spec/csharp-design.md)；[V4 工具协议规范](../design-spec/v4-protocol.md)；[工作台界面设计规范](../design-spec/workbench-ui.md)；[文档写作规范](../design-spec/documentation-style.md)；[ADR 0016](0016-architecture-decision-records.md)；任务 P8-60–P8-64；[ADR 0018](0018-workbench-ant-design.md)（工作台视觉规范改为 Ant Design v5）

## 背景

到 2026-10-11，代码、协议、工作台和文档分别沿用各自的历史做法：C# 有多个命名空间根和两种字段写法；V4 协议中同一概念在不同工具里叫法不一，错误码有时被当作兜底；工作台不支持高对比主题，读屏无法识别大部分控件；文档的术语和工具数量互相矛盾。仓库规则（`CLAUDE.md`、`AGENTS.md`）规定流程，但没有统一的设计标准，新工作没有可以引用的规则。

## 选项

1. 编写全新的项目规范：完全贴合项目；每条规则都要自行论证，也难以对照外部工具检查。
2. 以成熟的外部规范为基线，只补充项目特有的规则并记录取舍：规则可以引用，可以用 SDK 分析器和无障碍工具检查；部分规则需要按 MCP 与桌面场景解释。
3. 不写规范，只靠评审：没有前期工作；一致性无法检查。

## 决定

采用选项 2，以 Microsoft 规范为基线：

- C# 代码与 API：.NET Framework Design Guidelines，加 `.editorconfig` 与 .NET SDK 自带的分析器；
- V4 工具协议：MCP 规范为主，Microsoft REST API Guidelines 为辅（错误、分页、长操作、版本）；
- 工作台：Windows 应用设计指南，并以 WCAG 2.2 AA 作为可测试的无障碍标准；
- 文档：Microsoft Writing Style Guide 与 Microsoft 简体中文本地化风格指南。

规范对新工作是规范性的；现有差异记入偏差清单，在 4.0 前逐条决定为 4.0 前修改、4.0 后修改或有意偏差，不批量改动对外接口。同日的逐项决定（MCP 修订、工具注解、错误参数、工作台无障碍、旧版 worker 超时、分析器、命名空间、行长与文档）见 [设计规范索引](../design-spec/README.md#决定摘要)。

## 后果

- 正面：新工作有可引用、可检查的规则；现有差异集中记录，并且每条都有状态。
- 负面与取舍：4.0 前增加 P8-60–P8-64 五项任务；部分差异长期保留为有意偏差，规范中为此写明了项目取舍。
- 后续工作：P8-60 协议、P8-61 工作台、P8-62 C#、P8-63 文档（与 P7-11b 一起）、P8-64 旧版 worker 超时。
- 验证方式：四份偏差清单的状态列；P8-62 加入的 `.editorconfig` 与分析器配置；工作台的无障碍自动检查（A11Y-14）；仓库检查。
