# 文档目录

[项目首页](../README.zh-CN.md) · [English](../README.md) · [完整包下载](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

## 开始使用

- [新手使用指南](getting-started/beginners.zh-CN.md)：安装准备、本机或虚拟机连接、选择 PLC、Studio 操作和首次 SCL 导入编译。
- [配置指南](getting-started/configuration.md)：客户端、连接方式、升级和排错。
- [CLI 指南](getting-started/cli.md)：V20/V21 的命令行生成、修改、导入导出和编译。
- [Studio](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/src/Studio/README.md)：直接调用 Openness 的桌面工具。

## 工具、版本与示例

当前 4.0 产品入口为根 `TiaOpenness.exe` 和八版 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`；V20/V21 的完整工具目录由 `worker/` 子目录中的引擎 worker 提供。当前能力页说明 V4 结果、写入审批、审计位置和原生验收边界。

- [4.0 源码版本范围](reference/version-tools.md) · [上一版生成的工具目录](reference/version-tool-catalog.md) · [上一版生成的完整引擎矩阵](reference/tool-matrix.md)
- [当前能力与验收边界](reference/capabilities.md) · [运行时布局、日志和审计目录](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/runtime-layout.md)
- [统一工具和语言示例](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/official-tool-usage.md)：`GetToolUsage` 的参数、完整编程文件、调用序列和官方来源。
- [能力边界](reference/capabilities.md) · [官方 API 覆盖与逐版功能缺口](reference/openness-coverage.md) · [真实工程验收记录](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md)
- [已接入生态工具](reference/ecosystem-tools.md)

## 操作专题

- [工程生成](guides/project-generation.md) · [硬件与网络](guides/hardware-network.md) · [版本控制](guides/version-control.md) · [在线读取](guides/online-monitoring.md)
- PLC：[模板](guides/plc/templates.md)、[SCL](guides/plc/scl.md)、[LAD](guides/plc/lad.md)、[类型组](guides/plc/builders.md)、[构造器](guides/plc/builders.md)、[Safety](guides/plc/safety.md)
- HMI：[画面](guides/hmi/design.md)、[连接](guides/hmi/connections.md)、[变量绑定](guides/hmi/tag-binding.md)、[变量删除](guides/hmi/tag-binding.md)、[全局脚本](guides/hmi/global-scripts.md)、[动作](guides/hmi/unified-actions.md)、[主题布局](guides/hmi/unified-theme-layout.md)、[图形选择](guides/hmi/graphic-selection.md)、[运行设置](guides/hmi/runtime-settings.md)、[只读迁移](guides/hmi/read-only-migration.md)
- [可选工作进程隔离](guides/openness-worker-isolation.md)

## 排错和维护

- [错误解释](troubleshooting/errors.md) · [Openness 限制和已知问题](troubleshooting/openness-limitations.md) · [HMI 快照](troubleshooting/hmi-snapshots.md)
- [源码结构](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/repository-layout.md) · [构建验证](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/validation.md) · [发布](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/release-workflow.md) · [当前交接](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/handoff.md) · [待完成工作](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/roadmap.md)
- [多版本架构](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/unified-version-framework.md) · [调用诊断](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/native-call-diagnostics.md) · [原生生命周期测试](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/native-lifecycle-tests.md) · [原生 MCP 测试](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/native-mcp-session-tests.md)
- [脚本](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/scripts/README.md) · [清单](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/README.md) · [模板](../templates/README.md) · [贡献](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/.github/CONTRIBUTING.md) · [支持](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/.github/SUPPORT.md)
- [当前版本说明](releases/v3.3.0.md) · [变更记录](../CHANGELOG.md) · [历次 GitHub Releases](https://github.com/asckye/TIA_Portal_Openness_MCP/releases) · [许可证](licenses/THIRD-PARTY-NOTICES.md)

当前文档只保留持续使用的说明。旧版本发布正文、临时审计和阶段交接可从 Git 历史查询。示例路径相对完整交付包或仓库根目录；运行时参数和各版示例以连接到的服务为准。
