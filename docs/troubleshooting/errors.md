# 调用失败时如何处理

本文适用于 4.0 八个发布版本。先记录服务版本、工具名和完整的 V4 结果，检查 `error.code`、`error.details`、`meta.outcome`、`meta.execution` 与 `meta.requestId`，再按下表处理。

## 按错误码处理

| 错误码 | 原因 | 下一步 |
|---|---|---|
| `CONFIRMATION_REQUIRED` | 工作台拒绝、超时或不可用，或计划尚未确认；查看 `error.details.reason` | `denied` 表示人已拒绝；`timeout` 表示等待超时；`workbench-unavailable` 时在 TIA/MCP 所在电脑打开同一数据根的工作台。核对目标及参数后重新发起需要的请求，在工作台审批；`plan-confirmation` 按当前工具示例重新预览并确认计划，AI 不能代替人工批准 |
| `NATIVE_OPERATION_FAILED` | 原生操作报告失败；错误码本身不能证明修改已经回滚 | 查看去敏后的 `error.message`、`error.details`、结果状态及调用日志，检查 TIA 中的实际对象、依赖、安装和选件；结果未知时走恢复流程，原因未查清前不重复写入 |
| `OUTCOME_UNKNOWN` | 本次已派发写入的最终结果无法确定 | 检查目标和文件的实际变化，保留诊断。`GetSessionState` 列出未确认请求编号。后续独立调用可执行，保存时核对 `UNSETTLED_CALLS`，禁止自动重放 |
| `RESOURCE_UNAVAILABLE` | 所需环境或服务尚未就绪 | 对 TIA 环境运行 `InitializeEnvironment` 和 `GetEnvironmentDiagnostics`，按下节修复版本、安装或用户组 |
| `INVALID_ARGUMENT` | 参数缺失、类型或路径格式不符合当前版本 | 用 `GetToolUsage` 读取该版本的参数与示例，按指出的字段修正 |

工作台审批拒绝、超时或不可用时，`CONFIRMATION_REQUIRED` 对应操作尚未开始；它不等于原生操作失败。修复拒绝原因后重新请求，不要通过关闭审批绕过它。

## 结果未知时核对调用

`OUTCOME_UNKNOWN` 只描述该调用。通过 `meta.requestId` 关联日志，检查 TIA 项目和文件中的实际结果。`GetSessionState.data.sessionState.unsettled` 保留未确认调用，工作台显示其数量。项目切换或会话结束清除清单。

`SaveProject` 和 `SaveProjectCopy` 的预览与执行会携带 `UNSETTLED_CALLS`，列出需要核对的请求。后续独立请求继续使用正常的审批、项目身份及计划校验。

真正的 worker 传输故障目前返回 `PRECONDITION_FAILED`，`condition=worker-faulted`，按提示调用 `RestartOpennessWorker`。预算、超时排空和租约规则见 [worker 与恢复](../guides/openness-worker-isolation.md)。

## V20/V21：Openness 环境未就绪

V20/V21 MCP 服务可以在没有 TIA、TIA 版本与服务的发布版本不匹配、Openness API 初始化失败或用户组检查失败时启动，以保留诊断入口。运行 `InitializeEnvironment` 和 `GetEnvironmentDiagnostics` 查看原因及修复步骤。

需要 TIA 的调用此时返回 V4 `RESOURCE_UNAVAILABLE`，`error.details.resource` 为 `tia-openness-environment`，`meta.outcome` 为 `rejected-before-operation`、`meta.execution` 为 `not-started`。这表示工具未将操作派发给 Siemens API。修复安装、版本或用户组后重启服务，再读取 `GetOpennessWorkerStatus` 确认环境就绪。

不同发布版本的工具参数及结果可能不同，使用 `GetToolUsage` 核对正在连接的版本。

## 按现象排查

| 现象 | 下一步 |
|---|---|
| 参数缺少、类型错误或动作不支持 | 读取当前 schema 和对应 `operation` 示例，按返回的参数名修正；不要套用其他版本签名。 |
| 对象不存在或名称有歧义 | 重新读取选定工程的 PLC、组和对象路径，使用准确返回值。文件路径必须属于服务所在电脑。 |
| 无法导出不一致的块/类型 | 按实际目标编译并检查错误和嵌套诊断。批量导出同时核对成功项、跳过项和失败项。 |
| HTTP 401 / 无法建立连接 | 核对工作台中的地址、端口与密钥；连接测试通过后仍需确认工程绑定。详见[配置指南](../getting-started/configuration.md)。 |
| 远程/虚拟机连接出现 HTTP 502 或 ECONNRESET | 打开环境体检的“代理与远程连接”，比较直连与代理探测。无密钥直连返回 401、代理返回 502/0 时，选择“使用中继…”并确认迁移及备份，然后重启客户端。手工配置的 NO_PROXY 必须存在于客户端实际会话环境中。 |
| HTTP 403 | 核对 Host、Origin 与服务监听端点及允许列表；反向代理须使用人工配置的准确源，不要关闭校验。 |
| RELAY_CONNECTION_FAILED | 在 AI 所在电脑用同一 Windows 用户保存正确端点与密钥，核对本地交付包和数据目录；检查代理及远程连接项。中继不会自动重放请求。 |
| 原生 API / I/O / 选件异常 | 核对安装版本、选件和返回的原生原因。缺少服务不能解释为集合为空或操作成功。 |
| TIA 退出、句柄失效或连接受阻 | 先检查 TIA 进程和工程实际状态，再查看调用日志及[原生限制](openness-limitations.md)；不要自动换接口重放。 |
| 写入结果未知或部分成功 | 检查已返回的对象与文件以及 `mayHaveChanged` 等字段，再决定补救动作。异常不保证原子回滚。 |

`success`、原生导入状态、内容核对、编译结果、工程保存结果需要分别判断。质量审计的 `qualityPassed` 和 XSD 的 `fragmentSchemasPassed` 才是对应检查结论。

## 查找调用日志与诊断包

调用日志文件为 `calls-*.jsonl`，轮换文件为 `calls-*.jsonl.previous`。日志目录依次取：

1. `TIA_MCP_DIAGNOSTICS_DIRECTORY` 显式指定的绝对目录。
2. 数据根的 `diagnostics` 子目录。可写交付包默认为 `<包根>/data/diagnostics`；设置了 `TIA_MCP_DATA_DIRECTORY` 时为 `<该数据根>/diagnostics`。
3. 无可用数据根时回退到 `%LOCALAPPDATA%/TiaMcp/diagnostics`。

用结果的 `meta.requestId` 关联相应调用。调用日志与审批审计不同；审批审计默认在 `<数据根>/logs/audit`。先保留故障时的日志，再重启会话；分享前检查路径、项目内容和凭据是否已脱敏。

V20/V21 可用 `GetOpennessCompatibility` 查看加载程序集版本，用 `GetNativeInvocationLog` 读取已记录的调用边界。日志中的 `BEFORE` / `RETURNED` / `THREW` 用于定位时间和调用；它们不替代业务结果，也不能单独确认 Siemens 崩溃根因。HMI 快照的独立日志及完整性说明见[快照诊断](hmi-snapshots.md)。

需要本机退出证据时，在工作台环境页导出“诊断包”。它会收集最近 24 小时的匹配 Windows 事件、TIA/MCP 服务进程和转储文件清单，以及存在的原生导出日志；转储文件内容不会复制。包内日志和事件经过脱敏，但分享前仍应复核，因为系统消息可能包含私有路径或工程信息。

开发者应沿用现有错误类型和结果封装，不在本页维护第二套编码、异常装饰或日志规范。仓库行尾与编码以 `.gitattributes` 和现有构建检查为准。

断开后可在同一 MCP 会话调用 `ConnectPortal` 和 `AttachOpenProject`。新连接的 `PREVIOUS_OWNER_UNCLEAN` 表示前持有者留下 `BUSY`、`UNCERTAIN` 或遗留 `ACTIVE` 记录，详情在 `data.previousOwner`。未决编号进入当前会话并标记 `fromPreviousOwner=true`。检查这些调用的目标，保存时核对 `UNSETTLED_CALLS`。
