# 仓库结构与迁移对照

[文档目录](../README.md)

根目录只保留项目说明（README、CHANGELOG）、LICENSE/NOTICE、Git 配置、插件配置（`.claude-plugin/` 与 Claude Code 钩子 `hooks/`）及 `TiaMcpConfigurator.exe`。治理文件（CONTRIBUTING、CODE_OF_CONDUCT、SECURITY）在 `.github/`（GitHub 同样识别）；源码放在 `tools/`，运行文件放在 `runtime/`，模板放在 `templates/`，清单放在 `manifest/`；手动客户端配置示例是 `docs/getting-started/cursor.example.json`（原 `examples/` 目录已并入）。本机的 Siemens PublicAPI 副本放在仓库之外（如 `D:\TIA_PublicAPI\`），构建脚本以参数指向它。

## 文档分类

| 目录 | 内容 |
|---|---|
| `docs/getting-started` | 图形配置、CLI 和 AI spec 提示词 |
| `docs/guides` | 工程生成、版本控制、在线监视、硬件网络、PLC / HMI 专题（含原 `docs/tools` 的工具族细节，已并入） |
| `docs/reference` | 工具矩阵、能力和自然语言配方 |
| `docs/troubleshooting` | 错误、Openness 限制和 HMI 快照诊断 |
| `docs/development` | 发布、验证、结构与路线图 |
| `docs/releases`、`docs/archive` | 当前发布说明和历史记录 |
| `docs/licenses` | 随包分发的第三方程序集许可证清单与原文 |

原 `手册/` 并入上述分类；“开始使用”和“使用说明与介绍”并入 README 与配置指南；AI spec 提示词并入 CLI，PLC 网络模式并入模板指南，能力增补并入能力参考。v2.7.15 及之前发布说明合并存档。失效路线图、临时验收命令和私人原始采集数据不再作为当前文档保留。

## 脚本和运行路径

脚本分别移入 `build/`、`checks/`、`generate/`、`diagnostics/`、`operations/`，完整入口见 [脚本索引](../../scripts/README.md)。Actions、脚本间调用、清单、蓝图和文档同步更新。

旧 Build-DefectFix、Build-MultilingualFix、Package-DefectFix、Package-ReadOnlyV21 和 Python 预热桥接已删除。发布统一见 [发布流程](release-workflow.md)（一键入口 `scripts/build/Release.ps1`），预热使用原生 CLI；取证、预热和拖放生成仍有用途，保留在相应分类。

交付和 checkout 均使用 `runtime/v20`、`runtime/v21`，不再创建旧 `tools/.../bin[-v20]/Release/net48` 副本。

## 引擎源码布局（2.7.18 起）

`tools/tiaportal-mcp/src/TiaMcpServer/` 按职责分子目录，命名空间不变：`ModelContextProtocol/Tools/`（全部 `McpServer.*.cs` 工具声明）、`ModelContextProtocol/Builders/`（离线 XML/JSON 构造器、分析器、校验套件、`*Logic.cs`）、`ModelContextProtocol/`（响应类型、`ToolTaxonomy`、指南/提示、导出寄存等基础设施）、`Siemens/Portal/`（全部 `Portal.*.cs` 实现）、`Siemens/Hmi/`（`Unified*`、`Hmi*` 访问层）、`Siemens/`（Openness 解析、反射与纯逻辑助手）、`Runtime/`（S7/OPC UA/Web API/Open Pipe 运行时通道）、`Cli/` 与根目录 `Program*.cs`。所有 `.cs` 为无 BOM 的 UTF-8、仓库内 LF（`.gitattributes`）。离线测试项目按相对路径链接其中的纯逻辑文件，新增或移动文件时同步更新 `TiaMcpServer.Tests.csproj`。

2.7.25 起每个"官方 API 对齐"工具族固定四件套：`Siemens/<Family>Logic.cs`（无 Siemens 依赖的参数门控 / JSON 拆分，链接进离线测试）、`Siemens/Portal/Portal.<Family>.cs`（`partial class Portal` 的类型化实现，V20 / V21 差异用 `#if TIA_V20`）、`ModelContextProtocol/Tools/McpServer.<Family>.cs`（工具声明）、`tests/TiaMcpServer.Tests/<Family>Tests.cs` + `tests/TiaMcpServer.HttpTests/<Family>ShapeChecks.cs`（离线测试与对 PublicAPI 的逐成员形状检查，各在其 `Program.cs` 注册）。族名与版本的对应见 [接续工作交接 §1](handoff.md)；选件包（`Portal.Sivarc.cs`、`Portal.DccEngineering.cs`、`Portal.TestSuite.cs`、`Portal.OptionalEngineering.cs`、`Portal.SpecializedExchange.cs`）在服务缺席时回 NotSupported，不依赖选件许可编译。

`bin-build/` 是被忽略的本机验证/打包目录，不进入公开交付。用户工程、外部素材及客户端配置不属于仓库清理范围。
