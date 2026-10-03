# 文档目录

> Current multi-version development: [release tools, API audit and build instructions](reference/version-tools.md). Historical release/VM records below describe their original dates and do not authorize new native operations.


v3.1.0：[V20/V21 补充工具](reference/v20-v21-audit-tools.md) · [PLC 与生态工具](reference/ecosystem-tools.md) · [发布说明](releases/v3.1.0.md)。真实 TIA 验收边界见发布说明。

稳定性重点：[本地压力测试、原生验收边界及 GitHub 接入优先级](development/stability-and-integrations-20260930.md)。

2026-10-01：[工作进程隔离实现与最终本地验证结果](development/worker-isolation-validation-20261001.md)（故障注入、普通/隔离压力、原生未验收项）。

v3.0.0 生态扩展：[工具与使用说明](reference/ecosystem-tools.md) · [本地验证记录](development/ecosystem-integration-20260929.md)。

重点核对：[Siemens 官方指南与 API 流程审计](development/official-openness-audit-20260929.md)（32 个主题、已修复问题、待验收项）。

[项目首页](../README.zh-CN.md) · [English](../README.md)

## 入门

- [配置 TIA 服务与 AI 客户端](getting-started/configuration.md)：虚拟机、宿主机、本机连接，8 种客户端。
- [CLI 与 AI spec 提示词](getting-started/cli.md)：生成、增量修改、编译、预热和离线校验。

## 操作指南

- [Openness 工作进程隔离与故障恢复](guides/openness-worker-isolation.md)：可选开启，原生验收尚未完成。
- [完整 PLC + HMI 工程生成](guides/project-generation.md)
- [版本控制与 Git](guides/version-control.md)
- [在线实时读值](guides/online-monitoring.md)
- 硬件与网络：[硬件网络工具](guides/hardware-network.md)
- PLC：[模板及网络模式](guides/plc/templates.md)、[LAD](guides/plc/lad.md)、[SCL](guides/plc/scl.md)、[类型组](guides/plc/type-groups.md)、[构建器工具](guides/plc/builders.md)、[Safety（F 程序）](guides/plc/safety.md)
- HMI：[画面设计](guides/hmi/design.md)、[连接驱动](guides/hmi/connections.md)、[变量绑定](guides/hmi/tag-binding.md)、[只读迁移](guides/hmi/read-only-migration.md)、[全局脚本](guides/hmi/global-scripts.md)、[图形选择](guides/hmi/graphic-selection.md)、[运行设置](guides/hmi/runtime-settings.md)、[Unified 动作工具](guides/hmi/unified-actions.md)、[Unified 主题与布局](guides/hmi/unified-theme-layout.md)

## 参考与排错

- [TIA 版本分支与适用范围](reference/version-branches.md)（仅维护 master，历史版本分支已备份）
- [工具矩阵](reference/tool-matrix.md)、[能力与验收边界](reference/capabilities.md)、[真机台账（逐工具）](reference/real-machine-ledger.md)、[官方 API 覆盖清单](reference/openness-coverage.md)、[生态与参考资源](reference/ecosystem.md)、[自然语言配方](reference/natural-language-recipes.md)
- [错误模型](troubleshooting/errors.md)、[Openness 限制](troubleshooting/openness-limitations.md)、[HMI 快照诊断](troubleshooting/hmi-snapshots.md)
- [模板索引](../templates/README.md)、[AI 操作 skill](../tools/tiaportal-mcp/skill/SKILL.md)、[清单说明](../manifest/README.md)

## 开发与历史

- [Studio 直接调用 Openness 与全库重复检查](development/studio-native-and-duplicates-20261002.md)：移除 Studio MCP 层，V20/V21 原生构建、功能测试与剩余重叠。

- [结构与迁移对照](development/repository-layout.md)、[验证](development/validation.md)、[发布](development/release-workflow.md)、[贡献指南](../.github/CONTRIBUTING.md)、[支持](../.github/SUPPORT.md)、[安全策略](../.github/SECURITY.md)、[接续工作交接](development/handoff.md)（现状、下一步、每阶段固定动作、闸门、真机约定）、[交接历史](development/handoff-history.md)（逐版本记录、阶段收口清单）、[换机器交接单](development/handoff-checklist.md)（新机器准备、虚拟机现状、部署后按序要做的事、真机批跑工具）
- [路线图与待办](development/roadmap.md)：官方 API 全量对齐分阶段计划（阶段 1–5 Safety / WinCC Unified / Base / Step7 / 经典 WinCC 已于 2.7.25–2.7.37 收口，阶段 6 选件包进行中：⑥-① SiVArc 2.7.38、⑥-② Startdrive + DCC 2.7.39、⑥-③ SafetyValidation / Test Suite / Teamcenter / CFC 2.7.42 完成——阶段 6 收口，功能类型缺口全部归零）、引擎待重建项、第三方集成候选、合规事项（2026-09-17 审计，2026-09-19 更新）
- [第三方组件许可证清单](licenses/THIRD-PARTY-NOTICES.md)：随包分发的每个程序集的许可证与原文
- [变更记录](../CHANGELOG.md)、[当前发布说明](releases/v3.1.0.md)；历史发布说明按版本在 [releases/](releases/)（v2.7.16 起每版一篇，v2.7.2–v2.7.15 见下面的归档）
- 历史：[v2.7.2–v2.7.15 发布记录](archive/release-notes.md)（更早的多语言修复报告、v2.7.14 覆盖审计与配置器界面检查记录已删除，需要时看 Git 历史；覆盖审计的现行版本是[官方 API 覆盖清单](reference/openness-coverage.md)）

- [PLC 交叉引用退出调查（2026-09-30）](development/cross-reference-investigation-20260930.md)：官方重建流程、GitHub 查询修复、部署核对与证据缺口。
- [V20 / V21 缺陷与工具缺口核查（2026-09-30）](development/v20-v21-bugs-and-gaps-20260930.md)：两版 SDK 重新扫描、反射保护旁路、下载检查、导入验证及 V20 专用封装缺口。

日常使用以入门和操作指南为准，归档保留当时结论。代码块内以 `docs/`、`runtime/`、`templates/`、`scripts/` 或 `tools/` 开头的路径相对仓库/交付包根目录。

语言约定：面向用户的文档（入门、指南、参考、发布说明、各目录 README）以中文为主；治理文件（CONTRIBUTING、SECURITY、CODE_OF_CONDUCT）、`README.md`、自动生成的工具矩阵以及直接引用官方英文 API 名称的排错文档使用英文。同一文件内不混用两种主体语言（代码块和 API 标识符除外）。
