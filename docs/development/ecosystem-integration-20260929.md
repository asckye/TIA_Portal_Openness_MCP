# Openness 生态功能接入记录

用户范围：将此前推荐的八个项目的功能补充进当前 MCP；先完成本地工具、脚本及验证，不操作 TIA 界面。已有等价功能复用。源码来源固定到提交，保留许可；GPL 项目只按公开需求独立实现，不复制代码。当前新增 11 个 MCP 工具，总数 453 → 464。[使用说明及完整边界](../reference/ecosystem-tools.md)。

| 来源 | 功能范围 | 接入方式与进度 |
|---|---|---|
| siemens/tia-portal-ai-extensions | 32 份官方 Openness 指南、并发与崩溃排查 | ReadOpennessGuidance；进程内串行门；无参数的 BEFORE/RETURNED/THREW 日志 |
| Czarnak/tia-portal-mcp | 批量读取、预览/应用写操作、分页与部分结果 | ReadToolBatch / PreviewToolBatch / ApplyToolBatch；已有分页/预检复用；非原子批量明确停止/跳过 |
| Czarnak/tia-git-addin | SimaticML 比较、LAD 图形差异、Git 工作流 | MIT Core + RenderPlcVisualDiff；ManagePlcGitRepository；本地端到端通过 |
| core-engineering/siemens-plc-tools | code/iol/net/sim/sup/trace，以及 core/modbus 库 | 固定源码与独立 Python 环境；RunPlcCompanionTool 发现 49 个命令/命令组；网络/PLC 实测不在本轮范围 |
| tia-portal-applications/tia-addin-opc-ua-modelled-interface | OPC UA 用户建模接口生成/扩展 | GenerateOpcUaModelledInterface；改为无界面生成 XML，已有 ImportOpcUaInterface 等工具负责后续导入；尚未原生生成/导入验收 |
| Parozzz/TiaUtilities | LAD 模板/别名/报警批量生成与 XML 编辑 | ComposePlcAliasAlarmLad / InstantiatePlcXmlTemplates；复用导入导出；未移植桌面编辑器、JavaScript 自定义执行及图形逐步撤销/重做 |
| Repsay/tia-openness-api-client | 工程、设备、PLC/HMI、块、全局库与主副本 | 复用 CreateProject/OpenProject/SaveProject、AddDevice、块与软件单元工具、ManageGlobalLibrary/CreateLibraryMasterCopy/ImportMasterCopyFromGlobalLibrary 等；不创建第二个 Openness 连接层 |
| Thomas-Schlangen/tia-linter | 命名、注释、结构、硬件、元数据、库检查与报告 | AuditEngineeringExports + 现有分析器及 code lint；硬件/库采用明确 XPath 政策；HTML/PDF 报告通过；不宣称完整 Siemens 规范认证 |

本轮本地 PublicAPI：V21 `TIA_V21_PublicAPI/V21/net48`；V20 `TIA_V20_PublicAPI/V20`。这些组件不入库、不分发。

PLC 原生 `GetCrossReferences` 默认禁用；离线引用分析必须声明只覆盖所提供的导出文件。不能把缺少结果解释成“没有引用”。

面板类型内部控件、类型内部脚本及从零创建面板类型仍受实际公开 Openness API 限制；第三方功能接入不会自动消除该限制，参见 [面板验证记录](faceplate-probe-20260929.md)。

## 验证结果

- V21 完整构建：0 错误（12 个既有警告）；V20 完整构建：0 错误（14 个警告）。
- 主项目离线测试：2514 passed，0 failed，0 skipped，包含 LAD 差异/实体拒绝/HTML 转义、模板令牌/路径、报警拓扑、质量规则与令牌过期/复用。
- 既有 PublicAPI 形状检查：V21 3097，V20 2805 全部通过；这是程序集接口检查，不是活工程测试。
- [编译后工具集成检查](../../scripts/checks/Test-EcosystemAssembly.ps1)：两个版本各 23 项通过，含实际 MCP CallTool 桥接、报告文件、PDF、Git 临时仓库提交、伴随命令发现/帮助/失败返回和崩溃面包屑日志。
- HTTP 本地协议回归：两个版本各 28 项通过；沙箱内 HttpListener 不受支持，沙箱外以 localhost 假响应完成。没有连接真实 TIA。
- [上游 Python 离线检查](../../scripts/checks/Test-Ecosystem.py)：2091 passed，8 skipped。7 项缺少上游样例，1 项缺少 pandoc；未把跳过项算作通过。Windows UTF-8 与路径分隔符适配已验证。
- 文档链接/仓库入口与死工具引用检查通过；目录由编译程序集生成，全部新参数有描述。

机器可读记录（含两版实际 EXE 的 SHA-256）：[ecosystem-validation.json](../../manifest/ecosystem-validation.json)。

构建输出在 `tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48` 与 `bin-v20/Release/net48`。尚未替换 `runtime/` 中旧发布构建、部署 VM、重启当前 MCP 服务或发布 GitHub。源代码与新目录对应本地 Unreleased 构建，正式发布仍须走 Build-Release/交付哈希流程。

实际工程写入、OPC UA 模型原生导入、布尔 LAD 原生编译、抓包驱动及在线 OPC UA/Modbus/sup/trace 行为未在本轮验证。批量写协议的令牌纯逻辑和拒绝路径已验；真实多操作失败恢复仍需单独测试工程验收。
