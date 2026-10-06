# 仓库结构

[文档目录](../README.md) · [当前交接](handoff.md)

仅维护 `master`；旧版本分支记录由 Git 历史保留。

| 路径 | 用途 |
|---|---|
| `TiaPortalOpenness.slnx` | 51 个开发工程，按 Engine、PlcAdapters、Studio、Tools、Tests 分组；不包含 reference 示例、LibraryRenameProbe 和必须逐版本构建的 PlcWorker |
| `TiaPortalOpenness.Offline.slnx` | 30 个不需要 Siemens 程序集的工程，包含 CI 离线套件和 Studio 客户端；完整构建需要 Windows/.NET 10 SDK |
| `src/Shared/TiaPublicApi.props` | 按精确版本查找本机 PublicAPI 的共享路径表；由需要 SDK 的工程显式导入并由发布脚本收录源码哈希 |
| `Version.props` | 产品发布版本的唯一来源；引擎、基础宿主、Studio 显式导入，配置器构建脚本读取 |
| `src/Engine`、`src/FoundationHost`、`src/Worker` | V20/V21 完整引擎、六版 Foundation 宿主及八版 worker；程序集、命名空间和 EXE 名称不变 |
| `src/Adapters`、`src/Adapters.Contracts`、`src/Runtime`、`src/WorkerChannel` | 类型化适配器、契约、运行通道与进程通道 |
| `src/Logic` | net48/net10.0 纯逻辑库；共享 XML/JSON Builders、MCP 策略和运行通道数据转换，不引用 Siemens 或 MCP SDK |
| `src/Studio/Launcher` | 仅保留兼容启动器 `Launcher.cs`，由 Framework csc 编译 |
| `src/Studio/Gui/Configuration` | 编译型 WPF 配置页、配置逻辑及中英资源字典；使用 Studio 调色板 |
| `src/Studio/Gui/Themes/Glass.xaml`、`Controls/GlassLogView.cs`、`Fonts` | 工作台内的 Glass 样式、日志视图和字体；字体许可及来源记录与字体同目录 |
| `tests/Studio/TiaOpenness.Configuration.Tests` | .NET 10 配置控制台测试 |
| `src/Studio` | 直接调用 Openness 的 Studio、桥接进程和八个适配器 |
| `src/Shared` | 无 Siemens 版本依赖的安装布局解析、环境信息、参数、诊断、导入依赖规划和统一示例库 |
| `third_party` | 固定版本的第三方组件及其许可证 |
| `third_party/tia-openness-studio` | Studio 上游 LICENSE、`upstream.json` 和完整源码/历史归档；来源记录内的导入路径仍相对原上游目录 |
| `build-tools/native-call-weaver` | 仅构建期使用的原生调用点诊断插桩工具 |
| `tests/Engine`、`tests/Studio`、`tests/test-suites.json` | 功能、协议、API、诊断、离线及可选原生测试；测试工程目录及工程文件保留原名 |
| `plugin/skill` | Claude Code 插件技能，引导 AI 使用当前版本的 GetToolUsage 示例 |
| `scripts/operations/vci-watch` | 可选 V20/V21 工作区导出与本地 Git 提交脚本，不纳入交付包 |
| `docs/development/evidence` | 机器审计和 legacy 历史证据 |
| `runtime/v14sp1`、`runtime/v15.1`、`runtime/v16`–`runtime/v21` | 构建生成的八个 MCP 运行目录 |
| `runtime/studio` | 统一桌面程序及 `bridge/adapters` 内的八版适配器 |
| `runtime/dotnet` | 构建时由 `scripts/build/Get-BundledDotnet.ps1` 从微软官方压缩包展开的 .NET 10 运行时，不入 Git |
| `TiaOpenness.exe` | 统一工作台入口（程序集名 `TiaOpenness.Launcher`），不再打开独立配置器窗口 |
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

## 运行时资源定位

引擎与 Studio 使用 [`BundleLayout.cs`](../../src/Shared/BundleLayout.cs) 从已知安装/开发输出定位
含 `manifest/package-manifest.json` 的包根。完整包放在仓库外仍可使用，无需 `.git`。
指南、Python 桥接和 CLI 模板相对包根读取；V21 生态目录直接嵌入 V20/V21 引擎，随包 JSON 仅是参考副本。
包根只由 `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` 或已知安装锚点决定（见[运行时布局](runtime-layout.md)）；旧 `TIA_MCP_REPOSITORY_ROOT` 已删除。

安装引擎在 `runtime/<RuntimeDirectory>`；Studio 在 `runtime/studio`，原生桥接及其按版本的适配器在
`runtime/studio/bridge` 及其 `adapters` 子目录。版本路径取自 `TiaVersionCatalog`，生成的二进制不列入交付资源代码表。
开发输出候选、各调用方保留到 4.0 的兼容回退及新增资源流程见[运行时布局](runtime-layout.md)。
新增调用方复用解析器，不新增仓库结构探测。D334 只迁移源码目录及路径引用；产品入口与运行资源布局仍由 P6-36～39 处理。

`src/Studio/Directory.Build.props` 保留 Studio 公共构建设置；
`tests/Studio/Directory.Build.props` 显式导入它，保持原先的导入顺序及测试包引用。
`tests/Engine/Shared` 保留 xunit 共享适配器。原 `tools/README.md` 的源码与工具导航已并入上表，
仓库不再保留 `tools/`。发布脚本将构建日志写入 `bin-build/releases/v<Version>/`。

## 测试工程

`tests/Engine/TiaMcp.Adapters.DiagnosticsTests/Diagnostics.Tests.csproj`
是适配器的 net10.0 离线诊断控制台测试，程序集名保留为 `Diagnostics.Tests`，沿用逻辑库的
`InternalsVisibleTo`。运行时需要 worker bin 根目录和一个尚不存在的日志目录；命令见
[适配器说明](../../src/Adapters/README.md#input-regression-checks)。

`tests/Engine/TiaMcpServer.TransportFixture` 是 Foundation 协议 2 的行 JSON 替身，
由 `worker-channel`、`scripts/checks/Test-FoundationTransport.py` 使用，并由 CI 与
`scripts/build/Build-MultiVersion.ps1` 构建。夹具不连接 TIA；第 A 步删除了无生产调用方的预览协议
与两个预览夹具，规则映射见[适配器设计](adapter-merge.md#第-a-步最终规则核对)。

## 文档入口

| 路径 | 维护内容 |
|---|---|
| `docs/getting-started` | 新手指南、配置与 CLI |
| `docs/guides` | PLC/HMI、硬件网络、版本控制与具体工作流 |
| `docs/reference` | 版本工具矩阵、能力、API 缺口及生态参考 |
| `docs/troubleshooting` | 错误定位、Openness 限制与诊断 |
| `docs/development` | 当前架构、交接、验证、发布与路线图；Studio 工作台行为见 [`src/Studio/README.md`](../../src/Studio/README.md) |
| `docs/releases`、`docs/archive` | 发布记录及历史资料 |
| `docs/licenses` | 再分发组件的许可证清单和原文 |

开发目录只维护当前入口。已失效的源代码候选页、一次性日期审计、旧机器状态和重复交接页由 Git
历史保存；其有效结论并入当前架构、验证或限制页。机器可读 API 证据与官方示例语料继续保留。

## 完整引擎源码

`src/Engine` 中的 `ModelContextProtocol/Tools` 声明工具，
纯算法位于 `src/Logic/ModelContextProtocol/Builders`，引擎的 `ModelContextProtocol/Builders`
保留依赖工程环境或原生调用的编排；`Siemens/Portal` 放工程调用，`Siemens/Hmi`
放 HMI 访问层；`Runtime` 是 PLC/OPC UA/Web API/Open Pipe 通道，`Cli` 是命令入口。
新增原生能力应同时检查工具声明、类型化调用、版本策略、示例及有意义的功能验证。

所有 C# 源码使用无 BOM UTF-8、仓库内 LF。宿主和离线测试通过项目引用复用 `TiaMcp.Logic`，尚未迁移的
引擎源文件继续以相对路径链接。`src/Shared` 保持共享源码链接；统一示例资源嵌入读取它的
逻辑库。原生调用诊断和接触 Openness 对象的代码保留在受插桩的引擎程序集内。Foundation 与完整引擎
使用不同契约；共享边界见[版本框架](unified-version-framework.md)。
