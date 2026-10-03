# 仓库结构

[文档目录](../README.md) · [当前交接](handoff.md)

| 路径 | 用途 |
|---|---|
| `TiaPortalOpenness.slnx` | 69 个开发工程，按 Engine、PlcAdapters、WorkerProtocol、Studio、Tools、Tests 分组；不包含 reference 示例、LibraryRenameProbe 和必须逐版本构建的 PlcWorker |
| `TiaPortalOpenness.Offline.slnx` | 48 个不需要 Siemens 程序集的工程，包含 CI 离线套件和 Studio 客户端；完整构建需要 Windows/.NET 10 SDK |
| `tools/openness-shared/TiaPublicApi.props` | 按精确版本查找本机 PublicAPI 的共享路径表；由需要 SDK 的工程显式导入并由发布脚本收录源码哈希 |
| `Version.props` | 产品发布版本的唯一来源；引擎、基础宿主、Studio 显式导入，配置器构建脚本读取 |
| `tools/tiaportal-mcp` | V20/V21 完整引擎、Foundation、精确版本 worker 和测试 |
| `tools/tiaportal-mcp/src/TiaMcp.Logic` | net48/net8.0 纯逻辑库；共享 XML/JSON Builders、MCP 策略和运行通道数据转换，不引用 Siemens 或 MCP SDK |
| `tools/tia-openness-studio/src/TiaOpenness.Launcher` | 仅保留兼容启动器 `Launcher.cs`，由 Framework csc 编译 |
| `tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration` | 编译型 WPF 配置页、配置逻辑及中英资源字典；使用 Studio 调色板 |
| `tools/tia-openness-studio/src/TiaOpenness.Gui/Themes/Glass.xaml`、`Controls/GlassLogView.cs`、`Fonts` | 工作台内的 Glass 样式、日志视图和字体；字体许可及来源记录与字体同目录 |
| `tools/tia-openness-studio/tests/TiaOpenness.Configuration.Tests` | .NET 10 配置控制台测试 |
| `tools/tia-openness-studio` | 直接调用 Openness 的 Studio、桥接进程和八个适配器 |
| `tools/openness-shared` | 无 Siemens 版本依赖的环境信息、参数、诊断、导入依赖规划和统一示例库 |
| `tools/third-party` | 固定版本的第三方组件及其许可证 |
| `runtime/v14sp1`、`runtime/v15.1`、`runtime/v16`–`runtime/v21` | 构建生成的八个 MCP 运行目录 |
| `runtime/studio` | 统一桌面程序及 `bridge/adapters` 内的八版适配器 |
| `TiaMcpConfigurator.exe` | 统一工作台入口（保留原文件名），不再打开独立配置器窗口 |
| `reference/tool-examples` | 可编辑的调用、语言文件、返回解释和调用顺序 |
| `reference/siemens-openness`、`reference/siemens-code-snippets` | 固定来源的官方示例与授权记录 |
| `reference/version-feature-matrix.json` | 按版本区分实现、缺口和原生验收的功能证据 |
| `templates` | 编程及工程模板 |
| `manifest` | 生成的版本/工具/构建/交付哈希证据；`publication-v*.json` 保留在此 |
| `manifest/history` | 一次性日期取证记录，保留原始内容和适用版本 |
| `scripts` | build、checks、generate、diagnostics、operations 分类脚本 |
| `.github` | Actions、贡献/安全/行为规范 |
| `.claude-plugin`、`hooks` | 插件定义和 Claude Code 钩子 |

运行二进制不进入 Git；使用[发布流程](release-workflow.md)生成完整交付包。
Siemens PublicAPI 是本机构建输入，默认查找仓库根目录下的八版 `TIA_V*_PublicAPI`，也可通过
`TiaPublicApiRoot` 指定这些文件夹的父目录；单工程显式 `SiemensEngineeringDirectory` 优先。
具体命令见[验证分层](validation.md)。SDK 不随仓库或公开交付包分发。
`bin-build`、本机输出、用户工程、客户端配置及设计交接素材不属于版本化源码。

## 测试工程

`tools/tiaportal-mcp/tests/TiaMcp.Adapters.DiagnosticsTests/Diagnostics.Tests.csproj`
是适配器的 net8.0 离线诊断控制台测试，程序集名保留为 `Diagnostics.Tests`，沿用逻辑库的
`InternalsVisibleTo`。运行时需要 worker bin 根目录和一个尚不存在的日志目录；命令见
[适配器说明](../../tools/tiaportal-mcp/src/TiaMcp.Adapters/README.md#input-regression-checks)。

以下夹具均位于 `tools/tiaportal-mcp/tests/`，不连接 TIA；两个 TransportFixture 的协议不同，保留名称，
不合并：

| 工程目录 | 协议与用途 |
|---|---|
| `TiaMcpServer.TransportFixture` | 生产 Foundation worker 的行 JSON 替身，由 `scripts/checks/Test-FoundationTransport.py` 使用，并由 `.github/workflows/offline-checks.yml` 和 `scripts/build/Build-MultiVersion.ps1` 构建、调用检查 |
| `TiaMcp.TransportFixture` | `TiaMcp.WorkerProtocol.*` 的预览帧协议传输夹具，用于测试带长度前缀的帧及传输故障 |
| `TiaMcp.EndpointFixture` | 同一预览帧协议的端点夹具，用于测试端点状态、请求分派与绑定检查 |

`TiaMcp.WorkerProtocol.*` 尚未接入生产 worker；是否采用或删除由[重构计划](refactor-plan.md) P4-01 决定。

## 文档入口

| 路径 | 维护内容 |
|---|---|
| `docs/getting-started` | 新手指南、配置与 CLI |
| `docs/guides` | PLC/HMI、硬件网络、版本控制与具体工作流 |
| `docs/reference` | 版本工具矩阵、能力、API 缺口及生态参考 |
| `docs/troubleshooting` | 错误定位、Openness 限制与诊断 |
| `docs/development` | 当前架构、交接、验证、发布与路线图；[design-qa.md](design-qa.md) 记录界面设计验收 |
| `docs/releases`、`docs/archive` | 发布记录及历史资料 |
| `docs/licenses` | 再分发组件的许可证清单和原文 |

开发目录只维护当前入口。已失效的源代码候选页、一次性日期审计、旧机器状态和重复交接页由 Git
历史保存；其有效结论并入当前架构、验证或限制页。机器可读 API 证据与官方示例语料继续保留。

## 完整引擎源码

`tools/tiaportal-mcp/src/TiaMcpServer` 中的 `ModelContextProtocol/Tools` 声明工具，
纯算法位于 `TiaMcp.Logic/ModelContextProtocol/Builders`，引擎的 `ModelContextProtocol/Builders`
保留依赖工程环境或原生调用的编排；`Siemens/Portal` 放工程调用，`Siemens/Hmi`
放 HMI 访问层；`Runtime` 是 PLC/OPC UA/Web API/Open Pipe 通道，`Cli` 是命令入口。
新增原生能力应同时检查工具声明、类型化调用、版本策略、示例及有意义的功能验证。

所有 C# 源码使用无 BOM UTF-8、仓库内 LF。宿主和离线测试通过项目引用复用 `TiaMcp.Logic`，尚未迁移的
引擎源文件继续以相对路径链接。`tools/openness-shared` 保持共享源码链接；统一示例资源嵌入读取它的
逻辑库。原生调用诊断和接触 Openness 对象的代码保留在受插桩的引擎程序集内。Foundation 与完整引擎
使用不同契约；共享边界见[版本框架](unified-version-framework.md)。
