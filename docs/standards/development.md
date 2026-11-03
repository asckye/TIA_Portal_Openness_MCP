# 开发规范

状态：现行 · 最后核对：2026-11-03，master `1dfe2067`

本文是本仓库唯一的开发规范，规定代码、仓库结构、新工具开发、测试、Git、AI 分工和 C# 偏差登记，取代归档的 [C# 与 .NET API 设计规范](../archive/development/design-spec/csharp-design.md)、[C# 偏差清单](../archive/development/design-spec/deviations/csharp.md)、[工具开发](../archive/development/tool-development.md)、[仓库结构](../archive/development/repository-layout.md) 和本文件原来的测试策略。

## 1 范围与依据

- 本文只写本项目的规则和对通用手册的取舍；手册已有的规则只链接对应章节。
- 不在本文的内容：MCP 工具契约见 [设计规范](design.md)；安全见 [安全标准](security.md)；工作台界面见 [工作台界面规范](ui.md)；文档写作见 [文档规范](documentation.md)；任务流程和检查分档见 [开发流程](../manuals/workflow.md)；命令、作业和输出目录见 [验证分层](../manuals/validation.md)；发布见 [发布流程](../manuals/release.md)。
- 依据：[《工程开发手册》v1.1](engineering-handbook.html)（领域 A、C、D，档位 M，审批和数据安全按 L，见 [采用说明](README.md)）；[ADR 0015](decisions/0015-microsoft-design-spec-baseline.md)（C# 以 Microsoft 设计准则为基线）；[AGENTS.md](../../AGENTS.md)（Codex 执行规则）；[CLAUDE.md](../../CLAUDE.md)（仓库通用规则）。
- 用语：MUST、SHOULD、MAY 按 [文档规范](documentation.md) 第 0 节；[P] 表示超出或改变 Microsoft 准则的项目规则。

规则冲突时从上到下裁决：

| 顺序 | 来源 |
|---|---|
| 1 | 外部契约与安全不变量：MCP V4 契约（`manifest/contracts/v4/baseline`）、worker 协议 2 线格式、Openness 调用的顺序、参数和线程归属、文件格式与 manifest。命名或风格规则不得为此改变它们 |
| 2 | ADR（[索引](decisions/README.md)） |
| 3 | 本文、AGENTS.md、CLAUDE.md |
| 4 | 手册与 Microsoft 来源：[Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)（FDG）、[.NET runtime coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)、[C# coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) |
| 5 | 所在文件的既有风格：只在第 8 节的偏差处理完之前、修改旧文件时沿用；新文件按本文 |

## 2 代码

### 2.1 适用范围与程序集分级

- 适用于 `src/`、`build-tools/`、`tests/`、`scripts/` 中的 C#；测试与脚本按第 5.9 节放宽。`third_party/`、`reference/`、`sdk/` 和生成文件不适用（B-03、B-08）。
- C# 的基线是手册 [附录 L1](engineering-handbook.html#lcs) 和 [第 3 节](engineering-handbook.html#code)。本节只列项目规则和取舍；未列出的命名、类型、成员、异常、异步和资源规则按 L1 与 FDG 执行。
- MCP 工具入口方法（`[McpServerTool]`）是独立的表面：C# 方法名不上线，参数名、参数类型和默认值上线，由契约基线冻结（N-12）。

| 级别 | 程序集 | 应用方式 |
|---|---|---|
| A 契约与共享库 | `TiaMcp.Adapters.Contracts`、`TiaMcp.WorkerChannel`、`TiaMcp.Logic`、`TiaOpenness.Contracts`、`TiaMcp.BuildCommon`、`TiaMcp.OnlineChannels` | 跨进程、跨版本或被多个宿主引用；FDG 的公共 API 规则完整适用，线上 DTO 例外见 U-01 |
| B 应用 | FoundationHost、EngineHost、PlcWorker、八个适配器、工作台、Updater、WriteGuard、release tool、native-call-weaver | 公共表面最小；FDG 的类型、成员和异常规则作 SHOULD |
| C 测试与脚本 | `tests/`、`scripts/` | 同 B，另按第 5.9 节放宽 |

### 2.2 C#/.NET 设计

同一份代码常同时面向 .NET Framework 4.8 和 .NET 10：

| 问题 | 规则 |
|---|---|
| 语言版本 | 所有工程 `LangVersion latest`（CS-25）。面向 net48 的程序集只用纯编译器特性，MUST NOT 用默认接口成员、`static abstract` 接口成员等需要运行时支持的特性 |
| 缺少的特性类型 | net48 需要 `init`、`required` 或可空分析特性时，用共享源码中的 `internal` polyfill，每个程序集只编入一份，不引入新包 |
| BCL 差异 | 多目标代码用 `#if NET`、`NETFRAMEWORK` 或共享辅助方法，不用发布版本宏代替框架宏 |
| 全球化 | net48 用 NLS，.NET 10 用 ICU，文化敏感的比较可能得出不同结果；因此 U-06、U-07 为 MUST |

| ID | 级别 | 规则 |
|---|---|---|
| T-03 | MUST [P] | A 级的公共 DTO、异常、特性和值对象 `sealed`：默认密封，按设计开放 |
| T-08 | MUST [P] | 跨版本边界用接口（`IOpennessAdapter` 及各 facet），扩展面不可用时为 `null`；每个抽象至少有一个实现、一个使用方和测试。给已有 facet 增加成员时，八个适配器和所有宿主在同一提交内实现 |
| T-09 | SHOULD | 值对象和 V4 契约类型不可变：只读属性加构造函数校验（范例 [`Envelope.cs`](../../src/Logic/V4/Envelope.cs)）；线上 DTO 可按序列化器需要保留 setter |
| T-10 | SHOULD NOT [P] | 用新增的 `partial` 扩大已有的大类型；`partial` 只用于 XAML、生成代码和文档化的拆分 |
| T-11 | MUST NOT | 在两个程序集中重复定义同一 DTO、枚举或异常；共享类型放在 A 级契约程序集 |
| M-05 | SHOULD | 新的内部 API 有两个以上布尔参数时改用枚举或请求对象。[P] MCP 工具参数及镜像它们的 facet（`dryRun`、`confirm`、`overwrite`、`preservePath` 等）是线上形状，保持布尔 |
| M-07 | MUST [P] | MCP 工具方法用默认参数表达可选的 JSON 参数；`internal`、`private` 方法可以用；A 级接口的新成员不带默认值，改用重载或请求对象 |
| X-04 | MUST [P] | 宿主差异通过显式选项或策略对象（`ThreadPolicy`、行为策略）注入共享原语，不通过子类重写原生调用序列 |
| U-01 | MUST [P] | A 级公共签名不用 `List<T>`、`Dictionary<,>` 等具体集合，只读结果用 `IReadOnlyList<T>` 一类接口；线上 DTO 和生成模型（JSON 直接绑定、不含行为）可以用数组、`List<T>` 和 setter |
| U-04 | MUST [P] | 受织入的适配器程序集中，响应构造器和日志辅助方法的参数不含 `IEnumerable`；向 Siemens 程序集传可枚举对象的代码只留在适配器内（native-call-weaver 按此插桩） |
| U-05 | MUST | 以字符串为键的字典和集合显式指定比较器；标识符和路径用 `StringComparer.Ordinal` 或 `OrdinalIgnoreCase` |
| U-06 | MUST | `StartsWith`、`EndsWith`、`IndexOf(string)`、`Compare` 等显式传 `StringComparison`；标识符、路径、协议令牌和 XML 名称用 `Ordinal` 或 `OrdinalIgnoreCase` |
| U-07 | MUST | 机器数据的解析与格式化用 `CultureInfo.InvariantCulture`；大小写转换用 `ToUpperInvariant`、`ToLowerInvariant` |
| U-10 | MUST | 新代码只用 System.Text.Json，不新增 Newtonsoft.Json；不用 `BinaryFormatter` 一类不安全的序列化器，新类型不加 `[Serializable]` |
| U-12 | MUST [P] | 响应 POCO、V4 类型和 worker DTO 的成员顺序、命名策略、null 处理、数值类型和时间戳编码是线上契约，只随契约与响应基线一起改变；字段改属性、改 `IncludeFields`、换序列化选项都属于这类改变 |
| U-13 | SHOULD [P] | V4 线上类型显式写 `[JsonPropertyOrder]` |
| U-14 | MUST | 处理工程导出、用户文件等不受信任的 XML 时禁止 DTD、不解析外部实体（`DtdProcessing.Prohibit`，net48 另设 `XmlResolver = null`）；公共 API 不以 `XmlDocument`、`XmlNode` 作参数或返回值 |
| U-16 | MUST NOT [P] | MCP 工具类和领域服务单例实现 `IDisposable`；SDK 每次调用后会释放目标对象 |
| U-17 | MUST [P] | `TiaPortal`、`Project` 等 Openness 对象只由拥有它的会话在所属线程上释放；断开不保存、不关闭、不结束 TIA 进程 |
| U-19 | MUST NOT | 在 UI 线程或同步上下文中同步等待异步操作（`.Result`、`.Wait()`、`GetAwaiter().GetResult()`）；允许的位置（进程入口与 CLI 主流程、所属线程上包装同步原生 API、SDK 同步回调）写注释说明 |
| U-23 | MUST | 线上、日志和持久化的时间戳用 `DateTimeOffset.UtcNow`，时长用 `Stopwatch`；`DateTime.Now` 只用于本地显示和文件名。冻结的旧 meta 时间戳在旧族移除前不改（CS-44） |
| U-24 | SHOULD NOT | 引入可变静态状态；必需的进程级钩子只在启动时设置一次，并说明线程安全 |
| U-26 | MUST [P] | 版本条件编译只用 [`TiaFeatures.props`](../../src/Shared/TiaFeatures.props) 的特性符号、SDK 框架符号（`NET`、`NETFRAMEWORK`、`NET48`）和文档化的构建角色符号，不新增临时版本宏（`Check-TiaFeatures.py` 核对） |
| NR-01 | MUST | 新工程 `<Nullable>enable</Nullable>`；文件级 `#nullable disable` 只用于链接进禁用工程的旧源码，并在同一行写明原因 |
| NR-04 | MUST [P] | 可空注解不改变线上形状；JSON 中“缺失”与 `null` 的区别由契约基线决定 |

### 2.3 线程与 Openness 调用

各发布键 PLC worker 的所属线程和单元以 `src/Adapters/Policy/PlcLifecyclePolicy.cs` 为准（线程模型仍待决定，见 [开发进度](../plan/progress.md)）。手册对应章节是 [领域 C.1](engineering-handbook.html#pc-isolation)。

| ID | 级别 | 规则 |
|---|---|---|
| TH-01 | MUST | 不改变 Openness 调用的顺序、参数和线程归属，除非任务明确要求；改动时在报告中列出前后调用序列（`Compare-NativeCallOrder.py`、织入清单比较） |
| TH-02 | MUST | 原生调用前核对所属线程和单元；不符时抛异常，并保证没有发起原生调用（`RejectedBeforeNative`） |
| TH-03 | MUST NOT | 适配器用 `Task.Run`、`ConfigureAwait`、`Dispatcher` 或 `SynchronizationContext` 把原生调用移到其他线程；宿主用 `ThreadPolicy` 选择策略，适配器执行策略并拒绝重入 |
| TH-04 | MUST | 原生调用开始后的超时和取消一律视为 outcome `Unknown`：会话失效，不重放、不自动重启；取消只在进入原生代码前和批量项之间检查；进度回调在所属线程上同步发出 |
| TH-05 | MUST | 接触 Openness 对象的代码留在受织入的适配器程序集（`TiaMcp.Adapter.<发布键>`）；工具分派不用 `Delegate.CreateDelegate` 或表达式编译 |
| TH-06 | MUST | 原生句柄不跨契约或进程边界；对象用规范转义的路径寻址 |
| TH-07 | MUST | 每个 TIA 进程只由一个 MCP 会话持有进程租约（[ADR 0011](decisions/0011-tia-process-lease-on-all-releases.md)）；共用同一会话的参与方（AI 客户端与工作台）写入前先检查会话状态 |
| TH-08 | MUST | WPF 只在 UI 线程上更新绑定对象；UI 线程不阻塞等待桥接或 worker 调用 |

### 2.4 命名

命名按手册 [附录 L1](engineering-handbook.html#lcs) 和 FDG；以下是项目规则和取舍。

| ID | 级别 | 规则 |
|---|---|---|
| N-03 | MUST | 两字母缩写全大写（`IO`、`UI`、`IP`、`DB`、`OB`、`FB`、`FC`），三字母以上按单词写（`Xml`、`Json`、`Plc`、`Hmi`、`Tia`、`Mcp`、`Udt`、`Scl`）。[P] 直接映射 Siemens 类型或成员的标识符沿用厂商拼写（`IoSystem`、`GlobalDB`、`InstanceDB`）；已冻结的线上名称（`ErrorCode.IoFailed`、`ErrorCode.Cancelled`）不改（CS-05） |
| N-07 | MUST | 新代码的命名空间为 `TiaMcp.<组件>[.<功能>]`，与程序集根命名空间加文件夹一致；不新增 `TiaMcpServer.*`、`TiaMcpConfigurator`，不在 `src/Shared` 新增 `TiaOpenness.Shared`。[P] `src/Studio` 保留产品命名空间 `TiaOpenness.*`（CS-30）。不新增 `Helper`、`Node`、`Message` 一类泛名类型 |
| N-08 | MUST | 私有和内部实例字段 `_camelCase`，可变静态字段 `s_camelCase`，`[ThreadStatic]` 字段 `t_camelCase`，常量 PascalCase。[P] 保存不可变对象（`Regex`、冻结集合、序列化选项、只读表）的 `static readonly` 字段可以用 PascalCase（CS-03） |
| N-10 | MUST | 返回 `Task`、`ValueTask` 等可等待类型的方法以 `Async` 结尾。[P] MCP 工具入口方法由 `Name` 分派、不被 C# 代码按名调用，SHOULD 遵守但不强制 |
| N-12 | MUST [P] | MCP 工具入口方法显式写 `[McpServerTool(Name = "...")]`；方法的参数名、类型和默认值就是线上参数，只随契约基线改变 |
| N-14 | SHOULD [P] | 文件以主要类型命名，`partial` 文件命名为 `Type.Part.cs`，一个文件一个顶级类型。例外：同一契约的线上 DTO 族集中在 `*Contracts.cs`；只服务主类型的小型 `private` 或 `file` 类型；XAML 代码隐藏（CS-21） |

### 2.5 格式、注释与 MCP 文本

格式以根目录 `.editorconfig` 为准，由构建和 `dotnet format` 执行。以下是 Microsoft 来源没有或项目另作取舍的格式规则：

| ID | 级别 | 规则 |
|---|---|---|
| S-01 | MUST [P] | C# 源码为无 BOM 的 UTF-8、仓库内 LF、4 空格缩进、无行尾空白、文件以换行结尾；生成器和快照逐字节比较，Windows 与 Ubuntu 必须得到相同字节 |
| S-07 | MUST | 新文件用 file-scoped 命名空间；既有文件只在整体重写时转换（CS-10） |
| S-08 | SHOULD | `var` 只在类型从右侧显而易见时使用；既有代码不转换（CS-17） |
| S-12 | SHOULD [P] | 新代码每行不超过 120 字符，字符串数据、`[Description(...)]` 文本和 URL 除外；既有代码不为行长重新排版（CS-19） |
| S-15 | SHOULD NOT [P] | 用 `#region`；用类型拆分组织代码，折叠区域会掩盖类型过大 |

注释按手册 [4.4](engineering-handbook.html#quality-comments)，另有以下项目规则：

- 注释写代码说不出的原因：不变量、厂商 API 的怪癖和版本差异、线程要求、某个检查为什么存在。原生行为证据写明 TIA 或 PLCSIM 版本和日期，并链接 [真机台账](../reference/real-machine-ledger.md)。
- 不写产品版本号、维护者归属、`Phase N` 或 `sub-batch` 叙事、迁移墓碑、注释掉的代码和过时的工具数；修复经过改写为当前必须保持的不变量。
- 新注释用英文；改写既有注释时保留原注释的语言。注释独占一行；吞异常标注是例外，必须写在 catch 行或块首（E-06）。
- 生成脚本字符串中的 `TODO`、注释和中文属于响应数据，不按注释清理。
- `check-ratchet -Kind comments` 按注释内容和重复次数守护注释基线，移动文件不增加额度；计数口径与基线收缩命令见 [验证分层](../manuals/validation.md)“注释与 MCP 中文门禁”。

MCP 可见文本（[ADR 待办 B02](decisions/README.md)）：

- 新增的 MCP 可见文本（描述、参数说明、消息、异常文本）一律英文；中文只作为数据出现，例如 TIA 原文、工程对象名和示例名称。源码中的中文保持 UTF-8 原文，不改写为 `\uXXXX`（U-08）。
- MCP 响应不做本地化；本地化只用于工作台的 `Loc` 和 CLI `doctor` 输出。
- `check-ratchet -Kind mcp-text` 约束中文字面量只减不增。豁免按字面量及其出现次数逐条登记理由，不是对文件的豁免；新增的中文数据单独审查并记录理由。只减不增的门禁不授权改动已有的响应字节，契约与响应基线仍须通过。

### 2.6 复杂度门槛

- 新代码和改动的代码遵守手册 [4.1](engineering-handbook.html#quality-size) 的触发线：方法约 40–60 行，文件约 400–600 行且只含一个类型，圈复杂度约 10–15，嵌套 3 层，参数约 5 个。超过时拆分，或在审查说明中写一行理由（例如厂商要求的固定调用序列）。
- 按职责拆类型，不靠新增 `partial` 变小（T-10）。同一判断、转换或业务规则（约 5 行以上）第二次出现时合并为共享组件；各版本官方 API 的差异不算重复（手册 Q-03）。
- REVIEW-1 在合并档加入只报告、不阻塞的复杂度报告（`complexity-report.md`），尚未合并。

### 2.7 错误处理

MCP 客户端看到的错误语义（错误码、`details`、`OUTCOME_UNKNOWN`）见 [设计规范](design.md) 第 4.6 节和手册 [领域 A.5](engineering-handbook.html#mcp-errors)；本节规定 C# 代码内部怎样产生和传递错误。

| 层 | 位置 | 错误表达 |
|---|---|---|
| L1 组件内部 | 领域服务、适配器、Logic、WorkerChannel | 抛异常；可预期的输入拒绝可以用结果类型（如 V4 `InputContract.Validate`） |
| L2 工具边界 | `*ToolContract.Run`/`RunAsync`、`FoundationV4Tool`、[`HostFailurePolicy`](../../src/Shared/HostFailurePolicy.cs)、`HostBehavior` | 把异常和原生结果证据转为 V4 信封（`ok=false`、`error.code`、`meta.outcome`、`requiresSessionReset`）；异常类型到错误码的映射只在这里维护 |
| L3 进程通道 | WorkerChannel 协议 2、工作台桥接进程 | 错误作为数据（`error.data = {outcome, evidence}`）；通道故障使会话失效，不重放 |

| ID | 级别 | 规则 |
|---|---|---|
| E-02 | MUST [P] | 按语义选异常类型，只在 `HostFailurePolicy`、`HostBehavior` 中映射到 V4：参数错误用 `ArgumentException` 族（分派前为 `INVALID_ARGUMENT`）；对象状态不允许用 `InvalidOperationException`；分派前的安全拒绝用 `AdapterPreconditionException`；取消用 `OperationCanceledException`；IO 用 `IOException` 族；协议或数据格式用 `InvalidDataException`、`JsonException`。新增异常类型时同时登记映射并加测试 |
| E-03 | MUST | V4 参数错误填写 `details.parameter`（取自 `ParamName`），线上的 `Message` 文本不变：进入线上文本的路径用不追加参数名的项目辅助方法设置参数名。.NET 设置 `ParamName` 后会改写 `Message`，两种运行时的格式还不同 |
| E-05 | MUST | 自定义异常只在调用方或映射处需要区分时引入：`sealed`、以 `Exception` 结尾、基类反映语义、附加数据用强类型属性；携带必需数据的异常可以只提供带这些数据的构造函数。新代码不引入用于控制流的私有异常，改用结果类型 |
| E-06 | MUST [P] | 只捕获能处理的具体异常；`catch (Exception)` 只在边界（工具契约、worker 分派器、进程入口、UI 命令、清理与收尾）使用，能加 `when` 就加。每个吞掉异常的 catch 在 catch 行或块首标注 `/* swallow(<类别>): <原因> */`，类别取自 cleanup、teardown、logging-failure、probe-optional、enumerate-optional、native-fallback、parse-fallback、env-probe、ui、fail-open-guard、privacy；需要记录时调用 `SwallowedExceptions.Note`。`check-ratchet -Kind swallowed` 守护（基线为 0） |
| E-09 | MUST | 异常消息是以句号结尾的英文完整句子，不拼接调用方的机密；`SwallowedExceptions.Note` 不读取 `ex.Message`；新代码不把 `ex.ToString()` 写入线上字段（`meta.error` 只有首行“类型: 消息”是兼容契约） |
| E-10 | MUST [P] | 原生调用已分派后的未类型化失败一律归为 `Unknown`（会话需复位），不报告为干净失败，不重试、不重放；分派前的拒绝明确报告 `RejectedBeforeNative` |
| E-11 | SHOULD | 不用 `Exception.Data` 字典作分类契约；新代码用强类型属性或标记接口 |
| E-12 | SHOULD | 频繁失败的路径（反射探测可选成员、解析回退）用 Try 模式，不靠异常 |
| E-14 | MUST [P] | 状态不可恢复时使会话失效并要求显式重连；`Environment.Exit`、`FailFast` 只在进程入口和文档化的故障注入夹具中使用 |
| E-15 | MUST [P] | 冻结的旧错误族在 4.0 移除前不互相转换，不借重构改变已有工具的错误语义；新工具只走 V4 工具契约 |

### 2.8 依赖

- 新增依赖先写明用途、许可证、维护状况和更轻的替代；运行时依赖由维护者批准（手册 [第 14 节](engineering-handbook.html#deps)）。随包组件的许可证见 [`docs/licenses`](../licenses) 和 [NOTICE.md](../../NOTICE.md)。
- 共享包版本在 `src/Shared/DependencyVersions.props`（.NET 包版本与 `build-tools/bundled-dotnet.json` 的运行时一致）；`global.json` 固定 SDK。中央包管理（`Directory.Packages.props`）在 `refactor/CPM` 分支，尚未合并。
- 分析器只用 .NET SDK 自带的代码样式规则、NetAnalyzers 和 `dotnet format`；不引入新的分析器包（StyleCop、VS Threading Analyzers 等）。
- B-06（MUST）：契约程序集不引用 Siemens 或 JSON 库；`TiaMcp.Logic` 不引用 Siemens 或 MCP SDK；Siemens 引用只出现在适配器（含工作台的 `src/Studio/Openness`）和 PlcWorker 中（契约工程中的 MSBuild 目标 `RejectNativeAndJsonDependencies`）。
- Siemens PublicAPI 是本机构建输入，不入库、不分发：默认查找 `sdk/` 下的八版 `TIA_V*_PublicAPI`，也可以用 `TiaPublicApiRoot` 指定其父目录；单个工程显式给出的 `SiemensEngineeringDirectory` 优先（[`TiaPublicApi.props`](../../src/Shared/TiaPublicApi.props)）。
- B-08（MUST）：第三方源码保留原样式和许可证，不受本规范约束；`.editorconfig` 对 `third_party/`、`reference/`、`sdk/` 关闭全部诊断。上游地址、版本和本地修改记在组件目录的 `UPSTREAM.json`。

### 2.9 分析器、棘轮与工程规则

- 分析器设置在 [`src/Shared/CodeAnalysis.props`](../../src/Shared/CodeAnalysis.props)，由根 `Directory.Build.props` 导入，net48 和 netstandard2.0 目标同样运行 NetAnalyzers。规则严重性只在根 `.editorconfig` 中维护，不在工程中散布 `NoWarn`。
- 新代码零新增警告（手册 [Q-15](engineering-handbook.html#quality-analysis)）：`check-analyzer-warnings` 与审查过的基线比较，基线只能删除已消失的条目（`-UpdateBaseline -Complete`）。
- 确需抑制时用 `[SuppressMessage(..., Justification = "...")]` 或 `.editorconfig` 的路径段，并写明偏差编号；严重性只升不降。
- 与项目棘轮的约定：CA1031 关闭（与 swallowed 棘轮重复）；CA1303 关闭（与“MCP 响应不做本地化”冲突）；IDE0090 和 IDE0300 系列保持 silent，不批量自动修复（会改变 envelopes 清单统计的令牌模式）。
- 格式化和重命名单独提交，不与信封改写混在同一差异中；提交后复跑四种 `check-ratchet`（swallowed、comments、mcp-text、envelopes）和读取源码的文本检查（`Test-*Sources.py`）。重命名前先列出测试和脚本中按字符串名反射的成员（`GetField("...")` 等）；织入基线用正式工具重录。

| ID | 级别 | 规则 |
|---|---|---|
| B-01 | MUST | 新工程为 SDK 风格，显式写 `TargetFramework(s)`，`LangVersion latest`，`Nullable enable`；`ImplicitUsings` 只在仅面向 net10 的工程中启用 |
| B-02 | MUST NOT | 新增受跟踪的 PowerShell、batch、cmd 文件；构建、发布和检查自动化用 C# 写在 `build-tools/release`，用 `dotnet run --project build-tools/release -- <命令>` 调用 |
| B-03 | MUST NOT | 手改生成文件和 manifest 哈希（第 5.5 节） |
| B-04 | MUST NOT | 删除、跳过、过滤、放宽或重设检查；棘轮基线只减不增（第 5.8 节） |
| B-05 | MUST | 移动或重命名源文件时同步更新 `<Compile Include>` 链接、测试工程和文档路径，并按 [AGENTS.md](../../AGENTS.md)“验证”运行带必需文件清单的两项检查 |
| B-07 | MUST | 共享构建设置用显式导入的 props（`TiaPublicApi.props`、`TiaFeatures.props`、`CodeAnalysis.props` 等）；`src/Studio`、`src/PlcWorker` 有自己的 `Directory.Build.props`，不继承根目录的同名文件 |

## 3 仓库结构

根目录按维护者 2026-11-03 批准的整理方案组织，分批实施；尚未迁完的位置在表中注明。

| 路径 | 内容 |
|---|---|
| `src/` | 产品源码：八个发布键共用的 FoundationHost 宿主、PlcWorker、按发布键编译的适配器及其契约、WorkerChannel、OnlineChannels、纯逻辑库 `Logic`、共享源码 `Shared`、工作台 `Studio`、Updater 和随包工具。`src/Engine` 只剩不再编译的旧源码，由 Q2 删除（取代 P8-04l2），不在其中新增代码 |
| `tests/` | 测试工程，按 Shared、FoundationHost、Adapters、WorkerChannel、Studio、Generation、Release、Tools、Updater 等分组，目录与工程同名；`tests/test-suites.json` 是套件注册表 |
| `build-tools/` | 全部 C# 构建、检查与发布自动化：`release`（唯一入口）、`common`、`native-call-weaver`；剩余的 `Package-*.py` 由 P8-10 改为 C# |
| `scripts/` | 只放随包交付的 Python 和手动诊断脚本；开发用的 Python 检查和生成器（`scripts/checks`、`scripts/generate`）正由 P8-10 移植到 `build-tools/release` |
| `reference/` | 生成器输入和随包参考数据：`tool-examples`（调用、语言文件、结果解释、调用顺序）、`tool-catalog`、固定来源的 Siemens 官方示例与授权记录、`version-feature-matrix.json`、`v21-ecosystem.json`；根目录的 `schemas/` 以后移到这里 |
| `manifest/` | 生成的版本、工具、构建与交付记录，契约与响应基线 `contracts/v4`，发布记录 `publication-v*.json`；只由生成器和发布命令写入 |
| `templates/` | 随包的编程与工程模板 |
| `third_party/` | 固定版本的第三方组件，各自带许可证和 `UPSTREAM.json` |
| `plugin/` | Claude Code 插件技能，引导 AI 按版本使用 `GetToolUsage` 示例；插件定义在 `.claude-plugin/`，根目录的 `hooks/` 以后移到这里 |
| `docs/` | `standards/`（规范与 ADR）、`plan/`（计划与进展）、`manuals/`（操作手册）、`archive/`（冻结的历史）和用户文档 |
| `bin-build/` | 全部构建与运行输出：检查输出、测试结果、暂存、构建缓存（主检出的 `bin-build/cache/`，各 worktree 共用）和开发时的数据目录 `bin-build/data/`（安装包用 `data/`）；不入 Git |
| `sdk/` | 本机的八版 Siemens PublicAPI 副本（`TIA_V*_PublicAPI`）；不入 Git，不分发 |

根目录文件：`Version.props`（产品版本的唯一来源）、`Directory.Build.props`、`global.json`、`.editorconfig`、`TiaPortalOpenness.slnx`（全部开发工程）和 `TiaPortalOpenness.Offline.slnx`（不需要 Siemens 程序集的工程；整理方案改为一个 `.slnx`）。构建生成的八个运行目录和工作台目前仍输出到根目录 `runtime/`（只跟踪 README，布局与安装包相同），整理方案把运行输出改到 `bin-build/` 下（待核对）。

## 4 新工具开发

### 4.1 现行工具归属

- 工具声明与响应映射在 `src/Shared`；不需要 Openness 的宿主工具在 `src/Shared/Host`。FoundationHost 和 EngineHost 链接同一份声明，八个发布键共用一份逻辑（[ADR 0022](decisions/0022-engine-retirement-and-eight-release-unification.md)）。
- 原生实现在 `src/Adapters/Native`，经 `IOpennessAdapter` 的 facet 暴露，按发布键编译；版本 API 差异只放在适配器中，用 `TiaFeatures.props` 的特性符号区分（U-26）。
- 工具分类在 [`ToolMetadata`](../../src/Logic/ModelContextProtocol/ToolMetadata.cs) 或 `[ToolClassification]` 中显式登记。
- 注册与动作范围以生成目录为准：一个工具在某个发布键上至少有一个动作能在该版 API 上运行时才注册，不支持的动作明确拒绝（[ADR 0012](decisions/0012-old-release-tool-registration.md)、[ADR 0014](decisions/0014-tool-counts-follow-official-api.md)）。

### 4.2 步骤

1. 判断是否需要新工具：只为官方 Openness API 支持的能力注册；先考虑给现有工具加动作或可选参数（[设计规范](design.md) 第 6.1 节）。
2. 写工具规格：新工具和契约变化属于 L2，按 [开发流程](../manuals/workflow.md#附录-工具规格模板) 附录的模板把规格写进任务说明，审查后再实现；改变契约政策或安全边界属于 L3，另需 ADR。
3. 声明与实现：按第 4.1 节放置代码；入口方法显式写 `Name`（N-12），参数按 [设计规范](design.md) 第 6.4 节，文本用英文（第 2.5 节），错误走 V4 工具契约（第 2.7 节），写入工具提供预览并经工作台审批（[安全标准](security.md) SEC-01）。
4. 运行时资源：随包指南、模板或伴随脚本用 [`BundleLayout.cs`](../../src/Shared/BundleLayout.cs) 的资源表定位包根；包根只由 `--bundle-root`、`TIA_MCP_BUNDLE_ROOT` 或已知安装锚点决定，不新增向上探测仓库的逻辑。`reference/v21-ecosystem.json` 嵌入宿主程序集，修改后须重新构建。
5. 示例：在 `reference/tool-examples` 中维护调用、语言文件、参数来源、结果解释和调用顺序，参数须经真机核对（[设计规范](design.md) 第 6.6 节）；`GetToolUsage` 按版本返回这些数据。
6. 生成：依次运行 `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`，改名时再运行 `generate-release-migration`；各自的 `-Check` 逐字节核对。生成文件不手改（第 5.5 节）。
7. 测试：主要路径测试加第 5.6 节中相关的失败路径；构建受影响的发布键，用正式工具重新生成契约基线并逐项说明差异（第 5.5 节）。
8. 交付记录：用户可见的变化写入 `CHANGELOG.md` 的 `[Unreleased]` 段；原生行为在 [真机台账](../reference/real-machine-ledger.md) 中登记为 `NOT RUN`，由预发布的真机回归验证（[真机测试](../manuals/real-machine-testing.md)）。

## 5 测试

### 5.1 原则

- 开发机没有 TIA Portal。离线检查证明构建、协议、契约和宿主行为，不证明原生行为；原生结果以 [真机台账](../reference/real-machine-ledger.md) 为准，没有运行时写 `NOT RUN`。
- 测试不连接 TIA Portal、PLC、虚拟机或外部网络服务；可以使用自己在 127.0.0.1 上启动的合成服务（HTTP 夹具、命名管道夹具），注册套件、preflight 和传输检查不能因此跳过（[AGENTS.md](../../AGENTS.md)“范围”）。
- 测试证明主要行为和规定的失败路径；不写证明不了主要功能的防御性测试（手册 [第 12 节](engineering-handbook.html#testing)）。
- 具体命令、作业清单、超时和输出目录以 [验证分层](../manuals/validation.md) 为准；什么时候运行哪一档见 [开发流程](../manuals/workflow.md) 第 4 节。维护者 2026-11-03 批准的 SLIM-1（尚未合并）删除计时与延迟门禁、测试输出一致性检查、重复的构建配置以及 V14 SP1–V19 的响应捕获，本节的规则不依赖这些检查。

### 5.2 层次

| 层 | 内容 | 证明什么 | 不证明什么 |
|---|---|---|---|
| 注册的离线套件 | xUnit 和控制台测试工程，按 `tests/test-suites.json` 运行 | 宿主、worker 通道、审批、生成、发布工具和工作台的离线行为 | 原生 TIA 行为 |
| 契约与响应基线 | `manifest/contracts/v4` 中八个发布键的工具定义快照，以及规范化的响应（覆盖范围见验证分层） | 输入契约和返回结构没有意外变化 | 原生调用成功 |
| SDK 与 PublicAPI 形状 | 带 `publicApiRoot` 的套件编译 `sdk/` 中的 PublicAPI；生成的接口片段按各版本官方 XSD 校验 | API 形状、片段与 XSD 一致 | 原生导入成功 |
| 生成器与仓库检查 | 生成器的 `-Check`、`check-repository`、死引用、分析器警告与棘轮 | 生成文件、链接、随包路径和警告没有漂移 | 运行时行为 |
| 示例检查 | `GetToolUsage` 检索全部工具与动作，执行返回的离线示例（`manifest/tool-usage-coverage.json`） | 每个注册的工具和动作都有示例，离线示例可执行 | 示例参数在真实项目中正确 |
| 宿主故障与恢复 | FoundationHost 与假 worker：超时、异常退出、错误响应、迟到响应后的恢复 | 宿主的故障处理与恢复 | Siemens 内部的可靠性 |
| 真机验收 | 在匹配版本和许可的 TIA Portal 上读写往返、编译和显式保存 | 原生行为 | 其他版本和其他项目 |

预发布由测试者验证，我们另在虚拟机上按清单回归，结果记入真机台账（[ADR 0020](decisions/0020-prereleases-tested-by-users.md)）。

### 5.3 每个改动要证明什么

| 改动 | 必须有 |
|---|---|
| 缺陷修复 | 修改前失败、修改后通过的测试；任务说明写明这个测试和运行它的命令 |
| 新功能或新工具 | 主要路径测试，加第 5.6 节中相关的失败路径 |
| 替换代码路径或重构 | 新旧行为对等的测试 |
| MCP 契约变化 | 用正式工具重新生成的基线，差异逐项说明；破坏性变化写入发布说明和迁移表 |
| 新命令或脚本 | 至少真实运行一次 |
| Openness 调用的顺序、参数或线程改变 | 前后调用序列对照和真机验证计划（TH-01） |

夹具使用真实格式的脱敏数据；测试名说明行为；测试互相独立，不依赖执行顺序和共享状态。

### 5.4 套件注册

`tests/test-suites.json` 是注册套件的唯一来源。每个套件登记 `project`、`minimumPassed`（最少通过数）、`maximumSkipped`（最多跳过数）和 `arguments`（例如 `--filter`）；可选字段为 `platforms`（按平台覆盖下限，必须写 `reason`）、`splitFrom`、`companions`（必须同时运行的伙伴套件）和 `publicApiRoot`。

- 失败数必须为零；重复的测试身份被拒绝。
- 用 `dotnet run --project build-tools/release -- test-suites -Suite <名称>` 运行并核对注册门禁；`dotnet test` 只用于开发时的快速运行。未注册的控制台工程用 `dotnet run --project <csproj> -c Release`，`dotnet test` 不执行它们的断言。
- 新测试工程在注册表中登记后，才由 `test-suites` 和 `check-all` 按门禁运行。降低 `minimumPassed`、提高 `maximumSkipped` 或删除套件都属于放宽（第 5.8 节）。

### 5.5 基线与生成文件

功能按设计改变时，用正式写入方重新生成受影响的基线，在报告和合并审查中逐项列出差异：

| 基线 | 正式写入方 |
|---|---|
| 契约基线 | `Snapshot-ToolContracts.py capture`，再用 `compare` 审查差异 |
| 规划 golden | 设置 `TIA_MCP_UPDATE_GOLDEN=1` 运行 `generation-planning` 套件，移除开关后再正常运行一次（[golden 说明](../../tests/Generation/TiaMcp.Generation.Planning.Tests/Golden/README.md)） |
| TIA 功能检查 | `check-tia-features -Capture` |
| 嵌入目录、工具用法、工具数量块、迁移表 | `generate-catalogs`、`generate-tool-usage`、`generate-tool-counts`、`generate-release-migration`，按此顺序运行 |
| 分析器警告基线 | `check-analyzer-warnings -UpdateBaseline -Complete`，只能删除已消失的条目 |

- 不手改测试哈希、`manifest/*.json` 中的哈希和生成文件（`manifest/tools-list.json`、`src/Shared/ToolUsageData.json`、`docs/reference/tool-matrix.md` 等）。
- 重新生成基线不授权修改阈值或断言。
- 把全部生成器合成一条 `regen-all` 命令已于 2026-11-01 决定，由 RULES-1 实现，尚未提供。

### 5.6 必须覆盖的失败路径

新代码和改动的代码测试下列失败路径中与它相关的部分：

- 审批：拒绝、超时和工作台不可用都在派发前返回 `CONFIRMATION_REQUIRED`；读取不排队；默认开启审批和关闭审批两种情况都要测（[验证分层](../manuals/validation.md)“压力与故障检查”）。
- worker 与通道：超时、异常退出、错误响应、迟到响应、帧超限、未知或重复回复（`worker-channel`、`worker-robustness` 套件；故障由 `TIA_FIXTURE_FAULT` 注入）。
- 结果未知：返回 `OUTCOME_UNKNOWN`，会话锁定，不自动重放（E-10、TH-04）。
- 输入边界：路径越界、保留名、重解析点、数量与大小上限、格式错误（[安全标准](security.md) SEC-08、SEC-09）。
- 敏感值：带凭据的参数不进入审批摘要、计划哈希、审计、调用日志和响应（SEC-06）。
- 篡改：被改动的记录、哈希或来源文件被对应的 `-Check` 或校验拒绝。
- 参数错误：`INVALID_ARGUMENT` 指出出错的参数（E-03）。

### 5.7 时间敏感测试

- 测试不等待真实时间超过 1 秒：注入时钟或计时接缝（`TimeProvider` 一类），计时参数在 CPU 满载时仍成立（手册 TST-05）。
- 仍需真实计时的测试类登记在 `tests/Shared/TimeSensitiveTests.cs` 并写明原因；这份清单只用于调度隔离，不是门禁。
- 偶发失败时保留证据并定位原因，不靠重跑把结果变绿。认为某项测试不稳定时保持原样，在报告中附证据，由维护者决定。

### 5.8 放宽检查须维护者批准

- 删除、跳过、过滤、放宽或重设任何检查、测试、断言、阈值、超时和门禁（含套件的 `minimumPassed`、`maximumSkipped`），都须维护者事先批准（[ADR 待办 B36](decisions/README.md)、[AGENTS.md](../../AGENTS.md)“范围”）。批准记在任务说明或 ADR 中，报告逐项列出。
- 功能按设计减少用例时，在报告中列出删除的用例和原因，由维护者决定。
- 重新生成基线（第 5.5 节）是允许的，但不授权改阈值或断言。
- REVIEW-1（尚未合并）加入阻塞的 `check-loosening`：对照批准清单检查删除的测试、新增的跳过、降低的下限和放大的阈值。

### 5.9 测试代码的放宽与禁止事项

| ID | 级别 | 规则 |
|---|---|---|
| C-01 | MAY | 测试方法名用 `方法_场景_预期` 形式的下划线；测试方法不加 `Async` 后缀 |
| C-02 | MAY | 测试替身和夹具使用公共字段、可变静态计数器和全局命名空间的控制台入口 |
| C-03 | SHOULD | 失败用断言框架或套件自带的检查辅助（`CheckSuite`）报告；需要抛异常时用 `InvalidOperationException` 或专用异常，不用 `Exception` |
| C-04 | MUST [P] | 测试只能收紧（第 5.8 节）；需要 Siemens 程序集的测试遵守第 2.3 节 |

测试中不做的事：

- 不运行 live 测试分支，不解禁原生交叉引用。
- 不把开发缓存、`-Quick` 报告或合并门的结果当作发布证据（[开发流程](../manuals/workflow.md) 第 4 节）。
- 不在夹具中放真实项目、凭据、机器地址或未脱敏的现场日志。
- 不按进程名或模糊匹配结束进程；只结束自己在本 worktree 中启动、并核对过可执行文件路径的进程。

## 6 Git 与提交

规则基线是手册 [18.4](engineering-handbook.html#review-git)；任务分支怎样进入 master 见 [开发流程](../manuals/workflow.md) 第 2、5 节。

- master 是唯一长期分支。每个任务从当前 master 建独立 worktree 和 `refactor/<任务 ID>` 分支；审查并通过主机检查后变基到 master，快进推送，合并后删除分支。
- 有依赖的分支按依赖顺序逐个合并，每个都重新变基并通过合并门；变基后重新生成生成文件，不手工合并它们。
- 用明确的 `git add <路径>`，不用 `git add -A` 或 `git add .`。
- 提交信息用英文祈使句；不加 AI 署名（`Co-Authored-By`、“Generated with”）。
- 运行二进制、本地 SDK、用户项目、设计交接草稿、密钥和本机绝对路径不进 Git。
- 一次提交只做一件事；格式化和重命名单独提交（第 2.9 节）。
- 不经维护者明确同意不做破坏性 Git 操作：`reset --hard`、`push --force`、`clean -fdx`、改写已推送的历史、跳过钩子。
- 文档改动核对链接、路径、工具适用版本和引用，并运行 `python scripts/checks/Check-Repository.py --no-binaries`。

## 7 AI 分工

角色表见 [开发流程](../manuals/workflow.md) 第 1 节，Codex 的执行规则见 [AGENTS.md](../../AGENTS.md)，通用规则见手册 [第 19 节](engineering-handbook.html#ai)。

| 角色 | 负责 | 不做 |
|---|---|---|
| 维护者 | 范围、优先级和设计取舍；批准 ADR、检查或阈值的改变、发布 | 不需要亲自运行检查 |
| Claude | 研究、诊断、方案比较；任务说明和 ADR 草稿；审查；主机检查；提交、变基、合并；交接与计划 | 不写新功能代码；变基时的一行修正和文档笔误除外 |
| Codex | 在独立 worktree 的沙箱中按任务说明写全部新代码和测试，构建，交付前运行一次 `check-all -Affected -Quick` 并列出沙箱中无法运行的作业 | 不提交、不推送、不合并；不连接 TIA、PLC、虚拟机或外部网络 |

- 研究、分析和方案比较由 Claude 完成，不交给 Codex。
- 任务说明按手册模板 [T.4](engineering-handbook.html#tpl-task)，写明根因、涉及的文件和函数、预期设计、允许修改的路径、修改前失败而修改后通过的测试、验证命令和时间预算；需要维护者批准的断言或阈值改变在说明中事先写明。
- 每次 Codex 交付先由独立的 AI 审查一次（维护者 2026-11-02），再由 Claude 审查 diff 并在主机上运行受影响的检查。
- 主机检查失败时先定位原因，再带具体问题退回同一 Codex 会话；不反复重跑完整门禁，Claude 不自行打补丁。
- 同时运行的 Codex 任务数由维护者决定；有依赖的任务串行。

## 8 偏差登记

### 8.1 登记方式

- 现有代码与第 2 节规则的差异以 `CS-nn` 编号登记在本节，写明规则、现状和处理。数量来自 2026-10-11（master `3a227657`）的源码扫描，处理前重新计数。
- 新代码需要不遵守 MUST 规则时，在审查说明中引用规则 ID 和理由；长期例外在本节新增条目，不在代码中散布抑制。
- 已由 P8-62（`a8be1010`、`2197c672`）处理并从本节移除：CS-08、CS-38、CS-43、CS-51、CS-55、CS-57（2026-11-03 抽查确认），CS-04、CS-23（待核对）。

### 8.2 待处理

| ID | 规则 | 现状 | 处理 |
|---|---|---|---|
| CS-01 | 实例字段 `_camelCase`（N-08） | 无前缀的私有和内部实例字段 827 个，193 个文件 | 4.0 后按工程用 IDE 重命名；先列出被反射引用的字段 |
| CS-02 | 可变静态字段 `s_`（N-08） | 32 个，18 个文件 | 4.0 后，与 CS-31 一起 |
| CS-06 | `Async` 后缀（N-10） | 带 `async` 而无后缀的方法 111 个 | 4.0 后先改非工具方法；工具入口按 N-10 豁免 |
| CS-07 | 取消参数名为 `cancellationToken` 并放最后 | 名称不符 115 处，位置不符 7 处 | 4.0 后 |
| CS-09 | `using` 在命名空间外、`System` 在前并排序 | 顺序不符 190 个文件，位于命名空间内 3 个 | 4.0 后格式化提交 |
| CS-11 | 控制流关键字后加空格 | `if(` 等 1,354 行 | 4.0 后格式化提交 |
| CS-12 | 单语句 `if` 不写在同一行 | 9,151 行 | 工具能可靠修复时一次处理，否则转为有意偏差，只约束新代码 |
| CS-13 | 一行一条语句 | 5,547 行 | 4.0 后格式化提交 |
| CS-14 | 一条语句一个声明 | 125 处 | 4.0 后格式化提交 |
| CS-15 | Allman 大括号 | 行尾左大括号 67 行 | 4.0 后格式化提交 |
| CS-16 | 不写多余的 `this.` | 同名赋值 162 处 | 随 CS-01 消失 |
| CS-20 | 不用 `#region`（S-15） | 38 处，引擎 33 处 | 4.0 后；引擎部分随 `src/Engine` 删除 |
| CS-22 | 不出现连续空行 | 209 处 | 4.0 后格式化提交 |
| CS-24 | 总是写访问修饰符 | 约 22 处，多在 native-call-weaver | 与下一次必要的织入器改动合并（改动会改变织入器指纹，须重新织入全部产物） |
| CS-30 | 命名空间根（N-07） | `TiaMcpServer.*`、`TiaOpenness.Shared`、`TiaMcpConfigurator` 与 `TiaMcp.*` 并存，约 770 个文件 | 4.0 后一次性机械迁移并重录织入和反射基线；`src/Studio` 的 `TiaOpenness.*` 是有意偏差 |
| CS-31 | 可变静态状态（U-24） | 多会话宿主中 32 个：进程级钩子、环境诊断状态、桥接目录 | 4.0 后改为一次性初始化或注入 |
| CS-32 | 默认 `internal`、`sealed`（T-03） | 公共类型 1,251 个，未密封的公共类 238 个，多为旧响应 POCO | 4.0 后随旧族移除处理；契约 DTO 密封；工作台按 WPF 需要保留 |
| CS-33 | 不暴露公共实例字段 | 对外可见类型上 244 个，12 个文件 | 4.0 后逐类型附响应基线证据 |
| CS-37 | 不用公共嵌套类型 | 64 个，25 个文件 | 4.0 后（类型全名进入织入清单） |
| CS-39 | 设置 `ParamName`（E-03） | 只传消息的 `ArgumentException` 1,691 处 | 4.0 前按工具族分批转换，每批附基线差异；P8-62 后的剩余量待核对 |
| CS-40 | 不用 `Exception.Data` 分类（E-11） | 76 处，部分决定 V4 outcome | 4.0 后先补分类行为测试，再改为强类型属性 |
| CS-42 | 不同步等待异步（U-19） | 80 处，37 个文件 | 4.0 前审查工作台与 HTTP 宿主路径中的子集，其余 4.0 后；P8-62 后的状态待核对 |
| CS-45 | 字符串比较与解析（U-06、U-07） | 文化敏感的比较约 80 处，无区域参数的解析 119 处 | 4.0 前机械修改；P8-62 后的剩余量待核对（CA1310、CA1305 仍为 suggestion） |
| CS-46 | 缓存 `JsonSerializerOptions` | 每次调用新建 66 处，含 `PlcToolContract.Map` | 4.0 后，须证明缓存后的选项完全相同 |
| CS-47 | 启用可空（NR-01） | 工作台 Bridge、Client、Contracts、Openness 和 Updater 关闭可空；另有 20 个 `#nullable disable` 文件 | 4.0 后（工作台阶段） |
| CS-48 | Dispose 模式 | 未密封且无 `Dispose(bool)` 的可释放类型 2 个（`Portal`、`BlocksView`） | 4.0 后改为 `sealed` |
| CS-50 | 不重复定义类型（T-11） | 工作台 DTO 和枚举两份，`SessionPreconditionException` 两份 | 4.0 后合并为一份 |
| CS-52 | 安全的 XML 处理（U-14） | 引擎 CLI 中 `new XmlDocument` 6 处 | 随 `src/Engine` 删除，或按 CA3075 处理 |
| CS-53 | 只用 System.Text.Json（U-10） | `S7WebApiChannel` 仍用 Newtonsoft.Json | 4.0 后 |
| CS-56 | 不用控制流异常（E-05） | 私有或内部控制流异常 13 个，`InputFailure` 有 6 份 | 4.0 后合并为一个共享类型或改为结果类型 |
| CS-58 | 复用 `HttpClient` | 产品代码新建 4 处，更新检查每次调用都新建 | 4.0 后 |
| CS-71 | 测试不抛 `Exception`（C-03） | `throw new Exception` 229 处，`ApplicationException` 4 处 | 4.0 后改用 `CheckSuite` 或 `InvalidOperationException`，不借机放宽断言 |

### 8.3 有意偏差（已接受）

| ID | 规则 | 接受的做法 |
|---|---|---|
| CS-03 | N-08 | 常量性的 `static readonly` 字段用 PascalCase（650 个） |
| CS-05 | N-03 | 映射 Siemens API 的厂商拼写和冻结的线上名称保留；只有工作台内部的 `DialogButtons.OKCancel` 在 4.0 后改为 `OkCancel` |
| CS-10 | S-07 | 既有文件保留块级命名空间 |
| CS-17 | S-08 | 既有代码的 `var` 不转换；IDE0007、IDE0008 只到 suggestion |
| CS-18 | 注释独占一行 | 吞异常标注必须在 catch 行；其余既有行尾注释保留，新代码避免 |
| CS-19 | S-12 | 既有代码不按 120 字符重排 |
| CS-21 | N-14 | `*Contracts.cs` DTO 族和按主题拆分的分部文件保留；误导性的个案（异常定义在 `Enums.cs`、一个文件含五个以上无关类型）4.0 后整理 |
| CS-25 | 语言版本（第 2.2 节） | net48 工程用 `LangVersion latest`，只用纯编译器特性 |
| CS-30 | N-07 | `src/Studio` 保留 `TiaOpenness.*` 产品命名空间 |
| CS-34 | M-07 | `IOpennessAdapter` 各 facet 的方法带默认值，与工具参数一一对应；新增的 A 级接口成员不带默认值 |
| CS-35 | M-05 | facet 与工具的多个布尔参数保留 |
| CS-36 | U-01 | 线上 DTO 和生成模型使用数组、`List<T>` 和 setter；非 DTO 的公共 `List` 返回值随适配器重构收紧 |
| CS-41 | E-06 | `catch (Exception)` 由 swallow 标注和棘轮逐个治理，不启用 CA1031 |
| CS-44 | U-23 | 旧族 meta 时间戳的 `DateTime.Now` 保留到旧族移除；计时改 `Stopwatch` 在 4.0 后 |
| CS-49 | 事件委托类型 | internal 接口以 `Action` 作事件类型 |
| CS-54 | U-26 | PlcWorker 的构建角色符号 `OPCUA_NO_EXTENSION`、`OPCUA_NO_UNITS`，已登记在 `tia-feature-expectations.json` |
| CS-70 | C-01 | 测试方法名含下划线 |
| CS-72 | C-02 | 测试替身的公共字段和可变静态字段 |
| CS-73 | C-02 | 全局命名空间的测试入口 |
| CS-74 | C-01 | 测试的 Task 方法不加 `Async` 后缀 |
