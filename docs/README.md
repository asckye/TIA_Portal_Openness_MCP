# 文档目录

[项目首页](../README.zh-CN.md) · [English](../README.md)

## 入门

- [配置 TIA 服务与 AI 客户端](getting-started/configuration.md)：虚拟机、宿主机、本机连接，8 种客户端。
- [CLI 与 AI spec 提示词](getting-started/cli.md)：生成、增量修改、编译、预热和离线校验。

## 操作指南

- [完整 PLC + HMI 工程生成](guides/project-generation.md)
- [版本控制与 Git](guides/version-control.md)
- [在线实时读值](guides/online-monitoring.md)
- PLC：[模板及网络模式](guides/plc/templates.md)、[LAD](guides/plc/lad.md)、[SCL](guides/plc/scl.md)、[类型组](guides/plc/type-groups.md)
- HMI：[画面设计](guides/hmi/design.md)、[连接驱动](guides/hmi/connections.md)、[变量绑定](guides/hmi/tag-binding.md)、[只读迁移](guides/hmi/read-only-migration.md)、[全局脚本](guides/hmi/global-scripts.md)、[图形选择](guides/hmi/graphic-selection.md)、[运行设置](guides/hmi/runtime-settings.md)
- 工具专题：[PLC 构建](tools/plc-builders.md)、[硬件网络](tools/hardware-network.md)、[HMI 动作](tools/hmi-unified-actions.md)、[HMI 布局](tools/hmi-unified-theme-layout.md)

## 参考与排错

- [工具矩阵](reference/tool-matrix.md)、[能力与验收边界](reference/capabilities.md)、[自然语言配方](reference/natural-language-recipes.md)
- [错误模型](troubleshooting/errors.md)、[Openness 限制](troubleshooting/openness-limitations.md)、[HMI 快照诊断](troubleshooting/hmi-snapshots.md)
- [模板索引](../templates/README.md)、[AI 操作 skill](../tools/tiaportal-mcp/skill/SKILL.md)、[清单说明](../manifest/README.md)

## 开发与历史

- [结构与迁移对照](development/repository-layout.md)、[验证](development/validation.md)、[发布](development/release-workflow.md)、[界面检查记录](development/design-qa.md)
- [变更记录](../CHANGELOG.md)、[当前发布说明](releases/v2.7.17.md)、[v2.7.16](releases/v2.7.16.md)
- 历史：[v2.7.2–v2.7.15 发布记录](archive/release-notes.md)、[多语言修复](archive/multilingual-fix.md)、[v2.7.14 API 覆盖审计](archive/openness-audit-v2.7.14.md)

日常使用以入门和操作指南为准，归档保留当时结论。代码块内以 `docs/`、`runtime/`、`templates/`、`scripts/` 或 `tools/` 开头的路径相对仓库/交付包根目录。
