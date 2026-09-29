# PLC 交叉引用保护与 Unified 面板 GitHub 核查（2026-09-29）

## PLC 交叉引用

用户确认会导致 TIA 崩溃的工具是 `GetCrossReferences`。本次不连接虚拟机、不触发原生查询、不操作界面。

仓库 [v2.9.1 记录](../releases/v2.9.1.md) 有一次查询返回旧引用后 TIA 自行退出的时间线。未编译修改是怀疑的触发条件；不能据此证明所有崩溃都有同一原因，也不能保证编译后安全。本次尚未取得新一次崩溃的日志或转储，未确认其根因。

本地源码保护：

- `GetCrossReferences` 默认在访问项目/目标/原生服务之前拒绝。
- `DeletePlcBlock`、`DeletePlcType`、`DeletePlcTagTable` 的交叉引用支路共用此策略，即使调用方传 `crossReferences=true` 也不能绕过。
- 仅当服务器进程显式设置 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1` 时，允许继续检查编译状态。这是已保存测试工程上的诊断开关，不应由客户端自动开启；本次没有设置该变量。其他值均拒绝。
- 开启诊断后，任何 `IsConsistent=false` 或无法读取的状态仍拒绝。此前 null 状态会放行，此处已收紧。
- 删除报告的 `crossReferenceQueried` 表示是否实际尝试原生查询，不能再直接照抄请求参数。拒绝时为 false，保留原因，引用数量保持未知，不返回“零引用”。
- `GetUnifiedCrossReferences` 是另一个 HMI 工具，不属于用户本次确认的 PLC 崩溃路径；此次没有调用或修改它。

`GeneratePlcDocumentation` 可以从导出的 PLC 文档生成调用交叉引用。它不调用 TIA 的原生交叉引用服务，但仅覆盖导出文档中可解析的信息，不替代全工程变量、HMI、间接访问等完整引用检查，不能单凭离线结果判断可安全删除对象。

本地离线套件：2491 passed，0 failed，0 skipped（Windows 沙箱内两项既有原子文件替换测试受限，沙箱外重跑全部通过）。改动尚未部署到 VM，正在运行的 2.9.2 引擎不会因此自动获得保护。

后续用户提供了根目录 PublicAPI：V21 `TIA_V21_PublicAPI/V21/net48` 和 V20 `TIA_V20_PublicAPI/V20`。完整引擎现已分别编译通过（0 错误），原有 API 形状检查通过 3097/2805 项；此前“缺少 SDK 导致未编译”的状态已解除。仍未部署 VM；新功能验收见 [生态接入记录](ecosystem-integration-20260929.md)。

## GitHub 源码与功能范围

通过网页检索、GitHub 公共仓库 API 和默认分支源码核查以下项目。检索结论是“未找到实现”，不是穷尽所有公开/私有代码后的不可能性证明。没有安装、执行或移植这些第三方程序。

| 项目 | 查到的实现/说明 | 对本次需求的结论 |
|---|---|---|
| [TIA-Add-In-ShowScripts](https://github.com/tia-portal-applications/TIA-Add-In-ShowScripts) | README 明确限制为 Faceplate Containers；[AddScriptsToList.cs](https://github.com/tia-portal-applications/TIA-Add-In-ShowScripts/blob/master/src/ShowScripts/AddScriptsToList.cs) 遍历普通画面和画面对象的事件/动态脚本，面板分支读取 `HmiFaceplateContainer.ContainedType` 做统计。仓库最后推送 2024-12-17。 | 能参考画面脚本读写；没有找到新建面板类型或修改类型内部对象/局部脚本的实现。旧版限制不能直接当作 V21 的完整能力清单。 |
| [Unified Openness Library](https://github.com/tia-portal-applications/tia-portal-openness-unified-library) | [UnifiedOpennessConnector.cs](https://github.com/tia-portal-applications/tia-portal-openness-unified-library/blob/main/src/UnifiedOpennessLibrary/UnifiedOpennessConnector.cs) 负责连接、参数和查找 HmiSoftware。仓库最后推送 2024-09-02。 | 基础连接库，没有补出面板类型内部编辑接口。 |
| [AnyAutomationStudio](https://github.com/StaniB88/AnyAutomationStudio/blob/main/CHANGELOG.md) | 截至 2026-09-27 的默认分支变更日志列出选择项目库已有类型/版本来添加容器、读取实例接口、编辑画面对象脚本。 | 没有提供完成本次三项功能的公开实现证据；不能将画面控件脚本误认为面板类型内部脚本。 |
| [heilingbrunner/tiaportal-mcp](https://github.com/heilingbrunner/tiaportal-mcp/blob/main/src/TiaMcpServer/Siemens/Portal.CrossReferences.cs) | `GetService<CrossReferenceService>()` 后调用 `GetCrossReferences(filter)`。 | 同一个原生查询入口，未发现修复本次 TIA 自退问题的替代实现。 |
| [Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp/blob/main/TiaMcpServer.OpennessWorker/Openness/CrossReferenceReader.cs) | 有结果上限、不完整状态和异常处理，但仍调用 `CrossReferenceService.GetCrossReferences`。 | 可参考诊断表达方式，不能把进程隔离/异常处理当成能阻止外部 TIA 进程退出的保证。 |

GitHub `wincc faceplate` 仓库检索还返回 WinCC 7 VBA 工具和 Classic/Advanced 样例；它们不是当前 WinCC Unified 类型编辑的证据。本轮没有找到可以直接移植并完成“从零新建 Unified 面板类型、编辑类型内部控件、编辑内部局部脚本”三项的纯 Openness 开源实现。
