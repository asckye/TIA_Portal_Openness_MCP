# Openness 生态工具

[文档目录](../README.md) · [接入与验证记录](../development/ecosystem-integration-20260929.md) · [第三方许可](../licenses/THIRD-PARTY-NOTICES.md)

这些功能位于未发布的本地源码中。引擎构建后通过 `FindTools` / `CallTool` 调用；正在运行的旧 MCP 服务不会自动加载新工具。无需操作 TIA 界面。此轮没有连接 VM、修改工程、保存或下载到 PLC。

官方 `tia-portal-ai-extensions` 为优先参考。先调用 `GetAuthoringGuide(topic="openness-workflow")` 了解开发流程，再用 `ReadOpennessGuidance` 阅读对应主题。指南与具体版本手册冲突时，以手册和 PublicAPI 为准；详细差异、修复与缺口见[官方流程审计](../development/official-openness-audit-20260929.md)。`RunToolsInTransaction` 已收紧为其描述中的 8 个同步编辑工具，编译、保存、在线、文件和嵌套编排不能放进该事务。

| 新工具 | 用途 |
|---|---|
| `ReadOpennessGuidance` | 检索/分页阅读 32 份 Siemens 官方指南及随附 Markdown。文档作为资料返回。 |
| `RenderPlcVisualDiff` | 对两个单块 SimaticML 导出做结构比较及 LAD 并排 SVG/HTML 差异；其它语言保留结构比较。 |
| `ReadToolBatch` | 顺序读取最多 50 个明确声明为 READ 的工具，逐项保留失败和未知结果。 |
| `PreviewToolBatch` / `ApplyToolBatch` | 支持原生 dryRun 的 WRITE 工具先预览，签发 10 分钟一次性令牌；应用前重新检查工程/会话/PID和预览结果。 |
| `ManagePlcGitRepository` | 导出/VCI 目录的 status/history/diff/show/stage/commit；提交只包含指定文件。 |
| `RunPlcCompanionTool` | 独立 Python 进程调用 PLC Tools 的全部已发现 CLI：当前 49 个命令/命令组条目。 |
| `GenerateOpcUaModelledInterface` | 从 PLC 或软件单元读取类型、标签、DB，生成用户建模 OPC UA XML；可选文件夹、空 DB 和访问权限过滤。 |
| `AuditEngineeringExports` | 命名、注释、元数据、重复名、网络数以及自定义硬件/库 XML 规则；JSON、HTML、PDF 报告。 |
| `InstantiatePlcXmlTemplates` | 按行展开已验证的 PLC XML 模板，结构化转义替换属性/文本；先验证全部输出，执行时仅新建文件。 |
| `ComposePlcAliasAlarmLad` | V21 布尔别名/简单报警位 LAD FC；普通线圈或置位/确认复位网络。 |

## Python 伴随工具

需要 Python 3.12+，执行 [Install-PlcTools.ps1](../../scripts/ecosystem/Install-PlcTools.ps1)。默认环境在被 Git 忽略的 `TiaMcp_Output/ecosystem-python`，不会更改系统 Python。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ecosystem/Install-PlcTools.ps1
```

引擎在仓库内可自动发现默认环境。部署到别处时，在 MCP 服务进程环境配置 `TIA_MCP_REPOSITORY_ROOT`（包含 scripts、reference、tools/third-party 的根目录）和 `TIA_MCP_PLC_TOOLS_PYTHON`（环境中 python.exe 的绝对路径）。保留整个伴随源码目录及许可证。

先调用 `RunPlcCompanionTool(workingDirectory, mode="catalog")` 获取精确命令/选项；`mode="help", argumentsJson=["code","lint"]` 查看某个命令帮助。`mode="run"` 默认仍为预览，只有 `dryRun=false` 执行。

| 模块 | 接入的功能 |
|---|---|
| code | 项目配置/状态、SCL 检查、格式检查、内存布局、转译 Python、测试/覆盖率、文档/网页、语义差异、离线引用、安全边界规则、draw.io、参数表、PDF/Markdown、变量追踪 |
| iol | 项目配置、I/O 列表、标签 XML/Excel 导入导出、比较、验证 |
| net | 工业网络监视、OPC UA 包分析；抓包需要本机可用的 pcap/Npcap 提供程序 |
| sim | OPC UA 浏览/读写与测试场景；使用 core 和 modbus 库；可选本地 Web UI |
| sup | OPC UA 到 Redis/PostgreSQL/HTTP 的监督与集成测试 |
| trace | 从 UDT 生成跟踪文件、设备跟踪状态/启停/抓取 |

这些是 CLI 调用入口。code 的测试可以执行项目 Python；sim/sup/trace 可操作设备，net 可抓包。执行前需明确配置目标。服务不会自动选择设备、开始抓包或向 PLC 写值。每次命令有 1–300 秒超时，输出各限 1 MiB并报告截断；超时会结束子进程树，已发生的文件/设备操作不回滚。长期 web/monitor 可直接使用安装环境中的 CLI 运行。

上游 `code export pdf` 使用 pandoc/XeLaTeX/Eisvogel，需要另外配置；本项目的 `AuditEngineeringExports(..., reportPath="...pdf")` 使用 ReportLab，无需 TeX。来源工具的“测试通过”不等于 TIA 编译或真实 PLC 行为通过。

## 批量调用

预览输入示例格式：

```json
[{"name":"某个具有 dryRun 的 WRITE 工具","arguments":{"softwarePath":"PLC_1","dryRun":true}}]
```

令牌绑定服务器保存的精确有序操作。申请令牌必须指定 `expectedProject`；应用时不能更换参数。失败、未知结果、工程切换或预览变化会停止执行，后续条目标记 skipped。先前成功的操作可能保留；这不是原子事务。原生预览没有覆盖的内部状态无法由令牌检测，其他客户端仍可能在调用之间修改工程。需要原生事务时使用已有 `RunToolsInTransaction` 的限定能力。

单个服务器进程的工具调用串行执行，降低并发进入 Openness 的风险。不同端口的服务器不共享这个锁，应分别绑定不同 TIA 进程。日志默认在 `%LOCALAPPDATA%/TiaMcp/diagnostics`，也可配置 `TIA_MCP_DIAGNOSTICS_DIRECTORY`；按调用 ID记录 BEFORE/RETURNED/THREW，不记录参数、代码、认证头。RETURNED 只代表调用返回，并不等于业务成功。

## 生成与质量规则

OPC UA 生成器沿用官方目前支持的优化节点和字符串 ID。Safety 区域只允许排除或只读；嵌套 FB 的内部成员访问仍是上游限制。输出 XML 必须另行用 `ImportOpcUaInterface` 导入并编译，此轮未作原生导入验收。参数、节点跳过警告、输出哈希会返回。

布尔生成器按 `source` / `destination` 的明确全局符号分量数组处理，不能猜测标签类型。指定 `acknowledge` 时采用“先置位、再复位”，同时为真时**复位优先**，确认是电平触发；它不替代报警系统或安全功能设计。复杂网络用原生导出模板展开。所有新生成器均不自动导入、保存或下载。

质量审计的 `success` 仅表示执行完成；`qualityPassed` 才是规则结果，`dataComplete` 表示所选输入/规则是否完整处理。硬件、库等使用明确 XML 规则，例如：

```json
[{"id":"required-version","files":"Library.*[.]xml$","xpath":"//*[local-name()='Version']","minCount":1,"valuePattern":"^1[.]","severity":"error"}]
```

匹配不到文件的规则返回 notEvaluated，不算通过。默认检查不是完整 Siemens 编程风格认证，也不判断设备安全性。离线引用只覆盖输入导出，不能代替完整原生交叉引用。`GetCrossReferences` 的默认禁用保护继续有效。

## 八个推荐项目的对应关系

- [Siemens 官方指南](https://github.com/siemens/tia-portal-ai-extensions)：本地检索、并发和诊断措施。
- [Czarnak MCP](https://github.com/Czarnak/tia-portal-mcp)：独立实现批量读、预览/应用协议；复用现有参数预检、分页和结果护栏。
- [Czarnak Git Add-In](https://github.com/Czarnak/tia-git-addin)：MIT Core 的解析/结构差异/LAD 布局，加本项目 HTML 与 Git 适配器；未复制桌面 UI。上游尚未提供的 FBD/HMI 图形差异不列为已实现。
- [PLC Tools](https://github.com/core-engineering/siemens-plc-tools)：八个包及全部 CLI入口，固定提交源码随仓库保留。
- [Siemens OPC UA Add-In](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface)：源代码生成阶段适配为无界面工具；导入复用已有接口。
- [TiaUtilities](https://github.com/Parozzz/TiaUtilities)：按功能独立实现别名/报警/模板批量生成，复用 XML 导入导出和版本管理；不移植 WinForms 表格编辑器或执行其 JavaScript 自定义代码。图形操作的逐步撤销/重做未做成新的 MCP 工具。
- [Repsay 客户端](https://github.com/Repsay/tia-openness-api-client)：工程创建/打开/保存/编译，设备/PLC/HMI，块/实例 DB，库/主副本等已有原生 MCP 工具覆盖；无需再叠加第二个 Python Openness 连接层。
- [tia-linter](https://github.com/Thomas-Schlangen/tia-linter)：按功能独立实现导出审计、规则与报告，SCL/调用结构检查由现有分析器和 PLC Tools 提供；不复制 GPL 源码，不把该项目未完成的真机验证当作能力保证。

“所有功能”不等于所有第三方桌面 UI 或路线图项目已经重写。上述未支持项保留为明确缺口；特别是从零创建 Unified 面板类型、修改类型内部控件/局部脚本，仍没有在本项目实证可用的公开 Openness 实现，见[面板验证记录](../development/faceplate-probe-20260929.md)。
