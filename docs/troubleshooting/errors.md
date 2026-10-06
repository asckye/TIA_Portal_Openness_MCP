# 调用失败时如何处理

## V20/V21：Openness 环境未就绪

V20/V21 MCP 服务可以在没有 TIA、TIA 版本与引擎不匹配、Openness API 初始化失败或用户组检查失败时启动，以保留诊断入口。运行 `InitializeEnvironment` 和 `GetEnvironmentDiagnostics` 查看原因及修复步骤。

需要 TIA 的调用此时返回 V4 `RESOURCE_UNAVAILABLE`，`error.details.resource` 为 `tia-openness-environment`，`meta.outcome` 为 `rejected-before-operation`、`meta.execution` 为 `not-started`。这表示工具未将操作派发给 Siemens API。修复安装、版本或用户组后重启服务，再读取 `GetOpennessWorkerStatus` 确认环境就绪。

For V20/V21, a missing TIA installation, version mismatch, Openness initialization failure, or failed group check leaves the MCP host available for diagnostics. TIA-dependent calls return `RESOURCE_UNAVAILABLE` for `tia-openness-environment`; the V4 metadata reports `rejected-before-operation` / `not-started`. Read `InitializeEnvironment` and `GetEnvironmentDiagnostics` for the cause and repair steps. After correcting the environment, restart the service and check `GetOpennessWorkerStatus`.

先记录当前服务版本、工具名、返回错误及调用 ID，再按实际问题处理。同名工具在基础宿主和完整引擎上的返回结构可能不同，使用 `GetToolUsage` 核对正在连接的版本。

| 现象 | 下一步 |
|---|---|
| 参数缺少、类型错误或动作不支持 | 读取当前 schema 和对应 `operation` 示例，按返回的参数名修正；不要套用其他版本签名。 |
| 对象不存在或名称有歧义 | 重新读取选定工程的 PLC、组和对象路径，使用准确返回值。文件路径必须属于服务所在电脑。 |
| 无法导出不一致的块/类型 | 按实际目标编译并检查错误和嵌套诊断。批量导出同时核对成功项、跳过项和失败项。 |
| HTTP 401 / 无法建立连接 | 核对配置器中的地址、端口与密钥；连接测试通过后仍需确认工程绑定。详见[配置指南](../getting-started/configuration.md)。 |
| 原生 API / I/O / 选件异常 | 核对安装版本、选件和返回的原生原因。缺少服务不能解释为集合为空或操作成功。 |
| TIA 退出、句柄失效或连接受阻 | 先检查 TIA 进程和工程实际状态，再查看调用日志及[原生限制](openness-limitations.md)；不要自动换接口重放。 |
| 写入结果未知或部分成功 | 检查已返回的对象与文件以及 `mayHaveChanged` 等字段，再决定补救动作。异常不保证原子回滚。 |

`success`、原生导入状态、内容核对、编译结果、工程保存结果需要分别判断。质量审计的 `qualityPassed` 和 XSD 的 `fragmentSchemasPassed` 才是对应检查结论。

完整引擎可用 `GetOpennessCompatibility` 查看加载程序集版本，用 `GetNativeInvocationLog` 读取已记录的调用边界。日志中的 `BEFORE` / `RETURNED` / `THREW` 用于定位时间和调用；它们不替代业务结果，也不能单独确认 Siemens 崩溃根因。HMI 快照的独立日志及完整性说明见[快照诊断](hmi-snapshots.md)。

需要本机退出证据时，在 Workbench 环境页导出“诊断包”。它会收集最近 24 小时的匹配 Windows 事件、TIA/引擎进程和转储文件清单，以及存在的原生导出日志；转储文件内容不会复制。包内日志和事件经过脱敏，但分享前仍应复核，因为系统消息可能包含私有路径或工程信息。

开发者应沿用现有错误类型和结果封装，不在本页维护第二套编码、异常装饰或日志规范。仓库行尾与编码以 `.gitattributes` 和现有构建检查为准。
