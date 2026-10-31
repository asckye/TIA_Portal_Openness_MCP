# TIA Portal Openness MCP

[English](README.md) · [新手使用指南](docs/getting-started/beginners.zh-CN.md) · [文档目录](docs/README.md) · [下载完整包](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

同一个 TIA Portal 工作台提供 **MCP 与客户端** 和 **工程操作** 两个页面，支持中文和英文。配置 AI 连接、启停服务、浏览工程、导入导出和编译均在同一个主窗口内完成。

交付包包含工作台；从包根打开 `TiaOpenness.exe`。本页说明尚未发布的 4.0 源码；已下载版本以包内文档为准。

![MCP 客户端与工作台共享 FoundationHost 会话，由匹配版本的 Openness worker 操作 TIA](docs/assets/architecture.svg)

## 第一次使用

从 Releases 下载 **TIA_MCP_Delivery** ZIP，完整解压到较短的固定目录。GitHub 的 `Source code` 压缩包只含源码。

交付包只包含运行文件、用户文档、模板、Claude Code 插件及生态资源；开发源码、构建脚本和验证工具保留在源码仓库。

- **用 AI 操作**：双击 `TiaOpenness.exe`，在 **MCP 与客户端** 页面选择实际安装的 TIA 版本和“同一台电脑”或“虚拟机 ↔ 宿主机”，选择实际使用的 MCP 客户端，写入配置后重启客户端并新建会话。
- **自己操作**：打开同一个 `TiaOpenness.exe`，切换到 **工程操作**，连接前选择顶部的 TIA 版本，连接工程后选择目标 PLC。工程操作页附着 MCP 会话，使用同一套工具、审批和审计；没有 AI 会话时可创建工作台所属的宿主会话。
- **先看演示**：在包根目录运行 `TiaOpenness.exe --mock --lang zh`。演示使用模拟数据。

按[新手使用指南](docs/getting-started/beginners.zh-CN.md)完成安装、连接和第一个 SCL 导入编译练习；具体客户端配置见[配置指南](docs/getting-started/configuration.md)。

已在使用 3.3：4.0 起初不经 3.3 的更新器提供，需要并排手动安装，见[从 3.3 迁移到 4.0](docs/guides/migrate-3.3-to-4.0.md)。在真实 TIA 上测试预发布版本时，按[预发布真机验证清单](docs/guides/beta-tester-checklist.md)记录和报告。

## 版本和工具

<!-- generated:tool-counts:begin -->
由 `generate-tool-counts` 从 `manifest/version-tools.json` 和 `ToolProfiles.resx` 生成。以运行时 `tools/list` 为准。

| TIA 版本 | 注册工具数 | lite 档 |
|---|---:|---:|
| V14 SP1 | 279 | 不适用 |
| V15.1 | 319 | 不适用 |
| V16 | 348 | 不适用 |
| V17 | 371 | 不适用 |
| V18 | 397 | 不适用 |
| V19 | 473 | 不适用 |
| V20 | 496 | 81 |
| V21 | 511 | 84 |
<!-- generated:tool-counts:end -->

V20/V21 默认使用 lite 档，其余通过 `FindTools` 查找、`CallTool` 调用，也可配置 `--profile full`。八版统一入口为 `runtime/v<发布键>/TiaMcp.FoundationHost.exe --release-key <发布键>`；宿主自动使用 `worker/` 下该版本的 PLC worker（V20/V21 的完整目录也由它执行），八版共有工具采用同一份 Foundation 代码、参数和用法。准确范围见[逐版本工具说明](docs/reference/version-tools.md)及当前服务的 `tools/list`。V14 SP1、V15.1 不等于原版 V14、V15。

AI 调用不熟悉的工具时，用 `GetToolUsage(toolName, operation)` 获取本版参数、示例和结果解释；用 `language` 查编程语言示例，用 `exampleId` 取完整源码或调用序列。全部工具共用这套示例入口，官方 API 模式与本项目封装示例分别标明。

## 环境与验证

按需安装对应版本的 TIA Portal、Openness 和许可证。工作台和宿主所需的 .NET 10 运行时已随包附带（`runtime/dotnet`）；Openness 桥接进程和 PLC worker 需要 .NET Framework 4.8（Windows 10 1903 及以后已自带）。具体文件位置见[运行目录](runtime/README.md)。

八版本运行文件、工作台、协议、离线功能和官方 SDK/XSD 检查已有构建记录；新增功能的真实 TIA 工程验收仍为 **NOT RUN**。工具数量不等于完整覆盖所有 Siemens API，当前范围见[能力与验收说明](docs/reference/capabilities.md)。

## 开发和维护

仅维护 `master`。运行二进制随完整 Release 分发，不提交到 Git。准备 SDK 和伴随 Python 环境后，使用 `dotnet run --project build-tools/release -- build-multi-version -Test` 构建全部版本。

[构建与验证](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/validation.md) · [发布流程](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/release-workflow.md) · [当前交接](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/development/handoff.md) · [变更记录](CHANGELOG.md) · [第三方许可证](docs/licenses/THIRD-PARTY-NOTICES.md)
