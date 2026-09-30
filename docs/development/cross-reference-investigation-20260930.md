# PLC 交叉引用退出调查：官方流程与 GitHub 核查（2026-09-30）

## 结论与证据边界

用户报告 `GetCrossReferences` 刚刚再次导致 TIA 退出，要求优先核对西门子官方做法和 GitHub 解决方案。本轮未重跑原生查询、未附加 TIA 工程、未操作界面，也未编译、保存或重启工程。

- 官方支持在具体 PLC 块、类型等对象上获取 `CrossReferenceService` 后查询；本项目 Block/Type 路径已使用这一入口。
- 官方提供独立的“重建交叉引用信息”操作，用于缺失、重复或目标找不到的引用。不能将普通编译等同于重建，也不能据此保证解决进程退出。
- 找到了 GitHub 上已合并且作者报告经过 V21 实机验证的查询修复；修复内容是服务获取位置和结果完整性，没有证明解决本次 TIA 原生进程退出。
- 尚未取得本次退出的 TIA 日志、Windows 原始事件或转储，不能确认原生内部根因。未编译修改、旧引用和随后退出的历史时间线只支持调查假设。

### 本轮服务状态

此前仅通过 MCP `initialize` 握手读到 `192.168.86.128:8765/mcp` 返回 `TiaMcpServer 2.9.2.0`。用户随后明确表示已替换为 3.0，并确认原地址、服务已启动；之后两次沙箱内握手及一次沙箱外网络核对均超时，尚未读到更新后的版本。超时不是 TIA 崩溃或升级失败的证据，旧版本读数也不能再代表更新后的状态。

3.0.0 源码在原生查询前默认拒绝；服务器进程只有显式设置 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1` 才能进入后续一致性检查。此项是阻断有崩溃记录的调用，不是西门子缺陷补丁。本轮没有设置该变量，也没有通过调用查询工具来验证保护。

后续同日 [V20/V21 审计](v20-v21-bugs-and-gaps-20260930.md)发现通用 `InvokeService` 未接入这一策略。以上默认拒绝仅适用于专用 `GetCrossReferences` 和删除支路，不能解释为服务器内所有原生入口都已封锁；反射旁路尚未修复，也未在工程上调用验证。

## 官方 API 与修复流程

### 查询入口

[V21 官方查询说明](https://docs.tia.siemens.cloud/r/de-de/v21/tia-portal-openness-api-fur-die-automatisierung-von-engineering-workflows/tia-portal-openness-api/funktionen-fur-den-zugriff-auf-die-daten-eines-plc-gerats./funktionen-fur-den-zugriff-auf-den-plc-dienst/querverweise-fur-step-7-abrufen)和 [V20 英文说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7)示例在具体块上调用 `GetService<CrossReferenceService>()`，随后调用 `GetCrossReferences(filter)`，遍历 Sources、References、Locations 和 Children。

文档注明成员对象的 `UnderlyingObject` 可能不可用；查询的文本信息仍可存在。因此不应把取不到底层工程对象当作没有引用。官方示例允许按已知类型转换底层对象，但显示文本引用本身不要求额外读取它。

已查页面没有将“先编译”列为防止崩溃的保证。本项目的编译状态检查属于防护策略，不是官方保证。

### 重建交叉引用信息

[V21 官方重建步骤](https://docs.tia.siemens.cloud/r/en-us/v21/displaying-cross-references/recreating-the-existing-cross-reference-list)：项目视图的 Options → Settings → General → Cross-references → Recreate the cross-reference information；等待 Inspector 的完成/错误消息，再刷新列表。官方列出的用途包括缺失、重复以及引用对象找不到。

这与历史记录的旧引用现象相符，可作为工程副本上的候选修复步骤，尚未在本工程执行或证实有效。本轮在公开文档及根目录 V21 PublicAPI XML 中未找到对应的公开 Openness 重建接口，不能编造 `RebuildCrossReferences` 或用 `CompileSoftware` 冒充该操作。遵照用户先前要求，本轮不操作界面。

### 定位实际退出点

西门子官方 GitHub 仓库的 [crash-diagnosis 指南](https://github.com/siemens/tia-portal-ai-extensions/blob/b5c7041648dc10f9225ef082306ade6c335b8771/openness_development/skills/crash-diagnosis/SKILL.md)强调：Openness 通道失效可能在后续调用才暴露，最后报错的工具未必是原始故障位置；对可疑调用逐项追加调用前/返回后的日志，并保留先前操作顺序。指南中的 Startdrive 属性映射案例不是 PLC 交叉引用的已证实根因。

应用到本项目，需要区分目标解析、一致性检查、获取服务、执行查询和读取结果属性的阶段，并与 TIA 进程退出时间核对。当前 `InvocationJournal` 只记录 MCP 工具边界，不能独自区分工具内部的这些原生阶段。缺少返回记录也可能是等待、日志失败等原因，必须结合事件或转储判断。

[Microsoft Silent Process Exit 说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/registry-entries-for-silent-process-exit)解释了自身退出和被其他进程终止的区别。`EVENT_PROCESSTERMINATION_SELF` / 退出码 -1 不说明应用退出的内部原因，不能据此排除内存、内部异常或此前文件系统操作的影响。

## GitHub 实现核查

| 来源 | 查证内容 | 对本项目的意义 |
|---|---|---|
| [Czarnak issue #73](https://github.com/Czarnak/tia-portal-mcp/issues/73)、[PR #84](https://github.com/Czarnak/tia-portal-mcp/pull/84) | 在 `PlcSoftware` 上获取服务返回 null，却被报告为成功且零引用。PR 于 2026-09-27 合并（`b99a154dafcb25825510848a6943a6edab994295`），改为遍历支持服务的源对象，并明确结果是否完整。 | 本项目 Block/Type 的服务获取位置已正确；可参考失败与不完整结果的表达，不能将该修复当作本次退出的根因修复。 |
| [Czarnak CrossReferenceReader](https://github.com/Czarnak/tia-portal-mcp/blob/b99a154dafcb25825510848a6943a6edab994295/TiaMcpServer.OpennessWorker/Openness/CrossReferenceReader.cs) | 覆盖根目录和软件单元内的块、类型、变量；预期适配异常可保留部分结果，`NonRecoverableException` 不会当作普通适配失败吞掉。显示结果上限不等于限制底层查询次数。 | 值得参考完整性、异常传播和覆盖范围；仍使用 Siemens 原生服务，不能保证外部 TIA 进程不退出。 |
| [heilingbrunner 实现](https://github.com/heilingbrunner/tiaportal-mcp/blob/main/src/TiaMcpServer/Siemens/Portal.CrossReferences.cs) | 同样经 `GetService` 调用 `GetCrossReferences`。 | 没有发现绕过原生故障的替代查询引擎；不能直接移植并宣称修复崩溃。 |
| [siemens/tia-portal-ai-extensions](https://github.com/siemens/tia-portal-ai-extensions) | 官方提供开发和诊断指导；本轮当前提交为 `b5c7041648dc10f9225ef082306ade6c335b8771`，指南现位于 `openness_development/skills/`。 | 是诊断方法来源，本身不是替换 TIA 原生组件的补丁。 |

PR #84 的作者报告包含一次完整的 55/55 源对象零引用用例，以及 1,827 次源对象查询均成功的已知引用用例，后者明确标记受输出上限影响的不完整结果。这是该作者对其测试工程的验证，不是本项目或本次异常的复现结果。

本轮检索未找到明确针对当前 V21 PLC `GetCrossReferences` 退出问题的官方修复公告或可直接移植的已验证崩溃补丁；这不是不存在任何补丁的断言。

## 本项目需要跟进的代码问题

核对 [Portal.Software.CrossReferences.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/Portal/Portal.Software.CrossReferences.cs)、[CrossReferenceGuardLogic.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/Siemens/CrossReferenceGuardLogic.cs) 与 [InvocationJournal.cs](../../tools/tiaportal-mcp/src/TiaMcpServer/ModelContextProtocol/InvocationJournal.cs)：

1. `TryFlattenCrossReferenceResult` 和子节点遍历存在吞异常后返回部分/空列表的路径；结果没有明确表达遍历失败，不能可靠区分真实零引用。这是可独立修复的问题，尚未证明导致 TIA 退出。
2. 原生阶段缺少逐项日志；额外读取 `UnderlyingObject` 只为获得类型名，增加了需要定位的远程访问。应记录必要阶段并评估减少非必要读取，不能预先认定该 getter 就是崩溃点。
3. 编译护栏目前读取根 PLC 块树，没有证明覆盖全部软件单元，也不直接检测交叉引用信息是否健康；不应以通过护栏作为开启原生服务的充分证据。

本轮保留默认阻断策略，不修改原生查询行为。已有 `GeneratePlcDocumentation` 可针对导出文档作部分离线调用分析，不能代替全工程引用检查，也不能据此保证删除对象安全。

## 本轮本地改动与后续证据

- 校正 [v2.9.1 历史判断](../releases/v2.9.1.md)和 [交接记录](handoff.md)中无证据的排除结论，保留当时操作时间线与测试结果。
- [Collect-TiaExitEvidence.cmd](../../scripts/diagnostics/Collect-TiaExitEvidence.cmd)补充独立查询 `ProcessExitMonitor` 3000 和系统 `Resource-Exhaustion-Detector` 2004，并记录 UTC 采集时间、各查询错误。每条查询最多取最近 24 小时的 100 条；事件缺失不视为排除原因。
- 脚本只读取运行它的那台 Windows 的事件与已有 native-export 日志。宿主机执行不等于取得虚拟机证据；它不启用崩溃转储，不调用 TIA，也不自动导出 TIA 私有日志或 MCP 调用日志。

继续定位本次退出还需要虚拟机上的上述事件、退出前后 TIA 日志/已有转储，以及 MCP 调用日志（默认 `%LOCALAPPDATA%\TiaMcp\diagnostics\calls-*.jsonl`，如配置 `TIA_MCP_DIAGNOSTICS_DIRECTORY` 则以该目录为准）。需对齐时间、TIA PID、MCP 进程版本、实际请求及诊断开关；3.0 默认拒绝时不应进入原生查询，若仍有退出必须重新定位实际入口和前序调用。

公开来源核查用网页、GitHub API 和原始源码完成；只读握手记录及查询快照保存在忽略目录 `TiaMcp_Output/cross-reference-investigation-20260930/`。本轮未部署本地改动，未验证任何工程上的修复效果。

本地验证：仓库检查通过（184 个 Markdown 文档及入口，0 问题），`git diff --check` 通过。取证脚本在宿主机冒烟执行生成 12 个文件、包含 UTC 时间；沙箱内 `tasklist` 权限不足，沙箱外重跑后各错误文件均为空。该结果只验证本机采集命令能够执行，不代表已经采到本次虚拟机退出的证据。
