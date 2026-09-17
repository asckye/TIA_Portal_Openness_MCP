# 生态与参考资源

[文档目录](../README.md) · [路线图](../development/roadmap.md) · [能力与验收边界](capabilities.md)

本页登记 2026-09-17 审计中评估过、对本项目有用但**不进入代码**的外部资源：数据集与基准（skill 调优素材）、官方工具、可搭配的第三方项目，以及许可证红线。进入代码的候选（PLCSIM Advanced、S7 Web 服务器 API、Open Pipe、语义 diff、TODO 扫描、SCL 自测试模板、块可视化、SCL 预检、程序文档、AML 生成、写保护钩子）的落地状态见路线图 §5。

## 数据集与基准（skill 调优）

| 资源 | 内容 | 许可证 | 用途 |
|---|---|---|---|
| [AutoPLC](https://github.com/cangkui/AutoPLC) | 西门子 SCL API 知识库、标注案例、914 项任务基准 | MIT（内嵌案例逐案核实：OSCAT 为 LGPL、LGF 为 Siemens） | 校准 `tools/tiaportal-mcp/skill` 的 SCL 生成提示与回归；不复制案例进仓库 |
| [Agents4PLC 数据集 v2](https://huggingface.co/datasets/Luoji-zju/Agents4PLC_dataset_v2) | 可验证的 IEC 61131-3 ST 任务 | Apache-2.0 | 生成代码质量的离线回归（ST → SCL 需方言转换） |
| [awesome-structured-text](https://github.com/topics/structured-text) | ST/SCL 资源索引 | 各自 | 生态地图 |

## 官方工具与文档

| 资源 | 用途 |
|---|---|
| [TIA Openness Explorer（SIOS 109760816）](https://support.industry.siemens.com/cs/document/109760816) | 浏览 Openness 对象树的属性/方法，开发新工具前验证属性路径 |
| [TIA Portal Openness 在线手册（V21）](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows) | 本项目 [官方 API 覆盖清单](openness-coverage.md) 的对照基线 |
| [S7-PLCSIM Advanced 用户手册](https://support.industry.siemens.com/cs/products/6es7823-1fa04-0ya5/s7-plcsim-advanced) | `Simulation` 域工具所用 .NET API（`Siemens.Simatic.Simulation.Runtime`）的成员定义 |
| [WinCC Unified Open Pipe 手册（109803794）](https://support.industry.siemens.com/cs/attachments/109803794/WinCCRTUOpPenUS_en-US.pdf) | `UnifiedOpenPipeRequest` 等工具的消息格式 |
| [TIA Selection Tool → TIA Portal AML（109748223）](https://support.industry.siemens.com/cs/document/109748223) | `BuildDeviceAmlDocument` / `ImportDeviceAml` 的规划数据来源 |
| [Siemens 官方 code-snippets](https://github.com/tia-portal-applications) | Openness 用法参考 |

## 可搭配的第三方项目（不并入代码）

| 项目 | 许可证 | 关系 |
|---|---|---|
| [Czarnak/tia-git-addin](https://github.com/Czarnak/tia-git-addin) | MIT | SimaticML 结构化 diff 思路已自研为 `ComparePlcBlockDocuments` |
| [Czarnak/totally-integrated-claude](https://github.com/Czarnak/totally-integrated-claude) | MIT | 写保护钩子思路已实现为 `hooks/tia-write-guard.ps1` |
| [cmariusz/TiaImportExport.VSExt](https://github.com/cmariusz/TiaImportExport.VSExt) | MIT | TS 版 LAD/FBD 渲染器；本项目改为 `RenderPlcBlockDocument` 的 Mermaid 输出 |
| [Lorenz-Software/PLCSIM.UnitTest](https://github.com/Lorenz-Software/PLCSIM.UnitTest) | MIT | 单元测试思路已实现为 `RunPlcSimAdvancedTestScenario` |
| [core-engineering/siemens-plc-tools](https://github.com/core-engineering/siemens-plc-tools) | MIT | MkDocs/Draw.io 文档生成；本项目提供 `GeneratePlcDocumentation` 的单文件 Markdown |
| [vogler75/winccua-mcp-server](https://github.com/vogler75/winccua-mcp-server) | GPL-3.0 | 基于 GraphQL 的 Unified 运行时 MCP，可与本项目并列使用，不可并入 |
| [Aml.Engine](https://www.nuget.org/packages/Aml.Engine) | MIT（二进制） | 复杂 AML 生成/校验时可另行引入；本项目的 `BuildDeviceAmlDocument` 不依赖它 |

## 许可证红线

以下项目的代码不得进入本 MIT 项目：rickgaiser/TiaMcp（AGPL-3.0）、TUM-AIS/IEC611313ANTLRParser（GPL-3.0）、Parozzz/TiaUtilities（GPL-3.0）、node-red-contrib-s7（GPL-3.0）、mking2203/CodeGeneratorOpenness（GPL-3.0）、Codyte/Tia-Portal-CLI（AGPL + 商业）；DotNetSiemensPLCToolBoxLibrary（LGPL-2.1）与 iec-checker / rusty（LGPL-3.0）只能动态链接或子进程；OPC Foundation UA-.NETStandard 源码为非会员 GPL-2.0（NuGet 二进制可用）；无 LICENSE 的仓库（huahaizo/tia-portal-openness-ai、Gitee TIAOpennessTools、chewcw/tia-portal-openness-mcpserver）只能作设计参考。
