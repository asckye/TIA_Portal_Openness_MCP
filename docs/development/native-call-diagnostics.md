# 原生调用的细粒度诊断覆盖

v3.2.0 的 V20/V21 **Release 构建**包含原生调用诊断覆盖。范围是本项目编译进引擎的 Openness 调用边界，包括已接入的 OPC UA 建模源码；不修改或分发 Siemens PublicAPI，不注入 TIA 进程。它能定位最后执行到哪个调用，不能保证 TIA 不崩溃，也不能看到 Siemens SDK 或 TIA 服务进程内部的每一层调用。

## 覆盖机制

`tools/native-call-weaver` 使用固定版本 Mono.Cecil 0.11.6，在编译完成、复制 EXE 之前给调用点生成包装方法。构建逐项清点，实际二进制内嵌 `TiaMcp.NativeCallCoverage.json`，记录调用者、原始 IL 位置、目标成员及唯一调用点 ID。

- 直接调用：`Siemens.Engineering*` 程序集中的方法、构造器、属性 getter/setter、索引器、事件订阅与取消。
- 反射调用：`MethodInfo/ConstructorInfo.Invoke`、属性/字段读写、`Activator.CreateInstance(Type)` 等；接收者或反射成员属于 Openness 时才记录原生阶段。
- 接口访问：工程对象的集合/枚举接口，以及 `IDisposable` 等接口调用。结果对象的访问来源使用弱引用表保存，不依赖原生 `Name`、`Parent` 或其他额外 getter。
- 外部遍历：LINQ、集合构造器接收已知的原生 `IEnumerable` 时，适配其 `GetEnumerator`、`MoveNext`、`Current`、`Dispose`；保留集合计数、复制和索引访问的常规快捷路径。`AsEnumerable` 保持原引用。
- 对象虚方法：原生对象经 `object` 调用的 `ToString`、`Equals`、`GetHashCode`，以及相应转换边界。

每个调用点使用嵌套于原调用者的独立生成类型，同时保持私有类型访问权限，避免本地租约、日志等纯托管操作提前加载无关的 Siemens 程序集。原生方法组改成显式 lambda；新出现的未处理原生函数指针、动态代码生成、代理实例字段访问等路径会使发布构建失败，不能静默跳过。CLR 本地字段/元数据访问不是工程 IPC 调用。诊断运行时本身独立于 Siemens 引用，避免日志递归进入工程接口。

Release 构建没有关闭覆盖的开关。普通 Debug 编译保留原调试方式，`GetState.journalHealth.nativeBoundaryCoverage` 会明确返回 `not-instrumented`；不能将该产物当作通过发布检查的候选包。

## 日志与读取

使用已有 `ReadNativeInvocationLog(take=100)` 获取近期记录，或用 [证据收集脚本](../../scripts/diagnostics/Collect-TiaCrashEvidence.ps1) 保存日志及 Windows 事件。目录默认是 `%LOCALAPPDATA%\TiaMcp\diagnostics`，可通过绝对路径环境变量 `TIA_MCP_DIAGNOSTICS_DIRECTORY` 设置。

细粒度记录包含：

| 字段 | 含义 |
|---|---|
| `id` | 所属 MCP 请求的关联 ID |
| `nativeCallId` / `parentNativeCallId` | 本次边界调用及嵌套关系；同一方法重复访问也有不同 ID |
| `phase` | `BEFORE`、`RETURNED` 或 `THREW` |
| `callSite` / `member` / `dispatch` | 构建调用点、成员及调用方式 |
| `objectType` / `objectId` | 对象类型及本进程内的弱引用身份 |
| `objectPath` / `pathKind` | 已观察到的访问链，不冒充完整的工程规范路径 |
| `binding` | 已缓存的主版本、PID、启动时间、工程路径与绑定代次 |
| `threadId` / `apartment` | 执行线程及公寓模型 |
| `mcpProcessId` | 写入记录的 MCP 宿主或 worker 进程 |
| `exceptionType` / `hresult` / `exceptionChain` | 有界异常类型链，不保存异常消息或业务内容 |

记录 `Find`/属性访问的选择名称可帮助定位对象；不记录密码、脚本正文、变量值、参数数组或返回内容。为写日志不会额外调用工程 getter、对象 `ToString` 或 `GetHashCode`。无法取得工程规范路径时使用访问链和对象 ID，不进行额外遍历。

`BEFORE` 写入后刷盘；正常返回或抛错各有一个对应终止记录。异常使用原异常重新抛出，不重试、不更换线程、不改变 `ref/out` 或写入参数。日志 I/O 失败不阻止原操作，失败计数在 `GetState.journalHealth` 中可见。

按 `nativeCallId` 配对。如果 `BEFORE` 缺少终止记录，应结合进程退出时间、Windows 事件和前序调用定位中断。日志轮转、截取窗口、I/O 故障也可能导致记录缺失；不能单凭缺失证明某个 API 导致崩溃。日志仍采用约 10 MiB 当前文件加一个 `.previous` 文件的有界保留方式，需要及时收集。

## 验证与交付检查

`Build-Release.ps1` 会运行：

1. 独立假 API 的实际执行测试：构造、属性/索引器、反射、泛型、值类型、`ref/out`、异常原样传播、遍历与释放、集合快捷路径、日志内容及 I/O 故障。
2. 测试进程主动退出：确认最后一条原生 `BEFORE` 已持久化，且不存在伪造的完成记录。该测试不启动 TIA。
3. 破坏覆盖记录、包装终止逻辑或调用关系的拒绝测试，以及重复处理不改变二进制的检查。
4. V20/V21 二进制逐调用点检查和非开放泛型包装方法的 JIT 预编译；泛型模式通过替身执行验证，开放泛型并不等于已经执行过真实接口。
5. 原有离线套件、HTTP/STDIO、full/lite、普通/隔离模式检查及本地压力测试。

验证结果、覆盖清单哈希和构建工具哈希写入 `manifest/release-build.json`。构建工具源码也纳入编译输入哈希，打包拒绝缺少覆盖证据或构建后改变的输入。

真实工程上的原生调用延迟、日志开销、长时间运行及故障归因仍需虚拟机验收。本次本地检查不会启动/连接 TIA，也没有解禁 PLC 原生交叉引用。

依据：[项目内保存的 Siemens 崩溃诊断指南](../../reference/siemens-openness/skills/crash-diagnosis/SKILL.md)、[Mono.Cecil 官方说明](https://www.mono-project.com/docs/tools%2Blibraries/libraries/Mono.Cecil/)、[固定 NuGet 版本](https://www.nuget.org/packages/Mono.Cecil/0.11.6)。

## 与工作进程隔离的关系

V20/V21 的可选 `--isolate-openness` 把工程调用放入同 EXE 的独立 worker，
宿主保留 MCP 传输、认证、分页和独立诊断。私有握手核对协议、版本、构建哈希与工具表，
每个 worker 串行处理，最多 16 个在途/排队请求，使用 10–180 秒总期限。
开启方法和恢复步骤见[工作进程指南](../guides/openness-worker-isolation.md)。

超时、退出、管道断裂或错序使绑定和旧句柄失效；已发出的原生请求报告结果未知。
宿主只停止自己的 worker，不结束 TIA，不自动重放、保存或换绑。
精确工程身份包括版本、PID、OS 启动时间、规范化完整路径与绑定代次。
同 Windows 用户的会话租约阻止本项目新版本 MCP 重复附加同一实例，
不覆盖其他用户、旧版本或其他 Openness 客户端。

这套身份机制属于完整引擎，不能直接套用到 Foundation 的 PID-only 契约。
Foundation 的绑定快照接线尚未完成，见[版本框架](unified-version-framework.md)。
工作进程与调用边界日志分别提供故障隔离和定位证据；都不能保证 TIA 服务端不崩溃。
真实延迟、长期运行和故障恢复验收仍为 NOT RUN。
