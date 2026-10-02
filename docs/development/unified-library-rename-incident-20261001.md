# V21 Unified 脚本模块库类型改名故障（2026-10-01）

本页延长压力数字是历史候选的结果，原记录现保存在 `manifest/local-stability-extended-r3-20261001.json`。当前发布 EXE 的验收以 `manifest/release-build.json` 为准；不能相加或视为本版重复验收。
## 最新现场结果：指定样本的两次隔离复现

用户随后明确指定 AutomaticDipCoatingMachine 项目库中的 `LSicar_GeneralScripts` 用于测试。11:32–11:44（现场服务时间 `-07:00`）实际完成了样本复制和两次原生改名测试，**两次均失败并伴随测试 TIA 进程退出；目前没有可交付的可用改名修复**。下文最初恢复阶段和离线阶段的“尚未附加/重试”仅描述当时状态。

- 源路径：`LGen/Types_HMI/Scripts/LSicar_GeneralScripts`；类型 GUID：`04254662-9034-496e-a436-109c945e1de2`，实际类型 `Siemens.Engineering.HmiUnified.Library.ScriptModuleType`。
- 已发布版本：5.1.1（默认，GUID `e74fcb6f-b658-4ac6-9ea7-e5647251dc72`）、5.0.0（GUID `1dcd9356-4571-481b-8e81-cb011bd6e1ff`）。两版均为 `Committed`。
- 使用官方 `LibraryType.UpdateLibrary`，先从源项目库复制到新建全局库 `MCP_GeneralScripts_Rename_20261001_1133`，保存并关闭；再在独立无界面 TIA 中只读打开该库，复制到空白测试项目库并保存基线。两次复制均回读确认相同类型及版本 GUID。复制后的状态为 `Consistent`，源类型状态为 `NonDefaultVersionInstantiation`。没有把这种无实例副本视为完整生产引用场景。
- 两次真实请求均为 `ManageLibraryType(action="update", propertiesJson="{\"Name\":\"MCP_GeneralScripts_Renamed\"}", dryRun=false)`，区别仅在项目库或独立全局库的选择；第二轮在新的 TIA 实例中打开已保存的基线及可写诊断库。没有把两条路径实现为生产工具的自动重试。

| 样本位置 | TIA PID | setter BEFORE → THREW（UTC） | nativeCallId | 结果 |
|---|---:|---|---|---|
| 独立项目库 | 30552 | 18:39:37.999 → 18:39:42.923 | `b9851a7de4cc42c5bd8a5e976e047771` | `NonRecoverableException`，进程退出 |
| 独立全局库，ReadWrite | 30076 | 18:43:07.380 → 18:43:12.275 | `181cd772693d49548205fd3df8cf8f07` | `NonRecoverableException`，进程退出 |

两次日志均定位在 `LibraryType::Name` 的反射 setter，callSite 为 `6f22d25bdf3e1fe5a2d3`，分别在 MTA 线程 8 / 28。请求只有 `Name`，`lastAttemptedProperty=Name`，`appliedProperties=[]`，`mayHaveChanged=true`。日志中的对象访问路径分别经过 `get_ProjectLibrary` 和 `get_GlobalLibraries`，不是误操作同一库。MCP PID 19052 没有退出，故障后仍可导出日志和显式恢复连接。

这说明原工程中的实例引用并不是本次复现的必要条件；“先在全局库改名，再同步到项目库”的候选流程也在改名步骤失败，未进入改名后的同步。两次都经过现有 MCP 的读取、快照和反射路径，**尚不能排除前序调用的影响，也不能把它表述为独立于 MCP 的最小 C# 复现或全部 V21 补丁的结论**。本轮没有继续自动轮换 `SetAttributes`、版本编辑或导入重建；这些都不是已验证修复。

11:43:48 显式 `Disconnect` 清理第二个已退出测试进程的故障连接；11:44:05 按 PID、启动时间和完整路径重新附加源工程。11:44:06 回读源类型的名称、GUID、版本 GUID/状态、默认版本及来源信息，与 11:33:07 的基线一致；11:44:07 状态确认源 PID 5268 存活、`binding.fault=null`、`snapshotReadsBlocked=false`，8765 正常连接 AutomaticDipCoatingMachine。没有保存、改名或关闭源项目。

保存的隔离基线仍在虚拟机中，供后续分析：

- 项目：`C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\MCP_UnifiedRename_20261001_1113\MCP_UnifiedRename_20261001_1113.ap21`
- 全局库：`C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\library-general-1133\MCP_GeneralScripts_Rename_20261001_1133\MCP_GeneralScripts_Rename_20261001_1133.al21`

`ReadOpennessCompatibility` 记录 Base / WinCCUnified / Step7 的文件版本均为 `2100.0.121.1`，程序集版本 `21.0.0.0`；这不能确定已安装的 TIA Update/Hotfix。还需要现场 Windows/WER/ADIAG 服务端证据及安装补丁信息，才能进一步区分 TIA 实现缺陷、样本数据问题和调用前置状态。不能用客户端异常位置代替服务端根因。

机器可读证据、两组调用边界、请求与响应哈希见 [原生测试记录](../../manifest/unified-library-rename-native-20261001.json)。完整原始响应保留在本地忽略目录 `bin-build/unified-rename-native-20261001/`。本轮未部署临时拦截候选、未发布新版本。官方[通用改名示例](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-types)和[库结构同步流程](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-structure-during-library-update)是选取候选接口的依据，不是本样本改名成功的证据。

## GitHub 与官方流程复查（两次隔离复现后）

再次检索 GitHub 默认分支代码和公开问题：精确完整类名 `Siemens.Engineering.HmiUnified.Library.ScriptModuleType` 的相关结果是本仓库和 `Czarnak/totally-integrated-claude` 的 API 清单；`ScriptModuleType` 问题搜索未返回结果。此次可检索范围内没有找到针对该类 `Name` 写入崩溃的可直接移植、已有真实验证的修复。这不是声称 GitHub 上绝对不存在解决方案。

| 来源 | 查到的内容 | 与本故障的关系 |
|---|---|---|
| [siemens/tia-portal-ai-extensions](https://github.com/siemens/tia-portal-ai-extensions/blob/b5c7041648dc10f9225ef082306ade6c335b8771/openness_development/skills/global-library/SKILL.md) | 全局库遍历、放置及放置后对象改名模式；该仓库 `ScriptModule` 搜索无结果 | 不能把 PLC/设备放置后改名模式视为 Unified 脚本库类型修复 |
| [a4webdev/tiacommander-mcp](https://github.com/a4webdev/tiacommander-mcp/blob/master/CHANGELOG.md) | v3.22.0 加入 `rename_type` 和改名回读 | [README](https://github.com/a4webdev/tiacommander-mcp) 列出 V17–V20，完整测试为 V19，许可为 Proprietary；未找到 V21 Unified 脚本类型成功证据，不能据此移植未知实现 |
| [StaniB88/AnyAutomationStudio](https://github.com/StaniB88/AnyAutomationStudio/blob/main/CHANGELOG.md) | 列出脚本模块创建/保存、库类型版本操作、文档导入导出 | 未找到本故障的改名修复；[问题 #2](https://github.com/StaniB88/AnyAutomationStudio/issues/2) 是读取 Unified 画面时崩溃，不是脚本类型改名，不能混为同一根因 |
| [Czarnak/totally-integrated-claude](https://github.com/Czarnak/totally-integrated-claude/blob/083525ddd6424f61fdb52033e9e18e1fea68b236/skills/tia-hmi-operations/references/unified-plant-model.md) | 列出 `ScriptModuleType` 及 API 契约 | 类存在或继承可写 Name 不等于真实 setter 在本环境成功 |

仍有尚未完成的对照路径：

1. **优先：独立最小 C# 对照。** 官方[改名示例](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-types)同时列出 `type.Name = ...` 和 `type.SetAttributes(...)`。应在已保存诊断副本中，以新进程分别测试直接属性调用和单元素批量调用，只保留必要的类型定位及 GUID 核验，去掉现有 MCP 快照与反射路径。目的在于区分 API 调用与前序状态，不是承诺换成批量写入即可修好；不应把失败后的自动重试加进生产工具。
2. **官方文档往返方向。** Siemens 的 [WinCC Unified V19 Update 2 说明](https://support.industry.siemens.com/cs/attachments/109820989/ReadmeRTUniUpdate_V19u2_enUS.pdf)明确说明 JS Connector 支持项目库脚本模块类型的导出、外部编辑及重新导入。它提供内容编辑方向，但没有据此验证改名、更没有证明保留原类型/版本 GUID 及引用；需先读真实导出文件和官方导入约束。当前不能称为改名解决方案。
3. **补丁与服务端证据。** 已查 [V21 Unified Engineering Update 2](https://docs.tia.siemens.cloud/r/en-us/v21-updates/tia-portal-updates-readme/improvements-in-wincc-unified/unified-engineering/improvements-in-update-2)和 [Update 2 Hotfix 1 稳定性说明](https://docs.tia.siemens.cloud/r/en-us/v21.0/tia-portal-hotfixes-readme/improvements-in-update-2-hotfix-1/program-stability-when-using-eigen-engineering-agent)，没有找到明确写出修复 `ScriptModuleType.Name` 的条目。不能保证升级必然修复；仍需核对现场安装版本和 WER/ADIAG。

本次复查没有追加原生写入，没有修改 8765 当前连接或将任何候选作为已修复功能发布。

## 证据与结论

用户提供了两次相同的 `CallTool → ManageLibraryType` 请求：

```json
{
  "typePath": "LGen/Types_HMI/Scripts/LSicar_LibraryScripts",
  "action": "update",
  "propertiesJson": "{\"Name\": \"LGen_LibraryScripts\"}",
  "dryRun": false
}
```

`libraryName` 未传，选择项目库。用户记录的 10:19:56 预演通过，10:20:01 和 10:34:45 实际写入均失败。返回中 `requestedProperties` 只有 `Name`，`appliedProperties=[]`，`lastAttemptedProperty=Name`；堆栈从本项目的反射属性 setter 进入 `Siemens.Engineering.Private.Session...SetAttribute`，抛出无详细消息的 `NonRecoverableException`。这定位了失败的 API 边界；空的 appliedProperties 表示没有 setter 完成确认，不能证明服务端没有短暂或部分修改。

用户报告原绑定 PID 32356 已退出，并提供 `portalProcess.processAlive=false`；另一个 PID 36928 也不在之后的进程列表中。新 PID 5268 于用户记录的 10:36:42 启动，打开 AutomaticDipCoatingMachine。本轮没有启动 TIA、附加或操作该工程；后续只检查指定 8765 MCP 的缓存状态、清理旧连接并读取进程元数据（见下文）。关于 9 月 28 日“崩溃 #3”相同的问题以及另一个 TIA 进程退出的原因，尚未独立取得 Windows/ADIAG/前序日志验证。

Siemens 的[通用库类型文档](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-types)提供 `Name` 属性和动态属性改名示例；没有据此确认 Unified `ScriptModuleType` 在全部 V21 Update/Hotfix 下都不支持改名。当前结论是用户环境中这条具体写入路径重复触发原生异常并伴随 TIA 退出，服务端根因和补丁适用范围仍待厂商诊断。不能因为异常发生在 Siemens 内部就免除 MCP 的调用前保护责任。

## 当前交付边界

用户要求是恢复能够正常执行的改名功能。下面的拦截只是临时隔离，**不是该要求的修复结果**。本轮没有把它发布、打包或部署到 8765。属性赋值、批量 `SetAttributes`、版本编辑/释放以及文档导入不是已验证等价的替代路径；不得自动轮流尝试，也不得把另建类型算作保留原 GUID 的改名成功。

## 临时隔离与诊断改进

- 在共用标量属性准备和执行阶段，按 **V21 + `Siemens.Engineering.HmiUnified.Library.ScriptModuleType`（含派生代理）+ `Name` 写入**拦截，返回 `NativeCrashRiskBlocked`。没有不安全的重试开关，不改走 `SetAttribute` 或其他接口绕过。
- 整批属性在第一个 setter 前检查；预演同样拒绝已知危险路径。正常的预演通过仍不等于真实 setter 已验证。
- `ManageLibraryType` 在生成完整类型快照之前执行准备检查，并在请求开始记录 `mayHaveChanged=false`。普通类型改名、V20 和同类型其他属性没有因本次事件被整体禁用；这不是对其原生安全性的证明。
- 按[官方 SetForUpdate 限制](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-libraries-type)，工程库上的写入在请求验证时拒绝；写保护全局库的属性更新在 setter 前拒绝。
- 连接故障摘要保留工具、动作、库名、类型路径、最后尝试属性、已确认写入列表和不确定修改标志，不保存属性值。原生异常仍保留原样，不掩盖为成功。
- 主动拦截消息不包含会被旧错误分类器识别为当前通道故障的异常类名字样。回归检查同时验证拒绝状态不会被 `HmiReadSafety` 或 `PortalFailureClassifier` 判为断连，避免保护措施本身锁住连接。

## 故障后的连接恢复

`HmiConnectionUnavailable` 是共享的工程调用保护层状态名，不说明故障对象一定是 HMI 面板。`NonRecoverableException` 按[官方异常处理文档](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/exceptions/handling-exceptions)需要丢弃已失效的连接。

旧 `AttachToOpenProject` 的同名工程快速路径会验证缓存绑定，故障锁因此返回 `Native channel fault ... explicit recovery required`；仅在 UI 中重开 TIA 不会清除 MCP 的内存状态。本次在错误中加入具体恢复步骤，保持显式恢复和工程身份检查，不自动选择新进程：

1. 对发生故障的那个 MCP 服务调用 `ReadOpennessWorkerStatus`。该工具只读宿主状态，不联系 TIA。
2. 若 `enabled=true`，等待在途/排队请求结束，再调用 `RestartOpennessWorker(confirmRestart=true)`；它只丢弃 worker 和缓存，不启动、保存或连接 TIA。
3. 若 `enabled=false`，且已确认缓存绑定的旧 TIA PID 退出，可显式调用 `Disconnect`，再以 `GetState` 核验 `binding.fault=null`、`snapshotReadsBlocked=false`、`instanceReserved=false`。本例实测这种恢复方式成功。若无法完成或状态未清除，再重启**该 MCP 服务**；不要只重连客户端 HTTP 会话，也不要按名称杀掉所有 TIA/MCP 进程。若旧 TIA 尚在运行，应先独立检查其状态及未保存修改，不能将本例已退出的前提照搬。
4. 调用 `ListPortalProcessProjects`，取新的 `processId`、`processStartUtc`、完整 `projectPath`，再明确调用 `ConnectToProject`。不能复用旧 PID/启动时间或依靠同名缓存。
5. 若报告另一个 MCP 占用或上次实例租约结果不明，保留该保护；不要删租约文件绕过。日志收集和项目状态检查完成前不重放失败写入。本例原 TIA 已退出，新进程的身份应由进程列表重新确定。

通过 lite 桥接调用恢复工具时使用 `CallTool(name="RestartOpennessWorker", argumentsJson={"confirmRestart":true})`。没有隔离模式时该工具返回 `enabled=false`，并不会重置普通 MCP 宿主。

### 本次服务恢复结果

用户明确指定 `192.168.86.128:8765/mcp` 后，10:53:11（服务返回 `-07:00`）读取 worker 状态：`enabled=false/state=InProcess`。10:53:38 的缓存状态确认 PID 32356 已退出，故障仍是 10:34:45 的 `NonRecoverableException`。10:54:17 对此失效连接执行 `Disconnect` 返回成功；10:54:42 `GetState` 确认故障锁、快照阻断和旧租约均清除，且未绑定任何工程。10:55:20 `ListPortalProcessProjects` 成功返回 PID 5268 / `2026-10-01T17:36:42.2320120Z`。没有执行 `ConnectToProject`，也没有重试改名。故障锁解除已验证，新工程附加和写操作未验证。

## 本地验证

V20/V21 临时隔离候选的 Release 构建通过（未发布）；**2,630 项离线测试，0 失败、0 跳过**。新增 13 项检查覆盖危险改名、整批预检、真实写入前的二次保护、故障分类与上下文保留；另两版各增 2 项真实 PublicAPI 类型元数据检查，合计 API 检查为 V20 2,840 / V21 3,126。没有执行真实 SDK setter。

原有 36 项细粒度诊断行为检查及覆盖验证通过，两版未包装受检边界均为 0。最终候选各模式/传输再运行 300 轮：

| 引擎 | 模式 | 本地场景请求 | 协议/认证检查 | 非预期失败/退出/未闭合日志 |
|---|---|---:|---:|---:|
| V20 | normal | 26,484 | 4,200 | 0 |
| V20 | isolated | 26,484 | 4,200 | 0 |
| V21 | normal | 26,484 | 4,200 | 0 |
| V21 | isolated | 26,484 | 4,200 | 0 |

加上发布流程中的 50 轮基础检查，共 **123,872 次本地场景请求、19,600 项协议/认证检查**。只计算与最终源码/引擎/测试脚本哈希一致的 r3 结果，早期候选、构建中止和旧包数字不混入。内存和句柄门限未降低。证明见 [release-build.json](../../manifest/release-build.json) 和 [local-stability-extended-r3-20261001.json](../../manifest/local-stability-extended-r3-20261001.json)。

真实改名操作未重试；本地验证不等于解决 Siemens 服务端缺陷，也不证明其他未执行过的原生操作不会崩溃。8765 的失效连接清理是独立的现场恢复，不计入上述本地压力数字。

## 继续原生定位（用户随后授权独立测试）

- 从 8765 的既有诊断导出完整分页恢复 500 条记录，0 条格式损坏。10:34:45.955 的 `BEFORE` 和 10:34:51.278 的 `THREW` 对应同一 `nativeCallId=b4eeaa4253394f39b1aca61280c7383e`，边界为 `LibraryType::Name`，实际对象为 `Siemens.Engineering.HmiUnified.Library.ScriptModuleType`，线程 3 / MTA。异常链为 `TargetInvocationException → NonRecoverableException`。这不是因为该调用被派发到 STA 线程；仍不能由客户端日志推断 TIA 内部根因。
- 11:13:11 `ConnectIsolated` 成功，新建独立无界面进程 PID 20332，启动时间 `2026-10-01T18:12:50.6276383Z`。11:13:57 新建工程 `C:\Users\SIEMENS\Documents\Automation\MCP_Rename_Diagnostics_20261001\MCP_UnifiedRename_20261001_1113\MCP_UnifiedRename_20261001_1113.ap21`。测试调用前核对完整路径、PID 和启动时间。
- 独立进程只登记了四个系统库，没有 SICAR 参考库。最小全局模块 JS/YAML 样本不能作为已验证的库类型文档：两次 `CreateFromDocuments` 分别以 `MCP_RenameProbe.hmi` 和 `MCP_RenameProbe` 为基名，均收到可恢复的“文件为空或缺失”异常。第一轮后类型数为 0；没有执行 `Name` setter。**不能据此认定复合扩展名就是问题，也不能宣称从零脚本库类型创建已成功。** 不继续猜测文档结构。
- 等待虚拟机中 SICAR 参考工程、全局库或正式导出脚本库类型的路径，以便复制到独立测试工程。所需验收是：改名真实执行并回读；类型 GUID、版本 GUID/状态不被无意改变；引用仍指向同一类型；TIA 和 MCP 连续调用健康。离线检查不替代这些验收。
- `ReadPortalInfo` 只回报产品 `V21`，没有具体 Update/Hotfix，不能以此确定补丁级别。11:25 保存并关闭本轮独立测试工程；11:26:02 释放其连接；11:26:07 核对 8765 未绑定工程、故障为空、快照读取未阻断、未保留实例租约。既有 AutomaticDipCoatingMachine 只读取过进程元数据，没有附加、保存或修改。
- 官方通用库类型改名文档仍是候选接口依据。[官方脚本模块类型创建说明](https://docs.tia.siemens.cloud/r/en-us/v20/runtime-scripting-rt-unified/notes-on-creating-scripts-rt-unified/creating-a-script-module-type-from-a-global-module.-rt-unified)还描述了名称冲突时产生唯一名称；是否存在冲突是待查项，未据此修改实现或认定本次根因。GitHub 精确 `ScriptModuleType` 问题检索未找到可直接验证的改名崩溃修复；普通库类型改名支持不等于本场景经过验证。

## 12:14 续测结果与独立 C# 一键包

本节更新前文“未测试”的历史状态。用户随后指定 AutomaticDipCoatingMachine 项目库的 `LGen/Types_HMI/Scripts/LSicar_GeneralScripts`，并授权测试替代路径；源工程只读取、导出、复制。测试写入均在独立无界面 TIA 进程和诊断副本中执行。

1. 11:39 项目库副本、11:43 全局库副本的 MCP `Name` 写入分别导致 PID 30552、30076 退出。12:03 `Edit()` 成功得到 5.1.2 / InWork；12:04 随后的 `Name` 写入仍导致 PID 904 退出。三次均为同一实际类型 `ScriptModuleType`，均未解决改名，MCP 宿主保留并显式恢复。独立 C# 对照尚未执行，不能仅凭 MCP 栈排除封装与调用上下文的影响。
2. 11:58 从真实类型 5.1.1 成功导出 `Type.def.hmi.js` 和 `Type.def.hmi.yml`。先前将导入基名缩成 `Type`、另加 YAML 别名的试验失败；12:07 使用原文件路径、只去掉最后 `.yml` 后得到原生参数 `Type.def.hmi`，版本导入成功。现有 `Path.GetFileNameWithoutExtension` 在这个样本中正确，**不应修成去掉完整复合后缀**。
3. 12:08 只改 YAML 模块键，成功创建 `MCP_GeneralScripts_DocumentRenamed`，GUID `d9013b09-1684-411f-8b14-546c9a0d6ffc`；12:09 发布为 5.1.1；12:10 保存、关闭，12:12 重开回读仍存在且版本为 Committed。这是已验证的**改名副本创建**，不是原地改名：新 GUID、仅导入一个版本、未迁移引用、最低目标版本从原来的 20.0.0.0 变成 21.0.0.0。现有库读取工具要求 HMI 设备，而空白诊断工程没有 HMI；本轮 MCP 尚未进行新副本正文重导出哈希校验。
4. 12:10 将另一新模块名的 YAML 导入原类型版本，API 返回 Success，但原类型仍叫 `LSicar_GeneralScripts`。因此不能把 API 成功当作改名成功；临时 5.1.2 已 Discard。
5. 12:12 恢复 8765 到原进程 PID 5268；原类型和两个版本的已观察字段与 11:33 基线完全一致。12:14 故障锁为空、快照未阻断、只剩原 TIA 进程。这个比较不代表全工程完整审计。

原生证据及 SHA-256：[`unified-library-rename-followup-20261001.json`](../../manifest/unified-library-rename-followup-20261001.json)。早先两次隔离复现单独保留在 [`unified-library-rename-native-20261001.json`](../../manifest/unified-library-rename-native-20261001.json)。

用户确认虚拟机没有远程命令通道，要求准备一键包。源码和说明位于 [`LibraryRenameProbe`](../../scripts/diagnostics/LibraryRenameProbe/README.md)，以 net48 / x64 对真实 V21 PublicAPI 编译，不打包 Siemens DLL、不依赖 MCP。九组用例覆盖对照、直接 C# 属性、批量属性、Edit 后两种赋值，以及文档新建／版本更新。每组准备和执行分属新建 TIA 进程，保存逐调用日志、进程身份、异常和正文哈希，成功后保存重开验证。

本地 18 项纯离线检查通过；批跑器用独立无 Siemens API 的子进程模拟验证四种情况：单例失败后继续、准备失败停止、缺少结果停止、记录进程仍存活时停止。测试发现并修正 Windows PowerShell 5.1 短命子进程 ExitCode 可能为 null 的问题。以上不是 TIA 原生执行结果；一键包仍需在虚拟机双击运行，原 GUID 改名尚未完成验收。

## 用户返回首次一键包结果：准备阶段路径过长

用户提供 `D:\Code\LibraryRenameProbe-V21-20261001-121722`。其中 `results/20261001-122041-40b08e26` 的原始 `prepare-result.json` 与逐调用日志确认：独立 C# 成功启动 TIA PID 34856；`Projects.Create` 因新工程目录 **149 字符，超过该环境报告的 143 字符上限**而失败。尚未打开诊断输入库，也没有调用任何 Name setter 或文档导入。不能据此评价四种独立改名赋值路径。

这是首版测试包将工程放在嵌套解压目录下的设计遗漏。原始日志中的中文完好，但 Windows PowerShell 5.1 聚合 JSON 时默认解码错误，导致 `cases.json` 中文乱码。此次修正只在本仓库生成新版，不改写用户提供的返回目录。

Revision 2 将原生工程及文档交换目录移到 `%LOCALAPPDATA%\TRP\<输出路径哈希>`。两个工程路径在启动 TIA 前均检查长度、重解析路径与所有权；各用例短目录互不共用。日志和导出文件副本仍归集到包旁的 `results`，原生工程保留并以 `workspace-location.json` 指明位置。PowerShell 显式按 UTF-8 读取结果，另记录等待后的进程退出核验，避免把刚 Dispose 后的瞬时 `alive=true` 当成遗留进程。

48 项离线检查包含真实 149 字符失败路径、九组用例短路径唯一性及 143／144 字符边界。四种批跑模拟场景再次通过，并校验无 BOM 的中文 UTF-8 JSON 汇总、退出核验记录、准备失败不计入改名执行次数。修正后的原生执行仍待虚拟机重跑。

证据、返回二进制一致性与失败边界见 [`library-rename-probe-first-vm-run-20261001.json`](../../manifest/library-rename-probe-first-vm-run-20261001.json)。

## Second returned VM run: DirectoryInfo rejected at export

The user returned `D:\Code\LibraryRenameProbe-V21-20261001-121722\results\20261001-123143-91878807`. This is the second campaign despite the containing folder retaining the first package name. The returned evidence shows that PID 18312 successfully created the short-path project, opened the diagnostic library read-only, copied the source type with `UpdateLibrary`, and read both original version identities. At 12:32:25, its first `ExportAsDocuments` for 5.1.1 failed with `The argument 'directoryInfo' cannot be a relative path.` There were zero execution stages, Name writes, or document imports. The returned folder contains results only, so the r2 executable itself was not hash-verified in this return.

`nativeChannelFaulted=false`; project close and disposal returned. The supervisor subsequently recorded `ownedProcessExitedVerified=true` and `existingPortalsPreserved=true`. The immediate post-Dispose `ownedPortalAlive=true` sample does not indicate an orphaned process after the supervisor's wait. This failure is a preparation defect in the diagnostic package and provides no additional result about direct C# rename behavior.

Local reproduction under .NET Framework confirmed that `Directory.CreateDirectory(absolutePath)` returned an object with an absolute `FullName`, but `ToString()` contained only the leaf name `5.1.1`. The explicit public constructor retained both absolute representations. This matches the failed parameter and [Siemens' documented absolute-path constructor requirement](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/creating-a-directoryinfo/fileinfo-object); private Siemens serialization behavior has not been inspected. Revision 3 uses the explicit constructor for every directory passed to Openness, validates both representations, and journals export/import path arguments. The launcher displays full recorded errors or bootstrap stderr instead of only a generic preparation failure.

The net48/x64 probe compiles with zero errors and warnings. All 64 offline checks pass, including regression cases for version, import, and Unicode/space directories. Four independent supervisor simulations pass, covering continuation after a failed execution, stopping after preparation failure, missing results, and an owned process still alive; they also check native error and bootstrap stderr visibility. These remain offline results. Native export and original-GUID rename still require the next VM run.

Nine returned files were copied byte-for-byte and hashed without modifying the user's directory. See [`library-rename-probe-second-vm-run-20261001.json`](../../manifest/library-rename-probe-second-vm-run-20261001.json) for the exact sequence and failure boundary, and [`library-rename-probe-package-r3-20261001.json`](../../manifest/library-rename-probe-package-r3-20261001.json) for package verification.
