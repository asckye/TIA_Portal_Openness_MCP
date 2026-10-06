# Openness 与运行时通道的边界

## V20/V21 host 与 worker 的就绪状态

V20/V21 MCP host 在 TIA 未安装、检测到的 TIA 版本不匹配、Openness DLL 无法初始化或用户不在 `Siemens TIA Openness` 组时仍可提供 MCP 诊断。`InitializeEnvironment`、`GetEnvironmentDiagnostics` 和 `GetOpennessWorkerStatus` 显示失败原因与修复建议。需要原生 TIA 的工具在 dispatch 前返回 `RESOURCE_UNAVAILABLE`；环境门禁拒绝时，V4 元数据为 `rejected-before-operation` / `not-started`。读取、离线分类和诊断工具可继续使用。

隔离运行时，MCP transport、身份验证和诊断由 host 提供，Openness 调用由同一 V20/V21 EXE 的 worker 进程承担。worker 已接收调用后发生超时或崩溃时，不得报告为“操作前拒绝”；需要先检查工程实际状态。重启 worker 后必须重新连接并显式绑定目标工程。4.0 发布版保持默认隔离关闭，直到本页之外的 VM 验收完成；运行手册见[worker 隔离指南](../guides/openness-worker-isolation.md)。

For an isolated call, a timeout or crash after dispatch may leave the native outcome unknown. Inspect the project before retrying, restart only an idle/faulted worker, then explicitly connect and bind again. Isolation does not make a dispatched TIA operation transactional or cancellable.

本页描述 v3.2.0 当前实现和已知现场问题。八版入口并不具有同一套能力，先核对[版本矩阵](../reference/version-tools.md)与当前服务 `GetToolUsage`。官方 API 存在、工具可列出、离线测试通过、真实工程执行成功是不同证据。

## 已知未解决的现场问题

| 问题 | 当前处理与剩余边界 |
|---|---|
| `ManageStartdriveParameter` 读取 BICO `p2051[0]` 时 TIA 崩溃 | 已按官方 `Parameters.Find(name).Value` 路线改为精确单值读取，仅在找不到条目时检查只读视图；不展开位、限值、枚举或继续读取源参数值。离线回归通过，原生复测 **NOT RUN**，根因未确认。返回另一个 `DriveParameter` 表示连接来源，普通值按标量解释，`null` 保留未知，不能当作零、实时值或未连接证明。 |
| Unified 脚本模块库类型 `Name` 修改 | 指定样本在项目库和独立全局库两次测试中均伴随 TIA 退出，没有已验证修复；“先在全局库改名再同步”也失败。[原生证据](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/manifest/history/unified-library-rename-native-20261001.json)。 |
| PLC 原生交叉引用 | V21 有真实退出记录，默认禁用。工具返回未查询不等于零引用。编译通过也不能证明查询稳定。通用反射入口不能绕过相同策略。 |
| HMI 深层属性读取 | 故障或限制下可能仅返回部分快照；`HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` 暂缓读取。[快照诊断](hmi-snapshots.md)说明完整性、会话阻断与日志。 |

这些项目仍使用统一调用示例库，不再各自建立规范入口。官方示例：[V20 BICO](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)、[V21 BICO](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters)。示例的正确参数不能消除尚未确定的原生故障。

PLC 交叉引用的显式诊断开关为服务进程环境变量 `TIA_MCP_ENABLE_NATIVE_PLC_CROSS_REFERENCES=1`；启用后仍检查相关块/类型一致性。它不是日常使用必需步骤，也不是稳定性修复。离线 `AnalyzePlcReferences` 只覆盖提供的导出文件，不能据此证明整个工程没有引用。

## 工程数据与在线数据

| 通道 | 当前可以做什么 | 前提与限制 |
|---|---|---|
| TIA Openness | 工程对象读写、导入导出、编译；完整引擎还有上线/离线、下载、站上载和在线比较等指定接口。 | 取决于版本、对象、安装选件与原生 API。工程标签或 DB 初始值不是 CPU 实时值。 |
| S7 Web server API | 完整引擎的 `GetPlcWebVars` / `WritePlcWebVars`、`GetPlcWebDiagnostics`、`SetPlcWebOperatingMode`。 | PLC 固件及 Web API 支持、账户权限和证书；独立于 Openness。诊断工具的设备信息和运行模式不等于完整故障缓冲区。 |
| Unified Open Pipe | 完整引擎读取/写入 Runtime 标签、读取活动报警及限定的单次消息。 | MCP 运行在 Runtime 本机，用户具备对应组权限；不提供任意远程管道或持续订阅。 |
| PLCSIM Advanced API | 完整引擎管理仿真实例、读写标签并运行限定场景。 | 本机安装匹配 API；仿真通过不等于真实 CPU 验收。 |
| 其他运行时/伴随工具 | 按各工具声明的 OPC UA、S7 或独立程序接口执行。 | 分别检查连接目标和实际协议，不能因 Openness 已连接就视为这些通道已就绪。 |

基础宿主只提供其公开的 PLC 子集，不因完整引擎存在某个运行时工具而自动具备该工具。完整引擎中部分选件入口仅在一个版本有实际调用路径，详情见[逐版 Openness 覆盖](../reference/openness-coverage.md#v20v21-optional-tools-and-scope)。

## 当前没有可承诺的 Openness 路线

- 独立 CPU RUN / STOP 不是普通 Openness 在线接口；下载回调中的停止/启动属于下载流程。需要独立模式切换时核对实际运行时工具。
- 未实现通过 Openness 读取完整 CPU 诊断缓冲区或在线模块 LED / 健康状态的通用路线。
- 不提供运行时强制/解除强制入口。删除离线强制表或下载工程不能作为“已清除 CPU 强制”的证明。
- 没有按任意块集合选择性下载的已验证通用封装。
- 未证实可从零创建 Unified 面板库类型、直接修改类型内部所有控件和局部脚本，或通过公开 API 重建 PLC 交叉引用索引。
- Unified 普通按钮事件与全局脚本模块使用不同接口；CWC ZIP、普通画面对象与面板库类型也不能互换。

`ScanAccessibleDevices` 已能经指定 PC 接口查询可访问设备，不应再列为“只能看工程配置”。监控表定义、修改值和当前值的读取能力依对象而异；定义编辑成功不能冒充已经向 PLC 执行了一次修改。

## 读懂结果再继续

导入先检查状态、消息与实际返回对象；SIMATIC SD 使用目录及不带扩展名的文件名。编译检查错误数和完整嵌套诊断，不能只看请求返回。保存工程需要独立保存操作；下载是另一项明确动作。

当返回 `mayHaveChanged`、部分结果或结果未知时，先核对当前工程与日志，避免自动重复写入。`RETURNED` 日志只证明调用返回，最后一个 `BEFORE` 也不能单独证明崩溃根因。真实验收与历史原文见[证据索引](https://github.com/asckye/TIA_Portal_Openness_MCP/blob/master/docs/reference/real-machine-ledger.md)。
