# 仓库结构

[文档目录](../README.md) · [当前交接](handoff.md)

| 路径 | 用途 |
|---|---|
| `tools/tiaportal-mcp` | V20/V21 完整引擎、Foundation、精确版本 worker 和测试 |
| `tools/mcp-configurator` | 嵌入工作台的 WPF 配置模块、兼容入口与配置功能测试 |
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
| `manifest` | 生成的版本/工具/构建/交付哈希证据 |
| `scripts` | build、checks、generate、diagnostics、operations 分类脚本 |
| `.github` | Actions、贡献/安全/行为规范 |
| `.claude-plugin`、`hooks` | 插件定义和 Claude Code 钩子 |

运行二进制不进入 Git；使用[发布流程](release-workflow.md)生成完整交付包。
Siemens PublicAPI 是本机构建输入，通过参数指定目录，不随仓库或公开交付包分发。
`bin-build`、本机输出、用户工程、客户端配置及设计交接素材不属于版本化源码。

## 文档入口

| 路径 | 维护内容 |
|---|---|
| `docs/getting-started` | 新手指南、配置与 CLI |
| `docs/guides` | PLC/HMI、硬件网络、版本控制与具体工作流 |
| `docs/reference` | 版本工具矩阵、能力、API 缺口及生态参考 |
| `docs/troubleshooting` | 错误定位、Openness 限制与诊断 |
| `docs/development` | 当前架构、交接、验证、发布与路线图 |
| `docs/releases`、`docs/archive` | 发布记录及历史资料 |
| `docs/licenses` | 再分发组件的许可证清单和原文 |

开发目录只维护当前入口。已失效的源代码候选页、一次性日期审计、旧机器状态和重复交接页由 Git
历史保存；其有效结论并入当前架构、验证或限制页。机器可读 API 证据与官方示例语料继续保留。

## 完整引擎源码

`tools/tiaportal-mcp/src/TiaMcpServer` 中的 `ModelContextProtocol/Tools` 声明工具，
`ModelContextProtocol/Builders` 放纯算法，`Siemens/Portal` 放工程调用，`Siemens/Hmi`
放 HMI 访问层；`Runtime` 是 PLC/OPC UA/Web API/Open Pipe 通道，`Cli` 是命令入口。
新增原生能力应同时检查工具声明、类型化调用、版本策略、示例及有意义的功能验证。

所有 C# 源码使用无 BOM UTF-8、仓库内 LF。离线测试以相对路径链接纯逻辑源文件，移动文件时同步
更新项目引用。Foundation 与完整引擎使用不同契约；共享边界见[版本框架](unified-version-framework.md)。
