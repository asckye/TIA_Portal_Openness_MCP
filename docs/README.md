# 文档目录

[项目首页](../README.zh-CN.md) · [English](../README.md)

## 入门

- [配置 TIA 服务与 AI 客户端](getting-started/configuration.md)：虚拟机、宿主机、本机连接，8 种客户端。
- [CLI 与 AI spec 提示词](getting-started/cli.md)：生成、增量修改、编译、预热和离线校验。

## 操作指南

- [完整 PLC + HMI 工程生成](guides/project-generation.md)
- [版本控制与 Git](guides/version-control.md)
- [在线实时读值](guides/online-monitoring.md)
- 硬件与网络：[硬件网络工具](guides/hardware-network.md)
- PLC：[模板及网络模式](guides/plc/templates.md)、[LAD](guides/plc/lad.md)、[SCL](guides/plc/scl.md)、[类型组](guides/plc/type-groups.md)、[构建器工具](guides/plc/builders.md)、[Safety（F 程序）](guides/plc/safety.md)
- HMI：[画面设计](guides/hmi/design.md)、[连接驱动](guides/hmi/connections.md)、[变量绑定](guides/hmi/tag-binding.md)、[只读迁移](guides/hmi/read-only-migration.md)、[全局脚本](guides/hmi/global-scripts.md)、[图形选择](guides/hmi/graphic-selection.md)、[运行设置](guides/hmi/runtime-settings.md)、[Unified 动作工具](guides/hmi/unified-actions.md)、[Unified 主题与布局](guides/hmi/unified-theme-layout.md)

## 参考与排错

- [工具矩阵](reference/tool-matrix.md)、[能力与验收边界](reference/capabilities.md)、[官方 API 覆盖清单](reference/openness-coverage.md)、[生态与参考资源](reference/ecosystem.md)、[自然语言配方](reference/natural-language-recipes.md)
- [错误模型](troubleshooting/errors.md)、[Openness 限制](troubleshooting/openness-limitations.md)、[HMI 快照诊断](troubleshooting/hmi-snapshots.md)
- [模板索引](../templates/README.md)、[AI 操作 skill](../tools/tiaportal-mcp/skill/SKILL.md)、[清单说明](../manifest/README.md)

## 开发与历史

- [结构与迁移对照](development/repository-layout.md)、[验证](development/validation.md)、[发布](development/release-workflow.md)、[接续工作交接](development/handoff.md)（换机器继续 API 对齐计划时先读：现状、下一步、每阶段固定动作、闸门、真机约定）
- [路线图与待办](development/roadmap.md)：官方 API 全量对齐分阶段计划（阶段 1–5 Safety / WinCC Unified / Base / Step7 / 经典 WinCC 已于 2.7.25–2.7.37 收口，阶段 6 选件包进行中：⑥-① SiVArc 2.7.38、⑥-② Startdrive + DCC 2.7.39、⑥-③ SafetyValidation / Test Suite / Teamcenter / CFC 2.7.42 完成——阶段 6 收口，功能类型缺口全部归零）、引擎待重建项、第三方集成候选、合规事项（2026-09-17 审计，2026-09-19 更新）
- [第三方组件许可证清单](licenses/THIRD-PARTY-NOTICES.md)：随包分发的每个程序集的许可证与原文
- [变更记录](../CHANGELOG.md)、[当前发布说明](releases/v2.7.43.md)、[v2.7.42](releases/v2.7.42.md)、[v2.7.41](releases/v2.7.41.md)、[v2.7.40](releases/v2.7.40.md)、[v2.7.39](releases/v2.7.39.md)、[v2.7.38](releases/v2.7.38.md)、[v2.7.37](releases/v2.7.37.md)、[v2.7.36](releases/v2.7.36.md)、[v2.7.35](releases/v2.7.35.md)、[v2.7.34](releases/v2.7.34.md)、[v2.7.33](releases/v2.7.33.md)、[v2.7.32](releases/v2.7.32.md)、[v2.7.31](releases/v2.7.31.md)、[v2.7.30](releases/v2.7.30.md)、[v2.7.29](releases/v2.7.29.md)、[v2.7.28](releases/v2.7.28.md)、[v2.7.27](releases/v2.7.27.md)、[v2.7.26](releases/v2.7.26.md)、[v2.7.25](releases/v2.7.25.md)、[v2.7.24](releases/v2.7.24.md)、[v2.7.23](releases/v2.7.23.md)、[v2.7.22](releases/v2.7.22.md)、[v2.7.21](releases/v2.7.21.md)、[v2.7.20](releases/v2.7.20.md)
- 历史：[v2.7.2–v2.7.15 发布记录](archive/release-notes.md)、[多语言修复](archive/multilingual-fix.md)、[v2.7.14 API 覆盖审计](archive/openness-audit-v2.7.14.md)、[v2.7.16 配置器界面检查记录](archive/design-qa.md)

日常使用以入门和操作指南为准，归档保留当时结论。代码块内以 `docs/`、`runtime/`、`templates/`、`scripts/` 或 `tools/` 开头的路径相对仓库/交付包根目录。

语言约定：面向用户的文档（入门、指南、参考、发布说明、各目录 README）以中文为主；治理文件（CONTRIBUTING、SECURITY、CODE_OF_CONDUCT）、`README.md`、自动生成的工具矩阵以及直接引用官方英文 API 名称的排错文档使用英文。同一文件内不混用两种主体语言（代码块和 API 标识符除外）。
