# TIA Portal Openness MCP

[English](README.md) · [新手使用指南](docs/getting-started/beginners.zh-CN.md) · [文档目录](docs/README.md) · [下载完整包](https://github.com/asckye/TIA_Portal_Openness_MCP/releases/latest)

通过 MCP 让 AI 操作西门子 TIA Portal，或使用包内 Studio 自己浏览、导入导出和编译工程。配置器与 Studio 均支持中文和英文。

![架构图](docs/assets/architecture.svg)

## 第一次使用

从 Releases 下载 **TIA_MCP_Delivery** ZIP，完整解压到较短的固定目录。GitHub 的 `Source code` 压缩包只含源码。

- **用 AI 操作：**双击 `TiaMcpConfigurator.exe`，选择实际安装的 TIA 版本和“同一台电脑”或“虚拟机 ↔ 宿主机”，选择实际使用的 MCP 客户端，写入配置后重启客户端并新建会话。
- **自己操作：**双击 `runtime\studio\TiaOpenness.exe`，连接前选择 TIA 版本，连接工程后选择目标 PLC。Studio 直接调用 Openness，无需配置 MCP。
- **先看演示：**在包根目录运行 `runtime\studio\TiaOpenness.exe --mock --lang zh`。演示使用模拟数据。

按[新手使用指南](docs/getting-started/beginners.zh-CN.md)完成安装、连接和第一个 SCL 导入编译练习；具体客户端配置见[配置指南](docs/getting-started/configuration.md)。

## 版本和工具

| TIA 版本 | 注册工具数 | MCP 实现 |
|---|---:|---|
| V14 SP1 | 57 | PLC 基础引擎 |
| V15.1 | 58 | PLC 基础引擎 |
| V16 / V17 / V18 | 各 60 | PLC 基础引擎 |
| V19 | 62 | PLC 基础引擎 |
| V20 | 477 | 完整引擎 |
| V21 | 488 | 完整引擎 |

V20/V21 默认 lite 档显示 63 个常用工具，其余通过 `FindTools` 查找、`CallTool` 调用，也可配置 `--profile full`。旧版本直接显示各自的基础目录，参数和返回值可能不同。准确范围见[逐版本工具说明](docs/reference/version-tools.md)及当前服务的 `tools/list`。V14 SP1、V15.1 不等于原版 V14、V15。

AI 调用不熟悉的工具时，用 `GetToolUsage(toolName, operation)` 获取本版参数、示例和结果解释；用 `language` 查编程语言示例，用 `exampleId` 取完整源码或调用序列。全部工具共用这套示例入口，官方 API 模式与本项目封装示例分别标明。

## 环境与验证

按需安装对应版本的 TIA Portal、Openness 和许可证。配置器及完整引擎需要 .NET Framework 4.8；基础引擎另外需要 .NET 8 和 ASP.NET Core 8；Studio 需要 .NET 10 Desktop Runtime。具体文件位置见[运行目录](runtime/README.md)。

八版本运行文件、Studio、协议、离线功能和官方 SDK/XSD 检查已有构建记录；新增功能的真实 TIA 工程验收仍为 **NOT RUN**。工具数量不等于完整覆盖所有 Siemens API，当前范围见[能力与验收说明](docs/reference/capabilities.md)。

## 开发和维护

仅维护 `master`。运行二进制随完整 Release 分发，不提交到 Git。准备 SDK 和伴随 Python 环境后，使用 `Build-MultiVersion.ps1 -Test` 构建全部版本。

[构建与验证](docs/development/validation.md) · [发布流程](docs/development/release-workflow.md) · [当前交接](docs/development/handoff.md) · [变更记录](CHANGELOG.md) · [第三方许可证](docs/licenses/THIRD-PARTY-NOTICES.md)
